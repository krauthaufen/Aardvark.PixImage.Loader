using System;
using System.Collections.Generic;
using System.IO;
using Aardvark.ImageCodecs.KtxImpl;

namespace Aardvark.ImageCodecs
{
    public enum KtxTextureKind { Texture1D, Texture2D, Texture3D, CubeMap }

    /// <summary>KTX 2 supercompression schemes.</summary>
    public enum KtxSupercompression { None = 0, BasisLZ = 1, Zstd = 2, Zlib = 3 }

    /// <summary>Block compression of written textures.</summary>
    public enum TextureCompression
    {
        /// <summary>Uncompressed; the format follows the image's sample type and colour model.</summary>
        None,
        /// <summary>RGB with optional 1 bit alpha, 4 bits per pixel.</summary>
        BC1,
        /// <summary>RGB with 4 bit explicit alpha, 8 bits per pixel.</summary>
        BC2,
        /// <summary>RGB with interpolated alpha, 8 bits per pixel.</summary>
        BC3,
        /// <summary>One channel, 4 bits per pixel (SNORM for signed integer images).</summary>
        BC4,
        /// <summary>Two channels, 8 bits per pixel (SNORM for signed integer images).</summary>
        BC5,
        /// <summary>HDR RGB half floats, 8 bits per pixel (signed when any value is negative).</summary>
        BC6H,
        /// <summary>RGB or RGBA, 8 bits per pixel, the best quality of the 8 bit formats.</summary>
        BC7,
        /// <summary>
        /// Basis Universal ETC1S with BasisLZ supercompression (KTX 2 only): RGB(A) at roughly 0.3 to 3 bits per pixel,
        /// transcodable to every GPU format. Size versus quality: <see cref="KtxEncodeOptions.Etc1sQuality"/>.
        /// </summary>
        Etc1s,
        /// <summary>Basis Universal UASTC LDR 4x4 (KTX 2 only): RGB(A), 8 bits per pixel before the optional Zstandard supercompression.</summary>
        Uastc,
        /// <summary>
        /// ASTC with the footprint of <see cref="KtxEncodeOptions.AstcBlockWidth"/> x <see cref="KtxEncodeOptions.AstcBlockHeight"/>
        /// (8 bits per pixel at 4x4 down to 0.89 at 12x12): LDR (UNORM / SRGB) from integer images, HDR (SFLOAT) from float images.
        /// </summary>
        Astc,
        /// <summary>ETC2: RGB (4 bits per pixel), RGB with 1 bit alpha (images whose alpha is only 0 or 255) or RGBA (8 bits per pixel).</summary>
        Etc2,
        /// <summary>EAC R11 (one channel, 4 bits per pixel) or RG11 (two channels, 8 bits per pixel); signed for signed integer images.</summary>
        Eac,
    }

    public sealed class KtxDecodeOptions
    {
        /// <summary>Divide premultiplied colours (data format descriptor flag) by alpha (default), since RawImage alpha is straight.</summary>
        public bool Unpremultiply { get; set; } = true;

        /// <summary>
        /// Turn images stored bottom-up (KTXorientation "T=u" / "ru") the right way round, so that the first row is the top
        /// (default). Files without the key are taken as top-down, which is what KTX 2 specifies and what tools write.
        /// </summary>
        public bool ApplyOrientation { get; set; } = true;

        /// <summary>Upper bound for the pixels of all images together; larger files are rejected with <see cref="ImageNotSupportedException"/>.</summary>
        public long MaxPixels { get; set; } = 1L << 28;

        internal static readonly KtxDecodeOptions Default = new();
    }

    public sealed class KtxEncodeOptions
    {
        /// <summary>Block compression (<see cref="TextureCompression.None"/> by default). See <see cref="DdsEncodeOptions.Compression"/> for the conversions.</summary>
        public TextureCompression Compression { get; set; } = TextureCompression.None;

        /// <summary>ASTC footprint (4x4 by default): widths 4, 5, 6, 8, 10, 12 and the heights the format pairs with them.</summary>
        public int AstcBlockWidth { get; set; } = 4;

        /// <inheritdoc cref="AstcBlockWidth"/>
        public int AstcBlockHeight { get; set; } = 4;

        /// <summary>
        /// Speed versus quality of the block compression: for UASTC the modes and partitions tried, for ETC1S the codebook
        /// refinement (search widths and passes).
        /// </summary>
        public BcQuality Quality { get; set; } = BcQuality.Normal;

        /// <summary>
        /// ETC1S size versus quality, 1 to 255 (default 128, like basisu's -q): sets the endpoint and selector codebook sizes
        /// and how strongly blocks reuse their neighbours' codes.
        /// </summary>
        public int Etc1sQuality { get; set; } = 128;

        /// <summary>ETC1S: explicit endpoint codebook size (1 to 16128) instead of the one <see cref="Etc1sQuality"/> gives.</summary>
        public int? Etc1sMaxEndpoints { get; set; }

        /// <summary>ETC1S: explicit selector codebook size (1 to 16128) instead of the one <see cref="Etc1sQuality"/> gives.</summary>
        public int? Etc1sMaxSelectors { get; set; }

        /// <summary>Whether to add a computed mip chain to single images (and cube faces).</summary>
        public bool GenerateMipmaps { get; set; }

        /// <summary>Filter for computed mip levels (each level is resized from the full image; sRGB data is filtered in linear light).</summary>
        public ResampleFilter MipmapFilter { get; set; } = ResampleFilter.Box;

        /// <summary>
        /// Store 8 bit colour as sRGB (an _SRGB format). Null (the default) takes it from the image's
        /// <see cref="ImageMetadata.Transfer"/>; images without one are taken as sRGB when they are UInt8 colour images.
        /// </summary>
        public bool? Srgb { get; set; }

        /// <summary>
        /// Supercompression of the mip levels: <see cref="KtxSupercompression.Zstd"/> (default), Zlib or None. ETC1S is always
        /// written with BasisLZ (this setting is ignored), UASTC with Zstd or None.
        /// </summary>
        public KtxSupercompression Supercompression { get; set; } = KtxSupercompression.Zstd;

        /// <summary>Zstandard level (1 to 22).</summary>
        public int ZstdLevel { get; set; } = 9;

        /// <summary>Upper bound on the threads used for block compression and mip generation, &lt;= 0 for all cores.</summary>
        public int MaxDegreeOfParallelism { get; set; }

        /// <summary>Extra key / value pairs to store (KTXwriter, KTXorientation, KTXswizzle are written automatically).</summary>
        public IReadOnlyDictionary<string, byte[]>? KeyValues { get; set; }
    }

    /// <summary>Header level description of a KTX file.</summary>
    public class KtxTextureInfo
    {
        /// <summary>1 for KTX 1.1, 2 for KTX 2.0.</summary>
        public int Version { get; internal set; }
        public KtxTextureKind Kind { get; internal set; }
        /// <summary>Size of the top mip level.</summary>
        public int Width { get; internal set; }
        public int Height { get; internal set; }
        /// <summary>Slices of a volume texture (1 otherwise).</summary>
        public int Depth { get; internal set; } = 1;
        public int MipLevels { get; internal set; } = 1;
        /// <summary>Array layers (1 for textures that are not arrays).</summary>
        public int Layers { get; internal set; } = 1;
        /// <summary>6 for cube maps, 1 otherwise.</summary>
        public int Faces { get; internal set; } = 1;
        public bool IsArray { get; internal set; }
        /// <summary>The Vulkan format (KTX 2, or the equivalent of a KTX 1 OpenGL format), 0 for Basis Universal data.</summary>
        public int VkFormat { get; internal set; }
        /// <summary>KTX 1: the OpenGL internal format.</summary>
        public int GlInternalFormat { get; internal set; }
        /// <summary>The stored format, e.g. "BC7_SRGB_BLOCK", "R8G8B8A8_UNORM", "ETC1S", "UASTC".</summary>
        public string Format { get; internal set; } = "";
        public bool IsSrgb { get; internal set; }
        public bool IsBlockCompressed { get; internal set; }
        public KtxSupercompression Supercompression { get; internal set; }
        public bool IsPremultiplied { get; internal set; }
        public TransferFunction Transfer { get; internal set; }
        /// <summary>The key / value data (values without the terminating zero of strings).</summary>
        public IReadOnlyDictionary<string, byte[]> KeyValues { get; internal set; } = new Dictionary<string, byte[]>();
        /// <summary>What the decoded images hold.</summary>
        public ColorModel Color { get; internal set; }
        public SampleType SampleType { get; internal set; }
        public int Channels { get; internal set; }

        /// <summary>A key's value as a string (UTF-8, without the terminating zero), or null.</summary>
        public string? GetString(string key)
        {
            if (!KeyValues.TryGetValue(key, out var v)) return null;
            int n = v.Length;
            while (n > 0 && v[n - 1] == 0) n--;
            return System.Text.Encoding.UTF8.GetString(v, 0, n);
        }
    }

    /// <summary>Every image of a KTX file, decoded.</summary>
    public sealed class KtxTexture : KtxTextureInfo
    {
        // [mip][layer][face][slice]
        internal RawImage[][][][] Images = Array.Empty<RawImage[][][]>();

        /// <summary>One image: mip level, array layer, cube face (+X, -X, +Y, -Y, +Z, -Z), volume slice (slices halve with each level).</summary>
        public RawImage GetImage(int mip = 0, int layer = 0, int face = 0, int slice = 0) => Images[mip][layer][face][slice];

        /// <summary>The mip chain of one layer / face (slice 0 of a volume), largest first.</summary>
        public RawImage[] GetMipChain(int layer = 0, int face = 0)
        {
            var r = new RawImage[Images.Length];
            for (int m = 0; m < r.Length; m++) r[m] = Images[m][layer][face][0];
            return r;
        }

        /// <summary>All slices of a volume texture at one mip level.</summary>
        public RawImage[] GetSlices(int mip = 0, int layer = 0) => Images[mip][layer][0];
    }

    /// <summary>
    /// Khronos texture containers KTX 1.1 and KTX 2.0: 1D, 2D, 3D and cube map textures, arrays and mip chains. Reads the
    /// uncompressed Vulkan / OpenGL formats (8 to 64 bit integer and float components in any order, packed 16 and 32 bit
    /// formats, R11G11B10 and RGB9E5 float), BC1 to BC7, ETC1 / ETC2 / EAC, ASTC (LDR and HDR, 2D footprints) and the Basis
    /// Universal formats ETC1S (BasisLZ), UASTC LDR 4x4 and UASTC HDR 4x4, with Zstandard and zlib supercompression.
    /// Results are like those of <see cref="Dds"/>; ETC2 and ASTC decode to UInt8 Rgb / Rgba, EAC to UInt16 / Int16,
    /// ASTC HDR (and UASTC HDR) to Float16, ETC1S and UASTC LDR to UInt8 Rgba.
    /// Writes KTX 2 textures (single images with optional computed mip chains, given mip chains, cube maps, arrays and
    /// volumes), uncompressed, BC1 to BC7 compressed or Basis Universal ETC1S (BasisLZ, one codebook for all images) /
    /// UASTC LDR 4x4, with Zstandard supercompression and a complete data format descriptor.
    /// </summary>
    public static class Ktx
    {
        /// <summary>Whether the data starts with the KTX 1.1 or KTX 2.0 identifier.</summary>
        public static bool IsMatch(ReadOnlySpan<byte> header) => KtxReader.Version(header) != 0;

        /// <summary>Header level information on the top image; FrameCount is layers times faces, MipLevels the length of the chain.</summary>
        public static ImageInfo GetInfo(ReadOnlySpan<byte> data) => KtxReader.GetInfo(data);

        /// <summary>The texture's structure and format without decoding image data.</summary>
        public static KtxTextureInfo GetTextureInfo(ReadOnlySpan<byte> data) => KtxReader.Parse(data).Info;

        /// <summary>Decodes the top mip level of the first layer / face / slice.</summary>
        public static RawImage Decode(ReadOnlySpan<byte> data, KtxDecodeOptions? options = null)
            => KtxReader.Decode(data, options ?? KtxDecodeOptions.Default, topOnly: true).GetImage();

        /// <summary>Decodes every image.</summary>
        public static KtxTexture DecodeTexture(ReadOnlySpan<byte> data, KtxDecodeOptions? options = null)
            => KtxReader.Decode(data, options ?? KtxDecodeOptions.Default, topOnly: false);

        /// <summary>The mip chain of the first layer / face (slice 0 of a volume), largest first.</summary>
        public static RawImage[] DecodeMipmaps(ReadOnlySpan<byte> data, KtxDecodeOptions? options = null)
            => DecodeTexture(data, options).GetMipChain();

        /// <summary>The top mip level of every layer and face (every slice of a volume texture).</summary>
        public static RawImage[] DecodeAll(ReadOnlySpan<byte> data, KtxDecodeOptions? options = null)
        {
            var t = KtxReader.Decode(data, options ?? KtxDecodeOptions.Default, topOnly: true);
            var r = new List<RawImage>();
            for (int l = 0; l < t.Layers; l++)
                for (int f = 0; f < t.Faces; f++)
                    r.AddRange(t.Images[0][l][f]);
            return r.ToArray();
        }

        /// <summary>Writes one image (any colour model with up to 4 channels; UInt8, Int8, UInt16, Int16, UInt32, Int32, Float16 or Float32), with a computed mip chain if the options ask for one.</summary>
        public static void Encode(RawImage image, Stream output, KtxEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            options ??= new KtxEncodeOptions();
            KtxWriter.Write(output, KtxWriter.Layers(new[] { new[] { KtxWriter.Chain(image, options) } }), KtxTextureKind.Texture2D, false, options);
        }

        public static byte[] Encode(RawImage image, KtxEncodeOptions? options = null) => ToBytes(s => Encode(image, s, options));

        /// <summary>Writes a given mip chain (largest first; each level half the previous size, rounded down, at least 1).</summary>
        public static void EncodeMipmaps(IReadOnlyList<RawImage> levels, Stream output, KtxEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(levels);
            KtxWriter.Write(output, KtxWriter.Layers(new[] { new[] { levels } }), KtxTextureKind.Texture2D, false, options ?? new KtxEncodeOptions());
        }

        public static byte[] EncodeMipmaps(IReadOnlyList<RawImage> levels, KtxEncodeOptions? options = null) => ToBytes(s => EncodeMipmaps(levels, s, options));

        /// <summary>
        /// Writes a cube map from six square faces in the order +X, -X, +Y, -Y, +Z, -Z, each a mip chain (or a single image,
        /// extended with a computed chain if the options ask for one).
        /// </summary>
        public static void EncodeCube(IReadOnlyList<IReadOnlyList<RawImage>> faces, Stream output, KtxEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(faces);
            options ??= new KtxEncodeOptions();
            if (faces.Count != 6) throw new ArgumentException("a cube map has six faces", nameof(faces));
            var f = new IReadOnlyList<RawImage>[6];
            for (int i = 0; i < 6; i++) f[i] = faces[i].Count == 1 ? KtxWriter.Chain(faces[i][0], options) : faces[i];
            KtxWriter.Write(output, KtxWriter.Layers(new[] { f }), KtxTextureKind.CubeMap, false, options);
        }

        public static byte[] EncodeCube(IReadOnlyList<IReadOnlyList<RawImage>> faces, KtxEncodeOptions? options = null) => ToBytes(s => EncodeCube(faces, s, options));

        /// <summary>
        /// Writes a 2D array texture: one mip chain (or single image, extended if the options ask for it) per layer, all of the
        /// same size and format.
        /// </summary>
        public static void EncodeArray(IReadOnlyList<IReadOnlyList<RawImage>> layers, Stream output, KtxEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(layers);
            options ??= new KtxEncodeOptions();
            if (layers.Count == 0) throw new ArgumentException("an array needs at least one layer", nameof(layers));
            var l = new IReadOnlyList<RawImage>[layers.Count][];
            for (int i = 0; i < l.Length; i++) l[i] = new[] { layers[i].Count == 1 ? KtxWriter.Chain(layers[i][0], options) : layers[i] };
            KtxWriter.Write(output, KtxWriter.Layers(l), KtxTextureKind.Texture2D, true, options);
        }

        public static byte[] EncodeArray(IReadOnlyList<IReadOnlyList<RawImage>> layers, KtxEncodeOptions? options = null) => ToBytes(s => EncodeArray(layers, s, options));

        /// <summary>
        /// Writes a volume (3D) texture from its slices, front to back, all of the same size and format. With
        /// <see cref="KtxEncodeOptions.GenerateMipmaps"/> a 3D mip chain is computed (box filtered over 2 x 2 x 2 texels).
        /// </summary>
        public static void EncodeVolume(IReadOnlyList<RawImage> slices, Stream output, KtxEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(slices);
            options ??= new KtxEncodeOptions();
            if (slices.Count == 0) throw new ArgumentException("a volume needs at least one slice", nameof(slices));
            KtxWriter.WriteVolume(output, slices, options);
        }

        public static byte[] EncodeVolume(IReadOnlyList<RawImage> slices, KtxEncodeOptions? options = null) => ToBytes(s => EncodeVolume(slices, s, options));

        static byte[] ToBytes(Action<Stream> write)
        {
            var ms = new MemoryStream();
            write(ms);
            return ms.ToArray();
        }
    }
}
