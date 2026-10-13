using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace PixCodec.Compression
{
    /// <summary>Result of one <see cref="Inflater.Inflate"/> call.</summary>
    public enum InflateStatus
    {
        /// <summary>All input was consumed and the stream is not finished: supply more input.</summary>
        NeedInput,
        /// <summary>The output span is full and more output is (or may be) pending: call again with more room.</summary>
        NeedOutput,
        /// <summary>The end of the stream was reached and all output has been delivered.</summary>
        Done,
    }

    /// <summary>
    /// The resumable INFLATE (RFC 1951) state machine, optionally with the zlib (RFC 1950) wrapper. It decodes
    /// from an input range into an output range whose preceding bytes (from <c>outBase</c>) are the history that
    /// back references may reach. The caller decides whether that is the final destination (one-shot decoding)
    /// or a sliding window (<see cref="Inflater"/>).
    /// </summary>
    internal sealed unsafe class InflateCore
    {
        // Decode table entry (uint):
        //   bits 0..7   number of bits to drop from the bit buffer (code length plus extra bits)
        //   bits 8..12  code length alone (sub table pointers: index bits of the sub table)
        //   bit 13 Sub, bit 14 Exceptional (sub table pointer, end of block or invalid), bit 15 Literal
        //   bits 16..31 literal byte / length or distance base / sub table start / 0 = end of block, 1 = invalid
        const int LitBits = 11, DistBits = 9, CodeBits = 7;
        const uint Lit = 0x8000, Exc = 0x4000, Sub = 0x2000;
        const int LitTableSize = (1 << LitBits) + 288 * 16, DistTableSize = (1 << DistBits) + 32 * 64;
        // the fast loop runs while at least this much input and output slack is left
        const int FastIn = 16, FastOut = 3 + 258 + 64;

        enum State { Header, BlockHeader, StoredHeader, StoredCopy, DynCounts, DynCodeLens, DynLens, Symbols, Match, Trailer, Done }

        static readonly ushort[] LenBase = { 3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258 };
        static readonly byte[] LenExtra = { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0 };
        static readonly ushort[] DistBase = { 1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577 };
        static readonly byte[] DistExtra = { 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13 };
        static readonly byte[] CodeOrder = { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };
        // advance per 8 byte store that keeps a repeating pattern of period d in phase
        static readonly byte[] PatternStep = { 0, 8, 8, 6, 8, 5, 6, 7 };

        static readonly uint[] FixedLit = new uint[1 << LitBits];
        static readonly uint[] FixedDist = new uint[1 << DistBits];

        static InflateCore()
        {
            byte* l = stackalloc byte[288];
            for (int i = 0; i < 288; i++) l[i] = (byte)(i < 144 ? 8 : i < 256 ? 9 : i < 280 ? 7 : 8);
            fixed (uint* t = FixedLit) BuildTable(l, 288, LitBits, t, FixedLit.Length, 0);
            for (int i = 0; i < 32; i++) l[i] = 5;
            fixed (uint* t = FixedDist) BuildTable(l, 32, DistBits, t, FixedDist.Length, 1);
        }

        readonly uint[] dynLit = new uint[LitTableSize];
        readonly uint[] dynDist = new uint[DistTableSize];
        readonly uint[] codeTable = new uint[1 << CodeBits];
        readonly byte[] lens = new byte[320];

        State state;
        bool zlib, final, useFixed;
        ulong bitBuf;
        int bitCount;
        int storedLeft, hlit, hdist, hclen, index;
        int pendLen, pendDist;

        /// <summary>The Adler-32 stored in the zlib trailer (valid once the stream is done and has a zlib wrapper).</summary>
        public uint TrailerAdler { get; private set; }
        public bool IsDone => state == State.Done;

        public void Reset(bool zlibWrapper)
        {
            zlib = zlibWrapper;
            state = zlibWrapper ? State.Header : State.BlockHeader;
            final = false; useFixed = false;
            bitBuf = 0; bitCount = 0;
            storedLeft = 0; pendLen = 0; pendDist = 0; index = 0;
            TrailerAdler = 0;
        }

        static Exception Bad(string what) => new ImageFormatException("corrupt deflate data: " + what);

        static uint Reverse(uint v, int bits)
        {
            v = ((v & 0x5555) << 1) | ((v >> 1) & 0x5555);
            v = ((v & 0x3333) << 2) | ((v >> 2) & 0x3333);
            v = ((v & 0x0F0F) << 4) | ((v >> 4) & 0x0F0F);
            v = ((v & 0x00FF) << 8) | ((v >> 8) & 0x00FF);
            return v >> (16 - bits);
        }

        // kind: 0 literal/length, 1 distance, 2 code lengths. len is the code length as seen by the table level.
        static uint MakeEntry(int kind, int sym, int len)
        {
            if (kind == 2) return (uint)(sym << 16 | len);
            if (kind == 1)
                return sym < 30 ? (uint)(DistBase[sym] << 16 | len << 8 | (len + DistExtra[sym])) : Exc | 1u << 16 | (uint)len;
            if (sym < 256) return Lit | (uint)(sym << 16 | len << 8 | len);
            if (sym == 256) return Exc | (uint)len;
            if (sym < 286) return (uint)(LenBase[sym - 257] << 16 | len << 8 | (len + LenExtra[sym - 257]));
            return Exc | 1u << 16 | (uint)len;
        }

        /// <summary>Builds a two level canonical Huffman decode table. Returns false for an invalid code.</summary>
        static bool BuildTable(byte* codeLens, int n, int primaryBits, uint* table, int capacity, int kind)
        {
            int* count = stackalloc int[16];
            for (int i = 0; i < 16; i++) count[i] = 0;
            for (int i = 0; i < n; i++) count[codeLens[i]]++;
            int codes = n - count[0];
            int left = 1, maxLen = 0;
            for (int len = 1; len <= 15; len++)
            {
                left = (left << 1) - count[len];
                if (left < 0) return false; // over-subscribed
                if (count[len] != 0) maxLen = len;
            }
            int primarySize = 1 << primaryBits;
            if (left > 0)
            {
                // incomplete: only tolerated for "no codes" (distances only) and "one code of length one"
                if (kind == 2 || !(codes == 0 && kind == 1 || codes == 1 && count[1] == 1)) return false;
                for (int i = 0; i < primarySize; i++) table[i] = Exc | 1u << 16 | 1;
            }

            uint* next = stackalloc uint[16];
            uint code = 0;
            count[0] = 0;
            for (int len = 1; len <= 15; len++) { code = (code + (uint)count[len - 1]) << 1; next[len] = code; }

            if (maxLen > primaryBits)
            {
                // size each sub table by the longest code that shares its primary prefix
                byte* prefMax = stackalloc byte[primarySize];
                new Span<byte>(prefMax, primarySize).Clear();
                uint* nx = stackalloc uint[16];
                for (int len = primaryBits + 1; len <= 15; len++) nx[len] = next[len];
                for (int sym = 0; sym < n; sym++)
                {
                    int len = codeLens[sym];
                    if (len <= primaryBits) continue;
                    uint pre = Reverse(nx[len]++, len) & (uint)(primarySize - 1);
                    if (len > prefMax[pre]) prefMax[pre] = (byte)len;
                }
                int free = primarySize;
                for (int pre = 0; pre < primarySize; pre++)
                {
                    if (prefMax[pre] == 0) continue;
                    int bits = prefMax[pre] - primaryBits;
                    if (free + (1 << bits) > capacity) return false;
                    table[pre] = Exc | Sub | (uint)(free << 16 | bits << 8 | primaryBits);
                    free += 1 << bits;
                }
            }

            for (int sym = 0; sym < n; sym++)
            {
                int len = codeLens[sym];
                if (len == 0) continue;
                uint r = Reverse(next[len]++, len);
                if (len <= primaryBits)
                {
                    uint e = MakeEntry(kind, sym, len);
                    for (uint i = r; i < primarySize; i += 1u << len) table[i] = e;
                }
                else
                {
                    uint pe = table[r & (uint)(primarySize - 1)];
                    uint start = pe >> 16, size = 1u << (int)((pe >> 8) & 0x1F);
                    int subLen = len - primaryBits;
                    uint e = MakeEntry(kind, sym, subLen);
                    for (uint i = r >> primaryBits; i < size; i += 1u << subLen) table[start + i] = e;
                }
            }
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Refill(ref ulong bb, ref int bc, ref byte* inp, byte* inEnd)
        {
            // never fills to 64: the fast loop's refill needs bc <= 63
            while (bc < 56 && inp < inEnd) { bb |= (ulong)*inp++ << bc; bc += 8; }
        }

        /// <summary>
        /// Decodes as much as input and output room allow. History for back references is [outBase, outStart).
        /// Never reads outside [inStart, inEnd) nor writes outside [outStart, outEnd).
        /// </summary>
        public InflateStatus Run(byte* inStart, byte* inEnd, byte* outBase, byte* outStart, byte* outEnd, out int consumed, out int written)
        {
            byte* inp = inStart, outp = outStart;
            ulong bb = bitBuf;
            int bc = bitCount;
            InflateStatus result;
            const ulong LMask = (1 << LitBits) - 1, DMask = (1 << DistBits) - 1;

            fixed (uint* fixedLit = &FixedLit[0], fixedDist = &FixedDist[0], dynLitP = &dynLit[0], dynDistP = &dynDist[0], ct = &codeTable[0])
            fixed (byte* lensP = &lens[0])
            fixed (byte* patternStep = &PatternStep[0])
            {
                uint* lt = useFixed ? fixedLit : dynLitP, dt = useFixed ? fixedDist : dynDistP;
                for (; ; )
                {
                    switch (state)
                    {
                        case State.Header:
                        {
                            Refill(ref bb, ref bc, ref inp, inEnd);
                            if (bc < 16) goto needInput;
                            uint cmf = (uint)bb & 0xFF, flg = (uint)(bb >> 8) & 0xFF;
                            if ((cmf & 0x0F) != 8 || (cmf >> 4) > 7 || ((cmf << 8) | flg) % 31 != 0) throw Bad("bad zlib header");
                            if ((flg & 0x20) != 0) throw Bad("preset dictionary required");
                            bb >>= 16; bc -= 16;
                            state = State.BlockHeader;
                            break;
                        }
                        case State.BlockHeader:
                        {
                            if (final) { state = zlib ? State.Trailer : State.Done; break; }
                            Refill(ref bb, ref bc, ref inp, inEnd);
                            if (bc < 3) goto needInput;
                            final = (bb & 1) != 0;
                            uint type = (uint)(bb >> 1) & 3;
                            bb >>= 3; bc -= 3;
                            if (type == 0) state = State.StoredHeader;
                            else if (type == 1) { useFixed = true; lt = fixedLit; dt = fixedDist; state = State.Symbols; }
                            else if (type == 2) state = State.DynCounts;
                            else throw Bad("invalid block type");
                            break;
                        }
                        case State.StoredHeader:
                        {
                            bb >>= bc & 7; bc &= ~7;
                            Refill(ref bb, ref bc, ref inp, inEnd);
                            if (bc < 32) goto needInput;
                            uint len = (uint)bb & 0xFFFF, nlen = (uint)(bb >> 16) & 0xFFFF;
                            if (len != (nlen ^ 0xFFFF)) throw Bad("stored block length mismatch");
                            bb >>= 32; bc -= 32;
                            storedLeft = (int)len;
                            state = State.StoredCopy;
                            break;
                        }
                        case State.StoredCopy:
                        {
                            while (storedLeft > 0 && bc >= 8)
                            {
                                if (outp == outEnd) goto needOutput;
                                *outp++ = (byte)bb; bb >>= 8; bc -= 8; storedLeft--;
                            }
                            if (storedLeft > 0)
                            {
                                int n = (int)Math.Min(Math.Min(storedLeft, inEnd - inp), outEnd - outp);
                                Buffer.MemoryCopy(inp, outp, n, n);
                                inp += n; outp += n; storedLeft -= n;
                                if (storedLeft > 0) { if (outp == outEnd) goto needOutput; goto needInput; }
                            }
                            state = State.BlockHeader;
                            break;
                        }
                        case State.DynCounts:
                        {
                            Refill(ref bb, ref bc, ref inp, inEnd);
                            if (bc < 14) goto needInput;
                            hlit = (int)(bb & 31) + 257; hdist = (int)((bb >> 5) & 31) + 1; hclen = (int)((bb >> 10) & 15) + 4;
                            if (hlit > 286 || hdist > 30) throw Bad("too many length or distance codes");
                            bb >>= 14; bc -= 14;
                            new Span<byte>(lensP, 19).Clear();
                            index = 0;
                            state = State.DynCodeLens;
                            break;
                        }
                        case State.DynCodeLens:
                        {
                            while (index < hclen)
                            {
                                Refill(ref bb, ref bc, ref inp, inEnd);
                                if (bc < 3) goto needInput;
                                lensP[CodeOrder[index++]] = (byte)(bb & 7);
                                bb >>= 3; bc -= 3;
                            }
                            if (!BuildTable(lensP, 19, CodeBits, ct, 1 << CodeBits, 2)) throw Bad("invalid code length code");
                            index = 0;
                            state = State.DynLens;
                            break;
                        }
                        case State.DynLens:
                        {
                            int total = hlit + hdist;
                            while (index < total)
                            {
                                Refill(ref bb, ref bc, ref inp, inEnd);
                                uint e = ct[bb & ((1 << CodeBits) - 1)];
                                int l = (int)(e & 0xFF), sym = (int)(e >> 16);
                                if (l > bc) goto needInput;
                                if (sym < 16) { bb >>= l; bc -= l; lensP[index++] = (byte)sym; continue; }
                                int extra = sym == 16 ? 2 : sym == 17 ? 3 : 7;
                                if (l + extra > bc) goto needInput;
                                int rep = (int)((bb >> l) & ((1u << extra) - 1)) + (sym == 18 ? 11 : 3);
                                bb >>= l + extra; bc -= l + extra;
                                byte v = 0;
                                if (sym == 16)
                                {
                                    if (index == 0) throw Bad("repeat without a previous length");
                                    v = lensP[index - 1];
                                }
                                if (index + rep > total) throw Bad("code lengths overrun");
                                for (; rep > 0; rep--) lensP[index++] = v;
                            }
                            if (lensP[256] == 0) throw Bad("missing end of block code");
                            if (!BuildTable(lensP, hlit, LitBits, dynLitP, LitTableSize, 0)) throw Bad("invalid literal/length code");
                            if (!BuildTable(lensP + hlit, hdist, DistBits, dynDistP, DistTableSize, 1)) throw Bad("invalid distance code");
                            useFixed = false; lt = dynLitP; dt = dynDistP;
                            state = State.Symbols;
                            break;
                        }
                        case State.Symbols:
                        {
                            if (inEnd - inp >= FastIn && outEnd - outp >= FastOut)
                            {
                                byte* inFast = inEnd - FastIn, outFast = outEnd - FastOut;
                                bool endOfBlock = false;
                                do
                                {
                                    bb |= Unsafe.ReadUnaligned<ulong>(inp) << bc;
                                    inp += (63 - bc) >> 3;
                                    bc |= 56;
                                    uint e = lt[bb & LMask];
                                    if ((e & Lit) != 0)
                                    {
                                        bb >>= (int)e; bc -= (int)(e & 0xFF);
                                        *outp++ = (byte)(e >> 16);
                                        e = lt[bb & LMask];
                                        if ((e & Lit) != 0)
                                        {
                                            bb >>= (int)e; bc -= (int)(e & 0xFF);
                                            *outp++ = (byte)(e >> 16);
                                            e = lt[bb & LMask];
                                            if ((e & Lit) != 0)
                                            {
                                                bb >>= (int)e; bc -= (int)(e & 0xFF);
                                                *outp++ = (byte)(e >> 16);
                                                continue;
                                            }
                                        }
                                        // a length + distance pair needs up to 48 bits
                                        bb |= Unsafe.ReadUnaligned<ulong>(inp) << bc;
                                        inp += (63 - bc) >> 3;
                                        bc |= 56;
                                    }
                                    if ((e & Exc) != 0)
                                    {
                                        if ((e & Sub) != 0)
                                        {
                                            bb >>= LitBits; bc -= LitBits;
                                            e = lt[(e >> 16) + ((uint)bb & ((1u << (int)((e >> 8) & 0x1F)) - 1))];
                                            if ((e & Lit) != 0)
                                            {
                                                bb >>= (int)e; bc -= (int)(e & 0xFF);
                                                *outp++ = (byte)(e >> 16);
                                                continue;
                                            }
                                        }
                                        if ((e & Exc) != 0)
                                        {
                                            if ((e >> 16) != 0) throw Bad("invalid literal/length code");
                                            bb >>= (int)e; bc -= (int)(e & 0xFF);
                                            endOfBlock = true;
                                            break;
                                        }
                                    }
                                    ulong saved = bb;
                                    int total = (int)(e & 0xFF);
                                    bb >>= total; bc -= total;
                                    uint len = (e >> 16) + (uint)((saved & ((1UL << total) - 1)) >> (int)((e >> 8) & 0x1F));

                                    uint d = dt[bb & DMask];
                                    if ((d & Exc) != 0)
                                    {
                                        if ((d & Sub) == 0) throw Bad("invalid distance code");
                                        bb >>= DistBits; bc -= DistBits;
                                        d = dt[(d >> 16) + ((uint)bb & ((1u << (int)((d >> 8) & 0x1F)) - 1))];
                                        if ((d & Exc) != 0) throw Bad("invalid distance code");
                                    }
                                    saved = bb;
                                    total = (int)(d & 0xFF);
                                    bb >>= total; bc -= total;
                                    uint dist = (d >> 16) + (uint)((saved & ((1UL << total) - 1)) >> (int)((d >> 8) & 0x1F));
                                    if (dist > (ulong)(outp - outBase)) throw Bad("distance too far back");

                                    byte* src = outp - dist, end = outp + len;
                                    if (dist >= 16)
                                    {
                                        do
                                        {
                                            Unsafe.WriteUnaligned(outp, Unsafe.ReadUnaligned<Vector128<byte>>(src));
                                            outp += 16; src += 16;
                                        } while (outp < end);
                                    }
                                    else if (dist >= 8)
                                    {
                                        do
                                        {
                                            Unsafe.WriteUnaligned(outp, Unsafe.ReadUnaligned<ulong>(src));
                                            outp += 8; src += 8;
                                        } while (outp < end);
                                    }
                                    else
                                    {
                                        // widen the period-dist pattern to 8 bytes and store it in phase
                                        ulong v = Unsafe.ReadUnaligned<ulong>(src);
                                        int step;
                                        if (dist == 1) { v = (v & 0xFF) * 0x0101010101010101UL; step = 8; }
                                        else
                                        {
                                            int s = (int)dist * 8;
                                            v &= (1UL << s) - 1;
                                            for (; s < 64; s <<= 1) v |= v << s;
                                            step = patternStep[dist];
                                        }
                                        do
                                        {
                                            Unsafe.WriteUnaligned(outp, v);
                                            outp += step;
                                        } while (outp < end);
                                    }
                                    outp = end;
                                } while (inp <= inFast && outp <= outFast);
                                bb &= (1UL << bc) - 1; // drop the look-ahead bits of the branch-free refill
                                if (endOfBlock) { state = State.BlockHeader; break; }
                            }

                            // careful path: one symbol, committed only when all of its bits are available
                            {
                                Refill(ref bb, ref bc, ref inp, inEnd);
                                ulong tb = bb;
                                int used = 0;
                                uint e = lt[tb & LMask];
                                if ((e & (Exc | Sub)) == (Exc | Sub))
                                {
                                    used = LitBits; tb >>= LitBits;
                                    e = lt[(e >> 16) + ((uint)tb & ((1u << (int)((e >> 8) & 0x1F)) - 1))];
                                }
                                int n = (int)(e & 0xFF);
                                if (used + n > bc) goto needInput;
                                if ((e & Lit) != 0)
                                {
                                    if (outp == outEnd) goto needOutput;
                                    *outp++ = (byte)(e >> 16);
                                    bb = tb >> n; bc -= used + n;
                                    break;
                                }
                                if ((e & Exc) != 0)
                                {
                                    if ((e >> 16) != 0) throw Bad("invalid literal/length code");
                                    bb = tb >> n; bc -= used + n;
                                    state = State.BlockHeader;
                                    break;
                                }
                                int len = (int)((e >> 16) + (uint)((tb & ((1UL << n) - 1)) >> (int)((e >> 8) & 0x1F)));
                                tb >>= n; used += n;
                                uint d = dt[tb & DMask];
                                if ((d & (Exc | Sub)) == (Exc | Sub))
                                {
                                    used += DistBits; tb >>= DistBits;
                                    d = dt[(d >> 16) + ((uint)tb & ((1u << (int)((d >> 8) & 0x1F)) - 1))];
                                }
                                n = (int)(d & 0xFF);
                                if (used + n > bc) goto needInput;
                                if ((d & Exc) != 0) throw Bad("invalid distance code");
                                int dist = (int)((d >> 16) + (uint)((tb & ((1UL << n) - 1)) >> (int)((d >> 8) & 0x1F)));
                                bb = tb >> n; bc -= used + n;
                                if (dist > outp - outBase) throw Bad("distance too far back");
                                pendLen = len; pendDist = dist;
                                state = State.Match;
                            }
                            break;
                        }
                        case State.Match:
                        {
                            int n = (int)Math.Min(pendLen, outEnd - outp);
                            byte* src = outp - pendDist;
                            if (pendDist >= n) Buffer.MemoryCopy(src, outp, n, n);
                            else for (int i = 0; i < n; i++) outp[i] = src[i];
                            outp += n; pendLen -= n;
                            if (pendLen > 0) goto needOutput;
                            state = State.Symbols;
                            break;
                        }
                        case State.Trailer:
                        {
                            bb >>= bc & 7; bc &= ~7;
                            Refill(ref bb, ref bc, ref inp, inEnd);
                            if (bc < 32) goto needInput;
                            uint v = (uint)bb;
                            TrailerAdler = (v << 24) | ((v & 0xFF00) << 8) | ((v >> 8) & 0xFF00) | (v >> 24);
                            bb >>= 32; bc -= 32;
                            state = State.Done;
                            break;
                        }
                        default: // Done: hand back whole bytes that were buffered but not used
                        {
                            bb >>= bc & 7; bc &= ~7;
                            int unread = (int)Math.Min(bc >> 3, inp - inStart);
                            inp -= unread;
                            bb = 0; bc = 0;
                            result = InflateStatus.Done;
                            goto exit;
                        }
                    }
                }
            needInput:
                result = InflateStatus.NeedInput;
                goto exit;
            needOutput:
                result = InflateStatus.NeedOutput;
            exit:;
            }
            bitBuf = bb; bitCount = bc;
            consumed = (int)(inp - inStart);
            written = (int)(outp - outStart);
            return result;
        }

        [ThreadStatic] static InflateCore? cached;

        static InflateCore Rent(bool zlibWrapper)
        {
            var c = cached ??= new InflateCore();
            c.Reset(zlibWrapper);
            return c;
        }

        /// <summary>
        /// One-shot decode straight into <paramref name="destination"/>. Stops quietly when the destination is full
        /// or the input ends early (returning what was produced, like the BCL stream does); structural errors and
        /// checksum mismatches throw <see cref="ImageFormatException"/>.
        /// </summary>
        public static int Decompress(ReadOnlySpan<byte> source, Span<byte> destination, bool zlibWrapper, bool verifyChecksum)
        {
            var core = Rent(zlibWrapper);
            int written;
            InflateStatus status;
            fixed (byte* s = source, d = destination)
                status = core.Run(s, s + source.Length, d, d, d + destination.Length, out _, out written);
            if (status == InflateStatus.Done && zlibWrapper && verifyChecksum
                && core.TrailerAdler != Checksums.Adler32(destination.Slice(0, written), 1))
                throw Bad("Adler-32 mismatch");
            return written;
        }

        /// <summary>One-shot decode of a stream of unknown size into a growing array.</summary>
        public static byte[] Decompress(ReadOnlySpan<byte> source, int sizeHint, bool zlibWrapper, bool verifyChecksum)
        {
            var core = Rent(zlibWrapper);
            // deflate cannot expand more than 1032:1, so a lying hint cannot make us allocate without bound
            long bound = Math.Min((long)source.Length * 1032 + 64, Array.MaxLength);
            var buffer = new byte[(int)Math.Max(256, Math.Min(Math.Max(sizeHint, (long)source.Length * 3), bound))];
            int inPos = 0, outPos = 0;
            for (; ; )
            {
                InflateStatus status;
                int consumed, written;
                fixed (byte* s = source, d = buffer)
                    status = core.Run(s + inPos, s + source.Length, d, d + outPos, d + buffer.Length, out consumed, out written);
                inPos += consumed; outPos += written;
                if (status != InflateStatus.NeedOutput)
                {
                    if (status == InflateStatus.Done && zlibWrapper && verifyChecksum
                        && core.TrailerAdler != Checksums.Adler32(buffer.AsSpan(0, outPos), 1))
                        throw Bad("Adler-32 mismatch");
                    break;
                }
                if (buffer.Length >= Array.MaxLength) throw Bad("output too large");
                Array.Resize(ref buffer, (int)Math.Min((long)buffer.Length * 2, Array.MaxLength));
            }
            if (outPos != buffer.Length) Array.Resize(ref buffer, outPos);
            return buffer;
        }
    }

    /// <summary>
    /// Incremental zlib / raw deflate decompressor: feed the compressed data in arbitrary pieces (for example one
    /// PNG IDAT chunk at a time) and pull the decompressed bytes into spans of any size (for example one scanline
    /// at a time). The 32 KiB history window is carried internally across calls.
    /// </summary>
    public sealed unsafe class Inflater
    {
        const int Window = 32768, BufferSize = Window + 4 * 32768;

        readonly InflateCore core = new InflateCore();
        readonly byte[] buffer = new byte[BufferSize];
        readonly bool zlib, verify;
        int readPos, writePos;
        uint adler;
        bool checkedTrailer;

        /// <param name="zlibWrapper">true for a zlib stream (RFC 1950), false for raw deflate (RFC 1951).</param>
        /// <param name="verifyChecksum">Verify the Adler-32 trailer of a zlib stream.</param>
        public Inflater(bool zlibWrapper = true, bool verifyChecksum = true)
        {
            zlib = zlibWrapper; verify = verifyChecksum;
            Reset();
        }

        /// <summary>Total number of decompressed bytes delivered so far.</summary>
        public long TotalOut { get; private set; }

        /// <summary>Total number of compressed bytes consumed so far.</summary>
        public long TotalIn { get; private set; }

        /// <summary>True once the end of the stream was decoded and every output byte was delivered.</summary>
        public bool IsFinished => core.IsDone && readPos == writePos;

        /// <summary>Restarts for a new stream.</summary>
        public void Reset()
        {
            core.Reset(zlib);
            readPos = writePos = 0;
            adler = 1;
            checkedTrailer = false;
            TotalOut = TotalIn = 0;
        }

        /// <summary>
        /// Consumes bytes from <paramref name="input"/> and writes decompressed bytes to <paramref name="output"/>.
        /// Input that was not consumed (only possible with <see cref="InflateStatus.NeedOutput"/> or
        /// <see cref="InflateStatus.Done"/>) must be presented again. Throws <see cref="ImageFormatException"/> on
        /// corrupt data.
        /// </summary>
        public InflateStatus Inflate(ReadOnlySpan<byte> input, Span<byte> output, out int bytesConsumed, out int bytesWritten)
        {
            int consumed = 0, written = 0;
            InflateStatus result;
            fixed (byte* inP = input, buf = buffer)
            {
                for (; ; )
                {
                    int n = Math.Min(writePos - readPos, output.Length - written);
                    if (n > 0)
                    {
                        new ReadOnlySpan<byte>(buf + readPos, n).CopyTo(output.Slice(written));
                        readPos += n; written += n;
                    }
                    if (readPos < writePos) { result = InflateStatus.NeedOutput; break; }
                    if (core.IsDone) { result = InflateStatus.Done; break; }
                    // Nothing pending. With a full output span only non-producing work (end of block, trailer) is
                    // attempted, so that the end of the stream is reported together with the last bytes.
                    bool noRoom = written == output.Length && output.Length != 0;
                    if (!noRoom && BufferSize - writePos < 4096)
                    {
                        Buffer.MemoryCopy(buf + writePos - Window, buf, Window, Window);
                        readPos = writePos = Window;
                    }
                    int limit = noRoom ? writePos : BufferSize;
                    var status = core.Run(inP + consumed, inP + input.Length, buf, buf + writePos, buf + limit, out int c, out int w);
                    consumed += c;
                    if (zlib && verify && w > 0) adler = Checksums.Adler32(new ReadOnlySpan<byte>(buf + writePos, w), adler);
                    writePos += w;
                    if (status == InflateStatus.Done && zlib && verify && !checkedTrailer)
                    {
                        checkedTrailer = true;
                        if (core.TrailerAdler != adler) throw new ImageFormatException("corrupt deflate data: Adler-32 mismatch");
                    }
                    if (noRoom && status != InflateStatus.Done)
                    {
                        result = status == InflateStatus.NeedInput ? InflateStatus.NeedInput : InflateStatus.NeedOutput;
                        break;
                    }
                    if (status == InflateStatus.NeedInput && w == 0) { result = InflateStatus.NeedInput; break; }
                }
            }
            TotalIn += consumed; TotalOut += written;
            bytesConsumed = consumed; bytesWritten = written;
            return result;
        }
    }
}
