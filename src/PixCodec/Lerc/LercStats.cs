using System;

namespace PixCodec
{
    /// <summary>
    /// Counts which parts of the format a decode went through. Only collected while a test has set
    /// <see cref="Current"/> on its thread; it exists so the tests can show that the fixtures reach every coding mode.
    /// </summary>
    internal sealed class LercStats
    {
        [ThreadStatic] public static LercStats? Current;

        public long Bands, EmptyBands, ConstantBands, RawBands, TileBands, DeltaHuffmanBands, PlainHuffmanBands, FloatLosslessBands;
        public long MaskStored, MaskReused, NoDataRemapped;
        /// <summary>Blocks by kind: 0 raw, 1 bit stuffed, 2 all zero, 3 constant.</summary>
        public readonly long[] Blocks = new long[4];
        public long LutBlocks, RelativeBlocks, ZeroWidthBlocks;
        /// <summary>Block offsets by the LERC type they were stored in.</summary>
        public readonly long[] OffsetTypes = new long[8];
        public readonly long[] BlockSizes = new long[33];
        public readonly long[] FloatPredictors = new long[3];
        /// <summary>Byte planes by coding: 0 Huffman, 1 constant, 2 raw, 3 run length.</summary>
        public readonly long[] PlaneCodings = new long[4];
        public readonly long[] PlaneDeltas = new long[6];
        public long LongHuffmanCodes;
        public readonly long[] Lerc1Blocks = new long[4];
    }
}
