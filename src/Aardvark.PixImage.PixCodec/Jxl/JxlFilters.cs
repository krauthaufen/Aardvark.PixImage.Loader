using System;
using System.Numerics;
using System.Runtime.CompilerServices;
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

        public static unsafe float[] Gaborish(float[] src, int w, int h, float w1, float w2, bool parallel)
        {
            var P = Pad(src, w, h, 1);
            int pw = w + 2;
            var o = new float[src.Length];
            float norm = 1 / (1 + 4 * w1 + 4 * w2);
            float c0 = norm, c1 = w1 * norm, c2 = w2 * norm;
            int vc = Vector<float>.Count;
            Rows(h, parallel, y =>
            {
                fixed (float* pp = P, po = o)
                {
                    float* m = pp + (y + 1) * pw + 1, u = m - pw, d = m + pw;
                    float* orow = po + (long)y * w;
                    var v0 = new Vector<float>(c0); var v1 = new Vector<float>(c1); var v2 = new Vector<float>(c2);
                    int x = 0;
                    for (; x + vc <= w; x += vc)
                    {
                        var side = Unsafe.ReadUnaligned<Vector<float>>(u + x) + Unsafe.ReadUnaligned<Vector<float>>(d + x)
                                 + Unsafe.ReadUnaligned<Vector<float>>(m + x - 1) + Unsafe.ReadUnaligned<Vector<float>>(m + x + 1);
                        var corner = Unsafe.ReadUnaligned<Vector<float>>(u + x - 1) + Unsafe.ReadUnaligned<Vector<float>>(u + x + 1)
                                   + Unsafe.ReadUnaligned<Vector<float>>(d + x - 1) + Unsafe.ReadUnaligned<Vector<float>>(d + x + 1);
                        Unsafe.WriteUnaligned(orow + x, Unsafe.ReadUnaligned<Vector<float>>(m + x) * v0 + side * v1 + corner * v2);
                    }
                    for (; x < w; x++)
                    {
                        float side = u[x] + d[x] + m[x - 1] + m[x + 1];
                        float corner = u[x - 1] + u[x + 1] + d[x - 1] + d[x + 1];
                        orow[x] = m[x] * c0 + side * c1 + corner * c2;
                    }
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

        /// <summary>The planes with a mirrored border of pad samples on every side.</summary>
        static float[] Pad(float[] src, int w, int h, int pad)
        {
            int pw = w + 2 * pad;
            var o = new float[(long)pw * (h + 2 * pad)];
            for (int y = -pad; y < h + pad; y++)
            {
                int sy = Mirror(y, h);
                int orow = (y + pad) * pw;
                Array.Copy(src, sy * w, o, orow + pad, w);
                for (int x = 1; x <= pad; x++)
                {
                    o[orow + pad - x] = src[sy * w + Mirror(-x, w)];
                    o[orow + pad + w - 1 + x] = src[sy * w + Mirror(w - 1 + x, w)];
                }
            }
            return o;
        }

        /// <summary>
        /// One EPF pass over the three planes. invSigma[block] holds InvSigmaNum / sigma per 8x8 block (very negative to
        /// skip the block).
        /// </summary>
        public static unsafe float[][] EpfPass(float[][] src, int w, int h, int pass, float[] invSigma, int blocksX, JxlRestorationFilter f, bool parallel)
        {
            const int pad = 3;
            int pw = w + 2 * pad;
            var P = new[] { Pad(src[0], w, h, pad), Pad(src[1], w, h, pad), Pad(src[2], w, h, pad) };
            var dst = new[] { new float[src[0].Length], new float[src[1].Length], new float[src[2].Length] };
            var nb = pass == 0 ? Pass0 : Pass12;
            float sigmaScale = SadScale * (pass == 0 ? f.EpfPass0SigmaScale : pass == 2 ? f.EpfPass2SigmaScale : 1);
            float s0 = f.EpfChannelScale[0], s1 = f.EpfChannelScale[1], s2 = f.EpfChannelScale[2];
            float border = f.EpfBorderSadMul;
            bool patch = pass != 2;
            int vc = Vector<float>.Count;
            Rows(h, parallel, y =>
            {
                // per pixel scaled inverse sigma (0 where the block is skipped) and SAD multiplier
                var inv = new float[w];   // scaled inverse sigma times the SAD multiplier, -1e30 where the block is skipped
                bool any = false;
                bool yBorder = (y & 7) == 0 || (y & 7) == 7;
                for (int x = 0; x < w; x++)
                {
                    float v = invSigma[(y >> 3) * blocksX + (x >> 3)];
                    if (v < MinSigma) { inv[x] = -1e30f; continue; }
                    any = true;
                    inv[x] = v * sigmaScale * (yBorder || (x & 7) == 0 || (x & 7) == 7 ? border : 1);
                }
                int orow = y * w, prow = (y + pad) * pw + pad;
                if (!any)
                {
                    for (int c = 0; c < 3; c++) Array.Copy(src[c], orow, dst[c], orow, w);
                    return;
                }
                var wsum = new float[w];
                var acc0 = new float[w]; var acc1 = new float[w]; var acc2 = new float[w];
                var d = new float[3 * (w + 2)];   // D rows y-1, y, y+1 over x = -1 .. w
                fixed (float* pa = P[0], pb = P[1], pc = P[2], pd = d, pwsum = wsum, pacc0 = acc0, pacc1 = acc1, pacc2 = acc2, pinv = inv)
                {
                    for (int x = 0; x < w; x++) { pwsum[x] = 1; pacc0[x] = pa[prow + x]; pacc1[x] = pb[prow + x]; pacc2[x] = pc[prow + x]; }
                    var vs0 = new Vector<float>(s0); var vs1 = new Vector<float>(s1); var vs2 = new Vector<float>(s2);
                    foreach (var (dx, dy) in nb)
                    {
                        int off = dy * pw + dx;
                        int nrows = patch ? 3 : 1;
                        for (int r = 0; r < nrows; r++)
                        {
                            int ry = patch ? r - 1 : 0;
                            int start = prow + ry * pw - 1;   // x = -1
                            float* drow = pd + r * (w + 2);
                            int n = w + 2, x = 0;
                            for (; x + vc <= n; x += vc)
                            {
                                int i = start + x;
                                var e = Vector.Abs(Unsafe.ReadUnaligned<Vector<float>>(pa + i) - Unsafe.ReadUnaligned<Vector<float>>(pa + i + off)) * vs0
                                      + Vector.Abs(Unsafe.ReadUnaligned<Vector<float>>(pb + i) - Unsafe.ReadUnaligned<Vector<float>>(pb + i + off)) * vs1
                                      + Vector.Abs(Unsafe.ReadUnaligned<Vector<float>>(pc + i) - Unsafe.ReadUnaligned<Vector<float>>(pc + i + off)) * vs2;
                                Unsafe.WriteUnaligned(drow + x, e);
                            }
                            for (; x < n; x++)
                            {
                                int i = start + x;
                                drow[x] = s0 * Math.Abs(pa[i] - pa[i + off]) + s1 * Math.Abs(pb[i] - pb[i + off]) + s2 * Math.Abs(pc[i] - pc[i + off]);
                            }
                        }
                        float* dm = pd + (patch ? (w + 2) : 0), du = pd, dd = pd + 2 * (w + 2);
                        var one = Vector<float>.One;
                        int xx = 0;
                        for (; xx + vc <= w; xx += vc)
                        {
                            var sad = Unsafe.ReadUnaligned<Vector<float>>(dm + xx + 1);
                            if (patch)
                                sad += Unsafe.ReadUnaligned<Vector<float>>(dm + xx) + Unsafe.ReadUnaligned<Vector<float>>(dm + xx + 2)
                                     + Unsafe.ReadUnaligned<Vector<float>>(du + xx + 1) + Unsafe.ReadUnaligned<Vector<float>>(dd + xx + 1);
                            var wt = Vector.Max(Vector<float>.Zero, one + sad * Unsafe.ReadUnaligned<Vector<float>>(pinv + xx));
                            int q = prow + xx + off;
                            Unsafe.WriteUnaligned(pwsum + xx, Unsafe.ReadUnaligned<Vector<float>>(pwsum + xx) + wt);
                            Unsafe.WriteUnaligned(pacc0 + xx, Unsafe.ReadUnaligned<Vector<float>>(pacc0 + xx) + wt * Unsafe.ReadUnaligned<Vector<float>>(pa + q));
                            Unsafe.WriteUnaligned(pacc1 + xx, Unsafe.ReadUnaligned<Vector<float>>(pacc1 + xx) + wt * Unsafe.ReadUnaligned<Vector<float>>(pb + q));
                            Unsafe.WriteUnaligned(pacc2 + xx, Unsafe.ReadUnaligned<Vector<float>>(pacc2 + xx) + wt * Unsafe.ReadUnaligned<Vector<float>>(pc + q));
                        }
                        for (; xx < w; xx++)
                        {
                            float sad = patch ? dm[xx] + dm[xx + 1] + dm[xx + 2] + du[xx + 1] + dd[xx + 1] : dm[xx + 1];
                            float wt = Math.Max(0, 1 + sad * pinv[xx]);
                            int q = prow + xx + off;
                            pwsum[xx] += wt;
                            pacc0[xx] += wt * pa[q]; pacc1[xx] += wt * pb[q]; pacc2[xx] += wt * pc[q];
                        }
                    }
                    var o0 = dst[0]; var o1 = dst[1]; var o2 = dst[2];
                    for (int x = 0; x < w; x++)
                    {
                        float rr = 1 / pwsum[x];
                        o0[orow + x] = pacc0[x] * rr; o1[orow + x] = pacc1[x] * rr; o2[orow + x] = pacc2[x] * rr;
                    }
                }
            });
            return dst;
        }
    }
}
