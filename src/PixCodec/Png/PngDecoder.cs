using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using PixCodec.Compression;

namespace PixCodec
{
    internal static unsafe class PngDecoder
    {
        // Adam7: start offset and step of each of the seven passes.
        public static ReadOnlySpan<byte> PassX0 => new byte[] { 0, 4, 0, 2, 0, 1, 0 };
        public static ReadOnlySpan<byte> PassY0 => new byte[] { 0, 0, 4, 0, 2, 0, 1 };
        public static ReadOnlySpan<byte> PassDx => new byte[] { 8, 8, 4, 4, 2, 2, 1 };
        public static ReadOnlySpan<byte> PassDy => new byte[] { 8, 8, 8, 4, 4, 2, 2 };

        static void OutputFormat(scoped in PngHeader h, bool hasTransparency, bool keepIndexed, out ColorModel color, out SampleType type)
        {
            type = h.BitDepth == 16 ? SampleType.UInt16 : SampleType.UInt8;
            color = h.ColorType switch
            {
                0 => hasTransparency ? ColorModel.GrayAlpha : ColorModel.Gray,
                2 => hasTransparency ? ColorModel.Rgba : ColorModel.Rgb,
                3 => keepIndexed ? ColorModel.Gray : hasTransparency ? ColorModel.Rgba : ColorModel.Rgb,
                4 => ColorModel.GrayAlpha,
                _ => ColorModel.Rgba,
            };
        }

        public static ImageInfo GetInfo(ReadOnlySpan<byte> data)
        {
            var h = PngChunks.ReadHeader(data, verifyCrc: false);
            PngChunks.ScanHead(data, h, out _, out var trns, out int frames);
            OutputFormat(h, !trns.IsEmpty, false, out var color, out var type);
            return new ImageInfo(ImageFormat.Png, h.Width, h.Height, RawImage.ChannelCount(color), type, color, frames);
        }

        public static byte[]? ReadPalette(ReadOnlySpan<byte> data)
        {
            var h = PngChunks.ReadHeader(data, verifyCrc: false);
            if (!h.IsPalette) return null;
            PngChunks.ScanHead(data, h, out var plte, out var trns, out _);
            int count = Math.Min(plte.Length / 3, 256);
            if (count == 0) return null;
            var table = new byte[count * 4];
            FillPalette(table, plte, trns);
            return table;
        }

        /// <summary>Writes RGBA entries; entries beyond the palette stay untouched.</summary>
        static void FillPalette(Span<byte> rgba, ReadOnlySpan<byte> plte, ReadOnlySpan<byte> trns)
        {
            int count = Math.Min(Math.Min(plte.Length / 3, 256), rgba.Length / 4);
            for (int i = 0; i < count; i++)
            {
                rgba[i * 4] = plte[i * 3];
                rgba[i * 4 + 1] = plte[i * 3 + 1];
                rgba[i * 4 + 2] = plte[i * 3 + 2];
                rgba[i * 4 + 3] = i < trns.Length ? trns[i] : (byte)255;
            }
        }

        // ------------------------------------------------------------------ decode

        public static RawImage Decode(ReadOnlySpan<byte> data, PngDecodeOptions options)
        {
            var s = PngChunks.Parse(data, options, PngChunks.ParseFlags.None);
            var image = DecodeImage(data, s, null, options);
            image.Metadata = s.Meta;
            return image;
        }

        /// <summary>
        /// Decodes one PNG datastream of the file: the default image (<paramref name="frame"/> null or an IDAT
        /// frame), or an animation frame, which is a stream of its own with the frame's size that shares everything
        /// else (bit depth, color type, palette, transparency) with the file.
        /// </summary>
        public static RawImage DecodeImage(ReadOnlySpan<byte> data, PngStructure s, PngFrameEntry? frame, PngDecodeOptions options)
        {
            var h = s.Header;
            bool fromIdat = frame == null || frame.UsesIdat;
            if (frame != null) { h.Width = frame.Width; h.Height = frame.Height; }
            long streamTotal = fromIdat ? s.IdatTotal : frame!.Total;
            int streamCount = fromIdat ? s.IdatCount : frame!.Data.Count;
            var plte = s.Plte(data);
            var trns = options.IgnoreTransparency ? default : s.Trns(data);

            long rawSize = InflatedSize(h, streamTotal);

            bool keepIndexed = options.KeepIndexed && h.IsPalette;
            OutputFormat(h, !trns.IsEmpty, keepIndexed, out var color, out var sampleType);
            int outChannels = RawImage.ChannelCount(color);
            long outSamples = (long)h.Width * h.Height * outChannels;
            if (outSamples * (h.BitDepth == 16 ? 2 : 1) > int.MaxValue || outSamples > Array.MaxLength || rawSize >= Array.MaxLength)
                throw new ImageNotSupportedException("PNG image is too large (more than 2 GB of pixel data)");

            RawImage image;
            byte[]? joined = null;
            byte[] raw = ArrayPool<byte>.Shared.Rent((int)rawSize + 1);
            try
            {
                ReadOnlySpan<byte> stream;
                if (streamCount == 1)
                {
                    if (fromIdat) PngChunks.ReadChunk(data, s.FirstIdat, out _, out stream);
                    else stream = data.Slice(frame!.Data[0].Offset, frame.Data[0].Length);
                }
                else
                {
                    joined = ArrayPool<byte>.Shared.Rent((int)streamTotal);
                    int n = 0;
                    if (fromIdat)
                    {
                        for (int p = s.FirstIdat, i = 0; i < streamCount; i++)
                        {
                            PngChunks.ReadChunk(data, p, out _, out var body);
                            body.CopyTo(joined.AsSpan(n));
                            n += body.Length;
                            p += 12 + body.Length;
                        }
                    }
                    else
                    {
                        foreach (var (offset, length) in frame!.Data)
                        {
                            data.Slice(offset, length).CopyTo(joined.AsSpan(n));
                            n += length;
                        }
                    }
                    stream = joined.AsSpan(0, n);
                }

                // The zlib wrapper is handled here and the payload inflated as raw deflate: that way the Adler-32 is
                // checked (or deliberately not checked) by this code, with a vectorized sum, whether or not the
                // inflater would have reached the trailer on its own.
                if (stream.Length < 2 || (stream[0] & 0x0F) != 8 || stream[0] >> 4 > 7 || (stream[0] << 8 | stream[1]) % 31 != 0 || (stream[1] & 0x20) != 0)
                    throw new ImageFormatException("corrupt PNG pixel data (invalid zlib header)");
                int got;
                try
                {
                    // one byte more than needed: a stream that fills it holds surplus data
                    got = Zlib.DecompressRaw(stream.Slice(2), raw.AsSpan(0, (int)rawSize + 1));
                }
                catch (Exception e) when (e is InvalidDataException or IOException)
                {
                    throw new ImageFormatException("corrupt PNG pixel data", e);
                }
                if (got < rawSize) throw new ImageFormatException("PNG pixel data is truncated");
                // Surplus pixel data (got > rawSize) is tolerated and cannot be checked: the sum covers all of it.
                if (options.VerifyChecksums && got == rawSize
                    && (stream.Length < 6 || Adler32(raw.AsSpan(0, (int)rawSize)) != BinaryPrimitives.ReadUInt32BigEndian(stream.Slice(stream.Length - 4))))
                    throw new ImageFormatException("PNG pixel data checksum (Adler-32) mismatch");

                // The output is only allocated once the stream has proven to really hold that many pixels.
                image = RawImage.Create(h.Width, h.Height, color, sampleType);
                var conv = new RowConverter(h, trns, plte, keepIndexed, outChannels, options.AllowInvalidPaletteIndices);
                Reconstruct(h, raw, image, conv);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(raw);
                if (joined != null) ArrayPool<byte>.Shared.Return(joined);
            }
            return image;
        }

        /// <summary>
        /// Size of the inflated stream of an image: every row is preceded by a filter byte; interlaced images are
        /// seven sub images. Deflate cannot expand by more than 1032:1, so a header promising more than
        /// <paramref name="streamLength"/> compressed bytes can hold is rejected here, before anything is allocated.
        /// </summary>
        public static long InflatedSize(scoped in PngHeader h, long streamLength)
        {
            int bitsPerPixel = h.BitsPerPixel;
            long rawSize = 0;
            if (h.Interlaced)
            {
                for (int p = 0; p < 7; p++)
                {
                    PassSize(h, p, out int pw, out int ph);
                    if (pw > 0 && ph > 0) rawSize += ph * (1 + (((long)pw * bitsPerPixel + 7) >> 3));
                }
            }
            else rawSize = h.Height * ((((long)h.Width * bitsPerPixel + 7) >> 3) + 1);
            if (rawSize > streamLength * 1032 + 1032) throw new ImageFormatException("PNG pixel data is truncated");
            return rawSize;
        }

        /// <summary>Adler-32 of the inflated data, 16 bytes per step where SSSE3 is available.</summary>
        internal static uint Adler32(ReadOnlySpan<byte> data)
        {
            if (!Ssse3.IsSupported || data.Length < 64) return Zlib.Adler32(data);
            const uint Mod = 65521;
            uint a = 1, b = 0;
            int done = 0;
            fixed (byte* start = data)
            {
                byte* p = start;
                var weights = Vector128.Create((sbyte)16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1);
                var ones = Vector128.Create((short)1);
                int blocks = data.Length / 16;
                while (blocks > 0)
                {
                    // b grows by a for every byte: after n bytes b += n * a0 + sum((n - i) * d[i]). Per 16 byte step
                    // that splits into 16 * (sum of all earlier steps) + the weighted sum of the step itself.
                    // 346 steps (5536 bytes) keep every 32 bit lane from overflowing.
                    int n = Math.Min(blocks, 346);
                    var sum = Vector128<uint>.Zero;
                    var earlier = Vector128<uint>.Zero;
                    var weighted = Vector128<uint>.Zero;
                    for (int k = 0; k < n; k++, p += 16)
                    {
                        var v = Sse2.LoadVector128(p);
                        earlier += sum;
                        sum += Sse2.SumAbsoluteDifferences(v, Vector128<byte>.Zero).AsUInt32();
                        weighted += Sse2.MultiplyAddAdjacent(Ssse3.MultiplyAddAdjacent(v, weights), ones).AsUInt32();
                    }
                    ulong nb = b + (ulong)(n * 16) * a + 16UL * Vector128.Sum(earlier) + Vector128.Sum(weighted);
                    a = (a + Vector128.Sum(sum)) % Mod;
                    b = (uint)(nb % Mod);
                    blocks -= n;
                    done += n * 16;
                }
            }
            return Zlib.Adler32(data.Slice(done), b << 16 | a);
        }

        public static void PassSize(scoped in PngHeader h, int pass, out int width, out int height)
        {
            width = (h.Width - PassX0[pass] + PassDx[pass] - 1) / PassDx[pass];
            height = (h.Height - PassY0[pass] + PassDy[pass] - 1) / PassDy[pass];
        }

        static void Reconstruct(scoped in PngHeader h, byte[] rawArray, RawImage image, RowConverter conv)
        {
            int width = h.Width, height = h.Height;
            int bitsPerPixel = h.BitsPerPixel;
            int filterBpp = Math.Max(1, bitsPerPixel >> 3);
            int rowBytes = (int)(((long)width * bitsPerPixel + 7) >> 3);
            int outPixelBytes = image.Channels * RawImage.BytesPerSample(image.SampleType);
            bool direct = conv.IsDirect;

            // Two scratch rows (current / previous) for everything that cannot be reconstructed straight into
            // the output, plus one row of converted pixels for interlaced images.
            byte[]? rowA = null, rowB = null, expanded = null;
            if (!direct || h.Interlaced)
            {
                rowA = ArrayPool<byte>.Shared.Rent(rowBytes);
                rowB = ArrayPool<byte>.Shared.Rent(rowBytes);
            }
            if (h.Interlaced && !direct) expanded = ArrayPool<byte>.Shared.Rent(checked(width * outPixelBytes));

            try
            {
                fixed (byte* raw = rawArray, output = image.GetBytes(), scratchA = rowA, scratchB = rowB, exp = expanded)
                {
                    byte* src = raw;
                    if (!h.Interlaced)
                    {
                        if (direct)
                        {
                            byte* prev = null, dst = output;
                            for (int y = 0; y < height; y++)
                            {
                                if (!PngFilters.Unfilter(*src, src + 1, prev, dst, rowBytes, filterBpp)) throw BadFilter();
                                prev = dst;
                                dst += rowBytes;
                                src += rowBytes + 1;
                            }
                        }
                        else
                        {
                            byte* prev = null, cur = scratchA, other = scratchB, dst = output;
                            nint outRowBytes = (nint)width * outPixelBytes;
                            for (int y = 0; y < height; y++)
                            {
                                if (!PngFilters.Unfilter(*src, src + 1, prev, cur, rowBytes, filterBpp)) throw BadFilter();
                                conv.Convert(cur, dst, width);
                                prev = cur; cur = other; other = prev;
                                dst += outRowBytes;
                                src += rowBytes + 1;
                            }
                        }
                        return;
                    }

                    for (int pass = 0; pass < 7; pass++)
                    {
                        PassSize(h, pass, out int pw, out int ph);
                        if (pw <= 0 || ph <= 0) continue;
                        int passRowBytes = (int)(((long)pw * bitsPerPixel + 7) >> 3);
                        int x0 = PassX0[pass], dx = PassDx[pass], dy = PassDy[pass];
                        byte* prev = null, cur = scratchA, other = scratchB;
                        for (int r = 0; r < ph; r++)
                        {
                            if (!PngFilters.Unfilter(*src, src + 1, prev, cur, passRowBytes, filterBpp)) throw BadFilter();
                            byte* pixels = cur;
                            if (!direct) { conv.Convert(cur, exp, pw); pixels = exp; }
                            int y = PassY0[pass] + r * dy;
                            Scatter(pixels, output + ((nint)y * width + x0) * outPixelBytes, pw, outPixelBytes, dx * outPixelBytes);
                            prev = cur; cur = other; other = prev;
                            src += passRowBytes + 1;
                        }
                    }
                }
            }
            finally
            {
                if (rowA != null) ArrayPool<byte>.Shared.Return(rowA);
                if (rowB != null) ArrayPool<byte>.Shared.Return(rowB);
                if (expanded != null) ArrayPool<byte>.Shared.Return(expanded);
            }
        }

        static ImageFormatException BadFilter() => new ImageFormatException("invalid PNG row filter type");

        /// <summary>Copies <paramref name="count"/> pixels of <paramref name="size"/> bytes to every <paramref name="step"/>-th byte position.</summary>
        static void Scatter(byte* src, byte* dst, int count, int size, int step)
        {
            switch (size)
            {
                case 1:
                    for (int i = 0; i < count; i++, dst += step) *dst = src[i];
                    break;
                case 2:
                    for (int i = 0; i < count; i++, dst += step) Unsafe.WriteUnaligned(dst, Unsafe.ReadUnaligned<ushort>(src + i * 2));
                    break;
                case 4:
                    for (int i = 0; i < count; i++, dst += step) Unsafe.WriteUnaligned(dst, Unsafe.ReadUnaligned<uint>(src + i * 4));
                    break;
                case 8:
                    for (int i = 0; i < count; i++, dst += step) Unsafe.WriteUnaligned(dst, Unsafe.ReadUnaligned<ulong>(src + i * 8));
                    break;
                default:
                    for (int i = 0; i < count; i++, dst += step, src += size)
                        for (int k = 0; k < size; k++) dst[k] = src[k];
                    break;
            }
        }

        /// <summary>Turns one reconstructed PNG row (packed, big endian) into output samples.</summary>
        sealed class RowConverter
        {
            enum Kind { Direct, Swap16, Unpack, Indices8, GrayKey8, GrayKey16, UnpackGrayKey, RgbKey8, RgbKey16, Palette }

            readonly Kind _kind;
            readonly int _depth, _pngChannels, _scale;
            readonly int _key0, _key1, _key2;
            readonly bool _alpha;
            readonly int _indexLimit;    // palette size when indices have to be checked against it, else 0
            readonly byte[]? _palette;   // 256 RGBA entries
            readonly byte[]? _indices;   // unpacked low bit depth samples of one row

            /// <summary>The reconstructed bytes already are the output samples.</summary>
            public bool IsDirect => _kind == Kind.Direct;

            public RowConverter(scoped in PngHeader h, ReadOnlySpan<byte> trns, ReadOnlySpan<byte> plte, bool keepIndexed, int outChannels, bool allowInvalidIndices)
            {
                _depth = h.BitDepth;
                _pngChannels = h.PngChannels;
                _scale = 1;
                bool keyed = !trns.IsEmpty && !h.IsPalette;
                if (keyed)
                {
                    // Only the low bits of the 16 bit key values are significant below 16 bit depth.
                    int mask = (1 << _depth) - 1;
                    _key0 = BinaryPrimitives.ReadUInt16BigEndian(trns) & mask;
                    if (h.ColorType == 2)
                    {
                        _key1 = BinaryPrimitives.ReadUInt16BigEndian(trns.Slice(2)) & mask;
                        _key2 = BinaryPrimitives.ReadUInt16BigEndian(trns.Slice(4)) & mask;
                    }
                }

                if (h.IsPalette)
                {
                    int count = Math.Min(plte.Length / 3, 256);
                    if (!allowInvalidIndices && count < 1 << _depth) _indexLimit = count;
                    if (keepIndexed) _kind = _depth < 8 ? Kind.Unpack : _indexLimit > 0 ? Kind.Indices8 : Kind.Direct;
                    else
                    {
                        _kind = Kind.Palette;
                        _alpha = outChannels == 4;
                        // Indices beyond the palette, where tolerated, decode as opaque black.
                        _palette = new byte[1024];
                        for (int i = 3; i < 1024; i += 4) _palette[i] = 255;
                        FillPalette(_palette, plte, trns);
                    }
                }
                else if (_depth == 16)
                    _kind = !keyed ? Kind.Swap16 : h.ColorType == 0 ? Kind.GrayKey16 : Kind.RgbKey16;
                else if (_depth == 8)
                    _kind = !keyed ? Kind.Direct : h.ColorType == 0 ? Kind.GrayKey8 : Kind.RgbKey8;
                else
                {
                    // 1, 2, 4 bit gray: replicate the bits to fill a byte.
                    _scale = 255 / ((1 << _depth) - 1);
                    _kind = keyed ? Kind.UnpackGrayKey : Kind.Unpack;
                }

                if (_depth < 8 && _kind is Kind.Palette or Kind.UnpackGrayKey) _indices = new byte[h.Width];
            }

            public void Convert(byte* src, byte* dst, int width)
            {
                switch (_kind)
                {
                    case Kind.Direct:
                    {
                        long n = (long)width * _pngChannels * (_depth >> 3);
                        Buffer.MemoryCopy(src, dst, n, n);
                        break;
                    }
                    case Kind.Swap16:
                    {
                        int n = width * _pngChannels;
                        BinaryPrimitives.ReverseEndianness(new ReadOnlySpan<ushort>(src, n), new Span<ushort>(dst, n));
                        break;
                    }
                    case Kind.Unpack:
                        Unpack(src, dst, width, _depth, _scale);
                        if (_indexLimit > 0) CheckIndices(dst, width);
                        break;
                    case Kind.Indices8:
                        CheckIndices(src, width);
                        Buffer.MemoryCopy(src, dst, width, width);
                        break;
                    case Kind.GrayKey8:
                        for (int x = 0; x < width; x++)
                        {
                            int v = src[x];
                            dst[2 * x] = (byte)v;
                            dst[2 * x + 1] = v == _key0 ? (byte)0 : (byte)255;
                        }
                        break;
                    case Kind.UnpackGrayKey:
                        fixed (byte* idx = _indices)
                        {
                            Unpack(src, idx, width, _depth, 1);
                            for (int x = 0; x < width; x++)
                            {
                                int v = idx[x];
                                dst[2 * x] = (byte)(v * _scale);
                                dst[2 * x + 1] = v == _key0 ? (byte)0 : (byte)255;
                            }
                        }
                        break;
                    case Kind.GrayKey16:
                    {
                        var d = (ushort*)dst;
                        for (int x = 0; x < width; x++)
                        {
                            int v = src[2 * x] << 8 | src[2 * x + 1];
                            d[2 * x] = (ushort)v;
                            d[2 * x + 1] = v == _key0 ? (ushort)0 : (ushort)65535;
                        }
                        break;
                    }
                    case Kind.RgbKey8:
                        for (int x = 0; x < width; x++, src += 3, dst += 4)
                        {
                            int r = src[0], g = src[1], b = src[2];
                            dst[0] = (byte)r; dst[1] = (byte)g; dst[2] = (byte)b;
                            dst[3] = r == _key0 && g == _key1 && b == _key2 ? (byte)0 : (byte)255;
                        }
                        break;
                    case Kind.RgbKey16:
                    {
                        var d = (ushort*)dst;
                        for (int x = 0; x < width; x++, src += 6, d += 4)
                        {
                            int r = src[0] << 8 | src[1], g = src[2] << 8 | src[3], b = src[4] << 8 | src[5];
                            d[0] = (ushort)r; d[1] = (ushort)g; d[2] = (ushort)b;
                            d[3] = r == _key0 && g == _key1 && b == _key2 ? (ushort)0 : (ushort)65535;
                        }
                        break;
                    }
                    case Kind.Palette:
                        fixed (byte* idx = _indices, pal = _palette)
                        {
                            byte* index = src;
                            if (_depth < 8) { Unpack(src, idx, width, _depth, 1); index = idx; }
                            if (_indexLimit > 0) CheckIndices(index, width);
                            if (_alpha)
                            {
                                for (int x = 0; x < width; x++)
                                    Unsafe.WriteUnaligned(dst + 4 * x, Unsafe.ReadUnaligned<uint>(pal + 4 * index[x]));
                            }
                            else
                            {
                                // 4 byte stores advancing by 3; the last pixel must not write past the row.
                                int x = 0;
                                for (; x < width - 1; x++, dst += 3)
                                    Unsafe.WriteUnaligned(dst, Unsafe.ReadUnaligned<uint>(pal + 4 * index[x]));
                                byte* e = pal + 4 * index[x];
                                dst[0] = e[0]; dst[1] = e[1]; dst[2] = e[2];
                            }
                        }
                        break;
                }
            }

            void CheckIndices(byte* index, int width)
            {
                int max = 0, x = 0;
                if (Vector.IsHardwareAccelerated && width >= Vector<byte>.Count)
                {
                    var m = Vector<byte>.Zero;
                    for (; x + Vector<byte>.Count <= width; x += Vector<byte>.Count)
                        m = Vector.Max(m, Unsafe.ReadUnaligned<Vector<byte>>(index + x));
                    for (int i = 0; i < Vector<byte>.Count; i++) max = Math.Max(max, m[i]);
                }
                for (; x < width; x++) max = Math.Max(max, index[x]);
                if (max >= _indexLimit) throw new ImageFormatException($"PNG pixel refers to palette entry {max} but the palette has {_indexLimit}");
            }

            /// <summary>Expands 1/2/4 bit samples (most significant bits first) to one byte each, multiplied by scale.</summary>
            static void Unpack(byte* src, byte* dst, int width, int depth, int scale)
            {
                int mask = (1 << depth) - 1;
                int perByte = 8 / depth;
                int x = 0;
                if (depth == 1)
                {
                    // Eight pixels per step: replicate the byte, keep bit 7-k in byte k, turn non zero into 1, scale.
                    for (; x + 8 <= width; x += 8)
                    {
                        ulong v = (*src++ * 0x0101010101010101UL) & 0x0102040810204080UL;
                        v = ((v + 0x7F7F7F7F7F7F7F7FUL) >> 7) & 0x0101010101010101UL;
                        Unsafe.WriteUnaligned(dst + x, v * (uint)scale);
                    }
                }
                for (int full = (width - x) / perByte; full > 0; full--)
                {
                    int b = *src++;
                    for (int s = 8 - depth; s >= 0; s -= depth) dst[x++] = (byte)(((b >> s) & mask) * scale);
                }
                if (x < width)
                {
                    int b = *src;
                    for (int s = 8 - depth; x < width; s -= depth) dst[x++] = (byte)(((b >> s) & mask) * scale);
                }
            }
        }

    }
}
