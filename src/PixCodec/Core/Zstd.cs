using System;

namespace PixCodec.Compression
{
    /// <summary>Zstandard (RFC 8878) decompression.</summary>
    public static class Zstd
    {
        /// <summary>
        /// Decompresses one or more concatenated zstd frames (skippable frames are skipped) into a buffer of known
        /// maximum size. Returns the number of bytes written. Throws <see cref="ImageFormatException"/> on malformed
        /// input or when the output does not fit.
        /// </summary>
        public static int Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
            => throw new ImageNotSupportedException("zstd is not implemented yet");
    }
}
