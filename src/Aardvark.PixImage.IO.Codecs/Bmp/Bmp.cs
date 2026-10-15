using System;
using System.IO;
using Aardvark.ImageCodecs.BmpImpl;

namespace Aardvark.ImageCodecs
{
    public sealed class BmpDecodeOptions
    {
        /// <summary>
        /// 32 bit images without an alpha mask (BI_RGB) have a fourth byte that the format declares unused. Some writers put
        /// alpha there anyway; set this to read it as alpha (the result is then Rgba). An all zero fourth byte is taken as opaque.
        /// </summary>
        public bool AlphaFromReservedByte { get; set; }

        /// <summary>Upper bound for the image in pixels; larger files are rejected with <see cref="ImageNotSupportedException"/>.</summary>
        public long MaxPixels { get; set; } = 1L << 28;

        /// <summary>
        /// An uncompressed image larger than this (in pixels) is only accepted when the file holds its pixel data; smaller
        /// truncated files decode as far as the data goes. Keeps a tiny damaged header from allocating gigabytes.
        /// </summary>
        public long MaxUnbackedPixels { get; set; } = 1L << 26;

        internal static readonly BmpDecodeOptions Default = new();
    }

    public sealed class BmpEncodeOptions
    {
        /// <summary>Store the rows top to bottom (negative height). Off by default: bottom-up files are what every reader expects.</summary>
        public bool TopDown { get; set; }

        /// <summary>Write the resolution and an ICC profile from <see cref="RawImage.Metadata"/> (default).</summary>
        public bool WriteMetadata { get; set; } = true;
    }

    /// <summary>
    /// Windows / OS/2 bitmap (BMP, DIB). Reads every header version (OS/2 1.x and 2.x, BITMAPINFOHEADER, V2 to V5), 1, 2, 4,
    /// 8, 16, 24 and 32 bit pixels, palettes, bit fields of any width (scaled to 8 bits rounding to nearest; more than 8 bits
    /// per field give UInt16 samples), alpha
    /// masks, RLE4 / RLE8 / OS/2 RLE24 (pixels a run skips are transparent, as browsers show them), embedded JPEG and PNG,
    /// top-down files, embedded ICC profiles and the resolution. Palette images whose colours are all gray decode as Gray.
    /// Writes UInt8 Gray (8 bit palette), Rgb (24 bit), Rgba and GrayAlpha (32 bit with an alpha mask).
    /// <para>Not supported: OS/2 Huffman 1D compression, 64 bit pixels, OS/2 icon / pointer / array files.</para>
    /// </summary>
    public static class Bmp
    {
        /// <summary>Returns whether the data starts like a BMP file ("BM" and a known header size).</summary>
        public static bool IsMatch(ReadOnlySpan<byte> header) => BmpHeader.IsMatch(header);

        /// <summary>
        /// Reads header level information without decoding pixel data. For RLE compressed files the run codes are walked
        /// (without writing pixels) to find out whether pixels are left out, which makes the result Rgba.
        /// </summary>
        public static ImageInfo GetInfo(ReadOnlySpan<byte> data) => GetInfo(data, null);

        /// <inheritdoc cref="GetInfo(ReadOnlySpan{byte})"/>
        public static ImageInfo GetInfo(ReadOnlySpan<byte> data, BmpDecodeOptions? options)
            => BmpDecoder.GetInfo(data, options ?? BmpDecodeOptions.Default);

        /// <summary>Decodes a complete BMP file held in memory. Files that end early decode as far as the data goes.</summary>
        public static RawImage Decode(ReadOnlySpan<byte> data, BmpDecodeOptions? options = null)
            => BmpDecoder.Decode(data, options ?? BmpDecodeOptions.Default);

        /// <summary>
        /// Decodes a DIB as found in clipboard data and in ICO / CUR files: the info header without the 14 byte file header,
        /// palette and pixels following directly.
        /// </summary>
        public static RawImage DecodeDib(ReadOnlySpan<byte> dib, BmpDecodeOptions? options = null)
            => BmpDecoder.Decode(dib, options ?? BmpDecodeOptions.Default, dibOnly: true);

        /// <summary>Encodes UInt8 Gray, GrayAlpha, Rgb or Rgba.</summary>
        public static void Encode(RawImage image, Stream output, BmpEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            ArgumentNullException.ThrowIfNull(output);
            BmpEncoder.Encode(image, output, options ?? new BmpEncodeOptions());
        }

        public static byte[] Encode(RawImage image, BmpEncodeOptions? options = null)
        {
            var ms = new MemoryStream();
            Encode(image, ms, options);
            return ms.ToArray();
        }

        /// <summary>Whether <see cref="GetInfo(ReadOnlySpan{byte})"/> needs the whole file (RLE) rather than only the headers.</summary>
        internal static bool InfoNeedsAllData(ReadOnlySpan<byte> prefix) => BmpHeader.InfoNeedsAllData(prefix);
    }
}
