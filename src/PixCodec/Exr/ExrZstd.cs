using System;
using System.Buffers.Binary;
using PixCodec.Compression;

namespace PixCodec
{
    /// <summary>
    /// ZSTD compression (OpenEXR 3.5). A chunk is "zstd-exr", two 32 bit fields (2 and 1 in every file of the
    /// reference writer), the 64 bit size of the compressed data, and one zstd frame. The frame holds one stream for the
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

        public static void Decode(ReadOnlySpan<byte> src, Span<byte> dst, in ExrChunk c, ExrScratch s)
        {
            if (src.Length < 24 || !src.Slice(0, 8).SequenceEqual(Magic)) throw Corrupt("chunk header");
            if (BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(8)) != 2 || BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(12)) != 1)
                throw new ImageNotSupportedException("EXR ZSTD chunk has an unknown layout version");
            if (BinaryPrimitives.ReadUInt64LittleEndian(src.Slice(16)) != (ulong)(src.Length - 24)) throw Corrupt("compressed size");
            int total = dst.Length + (HasChannels(c, 2) ? 8 : 0) + (HasChannels(c, 4) ? 8 : 0);
            var buf = s.Bytes(0, total + 1);
            int got;
            try { got = Zstd.Decompress(src.Slice(24), buf.AsSpan(0, total + 1)); }
            catch (Exception e) when (e is not ImageFormatException and not ImageNotSupportedException and not OutOfMemoryException)
            {
                throw new ImageFormatException("EXR chunk has corrupt zstd data", e);
            }
            if (got != total) throw Corrupt("decompressed size");

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
            var packed = Zstd.Compress(buf.AsSpan(0, total), 3);
            var result = new byte[24 + packed.Length];
            Magic.CopyTo(result);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), 2);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12), 1);
            BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(16), (ulong)packed.Length);
            packed.CopyTo(result, 24);
            return result;
        }
    }
}
