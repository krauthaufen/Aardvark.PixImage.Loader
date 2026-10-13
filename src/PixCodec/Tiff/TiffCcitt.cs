using System;

namespace PixCodec
{
    /// <summary>
    /// CCITT T.4 / T.6 bilevel decoding: compression 2 (modified Huffman, byte aligned rows), 3 (Group 3, one or two
    /// dimensional, with EOL codes) and 4 (Group 4). Produces packed rows, 1 bits for black runs.
    /// The "uncompressed mode" extension is decoded as well.
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

        /// <summary>Appends a run to the list of changing elements (which starts white and alternates).</summary>
        static void Emit(int[] cur, ref int n, int x, bool black)
        {
            if (((n & 1) == 1) == black) return; // the run continues
            if (n >= cur.Length) throw new ImageFormatException("TIFF: corrupt CCITT data");
            cur[n++] = x;
        }

        /// <summary>
        /// "Uncompressed mode" (an optional extension of T.4 / T.6): the pixels follow literally, 1 for black and
        /// 0 for white, except that a 1 is stuffed after five zeros (000001 = five white pixels) so that the exit
        /// code - six or more zeros, a 1, and a bit giving the colour of the next coded run - stays unique. The zeros
        /// beyond six in the exit code are white pixels. Returns the position reached.
        /// </summary>
        static int ReadUncompressed(ref Bits b, int[] cur, ref int n, int x, int width, out bool nextBlack)
        {
            while (true)
            {
                int z = 0;
                while (b.PeekBits(1) == 0)
                {
                    b.Skip(1);
                    if (++z > 10 || b.Overrun) throw new ImageFormatException("TIFF: corrupt CCITT data (uncompressed mode)");
                }
                b.Skip(1);
                int white = z <= 5 ? z : z - 6;
                if (white > width - x) throw new ImageFormatException("TIFF: corrupt CCITT data (uncompressed mode exceeds the row)");
                if (white > 0) { Emit(cur, ref n, x, false); x += white; }
                if (z >= 6)
                {
                    nextBlack = b.PeekBits(1) == 1;
                    b.Skip(1);
                    if (b.Overrun) throw new ImageFormatException("TIFF: CCITT data is truncated");
                    return x;
                }
                if (z <= 4)
                {
                    if (x >= width) throw new ImageFormatException("TIFF: corrupt CCITT data (uncompressed mode exceeds the row)");
                    Emit(cur, ref n, x, true);
                    x++;
                }
            }
        }

        static int Decode1D(ref Bits b, int[] cur, int width)
        {
            int a0 = 0, n = 0;
            bool black = false;
            while (a0 < width)
            {
                if (b.PeekBits(12) == 0x00F) // 000000001111: uncompressed mode follows
                {
                    b.Skip(12);
                    a0 = ReadUncompressed(ref b, cur, ref n, a0, width, out black);
                    Emit(cur, ref n, a0, black); // the coded runs go on in the colour the exit code names
                    continue;
                }
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
                else if (b.PeekBits(10) == 0x00F)                           // 0000001111: uncompressed mode follows
                {
                    b.Skip(10);
                    a0 = ReadUncompressed(ref b, cur, ref n, Math.Max(a0, 0), width, out black);
                    Emit(cur, ref n, a0, black); // the coded runs go on in the colour the exit code names
                    if (a0 == 0 && !black) a0 = -1;
                    continue;
                }
                else throw new ImageFormatException(b.Overrun || b.AtEnd ? "TIFF: CCITT data is truncated" : "TIFF: corrupt or unsupported CCITT data");

                if (a1 > width) a1 = width;
                if (a1 < Math.Max(a0, 0) || n >= cur.Length || b.Overrun) throw new ImageFormatException("TIFF: corrupt CCITT data");
                cur[n++] = a0 = a1;
                black = !black;
            }
            return n;
        }

        // ------------------------------------------------------------------------------------------ Group 4 encoder

        struct BitSink
        {
            public byte[] Buf;
            public int Len;
            ulong _acc;
            int _n;

            public void Put(int code, int bits)
            {
                _acc = _acc << bits | (uint)code;
                _n += bits;
                while (_n >= 8)
                {
                    _n -= 8;
                    if (Len == Buf.Length) Array.Resize(ref Buf, Buf.Length * 2);
                    Buf[Len++] = (byte)(_acc >> _n);
                }
            }

            public void Put(string code) => Put(Convert.ToInt32(code, 2), code.Length);

            public void Flush()
            {
                if (_n > 0) Put(0, 8 - _n);
            }
        }

        // (code, length) pairs for run lengths, built from the same tables the decoder uses
        static readonly (int code, int len)[] WhiteCodes = EncodeTable(WhiteTerm, WhiteMakeup), BlackCodes = EncodeTable(BlackTerm, BlackMakeup);

        static (int, int)[] EncodeTable(string[] term, string[] makeup)
        {
            // index: 0..63 terminating, 64 + k: make-up for 64 * (k + 1), up to 2560
            var t = new (int, int)[64 + 40];
            for (int i = 0; i < 64; i++) t[i] = (Convert.ToInt32(term[i], 2), term[i].Length);
            for (int i = 0; i < makeup.Length; i++) t[64 + i] = (Convert.ToInt32(makeup[i], 2), makeup[i].Length);
            for (int i = 0; i < ExtMakeup.Length; i++) t[64 + 27 + i] = (Convert.ToInt32(ExtMakeup[i], 2), ExtMakeup[i].Length);
            return t;
        }

        static void PutRun(ref BitSink o, int run, bool black)
        {
            var t = black ? BlackCodes : WhiteCodes;
            while (run >= 2560) { o.Put(t[64 + 39].code, t[64 + 39].len); run -= 2560; }
            if (run >= 64)
            {
                var m = t[64 + run / 64 - 1];
                o.Put(m.code, m.len);
                run &= 63;
            }
            o.Put(t[run].code, t[run].len);
        }

        /// <summary>Changing elements of a packed row: positions where the colour differs from the pixel before (white before column 0).</summary>
        static int Changes(byte* row, int width, int[] cur)
        {
            int n = 0, prev = 0;
            for (int x = 0; x < width;)
            {
                int b = row[x >> 3];
                if ((x & 7) == 0 && x + 8 <= width && (b == 0 || b == 0xFF) && (b != 0) == (prev != 0)) { x += 8; continue; } // uniform byte
                int v = b >> (7 - (x & 7)) & 1;
                if (v != prev) { cur[n++] = x; prev = v; }
                x++;
            }
            return n;
        }

        /// <summary>One-dimensional coding: alternating white and black run lengths, starting with white.</summary>
        static void Encode1D(ref BitSink o, int[] cur, int n, int width)
        {
            int pos = 0;
            bool black = false;
            for (int i = 0; i <= n && pos < width; i++)
            {
                int next = i < n ? cur[i] : width;
                PutRun(ref o, next - pos, black);
                pos = next;
                black = !black;
            }
            if (width == 0) PutRun(ref o, 0, false);
        }

        /// <summary>Two-dimensional coding of a row against the row above (pass, vertical and horizontal modes).</summary>
        static void Encode2D(ref BitSink o, int[] rf, int refCount, int[] cur, int n, int width)
        {
            int a0 = -1, ci = 0, pw = 0, pb = 1;
            bool black = false;
            while (a0 < width)
            {
                int a1 = ci < n ? cur[ci] : width;
                int i;
                if (black) { while (pb < refCount && rf[pb] <= a0) pb += 2; i = pb; }
                else { while (pw < refCount && rf[pw] <= a0) pw += 2; i = pw; }
                int b1 = i < refCount ? rf[i] : width;
                int b2 = i + 1 < refCount ? rf[i + 1] : width;
                if (b2 < a1)
                {
                    o.Put(1, 4); // pass
                    a0 = b2;
                    continue;
                }
                int d = a1 - b1;
                if (d >= -3 && d <= 3)
                {
                    switch (d)
                    {
                        case 0: o.Put(1, 1); break;
                        case 1: o.Put(3, 3); break;
                        case -1: o.Put(2, 3); break;
                        case 2: o.Put(3, 6); break;
                        case -2: o.Put(2, 6); break;
                        case 3: o.Put(3, 7); break;
                        default: o.Put(2, 7); break;
                    }
                    a0 = a1;
                    black = !black;
                    ci++;
                    continue;
                }
                int a2 = ci + 1 < n ? cur[ci + 1] : width;
                o.Put(1, 3); // horizontal
                PutRun(ref o, a1 - Math.Max(a0, 0), black);
                PutRun(ref o, a2 - a1, !black);
                a0 = a2;
                ci += 2;
            }
        }

        /// <summary>
        /// Encodes packed bilevel rows (1 bits are black).
        /// compression 4: Group 4 / T.6 - every row against the one above (the first against a white row), ending in EOFB.
        /// compression 3: Group 3 / T.4 - every row preceded by EOL; with <paramref name="twoD"/> a tag bit follows and
        /// every fourth row is coded one-dimensionally, the others against the row above.
        /// compression 2: modified Huffman - one-dimensional rows, each starting on a byte boundary, no EOL.
        /// </summary>
        public static byte[] Encode(byte* src, int width, int rows, int rowBytes, int compression, bool twoD, out int length)
        {
            var o = new BitSink { Buf = new byte[(int)Math.Min((long)rows * rowBytes / 4 + 64, 1 << 24)] };
            var rf = new int[width + 2];
            var cur = new int[width + 2];
            int refCount = 0;
            for (int y = 0; y < rows; y++)
            {
                int n = Changes(src + (long)y * rowBytes, width, cur);
                switch (compression)
                {
                    case 4:
                        Encode2D(ref o, rf, refCount, cur, n, width);
                        break;
                    case 3:
                        o.Put(1, 12); // EOL
                        if (!twoD) Encode1D(ref o, cur, n, width);
                        else if (y % 4 == 0) { o.Put(1, 1); Encode1D(ref o, cur, n, width); }
                        else { o.Put(0, 1); Encode2D(ref o, rf, refCount, cur, n, width); }
                        break;
                    default:
                        Encode1D(ref o, cur, n, width);
                        o.Flush();
                        break;
                }
                (rf, cur) = (cur, rf);
                refCount = n;
            }
            if (compression == 4) { o.Put(1, 12); o.Put(1, 12); } // EOFB
            o.Flush();
            length = o.Len;
            return o.Buf;
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
