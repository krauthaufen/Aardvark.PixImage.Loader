using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Aardvark.ImageCodecs.PcxImpl
{
    internal sealed class PcxHeader
    {
        public int Version, Encoding, Bits, Planes, BytesPerLine, Width, Height, HDpi, VDpi;

        public bool TrueColor => Bits == 8 && Planes >= 3;
        public int IndexBits => Bits * Planes;

        static int U16(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(o));

        public static bool IsMatch(ReadOnlySpan<byte> h)
        {
            if (h.Length < 128 || h[0] != 0x0A || h[1] is not (0 or 2 or 3 or 4 or 5) || h[2] > 1) return false;
            int bits = h[3], planes = h[65];
            if (bits is not (1 or 2 or 4 or 8) || planes is < 1 or > 4) return false;
            if (bits == 8 ? planes == 2 : bits * planes > 8) return false;
            int w = U16(h, 8) - U16(h, 4) + 1, hh = U16(h, 10) - U16(h, 6) + 1;
            return w > 0 && hh > 0 && U16(h, 66) >= (w * bits + 7) / 8;
        }

        public static PcxHeader Parse(ReadOnlySpan<byte> d)
        {
            if (!IsMatch(d)) throw new ImageFormatException("PCX: not a valid PCX header");
            return new PcxHeader
            {
                Version = d[1], Encoding = d[2], Bits = d[3], Planes = d[65], BytesPerLine = U16(d, 66),
                Width = U16(d, 8) - U16(d, 4) + 1, Height = U16(d, 10) - U16(d, 6) + 1, HDpi = U16(d, 12), VDpi = U16(d, 14),
            };
        }
    }

    internal static class PcxDecoder
    {
        const uint DcxMagic = 987654321;

        public static bool IsDcx(ReadOnlySpan<byte> d)
            => d.Length >= 8 && BinaryPrimitives.ReadUInt32LittleEndian(d) == DcxMagic && BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(4)) is >= 8;

        /// <summary>(offset, length) of the pages of a DCX file.</summary>
        static List<(int start, int length)> Pages(ReadOnlySpan<byte> d)
        {
            var offsets = new List<int>();
            for (int i = 4; i + 4 <= d.Length && offsets.Count < 1023; i += 4)
            {
                uint o = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(i));
                if (o == 0) break;
                if (o < 8 || o >= d.Length) throw new ImageFormatException("DCX: page offset outside the file");
                offsets.Add((int)o);
            }
            if (offsets.Count == 0) throw new ImageFormatException("DCX: no pages");
            var r = new List<(int, int)>(offsets.Count);
            for (int i = 0; i < offsets.Count; i++)
            {
                // a page ends where the next one in the file starts
                int end = d.Length;
                foreach (var o in offsets) if (o > offsets[i] && o < end) end = o;
                r.Add((offsets[i], end - offsets[i]));
            }
            return r;
        }

        public static ImageInfo GetInfo(ReadOnlySpan<byte> data, PcxDecodeOptions opt)
        {
            if (IsDcx(data))
            {
                var pages = Pages(data);
                var (s, l) = pages[0];
                return Info(data.Slice(s, l)) with { Format = ImageFormat.Dcx, FrameCount = pages.Count };
            }
            return Info(data);
        }

        static ImageInfo Info(ReadOnlySpan<byte> data)
        {
            var h = PcxHeader.Parse(data);
            var color = ColorOf(h, data, out _);
            return new ImageInfo(ImageFormat.Pcx, h.Width, h.Height, RawImage.ChannelCount(color), SampleType.UInt8, color)
            {
                BitsPerSample = h.TrueColor ? 8 : h.IndexBits == 1 ? 1 : 8,
                DpiX = h.HDpi > 0 && h.VDpi > 0 ? h.HDpi : 0,
                DpiY = h.HDpi > 0 && h.VDpi > 0 ? h.VDpi : 0,
            };
        }

        public static RawImage Decode(ReadOnlySpan<byte> data, PcxDecodeOptions opt)
        {
            if (IsDcx(data))
            {
                var (s, l) = Pages(data)[0];
                return DecodePage(data.Slice(s, l), opt);
            }
            return DecodePage(data, opt);
        }

        public static RawImage[] DecodeAll(ReadOnlySpan<byte> data, PcxDecodeOptions opt)
        {
            if (!IsDcx(data)) return new[] { DecodePage(data, opt) };
            var pages = Pages(data);
            var r = new RawImage[pages.Count];
            for (int i = 0; i < r.Length; i++) r[i] = DecodePage(data.Slice(pages[i].start, pages[i].length), opt);
            return r;
        }

        /// <summary>
        /// The colour model of the result, and the palette (256 R, G, B entries) of an indexed image: the VGA palette at the
        /// end of the file for more than 4 index bits (else a gray ramp), black and white for 1 bit monochrome, the 16 entry
        /// header palette otherwise.
        /// </summary>
        static ColorModel ColorOf(PcxHeader h, ReadOnlySpan<byte> data, out byte[]? palette)
        {
            palette = null;
            if (h.TrueColor) return h.Planes == 4 ? ColorModel.Rgba : ColorModel.Rgb;
            var pal = new byte[768];
            int n = 1 << h.IndexBits;
            if (h.IndexBits > 4)
            {
                if (HasVgaPalette(data)) data.Slice(data.Length - 768, 768).CopyTo(pal);
                else
                    for (int i = 0; i < n; i++) pal[i * 3] = pal[i * 3 + 1] = pal[i * 3 + 2] = (byte)(i * 255 / (n - 1));
            }
            else if (h.IndexBits == 1) { pal[3] = pal[4] = pal[5] = 255; }
            else data.Slice(16, 48).CopyTo(pal);
            palette = pal;
            return Indexed.IsGray(pal, n) ? ColorModel.Gray : ColorModel.Rgb;
        }

        static bool HasVgaPalette(ReadOnlySpan<byte> d) => d.Length >= 128 + 769 && d[d.Length - 769] == 0x0C;

        static unsafe RawImage DecodePage(ReadOnlySpan<byte> data, PcxDecodeOptions opt)
        {
            var h = PcxHeader.Parse(data);
            int w = h.Width, hh = h.Height;
            long pixels = (long)w * hh;
            if (pixels > opt.MaxPixels) throw new ImageNotSupportedException($"PCX: {w}x{hh} exceeds PcxDecodeOptions.MaxPixels");
            var color = ColorOf(h, data, out var palette);
            int end = data.Length;
            if (!h.TrueColor && h.IndexBits > 4 && HasVgaPalette(data)) end -= 769;
            int bpl = h.BytesPerLine, lineBytes = h.Planes * bpl;
            // RLE codes at most 63 bytes in two
            if (pixels > opt.MaxUnbackedPixels && (long)(end - 128) * (h.Encoding == 1 ? 32 : 1) < (long)lineBytes * hh)
                throw new ImageFormatException($"PCX: {w}x{hh} image with only {end - 128} bytes of pixel data");

            int ch = RawImage.ChannelCount(color);
            var dst = new byte[pixels * ch];
            var line = new byte[lineBytes];
            var idx = h.TrueColor ? null : new byte[w];
            int pos = 128, runLeft = 0;
            byte runValue = 0;
            fixed (byte* src = data)
            fixed (byte* l = line)
                for (int y = 0; y < hh; y++)
                {
                    // one scan line, all planes; runs may continue into the next line
                    int n = 0;
                    if (h.Encoding == 0)
                    {
                        n = Math.Min(lineBytes, end - pos);
                        if (n > 0) { new ReadOnlySpan<byte>(src + pos, n).CopyTo(line); pos += n; }
                        else n = 0;
                    }
                    else
                        while (n < lineBytes)
                        {
                            if (runLeft > 0)
                            {
                                int k = Math.Min(runLeft, lineBytes - n);
                                new Span<byte>(l + n, k).Fill(runValue);
                                n += k; runLeft -= k;
                                continue;
                            }
                            if (pos >= end) break;
                            byte b = src[pos++];
                            if ((b & 0xC0) == 0xC0)
                            {
                                if (pos >= end) break;
                                runLeft = b & 0x3F;
                                runValue = src[pos++];
                            }
                            else l[n++] = b;
                        }
                    if (n < lineBytes) line.AsSpan(n).Clear();   // truncated: the rest is black

                    var row = dst.AsSpan(y * w * ch, w * ch);
                    if (h.TrueColor)
                        fixed (byte* o = row)
                            for (int c = 0; c < h.Planes; c++)
                            {
                                byte* p = l + c * bpl;
                                for (int x = 0; x < w; x++) o[x * ch + c] = p[x];
                            }
                    else
                    {
                        Indices(l, h, w, idx!);
                        Indexed.Expand(idx, palette, row, color == ColorModel.Gray);
                    }
                }
            if (ch == 4) Indexed.OpaqueIfAllZero(dst, 4);
            var img = new RawImage(w, hh, color, dst, ch);
            if (h.HDpi > 0 && h.VDpi > 0) img.Metadata = new ImageMetadata { DpiX = h.HDpi, DpiY = h.VDpi };
            return img;
        }

        /// <summary>Palette indices of one scan line: plane p gives bits p * Bits and up, pixels are packed most significant bits first.</summary>
        static unsafe void Indices(byte* line, PcxHeader h, int w, byte[] idx)
        {
            int bits = h.Bits, bpl = h.BytesPerLine;
            fixed (byte* d = idx)
            {
                if (h.Planes == 1 && bits == 8) { new ReadOnlySpan<byte>(line, w).CopyTo(idx); return; }
                new Span<byte>(d, w).Clear();
                int mask = (1 << bits) - 1, perByte = 8 / bits;
                for (int p = 0; p < h.Planes; p++)
                {
                    byte* s = line + p * bpl;
                    int shift = p * bits;
                    if (bits == 1)
                        for (int x = 0; x < w; x++) d[x] |= (byte)((s[x >> 3] >> (7 - (x & 7)) & 1) << shift);
                    else
                        for (int x = 0; x < w; x++)
                        {
                            int v = s[x / perByte] >> (8 - bits - x % perByte * bits) & mask;
                            d[x] |= (byte)(v << shift);
                        }
                }
            }
        }
    }
}
