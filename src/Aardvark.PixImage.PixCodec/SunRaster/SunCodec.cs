using System;
using System.Buffers.Binary;
using System.IO;

namespace Aardvark.ImageCodecs.SunRasterImpl
{
    internal sealed class SunHeader
    {
        public const uint Magic = 0x59A66A95;
        public int Width, Height, Depth, Length, Type, MapType, MapLength;

        public static bool IsMatch(ReadOnlySpan<byte> h) => h.Length >= 32 && BinaryPrimitives.ReadUInt32BigEndian(h) == Magic;

        public static SunHeader Parse(ReadOnlySpan<byte> d)
        {
            if (!IsMatch(d)) throw new ImageFormatException("SunRaster: not a Sun Raster file");
            uint w = U(d, 1), h = U(d, 2), depth = U(d, 3), length = U(d, 4), type = U(d, 5), mapType = U(d, 6), mapLength = U(d, 7);
            if (w == 0 || h == 0 || w > int.MaxValue / 32 || h > int.MaxValue) throw new ImageFormatException($"SunRaster: invalid size {w}x{h}");
            if (type is 4 or 5 or 0xFFFF) throw new ImageNotSupportedException($"SunRaster: type {type} (TIFF / IFF / experimental) is not supported");
            if (type > 3) throw new ImageFormatException($"SunRaster: unknown type {type}");
            if (depth is not (1 or 8 or 24 or 32)) throw new ImageNotSupportedException($"SunRaster: depth {depth} is not supported");
            if (mapType > 2) throw new ImageFormatException($"SunRaster: unknown colour map type {mapType}");
            if (mapLength > int.MaxValue - 32 || length > int.MaxValue) throw new ImageFormatException("SunRaster: invalid lengths");
            return new SunHeader { Width = (int)w, Height = (int)h, Depth = (int)depth, Length = (int)length, Type = (int)type, MapType = (int)mapType, MapLength = (int)mapLength };
        }

        static uint U(ReadOnlySpan<byte> d, int i) => BinaryPrimitives.ReadUInt32BigEndian(d.Slice(4 * i));

        public int RowBytes => (int)(((long)Width * Depth + 15) / 16 * 2);
        public int MapEntries => MapType == 1 ? MapLength / 3 : 0;
    }

    internal static class SunCodec
    {
        /// <summary>The colour model, and for 1 / 8 bit images the 256 entry R, G, B palette.</summary>
        static ColorModel ColorOf(SunHeader h, ReadOnlySpan<byte> d, out byte[]? palette)
        {
            palette = null;
            if (h.Depth == 24) return ColorModel.Rgb;
            if (h.Depth == 32) return ColorModel.Rgba;
            if (d.Length < 32L + h.MapLength) throw new ImageFormatException("SunRaster: the colour map is cut off");
            var pal = new byte[768];
            int n = Math.Min(h.MapEntries, 256);
            if (h.Depth == 1 && n < 2) n = 0;
            if (n > 0)
            {
                var map = d.Slice(32, h.MapLength);
                int e = h.MapEntries;
                for (int i = 0; i < n; i++) { pal[i * 3] = map[i]; pal[i * 3 + 1] = map[e + i]; pal[i * 3 + 2] = map[2 * e + i]; }
            }
            else if (h.Depth == 1) { pal[0] = pal[1] = pal[2] = 255; n = 2; }   // 0 white, 1 black
            else { for (int i = 0; i < 256; i++) pal[i * 3] = pal[i * 3 + 1] = pal[i * 3 + 2] = (byte)i; n = 256; }
            palette = pal;
            return Indexed.IsGray(pal, n) ? ColorModel.Gray : ColorModel.Rgb;
        }

        public static ImageInfo GetInfo(ReadOnlySpan<byte> data)
        {
            var h = SunHeader.Parse(data);
            var color = ColorOf(h, data, out _);
            return new ImageInfo(ImageFormat.SunRaster, h.Width, h.Height, RawImage.ChannelCount(color), SampleType.UInt8, color)
            {
                BitsPerSample = h.Depth == 1 && h.MapEntries < 2 ? 1 : 8,
            };
        }

        public static unsafe RawImage Decode(ReadOnlySpan<byte> data, SunRasterDecodeOptions opt)
        {
            var h = SunHeader.Parse(data);
            int w = h.Width, hh = h.Height;
            long pixels = (long)w * hh;
            if (pixels > opt.MaxPixels) throw new ImageNotSupportedException($"SunRaster: {w}x{hh} exceeds SunRasterDecodeOptions.MaxPixels");
            var color = ColorOf(h, data, out var palette);
            int start = 32 + h.MapLength;
            if (start > data.Length) start = data.Length;
            int avail = data.Length - start;
            if (h.Length > 0 && h.Length < avail && h.Type != 0) avail = h.Length;
            int rowBytes = h.RowBytes;
            long total = (long)rowBytes * hh;
            if (total > Array.MaxLength) throw new ImageNotSupportedException("SunRaster: image too large");
            if (pixels > opt.MaxUnbackedPixels && (long)avail * (h.Type == 2 ? 85 : 1) < total)
                throw new ImageFormatException($"SunRaster: {w}x{hh} image with only {avail} bytes of pixel data");

            // the stored rows, decompressed
            var raw = new byte[total];
            var src = data.Slice(start, avail);
            if (h.Type != 2) src.Slice(0, (int)Math.Min(total, avail)).CopyTo(raw);
            else
                fixed (byte* s = src)
                fixed (byte* o = raw)
                {
                    long n = 0;
                    int p = 0;
                    while (n < total && p < avail)
                    {
                        byte b = s[p++];
                        if (b != 0x80) { o[n++] = b; continue; }
                        if (p >= avail) break;
                        int c = s[p++];
                        if (c == 0) { o[n++] = 0x80; continue; }
                        if (p >= avail) break;
                        byte v = s[p++];
                        int k = (int)Math.Min(c + 1, total - n);
                        new Span<byte>(o + n, k).Fill(v);
                        n += k;
                    }
                }

            int ch = RawImage.ChannelCount(color);
            var dst = new byte[pixels * ch];
            bool rgbOrder = h.Type == 3;
            fixed (byte* r0 = raw)
            fixed (byte* d0 = dst)
            {
                if (h.Depth <= 8)
                {
                    var idx = new byte[w];
                    for (int y = 0; y < hh; y++)
                    {
                        byte* r = r0 + (long)y * rowBytes;
                        if (h.Depth == 8) new ReadOnlySpan<byte>(r, w).CopyTo(idx);
                        else for (int x = 0; x < w; x++) idx[x] = (byte)(r[x >> 3] >> (7 - (x & 7)) & 1);
                        Indexed.Expand(idx, palette, new Span<byte>(d0 + (long)y * w * ch, w * ch), color == ColorModel.Gray);
                    }
                }
                else
                {
                    int bpp = h.Depth / 8, a = bpp == 4 ? 1 : 0;
                    int ri = rgbOrder ? a : a + 2, bi = rgbOrder ? a + 2 : a;
                    for (int y = 0; y < hh; y++)
                    {
                        byte* r = r0 + (long)y * rowBytes;
                        byte* d = d0 + (long)y * w * ch;
                        for (int x = 0; x < w; x++, r += bpp, d += ch)
                        {
                            d[0] = r[ri]; d[1] = r[a + 1]; d[2] = r[bi];
                            if (ch == 4) d[3] = r[0];
                        }
                    }
                }
            }
            if (ch == 4) Indexed.OpaqueIfAllZero(dst, 4);
            return new RawImage(w, hh, color, dst, ch);
        }

        // ------------------------------------------------------------------------------------------------ encoding

        public static unsafe byte[] Encode(RawImage img, SunRasterEncodeOptions opt)
        {
            Indexed.RequireUInt8(img, "SunRaster");
            int w = img.Width, h = img.Height, ch = img.Channels;
            bool gray = img.Color is ColorModel.Gray or ColorModel.GrayAlpha;
            int depth = opt.Bilevel ? 1 : gray && (!img.HasAlpha || opt.Palette) || opt.Palette ? 8 : img.HasAlpha ? 32 : 24;
            byte[] map = Array.Empty<byte>();
            byte[]? indices = null;
            if (opt.Bilevel) indices = Indexed.Bilevel(img);
            else if (depth == 8 && !gray)
            {
                var (colors, idx, _) = Indexed.Palette(img, 256);
                indices = idx;
                int n = colors.Length;
                map = new byte[3 * n];
                for (int i = 0; i < n; i++) { map[i] = (byte)colors[i]; map[n + i] = (byte)(colors[i] >> 8); map[2 * n + i] = (byte)(colors[i] >> 16); }
            }
            long rowBytesL = ((long)w * depth + 15) / 16 * 2;
            if (rowBytesL * h > Array.MaxLength) throw new ImageNotSupportedException("SunRaster: image too large");
            int rowBytes = (int)rowBytesL;
            var raw = new byte[rowBytesL * h];
            var src = img.GetData<byte>();
            bool rgbOrder = opt.RgbOrder && !opt.Rle;
            fixed (byte* s0 = src)
            fixed (byte* r0 = raw)
                for (int y = 0; y < h; y++)
                {
                    byte* r = r0 + (long)y * rowBytes;
                    byte* s = s0 + (long)y * w * ch;
                    switch (depth)
                    {
                        case 1:
                            for (int x = 0; x < w; x++) r[x >> 3] |= (byte)((indices![(long)y * w + x] ^ 1) << (7 - (x & 7)));
                            break;
                        case 8:
                            if (indices != null) indices.AsSpan(y * w, w).CopyTo(new Span<byte>(r, w));
                            else for (int x = 0; x < w; x++) r[x] = s[x * ch];
                            break;
                        default:
                            int bpp = depth / 8, a = bpp == 4 ? 1 : 0;
                            int ri = rgbOrder ? a : a + 2, bi = rgbOrder ? a + 2 : a;
                            for (int x = 0; x < w; x++, r += bpp, s += ch)
                            {
                                if (gray) r[ri] = r[a + 1] = r[bi] = s[0];
                                else { r[ri] = s[0]; r[a + 1] = s[1]; r[bi] = s[2]; }
                                if (bpp == 4) r[0] = s[ch - 1];
                            }
                            break;
                    }
                }

            byte[] body = opt.Rle ? Rle(raw) : raw;
            var res = new byte[32 + map.Length + body.Length];
            var hd = res.AsSpan();
            BinaryPrimitives.WriteUInt32BigEndian(hd, SunHeader.Magic);
            BinaryPrimitives.WriteUInt32BigEndian(hd.Slice(4), (uint)w);
            BinaryPrimitives.WriteUInt32BigEndian(hd.Slice(8), (uint)h);
            BinaryPrimitives.WriteUInt32BigEndian(hd.Slice(12), (uint)depth);
            BinaryPrimitives.WriteUInt32BigEndian(hd.Slice(16), (uint)body.Length);
            BinaryPrimitives.WriteUInt32BigEndian(hd.Slice(20), opt.Rle ? 2u : rgbOrder && depth >= 24 ? 3u : 1u);
            BinaryPrimitives.WriteUInt32BigEndian(hd.Slice(24), map.Length > 0 ? 1u : 0u);
            BinaryPrimitives.WriteUInt32BigEndian(hd.Slice(28), (uint)map.Length);
            map.CopyTo(res, 32);
            body.CopyTo(res, 32 + map.Length);
            return res;
        }

        /// <summary>RT_BYTE_ENCODED: 0x80 n v is n + 1 times v, 0x80 0 a single 0x80, any other byte itself.</summary>
        static byte[] Rle(byte[] raw)
        {
            var ms = new MemoryStream(raw.Length / 2 + 16);
            int i = 0, n = raw.Length;
            while (i < n)
            {
                byte v = raw[i];
                int run = 1;
                while (i + run < n && run < 256 && raw[i + run] == v) run++;
                if (run >= 3 || v == 0x80 && run >= 2)
                {
                    ms.WriteByte(0x80); ms.WriteByte((byte)(run - 1)); ms.WriteByte(v);
                }
                else if (v == 0x80) { ms.WriteByte(0x80); ms.WriteByte(0); }
                else
                {
                    ms.WriteByte(v);
                    if (run == 2) ms.WriteByte(v);
                }
                i += run;
            }
            return ms.ToArray();
        }
    }
}
