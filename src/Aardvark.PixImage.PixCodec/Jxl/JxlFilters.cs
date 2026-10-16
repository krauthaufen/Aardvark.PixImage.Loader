using System;
using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Aardvark.ImageCodecs.JpegXl
{
    /// <summary>
    /// Restoration filters: Gaborish smoothing and the edge preserving filter (EPF). The chain of stages runs tile by tile
    /// on small buffers that hold each stage's output for the tile plus the border the next stage reads; every stage
    /// mirrors its input at the frame boundaries, as if it ran over the whole frame.
    /// </summary>
    [SkipLocalsInit]
    internal static unsafe class JxlFilters
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

        public const float InvSigmaNum = -1.1715728752538099024f;
        /// <summary>Scale of the sum of absolute differences relative to sigma (found to be 1.65).</summary>
        public const float SadScale = 1.65f;
        public const float MinSigma = -3.90524291751269967465540850526868f;

        static readonly (int dx, int dy)[] Pass0 = { (0, -1), (-1, 0), (1, 0), (0, 1), (-1, -1), (1, -1), (-1, 1), (1, 1), (0, -2), (-2, 0), (2, 0), (0, 2) };
        static readonly (int dx, int dy)[] Pass12 = { (0, -1), (-1, 0), (1, 0), (0, 1) };

        const int TileW = 512, TileH = 64;

        /// <summary>A rectangle of a stage's output in frame coordinates (may reach outside the frame), stored row by row.</summary>
        struct Region
        {
            public int X0, Y0, W, H;
            public Region Grow(int r) => new() { X0 = X0 - r, Y0 = Y0 - r, W = W + 2 * r, H = H + 2 * r };
        }

        /// <summary>
        /// Runs Gaborish (when enabled) and the given EPF passes over the colour planes (1 or 3, w x h) and returns new
        /// planes. invSigma[block] holds InvSigmaNum / sigma per 8x8 block (very negative to skip the block).
        /// </summary>
        public static float[][] Restore(float[][] colour, int w, int h, JxlRestorationFilter f, int[] epfPasses, float[]? invSigma, int blocksX, bool parallel)
        {
            int nstage = (f.Gab ? 1 : 0) + epfPasses.Length;
            var radius = new int[nstage];
            var pass = new int[nstage];
            int s = 0;
            if (f.Gab) { radius[s] = 1; pass[s] = -1; s++; }
            foreach (int p in epfPasses) { radius[s] = p == 0 ? 3 : p == 1 ? 2 : 1; pass[s] = p; s++; }
            int rtot = 0;
            foreach (int r in radius) rtot += r;

            int nc = colour.Length;
            var dst = new float[nc][];
            for (int c = 0; c < nc; c++) dst[c] = new float[(long)w * h];
            int tw = Math.Min(TileW, w), th = Math.Min(TileH, h);
            int tx = (w + tw - 1) / tw, ty = (h + th - 1) / th;
            int bufPlane = (tw + 2 * rtot) * (th + 2 * rtot);
            var gab = new float[9];
            for (int c = 0; c < 3; c++)
            {
                float w1 = f.GabWeights[2 * Math.Min(c, nc - 1)], w2 = f.GabWeights[2 * Math.Min(c, nc - 1) + 1];
                float norm = 1 / (1 + 4 * w1 + 4 * w2);
                gab[3 * c] = norm; gab[3 * c + 1] = w1 * norm; gab[3 * c + 2] = w2 * norm;
            }
            var ctx = new Ctx { Src = colour, Dst = dst, W = w, H = h, Radius = radius, Pass = pass, F = f, InvSigma = invSigma, BlocksX = blocksX, Gab = gab };

            void Tile(int t, float[] work)
            {
                int x0 = t % tx * tw, y0 = t / tx * th;
                var outR = new Region { X0 = x0, Y0 = y0, W = Math.Min(tw, w - x0), H = Math.Min(th, h - y0) };
                ctx.RunTile(outR, work, bufPlane);
            }
            int workLen = 2 * 3 * bufPlane + 9 * (tw + 2 * rtot + 2) + 6 * (tw + 2 * rtot + 8) * (th + 2 * rtot + 8);
            int ntiles = tx * ty;
            if (parallel && ntiles > 1)
                Parallel.For(0, ntiles, () => ArrayPool<float>.Shared.Rent(workLen), (t, _, work) => { Tile(t, work); return work; },
                    work => ArrayPool<float>.Shared.Return(work));
            else
            {
                var work = ArrayPool<float>.Shared.Rent(workLen);
                for (int t = 0; t < ntiles; t++) Tile(t, work);
                ArrayPool<float>.Shared.Return(work);
            }
            return dst;
        }

        sealed class Ctx
        {
            public float[][] Src = null!, Dst = null!;
            public int W, H, BlocksX;
            public int[] Radius = null!, Pass = null!;
            public JxlRestorationFilter F = null!;
            public float[]? InvSigma;
            public float[] Gab = null!;

            public void RunTile(Region outR, float[] work, int bufPlane)
            {
                int n = Radius.Length;
                fixed (float* pw = work)
                {
                    float* bufA = pw, bufB = pw + 3 * bufPlane, rowWork = pw + 6 * bufPlane;
                    // stage input regions, from the last stage backwards
                    var regs = new Region[n + 1];
                    regs[n] = outR;
                    for (int k = n - 1; k >= 0; k--) regs[k] = regs[k + 1].Grow(Radius[k]);
                    LoadInput(regs[0], bufA, bufPlane);
                    float* cur = bufA, nxt = bufB;
                    for (int k = 0; k < n; k++)
                    {
                        var ri = regs[k]; var ro = regs[k + 1];
                        bool last = k == n - 1;
                        if (Pass[k] < 0) GabStage(ri, cur, ro, nxt, bufPlane);
                        else EpfStage(Pass[k], ri, cur, ro, nxt, bufPlane, rowWork);
                        if (!last) MirrorFill(ro, nxt, bufPlane);
                        float* tmp = cur; cur = nxt; nxt = tmp;
                    }
                    // cur holds the tile
                    int nc = Dst.Length;
                    for (int c = 0; c < nc; c++)
                    {
                        fixed (float* d = Dst[c])
                        {
                            for (int y = 0; y < outR.H; y++)
                                Buffer.MemoryCopy(cur + c * bufPlane + y * outR.W, d + (long)(outR.Y0 + y) * W + outR.X0, outR.W * 4L, outR.W * 4L);
                        }
                    }
                }
            }

            /// <summary>The input planes over a region, mirrored outside the frame (grey input fills all three planes).</summary>
            void LoadInput(Region r, float* buf, int bufPlane)
            {
                int ax = Math.Max(r.X0, 0), bx = Math.Min(r.X0 + r.W, W);
                for (int c = 0; c < 3; c++)
                {
                    fixed (float* s = Src[Math.Min(c, Src.Length - 1)])
                    {
                        for (int y = 0; y < r.H; y++)
                        {
                            float* srow = s + (long)Mirror(r.Y0 + y, H) * W;
                            float* drow = buf + c * bufPlane + y * r.W - r.X0;   // indexed by frame x
                            Buffer.MemoryCopy(srow + ax, drow + ax, (bx - ax) * 4L, (bx - ax) * 4L);
                            for (int x = r.X0; x < ax; x++) drow[x] = srow[Mirror(x, W)];
                            for (int x = bx; x < r.X0 + r.W; x++) drow[x] = srow[Mirror(x, W)];
                        }
                    }
                }
            }

            /// <summary>Fills the part of a stage output region outside the frame by mirroring the computed part.</summary>
            void MirrorFill(Region r, float* buf, int bufPlane)
            {
                int ax = Math.Max(r.X0, 0), bx = Math.Min(r.X0 + r.W, W);
                int ay = Math.Max(r.Y0, 0), by = Math.Min(r.Y0 + r.H, H);
                if (ax == r.X0 && bx == r.X0 + r.W && ay == r.Y0 && by == r.Y0 + r.H) return;
                for (int c = 0; c < 3; c++)
                {
                    float* p = buf + c * bufPlane;
                    if (ax > r.X0 || bx < r.X0 + r.W)
                        for (int y = ay; y < by; y++)
                        {
                            float* row = p + (y - r.Y0) * r.W - r.X0;
                            for (int x = r.X0; x < ax; x++) row[x] = row[Mirror(x, W)];
                            for (int x = bx; x < r.X0 + r.W; x++) row[x] = row[Mirror(x, W)];
                        }
                    for (int y = r.Y0; y < r.Y0 + r.H; y++)
                    {
                        if (y >= ay && y < by) continue;
                        Buffer.MemoryCopy(p + (Mirror(y, H) - r.Y0) * r.W, p + (y - r.Y0) * r.W, r.W * 4L, r.W * 4L);
                    }
                }
            }

            void GabStage(Region ri, float* src, Region ro, float* dst, int bufPlane)
            {
                int ax = Math.Max(ro.X0, 0), bx = Math.Min(ro.X0 + ro.W, W);
                int ay = Math.Max(ro.Y0, 0), by = Math.Min(ro.Y0 + ro.H, H);
                int vc = Vector<float>.Count;
                for (int c = 0; c < 3; c++)
                {
                    float c0 = Gab[3 * c], c1 = Gab[3 * c + 1], c2 = Gab[3 * c + 2];
                    var v0 = new Vector<float>(c0); var v1 = new Vector<float>(c1); var v2 = new Vector<float>(c2);
                    for (int y = ay; y < by; y++)
                    {
                        float* m = src + c * bufPlane + (y - ri.Y0) * ri.W - ri.X0, u = m - ri.W, d = m + ri.W;
                        float* o = dst + c * bufPlane + (y - ro.Y0) * ro.W - ro.X0;
                        int x = ax;
                        for (; x + vc <= bx; x += vc)
                        {
                            var side = Unsafe.ReadUnaligned<Vector<float>>(u + x) + Unsafe.ReadUnaligned<Vector<float>>(d + x)
                                     + Unsafe.ReadUnaligned<Vector<float>>(m + x - 1) + Unsafe.ReadUnaligned<Vector<float>>(m + x + 1);
                            var corner = Unsafe.ReadUnaligned<Vector<float>>(u + x - 1) + Unsafe.ReadUnaligned<Vector<float>>(u + x + 1)
                                       + Unsafe.ReadUnaligned<Vector<float>>(d + x - 1) + Unsafe.ReadUnaligned<Vector<float>>(d + x + 1);
                            Unsafe.WriteUnaligned(o + x, Unsafe.ReadUnaligned<Vector<float>>(m + x) * v0 + side * v1 + corner * v2);
                        }
                        for (; x < bx; x++)
                        {
                            float side = u[x] + d[x] + m[x - 1] + m[x + 1];
                            float corner = u[x - 1] + u[x + 1] + d[x - 1] + d[x + 1];
                            o[x] = m[x] * c0 + side * c1 + corner * c2;
                        }
                    }
                }
            }

            /// <summary>Distinct neighbour offsets of the EPF passes (each neighbour is one of them or its negation).</summary>
            static readonly (int dx, int dy)[] Offsets0 = { (0, 1), (1, 0), (1, 1), (-1, 1), (0, 2), (2, 0) };
            static readonly (int dx, int dy)[] Offsets12 = { (0, 1), (1, 0) };

            /// <summary>
            /// One EPF pass. The weighted sum of absolute differences between a pixel and a neighbour over the patch is built
            /// from difference images D_o(q) = sum_c scale_c |P_c(q) - P_c(q + o)|, one per distinct offset o, computed once
            /// per tile: neighbour +o reads D_o around the pixel p, neighbour -o reads it around p - o.
            /// </summary>
            void EpfStage(int pass, Region ri, float* src, Region ro, float* dst, int bufPlane, float* rw)
            {
                int ax = Math.Max(ro.X0, 0), bx = Math.Min(ro.X0 + ro.W, W);
                int ay = Math.Max(ro.Y0, 0), by = Math.Min(ro.Y0 + ro.H, H);
                int w = bx - ax;
                var f = F;
                var nb = pass == 0 ? Pass0 : Pass12;
                var offs = pass == 0 ? Offsets0 : Offsets12;
                float sigmaScale = SadScale * (pass == 0 ? f.EpfPass0SigmaScale : pass == 2 ? f.EpfPass2SigmaScale : 1);
                float s0 = f.EpfChannelScale[0], s1 = f.EpfChannelScale[1], s2 = f.EpfChannelScale[2];
                float border = f.EpfBorderSadMul;
                bool patch = pass != 2;
                int pr = patch ? 1 : 0;
                int vc = Vector<float>.Count;
                int pw = ri.W;
                var invSigma = InvSigma!;
                float* pinv = rw, pwsum = rw + w, pacc0 = rw + 2 * w, pacc1 = rw + 3 * w, pacc2 = rw + 4 * w;
                float* dbase = rw + 9 * (w + 2);
                var vs0 = new Vector<float>(s0); var vs1 = new Vector<float>(s1); var vs2 = new Vector<float>(s2);

                // difference images over the positions the patches reach: q = p + r and q = p - o, |r| <= pr
                int no = offs.Length;
                Span<int> dX0 = stackalloc int[no], dY0 = stackalloc int[no], dW = stackalloc int[no];
                var dImg = stackalloc float*[no];
                float* dnext = dbase;
                for (int k = 0; k < no; k++)
                {
                    var (ox, oy) = offs[k];
                    int qx0 = ax - pr + Math.Min(0, -ox), qx1 = bx + pr + Math.Max(0, -ox);
                    int qy0 = ay - pr + Math.Min(0, -oy), qy1 = by + pr + Math.Max(0, -oy);
                    int dw = qx1 - qx0;
                    dX0[k] = qx0; dY0[k] = qy0; dW[k] = dw; dImg[k] = dnext;
                    int off = oy * pw + ox;
                    for (int qy = qy0; qy < qy1; qy++)
                    {
                        float* pa = src + (qy - ri.Y0) * pw - ri.X0 + qx0, pb = pa + bufPlane, pc = pb + bufPlane;
                        float* drow = dnext + (qy - qy0) * dw;
                        int x = 0;
                        for (; x + vc <= dw; x += vc)
                        {
                            var e = Vector.Abs(Unsafe.ReadUnaligned<Vector<float>>(pa + x) - Unsafe.ReadUnaligned<Vector<float>>(pa + x + off)) * vs0
                                  + Vector.Abs(Unsafe.ReadUnaligned<Vector<float>>(pb + x) - Unsafe.ReadUnaligned<Vector<float>>(pb + x + off)) * vs1
                                  + Vector.Abs(Unsafe.ReadUnaligned<Vector<float>>(pc + x) - Unsafe.ReadUnaligned<Vector<float>>(pc + x + off)) * vs2;
                            Unsafe.WriteUnaligned(drow + x, e);
                        }
                        for (; x < dw; x++)
                            drow[x] = s0 * Math.Abs(pa[x] - pa[x + off]) + s1 * Math.Abs(pb[x] - pb[x + off]) + s2 * Math.Abs(pc[x] - pc[x + off]);
                    }
                    dnext += dw * (qy1 - qy0);
                }

                for (int y = ay; y < by; y++)
                {
                    bool any = false;
                    bool yBorder = (y & 7) == 0 || (y & 7) == 7;
                    int brow = (y >> 3) * BlocksX;
                    for (int i = 0; i < w; i++)
                    {
                        int x = ax + i;
                        float v = invSigma[brow + (x >> 3)];
                        if (v < MinSigma) { pinv[i] = -1e30f; continue; }
                        any = true;
                        pinv[i] = v * sigmaScale * (yBorder || (x & 7) == 0 || (x & 7) == 7 ? border : 1);
                    }
                    // rows of the three source planes at y, indexed from x = ax
                    float* pa = src + (y - ri.Y0) * pw - ri.X0 + ax, pb = pa + bufPlane, pc = pb + bufPlane;
                    float* oa = dst + (y - ro.Y0) * ro.W - ro.X0 + ax, ob = oa + bufPlane, oc = ob + bufPlane;
                    if (!any)
                    {
                        Buffer.MemoryCopy(pa, oa, w * 4L, w * 4L);
                        Buffer.MemoryCopy(pb, ob, w * 4L, w * 4L);
                        Buffer.MemoryCopy(pc, oc, w * 4L, w * 4L);
                        continue;
                    }
                    for (int x = 0; x < w; x++) { pwsum[x] = 1; pacc0[x] = pa[x]; pacc1[x] = pb[x]; pacc2[x] = pc[x]; }
                    foreach (var (dx, dy) in nb)
                    {
                        int k = 0;
                        bool neg = false;
                        for (; k < no; k++)
                        {
                            if (offs[k].dx == dx && offs[k].dy == dy) break;
                            if (offs[k].dx == -dx && offs[k].dy == -dy) { neg = true; break; }
                        }
                        // centre of the patch in D: p (neighbour +o) or p + neighbour (neighbour -o)
                        int cx = ax + (neg ? dx : 0), cy = y + (neg ? dy : 0);
                        int dw = dW[k];
                        float* dm = dImg[k] + (cy - dY0[k]) * dw + (cx - dX0[k]) - 1;   // dm[xx + 1] is the centre
                        float* du = dm - dw, dd = dm + dw;
                        int off = dy * pw + dx;
                        var one = Vector<float>.One;
                        int xx = 0;
                        for (; xx + vc <= w; xx += vc)
                        {
                            var sad = Unsafe.ReadUnaligned<Vector<float>>(dm + xx + 1);
                            if (patch)
                                sad += Unsafe.ReadUnaligned<Vector<float>>(dm + xx) + Unsafe.ReadUnaligned<Vector<float>>(dm + xx + 2)
                                     + Unsafe.ReadUnaligned<Vector<float>>(du + xx + 1) + Unsafe.ReadUnaligned<Vector<float>>(dd + xx + 1);
                            var wt = Vector.Max(Vector<float>.Zero, one + sad * Unsafe.ReadUnaligned<Vector<float>>(pinv + xx));
                            int q = xx + off;
                            Unsafe.WriteUnaligned(pwsum + xx, Unsafe.ReadUnaligned<Vector<float>>(pwsum + xx) + wt);
                            Unsafe.WriteUnaligned(pacc0 + xx, Unsafe.ReadUnaligned<Vector<float>>(pacc0 + xx) + wt * Unsafe.ReadUnaligned<Vector<float>>(pa + q));
                            Unsafe.WriteUnaligned(pacc1 + xx, Unsafe.ReadUnaligned<Vector<float>>(pacc1 + xx) + wt * Unsafe.ReadUnaligned<Vector<float>>(pb + q));
                            Unsafe.WriteUnaligned(pacc2 + xx, Unsafe.ReadUnaligned<Vector<float>>(pacc2 + xx) + wt * Unsafe.ReadUnaligned<Vector<float>>(pc + q));
                        }
                        for (; xx < w; xx++)
                        {
                            float sad = patch ? dm[xx] + dm[xx + 1] + dm[xx + 2] + du[xx + 1] + dd[xx + 1] : dm[xx + 1];
                            float wt = Math.Max(0, 1 + sad * pinv[xx]);
                            int q = xx + off;
                            pwsum[xx] += wt;
                            pacc0[xx] += wt * pa[q]; pacc1[xx] += wt * pb[q]; pacc2[xx] += wt * pc[q];
                        }
                    }
                    for (int x = 0; x < w; x++)
                    {
                        float rr = 1 / pwsum[x];
                        oa[x] = pacc0[x] * rr; ob[x] = pacc1[x] * rr; oc[x] = pacc2[x] * rr;
                    }
                }
            }
        }
    }
}
