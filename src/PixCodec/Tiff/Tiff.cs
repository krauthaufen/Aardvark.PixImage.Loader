using System;
using System.IO;

namespace PixCodec
{
    public sealed class TiffDecodeOptions
    {

    }

    public sealed class TiffEncodeOptions
    {
        public TiffCompression Compression { get; set; } = TiffCompression.Deflate;
    }

    /// <summary>TIFF codec.</summary>
    public static class Tiff
    {
        /// <summary>Returns whether the data starts with a TIFF signature (needs at most 16 bytes).</summary>
        public static bool IsMatch(ReadOnlySpan<byte> header) => throw new NotImplementedException();

        /// <summary>Reads header level information without decoding pixel data.</summary>
        public static ImageInfo GetInfo(ReadOnlySpan<byte> data) => throw new NotImplementedException();

        /// <summary>Decodes a complete TIFF file held in memory.</summary>
        public static RawImage Decode(ReadOnlySpan<byte> data, TiffDecodeOptions? options = null) => throw new NotImplementedException();

        /// <summary>Encodes an image as TIFF to the stream (which only needs to be writable, not seekable).</summary>
        public static void Encode(RawImage image, Stream output, TiffEncodeOptions? options = null) => throw new NotImplementedException();
    }
}

namespace PixCodec
{
    public enum TiffCompression { None, Lzw, Deflate, PackBits }
}
