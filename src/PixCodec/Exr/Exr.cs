using System;
using System.IO;

namespace PixCodec
{
    public sealed class ExrDecodeOptions
    {
        /// <summary>Convert half float channels to float32 (default). If false, all-half images are returned as Float16.</summary>
        public bool HalfAsFloat { get; set; } = true;
    }

    public sealed class ExrEncodeOptions
    {
        public ExrCompression Compression { get; set; } = ExrCompression.Zip;
    }

    /// <summary>OpenEXR codec.</summary>
    public static class Exr
    {
        /// <summary>Returns whether the data starts with a OpenEXR signature (needs at most 16 bytes).</summary>
        public static bool IsMatch(ReadOnlySpan<byte> header) => throw new NotImplementedException();

        /// <summary>Reads header level information without decoding pixel data.</summary>
        public static ImageInfo GetInfo(ReadOnlySpan<byte> data) => throw new NotImplementedException();

        /// <summary>Decodes a complete OpenEXR file held in memory.</summary>
        public static RawImage Decode(ReadOnlySpan<byte> data, ExrDecodeOptions? options = null) => throw new NotImplementedException();

        /// <summary>Encodes an image as OpenEXR to the stream (which only needs to be writable, not seekable).</summary>
        public static void Encode(RawImage image, Stream output, ExrEncodeOptions? options = null) => throw new NotImplementedException();
    }
}

namespace PixCodec
{
    public enum ExrCompression { None, Rle, Zips, Zip, Piz, Pxr24, B44, B44A }
}
