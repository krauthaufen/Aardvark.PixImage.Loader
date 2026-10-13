using System;
using System.Buffers.Binary;

namespace PixCodec.Compression
{
    /// <summary>
    /// XZ container / LZMA2 decompression (as written by liblzma, used by TIFF compression 34925), fully managed.
    /// Supports the LZMA2 filter with any number of preceding Delta filters, the CRC32, CRC64 and SHA-256 integrity
    /// checks (other check types are skipped), multiple blocks, concatenated streams and stream padding.
    /// BCJ (executable) filters are not supported. Also decodes the legacy ".lzma" format.
    /// </summary>
    public static class Xz
    {
        [ThreadStatic] static LzmaDecoder? cached;

        static Exception Bad(string what) => new ImageFormatException("corrupt xz data: " + what);

        /// <summary>
        /// Decompresses a complete .xz stream (possibly several concatenated streams, with padding) into a buffer of
        /// known maximum size. Returns the number of bytes written. Throws <see cref="ImageFormatException"/> on
        /// malformed input or when the output does not fit.
        /// </summary>
        public static int Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
        {
            byte[]? none = null;
            return Decode(source, destination, ref none);
        }

        /// <summary>Decompresses a complete .xz stream of unknown decompressed size.</summary>
        public static byte[] Decompress(ReadOnlySpan<byte> source)
        {
            byte[]? buffer = new byte[(int)Math.Clamp((long)source.Length * 4, 4096, 1 << 20)];
            int n = Decode(source, default, ref buffer);
            if (n != buffer!.Length) Array.Resize(ref buffer, n);
            return buffer;
        }

        /// <summary>
        /// Decompresses a legacy ".lzma" file (LZMA alone: 13 byte header, then raw LZMA1 data with a known size
        /// or an end marker) into a buffer of known maximum size. Returns the number of bytes written.
        /// </summary>
        public static int DecompressLzma(ReadOnlySpan<byte> source, Span<byte> destination)
        {
            byte[]? none = null;
            return (cached ??= new LzmaDecoder()).DecodeAlone(source, destination, ref none);
        }

        /// <summary>Decompresses a legacy ".lzma" file of unknown decompressed size.</summary>
        public static byte[] DecompressLzma(ReadOnlySpan<byte> source)
        {
            byte[]? buffer = Array.Empty<byte>();
            int n = (cached ??= new LzmaDecoder()).DecodeAlone(source, default, ref buffer);
            if (n != buffer!.Length) Array.Resize(ref buffer, n);
            return buffer;
        }

        /// <summary>True if the data starts with the .xz stream magic.</summary>
        public static bool IsXz(ReadOnlySpan<byte> source) => source.Length >= 6 && source.Slice(0, 6).SequenceEqual(HeaderMagic);

        static ReadOnlySpan<byte> HeaderMagic => new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00 };

        // size in bytes of the check field for each check id
        static ReadOnlySpan<byte> CheckSizes => new byte[] { 0, 4, 4, 4, 8, 8, 8, 16, 16, 16, 32, 32, 32, 64, 64, 64 };

        // multibyte integer: 7 bits per byte, least significant group first, at most 9 bytes
        static ulong ReadVarint(ReadOnlySpan<byte> s, ref int pos, int limit)
        {
            ulong v = 0;
            for (int i = 0; i < 9; i++)
            {
                if (pos >= limit) throw Bad("truncated integer");
                byte b = s[pos++];
                v |= (ulong)(b & 0x7F) << (7 * i);
                if ((b & 0x80) == 0)
                {
                    if (b == 0 && i != 0) throw Bad("non-minimal integer");
                    return v;
                }
            }
            throw Bad("integer too long");
        }

        static int Decode(ReadOnlySpan<byte> source, Span<byte> destination, ref byte[]? growable)
        {
            var lzma = cached ??= new LzmaDecoder();
            int pos = 0, outPos = 0;
            bool any = false;
            Span<int> deltas = stackalloc int[3];
            Span<byte> digest = stackalloc byte[32];
            while (pos < source.Length)
            {
                // stream padding: zero bytes in multiples of four between or after streams
                if (any && source[pos] == 0)
                {
                    int start = pos;
                    while (pos < source.Length && source[pos] == 0) pos++;
                    if (((pos - start) & 3) != 0) throw Bad("stream padding is not a multiple of four");
                    continue;
                }
                // ---- stream header
                if (source.Length - pos < 12) throw Bad("truncated stream header");
                if (!source.Slice(pos, 6).SequenceEqual(HeaderMagic)) throw Bad("bad magic bytes");
                if (BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(pos + 8)) != Checksums.Crc32(source.Slice(pos + 6, 2), 0)) throw Bad("stream header CRC mismatch");
                if (source[pos + 6] != 0 || (source[pos + 7] & 0xF0) != 0) throw new ImageNotSupportedException("unsupported xz stream flags");
                int checkType = source[pos + 7];
                int checkSize = CheckSizes[checkType];
                pos += 12;
                any = true;

                // ---- blocks, remembered as (unpadded size, uncompressed size) to compare with the index
                ulong recordCount = 0, recordHash = 0;
                for (; ; )
                {
                    if (pos >= source.Length) throw Bad("truncated stream");
                    if (source[pos] == 0) break; // index indicator
                    int blockStart = pos;
                    int headerSize = (source[pos] + 1) * 4;
                    if (source.Length - pos < headerSize) throw Bad("truncated block header");
                    var header = source.Slice(pos, headerSize);
                    if (BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(headerSize - 4)) != Checksums.Crc32(header.Slice(0, headerSize - 4), 0)) throw Bad("block header CRC mismatch");
                    int flags = header[1];
                    if ((flags & 0x3C) != 0) throw new ImageNotSupportedException("unsupported xz block flags");
                    int filters = (flags & 3) + 1, hp = 2, hlimit = headerSize - 4;
                    ulong packedSize = ulong.MaxValue, unpackedSize = ulong.MaxValue;
                    if ((flags & 0x40) != 0) packedSize = ReadVarint(header, ref hp, hlimit);
                    if ((flags & 0x80) != 0) unpackedSize = ReadVarint(header, ref hp, hlimit);
                    int deltaCount = 0;
                    for (int i = 0; i < filters; i++)
                    {
                        ulong id = ReadVarint(header, ref hp, hlimit);
                        ulong propSize = ReadVarint(header, ref hp, hlimit);
                        if (propSize > (ulong)(hlimit - hp)) throw Bad("truncated filter properties");
                        if (id == 0x21)
                        {
                            if (i != filters - 1) throw Bad("LZMA2 must be the last filter");
                            if (propSize != 1 || header[hp] > 40) throw Bad("invalid LZMA2 properties");
                        }
                        else if (id == 0x03)
                        {
                            if (i == filters - 1) throw Bad("the last filter must be LZMA2");
                            if (propSize != 1) throw Bad("invalid delta properties");
                            deltas[deltaCount++] = header[hp] + 1;
                        }
                        else throw new ImageNotSupportedException("unsupported xz filter 0x" + id.ToString("X"));
                        hp += (int)propSize;
                    }
                    for (; hp < hlimit; hp++) if (header[hp] != 0) throw Bad("non-zero block header padding");
                    pos += headerSize;

                    if (unpackedSize != ulong.MaxValue && growable == null && unpackedSize > (ulong)(destination.Length - outPos))
                        throw Bad("output does not fit the destination");
                    int dataStart = pos, blockOut = outPos;
                    outPos = lzma.DecodeLzma2(source, ref pos, destination, ref growable, outPos);
                    ulong packed = (ulong)(pos - dataStart), unpacked = (ulong)(outPos - blockOut);
                    if (packedSize != ulong.MaxValue && packedSize != packed) throw Bad("block compressed size mismatch");
                    if (unpackedSize != ulong.MaxValue && unpackedSize != unpacked) throw Bad("block uncompressed size mismatch");
                    while (((pos - blockStart) & 3) != 0)
                    {
                        if (pos >= source.Length) throw Bad("truncated block padding");
                        if (source[pos++] != 0) throw Bad("non-zero block padding");
                    }

                    Span<byte> data = (growable != null ? growable.AsSpan() : destination).Slice(blockOut, outPos - blockOut);
                    // filters are undone in reverse order: LZMA2 first, then the deltas from last to first
                    for (int i = deltaCount - 1; i >= 0; i--) UndoDelta(data, deltas[i]);

                    if (source.Length - pos < checkSize) throw Bad("truncated block check");
                    var check = source.Slice(pos, checkSize);
                    if (checkType == 1)
                    {
                        if (BinaryPrimitives.ReadUInt32LittleEndian(check) != Checksums.Crc32(data, 0)) throw Bad("CRC32 mismatch");
                    }
                    else if (checkType == 4)
                    {
                        if (BinaryPrimitives.ReadUInt64LittleEndian(check) != Checksums.Crc64(data)) throw Bad("CRC64 mismatch");
                    }
                    else if (checkType == 10)
                    {
                        Checksums.Sha256(data, digest);
                        if (!digest.SequenceEqual(check)) throw Bad("SHA-256 mismatch");
                    }
                    pos += checkSize;

                    recordCount++;
                    recordHash = Mix(Mix(recordHash, (ulong)headerSize + packed + (ulong)checkSize), unpacked);
                }

                // ---- index
                int indexStart = pos;
                pos++;
                ulong count = ReadVarint(source, ref pos, source.Length);
                if (count != recordCount) throw Bad("index does not match the blocks");
                ulong indexHash = 0;
                for (ulong i = 0; i < count; i++)
                {
                    ulong unpadded = ReadVarint(source, ref pos, source.Length);
                    ulong unpacked = ReadVarint(source, ref pos, source.Length);
                    indexHash = Mix(Mix(indexHash, unpadded), unpacked);
                }
                if (indexHash != recordHash) throw Bad("index does not match the blocks");
                while (((pos - indexStart) & 3) != 0)
                {
                    if (pos >= source.Length) throw Bad("truncated index");
                    if (source[pos++] != 0) throw Bad("non-zero index padding");
                }
                if (source.Length - pos < 4) throw Bad("truncated index");
                if (BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(pos)) != Checksums.Crc32(source.Slice(indexStart, pos - indexStart), 0)) throw Bad("index CRC mismatch");
                pos += 4;
                int indexSize = pos - indexStart;

                // ---- stream footer
                if (source.Length - pos < 12) throw Bad("truncated stream footer");
                var footer = source.Slice(pos, 12);
                if (footer[10] != (byte)'Y' || footer[11] != (byte)'Z') throw Bad("bad footer magic");
                if (BinaryPrimitives.ReadUInt32LittleEndian(footer) != Checksums.Crc32(footer.Slice(4, 6), 0)) throw Bad("stream footer CRC mismatch");
                if (((long)BinaryPrimitives.ReadUInt32LittleEndian(footer.Slice(4)) + 1) * 4 != indexSize) throw Bad("backward size mismatch");
                if (footer[8] != 0 || footer[9] != checkType) throw Bad("stream flags mismatch");
                pos += 12;
            }
            if (!any && source.Length > 0) throw Bad("bad magic bytes");
            return outPos;
        }

        // order sensitive combination, enough to compare the block list with the index without storing it
        static ulong Mix(ulong h, ulong v) => (h ^ v) * 0x9E3779B97F4A7C15UL + (h >> 29) + 1;

        static void UndoDelta(Span<byte> data, int distance)
        {
            for (int i = distance; i < data.Length; i++) data[i] += data[i - distance];
        }
    }
}
