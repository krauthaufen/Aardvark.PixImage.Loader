using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace PixCodec
{
    /// <summary>
    /// The VP8 loop filter (RFC 6386 section 15) on 16 edge positions at a time. Every lane computes exactly what the
    /// scalar code in <see cref="Vp8Decoder"/> does: the saturating byte arithmetic is the clamping of the format.
    /// </summary>
    internal static unsafe class Vp8LoopFilterSimd
    {
        public static bool IsSupported => Vector128.IsHardwareAccelerated;

        // ---- saturating arithmetic and interleaving, natively where available

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector128<sbyte> AddS(Vector128<sbyte> a, Vector128<sbyte> b)
        {
            if (Sse2.IsSupported) return Sse2.AddSaturate(a, b);
            if (AdvSimd.IsSupported) return AdvSimd.AddSaturate(a, b);
            var s = a + b;
            // overflow when both operands have the sign the sum has not
            var over = Vector128.LessThan((a ^ s) & (b ^ s), Vector128<sbyte>.Zero);
            return Vector128.ConditionalSelect(over, Vector128.ShiftRightArithmetic(a, 7) ^ Vector128.Create((sbyte)127), s);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector128<sbyte> SubS(Vector128<sbyte> a, Vector128<sbyte> b)
        {
            if (Sse2.IsSupported) return Sse2.SubtractSaturate(a, b);
            if (AdvSimd.IsSupported) return AdvSimd.SubtractSaturate(a, b);
            var s = a - b;
            var over = Vector128.LessThan((a ^ b) & (a ^ s), Vector128<sbyte>.Zero);
            return Vector128.ConditionalSelect(over, Vector128.ShiftRightArithmetic(a, 7) ^ Vector128.Create((sbyte)127), s);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector128<byte> AddU(Vector128<byte> a, Vector128<byte> b)
        {
            if (Sse2.IsSupported) return Sse2.AddSaturate(a, b);
            if (AdvSimd.IsSupported) return AdvSimd.AddSaturate(a, b);
            var s = a + b;
            return s | Vector128.LessThan(s, a);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector128<byte> AbsDiff(Vector128<byte> a, Vector128<byte> b) => Vector128.Max(a, b) - Vector128.Min(a, b);

        /// <summary>All ones where a &lt;= limit.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector128<byte> NotAbove(Vector128<byte> a, Vector128<byte> limit) => Vector128.Equals(Vector128.Max(a, limit), limit);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static Vector128<byte> ZipLo8(Vector128<byte> a, Vector128<byte> b)
        {
            if (Sse2.IsSupported) return Sse2.UnpackLow(a, b);
            if (AdvSimd.Arm64.IsSupported) return AdvSimd.Arm64.ZipLow(a, b);
            return Vector128.Shuffle(a, Vector128.Create((byte)0, 255, 1, 255, 2, 255, 3, 255, 4, 255, 5, 255, 6, 255, 7, 255))
                 | Vector128.Shuffle(b, Vector128.Create((byte)255, 0, 255, 1, 255, 2, 255, 3, 255, 4, 255, 5, 255, 6, 255, 7));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static Vector128<byte> ZipHi8(Vector128<byte> a, Vector128<byte> b)
        {
            if (Sse2.IsSupported) return Sse2.UnpackHigh(a, b);
            if (AdvSimd.Arm64.IsSupported) return AdvSimd.Arm64.ZipHigh(a, b);
            return Vector128.Shuffle(a, Vector128.Create((byte)8, 255, 9, 255, 10, 255, 11, 255, 12, 255, 13, 255, 14, 255, 15, 255))
                 | Vector128.Shuffle(b, Vector128.Create((byte)255, 8, 255, 9, 255, 10, 255, 11, 255, 12, 255, 13, 255, 14, 255, 15));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static Vector128<byte> ZipLo16(Vector128<byte> a, Vector128<byte> b)
        {
            if (Sse2.IsSupported) return Sse2.UnpackLow(a.AsUInt16(), b.AsUInt16()).AsByte();
            if (AdvSimd.Arm64.IsSupported) return AdvSimd.Arm64.ZipLow(a.AsUInt16(), b.AsUInt16()).AsByte();
            return Vector128.Shuffle(a, Vector128.Create((byte)0, 1, 255, 255, 2, 3, 255, 255, 4, 5, 255, 255, 6, 7, 255, 255))
                 | Vector128.Shuffle(b, Vector128.Create((byte)255, 255, 0, 1, 255, 255, 2, 3, 255, 255, 4, 5, 255, 255, 6, 7));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static Vector128<byte> ZipHi16(Vector128<byte> a, Vector128<byte> b)
        {
            if (Sse2.IsSupported) return Sse2.UnpackHigh(a.AsUInt16(), b.AsUInt16()).AsByte();
            if (AdvSimd.Arm64.IsSupported) return AdvSimd.Arm64.ZipHigh(a.AsUInt16(), b.AsUInt16()).AsByte();
            return Vector128.Shuffle(a, Vector128.Create((byte)8, 9, 255, 255, 10, 11, 255, 255, 12, 13, 255, 255, 14, 15, 255, 255))
                 | Vector128.Shuffle(b, Vector128.Create((byte)255, 255, 8, 9, 255, 255, 10, 11, 255, 255, 12, 13, 255, 255, 14, 15));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static Vector128<byte> ZipLo32(Vector128<byte> a, Vector128<byte> b)
        {
            if (Sse2.IsSupported) return Sse2.UnpackLow(a.AsUInt32(), b.AsUInt32()).AsByte();
            if (AdvSimd.Arm64.IsSupported) return AdvSimd.Arm64.ZipLow(a.AsUInt32(), b.AsUInt32()).AsByte();
            var x = a.AsUInt32(); var y = b.AsUInt32();
            return Vector128.Create(x.GetElement(0), y.GetElement(0), x.GetElement(1), y.GetElement(1)).AsByte();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static Vector128<byte> ZipHi32(Vector128<byte> a, Vector128<byte> b)
        {
            if (Sse2.IsSupported) return Sse2.UnpackHigh(a.AsUInt32(), b.AsUInt32()).AsByte();
            if (AdvSimd.Arm64.IsSupported) return AdvSimd.Arm64.ZipHigh(a.AsUInt32(), b.AsUInt32()).AsByte();
            var x = a.AsUInt32(); var y = b.AsUInt32();
            return Vector128.Create(x.GetElement(2), y.GetElement(2), x.GetElement(3), y.GetElement(3)).AsByte();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static Vector128<byte> ZipLo64(Vector128<byte> a, Vector128<byte> b) => Vector128.Create(a.AsUInt64().GetElement(0), b.AsUInt64().GetElement(0)).AsByte();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static Vector128<byte> ZipHi64(Vector128<byte> a, Vector128<byte> b) => Vector128.Create(a.AsUInt64().GetElement(1), b.AsUInt64().GetElement(1)).AsByte();

        /// <summary>Rows of a 4x4 matrix of 32 bit lanes to its columns (and back).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void Transpose4(ref Vector128<int> a, ref Vector128<int> b, ref Vector128<int> c, ref Vector128<int> d)
        {
            var t = ZipLo32(a.AsByte(), b.AsByte()); var u = ZipHi32(a.AsByte(), b.AsByte());
            var v = ZipLo32(c.AsByte(), d.AsByte()); var w = ZipHi32(c.AsByte(), d.AsByte());
            a = ZipLo64(t, v).AsInt32(); b = ZipHi64(t, v).AsInt32();
            c = ZipLo64(u, w).AsInt32(); d = ZipHi64(u, w).AsInt32();
        }

        // ---- the filters on vectors; pixels come in as stored, lanes are independent

        static readonly Vector128<byte> Sign = Vector128.Create((byte)0x80);

        /// <summary>All ones where the edge is to be filtered at all: |p0-q0|*2 + |p1-q1|/2 &lt;= limit.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector128<byte> EdgeMask(Vector128<byte> p1, Vector128<byte> p0, Vector128<byte> q0, Vector128<byte> q1, Vector128<byte> limit)
        {
            var d0 = AbsDiff(p0, q0);
            var d1 = Vector128.ShiftRightLogical((AbsDiff(p1, q1) & Vector128.Create((byte)0xfe)).AsUInt16(), 1).AsByte();
            return NotAbove(AddU(AddU(d0, d0), d1), limit);
        }

        /// <summary>The common adjustment of p0 and q0 by the filter value a (signed domain); returns (a + 4) >> 3.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector128<sbyte> Adjust(Vector128<sbyte> a, ref Vector128<sbyte> p0, ref Vector128<sbyte> q0)
        {
            var f1 = Vector128.ShiftRightArithmetic(AddS(a, Vector128.Create((sbyte)4)), 3);
            var f2 = Vector128.ShiftRightArithmetic(AddS(a, Vector128.Create((sbyte)3)), 3);
            q0 = SubS(q0, f1);
            p0 = AddS(p0, f2);
            return f1;
        }

        static void Simple(Vector128<byte> p1, ref Vector128<byte> p0, ref Vector128<byte> q0, Vector128<byte> q1, int limit)
        {
            var mask = EdgeMask(p1, p0, q0, q1, Vector128.Create((byte)limit)).AsSByte();
            var sp1 = (p1 ^ Sign).AsSByte(); var sq1 = (q1 ^ Sign).AsSByte();
            var sp0 = (p0 ^ Sign).AsSByte(); var sq0 = (q0 ^ Sign).AsSByte();
            var d = SubS(sq0, sp0);
            var a = AddS(AddS(AddS(SubS(sp1, sq1), d), d), d) & mask;
            Adjust(a, ref sp0, ref sq0);
            p0 = sp0.AsByte() ^ Sign;
            q0 = sq0.AsByte() ^ Sign;
        }

        /// <summary>Filter mask and high edge variance mask of the normal filters.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Masks(Vector128<byte> p3, Vector128<byte> p2, Vector128<byte> p1, Vector128<byte> p0, Vector128<byte> q0, Vector128<byte> q1,
            Vector128<byte> q2, Vector128<byte> q3, int limit, int interior, int hevThresh, out Vector128<sbyte> mask, out Vector128<sbyte> hev)
        {
            var dp = AbsDiff(p1, p0); var dq = AbsDiff(q1, q0);
            var inner = Vector128.Max(Vector128.Max(AbsDiff(p3, p2), AbsDiff(p2, p1)), Vector128.Max(AbsDiff(q3, q2), AbsDiff(q2, q1)));
            inner = Vector128.Max(inner, Vector128.Max(dp, dq));
            mask = (EdgeMask(p1, p0, q0, q1, Vector128.Create((byte)limit)) & NotAbove(inner, Vector128.Create((byte)interior))).AsSByte();
            hev = (~NotAbove(Vector128.Max(dp, dq), Vector128.Create((byte)hevThresh))).AsSByte();
        }

        /// <summary>The filter for edges between sub-blocks: changes p1, p0, q0, q1.</summary>
        static void Inner(Vector128<byte> p3, Vector128<byte> p2, ref Vector128<byte> p1, ref Vector128<byte> p0, ref Vector128<byte> q0, ref Vector128<byte> q1,
            Vector128<byte> q2, Vector128<byte> q3, int limit, int interior, int hevThresh)
        {
            Masks(p3, p2, p1, p0, q0, q1, q2, q3, limit, interior, hevThresh, out var mask, out var hev);
            var sp1 = (p1 ^ Sign).AsSByte(); var sq1 = (q1 ^ Sign).AsSByte();
            var sp0 = (p0 ^ Sign).AsSByte(); var sq0 = (q0 ^ Sign).AsSByte();
            var d = SubS(sq0, sp0);
            // the outer taps only take part where the edge variance is high
            var a = AddS(AddS(AddS(SubS(sp1, sq1) & hev, d), d), d) & mask;
            var f = Adjust(a, ref sp0, ref sq0);
            // elsewhere the next pixels move by half the step
            var half = Vector128.AndNot(Vector128.ShiftRightArithmetic(f + Vector128<sbyte>.One, 1), hev);
            sq1 = SubS(sq1, half);
            sp1 = AddS(sp1, half);
            p1 = sp1.AsByte() ^ Sign; p0 = sp0.AsByte() ^ Sign;
            q0 = sq0.AsByte() ^ Sign; q1 = sq1.AsByte() ^ Sign;
        }

        /// <summary>(k * w + 63) >> 7 on sixteen signed bytes.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector128<sbyte> Scaled(Vector128<short> lo, Vector128<short> hi, short k)
        {
            var c63 = Vector128.Create((short)63);
            var a = Vector128.ShiftRightArithmetic(lo * k + c63, 7);
            var b = Vector128.ShiftRightArithmetic(hi * k + c63, 7);
            return Vector128.Narrow(a, b);
        }

        /// <summary>The filter for edges between macroblocks: changes p2 .. q2.</summary>
        static void Macroblock(Vector128<byte> p3, ref Vector128<byte> p2, ref Vector128<byte> p1, ref Vector128<byte> p0, ref Vector128<byte> q0,
            ref Vector128<byte> q1, ref Vector128<byte> q2, Vector128<byte> q3, int limit, int interior, int hevThresh)
        {
            Masks(p3, p2, p1, p0, q0, q1, q2, q3, limit, interior, hevThresh, out var mask, out var hev);
            var sp2 = (p2 ^ Sign).AsSByte(); var sq2 = (q2 ^ Sign).AsSByte();
            var sp1 = (p1 ^ Sign).AsSByte(); var sq1 = (q1 ^ Sign).AsSByte();
            var sp0 = (p0 ^ Sign).AsSByte(); var sq0 = (q0 ^ Sign).AsSByte();
            var d = SubS(sq0, sp0);
            var w = AddS(AddS(AddS(SubS(sp1, sq1), d), d), d) & mask;
            // high edge variance: the simple adjustment of the two edge pixels
            Adjust(w & hev, ref sp0, ref sq0);
            // otherwise three pixels on either side move by 27/128, 18/128 and 9/128 of w
            var wl = Vector128.AndNot(w, hev);
            var (lo, hi) = Vector128.Widen(wl);
            var a = Scaled(lo, hi, 27);
            sq0 = SubS(sq0, a); sp0 = AddS(sp0, a);
            a = Scaled(lo, hi, 18);
            sq1 = SubS(sq1, a); sp1 = AddS(sp1, a);
            a = Scaled(lo, hi, 9);
            sq2 = SubS(sq2, a); sp2 = AddS(sp2, a);
            p2 = sp2.AsByte() ^ Sign; p1 = sp1.AsByte() ^ Sign; p0 = sp0.AsByte() ^ Sign;
            q0 = sq0.AsByte() ^ Sign; q1 = sq1.AsByte() ^ Sign; q2 = sq2.AsByte() ^ Sign;
        }

        // ---- getting at the pixels

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector128<byte> Pair(byte* u, byte* v) => Vector128.Create(Unsafe.ReadUnaligned<ulong>(u), Unsafe.ReadUnaligned<ulong>(v)).AsByte();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void StorePair(Vector128<byte> x, byte* u, byte* v)
        {
            Unsafe.WriteUnaligned(u, x.AsUInt64().GetElement(0));
            Unsafe.WriteUnaligned(v, x.AsUInt64().GetElement(1));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector128<byte> Row8(byte* p) => Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(p)).AsByte();

        /// <summary>
        /// Loads 8 pixels around a vertical edge from each of 16 rows (a: rows 0..7, b: rows 8..15) and turns them into
        /// eight vectors, one per column: p3 p2 p1 p0 q0 q1 q2 q3.
        /// </summary>
        static void LoadColumns(byte* a, byte* b, int stride, out Vector128<byte> c0, out Vector128<byte> c1, out Vector128<byte> c2, out Vector128<byte> c3,
            out Vector128<byte> c4, out Vector128<byte> c5, out Vector128<byte> c6, out Vector128<byte> c7)
        {
            var t0 = ZipLo8(Row8(a), Row8(a + stride)); var t1 = ZipLo8(Row8(a + 2 * stride), Row8(a + 3 * stride));
            var t2 = ZipLo8(Row8(a + 4 * stride), Row8(a + 5 * stride)); var t3 = ZipLo8(Row8(a + 6 * stride), Row8(a + 7 * stride));
            var t4 = ZipLo8(Row8(b), Row8(b + stride)); var t5 = ZipLo8(Row8(b + 2 * stride), Row8(b + 3 * stride));
            var t6 = ZipLo8(Row8(b + 4 * stride), Row8(b + 5 * stride)); var t7 = ZipLo8(Row8(b + 6 * stride), Row8(b + 7 * stride));
            var u0 = ZipLo16(t0, t1); var u1 = ZipHi16(t0, t1);     // rows 0..3: columns 0..3 / 4..7
            var u2 = ZipLo16(t2, t3); var u3 = ZipHi16(t2, t3);     // rows 4..7
            var u4 = ZipLo16(t4, t5); var u5 = ZipHi16(t4, t5);     // rows 8..11
            var u6 = ZipLo16(t6, t7); var u7 = ZipHi16(t6, t7);     // rows 12..15
            var v0 = ZipLo32(u0, u2); var v1 = ZipHi32(u0, u2); var v2 = ZipLo32(u1, u3); var v3 = ZipHi32(u1, u3);     // rows 0..7
            var w0 = ZipLo32(u4, u6); var w1 = ZipHi32(u4, u6); var w2 = ZipLo32(u5, u7); var w3 = ZipHi32(u5, u7);     // rows 8..15
            c0 = ZipLo64(v0, w0); c1 = ZipHi64(v0, w0); c2 = ZipLo64(v1, w1); c3 = ZipHi64(v1, w1);
            c4 = ZipLo64(v2, w2); c5 = ZipHi64(v2, w2); c6 = ZipLo64(v3, w3); c7 = ZipHi64(v3, w3);
        }

        static void StoreColumns(byte* a, byte* b, int stride, Vector128<byte> c0, Vector128<byte> c1, Vector128<byte> c2, Vector128<byte> c3,
            Vector128<byte> c4, Vector128<byte> c5, Vector128<byte> c6, Vector128<byte> c7)
        {
            var a0 = ZipLo8(c0, c1); var a1 = ZipHi8(c0, c1); var b0 = ZipLo8(c2, c3); var b1 = ZipHi8(c2, c3);
            var d0 = ZipLo8(c4, c5); var d1 = ZipHi8(c4, c5); var e0 = ZipLo8(c6, c7); var e1 = ZipHi8(c6, c7);
            StoreRows(a, stride, ZipLo16(a0, b0), ZipHi16(a0, b0), ZipLo16(d0, e0), ZipHi16(d0, e0));
            StoreRows(b, stride, ZipLo16(a1, b1), ZipHi16(a1, b1), ZipLo16(d1, e1), ZipHi16(d1, e1));
        }

        // f0 / f1: columns 0..3 of rows 0..3 / 4..7, g0 / g1: columns 4..7
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void StoreRows(byte* p, int stride, Vector128<byte> f0, Vector128<byte> f1, Vector128<byte> g0, Vector128<byte> g1)
        {
            StorePair(ZipLo32(f0, g0), p, p + stride);
            StorePair(ZipHi32(f0, g0), p + 2 * stride, p + 3 * stride);
            StorePair(ZipLo32(f1, g1), p + 4 * stride, p + 5 * stride);
            StorePair(ZipHi32(f1, g1), p + 6 * stride, p + 7 * stride);
        }

        // ---- entry points; p is the first pixel after the edge

        /// <summary>Horizontal edge over 16 luma pixels (the pixels across the edge lie one stride apart).</summary>
        public static void HorizontalEdge16(byte* p, int stride, int kind, int limit, int interior, int hev)
        {
            var p1 = Vector128.Load(p - 2 * stride); var p0 = Vector128.Load(p - stride);
            var q0 = Vector128.Load(p); var q1 = Vector128.Load(p + stride);
            if (kind == 0)
            {
                Simple(p1, ref p0, ref q0, q1, limit);
                p0.Store(p - stride); q0.Store(p);
                return;
            }
            var p3 = Vector128.Load(p - 4 * stride); var p2 = Vector128.Load(p - 3 * stride);
            var q2 = Vector128.Load(p + 2 * stride); var q3 = Vector128.Load(p + 3 * stride);
            if (kind == 1) Inner(p3, p2, ref p1, ref p0, ref q0, ref q1, q2, q3, limit, interior, hev);
            else
            {
                Macroblock(p3, ref p2, ref p1, ref p0, ref q0, ref q1, ref q2, q3, limit, interior, hev);
                p2.Store(p - 3 * stride); q2.Store(p + 2 * stride);
            }
            p1.Store(p - 2 * stride); p0.Store(p - stride); q0.Store(p); q1.Store(p + stride);
        }

        /// <summary>Horizontal edge over 8 pixels of each chroma plane.</summary>
        public static void HorizontalEdge8(byte* u, byte* v, int stride, int kind, int limit, int interior, int hev)
        {
            var p3 = Pair(u - 4 * stride, v - 4 * stride); var p2 = Pair(u - 3 * stride, v - 3 * stride);
            var p1 = Pair(u - 2 * stride, v - 2 * stride); var p0 = Pair(u - stride, v - stride);
            var q0 = Pair(u, v); var q1 = Pair(u + stride, v + stride);
            var q2 = Pair(u + 2 * stride, v + 2 * stride); var q3 = Pair(u + 3 * stride, v + 3 * stride);
            if (kind == 1) Inner(p3, p2, ref p1, ref p0, ref q0, ref q1, q2, q3, limit, interior, hev);
            else
            {
                Macroblock(p3, ref p2, ref p1, ref p0, ref q0, ref q1, ref q2, q3, limit, interior, hev);
                StorePair(p2, u - 3 * stride, v - 3 * stride); StorePair(q2, u + 2 * stride, v + 2 * stride);
            }
            StorePair(p1, u - 2 * stride, v - 2 * stride); StorePair(p0, u - stride, v - stride);
            StorePair(q0, u, v); StorePair(q1, u + stride, v + stride);
        }

        /// <summary>Vertical edge over 16 rows: a and b point at the first pixel right of the edge in rows 0 and 8
        /// (luma: b = a + 8 * stride; chroma: the two planes).</summary>
        public static void VerticalEdge(byte* a, byte* b, int stride, int kind, int limit, int interior, int hev)
        {
            LoadColumns(a - 4, b - 4, stride, out var p3, out var p2, out var p1, out var p0, out var q0, out var q1, out var q2, out var q3);
            if (kind == 0) Simple(p1, ref p0, ref q0, q1, limit);
            else if (kind == 1) Inner(p3, p2, ref p1, ref p0, ref q0, ref q1, q2, q3, limit, interior, hev);
            else Macroblock(p3, ref p2, ref p1, ref p0, ref q0, ref q1, ref q2, q3, limit, interior, hev);
            StoreColumns(a - 4, b - 4, stride, p3, p2, p1, p0, q0, q1, q2, q3);
        }
    }
}
