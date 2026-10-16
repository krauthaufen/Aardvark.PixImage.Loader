using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Aardvark.ImageCodecs.Compression;

namespace Aardvark.ImageCodecs
{
    /// <summary>The fields of a NIfTI-1 / NIfTI-2 (or Analyze 7.5) header, as stored (see nifti1.h / nifti2.h).</summary>
    public sealed class NiftiHeader
    {
        /// <summary>1 or 2 (NIfTI-1 / NIfTI-2); 0 for an Analyze 7.5 header without the NIfTI magic.</summary>
        public int Version { get; internal set; }
        /// <summary>True for a single file (.nii, magic "n+1" / "n+2"), false for a header with a separate .img ("ni1" / "ni2").</summary>
        public bool SingleFile { get; internal set; }
        public bool BigEndian { get; internal set; }
        /// <summary>dim[0..7]: dim[0] is the number of dimensions.</summary>
        public long[] Dim { get; internal set; } = new long[8];
        /// <summary>pixdim[0..7]: pixdim[0] is qfac (the sign of the third axis of the qform), the others the voxel sizes.</summary>
        public double[] PixDim { get; internal set; } = new double[8];
        public int DataType { get; internal set; }
        public int BitPix { get; internal set; }
        public double VoxOffset { get; internal set; }
        public double SclSlope { get; internal set; }
        public double SclInter { get; internal set; }
        public double CalMin { get; internal set; }
        public double CalMax { get; internal set; }
        /// <summary>Intent code (NIFTI_INTENT_*: 0 none, 2 correl, 3 ttest, ..., 1002 label, 1007 vector, 2003 RGB vector, ...).</summary>
        public int IntentCode { get; internal set; }
        public double IntentP1 { get; internal set; }
        public double IntentP2 { get; internal set; }
        public double IntentP3 { get; internal set; }
        public string IntentName { get; internal set; } = "";
        /// <summary>dim_info: bits 0-1 frequency encoding axis, 2-3 phase encoding axis, 4-5 slice axis (1-based, 0 = unknown).</summary>
        public int DimInfo { get; internal set; }
        public int FrequencyDim => DimInfo & 3;
        public int PhaseDim => DimInfo >> 2 & 3;
        public int SliceDim => DimInfo >> 4 & 3;
        public long SliceStart { get; internal set; }
        public long SliceEnd { get; internal set; }
        public int SliceCode { get; internal set; }
        public double SliceDuration { get; internal set; }
        public double TOffset { get; internal set; }
        /// <summary>xyzt_units: bits 0-2 spatial unit (1 m, 2 mm, 3 µm), bits 3-5 temporal unit (8 s, 16 ms, 24 µs, 32 Hz, 40 ppm, 48 rad/s).</summary>
        public int XyztUnits { get; internal set; }
        public int QFormCode { get; internal set; }
        public int SFormCode { get; internal set; }
        /// <summary>quatern_b, quatern_c, quatern_d, qoffset_x, qoffset_y, qoffset_z.</summary>
        public double[] Quatern { get; internal set; } = new double[6];
        /// <summary>srow_x, srow_y, srow_z (3 x 4, row major).</summary>
        public double[,] SRow { get; internal set; } = new double[3, 4];
        public string Description { get; internal set; } = "";
        public string AuxFile { get; internal set; } = "";
        /// <summary>The header extensions (ecode, data), NIfTI-1 / -2 single files and headers.</summary>
        public List<(int Code, byte[] Data)> Extensions { get; } = new();
        internal int HeaderSize => Version == 2 ? 540 : 348;

        /// <summary>The voxel to world (RAS) affine nibabel and the NIfTI standard use: sform if sform_code &gt; 0, else qform, else pixdim scaling.</summary>
        public double[,] GetAffine() => Nifti.Affine(this, out _);
    }

    public sealed class NiftiEncodeOptions
    {
        /// <summary>gzip the whole file (.nii.gz).</summary>
        public bool Gzip { get; set; }
        public int CompressionLevel { get; set; } = 6;
        /// <summary>
        /// Intent code to write (default: none, or vector (1007) when there are channels that are not RGB(A) UInt8); a
        /// NIfTI source's own intent is kept when this is null.
        /// </summary>
        public int? IntentCode { get; set; }
        /// <summary>The descrip field (up to 79 characters).</summary>
        public string? Description { get; set; }
    }

    /// <summary>
    /// NIfTI-1 and NIfTI-2 (nifti.nimh.nih.gov): .nii, gzipped .nii.gz, .hdr + .img pairs (also gzipped), and Analyze 7.5
    /// headers. Every data type except float128 / complex256: RGB24 and RGBA32 as UInt8 Rgb / Rgba, complex64 / complex128 as
    /// two Float32 / Float64 channels (real, imaginary), vector intents (dim[5] components) as interleaved channels. scl_slope /
    /// scl_inter are applied per <see cref="VolumeDecodeOptions.Scaling"/>.
    /// <para>
    /// Axes: i, j, k of the file are x, y, z of the grid, without any flip (row j = 0 first; ITK and nibabel index the same
    /// way, viewers place the voxels by the affine). The affine (sform when sform_code &gt; 0, else qform, else the voxel
    /// sizes) goes to <see cref="PhysicalMetadata"/> in RAS; dim_info, intent and every other field are in
    /// <see cref="NiftiHeader"/> (<see cref="ReadHeader"/>) and <see cref="PhysicalMetadata.Header"/>. Trailing axes of size
    /// one are dropped (a 3D file with one slice decodes as 2D, t = 1 adds no axis).
    /// </para>
    /// Writes NIfTI-1 single files (.nii, optionally gzipped) with qform and sform from the metadata.
    /// </summary>
    public static class Nifti
    {
        const string Fmt = "NIfTI";

        static int Magic(ReadOnlySpan<byte> d, out int version, out bool single, out bool big)
        {
            version = 0; single = false; big = false;
            if (d.Length < 348) return 0;
            int le = BinaryPrimitives.ReadInt32LittleEndian(d), be = BinaryPrimitives.ReadInt32BigEndian(d);
            if (le == 348 || be == 348)
            {
                big = be == 348;
                var m = d.Slice(344, 4);
                if (m.SequenceEqual("n+1\0"u8)) { version = 1; single = true; return 348; }
                if (m.SequenceEqual("ni1\0"u8)) { version = 1; return 348; }
                return -348; // Analyze 7.5 candidate
            }
            if ((le == 540 || be == 540) && d.Length >= 540)
            {
                big = be == 540;
                var m = d.Slice(4, 8);
                if (m.SequenceEqual("n+2\0\r\n\x1a\n"u8)) { version = 2; single = true; return 540; }
                if (m.SequenceEqual("ni2\0\r\n\x1a\n"u8)) { version = 2; return 540; }
            }
            return 0;
        }

        /// <summary>
        /// A NIfTI-1 / -2 header (single file or .hdr), also gzipped. A headerless Analyze 7.5 .hdr matches only when it is
        /// exactly 348 bytes long with a sane dim array.
        /// </summary>
        public static bool IsMatch(ReadOnlySpan<byte> header)
        {
            if (Gzip.IsGzip(header))
            {
                Span<byte> h = stackalloc byte[544];
                int n;
                try { n = Gzip.Decompress(header, h); }
                catch (ImageFormatException) { return false; }
                return Magic(h.Slice(0, n), out _, out _, out _) > 0;
            }
            int r = Magic(header, out _, out _, out bool big);
            if (r > 0) return true;
            if (r == -348 && header.Length == 348)
            {
                // Analyze 7.5: dims 1..7, positive sizes, a known data type
                int n = I16(header, big, 40);
                if (n < 1 || n > 7) return false;
                for (int i = 1; i <= n; i++) if (I16(header, big, 40 + 2 * i) <= 0) return false;
                return I16(header, big, 70) is 2 or 4 or 8 or 16 or 32 or 64 or 128 or 256 or 512 or 768 or 1024 or 1280 or 1792 or 2304;
            }
            return false;
        }

        // ================================================================ header

        /// <summary>Reads the header (of a .nii, .nii.gz or .hdr).</summary>
        public static NiftiHeader ReadHeader(ReadOnlySpan<byte> data)
        {
            if (Gzip.IsGzip(data))
            {
                var buf = new byte[1 << 16];
                int n = Gzip.Decompress(data, buf);
                return Parse(buf.AsSpan(0, n), true);
            }
            return Parse(data, true);
        }

        static short I16(ReadOnlySpan<byte> d, bool big, int o) => big ? BinaryPrimitives.ReadInt16BigEndian(d.Slice(o)) : BinaryPrimitives.ReadInt16LittleEndian(d.Slice(o));
        static int I32(ReadOnlySpan<byte> d, bool big, int o) => big ? BinaryPrimitives.ReadInt32BigEndian(d.Slice(o)) : BinaryPrimitives.ReadInt32LittleEndian(d.Slice(o));
        static long I64(ReadOnlySpan<byte> d, bool big, int o) => big ? BinaryPrimitives.ReadInt64BigEndian(d.Slice(o)) : BinaryPrimitives.ReadInt64LittleEndian(d.Slice(o));
        static float F32(ReadOnlySpan<byte> d, bool big, int o) => big ? BinaryPrimitives.ReadSingleBigEndian(d.Slice(o)) : BinaryPrimitives.ReadSingleLittleEndian(d.Slice(o));
        static double F64(ReadOnlySpan<byte> d, bool big, int o) => big ? BinaryPrimitives.ReadDoubleBigEndian(d.Slice(o)) : BinaryPrimitives.ReadDoubleLittleEndian(d.Slice(o));
        static string Str(ReadOnlySpan<byte> d, bool big, int o, int n) { var s = d.Slice(o, n); int z = s.IndexOf((byte)0); return Encoding.Latin1.GetString(z >= 0 ? s.Slice(0, z) : s).TrimEnd(); }

        internal static NiftiHeader Parse(ReadOnlySpan<byte> d, bool readExtensions)
        {
            int m = Magic(d, out int version, out bool single, out bool big);
            if (m == 0) throw new ImageFormatException("NIfTI: not a NIfTI / Analyze header");
            var h = new NiftiHeader { Version = version, SingleFile = single, BigEndian = big };
            if (version == 2)
            {
                h.DataType = I16(d, big, 12); h.BitPix = I16(d, big, 14);
                for (int i = 0; i < 8; i++) h.Dim[i] = I64(d, big, 16 + 8 * i);
                h.IntentP1 = F64(d, big, 80); h.IntentP2 = F64(d, big, 88); h.IntentP3 = F64(d, big, 96);
                for (int i = 0; i < 8; i++) h.PixDim[i] = F64(d, big, 104 + 8 * i);
                h.VoxOffset = I64(d, big, 168); h.SclSlope = F64(d, big, 176); h.SclInter = F64(d, big, 184); h.CalMax = F64(d, big, 192); h.CalMin = F64(d, big, 200);
                h.SliceDuration = F64(d, big, 208); h.TOffset = F64(d, big, 216); h.SliceStart = I64(d, big, 224); h.SliceEnd = I64(d, big, 232);
                h.Description = Str(d, big, 240, 80); h.AuxFile = Str(d, big, 320, 24);
                h.QFormCode = I32(d, big, 344); h.SFormCode = I32(d, big, 348);
                for (int i = 0; i < 6; i++) h.Quatern[i] = F64(d, big, 352 + 8 * i);
                for (int r = 0; r < 3; r++) for (int c = 0; c < 4; c++) h.SRow[r, c] = F64(d, big, 400 + 32 * r + 8 * c);
                h.SliceCode = I32(d, big, 496); h.XyztUnits = I32(d, big, 500); h.IntentCode = I32(d, big, 504); h.IntentName = Str(d, big, 508, 16); h.DimInfo = d[524];
            }
            else
            {
                h.DimInfo = d[39];
                for (int i = 0; i < 8; i++) h.Dim[i] = I16(d, big, 40 + 2 * i);
                h.IntentP1 = F32(d, big, 56); h.IntentP2 = F32(d, big, 60); h.IntentP3 = F32(d, big, 64);
                h.IntentCode = I16(d, big, 68); h.DataType = I16(d, big, 70); h.BitPix = I16(d, big, 72); h.SliceStart = I16(d, big, 74);
                for (int i = 0; i < 8; i++) h.PixDim[i] = F32(d, big, 76 + 4 * i);
                h.VoxOffset = F32(d, big, 108); h.SclSlope = F32(d, big, 112); h.SclInter = F32(d, big, 116); h.SliceEnd = I16(d, big, 120); h.SliceCode = d[122];
                h.XyztUnits = d[123]; h.CalMax = F32(d, big, 124); h.CalMin = F32(d, big, 128); h.SliceDuration = F32(d, big, 132); h.TOffset = F32(d, big, 136);
                h.Description = Str(d, big, 148, 80); h.AuxFile = Str(d, big, 228, 24);
                if (version == 1)
                {
                    h.QFormCode = I16(d, big, 252); h.SFormCode = I16(d, big, 254);
                    for (int i = 0; i < 6; i++) h.Quatern[i] = F32(d, big, 256 + 4 * i);
                    for (int r = 0; r < 3; r++) for (int c = 0; c < 4; c++) h.SRow[r, c] = F32(d, big, 280 + 16 * r + 4 * c);
                    h.IntentName = Str(d, big, 328, 16);
                }
                else h.IntentCode = 0; // Analyze: these bytes mean other things
            }
            if (h.Dim[0] < 1 || h.Dim[0] > 7) throw new ImageFormatException($"NIfTI: bad dim[0] = {h.Dim[0]}");
            for (int i = 1; i <= h.Dim[0]; i++)
                if (h.Dim[i] <= 0 || h.Dim[i] > int.MaxValue) throw new ImageFormatException($"NIfTI: bad dim[{i}] = {h.Dim[i]}");
            if (h.SingleFile && (h.VoxOffset < h.HeaderSize || double.IsNaN(h.VoxOffset) || h.VoxOffset > 1e15))
                throw new ImageFormatException("NIfTI: bad vox_offset");
            if (readExtensions)
            {
                int pos = h.HeaderSize;
                long end = h.SingleFile ? (long)h.VoxOffset : d.Length;
                if (pos + 4 <= d.Length && pos + 4 <= end && d[pos] != 0)
                {
                    pos += 4;
                    while (pos + 8 <= d.Length && pos + 8 <= end)
                    {
                        int size = I32(d, big, pos), code = I32(d, big, pos + 4);
                        if (size < 8 || pos + (long)size > d.Length || pos + (long)size > end) break;
                        h.Extensions.Add((code, d.Slice(pos + 8, size - 8).ToArray()));
                        pos += size;
                    }
                }
            }
            return h;
        }

        static (SampleType type, int comps, ColorModel color) TypeOf(int dt) => dt switch
        {
            2 => (SampleType.UInt8, 1, ColorModel.Gray),
            4 => (SampleType.Int16, 1, ColorModel.Gray),
            8 => (SampleType.Int32, 1, ColorModel.Gray),
            16 => (SampleType.Float32, 1, ColorModel.Gray),
            32 => (SampleType.Float32, 2, ColorModel.Other),
            64 => (SampleType.Float64, 1, ColorModel.Gray),
            128 => (SampleType.UInt8, 3, ColorModel.Rgb),
            256 => (SampleType.Int8, 1, ColorModel.Gray),
            512 => (SampleType.UInt16, 1, ColorModel.Gray),
            768 => (SampleType.UInt32, 1, ColorModel.Gray),
            1024 => (SampleType.Int64, 1, ColorModel.Gray),
            1280 => (SampleType.UInt64, 1, ColorModel.Gray),
            1792 => (SampleType.Float64, 2, ColorModel.Other),
            2304 => (SampleType.UInt8, 4, ColorModel.Rgba),
            1 => throw new ImageNotSupportedException("NIfTI: binary (1 bit) data is not supported"),
            1536 or 2048 => throw new ImageNotSupportedException("NIfTI: float128 / complex256 are not supported"),
            _ => throw new ImageFormatException($"NIfTI: unknown datatype {dt}"),
        };

        static bool IsVectorIntent(int code) => code is 1005 or 1006 or 1007 or 1010 or 1011 or 1012 or 2003 or 2004;

        sealed class Layout
        {
            public SampleType Stored;
            public int Comps;               // interleaved components per voxel (RGB24, complex)
            public int VecAxis = -1;        // dim index (1-based) of planar components
            public int Channels;
            public ColorModel Color;
            public int[] Grid = Array.Empty<int>();
            public long[] Dims = Array.Empty<long>(); // dim[1..n]
        }

        static Layout LayoutOf(NiftiHeader h)
        {
            var (t, comps, color) = TypeOf(h.DataType);
            var l = new Layout { Stored = t, Comps = comps, Color = color };
            int n = (int)h.Dim[0];
            l.Dims = new long[n];
            for (int i = 0; i < n; i++) l.Dims[i] = h.Dim[i + 1];
            if (n >= 5 && h.Dim[5] > 1 && IsVectorIntent(h.IntentCode) && comps == 1) l.VecAxis = 5;
            l.Channels = l.VecAxis > 0 ? (int)h.Dim[5] : comps;
            if (l.VecAxis > 0)
                l.Color = (h.IntentCode, l.Channels) switch { (2003, 3) => ColorModel.Rgb, (2004, 4) => ColorModel.Rgba, _ => ColorModel.Other };
            var grid = new List<int>();
            for (int i = 1; i <= n; i++) if (i != l.VecAxis) grid.Add((int)h.Dim[i]);
            while (grid.Count > 1 && grid[^1] == 1) grid.RemoveAt(grid.Count - 1);
            if (grid.Count == 1) grid.Add(1);
            l.Grid = grid.ToArray();
            return l;
        }

        internal static double[,] Affine(NiftiHeader h, out int method)
        {
            var a = new double[4, 4];
            a[3, 3] = 1;
            if (h.SFormCode > 0)
            {
                method = 3;
                for (int r = 0; r < 3; r++) for (int c = 0; c < 4; c++) a[r, c] = h.SRow[r, c];
                return a;
            }
            double dx = h.PixDim[1], dy = h.PixDim[2], dz = h.PixDim[3];
            if (h.QFormCode > 0)
            {
                method = 2;
                double b = h.Quatern[0], c = h.Quatern[1], d = h.Quatern[2];
                double aa = 1 - (b * b + c * c + d * d);
                double qa;
                if (aa < 1e-7) { double s = 1 / Math.Sqrt(b * b + c * c + d * d); b *= s; c *= s; d *= s; qa = 0; }
                else qa = Math.Sqrt(aa);
                double qfac = h.PixDim[0] < 0 ? -1 : 1;
                var R = new double[3, 3]
                {
                    { qa * qa + b * b - c * c - d * d, 2 * (b * c - qa * d), 2 * (b * d + qa * c) },
                    { 2 * (b * c + qa * d), qa * qa + c * c - b * b - d * d, 2 * (c * d - qa * b) },
                    { 2 * (b * d - qa * c), 2 * (c * d + qa * b), qa * qa + d * d - c * c - b * b },
                };
                double[] sc = { dx, dy, dz * qfac };
                for (int r = 0; r < 3; r++)
                {
                    for (int k = 0; k < 3; k++) a[r, k] = R[r, k] * sc[k];
                    a[r, 3] = h.Quatern[3 + r];
                }
                return a;
            }
            method = 1;
            a[0, 0] = dx == 0 ? 1 : dx; a[1, 1] = dy == 0 ? 1 : dy; a[2, 2] = dz == 0 ? 1 : dz;
            return a;
        }

        static string? SpaceUnit(int u) => (u & 7) switch { 1 => "m", 2 => "mm", 3 => "um", _ => null };
        static string? TimeUnit(int u) => (u & 0x38) switch { 8 => "s", 16 => "ms", 24 => "us", 32 => "Hz", 40 => "ppm", 48 => "rad/s", _ => null };

        static PhysicalMetadata Metadata(NiftiHeader h, Layout l)
        {
            var m = new PhysicalMetadata { SourceFormat = ImageFormat.Nifti };
            int n = l.Grid.Length;
            m.Spacing = new double[n];
            m.Units = new string?[n];
            var aff = Affine(h, out int method);
            int spatial = Math.Min(3, n);
            var cols = new double[3, spatial];
            for (int r = 0; r < 3; r++) for (int c = 0; c < spatial; c++) cols[r, c] = aff[r, c];
            var len = VolumeIO.Normalize(cols);
            for (int g = 0; g < n; g++)
            {
                m.Spacing[g] = g < spatial ? (method == 1 ? Math.Abs(h.PixDim[g + 1]) is var p && p > 0 ? p : double.NaN : len[g]) : h.PixDim[Math.Min(7, g + 1)];
                m.Units[g] = g < 3 ? SpaceUnit(h.XyztUnits) : g == 3 ? TimeUnit(h.XyztUnits) : null;
            }
            if (method > 1)
            {
                m.Space = "RAS";
                m.Direction = cols;
                m.Origin = new[] { aff[0, 3], aff[1, 3], aff[2, 3] };
            }
            if (h.SclSlope != 0 && double.IsFinite(h.SclSlope) && !(l.Comps > 1 && l.Color != ColorModel.Other) && double.IsFinite(h.SclInter))
            {
                m.Slope = h.SclSlope; m.Intercept = h.SclInter;
            }
            var e = m.Header;
            void Add(string k, object v) => e.Add(new(k, Convert.ToString(v, VolumeIO.Inv) ?? ""));
            Add("sizeof_hdr", h.HeaderSize); Add("magic", h.Version == 0 ? "" : (h.SingleFile ? "n+" : "ni") + h.Version);
            Add("dim_info", h.DimInfo);
            Add("dim", string.Join(" ", Array.ConvertAll(h.Dim, x => x.ToString(VolumeIO.Inv))));
            Add("intent_p1", h.IntentP1); Add("intent_p2", h.IntentP2); Add("intent_p3", h.IntentP3); Add("intent_code", h.IntentCode);
            Add("datatype", h.DataType); Add("bitpix", h.BitPix); Add("slice_start", h.SliceStart);
            Add("pixdim", string.Join(" ", Array.ConvertAll(h.PixDim, x => x.ToString("R", VolumeIO.Inv))));
            Add("vox_offset", h.VoxOffset); Add("scl_slope", h.SclSlope); Add("scl_inter", h.SclInter); Add("slice_end", h.SliceEnd);
            Add("slice_code", h.SliceCode); Add("xyzt_units", h.XyztUnits); Add("cal_max", h.CalMax); Add("cal_min", h.CalMin);
            Add("slice_duration", h.SliceDuration); Add("toffset", h.TOffset); Add("descrip", h.Description); Add("aux_file", h.AuxFile);
            Add("qform_code", h.QFormCode); Add("sform_code", h.SFormCode);
            string[] q = { "quatern_b", "quatern_c", "quatern_d", "qoffset_x", "qoffset_y", "qoffset_z" };
            for (int i = 0; i < 6; i++) Add(q[i], h.Quatern[i]);
            for (int r = 0; r < 3; r++)
                Add("srow_" + "xyz"[r], $"{h.SRow[r, 0].ToString("R", VolumeIO.Inv)} {h.SRow[r, 1].ToString("R", VolumeIO.Inv)} {h.SRow[r, 2].ToString("R", VolumeIO.Inv)} {h.SRow[r, 3].ToString("R", VolumeIO.Inv)}");
            Add("intent_name", h.IntentName);
            foreach (var x in h.Extensions) Add("extension", $"ecode={x.Code} size={x.Data.Length + 8}");
            return m;
        }

        // ================================================================ info

        static ReadOnlySpan<byte> HeaderBytes(ReadOnlySpan<byte> data, out byte[]? owned)
        {
            owned = null;
            if (!Gzip.IsGzip(data)) return data;
            // header and extensions: decompress what precedes the samples (at most 1 MB)
            var b = new byte[1 << 20];
            int n = Gzip.Decompress(data, b);
            owned = b.AsSpan(0, n).ToArray();
            return owned;
        }

        public static VolumeInfo GetVolumeInfo(ReadOnlySpan<byte> data, VolumeDecodeOptions? options = null)
        {
            options ??= VolumeDecodeOptions.Default;
            var h = Parse(HeaderBytes(data, out _), true);
            return Info(h, options, Gzip.IsGzip(data));
        }

        static VolumeInfo Info(NiftiHeader h, VolumeDecodeOptions o, bool gz)
        {
            var l = LayoutOf(h);
            var m = Metadata(h, l);
            var t = VolumeIO.ScaledType(l.Stored, m.Slope, m.Intercept, o.Scaling);
            return new VolumeInfo
            {
                Format = ImageFormat.Nifti, Sizes = l.Grid, Channels = l.Channels, Color = l.Color, StoredType = l.Stored, SampleType = t,
                Encoding = gz ? "gzip" : "raw", Detached = !h.SingleFile, Metadata = m,
            };
        }

        public static ImageInfo GetInfo(ReadOnlySpan<byte> data) => GetInfo(data, null);

        public static ImageInfo GetInfo(ReadOnlySpan<byte> data, VolumeDecodeOptions? options)
        {
            var h = Parse(Gzip.IsGzip(data) ? HeaderBytes(data, out _) : data, false);
            var i = Info(h, options ?? VolumeDecodeOptions.Default, false);
            return i.ToImageInfo(RawImage.BytesPerSample(i.StoredType) * 8);
        }

        // ================================================================ decode

        public static RawImage Decode(ReadOnlySpan<byte> data, VolumeDecodeOptions? options = null) => DecodeVolume(data, options).GetSlice(0);

        /// <summary>
        /// Decodes a .nii / .nii.gz. For a .hdr of a pair the .img is needed: use the path overload, or set
        /// <see cref="VolumeDecodeOptions.OpenDataFile"/> (called with "img").
        /// </summary>
        public static RawVolume DecodeVolume(ReadOnlySpan<byte> data, VolumeDecodeOptions? options = null)
        {
            options ??= VolumeDecodeOptions.Default;
            bool gz = Gzip.IsGzip(data);
            return VolumeIO.WithStream(data, s => Read(gz ? new GzipReader(s, true) : s, options, null));
        }

        /// <summary>Decodes a .nii, .nii.gz, or a .hdr (+ .img / .img.gz next to it). Data above 2 GB is fine.</summary>
        public static RawVolume DecodeVolume(string path, VolumeDecodeOptions? options = null)
        {
            options ??= VolumeDecodeOptions.Default;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            Span<byte> sig = stackalloc byte[3];
            int n = fs.Read(sig);
            fs.Position = 0;
            Stream s = n == 3 && Gzip.IsGzip(sig) ? new GzipReader(fs, true) : fs;
            return Read(s, options, path);
        }

        static Stream OpenImg(string? headerPath, VolumeDecodeOptions o)
        {
            if (o.OpenDataFile != null) return Wrap(o.OpenDataFile("img"));
            if (headerPath == null) throw new ImageFormatException("NIfTI: a .hdr needs its .img: decode from the path, or set OpenDataFile");
            string p = headerPath;
            if (p.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)) p = p.Substring(0, p.Length - 3);
            string stem = Path.ChangeExtension(p, null);
            foreach (var c in new[] { stem + ".img", stem + ".IMG", stem + ".img.gz", stem + ".IMG.GZ" })
                if (File.Exists(c)) return Wrap(new FileStream(c, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16));
            throw new ImageFormatException($"NIfTI: '{stem}.img' not found");
        }

        static Stream Wrap(Stream s)
        {
            if (!s.CanSeek) return s;
            Span<byte> sig = stackalloc byte[3];
            long p = s.Position;
            int n = s.Read(sig);
            s.Position = p;
            return n == 3 && Gzip.IsGzip(sig) ? new GzipReader(s, false) : s;
        }

        static RawVolume Read(Stream s, VolumeDecodeOptions o, string? path)
        {
            // header (and the extensions of a single file, which end at vox_offset)
            var head = new byte[540];
            int got = ReadUpTo(s, head);
            var h = Parse(head.AsSpan(0, got), false);
            long pos = got;
            if (h.SingleFile)
            {
                long off = (long)h.VoxOffset;
                if (off > pos)
                {
                    if (off - pos > 1 << 26) throw new ImageNotSupportedException("NIfTI: extensions above 64 MB");
                    var all = new byte[off];
                    head.AsSpan(0, got).CopyTo(all);
                    if (ReadUpTo(s, all.AsSpan(got)) != off - got) throw new ImageFormatException("NIfTI: data ends early");
                    h = Parse(all, true);
                }
                else if (off < pos)
                {
                    // vox_offset 348 / 352 inside what was read (NIfTI-1)
                    var rest = head.AsSpan((int)off, got - (int)off).ToArray();
                    return ReadSamples(h, new ConcatStream(rest, s), o);
                }
                return ReadSamples(h, s, o);
            }
            using var img = OpenImg(path, o);
            VolumeIO.Skip(img, (long)Math.Max(0, h.VoxOffset), Fmt);
            return ReadSamples(h, img, o);
        }

        static int ReadUpTo(Stream s, Span<byte> b)
        {
            int n = 0;
            while (n < b.Length)
            {
                int r = s.Read(b.Slice(n));
                if (r <= 0) break;
                n += r;
            }
            return n;
        }

        static RawVolume ReadSamples(NiftiHeader h, Stream s, VolumeDecodeOptions o)
        {
            var l = LayoutOf(h);
            long voxels = 1;
            foreach (var d in l.Dims) voxels = checked(voxels * d);
            long count = voxels * l.Comps;
            var arr = VolumeIO.Allocate(l.Stored, count, o.MaxSamples, Fmt);
            VolumeIO.ReadSamples(s, arr, 0, count, h.BigEndian, Fmt);
            if (l.VecAxis > 0)
            {
                // planar components along dim 5: interleave each block of dims 1..4
                long inner = 1;
                for (int i = 0; i < 4 && i < l.Dims.Length; i++) inner *= l.Dims[i];
                long block = inner * l.Channels;
                if (block != count)
                {
                    var dst = RawImage.CreateArray(l.Stored, (int)count);
                    for (long b = 0; b < count; b += block)
                    {
                        var src = RawImage.CreateArray(l.Stored, (int)block);
                        Array.Copy(arr, b, src, 0, block);
                        Array.Copy(VolumeIO.Interleave(src, inner, l.Channels), 0, dst, b, block);
                    }
                    arr = dst;
                }
                else arr = VolumeIO.Interleave(arr, inner, l.Channels);
            }
            var m = Metadata(h, l);
            var t = VolumeIO.ScaledType(l.Stored, m.Slope, m.Intercept, o.Scaling);
            if (VolumeIO.HasScaling(m.Slope, m.Intercept) && o.Scaling != DataScaling.Raw || t != l.Stored)
            {
                arr = VolumeIO.Scale(arr, t, m.Slope, m.Intercept, null);
                m.ScalingApplied = VolumeIO.HasScaling(m.Slope, m.Intercept);
            }
            return new RawVolume(l.Grid, l.Color, arr, l.Channels, m);
        }

        /// <summary>Some bytes already read, then the rest of a stream.</summary>
        sealed class ConcatStream : Stream
        {
            readonly byte[] head; int hp; readonly Stream rest;
            public ConcatStream(byte[] head, Stream rest) { this.head = head; this.rest = rest; }
            public override int Read(Span<byte> b)
            {
                if (hp < head.Length) { int n = Math.Min(b.Length, head.Length - hp); head.AsSpan(hp, n).CopyTo(b); hp += n; return n; }
                return rest.Read(b);
            }
            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
            public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        // ================================================================ encode

        public static void Encode(RawImage image, Stream output, NiftiEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            Encode(RawVolume.FromImage(image), output, options);
        }

        public static byte[] Encode(RawImage image, NiftiEncodeOptions? options = null) => ToBytes(s => Encode(image, s, options));
        public static byte[] Encode(RawVolume volume, NiftiEncodeOptions? options = null) => ToBytes(s => Encode(volume, s, options));

        /// <summary>
        /// Writes a NIfTI-1 single file. RGB(A) UInt8 become RGB24 / RGBA32; other channels a vector intent with planar
        /// components along dim 5; Float16 is written as Float32. Geometry from <see cref="RawVolume.Metadata"/> (converted to
        /// RAS) goes to both qform and sform.
        /// </summary>
        public static void Encode(RawVolume v, Stream output, NiftiEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(v);
            ArgumentNullException.ThrowIfNull(output);
            options ??= new NiftiEncodeOptions();
            if (options.Gzip)
            {
                using var g = new GzipWriter(output, options.CompressionLevel, leaveOpen: true);
                Write(v, g, options);
            }
            else Write(v, output, options);
        }

        static void Write(RawVolume v, Stream output, NiftiEncodeOptions o)
        {
            var data = v.Data;
            var type = v.SampleType;
            if (type == SampleType.Float16) { data = VolumeIO.Convert(data, SampleType.Float32); type = SampleType.Float32; }
            var m = v.Metadata;
            bool rgb = type == SampleType.UInt8 && (v.Color == ColorModel.Rgb || v.Color == ColorModel.Rgba);
            bool vec = v.Channels > 1 && !rgb;
            int dt = rgb ? (v.Channels == 3 ? 128 : 2304) : type switch
            {
                SampleType.UInt8 => 2, SampleType.Int16 => 4, SampleType.Int32 => 8, SampleType.Float32 => 16, SampleType.Float64 => 64,
                SampleType.Int8 => 256, SampleType.UInt16 => 512, SampleType.UInt32 => 768, SampleType.Int64 => 1024, SampleType.UInt64 => 1280,
                _ => throw new ArgumentException($"NIfTI: cannot write {type}"),
            };
            int bitpix = RawImage.BytesPerSample(type) * 8 * (rgb ? v.Channels : 1);
            var dims = new long[8];
            int g = v.Sizes.Length;
            if (vec)
            {
                if (g > 4) throw new ArgumentException("NIfTI: vector data with more than 4 grid axes");
                dims[0] = 5;
                for (int i = 0; i < 4; i++) dims[i + 1] = i < g ? v.Sizes[i] : 1;
                dims[5] = v.Channels;
            }
            else
            {
                if (g > 7) throw new ArgumentException("NIfTI: more than 7 axes");
                dims[0] = Math.Max(g, 3);
                for (int i = 0; i < dims[0]; i++) dims[i + 1] = i < g ? v.Sizes[i] : 1;
            }
            for (int i = (int)dims[0] + 1; i < 8; i++) dims[i] = 1;
            foreach (var d in dims) if (d > short.MaxValue) throw new ArgumentException("NIfTI-1: an axis exceeds 32767 samples");

            var hd = new byte[352];
            var s = hd.AsSpan();
            BinaryPrimitives.WriteInt32LittleEndian(s, 348);
            s[38] = (byte)'r';
            int dimInfo = m?.SourceFormat == ImageFormat.Nifti && int.TryParse(m.Get("dim_info"), out var di) ? di : 0;
            s[39] = (byte)dimInfo;
            for (int i = 0; i < 8; i++) BinaryPrimitives.WriteInt16LittleEndian(s.Slice(40 + 2 * i), (short)dims[i]);
            int intent = o.IntentCode ?? (m?.SourceFormat == ImageFormat.Nifti && int.TryParse(m.Get("intent_code"), out var ic) && (!vec || IsVectorIntent(ic)) ? ic : vec ? (v.Color == ColorModel.Rgb ? 2003 : v.Color == ColorModel.Rgba ? 2004 : 1007) : 0);
            if (vec && !IsVectorIntent(intent)) intent = 1007;
            BinaryPrimitives.WriteInt16LittleEndian(s.Slice(68), (short)intent);
            if (m?.SourceFormat == ImageFormat.Nifti)
            {
                for (int i = 0; i < 3; i++)
                    if (double.TryParse(m.Get("intent_p" + (i + 1)), System.Globalization.NumberStyles.Float, VolumeIO.Inv, out var p))
                        BinaryPrimitives.WriteSingleLittleEndian(s.Slice(56 + 4 * i), (float)p);
                var nm = Encoding.Latin1.GetBytes(m.Get("intent_name") ?? "");
                nm.AsSpan(0, Math.Min(15, nm.Length)).CopyTo(s.Slice(328));
            }
            BinaryPrimitives.WriteInt16LittleEndian(s.Slice(70), (short)dt);
            BinaryPrimitives.WriteInt16LittleEndian(s.Slice(72), (short)bitpix);

            // geometry in RAS
            var pix = new double[8];
            for (int i = 1; i < 8; i++) pix[i] = 1;
            if (m != null)
                for (int i = 0; i < Math.Min(7, m.Spacing.Length); i++)
                    if (!double.IsNaN(m.Spacing[i]) && m.Spacing[i] != 0) pix[i + 1] = Math.Abs(m.Spacing[i]);
            int code = 0;
            double[,]? R = null; double[] T = new double[3];
            if (m != null && (m.Direction != null || m.Origin != null))
            {
                var flips = PhysicalMetadata.Flips(m.Space, "RAS") ?? new[] { 1.0, 1, 1 };
                R = new double[3, 3];
                for (int c = 0; c < 3; c++)
                    for (int r = 0; r < 3; r++)
                        R[r, c] = (m.Direction != null && c < m.Direction.GetLength(1) ? (r < m.Direction.GetLength(0) ? m.Direction[r, c] : 0) : (r == c ? 1 : 0)) * flips[r];
                if (m.Origin != null) for (int r = 0; r < Math.Min(3, m.Origin.Length); r++) T[r] = m.Origin[r] * flips[r];
                code = 1;
                if (m.SourceFormat == ImageFormat.Nifti && int.TryParse(m.Get("sform_code"), out var sc) && sc > 0) code = sc;
                else if (m.SourceFormat == ImageFormat.Nifti && int.TryParse(m.Get("qform_code"), out var qc) && qc > 0) code = qc;
            }
            double qfac = 1;
            var quat = new double[3];
            if (R != null)
            {
                // complete a 2D direction to a right handed frame for the third column if it is degenerate
                double n2 = R[0, 2] * R[0, 2] + R[1, 2] * R[1, 2] + R[2, 2] * R[2, 2];
                if (n2 < 1e-12)
                {
                    R[0, 2] = R[1, 0] * R[2, 1] - R[2, 0] * R[1, 1];
                    R[1, 2] = R[2, 0] * R[0, 1] - R[0, 0] * R[2, 1];
                    R[2, 2] = R[0, 0] * R[1, 1] - R[1, 0] * R[0, 1];
                }
                double det = R[0, 0] * (R[1, 1] * R[2, 2] - R[1, 2] * R[2, 1]) - R[0, 1] * (R[1, 0] * R[2, 2] - R[1, 2] * R[2, 0]) + R[0, 2] * (R[1, 0] * R[2, 1] - R[1, 1] * R[2, 0]);
                var Q = (double[,])R.Clone();
                if (det < 0) { qfac = -1; for (int r = 0; r < 3; r++) Q[r, 2] = -Q[r, 2]; }
                quat = Quaternion(Q);
            }
            pix[0] = qfac;
            for (int i = 0; i < 8; i++) BinaryPrimitives.WriteSingleLittleEndian(s.Slice(76 + 4 * i), (float)pix[i]);
            BinaryPrimitives.WriteSingleLittleEndian(s.Slice(108), 352);
            // value scaling: only for samples that do not have it applied already
            if (m != null && !m.ScalingApplied && VolumeIO.HasScaling(m.Slope, m.Intercept))
            {
                BinaryPrimitives.WriteSingleLittleEndian(s.Slice(112), (float)m.Slope);
                BinaryPrimitives.WriteSingleLittleEndian(s.Slice(116), (float)m.Intercept);
            }
            int units = 0;
            if (m != null && m.Units.Length > 0)
                units |= m.Units[0] switch { "m" => 1, "mm" => 2, "um" or "µm" or "micron" => 3, _ => 0 };
            if (m != null && m.Units.Length > 3)
                units |= m.Units[3] switch { "s" or "sec" => 8, "ms" or "msec" => 16, "us" or "usec" => 24, "Hz" => 32, "ppm" => 40, "rad/s" => 48, _ => 0 };
            s[123] = (byte)units;
            if (m?.SourceFormat == ImageFormat.Nifti)
            {
                if (double.TryParse(m.Get("cal_max"), System.Globalization.NumberStyles.Float, VolumeIO.Inv, out var cmax)) BinaryPrimitives.WriteSingleLittleEndian(s.Slice(124), (float)cmax);
                if (double.TryParse(m.Get("cal_min"), System.Globalization.NumberStyles.Float, VolumeIO.Inv, out var cmin)) BinaryPrimitives.WriteSingleLittleEndian(s.Slice(128), (float)cmin);
                if (double.TryParse(m.Get("toffset"), System.Globalization.NumberStyles.Float, VolumeIO.Inv, out var tof)) BinaryPrimitives.WriteSingleLittleEndian(s.Slice(136), (float)tof);
            }
            var desc = Encoding.Latin1.GetBytes(o.Description ?? (m?.SourceFormat == ImageFormat.Nifti ? m.Get("descrip") ?? "" : ""));
            desc.AsSpan(0, Math.Min(79, desc.Length)).CopyTo(s.Slice(148));
            BinaryPrimitives.WriteInt16LittleEndian(s.Slice(252), (short)code);
            BinaryPrimitives.WriteInt16LittleEndian(s.Slice(254), (short)code);
            for (int i = 0; i < 3; i++) BinaryPrimitives.WriteSingleLittleEndian(s.Slice(256 + 4 * i), (float)quat[i]);
            for (int i = 0; i < 3; i++) BinaryPrimitives.WriteSingleLittleEndian(s.Slice(268 + 4 * i), (float)T[i]);
            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 3; c++)
                    BinaryPrimitives.WriteSingleLittleEndian(s.Slice(280 + 16 * r + 4 * c), (float)(R != null ? R[r, c] * pix[c + 1] : r == c ? pix[c + 1] : 0));
                BinaryPrimitives.WriteSingleLittleEndian(s.Slice(280 + 16 * r + 12), (float)T[r]);
            }
            "n+1\0"u8.CopyTo(s.Slice(344));
            output.Write(hd);
            if (vec)
            {
                long inner = v.FrameLength / v.Channels * (g > 3 ? v.Sizes[3] : 1);
                data = VolumeIO.Deinterleave(data, inner, v.Channels);
            }
            VolumeIO.WriteSamples(output, data, 0, data.LongLength, false);
        }

        /// <summary>Quaternion (b, c, d) of a proper rotation matrix, with a = sqrt(1 - b² - c² - d²) &gt;= 0.</summary>
        static double[] Quaternion(double[,] R)
        {
            // orthonormalise the columns first (spacing is in pixdim)
            double tr = R[0, 0] + R[1, 1] + R[2, 2];
            double a, b, c, d;
            if (tr > 0)
            {
                double s = Math.Sqrt(tr + 1) * 2;
                a = 0.25 * s; b = (R[2, 1] - R[1, 2]) / s; c = (R[0, 2] - R[2, 0]) / s; d = (R[1, 0] - R[0, 1]) / s;
            }
            else if (R[0, 0] > R[1, 1] && R[0, 0] > R[2, 2])
            {
                double s = Math.Sqrt(1 + R[0, 0] - R[1, 1] - R[2, 2]) * 2;
                a = (R[2, 1] - R[1, 2]) / s; b = 0.25 * s; c = (R[0, 1] + R[1, 0]) / s; d = (R[0, 2] + R[2, 0]) / s;
            }
            else if (R[1, 1] > R[2, 2])
            {
                double s = Math.Sqrt(1 + R[1, 1] - R[0, 0] - R[2, 2]) * 2;
                a = (R[0, 2] - R[2, 0]) / s; b = (R[0, 1] + R[1, 0]) / s; c = 0.25 * s; d = (R[1, 2] + R[2, 1]) / s;
            }
            else
            {
                double s = Math.Sqrt(1 + R[2, 2] - R[0, 0] - R[1, 1]) * 2;
                a = (R[1, 0] - R[0, 1]) / s; b = (R[0, 2] + R[2, 0]) / s; c = (R[1, 2] + R[2, 1]) / s; d = 0.25 * s;
            }
            if (a < 0) { b = -b; c = -c; d = -d; }
            double n = Math.Sqrt(a * a + b * b + c * c + d * d);
            return new[] { b / n, c / n, d / n };
        }

        static byte[] ToBytes(Action<Stream> write)
        {
            var ms = new MemoryStream();
            write(ms);
            return ms.ToArray();
        }
    }
}
