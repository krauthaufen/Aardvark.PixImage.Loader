using System;
using System.Collections.Generic;
using System.IO;
using Aardvark.ImageCodecs.PvrImpl;

namespace Aardvark.ImageCodecs
{
    /// <summary>The channel type field of a PVR header (EPVRTVariableType).</summary>
    public enum PvrChannelType
    {
        UnsignedByteNorm = 0, SignedByteNorm = 1, UnsignedByte = 2, SignedByte = 3,
        UnsignedShortNorm = 4, SignedShortNorm = 5, UnsignedShort = 6, SignedShort = 7,
        UnsignedIntNorm = 8, SignedIntNorm = 9, UnsignedInt = 10, SignedInt = 11,
        SignedFloat = 12, UnsignedFloat = 13,
    }

    public sealed class PvrDecodeOptions
    {
        /// <summary>Divide premultiplied colours (header flag, DXT2 / DXT4) by alpha (default), since RawImage alpha is straight.</summary>
        public bool Unpremultiply { get; set; } = true;

        /// <summary>
        /// Turn images the orientation metadata marks as stored bottom-up (Y up), right to left (X left) or back to front
        /// (Z out) the right way round (default), so that the first row is the top.
        /// </summary>
        public bool ApplyOrientation { get; set; } = true;

        /// <summary>Upper bound for the pixels of all images together; larger files are rejected with <see cref="ImageNotSupportedException"/>.</summary>
        public long MaxPixels { get; set; } = 1L << 28;

        internal static readonly PvrDecodeOptions Default = new();
    }

    public sealed class PvrEncodeOptions
    {
        /// <summary>
        /// Block compression (<see cref="TextureCompression.None"/> by default): BC1 to BC7, ETC2, EAC or ASTC (the Basis Universal
        /// formats are KTX only). Uncompressed images are stored with their sample type (Gray as "l", Gray + alpha as "la").
        /// </summary>
        public TextureCompression Compression { get; set; } = TextureCompression.None;

        /// <summary>ASTC footprint (4x4 by default), see <see cref="KtxEncodeOptions.AstcBlockWidth"/>.</summary>
        public int AstcBlockWidth { get; set; } = 4;
        /// <inheritdoc cref="AstcBlockWidth"/>
        public int AstcBlockHeight { get; set; } = 4;

        /// <summary>Speed versus quality of the block compression.</summary>
        public BcQuality Quality { get; set; } = BcQuality.Normal;

        /// <summary>Whether to add a computed mip chain to single images, cube faces, array layers and volumes.</summary>
        public bool GenerateMipmaps { get; set; }

        /// <summary>Filter for computed mip levels.</summary>
        public ResampleFilter MipmapFilter { get; set; } = ResampleFilter.Box;

        /// <summary>
        /// Mark 8 bit colour as sRGB (colour space 1). Null (the default) takes it from the image's <see cref="ImageMetadata.Transfer"/>;
        /// images without one are taken as sRGB when they are UInt8 colour images.
        /// </summary>
        public bool? Srgb { get; set; }

        /// <summary>Upper bound on the threads used for block compression and mip generation, &lt;= 0 for all cores.</summary>
        public int MaxDegreeOfParallelism { get; set; }

        internal KtxEncodeOptions ToKtx() => new()
        {
            Compression = Compression, AstcBlockWidth = AstcBlockWidth, AstcBlockHeight = AstcBlockHeight, Quality = Quality,
            GenerateMipmaps = GenerateMipmaps, MipmapFilter = MipmapFilter, Srgb = Srgb, MaxDegreeOfParallelism = MaxDegreeOfParallelism,
        };
    }

    /// <summary>Header level description of a PVR file.</summary>
    public class PvrTextureInfo
    {
        /// <summary>3 for PVR v3 files, 2 for the legacy ("PVR!") header.</summary>
        public int Version { get; internal set; }
        public int Width { get; internal set; }
        public int Height { get; internal set; }
        /// <summary>Slices of a volume texture (1 otherwise).</summary>
        public int Depth { get; internal set; } = 1;
        public int MipLevels { get; internal set; } = 1;
        /// <summary>Array surfaces (1 for textures that are not arrays).</summary>
        public int Surfaces { get; internal set; } = 1;
        /// <summary>Cube faces (6 for cube maps, 1 otherwise).</summary>
        public int Faces { get; internal set; } = 1;
        /// <summary>The 64 bit pixel format of the v3 header (for legacy files the equivalent v3 value).</summary>
        public ulong PixelFormat { get; internal set; }
        /// <summary>The stored format, e.g. "PVRTC1_4", "BC7", "ETC2_RGBA", "ASTC_6x6", "r8g8b8a8", "r5g6b5".</summary>
        public string Format { get; internal set; } = "";
        public PvrChannelType ChannelType { get; internal set; }
        public bool IsSrgb { get; internal set; }
        public bool IsCompressed { get; internal set; }
        public bool IsPremultiplied { get; internal set; }
        /// <summary>Cube face order of the metadata (e.g. "XxYyZz"), null without one. Decoded faces are in the order +X, -X, +Y, -Y, +Z, -Z when all six are present.</summary>
        public string? CubeFaceOrder { get; internal set; }
        /// <summary>Orientation metadata: X increases to the left, Y upwards, Z outwards (all false without the metadata).</summary>
        public bool XLeft { get; internal set; }
        public bool YUp { get; internal set; }
        public bool ZOut { get; internal set; }
        /// <summary>The metadata blocks: (FourCC, key, data).</summary>
        public IReadOnlyList<(uint FourCC, uint Key, byte[] Data)> Metadata { get; internal set; } = Array.Empty<(uint, uint, byte[])>();
        public TransferFunction Transfer { get; internal set; }
        /// <summary>What the decoded images hold.</summary>
        public ColorModel Color { get; internal set; }
        public SampleType SampleType { get; internal set; }
        public int Channels { get; internal set; }
    }

    /// <summary>Every image of a PVR file, decoded.</summary>
    public sealed class PvrTexture : PvrTextureInfo
    {
        // [mip][surface][face][slice]
        internal RawImage[][][][] Images = Array.Empty<RawImage[][][]>();

        /// <summary>One image: mip level, array surface, cube face (+X, -X, +Y, -Y, +Z, -Z), volume slice (slices halve with each level).</summary>
        public RawImage GetImage(int mip = 0, int surface = 0, int face = 0, int slice = 0) => Images[mip][surface][face][slice];

        /// <summary>The mip chain of one surface / face (slice 0 of a volume), largest first.</summary>
        public RawImage[] GetMipChain(int surface = 0, int face = 0)
        {
            var r = new RawImage[Images.Length];
            for (int m = 0; m < r.Length; m++) r[m] = Images[m][surface][face][0];
            return r;
        }

        /// <summary>All slices of a volume texture at one mip level.</summary>
        public RawImage[] GetSlices(int mip = 0, int surface = 0) => Images[mip][surface][0];
    }

    /// <summary>
    /// PowerVR texture containers (PVR v3, "PVR\x03", and the legacy v2 header "PVR!"): 2D and volume textures, cube maps,
    /// arrays and mip chains. Reads every uncompressed channel layout (any of r, g, b, a, l, i, x with 1 to 32 bits each, the
    /// integer, normalized and float channel types), PVRTC1 2 and 4 bpp, ETC1, ETC2 / EAC, BC1 to BC7 (DXT2 / DXT4
    /// premultiplied), ASTC (2D and 3D footprints, LDR and HDR), RGB9E5 and 1 bpp. Results are like those of <see cref="Ktx"/>
    /// (PVRTC as UInt8 Rgb / Rgba). Writes v3 files, uncompressed or with the BC, ETC2 / EAC and ASTC encoders, with given or
    /// computed mip chains, cube maps, arrays and volumes.
    /// </summary>
    public static class Pvr
    {
        /// <summary>Whether the data starts with a PVR v3 header (either byte order) or a legacy v2 header.</summary>
        public static bool IsMatch(ReadOnlySpan<byte> header) => PvrReader.Version(header) != 0;

        /// <summary>Header level information on the top image; FrameCount is surfaces times faces, MipLevels the length of the chain.</summary>
        public static ImageInfo GetInfo(ReadOnlySpan<byte> data) => PvrReader.GetInfo(data);

        /// <summary>The texture's structure and format without decoding image data.</summary>
        public static PvrTextureInfo GetTextureInfo(ReadOnlySpan<byte> data) => PvrReader.Parse(data).Info;

        /// <summary>Decodes the top mip level of the first surface / face / slice.</summary>
        public static RawImage Decode(ReadOnlySpan<byte> data, PvrDecodeOptions? options = null)
            => PvrReader.Decode(data, options ?? PvrDecodeOptions.Default, topOnly: true).GetImage();

        /// <summary>Decodes every image.</summary>
        public static PvrTexture DecodeTexture(ReadOnlySpan<byte> data, PvrDecodeOptions? options = null)
            => PvrReader.Decode(data, options ?? PvrDecodeOptions.Default, topOnly: false);

        /// <summary>The mip chain of the first surface / face (slice 0 of a volume), largest first.</summary>
        public static RawImage[] DecodeMipmaps(ReadOnlySpan<byte> data, PvrDecodeOptions? options = null)
            => DecodeTexture(data, options).GetMipChain();

        /// <summary>The top mip level of every surface and face (every slice of a volume texture).</summary>
        public static RawImage[] DecodeAll(ReadOnlySpan<byte> data, PvrDecodeOptions? options = null)
        {
            var t = PvrReader.Decode(data, options ?? PvrDecodeOptions.Default, topOnly: true);
            var r = new List<RawImage>();
            for (int s = 0; s < t.Surfaces; s++)
                for (int f = 0; f < t.Faces; f++)
                    r.AddRange(t.Images[0][s][f]);
            return r.ToArray();
        }

        /// <summary>Writes one image (up to 4 channels; UInt8, Int8, UInt16, Int16, UInt32, Int32, Float16 or Float32), with a computed mip chain if the options ask for one.</summary>
        public static void Encode(RawImage image, Stream output, PvrEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            options ??= new PvrEncodeOptions();
            PvrWriter.Write(output, new[] { new[] { Chain(image, options) } }, 1, options);
        }

        public static byte[] Encode(RawImage image, PvrEncodeOptions? options = null) => ToBytes(s => Encode(image, s, options));

        /// <summary>Writes a given mip chain (largest first; each level half the previous size, rounded down, at least 1).</summary>
        public static void EncodeMipmaps(IReadOnlyList<RawImage> levels, Stream output, PvrEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(levels);
            PvrWriter.Write(output, new[] { new[] { levels } }, 1, options ?? new PvrEncodeOptions());
        }

        public static byte[] EncodeMipmaps(IReadOnlyList<RawImage> levels, PvrEncodeOptions? options = null) => ToBytes(s => EncodeMipmaps(levels, s, options));

        /// <summary>Writes a cube map from six square faces in the order +X, -X, +Y, -Y, +Z, -Z, each a mip chain (or a single image, extended if the options ask for it).</summary>
        public static void EncodeCube(IReadOnlyList<IReadOnlyList<RawImage>> faces, Stream output, PvrEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(faces);
            options ??= new PvrEncodeOptions();
            if (faces.Count != 6) throw new ArgumentException("a cube map has six faces", nameof(faces));
            var f = new IReadOnlyList<RawImage>[6];
            for (int i = 0; i < 6; i++) f[i] = faces[i].Count == 1 ? Chain(faces[i][0], options) : faces[i];
            if (f[0][0].Width != f[0][0].Height) throw new ArgumentException("PVR: cube faces must be square");
            PvrWriter.Write(output, new[] { f }, 1, options);
        }

        public static byte[] EncodeCube(IReadOnlyList<IReadOnlyList<RawImage>> faces, PvrEncodeOptions? options = null) => ToBytes(s => EncodeCube(faces, s, options));

        /// <summary>Writes a texture array: one mip chain (or single image, extended if the options ask for it) per surface, all of the same size and format.</summary>
        public static void EncodeArray(IReadOnlyList<IReadOnlyList<RawImage>> surfaces, Stream output, PvrEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(surfaces);
            options ??= new PvrEncodeOptions();
            if (surfaces.Count == 0) throw new ArgumentException("an array needs at least one surface", nameof(surfaces));
            var l = new IReadOnlyList<RawImage>[surfaces.Count][];
            for (int i = 0; i < l.Length; i++) l[i] = new[] { surfaces[i].Count == 1 ? Chain(surfaces[i][0], options) : surfaces[i] };
            PvrWriter.Write(output, l, 1, options);
        }

        public static byte[] EncodeArray(IReadOnlyList<IReadOnlyList<RawImage>> surfaces, PvrEncodeOptions? options = null) => ToBytes(s => EncodeArray(surfaces, s, options));

        /// <summary>
        /// Writes a volume (3D) texture from its slices, front to back, all of the same size and format; with
        /// <see cref="PvrEncodeOptions.GenerateMipmaps"/> a 3D mip chain is computed. Block compression applies to each slice.
        /// </summary>
        public static void EncodeVolume(IReadOnlyList<RawImage> slices, Stream output, PvrEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(slices);
            options ??= new PvrEncodeOptions();
            if (slices.Count == 0) throw new ArgumentException("a volume needs at least one slice", nameof(slices));
            PvrWriter.WriteVolume(output, slices, options);
        }

        public static byte[] EncodeVolume(IReadOnlyList<RawImage> slices, PvrEncodeOptions? options = null) => ToBytes(s => EncodeVolume(slices, s, options));

        static IReadOnlyList<RawImage> Chain(RawImage image, PvrEncodeOptions o) => KtxImpl.KtxWriter.Chain(image, o.ToKtx());

        static byte[] ToBytes(Action<Stream> write)
        {
            var ms = new MemoryStream();
            write(ms);
            return ms.ToArray();
        }
    }
}
