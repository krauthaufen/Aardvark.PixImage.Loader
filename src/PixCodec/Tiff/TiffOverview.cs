using System;
using System.Numerics;

namespace PixCodec
{
    /// <summary>Overview generation: halves an image by averaging 2 x 2 blocks (fewer pixels at an odd right / bottom edge).</summary>
    internal static class TiffOverview
    {
        public static RawImage Halve(RawImage src)
        {
            int w = Math.Max(1, (src.Width + 1) / 2), h = Math.Max(1, (src.Height + 1) / 2);
            // by sample type, not by array type (an sbyte[] also matches a byte[] pattern)
            Array data = src.SampleType switch
            {
                SampleType.UInt8 => Halve((byte[])src.Data, src.Width, src.Height, src.Channels, w, h, true),
                SampleType.Int8 => Halve((sbyte[])src.Data, src.Width, src.Height, src.Channels, w, h, true),
                SampleType.UInt16 => Halve((ushort[])src.Data, src.Width, src.Height, src.Channels, w, h, true),
                SampleType.Int16 => Halve((short[])src.Data, src.Width, src.Height, src.Channels, w, h, true),
                SampleType.UInt32 => Halve((uint[])src.Data, src.Width, src.Height, src.Channels, w, h, true),
                SampleType.Int32 => Halve((int[])src.Data, src.Width, src.Height, src.Channels, w, h, true),
                SampleType.UInt64 => HalveWide((ulong[])src.Data, src.Width, src.Height, src.Channels, w, h),
                SampleType.Int64 => HalveWide((long[])src.Data, src.Width, src.Height, src.Channels, w, h),
                SampleType.Float16 => Halve((Half[])src.Data, src.Width, src.Height, src.Channels, w, h, false),
                SampleType.Float32 => Halve((float[])src.Data, src.Width, src.Height, src.Channels, w, h, false),
                _ => Halve((double[])src.Data, src.Width, src.Height, src.Channels, w, h, false),
            };
            return new RawImage(w, h, src.Color, data, src.Channels);
        }

        /// <summary>Halves an image by taking every second pixel of every second row (for palette indices, which cannot be averaged).</summary>
        public static RawImage Decimate(RawImage src)
        {
            int w = Math.Max(1, (src.Width + 1) / 2), h = Math.Max(1, (src.Height + 1) / 2);
            var dst = RawImage.Create(w, h, src.Color, src.SampleType, src.Channels);
            int pix = src.Channels * RawImage.BytesPerSample(src.SampleType);
            var s = src.GetBytes(); var d = dst.GetBytes();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    s.Slice((2 * y * src.Width + 2 * x) * pix, pix).CopyTo(d.Slice((y * w + x) * pix));
            return dst;
        }

        /// <summary>Mean of the block in double precision; integers are rounded half up.</summary>
        static T[] Halve<T>(T[] s, int sw, int sh, int ch, int w, int h, bool round) where T : unmanaged, INumber<T>
        {
            var d = new T[(long)w * h * ch];
            for (int y = 0; y < h; y++)
            {
                int y0 = 2 * y, y1 = Math.Min(y0 + 1, sh - 1);
                for (int x = 0; x < w; x++)
                {
                    int x0 = 2 * x, x1 = Math.Min(x0 + 1, sw - 1);
                    long a = ((long)y0 * sw + x0) * ch, b = ((long)y0 * sw + x1) * ch, c = ((long)y1 * sw + x0) * ch, e = ((long)y1 * sw + x1) * ch;
                    long o = ((long)y * w + x) * ch;
                    for (int k = 0; k < ch; k++)
                    {
                        // at an odd edge the missing neighbours repeat the last row / column, i.e. a plain mean of what is there
                        double v = (double.CreateTruncating(s[a + k]) + double.CreateTruncating(s[b + k]) + double.CreateTruncating(s[c + k]) + double.CreateTruncating(s[e + k])) * 0.25;
                        d[o + k] = round ? T.CreateSaturating(Math.Floor(v + 0.5)) : T.CreateTruncating(v);
                    }
                }
            }
            return d;
        }

        /// <summary>64-bit integers: exact 128-bit sums, rounded half up.</summary>
        static T[] HalveWide<T>(T[] s, int sw, int sh, int ch, int w, int h) where T : unmanaged, INumber<T>
        {
            var d = new T[(long)w * h * ch];
            for (int y = 0; y < h; y++)
            {
                int y0 = 2 * y, y1 = Math.Min(y0 + 1, sh - 1);
                for (int x = 0; x < w; x++)
                {
                    int x0 = 2 * x, x1 = Math.Min(x0 + 1, sw - 1);
                    long a = ((long)y0 * sw + x0) * ch, b = ((long)y0 * sw + x1) * ch, c = ((long)y1 * sw + x0) * ch, e = ((long)y1 * sw + x1) * ch;
                    long o = ((long)y * w + x) * ch;
                    for (int k = 0; k < ch; k++)
                    {
                        Int128 sum = Int128.CreateTruncating(s[a + k]) + Int128.CreateTruncating(s[b + k]) + Int128.CreateTruncating(s[c + k]) + Int128.CreateTruncating(s[e + k]);
                        d[o + k] = T.CreateTruncating((sum + 2) >> 2);
                    }
                }
            }
            return d;
        }
    }
}
