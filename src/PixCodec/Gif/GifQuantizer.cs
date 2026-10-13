using System;
using System.Numerics;

namespace PixCodec
{
    // Pixels inside the encoder are packed as r | g << 8 | b << 16 | a << 24 (a value, not a memory layout),
    // with alpha already thresholded: opaque pixels have a = 255, transparent pixels are exactly 0.

    /// <summary>Collects up to 256 distinct opaque colors and maps them to dense indices (the lossless path).</summary>
    internal sealed class GifColorSet
    {
        const int Size = 1024, Mask = Size - 1;
        readonly uint[] _keys = new uint[Size];     // 0 = empty; opaque pixels are never 0
        readonly short[] _index = new short[Size];
        public readonly uint[] Colors = new uint[256];
        public int Count;
        public bool Overflow;
        public bool HasTransparent;

        static int Hash(uint c) => (int)((c * 0x9E3779B1u) >> 22);

        /// <summary>Index of the color, adding it if new; -1 once more than 256 colors were seen.</summary>
        public int Add(uint c)
        {
            int h = Hash(c);
            while (true)
            {
                uint k = _keys[h];
                if (k == c) return _index[h];
                if (k == 0) break;
                h = (h + 1) & Mask;
            }
            if (Count == 256) { Overflow = true; return -1; }
            _keys[h] = c;
            _index[h] = (short)Count;
            Colors[Count] = c;
            return Count++;
        }

        public int Find(uint c)
        {
            int h = Hash(c);
            while (true)
            {
                uint k = _keys[h];
                if (k == c) return _index[h];
                if (k == 0) return -1;
                h = (h + 1) & Mask;
            }
        }

        /// <summary>Adds all opaque colors (until the set overflows) and notes whether any pixel is transparent.</summary>
        public void AddAll(ReadOnlySpan<uint> pixels)
        {
            uint last = 0;
            int i = 0;
            for (; i < pixels.Length && !Overflow; i++)
            {
                uint c = pixels[i];
                if (c == 0) { HasTransparent = true; continue; }
                if (c == last) continue;
                last = c;
                Add(c);
            }
            if (!HasTransparent && i < pixels.Length) HasTransparent = pixels.Slice(i).IndexOf(0u) >= 0;
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
        readonly long[] _w = new long[N], _r = new long[N], _g = new long[N], _b = new long[N];
        readonly double[] _m2 = new double[N];

        static int Ix(int r, int g, int b) => (r * S + g) * S + b;

        public void Add(ReadOnlySpan<uint> pixels)
        {
            long[] w = _w, mr = _r, mg = _g, mb = _b;
            double[] m2 = _m2;
            for (int i = 0; i < pixels.Length; i++)
            {
                uint c = pixels[i];
                if (c == 0) continue;
                int r = (int)(c & 255), g = (int)(c >> 8 & 255), b = (int)(c >> 16 & 255);
                int ix = Ix((r >> 3) + 1, (g >> 3) + 1, (b >> 3) + 1);
                w[ix]++; mr[ix] += r; mg[ix] += g; mb[ix] += b;
                m2[ix] += r * r + g * g + b * b;
            }
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
            long[] cw = (long[])_w.Clone(), cr = (long[])_r.Clone(), cg = (long[])_g.Clone(), cb = (long[])_b.Clone();
            long[] w = (long[])_w.Clone(), mr = (long[])_r.Clone(), mg = (long[])_g.Clone(), mb = (long[])_b.Clone();
            double[] m2 = (double[])_m2.Clone();
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
        static void Refine(long[] cw, long[] cr, long[] cg, long[] cb, double[] pr, double[] pg, double[] pb, int count, int iterations)
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
                            var dr = vr - new Vector<float>(qr, j); var dg = vg - new Vector<float>(qg, j); var db = vb - new Vector<float>(qb, j);
                            var d = dr * dr + dg * dg + db * db;
                            var less = Vector.LessThan(d, best);
                            best = Vector.ConditionalSelect(less, d, best);
                            bestIndex = Vector.ConditionalSelect(less, index, bestIndex);
                            index += step;
                        }
                        // smaller distance, then smaller index: independent of the vector width
                        float rd = best[0], ri = bestIndex[0];
                        for (int k = 1; k < vc; k++)
                            if (best[k] < rd || (best[k] == rd && bestIndex[k] < ri)) { rd = best[k]; ri = bestIndex[k]; }
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
    internal sealed class GifPalette
    {
        public readonly uint[] Colors;          // opaque entries; the transparent slot, if any, follows them
        public readonly int ColorCount;
        public readonly int TransparentIndex;   // -1 if none
        public int Size => ColorCount + (TransparentIndex >= 0 ? 1 : 0);

        readonly GifColorSet? _exact;
        ushort[]? _inverse;                     // index + 1, 0 = not computed yet
        readonly int[] _pr, _pg, _pb;

        public GifPalette(uint[] colors, int count, bool transparentSlot, GifColorSet? exact)
        {
            Colors = colors; ColorCount = count;
            TransparentIndex = transparentSlot ? count : -1;
            _exact = exact;
            // Padded to whole vectors with a color far away from everything.
            int padded = (count + Vector<int>.Count - 1) / Vector<int>.Count * Vector<int>.Count;
            _pr = new int[padded]; _pg = new int[padded]; _pb = new int[padded];
            _pr.AsSpan().Fill(1 << 12); _pg.AsSpan().Fill(1 << 12); _pb.AsSpan().Fill(1 << 12);
            for (int i = 0; i < count; i++)
            {
                _pr[i] = (int)(colors[i] & 255); _pg[i] = (int)(colors[i] >> 8 & 255); _pb[i] = (int)(colors[i] >> 16 & 255);
            }
        }

        /// <summary>Index of the closest palette entry (squared RGB distance), the lowest index on ties.</summary>
        int Nearest(int r, int g, int b)
        {
            int[] pr = _pr, pg = _pg, pb = _pb;
            if (!Vector.IsHardwareAccelerated)
            {
                int bestD = int.MaxValue, bi = 0;
                for (int i = 0; i < ColorCount; i++)
                {
                    int dr = r - pr[i], dg = g - pg[i], db = b - pb[i];
                    int d = dr * dr + dg * dg + db * db;
                    if (d < bestD) { bestD = d; bi = i; }
                }
                return bi;
            }
            var vr = new Vector<int>(r); var vg = new Vector<int>(g); var vb = new Vector<int>(b);
            var best = new Vector<int>(int.MaxValue);
            var bestIndex = Vector<int>.Zero;
            var index = Iota;
            var step = new Vector<int>(Vector<int>.Count);
            for (int i = 0; i < pr.Length; i += Vector<int>.Count)
            {
                var dr = vr - new Vector<int>(pr, i); var dg = vg - new Vector<int>(pg, i); var db = vb - new Vector<int>(pb, i);
                var d = dr * dr + dg * dg + db * db;
                var less = Vector.LessThan(d, best);
                best = Vector.ConditionalSelect(less, d, best);
                bestIndex = Vector.ConditionalSelect(less, index, bestIndex);
                index += step;
            }
            // Reducing with "smaller distance, then smaller index" gives the same answer for every vector width.
            int rd = best[0], ri = bestIndex[0];
            for (int k = 1; k < Vector<int>.Count; k++)
                if (best[k] < rd || (best[k] == rd && bestIndex[k] < ri)) { rd = best[k]; ri = bestIndex[k]; }
            return ri;
        }

        static readonly Vector<int> Iota = MakeIota();

        static Vector<int> MakeIota()
        {
            Span<int> v = stackalloc int[Vector<int>.Count];
            for (int i = 0; i < v.Length; i++) v[i] = i;
            return new Vector<int>(v);
        }

        int Lookup(int r, int g, int b)
        {
            int key = (r >> 2) << 12 | (g >> 2) << 6 | (b >> 2);
            int v = _inverse![key];
            if (v == 0)
            {
                v = Nearest((r & ~3) + 2, (g & ~3) + 2, (b & ~3) + 2) + 1;
                _inverse[key] = (ushort)v;
            }
            return v - 1;
        }

        /// <summary>Maps a w x h block of pixels (transparent ones go to the transparent slot) to palette indices.</summary>
        public void Map(ReadOnlySpan<uint> src, Span<byte> dst, int w, int h, GifDither dither)
        {
            byte t = (byte)Math.Max(0, TransparentIndex);
            if (_exact != null)
            {
                uint last = 0;
                byte lastIndex = t;
                for (int i = 0; i < src.Length; i++)
                {
                    uint c = src[i];
                    if (c != last)
                    {
                        last = c;
                        lastIndex = c == 0 ? t : (byte)_exact.Find(c);
                    }
                    dst[i] = lastIndex;
                }
                return;
            }

            _inverse ??= new ushort[1 << 18];
            if (dither == GifDither.None)
            {
                uint last = 0;
                byte lastIndex = t;
                for (int i = 0; i < src.Length; i++)
                {
                    uint c = src[i];
                    if (c != last)
                    {
                        last = c;
                        lastIndex = c == 0 ? t : (byte)Lookup((int)(c & 255), (int)(c >> 8 & 255), (int)(c >> 16 & 255));
                    }
                    dst[i] = lastIndex;
                }
                return;
            }

            // Floyd-Steinberg, alternating direction per row. Errors are kept in 1/16 units, three per pixel,
            // with one pixel of padding on both sides.
            var cur = new int[(w + 2) * 3];
            var next = new int[(w + 2) * 3];
            for (int y = 0; y < h; y++)
            {
                bool ltr = (y & 1) == 0;
                int dir = ltr ? 1 : -1;
                int x = ltr ? 0 : w - 1;
                for (int n = 0; n < w; n++, x += dir)
                {
                    uint c = src[y * w + x];
                    if (c == 0) { dst[y * w + x] = t; continue; }
                    int e = (x + 1) * 3;
                    int r = Math.Clamp((int)(c & 255) + ((cur[e] + 8) >> 4), 0, 255);
                    int g = Math.Clamp((int)(c >> 8 & 255) + ((cur[e + 1] + 8) >> 4), 0, 255);
                    int b = Math.Clamp((int)(c >> 16 & 255) + ((cur[e + 2] + 8) >> 4), 0, 255);
                    int i = Lookup(r, g, b);
                    dst[y * w + x] = (byte)i;
                    int er = r - _pr[i], eg = g - _pg[i], eb = b - _pb[i];
                    int ahead = e + dir * 3, behind = e - dir * 3;
                    cur[ahead] += er * 7; cur[ahead + 1] += eg * 7; cur[ahead + 2] += eb * 7;
                    next[behind] += er * 3; next[behind + 1] += eg * 3; next[behind + 2] += eb * 3;
                    next[e] += er * 5; next[e + 1] += eg * 5; next[e + 2] += eb * 5;
                    next[ahead] += er; next[ahead + 1] += eg; next[ahead + 2] += eb;
                }
                (cur, next) = (next, cur);
                Array.Clear(next);
            }
        }
    }
}
