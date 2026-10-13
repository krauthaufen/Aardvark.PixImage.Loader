using System;

namespace PixCodec
{
    /// <summary>Photometric post-processing of the raw sample image (inversion, palette, CMYK, Lab, alpha).</summary>
    internal static class TiffColor
    {
        public static Array Finish(TiffImage img, Array raw, int w, int h, ushort[]? colorMap)
        {
            int rc = img.RawChannels;
            long pixels = (long)w * h;
            switch (img.Photometric)
            {
                case 0:
                    Invert(raw, img.RawType, rc, img.SampleMax);
                    break;
                case 3:
                    return Palette(raw, img.RawType, pixels, rc, img.HasAlpha, img.Bits, colorMap!);
                case 5:
                    if (!img.ConvertToRgb) return raw;
                    raw = Cmyk(raw, img.RawType, pixels, rc, img.HasAlpha);
                    break;
                case 8: case 9: case 10:
                    if (!img.ConvertToRgb) return raw;
                    raw = LabToRgb(raw, img.RawType, pixels, rc, img.HasAlpha, img.Photometric);
                    break;
            }
            if (img.AssociatedAlpha) Unpremultiply(raw, img.RawType, img.OutChannels, img.SampleMax);
            return raw;
        }

        // Note: all switches go by SampleType. A `byte[]` type pattern would also match an sbyte[] at runtime.

        /// <summary>
        /// MinIsWhite: flips the colour sample of unsigned data so that 0 is black (max - v, max being the largest
        /// value of the stored bit depth). Signed and float data is left as stored.
        /// </summary>
        static void Invert(Array raw, SampleType type, int step, ulong max)
        {
            switch (type)
            {
                case SampleType.UInt8:
                {
                    var a = (byte[])raw;
                    byte m = (byte)max;
                    if (step == 1)
                    {
                        int i = 0;
                        var ones = new System.Numerics.Vector<byte>(m);
                        for (; i + System.Numerics.Vector<byte>.Count <= a.Length; i += System.Numerics.Vector<byte>.Count)
                            (ones - new System.Numerics.Vector<byte>(a, i)).CopyTo(a, i);
                        for (; i < a.Length; i++) a[i] = (byte)(m - a[i]);
                    }
                    else
                        for (int i = 0; i < a.Length; i += step) a[i] = (byte)(m - a[i]);
                    break;
                }
                case SampleType.UInt16:
                {
                    var a = (ushort[])raw;
                    ushort m = (ushort)max;
                    for (int i = 0; i < a.Length; i += step) a[i] = (ushort)(m - a[i]);
                    break;
                }
                case SampleType.UInt32:
                {
                    var a = (uint[])raw;
                    uint m = (uint)max;
                    for (int i = 0; i < a.Length; i += step) a[i] = m - a[i];
                    break;
                }
                case SampleType.UInt64:
                {
                    var a = (ulong[])raw;
                    for (int i = 0; i < a.Length; i += step) a[i] = max - a[i];
                    break;
                }
            }
        }

        /// <summary>
        /// Expands palette indices to 8-bit RGB. Colormap entries are 16 bit and reduced with >> 8 (as libtiff and
        /// Pillow do); a colormap that only holds values below 256 is taken to be 8 bit already.
        /// </summary>
        static byte[] Palette(Array raw, SampleType type, long pixels, int rc, bool alpha, int bits, ushort[] cm)
        {
            int n = 1 << bits;
            bool is8 = true;
            for (int i = 0; i < 3 * n; i++) if (cm[i] >= 256) { is8 = false; break; }
            var lut = new byte[3 * n];
            for (int i = 0; i < n; i++)
                for (int c = 0; c < 3; c++)
                    lut[i * 3 + c] = (byte)(is8 ? cm[c * n + i] : cm[c * n + i] >> 8);
            int oc = alpha ? 4 : 3;
            var o = new byte[pixels * oc];
            int amax = n - 1;
            int si = 0, di = 0;
            if (type == SampleType.UInt8)
            {
                var a = (byte[])raw;
                for (long p = 0; p < pixels; p++, si += rc, di += oc)
                {
                    int l = Math.Min((int)a[si], n - 1) * 3;
                    o[di] = lut[l]; o[di + 1] = lut[l + 1]; o[di + 2] = lut[l + 2];
                    if (alpha) o[di + 3] = (byte)((a[si + 1] * 255 + amax / 2) / amax);
                }
            }
            else
            {
                var a = (ushort[])raw;
                for (long p = 0; p < pixels; p++, si += rc, di += oc)
                {
                    int l = Math.Min((int)a[si], n - 1) * 3;
                    o[di] = lut[l]; o[di + 1] = lut[l + 1]; o[di + 2] = lut[l + 2];
                    if (alpha) o[di + 3] = (byte)((a[si + 1] * 255 + amax / 2) / amax);
                }
            }
            return o;
        }

        /// <summary>Naive CMYK to RGB: c' = (max - c) * (max - k) / max, rounded. Extra samples other than alpha are dropped.</summary>
        static Array Cmyk(Array raw, SampleType type, long pixels, int rc, bool alpha)
        {
            int oc = alpha ? 4 : 3;
            if (type == SampleType.UInt8)
            {
                var a = (byte[])raw;
                var o = new byte[pixels * oc];
                int si = 0, di = 0;
                for (long p = 0; p < pixels; p++, si += rc, di += oc)
                {
                    int k = 255 - a[si + 3];
                    o[di] = (byte)(((255 - a[si]) * k + 127) / 255);
                    o[di + 1] = (byte)(((255 - a[si + 1]) * k + 127) / 255);
                    o[di + 2] = (byte)(((255 - a[si + 2]) * k + 127) / 255);
                    if (alpha) o[di + 3] = a[si + 4];
                }
                return o;
            }
            else
            {
                var s = (ushort[])raw;
                var o = new ushort[pixels * oc];
                int si = 0, di = 0;
                for (long p = 0; p < pixels; p++, si += rc, di += oc)
                {
                    uint k = 65535u - s[si + 3];
                    o[di] = (ushort)(((65535u - s[si]) * k + 32767) / 65535);
                    o[di + 1] = (ushort)(((65535u - s[si + 1]) * k + 32767) / 65535);
                    o[di + 2] = (ushort)(((65535u - s[si + 2]) * k + 32767) / 65535);
                    if (alpha) o[di + 3] = s[si + 4];
                }
                return o;
            }
        }

        /// <summary>
        /// L*a*b* (relative to the D50 white of the TIFF / ICC definitions) to sRGB:
        /// <code>
        /// fy = (L + 16) / 116, fx = fy + a / 500, fz = fy - b / 200
        /// X = 0.96422 g(fx), Y = g(fy), Z = 0.82521 g(fz),   g(t) = t^3 if t > 6/29 else 3 (6/29)^2 (t - 4/29)
        /// linear sRGB = M * XYZ, M = the XYZ(D50) to sRGB matrix (sRGB primaries, Bradford adapted from D65 to D50)
        /// sRGB = 12.92 c if c &lt;= 0.0031308 else 1.055 c^(1/2.4) - 0.055, clamped to 0..1 and rounded
        /// </code>
        /// Sample encodings: CIELab (8): L unsigned over 0..100, a/b two's complement (16 bit: in 1/256 units);
        /// ICCLab (9): a/b offset by 128 (16 bit: L up to 0xFF00, a/b offset 0x8000 in 1/256 units);
        /// ITULab (10): a = (s - 128) * 170 / 255, b = (s - 96) * 200 / 255 (the T.42 default ranges).
        /// </summary>
        static Array LabToRgb(Array raw, SampleType type, long pixels, int rc, bool alpha, int photometric)
        {
            int oc = alpha ? 4 : 3;
            bool wide = type == SampleType.UInt16;
            var b8 = wide ? null : (byte[])raw;
            var b16 = wide ? (ushort[])raw : null;
            var o8 = wide ? null : new byte[pixels * oc];
            var o16 = wide ? new ushort[pixels * oc] : null;
            float max = wide ? 65535f : 255f;
            int si = 0, di = 0;
            for (long p = 0; p < pixels; p++, si += rc, di += oc)
            {
                float l, a, b;
                if (!wide)
                {
                    int s0 = b8![si], s1 = b8[si + 1], s2 = b8[si + 2];
                    l = s0 * (100f / 255);
                    switch (photometric)
                    {
                        case 8: a = (sbyte)s1; b = (sbyte)s2; break;
                        case 9: a = s1 - 128; b = s2 - 128; break;
                        default: a = (s1 - 128) * (170f / 255); b = (s2 - 96) * (200f / 255); break;
                    }
                }
                else
                {
                    int s0 = b16![si], s1 = b16[si + 1], s2 = b16[si + 2];
                    switch (photometric)
                    {
                        case 8: l = s0 * (100f / 65535); a = (short)s1 / 256f; b = (short)s2 / 256f; break;
                        case 9: l = s0 * (100f / 65280); a = s1 / 256f - 128; b = s2 / 256f - 128; break;
                        default: l = s0 * (100f / 65535); a = (s1 - 32768) * (170f / 65535); b = (s2 - 24576) * (200f / 65535); break;
                    }
                }
                float fy = (l + 16) / 116, fx = fy + a / 500, fz = fy - b / 200;
                float x = 0.96422f * LabInv(fx), y = LabInv(fy), z = 0.82521f * LabInv(fz);
                float r = 3.1338561f * x - 1.6168667f * y - 0.4906146f * z;
                float g = -0.9787684f * x + 1.9161415f * y + 0.0334540f * z;
                float bl = 0.0719453f * x - 0.2289914f * y + 1.4052427f * z;
                if (!wide)
                {
                    o8![di] = (byte)(SrgbEncode(r) * max + 0.5f);
                    o8[di + 1] = (byte)(SrgbEncode(g) * max + 0.5f);
                    o8[di + 2] = (byte)(SrgbEncode(bl) * max + 0.5f);
                    if (alpha) o8[di + 3] = b8![si + 3];
                }
                else
                {
                    o16![di] = (ushort)(SrgbEncode(r) * max + 0.5f);
                    o16[di + 1] = (ushort)(SrgbEncode(g) * max + 0.5f);
                    o16[di + 2] = (ushort)(SrgbEncode(bl) * max + 0.5f);
                    if (alpha) o16[di + 3] = b16![si + 3];
                }
            }
            return wide ? o16! : o8!;
        }

        static float LabInv(float t) => t > 6f / 29 ? t * t * t : 3 * (6f / 29) * (6f / 29) * (t - 4f / 29);

        static float SrgbEncode(float c)
        {
            if (!(c > 0)) return 0;
            if (c >= 1) return 1;
            return c <= 0.0031308f ? 12.92f * c : 1.055f * MathF.Pow(c, 1 / 2.4f) - 0.055f;
        }

        /// <summary>CIE XYZ (D65 white, as LogLuv files are defined) to linear RGB with Rec.709 / sRGB primaries. No gamma, no clamping.</summary>
        public static void XyzToLinearRgb(ref float x, ref float y, ref float z)
        {
            float r = 3.240479f * x - 1.537150f * y - 0.498535f * z;
            float g = -0.969256f * x + 1.875991f * y + 0.041556f * z;
            float b = 0.055648f * x - 0.204043f * y + 1.057311f * z;
            x = r; y = g; z = b;
        }

        /// <summary>
        /// Associated alpha to straight alpha: c = round(c * max / a), clamped; pixels with zero alpha are left as stored.
        /// Signed integer data is not touched.
        /// </summary>
        static void Unpremultiply(Array raw, SampleType type, int ch, ulong max)
        {
            int cc = ch - 1;
            switch (type)
            {
                case SampleType.UInt8:
                {
                    var a = (byte[])raw;
                    uint m = (uint)max;
                    for (int i = 0; i + ch <= a.Length; i += ch)
                    {
                        uint al = a[i + cc];
                        if (al == 0 || al >= m) continue;
                        for (int c = 0; c < cc; c++) a[i + c] = (byte)Math.Min(m, (a[i + c] * m + al / 2) / al);
                    }
                    break;
                }
                case SampleType.UInt16:
                {
                    var a = (ushort[])raw;
                    uint m = (uint)max;
                    for (int i = 0; i + ch <= a.Length; i += ch)
                    {
                        uint al = a[i + cc];
                        if (al == 0 || al >= m) continue;
                        for (int c = 0; c < cc; c++) a[i + c] = (ushort)Math.Min(m, (a[i + c] * m + al / 2) / al);
                    }
                    break;
                }
                case SampleType.UInt32:
                {
                    var a = (uint[])raw;
                    for (int i = 0; i + ch <= a.Length; i += ch)
                    {
                        ulong al = a[i + cc];
                        if (al == 0 || al >= max) continue;
                        for (int c = 0; c < cc; c++) a[i + c] = (uint)Math.Min(max, (a[i + c] * max + al / 2) / al);
                    }
                    break;
                }
                case SampleType.UInt64:
                {
                    var a = (ulong[])raw;
                    for (int i = 0; i + ch <= a.Length; i += ch)
                    {
                        ulong al = a[i + cc];
                        if (al == 0 || al >= max) continue;
                        for (int c = 0; c < cc; c++)
                        {
                            UInt128 v = ((UInt128)a[i + c] * max + al / 2) / al;
                            a[i + c] = v > max ? max : (ulong)v;
                        }
                    }
                    break;
                }
                case SampleType.Float16:
                {
                    var a = (Half[])raw;
                    for (int i = 0; i + ch <= a.Length; i += ch)
                    {
                        float al = (float)a[i + cc];
                        if (al == 0 || al == 1) continue;
                        for (int c = 0; c < cc; c++) a[i + c] = (Half)((float)a[i + c] / al);
                    }
                    break;
                }
                case SampleType.Float32:
                {
                    var a = (float[])raw;
                    for (int i = 0; i + ch <= a.Length; i += ch)
                    {
                        float al = a[i + cc];
                        if (al == 0 || al == 1) continue;
                        for (int c = 0; c < cc; c++) a[i + c] /= al;
                    }
                    break;
                }
                case SampleType.Float64:
                {
                    var a = (double[])raw;
                    for (int i = 0; i + ch <= a.Length; i += ch)
                    {
                        double al = a[i + cc];
                        if (al == 0 || al == 1) continue;
                        for (int c = 0; c < cc; c++) a[i + c] /= al;
                    }
                    break;
                }
            }
        }
    }
}
