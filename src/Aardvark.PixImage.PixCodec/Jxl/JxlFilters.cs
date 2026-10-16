using System;
using System.Threading.Tasks;

namespace Aardvark.ImageCodecs.JpegXl
{
    /// <summary>Restoration filters: Gaborish smoothing and the edge preserving filter (EPF).</summary>
    internal static class JxlFilters
    {
        /// <summary>Mirrored coordinate for out of range positions (-1 -> 0, n -> n - 1).</summary>
        public static int Mirror(int x, int n)
        {
            while (x < 0 || x >= n)
            {
                if (x < 0) x = -x - 1;
                if (x >= n) x = 2 * n - 1 - x;
            }
            return x;
        }

        static void Rows(int h, bool parallel, Action<int> row)
        {
            if (parallel && h >= 64) Parallel.For(0, (h + 15) / 16, k => { for (int y = k * 16, e = Math.Min(h, y + 16); y < e; y++) row(y); });
            else for (int y = 0; y < h; y++) row(y);
        }

        public static float[] Gaborish(float[] src, int w, int h, float w1, float w2, bool parallel)
        {
            var o = new float[src.Length];
            float norm = 1 / (1 + 4 * w1 + 4 * w2);
            float c0 = norm, c1 = w1 * norm, c2 = w2 * norm;
            Rows(h, parallel, y =>
            {
                int ym = Mirror(y - 1, h) * w, y0 = y * w, yp = Mirror(y + 1, h) * w;
                for (int x = 0; x < w; x++)
                {
                    int xm = x > 0 ? x - 1 : Mirror(x - 1, w), xp = x + 1 < w ? x + 1 : Mirror(x + 1, w);
                    float side = src[ym + x] + src[yp + x] + src[y0 + xm] + src[y0 + xp];
                    float corner = src[ym + xm] + src[ym + xp] + src[yp + xm] + src[yp + xp];
                    o[y0 + x] = src[y0 + x] * c0 + side * c1 + corner * c2;
                }
            });
            return o;
        }

        public const float InvSigmaNum = -1.1715728752538099024f;
        /// <summary>Scale of the sum of absolute differences relative to sigma (found to be 1.65).</summary>
        public const float SadScale = 1.65f;
        public const float MinSigma = -3.90524291751269967465540850526868f;

        static readonly (int dx, int dy)[] Pass0 = { (0, -1), (-1, 0), (1, 0), (0, 1), (-1, -1), (1, -1), (-1, 1), (1, 1), (0, -2), (-2, 0), (2, 0), (0, 2) };
        static readonly (int dx, int dy)[] Pass12 = { (0, -1), (-1, 0), (1, 0), (0, 1) };

        /// <summary>
        /// One EPF pass over the three planes. invSigma[block] holds InvSigmaNum / sigma per 8x8 block (very negative to
        /// skip the block).
        /// </summary>
        public static float[][] EpfPass(float[][] src, int w, int h, int pass, float[] invSigma, int blocksX, JxlRestorationFilter f, bool parallel)
        {
            var dst = new[] { new float[src[0].Length], new float[src[1].Length], new float[src[2].Length] };
            var nb = pass == 0 ? Pass0 : Pass12;
            float sigmaScale = SadScale * (pass == 0 ? f.EpfPass0SigmaScale : pass == 2 ? f.EpfPass2SigmaScale : 1);
            float s0 = f.EpfChannelScale[0], s1 = f.EpfChannelScale[1], s2 = f.EpfChannelScale[2];
            float border = f.EpfBorderSadMul;
            var a = src[0]; var b = src[1]; var c = src[2];
            var oa = dst[0]; var ob = dst[1]; var oc = dst[2];
            Rows(h, parallel, y =>
            {
                Span<int> rows = stackalloc int[5];
                for (int k = -2; k <= 2; k++) rows[k + 2] = Mirror(y + k, h) * w;
                bool yBorder = (y & 7) == 0 || (y & 7) == 7;
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    float inv = invSigma[(y >> 3) * blocksX + (x >> 3)];
                    if (inv < MinSigma)
                    {
                        oa[i] = a[i]; ob[i] = b[i]; oc[i] = c[i];
                        continue;
                    }
                    inv *= sigmaScale;
                    float mul = yBorder || (x & 7) == 0 || (x & 7) == 7 ? border : 1;
                    float wsum = 1, va = a[i], vb = b[i], vc = c[i];
                    foreach (var (dx, dy) in nb)
                    {
                        int qy = rows[2 + dy], qx = Mirror(x + dx, w);
                        float sad;
                        if (pass == 2)
                        {
                            int q = qy + qx;
                            sad = s0 * Math.Abs(a[i] - a[q]) + s1 * Math.Abs(b[i] - b[q]) + s2 * Math.Abs(c[i] - c[q]);
                        }
                        else
                        {
                            sad = 0;
                            // plus shaped patches around p and q
                            for (int k = 0; k < 5; k++)
                            {
                                int ox = k == 1 ? -1 : k == 2 ? 1 : 0, oy = k == 3 ? -1 : k == 4 ? 1 : 0;
                                int p2 = rows[2 + oy] + Mirror(x + ox, w);
                                int q2 = Mirror(y + dy + oy, h) * w + Mirror(x + dx + ox, w);
                                sad += s0 * Math.Abs(a[p2] - a[q2]) + s1 * Math.Abs(b[p2] - b[q2]) + s2 * Math.Abs(c[p2] - c[q2]);
                            }
                        }
                        float wt = Math.Max(0, 1 + sad * mul * inv);
                        int qq = qy + qx;
                        wsum += wt;
                        va += wt * a[qq]; vb += wt * b[qq]; vc += wt * c[qq];
                    }
                    float r = 1 / wsum;
                    oa[i] = va * r; ob[i] = vb * r; oc[i] = vc * r;
                }
            });
            return dst;
        }
    }
}
