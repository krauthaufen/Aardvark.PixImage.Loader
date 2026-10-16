using System;
using System.Numerics;
using System.Runtime.CompilerServices;

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
        public static void Idct(Vector<float>* data, int n, Vector<float>* scratch)
        {
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

        /// <summary>
        /// The scaled 2D IDCT (coefficient 0 = mean) of a rows x cols block when both dimensions are multiples of the
        /// vector width; returns false otherwise. tmp: 4 * rows * cols floats.
        /// </summary>
        public static bool Idct2D(float* coef, int rows, int cols, float* outp, int stride, float* tmp, bool transposeOut = false)
        {
            int vc = Vector<float>.Count;
            if (rows % vc != 0 || cols % vc != 0) return false;
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
