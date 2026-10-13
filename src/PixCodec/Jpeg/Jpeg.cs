using System;
using System.IO;

namespace PixCodec
{
    public sealed class JpegDecodeOptions
    {

    }

    public sealed class JpegEncodeOptions
    {
        /// <summary>Quality 1..100.</summary>
        public int Quality { get; set; } = 90;
    }

    /// <summary>JPEG codec.</summary>
    public static class Jpeg
    {
        /// <summary>Returns whether the data starts with a JPEG signature (needs at most 16 bytes).</summary>
        public static bool IsMatch(ReadOnlySpan<byte> header) => throw new NotImplementedException();

        /// <summary>Reads header level information without decoding pixel data.</summary>
        public static ImageInfo GetInfo(ReadOnlySpan<byte> data) => throw new NotImplementedException();

        /// <summary>Decodes a complete JPEG file held in memory.</summary>
        public static RawImage Decode(ReadOnlySpan<byte> data, JpegDecodeOptions? options = null) => throw new NotImplementedException();

        /// <summary>Encodes an image as JPEG to the stream (which only needs to be writable, not seekable).</summary>
        public static void Encode(RawImage image, Stream output, JpegEncodeOptions? options = null) => throw new NotImplementedException();
    }
}
