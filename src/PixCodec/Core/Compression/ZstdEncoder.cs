using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace PixCodec.Compression
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
        const int BlockSize = 128 * 1024, HashLog = 17, MinMatch = 4, MaxWindowLog = 23;
        const int WorkSize = 4 * BlockSize + 4096;

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

        readonly int[] hash = new int[1 << HashLog];
        readonly Seq[] seqs = new Seq[BlockSize / MinMatch + 8];
        readonly byte[] lits = new byte[BlockSize + 64];
        readonly byte[] work = new byte[WorkSize];
        readonly byte[] llCodes = new byte[BlockSize / MinMatch + 8], mlCodes = new byte[BlockSize / MinMatch + 8], ofCodes = new byte[BlockSize / MinMatch + 8];
        readonly CTable llTable = new CTable(), mlTable = new CTable(), ofTable = new CTable(), weightTable = new CTable();
        readonly ZstdDecoder verifier = new ZstdDecoder();
        uint rep; // the most recent match offset, as the decoder will see it

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
            var w = new BitWriter { Pos = dst, End = dstEnd };
            for (int i = n - 1; i >= 0; i--) w.Put(codes[lit[i]], lens[lit[i]]);
            w.Finish();
            return w.Overflow ? -1 : (int)(w.Pos - dst);
        }

        /// <summary>Writes the literals section for lit[0, n). Always succeeds (raw literals are the fallback).</summary>
        int EncodeLiterals(byte* lit, int n, byte* dst, byte* dstEnd)
        {
            if (n < 64) return RawLiterals(lit, n, dst, 0, n);
            uint* freq = stackalloc uint[256];
            for (int i = 0; i < 256; i++) freq[i] = 0;
            for (int i = 0; i < n; i++) freq[lit[i]]++;
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
                var w = new BitWriter { Pos = o, End = dstEnd };
                int last = nseq - 1;
                uint sLl = InitState(llTable, ll[last]), sOf = InitState(ofTable, of[last]), sMl = InitState(mlTable, ml[last]);
                for (int i = last; ; i--)
                {
                    int lc = ll[i], mc = ml[i], oc = of[i];
                    w.Put((uint)sq[i].LitLen - LlBase[lc], LlBits[lc]);
                    w.Put((uint)sq[i].MatchLen - MlBase[mc], MlBits[mc]);
                    w.Put(sq[i].OffValue - (1u << oc), oc);
                    if (i == 0) break;
                    // transitions into sequence i's states, read as: literal length, match length, offset
                    EncodeSymbol(ref w, ofTable, ref sOf, of[i - 1]);
                    EncodeSymbol(ref w, mlTable, ref sMl, ml[i - 1]);
                    EncodeSymbol(ref w, llTable, ref sLl, ll[i - 1]);
                }
                w.Put(sMl - (1u << mlTable.Log), mlTable.Log);
                w.Put(sOf - (1u << ofTable.Log), ofTable.Log);
                w.Put(sLl - (1u << llTable.Log), llTable.Log);
                w.Finish();
                if (w.Overflow) return -1;
                o = w.Pos;
            }
            return (int)(o - dst);
        }

        // ------------------------------------------------------------------------------------------ blocks

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static uint Hash(byte* p) => (uint)(((Unsafe.ReadUnaligned<ulong>(p) << 24) * 0x9E3779B185EBCA87UL) >> (64 - HashLog));

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
        /// Compresses src[start, start + length) (history: everything before start within the window) into dst.
        /// Returns the size, or 0 when the block is better stored raw.
        /// </summary>
        int CompressBlock(byte* src, int start, int length, int window, byte* dst, int capacity)
        {
            int nseq = 0, nlit = 0;
            uint rep = this.rep;
            fixed (int* ht = hash)
            fixed (Seq* sq = seqs)
            fixed (byte* lit = lits)
            {
                byte* end = src + start + length;
                byte* limit = end - 8; // the hash reads eight bytes
                byte* p = src + start, anchor = p;
                int misses = 0;
                while (p < limit)
                {
                    int matchLen = 0;
                    uint offset = 0;
                    // the previous offset again? (cheap to code)
                    if (p - src >= rep && p > anchor && Unsafe.ReadUnaligned<uint>(p) == Unsafe.ReadUnaligned<uint>(p - rep))
                    {
                        matchLen = 4 + MatchLength(p - rep + 4, p + 4, end);
                        offset = rep;
                    }
                    uint h = Hash(p);
                    int cand = ht[h];
                    ht[h] = (int)(p - src);
                    if (matchLen == 0)
                    {
                        long dist = p - src - cand;
                        if (dist > 0 && dist <= window && Unsafe.ReadUnaligned<uint>(src + cand) == Unsafe.ReadUnaligned<uint>(p))
                        {
                            matchLen = 4 + MatchLength(src + cand + 4, p + 4, end);
                            offset = (uint)dist;
                            // grow backwards over literals that also match
                            while (p > anchor && src + cand > src && p[-1] == p[-1 - (long)dist]) { p--; cand--; matchLen++; }
                        }
                    }
                    if (matchLen == 0)
                    {
                        p += 1 + (misses++ >> 6);
                        continue;
                    }
                    misses = 0;
                    int litLen = (int)(p - anchor);
                    Buffer.MemoryCopy(anchor, lit + nlit, litLen, litLen);
                    nlit += litLen;
                    sq[nseq].LitLen = litLen;
                    sq[nseq].MatchLen = matchLen;
                    // offset value 1 = "the most recent offset", valid as such only after at least one literal
                    sq[nseq].OffValue = offset == rep && litLen != 0 ? 1 : offset + 3;
                    nseq++;
                    rep = offset;
                    // index a couple of positions inside the match
                    byte* q = p + matchLen - 2;
                    if (q < limit && q > p) ht[Hash(q)] = (int)(q - src);
                    if (p + 1 < limit) ht[Hash(p + 1)] = (int)(p + 1 - src);
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

        // The repeat offset rule has one more twist the block coder must respect: an "offset value" of 1 with a
        // literal length of zero means the *second* most recent offset. CompressBlock never emits 1 in that case.

        [ThreadStatic] static ZstdEncoder? cached;

        public static byte[] Compress(ReadOnlySpan<byte> source, bool checksum) => (cached ??= new ZstdEncoder()).Run(source, checksum);

        byte[] Run(ReadOnlySpan<byte> source, bool checksum)
        {
            long bound = 18L + source.Length + 3L * (source.Length / BlockSize + 1) + 4;
            if (bound > Array.MaxLength) throw new ArgumentException("input too large", nameof(source));
            var output = new byte[bound];
            int o = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(output, 0xFD2FB528);
            o = 4;
            // window descriptor + 8 byte content size; no dictionary id
            int windowLog = Math.Clamp(source.Length <= 1 ? 10 : BitOperations.Log2((uint)source.Length - 1) + 1, 10, MaxWindowLog);
            int window = 1 << windowLog;
            output[o++] = (byte)(0xC0 | (checksum ? 0x04 : 0));
            output[o++] = (byte)((windowLog - 10) << 3);
            BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(o), (ulong)source.Length);
            o += 8;

            hash.AsSpan().Fill(int.MinValue / 2);
            rep = 1;
            fixed (byte* src = source, wk = work, dst = output)
            {
                int pos = 0;
                do
                {
                    int n = Math.Min(BlockSize, source.Length - pos);
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
                        int c = n >= 16 ? CompressBlock(src, pos, n, window, wk, WorkSize) : 0;
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
                } while (pos < source.Length);
            }
            if (checksum)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(o), (uint)Checksums.XxHash64(source));
                o += 4;
            }
            return output.AsSpan(0, o).ToArray();
        }
    }
}
