using System;
using System.IO;

namespace PixCodec
{
    public sealed class Jp2DecodeOptions
    {
    }

    public sealed class Jp2EncodeOptions
    {
        /// <summary>Reversible (5/3 wavelet, lossless) coding. Default true.</summary>
        public bool Lossless { get; set; } = true;
        /// <summary>Quality 1..100 for irreversible (9/7) coding; ignored when <see cref="Lossless"/> is set.</summary>
        public int Quality { get; set; } = 75;
        /// <summary>Write a raw codestream (.j2k / .j2c) instead of the JP2 file format.</summary>
        public bool RawCodestream { get; set; }
    }

    /// <summary>JPEG 2000 codec (ISO/IEC 15444-1 codestreams and JP2 files, plus High Throughput JPEG 2000, 15444-15).</summary>
    public static class Jp2
    {
        /// <summary>Returns whether the data starts with a JP2 signature box or a raw codestream (SOC + SIZ). Must not throw on short input.</summary>
        public static bool IsMatch(ReadOnlySpan<byte> header)
            => (header.Length >= 12 && header.Slice(0, 12).SequenceEqual(new byte[] { 0, 0, 0, 12, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A }))
            || (header.Length >= 4 && header[0] == 0xFF && header[1] == 0x4F && header[2] == 0xFF && header[3] == 0x51);

        /// <summary>Reads header level information without decoding pixel data.</summary>
        public static ImageInfo GetInfo(ReadOnlySpan<byte> data) => throw new ImageNotSupportedException("JPEG 2000 is not implemented yet");

        /// <summary>Decodes a complete JP2 file or raw codestream held in memory.</summary>
        public static RawImage Decode(ReadOnlySpan<byte> data, Jp2DecodeOptions? options = null) => throw new ImageNotSupportedException("JPEG 2000 is not implemented yet");

        /// <summary>Encodes an image as JPEG 2000 to the stream (which only needs to be writable, not seekable).</summary>
        public static void Encode(RawImage image, Stream output, Jp2EncodeOptions? options = null) => throw new ImageNotSupportedException("JPEG 2000 is not implemented yet");
    }
}
