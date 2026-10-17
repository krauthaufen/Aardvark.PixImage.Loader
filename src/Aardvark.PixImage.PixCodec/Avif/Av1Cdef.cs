using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
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
            int yDir = 0, var = 0;
            // the direction only matters with a primary strength
            if (h.CdefYPriStrength[idx] != 0 || s.NumPlanes > 1 && h.CdefUvPriStrength[idx] != 0) Direction(f, cur, r, c, out yDir, out var);
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

        static int Constrain(int diff, int threshold, int dampingAdj)
        {
            int ad = Math.Abs(diff);
            int v = Math.Clamp(threshold - (ad >> dampingAdj), 0, ad);
            return diff < 0 ? -v : v;
        }

        /// <summary>Marks samples outside the frame in the padded block (CdefAvailable is false).</summary>
        const short Na = 30000;

        /// <summary>
        /// The CDEF filter process (7.15.2) for one 8x8 block of a plane. The block and a 2 sample border are copied into a
        /// padded buffer first, unavailable samples marked, so that each tap is one load; a row of the block is filtered at
        /// once with 16 bit lanes. The mark is so large that the constrained difference to it is 0 (|d| &gt;&gt; adj exceeds
        /// any threshold: threshold &lt;&lt; adj &lt;= 2^(damping + 1) &lt;= 2^11), and it is left out of the maximum.
        /// </summary>
        static void Filter(Av1FrameState f, Av1Frame cur, Av1Frame outF, int plane, int r, int c, int priStr, int secStr, int damping, int dir)
        {
            // with both strengths 0 the sum is 0 and the sample stays as it is
            if (priStr == 0 && secStr == 0) return;
            var s = f.Seq;
            int coeffShift = s.BitDepth - 8;
            int sx = plane > 0 ? s.SubsamplingX : 0, sy = plane > 0 ? s.SubsamplingY : 0;
            int x0 = (c * Av1C.MiSize) >> sx, y0 = (r * Av1C.MiSize) >> sy;
            int w = 8 >> sx, hh = 8 >> sy;
            var src = cur.Planes[plane];
            var dst = outF.Planes[plane];
            int stride = cur.Stride[plane];
            const int B = 12;
            Span<short> buf = stackalloc short[B * B + 16];
            // available: inside the frame in units of 4x4 luma blocks
            int maxY = (f.MiRows << Av1C.MiSizeLog2) >> sy, maxX = (f.MiCols << Av1C.MiSizeLog2) >> sx;
            if (y0 >= 2 && y0 + hh + 2 <= maxY && x0 >= 2 && x0 + w + 2 <= maxX)
            {
                // inside the frame: plain row copies (samples are below 2^15)
                var s16 = MemoryMarshal.Cast<ushort, short>(src.AsSpan());
                for (int i = -2; i < hh + 2; i++)
                    s16.Slice((y0 + i) * stride + x0 - 2, w + 4).CopyTo(buf.Slice((i + 2) * B, w + 4));
            }
            else
                for (int i = -2; i < hh + 2; i++)
                {
                    int yy = y0 + i;
                    int bo = (i + 2) * B + 2;
                    bool rowOk = yy >= 0 && yy < maxY;
                    for (int j = -2; j < w + 2; j++)
                    {
                        int xx = x0 + j;
                        buf[bo + j] = rowOk && xx >= 0 && xx < maxX ? (short)src[yy * stride + xx] : Na;
                    }
                }
            int priTap = (priStr >> coeffShift) & 1;
            int p0 = T.CdefPriTaps[priTap * 2], p1 = T.CdefPriTaps[priTap * 2 + 1];
            int s0 = T.CdefSecTaps[priTap * 2], s1 = T.CdefSecTaps[priTap * 2 + 1];
            static int Off(int d, int k) => T.CdefDirections[(d * 2 + k) * 2] * B + T.CdefDirections[(d * 2 + k) * 2 + 1];
            int po0 = Off(dir, 0), po1 = Off(dir, 1);
            int da = (dir - 2) & 7, db = (dir + 2) & 7;
            int sa0 = Off(da, 0), sa1 = Off(da, 1), sb0 = Off(db, 0), sb1 = Off(db, 1);
            int priAdj = priStr != 0 ? Math.Max(0, damping - Av1BitReader.FloorLog2(priStr)) : 0;
            int secAdj = secStr != 0 ? Math.Max(0, damping - Av1BitReader.FloorLog2(secStr)) : 0;
            if (Vector128.IsHardwareAccelerated)
            {
                ref short b0 = ref MemoryMarshal.GetReference(buf);
                var vPri = Vector128.Create((short)priStr);
                var vSec = Vector128.Create((short)secStr);
                var na = Vector128.Create(Na);
                for (int i = 0; i < hh; i++)
                {
                    int ci = (i + 2) * B + 2;
                    var x = Vector128.LoadUnsafe(ref b0, (nuint)ci);
                    var sum = Vector128<short>.Zero;
                    Vector128<short> min = x, max = x;
                    if (priStr != 0)
                    {
                        TapV(ref b0, ci + po0, x, vPri, priAdj, (short)p0, ref sum, ref min, ref max, na);
                        TapV(ref b0, ci - po0, x, vPri, priAdj, (short)p0, ref sum, ref min, ref max, na);
                        TapV(ref b0, ci + po1, x, vPri, priAdj, (short)p1, ref sum, ref min, ref max, na);
                        TapV(ref b0, ci - po1, x, vPri, priAdj, (short)p1, ref sum, ref min, ref max, na);
                    }
                    else
                    {
                        MinMaxV(ref b0, ci + po0, ref min, ref max, na);
                        MinMaxV(ref b0, ci - po0, ref min, ref max, na);
                        MinMaxV(ref b0, ci + po1, ref min, ref max, na);
                        MinMaxV(ref b0, ci - po1, ref min, ref max, na);
                    }
                    if (secStr != 0)
                    {
                        TapV(ref b0, ci + sa0, x, vSec, secAdj, (short)s0, ref sum, ref min, ref max, na);
                        TapV(ref b0, ci - sa0, x, vSec, secAdj, (short)s0, ref sum, ref min, ref max, na);
                        TapV(ref b0, ci + sb0, x, vSec, secAdj, (short)s0, ref sum, ref min, ref max, na);
                        TapV(ref b0, ci - sb0, x, vSec, secAdj, (short)s0, ref sum, ref min, ref max, na);
                        TapV(ref b0, ci + sa1, x, vSec, secAdj, (short)s1, ref sum, ref min, ref max, na);
                        TapV(ref b0, ci - sa1, x, vSec, secAdj, (short)s1, ref sum, ref min, ref max, na);
                        TapV(ref b0, ci + sb1, x, vSec, secAdj, (short)s1, ref sum, ref min, ref max, na);
                        TapV(ref b0, ci - sb1, x, vSec, secAdj, (short)s1, ref sum, ref min, ref max, na);
                    }
                    else
                    {
                        MinMaxV(ref b0, ci + sa0, ref min, ref max, na);
                        MinMaxV(ref b0, ci - sa0, ref min, ref max, na);
                        MinMaxV(ref b0, ci + sb0, ref min, ref max, na);
                        MinMaxV(ref b0, ci - sb0, ref min, ref max, na);
                        MinMaxV(ref b0, ci + sa1, ref min, ref max, na);
                        MinMaxV(ref b0, ci - sa1, ref min, ref max, na);
                        MinMaxV(ref b0, ci + sb1, ref min, ref max, na);
                        MinMaxV(ref b0, ci - sb1, ref min, ref max, na);
                    }
                    // x + ((8 + sum - (sum < 0)) >> 4), clamped to min .. max; the comparison mask is -1 where sum < 0
                    var neg = Vector128.LessThan(sum, Vector128<short>.Zero);
                    var res = x + Vector128.ShiftRightArithmetic(sum + Vector128.Create((short)8) + neg, 4);
                    res = Vector128.Max(Vector128.Min(res, max), min);
                    var d = dst.AsSpan((y0 + i) * stride + x0, w);
                    if (w == 8) res.AsUInt16().StoreUnsafe(ref MemoryMarshal.GetReference(d));
                    else res.AsUInt16().GetLower().StoreUnsafe(ref MemoryMarshal.GetReference(d));
                }
                return;
            }
            for (int i = 0; i < hh; i++)
            {
                int row = (y0 + i) * stride + x0;
                for (int j = 0; j < w; j++)
                {
                    int ci = (i + 2) * B + j + 2;
                    int x = buf[ci];
                    int sum = 0, max = x, min = x;
                    static void Tap(Span<short> b, int idx, int tap, int str, int adj, int x, ref int sum, ref int min, ref int max)
                    {
                        int p = b[idx];
                        if (p == Na) return;
                        if (str != 0) sum += tap * Constrain(p - x, str, adj);
                        if (p > max) max = p;
                        if (p < min) min = p;
                    }
                    Tap(buf, ci + po0, p0, priStr, priAdj, x, ref sum, ref min, ref max);
                    Tap(buf, ci - po0, p0, priStr, priAdj, x, ref sum, ref min, ref max);
                    Tap(buf, ci + po1, p1, priStr, priAdj, x, ref sum, ref min, ref max);
                    Tap(buf, ci - po1, p1, priStr, priAdj, x, ref sum, ref min, ref max);
                    Tap(buf, ci + sa0, s0, secStr, secAdj, x, ref sum, ref min, ref max);
                    Tap(buf, ci - sa0, s0, secStr, secAdj, x, ref sum, ref min, ref max);
                    Tap(buf, ci + sb0, s0, secStr, secAdj, x, ref sum, ref min, ref max);
                    Tap(buf, ci - sb0, s0, secStr, secAdj, x, ref sum, ref min, ref max);
                    Tap(buf, ci + sa1, s1, secStr, secAdj, x, ref sum, ref min, ref max);
                    Tap(buf, ci - sa1, s1, secStr, secAdj, x, ref sum, ref min, ref max);
                    Tap(buf, ci + sb1, s1, secStr, secAdj, x, ref sum, ref min, ref max);
                    Tap(buf, ci - sb1, s1, secStr, secAdj, x, ref sum, ref min, ref max);
                    dst[row + j] = (ushort)Math.Clamp(x + ((8 + sum - (sum < 0 ? 1 : 0)) >> 4), min, max);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void TapV(ref short b0, int idx, Vector128<short> x, Vector128<short> str, int adj, short tap,
                         ref Vector128<short> sum, ref Vector128<short> min, ref Vector128<short> max, Vector128<short> na)
        {
            var p = Vector128.LoadUnsafe(ref b0, (nuint)idx);
            var d = p - x;
            var ad = Vector128.Abs(d);
            var v = Vector128.Min(ad, Vector128.Max(Vector128<short>.Zero, str - Vector128.ShiftRightArithmetic(ad, adj)));
            sum += Vector128.ConditionalSelect(Vector128.LessThan(d, Vector128<short>.Zero), -v, v) * tap;
            min = Vector128.Min(min, p);
            max = Vector128.Max(max, Vector128.AndNot(p, Vector128.Equals(p, na)));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void MinMaxV(ref short b0, int idx, ref Vector128<short> min, ref Vector128<short> max, Vector128<short> na)
        {
            var p = Vector128.LoadUnsafe(ref b0, (nuint)idx);
            min = Vector128.Min(min, p);
            max = Vector128.Max(max, Vector128.AndNot(p, Vector128.Equals(p, na)));
        }
    }
}
