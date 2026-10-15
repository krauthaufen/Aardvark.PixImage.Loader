using System;
using System.Runtime.CompilerServices;

namespace Aardvark.ImageCodecs.Compression
{
    /// <summary>
    /// LZMA decoder (range coder + LZ77 with the LZMA probability model) with drivers for the LZMA2 chunk format
    /// and the legacy ".lzma" (LZMA alone) header. Output goes straight into the destination, which also serves
    /// as the dictionary.
    /// </summary>
    internal sealed unsafe class LzmaDecoder
    {
        // offsets into the probability array
        const int IsMatch = 0, IsRep = IsMatch + 192, IsRepG0 = IsRep + 12, IsRepG1 = IsRepG0 + 12, IsRepG2 = IsRepG1 + 12,
            IsRep0Long = IsRepG2 + 12, PosSlot = IsRep0Long + 192, SpecPos = PosSlot + 256, Align = SpecPos + 128,
            LenCoder = Align + 16, RepLenCoder = LenCoder + LenSize, Literal = RepLenCoder + LenSize;
        const int LenSize = 2 + 128 + 128 + 256, LenLow = 2, LenMid = 2 + 128, LenHigh = 2 + 256;
        const int InputSlack = 64; // one symbol reads fewer input bytes than this
        const int Wild = 16;

        ushort[] probs = new ushort[Literal + (0x300 << 4) + 0x300];
        int lc, lp, pb;
        uint state, rep0, rep1, rep2, rep3, range, code;

        static Exception Bad(string what) => new ImageFormatException("corrupt LZMA data: " + what);

        bool SetProperties(int props, bool lzma2)
        {
            if (props > 224) return false;
            lc = props % 9; props /= 9;
            lp = props % 5; pb = props / 5;
            if (lzma2 && lc + lp > 4) return false;
            int need = Literal + (0x300 << (lc + lp));
            if (probs.Length < need + 0x300) probs = new ushort[need + 0x300]; // (the tree steps load one level ahead)
            return true;
        }

        void ResetState()
        {
            state = 0; rep0 = rep1 = rep2 = rep3 = 0;
            probs.AsSpan(0, Literal + (0x300 << (lc + lp))).Fill(1024);
        }

        // The decoder's speed is set by one dependency chain: range -> bound -> bit -> next range. The select at
        // its end is done with a mask (the JIT has no conditional move to offer inside this loop, and a branch on
        // the bit is mispredicted too often on image data). Whatever is not on that chain is kept off it: in
        // bit trees both children's probabilities are loaded before the bit is known.

        // One binary decision; returns 0 or 1.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static uint Bit(ushort* prob, ref uint range, ref uint code, ref byte* p)
        {
            if (range < 1u << 24) { range <<= 8; code = (code << 8) | *p++; }
            uint v = *prob;
            uint bound = (range >> 11) * v;
            uint mask = (uint)(((long)code - bound) >> 63); // all ones for a 0 bit
            uint rest = range - bound;
            range = (bound & mask) | (rest & ~mask);
            code -= bound & ~mask;
            // 0: v += (2048 - v) >> 5, 1: v -= v >> 5
            *prob = (ushort)((int)v - (((int)v + (int)(mask & unchecked((uint)(31 - 2048)))) >> 5));
            return mask + 1;
        }

        // One step down a bit tree: sym is the node (1 = root), p its probability; both are advanced.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void TreeStep(ushort* probs, ref uint sym, ref uint p, ref uint range, ref uint code, ref byte* inp)
        {
            if (range < 1u << 24) { range <<= 8; code = (code << 8) | *inp++; }
            uint bound = (range >> 11) * p;
            uint p0 = probs[2 * sym], p1 = probs[2 * sym + 1];
            uint mask = (uint)(((long)code - bound) >> 63);
            probs[sym] = (ushort)((int)p - (((int)p + (int)(mask & unchecked((uint)(31 - 2048)))) >> 5));
            uint rest = range - bound;
            range = (bound & mask) | (rest & ~mask);
            code -= bound & ~mask;
            sym = 2 * sym + 1 + mask;
            p = (p0 & mask) | (p1 & ~mask);
        }

        // (the last step of a tree loads "children" that lie past the tree: those slots exist and are never used)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static uint Tree(ushort* probs, int bits, ref uint range, ref uint code, ref byte* p)
        {
            uint m = 1, pr = probs[1];
            for (int i = 0; i < bits; i++) TreeStep(probs, ref m, ref pr, ref range, ref code, ref p);
            return m - (1u << bits);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static uint ReverseTree(ushort* probs, int bits, ref uint range, ref uint code, ref byte* p)
        {
            uint m = 1, pr = probs[1], sym = 0;
            for (int i = 0; i < bits; i++)
            {
                TreeStep(probs, ref m, ref pr, ref range, ref code, ref p);
                sym |= (m & 1) << i;
            }
            return sym;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static uint Length(ushort* coder, uint posState, ref uint range, ref uint code, ref byte* p)
        {
            if (Bit(coder, ref range, ref code, ref p) == 0) return Tree(coder + LenLow + (posState << 3), 3, ref range, ref code, ref p);
            if (Bit(coder + 1, ref range, ref code, ref p) == 0) return 8 + Tree(coder + LenMid + (posState << 3), 3, ref range, ref code, ref p);
            return 16 + Tree(coder + LenHigh, 8, ref range, ref code, ref p);
        }

        /// <summary>
        /// Decodes symbols until outPos reaches outLimit, the end marker is seen (returns true) or the input
        /// pointer passes <paramref name="safeEnd"/>. dst[dictStart, outPos) is the dictionary. A match may run
        /// past outLimit up to hardLimit (equal for LZMA2 chunks, where that is an error); outCapacity bounds the
        /// over-long copies.
        /// </summary>
        bool Symbols(ref byte* input, byte* safeEnd, byte* dst, ref long outPos, long outLimit, long hardLimit, long dictStart, long outCapacity, bool cut = false)
        {
            byte* p = input;
            uint range = this.range, code = this.code, state = this.state, rep0 = this.rep0, rep1 = this.rep1, rep2 = this.rep2, rep3 = this.rep3;
            int lc = this.lc;
            uint pbMask = (1u << pb) - 1, lpMask = (1u << lp) - 1;
            long o = outPos;
            bool marker = false;
            fixed (ushort* pr = probs)
            {
                while (o < outLimit && p <= safeEnd)
                {
                    uint pos = (uint)(o - dictStart);
                    uint posState = pos & pbMask;
                    if (Bit(pr + IsMatch + (state << 4) + posState, ref range, ref code, ref p) == 0)
                    {
                        uint prev = o == dictStart ? 0u : dst[o - 1];
                        ushort* lit = pr + Literal + 0x300 * (((pos & lpMask) << lc) + (prev >> (8 - lc)));
                        uint sym = 1;
                        if (state >= 7)
                        {
                            // Literal after a match: while its bits agree with the byte the last distance points at,
                            // the probabilities come from the row chosen by that byte's next bit - which is known
                            // in advance, so both children can still be loaded early.
                            uint matchByte = dst[o - rep0 - 1];
                            uint mb = (matchByte >> 7) & 1;
                            uint pm = lit[((1 + mb) << 8) + 1];
                            do
                            {
                                matchByte <<= 1;
                                uint mbNext = (matchByte >> 7) & 1;
                                ushort* row = lit + ((1 + mbNext) << 8);
                                if (range < 1u << 24) { range <<= 8; code = (code << 8) | *p++; }
                                uint bound = (range >> 11) * pm;
                                uint p0 = row[2 * sym], p1 = row[2 * sym + 1];
                                uint mask = (uint)(((long)code - bound) >> 63);
                                lit[((1 + mb) << 8) + sym] = (ushort)((int)pm - (((int)pm + (int)(mask & unchecked((uint)(31 - 2048)))) >> 5));
                                uint rest = range - bound;
                                range = (bound & mask) | (rest & ~mask);
                                code -= bound & ~mask;
                                uint b = mask + 1;
                                sym = 2 * sym + b;
                                if (mb != b) break;
                                pm = (p0 & mask) | (p1 & ~mask);
                                mb = mbNext;
                            } while (sym < 0x100);
                        }
                        if (sym < 0x100)
                        {
                            // plain tree steps for all eight bits, or for the rest after the first disagreement
                            uint pl = lit[sym];
                            do TreeStep(lit, ref sym, ref pl, ref range, ref code, ref p); while (sym < 0x100);
                        }
                        dst[o++] = (byte)sym;
                        state = state < 4 ? 0 : state < 10 ? state - 3 : state - 6;
                        continue;
                    }

                    uint len;
                    if (Bit(pr + IsRep + state, ref range, ref code, ref p) == 0)
                    {
                        rep3 = rep2; rep2 = rep1; rep1 = rep0;
                        len = Length(pr + LenCoder, posState, ref range, ref code, ref p);
                        state = state < 7 ? 7u : 10u;
                        uint slot = Tree(pr + PosSlot + (Math.Min(len, 3) << 6), 6, ref range, ref code, ref p);
                        if (slot < 4) rep0 = slot;
                        else
                        {
                            int direct = (int)(slot >> 1) - 1;
                            rep0 = (2 | (slot & 1)) << direct;
                            if (slot < 14) rep0 += ReverseTree(pr + SpecPos + rep0 - slot, direct, ref range, ref code, ref p);
                            else
                            {
                                uint d = 0;
                                for (int i = direct - 4; i > 0; i--)
                                {
                                    if (range < 1u << 24) { range <<= 8; code = (code << 8) | *p++; }
                                    range >>= 1;
                                    code -= range;
                                    uint t = 0 - (code >> 31);
                                    code += range & t;
                                    d = (d << 1) + (t + 1);
                                }
                                rep0 += d << 4;
                                rep0 += ReverseTree(pr + Align, 4, ref range, ref code, ref p);
                                if (rep0 == 0xFFFFFFFF) { marker = true; break; }
                            }
                        }
                    }
                    else
                    {
                        if (o == dictStart) throw Bad("repeat without history");
                        if (Bit(pr + IsRepG0 + state, ref range, ref code, ref p) == 0)
                        {
                            if (Bit(pr + IsRep0Long + (state << 4) + posState, ref range, ref code, ref p) == 0)
                            {
                                state = state < 7 ? 9u : 11u;
                                if (rep0 >= pos) throw Bad("distance too far back");
                                dst[o] = dst[o - rep0 - 1];
                                o++;
                                continue;
                            }
                        }
                        else
                        {
                            uint d;
                            if (Bit(pr + IsRepG1 + state, ref range, ref code, ref p) == 0) d = rep1;
                            else
                            {
                                if (Bit(pr + IsRepG2 + state, ref range, ref code, ref p) == 0) d = rep2;
                                else { d = rep3; rep3 = rep2; }
                                rep2 = rep1;
                            }
                            rep1 = rep0; rep0 = d;
                        }
                        len = Length(pr + RepLenCoder, posState, ref range, ref code, ref p);
                        state = state < 7 ? 8u : 11u;
                    }
                    len += 2;
                    if (rep0 >= pos) throw Bad("distance too far back");
                    if (len > hardLimit - o)
                    {
                        // a .lzma file with a declared size simply ends there; an LZMA2 chunk must end on a symbol
                        if (!cut) throw Bad("match runs past the end of the data");
                        len = (uint)(hardLimit - o);
                    }
                    if (outCapacity - o - len >= Wild) LzCopy.MatchWild(dst + o, rep0 + 1, len);
                    else LzCopy.Match(dst + o, rep0 + 1, len);
                    o += len;
                }
            }
            this.range = range; this.code = code; this.state = state;
            this.rep0 = rep0; this.rep1 = rep1; this.rep2 = rep2; this.rep3 = rep3;
            input = p; outPos = o;
            return marker;
        }

        /// <summary>
        /// Runs the range decoder over src[0, length) producing output up to outLimit. Returns the number of input
        /// bytes consumed; <paramref name="marker"/> tells whether the end marker ended the data.
        /// </summary>
        int RunCoder(byte* src, int length, byte* dst, ref long outPos, long outLimit, long dictStart, long outCapacity, out bool marker, bool cut = false)
        {
            if (length < 5) throw Bad("truncated range coder data");
            if (src[0] != 0) throw Bad("bad range coder start");
            code = (uint)(src[1] << 24 | src[2] << 16 | src[3] << 8 | src[4]);
            range = 0xFFFFFFFF;
            byte* p = src + 5, end = src + length;
            marker = false;
            if (end - p > InputSlack) marker = Symbols(ref p, end - InputSlack, dst, ref outPos, outLimit, outLimit, dictStart, outCapacity, cut);
            if (!marker && outPos < outLimit)
            {
                // the last bytes are decoded from a zero padded copy so that the hot loop needs no input checks
                int rest = (int)(end - p);
                if (rest > 2 * InputSlack) throw Bad("internal"); // cannot happen: a symbol reads fewer than InputSlack bytes
                byte* tmp = stackalloc byte[4 * InputSlack];
                new Span<byte>(tmp, 4 * InputSlack).Clear();
                Buffer.MemoryCopy(p, tmp, rest, rest);
                byte* q = tmp;
                marker = Symbols(ref q, tmp + rest, dst, ref outPos, outLimit, outLimit, dictStart, outCapacity, cut);
                if (q - tmp > rest) throw Bad("truncated data");
                p += q - tmp;
            }
            // the encoder flushes the coder: one more normalisation may be pending
            if (range < 1u << 24 && p < end) { range <<= 8; code = (code << 8) | *p++; }
            return (int)(p - src);
        }

        static Span<byte> Output(Span<byte> destination, ref byte[]? growable, long outPos, long need)
        {
            if (growable == null)
            {
                if (need > destination.Length - outPos) throw new ImageFormatException("corrupt LZMA data: output does not fit the destination");
                return destination;
            }
            if (outPos + need > Array.MaxLength) throw Bad("output too large");
            long want = Math.Min(outPos + need + Wild, Array.MaxLength); // no room for the copy overshoot at the very limit: the copies check
            if (want > growable.Length)
                Array.Resize(ref growable, (int)Math.Min(Math.Max(want, (long)growable.Length * 2), Array.MaxLength));
            return growable;
        }

        /// <summary>
        /// Decodes a raw LZMA2 stream that starts at source[pos] and ends with its 0x00 terminator. Returns the new
        /// output position; pos is advanced past the terminator.
        /// </summary>
        public int DecodeLzma2(ReadOnlySpan<byte> source, ref int pos, Span<byte> destination, ref byte[]? growable, int outStart)
        {
            long outPos = outStart, dictStart = outStart;
            bool needDictReset = true, needProps = true;
            fixed (byte* src = source)
            {
                for (; ; )
                {
                    if (pos >= source.Length) throw Bad("truncated LZMA2 stream");
                    int control = src[pos++];
                    if (control == 0) break;
                    if (control >= 0xE0 || control == 1) { needProps = true; needDictReset = true; }
                    else if (needDictReset) throw Bad("LZMA2 stream does not start with a dictionary reset");
                    if (control < 0x80)
                    {
                        if (control > 2) throw Bad("invalid LZMA2 control byte");
                        if (source.Length - pos < 2) throw Bad("truncated LZMA2 chunk");
                        int size = (src[pos] << 8 | src[pos + 1]) + 1;
                        pos += 2;
                        if (source.Length - pos < size) throw Bad("truncated LZMA2 chunk");
                        if (needDictReset) { needDictReset = false; dictStart = outPos; }
                        var o = Output(destination, ref growable, outPos, size);
                        source.Slice(pos, size).CopyTo(o.Slice((int)outPos));
                        pos += size; outPos += size;
                        continue;
                    }
                    if (source.Length - pos < 4) throw Bad("truncated LZMA2 chunk");
                    int unpacked = ((control & 0x1F) << 16 | src[pos] << 8 | src[pos + 1]) + 1;
                    int packed = (src[pos + 2] << 8 | src[pos + 3]) + 1;
                    pos += 4;
                    if (control >= 0xC0)
                    {
                        if (pos >= source.Length) throw Bad("truncated LZMA2 chunk");
                        if (!SetProperties(src[pos++], true)) throw Bad("invalid LZMA2 properties");
                        needProps = false;
                        ResetState();
                    }
                    else if (needProps) throw Bad("LZMA2 chunk without properties");
                    else if (control >= 0xA0) ResetState();
                    if (needDictReset) { needDictReset = false; dictStart = outPos; }
                    if (source.Length - pos < packed) throw Bad("truncated LZMA2 chunk");
                    var output = Output(destination, ref growable, outPos, unpacked);
                    long limit = outPos + unpacked;
                    fixed (byte* dst = output)
                    {
                        int used = RunCoder(src + pos, packed, dst, ref outPos, limit, dictStart, output.Length, out bool marker);
                        if (marker) throw Bad("end marker inside LZMA2 chunk");
                        if (outPos != limit || used != packed) throw Bad("LZMA2 chunk size mismatch");
                    }
                    pos += packed;
                }
            }
            return (int)outPos;
        }

        /// <summary>Decodes a ".lzma" (LZMA alone) file: 13 byte header (properties, dictionary size, size or -1) + data.</summary>
        public int DecodeAlone(ReadOnlySpan<byte> source, Span<byte> destination, ref byte[]? growable)
        {
            if (source.Length < 13) throw Bad("truncated .lzma header");
            if (!SetProperties(source[0], false)) throw Bad("invalid properties");
            ulong size = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(5));
            bool known = size != ulong.MaxValue;
            if (known && size > (ulong)Array.MaxLength) throw Bad("declared size too large");
            ResetState();
            long outPos = 0;
            int packed = source.Length - 13;
            fixed (byte* src = source)
            {
                if (known)
                {
                    // LZMA cannot expand by more than this factor, so the declared size can be checked before allocating
                    if (growable != null && (long)size > (long)packed * 9000 + 65536) throw Bad("declared size is implausible");
                    var output = Output(destination, ref growable, 0, (long)size);
                    fixed (byte* dst = output)
                        RunCoder(src + 13, packed, dst, ref outPos, (long)size, 0, output.Length, out _, true);
                    if (outPos != (long)size) throw Bad("truncated data");
                }
                else
                {
                    // Unknown size: decode until the end marker into a buffer of our own that grows with the data
                    // (bounded by the destination size when there is one), then hand the result over.
                    long maxOut = growable != null ? Array.MaxLength - 1024 : destination.Length;
                    if (packed < 5) throw Bad("truncated range coder data");
                    if (src[13] != 0) throw Bad("bad range coder start");
                    code = (uint)(src[14] << 24 | src[15] << 16 | src[16] << 8 | src[17]);
                    range = 0xFFFFFFFF;
                    const int Room = 273 + Wild; // longest match plus the copy overshoot
                    var buffer = new byte[(int)Math.Min(Math.Max(65536, (long)packed * 4), maxOut + 1) + Room];
                    byte* p = src + 18, end = src + source.Length;
                    byte* tail = stackalloc byte[4 * InputSlack];
                    new Span<byte>(tail, 4 * InputSlack).Clear();
                    byte* q = null;
                    int tailLen = 0;
                    for (; ; )
                    {
                        long limit = buffer.Length - Room;
                        bool marker = false;
                        fixed (byte* dst = buffer)
                        {
                            if (q == null)
                            {
                                if (end - p > InputSlack) marker = Symbols(ref p, end - InputSlack, dst, ref outPos, limit, long.MaxValue / 2, 0, buffer.Length);
                                if (!marker && outPos < limit)
                                {
                                    tailLen = (int)(end - p);
                                    Buffer.MemoryCopy(p, tail, tailLen, tailLen);
                                    q = tail;
                                }
                            }
                            if (!marker && q != null && outPos < limit)
                            {
                                marker = Symbols(ref q, tail + tailLen, dst, ref outPos, limit, long.MaxValue / 2, 0, buffer.Length);
                                if (q - tail > tailLen || !marker && outPos < limit) throw Bad("truncated data");
                            }
                        }
                        if (outPos > maxOut) throw Bad(growable != null ? "output too large" : "output does not fit the destination");
                        if (marker) break;
                        Array.Resize(ref buffer, (int)Math.Min((long)(buffer.Length - Room) * 2, maxOut + 1) + Room);
                    }
                    if (growable != null) { Array.Resize(ref buffer, (int)outPos); growable = buffer; }
                    else buffer.AsSpan(0, (int)outPos).CopyTo(destination);
                }
            }
            return (int)outPos;
        }
    }
}
