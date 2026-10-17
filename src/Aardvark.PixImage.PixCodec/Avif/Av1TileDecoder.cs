using System;
using System.Runtime.CompilerServices;
using T = Aardvark.ImageCodecs.Av1.Av1Tables;

namespace Aardvark.ImageCodecs.Av1
{
    /// <summary>
    /// Decodes one tile (AV1 spec 5.11): block syntax, prediction and reconstruction. Tiles of a frame are independent
    /// and can run on separate threads; each has its own CDFs, contexts and block list.
    /// </summary>
    internal sealed partial class Av1TileDecoder
    {
        readonly Av1FrameState fs;
        readonly Av1FrameHeader h;
        readonly Av1SequenceHeader seq;
        readonly int tileIndex;
        public Av1Block[] Blocks = new Av1Block[64];
        int blockCount;
        ushort[] palettePool = new ushort[256];
        int paletteCount;
        // a struct: its state lives in the tile decoder (one indirection less per symbol)
        Av1SymbolDecoder sd;
        public readonly ushort[] Cdf = Av1Cdf.Create();
        readonly ushort[] intraFrameYModeCdf = Av1Cdf.IntraFrameYModeDefaults();
        readonly ushort[] tmpCdf = new ushort[3];
        int MiRowStart, MiRowEnd, MiColStart, MiColEnd;
        int CurrentQIndex;
        readonly int numPlanes, subX, subY, bitDepth;
        readonly bool use128;
        readonly int sbSize, sbSize4, sbMask;

        // contexts (indexed by absolute 4x4 column / row of the plane)
        readonly byte[][] aboveLevelCtx = new byte[3][], aboveDcCtx = new byte[3][], leftLevelCtx = new byte[3][], leftDcCtx = new byte[3][];
        readonly byte[] aboveSegPredCtx, leftSegPredCtx;
        readonly int[] deltaLF = new int[4];
        readonly int[] refSgrXqd = new int[3 * 2];
        readonly int[] refLrWiener = new int[3 * 2 * 3];
        bool readDeltas;

        // BlockDecoded[plane][y][x] for y, x in -1 .. 32 (spec 5.11.3)
        const int BdStride = 35;
        readonly bool[] blockDecoded = new bool[3 * BdStride * BdStride];

        // the block being decoded
        int MiRow, MiCol, MiSize, bw4, bh4;
        bool HasChroma, AvailU, AvailL, AvailUChroma, AvailLChroma;
        bool skip, skipMode, isInter, useIntrabc, lossless;
        int segmentId;
        int YMode, UVMode, AngleDeltaY, AngleDeltaUV, CflAlphaU, CflAlphaV;
        bool useFilterIntra;
        int filterIntraMode;
        int PaletteSizeY, PaletteSizeUV;
        readonly ushort[] paletteColorsY = new ushort[8], paletteColorsU = new ushort[8], paletteColorsV = new ushort[8];
        readonly ushort[] paletteCache = new ushort[16];
        const int MapStride = 64 + 4;
        readonly byte[] colorMapY = new byte[MapStride * 64], colorMapUV = new byte[MapStride * 64];
        readonly int[] colorOrder = new int[8];
        int colorContextHash;
        int TxSize;
        readonly int[] RefFrame = new int[2];
        readonly int[] MvRow = new int[2], MvCol = new int[2];
        readonly int[] interpFilter = new int[2];
        int motionMode, interintra, interintraMode, wedgeInterintra, wedgeIndex, wedgeSign, maskType, compoundType, compGroupIdx, compoundIdx;
        int MaxLumaW, MaxLumaH;
        int curHandle;

        public Av1TileDecoder(Av1FrameState fs, int tileIndex)
        {
            this.fs = fs; h = fs.Hdr; seq = fs.Seq; this.tileIndex = tileIndex;
            numPlanes = seq.NumPlanes; subX = seq.SubsamplingX; subY = seq.SubsamplingY; bitDepth = seq.BitDepth;
            use128 = seq.Use128x128Superblock;
            sbSize = use128 ? Av1C.Block128x128 : Av1C.Block64x64;
            sbSize4 = use128 ? 32 : 16;
            sbMask = use128 ? 31 : 15;
            int cols = fs.MiCols + 32, rows = fs.MiRows + 32;
            for (int p = 0; p < numPlanes; p++)
            {
                aboveLevelCtx[p] = new byte[cols]; aboveDcCtx[p] = new byte[cols];
                leftLevelCtx[p] = new byte[rows]; leftDcCtx[p] = new byte[rows];
            }
            aboveSegPredCtx = new byte[cols];
            leftSegPredCtx = new byte[rows];
            InitScratch();
        }

        /// <summary>init_symbol, decode_tile and the CDF part of exit_symbol for tile number <paramref name="tileNum"/>.</summary>
        public void DecodeTile(byte[] data, int offset, int size, int tileNum)
        {
            int tileRow = tileNum / h.TileCols, tileCol = tileNum % h.TileCols;
            MiRowStart = h.MiRowStarts[tileRow];
            MiRowEnd = h.MiRowStarts[tileRow + 1];
            MiColStart = h.MiColStarts[tileCol];
            MiColEnd = h.MiColStarts[tileCol + 1];
            CurrentQIndex = h.BaseQIdx;
            Array.Copy(fs.Cdfs, Cdf, Cdf.Length);
            sd.Init(data, offset, size, h.DisableCdfUpdate);
            // clear_above_context
            for (int p = 0; p < numPlanes; p++) { Array.Clear(aboveLevelCtx[p]); Array.Clear(aboveDcCtx[p]); }
            Array.Clear(aboveSegPredCtx);
            for (int i = 0; i < 4; i++) deltaLF[i] = 0;
            for (int p = 0; p < numPlanes; p++)
                for (int pass = 0; pass < 2; pass++)
                {
                    refSgrXqd[p * 2 + pass] = T.SgrprojXqdMid[pass];
                    for (int i = 0; i < 3; i++) refLrWiener[(p * 2 + pass) * 3 + i] = T.WienerTapsMid[i];
                }
            for (int r = MiRowStart; r < MiRowEnd; r += sbSize4)
            {
                // clear_left_context
                for (int p = 0; p < numPlanes; p++) { Array.Clear(leftLevelCtx[p]); Array.Clear(leftDcCtx[p]); }
                Array.Clear(leftSegPredCtx);
                for (int c = MiColStart; c < MiColEnd; c += sbSize4)
                {
                    readDeltas = h.DeltaQPresent;
                    ClearCdef(r, c);
                    ClearBlockDecodedFlags(r, c);
                    ReadLr(r, c, sbSize);
                    DecodePartition(r, c, sbSize);
                }
            }
        }

        // ------------------------------------------------------------------ helpers

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int S(int off, int n) => sd.Read(Cdf, off, n);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int L(int n) => sd.Literal(n);

        bool IsInside(int r, int c) => c >= MiColStart && c < MiColEnd && r >= MiRowStart && r < MiRowEnd;

        ref Av1Block Nb(int r, int c) => ref fs.Block(fs.BlockAt[r * fs.MiStride + c]);

        static int BlockWidth(int bs) => T.Num4x4BlocksWide[bs] * 4;
        static int BlockHeight(int bs) => T.Num4x4BlocksHigh[bs] * 4;

        int PlaneResidualSize(int subsize, int plane)
        {
            int sx = plane > 0 ? subX : 0, sy = plane > 0 ? subY : 0;
            return T.SubsampledSize[subsize * 4 + sx * 2 + sy];
        }

        ref bool BlockDecoded(int plane, int y, int x) => ref blockDecoded[plane * BdStride * BdStride + (y + 1) * BdStride + x + 1];

        void ClearBlockDecodedFlags(int r, int c)
        {
            for (int plane = 0; plane < numPlanes; plane++)
            {
                int sx = plane > 0 ? subX : 0, sy = plane > 0 ? subY : 0;
                int sbWidth4 = (MiColEnd - c) >> sx;
                int sbHeight4 = (MiRowEnd - r) >> sy;
                for (int y = -1; y <= (sbSize4 >> sy); y++)
                    for (int x = -1; x <= (sbSize4 >> sx); x++)
                    {
                        bool v;
                        if (y < 0 && x < sbWidth4) v = true;
                        else if (x < 0 && y < sbHeight4) v = true;
                        else v = false;
                        BlockDecoded(plane, y, x) = v;
                    }
                BlockDecoded(plane, sbSize4 >> sy, -1) = false;
            }
        }

        void ClearCdef(int r, int c)
        {
            fs.CdefIdx[(r >> 4) * fs.CdefStride + (c >> 4)] = -1;
            if (use128)
            {
                fs.CdefIdx[(r >> 4) * fs.CdefStride + (c >> 4) + 1] = -1;
                fs.CdefIdx[((r >> 4) + 1) * fs.CdefStride + (c >> 4)] = -1;
                fs.CdefIdx[((r >> 4) + 1) * fs.CdefStride + (c >> 4) + 1] = -1;
            }
        }

        // ------------------------------------------------------------------ partition (5.11.4)

        void DecodePartition(int r, int c, int bSize)
        {
            if (r >= fs.MiRows || c >= fs.MiCols) return;
            bool availU = IsInside(r - 1, c), availL = IsInside(r, c - 1);
            int num4x4 = T.Num4x4BlocksWide[bSize];
            int halfBlock4x4 = num4x4 >> 1;
            int quarterBlock4x4 = halfBlock4x4 >> 1;
            bool hasRows = r + halfBlock4x4 < fs.MiRows;
            bool hasCols = c + halfBlock4x4 < fs.MiCols;
            int partition;
            if (bSize < Av1C.Block8x8) partition = Av1C.PartitionNone;
            else
            {
                int bsl = T.MiWidthLog2[bSize];
                int above = availU && T.MiWidthLog2[Nb(r - 1, c).MiSize] < bsl ? 1 : 0;
                int left = availL && T.MiHeightLog2[Nb(r, c - 1).MiSize] < bsl ? 1 : 0;
                int ctx = left * 2 + above;
                int off, n;
                switch (bsl)
                {
                    case 1: off = Av1Cdf.PartitionW8; n = 4; break;
                    case 2: off = Av1Cdf.PartitionW16; n = 10; break;
                    case 3: off = Av1Cdf.PartitionW32; n = 10; break;
                    case 4: off = Av1Cdf.PartitionW64; n = 10; break;
                    default: off = Av1Cdf.PartitionW128; n = 8; break;
                }
                off += ctx * (n + 1);
                if (hasRows && hasCols) partition = S(off, n);
                else if (hasCols)
                {
                    var pc = Cdf;
                    int P(int i) => pc[off + i] - (i > 0 ? pc[off + i - 1] : 0);
                    int psum = P(Av1C.PartitionVert) + P(Av1C.PartitionSplit) + P(Av1C.PartitionHorzA) + P(Av1C.PartitionVertA) + P(Av1C.PartitionVertB);
                    if (bSize != Av1C.Block128x128) psum += P(Av1C.PartitionVert4);
                    partition = TempBool(psum) != 0 ? Av1C.PartitionSplit : Av1C.PartitionHorz;
                }
                else if (hasRows)
                {
                    var pc = Cdf;
                    int P(int i) => pc[off + i] - (i > 0 ? pc[off + i - 1] : 0);
                    int psum = P(Av1C.PartitionHorz) + P(Av1C.PartitionSplit) + P(Av1C.PartitionHorzA) + P(Av1C.PartitionHorzB) + P(Av1C.PartitionVertA);
                    if (bSize != Av1C.Block128x128) psum += P(Av1C.PartitionHorz4);
                    partition = TempBool(psum) != 0 ? Av1C.PartitionSplit : Av1C.PartitionVert;
                }
                else partition = Av1C.PartitionSplit;
            }
            int subSize = T.PartitionSubsize[partition * Av1C.BlockSizes + bSize];
            int splitSize = T.PartitionSubsize[Av1C.PartitionSplit * Av1C.BlockSizes + bSize];
            switch (partition)
            {
                case Av1C.PartitionNone: DecodeBlock(r, c, subSize); break;
                case Av1C.PartitionHorz:
                    DecodeBlock(r, c, subSize);
                    if (hasRows) DecodeBlock(r + halfBlock4x4, c, subSize);
                    break;
                case Av1C.PartitionVert:
                    DecodeBlock(r, c, subSize);
                    if (hasCols) DecodeBlock(r, c + halfBlock4x4, subSize);
                    break;
                case Av1C.PartitionSplit:
                    DecodePartition(r, c, subSize);
                    DecodePartition(r, c + halfBlock4x4, subSize);
                    DecodePartition(r + halfBlock4x4, c, subSize);
                    DecodePartition(r + halfBlock4x4, c + halfBlock4x4, subSize);
                    break;
                case Av1C.PartitionHorzA:
                    DecodeBlock(r, c, splitSize);
                    DecodeBlock(r, c + halfBlock4x4, splitSize);
                    DecodeBlock(r + halfBlock4x4, c, subSize);
                    break;
                case Av1C.PartitionHorzB:
                    DecodeBlock(r, c, subSize);
                    DecodeBlock(r + halfBlock4x4, c, splitSize);
                    DecodeBlock(r + halfBlock4x4, c + halfBlock4x4, splitSize);
                    break;
                case Av1C.PartitionVertA:
                    DecodeBlock(r, c, splitSize);
                    DecodeBlock(r + halfBlock4x4, c, splitSize);
                    DecodeBlock(r, c + halfBlock4x4, subSize);
                    break;
                case Av1C.PartitionVertB:
                    DecodeBlock(r, c, subSize);
                    DecodeBlock(r, c + halfBlock4x4, splitSize);
                    DecodeBlock(r + halfBlock4x4, c + halfBlock4x4, splitSize);
                    break;
                case Av1C.PartitionHorz4:
                    DecodeBlock(r, c, subSize);
                    DecodeBlock(r + quarterBlock4x4, c, subSize);
                    DecodeBlock(r + quarterBlock4x4 * 2, c, subSize);
                    if (r + quarterBlock4x4 * 3 < fs.MiRows) DecodeBlock(r + quarterBlock4x4 * 3, c, subSize);
                    break;
                default:
                    DecodeBlock(r, c, subSize);
                    DecodeBlock(r, c + quarterBlock4x4, subSize);
                    DecodeBlock(r, c + quarterBlock4x4 * 2, subSize);
                    if (c + quarterBlock4x4 * 3 < fs.MiCols) DecodeBlock(r, c + quarterBlock4x4 * 3, subSize);
                    break;
            }
        }

        /// <summary>A symbol from the temporary CDF { 32768 - psum, 32768 } of split_or_horz / split_or_vert.</summary>
        int TempBool(int psum)
        {
            tmpCdf[0] = (ushort)(32768 - psum);
            tmpCdf[1] = 32768;
            tmpCdf[2] = 0;
            return sd.Read(tmpCdf, 0, 2);
        }

        // ------------------------------------------------------------------ block (5.11.5)

        void DecodeBlock(int r, int c, int subSize)
        {
            if (subSize >= Av1C.BlockSizes) throw new ImageFormatException("AV1: invalid block size");
            MiRow = r; MiCol = c; MiSize = subSize;
            bw4 = T.Num4x4BlocksWide[subSize];
            bh4 = T.Num4x4BlocksHigh[subSize];
            if (bh4 == 1 && subY != 0 && (MiRow & 1) == 0) HasChroma = false;
            else if (bw4 == 1 && subX != 0 && (MiCol & 1) == 0) HasChroma = false;
            else HasChroma = numPlanes > 1;
            AvailU = IsInside(r - 1, c);
            AvailL = IsInside(r, c - 1);
            AvailUChroma = AvailU;
            AvailLChroma = AvailL;
            if (HasChroma)
            {
                if (subY != 0 && bh4 == 1) AvailUChroma = IsInside(r - 2, c);
                if (subX != 0 && bw4 == 1) AvailLChroma = IsInside(r, c - 2);
            }
            else
            {
                AvailUChroma = AvailLChroma = false;
            }
            if (HasChroma && PlaneResidualSize(subSize, 1) == Av1C.BlockInvalid) throw new ImageFormatException("AV1: invalid chroma block size");
            useFilterIntra = false;
            useIntrabc = false;
            motionMode = Av1C.Simple;
            interintra = 0;
            compoundType = Av1C.CompoundAverage;
            compGroupIdx = 0; compoundIdx = 1;
            interpFilter[0] = interpFilter[1] = 0;
            MvRow[0] = MvCol[0] = MvRow[1] = MvCol[1] = 0;
            AngleDeltaY = AngleDeltaUV = 0;
            CflAlphaU = CflAlphaV = 0;
            UVMode = Av1C.DcPred;
            if (h.FrameIsIntra) IntraFrameModeInfo();
            else InterFrameModeInfo();
            PaletteTokens();
            ReadBlockTxSize();
            if (Av1Stats.On) BlockStats();
            if (skip) ResetBlockContext();
            StoreBlock();
            ComputePrediction();
            Residual();
        }

        void BlockStats()
        {
            Av1Stats.Count($"block.size{MiSize}");
            if (skip) Av1Stats.Count("block.skip");
            if (useIntrabc) { Av1Stats.Count("block.intrabc"); return; }
            if (!isInter)
            {
                Av1Stats.Count($"intra.y{YMode}");
                if (HasChroma) Av1Stats.Count($"intra.uv{UVMode}");
                if (AngleDeltaY != 0 || AngleDeltaUV != 0) Av1Stats.Count("intra.angleDelta");
                if (PaletteSizeY > 0) Av1Stats.Count($"intra.paletteY{PaletteSizeY}");
                if (PaletteSizeUV > 0) Av1Stats.Count($"intra.paletteUV{PaletteSizeUV}");
                if (useFilterIntra) Av1Stats.Count($"intra.filter{filterIntraMode}");
                return;
            }
            Av1Stats.Count($"inter.mode{YMode}");
            if (skipMode) Av1Stats.Count("inter.skipMode");
            if (RefFrame[1] > 0) Av1Stats.Count($"inter.compound{compoundType}");
            if (interintra != 0) Av1Stats.Count(wedgeInterintra != 0 ? "inter.interintraWedge" : $"inter.interintra{interintraMode}");
            if (motionMode != 0) Av1Stats.Count($"inter.motion{motionMode}");
            Av1Stats.Count($"inter.filter{interpFilter[0]}{interpFilter[1]}");
            if (segmentId != 0) Av1Stats.Count("block.segment>0");
        }

        void ResetBlockContext()
        {
            for (int plane = 0; plane < 1 + (HasChroma ? 2 : 0); plane++)
            {
                int sx = plane > 0 ? subX : 0, sy = plane > 0 ? subY : 0;
                for (int i = MiCol >> sx; i < ((MiCol + bw4) >> sx); i++) { aboveLevelCtx[plane][i] = 0; aboveDcCtx[plane][i] = 0; }
                for (int i = MiRow >> sy; i < ((MiRow + bh4) >> sy); i++) { leftLevelCtx[plane][i] = 0; leftDcCtx[plane][i] = 0; }
            }
        }

        /// <summary>The per 4x4 stores of decode_block (YModes, RefFrames, Mvs, ..., IsInters, Skips, SegmentIds, palettes, DeltaLFs).</summary>
        void StoreBlock()
        {
            if (blockCount == Blocks.Length)
            {
                if (blockCount >= 1 << 20) throw new ImageNotSupportedException("AV1: too many blocks in a tile");
                Array.Resize(ref Blocks, blockCount * 2);
            }
            int idx = blockCount++;
            ref var b = ref Blocks[idx];
            b.MiSize = (byte)MiSize;
            b.YMode = (byte)YMode;
            b.UVMode = (byte)UVMode;
            b.RefFrame0 = (sbyte)RefFrame[0];
            b.RefFrame1 = (sbyte)RefFrame[1];
            b.IsInter = isInter;
            b.Skip = skip;
            b.SkipMode = skipMode;
            b.UseIntrabc = useIntrabc;
            b.TxSize = (byte)TxSize;
            b.SegmentId = (byte)segmentId;
            b.PaletteSizeY = (byte)PaletteSizeY;
            b.PaletteSizeUV = (byte)PaletteSizeUV;
            b.InterpFilter0 = (byte)interpFilter[0];
            b.InterpFilter1 = (byte)interpFilter[1];
            b.CompGroupIdx = (byte)compGroupIdx;
            b.CompoundIdx = (byte)compoundIdx;
            b.DeltaLF0 = (sbyte)deltaLF[0]; b.DeltaLF1 = (sbyte)deltaLF[1]; b.DeltaLF2 = (sbyte)deltaLF[2]; b.DeltaLF3 = (sbyte)deltaLF[3];
            b.Mv0Row = (short)MvRow[0]; b.Mv0Col = (short)MvCol[0];
            b.Mv1Row = (short)MvRow[1]; b.Mv1Col = (short)MvCol[1];
            if (PaletteSizeY > 0 || PaletteSizeUV > 0)
            {
                if (paletteCount + 16 > palettePool.Length) Array.Resize(ref palettePool, palettePool.Length * 2);
                b.PaletteOffset = paletteCount;
                Array.Copy(paletteColorsY, 0, palettePool, paletteCount, 8);
                Array.Copy(paletteColorsU, 0, palettePool, paletteCount + 8, 8);
                paletteCount += 16;
            }
            else b.PaletteOffset = -1;
            uint handle = ((uint)tileIndex << 20) | (uint)idx;
            curHandle = (int)handle;
            int rows = Math.Min(bh4, fs.MiRows - MiRow), cols = Math.Min(bw4, fs.MiCols - MiCol);
            var grid = fs.BlockAt;
            var seg = fs.SegmentIds;
            for (int y = 0; y < rows; y++)
            {
                int o = (MiRow + y) * fs.MiStride + MiCol;
                grid.AsSpan(o, cols).Fill(handle);
                seg.AsSpan(o, cols).Fill((byte)segmentId);
            }
        }

        public ushort PaletteColor(int offset, int plane, int i) => palettePool[offset + plane * 8 + i];

        // ------------------------------------------------------------------ intra frame mode info (5.11.7)

        void IntraFrameModeInfo()
        {
            skip = false;
            if (h.SegIdPreSkip) IntraSegmentId();
            skipMode = false;
            ReadSkip();
            if (!h.SegIdPreSkip) IntraSegmentId();
            ReadCdef();
            ReadDeltaQIndex();
            ReadDeltaLf();
            readDeltas = false;
            RefFrame[0] = Av1C.IntraFrame;
            RefFrame[1] = Av1C.None;
            useIntrabc = h.AllowIntrabc && S(Av1Cdf.Intrabc, 2) != 0;
            PaletteSizeY = PaletteSizeUV = 0;
            if (useIntrabc)
            {
                isInter = true;
                YMode = Av1C.DcPred;
                UVMode = Av1C.DcPred;
                motionMode = Av1C.Simple;
                compoundType = Av1C.CompoundAverage;
                interpFilter[0] = interpFilter[1] = Av1C.Bilinear;
                FindMvStack(false);
                AssignMv(false);
            }
            else
            {
                isInter = false;
                int above = T.IntraModeContext[AvailU ? Nb(MiRow - 1, MiCol).YMode : Av1C.DcPred];
                int left = T.IntraModeContext[AvailL ? Nb(MiRow, MiCol - 1).YMode : Av1C.DcPred];
                YMode = sd.Read(intraFrameYModeCdf, (above * 5 + left) * 14, 13);
                IntraAngleInfoY();
                if (HasChroma)
                {
                    UVMode = ReadUvMode();
                    if (UVMode == Av1C.UvCflPred) ReadCflAlphas();
                    IntraAngleInfoUv();
                }
                if (MiSize >= Av1C.Block8x8 && BlockWidth(MiSize) <= 64 && BlockHeight(MiSize) <= 64 && h.AllowScreenContentTools)
                    PaletteModeInfo();
                FilterIntraModeInfo();
            }
        }

        int ReadUvMode()
        {
            bool cflAllowed = lossless && PlaneResidualSize(MiSize, 1) == Av1C.Block4x4
                || !lossless && Math.Max(BlockWidth(MiSize), BlockHeight(MiSize)) <= 32;
            if (cflAllowed) return S(Av1Cdf.UVModeCflAllowed + YMode * 15, 14);
            return S(Av1Cdf.UVModeCflNotAllowed + YMode * 14, 13);
        }

        void IntraSegmentId()
        {
            if (h.SegmentationEnabled) ReadSegmentId();
            else segmentId = 0;
            lossless = h.LosslessArray[segmentId];
        }

        void ReadSegmentId()
        {
            int prevUL = AvailU && AvailL ? fs.SegmentIds[(MiRow - 1) * fs.MiStride + MiCol - 1] : -1;
            int prevU = AvailU ? fs.SegmentIds[(MiRow - 1) * fs.MiStride + MiCol] : -1;
            int prevL = AvailL ? fs.SegmentIds[MiRow * fs.MiStride + MiCol - 1] : -1;
            int pred;
            if (prevU == -1) pred = prevL == -1 ? 0 : prevL;
            else if (prevL == -1) pred = prevU;
            else pred = prevUL == prevU ? prevU : prevL;
            if (skip) { segmentId = pred; return; }
            int ctx;
            if (prevUL < 0) ctx = 0;
            else if (prevUL == prevU && prevUL == prevL) ctx = 2;
            else if (prevUL == prevU || prevUL == prevL || prevU == prevL) ctx = 1;
            else ctx = 0;
            int v = S(Av1Cdf.SegmentId + ctx * 9, 8);
            segmentId = NegDeinterleave(v, pred, h.LastActiveSegId + 1);
            if (segmentId < 0 || segmentId > 7) throw new ImageFormatException("AV1: bad segment_id");
        }

        static int NegDeinterleave(int diff, int rf, int max)
        {
            if (rf == 0) return diff;
            if (rf >= max - 1) return max - diff - 1;
            if (2 * rf < max)
            {
                if (diff <= 2 * rf)
                {
                    if ((diff & 1) != 0) return rf + ((diff + 1) >> 1);
                    return rf - (diff >> 1);
                }
                return diff;
            }
            if (diff <= 2 * (max - rf - 1))
            {
                if ((diff & 1) != 0) return rf + ((diff + 1) >> 1);
                return rf - (diff >> 1);
            }
            return max - (diff + 1);
        }

        bool SegFeatureActive(int feature) => h.FeatureOn(segmentId, feature);

        void ReadSkipMode()
        {
            if (SegFeatureActive(Av1C.SegLvlSkip) || SegFeatureActive(Av1C.SegLvlRefFrame) || SegFeatureActive(Av1C.SegLvlGlobalMv)
                || !h.SkipModePresent || BlockWidth(MiSize) < 8 || BlockHeight(MiSize) < 8)
            {
                skipMode = false;
                return;
            }
            int ctx = 0;
            if (AvailU && Nb(MiRow - 1, MiCol).SkipMode) ctx++;
            if (AvailL && Nb(MiRow, MiCol - 1).SkipMode) ctx++;
            skipMode = S(Av1Cdf.SkipMode + ctx * 3, 2) != 0;
        }

        void ReadSkip()
        {
            if (h.SegIdPreSkip && SegFeatureActive(Av1C.SegLvlSkip)) { skip = true; return; }
            int ctx = 0;
            if (AvailU && Nb(MiRow - 1, MiCol).Skip) ctx++;
            if (AvailL && Nb(MiRow, MiCol - 1).Skip) ctx++;
            skip = S(Av1Cdf.Skip + ctx * 3, 2) != 0;
        }

        void ReadCdef()
        {
            if (skip || h.CodedLossless || !seq.EnableCdef || h.AllowIntrabc) return;
            int r = MiRow & ~15, c = MiCol & ~15;
            int i0 = (r >> 4) * fs.CdefStride + (c >> 4);
            if (fs.CdefIdx[i0] == -1)
            {
                sbyte v = (sbyte)L(h.CdefBits);
                for (int i = r; i < r + bh4; i += 16)
                    for (int j = c; j < c + bw4; j += 16)
                        fs.CdefIdx[(i >> 4) * fs.CdefStride + (j >> 4)] = v;
            }
        }

        void ReadDeltaQIndex()
        {
            if (MiSize == sbSize && skip) return;
            if (!readDeltas) return;
            int abs = S(Av1Cdf.DeltaQ, 4);
            if (abs == Av1C.DeltaQSmall)
            {
                int remBits = L(3) + 1;
                abs = L(remBits) + (1 << remBits) + 1;
            }
            if (abs != 0)
            {
                int sign = L(1);
                int reduced = sign != 0 ? -abs : abs;
                CurrentQIndex = Math.Clamp(CurrentQIndex + (reduced << h.DeltaQRes), 1, 255);
            }
        }

        void ReadDeltaLf()
        {
            if (MiSize == sbSize && skip) return;
            if (!readDeltas || !h.DeltaLfPresent) return;
            int frameLfCount = 1;
            if (h.DeltaLfMulti) frameLfCount = numPlanes > 1 ? Av1C.FrameLfCount : Av1C.FrameLfCount - 2;
            for (int i = 0; i < frameLfCount; i++)
            {
                int off = h.DeltaLfMulti ? Av1Cdf.DeltaLFMulti + i * 5 : Av1Cdf.DeltaLF;
                int abs = S(off, 4);
                if (abs == Av1C.DeltaLfSmall)
                {
                    int n = L(3) + 1;
                    abs = L(n) + (1 << n) + 1;
                }
                if (abs != 0)
                {
                    int sign = L(1);
                    int reduced = sign != 0 ? -abs : abs;
                    deltaLF[i] = Math.Clamp(deltaLF[i] + (reduced << h.DeltaLfRes), -Av1C.MaxLoopFilter, Av1C.MaxLoopFilter);
                }
            }
        }

        // ------------------------------------------------------------------ transform size (5.11.15 - 5.11.17)

        void ReadTxSize(bool allowSelect)
        {
            if (lossless) { TxSize = Av1C.Tx4x4; return; }
            int maxRectTxSize = T.MaxTxSizeRect[MiSize];
            int maxTxDepth = T.MaxTxDepth[MiSize];
            TxSize = maxRectTxSize;
            if (MiSize > Av1C.Block4x4 && allowSelect && h.TxMode == Av1C.TxModeSelect)
            {
                int maxTxWidth = T.TxWidth[maxRectTxSize], maxTxHeight = T.TxHeight[maxRectTxSize];
                int aboveW, leftH;
                if (AvailU && Nb(MiRow - 1, MiCol).IsInter) aboveW = BlockWidth(Nb(MiRow - 1, MiCol).MiSize);
                else if (AvailU) aboveW = AboveTxWidth(MiRow, MiCol);
                else aboveW = 0;
                if (AvailL && Nb(MiRow, MiCol - 1).IsInter) leftH = BlockHeight(Nb(MiRow, MiCol - 1).MiSize);
                else if (AvailL) leftH = LeftTxHeight(MiRow, MiCol);
                else leftH = 0;
                int ctx = (aboveW >= maxTxWidth ? 1 : 0) + (leftH >= maxTxHeight ? 1 : 0);
                int depth;
                if (maxTxDepth == 4) depth = S(Av1Cdf.Tx64x64 + ctx * 4, 3);
                else if (maxTxDepth == 3) depth = S(Av1Cdf.Tx32x32 + ctx * 4, 3);
                else if (maxTxDepth == 2) depth = S(Av1Cdf.Tx16x16 + ctx * 4, 3);
                else depth = S(Av1Cdf.Tx8x8 + ctx * 3, 2);
                for (int i = 0; i < depth; i++) TxSize = T.SplitTxSize[TxSize];
            }
        }

        int AboveTxWidth(int row, int col)
        {
            if (row == MiRow)
            {
                if (!AvailU) return 64;
                ref var a = ref Nb(row - 1, col);
                if (a.Skip && a.IsInter) return BlockWidth(a.MiSize);
            }
            return T.TxWidth[fs.InterTxSizes[(row - 1) * fs.MiStride + col]];
        }

        int LeftTxHeight(int row, int col)
        {
            if (col == MiCol)
            {
                if (!AvailL) return 64;
                ref var l = ref Nb(row, col - 1);
                if (l.Skip && l.IsInter) return BlockHeight(l.MiSize);
            }
            return T.TxHeight[fs.InterTxSizes[row * fs.MiStride + col - 1]];
        }

        void ReadBlockTxSize()
        {
            if (h.TxMode == Av1C.TxModeSelect && MiSize > Av1C.Block4x4 && isInter && !skip && !lossless)
            {
                int maxTxSz = T.MaxTxSizeRect[MiSize];
                int txW4 = T.TxWidth[maxTxSz] / 4, txH4 = T.TxHeight[maxTxSz] / 4;
                for (int row = MiRow; row < MiRow + bh4; row += txH4)
                    for (int col = MiCol; col < MiCol + bw4; col += txW4)
                        ReadVarTxSize(row, col, maxTxSz, 0);
            }
            else
            {
                ReadTxSize(!skip || !isInter);
                int rows = Math.Min(bh4, fs.MiRows - MiRow), cols = Math.Min(bw4, fs.MiCols - MiCol);
                for (int row = 0; row < rows; row++)
                    fs.InterTxSizes.AsSpan((MiRow + row) * fs.MiStride + MiCol, cols).Fill((byte)TxSize);
            }
        }

        void ReadVarTxSize(int row, int col, int txSz, int depth)
        {
            if (row >= fs.MiRows || col >= fs.MiCols) return;
            bool split;
            if (txSz == Av1C.Tx4x4 || depth == Av1C.MaxVartxDepth) split = false;
            else
            {
                int above = AboveTxWidth(row, col) < T.TxWidth[txSz] ? 1 : 0;
                int left = LeftTxHeight(row, col) < T.TxHeight[txSz] ? 1 : 0;
                int size = Math.Min(64, Math.Max(BlockWidth(MiSize), BlockHeight(MiSize)));
                int maxTxSz = FindTxSize(size, size);
                int txSzSqrUp = T.TxSizeSqrUp[txSz];
                int ctx = (txSzSqrUp != maxTxSz ? 1 : 0) * 3 + (Av1C.TxSizes - 1 - maxTxSz) * 6 + above + left;
                split = S(Av1Cdf.TxfmSplit + ctx * 3, 2) != 0;
            }
            int w4 = T.TxWidth[txSz] / 4, h4 = T.TxHeight[txSz] / 4;
            if (split)
            {
                int subTxSz = T.SplitTxSize[txSz];
                int stepW = T.TxWidth[subTxSz] / 4, stepH = T.TxHeight[subTxSz] / 4;
                for (int i = 0; i < h4; i += stepH)
                    for (int j = 0; j < w4; j += stepW)
                        ReadVarTxSize(row + i, col + j, subTxSz, depth + 1);
            }
            else
            {
                int rows = Math.Min(h4, fs.MiRows - row), cols = Math.Min(w4, fs.MiCols - col);
                for (int i = 0; i < rows; i++)
                    fs.InterTxSizes.AsSpan((row + i) * fs.MiStride + col, cols).Fill((byte)txSz);
                TxSize = txSz;
            }
        }

        static int FindTxSize(int w, int hh)
        {
            int txSz;
            for (txSz = 0; txSz < Av1C.TxSizesAll; txSz++)
                if (T.TxWidth[txSz] == w && T.TxHeight[txSz] == hh) break;
            return txSz;
        }

        // ------------------------------------------------------------------ intra info (5.11.42 - 5.11.46)

        static bool IsDirectionalMode(int mode) => mode >= Av1C.VPred && mode <= Av1C.D67Pred;

        void IntraAngleInfoY()
        {
            AngleDeltaY = 0;
            if (MiSize >= Av1C.Block8x8 && IsDirectionalMode(YMode))
                AngleDeltaY = S(Av1Cdf.AngleDelta + (YMode - Av1C.VPred) * 8, 7) - Av1C.MaxAngleDelta;
        }

        void IntraAngleInfoUv()
        {
            AngleDeltaUV = 0;
            if (MiSize >= Av1C.Block8x8 && IsDirectionalMode(UVMode))
                AngleDeltaUV = S(Av1Cdf.AngleDelta + (UVMode - Av1C.VPred) * 8, 7) - Av1C.MaxAngleDelta;
        }

        void ReadCflAlphas()
        {
            int signs = S(Av1Cdf.CflSign, 8);
            int signU = (signs + 1) / 3, signV = (signs + 1) % 3;
            if (signU != 0)
            {
                CflAlphaU = 1 + S(Av1Cdf.CflAlpha + ((signU - 1) * 3 + signV) * 17, 16);
                if (signU == 1) CflAlphaU = -CflAlphaU;
            }
            else CflAlphaU = 0;
            if (signV != 0)
            {
                CflAlphaV = 1 + S(Av1Cdf.CflAlpha + ((signV - 1) * 3 + signU) * 17, 16);
                if (signV == 1) CflAlphaV = -CflAlphaV;
            }
            else CflAlphaV = 0;
        }

        void FilterIntraModeInfo()
        {
            useFilterIntra = false;
            if (seq.EnableFilterIntra && YMode == Av1C.DcPred && PaletteSizeY == 0 && Math.Max(BlockWidth(MiSize), BlockHeight(MiSize)) <= 32)
            {
                useFilterIntra = S(Av1Cdf.FilterIntra + MiSize * 3, 2) != 0;
                if (useFilterIntra) filterIntraMode = S(Av1Cdf.FilterIntraMode, 5);
            }
        }

        // ------------------------------------------------------------------ palette (5.11.46, 5.11.49, 5.11.50)

        void PaletteModeInfo()
        {
            int bsizeCtx = T.MiWidthLog2[MiSize] + T.MiHeightLog2[MiSize] - 2;
            int maxVal = (1 << bitDepth) - 1;
            if (YMode == Av1C.DcPred)
            {
                int ctx = 0;
                if (AvailU && Nb(MiRow - 1, MiCol).PaletteSizeY > 0) ctx++;
                if (AvailL && Nb(MiRow, MiCol - 1).PaletteSizeY > 0) ctx++;
                if (S(Av1Cdf.PaletteYMode + (bsizeCtx * 3 + ctx) * 3, 2) != 0)
                {
                    PaletteSizeY = S(Av1Cdf.PaletteYSize + bsizeCtx * 8, 7) + 2;
                    int cacheN = GetPaletteCache(0);
                    int idx = 0;
                    for (int i = 0; i < cacheN && idx < PaletteSizeY; i++)
                        if (L(1) != 0) paletteColorsY[idx++] = paletteCache[i];
                    if (idx < PaletteSizeY) paletteColorsY[idx++] = (ushort)L(bitDepth);
                    int paletteBits = 0;
                    if (idx < PaletteSizeY) paletteBits = bitDepth - 3 + L(2);
                    while (idx < PaletteSizeY)
                    {
                        int delta = L(paletteBits) + 1;
                        paletteColorsY[idx] = (ushort)Math.Min(paletteColorsY[idx - 1] + delta, maxVal);
                        int range = (1 << bitDepth) - paletteColorsY[idx] - 1;
                        paletteBits = Math.Min(paletteBits, Av1BitReader.CeilLog2(range));
                        idx++;
                    }
                    Array.Sort(paletteColorsY, 0, PaletteSizeY);
                }
            }
            if (HasChroma && UVMode == Av1C.DcPred)
            {
                if (S(Av1Cdf.PaletteUVMode + (PaletteSizeY > 0 ? 3 : 0), 2) != 0)
                {
                    PaletteSizeUV = S(Av1Cdf.PaletteUVSize + bsizeCtx * 8, 7) + 2;
                    int cacheN = GetPaletteCache(1);
                    int idx = 0;
                    for (int i = 0; i < cacheN && idx < PaletteSizeUV; i++)
                        if (L(1) != 0) paletteColorsU[idx++] = paletteCache[i];
                    if (idx < PaletteSizeUV) paletteColorsU[idx++] = (ushort)L(bitDepth);
                    int paletteBits = 0;
                    if (idx < PaletteSizeUV) paletteBits = bitDepth - 3 + L(2);
                    while (idx < PaletteSizeUV)
                    {
                        int delta = L(paletteBits);
                        paletteColorsU[idx] = (ushort)Math.Min(paletteColorsU[idx - 1] + delta, maxVal);
                        int range = (1 << bitDepth) - paletteColorsU[idx];
                        paletteBits = Math.Min(paletteBits, Av1BitReader.CeilLog2(range));
                        idx++;
                    }
                    Array.Sort(paletteColorsU, 0, PaletteSizeUV);
                    if (L(1) != 0)
                    {
                        int minBits = bitDepth - 4;
                        int maxV = 1 << bitDepth;
                        int bits = minBits + L(2);
                        paletteColorsV[0] = (ushort)L(bitDepth);
                        for (idx = 1; idx < PaletteSizeUV; idx++)
                        {
                            int delta = L(bits);
                            if (delta != 0 && L(1) != 0) delta = -delta;
                            int val = paletteColorsV[idx - 1] + delta;
                            if (val < 0) val += maxV;
                            if (val >= maxV) val -= maxV;
                            paletteColorsV[idx] = (ushort)Math.Clamp(val, 0, maxVal);
                        }
                    }
                    else
                    {
                        for (idx = 0; idx < PaletteSizeUV; idx++) paletteColorsV[idx] = (ushort)L(bitDepth);
                    }
                }
            }
        }

        int GetPaletteCache(int plane)
        {
            int aboveN = 0, leftN = 0;
            int aboveOff = -1, leftOff = -1;
            if ((MiRow * Av1C.MiSize) % 64 != 0 && AvailU)
            {
                ref var a = ref Nb(MiRow - 1, MiCol);
                aboveN = plane == 0 ? a.PaletteSizeY : a.PaletteSizeUV;
                aboveOff = a.PaletteOffset;
            }
            if (AvailL)
            {
                ref var l = ref Nb(MiRow, MiCol - 1);
                leftN = plane == 0 ? l.PaletteSizeY : l.PaletteSizeUV;
                leftOff = l.PaletteOffset;
            }
            int aboveIdx = 0, leftIdx = 0, n = 0;
            while (aboveIdx < aboveN && leftIdx < leftN)
            {
                int aboveC = palettePool[aboveOff + plane * 8 + aboveIdx];
                int leftC = palettePool[leftOff + plane * 8 + leftIdx];
                if (leftC < aboveC)
                {
                    if (n == 0 || leftC != paletteCache[n - 1]) paletteCache[n++] = (ushort)leftC;
                    leftIdx++;
                }
                else
                {
                    if (n == 0 || aboveC != paletteCache[n - 1]) paletteCache[n++] = (ushort)aboveC;
                    aboveIdx++;
                    if (leftC == aboveC) leftIdx++;
                }
            }
            while (aboveIdx < aboveN)
            {
                int val = palettePool[aboveOff + plane * 8 + aboveIdx++];
                if (n == 0 || val != paletteCache[n - 1]) paletteCache[n++] = (ushort)val;
            }
            while (leftIdx < leftN)
            {
                int val = palettePool[leftOff + plane * 8 + leftIdx++];
                if (n == 0 || val != paletteCache[n - 1]) paletteCache[n++] = (ushort)val;
            }
            return n;
        }

        void PaletteTokens()
        {
            int blockHeight = BlockHeight(MiSize), blockWidth = BlockWidth(MiSize);
            int onscreenHeight = Math.Min(blockHeight, (fs.MiRows - MiRow) * Av1C.MiSize);
            int onscreenWidth = Math.Min(blockWidth, (fs.MiCols - MiCol) * Av1C.MiSize);
            if (PaletteSizeY != 0)
                ReadColorMap(colorMapY, PaletteSizeY, Av1Cdf.PaletteYColor, blockWidth, blockHeight, onscreenWidth, onscreenHeight);
            if (PaletteSizeUV != 0)
            {
                blockHeight >>= subY;
                blockWidth >>= subX;
                onscreenHeight >>= subY;
                onscreenWidth >>= subX;
                if (blockWidth < 4) { blockWidth += 2; onscreenWidth += 2; }
                if (blockHeight < 4) { blockHeight += 2; onscreenHeight += 2; }
                ReadColorMap(colorMapUV, PaletteSizeUV, Av1Cdf.PaletteUVColor, blockWidth, blockHeight, onscreenWidth, onscreenHeight);
            }
        }

        void ReadColorMap(byte[] map, int n, int[] cdfOffsets, int blockWidth, int blockHeight, int onscreenWidth, int onscreenHeight)
        {
            map[0] = (byte)sd.Ns(n);
            int baseOff = cdfOffsets[n];
            for (int i = 1; i < onscreenHeight + onscreenWidth - 1; i++)
                for (int j = Math.Min(i, onscreenWidth - 1); j >= Math.Max(0, i - onscreenHeight + 1); j--)
                {
                    GetPaletteColorContext(map, i - j, j, n);
                    int ctx = T.PaletteColorContext[colorContextHash];
                    int idx = S(baseOff + ctx * (n + 1), n);
                    map[(i - j) * MapStride + j] = (byte)colorOrder[idx];
                }
            for (int i = 0; i < onscreenHeight; i++)
                for (int j = onscreenWidth; j < blockWidth; j++)
                    map[i * MapStride + j] = map[i * MapStride + onscreenWidth - 1];
            for (int i = onscreenHeight; i < blockHeight; i++)
                Array.Copy(map, (onscreenHeight - 1) * MapStride, map, i * MapStride, blockWidth);
        }

        void GetPaletteColorContext(byte[] map, int r, int c, int n)
        {
            Span<int> scores = stackalloc int[8];
            for (int i = 0; i < 8; i++) { scores[i] = 0; colorOrder[i] = i; }
            if (c > 0) scores[map[r * MapStride + c - 1]] += 2;
            if (r > 0 && c > 0) scores[map[(r - 1) * MapStride + c - 1]] += 1;
            if (r > 0) scores[map[(r - 1) * MapStride + c]] += 2;
            for (int i = 0; i < Av1C.PaletteNumNeighbors; i++)
            {
                int maxScore = scores[i], maxIdx = i;
                for (int j = i + 1; j < n; j++)
                    if (scores[j] > maxScore) { maxScore = scores[j]; maxIdx = j; }
                if (maxIdx != i)
                {
                    maxScore = scores[maxIdx];
                    int maxColorOrder = colorOrder[maxIdx];
                    for (int k = maxIdx; k > i; k--)
                    {
                        scores[k] = scores[k - 1];
                        colorOrder[k] = colorOrder[k - 1];
                    }
                    scores[i] = maxScore;
                    colorOrder[i] = maxColorOrder;
                }
            }
            colorContextHash = 0;
            for (int i = 0; i < Av1C.PaletteNumNeighbors; i++) colorContextHash += scores[i] * T.PaletteColorHashMultipliers[i];
        }

        // ------------------------------------------------------------------ loop restoration syntax (5.11.57, 5.11.58)

        void ReadLr(int r, int c, int bSize)
        {
            if (h.AllowIntrabc) return;
            int w = T.Num4x4BlocksWide[bSize], hh = T.Num4x4BlocksHigh[bSize];
            for (int plane = 0; plane < numPlanes; plane++)
            {
                if (h.FrameRestorationType[plane] == Av1C.RestoreNone) continue;
                int sx = plane == 0 ? 0 : subX, sy = plane == 0 ? 0 : subY;
                int unitSize = h.LoopRestorationSize[plane];
                int unitRows = fs.LrUnitRows[plane], unitCols = fs.LrUnitCols[plane];
                int unitRowStart = (r * (Av1C.MiSize >> sy) + unitSize - 1) / unitSize;
                int unitRowEnd = Math.Min(unitRows, ((r + hh) * (Av1C.MiSize >> sy) + unitSize - 1) / unitSize);
                int numerator, denominator;
                if (h.UseSuperres)
                {
                    numerator = (Av1C.MiSize >> sx) * h.SuperresDenom;
                    denominator = unitSize * Av1C.SuperresNum;
                }
                else
                {
                    numerator = Av1C.MiSize >> sx;
                    denominator = unitSize;
                }
                int unitColStart = (c * numerator + denominator - 1) / denominator;
                int unitColEnd = Math.Min(unitCols, ((c + w) * numerator + denominator - 1) / denominator);
                for (int unitRow = unitRowStart; unitRow < unitRowEnd; unitRow++)
                    for (int unitCol = unitColStart; unitCol < unitColEnd; unitCol++)
                        ReadLrUnit(plane, unitRow * unitCols + unitCol);
            }
        }

        void ReadLrUnit(int plane, int unit)
        {
            int restorationType;
            int frt = h.FrameRestorationType[plane];
            if (frt == Av1C.RestoreWiener) restorationType = S(Av1Cdf.UseWiener, 2) != 0 ? Av1C.RestoreWiener : Av1C.RestoreNone;
            else if (frt == Av1C.RestoreSgrproj) restorationType = S(Av1Cdf.UseSgrproj, 2) != 0 ? Av1C.RestoreSgrproj : Av1C.RestoreNone;
            else restorationType = S(Av1Cdf.RestorationType, 3);
            fs.LrType[plane][unit] = (byte)restorationType;
            if (Av1Stats.On) Av1Stats.Count($"lr.unit.t{restorationType}");
            if (restorationType == Av1C.RestoreWiener)
            {
                for (int pass = 0; pass < 2; pass++)
                {
                    int firstCoeff;
                    if (plane != 0) { firstCoeff = 1; fs.LrWiener[plane][unit * 6 + pass * 3] = 0; }
                    else firstCoeff = 0;
                    for (int j = firstCoeff; j < 3; j++)
                    {
                        int min = T.WienerTapsMin[j], max = T.WienerTapsMax[j], k = T.WienerTapsK[j];
                        int v = DecodeSignedSubexpWithRefBool(min, max + 1, k, refLrWiener[(plane * 2 + pass) * 3 + j]);
                        fs.LrWiener[plane][unit * 6 + pass * 3 + j] = (short)v;
                        refLrWiener[(plane * 2 + pass) * 3 + j] = v;
                    }
                }
            }
            else if (restorationType == Av1C.RestoreSgrproj)
            {
                int set = L(Av1C.SgrprojParamsBits);
                fs.LrSgrSet[plane][unit] = (byte)set;
                for (int i = 0; i < 2; i++)
                {
                    int radius = T.SgrParams[set * 4 + i * 2];
                    int min = T.SgrprojXqdMin[i], max = T.SgrprojXqdMax[i];
                    int v;
                    if (radius != 0) v = DecodeSignedSubexpWithRefBool(min, max + 1, Av1C.SgrprojPrjSubexpK, refSgrXqd[plane * 2 + i]);
                    else
                    {
                        v = 0;
                        if (i == 1) v = Math.Clamp((1 << Av1C.SgrprojPrjBits) - refSgrXqd[plane * 2], min, max);
                    }
                    fs.LrSgrXqd[plane][unit * 2 + i] = (short)v;
                    refSgrXqd[plane * 2 + i] = v;
                }
            }
        }

        int DecodeSignedSubexpWithRefBool(int low, int high, int k, int r)
        {
            int x = DecodeUnsignedSubexpWithRefBool(high - low, k, r - low);
            return x + low;
        }

        int DecodeUnsignedSubexpWithRefBool(int mx, int k, int r)
        {
            int v = DecodeSubexpBool(mx, k);
            if ((r << 1) <= mx) return Av1Decoder.InverseRecenter(r, v);
            return mx - 1 - Av1Decoder.InverseRecenter(mx - 1 - r, v);
        }

        int DecodeSubexpBool(int numSyms, int k)
        {
            int i = 0, mk = 0;
            while (true)
            {
                int b2 = i != 0 ? k + i - 1 : k;
                int a = 1 << b2;
                if (numSyms <= mk + 3 * a) return sd.Ns(numSyms - mk) + mk;
                if (L(1) != 0) { i++; mk += a; }
                else return L(b2) + mk;
            }
        }
    }
}
