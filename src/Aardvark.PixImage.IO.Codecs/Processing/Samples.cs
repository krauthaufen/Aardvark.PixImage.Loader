using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Aardvark.ImageCodecs.Processing
{
    /// <summary>Conversions between stored samples (any <see cref="SampleType"/>) and the float or double working type.</summary>
    internal static class Samples
    {
        /// <summary>Samples of integer types are rounded and clamped on output, float samples are stored as they are.</summary>
        public static bool IsInteger<TS>() => typeof(TS) != typeof(Half) && typeof(TS) != typeof(float) && typeof(TS) != typeof(double);

        /// <summary>The largest value of an unsigned 8 or 16 bit type (for sRGB tables), 0 otherwise.</summary>
        public static int TableSize<TS>() => typeof(TS) == typeof(byte) ? 256 : typeof(TS) == typeof(ushort) ? 65536 : 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TF Load<TS, TF>(TS v) where TS : unmanaged, INumberBase<TS> where TF : unmanaged, IFloatingPointIeee754<TF>
            => TF.CreateTruncating(v);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TS Store<TS, TF>(TF v) where TS : unmanaged, INumberBase<TS> where TF : unmanaged, IFloatingPointIeee754<TF>
        {
            if (IsInteger<TS>()) return TS.CreateSaturating(TF.Floor(v + TF.CreateTruncating(0.5)));
            return TS.CreateTruncating(v);
        }

        /// <summary>Precise working type for a sample type: double for 32/64 bit integers and doubles, float otherwise.</summary>
        public static bool NeedsDouble(SampleType t) => t is SampleType.Float64 or SampleType.Int32 or SampleType.UInt32
            or SampleType.Int64 or SampleType.UInt64;

        public static double MaxValue(SampleType t) => t switch
        {
            SampleType.UInt8 => byte.MaxValue,
            SampleType.UInt16 => ushort.MaxValue,
            _ => 1,
        };

        /// <summary>sRGB decode table: stored value -> linear light in 0..1.</summary>
        public static TF[] SrgbToLinear<TF>(int size) where TF : unmanaged, IFloatingPointIeee754<TF>
        {
            var t = new TF[size];
            double max = size - 1;
            for (int i = 0; i < size; i++) t[i] = TF.CreateTruncating(SrgbDecode(i / max));
            return t;
        }

        // the sRGB transfer function (IEC 61966-2-1)
        public static double SrgbDecode(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        public static double SrgbEncode(double v) => v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
    }
}
