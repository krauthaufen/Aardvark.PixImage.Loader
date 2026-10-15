using System;

namespace Aardvark.ImageCodecs.JpegImpl
{
    /// <summary>
    /// Decoder back end for 16 bit planes (12 bit DCT frames and lossless frames): the same upsampling filters and
    /// color conversions as <see cref="JpegPost"/> in plain scalar code, for any sample precision.
    /// </summary>
    internal static unsafe class JpegPost16
    {
        public static void Assemble(JpegDecoder d, Span<ushort> output, int w, int h, JpegColorSpace cs)
        {
            int nc = d.Comps.Length;
            int max = (1 << d.Precision) - 1, center = 1 << (d.Precision - 1);
            var lineArr = new ushort[nc][];
            var cached = new int[nc];
            for (int i = 0; i < nc; i++)
            {
                lineArr[i] = new ushort[Math.Max(d.OutWidth, d.Comps[i].SampledW * d.Comps[i].HExp) + 8];
                cached[i] = -1;
            }
            var tmpArr = cs is JpegColorSpace.Ycck or JpegColorSpace.YcckToCmyk ? new ushort[w * 3] : null;
            ushort** lines = stackalloc ushort*[4];
            fixed (ushort* outp = output)
            fixed (ushort* l0 = lineArr[0], l1 = lineArr[nc > 1 ? 1 : 0], l2 = lineArr[nc > 2 ? 2 : 0], l3 = lineArr[nc > 3 ? 3 : 0])
            fixed (ushort* tmp = tmpArr)
            {
                ushort** buf = stackalloc ushort*[4];
                buf[0] = l0; buf[1] = l1; buf[2] = l2; buf[3] = l3;
                int outChannels = cs == JpegColorSpace.Gray ? 1 : cs is JpegColorSpace.Raw or JpegColorSpace.Rgb ? nc : cs == JpegColorSpace.YcckToCmyk ? 4 : 3;
                for (int y = 0; y < h; y++)
                {
                    for (int i = 0; i < nc; i++) lines[i] = GetLine(d.Comps[i], y, w, buf[i], ref cached[i]);
                    ushort* dst = outp + (nint)y * w * outChannels;
                    switch (cs)
                    {
                        case JpegColorSpace.Gray:
                            new ReadOnlySpan<ushort>(lines[0], w).CopyTo(new Span<ushort>(dst, w));
                            break;
                        case JpegColorSpace.YCbCr:
                            YccToRgb(lines[0], lines[1], lines[2], dst, w, center, max);
                            break;
                        case JpegColorSpace.Rgb:
                        case JpegColorSpace.Raw:
                            for (int i = 0; i < nc; i++)
                            {
                                ushort* src = lines[i];
                                for (int x = 0; x < w; x++) dst[x * nc + i] = src[x];
                            }
                            break;
                        case JpegColorSpace.Cmyk:
                            for (int x = 0; x < w; x++)
                                for (int i = 0; i < 3; i++) dst[3 * x + i] = (ushort)(((long)lines[i][x] * lines[3][x] + max / 2) / max);
                            break;
                        case JpegColorSpace.Ycck:
                            YccToRgb(lines[0], lines[1], lines[2], tmp, w, center, max);
                            for (int x = 0; x < w; x++)
                                for (int i = 0; i < 3; i++) dst[3 * x + i] = (ushort)(((long)(max - tmp[3 * x + i]) * lines[3][x] + max / 2) / max);
                            break;
                        case JpegColorSpace.YcckToCmyk:
                            YccToRgb(lines[0], lines[1], lines[2], tmp, w, center, max);
                            for (int x = 0; x < w; x++, dst += 4)
                            {
                                dst[0] = (ushort)(max - tmp[3 * x]); dst[1] = (ushort)(max - tmp[3 * x + 1]); dst[2] = (ushort)(max - tmp[3 * x + 2]);
                                dst[3] = lines[3][x];
                            }
                            break;
                    }
                }
            }
        }

        static void YccToRgb(ushort* yp, ushort* cbp, ushort* crp, ushort* dst, int w, int center, int max)
        {
            int x = 0;
            if (System.Numerics.Vector.IsHardwareAccelerated && !JpegPost.ForceScalar)
            {
                // whole vectors of 16 bit samples, computed in 32 bit lanes (the products stay far below 2^31 at 12 bit)
                int n = System.Numerics.Vector<ushort>.Count;
                ushort* tmp = stackalloc ushort[3 * n];
                var vc = new System.Numerics.Vector<int>(center); var vmax = new System.Numerics.Vector<int>(max);
                var half = new System.Numerics.Vector<int>(32768);
                for (; x + n <= w; x += n, dst += 3 * n)
                {
                    System.Numerics.Vector.Widen(System.Numerics.Vector.Load(yp + x), out var y0, out var y1);
                    System.Numerics.Vector.Widen(System.Numerics.Vector.Load(cbp + x), out var b0, out var b1);
                    System.Numerics.Vector.Widen(System.Numerics.Vector.Load(crp + x), out var r0, out var r1);
                    Convert(y0, b0, r0, vc, vmax, half, out var rr0, out var gg0, out var bb0);
                    Convert(y1, b1, r1, vc, vmax, half, out var rr1, out var gg1, out var bb1);
                    System.Numerics.Vector.Store(System.Numerics.Vector.Narrow(rr0, rr1), tmp);
                    System.Numerics.Vector.Store(System.Numerics.Vector.Narrow(gg0, gg1), tmp + n);
                    System.Numerics.Vector.Store(System.Numerics.Vector.Narrow(bb0, bb1), tmp + 2 * n);
                    for (int i = 0; i < n; i++)
                    {
                        dst[3 * i] = tmp[i]; dst[3 * i + 1] = tmp[n + i]; dst[3 * i + 2] = tmp[2 * n + i];
                    }
                }
            }
            for (; x < w; x++, dst += 3)
            {
                int yy = yp[x], cb = cbp[x] - center, cr = crp[x] - center;
                dst[0] = (ushort)Math.Clamp(yy + ((91881 * cr + 32768) >> 16), 0, max);
                dst[1] = (ushort)Math.Clamp(yy + (int)((-22554L * cb - 46802L * cr + 32768) >> 16), 0, max);
                dst[2] = (ushort)Math.Clamp(yy + (int)((116130L * cb + 32768) >> 16), 0, max);
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        static void Convert(System.Numerics.Vector<uint> y, System.Numerics.Vector<uint> cbU, System.Numerics.Vector<uint> crU,
            System.Numerics.Vector<int> center, System.Numerics.Vector<int> max, System.Numerics.Vector<int> half,
            out System.Numerics.Vector<uint> r, out System.Numerics.Vector<uint> g, out System.Numerics.Vector<uint> b)
        {
            var yy = System.Numerics.Vector.AsVectorInt32(y);
            var cb = System.Numerics.Vector.AsVectorInt32(cbU) - center;
            var cr = System.Numerics.Vector.AsVectorInt32(crU) - center;
            var zero = System.Numerics.Vector<int>.Zero;
            var rr = yy + System.Numerics.Vector.ShiftRightArithmetic(cr * 91881 + half, 16);
            var gg = yy + System.Numerics.Vector.ShiftRightArithmetic(cb * -22554 + cr * -46802 + half, 16);
            var bb = yy + System.Numerics.Vector.ShiftRightArithmetic(cb * 116130 + half, 16);
            r = System.Numerics.Vector.AsVectorUInt32(System.Numerics.Vector.Min(System.Numerics.Vector.Max(rr, zero), max));
            g = System.Numerics.Vector.AsVectorUInt32(System.Numerics.Vector.Min(System.Numerics.Vector.Max(gg, zero), max));
            b = System.Numerics.Vector.AsVectorUInt32(System.Numerics.Vector.Min(System.Numerics.Vector.Max(bb, zero), max));
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        static System.Numerics.Vector<int> Sum(ushort* a, ushort* b, int i, System.Numerics.Vector<int> three)
        {
            // 3 * a + b for one vector of samples, widened from 16 bit
            System.Numerics.Vector.Widen(System.Numerics.Vector.Load(a + i), out var a0, out _);
            System.Numerics.Vector.Widen(System.Numerics.Vector.Load(b + i), out var b0, out _);
            return System.Numerics.Vector.AsVectorInt32(a0) * three + System.Numerics.Vector.AsVectorInt32(b0);
        }

        static ushort* GetLine(JpegComponent c, int y, int w, ushort* line, ref int cachedRow)
        {
            ushort* plane = c.Plane16;
            nint stride = c.Stride;
            int n = c.SampledW;
            switch (c.Upsample)
            {
                case JpegUpsample.Direct:
                    return plane + y * stride;

                case JpegUpsample.Replicate:
                {
                    int sy = y / c.VExp;
                    ushort* src = plane + sy * stride;
                    if (c.HExp == 1) return src;
                    if (sy != cachedRow)
                    {
                        cachedRow = sy;
                        int e = c.HExp;
                        for (int x = 0; x < n; x++)
                            for (int j = 0; j < e; j++) line[x * e + j] = src[x];
                    }
                    return line;
                }

                case JpegUpsample.Fractional:
                {
                    int py = Math.Clamp(((2 * y + 1) * c.VNum * 256 / c.VDen - 256) / 2, 0, (c.SampledH - 1) * 256);
                    int y0 = py >> 8, fy = py & 255;
                    ushort* r0 = plane + y0 * stride, r1 = plane + Math.Min(y0 + 1, c.SampledH - 1) * stride;
                    for (int x = 0; x < w; x++)
                    {
                        int px = Math.Clamp(((2 * x + 1) * c.HNum * 256 / c.HDen - 256) / 2, 0, (n - 1) * 256);
                        int x0 = px >> 8, fx = px & 255, x1 = Math.Min(x0 + 1, n - 1);
                        long top = r0[x0] * (256 - fx) + r0[x1] * fx, bottom = r1[x0] * (256 - fx) + r1[x1] * fx;
                        line[x] = (ushort)((top * (256 - fy) + bottom * fy + 32768) >> 16);
                    }
                    return line;
                }

                case JpegUpsample.H2V1:
                {
                    ushort* s = plane + y * stride;
                    for (int i = 0; i < n; i++)
                    {
                        int c3 = 3 * s[i];
                        line[2 * i] = (ushort)((c3 + s[Math.Max(i - 1, 0)] + 1) >> 2);
                        line[2 * i + 1] = (ushort)((c3 + s[Math.Min(i + 1, n - 1)] + 2) >> 2);
                    }
                    return line;
                }

                case JpegUpsample.H2V2:
                {
                    int sy = y >> 1;
                    int near = (y & 1) == 0 ? Math.Max(sy - 1, 0) : Math.Min(sy + 1, c.SampledH - 1);
                    ushort* a = plane + sy * stride, b = plane + near * stride;
                    int prev = 3 * a[0] + b[0], cur = prev;
                    int start = 0;
                    if (System.Numerics.Vector.IsHardwareAccelerated && !JpegPost.ForceScalar && n > 2 * System.Numerics.Vector<int>.Count + 2)
                    {
                        // interior samples in 32 bit lanes; even and odd outputs are interleaved through 16 + 16 bit packing
                        int vn = System.Numerics.Vector<int>.Count;
                        var three = new System.Numerics.Vector<int>(3);
                        int i = 1;
                        for (; i + 2 * vn + 1 <= n; i += vn) // (the loads read two lane counts of samples)
                        {
                            var c0 = Sum(a, b, i, three); var cm = Sum(a, b, i - 1, three); var cp = Sum(a, b, i + 1, three);
                            var even = System.Numerics.Vector.ShiftRightArithmetic(c0 * three + cm + new System.Numerics.Vector<int>(8), 4);
                            var odd = System.Numerics.Vector.ShiftRightArithmetic(c0 * three + cp + new System.Numerics.Vector<int>(7), 4);
                            System.Numerics.Vector.Store(System.Numerics.Vector.AsVectorUInt16(even | System.Numerics.Vector.ShiftLeft(odd, 16)), line + 2 * i);
                        }
                        // sample 0 and the tail go through the scalar loop
                        line[0] = (ushort)((4 * prev + 8) >> 4);
                        line[1] = (ushort)((3 * prev + 3 * a[1] + b[1] + 7) >> 4);
                        start = i;
                        prev = 3 * a[i - 1] + b[i - 1]; cur = 3 * a[i] + b[i];
                    }
                    for (int i = start; i < n; i++)
                    {
                        int next = i + 1 < n ? 3 * a[i + 1] + b[i + 1] : cur;
                        line[2 * i] = (ushort)((3 * cur + prev + 8) >> 4);
                        line[2 * i + 1] = (ushort)((3 * cur + next + 7) >> 4);
                        prev = cur; cur = next;
                    }
                    return line;
                }

                default: // H1V2
                {
                    int sy = y >> 1;
                    int near = (y & 1) == 0 ? Math.Max(sy - 1, 0) : Math.Min(sy + 1, c.SampledH - 1);
                    ushort* a = plane + sy * stride, b = plane + near * stride;
                    int bias = (y & 1) == 0 ? 1 : 2;
                    for (int i = 0; i < n; i++) line[i] = (ushort)((3 * a[i] + b[i] + bias) >> 2);
                    return line;
                }
            }
        }
    }
}
