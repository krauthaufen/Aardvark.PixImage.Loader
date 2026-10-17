using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Aardvark.ImageCodecs
{
    /// <summary>How <see cref="Dng.Encode(RawImage, DngInfo, Stream, DngEncodeOptions?)"/> stores the raw samples.</summary>
    public enum DngCompression
    {
        /// <summary>Lossless JPEG tiles (DNG's usual choice; 8 / 16 bit integer samples). CFA tiles are coded as frames of
        /// two components per row so that the predictor works across samples of the same colour.</summary>
        LosslessJpeg,
        /// <summary>Uncompressed tiles.</summary>
        None,
        /// <summary>Deflate tiles with the floating point predictor (float samples; DNG allows Deflate for float and 32-bit data only).</summary>
        Deflate,
    }

    public sealed class DngEncodeOptions
    {
        public DngCompression Compression { get; set; } = DngCompression.LosslessJpeg;

        /// <summary>Tile width and height (a multiple of 16).</summary>
        public int TileSize { get; set; } = 256;

        /// <summary>
        /// An 8-bit RGB rendering to store as the preview in IFD 0 (the raw image then goes into a SubIFD, as cameras write
        /// it); null writes the raw image into IFD 0.
        /// </summary>
        public RawImage? Preview { get; set; }

        /// <summary>Compress tiles on several threads.</summary>
        public bool Parallel { get; set; } = true;
    }

    /// <summary>
    /// Writes a DNG 1.4 file from raw samples and a <see cref="DngInfo"/>: the raw IFD with every parameter the info
    /// carries (CFA, levels, linearization, crop, colour calibrations with forward matrices and hue / saturation maps, white
    /// balance, exposure, opcode lists, profile), EXIF / XMP from the image's metadata, optionally a preview.
    /// </summary>
    internal static class DngEncoder
    {
        static TiffField R(int tag, params double[] v) => Rationals(tag, v, false);
        static TiffField SR(int tag, params double[] v) => Rationals(tag, v, true);

        /// <summary>
        /// Rationals as the closest fractions with 32-bit terms (continued fractions): a value read from a rational is
        /// written back as the same fraction (or an equal one in lowest terms).
        /// </summary>
        static TiffField Rationals(int tag, double[] v, bool signed)
        {
            var d = new byte[v.Length * 8];
            for (int i = 0; i < v.Length; i++)
            {
                var (n, q) = Fraction(v[i], signed);
                BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(i * 8), unchecked((uint)n));
                BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(i * 8 + 4), (uint)q);
            }
            return new TiffField { Tag = (ushort)tag, Type = (ushort)(signed ? 10 : 5), Count = v.Length, Data = d };
        }

        static (long Num, long Den) Fraction(double v, bool signed)
        {
            if (!double.IsFinite(v)) return (0, 0);
            if (!signed && v < 0) v = 0;
            double limit = signed ? int.MaxValue : uint.MaxValue;
            int sign = Math.Sign(v);
            double x = Math.Abs(v);
            if (x > limit) return (sign * (long)limit, 1);
            // convergents h/k of the continued fraction of x, while both stay within the limit
            long h0 = 0, h1 = 1, k0 = 1, k1 = 0;
            double r = x;
            for (int i = 0; i < 64; i++)
            {
                double a = Math.Floor(r);
                if (a > limit) break;
                long ia = (long)a;
                double hn = (double)ia * h1 + h0, kn = (double)ia * k1 + k0;
                if (hn > limit || kn > limit) break;
                (h0, h1) = (h1, ia * h1 + h0);
                (k0, k1) = (k1, ia * k1 + k0);
                if ((double)h1 / k1 == x) break;
                double f = r - a;
                if (f < 1e-300) break;
                r = 1 / f;
            }
            return k1 == 0 ? (0, 1) : (sign * h1, k1);
        }
        static TiffField L(int tag, params long[] v) => TiffField.Longs(tag, false, v);
        static TiffField Bytes(int tag, byte[] v) => TiffField.FromTag(new TiffTag(tag, TiffTagType.Byte, v), false);
        static TiffField Undef(int tag, byte[] v) => TiffField.FromTag(new TiffTag(tag, TiffTagType.Undefined, v), false);
        static TiffField Text(int tag, string v) => TiffField.FromTag(TiffTag.Ascii(tag, v), false);
        static TiffField Floats(int tag, float[] v) => TiffField.FromTag(new TiffTag(tag, TiffTagType.Float, v), false);

        public static byte[] OpcodeList(IReadOnlyList<DngOpcode> ops)
        {
            var ms = new MemoryStream();
            Span<byte> b = stackalloc byte[16];
            BinaryPrimitives.WriteUInt32BigEndian(b, (uint)ops.Count);
            ms.Write(b.Slice(0, 4));
            foreach (var op in ops)
            {
                BinaryPrimitives.WriteUInt32BigEndian(b, (uint)op.Id);
                BinaryPrimitives.WriteUInt32BigEndian(b.Slice(4), op.Version);
                BinaryPrimitives.WriteUInt32BigEndian(b.Slice(8), (op.Optional ? 1u : 0) | (op.PreviewSkippable ? 2u : 0));
                BinaryPrimitives.WriteUInt32BigEndian(b.Slice(12), (uint)op.Parameters.Length);
                ms.Write(b);
                ms.Write(op.Parameters);
            }
            return ms.ToArray();
        }

        public static void Encode(RawImage raw, DngInfo info, Stream output, DngEncodeOptions o)
        {
            if (raw == null) throw new ArgumentNullException(nameof(raw));
            if (info == null) throw new ArgumentNullException(nameof(info));
            bool cfa = info.Layout == DngRawLayout.ColorFilterArray;
            if (cfa && raw.Channels != 1) throw new ArgumentException("DNG: colour filter array data has one channel");
            if (cfa && (info.CfaRepeatRows <= 0 || info.CfaRepeatCols <= 0 || info.CfaPattern.Length < info.CfaRepeatRows * info.CfaRepeatCols))
                throw new ArgumentException("DNG: the info needs CfaRepeatRows / Cols and CfaPattern");
            bool isFloat = raw.SampleType is SampleType.Float16 or SampleType.Float32;
            if (!(raw.SampleType is SampleType.UInt8 or SampleType.UInt16 or SampleType.UInt32 || isFloat))
                throw new ArgumentException($"DNG: {raw.SampleType} samples cannot be written");
            int tile = o.TileSize;
            if (tile < 16 || tile % 16 != 0) throw new ArgumentException("DNG: the tile size must be a positive multiple of 16");
            var comp = o.Compression;
            if (comp == DngCompression.LosslessJpeg && !(raw.SampleType is SampleType.UInt8 or SampleType.UInt16))
                throw new ArgumentException("DNG: lossless JPEG needs 8 or 16 bit integer samples (use Deflate for floats)");
            if (comp == DngCompression.Deflate && !(isFloat || raw.SampleType == SampleType.UInt32))
                throw new ArgumentException("DNG: Deflate is allowed for float and 32-bit data only (use lossless JPEG)");
            if (o.Preview != null && (o.Preview.SampleType != SampleType.UInt8 || o.Preview.Channels != 3))
                throw new ArgumentException("DNG: the preview must be an 8-bit RGB image");

            int w = raw.Width, h = raw.Height, spp = raw.Channels, bps = RawImage.BytesPerSample(raw.SampleType);
            int bits = raw.SampleType switch
            {
                SampleType.UInt8 => 8,
                SampleType.UInt16 => info.BitsPerSample is >= 9 and <= 16 ? info.BitsPerSample : 16,
                SampleType.UInt32 => 32,
                SampleType.Float16 => 16,
                _ => 32,
            };
            if (raw.SampleType == SampleType.UInt16 && bits < 16)
            {
                // the declared depth has to hold every value
                ushort max = 0;
                foreach (var v in raw.GetData<ushort>()) if (v > max) max = v;
                while (bits < 16 && max >> bits != 0) bits++;
            }

            // tiles
            int across = (w + tile - 1) / tile, down = (h + tile - 1) / tile;
            var chunks = new byte[across * down][];
            var src = raw.GetBytes().ToArray();
            void Tile(int t)
            {
                int tx = t % across * tile, ty = t / across * tile;
                var buf = new byte[(long)tile * tile * spp * bps];
                int rowBytes = tile * spp * bps;
                for (int y = 0; y < tile; y++)
                {
                    int sy = Math.Min(ty + y, h - 1);
                    for (int x = 0; x < tile; x++)
                    {
                        // edges: the last row / column repeated (tiles are always whole)
                        int sx = Math.Min(tx + x, w - 1);
                        Buffer.BlockCopy(src, ((sy * w) + sx) * spp * bps, buf, y * rowBytes + x * spp * bps, spp * bps);
                    }
                }
                switch (comp)
                {
                    case DngCompression.None: chunks[t] = bps == 2 && bits < 16 ? Pack(buf, tile * spp, tile, bits) : buf; break;
                    case DngCompression.Deflate: chunks[t] = Compression.Zlib.Compress(FloatPredict(buf, tile, spp, bps)); break;
                    default:
                    {
                        // CFA: two samples per JPEG pixel; LinearRaw: the planes as components
                        int fw = cfa ? tile / 2 : tile, fc = cfa ? 2 : spp;
                        Array data = bps == 1 ? buf : System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(buf).ToArray();
                        var img = new RawImage(fw, tile, ColorModel.Other, data, fc);
                        var ms = new MemoryStream();
                        Jpeg.Encode(img, ms, new JpegEncodeOptions { Lossless = true, Precision = bits, Predictor = 1 });
                        chunks[t] = ms.ToArray();
                        break;
                    }
                }
            }
            if (o.Parallel) System.Threading.Tasks.Parallel.For(0, chunks.Length, Tile);
            else for (int t = 0; t < chunks.Length; t++) Tile(t);

            // directories
            var rawIfd = new TiffIfdNode();
            void Add(TiffField f) => rawIfd.Set(f);
            Add(L(254, 0));
            Add(L(256, w)); Add(L(257, h));
            Add(TiffField.Shorts(258, Enumerable.Repeat(bits, spp).ToArray()));
            Add(TiffField.Shorts(259, comp switch { DngCompression.None => 1, DngCompression.Deflate => 8, _ => 7 }));
            Add(TiffField.Shorts(262, cfa ? 32803 : 34892));
            Add(TiffField.Shorts(277, spp));
            Add(TiffField.Shorts(284, 1));
            Add(TiffField.Shorts(339, Enumerable.Repeat(isFloat ? 3 : 1, spp).ToArray()));
            if (comp == DngCompression.Deflate) Add(TiffField.Shorts(317, 3));
            Add(L(322, tile)); Add(L(323, tile));
            if (cfa)
            {
                Add(TiffField.Shorts(33421, info.CfaRepeatRows, info.CfaRepeatCols));
                Add(Bytes(33422, info.CfaPattern.Take(info.CfaRepeatRows * info.CfaRepeatCols).ToArray()));
                Add(Bytes(50710, info.CfaPlaneColor));
                Add(TiffField.Shorts(50711, info.CfaLayout));
            }
            if (info.LinearizationTable != null) Add(TiffField.Shorts(50712, info.LinearizationTable.Select(x => (int)x).ToArray()));
            Add(TiffField.Shorts(50713, info.BlackLevelRepeatRows, info.BlackLevelRepeatCols));
            if (info.BlackLevel.Length > 0) Add(R(50714, info.BlackLevel));
            if (info.BlackLevelDeltaH != null) Add(SR(50715, info.BlackLevelDeltaH));
            if (info.BlackLevelDeltaV != null) Add(SR(50716, info.BlackLevelDeltaV));
            if (info.WhiteLevel.Length > 0)
            {
                if (isFloat) Add(R(50717, info.WhiteLevel));   // (1.0 for float data)
                else Add(L(50717, info.WhiteLevel.Select(x => (long)Math.Round(x)).ToArray()));
            }
            if (info.DefaultScaleH != 1 || info.DefaultScaleV != 1) Add(R(50718, info.DefaultScaleH, info.DefaultScaleV));
            if (info.BestQualityScale != 1) Add(R(50780, info.BestQualityScale));
            var a = info.ActiveArea.Width > 0 ? info.ActiveArea : new DngRect(0, 0, h, w);
            if (a != new DngRect(0, 0, h, w)) Add(L(50829, a.Top, a.Left, a.Bottom, a.Right));
            if (info.MaskedAreas.Length > 0) Add(L(50830, info.MaskedAreas.SelectMany(m => new long[] { m.Top, m.Left, m.Bottom, m.Right }).ToArray()));
            if (info.DefaultCropWidth > 0 && info.DefaultCropHeight > 0)
            {
                Add(R(50719, info.DefaultCropX, info.DefaultCropY));
                Add(R(50720, info.DefaultCropWidth, info.DefaultCropHeight));
            }
            if (info.DefaultUserCrop != null) Add(R(51125, info.DefaultUserCrop));
            if (info.NoiseProfile != null) Add(TiffField.FromTag(TiffTag.Doubles(51041, info.NoiseProfile), false));
            if (info.OpcodeList1.Count > 0) Add(Undef(51008, OpcodeList(info.OpcodeList1)));
            if (info.OpcodeList2.Count > 0) Add(Undef(51009, OpcodeList(info.OpcodeList2)));
            if (info.OpcodeList3.Count > 0) Add(Undef(51022, OpcodeList(info.OpcodeList3)));

            // IFD 0 tags: camera, version, profile (the raw directory when there is no preview)
            var ifd0 = o.Preview != null ? new TiffIfdNode() : rawIfd;
            void Add0(TiffField f) => ifd0.Set(f);
            Add0(Bytes(50706, new byte[] { 1, 4, 0, 0 }));
            Add0(Bytes(50707, new byte[] { 1, (byte)(isFloat || comp == DngCompression.Deflate ? 4 : info.OpcodeList1.Count + info.OpcodeList2.Count + info.OpcodeList3.Count > 0 ? 3 : 1), 0, 0 }));
            string makeModel = string.Join(" ", new[] { info.Make, info.Model }.Where(s => !string.IsNullOrEmpty(s)));
            Add0(Text(50708, info.UniqueCameraModel ?? (makeModel.Length > 0 ? makeModel : "Unknown")));
            if (info.Make != null) Add0(Text(271, info.Make));
            if (info.Model != null) Add0(Text(272, info.Model));
            if (info.LocalizedCameraModel != null) Add0(Text(50709, info.LocalizedCameraModel));
            if (info.CameraSerialNumber != null) Add0(Text(50735, info.CameraSerialNumber));
            Add0(Text(305, "Aardvark.PixImage.PixCodec"));
            Add0(TiffField.Shorts(274, info.Orientation is >= 1 and <= 8 ? info.Orientation : 1));
            int[] illTags = { 50778, 50779, 52529 }, cmTags = { 50721, 50722, 52531 }, ccTags = { 50723, 50724, 52530 },
                  rmTags = { 50725, 50726, 52538 }, fmTags = { 50964, 50965, 52532 }, idTags = { 52533, 52534, 52535 }, hsTags = { 50938, 50939, 52537 };
            for (int k = 0; k < Math.Min(3, info.Calibrations.Count); k++)
            {
                var c = info.Calibrations[k];
                Add0(TiffField.Shorts(illTags[k], c.Illuminant));
                Add0(SR(cmTags[k], c.ColorMatrix));
                if (c.CameraCalibration != null) Add0(SR(ccTags[k], c.CameraCalibration));
                if (c.ReductionMatrix != null) Add0(SR(rmTags[k], c.ReductionMatrix));
                if (c.ForwardMatrix != null) Add0(SR(fmTags[k], c.ForwardMatrix));
                if (c.HueSatMap != null && info.HueSatMapDims != null) Add0(Floats(hsTags[k], c.HueSatMap));
                if (c.Illuminant == 255 && c.IlluminantXY is { } xy)
                {
                    var d = new byte[18];
                    BinaryPrimitives.WriteUInt16LittleEndian(d, 0);
                    BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(2), (uint)Math.Round(xy.X * 1_000_000)); BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(6), 1_000_000);
                    BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(10), (uint)Math.Round(xy.Y * 1_000_000)); BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(14), 1_000_000);
                    Add0(Undef(idTags[k], d));
                }
            }
            if (info.HueSatMapDims != null && info.Calibrations.Any(c => c.HueSatMap != null))
            {
                Add0(L(50937, info.HueSatMapDims.Select(x => (long)x).ToArray()));
                if (info.HueSatMapEncoding != 0) Add0(L(51107, info.HueSatMapEncoding));
            }
            if (info.LookTable != null && info.LookTableDims != null)
            {
                Add0(L(50981, info.LookTableDims.Select(x => (long)x).ToArray()));
                Add0(Floats(50982, info.LookTable));
                if (info.LookTableEncoding != 0) Add0(L(51108, info.LookTableEncoding));
            }
            if (info.AnalogBalance != null) Add0(R(50727, info.AnalogBalance));
            if (info.AsShotNeutral != null) Add0(R(50728, info.AsShotNeutral));
            else if (info.AsShotWhiteXY is { } wxy) Add0(R(50729, wxy.X, wxy.Y));
            Add0(SR(50730, info.BaselineExposure));
            if (info.BaselineExposureOffset != 0) Add0(SR(51109, info.BaselineExposureOffset));
            if (info.BaselineNoise != 1) Add0(R(50731, info.BaselineNoise));
            if (info.BaselineSharpness != 1) Add0(R(50732, info.BaselineSharpness));
            if (info.LinearResponseLimit != 1) Add0(R(50734, info.LinearResponseLimit));
            if (info.CameraCalibrationSignature != null) Add0(Text(50931, info.CameraCalibrationSignature));
            if (info.ProfileCalibrationSignature != null) Add0(Text(50932, info.ProfileCalibrationSignature));
            if (info.ProfileName != null) Add0(Text(50936, info.ProfileName));
            if (info.DefaultBlackRender != 0) Add0(L(51110, info.DefaultBlackRender));
            // EXIF / GPS directories and XMP of the image's metadata
            if (raw.Metadata?.Exif is { } exif && ParseExif(exif) is { } en)
                foreach (var f in en.Fields)
                    if ((f.Child != null && f.Tag is 34665 or 34853) || (f.Child == null && ifd0.Find(f.Tag) == null)) Add0(f);
            if (raw.Metadata?.Xmp is { Length: > 0 } xmp) Add0(Bytes(700, xmp));

            // layout: header, preview strip, raw tiles, raw IFD, IFD 0
            var outMs = new MemoryStream();
            outMs.Write(new byte[] { (byte)'I', (byte)'I', 42, 0, 0, 0, 0, 0 });
            long previewAt = 0;
            byte[]? preview = null;
            if (o.Preview != null)
            {
                preview = o.Preview.GetData<byte>();
                previewAt = outMs.Position;
                outMs.Write(preview);
                if ((outMs.Position & 1) != 0) outMs.WriteByte(0);
            }
            var offs = new long[chunks.Length];
            for (int i = 0; i < chunks.Length; i++)
            {
                offs[i] = outMs.Position;
                outMs.Write(chunks[i]);
                if ((outMs.Position & 1) != 0) outMs.WriteByte(0);
            }
            if (outMs.Position > uint.MaxValue - (1 << 24)) throw new ImageNotSupportedException("DNG: files above 4 GB are not written");
            Add(L(324, offs));
            Add(L(325, chunks.Select(c => (long)c.Length).ToArray()));
            long rawAt = outMs.Position;
            if (o.Preview != null)
            {
                outMs.Write(rawIfd.Serialize(rawAt, 0, false, false));
                var p = o.Preview;
                Add0(L(254, 1)); Add0(L(256, p.Width)); Add0(L(257, p.Height)); Add0(TiffField.Shorts(258, 8, 8, 8));
                Add0(TiffField.Shorts(259, 1)); Add0(TiffField.Shorts(262, 2)); Add0(TiffField.Shorts(277, 3)); Add0(TiffField.Shorts(284, 1));
                Add0(L(273, previewAt)); Add0(L(278, p.Height)); Add0(L(279, preview!.Length));
                Add0(L(330, rawAt));
                Add0(L(50970, 2));
            }
            long ifd0At = outMs.Position;
            if (o.Preview == null) ifd0At = rawAt;
            outMs.Write(ifd0.Serialize(ifd0At, 0, false, false));
            var all = outMs.GetBuffer();
            BinaryPrimitives.WriteUInt32LittleEndian(all.AsSpan(4), (uint)ifd0At);
            output.Write(all, 0, (int)outMs.Length);
        }

        /// <summary>The EXIF / GPS directories and the descriptive and TIFF-EP tags of an EXIF blob's IFD 0.</summary>
        static unsafe TiffIfdNode? ParseExif(byte[] exif)
        {
            try
            {
                fixed (byte* p = exif)
                {
                    var file = new TiffFile(new TiffMemorySource(p, exif.Length));
                    return TiffIfdNode.Read(file, file.FirstIfd, t => TiffExif.IsDescriptive(t) || Dng.IsTiffEpExif(t));
                }
            }
            catch (ImageFormatException) { return null; }
        }

        /// <summary>16-bit samples packed to <paramref name="bits"/> bits, most significant bit first, rows padded to a byte (DNG requires this order in either byte order).</summary>
        static byte[] Pack(byte[] buf, int perRow, int rows, int bits)
        {
            var s = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(buf);
            int rowBytes = (perRow * bits + 7) / 8;
            var o = new byte[rowBytes * rows];
            for (int y = 0; y < rows; y++)
            {
                int bit = 0, ob = y * rowBytes;
                ulong acc = 0;
                for (int i = 0; i < perRow; i++)
                {
                    acc = acc << bits | s[y * perRow + i];
                    bit += bits;
                    while (bit >= 8) { bit -= 8; o[ob++] = (byte)(acc >> bit); }
                }
                if (bit > 0) o[ob] = (byte)(acc << (8 - bit));
            }
            return o;
        }

        /// <summary>The floating point predictor (Adobe TIFF technical note 3): bytes regrouped most significant first, then differenced.</summary>
        static byte[] FloatPredict(byte[] buf, int tile, int spp, int bps)
        {
            var o = new byte[buf.Length];
            int n = tile * spp, row = n * bps;
            for (int y = 0; y < tile; y++)
            {
                int r0 = y * row;
                for (int i = 0; i < n; i++)
                    for (int b = 0; b < bps; b++) o[r0 + b * n + i] = buf[r0 + i * bps + (bps - 1 - b)];
                for (int i = row - 1; i >= spp; i--) o[r0 + i] = (byte)(o[r0 + i] - o[r0 + i - spp]);
            }
            return o;
        }
    }
}
