using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using ArmCrc32 = System.Runtime.Intrinsics.Arm.Crc32;

namespace Aardvark.ImageCodecs.Compression
{
    /// <summary>Checksums used by the compression containers. All paths (scalar, SIMD) give identical results.</summary>
    internal static unsafe class Checksums
    {
        // ------------------------------------------------------------------------------------------------ CRC-32

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

        /// <summary>CRC-32 (IEEE 802.3, reflected, as used by PNG, gzip and xz).</summary>
        public static uint Crc32(ReadOnlySpan<byte> data, uint crc)
        {
            if (data.Length >= 64)
            {
                if (Pclmulqdq.IsSupported && Sse41.IsSupported) return Crc32Pclmul(data, crc);
                if (ArmCrc32.Arm64.IsSupported) return Crc32Arm(data, crc);
            }
            else if (ArmCrc32.Arm64.IsSupported) return Crc32Arm(data, crc);
            return Crc32Scalar(data, crc);
        }

        /// <summary>Slicing-by-8 table implementation.</summary>
        internal static uint Crc32Scalar(ReadOnlySpan<byte> data, uint crc)
        {
            fixed (byte* p = data) return ~Crc32Raw(p, data.Length, ~crc);
        }

        // works on the raw (non inverted) register
        static uint Crc32Raw(byte* p, int length, uint c)
        {
            fixed (uint* t = &CrcTable[0])
            {
                int i = 0;
                for (; i + 8 <= length; i += 8)
                {
                    uint lo = c ^ Unsafe.ReadUnaligned<uint>(p + i);
                    uint hi = Unsafe.ReadUnaligned<uint>(p + i + 4);
                    c = t[7 * 256 + (lo & 0xFF)] ^ t[6 * 256 + ((lo >> 8) & 0xFF)] ^ t[5 * 256 + ((lo >> 16) & 0xFF)] ^ t[4 * 256 + (lo >> 24)]
                      ^ t[3 * 256 + (hi & 0xFF)] ^ t[2 * 256 + ((hi >> 8) & 0xFF)] ^ t[1 * 256 + ((hi >> 16) & 0xFF)] ^ t[hi >> 24];
                }
                for (; i < length; i++) c = t[(c ^ p[i]) & 0xFF] ^ (c >> 8);
            }
            return c;
        }

        /// <summary>
        /// Carry-less multiplication folding: four 128 bit lanes are folded over 512 bits per step. The folded
        /// register is congruent to the message seen so far, so the last 16 bytes just go through the table.
        /// Fold constants are x^(D+32) mod P and x^(D-32) mod P (bit reflected, shifted left by one) for D = 512, 128.
        /// </summary>
        internal static uint Crc32Pclmul(ReadOnlySpan<byte> data, uint crc)
        {
            if (data.Length < 64) return Crc32Scalar(data, crc);
            fixed (byte* p0 = data)
            {
                byte* p = p0;
                int len = data.Length;
                var k512 = Vector128.Create(0x0000000154442bd4UL, 0x00000001c6e41596UL);
                var k128 = Vector128.Create(0x00000001751997d0UL, 0x00000000ccaa009eUL);

                var x1 = Sse2.LoadVector128(p).AsUInt64();
                var x2 = Sse2.LoadVector128(p + 16).AsUInt64();
                var x3 = Sse2.LoadVector128(p + 32).AsUInt64();
                var x4 = Sse2.LoadVector128(p + 48).AsUInt64();
                x1 = Sse2.Xor(x1, Vector128.CreateScalar(~crc).AsUInt64());
                p += 64; len -= 64;
                while (len >= 64)
                {
                    x1 = Sse2.Xor(Sse2.Xor(Pclmulqdq.CarrylessMultiply(x1, k512, 0x00), Pclmulqdq.CarrylessMultiply(x1, k512, 0x11)), Sse2.LoadVector128(p).AsUInt64());
                    x2 = Sse2.Xor(Sse2.Xor(Pclmulqdq.CarrylessMultiply(x2, k512, 0x00), Pclmulqdq.CarrylessMultiply(x2, k512, 0x11)), Sse2.LoadVector128(p + 16).AsUInt64());
                    x3 = Sse2.Xor(Sse2.Xor(Pclmulqdq.CarrylessMultiply(x3, k512, 0x00), Pclmulqdq.CarrylessMultiply(x3, k512, 0x11)), Sse2.LoadVector128(p + 32).AsUInt64());
                    x4 = Sse2.Xor(Sse2.Xor(Pclmulqdq.CarrylessMultiply(x4, k512, 0x00), Pclmulqdq.CarrylessMultiply(x4, k512, 0x11)), Sse2.LoadVector128(p + 48).AsUInt64());
                    p += 64; len -= 64;
                }
                x1 = Sse2.Xor(Sse2.Xor(Pclmulqdq.CarrylessMultiply(x1, k128, 0x00), Pclmulqdq.CarrylessMultiply(x1, k128, 0x11)), x2);
                x1 = Sse2.Xor(Sse2.Xor(Pclmulqdq.CarrylessMultiply(x1, k128, 0x00), Pclmulqdq.CarrylessMultiply(x1, k128, 0x11)), x3);
                x1 = Sse2.Xor(Sse2.Xor(Pclmulqdq.CarrylessMultiply(x1, k128, 0x00), Pclmulqdq.CarrylessMultiply(x1, k128, 0x11)), x4);
                while (len >= 16)
                {
                    x1 = Sse2.Xor(Sse2.Xor(Pclmulqdq.CarrylessMultiply(x1, k128, 0x00), Pclmulqdq.CarrylessMultiply(x1, k128, 0x11)), Sse2.LoadVector128(p).AsUInt64());
                    p += 16; len -= 16;
                }
                byte* tmp = stackalloc byte[16];
                Sse2.Store(tmp, x1.AsByte());
                uint c = Crc32Raw(tmp, 16, 0);
                return ~Crc32Raw(p, len, c);
            }
        }

        /// <summary>ARMv8 CRC32 instructions.</summary>
        internal static uint Crc32Arm(ReadOnlySpan<byte> data, uint crc)
        {
            uint c = ~crc;
            fixed (byte* p0 = data)
            {
                byte* p = p0;
                int len = data.Length;
                for (; len >= 8; len -= 8, p += 8) c = ArmCrc32.Arm64.ComputeCrc32(c, Unsafe.ReadUnaligned<ulong>(p));
                for (; len > 0; len--, p++) c = ArmCrc32.ComputeCrc32(c, *p);
            }
            return ~c;
        }

        // ---------------------------------------------------------------------------------------------- Adler-32

        const uint AdlerMod = 65521;
        const int AdlerMax = 5552; // largest n with 255n(n+1)/2 + (n+1)(65520) < 2^32

        public static uint Adler32(ReadOnlySpan<byte> data, uint adler)
        {
            if (data.Length >= 64)
            {
                if (Avx2.IsSupported) return Adler32Avx2(data, adler);
                if (Vector128.IsHardwareAccelerated) return Adler32Vector128(data, adler);
            }
            return Adler32Scalar(data, adler);
        }

        internal static uint Adler32Scalar(ReadOnlySpan<byte> data, uint adler)
        {
            uint a = adler & 0xFFFF, b = adler >> 16;
            fixed (byte* p0 = data)
            {
                byte* p = p0, end = p0 + data.Length;
                while (p < end)
                {
                    byte* stop = p + Math.Min(AdlerMax, (int)(end - p));
                    for (; p + 8 <= stop; p += 8)
                    {
                        a += p[0]; b += a; a += p[1]; b += a; a += p[2]; b += a; a += p[3]; b += a;
                        a += p[4]; b += a; a += p[5]; b += a; a += p[6]; b += a; a += p[7]; b += a;
                    }
                    for (; p < stop; p++) { a += *p; b += a; }
                    a %= AdlerMod; b %= AdlerMod;
                }
            }
            return (b << 16) | a;
        }

        internal static uint Adler32Avx2(ReadOnlySpan<byte> data, uint adler)
        {
            uint a = adler & 0xFFFF, b = adler >> 16;
            fixed (byte* p0 = data)
            {
                byte* p = p0;
                int len = data.Length;
                var taps = Vector256.Create((sbyte)32, 31, 30, 29, 28, 27, 26, 25, 24, 23, 22, 21, 20, 19, 18, 17, 16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1);
                var ones = Vector256.Create((short)1);
                var zero = Vector256<byte>.Zero;
                while (len >= 32)
                {
                    int blocks = Math.Min(len, AdlerMax) / 32;
                    len -= blocks * 32;
                    // b gains 32 * (a before each block) plus the weighted byte sum of each block
                    b += a * (uint)(blocks * 32) % AdlerMod; // a < 65521, blocks*32 <= 5536: no overflow
                    var vs1 = Vector256<uint>.Zero; // per-lane byte sums
                    var vps = Vector256<uint>.Zero; // sum of vs1 before each block
                    var vs2 = Vector256<uint>.Zero;
                    for (; blocks > 0; blocks--, p += 32)
                    {
                        var v = Avx.LoadVector256(p);
                        vps = Avx2.Add(vps, vs1);
                        vs1 = Avx2.Add(vs1, Avx2.SumAbsoluteDifferences(v, zero).AsUInt32());
                        vs2 = Avx2.Add(vs2, Avx2.MultiplyAddAdjacent(Avx2.MultiplyAddAdjacent(v, taps), ones).AsUInt32());
                    }
                    vs2 = Avx2.Add(vs2, Avx2.ShiftLeftLogical(vps, 5));
                    a += Vector256.Sum(vs1);
                    b += Vector256.Sum(vs2);
                    a %= AdlerMod; b %= AdlerMod;
                }
                return Adler32Scalar(new ReadOnlySpan<byte>(p, len), (b << 16) | a);
            }
        }

        /// <summary>Portable 128 bit path (SSE2, NEON, ...).</summary>
        internal static uint Adler32Vector128(ReadOnlySpan<byte> data, uint adler)
        {
            uint a = adler & 0xFFFF, b = adler >> 16;
            fixed (byte* p0 = data)
            {
                byte* p = p0;
                int len = data.Length;
                var tapLo = Vector128.Create((ushort)16, 15, 14, 13, 12, 11, 10, 9);
                var tapHi = Vector128.Create((ushort)8, 7, 6, 5, 4, 3, 2, 1);
                while (len >= 16)
                {
                    int blocks = Math.Min(len, AdlerMax) / 16;
                    len -= blocks * 16;
                    b += a * (uint)(blocks * 16) % AdlerMod;
                    var vs1 = Vector128<uint>.Zero;
                    var vps = Vector128<uint>.Zero;
                    var vs2 = Vector128<uint>.Zero;
                    for (; blocks > 0; blocks--, p += 16)
                    {
                        var v = Vector128.Load(p);
                        var (lo, hi) = Vector128.Widen(v);
                        vps += vs1;
                        var sum = lo + hi; // 8 lanes, each <= 510
                        var (s0, s1) = Vector128.Widen(sum);
                        vs1 += s0 + s1;
                        var wl = lo * tapLo; // <= 255 * 16
                        var wh = hi * tapHi;
                        var (w0, w1) = Vector128.Widen(wl);
                        var (w2, w3) = Vector128.Widen(wh);
                        vs2 += w0 + w1 + w2 + w3;
                    }
                    vs2 += Vector128.ShiftLeft(vps, 4);
                    a += Vector128.Sum(vs1);
                    b += Vector128.Sum(vs2);
                    a %= AdlerMod; b %= AdlerMod;
                }
                return Adler32Scalar(new ReadOnlySpan<byte>(p, len), (b << 16) | a);
            }
        }

        // ------------------------------------------------------------------------------------------------ CRC-64

        static readonly ulong[] Crc64Table = MakeCrc64Table();

        static ulong[] MakeCrc64Table()
        {
            var t = new ulong[4 * 256];
            for (uint n = 0; n < 256; n++)
            {
                ulong c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xC96C5795D7870F42UL ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            for (int n = 0; n < 256; n++)
                for (int s = 1; s < 4; s++)
                    t[s * 256 + n] = t[t[(s - 1) * 256 + n] & 0xFF] ^ (t[(s - 1) * 256 + n] >> 8);
            return t;
        }

        /// <summary>CRC-64 (ECMA-182 polynomial, reflected, as used by xz).</summary>
        public static ulong Crc64(ReadOnlySpan<byte> data, ulong crc = 0)
        {
            ulong c = ~crc;
            fixed (byte* p = data)
            fixed (ulong* t = &Crc64Table[0])
            {
                int i = 0;
                for (; i + 4 <= data.Length; i += 4)
                {
                    uint lo = (uint)c ^ Unsafe.ReadUnaligned<uint>(p + i);
                    c = (c >> 32) ^ t[3 * 256 + (lo & 0xFF)] ^ t[2 * 256 + ((lo >> 8) & 0xFF)] ^ t[1 * 256 + ((lo >> 16) & 0xFF)] ^ t[lo >> 24];
                }
                for (; i < data.Length; i++) c = t[(c ^ p[i]) & 0xFF] ^ (c >> 8);
            }
            return ~c;
        }

        // ---------------------------------------------------------------------------------------------- xxHash64

        const ulong P1 = 11400714785074694791UL, P2 = 14029467366897019727UL, P3 = 1609587929392839161UL,
                    P4 = 9650029242287828579UL, P5 = 2870177450012600261UL;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ulong XxRound(ulong acc, ulong input) => BitOperations.RotateLeft(acc + input * P2, 31) * P1;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ulong XxMerge(ulong h, ulong v) => (h ^ XxRound(0, v)) * P1 + P4;

        /// <summary>XXH64 (zstd content checksum uses the low 32 bits with seed 0).</summary>
        public static ulong XxHash64(ReadOnlySpan<byte> data, ulong seed = 0)
        {
            fixed (byte* p0 = data)
            {
                byte* p = p0, end = p0 + data.Length;
                ulong h;
                if (data.Length >= 32)
                {
                    ulong v1 = seed + P1 + P2, v2 = seed + P2, v3 = seed, v4 = seed - P1;
                    byte* limit = end - 32;
                    do
                    {
                        v1 = XxRound(v1, Unsafe.ReadUnaligned<ulong>(p));
                        v2 = XxRound(v2, Unsafe.ReadUnaligned<ulong>(p + 8));
                        v3 = XxRound(v3, Unsafe.ReadUnaligned<ulong>(p + 16));
                        v4 = XxRound(v4, Unsafe.ReadUnaligned<ulong>(p + 24));
                        p += 32;
                    } while (p <= limit);
                    h = BitOperations.RotateLeft(v1, 1) + BitOperations.RotateLeft(v2, 7) + BitOperations.RotateLeft(v3, 12) + BitOperations.RotateLeft(v4, 18);
                    h = XxMerge(h, v1); h = XxMerge(h, v2); h = XxMerge(h, v3); h = XxMerge(h, v4);
                }
                else h = seed + P5;
                h += (ulong)data.Length;
                for (; p + 8 <= end; p += 8)
                    h = BitOperations.RotateLeft(h ^ XxRound(0, Unsafe.ReadUnaligned<ulong>(p)), 27) * P1 + P4;
                if (p + 4 <= end)
                {
                    h = BitOperations.RotateLeft(h ^ (Unsafe.ReadUnaligned<uint>(p) * P1), 23) * P2 + P3;
                    p += 4;
                }
                for (; p < end; p++) h = BitOperations.RotateLeft(h ^ (*p * P5), 11) * P1;
                h ^= h >> 33; h *= P2; h ^= h >> 29; h *= P3; h ^= h >> 32;
                return h;
            }
        }

        // ----------------------------------------------------------------------------------------------- SHA-256

        static readonly uint[] ShaK =
        {
            0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
            0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
            0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
            0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
            0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
            0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
            0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
            0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
        };

        /// <summary>SHA-256 (FIPS 180-4), for the xz SHA-256 integrity check. Writes 32 bytes to <paramref name="digest"/>.</summary>
        public static void Sha256(ReadOnlySpan<byte> data, Span<byte> digest)
        {
            Span<uint> h = stackalloc uint[8]
            {
                0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19,
            };
            Span<uint> w = stackalloc uint[64];
            int full = data.Length & ~63;
            for (int i = 0; i < full; i += 64) ShaBlock(data.Slice(i, 64), h, w);
            Span<byte> tail = stackalloc byte[128];
            tail.Clear();
            int rest = data.Length - full;
            data.Slice(full).CopyTo(tail);
            tail[rest] = 0x80;
            int tailLen = rest < 56 ? 64 : 128;
            BinaryPrimitives.WriteUInt64BigEndian(tail.Slice(tailLen - 8), (ulong)data.Length * 8);
            for (int i = 0; i < tailLen; i += 64) ShaBlock(tail.Slice(i, 64), h, w);
            for (int i = 0; i < 8; i++) BinaryPrimitives.WriteUInt32BigEndian(digest.Slice(i * 4), h[i]);
        }

        static void ShaBlock(ReadOnlySpan<byte> block, Span<uint> h, Span<uint> w)
        {
            for (int i = 0; i < 16; i++) w[i] = BinaryPrimitives.ReadUInt32BigEndian(block.Slice(i * 4));
            for (int i = 16; i < 64; i++)
            {
                uint s0 = BitOperations.RotateRight(w[i - 15], 7) ^ BitOperations.RotateRight(w[i - 15], 18) ^ (w[i - 15] >> 3);
                uint s1 = BitOperations.RotateRight(w[i - 2], 17) ^ BitOperations.RotateRight(w[i - 2], 19) ^ (w[i - 2] >> 10);
                w[i] = w[i - 16] + s0 + w[i - 7] + s1;
            }
            uint a = h[0], b = h[1], c = h[2], d = h[3], e = h[4], f = h[5], g = h[6], hh = h[7];
            var k = ShaK;
            for (int i = 0; i < 64; i++)
            {
                uint S1 = BitOperations.RotateRight(e, 6) ^ BitOperations.RotateRight(e, 11) ^ BitOperations.RotateRight(e, 25);
                uint t1 = hh + S1 + ((e & f) ^ (~e & g)) + k[i] + w[i];
                uint S0 = BitOperations.RotateRight(a, 2) ^ BitOperations.RotateRight(a, 13) ^ BitOperations.RotateRight(a, 22);
                uint t2 = S0 + ((a & b) ^ (a & c) ^ (b & c));
                hh = g; g = f; f = e; e = d + t1; d = c; c = b; b = a; a = t1 + t2;
            }
            h[0] += a; h[1] += b; h[2] += c; h[3] += d; h[4] += e; h[5] += f; h[6] += g; h[7] += hh;
        }
    }
}
