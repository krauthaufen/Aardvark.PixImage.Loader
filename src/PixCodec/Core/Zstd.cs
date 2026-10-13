using System;

namespace PixCodec.Compression
{
    /// <summary>
    /// Zstandard (RFC 8878), fully managed. The decoder supports all block types, FSE and Huffman coded literals
    /// and sequences, repeat offsets, multiple and skippable frames and the xxHash64 content checksum; dictionaries
    /// are not supported. The compressor is a simple fast one with a single setting (see <see cref="Compress"/>).
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

        /// <summary>
        /// Compresses to a single zstd frame (with the content size in its header) that any zstd decoder reads.
        /// This is a simple fast compressor - greedy matching, Huffman literals, FSE sequences, raw/RLE block
        /// fallback - with one fixed speed/ratio trade-off; it is not a replacement for the reference
        /// implementation's higher levels. <paramref name="checksum"/> appends the xxHash64 content checksum.
        /// </summary>
        public static byte[] Compress(ReadOnlySpan<byte> source, bool checksum = false) => ZstdEncoder.Compress(source, checksum);
    }
}
