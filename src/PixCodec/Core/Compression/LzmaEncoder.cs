using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace PixCodec.Compression
{
    /// <summary>
    /// LZMA encoder: hash chain match finder (4 byte chains plus one-slot tables for 2 and 3 byte matches), greedy
    /// or lazy parsing with the usual preference for repeated distances, and the LZMA range coder. It writes raw
    /// LZMA1 (for ".lzma" files) or LZMA2 chunks (for .xz). No optimal parsing: the output is typically a few
    /// percent larger than liblzma's at the same dictionary size.
    /// </summary>
    internal sealed unsafe class LzmaEncoder
    {
        // probability layout: identical to LzmaDecoder
        const int IsMatch = 0, IsRep = IsMatch + 192, IsRepG0 = IsRep + 12, IsRepG1 = IsRepG0 + 12, IsRepG2 = IsRepG1 + 12,
            IsRep0Long = IsRepG2 + 12, PosSlot = IsRep0Long + 192, SpecPos = PosSlot + 256, Align = SpecPos + 128,
            LenCoder = Align + 16, RepLenCoder = LenCoder + LenSize, Literal = RepLenCoder + LenSize;
        const int LenSize = 2 + 128 + 128 + 256, LenLow = 2, LenMid = 2 + 128, LenHigh = 2 + 256;
        const int MaxMatch = 273;
        const int ChunkUnpackedMax = 1 << 21, ChunkPackedMax = 1 << 16;
        const int Hash3Bits = 16;
        const int PriceCheckLen = 16; // matches up to this length are compared with the price of literals

        readonly int lc, lp, pb, depth, niceLen;
        readonly bool lazy, useShort, useChain, optimal;
        readonly ushort[] probs;
        uint state, rep0, rep1, rep2, rep3;

        // range coder
        ulong low;
        uint range;
        byte cache;
        long cacheSize;
        byte[] rc = new byte[ChunkPackedMax + 4096];
        int rcPos;

        // match finder
        readonly int[] head4, head3 = new int[1 << Hash3Bits], head2 = new int[1 << 16];
        readonly int hash4Shift;
        readonly int[] chain;
        readonly int chainMask, dictSize;

        public int DictionarySize => dictSize;

        /// <summary>Dictionary size the level uses for an input of this length (a power of two, no larger than needed).</summary>
        public static int DictionarySizeFor(int level, long inputLength)
        {
            level = Math.Clamp(level, 0, 9);
            int maxDict = level <= 3 ? 1 << 20 : level <= 6 ? 1 << 23 : 1 << 25;
            int d = 1 << 12;
            while (d < maxDict && d < inputLength) d <<= 1;
            return d;
        }

        public LzmaEncoder(int level, long inputLength, int lc = 3, int lp = 0, int pb = 2)
        {
            level = Math.Clamp(level, 0, 9);
            this.lc = lc; this.lp = lp; this.pb = pb;
            depth = level switch { <= 1 => 1, 2 => 4, 3 => 12, 4 => 16, 5 => 24, 6 => 48, 7 => 64, 8 => 128, _ => 256 };
            // the fast levels keep to one table (every extra table is a cache miss per position)
            useShort = level >= 5;
            useChain = depth > 1;
            niceLen = level switch { <= 3 => 32, <= 5 => 64, <= 7 => 128, _ => MaxMatch };
            lazy = level >= 4;
            optimal = level >= 7;
            int d = DictionarySizeFor(level, inputLength);
            dictSize = d;
            chain = new int[level <= 1 ? 1 : d];
            // the main hash table is sized to the input (small strips should not pay for megabytes of tables)
            int hashBits = Math.Clamp(BitOperations.Log2((uint)Math.Max(inputLength, 1)) + 1, 12, level <= 2 ? 16 : level <= 4 ? 18 : 20);
            head4 = new int[1 << hashBits];
            hash4Shift = 32 - hashBits;
            chainMask = d - 1;
            probs = new ushort[Literal + (0x300 << (lc + lp))];
            head4.AsSpan().Fill(-1); head3.AsSpan().Fill(-1); head2.AsSpan().Fill(-1);
            ResetState();
        }

        void ResetState()
        {
            state = 0; rep0 = rep1 = rep2 = rep3 = 0;
            probs.AsSpan().Fill(1024);
        }

        // ------------------------------------------------------------------------------------- range coder

        void RcInit() { low = 0; range = 0xFFFFFFFF; cache = 0; cacheSize = 1; rcPos = 0; }

        void ShiftLow()
        {
            if ((uint)low < 0xFF000000 || (low >> 32) != 0)
            {
                byte carry = (byte)(low >> 32);
                byte b = cache;
                do
                {
                    if (rcPos == rc.Length) Array.Resize(ref rc, rc.Length * 2);
                    rc[rcPos++] = (byte)(b + carry);
                    b = 0xFF;
                } while (--cacheSize != 0);
                cache = (byte)(low >> 24);
            }
            cacheSize++;
            low = (low & 0x00FFFFFF) << 8;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Bit(ref ushort prob, uint bit)
        {
            uint p = prob, bound = (range >> 11) * p;
            if (bit == 0) { range = bound; prob = (ushort)(p + ((2048 - p) >> 5)); }
            else { low += bound; range -= bound; prob = (ushort)(p - (p >> 5)); }
            while (range < 1u << 24) { range <<= 8; ShiftLow(); }
        }

        void Direct(uint value, int bits)
        {
            for (int i = bits - 1; i >= 0; i--)
            {
                range >>= 1;
                if (((value >> i) & 1) != 0) low += range;
                while (range < 1u << 24) { range <<= 8; ShiftLow(); }
            }
        }

        void Tree(int offset, int bits, uint value)
        {
            uint m = 1;
            for (int i = bits - 1; i >= 0; i--)
            {
                uint b = (value >> i) & 1;
                Bit(ref probs[offset + m], b);
                m = (m << 1) | b;
            }
        }

        void ReverseTree(int offset, int bits, uint value)
        {
            uint m = 1;
            for (int i = 0; i < bits; i++)
            {
                uint b = value & 1;
                value >>= 1;
                Bit(ref probs[offset + m], b);
                m = (m << 1) | b;
            }
        }

        void Length(int coder, uint len, uint posState)
        {
            if (len < 8) { Bit(ref probs[coder], 0); Tree(coder + LenLow + (int)(posState << 3), 3, len); }
            else if (len < 16) { Bit(ref probs[coder], 1); Bit(ref probs[coder + 1], 0); Tree(coder + LenMid + (int)(posState << 3), 3, len - 8); }
            else { Bit(ref probs[coder], 1); Bit(ref probs[coder + 1], 1); Tree(coder + LenHigh, 8, len - 16); }
        }

        // ----------------------------------------------------------------------------------------- symbols

        void PutLiteral(byte* data, long pos)
        {
            uint posState = (uint)pos & ((1u << pb) - 1);
            Bit(ref probs[IsMatch + (state << 4) + posState], 0);
            uint prev = pos == 0 ? 0u : data[pos - 1];
            int lit = Literal + 0x300 * (int)((((uint)pos & ((1u << lp) - 1)) << lc) + (prev >> (8 - lc)));
            uint b = data[pos], sym = 1;
            int i = 7;
            if (state >= 7)
            {
                uint matchByte = data[pos - rep0 - 1];
                do
                {
                    uint mb = (matchByte >> 7) & 1;
                    matchByte <<= 1;
                    uint bit = (b >> i) & 1;
                    i--;
                    Bit(ref probs[lit + (int)(((1 + mb) << 8) + sym)], bit);
                    sym = (sym << 1) | bit;
                    if (mb != bit) break;
                } while (sym < 0x100);
            }
            while (sym < 0x100)
            {
                uint bit = (b >> i) & 1;
                i--;
                Bit(ref probs[lit + (int)sym], bit);
                sym = (sym << 1) | bit;
            }
            state = state < 4 ? 0 : state < 10 ? state - 3 : state - 6;
        }

        void PutMatch(long pos, uint dist, int len)
        {
            uint posState = (uint)pos & ((1u << pb) - 1);
            Bit(ref probs[IsMatch + (state << 4) + posState], 1);
            Bit(ref probs[IsRep + state], 0);
            Length(LenCoder, (uint)len - 2, posState);
            state = state < 7 ? 7u : 10u;
            uint slot;
            if (dist < 4) slot = dist;
            else
            {
                int hb = BitOperations.Log2(dist);
                slot = (uint)(2 * hb) + ((dist >> (hb - 1)) & 1);
            }
            Tree(PosSlot + (Math.Min(len - 2, 3) << 6), 6, slot);
            if (slot >= 4)
            {
                int direct = (int)(slot >> 1) - 1;
                uint baseValue = (2 | (slot & 1)) << direct;
                uint reduced = dist - baseValue;
                if (slot < 14) ReverseTree(SpecPos + (int)baseValue - (int)slot, direct, reduced);
                else
                {
                    Direct(reduced >> 4, direct - 4);
                    ReverseTree(Align, 4, reduced & 15);
                }
            }
            rep3 = rep2; rep2 = rep1; rep1 = rep0; rep0 = dist;
        }

        void PutRep(long pos, int index, int len)
        {
            uint posState = (uint)pos & ((1u << pb) - 1);
            Bit(ref probs[IsMatch + (state << 4) + posState], 1);
            Bit(ref probs[IsRep + state], 1);
            if (index == 0)
            {
                Bit(ref probs[IsRepG0 + state], 0);
                Bit(ref probs[IsRep0Long + (state << 4) + posState], len == 1 ? 0u : 1u);
                if (len == 1) { state = state < 7 ? 9u : 11u; return; }
            }
            else
            {
                Bit(ref probs[IsRepG0 + state], 1);
                uint d;
                if (index == 1) { Bit(ref probs[IsRepG1 + state], 0); d = rep1; }
                else
                {
                    Bit(ref probs[IsRepG1 + state], 1);
                    Bit(ref probs[IsRepG2 + state], index == 2 ? 0u : 1u);
                    if (index == 2) d = rep2; else { d = rep3; rep3 = rep2; }
                    rep2 = rep1;
                }
                rep1 = rep0; rep0 = d;
            }
            Length(RepLenCoder, (uint)len - 2, posState);
            state = state < 7 ? 8u : 11u;
        }

        // ------------------------------------------------------------------------------------------ prices

        // Cost in 1/16 bit of coding a 0 with probability index p >> 4 (the model adapts, so these are estimates
        // from its current state; they are only used to turn down short matches that cost more than literals).
        static readonly ushort[] PriceTable = MakePrices();

        static ushort[] MakePrices()
        {
            var t = new ushort[128];
            for (int i = 0; i < 128; i++) t[i] = (ushort)Math.Round(-Math.Log2((i * 16 + 8) / 2048.0) * 16);
            return t;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int Price(int index, uint bit) => PriceTable[(bit == 0 ? probs[index] : 2048 - probs[index]) >> 4];

        int TreePrice(int offset, int bits, uint value)
        {
            int price = 0;
            uint m = 1;
            for (int i = bits - 1; i >= 0; i--)
            {
                uint b = (value >> i) & 1;
                price += Price(offset + (int)m, b);
                m = (m << 1) | b;
            }
            return price;
        }

        int LiteralPrice(byte* data, long pos)
        {
            uint posState = (uint)pos & ((1u << pb) - 1);
            uint prev = pos == 0 ? 0u : data[pos - 1];
            int lit = Literal + 0x300 * (int)((((uint)pos & ((1u << lp) - 1)) << lc) + (prev >> (8 - lc)));
            // (taken as following a literal: state 0)
            return Price(IsMatch + (int)posState, 0) + TreePrice(lit, 8, data[pos]);
        }

        int LengthPrice(int coder, uint len, uint posState)
        {
            if (len < 8) return Price(coder, 0) + TreePrice(coder + LenLow + (int)(posState << 3), 3, len);
            if (len < 16) return Price(coder, 1) + Price(coder + 1, 0) + TreePrice(coder + LenMid + (int)(posState << 3), 3, len - 8);
            return Price(coder, 1) + Price(coder + 1, 1) + TreePrice(coder + LenHigh, 8, len - 16);
        }

        int MatchPrice(long pos, uint dist, int len)
        {
            uint posState = (uint)pos & ((1u << pb) - 1);
            int price = Price(IsMatch + (int)((state << 4) + posState), 1) + Price(IsRep + (int)state, 0) + LengthPrice(LenCoder, (uint)len - 2, posState);
            uint slot = dist < 4 ? dist : (uint)(2 * BitOperations.Log2(dist)) + ((dist >> (BitOperations.Log2(dist) - 1)) & 1);
            price += TreePrice(PosSlot + (Math.Min(len - 2, 3) << 6), 6, slot);
            if (slot >= 4) price += 16 * ((int)(slot >> 1) - 1); // the distance's low bits: about a bit each
            return price;
        }

        int RepPrice(long pos, int index, int len)
        {
            uint posState = (uint)pos & ((1u << pb) - 1);
            int price = Price(IsMatch + (int)((state << 4) + posState), 1) + Price(IsRep + (int)state, 1);
            if (index == 0) price += Price(IsRepG0 + (int)state, 0) + Price(IsRep0Long + (int)((state << 4) + posState), 1);
            else price += Price(IsRepG0 + (int)state, 1) + Price(IsRepG1 + (int)state, index == 1 ? 0u : 1u) + (index >= 2 ? Price(IsRepG2 + (int)state, index == 2 ? 0u : 1u) : 0);
            return price + LengthPrice(RepLenCoder, (uint)len - 2, posState);
        }

        // true if `len` literals from pos are estimated cheaper than the given match price
        bool LiteralsCheaper(byte* data, long pos, int len, int matchPrice)
        {
            int price = 0;
            for (int i = 0; i < len; i++)
            {
                price += LiteralPrice(data, pos + i);
                if (price >= matchPrice) return false;
            }
            return true;
        }

        // ------------------------------------------------------------------------------------ match finder

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int MatchLen(byte* a, byte* b, int max)
        {
            int n = 0;
            while (n + 8 <= max)
            {
                ulong x = Unsafe.ReadUnaligned<ulong>(a + n) ^ Unsafe.ReadUnaligned<ulong>(b + n);
                if (x != 0) return n + (BitOperations.TrailingZeroCount(x) >> 3);
                n += 8;
            }
            while (n < max && a[n] == b[n]) n++;
            return n;
        }

        /// <summary>Enters position pos into the tables and returns the longest match found there (0 if none).</summary>
        int Search(byte* data, long pos, long end, out uint dist)
        {
            dist = 0;
            int avail = (int)Math.Min(end - pos, MaxMatch);
            if (avail < 4)
            {
                return 0;
            }
            byte* cur = data + pos;
            uint v = Unsafe.ReadUnaligned<uint>(cur);
            int h4 = (int)((v * 2654435761u) >> hash4Shift);
            int c4 = head4[h4];
            int p = (int)pos;
            head4[h4] = p;
            if (useChain) chain[p & chainMask] = c4;

            int best = 1;
            if (useShort)
            {
                // two and three byte matches are only worth coding when they are close
                int h2 = (int)(v & 0xFFFF), h3 = (int)(((v & 0xFFFFFF) * 2654435761u) >> (32 - Hash3Bits));
                int c2 = head2[h2], c3 = head3[h3];
                head2[h2] = p; head3[h3] = p;
                if (c2 >= 0 && p - c2 <= 0x80 && *(ushort*)(data + c2) == (ushort)v)
                {
                    best = MatchLen(data + c2, cur, avail);
                    dist = (uint)(p - c2 - 1);
                }
                if (c3 >= 0 && c3 != c2 && p - c3 <= 0x4000 && ((Unsafe.ReadUnaligned<uint>(data + c3) ^ v) & 0xFFFFFF) == 0)
                {
                    int n = MatchLen(data + c3, cur, avail);
                    if (n > best) { best = n; dist = (uint)(p - c3 - 1); }
                }
            }
            int nice = Math.Min(niceLen, avail);
            // the chain budget shrinks while positions keep ending up as literals (noisy data)
            for (int steps = Math.Max(depth >> Math.Min(misses >> 4, 5), 1); c4 >= 0 && p - c4 <= dictSize && best < nice && steps > 0; steps--)
            {
                byte* c = data + c4;
                if (c[best] == cur[best] && Unsafe.ReadUnaligned<uint>(c) == v)
                {
                    int n = MatchLen(c, cur, avail);
                    if (n > best) { best = n; dist = (uint)(p - c4 - 1); }
                }
                if (!useChain) break;
                int next = chain[c4 & chainMask];
                if (next >= c4) break; // slot was reused by a newer position
                c4 = next;
            }
            return best < 2 ? 0 : best;
        }

        void Insert(byte* data, long pos, long end)
        {
            if (end - pos < 4) return;
            uint v = Unsafe.ReadUnaligned<uint>(data + pos);
            int p = (int)pos;
            int h4 = (int)((v * 2654435761u) >> hash4Shift);
            if (useShort)
            {
                head2[v & 0xFFFF] = p;
                head3[((v & 0xFFFFFF) * 2654435761u) >> (32 - Hash3Bits)] = p;
            }
            if (useChain) chain[p & chainMask] = head4[h4];
            head4[h4] = p;
        }

        int BestRep(byte* data, long pos, long end, out int index)
        {
            index = 0;
            int best = 0;
            int avail = (int)Math.Min(end - pos, MaxMatch);
            if (avail < 2) return 0;
            for (int i = 0; i < 4; i++)
            {
                uint d = i == 0 ? rep0 : i == 1 ? rep1 : i == 2 ? rep2 : rep3;
                if (d >= pos) continue;
                byte* c = data + pos - d - 1;
                if (*(ushort*)c != *(ushort*)(data + pos)) continue;
                int n = MatchLen(c, data + pos, avail);
                if (n > best) { best = n; index = i; }
            }
            return best;
        }

        // --------------------------------------------------------------------------------------- parsing

        int misses; // literals in a row

        // pending look-ahead result of the lazy parser
        long nextPos = -1;
        int nextLen;
        uint nextDist;

        /// <summary>
        /// Encodes symbols from pos until softEnd is reached, the range coder output exceeds packedLimit or the
        /// input ends. Returns the new position. data[0, pos) is the dictionary.
        /// </summary>
        long Encode(byte* data, long pos, long softEnd, long end, int packedLimit)
        {
            if (optimal)
            {
                // a window can add a few kilobytes at once: stop earlier
                while (pos < softEnd && rcPos + cacheSize < packedLimit - 8192) pos = OptimalWindow(data, pos, softEnd, end);
                return pos;
            }
            while (pos < softEnd && rcPos + cacheSize < packedLimit)
            {
                int mainLen;
                uint mainDist;
                if (nextPos == pos) { mainLen = nextLen; mainDist = nextDist; }
                else mainLen = Search(data, pos, end, out mainDist);
                nextPos = -1;
                // matches may not run past the end of the chunk
                int room = (int)Math.Min(softEnd - pos, MaxMatch);
                if (mainLen > room) mainLen = room;
                if (mainLen == 2 && mainDist >= 0x80) mainLen = 0;
                if (mainLen == 3 && mainDist >= 0x4000) mainLen = 0;
                if (mainLen < 2) mainLen = 0;

                int repLen = BestRep(data, pos, end, out int repIndex);
                if (repLen > room) repLen = room;
                if (mainLen >= 2 && mainLen <= PriceCheckLen && LiteralsCheaper(data, pos, mainLen, MatchPrice(pos, mainDist, mainLen))) mainLen = 0;
                if (repLen >= 2 && repLen <= PriceCheckLen && LiteralsCheaper(data, pos, repLen, RepPrice(pos, repIndex, repLen))) repLen = 0;
                if (repLen >= 2 && (repLen + 1 >= mainLen || repLen + 2 >= mainLen && mainDist >= 1 << 9 || repLen + 3 >= mainLen && mainDist >= 1 << 15))
                {
                    PutRep(pos, repIndex, repLen);
                    misses = 0;
                    for (long q = pos + 1; q < pos + repLen; q++) Insert(data, q, end);
                    pos += repLen;
                    continue;
                }
                if (mainLen >= 2 && lazy && misses < 64 && pos + 1 < softEnd)
                {
                    // would waiting one byte give something clearly better?
                    nextLen = Search(data, pos + 1, end, out nextDist);
                    nextPos = pos + 1;
                    bool defer = nextLen >= mainLen && nextDist < mainDist
                        || nextLen == mainLen + 1 && (nextDist >> 7) <= mainDist
                        || nextLen > mainLen + 1
                        || nextLen + 1 >= mainLen && mainLen >= 3 && (mainDist >> 7) > nextDist;
                    if (defer) mainLen = 0;
                }
                if (mainLen >= 2)
                {
                    PutMatch(pos, mainDist, mainLen);
                    misses = 0;
                    for (long q = (nextPos == pos + 1 ? pos + 2 : pos + 1); q < pos + mainLen; q++) Insert(data, q, end);
                    nextPos = -1;
                    pos += mainLen;
                    continue;
                }
                // literal, or a one byte repeat of the last distance when that byte is the same
                if (rep0 < pos && state >= 7 && data[pos] == data[pos - rep0 - 1]) PutRep(pos, 0, 1);
                else PutLiteral(data, pos);
                misses++;
                pos++;
            }
            return pos;
        }

        // ------------------------------------------------------------------- optimal parsing (levels >= 7)

        const int OptSize = 1024;

        // A position inside the window: cheapest known price to get there, and how (Back: -1 literal, -2 one
        // byte repeat, 0..3 repeat of that distance, >= 4 match with distance Back - 4), plus the coder state there.
        struct Node
        {
            public uint Price;
            public int Prev, Back;
            public uint State, R0, R1, R2, R3;
        }

        readonly Node[] opt = new Node[OptSize + 2];
        readonly int[] pairLen = new int[300];
        readonly uint[] pairDist = new uint[300];
        readonly int[] lenPrices = new int[2 * 16 * 272], slotPrices = new int[4 * 64], alignPrices = new int[16];
        readonly int[] pathBack = new int[OptSize + 2], pathLen = new int[OptSize + 2];
        int pricesAge = int.MaxValue;

        int ReverseTreePrice(int offset, int bits, uint value)
        {
            int price = 0;
            uint m = 1;
            for (int i = 0; i < bits; i++)
            {
                uint b = value & 1;
                value >>= 1;
                price += Price(offset + (int)m, b);
                m = (m << 1) | b;
            }
            return price;
        }

        // Tables for the prices that are looked up most (the model moves slowly; they are refreshed now and then).
        void FillPrices()
        {
            int posStates = 1 << pb;
            Span<int> high = stackalloc int[256];
            for (int c = 0; c < 2; c++)
            {
                int coder = c == 0 ? LenCoder : RepLenCoder;
                // lengths 16 and up do not depend on the position state: price their tree once
                int highBase = Price(coder, 1) + Price(coder + 1, 1);
                for (uint l = 0; l < 256; l++) high[(int)l] = highBase + TreePrice(coder + LenHigh, 8, l);
                for (uint ps = 0; ps < posStates; ps++)
                {
                    int row = (c * 16 + (int)ps) * 272;
                    for (uint l = 0; l < 16; l++) lenPrices[row + (int)l] = LengthPrice(coder, l, ps);
                    high.CopyTo(lenPrices.AsSpan(row + 16, 256));
                }
            }
            for (int ls = 0; ls < 4; ls++)
                for (uint slot = 0; slot < 64; slot++) slotPrices[ls * 64 + (int)slot] = TreePrice(PosSlot + (ls << 6), 6, slot);
            for (uint a = 0; a < 16; a++) alignPrices[a] = ReverseTreePrice(Align, 4, a);
            pricesAge = 0;
        }

        int DistancePrice(uint dist, int lenState)
        {
            if (dist < 4) return slotPrices[lenState * 64 + (int)dist];
            int hb = BitOperations.Log2(dist);
            uint slot = (uint)(2 * hb) + ((dist >> (hb - 1)) & 1);
            int direct = (int)(slot >> 1) - 1;
            uint baseValue = (2 | (slot & 1)) << direct;
            int price = slotPrices[lenState * 64 + (int)slot];
            if (slot < 14) return price + ReverseTreePrice(SpecPos + (int)baseValue - (int)slot, direct, dist - baseValue);
            return price + 16 * (direct - 4) + alignPrices[dist & 15];
        }

        int LiteralPriceAt(byte* data, long pos, uint st, uint r0)
        {
            uint posState = (uint)pos & ((1u << pb) - 1);
            uint prev = pos == 0 ? 0u : data[pos - 1];
            int lit = Literal + 0x300 * (int)((((uint)pos & ((1u << lp) - 1)) << lc) + (prev >> (8 - lc)));
            int price = Price(IsMatch + (int)((st << 4) + posState), 0);
            uint b = data[pos], sym = 1;
            int i = 7;
            if (st >= 7)
            {
                uint matchByte = data[pos - r0 - 1];
                do
                {
                    uint mb = (matchByte >> 7) & 1;
                    matchByte <<= 1;
                    uint bit = (b >> i) & 1;
                    i--;
                    price += Price(lit + (int)(((1 + mb) << 8) + sym), bit);
                    sym = (sym << 1) | bit;
                    if (mb != bit) break;
                } while (sym < 0x100);
            }
            while (sym < 0x100)
            {
                uint bit = (b >> i) & 1;
                i--;
                price += Price(lit + (int)sym, bit);
                sym = (sym << 1) | bit;
            }
            return price;
        }

        /// <summary>All matches at pos with strictly rising length (nearest first for each length); the position is indexed.</summary>
        int SearchAll(byte* data, long pos, long end, int limit)
        {
            int avail = (int)Math.Min(end - pos, MaxMatch);
            if (avail < 4) return 0;
            byte* cur = data + pos;
            uint v = Unsafe.ReadUnaligned<uint>(cur);
            int h4 = (int)((v * 2654435761u) >> hash4Shift);
            int c4 = head4[h4];
            int p = (int)pos;
            head4[h4] = p;
            chain[p & chainMask] = c4;
            int h2 = (int)(v & 0xFFFF), h3 = (int)(((v & 0xFFFFFF) * 2654435761u) >> (32 - Hash3Bits));
            int c2 = head2[h2], c3 = head3[h3];
            head2[h2] = p; head3[h3] = p;
            int count = 0, best = 1;
            if (c2 >= 0 && p - c2 <= dictSize && *(ushort*)(data + c2) == (ushort)v)
            {
                best = MatchLen(data + c2, cur, avail);
                pairLen[count] = best; pairDist[count++] = (uint)(p - c2 - 1);
            }
            if (c3 >= 0 && c3 != c2 && p - c3 <= dictSize && ((Unsafe.ReadUnaligned<uint>(data + c3) ^ v) & 0xFFFFFF) == 0)
            {
                int n = MatchLen(data + c3, cur, avail);
                if (n > best) { best = n; pairLen[count] = n; pairDist[count++] = (uint)(p - c3 - 1); }
            }
            int nice = Math.Min(niceLen, avail);
            for (int steps = depth; c4 >= 0 && p - c4 <= dictSize && best < nice && steps > 0; steps--)
            {
                byte* c = data + c4;
                if (c[best] == cur[best] && Unsafe.ReadUnaligned<uint>(c) == v)
                {
                    int n = MatchLen(c, cur, avail);
                    if (n > best) { best = n; pairLen[count] = n; pairDist[count++] = (uint)(p - c4 - 1); }
                }
                int next = chain[c4 & chainMask];
                if (next >= c4) break;
                c4 = next;
            }
            return count;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Relax(int index, uint price, int prev, int back)
        {
            if (price < opt[index].Price) { opt[index].Price = price; opt[index].Prev = prev; opt[index].Back = back; }
        }

        /// <summary>
        /// Finds the cheapest way (by the model's current prices) through the next stretch of input, treating every
        /// position as a node and every literal, repeat and match as an edge, then encodes that path. A long enough
        /// match ends the stretch at once. Returns the position after it.
        /// </summary>
        long OptimalWindow(byte* data, long pos, long softEnd, long end)
        {
            if (pricesAge >= 1024) FillPrices();
            int room = (int)Math.Min(softEnd - pos, OptSize);
            uint pbMask = (1u << pb) - 1;
            opt[0].Price = 0; opt[0].State = state; opt[0].R0 = rep0; opt[0].R1 = rep1; opt[0].R2 = rep2; opt[0].R3 = rep3;
            for (int k = 1; k <= room; k++) opt[k].Price = uint.MaxValue;
            int lenEnd = 0;
            long indexed = pos - 1; // last position entered into the match finder
            int i = 0;
            for (; ; i++)
            {
                if (i > 0)
                {
                    // the node's coder state follows from the cheapest edge into it
                    ref Node from = ref opt[opt[i].Prev];
                    int back = opt[i].Back;
                    uint st = from.State, r0 = from.R0, r1 = from.R1, r2 = from.R2, r3 = from.R3;
                    if (back == -1) st = st < 4 ? 0 : st < 10 ? st - 3 : st - 6;
                    else if (back == -2) st = st < 7 ? 9u : 11u;
                    else if (back < 4)
                    {
                        if (back == 1) { uint d = r1; r1 = r0; r0 = d; }
                        else if (back == 2) { uint d = r2; r2 = r1; r1 = r0; r0 = d; }
                        else if (back == 3) { uint d = r3; r3 = r2; r2 = r1; r1 = r0; r0 = d; }
                        st = st < 7 ? 8u : 11u;
                    }
                    else { r3 = r2; r2 = r1; r1 = r0; r0 = (uint)(back - 4); st = st < 7 ? 7u : 10u; }
                    opt[i].State = st; opt[i].R0 = r0; opt[i].R1 = r1; opt[i].R2 = r2; opt[i].R3 = r3;
                    if (i == lenEnd || i == room) break;
                }
                long p = pos + i;
                ref Node node = ref opt[i];
                uint here = node.Price, nst = node.State;
                uint posState = (uint)p & pbMask;
                int limit = (int)Math.Min(Math.Min(end - p, MaxMatch), room - i); // longest edge that stays inside
                // literal
                Relax(i + 1, here + (uint)LiteralPriceAt(data, p, nst, node.R0), i, -1);
                uint matchBase = here + (uint)Price(IsMatch + (int)((nst << 4) + posState), 1);
                uint repBase = matchBase + (uint)Price(IsRep + (int)nst, 1);
                // one byte from the last distance
                if (node.R0 < p && data[p] == data[p - node.R0 - 1])
                    Relax(i + 1, repBase + (uint)(Price(IsRepG0 + (int)nst, 0) + Price(IsRep0Long + (int)((nst << 4) + posState), 0)), i, -2);
                if (i + 1 > lenEnd) lenEnd = i + 1;
                int cut = 0, cutBack = 0;
                if (limit >= 2)
                {
                    for (int k = 0; k < 4; k++)
                    {
                        uint d = k == 0 ? node.R0 : k == 1 ? node.R1 : k == 2 ? node.R2 : node.R3;
                        if (d >= p || *(ushort*)(data + p - d - 1) != *(ushort*)(data + p)) continue;
                        int n = MatchLen(data + p - d - 1, data + p, limit);
                        uint kPrice = repBase + (uint)(k == 0
                            ? Price(IsRepG0 + (int)nst, 0) + Price(IsRep0Long + (int)((nst << 4) + posState), 1)
                            : Price(IsRepG0 + (int)nst, 1) + Price(IsRepG1 + (int)nst, k == 1 ? 0u : 1u) + (k >= 2 ? Price(IsRepG2 + (int)nst, k == 2 ? 0u : 1u) : 0));
                        int table = (16 + (int)posState) * 272;
                        for (int l = n >= niceLen ? n : 2; l <= n; l++) Relax(i + l, kPrice + (uint)lenPrices[table + l - 2], i, k);
                        if (i + n > lenEnd) lenEnd = i + n;
                        if (n >= niceLen && n > cut) { cut = n; cutBack = k; }
                    }
                }
                int pairs = SearchAll(data, p, end, limit);
                indexed = p;
                if (limit >= 2 && pairs > 0)
                {
                    uint normalBase = matchBase + (uint)Price(IsRep + (int)nst, 0);
                    int table = (int)posState * 272;
                    int done = 1;
                    for (int k = 0; k < pairs; k++)
                    {
                        int n = Math.Min(pairLen[k], limit);
                        uint d = pairDist[k];
                        // the distance price depends on the length only below five
                        int from = n >= niceLen ? n : Math.Max(done + 1, 2);
                        int distLong = n >= 5 ? DistancePrice(d, 3) : 0;
                        for (int l = from; l <= n; l++)
                        {
                            int dp = l >= 5 ? distLong : DistancePrice(d, l - 2);
                            Relax(i + l, normalBase + (uint)(lenPrices[table + l - 2] + dp), i, (int)d + 4);
                        }
                        if (n > done) done = n;
                        if (i + n > lenEnd) lenEnd = i + n;
                        if (n >= niceLen && n > cut) { cut = n; cutBack = (int)d + 4; }
                    }
                }
                if (cut != 0)
                {
                    // long enough: take it without looking for alternatives inside it
                    opt[i + cut].Prev = i; opt[i + cut].Back = cutBack;
                    lenEnd = i + cut;
                    i = lenEnd;
                    break;
                }
            }
            // collect the path backwards, then encode it front to back
            int steps = 0;
            for (int at = i; at > 0; at = opt[at].Prev) { pathBack[steps] = opt[at].Back; pathLen[steps++] = at - opt[at].Prev; }
            long q = pos;
            for (int k = steps - 1; k >= 0; k--)
            {
                int back = pathBack[k], len = pathLen[k];
                if (back == -1) PutLiteral(data, q);
                else if (back == -2) PutRep(q, 0, 1);
                else if (back < 4) PutRep(q, back, len);
                else PutMatch(q, (uint)(back - 4), len);
                q += len;
            }
            for (long z = indexed + 1; z < q; z++) Insert(data, z, end);
            pricesAge += i;
            return q;
        }

        void RcFlush() { for (int i = 0; i < 5; i++) ShiftLow(); }

        static void Append(ref byte[] output, ref int length, ReadOnlySpan<byte> bytes)
        {
            if (length + (long)bytes.Length > output.Length)
            {
                long want = Math.Max((long)output.Length * 2, length + (long)bytes.Length);
                if (want > Array.MaxLength) want = Array.MaxLength;
                if (length + (long)bytes.Length > want) throw new System.IO.IOException("compressed output too large");
                Array.Resize(ref output, (int)want);
            }
            bytes.CopyTo(output.AsSpan(length));
            length += bytes.Length;
        }

        /// <summary>Appends source as a complete LZMA2 stream (chunks plus the end marker) to output.</summary>
        public void EncodeLzma2(ReadOnlySpan<byte> source, ref byte[] output, ref int length)
        {
            Span<byte> header = stackalloc byte[6];
            bool needDictReset = true, needProps = true, needStateReset = false;
            fixed (byte* data = source)
            {
                long pos = 0, end = source.Length;
                while (pos < end)
                {
                    long chunkEnd = Math.Min(end, pos + ChunkUnpackedMax);
                    RcInit();
                    long stop = Encode(data, pos, chunkEnd, end, ChunkPackedMax - 64);
                    RcFlush();
                    int unpacked = (int)(stop - pos);
                    if (rcPos < unpacked && rcPos <= ChunkPackedMax)
                    {
                        int reset = needDictReset ? 3 : needProps ? 2 : needStateReset ? 1 : 0;
                        header[0] = (byte)(0x80 | reset << 5 | (unpacked - 1) >> 16);
                        header[1] = (byte)((unpacked - 1) >> 8); header[2] = (byte)(unpacked - 1);
                        header[3] = (byte)((rcPos - 1) >> 8); header[4] = (byte)(rcPos - 1);
                        header[5] = (byte)((pb * 5 + lp) * 9 + lc);
                        Append(ref output, ref length, header.Slice(0, reset >= 2 ? 6 : 5));
                        Append(ref output, ref length, rc.AsSpan(0, rcPos));
                        needDictReset = needProps = needStateReset = false;
                    }
                    else
                    {
                        // did not shrink: store the bytes; the decoder's model is then reset, and so is ours
                        for (long p = pos; p < stop;)
                        {
                            int n = (int)Math.Min(stop - p, 1 << 16);
                            header[0] = (byte)(needDictReset ? 1 : 2);
                            header[1] = (byte)((n - 1) >> 8); header[2] = (byte)(n - 1);
                            Append(ref output, ref length, header.Slice(0, 3));
                            Append(ref output, ref length, source.Slice((int)p, n));
                            if (needDictReset) needProps = true;
                            needDictReset = false;
                            p += n;
                        }
                        ResetState();
                        needStateReset = true;
                    }
                    pos = stop;
                }
            }
            header[0] = 0;
            Append(ref output, ref length, header.Slice(0, 1));
        }

        /// <summary>Appends source as one raw LZMA1 stream (no end marker; the size is known to the container).</summary>
        public void EncodeLzma1(ReadOnlySpan<byte> source, ref byte[] output, ref int length)
        {
            fixed (byte* data = source)
            {
                RcInit();
                long pos = 0, end = source.Length;
                while (pos < end)
                {
                    pos = Encode(data, pos, end, end, 1 << 16);
                    Append(ref output, ref length, rc.AsSpan(0, rcPos));
                    rcPos = 0;
                }
                RcFlush();
                Append(ref output, ref length, rc.AsSpan(0, rcPos));
            }
        }

        public byte Properties => (byte)((pb * 5 + lp) * 9 + lc);
    }
}
