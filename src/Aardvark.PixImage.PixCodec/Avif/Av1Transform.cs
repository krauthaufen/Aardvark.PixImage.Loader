using System;
using System.Runtime.CompilerServices;
using T = Aardvark.ImageCodecs.Av1.Av1Tables;

namespace Aardvark.ImageCodecs.Av1
{
    /// <summary>Inverse transforms of AV1 spec 7.13 (1D DCT / ADST / identity / WHT and the 2D process).</summary>
    internal sealed class Av1Transform
    {
        readonly long[] t = new long[64];
        readonly long[] copy = new long[64];

        static int Cos128(int angle)
        {
            int a2 = angle & 255;
            var lut = T.Cos128Lookup;
            if (a2 <= 64) return lut[a2];
            if (a2 <= 128) return -lut[128 - a2];
            if (a2 <= 192) return -lut[a2 - 128];
            return lut[256 - a2];
        }

        static int Sin128(int angle) => Cos128(angle - 64);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static long Round12(long x) => (x + 2048) >> 12;

        void B(int a, int b, int angle, int flip)
        {
            long c = Cos128(angle), s = Sin128(angle);
            long x = t[a] * c - t[b] * s;
            long y = t[a] * s + t[b] * c;
            t[a] = Round12(x);
            t[b] = Round12(y);
            if (flip != 0) (t[a], t[b]) = (t[b], t[a]);
        }

        void H(int a, int b, int flip, int r)
        {
            if (flip != 0) (a, b) = (b, a);
            long x = t[a], y = t[b];
            long lo = -(1L << (r - 1)), hi = (1L << (r - 1)) - 1;
            t[a] = Math.Clamp(x + y, lo, hi);
            t[b] = Math.Clamp(x - y, lo, hi);
        }

        static int Brev(int numBits, int x)
        {
            int r = 0;
            for (int i = 0; i < numBits; i++)
            {
                int bit = (x >> i) & 1;
                r += bit << (numBits - 1 - i);
            }
            return r;
        }

        void Dct(int n, int r)
        {
            int n0 = 1 << n;
            for (int i = 0; i < n0; i++) copy[i] = t[i];
            for (int i = 0; i < n0; i++) t[i] = copy[Brev(n, i)];
            if (n == 6) for (int i = 0; i < 16; i++) B(32 + i, 63 - i, 63 - 4 * Brev(4, i), 0);
            if (n >= 5) for (int i = 0; i < 8; i++) B(16 + i, 31 - i, 6 + (Brev(3, 7 - i) << 3), 0);
            if (n == 6) for (int i = 0; i < 16; i++) H(32 + i * 2, 33 + i * 2, i & 1, r);
            if (n >= 4) for (int i = 0; i < 4; i++) B(8 + i, 15 - i, 12 + (Brev(2, 3 - i) << 4), 0);
            if (n >= 5) for (int i = 0; i < 8; i++) H(16 + 2 * i, 17 + 2 * i, i & 1, r);
            if (n == 6) for (int i = 0; i < 4; i++) for (int j = 0; j < 2; j++) B(62 - i * 4 - j, 33 + i * 4 + j, 60 - 16 * Brev(2, i) + 64 * j, 1);
            if (n >= 3) for (int i = 0; i < 2; i++) B(4 + i, 7 - i, 56 - 32 * i, 0);
            if (n >= 4) for (int i = 0; i < 4; i++) H(8 + 2 * i, 9 + 2 * i, i & 1, r);
            if (n >= 5) for (int i = 0; i < 2; i++) for (int j = 0; j < 2; j++) B(30 - 4 * i - j, 17 + 4 * i + j, 24 + (j << 6) + ((1 - i) << 5), 1);
            if (n == 6) for (int i = 0; i < 8; i++) for (int j = 0; j < 2; j++) H(32 + i * 4 + j, 35 + i * 4 - j, i & 1, r);
            for (int i = 0; i < 2; i++) B(2 * i, 2 * i + 1, 32 + 16 * i, 1 - i);
            if (n >= 3) for (int i = 0; i < 2; i++) H(4 + 2 * i, 5 + 2 * i, i, r);
            if (n >= 4) for (int i = 0; i < 2; i++) B(14 - i, 9 + i, 48 + 64 * i, 1);
            if (n >= 5) for (int i = 0; i < 4; i++) for (int j = 0; j < 2; j++) H(16 + 4 * i + j, 19 + 4 * i - j, i & 1, r);
            if (n == 6) for (int i = 0; i < 2; i++) for (int j = 0; j < 4; j++) B(61 - i * 8 - j, 34 + i * 8 + j, 56 - i * 32 + (j >> 1) * 64, 1);
            for (int i = 0; i < 2; i++) H(i, 3 - i, 0, r);
            if (n >= 3) B(6, 5, 32, 1);
            if (n >= 4) for (int i = 0; i < 2; i++) for (int j = 0; j < 2; j++) H(8 + 4 * i + j, 11 + 4 * i - j, i, r);
            if (n >= 5) for (int i = 0; i < 4; i++) B(29 - i, 18 + i, 48 + (i >> 1) * 64, 1);
            if (n == 6) for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) H(32 + 8 * i + j, 39 + 8 * i - j, i & 1, r);
            if (n >= 3) for (int i = 0; i < 4; i++) H(i, 7 - i, 0, r);
            if (n >= 4) for (int i = 0; i < 2; i++) B(13 - i, 10 + i, 32, 1);
            if (n >= 5) for (int i = 0; i < 2; i++) for (int j = 0; j < 4; j++) H(16 + i * 8 + j, 23 + i * 8 - j, i, r);
            if (n == 6) for (int i = 0; i < 8; i++) B(59 - i, 36 + i, i < 4 ? 48 : 112, 1);
            if (n >= 4) for (int i = 0; i < 8; i++) H(i, 15 - i, 0, r);
            if (n >= 5) for (int i = 0; i < 4; i++) B(27 - i, 20 + i, 32, 1);
            if (n == 6)
                for (int i = 0; i < 8; i++)
                {
                    H(32 + i, 47 - i, 0, r);
                    H(48 + i, 63 - i, 1, r);
                }
            if (n >= 5) for (int i = 0; i < 16; i++) H(i, 31 - i, 0, r);
            if (n == 6) for (int i = 0; i < 8; i++) B(55 - i, 40 + i, 32, 1);
            if (n == 6) for (int i = 0; i < 32; i++) H(i, 63 - i, 0, r);
        }

        void AdstInputPermutation(int n)
        {
            int n0 = 1 << n;
            for (int i = 0; i < n0; i++) copy[i] = t[i];
            for (int i = 0; i < n0; i++)
            {
                int idx = (i & 1) != 0 ? i - 1 : n0 - i - 1;
                t[i] = copy[idx];
            }
        }

        void AdstOutputPermutation(int n)
        {
            int n0 = 1 << n;
            for (int i = 0; i < n0; i++) copy[i] = t[i];
            for (int i = 0; i < n0; i++)
            {
                int a = (i >> 3) & 1;
                int b = ((i >> 2) & 1) ^ ((i >> 3) & 1);
                int c = ((i >> 1) & 1) ^ ((i >> 2) & 1);
                int d = (i & 1) ^ ((i >> 1) & 1);
                int idx = ((d << 3) | (c << 2) | (b << 1) | a) >> (4 - n);
                t[i] = (i & 1) != 0 ? -copy[idx] : copy[idx];
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

        void Adst8(int r)
        {
            AdstInputPermutation(3);
            for (int i = 0; i < 4; i++) B(2 * i, 2 * i + 1, 60 - 16 * i, 1);
            for (int i = 0; i < 4; i++) H(i, 4 + i, 0, r);
            for (int i = 0; i < 2; i++) B(4 + 3 * i, 5 + i, 48 - 32 * i, 1);
            for (int i = 0; i < 2; i++) for (int j = 0; j < 2; j++) H(4 * j + i, 2 + 4 * j + i, 0, r);
            for (int i = 0; i < 2; i++) B(2 + 4 * i, 3 + 4 * i, 32, 1);
            AdstOutputPermutation(3);
        }

        void Adst16(int r)
        {
            AdstInputPermutation(4);
            for (int i = 0; i < 8; i++) B(2 * i, 2 * i + 1, 62 - 8 * i, 1);
            for (int i = 0; i < 8; i++) H(i, 8 + i, 0, r);
            for (int i = 0; i < 2; i++)
            {
                B(8 + 2 * i, 9 + 2 * i, 56 - 32 * i, 1);
                B(13 + 2 * i, 12 + 2 * i, 8 + 32 * i, 1);
            }
            for (int i = 0; i < 4; i++) for (int j = 0; j < 2; j++) H(8 * j + i, 4 + 8 * j + i, 0, r);
            for (int i = 0; i < 2; i++) for (int j = 0; j < 2; j++) B(4 + 8 * j + 3 * i, 5 + 8 * j + i, 48 - 32 * i, 1);
            for (int i = 0; i < 2; i++) for (int j = 0; j < 4; j++) H(4 * j + i, 2 + 4 * j + i, 0, r);
            for (int i = 0; i < 4; i++) B(2 + 4 * i, 3 + 4 * i, 32, 1);
            AdstOutputPermutation(4);
        }

        void Adst(int n, int r)
        {
            if (n == 2) Adst4();
            else if (n == 3) Adst8(r);
            else Adst16(r);
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

        /// <summary>
        /// 2D inverse transform (7.13.3) of <paramref name="dequant"/> (row-major, stride Min(32, w), Min(32, h) rows)
        /// into <paramref name="residual"/> (row-major, stride w).
        /// </summary>
        public void Inverse2D(int[] dequant, int txSz, int txType, bool lossless, int bitDepth, int[] residual)
        {
            int log2W = T.TxWidthLog2[txSz], log2H = T.TxHeightLog2[txSz];
            int w = 1 << log2W, hh = 1 << log2H;
            int tw = Math.Min(32, w), th = Math.Min(32, hh);
            int rowShift = lossless ? 0 : T.TransformRowShift[txSz];
            int colShift = lossless ? 0 : 4;
            int rowClamp = bitDepth + 8;
            int colClamp = Math.Max(bitDepth + 6, 16);
            bool rect = Math.Abs(log2W - log2H) == 1;
            long colLo = -(1L << (colClamp - 1)), colHi = (1L << (colClamp - 1)) - 1;
            for (int i = 0; i < hh; i++)
            {
                if (i >= th)
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
