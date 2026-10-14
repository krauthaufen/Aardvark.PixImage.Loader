using System;
using System.Buffers.Binary;

namespace PixCodec.DdsImpl
{
    /// <summary>Writes the bits of a 128 bit block, least significant first (the order <see cref="Bptc"/> reads them).</summary>
    internal ref struct BitWriter
    {
        ulong lo, hi;
        public int Pos;

        public void Write(int value, int n)
        {
            for (int k = 0; k < n; k++, Pos++)
            {
                ulong bit = (ulong)(value >> k & 1);
                if (Pos < 64) lo |= bit << Pos; else hi |= bit << (Pos - 64);
            }
        }

        public void Store(Span<byte> o)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(o, lo);
            BinaryPrimitives.WriteUInt64LittleEndian(o.Slice(8), hi);
        }
    }

    /// <summary>
    /// BC7 block encoding. Each mode is fitted per subset (principal axis, least squares refinement against the chosen
    /// indices, P-bit choice, optional greedy search over the quantized endpoints); partitions are ranked by how well each
    /// subset fits a line, and only the best few are encoded exactly. Errors are squared RGBA differences of the decoded
    /// texels.
    /// </summary>
    internal static class Bc7Encoder
    {
        // best stored value for an 8 bit target: [bits * 3 + pbit + 1][target], pbit -1 for none (bits = stored bits)
        static readonly byte[][] QuantTable = BuildQuant();

        static byte[][] BuildQuant()
        {
            var t = new byte[9 * 3][];
            for (int bits = 1; bits <= 8; bits++)
                for (int p = -1; p <= 1; p++)
                {
                    if (p >= 0 && bits == 8) continue;
                    var a = new byte[256];
                    int total = p < 0 ? bits : bits + 1;
                    for (int x = 0; x < 256; x++)
                    {
                        int best = int.MaxValue;
                        for (int v = 0; v < 1 << bits; v++)
                        {
                            int e = Math.Abs(Bptc.Expand(p < 0 ? v : v << 1 | p, total) - x);
                            if (e < best) { best = e; a[x] = (byte)v; }
                        }
                    }
                    t[bits * 3 + p + 1] = a;
                }
            return t;
        }

        static int Quant(float x, int bits, int pbit) => QuantTable[bits * 3 + pbit + 1][Math.Clamp((int)MathF.Round(x), 0, 255)];

        static int Value(int stored, int bits, int pbit) => pbit < 0 ? Bptc.Expand(stored, bits) : Bptc.Expand(stored << 1 | pbit, bits + 1);

        // stored and expanded values as spans over a small array: [endpoint * 4 + channel]
        sealed class Fit
        {
            public readonly int[] Stored = new int[8];
            public readonly int[] Value = new int[8];
            public readonly int[] PBit = new int[2];
            public readonly byte[] Index = new byte[16];
            public long Error = long.MaxValue;

            public void CopyFrom(Fit f)
            {
                f.Stored.CopyTo(Stored, 0); f.Value.CopyTo(Value, 0); f.PBit.CopyTo(PBit, 0); f.Index.CopyTo(Index, 0); Error = f.Error;
            }
        }

        /// <summary>One line fit: texels of a subset (by mask) to stored endpoints and indices.</summary>
        struct Spec
        {
            public int Channels;   // 3: RGB (alpha decodes as 255 or comes from elsewhere), 4: RGBA, 1: alpha only (channel 3)
            public int ColorBits, AlphaBits;
            public int PBits;      // 0 none, 1 unique per endpoint, 2 shared by both endpoints
            public bool Opaque;    // the block is opaque: P-bits 1, so that alpha stays exactly 255
            public int IndexBits;
        }

        /// <summary>
        /// Index assignment for the given endpoints: error over the texels in <paramref name="members"/> of the channels the spec
        /// covers.
        /// </summary>
        static long Assign(ReadOnlySpan<byte> px, ReadOnlySpan<byte> members, int count, in Spec s, Fit f)
        {
            var w = Bptc.Weights(s.IndexBits);
            int nw = w.Length;
            Span<int> pal = stackalloc int[16 * 4];
            int c0 = s.Channels == 1 ? 3 : 0, c1 = s.Channels == 3 ? 3 : 4;
            for (int k = 0; k < nw; k++)
                for (int c = c0; c < c1; c++)
                    pal[k * 4 + c] = ((64 - w[k]) * f.Value[c] + w[k] * f.Value[4 + c] + 32) >> 6;
            long total = 0;
            for (int m = 0; m < count; m++)
            {
                int i = members[m];
                int be = int.MaxValue, bi = 0;
                for (int k = 0; k < nw; k++)
                {
                    int e = 0;
                    for (int c = c0; c < c1; c++) { int d = px[i * 4 + c] - pal[k * 4 + c]; e += d * d; }
                    if (e < be) { be = e; bi = k; }
                }
                f.Index[i] = (byte)bi;
                total += be;
            }
            return total;
        }

        /// <summary>Quantizes float endpoints (a, b over channels c0..c1) choosing the P-bits by quantization error.</summary>
        static void Quantize(ReadOnlySpan<float> a, ReadOnlySpan<float> b, in Spec s, Fit f)
        {
            int c0 = s.Channels == 1 ? 3 : 0, c1 = s.Channels == 3 ? 3 : 4;
            Span<float> ep = stackalloc float[8];
            for (int c = 0; c < 4; c++) { ep[c] = a[c]; ep[4 + c] = b[c]; }
            int bestShared = 0;
            if (s.PBits == 2)
            {
                long be = long.MaxValue;
                for (int p = 0; p < 2; p++)
                {
                    long e = 0;
                    for (int k = 0; k < 2; k++)
                        for (int c = c0; c < c1; c++)
                        {
                            int bits = c == 3 ? s.AlphaBits : s.ColorBits;
                            int d = Value(Quant(ep[k * 4 + c], bits, p), bits, p) - (int)MathF.Round(Math.Clamp(ep[k * 4 + c], 0, 255));
                            e += d * d;
                        }
                    if (e < be) { be = e; bestShared = p; }
                }
            }
            for (int k = 0; k < 2; k++)
            {
                int pb = s.PBits == 0 ? -1 : s.PBits == 2 ? bestShared : 0;
                if (s.PBits == 1 && s.Opaque && s.Channels == 4) pb = 1;
                else if (s.PBits == 1)
                {
                    long be = long.MaxValue;
                    for (int p = 0; p < 2; p++)
                    {
                        long e = 0;
                        for (int c = c0; c < c1; c++)
                        {
                            int bits = c == 3 ? s.AlphaBits : s.ColorBits;
                            int d = Value(Quant(ep[k * 4 + c], bits, p), bits, p) - (int)MathF.Round(Math.Clamp(ep[k * 4 + c], 0, 255));
                            e += d * d;
                        }
                        if (e < be) { be = e; pb = p; }
                    }
                }
                f.PBit[k] = pb;
                for (int c = c0; c < c1; c++)
                {
                    int bits = c == 3 ? s.AlphaBits : s.ColorBits;
                    f.Stored[k * 4 + c] = Quant(ep[k * 4 + c], bits, pb);
                    f.Value[k * 4 + c] = Value(f.Stored[k * 4 + c], bits, pb);
                }
            }
        }

        static void Requantize(Fit f, in Spec s)
        {
            int c0 = s.Channels == 1 ? 3 : 0, c1 = s.Channels == 3 ? 3 : 4;
            for (int k = 0; k < 2; k++)
                for (int c = c0; c < c1; c++)
                    f.Value[k * 4 + c] = Value(f.Stored[k * 4 + c], c == 3 ? s.AlphaBits : s.ColorBits, f.PBit[k]);
        }

        /// <summary>Fits one subset. <paramref name="cur"/> is scratch; the result is in <paramref name="best"/>.</summary>
        static void FitSubset(ReadOnlySpan<byte> px, ReadOnlySpan<byte> members, int count, in Spec s, BcQuality q, Fit best, Fit cur)
        {
            int c0 = s.Channels == 1 ? 3 : 0, c1 = s.Channels == 3 ? 3 : 4, dim = c1 - c0;
            best.Error = long.MaxValue;
            Span<float> pts = stackalloc float[64];
            for (int m = 0; m < count; m++)
                for (int c = 0; c < dim; c++) pts[m * dim + c] = px[members[m] * 4 + c0 + c];
            Span<float> mean = stackalloc float[4], axis = stackalloc float[4];
            BcEncoder.PrincipalAxis(pts.Slice(0, count * dim), count, dim, mean, axis);
            float tmin = float.MaxValue, tmax = float.MinValue;
            for (int m = 0; m < count; m++)
            {
                float t = 0;
                for (int c = 0; c < dim; c++) t += (pts[m * dim + c] - mean[c]) * axis[c];
                tmin = Math.Min(tmin, t); tmax = Math.Max(tmax, t);
            }
            Span<float> a = stackalloc float[4], b = stackalloc float[4], la = stackalloc float[4], lb = stackalloc float[4];
            a.Clear(); b.Clear();
            for (int c = 0; c < dim; c++) { a[c0 + c] = mean[c] + tmin * axis[c]; b[c0 + c] = mean[c] + tmax * axis[c]; }

            var w = Bptc.Weights(s.IndexBits);
            Span<float> wts = stackalloc float[16];
            int iterations = q == BcQuality.Fast ? 1 : q == BcQuality.Normal ? 2 : 3;
            for (int it = 0; it <= iterations; it++)
            {
                Quantize(a, b, s, cur);
                cur.Error = Assign(px, members, count, s, cur);
                if (cur.Error < best.Error) best.CopyFrom(cur);
                if (it == iterations || cur.Error == 0) break;
                for (int m = 0; m < count; m++) wts[m] = w[cur.Index[members[m]]] / 64f;
                if (!BcEncoder.LeastSquares(pts, wts, count, dim, la, lb)) break;
                for (int c = 0; c < dim; c++) { a[c0 + c] = la[c]; b[c0 + c] = lb[c]; }
            }

            if (q >= BcQuality.High && best.Error > 0)
            {
                // greedy single steps on the stored values
                cur.CopyFrom(best);
                bool improved = true;
                for (int round = 0; round < 4 && improved; round++)
                {
                    improved = false;
                    for (int k = 0; k < 2; k++)
                        for (int c = c0; c < c1; c++)
                        {
                            int max = (1 << (c == 3 ? s.AlphaBits : s.ColorBits)) - 1;
                            for (int d = -1; d <= 1; d += 2)
                            {
                                int old = cur.Stored[k * 4 + c], v = old + d;
                                if (v < 0 || v > max) continue;
                                cur.Stored[k * 4 + c] = v;
                                Requantize(cur, s);
                                cur.Error = Assign(px, members, count, s, cur);
                                if (cur.Error < best.Error) { best.CopyFrom(cur); improved = true; }
                                else { cur.Stored[k * 4 + c] = old; Requantize(cur, s); }
                            }
                        }
                }
            }
        }

        // ------------------------------------------------------------------ partitions

        static int SubsetOf(int subsets, int part, int i)
            => subsets == 2 ? Bptc.Partition2[part] >> i & 1 : subsets == 3 ? (int)(Bptc.Partition3[part] >> (2 * i) & 3) : 0;

        /// <summary>For each partition: sum over subsets of the squared distances of the texels to their principal line.</summary>
        static void RankPartitions(ReadOnlySpan<byte> px, int subsets, int count, int dim, Span<int> order)
        {
            Span<float> score = stackalloc float[64];
            Span<float> pts = stackalloc float[64];
            Span<float> mean = stackalloc float[4], axis = stackalloc float[4];
            for (int part = 0; part < count; part++)
            {
                float total = 0;
                for (int s = 0; s < subsets; s++)
                {
                    int n = 0;
                    for (int i = 0; i < 16; i++)
                        if (SubsetOf(subsets, part, i) == s)
                        {
                            for (int c = 0; c < dim; c++) pts[n * dim + c] = px[i * 4 + c];
                            n++;
                        }
                    if (n < 2) continue;
                    BcEncoder.PrincipalAxis(pts.Slice(0, n * dim), n, dim, mean, axis);
                    for (int m = 0; m < n; m++)
                    {
                        float t = 0, d2 = 0;
                        for (int c = 0; c < dim; c++) t += (pts[m * dim + c] - mean[c]) * axis[c];
                        for (int c = 0; c < dim; c++) { float d = pts[m * dim + c] - mean[c] - t * axis[c]; d2 += d * d; }
                        total += d2;
                    }
                }
                score[part] = total;
                order[part] = part;
            }
            // sort by score
            for (int i = 1; i < count; i++)
            {
                int k = order[i]; float v = score[k]; int j = i - 1;
                while (j >= 0 && score[order[j]] > v) { order[j + 1] = order[j]; j--; }
                order[j + 1] = k;
            }
        }

        // ------------------------------------------------------------------ block

        sealed class Scratch
        {
            public readonly Fit[] Subset = { new(), new(), new() };
            public readonly Fit Cur = new(), Alpha = new(), AlphaCur = new();
            public readonly byte[] Rotated = new byte[64];
            public readonly byte[] BestBlock = new byte[16], Block = new byte[16];
        }

        [ThreadStatic] static Scratch? _scratch;

        /// <summary>Encodes 16 RGBA texels (row-major) into a 16 byte BC7 block.</summary>
        public static void Encode(ReadOnlySpan<byte> px, Span<byte> o, BcQuality q)
        {
            var sc = _scratch ??= new Scratch();
            bool opaque = true;
            for (int i = 0; i < 16; i++) opaque &= px[i * 4 + 3] == 255;
            long best = long.MaxValue;
            var bestBlock = sc.BestBlock;

            static void Keep(long err, Scratch sc, ref long best)
            {
                if (err < best) { best = err; sc.Block.CopyTo(sc.BestBlock, 0); }
            }

            // single subset modes
            Keep(SingleSubset(px, 6, 0, 0, opaque, q, sc), sc, ref best);
            if (!opaque || q >= BcQuality.High)
            {
                int rotations = q == BcQuality.Fast ? 1 : 4;
                for (int rot = 0; rot < rotations && best > 0; rot++)
                {
                    Keep(SingleSubset(px, 5, rot, 0, opaque, q, sc), sc, ref best);
                    if (q >= BcQuality.Normal)
                        for (int im = 0; im < 2; im++) Keep(SingleSubset(px, 4, rot, im, opaque, q, sc), sc, ref best);
                }
            }
            if (best == 0) { bestBlock.CopyTo(o); return; }

            // partitioned modes, on the best ranked partitions
            int dim = opaque ? 3 : 4;
            Span<int> order2 = stackalloc int[64], order3 = stackalloc int[64];
            RankPartitions(px, 2, 64, dim, order2);
            int top2 = q == BcQuality.Fast ? 1 : q == BcQuality.Normal ? 4 : 12;
            int[] twoModes = opaque ? (q >= BcQuality.High ? new[] { 1, 3, 7 } : new[] { 1, 3 }) : new[] { 7 };
            for (int k = 0; k < top2 && best > 0; k++)
                foreach (int mode in twoModes) Keep(Partitioned(px, mode, order2[k], opaque, q, sc), sc, ref best);
            if (opaque && q >= BcQuality.Normal && best > 0)
            {
                RankPartitions(px, 3, 64, 3, order3);
                int top3 = q == BcQuality.Normal ? 2 : 8;
                for (int k = 0; k < top3 && best > 0; k++)
                {
                    Keep(Partitioned(px, 2, order3[k], opaque, q, sc), sc, ref best);
                    if (order3[k] < 16) Keep(Partitioned(px, 0, order3[k], opaque, q, sc), sc, ref best);
                }
                // mode 0 only has the first 16 partitions: their best one too
                if (q >= BcQuality.High)
                {
                    int b16 = -1;
                    for (int k = 0; k < 64 && b16 < 0; k++) if (order3[k] < 16) b16 = order3[k];
                    Keep(Partitioned(px, 0, b16, opaque, q, sc), sc, ref best);
                }
            }
            bestBlock.CopyTo(o);
        }

        /// <summary>Error of the alpha channel for modes without alpha (decoded as 255).</summary>
        static long OpaqueAlphaError(ReadOnlySpan<byte> px)
        {
            long e = 0;
            for (int i = 0; i < 16; i++) { int d = 255 - px[i * 4 + 3]; e += d * d; }
            return e;
        }

        static readonly byte[] AllTexels = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };

        /// <summary>Modes 0 to 3 and 7; writes the block into the scratch and returns its error.</summary>
        static long Partitioned(ReadOnlySpan<byte> px, int mode, int part, bool opaque, BcQuality q, Scratch sc)
        {
            int ns = Bptc.Bc7Modes[mode, 0], cb = Bptc.Bc7Modes[mode, 4], ab = Bptc.Bc7Modes[mode, 5];
            int pbits = Bptc.Bc7Modes[mode, 6] != 0 ? 1 : Bptc.Bc7Modes[mode, 7] != 0 ? 2 : 0, ib = Bptc.Bc7Modes[mode, 8];
            var spec = new Spec { Channels = ab > 0 ? 4 : 3, ColorBits = cb, AlphaBits = ab, PBits = pbits, IndexBits = ib, Opaque = opaque };
            Span<byte> members = stackalloc byte[16];
            long err = ab > 0 ? 0 : OpaqueAlphaError(px);
            for (int s = 0; s < ns; s++)
            {
                int n = 0;
                for (int i = 0; i < 16; i++) if (SubsetOf(ns, part, i) == s) members[n++] = (byte)i;
                FitSubset(px, members, n, spec, q, sc.Subset[s], sc.Cur);
                err += sc.Subset[s].Error;
            }
            // merge the subsets' indices, fix the anchors and pack
            Span<byte> idx = stackalloc byte[16];
            for (int i = 0; i < 16; i++) idx[i] = sc.Subset[SubsetOf(ns, part, i)].Index[i];
            Span<int> anchors = stackalloc int[3];
            anchors[0] = 0;
            anchors[1] = ns == 2 ? Bptc.Anchor2[part] : ns == 3 ? Bptc.Anchor3Second[part] : 0;
            anchors[2] = ns == 3 ? Bptc.Anchor3Third[part] : 0;
            int top = (1 << ib) - 1;
            for (int s = 0; s < ns; s++)
                if (idx[anchors[s]] > top >> 1)
                {
                    Swap(sc.Subset[s]);
                    for (int i = 0; i < 16; i++) if (SubsetOf(ns, part, i) == s) idx[i] = (byte)(top - idx[i]);
                }

            var bw = new BitWriter();
            bw.Write(1 << mode, mode + 1);
            bw.Write(part, Bptc.Bc7Modes[mode, 1]);
            for (int c = 0; c < 3; c++)
                for (int s = 0; s < ns; s++)
                    for (int k = 0; k < 2; k++) bw.Write(sc.Subset[s].Stored[k * 4 + c], cb);
            if (ab > 0)
                for (int s = 0; s < ns; s++)
                    for (int k = 0; k < 2; k++) bw.Write(sc.Subset[s].Stored[k * 4 + 3], ab);
            if (pbits == 1)
                for (int s = 0; s < ns; s++)
                    for (int k = 0; k < 2; k++) bw.Write(sc.Subset[s].PBit[k], 1);
            else if (pbits == 2)
                for (int s = 0; s < ns; s++) bw.Write(sc.Subset[s].PBit[0], 1);
            for (int i = 0; i < 16; i++)
            {
                bool anchor = i == 0 || ns >= 2 && i == anchors[1] || ns == 3 && i == anchors[2];
                bw.Write(idx[i], anchor ? ib - 1 : ib);
            }
            bw.Store(sc.Block);
            return err;
        }

        static void Swap(Fit f)
        {
            for (int c = 0; c < 4; c++)
            {
                (f.Stored[c], f.Stored[4 + c]) = (f.Stored[4 + c], f.Stored[c]);
                (f.Value[c], f.Value[4 + c]) = (f.Value[4 + c], f.Value[c]);
            }
            (f.PBit[0], f.PBit[1]) = (f.PBit[1], f.PBit[0]);
        }

        /// <summary>Modes 4, 5 (rotation, index mode) and 6; writes the block into the scratch and returns its error.</summary>
        static long SingleSubset(ReadOnlySpan<byte> px, int mode, int rotation, int indexMode, bool opaque, BcQuality q, Scratch sc)
        {
            int cb = Bptc.Bc7Modes[mode, 4], ab = Bptc.Bc7Modes[mode, 5];
            int ib = Bptc.Bc7Modes[mode, 8], ib2 = Bptc.Bc7Modes[mode, 9];
            ReadOnlySpan<byte> src = px;
            if (rotation != 0)
            {
                var r = sc.Rotated;
                px.CopyTo(r);
                for (int i = 0; i < 16; i++) (r[i * 4 + rotation - 1], r[i * 4 + 3]) = (r[i * 4 + 3], r[i * 4 + rotation - 1]);
                src = r;
            }
            var bw = new BitWriter();
            bw.Write(1 << mode, mode + 1);
            long err;
            if (mode == 6)
            {
                var spec = new Spec { Channels = 4, ColorBits = cb, AlphaBits = ab, PBits = 1, IndexBits = ib, Opaque = opaque };
                var f = sc.Subset[0];
                FitSubset(src, AllTexels, 16, spec, q, f, sc.Cur);
                err = f.Error;
                if (f.Index[0] > 7) { Swap(f); for (int i = 0; i < 16; i++) f.Index[i] = (byte)(15 - f.Index[i]); }
                for (int c = 0; c < 4; c++)
                    for (int k = 0; k < 2; k++) bw.Write(f.Stored[k * 4 + c], 7);
                bw.Write(f.PBit[0], 1); bw.Write(f.PBit[1], 1);
                for (int i = 0; i < 16; i++) bw.Write(f.Index[i], i == 0 ? 3 : 4);
                bw.Store(sc.Block);
                return err;
            }
            // modes 4 / 5: colour and alpha with separate indices
            int colorIb = mode == 4 && indexMode == 1 ? ib2 : ib, alphaIb = mode == 4 && indexMode == 1 ? ib : ib2;
            var cs = new Spec { Channels = 3, ColorBits = cb, IndexBits = colorIb };
            var asp = new Spec { Channels = 1, AlphaBits = ab, IndexBits = alphaIb };
            var fc = sc.Subset[0];
            var fa = sc.Alpha;
            FitSubset(src, AllTexels, 16, cs, q, fc, sc.Cur);
            FitSubset(src, AllTexels, 16, asp, q, fa, sc.AlphaCur);
            err = fc.Error + fa.Error;
            int cTop = (1 << colorIb) - 1, aTop = (1 << alphaIb) - 1;
            if (fc.Index[0] > cTop >> 1) { Swap(fc); for (int i = 0; i < 16; i++) fc.Index[i] = (byte)(cTop - fc.Index[i]); }
            if (fa.Index[0] > aTop >> 1) { Swap(fa); for (int i = 0; i < 16; i++) fa.Index[i] = (byte)(aTop - fa.Index[i]); }
            bw.Write(rotation, 2);
            if (mode == 4) bw.Write(indexMode, 1);
            for (int c = 0; c < 3; c++)
                for (int k = 0; k < 2; k++) bw.Write(fc.Stored[k * 4 + c], cb);
            for (int k = 0; k < 2; k++) bw.Write(fa.Stored[k * 4 + 3], ab);
            // the first index set has ib bits, the second ib2; index mode 1 puts the alpha indices first
            var first = mode == 4 && indexMode == 1 ? fa.Index : fc.Index;
            var second = mode == 4 && indexMode == 1 ? fc.Index : fa.Index;
            for (int i = 0; i < 16; i++) bw.Write(first[i], i == 0 ? ib - 1 : ib);
            for (int i = 0; i < 16; i++) bw.Write(second[i], i == 0 ? ib2 - 1 : ib2);
            bw.Store(sc.Block);
            return err;
        }
    }

    /// <summary>
    /// BC6H block encoding. Texels are compared as half float bit patterns (the domain BC6H interpolates in, roughly
    /// logarithmic). One region modes are fitted on the whole block, two region modes on the best ranked partitions;
    /// endpoints that a transformed mode cannot express as deltas are clamped to the nearest expressible ones.
    /// </summary>
    internal static class Bc6hEncoder
    {
        static readonly int[] OneRegion = { 0x03, 0x07, 0x0B, 0x0F };
        static readonly int[] TwoRegions = { 0x00, 0x01, 0x02, 0x06, 0x0A, 0x0E, 0x12, 0x16, 0x1A, 0x1E };

        /// <summary>The half float as an integer of the domain the decoder interpolates in (sign-magnitude to two's complement).</summary>
        public static int Target(float v, bool signed)
        {
            if (float.IsNaN(v)) return 0;
            if (!signed && v < 0) v = 0;
            int h = BitConverter.HalfToInt16Bits((Half)v) & 0xFFFF;
            int mag = Math.Min(h & 0x7FFF, 0x7BFF);
            return (h & 0x8000) != 0 ? -mag : mag;
        }

        static int Decoded(int comp, bool signed)
        {
            int h = Bptc.Finish(comp, signed);
            return (h & 0x8000) != 0 ? -(h & 0x7FFF) : h;
        }

        /// <summary>The unquantized endpoint comp that would finish as about target t.</summary>
        static float Unfinish(int t, bool signed) => signed ? t * 32f / 31 : t * 64f / 31;

        static int Quantize(float u, int prec, bool signed)
        {
            if (!signed)
            {
                int max = (1 << prec) - 1;
                if (prec >= 15) return Math.Clamp((int)MathF.Round(u), 0, max);
                int c = (int)(u * (1 << prec) / 65536f);
                int best = 0; float be = float.MaxValue;
                for (int k = c - 1; k <= c + 1; k++)
                {
                    int x = Math.Clamp(k, 0, max);
                    float e = Math.Abs(Bptc.Unquantize(x, prec, false) - u);
                    if (e < be) { be = e; best = x; }
                }
                return best;
            }
            else
            {
                int max = (1 << (prec - 1)) - 1;
                if (prec >= 16) return Math.Clamp((int)MathF.Round(u), -max, max);
                int c = (int)(u * (1 << (prec - 1)) / 32768f);
                int best = 0; float be = float.MaxValue;
                for (int k = c - 1; k <= c + 1; k++)
                {
                    int x = Math.Clamp(k, -max, max);
                    float e = Math.Abs(Bptc.Unquantize(x, prec, true) - u);
                    if (e < be) { be = e; best = x; }
                }
                return best;
            }
        }

        sealed class Region
        {
            public readonly int[] Q = new int[6];       // quantized endpoints A rgb, B rgb (prec bits, signed values when signed)
            public readonly byte[] Index = new byte[16];
            public long Error;
        }

        sealed class Scratch
        {
            public readonly Region[] R = { new(), new() };
            public readonly Region Cur = new();
            public readonly byte[] Block = new byte[16], BestBlock = new byte[16];
        }

        [ThreadStatic] static Scratch? _scratch;

        static long Assign(ReadOnlySpan<int> t, ReadOnlySpan<byte> members, int count, Region r, int prec, int ib, bool signed)
        {
            var w = Bptc.Weights(ib);
            int nw = w.Length;
            Span<int> e = stackalloc int[6];
            for (int i = 0; i < 6; i++) e[i] = Bptc.Unquantize(r.Q[i], prec, signed);
            Span<int> pal = stackalloc int[16 * 3];
            for (int k = 0; k < nw; k++)
                for (int c = 0; c < 3; c++) pal[k * 3 + c] = Decoded((e[c] * (64 - w[k]) + e[3 + c] * w[k] + 32) >> 6, signed);
            long total = 0;
            for (int m = 0; m < count; m++)
            {
                int i = members[m];
                long be = long.MaxValue; int bi = 0;
                for (int k = 0; k < nw; k++)
                {
                    long d0 = t[i * 3] - pal[k * 3], d1 = t[i * 3 + 1] - pal[k * 3 + 1], d2 = t[i * 3 + 2] - pal[k * 3 + 2];
                    long er = d0 * d0 + d1 * d1 + d2 * d2;
                    if (er < be) { be = er; bi = k; }
                }
                r.Index[i] = (byte)bi;
                total += be;
            }
            return total;
        }

        /// <summary>Fits one region at precision prec (endpoints not yet constrained by deltas).</summary>
        static void FitRegion(ReadOnlySpan<int> t, ReadOnlySpan<byte> members, int count, int prec, int ib, bool signed, BcQuality q, Region best, Region cur)
        {
            Span<float> pts = stackalloc float[48];
            for (int m = 0; m < count; m++)
                for (int c = 0; c < 3; c++) pts[m * 3 + c] = Unfinish(t[members[m] * 3 + c], signed);
            Span<float> mean = stackalloc float[4], axis = stackalloc float[4];
            BcEncoder.PrincipalAxis(pts.Slice(0, count * 3), count, 3, mean, axis);
            float tmin = float.MaxValue, tmax = float.MinValue;
            for (int m = 0; m < count; m++)
            {
                float x = (pts[m * 3] - mean[0]) * axis[0] + (pts[m * 3 + 1] - mean[1]) * axis[1] + (pts[m * 3 + 2] - mean[2]) * axis[2];
                tmin = Math.Min(tmin, x); tmax = Math.Max(tmax, x);
            }
            Span<float> a = stackalloc float[3], b = stackalloc float[3], wts = stackalloc float[16];
            for (int c = 0; c < 3; c++) { a[c] = mean[c] + tmin * axis[c]; b[c] = mean[c] + tmax * axis[c]; }
            var w = Bptc.Weights(ib);
            best.Error = long.MaxValue;
            int iterations = q == BcQuality.Fast ? 1 : q == BcQuality.Normal ? 2 : 3;
            for (int it = 0; it <= iterations; it++)
            {
                for (int c = 0; c < 3; c++) { cur.Q[c] = Quantize(a[c], prec, signed); cur.Q[3 + c] = Quantize(b[c], prec, signed); }
                cur.Error = Assign(t, members, count, cur, prec, ib, signed);
                if (cur.Error < best.Error) Copy(cur, best);
                if (it == iterations || cur.Error == 0) break;
                for (int m = 0; m < count; m++) wts[m] = w[cur.Index[members[m]]] / 64f;
                if (!BcEncoder.LeastSquares(pts, wts, count, 3, a, b)) break;
            }
        }

        static void Copy(Region from, Region to)
        {
            from.Q.CopyTo(to.Q, 0); from.Index.CopyTo(to.Index, 0); to.Error = from.Error;
        }

        /// <summary>Encodes 16 RGB texels (floats, row-major) into a 16 byte BC6H block.</summary>
        public static void Encode(ReadOnlySpan<float> rgb, Span<byte> o, bool signed, BcQuality q)
        {
            var sc = _scratch ??= new Scratch();
            Span<int> t = stackalloc int[48];
            for (int i = 0; i < 48; i++) t[i] = Target(rgb[i], signed);
            long best = long.MaxValue;
            Span<byte> members = stackalloc byte[16];
            for (int i = 0; i < 16; i++) members[i] = (byte)i;

            foreach (int code in OneRegion)
            {
                var mode = Bptc.Bc6Modes[code]!;
                FitRegion(t, members, 16, mode.Prec, 4, signed, q, sc.R[0], sc.Cur);
                long e = Pack(t, code, mode, 0, signed, sc);
                if (e < best) { best = e; sc.Block.CopyTo(sc.BestBlock, 0); }
                if (best == 0) break;
            }
            if (best > 0)
            {
                Span<int> order = stackalloc int[32];
                Rank(t, signed, order);
                int top = q == BcQuality.Fast ? 1 : q == BcQuality.Normal ? 3 : 10;
                Span<byte> m0 = stackalloc byte[16], m1 = stackalloc byte[16];
                for (int k = 0; k < top && best > 0; k++)
                {
                    int part = order[k];
                    int n0 = 0, n1 = 0;
                    for (int i = 0; i < 16; i++) if ((Bptc.Partition2[part] >> i & 1) == 0) m0[n0++] = (byte)i; else m1[n1++] = (byte)i;
                    foreach (int code in TwoRegions)
                    {
                        var mode = Bptc.Bc6Modes[code]!;
                        FitRegion(t, m0, n0, mode.Prec, 3, signed, q, sc.R[0], sc.Cur);
                        FitRegion(t, m1, n1, mode.Prec, 3, signed, q, sc.R[1], sc.Cur);
                        long e = Pack(t, code, mode, part, signed, sc);
                        if (e < best) { best = e; sc.Block.CopyTo(sc.BestBlock, 0); }
                    }
                }
            }
            sc.BestBlock.CopyTo(o);
        }

        static void Rank(ReadOnlySpan<int> t, bool signed, Span<int> order)
        {
            Span<float> score = stackalloc float[32];
            Span<float> pts = stackalloc float[48];
            Span<float> mean = stackalloc float[4], axis = stackalloc float[4];
            for (int part = 0; part < 32; part++)
            {
                float total = 0;
                for (int s = 0; s < 2; s++)
                {
                    int n = 0;
                    for (int i = 0; i < 16; i++)
                        if ((Bptc.Partition2[part] >> i & 1) == s) { for (int c = 0; c < 3; c++) pts[n * 3 + c] = t[i * 3 + c]; n++; }
                    BcEncoder.PrincipalAxis(pts.Slice(0, n * 3), n, 3, mean, axis);
                    for (int m = 0; m < n; m++)
                    {
                        float d = 0, d2 = 0;
                        for (int c = 0; c < 3; c++) d += (pts[m * 3 + c] - mean[c]) * axis[c];
                        for (int c = 0; c < 3; c++) { float r = pts[m * 3 + c] - mean[c] - d * axis[c]; d2 += r * r; }
                        total += d2;
                    }
                }
                score[part] = total;
                order[part] = part;
            }
            for (int i = 1; i < 32; i++)
            {
                int k = order[i]; float v = score[k]; int j = i - 1;
                while (j >= 0 && score[order[j]] > v) { order[j + 1] = order[j]; j--; }
                order[j + 1] = k;
            }
        }

        /// <summary>
        /// Fixes anchors, expresses the endpoints in the mode (clamping deltas that do not fit, then re-assigning indices),
        /// writes the block into the scratch and returns its error.
        /// </summary>
        static long Pack(ReadOnlySpan<int> t, int code, Bptc.Bc6Mode mode, int part, bool signed, Scratch sc)
        {
            int regions = mode.Regions, prec = mode.Prec, ib = regions == 2 ? 3 : 4, top = (1 << ib) - 1;
            int anchor1 = regions == 2 ? Bptc.Anchor2[part] : 0;
            Span<byte> m = stackalloc byte[16];
            int Members(int r, Span<byte> m)
            {
                int n = 0;
                for (int i = 0; i < 16; i++) if ((regions == 2 ? Bptc.Partition2[part] >> i & 1 : 0) == r) m[n++] = (byte)i;
                return n;
            }
            for (int r = 0; r < regions; r++)
            {
                var reg = sc.R[r];
                int a = r == 0 ? 0 : anchor1;
                if (reg.Index[a] > top >> 1)
                {
                    for (int c = 0; c < 3; c++) (reg.Q[c], reg.Q[3 + c]) = (reg.Q[3 + c], reg.Q[c]);
                    int n = Members(r, m);
                    for (int k = 0; k < n; k++) reg.Index[m[k]] = (byte)(top - reg.Index[m[k]]);
                }
            }
            // endpoint values: A0 = R0.A, B0 = R0.B, A1 = R1.A, B1 = R1.B
            Span<int> e = stackalloc int[12];
            for (int c = 0; c < 3; c++)
            {
                e[c] = sc.R[0].Q[c]; e[3 + c] = sc.R[0].Q[3 + c];
                if (regions == 2) { e[6 + c] = sc.R[1].Q[c]; e[9 + c] = sc.R[1].Q[3 + c]; }
            }
            int ne = regions * 2;
            bool clamped = false;
            Span<int> f = stackalloc int[13];
            int precMask = (1 << prec) - 1;
            for (int c = 0; c < 3; c++)
            {
                f[c] = e[c] & precMask;
                int db = mode.Delta[c];
                for (int k = 1; k < ne; k++)
                {
                    if (mode.Transformed)
                    {
                        // difference modulo 2^prec, as a signed number
                        int d = (e[k * 3 + c] - e[c]) & precMask;
                        if (d >= 1 << (prec - 1)) d -= 1 << prec;
                        int lo = -(1 << (db - 1)), hi = (1 << (db - 1)) - 1;
                        if (d < lo || d > hi)
                        {
                            d = Math.Clamp(d, lo, hi);
                            int v = (e[c] + d) & precMask;
                            e[k * 3 + c] = signed ? Bptc.SignExtend(v, prec) : v;
                            clamped = true;
                        }
                        f[k * 3 + c] = d & ((1 << db) - 1);
                    }
                    else f[k * 3 + c] = e[k * 3 + c] & ((1 << db) - 1);
                }
            }
            f[12] = part;
            long err;
            if (clamped)
            {
                // endpoints moved: new indices; a swap would change the deltas, so an anchor that lands in the upper half is limited
                for (int r = 0; r < regions; r++)
                    for (int c = 0; c < 3; c++) { sc.R[r].Q[c] = e[r * 6 + c]; sc.R[r].Q[3 + c] = e[r * 6 + 3 + c]; }
                err = 0;
                for (int r = 0; r < regions; r++)
                {
                    int n = Members(r, m);
                    err += Assign(t, m, n, sc.R[r], prec, ib, signed);
                    int a = r == 0 ? 0 : anchor1;
                    if (sc.R[r].Index[a] > top >> 1) LimitAnchor(t, sc.R[r], a, prec, ib, signed, ref err);
                }
            }
            else
            {
                err = sc.R[0].Error + (regions == 2 ? sc.R[1].Error : 0);
            }

            var bw = new BitWriter();
            bw.Write(code, mode.ModeBits);
            foreach (var (field, bit) in mode.Layout) bw.Write(f[field] >> bit, 1);
            for (int i = 0; i < 16; i++)
            {
                int r = regions == 2 ? Bptc.Partition2[part] >> i & 1 : 0;
                bool anchor = i == 0 || regions == 2 && i == anchor1;
                bw.Write(sc.R[r].Index[i], anchor ? ib - 1 : ib);
            }
            bw.Store(sc.Block);
            return err;
        }

        /// <summary>Restricts the anchor texel to the indices whose top bit is clear (its error grows accordingly).</summary>
        static void LimitAnchor(ReadOnlySpan<int> t, Region r, int i, int prec, int ib, bool signed, ref long err)
        {
            var w = Bptc.Weights(ib);
            Span<int> e = stackalloc int[6];
            for (int k = 0; k < 6; k++) e[k] = Bptc.Unquantize(r.Q[k], prec, signed);
            long old = AnchorError(t, e, w, r.Index[i], i, signed), be = long.MaxValue; int bi = 0;
            for (int k = 0; k < (1 << (ib - 1)); k++) { long x = AnchorError(t, e, w, k, i, signed); if (x < be) { be = x; bi = k; } }
            r.Index[i] = (byte)bi;
            err += be - old;
        }

        static long AnchorError(ReadOnlySpan<int> t, ReadOnlySpan<int> e, ReadOnlySpan<byte> w, int k, int i, bool signed)
        {
            long s = 0;
            for (int c = 0; c < 3; c++)
            {
                long d = t[i * 3 + c] - Decoded((e[c] * (64 - w[k]) + e[3 + c] * w[k] + 32) >> 6, signed);
                s += d * d;
            }
            return s;
        }
    }
}
