using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace PixCodec.JpegImpl
{
    /// <summary>
    /// Decoder back end: brings every component plane to full resolution row by row and converts to the output
    /// color model. Upsampling of 2:1 ratios uses the same triangle filters (and rounding) as libjpeg's "fancy"
    /// upsampling, everything else is sample replication.
    /// </summary>
    internal static unsafe class JpegPost
    {
        /// <summary>Test hook: disables the vectorized paths.</summary>
        internal static bool ForceScalar;

        sealed class Upsampler
        {
            public JpegComponent C = null!;
            public byte[] LineArr = null!;
            public ushort[] SumArr = null!;
            public int CachedRow = -1;
        }

        public static void Assemble(JpegDecoder d, byte[] output) => Assemble(d, output, d.OutWidth, d.OutHeight, d.ColorSpace);

        /// <summary>Writes the top left w x h pixels (at most the output size of the frame) in the given color handling.</summary>
        public static void Assemble(JpegDecoder d, Span<byte> output, int w, int h, JpegColorSpace cs)
        {
            int nc = d.Comps.Length;
            var ups = new Upsampler[nc];
            for (int i = 0; i < nc; i++)
            {
                var c = d.Comps[i];
                // generous slack: vector loops store whole vectors
                ups[i] = new Upsampler { C = c, LineArr = new byte[Math.Max(d.OutWidth, c.SampledW * c.HExp) + 128], SumArr = new ushort[c.SampledW + 2 + 64] };
            }

            byte[]? tmpRgb = cs is JpegColorSpace.Ycck or JpegColorSpace.YcckToCmyk ? new byte[w * 3 + 64] : null;
            byte** lines = stackalloc byte*[4];
            fixed (byte* outp = output)
            fixed (byte* l0 = ups[0].LineArr, l1 = ups[nc > 1 ? 1 : 0].LineArr, l2 = ups[nc > 2 ? 2 : 0].LineArr, l3 = ups[nc > 3 ? 3 : 0].LineArr)
            fixed (ushort* s0 = ups[0].SumArr, s1 = ups[nc > 1 ? 1 : 0].SumArr, s2 = ups[nc > 2 ? 2 : 0].SumArr, s3 = ups[nc > 3 ? 3 : 0].SumArr)
            fixed (byte* tmp = tmpRgb)
            {
                byte** lineBuf = stackalloc byte*[4];
                ushort** sumBuf = stackalloc ushort*[4];
                lineBuf[0] = l0; lineBuf[1] = l1; lineBuf[2] = l2; lineBuf[3] = l3;
                sumBuf[0] = s0; sumBuf[1] = s1; sumBuf[2] = s2; sumBuf[3] = s3;
                int outChannels = cs == JpegColorSpace.Gray ? 1 : cs is JpegColorSpace.Raw or JpegColorSpace.Rgb ? nc : cs == JpegColorSpace.YcckToCmyk ? 4 : 3;
                for (int y = 0; y < h; y++)
                {
                    for (int i = 0; i < nc; i++) lines[i] = GetLine(ups[i], y, w, lineBuf[i], sumBuf[i] + 1);
                    byte* dst = outp + (nint)y * w * outChannels;
                    switch (cs)
                    {
                        case JpegColorSpace.Gray:
                            Buffer.MemoryCopy(lines[0], dst, w, w);
                            break;
                        case JpegColorSpace.YCbCr:
                            YccToRgb(lines[0], lines[1], lines[2], dst, w);
                            break;
                        case JpegColorSpace.Rgb:
                        case JpegColorSpace.Raw:
                            for (int i = 0; i < nc; i++)
                            {
                                byte* src = lines[i];
                                for (int x = 0; x < w; x++) dst[x * nc + i] = src[x];
                            }
                            break;
                        case JpegColorSpace.Cmyk:
                            CmykToRgb(lines[0], lines[1], lines[2], lines[3], 1, dst, w);
                            break;
                        case JpegColorSpace.Ycck:
                            // YCC decodes to RGB = 255 - (stored CMY), so its complement is the stored value
                            YccToRgb(lines[0], lines[1], lines[2], tmp, w);
                            for (int x = 0; x < w * 3; x++) tmp[x] = (byte)(255 - tmp[x]);
                            CmykToRgb(tmp, tmp + 1, tmp + 2, lines[3], 3, dst, w);
                            break;
                        case JpegColorSpace.YcckToCmyk:
                            YccToRgb(lines[0], lines[1], lines[2], tmp, w);
                            for (int x = 0; x < w; x++, dst += 4)
                            {
                                dst[0] = (byte)(255 - tmp[3 * x]); dst[1] = (byte)(255 - tmp[3 * x + 1]); dst[2] = (byte)(255 - tmp[3 * x + 2]);
                                dst[3] = lines[3][x];
                            }
                            break;
                    }
                }
            }
        }

        /// <summary>
        /// 4 component files carry inverted ink values (Adobe convention: 255 = no ink), so the plain
        /// multiplicative model RGB = (1 - C) * (1 - K) becomes stored_c * stored_k / 255, rounded to nearest.
        /// </summary>
        static void CmykToRgb(byte* c, byte* m, byte* y, byte* k, int step, byte* dst, int w)
        {
            for (int x = 0; x < w; x++, dst += 3)
            {
                int kk = k[x];
                dst[0] = (byte)((c[x * step] * kk + 127) / 255);
                dst[1] = (byte)((m[x * step] * kk + 127) / 255);
                dst[2] = (byte)((y[x * step] * kk + 127) / 255);
            }
        }

        // ---------------------------------------------------------------- upsampling

        static byte* GetLine(Upsampler u, int y, int w, byte* line, ushort* sum)
        {
            var c = u.C;
            byte* plane = c.Plane;
            nint stride = c.Stride;
            int n = c.SampledW;
            switch (c.Upsample)
            {
                case JpegUpsample.Direct:
                    return plane + y * stride;

                case JpegUpsample.Replicate:
                {
                    int sy = y / c.VExp;
                    byte* src = plane + sy * stride;
                    if (c.HExp == 1) return src;
                    if (sy != u.CachedRow)
                    {
                        u.CachedRow = sy;
                        int e = c.HExp;
                        byte* o = line;
                        if (e == 2)
                            for (int x = 0; x < n; x++) { o[2 * x] = src[x]; o[2 * x + 1] = src[x]; }
                        else
                            for (int x = 0; x < n; x++, o += e) new Span<byte>(o, e).Fill(src[x]);
                    }
                    return line;
                }

                case JpegUpsample.Fractional:
                {
                    // Ratios that are not whole numbers (libjpeg rejects such files): bilinear interpolation with
                    // sample centers aligned, positions in 1/256 sample units.
                    int py = Math.Clamp(((2 * y + 1) * c.VNum * 256 / c.VDen - 256) / 2, 0, (c.SampledH - 1) * 256);
                    int y0 = py >> 8, fy = py & 255;
                    byte* r0 = plane + y0 * stride, r1 = plane + Math.Min(y0 + 1, c.SampledH - 1) * stride;
                    for (int x = 0; x < w; x++)
                    {
                        int px = Math.Clamp(((2 * x + 1) * c.HNum * 256 / c.HDen - 256) / 2, 0, (n - 1) * 256);
                        int x0 = px >> 8, fx = px & 255, x1 = Math.Min(x0 + 1, n - 1);
                        int top = r0[x0] * (256 - fx) + r0[x1] * fx, bottom = r1[x0] * (256 - fx) + r1[x1] * fx;
                        line[x] = (byte)((top * (256 - fy) + bottom * fy + 32768) >> 16);
                    }
                    return line;
                }

                case JpegUpsample.H2V1:
                    Widen(plane + y * stride, sum, n);
                    Horizontal(sum, n, line, 1, 2, 2);
                    return line;

                case JpegUpsample.H2V2:
                {
                    int sy = y >> 1;
                    // the nearer of the two neighbouring rows gets weight 1/4, clamped at the image edge
                    int near = (y & 1) == 0 ? Math.Max(sy - 1, 0) : Math.Min(sy + 1, c.SampledH - 1);
                    Sum3(plane + sy * stride, plane + near * stride, sum, n);
                    Horizontal(sum, n, line, 8, 7, 4);
                    return line;
                }

                default: // H1V2
                {
                    int sy = y >> 1;
                    int near = (y & 1) == 0 ? Math.Max(sy - 1, 0) : Math.Min(sy + 1, c.SampledH - 1);
                    Blend(plane + sy * stride, plane + near * stride, line, n, (y & 1) == 0 ? 1 : 2);
                    return line;
                }
            }
        }

        static bool UseVec => Vector.IsHardwareAccelerated && BitConverter.IsLittleEndian && !ForceScalar;

        static void Widen(byte* src, ushort* sum, int n)
        {
            int i = 0;
            if (UseVec)
            {
                int vc = Vector<byte>.Count;
                for (; i + vc <= n; i += vc)
                {
                    Vector.Widen(Vector.Load(src + i), out Vector<ushort> lo, out Vector<ushort> hi);
                    Vector.Store(lo, sum + i);
                    Vector.Store(hi, sum + i + vc / 2);
                }
            }
            for (; i < n; i++) sum[i] = src[i];
            sum[-1] = sum[0];
            sum[n] = sum[n - 1];
        }

        /// <summary>sum = 3 * cur + near (vertical half of the 2x2 triangle filter).</summary>
        static void Sum3(byte* cur, byte* near, ushort* sum, int n)
        {
            int i = 0;
            if (UseVec)
            {
                int vc = Vector<byte>.Count;
                for (; i + vc <= n; i += vc)
                {
                    Vector.Widen(Vector.Load(cur + i), out Vector<ushort> clo, out Vector<ushort> chi);
                    Vector.Widen(Vector.Load(near + i), out Vector<ushort> nlo, out Vector<ushort> nhi);
                    Vector.Store(clo * (ushort)3 + nlo, sum + i);
                    Vector.Store(chi * (ushort)3 + nhi, sum + i + vc / 2);
                }
            }
            for (; i < n; i++) sum[i] = (ushort)(3 * cur[i] + near[i]);
            sum[-1] = sum[0];
            sum[n] = sum[n - 1];
        }

        /// <summary>
        /// Horizontal 2x triangle filter: out[2i] = (3 s[i] + s[i-1] + biasEven) >> shift, out[2i+1] = (3 s[i] + s[i+1] + biasOdd) >> shift.
        /// s[-1] and s[n] must hold the replicated edge samples.
        /// </summary>
        static void Horizontal(ushort* s, int n, byte* o, int biasEven, int biasOdd, int shift)
        {
            int i = 0;
            if (UseVec)
            {
                int vc = Vector<ushort>.Count;
                var be = new Vector<ushort>((ushort)biasEven);
                var bo = new Vector<ushort>((ushort)biasOdd);
                for (; i + vc <= n; i += vc)
                {
                    var c3 = Vector.Load(s + i) * (ushort)3;
                    var even = Vector.ShiftRightLogical(c3 + Vector.Load(s + i - 1) + be, shift);
                    var odd = Vector.ShiftRightLogical(c3 + Vector.Load(s + i + 1) + bo, shift);
                    // little endian: low byte of each 16 bit lane is the even output sample
                    Vector.Store(even | Vector.ShiftLeft(odd, 8), (ushort*)(o + 2 * i));
                }
            }
            for (; i < n; i++)
            {
                int c3 = 3 * s[i];
                o[2 * i] = (byte)((c3 + s[i - 1] + biasEven) >> shift);
                o[2 * i + 1] = (byte)((c3 + s[i + 1] + biasOdd) >> shift);
            }
        }

        /// <summary>out = (3 * cur + near + bias) >> 2 (vertical-only triangle filter).</summary>
        static void Blend(byte* cur, byte* near, byte* o, int n, int bias)
        {
            int i = 0;
            if (UseVec)
            {
                int vc = Vector<byte>.Count;
                var b = new Vector<ushort>((ushort)bias);
                for (; i + vc <= n; i += vc)
                {
                    Vector.Widen(Vector.Load(cur + i), out Vector<ushort> clo, out Vector<ushort> chi);
                    Vector.Widen(Vector.Load(near + i), out Vector<ushort> nlo, out Vector<ushort> nhi);
                    clo = Vector.ShiftRightLogical(clo * (ushort)3 + nlo + b, 2);
                    chi = Vector.ShiftRightLogical(chi * (ushort)3 + nhi + b, 2);
                    Vector.Store(Vector.Narrow(clo, chi), o + i);
                }
            }
            for (; i < n; i++) o[i] = (byte)((3 * cur[i] + near[i] + bias) >> 2);
        }

        // ---------------------------------------------------------------- color

        // ITU-R BT.601 full range, 16 bit fixed point (the JFIF conversion):
        //   R = Y + 1.402 Cr, G = Y - 0.344136 Cb - 0.714136 Cr, B = Y + 1.772 Cb
        const int CrR = 91881, CbG = -22554, CrG = -46802, CbB = 116130;

        public static void YccToRgb(byte* yp, byte* cbp, byte* crp, byte* dst, int w)
        {
            int x = 0;
            if (Avx2.IsSupported && !ForceScalar)
            {
                // 16 bit lanes via pmaddwd on (cb, cr) pairs; coefficients are reduced mod 65536 and the
                // dropped whole multiples of cb / cr are added back afterwards.
                var kR = Vector256.Create(0, CrR - 65536, 0, CrR - 65536, 0, CrR - 65536, 0, CrR - 65536, 0, CrR - 65536, 0, CrR - 65536, 0, CrR - 65536, 0, CrR - 65536).AsInt16();
                var kG = Vector256.Create((short)CbG, (short)(CrG + 65536), (short)CbG, (short)(CrG + 65536), (short)CbG, (short)(CrG + 65536), (short)CbG, (short)(CrG + 65536),
                    (short)CbG, (short)(CrG + 65536), (short)CbG, (short)(CrG + 65536), (short)CbG, (short)(CrG + 65536), (short)CbG, (short)(CrG + 65536));
                var kB = Vector256.Create((short)(CbB - 131072), 0, (short)(CbB - 131072), 0, (short)(CbB - 131072), 0, (short)(CbB - 131072), 0,
                    (short)(CbB - 131072), 0, (short)(CbB - 131072), 0, (short)(CbB - 131072), 0, (short)(CbB - 131072), 0);
                var half = Vector256.Create(32768);
                var c128 = Vector256.Create((short)128);
                var pack = Vector256.Create((byte)0, 1, 2, 4, 5, 6, 8, 9, 10, 12, 13, 14, 0x80, 0x80, 0x80, 0x80,
                    0, 1, 2, 4, 5, 6, 8, 9, 10, 12, 13, 14, 0x80, 0x80, 0x80, 0x80);
                // each iteration stores 52 bytes for 16 pixels, so stay 2 pixels clear of the row end
                for (; x + 18 <= w; x += 16, dst += 48)
                {
                    var yv = Avx2.ConvertToVector256Int16(yp + x);
                    var cb = Avx2.Subtract(Avx2.ConvertToVector256Int16(cbp + x), c128);
                    var cr = Avx2.Subtract(Avx2.ConvertToVector256Int16(crp + x), c128);
                    var lo = Avx2.UnpackLow(cb, cr);
                    var hi = Avx2.UnpackHigh(cb, cr);
                    var r = Avx2.PackSignedSaturate(
                        Avx2.ShiftRightArithmetic(Avx2.Add(Avx2.MultiplyAddAdjacent(lo, kR), half), 16),
                        Avx2.ShiftRightArithmetic(Avx2.Add(Avx2.MultiplyAddAdjacent(hi, kR), half), 16));
                    var g = Avx2.PackSignedSaturate(
                        Avx2.ShiftRightArithmetic(Avx2.Add(Avx2.MultiplyAddAdjacent(lo, kG), half), 16),
                        Avx2.ShiftRightArithmetic(Avx2.Add(Avx2.MultiplyAddAdjacent(hi, kG), half), 16));
                    var b = Avx2.PackSignedSaturate(
                        Avx2.ShiftRightArithmetic(Avx2.Add(Avx2.MultiplyAddAdjacent(lo, kB), half), 16),
                        Avx2.ShiftRightArithmetic(Avx2.Add(Avx2.MultiplyAddAdjacent(hi, kB), half), 16));
                    r = Avx2.Add(Avx2.Add(r, cr), yv);
                    g = Avx2.Add(Avx2.Subtract(g, cr), yv);
                    b = Avx2.Add(Avx2.Add(b, Avx2.Add(cb, cb)), yv);
                    var rb = Avx2.PackUnsignedSaturate(r, r);
                    var gb = Avx2.PackUnsignedSaturate(g, g);
                    var bb = Avx2.PackUnsignedSaturate(b, b);
                    var rg = Avx2.UnpackLow(rb, gb).AsInt16();
                    var bx = Avx2.UnpackLow(bb, Vector256<byte>.Zero).AsInt16();
                    var p0 = Avx2.Shuffle(Avx2.UnpackLow(rg, bx).AsByte(), pack);
                    var p1 = Avx2.Shuffle(Avx2.UnpackHigh(rg, bx).AsByte(), pack);
                    Sse2.Store(dst, p0.GetLower());
                    Sse2.Store(dst + 12, p1.GetLower());
                    Sse2.Store(dst + 24, p0.GetUpper());
                    Sse2.Store(dst + 36, p1.GetUpper());
                }
            }
            else if (Vector128.IsHardwareAccelerated && BitConverter.IsLittleEndian && !ForceScalar)
            {
                // portable variant: 4 pixels per step in 32 bit lanes; each step stores 16 bytes for 12
                var half = Vector128.Create(32768);
                var c128 = Vector128.Create(128);
                var max = Vector128.Create(255);
                for (; x + 6 <= w; x += 4, dst += 12)
                {
                    var yv = Load4(yp + x);
                    var cb = Load4(cbp + x) - c128;
                    var cr = Load4(crp + x) - c128;
                    var r = yv + Vector128.ShiftRightArithmetic(cr * CrR + half, 16);
                    var g = yv + Vector128.ShiftRightArithmetic(cb * CbG + cr * CrG + half, 16);
                    var b = yv + Vector128.ShiftRightArithmetic(cb * CbB + half, 16);
                    r = Vector128.Min(Vector128.Max(r, Vector128<int>.Zero), max);
                    g = Vector128.Min(Vector128.Max(g, Vector128<int>.Zero), max);
                    b = Vector128.Min(Vector128.Max(b, Vector128<int>.Zero), max);
                    var px = (r | Vector128.ShiftLeft(g, 8) | Vector128.ShiftLeft(b, 16)).AsByte();
                    Vector128.Shuffle(px, Vector128.Create((byte)0, 1, 2, 4, 5, 6, 8, 9, 10, 12, 13, 14, 0x80, 0x80, 0x80, 0x80)).Store(dst);
                }
            }
            for (; x < w; x++, dst += 3)
            {
                int yy = yp[x], cb = cbp[x] - 128, cr = crp[x] - 128;
                dst[0] = Clamp(yy + ((CrR * cr + 32768) >> 16));
                dst[1] = Clamp(yy + ((CbG * cb + CrG * cr + 32768) >> 16));
                dst[2] = Clamp(yy + ((CbB * cb + 32768) >> 16));
            }
        }

        /// <summary>4 bytes zero extended to 32 bit lanes.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector128<int> Load4(byte* p)
            => Vector128.WidenLower(Vector128.WidenLower(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<uint>(p)).AsByte())).AsInt32();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static byte Clamp(int v) => (byte)((uint)v <= 255 ? v : v < 0 ? 0 : 255);
    }
}
