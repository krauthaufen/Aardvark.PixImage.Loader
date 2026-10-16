using System;
using System.IO;
using Aardvark.ImageCodecs.SgiImpl;

namespace Aardvark.ImageCodecs
{
    public sealed class SgiDecodeOptions
    {
        /// <summary>Upper bound for the image in pixels; larger files are rejected with <see cref="ImageNotSupportedException"/>.</summary>
        public long MaxPixels { get; set; } = 1L << 28;

        /// <summary>
        /// An image larger than this (in pixels) is only accepted when the file holds its pixel data (verbatim) or its row
        /// tables (RLE); smaller truncated files decode as far as the data goes.
        /// </summary>
        public long MaxUnbackedPixels { get; set; } = 1L << 26;

        internal static readonly SgiDecodeOptions Default = new();
    }

    public sealed class SgiEncodeOptions
    {
        /// <summary>Run length encode the rows (storage format RLE, the default); false writes them verbatim.</summary>
        public bool Rle { get; set; } = true;

        /// <summary>The image name field of the header (at most 79 ASCII characters).</summary>
        public string? Name { get; set; }
    }

    /// <summary>
    /// SGI image format (.sgi, .rgb, .rgba, .bw, .int, .inta) as described in "The SGI Image File Format" (Paul Haeberli,
    /// version 1.0): 1, 2 and 3 dimensional images with any number of channels (1 Gray, 2 GrayAlpha, 3 Rgb, 4 Rgba, more as
    /// <see cref="ColorModel.Other"/>), 8 and 16 bits per channel (UInt8 / UInt16, the stored values, PIXMIN / PIXMAX are not
    /// applied), verbatim and RLE storage, rows stored bottom-up (the result's top row is the last row of the file). The
    /// obsolete DITHERED colour map type is decoded as R, G, B in bits 0-2, 3-5 and 6-7 of each byte (IRIS GL RGB mode);
    /// SCREEN and COLORMAP files are not images and are refused.
    /// Writes UInt8 and UInt16 images of any colour model, RLE (default) or verbatim.
    /// </summary>
    public static class Sgi
    {
        /// <summary>Whether the data starts with the SGI magic number 474 and a plausible header.</summary>
        public static bool IsMatch(ReadOnlySpan<byte> header) => SgiHeader.IsMatch(header);

        public static ImageInfo GetInfo(ReadOnlySpan<byte> data) => SgiCodec.GetInfo(data);

        /// <summary>Decodes an SGI image. Files that end early decode as far as the data goes.</summary>
        public static RawImage Decode(ReadOnlySpan<byte> data, SgiDecodeOptions? options = null)
            => SgiCodec.Decode(data, options ?? SgiDecodeOptions.Default);

        /// <summary>Encodes UInt8 or UInt16 images (up to 65535 channels).</summary>
        public static void Encode(RawImage image, Stream output, SgiEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            ArgumentNullException.ThrowIfNull(output);
            output.Write(SgiCodec.Encode(image, options ?? new SgiEncodeOptions()));
        }

        public static byte[] Encode(RawImage image, SgiEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            return SgiCodec.Encode(image, options ?? new SgiEncodeOptions());
        }
    }
}
