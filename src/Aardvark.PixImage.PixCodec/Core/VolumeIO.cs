using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Aardvark.ImageCodecs
{
    /// <summary>Options shared by the decoders of the scientific formats (NRRD, NIfTI, MetaImage, FITS).</summary>
    public class VolumeDecodeOptions
    {
        /// <summary>How the file's value scaling (NIfTI scl_slope / scl_inter, FITS BSCALE / BZERO) is applied. See <see cref="DataScaling"/>.</summary>
        public DataScaling Scaling { get; set; } = DataScaling.Physical;

        /// <summary>
        /// Directory against which relative names of detached data files are resolved (NRRD "data file", MetaImage
        /// ElementDataFile, the .img of a NIfTI .hdr). The path based methods set it to the header's directory.
        /// </summary>
        public string? DataDirectory { get; set; }

        /// <summary>
        /// Opens a detached data file by the name the header gives (overrides <see cref="DataDirectory"/>), e.g. to read
        /// from an archive. The stream is disposed after reading.
        /// </summary>
        public Func<string, Stream>? OpenDataFile { get; set; }

        /// <summary>Upper bound on the number of samples (times channels); larger grids throw <see cref="ImageNotSupportedException"/>.</summary>
        public long MaxSamples { get; set; } = Array.MaxLength;

        internal static readonly VolumeDecodeOptions Default = new();

        internal Stream Open(string name, string format)
        {
            if (OpenDataFile != null) return OpenDataFile(name);
            string path = Path.IsPathRooted(name) ? name
                : DataDirectory != null ? Path.Combine(DataDirectory, name)
                : throw new ImageFormatException($"{format}: the samples are in a separate file ('{name}'): decode from the header's path or set DataDirectory / OpenDataFile");
            if (!File.Exists(path)) throw new ImageFormatException($"{format}: data file '{path}' not found");
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        }
    }

    /// <summary>Shared helpers of the scientific formats: raw sample I/O, byte order, scaling, layout changes, text numbers.</summary>
    internal static class VolumeIO
    {
        public static bool IsInteger(SampleType t) => t is not (SampleType.Float16 or SampleType.Float32 or SampleType.Float64);

        public static unsafe T WithStream<T>(ReadOnlySpan<byte> data, Func<Stream, T> f)
        {
            if (data.IsEmpty) return f(new MemoryStream(Array.Empty<byte>(), false));
            fixed (byte* p = data)
            {
                using var ms = new UnmanagedMemoryStream(p, data.Length);
                return f(ms);
            }
        }

        // -------------------------------------------------------------- raw samples

        public static Array Allocate(SampleType t, long count, long max, string format)
        {
            if (count > max || count > Array.MaxLength) throw new ImageNotSupportedException($"{format}: {count} samples exceed the limit");
            return RawImage.CreateArray(t, (int)count);
        }

        /// <summary>Reads exactly the destination's samples from the stream (in file byte order), then fixes the byte order.</summary>
        public static void ReadSamples(Stream s, Array dst, long offset, long count, bool bigEndian, string format)
        {
            var t = RawImage.SampleTypeOf(dst.GetType().GetElementType()!);
            int bps = RawImage.BytesPerSample(t);
            const int Chunk = 1 << 24;
            while (count > 0)
            {
                int n = (int)Math.Min(count, Chunk / bps);
                var bytes = ByteSpan(dst, t, offset, n);
                int got = 0;
                while (got < bytes.Length)
                {
                    int r = s.Read(bytes.Slice(got));
                    if (r <= 0) throw new ImageFormatException($"{format}: data ends early");
                    got += r;
                }
                if (bigEndian && bps > 1) Swap(bytes, bps);
                offset += n; count -= n;
            }
        }

        /// <summary>Copies samples from file bytes (in file byte order) into the destination.</summary>
        public static void CopySamples(ReadOnlySpan<byte> src, Array dst, long offset, bool bigEndian)
        {
            var t = RawImage.SampleTypeOf(dst.GetType().GetElementType()!);
            int bps = RawImage.BytesPerSample(t);
            var bytes = ByteSpan(dst, t, offset, src.Length / bps);
            src.Slice(0, bytes.Length).CopyTo(bytes);
            if (bigEndian && bps > 1) Swap(bytes, bps);
        }

        /// <summary>The bytes of <paramref name="count"/> samples of an array starting at sample <paramref name="offset"/>.</summary>
        public static Span<byte> ByteSpan(Array a, SampleType t, long offset, int count)
        {
            int o = (int)offset;
            return t switch
            {
                SampleType.UInt8 => ((byte[])a).AsSpan(o, count),
                SampleType.Int8 => MemoryMarshal.AsBytes(((sbyte[])a).AsSpan(o, count)),
                SampleType.UInt16 => MemoryMarshal.AsBytes(((ushort[])a).AsSpan(o, count)),
                SampleType.Int16 => MemoryMarshal.AsBytes(((short[])a).AsSpan(o, count)),
                SampleType.UInt32 => MemoryMarshal.AsBytes(((uint[])a).AsSpan(o, count)),
                SampleType.Int32 => MemoryMarshal.AsBytes(((int[])a).AsSpan(o, count)),
                SampleType.UInt64 => MemoryMarshal.AsBytes(((ulong[])a).AsSpan(o, count)),
                SampleType.Int64 => MemoryMarshal.AsBytes(((long[])a).AsSpan(o, count)),
                SampleType.Float16 => MemoryMarshal.AsBytes(((Half[])a).AsSpan(o, count)),
                SampleType.Float32 => MemoryMarshal.AsBytes(((float[])a).AsSpan(o, count)),
                SampleType.Float64 => MemoryMarshal.AsBytes(((double[])a).AsSpan(o, count)),
                _ => throw new ArgumentOutOfRangeException(nameof(t)),
            };
        }

        /// <summary>Reverses the byte order of every sample in place.</summary>
        public static void Swap(Span<byte> bytes, int bps)
        {
            switch (bps)
            {
                case 2: { var s = MemoryMarshal.Cast<byte, ushort>(bytes); BinaryPrimitives.ReverseEndianness(s, s); break; }
                case 4: { var s = MemoryMarshal.Cast<byte, uint>(bytes); BinaryPrimitives.ReverseEndianness(s, s); break; }
                case 8: { var s = MemoryMarshal.Cast<byte, ulong>(bytes); BinaryPrimitives.ReverseEndianness(s, s); break; }
            }
        }

        /// <summary>Writes the samples [offset, offset + count) in the given byte order.</summary>
        public static void WriteSamples(Stream s, Array src, long offset, long count, bool bigEndian)
        {
            var t = RawImage.SampleTypeOf(src.GetType().GetElementType()!);
            int bps = RawImage.BytesPerSample(t);
            const int Chunk = 1 << 20;
            byte[]? tmp = bigEndian && bps > 1 ? new byte[Chunk] : null;
            while (count > 0)
            {
                int n = (int)Math.Min(count, Chunk / bps);
                var bytes = ByteSpan(src, t, offset, n);
                if (tmp != null)
                {
                    var d = tmp.AsSpan(0, bytes.Length);
                    bytes.CopyTo(d);
                    Swap(d, bps);
                    s.Write(d);
                }
                else s.Write(bytes);
                offset += n; count -= n;
            }
        }

        // -------------------------------------------------------------- layout

        /// <summary>Planar (all of channel 0, then channel 1, ...) to interleaved.</summary>
        public static Array Interleave(Array planar, long pixels, int channels)
        {
            if (channels == 1) return planar;
            var t = RawImage.SampleTypeOf(planar.GetType().GetElementType()!);
            var dst = RawImage.CreateArray(t, (int)(pixels * channels));
            switch (RawImage.BytesPerSample(t))
            {
                case 1: Interleave<byte>(ByteView(planar), ByteView(dst), pixels, channels); break;
                case 2: Interleave<ushort>(View<ushort>(planar), View<ushort>(dst), pixels, channels); break;
                case 4: Interleave<uint>(View<uint>(planar), View<uint>(dst), pixels, channels); break;
                default: Interleave<ulong>(View<ulong>(planar), View<ulong>(dst), pixels, channels); break;
            }
            return dst;
        }

        /// <summary>Interleaved to planar.</summary>
        public static Array Deinterleave(Array inter, long pixels, int channels)
        {
            if (channels == 1) return inter;
            var t = RawImage.SampleTypeOf(inter.GetType().GetElementType()!);
            var dst = RawImage.CreateArray(t, (int)(pixels * channels));
            switch (RawImage.BytesPerSample(t))
            {
                case 1: Deinterleave<byte>(ByteView(inter), ByteView(dst), pixels, channels); break;
                case 2: Deinterleave<ushort>(View<ushort>(inter), View<ushort>(dst), pixels, channels); break;
                case 4: Deinterleave<uint>(View<uint>(inter), View<uint>(dst), pixels, channels); break;
                default: Deinterleave<ulong>(View<ulong>(inter), View<ulong>(dst), pixels, channels); break;
            }
            return dst;
        }

        static Span<byte> ByteView(Array a) => ByteSpan(a, RawImage.SampleTypeOf(a.GetType().GetElementType()!), 0, (int)a.LongLength);
        static Span<T> View<T>(Array a) where T : unmanaged => MemoryMarshal.Cast<byte, T>(ByteView(a));

        static void Interleave<T>(Span<T> src, Span<T> dst, long pixels, int channels) where T : unmanaged
        {
            for (int c = 0; c < channels; c++)
            {
                var p = src.Slice((int)(c * pixels), (int)pixels);
                for (int i = 0, o = c; i < p.Length; i++, o += channels) dst[o] = p[i];
            }
        }

        static void Deinterleave<T>(Span<T> src, Span<T> dst, long pixels, int channels) where T : unmanaged
        {
            for (int c = 0; c < channels; c++)
            {
                var p = dst.Slice((int)(c * pixels), (int)pixels);
                for (int i = 0, o = c; i < p.Length; i++, o += channels) p[i] = src[o];
            }
        }

        /// <summary>Reverses the row order within every slice (rows of <paramref name="rowLength"/> samples, <paramref name="rows"/> per slice).</summary>
        public static void FlipRows(Array a, int rowLength, int rows)
        {
            if (rows <= 1) return;
            var t = RawImage.SampleTypeOf(a.GetType().GetElementType()!);
            int bps = RawImage.BytesPerSample(t);
            var all = ByteView(a);
            int rb = rowLength * bps;
            long slice = (long)rb * rows;
            var tmp = new byte[rb];
            for (long s0 = 0; s0 + slice <= all.Length; s0 += slice)
                for (int y = 0; y < rows / 2; y++)
                {
                    var r0 = all.Slice((int)(s0 + (long)y * rb), rb);
                    var r1 = all.Slice((int)(s0 + (long)(rows - 1 - y) * rb), rb);
                    r0.CopyTo(tmp); r1.CopyTo(r0); tmp.CopyTo(r1);
                }
        }

        // -------------------------------------------------------------- scaling

        static bool IsIntegral(double v) => Math.Abs(v) <= 18446744073709551616.0 && Math.Floor(v) == v;

        static (Int128 lo, Int128 hi) Range(SampleType t) => t switch
        {
            SampleType.UInt8 => (0, byte.MaxValue),
            SampleType.Int8 => (sbyte.MinValue, sbyte.MaxValue),
            SampleType.UInt16 => (0, ushort.MaxValue),
            SampleType.Int16 => (short.MinValue, short.MaxValue),
            SampleType.UInt32 => (0, uint.MaxValue),
            SampleType.Int32 => (int.MinValue, int.MaxValue),
            SampleType.UInt64 => (0, ulong.MaxValue),
            SampleType.Int64 => (long.MinValue, long.MaxValue),
            _ => (0, 0),
        };

        static readonly SampleType[] IntTypes = { SampleType.UInt8, SampleType.Int8, SampleType.UInt16, SampleType.Int16, SampleType.UInt32, SampleType.Int32, SampleType.UInt64, SampleType.Int64 };

        public static bool HasScaling(double slope, double inter) => !(slope == 1 && inter == 0);

        /// <summary>The sample type DecodeVolume produces for stored samples with the given scaling (see <see cref="DataScaling"/>).</summary>
        public static SampleType ScaledType(SampleType stored, double slope, double inter, DataScaling mode)
        {
            if (mode == DataScaling.Raw) return stored;
            if (mode == DataScaling.Float64) return SampleType.Float64;
            if (mode == DataScaling.Float32) return stored == SampleType.Float64 ? SampleType.Float64 : SampleType.Float32;
            if (!HasScaling(slope, inter)) return stored;
            if (IsInteger(stored) && IsIntegral(slope) && IsIntegral(inter))
            {
                var (lo, hi) = Range(stored);
                Int128 s = (Int128)slope, z = (Int128)inter;
                Int128 a = lo * s + z, b = hi * s + z;
                Int128 min = a < b ? a : b, max = a < b ? b : a;
                foreach (var t in IntTypes)
                {
                    var (tl, th) = Range(t);
                    if (min >= tl && max <= th) return t;
                }
                return SampleType.Float64;
            }
            return stored is SampleType.UInt8 or SampleType.Int8 or SampleType.UInt16 or SampleType.Int16 or SampleType.Float16 or SampleType.Float32
                ? SampleType.Float32 : SampleType.Float64;
        }

        /// <summary>Applies the scaling (see <see cref="ScaledType"/>); integer samples equal to <paramref name="blank"/> become NaN in float results.</summary>
        public static Array Scale(Array src, SampleType outType, double slope, double inter, long? blank)
        {
            var inType = RawImage.SampleTypeOf(src.GetType().GetElementType()!);
            if (outType == inType && !HasScaling(slope, inter) && (blank == null || IsInteger(outType))) return src;
            var dst = outType == inType ? src : RawImage.CreateArray(outType, (int)src.LongLength);
            return inType switch
            {
                SampleType.UInt8 => ScaleFrom((byte[])src, dst, outType, slope, inter, blank),
                SampleType.Int8 => ScaleFrom((sbyte[])src, dst, outType, slope, inter, blank),
                SampleType.UInt16 => ScaleFrom((ushort[])src, dst, outType, slope, inter, blank),
                SampleType.Int16 => ScaleFrom((short[])src, dst, outType, slope, inter, blank),
                SampleType.UInt32 => ScaleFrom((uint[])src, dst, outType, slope, inter, blank),
                SampleType.Int32 => ScaleFrom((int[])src, dst, outType, slope, inter, blank),
                SampleType.UInt64 => ScaleFrom((ulong[])src, dst, outType, slope, inter, blank),
                SampleType.Int64 => ScaleFrom((long[])src, dst, outType, slope, inter, blank),
                SampleType.Float16 => ScaleFrom((Half[])src, dst, outType, slope, inter, null),
                SampleType.Float32 => ScaleFrom((float[])src, dst, outType, slope, inter, null),
                SampleType.Float64 => ScaleFrom((double[])src, dst, outType, slope, inter, null),
                _ => throw new InvalidOperationException(),
            };
        }

        static Array ScaleFrom<TIn>(TIn[] src, Array dst, SampleType outType, double slope, double inter, long? blank)
            where TIn : unmanaged, INumberBase<TIn>
        {
            switch (outType)
            {
                case SampleType.Float32: ToFloat(src, (float[])dst, slope, inter, blank); break;
                case SampleType.Float64: ToDouble(src, (double[])dst, slope, inter, blank); break;
                case SampleType.Float16: ToHalf(src, (Half[])dst, slope, inter); break;
                case SampleType.UInt8: ToInt(src, (byte[])dst, slope, inter); break;
                case SampleType.Int8: ToInt(src, (sbyte[])dst, slope, inter); break;
                case SampleType.UInt16: ToInt(src, (ushort[])dst, slope, inter); break;
                case SampleType.Int16: ToInt(src, (short[])dst, slope, inter); break;
                case SampleType.UInt32: ToInt(src, (uint[])dst, slope, inter); break;
                case SampleType.Int32: ToInt(src, (int[])dst, slope, inter); break;
                case SampleType.UInt64: ToInt(src, (ulong[])dst, slope, inter); break;
                case SampleType.Int64: ToInt(src, (long[])dst, slope, inter); break;
            }
            return dst;
        }

        static void ToFloat<TIn>(TIn[] src, float[] dst, double slope, double inter, long? blank) where TIn : unmanaged, INumberBase<TIn>
        {
            TIn bt = TIn.CreateTruncating(blank ?? 0);
            bool b = blank.HasValue && long.CreateTruncating(bt) == blank.Value;
            for (long i = 0; i < src.LongLength; i++)
            {
                var x = src[i];
                if (b && x == bt) { dst[i] = float.NaN; continue; }
                dst[i] = (float)(double.CreateTruncating(x) * slope + inter);
            }
        }

        static void ToDouble<TIn>(TIn[] src, double[] dst, double slope, double inter, long? blank) where TIn : unmanaged, INumberBase<TIn>
        {
            TIn bt = TIn.CreateTruncating(blank ?? 0);
            bool b = blank.HasValue && long.CreateTruncating(bt) == blank.Value;
            for (long i = 0; i < src.LongLength; i++)
            {
                var x = src[i];
                if (b && x == bt) { dst[i] = double.NaN; continue; }
                dst[i] = double.CreateTruncating(x) * slope + inter;
            }
        }

        static void ToHalf<TIn>(TIn[] src, Half[] dst, double slope, double inter) where TIn : unmanaged, INumberBase<TIn>
        {
            for (long i = 0; i < src.LongLength; i++) dst[i] = (Half)(double.CreateTruncating(src[i]) * slope + inter);
        }

        static void ToInt<TIn, TOut>(TIn[] src, TOut[] dst, double slope, double inter)
            where TIn : unmanaged, INumberBase<TIn> where TOut : unmanaged, INumberBase<TOut>
        {
            Int128 s = (Int128)slope, z = (Int128)inter;
            if (s == 1)
            {
                for (long i = 0; i < src.LongLength; i++) dst[i] = TOut.CreateTruncating(Int128.CreateTruncating(src[i]) + z);
                return;
            }
            for (long i = 0; i < src.LongLength; i++) dst[i] = TOut.CreateTruncating(Int128.CreateTruncating(src[i]) * s + z);
        }

        /// <summary>Converts samples to a type that holds all their values (Float16 to Float32, integers to wider ones or float).</summary>
        public static Array Convert(Array src, SampleType outType) => Scale(src, outType, 1, 0, null);

        /// <summary>Subtracts an integer offset (for FITS' unsigned conventions on encode): dst = src - zero, in the target type.</summary>
        public static Array Offset(Array src, SampleType outType, long zero)
        {
            var dst = RawImage.CreateArray(outType, (int)src.LongLength);
            var inType = RawImage.SampleTypeOf(src.GetType().GetElementType()!);
            return inType switch
            {
                SampleType.Int8 => ScaleFrom((sbyte[])src, dst, outType, 1, -zero, null),
                SampleType.UInt16 => ScaleFrom((ushort[])src, dst, outType, 1, -zero, null),
                SampleType.UInt32 => ScaleFrom((uint[])src, dst, outType, 1, -zero, null),
                SampleType.UInt64 => UInt64Offset((ulong[])src, (long[])dst),
                _ => throw new InvalidOperationException(),
            };
        }

        static Array UInt64Offset(ulong[] src, long[] dst)
        {
            for (long i = 0; i < src.LongLength; i++) dst[i] = (long)(src[i] ^ 0x8000000000000000UL);
            return dst;
        }

        // -------------------------------------------------------------- text

        public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>Parses a floating point number; "nan", "inf", "-inf" as NRRD defines them, FITS' D exponents.</summary>
        public static bool TryParseDouble(ReadOnlySpan<char> s, out double v)
        {
            s = s.Trim();
            if (s.Contains("nan", StringComparison.OrdinalIgnoreCase)) { v = double.NaN; return true; }
            if (s.Contains("-inf", StringComparison.OrdinalIgnoreCase)) { v = double.NegativeInfinity; return true; }
            if (s.Contains("inf", StringComparison.OrdinalIgnoreCase)) { v = double.PositiveInfinity; return true; }
            if (double.TryParse(s, NumberStyles.Float, Inv, out v)) return true;
            if (s.IndexOfAny('D', 'd') >= 0)
            {
                Span<char> t = stackalloc char[Math.Min(s.Length, 256)];
                if (s.Length > t.Length) return false;
                for (int i = 0; i < s.Length; i++) t[i] = s[i] is 'D' or 'd' ? 'E' : s[i];
                return double.TryParse(t.Slice(0, s.Length), NumberStyles.Float, Inv, out v);
            }
            return false;
        }

        public static double ParseDouble(ReadOnlySpan<char> s, string format, string what)
            => TryParseDouble(s, out var v) ? v : throw new ImageFormatException($"{format}: bad number '{s.ToString()}' in {what}");

        public static string Format(double v) => double.IsNaN(v) ? "nan" : double.IsPositiveInfinity(v) ? "inf" : double.IsNegativeInfinity(v) ? "-inf" : v.ToString("R", Inv);

        public static string Format(long v) => v.ToString(Inv);

        /// <summary>Splits at runs of whitespace.</summary>
        public static string[] Words(string s) => s.Split(new[] { ' ', '\t', '\r', '\n', '\v', '\f' }, StringSplitOptions.RemoveEmptyEntries);

        /// <summary>Reads exactly <paramref name="n"/> bytes (or throws), from a possibly short stream.</summary>
        public static void Skip(Stream s, long n, string format)
        {
            if (n <= 0) return;
            if (s.CanSeek)
            {
                if (s.Position + n > s.Length) throw new ImageFormatException($"{format}: data ends early");
                s.Seek(n, SeekOrigin.Current);
                return;
            }
            var b = new byte[(int)Math.Min(n, 1 << 16)];
            while (n > 0)
            {
                int r = s.Read(b, 0, (int)Math.Min(n, b.Length));
                if (r <= 0) throw new ImageFormatException($"{format}: data ends early");
                n -= r;
            }
        }

        /// <summary>a * b, throwing ImageFormatException on overflow (sizes from damaged headers).</summary>
        public static long Mul(long a, long b, string format)
        {
            try { return checked(a * b); }
            catch (OverflowException) { throw new ImageFormatException($"{format}: sizes overflow"); }
        }

        public static long Product(int[] sizes, int channels)
        {
            long n = channels;
            try { foreach (var s in sizes) n = checked(n * s); }
            catch (OverflowException) { throw new ImageNotSupportedException("grid too large"); }
            return n;
        }

        /// <summary>Normalises the direction columns of a (space x axes) matrix of axis vectors, returning their lengths.</summary>
        public static double[] Normalize(double[,] m)
        {
            int rows = m.GetLength(0), cols = m.GetLength(1);
            var len = new double[cols];
            for (int c = 0; c < cols; c++)
            {
                double s = 0;
                for (int r = 0; r < rows; r++) s += m[r, c] * m[r, c];
                s = Math.Sqrt(s);
                len[c] = s;
                if (s > 0) for (int r = 0; r < rows; r++) m[r, c] /= s;
            }
            return len;
        }

        /// <summary>Axis vectors (columns, scaled by the spacings) from the metadata, or null without a direction.</summary>
        public static double[,] AxisVectors(PhysicalMetadata m, int axes, int space)
        {
            var r = new double[space, axes];
            for (int a = 0; a < axes; a++)
            {
                double s = a < m.Spacing.Length && !double.IsNaN(m.Spacing[a]) ? m.Spacing[a] : 1;
                for (int k = 0; k < space; k++)
                {
                    double d = m.Direction != null && a < m.Direction.GetLength(1)
                        ? (k < m.Direction.GetLength(0) ? m.Direction[k, a] : 0)
                        : (k == a ? 1 : 0);
                    r[k, a] = d * s;
                }
            }
            return r;
        }

        public static bool HasGeometry(PhysicalMetadata? m)
            => m != null && (m.Direction != null || m.Origin != null || Array.Exists(m.Spacing, s => !double.IsNaN(s) && s != 1));
    }
}
