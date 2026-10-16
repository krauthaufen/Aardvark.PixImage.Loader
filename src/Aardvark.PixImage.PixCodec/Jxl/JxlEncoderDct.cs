using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Aardvark.ImageCodecs.JpegXl
{
    /// <summary>
    /// Forward transforms of VarDCT, the inverses of <see cref="JxlTransforms"/>: coefficients in the stored layout (rows =
    /// frequencies along the smaller block dimension; square blocks hold the horizontal frequency in rows).
    /// </summary>
    internal static unsafe class JxlEncDct
    {
        /// <summary>F[ky * W + kx] = (1/H)(1/W) sum x[y][x] M_H[y][ky] M_W[x][kx] (coefficient 0 is the mean).</summary>
        public static void Dct2D(float* src, int stride, int H, int W, float* F, float* tmp)
        {
            var mx = JxlTransforms.IdctMatrix(W);
            var my = JxlTransforms.IdctMatrix(H);
            int vc = Vector<float>.Count;
            float iw = 1f / W, ih = 1f / H;
            fixed (float* pmx = mx, pmy = my)
            {
                // rows: T[y][kx] = (1/W) sum_x x[y][x] Mx[x][kx]
                for (int y = 0; y < H; y++)
                {
                    float* t = tmp + y * W;
                    float* s = src + y * stride;
                    if (W % vc == 0)
                    {
                        for (int k = 0; k < W; k += vc)
                        {
                            var acc = Vector<float>.Zero;
                            for (int x = 0; x < W; x++) acc += new Vector<float>(s[x]) * Unsafe.ReadUnaligned<Vector<float>>(pmx + x * W + k);
                            Unsafe.WriteUnaligned(t + k, acc * iw);
                        }
                    }
                    else
                    {
                        for (int k = 0; k < W; k++)
                        {
                            float a = 0;
                            for (int x = 0; x < W; x++) a += s[x] * pmx[x * W + k];
                            t[k] = a * iw;
                        }
                    }
                }
                // columns: F[ky][kx] = (1/H) sum_y T[y][kx] My[y][ky]
                for (int ky = 0; ky < H; ky++)
                {
                    float* f = F + ky * W;
                    if (W % vc == 0)
                    {
                        for (int k = 0; k < W; k += vc)
                        {
                            var acc = Vector<float>.Zero;
                            for (int y = 0; y < H; y++) acc += new Vector<float>(pmy[y * H + ky]) * Unsafe.ReadUnaligned<Vector<float>>(tmp + y * W + k);
                            Unsafe.WriteUnaligned(f + k, acc * ih);
                        }
                    }
                    else
                    {
                        for (int k = 0; k < W; k++)
                        {
                            float a = 0;
                            for (int y = 0; y < H; y++) a += tmp[y * W + k] * pmy[y * H + ky];
                            f[k] = a * ih;
                        }
                    }
                }
            }
        }

        /// <summary>Forward DCT of an H x W block into the stored layout. scratch: 2 H W floats.</summary>
        public static void DctStored(float* src, int stride, int H, int W, float* coef, float* scratch)
        {
            if (W > H) { Dct2D(src, stride, H, W, coef, scratch); return; }
            float* F = scratch + H * W;
            Dct2D(src, stride, H, W, F, scratch);
            for (int ky = 0; ky < H; ky++)
                for (int kx = 0; kx < W; kx++) coef[kx * H + ky] = F[ky * W + kx];
        }

        /// <summary>
        /// Forward transform of one varblock (pixels src[y * stride + x], 8 CoveredY rows by 8 CoveredX columns) into its
        /// coefficients (stored layout, the LLF / DC positions included). scratch: 3 * size floats.
        /// </summary>
        public static void Forward(int strategy, float* src, int stride, float* coef, float* scratch)
        {
            int cx = JxlAcStrategy.CoveredX[strategy], cy = JxlAcStrategy.CoveredY[strategy];
            switch (strategy)
            {
                case JxlAcStrategy.Dct4x4: Dct4x4(src, stride, coef, scratch); return;
                case JxlAcStrategy.Dct4x8: Dct4x8(src, stride, coef, scratch, false); return;
                case JxlAcStrategy.Dct8x4: Dct4x8(src, stride, coef, scratch, true); return;
                case JxlAcStrategy.Identity:
                case JxlAcStrategy.Dct2x2:
                case JxlAcStrategy.Afv0: case JxlAcStrategy.Afv1: case JxlAcStrategy.Afv2: case JxlAcStrategy.Afv3:
                    throw new NotSupportedException("JPEG XL encoder: transform not implemented");
            }
            DctStored(src, stride, cy * 8, cx * 8, coef, scratch);
        }

        static void Dct4x4(float* src, int stride, float* coef, float* scratch)
        {
            float* sub = scratch + 64;
            float* d = stackalloc float[4];
            for (int y = 0; y < 2; y++)
                for (int x = 0; x < 2; x++)
                {
                    DctStored(src + 4 * y * stride + 4 * x, stride, 4, 4, sub, scratch + 96);
                    d[y * 2 + x] = sub[0];
                    for (int iy = 0; iy < 4; iy++)
                        for (int ix = 0; ix < 4; ix++)
                        {
                            if (ix == 0 && iy == 0) continue;
                            coef[(iy * 2 + y) * 8 + ix * 2 + x] = sub[iy * 4 + ix];
                        }
                }
            // the four DCs, as the decoder's 2x2 step combines them
            coef[0] = (d[0] + d[1] + d[2] + d[3]) * 0.25f;
            coef[1] = (d[0] + d[1] - d[2] - d[3]) * 0.25f;
            coef[8] = (d[0] - d[1] + d[2] - d[3]) * 0.25f;
            coef[9] = (d[0] - d[1] - d[2] + d[3]) * 0.25f;
        }

        static void Dct4x8(float* src, int stride, float* coef, float* scratch, bool transposed)
        {
            float* half = scratch + 64;
            float d0 = 0, d1 = 0;
            for (int i = 0; i < 2; i++)
            {
                if (!transposed) DctStored(src + 4 * i * stride, stride, 4, 8, half, scratch + 96);
                else DctStored(src + 4 * i, stride, 8, 4, half, scratch + 96);
                if (i == 0) d0 = half[0]; else d1 = half[0];
                for (int iy = 0; iy < 4; iy++)
                    for (int ix = 0; ix < 8; ix++)
                    {
                        if (ix == 0 && iy == 0) continue;
                        coef[(iy * 2 + i) * 8 + ix] = half[iy * 8 + ix];
                    }
            }
            coef[0] = (d0 + d1) * 0.5f;
            coef[8] = (d0 - d1) * 0.5f;
        }
    }
}
