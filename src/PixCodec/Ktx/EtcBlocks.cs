using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PixCodec
{
    /// <summary>
    /// ETC1, ETC2 and EAC block decoding, written from the Khronos Data Format Specification 1.3 (sections 21 and 22).
    /// Each block's possible colours / values are computed once (vectorized where it pays) and the texels copied from them.
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Clamp255(int v) => (uint)v <= 255 ? v : v < 0 ? 0 : 255;
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
        /// instead of the differential bit (and there is no individual mode). The (up to) eight colours a block can produce
        /// are computed once and the texels copied from them, 4 bytes at a time.
        /// </summary>
        [SkipLocalsInit]
        static void Color(ReadOnlySpan<byte> b, Span<byte> o, bool punchThrough)
        {
            ulong v = BinaryPrimitives.ReadUInt64BigEndian(b);
            uint hi = (uint)(v >> 32), idx = (uint)v;
            bool bit33 = (hi & 2) != 0;
            bool opaque = !punchThrough || bit33;
            if (o.Length < 64) throw new ArgumentException("64 bytes needed", nameof(o));
            ref uint dst = ref Unsafe.As<byte, uint>(ref MemoryMarshal.GetReference(o));
            Unsafe.SkipInit(out Palette8 pal8);
            Span<uint> pal = pal8;

            if (!punchThrough && !bit33)
            {
                // 'individual' mode: two 4 bit base colours
                int r1 = Ext4((int)(hi >> 28) & 15), r2 = Ext4((int)(hi >> 24) & 15);
                int g1 = Ext4((int)(hi >> 20) & 15), g2 = Ext4((int)(hi >> 16) & 15);
                int b1 = Ext4((int)(hi >> 12) & 15), b2 = Ext4((int)(hi >> 8) & 15);
                SubBlocks(ref dst, pal, hi, idx, r1, g1, b1, r2, g2, b2, true);
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
                pal[0] = Rgb(r1, g1, b1);
                pal[1] = Rgb(r2 + d, g2 + d, b2 + d);
                pal[2] = opaque ? Rgb(r2, g2, b2) : 0;
                pal[3] = Rgb(r2 - d, g2 - d, b2 - d);
                PaintColors(ref dst, idx, pal);
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
                pal[0] = Rgb(r1 + d, g1 + d, b1 + d);
                pal[1] = Rgb(r1 - d, g1 - d, b1 - d);
                pal[2] = opaque ? Rgb(r2 + d, g2 + d, b2 + d) : 0;
                pal[3] = Rgb(r2 - d, g2 - d, b2 - d);
                PaintColors(ref dst, idx, pal);
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
                // value at (x, y) = (x (h - o) + y (v - o) + 4 o + 2) >> 2, stepped along the rows
                // (4 texels of a row as one vector)
                var x4 = Vector128.Create(0, 1, 2, 3);
                var vr = Vector128.Create(4 * ro + 2) + x4 * Vector128.Create(rh - ro);
                var vg = Vector128.Create(4 * go + 2) + x4 * Vector128.Create(gh - go);
                var vb = Vector128.Create(4 * bo + 2) + x4 * Vector128.Create(bh - bo);
                Vector128<int> sr = Vector128.Create(rv - ro), sg = Vector128.Create(gv - go), sb = Vector128.Create(bv - bo);
                Vector128<int> zero = Vector128<int>.Zero, max = Vector128.Create(255), alpha = Vector128.Create(unchecked((int)0xFF000000));
                ref byte d8 = ref Unsafe.As<uint, byte>(ref dst);
                for (int y = 0; y < 4; y++)
                {
                    var r8 = Vector128.Min(Vector128.Max(vr >> 2, zero), max);
                    var g8 = Vector128.Min(Vector128.Max(vg >> 2, zero), max);
                    var b8 = Vector128.Min(Vector128.Max(vb >> 2, zero), max);
                    var px = r8 | g8 << 8 | b8 << 16 | alpha;
                    if (!BitConverter.IsLittleEndian) px = Vector128.Create(BinaryPrimitives.ReverseEndianness(px[0]), BinaryPrimitives.ReverseEndianness(px[1]), BinaryPrimitives.ReverseEndianness(px[2]), BinaryPrimitives.ReverseEndianness(px[3]));
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref d8, y * 16), px);
                    vr += sr; vg += sg; vb += sb;
                }
            }
            else
            {
                // 'differential' mode
                SubBlocks(ref dst, pal, hi, idx, Ext5(r), Ext5(g), Ext5(bl), Ext5(r + dr), Ext5(g + dg), Ext5(bl + db), opaque);
            }
        }

        // 8 bits spread to every 4th bit
        static readonly uint[] Spread4 = MakeSpread4();

        static uint[] MakeSpread4()
        {
            var t = new uint[256];
            for (int v = 0; v < 256; v++)
                for (int b = 0; b < 8; b++) t[v] |= (uint)(v >> b & 1) << (4 * b);
            return t;
        }

        /// <summary>The 2 bit indices of the 16 pixels (LSBs in the low half of idx, MSBs in the high half), one per 4 bit
        /// field: pixel k's index at bits 4k, 4k + 1.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ulong Indices4(uint idx)
        {
            var t = Spread4;
            ulong lo = t[idx & 255] | (ulong)t[idx >> 8 & 255] << 32;
            ulong hi = t[idx >> 16 & 255] | (ulong)t[idx >> 24] << 32;
            return lo | hi << 1;
        }

        /// <summary>Writes the 16 texels (row-major) from the palette: pixel k (down the columns) takes entry (ix &gt;&gt; 4k) &amp; 7.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Scatter(ref uint dst, ulong ix, ref uint p)
        {
            Unsafe.WriteUnaligned(ref Unsafe.As<uint, byte>(ref Unsafe.Add(ref dst, 0)), Unsafe.Add(ref p, (int)(ix >> 0) & 7));
            Unsafe.WriteUnaligned(ref Unsafe.As<uint, byte>(ref Unsafe.Add(ref dst, 4)), Unsafe.Add(ref p, (int)(ix >> 4) & 7));
            Unsafe.WriteUnaligned(ref Unsafe.As<uint, byte>(ref Unsafe.Add(ref dst, 8)), Unsafe.Add(ref p, (int)(ix >> 8) & 7));
            Unsafe.WriteUnaligned(ref Unsafe.As<uint, byte>(ref Unsafe.Add(ref dst, 12)), Unsafe.Add(ref p, (int)(ix >> 12) & 7));
            Unsafe.WriteUnaligned(ref Unsafe.As<uint, byte>(ref Unsafe.Add(ref dst, 1)), Unsafe.Add(ref p, (int)(ix >> 16) & 7));
            Unsafe.WriteUnaligned(ref Unsafe.As<uint, byte>(ref Unsafe.Add(ref dst, 5)), Unsafe.Add(ref p, (int)(ix >> 20) & 7));
            Unsafe.WriteUnaligned(ref Unsafe.As<uint, byte>(ref Unsafe.Add(ref dst, 9)), Unsafe.Add(ref p, (int)(ix >> 24) & 7));
            Unsafe.WriteUnaligned(ref Unsafe.As<uint, byte>(ref Unsafe.Add(ref dst, 13)), Unsafe.Add(ref p, (int)(ix >> 28) & 7));
            Unsafe.WriteUnaligned(ref Unsafe.As<uint, byte>(ref Unsafe.Add(ref dst, 2)), Unsafe.Add(ref p, (int)(ix >> 32) & 7));
            Unsafe.WriteUnaligned(ref Unsafe.As<uint, byte>(ref Unsafe.Add(ref dst, 6)), Unsafe.Add(ref p, (int)(ix >> 36) & 7));
            Unsafe.WriteUnaligned(ref Unsafe.As<uint, byte>(ref Unsafe.Add(ref dst, 10)), Unsafe.Add(ref p, (int)(ix >> 40) & 7));
            Unsafe.WriteUnaligned(ref Unsafe.As<uint, byte>(ref Unsafe.Add(ref dst, 14)), Unsafe.Add(ref p, (int)(ix >> 44) & 7));
            Unsafe.WriteUnaligned(ref Unsafe.As<uint, byte>(ref Unsafe.Add(ref dst, 3)), Unsafe.Add(ref p, (int)(ix >> 48) & 7));
            Unsafe.WriteUnaligned(ref Unsafe.As<uint, byte>(ref Unsafe.Add(ref dst, 7)), Unsafe.Add(ref p, (int)(ix >> 52) & 7));
            Unsafe.WriteUnaligned(ref Unsafe.As<uint, byte>(ref Unsafe.Add(ref dst, 11)), Unsafe.Add(ref p, (int)(ix >> 56) & 7));
            Unsafe.WriteUnaligned(ref Unsafe.As<uint, byte>(ref Unsafe.Add(ref dst, 15)), Unsafe.Add(ref p, (int)(ix >> 60) & 7));
        }

        /// <summary>Eight colours on the stack (no stackalloc).</summary>
        [InlineArray(8)]
        struct Palette8
        {
            uint _e;
        }

        /// <summary>An opaque texel as stored in memory (R, G, B, A bytes), each channel clamped to 0..255.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static uint Rgb(int r, int g, int b)
        {
            uint x = (uint)Clamp255(r) | (uint)Clamp255(g) << 8 | (uint)Clamp255(b) << 16 | 0xFF000000u;
            return BitConverter.IsLittleEndian ? x : BinaryPrimitives.ReverseEndianness(x);
        }

        /// <summary>Individual / differential modes: two sub-blocks, each a base colour plus a modifier table (Table 130:
        /// index 0 +a, 1 +b, 2 -a, 3 -b; without the opaque bit (Table 136 / 138) a is 0 and index 2 transparent).</summary>
        static void SubBlocks(ref uint dst, Span<uint> pal, uint hi, uint idx, int r1, int g1, int b1, int r2, int g2, int b2, bool opaque)
        {
            int t1 = (int)(hi >> 5) & 7, t2 = (int)(hi >> 2) & 7;
            int a = opaque ? ModSmall[t1] : 0, l = ModLarge[t1];
            pal[0] = Rgb(r1 + a, g1 + a, b1 + a); pal[1] = Rgb(r1 + l, g1 + l, b1 + l);
            pal[2] = opaque ? Rgb(r1 - a, g1 - a, b1 - a) : 0; pal[3] = Rgb(r1 - l, g1 - l, b1 - l);
            a = opaque ? ModSmall[t2] : 0; l = ModLarge[t2];
            pal[4] = Rgb(r2 + a, g2 + a, b2 + a); pal[5] = Rgb(r2 + l, g2 + l, b2 + l);
            pal[6] = opaque ? Rgb(r2 - a, g2 - a, b2 - a) : 0; pal[7] = Rgb(r2 - l, g2 - l, b2 - l);
            // pixels a..p run down the columns: k = x * 4 + y; the second sub-block is x >= 2 (k >= 8) or, flipped, y >= 2
            // (4 added to pixel k's 2 bit index: the 3rd bit of the 4 bit fields of SecondFlip / SecondNoFlip)
            ulong ix = Indices4(idx) | ((hi & 1) != 0 ? 0x4400440044004400UL : 0x4444444400000000UL);
            Scatter(ref dst, ix, ref MemoryMarshal.GetReference(pal));
        }

        /// <summary>T / H modes: each pixel index selects one of four paint colours.</summary>
        static void PaintColors(ref uint dst, uint idx, ReadOnlySpan<uint> pal)
        {
            Scatter(ref dst, Indices4(idx), ref MemoryMarshal.GetReference(pal));   // 2 bit indices: entries 0..3
        }

        // ------------------------------------------------------------------ EAC

        /// <summary>The alpha part of ETC2 RGBA8 (8 bytes): 16 8 bit values written to o[i * stride], i row-major.</summary>
        [SkipLocalsInit]
        public static void EacAlpha8(ReadOnlySpan<byte> b, Span<byte> o, int stride)
        {
            ulong v = BinaryPrimitives.ReadUInt64BigEndian(b);
            int mul = (int)(v >> 52) & 15, tab = ((int)(v >> 48) & 15) * 8;
            var bv = Vector128.Create((int)(v >> 56)); var sv = Vector128.Create(mul);
            Vector128<int> zero = Vector128<int>.Zero, max = Vector128.Create(255);
            ref int m = ref EacModI[tab];
            var p0 = Vector128.Min(Vector128.Max(bv + Vector128.LoadUnsafe(ref m) * sv, zero), max);
            var p1 = Vector128.Min(Vector128.Max(bv + Vector128.LoadUnsafe(ref m, 4) * sv, zero), max);
            var w16 = Vector128.Narrow(p0, p1);
            EacScatter(v, Vector128.Narrow(w16, w16).AsByte(), o, stride);
        }

        /// <summary>The 16 texels of an EAC block from its palette: pixel a..p (down the columns) from bit 47, three bits each,
        /// written row-major to o[i * stride].</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void EacScatter<T>(ulong v, Vector128<T> pal, Span<T> o, int stride) where T : unmanaged
        {
            if (stride < 1 || o.Length <= 15 * stride) throw new ArgumentException("output too small", nameof(o));
            ref T dst = ref MemoryMarshal.GetReference(o);
            ref T p = ref Unsafe.As<Vector128<T>, T>(ref pal);
            // unrolled: constant shifts and offsets
            Unsafe.Add(ref dst, 0 * stride) = Unsafe.Add(ref p, (int)(v >> 45) & 7);
            Unsafe.Add(ref dst, 4 * stride) = Unsafe.Add(ref p, (int)(v >> 42) & 7);
            Unsafe.Add(ref dst, 8 * stride) = Unsafe.Add(ref p, (int)(v >> 39) & 7);
            Unsafe.Add(ref dst, 12 * stride) = Unsafe.Add(ref p, (int)(v >> 36) & 7);
            Unsafe.Add(ref dst, 1 * stride) = Unsafe.Add(ref p, (int)(v >> 33) & 7);
            Unsafe.Add(ref dst, 5 * stride) = Unsafe.Add(ref p, (int)(v >> 30) & 7);
            Unsafe.Add(ref dst, 9 * stride) = Unsafe.Add(ref p, (int)(v >> 27) & 7);
            Unsafe.Add(ref dst, 13 * stride) = Unsafe.Add(ref p, (int)(v >> 24) & 7);
            Unsafe.Add(ref dst, 2 * stride) = Unsafe.Add(ref p, (int)(v >> 21) & 7);
            Unsafe.Add(ref dst, 6 * stride) = Unsafe.Add(ref p, (int)(v >> 18) & 7);
            Unsafe.Add(ref dst, 10 * stride) = Unsafe.Add(ref p, (int)(v >> 15) & 7);
            Unsafe.Add(ref dst, 14 * stride) = Unsafe.Add(ref p, (int)(v >> 12) & 7);
            Unsafe.Add(ref dst, 3 * stride) = Unsafe.Add(ref p, (int)(v >> 9) & 7);
            Unsafe.Add(ref dst, 7 * stride) = Unsafe.Add(ref p, (int)(v >> 6) & 7);
            Unsafe.Add(ref dst, 11 * stride) = Unsafe.Add(ref p, (int)(v >> 3) & 7);
            Unsafe.Add(ref dst, 15 * stride) = Unsafe.Add(ref p, (int)(v >> 0) & 7);
        }

        // Table 133 as ints, for vector loads
        static readonly int[] EacModI = Array.ConvertAll(EacMod, m => (int)m);

        /// <summary>The 8 exact 11 bit values of an EAC R11 block's table (two vectors of 4).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static (Vector128<int>, Vector128<int>) Eac11Vectors(ulong v, bool signed)
        {
            int mul = (int)(v >> 52) & 15, tab = ((int)(v >> 48) & 15) * 8;
            int baseCw = signed ? Math.Max((int)(sbyte)(v >> 56), -127) * 8 : (int)(v >> 56) * 8 + 4;
            var lo = Vector128.Create(signed ? -1023 : 0); var hi = Vector128.Create(signed ? 1023 : 2047);
            var bv = Vector128.Create(baseCw); var sv = Vector128.Create(mul != 0 ? mul * 8 : 1);
            ref int m = ref EacModI[tab];
            var m0 = Vector128.LoadUnsafe(ref m); var m1 = Vector128.LoadUnsafe(ref m, 4);
            return (Vector128.Min(Vector128.Max(bv + m0 * sv, lo), hi), Vector128.Min(Vector128.Max(bv + m1 * sv, lo), hi));
        }

        /// <summary>11 bit values to 16 bits as <see cref="Extend11"/> / <see cref="Extend11Signed"/> do.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector128<int> Extend(Vector128<int> x, bool signed)
        {
            if (!signed) return x << 5 | x >>> 6;
            var sign = x >> 31;
            var a = Vector128.Abs(x);
            return ((a << 5 | a >> 5) ^ sign) - sign;
        }

        /// <summary>
        /// One EAC R11 channel (8 bytes): the 16 exact 11 bit values, row-major, unsigned 0..2047 or (<paramref name="signed"/>)
        /// -1023..1023.
        /// </summary>
        [SkipLocalsInit]
        public static void Eac11(ReadOnlySpan<byte> b, bool signed, Span<int> values)
        {
            ulong v = BinaryPrimitives.ReadUInt64BigEndian(b);
            var (p0, p1) = Eac11Vectors(v, signed);
            if (values.Length < 16) throw new ArgumentException("16 values needed", nameof(values));
            ref int dst = ref MemoryMarshal.GetReference(values);
            for (int k = 0; k < 16; k++)
            {
                int j = (int)(v >> (45 - 3 * k)) & 7;
                Unsafe.Add(ref dst, (k & 3) * 4 + (k >> 2)) = j < 4 ? p0.GetElement(j) : p1.GetElement(j - 4);
            }
        }

        /// <summary>11 bit unsigned to 16 bits by bit replication.</summary>
        public static ushort Extend11(int x) => (ushort)(x << 5 | x >> 6);

        /// <summary>11 bit signed (-1023..1023) to 16 bits: replicated magnitude, sign restored.</summary>
        public static short Extend11Signed(int x) => x >= 0 ? (short)(x << 5 | x >> 5) : (short)-(-x << 5 | -x >> 5);

        /// <summary>Unsigned R11 EAC (8 bytes): 16 UNORM16 values written to o[i * stride], i row-major. For RG11 call it
        /// for each 8 byte half (red first) with stride 2.</summary>
        [SkipLocalsInit]
        public static void EacR11(ReadOnlySpan<byte> b, Span<ushort> o, int stride)
        {
            ulong v = BinaryPrimitives.ReadUInt64BigEndian(b);
            var (p0, p1) = Eac11Vectors(v, false);
            EacScatter(v, Vector128.Narrow(Extend(p0, false).AsUInt32(), Extend(p1, false).AsUInt32()), o, stride);
        }

        /// <summary>Signed R11 EAC (8 bytes): 16 SNORM16 values written to o[i * stride], i row-major.</summary>
        [SkipLocalsInit]
        public static void EacR11Signed(ReadOnlySpan<byte> b, Span<short> o, int stride)
        {
            ulong v = BinaryPrimitives.ReadUInt64BigEndian(b);
            var (p0, p1) = Eac11Vectors(v, true);
            EacScatter(v, Vector128.Narrow(Extend(p0, true), Extend(p1, true)), o, stride);
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
