using System;
using System.Buffers.Binary;

namespace PixCodec
{
    /// <summary>
    /// ETC1, ETC2 and EAC block decoding, written from the Khronos Data Format Specification 1.3 (sections 21 and 22).
    /// Every format is specified with exact integer results; the decoders here are bit exact.
    /// <para>
    /// Texel order: blocks are 4x4, output is row-major (texel (x, y) at index y * 4 + x). Colour output is RGBA8, 4 bytes
    /// per texel (64 bytes per block). The EAC R11 / RG11 formats keep their 11 bit precision: the unsigned variants
    /// return the 11 bit value extended to 16 bits by bit replication, <c>(x &lt;&lt; 5) | (x &gt;&gt; 6)</c>, the signed
    /// variants the sign-magnitude replication <c>±((|x| &lt;&lt; 5) | (|x| &gt;&gt; 5))</c> in -32767..32767, both as the
    /// specification prescribes for implementations working with 16 bits (UNORM16 / SNORM16). <see cref="Eac11"/> gives
    /// the 11 bit values themselves.
    /// </para>
    /// </summary>
    internal static class EtcBlocks
    {
        // ------------------------------------------------------------------ tables (Khronos Data Format Specification)

        // Table 129: intensity modifier sets for 'individual' and 'differential' modes, the large and small value (b, a)
        static readonly int[] ModLarge = { 8, 17, 29, 42, 60, 80, 106, 183 };
        static readonly int[] ModSmall = { 2, 5, 9, 13, 18, 24, 33, 47 };

        // Table 131: distance table for 'T' and 'H' modes
        static readonly int[] Distance = { 3, 6, 11, 16, 23, 32, 41, 64 };

        // Table 133: intensity modifier sets for the EAC alpha / R11 component, 8 per table index
        static readonly sbyte[] EacMod =
        {
            -3, -6, -9, -15, 2, 5, 8, 14,
            -3, -7, -10, -13, 2, 6, 9, 12,
            -2, -5, -8, -13, 1, 4, 7, 12,
            -2, -4, -6, -13, 1, 3, 5, 12,
            -3, -6, -8, -12, 2, 5, 7, 11,
            -3, -7, -9, -11, 2, 6, 8, 10,
            -4, -7, -8, -11, 3, 6, 7, 10,
            -3, -5, -8, -11, 2, 4, 7, 10,
            -2, -6, -8, -10, 1, 5, 7, 9,
            -2, -5, -8, -10, 1, 4, 7, 9,
            -2, -4, -8, -10, 1, 3, 7, 9,
            -2, -5, -7, -10, 1, 4, 6, 9,
            -3, -4, -7, -10, 2, 3, 6, 9,
            -1, -2, -3, -10, 0, 1, 2, 9,
            -4, -6, -8, -9, 3, 5, 7, 8,
            -3, -5, -7, -9, 2, 4, 6, 8,
        };

        static int Clamp255(int v) => v < 0 ? 0 : v > 255 ? 255 : v;
        static int Ext4(int v) => v * 17;
        static int Ext5(int v) => v << 3 | v >> 2;
        static int Ext6(int v) => v << 2 | v >> 4;
        static int Ext7(int v) => v << 1 | v >> 6;
        static int Signed3(int v) => (v ^ 4) - 4;

        // ------------------------------------------------------------------ ETC1 / ETC2 colour

        /// <summary>ETC1 (8 bytes): 16 RGBA texels into <paramref name="rgba"/> (64 bytes), alpha 255. ETC1 is a subset of
        /// ETC2 RGB; blocks whose differential base colours overflow (undefined in ETC1) decode as ETC2.</summary>
        public static void Etc1(ReadOnlySpan<byte> b, Span<byte> rgba) => Color(b, rgba, false);

        /// <summary>ETC2 RGB (and its sRGB variant, which stores the same values; 8 bytes): 16 RGBA texels, alpha 255.</summary>
        public static void Etc2Rgb(ReadOnlySpan<byte> b, Span<byte> rgba) => Color(b, rgba, false);

        /// <summary>ETC2 RGB with punch-through alpha (RGB8A1, 8 bytes): 16 RGBA texels, alpha 0 or 255 (transparent texels
        /// are (0, 0, 0, 0)).</summary>
        public static void Etc2PunchThrough(ReadOnlySpan<byte> b, Span<byte> rgba) => Color(b, rgba, true);

        /// <summary>ETC2 RGBA8 (EAC alpha, 16 bytes: 8 alpha then 8 colour): 16 RGBA texels.</summary>
        public static void Etc2Rgba(ReadOnlySpan<byte> b, Span<byte> rgba)
        {
            Color(b.Slice(8), rgba, false);
            EacAlpha8(b, rgba.Slice(3), 4);
        }

        /// <summary>
        /// The colour part of every ETC format. <paramref name="punchThrough"/>: RGB8A1, where bit 33 is the 'opaque' bit
        /// instead of the differential bit (and there is no individual mode).
        /// </summary>
        static void Color(ReadOnlySpan<byte> b, Span<byte> o, bool punchThrough)
        {
            ulong v = BinaryPrimitives.ReadUInt64BigEndian(b);
            uint hi = (uint)(v >> 32), idx = (uint)v;
            bool bit33 = (hi & 2) != 0;
            bool opaque = !punchThrough || bit33;
            Span<int> paint = stackalloc int[12];   // four paint colours (T / H modes)

            if (!punchThrough && !bit33)
            {
                // 'individual' mode: two 4 bit base colours
                int r1 = Ext4((int)(hi >> 28) & 15), r2 = Ext4((int)(hi >> 24) & 15);
                int g1 = Ext4((int)(hi >> 20) & 15), g2 = Ext4((int)(hi >> 16) & 15);
                int b1 = Ext4((int)(hi >> 12) & 15), b2 = Ext4((int)(hi >> 8) & 15);
                SubBlocks(o, hi, idx, r1, g1, b1, r2, g2, b2, true);
                return;
            }

            int r = (int)(hi >> 27) & 31, dr = Signed3((int)(hi >> 24) & 7);
            int g = (int)(hi >> 19) & 31, dg = Signed3((int)(hi >> 16) & 7);
            int bl = (int)(hi >> 11) & 31, db = Signed3((int)(hi >> 8) & 7);

            if ((uint)(r + dr) > 31)
            {
                // 'T' mode
                int r1 = Ext4(((int)(hi >> 27) & 3) << 2 | (int)(hi >> 24) & 3);
                int g1 = Ext4((int)(hi >> 20) & 15), b1 = Ext4((int)(hi >> 16) & 15);
                int r2 = Ext4((int)(hi >> 12) & 15), g2 = Ext4((int)(hi >> 8) & 15), b2 = Ext4((int)(hi >> 4) & 15);
                int d = Distance[(int)(hi >> 1) & 6 | (int)hi & 1];
                Set(paint, 0, r1, g1, b1);
                Set(paint, 1, r2 + d, g2 + d, b2 + d);
                Set(paint, 2, r2, g2, b2);
                Set(paint, 3, r2 - d, g2 - d, b2 - d);
                PaintColors(o, idx, paint, opaque);
            }
            else if ((uint)(g + dg) > 31)
            {
                // 'H' mode
                int r1 = Ext4((int)(hi >> 27) & 15);
                int g1 = Ext4(((int)(hi >> 24) & 7) << 1 | (int)(hi >> 20) & 1);
                int b1 = Ext4(((int)(hi >> 19) & 1) << 3 | (int)(hi >> 15) & 7);
                int r2 = Ext4((int)(hi >> 11) & 15), g2 = Ext4((int)(hi >> 7) & 15), b2 = Ext4((int)(hi >> 3) & 15);
                int lsb = (r1 << 16 | g1 << 8 | b1) >= (r2 << 16 | g2 << 8 | b2) ? 1 : 0;
                int d = Distance[(int)hi & 4 | ((int)hi & 1) << 1 | lsb];
                Set(paint, 0, r1 + d, g1 + d, b1 + d);
                Set(paint, 1, r1 - d, g1 - d, b1 - d);
                Set(paint, 2, r2 + d, g2 + d, b2 + d);
                Set(paint, 3, r2 - d, g2 - d, b2 - d);
                PaintColors(o, idx, paint, opaque);
            }
            else if ((uint)(bl + db) > 31)
            {
                // 'planar' mode (always opaque)
                int ro = Ext6((int)(v >> 57) & 63);
                int go = Ext7(((int)(v >> 56) & 1) << 6 | (int)(v >> 49) & 63);
                int bo = Ext6(((int)(v >> 48) & 1) << 5 | ((int)(v >> 43) & 3) << 3 | (int)(v >> 39) & 7);
                int rh = Ext6(((int)(v >> 34) & 31) << 1 | (int)(v >> 32) & 1);
                int gh = Ext7((int)(v >> 25) & 127), bh = Ext6((int)(v >> 19) & 63);
                int rv = Ext6((int)(v >> 13) & 63), gv = Ext7((int)(v >> 6) & 127), bv = Ext6((int)v & 63);
                for (int y = 0; y < 4; y++)
                    for (int x = 0; x < 4; x++)
                    {
                        int p = (y * 4 + x) * 4;
                        o[p] = (byte)Clamp255((x * (rh - ro) + y * (rv - ro) + 4 * ro + 2) >> 2);
                        o[p + 1] = (byte)Clamp255((x * (gh - go) + y * (gv - go) + 4 * go + 2) >> 2);
                        o[p + 2] = (byte)Clamp255((x * (bh - bo) + y * (bv - bo) + 4 * bo + 2) >> 2);
                        o[p + 3] = 255;
                    }
            }
            else
            {
                // 'differential' mode
                SubBlocks(o, hi, idx, Ext5(r), Ext5(g), Ext5(bl), Ext5(r + dr), Ext5(g + dg), Ext5(bl + db), opaque);
            }
        }

        static void Set(Span<int> p, int i, int r, int g, int b)
        {
            p[i * 3] = Clamp255(r); p[i * 3 + 1] = Clamp255(g); p[i * 3 + 2] = Clamp255(b);
        }

        /// <summary>Individual / differential modes: two sub-blocks, each a base colour plus a modifier table.</summary>
        static void SubBlocks(Span<byte> o, uint hi, uint idx, int r1, int g1, int b1, int r2, int g2, int b2, bool opaque)
        {
            bool flip = (hi & 1) != 0;
            int t1 = (int)(hi >> 5) & 7, t2 = (int)(hi >> 2) & 7;
            for (int k = 0; k < 16; k++)
            {
                int x = k >> 2, y = k & 3;   // pixels a..p run down the columns
                bool second = flip ? y >= 2 : x >= 2;
                int t = second ? t2 : t1;
                int i = (int)(idx >> k) & 1 | (int)(idx >> (k + 15)) & 2;
                int p = (y * 4 + x) * 4;
                if (!opaque && i == 2)
                {
                    o[p] = o[p + 1] = o[p + 2] = o[p + 3] = 0;
                    continue;
                }
                // Table 130: 0 -> +a, 1 -> +b, 2 -> -a, 3 -> -b; without the opaque bit (Table 136 / 138) a is 0
                int m = (i & 1) != 0 ? ModLarge[t] : opaque ? ModSmall[t] : 0;
                if ((i & 2) != 0) m = -m;
                o[p] = (byte)Clamp255((second ? r2 : r1) + m);
                o[p + 1] = (byte)Clamp255((second ? g2 : g1) + m);
                o[p + 2] = (byte)Clamp255((second ? b2 : b1) + m);
                o[p + 3] = 255;
            }
        }

        /// <summary>T / H modes: each pixel index selects one of four paint colours.</summary>
        static void PaintColors(Span<byte> o, uint idx, ReadOnlySpan<int> paint, bool opaque)
        {
            for (int k = 0; k < 16; k++)
            {
                int i = (int)(idx >> k) & 1 | (int)(idx >> (k + 15)) & 2;
                int p = ((k & 3) * 4 + (k >> 2)) * 4;
                if (!opaque && i == 2)
                {
                    o[p] = o[p + 1] = o[p + 2] = o[p + 3] = 0;
                    continue;
                }
                o[p] = (byte)paint[i * 3]; o[p + 1] = (byte)paint[i * 3 + 1]; o[p + 2] = (byte)paint[i * 3 + 2]; o[p + 3] = 255;
            }
        }

        // ------------------------------------------------------------------ EAC

        /// <summary>The EAC 3 bit index of texel k (row-major) in the low 48 bits: pixel a..p (down the columns) from bit 47.</summary>
        static int EacIndex(ulong v, int k) => (int)(v >> (45 - 3 * ((k & 3) * 4 + (k >> 2)))) & 7;

        /// <summary>The alpha part of ETC2 RGBA8 (8 bytes): 16 8 bit values written to o[i * stride], i row-major.</summary>
        public static void EacAlpha8(ReadOnlySpan<byte> b, Span<byte> o, int stride)
        {
            ulong v = BinaryPrimitives.ReadUInt64BigEndian(b);
            int baseCw = (int)(v >> 56), mul = (int)(v >> 52) & 15, tab = ((int)(v >> 48) & 15) * 8;
            for (int k = 0; k < 16; k++) o[k * stride] = (byte)Clamp255(baseCw + EacMod[tab + EacIndex(v, k)] * mul);
        }

        /// <summary>
        /// One EAC R11 channel (8 bytes): the 16 exact 11 bit values, row-major, unsigned 0..2047 or (<paramref name="signed"/>)
        /// -1023..1023.
        /// </summary>
        public static void Eac11(ReadOnlySpan<byte> b, bool signed, Span<int> values)
        {
            ulong v = BinaryPrimitives.ReadUInt64BigEndian(b);
            int mul = (int)(v >> 52) & 15, tab = ((int)(v >> 48) & 15) * 8;
            if (signed)
            {
                int baseCw = Math.Max((int)(sbyte)(v >> 56), -127) * 8;
                for (int k = 0; k < 16; k++)
                {
                    int m = EacMod[tab + EacIndex(v, k)];
                    values[k] = Math.Clamp(baseCw + (mul != 0 ? m * mul * 8 : m), -1023, 1023);
                }
            }
            else
            {
                int baseCw = (int)(v >> 56) * 8 + 4;
                for (int k = 0; k < 16; k++)
                {
                    int m = EacMod[tab + EacIndex(v, k)];
                    values[k] = Math.Clamp(baseCw + (mul != 0 ? m * mul * 8 : m), 0, 2047);
                }
            }
        }

        /// <summary>11 bit unsigned to 16 bits by bit replication.</summary>
        public static ushort Extend11(int x) => (ushort)(x << 5 | x >> 6);

        /// <summary>11 bit signed (-1023..1023) to 16 bits: replicated magnitude, sign restored.</summary>
        public static short Extend11Signed(int x) => x >= 0 ? (short)(x << 5 | x >> 5) : (short)-(-x << 5 | -x >> 5);

        /// <summary>Unsigned R11 EAC (8 bytes): 16 UNORM16 values written to o[i * stride], i row-major. For RG11 call it
        /// for each 8 byte half (red first) with stride 2.</summary>
        public static void EacR11(ReadOnlySpan<byte> b, Span<ushort> o, int stride)
        {
            Span<int> t = stackalloc int[16];
            Eac11(b, false, t);
            for (int k = 0; k < 16; k++) o[k * stride] = Extend11(t[k]);
        }

        /// <summary>Signed R11 EAC (8 bytes): 16 SNORM16 values written to o[i * stride], i row-major.</summary>
        public static void EacR11Signed(ReadOnlySpan<byte> b, Span<short> o, int stride)
        {
            Span<int> t = stackalloc int[16];
            Eac11(b, true, t);
            for (int k = 0; k < 16; k++) o[k * stride] = Extend11Signed(t[k]);
        }

        /// <summary>Unsigned RG11 EAC (16 bytes): 16 interleaved RG UNORM16 texels (32 values).</summary>
        public static void EacRg11(ReadOnlySpan<byte> b, Span<ushort> rg)
        {
            EacR11(b, rg, 2);
            EacR11(b.Slice(8), rg.Slice(1), 2);
        }

        /// <summary>Signed RG11 EAC (16 bytes): 16 interleaved RG SNORM16 texels (32 values).</summary>
        public static void EacRg11Signed(ReadOnlySpan<byte> b, Span<short> rg)
        {
            EacR11Signed(b, rg, 2);
            EacR11Signed(b.Slice(8), rg.Slice(1), 2);
        }
    }
}
