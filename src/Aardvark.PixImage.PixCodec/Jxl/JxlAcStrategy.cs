using System;

namespace Aardvark.ImageCodecs.JpegXl
{
    /// <summary>The 27 transform types (AC strategies) of VarDCT with their geometry and table assignments.</summary>
    internal static class JxlAcStrategy
    {
        public const int Dct8 = 0, Identity = 1, Dct2x2 = 2, Dct4x4 = 3, Dct16x16 = 4, Dct32x32 = 5, Dct16x8 = 6, Dct8x16 = 7,
            Dct32x8 = 8, Dct8x32 = 9, Dct32x16 = 10, Dct16x32 = 11, Dct4x8 = 12, Dct8x4 = 13, Afv0 = 14, Afv1 = 15, Afv2 = 16, Afv3 = 17,
            Dct64x64 = 18, Dct64x32 = 19, Dct32x64 = 20, Dct128x128 = 21, Dct128x64 = 22, Dct64x128 = 23, Dct256x256 = 24,
            Dct256x128 = 25, Dct128x256 = 26;
        public const int NumStrategies = 27;

        /// <summary>Width (x) and height (y) in 8x8 blocks; names are rows x columns (DCT16X8 is 16 tall, 8 wide).</summary>
        public static readonly byte[] CoveredX = { 1, 1, 1, 1, 2, 4, 1, 2, 1, 4, 2, 4, 1, 1, 1, 1, 1, 1, 8, 4, 8, 16, 8, 16, 32, 16, 32 };
        public static readonly byte[] CoveredY = { 1, 1, 1, 1, 2, 4, 2, 1, 4, 1, 4, 2, 1, 1, 1, 1, 1, 1, 8, 8, 4, 16, 16, 8, 32, 32, 16 };

        /// <summary>Coefficient order set (0..12) of each strategy.</summary>
        public static readonly byte[] Order = { 0, 1, 1, 1, 2, 3, 4, 4, 5, 5, 6, 6, 1, 1, 1, 1, 1, 1, 7, 8, 8, 9, 10, 10, 11, 12, 12 };

        /// <summary>Quantization table (0..16) of each strategy.</summary>
        public static readonly byte[] QuantTable = { 0, 1, 2, 3, 4, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 10, 10, 11, 12, 12, 13, 14, 14, 15, 16, 16 };

        /// <summary>Size of the coefficient orders in blocks (x = the larger dimension).</summary>
        public static readonly byte[] OrderBlocksX = { 1, 1, 2, 4, 2, 4, 4, 8, 8, 16, 16, 32, 32 };
        public static readonly byte[] OrderBlocksY = { 1, 1, 2, 4, 1, 1, 2, 8, 4, 16, 8, 32, 16 };

        /// <summary>Rows / columns of each quantization table (columns = the larger dimension).</summary>
        public static readonly short[] TableRows = { 8, 8, 8, 8, 16, 32, 8, 8, 16, 8, 8, 64, 32, 128, 64, 256, 128 };
        public static readonly short[] TableCols = { 8, 8, 8, 8, 16, 32, 16, 32, 32, 8, 8, 64, 64, 128, 128, 256, 256 };

        static readonly int[][] natural = BuildNatural();

        /// <summary>Natural coefficient order of an order set: indices into the coefficients in the wide layout.</summary>
        public static int[] NaturalOrder(int order) => natural[order];

        static int[][] BuildNatural()
        {
            var r = new int[13][];
            for (int o = 0; o < 13; o++) r[o] = ComputeNatural(OrderBlocksX[o], OrderBlocksY[o]);
            return r;
        }

        /// <summary>
        /// LLF coefficients (the cx x cy lowest) first in raster order, then the rest along anti-diagonals of the square
        /// (8 cx)^2 in zig-zag fashion, where only rows that are multiples of cx / cy exist.
        /// </summary>
        static int[] ComputeNatural(int cx, int cy)
        {
            int xs = cx * 8;
            int size = cx * cy * 64;
            int ratio = cx / cy;
            var order = new int[size];
            int cur = 0;
            for (int y = 0; y < cy; y++)
                for (int x = 0; x < cx; x++) order[cur++] = y * xs + x;
            for (int i = 0; i < xs; i++)
            {
                for (int j = 0; j <= i; j++)
                {
                    int x = j, y = i - j;
                    if ((i & 1) != 0) (x, y) = (y, x);
                    if (y % ratio != 0) continue;
                    y /= ratio;
                    if (x < cx && y < cy) continue;
                    order[cur++] = y * xs + x;
                }
            }
            for (int ip = xs - 1; ip > 0; ip--)
            {
                int i = ip - 1;
                for (int j = 0; j <= i; j++)
                {
                    int x = xs - 1 - (i - j), y = xs - 1 - j;
                    if ((i & 1) != 0) (x, y) = (y, x);
                    if (y % ratio != 0) continue;
                    y /= ratio;
                    order[cur++] = y * xs + x;
                }
            }
            if (cur != size) throw new InvalidOperationException("natural order construction");
            return order;
        }
    }
}
