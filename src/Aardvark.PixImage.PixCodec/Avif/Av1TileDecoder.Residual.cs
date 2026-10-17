using System;
using System.Runtime.CompilerServices;
using T = Aardvark.ImageCodecs.Av1.Av1Tables;

namespace Aardvark.ImageCodecs.Av1
{
    internal sealed partial class Av1TileDecoder
    {
        int[] quant = null!;
        int[] dequant = null!;
        int[] residualBuf = null!;
        Av1Transform transform = null!;
        int planeTxType;

        void InitScratch()
        {
            quant = new int[1024];
            dequant = new int[1024];
            residualBuf = new int[64 * 64];
            transform = new Av1Transform();
            InitIntraScratch();
        }

        // ------------------------------------------------------------------ residual (5.11.34 - 5.11.38)

        void Residual()
        {
            int widthChunks = Math.Max(1, BlockWidth(MiSize) >> 6);
            int heightChunks = Math.Max(1, BlockHeight(MiSize) >> 6);
            int miSizeChunk = widthChunks > 1 || heightChunks > 1 ? Av1C.Block64x64 : MiSize;
            for (int chunkY = 0; chunkY < heightChunks; chunkY++)
                for (int chunkX = 0; chunkX < widthChunks; chunkX++)
                {
                    int miRowChunk = MiRow + (chunkY << 4);
                    int miColChunk = MiCol + (chunkX << 4);
                    for (int plane = 0; plane < 1 + (HasChroma ? 2 : 0); plane++)
                    {
                        int txSz = lossless ? Av1C.Tx4x4 : GetTxSize(plane, TxSize);
                        int stepX = T.TxWidth[txSz] >> 2, stepY = T.TxHeight[txSz] >> 2;
                        int planeSz = PlaneResidualSize(miSizeChunk, plane);
                        int num4x4W = T.Num4x4BlocksWide[planeSz], num4x4H = T.Num4x4BlocksHigh[planeSz];
                        int sx = plane > 0 ? subX : 0, sy = plane > 0 ? subY : 0;
                        int baseX = (miColChunk >> sx) * Av1C.MiSize;
                        int baseY = (miRowChunk >> sy) * Av1C.MiSize;
                        if (isInter && !lossless && plane == 0)
                            TransformTree(baseX, baseY, num4x4W * 4, num4x4H * 4);
                        else
                        {
                            int baseXBlock = (MiCol >> sx) * Av1C.MiSize;
                            int baseYBlock = (MiRow >> sy) * Av1C.MiSize;
                            for (int y = 0; y < num4x4H; y += stepY)
                                for (int x = 0; x < num4x4W; x += stepX)
                                    TransformBlock(plane, baseXBlock, baseYBlock, txSz, x + ((chunkX << 4) >> sx), y + ((chunkY << 4) >> sy));
                        }
                    }
                }
        }

        int GetTxSize(int plane, int txSz)
        {
            if (plane == 0) return txSz;
            int uvTx = T.MaxTxSizeRect[PlaneResidualSize(MiSize, plane)];
            if (T.TxWidth[uvTx] == 64 || T.TxHeight[uvTx] == 64)
            {
                if (T.TxWidth[uvTx] == 16) return Av1C.Tx16x32;
                if (T.TxHeight[uvTx] == 16) return Av1C.Tx32x16;
                return Av1C.Tx32x32;
            }
            return uvTx;
        }

        void TransformTree(int startX, int startY, int w, int hh)
        {
            int maxX = fs.MiCols * Av1C.MiSize, maxY = fs.MiRows * Av1C.MiSize;
            if (startX >= maxX || startY >= maxY) return;
            int row = startY >> Av1C.MiSizeLog2, col = startX >> Av1C.MiSizeLog2;
            int lumaTxSz = fs.InterTxSizes[row * fs.MiStride + col];
            int lumaW = T.TxWidth[lumaTxSz], lumaH = T.TxHeight[lumaTxSz];
            if (w <= lumaW && hh <= lumaH)
            {
                int txSz = FindTxSize(w, hh);
                TransformBlock(0, startX, startY, txSz, 0, 0);
            }
            else if (w > hh)
            {
                TransformTree(startX, startY, w / 2, hh);
                TransformTree(startX + w / 2, startY, w / 2, hh);
            }
            else if (w < hh)
            {
                TransformTree(startX, startY, w, hh / 2);
                TransformTree(startX, startY + hh / 2, w, hh / 2);
            }
            else
            {
                TransformTree(startX, startY, w / 2, hh / 2);
                TransformTree(startX + w / 2, startY, w / 2, hh / 2);
                TransformTree(startX, startY + hh / 2, w / 2, hh / 2);
                TransformTree(startX + w / 2, startY + hh / 2, w / 2, hh / 2);
            }
        }

        void TransformBlock(int plane, int baseX, int baseY, int txSz, int x, int y)
        {
            int startX = baseX + 4 * x, startY = baseY + 4 * y;
            int sx = plane > 0 ? subX : 0, sy = plane > 0 ? subY : 0;
            int row = (startY << sy) >> Av1C.MiSizeLog2, col = (startX << sx) >> Av1C.MiSizeLog2;
            int subBlockMiRow = row & sbMask, subBlockMiCol = col & sbMask;
            int stepX = T.TxWidth[txSz] >> Av1C.MiSizeLog2, stepY = T.TxHeight[txSz] >> Av1C.MiSizeLog2;
            int maxX = (fs.MiCols * Av1C.MiSize) >> sx, maxY = (fs.MiRows * Av1C.MiSize) >> sy;
            if (startX >= maxX || startY >= maxY) return;
            if (!isInter)
            {
                if (plane == 0 && PaletteSizeY != 0 || plane != 0 && PaletteSizeUV != 0)
                    PredictPalette(plane, startX, startY, x, y, txSz);
                else
                {
                    bool isCfl = plane > 0 && UVMode == Av1C.UvCflPred;
                    int mode = plane == 0 ? YMode : isCfl ? Av1C.DcPred : UVMode;
                    int log2W = T.TxWidthLog2[txSz], log2H = T.TxHeightLog2[txSz];
                    bool haveLeft = (plane == 0 ? AvailL : AvailLChroma) || x > 0;
                    bool haveAbove = (plane == 0 ? AvailU : AvailUChroma) || y > 0;
                    bool haveAboveRight = BlockDecoded(plane, (subBlockMiRow >> sy) - 1, (subBlockMiCol >> sx) + stepX);
                    bool haveBelowLeft = BlockDecoded(plane, (subBlockMiRow >> sy) + stepY, (subBlockMiCol >> sx) - 1);
                    PredictIntra(plane, startX, startY, haveLeft, haveAbove, haveAboveRight, haveBelowLeft, mode, log2W, log2H);
                    if (isCfl) PredictChromaFromLuma(plane, startX, startY, txSz);
                }
                if (plane == 0)
                {
                    MaxLumaW = startX + stepX * 4;
                    MaxLumaH = startY + stepY * 4;
                }
            }
            if (!skip)
            {
                int eob = Coeffs(plane, startX, startY, txSz);
                if (eob > 0) Reconstruct(plane, startX, startY, txSz);
            }
            var lf = fs.LfTxSizes[plane];
            int r0 = row >> sy, c0 = col >> sx;
            int rEnd = Math.Min(r0 + stepY, fs.MiRows), cEnd = Math.Min(c0 + stepX, fs.MiCols);
            for (int i = r0; i < rEnd; i++)
                lf.AsSpan(i * fs.MiStride + c0, cEnd - c0).Fill((byte)txSz);
            for (int i = 0; i < stepY; i++)
                for (int j = 0; j < stepX; j++)
                    BlockDecoded(plane, (subBlockMiRow >> sy) + i, (subBlockMiCol >> sx) + j) = true;
        }

        // ------------------------------------------------------------------ transform type (5.11.40, 5.11.47, 5.11.48)

        int GetTxSet(int txSz)
        {
            int txSzSqr = T.TxSizeSqr[txSz], txSzSqrUp = T.TxSizeSqrUp[txSz];
            if (txSzSqrUp > Av1C.Tx32x32) return Av1C.TxSetDctOnly;
            if (isInter)
            {
                if (h.ReducedTxSet || txSzSqrUp == Av1C.Tx32x32) return Av1C.TxSetInter3;
                if (txSzSqr == Av1C.Tx16x16) return Av1C.TxSetInter2;
                return Av1C.TxSetInter1;
            }
            if (txSzSqrUp == Av1C.Tx32x32) return Av1C.TxSetDctOnly;
            if (h.ReducedTxSet) return Av1C.TxSetIntra2;
            if (txSzSqr == Av1C.Tx16x16) return Av1C.TxSetIntra2;
            return Av1C.TxSetIntra1;
        }

        bool IsTxTypeInSet(int txSet, int txType)
            => isInter ? T.TxTypeInSetInter[txSet * 16 + txType] != 0 : T.TxTypeInSetIntra[txSet * 16 + txType] != 0;

        void TransformType(int x4, int y4, int txSz)
        {
            int set = GetTxSet(txSz);
            int txType;
            int q = h.SegmentationEnabled ? h.QIndex(true, segmentId, CurrentQIndex) : h.BaseQIdx;
            if (set > 0 && q > 0)
            {
                int sqr = T.TxSizeSqr[txSz];
                if (isInter)
                {
                    if (set == Av1C.TxSetInter1) txType = T.TxTypeInterInvSet1[S(Av1Cdf.InterTxTypeSet1 + sqr * 17, 16)];
                    else if (set == Av1C.TxSetInter2) txType = T.TxTypeInterInvSet2[S(Av1Cdf.InterTxTypeSet2, 12)];
                    else txType = T.TxTypeInterInvSet3[S(Av1Cdf.InterTxTypeSet3 + sqr * 3, 2)];
                }
                else
                {
                    int intraDir = useFilterIntra ? T.FilterIntraModeToIntraDir[filterIntraMode] : YMode;
                    if (set == Av1C.TxSetIntra1) txType = T.TxTypeIntraInvSet1[S(Av1Cdf.IntraTxTypeSet1 + (sqr * 13 + intraDir) * 8, 7)];
                    else txType = T.TxTypeIntraInvSet2[S(Av1Cdf.IntraTxTypeSet2 + (sqr * 13 + intraDir) * 6, 5)];
                }
            }
            else txType = Av1C.DctDct;
            SetTxTypes(x4, y4, txSz, txType);
        }

        void SetTxTypes(int x4, int y4, int txSz, int txType)
        {
            int w4 = T.TxWidth[txSz] >> 2, h4 = T.TxHeight[txSz] >> 2;
            int rEnd = Math.Min(y4 + h4, fs.MiRows), cEnd = Math.Min(x4 + w4, fs.MiCols);
            for (int r = y4; r < rEnd; r++) fs.TxTypes.AsSpan(r * fs.MiStride + x4, cEnd - x4).Fill((byte)txType);
        }

        int ComputeTxType(int plane, int txSz, int blockX, int blockY)
        {
            int txSzSqrUp = T.TxSizeSqrUp[txSz];
            if (lossless || txSzSqrUp > Av1C.Tx32x32) return Av1C.DctDct;
            int txSet = GetTxSet(txSz);
            if (plane == 0) return fs.TxTypes[blockY * fs.MiStride + blockX];
            int txType;
            if (isInter)
            {
                int x4 = Math.Max(MiCol, blockX << subX), y4 = Math.Max(MiRow, blockY << subY);
                txType = fs.TxTypes[y4 * fs.MiStride + x4];
            }
            else txType = T.ModeToTxfm[UVMode];
            return IsTxTypeInSet(txSet, txType) ? txType : Av1C.DctDct;
        }

        ReadOnlySpan<byte> ScanU8(int txSz, int txType)
        {
            bool preferRow = txType is Av1C.VDct or Av1C.VAdst or Av1C.VFlipadst;
            bool preferCol = txType is Av1C.HDct or Av1C.HAdst or Av1C.HFlipadst;
            if (txType == Av1C.Idtx) preferRow = preferCol = false;
            switch (txSz)
            {
                case Av1C.Tx4x4: return preferRow ? T.MrowScan4x4 : preferCol ? T.McolScan4x4 : T.DefaultScan4x4;
                case Av1C.Tx4x8: return preferRow ? T.MrowScan4x8 : preferCol ? T.McolScan4x8 : T.DefaultScan4x8;
                case Av1C.Tx8x4: return preferRow ? T.MrowScan8x4 : preferCol ? T.McolScan8x4 : T.DefaultScan8x4;
                case Av1C.Tx8x8: return preferRow ? T.MrowScan8x8 : preferCol ? T.McolScan8x8 : T.DefaultScan8x8;
                case Av1C.Tx8x16: return preferRow ? T.MrowScan8x16 : preferCol ? T.McolScan8x16 : T.DefaultScan8x16;
                case Av1C.Tx16x8: return preferRow ? T.MrowScan16x8 : preferCol ? T.McolScan16x8 : T.DefaultScan16x8;
                case Av1C.Tx16x16: return preferRow ? T.MrowScan16x16 : preferCol ? T.McolScan16x16 : T.DefaultScan16x16;
                case Av1C.Tx4x16: return preferRow ? T.MrowScan4x16 : preferCol ? T.McolScan4x16 : T.DefaultScan4x16;
                case Av1C.Tx16x4: return preferRow ? T.MrowScan16x4 : preferCol ? T.McolScan16x4 : T.DefaultScan16x4;
                case Av1C.Tx8x32: return T.DefaultScan8x32;
                case Av1C.Tx32x8: return T.DefaultScan32x8;
                default: return default;
            }
        }

        ReadOnlySpan<ushort> ScanU16(int txSz)
        {
            if (txSz == Av1C.Tx16x64 || txSz == Av1C.Tx16x32) return T.DefaultScan16x32;
            if (txSz == Av1C.Tx64x16 || txSz == Av1C.Tx32x16) return T.DefaultScan32x16;
            return T.DefaultScan32x32;
        }

        static bool ScanIsWide(int txSz) => txSz is Av1C.Tx16x32 or Av1C.Tx32x16 or Av1C.Tx32x32 or Av1C.Tx16x64 or Av1C.Tx64x16
            or Av1C.Tx32x64 or Av1C.Tx64x32 or Av1C.Tx64x64;

        static int TxClass(int txType) => txType is Av1C.VDct or Av1C.VAdst or Av1C.VFlipadst ? 2
            : txType is Av1C.HDct or Av1C.HAdst or Av1C.HFlipadst ? 1 : 0;

        // ------------------------------------------------------------------ coefficients (5.11.39)

        int Coeffs(int plane, int startX, int startY, int txSz)
        {
            int x4 = startX >> 2, y4 = startY >> 2;
            int w4 = T.TxWidth[txSz] >> 2, h4 = T.TxHeight[txSz] >> 2;
            int txSzCtx = (T.TxSizeSqr[txSz] + T.TxSizeSqrUp[txSz] + 1) >> 1;
            int ptype = plane > 0 ? 1 : 0;
            int segEob = txSz == Av1C.Tx16x64 || txSz == Av1C.Tx64x16 ? 512 : Math.Min(1024, T.TxWidth[txSz] * T.TxHeight[txSz]);
            Array.Clear(quant, 0, segEob);
            int eob = 0, culLevel = 0, dcCategory = 0;
            var aboveLevel = aboveLevelCtx[plane];
            var leftLevel = leftLevelCtx[plane];
            var aboveDc = aboveDcCtx[plane];
            var leftDc = leftDcCtx[plane];
            int maxX4 = fs.MiCols, maxY4 = fs.MiRows;
            if (plane > 0) { maxX4 >>= subX; maxY4 >>= subY; }

            // all_zero
            int allZeroCtx;
            {
                int w = T.TxWidth[txSz], hh = T.TxHeight[txSz];
                int bsize = PlaneResidualSize(MiSize, plane);
                int bw = BlockWidth(bsize), bh = BlockHeight(bsize);
                if (plane == 0)
                {
                    int top = 0, left = 0;
                    for (int k = 0; k < w4; k++) if (x4 + k < maxX4) top = Math.Max(top, aboveLevel[x4 + k]);
                    for (int k = 0; k < h4; k++) if (y4 + k < maxY4) left = Math.Max(left, leftLevel[y4 + k]);
                    top = Math.Min(top, 255);
                    left = Math.Min(left, 255);
                    if (bw == w && bh == hh) allZeroCtx = 0;
                    else if (top == 0 && left == 0) allZeroCtx = 1;
                    else if (top == 0 || left == 0) allZeroCtx = 2 + (Math.Max(top, left) > 3 ? 1 : 0);
                    else if (Math.Max(top, left) <= 3) allZeroCtx = 4;
                    else if (Math.Min(top, left) <= 3) allZeroCtx = 5;
                    else allZeroCtx = 6;
                }
                else
                {
                    int above = 0, left = 0;
                    for (int i = 0; i < w4; i++) if (x4 + i < maxX4) above |= aboveLevel[x4 + i] | aboveDc[x4 + i];
                    for (int i = 0; i < h4; i++) if (y4 + i < maxY4) left |= leftLevel[y4 + i] | leftDc[y4 + i];
                    allZeroCtx = (above != 0 ? 1 : 0) + (left != 0 ? 1 : 0);
                    allZeroCtx += 7;
                    if (bw * bh > w * hh) allZeroCtx += 3;
                }
            }
            bool allZero = S(Av1Cdf.TxbSkip + (txSzCtx * 13 + allZeroCtx) * 3, 2) != 0;
            if (allZero)
            {
                if (plane == 0) SetTxTypes(x4, y4, txSz, Av1C.DctDct);
            }
            else
            {
                if (plane == 0) TransformType(x4, y4, txSz);
                planeTxType = ComputeTxType(plane, txSz, x4, y4);
                if (Av1Stats.On) { Av1Stats.Count($"tx.size{txSz}"); Av1Stats.Count($"tx.type{planeTxType}"); if (lossless) Av1Stats.Count("tx.wht"); }
                int txClass = TxClass(planeTxType);
                bool wide = ScanIsWide(txSz);
                ReadOnlySpan<byte> scan8 = wide ? default : ScanU8(txSz, planeTxType);
                ReadOnlySpan<ushort> scan16 = wide ? ScanU16(txSz) : default;
                int eobMultisize = Math.Min((int)T.TxWidthLog2[txSz], 5) + Math.Min((int)T.TxHeightLog2[txSz], 5) - 4;
                int eobCtx = txClass == 0 ? 0 : 1;
                int eobPt;
                switch (eobMultisize)
                {
                    case 0: eobPt = S(Av1Cdf.EobPt16 + (ptype * 2 + eobCtx) * 6, 5) + 1; break;
                    case 1: eobPt = S(Av1Cdf.EobPt32 + (ptype * 2 + eobCtx) * 7, 6) + 1; break;
                    case 2: eobPt = S(Av1Cdf.EobPt64 + (ptype * 2 + eobCtx) * 8, 7) + 1; break;
                    case 3: eobPt = S(Av1Cdf.EobPt128 + (ptype * 2 + eobCtx) * 9, 8) + 1; break;
                    case 4: eobPt = S(Av1Cdf.EobPt256 + (ptype * 2 + eobCtx) * 10, 9) + 1; break;
                    case 5: eobPt = S(Av1Cdf.EobPt512 + ptype * 11, 10) + 1; break;
                    default: eobPt = S(Av1Cdf.EobPt1024 + ptype * 12, 11) + 1; break;
                }
                eob = eobPt < 2 ? eobPt : (1 << (eobPt - 2)) + 1;
                int eobShift = Math.Max(-1, eobPt - 3);
                if (eobShift >= 0)
                {
                    if (S(Av1Cdf.EobExtra + ((txSzCtx * 2 + ptype) * 9 + eobPt - 3) * 3, 2) != 0) eob += 1 << eobShift;
                    for (int i = 1; i < Math.Max(0, eobPt - 2); i++)
                    {
                        eobShift = Math.Max(0, eobPt - 2) - 1 - i;
                        if (L(1) != 0) eob += 1 << eobShift;
                    }
                }
                int adjTxSz = T.AdjustedTxSize[txSz];
                int bwl = T.TxWidthLog2[adjTxSz];
                int width = 1 << bwl, height = T.TxHeight[adjTxSz];
                int baseCtxOff = Av1Cdf.CoeffBase + (txSzCtx * 2 + ptype) * 42 * 5;
                int brCtxOff = Av1Cdf.CoeffBr + (Math.Min(txSzCtx, Av1C.Tx32x32) * 2 + ptype) * 21 * 5;
                for (int c = eob - 1; c >= 0; c--)
                {
                    int pos = wide ? scan16[c] : scan8[c];
                    int level;
                    if (c == eob - 1)
                    {
                        int ctx;
                        if (c == 0) ctx = 0;
                        else if (c <= (height << bwl) / 8) ctx = 1;
                        else if (c <= (height << bwl) / 4) ctx = 2;
                        else ctx = 3;
                        level = S(Av1Cdf.CoeffBaseEob + ((txSzCtx * 2 + ptype) * 4 + ctx) * 4, 3) + 1;
                    }
                    else
                    {
                        int ctx = CoeffBaseCtx(txSz, txClass, bwl, width, height, pos);
                        level = S(baseCtxOff + ctx * 5, 4);
                    }
                    if (level > Av1C.NumBaseLevels)
                    {
                        int brCtx = CoeffBrCtx(txClass, bwl, width, height, pos);
                        for (int idx = 0; idx < Av1C.CoeffBaseRange / (Av1C.BrCdfSize - 1); idx++)
                        {
                            int br = S(brCtxOff + brCtx * 5, 4);
                            level += br;
                            if (br < Av1C.BrCdfSize - 1) break;
                        }
                    }
                    quant[pos] = level;
                }
                for (int c = 0; c < eob; c++)
                {
                    int pos = wide ? scan16[c] : scan8[c];
                    int sign = 0;
                    if (quant[pos] != 0)
                    {
                        if (c == 0)
                        {
                            int dcSign = 0;
                            for (int k = 0; k < w4; k++)
                                if (x4 + k < maxX4)
                                {
                                    int s = aboveDc[x4 + k];
                                    if (s == 1) dcSign--; else if (s == 2) dcSign++;
                                }
                            for (int k = 0; k < h4; k++)
                                if (y4 + k < maxY4)
                                {
                                    int s = leftDc[y4 + k];
                                    if (s == 1) dcSign--; else if (s == 2) dcSign++;
                                }
                            int ctx = dcSign < 0 ? 1 : dcSign > 0 ? 2 : 0;
                            sign = S(Av1Cdf.DcSign + (ptype * 3 + ctx) * 3, 2);
                        }
                        else sign = L(1);
                    }
                    if (quant[pos] > Av1C.NumBaseLevels + Av1C.CoeffBaseRange)
                    {
                        int length = 0;
                        int bit;
                        do
                        {
                            length++;
                            bit = L(1);
                            if (length > 32) throw new ImageFormatException("AV1: bad Golomb code");
                        } while (bit == 0);
                        int xx = 1;
                        for (int i = length - 2; i >= 0; i--) xx = (xx << 1) | L(1);
                        quant[pos] = xx + Av1C.CoeffBaseRange + Av1C.NumBaseLevels;
                    }
                    if (pos == 0 && quant[pos] > 0) dcCategory = sign != 0 ? 1 : 2;
                    quant[pos] &= 0xFFFFF;
                    culLevel += quant[pos];
                    if (sign != 0) quant[pos] = -quant[pos];
                }
                culLevel = Math.Min(63, culLevel);
            }
            for (int i = 0; i < w4; i++) { aboveLevel[x4 + i] = (byte)culLevel; aboveDc[x4 + i] = (byte)dcCategory; }
            for (int i = 0; i < h4; i++) { leftLevel[y4 + i] = (byte)culLevel; leftDc[y4 + i] = (byte)dcCategory; }
            return eob;
        }

        int CoeffBaseCtx(int txSz, int txClass, int bwl, int width, int height, int pos)
        {
            int row = pos >> bwl, col = pos - (row << bwl);
            int mag = 0;
            var offs = T.SigRefDiffOffset;
            for (int idx = 0; idx < 5; idx++)
            {
                int refRow = row + offs[(txClass * 5 + idx) * 2];
                int refCol = col + offs[(txClass * 5 + idx) * 2 + 1];
                if (refRow < height && refCol < width)
                    mag += Math.Min(Math.Abs(quant[(refRow << bwl) + refCol]), 3);
            }
            int ctx = Math.Min((mag + 1) >> 1, 4);
            if (txClass == 0)
            {
                if (row == 0 && col == 0) return 0;
                return ctx + T.CoeffBaseCtxOffset[txSz * 25 + Math.Min(row, 4) * 5 + Math.Min(col, 4)];
            }
            int i2 = txClass == 2 ? row : col;
            return ctx + T.CoeffBasePosCtxOffset[Math.Min(i2, 2)];
        }

        int CoeffBrCtx(int txClass, int bwl, int txw, int txh, int pos)
        {
            int row = pos >> bwl, col = pos - (row << bwl);
            int mag = 0;
            var offs = T.MagRefOffsetWithTxClass;
            for (int idx = 0; idx < 3; idx++)
            {
                int refRow = row + offs[(txClass * 3 + idx) * 2];
                int refCol = col + offs[(txClass * 3 + idx) * 2 + 1];
                if (refRow < txh && refCol < (1 << bwl))
                    mag += Math.Min(quant[refRow * txw + refCol], Av1C.CoeffBaseRange + Av1C.NumBaseLevels + 1);
            }
            mag = Math.Min((mag + 1) >> 1, 6);
            if (pos == 0) return mag;
            if (txClass == 0) return row < 2 && col < 2 ? mag + 7 : mag + 14;
            if (txClass == 1) return col == 0 ? mag + 7 : mag + 14;
            return row == 0 ? mag + 7 : mag + 14;
        }

        // ------------------------------------------------------------------ reconstruction (7.12.3)

        int DcQ(int b) => T.DcQlookup[((bitDepth - 8) >> 1) * 256 + Math.Clamp(b, 0, 255)];
        int AcQ(int b) => T.AcQlookup[((bitDepth - 8) >> 1) * 256 + Math.Clamp(b, 0, 255)];

        void Reconstruct(int plane, int x, int y, int txSz)
        {
            int dqDenom = txSz is Av1C.Tx32x32 or Av1C.Tx16x32 or Av1C.Tx32x16 or Av1C.Tx16x64 or Av1C.Tx64x16 ? 2
                : txSz is Av1C.Tx64x64 or Av1C.Tx32x64 or Av1C.Tx64x32 ? 4 : 1;
            int log2W = T.TxWidthLog2[txSz], log2H = T.TxHeightLog2[txSz];
            int w = 1 << log2W, hh = 1 << log2H;
            int tw = Math.Min(32, w), th = Math.Min(32, hh);
            int tt = planeTxType;
            bool flipUD = tt is Av1C.FlipadstDct or Av1C.FlipadstAdst or Av1C.VFlipadst or Av1C.FlipadstFlipadst;
            bool flipLR = tt is Av1C.DctFlipadst or Av1C.AdstFlipadst or Av1C.HFlipadst or Av1C.FlipadstFlipadst;
            int qindex = h.QIndex(false, segmentId, CurrentQIndex);
            int dcQ, acQ;
            if (plane == 0) { dcQ = DcQ(qindex + h.DeltaQYDc); acQ = AcQ(qindex); }
            else if (plane == 1) { dcQ = DcQ(qindex + h.DeltaQUDc); acQ = AcQ(qindex + h.DeltaQUAc); }
            else { dcQ = DcQ(qindex + h.DeltaQVDc); acQ = AcQ(qindex + h.DeltaQVAc); }
            int qmLevel = h.UsingQmatrix ? h.SegQMLevel[plane * 8 + segmentId] : 15;
            bool useQm = h.UsingQmatrix && tt < Av1C.Idtx && qmLevel < 15;
            int qmBase = useQm ? (qmLevel * 2 + (plane > 0 ? 1 : 0)) * 3344 + T.QmOffset[txSz] : 0;
            var qm = T.QuantizerMatrix;
            long lo = -(1L << (7 + bitDepth)), hi = (1L << (7 + bitDepth)) - 1;
            for (int i = 0; i < th; i++)
                for (int j = 0; j < tw; j++)
                {
                    int k = i * tw + j;
                    int qv = quant[k];
                    if (qv == 0) { dequant[k] = 0; continue; }
                    long q = k == 0 ? dcQ : acQ;
                    if (useQm) q = (q * qm[qmBase + k] + 16) >> 5;
                    long dq = qv * q;
                    long a = (Math.Abs(dq) & 0xFFFFFF) / dqDenom;
                    dequant[k] = (int)Math.Clamp(dq < 0 ? -a : a, lo, hi);
                }
            transform.Inverse2D(dequant, txSz, tt, lossless, bitDepth, residualBuf);
            var plane0 = fs.Cur.Planes[plane];
            int stride = fs.Cur.Stride[plane];
            int maxV = (1 << bitDepth) - 1;
            for (int i = 0; i < hh; i++)
            {
                int yy = flipUD ? hh - i - 1 : i;
                int rowOff = (y + yy) * stride + x;
                for (int j = 0; j < w; j++)
                {
                    int xx = flipLR ? w - j - 1 : j;
                    ref ushort px = ref plane0[rowOff + xx];
                    px = (ushort)Math.Clamp(px + residualBuf[i * w + j], 0, maxV);
                }
            }
        }
    }
}
