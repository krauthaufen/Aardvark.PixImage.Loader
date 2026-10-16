using System;

namespace Aardvark.ImageCodecs
{
    /// <summary>Palette and bilevel helpers shared by the legacy raster formats (PCX, Sun Raster, XPM, XBM, WBMP).</summary>
    internal static class Indexed
    {
        /// <summary>Whether the first <paramref name="count"/> entries of an R, G, B palette are all gray.</summary>
        public static bool IsGray(ReadOnlySpan<byte> rgb, int count)
        {
            for (int i = 0; i < count; i++)
                if (rgb[i * 3] != rgb[i * 3 + 1] || rgb[i * 3] != rgb[i * 3 + 2]) return false;
            return true;
        }

        /// <summary>
        /// Looks indices (one byte per pixel) up in an R, G, B palette of 256 entries: Gray takes the red value, Rgb all three.
        /// </summary>
        public static unsafe void Expand(ReadOnlySpan<byte> indices, ReadOnlySpan<byte> palette, Span<byte> dst, bool gray)
        {
            if (palette.Length < 768) throw new ArgumentException("palette needs 256 entries");
            fixed (byte* s = indices)
            fixed (byte* p = palette)
            fixed (byte* d = dst)
            {
                int n = indices.Length;
                if (gray)
                    for (int i = 0; i < n; i++) d[i] = p[s[i] * 3];
                else
                    for (int i = 0; i < n; i++)
                    {
                        byte* e = p + s[i] * 3;
                        byte* o = d + i * 3;
                        o[0] = e[0]; o[1] = e[1]; o[2] = e[2];
                    }
            }
        }

        public static void RequireUInt8(RawImage img, string format)
        {
            if (img.SampleType != SampleType.UInt8)
                throw new ImageNotSupportedException($"{format}: only UInt8 samples can be written, not {img.SampleType}");
            if (img.Color == ColorModel.Other)
                throw new ImageNotSupportedException($"{format}: only Gray, GrayAlpha, Rgb and Rgba can be written");
        }

        /// <summary>
        /// The opaque colour of every pixel packed as r | g &lt;&lt; 8 | b &lt;&lt; 16 | 255 &lt;&lt; 24, or 0 for pixels whose alpha is below
        /// 128 when <paramref name="transparent"/> is set (else alpha is ignored).
        /// </summary>
        public static unsafe uint[] Pack(RawImage img, bool transparent)
        {
            int n = img.Width * img.Height, ch = img.Channels;
            var r = new uint[n];
            var src = img.GetData<byte>();
            bool gray = img.Color is ColorModel.Gray or ColorModel.GrayAlpha, alpha = transparent && img.HasAlpha;
            fixed (byte* s0 = src)
            fixed (uint* d = r)
                for (int i = 0; i < n; i++)
                {
                    byte* s = s0 + (long)i * ch;
                    if (alpha && s[ch - 1] < 128) { d[i] = 0; continue; }
                    d[i] = gray ? s[0] * 0x010101u | 0xFF000000u : s[0] | (uint)s[1] << 8 | (uint)s[2] << 16 | 0xFF000000u;
                }
            return r;
        }

        /// <summary>
        /// A palette of at most <paramref name="maxColors"/> entries for the colours of <paramref name="img"/> (alpha ignored)
        /// and the index of every pixel: exact when the image has no more colours, else from the Wu quantizer of the GIF
        /// encoder (no dithering). Entries are packed as in <see cref="Pack"/>.
        /// </summary>
        public static (uint[] colors, byte[] indices, bool exact) Palette(RawImage img, int maxColors)
        {
            var px = Pack(img, false);
            var idx = new byte[px.Length];
            var set = new GifColorSet();
            if (set.MapAll(px, idx) && set.Count <= maxColors) return (set.Colors.AsSpan(0, set.Count).ToArray(), idx, true);
            var q = new GifQuantizer();
            q.Add(px);
            var colors = q.BuildPalette(maxColors, 2);
            new GifPalette(colors, colors.Length, false, null).Map(px, idx, img.Width, img.Height, GifDither.None);
            return (colors, idx, false);
        }

        /// <summary>
        /// One byte per pixel, 1 for white and 0 for black: luma (or gray) at or above 128 is white, and so are pixels whose
        /// alpha is below 128 (the background shows).
        /// </summary>
        public static unsafe byte[] Bilevel(RawImage img)
        {
            int n = img.Width * img.Height, ch = img.Channels;
            var r = new byte[n];
            var src = img.GetData<byte>();
            bool gray = img.Color is ColorModel.Gray or ColorModel.GrayAlpha, alpha = img.HasAlpha;
            fixed (byte* s0 = src)
            fixed (byte* d = r)
                for (int i = 0; i < n; i++)
                {
                    byte* s = s0 + (long)i * ch;
                    int y = gray ? s[0] : (77 * s[0] + 150 * s[1] + 29 * s[2] + 128) >> 8;
                    d[i] = (byte)(y >= 128 || alpha && s[ch - 1] < 128 ? 1 : 0);
                }
            return r;
        }

        /// <summary>Sets an alpha channel that is zero everywhere to opaque (such files do not mean it as alpha).</summary>
        public static void OpaqueIfAllZero(byte[] d, int ch)
        {
            for (long i = ch - 1; i < d.LongLength; i += ch)
                if (d[i] != 0) return;
            for (long i = ch - 1; i < d.LongLength; i += ch) d[i] = 255;
        }
    }
}
