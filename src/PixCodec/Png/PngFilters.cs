using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace PixCodec
{
    /// <summary>
    /// PNG scanline filters. <c>bpp</c> is the filter distance: bytes per complete pixel, at least 1.
    /// Reconstruction never works in place: it reads the filtered row and writes to a separate destination,
    /// which lets the 8 bit decode path reconstruct straight into the output image.
    /// </summary>
    internal static unsafe class PngFilters
    {
        // ------------------------------------------------------------------ reconstruction (decoder)

        /// <summary>
        /// Reconstructs one row of <paramref name="n"/> bytes. <paramref name="prev"/> is the reconstructed row above,
        /// or null for the first row of an image / interlace pass (treated as all zero).
        /// Returns false for an invalid filter type.
        /// </summary>
        public static bool Unfilter(int filter, byte* src, byte* prev, byte* dst, int n, int bpp)
        {
            switch (filter)
            {
                case 0:
                    Buffer.MemoryCopy(src, dst, n, n);
                    return true;
                case 1:
                    Sub(src, dst, n, bpp);
                    return true;
                case 2:
                    if (prev == null) Buffer.MemoryCopy(src, dst, n, n);
                    else Up(src, prev, dst, n);
                    return true;
                case 3:
                    if (prev == null) AverageFirstRow(src, dst, n, bpp);
                    else Average(src, prev, dst, n, bpp);
                    return true;
                case 4:
                    // With a zero row above, the Paeth predictor always picks the left neighbor.
                    if (prev == null) Sub(src, dst, n, bpp);
                    else Paeth(src, prev, dst, n, bpp);
                    return true;
                default:
                    return false;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector128<byte> Load8(byte* p) => Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(p)).AsByte();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Store8(byte* p, Vector128<byte> v) => Unsafe.WriteUnaligned(p, v.AsUInt64().ToScalar());

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector128<short> Widen8(byte* p) => Vector128.WidenLower(Load8(p)).AsInt16();

        /// <summary>Stores the low bytes of the eight 16 bit lanes (which must be in 0..255).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void StoreNarrow8(byte* p, Vector128<short> v)
        {
            if (Sse2.IsSupported) Store8(p, Sse2.PackUnsignedSaturate(v, v));
            else Store8(p, Vector128.Narrow(v.AsUInt16(), v.AsUInt16()));
        }

        // The serial filters (Sub, Average, Paeth) depend on the pixel to the left, so they cannot be vectorized
        // across pixels. Instead one whole pixel (up to 8 bytes) is kept in a vector register and every channel is
        // processed at once. Each step loads/stores 8 bytes but advances by bpp; lanes beyond bpp hold garbage that
        // the next step overwrites. The loops stop while 8 bytes still fit and a scalar tail finishes the row.

        static void Sub(byte* src, byte* dst, int n, int bpp)
        {
            int i = 0;
            if (bpp == 1)
            {
                byte a = 0;
                for (; i < n; i++) { a += src[i]; dst[i] = a; }
                return;
            }
            if (Vector128.IsHardwareAccelerated)
            {
                var a = Vector128<byte>.Zero;
                for (; i + 8 <= n; i += bpp)
                {
                    a += Load8(src + i);
                    Store8(dst + i, a);
                }
            }
            for (; i < n; i++) dst[i] = (byte)(src[i] + (i >= bpp ? dst[i - bpp] : 0));
        }

        static void Up(byte* src, byte* prev, byte* dst, int n)
        {
            int i = 0;
            if (Vector.IsHardwareAccelerated)
            {
                for (; i + Vector<byte>.Count <= n; i += Vector<byte>.Count)
                    Unsafe.WriteUnaligned(dst + i, Unsafe.ReadUnaligned<Vector<byte>>(src + i) + Unsafe.ReadUnaligned<Vector<byte>>(prev + i));
            }
            for (; i < n; i++) dst[i] = (byte)(src[i] + prev[i]);
        }

        static void AverageFirstRow(byte* src, byte* dst, int n, int bpp)
        {
            int i = 0;
            for (; i < bpp && i < n; i++) dst[i] = src[i];
            for (; i < n; i++) dst[i] = (byte)(src[i] + (dst[i - bpp] >> 1));
        }

        static void Average(byte* src, byte* prev, byte* dst, int n, int bpp)
        {
            int i = 0;
            if (bpp == 1)
            {
                int a = 0;
                for (; i < n; i++) { a = (byte)(src[i] + ((a + prev[i]) >> 1)); dst[i] = (byte)a; }
                return;
            }
            if (Vector128.IsHardwareAccelerated)
            {
                var a = Vector128<short>.Zero;
                var mask = Vector128.Create((short)0xFF);
                for (; i + 8 <= n; i += bpp)
                {
                    a = (((a + Widen8(prev + i)) >>> 1) + Widen8(src + i)) & mask;
                    StoreNarrow8(dst + i, a);
                }
            }
            for (; i < n; i++)
            {
                int a = i >= bpp ? dst[i - bpp] : 0;
                dst[i] = (byte)(src[i] + ((a + prev[i]) >> 1));
            }
        }

        static void Paeth(byte* src, byte* prev, byte* dst, int n, int bpp)
        {
            int i = 0;
            if (bpp == 1)
            {
                int a = 0, c = 0;
                for (; i < n; i++)
                {
                    int b = prev[i];
                    a = (byte)(src[i] + PaethPredictor(a, b, c));
                    dst[i] = (byte)a;
                    c = b;
                }
                return;
            }
            if (Vector128.IsHardwareAccelerated)
            {
                var a = Vector128<short>.Zero;
                var c = Vector128<short>.Zero;
                var mask = Vector128.Create((short)0xFF);
                for (; i + 8 <= n; i += bpp)
                {
                    var b = Widen8(prev + i);
                    var pa = b - c;             // p - a = b - c
                    var pb = a - c;             // p - b = a - c
                    var pc = Vector128.Abs(pa + pb);
                    pa = Vector128.Abs(pa);
                    pb = Vector128.Abs(pb);
                    var min = Vector128.Min(pc, Vector128.Min(pa, pb));
                    // ties resolve in the order a, b, c
                    var pred = Vector128.ConditionalSelect(Vector128.Equals(min, pa), a,
                               Vector128.ConditionalSelect(Vector128.Equals(min, pb), b, c));
                    a = (pred + Widen8(src + i)) & mask;
                    c = b;
                    StoreNarrow8(dst + i, a);
                }
            }
            for (; i < n; i++)
            {
                int a = 0, c = 0;
                if (i >= bpp) { a = dst[i - bpp]; c = prev[i - bpp]; }
                dst[i] = (byte)(src[i] + PaethPredictor(a, prev[i], c));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int PaethPredictor(int a, int b, int c)
        {
            int pa = b - c, pb = a - c;
            int pc = Math.Abs(pa + pb);
            pa = Math.Abs(pa);
            pb = Math.Abs(pb);
            int bc = pb <= pc ? b : c;
            return pa <= pb && pa <= pc ? a : bc;
        }

        // ------------------------------------------------------------------ filtering (encoder)
        //
        // cur/prev point at the first byte of the raw rows; both must be preceded by at least bpp readable zero
        // bytes (the "pixel to the left" of the first pixel). The encoder has the complete raw rows, so nothing
        // here is serial and everything vectorizes across the row.
        // Every function returns the sum of |residual| (residuals taken as signed bytes): the usual heuristic for
        // picking the filter that deflates best.

        /// <summary>|r| for a residual seen as a signed byte (0x80 counts as 128).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int AbsResidual(byte r) => r < 128 ? r : 256 - r;

        public static long SumAbs(byte* row, int n)
        {
            long sum = 0;
            int i = 0;
            if (Vector.IsHardwareAccelerated)
            {
                var acc = new Accumulator();
                for (; i + Vector<byte>.Count <= n; i += Vector<byte>.Count)
                    acc.Add(Unsafe.ReadUnaligned<Vector<byte>>(row + i));
                sum = acc.Total();
            }
            for (; i < n; i++) sum += AbsResidual(row[i]);
            return sum;
        }

        public static long FilterSub(byte* cur, byte* dst, int n, int bpp)
        {
            long sum = 0;
            int i = 0;
            if (Vector.IsHardwareAccelerated)
            {
                var acc = new Accumulator();
                for (; i + Vector<byte>.Count <= n; i += Vector<byte>.Count)
                {
                    var r = Unsafe.ReadUnaligned<Vector<byte>>(cur + i) - Unsafe.ReadUnaligned<Vector<byte>>(cur + i - bpp);
                    Unsafe.WriteUnaligned(dst + i, r);
                    acc.Add(r);
                }
                sum = acc.Total();
            }
            for (; i < n; i++)
            {
                byte r = (byte)(cur[i] - cur[i - bpp]);
                dst[i] = r;
                sum += AbsResidual(r);
            }
            return sum;
        }

        public static long FilterUp(byte* cur, byte* prev, byte* dst, int n)
        {
            long sum = 0;
            int i = 0;
            if (Vector.IsHardwareAccelerated)
            {
                var acc = new Accumulator();
                for (; i + Vector<byte>.Count <= n; i += Vector<byte>.Count)
                {
                    var r = Unsafe.ReadUnaligned<Vector<byte>>(cur + i) - Unsafe.ReadUnaligned<Vector<byte>>(prev + i);
                    Unsafe.WriteUnaligned(dst + i, r);
                    acc.Add(r);
                }
                sum = acc.Total();
            }
            for (; i < n; i++)
            {
                byte r = (byte)(cur[i] - prev[i]);
                dst[i] = r;
                sum += AbsResidual(r);
            }
            return sum;
        }

        public static long FilterAverage(byte* cur, byte* prev, byte* dst, int n, int bpp)
        {
            long sum = 0;
            int i = 0;
            if (Vector.IsHardwareAccelerated)
            {
                var acc = new Accumulator();
                var low7 = new Vector<byte>(0x7F);
                for (; i + Vector<byte>.Count <= n; i += Vector<byte>.Count)
                {
                    var a = Unsafe.ReadUnaligned<Vector<byte>>(cur + i - bpp);
                    var b = Unsafe.ReadUnaligned<Vector<byte>>(prev + i);
                    // floor((a + b) / 2) without leaving 8 bits: (a & b) + ((a ^ b) >> 1)
                    var half = Vector.AsVectorByte(Vector.ShiftRightLogical(Vector.AsVectorUInt16(a ^ b), 1)) & low7;
                    var r = Unsafe.ReadUnaligned<Vector<byte>>(cur + i) - ((a & b) + half);
                    Unsafe.WriteUnaligned(dst + i, r);
                    acc.Add(r);
                }
                sum = acc.Total();
            }
            for (; i < n; i++)
            {
                byte r = (byte)(cur[i] - ((cur[i - bpp] + prev[i]) >> 1));
                dst[i] = r;
                sum += AbsResidual(r);
            }
            return sum;
        }

        public static long FilterPaeth(byte* cur, byte* prev, byte* dst, int n, int bpp)
        {
            long sum = 0;
            int i = 0;
            if (Vector.IsHardwareAccelerated)
            {
                var acc = new Accumulator();
                for (; i + Vector<byte>.Count <= n; i += Vector<byte>.Count)
                {
                    var a = Unsafe.ReadUnaligned<Vector<byte>>(cur + i - bpp);
                    var b = Unsafe.ReadUnaligned<Vector<byte>>(prev + i);
                    var c = Unsafe.ReadUnaligned<Vector<byte>>(prev + i - bpp);
                    // Everything stays in unsigned bytes. pa = |b - c| and pb = |a - c| fit; pc = |(b - c) + (a - c)|
                    // is |pa - pb| when the two differences have opposite signs, else pa + pb, which may exceed 255 -
                    // saturating it is fine because it is then no smaller than pa and pb anyway.
                    var minBC = Vector.Min(b, c);
                    var minAC = Vector.Min(a, c);
                    var pa = Vector.Max(b, c) - minBC;
                    var pb = Vector.Max(a, c) - minAC;
                    var opposite = Vector.Equals(minBC, c) ^ Vector.Equals(minAC, c);
                    var add = pa + pb;
                    add |= ~Vector.Equals(Vector.Max(add, pa), add); // wrapped -> 255
                    var pc = Vector.ConditionalSelect(opposite, Vector.Max(pa, pb) - Vector.Min(pa, pb), add);
                    var useA = Vector.Equals(Vector.Min(pa, pb), pa) & Vector.Equals(Vector.Min(pa, pc), pa);
                    var useB = Vector.Equals(Vector.Min(pb, pc), pb);
                    var pred = Vector.ConditionalSelect(useA, a, Vector.ConditionalSelect(useB, b, c));
                    var r = Unsafe.ReadUnaligned<Vector<byte>>(cur + i) - pred;
                    Unsafe.WriteUnaligned(dst + i, r);
                    acc.Add(r);
                }
                sum = acc.Total();
            }
            for (; i < n; i++)
            {
                byte r = (byte)(cur[i] - PaethPredictor(cur[i - bpp], prev[i], prev[i - bpp]));
                dst[i] = r;
                sum += AbsResidual(r);
            }
            return sum;
        }

        /// <summary>Sums |x| over signed bytes without overflowing: 16 bit lanes, flushed to 32 bit lanes in time.</summary>
        struct Accumulator
        {
            Vector<ushort> _acc16;
            Vector<uint> _acc32;
            int _pending;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Add(Vector<byte> residual)
            {
                // |-128| stays 0x80, which is the wanted 128 once viewed as unsigned.
                var abs = Vector.AsVectorByte(Vector.Abs(Vector.AsVectorSByte(residual)));
                Vector.Widen(abs, out var lo, out var hi);
                _acc16 += lo + hi;          // at most 256 per lane and step
                if (++_pending == 255) Flush();
            }

            void Flush()
            {
                Vector.Widen(_acc16, out var lo, out var hi);
                _acc32 += lo + hi;
                _acc16 = Vector<ushort>.Zero;
                _pending = 0;
            }

            public long Total()
            {
                Flush();
                Vector.Widen(_acc32, out var lo, out var hi);
                return (long)Vector.Sum(lo + hi);
            }
        }
    }
}
