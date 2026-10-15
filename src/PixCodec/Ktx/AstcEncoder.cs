using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;

namespace PixCodec
{
    /// <summary>
    /// ASTC block encoding for 2D footprints (4x4 to 12x12): LDR (UNORM8 or sRGB input) and HDR (half float input).
    /// Written from the Khronos Data Format Specification 1.3 (section 23), the inverse of <see cref="AstcBlocks"/>, and
    /// the general approach of Nystad et al., "Adaptive Scalable Texture Compression" (HPG 2012): fit an ideal line per
    /// partition, estimate the error of every block mode (weight grid, weight range, colour range) from the ideal weights,
    /// and encode only the most promising candidates exactly.
    /// <para>
    /// Per block: constant blocks become void-extent blocks (exact). Otherwise one partition with one or two weight planes,
    /// then 2 to 4 partitions whose seeds are ranked by how well the partition pattern matches a k-means clustering of the
    /// texels. The (block mode, endpoint mode) pairs of a trial are ranked by an estimate: line residual + decimation and
    /// quantization error of the ideal weights on the grid + endpoint quantization error, measured through the quadratic
    /// texel error for fixed weights; grids are skipped when one dimensional decimation bounds already exceed the best
    /// estimate. For a candidate the endpoints are quantized in the chosen endpoint mode (luminance, luminance-alpha,
    /// RGB scale, RGB / RGBA direct with or without blue contraction, base + offset forms; HDR: luminance, RGB base +
    /// scale, RGB direct, RGB + LDR alpha) picking among neighbouring codes by that quadratic error, the ideal weights
    /// against the decoded endpoints are decimated to the weight grid by least squares and quantized, then the endpoints
    /// are refitted against the decoded weights. The error is the exact squared difference of the decoded texels (8 bit
    /// for LDR, the 16 bit logarithmic interpolation value for HDR, which is what the decoder converts to half floats).
    /// </para>
    /// <para>
    /// Quality: Fast tries one candidate per trial and one 2 partition pattern (looked up for 4x4), and a second plane or
    /// partitions only when the line residual is a large part of the error; Normal two candidates, more patterns and
    /// base + offset endpoints; High three candidates, up to 4 partitions (re-ranked by their line residual), two
    /// second-plane channels and greedy weight steps on the best block. Measured on the Kodak set (decoded by
    /// <see cref="AstcBlocks"/>) Fast is about as good as ARM astcenc -fast, Normal within 0.45 dB of -medium, High within
    /// 0.3 dB of -thorough.
    /// </para>
    /// Every block produced is legal: it decodes without the error colour (HDR blocks only in the HDR profile). The block
    /// functions are thread safe (per thread scratch state).
    /// </summary>
    [System.Runtime.CompilerServices.SkipLocalsInit]
    internal static class AstcEncoder
    {
        const int MaxN = 144;
        const float HdrOvershoot = 2048 / 16f;   // HDR: endpoints at most one stop above the partition's brightest value   // HDR: values more than 12 stops below the block's brightest count as that

        // ------------------------------------------------------------------ public entry points

        /// <summary>Encodes one block of bw * bh RGBA8 texels (row major); <paramref name="srgb"/>: the texels are sRGB encoded
        /// and the block is meant for an sRGB format (decoded with <c>AstcBlocks.DecodeUnorm8(.., srgb: true, ..)</c>).</summary>
        public static void EncodeUnorm8(ReadOnlySpan<byte> rgba, int bw, int bh, bool srgb, BcQuality quality, Span<byte> block)
        {
            var c = Ctx.Get(bw, bh, quality);
            c.Hdr = false; c.Srgb = srgb;
            int n = c.N;
            bool opaque = true, grey = true, constant = true;
            for (int t = 0; t < n; t++)
            {
                byte r = rgba[t * 4], g = rgba[t * 4 + 1], b = rgba[t * 4 + 2], a = rgba[t * 4 + 3];
                c.X[t * 4] = r; c.X[t * 4 + 1] = g; c.X[t * 4 + 2] = b; c.X[t * 4 + 3] = a;
                c.Tgt[t * 4] = r; c.Tgt[t * 4 + 1] = g; c.Tgt[t * 4 + 2] = b; c.Tgt[t * 4 + 3] = a;
                if (a != 255) opaque = false;
                if (r != g || g != b) grey = false;
                if (r != rgba[0] || g != rgba[1] || b != rgba[2] || a != rgba[3]) constant = false;
            }
            if (constant)
            {
                VoidExtent(block, false, rgba[0] * 257, rgba[1] * 257, rgba[2] * 257, rgba[3] * 257);
                LastErr = 0;
                return;
            }
            c.Opaque = opaque; c.Grey = grey; c.Ch = opaque ? 3 : 4;
            Encode(c, block);
        }

        /// <summary>Encodes one block of bw * bh RGBA half floats (raw binary16 bits) with the HDR endpoint modes (RGB HDR, alpha
        /// LDR when it is not 1 everywhere). Negative values and NaN encode as 0, infinities as the largest half.</summary>
        public static void EncodeFloat16(ReadOnlySpan<ushort> rgba, int bw, int bh, BcQuality quality, Span<byte> block)
        {
            var c = Ctx.Get(bw, bh, quality);
            c.Hdr = true; c.Srgb = false;
            int n = c.N;
            bool opaque = true, grey = true, constant = true;
            Span<ushort> h = stackalloc ushort[4];
            Span<ushort> h0 = stackalloc ushort[4];
            for (int t = 0; t < n; t++)
            {
                for (int k = 0; k < 4; k++) h[k] = CleanHalf(rgba[t * 4 + k]);
                if (t == 0) h.CopyTo(h0);
                for (int k = 0; k < 3; k++)
                {
                    float l = HalfToLns(h[k]);
                    c.X[t * 4 + k] = l / 16;
                    c.Tgt[t * 4 + k] = (int)MathF.Round(l);
                }
                int a16 = (int)MathF.Round(Math.Clamp((float)BitConverter.UInt16BitsToHalf(h[3]), 0, 1) * 65535);
                c.X[t * 4 + 3] = a16 / 16f;
                c.Tgt[t * 4 + 3] = a16;
                if (h[3] != 0x3C00) opaque = false;
                if (h[0] != h[1] || h[1] != h[2]) grey = false;
                if (h[0] != h0[0] || h[1] != h0[1] || h[2] != h0[2] || h[3] != h0[3]) constant = false;
            }
            if (constant)
            {
                VoidExtent(block, true, h0[0], h0[1], h0[2], h0[3]);
                LastErr = 0;
                return;
            }
            // the error is logarithmic: values below the smallest normal half are all treated alike, so that zeros and
            // subnormals do not dominate the fit
            float floorL = 2048 / 16f;
            for (int t = 0; t < n; t++)
                for (int k = 0; k < 3; k++)
                    if (c.X[t * 4 + k] < floorL)
                    {
                        c.X[t * 4 + k] = floorL;
                        c.Tgt[t * 4 + k] = (int)MathF.Round(floorL * 16);
                    }
            c.Opaque = opaque; c.Grey = grey; c.Ch = opaque ? 3 : 4;
            Encode(c, block);
        }

        /// <summary>As <see cref="EncodeFloat16"/> from 32 bit floats (converted to half first).</summary>
        public static void EncodeFloat32(ReadOnlySpan<float> rgba, int bw, int bh, BcQuality quality, Span<byte> block)
        {
            Span<ushort> h = stackalloc ushort[bw * bh * 4];
            for (int i = 0; i < h.Length; i++) h[i] = BitConverter.HalfToUInt16Bits((Half)rgba[i]);
            EncodeFloat16(h, bw, bh, quality, block);
        }

        /// <summary>
        /// Encodes a whole RGBA8 image (width * height * 4 bytes) into ceil(width / bw) * ceil(height / bh) blocks of 16
        /// bytes, row major; edge blocks repeat the last row / column. Block rows run in parallel when asked.
        /// </summary>
        public static byte[] EncodeImageUnorm8(byte[] rgba, int width, int height, int bw, int bh, bool srgb, BcQuality quality, bool parallel = true)
        {
            int bx = (width + bw - 1) / bw, by = (height + bh - 1) / bh;
            var o = new byte[bx * by * 16];
            void Row(int y)
            {
                Span<byte> px = stackalloc byte[bw * bh * 4];
                for (int x = 0; x < bx; x++)
                {
                    for (int j = 0; j < bh; j++)
                    {
                        int sy = Math.Min(y * bh + j, height - 1), x0 = x * bw;
                        if (x0 + bw <= width) rgba.AsSpan((sy * width + x0) * 4, bw * 4).CopyTo(px.Slice(j * bw * 4));
                        else
                            for (int i = 0; i < bw; i++)
                                rgba.AsSpan((sy * width + Math.Min(x0 + i, width - 1)) * 4, 4).CopyTo(px.Slice((j * bw + i) * 4));
                    }
                    EncodeUnorm8(px, bw, bh, srgb, quality, o.AsSpan((y * bx + x) * 16, 16));
                }
            }
            if (parallel) Parallel.For(0, by, Row); else for (int y = 0; y < by; y++) Row(y);
            return o;
        }

        /// <summary>As <see cref="EncodeImageUnorm8"/> for RGBA half float input (raw bits), HDR profile.</summary>
        public static byte[] EncodeImageFloat16(ushort[] rgba, int width, int height, int bw, int bh, BcQuality quality, bool parallel = true)
        {
            int bx = (width + bw - 1) / bw, by = (height + bh - 1) / bh;
            var o = new byte[bx * by * 16];
            void Row(int y)
            {
                Span<ushort> px = stackalloc ushort[bw * bh * 4];
                for (int x = 0; x < bx; x++)
                {
                    for (int j = 0; j < bh; j++)
                        for (int i = 0; i < bw; i++)
                        {
                            int sx = Math.Min(x * bw + i, width - 1), sy = Math.Min(y * bh + j, height - 1);
                            rgba.AsSpan((sy * width + sx) * 4, 4).CopyTo(px.Slice((j * bw + i) * 4));
                        }
                    EncodeFloat16(px, bw, bh, quality, o.AsSpan((y * bx + x) * 16, 16));
                }
            }
            if (parallel) Parallel.For(0, by, Row); else for (int y = 0; y < by; y++) Row(y);
            return o;
        }

        // ------------------------------------------------------------------ tables

        static readonly byte[] QMax = AstcBlocks.QMax, QKind = AstcBlocks.QKind, QBits = AstcBlocks.QBits;
        static readonly int[][] CUnq = new int[21][];      // colour: [range][code] -> 0..255
        static readonly byte[][] CNear = new byte[21][];   // colour: [range][0..255] -> nearest code
        static readonly short[][] CExact = new short[21][];  // colour: [range][0..255] -> the code of exactly that value, or -1
        static readonly byte[][] CFloor = new byte[21][], CCeil = new byte[21][];   // colour: [range][x] -> largest value <= x / smallest >= x
        static readonly byte[][] CUp = new byte[21][], CDown = new byte[21][];     // colour: [range][code] -> next code up / down in value
        static readonly int[][] WUnq = new int[12][];      // weights: [range][code] -> 0..64
        static readonly byte[][] WNear = new byte[12][];   // weights: [range][round(w * 256)] (w in 0..1) -> nearest code
        static readonly byte[][] WUp = new byte[12][], WDown = new byte[12][];   // weights: next code up / down in value
        static readonly byte[][] TritEnc = new byte[5][], QuintEnc = new byte[3][];   // [values in group - 1][packed digits]
        static readonly byte[] SrgbAlpha = new byte[65536];
        static readonly float[] InvQ2 = new float[21];      // 1 / QMax^2
        static readonly float[][] WErr = new float[12][];   // weights: [range][i] squared error of quantizing i / 256

        static AstcEncoder()
        {
            for (int q = 0; q < 21; q++)
            {
                int m = QMax[q];
                var u = new int[m + 1];
                for (int v = 0; v <= m; v++) u[v] = AstcBlocks.UnquantColor(q, v);
                var near = new byte[256];
                for (int x = 0; x < 256; x++)
                {
                    int best = int.MaxValue;
                    for (int v = 0; v <= m; v++)
                    {
                        int d = Math.Abs(u[v] - x);
                        if (d < best) { best = d; near[x] = (byte)v; }
                    }
                }
                CUnq[q] = u; CNear[q] = near;
                var ex = new short[256];
                Array.Fill(ex, (short)-1);
                for (int v = 0; v <= m; v++) ex[u[v]] = (short)v;
                CExact[q] = ex;
                var fl = new byte[256];
                var ce = new byte[256];
                for (int x = 0; x < 256; x++)
                {
                    int f = 0, cc = 255;
                    for (int v = 0; v <= m; v++)
                    {
                        if (u[v] <= x && u[v] >= f) f = u[v];
                        if (u[v] >= x && u[v] <= cc) cc = u[v];
                    }
                    fl[x] = (byte)f; ce[x] = (byte)cc;
                }
                CFloor[q] = fl; CCeil[q] = ce;
                var cu = new byte[m + 1];
                var cd = new byte[m + 1];
                for (int v = 0; v <= m; v++)
                {
                    int bu = v, bd = v;
                    for (int k = 0; k <= m; k++)
                    {
                        if (u[k] > u[v] && (bu == v || u[k] < u[bu])) bu = k;
                        if (u[k] < u[v] && (bd == v || u[k] > u[bd])) bd = k;
                    }
                    cu[v] = (byte)bu; cd[v] = (byte)bd;
                }
                CUp[q] = cu; CDown[q] = cd;
            }
            for (int q = 0; q < 12; q++)
            {
                int m = QMax[q];
                var u = new int[m + 1];
                for (int v = 0; v <= m; v++) u[v] = AstcBlocks.UnquantWeight(q, v);
                var near = new byte[257];
                for (int x = 0; x <= 256; x++)
                {
                    int best = int.MaxValue;
                    for (int v = 0; v <= m; v++)
                    {
                        int d = Math.Abs(u[v] * 4 - x);
                        if (d < best) { best = d; near[x] = (byte)v; }
                    }
                }
                var up = new byte[m + 1];
                var down = new byte[m + 1];
                for (int v = 0; v <= m; v++)
                {
                    int bu = v, bd = v;
                    for (int k = 0; k <= m; k++)
                    {
                        if (u[k] > u[v] && (bu == v || u[k] < u[bu])) bu = k;
                        if (u[k] < u[v] && (bd == v || u[k] > u[bd])) bd = k;
                    }
                    up[v] = (byte)bu; down[v] = (byte)bd;
                }
                WUnq[q] = u; WNear[q] = near; WUp[q] = up; WDown[q] = down;
            }
            // integer sequence encoding: for a group of r values (the rest of the group zero), the packed trit / quint
            // block whose bits past the r values' share are zero, so that a truncated final group decodes the same
            int[] tk = { 2, 4, 5, 7, 8 }, qk = { 3, 5, 7 };
            Span<int> d5 = stackalloc int[5];
            for (int r = 1; r <= 5; r++)
            {
                int size = Pow(3, r);
                var tab = new byte[size];
                var set = new bool[size];
                for (int T = 0; T < 1 << tk[r - 1]; T++)
                {
                    AstcBlocks.Trits(T, d5);
                    int idx = 0;
                    for (int j = r - 1; j >= 0; j--) idx = idx * 3 + d5[j];
                    if (!set[idx]) { set[idx] = true; tab[idx] = (byte)T; }
                }
                if (Array.IndexOf(set, false) >= 0) throw new InvalidOperationException("trit table");
                TritEnc[r - 1] = tab;
            }
            for (int r = 1; r <= 3; r++)
            {
                int size = Pow(5, r);
                var tab = new byte[size];
                var set = new bool[size];
                for (int Q = 0; Q < 1 << qk[r - 1]; Q++)
                {
                    AstcBlocks.Quints(Q, d5);
                    int idx = 0;
                    for (int j = r - 1; j >= 0; j--) idx = idx * 5 + d5[j];
                    if (!set[idx]) { set[idx] = true; tab[idx] = (byte)Q; }
                }
                if (Array.IndexOf(set, false) >= 0) throw new InvalidOperationException("quint table");
                QuintEnc[r - 1] = tab;
            }
            for (int q = 0; q < 21; q++) InvQ2[q] = 1f / (QMax[q] * QMax[q]);
            for (int q = 0; q < 12; q++)
            {
                WErr[q] = new float[257];
                for (int i = 0; i <= 256; i++) { float d = WUnq[q][WNear[q][i]] / 64f - i / 256f; WErr[q][i] = d * d; }
            }
            for (int u = 0; u < 65536; u++) SrgbAlpha[u] = AstcBlocks.HalfToUnorm8(AstcBlocks.Unorm16ToHalf(u));
        }

        static int Pow(int b, int e) { int r = 1; while (e-- > 0) r *= b; return r; }

        // ------------------------------------------------------------------ footprints: weight grids, block modes, partitions

        sealed class Grid
        {
            public int W, H, Count;
            public bool Identity;
            public int[] Idx = null!, Wt = null!;            // per texel: 4 grid indices and their weights (sum 16)
            public int[] TStart = null!, TTex = null!, TWt = null!;   // per grid point: the texels it contributes to
            public float[] WtF = null!, TWtF = null!;                // Wt / 16, TWt / 16
            public float NoiseGain;   // mean over texels of the sum of squared infill weights: how much grid noise survives
            public int Stride;   // n rounded up to the vector width
            public float[]? Pinv;      // ((F^T F)^-1 F^T)^T: per texel a row of GStride: the unweighted least squares grid weights
            public int GStride;        // Count rounded up to the vector width
            public float[] MinWTerm = new float[2];   // the smallest weight quantization term of the grid's single / dual plane modes
            public float[][] WTerms = new float[2][];  // per single / dual plane mode of the grid: its weight quantization term
            public sbyte[][] Cqs = new sbyte[2][];     // and its colour ranges [mode * 16 + slot]
        }

        struct BlockMode
        {
            public int Bits, Grid, Wq, WeightCount, WeightBits;
            public bool Dual;
            public sbyte[] Cq;   // colour range per [(partitions - 1) * 4 + values / 2 - 1], -1 when it does not fit
            public float WTerm;  // expected squared weight quantization error (0..1 scale)
        }

        sealed class PartSet
        {
            public int Count, Words;
            public int[] Seeds = null!;
            public byte[] Map = null!;     // [i * N + t]
            public ulong[] Masks = null!;  // [(i * P + p) * Words + w]
        }

        sealed class Footprint
        {
            public int W, H, N;
            public Grid[] Grids = null!;
            public BlockMode[] Modes = null!;
            public int[][] GridModes = null!, GridModesDual = null!;   // per grid: its single / dual plane modes
            public int[] GridOrder = null!;   // grid indices by weight count, largest first
            public uint[]? P2Best;            // 16 texels: see P2Best (filled on demand)
            public byte[][] JX = new byte[13][], FX = new byte[13][], JY = new byte[13][], FY = new byte[13][];   // 1D infill per grid size
            public float[][] PX = new float[13][], PY = new float[13][];   // per grid size: projection onto the 1D infill space (len x len)
            public float[][] QX = new float[13][], QY = new float[13][];   // per grid size: orthonormal basis of the 1D infill space (len x size)
            public readonly int[] BestCq = new int[16];   // the finest colour range of any single plane mode per [(P - 1) * 4 + values / 2 - 1]
            public readonly PartSet[] Parts = new PartSet[5];
        }

        static readonly ConcurrentDictionary<int, Footprint> Footprints = new();

        static Footprint GetFootprint(int bw, int bh)
            => Footprints.GetOrAdd(bw << 8 | bh, _ => BuildFootprint(bw, bh));

        static Footprint BuildFootprint(int bw, int bh)
        {
            if (bw < 4 || bh < 4 || bw > 12 || bh > 12) throw new ArgumentException($"unsupported ASTC footprint {bw}x{bh}");
            var f = new Footprint { W = bw, H = bh, N = bw * bh };
            int n = f.N;
            var grids = new List<Grid>();
            var gridIndex = new Dictionary<int, int>();
            var modes = new List<BlockMode>();
            var seen = new HashSet<int>();
            for (int m = 0; m < 2048; m++)
            {
                if (!AstcBlocks.BlockMode(m, false, out int gw, out int gh, out _, out int wq, out bool dual)) continue;
                if (gw > bw || gh > bh) continue;
                int count = gw * gh * (dual ? 2 : 1);
                if (count > 64) continue;
                int bits = AstcBlocks.IseBits(wq, count);
                if (bits < 24 || bits > 96) continue;
                int key = gw << 16 | gh << 8 | wq << 1 | (dual ? 1 : 0);
                if (!seen.Add(key)) continue;
                if (!gridIndex.TryGetValue(gw << 8 | gh, out int gi))
                {
                    gi = grids.Count;
                    gridIndex[gw << 8 | gh] = gi;
                    grids.Add(BuildGrid(bw, bh, gw, gh));
                }
                var cq = new sbyte[16];
                for (int p = 1; p <= 4; p++)
                    for (int v = 1; v <= 4; v++) cq[(p - 1) * 4 + v - 1] = (sbyte)ColorRange(p, dual, bits, v * 2);
                modes.Add(new BlockMode { Bits = m, Grid = gi, Wq = wq, Dual = dual, WeightCount = count, WeightBits = bits, Cq = cq, WTerm = 1f / (QMax[wq] * QMax[wq] * 12) });
            }
            f.Grids = grids.ToArray();
            f.Modes = modes.ToArray();
            Array.Fill(f.BestCq, -1);
            foreach (var md in modes)
                if (!md.Dual) for (int i = 0; i < 16; i++) f.BestCq[i] = Math.Max(f.BestCq[i], md.Cq[i]);
            for (int gs = 2; gs <= 12; gs++)
            {
                f.JX[gs] = new byte[bw]; f.FX[gs] = new byte[bw]; f.JY[gs] = new byte[bh]; f.FY[gs] = new byte[bh];
                int dsx = (1024 + bw / 2) / (bw - 1), dsy = (1024 + bh / 2) / (bh - 1);
                for (int x = 0; x < bw; x++) { int v = (dsx * x * (gs - 1) + 32) >> 6; f.JX[gs][x] = (byte)(v >> 4); f.FX[gs][x] = (byte)(v & 15); }
                for (int y = 0; y < bh; y++) { int v = (dsy * y * (gs - 1) + 32) >> 6; f.JY[gs][y] = (byte)(v >> 4); f.FY[gs][y] = (byte)(v & 15); }
            }
            for (int gs = 2; gs <= 12; gs++)
            {
                if (gs < bw) { f.PX[gs] = Projection(bw, gs, f.JX[gs], f.FX[gs]); f.QX[gs] = Basis1D(bw, gs, f.JX[gs], f.FX[gs]); }
                if (gs < bh) { f.PY[gs] = Projection(bh, gs, f.JY[gs], f.FY[gs]); f.QY[gs] = Basis1D(bh, gs, f.JY[gs], f.FY[gs]); }
            }
            f.GridOrder = new int[grids.Count];
            for (int gi = 0; gi < grids.Count; gi++) f.GridOrder[gi] = gi;
            Array.Sort(f.GridOrder, (x, y) => grids[y].Count != grids[x].Count ? grids[y].Count - grids[x].Count : x - y);
            f.GridModes = new int[grids.Count][];
            f.GridModesDual = new int[grids.Count][];
            for (int gi = 0; gi < grids.Count; gi++)
            {
                var s1 = new List<int>();
                var s2 = new List<int>();
                for (int mi = 0; mi < modes.Count; mi++)
                    if (modes[mi].Grid == gi) (modes[mi].Dual ? s2 : s1).Add(mi);
                f.GridModes[gi] = s1.ToArray();
                f.GridModesDual[gi] = s2.ToArray();
                for (int d = 0; d < 2; d++)
                {
                    var list = d == 0 ? s1 : s2;
                    var g = grids[gi];
                    g.WTerms[d] = new float[list.Count];
                    g.Cqs[d] = new sbyte[list.Count * 16];
                    g.MinWTerm[d] = float.MaxValue;
                    for (int i = 0; i < list.Count; i++)
                    {
                        g.WTerms[d][i] = modes[list[i]].WTerm;
                        g.MinWTerm[d] = Math.Min(g.MinWTerm[d], modes[list[i]].WTerm);
                        Array.Copy(modes[list[i]].Cq, 0, g.Cqs[d], i * 16, 16);
                    }
                }
            }

            bool small = n < 31;
            for (int p = 2; p <= 4; p++)
            {
                var keys = new HashSet<string>();
                var seeds = new List<int>();
                var maps = new List<byte>();
                var map = new byte[n];
                var canon = new char[n];
                var relabel = new int[4];
                for (int seed = 0; seed < 1024; seed++)
                {
                    for (int y = 0, t = 0; y < bh; y++)
                        for (int x = 0; x < bw; x++, t++) map[t] = (byte)AstcBlocks.SelectPartition(seed, x, y, 0, p, small);
                    Array.Fill(relabel, -1);
                    int next = 0;
                    for (int t = 0; t < n; t++)
                    {
                        if (relabel[map[t]] < 0) relabel[map[t]] = next++;
                        canon[t] = (char)('0' + relabel[map[t]]);
                    }
                    if (next < p || !keys.Add(new string(canon))) continue;
                    seeds.Add(seed);
                    maps.AddRange(map);
                }
                int words = (n + 63) / 64;
                var ps = new PartSet { Count = seeds.Count, Words = words, Seeds = seeds.ToArray(), Map = maps.ToArray() };
                ps.Masks = new ulong[ps.Count * p * words];
                for (int i = 0; i < ps.Count; i++)
                    for (int t = 0; t < n; t++)
                        ps.Masks[(i * p + ps.Map[i * n + t]) * words + (t >> 6)] |= 1UL << (t & 63);
                f.Parts[p] = ps;
            }
            return f;
        }

        /// <summary>Orthonormal basis (len x gs, row major) of the columns of a grid dimension's interpolation matrix.</summary>
        static float[] Basis1D(int len, int gs, byte[] J, byte[] Fr)
        {
            var cols = new double[gs][];
            for (int k = 0; k < gs; k++) cols[k] = new double[len];
            for (int i = 0; i < len; i++)
            {
                double b = Fr[i] / 16.0;
                cols[J[i]][i] += 1 - b;
                if (b > 0) cols[J[i] + 1][i] += b;
            }
            var Q = new float[len * gs];
            for (int k = 0; k < gs; k++)
            {
                var v = cols[k];
                for (int j = 0; j < k; j++)
                {
                    double d = 0;
                    for (int i = 0; i < len; i++) d += Q[i * gs + j] * v[i];
                    for (int i = 0; i < len; i++) v[i] -= d * Q[i * gs + j];
                }
                double l = 0;
                for (int i = 0; i < len; i++) l += v[i] * v[i];
                l = l > 1e-12 ? 1 / Math.Sqrt(l) : 0;
                for (int i = 0; i < len; i++) Q[i * gs + k] = (float)(v[i] * l);
            }
            return Q;
        }

        /// <summary>F (F^T F)^-1 F^T for the len x gs linear interpolation matrix F of a grid dimension.</summary>
        static float[] Projection(int len, int gs, byte[] J, byte[] Fr)
        {
            var F = new double[len, gs];
            for (int i = 0; i < len; i++)
            {
                double b = Fr[i] / 16.0;
                F[i, J[i]] += 1 - b;
                if (b > 0) F[i, J[i] + 1] += b;
            }
            var A = new double[gs, 2 * gs];
            for (int a = 0; a < gs; a++)
            {
                for (int b = 0; b < gs; b++)
                    for (int i = 0; i < len; i++) A[a, b] += F[i, a] * F[i, b];
                A[a, gs + a] = 1;
            }
            for (int col = 0; col < gs; col++)
            {
                int piv = col;
                for (int r = col + 1; r < gs; r++) if (Math.Abs(A[r, col]) > Math.Abs(A[piv, col])) piv = r;
                for (int k = 0; k < 2 * gs; k++) (A[col, k], A[piv, k]) = (A[piv, k], A[col, k]);
                double d = A[col, col];
                if (Math.Abs(d) < 1e-12) continue;
                for (int k = 0; k < 2 * gs; k++) A[col, k] /= d;
                for (int r = 0; r < gs; r++)
                    if (r != col && A[r, col] != 0)
                    {
                        double m = A[r, col];
                        for (int k = 0; k < 2 * gs; k++) A[r, k] -= m * A[col, k];
                    }
            }
            var P = new float[len * len];
            for (int i = 0; i < len; i++)
                for (int j = 0; j < len; j++)
                {
                    double v = 0;
                    for (int a = 0; a < gs; a++)
                        for (int b = 0; b < gs; b++) v += F[i, a] * A[a, gs + b] * F[j, b];
                    P[i * len + j] = (float)v;
                }
            return P;
        }

        static float[]? PseudoInverse(int n, int gc, Grid g, int stride)
        {
            var F = new double[n, gc];
            for (int t = 0; t < n; t++) for (int j = 0; j < 4; j++) F[t, g.Idx[t * 4 + j]] += g.Wt[t * 4 + j] / 16.0;
            var A = new double[gc, 2 * gc];
            for (int a = 0; a < gc; a++)
            {
                for (int b = 0; b < gc; b++) for (int t = 0; t < n; t++) A[a, b] += F[t, a] * F[t, b];
                A[a, gc + a] = 1;
            }
            for (int col = 0; col < gc; col++)
            {
                int piv = col;
                for (int r = col + 1; r < gc; r++) if (Math.Abs(A[r, col]) > Math.Abs(A[piv, col])) piv = r;
                for (int k = 0; k < 2 * gc; k++) (A[col, k], A[piv, k]) = (A[piv, k], A[col, k]);
                double d = A[col, col];
                if (Math.Abs(d) < 1e-12) return null;
                for (int k = 0; k < 2 * gc; k++) A[col, k] /= d;
                for (int r = 0; r < gc; r++)
                    if (r != col && A[r, col] != 0)
                    {
                        double m = A[r, col];
                        for (int k = 0; k < 2 * gc; k++) A[r, k] -= m * A[col, k];
                    }
            }
            int gs8 = (gc + System.Numerics.Vector<float>.Count - 1) / System.Numerics.Vector<float>.Count * System.Numerics.Vector<float>.Count;
            var P = new float[n * gs8];
            for (int a = 0; a < gc; a++)
                for (int t = 0; t < n; t++)
                {
                    double v = 0;
                    for (int b = 0; b < gc; b++) v += A[a, gc + b] * F[t, b];
                    P[t * gs8 + a] = (float)v;
                }
            return P;
        }

        static Grid BuildGrid(int bw, int bh, int gw, int gh)
        {
            int n = bw * bh, gc = gw * gh;
            var g = new Grid { W = gw, H = gh, Count = gc, Identity = gw == bw && gh == bh, Idx = new int[n * 4], Wt = new int[n * 4] };
            int ds = (1024 + bw / 2) / (bw - 1), dt = (1024 + bh / 2) / (bh - 1);
            var lists = new List<(int t, int w)>[gc];
            for (int k = 0; k < gc; k++) lists[k] = new();
            for (int y = 0, t = 0; y < bh; y++)
                for (int x = 0; x < bw; x++, t++)
                {
                    int gs = (ds * x * (gw - 1) + 32) >> 6, gt = (dt * y * (gh - 1) + 32) >> 6;
                    int js = gs >> 4, fs = gs & 15, jt = gt >> 4, ft = gt & 15;
                    int v0 = js + jt * gw;
                    int w11 = (fs * ft + 8) >> 4, w10 = ft - w11, w01 = fs - w11, w00 = 16 - fs - ft + w11;
                    int[] idx = { v0, v0 + 1, v0 + gw, v0 + gw + 1 };
                    int[] wt = { w00, w01, w10, w11 };
                    for (int j = 0; j < 4; j++)
                    {
                        if (wt[j] == 0) idx[j] = v0;   // never read past the grid
                        g.Idx[t * 4 + j] = idx[j];
                        g.Wt[t * 4 + j] = wt[j];
                        if (wt[j] != 0) lists[idx[j]].Add((t, wt[j]));
                    }
                }
            g.TStart = new int[gc + 1];
            int total = 0;
            for (int k = 0; k < gc; k++) { g.TStart[k] = total; total += lists[k].Count; }
            g.TStart[gc] = total;
            g.TTex = new int[total]; g.TWt = new int[total]; g.TWtF = new float[total];
            for (int k = 0, i = 0; k < gc; k++)
                foreach (var (t, w) in lists[k]) { g.TTex[i] = t; g.TWt[i] = w; g.TWtF[i] = w / 16f; i++; }
            g.WtF = new float[n * 4];
            for (int i = 0; i < n * 4; i++) { g.WtF[i] = g.Wt[i] / 16f; g.NoiseGain += g.WtF[i] * g.WtF[i] / n; }
            int vl = System.Numerics.Vector<float>.Count;
            g.Stride = (n + vl - 1) / vl * vl;
            g.GStride = (gc + vl - 1) / vl * vl;
            if (!g.Identity) g.Pinv = PseudoInverse(n, gc, g, g.Stride);

            return g;
        }

        // ------------------------------------------------------------------ quality settings

        internal sealed class Prm
        {
            public int Candidates, Iters, DecIters, RankSweeps, Dual, KMeansIters;
            public int[] PartTries = new int[5];
            public bool Offset, Refine;
            public float GoodEnough;   // mean squared error per channel at which the search stops
            public float EstFactor;    // a candidate is skipped when its estimated error times this exceeds the best error
            public int PartPre = 1;    // partition patterns matched per try, re-ranked by their line residual
            public int Spare = 2;      // extra candidates ranked before the decimation proper corrects the estimates
            public float Gate;         // LDR: dual plane and partitions are only tried when the 1 partition line residual is this part of the error
            public float GateMid, GateLarge;   // the same for LDR footprints of 36 to 63 texels, and 64 and more
        }

        internal static readonly Prm[] Params =
        {
            new Prm { Candidates = 1, Iters = 1, DecIters = 1, RankSweeps = 1, Dual = 1, KMeansIters = 0, PartTries = new[] { 0, 0, 1, 0, 0 }, Offset = false, Refine = false, GoodEnough = 2.5f, EstFactor = 0.8f, Gate = 0.5f, GateMid = 0.6f, GateLarge = 0.8f },
            new Prm { Candidates = 2, Iters = 1, DecIters = 2, RankSweeps = 1, Dual = 1, KMeansIters = 3, PartTries = new[] { 0, 0, 2, 1, 0 }, Offset = true, Refine = false, GoodEnough = 1.0f, EstFactor = 0.65f, GateMid = 0.3f, GateLarge = 0.2f },
            new Prm { Candidates = 3, Iters = 2, DecIters = 3, RankSweeps = 1, Dual = 2, KMeansIters = 4, PartTries = new[] { 0, 0, 6, 3, 1 }, PartPre = 4, Offset = true, Refine = true, GoodEnough = 0.3f, EstFactor = 0.5f },
        };

        // ------------------------------------------------------------------ per thread state

        sealed class Ctx
        {
            [ThreadStatic] static Ctx? current;

            public static Ctx Get(int bw, int bh, BcQuality q)
            {
                var c = current ??= new Ctx();
                if (c.F == null || c.F.W != bw || c.F.H != bh) { c.F = GetFootprint(bw, bh); c.N = bw * bh; }
                c.Q = q;
                c.Prm = Params[Math.Clamp((int)q, 0, 2)];
                return c;
            }

            public Footprint F = null!;
            public Prm Prm = null!;
            public BcQuality Q;
            public bool Hdr, Srgb, Opaque, Grey;
            public int N, Ch;
            public readonly float[] X = new float[MaxN * 4];   // texels in the fitting domain (LDR: 0..255, HDR: 16 bit / 16)
            public readonly int[] Tgt = new int[MaxN * 4];     // texels in the error domain (LDR: 8 bit, HDR: 16 bit)
            public readonly Vector4[] XV = new Vector4[MaxN];  // X as vectors
            public Vector4 Mask0;                              // 1 for the channels of weight plane 0
            public readonly Vector4[] Cov = new Vector4[16];   // per partition: covariance rows of the last single plane fit
            public Vector4 Axis1, Mean1;                       // principal axis and mean of the 1 partition, 1 plane fit

            // trial: partitioning and planes
            public int P, Seed;
            public readonly byte[] Part = new byte[MaxN];
            public readonly int[] Mem = new int[4 * MaxN];
            public readonly int[] MemN = new int[4];
            public bool Dual;
            public int Ccs = -1, NCh0;
            public readonly int[] Ch0 = new int[4];
            public readonly float[] E0 = new float[16], E1 = new float[16];   // ideal endpoints [p * 4 + c]
            public float Residual;
            public readonly float[] IW = new float[2 * MaxN];   // ideal weights (0..1) [plane * MaxN + t]
            public readonly float[] Imp = new float[2 * MaxN];  // their importance (squared endpoint distance)
            public readonly float[] GW = new float[128];        // decimated weights [plane * 64 + k]
            public readonly float[] Inf = new float[MaxN];      // scratch: infilled float weights
            public readonly float[] DX = new float[2 * 13], DY = new float[2 * 13];   // per plane: 1D decimation errors per grid width / height
            public long LineDone;   // which of DX / DY are computed in this trial: bit plane * 26 + dir * 13 + size; Gram matrices bits 52..55
            public readonly float[] GramX = new float[2 * 144], GramY = new float[2 * 144];
            public readonly float[] ColErr = new float[4 * 21], ColErrExact = new float[4 * 21];
            public readonly int[] ERnd = new int[32];
            public readonly float[] PMax = new float[16];   // HDR: per partition and channel the largest value (fitting domain)   // per trial: the ideal endpoints rounded to 8 bits [p * 8 + e * 4 + c]
            public readonly float[] VP = new float[2 * ProjStride], VV = new float[2];   // projection inputs per plane
            public readonly short[] QI = new short[2 * MaxN];   // ideal weights in 1/256 steps
            public readonly float[] VQ = new float[2 * 13 * 144];   // V QX per plane and grid width
            public long VQDone;  // per trial: estimated endpoint quantization error [cem index * 21 + range]

            // candidate
            public readonly int[] Codes = new int[18];
            public readonly int[] C16 = new int[32];   // decoded endpoints as 16 bit interpolation inputs [p * 8 + e * 4 + c]
            public readonly float[] D = new float[32]; // the same in the fitting domain
            public readonly int[] WC = new int[128];   // weight codes [plane * 64 + k]
            public readonly int[] WI = new int[128];   // their values 0..64
            public readonly int[] TW = new int[2 * MaxN];   // infilled texel weights [plane * MaxN + t]
            public readonly int[] TErr = new int[2 * MaxN]; // scratch for weight refinement

            // scratch for endpoint quantization
            public readonly int[] QCodes = new int[18];
            public readonly float[] SW = new float[24], ST0 = new float[16], ST1 = new float[16];      // quadratic error sums
            public readonly float[] SWInit = new float[24], ST0Init = new float[16], ST1Init = new float[16];
            public int CurP;   // the partition being quantized
            public readonly int[] PCodes = new int[8], PE = new int[8], TCodes = new int[8], TE = new int[8];

            // result
            public long BestErr;
            public int BestMode = -1, BestCem, BestCq, BestP, BestSeed, BestCcs;   // how the best block was made
            public bool BestDual, RefineNow;
            public readonly byte[] BestPart = new byte[MaxN];
            public readonly byte[] Best = new byte[16];

            // partition ranking
            public readonly byte[] Label = new byte[MaxN];
            public readonly int[] TopIdx = new int[32];
            public readonly float[] PartRes = new float[32];
            public readonly int[] TopScore = new int[32];
        }

        // ------------------------------------------------------------------ block search

        static void Encode(Ctx c, Span<byte> block)
        {
            var prm = c.Prm;
            int n = c.N;
            for (int t = 0; t < n; t++) c.XV[t] = new Vector4(c.X[t * 4], c.X[t * 4 + 1], c.X[t * 4 + 2], c.X[t * 4 + 3]);
            c.BestErr = long.MaxValue;
            c.BestMode = -1;
            float scale = c.Hdr ? 16 : 1;
            long good = (long)(n * c.Ch * prm.GoodEnough * scale);

            // one partition, one plane
            c.P = 1;
            Array.Clear(c.Part, 0, n);
            Trial(c, false, -1);
            // more planes or partitions only lower the line residual, at the cost of coarser quantization: when the
            // residual is a small part of the error, they do not pay
            if (c.Residual * scale < (c.Hdr ? 0 : n < 36 ? prm.Gate : n < 64 ? prm.GateMid : prm.GateLarge) * c.BestErr) good = long.MaxValue;

            // one partition, two planes
            Span<int> order = stackalloc int[4];
            if (c.BestErr > good && !(c.Grey && c.Opaque))
            {
                int cands = RankDualChannels(c, order);
                for (int i = 0; i < Math.Min(cands, prm.Dual) && c.BestErr > good; i++) Trial(c, true, order[i]);
            }

            // 2 to 4 partitions
            for (int p = 2; p <= 4 && c.BestErr > good; p++)
            {
                int tries = prm.PartTries[p];
                if (tries == 0 || p == 4 && !c.Opaque && !c.Grey) continue;
                // lower bound of any p partition encoding: the colour quantization error at the finest colour range
                float colUnit = n * c.Ch / 18f * (c.Hdr ? 2048f * 2048f : 255f * 255f), lb = float.MaxValue;
                foreach (int cem in CemList(c))
                {
                    int cq = c.F.BestCq[(p - 1) * 4 + CemValues(cem) / 2 - 1];
                    if (cq >= 0) lb = Math.Min(lb, colUnit / (QMax[cq] * QMax[cq]));
                }
                if (lb * scale * prm.EstFactor >= c.BestErr) break;
                int pre = n >= 36 ? prm.PartPre : 1;
                int found = RankPartitions(c, p, tries * pre);
                if (pre > 1 && found > tries)
                {
                    // re-rank the k-means matches by the line residual of their partitions
                    var ps0 = c.F.Parts[p];
                    var rr = c.PartRes;
                    for (int i = 0; i < found; i++)
                    {
                        c.P = p;
                        Array.Copy(ps0.Map, c.TopIdx[i] * n, c.Part, 0, n);
                        BuildMembers(c);
                        SetPlanes(c, false, -1);
                        float r = 0;
                        for (int q = 0; q < p; q++) r += FitPartition(c, q, false);
                        rr[i] = r;
                    }
                    for (int i = 1; i < found; i++)
                        for (int j = i; j > 0 && rr[j] < rr[j - 1]; j--)
                        {
                            (rr[j], rr[j - 1]) = (rr[j - 1], rr[j]);
                            (c.TopIdx[j], c.TopIdx[j - 1]) = (c.TopIdx[j - 1], c.TopIdx[j]);
                        }
                    found = tries;
                }
                for (int i = 0; i < found && c.BestErr > good; i++)
                {
                    var ps = c.F.Parts[p];
                    int idx = c.TopIdx[i];
                    c.P = p;
                    Array.Copy(ps.Map, idx * n, c.Part, 0, n);
                    c.Seed = ps.Seeds[idx];
                    Trial(c, false, -1);
                    if (c.Q == BcQuality.High && p <= 3 && i < 2 && c.BestErr > good && !(c.Grey && c.Opaque))
                    {
                        int cands = RankDualChannels(c, order);
                        if (cands > 0) Trial(c, true, order[0]);
                    }
                }
            }
            if (prm.Refine && c.BestMode >= 0 && c.BestErr > 0)
            {
                // greedy weight steps, on the best encoding only: rebuild its state and encode it again refining
                c.P = c.BestP; c.Seed = c.BestSeed;
                Array.Copy(c.BestPart, c.Part, n);
                bool dual = c.BestDual;
                SetupTrial(c, dual, c.BestCcs);
                c.RefineNow = true;
                FullEncode(c, c.BestMode, c.BestCem, c.BestCq);
                c.RefineNow = false;
            }
            c.Best.CopyTo(block);
            LastErr = c.BestErr;
        }

        [ThreadStatic] internal static long LastErr;

        /// <summary>Channels for the second weight plane, best first: by the line residual of the remaining channels.</summary>
        static int RankDualChannels(Ctx c, Span<int> order) => RankDualChannels(c, order, stackalloc float[4]);

        static int RankDualChannels(Ctx c, Span<int> order, Span<float> res)
        {
            int cnt = 0;
            // from the covariances of the last single plane fit: the residual of the line through the other channels
            for (int k = 0; k < c.Ch; k++)
            {
                if (c.Grey && k < 3) continue;
                float r = 0;
                for (int p = 0; p < c.P; p++) r += SubResidual(c.Cov.AsSpan(p * 4, 4), k);
                res[cnt] = r; order[cnt++] = k;
            }
            for (int i = 1; i < cnt; i++)
                for (int j = i; j > 0 && res[j] < res[j - 1]; j--)
                {
                    (res[j], res[j - 1]) = (res[j - 1], res[j]);
                    (order[j], order[j - 1]) = (order[j - 1], order[j]);
                }
            return cnt;
        }

        /// <summary>Residual of the principal line of a covariance (rows r) without channel k: trace minus the largest
        /// eigenvalue.</summary>
        static float SubResidual(ReadOnlySpan<Vector4> r, int k)
        {
            Vector4 m = Vector4.One;
            m[k] = 0;
            Vector4 r0 = r[0] * m * m.X, r1 = r[1] * m * m.Y, r2 = r[2] * m * m.Z, r3 = r[3] * m * m.W;
            float tr = r0.X + r1.Y + r2.Z + r3.W;
            Vector4 a = r0;
            float big = r0.X;
            if (r1.Y > big) { big = r1.Y; a = r1; }
            if (r2.Z > big) { big = r2.Z; a = r2; }
            if (r3.W > big) { big = r3.W; a = r3; }
            if (big <= 1e-12f) return 0;
            float lambda = 0;
            for (int it = 0; it < 8; it++)
            {
                Vector4 nx = r0 * a.X + r1 * a.Y + r2 * a.Z + r3 * a.W;
                float len = nx.Length();
                if (len < 1e-20f) break;
                lambda = Vector4.Dot(a, nx) / a.LengthSquared();
                a = nx / len;
            }
            return Math.Max(0, tr - lambda);
        }

        static void BuildMembers(Ctx c)
        {
            for (int p = 0; p < 4; p++) c.MemN[p] = 0;
            for (int t = 0; t < c.N; t++)
            {
                int p = c.Part[t];
                c.Mem[p * MaxN + c.MemN[p]++] = t;
            }
        }

        static void SetPlanes(Ctx c, bool dual, int ccs)
        {
            c.Dual = dual; c.Ccs = dual ? ccs : -1;
            int n0 = 0;
            Vector4 m = Vector4.Zero;
            for (int k = 0; k < c.Ch; k++) if (!dual || k != ccs) { c.Ch0[n0++] = k; m[k] = 1; }
            c.NCh0 = n0;
            c.Mask0 = m;
        }

        // endpoint modes to try for the block content; the second of a pair (base + offset) is tried inside quantization
        static ReadOnlySpan<int> CemList(Ctx c)
        {
            if (c.Hdr) return !c.Opaque ? CemH14 : c.Grey ? CemHGrey : CemHRgb;
            if (c.Grey) return c.Opaque ? CemL : CemLA;
            return c.Opaque ? CemRgb : CemRgba;
        }

        static readonly int[] CemH14 = { 14 }, CemHGrey = { 7, 11, 2 }, CemHRgb = { 7, 11 }, CemL = { 0 }, CemLA = { 4 }, CemRgb = { 6, 8 }, CemRgba = { 10, 12 };

        static int CemValues(int cem) => ((cem >> 2) + 1) * 2;


        static void Trial(Ctx c, bool dual, int ccs)
        {
            TrialCore(c, dual, ccs);
        }

        /// <summary>Members, planes, line fits and quadratic sums of the current partitioning; returns the line residual.</summary>
        static float SetupTrial(Ctx c, bool dual, int ccs)
        {
            BuildMembers(c);
            SetPlanes(c, dual, ccs);
            if (c.Hdr)
                for (int p = 0; p < c.P; p++)
                    for (int k = 0; k < 3; k++)
                    {
                        float m = 0;
                        for (int i = 0; i < c.MemN[p]; i++) m = Math.Max(m, c.X[c.Mem[p * MaxN + i] * 4 + k]);
                        c.PMax[p * 4 + k] = m;
                    }
            float res = 0;
            for (int p = 0; p < c.P; p++) res += FitPartition(c, p, true);
            c.Residual = res;
            ComputeSums(c, false);
            c.SW.CopyTo(c.SWInit, 0); c.ST0.CopyTo(c.ST0Init, 0); c.ST1.CopyTo(c.ST1Init, 0);
            return res;
        }

        static void TrialCore(Ctx c, bool dual, int ccs)
        {
            var prm = c.Prm;
            float res = SetupTrial(c, dual, ccs);
            float scale = c.Hdr ? 16 : 1;
            if (c.BestErr != long.MaxValue && res * scale >= c.BestErr) return;   // no encoding beats its line residual

            var cems = CemList(c);
            int n = c.N, planes = dual ? 2 : 1;
            float impSum = 0;
            for (int t = 0; t < n; t++) impSum += c.Imp[t] + (dual ? c.Imp[MaxN + t] : 0);
            c.ColErr.AsSpan(0, 21 * cems.Length).Fill(-1f);
            if (c.Hdr) c.ColErrExact.AsSpan(0, 21 * cems.Length).Fill(-1f);
            for (int p = 0; p < c.P; p++)
                for (int k = 0; k < 4; k++) { c.ERnd[p * 8 + k] = R8(c.E0[p * 4 + k]); c.ERnd[p * 8 + 4 + k] = R8(c.E1[p * 4 + k]); }

            // CEM 6 / 10 (RGB scale) cost: the dark endpoint is forced onto the line through the bright one and the origin
            float scalePenalty = 0;
            if (!c.Hdr && !c.Grey)
                for (int p = 0; p < c.P; p++)
                {
                    float s0 = c.E0[p * 4] + c.E0[p * 4 + 1] + c.E0[p * 4 + 2], s1 = c.E1[p * 4] + c.E1[p * 4 + 1] + c.E1[p * 4 + 2];
                    var dk = s0 <= s1 ? c.E0.AsSpan(p * 4, 4) : c.E1.AsSpan(p * 4, 4);
                    var br = s0 <= s1 ? c.E1.AsSpan(p * 4, 4) : c.E0.AsSpan(p * 4, 4);
                    float bb = br[0] * br[0] + br[1] * br[1] + br[2] * br[2] + 1e-6f;
                    float s = Math.Clamp((dk[0] * br[0] + dk[1] * br[1] + dk[2] * br[2]) / bb, 0, 1);
                    float d = 0;
                    for (int k = 0; k < 3; k++) { float e = dk[k] - s * br[k]; d += e * e; }
                    scalePenalty += d * c.MemN[p] / 3;
                }

            // rank (block mode, endpoint mode) pairs by the estimated error. Grids from the largest down; a grid's
            // decimation is only computed when its estimate without it can still enter the list.
            // ranked with the unclamped least squares decimation error; the decimation proper of the best few
            // candidates' grids then corrects their estimates
            int spare = c.P == 1 && !dual || c.Q == BcQuality.High ? prm.Spare : 0;   // the corrections matter most for the first trial
            int K = Math.Min(8, prm.Candidates + spare + (c.Hdr ? HdrSpare : 0)), Kfinal = prm.Candidates;
            Span<float> topEst = stackalloc float[8], topDec = stackalloc float[8];
            Span<int> topMode = stackalloc int[8], topCem = stackalloc int[8], topCq = stackalloc int[8];
            int found = 0;
            var modes = c.F.Modes;
            var gridModes = dual ? c.F.GridModesDual : c.F.GridModes;
            int pv = (c.P - 1) * 4;
            float colUnit = n * c.Ch / 18f * (c.Hdr ? 2048f * 2048f : 255f * 255f);
            int ncem = cems.Length;
            Span<int> cemSlot = stackalloc int[4];
            float lbCol = float.MaxValue;
            var colErr = c.ColErr;
            for (int ci = 0; ci < ncem; ci++)
            {
                cemSlot[ci] = pv + CemValues(cems[ci]) / 2 - 1;
                int bq = c.F.BestCq[cemSlot[ci]];
                if (bq >= 0) lbCol = Math.Min(lbCol, ColourError(c, ci, cems[ci], bq, colUnit));
            }
            if (c.BestErr != long.MaxValue && (res + lbCol) * scale * prm.EstFactor >= c.BestErr) return;
            float limit = c.BestErr == long.MaxValue ? float.MaxValue : c.BestErr / (scale * prm.EstFactor);
            PrepareProjections(c, planes);
            Span<float> cemFloor = stackalloc float[4];
            float minFloor = float.MaxValue;
            for (int ci = 0; ci < ncem; ci++)
            {
                cemFloor[ci] = res + (cems[ci] == 6 || cems[ci] == 10 ? scalePenalty : 0);
                minFloor = Math.Min(minFloor, cemFloor[ci]);
            }
            foreach (int gi in c.F.GridOrder)
            {
                var gm = gridModes[gi];
                if (gm.Length == 0) continue;
                var g = c.F.Grids[gi];
                float gain = impSum * g.NoiseGain;
                float bound = found == K ? Math.Min(limit, topEst[K - 1]) : limit;
                float dec = 0;
                if (!g.Identity)
                {
                    // the least squares decimation error: the ideal weights (scaled by the square root of their
                    // importance) minus their projection onto the grid's infill space
                    if (gain * g.MinWTerm[planes - 1] + minFloor >= bound) continue;
                    for (int pl = 0; pl < planes; pl++) dec += ProjectionError(c, g, pl);
                    if (dec + gain * g.MinWTerm[planes - 1] + minFloor >= bound) continue;
                }
                foreach (int mi in gm)
                {
                    ref readonly var m = ref modes[mi];
                    float wterm = 0;
                    if (g.Identity) for (int pl = 0; pl < planes; pl++) wterm += WeightQuantError(c, g, m.Wq, pl);
                    else wterm = dec + gain * m.WTerm;
                    for (int ci = 0; ci < ncem; ci++)
                    {
                        int cq = m.Cq[cemSlot[ci]];
                        if (cq < 0) continue;
                        float est0 = wterm + cemFloor[ci];
                        if (est0 >= limit || found == K && est0 >= topEst[K - 1]) continue;
                        float col = colErr[ci * 21 + cq];
                        if (col < 0) col = ColourError(c, ci, cems[ci], cq, colUnit);
                        float est = res + wterm + col;
                        if (found < K || est < topEst[found - 1])
                        {
                            int j = found < K ? found++ : found - 1;
                            while (j > 0 && topEst[j - 1] > est)
                            {
                                topEst[j] = topEst[j - 1]; topMode[j] = topMode[j - 1]; topCem[j] = topCem[j - 1]; topCq[j] = topCq[j - 1];
                                topDec[j] = topDec[j - 1];
                                j--;
                            }
                            topEst[j] = est; topMode[j] = mi; topCem[j] = cems[ci]; topCq[j] = cq;
                            topDec[j] = g.Identity ? -1 : dec;
                        }
                    }
                }
            }
            // the decimation proper (clamped weights, actual infill) for the candidates' grids
            Span<int> dGrid = stackalloc int[8];
            Span<float> dVal = stackalloc float[8];
            int nd = 0;
            if (c.Hdr)
                for (int i = 0; i < found; i++)
                {
                    int ci = 0;
                    while (cems[ci] != topCem[i]) ci++;
                    topEst[i] += ExactHdrColour(c, ci, topCem[i], topCq[i]) - colErr[ci * 21 + topCq[i]];
                }
            for (int i = 0; i < found; i++)
            {
                if (topDec[i] < 0) continue;
                int gi = modes[topMode[i]].Grid, k = 0;
                while (k < nd && dGrid[k] != gi) k++;
                if (k == nd)
                {
                    var g = c.F.Grids[gi];
                    float dec = 0;
                    for (int pl = 0; pl < planes; pl++)
                    {
                        Decimate(c, g, pl, prm.RankSweeps);
                        for (int t = 0, bb = pl * MaxN; t < n; t++) { float d = c.Inf[t] - c.IW[bb + t]; dec += d * d * c.Imp[bb + t]; }
                    }
                    dGrid[nd] = gi; dVal[nd++] = dec;
                }
                topEst[i] += dVal[k] - topDec[i];
            }
            for (int i = 1; i < found; i++)
                for (int j = i; j > 0 && topEst[j] < topEst[j - 1]; j--)
                {
                    (topEst[j], topEst[j - 1]) = (topEst[j - 1], topEst[j]);
                    (topMode[j], topMode[j - 1]) = (topMode[j - 1], topMode[j]);
                    (topCem[j], topCem[j - 1]) = (topCem[j - 1], topCem[j]);
                    (topCq[j], topCq[j - 1]) = (topCq[j - 1], topCq[j]);
                }
            found = Math.Min(found, Kfinal);
            for (int i = 0; i < found; i++)
            {
                if (c.BestErr != long.MaxValue && topEst[i] * scale * prm.EstFactor >= c.BestErr) break;
                FullEncode(c, topMode[i], topCem[i], topCq[i]);
            }
        }

        /// <summary>The colour range the decoder will derive, or -1 when the values do not fit.</summary>
        static int ColorRange(int parts, bool dual, int weightBits, int values)
        {
            int count = parts * values;
            if (count > 18) return -1;
            int config = (parts == 1 ? 17 : 29) + (dual ? 2 : 0);
            int remaining = 128 - config - weightBits;
            if (remaining < (13 * count + 4) / 5) return -1;
            int cq = 20;
            while (AstcBlocks.IseBits(cq, count) > remaining) cq--;
            return cq;
        }

        // ------------------------------------------------------------------ ideal lines

        /// <summary>
        /// Fits partition p: the principal axis of the plane-0 channels, the range of the second plane's channel. With
        /// <paramref name="store"/> the ideal endpoints, weights and importances are written. Returns the line residual.
        /// </summary>
        static float FitPartition(Ctx c, int p, bool store)
        {
            int cnt = c.MemN[p];
            var mem = c.Mem.AsSpan(p * MaxN, cnt);
            var XV = c.XV;
            var mask = c.Mask0;
            Vector4 sum = Vector4.Zero;
            foreach (int t in mem) sum += XV[t];
            Vector4 mean = cnt > 0 ? sum * mask / cnt : Vector4.Zero;
            Vector4 r0 = Vector4.Zero, r1 = Vector4.Zero, r2 = Vector4.Zero, r3 = Vector4.Zero;
            foreach (int t in mem)
            {
                Vector4 d = (XV[t] - mean) * mask;
                r0 += d * d.X; r1 += d * d.Y; r2 += d * d.Z; r3 += d * d.W;
            }
            float total = r0.X + r1.Y + r2.Z + r3.W;
            if (!c.Dual) { c.Cov[p * 4] = r0; c.Cov[p * 4 + 1] = r1; c.Cov[p * 4 + 2] = r2; c.Cov[p * 4 + 3] = r3; }
            bool keep = c.P == 1 && !c.Dual;
            // power iteration from the row of the largest variance
            Vector4 axis = r0;
            float big = r0.X;
            if (r1.Y > big) { big = r1.Y; axis = r1; }
            if (r2.Z > big) { big = r2.Z; axis = r2; }
            if (r3.W > big) { big = r3.W; axis = r3; }
            bool ok = big > 1e-12f;
            if (ok)
                for (int it = 0; it < 4; it++)
                {
                    Vector4 nx = r0 * axis.X + r1 * axis.Y + r2 * axis.Z + r3 * axis.W;
                    float len = nx.LengthSquared();
                    if (len < 1e-30f) { ok = false; break; }
                    axis = nx / MathF.Sqrt(len);
                }
            if (!ok) axis = mask / MathF.Sqrt(Math.Max(1, c.NCh0));
            if (keep) { c.Axis1 = axis; c.Mean1 = mean; }
            float tmin = float.MaxValue, tmax = float.MinValue, proj2 = 0;
            var IW = c.IW;
            foreach (int t in mem)
            {
                float s = Vector4.Dot(XV[t] - mean, axis);
                proj2 += s * s;
                if (s < tmin) tmin = s;
                if (s > tmax) tmax = s;
                IW[t] = s;
            }
            float residual = Math.Max(0, total - proj2);
            if (!store) return residual;
            if (cnt == 0) { tmin = tmax = 0; }

            float hiVal = c.Hdr ? 4095.9f : 255, dflt = c.Hdr ? 0 : 255;
            Vector4 e0 = mean + tmin * axis, e1 = mean + tmax * axis;
            for (int k = 0; k < 4; k++)
            {
                bool on = mask[k] != 0;
                c.E0[p * 4 + k] = on ? Math.Clamp(e0[k], 0, hiVal) : dflt;
                c.E1[p * 4 + k] = on ? Math.Clamp(e1[k], 0, hiVal) : dflt;
            }
            float range = tmax - tmin, ir = range > 1e-6f ? 1 / range : 0, imp = range * range;
            var Imp = c.Imp;
            foreach (int t in mem)
            {
                IW[t] = (IW[t] - tmin) * ir;
                Imp[t] = imp;
            }
            if (c.Dual)
            {
                int k = c.Ccs;
                var X = c.X;
                float lo = float.MaxValue, hi = float.MinValue;
                foreach (int t in mem) { lo = Math.Min(lo, X[t * 4 + k]); hi = Math.Max(hi, X[t * 4 + k]); }
                if (cnt == 0) lo = hi = 0;
                c.E0[p * 4 + k] = lo; c.E1[p * 4 + k] = hi;
                float rr = hi - lo, i2 = rr > 1e-6f ? 1 / rr : 0;
                foreach (int t in mem)
                {
                    IW[MaxN + t] = (X[t * 4 + k] - lo) * i2;
                    Imp[MaxN + t] = rr * rr;
                }
            }
            return residual;
        }

        // ------------------------------------------------------------------ weight grids

        /// <summary>
        /// Decimation errors of the ideal weights of a plane along one axis only: every row fitted by the linear
        /// interpolation of gw grid values (exact weighted least squares, a tridiagonal system), and every column by gh.
        /// A grid gw x gh cannot do better than either, which bounds its error from below.
        /// </summary>
        static float LineError(Ctx c, int plane, int dir, int gs)
        {
            long bit = 1L << (plane * 26 + dir * 13 + gs);
            var arr = dir == 0 ? c.DX : c.DY;
            if ((c.LineDone & bit) != 0) return arr[plane * 13 + gs];
            var f = c.F;
            if (c.P == 1)
            {
                // one partition: the importance is the same for every texel, the fits are fixed projections. The
                // residual summed over the lines is trace(G) - <P, G> for the Gram matrix G of the lines.
                int len0 = dir == 0 ? f.W : f.H;
                long gbit = 1L << (52 + plane * 2 + dir);
                var G = dir == 0 ? c.GramX : c.GramY;
                int go = plane * 144;
                if ((c.LineDone & gbit) == 0)
                {
                    int bw0 = f.W, lines0 = dir == 0 ? f.H : f.W;
                    Array.Clear(G, go, len0 * len0);
                    var iw = c.IW;
                    for (int l = 0; l < lines0; l++)
                        for (int i = 0; i < len0; i++)
                        {
                            float vi = iw[plane * MaxN + (dir == 0 ? l * bw0 + i : i * bw0 + l)];
                            if (vi == 0) continue;
                            for (int j = i; j < len0; j++)
                                G[go + i * len0 + j] += vi * iw[plane * MaxN + (dir == 0 ? l * bw0 + j : j * bw0 + l)];
                        }
                    for (int i = 0; i < len0; i++)
                        for (int j = 0; j < i; j++) G[go + i * len0 + j] = G[go + j * len0 + i];
                    c.LineDone |= gbit;
                }
                var P = dir == 0 ? f.PX[gs] : f.PY[gs];
                float tr = 0, pg = 0;
                for (int i = 0; i < len0; i++) tr += G[go + i * len0 + i];
                for (int i = 0; i < len0 * len0; i++) pg += P[i] * G[go + i];
                float r0 = Math.Max(0, tr - pg) * c.Imp[plane * MaxN];
                arr[plane * 13 + gs] = r0;
                c.LineDone |= bit;
                return r0;
            }
            int bw = f.W, len = dir == 0 ? bw : f.H, lines = dir == 0 ? f.H : bw;
            var J = dir == 0 ? f.JX[gs] : f.JY[gs];
            var Fr = dir == 0 ? f.FX[gs] : f.FY[gs];
            Span<float> v = stackalloc float[12], im = stackalloc float[12];
            float total = 0;
            for (int l = 0; l < lines; l++)
            {
                for (int i = 0; i < len; i++)
                {
                    int t = dir == 0 ? l * bw + i : i * bw + l;
                    v[i] = c.IW[plane * MaxN + t]; im[i] = c.Imp[plane * MaxN + t] + 1e-3f;
                }
                total += LineFit(v, im, len, J, Fr, gs);
            }
            arr[plane * 13 + gs] = total;
            c.LineDone |= bit;
            return total;
        }

        /// <summary>Weighted least squares residual of len samples fitted by linear interpolation of gs knots.</summary>
        static float LineFit(ReadOnlySpan<float> v, ReadOnlySpan<float> im, int len, byte[] J, byte[] Fr, int gs)
        {
            Span<float> diag = stackalloc float[13], off = stackalloc float[13], rhs = stackalloc float[13];
            diag.Clear(); off.Clear(); rhs.Clear();
            float vv = 0;
            for (int i = 0; i < len; i++)
            {
                int j = J[i];
                float b = Fr[i] * (1f / 16), a = 1 - b, w = im[i], x = v[i];
                vv += w * x * x;
                diag[j] += w * a * a; rhs[j] += w * a * x;
                if (b > 0) { diag[j + 1] += w * b * b; off[j] += w * a * b; rhs[j + 1] += w * b * x; }
            }
            // Thomas algorithm on the symmetric tridiagonal system (diag, off), slightly regularized
            Span<float> cp = stackalloc float[13], dp = stackalloc float[13];
            float m0 = diag[0] + 1e-6f;
            cp[0] = off[0] / m0; dp[0] = rhs[0] / m0;
            for (int j = 1; j < gs; j++)
            {
                float m = diag[j] + 1e-6f - off[j - 1] * cp[j - 1];
                cp[j] = off[j] / m;
                dp[j] = (rhs[j] - off[j - 1] * dp[j - 1]) / m;
            }
            float gr = 0;
            float next = 0;
            for (int j = gs - 1; j >= 0; j--)
            {
                float g = j == gs - 1 ? dp[j] : dp[j] - cp[j] * next;
                next = g;
                gr += g * rhs[j];
            }
            return Math.Max(0, vv - gr);
        }

        /// <summary>The ideal weights of each plane scaled by the square root of their importance (zero padded), and their
        /// squared norms, for <see cref="ProjectionError"/>.</summary>
        static void PrepareProjections(Ctx c, int planes)
        {
            int n = c.N, stride = (n + System.Numerics.Vector<float>.Count - 1) / System.Numerics.Vector<float>.Count * System.Numerics.Vector<float>.Count;
            for (int pl = 0; pl < planes; pl++)
            {
                float vv = 0;
                int b = pl * MaxN, o = pl * ProjStride;
                float sq0 = MathF.Sqrt(c.Imp[b]);
                for (int t = 0; t < n; t++)
                {
                    float w = c.IW[b + t], v = w * (c.P == 1 ? sq0 : MathF.Sqrt(c.Imp[b + t]));
                    c.VP[o + t] = v;
                    vv += v * v;
                    c.QI[b + t] = (short)Math.Clamp((int)(w * 256 + 0.5f), 0, 256);
                }
                for (int t = n; t < stride; t++) c.VP[o + t] = 0;
                c.VV[pl] = vv;
            }
            c.VQDone = 0;
        }

        const int ProjStride = 152;
        const int HdrSpare = 2;   // HDR: extra candidates whose colour error is measured exactly

        /// <summary>
        /// Least squares decimation error of a plane on a grid, through the (nearly exact) separable form of the infill:
        /// |V|^2 - &lt;PY, G&gt; for the ideal weight matrix V (bh x bw), G = (V QX)(V QX)^T with the orthonormal 1D
        /// basis QX of the grid width (cached per width) and PY the projection of the grid height.
        /// </summary>
        static unsafe float ProjectionError(Ctx c, Grid g, int plane)
        {
            var f = c.F;
            int bw = f.W, bh = f.H, gw = g.W, gh = g.H, hh = bh * bh;
            int go = (plane * 13 + gw) * 144;
            long bit = 1L << (plane * 13 + gw);
            fixed (float* v = &c.VP[plane * ProjStride], G = &c.VQ[go])
            {
                if ((c.VQDone & bit) == 0)
                {
                    float* B = stackalloc float[144];
                    int bs;
                    if (gw == bw) { B = v; bs = bw; }
                    else
                    {
                        bs = gw;
                        fixed (float* qx = f.QX[gw])
                            for (int y = 0; y < bh; y++)
                                for (int k = 0; k < gw; k++)
                                {
                                    float sum = 0;
                                    float* vr = v + y * bw;
                                    for (int x = 0; x < bw; x++) sum += vr[x] * qx[x * gw + k];
                                    B[y * gw + k] = sum;
                                }
                    }
                    for (int a = 0; a < bh; a++)
                        for (int b = a; b < bh; b++)
                        {
                            float sum = 0;
                            float* ra = B + a * bs, rb = B + b * bs;
                            for (int k = 0; k < gw; k++) sum += ra[k] * rb[k];
                            G[a * bh + b] = sum; G[b * bh + a] = sum;
                        }
                    c.VQDone |= bit;
                }
                float r = 0;
                if (gh == bh) for (int a = 0; a < bh; a++) r += G[a * bh + a];
                else
                    fixed (float* py = f.PY[gh])
                    {
                        int i = 0;
                        if (System.Numerics.Vector.IsHardwareAccelerated)
                        {
                            int vl = System.Numerics.Vector<float>.Count;
                            var acc = System.Numerics.Vector<float>.Zero;
                            for (; i + vl <= hh; i += vl)
                                acc += System.Runtime.CompilerServices.Unsafe.ReadUnaligned<System.Numerics.Vector<float>>(py + i)
                                     * System.Runtime.CompilerServices.Unsafe.ReadUnaligned<System.Numerics.Vector<float>>(G + i);
                            r = System.Numerics.Vector.Sum(acc);
                        }
                        for (; i < hh; i++) r += py[i] * G[i];
                    }
                return Math.Max(0, c.VV[plane] - r);
            }
        }

        /// <summary>
        /// The error from representing the ideal weights of a plane on the grid (decimated into GW by
        /// <see cref="Decimate"/>) quantized to weight range wq: sum of importance * (infilled - ideal)^2.
        /// </summary>
        static unsafe float WeightQuantError(Ctx c, Grid g, int wq, int plane)
        {
            int n = c.N;
            var near = WNear[wq];
            var unq = WUnq[wq];
            float e = 0;
            fixed (float* iw = &c.IW[plane * MaxN], imp = &c.Imp[plane * MaxN], gw = &c.GW[plane * 64])
            {
                if (g.Identity)
                {
                    // the squared error of quantizing each texel's weight, from a table over the weight in 1/256 steps
                    var tab = WErr[wq];
                    var qi = c.QI;
                    int b = plane * MaxN;
                    if (c.P == 1)
                    {
                        for (int t = 0; t < n; t++) e += tab[qi[b + t]];
                        return e * imp[0];
                    }
                    for (int t = 0; t < n; t++) e += tab[qi[b + t]] * imp[t];
                    return e;
                }
                float* q = stackalloc float[64];
                for (int k = 0; k < g.Count; k++) q[k] = unq[near[Math.Clamp((int)(gw[k] * 256 + 0.5f), 0, 256)]] * (1f / 64);
                fixed (int* idx = g.Idx)
                fixed (float* wt = g.WtF)
                    for (int t = 0; t < n; t++)
                    {
                        int* ix = idx + t * 4;
                        float* w = wt + t * 4;
                        float d = w[0] * q[ix[0]] + w[1] * q[ix[1]] + w[2] * q[ix[2]] + w[3] * q[ix[3]] - iw[t];
                        e += d * d * imp[t];
                    }
            }
            return e;
        }

        /// <summary>
        /// Estimated error added by quantizing the ideal endpoints in endpoint mode cem with colour range cq: the endpoint
        /// offsets through the quadratic error of the ideal weights (cached per trial).
        /// </summary>
        static float ColourError(Ctx c, int ci, int cem, int cq, float colUnit)
        {
            ref float slot = ref c.ColErr[ci * 21 + cq];
            if (slot >= 0) return slot;
            float total = 0;
            if (c.Hdr)
            {
                // the HDR modes pack their values irregularly: the error of the mode's own representation at full
                // colour precision (the quantizer run once per endpoint mode), plus the expected error of quantizing
                // its values to range cq
                float full = ExactHdrColour(c, ci, cem, 20);
                total = full + colUnit * (InvQ2[cq] - InvQ2[20]);
            }
            else
            {
                var near = CNear[cq];
                var unq = CUnq[cq];
                Span<float> q0 = stackalloc float[4], q1 = stackalloc float[4], qb = stackalloc float[3];
                for (int p = 0; p < c.P; p++)
                {
                    var a = c.E0.AsSpan(p * 4, 4);
                    var b = c.E1.AsSpan(p * 4, 4);
                    if (cem == 6 || cem == 10)
                    {
                        // RGB scale: bright endpoint quantized, the dark one its scaled copy
                        bool bHi = b[0] + b[1] + b[2] >= a[0] + a[1] + a[2];
                        var br = bHi ? b : a;
                        var dk = bHi ? a : b;
                        float bb = 0, db = 0;
                        for (int k = 0; k < 3; k++) { qb[k] = unq[near[R8(br[k])]]; bb += qb[k] * qb[k]; db += dk[k] * qb[k]; }
                        float sc = bb > 0 ? Math.Clamp(db / bb, 0, 1) : 0;
                        float sq = unq[near[Math.Clamp((int)MathF.Round(sc * 256), 0, 255)]] / 256f;
                        for (int k = 0; k < 3; k++)
                        {
                            float qd = qb[k] * sq;
                            if (bHi) { q0[k] = qd; q1[k] = qb[k]; } else { q0[k] = qb[k]; q1[k] = qd; }
                        }
                        q0[3] = unq[near[R8(a[3])]]; q1[3] = unq[near[R8(b[3])]];
                    }
                    else
                        for (int k = 0; k < 4; k++)
                        {
                            q0[k] = unq[near[c.ERnd[p * 8 + k]]]; q1[k] = unq[near[c.ERnd[p * 8 + 4 + k]]];
                        }
                    for (int k = 0; k < c.Ch; k++)
                    {
                        int o = (p * 2 + (c.Dual && k == c.Ccs ? 1 : 0)) * 3;
                        float e0 = q0[k] - a[k], e1 = q1[k] - b[k];
                        total += c.SWInit[o] * e0 * e0 + 2 * c.SWInit[o + 1] * e0 * e1 + c.SWInit[o + 2] * e1 * e1;
                    }
                }
            }
            slot = total;
            return total;
        }

        /// <summary>
        /// Least squares weights on the grid for the ideal texel weights of a plane (weighted by importance): the weighted
        /// mean of each grid point's footprint, then Gauss-Seidel sweeps. Leaves the infilled result in Inf.
        /// </summary>
        static unsafe void Decimate(Ctx c, Grid g, int plane, int sweeps)
        {
            int n = c.N, gc = g.Count;
            fixed (float* iw = &c.IW[plane * MaxN], imp = &c.Imp[plane * MaxN], gw = &c.GW[plane * 64], inf = c.Inf)
            {
                if (g.Identity)
                {
                    for (int t = 0; t < n; t++) { gw[t] = iw[t]; inf[t] = iw[t]; }
                    return;
                }
                fixed (int* ts = g.TStart, tt = g.TTex, idx = g.Idx)
                fixed (float* tw = g.TWtF, wt = g.WtF)
                {
                    if (g.Pinv != null)
                    {
                        // the unweighted least squares weights (exact for one partition, whose importance is uniform);
                        // the sweeps below are only needed where the weights were clamped or the importance varies
                        bool clamped = false;
                        int gs = g.GStride, vl = System.Numerics.Vector<float>.Count, nv = gs / vl;
                        var accs = stackalloc System.Numerics.Vector<float>[8];
                        for (int j = 0; j < nv; j++) accs[j] = System.Numerics.Vector<float>.Zero;
                        fixed (float* pv = g.Pinv)
                            for (int t = 0; t < n; t++)
                            {
                                var x = new System.Numerics.Vector<float>(iw[t]);
                                float* row = pv + t * gs;
                                for (int j = 0; j < nv; j++)
                                    accs[j] += System.Runtime.CompilerServices.Unsafe.ReadUnaligned<System.Numerics.Vector<float>>(row + j * vl) * x;
                            }
                        float* sums = (float*)accs;
                        for (int k = 0; k < gc; k++)
                        {
                            float v = sums[k];
                            if (v < 0) { v = 0; clamped = true; } else if (v > 1) { v = 1; clamped = true; }
                            gw[k] = v;
                        }
                        if (c.P == 1 && !clamped) sweeps = 0;
                    }
                    else
                    for (int k = 0; k < gc; k++)
                    {
                        float num = 0, den = 0;
                        for (int i = ts[k], e = ts[k + 1]; i < e; i++)
                        {
                            int t = tt[i];
                            float f = tw[i] * (imp[t] + 1e-3f);
                            num += f * iw[t]; den += f;
                        }
                        gw[k] = den > 0 ? num / den : 0;
                    }
                    for (int t = 0; t < n; t++)
                    {
                        int* ix = idx + t * 4;
                        float* w = wt + t * 4;
                        inf[t] = w[0] * gw[ix[0]] + w[1] * gw[ix[1]] + w[2] * gw[ix[2]] + w[3] * gw[ix[3]];
                    }
                    for (int sw = 0; sw < sweeps; sw++)
                        for (int k = 0; k < gc; k++)
                        {
                            float num = 0, den = 0;
                            int s0 = ts[k], s1 = ts[k + 1];
                            for (int i = s0; i < s1; i++)
                            {
                                int t = tt[i];
                                float f = tw[i], im = imp[t] + 1e-3f;
                                num += f * im * (iw[t] - inf[t]);
                                den += f * f * im;
                            }
                            if (den <= 0) continue;
                            float nv = Math.Clamp(gw[k] + num / den, 0, 1), dv = nv - gw[k];
                            if (dv == 0) continue;
                            gw[k] = nv;
                            for (int i = s0; i < s1; i++) inf[tt[i]] += tw[i] * dv;
                        }
                }
            }
        }

        // ------------------------------------------------------------------ candidate encoding


        static long FullEncode(Ctx c, int mi, int cem, int cq)
        {
            ref readonly var m = ref c.F.Modes[mi];
            long bestHere = long.MaxValue;
            var prm = c.Prm;
            var g = c.F.Grids[m.Grid];
            int n = c.N, planes = m.Dual ? 2 : 1;
            Span<float> we0 = stackalloc float[16], we1 = stackalloc float[16];
            c.E0.AsSpan().CopyTo(we0); c.E1.AsSpan().CopyTo(we1);
            c.SWInit.CopyTo(c.SW, 0); c.ST0Init.CopyTo(c.ST0, 0); c.ST1Init.CopyTo(c.ST1, 0);
            Span<int> prevCodes = stackalloc int[18];
            int prevCem = -1, nvals = c.P * CemValues(cem);
            for (int it = 0; ; it++)
            {
                int cemUsed = QuantizeEndpoints(c, cem, cq, we0, we1);
                if (it > 0 && cemUsed == prevCem && c.Codes.AsSpan(0, nvals).SequenceEqual(prevCodes.Slice(0, nvals))) break;   // nothing moved
                prevCem = cemUsed;
                c.Codes.AsSpan(0, nvals).CopyTo(prevCodes);
                IdealWeights(c);
                for (int pl = 0; pl < planes; pl++)
                {
                    Decimate(c, g, pl, prm.DecIters);
                    var wnear = WNear[m.Wq];
                    var wunq = WUnq[m.Wq];
                    for (int k = 0; k < g.Count; k++)
                    {
                        int code = wnear[Math.Clamp((int)(c.GW[pl * 64 + k] * 256 + 0.5f), 0, 256)];
                        c.WC[pl * 64 + k] = code;
                        c.WI[pl * 64 + k] = wunq[code];
                    }
                    for (int t = 0; t < n; t++) c.TW[pl * MaxN + t] = Infill(c, g, pl, t);
                }
                long err = Error(c);
                if (c.RefineNow && err > 0) err = RefineWeights(c, g, m.Wq, planes, err);
                bestHere = Math.Min(bestHere, err);
                if (err < c.BestErr)
                {
                    c.BestErr = err;
                    Pack(c, m, cemUsed, cq, c.Best);
                    c.BestMode = mi; c.BestCem = cem; c.BestCq = cq; c.BestP = c.P; c.BestSeed = c.Seed;
                    c.BestDual = c.Dual; c.BestCcs = c.Ccs;
                    Array.Copy(c.Part, c.BestPart, c.N);
                }
                // refit only candidates that are about as good as the best so far
                if (it >= prm.Iters || err == 0 || c.Q != BcQuality.High && err > c.BestErr + (c.BestErr >> 3)) break;
                ComputeSums(c, true);
                SolveEndpoints(c, we0, we1);
            }
            return bestHere;
        }

        static int Infill(Ctx c, Grid g, int plane, int t)
        {
            var wi = c.WI;
            int b = plane * 64, i = t * 4;
            return (wi[b + g.Idx[i]] * g.Wt[i] + wi[b + g.Idx[i + 1]] * g.Wt[i + 1] + wi[b + g.Idx[i + 2]] * g.Wt[i + 2]
                + wi[b + g.Idx[i + 3]] * g.Wt[i + 3] + 8) >> 4;
        }

        /// <summary>Ideal weights (0..1) of every texel against the decoded endpoints, and their importance.</summary>
        static void IdealWeights(Ctx c)
        {
            var X = c.X;
            var D = c.D;
            Span<float> dir = stackalloc float[4];
            for (int p = 0; p < c.P; p++)
            {
                int cnt = c.MemN[p];
                var mem = c.Mem.AsSpan(p * MaxN, cnt);
                float dd = 0;
                for (int i = 0; i < c.NCh0; i++)
                {
                    int k = c.Ch0[i];
                    dir[i] = D[p * 8 + 4 + k] - D[p * 8 + k];
                    dd += dir[i] * dir[i];
                }
                float idd = dd > 1e-9f ? 1 / dd : 0;
                foreach (int t in mem)
                {
                    float s = 0;
                    for (int i = 0; i < c.NCh0; i++) s += (X[t * 4 + c.Ch0[i]] - D[p * 8 + c.Ch0[i]]) * dir[i];
                    c.IW[t] = Math.Clamp(s * idd, 0, 1);
                    c.Imp[t] = dd;
                }
                if (c.Dual)
                {
                    int k = c.Ccs;
                    float d0 = D[p * 8 + k], d = D[p * 8 + 4 + k] - d0, id = Math.Abs(d) > 1e-6f ? 1 / d : 0;
                    foreach (int t in mem)
                    {
                        c.IW[MaxN + t] = Math.Clamp((X[t * 4 + k] - d0) * id, 0, 1);
                        c.Imp[MaxN + t] = d * d;
                    }
                }
            }
        }

        /// <summary>
        /// The sums of the per channel quadratic error for fixed weights: per partition and plane s00 = sum (1 - w)^2,
        /// s01 = sum (1 - w) w, s11 = sum w^2; per partition and channel t0 = sum (1 - w) x, t1 = sum w x. Weights from the
        /// decoded texel weights (<paramref name="decoded"/>) or the ideal ones (IW).
        /// </summary>
        static void ComputeSums(Ctx c, bool decoded)
        {
            var X = c.X;
            var SW = c.SW;
            var T0 = c.ST0;
            var T1 = c.ST1;
            int planes = c.Dual ? 2 : 1;
            for (int p = 0; p < c.P; p++)
            {
                int cnt = c.MemN[p];
                var mem = c.Mem.AsSpan(p * MaxN, cnt);
                Vector4 t0 = Vector4.Zero, t1 = Vector4.Zero;
                for (int pl = 0; pl < planes; pl++)
                {
                    float a = 0, b = 0, d = 0;
                    Vector4 x0 = Vector4.Zero, x1 = Vector4.Zero;
                    foreach (int t in mem)
                    {
                        float w = decoded ? c.TW[pl * MaxN + t] * (1f / 64) : c.IW[pl * MaxN + t], v = 1 - w;
                        a += v * v; b += v * w; d += w * w;
                        x0 += c.XV[t] * v; x1 += c.XV[t] * w;
                    }
                    SW[(p * 2 + pl) * 3] = a; SW[(p * 2 + pl) * 3 + 1] = b; SW[(p * 2 + pl) * 3 + 2] = d;
                    if (pl == 0) { t0 = x0; t1 = x1; }
                    else { t0[c.Ccs] = x0[c.Ccs]; t1[c.Ccs] = x1[c.Ccs]; }
                }
                for (int k = 0; k < 4; k++) { T0[p * 4 + k] = t0[k]; T1[p * 4 + k] = t1[k]; }
            }
        }

        /// <summary>Least squares endpoints from the sums (where the system is not singular).</summary>
        static void SolveEndpoints(Ctx c, Span<float> we0, Span<float> we1)
        {
            float hiVal = c.Hdr ? 4095.9f : 255;
            for (int p = 0; p < c.P; p++)
                for (int k = 0; k < c.Ch; k++)
                {
                    float lo = 0, hi = hiVal;
                    if (c.Hdr)
                    {
                        // HDR: endpoints beyond the data's range interpolate in the logarithmic domain and can land
                        // far from it in linear terms; keep them within
                        lo = float.MaxValue; hi = float.MinValue;
                        for (int i = 0; i < c.MemN[p]; i++)
                        {
                            float x = c.X[c.Mem[p * MaxN + i] * 4 + k];
                            lo = Math.Min(lo, x); hi = Math.Max(hi, x);
                        }
                        if (lo > hi) { lo = 0; hi = hiVal; }
                    }
                    int pl = c.Dual && k == c.Ccs ? 1 : 0, o = (p * 2 + pl) * 3;
                    float a = c.SW[o], b = c.SW[o + 1], d = c.SW[o + 2], det = a * d - b * b;
                    if (det <= 1e-4f * (a + d) * (a + d)) continue;
                    float id = 1 / det, x0 = c.ST0[p * 4 + k], x1 = c.ST1[p * 4 + k];
                    we0[p * 4 + k] = Math.Clamp((d * x0 - b * x1) * id, lo, hi);
                    we1[p * 4 + k] = Math.Clamp((a * x1 - b * x0) * id, lo, hi);
                }
        }

        // ------------------------------------------------------------------ exact error

        static long Error(Ctx c)
        {
            int n = c.N;
            long e = 0;
            if (!c.Hdr && !c.Srgb && !c.Dual)
            {
                var C = c.C16;
                var T = c.Tgt;
                var TW = c.TW;
                var part = c.Part;
                int ch = c.Ch;
                for (int t = 0; t < n; t++)
                {
                    int p8 = part[t] * 8, w = TW[t], iw = 64 - w, s = 0;
                    for (int k = 0; k < ch; k++)
                    {
                        int u = (C[p8 + k] * iw + C[p8 + 4 + k] * w + 32) >> 6;
                        int d = (u >> 8) - T[t * 4 + k];
                        s += d * d;
                    }
                    e += s;
                }
                return e;
            }
            for (int t = 0; t < n; t++) e += TexelError(c, t, -1);
            return e;
        }

        /// <summary>Squared error of texel t over the channels of the given plane (-1: all channels).</summary>
        static int TexelError(Ctx c, int t, int plane)
        {
            int p = c.Part[t], w0 = c.TW[t], w1 = c.Dual ? c.TW[MaxN + t] : w0;
            var C = c.C16;
            int e = 0;
            if (!c.Dual && !c.Hdr && !c.Srgb)
            {
                int p8 = p * 8, iw = 64 - w0;
                var T = c.Tgt;
                for (int k = 0; k < c.Ch; k++)
                {
                    int d = ((C[p8 + k] * iw + C[p8 + 4 + k] * w0 + 32) >> 14) - T[t * 4 + k];
                    e += d * d;
                }
                return e;
            }
            for (int k = 0; k < c.Ch; k++)
            {
                bool second = c.Dual && k == c.Ccs;
                if (plane >= 0 && second != (plane == 1)) continue;
                int w = second ? w1 : w0;
                int u = (C[p * 8 + k] * (64 - w) + C[p * 8 + 4 + k] * w + 32) >> 6;
                int d;
                if (c.Hdr) d = (u - c.Tgt[t * 4 + k]) >> 2;   // error domain: 14 bits, keeping the sums in int range
                else if (c.Srgb && k == 3) d = SrgbAlpha[u] - c.Tgt[t * 4 + k];
                else d = (u >> 8) - c.Tgt[t * 4 + k];
                e += d * d;
            }
            return e;
        }

        /// <summary>Greedy single steps on the stored weights (High).</summary>
        static unsafe long RefineWeights(Ctx c, Grid g, int wq, int planes, long err)
        {
            // per texel and plane the error as a quadratic in the infilled weight w (0..64): A w^2 + 2 B w + C
            int n = c.N;
            float* qa = stackalloc float[2 * MaxN], qb = stackalloc float[2 * MaxN];
            float scale = c.Hdr ? 0.25f : 1f / 256;
            for (int pl = 0; pl < planes; pl++)
                for (int t = 0; t < n; t++)
                {
                    int p = c.Part[t];
                    float A = 0, B = 0;
                    for (int k = 0; k < c.Ch; k++)
                    {
                        if (planes == 2 && (k == c.Ccs) != (pl == 1)) continue;
                        float e0 = c.C16[p * 8 + k] * scale, e1 = c.C16[p * 8 + 4 + k] * scale;
                        float x = c.Hdr ? c.Tgt[t * 4 + k] * scale : c.Tgt[t * 4 + k];
                        float a = (e1 - e0) * (1f / 64), b = e0 - x;
                        A += a * a; B += a * b;
                    }
                    qa[pl * MaxN + t] = A; qb[pl * MaxN + t] = B;
                }
            var up = WUp[wq];
            var down = WDown[wq];
            var unq = WUnq[wq];
            bool any = false;
            const float slack = 1.5f;
            Span<int> cand = stackalloc int[2];
            Span<float> cd = stackalloc float[2];
            fixed (int* ts = g.TStart, tt = g.TTex, idx = g.Idx, wt = g.Wt, wi = c.WI, tw = c.TW, wc = c.WC)
                for (int pass = 0; pass < 2; pass++)
                {
                    bool improved = false;
                    for (int pl = 0; pl < planes; pl++)
                    {
                        int* wip = wi + pl * 64, twp = tw + pl * MaxN;
                        float* A = qa + pl * MaxN, B = qb + pl * MaxN;
                        for (int k = 0; k < g.Count; k++)
                        {
                            int s0 = ts[k], s1 = ts[k + 1];
                            int orig = wc[pl * 64 + k];
                            // the model ignores the decoder's rounding: moves it does not rule out are measured exactly
                            int nc = 0;
                            for (int dir = 0; dir < 2; dir++)
                            {
                                int code = dir == 0 ? up[orig] : down[orig];
                                if (code == orig) continue;
                                wip[k] = unq[code];
                                float d = 0;
                                for (int i = s0; i < s1; i++)
                                {
                                    int t = tt[i], j = t * 4;
                                    int w1 = (wip[idx[j]] * wt[j] + wip[idx[j + 1]] * wt[j + 1] + wip[idx[j + 2]] * wt[j + 2] + wip[idx[j + 3]] * wt[j + 3] + 8) >> 4;
                                    int w0 = twp[t];
                                    d += A[t] * (w1 * w1 - w0 * w0) + 2 * B[t] * (w1 - w0);
                                }
                                if (d < slack || s1 - s0 <= 4) { cand[nc] = code; cd[nc++] = d; }
                            }
                            wip[k] = unq[orig];
                            if (nc == 0) continue;
                            if (nc == 2 && cd[1] < cd[0]) { (cand[0], cand[1]) = (cand[1], cand[0]); }
                            int before = 0, bestE = int.MaxValue, bestCode = orig;
                            for (int i = s0; i < s1; i++) before += TexelError(c, tt[i], planes == 1 ? -1 : pl);
                            bestE = before;
                            for (int ci2 = 0; ci2 < nc; ci2++)
                            {
                                wip[k] = unq[cand[ci2]];
                                int after = 0;
                                for (int i = s0; i < s1; i++)
                                {
                                    int t = tt[i], j = t * 4;
                                    twp[t] = (wip[idx[j]] * wt[j] + wip[idx[j + 1]] * wt[j + 1] + wip[idx[j + 2]] * wt[j + 2] + wip[idx[j + 3]] * wt[j + 3] + 8) >> 4;
                                    after += TexelError(c, t, planes == 1 ? -1 : pl);
                                }
                                if (after < bestE) { bestE = after; bestCode = cand[ci2]; }
                            }
                            wc[pl * 64 + k] = bestCode;
                            wip[k] = unq[bestCode];
                            for (int i = s0; i < s1; i++)
                            {
                                int t = tt[i], j = t * 4;
                                twp[t] = (wip[idx[j]] * wt[j] + wip[idx[j + 1]] * wt[j + 1] + wip[idx[j + 2]] * wt[j + 2] + wip[idx[j + 3]] * wt[j + 3] + 8) >> 4;
                            }
                            if (bestCode != orig) improved = any = true;
                        }
                    }
                    if (!improved) break;
                }
            return any ? Error(c) : err;
        }

        // ------------------------------------------------------------------ endpoint quantization

        /// <summary>
        /// Quantizes the target endpoints of every partition in endpoint mode <paramref name="cem"/> (or its base + offset
        /// sibling when that is better for all partitions together): colour codes to Codes, decoded endpoints to C16 / D.
        /// Returns the endpoint mode used.
        /// </summary>
        static int QuantizeEndpoints(Ctx c, int cem, int cq, ReadOnlySpan<float> we0, ReadOnlySpan<float> we1)
        {
            int vals = CemValues(cem);
            float total = 0;
            for (int p = 0; p < c.P; p++)
            {
                c.CurP = p;
                total += QuantPart(c, cem, cq, we0.Slice(p * 4, 4), we1.Slice(p * 4, 4), c.PCodes, c.PE);
                c.PCodes.AsSpan(0, vals).CopyTo(c.Codes.AsSpan(p * vals));
            }
            int used = cem;
            int alt = cem switch { 0 => 1, 4 => 5, 8 => 9, 12 => 13, _ => -1 };
            if (alt >= 0 && c.Prm.Offset)
            {
                bool close = true;
                for (int p = 0; p < c.P && close; p++)
                    for (int k = 0; k < c.Ch; k++)
                        if (MathF.Abs(we1[p * 4 + k] - we0[p * 4 + k]) > (alt == 1 ? 64 : 34)) { close = false; break; }
                if (close)
                {
                    float t2 = 0;
                    for (int p = 0; p < c.P && t2 < total; p++)
                    {
                        c.CurP = p;
                        t2 += QuantPart(c, alt, cq, we0.Slice(p * 4, 4), we1.Slice(p * 4, 4), c.PCodes, c.PE);
                        c.PCodes.AsSpan(0, vals).CopyTo(c.QCodes.AsSpan(p * vals));
                    }
                    if (t2 < total)
                    {
                        used = alt;
                        c.QCodes.AsSpan(0, c.P * vals).CopyTo(c.Codes);
                    }
                }
            }
            // decoded endpoints from the codes
            Span<int> u = stackalloc int[8];
            Span<int> e = stackalloc int[8];
            for (int p = 0; p < c.P; p++)
            {
                for (int i = 0; i < vals; i++) u[i] = CUnq[cq][c.Codes[p * vals + i]];
                int flags = AstcBlocks.Endpoints(used, u, e);
                for (int k = 0; k < 4; k++)
                {
                    for (int j = 0; j < 2; j++)
                    {
                        int ev = e[j * 4 + k], c16;
                        float d;
                        bool hdr = (flags & (k == 3 ? AstcBlocks.HdrA : AstcBlocks.HdrRgb)) != 0;
                        if (hdr) { c16 = ev << 4; d = ev; }
                        else if (c.Srgb && k < 3) { c16 = ev << 8 | 0x80; d = c16 / 257f; }
                        else { c16 = ev << 8 | ev; d = c.Hdr ? c16 / 16f : ev; }
                        c.C16[p * 8 + j * 4 + k] = c16;
                        c.D[p * 8 + j * 4 + k] = d;
                    }
                }
            }
            return used;
        }

        static int R8(float x) => Math.Clamp((int)MathF.Round(x), 0, 255);

        /// <summary>The fitting-domain value of decoded endpoint value ev of channel k under endpoint mode cem.</summary>
        static float Fit(Ctx c, int cem, int k, int ev)
        {
            if (c.Hdr)
            {
                bool ldrAlpha = k == 3 && cem == 14;
                return ldrAlpha ? (ev << 8 | ev) / 16f : ev;
            }
            if (c.Srgb && k < 3) return (ev << 8 | 0x80) / 257f;
            return ev;
        }

        /// <summary>
        /// The texel error of decoded endpoints e (8 ints) of the current partition for the current weight sums, up to a
        /// constant; the better of both endpoint orders (the weights are recomputed for the decoded endpoints).
        /// </summary>
        static float EndpointError(Ctx c, int cem, ReadOnlySpan<int> e, ReadOnlySpan<float> a, ReadOnlySpan<float> b)
        {
            float s = 0, r = 0;
            int p = c.CurP;
            for (int k = 0; k < c.Ch; k++)
            {
                int o = (p * 2 + (c.Dual && k == c.Ccs ? 1 : 0)) * 3;
                float d0 = Fit(c, cem, k, e[k]), d1 = Fit(c, cem, k, e[4 + k]);
                float s00 = c.SW[o], s01 = c.SW[o + 1], s11 = c.SW[o + 2], x0 = c.ST0[p * 4 + k], x1 = c.ST1[p * 4 + k];
                float q = 2 * s01 * d0 * d1;
                s += s00 * d0 * d0 + q + s11 * d1 * d1 - 2 * (x0 * d0 + x1 * d1);
                r += s00 * d1 * d1 + q + s11 * d0 * d0 - 2 * (x0 * d1 + x1 * d0);
                if (c.Hdr && k < 3)
                {
                    // HDR: an endpoint well above the partition's brightest value interpolates (logarithmically) into
                    // values far too bright: never preferred, whatever the fit elsewhere
                    float over = Math.Max(d0, d1) - (c.PMax[p * 4 + k] + HdrOvershoot);
                    if (over > 0) { s += 1e9f * (1 + over); r += 1e9f * (1 + over); }
                }
            }
            return Math.Min(s, r);
        }

        /// <summary>HDR: the endpoint quantization error estimate with the quantizer run at range cq (cached per trial).</summary>
        static float ExactHdrColour(Ctx c, int ci, int cem, int cq)
        {
            ref float slot = ref c.ColErrExact[ci * 21 + cq];
            if (slot >= 0) return slot;
            float f = 0;
            for (int p = 0; p < c.P; p++)
            {
                c.CurP = p;
                var a = c.E0.AsSpan(p * 4, 4);
                var b = c.E1.AsSpan(p * 4, 4);
                f += Math.Max(0, QuantPart(c, cem, cq, a, b, c.PCodes, c.PE) - IdealError(c, a, b));
            }
            slot = f;
            return f;
        }

        /// <summary>The quadratic error (as <see cref="EndpointError"/>) of unquantized endpoints a, b.</summary>
        static float IdealError(Ctx c, ReadOnlySpan<float> a, ReadOnlySpan<float> b)
        {
            float s = 0, r = 0;
            int p = c.CurP;
            for (int k = 0; k < c.Ch; k++)
            {
                s += ChannelError(c, k, a[k], b[k]);
                r += ChannelError(c, k, b[k], a[k]);
            }
            return Math.Min(s, r);
        }

        /// <summary>The quadratic error of channel k for endpoint values (fitting domain) d0 at weight 0, d1 at weight 1.</summary>
        static float ChannelError(Ctx c, int k, float d0, float d1)
        {
            int p = c.CurP, o = (p * 2 + (c.Dual && k == c.Ccs ? 1 : 0)) * 3;
            return c.SW[o] * d0 * d0 + 2 * c.SW[o + 1] * d0 * d1 + c.SW[o + 2] * d1 * d1 - 2 * (c.ST0[p * 4 + k] * d0 + c.ST1[p * 4 + k] * d1);
        }

        /// <summary>Quantizes the wanted unquantized values u to codes, decodes and measures; keeps the result in
        /// (codes, e) when it beats <paramref name="best"/>.</summary>
        static float Try(Ctx c, int cem, int cq, ReadOnlySpan<int> u, int nv, ReadOnlySpan<float> a, ReadOnlySpan<float> b,
            float best, Span<int> codes, Span<int> e)
        {
            var near = CNear[cq];
            var unq = CUnq[cq];
            Span<int> tc = c.TCodes, te = c.TE;
            Span<int> v = stackalloc int[8];
            for (int i = 0; i < nv; i++) { tc[i] = near[Math.Clamp(u[i], 0, 255)]; v[i] = unq[tc[i]]; }
            AstcBlocks.Endpoints(cem, v, te);
            float err = EndpointError(c, cem, te, a, b);
            if (err < best)
            {
                tc.Slice(0, nv).CopyTo(codes);
                te.CopyTo(e);
                return err;
            }
            return best;
        }

        /// <summary>As <see cref="Try"/> for codes chosen directly.</summary>
        static float TryCodes(Ctx c, int cem, int cq, ReadOnlySpan<int> codesIn, int nv, ReadOnlySpan<float> a, ReadOnlySpan<float> b,
            float best, Span<int> codes, Span<int> e)
        {
            var unq = CUnq[cq];
            Span<int> te = c.TE;
            Span<int> v = stackalloc int[8];
            for (int i = 0; i < nv; i++) v[i] = unq[codesIn[i]];
            AstcBlocks.Endpoints(cem, v, te);
            float err = EndpointError(c, cem, te, a, b);
            if (err < best)
            {
                codesIn.Slice(0, nv).CopyTo(codes);
                te.CopyTo(e);
                return err;
            }
            return best;
        }

        static float QuantPart(Ctx c, int cem, int cq, ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<int> codes, Span<int> e)
        {
            Span<int> u = stackalloc int[8];
            Span<int> k8 = stackalloc int[8];
            float best = float.MaxValue;
            // lo / hi: the endpoints ordered by brightness (the order rules of several modes depend on it)
            Span<float> lo = stackalloc float[4], hi = stackalloc float[4];
            float sa = a[0] + a[1] + a[2], sb = b[0] + b[1] + b[2];
            if (sb >= sa) { a.CopyTo(lo); b.CopyTo(hi); } else { b.CopyTo(lo); a.CopyTo(hi); }
            switch (cem)
            {
                case 0:
                    LumPair(c, cq, lo, hi, sb < sa, u);
                    best = Try(c, cem, cq, u, 2, lo, hi, best, codes, e);
                    return Polish(c, cem, cq, 2, best, codes, e);
                case 1:
                    {
                        float l0 = (lo[0] + lo[1] + lo[2]) / 3, l1 = (hi[0] + hi[1] + hi[2]) / 3;
                        int top = QMax[cq];
                        for (int k1 = 0; k1 <= top; k1++)
                        {
                            int u1 = CUnq[cq][k1], hb = u1 & 0xC0, d = u1 & 0x3F;
                            int low = Math.Clamp((int)MathF.Round((l0 + l1 - d) / 2) - hb, 0, 63);
                            k8[0] = CNear[cq][low << 2]; k8[1] = k1;
                            best = TryCodes(c, cem, cq, k8, 2, lo, hi, best, codes, e);
                        }
                        return best;
                    }
                case 4:
                    LumPair(c, cq, lo, hi, sb < sa, u);
                    ChannelPair(c, cem, cq, 3, lo[3], hi[3], sb < sa, out u[2], out u[3]);
                    best = Try(c, cem, cq, u, 4, lo, hi, best, codes, e);
                    return Polish(c, cem, cq, 4, best, codes, e);
                case 5:
                    OffsetPair(cq, (lo[0] + lo[1] + lo[2]) / 3, (hi[0] + hi[1] + hi[2]) / 3, out k8[0], out k8[1]);
                    OffsetPair(cq, lo[3], hi[3], out k8[2], out k8[3]);
                    return TryCodes(c, cem, cq, k8, 4, lo, hi, best, codes, e);
                case 6:
                case 10:
                    {
                        for (int k = 0; k < 3; k++) u[k] = R8(hi[k]);
                        float qb0 = CUnq[cq][CNear[cq][u[0]]], qb1 = CUnq[cq][CNear[cq][u[1]]], qb2 = CUnq[cq][CNear[cq][u[2]]];
                        float bb = qb0 * qb0 + qb1 * qb1 + qb2 * qb2;
                        float s = bb > 0 ? (lo[0] * qb0 + lo[1] * qb1 + lo[2] * qb2) / bb : 0;
                        u[3] = Math.Clamp((int)MathF.Round(s * 256), 0, 255);
                        u[4] = R8(lo[3]); u[5] = R8(hi[3]);
                        best = Try(c, cem, cq, u, cem == 6 ? 4 : 6, lo, hi, best, codes, e);
                        return Polish(c, cem, cq, cem == 6 ? 4 : 6, best, codes, e);
                    }
                case 8:
                case 12:
                    {
                        int nv = cem == 8 ? 6 : 8;
                        for (int k = 0; k < nv / 2; k++) ChannelPair(c, cem, cq, k, lo[k], hi[k], sb < sa, out u[2 * k], out u[2 * k + 1]);
                        // the direct decoding needs the second endpoint's RGB sum to be the larger: else store them swapped
                        if (u[1] + u[3] + u[5] < u[0] + u[2] + u[4])
                            for (int k = 0; k < nv / 2; k++) (u[2 * k], u[2 * k + 1]) = (u[2 * k + 1], u[2 * k]);
                        best = Try(c, cem, cq, u, nv, lo, hi, best, codes, e);
                        // blue contraction: stored (2r - b, 2g - b, b), decoded ((r + b) / 2, (g + b) / 2, b), endpoints swapped
                        bool ok = true;
                        for (int k = 0; k < 2 && ok; k++)
                        {
                            float x0 = 2 * lo[k] - lo[2], x1 = 2 * hi[k] - hi[2];
                            if (x0 < -1 || x0 > 256 || x1 < -1 || x1 > 256) ok = false;
                        }
                        if (ok && cq < 20)
                        {
                            // the decoder contracts when the stored second endpoint sums lower: that one decodes as e0
                            float iL = 2 * (lo[0] + lo[1]) - lo[2], iH = 2 * (hi[0] + hi[1]) - hi[2];
                            var e0t = iL < iH ? lo : hi;
                            var e1t = iL < iH ? hi : lo;
                            for (int k = 0; k < 2; k++) { u[2 * k] = R8(2 * e1t[k] - e1t[2]); u[2 * k + 1] = R8(2 * e0t[k] - e0t[2]); }
                            u[4] = R8(e1t[2]); u[5] = R8(e0t[2]);
                            u[6] = R8(e1t[3]); u[7] = R8(e0t[3]);
                            best = Try(c, cem, cq, u, nv, e0t, e1t, best, codes, e);
                        }
                        return Polish(c, cem, cq, nv, best, codes, e);
                    }
                case 9:
                case 13:
                    {
                        int nv = cem == 9 ? 6 : 8;
                        for (int k = 0; k < (cem == 9 ? 3 : 4); k++) OffsetPair(cq, lo[k], hi[k], out k8[2 * k], out k8[2 * k + 1]);
                        best = TryCodes(c, cem, cq, k8, nv, lo, hi, best, codes, e);
                        // the offsets must not sum negative (that selects blue contraction): also try from the other end
                        for (int k = 0; k < (cem == 9 ? 3 : 4); k++) OffsetPair(cq, hi[k], lo[k], out k8[2 * k], out k8[2 * k + 1]);
                        best = TryCodes(c, cem, cq, k8, nv, lo, hi, best, codes, e);
                        return Polish(c, cem, cq, nv, best, codes, e);
                    }
                case 2:
                    u[0] = R8((lo[0] + lo[1] + lo[2]) / 48); u[1] = R8((hi[0] + hi[1] + hi[2]) / 48);
                    return Try(c, cem, cq, u, 2, lo, hi, best, codes, e);
                case 7:
                    return HdrScale(c, cq, lo, hi, codes, e);
                default:   // 11, 14
                    return HdrDirect(c, cem, cq, lo, hi, codes, e);
            }
        }

        /// <summary>
        /// The stored value pair of channel k for a direct mode: the floor / ceiling representable values around both
        /// targets, the pair with the least quadratic error (<paramref name="swapped"/>: lo is the endpoint at weight 1).
        /// </summary>
        static void ChannelPair(Ctx c, int cem, int cq, int k, float lo, float hi, bool swapped, out int v0, out int v1)
        {
            var fl = CFloor[cq];
            var ce = CCeil[cq];
            int a0 = fl[Math.Clamp((int)MathF.Floor(lo), 0, 255)], a1 = ce[Math.Clamp((int)MathF.Ceiling(lo), 0, 255)];
            int b0 = fl[Math.Clamp((int)MathF.Floor(hi), 0, 255)], b1 = ce[Math.Clamp((int)MathF.Ceiling(hi), 0, 255)];
            float best = float.MaxValue;
            v0 = a0; v1 = b0;
            int p = c.CurP, o = (p * 2 + (c.Dual && k == c.Ccs ? 1 : 0)) * 3;
            float s00 = c.SW[o], s01 = c.SW[o + 1], s11 = c.SW[o + 2], t0 = c.ST0[p * 4 + k], t1 = c.ST1[p * 4 + k];
            if (swapped) { (s00, s11) = (s11, s00); (t0, t1) = (t1, t0); }
            bool plain = !c.Hdr && !c.Srgb;
            for (int i = 0; i < 4; i++)
            {
                int x = (i & 1) == 0 ? a0 : a1, y = (i & 2) == 0 ? b0 : b1;
                if (i > 0 && (x == a0 && (i & 1) != 0 || y == b0 && (i & 2) != 0)) continue;
                float dx = plain ? x : Fit(c, cem, k, x), dy = plain ? y : Fit(c, cem, k, y);
                float err = s00 * dx * dx + 2 * s01 * dx * dy + s11 * dy * dy - 2 * (t0 * dx + t1 * dy);
                if (err < best) { best = err; v0 = x; v1 = y; }
            }
        }

        /// <summary>As <see cref="ChannelPair"/> for the luminance of modes 0 and 4 (the error summed over RGB).</summary>
        static void LumPair(Ctx c, int cq, ReadOnlySpan<float> lo, ReadOnlySpan<float> hi, bool swapped, Span<int> u)
        {
            float l0 = (lo[0] + lo[1] + lo[2]) / 3, l1 = (hi[0] + hi[1] + hi[2]) / 3;
            var fl = CFloor[cq];
            var ce = CCeil[cq];
            int a0 = fl[Math.Clamp((int)MathF.Floor(l0), 0, 255)], a1 = ce[Math.Clamp((int)MathF.Ceiling(l0), 0, 255)];
            int b0 = fl[Math.Clamp((int)MathF.Floor(l1), 0, 255)], b1 = ce[Math.Clamp((int)MathF.Ceiling(l1), 0, 255)];
            float best = float.MaxValue;
            u[0] = a0; u[1] = b0;
            for (int i = 0; i < 4; i++)
            {
                int x = (i & 1) == 0 ? a0 : a1, y = (i & 2) == 0 ? b0 : b1;
                float err = 0;
                for (int k = 0; k < 3; k++)
                {
                    float dx = Fit(c, 0, k, x), dy = Fit(c, 0, k, y);
                    err += swapped ? ChannelError(c, k, dy, dx) : ChannelError(c, k, dx, dy);
                }
                if (err < best) { best = err; u[0] = x; u[1] = y; }
            }
        }

        /// <summary>Greedy single code steps on the stored values while the quadratic error drops (Normal, High).</summary>
        static float Polish(Ctx c, int cem, int cq, int nv, float best, Span<int> codes, Span<int> e)
        {
            if (c.Q == BcQuality.Fast || best == float.MaxValue) return best;
            Span<int> k8 = stackalloc int[8];
            var up = CUp[cq];
            var down = CDown[cq];
            for (int pass = 0; pass < (c.Q == BcQuality.High ? 3 : 1); pass++)
            {
                bool improved = false;
                for (int i = 0; i < nv; i++)
                    for (int dir = 0; dir < 2; dir++)
                    {
                        codes.Slice(0, nv).CopyTo(k8);
                        int nc = dir == 0 ? up[codes[i]] : down[codes[i]];
                        if (nc == codes[i]) continue;
                        k8[i] = nc;
                        float r = TryCodes(c, cem, cq, k8, nv, default, default, best, codes, e);
                        if (r < best) { best = r; improved = true; }
                    }
                if (!improved) break;
            }
            return best;
        }

        /// <summary>Codes for a bit-transfer base + offset pair (modes 5, 9, 13): base = (v0 &gt;&gt; 1) | (v1 &amp; 0x80),
        /// offset = the signed 6 bits v1 &gt;&gt; 1.</summary>
        static void OffsetPair(int cq, float t0, float t1, out int c0, out int c1)
        {
            var unq = CUnq[cq];
            var near = CNear[cq];
            if (cq == 20)
            {
                int B = R8(t0), d = Math.Clamp((int)MathF.Round(t1) - B, -32, 31);
                c0 = (B & 0x7F) << 1;
                c1 = (B & 0x80) | (d & 0x3F) << 1;
                return;
            }
            float best = float.MaxValue;
            c0 = c1 = 0;
            var exact = CExact[cq];
            int bt = R8(t0), dt = Math.Clamp((int)MathF.Round(t1 - t0), -32, 31);
            for (int tp = 0; tp < 2; tp++)
            {
                int top = tp == 0 ? bt & 0x80 : (bt & 0x80) ^ 0x80;
                if (tp == 1 && Math.Abs(bt - 128) > 24) break;
                for (int dd = 0; dd <= 16; dd++)
                    for (int sg = -1; sg <= 1; sg += 2)
                    {
                        if (dd == 0 && sg > 0) continue;
                        int d = dt + sg * dd;
                        if (d < -32 || d > 31) continue;
                        for (int lb = 0; lb < 2; lb++)
                        {
                            int k1 = exact[top | (d & 0x3F) << 1 | lb];
                            if (k1 < 0) continue;
                            int low = Math.Clamp((int)MathF.Round((t0 + t1 - d) * 0.5f) - top, 0, 127);
                            int k0 = near[low << 1];
                            int bse = (unq[k0] >> 1) | top, e1 = Math.Clamp(bse + d, 0, 255);
                            float d0 = bse - t0, d1 = e1 - t1, err = d0 * d0 + d1 * d1;
                            if (err < best) { best = err; c0 = k0; c1 = k1; }
                        }
                    }
            }
            if (best == float.MaxValue)
            {
                c0 = near[(bt & 0x7F) << 1];
                c1 = near[bt & 0x80];
            }
        }

        // ------------------------------------------------------------------ HDR endpoint modes

        // mode 11 (HDR RGB direct): extra bits x0..x5 (v2 b6, v3 b6, v4 b6, v5 b6, v4 b5, v5 b5) per submode:
        // (x, field (0 a, 1 c, 2 b0, 3 b1), bit, submode mask)
        static readonly (int x, int f, int bit, int mask)[] M11 =
        {
            (0, 0, 9, 0xA4), (2, 0, 9, 0x08), (4, 0, 9, 0x50), (5, 0, 10, 0x50), (1, 0, 10, 0xA0), (2, 0, 11, 0xC0),
            (1, 1, 6, 0x04), (3, 1, 6, 0xE8), (2, 1, 7, 0x20),
            (0, 2, 6, 0x5B), (1, 3, 6, 0x5B), (2, 2, 7, 0x12), (3, 3, 7, 0x12),
        };
        static readonly int[] M11D = { 7, 6, 7, 6, 5, 6, 5, 6 };

        // mode 7 (HDR RGB base + scale): x0..x6 (v1 b6, v1 b5, v2 b6, v2 b5, v3 b7, v3 b6, v3 b5);
        // (x, field (0 r, 1 g, 2 b, 3 scale), bit, submode mask)
        static readonly (int x, int f, int bit, int mask)[] M7 =
        {
            (0, 1, 6, 0x30), (1, 1, 5, 0x3A), (2, 2, 6, 0x30), (3, 2, 5, 0x3A), (6, 3, 5, 0x3D), (5, 3, 6, 0x2D), (4, 3, 7, 0x04),
            (4, 0, 6, 0x3B), (3, 0, 6, 0x04), (5, 0, 7, 0x10), (2, 0, 7, 0x0F), (1, 0, 8, 0x05), (0, 0, 8, 0x0A), (0, 0, 9, 0x05),
            (6, 0, 9, 0x02), (3, 0, 10, 0x01), (5, 0, 10, 0x02),
        };
        static readonly int[] M7Shift = { 1, 1, 2, 3, 4, 5 };

        static readonly int[,] M11Mask = FieldMasks(M11, 8, new[] { 0x1FF, 0x3F, 0x3F, 0x3F });
        static readonly int[,] M7Mask = FieldMasks(M7, 6, new[] { 0x3F, 0x1F, 0x1F, 0x1F });

        static int[,] FieldMasks((int x, int f, int bit, int mask)[] tab, int modes, int[] base_)
        {
            var r = new int[modes, 4];
            for (int m = 0; m < modes; m++)
            {
                for (int f = 0; f < 4; f++) r[m, f] = base_[f];
                foreach (var e in tab) if ((e.mask >> m & 1) != 0) r[m, e.f] |= 1 << e.bit;
            }
            return r;
        }

        static int Perm(int maj, int i) => maj == 1 ? (i == 0 ? 1 : i == 1 ? 0 : 2) : maj == 2 ? (i == 0 ? 2 : i == 2 ? 0 : 1) : i;

        static int Fits(int v, int mask) => Math.Clamp(v, 0, mask) & mask;

        static float HdrDirect(Ctx c, int cem, int cq, ReadOnlySpan<float> lo, ReadOnlySpan<float> hi, Span<int> codes, Span<int> e)
        {
            Span<int> u = stackalloc int[8];
            Span<int> x = stackalloc int[6];
            Span<int> fv = stackalloc int[4];
            float best = float.MaxValue;
            int nv = cem == 14 ? 8 : 6;
            u[6] = Math.Clamp((int)MathF.Round(lo[3] * 16 / 257), 0, 255);
            u[7] = Math.Clamp((int)MathF.Round(hi[3] * 16 / 257), 0, 255);
            int maj = hi[0] >= hi[1] && hi[0] >= hi[2] ? 0 : hi[1] >= hi[2] ? 1 : 2;
            int p0 = Perm(maj, 0), p1 = Perm(maj, 1), p2 = Perm(maj, 2);
            // submodes from the most precise (smallest shift) down; the first two whose fields hold the values unclamped
            // are tried (all when none does)
            int tried = 0;
            for (int pass = 0; pass < 2 && (tried == 0 || best >= 1e8f); pass++)
            for (int m = 7; m >= 0 && (tried < 2 || pass == 1); m--)
            {
                int sh = (m >> 1) ^ 3;
                float s = 1 << sh;
                int db = M11D[m], dmax = (1 << (db - 1)) - 1;
                int ra = (int)MathF.Round(hi[p0] / s), qa = Fits(ra, M11Mask[m, 0]);
                float A = qa * s;
                int rc = (int)MathF.Round((A - lo[p0]) / s), qc = Fits(rc, M11Mask[m, 1]);
                int rb0 = (int)MathF.Round((A - hi[p1]) / s), qb0 = Fits(rb0, M11Mask[m, 2]);
                int rb1 = (int)MathF.Round((A - hi[p2]) / s), qb1 = Fits(rb1, M11Mask[m, 3]);
                int rd0 = (int)MathF.Round((A - (qb0 + qc) * s - lo[p1]) / s), qd0 = Math.Clamp(rd0, -dmax - 1, dmax);
                int rd1 = (int)MathF.Round((A - (qb1 + qc) * s - lo[p2]) / s), qd1 = Math.Clamp(rd1, -dmax - 1, dmax);
                if (pass == 0 && (ra != qa || rc != qc || rb0 != qb0 || rb1 != qb1 || rd0 != qd0 || rd1 != qd1)) continue;
                tried++;
                fv[0] = qa; fv[1] = qc; fv[2] = qb0; fv[3] = qb1;
                x.Clear();
                foreach (var t in M11) if ((t.mask >> m & 1) != 0) x[t.x] = fv[t.f] >> t.bit & 1;
                int dm = (1 << db) - 1;
                u[0] = qa & 0xFF;
                u[1] = (qa >> 8 & 1) << 6 | qc & 0x3F | (m & 1) << 7;
                u[2] = qb0 & 0x3F | x[0] << 6 | (m >> 1 & 1) << 7;
                u[3] = qb1 & 0x3F | x[1] << 6 | (m >> 2 & 1) << 7;
                u[4] = qd0 & dm | (maj & 1) << 7;
                u[5] = qd1 & dm | (maj >> 1) << 7;
                if (db < 7) { u[4] |= x[2] << 6; u[5] |= x[3] << 6; }
                if (db < 6) { u[4] |= x[4] << 5; u[5] |= x[5] << 5; }
                best = Try(c, cem, cq, u, nv, lo, hi, best, codes, e);
            }
            // major component 3: plain 8 / 8 / 7 bit values
            u[0] = Math.Clamp((int)MathF.Round(lo[0] / 16), 0, 255); u[1] = Math.Clamp((int)MathF.Round(hi[0] / 16), 0, 255);
            u[2] = Math.Clamp((int)MathF.Round(lo[1] / 16), 0, 255); u[3] = Math.Clamp((int)MathF.Round(hi[1] / 16), 0, 255);
            u[4] = Math.Clamp((int)MathF.Round(lo[2] / 32), 0, 127) | 0x80; u[5] = Math.Clamp((int)MathF.Round(hi[2] / 32), 0, 127) | 0x80;
            best = Try(c, cem, cq, u, nv, lo, hi, best, codes, e);
            return best;
        }

        static float HdrScale(Ctx c, int cq, ReadOnlySpan<float> lo, ReadOnlySpan<float> hi, Span<int> codes, Span<int> e)
        {
            Span<int> u = stackalloc int[4];
            Span<int> x = stackalloc int[7];
            Span<int> fv = stackalloc int[4];
            float best = float.MaxValue;
            float S = Math.Max(0, ((hi[0] - lo[0]) + (hi[1] - lo[1]) + (hi[2] - lo[2])) / 3);
            int argmax = hi[0] >= hi[1] && hi[0] >= hi[2] ? 0 : hi[1] >= hi[2] ? 1 : 2;
            int tried = 0;
            for (int pass = 0; pass < 2 && (tried == 0 || best >= 1e8f); pass++)
            for (int mode = 0; mode < 6 && (tried < 2 || pass == 1); mode++)
            {
                int maj = mode == 5 ? 0 : argmax;
                int p0 = Perm(maj, 0), p1 = Perm(maj, 1), p2 = Perm(maj, 2);
                float s = 1 << M7Shift[mode];
                int qr = Fits((int)MathF.Round(hi[p0] / s), M7Mask[mode, 0]);
                int qg, qb;
                if (mode < 5)
                {
                    qg = Fits((int)MathF.Round((qr * s - hi[p1]) / s), M7Mask[mode, 1]);
                    qb = Fits((int)MathF.Round((qr * s - hi[p2]) / s), M7Mask[mode, 2]);
                }
                else
                {
                    qg = Fits((int)MathF.Round(hi[p1] / s), M7Mask[mode, 1]);
                    qb = Fits((int)MathF.Round(hi[p2] / s), M7Mask[mode, 2]);
                }
                int qs = Fits((int)MathF.Round(S / s), M7Mask[mode, 3]);
                if (pass == 0)
                {
                    // skip submodes whose fields clamp
                    int rr = (int)MathF.Round(hi[p0] / s);
                    int rg = mode < 5 ? (int)MathF.Round((qr * s - hi[p1]) / s) : (int)MathF.Round(hi[p1] / s);
                    int rbb = mode < 5 ? (int)MathF.Round((qr * s - hi[p2]) / s) : (int)MathF.Round(hi[p2] / s);
                    int rs = (int)MathF.Round(S / s);
                    if (rr != qr || rg != qg || rbb != qb || rs != qs) continue;
                }
                tried++;
                fv[0] = qr; fv[1] = qg; fv[2] = qb; fv[3] = qs;
                x.Clear();
                foreach (var t in M7) if ((t.mask >> mode & 1) != 0) x[t.x] = fv[t.f] >> t.bit & 1;
                int modeval = mode < 4 ? maj << 2 | mode : mode == 4 ? 0xC | maj : 0xF;
                u[0] = qr & 0x3F | (modeval & 3) << 6;
                u[1] = qg & 0x1F | x[1] << 5 | x[0] << 6 | (modeval >> 2 & 1) << 7;
                u[2] = qb & 0x1F | x[3] << 5 | x[2] << 6 | (modeval >> 3 & 1) << 7;
                u[3] = qs & 0x1F | x[6] << 5 | x[5] << 6 | x[4] << 7;
                best = Try(c, 7, cq, u, 4, lo, hi, best, codes, e);
            }
            return best;
        }

        // ------------------------------------------------------------------ partitions

        static readonly byte[][] Perms = { Array.Empty<byte>(), new byte[] { 0 }, BuildPerms(2), BuildPerms(3), BuildPerms(4) };

        static byte[] BuildPerms(int n)
        {
            var r = new List<byte>();
            Span<int> a = stackalloc int[4];
            for (int i = 0; i < Pow(n, n); i++)
            {
                int v = i, used = 0;
                bool ok = true;
                for (int j = 0; j < n; j++) { a[j] = v % n; v /= n; if ((used >> a[j] & 1) != 0) ok = false; used |= 1 << a[j]; }
                if (ok) for (int j = 0; j < n; j++) r.Add((byte)a[j]);
            }
            return r.ToArray();
        }

        /// <summary>K-means clusters of the texels, then every partition pattern scored by its agreement with them; the best
        /// <paramref name="tries"/> indices go to TopIdx. Returns their number.</summary>
        static unsafe int RankPartitions(Ctx c, int P, int tries)
        {
            int n = c.N;
            var XV = c.XV;
            // inactive channels (alpha of opaque blocks) are constant and do not disturb the distances
            Vector4 mean = Vector4.Zero;
            for (int t = 0; t < n; t++) mean += XV[t];
            mean /= n;
            // seeding: 2 clusters split by the principal axis of the 1 partition fit; else the farthest point from the
            // mean, then farthest from the nearest centre
            Span<float> dist = stackalloc float[MaxN];
            Span<Vector4> cen = stackalloc Vector4[4];
            var label = c.Label;
            Span<Vector4> sum = stackalloc Vector4[4];
            Span<int> cnt = stackalloc int[4];
            if (P == 2 && c.Prm.KMeansIters == 0)
            {
                sum.Clear(); cnt.Clear();
                for (int t = 0; t < n; t++)
                {
                    int j = Vector4.Dot(XV[t] - c.Mean1, c.Axis1) > 0 ? 1 : 0;
                    cnt[j]++; sum[j] += XV[t];
                }
                for (int j = 0; j < 2; j++) cen[j] = cnt[j] > 0 ? sum[j] / cnt[j] : mean;
            }
            else
            for (int t = 0; t < n; t++) dist[t] = Vector4.DistanceSquared(XV[t], mean);
            for (int j = 0; j < P && !(P == 2 && c.Prm.KMeansIters == 0); j++)
            {
                int far = 0;
                for (int t = 1; t < n; t++) if (dist[t] > dist[far]) far = t;
                cen[j] = XV[far];
                if (j == P - 1) break;
                for (int t = 0; t < n; t++)
                {
                    float d = Vector4.DistanceSquared(XV[t], cen[j]);
                    dist[t] = j == 0 ? d : Math.Min(dist[t], d);
                }
            }
            for (int it = 0; ; it++)
            {
                for (int t = 0; t < n; t++)
                {
                    Vector4 x = XV[t];
                    float bd = Vector4.DistanceSquared(x, cen[0]);
                    int bj = 0;
                    for (int j = 1; j < P; j++)
                    {
                        float d = Vector4.DistanceSquared(x, cen[j]);
                        if (d < bd) { bd = d; bj = j; }
                    }
                    label[t] = (byte)bj;
                }
                if (it == c.Prm.KMeansIters) break;
                sum.Clear(); cnt.Clear();
                for (int t = 0; t < n; t++) { int j = label[t]; cnt[j]++; sum[j] += XV[t]; }
                for (int j = 0; j < P; j++) if (cnt[j] > 0) cen[j] = sum[j] / cnt[j];
            }

            var ps = c.F.Parts[P];
            int words = ps.Words;
            Span<ulong> km = stackalloc ulong[16];
            km.Clear();
            for (int t = 0; t < n; t++) km[label[t] * words + (t >> 6)] |= 1UL << (t & 63);
            int found = 0;
            tries = Math.Min(tries, c.TopIdx.Length);
            if (P == 2 && n == 16 && tries <= 2)
            {
                // 16 texels: the best two patterns of every 2 cluster labelling are tabulated
                uint r = P2Best(c.F, (int)km[0] & 0xFFFF);
                c.TopIdx[0] = (int)(r & 0xFFFF);
                if (tries == 1 || (r >> 16) == (r & 0xFFFF)) return 1;
                c.TopIdx[1] = (int)(r >> 16);
                return 2;
            }
            int floor = -1;   // the score a pattern must beat to enter the list once it is full
            fixed (ulong* masks = ps.Masks)
            {
                if (words == 1)
                {
                    ulong k0 = km[0], k1 = km[1], k2 = km[2], k3 = km[3];
                    for (int i = 0; i < ps.Count; i++)
                    {
                        int score;
                        if (P == 2)
                        {
                            int x = BitOperations.PopCount(masks[i * 2] ^ k0);
                            score = Math.Max(x, n - x);
                        }
                        else if (P == 3)
                        {
                            ulong* q = masks + i * 3;
                            ulong a0 = q[0], a1 = q[1], a2 = q[2];
                            int m00 = BitOperations.PopCount(a0 & k0), m01 = BitOperations.PopCount(a0 & k1), m02 = BitOperations.PopCount(a0 & k2);
                            int m10 = BitOperations.PopCount(a1 & k0), m11 = BitOperations.PopCount(a1 & k1), m12 = BitOperations.PopCount(a1 & k2);
                            int m20 = BitOperations.PopCount(a2 & k0), m21 = BitOperations.PopCount(a2 & k1), m22 = BitOperations.PopCount(a2 & k2);
                            score = Math.Max(Math.Max(m00 + m11 + m22, m00 + m12 + m21), Math.Max(Math.Max(m01 + m10 + m22, m01 + m12 + m20),
                                Math.Max(m02 + m10 + m21, m02 + m11 + m20)));
                        }
                        else
                        {
                            // best cluster per partition (an upper bound of the best assignment)
                            ulong* q = masks + i * 4;
                            score = 0;
                            for (int a = 0; a < 4; a++)
                            {
                                ulong pm = q[a];
                                score += Math.Max(Math.Max(BitOperations.PopCount(pm & k0), BitOperations.PopCount(pm & k1)),
                                    Math.Max(BitOperations.PopCount(pm & k2), BitOperations.PopCount(pm & k3)));
                            }
                        }
                        if (score > floor) Insert(c, ref found, tries, ref floor, score, i);
                    }
                }
                else
                {
                    Span<int> m = stackalloc int[16];
                    var perms = Perms[P];
                    int nperm = perms.Length / P;
                    for (int i = 0; i < ps.Count; i++)
                    {
                        ulong* bse = masks + i * P * words;
                        int score = 0;
                        if (P == 2)
                        {
                            int x = 0;
                            for (int w = 0; w < words; w++) x += BitOperations.PopCount(bse[w] ^ km[w]);
                            score = Math.Max(x, n - x);
                        }
                        else
                        {
                            for (int a = 0; a < P; a++)
                                for (int b = 0; b < P; b++)
                                {
                                    int s = 0;
                                    for (int w = 0; w < words; w++) s += BitOperations.PopCount(bse[a * words + w] & km[b * words + w]);
                                    m[a * 4 + b] = s;
                                }
                            for (int q = 0; q < nperm; q++)
                            {
                                int s = 0;
                                for (int a = 0; a < P; a++) s += m[a * 4 + perms[q * P + a]];
                                if (s > score) score = s;
                            }
                        }
                        if (score > floor) Insert(c, ref found, tries, ref floor, score, i);
                    }
                }
            }
            return found;
        }

        /// <summary>For 16 texel footprints: per 2 cluster labelling (bit mask), the indices of the two best matching 2
        /// partition patterns (computed on first use).</summary>
        static uint P2Best(Footprint f, int m)
        {
            var t = f.P2Best;
            if (t == null)
            {
                t = new uint[65536];
                Array.Fill(t, uint.MaxValue);
                f.P2Best = t;
            }
            uint r = t[m];
            if (r != uint.MaxValue) return r;
            var ps = f.Parts[2];
            int b0 = -1, b1 = -1, i0 = 0, i1 = 0;
            for (int i = 0; i < ps.Count; i++)
            {
                int x = BitOperations.PopCount(((uint)ps.Masks[i * 2] ^ (uint)m) & 0xFFFF), sc = Math.Max(x, 16 - x);
                if (sc > b0) { b1 = b0; i1 = i0; b0 = sc; i0 = i; }
                else if (sc > b1) { b1 = sc; i1 = i; }
            }
            r = (uint)i0 | (uint)(b1 < 0 ? i0 : i1) << 16;
            t[m] = r;
            return r;
        }

        static void Insert(Ctx c, ref int found, int tries, ref int floor, int score, int idx)
        {
            int j = found < tries ? found++ : found - 1;
            while (j > 0 && c.TopScore[j - 1] < score)
            {
                c.TopScore[j] = c.TopScore[j - 1]; c.TopIdx[j] = c.TopIdx[j - 1];
                j--;
            }
            c.TopScore[j] = score; c.TopIdx[j] = idx;
            if (found == tries) floor = c.TopScore[found - 1];
        }

        // ------------------------------------------------------------------ packing

        struct Bits
        {
            public ulong Lo, Hi;

            public void Put(ref int pos, int v, int n, int limit)
            {
                int k = Math.Min(n, limit - pos);
                if (k > 0)
                {
                    ulong b = (ulong)v & ((1UL << k) - 1);
                    if (pos >= 64) Hi |= b << (pos - 64);
                    else
                    {
                        Lo |= b << pos;
                        if (pos + k > 64) Hi |= b >> (64 - pos);
                    }
                }
                pos += n;
            }
        }

        static void WriteIse(ref Bits bits, int start, int q, ReadOnlySpan<int> v, int count)
        {
            int n = QBits[q], pos = start, limit = start + AstcBlocks.IseBits(q, count), mask = (1 << n) - 1;
            Span<int> mv = stackalloc int[5];
            switch (QKind[q])
            {
                case 0:
                    for (int i = 0; i < count; i++) bits.Put(ref pos, v[i], n, limit);
                    break;
                case 1:
                    for (int i = 0; i < count; i += 5)
                    {
                        int r = Math.Min(5, count - i), idx = 0;
                        for (int j = r - 1; j >= 0; j--) idx = idx * 3 + (v[i + j] >> n);
                        int T = TritEnc[r - 1][idx];
                        for (int j = 0; j < 5; j++) mv[j] = j < r ? v[i + j] & mask : 0;
                        bits.Put(ref pos, mv[0], n, limit); bits.Put(ref pos, T, 2, limit);
                        bits.Put(ref pos, mv[1], n, limit); bits.Put(ref pos, T >> 2, 2, limit);
                        bits.Put(ref pos, mv[2], n, limit); bits.Put(ref pos, T >> 4, 1, limit);
                        bits.Put(ref pos, mv[3], n, limit); bits.Put(ref pos, T >> 5, 2, limit);
                        bits.Put(ref pos, mv[4], n, limit); bits.Put(ref pos, T >> 7, 1, limit);
                    }
                    break;
                default:
                    for (int i = 0; i < count; i += 3)
                    {
                        int r = Math.Min(3, count - i), idx = 0;
                        for (int j = r - 1; j >= 0; j--) idx = idx * 5 + (v[i + j] >> n);
                        int Q = QuintEnc[r - 1][idx];
                        for (int j = 0; j < 3; j++) mv[j] = j < r ? v[i + j] & mask : 0;
                        bits.Put(ref pos, mv[0], n, limit); bits.Put(ref pos, Q, 3, limit);
                        bits.Put(ref pos, mv[1], n, limit); bits.Put(ref pos, Q >> 3, 2, limit);
                        bits.Put(ref pos, mv[2], n, limit); bits.Put(ref pos, Q >> 5, 2, limit);
                    }
                    break;
            }
        }

        static void Pack(Ctx c, in BlockMode m, int cem, int cq, Span<byte> o)
        {
            var bits = new Bits();
            int P = c.P;
            bits.Lo = (uint)m.Bits | (uint)(P - 1) << 11;
            int start;
            if (P == 1) { bits.Lo |= (ulong)cem << 13; start = 17; }
            else { bits.Lo |= (ulong)c.Seed << 13 | (ulong)cem << 25; start = 29; }
            WriteIse(ref bits, start, cq, c.Codes, P * CemValues(cem));

            Span<int> w = stackalloc int[64];
            int gc = c.F.Grids[m.Grid].Count;
            for (int k = 0; k < gc; k++)
            {
                if (m.Dual) { w[2 * k] = c.WC[k]; w[2 * k + 1] = c.WC[64 + k]; }
                else w[k] = c.WC[k];
            }
            var wb = new Bits();
            WriteIse(ref wb, 0, m.Wq, w, m.WeightCount);
            bits.Hi |= AstcBlocks.Reverse(wb.Lo);
            bits.Lo |= AstcBlocks.Reverse(wb.Hi);
            if (m.Dual)
            {
                int pos = 128 - m.WeightBits - 2;
                bits.Put(ref pos, c.Ccs, 2, 128);
            }
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(o, bits.Lo);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(o.Slice(8), bits.Hi);
        }

        static void VoidExtent(Span<byte> o, bool hdr, int r, int g, int b, int a)
        {
            ulong lo = 0x1FCUL | (hdr ? 1UL << 9 : 0) | 3UL << 10 | ~0UL << 12;
            ulong hi = (ulong)(ushort)r | (ulong)(ushort)g << 16 | (ulong)(ushort)b << 32 | (ulong)(ushort)a << 48;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(o, lo);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(o.Slice(8), hi);
        }

        // ------------------------------------------------------------------ HDR helpers

        /// <summary>Non-negative finite half (negative and NaN to 0, infinity to the largest finite).</summary>
        static ushort CleanHalf(ushort h)
        {
            if ((h & 0x7C00) == 0x7C00) return (h & 0x3FF) != 0 || (h & 0x8000) != 0 ? (ushort)0 : (ushort)0x7BFF;
            return (h & 0x8000) != 0 ? (ushort)0 : h;
        }

        /// <summary>The 16 bit logarithmic interpolation value the decoder maps onto half h (the centre of its interval).</summary>
        static float HalfToLns(ushort h)
        {
            int e = h >> 10, m = h & 0x3FF;
            float mt = m * 8 + 3.5f;
            float mm = mt < 1536 ? mt / 3 : mt < 5632 ? (mt + 512) / 4 : (mt + 2048) / 5;
            return Math.Min(e * 2048 + mm, 65535);
        }
    }
}
