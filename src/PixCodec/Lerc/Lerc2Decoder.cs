using System;
using System.Runtime.CompilerServices;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace PixCodec
{
    /// <summary>The fixed header of one LERC2 band blob.</summary>
    internal struct Lerc2Header
    {
        public const int MaxVersion = 6;

        public int Version;
        public uint Checksum;
        public int Rows, Cols, Depth, ValidCount, MicroBlockSize, BlobSize, DataType;
        /// <summary>v6: number of band blobs that follow this one.</summary>
        public int BlobsMore;
        /// <summary>v6: the data contains no-data values (pixels with valid and invalid depth values).</summary>
        public bool PassNoData;
        public bool IsInt;
        public double MaxZError, ZMin, ZMax, NoData, NoDataOrig;
        /// <summary>Offset of the mask size field.</summary>
        public int Size;

        public static int SizeOf(int version) => version >= 6 ? 90 : version >= 4 ? 66 : version == 3 ? 62 : 58;

        public static bool IsMagic(ReadOnlySpan<byte> data) => data.Length >= 6 && data.Slice(0, 6).SequenceEqual("Lerc2 "u8);

        /// <summary>Parses and validates the header; the data only needs to cover the header itself.</summary>
        public static Lerc2Header Parse(ReadOnlySpan<byte> d)
        {
            if (!IsMagic(d)) throw new ImageFormatException("LERC: not a LERC2 blob");
            if (d.Length < 10) throw new ImageFormatException("LERC: header is truncated");
            var h = new Lerc2Header { Version = BinaryPrimitives.ReadInt32LittleEndian(d.Slice(6)) };
            if (h.Version < 2) throw new ImageFormatException("LERC: invalid LERC2 version " + h.Version);
            if (h.Version > MaxVersion) throw new ImageNotSupportedException("LERC: LERC2 version " + h.Version + " is not supported");
            h.Size = SizeOf(h.Version);
            if (d.Length < h.Size) throw new ImageFormatException("LERC: header is truncated");
            int p = 10;
            if (h.Version >= 3) { h.Checksum = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(p)); p += 4; }
            h.Rows = BinaryPrimitives.ReadInt32LittleEndian(d.Slice(p));
            h.Cols = BinaryPrimitives.ReadInt32LittleEndian(d.Slice(p + 4));
            p += 8;
            h.Depth = 1;
            if (h.Version >= 4) { h.Depth = BinaryPrimitives.ReadInt32LittleEndian(d.Slice(p)); p += 4; }
            h.ValidCount = BinaryPrimitives.ReadInt32LittleEndian(d.Slice(p));
            h.MicroBlockSize = BinaryPrimitives.ReadInt32LittleEndian(d.Slice(p + 4));
            h.BlobSize = BinaryPrimitives.ReadInt32LittleEndian(d.Slice(p + 8));
            h.DataType = BinaryPrimitives.ReadInt32LittleEndian(d.Slice(p + 12));
            p += 16;
            if (h.Version >= 6)
            {
                h.BlobsMore = BinaryPrimitives.ReadInt32LittleEndian(d.Slice(p));
                h.PassNoData = d[p + 4] != 0;
                h.IsInt = d[p + 5] != 0;
                p += 8;
            }
            h.MaxZError = BinaryPrimitives.ReadDoubleLittleEndian(d.Slice(p));
            h.ZMin = BinaryPrimitives.ReadDoubleLittleEndian(d.Slice(p + 8));
            h.ZMax = BinaryPrimitives.ReadDoubleLittleEndian(d.Slice(p + 16));
            p += 24;
            if (h.Version >= 6)
            {
                h.NoData = BinaryPrimitives.ReadDoubleLittleEndian(d.Slice(p));
                h.NoDataOrig = BinaryPrimitives.ReadDoubleLittleEndian(d.Slice(p + 8));
            }

            if (h.Rows <= 0 || h.Cols <= 0 || h.Depth <= 0) throw new ImageFormatException("LERC: invalid image size");
            long pixels = (long)h.Rows * h.Cols;
            if (h.ValidCount < 0 || h.ValidCount > pixels) throw new ImageFormatException("LERC: invalid number of valid pixels");
            if (h.MicroBlockSize <= 0 || h.MicroBlockSize > 32) throw new ImageFormatException("LERC: invalid block size");
            if ((uint)h.DataType > LercTypes.Double) throw new ImageFormatException("LERC: invalid data type");
            if (h.BlobSize < h.Size + 4) throw new ImageFormatException("LERC: invalid blob size");
            if (!(h.MaxZError >= 0) || h.BlobsMore < 0) throw new ImageFormatException("LERC: invalid header");
            return h;
        }
    }

    internal static unsafe class Lerc2Decoder
    {
        const int Tiles = 0, DeltaHuffman = 1, PlainHuffman = 2, FloatLossless = 3;

        /// <summary>
        /// Decodes one band blob into channels chan..chan+depth-1 of the pixel interleaved <paramref name="dst"/>
        /// (<paramref name="stride"/> samples per pixel). <paramref name="blob"/> is exactly the band's bytes. Invalid
        /// pixels are set to 0. <paramref name="mask"/> holds the previous band's mask on entry and this band's on exit.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void DecodeBand<T>(ReadOnlySpan<byte> blob, in Lerc2Header h, Span<T> dst, int stride, int chan, LercMask mask, bool verifyChecksum)
            where T : unmanaged
        {
            int rows = h.Rows, cols = h.Cols, depth = h.Depth, pixels = checked(rows * cols);
            if (verifyChecksum && h.Version >= 3 && LercBits.Fletcher32(blob.Slice(14)) != h.Checksum)
                throw new ImageFormatException("LERC: checksum mismatch");

            var stats = LercStats.Current;
            int pos = h.Size;
            ReadMask(blob, ref pos, h, mask, stats);
            byte[]? valid = mask.Bytes;
            if (stats != null) stats.Bands++;

            if (valid != null || stride != depth) ClearChannels(dst, pixels, stride, chan, depth);
            if (h.ValidCount == 0)
            {
                if (stats != null) stats.EmptyBands++;
                return;
            }

            int elem = sizeof(T);
            Span<double> zMin = depth <= 64 ? stackalloc double[depth] : new double[depth];
            Span<double> zMax = depth <= 64 ? stackalloc double[depth] : new double[depth];
            bool constant;
            if (h.ZMin == h.ZMax)
            {
                // one value everywhere: no per depth ranges follow
                zMin.Fill(h.ZMin);
                constant = true;
            }
            else if (h.Version >= 4)
            {
                if (blob.Length - pos < (long)2 * depth * elem) throw new ImageFormatException("LERC: data ranges are truncated");
                for (int m = 0; m < depth; m++) zMin[m] = LercTypes.ToDouble(MemoryMarshal.Read<T>(blob.Slice(pos + m * elem)));
                pos += depth * elem;
                for (int m = 0; m < depth; m++) zMax[m] = LercTypes.ToDouble(MemoryMarshal.Read<T>(blob.Slice(pos + m * elem)));
                pos += depth * elem;
                constant = true;
                for (int m = 0; m < depth; m++) constant &= zMin[m] == zMax[m];
            }
            else
            {
                zMin[0] = h.ZMin; zMax[0] = h.ZMax;
                constant = h.ZMin == h.ZMax;
            }

            if (constant)
            {
                if (stats != null) stats.ConstantBands++;
                for (int m = 0; m < depth; m++)
                {
                    T v = LercTypes.FromDouble<T>(zMin[m]);
                    int di = chan + m;
                    for (int k = 0; k < pixels; k++, di += stride)
                        if (valid == null || valid[k] != 0) dst[di] = v;
                }
                return;
            }

            if (pos >= blob.Length) throw new ImageFormatException("LERC: pixel data is missing");
            if (blob[pos++] != 0)
            {
                if (stats != null) stats.RawBands++;
                ReadRaw(blob, pos, h, dst, stride, chan, valid);
                return;
            }

            int mode = Tiles;
            bool tryHuffman = h.Version >= 2 && h.DataType <= LercTypes.Byte && h.MaxZError == 0.5;
            bool tryFloat = h.Version >= 6 && h.DataType >= LercTypes.Float && h.MaxZError == 0;
            if (tryHuffman || tryFloat)
            {
                if (pos >= blob.Length) throw new ImageFormatException("LERC: pixel data is missing");
                mode = blob[pos++];
                bool ok = mode == Tiles
                    || (tryHuffman && (mode == DeltaHuffman || (mode == PlainHuffman && h.Version >= 4)))
                    || (tryFloat && mode == FloatLossless);
                if (!ok) throw new ImageFormatException("LERC: invalid encoding mode " + mode);
            }

            if (stats != null)
            {
                if (mode == Tiles) { stats.TileBands++; stats.BlockSizes[h.MicroBlockSize]++; }
                else if (mode == DeltaHuffman) stats.DeltaHuffmanBands++;
                else if (mode == PlainHuffman) stats.PlainHuffmanBands++;
                else stats.FloatLosslessBands++;
            }
            if (mode == Tiles) ReadTiles(blob, pos, h, dst, stride, chan, valid, zMax, stats);
            else if (mode == FloatLossless) LercFloatLossless.Decode(blob, pos, h, dst, stride, chan, valid);
            else ReadHuffman(blob, pos, h, MemoryMarshal.AsBytes(dst), stride, chan, valid, mode == DeltaHuffman);
        }

        static void ClearChannels<T>(Span<T> dst, int pixels, int stride, int chan, int depth) where T : unmanaged
        {
            if (stride == depth) { dst.Slice(0, pixels * stride).Clear(); return; }
            for (int k = 0, di = chan; k < pixels; k++, di += stride) dst.Slice(di, depth).Clear();
        }

        static void ReadMask(ReadOnlySpan<byte> blob, ref int pos, in Lerc2Header h, LercMask mask, LercStats? stats)
        {
            int pixels = h.Rows * h.Cols;
            if (blob.Length - pos < 4) throw new ImageFormatException("LERC: mask is truncated");
            int maskBytes = BinaryPrimitives.ReadInt32LittleEndian(blob.Slice(pos));
            pos += 4;
            if (maskBytes < 0 || maskBytes > blob.Length - pos) throw new ImageFormatException("LERC: mask is truncated");
            bool partial = h.ValidCount > 0 && h.ValidCount < pixels;
            if (!partial)
            {
                if (maskBytes != 0) throw new ImageFormatException("LERC: unexpected mask data");
                mask.Bytes = h.ValidCount == 0 ? new byte[pixels] : null;
                mask.ValidCount = h.ValidCount;
                return;
            }
            if (maskBytes == 0)
            {
                // a later band of a multi-band blob: the mask is the previous band's
                if (mask.Bytes == null || mask.ValidCount != h.ValidCount) throw new ImageFormatException("LERC: mask is missing");
                if (stats != null) stats.MaskReused++;
                return;
            }
            int planeBytes = (pixels + 7) >> 3;
            // each 3 byte run expands to at most 32767 bytes: reject a mask that cannot be that large before allocating
            if (planeBytes > ((long)maskBytes / 3 + 1) * 32767) throw new ImageFormatException("LERC: mask data is too short");
            byte[] plane = new byte[planeBytes];
            LercBits.DecodeMaskRle(blob.Slice(pos, maskBytes), plane);
            pos += maskBytes;
            byte[] bytes = new byte[pixels];
            if (LercBits.ExpandMask(plane, bytes) != h.ValidCount) throw new ImageFormatException("LERC: mask does not match the number of valid pixels");
            mask.Bytes = bytes;
            mask.ValidCount = h.ValidCount;
            if (stats != null) stats.MaskStored++;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void ReadRaw<T>(ReadOnlySpan<byte> blob, int pos, in Lerc2Header h, Span<T> dst, int stride, int chan, byte[]? valid)
            where T : unmanaged
        {
            int pixels = h.Rows * h.Cols, depth = h.Depth;
            long need = (long)h.ValidCount * depth * sizeof(T);
            if (blob.Length - pos < need) throw new ImageFormatException("LERC: raw pixel data is truncated");
            var src = MemoryMarshal.Cast<byte, T>(blob.Slice(pos, (int)need));
            if (valid == null && stride == depth) { src.CopyTo(dst); return; }
            int si = 0;
            for (int k = 0, di = chan; k < pixels; k++, di += stride)
            {
                if (valid != null && valid[k] == 0) continue;
                src.Slice(si, depth).CopyTo(dst.Slice(di, depth));
                si += depth;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void ReadTiles<T>(ReadOnlySpan<byte> blob, int pos, in Lerc2Header h, Span<T> dst, int stride, int chan, byte[]? valid, ReadOnlySpan<double> zMax, LercStats? stats)
            where T : unmanaged
        {
            int rows = h.Rows, cols = h.Cols, depth = h.Depth, mb = h.MicroBlockSize, version = h.Version;
            long tiles = (long)((rows + mb - 1) / mb) * ((cols + mb - 1) / mb) * depth;
            if (blob.Length - pos < tiles) throw new ImageFormatException("LERC: pixel data is truncated");
            int elem = sizeof(T), dt = h.DataType;
            double invScale = 2 * h.MaxZError;
            int checkMask = version >= 5 ? 14 : 15;
            uint* q = stackalloc uint[mb * mb];
            var qs = new Span<uint>(q, mb * mb);

            fixed (byte* src = blob)
            fixed (T* d = dst)
            fixed (byte* vm = valid)
            {
                for (int i0 = 0; i0 < rows; i0 += mb)
                {
                    int th = Math.Min(mb, rows - i0);
                    for (int j0 = 0; j0 < cols; j0 += mb)
                    {
                        int tw = Math.Min(mb, cols - j0);
                        int count = tw * th;
                        if (vm != null)
                        {
                            count = 0;
                            for (int r = 0; r < th; r++)
                            {
                                byte* row = vm + (long)(i0 + r) * cols + j0;
                                for (int c = 0; c < tw; c++) count += row[c];
                            }
                        }

                        for (int m = 0; m < depth; m++)
                        {
                            if (pos >= blob.Length) throw new ImageFormatException("LERC: pixel data is truncated");
                            int flag = src[pos++];
                            if (((flag >> 2) & checkMask) != ((j0 >> 3) & checkMask)) throw new ImageFormatException("LERC: block header check failed");
                            bool diff = version >= 5 && (flag & 4) != 0;
                            if (diff && m == 0) throw new ImageFormatException("LERC: first depth block cannot be relative");
                            int kind = flag & 3, typeCode = flag >> 6;
                            if (stats != null)
                            {
                                stats.Blocks[kind]++;
                                if (diff) stats.RelativeBlocks++;
                            }
                            T* tile = d + ((long)i0 * cols + j0) * stride + chan + m;
                            long rowStep = (long)cols * stride;
                            byte* tmask = vm == null ? null : vm + (long)i0 * cols + j0;

                            if (kind == 2)
                            {
                                // all 0, or all equal to the previous depth value
                                for (int r = 0; r < th; r++)
                                {
                                    T* p = tile + r * rowStep;
                                    byte* pm = tmask == null ? null : tmask + (long)r * cols;
                                    for (int c = 0; c < tw; c++, p += stride)
                                        if (pm == null || pm[c] != 0) *p = diff ? p[-1] : default;
                                }
                                continue;
                            }
                            if (kind == 0)
                            {
                                if (diff) throw new ImageFormatException("LERC: raw block cannot be relative");
                                if (blob.Length - pos < (long)count * elem) throw new ImageFormatException("LERC: pixel data is truncated");
                                T* s = (T*)(src + pos);
                                for (int r = 0; r < th; r++)
                                {
                                    T* p = tile + r * rowStep;
                                    byte* pm = tmask == null ? null : tmask + (long)r * cols;
                                    for (int c = 0; c < tw; c++, p += stride)
                                        if (pm == null || pm[c] != 0) *p = *s++; // x64/arm64 read unaligned
                                }
                                pos += count * elem;
                                continue;
                            }

                            int used = LercTypes.ReducedType(diff && dt < LercTypes.Float ? LercTypes.Int : dt, typeCode);
                            if (used < 0) throw new ImageFormatException("LERC: invalid block offset type");
                            double offset = LercTypes.ReadValue(blob, ref pos, used);
                            if (stats != null)
                            {
                                stats.OffsetTypes[used]++;
                                if (kind == 1 && (src[pos] & 32) != 0) stats.LutBlocks++;
                                if (kind == 1 && (src[pos] & 31) == 0) stats.ZeroWidthBlocks++;
                            }
                            double max = zMax[version >= 4 ? m : 0];
                            if (kind == 3)
                            {
                                T v = LercTypes.FromDouble<T>(offset);
                                for (int r = 0; r < th; r++)
                                {
                                    T* p = tile + r * rowStep;
                                    byte* pm = tmask == null ? null : tmask + (long)r * cols;
                                    for (int c = 0; c < tw; c++, p += stride)
                                        if (pm == null || pm[c] != 0)
                                            *p = diff ? LercTypes.FromDouble<T>(LercTypes.Clamp(offset + LercTypes.ToDouble(p[-1]), max)) : v;
                                }
                                continue;
                            }

                            if (!diff && tmask == null && version >= 3 && pos < blob.Length && (src[pos] & 32) == 0
                                && DecodeStuffedDirect(src, blob.Length, ref pos, tile, tw, th, stride, rowStep, offset, invScale, max, dt))
                                continue;
                            if (LercBits.DecodeArray(blob, ref pos, qs, version) < count) throw new ImageFormatException("LERC: block holds too few values");
                            uint* qp = q;
                            if (diff)
                            {
                                for (int r = 0; r < th; r++)
                                {
                                    T* p = tile + r * rowStep;
                                    byte* pm = tmask == null ? null : tmask + (long)r * cols;
                                    for (int c = 0; c < tw; c++, p += stride)
                                        if (pm == null || pm[c] != 0)
                                            *p = LercTypes.FromDouble<T>(LercTypes.Clamp(offset + *qp++ * invScale + LercTypes.ToDouble(p[-1]), max));
                                }
                            }
                            else if (tmask == null && invScale == 1 && dt < LercTypes.Float)
                            {
                                // lossless integers: no floating point needed
                                long off = (long)offset, lmax = (long)max;
                                for (int r = 0; r < th; r++)
                                {
                                    T* p = tile + r * rowStep;
                                    for (int c = 0; c < tw; c++, p += stride)
                                        *p = LercTypes.FromLong<T>(Math.Min(off + *qp++, lmax));
                                }
                            }
                            else if (tmask == null)
                            {
                                for (int r = 0; r < th; r++)
                                {
                                    T* p = tile + r * rowStep;
                                    for (int c = 0; c < tw; c++, p += stride)
                                        *p = LercTypes.FromDouble<T>(LercTypes.Clamp(offset + *qp++ * invScale, max));
                                }
                            }
                            else
                            {
                                for (int r = 0; r < th; r++)
                                {
                                    T* p = tile + r * rowStep;
                                    byte* pm = tmask + (long)r * cols;
                                    for (int c = 0; c < tw; c++, p += stride)
                                        if (pm[c] != 0) *p = LercTypes.FromDouble<T>(LercTypes.Clamp(offset + *qp++ * invScale, max));
                                }
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// The common block: all pixels valid, plain bit stuffing (v3+). The values are unpacked straight into the
        /// image. Returns false (nothing consumed) where the general path has to do it: near the end of the data,
        /// or when the header does not match the block.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool DecodeStuffedDirect<T>(byte* src, int len, ref int pos, T* tile, int tw, int th, int stride, long rowStep,
            double offset, double invScale, double max, int dt) where T : unmanaged
        {
            int head = src[pos], numBits = head & 31, code = head >> 6;
            int nb = code == 0 ? 4 : 3 - code;
            if (nb <= 0 || pos + 1 + nb > len) return false;
            uint count = nb == 1 ? src[pos + 1] : nb == 2 ? *(ushort*)(src + pos + 1) : *(uint*)(src + pos + 1);
            if (count != (uint)(tw * th)) return false;
            long bytes = ((long)count * numBits + 7) >> 3;
            int start = pos + 1 + nb;
            if (start + bytes + 8 > len) return false; // the 8 byte reads below must stay inside
            byte* bits = src + start;
            ulong mask = (1UL << numBits) - 1;
            long bitPos = 0;
            if (invScale == 1 && dt < LercTypes.Float)
            {
                long off = (long)offset, lmax = (long)max;
                for (int r = 0; r < th; r++)
                {
                    T* p = tile + r * rowStep;
                    for (int c = 0; c < tw; c++, p += stride, bitPos += numBits)
                    {
                        long v = off + (long)((*(ulong*)(bits + (bitPos >> 3)) >> (int)(bitPos & 7)) & mask);
                        *p = LercTypes.FromLong<T>(v < lmax ? v : lmax);
                    }
                }
            }
            else
            {
                for (int r = 0; r < th; r++)
                {
                    T* p = tile + r * rowStep;
                    for (int c = 0; c < tw; c++, p += stride, bitPos += numBits)
                    {
                        uint qv = (uint)((*(ulong*)(bits + (bitPos >> 3)) >> (int)(bitPos & 7)) & mask);
                        *p = LercTypes.FromDouble<T>(LercTypes.Clamp(offset + qv * invScale, max));
                    }
                }
            }
            pos = start + (int)bytes;
            return true;
        }

        /// <summary>
        /// 8 bit data: one code book for the whole image, either over the pixel values or over their difference to
        /// the left neighbour (the one above at the start of a run, the last decoded value otherwise).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void ReadHuffman(ReadOnlySpan<byte> blob, int pos, in Lerc2Header h, Span<byte> dst, int stride, int chan, byte[]? valid, bool delta)
        {
            int rows = h.Rows, cols = h.Cols, depth = h.Depth;
            var table = LercHuffman.ReadTable(blob, ref pos, h.Version);
            if ((blob.Length - pos) * 8L < (long)h.ValidCount * depth) throw new ImageFormatException("LERC: Huffman data is truncated");
            var reader = new LercHuffman.Reader(table, blob, pos);
            byte bias = (byte)(h.DataType == LercTypes.Char ? 128 : 0); // symbols are value + 128 for signed data
            int rowStep = cols * stride;

            if (!delta)
            {
                // symbols are in pixel order with the depth values of a pixel together
                byte[] line = new byte[cols * depth];
                for (int i = 0; i < rows; i++)
                {
                    int n = cols;
                    if (valid != null)
                    {
                        n = 0;
                        for (int j = 0; j < cols; j++) n += valid[i * cols + j];
                    }
                    var sym = line.AsSpan(0, n * depth);
                    reader.Read(sym);
                    int si = 0, di = i * rowStep + chan;
                    for (int j = 0; j < cols; j++, di += stride)
                    {
                        if (valid != null && valid[i * cols + j] == 0) continue;
                        for (int m = 0; m < depth; m++) dst[di + m] = (byte)(sym[si++] - bias);
                    }
                }
                return;
            }

            byte[] buf = new byte[cols];
            for (int m = 0; m < depth; m++)
            {
                byte prev = 0;
                for (int i = 0; i < rows; i++)
                {
                    int di = i * rowStep + chan + m;
                    if (valid == null)
                    {
                        reader.Read(buf);
                        if (i > 0) prev = dst[di - rowStep];
                        for (int j = 0; j < cols; j++, di += stride)
                        {
                            prev = (byte)(buf[j] - bias + prev);
                            dst[di] = prev;
                        }
                        continue;
                    }
                    int n = 0, mi = i * cols;
                    for (int j = 0; j < cols; j++) n += valid[mi + j];
                    reader.Read(buf.AsSpan(0, n));
                    int si = 0;
                    for (int j = 0; j < cols; j++, di += stride)
                    {
                        if (valid[mi + j] == 0) continue;
                        byte v = (byte)(buf[si++] - bias);
                        if (j > 0 && valid[mi + j - 1] != 0) v += prev;
                        else if (i > 0 && valid[mi + j - cols] != 0) v += dst[di - rowStep];
                        else v += prev;
                        dst[di] = v;
                        prev = v;
                    }
                }
            }
        }
    }
}
