using System;
using System.Numerics;
using System.Threading.Tasks;

namespace PixCodec.Processing
{
    /// <summary>Fills the source positions of one destination row (pixel centres at integer coordinates).</summary>
    internal delegate void RowMap(int y, Span<double> sx, Span<double> sy);

    /// <summary>
    /// Inverse mapping: every destination pixel samples the source at a computed position with an interpolation filter,
    /// directly (no antialiasing). Source pixel centres are at integer coordinates.
    /// </summary>
    internal static class Warper
    {
        public static RawImage Warp(RawImage src, int width, int height, RowMap map, WarpOptions o)
        {
            if (width <= 0 || height <= 0) throw new ArgumentException("output size must be positive");
            var dst = RawImage.Create(width, height, src.Color, src.SampleType, src.Channels);
            int alpha = o.AlphaChannel ?? (src.HasAlpha ? src.Channels - 1 : -1);
            if (alpha >= src.Channels) throw new ArgumentOutOfRangeException(nameof(o.AlphaChannel));
            var border = new double[src.Channels];
            if (o.BorderValue is { Length: > 0 } bv)
                for (int c = 0; c < border.Length; c++) border[c] = bv.Length == 1 ? bv[0] : c < bv.Length ? bv[c] : 0;
            var job = new Job(src.Width, src.Height, src.Channels, width, height, map, o.Filter, o.Border, border,
                o.PremultiplyAlpha ? alpha : -1, o.MaxDegreeOfParallelism);
            switch (src.SampleType)
            {
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

        sealed record Job(int SW, int SH, int NC, int DW, int DH, RowMap Map, ResampleFilter Filter, BorderMode Border,
            double[] BorderValue, int PremulAlpha, int MaxParallel);

        /// <summary>Maps an index outside [0, n) into it, or returns -1 for the constant border.</summary>
        internal static int Resolve(int i, int n, BorderMode mode)
        {
            if ((uint)i < (uint)n) return i;
            switch (mode)
            {
                case BorderMode.Clamp: return i < 0 ? 0 : n - 1;
                case BorderMode.Wrap: i %= n; return i < 0 ? i + n : i;
                case BorderMode.Mirror:
                    if (n == 1) return 0;
                    {
                        int p = 2 * (n - 1);
                        i %= p; if (i < 0) i += p;
                        return i < n ? i : p - i;
                    }
                case BorderMode.MirrorRepeat:
                    {
                        int p = 2 * n;
                        i %= p; if (i < 0) i += p;
                        return i < n ? i : p - 1 - i;
                    }
                default: return -1;
            }
        }

        static void Run<TS, TF>(Job j, TS[] src, TS[] dst)
            where TS : unmanaged, INumberBase<TS> where TF : unmanaged, IFloatingPointIeee754<TF>
        {
            int nc = j.NC, premul = j.PremulAlpha;
            bool nearest = j.Filter.IsNearest;
            double support = j.Filter.Support;
            int maxTaps = nearest ? 1 : (int)Math.Ceiling(2 * support) + 1;
            var borderPix = new TF[nc];
            for (int c = 0; c < nc; c++) borderPix[c] = TF.CreateTruncating(j.BorderValue[c]);
            if (premul >= 0)
                for (int c = 0; c < nc; c++) if (c != premul) borderPix[c] *= borderPix[premul];

            Parallel.For(0, j.DH, Resampler.Par(j.MaxParallel), () => new Scratch<TF>(j.DW, maxTaps, nc), (y, _, s) =>
            {
                j.Map(y, s.SX, s.SY);
                var acc = s.Acc;
                for (int x = 0; x < j.DW; x++)
                {
                    double fx = s.SX[x], fy = s.SY[x];
                    var drow = dst.AsSpan((y * j.DW + x) * nc, nc);
                    if (double.IsNaN(fx) || double.IsNaN(fy) || Math.Abs(fx) > 1e9 || Math.Abs(fy) > 1e9)
                    {
                        Finish(borderPix, drow, nc, premul);
                        continue;
                    }
                    int nx = Taps(fx, j.Filter, nearest, support, s.IX, s.WX);
                    int ny = Taps(fy, j.Filter, nearest, support, s.IY, s.WY);
                    acc.AsSpan().Clear();
                    for (int ty = 0; ty < ny; ty++)
                    {
                        TF wy = s.WY[ty];
                        int ry = Resolve(s.IY[ty], j.SH, j.Border);
                        for (int tx = 0; tx < nx; tx++)
                        {
                            TF w = wy * s.WX[tx];
                            int rx = ry < 0 ? -1 : Resolve(s.IX[tx], j.SW, j.Border);
                            if (rx < 0)
                            {
                                for (int c = 0; c < nc; c++) acc[c] += w * borderPix[c];
                                continue;
                            }
                            int p = (ry * j.SW + rx) * nc;
                            if (premul >= 0)
                            {
                                TF a = Samples.Load<TS, TF>(src[p + premul]);
                                TF wa = w * a;
                                for (int c = 0; c < nc; c++)
                                    acc[c] += c == premul ? wa : wa * Samples.Load<TS, TF>(src[p + c]);
                            }
                            else
                            {
                                for (int c = 0; c < nc; c++) acc[c] += w * Samples.Load<TS, TF>(src[p + c]);
                            }
                        }
                    }
                    Finish(acc, drow, nc, premul);
                }
                return s;
            }, _ => { });
        }

        static void Finish<TS, TF>(TF[] acc, Span<TS> dst, int nc, int premul)
            where TS : unmanaged, INumberBase<TS> where TF : unmanaged, IFloatingPointIeee754<TF>
        {
            if (premul >= 0)
            {
                TF a = acc[premul];
                TF inv = a > TF.Zero ? TF.One / a : TF.Zero;
                for (int c = 0; c < nc; c++)
                    dst[c] = Samples.Store<TS, TF>(c == premul ? a : acc[c] * inv);
            }
            else
            {
                for (int c = 0; c < nc; c++) dst[c] = Samples.Store<TS, TF>(acc[c]);
            }
        }

        /// <summary>Indices and normalised weights of the taps around position f.</summary>
        static int Taps<TF>(double f, ResampleFilter filter, bool nearest, double support, int[] idx, TF[] w)
            where TF : unmanaged, IFloatingPointIeee754<TF>
        {
            if (nearest)
            {
                idx[0] = (int)Math.Floor(f + 0.5);
                w[0] = TF.One;
                return 1;
            }
            int lo = (int)Math.Floor(f - support) + 1, hi = (int)Math.Floor(f + support);
            if (hi - lo + 1 > idx.Length) hi = lo + idx.Length - 1;
            double sum = 0;
            int n = 0;
            Span<double> tmp = stackalloc double[idx.Length];
            for (int i = lo; i <= hi; i++)
            {
                double v = filter.Evaluate(f - i);
                if (v == 0 && n == 0) continue; // leading zero
                idx[n] = i; tmp[n] = v; n++;
                sum += v;
            }
            while (n > 1 && tmp[n - 1] == 0) n--;
            if (n == 0 || sum == 0)
            {
                idx[0] = (int)Math.Floor(f + 0.5);
                w[0] = TF.One;
                return 1;
            }
            for (int k = 0; k < n; k++) w[k] = TF.CreateTruncating(tmp[k] / sum);
            return n;
        }

        sealed class Scratch<TF>
        {
            public readonly double[] SX, SY;
            public readonly int[] IX, IY;
            public readonly TF[] WX, WY, Acc;
            public Scratch(int width, int taps, int nc)
            {
                SX = new double[width]; SY = new double[width];
                IX = new int[taps]; IY = new int[taps];
                WX = new TF[taps]; WY = new TF[taps];
                Acc = new TF[nc];
            }
        }
    }
}
