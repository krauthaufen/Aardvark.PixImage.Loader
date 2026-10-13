using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using PixCodec.Compression;

namespace PixCodec
{
    internal static unsafe class PngEncoder
    {
        const int DataChunkSize = 1 << 18;

        // ------------------------------------------------------------------ entry points

        public static void Encode(RawImage image, Stream output, PngEncodeOptions options)
        {
            ArgumentNullException.ThrowIfNull(image);
            ArgumentNullException.ThrowIfNull(output);
            var source = Source.ForImage(image, options, allowTransforms: true);
            WriteStill(source, image.Metadata, output, options);
        }

        public static void EncodeIndexed(RawImage indices, ReadOnlySpan<byte> paletteRgba, Stream output, PngEncodeOptions options)
        {
            ArgumentNullException.ThrowIfNull(indices);
            ArgumentNullException.ThrowIfNull(output);
            var source = Source.ForIndices(indices, paletteRgba);
            WriteStill(source, indices.Metadata, output, options);
        }

        static void WriteStill(Source source, ImageMetadata? metadata, Stream output, PngEncodeOptions options)
        {
            int level = Math.Clamp(options.CompressionLevel, 0, 9);
            WriteHead(source, metadata, output, options, level);
            using (var chunks = new DataStream(output, PngChunks.IDAT, null))
            {
                using (var z = Zlib.CreateCompressor(chunks, level))
                    WriteScanlines(z, source, 0, 0, source.Width, source.Height, options.Interlace, ResolveFilter(options, source, level));
                chunks.Finish();
            }
            WriteChunk(output, PngChunks.IEND, default);
        }

        public static void EncodeAnimation(IEnumerable<PngInputFrame> frames, Stream output, PngEncodeOptions options)
        {
            ArgumentNullException.ThrowIfNull(frames);
            ArgumentNullException.ThrowIfNull(output);
            // The frame count goes into the acTL chunk in front of the first frame, so the list is needed up front.
            var list = frames as IReadOnlyList<PngInputFrame> ?? frames.ToList();
            if (list.Count == 0) throw new ArgumentException("an animation needs at least one frame", nameof(frames));
            if (options.LoopCount < 0) throw new ArgumentOutOfRangeException(nameof(options), "LoopCount must not be negative");
            var first = list[0].Image ?? throw new ArgumentException("frame without an image", nameof(frames));
            foreach (var f in list)
            {
                if (f.Image == null) throw new ArgumentException("frame without an image", nameof(frames));
                if (f.Image.Width != first.Width || f.Image.Height != first.Height || f.Image.SampleType != first.SampleType || f.Image.Color != first.Color)
                    throw new ArgumentException("all frames of an animation must have the same size, sample type and color model", nameof(frames));
                if (f.DelayNumerator is < 0 or > 65535 || f.DelayDenominator is < 0 or > 65535)
                    throw new ArgumentOutOfRangeException(nameof(frames), "frame delay numerator and denominator must be 0..65535");
            }

            int level = Math.Clamp(options.CompressionLevel, 0, 9);
            var source = Source.ForImage(first, options, allowTransforms: false);
            var filter = ResolveFilter(options, source, level);
            WriteHead(source, first.Metadata, output, options, level);

            Span<byte> actl = stackalloc byte[8];
            BinaryPrimitives.WriteInt32BigEndian(actl, list.Count);
            BinaryPrimitives.WriteInt32BigEndian(actl.Slice(4), options.LoopCount);
            WriteChunk(output, PngChunks.acTL, actl);

            var sequence = new Sequence();
            for (int k = 0; k < list.Count; k++)
            {
                var image = list[k].Image;
                int x = 0, y = 0, w = first.Width, h = first.Height;
                if (k > 0)
                {
                    source = Source.ForImage(image, options, allowTransforms: false);
                    if (options.FrameDifferencing) ChangedRegion(list[k - 1].Image, image, out x, out y, out w, out h);
                }

                Span<byte> fctl = stackalloc byte[26];
                BinaryPrimitives.WriteUInt32BigEndian(fctl, sequence.Next++);
                BinaryPrimitives.WriteInt32BigEndian(fctl.Slice(4), w);
                BinaryPrimitives.WriteInt32BigEndian(fctl.Slice(8), h);
                BinaryPrimitives.WriteInt32BigEndian(fctl.Slice(12), x);
                BinaryPrimitives.WriteInt32BigEndian(fctl.Slice(16), y);
                BinaryPrimitives.WriteUInt16BigEndian(fctl.Slice(20), (ushort)list[k].DelayNumerator);
                BinaryPrimitives.WriteUInt16BigEndian(fctl.Slice(22), (ushort)list[k].DelayDenominator);
                fctl[24] = (byte)PngDisposal.None;
                fctl[25] = (byte)PngBlend.Source;
                WriteChunk(output, PngChunks.fcTL, fctl);

                // The first frame is the default image (IDAT); later frames go into sequence numbered fdAT chunks.
                using var chunks = k == 0 ? new DataStream(output, PngChunks.IDAT, null) : new DataStream(output, PngChunks.fdAT, sequence);
                using (var z = Zlib.CreateCompressor(chunks, level))
                    WriteScanlines(z, source, x, y, w, h, options.Interlace, filter);
                chunks.Finish();
            }
            WriteChunk(output, PngChunks.IEND, default);
        }

        static PngFilterMode ResolveFilter(PngEncodeOptions options, Source source, int level)
        {
            if (options.Filter != PngFilterMode.Auto) return options.Filter;
            // Filtering rarely helps indexed and sub-byte data, and never without compression.
            return source.ColorType == 3 || source.BitDepth < 8 || level == 0 ? PngFilterMode.None : PngFilterMode.Adaptive;
        }

        /// <summary>Smallest rectangle containing every pixel that differs between two frames (1x1 if none does).</summary>
        static void ChangedRegion(RawImage previous, RawImage current, out int x, out int y, out int w, out int h)
        {
            var a = previous.GetBytes();
            var b = current.GetBytes();
            int pixelBytes = current.Channels * RawImage.BytesPerSample(current.SampleType);
            int rowBytes = current.Width * pixelBytes;
            int top = 0, bottom = current.Height - 1;
            while (top <= bottom && a.Slice(top * rowBytes, rowBytes).SequenceEqual(b.Slice(top * rowBytes, rowBytes))) top++;
            if (top > bottom) { x = 0; y = 0; w = 1; h = 1; return; }
            while (a.Slice(bottom * rowBytes, rowBytes).SequenceEqual(b.Slice(bottom * rowBytes, rowBytes))) bottom--;
            int left = current.Width, right = -1;
            for (int row = top; row <= bottom; row++)
            {
                var ra = a.Slice(row * rowBytes, rowBytes);
                var rb = b.Slice(row * rowBytes, rowBytes);
                int firstDiff = ra.CommonPrefixLength(rb);
                if (firstDiff == rowBytes) continue;
                int lastDiff = rowBytes - 1;
                while (ra[lastDiff] == rb[lastDiff]) lastDiff--;
                left = Math.Min(left, firstDiff / pixelBytes);
                right = Math.Max(right, lastDiff / pixelBytes);
            }
            x = left; y = top; w = right - left + 1; h = bottom - top + 1;
        }

        // ------------------------------------------------------------------ what gets written

        /// <summary>
        /// The image as it goes into the file: PNG color type and bit depth, palette and transparency chunks, and
        /// access to its rows. Rows are handed out "unpacked": the bytes of the PNG scanline for 8 and 16 bit
        /// samples, one byte per sample below that.
        /// </summary>
        sealed class Source
        {
            enum Kind { Bytes, Swap16, DropAlpha8, DropAlpha16, GrayReduce }

            public int Width, Height, ColorType, BitDepth, Channels;
            public byte[]? Plte, Trns;
            /// <summary>The palette was given by the caller (rather than derived from the pixels).</summary>
            public bool ExplicitPalette;
            /// <summary>Divisor applied to 8 bit gray samples when writing at a lower bit depth, else 1.</summary>
            public int GrayDivisor = 1;

            Kind _kind;
            byte[]? _bytes;
            ushort[]? _words;
            int _sourceChannels;

            public int UnpackedPixelBytes => BitDepth >= 8 ? Channels * BitDepth >> 3 : 1;

            public static Source ForImage(RawImage image, PngEncodeOptions options, bool allowTransforms)
            {
                int depth = image.SampleType switch
                {
                    SampleType.UInt8 => 8,
                    SampleType.UInt16 => 16,
                    _ => throw new ImageNotSupportedException($"PNG cannot store {image.SampleType} samples"),
                };
                int colorType = image.Color switch
                {
                    ColorModel.Gray => 0,
                    ColorModel.Rgb => 2,
                    ColorModel.GrayAlpha => 4,
                    ColorModel.Rgba => 6,
                    _ => throw new ImageNotSupportedException($"PNG cannot store the color model {image.Color}"),
                };
                var s = new Source
                {
                    Width = image.Width, Height = image.Height, ColorType = colorType, BitDepth = depth,
                    Channels = image.Channels, _sourceChannels = image.Channels,
                };
                if (depth == 8) { s._bytes = image.GetData<byte>(); s._kind = Kind.Bytes; }
                else { s._words = image.GetData<ushort>(); s._kind = Kind.Swap16; }
                if (((long)s.Width * s.Channels * depth + 7) >> 3 > Array.MaxLength - 64)
                    throw new ImageNotSupportedException("image rows are too long for the PNG encoder");

                if (allowTransforms)
                {
                    if (options.AutoPalette && depth == 8 && image.Channels >= 3 && s.TryPalette()) return s;
                    if (options.ColorKeyTransparency && image.HasAlpha && s.TryColorKey()) return s;
                    if (options.ReduceGrayDepth && depth == 8 && colorType == 0) s.TryReduceGray();
                }
                return s;
            }

            public static Source ForIndices(RawImage indices, ReadOnlySpan<byte> paletteRgba)
            {
                if (indices.SampleType != SampleType.UInt8 || indices.Channels != 1)
                    throw new ArgumentException("palette indices must be a single channel UInt8 image", nameof(indices));
                if (paletteRgba.Length == 0 || paletteRgba.Length % 4 != 0 || paletteRgba.Length > 1024)
                    throw new ArgumentException("the palette must hold 1..256 RGBA entries (4 bytes each)", nameof(paletteRgba));
                int count = paletteRgba.Length / 4;
                var data = indices.GetData<byte>();
                foreach (byte i in data)
                    if (i >= count) throw new ArgumentException($"pixel refers to palette entry {i} but the palette has {count}", nameof(indices));

                var plte = new byte[count * 3];
                int alphaCount = 0;
                for (int i = 0; i < count; i++)
                {
                    paletteRgba.Slice(i * 4, 3).CopyTo(plte.AsSpan(i * 3));
                    if (paletteRgba[i * 4 + 3] != 255) alphaCount = i + 1;
                }
                var trns = new byte[alphaCount];
                for (int i = 0; i < alphaCount; i++) trns[i] = paletteRgba[i * 4 + 3];
                return new Source
                {
                    Width = indices.Width, Height = indices.Height, ColorType = 3, Channels = 1, _sourceChannels = 1,
                    BitDepth = count <= 2 ? 1 : count <= 4 ? 2 : count <= 16 ? 4 : 8,
                    Plte = plte, Trns = alphaCount > 0 ? trns : null, ExplicitPalette = true,
                    _bytes = data, _kind = Kind.Bytes,
                };
            }

            /// <summary>Copies <paramref name="count"/> pixels of row <paramref name="y"/> starting at <paramref name="x0"/>.</summary>
            public void FillRow(int y, int x0, int count, Span<byte> dst)
            {
                int start = checked((y * Width + x0) * _sourceChannels);
                switch (_kind)
                {
                    case Kind.Bytes:
                        _bytes.AsSpan(start, count * _sourceChannels).CopyTo(dst);
                        break;
                    case Kind.Swap16: // PNG samples are big endian
                        BinaryPrimitives.ReverseEndianness(_words.AsSpan(start, count * _sourceChannels), MemoryMarshal.Cast<byte, ushort>(dst));
                        break;
                    case Kind.GrayReduce:
                    {
                        var src = _bytes.AsSpan(start, count);
                        for (int i = 0; i < src.Length; i++) dst[i] = (byte)(src[i] / GrayDivisor);
                        break;
                    }
                    case Kind.DropAlpha8:
                    {
                        var src = _bytes.AsSpan(start, count * _sourceChannels);
                        if (Channels == 1)
                            for (int i = 0; i < count; i++) dst[i] = src[i * 2];
                        else
                            for (int i = 0; i < count; i++) { dst[i * 3] = src[i * 4]; dst[i * 3 + 1] = src[i * 4 + 1]; dst[i * 3 + 2] = src[i * 4 + 2]; }
                        break;
                    }
                    case Kind.DropAlpha16:
                    {
                        var src = _words.AsSpan(start, count * _sourceChannels);
                        var d = MemoryMarshal.Cast<byte, ushort>(dst);
                        for (int i = 0, o = 0; i < src.Length; i += _sourceChannels)
                            for (int c = 0; c < Channels; c++) d[o++] = BinaryPrimitives.ReverseEndianness(src[i + c]);
                        break;
                    }
                }
            }

            /// <summary>8 bit gray at 1, 2 or 4 bits when every sample is a multiple of 255, 85 or 17.</summary>
            void TryReduceGray()
            {
                bool fits1 = true, fits2 = true, fits4 = true;
                foreach (byte v in _bytes!)
                {
                    if (v % 17 != 0) return;
                    if (v % 85 != 0) { fits2 = false; fits1 = false; }
                    else if (v != 0 && v != 255) fits1 = false;
                }
                BitDepth = fits1 ? 1 : fits2 ? 2 : fits4 ? 4 : 8;
                GrayDivisor = fits1 ? 255 : fits2 ? 85 : 17;
                _kind = Kind.GrayReduce;
            }

            /// <summary>
            /// Drops the alpha channel in favor of a tRNS color key when alpha is binary, the transparent pixels share
            /// one color and no opaque pixel has it. Without any transparent pixel nothing changes: a key would have
            /// to be a color the image does not use, and dropping alpha silently would change the decoded color model.
            /// </summary>
            bool TryColorKey()
            {
                int ch = _sourceChannels, color = ch - 1;
                Span<int> key = stackalloc int[3];
                bool haveKey = false;
                long samples = (long)Width * Height * ch;
                int max = BitDepth == 16 ? 65535 : 255;
                for (int pass = 0; pass < 2; pass++)
                {
                    for (long i = 0; i < samples; i += ch)
                    {
                        int alpha = Sample(i + color);
                        if (alpha != 0 && alpha != max) return false;
                        bool isKey = haveKey;
                        for (int c = 0; c < color && isKey; c++) isKey = Sample(i + c) == key[c];
                        if (pass == 0)
                        {
                            if (alpha != 0) continue;
                            if (!haveKey) { for (int c = 0; c < color; c++) key[c] = Sample(i + c); haveKey = true; }
                            else if (!isKey) return false;      // two different transparent colors
                        }
                        else if (alpha != 0 && isKey) return false; // an opaque pixel of the key color would vanish
                    }
                    if (!haveKey) return false;
                }
                Trns = new byte[color * 2];
                for (int c = 0; c < color; c++) BinaryPrimitives.WriteUInt16BigEndian(Trns.AsSpan(c * 2), (ushort)key[c]);
                Channels = color;
                ColorType = color == 1 ? 0 : 2;
                _kind = BitDepth == 16 ? Kind.DropAlpha16 : Kind.DropAlpha8;
                return true;
            }

            int Sample(long i) => _bytes != null ? _bytes[i] : _words![i];

            /// <summary>Re-expresses an 8 bit Rgb/Rgba image as a palette of at most 256 colors plus indices.</summary>
            bool TryPalette()
            {
                int channels = _sourceChannels;
                long pixelCount = (long)Width * Height;
                if (pixelCount > Array.MaxLength) return false;
                var src = _bytes!;
                var indices = new byte[pixelCount];

                // Open addressing hash set of the colors seen so far (key = RGBA, value = palette index).
                const int TableSize = 1024;
                Span<uint> keys = stackalloc uint[TableSize];
                Span<short> slots = stackalloc short[TableSize];
                slots.Fill(-1);
                Span<uint> colors = stackalloc uint[256];
                int count = 0;
                uint lastKey = 0;
                int lastIndex = -1;

                for (int i = 0, s = 0; i < indices.Length; i++, s += channels)
                {
                    uint key = (uint)(src[s] | src[s + 1] << 8 | src[s + 2] << 16) | (channels == 4 ? (uint)src[s + 3] << 24 : 0xFF000000u);
                    if (key != lastKey || lastIndex < 0)
                    {
                        uint slot = (key * 0x9E3779B1u) >> 22;
                        while (true)
                        {
                            if (slots[(int)slot] < 0)
                            {
                                if (count == 256) return false;
                                keys[(int)slot] = key;
                                slots[(int)slot] = (short)count;
                                colors[count] = key;
                                lastIndex = count++;
                                break;
                            }
                            if (keys[(int)slot] == key) { lastIndex = slots[(int)slot]; break; }
                            slot = (slot + 1) & (TableSize - 1);
                        }
                        lastKey = key;
                    }
                    indices[i] = (byte)lastIndex;
                }

                // tRNS lists alpha for the first entries only, so translucent colors go to the front.
                Span<byte> remap = stackalloc byte[256];
                var rgb = new byte[count * 3];
                int next = 0, translucent = 0;
                for (int pass = 0; pass < 2; pass++)
                {
                    for (int c = 0; c < count; c++)
                    {
                        bool opaque = colors[c] >> 24 == 255;
                        if (opaque != (pass == 1)) continue;
                        remap[c] = (byte)next;
                        rgb[next * 3] = (byte)colors[c];
                        rgb[next * 3 + 1] = (byte)(colors[c] >> 8);
                        rgb[next * 3 + 2] = (byte)(colors[c] >> 16);
                        next++;
                    }
                    if (pass == 0) translucent = next;
                }
                // Rgba input always gets a tRNS chunk, so that it decodes as Rgba again.
                int alphaCount = channels == 4 ? Math.Max(translucent, 1) : 0;
                var alpha = new byte[alphaCount];
                for (int c = 0; c < count; c++)
                    if (remap[c] < alphaCount) alpha[remap[c]] = (byte)(colors[c] >> 24);
                for (int i = 0; i < indices.Length; i++) indices[i] = remap[indices[i]];

                Plte = rgb;
                Trns = alphaCount > 0 ? alpha : null;
                ColorType = 3;
                Channels = 1;
                BitDepth = count <= 2 ? 1 : count <= 4 ? 2 : count <= 16 ? 4 : 8;
                _bytes = indices;
                _sourceChannels = 1;
                _kind = Kind.Bytes;
                return true;
            }
        }

        // ------------------------------------------------------------------ pixel data

        /// <summary>
        /// Filters the region (x0, y0, w, h) of the source and writes the scanlines to the compressor; with
        /// <paramref name="interlace"/> as the seven Adam7 passes, each of which is filtered as an image of its own.
        /// </summary>
        static void WriteScanlines(Stream z, Source source, int x0, int y0, int w, int h, bool interlace, PngFilterMode mode)
        {
            int depth = source.BitDepth, channels = source.Channels;
            int unpacked = source.UnpackedPixelBytes;
            int rowBytes = (int)(((long)w * channels * depth + 7) >> 3);
            int bpp = Math.Max(1, channels * depth >> 3);

            // cur/prev hold the raw rows behind bpp zero bytes, so "the pixel to the left" is always readable.
            // Each candidate holds a filter type byte followed by the filtered row.
            int candidateCount = mode == PngFilterMode.Adaptive ? 5 : 1;
            int stride = 1 + rowBytes;
            var rows = new byte[2 * (bpp + rowBytes)];
            var candidates = new byte[candidateCount * stride];
            // Sub-byte samples are packed after, and interlaced rows are picked from, a complete unpacked row.
            byte[]? full = interlace || depth < 8 ? new byte[w * unpacked] : null;
            byte[]? picked = interlace && depth < 8 ? new byte[w] : null;

            fixed (byte* rowsP = rows, cand = candidates)
            {
                byte* cur = rowsP + bpp, prev = rowsP + bpp + rowBytes + bpp;
                for (int pass = 0; pass < (interlace ? 7 : 1); pass++)
                {
                    int px0 = 0, py0 = 0, dx = 1, dy = 1;
                    if (interlace) { px0 = PngDecoder.PassX0[pass]; py0 = PngDecoder.PassY0[pass]; dx = PngDecoder.PassDx[pass]; dy = PngDecoder.PassDy[pass]; }
                    int pw = (w - px0 + dx - 1) / dx, ph = (h - py0 + dy - 1) / dy;
                    if (pw <= 0 || ph <= 0) continue;
                    int n = (int)(((long)pw * channels * depth + 7) >> 3);
                    new Span<byte>(prev, n).Clear(); // every pass starts without a row above

                    for (int r = 0; r < ph; r++)
                    {
                        int y = y0 + py0 + r * dy;
                        var curSpan = new Span<byte>(cur, n);
                        if (full == null) source.FillRow(y, x0, w, curSpan);
                        else
                        {
                            source.FillRow(y, x0, w, full);
                            ReadOnlySpan<byte> samples = full;
                            if (interlace)
                            {
                                var target = depth < 8 ? picked.AsSpan(0, pw) : curSpan;
                                for (int i = 0, s = px0 * unpacked; i < pw; i++, s += dx * unpacked)
                                    full.AsSpan(s, unpacked).CopyTo(target.Slice(i * unpacked));
                                samples = target;
                            }
                            if (depth < 8)
                            {
                                curSpan.Clear();
                                for (int i = 0; i < pw; i++)
                                    cur[i * depth >> 3] |= (byte)(samples[i] << (8 - depth - (i * depth & 7)));
                            }
                        }

                        byte* best = cand;
                        if (mode == PngFilterMode.Adaptive)
                        {
                            long bestSum = PngFilters.SumAbs(cur, n);
                            int bestType = 0;
                            long s;
                            if ((s = PngFilters.FilterSub(cur, cand + stride + 1, n, bpp)) < bestSum) { bestSum = s; bestType = 1; }
                            if ((s = PngFilters.FilterUp(cur, prev, cand + 2 * stride + 1, n)) < bestSum) { bestSum = s; bestType = 2; }
                            if ((s = PngFilters.FilterAverage(cur, prev, cand + 3 * stride + 1, n, bpp)) < bestSum) { bestSum = s; bestType = 3; }
                            if ((s = PngFilters.FilterPaeth(cur, prev, cand + 4 * stride + 1, n, bpp)) < bestSum) { bestSum = s; bestType = 4; }
                            best = cand + bestType * stride;
                            if (bestType == 0) Buffer.MemoryCopy(cur, best + 1, n, n);
                            *best = (byte)bestType;
                        }
                        else
                        {
                            switch (mode)
                            {
                                case PngFilterMode.Sub: *best = 1; PngFilters.FilterSub(cur, best + 1, n, bpp); break;
                                case PngFilterMode.Up: *best = 2; PngFilters.FilterUp(cur, prev, best + 1, n); break;
                                case PngFilterMode.Average: *best = 3; PngFilters.FilterAverage(cur, prev, best + 1, n, bpp); break;
                                case PngFilterMode.Paeth: *best = 4; PngFilters.FilterPaeth(cur, prev, best + 1, n, bpp); break;
                                default: *best = 0; Buffer.MemoryCopy(cur, best + 1, n, n); break;
                            }
                        }
                        z.Write(new ReadOnlySpan<byte>(best, 1 + n));

                        byte* t = cur; cur = prev; prev = t;
                    }
                }
            }
        }

        // ------------------------------------------------------------------ chunks

        static void WriteChunk(Stream output, uint type, ReadOnlySpan<byte> body)
        {
            Span<byte> head = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(head, (uint)body.Length);
            BinaryPrimitives.WriteUInt32BigEndian(head.Slice(4), type);
            output.Write(head);
            output.Write(body);
            uint crc = Zlib.Crc32(body, Zlib.Crc32(head.Slice(4)));
            BinaryPrimitives.WriteUInt32BigEndian(head, crc);
            output.Write(head.Slice(0, 4));
        }

        /// <summary>Signature, IHDR and everything that precedes the pixel data.</summary>
        static void WriteHead(Source source, ImageMetadata? metadata, Stream output, PngEncodeOptions options, int level)
        {
            output.Write(PngChunks.Signature);

            Span<byte> ihdr = stackalloc byte[13];
            BinaryPrimitives.WriteInt32BigEndian(ihdr, source.Width);
            BinaryPrimitives.WriteInt32BigEndian(ihdr.Slice(4), source.Height);
            ihdr[8] = (byte)source.BitDepth;
            ihdr[9] = (byte)source.ColorType;
            ihdr[10] = 0; ihdr[11] = 0;
            ihdr[12] = options.Interlace ? (byte)1 : (byte)0;
            WriteChunk(output, PngChunks.IHDR, ihdr);

            var meta = options.WriteMetadata ? metadata : null;
            var png = options.Metadata;
            Span<byte> b = stackalloc byte[32];

            // Color space chunks have to precede PLTE.
            bool hasIcc = meta?.IccProfile is { Length: > 0 };
            if (png?.SrgbIntent is { } intent && !hasIcc) // sRGB and iCCP exclude each other; the profile wins
            {
                b[0] = (byte)intent;
                WriteChunk(output, PngChunks.sRGB, b.Slice(0, 1));
            }
            if (meta != null && meta.Gamma > 0 && !double.IsInfinity(meta.Gamma))
            {
                BinaryPrimitives.WriteUInt32BigEndian(b, (uint)Math.Clamp(Math.Round(meta.Gamma * 100000.0), 1, int.MaxValue));
                WriteChunk(output, PngChunks.gAMA, b.Slice(0, 4));
            }
            if (png?.Chromaticities is { } c)
            {
                ReadOnlySpan<double> v = stackalloc double[] { c.WhiteX, c.WhiteY, c.RedX, c.RedY, c.GreenX, c.GreenY, c.BlueX, c.BlueY };
                for (int i = 0; i < 8; i++)
                {
                    if (!(v[i] >= 0 && v[i] <= 21474.83647)) throw new ArgumentOutOfRangeException(nameof(options), "chromaticity out of range");
                    BinaryPrimitives.WriteUInt32BigEndian(b.Slice(i * 4), (uint)Math.Round(v[i] * 100000.0));
                }
                WriteChunk(output, PngChunks.cHRM, b);
            }
            if (hasIcc)
            {
                var name = "ICC Profile"u8;
                var z = Zlib.Compress(meta!.IccProfile, Math.Max(level, 1));
                var body = new byte[name.Length + 2 + z.Length];
                name.CopyTo(body);
                z.CopyTo(body.AsSpan(name.Length + 2)); // name, 0 terminator, compression method 0
                WriteChunk(output, PngChunks.iCCP, body);
            }
            if (png?.SignificantBits is { } bits)
            {
                // The entries follow the stored color type, which may have lost its alpha channel on the way.
                int needed = PngChunks.SignificantBitsCount(source.ColorType);
                if (bits.Length == needed || bits.Length == needed + 1)
                {
                    int maxBits = source.ColorType == 3 ? 8 : source.BitDepth;
                    for (int i = 0; i < needed; i++) b[i] = (byte)Math.Clamp((int)bits[i], 1, maxBits);
                    WriteChunk(output, PngChunks.sBIT, b.Slice(0, needed));
                }
            }

            if (source.Plte != null) WriteChunk(output, PngChunks.PLTE, source.Plte);
            if (source.Trns != null) WriteChunk(output, PngChunks.tRNS, source.Trns);

            if (png?.Background is { } bg) WriteBackground(output, source, bg);
            if (meta != null)
            {
                double dx = meta.DpiX > 0 ? meta.DpiX : meta.DpiY, dy = meta.DpiY > 0 ? meta.DpiY : meta.DpiX;
                if (dx > 0 && dy > 0 && !double.IsInfinity(dx) && !double.IsInfinity(dy))
                {
                    BinaryPrimitives.WriteUInt32BigEndian(b, (uint)Math.Clamp(Math.Round(dx / 0.0254), 1, int.MaxValue));
                    BinaryPrimitives.WriteUInt32BigEndian(b.Slice(4), (uint)Math.Clamp(Math.Round(dy / 0.0254), 1, int.MaxValue));
                    b[8] = 1; // unit: meter
                    WriteChunk(output, PngChunks.pHYs, b.Slice(0, 9));
                }
            }
            if (png?.LastModified is { } time)
            {
                var utc = time.Kind == DateTimeKind.Local ? time.ToUniversalTime() : time;
                BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)utc.Year);
                b[2] = (byte)utc.Month; b[3] = (byte)utc.Day; b[4] = (byte)utc.Hour; b[5] = (byte)utc.Minute; b[6] = (byte)utc.Second;
                WriteChunk(output, PngChunks.tIME, b.Slice(0, 7));
            }
            if (png != null)
                foreach (var entry in png.Text) WriteText(output, entry, level);
            if (meta != null)
            {
                if (meta.Exif is { Length: > 0 } exif)
                {
                    WriteChunk(output, PngChunks.eXIf, exif);
                }
                else if (meta.Orientation is >= 1 and <= 8)
                {
                    // Minimal EXIF: big endian TIFF header and one IFD holding just the orientation tag.
                    ReadOnlySpan<byte> tiff = stackalloc byte[]
                    {
                        (byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8,
                        0, 1,
                        0x01, 0x12, 0, 3, 0, 0, 0, 1, 0, (byte)meta.Orientation, 0, 0,
                        0, 0, 0, 0,
                    };
                    WriteChunk(output, PngChunks.eXIf, tiff);
                }
            }
        }

        static void WriteBackground(Stream output, Source source, PngBackground bg)
        {
            Span<byte> b = stackalloc byte[6];
            int max = (1 << source.BitDepth) - 1;
            switch (source.ColorType)
            {
                case 3:
                {
                    // the caller's index for a caller supplied palette, otherwise whichever entry has the color
                    var plte = source.Plte!;
                    int index = -1;
                    if (source.ExplicitPalette && bg.PaletteIndex >= 0 && bg.PaletteIndex < plte.Length / 3) index = bg.PaletteIndex;
                    for (int i = 0; index < 0 && i < plte.Length; i += 3)
                        if (plte[i] == bg.R && plte[i + 1] == bg.G && plte[i + 2] == bg.B) index = i / 3;
                    if (index < 0) return;
                    b[0] = (byte)index;
                    WriteChunk(output, PngChunks.bKGD, b.Slice(0, 1));
                    break;
                }
                case 0:
                case 4:
                    BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)Math.Min(max, (bg.R + source.GrayDivisor / 2) / source.GrayDivisor));
                    WriteChunk(output, PngChunks.bKGD, b.Slice(0, 2));
                    break;
                default:
                    BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)Math.Min(max, (int)bg.R));
                    BinaryPrimitives.WriteUInt16BigEndian(b.Slice(2), (ushort)Math.Min(max, (int)bg.G));
                    BinaryPrimitives.WriteUInt16BigEndian(b.Slice(4), (ushort)Math.Min(max, (int)bg.B));
                    WriteChunk(output, PngChunks.bKGD, b);
                    break;
            }
        }

        static void WriteText(Stream output, PngTextEntry entry, int level)
        {
            string key = entry.Keyword ?? "", text = entry.Text ?? "";
            bool keyOk = key.Length is >= 1 and <= 79 && key[0] != ' ' && key[^1] != ' ' && !key.Contains("  ");
            foreach (char ch in key) keyOk &= ch is >= ' ' and <= '~' or >= '¡' and <= 'ÿ';
            if (!keyOk) throw new ArgumentException($"invalid PNG text keyword '{key}' (1..79 printable Latin-1 characters, no leading, trailing or double spaces)");
            if (text.Contains('\0')) throw new ArgumentException("PNG text must not contain NUL characters");

            bool international = entry.International || entry.LanguageTag.Length > 0 || entry.TranslatedKeyword.Length > 0;
            foreach (char ch in text) international |= ch > 'ÿ';

            using var body = new MemoryStream();
            body.Write(Encoding.Latin1.GetBytes(key));
            body.WriteByte(0);
            if (!international)
            {
                var raw = Encoding.Latin1.GetBytes(text);
                if (entry.Compressed) { body.WriteByte(0); raw = Zlib.Compress(raw, Math.Max(level, 1)); }
                body.Write(raw);
                WriteChunk(output, entry.Compressed ? PngChunks.zTXt : PngChunks.tEXt, body.GetBuffer().AsSpan(0, (int)body.Length));
                return;
            }
            foreach (char ch in entry.LanguageTag)
                if (ch is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-'))
                    throw new ArgumentException($"invalid PNG text language tag '{entry.LanguageTag}'");
            if (entry.TranslatedKeyword.Contains('\0')) throw new ArgumentException("PNG text must not contain NUL characters");
            body.WriteByte(entry.Compressed ? (byte)1 : (byte)0);
            body.WriteByte(0);
            body.Write(Encoding.ASCII.GetBytes(entry.LanguageTag));
            body.WriteByte(0);
            body.Write(Encoding.UTF8.GetBytes(entry.TranslatedKeyword));
            body.WriteByte(0);
            var utf8 = Encoding.UTF8.GetBytes(text);
            body.Write(entry.Compressed ? Zlib.Compress(utf8, Math.Max(level, 1)) : utf8);
            WriteChunk(output, PngChunks.iTXt, body.GetBuffer().AsSpan(0, (int)body.Length));
        }

        /// <summary>Running APNG sequence number shared by the fcTL and fdAT chunks of a file.</summary>
        sealed class Sequence
        {
            public uint Next;
        }

        /// <summary>
        /// Collects the compressed stream and emits it as IDAT chunks of a fixed size, or as fdAT chunks, each of
        /// which starts with the next sequence number.
        /// </summary>
        sealed class DataStream : Stream
        {
            readonly Stream _output;
            readonly uint _type;
            readonly Sequence? _sequence;
            readonly int _prefix;
            byte[]? _buffer = ArrayPool<byte>.Shared.Rent(DataChunkSize);
            int _count;

            public DataStream(Stream output, uint type, Sequence? sequence)
            {
                _output = output; _type = type; _sequence = sequence;
                _prefix = sequence != null ? 4 : 0;
                _count = _prefix;
            }

            public override void Write(ReadOnlySpan<byte> data)
            {
                var buffer = _buffer ?? throw new ObjectDisposedException(nameof(DataStream));
                while (!data.IsEmpty)
                {
                    int n = Math.Min(data.Length, DataChunkSize - _count);
                    data.Slice(0, n).CopyTo(buffer.AsSpan(_count));
                    _count += n;
                    data = data.Slice(n);
                    if (_count == DataChunkSize) Emit();
                }
            }

            public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
            public override void WriteByte(byte value) => Write(new ReadOnlySpan<byte>(in value));

            void Emit()
            {
                if (_sequence != null) BinaryPrimitives.WriteUInt32BigEndian(_buffer, _sequence.Next++);
                WriteChunk(_output, _type, _buffer.AsSpan(0, _count));
                _count = _prefix;
            }

            /// <summary>Writes the remaining data (call after the compressor has been closed).</summary>
            public void Finish()
            {
                if (_count > _prefix) Emit();
            }

            protected override void Dispose(bool disposing)
            {
                if (_buffer != null) { ArrayPool<byte>.Shared.Return(_buffer); _buffer = null; }
                base.Dispose(disposing);
            }

            public override void Flush() { } // chunks are cut by size only
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
