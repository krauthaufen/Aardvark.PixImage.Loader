using System;

namespace Aardvark.ImageCodecs
{
    /// <summary>
    /// A hue / saturation / value mapping table (ProfileHueSatMap or ProfileLookTable), applied in linear ProPhoto RGB as
    /// chapter 6 describes: RGB to HSV, trilinear lookup (hue wrapping around), hue shift, saturation scale (clipped to 1),
    /// value scale (clipped to 1, as for standard dynamic range profiles), back to RGB. With the sRGB encoding the value
    /// axis is indexed and scaled sRGB encoded.
    /// </summary>
    internal sealed class DngHsv
    {
        readonly float[] _t;
        readonly int _hd, _sd, _vd;
        readonly bool _srgb;

        public DngHsv(float[] table, int[] dims, int encoding)
        {
            _t = table; _hd = dims[0]; _sd = dims[1]; _vd = dims[2]; _srgb = encoding == 1 && _vd > 1;
        }

        /// <summary>The maps of the calibrations blended with the calibration weights; null without maps.</summary>
        public static DngHsv? HueSatMap(DngInfo info, double[] weights)
        {
            var dims = info.HueSatMapDims;
            if (dims == null) return null;
            float[]? only = null;
            int count = 0;
            double wsum = 0;
            for (int i = 0; i < info.Calibrations.Count; i++)
                if (info.Calibrations[i].HueSatMap is { } m) { only = m; count++; wsum += weights[i]; }
            if (only == null) return null;
            if (count == 1 || wsum <= 0) return new DngHsv(only, dims, info.HueSatMapEncoding);
            var t = new float[only.Length];
            for (int i = 0; i < info.Calibrations.Count; i++)
            {
                if (info.Calibrations[i].HueSatMap is not { } m || weights[i] == 0) continue;
                float w = (float)(weights[i] / wsum);
                for (int k = 0; k < t.Length; k++) t[k] += w * m[k];
            }
            return new DngHsv(t, dims, info.HueSatMapEncoding);
        }

        static float Enc(float v) => v <= 0.0031308f ? 12.92f * v : 1.055f * MathF.Pow(v, 1 / 2.4f) - 0.055f;
        static float Dec(float v) => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);

        public void Apply(ref float r, ref float g, ref float b)
        {
            // RGB -> HSV
            float max = MathF.Max(r, MathF.Max(g, b)), min = MathF.Min(r, MathF.Min(g, b)), d = max - min;
            if (!(max > 0)) return;
            float h = 0, s = d / max, v = max;
            if (d > 0)
            {
                if (max == r) h = (g - b) / d;
                else if (max == g) h = 2 + (b - r) / d;
                else h = 4 + (r - g) / d;
                h *= 60;
                if (h < 0) h += 360;
            }
            if (_srgb) v = Enc(MathF.Min(v, 1));

            // lookup: hue wraps around, saturation and value are clamped
            float hs = h * _hd / 360f, ss = Math.Clamp(s, 0, 1) * (_sd - 1), vs = _vd > 1 ? Math.Clamp(v, 0, 1) * (_vd - 1) : 0;
            int h0 = (int)hs; float fh = hs - h0; h0 %= _hd; int h1 = (h0 + 1) % _hd;
            int s0 = Math.Min((int)ss, _sd - 1); float fs = ss - s0; int s1 = Math.Min(s0 + 1, _sd - 1);
            int v0 = Math.Min((int)vs, _vd - 1); float fv = vs - v0; int v1 = Math.Min(v0 + 1, _vd - 1);
            float a0 = 0, a1 = 0, a2 = 0;
            for (int iv = 0; iv < 2; iv++)
            {
                float wv = iv == 0 ? 1 - fv : fv;
                if (wv == 0) continue;
                int vv = iv == 0 ? v0 : v1;
                for (int ih = 0; ih < 2; ih++)
                {
                    float wh = ih == 0 ? 1 - fh : fh;
                    if (wh == 0) continue;
                    int hh = ih == 0 ? h0 : h1;
                    for (int js = 0; js < 2; js++)
                    {
                        float w = wv * wh * (js == 0 ? 1 - fs : fs);
                        if (w == 0) continue;
                        int k = ((vv * _hd + hh) * _sd + (js == 0 ? s0 : s1)) * 3;
                        a0 += w * _t[k]; a1 += w * _t[k + 1]; a2 += w * _t[k + 2];
                    }
                }
            }
            h += a0;
            h = (h % 360 + 360) % 360;
            s = MathF.Min(s * a1, 1);
            v = MathF.Min(v * a2, 1);
            if (_srgb) v = Dec(v);

            // HSV -> RGB
            float c = v * s, hp = h / 60, x = c * (1 - MathF.Abs(hp % 2 - 1)), m0 = v - c;
            int sector = Math.Min((int)hp, 5);
            (r, g, b) = sector switch
            {
                0 => (c, x, 0f),
                1 => (x, c, 0f),
                2 => (0f, c, x),
                3 => (0f, x, c),
                4 => (x, 0f, c),
                _ => (c, 0f, x),
            };
            r += m0; g += m0; b += m0;
        }
    }
}
