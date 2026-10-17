using System;

namespace Aardvark.ImageCodecs.Av1
{
    /// <summary>Mode info of one coded block (the per block values the spec keeps in its per 4x4 arrays).</summary>
    internal struct Av1Block
    {
        public byte MiSize, YMode, UVMode, TxSize, SegmentId, PaletteSizeY, PaletteSizeUV;
        public sbyte RefFrame0, RefFrame1;
        public bool IsInter, Skip, SkipMode, UseIntrabc;
        public byte InterpFilter0, InterpFilter1, CompGroupIdx, CompoundIdx;
        public sbyte DeltaLF0, DeltaLF1, DeltaLF2, DeltaLF3;
        public short Mv0Row, Mv0Col, Mv1Row, Mv1Col;
        /// <summary>Offset of the palette colours (8 Y then 8 U) in the tile's palette pool.</summary>
        public int PaletteOffset;

        public readonly int DeltaLF(int i) => i switch { 0 => DeltaLF0, 1 => DeltaLF1, 2 => DeltaLF2, _ => DeltaLF3 };
        public readonly int RefFrame(int list) => list == 0 ? RefFrame0 : RefFrame1;
    }

    /// <summary>Frame level state of the frame being decoded (arrays shared by all tiles, filters, saved state).</summary>
    internal sealed class Av1FrameState
    {
        public readonly Av1Decoder Dec;
        public readonly Av1SequenceHeader Seq;
        public readonly Av1FrameHeader Hdr;
        public readonly ushort[] Cdfs = Av1Cdf.Create();
        public ushort[]? SavedCdfs;
        public readonly int[,] PrevGmParams = new int[8, 6];
        public byte[]? PrevSegmentIds;
        public Av1Frame Cur = null!;
        public int MiRows, MiCols, MiStride;
        public uint[] BlockAt = null!;
        public Av1TileDecoder[] Tiles = Array.Empty<Av1TileDecoder>();
        public byte[] TxTypes = null!, InterTxSizes = null!, SegmentIds = null!;
        public readonly byte[][] LfTxSizes = new byte[3][];
        public sbyte[] CdefIdx = null!;
        public int CdefStride;
        // loop restoration units per plane
        public readonly int[] LrUnitRows = new int[3], LrUnitCols = new int[3];
        public readonly byte[][] LrType = new byte[3][];
        public readonly short[][] LrWiener = new short[3][];   // [unit][pass][3]
        public readonly byte[][] LrSgrSet = new byte[3][];
        public readonly short[][] LrSgrXqd = new short[3][];   // [unit][2]
        public int[][]? MotionFieldMvs;                        // [ref][y8 * (MiCols >> 1) + x8], row low 16 bits, col high 16 bits
        public byte[]? TileSource;
        public const uint NoBlock = 0xFFFFFFFF;

        public Av1FrameState(Av1Decoder dec, Av1SequenceHeader seq, Av1FrameHeader hdr)
        {
            Dec = dec; Seq = seq; Hdr = hdr;
        }

        /// <summary>Allocates the frame buffers and arrays once the frame size is known (after the uncompressed header).</summary>
        public void Allocate()
        {
            var h = Hdr;
            MiRows = h.MiRows; MiCols = h.MiCols; MiStride = MiCols;
            int sb4 = Seq.Use128x128Superblock ? 32 : 16;
            int alignedMiCols = (MiCols + sb4 - 1) / sb4 * sb4, alignedMiRows = (MiRows + sb4 - 1) / sb4 * sb4;
            int allocW = Math.Max(alignedMiCols * 4, (h.UpscaledWidth + 7) & ~7);
            int allocH = alignedMiRows * 4;
            Cur = new Av1Frame(Seq.NumPlanes, allocW, allocH, Seq.SubsamplingX, Seq.SubsamplingY, Seq.BitDepth)
            {
                Width = h.FrameWidth, Height = h.FrameHeight,
            };
            int n = MiRows * MiCols;
            BlockAt = new uint[n];
            Array.Fill(BlockAt, NoBlock);
            TxTypes = new byte[n];
            InterTxSizes = new byte[n];
            SegmentIds = new byte[n];
            for (int p = 0; p < Seq.NumPlanes; p++) LfTxSizes[p] = new byte[n];
            CdefStride = ((MiCols + 31) >> 5) * 2;
            CdefIdx = new sbyte[CdefStride * ((MiRows + 31) >> 5) * 2];
            Array.Fill(CdefIdx, (sbyte)-1);
            if (h.UsesLr)
                for (int p = 0; p < Seq.NumPlanes; p++)
                {
                    if (h.FrameRestorationType[p] == Av1C.RestoreNone) continue;
                    int ss_x = p == 0 ? 0 : Seq.SubsamplingX, ss_y = p == 0 ? 0 : Seq.SubsamplingY;
                    int unitSize = h.LoopRestorationSize[p];
                    LrUnitRows[p] = CountUnits(unitSize, Round2(h.FrameHeight, ss_y));
                    LrUnitCols[p] = CountUnits(unitSize, Round2(h.UpscaledWidth, ss_x));
                    int units = LrUnitRows[p] * LrUnitCols[p];
                    LrType[p] = new byte[units];
                    LrWiener[p] = new short[units * 6];
                    LrSgrSet[p] = new byte[units];
                    LrSgrXqd[p] = new short[units * 2];
                }
        }

        public static int CountUnits(int unitSize, int frameSize) => Math.Max((frameSize + (unitSize >> 1)) / unitSize, 1);
        public static int Round2(int x, int n) => n == 0 ? x : (x + (1 << (n - 1))) >> n;

        public ref Av1Block Block(uint handle) => ref Tiles[handle >> 20].Blocks[handle & 0xFFFFF];
        public ref Av1Block BlockAtMi(int row, int col) => ref Block(BlockAt[row * MiStride + col]);
        public bool HasBlock(int row, int col) => BlockAt[row * MiStride + col] != NoBlock;
    }
}
