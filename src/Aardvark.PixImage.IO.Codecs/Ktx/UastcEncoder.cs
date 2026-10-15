using System;
using System.Threading.Tasks;

namespace Aardvark.ImageCodecs
{
    /// <summary>
    /// UASTC LDR 4x4 block encoding, written from Binomial's public domain "UASTC LDR 4x4 Texture Specification" (mode
    /// layouts, BISE endpoint ranges, partition tables, ETC1 hint semantics) and the ASTC sections of the Khronos Data
    /// Format Specification. For every block the encoder fits the applicable modes (principal axis, least squares
    /// endpoint refinement on the quantized endpoint grid, exact weight selection), ranks the partition patterns of the
    /// multi subset modes by their principal axis residual and keeps the mode with the least squared RGBA error.
    /// The transcoding hints are filled in: the ETC1 hints (flip, colour mode, intensity tables, bias) are chosen for
    /// the lowest ETC1 error of the decoded block, solid blocks carry their best ETC1 encoding, the alpha modes an EAC
    /// table / multiplier; the BC1 hints are left off (the transcoder then runs its full BC1 encoder).
    /// </summary>
    internal static class UastcEncoder
    {
        // mode prefix codes (value, length), first bit at bit 0 of the block
        static readonly (int Code, int Len)[] ModeCodes =
        {
            (0x1, 4), (0x35, 6), (0x1D, 5), (0x3, 5), (0x13, 5), (0xB, 5), (0x1B, 5), (0x7, 5), (0x17, 5), (0xF, 5),
            (0x2, 3), (0x0, 2), (0x6, 3), (0x1F, 5), (0xD, 5), (0x5, 7), (0x15, 6), (0x25, 6), (0x9, 4),
        };

        // ------------------------------------------------------------------ quantization tables

        sealed class Quant
        {
            public int Levels;
            public int[] Dec = Array.Empty<int>();       // BISE integer -> 0..255
            public int[] Sorted = Array.Empty<int>();    // integers ordered by decoded value
            public int[] Rank = Array.Empty<int>();      // integer -> position in Sorted
            public byte[] Nearest = new byte[256];       // 0..255 -> closest integer
        }

        static readonly Quant[] Quants = BuildQuants();

        static Quant[] BuildQuants()
        {
            var r = new Quant[21];
            foreach (int range in new[] { 7, 8, 11, 12, 13, 18, 19, 20 })
            {
                var (bits, tq) = BasisUastc.Range(range);
                int levels = (tq == 0 ? 1 : tq == 1 ? 3 : 5) << bits;
                var q = new Quant { Levels = levels, Dec = new int[levels] };
                for (int i = 0; i < levels; i++) q.Dec[i] = BasisUastc.Unquantize(i, range);
                q.Sorted = new int[levels];
                for (int i = 0; i < levels; i++) q.Sorted[i] = i;
                Array.Sort(q.Sorted, (a, b) => q.Dec[a].CompareTo(q.Dec[b]));
                q.Rank = new int[levels];
                for (int i = 0; i < levels; i++) q.Rank[q.Sorted[i]] = i;
                for (int v = 0; v < 256; v++)
                {
                    int best = 0, bd = int.MaxValue;
                    for (int i = 0; i < levels; i++)
                    {
                        int d = Math.Abs(q.Dec[i] - v);
                        if (d < bd) { bd = d; best = i; }
                    }
                    q.Nearest[v] = (byte)best;
                }
                r[range] = q;
            }
            return r;
        }

        // weight bits -> (6 bit weight value 0..64 -> closest weight index)
        static readonly byte[][] NearestWeight = BuildNearestWeight();

        static byte[][] BuildNearestWeight()
        {
            var r = new byte[6][];
            for (int b = 1; b <= 5; b++)
            {
                var wv = BasisUastc.WeightValues[b];
                r[b] = new byte[65];
                for (int v = 0; v <= 64; v++)
                {
                    int best = 0;
                    for (int i = 1; i < wv.Length; i++) if (Math.Abs(wv[i] - v) < Math.Abs(wv[best] - v)) best = i;
                    r[b][v] = (byte)best;
                }
            }
            return r;
        }

        /// <summary>ASTC weight application of the decoder (8 bit endpoints expanded to 16 bits, result's top byte).</summary>
        static int Interp(int l, int h, int w) => ((l * 257 * (64 - w) + h * 257 * w + 32) >> 6) >> 8;

        static readonly byte[][] Tables2 = BasisUastc.Partitions2, Tables3 = BasisUastc.Partitions3, Tables7 = BasisUastc.Partitions7;

        // ------------------------------------------------------------------ per thread state

        sealed class Cand
        {
            public int Mode, Pattern, Ccs;
            public long Err = long.MaxValue;
            public readonly int[] Ev = new int[18];      // BISE integers in UASTC order
            public readonly byte[] W = new byte[32];     // weight indices, texel major, planes interleaved
            public void CopyFrom(Cand c)
            {
                Mode = c.Mode; Pattern = c.Pattern; Ccs = c.Ccs; Err = c.Err;
                Array.Copy(c.Ev, Ev, 18); Array.Copy(c.W, W, 32);
            }
        }

        sealed class Scratch
        {
            public readonly int[] Px = new int[64];
            public readonly Cand Best = new(), Cur = new();
            public readonly int[] Lo = new int[4], Hi = new int[4], TLo = new int[4], THi = new int[4];
            public readonly byte[] Wm = new byte[16], Tw = new byte[16], Tw2 = new byte[16];
            public readonly int[] Pal = new int[32 * 4];
            public readonly byte[] Members = new byte[16 * 3];
            public readonly int[] Count = new int[3];
            public readonly (long est, int pat)[] Rank = new (long, int)[30];
            public readonly byte[] Decoded = new byte[64];
        }

        [ThreadStatic] static Scratch? scratch;
        /// <summary>The fitted squared error of the last non solid block of this thread (tests).</summary>
        [ThreadStatic] internal static long LastError;

        static readonly int[] ChRgb = { 0, 1, 2 }, ChRgba = { 0, 1, 2, 3 }, ChLa = { 0, 3 };
        static readonly int[] WRgb = { 1, 1, 1 }, WRgba = { 1, 1, 1, 1 }, WLa = { 3, 1 };

        // ------------------------------------------------------------------ public entry points

        /// <summary>
        /// Encodes an RGBA8 image (tightly packed, width * height * 4 bytes) to UASTC LDR 4x4 blocks in raster order;
        /// edge blocks repeat the last row / column. <paramref name="effort"/>: 0 fast, 1 normal, 2 high.
        /// </summary>
        public static byte[] EncodeImage(ReadOnlySpan<byte> rgba, int width, int height, int effort, int maxDegreeOfParallelism)
        {
            int bx = (width + 3) / 4, by = (height + 3) / 4;
            var o = new byte[(long)bx * by * 16];
            var src = rgba.ToArray();
            var par = new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism > 0 ? maxDegreeOfParallelism : -1 };
            Parallel.For(0, by, par, y =>
            {
                Span<byte> px = stackalloc byte[64];
                for (int x = 0; x < bx; x++)
                {
                    for (int t = 0; t < 16; t++)
                    {
                        int sx = Math.Min(x * 4 + (t & 3), width - 1), sy = Math.Min(y * 4 + (t >> 2), height - 1);
                        src.AsSpan((sy * width + sx) * 4, 4).CopyTo(px.Slice(t * 4));
                    }
                    EncodeBlock(px, o.AsSpan((y * bx + x) * 16, 16), effort);
                }
            });
            return o;
        }

        /// <summary>Encodes 16 RGBA8 texels (row-major) to one UASTC block.</summary>
        public static void EncodeBlock(ReadOnlySpan<byte> rgba, Span<byte> dst, int effort)
        {
            var s = scratch ??= new Scratch();
            var px = s.Px;
            bool solid = true, opaque = true, gray = true;
            for (int t = 0; t < 16; t++)
            {
                for (int c = 0; c < 4; c++) px[t * 4 + c] = rgba[t * 4 + c];
                if (rgba[t * 4 + 3] != 255) opaque = false;
                if (rgba[t * 4] != rgba[t * 4 + 1] || rgba[t * 4] != rgba[t * 4 + 2]) gray = false;
                if (t > 0 && (rgba[t * 4] != rgba[0] || rgba[t * 4 + 1] != rgba[1] || rgba[t * 4 + 2] != rgba[2] || rgba[t * 4 + 3] != rgba[3])) solid = false;
            }
            if (solid) { PackSolid(rgba[0], rgba[1], rgba[2], rgba[3], dst); return; }

            var best = s.Best;
            best.Err = long.MaxValue;
            int iters = effort == 0 ? 2 : effort == 1 ? 3 : 4;
            int topK = effort == 0 ? 1 : effort == 1 ? 3 : 8;
            if (opaque)
            {
                foreach (int m in effort == 0 ? new[] { 0, 18 } : new[] { 0, 1, 5, 18 }) Single(s, m, -1, iters, effort);
                for (int c = 0; c < 3; c++) Single(s, 6, c, iters, effort);
                Partitioned(s, Tables2, 2, new[] { 2, 4 }, topK, iters, effort);
                Partitioned(s, Tables7, 7, new[] { 7 }, topK, iters, effort);
                Partitioned(s, Tables3, 3, new[] { 3 }, topK, iters, effort);
            }
            else
            {
                foreach (int m in effort == 0 ? new[] { 10, 12 } : new[] { 10, 12, 14 }) Single(s, m, -1, iters, effort);
                for (int c = effort == 0 ? 3 : 0; c < 4; c++) { Single(s, 11, c, iters, effort); Single(s, 13, c, iters, effort); }
                Partitioned(s, Tables2, 2, new[] { 9 }, topK, iters, effort);
            }
            if (gray && best.Err > 0)
            {
                Single(s, 15, -1, iters, effort);
                Single(s, 17, 3, iters, effort);
                Partitioned(s, Tables2, 2, new[] { 16 }, topK, iters, effort);
            }
            LastError = best.Err;
            Pack(best, dst, 0, 0, 0, 0, 0, 13, 0);
            // transcoding hints from the decoded block
            BasisUastc.DecodeBlock(dst, s.Decoded);
            Etc1Hints(s.Decoded, effort >= 1, effort >= 2, out int flip, out int diff, out int i0, out int i1, out int bias);
            int tm = best.Mode is >= 9 and <= 17 ? Etc2AlphaHint(s.Decoded) : 0;
            Pack(best, dst, 0, flip, diff, i0, i1, bias, tm);
        }

        // ------------------------------------------------------------------ mode fitting

        static (int[] ch, int[] cw) Channels(int comps) => comps switch
        {
            2 => (ChLa, WLa),
            3 => (ChRgb, WRgb),
            _ => (ChRgba, WRgba),
        };

        /// <summary>A one subset mode; <paramref name="ccs"/> is the second plane's channel (dual plane modes) or -1.</summary>
        static void Single(Scratch s, int mode, int ccs, int iters, int effort)
        {
            var cur = s.Cur;
            int comps = BasisUastc.Modes[mode, 0], wbits = BasisUastc.Modes[mode, 3];
            var q = Quants[BasisUastc.Modes[mode, 4]];
            var (ch, cw) = Channels(comps);
            var members = s.Members.AsSpan(0, 16);
            for (int t = 0; t < 16; t++) members[t] = (byte)t;
            cur.Mode = mode; cur.Pattern = 0; cur.Ccs = Math.Max(ccs, 0);
            if (ccs < 0)
            {
                cur.Err = FitPlane(s, members, ch, cw, wbits, q, iters, effort, s.Lo, s.Hi, s.Wm);
                if (cur.Err >= s.Best.Err) return;
                for (int k = 0; k < comps; k++) { cur.Ev[k * 2] = s.Lo[k]; cur.Ev[k * 2 + 1] = s.Hi[k]; }
                for (int t = 0; t < 16; t++) cur.W[t] = s.Wm[t];
            }
            else
            {
                // channel ccs (its position among the mode's channels) on plane 1, the others on plane 0
                int ci = Array.IndexOf(ch, ccs);
                Span<int> ch0 = stackalloc int[3], cw0 = stackalloc int[3];
                int n0 = 0;
                for (int k = 0; k < comps; k++) if (k != ci) { ch0[n0] = ch[k]; cw0[n0] = cw[k]; n0++; }
                long e0 = FitPlane(s, members, ch0.Slice(0, n0), cw0.Slice(0, n0), wbits, q, iters, effort, s.Lo, s.Hi, s.Wm);
                if (e0 >= s.Best.Err) return;
                int j = 0;
                for (int k = 0; k < comps; k++) if (k != ci) { cur.Ev[k * 2] = s.Lo[j]; cur.Ev[k * 2 + 1] = s.Hi[j]; j++; }
                for (int t = 0; t < 16; t++) cur.W[t * 2] = s.Wm[t];
                long e1 = FitPlane(s, members, new[] { ch[ci] }, new[] { cw[ci] }, wbits, q, iters, effort, s.Lo, s.Hi, s.Wm);
                cur.Ev[ci * 2] = s.Lo[0]; cur.Ev[ci * 2 + 1] = s.Hi[0];
                for (int t = 0; t < 16; t++) cur.W[t * 2 + 1] = s.Wm[t];
                cur.Err = e0 + e1;
            }
            if (cur.Err < s.Best.Err) s.Best.CopyFrom(cur);
        }

        /// <summary>The multi subset <paramref name="modes"/> of one pattern table, on its best ranked patterns.</summary>
        static void Partitioned(Scratch s, byte[][] table, int tableId, int[] modes, int topK, int iters, int effort)
        {
            int comps = BasisUastc.Modes[modes[0], 0];
            var (ch, cw) = Channels(comps);
            var px = s.Px;
            int n = table.Length;
            int subsets = tableId == 3 ? 3 : 2;
            // rank the patterns by the residual of each subset's principal axis fit (moments from per texel products)
            Span<float> v = stackalloc float[16 * 4], prod = stackalloc float[16 * 10];
            Span<float> tot1 = stackalloc float[4], tot2 = stackalloc float[10];
            tot1.Clear(); tot2.Clear();
            for (int t = 0; t < 16; t++)
            {
                for (int a = 0; a < comps; a++) v[t * 4 + a] = px[t * 4 + ch[a]] * (cw[a] == 1 ? 1f : 1.7320508f);
                int kk = 0;
                for (int a = 0; a < comps; a++)
                    for (int b = a; b < comps; b++, kk++) prod[t * 10 + kk] = v[t * 4 + a] * v[t * 4 + b];
                for (int a = 0; a < comps; a++) tot1[a] += v[t * 4 + a];
                for (int j = 0; j < kk; j++) tot2[j] += prod[t * 10 + j];
            }
            int np = comps * (comps + 1) / 2;
            Span<float> m1 = stackalloc float[4 * 3], m2 = stackalloc float[10 * 3];
            Span<int> cnts = stackalloc int[3];
            Span<double> cov = stackalloc double[16];
            for (int p = 0; p < n; p++)
            {
                var part = table[p];
                m1.Clear(); m2.Clear(); cnts.Clear();
                for (int t = 0; t < 16; t++)
                {
                    int sub = part[t];
                    if (sub == 0) continue;   // subset 0: the total minus the others
                    cnts[sub]++;
                    for (int a = 0; a < comps; a++) m1[sub * 4 + a] += v[t * 4 + a];
                    for (int j = 0; j < np; j++) m2[sub * 10 + j] += prod[t * 10 + j];
                }
                cnts[0] = 16 - cnts[1] - cnts[2];
                for (int a = 0; a < comps; a++) m1[a] = tot1[a] - m1[4 + a] - m1[8 + a];
                for (int j = 0; j < np; j++) m2[j] = tot2[j] - m2[10 + j] - m2[20 + j];
                double est = 0;
                for (int sub = 0; sub < subsets; sub++)
                {
                    int c = cnts[sub];
                    if (c < 2) continue;
                    double trace = 0;
                    int kk = 0;
                    for (int a = 0; a < comps; a++)
                        for (int b = a; b < comps; b++, kk++)
                        {
                            double x = m2[sub * 10 + kk] - (double)m1[sub * 4 + a] * m1[sub * 4 + b] / c;
                            cov[a * 4 + b] = cov[b * 4 + a] = x;
                            if (a == b) trace += x;
                        }
                    est += trace - LargestEigen(cov, comps);
                }
                s.Rank[p] = ((long)(est * 16), p);
            }
            var rank = s.Rank.AsSpan(0, n);
            rank.Sort();
            int k = Math.Min(topK, n);
            var cur = s.Cur;
            foreach (int mode in modes)
            {
                int wbits = BasisUastc.Modes[mode, 3];
                var q = Quants[BasisUastc.Modes[mode, 4]];
                for (int r = 0; r < k; r++)
                {
                    int pat = rank[r].pat;
                    var part = table[pat];
                    cur.Mode = mode; cur.Pattern = pat; cur.Ccs = 0;
                    long err = 0;
                    for (int sub = 0; sub < subsets && err < s.Best.Err; sub++)
                    {
                        int cnt = 0;
                        for (int t = 0; t < 16; t++) if (part[t] == sub) s.Members[cnt++] = (byte)t;
                        var mem = s.Members.AsSpan(0, cnt);
                        err += FitPlane(s, mem, ch, cw, wbits, q, iters, effort, s.Lo, s.Hi, s.Wm);
                        for (int c = 0; c < comps; c++) { cur.Ev[(sub * comps + c) * 2] = s.Lo[c]; cur.Ev[(sub * comps + c) * 2 + 1] = s.Hi[c]; }
                        for (int i = 0; i < cnt; i++) cur.W[mem[i]] = s.Wm[i];
                    }
                    cur.Err = err;
                    if (err < s.Best.Err) s.Best.CopyFrom(cur);
                }
            }
        }

        static double LargestEigen(Span<double> cov, int n)
        {
            if (n == 2)
            {
                double h = (cov[0] - cov[5]) * 0.5;
                return (cov[0] + cov[5]) * 0.5 + Math.Sqrt(h * h + cov[1] * cov[1]);
            }
            if (n == 3)
            {
                // closed form for symmetric 3 x 3 matrices (trigonometric solution of the characteristic cubic)
                double a00 = cov[0], a11 = cov[5], a22 = cov[10], a01 = cov[1], a02 = cov[2], a12 = cov[6];
                double p1 = a01 * a01 + a02 * a02 + a12 * a12;
                if (p1 <= 1e-12) return Math.Max(a00, Math.Max(a11, a22));
                double q = (a00 + a11 + a22) / 3;
                double b00 = a00 - q, b11 = a11 - q, b22 = a22 - q;
                double p2 = b00 * b00 + b11 * b11 + b22 * b22 + 2 * p1;
                double pp = Math.Sqrt(p2 / 6);
                if (pp <= 1e-12) return q;
                double det = b00 * (b11 * b22 - a12 * a12) - a01 * (a01 * b22 - a12 * a02) + a02 * (a01 * a12 - b11 * a02);
                double r = Math.Clamp(det / (2 * pp * pp * pp), -1, 1);
                return q + 2 * pp * Math.Cos(Math.Acos(r) / 3);
            }
            Span<double> v = stackalloc double[4], u = stackalloc double[4];
            int best = 0;
            for (int i = 1; i < n; i++) if (cov[i * 4 + i] > cov[best * 4 + best]) best = i;
            if (cov[best * 4 + best] <= 0) return 0;
            for (int i = 0; i < n; i++) v[i] = cov[best * 4 + i];
            double lambda = 0;
            for (int it = 0; it < 4; it++)
            {
                double norm = 0;
                for (int i = 0; i < n; i++)
                {
                    double x = 0;
                    for (int j = 0; j < n; j++) x += cov[i * 4 + j] * v[j];
                    u[i] = x; norm += x * x;
                }
                norm = Math.Sqrt(norm);
                if (norm < 1e-12) return 0;
                double dot = 0;
                for (int i = 0; i < n; i++) dot += u[i] * v[i];
                double vn = 0;
                for (int i = 0; i < n; i++) vn += v[i] * v[i];
                lambda = dot / vn;
                for (int i = 0; i < n; i++) v[i] = u[i] / norm;
            }
            return lambda;
        }

        /// <summary>
        /// Fits one weight plane to the <paramref name="members"/> texels (raster order) on channels <paramref name="ch"/>:
        /// quantized endpoints <paramref name="qlo"/> / <paramref name="qhi"/> (BISE integers per channel) and a weight index
        /// per member, the anchor (first member) with its top bit clear. Returns the weighted squared error.
        /// </summary>
        static long FitPlane(Scratch s, ReadOnlySpan<byte> members, ReadOnlySpan<int> ch, ReadOnlySpan<int> cw, int wbits,
            Quant q, int iters, int effort, int[] qlo, int[] qhi, byte[] w)
        {
            var px = s.Px;
            int n = members.Length, nc = ch.Length;
            Span<float> mean = stackalloc float[4], axis = stackalloc float[4], lo = stackalloc float[4], hi = stackalloc float[4];
            Span<float> cov = stackalloc float[16];
            mean.Clear(); cov.Clear();
            for (int i = 0; i < n; i++)
                for (int c = 0; c < nc; c++) mean[c] += px[members[i] * 4 + ch[c]];
            for (int c = 0; c < nc; c++) mean[c] /= n;
            for (int i = 0; i < n; i++)
            {
                int t = members[i];
                for (int a = 0; a < nc; a++)
                {
                    float da = px[t * 4 + ch[a]] - mean[a];
                    for (int b = a; b < nc; b++) cov[a * 4 + b] += da * (px[t * 4 + ch[b]] - mean[b]);
                }
            }
            for (int a = 0; a < nc; a++) for (int b = 0; b < a; b++) cov[a * 4 + b] = cov[b * 4 + a];
            // principal axis by power iteration, from the channel of largest variance
            int big = 0;
            for (int c = 1; c < nc; c++) if (cov[c * 4 + c] > cov[big * 4 + big]) big = c;
            axis.Clear();
            if (cov[big * 4 + big] > 0)
            {
                for (int c = 0; c < nc; c++) axis[c] = cov[big * 4 + c];
                Span<float> u = stackalloc float[4];
                for (int it = 0; it < 8; it++)
                {
                    float norm = 0;
                    for (int a = 0; a < nc; a++)
                    {
                        float x = 0;
                        for (int b = 0; b < nc; b++) x += cov[a * 4 + b] * axis[b];
                        u[a] = x; norm += x * x;
                    }
                    if (norm <= 1e-20f) break;
                    norm = 1 / MathF.Sqrt(norm);
                    for (int a = 0; a < nc; a++) axis[a] = u[a] * norm;
                }
            }
            float tmin = 0, tmax = 0;
            for (int i = 0; i < n; i++)
            {
                int t = members[i];
                float d = 0;
                for (int c = 0; c < nc; c++) d += (px[t * 4 + ch[c]] - mean[c]) * axis[c];
                if (d < tmin) tmin = d;
                if (d > tmax) tmax = d;
            }
            for (int c = 0; c < nc; c++) { lo[c] = mean[c] + tmin * axis[c]; hi[c] = mean[c] + tmax * axis[c]; }

            var tlo = s.TLo; var thi = s.THi; var tw = s.Tw;
            long best = long.MaxValue;
            var wv = BasisUastc.WeightValues[wbits];
            for (int it = 0; it < iters; it++)
            {
                bool same = it > 0;
                for (int c = 0; c < nc; c++)
                {
                    int a = q.Nearest[Math.Clamp((int)MathF.Round(lo[c]), 0, 255)], b = q.Nearest[Math.Clamp((int)MathF.Round(hi[c]), 0, 255)];
                    if (a != tlo[c] || b != thi[c]) same = false;
                    tlo[c] = a; thi[c] = b;
                }
                if (same) break;
                long err = Eval(s, members, ch, cw, wbits, q, tlo, thi, tw);
                if (err < best)
                {
                    best = err;
                    for (int c = 0; c < nc; c++) { qlo[c] = tlo[c]; qhi[c] = thi[c]; }
                    tw.AsSpan(0, n).CopyTo(w);
                }
                if (err == 0 || it == iters - 1) break;
                // least squares endpoints for these weights, channel by channel
                float sa = 0, sb = 0, sc = 0;
                for (int i = 0; i < n; i++)
                {
                    float f = wv[tw[i]] / 64f;
                    sa += (1 - f) * (1 - f); sb += f * (1 - f); sc += f * f;
                }
                float det = sa * sc - sb * sb;
                if (Math.Abs(det) < 1e-6f) break;
                for (int c = 0; c < nc; c++)
                {
                    float x = 0, y = 0;
                    for (int i = 0; i < n; i++)
                    {
                        float f = wv[tw[i]] / 64f, p = px[members[i] * 4 + ch[c]];
                        x += (1 - f) * p; y += f * p;
                    }
                    lo[c] = (sc * x - sb * y) / det;
                    hi[c] = (sa * y - sb * x) / det;
                }
            }
            if (effort >= 2 && best > 0)
            {
                // endpoint refinement: one quantization step up / down per endpoint value
                for (int c = 0; c < nc; c++)
                    for (int side = 0; side < 2; side++)
                        for (int d = -1; d <= 1; d += 2)
                        {
                            for (int k = 0; k < nc; k++) { tlo[k] = qlo[k]; thi[k] = qhi[k]; }
                            var arr = side == 0 ? tlo : thi;
                            int r = q.Rank[arr[c]] + d;
                            if (r < 0 || r >= q.Levels) continue;
                            arr[c] = q.Sorted[r];
                            long err = Eval(s, members, ch, cw, wbits, q, tlo, thi, tw);
                            if (err < best)
                            {
                                best = err;
                                for (int k = 0; k < nc; k++) { qlo[k] = tlo[k]; qhi[k] = thi[k]; }
                                tw.AsSpan(0, n).CopyTo(w);
                            }
                        }
            }
            // anchor: the first member's weight index needs a clear top bit; swap the endpoints otherwise
            int levels = 1 << wbits;
            if (w[0] >= levels / 2)
            {
                for (int c = 0; c < nc; c++) (qlo[c], qhi[c]) = (qhi[c], qlo[c]);
                for (int i = 0; i < n; i++) w[i] = (byte)(levels - 1 - w[i]);
            }
            return best;
        }

        /// <summary>Best weight index per member for quantized endpoints; returns the weighted squared error.</summary>
        static long Eval(Scratch s, ReadOnlySpan<byte> members, ReadOnlySpan<int> ch, ReadOnlySpan<int> cw, int wbits, Quant q,
            int[] qlo, int[] qhi, byte[] w)
        {
            var px = s.Px;
            var pal = s.Pal;
            var wv = BasisUastc.WeightValues[wbits];
            int levels = 1 << wbits, nc = ch.Length;
            Span<int> el = stackalloc int[4], d = stackalloc int[4];
            int dd = 0;
            for (int c = 0; c < nc; c++)
            {
                el[c] = q.Dec[qlo[c]];
                int eh = q.Dec[qhi[c]];
                d[c] = eh - el[c];
                dd += d[c] * d[c];
                for (int i = 0; i < levels; i++) pal[i * 4 + c] = Interp(el[c], eh, wv[i]);
            }
            var nw = NearestWeight[wbits];
            long total = 0;
            for (int i = 0; i < members.Length; i++)
            {
                int t = members[i] * 4;
                int from, to;
                if (levels <= 4) { from = 0; to = levels - 1; }
                else if (dd == 0) { from = to = 0; }
                else
                {
                    int dot = 0;
                    for (int c = 0; c < nc; c++) dot += (px[t + ch[c]] - el[c]) * d[c];
                    int g = nw[Math.Clamp((dot * 64 + dd / 2) / dd, 0, 64)];
                    from = Math.Max(0, g - 1); to = Math.Min(levels - 1, g + 1);
                }
                int bestE = int.MaxValue, bi = from;
                for (int k = from; k <= to; k++)
                {
                    int e = 0;
                    for (int c = 0; c < nc; c++)
                    {
                        int x = pal[k * 4 + c] - px[t + ch[c]];
                        e += cw[c] * x * x;
                    }
                    if (e < bestE) { bestE = e; bi = k; }
                }
                w[i] = (byte)bi;
                total += bestE;
            }
            return total;
        }

        // ------------------------------------------------------------------ packing

        ref struct BitWriter
        {
            public ulong Lo, Hi;
            public int Pos;
            public void Put(int value, int n)
            {
                if (n == 0) return;
                ulong v = (ulong)(uint)value & ((1UL << n) - 1);
                if (Pos < 64)
                {
                    Lo |= v << Pos;
                    if (Pos + n > 64) Hi |= v >> (64 - Pos);
                }
                else Hi |= v << (Pos - 64);
                Pos += n;
            }
            public void Store(Span<byte> d)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(d, Lo);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(d.Slice(8), Hi);
            }
        }

        static void Pack(Cand c, Span<byte> dst, int bc1, int flip, int diff, int i0, int i1, int bias, int tm)
        {
            int mode = c.Mode;
            var bw = new BitWriter();
            bw.Put(ModeCodes[mode].Code, ModeCodes[mode].Len);
            bool noH1 = mode is 10 or 11 or 12;
            bw.Put(bc1, 1);
            if (!noH1) bw.Put(0, 1);
            bw.Put(flip, 1); bw.Put(diff, 1); bw.Put(i0, 3); bw.Put(i1, 3);
            if (!noH1) bw.Put(bias, 5);
            if (mode is >= 9 and <= 17) bw.Put(tm, 8);
            int comps = BasisUastc.Modes[mode, 0], subsets = BasisUastc.Modes[mode, 1], planes = BasisUastc.Modes[mode, 2];
            int wbits = BasisUastc.Modes[mode, 3], range = BasisUastc.Modes[mode, 4];
            if (BasisUastc.Modes[mode, 5] != 0) bw.Put(c.Pattern, BasisUastc.Modes[mode, 7]);
            if (BasisUastc.Modes[mode, 8] >= 0) bw.Put(c.Ccs, 2);
            System.Diagnostics.Debug.Assert(bw.Pos == BasisUastc.Modes[mode, 9]);
            // endpoints: trit / quint codes of all values first, then the low bits of each
            int nv = comps * 2 * subsets;
            var (bits, tq) = BasisUastc.Range(range);
            if (tq != 0)
            {
                int bse = tq == 1 ? 3 : 5, group = tq == 1 ? 5 : 3;
                for (int g = 0; g < nv; g += group)
                {
                    int k = Math.Min(group, nv - g), max = 1, code = 0, mul = 1;
                    for (int i = 0; i < k; i++) { code += (c.Ev[g + i] >> bits) * mul; mul *= bse; max *= bse; }
                    bw.Put(code, 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)(max - 1)));
                }
            }
            for (int i = 0; i < nv; i++) bw.Put(c.Ev[i] & ((1 << bits) - 1), bits);
            // weights, the anchor of each subset with one bit less
            byte[] part = BasisUastc.Modes[mode, 5] switch
            {
                2 => Tables2[c.Pattern],
                3 => Tables3[c.Pattern],
                7 => Tables7[c.Pattern],
                _ => Zero16,
            };
            int seen = 0;
            for (int t = 0; t < 16; t++)
            {
                int b = 1 << part[t];
                int nb = (seen & b) == 0 ? wbits - 1 : wbits;
                seen |= b;
                for (int p = 0; p < planes; p++) bw.Put(c.W[t * planes + p], nb);
            }
            bw.Store(dst);
        }

        static readonly byte[] Zero16 = new byte[16];

        // ETC1 intensity modifier tables (Khronos Data Format Specification): small and large magnitude
        static readonly int[] IntenA = { 2, 5, 9, 13, 18, 24, 33, 47 };
        static readonly int[] IntenB = { 8, 17, 29, 42, 60, 80, 106, 183 };

        static int Ext5(int v) => v << 3 | v >> 2;

        /// <summary>Mode 8 (void extent): the colour and the best ETC1 encoding of it (differential, 5 bit colour).</summary>
        static void PackSolid(int r, int g, int b, int a, Span<byte> dst)
        {
            long bestE = long.MaxValue;
            int bt = 0, bs = 0, br = 0, bg = 0, bb = 0;
            Span<int> col = stackalloc int[] { r, g, b };
            Span<int> q = stackalloc int[3];
            for (int t = 0; t < 8; t++)
                for (int sel = 0; sel < 4; sel++)
                {
                    int mod = sel switch { 0 => -IntenB[t], 1 => -IntenA[t], 2 => IntenA[t], _ => IntenB[t] };
                    long e = 0;
                    for (int c = 0; c < 3; c++)
                    {
                        int bestC = 0, be = int.MaxValue;
                        for (int v = 0; v < 32; v++)
                        {
                            int x = Math.Clamp(Ext5(v) + mod, 0, 255) - col[c];
                            if (x * x < be) { be = x * x; bestC = v; }
                        }
                        q[c] = bestC; e += be;
                    }
                    if (e < bestE) { bestE = e; bt = t; bs = sel; br = q[0]; bg = q[1]; bb = q[2]; }
                }
            var bw = new BitWriter();
            bw.Put(ModeCodes[8].Code, ModeCodes[8].Len);
            bw.Put(r, 8); bw.Put(g, 8); bw.Put(b, 8); bw.Put(a, 8);
            bw.Put(1, 1); bw.Put(bt, 3); bw.Put(bs, 2); bw.Put(br, 5); bw.Put(bg, 5); bw.Put(bb, 5);
            bw.Store(dst);
        }

        // ------------------------------------------------------------------ transcoding hints

        /// <summary>The UASTC specification's apply_etc1_bias: bias 13 changes nothing.</summary>
        internal static int ApplyBias(int v, int bias, int limit, int subblock, int c)
        {
            int delta;
            switch (bias)
            {
                case 2: delta = subblock != 0 ? 0 : (c == 0 ? -1 : 0); break;
                case 5: delta = subblock != 0 ? 0 : (c == 1 ? -1 : 0); break;
                case 6: delta = subblock != 0 ? 0 : (c == 2 ? -1 : 0); break;
                case 7: delta = subblock != 0 ? 0 : (c == 0 ? 1 : 0); break;
                case 11: delta = subblock != 0 ? 0 : (c == 1 ? 1 : 0); break;
                case 15: delta = subblock != 0 ? 0 : (c == 2 ? 1 : 0); break;
                case 18: delta = subblock != 0 ? (c == 0 ? -1 : 0) : 0; break;
                case 19: delta = subblock != 0 ? (c == 1 ? -1 : 0) : 0; break;
                case 20: delta = subblock != 0 ? (c == 2 ? -1 : 0) : 0; break;
                case 21: delta = subblock != 0 ? (c == 0 ? 1 : 0) : 0; break;
                case 24: delta = subblock != 0 ? (c == 1 ? 1 : 0) : 0; break;
                case 8: delta = subblock != 0 ? (c == 2 ? 1 : 0) : 0; break;
                case 10: delta = -2; break;
                case 27: delta = subblock != 0 ? 0 : -1; break;
                case 28: delta = subblock != 0 ? -1 : 1; break;
                case 29: delta = subblock != 0 ? 1 : 0; break;
                case 30: delta = subblock != 0 ? -1 : 0; break;
                case 31: delta = subblock != 0 ? 0 : 1; break;
                default: delta = bias / (c == 0 ? 1 : c == 1 ? 3 : 9) % 3 - 1; break;
            }
            if (v == 0) v += delta == -2 ? 3 : delta + 1;
            else if (v == limit) v += delta - 1;
            else
            {
                v += delta;
                if (v < 0 || v > limit) v = v - delta - delta;
            }
            return v;
        }

        /// <summary>
        /// ETC1 hints for the decoded block: the transcoder averages each subblock, quantizes with rounding, applies the
        /// bias and picks the texel selectors, so the hints are chosen for the least error of exactly that.
        /// </summary>
        static void Etc1Hints(ReadOnlySpan<byte> px, bool searchBias, bool full, out int bestFlip, out int bestDiff, out int bestI0, out int bestI1, out int bestBias)
        {
            bestFlip = 0; bestDiff = 1; bestI0 = 0; bestI1 = 0; bestBias = 13;
            long bestE = long.MaxValue;
            Span<float> avg = stackalloc float[6];
            for (int flip = 0; flip < 2; flip++)
            {
                avg.Clear();
                for (int t = 0; t < 16; t++)
                {
                    int sb = flip == 0 ? ((t & 3) >= 2 ? 1 : 0) : (t >= 8 ? 1 : 0);
                    for (int c = 0; c < 3; c++) avg[sb * 3 + c] += px[t * 4 + c];
                }
                for (int i = 0; i < 6; i++) avg[i] /= 8;
                for (int diff = 0; diff < 2; diff++)
                {
                    long e = Etc1Error(px, avg, flip, diff, 13, out int i0, out int i1);
                    if (e < bestE) { bestE = e; bestFlip = flip; bestDiff = diff; bestI0 = i0; bestI1 = i1; bestBias = 13; }
                }
            }
            if (!searchBias) return;
            avg.Clear();
            for (int t = 0; t < 16; t++)
            {
                int sb = bestFlip == 0 ? ((t & 3) >= 2 ? 1 : 0) : (t >= 8 ? 1 : 0);
                for (int c = 0; c < 3; c++) avg[sb * 3 + c] += px[t * 4 + c];
            }
            for (int i = 0; i < 6; i++) avg[i] /= 8;
            // effort 1: the biases with the intensity tables of the neutral bias, the tables again for the best; effort 2: all
            int t0 = full ? -1 : bestI0, t1 = full ? -1 : bestI1, found = 13;
            for (int bias = 0; bias < 32; bias++)
            {
                if (bias == 13) continue;
                long e = Etc1Error(px, avg, bestFlip, bestDiff, bias, out int i0, out int i1, t0, t1);
                if (e < bestE) { bestE = e; bestI0 = i0; bestI1 = i1; bestBias = bias; found = bias; }
            }
            if (!full && found != 13)
            {
                Etc1Error(px, avg, bestFlip, bestDiff, found, out int i0, out int i1);
                bestI0 = i0; bestI1 = i1;
            }
        }

        static long Etc1Error(ReadOnlySpan<byte> px, ReadOnlySpan<float> avg, int flip, int diff, int bias, out int i0, out int i1, int fix0 = -1, int fix1 = -1)
        {
            Span<int> baseCol = stackalloc int[6];
            int limit = diff != 0 ? 31 : 15;
            for (int sb = 0; sb < 2; sb++)
                for (int c = 0; c < 3; c++)
                {
                    int v = (int)(avg[sb * 3 + c] * limit / 255f + 0.5f);
                    baseCol[sb * 3 + c] = ApplyBias(v, bias, limit, sb, c);
                }
            if (diff != 0)
                for (int c = 0; c < 3; c++)
                    baseCol[3 + c] = baseCol[c] + Math.Clamp(baseCol[3 + c] - baseCol[c], -4, 3);
            for (int i = 0; i < 6; i++) baseCol[i] = diff != 0 ? Ext5(baseCol[i]) : baseCol[i] * 17;
            long total = 0;
            i0 = i1 = 0;
            for (int sb = 0; sb < 2; sb++)
            {
                long best = long.MaxValue;
                int bt = 0;
                int fix = sb == 0 ? fix0 : fix1;
                for (int t = fix < 0 ? 0 : fix; t < (fix < 0 ? 8 : fix + 1); t++)
                {
                    // the selector the transcoder picks: nearest modifier to the texel's mean offset from the base (luma)
                    long e = 0;
                    int ia = IntenA[t], ib = IntenB[t], half = ia + ib;   // thresholds at -half / 2, 0, half / 2 (doubled)
                    int bsum = baseCol[sb * 3] + baseCol[sb * 3 + 1] + baseCol[sb * 3 + 2];
                    for (int p = 0; p < 16; p++)
                    {
                        int s = flip == 0 ? ((p & 3) >= 2 ? 1 : 0) : (p >= 8 ? 1 : 0);
                        if (s != sb) continue;
                        int d2 = 2 * (px[p * 4] + px[p * 4 + 1] + px[p * 4 + 2] - bsum);   // 6 * mean offset
                        int mod = d2 < -3 * half ? -ib : d2 < 0 ? -ia : d2 < 3 * half ? ia : ib;
                        for (int c = 0; c < 3; c++)
                        {
                            int x = Math.Clamp(baseCol[sb * 3 + c] + mod, 0, 255) - px[p * 4 + c];
                            e += x * x;
                        }
                    }
                    if (e < best) { best = e; bt = t; }
                }
                total += best;
                if (sb == 0) i0 = bt; else i1 = bt;
            }
            return total;
        }

        /// <summary>ETC2 EAC A8 hint: table (low 4 bits) and multiplier (high 4 bits) for the decoded alpha, base at mid range.</summary>
        static int Etc2AlphaHint(ReadOnlySpan<byte> px)
        {
            int mn = 255, mx = 0;
            for (int t = 0; t < 16; t++) { mn = Math.Min(mn, px[t * 4 + 3]); mx = Math.Max(mx, px[t * 4 + 3]); }
            if (mn == mx) return 13 | 1 << 4;
            int baseV = (mn + mx + 1) / 2;
            var mods = EtcBlocks.EacMod;
            long bestE = long.MaxValue;
            int best = 0;
            for (int table = 0; table < 16; table++)
            {
                int tmin = 0, tmax = 0;
                for (int k = 0; k < 8; k++) { tmin = Math.Min(tmin, mods[table * 8 + k]); tmax = Math.Max(tmax, mods[table * 8 + k]); }
                int m0 = Math.Clamp((mx - mn + (tmax - tmin) / 2) / (tmax - tmin), 1, 15);
                for (int mul = Math.Max(1, m0 - 1); mul <= Math.Min(15, m0 + 1); mul++)
                {
                    long e = 0;
                    for (int t = 0; t < 16; t++)
                    {
                        int a = px[t * 4 + 3], be = int.MaxValue;
                        for (int k = 0; k < 8; k++)
                        {
                            int x = Math.Clamp(baseV + mods[table * 8 + k] * mul, 0, 255) - a;
                            if (x * x < be) be = x * x;
                        }
                        e += be;
                    }
                    if (e < bestE) { bestE = e; best = table | mul << 4; }
                }
            }
            return best;
        }
    }
}
