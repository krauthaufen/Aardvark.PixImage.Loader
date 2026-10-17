using System;
using System.Threading.Tasks;
using T = Aardvark.ImageCodecs.Av1.Av1Tables;

namespace Aardvark.ImageCodecs.Av1
{
    /// <summary>The deblocking loop filter of AV1 spec 7.14 (modifies CurrFrame in place).</summary>
    internal static class Av1LoopFilter
    {
        public static void Apply(Av1FrameState f, bool multithreaded)
        {
            var h = f.Hdr;
            var s = f.Seq;
            for (int plane = 0; plane < s.NumPlanes; plane++)
            {
                if (plane != 0 && h.LoopFilterLevel[1 + plane] == 0) continue;
                int sx = plane == 0 ? 0 : s.SubsamplingX, sy = plane == 0 ? 0 : s.SubsamplingY;
                int rowStep = plane == 0 ? 1 : 1 << sy, colStep = plane == 0 ? 1 : 1 << sx;
                int p = plane;
                // pass 0: vertical edges, rows are independent
                int rowGroups = (f.MiRows + 15) / 16;
                void Rows(int g)
                {
                    var cache = new LevelCache();
                    for (int row = g * 16; row < Math.Min(f.MiRows, g * 16 + 16); row += rowStep)
                        for (int col = 0; col < f.MiCols; col += colStep) Edge(f, p, 0, row, col, ref cache);
                }
                if (multithreaded && rowGroups > 1) Parallel.For(0, rowGroups, Rows);
                else for (int g = 0; g < rowGroups; g++) Rows(g);
                // pass 1: horizontal edges, columns are independent
                int colGroups = (f.MiCols + 15) / 16;
                void Cols(int g)
                {
                    var cache = new LevelCache();
                    for (int row = 0; row < f.MiRows; row += rowStep)
                        for (int col = g * 16; col < Math.Min(f.MiCols, g * 16 + 16); col += colStep) Edge(f, p, 1, row, col, ref cache);
                }
                if (multithreaded && colGroups > 1) Parallel.For(0, colGroups, Cols);
                else for (int g = 0; g < colGroups; g++) Cols(g);
            }
        }

        /// <summary>The filter levels of the last two blocks seen (neighbouring edges mostly share their blocks).</summary>
        struct LevelCache
        {
            public uint H0, H1;
            public int L0, L1;
            public LevelCache() { H0 = H1 = uint.MaxValue; }

            public int Get(Av1FrameState f, uint handle, int plane, int pass)
            {
                if (handle == H0) return L0;
                if (handle == H1) return L1;
                int l = FilterLevel(f, ref f.Block(handle), plane, pass);
                H1 = H0; L1 = L0; H0 = handle; L0 = l;
                return l;
            }
        }

        static void Edge(Av1FrameState f, int plane, int pass, int row, int col, ref LevelCache cache)
        {
            var h = f.Hdr;
            var s = f.Seq;
            int sx = plane == 0 ? 0 : s.SubsamplingX, sy = plane == 0 ? 0 : s.SubsamplingY;
            int dx = pass == 0 ? 1 : 0, dy = pass == 0 ? 0 : 1;
            int x = col * Av1C.MiSize, y = row * Av1C.MiSize;
            row |= sy;
            col |= sx;
            if (x >= h.FrameWidth || y >= h.FrameHeight) return;
            if (pass == 0 && x == 0) return;
            if (pass == 1 && y == 0) return;
            int xP = x >> sx, yP = y >> sy;
            int prevRow = row - (dy << sy), prevCol = col - (dx << sx);
            uint handle = f.BlockAt[row * f.MiStride + col];
            ref var b = ref f.Block(handle);
            int miSize = b.MiSize;
            var lft = f.LfTxSizes[plane];
            int txSz = lft[(row >> sy) * f.MiStride + (col >> sx)];
            int planeSize = T.SubsampledSize[miSize * 4 + sx * 2 + sy];
            bool skip = b.Skip;
            bool isIntra = b.RefFrame0 <= Av1C.IntraFrame;
            int prevTxSz = lft[(prevRow >> sy) * f.MiStride + (prevCol >> sx)];
            // the block and transform sizes are powers of two
            bool isBlockEdge = pass == 0 ? (xP & (T.Num4x4BlocksWide[planeSize] * 4 - 1)) == 0 : (yP & (T.Num4x4BlocksHigh[planeSize] * 4 - 1)) == 0;
            bool isTxEdge = pass == 0 ? (xP & (T.TxWidth[txSz] - 1)) == 0 : (yP & (T.TxHeight[txSz] - 1)) == 0;
            bool applyFilter = isTxEdge && (isBlockEdge || !skip || isIntra);
            if (!applyFilter) return;
            int baseSize = pass == 0 ? Math.Min(T.TxWidth[prevTxSz], T.TxWidth[txSz]) : Math.Min(T.TxHeight[prevTxSz], T.TxHeight[txSz]);
            int filterSize = plane == 0 ? Math.Min(16, baseSize) : Math.Min(8, baseSize);
            int lvl = cache.Get(f, handle, plane, pass);
            if (lvl == 0) lvl = cache.Get(f, f.BlockAt[prevRow * f.MiStride + prevCol], plane, pass);
            if (lvl == 0) return;
            int shift = h.LoopFilterSharpness > 4 ? 2 : h.LoopFilterSharpness > 0 ? 1 : 0;
            int limit = h.LoopFilterSharpness > 0 ? Math.Clamp(lvl >> shift, 1, 9 - h.LoopFilterSharpness) : Math.Max(1, lvl >> shift);
            int blimit = 2 * (lvl + 2) + limit;
            int thresh = lvl >> 4;
            var pl = f.Cur.Planes[plane];
            int stride = f.Cur.Stride[plane];
            int step = pass == 0 ? 1 : stride;      // across the edge
            int along = pass == 0 ? stride : 1;     // along the edge
            int bd = s.BitDepth;
            for (int i = 0; i < Av1C.MiSize; i++)
                SampleFilter(pl, (yP + dx * i) * stride + xP + dy * i, step, limit, blimit, thresh, filterSize, plane, bd);
        }

        static int FilterLevel(Av1FrameState f, ref Av1Block b, int plane, int pass)
        {
            var h = f.Hdr;
            int segment = b.SegmentId;
            int rf = b.RefFrame0;
            int mode = b.YMode;
            int modeType = mode >= Av1C.NearestMv && mode != Av1C.GlobalMv && mode != Av1C.GlobalGlobalMv ? 1 : 0;
            int deltaLF = !h.DeltaLfMulti ? b.DeltaLF0 : b.DeltaLF(plane == 0 ? pass : plane + 1);
            int i = plane == 0 ? pass : plane + 1;
            int baseFilterLevel = Math.Clamp(deltaLF + h.LoopFilterLevel[i], 0, Av1C.MaxLoopFilter);
            int lvlSeg = baseFilterLevel;
            int feature = Av1C.SegLvlAltLfYV + i;
            if (h.FeatureOn(segment, feature))
            {
                lvlSeg = h.Feature(segment, feature) + lvlSeg;
                lvlSeg = Math.Clamp(lvlSeg, 0, Av1C.MaxLoopFilter);
            }
            if (h.LoopFilterDeltaEnabled)
            {
                int nShift = lvlSeg >> 5;
                if (rf == Av1C.IntraFrame) lvlSeg += h.LoopFilterRefDeltas[Av1C.IntraFrame] << nShift;
                else if (rf > Av1C.IntraFrame) lvlSeg += (h.LoopFilterRefDeltas[rf] << nShift) + (h.LoopFilterModeDeltas[modeType] << nShift);
                lvlSeg = Math.Clamp(lvlSeg, 0, Av1C.MaxLoopFilter);
            }
            return lvlSeg;
        }

        static void SampleFilter(ushort[] p, int o, int step, int limit, int blimit, int thresh, int filterSize, int plane, int bd)
        {
            int q0 = p[o], q1 = p[o + step], q2 = 0, q3 = 0;
            int p0 = p[o - step], p1 = p[o - 2 * step], p2 = 0, p3 = 0;
            int sh = bd - 8;
            int threshBd = thresh << sh;
            bool hevMask = Math.Abs(p1 - p0) > threshBd || Math.Abs(q1 - q0) > threshBd;
            int filterLen = filterSize == 4 ? 4 : plane != 0 ? 6 : filterSize == 8 ? 8 : 16;
            int limitBd = limit << sh, blimitBd = blimit << sh;
            bool mask = Math.Abs(p1 - p0) > limitBd || Math.Abs(q1 - q0) > limitBd || Math.Abs(p0 - q0) * 2 + Math.Abs(p1 - q1) / 2 > blimitBd;
            if (filterLen >= 6)
            {
                q2 = p[o + 2 * step]; p2 = p[o - 3 * step];
                mask |= Math.Abs(p2 - p1) > limitBd || Math.Abs(q2 - q1) > limitBd;
            }
            if (filterLen >= 8)
            {
                q3 = p[o + 3 * step]; p3 = p[o - 4 * step];
                mask |= Math.Abs(p3 - p2) > limitBd || Math.Abs(q3 - q2) > limitBd;
            }
            if (mask) return;   // filterMask == 0
            int thresholdBd = 1 << sh;
            bool flat = false, flat2 = false;
            if (filterSize >= 8)
            {
                flat = !(Math.Abs(p1 - p0) > thresholdBd || Math.Abs(q1 - q0) > thresholdBd || Math.Abs(p2 - p0) > thresholdBd || Math.Abs(q2 - q0) > thresholdBd);
                if (filterLen >= 8) flat &= !(Math.Abs(p3 - p0) > thresholdBd || Math.Abs(q3 - q0) > thresholdBd);
            }
            if (filterSize >= 16)
            {
                int q4 = p[o + 4 * step], q5 = p[o + 5 * step], q6 = p[o + 6 * step];
                int p4 = p[o - 5 * step], p5 = p[o - 6 * step], p6 = p[o - 7 * step];
                flat2 = !(Math.Abs(p6 - p0) > thresholdBd || Math.Abs(q6 - q0) > thresholdBd || Math.Abs(p5 - p0) > thresholdBd
                          || Math.Abs(q5 - q0) > thresholdBd || Math.Abs(p4 - p0) > thresholdBd || Math.Abs(q4 - q0) > thresholdBd);
            }
            if (filterSize == 4 || !flat) NarrowFilter(p, o, step, hevMask, bd);
            else if (filterSize == 8 || !flat2) WideFilter(p, o, step, 3, plane);
            else WideFilter(p, o, step, 4, plane);
        }

        static void NarrowFilter(ushort[] p, int o, int step, bool hevMask, int bd)
        {
            int q0 = p[o], q1 = p[o + step], p0 = p[o - step], p1 = p[o - 2 * step];
            int off = 0x80 << (bd - 8);
            int lo = -(1 << (bd - 1)), hi = (1 << (bd - 1)) - 1;
            int ps1 = p1 - off, ps0 = p0 - off, qs0 = q0 - off, qs1 = q1 - off;
            int filter = hevMask ? Math.Clamp(ps1 - qs1, lo, hi) : 0;
            filter = Math.Clamp(filter + 3 * (qs0 - ps0), lo, hi);
            int filter1 = Math.Clamp(filter + 4, lo, hi) >> 3;
            int filter2 = Math.Clamp(filter + 3, lo, hi) >> 3;
            p[o] = (ushort)(Math.Clamp(qs0 - filter1, lo, hi) + off);
            p[o - step] = (ushort)(Math.Clamp(ps0 + filter2, lo, hi) + off);
            if (!hevMask)
            {
                filter = (filter1 + 1) >> 1;
                p[o + step] = (ushort)(Math.Clamp(qs1 - filter, lo, hi) + off);
                p[o - 2 * step] = (ushort)(Math.Clamp(ps1 + filter, lo, hi) + off);
            }
        }

        /// <summary>
        /// The wide filter process (7.14.6.4): each output is the rounded mean of 2^log2Size taps around it (the centre
        /// taps weighted 2, samples beyond the filter clamped to the outermost one), computed as a sliding sum.
        /// </summary>
        static void WideFilter(ushort[] p, int o, int step, int log2Size, int plane)
        {
            int n = log2Size == 4 ? 6 : plane == 0 ? 3 : 2;
            int n2 = log2Size == 3 && plane == 0 ? 0 : 1;
            Span<int> v = stackalloc int[32];   // samples -(n + 1) .. n at v[k + 15], clamped beyond
            int lo = -(n + 1), hi = n;
            for (int k = lo; k <= hi; k++) v[k + 15] = p[o + k * step];
            for (int k = lo - n; k < lo; k++) v[k + 15] = v[lo + 15];
            for (int k = hi + 1; k <= hi + n; k++) v[k + 15] = v[hi + 15];
            int round = 1 << (log2Size - 1);
            // t(i) = sum over j in -n..n of v[i + j] plus the extra weight of |j| <= n2
            int sum = 0;
            for (int j = -n; j <= n; j++) sum += v[-n + j + 15];
            Span<int> F = stackalloc int[12];
            for (int i = -n; i < n; i++)
            {
                int extra = 0;
                for (int j = -n2; j <= n2; j++) extra += v[i + j + 15];
                F[i + 6] = (sum + extra + round) >> log2Size;
                sum += v[i + n + 1 + 15] - v[i - n + 15];
            }
            for (int i = -n; i < n; i++) p[o + i * step] = (ushort)F[i + 6];
        }
    }
}
