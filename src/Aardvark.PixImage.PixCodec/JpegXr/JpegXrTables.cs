using System;

namespace Aardvark.ImageCodecs.JpegXrImpl
{
    /// <summary>
    /// A variable length code given as one bit string per value; decoded through a lookup table indexed by the next
    /// <see cref="MaxLength"/> bits, encoded through the code and length per value.
    /// </summary>
    internal sealed class Vlc
    {
        public readonly int MaxLength;
        /// <summary>value | length &lt;&lt; 8, indexed by the next MaxLength bits.</summary>
        public readonly ushort[] Lookup;
        public readonly uint[] Codes;
        public readonly byte[] Lengths;
        /// <summary>Value of each code index (Table 81 lists its codes out of value order).</summary>
        public Vlc(params string[] codes) : this(codes, null) { }

        public Vlc(string[] codes, int[]? values)
        {
            int n = codes.Length;
            Codes = new uint[n];
            Lengths = new byte[n];
            int max = 0;
            for (int i = 0; i < n; i++)
            {
                var s = codes[i].Replace(" ", "");
                int v = values?[i] ?? i;
                uint c = 0;
                foreach (var ch in s) c = c << 1 | (uint)(ch - '0');
                Codes[v] = c;
                Lengths[v] = (byte)s.Length;
                max = Math.Max(max, s.Length);
            }
            MaxLength = max;
            Lookup = new ushort[1 << max];
            for (int v = 0; v < n; v++)
            {
                int len = Lengths[v];
                int first = (int)(Codes[v] << (max - len));
                for (int k = 0; k < 1 << (max - len); k++)
                {
                    if (Lookup[first + k] != 0) throw new InvalidOperationException("JPEG XR: VLC table is not prefix free");
                    Lookup[first + k] = (ushort)(v | len << 8);
                }
            }
        }
    }

    /// <summary>The code tables and constants of T.832 clause 8.</summary>
    internal static class JxrTables
    {
        /// <summary>Table 51: VAL_DC_YUV.</summary>
        public static readonly Vlc ValDcYuv = new("10", "001", "0000 1", "0001", "11", "010", "0000 0", "011");

        /// <summary>Table 52: ABS_LEVEL_INDEX, code tables 0 and 1.</summary>
        public static readonly Vlc[] AbsLevelIndex =
        {
            new("01", "10", "11", "001", "0001", "00000", "00001"),
            new("1", "01", "001", "0001", "00001", "000000", "000001"),
        };

        /// <summary>Table 55 (YUV444) and Table 56 (YUV420 / YUV422): CBPLP_YUV1.</summary>
        public static readonly Vlc CbplpYuv444 = new("0", "100", "1010", "1011", "1100", "1101", "1110", "1111");
        public static readonly Vlc CbplpYuv42x = new("0", "10", "110", "111");

        /// <summary>Table 59: NUM_CBPHP; Table 60: NUM_BLKCBPHP for YONLY, YUVK and NCOMPONENT (the same codes).</summary>
        public static readonly Vlc[] NumCbphp =
        {
            new("1", "01", "001", "0000", "0001"),
            new("1", "000", "001", "010", "011"),
        };

        /// <summary>Table 61: NUM_BLKCBPHP for YUV420 / YUV422 / YUV444.</summary>
        public static readonly Vlc[] NumBlkCbphpYuv =
        {
            new("010", "00000", "0010", "00001", "00010", "1", "011", "00011", "0011"),
            new("1", "001", "010", "0001", "000001", "011", "00001", "0000000", "0000001"),
        };

        /// <summary>Table 62: CHR_CBPHP, VAL_INC and CBPHP_CH_BLK.</summary>
        public static readonly Vlc ChrCbphp = new("1", "01", "00");

        /// <summary>Table 63: NUM_CH_BLK.</summary>
        public static readonly Vlc NumChBlk = new("1", "01", "000", "001");

        /// <summary>Table 64: REF_CBPHP1, code index i stands for RefCbphp1Values[i].</summary>
        public static readonly Vlc RefCbphp1 = new("00", "01", "100", "101", "110", "111");
        public static readonly int[] RefCbphp1Values = { 3, 5, 6, 9, 10, 12 };

        /// <summary>Tables 76 .. 78: RUN_VALUE for iMaxRun 2, 3 and 4 (code index = run - 1).</summary>
        public static readonly Vlc[] RunValue =
        {
            null!, null!,
            new("1", "0"),
            new("1", "01", "00"),
            new("1", "01", "001", "000"),
        };

        /// <summary>Table 79: RUN_INDEX.</summary>
        public static readonly Vlc RunIndex = new("1", "01", "001", "0000", "0001");

        /// <summary>Table 80: INDEX_A, code tables 0 .. 3.</summary>
        public static readonly Vlc[] IndexA =
        {
            new("1", "0 0000", "001", "0 0001", "01", "0001"),
            new("01", "0000", "10", "0001", "11", "001"),
            new("0000", "0001", "01", "10", "11", "001"),
            new("0 0000", "0 0001", "01", "1", "0001", "001"),
        };

        /// <summary>Table 81: INDEX_B (codes listed for the values 0, 2, 1, 3).</summary>
        public static readonly Vlc IndexB = new(new[] { "0", "10", "110", "111" }, new[] { 0, 2, 1, 3 });

        /// <summary>Table 82: FIRST_INDEX, code tables 0 .. 4.</summary>
        public static readonly Vlc[] FirstIndex =
        {
            new("0000 1", "0000 01", "0000 000", "0000 001", "0010 0", "010", "0010 1", "1", "0011 0", "0001", "0011 1", "011"),
            new("0010", "0001 0", "0000 00", "0000 01", "0011", "010", "0001 1", "11", "011", "100", "0000 1", "101"),
            new("11", "001", "0000 000", "0000 001", "0000 1", "010", "0000 010", "011", "100", "101", "0000 011", "0001"),
            new("001", "11", "0000 000", "0000 1", "0001 0", "010", "0000 001", "011", "0001 1", "100", "0000 01", "101"),
            new("010", "1", "0000 001", "0001", "0000 010", "011", "0000 0000", "0010", "0000 011", "0011", "0000 0001", "0000 1"),
        };

        /// <summary>Table 86: AbslevelIndexDelta[0][n].</summary>
        public static readonly int[] AbsLevelIndexDelta = { 1, 0, -1, -1, -1, -1, -1 };

        /// <summary>Table 87: FirstIndexDelta[m][n].</summary>
        public static readonly int[][] FirstIndexDelta =
        {
            new[] { 1, 1, 1, 1, 1, 0, 0, -1, 2, 1, 0, 0 },
            new[] { 2, 2, -1, -1, -1, 0, -2, -1, 0, 0, -2, -1 },
            new[] { -1, 1, 0, 2, 0, 0, 0, 0, -2, 0, 1, 1 },
            new[] { 0, 1, 0, 1, -2, 0, -1, -1, -2, -1, -2, -2 },
        };

        /// <summary>Table 88: Index1Delta[m][n].</summary>
        public static readonly int[][] Index1Delta =
        {
            new[] { -1, 1, 1, 1, 0, 1 },
            new[] { -2, 0, 0, 2, 0, 0 },
            new[] { -1, -1, 0, 1, -2, 0 },
        };

        /// <summary>Table 89 (NUM_CBPHP) and Table 90 (NUM_BLKCBPHP for YONLY, YUVK, NCOMPONENT).</summary>
        public static readonly int[] NumCbphpDelta = { 0, -1, 0, 1, 1 };

        /// <summary>Table 91: NumBlkCBPHPDelta for the other internal colour formats.</summary>
        public static readonly int[] NumBlkCbphpDeltaYuv = { 2, 2, 1, 1, -1, -2, -2, -2, -3 };

        /// <summary>Table 107: ScanOrder0 / ScanOrder1, index 1 .. 15.</summary>
        public static readonly int[] ScanOrder0 = { 0, 4, 1, 5, 8, 2, 9, 6, 12, 3, 10, 13, 7, 14, 11, 15 };
        public static readonly int[] ScanOrder1 = { 0, 1, 2, 5, 4, 3, 6, 9, 8, 7, 12, 15, 13, 10, 11, 14 };

        /// <summary>Table 108: ScanTotals, index 1 .. 15.</summary>
        public static readonly int[] ScanTotals = { 0, 32, 30, 28, 26, 24, 22, 20, 18, 16, 14, 12, 10, 8, 6, 4 };

        /// <summary>Table 47: bits of QPINDEX_REF by number of QPs.</summary>
        public static readonly int[] BitsQpIndex = { 0, 0, 1, 1, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 4, 4, 4 };

        public static readonly int[] Transpose444 = { 0, 4, 8, 12, 1, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15 };
        public static readonly int[] Transpose422 = { 0, 2, 1, 3, 4, 6, 5, 7 };
        public static readonly int[] Transpose420 = { 0, 2, 1, 3 };
        public static readonly int[] HierScanOrder = { 0, 1, 4, 5, 2, 3, 6, 7, 8, 9, 12, 13, 10, 11, 14, 15 };

        /// <summary>8.7.17.2: iFLC, iOff, iOut of MB_CBPHP.</summary>
        public static readonly int[] CbphpFlc = { 0, 2, 1, 2, 2, 0 };
        public static readonly int[] CbphpOff = { 0, 4, 2, 8, 12, 1 };
        public static readonly int[] CbphpOut = { 0, 15, 3, 12, 1, 2, 4, 8, 5, 6, 9, 10, 7, 11, 13, 14 };

        /// <summary>8.7.13: iRemap, iFixedLen of DECODE_ABS_LEVEL.</summary>
        public static readonly int[] AbsRemap = { 2, 3, 4, 6, 10, 14 };
        public static readonly int[] AbsFixedLen = { 0, 0, 1, 2, 2, 2 };

        /// <summary>8.7.18.6: DECODE_RUN tables.</summary>
        public static readonly int[] RunRemap = { 1, 2, 3, 5, 7, 1, 2, 3, 5, 7, 1, 2, 3, 4, 5 };
        public static readonly int[] RunBin = { -1, -1, -1, -1, 2, 2, 2, 1, 1, 1, 1, 0, 0, 0, 0 };
        public static readonly int[] RunFixedLength = { 0, 0, 1, 1, 3, 0, 0, 1, 1, 2, 0, 0, 0, 0, 1 };

        /// <summary>8.12.2: UpdateModelMB weights.</summary>
        public static readonly int[] Weight0 = { 240, 12, 1 };
        public static readonly int[][] Weight1 =
        {
            new[] { 0, 240, 120, 80, 60, 48, 40, 34, 30, 27, 24, 22, 20, 18, 17, 16 },
            new[] { 0, 12, 6, 4, 3, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1 },
            new[] { 0, 16, 8, 5, 4, 3, 3, 2, 2, 2, 2, 1, 1, 1, 1, 1 },
        };
        public static readonly int[] Weight2 = { 120, 37, 2, 120, 18, 1 };

        /// <summary>Table 164 / D.11: the inverse and forward coefficient permutations of the core transform.</summary>
        public static readonly int[] InvPermArr = { 0, 8, 4, 13, 2, 15, 3, 14, 1, 12, 5, 9, 7, 11, 6, 10 };
        public static readonly int[] FwdPermArr = { 0, 8, 4, 6, 2, 10, 14, 12, 1, 11, 15, 13, 9, 3, 7, 5 };
    }
}
