using System;
using System.IO;

namespace PixCodec
{
    public sealed class WebpDecodeOptions
    {
    }

    public sealed class WebpEncodeOptions
    {
        /// <summary>Quality 0..100 for lossy output.</summary>
        public int Quality { get; set; } = 75;
        /// <summary>Write lossless (VP8L) instead of lossy (VP8).</summary>
        public bool Lossless { get; set; }
    }

    /// <summary>WebP codec.</summary>
    public static class Webp
    {
        /// <summary>Returns whether the data starts with a WebP signature (RIFF....WEBP, needs 12 bytes). Must not throw on short input.</summary>
        public static bool IsMatch(ReadOnlySpan<byte> header)
            => header.Length >= 12 && header.Slice(0, 4).SequenceEqual("RIFF"u8) && header.Slice(8, 4).SequenceEqual("WEBP"u8);

        /// <summary>Reads header level information without decoding pixel data.</summary>
        public static ImageInfo GetInfo(ReadOnlySpan<byte> data) => throw new ImageNotSupportedException("WebP is not implemented yet");

        /// <summary>Decodes a complete WebP file held in memory (first frame of an animation, composited).</summary>
        public static RawImage Decode(ReadOnlySpan<byte> data, WebpDecodeOptions? options = null) => throw new ImageNotSupportedException("WebP is not implemented yet");

        /// <summary>Encodes an image as WebP to the stream (which only needs to be writable, not seekable).</summary>
        public static void Encode(RawImage image, Stream output, WebpEncodeOptions? options = null) => throw new ImageNotSupportedException("WebP is not implemented yet");
    }
}
