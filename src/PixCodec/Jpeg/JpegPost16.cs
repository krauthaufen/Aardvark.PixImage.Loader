using System;

namespace PixCodec.JpegImpl
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
            for (int x = 0; x < w; x++, dst += 3)
            {
                int yy = yp[x], cb = cbp[x] - center, cr = crp[x] - center;
                dst[0] = (ushort)Math.Clamp(yy + ((91881 * cr + 32768) >> 16), 0, max);
                dst[1] = (ushort)Math.Clamp(yy + (int)((-22554L * cb - 46802L * cr + 32768) >> 16), 0, max);
                dst[2] = (ushort)Math.Clamp(yy + (int)((116130L * cb + 32768) >> 16), 0, max);
            }
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
                    for (int i = 0; i < n; i++)
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
