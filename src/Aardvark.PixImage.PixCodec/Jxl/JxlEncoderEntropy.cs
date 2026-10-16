using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Aardvark.ImageCodecs.JpegXl
{
    /// <summary>
    /// The symbols of one entropy coded stream (one section's ANS state): integers in contexts, plus LZ77 copies. A copy is
    /// two entries: (context | <see cref="LzFlag"/>, length - min length) followed by (LZ77 context, distance code).
    /// </summary>
    internal sealed class JxlTokens
    {
        public const int LzFlag = 1 << 31;
        public int[] Ctx;
        public uint[] Val;
        public int Count;

        public JxlTokens(int capacity = 1024)
        {
            capacity = Math.Max(16, capacity);
            Ctx = new int[capacity];
            Val = new uint[capacity];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(int ctx, uint v)
        {
            if (Count == Ctx.Length) Grow();
            Ctx[Count] = ctx;
            Val[Count] = v;
            Count++;
        }

        public void Reserve(int extra)
        {
            if (Count + extra > Ctx.Length)
            {
                int n = Math.Max(Ctx.Length * 2, Count + extra);
                Array.Resize(ref Ctx, n);
                Array.Resize(ref Val, n);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void Grow()
        {
            int n = Ctx.Length * 2;
            Array.Resize(ref Ctx, n);
            Array.Resize(ref Val, n);
        }
    }

    /// <summary>Encoding side of the hybrid integer configuration.</summary>
    internal static class JxlHybridEnc
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Token(in JxlHybridUint c, uint v, out int nbits, out uint bits)
        {
            if (v < c.Split) { nbits = 0; bits = 0; return v; }
            int e = 31 - BitOperations.LeadingZeroCount(v);
            int msb = c.MsbInToken, lsb = c.LsbInToken;
            int n = e - msb - lsb;
            uint mid = (v >> (e - msb)) & ((1u << msb) - 1);
            uint low = v & ((1u << lsb) - 1);
            nbits = n;
            bits = (v >> lsb) & (uint)((1UL << n) - 1);
            return c.Split + ((uint)(e - c.SplitExponent) << (msb + lsb)) + (mid << lsb) + low;
        }

        /// <summary>Fine value buckets: exact below 64, above that the exponent and the two bits after the leading one.</summary>
        public const int FineBuckets = 64 + 26 * 4;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Fine(uint v)
        {
            if (v < 64) return (int)v;
            int e = 31 - BitOperations.LeadingZeroCount(v);
            return 64 + (e - 6) * 4 + (int)((v >> (e - 2)) & 3);
        }

        /// <summary>Token and extra bit count of a fine bucket under a config with split exponent &lt;= 6, msb &lt;= 2, lsb = 0.</summary>
        public static int FineToken(int fine, in JxlHybridUint c, out int nbits)
        {
            uint v;
            if (fine < 64) v = (uint)fine;
            else
            {
                int e = (fine - 64) / 4 + 6, top = (fine - 64) & 3;
                v = (1u << e) | ((uint)top << (e - 2));
            }
            return (int)Token(c, v, out nbits, out _);
        }
    }

    /// <summary>
    /// Builds and writes the entropy code (context map, hybrid uint configs, ANS distributions) for a set of token
    /// streams sharing it, then ANS-encodes each stream.
    /// </summary>
    internal sealed class JxlEntropyEncoder
    {
        public readonly int NumContexts;
        public byte[] ContextMap = Array.Empty<byte>();
        public int NumClusters;
        public int LogAlphaSize = 5;
        public JxlHybridUint[] Configs = Array.Empty<JxlHybridUint>();
        public int[][] Freqs = Array.Empty<int[]>();
        public int[] Shifts = Array.Empty<int>();
        int[][] reverse = Array.Empty<int[]>();   // per cluster: [cumulative freq + offset] -> ANS slot
        int[][] cumul = Array.Empty<int[]>();
        // LZ77
        public bool Lz77;
        public uint LzMinSymbol = 224, LzMinLength = 3;
        public JxlHybridUint LzLenConf = JxlHybridUint.Make(0, 0, 0);

        /// <summary>Configs that may be chosen per cluster (split exponent &lt;= 6, msb &lt;= 2, lsb = 0).</summary>
        public JxlHybridUint[] Candidates = DefaultCandidates;
        public int MaxClusters = 64;
        /// <summary>Search effort for clustering (0 = only merge identical / tiny histograms).</summary>
        public int ClusterEffort = 1;

        public static readonly JxlHybridUint[] DefaultCandidates =
        {
            JxlHybridUint.Make(4, 1, 0), JxlHybridUint.Make(4, 2, 0), JxlHybridUint.Make(4, 0, 0), JxlHybridUint.Make(3, 1, 0),
            JxlHybridUint.Make(5, 2, 0), JxlHybridUint.Make(5, 1, 0), JxlHybridUint.Make(6, 2, 0), JxlHybridUint.Make(2, 0, 0),
            JxlHybridUint.Make(3, 2, 0), JxlHybridUint.Make(1, 0, 0), JxlHybridUint.Make(0, 0, 0), JxlHybridUint.Make(6, 1, 0),
        };

        public JxlEntropyEncoder(int numContexts) { NumContexts = numContexts; }

        int TotalContexts => Lz77 ? NumContexts + 1 : NumContexts;

        // ------------------------------------------------------------------ building

        /// <summary>Histograms (fine buckets) of every context over all streams; LZ77 length tokens separately.</summary>
        public void Build(IReadOnlyList<JxlTokens> streams, bool parallel)
        {
            int nctx = TotalContexts;
            int nb = JxlHybridEnc.FineBuckets;
            var hist = new long[nctx * nb];
            var lzHist = Lz77 ? new long[nctx * 64] : null;
            object gate = new();
            void Count(JxlTokens t, long[] h, long[]? lh)
            {
                var ctx = t.Ctx; var val = t.Val;
                for (int i = 0; i < t.Count; i++)
                {
                    int c = ctx[i];
                    if (c < 0)
                    {
                        c &= ~JxlTokens.LzFlag;
                        uint tok = JxlHybridEnc.Token(LzLenConf, val[i], out _, out _);
                        lh![c * 64 + (int)tok]++;
                        continue;
                    }
                    h[c * nb + JxlHybridEnc.Fine(val[i])]++;
                }
            }
            if (parallel && streams.Count > 1)
            {
                Parallel.For(0, streams.Count, () => (new long[nctx * nb], Lz77 ? new long[nctx * 64] : null),
                    (i, _, loc) => { Count(streams[i], loc.Item1, loc.Item2); return loc; },
                    loc =>
                    {
                        lock (gate)
                        {
                            for (int k = 0; k < hist.Length; k++) hist[k] += loc.Item1[k];
                            if (lzHist != null) for (int k = 0; k < lzHist.Length; k++) lzHist[k] += loc.Item2![k];
                        }
                    });
            }
            else foreach (var s in streams) Count(s, hist, lzHist);
            BuildFromHistograms(hist, lzHist);
        }

        /// <summary>Clusters the contexts, picks a hybrid uint config per cluster and quantizes the distributions.</summary>
        public void BuildFromHistograms(long[] hist, long[]? lzHist)
        {
            int nctx = TotalContexts;
            int nb = JxlHybridEnc.FineBuckets;
            // clustering on token histograms of a provisional config
            var prov = JxlHybridUint.Make(4, 1, 0);
            var fineTok = new int[nb];
            var fineBits = new int[nb];
            for (int f = 0; f < nb; f++) fineTok[f] = JxlHybridEnc.FineToken(f, prov, out fineBits[f]);
            int ntok = 0;
            for (int f = 0; f < nb; f++) ntok = Math.Max(ntok, fineTok[f] + 1);
            var ctxTok = new long[nctx][];
            for (int c = 0; c < nctx; c++)
            {
                var h = new long[ntok + (lzHist != null ? 64 : 0)];
                for (int f = 0; f < nb; f++) h[fineTok[f]] += hist[c * nb + f];
                if (lzHist != null) for (int k = 0; k < 64; k++) h[ntok + k] += lzHist[c * 64 + k];
                ctxTok[c] = h;
            }
            ContextMap = Cluster(ctxTok, out NumClusters);

            // per cluster: config choice and distribution
            Configs = new JxlHybridUint[NumClusters];
            var clusterFine = new long[NumClusters][];
            var clusterLz = new long[NumClusters][];
            for (int k = 0; k < NumClusters; k++) { clusterFine[k] = new long[nb]; clusterLz[k] = new long[64]; }
            for (int c = 0; c < nctx; c++)
            {
                int k = ContextMap[c];
                for (int f = 0; f < nb; f++) clusterFine[k][f] += hist[c * nb + f];
                if (lzHist != null) for (int t = 0; t < 64; t++) clusterLz[k][t] += lzHist[c * 64 + t];
            }
            int maxTok = 0;
            var tokHists = new long[NumClusters][];
            for (int k = 0; k < NumClusters; k++)
            {
                double best = double.MaxValue;
                JxlHybridUint bc = Candidates[0];
                foreach (var cand in Candidates)
                {
                    if (Lz77 && MaxLiteralToken(cand) >= LzMinSymbol) continue;
                    double cost = ConfigCost(clusterFine[k], cand);
                    if (cost < best - 1e-9) { best = cost; bc = cand; }
                }
                Configs[k] = bc;
                var th = new long[256];
                for (int f = 0; f < nb; f++)
                {
                    if (clusterFine[k][f] == 0) continue;
                    int t = JxlHybridEnc.FineToken(f, bc, out _);
                    th[t] += clusterFine[k][f];
                    maxTok = Math.Max(maxTok, t);
                }
                if (Lz77)
                    for (int t = 0; t < 64; t++)
                        if (clusterLz[k][t] != 0)
                        {
                            th[LzMinSymbol + t] += clusterLz[k][t];
                            maxTok = Math.Max(maxTok, (int)LzMinSymbol + t);
                        }
                tokHists[k] = th;
            }
            LogAlphaSize = 5;
            while ((1 << LogAlphaSize) <= maxTok) LogAlphaSize++;
            if (LogAlphaSize > 8) throw new InvalidOperationException("JPEG XL encoder: token alphabet too large");
            Freqs = new int[NumClusters][];
            Shifts = new int[NumClusters];
            for (int k = 0; k < NumClusters; k++)
            {
                Freqs[k] = JxlAnsEnc.Normalize(tokHists[k], 1 << LogAlphaSize);
                Shifts[k] = JxlAnsEnc.Choose(Freqs[k], tokHists[k], LogAlphaSize);
            }
            BuildTables();
        }

        static uint MaxLiteralToken(in JxlHybridUint c) => JxlHybridEnc.Token(c, uint.MaxValue, out _, out _);

        static double ConfigCost(long[] fine, in JxlHybridUint c)
        {
            Span<long> th = stackalloc long[256];
            th.Clear();
            double extra = 0;
            long total = 0;
            for (int f = 0; f < fine.Length; f++)
            {
                long n = fine[f];
                if (n == 0) continue;
                int t = JxlHybridEnc.FineToken(f, c, out int nb);
                if (t >= 256) return double.MaxValue;
                th[t] += n;
                extra += (double)n * nb;
                total += n;
            }
            double bits = extra + JxlEncMath.XLogX(total);
            int used = 0;
            for (int t = 0; t < 256; t++)
            {
                if (th[t] == 0) continue;
                used++;
                bits -= JxlEncMath.XLogX(th[t]);
            }
            return bits + used * 6;   // rough cost of the distribution in the header
        }

        /// <summary>Estimated cost in bits of coding the histogram with its own distribution (data + header).</summary>
        static double HistCost(long[] h)
        {
            long total = 0;
            int used = 0;
            for (int i = 0; i < h.Length; i++) if (h[i] != 0) { total += h[i]; used++; }
            if (total == 0) return 0;
            if (used == 1) return 8;
            double bits = JxlEncMath.XLogX(total);
            for (int i = 0; i < h.Length; i++) if (h[i] != 0) bits -= JxlEncMath.XLogX(h[i]);
            return bits + 20 + used * 5;
        }

        static double MergedCost(long[] a, long[] b)
        {
            long total = 0;
            int used = 0;
            for (int i = 0; i < a.Length; i++) { long n = a[i] + b[i]; if (n != 0) { total += n; used++; } }
            if (total == 0) return 0;
            if (used == 1) return 8;
            double bits = JxlEncMath.XLogX(total);
            for (int i = 0; i < a.Length; i++) { long n = a[i] + b[i]; if (n != 0) bits -= JxlEncMath.XLogX(n); }
            return bits + 20 + used * 5;
        }

        /// <summary>Greedy clustering: contexts by decreasing size join the cluster where they cost least, or open a new one.</summary>
        byte[] Cluster(long[][] h, out int numClusters)
        {
            int n = h.Length;
            var map = new byte[n];
            var order = new List<int>();
            var totals = new long[n];
            for (int c = 0; c < n; c++)
            {
                long t = 0;
                foreach (var v in h[c]) t += v;
                totals[c] = t;
                if (t > 0) order.Add(c);
            }
            if (order.Count == 0) { numClusters = 1; return map; }
            order.Sort((a, b) => totals[b].CompareTo(totals[a]));
            int alpha = h[0].Length;
            var cl = new List<long[]>();
            var clCost = new List<double>();
            var assign = new int[n];
            Array.Fill(assign, -1);
            int maxCl = Math.Min(256, Math.Max(1, MaxClusters));
            foreach (int c in order)
            {
                double own = HistCost(h[c]);
                int best = -1;
                double bestDelta = double.MaxValue;
                for (int k = 0; k < cl.Count; k++)
                {
                    double d = MergedCost(cl[k], h[c]) - clCost[k] - own;
                    if (d < bestDelta) { bestDelta = d; best = k; }
                }
                if (best >= 0 && (bestDelta < 0 || cl.Count >= maxCl))
                {
                    var t = cl[best];
                    for (int i = 0; i < alpha; i++) t[i] += h[c][i];
                    clCost[best] = HistCost(t);
                    assign[c] = best;
                }
                else
                {
                    cl.Add((long[])h[c].Clone());
                    clCost.Add(own);
                    assign[c] = cl.Count - 1;
                }
            }
            // refinement: move contexts to the cluster that codes them cheapest (against the cluster without them)
            for (int iter = 0; iter < ClusterEffort; iter++)
            {
                bool moved = false;
                foreach (int c in order)
                {
                    int cur = assign[c];
                    var hc = h[c];
                    double curCost = BitsUnder(cl[cur], hc, true);
                    int best = cur;
                    double bestCost = curCost;
                    for (int k = 0; k < cl.Count; k++)
                    {
                        if (k == cur) continue;
                        double cost = BitsUnder(cl[k], hc, false);
                        if (cost < bestCost - 1e-6) { bestCost = cost; best = k; }
                    }
                    if (best != cur)
                    {
                        // never empty a cluster completely (it would leave a hole in the map)
                        long rest = 0;
                        for (int i = 0; i < alpha; i++) rest += cl[cur][i] - hc[i];
                        if (rest <= 0) continue;
                        for (int i = 0; i < alpha; i++) { cl[cur][i] -= hc[i]; cl[best][i] += hc[i]; }
                        assign[c] = best;
                        moved = true;
                    }
                }
                if (!moved) break;
            }
            // compact: clusters in order of first use, so that the map is cheap to code
            var remap = new int[cl.Count];
            Array.Fill(remap, -1);
            int next = 0;
            for (int c = 0; c < n; c++)
            {
                int a = assign[c];
                if (a < 0) { map[c] = 0; continue; }
                if (remap[a] < 0) remap[a] = next++;
                map[c] = (byte)remap[a];
            }
            // empty contexts map to cluster 0 (any cluster works)
            numClusters = Math.Max(1, next);
            return map;
        }

        /// <summary>Bits to code histogram hc with the distribution of cluster cl (which contains hc when included).</summary>
        static double BitsUnder(long[] cl, long[] hc, bool included)
        {
            long total = 0;
            for (int i = 0; i < cl.Length; i++) total += cl[i] + (included ? 0 : hc[i]);
            double bits = 0, lt = Math.Log2(total);
            for (int i = 0; i < cl.Length; i++)
            {
                if (hc[i] == 0) continue;
                long n = cl[i] + (included ? 0 : hc[i]);
                bits += hc[i] * (lt - Math.Log2(n));
            }
            return bits;
        }

        uint[] flatFreq = Array.Empty<uint>();
        int[] flatCum = Array.Empty<int>();
        double[] flatInv = Array.Empty<double>();
        ushort[] flatRev = Array.Empty<ushort>();

        void BuildTables()
        {
            reverse = new int[NumClusters][];
            cumul = new int[NumClusters][];
            int asz0 = 1 << LogAlphaSize;
            flatFreq = new uint[NumClusters * asz0];
            flatCum = new int[NumClusters * asz0];
            flatInv = new double[NumClusters * asz0];
            flatRev = new ushort[NumClusters * JxlAnsTable.TabSize];
            for (int k = 0; k < NumClusters; k++)
            {
                var t = JxlAnsTable.Build(Freqs[k], LogAlphaSize);
                int asz = 1 << LogAlphaSize;
                var cum = new int[asz + 1];
                for (int s = 0; s < asz; s++) cum[s + 1] = cum[s] + Freqs[k][s];
                var rev = new int[JxlAnsTable.TabSize];
                for (int idx = 0; idx < JxlAnsTable.TabSize; idx++)
                {
                    int i = idx >> t.LogBucket;
                    int pos = idx & ((1 << t.LogBucket) - 1);
                    int sym, off;
                    if (pos >= t.Cutoff[i]) { sym = t.Symbol[i]; off = t.Offset[i] + pos; }
                    else { sym = i; off = pos; }
                    rev[cum[sym] + off] = idx;
                }
                reverse[k] = rev;
                cumul[k] = cum;
                for (int sy = 0; sy < asz; sy++)
                {
                    int f = Freqs[k][sy];
                    flatFreq[k * asz + sy] = (uint)f;
                    flatCum[k * asz + sy] = cum[sy];
                    flatInv[k * asz + sy] = f > 0 ? 1.0 / f : 0;
                }
                for (int x = 0; x < JxlAnsTable.TabSize; x++) flatRev[k * JxlAnsTable.TabSize + x] = (ushort)rev[x];
            }
        }

        // ------------------------------------------------------------------ writing the code

        /// <summary>Writes the entropy code as <see cref="JxlEntropyCode.Read"/> reads it.</summary>
        public void WriteCode(JxlBitWriter w)
        {
            w.WriteBool(Lz77);
            if (Lz77)
            {
                w.WriteU32(LzMinSymbol, JxlBitReader.Val(224), JxlBitReader.Val(512), JxlBitReader.Val(4096), JxlBitReader.BitsOffset(15, 8));
                w.WriteU32(LzMinLength, JxlBitReader.Val(3), JxlBitReader.Val(4), JxlBitReader.BitsOffset(2, 5), JxlBitReader.BitsOffset(8, 9));
                WriteConfig(w, LzLenConf, 8);
            }
            if (TotalContexts > 1) WriteContextMap(w, ContextMap, NumClusters);
            w.WriteBool(false);   // ANS, not prefix codes
            w.Write(2, (uint)(LogAlphaSize - 5));
            for (int k = 0; k < NumClusters; k++) WriteConfig(w, Configs[k], LogAlphaSize);
            for (int k = 0; k < NumClusters; k++) JxlAnsEnc.WriteDistribution(w, Freqs[k], LogAlphaSize, Shifts[k]);
        }

        static void WriteConfig(JxlBitWriter w, in JxlHybridUint c, int logAlpha)
        {
            w.Write(JxlHybridUint.CeilLog2(logAlpha + 1), (uint)c.SplitExponent);
            if (c.SplitExponent != logAlpha)
            {
                w.Write(JxlHybridUint.CeilLog2(c.SplitExponent + 1), (uint)c.MsbInToken);
                w.Write(JxlHybridUint.CeilLog2(c.SplitExponent - c.MsbInToken + 1), (uint)c.LsbInToken);
            }
        }

        /// <summary>A context map (D.3.5): simple when the cluster ids fit in few bits, else entropy coded (with or without MTF).</summary>
        public static void WriteContextMap(JxlBitWriter w, byte[] map, int numClusters)
        {
            int nbits = JxlHybridUint.CeilLog2(numClusters);
            double simpleCost = nbits <= 3 ? 3 + (double)nbits * map.Length : double.MaxValue;
            JxlBitWriter? best = null;
            if (map.Length > 1 && numClusters > 1)
            {
                foreach (bool mtf in new[] { false, true })
                {
                    var vals = mtf ? Mtf(map) : map;
                    var bw = new JxlBitWriter();
                    bw.WriteBool(false);   // not simple
                    bw.WriteBool(mtf);
                    var enc = new JxlEntropyEncoder(1) { MaxClusters = 1 };
                    var tok = new JxlTokens(vals.Length);
                    foreach (var v in vals) tok.Add(0, v);
                    enc.Build(new[] { tok }, false);
                    enc.WriteCode(bw);
                    enc.WriteTokens(bw, tok);
                    if (best == null || bw.BitLength < best.BitLength) best = bw;
                }
            }
            if (best == null || simpleCost <= best.BitLength)
            {
                w.WriteBool(true);
                w.Write(2, (uint)nbits);
                foreach (var v in map) w.Write(nbits, v);
                return;
            }
            w.Append(best);
        }

        static byte[] Mtf(byte[] map)
        {
            var order = new byte[256];
            for (int i = 0; i < 256; i++) order[i] = (byte)i;
            var r = new byte[map.Length];
            for (int i = 0; i < map.Length; i++)
            {
                byte v = map[i];
                int idx = Array.IndexOf(order, v);
                r[i] = (byte)idx;
                for (int k = idx; k > 0; k--) order[k] = order[k - 1];
                order[0] = v;
            }
            return r;
        }

        // ------------------------------------------------------------------ ANS encoding of a stream

        [ThreadStatic] static int[]? tlSlot;
        [ThreadStatic] static uint[]? tlBits;
        [ThreadStatic] static byte[]? tlNb;
        [ThreadStatic] static ushort[]? tlWords;
        [ThreadStatic] static bool[]? tlHasWord;

        static T[] Scratch<T>(ref T[]? a, int n) => a is { } x && x.Length >= n ? x : (a = new T[Math.Max(n, 1024)]);

        /// <summary>ANS-encodes one stream (its own state) as <see cref="JxlSymbolReader"/> decodes it.</summary>
        public unsafe void WriteTokens(JxlBitWriter w, JxlTokens t)
        {
            int ns = t.Count;
            // the ANS symbols in decode order (literal tokens, or LZ77 length tokens followed by distance tokens) as
            // slots cluster * alphabet + token, with their extra bits
            var slot = Scratch(ref tlSlot, ns);
            var bits = Scratch(ref tlBits, ns);
            var nbs = Scratch(ref tlNb, ns);
            var ctx = t.Ctx; var val = t.Val;
            int la = LogAlphaSize;
            var map = ContextMap;
            var cfg = Configs;
            for (int i = 0; i < ns; i++)
            {
                int c = ctx[i];
                uint tk;
                int k, nb;
                uint bv;
                if (c < 0)
                {
                    k = map[c & ~JxlTokens.LzFlag];
                    tk = LzMinSymbol + JxlHybridEnc.Token(LzLenConf, val[i], out nb, out bv);
                }
                else
                {
                    k = map[c];
                    tk = JxlHybridEnc.Token(cfg[k], val[i], out nb, out bv);
                }
                slot[i] = (k << la) | (int)tk;
                bits[i] = bv; nbs[i] = (byte)nb;
            }
            var words = Scratch(ref tlWords, ns);
            var has = Scratch(ref tlHasWord, ns);
            var fq = flatFreq; var cm = flatCum; var inv = flatInv; var rev = flatRev;
            uint state = 0x130000;
            int nw = 0;
            for (int i = ns - 1; i >= 0; i--)
            {
                int sl = slot[i];
                uint f = fq[sl];
                if (state >= (ulong)f << 20)
                {
                    words[nw++] = (ushort)state;
                    state >>= 16;
                    has[i] = true;
                }
                else has[i] = false;
                // state / f through the reciprocal, corrected by one where it rounded
                uint q = (uint)(state * inv[sl]);
                long r = (long)state - (long)q * f;
                if (r < 0) { q--; r += f; } else if (r >= f) { q++; r -= f; }
                state = (q << 12) | (uint)rev[((sl >> la) << JxlAnsTable.LogTabSize) + cm[sl] + (int)r];
            }
            w.Write(32, state);
            for (int i = 0; i < ns; i++)
            {
                if (has[i]) w.Write(16, words[--nw]);
                w.Write(nbs[i], bits[i]);
            }
        }

        /// <summary>Estimated bits of a stream under the built code (for comparisons between alternatives).</summary>
        public double EstimateBits(JxlTokens t)
        {
            double bits = 0;
            for (int i = 0; i < t.Count; i++)
            {
                int c = t.Ctx[i];
                int k, s, nb;
                if (c < 0) { k = ContextMap[c & ~JxlTokens.LzFlag]; s = (int)(LzMinSymbol + JxlHybridEnc.Token(LzLenConf, t.Val[i], out nb, out _)); }
                else { k = ContextMap[c]; s = (int)JxlHybridEnc.Token(Configs[k], t.Val[i], out nb, out _); }
                bits += nb + 12 - Math.Log2(Math.Max(1, Freqs[k][s]));
            }
            return bits;
        }
    }

    /// <summary>ANS distributions: normalization to 4096 and the header format of D.3.4.</summary>
    internal static class JxlAnsEnc
    {
        const int Tab = JxlAnsTable.TabSize;

        static readonly string[] LogCountCodes =
        {
            "10001", "1101", "1111", "1100", "1001", "1110", "001", "010", "101", "011", "000", "100001", "1000000", "1000001",
        };

        /// <summary>Frequencies summing to 4096 (each used symbol at least 1).</summary>
        public static int[] Normalize(long[] counts, int alphaSize)
        {
            var f = new int[alphaSize];
            long total = 0;
            int used = 0, maxIdx = -1;
            for (int i = 0; i < Math.Min(counts.Length, alphaSize); i++)
            {
                if (counts[i] <= 0) continue;
                total += counts[i];
                used++;
                if (maxIdx < 0 || counts[i] > counts[maxIdx]) maxIdx = i;
            }
            if (used == 0) { f[0] = Tab; return f; }
            if (used == 1) { f[maxIdx] = Tab; return f; }
            int sum = 0;
            for (int i = 0; i < alphaSize && i < counts.Length; i++)
            {
                if (counts[i] <= 0) continue;
                int v = (int)Math.Max(1, (long)((double)counts[i] * Tab / total + 0.5));
                f[i] = v;
                sum += v;
            }
            int diff = Tab - sum;
            if (diff >= 0) f[maxIdx] += diff;
            else
            {
                // too much: take from the largest entries
                while (diff < 0)
                {
                    int m = 0;
                    for (int i = 1; i < alphaSize; i++) if (f[i] > f[m]) m = i;
                    int take = Math.Min(-diff, f[m] - 1);
                    if (take <= 0) throw new InvalidOperationException("JPEG XL encoder: cannot normalize histogram");
                    f[m] -= take;
                    diff += take;
                }
            }
            return f;
        }

        static int LogCode(int f) => f == 0 ? 0 : 1 + (31 - BitOperations.LeadingZeroCount((uint)f));

        static int BitCount(int code, int shift) => Math.Min(code - 1, Math.Max(0, shift - ((JxlAnsTable.LogTabSize - (code - 1)) >> 1)));

        static void WriteU8(JxlBitWriter w, int v)
        {
            if (v == 0) { w.WriteBool(false); return; }
            w.WriteBool(true);
            int n = 31 - BitOperations.LeadingZeroCount((uint)v);
            w.Write(3, (uint)n);
            w.Write(n, (uint)(v - (1 << n)));
        }

        static int U8Bits(int v) => v == 0 ? 1 : 4 + (31 - BitOperations.LeadingZeroCount((uint)v));

        /// <summary>
        /// Picks the precision (shift) for a general distribution and quantizes the frequencies to it (in place), weighing
        /// the coding loss on the actual counts against the header bits. Simple and flat distributions stay as they are.
        /// </summary>
        public static int Choose(int[] freq, long[] counts, int logAlpha)
        {
            int alpha = 1 << logAlpha;
            int used = 0, last = -1, first = -1, second = -1;
            for (int i = 0; i < alpha; i++)
            {
                if (freq[i] == 0) continue;
                used++; last = i;
                if (first < 0) first = i; else if (second < 0) second = i;
            }
            if (used <= 1 || used == 2 && second < 256 || IsFlat(freq, last + 1)) return -1;
            int length = Math.Max(3, last + 1);
            int bestShift = -1;
            double bestCost = double.MaxValue;
            var work = new int[length];
            for (int shift = 13; shift >= 0; shift--)
            {
                if (!Quantize(freq, length, shift, work, out int omit)) continue;
                double cost = 0;
                for (int i = 0; i < length; i++)
                {
                    if (freq[i] == 0) continue;
                    long c = i < counts.Length ? counts[i] : 0;
                    if (c > 0 && work[i] != freq[i]) cost += c * Math.Log2((double)freq[i] / work[i]);
                    int code = LogCode(work[i]);
                    if (i != omit && code > 1) cost += BitCount(code, shift);
                }
                if (cost < bestCost) { bestCost = cost; bestShift = shift; }
            }
            if (bestShift < 0) throw new InvalidOperationException("JPEG XL encoder: no valid ANS distribution");
            Quantize(freq, length, bestShift, work, out _);
            Array.Copy(work, freq, length);
            return bestShift;
        }

        static bool IsFlat(int[] freq, int n)
        {
            if (n > 256 || n < 1) return false;
            for (int i = 0; i < n; i++) if (freq[i] != Tab / n + (i < Tab % n ? 1 : 0)) return false;
            for (int i = n; i < freq.Length; i++) if (freq[i] != 0) return false;
            return true;
        }

        /// <summary>Writes a distribution prepared by <see cref="Choose"/> (shift -1: simple or flat).</summary>
        public static void WriteDistribution(JxlBitWriter w, int[] freq, int logAlpha, int shift)
        {
            int alpha = 1 << logAlpha;
            int used = 0, first = -1, second = -1, last = -1;
            for (int i = 0; i < alpha; i++)
            {
                if (freq[i] == 0) continue;
                used++;
                last = i;
                if (first < 0) first = i; else if (second < 0) second = i;
            }
            if (used <= 1)
            {
                w.WriteBool(true); w.WriteBool(false);
                WriteU8(w, Math.Max(0, first));
                return;
            }
            if (used == 2 && second < 256)
            {
                w.WriteBool(true); w.WriteBool(true);
                WriteU8(w, first); WriteU8(w, second);
                w.Write(12, (uint)freq[first]);
                return;
            }
            if (IsFlat(freq, last + 1))
            {
                w.WriteBool(false); w.WriteBool(true);
                WriteU8(w, last);
                return;
            }
            int length = Math.Max(3, last + 1);
            int omit = -1, maxCode = -1;
            var codes = new int[length];
            for (int i = 0; i < length; i++)
            {
                codes[i] = LogCode(freq[i]);
                if (codes[i] > maxCode) { maxCode = codes[i]; omit = i; }
            }
            w.WriteBool(false); w.WriteBool(false);
            int s1 = shift + 1;
            int len = 31 - BitOperations.LeadingZeroCount((uint)s1);
            for (int i = 0; i < len; i++) w.WriteBool(true);
            if (len < 3) w.WriteBool(false);
            w.Write(len, (uint)(s1 - (1 << len)));
            WriteU8(w, length - 3);
            // runs: an RLE code at i repeats freq[i - 1] over rle[i] (4..259) entries; never right after or over the omitted one
            var rle = new int[length];
            for (int i = 1; i < length;)
            {
                if (i - 1 == omit) { i++; continue; }
                int j = i;
                while (j < length && j != omit && freq[j] == freq[i - 1] && j - i < 259) j++;
                if (j - i >= 4) { rle[i] = j - i; i = j; }
                else i++;
            }
            for (int i = 0; i < length; i++)
            {
                if (rle[i] > 0)
                {
                    WriteCodeBits(w, 13);
                    WriteU8(w, rle[i] - 4);
                    i += rle[i] - 1;
                    continue;
                }
                WriteCodeBits(w, codes[i]);
            }
            for (int i = 0; i < length; i++)
            {
                if (rle[i] > 0) { i += rle[i] - 1; continue; }
                int code = codes[i];
                if (i == omit || code <= 1) continue;
                int bc = BitCount(code, shift);
                int drop = code - 1 - bc;
                if (((freq[i] >> drop) << drop) != freq[i]) throw new InvalidOperationException("JPEG XL encoder: distribution not quantized");
                w.Write(bc, (uint)((freq[i] - (1 << (code - 1))) >> drop));
            }
        }

        static void WriteCodeBits(JxlBitWriter w, int v)
        {
            var c = LogCountCodes[v];
            for (int i = 0; i < c.Length; i++) w.WriteBool(c[i] == '1');
        }

        /// <summary>
        /// Rounds the frequencies to the precision of the shift (down, keeping the leading bit) and lets the omitted
        /// symbol (the first with the largest log count) take the rest. False when the result is not decodable.
        /// </summary>
        static bool Quantize(int[] freq, int length, int shift, int[] o, out int omit)
        {
            omit = -1;
            int maxCode = -1;
            for (int i = 0; i < length; i++)
            {
                int c = LogCode(freq[i]);
                if (c > maxCode) { maxCode = c; omit = i; }
            }
            int sum = 0;
            for (int i = 0; i < length; i++)
            {
                int f = freq[i];
                if (i == omit || f == 0) { o[i] = f; continue; }
                int code = LogCode(f);
                if (code > 1)
                {
                    int drop = code - 1 - BitCount(code, shift);
                    f = f >> drop << drop;
                }
                o[i] = f;
                sum += f;
            }
            if (sum >= Tab) return false;
            o[omit] = Tab - sum;
            int oc = LogCode(o[omit]);
            for (int i = 0; i < length; i++)
            {
                if (i == omit) continue;
                int c = LogCode(o[i]);
                if (i < omit ? c >= oc : c > oc) return false;
            }
            return oc <= 12;
        }
    }
}
