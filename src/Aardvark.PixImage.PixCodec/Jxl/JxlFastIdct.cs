using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Aardvark.ImageCodecs.JpegXl
{
    /// <summary>
    /// O(N log N) inverse DCT on vectors of independent columns (each Vector lane is one column):
    /// y[n] = sum_k c[k] cos(pi (2n + 1) k / 2N) splits into the half size transform of the even coefficients and,
    /// through 2 cos(a) cos(b) = cos(a + b) + cos(a - b), the half size transform of c[2m + 1] + c[2m - 1] divided by
    /// 2 cos(pi (2n + 1) / 2N).
    /// </summary>
    internal static unsafe class JxlFastIdct
    {
        static readonly float[][] halfSecant = new float[9][];   // [log2 N][n] = 1 / (2 cos(pi (2n + 1) / 2N)), n < N / 2

        static JxlFastIdct()
        {
            for (int l = 1; l <= 8; l++)
            {
                int n = 1 << l;
                var t = new float[n / 2];
                for (int i = 0; i < n / 2; i++) t[i] = (float)(0.5 / Math.Cos(Math.PI * (2 * i + 1) / (2.0 * n)));
                halfSecant[l] = t;
            }
        }

        /// <summary>
        /// In place unscaled IDCT of n vectors (data[k] = coefficient k of every lane). scratch: n vectors.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Idct(Vector<float>* data, int n, Vector<float>* scratch)
        {
            if (n == 8) { Idct8(data); return; }
            if (n == 4) { Idct4(ref data[0], ref data[1], ref data[2], ref data[3]); return; }
            if (n == 1) return;
            if (n == 2)
            {
                var a = data[0]; var b = data[1] * new Vector<float>(0.70710678118654752f);
                data[0] = a + b; data[1] = a - b;
                return;
            }
            int h = n / 2;
            Vector<float>* even = scratch, odd = scratch + h;
            for (int m = 0; m < h; m++) even[m] = data[2 * m];
            odd[0] = data[1];
            for (int m = 1; m < h; m++) odd[m] = data[2 * m + 1] + data[2 * m - 1];
            Idct(even, h, data);
            Idct(odd, h, data);
            var sec = halfSecant[BitOperations.Log2((uint)n)];
            for (int i = 0; i < h; i++)
            {
                var o = odd[i] * new Vector<float>(sec[i]);
                data[i] = even[i] + o;
                data[n - 1 - i] = even[i] - o;
            }
        }

        const float InvSqrt2 = 0.70710678118654752f;

        /// <summary>The 4 point case of <see cref="Idct"/>, unrolled (the same operations in the same order).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Idct4(ref Vector<float> d0, ref Vector<float> d1, ref Vector<float> d2, ref Vector<float> d3)
        {
            var s = new Vector<float>(InvSqrt2);
            var b0 = d2 * s; var e0 = d0 + b0; var e1 = d0 - b0;
            var b1 = (d3 + d1) * s; var o0 = d1 + b1; var o1 = d1 - b1;
            var x0 = o0 * new Vector<float>(Sec4_0); var x1 = o1 * new Vector<float>(Sec4_1);
            d0 = e0 + x0; d3 = e0 - x0; d1 = e1 + x1; d2 = e1 - x1;
        }

        static readonly float Sec4_0 = (float)(0.5 / Math.Cos(Math.PI * 1 / 8.0)), Sec4_1 = (float)(0.5 / Math.Cos(Math.PI * 3 / 8.0));
        static readonly float Sec8_0 = (float)(0.5 / Math.Cos(Math.PI * 1 / 16.0)), Sec8_1 = (float)(0.5 / Math.Cos(Math.PI * 3 / 16.0)),
            Sec8_2 = (float)(0.5 / Math.Cos(Math.PI * 5 / 16.0)), Sec8_3 = (float)(0.5 / Math.Cos(Math.PI * 7 / 16.0));

        /// <summary>The 8 point case of <see cref="Idct"/>, unrolled (the same operations in the same order).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Idct8(Vector<float>* d)
        {
            var e0 = d[0]; var e1 = d[2]; var e2 = d[4]; var e3 = d[6];
            var o0 = d[1]; var o1 = d[3] + d[1]; var o2 = d[5] + d[3]; var o3 = d[7] + d[5];
            Idct4(ref e0, ref e1, ref e2, ref e3);
            Idct4(ref o0, ref o1, ref o2, ref o3);
            var x0 = o0 * new Vector<float>(Sec8_0); var x1 = o1 * new Vector<float>(Sec8_1);
            var x2 = o2 * new Vector<float>(Sec8_2); var x3 = o3 * new Vector<float>(Sec8_3);
            d[0] = e0 + x0; d[7] = e0 - x0;
            d[1] = e1 + x1; d[6] = e1 - x1;
            d[2] = e2 + x2; d[5] = e2 - x2;
            d[3] = e3 + x3; d[4] = e3 - x3;
        }

        /// <summary>Transposes the 8 x 8 floats at src (row stride ss) to dst (row stride ds).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Transpose8x8(float* src, int ss, float* dst, int ds)
        {
            var r0 = Avx.LoadVector256(src); var r1 = Avx.LoadVector256(src + ss);
            var r2 = Avx.LoadVector256(src + 2 * ss); var r3 = Avx.LoadVector256(src + 3 * ss);
            var r4 = Avx.LoadVector256(src + 4 * ss); var r5 = Avx.LoadVector256(src + 5 * ss);
            var r6 = Avx.LoadVector256(src + 6 * ss); var r7 = Avx.LoadVector256(src + 7 * ss);
            var t0 = Avx.UnpackLow(r0, r1); var t1 = Avx.UnpackHigh(r0, r1);
            var t2 = Avx.UnpackLow(r2, r3); var t3 = Avx.UnpackHigh(r2, r3);
            var t4 = Avx.UnpackLow(r4, r5); var t5 = Avx.UnpackHigh(r4, r5);
            var t6 = Avx.UnpackLow(r6, r7); var t7 = Avx.UnpackHigh(r6, r7);
            var u0 = Avx.Shuffle(t0, t2, 0x44); var u1 = Avx.Shuffle(t0, t2, 0xEE);
            var u2 = Avx.Shuffle(t1, t3, 0x44); var u3 = Avx.Shuffle(t1, t3, 0xEE);
            var u4 = Avx.Shuffle(t4, t6, 0x44); var u5 = Avx.Shuffle(t4, t6, 0xEE);
            var u6 = Avx.Shuffle(t5, t7, 0x44); var u7 = Avx.Shuffle(t5, t7, 0xEE);
            Avx.Store(dst, Avx.Permute2x128(u0, u4, 0x20)); Avx.Store(dst + ds, Avx.Permute2x128(u1, u5, 0x20));
            Avx.Store(dst + 2 * ds, Avx.Permute2x128(u2, u6, 0x20)); Avx.Store(dst + 3 * ds, Avx.Permute2x128(u3, u7, 0x20));
            Avx.Store(dst + 4 * ds, Avx.Permute2x128(u0, u4, 0x31)); Avx.Store(dst + 5 * ds, Avx.Permute2x128(u1, u5, 0x31));
            Avx.Store(dst + 6 * ds, Avx.Permute2x128(u2, u6, 0x31)); Avx.Store(dst + 7 * ds, Avx.Permute2x128(u3, u7, 0x31));
        }

        /// <summary><see cref="Idct2D"/> with 8 lane vectors: transposes in 8 x 8 tiles, no row major intermediate.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void Idct2DAvx(float* coef, int rows, int cols, float* outp, int stride, float* tmp, bool transposeOut)
        {
            const float Sqrt2 = 1.41421356237309505f;
            int m = Math.Max(rows, cols);
            var vec = (Vector<float>*)tmp;
            var scr = vec + m;
            float* u = (float*)(scr + m);   // cols x rows, the transpose of the vertical result
            var vs = new Vector<float>(Sqrt2);
            for (int x = 0; x < cols; x += 8)
            {
                for (int k = 0; k < rows; k++)
                {
                    var v = Unsafe.ReadUnaligned<Vector<float>>(coef + k * cols + x);
                    vec[k] = k == 0 ? v : v * vs;
                }
                Idct(vec, rows, scr);
                for (int y = 0; y < rows; y += 8) Transpose8x8((float*)(vec + y), 8, u + x * rows + y, rows);
            }
            for (int y = 0; y < rows; y += 8)
            {
                for (int k = 0; k < cols; k++)
                {
                    var v = Unsafe.ReadUnaligned<Vector<float>>(u + k * rows + y);
                    vec[k] = k == 0 ? v : v * vs;
                }
                Idct(vec, cols, scr);
                if (transposeOut)
                    for (int x = 0; x < cols; x++) Unsafe.WriteUnaligned(outp + x * stride + y, vec[x]);
                else
                    for (int x = 0; x < cols; x += 8) Transpose8x8((float*)(vec + x), 8, outp + y * stride + x, stride);
            }
        }

        /// <summary>
        /// The scaled 2D IDCT (coefficient 0 = mean) of a rows x cols block when both dimensions are multiples of the
        /// vector width; returns false otherwise. tmp: 4 * rows * cols floats.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static bool Idct2D(float* coef, int rows, int cols, float* outp, int stride, float* tmp, bool transposeOut = false)
        {
            int vc = Vector<float>.Count;
            if (rows % vc != 0 || cols % vc != 0) return false;
            if (vc == 8 && Avx.IsSupported) { Idct2DAvx(coef, rows, cols, outp, stride, tmp, transposeOut); return true; }
            const float Sqrt2 = 1.41421356237309505f;
            int n = rows * cols;
            var vec = (Vector<float>*)tmp;                         // max(rows, cols) vectors
            var scr = vec + Math.Max(rows, cols);                  // the same again as scratch
            float* t = tmp + 2 * Math.Max(rows, cols) * vc + 64;   // rows x cols, row major
            float* u = t + n;                                      // cols x rows, the transpose
            var vs = new Vector<float>(Sqrt2);
            // vertical transforms, vc columns at a time: t[y][x]
            for (int x = 0; x < cols; x += vc)
            {
                for (int k = 0; k < rows; k++)
                {
                    var v = Unsafe.ReadUnaligned<Vector<float>>(coef + k * cols + x);
                    vec[k] = k == 0 ? v : v * vs;
                }
                Idct(vec, rows, scr);
                for (int y = 0; y < rows; y++) Unsafe.WriteUnaligned(t + y * cols + x, vec[y]);
            }
            for (int y = 0; y < rows; y++)
                for (int x = 0; x < cols; x++) u[x * rows + y] = t[y * cols + x];
            // horizontal transforms on the transpose, vc rows at a time: t[x][y] (cols x rows)
            for (int y = 0; y < rows; y += vc)
            {
                for (int k = 0; k < cols; k++)
                {
                    var v = Unsafe.ReadUnaligned<Vector<float>>(u + k * rows + y);
                    vec[k] = k == 0 ? v : v * vs;
                }
                Idct(vec, cols, scr);
                for (int x = 0; x < cols; x++) Unsafe.WriteUnaligned(t + x * rows + y, vec[x]);
            }
            if (transposeOut)
            {
                // the caller's rows / columns are swapped: out[x][y]
                for (int x = 0; x < cols; x++)
                {
                    float* orow = outp + x * stride;
                    int y = 0;
                    for (; y + vc <= rows; y += vc) Unsafe.WriteUnaligned(orow + y, Unsafe.ReadUnaligned<Vector<float>>(t + x * rows + y));
                }
            }
            else
            {
                for (int y = 0; y < rows; y++)
                {
                    float* orow = outp + y * stride;
                    for (int x = 0; x < cols; x++) orow[x] = t[x * rows + y];
                }
            }
            return true;
        }
    }
}
