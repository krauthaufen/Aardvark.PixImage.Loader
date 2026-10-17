using System;
using T = Aardvark.ImageCodecs.Av1.Av1Tables;

namespace Aardvark.ImageCodecs.Av1
{
    /// <summary>Prediction for inter and intra block copy blocks (AV1 spec 5.11.33, 7.11.3).</summary>
    internal sealed partial class Av1TileDecoder
    {
        int[][] preds = null!;
        int[] intermediate = null!;
        int[] obmcPred = null!;
        byte[] mask = null!;
        int interRound0, interRound1, interPostRound;
        readonly int[] localWarpParams = new int[6];
        bool localValid;
        int fwdWeight, bckWeight;

        void EnsureInterScratch()
        {
            if (preds != null) return;
            preds = new[] { new int[128 * 128], new int[128 * 128] };
            intermediate = new int[(2 * 128 + 16 + 8) * 128];
            obmcPred = new int[128 * 64];
            mask = new byte[128 * 128];
        }

        void ComputePrediction()
        {
            if (!isInter) return;
            EnsureInterScratch();
            int subBlockMiRow = MiRow & sbMask, subBlockMiCol = MiCol & sbMask;
            for (int plane = 0; plane < 1 + (HasChroma ? 2 : 0); plane++)
            {
                int planeSz = PlaneResidualSize(MiSize, plane);
                int num4x4W = T.Num4x4BlocksWide[planeSz], num4x4H = T.Num4x4BlocksHigh[planeSz];
                int log2W = Av1C.MiSizeLog2 + T.MiWidthLog2[planeSz], log2H = Av1C.MiSizeLog2 + T.MiHeightLog2[planeSz];
                int sx = plane > 0 ? subX : 0, sy = plane > 0 ? subY : 0;
                int baseX = (MiCol >> sx) * Av1C.MiSize, baseY = (MiRow >> sy) * Av1C.MiSize;
                int candRow = (MiRow >> sy) << sy, candCol = (MiCol >> sx) << sx;
                bool isInterIntra = isInter && RefFrame[1] == Av1C.IntraFrame;
                if (isInterIntra)
                {
                    int mode = interintraMode switch
                    {
                        Av1C.IiDcPred => Av1C.DcPred,
                        Av1C.IiVPred => Av1C.VPred,
                        Av1C.IiHPred => Av1C.HPred,
                        _ => Av1C.SmoothPred,
                    };
                    PredictIntra(plane, baseX, baseY, plane == 0 ? AvailL : AvailLChroma, plane == 0 ? AvailU : AvailUChroma,
                        BlockDecoded(plane, (subBlockMiRow >> sy) - 1, (subBlockMiCol >> sx) + num4x4W),
                        BlockDecoded(plane, (subBlockMiRow >> sy) + num4x4H, (subBlockMiCol >> sx) - 1), mode, log2W, log2H);
                }
                int predW = BlockWidth(MiSize) >> sx, predH = BlockHeight(MiSize) >> sy;
                bool someUseIntra = false;
                for (int r = 0; r < (num4x4H << sy) && !someUseIntra; r++)
                    for (int c = 0; c < (num4x4W << sx); c++)
                        if (Nb(candRow + r, candCol + c).RefFrame0 == Av1C.IntraFrame) { someUseIntra = true; break; }
                if (someUseIntra)
                {
                    predW = num4x4W * 4;
                    predH = num4x4H * 4;
                    candRow = MiRow;
                    candCol = MiCol;
                }
                int rr = 0;
                for (int y = 0; y < num4x4H * 4; y += predH)
                {
                    int cc = 0;
                    for (int x = 0; x < num4x4W * 4; x += predW)
                    {
                        PredictInter(plane, baseX + x, baseY + y, predW, predH, candRow + rr, candCol + cc);
                        cc++;
                    }
                    rr++;
                }
            }
        }

        void RoundingVariables(bool isCompound)
        {
            interRound0 = 3;
            interRound1 = isCompound ? 7 : 11;
            if (bitDepth == 12) interRound0 += 2;
            if (bitDepth == 12 && !isCompound) interRound1 -= 2;
            interPostRound = 2 * Av1C.FilterBits - (interRound0 + interRound1);
        }

        void PredictInter(int plane, int x, int y, int w, int hh, int candRow, int candCol)
        {
            ref var cand = ref Nb(candRow, candCol);
            bool isCompound = cand.RefFrame1 > Av1C.IntraFrame;
            RoundingVariables(isCompound);
            if (plane == 0 && motionMode == Av1C.LocalWarp)
            {
                WarpEstimation();
                if (localValid) localValid = SetupShear(localWarpParams, out _, out _, out _, out _);
            }
            int maxV = (1 << bitDepth) - 1;
            for (int refList = 0; refList < 1 + (isCompound ? 1 : 0); refList++)
            {
                int refFrame = cand.RefFrame(refList);
                bool globalValid = false;
                if ((YMode == Av1C.GlobalMv || YMode == Av1C.GlobalGlobalMv) && h.GmType[refFrame] > Av1C.Translation)
                    globalValid = SetupShear(GmRow(refFrame), out _, out _, out _, out _);
                int useWarp;
                if (w < 8 || hh < 8) useWarp = 0;
                else if (h.ForceIntegerMv) useWarp = 0;
                else if (motionMode == Av1C.LocalWarp && localValid) useWarp = 1;
                else if ((YMode == Av1C.GlobalMv || YMode == Av1C.GlobalGlobalMv) && h.GmType[refFrame] > Av1C.Translation
                         && !IsScaled(refFrame) && globalValid) useWarp = 2;
                else useWarp = 0;
                int mvRow = refList == 0 ? cand.Mv0Row : cand.Mv1Row;
                int mvCol = refList == 0 ? cand.Mv0Col : cand.Mv1Col;
                Av1Frame refPic;
                int refUpscaledWidth, refFrameWidth, refFrameHeight;
                if (!useIntrabc)
                {
                    var slot = fs.Dec.Ref[h.RefFrameIdx[refFrame - Av1C.LastFrame]]!;
                    refPic = slot.Frame;
                    refUpscaledWidth = slot.UpscaledWidth; refFrameWidth = slot.FrameWidth; refFrameHeight = slot.FrameHeight;
                }
                else
                {
                    refPic = fs.Cur;
                    refUpscaledWidth = h.UpscaledWidth; refFrameWidth = h.FrameWidth; refFrameHeight = h.FrameHeight;
                }
                MotionVectorScaling(plane, refUpscaledWidth, refFrameHeight, x, y, mvRow, mvCol, out int startX, out int startY, out int stepX, out int stepY);
                if (useIntrabc)
                {
                    refUpscaledWidth = fs.MiCols * Av1C.MiSize;
                    refFrameHeight = fs.MiRows * Av1C.MiSize;
                }
                if (useWarp != 0)
                {
                    var warpParams = useWarp == 1 ? localWarpParams : GmRow(refFrame);
                    for (int i8 = 0; i8 <= (hh - 1) >> 3; i8++)
                        for (int j8 = 0; j8 <= (w - 1) >> 3; j8++)
                            BlockWarp(warpParams, refPic, refUpscaledWidth, refFrameHeight, plane, x, y, i8, j8, w, hh, preds[refList]);
                }
                else
                {
                    int f0 = cand.InterpFilter0, f1 = cand.InterpFilter1;
                    BlockInterPrediction(refPic, refUpscaledWidth, refFrameHeight, plane, startX, startY, stepX, stepY, w, hh, f0, f1, preds[refList], w);
                }
            }
            bool isInterIntra = RefFrame[1] == Av1C.IntraFrame;   // the current block's (spec IsInterIntra)
            if (compoundType == Av1C.CompoundWedge && plane == 0) WedgeMask(w, hh);
            else if (compoundType == Av1C.CompoundIntra) IntraModeVariantMask(w, hh);
            else if (compoundType == Av1C.CompoundDiffwtd && plane == 0) DifferenceWeightMask(w, hh);
            if (compoundType == Av1C.CompoundDistance) DistanceWeights(ref cand);
            var cur = fs.Cur.Planes[plane];
            int stride = fs.Cur.Stride[plane];
            var p0 = preds[0];
            var p1 = preds[1];
            if (!isCompound && !isInterIntra)
            {
                for (int i = 0; i < hh; i++)
                {
                    int o = (y + i) * stride + x;
                    for (int j = 0; j < w; j++) cur[o + j] = (ushort)Math.Clamp(p0[i * w + j], 0, maxV);
                }
            }
            else if (compoundType == Av1C.CompoundAverage)
            {
                int sh = 1 + interPostRound;
                for (int i = 0; i < hh; i++)
                {
                    int o = (y + i) * stride + x;
                    for (int j = 0; j < w; j++)
                        cur[o + j] = (ushort)Math.Clamp((p0[i * w + j] + p1[i * w + j] + (1 << (sh - 1))) >> sh, 0, maxV);
                }
            }
            else if (compoundType == Av1C.CompoundDistance)
            {
                int sh = 4 + interPostRound;
                for (int i = 0; i < hh; i++)
                {
                    int o = (y + i) * stride + x;
                    for (int j = 0; j < w; j++)
                        cur[o + j] = (ushort)Math.Clamp((fwdWeight * p0[i * w + j] + bckWeight * p1[i * w + j] + (1 << (sh - 1))) >> sh, 0, maxV);
                }
            }
            else MaskBlend(plane, x, y, w, hh);
            if (motionMode == Av1C.Obmc) OverlappedMotionCompensation(plane, w, hh);
        }

        readonly int[] gmRowTmp = new int[6];

        int[] GmRow(int refFrame)
        {
            for (int i = 0; i < 6; i++) gmRowTmp[i] = h.GmParams[refFrame, i];
            return gmRowTmp;
        }

        void MotionVectorScaling(int plane, int refUpscaledWidth, int refFrameHeight, int x, int y, int mvRow, int mvCol,
            out int startX, out int startY, out int stepX, out int stepY)
        {
            int xScale = ((refUpscaledWidth << Av1C.RefScaleShift) + h.FrameWidth / 2) / h.FrameWidth;
            int yScale = ((refFrameHeight << Av1C.RefScaleShift) + h.FrameHeight / 2) / h.FrameHeight;
            int sx = plane == 0 ? 0 : subX, sy = plane == 0 ? 0 : subY;
            const int halfSample = 1 << (Av1C.SubpelBits - 1);
            int origX = (x << Av1C.SubpelBits) + ((2 * mvCol) >> sx) + halfSample;
            int origY = (y << Av1C.SubpelBits) + ((2 * mvRow) >> sy) + halfSample;
            long baseX = (long)origX * xScale - (halfSample << Av1C.RefScaleShift);
            long baseY = (long)origY * yScale - (halfSample << Av1C.RefScaleShift);
            const int off = (1 << (Av1C.ScaleSubpelBits - Av1C.SubpelBits)) / 2;
            startX = (int)(Round2Signed(baseX, Av1C.RefScaleShift + Av1C.SubpelBits - Av1C.ScaleSubpelBits) + off);
            startY = (int)(Round2Signed(baseY, Av1C.RefScaleShift + Av1C.SubpelBits - Av1C.ScaleSubpelBits) + off);
            stepX = (int)Round2Signed((long)xScale, Av1C.RefScaleShift - Av1C.ScaleSubpelBits);
            stepY = (int)Round2Signed((long)yScale, Av1C.RefScaleShift - Av1C.ScaleSubpelBits);
        }

        void BlockInterPrediction(Av1Frame refPic, int refUpscaledWidth, int refFrameHeight, int plane, int x, int y, int xStep, int yStep,
            int w, int hh, int filter0, int filter1, int[] outp, int outStride)
        {
            var refPlane = refPic.Planes[plane];
            int refStride = refPic.Stride[plane];
            int sx = plane == 0 ? 0 : subX, sy = plane == 0 ? 0 : subY;
            int lastX = ((refUpscaledWidth + sx) >> sx) - 1;
            int lastY = ((refFrameHeight + sy) >> sy) - 1;
            int intermediateHeight = (((hh - 1) * yStep + (1 << Av1C.ScaleSubpelBits) - 1) >> Av1C.ScaleSubpelBits) + 8;
            int interpFilter = filter1;
            if (w <= 4)
            {
                if (interpFilter == Av1C.EightTap || interpFilter == Av1C.EightTapSharp) interpFilter = 4;
                else if (interpFilter == Av1C.EightTapSmooth) interpFilter = 5;
            }
            var filters = T.SubpelFilters;
            var im = intermediate;
            int r0 = interRound0;
            for (int r = 0; r < intermediateHeight; r++)
            {
                int ry = Math.Clamp((y >> 10) + r - 3, 0, lastY) * refStride;
                for (int c = 0; c < w; c++)
                {
                    int s = 0;
                    int p = x + xStep * c;
                    int fo = (interpFilter * 16 + ((p >> 6) & Av1C.SubpelMask)) * 8;
                    int px = (p >> 10) - 3;
                    for (int t = 0; t < 8; t++) s += filters[fo + t] * refPlane[ry + Math.Clamp(px + t, 0, lastX)];
                    im[r * w + c] = (s + (1 << (r0 - 1))) >> r0;
                }
            }
            interpFilter = filter0;
            if (hh <= 4)
            {
                if (interpFilter == Av1C.EightTap || interpFilter == Av1C.EightTapSharp) interpFilter = 4;
                else if (interpFilter == Av1C.EightTapSmooth) interpFilter = 5;
            }
            int r1 = interRound1;
            for (int r = 0; r < hh; r++)
            {
                int p = (y & 1023) + yStep * r;
                int fo = (interpFilter * 16 + ((p >> 6) & Av1C.SubpelMask)) * 8;
                int baseRow = p >> 10;
                for (int c = 0; c < w; c++)
                {
                    int s = 0;
                    for (int t = 0; t < 8; t++) s += filters[fo + t] * im[(baseRow + t) * w + c];
                    outp[r * outStride + c] = (s + (1 << (r1 - 1))) >> r1;
                }
            }
        }

        void BlockWarp(int[] warpParams, Av1Frame refPic, int refUpscaledWidth, int refFrameHeight, int plane, int x, int y, int i8, int j8,
            int w, int hh, int[] outp)
        {
            var refPlane = refPic.Planes[plane];
            int refStride = refPic.Stride[plane];
            int sx = plane == 0 ? 0 : subX, sy = plane == 0 ? 0 : subY;
            int lastX = ((refUpscaledWidth + sx) >> sx) - 1;
            int lastY = ((refFrameHeight + sy) >> sy) - 1;
            int srcX = (x + j8 * 8 + 4) << sx;
            int srcY = (y + i8 * 8 + 4) << sy;
            long dstX = (long)warpParams[2] * srcX + (long)warpParams[3] * srcY + warpParams[0];
            long dstY = (long)warpParams[4] * srcX + (long)warpParams[5] * srcY + warpParams[1];
            SetupShear(warpParams, out int alpha, out int beta, out int gamma, out int delta);
            long x4 = dstX >> sx;
            long y4 = dstY >> sy;
            int ix4 = (int)(x4 >> Av1C.WarpedModelPrecBits);
            int sx4 = (int)(x4 & ((1 << Av1C.WarpedModelPrecBits) - 1));
            int iy4 = (int)(y4 >> Av1C.WarpedModelPrecBits);
            int sy4 = (int)(y4 & ((1 << Av1C.WarpedModelPrecBits) - 1));
            var wf = T.WarpedFilters;
            Span<int> inter = stackalloc int[15 * 8];
            int r0 = interRound0, r1 = interRound1;
            for (int i1 = -7; i1 < 8; i1++)
            {
                int row = Math.Clamp(iy4 + i1, 0, lastY) * refStride;
                for (int i2 = -4; i2 < 4; i2++)
                {
                    int sxv = sx4 + alpha * i2 + beta * i1;
                    int offs = ((sxv + (1 << (Av1C.WarpedDiffPrecBits - 1))) >> Av1C.WarpedDiffPrecBits) + Av1C.WarpedPixelPrecShifts;
                    int s = 0;
                    for (int i3 = 0; i3 < 8; i3++) s += wf[offs * 8 + i3] * refPlane[row + Math.Clamp(ix4 + i2 - 3 + i3, 0, lastX)];
                    inter[(i1 + 7) * 8 + i2 + 4] = (s + (1 << (r0 - 1))) >> r0;
                }
            }
            for (int i1 = -4; i1 < Math.Min(4, hh - i8 * 8 - 4); i1++)
                for (int i2 = -4; i2 < Math.Min(4, w - j8 * 8 - 4); i2++)
                {
                    int syv = sy4 + gamma * i2 + delta * i1;
                    int offs = ((syv + (1 << (Av1C.WarpedDiffPrecBits - 1))) >> Av1C.WarpedDiffPrecBits) + Av1C.WarpedPixelPrecShifts;
                    int s = 0;
                    for (int i3 = 0; i3 < 8; i3++) s += wf[offs * 8 + i3] * inter[(i1 + i3 + 4) * 8 + i2 + 4];
                    outp[(i8 * 8 + i1 + 4) * w + j8 * 8 + i2 + 4] = (s + (1 << (r1 - 1))) >> r1;
                }
        }

        static bool SetupShear(int[] warpParams, out int alpha, out int beta, out int gamma, out int delta)
        {
            int alpha0 = Math.Clamp(warpParams[2] - (1 << Av1C.WarpedModelPrecBits), -32768, 32767);
            int beta0 = Math.Clamp(warpParams[3], -32768, 32767);
            ResolveDivisor(warpParams[2], out int divShift, out int divFactor);
            long v = (long)warpParams[4] << Av1C.WarpedModelPrecBits;
            int gamma0 = (int)Math.Clamp(Round2Signed(v * divFactor, divShift), -32768, 32767);
            long w = (long)warpParams[3] * warpParams[4];
            int delta0 = (int)Math.Clamp(warpParams[5] - Round2Signed(w * divFactor, divShift) - (1 << Av1C.WarpedModelPrecBits), -32768, 32767);
            alpha = Round2Signed(alpha0, Av1C.WarpParamReduceBits) << Av1C.WarpParamReduceBits;
            beta = Round2Signed(beta0, Av1C.WarpParamReduceBits) << Av1C.WarpParamReduceBits;
            gamma = Round2Signed(gamma0, Av1C.WarpParamReduceBits) << Av1C.WarpParamReduceBits;
            delta = Round2Signed(delta0, Av1C.WarpParamReduceBits) << Av1C.WarpParamReduceBits;
            if (4 * Math.Abs(alpha) + 7 * Math.Abs(beta) >= 1 << Av1C.WarpedModelPrecBits) return false;
            if (4 * Math.Abs(gamma) + 4 * Math.Abs(delta) >= 1 << Av1C.WarpedModelPrecBits) return false;
            return true;
        }

        static void ResolveDivisor(long d, out int divShift, out int divFactor)
        {
            long ad = Math.Abs(d);
            int n = 63 - System.Numerics.BitOperations.LeadingZeroCount((ulong)ad);
            long e = ad - (1L << n);
            long f;
            if (n > Av1C.DivLutBits) f = (e + (1L << (n - Av1C.DivLutBits - 1))) >> (n - Av1C.DivLutBits);
            else f = e << (Av1C.DivLutBits - n);
            divShift = n + Av1C.DivLutPrecBits;
            divFactor = d < 0 ? -T.DivLut[(int)f] : T.DivLut[(int)f];
        }

        void WarpEstimation()
        {
            long a00 = 0, a01 = 0, a11 = 0, bx0 = 0, bx1 = 0, by0 = 0, by1 = 0;
            int midY = MiRow * 4 + bh4 * 2 - 1;
            int midX = MiCol * 4 + bw4 * 2 - 1;
            int suy = midY * 8, sux = midX * 8;
            int duy = suy + MvRow[0], dux = sux + MvCol[0];
            static long Ls(long a, long b) => ((a * b) >> 2) + (a + b);
            for (int i = 0; i < numSamples; i++)
            {
                int sy = candList[i * 4] - suy, sx = candList[i * 4 + 1] - sux;
                int dy = candList[i * 4 + 2] - duy, dx = candList[i * 4 + 3] - dux;
                if (Math.Abs(sx - dx) < Av1C.LsMvMax && Math.Abs(sy - dy) < Av1C.LsMvMax)
                {
                    a00 += Ls(sx, sx) + 8;
                    a01 += Ls(sx, sy) + 4;
                    a11 += Ls(sy, sy) + 8;
                    bx0 += Ls(sx, dx) + 8;
                    bx1 += Ls(sy, dx) + 4;
                    by0 += Ls(sx, dy) + 4;
                    by1 += Ls(sy, dy) + 8;
                }
            }
            long det = a00 * a11 - a01 * a01;
            localValid = det != 0;
            if (!localValid) return;
            ResolveDivisor(det, out int divShift, out int divFactorI);
            long divFactor = divFactorI;
            divShift -= Av1C.WarpedModelPrecBits;
            if (divShift < 0)
            {
                divFactor <<= -divShift;
                divShift = 0;
            }
            long Nondiag(long v) => Math.Clamp(Round2SignedBig(v, divFactor, divShift), -Av1C.WarpedModelNonDiagAffineClamp + 1, Av1C.WarpedModelNonDiagAffineClamp - 1);
            long Diag(long v) => Math.Clamp(Round2SignedBig(v, divFactor, divShift),
                (1 << Av1C.WarpedModelPrecBits) - Av1C.WarpedModelNonDiagAffineClamp + 1, (1 << Av1C.WarpedModelPrecBits) + Av1C.WarpedModelNonDiagAffineClamp - 1);
            localWarpParams[2] = (int)Diag(a11 * bx0 - a01 * bx1);
            localWarpParams[3] = (int)Nondiag(-a01 * bx0 + a00 * bx1);
            localWarpParams[4] = (int)Nondiag(a11 * by0 - a01 * by1);
            localWarpParams[5] = (int)Diag(-a01 * by0 + a00 * by1);
            long vx = (long)MvCol[0] * (1 << (Av1C.WarpedModelPrecBits - 3)) - ((long)midX * (localWarpParams[2] - (1 << Av1C.WarpedModelPrecBits)) + (long)midY * localWarpParams[3]);
            long vy = (long)MvRow[0] * (1 << (Av1C.WarpedModelPrecBits - 3)) - ((long)midX * localWarpParams[4] + (long)midY * (localWarpParams[5] - (1 << Av1C.WarpedModelPrecBits)));
            localWarpParams[0] = (int)Math.Clamp(vx, -Av1C.WarpedModelTransClamp, Av1C.WarpedModelTransClamp - 1);
            localWarpParams[1] = (int)Math.Clamp(vy, -Av1C.WarpedModelTransClamp, Av1C.WarpedModelTransClamp - 1);
        }

        /// <summary>Round2Signed( v * f, n ) where the product can exceed 64 bits.</summary>
        static long Round2SignedBig(long v, long f, int n)
        {
            var p = (System.Int128)v * f;
            bool neg = p < 0;
            if (neg) p = -p;
            if (n > 0) p = (p + ((System.Int128)1 << (n - 1))) >> n;
            if (p > long.MaxValue) p = long.MaxValue;
            return neg ? -(long)p : (long)p;
        }

        // ------------------------------------------------------------------ OBMC (7.11.3.10, 7.11.3.11)

        void OverlappedMotionCompensation(int plane, int w, int hh)
        {
            int sx = plane == 0 ? 0 : subX, sy = plane == 0 ? 0 : subY;
            if (AvailU)
            {
                if (PlaneResidualSize(MiSize, plane) >= Av1C.Block8x8)
                {
                    int x4 = MiCol, y4 = MiRow;
                    int nCount = 0;
                    int nLimit = Math.Min(4, (int)T.MiWidthLog2[MiSize]);
                    while (nCount < nLimit && x4 < Math.Min(fs.MiCols, MiCol + bw4))
                    {
                        int candRow = MiRow - 1, candCol = x4 | 1;
                        ref var cb = ref Nb(candRow, candCol);
                        int step4 = Math.Clamp((int)T.Num4x4BlocksWide[cb.MiSize], 2, 16);
                        if (cb.RefFrame0 > Av1C.IntraFrame)
                        {
                            nCount++;
                            int predW = Math.Min(w, (step4 * Av1C.MiSize) >> sx);
                            int predH = Math.Min(hh >> 1, 32 >> sy);
                            PredictOverlap(ref cb, plane, x4, y4, predW, predH, 0, predH);
                        }
                        x4 += step4;
                    }
                }
            }
            if (AvailL)
            {
                int x4 = MiCol, y4 = MiRow;
                int nCount = 0;
                int nLimit = Math.Min(4, (int)T.MiHeightLog2[MiSize]);
                while (nCount < nLimit && y4 < Math.Min(fs.MiRows, MiRow + bh4))
                {
                    int candCol = MiCol - 1, candRow = y4 | 1;
                    ref var cb = ref Nb(candRow, candCol);
                    int step4 = Math.Clamp((int)T.Num4x4BlocksHigh[cb.MiSize], 2, 16);
                    if (cb.RefFrame0 > Av1C.IntraFrame)
                    {
                        nCount++;
                        int predW = Math.Min(w >> 1, 32 >> sx);
                        int predH = Math.Min(hh, (step4 * Av1C.MiSize) >> sy);
                        PredictOverlap(ref cb, plane, x4, y4, predW, predH, 1, predW);
                    }
                    y4 += step4;
                }
            }
        }

        static ReadOnlySpan<byte> ObmcMask(int length) => length switch
        {
            2 => T.ObmcMask2,
            4 => T.ObmcMask4,
            8 => T.ObmcMask8,
            16 => T.ObmcMask16,
            _ => T.ObmcMask32,
        };

        void PredictOverlap(ref Av1Block cb, int plane, int x4, int y4, int predW, int predH, int pass, int maskLen)
        {
            var m = ObmcMask(maskLen);
            var slot = fs.Dec.Ref[h.RefFrameIdx[cb.RefFrame0 - Av1C.LastFrame]]!;
            int sx = plane == 0 ? 0 : subX, sy = plane == 0 ? 0 : subY;
            int predX = (x4 * 4) >> sx, predY = (y4 * 4) >> sy;
            MotionVectorScaling(plane, slot.UpscaledWidth, slot.FrameHeight, predX, predY, cb.Mv0Row, cb.Mv0Col,
                out int startX, out int startY, out int stepX, out int stepY);
            BlockInterPrediction(slot.Frame, slot.UpscaledWidth, slot.FrameHeight, plane, startX, startY, stepX, stepY, predW, predH,
                cb.InterpFilter0, cb.InterpFilter1, obmcPred, predW);
            int maxV = (1 << bitDepth) - 1;
            var cur = fs.Cur.Planes[plane];
            int stride = fs.Cur.Stride[plane];
            for (int i = 0; i < predH; i++)
                for (int j = 0; j < predW; j++)
                {
                    int op = Math.Clamp(obmcPred[i * predW + j], 0, maxV);
                    int mm = pass == 0 ? m[i] : m[j];
                    ref ushort px = ref cur[(predY + i) * stride + predX + j];
                    px = (ushort)((mm * px + (64 - mm) * op + 32) >> 6);
                }
        }

        // ------------------------------------------------------------------ masks (7.11.3.12 - 7.11.3.15)

        static byte[]? wedgeMasks;   // [bsize][flipSign][wedge][h][w], packed per block size
        static int[]? wedgeMaskOffsets;

        static void InitWedgeMasks()
        {
            if (wedgeMasks != null) return;
            const int M = Av1C.MaskMasterSize;
            var master = new byte[6 * M * M];
            int Mi(int dir, int i, int j) => (dir * M + i) * M + j;
            for (int j = 0; j < M; j++)
            {
                int shift = M / 4;
                for (int i = 0; i < M; i += 2)
                {
                    master[Mi(3, i, j)] = T.WedgeMasterObliqueEven[Math.Clamp(j - shift, 0, M - 1)];
                    shift -= 1;
                    master[Mi(3, i + 1, j)] = T.WedgeMasterObliqueOdd[Math.Clamp(j - shift, 0, M - 1)];
                    master[Mi(1, i, j)] = T.WedgeMasterVertical[j];
                    master[Mi(1, i + 1, j)] = T.WedgeMasterVertical[j];
                }
            }
            for (int i = 0; i < M; i++)
                for (int j = 0; j < M; j++)
                {
                    int msk = master[Mi(3, i, j)];
                    master[Mi(2, j, i)] = (byte)msk;
                    master[Mi(4, i, M - 1 - j)] = (byte)(64 - msk);
                    master[Mi(5, M - 1 - j, i)] = (byte)(64 - msk);
                    master[Mi(0, j, i)] = master[Mi(1, i, j)];
                }
            var offsets = new int[Av1C.BlockSizes];
            int total = 0;
            for (int bs = 0; bs < Av1C.BlockSizes; bs++)
            {
                offsets[bs] = total;
                if (T.WedgeBits[bs] > 0) total += 2 * 16 * BlockWidth(bs) * BlockHeight(bs);
            }
            var masks = new byte[total];
            for (int bsize = Av1C.Block8x8; bsize < Av1C.BlockSizes; bsize++)
            {
                if (T.WedgeBits[bsize] == 0) continue;
                int w = BlockWidth(bsize), hh = BlockHeight(bsize);
                int w4 = T.Num4x4BlocksWide[bsize], h4 = T.Num4x4BlocksHigh[bsize];
                int shape = h4 > w4 ? 0 : h4 < w4 ? 1 : 2;
                for (int wedge = 0; wedge < 16; wedge++)
                {
                    int cb = (shape * 16 + wedge) * 3;
                    int dir = T.WedgeCodebook[cb];
                    int xoff = M / 2 - ((T.WedgeCodebook[cb + 1] * w) >> 3);
                    int yoff = M / 2 - ((T.WedgeCodebook[cb + 2] * hh) >> 3);
                    int sum = 0;
                    for (int i = 0; i < w; i++) sum += master[Mi(dir, yoff, xoff + i)];
                    for (int i = 1; i < hh; i++) sum += master[Mi(dir, yoff + i, xoff)];
                    int avg = (sum + (w + hh - 1) / 2) / (w + hh - 1);
                    int flipSign = avg < 32 ? 1 : 0;
                    for (int i = 0; i < hh; i++)
                        for (int j = 0; j < w; j++)
                        {
                            int v = master[Mi(dir, yoff + i, xoff + j)];
                            masks[offsets[bsize] + ((flipSign * 16 + wedge) * hh + i) * w + j] = (byte)v;
                            masks[offsets[bsize] + (((1 - flipSign) * 16 + wedge) * hh + i) * w + j] = (byte)(64 - v);
                        }
                }
            }
            wedgeMaskOffsets = offsets;
            wedgeMasks = masks;
        }

        void WedgeMask(int w, int hh)
        {
            InitWedgeMasks();
            int bw = BlockWidth(MiSize), bh = BlockHeight(MiSize);
            int o = wedgeMaskOffsets![MiSize] + ((wedgeSign * 16 + wedgeIndex) * bh) * bw;
            for (int i = 0; i < hh; i++)
                for (int j = 0; j < w; j++) mask[i * w + j] = wedgeMasks![o + i * bw + j];
            maskStride = w;
        }

        int maskStride;

        void IntraModeVariantMask(int w, int hh)
        {
            int sizeScale = Av1C.MaxSbSize / Math.Max(hh, w);
            var ii = T.IiWeights1d;
            for (int i = 0; i < hh; i++)
                for (int j = 0; j < w; j++)
                {
                    int v;
                    if (interintraMode == Av1C.IiVPred) v = ii[i * sizeScale];
                    else if (interintraMode == Av1C.IiHPred) v = ii[j * sizeScale];
                    else if (interintraMode == Av1C.IiSmoothPred) v = ii[Math.Min(i, j) * sizeScale];
                    else v = 32;
                    mask[i * w + j] = (byte)v;
                }
            maskStride = w;
        }

        void DifferenceWeightMask(int w, int hh)
        {
            var p0 = preds[0];
            var p1 = preds[1];
            int sh = (bitDepth - 8) + interPostRound;
            for (int i = 0; i < hh; i++)
                for (int j = 0; j < w; j++)
                {
                    int diff = Math.Abs(p0[i * w + j] - p1[i * w + j]);
                    diff = sh == 0 ? diff : (diff + (1 << (sh - 1))) >> sh;
                    int m = Math.Clamp(38 + diff / 16, 0, 64);
                    mask[i * w + j] = (byte)(maskType != 0 ? 64 - m : m);
                }
            maskStride = w;
        }

        void MaskBlend(int plane, int dstX, int dstY, int w, int hh)
        {
            int sx = plane == 0 ? 0 : subX, sy = plane == 0 ? 0 : subY;
            var cur = fs.Cur.Planes[plane];
            int stride = fs.Cur.Stride[plane];
            int maxV = (1 << bitDepth) - 1;
            var p0 = preds[0];
            var p1 = preds[1];
            int ms = maskStride;
            for (int y = 0; y < hh; y++)
                for (int x = 0; x < w; x++)
                {
                    int m;
                    if (sx == 0 && sy == 0 || interintra != 0 && wedgeInterintra == 0) m = mask[y * ms + x];
                    else if (sx != 0 && sy == 0) m = (mask[y * ms + 2 * x] + mask[y * ms + 2 * x + 1] + 1) >> 1;
                    else m = (mask[2 * y * ms + 2 * x] + mask[2 * y * ms + 2 * x + 1] + mask[(2 * y + 1) * ms + 2 * x] + mask[(2 * y + 1) * ms + 2 * x + 1] + 2) >> 2;
                    ref ushort px = ref cur[(y + dstY) * stride + x + dstX];
                    if (interintra != 0)
                    {
                        int pred0 = Math.Clamp(interPostRound == 0 ? p0[y * w + x] : (p0[y * w + x] + (1 << (interPostRound - 1))) >> interPostRound, 0, maxV);
                        int pred1 = px;
                        px = (ushort)((m * pred1 + (64 - m) * pred0 + 32) >> 6);
                    }
                    else
                    {
                        int sh = 6 + interPostRound;
                        px = (ushort)Math.Clamp((m * p0[y * w + x] + (64 - m) * p1[y * w + x] + (1 << (sh - 1))) >> sh, 0, maxV);
                    }
                }
        }

        void DistanceWeights(ref Av1Block cand)
        {
            Span<int> dist = stackalloc int[2];
            for (int refList = 0; refList < 2; refList++)
            {
                int hh = h.OrderHints[cand.RefFrame(refList)];
                dist[refList] = Math.Clamp(Math.Abs(fs.Dec.RelativeDist(hh, h.OrderHint)), 0, Av1C.MaxFrameDistance);
            }
            int d0 = dist[1], d1 = dist[0];
            int order = d0 <= d1 ? 1 : 0;
            if (d0 == 0 || d1 == 0)
            {
                fwdWeight = T.QuantDistLookup[3 * 2 + order];
                bckWeight = T.QuantDistLookup[3 * 2 + 1 - order];
                return;
            }
            int i;
            for (i = 0; i < 3; i++)
            {
                int c0 = T.QuantDistWeight[i * 2 + order], c1 = T.QuantDistWeight[i * 2 + 1 - order];
                if (order != 0) { if (d0 * c0 > d1 * c1) break; }
                else { if (d0 * c0 < d1 * c1) break; }
            }
            fwdWeight = T.QuantDistLookup[i * 2 + order];
            bckWeight = T.QuantDistLookup[i * 2 + 1 - order];
        }
    }
}
