using System;
using T = Aardvark.ImageCodecs.Av1.Av1Tables;

namespace Aardvark.ImageCodecs.Av1
{
    /// <summary>The film grain synthesis process of AV1 spec 7.18.3 (applied to a copy of the output frame).</summary>
    internal static class Av1FilmGrain
    {
        public static Av1Frame Apply(Av1Frame input, Av1FilmGrainParams g, Av1SequenceHeader seq)
        {
            var o = input.Clone();
            int bd = input.BitDepth;
            int w = input.Width, h = input.Height;
            int subX = input.SubX, subY = input.SubY;
            int numPlanes = input.NumPlanes;
            bool mono = numPlanes == 1;
            int rnd = g.GrainSeed;
            int GetRandom(int bits)
            {
                int r = rnd;
                int bit = ((r >> 0) ^ (r >> 1) ^ (r >> 3) ^ (r >> 12)) & 1;
                r = (r >> 1) | (bit << 15);
                rnd = r;
                return (r >> (16 - bits)) & ((1 << bits) - 1);
            }
            int grainCenter = 128 << (bd - 8);
            int grainMin = -grainCenter;
            int grainMax = (256 << (bd - 8)) - 1 - grainCenter;
            var gauss = T.GaussianSequence;

            // generate grain (7.18.3.3)
            var luma = new int[73 * 82];
            int shift = 12 - bd + g.GrainScaleShift;
            for (int y = 0; y < 73; y++)
                for (int x = 0; x < 82; x++)
                {
                    int gv = g.NumYPoints > 0 ? gauss[GetRandom(11)] : 0;
                    luma[y * 82 + x] = Round2(gv, shift);
                }
            int arShift = g.ArCoeffShiftMinus6 + 6;
            int lag = g.ArCoeffLag;
            for (int y = 3; y < 73; y++)
                for (int x = 3; x < 82 - 3; x++)
                {
                    int s = 0, pos = 0;
                    for (int dr = -lag; dr <= 0; dr++)
                        for (int dc = -lag; dc <= lag; dc++)
                        {
                            if (dr == 0 && dc == 0) goto done;
                            int c = g.ArCoeffsYPlus128[pos] - 128;
                            s += luma[(y + dr) * 82 + x + dc] * c;
                            pos++;
                        }
                    done:
                    luma[y * 82 + x] = Math.Clamp(luma[y * 82 + x] + Round2(s, arShift), grainMin, grainMax);
                }
            int chromaW = subX != 0 ? 44 : 82, chromaH = subY != 0 ? 38 : 73;
            int[]? cb = null, cr = null;
            if (!mono)
            {
                cb = new int[chromaH * chromaW];
                cr = new int[chromaH * chromaW];
                rnd = g.GrainSeed ^ 0xb524;
                for (int y = 0; y < chromaH; y++)
                    for (int x = 0; x < chromaW; x++)
                    {
                        int gv = g.NumCbPoints > 0 || g.ChromaScalingFromLuma ? gauss[GetRandom(11)] : 0;
                        cb[y * chromaW + x] = Round2(gv, shift);
                    }
                rnd = g.GrainSeed ^ 0x49d8;
                for (int y = 0; y < chromaH; y++)
                    for (int x = 0; x < chromaW; x++)
                    {
                        int gv = g.NumCrPoints > 0 || g.ChromaScalingFromLuma ? gauss[GetRandom(11)] : 0;
                        cr[y * chromaW + x] = Round2(gv, shift);
                    }
                for (int y = 3; y < chromaH; y++)
                    for (int x = 3; x < chromaW - 3; x++)
                    {
                        int s0 = 0, s1 = 0, pos = 0;
                        for (int dr = -lag; dr <= 0; dr++)
                            for (int dc = -lag; dc <= lag; dc++)
                            {
                                int c0 = g.ArCoeffsCbPlus128[pos] - 128;
                                int c1 = g.ArCoeffsCrPlus128[pos] - 128;
                                if (dr == 0 && dc == 0)
                                {
                                    if (g.NumYPoints > 0)
                                    {
                                        int l = 0;
                                        int lumaX = ((x - 3) << subX) + 3, lumaY = ((y - 3) << subY) + 3;
                                        for (int i = 0; i <= subY; i++)
                                            for (int j = 0; j <= subX; j++) l += luma[(lumaY + i) * 82 + lumaX + j];
                                        l = Round2(l, subX + subY);
                                        s0 += l * c0;
                                        s1 += l * c1;
                                    }
                                    goto cdone;
                                }
                                s0 += cb[(y + dr) * chromaW + x + dc] * c0;
                                s1 += cr[(y + dr) * chromaW + x + dc] * c1;
                                pos++;
                            }
                        cdone:
                        cb[y * chromaW + x] = Math.Clamp(cb[y * chromaW + x] + Round2(s0, arShift), grainMin, grainMax);
                        cr[y * chromaW + x] = Math.Clamp(cr[y * chromaW + x] + Round2(s1, arShift), grainMin, grainMax);
                    }
            }

            // scaling lookup (7.18.3.4)
            var lut = new int[3][];
            for (int plane = 0; plane < numPlanes; plane++)
            {
                lut[plane] = new int[256];
                int numPoints;
                int[] xs, ys;
                if (plane == 0 || g.ChromaScalingFromLuma) { numPoints = g.NumYPoints; xs = g.PointYValue; ys = g.PointYScaling; }
                else if (plane == 1) { numPoints = g.NumCbPoints; xs = g.PointCbValue; ys = g.PointCbScaling; }
                else { numPoints = g.NumCrPoints; xs = g.PointCrValue; ys = g.PointCrScaling; }
                if (numPoints == 0) continue;
                for (int x = 0; x < xs[0]; x++) lut[plane][x] = ys[0];
                for (int i = 0; i < numPoints - 1; i++)
                {
                    int deltaY = ys[i + 1] - ys[i];
                    int deltaX = xs[i + 1] - xs[i];
                    if (deltaX <= 0) continue;
                    int delta = deltaY * ((65536 + (deltaX >> 1)) / deltaX);
                    for (int x = 0; x < deltaX; x++)
                    {
                        int v = ys[i] + ((x * delta + 32768) >> 16);
                        lut[plane][xs[i] + x] = v;
                    }
                }
                for (int x = xs[numPoints - 1]; x < 256; x++) lut[plane][x] = ys[numPoints - 1];
            }
            int ScaleLut(int plane, int index)
            {
                int sh = bd - 8;
                int x = index >> sh;
                int rem = index - (x << sh);
                if (bd == 8 || x == 255) return lut[plane][x];
                int start = lut[plane][x], end = lut[plane][x + 1];
                return start + Round2((end - start) * rem, sh);
            }

            // noise stripes (7.18.3.5)
            int lumaNumMax = (h + 1) / 2 / 16 + 2;
            int stripeW = w + 64;
            var stripes = new int[numPlanes][][];
            for (int p = 0; p < numPlanes; p++) stripes[p] = new int[lumaNumMax][];
            int lumaNum = 0;
            for (int y = 0; y < (h + 1) / 2; y += 16)
            {
                rnd = g.GrainSeed;
                rnd ^= ((lumaNum * 37 + 178) & 255) << 8;
                rnd ^= (lumaNum * 173 + 105) & 255;
                for (int p = 0; p < numPlanes; p++) stripes[p][lumaNum] = new int[34 * stripeW];
                for (int x = 0; x < (w + 1) / 2; x += 16)
                {
                    int rand = GetRandom(8);
                    int offsetX = rand >> 4, offsetY = rand & 15;
                    for (int plane = 0; plane < numPlanes; plane++)
                    {
                        int psx = plane > 0 ? subX : 0, psy = plane > 0 ? subY : 0;
                        int planeOffsetX = psx != 0 ? 6 + offsetX : 9 + offsetX * 2;
                        int planeOffsetY = psy != 0 ? 6 + offsetY : 9 + offsetY * 2;
                        var ns = stripes[plane][lumaNum];
                        var grain = plane == 0 ? luma : plane == 1 ? cb! : cr!;
                        int gw = plane == 0 ? 82 : chromaW;
                        for (int i = 0; i < (34 >> psy); i++)
                            for (int j = 0; j < (34 >> psx); j++)
                            {
                                int gv = grain[(planeOffsetY + i) * gw + planeOffsetX + j];
                                if (psx == 0)
                                {
                                    int idx = i * stripeW + x * 2 + j;
                                    if (j < 2 && g.OverlapFlag && x > 0)
                                    {
                                        int old = ns[idx];
                                        gv = j == 0 ? old * 27 + gv * 17 : old * 17 + gv * 27;
                                        gv = Math.Clamp(Round2(gv, 5), grainMin, grainMax);
                                    }
                                    ns[idx] = gv;
                                }
                                else
                                {
                                    int idx = i * stripeW + x + j;
                                    if (j == 0 && g.OverlapFlag && x > 0)
                                    {
                                        int old = ns[idx];
                                        gv = old * 23 + gv * 22;
                                        gv = Math.Clamp(Round2(gv, 5), grainMin, grainMax);
                                    }
                                    ns[idx] = gv;
                                }
                            }
                    }
                }
                lumaNum++;
            }

            // noise image and blending
            var noise = new int[numPlanes][];
            for (int plane = 0; plane < numPlanes; plane++)
            {
                int psx = plane > 0 ? subX : 0, psy = plane > 0 ? subY : 0;
                int pw = (w + psx) >> psx, ph = (h + psy) >> psy;
                var ni = noise[plane] = new int[pw * ph];
                for (int y = 0; y < ph; y++)
                {
                    int ln = y >> (5 - psy);
                    int i = y - (ln << (5 - psy));
                    for (int x = 0; x < pw; x++)
                    {
                        int gv = stripes[plane][ln][i * stripeW + x];
                        if (psy == 0)
                        {
                            if (i < 2 && ln > 0 && g.OverlapFlag)
                            {
                                int old = stripes[plane][ln - 1][(i + 32) * stripeW + x];
                                gv = i == 0 ? old * 27 + gv * 17 : old * 17 + gv * 27;
                                gv = Math.Clamp(Round2(gv, 5), grainMin, grainMax);
                            }
                        }
                        else
                        {
                            if (i < 1 && ln > 0 && g.OverlapFlag)
                            {
                                int old = stripes[plane][ln - 1][(i + 16) * stripeW + x];
                                gv = old * 23 + gv * 22;
                                gv = Math.Clamp(Round2(gv, 5), grainMin, grainMax);
                            }
                        }
                        ni[y * pw + x] = gv;
                    }
                }
            }
            int minValue, maxLuma, maxChroma;
            if (g.ClipToRestrictedRange)
            {
                minValue = 16 << (bd - 8);
                maxLuma = 235 << (bd - 8);
                maxChroma = seq.MatrixCoefficients == Av1C.McIdentity ? maxLuma : 240 << (bd - 8);
            }
            else
            {
                minValue = 0;
                maxLuma = (256 << (bd - 8)) - 1;
                maxChroma = maxLuma;
            }
            int scalingShift = g.GrainScalingMinus8 + 8;
            var Y = input.Planes[0];
            int ys0 = input.Stride[0];
            int maxPix = (1 << bd) - 1;
            if (!mono)
            {
                var U = o.Planes[1]; var V = o.Planes[2];
                int cs = o.Stride[1];
                int pw = (w + subX) >> subX, ph = (h + subY) >> subY;
                for (int y = 0; y < ph; y++)
                    for (int x = 0; x < pw; x++)
                    {
                        int lumaX = x << subX, lumaY = y << subY;
                        int lumaNextX = Math.Min(lumaX + 1, w - 1);
                        int averageLuma = subX != 0 ? Round2(Y[lumaY * ys0 + lumaX] + Y[lumaY * ys0 + lumaNextX], 1) : Y[lumaY * ys0 + lumaX];
                        if (g.NumCbPoints > 0 || g.ChromaScalingFromLuma)
                        {
                            int orig = U[y * cs + x];
                            int merged;
                            if (g.ChromaScalingFromLuma) merged = averageLuma;
                            else
                            {
                                int combined = averageLuma * (g.CbLumaMult - 128) + orig * (g.CbMult - 128);
                                merged = Math.Clamp((combined >> 6) + ((g.CbOffset - 256) << (bd - 8)), 0, maxPix);
                            }
                            int nz = noise[1][y * pw + x];
                            nz = Round2(ScaleLut(1, merged) * nz, scalingShift);
                            U[y * cs + x] = (ushort)Math.Clamp(orig + nz, minValue, maxChroma);
                        }
                        if (g.NumCrPoints > 0 || g.ChromaScalingFromLuma)
                        {
                            int orig = V[y * cs + x];
                            int merged;
                            if (g.ChromaScalingFromLuma) merged = averageLuma;
                            else
                            {
                                int combined = averageLuma * (g.CrLumaMult - 128) + orig * (g.CrMult - 128);
                                merged = Math.Clamp((combined >> 6) + ((g.CrOffset - 256) << (bd - 8)), 0, maxPix);
                            }
                            int nz = noise[2][y * pw + x];
                            nz = Round2(ScaleLut(2, merged) * nz, scalingShift);
                            V[y * cs + x] = (ushort)Math.Clamp(orig + nz, minValue, maxChroma);
                        }
                    }
            }
            if (g.NumYPoints > 0)
            {
                var OY = o.Planes[0];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int orig = Y[y * ys0 + x];
                        int nz = noise[0][y * w + x];
                        nz = Round2(ScaleLut(0, orig) * nz, scalingShift);
                        OY[y * ys0 + x] = (ushort)Math.Clamp(orig + nz, minValue, maxLuma);
                    }
            }
            return o;
        }

        static int Round2(int x, int n) => n == 0 ? x : (x + (1 << (n - 1))) >> n;
    }
}
