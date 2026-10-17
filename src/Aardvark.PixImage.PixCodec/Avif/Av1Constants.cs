namespace Aardvark.ImageCodecs.Av1
{
    /// <summary>Constants of the AV1 specification (section 3 and the semantics of section 6).</summary>
    internal static class Av1C
    {
        public const int RefsPerFrame = 7;
        public const int TotalRefsPerFrame = 8;
        public const int BlockSizeGroups = 4;
        public const int BlockSizes = 22;
        public const int BlockInvalid = 22;
        public const int MaxSbSize = 128;
        public const int MiSize = 4;
        public const int MiSizeLog2 = 2;
        public const int MaxTileWidth = 4096;
        public const int MaxTileArea = 4096 * 2304;
        public const int MaxTileRows = 64;
        public const int MaxTileCols = 64;
        public const int IntrabcDelayPixels = 256;
        public const int IntrabcDelaySb64 = 4;
        public const int NumRefFrames = 8;
        public const int MaxSegments = 8;
        public const int SegLvlAltQ = 0;
        public const int SegLvlAltLfYV = 1;
        public const int SegLvlRefFrame = 5;
        public const int SegLvlSkip = 6;
        public const int SegLvlGlobalMv = 7;
        public const int SegLvlMax = 8;
        public const int MaxLoopFilter = 63;
        public const int RefScaleShift = 14;
        public const int SubpelBits = 4;
        public const int SubpelMask = 15;
        public const int ScaleSubpelBits = 10;
        public const int MvBorder = 128;
        public const int PaletteColors = 8;
        public const int PaletteNumNeighbors = 3;
        public const int DeltaQSmall = 3;
        public const int DeltaLfSmall = 3;
        public const int MaxAngleDelta = 3;
        public const int AngleStep = 3;
        public const int WarpedModelPrecBits = 16;
        public const int GmAbsTransBits = 12;
        public const int GmAbsTransOnlyBits = 9;
        public const int GmAbsAlphaBits = 12;
        public const int DivLutPrecBits = 14;
        public const int DivLutBits = 8;
        public const int DivLutNum = 257;
        public const int LeastSquaresSamplesMax = 8;
        public const int LsMvMax = 256;
        public const int WarpedModelTransClamp = 1 << 23;
        public const int WarpedModelNonDiagAffineClamp = 1 << 13;
        public const int WarpedPixelPrecShifts = 1 << 6;
        public const int WarpedDiffPrecBits = 10;
        public const int GmAlphaPrecBits = 15;
        public const int GmTransPrecBits = 6;
        public const int GmTransOnlyPrecBits = 3;
        public const int MaskMasterSize = 64;
        public const int MaxVartxDepth = 2;
        public const int RefCatLevel = 640;
        public const int MaxRefMvStackSize = 8;
        public const int MfmvStackSize = 3;
        public const int FilterBits = 7;
        public const int WienerCoeffs = 3;
        public const int SgrprojParamsBits = 4;
        public const int SgrprojPrjSubexpK = 4;
        public const int SgrprojPrjBits = 7;
        public const int SgrprojRstBits = 4;
        public const int SgrprojMtableBits = 20;
        public const int SgrprojRecipBits = 12;
        public const int SgrprojSgrBits = 8;
        public const int EcProbShift = 6;
        public const int EcMinProb = 4;
        public const int SelectScreenContentTools = 2;
        public const int SelectIntegerMv = 2;
        public const int RestorationTileSizeMax = 256;
        public const int MaxFrameDistance = 31;
        public const int MaxOffsetWidth = 8;
        public const int MaxOffsetHeight = 0;
        public const int WarpParamReduceBits = 6;
        public const int NumBaseLevels = 2;
        public const int CoeffBaseRange = 12;
        public const int BrCdfSize = 4;
        public const int SigCoefContexts = 42;
        public const int SigCoefContextsEob = 4;
        public const int SuperresNum = 8;
        public const int SuperresDenomMin = 9;
        public const int SuperresDenomBits = 3;
        public const int SuperresFilterBits = 6;
        public const int SuperresFilterTaps = 8;
        public const int SuperresFilterOffset = 3;
        public const int SuperresScaleBits = 14;
        public const int SuperresScaleMask = (1 << 14) - 1;
        public const int SuperresExtraBits = 8;
        public const int RefMvsLimit = (1 << 12) - 1;
        public const int IntraFilterScaleBits = 4;
        public const int PrimaryRefNone = 7;
        public const int FrameLfCount = 4;
        public const int MaxTxDepth = 2;

        // OBU types
        public const int ObuSequenceHeader = 1, ObuTemporalDelimiter = 2, ObuFrameHeader = 3, ObuTileGroup = 4, ObuMetadata = 5,
            ObuFrame = 6, ObuRedundantFrameHeader = 7, ObuTileList = 8, ObuPadding = 15;

        // frame types
        public const int KeyFrame = 0, InterFrame = 1, IntraOnlyFrame = 2, SwitchFrame = 3;

        // reference frames
        public const int None = -1, IntraFrame = 0, LastFrame = 1, Last2Frame = 2, Last3Frame = 3, GoldenFrame = 4, BwdrefFrame = 5,
            Altref2Frame = 6, AltrefFrame = 7;

        // block sizes
        public const int Block4x4 = 0, Block4x8 = 1, Block8x4 = 2, Block8x8 = 3, Block8x16 = 4, Block16x8 = 5, Block16x16 = 6,
            Block16x32 = 7, Block32x16 = 8, Block32x32 = 9, Block32x64 = 10, Block64x32 = 11, Block64x64 = 12, Block64x128 = 13,
            Block128x64 = 14, Block128x128 = 15, Block4x16 = 16, Block16x4 = 17, Block8x32 = 18, Block32x8 = 19, Block16x64 = 20,
            Block64x16 = 21;

        // partitions
        public const int PartitionNone = 0, PartitionHorz = 1, PartitionVert = 2, PartitionSplit = 3, PartitionHorzA = 4,
            PartitionHorzB = 5, PartitionVertA = 6, PartitionVertB = 7, PartitionHorz4 = 8, PartitionVert4 = 9;

        // transform sizes
        public const int Tx4x4 = 0, Tx8x8 = 1, Tx16x16 = 2, Tx32x32 = 3, Tx64x64 = 4, Tx4x8 = 5, Tx8x4 = 6, Tx8x16 = 7, Tx16x8 = 8,
            Tx16x32 = 9, Tx32x16 = 10, Tx32x64 = 11, Tx64x32 = 12, Tx4x16 = 13, Tx16x4 = 14, Tx8x32 = 15, Tx32x8 = 16,
            Tx16x64 = 17, Tx64x16 = 18;
        public const int TxSizes = 5;
        public const int TxSizesAll = 19;

        // tx modes
        public const int Only4x4 = 0, TxModeLargest = 1, TxModeSelect = 2;

        // transform types
        public const int DctDct = 0, AdstDct = 1, DctAdst = 2, AdstAdst = 3, FlipadstDct = 4, DctFlipadst = 5, FlipadstFlipadst = 6,
            AdstFlipadst = 7, FlipadstAdst = 8, Idtx = 9, VDct = 10, HDct = 11, VAdst = 12, HAdst = 13, VFlipadst = 14, HFlipadst = 15;

        // transform sets
        public const int TxSetDctOnly = 0, TxSetIntra1 = 1, TxSetIntra2 = 2, TxSetInter1 = 1, TxSetInter2 = 2, TxSetInter3 = 3;

        // intra modes
        public const int DcPred = 0, VPred = 1, HPred = 2, D45Pred = 3, D135Pred = 4, D113Pred = 5, D157Pred = 6, D203Pred = 7,
            D67Pred = 8, SmoothPred = 9, SmoothVPred = 10, SmoothHPred = 11, PaethPred = 12, UvCflPred = 13;
        public const int IntraModes = 13;

        // inter modes
        public const int NearestMv = 14, NearMv = 15, GlobalMv = 16, NewMv = 17, NearestNearestMv = 18, NearNearMv = 19,
            NearestNewMv = 20, NewNearestMv = 21, NearNewMv = 22, NewNearMv = 23, GlobalGlobalMv = 24, NewNewMv = 25;

        // interpolation filters
        public const int EightTap = 0, EightTapSmooth = 1, EightTapSharp = 2, Bilinear = 3, Switchable = 4;

        // motion modes
        public const int Simple = 0, Obmc = 1, LocalWarp = 2;

        // compound types
        public const int CompoundWedge = 0, CompoundDiffwtd = 1, CompoundAverage = 2, CompoundIntra = 3, CompoundDistance = 4;

        // interintra modes
        public const int IiDcPred = 0, IiVPred = 1, IiHPred = 2, IiSmoothPred = 3;

        // global motion types
        public const int Identity = 0, Translation = 1, RotZoom = 2, Affine = 3;

        // restoration types
        public const int RestoreNone = 0, RestoreWiener = 1, RestoreSgrproj = 2, RestoreSwitchable = 3;

        // MV joints, classes
        public const int MvJointZero = 0, MvJointHnzvz = 1, MvJointHzvnz = 2, MvJointHnzvnz = 3;
        public const int MvIntrabcContext = 1;

        // colour
        public const int CpBt709 = 1, CpUnspecified = 2;
        public const int TcUnspecified = 2, TcSrgb = 13;
        public const int McIdentity = 0, McUnspecified = 2;
    }
}
