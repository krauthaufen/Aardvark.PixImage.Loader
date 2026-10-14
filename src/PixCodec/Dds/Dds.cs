using System;
using System.Collections.Generic;
using System.IO;
using PixCodec.DdsImpl;

namespace PixCodec
{
    public enum DdsTextureKind { Texture1D, Texture2D, Texture3D, CubeMap }

    /// <summary>How the alpha channel of a DDS file is meant (DX10 header; DXT2 / DXT4 are premultiplied).</summary>
    public enum DdsAlphaMode { Unknown = 0, Straight = 1, Premultiplied = 2, Opaque = 3, Custom = 4 }

    public sealed class DdsDecodeOptions
    {
        /// <summary>Divide premultiplied colours (DXT2, DXT4, DX10 alpha mode premultiplied) by alpha (default), since RawImage alpha is straight.</summary>
        public bool Unpremultiply { get; set; } = true;

        /// <summary>Upper bound for the pixels of all surfaces together; larger files are rejected with <see cref="ImageNotSupportedException"/>.</summary>
        public long MaxPixels { get; set; } = 1L << 28;

        /// <summary>
        /// Surfaces of more pixels than this together are only accepted when the file holds their data; smaller truncated
        /// files decode with the missing data as zeros. Keeps a tiny damaged header from allocating gigabytes.
        /// </summary>
        public long MaxUnbackedPixels { get; set; } = 1L << 26;

        internal static readonly DdsDecodeOptions Default = new();
    }

    public enum DdsMipmaps
    {
        /// <summary>Only the given image(s).</summary>
        None,
        /// <summary>A full chain down to 1 x 1 is computed from each image with <see cref="DdsEncodeOptions.MipmapFilter"/>.</summary>
        Generate,
    }

    /// <summary>The stored pixel format of written textures.</summary>
    public enum DdsCompression
    {
        /// <summary>Uncompressed (the format follows the image's sample type and colour model).</summary>
        None,
        /// <summary>RGB with optional 1 bit alpha (texels with alpha below 128 become transparent black), 4 bits per pixel.</summary>
        BC1,
        /// <summary>RGB with 4 bit explicit alpha, 8 bits per pixel.</summary>
        BC2,
        /// <summary>RGB with interpolated alpha, 8 bits per pixel.</summary>
        BC3,
        /// <summary>One channel (the first), 4 bits per pixel; SNORM for signed integer images.</summary>
        BC4,
        /// <summary>Two channels (the first two), 8 bits per pixel; SNORM for signed integer images.</summary>
        BC5,
        /// <summary>HDR RGB as half floats (alpha is dropped), 8 bits per pixel; SF16 when any value is negative.</summary>
        BC6H,
        /// <summary>RGB or RGBA, 8 bits per pixel, the best quality of the 8 bit formats.</summary>
        BC7,
    }

    /// <summary>Speed versus quality of the block compression encoders.</summary>
    public enum BcQuality
    {
        /// <summary>Endpoints from the extremes along the principal axis, refined by least squares; few BC6H / BC7 modes.</summary>
        Fast,
        /// <summary>Least squares refinement and endpoint search (BC1 to BC5), the best ranked partitions (BC6H / BC7).</summary>
        Normal,
        /// <summary>Cluster fit (BC1 to BC3), wider searches (more partitions and modes, endpoint refinement); several times slower.</summary>
        High,
    }

    public sealed class DdsEncodeOptions
    {
        /// <summary>
        /// Block compression (<see cref="DdsCompression.None"/> by default). Integer images are scaled to 8 bits (BC1 to BC5,
        /// BC7); float images are clamped to [0, 1] for those formats. Gray images are replicated to RGB.
        /// </summary>
        public DdsCompression Compression { get; set; } = DdsCompression.None;

        /// <summary>Speed versus quality of the block compression.</summary>
        public BcQuality Quality { get; set; } = BcQuality.Normal;

        /// <summary>Upper bound on the threads used for block compression and mip generation, &lt;= 0 for all cores.</summary>
        public int MaxDegreeOfParallelism { get; set; }

        /// <summary>Whether to add a computed mip chain when encoding single images.</summary>
        public DdsMipmaps Mipmaps { get; set; } = DdsMipmaps.None;

        /// <summary>Filter for computed mip levels (each level is resized from the full image, alpha premultiplied while filtering).</summary>
        public ResampleFilter MipmapFilter { get; set; } = ResampleFilter.Box;

        /// <summary>Mark 8 bit colour data as sRGB (DX10 header with an _SRGB format; for BC1 to BC3 and BC7, ignored by the others).</summary>
        public bool Srgb { get; set; }
    }

    /// <summary>Header level description of a DDS file.</summary>
    public class DdsTextureInfo
    {
        public DdsTextureKind Kind { get; internal set; }
        /// <summary>Size of the top mip level.</summary>
        public int Width { get; internal set; }
        public int Height { get; internal set; }
        /// <summary>Slices of a volume texture (1 otherwise).</summary>
        public int Depth { get; internal set; } = 1;
        public int MipLevels { get; internal set; } = 1;
        /// <summary>Number of textures in an array (for cube maps: number of cubes).</summary>
        public int ArraySize { get; internal set; } = 1;
        /// <summary>The cube faces present, in the order +X, -X, +Y, -Y, +Z, -Z (all six for DX10 files); empty for other kinds.</summary>
        public IReadOnlyList<int> Faces { get; internal set; } = Array.Empty<int>();
        /// <summary>The stored pixel format as a DXGI name without the prefix, e.g. "BC7_UNORM_SRGB" or "B8G8R8A8_UNORM".</summary>
        public string Format { get; internal set; } = "";
        /// <summary>The DXGI_FORMAT value of the stored pixels (the legacy formats are mapped to their DXGI equivalent; 0 for
        /// legacy bit mask formats without one).</summary>
        public int DxgiFormat { get; internal set; }
        public bool IsSrgb { get; internal set; }
        public bool IsBlockCompressed { get; internal set; }
        public DdsAlphaMode AlphaMode { get; internal set; }
        /// <summary>What the decoded surfaces hold.</summary>
        public ColorModel Color { get; internal set; }
        public SampleType SampleType { get; internal set; }
        public int Channels { get; internal set; }
    }

    /// <summary>Every surface of a DDS file, decoded.</summary>
    public sealed class DdsTexture : DdsTextureInfo
    {
        // [item][face][mip][slice]
        internal RawImage[][][][] Surfaces = Array.Empty<RawImage[][][]>();

        /// <summary>
        /// One surface: mip level <paramref name="mip"/> of array element <paramref name="item"/>, cube face index <paramref name="face"/>
        /// (an index into <see cref="DdsTextureInfo.Faces"/>), volume slice <paramref name="slice"/> (slices halve with each mip level).
        /// </summary>
        public RawImage GetImage(int mip = 0, int item = 0, int face = 0, int slice = 0) => Surfaces[item][face][mip][slice];

        /// <summary>The mip chain of one array element / cube face (of slice 0 for volume textures), largest first.</summary>
        public RawImage[] GetMipChain(int item = 0, int face = 0)
        {
            var levels = Surfaces[item][face];
            var r = new RawImage[levels.Length];
            for (int m = 0; m < r.Length; m++) r[m] = levels[m][0];
            return r;
        }

        /// <summary>All slices of a volume texture at one mip level.</summary>
        public RawImage[] GetSlices(int mip = 0, int item = 0) => Surfaces[item][0][mip];
    }

    /// <summary>
    /// DirectDraw Surface (DDS) textures: 1D, 2D, volume and cube map textures, texture arrays and mip chains, with legacy and
    /// DX10 headers. Reads the block compressed formats BC1 to BC7 (DXT1 to DXT5, ATI1 / ATI2, BC6H half float, BC7),
    /// legacy bit mask formats (RGB, luminance, alpha, bump map), and the common uncompressed DXGI formats (8 / 16 / 32 bit
    /// integer and float, 10:10:10:2, 5:6:5, 5:5:5:1, 4:4:4:4, R11G11B10 and RGB9E5 float). Block compressed surfaces decode
    /// to UInt8 (BC1 to BC3 and BC7 as Rgba, BC4 as Gray, BC5 as two channels, the signed variants as Int8) and BC6H to
    /// Float16 Rgb. Writes 2D textures (optionally with computed mip chains), cube maps and volume textures, uncompressed or
    /// block compressed (BC1 to BC7, see <see cref="DdsEncodeOptions.Compression"/>).
    /// </summary>
    public static class Dds
    {
        /// <summary>Whether the data starts with the "DDS " signature.</summary>
        public static bool IsMatch(ReadOnlySpan<byte> header) => DdsReader.IsMatch(header);

        /// <summary>Header level information on the top surface; FrameCount is the number of array elements times cube faces,
        /// MipLevels the length of the mip chains.</summary>
        public static ImageInfo GetInfo(ReadOnlySpan<byte> data) => DdsReader.GetInfo(data);

        /// <summary>The texture's structure and format without decoding pixel data.</summary>
        public static DdsTextureInfo GetTextureInfo(ReadOnlySpan<byte> data) => DdsReader.Parse(data).Info;

        /// <summary>Decodes the top mip level of the first array element / cube face / volume slice.</summary>
        public static RawImage Decode(ReadOnlySpan<byte> data, DdsDecodeOptions? options = null)
            => DdsReader.DecodeTop(data, options ?? DdsDecodeOptions.Default);

        /// <summary>Decodes every surface.</summary>
        public static DdsTexture DecodeTexture(ReadOnlySpan<byte> data, DdsDecodeOptions? options = null)
            => DdsReader.DecodeAll(data, options ?? DdsDecodeOptions.Default);

        /// <summary>The mip chain of the first array element / cube face (slice 0 of a volume texture), largest first.</summary>
        public static RawImage[] DecodeMipmaps(ReadOnlySpan<byte> data, DdsDecodeOptions? options = null)
            => DdsReader.DecodeChain(data, options ?? DdsDecodeOptions.Default);

        /// <summary>
        /// Writes one image (Gray, GrayAlpha, Rgb or Rgba of UInt8, UInt16, Float16 or Float32), with a computed mip chain if
        /// <see cref="DdsEncodeOptions.Mipmaps"/> asks for one.
        /// </summary>
        public static void Encode(RawImage image, Stream output, DdsEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            options ??= new DdsEncodeOptions();
            DdsWriter.Write(output, new[] { Slices(DdsWriter.Chain(image, options)) }, DdsTextureKind.Texture2D, options);
        }

        public static byte[] Encode(RawImage image, DdsEncodeOptions? options = null) => ToBytes(s => Encode(image, s, options));

        /// <summary>Writes a given mip chain (largest first; each level half the previous size, rounded down, at least 1).</summary>
        public static void EncodeMipmaps(IReadOnlyList<RawImage> levels, Stream output, DdsEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(levels);
            DdsWriter.Write(output, new[] { Slices(levels) }, DdsTextureKind.Texture2D, options ?? new DdsEncodeOptions());
        }

        public static byte[] EncodeMipmaps(IReadOnlyList<RawImage> levels, DdsEncodeOptions? options = null) => ToBytes(s => EncodeMipmaps(levels, s, options));

        /// <summary>
        /// Writes a cube map from six square faces in the order +X, -X, +Y, -Y, +Z, -Z, each a mip chain (or a single image,
        /// extended with a computed chain if <see cref="DdsEncodeOptions.Mipmaps"/> asks for it).
        /// </summary>
        public static void EncodeCube(IReadOnlyList<IReadOnlyList<RawImage>> faces, Stream output, DdsEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(faces);
            options ??= new DdsEncodeOptions();
            if (faces.Count != 6) throw new ArgumentException("a cube map has six faces", nameof(faces));
            var chains = new IReadOnlyList<IReadOnlyList<RawImage>>[6];
            for (int i = 0; i < 6; i++) chains[i] = Slices(faces[i].Count == 1 ? DdsWriter.Chain(faces[i][0], options) : faces[i]);
            DdsWriter.Write(output, chains, DdsTextureKind.CubeMap, options);
        }

        public static byte[] EncodeCube(IReadOnlyList<IReadOnlyList<RawImage>> faces, DdsEncodeOptions? options = null) => ToBytes(s => EncodeCube(faces, s, options));

        /// <summary>
        /// Writes a volume (3D) texture from its slices, front to back, all of the same size and format. Written without mip
        /// levels (<see cref="DdsEncodeOptions.Mipmaps"/> is ignored). Block compression applies to each slice.
        /// </summary>
        public static void EncodeVolume(IReadOnlyList<RawImage> slices, Stream output, DdsEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(slices);
            if (slices.Count == 0) throw new ArgumentException("a volume needs at least one slice", nameof(slices));
            DdsWriter.Write(output, new[] { new[] { slices } }, DdsTextureKind.Texture3D, options ?? new DdsEncodeOptions());
        }

        public static byte[] EncodeVolume(IReadOnlyList<RawImage> slices, DdsEncodeOptions? options = null) => ToBytes(s => EncodeVolume(slices, s, options));

        static IReadOnlyList<IReadOnlyList<RawImage>> Slices(IReadOnlyList<RawImage> levels)
        {
            var r = new IReadOnlyList<RawImage>[levels.Count];
            for (int m = 0; m < r.Length; m++) r[m] = new[] { levels[m] };
            return r;
        }

        static byte[] ToBytes(Action<Stream> write)
        {
            var ms = new MemoryStream();
            write(ms);
            return ms.ToArray();
        }
    }
}
