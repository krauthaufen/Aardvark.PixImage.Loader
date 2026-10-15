using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace PixCodec
{
    internal sealed class J2kHtScratch
    {
        public byte[] Rho = new byte[516];        // significance patterns of the quad row above (index quad + 1)
        public byte[] Exp = new byte[1030];       // magnitude exponents of the sample row above (index x + 1)
        public byte[] Sig = Array.Empty<byte>();  // (w + 2) * (h + 2): 1 significant after cleanup, 2 after SigProp
    }

    /// <summary>The HT block decoder of T.814 clause 7: cleanup pass (MEL, VLC and MagSgn streams), SigProp and MagRef.</summary>
    internal static unsafe class J2kHtDecoder
    {
        static ReadOnlySpan<byte> MelExp => new byte[] { 0, 0, 0, 1, 1, 1, 2, 2, 2, 3, 3, 4, 5 };

        /// <summary>MEL bit-stream: forward from Pcup, most significant bit first (7.1.3, 7.3.3).</summary>
        struct Mel
        {
            public byte* D;
            public int Pos, End, Bits, K, Run;
            public uint Tmp;
            public bool One;

            int Bit()
            {
                if (Bits == 0)
                {
                    Bits = Tmp == 0xFF ? 7 : 8;
                    Tmp = Pos < End ? D[Pos++] : 0xFFu;
                }
                Bits--;
                return (int)(Tmp >> Bits) & 1;
            }

            public int Sym()
            {
                if (Run == 0 && !One)
                {
                    int eval = MelExp[K];
                    if (Bit() == 1)
                    {
                        Run = 1 << eval;
                        if (K < 12) K++;
                    }
                    else
                    {
                        Run = 0;
                        for (; eval > 0; eval--) Run = 2 * Run + Bit();
                        if (K > 0) K--;
                        One = true;
                    }
                }
                if (Run > 0) { Run--; return 0; }
                One = false;
                return 1;
            }
        }

        /// <summary>A bit-stream read backwards, least significant bit first, with the 0x8F / 0x7F unstuffing rule (7.1.4, 7.1.6).</summary>
        struct Reverse
        {
            public byte* D;
            public int Pos, Min;             // next byte to read, lowest readable position
            public uint Last;
            public ulong Buf;
            public int Bits;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Fill()
            {
                // four bytes at once while none of them can be a stuffed one (low seven bits all ones)
                while (Bits <= 32 && Pos - 3 >= Min)
                {
                    uint x = Unsafe.ReadUnaligned<uint>(D + Pos - 3), y = x | 0x80808080u;
                    if (((~y - 0x01010101u) & y & 0x80808080u) != 0) break;
                    Buf |= (ulong)BinaryPrimitives.ReverseEndianness(x) << Bits;
                    Bits += 32;
                    Pos -= 4;
                    Last = x & 0xFF;
                }
                while (Bits <= 56)
                {
                    uint t = Pos >= Min ? D[Pos] : 0u;
                    Pos--;
                    int n = Last > 0x8F && (t & 0x7F) == 0x7F ? 7 : 8;
                    Last = t;
                    Buf |= (ulong)(t & ((1u << n) - 1)) << Bits;
                    Bits += n;
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Skip(int n) { Buf >>= n; Bits -= n; }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int Take(int n)
            {
                int v = (int)(Buf & ((1u << n) - 1));
                Buf >>= n; Bits -= n;
                return v;
            }
        }

        /// <summary>A bit-stream read forwards, least significant bit first, a byte after 0xFF carrying 7 bits (7.1.2, 7.1.5).</summary>
        struct Forward
        {
            public byte* D;
            public int Pos, End;
            public uint Last, Pad;           // Pad: what is read behind the end (0xFF for MagSgn, 0 for SigProp)
            public ulong Buf;
            public int Bits;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Fill()
            {
                // four bytes at once while neither they nor the byte before them is 0xFF
                while (Bits <= 32 && Last != 0xFF && Pos + 4 <= End)
                {
                    uint x = Unsafe.ReadUnaligned<uint>(D + Pos);
                    if (((~x - 0x01010101u) & x & 0x80808080u) != 0) break;
                    Buf |= (ulong)x << Bits;
                    Bits += 32;
                    Pos += 4;
                    Last = x >> 24;
                }
                while (Bits <= 56)
                {
                    uint t = Pos < End ? D[Pos] : Pad;
                    Pos++;
                    int n = Last == 0xFF ? 7 : 8;
                    Last = t;
                    Buf |= (ulong)(t & ((1u << n) - 1)) << Bits;
                    Bits += n;
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public ulong Take(int n)
            {
                if (n > 32) return TakeLong(n);
                if (Bits < n) Fill();
                ulong v = Buf & ((1UL << n) - 1);
                Buf >>= n; Bits -= n;
                return v;
            }

            // more than 32 bits (not part of Take: a recursive call would keep it from being inlined)
            [MethodImpl(MethodImplOptions.NoInlining)]
            ulong TakeLong(int n)
            {
                ulong lo = Take(32);
                return lo | Take(n - 32) << 32;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Max(int a, int b) => a - ((a - b) & ((a - b) >> 31));

        /// <summary>The MagSgn value of sample j of a quad (0 bits and 0 if it is not significant); sets bad on an impossible value.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ulong Sample(ref Forward ms, int U, int k, int i1, int r, int j, int maxBits, ref bool bad)
        {
            int sig = r >> j & 1;
            int m = (U - (k >> j & 1)) & -sig;
            ulong v = ms.Take(m) | (ulong)(i1 >> j & sig) << m;
            ulong mu = (v >> 1) + 1;
            bad |= (64 - BitOperations.LeadingZeroCount(v | 1) > maxBits + 1 || mu >> maxBits != 0) & sig != 0;
            return v;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Exp(ulong v, int sig) => (64 - BitOperations.LeadingZeroCount(v | 1)) & -sig;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static T Value<T>(ulong v, int sig, int p, T half, T signBit) where T : unmanaged, IBinaryInteger<T>
        {
            T val = T.CreateTruncating(((v >> 1) + 1) << (p + 1)) | half | (signBit & T.CreateTruncating(0 - (v & 1)));
            return val & T.CreateTruncating(0 - (ulong)sig);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int UPrefix(ref Reverse vlc)
        {
            // by table on the next three bits (prefix | length << 4): 1 "1", 2 "01", 3 "001", 5 "000"
            int e = (int)(0x1122_1133_1122_1135UL >> (int)(((uint)vlc.Buf & 7) << 3)) & 0xFF;
            vlc.Skip(e >> 4);
            return e & 15;
        }

        // suffix bits per prefix: 0 for 1 and 2, 1 for 3, 5 for 5 (no branches: the prefixes are irregular)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int USuffix(ref Reverse vlc, int pfx) => vlc.Take((0x501000 >> (4 * pfx)) & 15);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int UExt(ref Reverse vlc, int sfx) => vlc.Take((27 - sfx) >> 31 & 4);

        /// <summary>
        /// Decodes an HT set into data (w * h, row-major; representation as in <see cref="J2kBlockCoder"/>).
        /// The segments lie in coder.Buf. <paramref name="p"/> is the bit plane of the cleanup pass' least significant
        /// magnitude bit. Returns false if the segments are not a valid HT set.
        /// </summary>
        public static bool Decode<T>(J2kBlockCoder coder, T* data, int w, int h, int cupStart, int lcup, int refStart, int lref,
            int zblk, int p, bool causal) where T : unmanaged, IBinaryInteger<T>
        {
            int n = w * h;
            new Span<T>(data, n).Clear();
            if (lcup < 2) return false;
            if (zblk > 1 && p == 0) zblk = 1;
            int typeBits = sizeof(T) * 8;
            if (p + 1 >= typeBits - 1) return false;
            int maxBits = typeBits - 2 - p;      // bits a magnitude may have
            var s = coder.Ht!;
            int qw = (w + 1) >> 1, qh = (h + 1) >> 1;
            if (s.Rho.Length < qw + 2) s.Rho = new byte[qw + 2];
            if (s.Exp.Length < 2 * qw + 3) s.Exp = new byte[2 * qw + 3];
            T signBit = T.One << (typeBits - 1);
            T half = T.One << p;

            fixed (byte* buf = coder.Buf, rhoRow = s.Rho, expRow = s.Exp)
            fixed (ushort* lut0 = J2kHtTables.Decode0, lut1 = J2kHtTables.Decode1)
            {
                byte* d = buf + cupStart;
                int scup = (d[lcup - 1] << 4) + (d[lcup - 2] & 0x0F);
                if (scup < 2 || scup > lcup || scup > 4079) return false;
                int pcup = lcup - scup;
                d[lcup - 1] = 0xFF;
                d[lcup - 2] |= 0x0F;

                var mel = new Mel { D = d, Pos = pcup, End = lcup };
                var vlc = new Reverse { D = d, Pos = lcup - 3, Min = pcup };
                vlc.Last = d[lcup - 2];
                {
                    uint t = vlc.Last >> 4;
                    int nb = (t & 7) < 7 ? 4 : 3;
                    vlc.Buf = t & ((1u << nb) - 1);
                    vlc.Bits = nb;
                }
                var ms = new Forward { D = d, Pos = 0, End = pcup, Pad = 0xFF };

                new Span<byte>(rhoRow, qw + 2).Clear();
                new Span<byte>(expRow, 2 * qw + 3).Clear();
                byte* rhoAbove = rhoRow + 1;   // rhoAbove[q] for q = -1 .. qw
                byte* expAbove = expRow + 1;   // expAbove[x] for x = -1 .. 2 qw

                int* rho = stackalloc int[2], uoff = stackalloc int[2], ek = stackalloc int[2], e1 = stackalloc int[2], u = stackalloc int[2];
                for (int qy = 0; qy < qh; qy++)
                {
                    bool firstRow = qy == 0;
                    ushort* lut = firstRow ? lut0 : lut1;
                    int rhoLeft = 0;             // pattern of the quad to the left in this row
                    int prevAboveRho = 0;        // rhoAbove[q - 1] before it was overwritten
                    int oldExpLeft = 0;          // expAbove[2q - 1] of the row above
                    T* row0 = data + 2 * qy * w;
                    T* row1 = row0 + w;
                    bool hasRow1 = 2 * qy + 1 < h;
                    for (int qx = 0; qx < qw; qx += 2)
                    {
                        int pair = qx + 1 < qw ? 2 : 1;
                        // --- significance patterns and EMB patterns (7.3.5) ---
                        int aboveLeft = prevAboveRho;
                        uoff[1] = 0;
                        for (int i = 0; i < pair; i++)
                        {
                            int q = qx + i;
                            int c;
                            if (firstRow) c = ((rhoLeft & 3) != 0 ? 1 : 0) | (rhoLeft >> 1 & 2) | (rhoLeft >> 1 & 4);
                            else
                            {
                                int above = rhoAbove[q], aboveRight = rhoAbove[q + 1];
                                c = ((aboveLeft & 8) != 0 || (above & 2) != 0 ? 1 : 0)
                                    | ((rhoLeft & 12) != 0 ? 2 : 0)
                                    | ((above & 8) != 0 || (aboveRight & 2) != 0 ? 4 : 0);
                                aboveLeft = above;
                            }
                            if (c == 0 && mel.Sym() == 0)
                            {
                                rho[i] = uoff[i] = ek[i] = e1[i] = 0;
                            }
                            else
                            {
                                vlc.Fill();
                                int e = lut[c << 7 | (int)(vlc.Buf & 0x7F)];
                                vlc.Skip(e & 7);
                                uoff[i] = e >> 3 & 1; rho[i] = e >> 4 & 15; ek[i] = e >> 8 & 15; e1[i] = e >> 12;
                            }
                            rhoLeft = rho[i];
                        }
                        // --- unsigned residuals (7.3.6) ---
                        vlc.Fill();
                        u[0] = u[1] = 0;
                        if (pair == 2 && firstRow && uoff[0] != 0 && uoff[1] != 0)
                        {
                            if (mel.Sym() == 1)
                            {
                                int p0 = UPrefix(ref vlc), p1 = UPrefix(ref vlc);
                                int s0 = USuffix(ref vlc, p0), s1 = USuffix(ref vlc, p1);
                                u[0] = 2 + p0 + s0 + 4 * UExt(ref vlc, s0);
                                u[1] = 2 + p1 + s1 + 4 * UExt(ref vlc, s1);
                            }
                            else
                            {
                                int p0 = UPrefix(ref vlc);
                                if (p0 > 2)
                                {
                                    int ubit = vlc.Take(1);
                                    int s0 = USuffix(ref vlc, p0);
                                    u[0] = p0 + s0 + 4 * UExt(ref vlc, s0);
                                    u[1] = ubit + 1;
                                }
                                else
                                {
                                    int p1 = UPrefix(ref vlc);
                                    int s0 = USuffix(ref vlc, p0), s1 = USuffix(ref vlc, p1);
                                    u[0] = p0 + s0 + 4 * UExt(ref vlc, s0);
                                    u[1] = p1 + s1 + 4 * UExt(ref vlc, s1);
                                }
                            }
                        }
                        else
                        {
                            int p0 = uoff[0] != 0 ? UPrefix(ref vlc) : 0, p1 = uoff[1] != 0 ? UPrefix(ref vlc) : 0;
                            int s0 = USuffix(ref vlc, p0), s1 = USuffix(ref vlc, p1);
                            u[0] = p0 + s0 + 4 * UExt(ref vlc, s0);
                            u[1] = p1 + s1 + 4 * UExt(ref vlc, s1);
                        }
                        if (vlc.Bits < 0) return false;
                        // --- exponent bounds and MagSgn values (7.3.7, 7.3.8) ---
                        for (int i = 0; i < pair; i++)
                        {
                            int q = qx + i;
                            int r = rho[i];
                            int x = 2 * q;
                            // exponents of the row above around this quad; expAbove is being overwritten as we go
                            int eNw = oldExpLeft, eN = expAbove[x], eNe = expAbove[x + 1], eNf = expAbove[x + 2];
                            oldExpLeft = eNe;
                            prevAboveRho = rhoAbove[q];
                            rhoAbove[q] = (byte)r;
                            // (maxima without branches: the operands are small, the outcomes irregular)
                            int kappa = 1;
                            if (!firstRow && (r & (r - 1)) != 0)
                                kappa = Max(1, Max(Max(eNw, eN), Max(eNe, eNf)) - 1);
                            int U = kappa + u[i];
                            int eb0 = 0, eb1 = 0;    // exponents of the two bottom samples
                            if (r != 0)
                            {
                                if (U > maxBits + 1) return false;
                                int k = ek[i], i1 = e1[i];
                                // the four samples unrolled; an insignificant one takes 0 bits and writes nothing
                                bool bad = false;
                                ulong v0 = Sample(ref ms, U, k, i1, r, 0, maxBits, ref bad);
                                ulong v1 = Sample(ref ms, U, k, i1, r, 1, maxBits, ref bad);
                                ulong v2 = Sample(ref ms, U, k, i1, r, 2, maxBits, ref bad);
                                ulong v3 = Sample(ref ms, U, k, i1, r, 3, maxBits, ref bad);
                                // padded samples are never significant
                                if (bad || (x + 1 >= w && (r & 12) != 0) || (!hasRow1 && (r & 10) != 0)) return false;
                                eb0 = Exp(v1, r >> 1 & 1);
                                eb1 = Exp(v3, r >> 3 & 1);
                                // (the block was cleared: insignificant samples write 0, no branch on the pattern)
                                row0[x] = Value<T>(v0, r & 1, p, half, signBit);
                                if (x + 1 < w) row0[x + 1] = Value<T>(v2, r >> 2 & 1, p, half, signBit);
                                if (hasRow1)
                                {
                                    row1[x] = Value<T>(v1, r >> 1 & 1, p, half, signBit);
                                    if (x + 1 < w) row1[x + 1] = Value<T>(v3, r >> 3 & 1, p, half, signBit);
                                }
                            }
                            expAbove[x] = (byte)eb0;
                            expAbove[x + 1] = (byte)eb1;
                        }
                    }
                }

                if (zblk > 1)
                {
                    int stride = w + 2;
                    int sigLen = stride * (h + 2);
                    if (s.Sig.Length < sigLen) s.Sig = new byte[sigLen];
                    fixed (byte* sig = s.Sig)
                    {
                        new Span<byte>(sig, sigLen).Clear();
                        for (int y = 0; y < h; y++)
                            for (int x = 0; x < w; x++)
                                if (data[y * w + x] != T.Zero) sig[(y + 1) * stride + x + 1] = 1;
                        byte* r = buf + refStart;
                        var sp = new Forward { D = r, Pos = 0, End = lref, Pad = 0 };
                        T newSig = T.One << p | T.One << (p - 1);
                        for (int y0 = 0; y0 < h; y0 += 4)
                        {
                            int y1 = Math.Min(y0 + 4, h);
                            for (int x0 = 0; x0 < w; x0 += 4)
                            {
                                int x1 = Math.Min(x0 + 4, w);
                                for (int x = x0; x < x1; x++)
                                    for (int y = y0; y < y1; y++)
                                    {
                                        byte* f = sig + (y + 1) * stride + x + 1;
                                        if (*f != 0) continue;
                                        int mbr = f[-1] | f[1] | f[-stride - 1] | f[-stride] | f[-stride + 1];
                                        // with vertically causal contexts the next stripe is not looked at
                                        if (!(causal && y == y0 + 3)) mbr |= f[stride - 1] | f[stride] | f[stride + 1];
                                        if (mbr == 0) continue;
                                        if (sp.Take(1) != 0) { *f = 2; data[y * w + x] = newSig; }
                                    }
                                // the signs of the samples that became significant in this column group
                                for (int x = x0; x < x1; x++)
                                    for (int y = y0; y < y1; y++)
                                        if (sig[(y + 1) * stride + x + 1] == 2 && sp.Take(1) != 0) data[y * w + x] |= signBit;
                            }
                        }
                        if (zblk > 2)
                        {
                            var mr = new Reverse { D = r, Pos = lref - 1, Min = 0, Last = 0xFF };
                            T planeBit = T.One << p, lowHalf = T.One << (p - 1);
                            for (int y0 = 0; y0 < h; y0 += 4)
                            {
                                int y1 = Math.Min(y0 + 4, h);
                                for (int x = 0; x < w; x++)
                                    for (int y = y0; y < y1; y++)
                                    {
                                        if (sig[(y + 1) * stride + x + 1] != 1) continue;
                                        if (mr.Bits == 0) mr.Fill();
                                        int bit = mr.Take(1);
                                        T v = data[y * w + x];
                                        v = (v & ~planeBit) | lowHalf;
                                        if (bit != 0) v |= planeBit;
                                        data[y * w + x] = v;
                                    }
                            }
                        }
                    }
                }
            }
            return true;
        }
    }
}
