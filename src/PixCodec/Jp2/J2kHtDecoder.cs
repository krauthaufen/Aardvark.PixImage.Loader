using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

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

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int Sym()
            {
                // the common cases inline: inside a run of zeros, or the one that ends it
                if (Run > 0) { Run--; return 0; }
                if (One) { One = false; return 1; }
                return Next();
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            int Next()
            {
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

        /// <summary>
        /// Pass 1 of a quad row: significance, EMB patterns and residuals from the MEL and VLC streams (7.3.5, 7.3.6), into
        /// rhoCur, quadE and quadU; rhoAbove (the row above) is only read. A method of its own, with the streams in local copies.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static bool VlcRow(ref Mel melRef, ref Reverse vlc, ushort* lut, bool firstRow, byte* rhoAbove, byte* rhoCur, ushort* quadE, byte* quadU, int qw)
        {
            var mel = melRef;
            // The VLC bit buffer lives in locals; a pair of quads takes at most 7 + 7 + 24 bits, so one refill of the
            // structure (to more than 56 bits) per pair leaves every read below unchecked.
            ulong buf = vlc.Buf;
            int bits = vlc.Bits;
            int rhoLeft = 0;             // pattern of the quad to the left in this row
            rhoCur[-1] = 0;
            for (int qx = 0; qx < qw; qx += 2)
            {
                if (bits < 38) { vlc.Buf = buf; vlc.Bits = bits; vlc.Fill(); buf = vlc.Buf; bits = vlc.Bits; }
                bool two = qx + 1 < qw;
                int eA = QuadCode(ref mel, ref buf, ref bits, lut, firstRow, rhoLeft, rhoAbove[qx - 1], rhoAbove[qx], rhoAbove[qx + 1]);
                rhoLeft = eA >> 4 & 15;
                int eB = 0;
                if (two)
                {
                    eB = QuadCode(ref mel, ref buf, ref bits, lut, firstRow, rhoLeft, rhoAbove[qx], rhoAbove[qx + 1], rhoAbove[qx + 2]);
                    rhoLeft = eB >> 4 & 15;
                }
                // --- unsigned residuals (7.3.6) ---
                int uA, uB;
                int offA = eA >> 3 & 1, offB = eB >> 3 & 1;
                if (two && firstRow && offA != 0 && offB != 0)
                {
                    if (mel.Sym() == 1)
                    {
                        int p0 = Prefix(ref buf, ref bits, 1), p1 = Prefix(ref buf, ref bits, 1);
                        int s0 = Suffix(ref buf, ref bits, p0), s1 = Suffix(ref buf, ref bits, p1);
                        uA = 2 + p0 + s0 + 4 * Ext(ref buf, ref bits, s0);
                        uB = 2 + p1 + s1 + 4 * Ext(ref buf, ref bits, s1);
                    }
                    else
                    {
                        int p0 = Prefix(ref buf, ref bits, 1);
                        if (p0 > 2)
                        {
                            int ubit = Bits(ref buf, ref bits, 1);
                            int s0 = Suffix(ref buf, ref bits, p0);
                            uA = p0 + s0 + 4 * Ext(ref buf, ref bits, s0);
                            uB = ubit + 1;
                        }
                        else
                        {
                            int p1 = Prefix(ref buf, ref bits, 1);
                            int s0 = Suffix(ref buf, ref bits, p0), s1 = Suffix(ref buf, ref bits, p1);
                            uA = p0 + s0 + 4 * Ext(ref buf, ref bits, s0);
                            uB = p1 + s1 + 4 * Ext(ref buf, ref bits, s1);
                        }
                    }
                }
                else
                {
                    // (a quad without residual reads no prefix: its offset bit masks the table's length)
                    int p0 = Prefix(ref buf, ref bits, offA), p1 = Prefix(ref buf, ref bits, offB);
                    int s0 = Suffix(ref buf, ref bits, p0), s1 = Suffix(ref buf, ref bits, p1);
                    uA = p0 + s0 + 4 * Ext(ref buf, ref bits, s0);
                    uB = p1 + s1 + 4 * Ext(ref buf, ref bits, s1);
                }
                if (bits < 0) { melRef = mel; vlc.Buf = buf; vlc.Bits = bits; return false; }
                rhoCur[qx] = (byte)(eA >> 4 & 15); quadE[qx] = (ushort)eA; quadU[qx] = (byte)uA;
                if (two) { rhoCur[qx + 1] = (byte)(eB >> 4 & 15); quadE[qx + 1] = (ushort)eB; quadU[qx + 1] = (byte)uB; }
            }
            melRef = mel;
            vlc.Buf = buf; vlc.Bits = bits;
            return true;
        }

        /// <summary>One quad's context, MEL symbol and VLC codeword on the bit buffer in locals; returns the table entry (0: insignificant).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int QuadCode(ref Mel mel, ref ulong buf, ref int bits, ushort* lut, bool firstRow, int rhoLeft, int aboveLeft, int above, int aboveRight)
        {
            int c = firstRow
                ? ((rhoLeft & 3) != 0 ? 1 : 0) | (rhoLeft >> 1 & 2) | (rhoLeft >> 1 & 4)
                : ((aboveLeft | above << 2) >> 3 & 1)                                 // (aboveLeft & 8) | (above & 2)
                  | ((rhoLeft & 12) != 0 ? 2 : 0)
                  | ((above | aboveRight << 2) >> 1 & 4);                             // (above & 8) | (aboveRight & 2)
            if (c == 0 && mel.Sym() == 0) return 0;
            int e = lut[c << 7 | (int)(buf & 0x7F)];
            buf >>= e & 7; bits -= e & 7;
            return e;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Bits(ref ulong buf, ref int bits, int n)
        {
            int v = (int)(buf & ((1u << n) - 1));
            buf >>= n; bits -= n;
            return v;
        }

        // U-VLC prefix by table on the next three bits (prefix | length << 4): 1 "1", 2 "01", 3 "001", 5 "000"; none if on is 0
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Prefix(ref ulong buf, ref int bits, int on)
        {
            int e = (int)(0x1122_1133_1122_1135UL >> (int)(((uint)buf & 7) << 3)) & 0xFF & -on;
            buf >>= e >> 4; bits -= e >> 4;
            return e & 15;
        }

        // suffix bits per prefix: 0 for 1 and 2, 1 for 3, 5 for 5; extension of 4 bits for suffixes from 28
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Suffix(ref ulong buf, ref int bits, int pfx) => Bits(ref buf, ref bits, (0x501000 >> (4 * pfx)) & 15);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Ext(ref ulong buf, ref int bits, int sfx) => Bits(ref buf, ref bits, (27 - sfx) >> 31 & 4);

        /// <summary>
        /// Pass 2 of a quad row: exponent bounds and MagSgn values (7.3.7, 7.3.8). A method of its own, with the stream in
        /// a local copy, so that its state gets the registers.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static bool MagSgnRow<T>(ref Forward msRef, T* row0, T* row1, byte* rhoCur, ushort* quadE, byte* quadU, byte* expAbove,
            int qw, int w, bool firstRow, bool hasRow1, int p, int maxBits, T half, T signBit) where T : unmanaged, IBinaryInteger<T>
        {
            var ms = msRef;
            // the bit buffer in locals; the structure is only touched by refills
            ulong buf = ms.Buf;
            int bits = ms.Bits;
            // the largest exponent of the row above around each quad (columns x - 1 .. x + 2), before expAbove is overwritten
            byte* above = stackalloc byte[qw + 16];
            if (!firstRow) AboveMaxima(expAbove, qw, above);
            for (int q = 0; q < qw; q++)
            {
                int r = rhoCur[q];
                int x = 2 * q;
                int eb0 = 0, eb1 = 0;    // exponents of the two bottom samples
                if (r != 0)
                {
                    // (maxima without branches: the operands are small, the outcomes irregular)
                    int kappa = 1;
                    if (!firstRow && (r & (r - 1)) != 0)
                        kappa = Max(1, above[q] - 1);
                    int U = kappa + quadU[q];
                    if (U > maxBits + 1) return false;
                    int e = quadE[q];
                    int k = e >> 8 & 15, i1 = e >> 12;
                    // bits the quad's significant samples take
                    int quadBits = 4 * U - BitOperations.PopCount((uint)(k & r)) - (4 - BitOperations.PopCount((uint)r)) * U;
                    if (typeof(T) == typeof(uint) && VectorQuads && quadBits <= 56 && U <= 16)
                    {
                        // the whole quad at once (its bits, at most 56, are in the buffer after one refill)
                        if (bits < quadBits) { ms.Buf = buf; ms.Bits = bits; ms.Fill(); buf = ms.Buf; bits = ms.Bits; }
                        if (!QuadVector(ref buf, ref bits, U, k, i1, r, maxBits, p, uint.CreateTruncating(half), out var vals, out uint q1, out uint q3))
                            return false;
                        if ((x + 1 >= w && (r & 12) != 0) || (!hasRow1 && (r & 10) != 0)) return false;
                        eb0 = (32 - BitOperations.LeadingZeroCount(q1 | 1)) & -(r >> 1 & 1);
                        eb1 = (32 - BitOperations.LeadingZeroCount(q3 | 1)) & -(r >> 3 & 1);
                        // lanes: x row 0, x row 1, x + 1 row 0, x + 1 row 1 -> the two rows' pairs
                        var pairs = Vector128.Shuffle(vals, Vector128.Create(0u, 2, 1, 3)).AsUInt64();
                        uint* r0 = (uint*)row0 + x, r1 = (uint*)row1 + x;
                        if (x + 1 < w)
                        {
                            *(ulong*)r0 = pairs.GetElement(0);
                            if (hasRow1) *(ulong*)r1 = pairs.GetElement(1);
                        }
                        else
                        {
                            *r0 = vals.GetElement(0);
                            if (hasRow1) *r1 = vals.GetElement(1);
                        }
                    }
                    else
                    {
                    // the four samples unrolled; an insignificant one takes 0 bits and writes nothing
                    bool bad = false;
                    ulong v0 = Sample(ref buf, ref bits, ref ms, U, k, i1, r, 0, maxBits, ref bad);
                    ulong v1 = Sample(ref buf, ref bits, ref ms, U, k, i1, r, 1, maxBits, ref bad);
                    ulong v2 = Sample(ref buf, ref bits, ref ms, U, k, i1, r, 2, maxBits, ref bad);
                    ulong v3 = Sample(ref buf, ref bits, ref ms, U, k, i1, r, 3, maxBits, ref bad);
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
                }
                expAbove[x] = (byte)eb0;
                expAbove[x + 1] = (byte)eb1;
            }
            ms.Buf = buf; ms.Bits = bits;
            msRef = ms;
            return true;
        }

        /// <summary>max(e[2q - 1], e[2q], e[2q + 1], e[2q + 2]) for every quad q of a row, sixteen at a time.</summary>
        static void AboveMaxima(byte* e, int qw, byte* dst)
        {
            int q = 0;
            if (Vector128.IsHardwareAccelerated)
            {
                var low = Vector128.Create((ushort)0xFF);
                for (; q + 8 <= qw; q += 8)
                {
                    var a = Vector128.Load((ushort*)(e + 2 * q - 1)); var b = Vector128.Load((ushort*)(e + 2 * q + 1));
                    var m = Vector128.Max(Vector128.Max(a & low, Vector128.ShiftRightLogical(a, 8)), Vector128.Max(b & low, Vector128.ShiftRightLogical(b, 8)));
                    *(ulong*)(dst + q) = Vector128.Narrow(m, m).AsUInt64().ToScalar();
                }
            }
            for (; q < qw; q++)
            {
                int x = 2 * q;
                dst[q] = (byte)Math.Max(Math.Max(e[x - 1], e[x]), Math.Max(e[x + 1], e[x + 2]));
            }
        }

        // per lane variable shifts are what the quad at once needs
        static readonly bool VectorQuads = System.Runtime.Intrinsics.X86.Avx2.IsSupported || System.Runtime.Intrinsics.Arm.AdvSimd.IsSupported;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector128<uint> ShiftRight(Vector128<uint> a, Vector128<uint> n)
            => System.Runtime.Intrinsics.X86.Avx2.IsSupported ? System.Runtime.Intrinsics.X86.Avx2.ShiftRightLogicalVariable(a, n)
                : System.Runtime.Intrinsics.Arm.AdvSimd.ShiftLogical(a, -n.AsInt32());

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector128<uint> ShiftLeft(Vector128<uint> a, Vector128<uint> n)
            => System.Runtime.Intrinsics.X86.Avx2.IsSupported ? System.Runtime.Intrinsics.X86.Avx2.ShiftLeftLogicalVariable(a, n)
                : System.Runtime.Intrinsics.Arm.AdvSimd.ShiftLogical(a, n.AsInt32());

        /// <summary>
        /// The MagSgn values of a whole quad whose samples take at most 56 bits, U &lt;= 16 (32-bit samples), one vector lane per sample in the order of
        /// <see cref="Sample"/>: each lane extracts its bits from the low 32 bits of the buffer at its offset (the sum of the
        /// lengths before it). Returns the decoded sample values (0 for insignificant ones) and the raw values of samples 1
        /// and 3; false for an impossible value.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool QuadVector(ref ulong buf, ref int bits, int U, int k, int i1, int r, int maxBits, int p, uint half,
            out Vector128<uint> vals, out uint v1, out uint v3)
        {
            int m0 = (U - (k & 1)) & -(r & 1), m1 = (U - (k >> 1 & 1)) & -(r >> 1 & 1);
            int m2 = (U - (k >> 2 & 1)) & -(r >> 2 & 1), m3 = (U - (k >> 3 & 1)) & -(r >> 3 & 1);
            int o2 = m0 + m1, o3 = o2 + m2, total = o3 + m3;
            var lanes = Vector128.Create(1u, 2, 4, 8);
            var sig = ~Vector128.Equals(Vector128.Create((uint)r) & lanes, Vector128<uint>.Zero);
            var m = Vector128.Create((uint)m0, (uint)m1, (uint)m2, (uint)m3);
            // samples 0 and 1 from the low 32 bits of the buffer, 2 and 3 from the 32 bits from offset o2 (U <= 16: each pair
            // fits its word)
            var raw = ShiftRight(Vector128.Create((uint)buf, (uint)buf, (uint)(buf >> o2), (uint)(buf >> o2)), Vector128.Create(0u, (uint)m0, 0u, (uint)m2))
                & (ShiftLeft(Vector128<uint>.One, m) - Vector128<uint>.One);
            var e1 = ~Vector128.Equals(Vector128.Create((uint)i1) & lanes, Vector128<uint>.Zero) & sig & Vector128<uint>.One;
            var v = raw | ShiftLeft(e1, m);
            buf >>= total; bits -= total;
            var mu = Vector128.ShiftRightLogical(v, 1) + Vector128<uint>.One;
            vals = (Vector128.ShiftLeft(mu, p + 1) | Vector128.Create(half) | Vector128.ShiftLeft(v, 31)) & sig;
            v1 = v.GetElement(1); v3 = v.GetElement(3);
            var bad = (Vector128.ShiftRightLogical(v, maxBits + 1) | Vector128.ShiftRightLogical(mu, maxBits)) & sig;
            return bad == Vector128<uint>.Zero;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Max(int a, int b) => a - ((a - b) & ((a - b) >> 31));

        /// <summary>The MagSgn value of sample j of a quad (0 bits and 0 if it is not significant); sets bad on an impossible value.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ulong Sample(ref ulong buf, ref int bits, ref Forward ms, int U, int k, int i1, int r, int j, int maxBits, ref bool bad)
        {
            int sig = r >> j & 1;
            int m = (U - (k >> j & 1)) & -sig;
            ulong raw;
            if (m > 32 || bits < m)
            {
                ms.Buf = buf; ms.Bits = bits;
                raw = ms.Take(m);
                buf = ms.Buf; bits = ms.Bits;
            }
            else
            {
                raw = buf & ((1UL << m) - 1);
                buf >>= m; bits -= m;
            }
            ulong v = raw | (ulong)(i1 >> j & sig) << m;
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

                // per quad of the current row: the VLC table entry and the residual u; two significance rows (index quad + 1)
                ushort* quadE = stackalloc ushort[qw + 2];
                byte* quadU = stackalloc byte[qw + 2];
                byte* rhoSecond = stackalloc byte[qw + 2];
                new Span<byte>(rhoSecond, qw + 2).Clear();
                byte* rhoCur = rhoSecond + 1;
                for (int qy = 0; qy < qh; qy++)
                {
                    bool firstRow = qy == 0;
                    ushort* lut = firstRow ? lut0 : lut1;
                    T* row0 = data + 2 * qy * w;
                    T* row1 = row0 + w;
                    bool hasRow1 = 2 * qy + 1 < h;
                    // pass 1: the quad row's significance, EMB patterns and residuals from the MEL and VLC streams; the new
                    // significance goes to rhoCur, the row above (rhoAbove) is only read
                    if (!VlcRow(ref mel, ref vlc, lut, firstRow, rhoAbove, rhoCur, quadE, quadU, qw)) return false;
                    // pass 2: exponent bounds and MagSgn values (7.3.7, 7.3.8)
                    if (!MagSgnRow(ref ms, row0, row1, rhoCur, quadE, quadU, expAbove, qw, w, firstRow, hasRow1, p, maxBits, half, signBit)) return false;
                    // the new row becomes the row above
                    { byte* t = rhoAbove; rhoAbove = rhoCur; rhoCur = t; }
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
