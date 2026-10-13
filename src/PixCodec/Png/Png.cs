using System;
using System.IO;

namespace PixCodec
{
    public sealed class PngDecodeOptions
    {

    }

    public sealed class PngEncodeOptions
    {
        /// <summary>zlib level 0..9.</summary>
        public int CompressionLevel { get; set; } = 6;
    }

    /// <summary>PNG codec.</summary>
    public static class Png
    {
        /// <summary>Returns whether the data starts with a PNG signature (needs at most 16 bytes).</summary>
        public static bool IsMatch(ReadOnlySpan<byte> header) => throw new NotImplementedException();

        /// <summary>Reads header level information without decoding pixel data.</summary>
        public static ImageInfo GetInfo(ReadOnlySpan<byte> data) => throw new NotImplementedException();

        /// <summary>Decodes a complete PNG file held in memory.</summary>
        public static RawImage Decode(ReadOnlySpan<byte> data, PngDecodeOptions? options = null) => throw new NotImplementedException();

        /// <summary>Encodes an image as PNG to the stream (which only needs to be writable, not seekable).</summary>
        public static void Encode(RawImage image, Stream output, PngEncodeOptions? options = null) => throw new NotImplementedException();
    }
}
