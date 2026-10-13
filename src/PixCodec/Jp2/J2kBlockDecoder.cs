using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace PixCodec
{
    /// <summary>
    /// Per-thread scratch and the Tier-1 (EBCOT) block decoder of T.800 Annex D.
    ///
    /// A decoded sample is sign-magnitude with ONE fractional bit: bit 0 is half a quantization step, the top bit of
    /// the element type is the sign. A sample that is cut off after bit plane p carries a 1 in the position below p
    /// (the midpoint of what is still undecided), so a fully decoded sample is 2 * magnitude + 1.
    ///
    /// Flags: one ushort per sample, grouped so that the four samples of a stripe column are adjacent (a column is
    /// one 64 bit load). A stripe row of flags is 4 * (w + 2) entries, with one unused stripe above and below.
    /// </summary>
    internal sealed unsafe partial class J2kBlockCoder
    {
        public uint[] Data32 = new uint[4096];
        public ulong[]? Data64;
        public ushort[] Flags = new ushort[18 * 66 * 4];
        public byte[] Buf = new byte[8192];
        readonly byte[] _cx = new byte[J2kT1.NumCtx];
        // HT scratch lives here too (see J2kHtDecoder)
        public J2kHtScratch? Ht;

        const ulong Sig4 = 0x1000100010001000, Visit4 = 0x2000200020002000, Nb4 = 0x00FF00FF00FF00FF;

        public void EnsureBuf(int n)
        {
            if (Buf.Length < n) Buf = new byte[Math.Max(n, Buf.Length * 2)];
        }

        /// <summary>
        /// Decodes <paramref name="passes"/> coding passes into data (w * h, row-major, cleared here).
        /// segStart/segLen describe the codeword segments inside Buf (each followed by two 0xFF bytes).
        /// </summary>
        public void Decode<T>(T* data, int w, int h, int orient, int numBps, int passes, int style,
            ReadOnlySpan<int> segStart, ReadOnlySpan<int> segLen) where T : unmanaged, IBinaryInteger<T>
        {
            new Span<T>(data, w * h).Clear();
            if (passes <= 0 || numBps <= 0) return;
            if (passes > 3 * numBps - 2) passes = 3 * numBps - 2;
            int stride = (w + 2) * 4;
            int stripes = (h + 3) >> 2;
            int flagLen = (stripes + 2) * stride;
            if (Flags.Length < flagLen) Flags = new ushort[flagLen];
            Array.Clear(Flags, 0, flagLen);
            J2kT1.ResetContexts(_cx);
            bool causal = (style & J2kCod.Causal) != 0;
            bool bypass = (style & J2kCod.Bypass) != 0;
            bool termAll = (style & J2kCod.TermAll) != 0;
            bool reset = (style & J2kCod.Reset) != 0;
            bool segSym = (style & J2kCod.SegSym) != 0;
            T signBit = T.One << (sizeof(T) * 8 - 1);

            fixed (ushort* flags = Flags)
            fixed (byte* buf = Buf, cx = _cx, zc = &J2kT1.Zc[orient * 256], sc = J2kT1.Sc)
            {
                var mq = default(J2kMqDecoder);
                var raw = default(J2kRawDecoder);
                int seg = 0, segEnd = 0;   // segEnd: first pass after the current segment
                bool isRaw = false;
                int bp = numBps - 1;
                for (int pass = 0; pass < passes; pass++)
                {
                    int type = pass == 0 ? 2 : (pass - 1) % 3;   // 0 significance propagation, 1 refinement, 2 cleanup
                    if (pass > 0 && type == 0) bp--;
                    if (pass == segEnd)
                    {
                        if (seg >= segStart.Length) break;
                        isRaw = bypass && pass >= 10 && type != 2;
                        if (isRaw) raw.Init(buf + segStart[seg], segLen[seg]);
                        else mq.Init(buf + segStart[seg]);
                        segEnd = pass + SegmentPasses(style, pass);
                        seg++;
                    }
                    T one = T.One << (bp + 1);
                    if (type == 2) Cleanup(ref mq, data, flags, w, h, stride, one, signBit, causal, segSym, cx, zc, sc);
                    else if (type == 0)
                    {
                        if (isRaw) SigPropRaw(ref raw, data, flags, w, h, stride, one, signBit, causal);
                        else SigProp(ref mq, data, flags, w, h, stride, one, signBit, causal, cx, zc, sc);
                    }
                    else
                    {
                        if (isRaw) RefineRaw(ref raw, data, flags, w, h, stride, one);
                        else Refine(ref mq, data, flags, w, h, stride, one, cx);
                    }
                    if (reset) J2kT1.ResetContexts(_cx);
                }
            }
        }

        /// <summary>Number of passes from <paramref name="pass"/> to the end of its codeword segment.</summary>
        public static int SegmentPasses(int style, int pass)
        {
            if ((style & J2kCod.TermAll) != 0) return 1;
            if ((style & J2kCod.Bypass) != 0)
            {
                if (pass < 10) return 10 - pass;
                int m = (pass - 10) % 3;
                return m == 0 ? 2 : 1;
            }
            return 1 << 20;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void SetSignificant(ushort* f, int k, int stride, int neg, bool causal)
        {
            int north = k == 0 ? 3 - stride : -1;
            int south = k == 3 ? stride - 3 : 1;
            f[0] |= J2kT1.FSig;
            f[-4] |= (ushort)(J2kT1.FE | neg << 9);
            f[4] |= (ushort)(J2kT1.FW | neg << 8);
            f[south] |= (ushort)(J2kT1.FN | neg << 10);
            f[south - 4] |= J2kT1.FNE;
            f[south + 4] |= J2kT1.FNW;
            if (k != 0 || !causal)
            {
                f[north] |= (ushort)(J2kT1.FS | neg << 11);
                f[north - 4] |= J2kT1.FSE;
                f[north + 4] |= J2kT1.FSW;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int ScIndex(int f) => (f & 0xF) | ((f >> 4) & 0xF0);

        static void Cleanup<T>(ref J2kMqDecoder mqRef, T* data, ushort* flags, int w, int h, int stride, T one, T signBit,
            bool causal, bool segSym, byte* cx, byte* zc, byte* sc) where T : unmanaged, IBinaryInteger<T>
        {
            var mq = mqRef;
            T sigValue = one | (one >> 1);
            for (int y0 = 0; y0 < h; y0 += 4)
            {
                int rows = Math.Min(4, h - y0);
                ushort* fp = flags + ((y0 >> 2) + 1) * stride + 4;
                T* dp = data + y0 * w;
                for (int x = 0; x < w; x++, fp += 4, dp++)
                {
                    ulong col = Unsafe.ReadUnaligned<ulong>(fp);
                    int k = 0;
                    if (col == 0 && rows == 4)
                    {
                        if (mq.Decode(ref cx[J2kT1.CtxRun]) == 0) continue;
                        k = mq.Decode(ref cx[J2kT1.CtxUni]) << 1;
                        k |= mq.Decode(ref cx[J2kT1.CtxUni]);
                        int v = sc[ScIndex(fp[k])];
                        int neg = mq.Decode(ref cx[v & 31]) ^ (v >> 7);
                        dp[k * w] = neg != 0 ? sigValue | signBit : sigValue;
                        SetSignificant(fp + k, k, stride, neg, causal);
                        k++;
                    }
                    else if ((((col >> 12) | (col >> 13)) & 0x0001000100010001) == 0x0001000100010001)
                    {
                        // every sample was coded in this bit plane already
                        Unsafe.WriteUnaligned(fp, col & ~Visit4);
                        continue;
                    }
                    for (; k < rows; k++)
                    {
                        int f = fp[k];
                        if ((f & (J2kT1.FSig | J2kT1.FVisit)) != 0) continue;
                        if (mq.Decode(ref cx[zc[f & 0xFF]]) == 0) continue;
                        int v = sc[ScIndex(f)];
                        int neg = mq.Decode(ref cx[v & 31]) ^ (v >> 7);
                        dp[k * w] = neg != 0 ? sigValue | signBit : sigValue;
                        SetSignificant(fp + k, k, stride, neg, causal);
                    }
                    Unsafe.WriteUnaligned(fp, Unsafe.ReadUnaligned<ulong>(fp) & ~Visit4);
                }
            }
            if (segSym)
            {
                // 1010; a mismatch means a damaged block, which is decoded as far as it goes
                for (int i = 0; i < 4; i++) mq.Decode(ref cx[J2kT1.CtxUni]);
            }
            mqRef = mq;
        }

        static void SigProp<T>(ref J2kMqDecoder mqRef, T* data, ushort* flags, int w, int h, int stride, T one, T signBit,
            bool causal, byte* cx, byte* zc, byte* sc) where T : unmanaged, IBinaryInteger<T>
        {
            var mq = mqRef;
            T sigValue = one | (one >> 1);
            for (int y0 = 0; y0 < h; y0 += 4)
            {
                int rows = Math.Min(4, h - y0);
                ushort* fp = flags + ((y0 >> 2) + 1) * stride + 4;
                T* dp = data + y0 * w;
                for (int x = 0; x < w; x++, fp += 4, dp++)
                {
                    if ((Unsafe.ReadUnaligned<ulong>(fp) & Nb4) == 0) continue;
                    for (int k = 0; k < rows; k++)
                    {
                        int f = fp[k];
                        if ((f & J2kT1.FSig) != 0 || (f & 0xFF) == 0) continue;
                        if (mq.Decode(ref cx[zc[f & 0xFF]]) != 0)
                        {
                            int v = sc[ScIndex(f)];
                            int neg = mq.Decode(ref cx[v & 31]) ^ (v >> 7);
                            dp[k * w] = neg != 0 ? sigValue | signBit : sigValue;
                            SetSignificant(fp + k, k, stride, neg, causal);
                        }
                        fp[k] |= J2kT1.FVisit;
                    }
                }
            }
            mqRef = mq;
        }

        static void SigPropRaw<T>(ref J2kRawDecoder rawRef, T* data, ushort* flags, int w, int h, int stride, T one, T signBit,
            bool causal) where T : unmanaged, IBinaryInteger<T>
        {
            var raw = rawRef;
            T sigValue = one | (one >> 1);
            for (int y0 = 0; y0 < h; y0 += 4)
            {
                int rows = Math.Min(4, h - y0);
                ushort* fp = flags + ((y0 >> 2) + 1) * stride + 4;
                T* dp = data + y0 * w;
                for (int x = 0; x < w; x++, fp += 4, dp++)
                {
                    if ((Unsafe.ReadUnaligned<ulong>(fp) & Nb4) == 0) continue;
                    for (int k = 0; k < rows; k++)
                    {
                        int f = fp[k];
                        if ((f & J2kT1.FSig) != 0 || (f & 0xFF) == 0) continue;
                        if (raw.Bit() != 0)
                        {
                            int neg = raw.Bit();
                            dp[k * w] = neg != 0 ? sigValue | signBit : sigValue;
                            SetSignificant(fp + k, k, stride, neg, causal);
                        }
                        fp[k] |= J2kT1.FVisit;
                    }
                }
            }
            rawRef = raw;
        }

        static void Refine<T>(ref J2kMqDecoder mqRef, T* data, ushort* flags, int w, int h, int stride, T one, byte* cx)
            where T : unmanaged, IBinaryInteger<T>
        {
            var mq = mqRef;
            T half = one >> 1, both = one | half;
            for (int y0 = 0; y0 < h; y0 += 4)
            {
                int rows = Math.Min(4, h - y0);
                ushort* fp = flags + ((y0 >> 2) + 1) * stride + 4;
                T* dp = data + y0 * w;
                for (int x = 0; x < w; x++, fp += 4, dp++)
                {
                    if ((Unsafe.ReadUnaligned<ulong>(fp) & Sig4) == 0) continue;
                    for (int k = 0; k < rows; k++)
                    {
                        int f = fp[k];
                        if ((f & (J2kT1.FSig | J2kT1.FVisit)) != J2kT1.FSig) continue;
                        int c = (f & J2kT1.FRefined) != 0 ? 16 : (f & 0xFF) != 0 ? 15 : 14;
                        dp[k * w] ^= mq.Decode(ref cx[c]) != 0 ? half : both;
                        fp[k] = (ushort)(f | J2kT1.FRefined);
                    }
                }
            }
            mqRef = mq;
        }

        static void RefineRaw<T>(ref J2kRawDecoder rawRef, T* data, ushort* flags, int w, int h, int stride, T one)
            where T : unmanaged, IBinaryInteger<T>
        {
            var raw = rawRef;
            T half = one >> 1, both = one | half;
            for (int y0 = 0; y0 < h; y0 += 4)
            {
                int rows = Math.Min(4, h - y0);
                ushort* fp = flags + ((y0 >> 2) + 1) * stride + 4;
                T* dp = data + y0 * w;
                for (int x = 0; x < w; x++, fp += 4, dp++)
                {
                    if ((Unsafe.ReadUnaligned<ulong>(fp) & Sig4) == 0) continue;
                    for (int k = 0; k < rows; k++)
                    {
                        int f = fp[k];
                        if ((f & (J2kT1.FSig | J2kT1.FVisit)) != J2kT1.FSig) continue;
                        dp[k * w] ^= raw.Bit() != 0 ? half : both;
                        fp[k] = (ushort)(f | J2kT1.FRefined);
                    }
                }
            }
            rawRef = raw;
        }
    }
}
