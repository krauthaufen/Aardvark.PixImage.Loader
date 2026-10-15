using System;
using System.Buffers.Binary;

namespace Aardvark.ImageCodecs.DdsImpl
{
    /// <summary>
    /// BC1 to BC5 (S3TC / RGTC) block decoding, written from the Khronos Data Format Specification. The specification gives
    /// the interpolated values as real numbers; they are computed exactly here and rounded once to the output precision.
    /// </summary>
    internal static class BcBlocks
    {
        // n / 31 and n / 63 in 8 bit, rounded
        static int Five(int v) => (v * 255 + 15) / 31;
        static int Six(int v) => (v * 255 + 31) / 63;

        /// <summary>A rational a / d of 8 bit units, rounded to nearest (d &gt; 0, a &gt;= 0).</summary>
        static byte Round(int a, int d) => (byte)((2 * a + d) / (2 * d));

        /// <summary>
        /// BC1 / the colour part of BC2 and BC3: 16 RGBA texels (4 bytes each, row-major) into <paramref name="o"/>.
        /// <paramref name="fourColors"/>: always use the four colour mode (BC2, BC3).
        /// </summary>
        public static void Color(ReadOnlySpan<byte> b, Span<byte> o, bool fourColors)
        {
            int c0 = b[0] | b[1] << 8, c1 = b[2] | b[3] << 8;
            uint bits = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(4));
            // endpoints as exact multiples of 1/(31*63*...): keep the raw 5/6 bit values and interpolate in rationals
            int r0 = c0 >> 11, g0 = c0 >> 5 & 63, b0 = c0 & 31, r1 = c1 >> 11, g1 = c1 >> 5 & 63, b1 = c1 & 31;
            Span<byte> pal = stackalloc byte[16];
            pal[0] = (byte)Five(r0); pal[1] = (byte)Six(g0); pal[2] = (byte)Five(b0); pal[3] = 255;
            pal[4] = (byte)Five(r1); pal[5] = (byte)Six(g1); pal[6] = (byte)Five(b1); pal[7] = 255;
            if (c0 > c1 || fourColors)
            {
                // (2 c0 + c1) / 3 of the normalized values: 255 * (2 r0 + r1) / (3 * 31)
                pal[8] = Round(255 * (2 * r0 + r1), 93); pal[9] = Round(255 * (2 * g0 + g1), 189); pal[10] = Round(255 * (2 * b0 + b1), 93); pal[11] = 255;
                pal[12] = Round(255 * (r0 + 2 * r1), 93); pal[13] = Round(255 * (g0 + 2 * g1), 189); pal[14] = Round(255 * (b0 + 2 * b1), 93); pal[15] = 255;
            }
            else
            {
                pal[8] = Round(255 * (r0 + r1), 62); pal[9] = Round(255 * (g0 + g1), 126); pal[10] = Round(255 * (b0 + b1), 62); pal[11] = 255;
                pal[12] = pal[13] = pal[14] = pal[15] = 0;   // transparent black
            }
            for (int i = 0; i < 16; i++)
            {
                int k = (int)(bits >> (2 * i) & 3) * 4;
                o[i * 4] = pal[k]; o[i * 4 + 1] = pal[k + 1]; o[i * 4 + 2] = pal[k + 2]; o[i * 4 + 3] = pal[k + 3];
            }
        }

        /// <summary>BC2 explicit alpha: 4 bits per texel into every fourth byte of <paramref name="o"/>.</summary>
        public static void ExplicitAlpha(ReadOnlySpan<byte> b, Span<byte> o)
        {
            ulong bits = BinaryPrimitives.ReadUInt64LittleEndian(b);
            for (int i = 0; i < 16; i++) o[i * 4 + 3] = (byte)((bits >> (4 * i) & 15) * 17);
        }

        /// <summary>
        /// BC3 alpha / BC4 / one channel of BC5, unsigned: 16 values written to o[i * stride].
        /// </summary>
        public static void Channel(ReadOnlySpan<byte> b, Span<byte> o, int stride)
        {
            int a0 = b[0], a1 = b[1];
            ulong bits = BinaryPrimitives.ReadUInt64LittleEndian(b) >> 16;
            Span<byte> pal = stackalloc byte[8];
            pal[0] = (byte)a0; pal[1] = (byte)a1;
            if (a0 > a1)
                for (int k = 1; k < 7; k++) pal[k + 1] = Round((7 - k) * a0 + k * a1, 7);
            else
            {
                for (int k = 1; k < 5; k++) pal[k + 1] = Round((5 - k) * a0 + k * a1, 5);
                pal[6] = 0; pal[7] = 255;
            }
            for (int i = 0; i < 16; i++) o[i * stride] = pal[(int)(bits >> (3 * i) & 7)];
        }

        /// <summary>The signed variant (BC4 / BC5 SNORM): values -127..127 (-128 counts as -127).</summary>
        public static void ChannelSigned(ReadOnlySpan<byte> b, Span<sbyte> o, int stride)
        {
            int a0 = Math.Max((sbyte)b[0], (sbyte)-127), a1 = Math.Max((sbyte)b[1], (sbyte)-127);
            ulong bits = BinaryPrimitives.ReadUInt64LittleEndian(b) >> 16;
            Span<sbyte> pal = stackalloc sbyte[8];
            pal[0] = (sbyte)a0; pal[1] = (sbyte)a1;
            if ((sbyte)b[0] > (sbyte)b[1])
                for (int k = 1; k < 7; k++) pal[k + 1] = RoundSigned((7 - k) * a0 + k * a1, 7);
            else
            {
                for (int k = 1; k < 5; k++) pal[k + 1] = RoundSigned((5 - k) * a0 + k * a1, 5);
                pal[6] = -127; pal[7] = 127;
            }
            for (int i = 0; i < 16; i++) o[i * stride] = pal[(int)(bits >> (3 * i) & 7)];
        }

        static sbyte RoundSigned(int a, int d) => (sbyte)(a >= 0 ? (2 * a + d) / (2 * d) : -((-2 * a + d) / (2 * d)));
    }
}
