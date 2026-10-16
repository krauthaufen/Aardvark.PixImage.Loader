using System;
using System.Threading.Tasks;

namespace Aardvark.ImageCodecs.JpegXl
{
    /// <summary>Encoder colour handling: input samples to linear sRGB and the forward XYB (opsin) transform.</summary>
    internal static class JxlEncColour
    {
        /// <summary>The opsin absorbance matrix: the inverse of the default inverse matrix of the header.</summary>
        static readonly double[] Opsin = Invert(new JxlOpsinParams().InvMat);
        static readonly float Bias = 0.0037930732552754493f;
        static readonly float CbrtBias = MathF.Cbrt(Bias);

        static double[] Invert(float[] m)
        {
            double a = m[0], b = m[1], c = m[2], d = m[3], e = m[4], f = m[5], g = m[6], h = m[7], i = m[8];
            double A = e * i - f * h, B = -(d * i - f * g), C = d * h - e * g;
            double det = a * A + b * B + c * C;
            return new[]
            {
                A / det, -(b * i - c * h) / det, (b * f - c * e) / det,
                B / det, (a * i - c * g) / det, -(a * f - c * d) / det,
                C / det, -(a * h - b * g) / det, (a * e - b * d) / det,
            };
        }

        static float SrgbToLinear(float v)
        {
            float a = Math.Abs(v);
            float r = a <= 0.04045f ? a / 12.92f : MathF.Pow((a + 0.055f) / 1.055f, 2.4f);
            return v < 0 ? -r : r;
        }

        static float Decode(float v, TransferFunction tf, double gamma)
        {
            switch (tf)
            {
                case TransferFunction.Linear: return v;
                case TransferFunction.Gamma when gamma > 0: return v <= 0 ? 0 : MathF.Pow(v, (float)(1 / gamma));
                case TransferFunction.Pq:
                    {
                        // SMPTE ST 2084 to linear, 1.0 = 10000 nits
                        const float m1 = 2610f / 16384, m2 = 2523f / 4096 * 128, c1 = 3424f / 4096, c2 = 2413f / 4096 * 32, c3 = 2392f / 4096 * 32;
                        float p = MathF.Pow(Math.Max(v, 0), 1 / m2);
                        return MathF.Pow(Math.Max(p - c1, 0) / (c2 - c3 * p), 1 / m1);
                    }
                case TransferFunction.Hlg:
                    {
                        const float a = 0.17883277f, b = 0.28466892f, c = 0.55991073f;
                        v = Math.Max(v, 0);
                        return v <= 0.5f ? v * v / 3 : (MathF.Exp((v - c) / a) + b) / 12;
                    }
                default: return SrgbToLinear(v);
            }
        }

        /// <summary>
        /// The HLG OOTF on scene light planes scaled by <paramref name="mul"/>: display = scene * Ys^(gamma - 1) with Ys the
        /// scene luminance (weights null for grey, where the three planes are equal).
        /// </summary>
        public static void HlgOotf(float[][] planes, float[]? weights, float gamma, float mul)
        {
            if (gamma == 1) return;
            float e = gamma - 1, inv = 1 / mul;
            var (r, g, b) = (planes[0], planes[1], planes[2]);
            for (int i = 0; i < r.Length; i++)
            {
                float ys = (weights == null ? r[i] : weights[0] * r[i] + weights[1] * g[i] + weights[2] * b[i]) * inv;
                float f = ys > 0 ? MathF.Pow(ys, e) : 0;
                r[i] *= f; g[i] *= f; b[i] *= f;
            }
        }

        /// <summary>
        /// The colour channels as linear sRGB planes of size pw x ph (the image padded by repeating the last column and
        /// row), scaled by intensityScale (255 / intensity target times the nits of 1.0).
        /// </summary>
        public static float[][] LinearPlanes(RawImage img, TransferFunction tf, double gamma, IccTransform? icc, int pw, int ph, float scale, bool parallel)
        {
            int w = img.Width, h = img.Height, nc = img.Channels;
            int ncol = img.Color is ColorModel.Gray or ColorModel.GrayAlpha ? 1 : 3;
            var planes = new[] { new float[pw * ph], new float[pw * ph], new float[pw * ph] };
            float[]? lut8 = null;
            if (img.SampleType == SampleType.UInt8 && icc == null)
            {
                lut8 = new float[256];
                for (int i = 0; i < 256; i++) lut8[i] = Decode(i / 255f, tf, gamma) * scale;
            }
            float[]? lut16 = null;
            if (img.SampleType == SampleType.UInt16 && icc == null)
            {
                lut16 = new float[65536];
                for (int i = 0; i < 65536; i++) lut16[i] = Decode(i / 65535f, tf, gamma) * scale;
            }
            bool isFloat = img.SampleType is SampleType.Float16 or SampleType.Float32;
            void Row(int y)
            {
                if (lut8 != null)
                {
                    // the common case: 8 bit samples through the table
                    var d8 = (byte[])img.Data;
                    int o = y * pw, s0 = y * w * nc;
                    var p0 = planes[0]; var p1 = planes[1]; var p2 = planes[2];
                    for (int x = 0; x < w; x++, s0 += nc)
                    {
                        float r = lut8[d8[s0]];
                        p0[o + x] = r;
                        p1[o + x] = ncol == 1 ? r : lut8[d8[s0 + 1]];
                        p2[o + x] = ncol == 1 ? r : lut8[d8[s0 + 2]];
                    }
                    for (int c = 0; c < 3; c++) for (int x = w; x < pw; x++) planes[c][o + x] = planes[c][o + w - 1];
                    return;
                }
                var row = new float[w * 3];
                for (int x = 0; x < w; x++)
                {
                    int s = (y * w + x) * nc;
                    for (int c = 0; c < ncol; c++)
                    {
                        float v;
                        switch (img.SampleType)
                        {
                            case SampleType.UInt8: v = lut8 != null ? lut8[((byte[])img.Data)[s + c]] : ((byte[])img.Data)[s + c] / 255f; break;
                            case SampleType.UInt16: v = lut16 != null ? lut16[((ushort[])img.Data)[s + c]] : ((ushort[])img.Data)[s + c] / 65535f; break;
                            case SampleType.Float16: v = (float)((Half[])img.Data)[s + c]; break;
                            default: v = ((float[])img.Data)[s + c]; break;
                        }
                        if (isFloat && icc == null) v = (tf == TransferFunction.Linear || tf == TransferFunction.Unknown ? v : Decode(v, tf, gamma)) * scale;
                        if (float.IsNaN(v)) v = 0;
                        row[x * 3 + c] = v;
                    }
                    if (ncol == 1) row[x * 3 + 1] = row[x * 3 + 2] = row[x * 3];
                }
                if (icc != null)
                {
                    var outp = new float[w * 3];
                    if (ncol == 1)
                    {
                        var g = new float[w];
                        for (int x = 0; x < w; x++) g[x] = row[x * 3];
                        var go = new float[w];
                        icc.Transform(g, go);
                        for (int x = 0; x < w; x++) outp[x * 3] = outp[x * 3 + 1] = outp[x * 3 + 2] = go[x] * scale;
                    }
                    else
                    {
                        icc.Transform(row, outp);
                        for (int x = 0; x < w * 3; x++) outp[x] *= scale;
                    }
                    row = outp;
                }
                for (int c = 0; c < 3; c++)
                {
                    var p = planes[c];
                    int o = y * pw;
                    for (int x = 0; x < w; x++) p[o + x] = row[x * 3 + c];
                    for (int x = w; x < pw; x++) p[o + x] = p[o + w - 1];
                }
            }
            if (parallel) Parallel.For(0, h, Row); else for (int y = 0; y < h; y++) Row(y);
            for (int c = 0; c < 3; c++)
                for (int y = h; y < ph; y++) Array.Copy(planes[c], (h - 1) * pw, planes[c], y * pw, pw);
            return planes;
        }

        /// <summary>Cube root (bit trick start, three Newton steps: relative error below 1e-7), negative inputs mirrored.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        static float Cbrt(float v)
        {
            float a = MathF.Abs(v);
            if (a < 1e-30f) return 0;
            float y = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(a) / 3 + 709921077);
            y = (2 * y + a / (y * y)) * (1 / 3f);
            y = (2 * y + a / (y * y)) * (1 / 3f);
            y = (2 * y + a / (y * y)) * (1 / 3f);
            return v < 0 ? -y : y;
        }

        /// <summary>In place: linear sRGB to X, Y, B.</summary>
        public static void ToXyb(float[] r, float[] g, float[] b, bool parallel)
        {
            var m = new float[9];
            for (int i = 0; i < 9; i++) m[i] = (float)Opsin[i];
            int n = r.Length;
            void Range(int s, int e)
            {
                for (int i = s; i < e; i++)
                {
                    float R = r[i], G = g[i], B = b[i];
                    float l = m[0] * R + m[1] * G + m[2] * B + Bias;
                    float mm = m[3] * R + m[4] * G + m[5] * B + Bias;
                    float sv = m[6] * R + m[7] * G + m[8] * B + Bias;
                    float lc = Cbrt(l) - CbrtBias, mc = Cbrt(mm) - CbrtBias, sc = Cbrt(sv) - CbrtBias;
                    r[i] = (lc - mc) * 0.5f;
                    g[i] = (lc + mc) * 0.5f;
                    b[i] = sc;
                }
            }
            JxlColour.Chunked(n, parallel, Range);
        }
    }
}
