using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using static Aardvark.ImageCodecs.JpegXl.JxlBitReader;

namespace Aardvark.ImageCodecs.JpegXl
{
    /// <summary>A modular transform as written in a modular header.</summary>
    internal sealed class JxlTransformEnc
    {
        public JxlTransformKind Kind;
        public int BeginC, RctType, NumC, NbColours;

        public void Write(JxlBitWriter w)
        {
            w.Write(2, (uint)Kind);
            if (Kind != JxlTransformKind.Squeeze)
                w.WriteU32((uint)BeginC, Bits(3), BitsOffset(6, 8), BitsOffset(10, 72), BitsOffset(13, 1096));
            if (Kind == JxlTransformKind.Rct)
                w.WriteU32((uint)RctType, Val(6), Bits(2), BitsOffset(4, 2), BitsOffset(6, 10));
            else if (Kind == JxlTransformKind.Palette)
            {
                w.WriteU32((uint)NumC, Val(1), Val(3), Val(4), BitsOffset(13, 1));
                w.WriteU32((uint)NbColours, BitsOffset(8, 0), BitsOffset(10, 256), BitsOffset(12, 1280), BitsOffset(16, 5376));
                w.WriteU32(0, Val(0), BitsOffset(8, 1), BitsOffset(10, 257), BitsOffset(16, 1281));   // no deltas
                w.Write(4, 0);   // delta predictor
            }
            else w.WriteU32(0, Val(0), BitsOffset(4, 1), BitsOffset(6, 9), BitsOffset(8, 41));   // default squeeze
        }

        public static void WriteHeader(JxlBitWriter w, IReadOnlyList<JxlTransformEnc> transforms, bool globalTree = true)
        {
            w.WriteBool(globalTree);
            w.WriteBool(true);   // default weighted predictor parameters
            w.WriteU32((uint)transforms.Count, Val(0), Val(1), BitsOffset(4, 2), BitsOffset(8, 18));
            foreach (var t in transforms) t.Write(w);
        }
    }

    /// <summary>Forward modular transforms on a channel list (the inverses are in JxlModularDecode.cs).</summary>
    internal static class JxlForwardTransforms
    {
        public static void Rct(List<JxlChannel> ch, int begin, int rctType)
        {
            int perm = rctType / 7, type = rctType % 7;
            int p0 = perm % 3, p1 = (perm + 1 + perm / 3) % 3, p2 = (perm + 2 - perm / 3) % 3;
            var a = ch[begin + p0]; var b = ch[begin + p1]; var c = ch[begin + p2];
            var A = a.Data; var B = b.Data; var C = c.Data;
            int n = A.Length;
            var o0 = new int[n]; var o1 = new int[n]; var o2 = new int[n];
            for (int i = 0; i < n; i++) Forward(type, A[i], B[i], C[i], out o0[i], out o1[i], out o2[i]);
            var shape = a;
            ch[begin] = new JxlChannel(shape.W, shape.H, shape.HShift, shape.VShift, false) { Data = o0 };
            ch[begin + 1] = new JxlChannel(shape.W, shape.H, shape.HShift, shape.VShift, false) { Data = o1 };
            ch[begin + 2] = new JxlChannel(shape.W, shape.H, shape.HShift, shape.VShift, false) { Data = o2 };
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static void Forward(int type, int a, int b, int c, out int o0, out int o1, out int o2)
        {
            switch (type)
            {
                case 0: o0 = a; o1 = b; o2 = c; return;
                case 1: o0 = a; o1 = b; o2 = c - a; return;
                case 2: o0 = a; o1 = b - a; o2 = c; return;
                case 3: o0 = a; o1 = b - a; o2 = c - a; return;
                case 4: o0 = a; o1 = b - (int)(((long)a + c) >> 1); o2 = c; return;
                case 5: { int c2 = c - a; o0 = a; o1 = b - a - (c2 >> 1); o2 = c2; return; }
                default:
                    {
                        int co = a - c;
                        int tmp = c + (co >> 1);
                        int cg = b - tmp;
                        o0 = tmp + (cg >> 1); o1 = co; o2 = cg;
                        return;
                    }
            }
        }

        /// <summary>Palette over channels begin..begin+n-1 with the given colours (n values each, colour major).</summary>
        public static void Palette(List<JxlChannel> ch, int begin, int n, int[][] colours, Dictionary<ulong, int> index, ref int nbMeta)
        {
            var first = ch[begin];
            int len = first.W * first.H;
            var idx = new int[len];
            var src = new int[n][];
            for (int c = 0; c < n; c++) src[c] = ch[begin + c].Data;
            for (int i = 0; i < len; i++)
            {
                ulong key = 0;
                for (int c = 0; c < n; c++) key = key << 16 | (uint)(ushort)src[c][i];
                idx[i] = index[key];
            }
            var meta = new JxlChannel(colours.Length, n, -1, -1, true);
            for (int k = 0; k < colours.Length; k++)
                for (int c = 0; c < n; c++) meta.Data[c * colours.Length + k] = colours[k][c];
            ch.RemoveRange(begin, n);
            ch.Insert(begin, new JxlChannel(first.W, first.H, first.HShift, first.VShift, false) { Data = idx });
            ch.Insert(0, meta);
            nbMeta++;
        }

        /// <summary>Palette of a single channel holding the given sorted values.</summary>
        public static void ChannelPalette(List<JxlChannel> ch, int begin, int[] values, int minV, int[] lut, ref int nbMeta)
        {
            var c = ch[begin];
            var d = c.Data;
            var idx = new int[d.Length];
            for (int i = 0; i < d.Length; i++) idx[i] = lut[d[i] - minV];
            var meta = new JxlChannel(values.Length, 1, -1, -1, false) { Data = (int[])values.Clone() };
            ch[begin] = new JxlChannel(c.W, c.H, c.HShift, c.VShift, false) { Data = idx };
            ch.Insert(0, meta);
            nbMeta++;
        }
    }

    /// <summary>
    /// The modular images of a frame coded with one global MA tree and entropy code (GlobalModular): learns or builds the
    /// tree, tokenizes every stream and writes them.
    /// </summary>
    internal sealed class JxlModularStreams
    {
        public readonly List<JxlStreamImage> Streams = new();
        public JxlEncTree Tree = null!;
        public JxlEntropyEncoder Code = null!;
        public JxlTokens[] Tokens = Array.Empty<JxlTokens>();
        public readonly JxlWpHeader Wp = JxlWpHeader.Default;

        public int Add(JxlStreamImage s) { Streams.Add(s); return Streams.Count - 1; }

        public void Prepare(JxlEncTree tree, JxlLosslessParams lp, bool parallel)
        {
            Tree = tree;
            Tokens = new JxlTokens[Streams.Count];
            void Tok(int i)
            {
                var s = Streams[i];
                long px = 0;
                foreach (var c in s.Channels) px += (long)c.W * c.H;
                var t = new JxlTokens((int)Math.Min(int.MaxValue / 2, px + 16));
                for (int c = 0; c < s.Channels.Count; c++)
                {
                    if (s.Channels[c].W == 0 || s.Channels[c].H == 0) continue;
                    JxlModularTokenizer.EncodeChannel(s.Channels, c, Tree.Specialize(c, s.StreamId), Wp, t);
                }
                Tokens[i] = t;
            }
            if (parallel && Streams.Count > 1) Parallel.For(0, Streams.Count, Tok);
            else for (int i = 0; i < Streams.Count; i++) Tok(i);
            JxlProfile.Mark("enc tokenize");
            Code = NewCode(lp, false);
            Code.Build(Tokens, parallel);
            if (lp.Lz77)
            {
                // LZ77 only when it pays: compare the estimated sizes with and without it
                var lz = new JxlTokens[Tokens.Length];
                void Lz(int i)
                {
                    var t = Tokens[i];
                    var c = new JxlTokens(1) { Ctx = (int[])t.Ctx.Clone(), Val = (uint[])t.Val.Clone(), Count = t.Count };
                    JxlLz77.Apply(c, Tree.NumLeaves, MaxWidth(Streams[i]), lp.Lz77Effort);
                    lz[i] = c;
                }
                if (parallel && Tokens.Length > 1) Parallel.For(0, Tokens.Length, Lz); else for (int i = 0; i < Tokens.Length; i++) Lz(i);
                var lzCode = NewCode(lp, true);
                lzCode.Build(lz, parallel);
                double plain = 0, withLz = 0;
                for (int i = 0; i < Tokens.Length; i++) { plain += Code.EstimateBits(Tokens[i]); withLz += lzCode.EstimateBits(lz[i]); }
                if (withLz < plain * 0.995) { Code = lzCode; Tokens = lz; }
            }
            JxlProfile.Mark("enc entropy code");
        }

        JxlEntropyEncoder NewCode(JxlLosslessParams lp, bool lz)
        {
            var c = new JxlEntropyEncoder(Tree.NumLeaves)
            {
                MaxClusters = lp.MaxClusters, ClusterEffort = lp.ClusterEffort,
                Candidates = lp.HybridSearch ? JxlEntropyEncoder.DefaultCandidates : new[] { JxlHybridUint.Make(4, 1, 0) },
            };
            if (lz) JxlLz77.Configure(c);
            return c;
        }

        public static int MaxWidth(JxlStreamImage s)
        {
            int m = 0;
            foreach (var c in s.Channels) if (c.W > 0 && c.H > 0) m = Math.Max(m, c.W);
            return m;
        }

        public void WriteGlobal(JxlBitWriter w)
        {
            w.WriteBool(true);   // the frame has a global tree
            Tree.Write(w);
            Code.WriteCode(w);
        }

        /// <summary>
        /// Header and data of stream i. Group streams without channels write nothing; the global stream always has its
        /// header (the decoder reads it whenever the frame has channels, also when they are all coded in groups).
        /// </summary>
        public void WriteStream(JxlBitWriter w, int i, IReadOnlyList<JxlTransformEnc> transforms, bool alwaysHeader = false)
        {
            var s = Streams[i];
            if (s.Channels.Count == 0 && !alwaysHeader) return;
            JxlTransformEnc.WriteHeader(w, transforms);
            bool any = false;
            foreach (var c in s.Channels) if (c.W > 0 && c.H > 0) any = true;
            if (!any) return;
            Code.WriteTokens(w, Tokens[i]);
        }
    }

    /// <summary>Effort dependent settings of lossless (modular) coding.</summary>
    internal sealed class JxlLosslessParams
    {
        public int Effort;
        /// <summary>0 learned, 1 fixed gradient tree, 2 fixed weighted predictor tree.</summary>
        public int FixedTree;
        public JxlTreeParams Tree = new();
        public int RctMode;            // 0 YCoCg only, 1 a few candidates, 2 all 42
        public int PaletteMaxColours;
        public bool ChannelPalette;
        public bool Lz77;
        public int Lz77Effort;
        public int MaxClusters = 64;
        public int ClusterEffort = 1;
        public bool HybridSearch = true;
        /// <summary>Encode with and without palette and keep the smaller (very slow efforts).</summary>
        public bool TryPaletteBoth;

        static readonly int[] AllProps = { 0, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23 };

        public static JxlLosslessParams For(int effort)
        {
            var p = new JxlLosslessParams { Effort = effort };
            switch (effort)
            {
                case 1:
                    p.FixedTree = 1; p.RctMode = 0; p.PaletteMaxColours = 256; p.ChannelPalette = true; p.MaxClusters = 16; p.ClusterEffort = 0; p.HybridSearch = false;
                    break;
                case 2:
                    p.FixedTree = 1; p.RctMode = 1; p.PaletteMaxColours = 1024; p.ChannelPalette = true; p.MaxClusters = 32;
                    break;
                case 3:
                    p.FixedTree = 2; p.RctMode = 1; p.PaletteMaxColours = 1024; p.ChannelPalette = true; p.MaxClusters = 32;
                    break;
                case 4:
                    p.RctMode = 1; p.PaletteMaxColours = 1024; p.ChannelPalette = true;
                    p.Tree = new JxlTreeParams
                    {
                        Predictors = new[] { 5, 0, 1, 2 }, Properties = new[] { 0, 2, 3, 6, 7, 9, 10, 11, 12, 13, 14, 16, 17 },
                        SampleFraction = 0.03, MaxSamples = 120_000, Quantiles = 16, MaxLeaves = 256, SplitPenalty = 400,
                    };
                    break;
                case 5:
                    p.RctMode = 2; p.PaletteMaxColours = 1024; p.ChannelPalette = true;
                    p.Tree = new JxlTreeParams
                    {
                        Predictors = new[] { 6, 5, 0, 1, 2 }, Properties = new[] { 0, 2, 3, 6, 7, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19 },
                        SampleFraction = 0.05, MaxSamples = 200_000, Quantiles = 24, MaxLeaves = 512, SplitPenalty = 320,
                    };
                    break;
                case 6:
                    p.RctMode = 2; p.PaletteMaxColours = 1024; p.ChannelPalette = true;
                    p.Tree = new JxlTreeParams
                    {
                        Predictors = new[] { 6, 5, 0, 1, 2, 3, 4, 13 }, Properties = AllProps,
                        SampleFraction = 0.08, MaxSamples = 300_000, Quantiles = 32, MaxLeaves = 1024, SplitPenalty = 280,
                    };
                    break;
                case 7:
                    p.RctMode = 2; p.PaletteMaxColours = 1024; p.ChannelPalette = true;
                    p.Tree = new JxlTreeParams
                    {
                        Predictors = new[] { 6, 5, 0, 1, 2, 3, 4, 13 }, Properties = AllProps,
                        SampleFraction = 0.12, MaxSamples = 500_000, Quantiles = 48, MaxLeaves = 2048, SplitPenalty = 250,
                    };
                    p.ClusterEffort = 2;
                    break;
                case 8:
                    p.RctMode = 2; p.PaletteMaxColours = 2048; p.ChannelPalette = true; p.Lz77 = true; p.Lz77Effort = 1;
                    p.Tree = new JxlTreeParams
                    {
                        Predictors = new[] { 6, 5, 0, 1, 2, 3, 4, 7, 8, 9, 10, 11, 12, 13 }, Properties = AllProps,
                        SampleFraction = 0.25, MaxSamples = 1_000_000, Quantiles = 64, MaxLeaves = 4096, SplitPenalty = 220,
                    };
                    p.ClusterEffort = 3; p.MaxClusters = 128;
                    break;
                default:
                    p.RctMode = 2; p.PaletteMaxColours = 4096; p.ChannelPalette = true; p.Lz77 = true; p.Lz77Effort = 2; p.TryPaletteBoth = true;
                    p.Tree = new JxlTreeParams
                    {
                        Predictors = new[] { 6, 5, 0, 1, 2, 3, 4, 7, 8, 9, 10, 11, 12, 13 }, Properties = AllProps,
                        SampleFraction = 0.5, MaxSamples = 2_000_000, Quantiles = 64, MaxLeaves = 8192, SplitPenalty = 200,
                    };
                    p.ClusterEffort = 4; p.MaxClusters = 256;
                    break;
            }
            DebugOverrides(p);
            return p;
        }

        static void DebugOverrides(JxlLosslessParams p)
        {
            static double? D(string n) => double.TryParse(Environment.GetEnvironmentVariable("PIXCODEC_JXL_" + n), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
            if (D("PEN") is double a) p.Tree.SplitPenalty = a;
            if (D("LEAVES") is double b) p.Tree.MaxLeaves = (int)b;
            if (D("FRAC") is double c) p.Tree.SampleFraction = c;
            if (D("MAXS") is double d) p.Tree.MaxSamples = (int)d;
            if (D("Q") is double e) p.Tree.Quantiles = (int)e;
            if (D("MINLEAF") is double f) p.Tree.MinLeafSamples = (int)f;
            if (D("CL") is double g) p.MaxClusters = (int)g;
            if (D("LZ") is double h) p.Lz77 = h > 0;
            if (D("PB") is double i) p.TryPaletteBoth = i > 0;
            if (D("PAL") is double j) p.PaletteMaxColours = (int)j;
        }
    }
}
