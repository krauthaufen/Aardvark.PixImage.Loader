using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Aardvark.ImageCodecs
{
    /// <summary>
    /// ASTC block decoding (2D footprints 4x4 to 12x12 and 3D footprints 3x3x3 to 6x6x6, LDR and HDR endpoint modes,
    /// void-extent blocks), written from the Khronos Data Format Specification 1.3, section 23, which defines bit exact
    /// results.
    /// <para>
    /// Every call decodes one 128 bit block into its whole footprint: texel (x, y, z) at index ((z * bh + y) * bw + x),
    /// four channels (RGBA) each.
    /// <list type="bullet">
    /// <item><c>DecodeUnorm8</c>: LDR mode, the <c>decode_unorm8</c> mode (top 8 bits of the UNORM16 interpolation), or
    /// with <c>srgb</c> the sRGB decode: RGB endpoints expanded as (c &lt;&lt; 8) | 0x80 and the top 8 bits returned (sRGB
    /// encoded, for the caller to linearize); alpha expanded by bit replication and decoded as <c>decode_float16</c>
    /// (23.19), here then rounded to the nearest 8 bit value. HDR endpoint modes and HDR void-extent blocks give the
    /// error colour (only for the texels of the HDR partition).</item>
    /// <item><c>DecodeFloat16</c>: HDR mode, the <c>decode_float16</c> mode: IEEE half floats (as raw bits); LDR
    /// endpoints give the UNORM16 value converted with round-to-zero (65535 is 1.0), HDR results that would be Inf / NaN
    /// become 0x7BFF.</item>
    /// </list>
    /// Illegal encodings (23.24, including void extents with min &gt;= max) decode to the error colour: opaque magenta
    /// (255, 0, 255, 255) in LDR mode, and in HDR mode the vector of NaNs 0xFFFF (Table 139).
    /// </para>
    /// </summary>
    internal static class AstcBlocks
    {
        enum Mode { Unorm8, Srgb8, Float16 }

        const ushort HalfOne = 0x3C00, HalfNaN = 0xFFFF;

        // ------------------------------------------------------------------ public entry points

        /// <summary>2D block to 8 bit RGBA (<paramref name="rgba"/>: bw * bh * 4 bytes).</summary>
        public static void DecodeUnorm8(ReadOnlySpan<byte> block, int bw, int bh, bool srgb, Span<byte> rgba)
            => Decode(block, bw, bh, 1, srgb ? Mode.Srgb8 : Mode.Unorm8, rgba, default);

        /// <summary>2D or 3D block to 8 bit RGBA (<paramref name="rgba"/>: bw * bh * bd * 4 bytes).</summary>
        public static void DecodeUnorm8(ReadOnlySpan<byte> block, int bw, int bh, int bd, bool srgb, Span<byte> rgba)
            => Decode(block, bw, bh, bd, srgb ? Mode.Srgb8 : Mode.Unorm8, rgba, default);

        /// <summary>2D block to half float RGBA, raw IEEE binary16 bits (<paramref name="rgba"/>: bw * bh * 4 values).</summary>
        public static void DecodeFloat16(ReadOnlySpan<byte> block, int bw, int bh, Span<ushort> rgba)
            => Decode(block, bw, bh, 1, Mode.Float16, default, rgba);

        /// <summary>2D or 3D block to half float RGBA, raw IEEE binary16 bits (bw * bh * bd * 4 values).</summary>
        public static void DecodeFloat16(ReadOnlySpan<byte> block, int bw, int bh, int bd, Span<ushort> rgba)
            => Decode(block, bw, bh, bd, Mode.Float16, default, rgba);

        /// <summary>As <see cref="DecodeFloat16(ReadOnlySpan{byte}, int, int, int, Span{ushort})"/>, widened to float
        /// (exactly).</summary>
        public static void DecodeFloat32(ReadOnlySpan<byte> block, int bw, int bh, int bd, Span<float> rgba)
        {
            int n = bw * bh * bd * 4;
            Span<ushort> h = stackalloc ushort[n];
            Decode(block, bw, bh, bd, Mode.Float16, default, h);
            for (int i = 0; i < n; i++) rgba[i] = (float)BitConverter.UInt16BitsToHalf(h[i]);
        }

        /// <summary>
        /// Whether a block needs the HDR profile: an HDR void extent, or a partition with an HDR colour endpoint mode (2, 3,
        /// 7, 11, 14, 15). Illegal blocks count as LDR.
        /// </summary>
        public static bool IsHdr(ReadOnlySpan<byte> block, bool is3D)
        {
            ulong lo = BinaryPrimitives.ReadUInt64LittleEndian(block), hi = BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(8));
            int bm = (int)lo & 0x7FF;
            if ((bm & 0x1FF) == 0x1FC) return (lo >> 9 & 1) != 0;
            int mi = (is3D ? Modes3D : Modes2D)[bm];
            if (mi < 0) return false;
            int gw = mi & 15, gh = mi >> 4 & 15, gd = mi >> 8 & 15, wq = mi >> 12 & 15;
            bool dual = (mi >> 16 & 1) != 0;
            int parts = ((int)(lo >> 11) & 3) + 1;
            int weightCount = gw * gh * gd * (dual ? 2 : 1);
            if (weightCount > 64) return false;
            int weightBits = IseBits(wq, weightCount);
            if (weightBits < 24 || weightBits > 96) return false;
            Span<int> cem = stackalloc int[4];
            if (parts == 1) cem[0] = (int)(lo >> 13) & 15;
            else
            {
                int sel = (int)(lo >> 23) & 3;
                if (sel == 0) for (int i = 0; i < parts; i++) cem[i] = (int)(lo >> 25) & 15;
                else
                {
                    int extra = 3 * parts - 4;
                    int field = (int)(lo >> 25) & 15 | Read(lo, hi, 128 - weightBits - extra, extra, 128) << 4;
                    for (int i = 0; i < parts; i++) cem[i] = (sel - 1 + (field >> i & 1)) << 2 | field >> (parts + 2 * i) & 3;
                }
            }
            for (int i = 0; i < parts; i++)
                if (cem[i] is 2 or 3 or 7 or 11 or 14 or 15) return true;
            return false;
        }

        // ------------------------------------------------------------------ quantization ranges

        // All ranges used by ASTC, ascending: maximum value, trits / quints, and the number of plain bits.
        // Indices 0..11 are the weight ranges in the order of Table 146 ((rho - 2) + 6 * P); 4..20 the colour ranges.
        internal static readonly byte[] QMax = { 1, 2, 3, 4, 5, 7, 9, 11, 15, 19, 23, 31, 39, 47, 63, 79, 95, 127, 159, 191, 255 };
        internal static readonly byte[] QKind = { 0, 1, 0, 2, 1, 0, 2, 1, 0, 2, 1, 0, 2, 1, 0, 2, 1, 0, 2, 1, 0 };   // 1 trit, 2 quint
        internal static readonly byte[] QBits = { 1, 0, 2, 0, 1, 3, 1, 2, 4, 2, 3, 5, 3, 4, 6, 4, 5, 7, 5, 6, 8 };

        /// <summary>Bits taken by an integer sequence of <paramref name="count"/> values in range <paramref name="q"/>.</summary>
        internal static int IseBits(int q, int count)
        {
            int n = QBits[q] * count;
            return QKind[q] switch { 1 => n + (8 * count + 4) / 5, 2 => n + (7 * count + 2) / 3, _ => n };
        }

        // ------------------------------------------------------------------ bit access

        /// <summary>n (&lt;= 8) bits from bit pos of the 128 bit value (lo, hi); bits at or beyond limit read as 0.</summary>
        static int Read(ulong lo, ulong hi, int pos, int n, int limit)
        {
            if (n == 0 || pos >= limit) return 0;
            ulong v = pos == 0 ? lo : pos >= 64 ? hi >> (pos - 64) : lo >> pos | hi << (64 - pos);
            int k = Math.Min(n, limit - pos);
            return (int)(v & ((1UL << k) - 1));
        }

        internal static ulong Reverse(ulong v)
        {
            v = (v >> 1) & 0x5555555555555555UL | (v & 0x5555555555555555UL) << 1;
            v = (v >> 2) & 0x3333333333333333UL | (v & 0x3333333333333333UL) << 2;
            v = (v >> 4) & 0x0F0F0F0F0F0F0F0FUL | (v & 0x0F0F0F0F0F0F0F0FUL) << 4;
            return BinaryPrimitives.ReverseEndianness(v);
        }

        // ------------------------------------------------------------------ integer sequence encoding (23.12)

        static void DecodeIse(ulong lo, ulong hi, int start, int q, int count, Span<int> o)
        {
            // the bits from start to the end of the sequence, as a 128 bit window shifted down as it is consumed (bits
            // past the end read as 0)
            int n = QBits[q], limit = start + IseBits(q, count);
            if (limit < 64) { lo &= (1UL << limit) - 1; hi = 0; }
            else if (limit < 128) hi &= (1UL << (limit - 64)) - 1;
            if (start >= 64) { lo = hi >> (start - 64); hi = 0; }
            else if (start > 0) { lo = lo >> start | hi << (64 - start); hi >>= start; }
            ulong mask = (1UL << n) - 1;
            switch (QKind[q])
            {
                case 0:
                    for (int i = 0; i < count; i++) o[i] = (int)Take(ref lo, ref hi, n, mask);
                    break;
                case 1:
                    for (int i = 0; i < count; i += 5)
                    {
                        int m0 = (int)Take(ref lo, ref hi, n, mask);
                        int T = (int)Take(ref lo, ref hi, 2, 3);
                        int m1 = (int)Take(ref lo, ref hi, n, mask);
                        T |= (int)Take(ref lo, ref hi, 2, 3) << 2;
                        int m2 = (int)Take(ref lo, ref hi, n, mask);
                        T |= (int)Take(ref lo, ref hi, 1, 1) << 4;
                        int m3 = (int)Take(ref lo, ref hi, n, mask);
                        T |= (int)Take(ref lo, ref hi, 2, 3) << 5;
                        int m4 = (int)Take(ref lo, ref hi, n, mask);
                        T |= (int)Take(ref lo, ref hi, 1, 1) << 7;
                        int tr = TritTable[T];
                        int r = Math.Min(5, count - i);
                        o[i] = (tr & 3) << n | m0;
                        if (r > 1) o[i + 1] = (tr >> 2 & 3) << n | m1;
                        if (r > 2) o[i + 2] = (tr >> 4 & 3) << n | m2;
                        if (r > 3) o[i + 3] = (tr >> 6 & 3) << n | m3;
                        if (r > 4) o[i + 4] = (tr >> 8 & 3) << n | m4;
                    }
                    break;
                default:
                    for (int i = 0; i < count; i += 3)
                    {
                        int m0 = (int)Take(ref lo, ref hi, n, mask);
                        int Q = (int)Take(ref lo, ref hi, 3, 7);
                        int m1 = (int)Take(ref lo, ref hi, n, mask);
                        Q |= (int)Take(ref lo, ref hi, 2, 3) << 3;
                        int m2 = (int)Take(ref lo, ref hi, n, mask);
                        Q |= (int)Take(ref lo, ref hi, 2, 3) << 5;
                        int qu = QuintTable[Q];
                        int r = Math.Min(3, count - i);
                        o[i] = (qu & 7) << n | m0;
                        if (r > 1) o[i + 1] = (qu >> 3 & 7) << n | m1;
                        if (r > 2) o[i + 2] = (qu >> 6 & 7) << n | m2;
                    }
                    break;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ulong Take(ref ulong lo, ref ulong hi, int n, ulong mask)
        {
            ulong v = lo & mask;
            if (n != 0) { lo = lo >> n | hi << (64 - n); hi >>= n; }
            return v;
        }

        // the 5 trits of a packed byte, 2 bits each; the 3 quints of a packed 7 bit value, 3 bits each
        static readonly int[] TritTable = BuildTrits(), QuintTable = BuildQuints();

        static int[] BuildTrits()
        {
            var r = new int[256];
            Span<int> t = stackalloc int[5];
            for (int T = 0; T < 256; T++)
            {
                Trits(T, t);
                r[T] = t[0] | t[1] << 2 | t[2] << 4 | t[3] << 6 | t[4] << 8;
            }
            return r;
        }

        static int[] BuildQuints()
        {
            var r = new int[128];
            Span<int> q = stackalloc int[3];
            for (int Q = 0; Q < 128; Q++)
            {
                Quints(Q, q);
                r[Q] = q[0] | q[1] << 3 | q[2] << 6;
            }
            return r;
        }

        static int Bit(int v, int i) => v >> i & 1;

        internal static void Trits(int T, Span<int> t)
        {
            int C;
            if ((T >> 2 & 7) == 7)
            {
                C = (T >> 5 & 7) << 2 | T & 3;
                t[4] = t[3] = 2;
            }
            else
            {
                C = T & 0x1F;
                if ((T >> 5 & 3) == 3) { t[4] = 2; t[3] = Bit(T, 7); }
                else { t[4] = Bit(T, 7); t[3] = T >> 5 & 3; }
            }
            if ((C & 3) == 3) { t[2] = 2; t[1] = Bit(C, 4); t[0] = Bit(C, 3) << 1 | Bit(C, 2) & ~Bit(C, 3) & 1; }
            else if ((C >> 2 & 3) == 3) { t[2] = 2; t[1] = 2; t[0] = C & 3; }
            else { t[2] = Bit(C, 4); t[1] = C >> 2 & 3; t[0] = Bit(C, 1) << 1 | Bit(C, 0) & ~Bit(C, 1) & 1; }
        }

        internal static void Quints(int Q, Span<int> q)
        {
            if ((Q >> 1 & 3) == 3 && (Q >> 5 & 3) == 0)
            {
                int q0 = Bit(Q, 0), nq0 = q0 ^ 1;
                q[2] = q0 << 2 | (Bit(Q, 4) & nq0) << 1 | Bit(Q, 3) & nq0;
                q[1] = q[0] = 4;
                return;
            }
            int C;
            if ((Q >> 1 & 3) == 3) { q[2] = 4; C = (Q >> 3 & 3) << 3 | (~Q >> 5 & 3) << 1 | Q & 1; }
            else { q[2] = Q >> 5 & 3; C = Q & 0x1F; }
            if ((C & 7) == 5) { q[1] = 4; q[0] = C >> 3 & 3; }
            else { q[1] = C >> 3 & 3; q[0] = C & 7; }
        }

        // ------------------------------------------------------------------ unquantization (23.13, 23.17)

        static int Replicate(int v, int n, int to)
        {
            int r = 0, s = to;
            while (s > 0)
            {
                s -= n;
                r |= s >= 0 ? v << s : v >> -s;
            }
            return r;
        }

        internal static int UnquantColor(int q, int v)
        {
            int n = QBits[q];
            if (QKind[q] == 0) return Replicate(v, n, 8);
            int D = v >> n, A = (v & 1) != 0 ? 0x1FF : 0;
            int b = Bit(v, 1), c = Bit(v, 2), d = Bit(v, 3), e = Bit(v, 4), f = Bit(v, 5);
            int B, C;
            switch (QMax[q])
            {
                case 5: B = 0; C = 204; break;
                case 9: B = 0; C = 113; break;
                case 11: B = b * 0x116; C = 93; break;
                case 19: B = b * 0x10C; C = 54; break;
                case 23: B = c * 0x10A + b * 0x085; C = 44; break;
                case 39: B = c * 0x105 + b * 0x082; C = 26; break;
                case 47: B = d * 0x104 + c * 0x082 + b * 0x041; C = 22; break;
                case 79: B = d * 0x102 + c * 0x081 + b * 0x040; C = 13; break;
                case 95: B = e * 0x102 + d * 0x081 + c * 0x040 + b * 0x020; C = 11; break;
                case 159: B = e * 0x101 + d * 0x080 + c * 0x040 + b * 0x020; C = 6; break;
                default: B = f * 0x101 + e * 0x080 + d * 0x040 + c * 0x020 + b * 0x010; C = 5; break;   // 191
            }
            int u = D * C + B;
            u ^= A;
            return (A & 0x80) | (u >> 2);
        }

        internal static int UnquantWeight(int q, int v)
        {
            int n = QBits[q], u;
            if (QKind[q] == 0) u = Replicate(v, n, 6);
            else if (n == 0) u = QMax[q] == 2 ? (v == 0 ? 0 : v == 1 ? 32 : 63) : v switch { 0 => 0, 1 => 16, 2 => 32, 3 => 47, _ => 63 };
            else
            {
                int D = v >> n, A = (v & 1) != 0 ? 0x7F : 0, b = Bit(v, 1), c = Bit(v, 2);
                int B, C;
                switch (QMax[q])
                {
                    case 5: B = 0; C = 50; break;
                    case 9: B = 0; C = 28; break;
                    case 11: B = b * 0x45; C = 23; break;
                    case 19: B = b * 0x42; C = 13; break;
                    default: B = c * 0x42 + b * 0x21; C = 11; break;   // 23
                }
                u = D * C + B;
                u ^= A;
                u = (A & 0x20) | (u >> 2);
            }
            return u > 32 ? u + 1 : u;
        }

        // ------------------------------------------------------------------ block mode (23.10)

        /// <summary>Weight grid size, weight range index and dual plane flag; false for reserved modes.</summary>
        internal static bool BlockMode(int m, bool is3D, out int gw, out int gh, out int gd, out int wq, out bool dual)
        {
            gw = gh = gd = 1; wq = 0; dual = false;
            int rho, p = Bit(m, 9);
            dual = Bit(m, 10) != 0;
            if (!is3D)
            {
                if ((m & 3) != 0)
                {
                    rho = Bit(m, 4) | (m & 3) << 1;
                    int a = m >> 5 & 3, b = m >> 7 & 3;
                    switch (m >> 2 & 3)
                    {
                        case 0: gw = b + 4; gh = a + 2; break;
                        case 1: gw = b + 8; gh = a + 2; break;
                        case 2: gw = a + 2; gh = b + 8; break;
                        default:
                            if (Bit(m, 8) == 0) { gw = a + 2; gh = Bit(m, 7) + 6; }
                            else { gw = Bit(m, 7) + 2; gh = a + 2; }
                            break;
                    }
                }
                else
                {
                    if ((m & 0xF) == 0) return false;
                    rho = Bit(m, 4) | (m >> 2 & 3) << 1;
                    int a = m >> 5 & 3;
                    switch (m >> 7 & 3)
                    {
                        case 0: gw = 12; gh = a + 2; break;
                        case 1: gw = a + 2; gh = 12; break;
                        case 2: gw = a + 6; gh = (m >> 9 & 3) + 6; p = 0; dual = false; break;
                        default:
                            if (Bit(m, 6) != 0) return false;
                            if (Bit(m, 5) == 0) { gw = 6; gh = 10; } else { gw = 10; gh = 6; }
                            break;
                    }
                }
            }
            else
            {
                if ((m & 3) != 0)
                {
                    rho = Bit(m, 4) | (m & 3) << 1;
                    gw = (m >> 5 & 3) + 2; gh = (m >> 7 & 3) + 2; gd = (m >> 2 & 3) + 2;
                }
                else
                {
                    if ((m & 0xF) == 0) return false;
                    rho = Bit(m, 4) | (m >> 2 & 3) << 1;
                    int a = m >> 5 & 3, c = m >> 9 & 3;
                    switch (m >> 7 & 3)
                    {
                        case 0: gw = 6; gh = c + 2; gd = a + 2; p = 0; dual = false; break;
                        case 1: gw = a + 2; gh = 6; gd = c + 2; p = 0; dual = false; break;
                        case 2: gw = a + 2; gh = c + 2; gd = 6; p = 0; dual = false; break;
                        default:
                            switch (a)
                            {
                                case 0: gw = 6; gh = 2; gd = 2; break;
                                case 1: gw = 2; gh = 6; gd = 2; break;
                                case 2: gw = 2; gh = 2; gd = 6; break;
                                default: return false;
                            }
                            break;
                    }
                }
            }
            wq = rho - 2 + 6 * p;
            return true;
        }

        // ------------------------------------------------------------------ partitions (23.21)

        static uint Hash52(uint p)
        {
            p ^= p >> 15; p -= p << 17; p += p << 7; p += p << 4;
            p ^= p >> 5; p += p << 16; p ^= p >> 7; p ^= p >> 3;
            p ^= p << 6; p ^= p >> 17;
            return p;
        }

        internal static int SelectPartition(int seed, int x, int y, int z, int count, bool small)
        {
            if (small) { x <<= 1; y <<= 1; z <<= 1; }
            seed += (count - 1) * 1024;
            uint rnum = Hash52((uint)seed);
            int s1 = (int)(rnum & 0xF), s2 = (int)(rnum >> 4 & 0xF), s3 = (int)(rnum >> 8 & 0xF), s4 = (int)(rnum >> 12 & 0xF);
            int s5 = (int)(rnum >> 16 & 0xF), s6 = (int)(rnum >> 20 & 0xF), s7 = (int)(rnum >> 24 & 0xF), s8 = (int)(rnum >> 28 & 0xF);
            int s9 = (int)(rnum >> 18 & 0xF), s10 = (int)(rnum >> 22 & 0xF), s11 = (int)(rnum >> 26 & 0xF);
            int s12 = (int)((rnum >> 30 | rnum << 2) & 0xF);
            s1 *= s1; s2 *= s2; s3 *= s3; s4 *= s4; s5 *= s5; s6 *= s6;
            s7 *= s7; s8 *= s8; s9 *= s9; s10 *= s10; s11 *= s11; s12 *= s12;
            int sh1, sh2;
            if ((seed & 1) != 0) { sh1 = (seed & 2) != 0 ? 4 : 5; sh2 = count == 3 ? 6 : 5; }
            else { sh1 = count == 3 ? 6 : 5; sh2 = (seed & 2) != 0 ? 4 : 5; }
            int sh3 = (seed & 0x10) != 0 ? sh1 : sh2;
            s1 >>= sh1; s2 >>= sh2; s3 >>= sh1; s4 >>= sh2; s5 >>= sh1; s6 >>= sh2; s7 >>= sh1; s8 >>= sh2;
            s9 >>= sh3; s10 >>= sh3; s11 >>= sh3; s12 >>= sh3;
            int a = (s1 * x + s2 * y + s11 * z + (int)(rnum >> 14)) & 0x3F;
            int b = (s3 * x + s4 * y + s12 * z + (int)(rnum >> 10)) & 0x3F;
            int c = (s5 * x + s6 * y + s9 * z + (int)(rnum >> 6)) & 0x3F;
            int d = (s7 * x + s8 * y + s10 * z + (int)(rnum >> 2)) & 0x3F;
            if (count < 4) d = 0;
            if (count < 3) c = 0;
            if (a >= b && a >= c && a >= d) return 0;
            if (b >= c && b >= d) return 1;
            if (c >= d) return 2;
            return 3;
        }

        // ------------------------------------------------------------------ endpoints (23.14, 23.15)

        // per partition: e0 (r, g, b, a), e1 (r, g, b, a); flags bit 0: RGB is HDR (12 bit), bit 1: alpha is HDR
        internal const int HdrRgb = 1, HdrA = 2;

        static void BitTransferSigned(ref int a, ref int b)
        {
            b >>= 1;
            b |= a & 0x80;
            a >>= 1;
            a &= 0x3F;
            if ((a & 0x20) != 0) a -= 0x40;
        }

        static void Set(Span<int> e, int r, int g, int b, int a) { e[0] = r; e[1] = g; e[2] = b; e[3] = a; }

        static void BlueContract(Span<int> e, int r, int g, int b, int a) { e[0] = (r + b) >> 1; e[1] = (g + b) >> 1; e[2] = b; e[3] = a; }

        static void ClampUnorm8(Span<int> e) { for (int i = 0; i < 4; i++) e[i] = Math.Clamp(e[i], 0, 255); }

        /// <summary>Decodes the endpoint pair of colour endpoint mode <paramref name="cem"/> from values v into e (8 ints);
        /// returns the HDR flags.</summary>
        internal static int Endpoints(int cem, ReadOnlySpan<int> v, Span<int> e)
        {
            Span<int> e0 = e.Slice(0, 4), e1 = e.Slice(4, 4);
            switch (cem)
            {
                case 0:
                    Set(e0, v[0], v[0], v[0], 0xFF); Set(e1, v[1], v[1], v[1], 0xFF);
                    return 0;
                case 1:
                    {
                        int l0 = (v[0] >> 2) | (v[1] & 0xC0), l1 = Math.Min(l0 + (v[1] & 0x3F), 0xFF);
                        Set(e0, l0, l0, l0, 0xFF); Set(e1, l1, l1, l1, 0xFF);
                        return 0;
                    }
                case 4:
                    Set(e0, v[0], v[0], v[0], v[2]); Set(e1, v[1], v[1], v[1], v[3]);
                    return 0;
                case 5:
                    {
                        int v0 = v[0], v1 = v[1], v2 = v[2], v3 = v[3];
                        BitTransferSigned(ref v1, ref v0); BitTransferSigned(ref v3, ref v2);
                        Set(e0, v0, v0, v0, v2); Set(e1, v0 + v1, v0 + v1, v0 + v1, v2 + v3);
                        ClampUnorm8(e0); ClampUnorm8(e1);
                        return 0;
                    }
                case 6:
                    Set(e0, v[0] * v[3] >> 8, v[1] * v[3] >> 8, v[2] * v[3] >> 8, 0xFF); Set(e1, v[0], v[1], v[2], 0xFF);
                    return 0;
                case 8:
                case 12:
                    {
                        int a0 = cem == 12 ? v[6] : 0xFF, a1 = cem == 12 ? v[7] : 0xFF;
                        if (v[1] + v[3] + v[5] >= v[0] + v[2] + v[4]) { Set(e0, v[0], v[2], v[4], a0); Set(e1, v[1], v[3], v[5], a1); }
                        else { BlueContract(e0, v[1], v[3], v[5], a1); BlueContract(e1, v[0], v[2], v[4], a0); }
                        return 0;
                    }
                case 9:
                case 13:
                    {
                        int v0 = v[0], v1 = v[1], v2 = v[2], v3 = v[3], v4 = v[4], v5 = v[5];
                        int v6 = cem == 13 ? v[6] : 0xFF, v7 = cem == 13 ? v[7] : 0;
                        BitTransferSigned(ref v1, ref v0); BitTransferSigned(ref v3, ref v2); BitTransferSigned(ref v5, ref v4);
                        if (cem == 13) BitTransferSigned(ref v7, ref v6);
                        if (v1 + v3 + v5 >= 0) { Set(e0, v0, v2, v4, v6); Set(e1, v0 + v1, v2 + v3, v4 + v5, v6 + v7); }
                        else { BlueContract(e0, v0 + v1, v2 + v3, v4 + v5, v6 + v7); BlueContract(e1, v0, v2, v4, v6); }
                        ClampUnorm8(e0); ClampUnorm8(e1);
                        return 0;
                    }
                case 10:
                    Set(e0, v[0] * v[3] >> 8, v[1] * v[3] >> 8, v[2] * v[3] >> 8, v[4]); Set(e1, v[0], v[1], v[2], v[5]);
                    return 0;
                case 2:
                    {
                        int y0, y1;
                        if (v[1] >= v[0]) { y0 = v[0] << 4; y1 = v[1] << 4; }
                        else { y0 = (v[1] << 4) + 8; y1 = (v[0] << 4) - 8; }
                        Set(e0, y0, y0, y0, 0x780); Set(e1, y1, y1, y1, 0x780);
                        return HdrRgb | HdrA;
                    }
                case 3:
                    {
                        int y0, d;
                        if ((v[0] & 0x80) != 0) { y0 = (v[1] & 0xE0) << 4 | (v[0] & 0x7F) << 2; d = (v[1] & 0x1F) << 2; }
                        else { y0 = (v[1] & 0xF0) << 4 | (v[0] & 0x7F) << 1; d = (v[1] & 0x0F) << 1; }
                        int y1 = Math.Min(y0 + d, 0xFFF);
                        Set(e0, y0, y0, y0, 0x780); Set(e1, y1, y1, y1, 0x780);
                        return HdrRgb | HdrA;
                    }
                case 7:
                    HdrMode7(v, e0, e1);
                    return HdrRgb | HdrA;
                case 11:
                    HdrMode11(v, e0, e1);
                    return HdrRgb | HdrA;
                case 14:
                    HdrMode11(v, e0, e1);
                    e0[3] = v[6]; e1[3] = v[7];
                    return HdrRgb;
                default: // 15
                    {
                        HdrMode11(v, e0, e1);
                        int mode = (v[6] >> 7 & 1) | (v[7] >> 6 & 2);
                        int v6 = v[6] & 0x7F, v7 = v[7] & 0x7F;
                        if (mode == 3) { e0[3] = v6 << 5; e1[3] = v7 << 5; }
                        else
                        {
                            v6 |= (v7 << (mode + 1)) & 0x780;
                            v7 &= 0x3F >> mode;
                            v7 ^= 0x20 >> mode;
                            v7 -= 0x20 >> mode;
                            v6 <<= 4 - mode; v7 <<= 4 - mode;
                            v7 += v6;
                            v7 = Math.Clamp(v7, 0, 0xFFF);
                            e0[3] = v6; e1[3] = v7;
                        }
                        return HdrRgb | HdrA;
                    }
            }
        }

        static void HdrMode7(ReadOnlySpan<int> v, Span<int> e0, Span<int> e1)
        {
            int v0 = v[0], v1 = v[1], v2 = v[2], v3 = v[3];
            int modeval = (v0 & 0xC0) >> 6 | (v1 & 0x80) >> 5 | (v2 & 0x80) >> 4;
            int majcomp, mode;
            if ((modeval & 0xC) != 0xC) { majcomp = modeval >> 2; mode = modeval & 3; }
            else if (modeval != 0xF) { majcomp = modeval & 3; mode = 4; }
            else { majcomp = 0; mode = 5; }

            int red = v0 & 0x3F, green = v1 & 0x1F, blue = v2 & 0x1F, scale = v3 & 0x1F;
            int x0 = v1 >> 6 & 1, x1 = v1 >> 5 & 1, x2 = v2 >> 6 & 1, x3 = v2 >> 5 & 1;
            int x4 = v3 >> 7 & 1, x5 = v3 >> 6 & 1, x6 = v3 >> 5 & 1;

            int ohm = 1 << mode;
            if ((ohm & 0x30) != 0) green |= x0 << 6;
            if ((ohm & 0x3A) != 0) green |= x1 << 5;
            if ((ohm & 0x30) != 0) blue |= x2 << 6;
            if ((ohm & 0x3A) != 0) blue |= x3 << 5;
            if ((ohm & 0x3D) != 0) scale |= x6 << 5;
            if ((ohm & 0x2D) != 0) scale |= x5 << 6;
            if ((ohm & 0x04) != 0) scale |= x4 << 7;
            if ((ohm & 0x3B) != 0) red |= x4 << 6;
            if ((ohm & 0x04) != 0) red |= x3 << 6;
            if ((ohm & 0x10) != 0) red |= x5 << 7;
            if ((ohm & 0x0F) != 0) red |= x2 << 7;
            if ((ohm & 0x05) != 0) red |= x1 << 8;
            if ((ohm & 0x0A) != 0) red |= x0 << 8;
            if ((ohm & 0x05) != 0) red |= x0 << 9;
            if ((ohm & 0x02) != 0) red |= x6 << 9;
            if ((ohm & 0x01) != 0) red |= x3 << 10;
            if ((ohm & 0x02) != 0) red |= x5 << 10;

            int shamt = mode switch { 0 => 1, 1 => 1, 2 => 2, 3 => 3, 4 => 4, _ => 5 };
            red <<= shamt; green <<= shamt; blue <<= shamt; scale <<= shamt;
            if (mode != 5) { green = red - green; blue = red - blue; }
            if (majcomp == 1) (red, green) = (green, red);
            if (majcomp == 2) (red, blue) = (blue, red);

            e1[0] = Math.Clamp(red, 0, 0xFFF); e1[1] = Math.Clamp(green, 0, 0xFFF); e1[2] = Math.Clamp(blue, 0, 0xFFF); e1[3] = 0x780;
            e0[0] = Math.Clamp(red - scale, 0, 0xFFF); e0[1] = Math.Clamp(green - scale, 0, 0xFFF); e0[2] = Math.Clamp(blue - scale, 0, 0xFFF); e0[3] = 0x780;
        }

        static int SignExtend(int v, int bits) => v << (32 - bits) >> (32 - bits);

        static void HdrMode11(ReadOnlySpan<int> v, Span<int> e0, Span<int> e1)
        {
            int v0 = v[0], v1 = v[1], v2 = v[2], v3 = v[3], v4 = v[4], v5 = v[5];
            int majcomp = (v4 & 0x80) >> 7 | (v5 & 0x80) >> 6;
            if (majcomp == 3)
            {
                e0[0] = v0 << 4; e0[1] = v2 << 4; e0[2] = (v4 & 0x7F) << 5; e0[3] = 0x780;
                e1[0] = v1 << 4; e1[1] = v3 << 4; e1[2] = (v5 & 0x7F) << 5; e1[3] = 0x780;
                return;
            }
            int mode = (v1 & 0x80) >> 7 | (v2 & 0x80) >> 6 | (v3 & 0x80) >> 5;
            int va = v0 | (v1 & 0x40) << 2;
            int vb0 = v2 & 0x3F, vb1 = v3 & 0x3F, vc = v1 & 0x3F;
            int dbits = mode switch { 0 => 7, 1 => 6, 2 => 7, 3 => 6, 4 => 5, 5 => 6, 6 => 5, _ => 6 };
            int vd0 = SignExtend(v4 & 0x7F, dbits), vd1 = SignExtend(v5 & 0x7F, dbits);

            int x0 = v2 >> 6 & 1, x1 = v3 >> 6 & 1, x2 = v4 >> 6 & 1, x3 = v5 >> 6 & 1, x4 = v4 >> 5 & 1, x5 = v5 >> 5 & 1;
            int ohm = 1 << mode;
            if ((ohm & 0xA4) != 0) va |= x0 << 9;
            if ((ohm & 0x08) != 0) va |= x2 << 9;
            if ((ohm & 0x50) != 0) va |= x4 << 9;
            if ((ohm & 0x50) != 0) va |= x5 << 10;
            if ((ohm & 0xA0) != 0) va |= x1 << 10;
            if ((ohm & 0xC0) != 0) va |= x2 << 11;
            if ((ohm & 0x04) != 0) vc |= x1 << 6;
            if ((ohm & 0xE8) != 0) vc |= x3 << 6;
            if ((ohm & 0x20) != 0) vc |= x2 << 7;
            if ((ohm & 0x5B) != 0) vb0 |= x0 << 6;
            if ((ohm & 0x5B) != 0) vb1 |= x1 << 6;
            if ((ohm & 0x12) != 0) vb0 |= x2 << 7;
            if ((ohm & 0x12) != 0) vb1 |= x3 << 7;

            // the specification's listing writes 'modeval' here; the mode bits m2..0 decoded above are meant
            int shamt = (mode >> 1) ^ 3;
            va <<= shamt; vb0 <<= shamt; vb1 <<= shamt; vc <<= shamt; vd0 <<= shamt; vd1 <<= shamt;

            e1[0] = Math.Clamp(va, 0, 0xFFF);
            e1[1] = Math.Clamp(va - vb0, 0, 0xFFF);
            e1[2] = Math.Clamp(va - vb1, 0, 0xFFF);
            e1[3] = 0x780;
            e0[0] = Math.Clamp(va - vc, 0, 0xFFF);
            e0[1] = Math.Clamp(va - vb0 - vc - vd0, 0, 0xFFF);
            e0[2] = Math.Clamp(va - vb1 - vc - vd1, 0, 0xFFF);
            e0[3] = 0x780;
            if (majcomp == 1) { (e0[0], e0[1]) = (e0[1], e0[0]); (e1[0], e1[1]) = (e1[1], e1[0]); }
            else if (majcomp == 2) { (e0[0], e0[2]) = (e0[2], e0[0]); (e1[0], e1[2]) = (e1[2], e1[0]); }
        }

        // ------------------------------------------------------------------ output conversions (23.19)

        /// <summary>A UNORM16 interpolation result to half float, rounding toward zero (65535 is exactly 1.0).</summary>
        internal static ushort Unorm16ToHalf(int c)
        {
            if (c == 65535) return HalfOne;
            if (c < 4) return (ushort)(c << 8);   // subnormal: c / 65536 = (c * 256) * 2^-24
            int p = 31 - BitOperations.LeadingZeroCount((uint)c);   // 2..15
            int mant = p >= 10 ? c >> (p - 10) : c << (10 - p);
            return (ushort)((p - 1) << 10 | mant & 0x3FF);
        }

        /// <summary>Half float in 0..1 to 8 bits, rounded to nearest (255 x is never halfway for a binary16 x).</summary>
        internal static byte HalfToUnorm8(ushort h) => (byte)Math.Round((double)BitConverter.UInt16BitsToHalf(h) * 255);

        /// <summary>An interpolated HDR value (16 bit, from 12 bit endpoints shifted up by 4) to half float.</summary>
        internal static ushort HdrToHalf(int c)
        {
            int e = c >> 11, m = c & 0x7FF;
            int mt = m < 512 ? 3 * m : m >= 1536 ? 5 * m - 2048 : 4 * m - 512;
            int cf = (e << 10) + (mt >> 3);
            return (ushort)(cf >= 0x7C00 ? 0x7BFF : cf);
        }

        static void Error(int texels, Mode mode, Span<byte> o8, Span<ushort> o16)
        {
            for (int i = 0; i < texels; i++)
                if (mode == Mode.Float16) o16.Slice(i * 4, 4).Fill(HalfNaN);
                else { o8[i * 4] = 255; o8[i * 4 + 1] = 0; o8[i * 4 + 2] = 255; o8[i * 4 + 3] = 255; }
        }

        // ------------------------------------------------------------------ block decoding

        [System.Runtime.CompilerServices.SkipLocalsInit]
        static void Decode(ReadOnlySpan<byte> block, int bw, int bh, int bd, Mode mode, Span<byte> o8, Span<ushort> o16)
        {
            int texels = bw * bh * bd;
            bool is3D = bd > 1;
            ulong lo = BinaryPrimitives.ReadUInt64LittleEndian(block), hi = BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(8));
            int bm = (int)lo & 0x7FF;

            if ((bm & 0x1FF) == 0x1FC)
            {
                VoidExtent(lo, hi, is3D, texels, mode, o8, o16);
                return;
            }
            int mi = (is3D ? Modes3D : Modes2D)[bm];
            int gw = mi & 15, gh = mi >> 4 & 15, gd = mi >> 8 & 15, wq = mi >> 12 & 15;
            bool dual = (mi >> 16 & 1) != 0;
            if (mi < 0 || gw > bw || gh > bh || gd > bd)
            {
                Error(texels, mode, o8, o16);
                return;
            }
            int parts = ((int)(lo >> 11) & 3) + 1;
            int gridCount = gw * gh * gd, weightCount = gridCount * (dual ? 2 : 1);
            if (weightCount > 64 || dual && parts == 4)
            {
                Error(texels, mode, o8, o16);
                return;
            }
            int weightBits = IseBits(wq, weightCount);
            if (weightBits < 24 || weightBits > 96)
            {
                Error(texels, mode, o8, o16);
                return;
            }

            // colour endpoint modes and the configuration size (23.11, 23.22)
            Span<int> cem = stackalloc int[4];
            int seed = 0, configBits, extraCemBits = 0;
            if (parts == 1)
            {
                cem[0] = (int)(lo >> 13) & 15;
                configBits = 17;
            }
            else
            {
                seed = (int)(lo >> 13) & 0x3FF;
                int sel = (int)(lo >> 23) & 3;
                if (sel == 0)
                {
                    for (int i = 0; i < parts; i++) cem[i] = (int)(lo >> 25) & 15;
                    configBits = 29;
                }
                else
                {
                    extraCemBits = 3 * parts - 4;
                    int field = (int)(lo >> 25) & 15 | Read(lo, hi, 128 - weightBits - extraCemBits, extraCemBits, 128) << 4;
                    for (int i = 0; i < parts; i++)
                        cem[i] = (sel - 1 + (field >> i & 1)) << 2 | field >> (parts + 2 * i) & 3;
                    configBits = 25 + 3 * parts;
                }
            }
            int ccs = 0;
            if (dual)
            {
                ccs = Read(lo, hi, 128 - weightBits - extraCemBits - 2, 2, 128);
                configBits += 2;
            }
            int colorCount = 0;
            for (int i = 0; i < parts; i++) colorCount += ((cem[i] >> 2) + 1) * 2;
            int remaining = 128 - configBits - weightBits;
            if (colorCount > 18 || remaining < (13 * colorCount + 4) / 5)
            {
                Error(texels, mode, o8, o16);
                return;
            }
            int cq = 20;
            while (IseBits(cq, colorCount) > remaining) cq--;

            // endpoint values and endpoints
            Span<int> values = stackalloc int[18];
            DecodeIse(lo, hi, parts == 1 ? 17 : 29, cq, colorCount, values);
            for (int i = 0; i < colorCount; i++) values[i] = ColorUnq[cq * 256 + values[i]];
            Span<int> ep = stackalloc int[32];
            Span<int> flags = stackalloc int[4];
            flags.Clear();
            for (int i = 0, k = 0; i < parts; i++)
            {
                flags[i] = Endpoints(cem[i], values.Slice(k), ep.Slice(i * 8, 8));
                k += ((cem[i] >> 2) + 1) * 2;
            }

            // weights, stored bit-reversed from the top of the block; padded so the infill may read past the grid edge
            Span<int> raw = stackalloc int[64];
            DecodeIse(Reverse(hi), Reverse(lo), 0, wq, weightCount, raw);
            // the infill may read past the grid (with weight 0): the arrays are padded with zeros
            Span<int> w0 = stackalloc int[160];
            Span<int> w1 = stackalloc int[160];
            int pad = Math.Min(160, gridCount + gw * gh + gw + 2) - gridCount;
            w0.Slice(gridCount, pad).Clear();
            if (dual) w1.Slice(gridCount, pad).Clear();
            int ub = wq * 64;
            for (int i = 0; i < gridCount; i++)
            {
                if (dual)
                {
                    w0[i] = WeightUnq[ub + raw[2 * i]];
                    w1[i] = WeightUnq[ub + raw[2 * i + 1]];
                }
                else w0[i] = WeightUnq[ub + raw[i]];
            }

            var fp = GetFootprint(bw, bh, bd);
            int[] tab = InfillTable(fp, gw, gh, gd);
            byte[]? pmap = parts == 1 ? null : PartitionTable(fp, parts, seed);

            if (mode == Mode.Unorm8 && (flags[0] | flags[1] | flags[2] | flags[3]) == 0 && o8.Length >= texels * 4)
            {
                DecodeLdr8(ep, parts, w0, w1, dual, ccs, tab, pmap, texels, o8);
                return;
            }
            if (mode == Mode.Float16 && o16.Length >= texels * 4)
            {
                DecodeF16(ep, flags, parts, w0, w1, dual, ccs, tab, pmap, texels, o16);
                return;
            }

            for (int t = 0, ti = 0; t < texels; t++, ti += 8)
                    {
                        int part = pmap == null ? 0 : pmap[t];
                        int f = flags[part];
                        if (f != 0 && mode != Mode.Float16)
                        {
                            Error(1, mode, o8.Slice(t * 4), o16);
                            continue;
                        }
                        int i0 = (w0[tab[ti]] * tab[ti + 4] + w0[tab[ti + 1]] * tab[ti + 5] + w0[tab[ti + 2]] * tab[ti + 6] + w0[tab[ti + 3]] * tab[ti + 7] + 8) >> 4;
                        int i1 = dual ? (w1[tab[ti]] * tab[ti + 4] + w1[tab[ti + 1]] * tab[ti + 5] + w1[tab[ti + 2]] * tab[ti + 6] + w1[tab[ti + 3]] * tab[ti + 7] + 8) >> 4 : 0;
                        Span<int> e = ep.Slice(part * 8, 8);
                        for (int c = 0; c < 4; c++)
                        {
                            int w = dual && c == ccs ? i1 : i0;
                            bool hdr = (f & (c == 3 ? HdrA : HdrRgb)) != 0;
                            int c0 = e[c], c1 = e[c + 4];
                            if (hdr)
                            {
                                int v = ((c0 << 4) * (64 - w) + (c1 << 4) * w + 32) >> 6;
                                o16[t * 4 + c] = HdrToHalf(v);
                                continue;
                            }
                            if (mode == Mode.Srgb8 && c < 3) { c0 = c0 << 8 | 0x80; c1 = c1 << 8 | 0x80; }
                            else { c0 = c0 << 8 | c0; c1 = c1 << 8 | c1; }
                            int u = (c0 * (64 - w) + c1 * w + 32) >> 6;
                            if (mode == Mode.Float16) o16[t * 4 + c] = Unorm16ToHalf(u);
                            else if (mode == Mode.Srgb8 && c == 3) o8[t * 4 + c] = HalfToUnorm8(Unorm16ToHalf(u));
                            else o8[t * 4 + c] = (byte)(u >> 8);
                        }
                    }
        }

        /// <summary>The LDR, 8 bit output texel loop: all channels of a texel at once.</summary>
        static unsafe void DecodeLdr8(ReadOnlySpan<int> ep, int parts, ReadOnlySpan<int> w0, ReadOnlySpan<int> w1, bool dual, int ccs,
            int[] tab, byte[]? pmap, int texels, Span<byte> o8)
        {
            Span<Vector128<int>> e0 = stackalloc Vector128<int>[4], e1 = stackalloc Vector128<int>[4];
            for (int p = 0; p < parts; p++)
            {
                Vector128<int> a = Vector128.Create(ep[p * 8], ep[p * 8 + 1], ep[p * 8 + 2], ep[p * 8 + 3]);
                Vector128<int> b = Vector128.Create(ep[p * 8 + 4], ep[p * 8 + 5], ep[p * 8 + 6], ep[p * 8 + 7]);
                e0[p] = (a << 8) | a;
                e1[p] = (b << 8) | b;
            }
            // lanes of the second plane's channel
            Vector128<int> sel = dual ? Vector128.Equals(Vector128.Create(0, 1, 2, 3), Vector128.Create(ccs)) : Vector128<int>.Zero;
            Vector128<int> v64 = Vector128.Create(64), v32 = Vector128.Create(32);
            fixed (int* tp = tab, pw0 = w0, pw1 = w1)
            fixed (byte* op = o8)
            {
                int* tt = tp;
                for (int t = 0; t < texels; t++, tt += 8)
                {
                    int i0 = (pw0[tt[0]] * tt[4] + pw0[tt[1]] * tt[5] + pw0[tt[2]] * tt[6] + pw0[tt[3]] * tt[7] + 8) >> 4;
                    Vector128<int> w = Vector128.Create(i0);
                    if (dual)
                    {
                        int i1 = (pw1[tt[0]] * tt[4] + pw1[tt[1]] * tt[5] + pw1[tt[2]] * tt[6] + pw1[tt[3]] * tt[7] + 8) >> 4;
                        w = Vector128.ConditionalSelect(sel, Vector128.Create(i1), w);
                    }
                    int p = pmap == null ? 0 : pmap[t];
                    Vector128<int> u = (e0[p] * (v64 - w) + e1[p] * w + v32) >> 14;
                    Vector128<byte> b8 = Vector128.Narrow(Vector128.Narrow(u.AsUInt32(), u.AsUInt32()), Vector128<ushort>.Zero);
                    Unsafe.WriteUnaligned(op + t * 4, b8.AsUInt32().ToScalar());
                }
            }
        }

        // UNORM16 interpolation result to half (round toward zero), HDR interpolation result to half
        static readonly ushort[] UnormHalf = BuildHalfTable(false), HdrHalf = BuildHalfTable(true);

        static ushort[] BuildHalfTable(bool hdr)
        {
            var t = new ushort[65536];
            for (int i = 0; i < 65536; i++) t[i] = hdr ? HdrToHalf(i) : Unorm16ToHalf(i);
            return t;
        }

        /// <summary>The half float output texel loop (LDR and HDR endpoints).</summary>
        static unsafe void DecodeF16(ReadOnlySpan<int> ep, ReadOnlySpan<int> flags, int parts, ReadOnlySpan<int> w0, ReadOnlySpan<int> w1,
            bool dual, int ccs, int[] tab, byte[]? pmap, int texels, Span<ushort> o16)
        {
            Span<Vector128<int>> e0 = stackalloc Vector128<int>[4], e1 = stackalloc Vector128<int>[4];
            Span<int> hdrMask = stackalloc int[4];   // per partition: bit c set when channel c is HDR
            Span<int> a = stackalloc int[4], b = stackalloc int[4];
            for (int p = 0; p < parts; p++)
            {
                int m = 0;
                for (int c = 0; c < 4; c++)
                {
                    bool h = (flags[p] & (c == 3 ? HdrA : HdrRgb)) != 0;
                    int x = ep[p * 8 + c], y = ep[p * 8 + 4 + c];
                    if (h) { a[c] = x << 4; b[c] = y << 4; m |= 1 << c; }
                    else { a[c] = x << 8 | x; b[c] = y << 8 | y; }
                }
                e0[p] = Vector128.Create(a[0], a[1], a[2], a[3]);
                e1[p] = Vector128.Create(b[0], b[1], b[2], b[3]);
                hdrMask[p] = m;
            }
            Vector128<int> sel = dual ? Vector128.Equals(Vector128.Create(0, 1, 2, 3), Vector128.Create(ccs)) : Vector128<int>.Zero;
            Vector128<int> v64 = Vector128.Create(64), v32 = Vector128.Create(32);
            fixed (int* tp = tab, pw0 = w0, pw1 = w1)
            fixed (ushort* op = o16, hh = HdrHalf, uh = UnormHalf)
            {
                int* tt = tp;
                for (int t = 0; t < texels; t++, tt += 8)
                {
                    int i0 = (pw0[tt[0]] * tt[4] + pw0[tt[1]] * tt[5] + pw0[tt[2]] * tt[6] + pw0[tt[3]] * tt[7] + 8) >> 4;
                    Vector128<int> w = Vector128.Create(i0);
                    if (dual)
                    {
                        int i1 = (pw1[tt[0]] * tt[4] + pw1[tt[1]] * tt[5] + pw1[tt[2]] * tt[6] + pw1[tt[3]] * tt[7] + 8) >> 4;
                        w = Vector128.ConditionalSelect(sel, Vector128.Create(i1), w);
                    }
                    int p = pmap == null ? 0 : pmap[t], m = hdrMask[p];
                    Vector128<int> u = (e0[p] * (v64 - w) + e1[p] * w + v32) >> 6;
                    ushort* o = op + t * 4;
                    o[0] = ((m & 1) != 0 ? hh : uh)[u.GetElement(0)];
                    o[1] = ((m & 2) != 0 ? hh : uh)[u.GetElement(1)];
                    o[2] = ((m & 4) != 0 ? hh : uh)[u.GetElement(2)];
                    o[3] = ((m & 8) != 0 ? hh : uh)[u.GetElement(3)];
                }
            }
        }

        // ------------------------------------------------------------------ cached tables

        // block mode (2D / 3D) -> gw | gh << 4 | gd << 8 | wq << 12 | dual << 16, or -1 for reserved modes
        static readonly int[] Modes2D = BuildModes(false), Modes3D = BuildModes(true);
        static readonly int[] WeightUnq = BuildWeightUnq(), ColorUnq = BuildColorUnq();   // [range * 64 + v], [range * 256 + v]

        static int[] BuildModes(bool is3D)
        {
            var m = new int[2048];
            for (int i = 0; i < 2048; i++)
                m[i] = BlockMode(i, is3D, out int gw, out int gh, out int gd, out int wq, out bool dual)
                    ? gw | gh << 4 | gd << 8 | wq << 12 | (dual ? 1 << 16 : 0) : -1;
            return m;
        }

        static int[] BuildWeightUnq()
        {
            var t = new int[12 * 64];
            for (int q = 0; q < 12; q++) for (int v = 0; v <= QMax[q]; v++) t[q * 64 + v] = UnquantWeight(q, v);
            return t;
        }

        static int[] BuildColorUnq()
        {
            var t = new int[21 * 256];
            for (int q = 0; q < 21; q++) for (int v = 0; v <= QMax[q]; v++) t[q * 256 + v] = UnquantColor(q, v);
            return t;
        }

        /// <summary>Per footprint: the infill taps of every weight grid and the partition of every texel per seed, built on
        /// first use.</summary>
        sealed class Footprint
        {
            public int Bw, Bh, Bd;
            public readonly int[]?[] Infill = new int[]?[13 * 13 * 7];
            public readonly byte[]?[] Parts = new byte[]?[3 * 1024];
        }

        static readonly ConcurrentDictionary<int, Footprint> Footprints = new();
        [ThreadStatic] static Footprint? lastFootprint;

        static Footprint GetFootprint(int bw, int bh, int bd)
        {
            var f = lastFootprint;
            if (f != null && f.Bw == bw && f.Bh == bh && f.Bd == bd) return f;
            f = Footprints.GetOrAdd(bw << 16 | bh << 8 | bd, _ => new Footprint { Bw = bw, Bh = bh, Bd = bd });
            lastFootprint = f;
            return f;
        }

        /// <summary>Per texel: the 4 grid indices and their weights (sum 16) of the infill (23.18), as the decoder
        /// computes them; indices of weight 0 may point past the grid.</summary>
        static int[] InfillTable(Footprint f, int gw, int gh, int gd)
        {
            int key = (gw * 13 + gh) * 7 + gd;
            var tab = f.Infill[key];
            if (tab != null) return tab;
            int bw = f.Bw, bh = f.Bh, bd = f.Bd;
            bool is3D = bd > 1;
            tab = new int[bw * bh * bd * 8];
            int ds = (1024 + bw / 2) / (bw - 1), dt = (1024 + bh / 2) / (bh - 1), dr = is3D ? (1024 + bd / 2) / (bd - 1) : 0;
            for (int z = 0, t = 0; z < bd; z++)
                for (int y = 0; y < bh; y++)
                    for (int x = 0; x < bw; x++, t++)
                    {
                        int gs = (ds * x * (gw - 1) + 32) >> 6, gt = (dt * y * (gh - 1) + 32) >> 6, gr = (dr * z * (gd - 1) + 32) >> 6;
                        int js = gs >> 4, fs = gs & 15, jt = gt >> 4, ft = gt & 15;
                        Span<int> e = tab.AsSpan(t * 8, 8);
                        if (!is3D)
                        {
                            int v0 = js + jt * gw;
                            int w11 = (fs * ft + 8) >> 4, w10 = ft - w11, w01 = fs - w11, w00 = 16 - fs - ft + w11;
                            e[0] = v0; e[1] = v0 + 1; e[2] = v0 + gw; e[3] = v0 + gw + 1;
                            e[4] = w00; e[5] = w01; e[6] = w10; e[7] = w11;
                            continue;
                        }
                        int jr = gr >> 4, fr = gr & 15, plane = gw * gh;
                        int s1, s2, a0, a1, a2, a3;
                        if (fs > ft && ft > fr) { s1 = 1; s2 = gw; a0 = 16 - fs; a1 = fs - ft; a2 = ft - fr; a3 = fr; }
                        else if (fs > ft && fs > fr) { s1 = 1; s2 = plane; a0 = 16 - fs; a1 = fs - fr; a2 = fr - ft; a3 = ft; }
                        else if (fs > ft) { s1 = plane; s2 = 1; a0 = 16 - fr; a1 = fr - fs; a2 = fs - ft; a3 = ft; }
                        else if (ft > fr && fs > fr) { s1 = gw; s2 = 1; a0 = 16 - ft; a1 = ft - fs; a2 = fs - fr; a3 = fr; }
                        else if (ft > fr) { s1 = gw; s2 = plane; a0 = 16 - ft; a1 = ft - fr; a2 = fr - fs; a3 = fs; }
                        else { s1 = plane; s2 = gw; a0 = 16 - fr; a1 = fr - ft; a2 = ft - fs; a3 = fs; }
                        int b0 = js + jt * gw + jr * plane;
                        e[0] = b0; e[1] = b0 + s1; e[2] = b0 + s1 + s2; e[3] = b0 + plane + gw + 1;
                        e[4] = a0; e[5] = a1; e[6] = a2; e[7] = a3;
                    }
            f.Infill[key] = tab;
            return tab;
        }

        /// <summary>The partition of every texel for a partition count and seed (23.21).</summary>
        static byte[] PartitionTable(Footprint f, int parts, int seed)
        {
            int key = (parts - 2) * 1024 + seed;
            var tab = f.Parts[key];
            if (tab != null) return tab;
            int bw = f.Bw, bh = f.Bh, bd = f.Bd;
            tab = new byte[bw * bh * bd];
            bool small = tab.Length < 31;
            for (int z = 0, t = 0; z < bd; z++)
                for (int y = 0; y < bh; y++)
                    for (int x = 0; x < bw; x++, t++) tab[t] = (byte)SelectPartition(seed, x, y, z, parts, small);
            f.Parts[key] = tab;
            return tab;
        }

        static void VoidExtent(ulong lo, ulong hi, bool is3D, int texels, Mode mode, Span<byte> o8, Span<ushort> o16)
        {
            bool hdr = (lo >> 9 & 1) != 0;
            bool bad;
            if (!is3D)
            {
                int s0 = (int)(lo >> 12) & 0x1FFF, s1 = (int)(lo >> 25) & 0x1FFF, t0 = (int)(lo >> 38) & 0x1FFF, t1 = (int)(lo >> 51) & 0x1FFF;
                bool all1 = (s0 & s1 & t0 & t1) == 0x1FFF;
                bad = (lo >> 10 & 3) != 3 || !all1 && (s0 >= s1 || t0 >= t1);
            }
            else
            {
                int s0 = (int)(lo >> 10) & 0x1FF, s1 = (int)(lo >> 19) & 0x1FF, t0 = (int)(lo >> 28) & 0x1FF;
                int t1 = (int)(lo >> 37) & 0x1FF, r0 = (int)(lo >> 46) & 0x1FF, r1 = (int)(lo >> 55) & 0x1FF;
                bool all1 = (s0 & s1 & t0 & t1 & r0 & r1) == 0x1FF;
                bad = !all1 && (s0 >= s1 || t0 >= t1 || r0 >= r1);
            }
            if (bad || hdr && mode != Mode.Float16)
            {
                Error(texels, mode, o8, o16);
                return;
            }
            Span<int> c = stackalloc int[4];
            for (int i = 0; i < 4; i++) c[i] = (int)(hi >> (16 * i)) & 0xFFFF;
            for (int t = 0; t < texels; t++)
                for (int i = 0; i < 4; i++)
                    if (mode == Mode.Float16) o16[t * 4 + i] = hdr ? (ushort)c[i] : Unorm16ToHalf(c[i]);
                    else o8[t * 4 + i] = mode == Mode.Srgb8 && i == 3 ? HalfToUnorm8(Unorm16ToHalf(c[i])) : (byte)(c[i] >> 8);
        }
    }
}
