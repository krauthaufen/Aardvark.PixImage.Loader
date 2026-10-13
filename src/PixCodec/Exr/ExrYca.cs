using System;

namespace PixCodec
{
    /// <summary>
    /// Luminance/chroma images: Y at full resolution, RY = (R - Y) / Y and BY = (B - Y) / Y at half resolution in both
    /// directions. Decoding follows the reference library's RGBA interface step by step (same filters, same half float
    /// rounding between the steps) so that the result is bit identical: chroma is interpolated horizontally, then
    /// vertically, with a 27 tap half band filter, converted to RGB with luma weights derived from the file's
    /// chromaticities, and finally over-saturated pixels (ringing of the filter) are desaturated towards their
    /// neighbours. All intermediate values are half floats, kept here as bit patterns.
    /// </summary>
    internal static class ExrYca
    {
        const int N2 = 13;

        // interpolation: taps at distance 13, 11, ..., 1 (the kernel is symmetric)
        static readonly float[] Up = { 0.002128f, -0.007540f, 0.019597f, -0.043159f, 0.087929f, -0.186077f, 0.627123f };
        // decimation: taps at distance 13, 11, ..., 1 and the centre tap
        static readonly float[] Down = { 0.001064f, -0.003771f, 0.009801f, -0.021586f, 0.043978f, -0.093067f, 0.313659f };
        const float DownCentre = 0.499847f;

        /// <summary>Weights of R, G, B in luminance: the Y row of the RGB to XYZ matrix of the primaries, normalised.</summary>
        public static (float R, float G, float B) LumaWeights(ExrChromaticities? chroma)
        {
            var c = chroma ?? new ExrChromaticities(0.64f, 0.33f, 0.30f, 0.60f, 0.15f, 0.06f, 0.3127f, 0.3290f);
            const float Y = 1;
            float X = c.WhiteX * Y / c.WhiteY;
            float Z = (1 - c.WhiteX - c.WhiteY) * Y / c.WhiteY;
            float d = c.RedX * (c.BlueY - c.GreenY) + c.BlueX * (c.GreenY - c.RedY) + c.GreenX * (c.RedY - c.BlueY);
            float sr = (X * (c.BlueY - c.GreenY) - c.GreenX * (Y * (c.BlueY - 1) + c.BlueY * (X + Z)) + c.BlueX * (Y * (c.GreenY - 1) + c.GreenY * (X + Z))) / d;
            float sg = (X * (c.RedY - c.BlueY) + c.RedX * (Y * (c.BlueY - 1) + c.BlueY * (X + Z)) - c.BlueX * (Y * (c.RedY - 1) + c.RedY * (X + Z))) / d;
            float sb = (X * (c.GreenY - c.RedY) - c.RedX * (Y * (c.GreenY - 1) + c.GreenY * (X + Z)) + c.GreenX * (Y * (c.RedY - 1) + c.RedY * (X + Z))) / d;
            float wr = sr * c.RedY, wg = sg * c.GreenY, wb = sb * c.BlueY;
            float sum = wr + wg + wb;
            return (wr / sum, wg / sum, wb / sum);
        }

        static float F(ushort h) => BitConverter.UInt32BitsToSingle(ExrHalf.ToFloatBits[h]);
        static ushort H(float f) => ExrHalf.FromFloatIeee(f);

        /// <summary>
        /// Converts full resolution planes of half bits (chroma valid at even x and even y only; null = absent) into
        /// interleaved R,G,B(,A) half bits. Width and height must be even.
        /// </summary>
        public static ushort[] ToRgb(ushort[] luma, ushort[]? ry, ushort[]? by, ushort[]? alpha, int w, int h, (float R, float G, float B) yw)
        {
            int nc = alpha != null ? 4 : 3;
            var result = new ushort[w * h * nc];

            // chroma of the even rows, interpolated horizontally
            int rows2 = h / 2;
            var cr = new ushort[rows2 * w];
            var cb = new ushort[rows2 * w];
            for (int r2 = 0; r2 < rows2; r2++)
            {
                HorizontalUp(ry, r2 * 2 * w, w, cr.AsSpan(r2 * w, w));
                HorizontalUp(by, r2 * 2 * w, w, cb.AsSpan(r2 * w, w));
            }

            // RGB rows y - 1, y, y + 1 are needed for the saturation fix; rows outside the image repeat edge rows
            var rgb = new ushort[3][];
            for (int i = 0; i < 3; i++) rgb[i] = new ushort[w * 3];
            var tr = new ushort[w];
            var tb = new ushort[w];
            int Clamp(int r) => r < 0 ? 0 : r > h - 1 ? h - 2 : r;
            void MakeRow(int r, ushort[] dst)
            {
                if ((r & 1) == 0)
                {
                    int e = Clamp(r) / 2 * w;
                    cr.AsSpan(e, w).CopyTo(tr);
                    cb.AsSpan(e, w).CopyTo(tb);
                }
                else
                {
                    for (int x = 0; x < w; x++)
                    {
                        float sr = 0, sb = 0;
                        for (int k = 0; k < 14; k++)
                        {
                            int e = Clamp(r - 13 + 2 * k) / 2 * w + x;
                            float c = Up[k < 7 ? k : 13 - k];
                            if (k == 0) { sr = F(cr[e]) * c; sb = F(cb[e]) * c; }
                            else { sr += F(cr[e]) * c; sb += F(cb[e]) * c; }
                        }
                        tr[x] = H(sr); tb[x] = H(sb);
                    }
                }
                int lr = Clamp(r) * w;
                for (int x = 0; x < w; x++)
                {
                    ushort yh = luma[lr + x];
                    if (F(tr[x]) == 0 && F(tb[x]) == 0)
                    {
                        dst[x * 3] = dst[x * 3 + 1] = dst[x * 3 + 2] = yh;
                    }
                    else
                    {
                        float Y = F(yh);
                        float r_ = (F(tr[x]) + 1) * Y;
                        float b_ = (F(tb[x]) + 1) * Y;
                        float g_ = (Y - r_ * yw.R - b_ * yw.B) / yw.G;
                        dst[x * 3] = H(r_); dst[x * 3 + 1] = H(g_); dst[x * 3 + 2] = H(b_);
                    }
                }
            }

            MakeRow(-1, rgb[0]);
            MakeRow(0, rgb[1]);
            for (int y = 0; y < h; y++)
            {
                MakeRow(y + 1, rgb[2]);
                FixSaturation(rgb[0], rgb[1], rgb[2], w, yw, result.AsSpan(y * w * nc), nc);
                if (alpha != null)
                    for (int x = 0; x < w; x++) result[(y * w + x) * 4 + 3] = alpha[y * w + x];
                (rgb[0], rgb[1], rgb[2]) = (rgb[1], rgb[2], rgb[0]);
            }
            return result;
        }

        static void HorizontalUp(ushort[]? plane, int row, int w, Span<ushort> dst)
        {
            if (plane == null) { dst.Clear(); return; }
            for (int x = 0; x < w; x++)
            {
                if ((x & 1) == 0) { dst[x] = plane[row + x]; continue; }
                float s = 0;
                for (int k = 0; k < 14; k++)
                {
                    int i = x - 13 + 2 * k;
                    i = i < 0 ? 0 : i >= w ? w - 2 : i;
                    float v = F(plane[row + i]) * Up[k < 7 ? k : 13 - k];
                    s = k == 0 ? v : s + v;
                }
                dst[x] = H(s);
            }
        }

        static float Saturation(ushort[] row, int x)
        {
            float r = F(row[x * 3]), g = F(row[x * 3 + 1]), b = F(row[x * 3 + 2]);
            float max = MathF.Max(r, MathF.Max(g, b)), min = MathF.Min(r, MathF.Min(g, b));
            return max > 0 ? 1 - min / max : 0;
        }

        /// <summary>Limits the saturation of each pixel of the middle row to what its diagonal neighbours suggest.</summary>
        static void FixSaturation(ushort[] above, ushort[] row, ushort[] below, int w, (float R, float G, float B) yw, Span<ushort> dst, int nc)
        {
            float a2 = Saturation(above, 0), a1 = a2, b2 = Saturation(below, 0), b1 = b2;
            for (int x = 0; x < w; x++)
            {
                float a0 = a1; a1 = a2;
                float b0 = b1; b1 = b2;
                if (x < w - 1) { a2 = Saturation(above, x + 1); b2 = Saturation(below, x + 1); }
                float sMean = MathF.Min(1.0f, 0.25f * (a0 + a2 + b0 + b2));
                ushort hr = row[x * 3], hg = row[x * 3 + 1], hb = row[x * 3 + 2];
                float s = Saturation(row, x);
                if (s > sMean)
                {
                    float sMax = MathF.Min(1.0f, 1 - (1 - sMean) * 0.25f);
                    if (s > sMax)
                    {
                        float f = sMax / s;
                        float r = F(hr), g = F(hg), b = F(hb);
                        float max = MathF.Max(r, MathF.Max(g, b));
                        ushort or = H(MathF.Max(max - (max - r) * f, 0.0f));
                        ushort og = H(MathF.Max(max - (max - g) * f, 0.0f));
                        ushort ob = H(MathF.Max(max - (max - b) * f, 0.0f));
                        float yin = r * yw.R + g * yw.G + b * yw.B;
                        float yout = F(or) * yw.R + F(og) * yw.G + F(ob) * yw.B;
                        if (yout > 0)
                        {
                            float q = yin / yout;
                            or = H(F(or) * q); og = H(F(og) * q); ob = H(F(ob) * q);
                        }
                        hr = or; hg = og; hb = ob;
                    }
                }
                dst[x * nc] = hr; dst[x * nc + 1] = hg; dst[x * nc + 2] = hb;
            }
        }

        // ------------------------------------------------------------------ encode

        /// <summary>
        /// Converts interleaved R,G,B half bits (stride channels per pixel) into a full resolution Y plane and half
        /// resolution RY / BY planes (width / 2 x height / 2). Negative and non finite colours are treated as 0, as
        /// luminance/chroma cannot represent them. Width and height must be even.
        /// </summary>
        public static void FromRgb(ushort[] rgb, int stride, int w, int h, (float R, float G, float B) yw, out ushort[] luma, out ushort[] ry, out ushort[] by)
        {
            luma = new ushort[w * h];
            var fr = new ushort[w * h];
            var fb = new ushort[w * h];
            for (int i = 0; i < w * h; i++)
            {
                float r = F(rgb[i * stride]), g = F(rgb[i * stride + 1]), b = F(rgb[i * stride + 2]);
                if (!float.IsFinite(r) || r < 0) r = 0;
                if (!float.IsFinite(g) || g < 0) g = 0;
                if (!float.IsFinite(b) || b < 0) b = 0;
                if (r == g && g == b)
                {
                    // grey: no chroma, and no rounding of the luminance
                    luma[i] = H(g);
                    continue;
                }
                ushort yh = H(r * yw.R + g * yw.G + b * yw.B);
                luma[i] = yh;
                float Y = F(yh);
                fr[i] = MathF.Abs(r - Y) < 65504f * Y ? H((r - Y) / Y) : (ushort)0;
                fb[i] = MathF.Abs(b - Y) < 65504f * Y ? H((b - Y) / Y) : (ushort)0;
            }

            // low pass and drop every second column, then every second row
            int w2 = w / 2, h2 = h / 2;
            var hr = new ushort[w2 * h];
            var hb = new ushort[w2 * h];
            for (int y = 0; y < h; y++)
                for (int x2 = 0; x2 < w2; x2++)
                {
                    hr[y * w2 + x2] = H(Filter(fr, y * w + 2 * x2, 1, 2 * x2, w));
                    hb[y * w2 + x2] = H(Filter(fb, y * w + 2 * x2, 1, 2 * x2, w));
                }
            ry = new ushort[w2 * h2];
            by = new ushort[w2 * h2];
            for (int y2 = 0; y2 < h2; y2++)
                for (int x2 = 0; x2 < w2; x2++)
                {
                    ry[y2 * w2 + x2] = H(Filter(hr, 2 * y2 * w2 + x2, w2, 2 * y2, h));
                    by[y2 * w2 + x2] = H(Filter(hb, 2 * y2 * w2 + x2, w2, 2 * y2, h));
                }
        }

        /// <summary>Half band low pass around position pos of a line of n samples (index = centre + (i - pos) * step), edges repeated.</summary>
        static float Filter(ushort[] data, int centre, int step, int pos, int n)
        {
            float s = 0;
            for (int k = 0; k < 7; k++)
            {
                int d = 13 - 2 * k;
                int lo = Math.Max(pos - d, 0), hi = Math.Min(pos + d, n - 1);
                s += (F(data[centre + (lo - pos) * step]) + F(data[centre + (hi - pos) * step])) * Down[k];
            }
            return s + F(data[centre]) * DownCentre;
        }
    }
}
