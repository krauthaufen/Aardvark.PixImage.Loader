using System;

namespace PixCodec.Compression
{
    /// <summary>
    /// Zstandard (RFC 8878) decompression, fully managed. Supports all block types, FSE and Huffman coded
    /// literals and sequences, repeat offsets, multiple and skippable frames and the xxHash64 content checksum.
    /// Dictionaries are not supported.
    /// </summary>
    public static class Zstd
    {
        [ThreadStatic] static ZstdDecoder? cached;

        /// <summary>
        /// Decompresses one or more concatenated zstd frames (skippable frames are skipped) into a buffer of known
        /// maximum size. Returns the number of bytes written. Throws <see cref="ImageFormatException"/> on malformed
        /// input or when the output does not fit, and <see cref="ImageNotSupportedException"/> when a dictionary
        /// is required.
        /// </summary>
        public static int Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
        {
            byte[]? none = null;
            return (cached ??= new ZstdDecoder()).Decode(source, destination, ref none);
        }

        /// <summary>
        /// Decompresses one or more concatenated zstd frames of unknown decompressed size. The output grows with
        /// the data; a frame header that announces a huge size does not by itself cause a huge allocation.
        /// </summary>
        public static byte[] Decompress(ReadOnlySpan<byte> source)
        {
            byte[]? buffer = new byte[(int)Math.Clamp((long)source.Length * 4, 4096, 1 << 20)];
            int n = (cached ??= new ZstdDecoder()).Decode(source, default, ref buffer);
            if (n != buffer!.Length) Array.Resize(ref buffer, n);
            return buffer;
        }
    }
}
