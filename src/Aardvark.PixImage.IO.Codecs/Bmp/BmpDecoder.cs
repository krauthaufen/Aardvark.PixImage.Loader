using System;
using System.Buffers.Binary;
using System.Numerics;

namespace Aardvark.ImageCodecs.BmpImpl
{
    internal enum BmpCompression { Rgb, Rle8, Rle4, BitFields, Jpeg, Png, Rle24, Huffman1D }

    /// <summary>Everything in front of the pixel data.</summary>
    internal sealed class BmpHeader
    {
        public int Width, Height;
        public bool TopDown;
        public int Bpp;
        public BmpCompression Compression;
        public uint RMask, GMask, BMask, AMask;
        public bool HasMasks;              // masks given by the file (bit fields) rather than defaults
        public byte[] Palette = Array.Empty<byte>();   // RGB triplets
        public int PaletteEntries;
        public long PixelOffset;
        public double DpiX, DpiY;
        public byte[]? Icc;
        public int InfoSize;

        const uint ProfileEmbedded = 0x4D424544;   // 'MBED'

        static uint U32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(o));
        static int I32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadInt32LittleEndian(d.Slice(o));
        static ushort U16(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(o));

        static bool KnownInfoSize(uint s) => s == 12 || s == 16 || s == 40 || s == 52 || s == 56 || s == 64 || s == 108 || s == 124
            || (s > 16 && s < 64);   // OS/2 2.x headers may be cut anywhere after the first 16 bytes

        public static bool IsMatch(ReadOnlySpan<byte> h) => h.Length >= 18 && h[0] == 'B' && h[1] == 'M' && KnownInfoSize(U32(h, 14));

        /// <summary>Parses the headers. <paramref name="dib"/>: the data starts with the info header (no file header).</summary>
        public static BmpHeader Parse(ReadOnlySpan<byte> data, bool dib)
        {
            int start = dib ? 0 : 14;
            if (!dib && !IsMatch(data)) throw new ImageFormatException("BMP: not a BMP file");
            if (data.Length < start + 4) throw new ImageFormatException("BMP: header truncated");
            uint size = U32(data, start);
            if (!KnownInfoSize(size)) throw new ImageFormatException($"BMP: unknown header size {size}");
            if (data.Length < start + size) throw new ImageFormatException("BMP: header truncated");
            var h = new BmpHeader { InfoSize = (int)size };
            var info = data.Slice(start, (int)size);
            bool os2 = size == 16 || size == 64 || (size > 16 && size < 40) || (size > 40 && size < 64 && size != 52 && size != 56);
            uint compression = 0;
            if (size == 12)
            {
                h.Width = U16(info, 4);
                h.Height = U16(info, 6);
                h.Bpp = U16(info, 10);
            }
            else
            {
                h.Width = I32(info, 4);
                int height = I32(info, 8);
                h.TopDown = height < 0;
                h.Height = height == int.MinValue ? throw new ImageFormatException("BMP: bad height") : Math.Abs(height);
                h.Bpp = U16(info, 14);
                if (size >= 20) compression = U32(info, 16);
                if (size >= 32)
                {
                    h.DpiX = Dpi(U32(info, 24));
                    h.DpiY = Dpi(U32(info, 28));
                }
                if (size >= 36) h.PaletteEntries = (int)Math.Min(U32(info, 32), 1u << 16);
            }
            if (h.Width <= 0 || h.Height <= 0) throw new ImageFormatException("BMP: image dimensions must be positive");

            h.Compression = (os2, compression) switch
            {
                (_, 0) => BmpCompression.Rgb,
                (_, 1) => BmpCompression.Rle8,
                (_, 2) => BmpCompression.Rle4,
                (false, 3) or (false, 6) => BmpCompression.BitFields,
                (true, 3) => BmpCompression.Huffman1D,
                (false, 4) => BmpCompression.Jpeg,
                (true, 4) => BmpCompression.Rle24,
                (false, 5) => BmpCompression.Png,
                _ => throw new ImageNotSupportedException($"BMP: compression {compression} is not supported"),
            };
            if (h.Compression == BmpCompression.Huffman1D) throw new ImageNotSupportedException("BMP: OS/2 Huffman 1D compression is not supported");
            if (h.Compression is BmpCompression.Rle8 && h.Bpp != 8 || h.Compression is BmpCompression.Rle4 && h.Bpp != 4
                || h.Compression is BmpCompression.Rle24 && h.Bpp != 24)
                throw new ImageFormatException($"BMP: {h.Compression} with {h.Bpp} bits per pixel");
            if (h.Compression is not (BmpCompression.Jpeg or BmpCompression.Png))
            {
                if (h.Bpp is not (1 or 2 or 4 or 8 or 16 or 24 or 32))
                    throw h.Bpp == 64 ? new ImageNotSupportedException("BMP: 64 bit pixels are not supported") : new ImageFormatException($"BMP: {h.Bpp} bits per pixel");
                if (h.Compression == BmpCompression.BitFields && h.Bpp is not (16 or 24 or 32))
                    throw new ImageFormatException($"BMP: bit fields with {h.Bpp} bits per pixel");
            }

            int pos = start + (int)size;
            // masks: inside V2+ headers, or following a 40 byte header
            if (h.Compression == BmpCompression.BitFields)
            {
                h.HasMasks = true;
                if (size >= 52)
                {
                    h.RMask = U32(info, 40); h.GMask = U32(info, 44); h.BMask = U32(info, 48);
                    if (size >= 56) h.AMask = U32(info, 52);
                }
                else
                {
                    int n = compression == 6 ? 4 : 3;
                    if (data.Length < pos + 4 * n) throw new ImageFormatException("BMP: bit field masks truncated");
                    h.RMask = U32(data, pos); h.GMask = U32(data, pos + 4); h.BMask = U32(data, pos + 8);
                    if (n == 4) h.AMask = U32(data, pos + 12);
                    pos += 4 * n;
                }
            }
            else if (h.Bpp == 16) { h.RMask = 0x7C00; h.GMask = 0x03E0; h.BMask = 0x001F; }
            else if (h.Bpp == 32) { h.RMask = 0xFF0000; h.GMask = 0xFF00; h.BMask = 0xFF; }

            long fileOffset = dib ? 0 : U32(data, 10);

            // palette
            if (h.Bpp <= 8 && h.Compression is not (BmpCompression.Jpeg or BmpCompression.Png))
            {
                int entrySize = size == 12 ? 3 : 4;
                int count = h.PaletteEntries > 0 ? Math.Min(h.PaletteEntries, 1 << 16) : 1 << h.Bpp;
                // a palette must not run into the pixels; files that say otherwise get the entries that fit
                long limit = !dib && fileOffset > pos ? Math.Min(fileOffset, data.Length) : data.Length;
                count = (int)Math.Min(count, Math.Max(0, (limit - pos) / entrySize));
                h.Palette = new byte[count * 3];
                for (int i = 0; i < count; i++)
                {
                    int e = pos + i * entrySize;
                    h.Palette[i * 3] = data[e + 2]; h.Palette[i * 3 + 1] = data[e + 1]; h.Palette[i * 3 + 2] = data[e];
                }
                h.PaletteEntries = count;
                pos += count * entrySize;
            }
            else h.PaletteEntries = 0;

            // pixel data: where the file header says, unless that points into the headers or beyond the file
            h.PixelOffset = !dib && fileOffset >= pos && fileOffset < data.Length ? fileOffset : pos;

            if (size >= 124 && U32(info, 56) == ProfileEmbedded)
            {
                long off = start + (long)U32(info, 112), len = U32(info, 116);
                if (len > 0 && off >= start && off + len <= data.Length) h.Icc = data.Slice((int)off, (int)len).ToArray();
            }
            return h;
        }

        static double Dpi(uint pelsPerMeter) => pelsPerMeter == 0 || pelsPerMeter > 10_000_000 ? 0 : Math.Round(pelsPerMeter * 0.0254, 3);

        public static bool InfoNeedsAllData(ReadOnlySpan<byte> prefix)
        {
            if (prefix.Length < 34) return false;
            uint size = U32(prefix, 14);
            if (size < 20) return false;
            uint c = U32(prefix, 30);
            return c is 1 or 2 || c == 4 && (size == 16 || size == 64 || size < 40);
        }

        /// <summary>Whether the palette holds only grays (the image is then Gray).</summary>
        public bool GrayPalette
        {
            get
            {
                if (PaletteEntries == 0) return false;
                for (int i = 0; i < PaletteEntries; i++)
                    if (Palette[i * 3] != Palette[i * 3 + 1] || Palette[i * 3] != Palette[i * 3 + 2]) return false;
                return true;
            }
        }
    }

    internal static class BmpDecoder
    {
        sealed class Plan
        {
            public required BmpHeader H;
            public ColorModel Color;
            public bool Wide;                          // UInt16 samples
            public bool UseAlpha;                      // the alpha mask (or reserved byte) is read
            public bool RleHoles;                      // RLE leaves pixels out: they become transparent
        }

        static Plan MakePlan(BmpHeader h, ReadOnlySpan<byte> data, BmpDecodeOptions opt)
        {
            var p = new Plan { H = h };
            if (h.Bpp <= 8 || h.Compression == BmpCompression.Rle24)
            {
                bool gray = h.Bpp <= 8 && h.GrayPalette;
                if (h.Compression is BmpCompression.Rle4 or BmpCompression.Rle8 or BmpCompression.Rle24)
                    p.RleHoles = RleCountDefined(h, data, null) < (long)h.Width * h.Height;
                p.Color = gray ? (p.RleHoles ? ColorModel.GrayAlpha : ColorModel.Gray) : (p.RleHoles ? ColorModel.Rgba : ColorModel.Rgb);
                return p;
            }
            if (h.Bpp == 32 && !h.HasMasks && opt.AlphaFromReservedByte) h.AMask = 0xFF000000;
            p.UseAlpha = h.AMask != 0;
            p.Color = p.UseAlpha ? ColorModel.Rgba : ColorModel.Rgb;
            p.Wide = Width(h.RMask) > 8 || Width(h.GMask) > 8 || Width(h.BMask) > 8 || Width(h.AMask) > 8;
            foreach (var m in new[] { h.RMask, h.GMask, h.BMask, h.AMask })
                if (m != 0 && (m >> BitOperations.TrailingZeroCount(m) & (m >> BitOperations.TrailingZeroCount(m)) + 1) != 0)
                    throw new ImageFormatException("BMP: bit field mask is not contiguous");
            return p;
        }

        static int Width(uint mask) => BitOperations.PopCount(mask);

        public static ImageInfo GetInfo(ReadOnlySpan<byte> data, BmpDecodeOptions opt)
        {
            var h = BmpHeader.Parse(data, false);
            if (h.Compression is BmpCompression.Jpeg or BmpCompression.Png)
            {
                var inner = ImageCodec.GetInfo(Embedded(h, data));
                return inner with { Format = ImageFormat.Bmp, Orientation = 0, OrientationApplied = false, DpiX = h.DpiX, DpiY = h.DpiY, HasIccProfile = h.Icc != null };
            }
            var p = MakePlan(h, data, opt);
            return new ImageInfo(ImageFormat.Bmp, h.Width, h.Height, RawImage.ChannelCount(p.Color), p.Wide ? SampleType.UInt16 : SampleType.UInt8, p.Color)
            {
                BitsPerSample = h.Bpp <= 8 ? 8 : h.Bpp == 24 ? 8 : Math.Max(Math.Max(Width(h.RMask), Width(h.GMask)), Math.Max(Width(h.BMask), Width(h.AMask))),
                DpiX = h.DpiX, DpiY = h.DpiY,
                HasIccProfile = h.Icc != null,
            };
        }

        static ReadOnlySpan<byte> Embedded(BmpHeader h, ReadOnlySpan<byte> data)
            => h.PixelOffset < data.Length ? data.Slice((int)h.PixelOffset) : throw new ImageFormatException("BMP: embedded image missing");

        public static RawImage Decode(ReadOnlySpan<byte> data, BmpDecodeOptions opt, bool dibOnly = false)
        {
            var h = BmpHeader.Parse(data, dibOnly);
            var meta = h.DpiX > 0 || h.DpiY > 0 || h.Icc != null ? new ImageMetadata { DpiX = h.DpiX, DpiY = h.DpiY, IccProfile = h.Icc } : null;
            if (h.Compression is BmpCompression.Jpeg or BmpCompression.Png)
            {
                var inner = ImageCodec.Decode(Embedded(h, data));
                inner.Metadata = meta;
                return inner;
            }
            long pixels = (long)h.Width * h.Height;
            if (pixels > opt.MaxPixels) throw new ImageNotSupportedException($"BMP: {h.Width}x{h.Height} exceeds BmpDecodeOptions.MaxPixels");
            var p = MakePlan(h, data, opt);
            int ch = RawImage.ChannelCount(p.Color);
            if (pixels * ch > Array.MaxLength) throw new ImageNotSupportedException("BMP: image too large for a single array");

            var px = data.Slice((int)Math.Min(h.PixelOffset, data.Length));
            RawImage img;
            if (h.Compression is BmpCompression.Rle4 or BmpCompression.Rle8 or BmpCompression.Rle24)
                img = DecodeRle(h, p, data);
            else
            {
                long stride = ((long)h.Width * h.Bpp + 31) / 32 * 4;
                if (pixels > opt.MaxUnbackedPixels && px.Length < stride * h.Height)
                    throw new ImageFormatException($"BMP: {h.Width}x{h.Height} image with only {px.Length} bytes of pixel data");
                img = p.Wide ? DecodeWide(h, p, px, stride) : DecodePlain(h, p, px, stride);
            }
            img.Metadata = meta;
            return img;
        }

        // ------------------------------------------------------------------ uncompressed

        static RawImage DecodePlain(BmpHeader h, Plan p, ReadOnlySpan<byte> px, long stride)
        {
            int w = h.Width, hh = h.Height, ch = RawImage.ChannelCount(p.Color);
            var dst = new byte[(long)w * hh * ch];
            var pal = h.Palette;
            int entries = h.PaletteEntries;
            var mr = new Field(h.RMask, 8); var mg = new Field(h.GMask, 8); var mb = new Field(h.BMask, 8); var ma = new Field(h.AMask, 8);
            bool anyAlpha = false;
            int bytesPer = h.Bpp / 8;
            for (int y = 0; y < hh; y++)
            {
                long rowStart = (h.TopDown ? y : hh - 1 - y) * stride;
                if (rowStart >= px.Length) continue;   // truncated: the rest stays black
                var row = px.Slice((int)rowStart, (int)Math.Min(stride, px.Length - rowStart));
                var o = dst.AsSpan((int)((long)y * w * ch), w * ch);
                if (h.Bpp <= 8)
                {
                    int perByte = 8 / h.Bpp, mask = (1 << h.Bpp) - 1;
                    int avail = Math.Min(w, row.Length * perByte);
                    for (int x = 0; x < avail; x++)
                    {
                        int idx = row[x / perByte] >> (8 - h.Bpp - x % perByte * h.Bpp) & mask;
                        Palette(pal, entries, idx, o, x, ch);
                    }
                }
                else if (h.Bpp == 24 && !h.HasMasks)
                {
                    int avail = Math.Min(w, row.Length / 3);
                    for (int x = 0; x < avail; x++)
                    {
                        o[x * 3] = row[x * 3 + 2]; o[x * 3 + 1] = row[x * 3 + 1]; o[x * 3 + 2] = row[x * 3];
                    }
                }
                else
                {
                    int avail = Math.Min(w, row.Length / bytesPer);
                    for (int x = 0; x < avail; x++)
                    {
                        uint v = bytesPer switch
                        {
                            2 => BinaryPrimitives.ReadUInt16LittleEndian(row.Slice(x * 2)),
                            3 => (uint)(row[x * 3] | row[x * 3 + 1] << 8 | row[x * 3 + 2] << 16),
                            _ => BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(x * 4)),
                        };
                        o[x * ch] = (byte)mr.Get(v); o[x * ch + 1] = (byte)mg.Get(v); o[x * ch + 2] = (byte)mb.Get(v);
                        if (ch == 4)
                        {
                            byte a = (byte)ma.Get(v);
                            o[x * 4 + 3] = a;
                            anyAlpha |= a != 0;
                        }
                    }
                }
            }
            // an alpha channel that is zero everywhere was not meant as one (as browsers decide)
            if (ch == 4 && !anyAlpha)
                for (long i = 3; i < dst.LongLength; i += 4) dst[i] = 255;
            return new RawImage(w, hh, p.Color, dst, ch);
        }

        static RawImage DecodeWide(BmpHeader h, Plan p, ReadOnlySpan<byte> px, long stride)
        {
            int w = h.Width, hh = h.Height, ch = RawImage.ChannelCount(p.Color);
            var dst = new ushort[(long)w * hh * ch];
            var mr = new Field(h.RMask, 16); var mg = new Field(h.GMask, 16); var mb = new Field(h.BMask, 16); var ma = new Field(h.AMask, 16);
            bool anyAlpha = false;
            int bytesPer = h.Bpp / 8;
            for (int y = 0; y < hh; y++)
            {
                long rowStart = (h.TopDown ? y : hh - 1 - y) * stride;
                if (rowStart >= px.Length) continue;
                var row = px.Slice((int)rowStart, (int)Math.Min(stride, px.Length - rowStart));
                var o = dst.AsSpan((int)((long)y * w * ch), w * ch);
                int avail = Math.Min(w, row.Length / bytesPer);
                for (int x = 0; x < avail; x++)
                {
                    uint v = bytesPer switch
                    {
                        2 => BinaryPrimitives.ReadUInt16LittleEndian(row.Slice(x * 2)),
                        3 => (uint)(row[x * 3] | row[x * 3 + 1] << 8 | row[x * 3 + 2] << 16),
                        _ => BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(x * 4)),
                    };
                    o[x * ch] = (ushort)mr.Get(v); o[x * ch + 1] = (ushort)mg.Get(v); o[x * ch + 2] = (ushort)mb.Get(v);
                    if (ch == 4)
                    {
                        ushort a = (ushort)ma.Get(v);
                        o[x * 4 + 3] = a;
                        anyAlpha |= a != 0;
                    }
                }
            }
            if (ch == 4 && !anyAlpha)
                for (long i = 3; i < dst.LongLength; i += 4) dst[i] = ushort.MaxValue;
            return new RawImage(w, hh, p.Color, dst, ch);
        }

        /// <summary>
        /// A bit field scaled to the output sample size, rounding to nearest (v * 255 / 31 for 5 bits to 8): what the BMP Suite's
        /// reference renderings show. Fields wider than the output keep their top bits.
        /// </summary>
        readonly struct Field
        {
            readonly int shift, bits, outBits;
            readonly uint max;
            public Field(uint mask, int outBits)
            {
                shift = mask == 0 ? 0 : BitOperations.TrailingZeroCount(mask);
                max = mask >> shift;
                bits = BitOperations.PopCount(mask);
                this.outBits = outBits;
            }
            public uint Get(uint v)
            {
                if (max == 0) return 0;
                uint x = v >> shift & max;
                if (bits >= outBits) return x >> (bits - outBits);
                uint outMax = (1u << outBits) - 1;
                return (uint)(((ulong)x * outMax + max / 2) / max);
            }
        }

        static void Palette(byte[] pal, int entries, int idx, Span<byte> o, int x, int ch)
        {
            // an index beyond the palette is black
            byte r = 0, g = 0, b = 0;
            if (idx < entries) { r = pal[idx * 3]; g = pal[idx * 3 + 1]; b = pal[idx * 3 + 2]; }
            if (ch == 1) o[x] = r;
            else if (ch == 2) { o[x * 2] = r; o[x * 2 + 1] = 255; }
            else
            {
                o[x * ch] = r; o[x * ch + 1] = g; o[x * ch + 2] = b;
                if (ch == 4) o[x * 4 + 3] = 255;
            }
        }

        // ------------------------------------------------------------------ RLE

        /// <summary>
        /// Walks the run codes and returns how many pixels they define. With <paramref name="put"/> each defined pixel is handed
        /// over as (x, y from the top, value: palette index or 0xRRGGBB for RLE24).
        /// </summary>
        static long RleCountDefined(BmpHeader h, ReadOnlySpan<byte> data, Action<int, int, int>? put)
        {
            int w = h.Width, hh = h.Height;
            var s = data.Slice((int)Math.Min(h.PixelOffset, data.Length));
            int pos = 0, x = 0, y = 0;
            long defined = 0;
            bool rle4 = h.Compression == BmpCompression.Rle4, rle24 = h.Compression == BmpCompression.Rle24;
            void Emit(int v)
            {
                if (x < w && y < hh)
                {
                    put?.Invoke(x, h.TopDown ? y : hh - 1 - y, v);
                    defined++;
                }
                x++;
            }
            while (y < hh && pos + 1 < s.Length)
            {
                int n = s[pos], c = s[pos + 1];
                pos += 2;
                if (n > 0)
                {
                    if (rle24)
                    {
                        if (pos + 2 > s.Length) break;
                        int v = s[pos + 1] << 16 | s[pos] << 8 | c;   // stored B, G, R: c is blue
                        pos += 2;
                        for (int i = 0; i < n && x < w; i++) Emit(v);
                    }
                    else if (rle4)
                        for (int i = 0; i < n && x < w; i++) Emit(i % 2 == 0 ? c >> 4 : c & 15);
                    else
                        for (int i = 0; i < n && x < w; i++) Emit(c);
                    continue;
                }
                switch (c)
                {
                    case 0: x = 0; y++; break;                      // end of line
                    case 1: return defined;                          // end of bitmap
                    case 2:                                          // delta
                        if (pos + 1 >= s.Length) return defined;
                        x += s[pos]; y += s[pos + 1];
                        pos += 2;
                        break;
                    default:                                         // absolute run of c pixels
                        {
                            int bytes = rle24 ? c * 3 : rle4 ? (c + 1) / 2 : c;
                            int avail = Math.Min(bytes, s.Length - pos);
                            for (int i = 0; i < c; i++)
                            {
                                int v;
                                if (rle24)
                                {
                                    if (i * 3 + 2 >= avail) break;
                                    v = s[pos + i * 3 + 2] << 16 | s[pos + i * 3 + 1] << 8 | s[pos + i * 3];
                                }
                                else if (rle4)
                                {
                                    if (i / 2 >= avail) break;
                                    v = i % 2 == 0 ? s[pos + i / 2] >> 4 : s[pos + i / 2] & 15;
                                }
                                else
                                {
                                    if (i >= avail) break;
                                    v = s[pos + i];
                                }
                                Emit(v);
                            }
                            pos += bytes + (bytes & 1);              // runs are padded to 16 bits
                            break;
                        }
                }
            }
            return defined;
        }

        static RawImage DecodeRle(BmpHeader h, Plan p, ReadOnlySpan<byte> data)
        {
            int w = h.Width, ch = RawImage.ChannelCount(p.Color);
            var dst = new byte[(long)w * h.Height * ch];   // left out pixels stay transparent black
            var pal = h.Palette;
            int entries = h.PaletteEntries;
            bool rle24 = h.Compression == BmpCompression.Rle24;
            RleCountDefined(h, data, (x, y, v) =>
            {
                var o = dst.AsSpan((int)((long)y * w * ch), w * ch);
                if (rle24)
                {
                    o[x * ch] = (byte)(v >> 16); o[x * ch + 1] = (byte)(v >> 8); o[x * ch + 2] = (byte)v;
                    if (ch == 4) o[x * 4 + 3] = 255;
                }
                else Palette(pal, entries, v, o, x, ch);
            });
            return new RawImage(w, h.Height, p.Color, dst, ch);
        }
    }
}
