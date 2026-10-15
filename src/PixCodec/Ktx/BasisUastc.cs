using System;
using System.Buffers.Binary;

namespace PixCodec
{
    /// <summary>
    /// UASTC LDR 4x4 block decoding, written from Binomial's public domain "UASTC LDR 4x4 Texture Specification" (block
    /// layouts, partition / anchor tables) and the ASTC sections of the Khronos Data Format Specification (endpoint
    /// unquantization, weight application). UASTC is a 19 mode subset of 4x4 LDR ASTC with its own 128 bit packing; the
    /// result is bit exact (ASTC decode mode RGBA8, endpoints expanded as linear, not sRGB). UASTC HDR 4x4 (a different
    /// format, KTX2 DFD model UASTC with an HDR vkFormat / transfer) is not handled here.
    /// </summary>
    internal static class BasisUastc
    {
        // ------------------------------------------------------------------ mode tables (UASTC specification)

        // mode index from the low 7 bits of the first byte, and the length of each mode's code
        static readonly byte[] ModeFromBits =
        {
            11,0,10,3,11,15,12,7,11,18,10,5,11,14,12,9,11,0,10,4,11,16,12,8,11,18,10,6,11,2,12,13,
            11,0,10,3,11,17,12,7,11,18,10,5,11,14,12,9,11,0,10,4,11,1,12,8,11,18,10,6,11,2,12,13,
            11,0,10,3,11,19,12,7,11,18,10,5,11,14,12,9,11,0,10,4,11,16,12,8,11,18,10,6,11,2,12,13,
            11,0,10,3,11,17,12,7,11,18,10,5,11,14,12,9,11,0,10,4,11,1,12,8,11,18,10,6,11,2,12,13,
        };

        // per mode: components, subsets, planes, weight bits, endpoint range, pattern table (0 none, 2, 3, 7 = 2 subset
        // ASTC / 3 subset BC7), pattern field offset, pattern field bits, component selector offset (-1 none), offset
        // of the endpoint data (ETQ, else EBITS); weights follow the endpoint bits
        static readonly sbyte[,] Modes =
        {
            //  c  s  p  w   er  pt  po  pb  cs  data
            { 3, 1, 1, 4, 19, 0, 0, 0, -1, 19 },   // 0
            { 3, 1, 1, 2, 20, 0, 0, 0, -1, 21 },   // 1
            { 3, 2, 1, 3, 8, 2, 20, 5, -1, 25 },   // 2
            { 3, 3, 1, 2, 7, 3, 20, 4, -1, 24 },   // 3
            { 3, 2, 1, 2, 12, 2, 20, 5, -1, 25 },  // 4
            { 3, 1, 1, 3, 20, 0, 0, 0, -1, 20 },   // 5
            { 3, 1, 2, 2, 18, 0, 0, 0, 20, 22 },   // 6
            { 3, 2, 1, 2, 12, 7, 20, 5, -1, 25 },  // 7
            { 4, 0, 0, 0, 0, 0, 0, 0, -1, 0 },     // 8: void extent
            { 4, 2, 1, 2, 8, 2, 28, 5, -1, 33 },   // 9
            { 4, 1, 1, 4, 13, 0, 0, 0, -1, 20 },   // 10
            { 4, 1, 2, 2, 13, 0, 0, 0, 19, 21 },   // 11
            { 4, 1, 1, 3, 19, 0, 0, 0, -1, 20 },   // 12
            { 4, 1, 2, 1, 20, 0, 0, 0, 28, 30 },   // 13
            { 4, 1, 1, 2, 20, 0, 0, 0, -1, 28 },   // 14
            { 2, 1, 1, 4, 20, 0, 0, 0, -1, 30 },   // 15
            { 2, 2, 1, 2, 20, 2, 29, 5, -1, 34 },  // 16
            { 2, 1, 2, 2, 20, 0, 0, 0, -1, 29 },   // 17: component selector implied 3 (alpha)
            { 3, 1, 1, 5, 11, 0, 0, 0, -1, 19 },   // 18
        };

        // ASTC 6 bit weights for 1 to 5 bit weight indices
        static readonly byte[][] WeightValues =
        {
            Array.Empty<byte>(),
            new byte[] { 0, 64 },
            new byte[] { 0, 21, 43, 64 },
            new byte[] { 0, 9, 18, 27, 37, 46, 55, 64 },
            new byte[] { 0, 4, 8, 12, 17, 21, 25, 29, 35, 39, 43, 47, 52, 56, 60, 64 },
            new byte[] { 0, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 22, 24, 26, 28, 30, 34, 36, 38, 40, 42, 44, 46, 48, 50, 52, 54, 56, 58, 60, 62, 64 },
        };

        // partition patterns: subset of each texel (x + 4 y), as hex digits
        static readonly string[] Patterns2 =
        {
            "0011001100110011", "0001000100010001", "1000100010001000", "0001001100110111", "1111111011101100", "0011011101111111",
            "1110110010000000", "1111111011001000", "0000000000010011", "1100100000000000", "0000000101111111", "1111111111101000",
            "1110100000000000", "1111111100000000", "0000111111111111", "1111111111110000", "1000111011111111", "1111111101110001",
            "0111001100010000", "0011000100000000", "0000100011001110", "1111111101110011", "1000110011001110", "0011000100010000",
            "1111011101110011", "0110011001100110", "1111000000001111", "1010101010101010", "1111000011110000", "1001001101101100",
        };
        static readonly string[] Patterns3 =
        {
            "0000000011221122", "1111111100002222", "1111000000002222", "1111222200000000", "1120112011201120", "0112011201120112",
            "0211021102110211", "2000200021112111", "2012201220122012", "1111000022221111", "0022001100110022",
        };
        static readonly string[] Patterns7 =
        {
            "0000111100000000", "0010001000100010", "1100110010000000", "0000000100110011", "1111111100001111", "0100010001000100",
            "0001001111111111", "0111001100110011", "1100000000111100", "0111011100000000", "0000000011101110", "1100000000001100",
            "0111001100000000", "0000000111111111", "1111111111110110", "1100110011001000", "1111111110001000", "0011011011001000",
            "1111011100000000",
        };
        // The anchor texel of a subset (whose weights have one bit less) is its first texel in the pattern: that is
        // what the specification's anchor tables list.

        static readonly byte[] Single = new byte[16];
        static readonly byte[][] Partitions2 = Parse(Patterns2), Partitions3 = Parse(Patterns3), Partitions7 = Parse(Patterns7);

        static byte[][] Parse(string[] p)
        {
            var r = new byte[p.Length][];
            for (int i = 0; i < p.Length; i++)
            {
                r[i] = new byte[16];
                for (int t = 0; t < 16; t++) r[i][t] = (byte)(p[i][t] - '0');
            }
            return r;
        }

        // endpoint ranges: bits, trits (1) or quints (2)
        static (int bits, int tq) Range(int range) => range switch
        {
            7 => (2, 1), 8 => (4, 0), 11 => (5, 0), 12 => (3, 2), 13 => (4, 1), 18 => (5, 2), 19 => (6, 1), 20 => (8, 0),
            _ => throw new ArgumentOutOfRangeException(nameof(range)),
        };

        // ------------------------------------------------------------------ bits

        ref struct Bits
        {
            readonly ulong lo, hi;
            public int Pos;
            public Bits(ReadOnlySpan<byte> b) { lo = BinaryPrimitives.ReadUInt64LittleEndian(b); hi = BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(8)); Pos = 0; }
            public int Read(int n)
            {
                if (n == 0) return 0;
                ulong v = Pos >= 64 ? hi >> (Pos - 64) : Pos + n <= 64 ? lo >> Pos : lo >> Pos | hi << (64 - Pos);
                Pos += n;
                return (int)(v & ((1UL << n) - 1));
            }
        }

        // ------------------------------------------------------------------ decoding

        /// <summary>
        /// Decodes one 16 byte UASTC LDR 4x4 block to 16 RGBA8 texels (64 bytes, row-major). The reserved mode 19
        /// decodes to all zero.
        /// </summary>
        public static void DecodeBlock(ReadOnlySpan<byte> block, Span<byte> rgba)
        {
            var o = rgba.Slice(0, 64);
            int mode = ModeFromBits[block[0] & 127];
            if (mode == 19) { o.Clear(); return; }
            var r = new Bits(block);
            if (mode == 8)
            {
                r.Pos = 5;
                byte cr = (byte)r.Read(8), cg = (byte)r.Read(8), cb = (byte)r.Read(8), ca = (byte)r.Read(8);
                for (int i = 0; i < 64; i += 4) { o[i] = cr; o[i + 1] = cg; o[i + 2] = cb; o[i + 3] = ca; }
                return;
            }

            int comps = Modes[mode, 0], subsets = Modes[mode, 1], planes = Modes[mode, 2], wbits = Modes[mode, 3];
            int range = Modes[mode, 4], patTable = Modes[mode, 5];
            int ccs = -1;
            if (planes == 2) { ccs = 3; if (Modes[mode, 8] >= 0) { r.Pos = Modes[mode, 8]; ccs = r.Read(2); } }

            // partition
            byte[] subset = Single;
            if (patTable != 0)
            {
                r.Pos = Modes[mode, 6];
                int pat = r.Read(Modes[mode, 7]);
                var tab = patTable == 2 ? Partitions2 : patTable == 3 ? Partitions3 : Partitions7;
                if (pat >= tab.Length) { o.Clear(); return; }   // invalid pattern index
                subset = tab[pat];
            }

            // endpoints: all trit / quint codes first, then the low bits of every value
            r.Pos = Modes[mode, 9];
            int nv = comps * 2 * subsets;
            var (bits, tq) = Range(range);
            Span<int> ev = stackalloc int[18];
            ev.Clear();
            if (tq != 0)
            {
                int bse = tq == 1 ? 3 : 5, group = tq == 1 ? 5 : 3;
                for (int g = 0; g < nv; g += group)
                {
                    int k = Math.Min(group, nv - g), max = 1;
                    for (int i = 0; i < k; i++) max *= bse;
                    int code = r.Read(32 - System.Numerics.BitOperations.LeadingZeroCount((uint)(max - 1)));
                    for (int i = 0; i < k; i++) { ev[g + i] = code % bse; code /= bse; }
                }
            }
            for (int i = 0; i < nv; i++) ev[i] = Unquantize(ev[i] << bits | r.Read(bits), range);

            // endpoint colours per subset, expanded to RGBA (LA -> LLLA)
            Span<byte> lo = stackalloc byte[12], hi = stackalloc byte[12];
            for (int s = 0; s < subsets; s++)
            {
                var e = ev.Slice(s * comps * 2);
                for (int c = 0; c < 4; c++)
                {
                    int src = comps == 2 ? (c < 3 ? 0 : 1) : c;
                    if (src >= comps) { lo[s * 4 + c] = 255; hi[s * 4 + c] = 255; continue; }
                    lo[s * 4 + c] = (byte)e[src * 2];
                    hi[s * 4 + c] = (byte)e[src * 2 + 1];
                }
            }

            // weights: plain bits after the endpoints, texel order, both planes per texel; anchors have one bit less
            var wv = WeightValues[wbits];
            Span<byte> w = stackalloc byte[32];
            int seen = 0;   // subsets whose anchor has been read
            for (int t = 0; t < 16; t++)
            {
                int bit = 1 << subset[t];
                int n = (seen & bit) == 0 ? wbits - 1 : wbits;
                seen |= bit;
                for (int p = 0; p < planes; p++) w[t * planes + p] = wv[r.Read(n)];
            }

            for (int t = 0; t < 16; t++)
            {
                int s = subset[t] * 4;
                for (int c = 0; c < 4; c++)
                {
                    int wt = w[t * planes + (c == ccs ? 1 : 0)];
                    int l = lo[s + c] * 257, h = hi[s + c] * 257;
                    o[t * 4 + c] = (byte)(((l * (64 - wt) + h * wt + 32) >> 6) >> 8);
                }
            }
        }

        /// <summary>
        /// ASTC color endpoint unquantization (Khronos Data Format Specification, ASTC "Color endpoint unquantization")
        /// of an integer of the given UASTC endpoint range to 0..255.
        /// </summary>
        internal static int Unquantize(int v, int range)
        {
            var (n, tq) = Range(range);
            if (tq == 0)
            {
                int x = v << (8 - n);
                for (int s = n; s < 8; s += n) x |= x >> n;   // bit replication
                return x & 255;
            }
            int m = v & ((1 << n) - 1), d = v >> n;
            int a = (m & 1) != 0 ? 0x1FF : 0, xb = m >> 1;
            int b, c;
            if (tq == 1)
                (b, c) = n switch
                {
                    1 => (0, 204),
                    2 => (xb * 0x116, 93),
                    3 => (xb << 7 | xb << 2 | xb, 44),
                    4 => (xb << 6 | xb, 22),
                    5 => (xb << 5 | xb >> 2, 11),
                    _ => (xb << 4 | xb >> 4, 5),
                };
            else
                (b, c) = n switch
                {
                    1 => (0, 113),
                    2 => (xb << 8 | xb << 3 | xb << 2, 54),
                    3 => (xb << 7 | xb << 1 | xb >> 1, 26),
                    4 => (xb << 6 | xb >> 1, 13),
                    _ => (xb << 5 | xb >> 3, 6),
                };
            int t = d * c + b;
            t ^= a;
            return (a & 0x80) | t >> 2;
        }

        /// <summary>
        /// Decodes an image of UASTC LDR 4x4 blocks (raster order, ceil(width / 4) * ceil(height / 4) blocks of 16
        /// bytes: one image of a KTX2 level after zstd decompression) to tightly packed RGBA8, cropped to
        /// <paramref name="width"/> x <paramref name="height"/>.
        /// </summary>
        public static void DecodeImage(ReadOnlySpan<byte> blocks, int width, int height, Span<byte> rgba)
        {
            int bx = (width + 3) >> 2, by = (height + 3) >> 2;
            if (blocks.Length < (long)bx * by * 16) throw new ImageFormatException("UASTC: image data truncated");
            if (rgba.Length < (long)width * height * 4) throw new ArgumentException("output too small", nameof(rgba));
            Span<byte> px = stackalloc byte[64];
            for (int y = 0; y < by; y++)
                for (int x = 0; x < bx; x++)
                {
                    DecodeBlock(blocks.Slice((y * bx + x) * 16, 16), px);
                    int w = Math.Min(4, width - 4 * x), h = Math.Min(4, height - 4 * y);
                    for (int py = 0; py < h; py++)
                        px.Slice(py * 16, w * 4).CopyTo(rgba.Slice(((4 * y + py) * width + 4 * x) * 4));
                }
        }
    }
}
