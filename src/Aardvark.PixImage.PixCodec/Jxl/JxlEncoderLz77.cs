using System;

namespace Aardvark.ImageCodecs.JpegXl
{
    /// <summary>
    /// LZ77 on token streams (D.3.6): runs of a repeated value (distance 1), copies from the row above and, with more
    /// effort, hash chain matches anywhere in the window. Copies replace literal tokens; the decoder reproduces the values.
    /// </summary>
    internal static class JxlLz77
    {
        public const uint MinLength = 3;
        public static readonly JxlHybridUint LenConf = JxlHybridUint.Make(2, 0, 0);
        const int MaxLength = 1 << 18;
        const int Window = 1 << 20;

        public static void Configure(JxlEntropyEncoder e)
        {
            e.Lz77 = true;
            e.LzMinSymbol = 224;
            e.LzMinLength = MinLength;
            e.LzLenConf = LenConf;
        }

        /// <summary>Replaces repeated stretches of the stream by copies; lzCtx is the extra context of the distances.</summary>
        public static void Apply(JxlTokens t, int lzCtx, int distMultiplier, int effort)
        {
            int n = t.Count;
            if (n < 16) return;
            var ctx = t.Ctx; var val = t.Val;
            var outCtx = new int[n + 16]; var outVal = new uint[n + 16];
            int o = 0;
            int[]? head = null, chain = null;
            const int HashBits = 16;
            if (effort >= 2) { head = new int[1 << HashBits]; Array.Fill(head, -1); chain = new int[n]; }
            int minRun = 24, minMatch = 12;
            int i = 0;
            while (i < n)
            {
                int bestLen = 0, bestDist = 0;
                // run of the previous value
                if (i > 0)
                {
                    int l = 0;
                    uint pv = val[i - 1];
                    while (i + l < n && val[i + l] == pv && l < MaxLength) l++;
                    if (l >= minRun) { bestLen = l; bestDist = 1; }
                }
                // the row above
                if (distMultiplier > 1 && i >= distMultiplier)
                {
                    int l = 0;
                    while (i + l < n && val[i + l] == val[i + l - distMultiplier] && l < MaxLength) l++;
                    if (l >= minMatch && l > bestLen + 4) { bestLen = l; bestDist = distMultiplier; }
                }
                if (head != null && i + 4 <= n)
                {
                    int h = Hash(val, i, HashBits);
                    int cand = head[h];
                    int tries = 32;
                    while (cand >= 0 && i - cand <= Window && tries-- > 0)
                    {
                        if (val[cand] == val[i])
                        {
                            int l = 0;
                            while (i + l < n && val[cand + l] == val[i + l] && l < MaxLength) l++;
                            if (l >= minMatch && l > bestLen + 2) { bestLen = l; bestDist = i - cand; }
                        }
                        cand = chain![cand];
                    }
                }
                if (bestLen >= MinLength && bestDist > 0)
                {
                    if (o + 2 > outCtx.Length) { Array.Resize(ref outCtx, outCtx.Length * 2); Array.Resize(ref outVal, outCtx.Length); }
                    outCtx[o] = ctx[i] | JxlTokens.LzFlag; outVal[o] = (uint)(bestLen - (int)MinLength); o++;
                    outCtx[o] = lzCtx; outVal[o] = DistanceCode(bestDist, distMultiplier); o++;
                    if (head != null)
                        for (int k = 0; k < bestLen && i + k + 4 <= n; k += 1) Insert(val, i + k, head, chain!, HashBits);
                    i += bestLen;
                    continue;
                }
                if (head != null && i + 4 <= n) Insert(val, i, head, chain!, HashBits);
                if (o + 1 > outCtx.Length) { Array.Resize(ref outCtx, outCtx.Length * 2); Array.Resize(ref outVal, outCtx.Length); }
                outCtx[o] = ctx[i]; outVal[o] = val[i]; o++;
                i++;
            }
            t.Ctx = outCtx; t.Val = outVal; t.Count = o;
        }

        static int Hash(uint[] v, int i, int bits)
        {
            uint h = v[i] * 0x9E3779B1u ^ v[i + 1] * 0x85EBCA77u ^ v[i + 2] * 0xC2B2AE3Du ^ v[i + 3] * 0x27D4EB2Fu;
            return (int)(h >> (32 - bits));
        }

        static void Insert(uint[] v, int i, int[] head, int[] chain, int bits)
        {
            int h = Hash(v, i, bits);
            chain[i] = head[h];
            head[h] = i;
        }

        /// <summary>The distance symbol: special codes for (0, 1) row above and (1, 0) previous, else distance + 119.</summary>
        static uint DistanceCode(int d, int mul)
        {
            if (mul == 0) return (uint)(d - 1);
            if (d == 1) return 1;
            if (d == mul) return 0;
            return (uint)(d + 119);
        }
    }
}
