using System;
using System.IO;
using System.IO.Compression;

namespace PixCodec.Compression
{
    /// <summary>
    /// zlib (RFC 1950) and raw deflate (RFC 1951) for all codecs. Every codec must go through this class
    /// so the backend can be swapped (BCL or fully managed) in one place.
    /// </summary>
    public static class Zlib
    {
        /// <summary>Decompresses a zlib stream into a buffer of known size. Returns the number of bytes written.</summary>
        public static int Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
        {
            unsafe
            {
                fixed (byte* p = source)
                {
                    using var ms = new UnmanagedMemoryStream(p, source.Length);
                    using var z = new ZLibStream(ms, CompressionMode.Decompress);
                    return ReadFully(z, destination);
                }
            }
        }

        /// <summary>Decompresses a zlib stream of unknown decompressed size.</summary>
        public static byte[] Decompress(ReadOnlySpan<byte> source, int sizeHint = 0)
        {
            unsafe
            {
                fixed (byte* p = source)
                {
                    using var ms = new UnmanagedMemoryStream(p, source.Length);
                    using var z = new ZLibStream(ms, CompressionMode.Decompress);
                    using var o = new MemoryStream(Math.Max(sizeHint, 256));
                    z.CopyTo(o);
                    return o.ToArray();
                }
            }
        }

        /// <summary>Decompresses raw deflate data (no zlib header) into a buffer of known size.</summary>
        public static int DecompressRaw(ReadOnlySpan<byte> source, Span<byte> destination)
        {
            unsafe
            {
                fixed (byte* p = source)
                {
                    using var ms = new UnmanagedMemoryStream(p, source.Length);
                    using var z = new DeflateStream(ms, CompressionMode.Decompress);
                    return ReadFully(z, destination);
                }
            }
        }

        /// <summary>Compresses to a zlib stream. Level 0..9 (0 = store, 6 = default, 9 = smallest).</summary>
        public static byte[] Compress(ReadOnlySpan<byte> source, int level = 6)
        {
            using var o = new MemoryStream(source.Length / 2 + 64);
            using (var z = new ZLibStream(o, MapLevel(level), leaveOpen: true))
                z.Write(source);
            return o.ToArray();
        }

        /// <summary>A stream that zlib-compresses everything written to it into <paramref name="output"/> (left open).</summary>
        public static Stream CreateCompressor(Stream output, int level = 6)
            => new ZLibStream(output, MapLevel(level), leaveOpen: true);

        static CompressionLevel MapLevel(int level) => level switch
        {
            <= 0 => CompressionLevel.NoCompression,
            <= 3 => CompressionLevel.Fastest,
            <= 7 => CompressionLevel.Optimal,
            _ => CompressionLevel.SmallestSize,
        };

        static int ReadFully(Stream s, Span<byte> destination)
        {
            int total = 0;
            while (total < destination.Length)
            {
                int n = s.Read(destination.Slice(total));
                if (n <= 0) break;
                total += n;
            }
            return total;
        }

        /// <summary>Adler-32 checksum (zlib trailer).</summary>
        public static uint Adler32(ReadOnlySpan<byte> data, uint adler = 1)
        {
            uint a = adler & 0xFFFF, b = adler >> 16;
            int i = 0;
            while (i < data.Length)
            {
                int n = Math.Min(5552, data.Length - i);
                for (int end = i + n; i < end; i++) { a += data[i]; b += a; }
                a %= 65521; b %= 65521;
            }
            return (b << 16) | a;
        }

        /// <summary>CRC-32 (IEEE, as used by PNG and gzip).</summary>
        public static uint Crc32(ReadOnlySpan<byte> data, uint crc = 0)
        {
            var t = CrcTable;
            uint c = ~crc;
            int i = 0;
            for (; i + 8 <= data.Length; i += 8)
            {
                uint lo = c ^ (uint)(data[i] | data[i + 1] << 8 | data[i + 2] << 16 | data[i + 3] << 24);
                uint hi = (uint)(data[i + 4] | data[i + 5] << 8 | data[i + 6] << 16 | data[i + 7] << 24);
                c = t[7 * 256 + (lo & 0xFF)] ^ t[6 * 256 + ((lo >> 8) & 0xFF)] ^ t[5 * 256 + ((lo >> 16) & 0xFF)] ^ t[4 * 256 + (lo >> 24)]
                  ^ t[3 * 256 + (hi & 0xFF)] ^ t[2 * 256 + ((hi >> 8) & 0xFF)] ^ t[1 * 256 + ((hi >> 16) & 0xFF)] ^ t[hi >> 24];
            }
            for (; i < data.Length; i++) c = t[(c ^ data[i]) & 0xFF] ^ (c >> 8);
            return ~c;
        }

        static readonly uint[] CrcTable = MakeCrcTable();

        static uint[] MakeCrcTable()
        {
            var t = new uint[8 * 256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            for (int n = 0; n < 256; n++)
                for (int s = 1; s < 8; s++)
                    t[s * 256 + n] = t[t[(s - 1) * 256 + n] & 0xFF] ^ (t[(s - 1) * 256 + n] >> 8);
            return t;
        }
    }
}
