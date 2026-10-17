using System;

namespace Aardvark.ImageCodecs.Av1
{
    /// <summary>Sequence header OBU (AV1 spec 5.5).</summary>
    internal sealed class Av1SequenceHeader
    {
        public int Profile;
        public bool StillPicture, ReducedStillPictureHeader;
        public bool TimingInfoPresent, DecoderModelInfoPresent, EqualPictureInterval, InitialDisplayDelayPresent;
        public uint NumUnitsInDisplayTick, TimeScale, NumTicksPerPictureMinus1;
        public int BufferDelayLengthMinus1, BufferRemovalTimeLengthMinus1, FramePresentationTimeLengthMinus1;
        public int OperatingPointsCount;
        public int[] OperatingPointIdc = new int[32];
        public int[] SeqLevelIdx = new int[32];
        public int[] SeqTier = new int[32];
        public bool[] DecoderModelPresentForThisOp = new bool[32];
        public int OperatingPoint, OperatingPointIdc0;
        public int FrameWidthBitsMinus1, FrameHeightBitsMinus1, MaxFrameWidthMinus1, MaxFrameHeightMinus1;
        public bool FrameIdNumbersPresent;
        public int DeltaFrameIdLengthMinus2, AdditionalFrameIdLengthMinus1;
        public bool Use128x128Superblock, EnableFilterIntra, EnableIntraEdgeFilter, EnableInterintraCompound, EnableMaskedCompound,
            EnableWarpedMotion, EnableDualFilter, EnableOrderHint, EnableJntComp, EnableRefFrameMvs;
        public int SeqForceScreenContentTools, SeqForceIntegerMv, OrderHintBits;
        public bool EnableSuperres, EnableCdef, EnableRestoration;
        // colour config
        public int BitDepth;
        public bool MonoChrome;
        public int NumPlanes;
        public bool ColorDescriptionPresent;
        public int ColorPrimaries = Av1C.CpUnspecified, TransferCharacteristics = Av1C.TcUnspecified, MatrixCoefficients = Av1C.McUnspecified;
        public bool ColorRange;
        public int SubsamplingX, SubsamplingY, ChromaSamplePosition;
        public bool SeparateUvDeltaQ;
        public bool FilmGrainParamsPresent;

        /// <summary>Parses the payload of a sequence header OBU; <paramref name="operatingPoint"/> chooses the operating point.</summary>
        public static Av1SequenceHeader Parse(Av1BitReader r, int operatingPoint = 0)
        {
            var s = new Av1SequenceHeader();
            s.Profile = r.I(3);
            if (s.Profile > 2) throw new ImageNotSupportedException($"AV1: seq_profile {s.Profile}");
            s.StillPicture = r.Flag();
            s.ReducedStillPictureHeader = r.Flag();
            if (s.ReducedStillPictureHeader)
            {
                s.OperatingPointsCount = 1;
                s.OperatingPointIdc[0] = 0;
                s.SeqLevelIdx[0] = r.I(5);
            }
            else
            {
                s.TimingInfoPresent = r.Flag();
                if (s.TimingInfoPresent)
                {
                    s.NumUnitsInDisplayTick = r.F(32);
                    s.TimeScale = r.F(32);
                    s.EqualPictureInterval = r.Flag();
                    if (s.EqualPictureInterval) s.NumTicksPerPictureMinus1 = r.Uvlc();
                    s.DecoderModelInfoPresent = r.Flag();
                    if (s.DecoderModelInfoPresent)
                    {
                        s.BufferDelayLengthMinus1 = r.I(5);
                        r.F(32);   // num_units_in_decoding_tick
                        s.BufferRemovalTimeLengthMinus1 = r.I(5);
                        s.FramePresentationTimeLengthMinus1 = r.I(5);
                    }
                }
                s.InitialDisplayDelayPresent = r.Flag();
                s.OperatingPointsCount = r.I(5) + 1;
                for (int i = 0; i < s.OperatingPointsCount; i++)
                {
                    s.OperatingPointIdc[i] = r.I(12);
                    s.SeqLevelIdx[i] = r.I(5);
                    s.SeqTier[i] = s.SeqLevelIdx[i] > 7 ? r.I(1) : 0;
                    if (s.DecoderModelInfoPresent)
                    {
                        s.DecoderModelPresentForThisOp[i] = r.Flag();
                        if (s.DecoderModelPresentForThisOp[i])
                        {
                            int n = s.BufferDelayLengthMinus1 + 1;
                            r.F(n); r.F(n); r.F(1);   // decoder / encoder buffer delay, low delay mode
                        }
                    }
                    if (s.InitialDisplayDelayPresent && r.Flag()) r.F(4);
                }
            }
            s.OperatingPoint = Math.Clamp(operatingPoint, 0, s.OperatingPointsCount - 1);
            s.OperatingPointIdc0 = s.OperatingPointIdc[s.OperatingPoint];
            s.FrameWidthBitsMinus1 = r.I(4);
            s.FrameHeightBitsMinus1 = r.I(4);
            s.MaxFrameWidthMinus1 = r.I(s.FrameWidthBitsMinus1 + 1);
            s.MaxFrameHeightMinus1 = r.I(s.FrameHeightBitsMinus1 + 1);
            s.FrameIdNumbersPresent = !s.ReducedStillPictureHeader && r.Flag();
            if (s.FrameIdNumbersPresent)
            {
                s.DeltaFrameIdLengthMinus2 = r.I(4);
                s.AdditionalFrameIdLengthMinus1 = r.I(3);
            }
            s.Use128x128Superblock = r.Flag();
            s.EnableFilterIntra = r.Flag();
            s.EnableIntraEdgeFilter = r.Flag();
            if (s.ReducedStillPictureHeader)
            {
                s.SeqForceScreenContentTools = Av1C.SelectScreenContentTools;
                s.SeqForceIntegerMv = Av1C.SelectIntegerMv;
                s.OrderHintBits = 0;
            }
            else
            {
                s.EnableInterintraCompound = r.Flag();
                s.EnableMaskedCompound = r.Flag();
                s.EnableWarpedMotion = r.Flag();
                s.EnableDualFilter = r.Flag();
                s.EnableOrderHint = r.Flag();
                if (s.EnableOrderHint)
                {
                    s.EnableJntComp = r.Flag();
                    s.EnableRefFrameMvs = r.Flag();
                }
                s.SeqForceScreenContentTools = r.Flag() ? Av1C.SelectScreenContentTools : r.I(1);
                if (s.SeqForceScreenContentTools > 0)
                    s.SeqForceIntegerMv = r.Flag() ? Av1C.SelectIntegerMv : r.I(1);
                else
                    s.SeqForceIntegerMv = Av1C.SelectIntegerMv;
                s.OrderHintBits = s.EnableOrderHint ? r.I(3) + 1 : 0;
            }
            s.EnableSuperres = r.Flag();
            s.EnableCdef = r.Flag();
            s.EnableRestoration = r.Flag();
            ColorConfig(r, s);
            s.FilmGrainParamsPresent = r.Flag();
            return s;
        }

        static void ColorConfig(Av1BitReader r, Av1SequenceHeader s)
        {
            bool highBitdepth = r.Flag();
            if (s.Profile == 2 && highBitdepth) s.BitDepth = r.Flag() ? 12 : 10;
            else s.BitDepth = highBitdepth ? 10 : 8;
            s.MonoChrome = s.Profile != 1 && r.Flag();
            s.NumPlanes = s.MonoChrome ? 1 : 3;
            s.ColorDescriptionPresent = r.Flag();
            if (s.ColorDescriptionPresent)
            {
                s.ColorPrimaries = r.I(8);
                s.TransferCharacteristics = r.I(8);
                s.MatrixCoefficients = r.I(8);
            }
            if (s.MonoChrome)
            {
                s.ColorRange = r.Flag();
                s.SubsamplingX = s.SubsamplingY = 1;
                s.ChromaSamplePosition = 0;
                s.SeparateUvDeltaQ = false;
                return;
            }
            if (s.ColorPrimaries == Av1C.CpBt709 && s.TransferCharacteristics == Av1C.TcSrgb && s.MatrixCoefficients == Av1C.McIdentity)
            {
                s.ColorRange = true;
                s.SubsamplingX = s.SubsamplingY = 0;
            }
            else
            {
                s.ColorRange = r.Flag();
                if (s.Profile == 0) { s.SubsamplingX = s.SubsamplingY = 1; }
                else if (s.Profile == 1) { s.SubsamplingX = s.SubsamplingY = 0; }
                else
                {
                    if (s.BitDepth == 12)
                    {
                        s.SubsamplingX = r.I(1);
                        s.SubsamplingY = s.SubsamplingX != 0 ? r.I(1) : 0;
                    }
                    else { s.SubsamplingX = 1; s.SubsamplingY = 0; }
                }
                if (s.SubsamplingX != 0 && s.SubsamplingY != 0) s.ChromaSamplePosition = r.I(2);
            }
            s.SeparateUvDeltaQ = r.Flag();
        }
    }

    /// <summary>Film grain parameters (AV1 spec 5.9.30, 6.8.20).</summary>
    internal sealed class Av1FilmGrainParams
    {
        public bool ApplyGrain;
        public int GrainSeed;
        public bool UpdateGrain;
        public int NumYPoints;
        public int[] PointYValue = new int[16], PointYScaling = new int[16];
        public bool ChromaScalingFromLuma;
        public int NumCbPoints, NumCrPoints;
        public int[] PointCbValue = new int[16], PointCbScaling = new int[16];
        public int[] PointCrValue = new int[16], PointCrScaling = new int[16];
        public int GrainScalingMinus8, ArCoeffLag;
        public int[] ArCoeffsYPlus128 = new int[24], ArCoeffsCbPlus128 = new int[25], ArCoeffsCrPlus128 = new int[25];
        public int ArCoeffShiftMinus6, GrainScaleShift;
        public int CbMult, CbLumaMult, CbOffset, CrMult, CrLumaMult, CrOffset;
        public bool OverlapFlag, ClipToRestrictedRange;

        public Av1FilmGrainParams Clone()
        {
            var c = (Av1FilmGrainParams)MemberwiseClone();
            c.PointYValue = (int[])PointYValue.Clone(); c.PointYScaling = (int[])PointYScaling.Clone();
            c.PointCbValue = (int[])PointCbValue.Clone(); c.PointCbScaling = (int[])PointCbScaling.Clone();
            c.PointCrValue = (int[])PointCrValue.Clone(); c.PointCrScaling = (int[])PointCrScaling.Clone();
            c.ArCoeffsYPlus128 = (int[])ArCoeffsYPlus128.Clone(); c.ArCoeffsCbPlus128 = (int[])ArCoeffsCbPlus128.Clone();
            c.ArCoeffsCrPlus128 = (int[])ArCoeffsCrPlus128.Clone();
            return c;
        }
    }

    /// <summary>The uncompressed header of a frame (AV1 spec 5.9) with the derived frame level variables.</summary>
    internal sealed class Av1FrameHeader
    {
        public bool ShowExistingFrame;
        public int FrameToShowMapIdx;
        public int FrameType;
        public bool FrameIsIntra, ShowFrame, ShowableFrame, ErrorResilientMode;
        public bool DisableCdfUpdate;
        public bool AllowScreenContentTools, ForceIntegerMv;
        public int CurrentFrameId;
        public bool FrameSizeOverrideFlag;
        public int OrderHint;
        public int PrimaryRefFrame;
        public int RefreshFrameFlags;
        public int[] RefOrderHintSignalled = new int[8];
        public int FrameWidth, FrameHeight, UpscaledWidth, RenderWidth, RenderHeight;
        public bool UseSuperres;
        public int SuperresDenom;
        public int MiCols, MiRows;
        public bool AllowIntrabc;
        public int[] RefFrameIdx = new int[7];
        public bool AllowHighPrecisionMv;
        public int InterpolationFilter;
        public bool IsMotionModeSwitchable, UseRefFrameMvs;
        public int[] OrderHints = new int[8];
        public int[] RefFrameSignBias = new int[8];
        public bool DisableFrameEndUpdateCdf;
        // tile info
        public int TileColsLog2, TileRowsLog2, TileCols, TileRows;
        public int[] MiColStarts = new int[65], MiRowStarts = new int[65];
        public int ContextUpdateTileId, TileSizeBytes;
        // quantization
        public int BaseQIdx, DeltaQYDc, DeltaQUDc, DeltaQUAc, DeltaQVDc, DeltaQVAc;
        public bool UsingQmatrix;
        public int QmY, QmU, QmV;
        // segmentation
        public bool SegmentationEnabled, SegmentationUpdateMap, SegmentationTemporalUpdate, SegmentationUpdateData;
        public bool[] FeatureEnabled = new bool[8 * 8];
        public int[] FeatureData = new int[8 * 8];
        public bool SegIdPreSkip;
        public int LastActiveSegId;
        // delta q / lf
        public bool DeltaQPresent, DeltaLfPresent, DeltaLfMulti;
        public int DeltaQRes, DeltaLfRes;
        // lossless
        public bool CodedLossless, AllLossless;
        public bool[] LosslessArray = new bool[8];
        public int[] SegQMLevel = new int[3 * 8];
        // loop filter
        public int[] LoopFilterLevel = new int[4];
        public int LoopFilterSharpness;
        public bool LoopFilterDeltaEnabled;
        public int[] LoopFilterRefDeltas = new int[8];
        public int[] LoopFilterModeDeltas = new int[2];
        // cdef
        public int CdefDamping, CdefBits;
        public int[] CdefYPriStrength = new int[8], CdefYSecStrength = new int[8], CdefUvPriStrength = new int[8], CdefUvSecStrength = new int[8];
        // loop restoration
        public int[] FrameRestorationType = new int[3];
        public int[] LoopRestorationSize = new int[3];
        public bool UsesLr;
        // modes
        public int TxMode;
        public bool ReferenceSelect;
        public bool SkipModePresent;
        public int[] SkipModeFrame = new int[2];
        public bool AllowWarpedMotion, ReducedTxSet;
        public int[] GmType = new int[8];
        public int[,] GmParams = new int[8, 6];
        public Av1FilmGrainParams FilmGrain = new();

        public bool FeatureOn(int segment, int feature) => SegmentationEnabled && FeatureEnabled[segment * 8 + feature];
        public int Feature(int segment, int feature) => FeatureData[segment * 8 + feature];

        /// <summary>get_qindex( 1, segmentId ) / ( 0, segmentId ) (spec 7.12.2).</summary>
        public int QIndex(bool ignoreDeltaQ, int segmentId, int currentQIndex)
        {
            if (FeatureOn(segmentId, Av1C.SegLvlAltQ))
            {
                int data = Feature(segmentId, Av1C.SegLvlAltQ);
                int q = BaseQIdx + data;
                if (!ignoreDeltaQ && DeltaQPresent) q = currentQIndex + data;
                return Math.Clamp(q, 0, 255);
            }
            if (!ignoreDeltaQ && DeltaQPresent) return currentQIndex;
            return BaseQIdx;
        }
    }
}
