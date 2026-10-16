using System;
using System.Numerics;
using System.Threading.Tasks;

namespace Aardvark.ImageCodecs.JpegXl
{
    /// <summary>Colour transforms of JPEG XL: XYB to linear RGB, YCbCr, transfer functions, primaries.</summary>
    internal static class JxlColour
    {
        /// <summary>In place: XYB planes to linear RGB with sRGB primaries (scaled so that 1.0 is the intensity target).</summary>
        public static void XybToLinear(float[] x, float[] y, float[] b, int n, JxlOpsinParams p, float intensityTarget, bool parallel)
        {
            float scale = 255.0f / intensityTarget;
            var m = new float[9];
            for (int i = 0; i < 9; i++) m[i] = p.InvMat[i] * scale;
            float b0 = p.OpsinBias[0], b1 = p.OpsinBias[1], b2 = p.OpsinBias[2];
            float c0 = MathF.Cbrt(b0), c1 = MathF.Cbrt(b1), c2 = MathF.Cbrt(b2);
            void Range(int s, int e)
            {
                for (int i = s; i < e; i++)
                {
                    float xx = x[i], yy = y[i], bb = b[i];
                    float lg = yy + xx - c0, mg = yy - xx - c1, sg = bb - c2;
                    float l = lg * lg * lg + b0, mm = mg * mg * mg + b1, ss = sg * sg * sg + b2;
                    x[i] = m[0] * l + m[1] * mm + m[2] * ss;
                    y[i] = m[3] * l + m[4] * mm + m[5] * ss;
                    b[i] = m[6] * l + m[7] * mm + m[8] * ss;
                }
            }
            Chunked(n, parallel, Range);
        }

        public static void Chunked(int n, bool parallel, Action<int, int> range)
        {
            const int Chunk = 1 << 16;
            if (!parallel || n < 2 * Chunk) { range(0, n); return; }
            int chunks = (n + Chunk - 1) / Chunk;
            Parallel.For(0, chunks, k => range(k * Chunk, Math.Min(n, (k + 1) * Chunk)));
        }

        /// <summary>JPEG style YCbCr (full range, BT.601) to RGB; inputs are in [0, 1] with chroma centred on 0.</summary>
        public static void YCbCrToRgb(float[] yp, float[] cb, float[] cr, int n, out float[] r, out float[] g, out float[] b)
        {
            r = new float[n]; g = new float[n]; b = new float[n];
            for (int i = 0; i < n; i++)
            {
                float Y = yp[i] + 128 / 255f, Cb = cb[i], Cr = cr[i];
                r[i] = Y + 1.402f * Cr;
                g[i] = Y - 0.344136286f * Cb - 0.714136286f * Cr;
                b[i] = Y + 1.772f * Cb;
            }
        }

        /// <summary>In place YCbCr to RGB on the three planes (Y in the middle one, as in VarDCT channel order X, Y, B = Cb, Y, Cr).</summary>
        public static void YCbCrToRgbInPlace(float[] cb, float[] yp, float[] cr, int n)
        {
            for (int i = 0; i < n; i++)
            {
                float Y = yp[i] + 128 / 255f, Cb = cb[i], Cr = cr[i];
                cb[i] = Y + 1.402f * Cr;
                yp[i] = Y - 0.344136286f * Cb - 0.714136286f * Cr;
                cr[i] = Y + 1.772f * Cb;
            }
        }

        /// <summary>JPEG "fancy" chroma upsampling (triangle filter, 3/4 1/4) to the full plane size.</summary>
        public static float[] UpsampleJpegChroma(float[] src, int w, int h, int hs, int vs, int ow, int oh)
        {
            float[] cur = src;
            int cw = w, chh = h;
            if (hs > 0)
            {
                int nw = Math.Min(ow, cw * 2);
                var o = new float[nw * chh];
                for (int y = 0; y < chh; y++)
                    for (int x = 0; x < nw; x++)
                    {
                        int sx = x >> 1;
                        float c = cur[y * cw + sx];
                        int nx = (x & 1) == 0 ? Math.Max(sx - 1, 0) : Math.Min(sx + 1, cw - 1);
                        o[y * nw + x] = 0.75f * c + 0.25f * cur[y * cw + nx];
                    }
                cur = o; cw = nw;
            }
            if (vs > 0)
            {
                int nh = Math.Min(oh, chh * 2);
                var o = new float[cw * nh];
                for (int y = 0; y < nh; y++)
                {
                    int sy = y >> 1;
                    int ny = (y & 1) == 0 ? Math.Max(sy - 1, 0) : Math.Min(sy + 1, chh - 1);
                    for (int x = 0; x < cw; x++) o[y * cw + x] = 0.75f * cur[sy * cw + x] + 0.25f * cur[ny * cw + x];
                }
                cur = o; chh = nh;
            }
            if (cw == ow && chh == oh) return cur;
            var r = new float[ow * oh];
            for (int y = 0; y < oh; y++)
                for (int x = 0; x < ow; x++) r[y * ow + x] = cur[Math.Min(y, chh - 1) * cw + Math.Min(x, cw - 1)];
            return r;
        }

        // ------------------------------------------------------------------ transfer functions (linear -> encoded)

        public static float SrgbFromLinear(float v)
        {
            if (v <= 0.0031308f) return v * 12.92f;
            return 1.055f * MathF.Pow(v, 1 / 2.4f) - 0.055f;
        }

        public static float Bt709FromLinear(float v)
        {
            if (v < 0.018f) return 4.5f * v;
            return 1.099f * MathF.Pow(v, 0.45f) - 0.099f;
        }

        public static float PqFromLinear(float v)
        {
            // v: 1.0 = 10000 nits
            const float m1 = 2610f / 16384f, m2 = 2523f / 4096f * 128f, c1 = 3424f / 4096f, c2 = 2413f / 4096f * 32f, c3 = 2392f / 4096f * 32f;
            if (v <= 0) return 0;
            float yp = MathF.Pow(v, m1);
            return MathF.Pow((c1 + c2 * yp) / (1 + c3 * yp), m2);
        }

        public static float HlgFromLinear(float v)
        {
            const float a = 0.17883277f, b = 0.28466892f, c = 0.55991073f;
            if (v <= 0) return 0;
            if (v <= 1 / 12f) return MathF.Sqrt(3 * v);
            return a * MathF.Log(12 * v - b) + c;
        }

        /// <summary>Applies the encoding transfer function to linear values in place (sign preserving for out of range values).</summary>
        public static void FromLinear(float[] p, int n, JxlColourEncoding ce, float intensityTarget, bool parallel)
        {
            switch (ce.Transfer)
            {
                case JxlTransfer.Linear: return;
                case JxlTransfer.Srgb:
                    Chunked(n, parallel, (s, e) => { for (int i = s; i < e; i++) { float v = p[i]; p[i] = v < 0 ? -SrgbFromLinear(-v) : SrgbFromLinear(v); } });
                    return;
                case JxlTransfer.Bt709:
                    Chunked(n, parallel, (s, e) => { for (int i = s; i < e; i++) { float v = p[i]; p[i] = v < 0 ? -Bt709FromLinear(-v) : Bt709FromLinear(v); } });
                    return;
                case JxlTransfer.Pq:
                    {
                        float sc = intensityTarget / 10000f;
                        Chunked(n, parallel, (s, e) => { for (int i = s; i < e; i++) { float v = p[i] * sc; p[i] = v < 0 ? -PqFromLinear(-v) : PqFromLinear(v); } });
                        return;
                    }
                case JxlTransfer.Hlg:
                    Chunked(n, parallel, (s, e) => { for (int i = s; i < e; i++) { float v = p[i]; p[i] = v < 0 ? -HlgFromLinear(-v) : HlgFromLinear(v); } });
                    return;
                case JxlTransfer.Dci:
                    Chunked(n, parallel, (s, e) => { for (int i = s; i < e; i++) { float v = p[i]; p[i] = v < 0 ? -MathF.Pow(-v, 1 / 2.6f) : MathF.Pow(v, 1 / 2.6f); } });
                    return;
                case JxlTransfer.Gamma:
                    {
                        float g = (float)ce.Gamma;
                        Chunked(n, parallel, (s, e) => { for (int i = s; i < e; i++) { float v = p[i]; p[i] = v < 0 ? -MathF.Pow(-v, g) : MathF.Pow(v, g); } });
                        return;
                    }
                default:
                    // unknown: leave linear
                    return;
            }
        }

        // ------------------------------------------------------------------ primaries

        static (double x, double y) WhiteXy(JxlColourEncoding ce) => ce.WhitePoint switch
        {
            JxlWhitePoint.D65 => (0.3127, 0.3290),
            JxlWhitePoint.E => (1 / 3.0, 1 / 3.0),
            JxlWhitePoint.Dci => (0.314, 0.351),
            _ => (ce.White.x, ce.White.y),
        };

        static ((double, double) r, (double, double) g, (double, double) b) PrimariesXy(JxlColourEncoding ce) => ce.Primaries switch
        {
            JxlPrimaries.Srgb => ((0.639998686, 0.330010138), (0.300003784, 0.600003357), (0.150002046, 0.059997204)),
            JxlPrimaries.Bt2100 => ((0.708, 0.292), (0.170, 0.797), (0.131, 0.046)),
            JxlPrimaries.P3 => ((0.680, 0.320), (0.265, 0.690), (0.150, 0.060)),
            _ => ((ce.Red.x, ce.Red.y), (ce.Green.x, ce.Green.y), (ce.Blue.x, ce.Blue.y)),
        };

        /// <summary>Whether linear sRGB (D65) needs a matrix to get to the encoding's primaries / white point.</summary>
        public static bool NeedsPrimaryConversion(JxlColourEncoding ce)
            => !ce.IsGray && (ce.Primaries != JxlPrimaries.Srgb || ce.WhitePoint != JxlWhitePoint.D65);

        static double[] RgbToXyz((double, double) r, (double, double) g, (double, double) b, (double x, double y) w)
        {
            double[] P =
            {
                r.Item1, g.Item1, b.Item1,
                r.Item2, g.Item2, b.Item2,
                1 - r.Item1 - r.Item2, 1 - g.Item1 - g.Item2, 1 - b.Item1 - b.Item2,
            };
            var inv = Inverse3(P);
            double wx = w.x / w.y, wy = 1, wz = (1 - w.x - w.y) / w.y;
            double sr = inv[0] * wx + inv[1] * wy + inv[2] * wz;
            double sg = inv[3] * wx + inv[4] * wy + inv[5] * wz;
            double sb = inv[6] * wx + inv[7] * wy + inv[8] * wz;
            return new[] { P[0] * sr, P[1] * sg, P[2] * sb, P[3] * sr, P[4] * sg, P[5] * sb, P[6] * sr, P[7] * sg, P[8] * sb };
        }

        static double[] Bradford((double x, double y) from, (double x, double y) to)
        {
            double[] M = { 0.8951, 0.2664, -0.1614, -0.7502, 1.7135, 0.0367, 0.0389, -0.0685, 1.0296 };
            double[] Lms(double x, double y) => Mul(M, new[] { x / y, 1, (1 - x - y) / y });
            var a = Lms(from.x, from.y); var b = Lms(to.x, to.y);
            double[] D = { b[0] / a[0], 0, 0, 0, b[1] / a[1], 0, 0, 0, b[2] / a[2] };
            return Mul3(Inverse3(M), Mul3(D, M));
        }

        /// <summary>3x3 matrix from linear sRGB (D65) to linear RGB in the encoding's primaries and white point.</summary>
        public static float[] SrgbToTargetMatrix(JxlColourEncoding ce)
        {
            var srgb = RgbToXyz((0.639998686, 0.330010138), (0.300003784, 0.600003357), (0.150002046, 0.059997204), (0.3127, 0.3290));
            var p = PrimariesXy(ce);
            var w = WhiteXy(ce);
            var target = RgbToXyz(p.r, p.g, p.b, w);
            var adapt = Bradford((0.3127, 0.3290), w);
            var m = Mul3(Inverse3(target), Mul3(adapt, srgb));
            var f = new float[9];
            for (int i = 0; i < 9; i++) f[i] = (float)m[i];
            return f;
        }

        public static void ApplyMatrix(float[] r, float[] g, float[] b, int n, float[] m)
        {
            for (int i = 0; i < n; i++)
            {
                float x = r[i], y = g[i], z = b[i];
                r[i] = m[0] * x + m[1] * y + m[2] * z;
                g[i] = m[3] * x + m[4] * y + m[5] * z;
                b[i] = m[6] * x + m[7] * y + m[8] * z;
            }
        }

        static double[] Mul(double[] m, double[] v) => new[] { m[0] * v[0] + m[1] * v[1] + m[2] * v[2], m[3] * v[0] + m[4] * v[1] + m[5] * v[2], m[6] * v[0] + m[7] * v[1] + m[8] * v[2] };

        static double[] Mul3(double[] a, double[] b)
        {
            var r = new double[9];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    r[i * 3 + j] = a[i * 3] * b[j] + a[i * 3 + 1] * b[3 + j] + a[i * 3 + 2] * b[6 + j];
            return r;
        }

        static double[] Inverse3(double[] m)
        {
            double a = m[0], b = m[1], c = m[2], d = m[3], e = m[4], f = m[5], g = m[6], h = m[7], i = m[8];
            double det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
            if (Math.Abs(det) < 1e-12) throw new ImageFormatException("JPEG XL: degenerate primaries");
            double s = 1 / det;
            return new[]
            {
                (e * i - f * h) * s, (c * h - b * i) * s, (b * f - c * e) * s,
                (f * g - d * i) * s, (a * i - c * g) * s, (c * d - a * f) * s,
                (d * h - e * g) * s, (b * g - a * h) * s, (a * e - b * d) * s,
            };
        }
    }
}
