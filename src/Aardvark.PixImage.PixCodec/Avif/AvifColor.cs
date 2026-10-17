using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Threading.Tasks;

namespace Aardvark.ImageCodecs.Heif
{
    /// <summary>Decoded planes of an AV1 image (or a grid of them) with their colour description.</summary>
    internal sealed class YuvImage
    {
        public ushort[][] Planes = new ushort[3][];
        public int[] Stride = new int[3];
        public int Width, Height, BitDepth, SubX, SubY;
        public bool Mono;
        public int ColorPrimaries = 2, TransferCharacteristics = 2, MatrixCoefficients = 2;
        public bool FullRange;

        public int PlaneWidth(int p) => p == 0 ? Width : (Width + SubX) >> SubX;
        public int PlaneHeight(int p) => p == 0 ? Height : (Height + SubY) >> SubY;
        public int NumPlanes => Mono ? 1 : 3;

        public static YuvImage Create(int w, int h, int bd, int subX, int subY, bool mono)
        {
            var y = new YuvImage { Width = w, Height = h, BitDepth = bd, SubX = subX, SubY = subY, Mono = mono };
            for (int p = 0; p < y.NumPlanes; p++)
            {
                int pw = y.PlaneWidth(p), ph = y.PlaneHeight(p);
                y.Planes[p] = new ushort[(long)pw * ph];
                y.Stride[p] = pw;
            }
            return y;
        }
    }

    /// <summary>
    /// YUV to RGB conversion as ISO/IEC 23091-2 (H.273) describes it: Kr / Kb from matrix_coefficients (or the
    /// colour primaries for the chromaticity derived matrices), full or limited range, identity (GBR) and YCgCo, and
    /// bilinear chroma upsampling (chroma samples centred between the luma samples).
    /// </summary>
    internal static class AvifColor
    {
        /// <summary>Kr and Kb of a matrix; false for matrices that are not of the Kr / Kb form (identity, YCgCo, ...).</summary>
        public static bool Coefficients(int mc, int cp, out double kr, out double kb)
        {
            switch (mc)
            {
                case 1: kr = 0.2126; kb = 0.0722; return true;
                case 4: kr = 0.30; kb = 0.11; return true;
                case 5: case 6: kr = 0.299; kb = 0.114; return true;
                case 7: kr = 0.212; kb = 0.087; return true;
                case 9: case 10: kr = 0.2627; kb = 0.0593; return true;
                case 12: case 13:
                    if (PrimariesCoefficients(cp, out kr, out kb)) return true;
                    kr = 0.299; kb = 0.114; return true;
                case 2: case 3: default:
                    // unspecified: BT.601, as decoders do in practice
                    kr = 0.299; kb = 0.114; return true;
            }
        }

        /// <summary>Kr / Kb derived from the colour primaries (H.273 equations for matrix_coefficients 12).</summary>
        static bool PrimariesCoefficients(int cp, out double kr, out double kb)
        {
            kr = kb = 0;
            if (!Primaries(cp, out var p)) return false;
            double xr = p[0], yr = p[1], xg = p[2], yg = p[3], xb = p[4], yb = p[5], xw = p[6], yw = p[7];
            double zr = 1 - xr - yr, zg = 1 - xg - yg, zb = 1 - xb - yb, zw = 1 - xw - yw;
            double denom = yw * (xr * (yg * zb - yb * zg) + xg * (yb * zr - yr * zb) + xb * (yr * zg - yg * zr));
            if (denom == 0) return false;
            kr = yr * (xw * (yg * zb - yb * zg) + yw * (xb * zg - xg * zb) + zw * (xg * yb - xb * yg)) / denom;
            kb = yb * (xw * (yr * zg - yg * zr) + yw * (xg * zr - xr * zg) + zw * (xr * yg - xg * yr)) / denom;
            return true;
        }

        /// <summary>Chromaticities (xr, yr, xg, yg, xb, yb, xw, yw) of H.273 colour primaries.</summary>
        public static bool Primaries(int cp, out double[] p)
        {
            p = cp switch
            {
                1 => new[] { 0.64, 0.33, 0.30, 0.60, 0.15, 0.06, 0.3127, 0.3290 },
                4 => new[] { 0.67, 0.33, 0.21, 0.71, 0.14, 0.08, 0.310, 0.316 },
                5 => new[] { 0.64, 0.33, 0.29, 0.60, 0.15, 0.06, 0.3127, 0.3290 },
                6 or 7 => new[] { 0.630, 0.340, 0.310, 0.595, 0.155, 0.070, 0.3127, 0.3290 },
                8 => new[] { 0.681, 0.319, 0.243, 0.692, 0.145, 0.049, 0.310, 0.316 },
                9 => new[] { 0.708, 0.292, 0.170, 0.797, 0.131, 0.046, 0.3127, 0.3290 },
                10 => new[] { 1.0, 0.0, 0.0, 1.0, 0.0, 0.0, 1.0 / 3, 1.0 / 3 },
                11 => new[] { 0.680, 0.320, 0.265, 0.690, 0.150, 0.060, 0.314, 0.351 },
                12 => new[] { 0.680, 0.320, 0.265, 0.690, 0.150, 0.060, 0.3127, 0.3290 },
                22 => new[] { 0.630, 0.340, 0.295, 0.605, 0.155, 0.077, 0.3127, 0.3290 },
                _ => null!,
            };
            return p != null;
        }

        /// <summary>
        /// Converts to interleaved RGB(A) or gray(+alpha) samples of the source bit depth (UInt8 for 8 bit, else UInt16
        /// holding 0 .. 2^bitDepth - 1). <paramref name="alpha"/> is a full range plane of the same size (or null).
        /// </summary>
        public static RawImage ToRaw(YuvImage y, YuvImage? alpha, bool premultiplied, bool multithreaded)
        {
            int w = y.Width, h = y.Height;
            int bd = y.BitDepth;
            bool gray = y.Mono;
            bool hasAlpha = alpha != null;
            var color = gray ? (hasAlpha ? ColorModel.GrayAlpha : ColorModel.Gray) : (hasAlpha ? ColorModel.Rgba : ColorModel.Rgb);
            int nc = RawImage.ChannelCount(color);
            var type = bd <= 8 ? SampleType.UInt8 : SampleType.UInt16;
            var img = RawImage.Create(w, h, color, type);
            int maxV = (1 << bd) - 1;
            float maxF = maxV;
            float yOff, yScale, cOff, cScale;
            if (y.FullRange) { yOff = 0; yScale = 1f / maxV; cOff = 1 << (bd - 1); cScale = 1f / maxV; }
            else
            {
                yOff = 16 << (bd - 8);
                yScale = 1f / (219 << (bd - 8));
                cOff = 128 << (bd - 8);
                cScale = 1f / (224 << (bd - 8));
            }
            int mc = y.MatrixCoefficients;
            Coefficients(mc, y.ColorPrimaries, out double kr, out double kb);
            double kg = 1 - kr - kb;
            float crR = (float)(2 * (1 - kr)), cbB = (float)(2 * (1 - kb));
            float cbG = (float)(2 * kb * (1 - kb) / kg), crG = (float)(2 * kr * (1 - kr) / kg);
            // alpha plane depth may differ from the colour depth
            int aMax = alpha != null ? (1 << alpha.BitDepth) - 1 : 0;
            byte[]? b8 = type == SampleType.UInt8 ? (byte[])img.Data : null;
            ushort[]? b16 = type == SampleType.UInt16 ? (ushort[])img.Data : null;
            void Row(int row, float[] uRow, float[] vRow, int[] scratch, int[] rgb)
            {
                if (!gray) UpsampleRow(y, row, uRow, vRow, scratch);
                if (alpha == null && !gray && mc != 0 && mc != 8)
                {
                    FastRow(y, row, uRow, vRow, rgb, b8, b16, yOff, yScale, cOff, cScale, crR, cbB, cbG, crG, maxF);
                    return;
                }
                var Y = y.Planes[0];
                int yo = row * y.Stride[0];
                int o = row * w * nc;
                for (int x = 0; x < w; x++)
                {
                    float yy = (Y[yo + x] - yOff) * yScale;
                    float r, g, b;
                    if (gray) { r = g = b = yy; }
                    else
                    {
                        float cb = (uRow[x] - cOff) * cScale, cr = (vRow[x] - cOff) * cScale;
                        if (mc == 0)
                        {
                            // identity: G, B, R in the Y, U, V planes
                            float yu = (uRow[x] - (y.FullRange ? 0 : yOff)) * yScale, yv = (vRow[x] - (y.FullRange ? 0 : yOff)) * yScale;
                            g = yy; b = yu; r = yv;
                        }
                        else if (mc == 8)
                        {
                            float t = yy - cb;
                            g = yy + cb;
                            b = t - cr;
                            r = t + cr;
                        }
                        else
                        {
                            r = yy + crR * cr;
                            b = yy + cbB * cb;
                            g = yy - cbG * cb - crG * cr;
                        }
                    }
                    int ai = 0;
                    float af = 1;
                    if (alpha != null)
                    {
                        int av = alpha.Planes[0][row * alpha.Stride[0] + x];
                        af = (float)av / aMax;
                        ai = aMax == maxV ? av : (int)(af * maxV + 0.5f);
                        if (premultiplied && af > 0)
                        {
                            r = Math.Min(1f, r / af); g = Math.Min(1f, g / af); b = Math.Min(1f, b / af);
                        }
                    }
                    int R = Q(r, maxF), G = Q(g, maxF), B = Q(b, maxF);
                    if (b8 != null)
                    {
                        if (gray) { b8[o] = (byte)R; if (hasAlpha) b8[o + 1] = (byte)ai; }
                        else
                        {
                            b8[o] = (byte)R; b8[o + 1] = (byte)G; b8[o + 2] = (byte)B;
                            if (hasAlpha) b8[o + 3] = (byte)ai;
                        }
                    }
                    else
                    {
                        if (gray) { b16![o] = (ushort)R; if (hasAlpha) b16[o + 1] = (ushort)ai; }
                        else
                        {
                            b16![o] = (ushort)R; b16[o + 1] = (ushort)G; b16[o + 2] = (ushort)B;
                            if (hasAlpha) b16[o + 3] = (ushort)ai;
                        }
                    }
                    o += nc;
                }
            }
            const int Chunk = 16;
            void Rows(int c)
            {
                var uRow = new float[w];
                var vRow = new float[w];
                var scratch = new int[2 * w + 2];
                var rgb = new int[3 * w + 12];
                for (int r = c * Chunk; r < Math.Min(h, c * Chunk + Chunk); r++) Row(r, uRow, vRow, scratch, rgb);
            }
            int chunks = (h + Chunk - 1) / Chunk;
            if (multithreaded && h > 64) Parallel.For(0, chunks, Rows);
            else for (int c = 0; c < chunks; c++) Rows(c);
            return img;
        }

        /// <summary>One row of the usual case: Y'CbCr with a matrix, no alpha (four pixels at a time, then interleaved).</summary>
        static void FastRow(YuvImage y, int row, float[] uRow, float[] vRow, int[] rgb, byte[]? b8, ushort[]? b16,
                            float yOff, float yScale, float cOff, float cScale, float crR, float cbB, float cbG, float crG, float maxF)
        {
            int w = y.Width;
            var Y = y.Planes[0].AsSpan(row * y.Stride[0], w);
            int x = 0;
            if (Vector128.IsHardwareAccelerated)
            {
                var vyOff = Vector128.Create(yOff); var vyScale = Vector128.Create(yScale);
                var vcOff = Vector128.Create(cOff); var vcScale = Vector128.Create(cScale);
                var vMax = Vector128.Create(maxF); var half = Vector128.Create(0.5f);
                var one = Vector128.Create(1f);
                for (; x + 4 <= w; x += 4)
                {
                    var yv = Vector128.ConvertToSingle(Vector128.WidenLower(Vector128.Create(Y[x], Y[x + 1], Y[x + 2], Y[x + 3], 0, 0, 0, 0)).AsInt32());
                    var yy = (yv - vyOff) * vyScale;
                    var cb = (Vector128.Create<float>(uRow.AsSpan(x, 4)) - vcOff) * vcScale;
                    var cr = (Vector128.Create<float>(vRow.AsSpan(x, 4)) - vcOff) * vcScale;
                    var r = yy + Vector128.Create(crR) * cr;
                    var g = yy - Vector128.Create(cbG) * cb - Vector128.Create(crG) * cr;
                    var b = yy + Vector128.Create(cbB) * cb;
                    Vector128.ConvertToInt32(Vector128.Min(Vector128.Max(r, Vector128<float>.Zero), one) * vMax + half).CopyTo(rgb, x);
                    Vector128.ConvertToInt32(Vector128.Min(Vector128.Max(g, Vector128<float>.Zero), one) * vMax + half).CopyTo(rgb, w + x);
                    Vector128.ConvertToInt32(Vector128.Min(Vector128.Max(b, Vector128<float>.Zero), one) * vMax + half).CopyTo(rgb, 2 * w + x);
                }
            }
            for (; x < w; x++)
            {
                float yy = (Y[x] - yOff) * yScale;
                float cb = (uRow[x] - cOff) * cScale, cr = (vRow[x] - cOff) * cScale;
                rgb[x] = Q(yy + crR * cr, maxF);
                rgb[w + x] = Q(yy - cbG * cb - crG * cr, maxF);
                rgb[2 * w + x] = Q(yy + cbB * cb, maxF);
            }
            int o = row * w * 3;
            if (b8 != null)
            {
                var d = b8.AsSpan(o, w * 3);
                for (int i = 0; i < w; i++) { d[3 * i] = (byte)rgb[i]; d[3 * i + 1] = (byte)rgb[w + i]; d[3 * i + 2] = (byte)rgb[2 * w + i]; }
            }
            else
            {
                var d = b16!.AsSpan(o, w * 3);
                for (int i = 0; i < w; i++) { d[3 * i] = (ushort)rgb[i]; d[3 * i + 1] = (ushort)rgb[w + i]; d[3 * i + 2] = (ushort)rgb[2 * w + i]; }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Q(float v, float max)
        {
            return (int)(Math.Clamp(v, 0f, 1f) * max + 0.5f);
        }

        /// <summary>
        /// The chroma samples of one luma row, bilinearly upsampled (3:1 weights) for subsampled planes and rounded to the
        /// sample grid (as libavif does, so the RGB stays within one step of avifdec).
        /// </summary>
        /// <summary>
        /// The chroma of one luma row in integers: vertically 3 * near + far (or 4 * the row), horizontally
        /// 3 * near + far again, and (sum + 8) &gt;&gt; 4 - the bilinear weights 9 / 3 / 3 / 1 rounded once.
        /// </summary>
        static void UpsampleRow(YuvImage y, int row, float[] uRow, float[] vRow, int[]? scratch)
        {
            int w = y.Width;
            int cw = y.PlaneWidth(1), ch = y.PlaneHeight(1);
            var U = y.Planes[1];
            var V = y.Planes[2];
            int cs = y.Stride[1];
            int r0 = row, r1 = row, a0 = 4, a1 = 0;
            if (y.SubY != 0)
            {
                r0 = row >> 1;
                r1 = Math.Clamp((row & 1) == 0 ? r0 - 1 : r0 + 1, 0, ch - 1);
                a0 = 3; a1 = 1;
            }
            if (y.SubX == 0)
            {
                for (int x = 0; x < w; x++)
                {
                    uRow[x] = (a0 * U[r0 * cs + x] + a1 * U[r1 * cs + x] + 2) >> 2;
                    vRow[x] = (a0 * V[r0 * cs + x] + a1 * V[r1 * cs + x] + 2) >> 2;
                }
                return;
            }
            var tmp = scratch != null && scratch.Length >= 2 * cw ? scratch : new int[2 * cw];
            for (int i = 0; i < cw; i++)
            {
                tmp[i] = a0 * U[r0 * cs + i] + a1 * U[r1 * cs + i];
                tmp[cw + i] = a0 * V[r0 * cs + i] + a1 * V[r1 * cs + i];
            }
            // two luma columns per chroma sample: its left and right neighbour as the far one
            for (int i = 0; i < cw; i++)
            {
                int l = Math.Max(i - 1, 0), r = Math.Min(i + 1, cw - 1);
                int cu = 3 * tmp[i] + 8, cv = 3 * tmp[cw + i] + 8;
                int x = 2 * i;
                uRow[x] = (cu + tmp[l]) >> 4;
                vRow[x] = (cv + tmp[cw + l]) >> 4;
                if (x + 1 < w)
                {
                    uRow[x + 1] = (cu + tmp[r]) >> 4;
                    vRow[x + 1] = (cv + tmp[cw + r]) >> 4;
                }
            }
            // 4:2:0: the last column of an odd width takes its chroma sample alone (as avifdec does)
            if ((w & 1) != 0 && y.SubY != 0)
            {
                uRow[w - 1] = (4 * tmp[cw - 1] + 8) >> 4;
                vRow[w - 1] = (4 * tmp[2 * cw - 1] + 8) >> 4;
            }
        }
    }
}
