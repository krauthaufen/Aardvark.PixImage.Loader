using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace PixCodec.Compression
{
    /// <summary>Copies shared by the LZ decoders.</summary>
    internal static unsafe class LzCopy
    {
        /// <summary>
        /// Overlapping match copy that may write up to 15 bytes past the end of the match
        /// (the caller guarantees that much room after dst + len).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void MatchWild(byte* dst, nuint dist, nuint len)
        {
            byte* src = dst - dist, end = dst + len;
            if (dist >= 16)
            {
                do
                {
                    Unsafe.WriteUnaligned(dst, Unsafe.ReadUnaligned<Vector128<byte>>(src));
                    dst += 16; src += 16;
                } while (dst < end);
            }
            else if (dist >= 8)
            {
                do
                {
                    Unsafe.WriteUnaligned(dst, Unsafe.ReadUnaligned<ulong>(src));
                    dst += 8; src += 8;
                } while (dst < end);
            }
            else if (dist == 1)
            {
                Unsafe.InitBlockUnaligned(dst, *src, (uint)len);
            }
            else
            {
                for (; dst < end; dst++, src++) *dst = *src;
            }
        }

        /// <summary>Exact overlapping match copy.</summary>
        public static void Match(byte* dst, nuint dist, nuint len)
        {
            byte* src = dst - dist;
            if (dist >= len) Buffer.MemoryCopy(src, dst, len, len);
            else if (dist == 1) Unsafe.InitBlockUnaligned(dst, *src, (uint)len);
            else for (nuint i = 0; i < len; i++) dst[i] = src[i];
        }
    }

    /// <summary>Zstandard frame decoder (RFC 8878), written from the format description.</summary>
    internal sealed unsafe class ZstdDecoder
    {
        const int MaxBlock = 128 * 1024;
        const uint Magic = 0xFD2FB528, SkippableMagic = 0x184D2A50;
        const int Slack = 32;

        // One FSE state: the symbol's value base and extra bit count, and how to reach the next state.
        struct Entry
        {
            public ushort NextBase;
            public byte NextBits;
            public byte ExtraBits;
            public uint Value;
        }

        static readonly uint[] LlBase =
        {
            0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 18, 20, 22, 24, 28, 32, 40, 48, 64, 128, 256, 512, 1024, 2048, 4096,
            8192, 16384, 32768, 65536,
        };
        static readonly byte[] LlBits =
        {
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 3, 3, 4, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
        };
        static readonly uint[] MlBase =
        {
            3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34,
            35, 37, 39, 41, 43, 47, 51, 59, 67, 83, 99, 131, 259, 515, 1027, 2051, 4099, 8195, 16387, 32771, 65539,
        };
        static readonly byte[] MlBits =
        {
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            1, 1, 1, 1, 2, 2, 3, 3, 4, 4, 5, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
        };
        static readonly short[] LlDefault =
        {
            4, 3, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 3, 2, 1, 1, 1, 1, 1, -1, -1, -1, -1,
        };
        static readonly short[] MlDefault =
        {
            1, 4, 3, 2, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
            1, 1, 1, 1, 1, 1, -1, -1, -1, -1, -1, -1, -1,
        };
        static readonly short[] OfDefault =
        {
            1, 1, 1, 1, 1, 1, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, -1, -1, -1, -1, -1,
        };

        static readonly Entry[] LlDefaultTable = new Entry[64], MlDefaultTable = new Entry[64], OfDefaultTable = new Entry[32];

        static ZstdDecoder()
        {
            fixed (short* ll = LlDefault, ml = MlDefault, of = OfDefault)
            fixed (Entry* tl = LlDefaultTable, tm = MlDefaultTable, to = OfDefaultTable)
            {
                BuildFse(ll, LlDefault.Length, 6, tl, 0);
                BuildFse(ml, MlDefault.Length, 6, tm, 1);
                BuildFse(of, OfDefault.Length, 5, to, 2);
            }
        }

        readonly Entry[] llTable = new Entry[512], mlTable = new Entry[512], ofTable = new Entry[256];
        readonly Entry[] weightTable = new Entry[64];
        readonly ushort[] huf = new ushort[1 << 11]; // symbol | bits << 8
        readonly byte[] lits = new byte[MaxBlock + Slack];
        int llLog, mlLog, ofLog, hufLog;
        bool llSet, mlSet, ofSet, hufSet;
        uint rep0, rep1, rep2;

        static Exception Bad(string what) => new ImageFormatException("corrupt zstd data: " + what);

        // ------------------------------------------------------------------------------------------ FSE tables

        /// <summary>kind: 0 literal lengths, 1 match lengths, 2 offsets, 3 plain symbols (Huffman weights).</summary>
        static void BuildFse(short* norm, int symbols, int log, Entry* table, int kind)
        {
            int size = 1 << log, high = size - 1;
            byte* symbolAt = stackalloc byte[size];
            ushort* next = stackalloc ushort[symbols];
            for (int s = 0; s < symbols; s++)
            {
                if (norm[s] == -1) { symbolAt[high--] = (byte)s; next[s] = 1; }
                else next[s] = (ushort)norm[s];
            }
            int step = (size >> 1) + (size >> 3) + 3, mask = size - 1, pos = 0;
            for (int s = 0; s < symbols; s++)
                for (int i = 0; i < norm[s]; i++)
                {
                    symbolAt[pos] = (byte)s;
                    do pos = (pos + step) & mask; while (pos > high);
                }
            if (pos != 0) throw Bad("invalid FSE distribution");
            for (int i = 0; i < size; i++)
            {
                int s = symbolAt[i];
                uint x = next[s]++;
                int bits = log - BitOperations.Log2(x);
                Entry e;
                e.NextBits = (byte)bits;
                e.NextBase = (ushort)((x << bits) - (uint)size);
                switch (kind)
                {
                    case 0: e.Value = LlBase[s]; e.ExtraBits = LlBits[s]; break;
                    case 1: e.Value = MlBase[s]; e.ExtraBits = MlBits[s]; break;
                    case 2: e.Value = 1u << s; e.ExtraBits = (byte)s; break;
                    default: e.Value = (uint)s; e.ExtraBits = 0; break;
                }
                table[i] = e;
            }
        }

        /// <summary>Reads an FSE table description (forward bit stream) and builds the table. Returns the accuracy log.</summary>
        static int ReadFse(byte* src, int length, ref int pos, int maxLog, int maxSymbol, Entry* table, int kind)
        {
            short* norm = stackalloc short[256];
            long bit = (long)pos * 8, endBit = (long)length * 8;
            int log = (int)Peek(src, length, bit, 4) + 5;
            bit += 4;
            if (log > maxLog) throw Bad("FSE accuracy log too large");
            int remaining = 1 << log, symbols = 0;
            while (remaining > 0 && symbols <= maxSymbol)
            {
                int bits = BitOperations.Log2((uint)remaining + 1) + 1;
                int value = (int)Peek(src, length, bit, bits);
                int lowerMask = (1 << (bits - 1)) - 1;
                int threshold = (1 << bits) - 1 - (remaining + 1);
                if ((value & lowerMask) < threshold) { bit += bits - 1; value &= lowerMask; }
                else { bit += bits; if (value > lowerMask) value -= threshold; }
                int proba = value - 1;
                remaining -= proba < 0 ? 1 : proba;
                norm[symbols++] = (short)proba;
                if (proba == 0)
                {
                    for (; ; )
                    {
                        int repeat = (int)Peek(src, length, bit, 2);
                        bit += 2;
                        if (symbols + repeat > maxSymbol + 1) throw Bad("too many FSE symbols");
                        for (int i = 0; i < repeat; i++) norm[symbols++] = 0;
                        if (repeat != 3) break;
                    }
                }
                if (bit > endBit) throw Bad("truncated FSE table");
            }
            if (remaining != 0 || symbols > maxSymbol + 1 || bit > endBit) throw Bad("invalid FSE table");
            pos = (int)((bit + 7) >> 3);
            BuildFse(norm, symbols, log, table, kind);
            return log;
        }

        // forward (LSB first) peek of up to 25 bits; bits past the end read as zero
        static uint Peek(byte* src, int length, long bit, int count)
        {
            int i = (int)(bit >> 3);
            uint v = 0;
            for (int k = 0; k < 4 && i + k < length; k++) v |= (uint)src[i + k] << (8 * k);
            return (v >> (int)(bit & 7)) & ((1u << count) - 1);
        }

        // ------------------------------------------------------------------------------- backward bit streams

        // Bits are consumed from the end of the buffer towards its start; `bit` counts the bits still unread.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ulong ReadBack(byte* start, int length, ref long bit, int count)
        {
            bit -= count;
            if (bit < 0) return 0; // the caller detects the underrun through the negative position
            int i = (int)(bit >> 3), shift = (int)(bit & 7);
            ulong v;
            if (i + 8 <= length) v = Unsafe.ReadUnaligned<ulong>(start + i);
            else
            {
                v = 0;
                for (int k = 0; i + k < length; k++) v |= (ulong)start[i + k] << (8 * k);
            }
            return (v >> shift) & ((1UL << count) - 1);
        }

        // The next (up to 57) unread bits, first one in the top bit. Bits before the start of the stream read as zero.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ulong LoadBack(byte* start, int length, long bit)
        {
            if (bit >= 64)
            {
                int i = (int)((bit + 7) >> 3) - 8;
                return Unsafe.ReadUnaligned<ulong>(start + i) << (int)(64 - (bit - (long)i * 8));
            }
            if (bit <= 0) return 0;
            ulong v = 0;
            for (int k = 0; k < 8 && k < length; k++) v |= (ulong)start[k] << (8 * k);
            return v << (int)(64 - bit);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static uint Take(ref ulong c, int count)
        {
            uint v = (uint)((c >> 1) >> (63 - count));
            c <<= count;
            return v;
        }

        static long InitBack(byte* start, int length)
        {
            if (length == 0 || start[length - 1] == 0) throw Bad("bad bit stream end");
            return (long)(length - 1) * 8 + BitOperations.Log2(start[length - 1]);
        }

        // ---------------------------------------------------------------------------------------- Huffman

        // (also used by the encoder to check the tree descriptions it writes)
        internal int HuffmanLog => hufLog;
        internal int HuffmanEntry(int index) => huf[index];

        internal void ReadHuffmanTree(byte* src, int length, ref int pos)
        {
            if (pos >= length) throw Bad("truncated literals");
            int header = src[pos++];
            byte* weights = stackalloc byte[256];
            int count;
            if (header >= 128)
            {
                count = header - 127;
                int bytes = (count + 1) / 2;
                if (pos + bytes > length) throw Bad("truncated Huffman weights");
                for (int i = 0; i < count; i++)
                    weights[i] = (byte)((i & 1) == 0 ? src[pos + i / 2] >> 4 : src[pos + i / 2] & 15);
                pos += bytes;
            }
            else
            {
                if (header == 0 || pos + header > length) throw Bad("truncated Huffman weights");
                int p = pos;
                fixed (Entry* wt = weightTable)
                {
                    int log = ReadFse(src, pos + header, ref p, 6, 255, wt, 3);
                    byte* s = src + p;
                    int n = pos + header - p;
                    long bit = InitBack(s, n);
                    uint s1 = (uint)ReadBack(s, n, ref bit, log), s2 = (uint)ReadBack(s, n, ref bit, log);
                    if (bit < 0) throw Bad("truncated Huffman weights");
                    count = 0;
                    for (; ; )
                    {
                        if (count > 253) throw Bad("too many Huffman weights");
                        Entry e = wt[s1];
                        weights[count++] = (byte)e.Value;
                        if (bit < e.NextBits) { weights[count++] = (byte)wt[s2].Value; break; }
                        s1 = e.NextBase + (uint)ReadBack(s, n, ref bit, e.NextBits);
                        e = wt[s2];
                        weights[count++] = (byte)e.Value;
                        if (bit < e.NextBits) { weights[count++] = (byte)wt[s1].Value; break; }
                        s2 = e.NextBase + (uint)ReadBack(s, n, ref bit, e.NextBits);
                    }
                }
                pos += header;
            }

            // the last weight is implied: the weights must fill a power of two exactly
            uint sum = 0;
            for (int i = 0; i < count; i++)
            {
                if (weights[i] > 11) throw Bad("Huffman weight too large");
                if (weights[i] != 0) sum += 1u << (weights[i] - 1);
            }
            if (sum == 0) throw Bad("empty Huffman tree");
            int maxBits = BitOperations.Log2(sum) + 1;
            if (maxBits > 11) throw Bad("Huffman tree too deep");
            uint rest = (1u << maxBits) - sum;
            if (!BitOperations.IsPow2(rest)) throw Bad("invalid Huffman weights");
            weights[count++] = (byte)(BitOperations.Log2(rest) + 1);

            int* rank = stackalloc int[13];
            for (int i = 0; i < 13; i++) rank[i] = 0;
            for (int i = 0; i < count; i++) rank[weights[i]]++;
            if (rank[1] < 2 || (rank[1] & 1) != 0) throw Bad("invalid Huffman weights");
            // weight w takes 2^(w-1) table slots per symbol, lowest weights first, symbols in rising order
            int* start = stackalloc int[13];
            int at = 0;
            for (int w = 1; w <= maxBits; w++) { start[w] = at; at += rank[w] << (w - 1); }
            fixed (ushort* t = huf)
            {
                for (int s = 0; s < count; s++)
                {
                    int w = weights[s];
                    if (w == 0) continue;
                    int n = 1 << (w - 1);
                    ushort e = (ushort)(s | (maxBits + 1 - w) << 8);
                    ushort* q = t + start[w];
                    for (int i = 0; i < n; i++) q[i] = e;
                    start[w] += n;
                }
            }
            hufLog = maxBits;
            hufSet = true;
        }

        // Five symbols from one 8 byte window that ends at the read position (needs bit >= 64; 5 * 11 <= 57).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Decode5(ushort* t, byte* src, ref long bit, byte* dst, int shift)
        {
            int i = (int)((bit + 7) >> 3) - 8;
            ulong c = Unsafe.ReadUnaligned<ulong>(src + i) << (int)(64 - (bit - (long)i * 8));
            uint e = t[c >> shift]; dst[0] = (byte)e; int n = (int)(e >> 8); c <<= n; int used = n;
            e = t[c >> shift]; dst[1] = (byte)e; n = (int)(e >> 8); c <<= n; used += n;
            e = t[c >> shift]; dst[2] = (byte)e; n = (int)(e >> 8); c <<= n; used += n;
            e = t[c >> shift]; dst[3] = (byte)e; n = (int)(e >> 8); c <<= n; used += n;
            e = t[c >> shift]; dst[4] = (byte)e; used += (int)(e >> 8);
            bit -= used;
        }

        // The rest of a stream, symbol by symbol, with the end checks.
        void FinishHuffmanStream(ushort* t, byte* src, int length, long bit, byte* dst, byte* end)
        {
            int log = hufLog, shift = 64 - log;
            while (bit >= 64 && end - dst >= 5) { Decode5(t, src, ref bit, dst, shift); dst += 5; }
            while (dst < end)
            {
                uint index;
                if (bit >= log) { long b = bit; index = (uint)ReadBack(src, length, ref b, log); }
                else { long b = bit; index = (uint)ReadBack(src, length, ref b, (int)bit) << (int)(log - bit); }
                uint e = t[index];
                *dst++ = (byte)e;
                bit -= e >> 8;
                if (bit < 0) throw Bad("Huffman stream overrun");
            }
            if (bit != 0) throw Bad("Huffman stream not fully consumed");
        }

        void DecodeHuffmanStream(byte* src, int length, byte* dst, int count)
        {
            long bit = InitBack(src, length);
            fixed (ushort* t = huf) FinishHuffmanStream(t, src, length, bit, dst, dst + count);
        }

        /// <summary>Four streams side by side: they are independent, so the CPU overlaps their table lookups.</summary>
        void DecodeHuffman4(byte* s0, int n0, byte* s1, int n1, byte* s2, int n2, byte* s3, int n3, byte* dst, int per, int last)
        {
            long b0 = InitBack(s0, n0), b1 = InitBack(s1, n1), b2 = InitBack(s2, n2), b3 = InitBack(s3, n3);
            byte* o0 = dst, o1 = dst + per, o2 = dst + 2 * per, o3 = dst + 3 * per;
            byte* e0 = o1, e3 = o3 + last;
            int shift = 64 - hufLog;
            fixed (ushort* t = huf)
            {
                // the first three streams have the same length, so one output check covers them
                while (b0 >= 64 && b1 >= 64 && b2 >= 64 && b3 >= 64 && e0 - o0 >= 5 && e3 - o3 >= 5)
                {
                    Decode5(t, s0, ref b0, o0, shift);
                    Decode5(t, s1, ref b1, o1, shift);
                    Decode5(t, s2, ref b2, o2, shift);
                    Decode5(t, s3, ref b3, o3, shift);
                    o0 += 5; o1 += 5; o2 += 5; o3 += 5;
                }
                FinishHuffmanStream(t, s0, n0, b0, o0, dst + per);
                FinishHuffmanStream(t, s1, n1, b1, o1, dst + 2 * per);
                FinishHuffmanStream(t, s2, n2, b2, o2, dst + 3 * per);
                FinishHuffmanStream(t, s3, n3, b3, o3, e3);
            }
        }

        // ------------------------------------------------------------------------------------------- blocks

        /// <summary>Decodes the literals section into <see cref="lits"/>; returns its size.</summary>
        int DecodeLiterals(byte* src, int length, ref int pos, int blockMax)
        {
            if (pos >= length) throw Bad("truncated literals");
            int b0 = src[pos];
            int type = b0 & 3, format = (b0 >> 2) & 3;
            int regen, comp = 0, headerSize;
            bool four = false;
            if (type < 2)
            {
                if ((format & 1) == 0) { headerSize = 1; regen = b0 >> 3; }
                else if (format == 1) { headerSize = 2; if (pos + 2 > length) throw Bad("truncated literals"); regen = (b0 >> 4) | src[pos + 1] << 4; }
                else { headerSize = 3; if (pos + 3 > length) throw Bad("truncated literals"); regen = (b0 >> 4) | src[pos + 1] << 4 | src[pos + 2] << 12; }
            }
            else
            {
                headerSize = format < 2 ? 3 : format + 2;
                if (pos + headerSize > length) throw Bad("truncated literals");
                ulong h = 0;
                for (int i = 0; i < headerSize; i++) h |= (ulong)src[pos + i] << (8 * i);
                h >>= 4;
                int bits = format < 2 ? 10 : format == 2 ? 14 : 18;
                regen = (int)(h & ((1u << bits) - 1));
                comp = (int)((h >> bits) & ((1u << bits) - 1));
                four = format != 0;
            }
            pos += headerSize;
            if (regen > blockMax) throw Bad("literals too large");
            fixed (byte* l = lits)
            {
                if (type == 0)
                {
                    if (pos + regen > length) throw Bad("truncated literals");
                    Buffer.MemoryCopy(src + pos, l, regen, regen);
                    pos += regen;
                }
                else if (type == 1)
                {
                    if (pos >= length) throw Bad("truncated literals");
                    new Span<byte>(l, regen).Fill(src[pos++]);
                }
                else
                {
                    if (pos + comp > length) throw Bad("truncated literals");
                    int end = pos + comp;
                    if (type == 2) ReadHuffmanTree(src, end, ref pos);
                    else if (!hufSet) throw Bad("no Huffman table to reuse");
                    if (!four) DecodeHuffmanStream(src + pos, end - pos, l, regen);
                    else
                    {
                        if (end - pos < 6) throw Bad("truncated literal streams");
                        int n1 = src[pos] | src[pos + 1] << 8, n2 = src[pos + 2] | src[pos + 3] << 8, n3 = src[pos + 4] | src[pos + 5] << 8;
                        pos += 6;
                        int n4 = end - pos - n1 - n2 - n3;
                        int per = (regen + 3) / 4, last = regen - 3 * per;
                        if (n4 < 0 || last < 0) throw Bad("invalid literal streams");
                        DecodeHuffman4(src + pos, n1, src + pos + n1, n2, src + pos + n1 + n2, n3, src + pos + n1 + n2 + n3, n4, l, per, last);
                    }
                    pos = end;
                }
            }
            return regen;
        }

        int SetupTable(int mode, byte* src, int length, ref int pos, Entry[] table, Entry[] defaults, int defaultLog,
            int maxLog, int maxSymbol, int kind, ref bool isSet, int currentLog)
        {
            switch (mode)
            {
                case 0:
                    Array.Copy(defaults, table, defaults.Length);
                    isSet = true;
                    return defaultLog;
                case 1:
                {
                    if (pos >= length) throw Bad("truncated sequences");
                    int s = src[pos++];
                    if (s > maxSymbol) throw Bad("invalid RLE symbol");
                    Entry e = default;
                    switch (kind)
                    {
                        case 0: e.Value = LlBase[s]; e.ExtraBits = LlBits[s]; break;
                        case 1: e.Value = MlBase[s]; e.ExtraBits = MlBits[s]; break;
                        default: e.Value = 1u << s; e.ExtraBits = (byte)s; break;
                    }
                    table[0] = e;
                    isSet = true;
                    return 0;
                }
                case 2:
                {
                    int log;
                    fixed (Entry* t = table) log = ReadFse(src, length, ref pos, maxLog, maxSymbol, t, kind);
                    isSet = true;
                    return log;
                }
                default:
                    if (!isSet) throw Bad("no table to repeat");
                    return currentLog;
            }
        }

        /// <summary>Decodes one compressed block to dst[outPos..]; history starts at dst[histStart]. Returns the new outPos.</summary>
        int DecodeBlock(byte* src, int length, byte* dst, int outPos, int capacity, int histStart, int blockMax)
        {
            int pos = 0;
            int litSize = DecodeLiterals(src, length, ref pos, blockMax);
            if (pos >= length) throw Bad("truncated sequences");
            int nseq = src[pos++];
            if (nseq >= 128)
            {
                if (nseq == 255)
                {
                    if (pos + 2 > length) throw Bad("truncated sequences");
                    nseq = (src[pos] | src[pos + 1] << 8) + 0x7F00;
                    pos += 2;
                }
                else
                {
                    if (pos + 1 > length) throw Bad("truncated sequences");
                    nseq = ((nseq - 128) << 8) + src[pos++];
                }
            }
            int blockStart = outPos;
            fixed (byte* l = lits)
            {
                int litPos = 0;
                if (nseq > 0)
                {
                    if (pos >= length) throw Bad("truncated sequences");
                    int modes = src[pos++];
                    if ((modes & 3) != 0) throw Bad("reserved bits set");
                    llLog = SetupTable(modes >> 6, src, length, ref pos, llTable, LlDefaultTable, 6, 9, 35, 0, ref llSet, llLog);
                    ofLog = SetupTable((modes >> 4) & 3, src, length, ref pos, ofTable, OfDefaultTable, 5, 8, 31, 2, ref ofSet, ofLog);
                    mlLog = SetupTable((modes >> 2) & 3, src, length, ref pos, mlTable, MlDefaultTable, 6, 9, 52, 1, ref mlSet, mlLog);

                    byte* s = src + pos;
                    int n = length - pos;
                    long bit = InitBack(s, n);
                    fixed (Entry* llT = llTable, ofT = ofTable, mlT = mlTable)
                    {
                        uint llState = (uint)ReadBack(s, n, ref bit, llLog);
                        uint ofState = (uint)ReadBack(s, n, ref bit, ofLog);
                        uint mlState = (uint)ReadBack(s, n, ref bit, mlLog);
                        uint r0 = rep0, r1 = rep1, r2 = rep2;
                        int blockEnd = (int)Math.Min((long)capacity, (long)blockStart + blockMax);
                        for (int i = 0; i < nseq; i++)
                        {
                            Entry ll = llT[llState], of = ofT[ofState], ml = mlT[mlState];
                            // one 57 bit window serves the three extra bit fields (two for huge offsets), a second one the states
                            ulong c = LoadBack(s, n, bit);
                            uint offValue = of.Value + Take(ref c, of.ExtraBits);
                            bit -= of.ExtraBits;
                            if (of.ExtraBits + ml.ExtraBits + ll.ExtraBits > 56) c = LoadBack(s, n, bit);
                            uint matchLen = ml.Value + Take(ref c, ml.ExtraBits);
                            uint litLen = ll.Value + Take(ref c, ll.ExtraBits);
                            bit -= ml.ExtraBits + ll.ExtraBits;
                            if (i != nseq - 1)
                            {
                                c = LoadBack(s, n, bit);
                                llState = ll.NextBase + Take(ref c, ll.NextBits);
                                mlState = ml.NextBase + Take(ref c, ml.NextBits);
                                ofState = of.NextBase + Take(ref c, of.NextBits);
                                bit -= ll.NextBits + ml.NextBits + of.NextBits;
                            }
                            if (bit < 0) throw Bad("sequence stream overrun");

                            uint offset;
                            if (offValue > 3) { offset = offValue - 3; r2 = r1; r1 = r0; r0 = offset; }
                            else
                            {
                                uint idx = offValue - 1 + (litLen == 0 ? 1u : 0u);
                                if (idx == 0) offset = r0;
                                else
                                {
                                    offset = idx == 1 ? r1 : idx == 2 ? r2 : r0 - 1;
                                    if (offset == 0) throw Bad("zero offset");
                                    if (idx != 1) r2 = r1;
                                    r1 = r0; r0 = offset;
                                }
                            }

                            if (litLen > (uint)(litSize - litPos)) throw Bad("literals overrun");
                            if ((ulong)litLen + matchLen > (ulong)(blockEnd - outPos))
                                throw Bad(blockEnd == capacity ? "output does not fit the destination" : "block too large");
                            byte* o = dst + outPos;
                            // wild copies need room after the sequence; the literal buffer always has it
                            bool wild = capacity - outPos - (long)litLen - matchLen >= Slack;
                            if (wild && litLen <= 16) Unsafe.WriteUnaligned(o, Unsafe.ReadUnaligned<Vector128<byte>>(l + litPos));
                            else Buffer.MemoryCopy(l + litPos, o, litLen, litLen);
                            litPos += (int)litLen; outPos += (int)litLen; o += litLen;
                            if (offset > (uint)(outPos - histStart)) throw Bad("offset too far back");
                            if (wild) LzCopy.MatchWild(o, offset, matchLen);
                            else LzCopy.Match(o, offset, matchLen);
                            outPos += (int)matchLen;
                        }
                        if (bit != 0) throw Bad("sequence stream not fully consumed");
                        rep0 = r0; rep1 = r1; rep2 = r2;
                    }
                }
                else if (pos != length) throw Bad("trailing bytes in block");

                int rest = litSize - litPos;
                if (rest > capacity - outPos) throw Bad("output does not fit the destination");
                if ((long)outPos + rest - blockStart > blockMax) throw Bad("block too large");
                Buffer.MemoryCopy(l + litPos, dst + outPos, rest, rest);
                outPos += rest;
            }
            return outPos;
        }

        // -------------------------------------------------------------------------------------------- frames

        /// <summary>
        /// Decodes every frame in <paramref name="source"/>. When <paramref name="growable"/> is given, the output
        /// goes there and the array is enlarged as the data requires (a declared content size is only trusted up to
        /// a bound derived from the input size); otherwise output that does not fit <paramref name="destination"/>
        /// is an error. Returns the number of bytes written.
        /// </summary>
        public int Decode(ReadOnlySpan<byte> source, Span<byte> destination, ref byte[]? growable)
        {
            bool grow = growable != null;
            int capacity = grow ? growable!.Length : destination.Length;
            int outPos = 0, pos = 0;
            fixed (byte* src = source)
            {
                while (pos < source.Length)
                {
                    if (source.Length - pos < 4) throw Bad("truncated frame header");
                    uint magic = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(pos));
                    if ((magic & 0xFFFFFFF0) == SkippableMagic)
                    {
                        if (source.Length - pos < 8) throw Bad("truncated skippable frame");
                        uint size = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(pos + 4));
                        if (size > (uint)(source.Length - pos - 8)) throw Bad("truncated skippable frame");
                        pos += 8 + (int)size;
                        continue;
                    }
                    if (magic != Magic) throw Bad("bad magic number");
                    pos += 4;
                    if (pos >= source.Length) throw Bad("truncated frame header");
                    int fhd = src[pos++];
                    if ((fhd & 0x08) != 0) throw Bad("reserved bit set");
                    bool single = (fhd & 0x20) != 0, checksum = (fhd & 0x04) != 0;
                    int dictBytes = (fhd & 3) == 3 ? 4 : fhd & 3;
                    int fcsBytes = (fhd >> 6) switch { 0 => single ? 1 : 0, 1 => 2, 2 => 4, _ => 8 };
                    if (source.Length - pos < (single ? 0 : 1) + dictBytes + fcsBytes) throw Bad("truncated frame header");
                    ulong windowSize = 0;
                    if (!single)
                    {
                        int wd = src[pos++];
                        int exponent = wd >> 3, mantissa = wd & 7;
                        if (exponent > 31) throw Bad("window too large");
                        windowSize = 1UL << (10 + exponent);
                        windowSize += (windowSize >> 3) * (ulong)mantissa;
                    }
                    uint dictId = 0;
                    for (int i = 0; i < dictBytes; i++) dictId |= (uint)src[pos++] << (8 * i);
                    if (dictId != 0) throw new ImageNotSupportedException("zstd data needs a dictionary (id " + dictId + ")");
                    ulong contentSize = ulong.MaxValue;
                    if (fcsBytes > 0)
                    {
                        contentSize = 0;
                        for (int i = 0; i < fcsBytes; i++) contentSize |= (ulong)src[pos++] << (8 * i);
                        if (fcsBytes == 2) contentSize += 256;
                    }
                    if (single) windowSize = contentSize;
                    int blockMax = (int)Math.Min(windowSize, MaxBlock);

                    if (contentSize != ulong.MaxValue)
                    {
                        if (!grow && contentSize > (ulong)(capacity - outPos)) throw Bad("output does not fit the destination");
                        if (contentSize > (ulong)(Array.MaxLength - outPos)) throw Bad("frame too large");
                    }

                    int frameStart = outPos;
                    llSet = mlSet = ofSet = hufSet = false;
                    rep0 = 1; rep1 = 4; rep2 = 8;
                    for (; ; )
                    {
                        if (source.Length - pos < 3) throw Bad("truncated block header");
                        int bh = src[pos] | src[pos + 1] << 8 | src[pos + 2] << 16;
                        pos += 3;
                        bool last = (bh & 1) != 0;
                        int type = (bh >> 1) & 3, size = bh >> 3;
                        int produced = type == 2 ? blockMax : size; // upper bound for a compressed block
                        if (type != 2 && size > blockMax) throw Bad("block too large");
                        if (grow && capacity - outPos < produced + Slack)
                        {
                            long need = (long)outPos + produced + Slack;
                            if (need > Array.MaxLength) throw Bad("output too large");
                            long want = Math.Max(need, (long)capacity * 2);
                            if (contentSize != ulong.MaxValue)
                            {
                                long target = frameStart + (long)contentSize + Slack;
                                long trusted = Math.Max(1 << 20, (long)source.Length * 64);
                                if (target > want && target <= trusted) want = target;
                                else if (target < want) want = Math.Max(need, target);
                            }
                            Array.Resize(ref growable, (int)Math.Min(want, Array.MaxLength));
                            capacity = growable!.Length;
                        }
                        Span<byte> output = grow ? growable : destination;
                        fixed (byte* dst = output)
                        {
                            switch (type)
                            {
                                case 0:
                                    if (size > source.Length - pos) throw Bad("truncated block");
                                    if (size > capacity - outPos) throw Bad("output does not fit the destination");
                                    Buffer.MemoryCopy(src + pos, dst + outPos, size, size);
                                    pos += size; outPos += size;
                                    break;
                                case 1:
                                    if (source.Length - pos < 1) throw Bad("truncated block");
                                    if (size > capacity - outPos) throw Bad("output does not fit the destination");
                                    new Span<byte>(dst + outPos, size).Fill(src[pos++]);
                                    outPos += size;
                                    break;
                                case 2:
                                    if (size > source.Length - pos) throw Bad("truncated block");
                                    if (size > MaxBlock) throw Bad("block too large");
                                    outPos = DecodeBlock(src + pos, size, dst, outPos, capacity, frameStart, blockMax);
                                    pos += size;
                                    break;
                                default:
                                    throw Bad("reserved block type");
                            }
                        }
                        if (contentSize != ulong.MaxValue && (ulong)(outPos - frameStart) > contentSize) throw Bad("frame larger than its declared size");
                        if (last) break;
                    }
                    if (contentSize != ulong.MaxValue && (ulong)(outPos - frameStart) != contentSize) throw Bad("frame size mismatch");
                    if (checksum)
                    {
                        if (source.Length - pos < 4) throw Bad("truncated checksum");
                        uint expected = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(pos));
                        pos += 4;
                        ReadOnlySpan<byte> content = grow ? growable.AsSpan(frameStart, outPos - frameStart) : destination.Slice(frameStart, outPos - frameStart);
                        if ((uint)Checksums.XxHash64(content) != expected) throw Bad("content checksum mismatch");
                    }
                }
            }
            return outPos;
        }
    }
}
