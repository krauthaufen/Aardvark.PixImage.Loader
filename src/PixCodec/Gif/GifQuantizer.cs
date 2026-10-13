using System;
using System.Numerics;
using System.Runtime.Intrinsics;

namespace PixCodec
{
    // Pixels inside the encoder are packed as r | g << 8 | b << 16 | a << 24 (a value, not a memory layout),
    // with alpha already thresholded: opaque pixels have a = 255, transparent pixels are exactly 0.

    /// <summary>Collects up to 256 distinct opaque colors and maps them to dense indices (the lossless path).</summary>
    internal sealed unsafe class GifColorSet
    {
        const int Bits = 10, Size = 1 << Bits, Mask = Size - 1;
        const uint Empty = 1;                       // never a pixel: those are 0 or have alpha 255
        /// <summary>What <see cref="MapAll"/> writes for transparent pixels; never a color index while there are any.</summary>
        public const byte TransparentMarker = 255;

        readonly ulong[] _slots = new ulong[Size];  // color | index << 32, open addressing
        public readonly uint[] Colors = new uint[256];
        public int Count;
        public bool Overflow;
        public bool HasTransparent;

        public GifColorSet() => Array.Fill(_slots, (ulong)Empty);

        /// <summary>Adds all opaque colors (until the set overflows) and notes whether any pixel is transparent.</summary>
        public void AddAll(ReadOnlySpan<uint> pixels) => Scan(pixels, default, insert: true, TransparentMarker);

        /// <summary>
        /// <see cref="AddAll"/> and the index of every pixel in one pass. False once more than 256 colors were seen
        /// (the indices are useless then).
        /// </summary>
        public bool MapAll(ReadOnlySpan<uint> pixels, Span<byte> indices)
        {
            Scan(pixels, indices, insert: true, TransparentMarker);
            return !Overflow;
        }

        /// <summary>Indices of pixels whose colors are all in the set.</summary>
        public void MapKnown(ReadOnlySpan<uint> pixels, Span<byte> indices, byte transparent) => Scan(pixels, indices, insert: false, transparent);

        void Scan(ReadOnlySpan<uint> pixels, Span<byte> dst, bool insert, byte transparent)
        {
            bool write = !dst.IsEmpty;
            int n = pixels.Length, i = 0;
            if (Overflow) n = 0;
            fixed (ulong* slots = _slots)
            fixed (uint* s = pixels)
            fixed (byte* d = dst)
            {
                for (; i < n; i++)
                {
                    uint c = s[i];
                    uint h = (c * 0x9E3779B1u) >> (32 - Bits);
                    ulong e = slots[h];
                    if ((uint)e == c)
                    {
                        // the common case: one probe, nothing that depends on the previous pixel
                        if (write) d[i] = (byte)(e >> 32);
                        continue;
                    }
                    if (c == 0)
                    {
                        HasTransparent = true;
                        if (write) d[i] = transparent;
                        continue;
                    }
                    while ((uint)e != Empty && (uint)e != c)
                    {
                        h = (h + 1) & Mask;
                        e = slots[h];
                    }
                    if ((uint)e != c)
                    {
                        if (!insert) e = 0;
                        else
                        {
                            if (Count == 256) { Overflow = true; break; }
                            e = c | (ulong)Count << 32;
                            slots[h] = e;
                            Colors[Count++] = c;
                        }
                    }
                    if (write) d[i] = (byte)(e >> 32);
                }
            }
            if (Overflow && !HasTransparent && i < pixels.Length) HasTransparent = pixels.Slice(i).IndexOf(0u) >= 0;
        }
    }

    /// <summary>
    /// Color quantizer after Xiaolin Wu's variance minimization ("Efficient statistical computations for optimal color
    /// quantization", 1991): a 33^3 grid of cumulative moments makes the statistics of any axis-aligned box an eight term
    /// sum, boxes are split greedily where the split reduces the squared error most. The box means are then polished
    /// with a few k-means passes over the histogram cells.
    /// </summary>
    internal sealed class GifQuantizer
    {
        const int S = 33, N = S * S * S;
        // Per cell: count, sum r, sum g, sum b, sum of r*r + g*g + b*b.
        const int Fields = 5;
        readonly long[] _cells = new long[N * Fields];

        // Pixels are first counted in 24 byte cells (four 32 bit sums and the 64 bit sum of squares next to each
        // other): a pixel touches one cache line, and the cells an image actually uses stay in the L2 cache. The
        // 32 bit sums are folded into _cells before they could overflow.
        const int HotFields = 6;
        const int FoldAfter = 1 << 24;          // 255 * 2^24 < 2^32
        readonly uint[] _hot = new uint[N * HotFields];
        int _hotPixels;

        static int Ix(int r, int g, int b) => (r * S + g) * S + b;

        public unsafe void Add(ReadOnlySpan<uint> pixels)
        {
            while (!pixels.IsEmpty)
            {
                int n = Math.Min(pixels.Length, FoldAfter - _hotPixels);
                fixed (uint* hot = _hot)
                fixed (uint* s = pixels)
                {
                    for (int i = 0; i < n; i++)
                    {
                        uint c = s[i];
                        if (c == 0) continue;
                        uint r = c & 255, g = c >> 8 & 255, b = c >> 16 & 255;
                        uint* cell = hot + (((r >> 3) + 1) * S + (g >> 3) + 1) * S * HotFields + ((b >> 3) + 1) * HotFields;
                        cell[0]++; cell[1] += r; cell[2] += g; cell[3] += b;
                        *(ulong*)(cell + 4) += r * r + g * g + b * b;
                    }
                }
                _hotPixels += n;
                pixels = pixels.Slice(n);
                if (_hotPixels == FoldAfter) Fold();
            }
        }

        /// <summary>Adds every pixel <paramref name="weight"/> times (negative: takes it out again).</summary>
        public unsafe void Add(ReadOnlySpan<uint> pixels, long weight)
        {
            fixed (long* cells = _cells)
            fixed (uint* s = pixels)
            {
                for (int i = 0; i < pixels.Length; i++)
                {
                    uint c = s[i];
                    if (c == 0) continue;
                    uint r = c & 255, g = c >> 8 & 255, b = c >> 16 & 255;
                    long* cell = cells + (((r >> 3) + 1) * S + (g >> 3) + 1) * S * Fields + ((b >> 3) + 1) * Fields;
                    cell[0] += weight; cell[1] += r * weight; cell[2] += g * weight; cell[3] += b * weight;
                    cell[4] += (r * r + g * g + b * b) * weight;
                }
            }
        }

        unsafe void Fold()
        {
            if (_hotPixels == 0) return;
            fixed (uint* hot = _hot)
            fixed (long* cells = _cells)
            {
                for (int i = 0; i < N; i++)
                {
                    uint* h = hot + i * HotFields;
                    if (h[0] == 0) continue;
                    long* c = cells + i * Fields;
                    c[0] += h[0]; c[1] += h[1]; c[2] += h[2]; c[3] += h[3];
                    c[4] += (long)*(ulong*)(h + 4);
                }
            }
            Array.Clear(_hot);
            _hotPixels = 0;
        }

        struct Box
        {
            public int R0, R1, G0, G1, B0, B1;      // low bounds exclusive, high bounds inclusive
            public double Variance;
        }

        static long Vol(in Box c, long[] m)
            => m[Ix(c.R1, c.G1, c.B1)] - m[Ix(c.R1, c.G1, c.B0)] - m[Ix(c.R1, c.G0, c.B1)] + m[Ix(c.R1, c.G0, c.B0)]
             - m[Ix(c.R0, c.G1, c.B1)] + m[Ix(c.R0, c.G1, c.B0)] + m[Ix(c.R0, c.G0, c.B1)] - m[Ix(c.R0, c.G0, c.B0)];

        static double Vol(in Box c, double[] m)
            => m[Ix(c.R1, c.G1, c.B1)] - m[Ix(c.R1, c.G1, c.B0)] - m[Ix(c.R1, c.G0, c.B1)] + m[Ix(c.R1, c.G0, c.B0)]
             - m[Ix(c.R0, c.G1, c.B1)] + m[Ix(c.R0, c.G1, c.B0)] + m[Ix(c.R0, c.G0, c.B1)] - m[Ix(c.R0, c.G0, c.B0)];

        /// <summary>Returns up to <paramref name="maxColors"/> packed opaque colors.</summary>
        public uint[] BuildPalette(int maxColors, int refineIterations)
        {
            // Keep the per cell statistics for the k-means passes, then turn the grids into 3D prefix sums.
            Fold();
            long[] cw = new long[N], cr = new long[N], cg = new long[N], cb = new long[N];
            double[] m2 = new double[N];
            for (int i = 0; i < N; i++)
            {
                cw[i] = _cells[i * Fields]; cr[i] = _cells[i * Fields + 1]; cg[i] = _cells[i * Fields + 2]; cb[i] = _cells[i * Fields + 3];
                m2[i] = _cells[i * Fields + 4];
            }
            long[] w = (long[])cw.Clone(), mr = (long[])cr.Clone(), mg = (long[])cg.Clone(), mb = (long[])cb.Clone();
            for (int r = 1; r < S; r++)
                for (int g = 1; g < S; g++)
                    for (int b = 1; b < S; b++)
                    {
                        int i = Ix(r, g, b);
                        int i1 = Ix(r - 1, g, b), i2 = Ix(r, g - 1, b), i3 = Ix(r, g, b - 1);
                        int i12 = Ix(r - 1, g - 1, b), i13 = Ix(r - 1, g, b - 1), i23 = Ix(r, g - 1, b - 1), i123 = Ix(r - 1, g - 1, b - 1);
                        w[i] += w[i1] + w[i2] + w[i3] - w[i12] - w[i13] - w[i23] + w[i123];
                        mr[i] += mr[i1] + mr[i2] + mr[i3] - mr[i12] - mr[i13] - mr[i23] + mr[i123];
                        mg[i] += mg[i1] + mg[i2] + mg[i3] - mg[i12] - mg[i13] - mg[i23] + mg[i123];
                        mb[i] += mb[i1] + mb[i2] + mb[i3] - mb[i12] - mb[i13] - mb[i23] + mb[i123];
                        m2[i] += m2[i1] + m2[i2] + m2[i3] - m2[i12] - m2[i13] - m2[i23] + m2[i123];
                    }

            double Variance(in Box c)
            {
                long bw = Vol(c, w);
                if (bw == 0) return 0;
                double dr = Vol(c, mr), dg = Vol(c, mg), db = Vol(c, mb);
                return Math.Max(0, Vol(c, m2) - (dr * dr + dg * dg + db * db) / bw);
            }

            // Splits the box where the two halves have the largest sum of |sum|^2 / weight, i.e. the least total variance.
            bool Split(ref Box box, out Box upper)
            {
                upper = box;
                long tw = Vol(box, w);
                double tr = Vol(box, mr), tg = Vol(box, mg), tb = Vol(box, mb);
                double best = 0;
                int bestAxis = -1, bestCut = 0;
                for (int axis = 0; axis < 3; axis++)
                {
                    int lo = axis == 0 ? box.R0 : axis == 1 ? box.G0 : box.B0;
                    int hi = axis == 0 ? box.R1 : axis == 1 ? box.G1 : box.B1;
                    Box half = box;
                    for (int cut = lo + 1; cut < hi; cut++)
                    {
                        if (axis == 0) half.R1 = cut; else if (axis == 1) half.G1 = cut; else half.B1 = cut;
                        long hw = Vol(half, w);
                        if (hw == 0 || hw == tw) continue;
                        double hr = Vol(half, mr), hg = Vol(half, mg), hb = Vol(half, mb);
                        double score = (hr * hr + hg * hg + hb * hb) / hw
                                     + ((tr - hr) * (tr - hr) + (tg - hg) * (tg - hg) + (tb - hb) * (tb - hb)) / (tw - hw);
                        if (score > best) { best = score; bestAxis = axis; bestCut = cut; }
                    }
                }
                if (bestAxis < 0) return false;
                if (bestAxis == 0) { box.R1 = bestCut; upper.R0 = bestCut; }
                else if (bestAxis == 1) { box.G1 = bestCut; upper.G0 = bestCut; }
                else { box.B1 = bestCut; upper.B0 = bestCut; }
                return true;
            }

            var boxes = new Box[Math.Max(1, maxColors)];
            boxes[0] = new Box { R1 = S - 1, G1 = S - 1, B1 = S - 1 };
            boxes[0].Variance = Variance(boxes[0]);
            int count = 1;
            while (count < maxColors)
            {
                int pick = -1;
                double v = 0;
                for (int i = 0; i < count; i++)
                    if (boxes[i].Variance > v) { v = boxes[i].Variance; pick = i; }
                if (pick < 0) break;
                if (Split(ref boxes[pick], out Box upper))
                {
                    boxes[pick].Variance = Variance(boxes[pick]);
                    upper.Variance = Variance(upper);
                    boxes[count++] = upper;
                }
                else boxes[pick].Variance = 0;
            }

            var pr = new double[count]; var pg = new double[count]; var pb = new double[count];
            int k = 0;
            for (int i = 0; i < count; i++)
            {
                long bw = Vol(boxes[i], w);
                if (bw == 0) continue;
                pr[k] = (double)Vol(boxes[i], mr) / bw; pg[k] = (double)Vol(boxes[i], mg) / bw; pb[k] = (double)Vol(boxes[i], mb) / bw;
                k++;
            }
            count = k;
            if (count == 0) return new uint[] { 0xFF000000u };

            if (refineIterations > 0 && count > 1) Refine(cw, cr, cg, cb, pr, pg, pb, count, refineIterations);

            var palette = new uint[count];
            for (int i = 0; i < count; i++)
                palette[i] = (uint)Math.Clamp((int)(pr[i] + 0.5), 0, 255) | (uint)Math.Clamp((int)(pg[i] + 0.5), 0, 255) << 8
                           | (uint)Math.Clamp((int)(pb[i] + 0.5), 0, 255) << 16 | 0xFF000000u;
            return palette;
        }

        /// <summary>Lloyd iterations on the occupied histogram cells (each cell acts as one weighted point at its mean).</summary>
        static unsafe void Refine(long[] cw, long[] cr, long[] cg, long[] cb, double[] pr, double[] pg, double[] pb, int count, int iterations)
        {
            int cells = 0;
            for (int i = 0; i < N; i++) if (cw[i] != 0) cells++;
            var ix = new int[cells];
            var xr = new float[cells]; var xg = new float[cells]; var xb = new float[cells];
            cells = 0;
            for (int i = 0; i < N; i++)
                if (cw[i] != 0)
                {
                    ix[cells] = i;
                    xr[cells] = (float)cr[i] / cw[i]; xg[cells] = (float)cg[i] / cw[i]; xb[cells] = (float)cb[i] / cw[i];
                    cells++;
                }

            int vc = Vector<float>.Count;
            int padded = (count + vc - 1) / vc * vc;
            var qr = new float[padded]; var qg = new float[padded]; var qb = new float[padded];
            var sw = new long[count]; var sr = new long[count]; var sg = new long[count]; var sb = new long[count];
            Span<float> lane = stackalloc float[vc];
            for (int j = 0; j < vc; j++) lane[j] = j;
            var iota = new Vector<float>(lane);
            float* laneD = stackalloc float[vc];
            float* laneI = stackalloc float[vc];
            fixed (float* fr = qr, fg = qg, fb = qb)
            for (int it = 0; it < iterations; it++)
            {
                qr.AsSpan().Fill(1e6f); qg.AsSpan().Fill(1e6f); qb.AsSpan().Fill(1e6f);
                for (int j = 0; j < count; j++) { qr[j] = (float)pr[j]; qg[j] = (float)pg[j]; qb[j] = (float)pb[j]; }
                Array.Clear(sw); Array.Clear(sr); Array.Clear(sg); Array.Clear(sb);
                for (int c = 0; c < cells; c++)
                {
                    float r = xr[c], g = xg[c], b = xb[c];
                    int bj = 0;
                    if (Vector.IsHardwareAccelerated)
                    {
                        var vr = new Vector<float>(r); var vg = new Vector<float>(g); var vb = new Vector<float>(b);
                        var best = new Vector<float>(float.MaxValue);
                        var bestIndex = Vector<float>.Zero;
                        var index = iota;
                        var step = new Vector<float>(vc);
                        for (int j = 0; j < padded; j += vc)
                        {
                            var dr = vr - Vector.Load(fr + j); var dg = vg - Vector.Load(fg + j); var db = vb - Vector.Load(fb + j);
                            var d = dr * dr + dg * dg + db * db;
                            var less = Vector.LessThan(d, best);
                            best = Vector.ConditionalSelect(less, d, best);
                            bestIndex = Vector.ConditionalSelect(less, index, bestIndex);
                            index += step;
                        }
                        // smaller distance, then smaller index: independent of the vector width
                        best.Store(laneD); bestIndex.Store(laneI);
                        float rd = laneD[0], ri = laneI[0];
                        for (int k = 1; k < vc; k++)
                            if (laneD[k] < rd || (laneD[k] == rd && laneI[k] < ri)) { rd = laneD[k]; ri = laneI[k]; }
                        bj = (int)ri;
                    }
                    else
                    {
                        float best = float.MaxValue;
                        for (int j = 0; j < count; j++)
                        {
                            float dr = r - qr[j], dg = g - qg[j], db = b - qb[j];
                            float d = dr * dr + dg * dg + db * db;
                            if (d < best) { best = d; bj = j; }
                        }
                    }
                    int i = ix[c];
                    sw[bj] += cw[i]; sr[bj] += cr[i]; sg[bj] += cg[i]; sb[bj] += cb[i];
                }
                for (int j = 0; j < count; j++)
                    if (sw[j] != 0) { pr[j] = (double)sr[j] / sw[j]; pg[j] = (double)sg[j] / sw[j]; pb[j] = (double)sb[j] / sw[j]; }
            }
        }
    }

    /// <summary>
    /// A finished palette plus the machinery to turn pixels into indices: an exact hash lookup when the palette holds
    /// every color of the source, otherwise a lazily filled 6-6-6 bit inverse table (nearest entry to the cell center),
    /// optionally with Floyd-Steinberg error diffusion.
    /// </summary>
    internal sealed unsafe class GifPalette
    {
        public readonly uint[] Colors;          // opaque entries; the transparent slot, if any, follows them
        public readonly int ColorCount;
        public readonly int TransparentIndex;   // -1 if none
        public int Size => ColorCount + (TransparentIndex >= 0 ? 1 : 0);

        readonly GifColorSet? _exact;
        public bool IsExact => _exact != null;
        ushort[]? _inverse;                     // index + 1, 0 = not computed yet
        readonly int[] _pr, _pg, _pb;       // padded to whole vectors
        readonly int[] _rgb;                // r, g, b, 255 per color

        public GifPalette(uint[] colors, int count, bool transparentSlot, GifColorSet? exact)
        {
            Colors = colors; ColorCount = count;
            TransparentIndex = transparentSlot ? count : -1;
            _exact = exact;
            // Padded to whole vectors with a color far away from everything.
            int padded = (count + Vector<int>.Count - 1) / Vector<int>.Count * Vector<int>.Count;
            _pr = new int[padded]; _pg = new int[padded]; _pb = new int[padded];
            _pr.AsSpan().Fill(1 << 12); _pg.AsSpan().Fill(1 << 12); _pb.AsSpan().Fill(1 << 12);
            _rgb = new int[Math.Max(1, count) * 4];
            for (int i = 0; i < count; i++)
            {
                _rgb[i * 4] = _pr[i] = (int)(colors[i] & 255);
                _rgb[i * 4 + 1] = _pg[i] = (int)(colors[i] >> 8 & 255);
                _rgb[i * 4 + 2] = _pb[i] = (int)(colors[i] >> 16 & 255);
                _rgb[i * 4 + 3] = 255;
            }
        }

        // The 6-6-6 inverse table is filled one 16 x 16 x 16 block of the color cube (4 x 4 x 4 cells) at a time, when
        // a pixel first lands in it. Only palette entries that can be the nearest one for SOME point of the block are
        // looked at (their closest approach to the block is not farther than the farthest corner from the best entry);
        // the result is identical to a full search: nearest by squared RGB distance, lowest index on ties.
        int[]? _approach;

        void FillBlock(ushort* inverse, int* pal, int block)
        {
            int[] pr = _pr, pg = _pg, pb = _pb;
            int[] approach = _approach ??= new int[pr.Length];
            int r0 = (block >> 8) << 4, g0 = (block >> 4 & 15) << 4, b0 = (block & 15) << 4;
            int reach = int.MaxValue;
            if (Vector.IsHardwareAccelerated)
            {
                // per channel: distance of the palette value to the block's center c, |p - c|; the block spans
                // c - 7.5 .. c + 7.5, so in doubled units far = 2|p - c| + 15 and near = max(0, 2|p - c| - 15)
                var cr = new Vector<int>(2 * r0 + 15); var cg = new Vector<int>(2 * g0 + 15); var cb = new Vector<int>(2 * b0 + 15);
                var half = new Vector<int>(15);
                var vreach = new Vector<int>(int.MaxValue);
                for (int i = 0; i < pr.Length; i += Vector<int>.Count)
                {
                    var dr = Vector.Abs(new Vector<int>(pr, i) * 2 - cr);
                    var dg = Vector.Abs(new Vector<int>(pg, i) * 2 - cg);
                    var db = Vector.Abs(new Vector<int>(pb, i) * 2 - cb);
                    var fr = dr + half; var fg = dg + half; var fb = db + half;
                    vreach = Vector.Min(vreach, fr * fr + fg * fg + fb * fb);
                    var nr = Vector.Max(dr - half, Vector<int>.Zero); var ng = Vector.Max(dg - half, Vector<int>.Zero); var nb = Vector.Max(db - half, Vector<int>.Zero);
                    (nr * nr + ng * ng + nb * nb).CopyTo(approach, i);
                }
                for (int k = 0; k < Vector<int>.Count; k++) reach = Math.Min(reach, vreach[k]);
            }
            else
            {
                for (int i = 0; i < ColorCount; i++)
                {
                    int dr = Math.Abs(pr[i] * 2 - (2 * r0 + 15)), dg = Math.Abs(pg[i] * 2 - (2 * g0 + 15)), db = Math.Abs(pb[i] * 2 - (2 * b0 + 15));
                    reach = Math.Min(reach, (dr + 15) * (dr + 15) + (dg + 15) * (dg + 15) + (db + 15) * (db + 15));
                    int nr = Math.Max(dr - 15, 0), ng = Math.Max(dg - 15, 0), nb = Math.Max(db - 15, 0);
                    approach[i] = nr * nr + ng * ng + nb * nb;
                }
            }

            // All 64 cell centers against every candidate, in palette order.
            int* best = stackalloc int[64];
            int* bestIndex = stackalloc int[64];
            for (int k = 0; k < 64; k++) best[k] = int.MaxValue;
            int* dg2 = stackalloc int[4];
            int* db2 = stackalloc int[4];
            for (int i = 0; i < ColorCount; i++)
            {
                if (approach[i] > reach) continue;
                int* p = pal + i * 4;
                for (int k = 0; k < 4; k++)
                {
                    int dg = g0 + k * 4 + 2 - p[1], db = b0 + k * 4 + 2 - p[2];
                    dg2[k] = dg * dg; db2[k] = db * db;
                }
                for (int ri = 0, cell = 0; ri < 4; ri++)
                {
                    int dr = r0 + ri * 4 + 2 - p[0];
                    int dr2 = dr * dr;
                    for (int gi = 0; gi < 4; gi++)
                    {
                        int drg = dr2 + dg2[gi];
                        for (int bi = 0; bi < 4; bi++, cell++)
                        {
                            int d = drg + db2[bi];
                            if (d < best[cell]) { best[cell] = d; bestIndex[cell] = i; }
                        }
                    }
                }
            }
            int baseKey = (r0 >> 2) << 12 | (g0 >> 2) << 6 | b0 >> 2;
            for (int ri = 0, cell = 0; ri < 4; ri++)
                for (int gi = 0; gi < 4; gi++)
                    for (int bi = 0; bi < 4; bi++, cell++)
                        inverse[baseKey + (ri << 12) + (gi << 6) + bi] = (ushort)(bestIndex[cell] + 1);
        }

        /// <summary>An inverse table cell that has not been computed yet; pal is the pinned <see cref="_rgb"/>.</summary>
        int Fill(ushort* inverse, int* pal, int key)
        {
            FillBlock(inverse, pal, (key >> 14) << 8 | (key >> 8 & 15) << 4 | (key >> 2 & 15));
            return inverse[key];
        }

        /// <summary>Maps a w x h block of pixels (transparent ones go to the transparent slot) to palette indices.</summary>
        public void Map(ReadOnlySpan<uint> src, Span<byte> dst, int w, int h, GifDither dither)
        {
            byte t = (byte)Math.Max(0, TransparentIndex);
            if (_exact != null)
            {
                _exact.MapKnown(src.Slice(0, w * h), dst.Slice(0, w * h), t);
                return;
            }

            _inverse ??= new ushort[1 << 18];
            if (ColorCount == 0) { dst.Slice(0, w * h).Fill(t); return; }
            fixed (ushort* inverse = _inverse)
            fixed (int* pal = _rgb)
            fixed (uint* s = src)
            fixed (byte* d = dst)
            {
                if (dither == GifDither.None)
                {
                    int n = w * h;
                    for (int i = 0; i < n; i++)
                    {
                        uint c = s[i];
                        if (c == 0) { d[i] = t; continue; }
                        int key = (int)((c & 0xFC) << 10 | (c >> 4 & 0xFC0) | (c >> 18 & 0x3F));
                        int v = inverse[key];
                        if (v == 0) v = Fill(inverse, pal, key);
                        d[i] = (byte)(v - 1);
                    }
                    return;
                }

                // Floyd-Steinberg, every row left to right. The error of a pixel depends on the pixel before it through
                // the palette lookup, a chain the CPU cannot shorten - so several rows are worked on at once, each two
                // pixels behind the row above (which is all the distance the error terms need). The result is the
                // same as going row by row. Errors are kept in 1/16 units, one vector (r, g, b, 0) per pixel with a
                // pixel of padding on both sides; the 7/16 for the next pixel stays out of the buffers.
                const int Rows = 4;
                int stride = (w + 2) * 4;
                var errors = new int[(Rows + 1) * stride];
                var eight = Vector128.Create(8);
                var max = Vector128.Create(255);
                Vector128<int>* carry = stackalloc Vector128<int>[Rows];
                fixed (int* err = errors)
                {
                    for (int y0 = 0; y0 < h; y0 += Rows)
                    {
                        int rows = Math.Min(Rows, h - y0);
                        for (int r = 0; r < rows; r++) carry[r] = Vector128<int>.Zero;
                        for (int j = 0, end = w + 2 * (rows - 1); j < end; j++)
                        {
                            for (int r = 0; r < rows; r++)
                            {
                                int x = j - 2 * r;
                                if ((uint)x >= (uint)w) continue;
                                long at = (long)(y0 + r) * w + x;
                                uint c = s[at];
                                if (c == 0) { d[at] = t; carry[r] = Vector128<int>.Zero; continue; }
                                int* mine = err + r * stride + (x + 1) * 4;
                                // r, g, b, 255: the fourth lane rides along and always ends up with error 0
                                var v = Vector128.WidenLower(Vector128.WidenLower(Vector128.CreateScalarUnsafe(c).AsByte())).AsInt32()
                                      + Vector128.ShiftRightArithmetic(Vector128.Load(mine) + carry[r] + eight, 4);
                                v = Vector128.Min(Vector128.Max(v, Vector128<int>.Zero), max);
                                int key = (v.GetElement(0) >> 2) << 12 | (v.GetElement(1) >> 2) << 6 | v.GetElement(2) >> 2;
                                int i = inverse[key];
                                if (i == 0) i = Fill(inverse, pal, key);
                                i--;
                                d[at] = (byte)i;
                                var e = v - Vector128.Load(pal + i * 4);
                                carry[r] = Vector128.ShiftLeft(e, 3) - e;                       // 7/16 to the right
                                int* below = mine + stride;
                                (Vector128.Load(below - 4) + Vector128.ShiftLeft(e, 1) + e).Store(below - 4);   // 3/16
                                (Vector128.Load(below) + Vector128.ShiftLeft(e, 2) + e).Store(below);           // 5/16
                                (Vector128.Load(below + 4) + e).Store(below + 4);                               // 1/16
                            }
                        }
                        // what the last row passed down belongs to the first row of the next band
                        new Span<int>(err + rows * stride, stride).CopyTo(new Span<int>(err, stride));
                        new Span<int>(err + stride, Rows * stride).Clear();
                    }
                }
            }
        }
    }
}
