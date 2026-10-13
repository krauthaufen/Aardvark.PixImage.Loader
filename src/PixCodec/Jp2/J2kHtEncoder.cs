using System;
using System.Numerics;

namespace PixCodec
{
    /// <summary>The HT block encoder (T.814 Annex F): one cleanup pass, optionally followed by SigProp and MagRef.</summary>
    internal static unsafe class J2kHtEncoder
    {
        static ReadOnlySpan<byte> MelExp => new byte[] { 0, 0, 0, 1, 1, 1, 2, 2, 2, 3, 3, 4, 5 };

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

            public void Emit(ulong val, int len)
            {
                if (len > 32) { Emit(val & 0xFFFFFFFF, 32); val >>= 32; len -= 32; }
                if (len == 0) return;
                Tmp |= (val & ((1UL << len) - 1)) << Bits;
                Bits += len;
                while (Bits >= Max)
                {
                    uint b = (uint)Tmp & (Max == 8 ? 0xFFu : 0x7Fu);
                    Buf[Pos++] = (byte)b;
                    Tmp >>= Max; Bits -= Max;
                    Max = b == 0xFF ? 7 : 8;
                }
            }
        }

        /// <summary>Bit packer for the streams that are read backwards (VLC, MagRef); written forwards, reversed at the end.</summary>
        struct Backward
        {
            public byte* Buf;
            public int Pos, Bits;
            public uint Tmp, Last;

            public void Emit(uint cwd, int len)
            {
                for (; len > 0; len--, cwd >>= 1)
                {
                    Tmp |= (cwd & 1) << Bits;
                    Bits++;
                    if (Last > 0x8F && Tmp == 0x7F) Bits++;
                    if (Bits == 8)
                    {
                        Buf[Pos++] = (byte)Tmp;
                        Last = Tmp; Tmp = 0; Bits = 0;
                    }
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

        static void UVlc(int u, out uint pfx, out int lp, out uint sfx, out int ls, out uint ext, out int le)
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
                var vlc = new Backward { Buf = buf + msCap + melCap, Pos = 1, Bits = 4, Tmp = 15, Last = 255 };
                vlc.Buf[0] = 255;
                new Span<byte>(rhoRow, qw + 2).Clear();
                new Span<byte>(expRow, 2 * qw + 3).Clear();
                byte* rhoAbove = rhoRow + 1, expAbove = expRow + 1;

                Span<int> rho = stackalloc int[2], U = stackalloc int[2], u = stackalloc int[2], emb = stackalloc int[2], ek = stackalloc int[2];
                Span<int> eb0 = stackalloc int[2], eb1 = stackalloc int[2];
                Span<ulong> v = stackalloc ulong[8];
                Span<int> e = stackalloc int[8];
                Span<uint> cw = stackalloc uint[2];
                Span<int> cl = stackalloc int[2];
                for (int qy = 0; qy < qh; qy++)
                {
                    bool firstRow = qy == 0;
                    ushort* enc = firstRow ? enc0 : enc1;
                    int rhoLeft = 0, prevAboveRho = 0, oldExpLeft = 0;
                    T* row0 = data + 2 * qy * w;
                    T* row1 = 2 * qy + 1 < h ? row0 + w : null;
                    for (int qx = 0; qx < qw; qx += 2)
                    {
                        int pair = qx + 1 < qw ? 2 : 1;
                        int aboveLeft = prevAboveRho;
                        u[1] = 0; rho[1] = 0;
                        for (int i = 0; i < pair; i++)
                        {
                            int q = qx + i, x = 2 * q;
                            // MagSgn values and exponents of the four samples
                            int r = 0, emax = 0;
                            for (int j = 0; j < 4; j++)
                            {
                                int sx = x + (j >> 1);
                                T* row = (j & 1) == 0 ? row0 : row1;
                                ulong mu = 0, sign = 0;
                                if (row != null && sx < w)
                                {
                                    T t = row[sx];
                                    mu = ulong.CreateTruncating((t & magMask) >> p);
                                    sign = (t & signBit) != T.Zero ? 1UL : 0UL;
                                }
                                int ex = 0;
                                if (mu != 0)
                                {
                                    ulong vv = sign + 2 * (mu - 1);
                                    v[4 * i + j] = vv;
                                    ex = 64 - BitOperations.LeadingZeroCount(2 * mu - 1);
                                    r |= 1 << j;
                                    if (ex > emax) emax = ex;
                                }
                                e[4 * i + j] = ex;
                            }
                            rho[i] = r;
                            int kappa = 1;
                            int eNw = oldExpLeft, eN = expAbove[x], eNe = expAbove[x + 1], eNf = expAbove[x + 2];
                            oldExpLeft = eNe;
                            if (!firstRow && (r & (r - 1)) != 0)
                                kappa = Math.Max(1, Math.Max(Math.Max(eNw, eN), Math.Max(eNe, eNf)) - 1);
                            U[i] = Math.Max(emax, kappa);
                            u[i] = U[i] - kappa;
                            int m = 0;
                            if (u[i] > 0)
                                for (int j = 0; j < 4; j++) if (e[4 * i + j] == emax) m |= 1 << j;
                            emb[i] = m;
                            // context
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
                            if (c == 0) mel.Encode(r != 0 ? 1 : 0);
                            int t2 = enc[m | r << 4 | c << 8];
                            cw[i] = (uint)(t2 & 127); cl[i] = t2 >> 7 & 7; ek[i] = t2 >> 10;
                            rhoLeft = r;
                            eb0[i] = e[4 * i + 1]; eb1[i] = e[4 * i + 3];
                        }
                        // the row above is only replaced once both quads of the pair have used it
                        for (int i = 0; i < pair; i++)
                        {
                            int q = qx + i;
                            prevAboveRho = rhoAbove[q];
                            rhoAbove[q] = (byte)rho[i];
                            expAbove[2 * q] = (byte)eb0[i];
                            expAbove[2 * q + 1] = (byte)eb1[i];
                        }
                        // VLC stream: both CxtVLC codewords, then the U-VLC parts interleaved
                        vlc.Emit(cw[0], cl[0]);
                        if (pair == 2) vlc.Emit(cw[1], cl[1]);
                        uint p0, p1, s0, s1, x0, x1; int lp0, lp1, ls0, ls1, le0, le1;
                        if (pair == 2 && firstRow && u[0] > 0 && u[1] > 0)
                        {
                            if (Math.Min(u[0], u[1]) > 2)
                            {
                                mel.Encode(1);
                                UVlc(u[0] - 2, out p0, out lp0, out s0, out ls0, out x0, out le0);
                                UVlc(u[1] - 2, out p1, out lp1, out s1, out ls1, out x1, out le1);
                            }
                            else
                            {
                                mel.Encode(0);
                                UVlc(u[0], out p0, out lp0, out s0, out ls0, out x0, out le0);
                                if (u[0] > 2) { p1 = (uint)(u[1] - 1); lp1 = 1; s1 = x1 = 0; ls1 = le1 = 0; }
                                else UVlc(u[1], out p1, out lp1, out s1, out ls1, out x1, out le1);
                            }
                        }
                        else
                        {
                            UVlc(u[0], out p0, out lp0, out s0, out ls0, out x0, out le0);
                            UVlc(u[1], out p1, out lp1, out s1, out ls1, out x1, out le1);
                        }
                        vlc.Emit(p0, lp0); vlc.Emit(p1, lp1);
                        vlc.Emit(s0, ls0); vlc.Emit(s1, ls1);
                        vlc.Emit(x0, le0); vlc.Emit(x1, le1);
                        // MagSgn stream
                        for (int i = 0; i < pair; i++)
                        {
                            int r = rho[i];
                            for (int j = 0; j < 4; j++)
                            {
                                if ((r >> j & 1) == 0) continue;
                                ms.Emit(v[4 * i + j], U[i] - (ek[i] >> j & 1));
                            }
                        }
                    }
                }

                // ---- termination and assembly of the cleanup segment (F.4) ----
                mel.Terminate();
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
            var mr = new Backward { Buf = dst + cap, Pos = 0, Bits = 0, Tmp = 0, Last = 255 };
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
