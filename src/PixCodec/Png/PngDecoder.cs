using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.CompilerServices;
using PixCodec.Compression;

namespace PixCodec
{
    internal static unsafe class PngDecoder
    {
        public static ReadOnlySpan<byte> Signature => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        public const uint IHDR = 0x49484452, PLTE = 0x504C5445, IDAT = 0x49444154, IEND = 0x49454E44,
                          tRNS = 0x74524E53, gAMA = 0x67414D41, pHYs = 0x70485973, iCCP = 0x69434350, eXIf = 0x65584966;

        const int FirstChunkAfterHeader = 8 + 12 + 13;

        // Adam7: start offset and step of each of the seven passes.
        static ReadOnlySpan<byte> PassX0 => new byte[] { 0, 4, 0, 2, 0, 1, 0 };
        static ReadOnlySpan<byte> PassY0 => new byte[] { 0, 0, 4, 0, 2, 0, 1 };
        static ReadOnlySpan<byte> PassDx => new byte[] { 8, 8, 4, 4, 2, 2, 1 };
        static ReadOnlySpan<byte> PassDy => new byte[] { 8, 8, 8, 4, 4, 2, 2 };

        struct Header
        {
            public int Width, Height, BitDepth, ColorType;
            public bool Interlaced;

            public int PngChannels => ColorType switch { 2 => 3, 4 => 2, 6 => 4, _ => 1 };
            public int BitsPerPixel => PngChannels * BitDepth;
            public bool IsPalette => ColorType == 3;
        }

        // ------------------------------------------------------------------ container

        static Header ReadHeader(ReadOnlySpan<byte> data, bool verifyCrc)
        {
            if (data.Length < 8 || !data.Slice(0, 8).SequenceEqual(Signature))
                throw new ImageFormatException("not a PNG file (bad signature)");
            if (data.Length < FirstChunkAfterHeader)
                throw new ImageFormatException("PNG header is truncated");
            if (BinaryPrimitives.ReadUInt32BigEndian(data.Slice(8)) != 13 || BinaryPrimitives.ReadUInt32BigEndian(data.Slice(12)) != IHDR)
                throw new ImageFormatException("PNG does not start with an IHDR chunk");
            if (verifyCrc && !CrcOk(data, 8, 13))
                throw new ImageFormatException("PNG IHDR checksum mismatch");

            var b = data.Slice(16, 13);
            uint w = BinaryPrimitives.ReadUInt32BigEndian(b), h = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(4));
            if (w == 0 || h == 0 || w > int.MaxValue || h > int.MaxValue)
                throw new ImageFormatException("invalid PNG image dimensions");
            int depth = b[8], color = b[9];
            bool ok = color switch
            {
                0 => depth is 1 or 2 or 4 or 8 or 16,
                3 => depth is 1 or 2 or 4 or 8,
                2 or 4 or 6 => depth is 8 or 16,
                _ => false,
            };
            if (!ok) throw new ImageFormatException($"invalid PNG color type {color} / bit depth {depth}");
            if (b[10] != 0) throw new ImageFormatException("invalid PNG compression method");
            if (b[11] != 0) throw new ImageFormatException("invalid PNG filter method");
            if (b[12] > 1) throw new ImageFormatException("invalid PNG interlace method");
            return new Header { Width = (int)w, Height = (int)h, BitDepth = depth, ColorType = color, Interlaced = b[12] == 1 };
        }

        /// <summary>Reads the chunk at <paramref name="pos"/>; throws when it does not fit into the data.</summary>
        static void ReadChunk(ReadOnlySpan<byte> data, int pos, out uint type, out ReadOnlySpan<byte> body)
        {
            if (data.Length - pos < 12) throw new ImageFormatException("PNG chunk is truncated");
            uint len = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(pos));
            if (len > (uint)(data.Length - pos - 12)) throw new ImageFormatException("PNG chunk is truncated");
            type = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(pos + 4));
            for (int s = 0; s < 32; s += 8)
            {
                uint c = ((type >> s) & 0xFF) | 0x20;
                if (c < 'a' || c > 'z') throw new ImageFormatException("invalid PNG chunk type");
            }
            body = data.Slice(pos + 8, (int)len);
        }

        static bool CrcOk(ReadOnlySpan<byte> data, int chunkPos, int bodyLength)
            => Zlib.Crc32(data.Slice(chunkPos + 4, 4 + bodyLength)) == BinaryPrimitives.ReadUInt32BigEndian(data.Slice(chunkPos + 8 + bodyLength));

        static bool IsCritical(uint type) => (type & 0x20000000) == 0;

        static string TypeName(uint type)
            => new string(new[] { (char)(type >> 24), (char)((type >> 16) & 0xFF), (char)((type >> 8) & 0xFF), (char)(type & 0xFF) });

        static bool IsUsableTransparency(in Header h, ReadOnlySpan<byte> trns) => h.ColorType switch
        {
            0 => trns.Length >= 2,
            2 => trns.Length >= 6,
            3 => trns.Length >= 1,
            _ => false,
        };

        static void OutputFormat(in Header h, bool hasTransparency, bool keepIndexed, out ColorModel color, out SampleType type)
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

        /// <summary>
        /// Walks the chunks in front of the pixel data without checking CRCs and picks up PLTE / tRNS.
        /// Stops quietly at IDAT, IEND or where the data ends.
        /// </summary>
        static void ScanHead(ReadOnlySpan<byte> data, scoped in Header h, out ReadOnlySpan<byte> plte, out ReadOnlySpan<byte> trns)
        {
            plte = default; trns = default;
            int pos = FirstChunkAfterHeader;
            while (data.Length - pos >= 12)
            {
                uint len = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(pos));
                uint type = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(pos + 4));
                if (type == IDAT || type == IEND || len > (uint)(data.Length - pos - 12)) return;
                var body = data.Slice(pos + 8, (int)len);
                if (type == PLTE && plte.IsEmpty) plte = body;
                else if (type == tRNS && trns.IsEmpty && IsUsableTransparency(h, body)) trns = body;
                pos += 12 + (int)len;
            }
        }

        public static ImageInfo GetInfo(ReadOnlySpan<byte> data)
        {
            var h = ReadHeader(data, verifyCrc: false);
            ReadOnlySpan<byte> trns = default;
            if (h.ColorType is 0 or 2 or 3) ScanHead(data, h, out _, out trns);
            OutputFormat(h, !trns.IsEmpty, false, out var color, out var type);
            return new ImageInfo(ImageFormat.Png, h.Width, h.Height, RawImage.ChannelCount(color), type, color);
        }

        public static byte[]? ReadPalette(ReadOnlySpan<byte> data)
        {
            var h = ReadHeader(data, verifyCrc: false);
            if (!h.IsPalette) return null;
            ScanHead(data, h, out var plte, out var trns);
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
            bool verify = options.VerifyChecksums;
            var h = ReadHeader(data, verify);

            ReadOnlySpan<byte> plte = default, trns = default;
            bool hasPlte = false;
            ImageMetadata? meta = null;
            int firstIdat = -1, idatCount = 0;
            long idatTotal = 0;
            bool idatEnded = false;

            int pos = FirstChunkAfterHeader;
            for (bool end = false; !end;)
            {
                if (pos == data.Length) throw new ImageFormatException("PNG data ends without an IEND chunk");
                ReadChunk(data, pos, out uint type, out var body);
                int chunkPos = pos;
                pos += 12 + body.Length;

                bool critical = IsCritical(type);
                bool wanted = critical || type is tRNS or gAMA or pHYs or iCCP or eXIf;
                if (verify && wanted && !CrcOk(data, chunkPos, body.Length))
                {
                    if (critical) throw new ImageFormatException($"PNG {TypeName(type)} checksum mismatch");
                    continue; // a damaged ancillary chunk is dropped
                }
                if (type != IDAT && idatCount > 0) idatEnded = true;

                switch (type)
                {
                    case IHDR:
                        throw new ImageFormatException("duplicate PNG IHDR chunk");
                    case PLTE:
                        if (hasPlte) throw new ImageFormatException("duplicate PNG PLTE chunk");
                        if (idatCount > 0) throw new ImageFormatException("PNG PLTE chunk after the pixel data");
                        if (body.Length == 0 || body.Length % 3 != 0 || body.Length > 768)
                            throw new ImageFormatException("invalid PNG PLTE chunk length");
                        plte = body; hasPlte = true;
                        break;
                    case IDAT:
                        if (idatEnded) throw new ImageFormatException("PNG IDAT chunks are not consecutive");
                        if (firstIdat < 0) firstIdat = chunkPos;
                        idatCount++;
                        idatTotal += body.Length;
                        break;
                    case IEND:
                        end = true;
                        break;
                    case tRNS:
                        if (idatCount == 0 && trns.IsEmpty && !options.IgnoreTransparency && IsUsableTransparency(h, body)) trns = body;
                        break;
                    case gAMA:
                        if (body.Length == 4 && BinaryPrimitives.ReadUInt32BigEndian(body) is > 0 and var g)
                            (meta ??= new ImageMetadata()).Gamma = g / 100000.0;
                        break;
                    case pHYs:
                        if (body.Length == 9 && body[8] == 1)
                        {
                            double x = PixelsPerMeterToDpi(BinaryPrimitives.ReadUInt32BigEndian(body));
                            double y = PixelsPerMeterToDpi(BinaryPrimitives.ReadUInt32BigEndian(body.Slice(4)));
                            if (x > 0 && y > 0) { meta ??= new ImageMetadata(); meta.DpiX = x; meta.DpiY = y; }
                        }
                        break;
                    case iCCP:
                        if (ReadIccProfile(body) is { } icc) (meta ??= new ImageMetadata()).IccProfile = icc;
                        break;
                    case eXIf:
                        if (body.Length >= 8)
                        {
                            meta ??= new ImageMetadata();
                            meta.Exif = body.ToArray();
                            meta.Orientation = ExifOrientation(body);
                        }
                        break;
                    default:
                        if (critical) throw new ImageNotSupportedException($"unknown critical PNG chunk '{TypeName(type)}'");
                        break;
                }
            }

            if (idatCount == 0) throw new ImageFormatException("PNG has no IDAT chunk");
            if (h.IsPalette && !hasPlte) throw new ImageFormatException("palette PNG has no PLTE chunk");

            // Size of the inflated stream: every row is preceded by a filter byte; interlaced images are 7 sub images.
            int bitsPerPixel = h.BitsPerPixel;
            long rowBytes = ((long)h.Width * bitsPerPixel + 7) >> 3;
            long rawSize = 0;
            if (h.Interlaced)
            {
                for (int p = 0; p < 7; p++)
                {
                    PassSize(h, p, out int pw, out int ph);
                    if (pw > 0 && ph > 0) rawSize += ph * (1 + (((long)pw * bitsPerPixel + 7) >> 3));
                }
            }
            else rawSize = h.Height * (rowBytes + 1);

            // Deflate cannot expand by more than 1032:1, so a header promising more than the data can hold is
            // rejected before anything is allocated.
            if (rawSize > idatTotal * 1032 + 1032) throw new ImageFormatException("PNG pixel data is truncated");

            bool keepIndexed = options.KeepIndexed && h.IsPalette;
            OutputFormat(h, !trns.IsEmpty, keepIndexed, out var color, out var sampleType);
            int outChannels = RawImage.ChannelCount(color);
            long outSamples = (long)h.Width * h.Height * outChannels;
            if (outSamples * (h.BitDepth == 16 ? 2 : 1) > int.MaxValue || outSamples > Array.MaxLength || rawSize > Array.MaxLength)
                throw new ImageNotSupportedException("PNG image is too large (more than 2 GB of pixel data)");

            RawImage image;
            byte[]? joined = null;
            byte[] raw = ArrayPool<byte>.Shared.Rent((int)rawSize);
            try
            {
                ReadOnlySpan<byte> idat;
                if (idatCount == 1)
                {
                    ReadChunk(data, firstIdat, out _, out idat);
                }
                else
                {
                    joined = ArrayPool<byte>.Shared.Rent((int)idatTotal);
                    int n = 0;
                    for (int p = firstIdat, i = 0; i < idatCount; i++)
                    {
                        ReadChunk(data, p, out _, out var body);
                        body.CopyTo(joined.AsSpan(n));
                        n += body.Length;
                        p += 12 + body.Length;
                    }
                    idat = joined.AsSpan(0, n);
                }

                int got;
                try
                {
                    got = Zlib.Decompress(idat, raw.AsSpan(0, (int)rawSize));
                }
                catch (Exception e) when (e is InvalidDataException or IOException)
                {
                    throw new ImageFormatException("corrupt PNG pixel data", e);
                }
                if (got != rawSize) throw new ImageFormatException("PNG pixel data is truncated");

                // The output is only allocated once the stream has proven to really hold that many pixels.
                image = RawImage.Create(h.Width, h.Height, color, sampleType);
                image.Metadata = meta;
                var conv = new RowConverter(h, trns, plte, keepIndexed, outChannels);
                Reconstruct(h, raw, image, conv);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(raw);
                if (joined != null) ArrayPool<byte>.Shared.Return(joined);
            }
            return image;
        }

        static void PassSize(in Header h, int pass, out int width, out int height)
        {
            width = (h.Width - PassX0[pass] + PassDx[pass] - 1) / PassDx[pass];
            height = (h.Height - PassY0[pass] + PassDy[pass] - 1) / PassDy[pass];
        }

        static void Reconstruct(in Header h, byte[] rawArray, RawImage image, RowConverter conv)
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
            enum Kind { Direct, Swap16, Unpack, GrayKey8, GrayKey16, UnpackGrayKey, RgbKey8, RgbKey16, Palette }

            readonly Kind _kind;
            readonly int _depth, _pngChannels, _scale;
            readonly int _key0, _key1, _key2;
            readonly bool _alpha;
            readonly byte[]? _palette;   // 256 RGBA entries
            readonly byte[]? _indices;   // unpacked low bit depth samples of one row

            /// <summary>The reconstructed bytes already are the output samples.</summary>
            public bool IsDirect => _kind == Kind.Direct;

            public RowConverter(in Header h, ReadOnlySpan<byte> trns, ReadOnlySpan<byte> plte, bool keepIndexed, int outChannels)
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
                    if (keepIndexed) _kind = _depth == 8 ? Kind.Direct : Kind.Unpack;
                    else
                    {
                        _kind = Kind.Palette;
                        _alpha = outChannels == 4;
                        // Indices beyond the palette decode as opaque black.
                        _palette = new byte[1024];
                        for (int i = 3; i < 1024; i += 4) _palette[i] = 255;
                        FillPalette(_palette, plte, trns);
                    }
                }
                else if (_depth == 16)
                    _kind = !keyed ? (BitConverter.IsLittleEndian ? Kind.Swap16 : Kind.Direct) : h.ColorType == 0 ? Kind.GrayKey16 : Kind.RgbKey16;
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

            /// <summary>Expands 1/2/4 bit samples (most significant bits first) to one byte each, multiplied by scale.</summary>
            static void Unpack(byte* src, byte* dst, int width, int depth, int scale)
            {
                int mask = (1 << depth) - 1;
                int perByte = 8 / depth;
                int x = 0;
                if (depth == 1 && BitConverter.IsLittleEndian)
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

        // ------------------------------------------------------------------ ancillary chunks

        /// <summary>
        /// pHYs stores whole pixels per meter, so e.g. 72 dpi comes back as 72.009. A value within half a
        /// quantization step (0.0127 dpi) of a whole dpi is taken to mean that whole number.
        /// </summary>
        static double PixelsPerMeterToDpi(uint pixelsPerMeter)
        {
            double dpi = pixelsPerMeter * 0.0254;
            double rounded = Math.Round(dpi);
            return Math.Abs(dpi - rounded) <= 0.0127 ? rounded : dpi;
        }

        static byte[]? ReadIccProfile(ReadOnlySpan<byte> body)
        {
            // profile name (1..79 bytes), 0, compression method (0), zlib stream
            int nameEnd = body.Slice(0, Math.Min(body.Length, 80)).IndexOf((byte)0);
            if (nameEnd < 1 || body.Length < nameEnd + 3 || body[nameEnd + 1] != 0) return null;
            try
            {
                var icc = Zlib.Decompress(body.Slice(nameEnd + 2));
                return icc.Length > 0 ? icc : null;
            }
            catch (Exception e) when (e is InvalidDataException or IOException)
            {
                return null;
            }
        }

        /// <summary>Orientation (tag 0x0112) from the first IFD of an EXIF blob, 0 if absent or malformed.</summary>
        static int ExifOrientation(ReadOnlySpan<byte> exif)
        {
            if (exif.Length < 8) return 0;
            bool little;
            if (exif[0] == 'I' && exif[1] == 'I') little = true;
            else if (exif[0] == 'M' && exif[1] == 'M') little = false;
            else return 0;
            if (U16(exif, 2, little) != 42) return 0;
            uint ifd = U32(exif, 4, little);
            if (ifd > (uint)exif.Length - 2) return 0;
            int count = U16(exif, (int)ifd, little);
            long entry = ifd + 2;
            for (int i = 0; i < count && entry + 12 <= exif.Length; i++, entry += 12)
            {
                if (U16(exif, (int)entry, little) != 0x0112) continue;
                if (U16(exif, (int)entry + 2, little) != 3 || U32(exif, (int)entry + 4, little) != 1) return 0;
                int v = U16(exif, (int)entry + 8, little);
                return v is >= 1 and <= 8 ? v : 0;
            }
            return 0;

            static int U16(ReadOnlySpan<byte> s, int o, bool le)
                => le ? BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(o)) : BinaryPrimitives.ReadUInt16BigEndian(s.Slice(o));
            static uint U32(ReadOnlySpan<byte> s, int o, bool le)
                => le ? BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(o)) : BinaryPrimitives.ReadUInt32BigEndian(s.Slice(o));
        }
    }
}
