using System;
using System.Buffers.Binary;
using System.Numerics;

namespace PixCodec
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

        // ------------------------------------------------------------------ quantization ranges

        // All ranges used by ASTC, ascending: maximum value, trits / quints, and the number of plain bits.
        // Indices 0..11 are the weight ranges in the order of Table 146 ((rho - 2) + 6 * P); 4..20 the colour ranges.
        static readonly byte[] QMax = { 1, 2, 3, 4, 5, 7, 9, 11, 15, 19, 23, 31, 39, 47, 63, 79, 95, 127, 159, 191, 255 };
        static readonly byte[] QKind = { 0, 1, 0, 2, 1, 0, 2, 1, 0, 2, 1, 0, 2, 1, 0, 2, 1, 0, 2, 1, 0 };   // 1 trit, 2 quint
        static readonly byte[] QBits = { 1, 0, 2, 0, 1, 3, 1, 2, 4, 2, 3, 5, 3, 4, 6, 4, 5, 7, 5, 6, 8 };

        /// <summary>Bits taken by an integer sequence of <paramref name="count"/> values in range <paramref name="q"/>.</summary>
        static int IseBits(int q, int count)
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

        static ulong Reverse(ulong v)
        {
            v = (v >> 1) & 0x5555555555555555UL | (v & 0x5555555555555555UL) << 1;
            v = (v >> 2) & 0x3333333333333333UL | (v & 0x3333333333333333UL) << 2;
            v = (v >> 4) & 0x0F0F0F0F0F0F0F0FUL | (v & 0x0F0F0F0F0F0F0F0FUL) << 4;
            return BinaryPrimitives.ReverseEndianness(v);
        }

        // ------------------------------------------------------------------ integer sequence encoding (23.12)

        static void DecodeIse(ulong lo, ulong hi, int start, int q, int count, Span<int> o)
        {
            int n = QBits[q], limit = start + IseBits(q, count), pos = start;
            switch (QKind[q])
            {
                case 0:
                    for (int i = 0; i < count; i++, pos += n) o[i] = Read(lo, hi, pos, n, limit);
                    break;
                case 1:
                    Span<int> m = stackalloc int[5];
                    Span<int> t = stackalloc int[5];
                    for (int i = 0; i < count; i += 5)
                    {
                        m[0] = Read(lo, hi, pos, n, limit); pos += n;
                        int T = Read(lo, hi, pos, 2, limit); pos += 2;
                        m[1] = Read(lo, hi, pos, n, limit); pos += n;
                        T |= Read(lo, hi, pos, 2, limit) << 2; pos += 2;
                        m[2] = Read(lo, hi, pos, n, limit); pos += n;
                        T |= Read(lo, hi, pos, 1, limit) << 4; pos += 1;
                        m[3] = Read(lo, hi, pos, n, limit); pos += n;
                        T |= Read(lo, hi, pos, 2, limit) << 5; pos += 2;
                        m[4] = Read(lo, hi, pos, n, limit); pos += n;
                        T |= Read(lo, hi, pos, 1, limit) << 7; pos += 1;
                        Trits(T, t);
                        for (int j = 0; j < 5 && i + j < count; j++) o[i + j] = t[j] << n | m[j];
                    }
                    break;
                default:
                    Span<int> mq = stackalloc int[3];
                    Span<int> qv = stackalloc int[3];
                    for (int i = 0; i < count; i += 3)
                    {
                        mq[0] = Read(lo, hi, pos, n, limit); pos += n;
                        int Q = Read(lo, hi, pos, 3, limit); pos += 3;
                        mq[1] = Read(lo, hi, pos, n, limit); pos += n;
                        Q |= Read(lo, hi, pos, 2, limit) << 3; pos += 2;
                        mq[2] = Read(lo, hi, pos, n, limit); pos += n;
                        Q |= Read(lo, hi, pos, 2, limit) << 5; pos += 2;
                        Quints(Q, qv);
                        for (int j = 0; j < 3 && i + j < count; j++) o[i + j] = qv[j] << n | mq[j];
                    }
                    break;
            }
        }

        static int Bit(int v, int i) => v >> i & 1;

        static void Trits(int T, Span<int> t)
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

        static void Quints(int Q, Span<int> q)
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

        static int UnquantColor(int q, int v)
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

        static int UnquantWeight(int q, int v)
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
        static bool BlockMode(int m, bool is3D, out int gw, out int gh, out int gd, out int wq, out bool dual)
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

        static int SelectPartition(int seed, int x, int y, int z, int count, bool small)
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
        const int HdrRgb = 1, HdrA = 2;

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
        static int Endpoints(int cem, ReadOnlySpan<int> v, Span<int> e)
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
        static ushort Unorm16ToHalf(int c)
        {
            if (c == 65535) return HalfOne;
            if (c < 4) return (ushort)(c << 8);   // subnormal: c / 65536 = (c * 256) * 2^-24
            int p = 31 - BitOperations.LeadingZeroCount((uint)c);   // 2..15
            int mant = p >= 10 ? c >> (p - 10) : c << (10 - p);
            return (ushort)((p - 1) << 10 | mant & 0x3FF);
        }

        /// <summary>Half float in 0..1 to 8 bits, rounded to nearest (255 x is never halfway for a binary16 x).</summary>
        static byte HalfToUnorm8(ushort h) => (byte)Math.Round((double)BitConverter.UInt16BitsToHalf(h) * 255);

        /// <summary>An interpolated HDR value (16 bit, from 12 bit endpoints shifted up by 4) to half float.</summary>
        static ushort HdrToHalf(int c)
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
            if (!BlockMode(bm, is3D, out int gw, out int gh, out int gd, out int wq, out bool dual) || gw > bw || gh > bh || gd > bd)
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
            for (int i = 0; i < colorCount; i++) values[i] = UnquantColor(cq, values[i]);
            Span<int> ep = stackalloc int[32];
            Span<int> flags = stackalloc int[4];
            for (int i = 0, k = 0; i < parts; i++)
            {
                flags[i] = Endpoints(cem[i], values.Slice(k), ep.Slice(i * 8, 8));
                k += ((cem[i] >> 2) + 1) * 2;
            }

            // weights, stored bit-reversed from the top of the block; padded so the infill may read past the grid edge
            Span<int> raw = stackalloc int[64];
            DecodeIse(Reverse(hi), Reverse(lo), 0, wq, weightCount, raw);
            Span<int> w0 = stackalloc int[160];
            Span<int> w1 = stackalloc int[160];
            w0.Clear(); w1.Clear();
            for (int i = 0; i < gridCount; i++)
            {
                if (dual)
                {
                    w0[i] = UnquantWeight(wq, raw[2 * i]);
                    w1[i] = UnquantWeight(wq, raw[2 * i + 1]);
                }
                else w0[i] = UnquantWeight(wq, raw[i]);
            }

            // infill scale factors (23.18)
            int ds = (1024 + bw / 2) / (bw - 1), dt = (1024 + bh / 2) / (bh - 1), dr = is3D ? (1024 + bd / 2) / (bd - 1) : 0;
            bool small = texels < 31;
            for (int z = 0; z < bd; z++)
                for (int y = 0; y < bh; y++)
                    for (int x = 0; x < bw; x++)
                    {
                        int t = (z * bh + y) * bw + x;
                        int part = parts == 1 ? 0 : SelectPartition(seed, x, y, z, parts, small);
                        int f = flags[part];
                        if (f != 0 && mode != Mode.Float16)
                        {
                            Error(1, mode, o8.Slice(t * 4), o16);
                            continue;
                        }
                        int gs = (ds * x * (gw - 1) + 32) >> 6, gt = (dt * y * (gh - 1) + 32) >> 6, gr = (dr * z * (gd - 1) + 32) >> 6;
                        int i0 = Infill(w0, gs, gt, gr, gw, gh, is3D), i1 = dual ? Infill(w1, gs, gt, gr, gw, gh, is3D) : 0;
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

        /// <summary>The effective weight at weight grid position (gs, gt, gr) in 4.4 fixed point: bilinear (2D) or
        /// simplex (3D) interpolation of the stored weights.</summary>
        static int Infill(ReadOnlySpan<int> w, int gs, int gt, int gr, int gw, int gh, bool is3D)
        {
            int js = gs >> 4, fs = gs & 15, jt = gt >> 4, ft = gt & 15;
            if (!is3D)
            {
                int v0 = js + jt * gw;
                int w11 = (fs * ft + 8) >> 4, w10 = ft - w11, w01 = fs - w11, w00 = 16 - fs - ft + w11;
                return (w[v0] * w00 + w[v0 + 1] * w01 + w[v0 + gw] * w10 + w[v0 + gw + 1] * w11 + 8) >> 4;
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
            return (w[b0] * a0 + w[b0 + s1] * a1 + w[b0 + s1 + s2] * a2 + w[b0 + plane + gw + 1] * a3 + 8) >> 4;
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
