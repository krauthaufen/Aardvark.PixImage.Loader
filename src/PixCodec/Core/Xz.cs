using System;

namespace PixCodec.Compression
{
    /// <summary>XZ container / LZMA2 decompression (as written by liblzma, used by TIFF compression 34925).</summary>
    public static class Xz
    {
        /// <summary>
        /// Decompresses a complete .xz stream (possibly several concatenated streams, with padding) into a buffer of
        /// known maximum size. Returns the number of bytes written. Throws <see cref="ImageFormatException"/> on
        /// malformed input or when the output does not fit.
        /// </summary>
        public static int Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
            => throw new ImageNotSupportedException("xz is not implemented yet");
    }
}
