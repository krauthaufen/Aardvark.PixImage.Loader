using System;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace PixCodec.Compression
{
    /// <summary>
    /// DEFLATE (RFC 1951) compressor: hash chain match finder (4 byte hash, plus a one-slot 3 byte hash on the
    /// lazy levels), greedy parsing on levels 1..3 and lazy parsing on 4..9, dynamic Huffman blocks that are split
    /// where the symbol statistics change, with fixed and stored blocks used whenever they are smaller.
    /// The same instance serves one-shot compression of a span and the streaming wrapper.
    /// </summary>
    internal sealed unsafe class Deflater
    {
        const int WindowSize = 32768, WindowMask = WindowSize - 1;
        const int MinLookahead = 258 + 8;
        const int NoPos = -(1 << 30);
        const int SymCapacity = 1 << 16, SymLimit = SymCapacity - 300; // one parse step adds fewer than 300 symbols
        const int SplitCheck = 1024, SplitMinSyms = 4096;
        const int Classes = 20;

        // good length, max lazy, nice length, max chain
        static readonly int[] Config =
        {
            0, 0, 0, 0,
            0, 0, 32, 1,       // 1: single probe, greedy
            0, 0, 32, 4,       // 2: greedy
            0, 0, 64, 12,      // 3: greedy
            4, 4, 16, 16,      // 4: lazy from here on
            8, 16, 32, 32,
            8, 16, 128, 128,
            8, 32, 128, 256,
            32, 128, 258, 1024,
            32, 258, 258, 4096,
        };

        static readonly ushort[] LenBase = { 3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258 };
        static readonly byte[] LenExtra = { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0 };
        static readonly byte[] CodeOrder = { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };
        static readonly byte[] LenCode = MakeLenCode();     // match length - 3 -> length symbol - 257
        static readonly ushort[] FixedLitCode = new ushort[288];
        static readonly byte[] FixedLitLen = new byte[288];
        static readonly ushort[] FixedDistCode = new ushort[32];
        static readonly byte[] FixedDistLen = new byte[32];

        static byte[] MakeLenCode()
        {
            var t = new byte[256];
            for (int c = 0; c < 28; c++)
                for (int l = LenBase[c]; l < LenBase[c] + (1 << LenExtra[c]) && l <= 258; l++) t[l - 3] = (byte)c;
            t[255] = 28;
            return t;
        }

        static Deflater()
        {
            for (int i = 0; i < 288; i++) FixedLitLen[i] = (byte)(i < 144 ? 8 : i < 256 ? 9 : i < 280 ? 7 : 8);
            for (int i = 0; i < 32; i++) FixedDistLen[i] = 5;
            fixed (byte* l = FixedLitLen, d = FixedDistLen)
            fixed (ushort* lc = FixedLitCode, dc = FixedDistCode)
            {
                MakeCodes(l, 288, lc);
                MakeCodes(d, 32, dc);
            }
        }

        readonly int level, goodLen, lazyMax, niceLen, chainMax, hashBits;
        readonly bool zlib;
        readonly int[] head = Array.Empty<int>();
        readonly int[]? head3;
        readonly ushort[] prev = Array.Empty<ushort>();
        readonly uint[] syms = Array.Empty<uint>();
        readonly uint[] litFreq = new uint[288], distFreq = new uint[32];
        readonly uint[] classPrev = new uint[Classes];
        readonly byte[] litLens = new byte[288], distLens = new byte[32];
        readonly ushort[] litCodes = new ushort[288], distCodes = new ushort[32];

        int symCount, pos, blockStart;
        int checkSym, checkPos;
        int misses;

        byte[] outBuf;
        int outPos;
        ulong acc;
        int accBits;

        public Deflater(int level, bool zlibWrapper, int capacityHint = 0)
        {
            this.level = level = Math.Clamp(level, 0, 9);
            zlib = zlibWrapper;
            goodLen = Config[level * 4]; lazyMax = Config[level * 4 + 1]; niceLen = Config[level * 4 + 2]; chainMax = Config[level * 4 + 3];
            outBuf = new byte[Math.Max(capacityHint, 256)];
            if (level > 0)
            {
                hashBits = level <= 3 ? 15 : 16;
                head = new int[1 << hashBits];
                prev = new ushort[WindowSize];
                if (level >= 4) head3 = new int[1 << 15];
                syms = new uint[SymCapacity];
                ResetTables();
            }
            if (zlibWrapper)
            {
                outBuf[0] = 0x78;
                outBuf[1] = level < 2 ? (byte)0x01 : level < 6 ? (byte)0x5E : level == 6 ? (byte)0x9C : (byte)0xDA;
                outPos = 2;
            }
        }

        void ResetTables()
        {
            head.AsSpan().Fill(NoPos);
            head3?.AsSpan().Fill(NoPos);
        }

        public byte[] OutputBuffer => outBuf;
        public int OutputLength => outPos;
        public void ClearOutput() => outPos = 0;
        public int Position => pos;

        // ------------------------------------------------------------------------------------------ bit output

        void Reserve(long bytes)
        {
            long need = outPos + bytes + 16;
            if (need <= outBuf.Length) return;
            if (need > Array.MaxLength) throw new IOException("compressed output too large");
            Array.Resize(ref outBuf, (int)Math.Min(Math.Max(need, (long)outBuf.Length * 2), Array.MaxLength));
        }

        void PutBits(uint value, int bits)
        {
            acc |= (ulong)value << accBits;
            accBits += bits;
            while (accBits >= 8) { outBuf[outPos++] = (byte)acc; acc >>= 8; accBits -= 8; }
        }

        void AlignToByte()
        {
            if (accBits > 0) { outBuf[outPos++] = (byte)acc; acc = 0; accBits = 0; }
        }

        // ------------------------------------------------------------------------------------- Huffman helpers

        static uint Reverse(uint v, int bits)
        {
            v = ((v & 0x5555) << 1) | ((v >> 1) & 0x5555);
            v = ((v & 0x3333) << 2) | ((v >> 2) & 0x3333);
            v = ((v & 0x0F0F) << 4) | ((v >> 4) & 0x0F0F);
            v = ((v & 0x00FF) << 8) | ((v >> 8) & 0x00FF);
            return v >> (16 - bits);
        }

        /// <summary>Canonical codes (already bit reversed for LSB-first output) from code lengths.</summary>
        static void MakeCodes(byte* lens, int n, ushort* codes)
        {
            int* count = stackalloc int[16];
            for (int i = 0; i < 16; i++) count[i] = 0;
            for (int i = 0; i < n; i++) count[lens[i]]++;
            count[0] = 0;
            uint* next = stackalloc uint[16];
            uint code = 0;
            for (int b = 1; b <= 15; b++) { code = (code + (uint)count[b - 1]) << 1; next[b] = code; }
            for (int i = 0; i < n; i++)
                codes[i] = lens[i] == 0 ? (ushort)0 : (ushort)Reverse(next[lens[i]]++, lens[i]);
        }

        /// <summary>
        /// Length limited Huffman code lengths. At least two symbols get a code so that the code is complete
        /// (a lone symbol would give an incomplete code that some decoders reject).
        /// </summary>
        internal static void BuildLengths(uint* freq, int n, int maxBits, byte* lens)
        {
            ulong* sorted = stackalloc ulong[n + 2];
            int m = 0;
            for (int i = 0; i < n; i++)
            {
                lens[i] = 0;
                if (freq[i] != 0) sorted[m++] = (ulong)freq[i] << 16 | (uint)i;
            }
            if (m < 2)
            {
                int have = m == 1 ? (int)(sorted[0] & 0xFFFF) : -1;
                for (int i = 0; m < 2; i++)
                    if (i != have) sorted[m++] = 1UL << 16 | (uint)i;
            }
            new Span<ulong>(sorted, m).Sort();
            if (m == 2)
            {
                lens[sorted[0] & 0xFFFF] = 1; lens[sorted[1] & 0xFFFF] = 1;
                return;
            }

            // two queue merge: leaves [0, m) in rising weight, internal nodes [m, 2m-1) are created in rising weight
            ulong* weight = stackalloc ulong[2 * m];
            int* parent = stackalloc int[2 * m];
            for (int i = 0; i < m; i++) weight[i] = sorted[i] >> 16;
            int leaf = 0, inner = m, nodes = m;
            while (nodes < 2 * m - 1)
            {
                int a = leaf < m && (inner >= nodes || weight[leaf] <= weight[inner]) ? leaf++ : inner++;
                int b = leaf < m && (inner >= nodes || weight[leaf] <= weight[inner]) ? leaf++ : inner++;
                weight[nodes] = weight[a] + weight[b];
                parent[a] = nodes; parent[b] = nodes;
                nodes++;
            }
            int* blCount = stackalloc int[maxBits + 1];
            for (int i = 0; i <= maxBits; i++) blCount[i] = 0;
            parent[2 * m - 2] = 0; // parent[] now holds the depth
            for (int i = 2 * m - 3; i >= 0; i--)
            {
                int depth = parent[parent[i]] + 1;
                parent[i] = depth;
                if (i < m) blCount[Math.Min(depth, maxBits)]++;
            }
            // Leaves deeper than maxBits were clamped, which over-subscribes the code. Each repair step turns the
            // deepest leaf above the last level into a node whose children are that leaf and one leaf from the
            // last level; this lowers the Kraft sum by exactly one unit of 2^-maxBits.
            long excess = -(1L << maxBits);
            for (int bits = 1; bits <= maxBits; bits++) excess += (long)blCount[bits] << (maxBits - bits);
            for (; excess > 0; excess--)
            {
                int bits = maxBits - 1;
                while (blCount[bits] == 0) bits--;
                blCount[bits]--; blCount[bits + 1] += 2; blCount[maxBits]--;
            }
            int k = 0;
            for (int bits = maxBits; bits >= 1; bits--)
                for (int c = blCount[bits]; c > 0; c--) lens[sorted[k++] & 0xFFFF] = (byte)bits;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int DistCode(int dist)
        {
            uint d = (uint)dist - 1;
            if (d < 4) return (int)d;
            int hb = BitOperations.Log2(d);
            return 2 * hb + (int)((d >> (hb - 1)) & 1);
        }

        // ------------------------------------------------------------------------------------------ block output

        void EmitStored(byte* data, int length, bool final)
        {
            do
            {
                int n = Math.Min(length, 65535);
                Reserve(n + 8);
                PutBits(final && n == length ? 1u : 0u, 3);
                AlignToByte();
                outBuf[outPos++] = (byte)n; outBuf[outPos++] = (byte)(n >> 8);
                outBuf[outPos++] = (byte)~n; outBuf[outPos++] = (byte)(~n >> 8);
                new ReadOnlySpan<byte>(data, n).CopyTo(outBuf.AsSpan(outPos));
                outPos += n; data += n; length -= n;
            } while (length > 0);
        }

        /// <summary>Writes symbols [0, count) covering window[blockStart, dataEnd) as one block and removes them.</summary>
        void EmitBlock(byte* window, int count, int dataEnd, bool final)
        {
            // the symbols after `count` stay for the next block: take their statistics out first
            fixed (uint* sy = syms, lf = litFreq, df = distFreq)
            fixed (byte* ll = litLens, dl = distLens, lenCode = LenCode)
            fixed (ushort* lc = litCodes, dc = distCodes)
            {
                for (int i = count; i < symCount; i++)
                {
                    uint s = sy[i];
                    if ((int)s >= 0) lf[s]--;
                    else { lf[257 + lenCode[s & 0xFF]]--; df[DistCode((int)((s >> 8) & 0xFFFF))]--; }
                }
                lf[256] = 1;
                BuildLengths(lf, 286, 15, ll);
                BuildLengths(df, 30, 15, dl);

                long extraBits = 0, dynBits = 0, fixedBits = 0;
                for (int i = 0; i < 286; i++)
                {
                    dynBits += (long)lf[i] * ll[i]; fixedBits += (long)lf[i] * FixedLitLen[i];
                    if (i >= 265 && i < 285) extraBits += (long)lf[i] * LenExtra[i - 257];
                }
                for (int i = 0; i < 30; i++)
                {
                    dynBits += (long)df[i] * dl[i]; fixedBits += (long)df[i] * 5;
                    if (i >= 4) extraBits += (long)df[i] * ((i >> 1) - 1);
                }

                // code length code
                int hlit = 286, hdist = 30;
                while (hlit > 257 && ll[hlit - 1] == 0) hlit--;
                while (hdist > 1 && dl[hdist - 1] == 0) hdist--;
                byte* all = stackalloc byte[320];
                for (int i = 0; i < hlit; i++) all[i] = ll[i];
                for (int i = 0; i < hdist; i++) all[hlit + i] = dl[i];
                int total = hlit + hdist;
                byte* tokSym = stackalloc byte[320];
                byte* tokExtra = stackalloc byte[320];
                uint* clFreq = stackalloc uint[19];
                for (int i = 0; i < 19; i++) clFreq[i] = 0;
                int tokens = 0;
                for (int i = 0; i < total;)
                {
                    byte v = all[i];
                    int run = 1;
                    while (i + run < total && all[i + run] == v) run++;
                    i += run;
                    if (v == 0)
                    {
                        while (run >= 11) { int r = Math.Min(run, 138); tokSym[tokens] = 18; tokExtra[tokens++] = (byte)(r - 11); run -= r; }
                        if (run >= 3) { tokSym[tokens] = 17; tokExtra[tokens++] = (byte)(run - 3); run = 0; }
                    }
                    else
                    {
                        tokSym[tokens] = v; tokExtra[tokens++] = 0; run--;
                        while (run >= 3) { int r = Math.Min(run, 6); tokSym[tokens] = 16; tokExtra[tokens++] = (byte)(r - 3); run -= r; }
                    }
                    for (; run > 0; run--) { tokSym[tokens] = v; tokExtra[tokens++] = 0; }
                }
                for (int i = 0; i < tokens; i++) clFreq[tokSym[i]]++;
                byte* clLens = stackalloc byte[19];
                ushort* clCodes = stackalloc ushort[19];
                BuildLengths(clFreq, 19, 7, clLens);
                MakeCodes(clLens, 19, clCodes);
                int hclen = 19;
                while (hclen > 4 && clLens[CodeOrder[hclen - 1]] == 0) hclen--;
                long headerBits = 14 + 3 * hclen;
                for (int i = 0; i < 19; i++)
                    headerBits += (long)clFreq[i] * (clLens[i] + (i == 16 ? 2 : i == 17 ? 3 : i == 18 ? 7 : 0));

                dynBits += 3 + headerBits + extraBits;
                fixedBits += 3 + extraBits;
                int rawLen = dataEnd - blockStart;
                long storedBits = ((accBits + 3 + 7) & ~7) - accBits + 32 + 8L * rawLen + (rawLen / 65535) * 40L;

                if (storedBits <= dynBits && storedBits <= fixedBits)
                    EmitStored(window + blockStart, rawLen, final);
                else
                {
                    bool useFixed = fixedBits <= dynBits;
                    Reserve((Math.Min(dynBits, fixedBits) >> 3) + 16);
                    if (useFixed) PutBits(final ? 3u : 2u, 3);
                    else
                    {
                        PutBits(final ? 5u : 4u, 3);
                        PutBits((uint)(hlit - 257), 5); PutBits((uint)(hdist - 1), 5); PutBits((uint)(hclen - 4), 4);
                        for (int i = 0; i < hclen; i++) PutBits(clLens[CodeOrder[i]], 3);
                        for (int i = 0; i < tokens; i++)
                        {
                            int s = tokSym[i];
                            PutBits(clCodes[s], clLens[s]);
                            if (s >= 16) PutBits(tokExtra[i], s == 16 ? 2 : s == 17 ? 3 : 7);
                        }
                        MakeCodes(ll, 286, lc);
                        MakeCodes(dl, 30, dc);
                    }
                    fixed (byte* fll = FixedLitLen, fdl = FixedDistLen, lenExtra = LenExtra)
                    fixed (ushort* flc = FixedLitCode, fdc = FixedDistCode, lenBase = LenBase)
                    fixed (byte* ob = outBuf)
                    {
                        byte* lLen = useFixed ? fll : ll, dLen = useFixed ? fdl : dl;
                        ushort* lCode = useFixed ? flc : lc, dCode = useFixed ? fdc : dc;
                        byte* o = ob + outPos;
                        ulong a = acc;
                        int bits = accBits;
                        for (int i = 0; i < count; i++)
                        {
                            uint s = sy[i];
                            if ((int)s >= 0)
                            {
                                a |= (ulong)lCode[s] << bits; bits += lLen[s];
                            }
                            else
                            {
                                int l = (int)(s & 0xFF), dist = (int)((s >> 8) & 0xFFFF);
                                int lcode = lenCode[l];
                                a |= (ulong)lCode[257 + lcode] << bits; bits += lLen[257 + lcode];
                                a |= (ulong)(uint)(l + 3 - lenBase[lcode]) << bits; bits += lenExtra[lcode];
                                uint d = (uint)dist - 1;
                                int dcode = DistCode(dist);
                                a |= (ulong)dCode[dcode] << bits; bits += dLen[dcode];
                                if (dcode >= 4)
                                {
                                    int eb = (dcode >> 1) - 1;
                                    a |= (ulong)(d & ((1u << eb) - 1)) << bits; bits += eb;
                                }
                            }
                            // at most 48 bits were added to fewer than 8 pending ones
                            Unsafe.WriteUnaligned(o, a);
                            o += bits >> 3;
                            a >>= bits & ~7;
                            bits &= 7;
                        }
                        a |= (ulong)lCode[256] << bits; bits += lLen[256];
                        Unsafe.WriteUnaligned(o, a);
                        o += bits >> 3;
                        a >>= bits & ~7;
                        bits &= 7;
                        outPos = (int)(o - ob);
                        acc = a; accBits = bits;
                    }
                }

                // keep the remaining symbols; lf/df already hold exactly their statistics
                int rest = symCount - count;
                for (int i = 0; i < 286; i++) lf[i] = 0;
                for (int i = 0; i < 30; i++) df[i] = 0;
                for (int i = 0; i < rest; i++)
                {
                    uint s = sy[count + i];
                    sy[i] = s;
                    if ((int)s >= 0) lf[s]++;
                    else { lf[257 + lenCode[s & 0xFF]]++; df[DistCode((int)((s >> 8) & 0xFFFF))]++; }
                }
                symCount = rest;
                blockStart = dataEnd;
                checkSym = 0; checkPos = dataEnd;
                classPrev.AsSpan().Clear();
            }
        }

        // literals by high nibble, match lengths in three groups, plus the match count per distance magnitude
        void ClassSums(uint* cls)
        {
            for (int i = 0; i < Classes; i++) cls[i] = 0;
            for (int i = 0; i < 256; i++) cls[i >> 4] += litFreq[i];
            for (int i = 257; i < 286; i++) cls[i < 261 ? 16 : i < 269 ? 17 : 18] += litFreq[i];
        }

        /// <summary>
        /// Looks at the symbols added since the last check; when their distribution differs clearly from the block
        /// so far, the block is ended at the last check point. Returns true if a block was written.
        /// </summary>
        bool CheckSplit(byte* window, int p)
        {
            uint* cur = stackalloc uint[Classes];
            ClassSums(cur);
            uint oldTotal = 0, newTotal = 0;
            for (int i = 0; i < Classes; i++) { oldTotal += classPrev[i]; newTotal += cur[i] - classPrev[i]; }
            if (oldTotal >= SplitMinSyms && newTotal > 0)
            {
                // total variation between the two distributions, scaled by oldTotal * newTotal
                ulong diff = 0;
                for (int i = 0; i < Classes; i++)
                {
                    long x = (long)(cur[i] - classPrev[i]) * oldTotal - (long)classPrev[i] * newTotal;
                    diff += (ulong)Math.Abs(x);
                }
                if (diff * 100 > (ulong)oldTotal * newTotal * 28)
                {
                    EmitBlock(window, checkSym, checkPos, false);
                    return true;
                }
            }
            for (int i = 0; i < Classes; i++) classPrev[i] = cur[i];
            checkSym = symCount; checkPos = p;
            return false;
        }

        // -------------------------------------------------------------------------------------------- matching

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Extend(byte* a, byte* b, int len, int maxLen)
        {
            while (len + 8 <= maxLen)
            {
                ulong x = Unsafe.ReadUnaligned<ulong>(a + len) ^ Unsafe.ReadUnaligned<ulong>(b + len);
                if (x != 0) return len + (BitOperations.TrailingZeroCount(x) >> 3);
                len += 8;
            }
            while (len < maxLen && a[len] == b[len]) len++;
            return len;
        }

        /// <summary>Walks the hash chain from <paramref name="cand"/>; returns the best length above <paramref name="bestLen"/> (or bestLen).</summary>
        static int Find(byte* w, int p, int cand, ushort* prev, int maxLen, int bestLen, int chain, int nice, ref int bestDist)
        {
            byte* cur = w + p;
            uint first = Unsafe.ReadUnaligned<uint>(cur);
            if (nice > maxLen) nice = maxLen;
            if (bestLen >= maxLen) return bestLen;
            for (; ; )
            {
                uint dist = (uint)(p - cand);
                // at distance 32768 the chain slot is the one position p itself just took
                if (dist >= WindowSize) break;
                byte* c = w + cand;
                if (c[bestLen] == cur[bestLen] && Unsafe.ReadUnaligned<uint>(c) == first)
                {
                    int len = Extend(c, cur, 4, maxLen);
                    if (len > bestLen)
                    {
                        bestLen = len; bestDist = (int)dist;
                        if (len >= nice) break;
                    }
                }
                if (--chain <= 0) break;
                int delta = prev[cand & WindowMask];
                if (delta == 0) break;
                cand -= delta;
            }
            return bestLen;
        }

        /// <summary>
        /// Parses window[pos, end) into symbols, writing blocks as they fill up. Positions before pos back to
        /// 32 KiB are the history. Unless <paramref name="flush"/> is set, the last few hundred bytes are left for
        /// the next call so that matches can run into data that has not arrived yet.
        /// </summary>
        public void Parse(byte* window, int end, bool flush)
        {
            if (level == 0) { pos = end; return; }
            int limit = flush ? end : end - MinLookahead;
            int p = pos;
            int hashShift = 32 - hashBits;
            bool lazy = level >= 4;
            int chainMax = this.chainMax, niceLen = this.niceLen, lazyMax = this.lazyMax, goodLen = this.goodLen;
            int count = symCount;
            int hashEnd = end - 3; // positions below this have four readable bytes
            fixed (int* hd = head, hd3 = head3)
            fixed (ushort* pv = prev)
            fixed (uint* sy = syms, lf = litFreq, df = distFreq)
            fixed (byte* lenCode = LenCode)
            {
                while (p < limit)
                {
                    if (count >= SymLimit || count - checkSym >= SplitCheck)
                    {
                        symCount = count;
                        if (count >= SymLimit) EmitBlock(window, count, p, false);
                        else CheckSplit(window, p);
                        count = symCount;
                    }
                    if (p >= hashEnd)
                    {
                        sy[count++] = window[p]; lf[window[p]]++; p++;
                        continue;
                    }
                    int maxLen = Math.Min(258, end - p);
                    uint v = Unsafe.ReadUnaligned<uint>(window + p);
                    uint h = (v * 2654435761u) >> hashShift;
                    int cand = hd[h];
                    hd[h] = p;
                    uint delta = (uint)(p - cand);
                    pv[p & WindowMask] = delta < WindowSize ? (ushort)delta : (ushort)0;
                    int dist = 0, len = 3;
                    if (delta < WindowSize) len = Find(window, p, cand, pv, maxLen, 3, chainMax, niceLen, ref dist);

                    if (!lazy)
                    {
                        if (len < 4)
                        {
                            sy[count++] = window[p]; lf[window[p]]++;
                            // incompressible stretches: step faster the longer nothing matches
                            int step = Math.Min(1 + (misses++ >> 6), 16);
                            for (int q = p + 1; q < p + step && q < limit; q++) { sy[count++] = window[q]; lf[window[q]]++; }
                            p = Math.Min(p + step, Math.Max(limit, p + 1));
                            continue;
                        }
                        misses = 0;
                        sy[count++] = 0x80000000u | (uint)dist << 8 | (uint)(len - 3);
                        lf[257 + lenCode[len - 3]]++; df[DistCode(dist)]++;
                        int stop = Math.Min(p + len, hashEnd);
                        // long matches are not worth indexing byte by byte on the fast levels
                        int q2 = len <= 32 ? p + 1 : p + len - 8;
                        for (; q2 < stop; q2++)
                        {
                            uint hq = (Unsafe.ReadUnaligned<uint>(window + q2) * 2654435761u) >> hashShift;
                            uint dq = (uint)(q2 - hd[hq]);
                            hd[hq] = q2;
                            pv[q2 & WindowMask] = dq < WindowSize ? (ushort)dq : (ushort)0;
                        }
                        p += len;
                        continue;
                    }

                    // lazy levels: also remember the most recent position of every three byte string
                    uint h3 = ((v & 0xFFFFFF) * 2654435761u) >> 17;
                    int cand3 = hd3[h3];
                    hd3[h3] = p;
                    if (len < 4)
                    {
                        uint d3 = (uint)(p - cand3);
                        if (d3 <= 4096 && ((Unsafe.ReadUnaligned<uint>(window + cand3) ^ v) & 0xFFFFFF) == 0) { len = 3; dist = (int)d3; }
                        else
                        {
                            sy[count++] = window[p]; lf[window[p]]++; p++;
                            continue;
                        }
                    }
                    int inserted = p;
                    while (len < lazyMax && p + 1 < hashEnd)
                    {
                        int q = p + 1;
                        uint vq = Unsafe.ReadUnaligned<uint>(window + q);
                        uint hq = (vq * 2654435761u) >> hashShift;
                        int cq = hd[hq];
                        hd[hq] = q;
                        uint dq = (uint)(q - cq);
                        pv[q & WindowMask] = dq < WindowSize ? (ushort)dq : (ushort)0;
                        hd3[((vq & 0xFFFFFF) * 2654435761u) >> 17] = q;
                        inserted = q;
                        if (dq >= WindowSize) break;
                        int dist2 = 0;
                        int len2 = Find(window, q, cq, pv, Math.Min(258, end - q), len, len >= goodLen ? chainMax >> 2 : chainMax, niceLen, ref dist2);
                        if (len2 <= len) break;
                        sy[count++] = window[p]; lf[window[p]]++;
                        p = q; len = len2; dist = dist2;
                    }
                    sy[count++] = 0x80000000u | (uint)dist << 8 | (uint)(len - 3);
                    lf[257 + lenCode[len - 3]]++; df[DistCode(dist)]++;
                    {
                        int stop = Math.Min(p + len, hashEnd);
                        for (int q = inserted + 1; q < stop; q++)
                        {
                            uint vq = Unsafe.ReadUnaligned<uint>(window + q);
                            uint hq = (vq * 2654435761u) >> hashShift;
                            uint dq = (uint)(q - hd[hq]);
                            hd[hq] = q;
                            pv[q & WindowMask] = dq < WindowSize ? (ushort)dq : (ushort)0;
                            hd3[((vq & 0xFFFFFF) * 2654435761u) >> 17] = q;
                        }
                    }
                    p += len;
                }
            }
            symCount = count;
            pos = p;
        }

        /// <summary>Ends the current block at the parse position.</summary>
        public void EndBlock(byte* window, bool final)
        {
            if (level == 0) { EmitStored(window + blockStart, pos - blockStart, final); blockStart = pos; return; }
            if (symCount == 0 && !final && pos == blockStart) return;
            EmitBlock(window, symCount, pos, final);
        }

        /// <summary>Byte aligns the output with an empty stored block (a zlib "sync flush").</summary>
        public void SyncFlush()
        {
            Reserve(16);
            PutBits(0, 3);
            AlignToByte();
            outBuf[outPos++] = 0; outBuf[outPos++] = 0; outBuf[outPos++] = 0xFF; outBuf[outPos++] = 0xFF;
        }

        public void Finish(uint adler)
        {
            Reserve(16);
            AlignToByte();
            if (zlib)
            {
                outBuf[outPos++] = (byte)(adler >> 24); outBuf[outPos++] = (byte)(adler >> 16);
                outBuf[outPos++] = (byte)(adler >> 8); outBuf[outPos++] = (byte)adler;
            }
        }

        /// <summary>The window was moved down by <paramref name="shift"/> bytes (a multiple of the window size).</summary>
        public void Rebase(int shift)
        {
            pos -= shift; blockStart -= shift; checkPos -= shift;
            if (level == 0) return;
            foreach (ref int x in head.AsSpan()) x = Math.Max(x - shift, NoPos);
            if (head3 != null) foreach (ref int x in head3.AsSpan()) x = Math.Max(x - shift, NoPos);
        }

        public static byte[] Compress(ReadOnlySpan<byte> source, int level, bool zlibWrapper)
        {
            var d = new Deflater(level, zlibWrapper, level == 0 ? source.Length + source.Length / 65535 * 5 + 32 : source.Length / 2 + 64);
            fixed (byte* s = source)
            {
                d.Parse(s, source.Length, true);
                d.EndBlock(s, true);
            }
            d.Finish(zlibWrapper ? Checksums.Adler32(source, 1) : 0);
            return d.outBuf.AsSpan(0, d.outPos).ToArray();
        }
    }

    /// <summary>Write-only stream that deflates into another stream (left open on dispose).</summary>
    internal sealed unsafe class DeflaterStream : Stream
    {
        const int Window = 32768, Chunk = 8 * 32768, Size = 2 * Window + Chunk;

        readonly Stream output;
        readonly Deflater deflater;
        readonly bool zlib;
        readonly byte[] buffer = new byte[Size];
        int fill;
        uint adler = 1;
        bool finished;

        public DeflaterStream(Stream output, int level, bool zlibWrapper)
        {
            this.output = output ?? throw new ArgumentNullException(nameof(output));
            zlib = zlibWrapper;
            deflater = new Deflater(level, zlibWrapper, 1 << 16);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !finished;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Write(new ReadOnlySpan<byte>(buffer, offset, count));

        public override void WriteByte(byte value) => Write(new ReadOnlySpan<byte>(in value));

        public override void Write(ReadOnlySpan<byte> data)
        {
            if (finished) throw new ObjectDisposedException(nameof(DeflaterStream));
            if (zlib) adler = Checksums.Adler32(data, adler);
            while (data.Length > 0)
            {
                if (fill == Size) Drain();
                int n = Math.Min(data.Length, Size - fill);
                data.Slice(0, n).CopyTo(buffer.AsSpan(fill));
                fill += n;
                data = data.Slice(n);
            }
        }

        // The buffer is full: compress what is there (keeping some look-ahead), then slide the window down.
        void Drain()
        {
            fixed (byte* b = buffer)
            {
                deflater.Parse(b, fill, false);
                // a block must not outlive its bytes (it may still be written as a stored block)
                deflater.EndBlock(b, false);
                int shift = (deflater.Position - Window) & ~(Window - 1);
                if (shift > 0)
                {
                    Buffer.MemoryCopy(b + shift, b, Size, fill - shift);
                    fill -= shift;
                    deflater.Rebase(shift);
                }
            }
            WriteOut();
        }

        void WriteOut()
        {
            // only whole bytes are in the buffer; pending bits stay in the deflater
            if (deflater.OutputLength > 0)
            {
                output.Write(deflater.OutputBuffer, 0, deflater.OutputLength);
                deflater.ClearOutput();
            }
        }

        public override void Flush()
        {
            if (finished) return;
            fixed (byte* b = buffer)
            {
                deflater.Parse(b, fill, true);
                deflater.EndBlock(b, false);
            }
            deflater.SyncFlush();
            WriteOut();
            output.Flush();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !finished)
            {
                finished = true;
                fixed (byte* b = buffer)
                {
                    deflater.Parse(b, fill, true);
                    deflater.EndBlock(b, true);
                }
                deflater.Finish(adler);
                WriteOut();
            }
            base.Dispose(disposing);
        }
    }
}
