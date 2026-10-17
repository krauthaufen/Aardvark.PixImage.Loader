using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using T = Aardvark.ImageCodecs.Av1.Av1Tables;

namespace Aardvark.ImageCodecs.Av1
{
    /// <summary>Inverse transforms of AV1 spec 7.13 (1D DCT / ADST / identity / WHT and the 2D process).</summary>
    internal sealed partial class Av1Transform
    {
        readonly long[] t = new long[64];
        readonly Vector128<int>[] tv = new Vector128<int>[64];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static long Round12(long x) => (x + 2048) >> 12;

        /// <summary>The inverse DCT process (7.13.2.3) on t[0 .. 2^n - 1] (generated kernels).</summary>
        void Dct(int n, int r)
        {
            long lo = -(1L << (r - 1)), hi = (1L << (r - 1)) - 1;
            var s = t.AsSpan();
            switch (n)
            {
                case 2: Dct4(s, lo, hi); break;
                case 3: Dct8(s, lo, hi); break;
                case 4: Dct16(s, lo, hi); break;
                case 5: Dct32(s, lo, hi); break;
                default: Dct64(s, lo, hi); break;
            }
        }

        const int Sinpi19 = 1321, Sinpi29 = 2482, Sinpi39 = 3344, Sinpi49 = 3803;

        void Adst4()
        {
            long s0 = Sinpi19 * t[0];
            long s1 = Sinpi29 * t[0];
            long s2 = Sinpi39 * t[1];
            long s3 = Sinpi49 * t[2];
            long s4 = Sinpi19 * t[2];
            long s5 = Sinpi29 * t[3];
            long s6 = Sinpi49 * t[3];
            long a7 = t[0] - t[2];
            long b7 = a7 + t[3];
            s0 = s0 + s3;
            s1 = s1 - s4;
            s3 = s2;
            s2 = Sinpi39 * b7;
            s0 = s0 + s5;
            s1 = s1 - s6;
            long x0 = s0 + s3;
            long x1 = s1 + s3;
            long x2 = s2;
            long x3 = s0 + s1;
            x3 = x3 - s3;
            t[0] = Round12(x0);
            t[1] = Round12(x1);
            t[2] = Round12(x2);
            t[3] = Round12(x3);
        }

        void Adst(int n, int r)
        {
            if (n == 2) { Adst4(); return; }
            long lo = -(1L << (r - 1)), hi = (1L << (r - 1)) - 1;
            if (n == 3) Adst8K(t, lo, hi);
            else Adst16K(t, lo, hi);
        }

        void Wht(int shift)
        {
            long a = t[0] >> shift, c = t[1] >> shift, d = t[2] >> shift, b = t[3] >> shift;
            a += c;
            d -= b;
            long e = (a - d) >> 1;
            b = e - b;
            c = e - c;
            a -= b;
            d += c;
            t[0] = a; t[1] = b; t[2] = c; t[3] = d;
        }

        void Identity(int n)
        {
            int n0 = 1 << n;
            switch (n)
            {
                case 2: for (int i = 0; i < 4; i++) t[i] = Round12(t[i] * 5793); break;
                case 3: for (int i = 0; i < 8; i++) t[i] = t[i] * 2; break;
                case 4: for (int i = 0; i < 16; i++) t[i] = Round12(t[i] * 11586); break;
                default: for (int i = 0; i < n0; i++) t[i] = t[i] * 4; break;
            }
        }

        static bool RowIsDct(int tt) => tt is Av1C.DctDct or Av1C.AdstDct or Av1C.FlipadstDct or Av1C.HDct;
        static bool RowIsAdst(int tt) => tt is Av1C.DctAdst or Av1C.AdstAdst or Av1C.DctFlipadst or Av1C.FlipadstFlipadst
            or Av1C.AdstFlipadst or Av1C.FlipadstAdst or Av1C.HAdst or Av1C.HFlipadst;
        static bool ColIsDct(int tt) => tt is Av1C.DctDct or Av1C.DctAdst or Av1C.DctFlipadst or Av1C.VDct;
        static bool ColIsAdst(int tt) => tt is Av1C.AdstDct or Av1C.AdstAdst or Av1C.FlipadstDct or Av1C.FlipadstFlipadst
            or Av1C.AdstFlipadst or Av1C.FlipadstAdst or Av1C.VAdst or Av1C.VFlipadst;

        static long Round2(long x, int n) => n == 0 ? x : (x + (1L << (n - 1))) >> n;

        void Kernel1D(Span<Vector128<int>> v, int n, bool dct, bool adst, int r)
        {
            var lo = Vector128.Create(-(1 << (r - 1)));
            var hi = Vector128.Create((1 << (r - 1)) - 1);
            if (dct)
            {
                switch (n)
                {
                    case 2: Dct4V(v, lo, hi); break;
                    case 3: Dct8V(v, lo, hi); break;
                    case 4: Dct16V(v, lo, hi); break;
                    case 5: Dct32V(v, lo, hi); break;
                    default: Dct64V(v, lo, hi); break;
                }
            }
            else if (adst)
            {
                if (n == 2) Adst4V(v);
                else if (n == 3) Adst8V(v, lo, hi);
                else Adst16V(v, lo, hi);
            }
            else IdentityV(v, n);
        }

        static void Adst4V(Span<Vector128<int>> t)
        {
            var t0 = t[0]; var t1 = t[1]; var t2 = t[2]; var t3 = t[3];
            var s0 = t0 * Sinpi19 + t2 * Sinpi49 + t3 * Sinpi29;
            var s1 = t0 * Sinpi29 - t2 * Sinpi19 - t3 * Sinpi49;
            var s3 = t1 * Sinpi39;
            var s2 = (t0 - t2 + t3) * Sinpi39;
            t[0] = (s0 + s3 + R12) >> 12;
            t[1] = (s1 + s3 + R12) >> 12;
            t[2] = (s2 + R12) >> 12;
            t[3] = (s0 + s1 - s3 + R12) >> 12;
        }

        static void IdentityV(Span<Vector128<int>> t, int n)
        {
            int n0 = 1 << n;
            switch (n)
            {
                case 2: for (int i = 0; i < 4; i++) t[i] = (t[i] * 5793 + R12) >> 12; break;
                case 3: for (int i = 0; i < 8; i++) t[i] = t[i] + t[i]; break;
                case 4: for (int i = 0; i < 16; i++) t[i] = (t[i] * 11586 + R12) >> 12; break;
                default: for (int i = 0; i < n0; i++) t[i] = t[i] << 2; break;
            }
        }

        /// <summary><see cref="Inverse2D"/> four rows and four columns at a time (bit depth 8 or 10, not lossless).</summary>
        void InverseVec(int[] dequant, int txSz, int txType, int bitDepth, int[] residual, int nzRows)
        {
            int log2W = T.TxWidthLog2[txSz], log2H = T.TxHeightLog2[txSz];
            int w = 1 << log2W, hh = 1 << log2H;
            int tw = Math.Min(32, w);
            int rowShift = T.TransformRowShift[txSz];
            int rowClamp = bitDepth + 8;
            int colClamp = Math.Max(bitDepth + 6, 16);
            bool rect = Math.Abs(log2W - log2H) == 1;
            int colLo = -(1 << (colClamp - 1)), colHi = (1 << (colClamp - 1)) - 1;
            var v = tv.AsSpan();
            bool rowDct = RowIsDct(txType), rowAdst = RowIsAdst(txType);
            int rowGroups = (nzRows + 3) >> 2;
            if (bitDepth == 8)
            {
                var vColLo = Vector128.Create(colLo);
                var vColHi = Vector128.Create(colHi);
                var rnd = Vector128.Create(rowShift == 0 ? 0 : 1 << (rowShift - 1));
                for (int g = 0; g < rowGroups; g++)
                {
                    int i0 = g * 4;
                    int o0 = i0 * tw, o1 = o0 + tw, o2 = o1 + tw, o3 = o2 + tw;
                    for (int j = 0; j < tw; j++)
                        v[j] = Vector128.Create(dequant[o0 + j], dequant[o1 + j], dequant[o2 + j], dequant[o3 + j]);
                    for (int j = tw; j < w; j++) v[j] = Vector128<int>.Zero;
                    if (rect) for (int j = 0; j < w; j++) v[j] = (v[j] * 2896 + R12) >> 12;
                    Kernel1D(v, log2W, rowDct, rowAdst, rowClamp);
                    for (int j = 0; j < w; j++)
                    {
                        var r = Vector128.Min(Vector128.Max((v[j] + rnd) >> rowShift, vColLo), vColHi);
                        residual[i0 * w + j] = r.GetElement(0);
                        residual[(i0 + 1) * w + j] = r.GetElement(1);
                        residual[(i0 + 2) * w + j] = r.GetElement(2);
                        residual[(i0 + 3) * w + j] = r.GetElement(3);
                    }
                }
            }
            else
            {
                // 10 bit rows need 64 bit products
                for (int i = 0; i < rowGroups * 4; i++)
                {
                    for (int j = 0; j < w; j++) t[j] = j < tw ? dequant[i * tw + j] : 0;
                    if (rect) for (int j = 0; j < w; j++) t[j] = Round12(t[j] * 2896);
                    if (rowDct) Dct(log2W, rowClamp);
                    else if (rowAdst) Adst(log2W, rowClamp);
                    else Identity(log2W);
                    for (int j = 0; j < w; j++) residual[i * w + j] = (int)Math.Clamp(Round2(t[j], rowShift), colLo, colHi);
                }
            }
            if (rowGroups * 4 < hh) Array.Clear(residual, rowGroups * 4 * w, (hh - rowGroups * 4) * w);
            bool colDct = ColIsDct(txType), colAdst = ColIsAdst(txType);
            var r8 = Vector128.Create(8);
            for (int j = 0; j < w; j += 4)
            {
                for (int i = 0; i < hh; i++) v[i] = Vector128.Create<int>(residual.AsSpan(i * w + j, 4));
                Kernel1D(v, log2H, colDct, colAdst, colClamp);
                for (int i = 0; i < hh; i++)
                {
                    var r = (v[i] + r8) >> 4;
                    r.CopyTo(residual, i * w + j);
                }
            }
        }

        /// <summary>
        /// The 2D inverse DCT_DCT of a block whose only nonzero coefficient is the DC one: every 1D DCT of a DC-only input
        /// outputs Round2( dc * cos128( 32 ), 12 ) everywhere (the other butterfly inputs are zero), so the residual is
        /// one value.
        /// </summary>
        public static int DcOnly(int dc, int txSz, int bitDepth)
        {
            int log2W = T.TxWidthLog2[txSz], log2H = T.TxHeightLog2[txSz];
            int rowShift = T.TransformRowShift[txSz];
            int rowClamp = bitDepth + 8, colClamp = Math.Max(bitDepth + 6, 16);
            long rowLo = -(1L << (rowClamp - 1)), rowHi = (1L << (rowClamp - 1)) - 1;
            long colLo = -(1L << (colClamp - 1)), colHi = (1L << (colClamp - 1)) - 1;
            long a = dc;
            if (Math.Abs(log2W - log2H) == 1) a = Round12(a * 2896);
            long v = Math.Clamp(Round12(a * 2896), rowLo, rowHi);
            long r = Math.Clamp(Round2(v, rowShift), colLo, colHi);
            long u = Math.Clamp(Round12(r * 2896), colLo, colHi);
            return (int)Round2(u, 4);
        }

        /// <summary>
        /// 2D inverse transform (7.13.3) of <paramref name="dequant"/> (row-major, stride Min(32, w), Min(32, h) rows)
        /// into <paramref name="residual"/> (row-major, stride w); rows of dequant from <paramref name="nzRows"/> on are zero.
        /// </summary>
        public void Inverse2D(int[] dequant, int txSz, int txType, bool lossless, int bitDepth, int[] residual, int nzRows)
        {
            // four rows / columns at once in 32 bit lanes where the products fit: the inputs of a 1D transform are within
            // 16 bits for 8 bit rows and 8 / 10 bit columns (BitDepth + 8 and Max(BitDepth + 6, 16) bit clamps), a
            // butterfly multiplies by at most 4096 and adds two products (and every rotation is followed by a clamp)
            if (!lossless && bitDepth <= 10 && Vector128.IsHardwareAccelerated)
            {
                InverseVec(dequant, txSz, txType, bitDepth, residual, nzRows);
                return;
            }
            int log2W = T.TxWidthLog2[txSz], log2H = T.TxHeightLog2[txSz];
            int w = 1 << log2W, hh = 1 << log2H;
            int tw = Math.Min(32, w), th = Math.Min(32, hh);
            int rowShift = lossless ? 0 : T.TransformRowShift[txSz];
            int colShift = lossless ? 0 : 4;
            int rowClamp = bitDepth + 8;
            int colClamp = Math.Max(bitDepth + 6, 16);
            bool rect = Math.Abs(log2W - log2H) == 1;
            long colLo = -(1L << (colClamp - 1)), colHi = (1L << (colClamp - 1)) - 1;
            // rows from nzRows on have no nonzero coefficient: they transform to zero
            for (int i = 0; i < hh; i++)
            {
                if (i >= nzRows)
                {
                    Array.Clear(residual, i * w, w);
                    continue;
                }
                for (int j = 0; j < w; j++) t[j] = j < tw ? dequant[i * tw + j] : 0;
                if (rect) for (int j = 0; j < w; j++) t[j] = Round12(t[j] * 2896);
                if (lossless) Wht(2);
                else if (RowIsDct(txType)) Dct(log2W, rowClamp);
                else if (RowIsAdst(txType)) Adst(log2W, rowClamp);
                else Identity(log2W);
                for (int j = 0; j < w; j++) residual[i * w + j] = (int)Math.Clamp(Round2(t[j], rowShift), colLo, colHi);
            }
            for (int j = 0; j < w; j++)
            {
                for (int i = 0; i < hh; i++) t[i] = residual[i * w + j];
                if (lossless) Wht(0);
                else if (ColIsDct(txType)) Dct(log2H, colClamp);
                else if (ColIsAdst(txType)) Adst(log2H, colClamp);
                else Identity(log2H);
                for (int i = 0; i < hh; i++) residual[i * w + j] = (int)Round2(t[i], colShift);
            }
        }
    }
}
