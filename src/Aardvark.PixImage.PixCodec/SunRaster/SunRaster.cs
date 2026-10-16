using System;
using System.IO;
using Aardvark.ImageCodecs.SunRasterImpl;

namespace Aardvark.ImageCodecs
{
    public sealed class SunRasterDecodeOptions
    {
        /// <summary>Upper bound for the image in pixels; larger files are rejected with <see cref="ImageNotSupportedException"/>.</summary>
        public long MaxPixels { get; set; } = 1L << 28;

        /// <summary>
        /// An image larger than this (in pixels) is only accepted when the file holds enough data to plausibly code it; smaller
        /// truncated files decode as far as the data goes.
        /// </summary>
        public long MaxUnbackedPixels { get; set; } = 1L << 26;

        internal static readonly SunRasterDecodeOptions Default = new();
    }

    public sealed class SunRasterEncodeOptions
    {
        /// <summary>Run length encode the data (RT_BYTE_ENCODED). Off by default: RT_STANDARD is what every reader handles.</summary>
        public bool Rle { get; set; }

        /// <summary>Store 24 / 32 bit pixels in R, G, B order (RT_FORMAT_RGB) instead of the standard B, G, R. Ignored with <see cref="Rle"/>.</summary>
        public bool RgbOrder { get; set; }

        /// <summary>
        /// Write 8 bit indices with a colour map (Rgb images with more than 256 colours are quantized, Wu's method without
        /// dithering) instead of 24 / 32 bit pixels; alpha is dropped. Gray images are always 8 bit without a map.
        /// </summary>
        public bool Palette { get; set; }

        /// <summary>Write 1 bit black and white: luma at or above 128 is white, as are pixels with alpha below 128.</summary>
        public bool Bilevel { get; set; }
    }

    /// <summary>
    /// Sun Raster (.ras, .sun, .im1 / .im8 / .im24 / .im32): 1, 8, 24 and 32 bit images of type RT_OLD, RT_STANDARD,
    /// RT_BYTE_ENCODED (RLE) and RT_FORMAT_RGB, without colour map, with an RMT_EQUAL_RGB colour map (indices outside it
    /// are black) or with an RMT_RAW one (ignored). 1 bit images without a map are 1 = black, 0 = white; with a map the
    /// bit indexes it. 8 bit images without a map are gray. Indexed images whose map is all gray come out Gray, else Rgb.
    /// 24 bit pixels are B, G, R (R, G, B for RT_FORMAT_RGB); 32 bit ones have a leading byte that is taken as alpha (as
    /// ImageMagick writes it) unless it is zero everywhere. Results are UInt8.
    /// Writes UInt8 Gray (8 bit, or 1 bit with <see cref="SunRasterEncodeOptions.Bilevel"/>), Rgb (24 bit, or 8 bit with a
    /// colour map) and GrayAlpha / Rgba (32 bit), optionally RLE.
    /// </summary>
    public static class SunRaster
    {
        /// <summary>Whether the data starts with the Sun Raster magic number 0x59A66A95.</summary>
        public static bool IsMatch(ReadOnlySpan<byte> header) => SunHeader.IsMatch(header);

        public static ImageInfo GetInfo(ReadOnlySpan<byte> data) => SunCodec.GetInfo(data);

        /// <summary>Decodes a Sun Raster file. Files that end early decode as far as the data goes.</summary>
        public static RawImage Decode(ReadOnlySpan<byte> data, SunRasterDecodeOptions? options = null)
            => SunCodec.Decode(data, options ?? SunRasterDecodeOptions.Default);

        /// <summary>Encodes UInt8 Gray, GrayAlpha, Rgb or Rgba.</summary>
        public static void Encode(RawImage image, Stream output, SunRasterEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            ArgumentNullException.ThrowIfNull(output);
            output.Write(SunCodec.Encode(image, options ?? new SunRasterEncodeOptions()));
        }

        public static byte[] Encode(RawImage image, SunRasterEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            return SunCodec.Encode(image, options ?? new SunRasterEncodeOptions());
        }
    }
}
