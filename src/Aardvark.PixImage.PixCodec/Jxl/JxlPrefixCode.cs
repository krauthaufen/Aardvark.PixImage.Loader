using System;
using System.Runtime.CompilerServices;

namespace Aardvark.ImageCodecs.JpegXl
{
    /// <summary>
    /// Canonical prefix (Huffman) code in the format of RFC 7932 (Brotli) section 3, which JPEG XL uses as well: code lengths
    /// transmitted as a simple or a complex prefix code, bits read least significant first. Decoding uses a two level table.
    /// </summary>
    internal sealed class JxlPrefixCode
    {
        const int RootBits = 8;
        // entry: bits 0..7 length (or sub-table bits when bit 8 is set), bit 8 sub-table flag, bits 16.. symbol / sub-table offset
        int[] table = Array.Empty<int>();
        int singleSymbol = -1;

        public static JxlPrefixCode Single(int symbol) => new JxlPrefixCode { singleSymbol = symbol };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Decode(JxlBitReader br)
        {
            if (singleSymbol >= 0) return singleSymbol;
            uint bits = br.PeekBits(15);
            int e = table[bits & ((1 << RootBits) - 1)];
            if ((e & 0x100) != 0)
            {
                int sub = e & 0xFF;
                e = table[(e >> 16) + (int)((bits >> RootBits) & ((1u << sub) - 1))];
            }
            br.SkipBits(e & 0xFF);
            return e >> 16;
        }

        /// <summary>Builds the code from code lengths (0 = unused symbol). Requires a complete code unless one symbol is used.</summary>
        public static JxlPrefixCode FromLengths(ReadOnlySpan<byte> lengths)
        {
            int used = 0, last = 0, maxLen = 0;
            Span<int> count = stackalloc int[16];
            count.Clear();
            for (int i = 0; i < lengths.Length; i++)
            {
                int l = lengths[i];
                if (l == 0) continue;
                if (l > 15) throw new ImageFormatException("prefix code: code length above 15");
                used++; last = i; count[l]++;
                if (l > maxLen) maxLen = l;
            }
            if (used == 0) throw new ImageFormatException("prefix code: no symbols");
            if (used == 1) return Single(last);
            long kraft = 0;
            for (int l = 1; l <= 15; l++) kraft += (long)count[l] << (15 - l);
            if (kraft != 1 << 15) throw new ImageFormatException("prefix code: incomplete or oversubscribed code");

            // canonical codes, ordered by (length, symbol)
            Span<int> next = stackalloc int[17];
            next.Clear();
            int code = 0;
            for (int l = 1; l <= 15; l++) { code = (code + count[l - 1]) << 1; next[l] = code; }
            // count[0] counts nothing (unused), so next[1] = 0
            var codes = new int[lengths.Length];
            for (int i = 0; i < lengths.Length; i++)
            {
                int l = lengths[i];
                if (l == 0) continue;
                codes[i] = Reverse(next[l]++, l);
            }
            var pc = new JxlPrefixCode();
            int rootSize = 1 << RootBits;
            // sub-table sizes per root slot
            var subBits = new int[rootSize];
            for (int i = 0; i < lengths.Length; i++)
            {
                int l = lengths[i];
                if (l <= RootBits) continue;
                int r = codes[i] & (rootSize - 1);
                subBits[r] = Math.Max(subBits[r], l - RootBits);
            }
            int total = rootSize;
            var offsets = new int[rootSize];
            for (int r = 0; r < rootSize; r++)
                if (subBits[r] > 0) { offsets[r] = total; total += 1 << subBits[r]; }
            var t = new int[total];
            for (int r = 0; r < rootSize; r++)
                if (subBits[r] > 0) t[r] = offsets[r] << 16 | 0x100 | subBits[r];
            for (int i = 0; i < lengths.Length; i++)
            {
                int l = lengths[i];
                if (l == 0) continue;
                int c = codes[i];
                int entry = i << 16 | l;
                if (l <= RootBits)
                {
                    for (int k = c; k < rootSize; k += 1 << l) t[k] = entry;
                }
                else
                {
                    int r = c & (rootSize - 1);
                    int sb = subBits[r];
                    int hi = c >> RootBits, step = 1 << (l - RootBits);
                    int subEntry = i << 16 | l;   // full length: the root bits are consumed together with the rest
                    for (int k = hi; k < 1 << sb; k += step) t[offsets[r] + k] = subEntry;
                }
            }
            pc.table = t;
            return pc;
        }

        static int Reverse(int code, int len)
        {
            int r = 0;
            for (int i = 0; i < len; i++) { r = r << 1 | (code & 1); code >>= 1; }
            return r;
        }

        static readonly byte[] CodeLengthOrder = { 1, 2, 3, 4, 0, 5, 17, 6, 16, 7, 8, 9, 10, 11, 12, 13, 14, 15 };

        /// <summary>Reads a prefix code for an alphabet of the given size (RFC 7932 section 3.4 / 3.5).</summary>
        public static JxlPrefixCode Read(JxlBitReader br, int alphabetSize)
        {
            if (alphabetSize <= 1) return Single(0);
            int hskip = (int)br.ReadBits(2);
            var lengths = new byte[alphabetSize];
            if (hskip == 1)
            {
                int nsym = (int)br.ReadBits(2) + 1;
                int abits = 0;
                while ((1 << abits) < alphabetSize) abits++;
                Span<int> sym = stackalloc int[4];
                for (int i = 0; i < nsym; i++)
                {
                    sym[i] = (int)br.ReadBits(abits);
                    if (sym[i] >= alphabetSize) throw new ImageFormatException("prefix code: symbol out of range");
                    for (int j = 0; j < i; j++) if (sym[j] == sym[i]) throw new ImageFormatException("prefix code: duplicate symbol");
                }
                switch (nsym)
                {
                    case 1: return Single(sym[0]);
                    case 2: lengths[sym[0]] = 1; lengths[sym[1]] = 1; break;
                    case 3: lengths[sym[0]] = 1; lengths[sym[1]] = 2; lengths[sym[2]] = 2; break;
                    default:
                        if (br.ReadBool()) { lengths[sym[0]] = 1; lengths[sym[1]] = 2; lengths[sym[2]] = 3; lengths[sym[3]] = 3; }
                        else { lengths[sym[0]] = 2; lengths[sym[1]] = 2; lengths[sym[2]] = 2; lengths[sym[3]] = 2; }
                        break;
                }
                return FromLengths(lengths);
            }
            // complex code: first the code length code lengths
            Span<byte> clLens = stackalloc byte[18];
            clLens.Clear();
            int space = 32, numCodes = 0;
            for (int i = hskip; i < 18; i++)
            {
                uint p = br.PeekBits(4);
                int v, n;
                switch (p & 3)
                {
                    case 0: v = 0; n = 2; break;
                    case 1: v = 4; n = 2; break;
                    case 2: v = 3; n = 2; break;
                    default:
                        if ((p & 4) == 0) { v = 2; n = 3; }
                        else if ((p & 8) == 0) { v = 1; n = 4; }
                        else { v = 5; n = 4; }
                        break;
                }
                br.SkipBits(n);
                clLens[CodeLengthOrder[i]] = (byte)v;
                if (v != 0)
                {
                    space -= 32 >> v;
                    numCodes++;
                    if (space <= 0) break;
                }
            }
            if (numCodes != 1 && space != 0) throw new ImageFormatException("prefix code: bad code length code");
            var clCode = FromLengths(clLens);
            int symbol = 0, prevLen = 8, repeat = 0, repeatLen = 0;
            int sp = 32768;
            while (symbol < alphabetSize && sp > 0)
            {
                int p = clCode.Decode(br);
                if (p < 16)
                {
                    repeat = 0;
                    if (p != 0)
                    {
                        lengths[symbol] = (byte)p;
                        prevLen = p;
                        sp -= 32768 >> p;
                    }
                    symbol++;
                }
                else
                {
                    int extra = p == 16 ? 2 : 3;
                    int newLen = p == 16 ? prevLen : 0;
                    if (repeatLen != newLen) { repeat = 0; repeatLen = newLen; }
                    int old = repeat;
                    if (repeat > 0) repeat = (repeat - 2) << extra;
                    repeat += (int)br.ReadBits(extra) + 3;
                    int delta = repeat - old;
                    if (symbol + delta > alphabetSize) throw new ImageFormatException("prefix code: code length repeat overflows");
                    for (int k = 0; k < delta; k++) lengths[symbol++] = (byte)repeatLen;
                    if (repeatLen != 0) sp -= delta * (32768 >> repeatLen);
                }
                if (br.Overrun) throw new ImageFormatException("prefix code: unexpected end of data");
            }
            if (sp != 0) throw new ImageFormatException("prefix code: incomplete code");
            return FromLengths(lengths);
        }
    }
}
