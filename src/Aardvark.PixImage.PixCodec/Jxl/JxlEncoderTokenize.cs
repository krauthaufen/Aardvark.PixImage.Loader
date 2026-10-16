using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using static Aardvark.ImageCodecs.JpegXl.JxlEncPredict;

namespace Aardvark.ImageCodecs.JpegXl
{
    /// <summary>Turns modular channels into tokens (leaf context, packed residual) with a specialized MA tree.</summary>
    internal static class JxlModularTokenizer
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static uint Pack(long v, long pred) => JxlBitWriter.PackSigned(unchecked((int)(v - pred)));

        public static void EncodeChannel(List<JxlChannel> channels, int index, JxlSpecTree t, JxlWpHeader wph, JxlTokens tok)
        {
            var ch = channels[index];
            if (ch.W == 0 || ch.H == 0) return;
            tok.Reserve(ch.W * ch.H);
            if (t.IsSingleLeaf && t.Pred[0] != 6) { SingleLeaf(ch, t.Pred[0], t.Ctx[0], tok); return; }
            if (t.UsesWp && t.OnlyProperty(15, out var bounds, out var leaves)) { WpIntervals(ch, t, wph, bounds, leaves, tok); return; }
            if (!t.UsesWp && TwoPropLut(ch, t, tok)) return;
            if (!t.UsesWp && t.MaxProp < JxlMaTree.NumNonRefProperties && Array.IndexOf(t.Prop, 8) < 0) { NoWp(ch, t, tok); return; }
            General(channels, index, t, wph, tok);
        }

        static void SingleLeaf(JxlChannel ch, int pred, int ctx, JxlTokens tok)
        {
            int w = ch.W, h = ch.H;
            var d = ch.Data;
            var cv = tok.Ctx; var vv = tok.Val;
            int n = tok.Count;
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    long p;
                    switch (pred)
                    {
                        case 0: p = 0; break;
                        case 1: p = x > 0 ? d[row + x - 1] : y > 0 ? d[row - w + x] : 0; break;
                        case 2: p = y > 0 ? d[row - w + x] : x > 0 ? d[row + x - 1] : 0; break;
                        case 5:
                            if (x > 0 && y > 0) p = ClampedGradient(d[row + x - 1], d[row - w + x], d[row - w + x - 1]);
                            else p = x > 0 ? d[row + x - 1] : y > 0 ? d[row - w + x] : 0;
                            break;
                        default:
                            {
                                Neighbours(d, w, x, y, out long W, out long N, out long NW, out long NE, out long NN, out long WW, out long NEE);
                                p = Predict(pred, W, N, NW, NE, NN, WW, NEE, 0);
                                break;
                            }
                    }
                    cv[n] = ctx; vv[n] = Pack(d[row + x], p); n++;
                }
            }
            tok.Count = n;
        }

        static void WpIntervals(JxlChannel ch, JxlSpecTree t, JxlWpHeader wph, int[] bounds, int[] leaves, JxlTokens tok)
        {
            int w = ch.W, h = ch.H;
            var d = ch.Data;
            int nl = leaves.Length;
            var lpred = new int[nl]; var lctx = new int[nl];
            for (int k = 0; k < nl; k++) { lpred[k] = t.Pred[leaves[k]]; lctx[k] = t.Ctx[leaves[k]]; }
            var wp = new JxlEncWp(wph, w);
            var cv = tok.Ctx; var vv = tok.Val;
            int n = tok.Count;
            for (int y = 0; y < h; y++)
            {
                wp.StartRow(y);
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    Neighbours(d, w, x, y, out long W, out long N, out long NW, out long NE, out long NN, out long WW, out long NEE);
                    long wpPred = wp.Predict(x, N, W, NE, NW, NN);
                    int me = wp.MaxError;
                    int k = 0;
                    while (me > bounds[k]) k++;
                    long v = d[row + x];
                    long p = lpred[k] == 6 ? wpPred : Predict(lpred[k], W, N, NW, NE, NN, WW, NEE, wpPred);
                    cv[n] = lctx[k]; vv[n] = Pack(v, p); n++;
                    wp.Update(x, v);
                }
            }
            tok.Count = n;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static long DiffProp(int p, long W, long N, long NW, long NE, long NN, long WW) => p switch
        {
            10 => W - NW, 11 => NW - N, 12 => N - NE, 13 => N - NN, _ => W - WW,
        };

        /// <summary>
        /// Trees deciding on at most two of the difference properties 10..14 (the fixed trees of the fast efforts): the
        /// leaf of every pair of intervals comes from a table, the intervals from a lookup over the clamped values.
        /// </summary>
        static bool TwoPropLut(JxlChannel ch, JxlSpecTree t, JxlTokens tok)
        {
            var props = new List<int>();
            var thr = new List<SortedSet<int>>();
            for (int i = 0; i < t.Prop.Length; i++)
            {
                int p = t.Prop[i];
                if (p < 0) continue;
                if (p < 10 || p > 14) return false;
                int k = props.IndexOf(p);
                if (k < 0) { if (props.Count == 2) return false; props.Add(p); thr.Add(new SortedSet<int>()); k = props.Count - 1; }
                thr[k].Add(t.Val[i]);
            }
            if (props.Count == 0) return false;
            if (props.Count == 1) { props.Add(props[0]); thr.Add(new SortedSet<int>(thr[0])); }
            var ta = new List<int>(thr[0]); var tb = new List<int>(thr[1]);
            int na = ta.Count + 1, nb = tb.Count + 1;
            // lookups value -> interval over [min threshold, max threshold + 1]
            int[] Lut(List<int> th, out int off)
            {
                off = th[0];
                long span = (long)th[^1] - th[0] + 2;
                if (span > 1 << 18) return Array.Empty<int>();
                var l = new int[span];
                int k = 0;
                for (int v = 0; v < span; v++)
                {
                    while (k < th.Count && th[k] < v + off) k++;
                    l[v] = k;
                }
                return l;
            }
            var la = Lut(ta, out int oa); var lb = Lut(tb, out int ob);
            if (la.Length == 0 || lb.Length == 0) return false;
            int ma = la.Length - 1, mb = lb.Length - 1;
            // leaf of each interval pair, found by walking the tree with representative values
            var leafCtx = new int[na * nb]; var leafPred = new int[na * nb];
            bool allGradient = true;
            for (int ia = 0; ia < na; ia++)
                for (int ib = 0; ib < nb; ib++)
                {
                    int va = ia < ta.Count ? ta[ia] : ta[^1] + 1, vb = ib < tb.Count ? tb[ib] : tb[^1] + 1;
                    int node = 0;
                    while (t.Prop[node] >= 0)
                    {
                        int pv = t.Prop[node] == props[0] ? va : vb;
                        node = pv > t.Val[node] ? t.Left[node] : t.Right[node];
                    }
                    leafCtx[ia * nb + ib] = t.Ctx[node];
                    leafPred[ia * nb + ib] = t.Pred[node];
                    if (t.Pred[node] != 5) allGradient = false;
                }
            int pa = props[0], pb = props[1];
            int w = ch.W, h = ch.H;
            var d = ch.Data;
            var cv = tok.Ctx; var vv = tok.Val;
            int n = tok.Count;
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    Neighbours(d, w, x, y, out long W, out long N, out long NW, out long NE, out long NN, out long WW, out long NEE);
                    long a = (long)(int)DiffProp(pa, W, N, NW, NE, NN, WW) - oa, b = (long)(int)DiffProp(pb, W, N, NW, NE, NN, WW) - ob;
                    int ia = la[a < 0 ? 0 : a > ma ? ma : a], ib = lb[b < 0 ? 0 : b > mb ? mb : b];
                    int li = ia * nb + ib;
                    long p = allGradient ? ClampedGradient(W, N, NW) : Predict(leafPred[li], W, N, NW, NE, NN, WW, NEE, 0);
                    cv[n] = leafCtx[li]; vv[n] = Pack(d[row + x], p); n++;
                }
            }
            tok.Count = n;
            return true;
        }

        /// <summary>Trees without the weighted predictor and without reference properties.</summary>
        static void NoWp(JxlChannel ch, JxlSpecTree t, JxlTokens tok)
        {
            int w = ch.W, h = ch.H;
            var d = ch.Data;
            var prop = t.Prop; var val = t.Val; var left = t.Left; var right = t.Right; var lpred = t.Pred; var lctx = t.Ctx;
            var cv = tok.Ctx; var vv = tok.Val;
            int n = tok.Count;
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    Neighbours(d, w, x, y, out long W, out long N, out long NW, out long NE, out long NN, out long WW, out long NEE);
                    int node = 0;
                    while (prop[node] >= 0)
                    {
                        long pv = prop[node] switch
                        {
                            2 => y, 3 => x, 4 => Math.Abs(N), 5 => Math.Abs(W), 6 => N, 7 => W,
                            9 => (int)(W + N - NW), 10 => W - NW, 11 => NW - N, 12 => N - NE, 13 => N - NN, 14 => W - WW,
                            _ => 0,
                        };
                        node = (int)pv > val[node] ? left[node] : right[node];
                    }
                    long p = Predict(lpred[node], W, N, NW, NE, NN, WW, NEE, 0);
                    cv[n] = lctx[node]; vv[n] = Pack(d[row + x], p); n++;
                }
            }
            tok.Count = n;
        }

        /// <summary>Reference channels for properties 16+: earlier channels of the same geometry, nearest first.</summary>
        public static List<int[]> RefChannels(List<JxlChannel> channels, int index, int maxProp)
        {
            var ch = channels[index];
            int numRefs = maxProp >= JxlMaTree.NumNonRefProperties ? (maxProp - JxlMaTree.NumNonRefProperties) / 4 + 1 : 0;
            var refs = new List<int[]>();
            for (int j = index - 1; j >= 0 && refs.Count < numRefs; j--)
            {
                var c = channels[j];
                if (c.W != ch.W || c.H != ch.H || c.HShift != ch.HShift || c.VShift != ch.VShift) continue;
                refs.Add(c.Data);
            }
            return refs;
        }

        static void General(List<JxlChannel> channels, int index, JxlSpecTree t, JxlWpHeader wph, JxlTokens tok)
        {
            var ch = channels[index];
            int w = ch.W, h = ch.H;
            var d = ch.Data;
            var wp = t.UsesWp ? new JxlEncWp(wph, w) : null;
            var refs = RefChannels(channels, index, t.MaxProp);
            var prop = t.Prop; var val = t.Val; var left = t.Left; var right = t.Right; var lpred = t.Pred; var lctx = t.Ctx;
            var cv = tok.Ctx; var vv = tok.Val;
            int n = tok.Count;
            for (int y = 0; y < h; y++)
            {
                wp?.StartRow(y);
                long lastProp9 = 0;
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    Neighbours(d, w, x, y, out long W, out long N, out long NW, out long NE, out long NN, out long WW, out long NEE);
                    long wpPred = 0;
                    if (wp != null) wpPred = wp.Predict(x, N, W, NE, NW, NN);
                    long prop9 = (int)(W + N - NW);
                    int node = 0;
                    while (prop[node] >= 0)
                    {
                        int p = prop[node];
                        long pv = PropertyValue(p, x, y, W, N, NW, NE, NN, WW, lastProp9, prop9, wp, refs, row, w);
                        node = (int)pv > val[node] ? left[node] : right[node];
                    }
                    lastProp9 = prop9;
                    long v = d[row + x];
                    long pr = Predict(lpred[node], W, N, NW, NE, NN, WW, NEE, wpPred);
                    cv[n] = lctx[node]; vv[n] = Pack(v, pr); n++;
                    wp?.Update(x, v);
                }
            }
            tok.Count = n;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long PropertyValue(int p, int x, int y, long W, long N, long NW, long NE, long NN, long WW, long lastProp9, long prop9,
            JxlEncWp? wp, List<int[]> refs, int row, int w)
        {
            switch (p)
            {
                case 2: return y;
                case 3: return x;
                case 4: return Math.Abs(N);
                case 5: return Math.Abs(W);
                case 6: return N;
                case 7: return W;
                case 8: return (int)(W - lastProp9);
                case 9: return prop9;
                case 10: return W - NW;
                case 11: return NW - N;
                case 12: return N - NE;
                case 13: return N - NN;
                case 14: return W - WW;
                case 15: return wp != null ? wp.MaxError : 0;
                default:
                    {
                        int k = p - JxlMaTree.NumNonRefProperties;
                        int j = k >> 2;
                        if (j >= refs.Count) return 0;
                        var rd = refs[j];
                        long rv = rd[row + x];
                        if ((k & 2) != 0)
                        {
                            Neighbours(rd, w, x, y, out long rW, out long rN, out long rNW, out _, out _, out _, out _);
                            rv -= ClampedGradient(rW, rN, rNW);
                        }
                        return (k & 1) != 0 ? rv : Math.Abs(rv);
                    }
            }
        }
    }
}
