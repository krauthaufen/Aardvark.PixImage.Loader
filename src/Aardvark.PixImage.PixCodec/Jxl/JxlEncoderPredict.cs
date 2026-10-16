using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Aardvark.ImageCodecs.JpegXl
{
    /// <summary>Cost estimates: c log2 c from a table.</summary>
    internal static class JxlEncMath
    {
        const int TableSize = 1 << 16;
        static readonly double[] xlogx = Build();

        static double[] Build()
        {
            var t = new double[TableSize];
            for (int i = 1; i < TableSize; i++) t[i] = i * Math.Log2(i);
            return t;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double XLogX(long c) => (ulong)c < TableSize ? xlogx[c] : c * Math.Log2(c);

        /// <summary>Bits of coding the counts with their own (ideal) distribution.</summary>
        public static double Entropy(ReadOnlySpan<long> h)
        {
            long total = 0;
            double s = 0;
            foreach (var c in h) { total += c; s += XLogX(c); }
            return XLogX(total) - s;
        }
    }

    /// <summary>Encoder side predictors of modular mode (H.5 of ISO/IEC 18181-1).</summary>
    internal static class JxlEncPredict
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long Predict(int predictor, long W, long N, long NW, long NE, long NN, long WW, long NEE, long wp)
        {
            switch (predictor)
            {
                case 0: return 0;
                case 1: return W;
                case 2: return N;
                case 3: return (W + N) / 2;
                case 4:
                    {
                        long p = W + N - NW;
                        return Math.Abs(p - W) < Math.Abs(p - N) ? W : N;
                    }
                case 5: return ClampedGradient(W, N, NW);
                case 6: return wp;
                case 7: return NE;
                case 8: return NW;
                case 9: return WW;
                case 10: return (W + NW) / 2;
                case 11: return (N + NW) / 2;
                case 12: return (N + NE) / 2;
                default: return (6 * N - 2 * NN + 7 * W + WW + NEE + 3 * NE + 8) / 16;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long ClampedGradient(long W, long N, long NW)
        {
            long mn = Math.Min(W, N), mx = Math.Max(W, N);
            long g = W + N - NW;
            return g < mn ? mn : g > mx ? mx : g;
        }

        /// <summary>The neighbours of (x, y) with the edge rules of the standard.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Neighbours(int[] d, int w, int x, int y, out long W, out long N, out long NW, out long NE, out long NN, out long WW, out long NEE)
        {
            int i = y * w + x;
            if (y >= 2 && x >= 2 && x + 2 < w)
            {
                ref int r = ref MemoryMarshal.GetArrayDataReference(d);
                W = Unsafe.Add(ref r, i - 1); WW = Unsafe.Add(ref r, i - 2);
                int n = i - w;
                N = Unsafe.Add(ref r, n); NW = Unsafe.Add(ref r, n - 1); NE = Unsafe.Add(ref r, n + 1); NEE = Unsafe.Add(ref r, n + 2);
                NN = Unsafe.Add(ref r, n - w);
                return;
            }
            W = x > 0 ? d[i - 1] : y > 0 ? d[i - w] : 0;
            N = y > 0 ? d[i - w] : W;
            NW = x > 0 && y > 0 ? d[i - w - 1] : W;
            NE = x + 1 < w && y > 0 ? d[i - w + 1] : N;
            NN = y > 1 ? d[i - 2 * w] : N;
            WW = x > 1 ? d[i - 2] : W;
            NEE = x + 2 < w && y > 0 ? d[i - w + 2] : NE;
        }
    }

    /// <summary>
    /// The self-correcting (weighted) predictor, encoder copy: same arithmetic as the decoder's (the bitstream depends
    /// on it), kept separate so that both sides can be tuned independently.
    /// </summary>
    internal sealed class JxlEncWp
    {
        struct Cell
        {
            public uint E0, E1, E2, E3;
            public int True;
        }

        readonly int w, stride;
        readonly Cell[] cells;
        int cur, prev;
        long pred, sub0, sub1, sub2, sub3;
        public int MaxError;
        readonly int p1c, p2c, p3a, p3b, p3c, p3d, p3e;
        readonly uint mw0, mw1, mw2, mw3;

        static readonly uint[] DivLookup = BuildDiv();

        static uint[] BuildDiv()
        {
            var d = new uint[64];
            for (int i = 0; i < 64; i++) d[i] = (uint)((1 << 24) / (i + 1));
            return d;
        }

        public JxlEncWp(JxlWpHeader h, int width)
        {
            w = width; stride = width + 2;
            cells = new Cell[2 * stride];
            p1c = h.P1C; p2c = h.P2C; p3a = h.P3Ca; p3b = h.P3Cb; p3c = h.P3Cc; p3d = h.P3Cd; p3e = h.P3Ce;
            mw0 = h.W0; mw1 = h.W1; mw2 = h.W2; mw3 = h.W3;
        }

        public void StartRow(int y)
        {
            cur = (y & 1) != 0 ? 0 : stride;
            prev = (y & 1) != 0 ? stride : 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static uint ErrorWeight(uint x, uint maxWeight)
        {
            int shift = 63 - BitOperations.LeadingZeroCount((ulong)x + 1) - 5;
            if (shift < 0) shift = 0;
            return 4 + ((maxWeight * DivLookup[x >> shift]) >> shift);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static long Abs(long v) => (v ^ (v >> 63)) - (v >> 63);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long Predict(int x, long N, long W, long NE, long NW, long NN)
        {
            ref Cell c0 = ref MemoryMarshal.GetArrayDataReference(cells);
            int pN = prev + x;
            ref Cell cn = ref Unsafe.Add(ref c0, pN);
            ref Cell cne = ref Unsafe.Add(ref c0, x < w - 1 ? pN + 1 : pN);
            ref Cell cnw = ref Unsafe.Add(ref c0, x > 0 ? pN - 1 : pN);
            uint w0 = ErrorWeight(cn.E0 + cne.E0 + cnw.E0, mw0);
            uint w1 = ErrorWeight(cn.E1 + cne.E1 + cnw.E1, mw1);
            uint w2 = ErrorWeight(cn.E2 + cne.E2 + cnw.E2, mw2);
            uint w3 = ErrorWeight(cn.E3 + cne.E3 + cnw.E3, mw3);
            long N3 = N << 3, W3 = W << 3, NE3 = NE << 3, NW3 = NW << 3, NN3 = NN << 3;
            long teW = x == 0 ? 0 : Unsafe.Add(ref c0, cur + x - 1).True;
            long teN = cn.True, teNW = cnw.True, teNE = cne.True;
            long sumWN = teN + teW;
            long p0 = W3 + NE3 - N3;
            long p1 = N3 - ((sumWN + teNE) * p1c >> 5);
            long p2 = W3 - ((sumWN + teNW) * p2c >> 5);
            long p3 = N3 - ((teNW * p3a + teN * p3b + teNE * p3c + (NN3 - N3) * p3d + (NW3 - W3) * p3e) >> 5);
            uint ws = w0 + w1 + w2 + w3;
            int lw = 31 - BitOperations.LeadingZeroCount(ws) - 4;
            w0 >>= lw; w1 >>= lw; w2 >>= lw; w3 >>= lw;
            uint sum = w0 + w1 + w2 + w3;
            long s = (sum >> 1) - 1 + p0 * w0 + p1 * w1 + p2 * w2 + p3 * w3;
            long p = (s * DivLookup[sum - 1]) >> 24;
            if (((teN ^ teW) | (teN ^ teNW)) <= 0)
            {
                long mx = Math.Max(W3, Math.Max(N3, NE3)), mn = Math.Min(W3, Math.Min(N3, NE3));
                if (p < mn) p = mn; else if (p > mx) p = mx;
            }
            long me = teW;
            if (Abs(teN) > Abs(me)) me = teN;
            if (Abs(teNW) > Abs(me)) me = teNW;
            if (Abs(teNE) > Abs(me)) me = teNE;
            MaxError = (int)me;
            pred = p; sub0 = p0; sub1 = p1; sub2 = p2; sub3 = p3;
            return (p + 3) >> 3;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Update(int x, long value)
        {
            long v3 = value << 3;
            ref Cell c0 = ref MemoryMarshal.GetArrayDataReference(cells);
            ref Cell c = ref Unsafe.Add(ref c0, cur + x);
            ref Cell ne = ref Unsafe.Add(ref c0, prev + x + 1);
            c.True = (int)(pred - v3);
            uint e;
            e = (uint)((Abs(sub0 - v3) + 3) >> 3); c.E0 = e; ne.E0 += e;
            e = (uint)((Abs(sub1 - v3) + 3) >> 3); c.E1 = e; ne.E1 += e;
            e = (uint)((Abs(sub2 - v3) + 3) >> 3); c.E2 = e; ne.E2 += e;
            e = (uint)((Abs(sub3 - v3) + 3) >> 3); c.E3 = e; ne.E3 += e;
        }
    }
}
