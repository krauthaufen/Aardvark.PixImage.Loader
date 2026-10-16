using System;
using System.IO;
using Aardvark.ImageCodecs.JpegXl;

namespace Aardvark.ImageCodecs.Compression
{
    /// <summary>
    /// Brotli decompression (RFC 7932), written from the RFC. Used for the Brotli compressed metadata boxes of JPEG XL. The
    /// output grows as the data really produces it, bounded by a caller supplied maximum. Malformed input throws
    /// <see cref="ImageFormatException"/>.
    /// </summary>
    internal static class Brotli
    {
        // ------------------------------------------------------------------ static dictionary (RFC 7932 appendix A)

        static readonly byte[] NdBits = { 0, 0, 0, 0, 10, 10, 11, 11, 10, 10, 10, 10, 10, 9, 9, 8, 7, 7, 8, 7, 7, 6, 6, 5, 5 };
        static readonly int[] DOffset = BuildOffsets();
        static byte[]? dictionary;

        static int[] BuildOffsets()
        {
            var o = new int[26];
            for (int l = 0; l < 25; l++) o[l + 1] = o[l] + (l >= 4 ? l << NdBits[l] : 0);
            return o;
        }

        /// <summary>The 122,784 byte dictionary of RFC 7932 appendix A, stored zlib compressed as a resource.</summary>
        static byte[] Dictionary
        {
            get
            {
                if (dictionary != null) return dictionary;
                using var s = typeof(Brotli).Assembly.GetManifestResourceStream("Aardvark.ImageCodecs.Compression.BrotliDictionary.zlib")
                    ?? throw new InvalidOperationException("Brotli dictionary resource missing");
                var packed = new byte[s.Length];
                s.ReadExactly(packed);
                var d = InflateCore.Decompress(packed, 122784, true, true);
                if (d.Length != 122784) throw new InvalidOperationException("Brotli dictionary resource damaged");
                return dictionary = d;
            }
        }

        // ------------------------------------------------------------------ word transforms (RFC 7932 appendix B)
        // elementary transform: 0 identity, 1 ferment first, 2 ferment all, 3..11 omit first 1..9, 12..20 omit last 1..9

        static readonly (string Prefix, int Kind, string Suffix)[] Transforms =
        {
            ("", 0, ""),
            ("", 0, " "),
            (" ", 0, " "),
            ("", 3, ""),
            ("", 1, " "),
            ("", 0, " the "),
            (" ", 0, ""),
            ("s ", 0, " "),
            ("", 0, " of "),
            ("", 1, ""),
            ("", 0, " and "),
            ("", 4, ""),
            ("", 12, ""),
            (", ", 0, " "),
            ("", 0, ", "),
            (" ", 1, " "),
            ("", 0, " in "),
            ("", 0, " to "),
            ("e ", 0, " "),
            ("", 0, "\x22"),
            ("", 0, "."),
            ("", 0, "\x22>"),
            ("", 0, "\n"),
            ("", 14, ""),
            ("", 0, "]"),
            ("", 0, " for "),
            ("", 5, ""),
            ("", 13, ""),
            ("", 0, " a "),
            ("", 0, " that "),
            (" ", 1, ""),
            ("", 0, ". "),
            (".", 0, ""),
            (" ", 0, ", "),
            ("", 6, ""),
            ("", 0, " with "),
            ("", 0, "'"),
            ("", 0, " from "),
            ("", 0, " by "),
            ("", 7, ""),
            ("", 8, ""),
            (" the ", 0, ""),
            ("", 15, ""),
            ("", 0, ". The "),
            ("", 2, ""),
            ("", 0, " on "),
            ("", 0, " as "),
            ("", 0, " is "),
            ("", 18, ""),
            ("", 12, "ing "),
            ("", 0, "\n\x09"),
            ("", 0, ":"),
            (" ", 0, ". "),
            ("", 0, "ed "),
            ("", 11, ""),
            ("", 9, ""),
            ("", 17, ""),
            ("", 0, "("),
            ("", 1, ", "),
            ("", 19, ""),
            ("", 0, " at "),
            ("", 0, "ly "),
            (" the ", 0, " of "),
            ("", 16, ""),
            ("", 20, ""),
            (" ", 1, ", "),
            ("", 1, "\x22"),
            (".", 0, "("),
            ("", 2, " "),
            ("", 1, "\x22>"),
            ("", 0, "=\x22"),
            (" ", 0, "."),
            (".com/", 0, ""),
            (" the ", 0, " of the "),
            ("", 1, "'"),
            ("", 0, ". This "),
            ("", 0, ","),
            (".", 0, " "),
            ("", 1, "("),
            ("", 1, "."),
            ("", 0, " not "),
            (" ", 0, "=\x22"),
            ("", 0, "er "),
            (" ", 2, " "),
            ("", 0, "al "),
            (" ", 2, ""),
            ("", 0, "='"),
            ("", 2, "\x22"),
            ("", 1, ". "),
            (" ", 0, "("),
            ("", 0, "ful "),
            (" ", 1, ". "),
            ("", 0, "ive "),
            ("", 0, "less "),
            ("", 2, "'"),
            ("", 0, "est "),
            (" ", 1, "."),
            ("", 2, "\x22>"),
            (" ", 0, "='"),
            ("", 1, ","),
            ("", 0, "ize "),
            ("", 2, "."),
            ("\xc2\xa0", 0, ""),
            (" ", 0, ","),
            ("", 1, "=\x22"),
            ("", 2, "=\x22"),
            ("", 0, "ous "),
            ("", 2, ", "),
            ("", 1, "='"),
            (" ", 1, ","),
            (" ", 2, "=\x22"),
            (" ", 2, ", "),
            ("", 2, ","),
            ("", 2, "("),
            ("", 2, ". "),
            (" ", 2, "."),
            ("", 2, "='"),
            (" ", 2, ". "),
            (" ", 1, "=\x22"),
            (" ", 2, "='"),
            (" ", 1, "='"),
        };

        static int Ferment(byte[] w, int start, int len, int pos)
        {
            int p = start + pos;
            if (w[p] < 192)
            {
                if (w[p] >= 97 && w[p] <= 122) w[p] ^= 32;
                return 1;
            }
            if (w[p] < 224)
            {
                if (pos + 1 < len) w[p + 1] ^= 32;
                return 2;
            }
            if (pos + 2 < len) w[p + 2] ^= 5;
            return 3;
        }

        /// <summary>Writes the transformed dictionary word into buf, returns its length.</summary>
        static int TransformWord(byte[] buf, int wordOffset, int wordLen, int transform)
        {
            var (prefix, kind, suffix) = Transforms[transform];
            var dict = Dictionary;
            int n = 0;
            foreach (char c in prefix) buf[n++] = (byte)c;
            int start = n;
            int skip = kind >= 3 && kind <= 11 ? kind - 2 : 0;
            int cut = kind >= 12 ? kind - 11 : 0;
            int len = wordLen - skip - cut;
            if (len < 0) len = 0;
            for (int i = 0; i < len; i++) buf[n++] = dict[wordOffset + skip + i];
            if (kind == 1 && len > 0) Ferment(buf, start, len, 0);
            else if (kind == 2)
            {
                int i = 0;
                while (i < len) i += Ferment(buf, start, len, i);
            }
            foreach (char c in suffix) buf[n++] = (byte)c;
            return n;
        }

        // ------------------------------------------------------------------ tables (RFC 7932 sections 5, 6, 7.1)

        static readonly int[] BlockCountBase = { 1, 5, 9, 13, 17, 25, 33, 41, 49, 65, 81, 97, 113, 145, 177, 209, 241, 305, 369, 497, 753, 1265, 2289, 4337, 8433, 16625 };
        static readonly byte[] BlockCountExtra = { 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 6, 6, 7, 8, 9, 10, 11, 12, 13, 24 };
        static readonly int[] InsertBase = { 0, 1, 2, 3, 4, 5, 6, 8, 10, 14, 18, 26, 34, 50, 66, 98, 130, 194, 322, 578, 1090, 2114, 6210, 22594 };
        static readonly byte[] InsertExtra = { 0, 0, 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 7, 8, 9, 10, 12, 14, 24 };
        static readonly int[] CopyBase = { 2, 3, 4, 5, 6, 7, 8, 9, 10, 12, 14, 18, 22, 30, 38, 54, 70, 102, 134, 198, 326, 582, 1094, 2118 };
        static readonly byte[] CopyExtra = { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 7, 8, 9, 10, 24 };
        // insert / copy code bases of the 11 cells of 64 insert-and-copy codes
        static readonly byte[] CellInsert = { 0, 0, 0, 0, 8, 8, 0, 16, 8, 16, 16 };
        static readonly byte[] CellCopy = { 0, 8, 0, 8, 0, 8, 16, 0, 16, 8, 16 };

        static readonly byte[] Lut0 =
        {
            0, 0, 0, 0, 0, 0, 0, 0, 0, 4, 4, 0, 0, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            8, 12, 16, 12, 12, 20, 12, 16, 24, 28, 12, 12, 32, 12, 36, 12, 44, 44, 44, 44, 44, 44, 44, 44, 44, 44, 32, 32, 24, 40, 28, 12,
            12, 48, 52, 52, 52, 48, 52, 52, 52, 48, 52, 52, 52, 52, 52, 48, 52, 52, 52, 52, 52, 48, 52, 52, 52, 52, 52, 24, 12, 28, 12, 12,
            12, 56, 60, 60, 60, 56, 60, 60, 60, 56, 60, 60, 60, 60, 60, 56, 60, 60, 60, 60, 60, 56, 60, 60, 60, 60, 60, 24, 12, 28, 12, 0,
            0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1,
            0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1,
            2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3,
            2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3,
        };
        static readonly byte[] Lut1 =
        {
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1,
            1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1,
            1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 1, 1, 1, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
        };
        static readonly byte[] Lut2 =
        {
            0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
            2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
            3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
            3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
            4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4,
            4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4,
            5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5,
            5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 7,
        };

        // ------------------------------------------------------------------ decoding

        sealed class BlockCategory
        {
            public int NumTypes = 1, Type, PrevType = 1;
            public long Count = 1 << 24;
            public JxlPrefixCode? TypeCode, CountCode;

            public void Switch(JxlBitReader br)
            {
                int sym = TypeCode!.Decode(br);
                int t = sym == 0 ? PrevType : sym == 1 ? (Type + 1) % NumTypes : sym - 2;
                if (t >= NumTypes) throw Bad("block type out of range");
                PrevType = Type;
                Type = t;
                Count = ReadBlockCount(br, CountCode!);
            }
        }

        static ImageFormatException Bad(string what) => new("Brotli: " + what);

        static int ReadBlockCount(JxlBitReader br, JxlPrefixCode code)
        {
            int c = code.Decode(br);
            return BlockCountBase[c] + (int)br.ReadBits(BlockCountExtra[c]);
        }

        static int ReadVarCount(JxlBitReader br)
        {
            if (!br.ReadBool()) return 1;
            int n = (int)br.ReadBits(3);
            return (1 << n) + 1 + (int)br.ReadBits(n);
        }

        static byte[] ReadContextMap(JxlBitReader br, int size, int numTrees)
        {
            var map = new byte[size];
            if (numTrees < 2) return map;
            int rleMax = br.ReadBool() ? (int)br.ReadBits(4) + 1 : 0;
            var code = JxlPrefixCode.Read(br, numTrees + rleMax);
            for (int i = 0; i < size;)
            {
                int sym = code.Decode(br);
                if (sym == 0) map[i++] = 0;
                else if (sym <= rleMax)
                {
                    int run = (1 << sym) + (int)br.ReadBits(sym);
                    if (i + run > size) throw Bad("context map run too long");
                    i += run;   // already zero
                }
                else map[i++] = (byte)(sym - rleMax);
                if (br.Overrun) throw Bad("truncated context map");
            }
            if (br.ReadBool())
            {
                Span<byte> mtf = stackalloc byte[256];
                for (int i = 0; i < 256; i++) mtf[i] = (byte)i;
                for (int i = 0; i < size; i++)
                {
                    int idx = map[i];
                    byte v = mtf[idx];
                    map[i] = v;
                    for (; idx > 0; idx--) mtf[idx] = mtf[idx - 1];
                    mtf[0] = v;
                }
            }
            for (int i = 0; i < size; i++) if (map[i] >= numTrees) throw Bad("context map value out of range");
            return map;
        }

        static void ReadCategory(JxlBitReader br, BlockCategory cat)
        {
            cat.NumTypes = ReadVarCount(br);
            cat.Type = 0; cat.PrevType = 1;
            if (cat.NumTypes >= 2)
            {
                cat.TypeCode = JxlPrefixCode.Read(br, cat.NumTypes + 2);
                cat.CountCode = JxlPrefixCode.Read(br, 26);
                cat.Count = ReadBlockCount(br, cat.CountCode);
            }
            else cat.Count = 1 << 24;
        }

        /// <summary>Decompresses a complete Brotli stream; at most maxOutput bytes are produced.</summary>
        public static byte[] Decompress(ReadOnlySpan<byte> source, int maxOutput)
        {
            var src = source.ToArray();
            var br = new JxlBitReader(src);
            int wbits;
            if (!br.ReadBool()) wbits = 16;
            else
            {
                int n = (int)br.ReadBits(3);
                if (n != 0) wbits = 17 + n;
                else
                {
                    int m = (int)br.ReadBits(3);
                    if (m == 1) throw Bad("invalid window size");
                    wbits = m == 0 ? 17 : 8 + m;
                }
            }
            long windowSize = (1L << wbits) - 16;
            var outp = new byte[Math.Min(Math.Max(1024, source.Length * 4), maxOutput)];
            int pos = 0;
            int[] dist = { 16, 15, 11, 4 };   // ring buffer, dist[3] = last
            var word = new byte[64];
            void Ensure(long need)
            {
                if (need > maxOutput) throw Bad("output exceeds the allowed size");
                if (need <= outp.Length) return;
                long n = Math.Max(need, Math.Min((long)outp.Length * 2, maxOutput));
                Array.Resize(ref outp, (int)n);
            }
            while (true)
            {
                bool isLast = br.ReadBool();
                if (isLast && br.ReadBool()) break;   // ISLASTEMPTY
                int nib = (int)br.ReadBits(2);
                if (nib == 3)
                {
                    if (br.ReadBool()) throw Bad("reserved bit set");
                    int skipBytes = (int)br.ReadBits(2);
                    long skipLen = 0;
                    for (int i = 0; i < skipBytes; i++) skipLen |= (long)br.ReadBits(8) << (8 * i);
                    if (skipBytes > 0) skipLen++;
                    br.ZeroPadToByte();
                    if (skipLen > src.Length) throw Bad("metadata block exceeds the data");
                    for (long i = 0; i < skipLen; i++) br.ReadBits(8);
                    br.CheckEnd();
                    if (isLast) break;
                    continue;
                }
                int nibbles = nib + 4;
                long mlen = 0;
                for (int i = 0; i < nibbles; i++) mlen |= (long)br.ReadBits(4) << (4 * i);
                mlen++;
                if (!isLast && br.ReadBool())
                {
                    // uncompressed meta-block
                    br.ZeroPadToByte();
                    if (mlen > src.Length) throw Bad("uncompressed block exceeds the data");
                    Ensure(pos + mlen);
                    for (long i = 0; i < mlen; i++) outp[pos++] = (byte)br.ReadBits(8);
                    br.CheckEnd();
                    continue;
                }
                var L = new BlockCategory(); var I = new BlockCategory(); var D = new BlockCategory();
                ReadCategory(br, L); ReadCategory(br, I); ReadCategory(br, D);
                int npostfix = (int)br.ReadBits(2);
                int ndirect = (int)br.ReadBits(4) << npostfix;
                var modes = new byte[L.NumTypes];
                for (int i = 0; i < L.NumTypes; i++) modes[i] = (byte)br.ReadBits(2);
                int ntreesL = ReadVarCount(br);
                var cmapL = ReadContextMap(br, 64 * L.NumTypes, ntreesL);
                int ntreesD = ReadVarCount(br);
                var cmapD = ReadContextMap(br, 4 * D.NumTypes, ntreesD);
                var litCodes = new JxlPrefixCode[ntreesL];
                for (int i = 0; i < ntreesL; i++) litCodes[i] = JxlPrefixCode.Read(br, 256);
                var cmdCodes = new JxlPrefixCode[I.NumTypes];
                for (int i = 0; i < I.NumTypes; i++) cmdCodes[i] = JxlPrefixCode.Read(br, 704);
                int distAlphabet = 16 + ndirect + (48 << npostfix);
                var distCodes = new JxlPrefixCode[ntreesD];
                for (int i = 0; i < ntreesD; i++) distCodes[i] = JxlPrefixCode.Read(br, distAlphabet);
                br.CheckEnd();
                long end = pos + mlen;
                Ensure(end);
                while (pos < end)
                {
                    if (I.Count == 0) I.Switch(br);
                    I.Count--;
                    int cmd = cmdCodes[I.Type].Decode(br);
                    int cell = cmd >> 6;
                    int insCode = CellInsert[cell] + ((cmd >> 3) & 7), copyCode = CellCopy[cell] + (cmd & 7);
                    long ilen = InsertBase[insCode] + br.ReadBits(InsertExtra[insCode]);
                    long clen = CopyBase[copyCode] + br.ReadBits(CopyExtra[copyCode]);
                    bool implicitZero = cmd < 128;
                    if (pos + ilen > end) throw Bad("insert length exceeds the meta-block");
                    for (long k = 0; k < ilen; k++)
                    {
                        if (L.Count == 0) L.Switch(br);
                        L.Count--;
                        int p1 = pos > 0 ? outp[pos - 1] : 0, p2 = pos > 1 ? outp[pos - 2] : 0;
                        int cid = modes[L.Type] switch
                        {
                            0 => p1 & 0x3f,
                            1 => p1 >> 2,
                            2 => Lut0[p1] | Lut1[p2],
                            _ => (Lut2[p1] << 3) | Lut2[p2],
                        };
                        outp[pos++] = (byte)litCodes[cmapL[64 * L.Type + cid]].Decode(br);
                    }
                    if (br.Overrun) throw Bad("unexpected end of data");
                    if (pos >= end) break;
                    long distance;
                    bool pushes = false;
                    if (implicitZero) distance = dist[3];
                    else
                    {
                        if (D.Count == 0) D.Switch(br);
                        D.Count--;
                        int cidd = clen > 4 ? 3 : (int)clen - 2;
                        int dcode = distCodes[cmapD[4 * D.Type + cidd]].Decode(br);
                        if (dcode < 16)
                        {
                            distance = dcode switch
                            {
                                0 => dist[3],
                                1 => dist[2],
                                2 => dist[1],
                                3 => dist[0],
                                4 => dist[3] - 1, 5 => dist[3] + 1, 6 => dist[3] - 2, 7 => dist[3] + 2, 8 => dist[3] - 3, 9 => dist[3] + 3,
                                10 => dist[2] - 1, 11 => dist[2] + 1, 12 => dist[2] - 2, 13 => dist[2] + 2, 14 => dist[2] - 3, _ => dist[2] + 3,
                            };
                            if (distance <= 0) throw Bad("invalid distance");
                            pushes = dcode != 0;
                        }
                        else if (dcode < 16 + ndirect) { distance = dcode - 15; pushes = true; }
                        else
                        {
                            int x = dcode - ndirect - 16;
                            int ndistbits = 1 + (x >> (npostfix + 1));
                            int hcode = x >> npostfix, lcode = x & ((1 << npostfix) - 1);
                            long offset = ((2L + (hcode & 1)) << ndistbits) - 4;
                            distance = ((offset + br.ReadBits(ndistbits)) << npostfix) + lcode + ndirect + 1;
                            pushes = true;
                        }
                    }
                    long maxDist = Math.Min(windowSize, pos);
                    if (distance <= maxDist)
                    {
                        if (pushes) { dist[0] = dist[1]; dist[1] = dist[2]; dist[2] = dist[3]; dist[3] = (int)distance; }
                        if (pos + clen > end) throw Bad("copy length exceeds the meta-block");
                        int from = pos - (int)distance;
                        for (long k = 0; k < clen; k++) outp[pos++] = outp[from++];
                    }
                    else
                    {
                        // static dictionary reference
                        if (clen < 4 || clen > 24) throw Bad("bad dictionary word length");
                        long wordId = distance - (maxDist + 1);
                        int nbits = NdBits[clen];
                        long index = wordId & ((1L << nbits) - 1);
                        long transform = wordId >> nbits;
                        if (transform > 120) throw Bad("bad dictionary transform");
                        int n = TransformWord(word, DOffset[clen] + (int)index * (int)clen, (int)clen, (int)transform);
                        if (pos + n > end) throw Bad("dictionary word exceeds the meta-block");
                        Array.Copy(word, 0, outp, pos, n);
                        pos += n;
                    }
                    if (br.Overrun) throw Bad("unexpected end of data");
                }
                if (isLast) break;
            }
            br.CheckEnd();
            return outp.AsSpan(0, pos).ToArray();
        }
    }
}
