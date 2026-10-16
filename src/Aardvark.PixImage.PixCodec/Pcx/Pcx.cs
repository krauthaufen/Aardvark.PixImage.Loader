using System;
using System.Collections.Generic;
using System.IO;
using Aardvark.ImageCodecs.PcxImpl;

namespace Aardvark.ImageCodecs
{
    public sealed class PcxDecodeOptions
    {
        /// <summary>Upper bound for the image in pixels; larger files are rejected with <see cref="ImageNotSupportedException"/>.</summary>
        public long MaxPixels { get; set; } = 1L << 28;

        /// <summary>
        /// An image larger than this (in pixels) is only accepted when the file holds enough data to plausibly code it; smaller
        /// truncated files decode as far as the data goes.
        /// </summary>
        public long MaxUnbackedPixels { get; set; } = 1L << 26;

        internal static readonly PcxDecodeOptions Default = new();
    }

    /// <summary>How <see cref="Pcx.Encode(RawImage, Stream, PcxEncodeOptions?)"/> stores the pixels.</summary>
    public enum PcxColorMode
    {
        /// <summary>
        /// Lossless and compact: Gray images holding only 0 and 255 as 1 bit, other Gray as 8 bit with a gray palette, Rgb with
        /// at most 256 colours as 8 bit with a palette, other Rgb as 24 bit (three planes), images with alpha as 32 bit (four
        /// planes, which ImageMagick and this library read; many older programs do not).
        /// </summary>
        Auto = 0,
        /// <summary>1 bit black and white: luma at or above 128 is white, as are pixels with alpha below 128.</summary>
        Bilevel,
        /// <summary>8 bit with a 256 colour palette; images with more colours are quantized (Wu's method, no dithering).</summary>
        Palette,
        /// <summary>24 bit (three planes); alpha is dropped.</summary>
        TrueColor,
    }

    public sealed class PcxEncodeOptions
    {
        public PcxColorMode Mode { get; set; } = PcxColorMode.Auto;
    }

    /// <summary>
    /// ZSoft PCX (PC Paintbrush) and its multi-page container DCX. Reads every version (0 to 5), RLE and uncompressed files,
    /// 1, 2, 4 and 8 bits per pixel in 1 to 4 planes (plane p holding bits p * bits of the palette index: 1 bit monochrome,
    /// CGA, EGA, packed 16 colour, 256 colour with the VGA palette at the end of the file), 8 bits in 3 planes as RGB and in
    /// 4 planes as RGBA. Palette images come out Gray when every palette entry is gray (1 bit monochrome: 0 black, 1 white;
    /// 8 bit without a VGA palette: the index as gray value), else Rgb. An alpha plane that is zero everywhere is opaque.
    /// Results are UInt8; the header's resolution is reported as DPI. DCX files give their first page from
    /// <see cref="Decode"/> and all pages from <see cref="DecodeAll"/>.
    /// Writes UInt8 Gray, GrayAlpha, Rgb and Rgba as RLE compressed version 5 files (see <see cref="PcxColorMode"/>), and
    /// DCX files with <see cref="EncodeDcx(IReadOnlyList{RawImage}, Stream, PcxEncodeOptions?)"/>.
    /// <para>PCX has no real signature: <see cref="IsMatch"/> checks that the header is plausible.</para>
    /// </summary>
    public static class Pcx
    {
        /// <summary>Whether the data starts with a plausible PCX header (or the DCX magic number, see <see cref="IsDcx"/>).</summary>
        public static bool IsMatch(ReadOnlySpan<byte> header) => PcxHeader.IsMatch(header) || IsDcx(header);

        /// <summary>Whether the data starts with the DCX magic number 987654321 (little endian).</summary>
        public static bool IsDcx(ReadOnlySpan<byte> header) => PcxDecoder.IsDcx(header);

        /// <summary>Header information of a PCX file, or of the first page of a DCX file (FrameCount = number of pages).</summary>
        public static ImageInfo GetInfo(ReadOnlySpan<byte> data, PcxDecodeOptions? options = null)
            => PcxDecoder.GetInfo(data, options ?? PcxDecodeOptions.Default);

        /// <summary>Decodes a PCX file, or the first page of a DCX file. Files that end early decode as far as the data goes.</summary>
        public static RawImage Decode(ReadOnlySpan<byte> data, PcxDecodeOptions? options = null)
            => PcxDecoder.Decode(data, options ?? PcxDecodeOptions.Default);

        /// <summary>All pages of a DCX file (a PCX file gives its one image).</summary>
        public static RawImage[] DecodeAll(ReadOnlySpan<byte> data, PcxDecodeOptions? options = null)
            => PcxDecoder.DecodeAll(data, options ?? PcxDecodeOptions.Default);

        /// <summary>Encodes UInt8 Gray, GrayAlpha, Rgb or Rgba.</summary>
        public static void Encode(RawImage image, Stream output, PcxEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            ArgumentNullException.ThrowIfNull(output);
            output.Write(PcxEncoder.Encode(image, options ?? new PcxEncodeOptions()));
        }

        public static byte[] Encode(RawImage image, PcxEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            return PcxEncoder.Encode(image, options ?? new PcxEncodeOptions());
        }

        /// <summary>Writes the images as the pages of a DCX file (at most 1023).</summary>
        public static void EncodeDcx(IReadOnlyList<RawImage> pages, Stream output, PcxEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(pages);
            ArgumentNullException.ThrowIfNull(output);
            PcxEncoder.EncodeDcx(pages, output, options ?? new PcxEncodeOptions());
        }

        public static byte[] EncodeDcx(IReadOnlyList<RawImage> pages, PcxEncodeOptions? options = null)
        {
            var ms = new MemoryStream();
            EncodeDcx(pages, ms, options);
            return ms.ToArray();
        }
    }
}
