using System;
using System.Collections.Generic;

namespace Aardvark.ImageCodecs.JpegXl
{
    /// <summary>Meta-adaptive (MA) context tree of a modular image.</summary>
    internal sealed class JxlMaTree
    {
        // decision nodes: Property >= 0; leaves: Property = -1
        public int[] Property = Array.Empty<int>();
        public int[] Value = Array.Empty<int>();
        public int[] Left = Array.Empty<int>();     // taken when property > value
        public int[] Right = Array.Empty<int>();
        // leaves
        public int[] Predictor = Array.Empty<int>();
        public long[] Offset = Array.Empty<long>();
        public uint[] Multiplier = Array.Empty<uint>();
        public int[] Context = Array.Empty<int>();
        public int NumLeaves;
        public int Count;

        public const int NumNonRefProperties = 16;
        public const int MaxNodes = 1 << 22;

        public static JxlMaTree Read(JxlBitReader br, long maxNodes)
        {
            var code = JxlEntropyCode.Read(br, 6);
            var r = new JxlSymbolReader(code, br, 0);
            var prop = new List<int>(); var val = new List<int>(); var left = new List<int>(); var right = new List<int>();
            var pred = new List<int>(); var off = new List<long>(); var mul = new List<uint>(); var ctx = new List<int>();
            int toDecode = 1, nextIndex = 1, leaves = 0;
            long limit = Math.Min(maxNodes, MaxNodes);
            while (toDecode > 0)
            {
                if (prop.Count >= limit) throw new ImageFormatException("JPEG XL: MA tree too large");
                toDecode--;
                uint p = r.ReadUint(br, 1);
                if (p > 255) throw new ImageFormatException("JPEG XL: MA tree property out of range");
                if (p > 0)
                {
                    int v = JxlBitReader.UnpackSigned(r.ReadUint(br, 0));
                    prop.Add((int)p - 1); val.Add(v); left.Add(nextIndex); right.Add(nextIndex + 1);
                    pred.Add(0); off.Add(0); mul.Add(0); ctx.Add(0);
                    nextIndex += 2;
                    toDecode += 2;
                }
                else
                {
                    uint predictor = r.ReadUint(br, 2);
                    if (predictor >= 14) throw new ImageFormatException("JPEG XL: bad predictor");
                    long offset = JxlBitReader.UnpackSigned(r.ReadUint(br, 3));
                    uint mulLog = r.ReadUint(br, 4);
                    if (mulLog > 30) throw new ImageFormatException("JPEG XL: bad multiplier");
                    uint mulBits = r.ReadUint(br, 5);
                    if (mulBits >= (1u << (31 - (int)mulLog)) - 1) throw new ImageFormatException("JPEG XL: bad multiplier");
                    prop.Add(-1); val.Add(0); left.Add(0); right.Add(0);
                    pred.Add((int)predictor); off.Add(offset); mul.Add((mulBits + 1) << (int)mulLog); ctx.Add(leaves++);
                }
                if (br.Overrun) throw new ImageFormatException("JPEG XL: truncated MA tree");
            }
            r.CheckFinalState();
            var t = new JxlMaTree
            {
                Property = prop.ToArray(), Value = val.ToArray(), Left = left.ToArray(), Right = right.ToArray(),
                Predictor = pred.ToArray(), Offset = off.ToArray(), Multiplier = mul.ToArray(), Context = ctx.ToArray(),
                NumLeaves = leaves, Count = prop.Count,
            };
            return t;
        }

        /// <summary>
        /// The tree for one channel of one stream: decisions on the channel (property 0) and stream (property 1) resolved.
        /// Returns a compacted tree whose node 0 is the root.
        /// </summary>
        public JxlMaTree Specialize(int channel, int stream)
        {
            var map = new Dictionary<int, int>();
            var prop = new List<int>(); var val = new List<int>(); var left = new List<int>(); var right = new List<int>();
            var pred = new List<int>(); var off = new List<long>(); var mul = new List<uint>(); var ctx = new List<int>();
            var queue = new Queue<(int src, int dst)>();
            int Resolve(int n)
            {
                while (Property[n] == 0 || Property[n] == 1)
                {
                    int pv = Property[n] == 0 ? channel : stream;
                    n = pv > Value[n] ? Left[n] : Right[n];
                }
                return n;
            }
            int Add(int src)
            {
                int d = prop.Count;
                prop.Add(Property[src]); val.Add(Value[src]); left.Add(0); right.Add(0);
                pred.Add(Predictor[src]); off.Add(Offset[src]); mul.Add(Multiplier[src]); ctx.Add(Context[src]);
                queue.Enqueue((src, d));
                return d;
            }
            Add(Resolve(0));
            while (queue.Count > 0)
            {
                var (s, d) = queue.Dequeue();
                if (Property[s] < 0) continue;
                left[d] = Add(Resolve(Left[s]));
                right[d] = Add(Resolve(Right[s]));
            }
            return new JxlMaTree
            {
                Property = prop.ToArray(), Value = val.ToArray(), Left = left.ToArray(), Right = right.ToArray(),
                Predictor = pred.ToArray(), Offset = off.ToArray(), Multiplier = mul.ToArray(), Context = ctx.ToArray(),
                NumLeaves = NumLeaves, Count = prop.Count,
            };
        }

        public int MaxProperty()
        {
            int m = -1;
            for (int i = 0; i < Count; i++) m = Math.Max(m, Property[i]);
            return m;
        }

        public bool UsesWp()
        {
            for (int i = 0; i < Count; i++)
                if (Property[i] == 15 || Property[i] < 0 && Predictor[i] == 6) return true;
            return false;
        }
    }

    /// <summary>Parameters of the self-correcting (weighted) predictor.</summary>
    internal sealed class JxlWpHeader
    {
        public int P1C = 16, P2C = 10, P3Ca = 7, P3Cb = 7, P3Cc = 7, P3Cd = 0, P3Ce = 0;
        public uint W0 = 0xd, W1 = 0xc, W2 = 0xc, W3 = 0xc;

        public static readonly JxlWpHeader Default = new();

        public static JxlWpHeader Read(JxlBitReader br)
        {
            if (br.ReadBool()) return Default;
            return new JxlWpHeader
            {
                P1C = (int)br.ReadBits(5), P2C = (int)br.ReadBits(5),
                P3Ca = (int)br.ReadBits(5), P3Cb = (int)br.ReadBits(5), P3Cc = (int)br.ReadBits(5), P3Cd = (int)br.ReadBits(5), P3Ce = (int)br.ReadBits(5),
                W0 = br.ReadBits(4), W1 = br.ReadBits(4), W2 = br.ReadBits(4), W3 = br.ReadBits(4),
            };
        }
    }

    /// <summary>
    /// State of the self-correcting predictor over one channel. Two rows of errors with a padding of two; the sub-predictor
    /// errors of a pixel are also added to the entry north-east of it, so that the entries N and NW of the next pixel
    /// include its W and WW errors (this also defines the behaviour at the image edges).
    /// </summary>
    internal sealed class JxlWeightedPredictor
    {
        readonly JxlWpHeader h;
        readonly int w, stride;
        readonly int[] trueErr;
        readonly uint[] err0, err1, err2, err3;
        int cur, prev;
        long pred, sub0, sub1, sub2, sub3;
        public int MaxError;

        static readonly uint[] DivLookup = BuildDiv();

        static uint[] BuildDiv()
        {
            var d = new uint[64];
            for (int i = 0; i < 64; i++) d[i] = (uint)((1 << 24) / (i + 1));
            return d;
        }

        public JxlWeightedPredictor(JxlWpHeader header, int width)
        {
            h = header; w = width; stride = width + 2;
            trueErr = new int[2 * stride];
            err0 = new uint[2 * stride]; err1 = new uint[2 * stride]; err2 = new uint[2 * stride]; err3 = new uint[2 * stride];
        }

        public void StartRow(int y)
        {
            cur = (y & 1) != 0 ? 0 : stride;
            prev = (y & 1) != 0 ? stride : 0;
        }

        static int FloorLog2(uint x) => 31 - System.Numerics.BitOperations.LeadingZeroCount(x);

        static uint ErrorWeight(uint x, uint maxWeight)
        {
            int shift = FloorLog2(x + 1) - 5;
            if (shift < 0) shift = 0;
            return 4 + ((maxWeight * DivLookup[x >> shift]) >> shift);
        }

        /// <summary>Returns the prediction (pixel units) for (x, y); the neighbours are the plain sample values.</summary>
        public long Predict(int x, int y, long N, long W, long NE, long NW, long NN)
        {
            int pN = prev + x;
            int pNE = x < w - 1 ? pN + 1 : pN;
            int pNW = x > 0 ? pN - 1 : pN;
            uint w0 = ErrorWeight(err0[pN] + err0[pNE] + err0[pNW], h.W0);
            uint w1 = ErrorWeight(err1[pN] + err1[pNE] + err1[pNW], h.W1);
            uint w2 = ErrorWeight(err2[pN] + err2[pNE] + err2[pNW], h.W2);
            uint w3 = ErrorWeight(err3[pN] + err3[pNE] + err3[pNW], h.W3);
            long N3 = N << 3, W3 = W << 3, NE3 = NE << 3, NW3 = NW << 3, NN3 = NN << 3;
            long teW = x == 0 ? 0 : trueErr[cur + x - 1];
            long teN = trueErr[pN], teNW = trueErr[pNW], teNE = trueErr[pNE];
            long sumWN = teN + teW;
            long p0 = W3 + NE3 - N3;
            long p1 = N3 - ((sumWN + teNE) * h.P1C >> 5);
            long p2 = W3 - ((sumWN + teNW) * h.P2C >> 5);
            long p3 = N3 - ((teNW * h.P3Ca + teN * h.P3Cb + teNE * h.P3Cc + (NN3 - N3) * h.P3Cd + (NW3 - W3) * h.P3Ce) >> 5);
            uint ws = w0 + w1 + w2 + w3;
            int lw = FloorLog2(ws) - 4;
            w0 >>= lw; w1 >>= lw; w2 >>= lw; w3 >>= lw;
            uint sum = w0 + w1 + w2 + w3;
            long s = (sum >> 1) - 1 + p0 * w0 + p1 * w1 + p2 * w2 + p3 * w3;
            long p = (s * DivLookup[sum - 1]) >> 24;
            if (((teN ^ teW) | (teN ^ teNW)) <= 0)
            {
                long mx = Math.Max(W3, Math.Max(N3, NE3)), mn = Math.Min(W3, Math.Min(N3, NE3));
                if (p < mn) p = mn; else if (p > mx) p = mx;
            }
            long me = teW;
            if (Math.Abs(teN) > Math.Abs(me)) me = teN;
            if (Math.Abs(teNW) > Math.Abs(me)) me = teNW;
            if (Math.Abs(teNE) > Math.Abs(me)) me = teNE;
            MaxError = (int)me;
            pred = p; sub0 = p0; sub1 = p1; sub2 = p2; sub3 = p3;
            return (p + 3) >> 3;
        }

        public void Update(int x, long value)
        {
            long v3 = value << 3;
            int i = cur + x, ne = prev + x + 1;
            trueErr[i] = (int)(pred - v3);
            uint e;
            e = (uint)((Math.Abs(sub0 - v3) + 3) >> 3); err0[i] = e; err0[ne] += e;
            e = (uint)((Math.Abs(sub1 - v3) + 3) >> 3); err1[i] = e; err1[ne] += e;
            e = (uint)((Math.Abs(sub2 - v3) + 3) >> 3); err2[i] = e; err2[ne] += e;
            e = (uint)((Math.Abs(sub3 - v3) + 3) >> 3); err3[i] = e; err3[ne] += e;
        }
    }
}
