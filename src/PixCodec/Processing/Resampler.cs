using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Threading.Tasks;

namespace PixCodec.Processing
{
    /// <summary>
    /// Weights of a 1D resampling from n source to m destination pixels. Pixel i covers [i, i + 1), so destination pixel j is
    /// centred on source position (j + 0.5) * n / m. When downscaling, the filter is stretched by n / m. Taps outside the
    /// source are dropped and the remaining weights renormalised.
    /// </summary>
    internal sealed class AxisWeights
    {
        public readonly int[] Start;
        public readonly int[] Count;
        public readonly double[] Weights; // MaxTaps per destination pixel
        public readonly int MaxTaps;

        AxisWeights(int[] start, int[] count, double[] weights, int maxTaps)
        {
            Start = start; Count = count; Weights = weights; MaxTaps = maxTaps;
        }

        public static AxisWeights Compute(int srcN, int dstN, ResampleFilter filter)
        {
            var start = new int[dstN];
            var count = new int[dstN];
            double ratio = (double)srcN / dstN;
            if (filter.IsNearest)
            {
                var w1 = new double[dstN];
                for (int j = 0; j < dstN; j++)
                {
                    start[j] = Math.Min((int)Math.Floor((j + 0.5) * ratio), srcN - 1);
                    count[j] = 1;
                    w1[j] = 1;
                }
                return new AxisWeights(start, count, w1, 1);
            }

            double fs = Math.Max(1, ratio);
            double support = filter.Support * fs;
            int maxTaps = Math.Min(srcN, (int)Math.Ceiling(2 * support) + 2);
            var w = new double[dstN * maxTaps];
            var tmp = new double[(int)Math.Ceiling(2 * support) + 3];
            int used = 1;
            for (int j = 0; j < dstN; j++)
            {
                double c = (j + 0.5) * ratio;
                int lo = Math.Max(0, (int)Math.Floor(c - support));
                int hi = Math.Min(srcN, (int)Math.Ceiling(c + support));
                double sum = 0;
                int n = 0;
                for (int i = lo; i < hi; i++)
                {
                    double v = filter.Evaluate((i + 0.5 - c) / fs);
                    tmp[n++] = v;
                    sum += v;
                }
                // trim zero weights at both ends
                int a = 0, b = n;
                while (a < b && tmp[a] == 0) a++;
                while (b > a && tmp[b - 1] == 0) b--;
                if (a == b || sum == 0)
                {
                    start[j] = Math.Clamp((int)Math.Floor(c), 0, srcN - 1);
                    count[j] = 1;
                    w[j * maxTaps] = 1;
                    continue;
                }
                start[j] = lo + a;
                count[j] = b - a;
                used = Math.Max(used, b - a);
                for (int k = a; k < b; k++) w[j * maxTaps + k - a] = tmp[k] / sum;
            }
            return new AxisWeights(start, count, w, maxTaps);
        }

        public TF[] Typed<TF>() where TF : unmanaged, IFloatingPointIeee754<TF>
        {
            var r = new TF[Weights.Length];
            for (int i = 0; i < r.Length; i++) r[i] = TF.CreateTruncating(Weights[i]);
            return r;
        }

        /// <summary>True if every destination pixel is a copy of the source pixel at the same index.</summary>
        public bool IsIdentity
        {
            get
            {
                for (int j = 0; j < Start.Length; j++)
                    if (Start[j] != j || Count[j] != 1 || Weights[j * MaxTaps] != 1) return false;
                return true;
            }
        }
    }

    /// <summary>Separable resampling (horizontal pass into a float band, then vertical), parallel over bands of output rows.</summary>
    internal static class Resampler
    {
        public static RawImage Resize(RawImage src, int width, int height, ResizeOptions o)
        {
            var ax = AxisWeights.Compute(src.Width, width, o.Filter);
            var ay = AxisWeights.Compute(src.Height, height, o.Filter);
            var dst = RawImage.Create(width, height, src.Color, src.SampleType, src.Channels);
            int alpha = o.AlphaChannel ?? (src.HasAlpha ? src.Channels - 1 : -1);
            if (alpha >= src.Channels) throw new ArgumentOutOfRangeException(nameof(o.AlphaChannel));
            var job = new Job(src.Width, src.Height, src.Channels, width, height, ax, ay, o.PremultiplyAlpha ? alpha : -1, alpha,
                o.LinearLight, o.MaxDegreeOfParallelism);
            switch (src.SampleType)
            {
                // by SampleType, not by array pattern: sbyte[] also matches `byte[]` (CLR array covariance)
                case SampleType.UInt8: Run<byte, float>(job, (byte[])src.Data, (byte[])dst.Data); break;
                case SampleType.Int8: Run<sbyte, float>(job, (sbyte[])src.Data, (sbyte[])dst.Data); break;
                case SampleType.UInt16: Run<ushort, float>(job, (ushort[])src.Data, (ushort[])dst.Data); break;
                case SampleType.Int16: Run<short, float>(job, (short[])src.Data, (short[])dst.Data); break;
                case SampleType.UInt32: Run<uint, double>(job, (uint[])src.Data, (uint[])dst.Data); break;
                case SampleType.Int32: Run<int, double>(job, (int[])src.Data, (int[])dst.Data); break;
                case SampleType.UInt64: Run<ulong, double>(job, (ulong[])src.Data, (ulong[])dst.Data); break;
                case SampleType.Int64: Run<long, double>(job, (long[])src.Data, (long[])dst.Data); break;
                case SampleType.Float16: Run<Half, float>(job, (Half[])src.Data, (Half[])dst.Data); break;
                case SampleType.Float32: Run<float, float>(job, (float[])src.Data, (float[])dst.Data); break;
                case SampleType.Float64: Run<double, double>(job, (double[])src.Data, (double[])dst.Data); break;
                default: throw new NotSupportedException(src.SampleType.ToString());
            }
            return dst;
        }

        internal sealed record Job(int SW, int SH, int NC, int DW, int DH, AxisWeights AX, AxisWeights AY,
            int PremulAlpha, int Alpha, bool Linear, int MaxParallel);

        static void Run<TS, TF>(Job j, TS[] src, TS[] dst)
            where TS : unmanaged, INumberBase<TS> where TF : unmanaged, IFloatingPointIeee754<TF>
        {
            int nc = j.NC;
            if (j.AX.Count[0] == 1 && j.AX.MaxTaps == 1 && j.AY.MaxTaps == 1)
            {
                // nearest neighbour: plain copies, exact for every type
                Parallel.For(0, j.DH, Par(j.MaxParallel), y =>
                {
                    int sy = j.AY.Start[y];
                    var srow = src.AsSpan(sy * j.SW * nc, j.SW * nc);
                    var drow = dst.AsSpan(y * j.DW * nc, j.DW * nc);
                    for (int x = 0; x < j.DW; x++)
                        srow.Slice(j.AX.Start[x] * nc, nc).CopyTo(drow.Slice(x * nc, nc));
                });
                return;
            }

            var wx = j.AX.Typed<TF>();
            var wy = j.AY.Typed<TF>();
            TF[]? decode = null;
            byte[]? encode8 = null;
            bool linear = j.Linear && Samples.TableSize<TS>() > 0;
            if (linear)
            {
                decode = Samples.SrgbToLinear<TF>(Samples.TableSize<TS>());
                if (typeof(TS) == typeof(byte))
                {
                    encode8 = new byte[EncodeTableSize];
                    for (int i = 0; i < encode8.Length; i++)
                        encode8[i] = (byte)Math.Floor(Samples.SrgbEncode(i / (double)(EncodeTableSize - 1)) * 255 + 0.5);
                }
            }
            bool skipH = j.AX.IsIdentity;

            // bands of output rows; each band filters the source rows it needs horizontally, then vertically
            int procs = j.MaxParallel > 0 ? j.MaxParallel : Environment.ProcessorCount;
            int bandRows = Math.Max(16, (j.DH + procs * 2 - 1) / (procs * 2));
            int bands = (j.DH + bandRows - 1) / bandRows;
            Parallel.For(0, bands, Par(j.MaxParallel), band =>
            {
                int y0 = band * bandRows, y1 = Math.Min(j.DH, y0 + bandRows);
                int r0 = int.MaxValue, r1 = 0;
                for (int y = y0; y < y1; y++)
                {
                    r0 = Math.Min(r0, j.AY.Start[y]);
                    r1 = Math.Max(r1, j.AY.Start[y] + j.AY.Count[y]);
                }
                int rowLen = j.DW * nc;
                // (+4: the SIMD paths for 3 channels read and write one sample past a pixel)
                var tmp = new TF[(r1 - r0) * rowLen + 4];
                var line = new TF[j.SW * nc + 4];
                for (int r = r0; r < r1; r++)
                {
                    LoadRow<TS, TF>(src.AsSpan(r * j.SW * nc, j.SW * nc), line, nc, j.PremulAlpha, j.Alpha, decode);
                    var outRow = tmp.AsSpan((r - r0) * rowLen, rowLen);
                    if (skipH) line.AsSpan(0, rowLen).CopyTo(outRow);
                    else if (typeof(TF) == typeof(float) && (nc == 3 || nc == 4) && Vector128.IsHardwareAccelerated)
                        HorizontalSimd((float[])(object)line, (float[])(object)tmp, (r - r0) * rowLen, nc, j.AX, (float[])(object)wx, j.DW);
                    else Horizontal(line, outRow, nc, j.AX, wx);
                }
                var acc = new TF[rowLen];
                for (int y = y0; y < y1; y++)
                {
                    int s = j.AY.Start[y], n = j.AY.Count[y], wp = y * j.AY.MaxTaps;
                    var a = acc.AsSpan();
                    Scale(tmp.AsSpan((s - r0) * rowLen, rowLen), wy[wp], a);
                    for (int k = 1; k < n; k++) MulAdd(tmp.AsSpan((s + k - r0) * rowLen, rowLen), wy[wp + k], a);
                    StoreRow(a, dst.AsSpan(y * rowLen, rowLen), nc, j.PremulAlpha, j.Alpha, decode != null, encode8);
                }
            });
        }

        const int EncodeTableSize = 1 << 16;

        internal static ParallelOptions Par(int max) => new ParallelOptions { MaxDegreeOfParallelism = max > 0 ? max : -1 };

        static void LoadRow<TS, TF>(ReadOnlySpan<TS> src, Span<TF> dst, int nc, int premul, int alpha, TF[]? decode)
            where TS : unmanaged, INumberBase<TS> where TF : unmanaged, IFloatingPointIeee754<TF>
        {
            if (decode != null)
            {
                for (int i = 0; i < src.Length; i++) dst[i] = decode[int.CreateTruncating(src[i])];
                if (alpha >= 0)
                    for (int i = alpha; i < src.Length; i += nc) dst[i] = Samples.Load<TS, TF>(src[i]);
            }
            else if (typeof(TS) == typeof(byte) && typeof(TF) == typeof(float))
                RowConvert.Widen(MemoryMarshal.Cast<TS, byte>(src), MemoryMarshal.Cast<TF, float>(dst));
            else if (typeof(TS) == typeof(ushort) && typeof(TF) == typeof(float))
                RowConvert.Widen(MemoryMarshal.Cast<TS, ushort>(src), MemoryMarshal.Cast<TF, float>(dst));
            else if (typeof(TS) == typeof(float) && typeof(TF) == typeof(float))
                MemoryMarshal.Cast<TS, TF>(src).CopyTo(dst);
            else
            {
                for (int i = 0; i < src.Length; i++) dst[i] = Samples.Load<TS, TF>(src[i]);
            }
            if (premul == 3 && nc == 4 && typeof(TF) == typeof(float) && Vector128.IsHardwareAccelerated)
            {
                // r g b a -> r*a g*a b*a a
                var d = MemoryMarshal.Cast<TF, float>(dst);
                ref float d0 = ref MemoryMarshal.GetReference(d);
                var keepA = Vector128.Create(0, 0, 0, -1).AsSingle();
                for (int p = 0; p < src.Length; p += 4)
                {
                    var v = Vector128.LoadUnsafe(ref d0, (nuint)p);
                    var a = Vector128.Create(v.GetElement(3));
                    (v * Vector128.ConditionalSelect(keepA, Vector128<float>.One, a)).StoreUnsafe(ref d0, (nuint)p);
                }
            }
            else if (premul >= 0)
            {
                for (int p = 0; p < src.Length; p += nc)
                {
                    TF a = dst[p + premul];
                    for (int c = 0; c < nc; c++)
                        if (c != premul) dst[p + c] *= a;
                }
            }
        }

        static void StoreRow<TS, TF>(Span<TF> src, Span<TS> dst, int nc, int premul, int alpha, bool linear, byte[]? encode8)
            where TS : unmanaged, INumberBase<TS> where TF : unmanaged, IFloatingPointIeee754<TF>
        {
            if (premul == 3 && nc == 4 && typeof(TF) == typeof(float) && Vector128.IsHardwareAccelerated)
            {
                var d = MemoryMarshal.Cast<TF, float>(src);
                ref float d0 = ref MemoryMarshal.GetReference(d);
                var keepA = Vector128.Create(0, 0, 0, -1).AsSingle();
                for (int p = 0; p < src.Length; p += 4)
                {
                    var v = Vector128.LoadUnsafe(ref d0, (nuint)p);
                    float a = v.GetElement(3);
                    var f = a > 0 ? Vector128.ConditionalSelect(keepA, Vector128<float>.One, Vector128.Create(1 / a)) : Vector128<float>.Zero;
                    (v * f).StoreUnsafe(ref d0, (nuint)p);
                }
            }
            else if (premul >= 0)
            {
                for (int p = 0; p < src.Length; p += nc)
                {
                    TF a = src[p + premul];
                    if (a > TF.Zero)
                    {
                        TF inv = TF.One / a;
                        for (int c = 0; c < nc; c++)
                            if (c != premul) src[p + c] *= inv;
                    }
                    else
                    {
                        for (int c = 0; c < nc; c++) src[p + c] = TF.Zero;
                    }
                }
            }
            if (!linear)
            {
                if (typeof(TS) == typeof(byte) && typeof(TF) == typeof(float))
                    RowConvert.Narrow(MemoryMarshal.Cast<TF, float>(src), MemoryMarshal.Cast<TS, byte>(dst));
                else if (typeof(TS) == typeof(ushort) && typeof(TF) == typeof(float))
                    RowConvert.Narrow(MemoryMarshal.Cast<TF, float>(src), MemoryMarshal.Cast<TS, ushort>(dst));
                else if (typeof(TS) == typeof(float) && typeof(TF) == typeof(float))
                    MemoryMarshal.Cast<TF, TS>(src).CopyTo(dst);
                else
                    for (int i = 0; i < src.Length; i++) dst[i] = Samples.Store<TS, TF>(src[i]);
                return;
            }
            double max = typeof(TS) == typeof(byte) ? 255 : 65535;
            for (int i = 0; i < src.Length; i++)
            {
                if (i % nc == alpha) { dst[i] = Samples.Store<TS, TF>(src[i]); continue; }
                double v = Math.Clamp(double.CreateTruncating(src[i]), 0, 1);
                if (encode8 != null) dst[i] = TS.CreateTruncating(encode8[(int)(v * (EncodeTableSize - 1) + 0.5)]);
                else dst[i] = TS.CreateTruncating(Math.Floor(Samples.SrgbEncode(v) * max + 0.5));
            }
        }

        static void Horizontal<TF>(ReadOnlySpan<TF> src, Span<TF> dst, int nc, AxisWeights ax, TF[] w)
            where TF : unmanaged, IFloatingPointIeee754<TF>
        {
            int m = ax.MaxTaps, dw = dst.Length / nc;
            var start = ax.Start;
            var count = ax.Count;
            switch (nc)
            {
                case 1:
                    for (int x = 0; x < dw; x++)
                    {
                        int s = start[x], n = count[x], wp = x * m;
                        TF a0 = TF.Zero;
                        for (int k = 0; k < n; k++) a0 += w[wp + k] * src[s + k];
                        dst[x] = a0;
                    }
                    break;
                case 2:
                    for (int x = 0; x < dw; x++)
                    {
                        int s = start[x] * 2, n = count[x], wp = x * m;
                        TF a0 = TF.Zero, a1 = TF.Zero;
                        for (int k = 0; k < n; k++, s += 2)
                        {
                            TF c = w[wp + k];
                            a0 += c * src[s]; a1 += c * src[s + 1];
                        }
                        dst[x * 2] = a0; dst[x * 2 + 1] = a1;
                    }
                    break;
                case 3:
                    for (int x = 0; x < dw; x++)
                    {
                        int s = start[x] * 3, n = count[x], wp = x * m;
                        TF a0 = TF.Zero, a1 = TF.Zero, a2 = TF.Zero;
                        for (int k = 0; k < n; k++, s += 3)
                        {
                            TF c = w[wp + k];
                            a0 += c * src[s]; a1 += c * src[s + 1]; a2 += c * src[s + 2];
                        }
                        dst[x * 3] = a0; dst[x * 3 + 1] = a1; dst[x * 3 + 2] = a2;
                    }
                    break;
                case 4:
                    for (int x = 0; x < dw; x++)
                    {
                        int s = start[x] * 4, n = count[x], wp = x * m;
                        TF a0 = TF.Zero, a1 = TF.Zero, a2 = TF.Zero, a3 = TF.Zero;
                        for (int k = 0; k < n; k++, s += 4)
                        {
                            TF c = w[wp + k];
                            a0 += c * src[s]; a1 += c * src[s + 1]; a2 += c * src[s + 2]; a3 += c * src[s + 3];
                        }
                        dst[x * 4] = a0; dst[x * 4 + 1] = a1; dst[x * 4 + 2] = a2; dst[x * 4 + 3] = a3;
                    }
                    break;
                default:
                    for (int x = 0; x < dw; x++)
                    {
                        int s = start[x] * nc, n = count[x], wp = x * m;
                        var d = dst.Slice(x * nc, nc);
                        d.Clear();
                        for (int k = 0; k < n; k++, s += nc)
                        {
                            TF c = w[wp + k];
                            for (int ch = 0; ch < nc; ch++) d[ch] += c * src[s + ch];
                        }
                    }
                    break;
            }
        }

        // 3 or 4 channel float rows: one 4 lane multiply-add per tap. dst starts at dstOff in dst; for 3 channels the
        // fourth lane reads the next pixel's first sample and is overwritten by the next pixel (the buffers are padded)
        static void HorizontalSimd(float[] src, float[] dst, int dstOff, int nc, AxisWeights ax, float[] w, int dw)
        {
            int m = ax.MaxTaps;
            var start = ax.Start;
            var count = ax.Count;
            ref float s0 = ref MemoryMarshal.GetArrayDataReference(src);
            ref float d0 = ref MemoryMarshal.GetArrayDataReference(dst);
            for (int x = 0; x < dw; x++)
            {
                int s = start[x] * nc, n = count[x], wp = x * m;
                var acc = Vector128<float>.Zero;
                for (int k = 0; k < n; k++, s += nc)
                    acc += Vector128.LoadUnsafe(ref s0, (nuint)s) * Vector128.Create(w[wp + k]);
                acc.StoreUnsafe(ref d0, (nuint)(dstOff + x * nc));
            }
        }

        // dst = w * src
        static void Scale<TF>(ReadOnlySpan<TF> src, TF w, Span<TF> dst) where TF : unmanaged, IFloatingPointIeee754<TF>
        {
            int i = 0;
            if (Vector.IsHardwareAccelerated && src.Length >= Vector<TF>.Count)
            {
                var vw = new Vector<TF>(w);
                for (; i <= src.Length - Vector<TF>.Count; i += Vector<TF>.Count)
                    (new Vector<TF>(src.Slice(i)) * vw).CopyTo(dst.Slice(i));
            }
            for (; i < src.Length; i++) dst[i] = w * src[i];
        }

        // dst += w * src
        static void MulAdd<TF>(ReadOnlySpan<TF> src, TF w, Span<TF> dst) where TF : unmanaged, IFloatingPointIeee754<TF>
        {
            int i = 0;
            if (Vector.IsHardwareAccelerated && src.Length >= Vector<TF>.Count)
            {
                var vw = new Vector<TF>(w);
                for (; i <= src.Length - Vector<TF>.Count; i += Vector<TF>.Count)
                    (new Vector<TF>(dst.Slice(i)) + new Vector<TF>(src.Slice(i)) * vw).CopyTo(dst.Slice(i));
            }
            for (; i < src.Length; i++) dst[i] += w * src[i];
        }
    }
}
