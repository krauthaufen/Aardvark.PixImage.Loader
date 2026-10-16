using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Aardvark.ImageCodecs
{
    /// <summary>
    /// How decoders of scientific formats treat the value scaling a file declares (NIfTI scl_slope / scl_inter, FITS BSCALE /
    /// BZERO): physical value = stored value * slope + intercept.
    /// </summary>
    public enum DataScaling : byte
    {
        /// <summary>
        /// The default. The scaling is applied, in a sample type that loses nothing: when slope and intercept are integers the
        /// result stays integer, in the smallest type holding every possible value (so FITS' unsigned conventions, BZERO = 32768
        /// with 16 bit data and so on, give UInt16 / UInt32 / UInt64 / Int8); otherwise Float32 for stored types up to 16 bits
        /// and Float32 data, Float64 for 32 / 64 bit integers and Float64 data. Integer samples equal to a FITS BLANK value
        /// become NaN when the result is float. Without scaling (slope 1, intercept 0) the stored type is kept.
        /// </summary>
        Physical = 0,
        /// <summary>The stored values unchanged; slope and intercept are only reported (<see cref="PhysicalMetadata.Slope"/>).</summary>
        Raw = 1,
        /// <summary>The scaling is applied and the result is always Float32 (Float64 data stays Float64).</summary>
        Float32 = 2,
        /// <summary>The scaling is applied and the result is always Float64.</summary>
        Float64 = 3,
    }

    /// <summary>
    /// The physical description of a sampled grid (a 2D image or a volume) from a scientific / medical format: where the
    /// samples lie in space, what their values mean, and the file's own header entries. Filled by the NRRD, NIfTI, MetaImage
    /// and FITS decoders (<see cref="RawVolume.Metadata"/>, and <see cref="ImageMetadata.Physical"/> for 2D images) and used by
    /// their encoders.
    /// <para>
    /// Grid axes are those of the decoded data: axis 0 = x (fastest), 1 = y (rows), 2 = z (slices), then further axes (time,
    /// ...). The position of the sample with index (i, j, k) is
    /// <c>Origin + i * Spacing[0] * Direction[:, 0] + j * Spacing[1] * Direction[:, 1] + k * Spacing[2] * Direction[:, 2]</c>
    /// in <see cref="Space"/> (see <see cref="GetVoxelToWorld"/>).
    /// </para>
    /// </summary>
    public sealed class PhysicalMetadata
    {
        /// <summary>The format the metadata was read from (Unknown when built by hand).</summary>
        public ImageFormat SourceFormat { get; set; }

        /// <summary>
        /// The world coordinate system: "RAS" (right-anterior-superior: NIfTI, 3D Slicer), "LPS" (left-posterior-superior:
        /// DICOM, ITK, MetaImage), "LAS", or another NRRD space name ("scanner-xyz", "3D-right-handed", ...), "FITS-WCS" for
        /// the linear part of a FITS world coordinate system; null if unknown. A time variant of a NRRD space ("RAST", ...) is
        /// reported without the time ("RAS").
        /// </summary>
        public string? Space { get; set; }

        /// <summary>Distance between samples along each grid axis (NaN where unknown). Length = number of grid axes.</summary>
        public double[] Spacing { get; set; } = Array.Empty<double>();

        /// <summary>Units of the grid axes (e.g. "mm", "s", "deg"), null entries where unknown. Length = number of grid axes.</summary>
        public string?[] Units { get; set; } = Array.Empty<string?>();

        /// <summary>World position of the first sample (index 0, 0, 0), null if unknown. Length = space dimension (usually 3).</summary>
        public double[]? Origin { get; set; }

        /// <summary>
        /// Unit direction of each spatial grid axis in world space: Direction[r, a] is component r of axis a (columns are
        /// axes), as ITK's direction matrix. Size space dimension x number of spatial axes; null if unknown (identity).
        /// </summary>
        public double[,]? Direction { get; set; }

        /// <summary>Unit of the sample values (FITS BUNIT, NRRD sample units), null if unknown.</summary>
        public string? ValueUnit { get; set; }

        /// <summary>The file's value scaling (NIfTI scl_slope / scl_inter, FITS BSCALE / BZERO); 1 and 0 when there is none.</summary>
        public double Slope { get; set; } = 1;
        /// <inheritdoc cref="Slope"/>
        public double Intercept { get; set; }

        /// <summary>Whether the decoded samples have <see cref="Slope"/> and <see cref="Intercept"/> applied (see <see cref="DataScaling"/>).</summary>
        public bool ScalingApplied { get; set; }

        /// <summary>
        /// The value that marks undefined samples in the decoded data when it stays integer (FITS BLANK, scaled like the data);
        /// null when there is none or undefined samples became NaN.
        /// </summary>
        public double? Blank { get; set; }

        /// <summary>
        /// Every entry of the file's header in file order, as text: NRRD fields and key:=value pairs, NIfTI header fields (by
        /// their names in nifti1.h: "intent_code", "qform_code", "descrip", ...), MetaImage tags, FITS keywords (also COMMENT /
        /// HISTORY, which can repeat; strings without quotes, logicals as T / F).
        /// </summary>
        public List<KeyValuePair<string, string>> Header { get; set; } = new();

        /// <summary>The last header entry named <paramref name="key"/> (case insensitive), null if there is none.</summary>
        public string? Get(string key)
        {
            for (int i = Header.Count - 1; i >= 0; i--)
                if (string.Equals(Header[i].Key, key, StringComparison.OrdinalIgnoreCase)) return Header[i].Value;
            return null;
        }

        /// <summary>Space dimension: length of <see cref="Origin"/> or rows of <see cref="Direction"/>, else 3.</summary>
        public int SpaceDimension => Origin?.Length ?? Direction?.GetLength(0) ?? 3;

        /// <summary>
        /// The 4x4 affine (row major, [row, column]) from a voxel index (i, j, k, 1) to world coordinates in <see cref="Space"/>,
        /// for the first three grid axes; missing parts are taken as identity, unknown spacings as 1.
        /// </summary>
        public double[,] GetVoxelToWorld()
        {
            var m = new double[4, 4];
            m[3, 3] = 1;
            int axes = Direction?.GetLength(1) ?? 0, rows = Direction?.GetLength(0) ?? 0;
            for (int a = 0; a < 3; a++)
            {
                double s = a < Spacing.Length && !double.IsNaN(Spacing[a]) ? Spacing[a] : 1;
                for (int r = 0; r < 3; r++)
                {
                    double d = a < axes ? (r < rows ? Direction![r, a] : 0) : (r == a ? 1 : 0);
                    m[r, a] = d * s;
                }
            }
            if (Origin != null) for (int r = 0; r < Math.Min(3, Origin.Length); r++) m[r, 3] = Origin[r];
            return m;
        }

        /// <summary>
        /// <see cref="GetVoxelToWorld"/> converted to LPS (ITK, DICOM) when <see cref="Space"/> is RAS, LAS or LPS (also when it
        /// is unknown: then the coordinates are taken as LPS); null for other spaces.
        /// </summary>
        public double[,]? GetVoxelToLps() => Convert(GetVoxelToWorld(), Space, "LPS");

        /// <summary><see cref="GetVoxelToWorld"/> converted to RAS (NIfTI, 3D Slicer); see <see cref="GetVoxelToLps"/>.</summary>
        public double[,]? GetVoxelToRas() => Convert(GetVoxelToWorld(), Space, "RAS");

        /// <summary>Sign flips that turn coordinates of <paramref name="from"/> into <paramref name="to"/> (RAS / LAS / LPS); null otherwise.</summary>
        internal static double[]? Flips(string? from, string to)
        {
            static double[]? Signs(string? s) => (s ?? "LPS") switch
            {
                "RAS" => new[] { 1.0, 1, 1 },
                "LAS" => new[] { -1.0, 1, 1 },
                "LPS" => new[] { -1.0, -1, 1 },
                "scanner-xyz" => new[] { -1.0, -1, 1 },
                _ => null,
            };
            var a = Signs(from); var b = Signs(to);
            if (a == null || b == null) return null;
            return new[] { a[0] * b[0], a[1] * b[1], a[2] * b[2] };
        }

        static double[,]? Convert(double[,] m, string? from, string to)
        {
            var f = Flips(from, to);
            if (f == null) return null;
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 4; c++) m[r, c] *= f[r];
            return m;
        }

        /// <summary>A deep copy.</summary>
        public PhysicalMetadata Clone() => new()
        {
            SourceFormat = SourceFormat, Space = Space, Spacing = (double[])Spacing.Clone(), Units = (string?[])Units.Clone(),
            Origin = (double[]?)Origin?.Clone(), Direction = (double[,]?)Direction?.Clone(), ValueUnit = ValueUnit, Slope = Slope,
            Intercept = Intercept, ScalingApplied = ScalingApplied, Blank = Blank, Header = new(Header),
        };

        /// <summary>The metadata restricted to the first <paramref name="axes"/> grid axes (for a slice or a frame).</summary>
        internal PhysicalMetadata ForAxes(int axes)
        {
            var c = Clone();
            if (c.Spacing.Length > axes) c.Spacing = c.Spacing.AsSpan(0, axes).ToArray();
            if (c.Units.Length > axes) c.Units = c.Units.AsSpan(0, axes).ToArray();
            return c;
        }
    }

    /// <summary>
    /// A decoded n-dimensional sample grid with interleaved channels: a volume (x, y, z), a time series of volumes, or any
    /// number of further axes. <see cref="Data"/> is dense: channel fastest, then x, then y, then z, then the further axes
    /// (row y = 0 first, as in <see cref="RawImage"/>), with exactly product(<see cref="Sizes"/>) * <see cref="Channels"/>
    /// elements of the type given by <see cref="SampleType"/>.
    /// <para>
    /// The axes beyond the third are "frames": <see cref="FrameCount"/> is their product, <see cref="GetFrame"/> picks one
    /// volume, <see cref="GetIndex"/> addresses samples by all coordinates.
    /// </para>
    /// </summary>
    public sealed class RawVolume
    {
        /// <summary>Size of every grid axis (at least one): x, y, z, then further axes.</summary>
        public int[] Sizes { get; }
        public int Channels { get; }
        public SampleType SampleType { get; }
        public ColorModel Color { get; }
        public Array Data { get; }
        public PhysicalMetadata? Metadata { get; set; }

        public int Width => Sizes[0];
        public int Height => Sizes.Length > 1 ? Sizes[1] : 1;
        public int Depth => Sizes.Length > 2 ? Sizes[2] : 1;
        /// <summary>Number of grid axes (<see cref="Sizes"/> length).</summary>
        public int Dimensions => Sizes.Length;

        /// <summary>Product of the sizes of the axes beyond the third (1 for a plain volume).</summary>
        public int FrameCount
        {
            get
            {
                long n = 1;
                for (int i = 3; i < Sizes.Length; i++) n *= Sizes[i];
                return (int)n;
            }
        }

        /// <summary>Samples (times channels) of one z slice.</summary>
        public long SliceLength => (long)Width * Height * Channels;
        /// <summary>Samples (times channels) of one frame (one volume).</summary>
        public long FrameLength => SliceLength * Depth;

        public RawVolume(int[] sizes, ColorModel color, Array data, int channels = 0, PhysicalMetadata? metadata = null)
        {
            ArgumentNullException.ThrowIfNull(sizes);
            ArgumentNullException.ThrowIfNull(data);
            if (sizes.Length == 0) throw new ArgumentException("a grid needs at least one axis");
            long n = 1;
            foreach (var s in sizes)
            {
                if (s <= 0) throw new ArgumentException("grid sizes must be positive");
                n *= s;
                if (n > Array.MaxLength) throw new ArgumentException("grid too large");
            }
            if (channels == 0) channels = RawImage.ChannelCount(color);
            if (channels <= 0) throw new ArgumentException("channel count must be positive");
            if (color != ColorModel.Other && channels != RawImage.ChannelCount(color))
                throw new ArgumentException($"{color} requires {RawImage.ChannelCount(color)} channels, got {channels}");
            if (data.LongLength != n * channels) throw new ArgumentException($"data length {data.LongLength} does not match the grid ({n} x {channels})");
            Sizes = (int[])sizes.Clone();
            Channels = channels; Color = color; Data = data;
            SampleType = RawImage.SampleTypeOf(data.GetType().GetElementType()!);
            Metadata = metadata;
        }

        public RawVolume(int width, int height, int depth, ColorModel color, Array data, int channels = 0, PhysicalMetadata? metadata = null)
            : this(new[] { width, height, depth }, color, data, channels, metadata) { }

        /// <summary>Allocates a zeroed grid.</summary>
        public static RawVolume Create(int[] sizes, ColorModel color, SampleType type, int channels = 0)
        {
            if (channels == 0) channels = RawImage.ChannelCount(color);
            long n = channels;
            foreach (var s in sizes) n *= s;
            if (n > Array.MaxLength) throw new ImageNotSupportedException("grid too large for one array");
            return new RawVolume(sizes, color, RawImage.CreateArray(type, (int)n), channels);
        }

        /// <summary>The typed sample array. Throws if T does not match <see cref="SampleType"/>.</summary>
        public T[] GetData<T>() where T : unmanaged
            => typeof(T) == Data.GetType().GetElementType()
                ? (T[])Data
                : throw new InvalidOperationException($"volume holds {SampleType} samples, not {typeof(T).Name}");

        /// <summary>Index into <see cref="Data"/> of channel 0 of the sample at the given grid coordinates (missing ones are 0).</summary>
        public long GetIndex(params int[] coordinates)
        {
            long idx = 0, stride = Channels;
            for (int a = 0; a < Sizes.Length; a++)
            {
                int c = a < coordinates.Length ? coordinates[a] : 0;
                if ((uint)c >= (uint)Sizes[a]) throw new ArgumentOutOfRangeException(nameof(coordinates));
                idx += c * stride;
                stride *= Sizes[a];
            }
            return idx;
        }

        /// <summary>A copy of one z slice of one frame as an image (with the metadata of the first two axes).</summary>
        public RawImage GetSlice(int z, int frame = 0)
        {
            if ((uint)z >= (uint)Depth) throw new ArgumentOutOfRangeException(nameof(z));
            if ((uint)frame >= (uint)FrameCount) throw new ArgumentOutOfRangeException(nameof(frame));
            long len = SliceLength;
            var a = RawImage.CreateArray(SampleType, (int)len);
            Array.Copy(Data, (long)frame * FrameLength + z * len, a, 0, len);
            var meta = Metadata == null ? null : new ImageMetadata { Physical = Metadata.ForAxes(2) };
            return new RawImage(Width, Height, Color, a, Channels, meta);
        }

        /// <summary>Copies of all z slices of one frame.</summary>
        public RawImage[] GetSlices(int frame = 0)
        {
            var r = new RawImage[Depth];
            for (int z = 0; z < r.Length; z++) r[z] = GetSlice(z, frame);
            return r;
        }

        /// <summary>A copy of one frame (one x, y, z volume); the grid itself when it has only one.</summary>
        public RawVolume GetFrame(int frame)
        {
            if ((uint)frame >= (uint)FrameCount) throw new ArgumentOutOfRangeException(nameof(frame));
            if (Sizes.Length <= 3) return this;
            long len = FrameLength;
            var a = RawImage.CreateArray(SampleType, (int)len);
            Array.Copy(Data, frame * len, a, 0, len);
            return new RawVolume(new[] { Width, Height, Depth }, Color, a, Channels, Metadata?.ForAxes(3));
        }

        /// <summary>A grid of 2D or more axes from equally sized slices (front to back), copying the samples.</summary>
        public static RawVolume FromSlices(IReadOnlyList<RawImage> slices, PhysicalMetadata? metadata = null)
        {
            ArgumentNullException.ThrowIfNull(slices);
            if (slices.Count == 0) throw new ArgumentException("a volume needs at least one slice", nameof(slices));
            var f = slices[0];
            long len = (long)f.Width * f.Height * f.Channels;
            if (len * slices.Count > Array.MaxLength) throw new ImageNotSupportedException("volume too large for one array");
            var a = RawImage.CreateArray(f.SampleType, (int)(len * slices.Count));
            for (int z = 0; z < slices.Count; z++)
            {
                var s = slices[z];
                if (s.Width != f.Width || s.Height != f.Height || s.Channels != f.Channels || s.SampleType != f.SampleType)
                    throw new ArgumentException("all slices must have the same size, channels and sample type", nameof(slices));
                Array.Copy(s.Data, 0, a, z * len, len);
            }
            return new RawVolume(new[] { f.Width, f.Height, slices.Count }, f.Color, a, f.Channels, metadata ?? f.Metadata?.Physical);
        }

        /// <summary>A one slice volume holding the image's samples (no copy).</summary>
        public static RawVolume FromImage(RawImage image)
        {
            ArgumentNullException.ThrowIfNull(image);
            return new RawVolume(new[] { image.Width, image.Height }, image.Color, image.Data, image.Channels, image.Metadata?.Physical);
        }

        /// <summary>The samples reinterpreted as raw bytes (in machine order), for grids below 2 GB.</summary>
        public Span<byte> GetBytes() => new RawImage(1, (int)(Data.LongLength / Channels), Color, Data, Channels).GetBytes();
    }

    /// <summary>
    /// Header level information on an n-dimensional grid (see <see cref="RawVolume"/>): what DecodeVolume returns with the
    /// given options.
    /// </summary>
    public sealed class VolumeInfo
    {
        public ImageFormat Format { get; internal set; }
        /// <summary>Size of every grid axis (x, y, z, further axes).</summary>
        public int[] Sizes { get; internal set; } = Array.Empty<int>();
        public int Channels { get; internal set; }
        public SampleType SampleType { get; internal set; }
        public ColorModel Color { get; internal set; }
        /// <summary>The sample type stored in the file (before any scaling).</summary>
        public SampleType StoredType { get; internal set; }
        /// <summary>Compression / encoding of the samples as the format names it ("raw", "gzip", "RICE_1", ...).</summary>
        public string Encoding { get; internal set; } = "raw";
        /// <summary>Whether the samples are in separate data file(s) (NRRD .nhdr, MetaImage .mhd, NIfTI .hdr / .img).</summary>
        public bool Detached { get; internal set; }
        public PhysicalMetadata Metadata { get; internal set; } = new();

        public int Width => Sizes.Length > 0 ? Sizes[0] : 0;
        public int Height => Sizes.Length > 1 ? Sizes[1] : 1;
        public int Depth => Sizes.Length > 2 ? Sizes[2] : 1;
        public int FrameCount
        {
            get
            {
                long n = 1;
                for (int i = 3; i < Sizes.Length; i++) n *= Sizes[i];
                return (int)Math.Min(n, int.MaxValue);
            }
        }

        internal ImageInfo ToImageInfo(int bits) => new(Format, Width, Height, Channels, SampleType, Color, FrameCount)
        {
            Depth = Depth, BitsPerSample = bits,
        };
    }
}
