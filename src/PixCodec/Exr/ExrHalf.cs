using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PixCodec
{
    /// <summary>Bulk conversions between IEEE half and float bit patterns (System.Half's operators are slow in loops).</summary>
    internal static unsafe class ExrHalf
    {
        /// <summary>float bit pattern for each of the 65536 half bit patterns.</summary>
        public static readonly uint[] ToFloatBits = BuildTable();

        static uint[] BuildTable()
        {
            var t = new uint[65536];
            for (int i = 0; i < 65536; i++)
            {
                uint sign = (uint)(i & 0x8000) << 16;
                int e = (i >> 10) & 31, m = i & 0x3ff;
                uint bits;
                if (e == 0)
                    bits = BitConverter.SingleToUInt32Bits(m * (1f / 16777216f));   // zero and subnormals: m * 2^-24, exact
                else if (e == 31)
                    bits = 0x7f800000u | ((uint)m << 13);
                else
                    bits = ((uint)(e + 112) << 23) | ((uint)m << 13);
                t[i] = bits | sign;
            }
            return t;
        }

        /// <summary>Converts n half bit patterns (unaligned) to floats.</summary>
        public static void ToFloat(ushort* src, float* dst, int n)
        {
            int i = 0;
            if (Vector256.IsHardwareAccelerated)
            {
                var m7fff = Vector256.Create(0x7fff);
                var bias = Vector256.Create(0x38000000);
                var inf = Vector256.Create(0x7f800000);
                var scale = Vector256.Create(1f / 16777216f);
                var subLimit = Vector256.Create(0x400);
                var maxFinite = Vector256.Create(0x7bff);
                for (; i + 8 <= n; i += 8)
                {
                    var h = Vector256.WidenLower(Vector128.Load(src + i).ToVector256Unsafe()).AsInt32();
                    var em = h & m7fff;
                    var sign = Vector256.ShiftLeft(h ^ em, 16);
                    var sh = Vector256.ShiftLeft(em, 13);
                    var r = sh + bias;
                    r = Vector256.ConditionalSelect(Vector256.LessThan(em, subLimit), (Vector256.ConvertToSingle(em) * scale).AsInt32(), r);
                    r = Vector256.ConditionalSelect(Vector256.GreaterThan(em, maxFinite), sh | inf, r);
                    (r | sign).AsSingle().Store(dst + i);
                }
            }
            else if (Vector128.IsHardwareAccelerated)
            {
                var m7fff = Vector128.Create(0x7fff);
                var bias = Vector128.Create(0x38000000);
                var inf = Vector128.Create(0x7f800000);
                var scale = Vector128.Create(1f / 16777216f);
                var subLimit = Vector128.Create(0x400);
                var maxFinite = Vector128.Create(0x7bff);
                for (; i + 4 <= n; i += 4)
                {
                    var h = Vector128.WidenLower(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(src + i)).AsUInt16()).AsInt32();
                    var em = h & m7fff;
                    var sign = Vector128.ShiftLeft(h ^ em, 16);
                    var sh = Vector128.ShiftLeft(em, 13);
                    var r = sh + bias;
                    r = Vector128.ConditionalSelect(Vector128.LessThan(em, subLimit), (Vector128.ConvertToSingle(em) * scale).AsInt32(), r);
                    r = Vector128.ConditionalSelect(Vector128.GreaterThan(em, maxFinite), sh | inf, r);
                    (r | sign).AsSingle().Store(dst + i);
                }
            }
            fixed (uint* t = ToFloatBits)
                for (; i < n; i++) ((uint*)dst)[i] = t[Unsafe.ReadUnaligned<ushort>(src + i)];
        }

        /// <summary>
        /// float -> half, round to nearest even. Finite values beyond the half range are clamped to +-65504
        /// (HALF_MAX) instead of becoming infinite; infinities and NaNs are preserved.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort FromFloat(float f)
        {
            uint x = BitConverter.SingleToUInt32Bits(f);
            uint sign = (x >> 16) & 0x8000;
            x &= 0x7fffffff;
            if (x - 0x38800000u < 0x47800000u - 0x38800000u)
            {
                // normal half range: rebias the exponent, then round the 13 dropped mantissa bits
                uint m = x - 0x38000000u;
                uint r = (m + 0xfff + ((m >> 13) & 1)) >> 13;
                return (ushort)(sign | (r > 0x7bff ? 0x7bffu : r));
            }
            return FromFloatSlow(x, sign);
        }

        /// <summary>float -> half with IEEE overflow to infinity (what the reference lookup tables were built with).</summary>
        public static ushort FromFloatIeee(float f)
        {
            if (MathF.Abs(f) >= 65520f) return (ushort)(f < 0 ? 0xfc00 : 0x7c00);
            return FromFloat(f);
        }

        static ushort FromFloatSlow(uint x, uint sign)
        {
            if (x >= 0x47800000u)
            {
                if (x > 0x7f800000u) { uint m = (x >> 13) & 0x3ff; return (ushort)(sign | 0x7c00 | (m == 0 ? 1u : m)); }   // NaN
                if (x == 0x7f800000u) return (ushort)(sign | 0x7c00);
                return (ushort)(sign | 0x7bff);
            }
            if (x < 0x33000000u) return (ushort)sign;    // below 2^-25: rounds to zero
            int shift = 126 - (int)(x >> 23);            // 14..24
            uint mant = (x & 0x7fffff) | 0x800000;
            uint q = mant >> shift, rem = mant & ((1u << shift) - 1), half = 1u << (shift - 1);
            if (rem > half || (rem == half && (q & 1) != 0)) q++;
            return (ushort)(sign | q);
        }
    }
}
