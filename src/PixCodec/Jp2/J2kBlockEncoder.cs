using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace PixCodec
{
    /// <summary>A code-block on the encoder side: its codeword and what every coding pass costs and gains.</summary>
    internal sealed class J2kEncBlock
    {
        public byte[] Data = Array.Empty<byte>();
        public int NumBps;                   // magnitude bit planes signalled as coded (0: the block is all zero)
        public int MagBits;                  // bits of the largest magnitude
        public int Passes;
        /// <summary>Bytes of Data needed to decode passes 0..k.</summary>
        public int[] PassLen = Array.Empty<int>();
        /// <summary>Squared error (in quantizer steps) removed by passes 0..k.</summary>
        public double[] PassDist = Array.Empty<double>();
        /// <summary>The codeword is terminated after pass k (a segment ends there).</summary>
        public bool[] PassTerm = Array.Empty<bool>();
        public bool Ht;
        // rate control
        public float Weight;                 // squared error in the image per unit of PassDist
        public int[] Hull = Array.Empty<int>();     // truncation points on the convex hull (pass counts), ascending
        public float[] Slope = Array.Empty<float>(); // distortion-rate slope of each hull point
        public int[] LayerPasses = Array.Empty<int>(); // passes included up to and including layer l
        // tier-2 state
        public int Included;                 // passes written so far
        public int Lblock = 3;
    }

    internal sealed unsafe partial class J2kBlockCoder
    {
        /// <summary>Raw (bypass) bit writer: MSB first, a 0 bit stuffed after a 0xFF byte.</summary>
        struct RawWriter
        {
            public byte* P;
            uint _cur;
            int _free;

            public void Init(byte* p) { P = p; _cur = 0; _free = 8; }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Bit(int b)
            {
                _free--;
                _cur |= (uint)b << _free;
                if (_free == 0)
                {
                    *P++ = (byte)_cur;
                    _free = _cur == 0xFF ? 7 : 8;
                    _cur = 0;
                }
            }

            /// <summary>Bytes needed for the bits written so far if the stream ended here.</summary>
            public int LengthNow(byte* start) => (int)(P - start) + (_free < 8 && !(_free == 7 && P > start && P[-1] == 0xFF) ? 1 : 0);

            /// <summary>Pads to a byte boundary (alternating 0 1 for predictable termination) and returns the end.</summary>
            public byte* Flush(byte* start, bool predictable)
            {
                bool afterFf = P > start && P[-1] == 0xFF;
                int full = afterFf ? 7 : 8;
                if (_free < full || afterFf)
                {
                    if (predictable)
                    {
                        int bit = 0;
                        while (_free > 0) { _free--; _cur |= (uint)bit << _free; bit ^= 1; }
                    }
                    *P++ = (byte)_cur;
                }
                // a segment may not end with 0xFF
                if (P > start && P[-1] == 0xFF) *P++ = predictable ? (byte)0x2A : (byte)0;
                return P;
            }
        }

        /// <summary>
        /// Encodes a block of sign-magnitude samples (bit 31 = sign) with the Part 1 block coder.
        /// With <paramref name="rateControl"/> the per pass lengths and distortions are recorded.
        /// </summary>
        public void Encode<T>(T* data, int w, int h, int orient, int style, J2kEncBlock dst, bool rateControl)
            where T : unmanaged, IBinaryInteger<T>
        {
            int typeBits = sizeof(T) * 8;
            T magMask = (T.One << (typeBits - 1)) - T.One;
            T max = T.Zero;
            int n = w * h;
            for (int i = 0; i < n; i++) max |= data[i] & magMask;
            int numBps = typeBits - int.CreateTruncating(T.LeadingZeroCount(max));
            dst.NumBps = dst.MagBits = numBps; dst.Ht = false;
            if (numBps == 0) { dst.Passes = 0; dst.Data = Array.Empty<byte>(); return; }
            int passes = 3 * numBps - 2;
            dst.Passes = passes;
            dst.PassLen = new int[passes]; dst.PassDist = new double[passes]; dst.PassTerm = new bool[passes];

            int stride = (w + 2) * 4;
            int stripes = (h + 3) >> 2;
            int flagLen = (stripes + 2) * stride;
            if (Flags.Length < flagLen) Flags = new ushort[flagLen];
            Array.Clear(Flags, 0, flagLen);
            J2kT1.ResetContexts(_cx);
            bool causal = (style & J2kCod.Causal) != 0, bypass = (style & J2kCod.Bypass) != 0;
            bool reset = (style & J2kCod.Reset) != 0, segSym = (style & J2kCod.SegSym) != 0, predTerm = (style & J2kCod.PredTerm) != 0;
            // worst case: every sample costs about 2.5 bytes per bit plane is far above what the MQ coder can produce
            int cap = 64 + n * numBps * 3 / 2 + passes * 8;
            EnsureBuf(cap + 8);

            fixed (ushort* flags = Flags)
            fixed (byte* buf = Buf, cx = _cx, zc = &J2kT1.Zc[orient * 256], sc = J2kT1.Sc)
            {
                byte* start = buf + 1;
                buf[0] = 0;
                var mq = default(J2kMqEncoder);
                var raw = default(RawWriter);
                byte* segStart = start;      // start of the current codeword segment
                bool isRaw = false;
                mq.Init(start);
                double dist = 0;
                int bp = numBps - 1;
                int segEnd = SegmentPasses(style, 0);
                for (int pass = 0; pass < passes; pass++)
                {
                    int type = pass == 0 ? 2 : (pass - 1) % 3;
                    if (pass > 0 && type == 0) bp--;
                    if (type == 2) dist += EncCleanup(ref mq, data, flags, w, h, stride, bp, causal, segSym, cx, zc, sc, rateControl);
                    else if (type == 0)
                    {
                        if (isRaw) dist += EncSigPropRaw(ref raw, data, flags, w, h, stride, bp, causal, rateControl);
                        else dist += EncSigProp(ref mq, data, flags, w, h, stride, bp, causal, cx, zc, sc, rateControl);
                    }
                    else
                    {
                        if (isRaw) dist += EncRefineRaw(ref raw, data, flags, w, h, stride, bp, rateControl);
                        else dist += EncRefine(ref mq, data, flags, w, h, stride, bp, cx, rateControl);
                    }
                    dst.PassDist[pass] = dist;
                    bool last = pass == passes - 1;
                    if (pass + 1 == segEnd || last)
                    {
                        // the segment ends here: terminate
                        byte* end;
                        if (isRaw) end = raw.Flush(segStart, predTerm);
                        else end = segStart + (predTerm ? mq.FlushPredictable() : mq.Flush());
                        dst.PassLen[pass] = (int)(end - start);
                        dst.PassTerm[pass] = true;
                        if (!last)
                        {
                            segStart = end;
                            segEnd = pass + 1 + SegmentPasses(style, pass + 1);
                            int nextType = pass % 3;   // type of pass + 1
                            isRaw = bypass && pass + 1 >= 10 && nextType != 2;
                            if (isRaw) raw.Init(segStart);
                            else mq.Restart(segStart);
                        }
                    }
                    else if (isRaw) dst.PassLen[pass] = (int)(segStart - start) + raw.LengthNow(segStart);
                    else dst.PassLen[pass] = (int)(segStart - start) + mq.Position + 4;   // bytes that pin down everything coded so far
                    if (reset) J2kT1.ResetContexts(_cx);
                }
                int total = dst.PassLen[passes - 1];
                // estimates may not exceed the end of their segment, and lengths never decrease
                int segLimit = total;
                for (int k = passes - 1; k >= 0; k--)
                {
                    if (dst.PassTerm[k]) segLimit = dst.PassLen[k];
                    else if (dst.PassLen[k] > segLimit) dst.PassLen[k] = segLimit;
                }
                // a contribution that is cut inside a segment may not end with 0xFF (the next byte could complete a marker)
                for (int k = 0; k < passes; k++)
                    if (!dst.PassTerm[k]) while (dst.PassLen[k] > 0 && start[dst.PassLen[k] - 1] == 0xFF) dst.PassLen[k]--;
                for (int k = 1; k < passes; k++) if (dst.PassLen[k] < dst.PassLen[k - 1]) dst.PassLen[k] = dst.PassLen[k - 1];
                dst.Data = new ReadOnlySpan<byte>(start, total).ToArray();
            }
        }

        // Distortion bookkeeping: m is the magnitude, p the bit plane just coded. Returns the squared error removed.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static double GainSignificant(ulong m, int p)
        {
            double rec = p == 0 ? m : ((m >> p) << p) + (1UL << (p - 1));
            double e = m - rec;
            return (double)m * m - e * e;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static double GainRefine(ulong m, int p)
        {
            double before = ((m >> (p + 1)) << (p + 1)) + (1UL << p);
            double after = p == 0 ? m : ((m >> p) << p) + (1UL << (p - 1));
            double e0 = m - before, e1 = m - after;
            return e0 * e0 - e1 * e1;
        }

        static double EncCleanup<T>(ref J2kMqEncoder mqRef, T* data, ushort* flags, int w, int h, int stride, int bp,
            bool causal, bool segSym, byte* cx, byte* zc, byte* sc, bool rc)
            where T : unmanaged, IBinaryInteger<T>
        {
            int signShift = sizeof(T) * 8 - 1;
            T magMask = (T.One << signShift) - T.One;
            var mq = mqRef;
            double dist = 0;
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
                        int first = -1;
                        for (int i = 0; i < 4; i++) if (((dp[i * w] >> bp) & T.One) != T.Zero) { first = i; break; }
                        if (first < 0) { mq.Encode(ref cx[J2kT1.CtxRun], 0); continue; }
                        mq.Encode(ref cx[J2kT1.CtxRun], 1);
                        mq.Encode(ref cx[J2kT1.CtxUni], first >> 1);
                        mq.Encode(ref cx[J2kT1.CtxUni], first & 1);
                        k = first;
                        T v = dp[k * w];
                        int neg = int.CreateTruncating(v >> signShift) & 1;
                        int s = sc[ScIndex(fp[k])];
                        mq.Encode(ref cx[s & 31], neg ^ (s >> 7));
                        SetSignificant(fp + k, k, stride, neg, causal);
                        if (rc) dist += GainSignificant(ulong.CreateTruncating(v & magMask), bp);
                        k++;
                    }
                    for (; k < rows; k++)
                    {
                        int f = fp[k];
                        if ((f & (J2kT1.FSig | J2kT1.FVisit)) != 0) continue;
                        T v = dp[k * w];
                        int bit = int.CreateTruncating(v >> bp) & 1;
                        mq.Encode(ref cx[zc[f & 0xFF]], bit);
                        if (bit == 0) continue;
                        int neg = int.CreateTruncating(v >> signShift) & 1;
                        int s = sc[ScIndex(f)];
                        mq.Encode(ref cx[s & 31], neg ^ (s >> 7));
                        SetSignificant(fp + k, k, stride, neg, causal);
                        if (rc) dist += GainSignificant(ulong.CreateTruncating(v & magMask), bp);
                    }
                    Unsafe.WriteUnaligned(fp, Unsafe.ReadUnaligned<ulong>(fp) & ~Visit4);
                }
            }
            if (segSym)
            {
                mq.Encode(ref cx[J2kT1.CtxUni], 1); mq.Encode(ref cx[J2kT1.CtxUni], 0);
                mq.Encode(ref cx[J2kT1.CtxUni], 1); mq.Encode(ref cx[J2kT1.CtxUni], 0);
            }
            mqRef = mq;
            return dist;
        }

        static double EncSigProp<T>(ref J2kMqEncoder mqRef, T* data, ushort* flags, int w, int h, int stride, int bp,
            bool causal, byte* cx, byte* zc, byte* sc, bool rc)
            where T : unmanaged, IBinaryInteger<T>
        {
            int signShift = sizeof(T) * 8 - 1;
            T magMask = (T.One << signShift) - T.One;
            var mq = mqRef;
            double dist = 0;
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
                        T v = dp[k * w];
                        int bit = int.CreateTruncating(v >> bp) & 1;
                        mq.Encode(ref cx[zc[f & 0xFF]], bit);
                        if (bit != 0)
                        {
                            int neg = int.CreateTruncating(v >> signShift) & 1;
                            int s = sc[ScIndex(f)];
                            mq.Encode(ref cx[s & 31], neg ^ (s >> 7));
                            SetSignificant(fp + k, k, stride, neg, causal);
                            if (rc) dist += GainSignificant(ulong.CreateTruncating(v & magMask), bp);
                        }
                        fp[k] |= J2kT1.FVisit;
                    }
                }
            }
            mqRef = mq;
            return dist;
        }

        static double EncSigPropRaw<T>(ref RawWriter rawRef, T* data, ushort* flags, int w, int h, int stride, int bp, bool causal, bool rc)
            where T : unmanaged, IBinaryInteger<T>
        {
            int signShift = sizeof(T) * 8 - 1;
            T magMask = (T.One << signShift) - T.One;
            var raw = rawRef;
            double dist = 0;
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
                        T v = dp[k * w];
                        int bit = int.CreateTruncating(v >> bp) & 1;
                        raw.Bit(bit);
                        if (bit != 0)
                        {
                            int neg = int.CreateTruncating(v >> signShift) & 1;
                            raw.Bit(neg);
                            SetSignificant(fp + k, k, stride, neg, causal);
                            if (rc) dist += GainSignificant(ulong.CreateTruncating(v & magMask), bp);
                        }
                        fp[k] |= J2kT1.FVisit;
                    }
                }
            }
            rawRef = raw;
            return dist;
        }

        static double EncRefine<T>(ref J2kMqEncoder mqRef, T* data, ushort* flags, int w, int h, int stride, int bp, byte* cx, bool rc)
            where T : unmanaged, IBinaryInteger<T>
        {
            int signShift = sizeof(T) * 8 - 1;
            T magMask = (T.One << signShift) - T.One;
            var mq = mqRef;
            double dist = 0;
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
                        T v = dp[k * w];
                        int c = (f & J2kT1.FRefined) != 0 ? 16 : (f & 0xFF) != 0 ? 15 : 14;
                        mq.Encode(ref cx[c], int.CreateTruncating(v >> bp) & 1);
                        fp[k] = (ushort)(f | J2kT1.FRefined);
                        if (rc) dist += GainRefine(ulong.CreateTruncating(v & magMask), bp);
                    }
                }
            }
            mqRef = mq;
            return dist;
        }

        static double EncRefineRaw<T>(ref RawWriter rawRef, T* data, ushort* flags, int w, int h, int stride, int bp, bool rc)
            where T : unmanaged, IBinaryInteger<T>
        {
            int signShift = sizeof(T) * 8 - 1;
            T magMask = (T.One << signShift) - T.One;
            var raw = rawRef;
            double dist = 0;
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
                        T v = dp[k * w];
                        raw.Bit(int.CreateTruncating(v >> bp) & 1);
                        fp[k] = (ushort)(f | J2kT1.FRefined);
                        if (rc) dist += GainRefine(ulong.CreateTruncating(v & magMask), bp);
                    }
                }
            }
            rawRef = raw;
            return dist;
        }
    }
}
