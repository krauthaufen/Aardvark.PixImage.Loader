using System;
using System.Numerics;

namespace PixCodec.Processing
{
    /// <summary>Vectorised conversions between 8 / 16 bit samples and float rows.</summary>
    internal static class RowConvert
    {
        public static void Widen(ReadOnlySpan<byte> src, Span<float> dst)
        {
            int i = 0, n = Vector<byte>.Count;
            if (Vector.IsHardwareAccelerated)
            {
                int q = Vector<float>.Count;
                for (; i <= src.Length - n; i += n)
                {
                    Vector.Widen(new Vector<byte>(src.Slice(i)), out var l, out var h);
                    Vector.Widen(l, out var a, out var b);
                    Vector.Widen(h, out var c, out var d);
                    Vector.ConvertToSingle(Vector.AsVectorInt32(a)).CopyTo(dst.Slice(i));
                    Vector.ConvertToSingle(Vector.AsVectorInt32(b)).CopyTo(dst.Slice(i + q));
                    Vector.ConvertToSingle(Vector.AsVectorInt32(c)).CopyTo(dst.Slice(i + 2 * q));
                    Vector.ConvertToSingle(Vector.AsVectorInt32(d)).CopyTo(dst.Slice(i + 3 * q));
                }
            }
            for (; i < src.Length; i++) dst[i] = src[i];
        }

        public static void Widen(ReadOnlySpan<ushort> src, Span<float> dst)
        {
            int i = 0, n = Vector<ushort>.Count;
            if (Vector.IsHardwareAccelerated)
            {
                int q = Vector<float>.Count;
                for (; i <= src.Length - n; i += n)
                {
                    Vector.Widen(new Vector<ushort>(src.Slice(i)), out var a, out var b);
                    Vector.ConvertToSingle(Vector.AsVectorInt32(a)).CopyTo(dst.Slice(i));
                    Vector.ConvertToSingle(Vector.AsVectorInt32(b)).CopyTo(dst.Slice(i + q));
                }
            }
            for (; i < src.Length; i++) dst[i] = src[i];
        }

        // round half up and saturate, as Samples.Store: floor(v + 0.5) clamped to the range
        static Vector<int> Round(Vector<float> v, Vector<float> max)
            => Vector.ConvertToInt32(Vector.Min(Vector.Max(v + new Vector<float>(0.5f), Vector<float>.Zero), max));

        public static void Narrow(ReadOnlySpan<float> src, Span<byte> dst)
        {
            int i = 0, n = Vector<byte>.Count;
            if (Vector.IsHardwareAccelerated)
            {
                int q = Vector<float>.Count;
                var max = new Vector<float>(255);
                for (; i <= src.Length - n; i += n)
                {
                    var a = Vector.AsVectorUInt32(Round(new Vector<float>(src.Slice(i)), max));
                    var b = Vector.AsVectorUInt32(Round(new Vector<float>(src.Slice(i + q)), max));
                    var c = Vector.AsVectorUInt32(Round(new Vector<float>(src.Slice(i + 2 * q)), max));
                    var d = Vector.AsVectorUInt32(Round(new Vector<float>(src.Slice(i + 3 * q)), max));
                    Vector.Narrow(Vector.Narrow(a, b), Vector.Narrow(c, d)).CopyTo(dst.Slice(i));
                }
            }
            for (; i < src.Length; i++) dst[i] = Samples.Store<byte, float>(src[i]);
        }

        public static void Narrow(ReadOnlySpan<float> src, Span<ushort> dst)
        {
            int i = 0, n = Vector<ushort>.Count;
            if (Vector.IsHardwareAccelerated)
            {
                int q = Vector<float>.Count;
                var max = new Vector<float>(65535);
                for (; i <= src.Length - n; i += n)
                {
                    var a = Vector.AsVectorUInt32(Round(new Vector<float>(src.Slice(i)), max));
                    var b = Vector.AsVectorUInt32(Round(new Vector<float>(src.Slice(i + q)), max));
                    Vector.Narrow(a, b).CopyTo(dst.Slice(i));
                }
            }
            for (; i < src.Length; i++) dst[i] = Samples.Store<ushort, float>(src[i]);
        }
    }
}
