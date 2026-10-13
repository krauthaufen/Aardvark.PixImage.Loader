using System;
using System.Buffers.Binary;

namespace PixCodec.Compression
{
    /// <summary>Integrity check stored in an .xz stream.</summary>
    public enum XzCheck { None = 0, Crc32 = 1, Crc64 = 4, Sha256 = 10 }

    /// <summary>
    /// XZ container / LZMA2 decompression (as written by liblzma, used by TIFF compression 34925), fully managed.
    /// Supports the LZMA2 filter with any number of preceding Delta filters, the CRC32, CRC64 and SHA-256 integrity
    /// checks (other check types are skipped), multiple blocks, concatenated streams and stream padding.
    /// The BCJ (executable) filters are supported as well. Also decodes the legacy ".lzma" format.
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

        /// <summary>
        /// Compresses to a single-block .xz stream that xz / liblzma read. Level 0..9 selects dictionary size and
        /// search effort (not liblzma's presets: there is no optimal parser, so expect somewhat larger output).
        /// <paramref name="deltaDistance"/> (1..256) puts a Delta filter in front of LZMA2, as TIFF writers do
        /// for predictor data; 0 = none.
        /// </summary>
        public static byte[] Compress(ReadOnlySpan<byte> source, int level = 6, int deltaDistance = 0, XzCheck check = XzCheck.Crc64)
        {
            if (deltaDistance < 0 || deltaDistance > 256) throw new ArgumentOutOfRangeException(nameof(deltaDistance));
            if (check != XzCheck.None && check != XzCheck.Crc32 && check != XzCheck.Crc64 && check != XzCheck.Sha256) throw new ArgumentOutOfRangeException(nameof(check));
            var enc = new LzmaEncoder(level, source.Length);
            var output = new byte[Math.Max(256, source.Length / 3 + 128)];
            int n = 0;
            // stream header
            Span<byte> tmp = stackalloc byte[64];
            HeaderMagic.CopyTo(tmp);
            tmp[6] = 0; tmp[7] = (byte)check;
            BinaryPrimitives.WriteUInt32LittleEndian(tmp.Slice(8), Checksums.Crc32(tmp.Slice(6, 2), 0));
            Put(ref output, ref n, tmp.Slice(0, 12));
            // block header: filters (delta), LZMA2 with the dictionary size code
            int dictCode = 0;
            while (dictCode < 40 && ((2L | (dictCode & 1)) << (dictCode / 2 + 11)) < enc.DictionarySize) dictCode++;
            int h = 1;
            tmp[h++] = (byte)(deltaDistance > 0 ? 1 : 0);
            if (deltaDistance > 0) { tmp[h++] = 0x03; tmp[h++] = 1; tmp[h++] = (byte)(deltaDistance - 1); }
            tmp[h++] = 0x21; tmp[h++] = 1; tmp[h++] = (byte)dictCode;
            while ((h & 3) != 0) tmp[h++] = 0;
            tmp[0] = (byte)((h + 4) / 4 - 1);
            BinaryPrimitives.WriteUInt32LittleEndian(tmp.Slice(h), Checksums.Crc32(tmp.Slice(0, h), 0));
            int headerSize = h + 4;
            Put(ref output, ref n, tmp.Slice(0, headerSize));
            // data
            int dataStart = n;
            if (deltaDistance > 0)
            {
                var filtered = source.ToArray();
                for (int i = filtered.Length - 1; i >= deltaDistance; i--) filtered[i] -= filtered[i - deltaDistance];
                enc.EncodeLzma2(filtered, ref output, ref n);
            }
            else enc.EncodeLzma2(source, ref output, ref n);
            long packed = n - dataStart;
            Span<byte> zeros = stackalloc byte[4];
            zeros.Clear();
            Put(ref output, ref n, zeros.Slice(0, (4 - (n - 12) % 4) % 4));
            int checkSize = CheckSizes[(int)check];
            if (check == XzCheck.Crc32) BinaryPrimitives.WriteUInt32LittleEndian(tmp, Checksums.Crc32(source, 0));
            else if (check == XzCheck.Crc64) BinaryPrimitives.WriteUInt64LittleEndian(tmp, Checksums.Crc64(source));
            else if (check == XzCheck.Sha256) Checksums.Sha256(source, tmp);
            Put(ref output, ref n, tmp.Slice(0, checkSize));
            // index: one record
            int ix = 0;
            tmp[ix++] = 0; tmp[ix++] = 1;
            WriteVarint(tmp, ref ix, (ulong)(headerSize + packed + checkSize));
            WriteVarint(tmp, ref ix, (ulong)source.Length);
            while ((ix & 3) != 0) tmp[ix++] = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(tmp.Slice(ix), Checksums.Crc32(tmp.Slice(0, ix), 0));
            int indexSize = ix + 4;
            Put(ref output, ref n, tmp.Slice(0, indexSize));
            // footer
            BinaryPrimitives.WriteUInt32LittleEndian(tmp.Slice(4), (uint)(indexSize / 4 - 1));
            tmp[8] = 0; tmp[9] = (byte)check;
            BinaryPrimitives.WriteUInt32LittleEndian(tmp, Checksums.Crc32(tmp.Slice(4, 6), 0));
            tmp[10] = (byte)'Y'; tmp[11] = (byte)'Z';
            Put(ref output, ref n, tmp.Slice(0, 12));
            Array.Resize(ref output, n);
            return output;
        }

        /// <summary>Compresses to a legacy ".lzma" file (LZMA alone, with the size in the header and no end marker).</summary>
        public static byte[] CompressLzma(ReadOnlySpan<byte> source, int level = 6) => CompressLzma(source, level, 3, 0, 2);

        // lc 0..8, lp 0..4, pb 0..4 (liblzma itself only reads lc + lp <= 4)
        internal static byte[] CompressLzma(ReadOnlySpan<byte> source, int level, int lc, int lp, int pb)
        {
            var enc = new LzmaEncoder(level, source.Length, lc, lp, pb);
            var output = new byte[Math.Max(256, source.Length / 3 + 128)];
            output[0] = enc.Properties;
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(1), (uint)enc.DictionarySize);
            BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(5), (ulong)source.Length);
            int n = 13;
            enc.EncodeLzma1(source, ref output, ref n);
            Array.Resize(ref output, n);
            return output;
        }

        static void Put(ref byte[] output, ref int length, ReadOnlySpan<byte> bytes)
        {
            if (length + bytes.Length > output.Length) Array.Resize(ref output, Math.Max(output.Length * 2, length + bytes.Length));
            bytes.CopyTo(output.AsSpan(length));
            length += bytes.Length;
        }

        static void WriteVarint(Span<byte> dst, ref int pos, ulong v)
        {
            while (v >= 0x80) { dst[pos++] = (byte)(v | 0x80); v >>= 7; }
            dst[pos++] = (byte)v;
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
            // the filters in front of LZMA2: id and parameter (delta distance or BCJ start offset)
            Span<int> filterIds = stackalloc int[3];
            Span<uint> filterArgs = stackalloc uint[3];
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
                    int filterCount = 0;
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
                        else if (id == 0x03 || BcjFilters.IsBcj(id))
                        {
                            if (i == filters - 1) throw Bad("the last filter must be LZMA2");
                            uint arg;
                            if (id == 0x03)
                            {
                                if (propSize != 1) throw Bad("invalid delta properties");
                                arg = header[hp] + 1u;
                            }
                            else
                            {
                                // optional start offset, a multiple of the instruction alignment
                                if (propSize != 0 && propSize != 4) throw Bad("invalid BCJ properties");
                                arg = propSize == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(hp)) : 0;
                                if (arg % (uint)BcjFilters.Alignment((int)id) != 0) throw Bad("invalid BCJ start offset");
                            }
                            filterIds[filterCount] = (int)id;
                            filterArgs[filterCount++] = arg;
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
                    // filters are undone in reverse order: LZMA2 first, then the others from last to first
                    for (int i = filterCount - 1; i >= 0; i--)
                    {
                        if (filterIds[i] == 0x03) UndoDelta(data, (int)filterArgs[i]);
                        else BcjFilters.Decode(filterIds[i], data, filterArgs[i]);
                    }

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
