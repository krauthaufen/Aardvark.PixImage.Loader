using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using PixCodec.Compression;

namespace PixCodec
{
    internal static unsafe class PngEncoder
    {
        const int IdatChunkSize = 1 << 18;

        public static void Encode(RawImage image, Stream output, PngEncodeOptions options)
        {
            ArgumentNullException.ThrowIfNull(image);
            ArgumentNullException.ThrowIfNull(output);

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
            int width = image.Width, height = image.Height, channels = image.Channels;

            Palette? palette = null;
            if (options.AutoPalette && depth == 8 && channels >= 3) palette = Palette.TryBuild(image);
            if (palette != null) { colorType = 3; depth = palette.BitDepth; channels = 1; }

            long rowBytesLong = ((long)width * channels * depth + 7) >> 3;
            if (rowBytesLong > Array.MaxLength - 64) throw new ImageNotSupportedException("image rows are too long for the PNG encoder");
            int rowBytes = (int)rowBytesLong;
            int bpp = Math.Max(1, channels * depth >> 3);

            output.Write(PngDecoder.Signature);

            Span<byte> ihdr = stackalloc byte[13];
            BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
            BinaryPrimitives.WriteInt32BigEndian(ihdr.Slice(4), height);
            ihdr[8] = (byte)depth;
            ihdr[9] = (byte)colorType;
            ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
            WriteChunk(output, PngDecoder.IHDR, ihdr);

            int level = Math.Clamp(options.CompressionLevel, 0, 9);
            var meta = options.WriteMetadata ? image.Metadata : null;
            if (meta != null) WriteColorMetadata(output, meta, level);
            if (palette != null)
            {
                WriteChunk(output, PngDecoder.PLTE, palette.Rgb);
                if (palette.Alpha.Length > 0) WriteChunk(output, PngDecoder.tRNS, palette.Alpha);
            }
            if (meta != null) WriteOtherMetadata(output, meta);

            var mode = options.Filter;
            if (mode == PngFilterMode.Auto)
                mode = palette != null || level == 0 ? PngFilterMode.None : PngFilterMode.Adaptive;

            // cur/prev hold the raw rows behind bpp zero bytes, so "the pixel to the left" is always readable.
            // Each candidate holds a filter type byte followed by the filtered row.
            int candidateCount = mode == PngFilterMode.Adaptive ? 5 : 1;
            var rows = new byte[2 * (bpp + rowBytes)];
            var candidates = new byte[candidateCount * (1 + rowBytes)];
            byte[]? source8 = image.Data as byte[];
            ushort[]? source16 = image.Data as ushort[];
            int samplesPerRow = width * image.Channels;

            using var idat = new IdatStream(output);
            using (var z = Zlib.CreateCompressor(idat, level))
            {
                fixed (byte* rowsP = rows, cand = candidates)
                {
                    byte* cur = rowsP + bpp, prev = rowsP + bpp + rowBytes + bpp;
                    for (int y = 0; y < height; y++)
                    {
                        var curSpan = new Span<byte>(cur, rowBytes);
                        if (palette != null) palette.PackRow(y, curSpan);
                        else if (source16 != null)
                        {
                            // PNG samples are big endian
                            var row = source16.AsSpan(checked(y * samplesPerRow), samplesPerRow);
                            if (BitConverter.IsLittleEndian) BinaryPrimitives.ReverseEndianness(row, MemoryMarshal.Cast<byte, ushort>(curSpan));
                            else MemoryMarshal.AsBytes(row).CopyTo(curSpan);
                        }
                        else source8.AsSpan(checked(y * rowBytes), rowBytes).CopyTo(curSpan);

                        byte* best = cand;
                        if (mode == PngFilterMode.Adaptive)
                        {
                            int stride = 1 + rowBytes;
                            long bestSum = PngFilters.SumAbs(cur, rowBytes);
                            int bestType = 0;
                            long s;
                            if ((s = PngFilters.FilterSub(cur, cand + stride + 1, rowBytes, bpp)) < bestSum) { bestSum = s; bestType = 1; }
                            if ((s = PngFilters.FilterUp(cur, prev, cand + 2 * stride + 1, rowBytes)) < bestSum) { bestSum = s; bestType = 2; }
                            if ((s = PngFilters.FilterAverage(cur, prev, cand + 3 * stride + 1, rowBytes, bpp)) < bestSum) { bestSum = s; bestType = 3; }
                            if ((s = PngFilters.FilterPaeth(cur, prev, cand + 4 * stride + 1, rowBytes, bpp)) < bestSum) { bestSum = s; bestType = 4; }
                            best = cand + bestType * stride;
                            if (bestType == 0) Buffer.MemoryCopy(cur, best + 1, rowBytes, rowBytes);
                            *best = (byte)bestType;
                        }
                        else
                        {
                            switch (mode)
                            {
                                case PngFilterMode.Sub: *best = 1; PngFilters.FilterSub(cur, best + 1, rowBytes, bpp); break;
                                case PngFilterMode.Up: *best = 2; PngFilters.FilterUp(cur, prev, best + 1, rowBytes); break;
                                case PngFilterMode.Average: *best = 3; PngFilters.FilterAverage(cur, prev, best + 1, rowBytes, bpp); break;
                                case PngFilterMode.Paeth: *best = 4; PngFilters.FilterPaeth(cur, prev, best + 1, rowBytes, bpp); break;
                                default: *best = 0; Buffer.MemoryCopy(cur, best + 1, rowBytes, rowBytes); break;
                            }
                        }
                        z.Write(new ReadOnlySpan<byte>(best, 1 + rowBytes));

                        byte* t = cur; cur = prev; prev = t;
                    }
                }
            }
            idat.Finish();

            WriteChunk(output, PngDecoder.IEND, default);
        }

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

        /// <summary>gAMA and iCCP, which must precede PLTE.</summary>
        static void WriteColorMetadata(Stream output, ImageMetadata meta, int level)
        {
            if (meta.Gamma > 0 && !double.IsInfinity(meta.Gamma))
            {
                Span<byte> b = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(b, (uint)Math.Clamp(Math.Round(meta.Gamma * 100000.0), 1, int.MaxValue));
                WriteChunk(output, PngDecoder.gAMA, b);
            }
            if (meta.IccProfile is { Length: > 0 } icc)
            {
                var name = "ICC Profile"u8;
                var z = Zlib.Compress(icc, Math.Max(level, 1));
                var body = new byte[name.Length + 2 + z.Length];
                name.CopyTo(body);
                z.CopyTo(body.AsSpan(name.Length + 2)); // name, 0 terminator, compression method 0
                WriteChunk(output, PngDecoder.iCCP, body);
            }
        }

        /// <summary>pHYs and eXIf.</summary>
        static void WriteOtherMetadata(Stream output, ImageMetadata meta)
        {
            double dx = meta.DpiX > 0 ? meta.DpiX : meta.DpiY, dy = meta.DpiY > 0 ? meta.DpiY : meta.DpiX;
            if (dx > 0 && dy > 0 && !double.IsInfinity(dx) && !double.IsInfinity(dy))
            {
                Span<byte> b = stackalloc byte[9];
                BinaryPrimitives.WriteUInt32BigEndian(b, (uint)Math.Clamp(Math.Round(dx / 0.0254), 1, int.MaxValue));
                BinaryPrimitives.WriteUInt32BigEndian(b.Slice(4), (uint)Math.Clamp(Math.Round(dy / 0.0254), 1, int.MaxValue));
                b[8] = 1; // unit: meter
                WriteChunk(output, PngDecoder.pHYs, b);
            }
            if (meta.Exif is { Length: > 0 } exif)
            {
                WriteChunk(output, PngDecoder.eXIf, exif);
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
                WriteChunk(output, PngDecoder.eXIf, tiff);
            }
        }

        /// <summary>Collects the compressed stream and emits it as IDAT chunks of a fixed size.</summary>
        sealed class IdatStream : Stream
        {
            readonly Stream _output;
            byte[]? _buffer = ArrayPool<byte>.Shared.Rent(IdatChunkSize);
            int _count;

            public IdatStream(Stream output) => _output = output;

            public override void Write(ReadOnlySpan<byte> data)
            {
                var buffer = _buffer ?? throw new ObjectDisposedException(nameof(IdatStream));
                while (!data.IsEmpty)
                {
                    int n = Math.Min(data.Length, IdatChunkSize - _count);
                    data.Slice(0, n).CopyTo(buffer.AsSpan(_count));
                    _count += n;
                    data = data.Slice(n);
                    if (_count == IdatChunkSize) Emit();
                }
            }

            public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
            public override void WriteByte(byte value) => Write(new ReadOnlySpan<byte>(in value));

            void Emit()
            {
                WriteChunk(_output, PngDecoder.IDAT, _buffer.AsSpan(0, _count));
                _count = 0;
            }

            /// <summary>Writes the remaining data (call after the compressor has been closed).</summary>
            public void Finish()
            {
                if (_count > 0) Emit();
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

        /// <summary>An 8 bit Rgb/Rgba image re-expressed as at most 256 palette entries plus one index per pixel.</summary>
        sealed class Palette
        {
            public byte[] Rgb = Array.Empty<byte>();
            /// <summary>tRNS content: alpha of the leading entries. Empty for Rgb input.</summary>
            public byte[] Alpha = Array.Empty<byte>();
            public int BitDepth;
            byte[] _indices = Array.Empty<byte>();
            int _width;

            public static Palette? TryBuild(RawImage image)
            {
                int channels = image.Channels;
                long pixelCount = (long)image.Width * image.Height;
                if (pixelCount > Array.MaxLength) return null;
                var src = image.GetData<byte>();
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
                                if (count == 256) return null;
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

                return new Palette
                {
                    Rgb = rgb,
                    Alpha = alpha,
                    BitDepth = count <= 2 ? 1 : count <= 4 ? 2 : count <= 16 ? 4 : 8,
                    _indices = indices,
                    _width = image.Width,
                };
            }

            public void PackRow(int y, Span<byte> row)
            {
                var src = _indices.AsSpan(checked(y * _width), _width);
                if (BitDepth == 8) { src.CopyTo(row); return; }
                row.Clear();
                int perByte = 8 / BitDepth;
                for (int x = 0; x < src.Length; x++)
                    row[x / perByte] |= (byte)(src[x] << (8 - BitDepth * (x % perByte + 1)));
            }
        }
    }
}
