using System;
using System.Threading.Tasks;
using T = Aardvark.ImageCodecs.Av1.Av1Tables;

namespace Aardvark.ImageCodecs.Av1
{
    /// <summary>
    /// Loop restoration (AV1 spec 7.17): Wiener and self guided filters per restoration unit. The spec filters 4x4 blocks;
    /// all blocks of a 64 row stripe inside one restoration unit share their parameters and source samples, so they are
    /// filtered together as one rectangle (same result, as the spec notes).
    /// </summary>
    internal static class Av1LoopRestoration
    {
        sealed class Ctx
        {
            public Av1FrameState F = null!;
            public Av1Frame UpCur = null!, UpCdef = null!, Lr = null!;
            public int Plane, StripeStartY, StripeEndY, PlaneEndX, PlaneEndY, BitDepth;
        }

        public static Av1Frame Apply(Av1FrameState f, Av1Frame upCur, Av1Frame upCdef, bool multithreaded)
        {
            var h = f.Hdr;
            var s = f.Seq;
            var lr = upCdef.Clone();
            for (int plane = 0; plane < s.NumPlanes; plane++)
            {
                if (h.FrameRestorationType[plane] == Av1C.RestoreNone) continue;
                int sx = plane > 0 ? s.SubsamplingX : 0, sy = plane > 0 ? s.SubsamplingY : 0;
                int unitSize = h.LoopRestorationSize[plane];
                int unitRows = f.LrUnitRows[plane], unitCols = f.LrUnitCols[plane];
                int planeEndX = Av1FrameState.Round2(h.UpscaledWidth, sx) - 1;
                int planeEndY = Av1FrameState.Round2(h.FrameHeight, sy) - 1;
                int numStripes = (h.FrameHeight + 8 + 63) / 64;
                int p = plane;
                void Stripe(int stripeNum)
                {
                    // luma rows of this stripe: (y + 8) / 64 == stripeNum, y a multiple of MI_SIZE below FrameHeight
                    int lumaY0 = Math.Max(0, stripeNum * 64 - 8), lumaY1 = Math.Min(h.FrameHeight, stripeNum * 64 + 56);
                    if (lumaY0 >= lumaY1) return;
                    int y0 = lumaY0 >> sy;
                    int y1 = Math.Min(planeEndY + 1, ((lumaY1 + Av1C.MiSize - 1) / Av1C.MiSize * Av1C.MiSize) >> sy);
                    if (y0 >= y1) return;
                    var c = new Ctx
                    {
                        F = f, UpCur = upCur, UpCdef = upCdef, Lr = lr, Plane = p,
                        StripeStartY = (-8 + stripeNum * 64) >> sy,
                        PlaneEndX = planeEndX, PlaneEndY = planeEndY, BitDepth = s.BitDepth,
                    };
                    c.StripeEndY = c.StripeStartY + (64 >> sy) - 1;
                    int unitRow = Math.Min(unitRows - 1, ((lumaY0 + 8) >> sy) / unitSize);
                    var scratch = new Scratch();
                    for (int unitCol = 0; unitCol < unitCols; unitCol++)
                    {
                        int x0 = unitCol * unitSize;
                        int x1 = unitCol == unitCols - 1 ? planeEndX + 1 : Math.Min(planeEndX + 1, (unitCol + 1) * unitSize);
                        if (x0 >= x1) continue;
                        int unit = unitRow * unitCols + unitCol;
                        int rType = f.LrType[p][unit];
                        if (rType == Av1C.RestoreWiener) Wiener(c, unit, x0, y0, x1 - x0, y1 - y0, scratch);
                        else if (rType == Av1C.RestoreSgrproj) SelfGuided(c, unit, x0, y0, x1 - x0, y1 - y0, scratch);
                    }
                }
                if (multithreaded && numStripes > 1) Parallel.For(0, numStripes, Stripe);
                else for (int st = 0; st < numStripes; st++) Stripe(st);
            }
            return lr;
        }

        sealed class Scratch
        {
            public int[] Inter = Array.Empty<int>(), A = Array.Empty<int>(), B = Array.Empty<int>(), F0 = Array.Empty<int>(), F1 = Array.Empty<int>();
            public static int[] Ensure(ref int[] a, int n) { if (a.Length < n) a = new int[n]; return a; }
        }

        static int Source(Ctx c, int x, int y)
        {
            x = Math.Max(0, Math.Min(c.PlaneEndX, x));
            y = Math.Max(0, Math.Min(c.PlaneEndY, y));
            if (y < c.StripeStartY)
            {
                y = Math.Max(c.StripeStartY - 2, y);
                return c.UpCur.Planes[c.Plane][y * c.UpCur.Stride[c.Plane] + x];
            }
            if (y > c.StripeEndY)
            {
                y = Math.Min(c.StripeEndY + 2, y);
                return c.UpCur.Planes[c.Plane][y * c.UpCur.Stride[c.Plane] + x];
            }
            return c.UpCdef.Planes[c.Plane][y * c.UpCdef.Stride[c.Plane] + x];
        }

        static void Wiener(Ctx c, int unit, int x, int y, int w, int hh, Scratch sc)
        {
            var f = c.F;
            int bd = c.BitDepth;
            int round0 = bd == 12 ? 5 : 3;
            int round1 = bd == 12 ? 9 : 11;
            Span<int> vfilter = stackalloc int[7], hfilter = stackalloc int[7];
            var coeffs = f.LrWiener[c.Plane];
            Coefficients(coeffs, unit * 6, vfilter);
            Coefficients(coeffs, unit * 6 + 3, hfilter);
            int offset = 1 << (bd + Av1C.FilterBits - round0 - 1);
            int limit = (1 << (bd + 1 + Av1C.FilterBits - round0)) - 1;
            var inter = Scratch.Ensure(ref sc.Inter, (hh + 6) * w);
            for (int r = 0; r < hh + 6; r++)
                for (int col = 0; col < w; col++)
                {
                    int s = 0;
                    for (int t = 0; t < 7; t++) s += hfilter[t] * Source(c, x + col + t - 3, y + r - 3);
                    int v = (s + (1 << (round0 - 1))) >> round0;
                    inter[r * w + col] = Math.Clamp(v, -offset, limit - offset);
                }
            var dst = c.Lr.Planes[c.Plane];
            int stride = c.Lr.Stride[c.Plane];
            int maxV = (1 << bd) - 1;
            for (int r = 0; r < hh; r++)
                for (int col = 0; col < w; col++)
                {
                    int s = 0;
                    for (int t = 0; t < 7; t++) s += vfilter[t] * inter[(r + t) * w + col];
                    int v = (s + (1 << (round1 - 1))) >> round1;
                    dst[(y + r) * stride + x + col] = (ushort)Math.Clamp(v, 0, maxV);
                }
        }

        static void Coefficients(short[] coeff, int o, Span<int> filter)
        {
            filter[3] = 128;
            for (int i = 0; i < 3; i++)
            {
                int cc = coeff[o + i];
                filter[i] = cc;
                filter[6 - i] = cc;
                filter[3] -= 2 * cc;
            }
        }

        static void SelfGuided(Ctx c, int unit, int x, int y, int w, int hh, Scratch sc)
        {
            var f = c.F;
            int set = f.LrSgrSet[c.Plane][unit];
            var flt0 = Scratch.Ensure(ref sc.F0, w * hh);
            var flt1 = Scratch.Ensure(ref sc.F1, w * hh);
            BoxFilter(c, x, y, w, hh, set, 0, flt0, sc);
            BoxFilter(c, x, y, w, hh, set, 1, flt1, sc);
            int w0 = f.LrSgrXqd[c.Plane][unit * 2], w1 = f.LrSgrXqd[c.Plane][unit * 2 + 1];
            int w2 = (1 << Av1C.SgrprojPrjBits) - w0 - w1;
            int r0 = T.SgrParams[set * 4], r1 = T.SgrParams[set * 4 + 2];
            var cdef = c.UpCdef.Planes[c.Plane];
            int cs = c.UpCdef.Stride[c.Plane];
            var dst = c.Lr.Planes[c.Plane];
            int ds = c.Lr.Stride[c.Plane];
            int maxV = (1 << c.BitDepth) - 1;
            const int sh = Av1C.SgrprojRstBits + Av1C.SgrprojPrjBits;
            for (int i = 0; i < hh; i++)
                for (int j = 0; j < w; j++)
                {
                    long u = cdef[(y + i) * cs + x + j] << Av1C.SgrprojRstBits;
                    long v = w1 * u;
                    v += r0 != 0 ? (long)w0 * flt0[i * w + j] : w0 * u;
                    v += r1 != 0 ? (long)w2 * flt1[i * w + j] : w2 * u;
                    long s = (v + (1L << (sh - 1))) >> sh;
                    dst[(y + i) * ds + x + j] = (ushort)Math.Clamp(s, 0, maxV);
                }
        }

        static void BoxFilter(Ctx c, int x, int y, int w, int hh, int set, int pass, int[] F, Scratch sc)
        {
            int r = T.SgrParams[set * 4 + pass * 2];
            if (r == 0) return;
            int eps = T.SgrParams[set * 4 + pass * 2 + 1];
            int bd = c.BitDepth;
            int aw = w + 2, ah = hh + 2;
            var A = Scratch.Ensure(ref sc.A, aw * ah);
            var B = Scratch.Ensure(ref sc.B, aw * ah);
            int n = (2 * r + 1) * (2 * r + 1);
            int n2e = n * n * eps;
            int s = ((1 << Av1C.SgrprojMtableBits) + n2e / 2) / n2e;
            int oneOverN = ((1 << Av1C.SgrprojRecipBits) + n / 2) / n;
            for (int i = -1; i < hh + 1; i++)
                for (int j = -1; j < w + 1; j++)
                {
                    long a = 0, b = 0;
                    for (int dy = -r; dy <= r; dy++)
                        for (int dx = -r; dx <= r; dx++)
                        {
                            int cv = Source(c, x + j + dx, y + i + dy);
                            a += cv * cv;
                            b += cv;
                        }
                    a = Round2(a, 2 * (bd - 8));
                    long d = Round2(b, bd - 8);
                    long p = Math.Max(0, a * n - d * d);
                    long z = (p * s + (1L << (Av1C.SgrprojMtableBits - 1))) >> Av1C.SgrprojMtableBits;
                    int a2;
                    if (z >= 255) a2 = 256;
                    else if (z == 0) a2 = 1;
                    else a2 = (int)(((z << Av1C.SgrprojSgrBits) + z / 2) / (z + 1));
                    long b2 = ((1 << Av1C.SgrprojSgrBits) - a2) * b * oneOverN;
                    A[(i + 1) * aw + j + 1] = a2;
                    B[(i + 1) * aw + j + 1] = (int)((b2 + (1L << (Av1C.SgrprojRecipBits - 1))) >> Av1C.SgrprojRecipBits);
                }
            var cdef = c.UpCdef.Planes[c.Plane];
            int cs = c.UpCdef.Stride[c.Plane];
            for (int i = 0; i < hh; i++)
            {
                int shift = 5;
                if (pass == 0 && (i & 1) != 0) shift = 4;
                for (int j = 0; j < w; j++)
                {
                    long a = 0, b = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int weight;
                            if (pass == 0) weight = ((i + dy) & 1) != 0 ? (dx == 0 ? 6 : 5) : 0;
                            else weight = dx == 0 || dy == 0 ? 4 : 3;
                            a += weight * A[(i + dy + 1) * aw + j + dx + 1];
                            b += weight * B[(i + dy + 1) * aw + j + dx + 1];
                        }
                    long v = a * cdef[(y + i) * cs + x + j] + b;
                    int sh = Av1C.SgrprojSgrBits + shift - Av1C.SgrprojRstBits;
                    F[i * w + j] = (int)((v + (1L << (sh - 1))) >> sh);
                }
            }
        }

        static long Round2(long x, int n) => n == 0 ? x : (x + (1L << (n - 1))) >> n;
    }
}
