using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace PixCodec.JpegImpl
{
    /// <summary>
    /// Huffman decoding table with a 9 bit lookahead. All arrays live in one pinned allocation so the
    /// hot loops can use raw pointers without per-block pinning.
    /// </summary>
    internal sealed unsafe class HuffDecodeTable
    {
        public const int LookBits = 9;
        public const int LookSize = 1 << LookBits;

        // Fast entry layout (0 = code longer than LookBits):
        //   bits 0-4   bits to consume (code length, plus the value bits when the value is inlined)
        //   bits 5-8   zero run (high nibble of the symbol)
        //   bits 9-12  number of value bits still to read (0 when inlined or when the symbol has none)
        //   bit  13    symbol has size 0 (EOB / ZRL / EOBn for AC tables)
        //   bits 16-31 inlined, sign extended value
        public const int NoValueFlag = 1 << 13;

        readonly int[] _mem;
        /// <summary>Combined code + value lookup, see layout above.</summary>
        public readonly int* Fast;
        /// <summary>Plain symbol lookup: length &lt;&lt; 8 | symbol, 0 when the code is longer than LookBits.</summary>
        public readonly int* Sym;
        /// <summary>Exclusive upper bound of the 16 bit left aligned codes of each length (index 1..16, 17 = sentinel).</summary>
        public readonly int* Limit;
        /// <summary>Added to a code of the given length to get its index into Values.</summary>
        public readonly int* Offset;
        public readonly int* Values;

        public HuffDecodeTable(ReadOnlySpan<byte> counts, ReadOnlySpan<byte> values)
        {
            _mem = GC.AllocateArray<int>(LookSize * 2 + 18 + 18 + 256, pinned: true);
            fixed (int* p = _mem) Fast = p;
            Sym = Fast + LookSize;
            Limit = Sym + LookSize;
            Offset = Limit + 18;
            Values = Offset + 18;
            for (int i = 0; i < values.Length; i++) Values[i] = values[i];

            int code = 0, k = 0;
            for (int len = 1; len <= 16; len++)
            {
                Offset[len] = k - code;
                int n = counts[len - 1];
                if (code + n > (1 << len)) throw new ImageFormatException("invalid JPEG Huffman table");
                for (int i = 0; i < n; i++, code++, k++)
                {
                    if (len > LookBits) continue;
                    int sym = values[k];
                    int first = code << (LookBits - len), span = 1 << (LookBits - len);
                    int run = sym >> 4, size = sym & 15;
                    for (int j = 0; j < span; j++)
                    {
                        Sym[first + j] = len << 8 | sym;
                        int e;
                        if (size == 0) e = len | run << 5 | NoValueFlag;
                        else if (len + size <= LookBits)
                        {
                            // the value bits follow the code inside the lookahead window
                            int v = (j >> (LookBits - len - size)) & ((1 << size) - 1);
                            if (v < (1 << (size - 1))) v -= (1 << size) - 1;
                            e = (len + size) | run << 5 | v << 16;
                        }
                        else e = len | run << 5 | size << 9;
                        Fast[first + j] = e;
                    }
                }
                Limit[len] = code << (16 - len);
                code <<= 1;
            }
            Limit[17] = int.MaxValue;
        }
    }

    /// <summary>
    /// MSB first bit reader over entropy coded data: removes FF00 byte stuffing and stops at any marker,
    /// feeding zero bits from there on (<see cref="Pad"/> counts them so the caller can detect the overrun).
    /// </summary>
    internal unsafe struct JpegBitReader
    {
        public byte* P, End;
        /// <summary>Unread bits, left aligned. Bits below <see cref="Cnt"/> are either zero or already hold the following stream bits.</summary>
        public ulong Buf;
        public int Cnt;
        /// <summary>Number of fabricated zero bits at the tail of the buffer.</summary>
        public int Pad;

        public void Reset(byte* p)
        {
            P = p; Buf = 0; Cnt = 0; Pad = 0;
        }

        /// <summary>True once bits past the end of the entropy coded segment have been consumed.</summary>
        public readonly bool Overrun => Cnt < Pad;

        /// <summary>Tops the buffer up to at least 56 bits.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Refill()
        {
            if (End - P >= 8)
            {
                ulong v = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(P));
                // no 0xFF among the next 8 bytes: take as many whole bytes as fit
                if (((~v - 0x0101010101010101UL) & v & 0x8080808080808080UL) == 0)
                {
                    Buf |= v >> Cnt;
                    P += (63 - Cnt) >> 3;
                    Cnt |= 56;
                    return;
                }
            }
            RefillSlow();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void RefillSlow()
        {
            while (Cnt <= 56)
            {
                if (P < End)
                {
                    int b = *P;
                    if (b != 0xFF) P++;
                    else if (End - P >= 2 && P[1] == 0) P += 2; // stuffed zero
                    else goto pad; // marker (or a lone FF at the end): leave it for the caller
                    Buf |= (ulong)b << (56 - Cnt);
                    Cnt += 8;
                    continue;
                }
            pad:
                Cnt += 8;
                Pad += 8;
            }
        }

        /// <summary>Reads n (1..16) bits.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetBits(int n)
        {
            if (Cnt < n) Refill();
            int v = (int)(Buf >> (64 - n));
            Buf <<= n;
            Cnt -= n;
            return v;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetBit()
        {
            if (Cnt < 1) Refill();
            int v = (int)(Buf >> 63);
            Buf <<= 1;
            Cnt--;
            return v;
        }

        /// <summary>Reads an s bit (1..15) magnitude and sign extends it as in T.81 F.2.2.1.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReceiveExtend(int s)
        {
            int v = GetBits(s);
            return v + (((v - (1 << (s - 1))) >> 31) & (1 - (1 << s)));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int DecodeSymbol(HuffDecodeTable t)
        {
            if (Cnt < 16) Refill();
            int e = t.Sym[(int)(Buf >> (64 - HuffDecodeTable.LookBits))];
            if (e != 0)
            {
                int n = e >> 8;
                Buf <<= n;
                Cnt -= n;
                return e & 255;
            }
            return DecodeLong(t);
        }

        int DecodeLong(HuffDecodeTable t)
        {
            int peek = (int)(Buf >> 48), len = HuffDecodeTable.LookBits + 1;
            while (peek >= t.Limit[len]) len++;
            if (len > 16)
            {
                // not a valid code: corrupt data, skip it and carry on like a zero symbol
                Buf <<= 16; Cnt -= 16;
                return 0;
            }
            Buf <<= len; Cnt -= len;
            return t.Values[((peek >> (16 - len)) + t.Offset[len]) & 255];
        }
    }
}
