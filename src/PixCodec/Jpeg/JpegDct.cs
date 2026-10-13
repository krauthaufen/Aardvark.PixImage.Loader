using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace PixCodec.JpegImpl
{
    /// <summary>
    /// 8x8 forward and inverse DCT after Loeffler, Ligtenberg and Moschytz (11 multiplies per 1-D transform) in
    /// 13 bit fixed point. The inverse uses the same pass order, scaling and rounding as the IJG "slow integer"
    /// method, so decoded samples agree bit for bit with libjpeg based decoders.
    /// All code paths (scalar, 4 lane, 8 lane) produce identical results.
    /// </summary>
    internal static unsafe class JpegDct
    {
        const int ConstBits = 13;
        const int Pass1Bits = 2;

        // round(x * 2^13)
        const int F0_298631336 = 2446;
        const int F0_390180644 = 3196;
        const int F0_541196100 = 4433;
        const int F0_765366865 = 6270;
        const int F0_899976223 = 7373;
        const int F1_175875602 = 9633;
        const int F1_501321110 = 12299;
        const int F1_847759065 = 15137;
        const int F1_961570560 = 16069;
        const int F2_053119869 = 16819;
        const int F2_562915447 = 20995;
        const int F3_072711026 = 25172;

        /// <summary>Test hook: 0 forces the scalar path, 1 the generic Vector&lt;int&gt; path without AVX2 shuffles, -1 = automatic.</summary>
        internal static int ForceLevel = -1;

        static bool Use8 => Vector<int>.Count == 8 && Avx2.IsSupported && ForceLevel < 0;
        static bool UseVec => Vector.IsHardwareAccelerated && (Vector<int>.Count == 4 || Vector<int>.Count == 8) && ForceLevel != 0;

        // ---------------------------------------------------------------- inverse

        /// <summary>Block with only a DC coefficient: a flat 8x8 area.</summary>
        public static void IdctDc(int dc, byte* dst, nint stride)
        {
            int v = ((dc + 4) >> 3) + 128;
            ulong fill = (byte)Math.Clamp(v, 0, 255) * 0x0101010101010101UL;
            for (int y = 0; y < 8; y++, dst += stride) Unsafe.WriteUnaligned(dst, fill);
        }

        /// <summary>Dequantizes (natural order coefficients times natural order table) and inverse transforms one block.</summary>
        public static void Idct(short* coef, int* quant, byte* dst, nint stride)
        {
            if (Use8) Idct8(coef, quant, dst, stride);
            else if (UseVec) IdctVec(coef, quant, dst, stride);
            else IdctScalar(coef, quant, dst, stride);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void IdctPass(ref Vector<int> a0, ref Vector<int> a1, ref Vector<int> a2, ref Vector<int> a3,
            ref Vector<int> a4, ref Vector<int> a5, ref Vector<int> a6, ref Vector<int> a7, int shift)
        {
            var round = new Vector<int>(1 << (shift - 1));
            // even part: rotation of (2,6), butterflies with (0,4)
            var z1 = (a2 + a6) * F0_541196100;
            var t2 = z1 - a6 * F1_847759065;
            var t3 = z1 + a2 * F0_765366865;
            var t0 = Vector.ShiftLeft(a0 + a4, ConstBits) + round;
            var t1 = Vector.ShiftLeft(a0 - a4, ConstBits) + round;
            var t10 = t0 + t3; var t13 = t0 - t3;
            var t11 = t1 + t2; var t12 = t1 - t2;

            // odd part
            var o0 = a7; var o1 = a5; var o2 = a3; var o3 = a1;
            z1 = o0 + o3;
            var z2 = o1 + o2;
            var z3 = o0 + o2;
            var z4 = o1 + o3;
            var z5 = (z3 + z4) * F1_175875602;
            o0 *= F0_298631336;
            o1 *= F2_053119869;
            o2 *= F3_072711026;
            o3 *= F1_501321110;
            z1 *= -F0_899976223;
            z2 *= -F2_562915447;
            z3 = z3 * -F1_961570560 + z5;
            z4 = z4 * -F0_390180644 + z5;
            o0 += z1 + z3;
            o1 += z2 + z4;
            o2 += z2 + z3;
            o3 += z1 + z4;

            a0 = Vector.ShiftRightArithmetic(t10 + o3, shift);
            a7 = Vector.ShiftRightArithmetic(t10 - o3, shift);
            a1 = Vector.ShiftRightArithmetic(t11 + o2, shift);
            a6 = Vector.ShiftRightArithmetic(t11 - o2, shift);
            a2 = Vector.ShiftRightArithmetic(t12 + o1, shift);
            a5 = Vector.ShiftRightArithmetic(t12 - o1, shift);
            a3 = Vector.ShiftRightArithmetic(t13 + o0, shift);
            a4 = Vector.ShiftRightArithmetic(t13 - o0, shift);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Transpose8(ref Vector<int> r0, ref Vector<int> r1, ref Vector<int> r2, ref Vector<int> r3,
            ref Vector<int> r4, ref Vector<int> r5, ref Vector<int> r6, ref Vector<int> r7)
        {
            var t0 = Avx2.UnpackLow(r0.AsVector256(), r1.AsVector256()).AsInt64();
            var t1 = Avx2.UnpackHigh(r0.AsVector256(), r1.AsVector256()).AsInt64();
            var t2 = Avx2.UnpackLow(r2.AsVector256(), r3.AsVector256()).AsInt64();
            var t3 = Avx2.UnpackHigh(r2.AsVector256(), r3.AsVector256()).AsInt64();
            var t4 = Avx2.UnpackLow(r4.AsVector256(), r5.AsVector256()).AsInt64();
            var t5 = Avx2.UnpackHigh(r4.AsVector256(), r5.AsVector256()).AsInt64();
            var t6 = Avx2.UnpackLow(r6.AsVector256(), r7.AsVector256()).AsInt64();
            var t7 = Avx2.UnpackHigh(r6.AsVector256(), r7.AsVector256()).AsInt64();
            var u0 = Avx2.UnpackLow(t0, t2); var u1 = Avx2.UnpackHigh(t0, t2);
            var u2 = Avx2.UnpackLow(t1, t3); var u3 = Avx2.UnpackHigh(t1, t3);
            var u4 = Avx2.UnpackLow(t4, t6); var u5 = Avx2.UnpackHigh(t4, t6);
            var u6 = Avx2.UnpackLow(t5, t7); var u7 = Avx2.UnpackHigh(t5, t7);
            r0 = Avx2.Permute2x128(u0, u4, 0x20).AsInt32().AsVector();
            r1 = Avx2.Permute2x128(u1, u5, 0x20).AsInt32().AsVector();
            r2 = Avx2.Permute2x128(u2, u6, 0x20).AsInt32().AsVector();
            r3 = Avx2.Permute2x128(u3, u7, 0x20).AsInt32().AsVector();
            r4 = Avx2.Permute2x128(u0, u4, 0x31).AsInt32().AsVector();
            r5 = Avx2.Permute2x128(u1, u5, 0x31).AsInt32().AsVector();
            r6 = Avx2.Permute2x128(u2, u6, 0x31).AsInt32().AsVector();
            r7 = Avx2.Permute2x128(u3, u7, 0x31).AsInt32().AsVector();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void StoreRow8(Vector<int> v, byte* dst)
        {
            var x = (v + new Vector<int>(128)).AsVector256();
            var s = Sse2.PackSignedSaturate(x.GetLower(), x.GetUpper());
            Sse2.StoreScalar((long*)dst, Sse2.PackUnsignedSaturate(s, s).AsInt64());
        }

        static void Idct8(short* coef, int* quant, byte* dst, nint stride)
        {
            Vector.Widen(Vector.Load(coef), out var r0, out var r1);
            Vector.Widen(Vector.Load(coef + 16), out var r2, out var r3);
            Vector.Widen(Vector.Load(coef + 32), out var r4, out var r5);
            Vector.Widen(Vector.Load(coef + 48), out var r6, out var r7);
            r0 *= Vector.Load(quant); r1 *= Vector.Load(quant + 8);
            r2 *= Vector.Load(quant + 16); r3 *= Vector.Load(quant + 24);
            r4 *= Vector.Load(quant + 32); r5 *= Vector.Load(quant + 40);
            r6 *= Vector.Load(quant + 48); r7 *= Vector.Load(quant + 56);
            IdctPass(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7, ConstBits - Pass1Bits);
            Transpose8(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
            IdctPass(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7, ConstBits + Pass1Bits + 3);
            Transpose8(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
            StoreRow8(r0, dst); dst += stride;
            StoreRow8(r1, dst); dst += stride;
            StoreRow8(r2, dst); dst += stride;
            StoreRow8(r3, dst); dst += stride;
            StoreRow8(r4, dst); dst += stride;
            StoreRow8(r5, dst); dst += stride;
            StoreRow8(r6, dst); dst += stride;
            StoreRow8(r7, dst);
        }

        static void TransposeInPlace(int* m)
        {
            for (int i = 0; i < 8; i++)
                for (int j = i + 1; j < 8; j++)
                    (m[i * 8 + j], m[j * 8 + i]) = (m[j * 8 + i], m[i * 8 + j]);
        }

        /// <summary>Runs one 1-D pass down the columns of the 8x8 int matrix using whatever vector width is available.</summary>
        static void VecPass(int* m, int shift, bool inverse)
        {
            int n = Vector<int>.Count;
            for (int x = 0; x < 8; x += n)
            {
                var a0 = Vector.Load(m + x); var a1 = Vector.Load(m + 8 + x);
                var a2 = Vector.Load(m + 16 + x); var a3 = Vector.Load(m + 24 + x);
                var a4 = Vector.Load(m + 32 + x); var a5 = Vector.Load(m + 40 + x);
                var a6 = Vector.Load(m + 48 + x); var a7 = Vector.Load(m + 56 + x);
                if (inverse) IdctPass(ref a0, ref a1, ref a2, ref a3, ref a4, ref a5, ref a6, ref a7, shift);
                else FdctPass(ref a0, ref a1, ref a2, ref a3, ref a4, ref a5, ref a6, ref a7, shift != 0);
                Vector.Store(a0, m + x); Vector.Store(a1, m + 8 + x);
                Vector.Store(a2, m + 16 + x); Vector.Store(a3, m + 24 + x);
                Vector.Store(a4, m + 32 + x); Vector.Store(a5, m + 40 + x);
                Vector.Store(a6, m + 48 + x); Vector.Store(a7, m + 56 + x);
            }
        }

        static void IdctVec(short* coef, int* quant, byte* dst, nint stride)
        {
            int* m = stackalloc int[64];
            for (int i = 0; i < 64; i++) m[i] = coef[i] * quant[i];
            VecPass(m, ConstBits - Pass1Bits, true);
            TransposeInPlace(m);
            VecPass(m, ConstBits + Pass1Bits + 3, true);
            for (int y = 0; y < 8; y++, dst += stride)
                for (int x = 0; x < 8; x++)
                    dst[x] = (byte)Math.Clamp(m[x * 8 + y] + 128, 0, 255);
        }

        static void IdctScalar(short* coef, int* quant, byte* dst, nint stride)
        {
            int* m = stackalloc int[64];
            for (int i = 0; i < 64; i++) m[i] = coef[i] * quant[i];
            for (int x = 0; x < 8; x++) IdctScalar1D(m + x, 8, ConstBits - Pass1Bits);
            for (int y = 0; y < 8; y++) IdctScalar1D(m + y * 8, 1, ConstBits + Pass1Bits + 3);
            for (int y = 0; y < 8; y++, dst += stride)
                for (int x = 0; x < 8; x++)
                    dst[x] = (byte)Math.Clamp(m[y * 8 + x] + 128, 0, 255);
        }

        static void IdctScalar1D(int* p, int s, int shift)
        {
            int round = 1 << (shift - 1);
            int a0 = p[0], a1 = p[s], a2 = p[2 * s], a3 = p[3 * s], a4 = p[4 * s], a5 = p[5 * s], a6 = p[6 * s], a7 = p[7 * s];
            int z1 = (a2 + a6) * F0_541196100;
            int t2 = z1 - a6 * F1_847759065;
            int t3 = z1 + a2 * F0_765366865;
            int t0 = ((a0 + a4) << ConstBits) + round;
            int t1 = ((a0 - a4) << ConstBits) + round;
            int t10 = t0 + t3, t13 = t0 - t3, t11 = t1 + t2, t12 = t1 - t2;

            int o0 = a7, o1 = a5, o2 = a3, o3 = a1;
            z1 = o0 + o3;
            int z2 = o1 + o2, z3 = o0 + o2, z4 = o1 + o3;
            int z5 = (z3 + z4) * F1_175875602;
            o0 *= F0_298631336;
            o1 *= F2_053119869;
            o2 *= F3_072711026;
            o3 *= F1_501321110;
            z1 *= -F0_899976223;
            z2 *= -F2_562915447;
            z3 = z3 * -F1_961570560 + z5;
            z4 = z4 * -F0_390180644 + z5;
            o0 += z1 + z3;
            o1 += z2 + z4;
            o2 += z2 + z3;
            o3 += z1 + z4;

            p[0] = (t10 + o3) >> shift; p[7 * s] = (t10 - o3) >> shift;
            p[s] = (t11 + o2) >> shift; p[6 * s] = (t11 - o2) >> shift;
            p[2 * s] = (t12 + o1) >> shift; p[5 * s] = (t12 - o1) >> shift;
            p[3 * s] = (t13 + o0) >> shift; p[4 * s] = (t13 - o0) >> shift;
        }

        // ---------------------------------------------------------------- forward

        /// <summary>
        /// Forward DCT of an 8x8 sample block (level shifted by -128) followed by quantization with the reciprocal
        /// table (1 / (8 * q), natural order). Writes the 64 quantized coefficients in zigzag order.
        /// </summary>
        public static void FdctQuantize(byte* src, nint stride, float* recip, short* zigzagOut)
        {
            int* m = stackalloc int[64];
            if (Use8)
            {
                var bias = new Vector<int>(128);
                var r0 = LoadRow8(src) - bias; var r1 = LoadRow8(src + stride) - bias;
                var r2 = LoadRow8(src + 2 * stride) - bias; var r3 = LoadRow8(src + 3 * stride) - bias;
                var r4 = LoadRow8(src + 4 * stride) - bias; var r5 = LoadRow8(src + 5 * stride) - bias;
                var r6 = LoadRow8(src + 6 * stride) - bias; var r7 = LoadRow8(src + 7 * stride) - bias;
                FdctPass(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7, false);
                Transpose8(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
                FdctPass(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7, true);
                // the matrix is transposed now; the transposed zigzag table below undoes that
                Vector.Store(Quantize(r0, recip), m); Vector.Store(Quantize(r1, recip + 8), m + 8);
                Vector.Store(Quantize(r2, recip + 16), m + 16); Vector.Store(Quantize(r3, recip + 24), m + 24);
                Vector.Store(Quantize(r4, recip + 32), m + 32); Vector.Store(Quantize(r5, recip + 40), m + 40);
                Vector.Store(Quantize(r6, recip + 48), m + 48); Vector.Store(Quantize(r7, recip + 56), m + 56);
            }
            else
            {
                // m is kept transposed (m[x * 8 + y]) so both paths share the same layout
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                        m[y * 8 + x] = src[y * stride + x] - 128;
                if (UseVec)
                {
                    VecPass(m, 0, false);
                    TransposeInPlace(m);
                    VecPass(m, 1, false);
                    int n = Vector<int>.Count;
                    for (int i = 0; i < 64; i += n) Vector.Store(Quantize(Vector.Load(m + i), recip + i), m + i);
                }
                else
                {
                    for (int x = 0; x < 8; x++) FdctScalar1D(m + x, 8, false);
                    TransposeInPlace(m);
                    for (int x = 0; x < 8; x++) FdctScalar1D(m + x, 8, true);
                    for (int i = 0; i < 64; i++)
                    {
                        float f = m[i] * recip[i];
                        m[i] = (int)(f + (f < 0 ? -0.5f : 0.5f));
                    }
                }
            }
            byte* zz = JpegTables.ZigZag;
            for (int i = 0; i < 64; i++)
            {
                int n = zz[i];
                zigzagOut[i] = (short)m[(n & 7) * 8 + (n >> 3)];
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector<int> LoadRow8(byte* p)
            => Avx2.ConvertToVector256Int32(Sse2.LoadScalarVector128((ulong*)p).AsByte()).AsVector();

        /// <summary>recip is indexed transposed relative to the natural order (see <see cref="FdctQuantize"/>).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector<int> Quantize(Vector<int> v, float* recip)
        {
            var f = Vector.ConvertToSingle(v) * Vector.Load(recip);
            // round half away from zero: add 0.5 carrying the sign of f, then truncate
            var half = Vector.BitwiseOr(Vector.BitwiseAnd(f, new Vector<float>(-0.0f)), new Vector<float>(0.5f));
            return Vector.ConvertToInt32(f + half);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void FdctPass(ref Vector<int> a0, ref Vector<int> a1, ref Vector<int> a2, ref Vector<int> a3,
            ref Vector<int> a4, ref Vector<int> a5, ref Vector<int> a6, ref Vector<int> a7, bool second)
        {
            var t0 = a0 + a7; var t7 = a0 - a7;
            var t1 = a1 + a6; var t6 = a1 - a6;
            var t2 = a2 + a5; var t5 = a2 - a5;
            var t3 = a3 + a4; var t4 = a3 - a4;
            var t10 = t0 + t3; var t13 = t0 - t3;
            var t11 = t1 + t2; var t12 = t1 - t2;

            int shift = second ? ConstBits + Pass1Bits : ConstBits - Pass1Bits;
            var round = new Vector<int>(1 << (shift - 1));
            if (second)
            {
                var r2 = new Vector<int>(1 << (Pass1Bits - 1));
                a0 = Vector.ShiftRightArithmetic(t10 + t11 + r2, Pass1Bits);
                a4 = Vector.ShiftRightArithmetic(t10 - t11 + r2, Pass1Bits);
            }
            else
            {
                a0 = Vector.ShiftLeft(t10 + t11, Pass1Bits);
                a4 = Vector.ShiftLeft(t10 - t11, Pass1Bits);
            }
            var z1 = (t12 + t13) * F0_541196100 + round;
            a2 = Vector.ShiftRightArithmetic(z1 + t13 * F0_765366865, shift);
            a6 = Vector.ShiftRightArithmetic(z1 - t12 * F1_847759065, shift);

            z1 = t4 + t7;
            var z2 = t5 + t6;
            var z3 = t4 + t6;
            var z4 = t5 + t7;
            var z5 = (z3 + z4) * F1_175875602 + round;
            t4 *= F0_298631336;
            t5 *= F2_053119869;
            t6 *= F3_072711026;
            t7 *= F1_501321110;
            z1 *= -F0_899976223;
            z2 *= -F2_562915447;
            z3 = z3 * -F1_961570560 + z5;
            z4 = z4 * -F0_390180644 + z5;
            a7 = Vector.ShiftRightArithmetic(t4 + z1 + z3, shift);
            a5 = Vector.ShiftRightArithmetic(t5 + z2 + z4, shift);
            a3 = Vector.ShiftRightArithmetic(t6 + z2 + z3, shift);
            a1 = Vector.ShiftRightArithmetic(t7 + z1 + z4, shift);
        }

        static void FdctScalar1D(int* p, int s, bool second)
        {
            int a0 = p[0], a1 = p[s], a2 = p[2 * s], a3 = p[3 * s], a4 = p[4 * s], a5 = p[5 * s], a6 = p[6 * s], a7 = p[7 * s];
            int t0 = a0 + a7, t7 = a0 - a7, t1 = a1 + a6, t6 = a1 - a6, t2 = a2 + a5, t5 = a2 - a5, t3 = a3 + a4, t4 = a3 - a4;
            int t10 = t0 + t3, t13 = t0 - t3, t11 = t1 + t2, t12 = t1 - t2;
            int shift = second ? ConstBits + Pass1Bits : ConstBits - Pass1Bits;
            int round = 1 << (shift - 1);
            if (second)
            {
                p[0] = (t10 + t11 + (1 << (Pass1Bits - 1))) >> Pass1Bits;
                p[4 * s] = (t10 - t11 + (1 << (Pass1Bits - 1))) >> Pass1Bits;
            }
            else
            {
                p[0] = (t10 + t11) << Pass1Bits;
                p[4 * s] = (t10 - t11) << Pass1Bits;
            }
            int z1 = (t12 + t13) * F0_541196100 + round;
            p[2 * s] = (z1 + t13 * F0_765366865) >> shift;
            p[6 * s] = (z1 - t12 * F1_847759065) >> shift;

            z1 = t4 + t7;
            int z2 = t5 + t6, z3 = t4 + t6, z4 = t5 + t7;
            int z5 = (z3 + z4) * F1_175875602 + round;
            t4 *= F0_298631336;
            t5 *= F2_053119869;
            t6 *= F3_072711026;
            t7 *= F1_501321110;
            z1 *= -F0_899976223;
            z2 *= -F2_562915447;
            z3 = z3 * -F1_961570560 + z5;
            z4 = z4 * -F0_390180644 + z5;
            p[7 * s] = (t4 + z1 + z3) >> shift;
            p[5 * s] = (t5 + z2 + z4) >> shift;
            p[3 * s] = (t6 + z2 + z3) >> shift;
            p[s] = (t7 + z1 + z4) >> shift;
        }
    }
}
