using System;
using System.Buffers.Binary;
using System.Numerics;

namespace Aardvark.ImageCodecs
{
    /// <summary>MD5 (RFC 1321) for ICC profile IDs; managed so that it also works where the platform offers no MD5.</summary>
    internal static class Md5
    {
        static readonly int[] S =
        {
            7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22,
            5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20,
            4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23,
            6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21,
        };

        static readonly uint[] K = MakeK();

        static uint[] MakeK()
        {
            var k = new uint[64];
            for (int i = 0; i < 64; i++) k[i] = (uint)(long)Math.Floor(Math.Abs(Math.Sin(i + 1)) * 4294967296.0);
            return k;
        }

        public static byte[] Hash(ReadOnlySpan<byte> msg)
        {
            uint a0 = 0x67452301, b0 = 0xefcdab89, c0 = 0x98badcfe, d0 = 0x10325476;
            long bitLen = (long)msg.Length * 8;
            int padded = ((msg.Length + 8) / 64 + 1) * 64;
            var buf = new byte[padded];
            msg.CopyTo(buf);
            buf[msg.Length] = 0x80;
            BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(padded - 8), bitLen);
            Span<uint> m = stackalloc uint[16];
            for (int blk = 0; blk < padded; blk += 64)
            {
                for (int i = 0; i < 16; i++) m[i] = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(blk + 4 * i));
                uint a = a0, b = b0, c = c0, d = d0;
                for (int i = 0; i < 64; i++)
                {
                    uint f; int g;
                    if (i < 16) { f = (b & c) | (~b & d); g = i; }
                    else if (i < 32) { f = (d & b) | (~d & c); g = (5 * i + 1) & 15; }
                    else if (i < 48) { f = b ^ c ^ d; g = (3 * i + 5) & 15; }
                    else { f = c ^ (b | ~d); g = (7 * i) & 15; }
                    f = f + a + K[i] + m[g];
                    a = d; d = c; c = b;
                    b += BitOperations.RotateLeft(f, S[i]);
                }
                a0 += a; b0 += b; c0 += c; d0 += d;
            }
            var r = new byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(r, a0);
            BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(4), b0);
            BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(8), c0);
            BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(12), d0);
            return r;
        }
    }
}
