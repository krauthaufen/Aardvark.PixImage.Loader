using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Aardvark.ImageCodecs.JpegXl
{
    /// <summary>
    /// An MA tree being built by the encoder: decision nodes (property &gt;= 0, left taken when property &gt; value) and
    /// leaves (predictor, context). <see cref="Finish"/> puts the nodes in the breadth first order of the bitstream and
    /// numbers the leaf contexts as the decoder does.
    /// </summary>
    internal sealed class JxlEncTree
    {
        public readonly List<int> Prop = new(), Val = new(), Left = new(), Right = new(), Pred = new();
        public int[] Ctx = Array.Empty<int>();
        public int NumLeaves;

        public int AddLeaf(int predictor)
        {
            Prop.Add(-1); Val.Add(0); Left.Add(0); Right.Add(0); Pred.Add(predictor);
            return Prop.Count - 1;
        }

        public int AddSplit(int property, int value, int left, int right)
        {
            Prop.Add(property); Val.Add(value); Left.Add(left); Right.Add(right); Pred.Add(0);
            return Prop.Count - 1;
        }

        public void SetSplit(int node, int property, int value, int left, int right)
        {
            Prop[node] = property; Val[node] = value; Left[node] = left; Right[node] = right;
        }

        /// <summary>Reorders the nodes breadth first from the given root and numbers the leaves.</summary>
        public JxlEncTree Finish(int root)
        {
            var t = new JxlEncTree();
            var q = new Queue<(int src, int dst)>();
            int Add(int src)
            {
                int d = t.Prop.Count;
                t.Prop.Add(Prop[src]); t.Val.Add(Val[src]); t.Left.Add(0); t.Right.Add(0); t.Pred.Add(Pred[src]);
                q.Enqueue((src, d));
                return d;
            }
            Add(root);
            while (q.Count > 0)
            {
                var (s, d) = q.Dequeue();
                if (Prop[s] < 0) continue;
                // children get consecutive indices in the order they are read: left (property > value) first
                int l = Add(Left[s]);
                int r = Add(Right[s]);
                t.Left[d] = l; t.Right[d] = r;
            }
            t.Ctx = new int[t.Prop.Count];
            int leaves = 0;
            for (int i = 0; i < t.Prop.Count; i++) t.Ctx[i] = t.Prop[i] < 0 ? leaves++ : -1;
            t.NumLeaves = leaves;
            return t;
        }

        public int MaxProperty()
        {
            int m = -1;
            foreach (var p in Prop) m = Math.Max(m, p);
            return m;
        }

        /// <summary>Writes the tree (its own entropy code over 6 contexts, then the nodes) as <see cref="JxlMaTree.Read"/> reads it.</summary>
        public void Write(JxlBitWriter w)
        {
            var tok = new JxlTokens(Prop.Count * 3 + 8);
            for (int i = 0; i < Prop.Count; i++)
            {
                if (Prop[i] >= 0)
                {
                    tok.Add(1, (uint)(Prop[i] + 1));
                    tok.Add(0, JxlBitWriter.PackSigned(Val[i]));
                }
                else
                {
                    tok.Add(1, 0);
                    tok.Add(2, (uint)Pred[i]);
                    tok.Add(3, 0);   // offset
                    tok.Add(4, 0);   // multiplier log
                    tok.Add(5, 0);   // multiplier bits
                }
            }
            var enc = new JxlEntropyEncoder(6) { MaxClusters = 6 };
            enc.Build(new[] { tok }, false);
            enc.WriteCode(w);
            enc.WriteTokens(w, tok);
        }

        /// <summary>The tree for one channel of one stream, with the decisions on properties 0 and 1 resolved.</summary>
        public JxlSpecTree Specialize(int channel, int stream)
        {
            var map = new List<int>();
            var prop = new List<int>(); var val = new List<int>(); var left = new List<int>(); var right = new List<int>();
            var pred = new List<int>(); var ctx = new List<int>();
            var q = new Queue<(int src, int dst)>();
            int Resolve(int n)
            {
                while (Prop[n] == 0 || Prop[n] == 1)
                {
                    int pv = Prop[n] == 0 ? channel : stream;
                    n = pv > Val[n] ? Left[n] : Right[n];
                }
                return n;
            }
            int Add(int src)
            {
                int d = prop.Count;
                prop.Add(Prop[src]); val.Add(Val[src]); left.Add(0); right.Add(0); pred.Add(Pred[src]); ctx.Add(Ctx[src]);
                q.Enqueue((src, d));
                return d;
            }
            Add(Resolve(0));
            while (q.Count > 0)
            {
                var (s, d) = q.Dequeue();
                if (Prop[s] < 0) continue;
                left[d] = Add(Resolve(Left[s]));
                right[d] = Add(Resolve(Right[s]));
            }
            return new JxlSpecTree(prop.ToArray(), val.ToArray(), left.ToArray(), right.ToArray(), pred.ToArray(), ctx.ToArray());
        }
    }

    /// <summary>A tree specialized to one channel (no decisions on channel / stream left).</summary>
    internal sealed class JxlSpecTree
    {
        public readonly int[] Prop, Val, Left, Right, Pred, Ctx;
        public readonly int MaxProp;
        public readonly bool UsesWp;

        public JxlSpecTree(int[] prop, int[] val, int[] left, int[] right, int[] pred, int[] ctx)
        {
            Prop = prop; Val = val; Left = left; Right = right; Pred = pred; Ctx = ctx;
            int m = -1;
            bool wp = false;
            for (int i = 0; i < prop.Length; i++)
            {
                m = Math.Max(m, prop[i]);
                if (prop[i] == 15 || prop[i] < 0 && pred[i] == 6) wp = true;
            }
            MaxProp = m; UsesWp = wp;
        }

        public bool IsSingleLeaf => Prop[0] < 0;

        /// <summary>Whether every decision tests one property (then the leaves form intervals of it).</summary>
        public bool OnlyProperty(int property, out int[] bounds, out int[] leaves)
        {
            bounds = leaves = Array.Empty<int>();
            for (int i = 0; i < Prop.Length; i++)
                if (Prop[i] >= 0 && Prop[i] != property) return false;
            if (Prop.Length > 4096) return false;
            var b = new List<int>(); var l = new List<int>();
            void Walk(int n, long lo, long hi)
            {
                if (lo > hi) return;
                if (Prop[n] < 0) { b.Add((int)hi); l.Add(n); return; }
                long v = Val[n];
                Walk(Right[n], lo, Math.Min(hi, v));
                Walk(Left[n], Math.Max(lo, v + 1), hi);
            }
            Walk(0, int.MinValue, int.MaxValue);
            bounds = b.ToArray(); leaves = l.ToArray();
            return true;
        }
    }
}
