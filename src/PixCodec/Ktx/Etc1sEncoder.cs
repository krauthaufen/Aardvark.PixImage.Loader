using System;
using System.Collections.Generic;
using System.Runtime.Intrinsics;
using System.Threading.Tasks;

namespace PixCodec
{
    /// <summary>
    /// ETC1S encoding with BasisLZ supercompression (KTX 2.0 Appendix C "BasisLZ Global Data" and Appendix D
    /// "BasisLZ/ETC1S Bitstream Specification", the public domain ".basis File Format and ETC1S Texture Video
    /// Specification"), original code.
    /// <para>
    /// Every image of the texture (levels, layers, faces, alpha slices) shares one endpoint and one selector codebook.
    /// Blocks are first fitted on their own; the endpoint codebook is built by splitting the block endpoints top down
    /// (principal axis, largest squared error first) and refined by alternating exact cluster fits and reassignment among
    /// the neighbouring clusters of the split tree; the selector codebook likewise, its entries chosen per texel for the
    /// least total error of their member blocks. A final raster pass picks each block's codes with a rate term
    /// (neighbour endpoints, recently used selectors) and the indices are Huffman coded as the bitstream defines.
    /// Every step is O(blocks * log(codebook size)) or O(blocks * candidates), so 4K textures stay fast.
    /// </para>
    /// </summary>
    internal static class Etc1sEncoder
    {
        public readonly struct Image
        {
            public readonly byte[] Rgba;
            public readonly int Width, Height, Level;
            public Image(byte[] rgba, int width, int height, int level) { Rgba = rgba; Width = width; Height = height; Level = level; }
        }

        public sealed class Params
        {
            /// <summary>1 to 255 like basisu's -q: codebook sizes and the rate term.</summary>
            public int Quality = 128;
            public int? MaxEndpoints, MaxSelectors;
            /// <summary>0 fast, 1 normal, 2 high: refinement passes and search widths.</summary>
            public int Effort = 1;
            public int MaxDegreeOfParallelism;
        }

        const int MaxCodebook = 16128;
        // rate term: lambda * (1 - quality)^2 squared error units per estimated bit
        const double LambdaScale = 400;
        // endpoint clusters tighter than this (per block, feature units squared, times (1 - quality)^2) are not split further
        const double EndpointMinSse = 40;
        // a block's selector search is skipped when its entry is within lb / 8 + 48 of the ideal selectors' error
        const int SkipShift = 3, SkipAbs = 48;
        // best bin first leaves searched in the selector tree when the selector codebook is too large to scan
        const int SearchBudget = 32;
        const int HistorySize = 64;

        static readonly int[] IntenA = { 2, 5, 9, 13, 18, 24, 33, 47 };
        static readonly int[] IntenB = { 8, 17, 29, 42, 60, 80, 106, 183 };
        // modifier of selector s (0 = -b, 1 = -a, 2 = +a, 3 = +b) in intensity table t, at t * 4 + s
        static readonly int[] Mod = BuildMod();

        static int[] BuildMod()
        {
            var m = new int[32];
            for (int t = 0; t < 8; t++) { m[t * 4] = -IntenB[t]; m[t * 4 + 1] = -IntenA[t]; m[t * 4 + 2] = IntenA[t]; m[t * 4 + 3] = IntenB[t]; }
            return m;
        }

        static int Ext5(int v) => v << 3 | v >> 2;
        static int Clamp255(int v) => v < 0 ? 0 : v > 255 ? 255 : v;

        // endpoint: R, G, B (5 bits each) and the intensity table
        static int Pack(int r, int g, int b, int t) => r | g << 5 | b << 10 | t << 15;
        static int R(int e) => e & 31;
        static int G(int e) => e >> 5 & 31;
        static int B(int e) => e >> 10 & 31;
        static int T(int e) => e >> 15 & 7;

        sealed class Slice
        {
            public int Image, Bx, By, First;
            public bool Alpha;
            public int Count => Bx * By;
        }

        /// <summary>Encoder state: the blocks of every slice and their codes.</summary>
        sealed class State
        {
            public int N;                       // blocks
            public byte[] Px = Array.Empty<byte>();   // 16 texels * RGB per block (alpha slices: A, A, A)
            public bool[] AlphaBlock = Array.Empty<bool>();
            public int[] Ep = Array.Empty<int>();     // block -> endpoint cluster
            public int[] MeanCell = Array.Empty<int>(); // block -> its mean colour, 5 bits per component
            public int[] Sel = Array.Empty<int>();    // block -> selector cluster
            public int[] Endpoints = Array.Empty<int>();
            public uint[] Selectors = Array.Empty<uint>();
            public ParallelOptions Par = new();
            public bool Gray;
        }

        // ------------------------------------------------------------------ entry point

        /// <summary>
        /// Encodes the images (in BasisLZ image descriptor order: level, layer, face, slice) and returns the
        /// supercompressionGlobalData and the data of each mip level.
        /// </summary>
        public static (byte[] sgd, byte[][] levels) Encode(Image[] images, int levelCount, bool alpha, Params p)
        {
            var st = new State { Par = new ParallelOptions { MaxDegreeOfParallelism = p.MaxDegreeOfParallelism > 0 ? p.MaxDegreeOfParallelism : -1 } };
            var slices = new List<Slice>();
            int n = 0;
            for (int i = 0; i < images.Length; i++)
                for (int a = 0; a < (alpha ? 2 : 1); a++)
                {
                    var s = new Slice { Image = i, Alpha = a == 1, Bx = (images[i].Width + 3) / 4, By = (images[i].Height + 3) / 4, First = n };
                    slices.Add(s);
                    n += s.Count;
                }
            st.N = n;
            st.Px = new byte[(long)n * 48];
            st.AlphaBlock = new bool[n];
            bool gray = true;
            foreach (var s in slices)
            {
                var img = images[s.Image];
                for (int by = 0; by < s.By; by++)
                    for (int bx = 0; bx < s.Bx; bx++)
                    {
                        int b = s.First + by * s.Bx + bx;
                        st.AlphaBlock[b] = s.Alpha;
                        for (int t = 0; t < 16; t++)
                        {
                            int x = Math.Min(bx * 4 + (t & 3), img.Width - 1), y = Math.Min(by * 4 + (t >> 2), img.Height - 1);
                            int o = (y * img.Width + x) * 4;
                            long d = (long)b * 48 + t * 3;
                            if (s.Alpha) st.Px[d] = st.Px[d + 1] = st.Px[d + 2] = img.Rgba[o + 3];
                            else
                            {
                                byte r = img.Rgba[o], g = img.Rgba[o + 1], bl = img.Rgba[o + 2];
                                st.Px[d] = r; st.Px[d + 1] = g; st.Px[d + 2] = bl;
                                if (r != g || r != bl) gray = false;
                            }
                        }
                    }
            }
            st.Gray = gray;

            int q = Math.Clamp(p.Quality, 1, 255);
            double qf = q / 255.0;
            // codebook sizes for a 768 x 512 texture (24576 blocks), proportionally smaller below that
            double scale = Math.Min(1.0, n / 24576.0);
            int ne = p.MaxEndpoints ?? (int)Math.Max(8, Math.Min(EndpointTarget(q) * scale, n / 4));
            int ns = p.MaxSelectors ?? (int)Math.Max(8, (96 + 16000 * Math.Pow(qf, 2.6)) * scale);
            ne = Math.Clamp(Math.Min(ne, n), 1, MaxCodebook);
            ns = Math.Clamp(Math.Min(ns, n), 1, MaxCodebook);

            BuildEndpoints(st, ne, p.Effort, qf);
            var selTree = BuildSelectors(st, ns, p.Effort);
            UpdateEndpoints(st, p.Effort);
            Refine(st, selTree, p.Effort == 0 ? 1 : p.Effort == 1 ? 2 : 4, p.Effort);
            Compact(st);

            // final codes per slice with the rate term, then the bitstreams
            double lambda = LambdaScale * Math.Pow(1 - qf, 2);
            var codes = new (int[] ep, int[] sel)[slices.Count];
            Parallel.For(0, slices.Count, st.Par, i => codes[i] = ChooseCodes(st, slices[i], lambda, p.Effort));
            return Write(st, slices, codes, images, levelCount, alpha);
        }

        /// <summary>Endpoint codebook size for a quality level (piecewise linear in log size; duplicates are merged later).</summary>
        static double EndpointTarget(int q)
        {
            ReadOnlySpan<int> qs = stackalloc int[] { 1, 16, 32, 64, 128, 192, 255 };
            ReadOnlySpan<int> ns = stackalloc int[] { 126, 600, 950, 1460, 2270, 4400, 6000 };
            for (int i = 1; i < qs.Length; i++)
                if (q <= qs[i])
                {
                    double t = (q - qs[i - 1]) / (double)(qs[i] - qs[i - 1]);
                    return Math.Exp(Math.Log(ns[i - 1]) + t * (Math.Log(ns[i]) - Math.Log(ns[i - 1])));
                }
            return ns[^1];
        }

        // ------------------------------------------------------------------ block fitting

        /// <summary>Palette (4 colours * RGB) of an endpoint.</summary>
        static void Palette(int e, Span<int> pal)
        {
            int r = Ext5(R(e)), g = Ext5(G(e)), b = Ext5(B(e)), t = T(e) * 4;
            for (int s = 0; s < 4; s++)
            {
                int m = Mod[t + s];
                pal[s * 3] = Clamp255(r + m); pal[s * 3 + 1] = Clamp255(g + m); pal[s * 3 + 2] = Clamp255(b + m);
            }
        }

        /// <summary>Per texel squared error of each selector (16 * 4) for a block and an endpoint.</summary>
        /// <summary>Fills the error table and returns the sum of each texel's least error (the error with ideal selectors).</summary>
        static int ErrTable(State st, int b, int e, Span<int> err)
        {
            var px = st.Px.AsSpan(b * 48, 48);
            if (Vector128.IsHardwareAccelerated && !st.AlphaBlock[b])
            {
                // the four selectors in the four lanes
                int r0 = Ext5(R(e)), g0 = Ext5(G(e)), b0 = Ext5(B(e)), t4 = T(e) * 4;
                var mod = Vector128.Create(Mod[t4], Mod[t4 + 1], Mod[t4 + 2], Mod[t4 + 3]);
                var zero = Vector128<int>.Zero;
                var max = Vector128.Create(255);
                var pr = Vector128.Min(Vector128.Max(Vector128.Create(r0) + mod, zero), max);
                var pg = Vector128.Min(Vector128.Max(Vector128.Create(g0) + mod, zero), max);
                var pb = Vector128.Min(Vector128.Max(Vector128.Create(b0) + mod, zero), max);
                var lbv = Vector128<int>.Zero;
                for (int t = 0; t < 16; t++)
                {
                    var dr = pr - Vector128.Create((int)px[t * 3]);
                    var dg = pg - Vector128.Create((int)px[t * 3 + 1]);
                    var db = pb - Vector128.Create((int)px[t * 3 + 2]);
                    var d = dr * dr + dg * dg + db * db;
                    d.CopyTo(err.Slice(t * 4, 4));
                    var m = Vector128.Min(d, Vector128.Shuffle(d, Vector128.Create(2, 3, 0, 1)));
                    lbv += Vector128.Min(m, Vector128.Shuffle(m, Vector128.Create(1, 0, 3, 2)));
                }
                return lbv.ToScalar();
            }
            Span<int> pal = stackalloc int[12];
            Palette(e, pal);
            int lb = 0;
            if (st.AlphaBlock[b])
            {
                for (int t = 0; t < 16; t++)
                {
                    int v = px[t * 3 + 1], m = int.MaxValue;
                    for (int s = 0; s < 4; s++) { int d = pal[s * 3 + 1] - v; err[t * 4 + s] = d * d; m = Math.Min(m, d * d); }
                    lb += m;
                }
                return lb;
            }
            for (int t = 0; t < 16; t++)
            {
                int r = px[t * 3], g = px[t * 3 + 1], bl = px[t * 3 + 2], m = int.MaxValue;
                for (int s = 0; s < 4; s++)
                {
                    int dr = pal[s * 3] - r, dg = pal[s * 3 + 1] - g, db = pal[s * 3 + 2] - bl;
                    int x = dr * dr + dg * dg + db * db;
                    err[t * 4 + s] = x;
                    m = Math.Min(m, x);
                }
                lb += m;
            }
            return lb;
        }

        /// <summary>Error of a block with an endpoint and the best selector per texel; the selectors in <paramref name="sel"/>.</summary>
        static int BestErr(State st, int b, int e, out uint sel)
        {
            Span<int> err = stackalloc int[64];
            ErrTable(st, b, e, err);
            int total = 0;
            sel = 0;
            for (int t = 0; t < 16; t++)
            {
                int bs = 0, be = err[t * 4];
                for (int s = 1; s < 4; s++) if (err[t * 4 + s] < be) { be = err[t * 4 + s]; bs = s; }
                total += be;
                sel |= (uint)bs << (2 * t);
            }
            return total;
        }

        static int SelErr(State st, int b, int e, uint sel)
        {
            Span<int> err = stackalloc int[64];
            ErrTable(st, b, e, err);
            int total = 0;
            for (int t = 0; t < 16; t++) total += err[t * 4 + (int)(sel >> (2 * t) & 3)];
            return total;
        }

        /// <summary>
        /// The endpoint (5 bit colour, intensity table) of least error for the texels of <paramref name="members"/>, each
        /// texel with its best selector (or with its block's given selector entry when <paramref name="sels"/> is set).
        /// </summary>
        static int FitEndpoint(State st, ReadOnlySpan<int> members, uint[]? sels, int effort, out long bestErr)
        {
            // large sets: candidates are ranked on an even sample, the best few checked on every member
            const int SampleSize = 48;
            if (members.Length > SampleSize * 2)
            {
                int step = members.Length / SampleSize;
                var sm = new int[SampleSize];
                var ss = sels != null ? new uint[SampleSize] : null;
                for (int i = 0; i < SampleSize; i++) { sm[i] = members[i * step]; if (ss != null) ss[i] = sels![i * step]; }
                Span<int> top = stackalloc int[4];
                int nt = FitCandidates(st, sm, ss, effort, top);
                bestErr = long.MaxValue;
                int best = top[0];
                for (int k = 0; k < nt; k++)
                {
                    long err = 0;
                    for (int i = 0; i < members.Length && err < bestErr; i++)
                        err += sels != null ? SelErr(st, members[i], top[k], sels[i]) : BestErr(st, members[i], top[k], out _);
                    if (err < bestErr) { bestErr = err; best = top[k]; }
                }
                return best;
            }
            Span<int> one = stackalloc int[1];
            FitCandidates(st, members, sels, effort, one, out bestErr);
            return one[0];
        }

        static int FitCandidates(State st, ReadOnlySpan<int> members, uint[]? sels, int effort, Span<int> top)
            => FitCandidates(st, members, sels, effort, top, out _);

        /// <summary>
        /// The endpoint search on a set of blocks: per intensity table the least squares base colour (from the mean, selectors
        /// and base alternately); for the best tables also starts with every texel on one modifier (flat blocks reach
        /// colours a 5 bit base cannot) and every rounding of the base colour. Writes the best distinct endpoints found
        /// (best first, up to top.Length) and returns how many.
        /// </summary>
        static int FitCandidates(State st, ReadOnlySpan<int> members, uint[]? sels, int effort, Span<int> top, out long bestErr)
        {
            Span<double> mean = stackalloc double[3];
            Span<long> cnt = stackalloc long[3];
            mean.Clear(); cnt.Clear();
            bool anyColour = false;
            foreach (int b in members)
            {
                var px = st.Px.AsSpan(b * 48, 48);
                if (st.AlphaBlock[b])
                {
                    for (int t = 0; t < 16; t++) mean[1] += px[t * 3 + 1];
                    cnt[1] += 16;
                }
                else
                {
                    anyColour = true;
                    for (int t = 0; t < 16; t++) { mean[0] += px[t * 3]; mean[1] += px[t * 3 + 1]; mean[2] += px[t * 3 + 2]; }
                    cnt[0] += 16; cnt[1] += 16; cnt[2] += 16;
                }
            }
            for (int c = 0; c < 3; c++) mean[c] = cnt[c] > 0 ? mean[c] / cnt[c] : 0;
            bool grayOnly = st.Gray || !anyColour;
            if (!anyColour) mean[0] = mean[2] = mean[1];

            // the best few distinct endpoints so far
            Span<long> topErr = stackalloc long[top.Length];
            topErr.Fill(long.MaxValue);
            int found = 0;
            static void Offer(int e, long err, Span<int> top, Span<long> topErr, ref int found)
            {
                for (int k = 0; k < found; k++) if (top[k] == e) return;
                if (err >= topErr[top.Length - 1]) return;
                int pos = Math.Min(found, top.Length - 1);
                while (pos > 0 && topErr[pos - 1] > err) { top[pos] = top[pos - 1]; topErr[pos] = topErr[pos - 1]; pos--; }
                top[pos] = e; topErr[pos] = err;
                if (found < top.Length) found++;
            }
            static long Eval(State st, ReadOnlySpan<int> members, uint[]? sels, int e, long bound, Span<double> sum, int t)
            {
                long err = 0;
                bool collect = !sum.IsEmpty;
                if (collect) sum.Clear();
                for (int i = 0; i < members.Length; i++)
                {
                    int b = members[i];
                    if (sels != null) err += SelErr(st, b, e, sels[i]);
                    else
                    {
                        err += BestErr(st, b, e, out uint sl);
                        if (collect) AddResiduals(st, b, t, sl, sum);
                    }
                    if (err >= bound && !collect) break;
                }
                return err;
            }

            Span<double> bas = stackalloc double[3], sum = stackalloc double[3];
            Span<double> tBase = stackalloc double[24];
            Span<long> tErr = stackalloc long[8];
            int iters = sels != null ? 1 : effort == 0 ? 1 : 2;
            // phase 1: every intensity table from the mean (or, selectors given, the exact least squares base)
            for (int t = 0; t < 8; t++)
            {
                if (sels != null)
                {
                    sum.Clear();
                    for (int i = 0; i < members.Length; i++) AddResiduals(st, members[i], t, sels[i], sum);
                    for (int c = 0; c < 3; c++) bas[c] = cnt[c] > 0 ? sum[c] / cnt[c] : 0;
                    if (!anyColour) bas[0] = bas[2] = bas[1];
                }
                else for (int c = 0; c < 3; c++) bas[c] = mean[c];
                tErr[t] = long.MaxValue;
                for (int it = 0; it < iters; it++)
                {
                    int e = Quantize(bas, t, grayOnly, cnt[0] > 0);
                    bool more = it + 1 < iters;
                    long err = Eval(st, members, sels, e, topErr[top.Length - 1], more ? sum : Span<double>.Empty, t);
                    if (err < tErr[t]) { tErr[t] = err; for (int c = 0; c < 3; c++) tBase[t * 3 + c] = bas[c]; }
                    Offer(e, err, top, topErr, ref found);
                    if (!more) break;
                    for (int c = 0; c < 3; c++) bas[c] = cnt[c] > 0 ? sum[c] / cnt[c] : bas[c];
                    if (!anyColour) bas[0] = bas[2] = bas[1];
                }
            }
            Span<int> order = stackalloc int[8];
            for (int t = 0; t < 8; t++) order[t] = t;
            for (int a = 1; a < 8; a++)
                for (int k = a; k > 0 && tErr[order[k]] < tErr[order[k - 1]]; k--) (order[k], order[k - 1]) = (order[k - 1], order[k]);
            // phase 1b: the best tables, every texel on one modifier to start with
            if (sels == null)
            {
                int tables = effort == 0 ? 2 : 3;
                for (int ki = 0; ki < tables; ki++)
                {
                    int t = order[ki];
                    for (int s = 0; s < 4; s++)
                    {
                        for (int c = 0; c < 3; c++) bas[c] = Math.Clamp(mean[c] - Mod[t * 4 + s], 0, 255);
                        int e = Quantize(bas, t, grayOnly, cnt[0] > 0);
                        long err = Eval(st, members, sels, e, topErr[top.Length - 1], Span<double>.Empty, t);
                        Offer(e, err, top, topErr, ref found);
                        if (err < tErr[t]) { tErr[t] = err; for (int c = 0; c < 3; c++) tBase[t * 3 + c] = bas[c]; }
                    }
                }
                for (int a = 1; a < 8; a++)
                    for (int k = a; k > 0 && tErr[order[k]] < tErr[order[k - 1]]; k--) (order[k], order[k - 1]) = (order[k - 1], order[k]);
            }
            // phase 2: the best tables with every rounding (down / up) of the base colour components
            int topK = effort == 0 ? 1 : effort == 1 ? 2 : 3;
            Span<int> lo = stackalloc int[3];
            for (int ki = 0; ki < topK; ki++)
            {
                int t = order[ki];
                double gv = cnt[0] > 0 ? (tBase[t * 3] + tBase[t * 3 + 1] + tBase[t * 3 + 2]) / 3 : tBase[t * 3 + 1];
                for (int c = 0; c < 3; c++) lo[c] = Math.Clamp((int)Math.Floor((grayOnly ? gv : tBase[t * 3 + c]) * 31 / 255), 0, 30);
                int combos = grayOnly ? 2 : 8;
                for (int m = 0; m < combos; m++)
                {
                    int r = lo[0] + (m & 1), g = lo[1] + (grayOnly ? m & 1 : m >> 1 & 1), bl = lo[2] + (grayOnly ? m & 1 : m >> 2 & 1);
                    if (grayOnly) g = bl = r;
                    int e = Pack(r, g, bl, t);
                    long err = Eval(st, members, sels, e, topErr[top.Length - 1], Span<double>.Empty, t);
                    Offer(e, err, top, topErr, ref found);
                }
            }
            if (effort >= 2)
            {
                // nudge each colour component of the best by one step
                for (int pass = 0; pass < 2; pass++)
                {
                    int best = top[0];
                    for (int c = 0; c < (grayOnly ? 1 : 3); c++)
                        for (int d = -1; d <= 1; d += 2)
                        {
                            int r = R(best), g = G(best), bl = B(best);
                            if (grayOnly) { r += d; g += d; bl += d; }
                            else if (c == 0) r += d; else if (c == 1) g += d; else bl += d;
                            if ((uint)r > 31 || (uint)g > 31 || (uint)bl > 31) continue;
                            int e = Pack(r, g, bl, T(best));
                            long err = Eval(st, members, sels, e, topErr[top.Length - 1], Span<double>.Empty, T(best));
                            Offer(e, err, top, topErr, ref found);
                        }
                    if (top[0] == best) break;
                }
            }
            bestErr = topErr[0];
            return found;
        }

        /// <summary>Adds texel minus modifier (per channel) of a block with given selectors: the least squares base colour sums.</summary>
        static void AddResiduals(State st, int b, int t, uint sl, Span<double> sum)
        {
            var px = st.Px.AsSpan(b * 48, 48);
            if (st.AlphaBlock[b])
            {
                for (int k = 0; k < 16; k++) sum[1] += px[k * 3 + 1] - Mod[t * 4 + (int)(sl >> (2 * k) & 3)];
                return;
            }
            for (int k = 0; k < 16; k++)
            {
                int m = Mod[t * 4 + (int)(sl >> (2 * k) & 3)];
                sum[0] += px[k * 3] - m; sum[1] += px[k * 3 + 1] - m; sum[2] += px[k * 3 + 2] - m;
            }
        }

        static int Quantize(ReadOnlySpan<double> bas, int t, bool gray, bool anyColour)
        {
            if (gray)
            {
                int v = Q5(anyColour ? (bas[0] + bas[1] + bas[2]) / 3 : bas[1]);
                return Pack(v, v, v, t);
            }
            return Pack(Q5(bas[0]), Q5(bas[1]), Q5(bas[2]), t);
        }

        static int Q5(double v) => Math.Clamp((int)Math.Round(v * 31 / 255), 0, 31);

        /// <summary>
        /// The endpoints bucketed by their 5 bit colour: candidates for a block are gathered in growing shells around the
        /// colour of its own best endpoint (nearest colours first).
        /// </summary>
        sealed class EndpointGrid
        {
            readonly int[] start = new int[32 * 32 * 32 + 1];
            readonly int[] items;

            /// <summary>Each endpoint is filed under the colour cells of its four palette entries (5 bits per component).</summary>
            public EndpointGrid(int[] endpoints)
            {
                Span<int> pal = stackalloc int[12];
                Span<int> cells = stackalloc int[4];
                int Cells(int e, Span<int> pal, Span<int> cells)
                {
                    Palette(e, pal);
                    int n = 0;
                    for (int s = 0; s < 4; s++)
                    {
                        int cell = (pal[s * 3] * 31 + 127) / 255 | (pal[s * 3 + 1] * 31 + 127) / 255 << 5 | (pal[s * 3 + 2] * 31 + 127) / 255 << 10;
                        bool dup = false;
                        for (int k = 0; k < n; k++) dup |= cells[k] == cell;
                        if (!dup) cells[n++] = cell;
                    }
                    return n;
                }
                int total = 0;
                foreach (int e in endpoints)
                {
                    int n = Cells(e, pal, cells);
                    total += n;
                    for (int k = 0; k < n; k++) start[cells[k] + 1]++;
                }
                for (int i = 0; i < 32768; i++) start[i + 1] += start[i];
                var pos = (int[])start.Clone();
                items = new int[total];
                for (int i = 0; i < endpoints.Length; i++)
                {
                    int n = Cells(endpoints[i], pal, cells);
                    for (int k = 0; k < n; k++) items[pos[cells[k]]++] = i;
                }
            }

            /// <summary>The candidates of every colour cell some block has its mean in (blocks share cells, so this is gathered once per cell).</summary>
            public int[][] ForCells(int[] meanCells, int endpointCount, int min, int cap, ParallelOptions par)
            {
                var used = new bool[32768];
                foreach (int c in meanCells) used[c] = true;
                var cells = new List<int>();
                for (int c = 0; c < 32768; c++) if (used[c]) cells.Add(c);
                var res = new int[32768][];
                Parallel.For(0, cells.Count, par, () => (new int[cap], new int[endpointCount], 0), (i, ls, buf) =>
                {
                    var (ids, stamp, tag) = buf;
                    int c = cells[i];
                    int n = Gather(c & 31, c >> 5 & 31, c >> 10, min, ids, stamp, ++tag);
                    res[c] = ids.AsSpan(0, n).ToArray();
                    return (ids, stamp, tag);
                }, _ => { });
                return res;
            }

            /// <summary>Endpoint ids in shells of growing radius around (r, g, b) until at least <paramref name="min"/> (all of a shell).</summary>
            public int Gather(int r, int g, int b, int min, Span<int> ids, int[] stamp, int tag)
            {
                int n = 0;
                for (int rad = 0; rad < 32 && n < min; rad++)
                {
                    for (int dr = -rad; dr <= rad; dr++)
                    {
                        int x = r + dr;
                        if ((uint)x > 31) continue;
                        for (int dg = -rad; dg <= rad; dg++)
                        {
                            int y = g + dg;
                            if ((uint)y > 31) continue;
                            bool edge = Math.Abs(dr) == rad || Math.Abs(dg) == rad;
                            for (int db = -rad; db <= rad; db += edge ? 1 : 2 * rad)
                            {
                                int z = b + db;
                                if ((uint)z <= 31)
                                {
                                    int cell = x | y << 5 | z << 10;
                                    for (int k = start[cell]; k < start[cell + 1] && n < ids.Length; k++)
                                    {
                                        int id = items[k];
                                        if (stamp[id] == tag) continue;
                                        stamp[id] = tag;
                                        ids[n++] = id;
                                    }
                                }
                                if (rad == 0) break;
                            }
                        }
                    }
                    if (n >= ids.Length) break;
                }
                return n;
            }
        }

        // ------------------------------------------------------------------ tree structured vector quantization

        /// <summary>
        /// The result of <see cref="Split"/>: each item's leaf, per leaf the leaves of its neighbourhood in the tree (the
        /// subtree some levels above) as reassignment candidates, and the split planes to find the leaf of any vector.
        /// </summary>
        sealed class SplitTree
        {
            public int Leaves, Dim;
            public int[] LeafOf = Array.Empty<int>();
            public int[][] Candidates = Array.Empty<int[]>();
            public int[] Left = Array.Empty<int>(), Right = Array.Empty<int>(), LeafId = Array.Empty<int>();
            public float[][] Mean = Array.Empty<float[]>(), Axis = Array.Empty<float[]>();
            public float[] Thresh = Array.Empty<float>();

            /// <summary>
            /// Best bin first search: up to <paramref name="budget"/> leaves in order of their split plane distance from
            /// <paramref name="f"/> (the nearest first); returns how many were written to <paramref name="leaves"/>.
            /// </summary>
            public int Search(ReadOnlySpan<float> f, int budget, Span<int> leaves, PriorityQueue<int, float> pq)
            {
                pq.Clear();
                pq.Enqueue(0, 0);
                int count = 0;
                while (count < budget && pq.TryDequeue(out int node, out float dist))
                {
                    while (Left[node] >= 0)
                    {
                        var m = Mean[node]; var a = Axis[node];
                        float d = 0;
                        for (int k = 0; k < Dim; k++) d += (f[k] - m[k]) * a[k];
                        d -= Thresh[node];
                        int near = d < 0 ? Left[node] : Right[node], far = d < 0 ? Right[node] : Left[node];
                        pq.Enqueue(far, Math.Max(dist, d * d));
                        node = near;
                    }
                    leaves[count++] = LeafId[node];
                }
                return count;
            }

            /// <summary>The leaf a feature vector falls into.</summary>
            public int Query(ReadOnlySpan<float> f)
            {
                int node = 0;
                while (Left[node] >= 0)
                {
                    var m = Mean[node]; var a = Axis[node];
                    float d = 0;
                    for (int k = 0; k < Dim; k++) d += (f[k] - m[k]) * a[k];
                    node = d < Thresh[node] ? Left[node] : Right[node];
                }
                return LeafId[node];
            }
        }

        /// <summary>
        /// Top down clustering: the cluster of largest squared error is split at its mean along its principal axis until
        /// there are <paramref name="target"/> clusters. Candidates of a leaf: the leaves under the node
        /// <paramref name="up"/> levels above it, at most <paramref name="maxCand"/>.
        /// </summary>
        static SplitTree Split(float[] feat, int dim, int n, int target, int up, int maxCand, ParallelOptions par, double minSse = 0)
        {
            var nodeMean = new List<float[]>(); var nodeAxis = new List<float[]>(); var nodeThresh = new List<float>();
            var perm = new int[n];
            for (int i = 0; i < n; i++) perm[i] = i;
            var start = new List<int>(); var end = new List<int>(); var parent = new List<int>();
            var left = new List<int>(); var right = new List<int>();
            var pq = new PriorityQueue<int, double>();
            int AddNode(int s, int e, int par0)
            {
                int id = start.Count;
                start.Add(s); end.Add(e); parent.Add(par0); left.Add(-1); right.Add(-1);
                nodeMean.Add(Array.Empty<float>()); nodeAxis.Add(Array.Empty<float>()); nodeThresh.Add(0);
                return id;
            }
            int root = AddNode(0, n, -1);
            pq.Enqueue(root, -Sse(feat, dim, perm, 0, n));
            int leaves = 1;
            var proj = new float[n];
            var axis = new float[dim];
            var mean = new float[dim];
            while (leaves < target && pq.Count > 0)
            {
                pq.TryDequeue(out int node, out double negSse);
                if (-negSse <= 1e-9) break;
                if (minSse > 0 && -negSse < minSse * (end[node] - start[node])) continue;   // tight enough: not worth another entry
                int s = start[node], e = end[node], m = e - s;
                if (m < 2) continue;
                Axis(feat, dim, perm, s, e, axis, mean);
                for (int i = s; i < e; i++)
                {
                    int it = perm[i];
                    float d = 0;
                    for (int k = 0; k < dim; k++) d += (feat[it * dim + k] - mean[k]) * axis[k];
                    proj[it] = d;
                }
                // partition at the mean projection (0); fall back to the median when one side is empty
                int lo = s, hi = e - 1;
                while (lo <= hi)
                {
                    if (proj[perm[lo]] < 0) lo++;
                    else { (perm[lo], perm[hi]) = (perm[hi], perm[lo]); hi--; }
                }
                int mid = lo;
                float thresh = 0;
                if (mid == s || mid == e)
                {
                    Array.Sort(perm, s, m, Comparer<int>.Create((a, b) => proj[a].CompareTo(proj[b])));
                    mid = s + m / 2;
                    if (proj[perm[s]] == proj[perm[e - 1]]) continue;   // all identical along the axis: cannot split
                    while (mid < e && proj[perm[mid]] == proj[perm[mid - 1]]) mid++;
                    if (mid == e) { mid = s + m / 2; while (mid > s && proj[perm[mid]] == proj[perm[mid - 1]]) mid--; }
                    thresh = proj[perm[mid]];
                }
                int l = AddNode(s, mid, node), r = AddNode(mid, e, node);
                left[node] = l; right[node] = r;
                nodeMean[node] = (float[])mean.Clone(); nodeAxis[node] = (float[])axis.Clone(); nodeThresh[node] = thresh;
                pq.Enqueue(l, -Sse(feat, dim, perm, s, mid));
                pq.Enqueue(r, -Sse(feat, dim, perm, mid, e));
                leaves++;
            }
            // number the leaves
            var leafId = new int[start.Count];
            var tree = new SplitTree
            {
                LeafOf = new int[n], Dim = dim, Left = left.ToArray(), Right = right.ToArray(), LeafId = leafId,
                Mean = nodeMean.ToArray(), Axis = nodeAxis.ToArray(), Thresh = nodeThresh.ToArray(),
            };
            int count = 0;
            for (int i = 0; i < start.Count; i++)
            {
                if (left[i] >= 0) { leafId[i] = -1; continue; }
                leafId[i] = count++;
                for (int k = start[i]; k < end[i]; k++) tree.LeafOf[perm[k]] = leafId[i];
            }
            tree.Leaves = count;
            tree.Candidates = new int[count][];
            var stack = new Stack<int>();
            var list = new List<int>();
            for (int i = 0; i < start.Count; i++)
            {
                if (leafId[i] < 0) continue;
                int a = i;
                for (int u = 0; u < up && parent[a] >= 0; u++) a = parent[a];
                list.Clear();
                list.Add(leafId[i]);
                stack.Clear(); stack.Push(a);
                while (stack.Count > 0 && list.Count < maxCand)
                {
                    int x = stack.Pop();
                    if (left[x] < 0) { if (leafId[x] != leafId[i]) list.Add(leafId[x]); continue; }
                    stack.Push(right[x]); stack.Push(left[x]);
                }
                tree.Candidates[leafId[i]] = list.ToArray();
            }
            return tree;
        }

        static double Sse(float[] feat, int dim, int[] perm, int s, int e)
        {
            double sq = 0;
            Span<double> sum = stackalloc double[dim];
            sum.Clear();
            for (int i = s; i < e; i++)
            {
                int it = perm[i];
                for (int k = 0; k < dim; k++) { double v = feat[it * dim + k]; sum[k] += v; sq += v * v; }
            }
            double m = e - s;
            for (int k = 0; k < dim; k++) sq -= sum[k] * sum[k] / m;
            return sq;
        }

        /// <summary>Mean and principal axis (power iteration on the covariance of at most 1024 sampled items).</summary>
        static void Axis(float[] feat, int dim, int[] perm, int s, int e, float[] axis, float[] mean)
        {
            int m = e - s;
            int step = Math.Max(1, m / 1024);
            Array.Clear(mean);
            int cnt = 0;
            for (int i = s; i < e; i += step) { int it = perm[i]; for (int k = 0; k < dim; k++) mean[k] += feat[it * dim + k]; cnt++; }
            for (int k = 0; k < dim; k++) mean[k] /= cnt;
            var cov = new double[dim * dim];
            for (int i = s; i < e; i += step)
            {
                int it = perm[i];
                for (int a = 0; a < dim; a++)
                {
                    double da = feat[it * dim + a] - mean[a];
                    if (da == 0) continue;
                    for (int b = a; b < dim; b++) cov[a * dim + b] += da * (feat[it * dim + b] - mean[b]);
                }
            }
            for (int a = 0; a < dim; a++) for (int b = 0; b < a; b++) cov[a * dim + b] = cov[b * dim + a];
            int big = 0;
            for (int a = 1; a < dim; a++) if (cov[a * dim + a] > cov[big * dim + big]) big = a;
            var v = new double[dim];
            var u = new double[dim];
            for (int a = 0; a < dim; a++) v[a] = cov[big * dim + a];
            for (int it = 0; it < 10; it++)
            {
                double norm = 0;
                for (int a = 0; a < dim; a++)
                {
                    double x = 0;
                    for (int b = 0; b < dim; b++) x += cov[a * dim + b] * v[b];
                    u[a] = x; norm += x * x;
                }
                if (norm <= 1e-30) break;
                norm = 1 / Math.Sqrt(norm);
                for (int a = 0; a < dim; a++) v[a] = u[a] * norm;
            }
            double vn = 0;
            for (int a = 0; a < dim; a++) vn += v[a] * v[a];
            if (vn <= 1e-30) { Array.Clear(axis); axis[big] = 1; return; }
            vn = 1 / Math.Sqrt(vn);
            for (int a = 0; a < dim; a++) axis[a] = (float)(v[a] * vn);
        }

        /// <summary>Members of each cluster as one array (counting sort): items of cluster c at offsets[c] .. offsets[c + 1].</summary>
        static (int[] items, int[] offsets) Members(int[] assign, int clusters)
        {
            var off = new int[clusters + 1];
            foreach (int a in assign) off[a + 1]++;
            for (int c = 0; c < clusters; c++) off[c + 1] += off[c];
            var pos = (int[])off.Clone();
            var items = new int[assign.Length];
            for (int i = 0; i < assign.Length; i++) items[pos[assign[i]]++] = i;
            return (items, off);
        }

        // ------------------------------------------------------------------ endpoint codebook

        static void BuildEndpoints(State st, int target, int effort, double qf)
        {
            int n = st.N;
            // each block on its own: mean colour and the spread of its best intensity table
            var feat = new float[n * 4];
            st.MeanCell = new int[n];
            Parallel.For(0, n, st.Par, b =>
            {
                var px = st.Px.AsSpan(b * 48, 48);
                float r = 0, g = 0, bl = 0;
                for (int t = 0; t < 16; t++) { r += px[t * 3]; g += px[t * 3 + 1]; bl += px[t * 3 + 2]; }
                r /= 16; g /= 16; bl /= 16;
                // spread: mean absolute deviation of the texels from the mean along gray (what the modifiers reach)
                float dev = 0;
                for (int t = 0; t < 16; t++) dev += Math.Abs(px[t * 3] - r + px[t * 3 + 1] - g + px[t * 3 + 2] - bl);
                feat[b * 4] = r; feat[b * 4 + 1] = g; feat[b * 4 + 2] = bl;
                feat[b * 4 + 3] = dev / 16 / 3 * 1.732f;
                st.MeanCell[b] = (int)(r * 31 / 255 + 0.5f) | (int)(g * 31 / 255 + 0.5f) << 5 | (int)(bl * 31 / 255 + 0.5f) << 10;
            });

            var tree = Split(feat, 4, n, target, 0, 1, st.Par, EndpointMinSse * (1 - qf) * (1 - qf));
            int k = tree.Leaves;
            var assign = tree.LeafOf;
            var endpoints = new int[k];
            int rounds = effort == 0 ? 2 : effort == 1 ? 2 : 3;
            int min = EndpointCandidates(effort);
            for (int round = 0; round < rounds; round++)
            {
                var (items, off) = Members(assign, k);
                Parallel.For(0, k, st.Par, c =>
                {
                    if (off[c + 1] > off[c]) endpoints[c] = FitEndpoint(st, items.AsSpan(off[c], off[c + 1] - off[c]), null, effort, out _);
                });
                if (round == rounds - 1) break;
                // reassign each block to the best endpoint near the colour of its own
                var cellCands = new EndpointGrid(endpoints).ForCells(st.MeanCell, k, min, 2 * min, st.Par);
                Parallel.For(0, n, st.Par, b =>
                {
                    int bestC = assign[b], bestE = BestErr(st, b, endpoints[bestC], out _);
                    int a = st.MeanCell[b];
                    var cc = cellCands[a];
                    for (int i = 0; i < cc.Length && bestE > 0; i++)
                    {
                        int c = cc[i];
                        if (c == bestC) continue;
                        int e = BestErrBounded(st, b, endpoints[c], bestE);
                        if (e < bestE) { bestE = e; bestC = c; }
                    }
                    assign[b] = bestC;
                });
            }
            st.Ep = assign;
            st.Endpoints = endpoints;
        }

        static int EndpointCandidates(int effort) => effort == 0 ? 8 : effort == 1 ? 16 : 32;

        /// <summary><see cref="BestErr"/>, stopping early once the error reaches <paramref name="bound"/>.</summary>
        static int BestErrBounded(State st, int b, int e, int bound)
        {
            Span<int> pal = stackalloc int[12];
            Palette(e, pal);
            var px = st.Px.AsSpan(b * 48, 48);
            int total = 0;
            bool al = st.AlphaBlock[b];
            for (int t = 0; t < 16 && total < bound; t++)
            {
                int be = int.MaxValue;
                if (al)
                {
                    int v = px[t * 3 + 1];
                    for (int s = 0; s < 4; s++) { int d = pal[s * 3 + 1] - v; if (d * d < be) be = d * d; }
                }
                else
                {
                    int r = px[t * 3], g = px[t * 3 + 1], bl = px[t * 3 + 2];
                    for (int s = 0; s < 4; s++)
                    {
                        int dr = pal[s * 3] - r, dg = pal[s * 3 + 1] - g, db = pal[s * 3 + 2] - bl;
                        int x = dr * dr + dg * dg + db * db;
                        if (x < be) be = x;
                    }
                }
                total += be;
            }
            return total;
        }

        /// <summary>Selector feature of a block for an endpoint: the modifier of each texel's best selector.</summary>
        static void SelFeature(ReadOnlySpan<int> err, int e, Span<float> f)
        {
            int t = T(e) * 4;
            for (int k = 0; k < 16; k++)
            {
                int bs = 0;
                for (int s = 1; s < 4; s++) if (err[k * 4 + s] < err[k * 4 + bs]) bs = s;
                f[k] = Mod[t + bs];
            }
        }

        // ------------------------------------------------------------------ selector codebook

        static SplitTree BuildSelectors(State st, int target, int effort)
        {
            int n = st.N;
            // each block's best selectors for its endpoint, as modifier offsets (flat blocks cluster near zero)
            var feat = new float[n * 16];
            Parallel.For(0, n, st.Par, () => new int[64], (b, ls, err) =>
            {
                int e = st.Endpoints[st.Ep[b]];
                ErrTable(st, b, e, err);
                SelFeature(err, e, feat.AsSpan(b * 16, 16));
                return err;
            }, _ => { });
            var tree = Split(feat, 16, n, target, effort == 0 ? 3 : 4, effort == 0 ? 8 : effort == 1 ? 16 : 32, st.Par);
            st.Sel = (int[])tree.LeafOf.Clone();
            st.Selectors = new uint[tree.Leaves];
            UpdateSelectors(st);
            return tree;
        }

        /// <summary>Each selector entry: per texel the selector of least total error over its member blocks.</summary>
        static void UpdateSelectors(State st)
        {
            int k = st.Selectors.Length;
            var (items, off) = Members(st.Sel, k);
            Parallel.For(0, k, st.Par, () => new int[128], (c, ls, acc) =>
            {
                if (off[c + 1] == off[c]) return acc;
                Array.Clear(acc, 0, 64);
                var err = acc.AsSpan(64, 64);
                for (int i = off[c]; i < off[c + 1]; i++)
                {
                    int b = items[i];
                    ErrTable(st, b, st.Endpoints[st.Ep[b]], err);
                    for (int j = 0; j < 64; j++) acc[j] += err[j];
                }
                uint code = 0;
                for (int t = 0; t < 16; t++)
                {
                    int bs = 0;
                    for (int s = 1; s < 4; s++) if (acc[t * 4 + s] < acc[t * 4 + bs]) bs = s;
                    code |= (uint)bs << (2 * t);
                }
                st.Selectors[c] = code;
                return acc;
            }, _ => { });
        }

        /// <summary>Each endpoint entry: the best for the texels of its member blocks with their selector entries.</summary>
        static void UpdateEndpoints(State st, int effort)
        {
            int ne = st.Endpoints.Length;
            var (items, off) = Members(st.Ep, ne);
            Parallel.For(0, ne, st.Par, c =>
            {
                int m = off[c + 1] - off[c];
                if (m == 0) return;
                var mem = items.AsSpan(off[c], m);
                var sels = new uint[m];
                for (int i = 0; i < m; i++) sels[i] = st.Selectors[st.Sel[mem[i]]];
                long cur = 0;
                for (int i = 0; i < m; i++) cur += SelErr(st, mem[i], st.Endpoints[c], sels[i]);
                int e = FitEndpoint(st, mem, sels, effort, out long err);
                if (err < cur) st.Endpoints[c] = e;
            });
        }

        /// <summary>
        /// Joint refinement: every block picks the endpoint (from its neighbourhood in the endpoint tree) and selector entry
        /// (from the neighbourhood of its ideal selectors for that endpoint in the selector tree) of least error; then the
        /// selector and endpoint entries are refitted to their members.
        /// </summary>

        static void Refine(State st, SplitTree selTree, int rounds, int effort)
        {
            int n = st.N, budget = SearchBudget;
            int min = EndpointCandidates(effort) / 2;
            // exhaustive selector search for the block's own endpoint while that stays affordable
            // (a scan of a few thousand entries costs about what a tree search does, and finds the best)
            bool exhaustive = st.Selectors.Length <= (effort == 0 ? 1024 : effort == 1 ? 4096 : 1 << 20) || (long)n * st.Selectors.Length <= (effort == 0 ? 0 : 400_000_000L);
            int searched = effort == 0 ? 0 : effort == 1 ? 1 : 3;
            for (int round = 0; round < rounds; round++)
            {
                var cellCands = new EndpointGrid(st.Endpoints).ForCells(st.MeanCell, st.Endpoints.Length, min, 2 * min, st.Par);
                Parallel.For(0, n, st.Par, () => new RefineScratch(st.Endpoints.Length), (b, ls, rs) =>
                {
                    int curE = st.Ep[b], curS = st.Sel[b];
                    int bestE = curE, bestS = curS;
                    var errs = rs.Errs;
                    // the block's own endpoint: the full selector search
                    int lb0 = ErrTable(st, b, st.Endpoints[curE], errs.AsSpan(0, 64));
                    int best = Cost(errs.AsSpan(0, 64), st.Selectors[curS]);
                    // (nothing to gain when the entry is about as good as ideal selectors)
                    if (best > lb0 + (lb0 >> SkipShift) + SkipAbs)
                        SearchSelectors(st, selTree, rs, errs.AsSpan(0, 64), st.Endpoints[curE], exhaustive, budget, curE, ref best, ref bestE, ref bestS);
                    // other endpoints near the block's colour: lower bound (ideal selectors) first, the best few searched
                    int a = st.MeanCell[b];
                    var cc = cellCands[a];
                    int nc = cc.Length;
                    int kept = 0;
                    for (int ci = 0; ci < nc; ci++)
                    {
                        int c = cc[ci];
                        if (c == curE) continue;
                        var err = errs.AsSpan(64 * (kept + 1), 64);
                        int lb = ErrTable(st, b, st.Endpoints[c], err);
                        if (lb >= best) continue;
                        int cs = Cost(err, st.Selectors[curS]);
                        if (cs < best) { best = cs; bestE = c; bestS = curS; }
                        rs.Lb[kept] = lb; rs.Ids[kept] = c; rs.Order[kept] = kept; kept++;
                    }
                    var order = rs.Order.AsSpan(0, kept);
                    for (int i = 1; i < kept; i++)
                        for (int k = i; k > 0 && rs.Lb[order[k]] < rs.Lb[order[k - 1]]; k--) (order[k], order[k - 1]) = (order[k - 1], order[k]);
                    for (int i = 0; i < Math.Min(kept, searched); i++)
                    {
                        int k = order[i];
                        if (rs.Lb[k] * 10L >= best * 9L) break;   // only clearly promising endpoints get a selector search
                        SearchSelectors(st, selTree, rs, errs.AsSpan(64 * (k + 1), 64), st.Endpoints[rs.Ids[k]], exhaustive, budget / 4, rs.Ids[k], ref best, ref bestE, ref bestS);
                    }
                    st.Ep[b] = bestE; st.Sel[b] = bestS;
                    return rs;
                }, _ => { });
                UpdateSelectors(st);
                UpdateEndpoints(st, effort);
            }
        }

        sealed class RefineScratch
        {
            public readonly int[] Errs = new int[64 * 33], Cands = new int[64], Lb = new int[32], Ids = new int[32], Order = new int[32];
            public readonly int[] Rows = new int[1024], Leaves = new int[256];
            public readonly float[] F = new float[16];
            public readonly PriorityQueue<int, float> Pq = new();
            public readonly int[] Stamp;
            public RefineScratch(int endpoints) { Stamp = new int[endpoints]; }
        }

        /// <summary>The selector entry of least error for an error table: every entry (row lookups) or a best bin first tree search.</summary>
        static void SearchSelectors(State st, SplitTree selTree, RefineScratch rs, ReadOnlySpan<int> err, int e, bool exhaustive, int budget,
            int c, ref int best, ref int bestE, ref int bestS)
        {
            if (exhaustive)
            {
                RowTables(err, rs.Rows);
                int bi = Scan(rs.Rows, st.Selectors, ref best);
                if (bi >= 0) { bestE = c; bestS = bi; }
                return;
            }
            SelFeature(err, e, rs.F);
            int nl = selTree.Search(rs.F, Math.Max(1, budget), rs.Leaves, rs.Pq);
            for (int li = 0; li < nl; li++)
            {
                int s = rs.Leaves[li];
                int cs = Cost(err, st.Selectors[s]);
                if (cs < best) { best = cs; bestE = c; bestS = s; }
            }
        }

        /// <summary>The selector entry of least error below <paramref name="best"/> (four row table lookups each), or -1.</summary>
        static unsafe int Scan(int[] rows, uint[] sel, ref int best)
        {
            int n = sel.Length, b = best, bi = -1, i = 0;
            fixed (int* r = rows)
            fixed (uint* s = sel)
            {
                if (System.Runtime.Intrinsics.X86.Avx2.IsSupported && n >= 8)
                {
                    // eight entries at a time: four gathers from the row tables
                    var m = Vector256.Create(255u);
                    var o1 = Vector256.Create(256); var o2 = Vector256.Create(512); var o3 = Vector256.Create(768);
                    var bv = Vector256.Create(b);
                    for (; i + 8 <= n; i += 8)
                    {
                        var c = Vector256.Load(s + i);
                        var cost = System.Runtime.Intrinsics.X86.Avx2.GatherVector256(r, (c & m).AsInt32(), 4)
                            + System.Runtime.Intrinsics.X86.Avx2.GatherVector256(r, (c >> 8 & m).AsInt32() + o1, 4)
                            + System.Runtime.Intrinsics.X86.Avx2.GatherVector256(r, (c >> 16 & m).AsInt32() + o2, 4)
                            + System.Runtime.Intrinsics.X86.Avx2.GatherVector256(r, (c >> 24).AsInt32() + o3, 4);
                        if (Vector256.LessThanAny(cost, bv))
                        {
                            for (int k = 0; k < 8; k++)
                                if (cost.GetElement(k) < b) { b = cost.GetElement(k); bi = i + k; }
                            bv = Vector256.Create(b);
                        }
                    }
                }
                for (; i < n; i++)
                {
                    uint code = s[i];
                    int cs = r[code & 255] + r[256 + (code >> 8 & 255)] + r[512 + (code >> 16 & 255)] + r[768 + (code >> 24)];
                    if (cs < b) { b = cs; bi = i; }
                }
            }
            best = b;
            return bi;
        }

        /// <summary>Error of every selector byte (one row of four texels) per row: 4 * 256 sums.</summary>
        static void RowTables(ReadOnlySpan<int> err, Span<int> rows)
        {
            Span<int> a = stackalloc int[16], b = stackalloc int[16];
            for (int j = 0; j < 4; j++)
            {
                int t = j * 16;
                for (int v = 0; v < 16; v++)
                {
                    a[v] = err[t + (v & 3)] + err[t + 4 + (v >> 2)];
                    b[v] = err[t + 8 + (v & 3)] + err[t + 12 + (v >> 2)];
                }
                var r = rows.Slice(j * 256, 256);
                if (Vector256.IsHardwareAccelerated)
                {
                    var lo = Vector256.Create<int>(a.Slice(0, 8)); var hi = Vector256.Create<int>(a.Slice(8, 8));
                    for (int h = 0; h < 16; h++)
                    {
                        var bh = Vector256.Create(b[h]);
                        (lo + bh).CopyTo(r.Slice(h * 16, 8));
                        (hi + bh).CopyTo(r.Slice(h * 16 + 8, 8));
                    }
                }
                else for (int v = 0; v < 256; v++) r[v] = a[v & 15] + b[v >> 4];
            }
        }

        static int Cost(ReadOnlySpan<int> err, uint code)
        {
            int total = 0;
            for (int t = 0; t < 16; t++) total += err[t * 4 + (int)(code >> (2 * t) & 3)];
            return total;
        }

        /// <summary>
        /// Drops unused and duplicate codebook entries, orders the endpoints along a space filling curve and the selectors
        /// by value (neighbouring entries are then similar, which the codebook's delta coding likes), at least two of each.
        /// </summary>
        static void Compact(State st)
        {
            // endpoints
            {
                var used = new bool[st.Endpoints.Length];
                foreach (int e in st.Ep) used[e] = true;
                var keys = new SortedDictionary<long, int>();   // key -> new index (after sorting)
                var distinct = new Dictionary<int, int>();
                var list = new List<int>();
                for (int i = 0; i < used.Length; i++)
                    if (used[i] && !distinct.ContainsKey(st.Endpoints[i])) { distinct[st.Endpoints[i]] = 0; list.Add(st.Endpoints[i]); }
                while (list.Count < 2) list.Add(list.Count == 0 ? 0 : (list[0] ^ 1));
                list.Sort((a, b) => EndpointKey(a).CompareTo(EndpointKey(b)));
                for (int i = 0; i < list.Count; i++) distinct[list[i]] = i;
                for (int b = 0; b < st.N; b++) st.Ep[b] = distinct[st.Endpoints[st.Ep[b]]];
                st.Endpoints = list.ToArray();
            }
            // selectors
            {
                var distinct = new Dictionary<uint, int>();
                var list = new List<uint>();
                foreach (int s in st.Sel)
                {
                    uint v = st.Selectors[s];
                    if (!distinct.ContainsKey(v)) { distinct[v] = 0; list.Add(v); }
                }
                while (list.Count < 2) list.Add(list.Count == 0 ? 0u : ~list[0]);
                list.Sort((a, b) => SelectorKey(a).CompareTo(SelectorKey(b)));
                for (int i = 0; i < list.Count; i++) distinct[list[i]] = i;
                for (int b = 0; b < st.N; b++) st.Sel[b] = distinct[st.Selectors[st.Sel[b]]];
                st.Selectors = list.ToArray();
            }
        }

        static long EndpointKey(int e)
        {
            // Morton order of the colour (5 bits per component), then the intensity
            long m = 0;
            int r = R(e), g = G(e), b = B(e);
            for (int i = 4; i >= 0; i--) m = m << 3 | (long)((g >> i & 1) << 2 | (r >> i & 1) << 1 | (b >> i & 1));
            return m << 3 | (long)T(e);
        }

        static long SelectorKey(uint s)
        {
            // rows top to bottom, each row as stored: entries sharing rows sort together
            return s;
        }

        // ------------------------------------------------------------------ final code choice (rate / distortion)

        sealed class Mtf
        {
            public readonly int[] V = new int[HistorySize];
            int rover = HistorySize / 2;
            public void Add(int x) { V[rover++] = x; if (rover == HistorySize) rover = HistorySize / 2; }
            public void Use(int i) { if (i != 0) (V[i / 2], V[i]) = (V[i], V[i / 2]); }
            public int Find(int x) { for (int i = 0; i < HistorySize; i++) if (V[i] == x) return i; return -1; }
        }

        /// <summary>
        /// Raster pass over one slice: per block the endpoint from its own, the left, upper and upper left one, and the
        /// selector from its own and the recently used ones, for the least error + lambda * estimated bits.
        /// </summary>
        static (int[] ep, int[] sel) ChooseCodes(State st, Slice s, double lambda, int effort)
        {
            int bx = s.Bx, by = s.By, count = s.Count;
            var ep = new int[count];
            var sel = new int[count];
            var hist = new Mtf();
            int ne = st.Endpoints.Length, ns = st.Selectors.Length;
            double bitsDelta = 2 + 0.6 * Math.Log2(ne), bitsNewSel = 1 + 0.85 * Math.Log2(ns);
            int histCand = effort == 0 ? 8 : effort == 1 ? 24 : HistorySize;
            Span<int> err = stackalloc int[64];
            Span<int> cands = stackalloc int[4];
            Span<double> candBits = stackalloc double[4];
            int prevEp = 0;
            for (int y = 0; y < by; y++)
                for (int x = 0; x < bx; x++)
                {
                    int i = y * bx + x, b = s.First + i;
                    int e0 = st.Ep[b], s0 = st.Sel[b];
                    int nc = 0;
                    cands[nc] = e0; candBits[nc++] = bitsDelta;
                    if (x > 0) { cands[nc] = ep[i - 1]; candBits[nc++] = 1; }
                    if (y > 0) { cands[nc] = ep[i - bx]; candBits[nc++] = 2; }
                    if (x > 0 && y > 0) { cands[nc] = ep[i - bx - 1]; candBits[nc++] = 3; }
                    double bestJ = double.MaxValue;
                    int bestE = e0, bestS = s0;
                    for (int c = 0; c < nc; c++)
                    {
                        int e = cands[c];
                        bool dup = false;
                        for (int k = 0; k < c; k++) if (cands[k] == e) dup = true;
                        if (dup) continue;
                        double eb = e == prevEp && x > 0 ? 1 : candBits[c];
                        if (lambda == 0 && e != e0) continue;
                        ErrTable(st, b, st.Endpoints[e], err);
                        // its own selector entry, new or from the history
                        int h0 = hist.Find(s0);
                        double j = Cost(err, st.Selectors[s0]) + lambda * (eb + (h0 < 0 ? bitsNewSel : h0 == 0 ? 1 : 3 + Math.Log2(1 + h0)));
                        if (j < bestJ) { bestJ = j; bestE = e; bestS = s0; }
                        if (lambda > 0)
                            for (int h = 0; h < histCand; h++)
                            {
                                int sv = hist.V[h];
                                if (sv == s0) continue;
                                double jj = Cost(err, st.Selectors[sv]) + lambda * (eb + (h == 0 ? 1 : 3 + Math.Log2(1 + h)));
                                if (jj < bestJ) { bestJ = jj; bestE = e; bestS = sv; }
                            }
                    }
                    ep[i] = bestE; sel[i] = bestS;
                    prevEp = bestE;
                    int hi = hist.Find(bestS);
                    if (hi >= 0) hist.Use(hi); else hist.Add(bestS);
                }
            return (ep, sel);
        }

        // ------------------------------------------------------------------ bitstreams

        // token kinds of the slice streams
        const int TokPred = 0, TokVlc4 = 1, TokDelta = 2, TokSel = 3, TokRle = 4, TokVlc7 = 5;

        static List<(int kind, int value)> Tokenize(Slice s, int[] ep, int[] sel, int ns)
        {
            int bx = s.Bx, by = s.By;
            var toks = new List<(int, int)>();
            // prediction of each block: 0 left, 1 above, 2 upper left, 3 delta from the previous block
            var pred = new int[bx * by];
            for (int y = 0; y < by; y++)
                for (int x = 0; x < bx; x++)
                {
                    int i = y * bx + x, e = ep[i];
                    pred[i] = x > 0 && ep[i - 1] == e ? 0 : y > 0 && ep[i - bx] == e ? 1 : x > 0 && y > 0 && ep[i - bx - 1] == e ? 2 : 3;
                }
            int P(int x, int y) => x < bx && y < by ? pred[y * bx + x] : 0;
            // the 2x2 group symbols in the order they are read
            var groupSyms = new List<int>();
            for (int y = 0; y < by; y += 2)
                for (int x = 0; x < bx; x += 2)
                    groupSyms.Add(P(x, y) | P(x + 1, y) << 2 | P(x, y + 1) << 4 | P(x + 1, y + 1) << 6);
            int gi = 0, repeat = 0, prevSym = 0;
            var hist = new Mtf();
            int rle = 0, prevEp = 0;
            for (int y = 0; y < by; y++)
                for (int x = 0; x < bx; x++)
                {
                    int i = y * bx + x;
                    if ((x & 1) == 0 && (y & 1) == 0)
                    {
                        if (repeat > 0) repeat--;
                        else
                        {
                            int sym = groupSyms[gi];
                            int run = 0;
                            if (sym == prevSym) while (gi + run < groupSyms.Count && groupSyms[gi + run] == prevSym) run++;
                            if (run >= 3)
                            {
                                toks.Add((TokPred, 256));
                                toks.Add((TokVlc4, run - 3));
                                repeat = run - 1;
                            }
                            else { toks.Add((TokPred, sym)); prevSym = sym; }
                        }
                        gi++;
                    }
                    int e = ep[i];
                    if (pred[i] == 3) toks.Add((TokDelta, e - prevEp));   // taken modulo the codebook size when written
                    prevEp = e;
                    // selector
                    int sv = sel[i];
                    if (rle > 0) { rle--; continue; }
                    int h = hist.Find(sv);
                    if (h == 0)
                    {
                        int run = 0;
                        while (i + run < bx * by && sel[i + run] == sv) run++;
                        if (run >= 3)
                        {
                            toks.Add((TokSel, ns + HistorySize));
                            if (run - 3 < 63) toks.Add((TokRle, run - 3));
                            else { toks.Add((TokRle, 63)); toks.Add((TokVlc7, run - 3)); }
                            rle = run - 1;
                            continue;
                        }
                        toks.Add((TokSel, ns));
                    }
                    else if (h > 0) { toks.Add((TokSel, ns + h)); hist.Use(h); }
                    else { toks.Add((TokSel, sv)); hist.Add(sv); }
                }
            return toks;
        }

        static (byte[] sgd, byte[][] levels) Write(State st, List<Slice> slices, (int[] ep, int[] sel)[] codes, Image[] images, int levelCount, bool alpha)
        {
            int ne = st.Endpoints.Length, ns = st.Selectors.Length;
            var tokens = new List<(int kind, int value)>[slices.Count];
            Parallel.For(0, slices.Count, st.Par, i => tokens[i] = Tokenize(slices[i], codes[i].ep, codes[i].sel, ns));
            // symbol statistics of the four slice tables
            var fPred = new long[257]; var fDelta = new long[ne]; var fSel = new long[ns + HistorySize + 1]; var fRle = new long[64];
            foreach (var list in tokens)
                foreach (var (kind, v) in list)
                    switch (kind)
                    {
                        case TokPred: fPred[v]++; break;
                        case TokDelta: fDelta[((v % ne) + ne) % ne]++; break;
                        case TokSel: fSel[v]++; break;
                        case TokRle: fRle[v]++; break;
                    }
            var hPred = HuffmanCode.Build(fPred, 16); var hDelta = HuffmanCode.Build(fDelta, 16);
            var hSel = HuffmanCode.Build(fSel, 16); var hRle = HuffmanCode.Build(fRle, 16);

            // slices
            var sliceBytes = new byte[slices.Count][];
            Parallel.For(0, slices.Count, st.Par, i =>
            {
                var w = new BitWriter();
                foreach (var (kind, v) in tokens[i])
                    switch (kind)
                    {
                        case TokPred: hPred.Put(ref w, v); break;
                        case TokDelta: hDelta.Put(ref w, ((v % ne) + ne) % ne); break;
                        case TokSel: hSel.Put(ref w, v); break;
                        case TokRle: hRle.Put(ref w, v); break;
                        case TokVlc4: Vlc(ref w, v, 4); break;
                        case TokVlc7: Vlc(ref w, v, 7); break;
                    }
                sliceBytes[i] = w.ToArray();
            });

            // level data and image descriptors
            int imageCount = images.Length;
            var desc = new (int ro, int rl, int ao, int al)[imageCount];
            var levels = new byte[levelCount][];
            var levelStreams = new System.IO.MemoryStream[levelCount];
            for (int l = 0; l < levelCount; l++) levelStreams[l] = new System.IO.MemoryStream();
            for (int i = 0; i < slices.Count; i++)
            {
                var s = slices[i];
                var ms = levelStreams[images[s.Image].Level];
                int off = (int)ms.Length;
                ms.Write(sliceBytes[i]);
                if (s.Alpha) { desc[s.Image].ao = off; desc[s.Image].al = sliceBytes[i].Length; }
                else { desc[s.Image].ro = off; desc[s.Image].rl = sliceBytes[i].Length; }
            }
            for (int l = 0; l < levelCount; l++) levels[l] = levelStreams[l].ToArray();

            var endpointsData = EndpointCodebook(st);
            var selectorsData = SelectorCodebook(st);
            var tw = new BitWriter();
            hPred.WriteTable(ref tw); hDelta.WriteTable(ref tw); hSel.WriteTable(ref tw); hRle.WriteTable(ref tw);
            tw.Put(HistorySize, 13);
            var tablesData = tw.ToArray();

            var sgd = new byte[20 + 20 * imageCount + endpointsData.Length + selectorsData.Length + tablesData.Length];
            var sp = sgd.AsSpan();
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(sp, (ushort)ne);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(sp.Slice(2), (ushort)ns);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(sp.Slice(4), (uint)endpointsData.Length);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(sp.Slice(8), (uint)selectorsData.Length);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(sp.Slice(12), (uint)tablesData.Length);
            for (int i = 0; i < imageCount; i++)
            {
                var d = sp.Slice(20 + 20 * i);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(4), (uint)desc[i].ro);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(8), (uint)desc[i].rl);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(12), (uint)desc[i].ao);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(16), (uint)desc[i].al);
            }
            int p = 20 + 20 * imageCount;
            endpointsData.CopyTo(sp.Slice(p)); p += endpointsData.Length;
            selectorsData.CopyTo(sp.Slice(p)); p += selectorsData.Length;
            tablesData.CopyTo(sp.Slice(p));
            return (sgd, levels);
        }

        static void Vlc(ref BitWriter w, int v, int bits)
        {
            uint u = (uint)v, mask = (1u << bits) - 1;
            for (int ofs = 0; ; ofs += bits)
            {
                uint chunk = u & mask;
                u >>= bits;
                bool more = u != 0 && ofs + bits < 32;
                w.Put(chunk | (more ? 1u << bits : 0), bits + 1);
                if (!more) break;
            }
        }

        /// <summary>The endpoint codebook (D.2): intensity and colour deltas, Huffman coded with three colour models by the previous value.</summary>
        static byte[] EndpointCodebook(State st)
        {
            bool gray = true;
            foreach (int e in st.Endpoints) if (R(e) != G(e) || R(e) != B(e)) { gray = false; break; }
            var f = new long[4][];
            for (int i = 0; i < 3; i++) f[i] = new long[32];
            f[3] = new long[8];
            void Walk(Action<int, int> emit)
            {
                Span<int> prev = stackalloc int[3] { 16, 16, 16 };
                int prevInten = 0;
                foreach (int e in st.Endpoints)
                {
                    emit(3, (T(e) - prevInten) & 7);
                    prevInten = T(e);
                    for (int c = 0; c < (gray ? 1 : 3); c++)
                    {
                        int v = c == 0 ? R(e) : c == 1 ? G(e) : B(e);
                        int model = prev[c] <= 9 ? 0 : prev[c] <= 21 ? 1 : 2;
                        emit(model, (v - prev[c]) & 31);
                        prev[c] = v;
                    }
                }
            }
            Walk((m, v) => f[m][v]++);
            var h = new HuffmanCode[4];
            for (int i = 0; i < 4; i++) h[i] = HuffmanCode.Build(f[i], 16);
            var w = new BitWriter();
            for (int i = 0; i < 4; i++) h[i].WriteTable(ref w);
            w.Put(gray ? 1u : 0u, 1);
            var syms = new List<(int, int)>();
            Walk((m, v) => syms.Add((m, v)));
            foreach (var (m, v) in syms) h[m].Put(ref w, v);
            return w.ToArray();
        }

        /// <summary>The selector codebook (D.3): XOR deltas of the rows, Huffman coded, or raw when that is smaller.</summary>
        static byte[] SelectorCodebook(State st)
        {
            var sels = st.Selectors;
            var f = new long[256];
            for (int i = 1; i < sels.Length; i++)
                for (int j = 0; j < 4; j++) f[(sels[i] >> (8 * j) ^ sels[i - 1] >> (8 * j)) & 0xFF]++;
            var h = HuffmanCode.Build(f, 16);
            var w = new BitWriter();
            w.Put(0, 2); w.Put(0, 1);
            h.WriteTable(ref w);
            for (int j = 0; j < 4; j++) w.Put(sels[0] >> (8 * j) & 0xFF, 8);
            for (int i = 1; i < sels.Length; i++)
                for (int j = 0; j < 4; j++) h.Put(ref w, (int)((sels[i] >> (8 * j) ^ sels[i - 1] >> (8 * j)) & 0xFF));
            var compressed = w.ToArray();
            var raw = new BitWriter();
            raw.Put(0, 2); raw.Put(1, 1);
            foreach (uint s in sels) for (int j = 0; j < 4; j++) raw.Put(s >> (8 * j) & 0xFF, 8);
            var r = raw.ToArray();
            return r.Length < compressed.Length ? r : compressed;
        }

        // ------------------------------------------------------------------ bits and Huffman codes

        internal struct BitWriter
        {
            byte[] buf;
            int len;
            ulong acc;
            int cnt;

            public void Put(uint v, int n)
            {
                if (n == 0) return;
                buf ??= new byte[256];
                acc |= (ulong)(v & (n == 32 ? uint.MaxValue : (1u << n) - 1)) << cnt;
                cnt += n;
                while (cnt >= 8)
                {
                    if (len == buf.Length) Array.Resize(ref buf, buf.Length * 2);
                    buf[len++] = (byte)acc;
                    acc >>= 8; cnt -= 8;
                }
            }

            public void Put(int v, int n) => Put((uint)v, n);

            public byte[] ToArray()
            {
                buf ??= new byte[1];
                var r = new byte[len + (cnt > 0 ? 1 : 0)];
                Array.Copy(buf, r, len);
                if (cnt > 0) r[len] = (byte)acc;
                return r;
            }
        }

        /// <summary>Canonical, length limited Huffman code (Deflate conventions: codes sent first bit first, i.e. reversed into the LSB first stream).</summary>
        internal sealed class HuffmanCode
        {
            public byte[] Lengths = Array.Empty<byte>();
            uint[] codes = Array.Empty<uint>();   // bit reversed

            public static HuffmanCode Build(long[] freq, int maxLen)
            {
                int n = freq.Length;
                var h = new HuffmanCode { Lengths = new byte[n] };
                var used = new List<int>();
                for (int i = 0; i < n; i++) if (freq[i] > 0) used.Add(i);
                // at least two coded symbols: decoders need a complete code
                for (int i = 0; used.Count < 2 && i < n; i++) if (!used.Contains(i)) used.Add(i);
                if (used.Count == 1) h.Lengths[used[0]] = 1;
                else
                {
                    var lens = ComputeLengths(used.ConvertAll(i => Math.Max(freq[i], 1)).ToArray(), maxLen);
                    for (int k = 0; k < used.Count; k++) h.Lengths[used[k]] = (byte)lens[k];
                }
                h.Assign();
                return h;
            }

            /// <summary>Code lengths (Huffman, then limited to maxLen by moving leaves, Kraft sum kept at 1).</summary>
            static int[] ComputeLengths(long[] f, int maxLen)
            {
                int n = f.Length;
                var order = new int[n];
                for (int i = 0; i < n; i++) order[i] = i;
                Array.Sort(order, (a, b) => f[a] != f[b] ? f[a].CompareTo(f[b]) : a.CompareTo(b));
                // two queue Huffman over the sorted leaves
                var weight = new long[2 * n];
                var parent = new int[2 * n];
                for (int i = 0; i < n; i++) weight[i] = f[order[i]];
                int leaf = 0, inner = n, next = n;
                int Pick()
                {
                    if (leaf < n && (inner >= next || weight[leaf] <= weight[inner])) return leaf++;
                    return inner++;
                }
                for (int k = 0; k < n - 1; k++)
                {
                    int a = Pick(), b = Pick();
                    weight[next] = weight[a] + weight[b];
                    parent[a] = parent[b] = next;
                    next++;
                }
                var depth = new int[2 * n];
                int root = next - 1;
                depth[root] = 0;
                for (int i = root - 1; i >= 0; i--) depth[i] = depth[parent[i]] + 1;
                // limit: counts per length, overflow moved to maxLen and the Kraft sum repaired
                var count = new int[Math.Max(maxLen, 64) + 1];
                for (int i = 0; i < n; i++) count[Math.Min(depth[i], maxLen)]++;
                long kraft = 0;
                for (int l = 1; l <= maxLen; l++) kraft += (long)count[l] << (maxLen - l);
                while (kraft > 1L << maxLen)
                {
                    count[maxLen]--;
                    for (int l = maxLen - 1; l > 0; l--)
                        if (count[l] > 0) { count[l]--; count[l + 1] += 2; break; }
                    kraft--;
                }
                // most frequent symbols get the shortest codes
                var lens = new int[n];
                int pos = n - 1;
                for (int l = 1; l <= maxLen; l++)
                    for (int c = 0; c < count[l]; c++) lens[order[pos--]] = l;
                return lens;
            }

            void Assign()
            {
                int n = Lengths.Length;
                codes = new uint[n];
                var count = new int[17];
                foreach (var l in Lengths) count[l]++;
                count[0] = 0;
                var next = new int[17];
                int code = 0;
                for (int l = 1; l <= 16; l++) { code = (code + count[l - 1]) << 1; next[l] = code; }
                for (int s = 0; s < n; s++)
                {
                    int l = Lengths[s];
                    if (l == 0) continue;
                    int c = next[l]++;
                    uint rev = 0;
                    for (int i = 0; i < l; i++) rev |= (uint)(c >> i & 1) << (l - 1 - i);
                    codes[s] = rev;
                }
            }

            public void Put(ref BitWriter w, int sym)
            {
                int l = Lengths[sym];
                if (l == 0) throw new InvalidOperationException("Huffman: symbol without a code");
                w.Put(codes[sym], l);
            }

            static readonly byte[] CodeLengthOrder = { 17, 18, 19, 20, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15, 16 };

            /// <summary>Writes the table as KTX 2.0 D.1 describes: code lengths run length coded with a small Huffman code.</summary>
            public void WriteTable(ref BitWriter w)
            {
                int total = Lengths.Length;
                // run length tokens: 0..16 lengths, 17 / 18 zero runs, 19 / 20 repeats of the previous length
                var toks = new List<(int sym, int extra, int bits)>();
                int i = 0;
                while (i < total)
                {
                    int l = Lengths[i], run = 1;
                    while (i + run < total && Lengths[i + run] == l) run++;
                    if (l == 0)
                    {
                        int r = run;
                        while (r > 0)
                        {
                            if (r >= 11) { int k = Math.Min(r, 138); toks.Add((18, k - 11, 7)); r -= k; }
                            else if (r >= 3) { toks.Add((17, r - 3, 3)); r = 0; }
                            else { toks.Add((0, 0, 0)); r--; }
                        }
                    }
                    else
                    {
                        toks.Add((l, 0, 0));
                        int r = run - 1;
                        while (r > 0)
                        {
                            if (r >= 7) { int k = Math.Min(r, 134); toks.Add((20, k - 7, 7)); r -= k; }
                            else if (r >= 3) { toks.Add((19, r - 3, 2)); r = 0; }
                            else { toks.Add((l, 0, 0)); r--; }
                        }
                    }
                    i += run;
                }
                var f = new long[21];
                foreach (var t in toks) f[t.sym]++;
                var clh = Build(f, 7);
                int ncl = 21;
                while (ncl > 1 && clh.Lengths[CodeLengthOrder[ncl - 1]] == 0) ncl--;
                w.Put(total, 14);
                w.Put(ncl, 5);
                for (int k = 0; k < ncl; k++) w.Put(clh.Lengths[CodeLengthOrder[k]], 3);
                foreach (var t in toks)
                {
                    clh.Put(ref w, t.sym);
                    if (t.bits > 0) w.Put(t.extra, t.bits);
                }
            }
        }
    }
}
