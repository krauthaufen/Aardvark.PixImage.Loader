using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using static Aardvark.ImageCodecs.JpegXl.JxlEncPredict;

namespace Aardvark.ImageCodecs.JpegXl
{
    /// <summary>A modular image to be coded as one stream: its channels and stream id (MA tree property 1).</summary>
    internal sealed class JxlStreamImage
    {
        public readonly List<JxlChannel> Channels;
        public readonly int StreamId;
        public JxlStreamImage(List<JxlChannel> channels, int streamId) { Channels = channels; StreamId = streamId; }
    }

    /// <summary>Settings of the MA tree learning.</summary>
    internal sealed class JxlTreeParams
    {
        public int[] Predictors = { 5 };
        public int[] Properties = { 0 };
        public double SampleFraction = 0.1;
        public int MaxSamples = 400_000;
        public int Quantiles = 32;
        public int MaxLeaves = 1024;
        public int MaxDepth = 24;
        public double SplitPenalty = 30;    // bits a split has to gain (in the full image)
        public int MinLeafSamples = 24;
        /// <summary>Children pick their own predictor when splits are evaluated (slower, better).</summary>
        public bool PerSidePredictor;
    }

    /// <summary>
    /// Learns an MA tree from samples of the images: greedy top down splits on quantized properties, each node keeping
    /// the predictor with the smallest estimated cost.
    /// </summary>
    internal static class JxlTreeLearner
    {
        sealed class Samples
        {
            public int NProps, NPreds, Count;
            public int[] Props = Array.Empty<int>();
            public uint[] Res = Array.Empty<uint>();

            public Samples(int np, int npr, int cap) { NProps = np; NPreds = npr; Props = new int[np * cap]; Res = new uint[npr * cap]; }

            public void Grow()
            {
                int cap = Math.Max(1024, Props.Length / Math.Max(1, NProps) * 2);
                Array.Resize(ref Props, cap * NProps);
                Array.Resize(ref Res, cap * NPreds);
            }

            public int Capacity => NProps == 0 ? Res.Length / NPreds : Props.Length / NProps;
        }

        const int TokAlpha = 72;   // tokens of the (4, 1, 0) hybrid uint config used for estimates

        static void Tokenize(uint v, out byte tok, out byte nb)
        {
            if (v < 16) { tok = (byte)v; nb = 0; return; }
            int e = 31 - System.Numerics.BitOperations.LeadingZeroCount(v);
            tok = (byte)(16 + ((e - 4) << 1) + ((v >> (e - 1)) & 1));
            nb = (byte)(e - 1);
        }

        public static JxlEncTree Learn(IReadOnlyList<JxlStreamImage> images, JxlTreeParams p, JxlWpHeader wph, bool parallel)
        {
            int np = p.Properties.Length, npr = p.Predictors.Length;
            bool needWp = Array.IndexOf(p.Predictors, 6) >= 0 || Array.IndexOf(p.Properties, 15) >= 0;
            int maxRefProp = -1;
            foreach (int q in p.Properties) maxRefProp = Math.Max(maxRefProp, q);
            long totalPixels = 0;
            foreach (var im in images) foreach (var c in im.Channels) totalPixels += (long)c.W * c.H;
            double frac = Math.Min(1.0, Math.Min(p.SampleFraction, (double)p.MaxSamples / Math.Max(1, totalPixels)));
            uint threshold = (uint)Math.Min(uint.MaxValue, frac * 4294967296.0);
            var parts = new Samples[images.Count];
            void Collect(int i)
            {
                var im = images[i];
                long px = 0;
                foreach (var c in im.Channels) px += (long)c.W * c.H;
                var s = new Samples(np, npr, (int)Math.Min(int.MaxValue / Math.Max(np, npr) / 2, Math.Max(256, (long)(px * frac * 1.1) + 64)));
                uint rng = (uint)(i * 2654435761u + 12345u);
                for (int ci = 0; ci < im.Channels.Count; ci++)
                {
                    var ch = im.Channels[ci];
                    if (ch.W == 0 || ch.H == 0) continue;
                    CollectChannel(im.Channels, ci, im.StreamId, p, wph, needWp, maxRefProp, threshold, ref rng, s);
                }
                parts[i] = s;
            }
            if (parallel && images.Count > 1) Parallel.For(0, images.Count, Collect);
            else for (int i = 0; i < images.Count; i++) Collect(i);
            JxlProfile.Mark("learn: collect");
            int n = 0;
            foreach (var s in parts) n += s.Count;
            var all = new Samples(np, npr, Math.Max(1, n));
            int o = 0;
            foreach (var s in parts)
            {
                Array.Copy(s.Props, 0, all.Props, o * np, s.Count * np);
                Array.Copy(s.Res, 0, all.Res, o * npr, s.Count * npr);
                o += s.Count;
            }
            all.Count = n;
            double scale = n > 0 ? (double)totalPixels / n : 1;
            return Build(all, p, scale);
        }

        static void CollectChannel(List<JxlChannel> chans, int index, int stream, JxlTreeParams p, JxlWpHeader wph, bool needWp, int maxProp,
            uint threshold, ref uint rng, Samples s)
        {
            var ch = chans[index];
            int w = ch.W, h = ch.H;
            var d = ch.Data;
            var wp = needWp ? new JxlEncWp(wph, w) : null;
            var refs = JxlModularTokenizer.RefChannels(chans, index, maxProp);
            var props = p.Properties; var preds = p.Predictors;
            int np = props.Length, npr = preds.Length;
            for (int y = 0; y < h; y++)
            {
                wp?.StartRow(y);
                long lastProp9 = 0;
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    rng = rng * 1664525u + 1013904223u;
                    bool take = rng < threshold;
                    if (!take && wp == null)
                    {
                        if (Array.IndexOf(props, 8) >= 0)
                        {
                            Neighbours(d, w, x, y, out long W0, out long N0, out long NW0, out _, out _, out _, out _);
                            lastProp9 = (int)(W0 + N0 - NW0);
                        }
                        continue;
                    }
                    Neighbours(d, w, x, y, out long W, out long N, out long NW, out long NE, out long NN, out long WW, out long NEE);
                    long wpPred = 0;
                    if (wp != null) wpPred = wp.Predict(x, N, W, NE, NW, NN);
                    long prop9 = (int)(W + N - NW);
                    long v = d[row + x];
                    if (take)
                    {
                        if (s.Count >= s.Capacity) s.Grow();
                        int b = s.Count * np;
                        for (int k = 0; k < np; k++)
                        {
                            int q = props[k];
                            s.Props[b + k] = q == 0 ? index : q == 1 ? stream
                                : (int)JxlModularTokenizer.PropertyValue(q, x, y, W, N, NW, NE, NN, WW, lastProp9, prop9, wp, refs, row, w);
                        }
                        int rb = s.Count * npr;
                        for (int k = 0; k < npr; k++)
                        {
                            long pr = Predict(preds[k], W, N, NW, NE, NN, WW, NEE, wpPred);
                            s.Res[rb + k] = JxlBitWriter.PackSigned(unchecked((int)(v - pr)));
                        }
                        s.Count++;
                    }
                    lastProp9 = prop9;
                    wp?.Update(x, v);
                }
            }
        }

        // ------------------------------------------------------------------ tree construction

        static double Entropy(int[] h, int off, int n, int total)
        {
            if (total <= 0) return 0;
            double bits = JxlEncMath.XLogX(total);
            for (int i = 0; i < n; i++) bits -= JxlEncMath.XLogX(h[off + i]);
            return bits;
        }

        static JxlEncTree Build(Samples s, JxlTreeParams p, double scale)
        {
            int n = s.Count, np = s.NProps, npr = s.NPreds;
            var tree = new JxlEncTree();
            if (n == 0)
            {
                int leaf = tree.AddLeaf(p.Predictors.Length > 0 ? p.Predictors[0] : 0);
                return tree.Finish(leaf);
            }
            // tokens of the residuals per predictor
            var tok = new byte[npr][]; var nbits = new byte[npr][];
            for (int k = 0; k < npr; k++)
            {
                tok[k] = new byte[n]; nbits[k] = new byte[n];
                for (int i = 0; i < n; i++) Tokenize(s.Res[i * npr + k], out tok[k][i], out nbits[k][i]);
            }
            // quantized properties
            var thr = new int[np][];
            var bucket = new byte[np][];
            for (int k = 0; k < np; k++)
            {
                int stride = Math.Max(1, n / 20000);
                var vals = new int[(n + stride - 1) / stride];
                for (int i = 0, j = 0; i < n; i += stride, j++) vals[j] = s.Props[i * np + k];
                thr[k] = Thresholds(vals, p.Properties[k] == 0 ? 255 : p.Quantiles);
                var tk = thr[k];
                var bk = new byte[n];
                for (int i = 0; i < n; i++)
                {
                    int v = s.Props[i * np + k];
                    int lo = 0, hi = tk.Length;   // number of thresholds < v
                    while (lo < hi) { int m = (lo + hi) >> 1; if (tk[m] < v) lo = m + 1; else hi = m; }
                    bk[i] = (byte)lo;
                }
                bucket[k] = bk;
            }
            JxlProfile.Mark("learn: quantize");
            var idx = new int[n];
            for (int i = 0; i < n; i++) idx[i] = i;
            // best first: nodes wait in a queue ordered by the gain of their best split
            var queue = new PriorityQueue<(int lo, int hi, int node, int depth, int k, int b), double>();
            int root = tree.AddLeaf(0);
            int leaves = 1;
            int maxB = 1;
            foreach (var t in thr) maxB = Math.Max(maxB, t.Length + 1);
            var hist = new int[maxB * TokAlpha * (p.PerSidePredictor ? npr : 1)];
            var cumL = new int[TokAlpha * npr];
            var cumR = new int[TokAlpha * npr];
            var predHist = new int[TokAlpha];
            var extraB = new long[maxB * npr];
            var rowCount = new int[maxB];
            var nodeTot = new int[TokAlpha];
            var act = new int[TokAlpha];
            double penalty = p.SplitPenalty / scale;
            void Evaluate(int lo, int hi, int node, int depth)
            {
                int cnt = hi - lo;
                // best predictor of the node
                int bestP = 0;
                double bestPc = double.MaxValue;
                for (int k = 0; k < npr; k++)
                {
                    Array.Clear(predHist);
                    long extra = 0;
                    var tk = tok[k]; var nk = nbits[k];
                    for (int i = lo; i < hi; i++) { int si = idx[i]; predHist[tk[si]]++; extra += nk[si]; }
                    double c = JxlEncMath.XLogX(cnt) + extra;
                    for (int t = 0; t < TokAlpha; t++) c -= JxlEncMath.XLogX(predHist[t]);
                    if (c < bestPc) { bestPc = c; bestP = k; }
                }
                tree.Pred[node] = p.Predictors[bestP];
                if (cnt < 2 * p.MinLeafSamples || depth >= p.MaxDepth) return;
                double nodeCost = bestPc;
                Array.Clear(nodeTot);
                {
                    var tk = tok[bestP];
                    for (int i = lo; i < hi; i++) nodeTot[tk[idx[i]]]++;
                }
                int na = 0;
                double nodeEnt = JxlEncMath.XLogX(cnt);
                for (int t = 0; t < TokAlpha; t++) if (nodeTot[t] != 0) { act[na++] = t; nodeEnt -= JxlEncMath.XLogX(nodeTot[t]); }
                double nodeExtra = nodeCost - nodeEnt;
                double bestCost = double.MaxValue;
                int bestK = -1, bestB = -1;
                for (int k = 0; k < np; k++)
                {
                    int nb = thr[k].Length + 1;
                    if (nb < 2) continue;
                    var bk = bucket[k];
                    if (!p.PerSidePredictor)
                    {
                        Array.Clear(hist, 0, nb * TokAlpha);
                        Array.Clear(rowCount, 0, nb);
                        var tk = tok[bestP];
                        for (int i = lo; i < hi; i++) { int si = idx[i]; int b = bk[si]; hist[b * TokAlpha + tk[si]]++; rowCount[b]++; }
                        // right = buckets 0..b, left = b+1..
                        for (int a = 0; a < na; a++) cumR[act[a]] = 0;
                        int nr = 0;
                        for (int b = 0; b < nb - 1; b++)
                        {
                            int added = rowCount[b];
                            if (added == 0) continue;
                            int bo = b * TokAlpha;
                            nr += added;
                            double sr = 0, sl = 0;
                            for (int a = 0; a < na; a++)
                            {
                                int t = act[a];
                                int r = cumR[t] += hist[bo + t];
                                sr += JxlEncMath.XLogX(r);
                                sl += JxlEncMath.XLogX(nodeTot[t] - r);
                            }
                            if (nr < p.MinLeafSamples) continue;
                            int nl = cnt - nr;
                            if (nl < p.MinLeafSamples) break;
                            double c = JxlEncMath.XLogX(nr) - sr + JxlEncMath.XLogX(nl) - sl + nodeExtra;
                            if (c < bestCost) { bestCost = c; bestK = k; bestB = b; }
                        }
                    }
                    else
                    {
                        Array.Clear(hist, 0, nb * TokAlpha * npr);
                        Array.Clear(extraB, 0, nb * npr);
                        for (int i = lo; i < hi; i++)
                        {
                            int si = idx[i];
                            int b = bk[si];
                            for (int q = 0; q < npr; q++)
                            {
                                hist[(q * nb + b) * TokAlpha + tok[q][si]]++;
                                extraB[q * nb + b] += nbits[q][si];
                            }
                        }
                        // per predictor cumulative over buckets; each side takes its best predictor
                        Array.Clear(cumR, 0, TokAlpha * npr);
                        var exR = new long[npr];
                        var exT = new long[npr];
                        var tot = new int[TokAlpha * npr];
                        for (int q = 0; q < npr; q++)
                            for (int b = 0; b < nb; b++)
                            {
                                exT[q] += extraB[q * nb + b];
                                for (int t = 0; t < TokAlpha; t++) tot[q * TokAlpha + t] += hist[(q * nb + b) * TokAlpha + t];
                            }
                        int nr = 0;
                        for (int b = 0; b < nb - 1; b++)
                        {
                            int added = 0;
                            for (int q = 0; q < npr; q++)
                            {
                                int bo = (q * nb + b) * TokAlpha;
                                for (int t = 0; t < TokAlpha; t++) { int v = hist[bo + t]; cumR[q * TokAlpha + t] += v; if (q == 0) added += v; }
                                exR[q] += extraB[q * nb + b];
                            }
                            nr += added;
                            if (added == 0 || nr < p.MinLeafSamples) continue;
                            int nl = cnt - nr;
                            if (nl < p.MinLeafSamples) break;
                            double cr = double.MaxValue, cl = double.MaxValue;
                            for (int q = 0; q < npr; q++)
                            {
                                double r = Entropy(cumR, q * TokAlpha, TokAlpha, nr) + exR[q];
                                if (r < cr) cr = r;
                                for (int t = 0; t < TokAlpha; t++) cumL[t] = tot[q * TokAlpha + t] - cumR[q * TokAlpha + t];
                                double l = Entropy(cumL, 0, TokAlpha, nl) + (exT[q] - exR[q]);
                                if (l < cl) cl = l;
                            }
                            double c = cr + cl;
                            if (c < bestCost) { bestCost = c; bestK = k; bestB = b; }
                        }
                    }
                }
                if (bestK < 0 || nodeCost - bestCost <= penalty) return;
                queue.Enqueue((lo, hi, node, depth, bestK, bestB), -(nodeCost - bestCost));
            }
            Evaluate(0, n, root, 0);
            while (queue.Count > 0 && leaves < p.MaxLeaves)
            {
                var (lo, hi, node, depth, bestK, bestB) = queue.Dequeue();
                // split: left (property > threshold) = buckets > b
                var bkk = bucket[bestK];
                int iL = lo, iR = hi - 1;
                while (iL <= iR)
                {
                    if (bkk[idx[iL]] > bestB) iL++;
                    else { (idx[iL], idx[iR]) = (idx[iR], idx[iL]); iR--; }
                }
                int mid = iL;   // [lo, mid) left, [mid, hi) right
                if (mid == lo || mid == hi) continue;
                int left = tree.AddLeaf(0), right = tree.AddLeaf(0);
                tree.SetSplit(node, p.Properties[bestK], thr[bestK][bestB], left, right);
                leaves++;
                Evaluate(lo, mid, left, depth + 1);
                Evaluate(mid, hi, right, depth + 1);
            }
            if (JxlProfile.On) Console.Error.WriteLine($"  tree: {n} samples, {leaves} leaves");
            return tree.Finish(root);
        }

        /// <summary>Up to q split thresholds at quantiles of the values (distinct, increasing).</summary>
        static int[] Thresholds(int[] vals, int q)
        {
            var v = (int[])vals.Clone();
            Array.Sort(v);
            var list = new List<int>();
            int n = v.Length;
            // all distinct values when there are few
            var distinct = new List<int>();
            for (int i = 0; i < n && distinct.Count <= q; i++) if (i == 0 || v[i] != v[i - 1]) distinct.Add(v[i]);
            if (distinct.Count <= q)
            {
                for (int i = 0; i + 1 < distinct.Count; i++) list.Add(distinct[i]);
                return list.ToArray();
            }
            for (int k = 1; k < q; k++)
            {
                int t = v[(int)((long)k * n / q)];
                if (t == v[n - 1]) t--;   // keep a non empty left side
                if (list.Count == 0 || t > list[^1]) list.Add(t);
            }
            // zero and minus one are natural splits of signed properties
            if (v[0] < 0 && v[n - 1] > 0)
            {
                if (!list.Contains(0)) list.Add(0);
                if (!list.Contains(-1)) list.Add(-1);
                list.Sort();
            }
            if (list.Count > 254) list.RemoveRange(254, list.Count - 254);
            return list.ToArray();
        }
    }
}
