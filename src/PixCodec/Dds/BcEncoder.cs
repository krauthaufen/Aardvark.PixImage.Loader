using System;
using System.Buffers.Binary;

namespace PixCodec.DdsImpl
{
    /// <summary>
    /// BC1 to BC5 block encoding. Endpoints come from the principal axis of the block's colours, refined by least squares
    /// fits to the chosen indices; the higher quality levels search every ordered split of the texels along the axis into
    /// palette clusters ("cluster fit") and probe neighbouring quantized endpoints. Errors are measured on the colours the
    /// decoder (<see cref="BcBlocks"/>) produces.
    /// </summary>
    internal static class BcEncoder
    {
        static int Five(int v) => (v * 255 + 15) / 31;
        static int Six(int v) => (v * 255 + 31) / 63;
        static int RoundDiv(int a, int d) => (2 * a + d) / (2 * d);

        static int Pack565(int r, int g, int b) => r << 11 | g << 5 | b;

        /// <summary>Palette of a colour block (4 RGB entries, 3 ints each), as <see cref="BcBlocks.Color"/> decodes it.</summary>
        static void Palette(int c0, int c1, bool four, Span<int> pal)
        {
            int r0 = c0 >> 11, g0 = c0 >> 5 & 63, b0 = c0 & 31, r1 = c1 >> 11, g1 = c1 >> 5 & 63, b1 = c1 & 31;
            pal[0] = Five(r0); pal[1] = Six(g0); pal[2] = Five(b0);
            pal[3] = Five(r1); pal[4] = Six(g1); pal[5] = Five(b1);
            if (four)
            {
                pal[6] = RoundDiv(255 * (2 * r0 + r1), 93); pal[7] = RoundDiv(255 * (2 * g0 + g1), 189); pal[8] = RoundDiv(255 * (2 * b0 + b1), 93);
                pal[9] = RoundDiv(255 * (r0 + 2 * r1), 93); pal[10] = RoundDiv(255 * (g0 + 2 * g1), 189); pal[11] = RoundDiv(255 * (b0 + 2 * b1), 93);
            }
            else
            {
                pal[6] = RoundDiv(255 * (r0 + r1), 62); pal[7] = RoundDiv(255 * (g0 + g1), 126); pal[8] = RoundDiv(255 * (b0 + b1), 62);
                pal[9] = pal[10] = pal[11] = 0;
            }
        }

        // ------------------------------------------------------------------ single colour tables

        // best (e0, e1) quantized pair whose 1/3 point decodes to v, for 5 and 6 bits; and the midpoint for three colour mode
        static readonly (byte e0, byte e1)[] Single5 = SingleTable(5, false), Single6 = SingleTable(6, false);

        static (byte, byte)[] SingleTable(int bits, bool half)
        {
            int max = (1 << bits) - 1, den = bits == 5 ? 93 : 189;
            var t = new (byte, byte)[256];
            for (int v = 0; v < 256; v++)
            {
                int best = int.MaxValue;
                for (int a = 0; a <= max; a++)
                    for (int b = 0; b <= max; b++)
                    {
                        int x = RoundDiv(255 * (2 * a + b), den);
                        // prefer close endpoints: decoders that round differently then still land near v
                        int e = Math.Abs(x - v) * 1000 + Math.Abs(a - b);
                        if (e < best) { best = e; t[v] = ((byte)a, (byte)b); }
                    }
            }
            return t;
        }

        // ------------------------------------------------------------------ colour blocks (BC1 / BC2 / BC3)

        /// <summary>
        /// Encodes the colour part of a block (8 bytes) from 16 RGBA texels. <paramref name="punchThrough"/>: texels with
        /// alpha &lt; 128 become transparent (BC1 three colour mode). <paramref name="fourOnly"/>: BC2 / BC3, which always decode
        /// four colours.
        /// </summary>
        public static void Color(ReadOnlySpan<byte> px, Span<byte> o, bool punchThrough, bool fourOnly, BcQuality q)
        {
            Span<bool> transparent = stackalloc bool[16];
            int opaque = 0;
            for (int i = 0; i < 16; i++) { transparent[i] = punchThrough && px[i * 4 + 3] < 128; if (!transparent[i]) opaque++; }
            if (opaque == 0)
            {
                // three colour mode, every index 3
                BinaryPrimitives.WriteUInt16LittleEndian(o, 0);
                BinaryPrimitives.WriteUInt16LittleEndian(o.Slice(2), 0);
                BinaryPrimitives.WriteUInt32LittleEndian(o.Slice(4), 0xFFFFFFFF);
                return;
            }
            bool anyTransparent = opaque < 16;

            Span<float> pts = stackalloc float[48];
            int n = 0;
            for (int i = 0; i < 16; i++)
                if (!transparent[i]) { pts[n * 3] = px[i * 4]; pts[n * 3 + 1] = px[i * 4 + 1]; pts[n * 3 + 2] = px[i * 4 + 2]; n++; }

            var best = new Candidate { Error = long.MaxValue };
            bool solid = true;
            for (int k = 1; k < n && solid; k++)
                solid = pts[k * 3] == pts[0] && pts[k * 3 + 1] == pts[1] && pts[k * 3 + 2] == pts[2];
            if (solid && !anyTransparent)
            {
                int r = (int)pts[0], g = (int)pts[1], b = (int)pts[2];
                var (r0, r1) = Single5[r]; var (g0, g1) = Single6[g]; var (b0, b1) = Single5[b];
                Try(Pack565(r0, g0, b0), Pack565(r1, g1, b1), true, px, transparent, ref best);
            }

            Span<float> axis = stackalloc float[3];
            Span<float> mean = stackalloc float[3];
            PrincipalAxis(pts.Slice(0, n * 3), n, 3, mean, axis);

            bool tryFour = !anyTransparent, tryThree = anyTransparent || (!fourOnly && q >= BcQuality.High);
            if (q < BcQuality.High || n < 3)
            {
                // extremes along the axis, then least squares refinement
                Extremes(pts, n, mean, axis, out var lo, out var hi);
                int it = q == BcQuality.Fast ? 2 : 4;
                if (tryFour) Refine(lo, hi, true, pts, n, px, transparent, ref best, it);
                if (tryThree) Refine(lo, hi, false, pts, n, px, transparent, ref best, it);
            }
            else
            {
                if (tryFour) ClusterFit(pts, n, mean, axis, true, px, transparent, ref best, q);
                if (tryThree) ClusterFit(pts, n, mean, axis, false, px, transparent, ref best, q);
            }
            if (q >= BcQuality.Normal) Perturb(px, transparent, ref best);
            Emit(best, px, transparent, fourOnly, o);
        }

        static readonly float[] Levels4 = { 0f, 1 / 3f, 2 / 3f, 1f }, Levels3 = { 0f, 0.5f, 1f };
        static readonly float[] Weights4 = { 0f, 1f, 1 / 3f, 2 / 3f }, Weights3 = { 0f, 1f, 0.5f };

        struct Candidate
        {
            public int C0, C1;
            public bool Four;
            public long Error;
        }

        /// <summary>Evaluates the endpoints (any order) in the given mode, keeping them if better.</summary>
        static void Try(int c0, int c1, bool four, ReadOnlySpan<byte> px, ReadOnlySpan<bool> transparent, ref Candidate best)
        {
            // order for the mode: four colours need c0 > c1, three colours c0 <= c1
            if (four ? c0 < c1 : c0 > c1) (c0, c1) = (c1, c0);
            if (four && c0 == c1)
            {
                // cannot express four colour mode; the three colour mode with c0 == c1 decodes the same colour at index 0
                four = false;
            }
            long err = BlockError(c0, c1, four, px, transparent, null);
            if (err < best.Error) best = new Candidate { C0 = c0, C1 = c1, Four = four, Error = err };
        }

        static long BlockError(int c0, int c1, bool four, ReadOnlySpan<byte> px, ReadOnlySpan<bool> transparent, Span<byte> indices)
        {
            Span<int> pal = stackalloc int[12];
            Palette(c0, c1, four, pal);
            int choices = four ? 4 : 3;
            long total = 0;
            for (int i = 0; i < 16; i++)
            {
                if (transparent[i]) { if (!indices.IsEmpty) indices[i] = 3; continue; }
                int r = px[i * 4], g = px[i * 4 + 1], b = px[i * 4 + 2];
                int be = int.MaxValue, bi = 0;
                for (int k = 0; k < choices; k++)
                {
                    int dr = r - pal[k * 3], dg = g - pal[k * 3 + 1], db = b - pal[k * 3 + 2];
                    int e = dr * dr + dg * dg + db * db;
                    if (e < be) { be = e; bi = k; }
                }
                total += be;
                if (!indices.IsEmpty) indices[i] = (byte)bi;
            }
            return total;
        }

        static void Emit(Candidate c, ReadOnlySpan<byte> px, ReadOnlySpan<bool> transparent, bool fourOnly, Span<byte> o)
        {
            int c0 = c.C0, c1 = c.C1;
            Span<byte> idx = stackalloc byte[16];
            BlockError(c0, c1, c.Four, px, transparent, idx);
            if (fourOnly && !c.Four)
            {
                // BC2 / BC3 decode four colours whatever the order: re-evaluate in that mode
                BlockError(c0, c1, true, px, transparent, idx);
            }
            BinaryPrimitives.WriteUInt16LittleEndian(o, (ushort)c0);
            BinaryPrimitives.WriteUInt16LittleEndian(o.Slice(2), (ushort)c1);
            uint bits = 0;
            for (int i = 0; i < 16; i++) bits |= (uint)idx[i] << (2 * i);
            BinaryPrimitives.WriteUInt32LittleEndian(o.Slice(4), bits);
        }

        static int Quantize565(ReadOnlySpan<float> c)
        {
            int r = Math.Clamp((int)MathF.Round(c[0] * 31 / 255f), 0, 31);
            int g = Math.Clamp((int)MathF.Round(c[1] * 63 / 255f), 0, 63);
            int b = Math.Clamp((int)MathF.Round(c[2] * 31 / 255f), 0, 31);
            return Pack565(r, g, b);
        }

        /// <summary>Mean and dominant direction (power iteration on the covariance) of n points of dim channels.</summary>
        internal static void PrincipalAxis(ReadOnlySpan<float> pts, int n, int dim, Span<float> mean, Span<float> axis)
        {
            mean.Clear();
            for (int i = 0; i < n; i++)
                for (int c = 0; c < dim; c++) mean[c] += pts[i * dim + c];
            for (int c = 0; c < dim; c++) mean[c] /= Math.Max(1, n);
            Span<float> cov = stackalloc float[16];
            cov.Clear();
            for (int i = 0; i < n; i++)
                for (int a = 0; a < dim; a++)
                {
                    float da = pts[i * dim + a] - mean[a];
                    for (int b = a; b < dim; b++) cov[a * 4 + b] += da * (pts[i * dim + b] - mean[b]);
                }
            for (int a = 0; a < dim; a++)
                for (int b = 0; b < a; b++) cov[a * 4 + b] = cov[b * 4 + a];
            // start from the largest diagonal entry's direction
            int start = 0;
            for (int c = 1; c < dim; c++) if (cov[c * 4 + c] > cov[start * 4 + start]) start = c;
            Span<float> v = stackalloc float[4];
            for (int c = 0; c < dim; c++) v[c] = cov[start * 4 + c];
            float len = 0;
            for (int c = 0; c < dim; c++) len += v[c] * v[c];
            if (len == 0) { axis.Clear(); axis[0] = 1; return; }
            Span<float> w = stackalloc float[4];
            for (int it = 0; it < 8; it++)
            {
                for (int a = 0; a < dim; a++)
                {
                    float s = 0;
                    for (int b = 0; b < dim; b++) s += cov[a * 4 + b] * v[b];
                    w[a] = s;
                }
                len = 0;
                for (int c = 0; c < dim; c++) len += w[c] * w[c];
                if (len == 0) break;
                len = 1 / MathF.Sqrt(len);
                for (int c = 0; c < dim; c++) v[c] = w[c] * len;
            }
            len = 0;
            for (int c = 0; c < dim; c++) len += v[c] * v[c];
            len = len > 0 ? 1 / MathF.Sqrt(len) : 0;
            for (int c = 0; c < dim; c++) axis[c] = v[c] * len;
        }

        static void Extremes(ReadOnlySpan<float> pts, int n, ReadOnlySpan<float> mean, ReadOnlySpan<float> axis, out (float, float, float) lo, out (float, float, float) hi)
        {
            float tmin = float.MaxValue, tmax = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                float t = (pts[i * 3] - mean[0]) * axis[0] + (pts[i * 3 + 1] - mean[1]) * axis[1] + (pts[i * 3 + 2] - mean[2]) * axis[2];
                tmin = Math.Min(tmin, t); tmax = Math.Max(tmax, t);
            }
            lo = (mean[0] + tmin * axis[0], mean[1] + tmin * axis[1], mean[2] + tmin * axis[2]);
            hi = (mean[0] + tmax * axis[0], mean[1] + tmax * axis[1], mean[2] + tmax * axis[2]);
        }

        /// <summary>Quantize, assign indices, refit endpoints by least squares; repeated.</summary>
        static void Refine((float, float, float) lo, (float, float, float) hi, bool four, ReadOnlySpan<float> pts, int n,
            ReadOnlySpan<byte> px, ReadOnlySpan<bool> transparent, ref Candidate best, int iterations)
        {
            Span<float> a = stackalloc float[3] { lo.Item1, lo.Item2, lo.Item3 };
            Span<float> b = stackalloc float[3] { hi.Item1, hi.Item2, hi.Item3 };
            Span<float> wts = stackalloc float[16];
            Span<int> pal = stackalloc int[12];
            for (int it = 0; it <= iterations; it++)
            {
                int c0 = Quantize565(a), c1 = Quantize565(b);
                Try(c0, c1, four, px, transparent, ref best);
                if (it == iterations) break;
                // weights of the nearest palette entries of the quantized endpoints (in the order a, b)
                Palette(c0, c1, four, pal);
                float[] wOf = four ? Weights4 : Weights3;
                for (int i = 0; i < n; i++)
                {
                    int be = int.MaxValue, bi = 0;
                    for (int k = 0; k < wOf.Length; k++)
                    {
                        float dr = pts[i * 3] - pal[k * 3], dg = pts[i * 3 + 1] - pal[k * 3 + 1], db = pts[i * 3 + 2] - pal[k * 3 + 2];
                        int e = (int)(dr * dr + dg * dg + db * db);
                        if (e < be) { be = e; bi = k; }
                    }
                    wts[i] = wOf[bi];
                }
                if (!LeastSquares(pts, wts, n, 3, a, b)) break;
            }
        }

        /// <summary>
        /// Endpoints a, b minimizing sum |(1 - w_i) a + w_i b - p_i|^2. False if the system is singular.
        /// </summary>
        internal static bool LeastSquares(ReadOnlySpan<float> pts, ReadOnlySpan<float> w, int n, int dim, Span<float> a, Span<float> b)
        {
            double aa = 0, bb = 0, ab = 0;
            Span<double> ax = stackalloc double[4], bx = stackalloc double[4];
            ax.Clear(); bx.Clear();
            for (int i = 0; i < n; i++)
            {
                double beta = w[i], alpha = 1 - beta;
                aa += alpha * alpha; bb += beta * beta; ab += alpha * beta;
                for (int c = 0; c < dim; c++) { ax[c] += alpha * pts[i * dim + c]; bx[c] += beta * pts[i * dim + c]; }
            }
            double det = aa * bb - ab * ab;
            if (Math.Abs(det) < 1e-9) return false;
            for (int c = 0; c < dim; c++)
            {
                a[c] = (float)((ax[c] * bb - bx[c] * ab) / det);
                b[c] = (float)((bx[c] * aa - ax[c] * ab) / det);
            }
            return true;
        }

        /// <summary>
        /// Every split of the texels, ordered along the axis, into the palette's clusters (in palette order along the line),
        /// each solved by least squares; the best few are evaluated exactly.
        /// </summary>
        static void ClusterFit(ReadOnlySpan<float> pts, int n, ReadOnlySpan<float> mean, ReadOnlySpan<float> axis, bool four,
            ReadOnlySpan<byte> px, ReadOnlySpan<bool> transparent, ref Candidate best, BcQuality q)
        {
            Span<float> t = stackalloc float[16];
            Span<int> order = stackalloc int[16];
            for (int i = 0; i < n; i++)
            {
                t[i] = (pts[i * 3] - mean[0]) * axis[0] + (pts[i * 3 + 1] - mean[1]) * axis[1] + (pts[i * 3 + 2] - mean[2]) * axis[2];
                order[i] = i;
            }
            // insertion sort by projection
            for (int i = 1; i < n; i++)
            {
                int k = order[i]; float v = t[k]; int j = i - 1;
                while (j >= 0 && t[order[j]] > v) { order[j + 1] = order[j]; j--; }
                order[j + 1] = k;
            }
            Span<float> sorted = stackalloc float[48];
            for (int i = 0; i < n; i++) { sorted[i * 3] = pts[order[i] * 3]; sorted[i * 3 + 1] = pts[order[i] * 3 + 1]; sorted[i * 3 + 2] = pts[order[i] * 3 + 2]; }

            // prefix sums for the closed form least squares of each split
            Span<float> pre = stackalloc float[17 * 3];
            pre.Clear();
            for (int i = 0; i < n; i++)
                for (int c = 0; c < 3; c++) pre[(i + 1) * 3 + c] = pre[i * 3 + c] + sorted[i * 3 + c];

            float[] levels = four ? Levels4 : Levels3;
            int nl = levels.Length;
            const int Keep = 4;
            Span<float> keepErr = stackalloc float[Keep];
            Span<float> keepEp = stackalloc float[Keep * 6];
            keepErr.Fill(float.MaxValue);
            Span<float> a = stackalloc float[3], b = stackalloc float[3];
            Span<int> bounds = stackalloc int[5];
            Span<double> ax = stackalloc double[3], bx = stackalloc double[3];

            // splits: cluster k holds sorted texels [s_k, s_{k+1})
            int s1Max = n, s2Max = n, s3Max = four ? n : 0;
            for (int s1 = 0; s1 <= s1Max; s1++)
                for (int s2 = s1; s2 <= s2Max; s2++)
                    for (int s3 = four ? s2 : n; s3 <= (four ? s3Max : n); s3++)
                    {
                        bounds[0] = 0; bounds[1] = s1; bounds[2] = s2; bounds[3] = four ? s3 : n; bounds[4] = n;
                        double aa = 0, bb = 0, ab = 0;
                        ax.Clear(); bx.Clear();
                        for (int k = 0; k < nl; k++)
                        {
                            int from = bounds[k], to = bounds[k + 1], cnt = to - from;
                            if (cnt == 0) continue;
                            double beta = levels[k], alpha = 1 - beta;
                            aa += cnt * alpha * alpha; bb += cnt * beta * beta; ab += cnt * alpha * beta;
                            for (int c = 0; c < 3; c++)
                            {
                                double s = pre[to * 3 + c] - pre[from * 3 + c];
                                ax[c] += alpha * s; bx[c] += beta * s;
                            }
                        }
                        double det = aa * bb - ab * ab;
                        if (Math.Abs(det) < 1e-9) continue;
                        float err = 0;
                        for (int c = 0; c < 3; c++)
                        {
                            a[c] = Math.Clamp((float)((ax[c] * bb - bx[c] * ab) / det), 0, 255);
                            b[c] = Math.Clamp((float)((bx[c] * aa - ax[c] * ab) / det), 0, 255);
                        }
                        // residual of the unquantized fit, up to the constant sum of |p|^2: per cluster n |e|^2 - 2 e . S
                        for (int k = 0; k < nl; k++)
                        {
                            int from = bounds[k], to = bounds[k + 1], cnt = to - from;
                            if (cnt == 0) continue;
                            float beta = levels[k], alpha = 1 - beta;
                            for (int c = 0; c < 3; c++)
                            {
                                float e = alpha * a[c] + beta * b[c];
                                err += cnt * e * e - 2 * e * (pre[to * 3 + c] - pre[from * 3 + c]);
                            }
                        }
                        // keep the best few
                        int worst = 0;
                        for (int k = 1; k < Keep; k++) if (keepErr[k] > keepErr[worst]) worst = k;
                        if (err < keepErr[worst])
                        {
                            keepErr[worst] = err;
                            for (int c = 0; c < 3; c++) { keepEp[worst * 6 + c] = a[c]; keepEp[worst * 6 + 3 + c] = b[c]; }
                        }
                    }
            for (int k = 0; k < Keep; k++)
            {
                if (keepErr[k] == float.MaxValue) continue;
                var lo = (keepEp[k * 6], keepEp[k * 6 + 1], keepEp[k * 6 + 2]);
                var hi = (keepEp[k * 6 + 3], keepEp[k * 6 + 4], keepEp[k * 6 + 5]);
                Refine(lo, hi, four, pts, n, px, transparent, ref best, q >= BcQuality.High ? 2 : 1);
            }
        }

        /// <summary>Greedy search over the quantized endpoints, one step in one channel at a time.</summary>
        static void Perturb(ReadOnlySpan<byte> px, ReadOnlySpan<bool> transparent, ref Candidate best)
        {
            bool improved = true;
            for (int round = 0; round < 8 && improved; round++)
            {
                improved = false;
                var cur = best;
                for (int e = 0; e < 2; e++)
                    for (int ch = 0; ch < 3; ch++)
                        for (int d = -1; d <= 1; d += 2)
                        {
                            int c = e == 0 ? cur.C0 : cur.C1;
                            int shift = ch == 0 ? 11 : ch == 1 ? 5 : 0, max = ch == 1 ? 63 : 31;
                            int v = (c >> shift & max) + d;
                            if (v < 0 || v > max) continue;
                            int nc = c & ~(max << shift) | v << shift;
                            int c0 = e == 0 ? nc : cur.C0, c1 = e == 0 ? cur.C1 : nc;
                            long before = best.Error;
                            Try(c0, c1, cur.Four, px, transparent, ref best);
                            if (best.Error < before) improved = true;
                        }
            }
        }

        // ------------------------------------------------------------------ BC2 explicit alpha

        public static void ExplicitAlpha(ReadOnlySpan<byte> px, Span<byte> o)
        {
            ulong bits = 0;
            for (int i = 0; i < 16; i++) bits |= (ulong)((px[i * 4 + 3] + 8) / 17) << (4 * i);
            BinaryPrimitives.WriteUInt64LittleEndian(o, bits);
        }

        // ------------------------------------------------------------------ single channel (BC3 alpha, BC4, BC5)

        /// <summary>The 8 entry palette of a channel block, as <see cref="BcBlocks.Channel"/> / <see cref="BcBlocks.ChannelSigned"/> decode it.</summary>
        static void ChannelPalette(int a0, int a1, bool signed, Span<int> pal)
        {
            int lo = signed ? -127 : 0, hi = signed ? 127 : 255;
            int v0 = signed ? Math.Max(a0, -127) : a0, v1 = signed ? Math.Max(a1, -127) : a1;
            pal[0] = v0; pal[1] = v1;
            if (a0 > a1)
                for (int k = 1; k < 7; k++) pal[k + 1] = RoundSigned((7 - k) * v0 + k * v1, 7);
            else
            {
                for (int k = 1; k < 5; k++) pal[k + 1] = RoundSigned((5 - k) * v0 + k * v1, 5);
                pal[6] = lo; pal[7] = hi;
            }
        }

        static int RoundSigned(int a, int d) => a >= 0 ? (2 * a + d) / (2 * d) : -((-2 * a + d) / (2 * d));

        static long ChannelError(int a0, int a1, bool signed, ReadOnlySpan<int> v, Span<byte> idx)
        {
            Span<int> pal = stackalloc int[8];
            ChannelPalette(a0, a1, signed, pal);
            long total = 0;
            for (int i = 0; i < 16; i++)
            {
                int be = int.MaxValue, bi = 0;
                for (int k = 0; k < 8; k++)
                {
                    int d = v[i] - pal[k];
                    if (d * d < be) { be = d * d; bi = k; }
                }
                total += be;
                if (!idx.IsEmpty) idx[i] = (byte)bi;
            }
            return total;
        }

        /// <summary>
        /// Encodes 16 values of one channel (0..255, or -127..127 when <paramref name="signed"/>) into an 8 byte block.
        /// </summary>
        public static void Channel(ReadOnlySpan<int> v, Span<byte> o, bool signed, BcQuality q)
        {
            int lo = signed ? -127 : 0, hi = signed ? 127 : 255;
            int min = int.MaxValue, max = int.MinValue;
            for (int i = 0; i < 16; i++) { min = Math.Min(min, v[i]); max = Math.Max(max, v[i]); }
            int b0, b1;
            long bestErr;
            if (min == max)
            {
                b0 = b1 = min;   // a0 <= a1 mode: index 0 is the value
                bestErr = 0;
            }
            else
            {
                // eight value mode: a0 > a1
                b0 = max; b1 = min;
                bestErr = ChannelError(b0, b1, signed, v, default);
                // six value mode, with the extremes 0 / 255 (or -127 / 127) as extra entries: endpoints from the inner values
                int imin = int.MaxValue, imax = int.MinValue;
                for (int i = 0; i < 16; i++)
                    if (v[i] != lo && v[i] != hi) { imin = Math.Min(imin, v[i]); imax = Math.Max(imax, v[i]); }
                if (imin <= imax)
                {
                    long e = ChannelError(imin, imax, signed, v, default);
                    if (e < bestErr) { bestErr = e; b0 = imin; b1 = imax; }
                }
                if (q >= BcQuality.Normal && bestErr > 0)
                {
                    // local search around both endpoints in each mode, then refinement by least squares
                    int radius = q >= BcQuality.High ? 6 : 3;
                    for (int mode = 0; mode < 2; mode++)
                    {
                        int c0 = mode == 0 ? max : imin, c1 = mode == 0 ? min : imax;
                        if (mode == 1 && imin > imax) continue;
                        for (int pass = 0; pass < 2; pass++)
                        {
                            int s0 = c0, s1 = c1;
                            for (int d0 = -radius; d0 <= radius; d0++)
                                for (int d1 = -radius; d1 <= radius; d1++)
                                {
                                    int x0 = Math.Clamp(s0 + d0, lo, hi), x1 = Math.Clamp(s1 + d1, lo, hi);
                                    if (mode == 0 ? x0 <= x1 : x0 > x1) continue;
                                    long e = ChannelError(x0, x1, signed, v, default);
                                    if (e < bestErr) { bestErr = e; b0 = x0; b1 = x1; c0 = x0; c1 = x1; }
                                }
                            if (bestErr == 0) break;
                        }
                    }
                }
            }
            Span<byte> idx = stackalloc byte[16];
            ChannelError(b0, b1, signed, v, idx);
            o[0] = (byte)b0; o[1] = (byte)b1;   // (signed: two's complement)
            ulong bits = 0;
            for (int i = 0; i < 16; i++) bits |= (ulong)idx[i] << (3 * i);
            for (int k = 0; k < 6; k++) o[2 + k] = (byte)(bits >> (8 * k));
        }
    }
}
