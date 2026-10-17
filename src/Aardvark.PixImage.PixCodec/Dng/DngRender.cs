using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Aardvark.ImageCodecs
{
    /// <summary>
    /// The DNG processing pipeline: stored values, OpcodeList1, linearization / black / white (chapter 5), OpcodeList2,
    /// white balance and highlight clipping, demosaicing, OpcodeList3, camera to output colour (chapter 6), exposure, default
    /// crop and scale, encoding, orientation.
    /// </summary>
    internal static class DngRender
    {
        // ------------------------------------------------------------------------------------------ stored values

        /// <summary>The raw directory's samples as stored, with RowInterleaveFactor / ColumnInterleaveFactor undone.</summary>
        public static RawImage ReadStored(TiffFile f, DngInfo info, long rawOffset, bool parallel, int maxThreads)
        {
            if (info.SubTileBlockRows != 1 || info.SubTileBlockCols != 1)
                throw new ImageNotSupportedException("DNG: SubTileBlockSize is not supported");
            var o = new TiffDecodeOptions { BitScaling = TiffBitScaling.None, ConvertToRgb = false, ApplyOrientation = false, Parallel = parallel, MaxDegreeOfParallelism = maxThreads };
            var img = new TiffImage(f, rawOffset, o);
            var raw = img.Decode(0, 0, img.Width, img.Height, o);
            if (info.RowInterleaveFactor > 1 || info.ColumnInterleaveFactor > 1) raw = Deinterleave(raw, info.RowInterleaveFactor, info.ColumnInterleaveFactor);
            raw.Metadata = null;
            return raw;
        }

        /// <summary>Stored index of image row / column i with n interleaved fields: the fields are stored one after the other.</summary>
        static int[] FieldOrder(int size, int n)
        {
            var map = new int[size];
            int[] start = new int[n];
            for (int fld = 1; fld < n; fld++) start[fld] = start[fld - 1] + (size - (fld - 1) + n - 1) / n;
            for (int i = 0; i < size; i++) map[i] = start[i % n] + i / n;
            return map;
        }

        static RawImage Deinterleave(RawImage raw, int rows, int cols)
        {
            int w = raw.Width, h = raw.Height, c = raw.Channels, bps = RawImage.BytesPerSample(raw.SampleType);
            var ry = FieldOrder(h, rows); var cx = FieldOrder(w, cols);
            var src = raw.GetBytes();
            var dst = RawImage.Create(w, h, raw.Color, raw.SampleType, c);
            var d = dst.GetBytes();
            int px = c * bps;
            for (int y = 0; y < h; y++)
            {
                var sr = src.Slice(ry[y] * w * px, w * px);
                var dr = d.Slice(y * w * px, w * px);
                if (cols == 1) { sr.CopyTo(dr); continue; }
                for (int x = 0; x < w; x++) sr.Slice(cx[x] * px, px).CopyTo(dr.Slice(x * px, px));
            }
            return dst;
        }

        // ------------------------------------------------------------------------------------------ linear reference values

        static DngPlanes ToPlanes(RawImage raw)
        {
            int w = raw.Width, h = raw.Height, c = raw.Channels;
            var o = new DngPlanes(w, h, c);
            long n = (long)w * h;
            switch (raw.SampleType)
            {
                case SampleType.UInt8: { var s = raw.GetData<byte>(); for (int p = 0; p < c; p++) { var d = o.Data[p]; for (long i = 0; i < n; i++) d[i] = s[i * c + p]; } break; }
                case SampleType.UInt16: { var s = raw.GetData<ushort>(); for (int p = 0; p < c; p++) { var d = o.Data[p]; for (long i = 0; i < n; i++) d[i] = s[i * c + p]; } break; }
                case SampleType.UInt32: { var s = raw.GetData<uint>(); for (int p = 0; p < c; p++) { var d = o.Data[p]; for (long i = 0; i < n; i++) d[i] = s[i * c + p]; } break; }
                case SampleType.Float16: { var s = raw.GetData<Half>(); for (int p = 0; p < c; p++) { var d = o.Data[p]; for (long i = 0; i < n; i++) d[i] = (float)s[i * c + p]; } break; }
                case SampleType.Float32: { var s = raw.GetData<float>(); for (int p = 0; p < c; p++) { var d = o.Data[p]; for (long i = 0; i < n; i++) d[i] = s[i * c + p]; } break; }
                default: throw new ImageNotSupportedException($"DNG: {raw.SampleType} raw samples are not supported");
            }
            return o;
        }

        /// <summary>
        /// Linear reference values of the active area (chapter 5): OpcodeList1 on the stored values, LinearizationTable,
        /// black subtraction (BlackLevel pattern + BlackLevelDeltaH / V), scaling by 1 / (WhiteLevel - largest black), values
        /// above 1 clipped (integer data), negative values kept, then OpcodeList2. <paramref name="trim"/> receives the offset
        /// of the result relative to the active area (TrimBounds).
        /// </summary>
        public static DngPlanes Linearize(RawImage stored, DngInfo info, bool opcodes, ParallelOptions po, out (int X, int Y) trim)
        {
            trim = (0, 0);
            int spp = stored.Channels;
            var a = info.ActiveArea;
            int w = a.Width, h = a.Height, sw = stored.Width;
            bool cfa = info.Layout == DngRawLayout.ColorFilterArray;
            bool isInt = stored.SampleType is SampleType.UInt8 or SampleType.UInt16 or SampleType.UInt32;
            DngPlanes? list1 = null;
            if (opcodes && info.OpcodeList1.Count > 0)
            {
                var none = (0, 0);
                float range = info.BitsPerSample > 16 && isInt ? uint.MaxValue : isInt ? 65535 : 1;
                list1 = DngOpcodes.Apply(info.OpcodeList1, ToPlanes(stored), Ranges(spp, range), true, cfa, ref none);
                if (list1.Width != stored.Width || list1.Height != stored.Height) throw new ImageNotSupportedException("DNG: TrimBounds in OpcodeList1 is not supported");
            }

            // per sample: scale and largest black
            var scale = new float[spp];
            var dh = info.BlackLevelDeltaH; var dv = info.BlackLevelDeltaV;
            double maxH = 0, maxV = 0;
            if (dh != null) foreach (var v in dh) maxH = Math.Max(maxH, v);
            if (dv != null) foreach (var v in dv) maxV = Math.Max(maxV, v);
            int brr = info.BlackLevelRepeatRows, brc = info.BlackLevelRepeatCols;
            var black = new double[brr * brc * spp];
            for (int i = 0; i < black.Length; i++) black[i] = info.BlackLevel.Length == 0 ? 0 : info.BlackLevel[Math.Min(i, info.BlackLevel.Length - 1)];
            for (int s = 0; s < spp; s++)
            {
                double mb = double.MinValue;
                for (int i = 0; i < brr * brc; i++) mb = Math.Max(mb, black[i * spp + s]);
                double range = info.WhiteLevel[Math.Min(s, info.WhiteLevel.Length - 1)] - (mb + maxH + maxV);
                scale[s] = range > 0 ? (float)(1 / range) : 1;
            }
            var dhs = new float[w]; var dvs = new float[h];
            if (dh != null) for (int x = 0; x < w && x < dh.Length; x++) dhs[x] = (float)dh[x];
            if (dv != null) for (int y = 0; y < h && y < dv.Length; y++) dvs[y] = (float)dv[y];

            var lin = info.LinearizationTable;
            var outp = new DngPlanes(w, h, spp);
            bool clipTop = isInt;

            // 8 / 16 bit integers without list 1: one table per black pattern cell and sample
            if (list1 == null && stored.SampleType is SampleType.UInt8 or SampleType.UInt16)
            {
                int size = stored.SampleType == SampleType.UInt8 ? 256 : 65536;
                var luts = new float[brr * brc * spp][];
                for (int cell = 0; cell < brr * brc; cell++)
                    for (int s = 0; s < spp; s++)
                    {
                        var t = luts[cell * spp + s] = new float[size];
                        double bl = black[cell * spp + s];
                        for (int v = 0; v < size; v++)
                        {
                            double x = lin != null ? lin[Math.Min(v, lin.Length - 1)] : v;
                            t[v] = (float)((x - bl) * scale[s]);
                        }
                    }
                bool u8 = stored.SampleType == SampleType.UInt8;
                byte[]? s8 = u8 ? stored.GetData<byte>() : null;
                ushort[]? s16 = u8 ? null : stored.GetData<ushort>();
                Parallel.For(0, h, po, y =>
                {
                    long srow = ((long)(y + a.Top) * sw + a.Left) * spp;
                    int by = y % brr;
                    for (int s = 0; s < spp; s++)
                    {
                        var d = outp.Data[s];
                        float sc = scale[s], ov = dvs[y] * sc;
                        for (int x = 0; x < w; x++)
                        {
                            long si = srow + (long)x * spp + s;
                            int v = u8 ? s8![si] : s16![si];
                            float r = luts[(by * brc + x % brc) * spp + s][v] - dhs[x] * sc - ov;
                            d[(long)y * w + x] = r > 1 ? 1 : r;
                        }
                    }
                });
            }
            else
            {
                var src = list1 ?? ToPlanes(stored);
                Parallel.For(0, h, po, y =>
                {
                    int by = y % brr;
                    for (int s = 0; s < spp; s++)
                    {
                        var sd = src.Data[s]; var d = outp.Data[s];
                        float sc = scale[s];
                        for (int x = 0; x < w; x++)
                        {
                            double v = sd[(long)(y + a.Top) * sw + a.Left + x];
                            if (lin != null && isInt) v = lin[(int)Math.Clamp(Math.Round(v), 0, lin.Length - 1)];
                            float r = (float)((v - black[(by * brc + x % brc) * spp + s] - dhs[x] - dvs[y]) * sc);
                            d[(long)y * w + x] = clipTop && r > 1 ? 1 : r;
                        }
                    }
                });
            }

            if (opcodes && info.OpcodeList2.Count > 0)
                outp = DngOpcodes.Apply(info.OpcodeList2, outp, Ranges(spp, 1), false, cfa, ref trim);
            return outp;
        }

        static float[] Ranges(int n, float v)
        {
            var r = new float[n];
            Array.Fill(r, v);
            return r;
        }

        // ------------------------------------------------------------------------------------------ white balance

        public static bool UseCameraCalibration(DngInfo info)
            => string.Equals(info.CameraCalibrationSignature ?? "", info.ProfileCalibrationSignature ?? "", StringComparison.Ordinal);

        /// <summary>The camera neutral and white point of the selected white balance.</summary>
        public static (double[] Neutral, (double X, double Y) White) WhiteBalance(DngInfo info, DngDecodeOptions o)
        {
            int n = info.ColorPlanes;
            bool cc = UseCameraCalibration(info);
            if (info.Calibrations.Count == 0)
            {
                // no colour profile (monochrome, or a file without matrices): only the neutral, if any, counts
                var neu = o.WhiteBalanceNeutral ?? info.AsShotNeutral ?? Ones(n);
                return (Check(neu, n), DngColor.D65);
            }
            if (o.WhiteBalanceNeutral is { } custom) { var c = Check(custom, n); return (c, DngColor.NeutralToXy(info, c, cc)); }
            if (o.WhiteBalanceXY is { } xy) return (DngColor.XyToNeutral(info, xy, cc), xy);
            if (info.AsShotNeutral is { } asn) return (asn, DngColor.NeutralToXy(info, asn, cc));
            if (info.AsShotWhiteXY is { } wxy) return (DngColor.XyToNeutral(info, wxy, cc), wxy);
            return (DngColor.XyToNeutral(info, DngColor.D65, cc), DngColor.D65);
        }

        static double[] Ones(int n) { var r = new double[n]; Array.Fill(r, 1.0); return r; }

        static double[] Check(double[] v, int n)
        {
            if (v.Length != n) throw new ArgumentException($"DNG: the white balance needs {n} values, one per colour plane");
            foreach (var x in v) if (!(x > 0)) throw new ArgumentException("DNG: white balance values must be positive");
            return v;
        }

        // ------------------------------------------------------------------------------------------ rendering

        public static RawImage Render(TiffFile f, DngInfo info, long rawOffset, DngDecodeOptions o, ImageMetadata meta)
        {
            if (info.Layout == DngRawLayout.ColorFilterArray && info.CfaLayout != 1)
                throw new ImageNotSupportedException($"DNG: staggered CFA layout {info.CfaLayout} is not supported");
            if (o.OutputType is not (SampleType.UInt8 or SampleType.UInt16 or SampleType.Float32))
                throw new ArgumentException("DNG: output sample type must be UInt8, UInt16 or Float32");
            var po = new ParallelOptions { MaxDegreeOfParallelism = !o.Parallel ? 1 : o.MaxDegreeOfParallelism > 0 ? o.MaxDegreeOfParallelism : -1 };
            int n = info.ColorPlanes;
            var stored = ReadStored(f, info, rawOffset, o.Parallel, o.MaxDegreeOfParallelism);
            if (info.Layout == DngRawLayout.LinearRaw && stored.Channels != n) throw new ImageFormatException("DNG: LinearRaw sample count differs from the colour planes");
            var lin = Linearize(stored, info, o.ApplyOpcodes, po, out var trim);
            stored = null!;

            var (neutral, white) = WhiteBalance(info, o);
            // white balanced camera space: neutral -> 1; a channel saturates at 1 / neutral, all of them at 1 / max(neutral)
            double maxN = 0;
            foreach (var v in neutral) maxN = Math.Max(maxN, v);
            float clip = o.ClipHighlights ? (float)(1 / maxN) : float.PositiveInfinity;
            var wb = new float[n];
            for (int i = 0; i < n; i++) wb[i] = (float)(1 / neutral[i]);

            DngPlanes img;
            if (info.Layout == DngRawLayout.ColorFilterArray)
            {
                int rr = info.CfaRepeatRows, rc = info.CfaRepeatCols;
                var cfa = new int[rr * rc];
                // CFA origin: top-left of the active area (moved by TrimBounds of list 2)
                for (int y = 0; y < rr; y++)
                    for (int x = 0; x < rc; x++)
                    {
                        int p = info.CfaPlane(y + trim.Y, x + trim.X);
                        if (p < 0 || p >= n) throw new ImageFormatException("DNG: CFAPattern names a colour without plane (CFAPlaneColor)");
                        cfa[y * rc + x] = p;
                    }
                var m = lin.Data[0];
                int w = lin.Width;
                Parallel.For(0, lin.Height, po, y =>
                {
                    int cy = (y % rr) * rc;
                    var row = m.AsSpan(y * w, w);
                    for (int x = 0; x < row.Length; x++) { float v = row[x] * wb[cfa[cy + x % rc]]; row[x] = v > clip ? clip : v; }
                });
                img = DngDemosaic.Run(m, w, lin.Height, cfa, rr, rc, n, o.Demosaic, po);
            }
            else
            {
                img = lin;
                for (int p = 0; p < n; p++)
                {
                    var d = img.Data[p]; float k = wb[p];
                    Parallel.For(0, img.Height, po, y =>
                    {
                        var row = d.AsSpan(y * img.Width, img.Width);
                        for (int x = 0; x < row.Length; x++) { float v = row[x] * k; row[x] = v > clip ? clip : v; }
                    });
                }
            }
            lin = null!;
            if (o.ApplyOpcodes && info.OpcodeList3.Count > 0)
            {
                // list 3 sees linear camera values clipped at 1, i.e. 1 / neutral in white balanced space
                var ranges = new float[n];
                for (int i = 0; i < n; i++) ranges[i] = Math.Min(wb[i], clip);
                img = DngOpcodes.Apply(info.OpcodeList3, img, ranges, false, false, ref trim);
            }

            // output colour
            int oc;
            double[] mat;   // oc x n, from white balanced camera space
            if (o.ColorSpace == DngColorSpace.Camera || info.Calibrations.Count == 0 || n == 1)
            {
                oc = n == 3 || n == 1 ? n : n;
                mat = DngColor.Identity(n);
                if (n == 3 && info.Calibrations.Count == 0 && o.ColorSpace != DngColorSpace.Camera) oc = 3;
            }
            else
            {
                var c2x = DngColor.CameraToXyzD50(info, neutral, white, UseCameraCalibration(info));
                var wb2x = DngColor.Mul(c2x, 3, n, DngColor.Diagonal(neutral), n);
                mat = o.ColorSpace == DngColorSpace.XyzD50 ? wb2x : DngColor.Mul(DngColor.XyzD50ToLinearSrgb, 3, 3, wb2x, n);
                oc = 3;
            }
            double ev = o.ExposureCompensation + (o.ApplyBaselineExposure ? info.BaselineExposure + info.BaselineExposureOffset : 0);
            float gain = (float)Math.Pow(2, ev);

            // default crop (relative to the active area, moved by TrimBounds)
            int cx0 = 0, cy0 = 0, cw = img.Width, ch = img.Height;
            if (o.ApplyDefaultCrop)
            {
                cx0 = Math.Clamp((int)Math.Round(info.DefaultCropX) - trim.X, 0, img.Width - 1);
                cy0 = Math.Clamp((int)Math.Round(info.DefaultCropY) - trim.Y, 0, img.Height - 1);
                cw = Math.Clamp((int)Math.Round(info.DefaultCropWidth), 1, img.Width - cx0);
                ch = Math.Clamp((int)Math.Round(info.DefaultCropHeight), 1, img.Height - cy0);
            }
            bool resize = o.ApplyDefaultScale && (Math.Abs(info.DefaultScaleH - 1) > 1e-9 || Math.Abs(info.DefaultScaleV - 1) > 1e-9);
            var type = resize ? SampleType.Float32 : o.OutputType;
            bool srgb = o.ColorSpace == DngColorSpace.Srgb && type != SampleType.Float32;
            var color = oc == 1 ? ColorModel.Gray : oc == 3 ? ColorModel.Rgb : ColorModel.Other;
            var data = RawImage.CreateArray(type, checked(cw * ch * oc));
            var lut = srgb ? SrgbLut() : null;
            var fm = new float[mat.Length];
            for (int i = 0; i < fm.Length; i++) fm[i] = (float)mat[i] * gain;
            int iw = img.Width;
            var planes = img.Data;
            Parallel.For(0, ch, po, y =>
            {
                Span<float> px = stackalloc float[16];
                Span<float> outv = stackalloc float[16];
                long src = (long)(y + cy0) * iw + cx0;
                long dst = (long)y * cw * oc;
                for (int x = 0; x < cw; x++, src++, dst += oc)
                {
                    for (int p = 0; p < n; p++) px[p] = planes[p][src];
                    for (int c = 0; c < oc; c++)
                    {
                        float s = 0;
                        for (int p = 0; p < n; p++) s += fm[c * n + p] * px[p];
                        outv[c] = s;
                    }
                    switch (type)
                    {
                        case SampleType.Float32:
                        {
                            var d = (float[])data;
                            for (int c = 0; c < oc; c++) d[dst + c] = outv[c];
                            break;
                        }
                        case SampleType.UInt16:
                        {
                            var d = (ushort[])data;
                            for (int c = 0; c < oc; c++) d[dst + c] = Encode16(outv[c], lut);
                            break;
                        }
                        default:
                        {
                            var d = (byte[])data;
                            for (int c = 0; c < oc; c++) d[dst + c] = (byte)((Encode16(outv[c], lut) * 255 + 32895) >> 16);
                            break;
                        }
                    }
                }
            });
            var result = new RawImage(cw, ch, color, data, oc, meta);
            if (resize)
            {
                int fw = Math.Max(1, (int)Math.Round(cw * info.DefaultScaleH)), fh = Math.Max(1, (int)Math.Round(ch * info.DefaultScaleV));
                result = ImageOps.Resize(result, fw, fh, ResampleFilter.Lanczos3);
                result = Quantize(result, o.OutputType, o.ColorSpace == DngColorSpace.Srgb);
                result.Metadata = meta;
            }
            meta.Transfer = o.OutputType == SampleType.Float32 || o.ColorSpace != DngColorSpace.Srgb ? TransferFunction.Linear : TransferFunction.Srgb;
            meta.Orientation = info.Orientation;
            if (o.ApplyOrientation && info.Orientation > 1) result = ImageOps.ApplyOrientation(result, info.Orientation);
            return result;
        }

        static RawImage Quantize(RawImage f, SampleType type, bool srgb)
        {
            if (type == SampleType.Float32) return f;
            var s = f.GetData<float>();
            var lut = srgb ? SrgbLut() : null;
            Array data = RawImage.CreateArray(type, s.Length);
            if (type == SampleType.UInt16) { var d = (ushort[])data; for (int i = 0; i < s.Length; i++) d[i] = Encode16(s[i], lut); }
            else { var d = (byte[])data; for (int i = 0; i < s.Length; i++) d[i] = (byte)((Encode16(s[i], lut) * 255 + 32895) >> 16); }
            return new RawImage(f.Width, f.Height, f.Color, data, f.Channels);
        }

        static ushort[]? s_srgb;

        /// <summary>sRGB encoding (IEC 61966-2-1) to 16 bits, indexed by round(sqrt(linear) * 65535): fine steps in the shadows.</summary>
        static ushort[] SrgbLut()
        {
            if (s_srgb != null) return s_srgb;
            var t = new ushort[65536];
            for (int i = 0; i < t.Length; i++)
            {
                double l = (double)i / 65535 * i / 65535;
                double e = l <= 0.0031308 ? 12.92 * l : 1.055 * Math.Pow(l, 1 / 2.4) - 0.055;
                t[i] = (ushort)Math.Round(Math.Clamp(e, 0, 1) * 65535);
            }
            return s_srgb = t;
        }

        static ushort Encode16(float v, ushort[]? lut)
        {
            if (!(v > 0)) return 0;
            if (v >= 1) return 65535;
            return lut != null ? lut[(int)(MathF.Sqrt(v) * 65535 + 0.5f)] : (ushort)(v * 65535 + 0.5f);
        }
    }
}
