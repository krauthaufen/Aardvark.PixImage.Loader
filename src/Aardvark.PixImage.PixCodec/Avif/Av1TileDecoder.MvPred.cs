using System;
using T = Aardvark.ImageCodecs.Av1.Av1Tables;

namespace Aardvark.ImageCodecs.Av1
{
    /// <summary>Motion vector prediction processes (AV1 spec 7.10).</summary>
    internal sealed partial class Av1TileDecoder
    {
        readonly int[] refStackRow = new int[Av1C.MaxRefMvStackSize * 2], refStackCol = new int[Av1C.MaxRefMvStackSize * 2];
        readonly int[] weightStack = new int[Av1C.MaxRefMvStackSize];
        readonly int[] drlCtxStack = new int[Av1C.MaxRefMvStackSize];
        readonly int[] globalMvRow = new int[2], globalMvCol = new int[2];
        int numMvFound, newMvCount, closeMatches, totalMatches;
        bool foundMatch;
        int newMvContext, refMvContext, zeroMvContext;
        // extra search
        readonly int[] refIdCount = new int[2], refDiffCount = new int[2];
        readonly int[] refIdRow = new int[4], refIdCol = new int[4], refDiffRow = new int[4], refDiffCol = new int[4];
        // warp samples
        int numSamples, numSamplesScanned;
        readonly int[] candList = new int[Av1C.LeastSquaresSamplesMax * 4];

        void FindMvStack(bool isCompound)
        {
            numMvFound = 0;
            newMvCount = 0;
            SetupGlobalMv(0);
            if (isCompound) SetupGlobalMv(1);
            foundMatch = false;
            ScanRow(-1, isCompound);
            bool foundAboveMatch = foundMatch;
            foundMatch = false;
            ScanCol(-1, isCompound);
            bool foundLeftMatch = foundMatch;
            foundMatch = false;
            if (Math.Max(bw4, bh4) <= 16) ScanPoint(-1, bw4, isCompound);
            if (foundMatch) foundAboveMatch = true;
            closeMatches = (foundAboveMatch ? 1 : 0) + (foundLeftMatch ? 1 : 0);
            int numNearest = numMvFound, numNew = newMvCount;
            for (int idx = 0; idx < numNearest; idx++) weightStack[idx] += Av1C.RefCatLevel;
            zeroMvContext = 0;
            if (h.UseRefFrameMvs) TemporalScan(isCompound);
            ScanPoint(-1, -1, isCompound);
            if (foundMatch) foundAboveMatch = true;
            foundMatch = false;
            ScanRow(-3, isCompound);
            if (foundMatch) foundAboveMatch = true;
            foundMatch = false;
            ScanCol(-3, isCompound);
            if (foundMatch) foundLeftMatch = true;
            foundMatch = false;
            if (bh4 > 1) ScanRow(-5, isCompound);
            if (foundMatch) foundAboveMatch = true;
            foundMatch = false;
            if (bw4 > 1) ScanCol(-5, isCompound);
            if (foundMatch) foundLeftMatch = true;
            totalMatches = (foundAboveMatch ? 1 : 0) + (foundLeftMatch ? 1 : 0);
            SortStack(0, numNearest, isCompound);
            SortStack(numNearest, numMvFound, isCompound);
            if (numMvFound < 2) ExtraSearch(isCompound);
            ContextAndClamping(isCompound, numNew);
        }

        void SetupGlobalMv(int refList)
        {
            int rf = RefFrame[refList];
            int typ = rf != Av1C.IntraFrame ? h.GmType[rf] : Av1C.Identity;
            int bw = BlockWidth(MiSize), bh = BlockHeight(MiSize);
            int mv0, mv1;
            if (rf == Av1C.IntraFrame || typ == Av1C.Identity) { mv0 = 0; mv1 = 0; }
            else if (typ == Av1C.Translation)
            {
                mv0 = h.GmParams[rf, 0] >> (Av1C.WarpedModelPrecBits - 3);
                mv1 = h.GmParams[rf, 1] >> (Av1C.WarpedModelPrecBits - 3);
            }
            else
            {
                int x = MiCol * Av1C.MiSize + bw / 2 - 1;
                int y = MiRow * Av1C.MiSize + bh / 2 - 1;
                long xc = (long)(h.GmParams[rf, 2] - (1 << Av1C.WarpedModelPrecBits)) * x + (long)h.GmParams[rf, 3] * y + h.GmParams[rf, 0];
                long yc = (long)h.GmParams[rf, 4] * x + (long)(h.GmParams[rf, 5] - (1 << Av1C.WarpedModelPrecBits)) * y + h.GmParams[rf, 1];
                if (h.AllowHighPrecisionMv)
                {
                    mv0 = (int)Round2Signed(yc, Av1C.WarpedModelPrecBits - 3);
                    mv1 = (int)Round2Signed(xc, Av1C.WarpedModelPrecBits - 3);
                }
                else
                {
                    mv0 = (int)Round2Signed(yc, Av1C.WarpedModelPrecBits - 2) * 2;
                    mv1 = (int)Round2Signed(xc, Av1C.WarpedModelPrecBits - 2) * 2;
                }
            }
            LowerMvPrecision(ref mv0);
            LowerMvPrecision(ref mv1);
            globalMvRow[refList] = mv0;
            globalMvCol[refList] = mv1;
        }

        void LowerMvPrecision(ref int v)
        {
            if (h.AllowHighPrecisionMv) return;
            if (h.ForceIntegerMv)
            {
                int a = Math.Abs(v);
                int aInt = (a + 3) >> 3;
                v = v > 0 ? aInt << 3 : -(aInt << 3);
            }
            else if ((v & 1) != 0) v += v > 0 ? -1 : 1;
        }

        void ScanRow(int deltaRow, bool isCompound)
        {
            int end4 = Math.Min(Math.Min(bw4, fs.MiCols - MiCol), 16);
            int deltaCol = 0;
            bool useStep16 = bw4 >= 16;
            if (Math.Abs(deltaRow) > 1)
            {
                deltaRow += MiRow & 1;
                deltaCol = 1 - (MiCol & 1);
            }
            int i = 0;
            while (i < end4)
            {
                int mvRow = MiRow + deltaRow, mvCol = MiCol + deltaCol + i;
                if (!IsInside(mvRow, mvCol)) break;
                int len = Math.Min(bw4, (int)T.Num4x4BlocksWide[Nb(mvRow, mvCol).MiSize]);
                if (Math.Abs(deltaRow) > 1) len = Math.Max(2, len);
                if (useStep16) len = Math.Max(4, len);
                AddRefMvCandidate(mvRow, mvCol, isCompound, len * 2);
                i += len;
            }
        }

        void ScanCol(int deltaCol, bool isCompound)
        {
            int end4 = Math.Min(Math.Min(bh4, fs.MiRows - MiRow), 16);
            int deltaRow = 0;
            bool useStep16 = bh4 >= 16;
            if (Math.Abs(deltaCol) > 1)
            {
                deltaRow = 1 - (MiRow & 1);
                deltaCol += MiCol & 1;
            }
            int i = 0;
            while (i < end4)
            {
                int mvRow = MiRow + deltaRow + i, mvCol = MiCol + deltaCol;
                if (!IsInside(mvRow, mvCol)) break;
                int len = Math.Min(bh4, (int)T.Num4x4BlocksHigh[Nb(mvRow, mvCol).MiSize]);
                if (Math.Abs(deltaCol) > 1) len = Math.Max(2, len);
                if (useStep16) len = Math.Max(4, len);
                AddRefMvCandidate(mvRow, mvCol, isCompound, len * 2);
                i += len;
            }
        }

        void ScanPoint(int deltaRow, int deltaCol, bool isCompound)
        {
            int mvRow = MiRow + deltaRow, mvCol = MiCol + deltaCol;
            if (IsInside(mvRow, mvCol) && fs.HasBlock(mvRow, mvCol)) AddRefMvCandidate(mvRow, mvCol, isCompound, 4);
        }

        void TemporalScan(bool isCompound)
        {
            int stepW4 = bw4 >= 16 ? 4 : 2, stepH4 = bh4 >= 16 ? 4 : 2;
            for (int deltaRow = 0; deltaRow < Math.Min(bh4, 16); deltaRow += stepH4)
                for (int deltaCol = 0; deltaCol < Math.Min(bw4, 16); deltaCol += stepW4)
                    AddTplRefMv(deltaRow, deltaCol, isCompound);
            bool allowExtension = bh4 >= T.Num4x4BlocksHigh[Av1C.Block8x8] && bh4 < T.Num4x4BlocksHigh[Av1C.Block64x64]
                && bw4 >= T.Num4x4BlocksWide[Av1C.Block8x8] && bw4 < T.Num4x4BlocksWide[Av1C.Block64x64];
            if (allowExtension)
            {
                for (int i = 0; i < 3; i++)
                {
                    int deltaRow = i == 0 ? bh4 : i == 1 ? bh4 : bh4 - 2;
                    int deltaCol = i == 0 ? -2 : bw4;
                    int row = (MiRow & 15) + deltaRow, col = (MiCol & 15) + deltaCol;
                    if (row >= 0 && row < 16 && col >= 0 && col < 16) AddTplRefMv(deltaRow, deltaCol, isCompound);
                }
            }
        }

        void AddTplRefMv(int deltaRow, int deltaCol, bool isCompound)
        {
            int mvRow = (MiRow + deltaRow) | 1, mvCol = (MiCol + deltaCol) | 1;
            if (!IsInside(mvRow, mvCol)) return;
            int x8 = mvCol >> 1, y8 = mvRow >> 1;
            int w8 = fs.MiCols >> 1;
            var mf = fs.MotionFieldMvs!;
            if (deltaRow == 0 && deltaCol == 0) zeroMvContext = 1;
            if (!isCompound)
            {
                int packed = mf[RefFrame[0]][y8 * w8 + x8];
                int c0 = (short)packed, c1 = packed >> 16;
                if (c0 == -32768) return;
                LowerMvPrecision(ref c0);
                LowerMvPrecision(ref c1);
                if (deltaRow == 0 && deltaCol == 0)
                    zeroMvContext = Math.Abs(c0 - globalMvRow[0]) >= 16 || Math.Abs(c1 - globalMvCol[0]) >= 16 ? 1 : 0;
                int idx;
                for (idx = 0; idx < numMvFound; idx++)
                    if (c0 == refStackRow[idx * 2] && c1 == refStackCol[idx * 2]) break;
                if (idx < numMvFound) weightStack[idx] += 2;
                else if (numMvFound < Av1C.MaxRefMvStackSize)
                {
                    refStackRow[numMvFound * 2] = c0; refStackCol[numMvFound * 2] = c1;
                    weightStack[numMvFound] = 2;
                    numMvFound++;
                }
            }
            else
            {
                int p0 = mf[RefFrame[0]][y8 * w8 + x8];
                int a0 = (short)p0, a1 = p0 >> 16;
                if (a0 == -32768) return;
                int p1 = mf[RefFrame[1]][y8 * w8 + x8];
                int b0 = (short)p1, b1 = p1 >> 16;
                if (b0 == -32768) return;
                LowerMvPrecision(ref a0); LowerMvPrecision(ref a1);
                LowerMvPrecision(ref b0); LowerMvPrecision(ref b1);
                if (deltaRow == 0 && deltaCol == 0)
                    zeroMvContext = Math.Abs(a0 - globalMvRow[0]) >= 16 || Math.Abs(a1 - globalMvCol[0]) >= 16
                        || Math.Abs(b0 - globalMvRow[1]) >= 16 || Math.Abs(b1 - globalMvCol[1]) >= 16 ? 1 : 0;
                int idx;
                for (idx = 0; idx < numMvFound; idx++)
                    if (a0 == refStackRow[idx * 2] && a1 == refStackCol[idx * 2] && b0 == refStackRow[idx * 2 + 1] && b1 == refStackCol[idx * 2 + 1]) break;
                if (idx < numMvFound) weightStack[idx] += 2;
                else if (numMvFound < Av1C.MaxRefMvStackSize)
                {
                    refStackRow[numMvFound * 2] = a0; refStackCol[numMvFound * 2] = a1;
                    refStackRow[numMvFound * 2 + 1] = b0; refStackCol[numMvFound * 2 + 1] = b1;
                    weightStack[numMvFound] = 2;
                    numMvFound++;
                }
            }
        }

        static bool HasNewMv(int mode) => mode is Av1C.NewMv or Av1C.NewNewMv or Av1C.NearNewMv or Av1C.NewNearMv or Av1C.NearestNewMv or Av1C.NewNearestMv;

        void AddRefMvCandidate(int mvRow, int mvCol, bool isCompound, int weight)
        {
            ref var b = ref Nb(mvRow, mvCol);
            if (!b.IsInter) return;
            if (!isCompound)
            {
                for (int cl = 0; cl < 2; cl++)
                    if (b.RefFrame(cl) == RefFrame[0]) SearchStack(ref b, cl, weight);
            }
            else if (b.RefFrame0 == RefFrame[0] && b.RefFrame1 == RefFrame[1]) CompoundSearchStack(ref b, weight);
        }

        void SearchStack(ref Av1Block b, int cl, int weight)
        {
            int candMode = b.YMode;
            int candSize = b.MiSize;
            bool large = Math.Min(BlockWidth(candSize), BlockHeight(candSize)) >= 8;
            int r, c;
            if ((candMode == Av1C.GlobalMv || candMode == Av1C.GlobalGlobalMv) && h.GmType[RefFrame[0]] > Av1C.Translation && large)
            {
                r = globalMvRow[0]; c = globalMvCol[0];
            }
            else if (cl == 0) { r = b.Mv0Row; c = b.Mv0Col; }
            else { r = b.Mv1Row; c = b.Mv1Col; }
            LowerMvPrecision(ref r);
            LowerMvPrecision(ref c);
            if (HasNewMv(candMode)) newMvCount++;
            foundMatch = true;
            int idx;
            for (idx = 0; idx < numMvFound; idx++)
                if (r == refStackRow[idx * 2] && c == refStackCol[idx * 2]) break;
            if (idx < numMvFound) weightStack[idx] += weight;
            else if (numMvFound < Av1C.MaxRefMvStackSize)
            {
                refStackRow[numMvFound * 2] = r; refStackCol[numMvFound * 2] = c;
                weightStack[numMvFound] = weight;
                numMvFound++;
            }
        }

        void CompoundSearchStack(ref Av1Block b, int weight)
        {
            int r0 = b.Mv0Row, c0 = b.Mv0Col, r1 = b.Mv1Row, c1 = b.Mv1Col;
            int candMode = b.YMode;
            if (candMode == Av1C.GlobalGlobalMv)
            {
                if (h.GmType[RefFrame[0]] > Av1C.Translation) { r0 = globalMvRow[0]; c0 = globalMvCol[0]; }
                if (h.GmType[RefFrame[1]] > Av1C.Translation) { r1 = globalMvRow[1]; c1 = globalMvCol[1]; }
            }
            LowerMvPrecision(ref r0); LowerMvPrecision(ref c0);
            LowerMvPrecision(ref r1); LowerMvPrecision(ref c1);
            foundMatch = true;
            int idx;
            for (idx = 0; idx < numMvFound; idx++)
                if (r0 == refStackRow[idx * 2] && c0 == refStackCol[idx * 2] && r1 == refStackRow[idx * 2 + 1] && c1 == refStackCol[idx * 2 + 1]) break;
            if (idx < numMvFound) weightStack[idx] += weight;
            else if (numMvFound < Av1C.MaxRefMvStackSize)
            {
                refStackRow[numMvFound * 2] = r0; refStackCol[numMvFound * 2] = c0;
                refStackRow[numMvFound * 2 + 1] = r1; refStackCol[numMvFound * 2 + 1] = c1;
                weightStack[numMvFound] = weight;
                numMvFound++;
            }
            if (HasNewMv(candMode)) newMvCount++;
        }

        void SortStack(int start, int end, bool isCompound)
        {
            while (end > start)
            {
                int newEnd = start;
                for (int idx = start + 1; idx < end; idx++)
                    if (weightStack[idx - 1] < weightStack[idx])
                    {
                        SwapStack(idx - 1, idx, isCompound);
                        newEnd = idx;
                    }
                end = newEnd;
            }
        }

        void SwapStack(int i, int j, bool isCompound)
        {
            (weightStack[i], weightStack[j]) = (weightStack[j], weightStack[i]);
            for (int list = 0; list < 1 + (isCompound ? 1 : 0); list++)
            {
                (refStackRow[i * 2 + list], refStackRow[j * 2 + list]) = (refStackRow[j * 2 + list], refStackRow[i * 2 + list]);
                (refStackCol[i * 2 + list], refStackCol[j * 2 + list]) = (refStackCol[j * 2 + list], refStackCol[i * 2 + list]);
            }
        }

        void ExtraSearch(bool isCompound)
        {
            refIdCount[0] = refIdCount[1] = 0;
            refDiffCount[0] = refDiffCount[1] = 0;
            int w4 = Math.Min(16, bw4), h4 = Math.Min(16, bh4);
            w4 = Math.Min(w4, fs.MiCols - MiCol);
            h4 = Math.Min(h4, fs.MiRows - MiRow);
            int num4x4 = Math.Min(w4, h4);
            for (int pass = 0; pass < 2; pass++)
            {
                int idx = 0;
                while (idx < num4x4 && numMvFound < 2)
                {
                    int mvRow, mvCol;
                    if (pass == 0) { mvRow = MiRow - 1; mvCol = MiCol + idx; }
                    else { mvRow = MiRow + idx; mvCol = MiCol - 1; }
                    if (!IsInside(mvRow, mvCol)) break;
                    AddExtraMvCandidate(mvRow, mvCol, isCompound);
                    ref var b = ref Nb(mvRow, mvCol);
                    if (pass == 0) idx += T.Num4x4BlocksWide[b.MiSize];
                    else idx += T.Num4x4BlocksHigh[b.MiSize];
                }
            }
            if (isCompound)
            {
                Span<int> combRow = stackalloc int[4], combCol = stackalloc int[4];   // [compCount][list]
                for (int list = 0; list < 2; list++)
                {
                    int compCount = 0;
                    for (int idx = 0; idx < refIdCount[list]; idx++)
                    {
                        combRow[compCount * 2 + list] = refIdRow[list * 2 + idx];
                        combCol[compCount * 2 + list] = refIdCol[list * 2 + idx];
                        compCount++;
                    }
                    for (int idx = 0; idx < refDiffCount[list] && compCount < 2; idx++)
                    {
                        combRow[compCount * 2 + list] = refDiffRow[list * 2 + idx];
                        combCol[compCount * 2 + list] = refDiffCol[list * 2 + idx];
                        compCount++;
                    }
                    while (compCount < 2)
                    {
                        combRow[compCount * 2 + list] = globalMvRow[list];
                        combCol[compCount * 2 + list] = globalMvCol[list];
                        compCount++;
                    }
                }
                if (numMvFound == 1)
                {
                    if (combRow[0] == refStackRow[0] && combCol[0] == refStackCol[0] && combRow[1] == refStackRow[1] && combCol[1] == refStackCol[1])
                    {
                        refStackRow[numMvFound * 2] = combRow[2]; refStackCol[numMvFound * 2] = combCol[2];
                        refStackRow[numMvFound * 2 + 1] = combRow[3]; refStackCol[numMvFound * 2 + 1] = combCol[3];
                    }
                    else
                    {
                        refStackRow[numMvFound * 2] = combRow[0]; refStackCol[numMvFound * 2] = combCol[0];
                        refStackRow[numMvFound * 2 + 1] = combRow[1]; refStackCol[numMvFound * 2 + 1] = combCol[1];
                    }
                    weightStack[numMvFound] = 2;
                    numMvFound++;
                }
                else
                {
                    for (int idx = 0; idx < 2; idx++)
                    {
                        refStackRow[numMvFound * 2] = combRow[idx * 2]; refStackCol[numMvFound * 2] = combCol[idx * 2];
                        refStackRow[numMvFound * 2 + 1] = combRow[idx * 2 + 1]; refStackCol[numMvFound * 2 + 1] = combCol[idx * 2 + 1];
                        weightStack[numMvFound] = 2;
                        numMvFound++;
                    }
                }
            }
            else
            {
                for (int idx = numMvFound; idx < 2; idx++)
                {
                    refStackRow[idx * 2] = globalMvRow[0];
                    refStackCol[idx * 2] = globalMvCol[0];
                }
            }
        }

        void AddExtraMvCandidate(int mvRow, int mvCol, bool isCompound)
        {
            ref var b = ref Nb(mvRow, mvCol);
            if (isCompound)
            {
                for (int cl = 0; cl < 2; cl++)
                {
                    int candRef = b.RefFrame(cl);
                    if (candRef <= Av1C.IntraFrame) continue;
                    for (int list = 0; list < 2; list++)
                    {
                        int r = cl == 0 ? b.Mv0Row : b.Mv1Row, c = cl == 0 ? b.Mv0Col : b.Mv1Col;
                        if (candRef == RefFrame[list] && refIdCount[list] < 2)
                        {
                            refIdRow[list * 2 + refIdCount[list]] = r;
                            refIdCol[list * 2 + refIdCount[list]] = c;
                            refIdCount[list]++;
                        }
                        else if (refDiffCount[list] < 2)
                        {
                            if (h.RefFrameSignBias[candRef] != h.RefFrameSignBias[RefFrame[list]]) { r = -r; c = -c; }
                            refDiffRow[list * 2 + refDiffCount[list]] = r;
                            refDiffCol[list * 2 + refDiffCount[list]] = c;
                            refDiffCount[list]++;
                        }
                    }
                }
            }
            else
            {
                for (int cl = 0; cl < 2; cl++)
                {
                    int candRef = b.RefFrame(cl);
                    if (candRef <= Av1C.IntraFrame) continue;
                    int r = cl == 0 ? b.Mv0Row : b.Mv1Row, c = cl == 0 ? b.Mv0Col : b.Mv1Col;
                    if (h.RefFrameSignBias[candRef] != h.RefFrameSignBias[RefFrame[0]]) { r = -r; c = -c; }
                    int idx;
                    for (idx = 0; idx < numMvFound; idx++)
                        if (r == refStackRow[idx * 2] && c == refStackCol[idx * 2]) break;
                    if (idx == numMvFound)
                    {
                        refStackRow[idx * 2] = r; refStackCol[idx * 2] = c;
                        weightStack[idx] = 2;
                        numMvFound++;
                    }
                }
            }
        }

        void ContextAndClamping(bool isCompound, int numNew)
        {
            int bw = BlockWidth(MiSize), bh = BlockHeight(MiSize);
            int numLists = isCompound ? 2 : 1;
            for (int idx = 0; idx < numMvFound; idx++)
            {
                int z = 0;
                if (idx + 1 < numMvFound)
                {
                    int w0 = weightStack[idx], w1 = weightStack[idx + 1];
                    if (w0 >= Av1C.RefCatLevel)
                    {
                        if (w1 < Av1C.RefCatLevel) z = 1;
                    }
                    else z = 2;
                }
                drlCtxStack[idx] = z;
            }
            for (int list = 0; list < numLists; list++)
                for (int idx = 0; idx < numMvFound; idx++)
                {
                    refStackRow[idx * 2 + list] = ClampMvRow(refStackRow[idx * 2 + list], Av1C.MvBorder + bh * 8);
                    refStackCol[idx * 2 + list] = ClampMvCol(refStackCol[idx * 2 + list], Av1C.MvBorder + bw * 8);
                }
            if (closeMatches == 0)
            {
                newMvContext = Math.Min(totalMatches, 1);
                refMvContext = totalMatches;
            }
            else if (closeMatches == 1)
            {
                newMvContext = 3 - Math.Min(numNew, 1);
                refMvContext = 2 + totalMatches;
            }
            else
            {
                newMvContext = 5 - Math.Min(numNew, 1);
                refMvContext = 5;
            }
        }

        int ClampMvRow(int mvec, int border)
        {
            int mbToTopEdge = -((MiRow * Av1C.MiSize) * 8);
            int mbToBottomEdge = ((fs.MiRows - bh4 - MiRow) * Av1C.MiSize) * 8;
            return Math.Clamp(mvec, mbToTopEdge - border, mbToBottomEdge + border);
        }

        int ClampMvCol(int mvec, int border)
        {
            int mbToLeftEdge = -((MiCol * Av1C.MiSize) * 8);
            int mbToRightEdge = ((fs.MiCols - bw4 - MiCol) * Av1C.MiSize) * 8;
            return Math.Clamp(mvec, mbToLeftEdge - border, mbToRightEdge + border);
        }

        bool HasOverlappableCandidates()
        {
            if (AvailU)
            {
                for (int x4 = MiCol; x4 < Math.Min(fs.MiCols, MiCol + bw4); x4 += 2)
                    if (Nb(MiRow - 1, x4 | 1).RefFrame0 > Av1C.IntraFrame) return true;
            }
            if (AvailL)
            {
                for (int y4 = MiRow; y4 < Math.Min(fs.MiRows, MiRow + bh4); y4 += 2)
                    if (Nb(y4 | 1, MiCol - 1).RefFrame0 > Av1C.IntraFrame) return true;
            }
            return false;
        }

        void FindWarpSamples()
        {
            numSamples = 0;
            numSamplesScanned = 0;
            bool doTopLeft = true, doTopRight = true;
            if (AvailU)
            {
                int srcW = T.Num4x4BlocksWide[Nb(MiRow - 1, MiCol).MiSize];
                if (bw4 <= srcW)
                {
                    int colOffset = -(MiCol & (srcW - 1));
                    if (colOffset < 0) doTopLeft = false;
                    if (colOffset + srcW > bw4) doTopRight = false;
                    AddSample(-1, 0);
                }
                else
                {
                    int miStep;
                    for (int i = 0; i < Math.Min(bw4, fs.MiCols - MiCol); i += miStep)
                    {
                        srcW = T.Num4x4BlocksWide[Nb(MiRow - 1, MiCol + i).MiSize];
                        miStep = Math.Min(bw4, srcW);
                        AddSample(-1, i);
                    }
                }
            }
            if (AvailL)
            {
                int srcH = T.Num4x4BlocksHigh[Nb(MiRow, MiCol - 1).MiSize];
                if (bh4 <= srcH)
                {
                    int rowOffset = -(MiRow & (srcH - 1));
                    if (rowOffset < 0) doTopLeft = false;
                    AddSample(0, -1);
                }
                else
                {
                    int miStep;
                    for (int i = 0; i < Math.Min(bh4, fs.MiRows - MiRow); i += miStep)
                    {
                        srcH = T.Num4x4BlocksHigh[Nb(MiRow + i, MiCol - 1).MiSize];
                        miStep = Math.Min(bh4, srcH);
                        AddSample(i, -1);
                    }
                }
            }
            if (doTopLeft) AddSample(-1, -1);
            if (doTopRight && Math.Max(bw4, bh4) <= 16) AddSample(-1, bw4);
            if (numSamples == 0 && numSamplesScanned > 0) numSamples = 1;
        }

        void AddSample(int deltaRow, int deltaCol)
        {
            if (numSamplesScanned >= Av1C.LeastSquaresSamplesMax) return;
            int mvRow = MiRow + deltaRow, mvCol = MiCol + deltaCol;
            if (!IsInside(mvRow, mvCol)) return;
            if (!fs.HasBlock(mvRow, mvCol)) return;
            ref var b = ref Nb(mvRow, mvCol);
            if (b.RefFrame0 != RefFrame[0]) return;
            if (b.RefFrame1 != Av1C.None) return;
            int candSz = b.MiSize;
            int candW4 = T.Num4x4BlocksWide[candSz], candH4 = T.Num4x4BlocksHigh[candSz];
            int candRow = mvRow & ~(candH4 - 1), candCol = mvCol & ~(candW4 - 1);
            int midY = candRow * 4 + candH4 * 2 - 1;
            int midX = candCol * 4 + candW4 * 2 - 1;
            int threshold = Math.Clamp(Math.Max(BlockWidth(MiSize), BlockHeight(MiSize)), 16, 112);
            int mvDiffRow = Math.Abs(b.Mv0Row - MvRow[0]);
            int mvDiffCol = Math.Abs(b.Mv0Col - MvCol[0]);
            bool valid = mvDiffRow + mvDiffCol <= threshold;
            numSamplesScanned++;
            if (!valid && numSamplesScanned > 1) return;
            int o = numSamples * 4;
            candList[o] = midY * 8;
            candList[o + 1] = midX * 8;
            candList[o + 2] = midY * 8 + b.Mv0Row;
            candList[o + 3] = midX * 8 + b.Mv0Col;
            if (valid) numSamples++;
        }
    }
}
