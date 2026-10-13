using System;

namespace PixCodec
{
    /// <summary>
    /// CCITT T.4 / T.6 bilevel decoding: compression 2 (modified Huffman, byte aligned rows), 3 (Group 3, one or two
    /// dimensional, with EOL codes) and 4 (Group 4). Produces packed rows, 1 bits for black runs.
    /// The "uncompressed mode" extension is not implemented.
    /// </summary>
    internal static unsafe class TiffCcitt
    {
        // Run length code tables of ITU-T T.4 (terminating codes 0..63, make-up codes 64..1728 step 64).
        static readonly string[] WhiteTerm =
        {
            "00110101", "000111", "0111", "1000", "1011", "1100", "1110", "1111", "10011", "10100", "00111", "01000", "001000", "000011",
            "110100", "110101", "101010", "101011", "0100111", "0001100", "0001000", "0010111", "0000011", "0000100", "0101000",
            "0101011", "0010011", "0100100", "0011000", "00000010", "00000011", "00011010", "00011011", "00010010", "00010011",
            "00010100", "00010101", "00010110", "00010111", "00101000", "00101001", "00101010", "00101011", "00101100", "00101101",
            "00000100", "00000101", "00001010", "00001011", "01010010", "01010011", "01010100", "01010101", "00100100", "00100101",
            "01011000", "01011001", "01011010", "01011011", "01001010", "01001011", "00110010", "00110011", "00110100",
        };

        static readonly string[] WhiteMakeup =
        {
            "11011", "10010", "010111", "0110111", "00110110", "00110111", "01100100", "01100101", "01101000", "01100111", "011001100",
            "011001101", "011010010", "011010011", "011010100", "011010101", "011010110", "011010111", "011011000", "011011001",
            "011011010", "011011011", "010011000", "010011001", "010011010", "011000", "010011011",
        };

        static readonly string[] BlackTerm =
        {
            "0000110111", "010", "11", "10", "011", "0011", "0010", "00011", "000101", "000100", "0000100", "0000101", "0000111",
            "00000100", "00000111", "000011000", "0000010111", "0000011000", "0000001000", "00001100111", "00001101000",
            "00001101100", "00000110111", "00000101000", "00000010111", "00000011000", "000011001010", "000011001011",
            "000011001100", "000011001101", "000001101000", "000001101001", "000001101010", "000001101011", "000011010010",
            "000011010011", "000011010100", "000011010101", "000011010110", "000011010111", "000001101100", "000001101101",
            "000011011010", "000011011011", "000001010100", "000001010101", "000001010110", "000001010111", "000001100100",
            "000001100101", "000001010010", "000001010011", "000000100100", "000000110111", "000000111000", "000000100111",
            "000000101000", "000001011000", "000001011001", "000000101011", "000000101100", "000001011010", "000001100110",
            "000001100111",
        };

        static readonly string[] BlackMakeup =
        {
            "0000001111", "000011001000", "000011001001", "000001011011", "000000110011", "000000110100", "000000110101",
            "0000001101100", "0000001101101", "0000001001010", "0000001001011", "0000001001100", "0000001001101", "0000001110010",
            "0000001110011", "0000001110100", "0000001110101", "0000001110110", "0000001110111", "0000001010010", "0000001010011",
            "0000001010100", "0000001010101", "0000001011010", "0000001011011", "0000001100100", "0000001100101",
        };

        // make-up codes 1792..2560, shared by both colours
        static readonly string[] ExtMakeup =
        {
            "00000001000", "00000001100", "00000001101", "000000010010", "000000010011", "000000010100", "000000010101",
            "000000010110", "000000010111", "000000011100", "000000011101", "000000011110", "000000011111",
        };

        const int Peek = 13; // longest code
        // entry = code length << 12 | run length, 0 = invalid
        static readonly ushort[] White = Build(WhiteTerm, WhiteMakeup);
        static readonly ushort[] Black = Build(BlackTerm, BlackMakeup);

        static ushort[] Build(string[] term, string[] makeup)
        {
            var t = new ushort[1 << Peek];
            void Add(string code, int run)
            {
                int v = Convert.ToInt32(code, 2), len = code.Length;
                int first = v << (Peek - len), n = 1 << (Peek - len);
                for (int i = 0; i < n; i++)
                {
                    if (t[first + i] != 0) throw new InvalidOperationException("CCITT code table is not prefix free");
                    t[first + i] = (ushort)(len << 12 | run);
                }
            }
            for (int i = 0; i < term.Length; i++) Add(term[i], i);
            for (int i = 0; i < makeup.Length; i++) Add(makeup[i], 64 * (i + 1));
            for (int i = 0; i < ExtMakeup.Length; i++) Add(ExtMakeup[i], 1792 + 64 * i);
            return t;
        }

        ref struct Bits
        {
            readonly ReadOnlySpan<byte> _s;
            int _pos;      // next byte
            ulong _buf;    // valid bits are the top _n
            int _n;

            public Bits(ReadOnlySpan<byte> s) { _s = s; _pos = 0; _buf = 0; _n = 0; }

            /// <summary>True once more bits were consumed than the data holds.</summary>
            public bool Overrun => _n < 0;

            void Fill()
            {
                while (_n <= 56 && _pos < _s.Length) { _buf |= (ulong)_s[_pos++] << (56 - _n); _n += 8; }
            }

            /// <summary>The next <paramref name="count"/> bits (zero padded past the end) without consuming them.</summary>
            public int PeekBits(int count)
            {
                if (_n < count) Fill();
                return (int)(_buf >> (64 - count));
            }

            public void Skip(int count) { _buf <<= count; _n -= count; }

            public void AlignToByte()
            {
                if (_n > 0) Skip(_n & 7);
            }

            public bool AtEnd => _n <= 0 && _pos >= _s.Length;
        }

        static int ReadRun(ref Bits b, ushort[] table, int limit)
        {
            int total = 0;
            while (true)
            {
                int e = table[b.PeekBits(Peek)];
                if (e == 0 || b.Overrun) throw new ImageFormatException("TIFF: corrupt CCITT data");
                b.Skip(e >> 12);
                int run = e & 0xFFF;
                total += run;
                if (total > limit) throw new ImageFormatException("TIFF: corrupt CCITT data (run exceeds the row)");
                if (run < 64) return total;
            }
        }

        /// <summary>Skips fill bits and an EOL code if one is next. Returns whether an EOL was consumed.</summary>
        static bool SkipEol(ref Bits b)
        {
            // No run length code starts with more than 7 zero bits, so 11 zeros can only be (fill +) EOL.
            if (b.PeekBits(11) != 0) return false;
            int guard = 0;
            while (b.PeekBits(1) == 0)
            {
                b.Skip(1);
                if (b.Overrun || ++guard > 1 << 20) return true; // trailing fill up to the end of the data
            }
            b.Skip(1);
            return true;
        }

        /// <summary>
        /// Decodes <paramref name="rows"/> rows of <paramref name="width"/> pixels into packed rows of (width + 7) / 8 bytes.
        /// <paramref name="options"/> is T4Options for compression 3 and T6Options for compression 4.
        /// </summary>
        public static void Decode(ReadOnlySpan<byte> src, byte* dst, int width, int rows, int compression, long options)
        {
            if ((options & 2) != 0) throw new ImageNotSupportedException("TIFF: CCITT uncompressed mode is not supported");
            int rowBytes = (width + 7) / 8;
            new Span<byte>(dst, rowBytes * rows).Clear();
            // changing elements of the reference and the current row, each terminated by sentinels at `width`
            var refLine = new int[width + 4];
            var curLine = new int[width + 4];
            int refCount = 0;
            var b = new Bits(src);
            bool twoD = compression == 3 && (options & 1) != 0;

            for (int y = 0; y < rows; y++)
            {
                bool row2D = compression == 4;
                if (compression == 2) b.AlignToByte();
                else if (compression == 3)
                {
                    SkipEol(ref b);
                    if (twoD)
                    {
                        row2D = b.PeekBits(1) == 0;
                        b.Skip(1);
                    }
                }
                int n = row2D ? Decode2D(ref b, refLine, refCount, curLine, width) : Decode1D(ref b, curLine, width);
                if (b.Overrun) throw new ImageFormatException("TIFF: CCITT data is truncated");

                byte* row = dst + (long)y * rowBytes;
                for (int i = 0; i < n; i += 2)
                {
                    int s = curLine[i], e = i + 1 < n ? curLine[i + 1] : width;
                    if (e > s) FillBlack(row, s, e);
                }
                (refLine, curLine) = (curLine, refLine);
                refCount = n;
            }
        }

        static int Decode1D(ref Bits b, int[] cur, int width)
        {
            int a0 = 0, n = 0;
            bool black = false;
            while (a0 < width)
            {
                a0 += ReadRun(ref b, black ? Black : White, width - a0);
                if (n >= cur.Length) throw new ImageFormatException("TIFF: corrupt CCITT data"); // endless zero length runs
                cur[n++] = a0;
                black = !black;
            }
            return n;
        }

        static int Decode2D(ref Bits b, int[] rf, int refCount, int[] cur, int width)
        {
            int a0 = -1, n = 0;
            bool black = false;
            int pw = 0, pb = 1; // candidates for b1 among the white->black (even) and black->white (odd) reference changes
            while (a0 < width)
            {
                int i;
                if (black)
                {
                    while (pb < refCount && rf[pb] <= a0) pb += 2;
                    i = pb;
                }
                else
                {
                    while (pw < refCount && rf[pw] <= a0) pw += 2;
                    i = pw;
                }
                int b1 = i < refCount ? rf[i] : width;
                int b2 = i + 1 < refCount ? rf[i + 1] : width;

                int code = b.PeekBits(7);
                int a1;
                if (code >= 0x40) { b.Skip(1); a1 = b1; }                    // 1        V0
                else if (code >= 0x30) { b.Skip(3); a1 = b1 + 1; }          // 011      VR1
                else if (code >= 0x20) { b.Skip(3); a1 = b1 - 1; }          // 010      VL1
                else if (code >= 0x10)                                      // 001      horizontal
                {
                    b.Skip(3);
                    int start = Math.Max(a0, 0);
                    int r1 = ReadRun(ref b, black ? Black : White, width - start);
                    int r2 = ReadRun(ref b, black ? White : Black, width - start - r1);
                    if (n + 2 > cur.Length) throw new ImageFormatException("TIFF: corrupt CCITT data");
                    cur[n++] = start + r1;
                    cur[n++] = a0 = start + r1 + r2;
                    continue;
                }
                else if (code >= 0x08) { b.Skip(4); a0 = b2; continue; }    // 0001     pass
                else if (code >= 0x06) { b.Skip(6); a1 = b1 + 2; }          // 000011   VR2
                else if (code >= 0x04) { b.Skip(6); a1 = b1 - 2; }          // 000010   VL2
                else if (code == 0x03) { b.Skip(7); a1 = b1 + 3; }          // 0000011  VR3
                else if (code == 0x02) { b.Skip(7); a1 = b1 - 3; }          // 0000010  VL3
                else throw new ImageFormatException(b.Overrun || b.AtEnd ? "TIFF: CCITT data is truncated" : "TIFF: corrupt or unsupported CCITT data");

                if (a1 > width) a1 = width;
                if (a1 < Math.Max(a0, 0) || n >= cur.Length || b.Overrun) throw new ImageFormatException("TIFF: corrupt CCITT data");
                cur[n++] = a0 = a1;
                black = !black;
            }
            return n;
        }

        static void FillBlack(byte* row, int s, int e)
        {
            int sb = s >> 3, eb = e >> 3;
            int smask = 0xFF >> (s & 7), emask = 0xFF00 >> (e & 7) & 0xFF;
            if (sb == eb) { row[sb] |= (byte)(smask & emask); return; }
            row[sb] |= (byte)smask;
            for (int i = sb + 1; i < eb; i++) row[i] = 0xFF;
            if (emask != 0) row[eb] |= (byte)emask;
        }
    }
}
