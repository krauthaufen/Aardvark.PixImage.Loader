using System;
using System.IO;

namespace PixCodec
{
    public sealed class PngDecodeOptions
    {
        /// <summary>
        /// Verify the CRC of critical chunks (IHDR, PLTE, IDAT, IEND) and drop ancillary chunks whose CRC is wrong.
        /// Turning it off saves one pass over the compressed data.
        /// </summary>
        public bool VerifyChecksums { get; set; } = true;

        /// <summary>
        /// When set, a tRNS chunk is ignored: gray/RGB images keep their channel count and palette images decode
        /// to Rgb. The default (false) expands transparency into an alpha channel.
        /// </summary>
        public bool IgnoreTransparency { get; set; }

        /// <summary>
        /// When set, palette images are returned as their raw indices (UInt8 Gray, one index per pixel, not scaled)
        /// instead of being expanded to Rgb/Rgba. Fetch the colors with <see cref="Png.ReadPalette"/>.
        /// Has no effect on other color types.
        /// </summary>
        public bool KeepIndexed { get; set; }
    }

    /// <summary>Row filter used by the PNG encoder.</summary>
    public enum PngFilterMode : byte
    {
        /// <summary><see cref="Adaptive"/> for 8/16 bit gray and true color data, <see cref="None"/> for palette output.</summary>
        Auto = 0,
        None,
        Sub,
        Up,
        Average,
        Paeth,
        /// <summary>Per row, the filter with the smallest sum of absolute (signed) residuals.</summary>
        Adaptive,
    }

    public sealed class PngEncodeOptions
    {
        /// <summary>zlib level 0..9.</summary>
        public int CompressionLevel { get; set; } = 6;

        /// <summary>Row filter strategy. Fixed filters are faster to encode, <see cref="PngFilterMode.Adaptive"/> usually compresses best.</summary>
        public PngFilterMode Filter { get; set; } = PngFilterMode.Auto;

        /// <summary>
        /// Write a palette (color type 3) image when an 8 bit Rgb/Rgba image has at most 256 distinct colors.
        /// Lossless: decodes back to the same pixels and color model.
        /// </summary>
        public bool AutoPalette { get; set; }

        /// <summary>Write gAMA / pHYs / iCCP / eXIf chunks from <see cref="RawImage.Metadata"/>.</summary>
        public bool WriteMetadata { get; set; } = true;
    }

    /// <summary>PNG codec.</summary>
    public static class Png
    {
        /// <summary>Returns whether the data starts with a PNG signature (needs at most 16 bytes).</summary>
        public static bool IsMatch(ReadOnlySpan<byte> header)
            => header.Length >= 8 && header.Slice(0, 8).SequenceEqual(PngDecoder.Signature);

        /// <summary>Reads header level information without decoding pixel data.</summary>
        public static ImageInfo GetInfo(ReadOnlySpan<byte> data) => PngDecoder.GetInfo(data);

        /// <summary>Decodes a complete PNG file held in memory.</summary>
        public static RawImage Decode(ReadOnlySpan<byte> data, PngDecodeOptions? options = null)
            => PngDecoder.Decode(data, options ?? DefaultDecode);

        /// <summary>
        /// Returns the palette of a color type 3 PNG as RGBA quadruples (4 bytes per entry, alpha from tRNS or 255),
        /// or null when the image has no palette. Reads only the chunks in front of the pixel data.
        /// </summary>
        public static byte[]? ReadPalette(ReadOnlySpan<byte> data) => PngDecoder.ReadPalette(data);

        /// <summary>Encodes an image as PNG to the stream (which only needs to be writable, not seekable).</summary>
        public static void Encode(RawImage image, Stream output, PngEncodeOptions? options = null)
            => PngEncoder.Encode(image, output, options ?? DefaultEncode);

        /// <summary>Encodes an image as PNG into a new array.</summary>
        public static byte[] Encode(RawImage image, PngEncodeOptions? options = null)
        {
            using var ms = new MemoryStream();
            PngEncoder.Encode(image, ms, options ?? DefaultEncode);
            return ms.ToArray();
        }

        static readonly PngDecodeOptions DefaultDecode = new();
        static readonly PngEncodeOptions DefaultEncode = new();
    }
}
