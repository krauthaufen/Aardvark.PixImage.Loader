using System;
using T = Aardvark.ImageCodecs.Av1.Av1Tables;

namespace Aardvark.ImageCodecs.Av1
{
    internal sealed partial class Av1TileDecoder
    {
        readonly int[] leftRefFrame = new int[2], aboveRefFrame = new int[2];
        bool leftIntra, aboveIntra, leftSingle, aboveSingle;
        int RefMvIdx;
        readonly int[] predMvRow = new int[2], predMvCol = new int[2];

        // ------------------------------------------------------------------ inter frame mode info (5.11.18)

        void InterFrameModeInfo()
        {
            useIntrabc = false;
            if (AvailL)
            {
                ref var l = ref Nb(MiRow, MiCol - 1);
                leftRefFrame[0] = l.RefFrame0; leftRefFrame[1] = l.RefFrame1;
            }
            else { leftRefFrame[0] = Av1C.IntraFrame; leftRefFrame[1] = Av1C.None; }
            if (AvailU)
            {
                ref var a = ref Nb(MiRow - 1, MiCol);
                aboveRefFrame[0] = a.RefFrame0; aboveRefFrame[1] = a.RefFrame1;
            }
            else { aboveRefFrame[0] = Av1C.IntraFrame; aboveRefFrame[1] = Av1C.None; }
            leftIntra = leftRefFrame[0] <= Av1C.IntraFrame;
            aboveIntra = aboveRefFrame[0] <= Av1C.IntraFrame;
            leftSingle = leftRefFrame[1] <= Av1C.IntraFrame;
            aboveSingle = aboveRefFrame[1] <= Av1C.IntraFrame;
            skip = false;
            InterSegmentId(true);
            ReadSkipMode();
            if (skipMode) skip = true;
            else ReadSkip();
            if (!h.SegIdPreSkip) InterSegmentId(false);
            lossless = h.LosslessArray[segmentId];
            ReadCdef();
            ReadDeltaQIndex();
            ReadDeltaLf();
            readDeltas = false;
            ReadIsInter();
            PaletteSizeY = PaletteSizeUV = 0;
            if (isInter) InterBlockModeInfo();
            else IntraBlockModeInfo();
        }

        void InterSegmentId(bool preSkip)
        {
            if (!h.SegmentationEnabled) { segmentId = 0; return; }
            int predicted = GetSegmentId();
            if (h.SegmentationUpdateMap)
            {
                if (preSkip && !h.SegIdPreSkip) { segmentId = 0; return; }
                if (!preSkip)
                {
                    if (skip)
                    {
                        for (int i = 0; i < bw4; i++) aboveSegPredCtx[MiCol + i] = 0;
                        for (int i = 0; i < bh4; i++) leftSegPredCtx[MiRow + i] = 0;
                        ReadSegmentId();
                        return;
                    }
                }
                if (h.SegmentationTemporalUpdate)
                {
                    int ctx = leftSegPredCtx[MiRow] + aboveSegPredCtx[MiCol];
                    int segIdPredicted = S(Av1Cdf.SegmentIdPredicted + ctx * 3, 2);
                    if (segIdPredicted != 0) segmentId = predicted;
                    else ReadSegmentId();
                    for (int i = 0; i < bw4; i++) aboveSegPredCtx[MiCol + i] = (byte)segIdPredicted;
                    for (int i = 0; i < bh4; i++) leftSegPredCtx[MiRow + i] = (byte)segIdPredicted;
                }
                else ReadSegmentId();
            }
            else segmentId = predicted;
        }

        int GetSegmentId()
        {
            int xMis = Math.Min(fs.MiCols - MiCol, bw4), yMis = Math.Min(fs.MiRows - MiRow, bh4);
            int seg = 7;
            var prev = fs.PrevSegmentIds;
            if (prev == null) return 0;
            for (int y = 0; y < yMis; y++)
                for (int x = 0; x < xMis; x++)
                    seg = Math.Min(seg, prev[(MiRow + y) * fs.MiStride + MiCol + x]);
            return seg;
        }

        void ReadIsInter()
        {
            if (skipMode) isInter = true;
            else if (SegFeatureActive(Av1C.SegLvlRefFrame)) isInter = h.Feature(segmentId, Av1C.SegLvlRefFrame) != Av1C.IntraFrame;
            else if (SegFeatureActive(Av1C.SegLvlGlobalMv)) isInter = true;
            else
            {
                int ctx;
                if (AvailU && AvailL) ctx = leftIntra && aboveIntra ? 3 : leftIntra || aboveIntra ? 1 : 0;
                else if (AvailU || AvailL) ctx = 2 * ((AvailU ? aboveIntra : leftIntra) ? 1 : 0);
                else ctx = 0;
                isInter = S(Av1Cdf.IsInter + ctx * 3, 2) != 0;
            }
        }

        void IntraBlockModeInfo()
        {
            RefFrame[0] = Av1C.IntraFrame;
            RefFrame[1] = Av1C.None;
            YMode = S(Av1Cdf.YMode + T.SizeGroup[MiSize] * 14, 13);
            IntraAngleInfoY();
            if (HasChroma)
            {
                UVMode = ReadUvMode();
                if (UVMode == Av1C.UvCflPred) ReadCflAlphas();
                IntraAngleInfoUv();
            }
            PaletteSizeY = PaletteSizeUV = 0;
            if (MiSize >= Av1C.Block8x8 && BlockWidth(MiSize) <= 64 && BlockHeight(MiSize) <= 64 && h.AllowScreenContentTools)
                PaletteModeInfo();
            FilterIntraModeInfo();
        }

        bool HasNearMv() => YMode is Av1C.NearMv or Av1C.NearNearMv or Av1C.NearNewMv or Av1C.NewNearMv;

        void InterBlockModeInfo()
        {
            PaletteSizeY = PaletteSizeUV = 0;
            ReadRefFrames();
            bool isCompound = RefFrame[1] > Av1C.IntraFrame;
            FindMvStack(isCompound);
            if (skipMode) YMode = Av1C.NearestNearestMv;
            else if (SegFeatureActive(Av1C.SegLvlSkip) || SegFeatureActive(Av1C.SegLvlGlobalMv)) YMode = Av1C.GlobalMv;
            else if (isCompound)
            {
                int ctx = T.CompoundModeCtxMap[(refMvContext >> 1) * 5 + Math.Min(newMvContext, 4)];
                YMode = Av1C.NearestNearestMv + S(Av1Cdf.CompoundMode + ctx * 9, 8);
            }
            else
            {
                if (S(Av1Cdf.NewMv + newMvContext * 3, 2) == 0) YMode = Av1C.NewMv;
                else if (S(Av1Cdf.ZeroMv + zeroMvContext * 3, 2) == 0) YMode = Av1C.GlobalMv;
                else YMode = S(Av1Cdf.RefMv + refMvContext * 3, 2) == 0 ? Av1C.NearestMv : Av1C.NearMv;
            }
            RefMvIdx = 0;
            if (YMode == Av1C.NewMv || YMode == Av1C.NewNewMv)
            {
                for (int idx = 0; idx < 2; idx++)
                    if (numMvFound > idx + 1)
                    {
                        int drl = S(Av1Cdf.DrlMode + drlCtxStack[idx] * 3, 2);
                        if (drl == 0) { RefMvIdx = idx; break; }
                        RefMvIdx = idx + 1;
                    }
            }
            else if (HasNearMv())
            {
                RefMvIdx = 1;
                for (int idx = 1; idx < 3; idx++)
                    if (numMvFound > idx + 1)
                    {
                        int drl = S(Av1Cdf.DrlMode + drlCtxStack[idx] * 3, 2);
                        if (drl == 0) { RefMvIdx = idx; break; }
                        RefMvIdx = idx + 1;
                    }
            }
            AssignMv(isCompound);
            ReadInterintraMode(isCompound);
            ReadMotionMode(isCompound);
            ReadCompoundType(isCompound);
            if (h.InterpolationFilter == Av1C.Switchable)
            {
                for (int dir = 0; dir < (seq.EnableDualFilter ? 2 : 1); dir++)
                {
                    if (NeedsInterpFilter())
                    {
                        int ctx = ((dir & 1) * 2 + (RefFrame[1] > Av1C.IntraFrame ? 1 : 0)) * 4;
                        int leftType = 3, aboveType = 3;
                        if (AvailL)
                        {
                            ref var l = ref Nb(MiRow, MiCol - 1);
                            if (l.RefFrame0 == RefFrame[0] || l.RefFrame1 == RefFrame[0]) leftType = dir == 0 ? l.InterpFilter0 : l.InterpFilter1;
                        }
                        if (AvailU)
                        {
                            ref var a = ref Nb(MiRow - 1, MiCol);
                            if (a.RefFrame0 == RefFrame[0] || a.RefFrame1 == RefFrame[0]) aboveType = dir == 0 ? a.InterpFilter0 : a.InterpFilter1;
                        }
                        if (leftType == aboveType) ctx += leftType;
                        else if (leftType == 3) ctx += aboveType;
                        else if (aboveType == 3) ctx += leftType;
                        else ctx += 3;
                        interpFilter[dir] = S(Av1Cdf.InterpFilter + ctx * 4, 3);
                    }
                    else interpFilter[dir] = Av1C.EightTap;
                }
                if (!seq.EnableDualFilter) interpFilter[1] = interpFilter[0];
            }
            else
            {
                interpFilter[0] = interpFilter[1] = h.InterpolationFilter;
            }
        }

        bool NeedsInterpFilter()
        {
            bool large = Math.Min(BlockWidth(MiSize), BlockHeight(MiSize)) >= 8;
            if (skipMode || motionMode == Av1C.LocalWarp) return false;
            if (large && YMode == Av1C.GlobalMv) return h.GmType[RefFrame[0]] == Av1C.Translation;
            if (large && YMode == Av1C.GlobalGlobalMv) return h.GmType[RefFrame[0]] == Av1C.Translation || h.GmType[RefFrame[1]] == Av1C.Translation;
            return true;
        }

        int CountRefs(int frameType)
        {
            int c = 0;
            if (AvailU)
            {
                if (aboveRefFrame[0] == frameType) c++;
                if (aboveRefFrame[1] == frameType) c++;
            }
            if (AvailL)
            {
                if (leftRefFrame[0] == frameType) c++;
                if (leftRefFrame[1] == frameType) c++;
            }
            return c;
        }

        static int RefCountCtx(int c0, int c1) => c0 < c1 ? 0 : c0 == c1 ? 1 : 2;
        static bool CheckBackward(int rf) => rf >= Av1C.BwdrefFrame && rf <= Av1C.AltrefFrame;

        int CtxCompRef() => RefCountCtx(CountRefs(Av1C.LastFrame) + CountRefs(Av1C.Last2Frame), CountRefs(Av1C.Last3Frame) + CountRefs(Av1C.GoldenFrame));
        int CtxCompRefP1() => RefCountCtx(CountRefs(Av1C.LastFrame), CountRefs(Av1C.Last2Frame));
        int CtxCompRefP2() => RefCountCtx(CountRefs(Av1C.Last3Frame), CountRefs(Av1C.GoldenFrame));
        int CtxCompBwdref() => RefCountCtx(CountRefs(Av1C.BwdrefFrame) + CountRefs(Av1C.Altref2Frame), CountRefs(Av1C.AltrefFrame));
        int CtxCompBwdrefP1() => RefCountCtx(CountRefs(Av1C.BwdrefFrame), CountRefs(Av1C.Altref2Frame));
        int CtxSingleRefP1() => RefCountCtx(
            CountRefs(Av1C.LastFrame) + CountRefs(Av1C.Last2Frame) + CountRefs(Av1C.Last3Frame) + CountRefs(Av1C.GoldenFrame),
            CountRefs(Av1C.BwdrefFrame) + CountRefs(Av1C.Altref2Frame) + CountRefs(Av1C.AltrefFrame));
        int CtxUniCompRefP1() => RefCountCtx(CountRefs(Av1C.Last2Frame), CountRefs(Av1C.Last3Frame) + CountRefs(Av1C.GoldenFrame));

        int CompModeCtx()
        {
            if (AvailU && AvailL)
            {
                if (aboveSingle && leftSingle) return (CheckBackward(aboveRefFrame[0]) ? 1 : 0) ^ (CheckBackward(leftRefFrame[0]) ? 1 : 0);
                if (aboveSingle) return 2 + (CheckBackward(aboveRefFrame[0]) || aboveIntra ? 1 : 0);
                if (leftSingle) return 2 + (CheckBackward(leftRefFrame[0]) || leftIntra ? 1 : 0);
                return 4;
            }
            if (AvailU) return aboveSingle ? (CheckBackward(aboveRefFrame[0]) ? 1 : 0) : 3;
            if (AvailL) return leftSingle ? (CheckBackward(leftRefFrame[0]) ? 1 : 0) : 3;
            return 1;
        }

        static bool IsSamedirRefPair(int r0, int r1) => (r0 >= Av1C.BwdrefFrame) == (r1 >= Av1C.BwdrefFrame);

        int CompRefTypeCtx()
        {
            int above0 = aboveRefFrame[0], above1 = aboveRefFrame[1], left0 = leftRefFrame[0], left1 = leftRefFrame[1];
            bool aboveCompInter = AvailU && !aboveIntra && !aboveSingle;
            bool leftCompInter = AvailL && !leftIntra && !leftSingle;
            bool aboveUniComp = aboveCompInter && IsSamedirRefPair(above0, above1);
            bool leftUniComp = leftCompInter && IsSamedirRefPair(left0, left1);
            if (AvailU && !aboveIntra && AvailL && !leftIntra)
            {
                int samedir = IsSamedirRefPair(above0, left0) ? 1 : 0;
                if (!aboveCompInter && !leftCompInter) return 1 + 2 * samedir;
                if (!aboveCompInter) return !leftUniComp ? 1 : 3 + samedir;
                if (!leftCompInter) return !aboveUniComp ? 1 : 3 + samedir;
                if (!aboveUniComp && !leftUniComp) return 0;
                if (!aboveUniComp || !leftUniComp) return 2;
                return 3 + ((above0 == Av1C.BwdrefFrame) == (left0 == Av1C.BwdrefFrame) ? 1 : 0);
            }
            if (AvailU && AvailL)
            {
                if (aboveCompInter) return 1 + 2 * (aboveUniComp ? 1 : 0);
                if (leftCompInter) return 1 + 2 * (leftUniComp ? 1 : 0);
                return 2;
            }
            if (aboveCompInter) return 4 * (aboveUniComp ? 1 : 0);
            if (leftCompInter) return 4 * (leftUniComp ? 1 : 0);
            return 2;
        }

        void ReadRefFrames()
        {
            if (skipMode)
            {
                RefFrame[0] = h.SkipModeFrame[0];
                RefFrame[1] = h.SkipModeFrame[1];
                return;
            }
            if (SegFeatureActive(Av1C.SegLvlRefFrame))
            {
                RefFrame[0] = h.Feature(segmentId, Av1C.SegLvlRefFrame);
                RefFrame[1] = Av1C.None;
                return;
            }
            if (SegFeatureActive(Av1C.SegLvlSkip) || SegFeatureActive(Av1C.SegLvlGlobalMv))
            {
                RefFrame[0] = Av1C.LastFrame;
                RefFrame[1] = Av1C.None;
                return;
            }
            bool compound = h.ReferenceSelect && Math.Min(bw4, bh4) >= 2 && S(Av1Cdf.CompMode + CompModeCtx() * 3, 2) != 0;
            if (compound)
            {
                int compRefType = S(Av1Cdf.CompRefType + CompRefTypeCtx() * 3, 2);
                if (compRefType == 0)   // UNIDIR_COMP_REFERENCE
                {
                    if (S(Av1Cdf.UniCompRef + (CtxSingleRefP1() * 3 + 0) * 3, 2) != 0)
                    {
                        RefFrame[0] = Av1C.BwdrefFrame;
                        RefFrame[1] = Av1C.AltrefFrame;
                    }
                    else
                    {
                        if (S(Av1Cdf.UniCompRef + (CtxUniCompRefP1() * 3 + 1) * 3, 2) != 0)
                        {
                            if (S(Av1Cdf.UniCompRef + (CtxCompRefP2() * 3 + 2) * 3, 2) != 0)
                            {
                                RefFrame[0] = Av1C.LastFrame;
                                RefFrame[1] = Av1C.GoldenFrame;
                            }
                            else
                            {
                                RefFrame[0] = Av1C.LastFrame;
                                RefFrame[1] = Av1C.Last3Frame;
                            }
                        }
                        else
                        {
                            RefFrame[0] = Av1C.LastFrame;
                            RefFrame[1] = Av1C.Last2Frame;
                        }
                    }
                }
                else
                {
                    if (S(Av1Cdf.CompRef + (CtxCompRef() * 3 + 0) * 3, 2) == 0)
                        RefFrame[0] = S(Av1Cdf.CompRef + (CtxCompRefP1() * 3 + 1) * 3, 2) != 0 ? Av1C.Last2Frame : Av1C.LastFrame;
                    else
                        RefFrame[0] = S(Av1Cdf.CompRef + (CtxCompRefP2() * 3 + 2) * 3, 2) != 0 ? Av1C.GoldenFrame : Av1C.Last3Frame;
                    if (S(Av1Cdf.CompBwdRef + (CtxCompBwdref() * 2 + 0) * 3, 2) == 0)
                        RefFrame[1] = S(Av1Cdf.CompBwdRef + (CtxCompBwdrefP1() * 2 + 1) * 3, 2) != 0 ? Av1C.Altref2Frame : Av1C.BwdrefFrame;
                    else RefFrame[1] = Av1C.AltrefFrame;
                }
            }
            else
            {
                if (S(Av1Cdf.SingleRef + (CtxSingleRefP1() * 6 + 0) * 3, 2) != 0)
                {
                    if (S(Av1Cdf.SingleRef + (CtxCompBwdref() * 6 + 1) * 3, 2) == 0)
                        RefFrame[0] = S(Av1Cdf.SingleRef + (CtxCompBwdrefP1() * 6 + 5) * 3, 2) != 0 ? Av1C.Altref2Frame : Av1C.BwdrefFrame;
                    else RefFrame[0] = Av1C.AltrefFrame;
                }
                else
                {
                    if (S(Av1Cdf.SingleRef + (CtxCompRef() * 6 + 2) * 3, 2) != 0)
                        RefFrame[0] = S(Av1Cdf.SingleRef + (CtxCompRefP2() * 6 + 4) * 3, 2) != 0 ? Av1C.GoldenFrame : Av1C.Last3Frame;
                    else
                        RefFrame[0] = S(Av1Cdf.SingleRef + (CtxCompRefP1() * 6 + 3) * 3, 2) != 0 ? Av1C.Last2Frame : Av1C.LastFrame;
                }
                RefFrame[1] = Av1C.None;
            }
        }

        int GetMode(int refList)
        {
            int y = YMode;
            if (refList == 0)
            {
                if (y < Av1C.NearestNearestMv) return y;
                if (y is Av1C.NewNewMv or Av1C.NewNearestMv or Av1C.NewNearMv) return Av1C.NewMv;
                if (y is Av1C.NearestNearestMv or Av1C.NearestNewMv) return Av1C.NearestMv;
                if (y is Av1C.NearNearMv or Av1C.NearNewMv) return Av1C.NearMv;
                return Av1C.GlobalMv;
            }
            if (y is Av1C.NewNewMv or Av1C.NearestNewMv or Av1C.NearNewMv) return Av1C.NewMv;
            if (y is Av1C.NearestNearestMv or Av1C.NewNearestMv) return Av1C.NearestMv;
            if (y is Av1C.NearNearMv or Av1C.NewNearMv) return Av1C.NearMv;
            return Av1C.GlobalMv;
        }

        void AssignMv(bool isCompound)
        {
            for (int i = 0; i < 1 + (isCompound ? 1 : 0); i++)
            {
                int compMode = useIntrabc ? Av1C.NewMv : GetMode(i);
                if (useIntrabc)
                {
                    predMvRow[0] = refStackRow[0 * 2]; predMvCol[0] = refStackCol[0 * 2];
                    if (predMvRow[0] == 0 && predMvCol[0] == 0) { predMvRow[0] = refStackRow[1 * 2]; predMvCol[0] = refStackCol[1 * 2]; }
                    if (predMvRow[0] == 0 && predMvCol[0] == 0)
                    {
                        int sb4 = T.Num4x4BlocksHigh[sbSize];
                        if (MiRow - sb4 < MiRowStart)
                        {
                            predMvRow[0] = 0;
                            predMvCol[0] = -(sb4 * Av1C.MiSize + Av1C.IntrabcDelayPixels) * 8;
                        }
                        else
                        {
                            predMvRow[0] = -(sb4 * Av1C.MiSize * 8);
                            predMvCol[0] = 0;
                        }
                    }
                }
                else if (compMode == Av1C.GlobalMv)
                {
                    predMvRow[i] = globalMvRow[i]; predMvCol[i] = globalMvCol[i];
                }
                else
                {
                    int pos = compMode == Av1C.NearestMv ? 0 : RefMvIdx;
                    if (compMode == Av1C.NewMv && numMvFound <= 1) pos = 0;
                    predMvRow[i] = refStackRow[pos * 2 + i]; predMvCol[i] = refStackCol[pos * 2 + i];
                }
                if (compMode == Av1C.NewMv) ReadMv(i);
                else { MvRow[i] = predMvRow[i]; MvCol[i] = predMvCol[i]; }
            }
        }

        void ReadMv(int rf)
        {
            int ctx = useIntrabc ? Av1C.MvIntrabcContext : 0;
            int joint = S(Av1Cdf.MvJoint + ctx * 5, 4);
            int d0 = 0, d1 = 0;
            if (joint == Av1C.MvJointHzvnz || joint == Av1C.MvJointHnzvnz) d0 = ReadMvComponent(ctx, 0);
            if (joint == Av1C.MvJointHnzvz || joint == Av1C.MvJointHnzvnz) d1 = ReadMvComponent(ctx, 1);
            MvRow[rf] = predMvRow[rf] + d0;
            MvCol[rf] = predMvCol[rf] + d1;
        }

        int ReadMvComponent(int ctx, int comp)
        {
            int cc = ctx * 2 + comp;
            int sign = S(Av1Cdf.MvSign + cc * 3, 2);
            int mvClass = S(Av1Cdf.MvClass + cc * 12, 11);
            int mag;
            if (mvClass == 0)
            {
                int class0Bit = S(Av1Cdf.MvClass0Bit + cc * 3, 2);
                int fr = h.ForceIntegerMv ? 3 : S(Av1Cdf.MvClass0Fr + (cc * 2 + class0Bit) * 5, 4);
                int hp = h.AllowHighPrecisionMv ? S(Av1Cdf.MvClass0Hp + cc * 3, 2) : 1;
                mag = ((class0Bit << 3) | (fr << 1) | hp) + 1;
            }
            else
            {
                int d = 0;
                for (int i = 0; i < mvClass; i++) d |= S(Av1Cdf.MvBit + (cc * 10 + i) * 3, 2) << i;
                mag = 2 << (mvClass + 2);   // CLASS0_SIZE << ( mv_class + 2 )
                int fr = h.ForceIntegerMv ? 3 : S(Av1Cdf.MvFr + cc * 5, 4);
                int hp = h.AllowHighPrecisionMv ? S(Av1Cdf.MvHp + cc * 3, 2) : 1;
                mag += ((d << 3) | (fr << 1) | hp) + 1;
            }
            return sign != 0 ? -mag : mag;
        }

        void ReadMotionMode(bool isCompound)
        {
            motionMode = Av1C.Simple;
            if (skipMode) return;
            if (!h.IsMotionModeSwitchable) return;
            if (Math.Min(BlockWidth(MiSize), BlockHeight(MiSize)) < 8) return;
            if (!h.ForceIntegerMv && (YMode == Av1C.GlobalMv || YMode == Av1C.GlobalGlobalMv) && h.GmType[RefFrame[0]] > Av1C.Translation) return;
            if (isCompound || RefFrame[1] == Av1C.IntraFrame || !HasOverlappableCandidates()) return;
            FindWarpSamples();
            if (h.ForceIntegerMv || numSamples == 0 || !h.AllowWarpedMotion || IsScaled(RefFrame[0]))
                motionMode = S(Av1Cdf.UseObmc + MiSize * 3, 2) != 0 ? Av1C.Obmc : Av1C.Simple;
            else motionMode = S(Av1Cdf.MotionMode + MiSize * 4, 3);
        }

        bool IsScaled(int refFrame)
        {
            var rs = fs.Dec.Ref[h.RefFrameIdx[refFrame - Av1C.LastFrame]]!;
            int xScale = ((rs.UpscaledWidth << Av1C.RefScaleShift) + h.FrameWidth / 2) / h.FrameWidth;
            int yScale = ((rs.FrameHeight << Av1C.RefScaleShift) + h.FrameHeight / 2) / h.FrameHeight;
            const int noScale = 1 << Av1C.RefScaleShift;
            return xScale != noScale || yScale != noScale;
        }

        void ReadInterintraMode(bool isCompound)
        {
            interintra = 0;
            if (!skipMode && seq.EnableInterintraCompound && !isCompound && MiSize >= Av1C.Block8x8 && MiSize <= Av1C.Block32x32)
            {
                int ctx = T.SizeGroup[MiSize] - 1;
                interintra = S(Av1Cdf.InterIntra + ctx * 3, 2);
                if (interintra != 0)
                {
                    interintraMode = S(Av1Cdf.InterIntraMode + ctx * 5, 4);
                    RefFrame[1] = Av1C.IntraFrame;
                    AngleDeltaY = AngleDeltaUV = 0;
                    useFilterIntra = false;
                    wedgeInterintra = S(Av1Cdf.WedgeInterIntra + MiSize * 3, 2);
                    if (wedgeInterintra != 0)
                    {
                        wedgeIndex = S(Av1Cdf.WedgeIndex + MiSize * 17, 16);
                        wedgeSign = 0;
                    }
                }
            }
        }

        void ReadCompoundType(bool isCompound)
        {
            compGroupIdx = 0;
            compoundIdx = 1;
            if (skipMode) { compoundType = Av1C.CompoundAverage; return; }
            if (isCompound)
            {
                int n = T.WedgeBits[MiSize];
                if (seq.EnableMaskedCompound)
                {
                    int ctx = 0;
                    if (AvailU)
                    {
                        if (!aboveSingle) ctx += Nb(MiRow - 1, MiCol).CompGroupIdx;
                        else if (aboveRefFrame[0] == Av1C.AltrefFrame) ctx += 3;
                    }
                    if (AvailL)
                    {
                        if (!leftSingle) ctx += Nb(MiRow, MiCol - 1).CompGroupIdx;
                        else if (leftRefFrame[0] == Av1C.AltrefFrame) ctx += 3;
                    }
                    ctx = Math.Min(5, ctx);
                    compGroupIdx = S(Av1Cdf.CompGroupIdx + ctx * 3, 2);
                }
                if (compGroupIdx == 0)
                {
                    if (seq.EnableJntComp)
                    {
                        int fwd = Math.Abs(fs.Dec.RelativeDist(h.OrderHints[RefFrame[0]], h.OrderHint));
                        int bck = Math.Abs(fs.Dec.RelativeDist(h.OrderHints[RefFrame[1]], h.OrderHint));
                        int ctx = fwd == bck ? 3 : 0;
                        if (AvailU)
                        {
                            if (!aboveSingle) ctx += Nb(MiRow - 1, MiCol).CompoundIdx;
                            else if (aboveRefFrame[0] == Av1C.AltrefFrame) ctx++;
                        }
                        if (AvailL)
                        {
                            if (!leftSingle) ctx += Nb(MiRow, MiCol - 1).CompoundIdx;
                            else if (leftRefFrame[0] == Av1C.AltrefFrame) ctx++;
                        }
                        compoundIdx = S(Av1Cdf.CompoundIdx + ctx * 3, 2);
                        compoundType = compoundIdx != 0 ? Av1C.CompoundAverage : Av1C.CompoundDistance;
                    }
                    else compoundType = Av1C.CompoundAverage;
                }
                else
                {
                    if (n == 0) compoundType = Av1C.CompoundDiffwtd;
                    else compoundType = S(Av1Cdf.CompoundType + MiSize * 3, 2);
                }
                if (compoundType == Av1C.CompoundWedge)
                {
                    wedgeIndex = S(Av1Cdf.WedgeIndex + MiSize * 17, 16);
                    wedgeSign = L(1);
                }
                else if (compoundType == Av1C.CompoundDiffwtd) maskType = L(1);
            }
            else
            {
                if (interintra != 0) compoundType = wedgeInterintra != 0 ? Av1C.CompoundWedge : Av1C.CompoundIntra;
                else compoundType = Av1C.CompoundAverage;
            }
        }
    }
}
