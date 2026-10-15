using System;
using System.Buffers.Binary;
using Aardvark.ImageCodecs.Compression;

namespace Aardvark.ImageCodecs
{
    /// <summary>
    /// ZSTD compression (OpenEXR 3.5). A block is "zstd-exr", two 32 bit fields (2 and 1 for the chunks of flat parts,
    /// 1 and 0 for the sample count tables and sample data of deep parts, which are stored without the difference
    /// step), the 64 bit size of the compressed data, and one zstd frame. The frame holds one stream for the
    /// 16 bit channels and one for the 32 bit channels (those that exist), each preceded by its 64 bit size. A stream
    /// is, for each line, for each of its channels, the samples of the line as differences to the previous sample; the
    /// whole stream is then split into byte planes, least significant bytes first.
    /// </summary>
    internal static class ExrZstd
    {
        static ReadOnlySpan<byte> Magic => "zstd-exr"u8;

        static ImageFormatException Corrupt(string what) => new("EXR ZSTD data is corrupt: " + what);

        static long StreamSize(in ExrChunk c, int bytes)
        {
            long n = 0;
            for (int i = 0; i < c.Chans.Length; i++)
                if (c.Chans[i].Bytes == bytes) n += (long)c.Nx[i] * c.Ny[i] * bytes;
            return n;
        }

        static bool HasChannels(in ExrChunk c, int bytes)
        {
            foreach (var ch in c.Chans) if (ch.Bytes == bytes) return true;
            return false;
        }

        /// <summary>Checks the block header and decompresses the frame, which must have exactly the given size.</summary>
        static byte[] Unpack(ReadOnlySpan<byte> src, int total, uint field1, uint field2, ExrScratch s)
        {
            if (src.Length < 24 || !src.Slice(0, 8).SequenceEqual(Magic)) throw Corrupt("block header");
            if (BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(8)) != field1 || BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(12)) != field2)
                throw new ImageNotSupportedException("EXR ZSTD block has an unknown layout version");
            if (BinaryPrimitives.ReadUInt64LittleEndian(src.Slice(16)) != (ulong)(src.Length - 24)) throw Corrupt("compressed size");
            var buf = s.Bytes(0, total + 1);
            int got;
            try { got = Zstd.Decompress(src.Slice(24), buf.AsSpan(0, total + 1)); }
            catch (Exception e) when (e is not ImageFormatException and not ImageNotSupportedException and not OutOfMemoryException)
            {
                throw new ImageFormatException("EXR chunk has corrupt zstd data", e);
            }
            if (got != total) throw Corrupt("decompressed size");
            return buf;
        }

        static byte[] Pack(ReadOnlySpan<byte> streams, uint field1, uint field2)
        {
            var packed = Zstd.Compress(streams, 3);
            var result = new byte[24 + packed.Length];
            Magic.CopyTo(result);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), field1);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12), field2);
            BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(16), (ulong)packed.Length);
            packed.CopyTo(result, 24);
            return result;
        }

        /// <summary>A deep sample count table: one stream of 32 bit values in byte planes.</summary>
        public static void DecodeTable(ReadOnlySpan<byte> src, Span<byte> dst, ExrScratch s)
        {
            var buf = Unpack(src, dst.Length + 8, 1, 0, s);
            if (BinaryPrimitives.ReadUInt64LittleEndian(buf) != (ulong)dst.Length) throw Corrupt("stream size");
            int count = dst.Length / 4;
            for (int k = 0; k < count; k++)
                for (int b = 0; b < 4; b++) dst[4 * k + b] = buf[8 + b * count + k];
        }

        public static byte[] EncodeTable(ReadOnlySpan<byte> table)
        {
            var buf = new byte[table.Length + 8];
            BinaryPrimitives.WriteUInt64LittleEndian(buf, (ulong)table.Length);
            int count = table.Length / 4;
            for (int k = 0; k < count; k++)
                for (int b = 0; b < 4; b++) buf[8 + b * count + k] = table[4 * k + b];
            return Pack(buf, 1, 0);
        }

        /// <summary>
        /// Deep sample data. raw is, per row of the chunk, per channel, the samples of the row (rowSamples[y] of them);
        /// the streams hold the same values grouped by sample size, in byte planes, without differences.
        /// </summary>
        public static void DeepData(ReadOnlySpan<byte> src, Span<byte> raw, ExrChan[] chans, int[] rowSamples, int rows, ExrScratch s, bool decode, out byte[]? encoded)
        {
            long n2 = 0, n4 = 0, total = 0;
            for (int y = 0; y < rows; y++) total += rowSamples[y];
            foreach (var ch in chans) { if (ch.Bytes == 2) n2 += total; else n4 += total; }
            int size = (int)(n2 * 2 + n4 * 4) + (HasSize(chans, 2) ? 8 : 0) + (HasSize(chans, 4) ? 8 : 0);
            byte[] buf = decode ? Unpack(src, size, 1, 0, s) : new byte[size];
            int p = 0;
            for (int bytes = 2; bytes <= 4; bytes += 2)
            {
                if (!HasSize(chans, bytes)) continue;
                long count = bytes == 2 ? n2 : n4;
                if (decode) { if (BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(p)) != (ulong)(count * bytes)) throw Corrupt("stream size"); }
                else BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(p), (ulong)(count * bytes));
                p += 8;
                int k = 0, o = 0, c = (int)count;
                for (int y = 0; y < rows; y++)
                    foreach (var ch in chans)
                    {
                        int n = rowSamples[y];
                        if (ch.Bytes == bytes)
                            for (int j = 0; j < n; j++, k++)
                                for (int b = 0; b < bytes; b++)
                                {
                                    if (decode) raw[o + j * bytes + b] = buf[p + b * c + k];
                                    else buf[p + b * c + k] = raw[o + j * bytes + b];
                                }
                        o += n * ch.Bytes;
                    }
                p += c * bytes;
            }
            encoded = decode ? null : Pack(buf, 1, 0);
        }

        static bool HasSize(ExrChan[] chans, int bytes)
        {
            foreach (var ch in chans) if (ch.Bytes == bytes) return true;
            return false;
        }

        public static void Decode(ReadOnlySpan<byte> src, Span<byte> dst, in ExrChunk c, ExrScratch s)
        {
            int total = dst.Length + (HasChannels(c, 2) ? 8 : 0) + (HasChannels(c, 4) ? 8 : 0);
            var buf = Unpack(src, total, 2, 1, s);

            int p = 0;
            for (int bytes = 2; bytes <= 4; bytes += 2)
            {
                if (!HasChannels(c, bytes)) continue;
                long size = StreamSize(c, bytes);
                if (BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(p)) != (ulong)size) throw Corrupt("stream size");
                p += 8;
                var stream = buf.AsSpan(p, (int)size);
                int count = (int)size / bytes, k = 0;     // samples in the stream; plane b of sample k is stream[b * count + k]
                int line = 0;
                for (int y = c.Y0; y <= c.Y1; y++)
                {
                    for (int ci = 0; ci < c.Chans.Length; ci++)
                    {
                        if (!ExrChunk.IsSampled(y, c.Chans[ci].YS)) continue;
                        int n = c.Nx[ci];
                        if (c.Chans[ci].Bytes == bytes)
                        {
                            var o = dst.Slice(line, n * bytes);
                            if (bytes == 2)
                            {
                                ushort v = 0;
                                for (int j = 0; j < n; j++, k++)
                                {
                                    v += (ushort)(stream[k] | stream[count + k] << 8);
                                    BinaryPrimitives.WriteUInt16LittleEndian(o.Slice(2 * j), v);
                                }
                            }
                            else
                            {
                                uint v = 0;
                                for (int j = 0; j < n; j++, k++)
                                {
                                    v += (uint)(stream[k] | stream[count + k] << 8 | stream[2 * count + k] << 16 | stream[3 * count + k] << 24);
                                    BinaryPrimitives.WriteUInt32LittleEndian(o.Slice(4 * j), v);
                                }
                            }
                        }
                        line += n * c.Chans[ci].Bytes;
                    }
                }
                p += (int)size;
            }
        }

        public static byte[] Encode(ReadOnlySpan<byte> raw, in ExrChunk c, ExrScratch s)
        {
            int total = raw.Length + (HasChannels(c, 2) ? 8 : 0) + (HasChannels(c, 4) ? 8 : 0);
            var buf = s.Bytes(0, total);
            int p = 0;
            for (int bytes = 2; bytes <= 4; bytes += 2)
            {
                if (!HasChannels(c, bytes)) continue;
                long size = StreamSize(c, bytes);
                BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(p), (ulong)size);
                p += 8;
                var stream = buf.AsSpan(p, (int)size);
                int count = (int)size / bytes, k = 0, line = 0;
                for (int y = c.Y0; y <= c.Y1; y++)
                {
                    for (int ci = 0; ci < c.Chans.Length; ci++)
                    {
                        if (!ExrChunk.IsSampled(y, c.Chans[ci].YS)) continue;
                        int n = c.Nx[ci];
                        if (c.Chans[ci].Bytes == bytes)
                        {
                            var o = raw.Slice(line, n * bytes);
                            uint prev = 0;
                            for (int j = 0; j < n; j++, k++)
                            {
                                uint v = bytes == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(o.Slice(2 * j)) : BinaryPrimitives.ReadUInt32LittleEndian(o.Slice(4 * j));
                                uint d = v - prev;
                                prev = v;
                                for (int b = 0; b < bytes; b++) stream[b * count + k] = (byte)(d >> (8 * b));
                            }
                        }
                        line += n * c.Chans[ci].Bytes;
                    }
                }
                p += (int)size;
            }
            return Pack(buf.AsSpan(0, total), 2, 1);
        }
    }
}
