using System;
using System.Threading.Tasks;
using T = Aardvark.ImageCodecs.Av1.Av1Tables;

namespace Aardvark.ImageCodecs.Av1
{
    /// <summary>The constrained directional enhancement filter of AV1 spec 7.15: reads CurrFrame, returns CdefFrame.</summary>
    internal static class Av1Cdef
    {
        public static Av1Frame Apply(Av1FrameState f, bool multithreaded)
        {
            var cur = f.Cur;
            var outF = cur.Clone();
            int rows8 = (f.MiRows + 1) / 2;
            void Row(int r8)
            {
                int r = r8 * 2;
                for (int c = 0; c < f.MiCols; c += 2)
                {
                    int idx = f.CdefIdx[(r >> 4) * f.CdefStride + (c >> 4)];
                    if (idx != -1) Block(f, cur, outF, r, c, idx);
                }
            }
            if (multithreaded && rows8 > 8) Parallel.For(0, rows8, Row);
            else for (int r8 = 0; r8 < rows8; r8++) Row(r8);
            return outF;
        }

        static bool Skip(Av1FrameState f, int r, int c)
        {
            bool Sk(int rr, int cc) => rr >= f.MiRows || cc >= f.MiCols || f.BlockAtMi(rr, cc).Skip;
            return Sk(r, c) && Sk(r + 1, c) && Sk(r, c + 1) && Sk(r + 1, c + 1);
        }

        static void Block(Av1FrameState f, Av1Frame cur, Av1Frame outF, int r, int c, int idx)
        {
            var h = f.Hdr;
            var s = f.Seq;
            int coeffShift = s.BitDepth - 8;
            if (Skip(f, r, c)) return;
            Direction(f, cur, r, c, out int yDir, out int var);
            int priStr = h.CdefYPriStrength[idx] << coeffShift;
            int secStr = h.CdefYSecStrength[idx] << coeffShift;
            int dir = priStr == 0 ? 0 : yDir;
            int varStr = (var >> 6) != 0 ? Math.Min(Av1BitReader.FloorLog2(var >> 6), 12) : 0;
            priStr = var != 0 ? (priStr * (4 + varStr) + 8) >> 4 : 0;
            int damping = h.CdefDamping + coeffShift;
            Filter(f, cur, outF, 0, r, c, priStr, secStr, damping, dir);
            if (s.NumPlanes == 1) return;
            priStr = h.CdefUvPriStrength[idx] << coeffShift;
            secStr = h.CdefUvSecStrength[idx] << coeffShift;
            dir = priStr == 0 ? 0 : T.CdefUvDir[(s.SubsamplingX * 2 + s.SubsamplingY) * 8 + yDir];
            damping = h.CdefDamping + coeffShift - 1;
            Filter(f, cur, outF, 1, r, c, priStr, secStr, damping, dir);
            Filter(f, cur, outF, 2, r, c, priStr, secStr, damping, dir);
        }

        static void Direction(Av1FrameState f, Av1Frame cur, int r, int c, out int yDir, out int var)
        {
            Span<int> cost = stackalloc int[8];
            Span<int> partial = stackalloc int[8 * 15];
            cost.Clear();
            partial.Clear();
            int bestCost = 0;
            yDir = 0;
            int x0 = c << Av1C.MiSizeLog2, y0 = r << Av1C.MiSizeLog2;
            var p = cur.Planes[0];
            int stride = cur.Stride[0];
            int sh = f.Seq.BitDepth - 8;
            for (int i = 0; i < 8; i++)
                for (int j = 0; j < 8; j++)
                {
                    int x = (p[(y0 + i) * stride + x0 + j] >> sh) - 128;
                    partial[0 * 15 + i + j] += x;
                    partial[1 * 15 + i + j / 2] += x;
                    partial[2 * 15 + i] += x;
                    partial[3 * 15 + 3 + i - j / 2] += x;
                    partial[4 * 15 + 7 + i - j] += x;
                    partial[5 * 15 + 3 - i / 2 + j] += x;
                    partial[6 * 15 + j] += x;
                    partial[7 * 15 + i / 2 + j] += x;
                }
            var div = T.DivTable;
            for (int i = 0; i < 8; i++)
            {
                cost[2] += partial[2 * 15 + i] * partial[2 * 15 + i];
                cost[6] += partial[6 * 15 + i] * partial[6 * 15 + i];
            }
            cost[2] *= div[8];
            cost[6] *= div[8];
            for (int i = 0; i < 7; i++)
            {
                cost[0] += (partial[i] * partial[i] + partial[14 - i] * partial[14 - i]) * div[i + 1];
                cost[4] += (partial[4 * 15 + i] * partial[4 * 15 + i] + partial[4 * 15 + 14 - i] * partial[4 * 15 + 14 - i]) * div[i + 1];
            }
            cost[0] += partial[7] * partial[7] * div[8];
            cost[4] += partial[4 * 15 + 7] * partial[4 * 15 + 7] * div[8];
            for (int i = 1; i < 8; i += 2)
            {
                for (int j = 0; j < 5; j++) cost[i] += partial[i * 15 + 3 + j] * partial[i * 15 + 3 + j];
                cost[i] *= div[8];
                for (int j = 0; j < 3; j++)
                    cost[i] += (partial[i * 15 + j] * partial[i * 15 + j] + partial[i * 15 + 10 - j] * partial[i * 15 + 10 - j]) * div[2 * j + 2];
            }
            for (int i = 0; i < 8; i++)
                if (cost[i] > bestCost) { bestCost = cost[i]; yDir = i; }
            var = (bestCost - cost[(yDir + 4) & 7]) >> 10;
        }

        static int Constrain(int diff, int threshold, int damping)
        {
            if (threshold == 0) return 0;
            int dampingAdj = Math.Max(0, damping - Av1BitReader.FloorLog2(threshold));
            int ad = Math.Abs(diff);
            int v = Math.Clamp(threshold - (ad >> dampingAdj), 0, ad);
            return diff < 0 ? -v : v;
        }

        static void Filter(Av1FrameState f, Av1Frame cur, Av1Frame outF, int plane, int r, int c, int priStr, int secStr, int damping, int dir)
        {
            var s = f.Seq;
            int coeffShift = s.BitDepth - 8;
            int sx = plane > 0 ? s.SubsamplingX : 0, sy = plane > 0 ? s.SubsamplingY : 0;
            int x0 = (c * Av1C.MiSize) >> sx, y0 = (r * Av1C.MiSize) >> sy;
            int w = 8 >> sx, hh = 8 >> sy;
            var src = cur.Planes[plane];
            var dst = outF.Planes[plane];
            int stride = cur.Stride[plane];
            var dirs = T.CdefDirections;
            int priTap = (priStr >> coeffShift) & 1;
            int miRows = f.MiRows, miCols = f.MiCols;
            for (int i = 0; i < hh; i++)
                for (int j = 0; j < w; j++)
                {
                    int sum = 0;
                    int x = src[(y0 + i) * stride + x0 + j];
                    int max = x, min = x;
                    for (int k = 0; k < 2; k++)
                        for (int sign = -1; sign <= 1; sign += 2)
                        {
                            {
                                int yy = y0 + i + sign * dirs[(dir * 2 + k) * 2];
                                int xx = x0 + j + sign * dirs[(dir * 2 + k) * 2 + 1];
                                int candR = (yy << sy) >> Av1C.MiSizeLog2, candC = (xx << sx) >> Av1C.MiSizeLog2;
                                if (yy >= 0 && xx >= 0 && candR < miRows && candC < miCols)
                                {
                                    int p = src[yy * stride + xx];
                                    sum += T.CdefPriTaps[priTap * 2 + k] * Constrain(p - x, priStr, damping);
                                    max = Math.Max(p, max);
                                    min = Math.Min(p, min);
                                }
                            }
                            for (int dirOff = -2; dirOff <= 2; dirOff += 4)
                            {
                                int d2 = (dir + dirOff) & 7;
                                int yy = y0 + i + sign * dirs[(d2 * 2 + k) * 2];
                                int xx = x0 + j + sign * dirs[(d2 * 2 + k) * 2 + 1];
                                int candR = (yy << sy) >> Av1C.MiSizeLog2, candC = (xx << sx) >> Av1C.MiSizeLog2;
                                if (yy >= 0 && xx >= 0 && candR < miRows && candC < miCols)
                                {
                                    int sv = src[yy * stride + xx];
                                    sum += T.CdefSecTaps[priTap * 2 + k] * Constrain(sv - x, secStr, damping);
                                    max = Math.Max(sv, max);
                                    min = Math.Min(sv, min);
                                }
                            }
                        }
                    dst[(y0 + i) * stride + x0 + j] = (ushort)Math.Clamp(x + ((8 + sum - (sum < 0 ? 1 : 0)) >> 4), min, max);
                }
        }
    }
}
