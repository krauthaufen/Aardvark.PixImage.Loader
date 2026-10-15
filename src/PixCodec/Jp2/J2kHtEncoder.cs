using System;
using System.Numerics;
using System.Runtime.Intrinsics;

namespace PixCodec
{
    /// <summary>The HT block encoder (T.814 Annex F): one cleanup pass, optionally followed by SigProp and MagRef.</summary>
    internal static unsafe class J2kHtEncoder
    {
        static ReadOnlySpan<byte> MelExp => new byte[] { 0, 0, 0, 1, 1, 1, 2, 2, 2, 3, 3, 4, 5 };

        /// <summary>
        /// MagSgn values (sign + 2 (mu - 1)) and exponents (bit length of 2 mu - 1, 0 for mu = 0) of a sample row, zero from
        /// filled to pw. 32-bit samples whose magnitudes have at most 23 bits go four at a time: the bit length is read
        /// from the exponent of the value converted to float, which is exact below 2^24.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
        static void PrepareRow<T>(T* row, int filled, int pw, int p, int magBits, T magMask, T signBit, byte* ex, ulong* vx)
            where T : unmanaged, IBinaryInteger<T>
        {
            int x = 0;
            if (typeof(T) == typeof(uint) && Vector128.IsHardwareAccelerated && magBits - p <= 23)
            {
                uint* src = (uint*)row;
                var mask = Vector128.Create(0x7FFFFFFFu);
                var one = Vector128<uint>.One;
                for (; x + 4 <= filled; x += 4)
                {
                    var t = Vector128.Load(src + x);
                    var mu = Vector128.ShiftRightLogical(t & mask, p);
                    var v = Vector128.ShiftRightLogical(t, 31) + Vector128.ShiftLeft(mu - one, 1);      // sign + 2 (mu - 1)
                    var y = Vector128.ShiftLeft(mu, 1) - one;                                          // 2 mu - 1 (all ones for 0)
                    var e = Vector128.ShiftRightLogical(Vector128.ConvertToSingle(y.AsInt32()).AsUInt32(), 23) - Vector128.Create(126u);
                    e = Vector128.AndNot(e, Vector128.Equals(mu, Vector128<uint>.Zero));
                    // (for mu = 0 the value differs from the scalar code's, but such a sample is never emitted)
                    var (lo, hi) = Vector128.Widen(v);
                    lo.Store(vx + x); hi.Store(vx + x + 2);
                    var e16 = Vector128.Narrow(e, e);
                    *(uint*)(ex + x) = Vector128.Narrow(e16, e16).AsUInt32().ToScalar();
                }
            }
            for (; x < filled; x++)
            {
                T t = row[x];
                ulong mu = ulong.CreateTruncating((t & magMask) >> p);
                ulong sign = (t & signBit) != T.Zero ? 1UL : 0UL;
                vx[x] = sign + 2 * (mu - 1);
                // the bit length of 2 * mu - 1, or 0 for mu = 0 (without a branch: zeros are frequent and random)
                long t1 = (long)(2 * mu) - 1;
                ex[x] = (byte)(64 - BitOperations.LeadingZeroCount((ulong)(t1 & ~(t1 >> 63))));
            }
            for (; x < pw; x++) { ex[x] = 0; vx[x] = 0; }
        }

        [ThreadStatic] static byte[]? _exps;
        [ThreadStatic] static ulong[]? _vals;

        // Encoding tables: index = emb | rho << 4 | context << 8; value = codeword | length << 7 | emb_k << 10.
        static readonly ushort[] Enc0 = BuildEncode(J2kHtTables.Table0), Enc1 = BuildEncode(J2kHtTables.Table1);

        static ushort[] BuildEncode(uint[] table)
        {
            var enc = new ushort[8 * 256];
            var best = new int[8 * 256];
            Array.Fill(best, -1);
            foreach (uint e in table)
            {
                int c = (int)(e & 7), rho = (int)(e >> 3 & 15), uoff = (int)(e >> 7 & 1), ek = (int)(e >> 8 & 15), e1 = (int)(e >> 12 & 15);
                int w = (int)(e >> 16 & 127), len = (int)(e >> 23);
                for (int emb = 0; emb < 16; emb++)
                {
                    // the entry serves the EMB pattern if it tells nothing wrong: its known bits agree with the pattern
                    if ((emb & ~rho) != 0 || (emb != 0) != (uoff != 0) || (emb & ek) != e1) continue;
                    int i = emb | rho << 4 | c << 8;
                    int known = BitOperations.PopCount((uint)ek);
                    if (known <= best[i]) continue;
                    best[i] = known;
                    enc[i] = (ushort)(w | len << 7 | ek << 10);
                }
            }
            return enc;
        }

        /// <summary>Forward bit packer, least significant bit first, 7 bits after a 0xFF byte (MagSgn and SigProp streams).</summary>
        struct Forward
        {
            public byte* Buf;
            public int Pos, Bits, Max;
            public ulong Tmp;

            public void Init(byte* buf) { Buf = buf; Pos = 0; Bits = 0; Max = 8; Tmp = 0; }

            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
            public void Emit(ulong val, int len)
            {
                if (len > 32)
                {
                    // (not a recursive call: that would keep this method from being inlined)
                    Tmp |= (val & 0xFFFFFFFF) << Bits;
                    Bits += 32;
                    Flush();
                    val >>= 32; len -= 32;
                }
                Tmp |= (val & ((1UL << len) - 1)) << Bits;
                Bits += len;
                if (Bits >= 32) Flush();
            }

            /// <summary>Writes out the complete bytes (after it fewer bits than a byte takes are pending).</summary>
            public void Flush()
            {
                // four bytes at once while none of them is 0xFF (no stuffing involved)
                while (Max == 8 && Bits >= 32)
                {
                    uint x = (uint)Tmp;
                    if (((~x - 0x01010101u) & x & 0x80808080u) != 0) break;
                    System.Runtime.CompilerServices.Unsafe.WriteUnaligned(Buf + Pos, x);
                    Pos += 4; Tmp >>= 32; Bits -= 32;
                }
                while (Bits >= Max)
                {
                    uint b = (uint)Tmp & (Max == 8 ? 0xFFu : 0x7Fu);
                    Buf[Pos++] = (byte)b;
                    Tmp >>= Max; Bits -= Max;
                    Max = b == 0xFF ? 7 : 8;
                }
            }
        }

        /// <summary>
        /// Bit packer for the streams that are read backwards (VLC, MagRef); written forwards, reversed at the end.
        /// A byte whose low seven bits are all ones gets a 0 as its top bit when the byte before it is above 0x8F.
        /// </summary>
        struct Backward
        {
            public byte* Buf;
            public int Pos, Bits;
            public uint Last;
            public ulong Acc;

            /// <summary>The bits of the byte under construction (F.4: VLC_tmp / MR_tmp).</summary>
            public uint Tmp
            {
                get => (uint)Acc;
                set => Acc = value;
            }

            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
            public void Emit(uint cwd, int len)
            {
                Acc |= (ulong)cwd << Bits;
                Bits += len;
                if (Bits >= 40) Flush();
            }

            /// <summary>Writes out the complete bytes; afterwards the bits of the byte under construction remain.</summary>
            public void Flush()
            {
                // four bytes at once while none of them has its low seven bits set (no stuffing involved)
                while (Bits >= 32)
                {
                    uint x = (uint)Acc, y = x | 0x80808080u;
                    if (((~y - 0x01010101u) & y & 0x80808080u) != 0) break;
                    System.Runtime.CompilerServices.Unsafe.WriteUnaligned(Buf + Pos, x);
                    Pos += 4; Last = x >> 24; Acc >>= 32; Bits -= 32;
                }
                while (Bits >= 7)
                {
                    uint b = (uint)Acc & 0xFF;
                    if (Last > 0x8F && (b & 0x7F) == 0x7F)
                    {
                        // seven ones: the eighth bit is a stuffed 0
                        Buf[Pos++] = 0x7F; Last = 0x7F;
                        Acc >>= 7; Bits -= 7;
                    }
                    else if (Bits >= 8)
                    {
                        Buf[Pos++] = (byte)b; Last = b;
                        Acc >>= 8; Bits -= 8;
                    }
                    else break;
                }
            }
        }

        struct Mel
        {
            public byte* Buf;
            public int Pos, Rem, K, Run, T;
            public uint Tmp;

            public void Init(byte* buf) { Buf = buf; Pos = 0; Rem = 8; Tmp = 0; K = 0; Run = 0; T = 1; }

            void Bit(uint bit)
            {
                Tmp = 2 * Tmp + bit;
                if (--Rem == 0)
                {
                    Buf[Pos++] = (byte)Tmp;
                    Rem = Tmp == 0xFF ? 7 : 8;
                    Tmp = 0;
                }
            }

            public void Encode(int sym)
            {
                if (sym == 0)
                {
                    if (++Run >= T)
                    {
                        Bit(1);
                        Run = 0;
                        if (K < 12) K++;
                        T = 1 << MelExp[K];
                    }
                }
                else
                {
                    Bit(0);
                    for (int eval = MelExp[K]; eval > 0;) { eval--; Bit((uint)(Run >> eval) & 1); }
                    Run = 0;
                    if (K > 0) K--;
                    T = 1 << MelExp[K];
                }
            }

            public void Terminate() { if (Run > 0) Bit(1); }
        }

        /// <summary>
        /// The exponents of a quad's four samples (j = 0: column x row 0, 1: x row 1, 2: x + 1 row 0, 3: x + 1 row 1) give its
        /// significance pattern (returned), U, u and the EMB pattern.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        static int QuadInfo(byte* ex0, byte* ex1, int x, bool firstRow, ref int oldExpLeft, byte* expAbove, out int U, out int u, out int m)
        {
            int ea = ex0[x], eb = ex1[x], ec = ex0[x + 1], ed = ex1[x + 1];
            int r = (ea != 0 ? 1 : 0) | (eb != 0 ? 2 : 0) | (ec != 0 ? 4 : 0) | (ed != 0 ? 8 : 0);
            int emax = Math.Max(Math.Max(ea, eb), Math.Max(ec, ed));
            int eNw = oldExpLeft, eN = expAbove[x], eNe = expAbove[x + 1], eNf = expAbove[x + 2];
            oldExpLeft = eNe;
            int kappa = 1;
            if (!firstRow && (r & (r - 1)) != 0)
                kappa = Math.Max(1, Math.Max(Math.Max(eNw, eN), Math.Max(eNe, eNf)) - 1);
            U = Math.Max(emax, kappa);
            u = U - kappa;
            m = u > 0 ? (ea == emax ? 1 : 0) | (eb == emax ? 2 : 0) | (ec == emax ? 4 : 0) | (ed == emax ? 8 : 0) : 0;
            return r;
        }

        /// <summary>The context of quad q from its neighbours' significance (aboveLeft moves on to the quad above).</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        static int Context(bool firstRow, int rhoLeft, ref int aboveLeft, byte* rhoAbove, int q)
        {
            if (firstRow) return ((rhoLeft & 3) != 0 ? 1 : 0) | (rhoLeft >> 1 & 2) | (rhoLeft >> 1 & 4);
            int above = rhoAbove[q], aboveRight = rhoAbove[q + 1];
            int c = ((aboveLeft & 8) != 0 || (above & 2) != 0 ? 1 : 0)
                | ((rhoLeft & 12) != 0 ? 2 : 0)
                | ((above & 8) != 0 || (aboveRight & 2) != 0 ? 4 : 0);
            aboveLeft = above;
            return c;
        }

        /// <summary>The MagSgn bits of a quad's significant samples, in sample order.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        static void QuadMs(ref ulong acc, ref int bits, ref Forward f, int r, int U, int ek, ulong* vx0, ulong* vx1, int x)
        {
            // insignificant samples emit nothing: length 0 (no branch on the pattern)
            PutMs(ref acc, ref bits, ref f, vx0[x], (U - (ek & 1)) & -(r & 1));
            PutMs(ref acc, ref bits, ref f, vx1[x], (U - (ek >> 1 & 1)) & -(r >> 1 & 1));
            PutMs(ref acc, ref bits, ref f, vx0[x + 1], (U - (ek >> 2 & 1)) & -(r >> 2 & 1));
            PutMs(ref acc, ref bits, ref f, vx1[x + 1], (U - (ek >> 3 & 1)) & -(r >> 3 & 1));
        }

        // Backward.Emit / Forward.Emit on accumulator and bit count held in the caller's locals: only a flush goes through
        // the packer structure (whose address is taken), so the locals stay in registers.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        static void PutVlc(ref ulong acc, ref int bits, ref Backward b, uint cwd, int len)
        {
            acc |= (ulong)cwd << bits;
            bits += len;
            if (bits >= 40)
            {
                b.Acc = acc; b.Bits = bits;
                b.Flush();
                acc = b.Acc; bits = b.Bits;
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        static void PutMs(ref ulong acc, ref int bits, ref Forward f, ulong val, int len)
        {
            if (len > 32)
            {
                f.Tmp = acc; f.Bits = bits;
                f.Emit(val, len);
                acc = f.Tmp; bits = f.Bits;
                return;
            }
            acc |= (val & ((1UL << len) - 1)) << bits;
            bits += len;
            if (bits >= 32)
            {
                f.Tmp = acc; f.Bits = bits;
                f.Flush();
                acc = f.Tmp; bits = f.Bits;
            }
        }

        // UVlc by table for the u that occur (u <= 2 * 64 + 2): pfx | lp << 8 | sfx << 12 | ls << 20 | ext << 24 | le << 32
        static readonly ulong[] UVlcTable = BuildUVlc();

        static ulong[] BuildUVlc()
        {
            var t = new ulong[160];
            for (int u = 0; u < t.Length; u++)
            {
                UVlcSlow(u, out uint pfx, out int lp, out uint sfx, out int ls, out uint ext, out int le);
                t[u] = pfx | (ulong)lp << 8 | (ulong)sfx << 12 | (ulong)ls << 20 | (ulong)ext << 24 | (ulong)le << 32;
            }
            return t;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        static void UVlc(int u, out uint pfx, out int lp, out uint sfx, out int ls, out uint ext, out int le)
        {
            if ((uint)u >= (uint)UVlcTable.Length) { UVlcSlow(u, out pfx, out lp, out sfx, out ls, out ext, out le); return; }
            ulong e = UVlcTable[u];
            pfx = (uint)e & 255; lp = (int)(e >> 8) & 15; sfx = (uint)(e >> 12) & 255; ls = (int)(e >> 20) & 15;
            ext = (uint)(e >> 24) & 255; le = (int)(e >> 32) & 15;
        }

        static void UVlcSlow(int u, out uint pfx, out int lp, out uint sfx, out int ls, out uint ext, out int le)
        {
            sfx = ext = 0; ls = le = 0;
            if (u == 0) { pfx = 0; lp = 0; }
            else if (u == 1) { pfx = 1; lp = 1; }
            else if (u == 2) { pfx = 2; lp = 2; }
            else if (u <= 4) { pfx = 4; lp = 3; sfx = (uint)(u - 3); ls = 1; }
            else if (u <= 32) { pfx = 0; lp = 3; sfx = (uint)(u - 5); ls = 5; }
            else { pfx = 0; lp = 3; sfx = (uint)(28 + (u - 33) % 4); ls = 5; ext = (uint)((u - 33) / 4); le = 4; }
        }

        /// <summary>
        /// Encodes a block of sign-magnitude samples (top bit = sign). The cleanup pass codes the magnitudes down to
        /// bit plane <paramref name="p"/>; with <paramref name="refine"/> (p &gt; 0) a SigProp and a MagRef pass add
        /// plane p - 1. Returns false if the block cannot be coded (magnitude streams too long for one HT segment).
        /// </summary>
        public static bool Encode<T>(J2kBlockCoder coder, T* data, int w, int h, int p, bool refine, bool causal, J2kEncBlock dst)
            where T : unmanaged, IBinaryInteger<T>
        {
            int n = w * h;
            int typeBits = sizeof(T) * 8;
            T signBit = T.One << (typeBits - 1);
            T magMask = signBit - T.One;
            T any = T.Zero;
            for (int i = 0; i < n; i++) any |= data[i] & magMask;
            dst.Ht = true;
            if ((any >> p) == T.Zero) { dst.NumBps = 0; dst.Passes = 0; dst.Data = Array.Empty<byte>(); return true; }
            int magBits = typeBits - int.CreateTruncating(T.LeadingZeroCount(any));
            dst.NumBps = p + 1;
            dst.MagBits = magBits;
            if (p == 0) refine = false;

            int qw = (w + 1) >> 1, qh = (h + 1) >> 1;
            var s = coder.Ht ??= new J2kHtScratch();
            if (s.Rho.Length < qw + 2) s.Rho = new byte[qw + 2];
            if (s.Exp.Length < 2 * qw + 3) s.Exp = new byte[2 * qw + 3];
            // MagSgn: at most magBits + 1 bits per sample (plus stuffing); VLC: at most 19 bits per quad; MEL: 1 bit per quad and pair
            int msCap = (int)(((long)n * (magBits + 2) + 7) / 7) + 16, vlcCap = qw * qh * 3 + 16, melCap = qw * qh / 2 + 16;
            int refCap = refine ? n / 2 + 32 : 0;
            coder.EnsureBuf(msCap + vlcCap + melCap + 2 * refCap + 16);

            fixed (byte* buf = coder.Buf, rhoRow = s.Rho, expRow = s.Exp)
            fixed (ushort* enc0 = Enc0, enc1 = Enc1)
            {
                var ms = default(Forward); ms.Init(buf);
                var mel = default(Mel); mel.Init(buf + msCap);
                var vlc = new Backward { Buf = buf + msCap + melCap, Pos = 1, Bits = 4, Acc = 15, Last = 255 };
                vlc.Buf[0] = 255;
                new Span<byte>(rhoRow, qw + 2).Clear();
                new Span<byte>(expRow, 2 * qw + 3).Clear();
                byte* rhoAbove = rhoRow + 1, expAbove = expRow + 1;

                // the two sample rows of a quad row, prepared once: exponents and MagSgn values per column (zero past the edge)
                int pw = 2 * qw + 2;
                if (_exps is null || _exps.Length < 2 * pw) { _exps = new byte[2 * pw]; _vals = new ulong[2 * pw]; }
                // the bits under construction of the VLC and MagSgn streams live in locals here (see PutVlc, PutMs)
                ulong vlcAcc = vlc.Acc, msAcc = ms.Tmp;
                int vlcBits = vlc.Bits, msBits = ms.Bits;
                fixed (byte* exps = _exps)
                fixed (ulong* vals = _vals)
                for (int qy = 0; qy < qh; qy++)
                {
                    bool firstRow = qy == 0;
                    ushort* enc = firstRow ? enc0 : enc1;
                    int rhoLeft = 0, prevAboveRho = 0, oldExpLeft = 0;
                    T* row0 = data + 2 * qy * w;
                    T* row1 = 2 * qy + 1 < h ? row0 + w : null;
                    for (int k = 0; k < 2; k++)
                    {
                        T* row = k == 0 ? row0 : row1;
                        byte* ex = exps + k * pw;
                        ulong* vx = vals + k * pw;
                        PrepareRow(row, row == null ? 0 : w, pw, p, magBits, magMask, signBit, ex, vx);
                    }
                    byte* ex0 = exps, ex1 = exps + pw;
                    ulong* vx0 = vals, vx1 = vals + pw;
                    for (int qx = 0; qx < qw; qx += 2)
                    {
                        // a pair of quads, A and B (B only if the row has it), all state in locals
                        bool two = qx + 1 < qw;
                        int aboveLeft = prevAboveRho;
                        int xA = 2 * qx;
                        int rA = QuadInfo(ex0, ex1, xA, firstRow, ref oldExpLeft, expAbove, out int UA, out int uA, out int mA);
                        int cA = Context(firstRow, rhoLeft, ref aboveLeft, rhoAbove, qx);
                        if (cA == 0) mel.Encode(rA != 0 ? 1 : 0);
                        int tA = enc[mA | rA << 4 | cA << 8];
                        rhoLeft = rA;
                        int rB = 0, UB = 0, uB = 0, tB = 0;
                        if (two)
                        {
                            rB = QuadInfo(ex0, ex1, xA + 2, firstRow, ref oldExpLeft, expAbove, out UB, out uB, out int mB);
                            int cB = Context(firstRow, rhoLeft, ref aboveLeft, rhoAbove, qx + 1);
                            if (cB == 0) mel.Encode(rB != 0 ? 1 : 0);
                            tB = enc[mB | rB << 4 | cB << 8];
                            rhoLeft = rB;
                        }
                        // the row above is only replaced once both quads of the pair have used it
                        prevAboveRho = rhoAbove[qx];
                        rhoAbove[qx] = (byte)rA;
                        expAbove[xA] = ex1[xA]; expAbove[xA + 1] = ex1[xA + 1];
                        if (two)
                        {
                            prevAboveRho = rhoAbove[qx + 1];
                            rhoAbove[qx + 1] = (byte)rB;
                            expAbove[xA + 2] = ex1[xA + 2]; expAbove[xA + 3] = ex1[xA + 3];
                        }
                        // VLC stream: both CxtVLC codewords, then the U-VLC parts interleaved
                        PutVlc(ref vlcAcc, ref vlcBits, ref vlc, (uint)(tA & 127), tA >> 7 & 7);
                        if (two) PutVlc(ref vlcAcc, ref vlcBits, ref vlc, (uint)(tB & 127), tB >> 7 & 7);
                        uint p0, p1, s0, s1, x0, x1; int lp0, lp1, ls0, ls1, le0, le1;
                        if (two && firstRow && uA > 0 && uB > 0)
                        {
                            if (Math.Min(uA, uB) > 2)
                            {
                                mel.Encode(1);
                                UVlc(uA - 2, out p0, out lp0, out s0, out ls0, out x0, out le0);
                                UVlc(uB - 2, out p1, out lp1, out s1, out ls1, out x1, out le1);
                            }
                            else
                            {
                                mel.Encode(0);
                                UVlc(uA, out p0, out lp0, out s0, out ls0, out x0, out le0);
                                if (uA > 2) { p1 = (uint)(uB - 1); lp1 = 1; s1 = x1 = 0; ls1 = le1 = 0; }
                                else UVlc(uB, out p1, out lp1, out s1, out ls1, out x1, out le1);
                            }
                        }
                        else
                        {
                            UVlc(uA, out p0, out lp0, out s0, out ls0, out x0, out le0);
                            UVlc(uB, out p1, out lp1, out s1, out ls1, out x1, out le1);
                        }
                        PutVlc(ref vlcAcc, ref vlcBits, ref vlc, p0, lp0); PutVlc(ref vlcAcc, ref vlcBits, ref vlc, p1, lp1);
                        PutVlc(ref vlcAcc, ref vlcBits, ref vlc, s0, ls0); PutVlc(ref vlcAcc, ref vlcBits, ref vlc, s1, ls1);
                        PutVlc(ref vlcAcc, ref vlcBits, ref vlc, x0, le0); PutVlc(ref vlcAcc, ref vlcBits, ref vlc, x1, le1);
                        // MagSgn stream
                        if (rA != 0) QuadMs(ref msAcc, ref msBits, ref ms, rA, UA, tA >> 10, vx0, vx1, xA);
                        if (rB != 0) QuadMs(ref msAcc, ref msBits, ref ms, rB, UB, tB >> 10, vx0, vx1, xA + 2);
                    }
                }

                // ---- termination and assembly of the cleanup segment (F.4) ----
                vlc.Acc = vlcAcc; vlc.Bits = vlcBits; ms.Tmp = msAcc; ms.Bits = msBits;
                mel.Terminate();
                ms.Flush(); vlc.Flush();
                if (ms.Bits > 0)
                {
                    while (ms.Bits < ms.Max) { ms.Tmp |= 1UL << ms.Bits; ms.Bits++; }
                    if (ms.Tmp != 0xFF) ms.Buf[ms.Pos++] = (byte)ms.Tmp;
                }
                else if (ms.Max == 7) ms.Pos--;
                {
                    uint melTmp = mel.Tmp << mel.Rem;
                    uint melMask = (0xFFu << mel.Rem) & 0xFF;
                    uint vlcMask = vlc.Bits == 0 ? 0u : 0xFFu >> (8 - vlc.Bits);
                    if ((melMask | vlcMask) != 0)
                    {
                        uint fuse = melTmp | vlc.Tmp;
                        if ((((fuse ^ melTmp) & melMask) | ((fuse ^ vlc.Tmp) & vlcMask)) == 0 && fuse != 0xFF)
                            mel.Buf[mel.Pos] = (byte)fuse;
                        else
                        {
                            mel.Buf[mel.Pos] = (byte)melTmp;
                            vlc.Buf[vlc.Pos++] = (byte)vlc.Tmp;
                        }
                        mel.Pos++;
                    }
                }
                int scup = mel.Pos + vlc.Pos;
                int lcup = ms.Pos + scup;
                if (scup > 4079 || lcup >= 65535) return false;
                if (lcup < 2) return false;

                // refinement passes go behind everything else in the scratch buffer
                byte* refBuf = buf + msCap + melCap + vlcCap;
                int lref = 0;
                if (refine) lref = EncodeRefinement(coder, data, w, h, p, causal, refBuf, refCap);

                var outData = new byte[lcup + lref];
                fixed (byte* o = outData)
                {
                    Buffer.MemoryCopy(ms.Buf, o, ms.Pos, ms.Pos);
                    Buffer.MemoryCopy(mel.Buf, o + ms.Pos, mel.Pos, mel.Pos);
                    for (int i = 0; i < vlc.Pos; i++) o[ms.Pos + mel.Pos + i] = vlc.Buf[vlc.Pos - 1 - i];
                    o[lcup - 1] = (byte)(scup >> 4);
                    o[lcup - 2] = (byte)((o[lcup - 2] & 0xF0) | (scup & 0x0F));
                    if (lref > 0) Buffer.MemoryCopy(refBuf, o + lcup, lref, lref);
                }
                dst.Data = outData;
                dst.Passes = refine ? 3 : 1;
                dst.PassLen = refine ? new[] { lcup, lcup + lref, lcup + lref } : new[] { lcup };
                dst.PassTerm = refine ? new[] { true, false, true } : new[] { true };
                dst.PassDist = new double[dst.Passes];
            }
            return true;
        }

        /// <summary>SigProp and MagRef passes for bit plane p - 1, fused into one refinement segment. Returns its length.</summary>
        static int EncodeRefinement<T>(J2kBlockCoder coder, T* data, int w, int h, int p, bool causal, byte* dst, int cap)
            where T : unmanaged, IBinaryInteger<T>
        {
            var s = coder.Ht!;
            int stride = w + 2;
            int sigLen = stride * (h + 2);
            if (s.Sig.Length < sigLen) s.Sig = new byte[sigLen];
            int typeBits = sizeof(T) * 8;
            T signBit = T.One << (typeBits - 1), magMask = signBit - T.One;
            var sp = default(Forward); sp.Init(dst);
            var mr = new Backward { Buf = dst + cap, Pos = 0, Bits = 0, Acc = 0, Last = 255 };
            fixed (byte* sig = s.Sig)
            {
                new Span<byte>(sig, sigLen).Clear();
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        if (((data[y * w + x] & magMask) >> p) != T.Zero) sig[(y + 1) * stride + x + 1] = 1;
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
                                if (!(causal && y == y0 + 3)) mbr |= f[stride - 1] | f[stride] | f[stride + 1];
                                if (mbr == 0) continue;
                                uint bit = uint.CreateTruncating((data[y * w + x] & magMask) >> (p - 1)) & 1;
                                sp.Emit(bit, 1);
                                if (bit != 0) *f = 2;
                            }
                        for (int x = x0; x < x1; x++)
                            for (int y = y0; y < y1; y++)
                                if (sig[(y + 1) * stride + x + 1] == 2)
                                {
                                    sp.Emit((data[y * w + x] & signBit) != T.Zero ? 1UL : 0UL, 1);
                                    sig[(y + 1) * stride + x + 1] = 3;
                                }
                    }
                }
                for (int y0 = 0; y0 < h; y0 += 4)
                {
                    int y1 = Math.Min(y0 + 4, h);
                    for (int x = 0; x < w; x++)
                        for (int y = y0; y < y1; y++)
                            if (sig[(y + 1) * stride + x + 1] == 1)
                                mr.Emit(uint.CreateTruncating((data[y * w + x] & magMask) >> (p - 1)) & 1, 1);
                }
            }
            // termSPandMRPackers
            sp.Flush(); mr.Flush();
            uint spTmp = (uint)sp.Tmp;
            uint spMask = sp.Bits == 0 ? 0u : 0xFFu >> (8 - sp.Bits);
            spMask |= (1u << sp.Max) & 0x80;
            uint mrMask = mr.Bits == 0 ? 0u : 0xFFu >> (8 - mr.Bits);
            if ((spMask | mrMask) != 0)
            {
                uint fuse = spTmp | mr.Tmp;
                if ((((fuse ^ spTmp) & spMask) | ((fuse ^ mr.Tmp) & mrMask)) == 0) sp.Buf[sp.Pos] = (byte)fuse;
                else
                {
                    sp.Buf[sp.Pos] = (byte)spTmp;
                    mr.Buf[mr.Pos++] = (byte)mr.Tmp;
                }
                sp.Pos++;
            }
            for (int i = 0; i < mr.Pos; i++) dst[sp.Pos + i] = mr.Buf[mr.Pos - 1 - i];
            return sp.Pos + mr.Pos;
        }
    }
}
