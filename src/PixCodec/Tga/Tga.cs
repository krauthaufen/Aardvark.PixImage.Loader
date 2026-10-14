using System;
using System.IO;
using PixCodec.TgaImpl;

namespace PixCodec
{
    public sealed class TgaDecodeOptions
    {
        /// <summary>Upper bound for the image in pixels; larger files are rejected with <see cref="ImageNotSupportedException"/>.</summary>
        public long MaxPixels { get; set; } = 1L << 28;

        /// <summary>
        /// An uncompressed image larger than this (in pixels) is only accepted when the file holds its pixel data; smaller
        /// truncated files decode as far as the data goes.
        /// </summary>
        public long MaxUnbackedPixels { get; set; } = 1L << 26;

        internal static readonly TgaDecodeOptions Default = new();
    }

    public sealed class TgaEncodeOptions
    {
        /// <summary>Run length encode the pixels (image types 10 / 11). Off by default.</summary>
        public bool Rle { get; set; }

        /// <summary>Store the rows top to bottom. Off by default: bottom-up is the classic layout every reader handles.</summary>
        public bool TopDown { get; set; }
    }

    /// <summary>
    /// Truevision TGA (TARGA). Reads colour-mapped (8 / 16 bit indices, 15 / 16 / 24 / 32 bit entries), true-colour (15, 16,
    /// 24, 32 bit) and gray (8 bit, 16 bit gray + alpha) images, uncompressed or RLE (runs may cross rows), any origin
    /// corner, and the TGA 2.0 extension area's alpha type (no alpha, premultiplied). Alpha comes from the alpha bits; an
    /// alpha channel that is zero everywhere is taken as opaque, as most viewers do. Results are UInt8.
    /// Writes UInt8 Gray, GrayAlpha, Rgb and Rgba, optionally RLE compressed, as TGA 2.0 with the alpha type recorded.
    /// <para>TGA has no signature: <see cref="IsMatch"/> checks that the header is plausible, and the format independent
    /// entry points try it last.</para>
    /// </summary>
    public static class Tga
    {
        /// <summary>Whether the data starts with a plausible TGA header (TGA has no signature).</summary>
        public static bool IsMatch(ReadOnlySpan<byte> header) => TgaHeader.IsMatch(header);

        /// <summary>Reads header level information (the header, and the TGA 2.0 footer at the end of the data).</summary>
        public static ImageInfo GetInfo(ReadOnlySpan<byte> data) => TgaDecoder.GetInfo(data);

        /// <summary>Decodes a complete TGA file held in memory. Files that end early decode as far as the data goes.</summary>
        public static RawImage Decode(ReadOnlySpan<byte> data, TgaDecodeOptions? options = null)
            => TgaDecoder.Decode(data, options ?? TgaDecodeOptions.Default);

        /// <summary>Encodes UInt8 Gray, GrayAlpha, Rgb or Rgba.</summary>
        public static void Encode(RawImage image, Stream output, TgaEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            ArgumentNullException.ThrowIfNull(output);
            TgaEncoder.Encode(image, output, options ?? new TgaEncodeOptions());
        }

        public static byte[] Encode(RawImage image, TgaEncodeOptions? options = null)
        {
            var ms = new MemoryStream();
            Encode(image, ms, options);
            return ms.ToArray();
        }
    }
}
