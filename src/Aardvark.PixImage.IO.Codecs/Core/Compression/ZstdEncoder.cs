using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Aardvark.ImageCodecs.Compression
{
    /// <summary>
    /// A simple, fast Zstandard compressor: greedy matching through one hash table (with the most recent offset
    /// tried first), Huffman coded literals and FSE coded sequences, one independent decision per 128 KiB block
    /// with raw and RLE blocks as fallbacks. It has a single speed/ratio setting, comparable to the fast levels
    /// of the reference implementation; it does not attempt optimal parsing, long distance matching or
    /// dictionaries.
    /// </summary>
    internal sealed unsafe class ZstdEncoder
    {
        const int BlockSize = 128 * 1024, MaxHashLog = 17;
        const int WorkSize = 9 * BlockSize + 4096;

        struct Seq
        {
            public int LitLen, MatchLen;
            public uint OffValue; // offset + 3, or 1 for "same offset as the previous sequence"
        }

        // An FSE encoding table: where each symbol's states live and how many bits a transition emits.
        sealed class CTable
        {
            public int Log;
            public readonly ushort[] States = new ushort[512];
            public readonly int[] DeltaBits = new int[64], DeltaState = new int[64];
        }

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
        static readonly byte[] LlCodeSmall = MakeCodes(LlBase, 64), MlCodeSmall = MakeCodes(MlBase, 131);

        static byte[] MakeCodes(uint[] bases, int count)
        {
            var t = new byte[count];
            for (int v = 0; v < count; v++)
            {
                int c = 0;
                while (c + 1 < bases.Length && bases[c + 1] <= v) c++;
                t[v] = (byte)c;
            }
            return t;
        }

        readonly int[] hash = new int[1 << MaxHashLog];
        int hashShift, longShift; // 64 - log2(table size)
        int[] longHash = Array.Empty<int>();
        int hashBytes;
        bool useLong;
        readonly Seq[] seqs = new Seq[BlockSize / 3 + 8];
        readonly byte[] lits = new byte[BlockSize + 64];
        readonly byte[] work = new byte[WorkSize];
        readonly byte[] llCodes = new byte[BlockSize / 3 + 8], mlCodes = new byte[BlockSize / 3 + 8], ofCodes = new byte[BlockSize / 3 + 8];
        readonly CTable llTable = new CTable(), mlTable = new CTable(), ofTable = new CTable(), weightTable = new CTable();
        readonly ZstdDecoder verifier = new ZstdDecoder();
        uint rep; // the most recent match offset, as the decoder will see it (fast level)
        uint rep0, rep1, rep2; // all three repeat offsets (lazy levels)

        // lazy levels: hash chains
        int[] chainHead = Array.Empty<int>(), chain = Array.Empty<int>();
        // price based levels: binary trees of the positions per hash of their first four bytes (see BtFind)
        int[] btHead = Array.Empty<int>(), tree = Array.Empty<int>();
        int btHashLog, btMask, btNice;
        bool useTree;
        int chainHashLog, chainMask, searchDepth, lazySteps;
        // estimated cost in bits of each literal byte value, from the last Huffman coded literals section
        readonly byte[] litCost = new byte[256];

        // ------------------------------------------------------------------------------------ bit output

        // Forward writer; the decoder reads these streams from their end, so what is written last is read first.
        struct BitWriter
        {
            public byte* Pos, End;
            ulong acc;
            int bits;
            public bool Overflow;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Put(uint value, int count)
            {
                acc |= (ulong)value << bits;
                bits += count;
                if (bits >= 32) Flush32();
            }

            void Flush32()
            {
                if (End - Pos < 8) { Overflow = true; bits = 0; acc = 0; return; }
                Unsafe.WriteUnaligned(Pos, (uint)acc);
                Pos += 4;
                acc >>= 32; bits -= 32;
            }

            /// <summary>Writes the end mark (a single one bit) and the pending bytes.</summary>
            public void Finish()
            {
                Put(1, 1);
                while (bits > 0)
                {
                    if (Pos >= End) { Overflow = true; return; }
                    *Pos++ = (byte)acc;
                    acc >>= 8; bits -= 8;
                }
                bits = 0;
            }

            /// <summary>Pads to a byte boundary without an end mark (FSE table descriptions).</summary>
            public void Align()
            {
                while (bits > 0)
                {
                    if (Pos >= End) { Overflow = true; return; }
                    *Pos++ = (byte)acc;
                    acc >>= 8; bits -= 8;
                }
                bits = 0;
            }
        }

        // ------------------------------------------------------------------------------------------- FSE

        static void BuildCTable(short* norm, int symbols, int log, CTable table)
        {
            int size = 1 << log, high = size - 1;
            byte* symbolAt = stackalloc byte[size];
            int* cumul = stackalloc int[symbols + 1];
            cumul[0] = 0;
            for (int s = 0; s < symbols; s++)
            {
                cumul[s + 1] = cumul[s] + (norm[s] == -1 ? 1 : norm[s]);
                if (norm[s] == -1) symbolAt[high--] = (byte)s;
            }
            int step = (size >> 1) + (size >> 3) + 3, mask = size - 1, pos = 0;
            for (int s = 0; s < symbols; s++)
                for (int i = 0; i < norm[s]; i++)
                {
                    symbolAt[pos] = (byte)s;
                    do pos = (pos + step) & mask; while (pos > high);
                }
            fixed (ushort* states = table.States)
            fixed (int* deltaBits = table.DeltaBits, deltaState = table.DeltaState)
            {
                for (int u = 0; u < size; u++) states[cumul[symbolAt[u]]++] = (ushort)(size + u);
                int total = 0;
                for (int s = 0; s < symbols; s++)
                {
                    int n = norm[s] == -1 ? 1 : norm[s];
                    if (n == 0) { deltaBits[s] = ((log + 1) << 16) - size; deltaState[s] = 0; continue; }
                    if (n == 1) { deltaBits[s] = (log << 16) - size; deltaState[s] = total - 1; total++; continue; }
                    int maxBitsOut = log - BitOperations.Log2((uint)n - 1);
                    deltaBits[s] = (maxBitsOut << 16) - (n << maxBitsOut);
                    deltaState[s] = total - n;
                    total += n;
                }
            }
            table.Log = log;
        }

        /// <summary>Scales a histogram to a power of two total; every symbol that occurs keeps at least one slot.</summary>
        static void Normalize(uint* count, int symbols, int total, int log, short* norm)
        {
            int target = 1 << log, sum = 0, largest = 0;
            for (int s = 0; s < symbols; s++)
            {
                if (count[s] == 0) { norm[s] = 0; continue; }
                int v = (int)((long)count[s] * target / total);
                if (v == 0) v = 1;
                norm[s] = (short)v;
                sum += v;
                if (v > norm[largest] || count[largest] == 0) largest = s;
            }
            if (sum <= target) { norm[largest] += (short)(target - sum); return; }
            for (; sum > target; sum--)
            {
                int big = 0;
                for (int s = 1; s < symbols; s++) if (norm[s] > norm[big]) big = s;
                norm[big]--;
            }
        }

        /// <summary>Writes the table description the decoder's ReadFse expects.</summary>
        static void WriteFseDescription(ref BitWriter w, short* norm, int symbols, int log)
        {
            w.Put((uint)(log - 5), 4);
            int remaining = 1 << log;
            int s = 0;
            while (remaining > 0 && s < symbols)
            {
                int bits = BitOperations.Log2((uint)remaining + 1) + 1;
                int lowerMask = (1 << (bits - 1)) - 1;
                int threshold = (1 << bits) - 1 - (remaining + 1);
                int value = norm[s] + 1;
                if (value < threshold) w.Put((uint)value, bits - 1);
                else w.Put((uint)(value > lowerMask ? value + threshold : value), bits);
                remaining -= norm[s];
                s++;
                if (norm[s - 1] == 0)
                {
                    // the zeros that follow are run length coded two bits at a time
                    int run = 0;
                    while (s < symbols && norm[s] == 0) { run++; s++; }
                    for (; run >= 3; run -= 3) w.Put(3, 2);
                    w.Put((uint)run, 2);
                }
            }
            w.Align();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static uint InitState(CTable t, int symbol)
        {
            int delta = t.DeltaBits[symbol];
            int bits = (delta + (1 << 15)) >> 16;
            int value = (bits << 16) - delta;
            return t.States[(value >> bits) + t.DeltaState[symbol]];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void EncodeSymbol(ref BitWriter w, CTable t, ref uint state, int symbol)
        {
            int bits = (int)((state + (uint)t.DeltaBits[symbol]) >> 16);
            w.Put(state & ((1u << bits) - 1), bits);
            state = t.States[(int)(state >> bits) + t.DeltaState[symbol]];
        }

        // ---------------------------------------------------------------------------------------- literals

        static int RawLiterals(byte* lit, int n, byte* dst, int type, int payload)
        {
            int h;
            if (n < 32) { dst[0] = (byte)(type | n << 3); h = 1; }
            else if (n < 4096) { int v = type | 1 << 2 | n << 4; dst[0] = (byte)v; dst[1] = (byte)(v >> 8); h = 2; }
            else { int v = type | 3 << 2 | n << 4; dst[0] = (byte)v; dst[1] = (byte)(v >> 8); dst[2] = (byte)(v >> 16); h = 3; }
            Buffer.MemoryCopy(lit, dst + h, payload, payload);
            return h + payload;
        }

        /// <summary>Huffman tree description: FSE compressed weights, or four bits per weight. Returns 0 if neither works.</summary>
        int WriteWeights(byte* weights, int count, byte* dst, byte* dstEnd)
        {
            uint* hist = stackalloc uint[16];
            for (int i = 0; i < 16; i++) hist[i] = 0;
            int maxWeight = 0;
            for (int i = 0; i < count; i++) { hist[weights[i]]++; if (weights[i] > maxWeight) maxWeight = weights[i]; }
            int distinct = 0;
            uint most = 0;
            for (int i = 0; i <= maxWeight; i++) { if (hist[i] != 0) distinct++; most = Math.Max(most, hist[i]); }

            int size = 0;
            if (count > 2 && distinct > 1 && most > 1)
            {
                int log = Math.Clamp(BitOperations.Log2((uint)count) - 1, 5, 6);
                short* norm = stackalloc short[16];
                Normalize(hist, maxWeight + 1, count, log, norm);
                BuildCTable(norm, maxWeight + 1, log, weightTable);
                var w = new BitWriter { Pos = dst + 1, End = dstEnd };
                WriteFseDescription(ref w, norm, maxWeight + 1, log);
                // two interleaved states; the decoder emits even positions from the first, odd ones from the second
                int n = count;
                uint s1, s2;
                if ((n & 1) != 0)
                {
                    s1 = InitState(weightTable, weights[n - 1]);
                    s2 = InitState(weightTable, weights[n - 2]);
                    EncodeSymbol(ref w, weightTable, ref s1, weights[n - 3]);
                    n -= 3;
                }
                else
                {
                    s2 = InitState(weightTable, weights[n - 1]);
                    s1 = InitState(weightTable, weights[n - 2]);
                    n -= 2;
                }
                for (; n > 0; n -= 2)
                {
                    EncodeSymbol(ref w, weightTable, ref s2, weights[n - 1]);
                    EncodeSymbol(ref w, weightTable, ref s1, weights[n - 2]);
                }
                w.Put(s2 - (1u << log), log);
                w.Put(s1 - (1u << log), log);
                w.Finish();
                int bytes = (int)(w.Pos - (dst + 1));
                if (!w.Overflow && bytes < 128 && bytes < (count + 1) / 2)
                {
                    dst[0] = (byte)bytes;
                    size = 1 + bytes;
                }
            }
            if (size == 0)
            {
                if (count > 128 || dstEnd - dst < 1 + (count + 1) / 2) return 0;
                dst[0] = (byte)(127 + count);
                for (int i = 0; i < count; i += 2)
                    dst[1 + i / 2] = (byte)(weights[i] << 4 | (i + 1 < count ? weights[i + 1] : 0));
                size = 1 + (count + 1) / 2;
            }
            return size;
        }

        static int EncodeHuffmanStream(byte* lit, int n, ushort* codes, byte* lens, byte* dst, byte* dstEnd)
        {
            // worst case 11 bits per symbol plus the end mark
            if (dstEnd - dst < (long)n * 11 / 8 + 16) return -1;
            byte* o = dst;
            ulong acc = 0;
            int bits = 0;
            int i = n - 1;
            // symbols go in last to first; five of them (at most 55 bits) fit between flushes
            for (; i >= 4; i -= 5)
            {
                uint s0 = lit[i], s1 = lit[i - 1], s2 = lit[i - 2], s3 = lit[i - 3], s4 = lit[i - 4];
                acc |= (ulong)codes[s0] << bits; bits += lens[s0];
                acc |= (ulong)codes[s1] << bits; bits += lens[s1];
                acc |= (ulong)codes[s2] << bits; bits += lens[s2];
                acc |= (ulong)codes[s3] << bits; bits += lens[s3];
                acc |= (ulong)codes[s4] << bits; bits += lens[s4];
                Unsafe.WriteUnaligned(o, acc);
                o += bits >> 3;
                acc >>= bits & ~7;
                bits &= 7;
            }
            for (; i >= 0; i--) { acc |= (ulong)codes[lit[i]] << bits; bits += lens[lit[i]]; }
            acc |= 1UL << bits; bits++;
            Unsafe.WriteUnaligned(o, acc);
            o += (bits + 7) >> 3;
            return (int)(o - dst);
        }

        /// <summary>Writes the literals section for lit[0, n). Always succeeds (raw literals are the fallback).</summary>
        int EncodeLiterals(byte* lit, int n, byte* dst, byte* dstEnd)
        {
            if (n < 64) return RawLiterals(lit, n, dst, 0, n);
            uint* freq = stackalloc uint[256];
            for (int i = 0; i < 256; i++) freq[i] = 0;
            {
                // four interleaved counters: runs of equal bytes do not serialise on one memory cell
                uint* f4 = stackalloc uint[4 * 256];
                new Span<uint>(f4, 4 * 256).Clear();
                int i4 = 0;
                for (; i4 + 4 <= n; i4 += 4) { f4[lit[i4]]++; f4[256 + lit[i4 + 1]]++; f4[512 + lit[i4 + 2]]++; f4[768 + lit[i4 + 3]]++; }
                for (; i4 < n; i4++) f4[lit[i4]]++;
                for (int i = 0; i < 256; i++) freq[i] = f4[i] + f4[256 + i] + f4[512 + i] + f4[768 + i];
            }
            int last = 255;
            while (freq[last] == 0) last--;
            if (freq[last] == n) return RawLiterals(lit, n, dst, 1, 1); // one value: run length coded

            byte* lens = stackalloc byte[256];
            Deflater.BuildLengths(freq, 256, 11, lens);
            int maxLen = 0;
            long bitTotal = 0;
            for (int i = 0; i < 256; i++) { if (lens[i] > maxLen) maxLen = lens[i]; bitTotal += (long)freq[i] * lens[i]; }
            // BuildLengths may have added a dummy symbol to complete a one symbol code
            last = 255;
            while (lens[last] == 0) last--;
            if (bitTotal / 8 + 16 >= n) return RawLiterals(lit, n, dst, 0, n);

            // weight w <-> code length maxLen + 1 - w; codes are handed out as the decoder fills its table:
            // rising weight, then rising symbol
            byte* weights = stackalloc byte[256];
            ushort* codes = stackalloc ushort[256];
            int* next = stackalloc int[13];
            int* rank = stackalloc int[13];
            for (int i = 0; i < 13; i++) rank[i] = 0;
            for (int i = 0; i <= last; i++) { weights[i] = (byte)(lens[i] == 0 ? 0 : maxLen + 1 - lens[i]); rank[weights[i]]++; }
            int at = 0;
            for (int w = 1; w <= maxLen; w++) { next[w] = at; at += rank[w] << (w - 1); }
            for (int i = 0; i <= last; i++)
            {
                int w = weights[i];
                if (w == 0) continue;
                codes[i] = (ushort)(next[w] >> (w - 1));
                next[w] += 1 << (w - 1);
            }

            bool four = n >= 256;
            int sizeBits = n < 1024 ? 10 : n < 16384 ? 14 : 18;
            int headerSize = sizeBits == 10 ? 3 : sizeBits == 14 ? 4 : 5;
            byte* body = dst + headerSize;
            int tree = WriteWeights(weights, last, body, dstEnd); // the last weight is implied
            if (tree == 0) return RawLiterals(lit, n, dst, 0, n);
            // the description must decode to exactly this code (cheap, and guards the corner cases of the weight coder)
            {
                int p = 0;
                try { verifier.ReadHuffmanTree(body, tree, ref p); }
                catch (ImageFormatException) { return RawLiterals(lit, n, dst, 0, n); }
                if (p != tree || verifier.HuffmanLog != maxLen) return RawLiterals(lit, n, dst, 0, n);
                for (int i = 0; i <= last; i++)
                    if (lens[i] != 0 && verifier.HuffmanEntry(codes[i] << (maxLen - lens[i])) != (i | lens[i] << 8))
                        return RawLiterals(lit, n, dst, 0, n);
            }

            byte* o = body + tree;
            if (!four)
            {
                int s = EncodeHuffmanStream(lit, n, codes, lens, o, dstEnd);
                if (s < 0) return RawLiterals(lit, n, dst, 0, n);
                o += s;
            }
            else
            {
                int per = (n + 3) / 4;
                byte* jump = o;
                o += 6;
                if (dstEnd - o < 16) return RawLiterals(lit, n, dst, 0, n);
                for (int k = 0; k < 4; k++)
                {
                    int count = k < 3 ? per : n - 3 * per;
                    int s = EncodeHuffmanStream(lit + k * per, count, codes, lens, o, dstEnd);
                    if (s < 0 || s > 65535) return RawLiterals(lit, n, dst, 0, n);
                    if (k < 3) { jump[2 * k] = (byte)s; jump[2 * k + 1] = (byte)(s >> 8); }
                    o += s;
                }
            }
            int comp = (int)(o - body);
            if (comp >= n || comp >= 1 << sizeBits) return RawLiterals(lit, n, dst, 0, n);
            // size format: 0 = one stream (10 bit sizes), 1..3 = four streams with 10, 14, 18 bit sizes
            for (int i = 0; i < 256; i++) litCost[i] = lens[i] != 0 ? lens[i] : (byte)12;
            int format = !four ? 0 : sizeBits == 10 ? 1 : sizeBits == 14 ? 2 : 3;
            ulong h = 2u | (uint)format << 2 | (ulong)n << 4 | (ulong)comp << (4 + sizeBits);
            for (int i = 0; i < headerSize; i++) dst[i] = (byte)(h >> (8 * i));
            return headerSize + comp;
        }

        // --------------------------------------------------------------------------------------- sequences

        /// <summary>Chooses predefined, RLE or a transmitted table for one code stream. Returns the mode.</summary>
        static int SetupTable(ref BitWriter headers, byte* codes, int count, int symbols, int maxLog, short[] defaults, int defaultLog, CTable table, ref byte rleByte)
        {
            uint* hist = stackalloc uint[64];
            for (int i = 0; i < 64; i++) hist[i] = 0;
            int max = 0;
            for (int i = 0; i < count; i++) { hist[codes[i]]++; if (codes[i] > max) max = codes[i]; }
            short* norm = stackalloc short[64];
            if (hist[max] == count)
            {
                // a single code: a table with one state that emits no bits
                for (int i = 0; i <= max; i++) norm[i] = 0;
                norm[max] = 1;
                BuildCTable(norm, max + 1, 0, table);
                rleByte = (byte)max;
                return 1;
            }
            bool fitsDefault = max < defaults.Length;
            if (count < 64 && fitsDefault)
            {
                fixed (short* d = defaults) BuildCTable(d, defaults.Length, defaultLog, table);
                return 0;
            }
            int present = 0;
            for (int i = 0; i <= max; i++) if (hist[i] != 0) present++;
            int log = Math.Clamp(BitOperations.Log2((uint)count) - 1, 5, maxLog);
            while (1 << log < present) log++;
            Normalize(hist, max + 1, count, log, norm);
            BuildCTable(norm, max + 1, log, table);
            WriteFseDescription(ref headers, norm, max + 1, log);
            return 2;
        }

        int EncodeSequences(int nseq, byte* dst, byte* dstEnd)
        {
            byte* o = dst;
            if (dstEnd - o < 16) return -1;
            if (nseq < 128) *o++ = (byte)nseq;
            else if (nseq < 0x7F00) { *o++ = (byte)((nseq >> 8) + 128); *o++ = (byte)nseq; }
            else { *o++ = 255; *o++ = (byte)(nseq - 0x7F00); *o++ = (byte)((nseq - 0x7F00) >> 8); }
            if (nseq == 0) return (int)(o - dst);

            fixed (Seq* sq = seqs)
            fixed (byte* ll = llCodes, ml = mlCodes, of = ofCodes)
            fixed (byte* llSmall = LlCodeSmall, mlSmall = MlCodeSmall)
            {
                for (int i = 0; i < nseq; i++)
                {
                    int l = sq[i].LitLen, m = sq[i].MatchLen;
                    ll[i] = l < 64 ? llSmall[l] : (byte)(BitOperations.Log2((uint)l) + 19);
                    ml[i] = m < 131 ? mlSmall[m] : (byte)(BitOperations.Log2((uint)m - 3) + 36);
                    of[i] = (byte)BitOperations.Log2(sq[i].OffValue);
                }
                byte* modes = o++;
                byte rleLl = 0, rleOf = 0, rleMl = 0;
                // table descriptions and RLE bytes come in the order literal lengths, offsets, match lengths
                var headers = new BitWriter { Pos = o, End = dstEnd };
                int modeLl = SetupTable(ref headers, ll, nseq, 36, 9, LlDefault, 6, llTable, ref rleLl);
                if (modeLl == 1) { if (headers.Pos >= dstEnd) return -1; *headers.Pos++ = rleLl; }
                int modeOf = SetupTable(ref headers, of, nseq, 32, 8, OfDefault, 5, ofTable, ref rleOf);
                if (modeOf == 1) { if (headers.Pos >= dstEnd) return -1; *headers.Pos++ = rleOf; }
                int modeMl = SetupTable(ref headers, ml, nseq, 53, 9, MlDefault, 6, mlTable, ref rleMl);
                if (modeMl == 1) { if (headers.Pos >= dstEnd) return -1; *headers.Pos++ = rleMl; }
                if (headers.Overflow) return -1;
                *modes = (byte)(modeLl << 6 | modeOf << 4 | modeMl << 2);
                o = headers.Pos;

                // The decoder reads this stream backwards, so the last sequence goes in first; within a sequence
                // the fields go in opposite to the order they are read.
                // one sequence adds at most 89 bits; three flushes of a 64 bit accumulator keep up with that
                if (dstEnd - o < (long)nseq * 12 + 32) return -1;
                fixed (ushort* llS = llTable.States, mlS = mlTable.States, ofS = ofTable.States)
                fixed (int* llDb = llTable.DeltaBits, llDs = llTable.DeltaState, mlDb = mlTable.DeltaBits, mlDs = mlTable.DeltaState,
                    ofDb = ofTable.DeltaBits, ofDs = ofTable.DeltaState)
                fixed (uint* llBase = LlBase, mlBase = MlBase)
                fixed (byte* llBits = LlBits, mlBits = MlBits)
                {
                    int last = nseq - 1;
                    uint sLl = InitState(llTable, ll[last]), sOf = InitState(ofTable, of[last]), sMl = InitState(mlTable, ml[last]);
                    ulong acc = 0;
                    int bits = 0;
                    for (int i = last; ; i--)
                    {
                        int lc = ll[i], mc = ml[i], oc = of[i];
                        acc |= (ulong)((uint)sq[i].LitLen - llBase[lc]) << bits; bits += llBits[lc];
                        acc |= (ulong)((uint)sq[i].MatchLen - mlBase[mc]) << bits; bits += mlBits[mc];
                        Unsafe.WriteUnaligned(o, acc); o += bits >> 3; acc >>= bits & ~7; bits &= 7;
                        acc |= (ulong)(sq[i].OffValue - (1u << oc)) << bits; bits += oc;
                        Unsafe.WriteUnaligned(o, acc); o += bits >> 3; acc >>= bits & ~7; bits &= 7;
                        if (i == 0) break;
                        // transitions into sequence i's states, read as: literal length, match length, offset
                        int sym = of[i - 1];
                        int n = (int)((sOf + (uint)ofDb[sym]) >> 16);
                        acc |= (ulong)(sOf & ((1u << n) - 1)) << bits; bits += n;
                        sOf = ofS[(int)(sOf >> n) + ofDs[sym]];
                        sym = ml[i - 1];
                        n = (int)((sMl + (uint)mlDb[sym]) >> 16);
                        acc |= (ulong)(sMl & ((1u << n) - 1)) << bits; bits += n;
                        sMl = mlS[(int)(sMl >> n) + mlDs[sym]];
                        sym = ll[i - 1];
                        n = (int)((sLl + (uint)llDb[sym]) >> 16);
                        acc |= (ulong)(sLl & ((1u << n) - 1)) << bits; bits += n;
                        sLl = llS[(int)(sLl >> n) + llDs[sym]];
                        Unsafe.WriteUnaligned(o, acc); o += bits >> 3; acc >>= bits & ~7; bits &= 7;
                    }
                    acc |= (ulong)(sMl - (1u << mlTable.Log)) << bits; bits += mlTable.Log;
                    acc |= (ulong)(sOf - (1u << ofTable.Log)) << bits; bits += ofTable.Log;
                    acc |= (ulong)(sLl - (1u << llTable.Log)) << bits; bits += llTable.Log;
                    acc |= 1UL << bits; bits++; // end mark
                    Unsafe.WriteUnaligned(o, acc);
                    o += (bits + 7) >> 3;
                }
            }
            return (int)(o - dst);
        }

        // ------------------------------------------------------------------------------------------ blocks

        // hash of the next hashBytes (5..7) bytes / of the next 8 bytes
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static uint Hash(byte* p, int shiftIn, int shiftOut) => (uint)(((Unsafe.ReadUnaligned<ulong>(p) << shiftIn) * 0x9E3779B185EBCA87UL) >> shiftOut);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static uint Hash8(byte* p, int shiftOut) => (uint)((Unsafe.ReadUnaligned<ulong>(p) * 0x9E3779B185EBCA87UL) >> shiftOut);

        static int MatchLength(byte* a, byte* b, byte* end)
        {
            byte* start = b;
            while (b + 8 <= end)
            {
                ulong x = Unsafe.ReadUnaligned<ulong>(a) ^ Unsafe.ReadUnaligned<ulong>(b);
                if (x != 0) return (int)(b - start) + (BitOperations.TrailingZeroCount(x) >> 3);
                a += 8; b += 8;
            }
            while (b < end && *a == *b) { a++; b++; }
            return (int)(b - start);
        }

        /// <summary>
        /// Greedy block compressor of the fast levels: one hash table over hashBytes bytes (so only matches of at
        /// least that length are looked for, which keeps noisy data fast), optionally a second one over 8 bytes that
        /// is tried first, and the previous offset checked one byte ahead. Compresses src[start, start + length)
        /// (history: everything before within the window) into dst; returns the size, or 0 for "store it raw".
        /// </summary>
        int CompressBlock(byte* src, int start, int length, int window, byte* dst, int capacity)
        {
            int nseq = 0, nlit = 0;
            uint rep = this.rep;
            int hashBytes = this.hashBytes, shiftIn = 64 - 8 * hashBytes;
            bool useLong = this.useLong;
            int hashShift = this.hashShift, longShift = this.longShift;
            fixed (int* ht = hash, lt = longHash)
            fixed (Seq* sq = seqs)
            fixed (byte* lit = lits)
            {
                byte* end = src + start + length;
                byte* limit = end - 9; // hashing reads eight bytes, also one position ahead
                byte* p = src + start, anchor = p;
                while (p < limit)
                {
                    int matchLen = 0;
                    uint offset = 0;
                    int pos = (int)(p - src);
                    // the previous offset again, one byte ahead? (cheap to code and usually the longest)
                    if ((uint)pos + 1 >= rep && Unsafe.ReadUnaligned<uint>(p + 1) == Unsafe.ReadUnaligned<uint>(p + 1 - rep))
                    {
                        p++;
                        matchLen = 4 + MatchLength(p - rep + 4, p + 4, end);
                        offset = rep;
                    }
                    else
                    {
                        uint h = Hash(p, shiftIn, hashShift);
                        int cand = ht[h];
                        ht[h] = pos;
                        if (useLong)
                        {
                            uint hl = Hash8(p, longShift);
                            int candLong = lt[hl];
                            lt[hl] = pos;
                            long distLong = (long)pos - candLong;
                            if (distLong > 0 && distLong <= window && Unsafe.ReadUnaligned<ulong>(src + candLong) == Unsafe.ReadUnaligned<ulong>(p))
                            {
                                matchLen = 8 + MatchLength(src + candLong + 8, p + 8, end);
                                offset = (uint)distLong;
                            }
                        }
                        if (matchLen == 0)
                        {
                            long dist = (long)pos - cand;
                            if (dist > 0 && dist <= window && Unsafe.ReadUnaligned<uint>(src + cand) == Unsafe.ReadUnaligned<uint>(p))
                            {
                                int n = 4 + MatchLength(src + cand + 4, p + 4, end);
                                if (n >= hashBytes)
                                {
                                    matchLen = n; offset = (uint)dist;
                                    if (useLong)
                                    {
                                        // a long match starting one byte later is usually the better deal
                                        uint h1 = Hash8(p + 1, longShift);
                                        int c1 = lt[h1];
                                        lt[h1] = pos + 1;
                                        long d1 = (long)pos + 1 - c1;
                                        if (d1 > 0 && d1 <= window && Unsafe.ReadUnaligned<ulong>(src + c1) == Unsafe.ReadUnaligned<ulong>(p + 1))
                                        {
                                            int n1 = 8 + MatchLength(src + c1 + 8, p + 9, end);
                                            if (n1 > n) { p++; matchLen = n1; offset = (uint)d1; }
                                        }
                                    }
                                }
                            }
                        }
                        // a short match that costs more than its bytes as literals is no match (frequent in image data)
                        if (matchLen != 0 && matchLen <= 8 && LiteralsCheaper(p, matchLen, offset, false)) matchLen = 0;
                        if (matchLen == 0)
                        {
                            // the longer nothing matches, the bigger the steps
                            p += 1 + ((p - anchor) >> 5);
                            continue;
                        }
                        // grow backwards over literals that also match
                        while (p > anchor && p - src > offset && p[-1] == p[-1 - (long)offset]) { p--; matchLen++; }
                    }
                    int litLen = (int)(p - anchor);
                    // short literal runs: one 16 byte copy (both buffers have the slack) instead of a call
                    if (litLen <= 16 && end - anchor >= 16) Unsafe.WriteUnaligned(lit + nlit, Unsafe.ReadUnaligned<System.Runtime.Intrinsics.Vector128<byte>>(anchor));
                    else Buffer.MemoryCopy(anchor, lit + nlit, litLen, litLen);
                    nlit += litLen;
                    sq[nseq].LitLen = litLen;
                    sq[nseq].MatchLen = matchLen;
                    // offset value 1 = "the most recent offset", valid as such only after at least one literal
                    sq[nseq].OffValue = offset == rep && litLen != 0 ? 1 : offset + 3;
                    nseq++;
                    rep = offset;
                    // index two positions inside the match
                    byte* q = p + matchLen - 2;
                    if (q < limit && q > p)
                    {
                        ht[Hash(q, shiftIn, hashShift)] = (int)(q - src);
                        if (useLong) lt[Hash8(q - 1, longShift)] = (int)(q - 1 - src);
                    }
                    if (p + 2 < limit) ht[Hash(p + 2, shiftIn, hashShift)] = (int)(p + 2 - src);
                    p += matchLen;
                    anchor = p;
                }
                int tail = (int)(end - anchor);
                Buffer.MemoryCopy(anchor, lit + nlit, tail, tail);
                nlit += tail;

                byte* dstEnd = dst + capacity;
                int a = EncodeLiterals(lit, nlit, dst, dstEnd - capacity / 2);
                int b = EncodeSequences(nseq, dst + a, dstEnd);
                if (b < 0 || a + b >= length) return 0;
                this.rep = rep;
                return a + b;
            }
        }

        // ------------------------------------------------------------------------- lazy parsing (levels >= 2)

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        uint ChainHash(byte* p) => (uint)(((Unsafe.ReadUnaligned<ulong>(p) << 24) * 0x9E3779B185EBCA87UL) >> (64 - chainHashLog)); // five bytes

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void ChainInsert(byte* src, int pos)
        {
            uint h = ChainHash(src + pos);
            chain[pos & chainMask] = chainHead[h];
            chainHead[h] = pos;
        }

        /// <summary>
        /// Best match at p: the repeat offsets usable with this literal run (cheap to code, so they get a bonus)
        /// and the hash chain. Returns the score (0 = nothing); the position is entered into the chain.
        /// </summary>
        int BestMatch(byte* src, byte* p, byte* end, int window, int depth, int litLen, uint r0, uint r1, uint r2, out int length, out uint offset)
        {
            length = 0; offset = 0;
            int score = 0;
            int pos = (int)(p - src);
            uint first = Unsafe.ReadUnaligned<uint>(p);
            // which offsets have a short code depends on whether literals precede the match
            for (int k = 0; k < 3; k++)
            {
                uint r = litLen != 0 ? (k == 0 ? r0 : k == 1 ? r1 : r2) : (k == 0 ? r1 : k == 1 ? r2 : r0 - 1);
                if (r == 0 || r > (uint)pos || r > (uint)window) continue;
                if (((Unsafe.ReadUnaligned<uint>(p - r) ^ first) & 0xFFFFFF) != 0) continue;
                int n = 3 + MatchLength(p - r + 3, p + 3, end);
                int sc = n * 4 + 4 - k;
                if (sc > score) { score = sc; length = n; offset = r; }
            }
            uint h = ChainHash(p);
            int cand = chainHead[h];
            chain[pos & chainMask] = cand;
            chainHead[h] = pos;
            int low = Math.Max(0, pos - Math.Min(window, chainMask));
            for (int steps = depth; cand >= low && steps > 0 && p + length < end; steps--)
            {
                byte* c = src + cand;
                if (Unsafe.ReadUnaligned<uint>(c) == first && c[length] == p[length])
                {
                    int n = 4 + MatchLength(c + 4, p + 4, end);
                    int sc = n * 4 - BitOperations.Log2((uint)(pos - cand) + 1);
                    if (sc > score) { score = sc; length = n; offset = (uint)(pos - cand); }
                }
                int next = chain[cand & chainMask];
                if (next >= cand) break;
                cand = next;
            }
            return score;
        }

        // would the bytes of a short match be cheaper as literals? (decisive on noisy image data)
        bool LiteralsCheaper(byte* p, int length, uint offset, bool isRep)
        {
            if (length > 8) return false;
            int cost = isRep ? 7 : 13 + BitOperations.Log2(offset + 3);
            int lit = 0;
            for (int i = 0; i < length; i++) lit += litCost[p[i]];
            return lit <= cost;
        }

        int CompressBlockLazy(byte* src, int start, int length, int window, byte* dst, int capacity)
        {
            int nseq = 0, nlit = 0;
            uint r0 = rep0, r1 = rep1, r2 = rep2;
            fixed (Seq* sq = seqs)
            fixed (byte* lit = lits)
            {
                byte* end = src + start + length;
                byte* limit = end - 8;
                byte* p = src + start, anchor = p;
                int misses = 0;
                while (p < limit)
                {
                    int litLen = (int)(p - anchor);
                    // the search budget shrinks while nothing useful is found (noisy data), and recovers at once
                    int depth = Math.Max(searchDepth >> Math.Min(misses >> 5, 4), 2);
                    int score = BestMatch(src, p, end, window, depth, litLen, r0, r1, r2, out int matchLen, out uint offset);
                    if (score != 0 && LiteralsCheaper(p, matchLen, offset, IsRep(offset, litLen, r0, r1, r2))) score = 0;
                    if (score == 0)
                    {
                        // step faster through stretches without matches
                        int step = 1 + (misses++ >> 7);
                        for (byte* q = p + 1; q < p + step && q < limit; q++) ChainInsert(src, (int)(q - src));
                        p += step;
                        continue;
                    }
                    misses = 0;
                    // lazy evaluation: a clearly better match one or two bytes later wins
                    int inserted = (int)(p - src);
                    for (int ahead = 1; ahead <= lazySteps && p + ahead < limit; ahead++)
                    {
                        byte* q = p + ahead;
                        inserted = (int)(q - src);
                        int s2 = BestMatch(src, q, end, window, depth, litLen + ahead, r0, r1, r2, out int len2, out uint off2);
                        if (s2 > score + (ahead == 1 ? 4 : 7) && !LiteralsCheaper(q, len2, off2, IsRep(off2, litLen + ahead, r0, r1, r2)))
                        {
                            p = q; litLen += ahead; score = s2; matchLen = len2; offset = off2;
                            ahead = 0;
                        }
                    }
                    // grow backwards over literals that also match
                    while (p > anchor && p - src > offset && p[-1] == p[-1 - (long)offset]) { p--; matchLen++; litLen--; }

                    Buffer.MemoryCopy(anchor, lit + nlit, litLen, litLen);
                    nlit += litLen;
                    sq[nseq].LitLen = litLen;
                    sq[nseq].MatchLen = matchLen;
                    // offset value and repeat offset history, exactly as the decoder will replay it
                    uint value;
                    if (litLen != 0)
                    {
                        if (offset == r0) value = 1;
                        else if (offset == r1) { value = 2; r1 = r0; r0 = offset; }
                        else if (offset == r2) { value = 3; r2 = r1; r1 = r0; r0 = offset; }
                        else { value = offset + 3; r2 = r1; r1 = r0; r0 = offset; }
                    }
                    else
                    {
                        if (offset == r1) { value = 1; r1 = r0; r0 = offset; }
                        else if (offset == r2) { value = 2; r2 = r1; r1 = r0; r0 = offset; }
                        else if (offset == r0 - 1) { value = 3; r2 = r1; r1 = r0; r0 = offset; }
                        else { value = offset + 3; r2 = r1; r1 = r0; r0 = offset; }
                    }
                    sq[nseq].OffValue = value;
                    nseq++;
                    byte* stop = p + matchLen < limit ? p + matchLen : limit;
                    for (byte* q = src + inserted + 1; q < stop; q++) ChainInsert(src, (int)(q - src));
                    p += matchLen;
                    anchor = p;
                }
                int tail = (int)(end - anchor);
                Buffer.MemoryCopy(anchor, lit + nlit, tail, tail);
                nlit += tail;

                byte* dstEnd = dst + capacity;
                int a = EncodeLiterals(lit, nlit, dst, dstEnd - capacity / 2);
                int b = EncodeSequences(nseq, dst + a, dstEnd);
                if (b < 0 || a + b >= length) return 0;
                rep0 = r0; rep1 = r1; rep2 = r2;
                return a + b;
            }
        }

        // ------------------------------------------------------------------- binary tree match finder

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        uint BtHash(byte* p) => (Unsafe.ReadUnaligned<uint>(p) * 2654435761u) >> (32 - btHashLog);

        /// <summary>
        /// Inserts position pos into the tree of its hash bucket and reports the matches found on the way, each longer than
        /// the one before and than minLen: lengths to outLen, offsets to outOff (at most 64). The tree of a bucket orders
        /// its positions by the bytes that follow them; the new position becomes the root, the walk from the old root
        /// splits the tree into the part before and the part after the new suffix (the common prefix with the nearest
        /// nodes on either side bounds how much of each comparison is already known). Positions before low, and walks
        /// longer than depth, end the search. Returns the number of matches.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        int BtFind(byte* src, int pos, byte* end, int low, int minLen, int depth, int* outLen, uint* outOff)
        {
            byte* p = src + pos;
            int count = 0, best = minLen, nice = btNice;
            int maxLen = (int)(end - p);
            fixed (int* head = btHead, t = tree)
            {
                uint h = BtHash(p);
                int cur = head[h];
                head[h] = pos;
                int* smaller = t + 2 * (pos & btMask), larger = smaller + 1;
                int lenS = 0, lenL = 0;
                while (cur >= low && depth-- > 0)
                {
                    byte* c = src + cur;
                    int len = Math.Min(lenS, lenL);
                    len += MatchLength(c + len, p + len, end);
                    int* node = t + 2 * (cur & btMask);
                    if (len > best)
                    {
                        best = len;
                        if (count < 64) { outLen[count] = len; outOff[count] = (uint)(pos - cur); count++; }
                    }
                    if (len >= nice || len == maxLen)
                    {
                        // equal as far as can be told: the new node takes over cur's subtrees
                        *smaller = node[0]; *larger = node[1];
                        return count;
                    }
                    if (c[len] < p[len]) { *smaller = cur; smaller = node + 1; cur = node[1]; lenS = len; }
                    else { *larger = cur; larger = node; cur = node[0]; lenL = len; }
                }
                *smaller = -1; *larger = -1;
            }
            return count;
        }

        // ---------------------------------------------------------- price based parsing (levels >= 16)

        int[] optPrice = Array.Empty<int>(), optLen = Array.Empty<int>();
        uint[] optOff = Array.Empty<uint>(), optRep = Array.Empty<uint>();

        // bits (x8) to code a match of this length: its length code's extra bits plus a typical code cost
        static int MatchLengthPrice(int len) => 8 * (MlBits[len < 131 ? MlCodeSmall[len] : BitOperations.Log2((uint)len - 3) + 36] + 5);

        /// <summary>
        /// Chooses the sequence of literals and matches with the lowest estimated cost for the whole block (a
        /// shortest path over positions; prices come from the last block's literal code and from the size of the
        /// offsets), instead of deciding match by match.
        /// </summary>
        int CompressBlockOptimal(byte* src, int start, int length, int window, byte* dst, int capacity)
        {
            if (optPrice.Length < BlockSize + 1)
            {
                optPrice = new int[BlockSize + 1]; optLen = new int[BlockSize + 1];
                optOff = new uint[BlockSize + 1]; optRep = new uint[BlockSize + 1];
            }
            const int Sufficient = 256; // a match this long is taken without looking for alternatives
            int nseq = 0, nlit = 0;
            fixed (int* price = optPrice, mlen = optLen)
            fixed (uint* moff = optOff, mrep = optRep)
            fixed (byte* lc = litCost)
            fixed (Seq* sq = seqs)
            fixed (byte* lit = lits)
            {
                int* foundLen = stackalloc int[64];
                uint* foundOff = stackalloc uint[64];
                byte* baseP = src + start;
                int searchEnd = length - 8; // positions that can be hashed
                for (int i = 1; i <= length; i++) price[i] = int.MaxValue;
                price[0] = 0; mlen[0] = 0; mrep[0] = rep0;
                byte* end = baseP + length;
                for (int i = 0; i < length; i++)
                {
                    int here = price[i];
                    // a literal
                    int pl = here + 8 * lc[baseP[i]];
                    if (pl < price[i + 1]) { price[i + 1] = pl; mlen[i + 1] = 0; mrep[i + 1] = mrep[i]; }
                    if (i >= searchEnd) continue;
                    byte* p = baseP + i;
                    int pos = start + i;
                    uint first = Unsafe.ReadUnaligned<uint>(p);
                    int covered = 2; // lengths up to this are already priced with a nearer offset
                    int seqBase = here + 8 * 3; // a new sequence: its literal length code
                    // the offset of the path's last match again
                    uint r = mrep[i];
                    if (r != 0 && r <= (uint)pos && ((Unsafe.ReadUnaligned<uint>(p - r) ^ first) & 0xFFFFFF) == 0)
                    {
                        int n = 3 + MatchLength(p - r + 3, p + 3, end);
                        int from = n >= Sufficient ? n : 3;
                        for (int l = from; l <= n; l++)
                        {
                            int c = seqBase + 8 * 2 + MatchLengthPrice(l);
                            if (c < price[i + l]) { price[i + l] = c; mlen[i + l] = l; moff[i + l] = r; mrep[i + l] = r; }
                        }
                        covered = n;
                    }
                    if (useTree)
                    {
                        int low = Math.Max(0, pos - Math.Min(window, btMask));
                        int found = BtFind(src, pos, end, low, covered, searchDepth, foundLen, foundOff);
                        for (int f = 0; f < found; f++)
                        {
                            int n = foundLen[f];
                            if (n < 4) continue;
                            uint off = foundOff[f];
                            int offPrice = seqBase + 8 * (BitOperations.Log2(off + 3) + 6);
                            int from = n >= Sufficient ? n : Math.Max(covered + 1, 4);
                            for (int l = from; l <= n; l++)
                            {
                                int cst = offPrice + MatchLengthPrice(l);
                                if (cst < price[i + l]) { price[i + l] = cst; mlen[i + l] = l; moff[i + l] = off; mrep[i + l] = off; }
                            }
                            covered = n;
                        }
                    }
                    else
                    {
                        uint h = ChainHash(p);
                        int cand = chainHead[h];
                        chain[pos & chainMask] = cand;
                        chainHead[h] = pos;
                        int low = Math.Max(0, pos - Math.Min(window, chainMask));
                        for (int steps = searchDepth; cand >= low && steps > 0 && covered < length - i; steps--)
                        {
                            byte* c = src + cand;
                            if (Unsafe.ReadUnaligned<uint>(c) == first && c[covered] == p[covered])
                            {
                                int n = 4 + MatchLength(c + 4, p + 4, end);
                                if (n > covered)
                                {
                                    uint off = (uint)(pos - cand);
                                    int offPrice = seqBase + 8 * (BitOperations.Log2(off + 3) + 6);
                                    int from = n >= Sufficient ? n : Math.Max(covered + 1, 4);
                                    for (int l = from; l <= n; l++)
                                    {
                                        int cst = offPrice + MatchLengthPrice(l);
                                        if (cst < price[i + l]) { price[i + l] = cst; mlen[i + l] = l; moff[i + l] = off; mrep[i + l] = off; }
                                    }
                                    covered = n;
                                }
                            }
                            int next = chain[cand & chainMask];
                            if (next >= cand) break;
                            cand = next;
                        }
                    }
                    if (covered >= Sufficient)
                    {
                        // jump over the long match (its end is priced through this position only)
                        int stop = Math.Min(i + covered, searchEnd);
                        for (int q = i + 1; q < stop; q++)
                        {
                            if (useTree) BtFind(src, start + q, end, Math.Max(0, start + q - Math.Min(window, btMask)), int.MaxValue, searchDepth, foundLen, foundOff);
                            else ChainInsert(src, start + q);
                            price[q] = int.MaxValue - (1 << 28);
                        }
                        i += covered - 1;
                    }
                }
                // walk the cheapest path backwards, then emit it front to back
                int count = 0;
                for (int i = length; i > 0;)
                {
                    int l = mlen[i];
                    if (l == 0) { i--; continue; }
                    optStack[count++] = (i - l, l, moff[i]);
                    i -= l;
                }
                uint r0 = rep0, r1 = rep1, r2 = rep2;
                int anchor = 0;
                for (int k = count - 1; k >= 0; k--)
                {
                    var (at, matchLen, offset) = optStack[k];
                    int litLen = at - anchor;
                    Buffer.MemoryCopy(baseP + anchor, lit + nlit, litLen, litLen);
                    nlit += litLen;
                    sq[nseq].LitLen = litLen;
                    sq[nseq].MatchLen = matchLen;
                    sq[nseq].OffValue = OffsetValue(offset, litLen, ref r0, ref r1, ref r2);
                    nseq++;
                    anchor = at + matchLen;
                }
                int tail = length - anchor;
                Buffer.MemoryCopy(baseP + anchor, lit + nlit, tail, tail);
                nlit += tail;

                byte* dstEnd = dst + capacity;
                int a = EncodeLiterals(lit, nlit, dst, dstEnd - capacity / 2);
                int b = EncodeSequences(nseq, dst + a, dstEnd);
                if (b < 0 || a + b >= length) return 0;
                rep0 = r0; rep1 = r1; rep2 = r2;
                return a + b;
            }
        }

        readonly (int at, int len, uint off)[] optStack = new (int, int, uint)[BlockSize / 3 + 8];

        // offset value for the sequence, and the repeat offset history exactly as the decoder will replay it
        static uint OffsetValue(uint offset, int litLen, ref uint r0, ref uint r1, ref uint r2)
        {
            uint value;
            if (litLen != 0)
            {
                if (offset == r0) return 1;
                if (offset == r1) { value = 2; r1 = r0; r0 = offset; return value; }
                value = offset == r2 ? 3 : offset + 3;
            }
            else
            {
                if (offset == r1) { r1 = r0; r0 = offset; return 1; }
                value = offset == r2 ? 2 : offset == r0 - 1 ? 3 : offset + 3;
            }
            r2 = r1; r1 = r0; r0 = offset;
            return value;
        }

        static bool IsRep(uint offset, int litLen, uint r0, uint r1, uint r2)
            => litLen != 0 ? offset == r0 || offset == r1 || offset == r2 : offset == r1 || offset == r2 || offset == r0 - 1;

        [ThreadStatic] static ZstdEncoder? cached;

        public static byte[] Compress(ReadOnlySpan<byte> source, int level, bool checksum, ReadOnlySpan<byte> dictionary = default)
            => (cached ??= new ZstdEncoder()).Run(source, level, checksum, dictionary);

        byte[] Run(ReadOnlySpan<byte> content, int level, bool checksum, ReadOnlySpan<byte> dictionary)
        {
            // With a dictionary the encoder works on "dictionary content + input" as one buffer: the content is
            // history that matches may reach, exactly as the decoder will see it.
            ReadOnlySpan<byte> source = content;
            int start = 0;
            uint dictionaryId = 0;
            bool structured = false;
            if (!dictionary.IsEmpty)
            {
                structured = verifier.ParseDictionary(dictionary, out dictionaryId, out int contentStart);
                var history = dictionary.Slice(contentStart);
                if ((long)history.Length + content.Length > Array.MaxLength) throw new ArgumentException("input too large to combine with the dictionary", nameof(content));
                var all = new byte[history.Length + content.Length];
                history.CopyTo(all);
                content.CopyTo(all.AsSpan(history.Length));
                source = all;
                start = history.Length;
            }
            // the output grows as needed: compressible input near 2 GB must not need a 2 GB buffer up front
            long bound = 22L + content.Length + 3L * (content.Length / BlockSize + 1) + 4;
            var output = new byte[Math.Min(bound, Math.Max(content.Length / 2 + 4096, 4 * BlockSize))];
            int o = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(output, 0xFD2FB528);
            o = 4;
            // level -> search effort and window
            // 1, 2: one hash (6 / 5 bytes); 3: two hashes (5 and 8 bytes); 4 and up: hash chains with lazy matching
            bool fast = level <= 3;
            hashBytes = level <= 2 ? 6 : 5;
            useLong = level == 3;
            // small tables and windows keep the fast levels inside the CPU caches
            int maxWindowLog = level <= 1 ? 19 : level <= 3 ? 21 : level <= 5 ? 23 : level <= 12 ? 25 : 27;
            hashShift = 64 - (level <= 1 ? 14 : level == 2 ? 16 : 17);
            longShift = 64 - MaxHashLog;
            searchDepth = level <= 4 ? 4 : level == 5 ? 8 : level == 6 ? 16 : level <= 8 ? 32 : level <= 12 ? 64 : level <= 17 ? 128 : 512;
            lazySteps = level <= 6 ? 1 : 2;
            int windowLog = Math.Clamp(source.Length <= 1 ? 10 : BitOperations.Log2((uint)source.Length - 1) + 1, 10, maxWindowLog);
            int window = 1 << windowLog;
            // frame header: 8 byte content size, optional checksum, 4 byte dictionary id for a structured dictionary
            output[o++] = (byte)(0xC0 | (checksum ? 0x04 : 0) | (dictionaryId != 0 ? 3 : 0));
            output[o++] = (byte)((windowLog - 10) << 3);
            if (dictionaryId != 0) { BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(o), dictionaryId); o += 4; }
            BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(o), (ulong)content.Length);
            o += 8;

            if (fast)
            {
                hash.AsSpan().Fill(int.MinValue / 2);
                if (useLong)
                {
                    if (longHash.Length == 0) longHash = new int[1 << MaxHashLog];
                    longHash.AsSpan().Fill(int.MinValue / 2);
                }
            }
            else
            {
                // tables sized to the input: a small strip should not pay for megabytes of tables
                chainHashLog = Math.Clamp(windowLog, 12, level <= 5 ? 17 : 20);
                int chainSize = 1 << Math.Min(windowLog, level <= 5 ? 20 : level <= 12 ? 22 : 24);
                if (chainHead.Length != 1 << chainHashLog) chainHead = new int[1 << chainHashLog];
                if (chain.Length != chainSize) chain = new int[chainSize];
                chainMask = chainSize - 1;
                chainHead.AsSpan().Fill(-1);
                useTree = level >= 16;
                if (useTree)
                {
                    btHashLog = Math.Clamp(windowLog, 12, 20);
                    int nodes = 1 << Math.Min(windowLog, 23);
                    if (btHead.Length != 1 << btHashLog) btHead = new int[1 << btHashLog];
                    if (tree.Length != 2 * nodes) tree = new int[2 * nodes];
                    btMask = nodes - 1;
                    btHead.AsSpan().Fill(-1);
                    // a tree walk finds better matches than a chain walk of the same length: shorter walks do
                    btNice = level >= 18 ? 256 : level == 17 ? 128 : 64;
                    searchDepth = level >= 19 ? 256 : level == 18 ? 128 : level == 17 ? 64 : 48;
                }
            }
            litCost.AsSpan().Fill(8);
            rep = 1; rep0 = 1; rep1 = 4; rep2 = 8;
            if (structured)
            {
                // the decoder starts with the dictionary's repeat offsets
                rep0 = verifier.DictionaryRepeatOffset(0); rep1 = verifier.DictionaryRepeatOffset(1); rep2 = verifier.DictionaryRepeatOffset(2);
                rep = rep0;
            }
            fixed (byte* src = source, wk = work)
            {
                // index the dictionary content
                if (fast)
                {
                    fixed (int* ht = hash, lt = longHash)
                        for (int i = 0; i + 8 <= start; i++)
                        {
                            ht[Hash(src + i, 64 - 8 * hashBytes, hashShift)] = i;
                            if (useLong) lt[Hash8(src + i, longShift)] = i;
                        }
                }
                else if (useTree)
                {
                    int* fl = stackalloc int[64];
                    uint* fo = stackalloc uint[64];
                    for (int i = 0; i + 8 <= start; i++) BtFind(src, i, src + start, Math.Max(0, i - Math.Min(window, btMask)), int.MaxValue, searchDepth, fl, fo);
                }
                else for (int i = 0; i + 8 <= start; i++) ChainInsert(src, i);
                int pos = start;
                do
                {
                    int n = Math.Min(BlockSize, source.Length - pos);
                    if (output.Length - o < n + 16)
                    {
                        long want = Math.Min(Math.Max((long)output.Length * 2, (long)o + n + 16), Array.MaxLength);
                        if (want - o < n + 16) throw new System.IO.IOException("compressed output too large");
                        Array.Resize(ref output, (int)want);
                    }
                    fixed (byte* dst = output)
                    {
                    bool last = pos + n == source.Length;
                    int type, size, payload;
                    bool same = n > 0;
                    for (int i = 1; i < n && same; i++) same = src[pos + i] == src[pos];
                    if (same && n > 1)
                    {
                        type = 1; size = n; payload = 1;
                        dst[o + 3] = src[pos];
                        // an RLE block changes the decoder's history, not its repeat offsets
                    }
                    else
                    {
                        int c = n < 16 ? 0 : fast ? CompressBlock(src, pos, n, window, wk, WorkSize)
                            : level >= 16 ? CompressBlockOptimal(src, pos, n, window, wk, WorkSize) : CompressBlockLazy(src, pos, n, window, wk, WorkSize);
                        if (c > 0)
                        {
                            type = 2; size = c; payload = c;
                            Buffer.MemoryCopy(wk, dst + o + 3, c, c);
                        }
                        else
                        {
                            type = 0; size = n; payload = n;
                            Buffer.MemoryCopy(src + pos, dst + o + 3, n, n);
                        }
                    }
                    int header = (last ? 1 : 0) | type << 1 | size << 3;
                    dst[o] = (byte)header; dst[o + 1] = (byte)(header >> 8); dst[o + 2] = (byte)(header >> 16);
                    o += 3 + payload;
                    pos += n;
                    }
                } while (pos < source.Length);
            }
            if (checksum)
            {
                if (output.Length - o < 4) Array.Resize(ref output, o + 4);
                BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(o), (uint)Checksums.XxHash64(content));
                o += 4;
            }
            return output.AsSpan(0, o).ToArray();
        }
    }
}
