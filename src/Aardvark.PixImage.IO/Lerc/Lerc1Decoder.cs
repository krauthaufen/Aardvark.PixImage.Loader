using System;
using System.Runtime.CompilerServices;
using System.Buffers.Binary;

namespace Aardvark.ImageCodecs
{
    /// <summary>
    /// One band of a LERC1 blob ("CntZImage "): a count part, which is the validity mask, and a z part with the
    /// float32 values in tiles. Later bands of a multi-band blob either carry only the z part and reuse the mask
    /// (Esri's writer) or are complete blobs with a mask of their own (GDAL's MRF driver).
    /// </summary>
    internal struct Lerc1Header
    {
        public const int FixedSize = 34, PartHeaderSize = 16;

        public int Width, Height;
        public double MaxZError;
        public bool HasMaskPart;
        public int MaskOffset, MaskBytes;
        public float MaskValue;
        public int TilesVert, TilesHori, DataOffset, DataBytes;
        public float MaxValue;
        public int BlobSize;

        public static bool IsMagic(ReadOnlySpan<byte> data) => data.Length >= 10 && data.Slice(0, 10).SequenceEqual("CntZImage "u8);

        /// <summary>Whether the part after the header is a count part (stored as a mask: no tiles).</summary>
        public static bool HasCountPart(ReadOnlySpan<byte> d)
            => d.Length >= FixedSize + 8 && BinaryPrimitives.ReadInt32LittleEndian(d.Slice(FixedSize)) == 0
               && BinaryPrimitives.ReadInt32LittleEndian(d.Slice(FixedSize + 4)) == 0;

        public static Lerc1Header Parse(ReadOnlySpan<byte> d, bool zPartOnly)
        {
            if (!IsMagic(d)) throw new ImageFormatException("LERC: not a LERC1 blob");
            if (d.Length < FixedSize + PartHeaderSize) throw new ImageFormatException("LERC: header is truncated");
            int version = BinaryPrimitives.ReadInt32LittleEndian(d.Slice(10));
            int type = BinaryPrimitives.ReadInt32LittleEndian(d.Slice(14));
            if (version != 11 || type != 8) throw new ImageFormatException("LERC: unknown LERC1 version or image type");
            var h = new Lerc1Header
            {
                Height = BinaryPrimitives.ReadInt32LittleEndian(d.Slice(18)),
                Width = BinaryPrimitives.ReadInt32LittleEndian(d.Slice(22)),
                MaxZError = BinaryPrimitives.ReadDoubleLittleEndian(d.Slice(26)),
                HasMaskPart = !zPartOnly,
            };
            if (h.Width <= 0 || h.Height <= 0 || h.Width > 20000 || h.Height > 20000) throw new ImageFormatException("LERC: invalid image size");
            if (!(h.MaxZError >= 0)) throw new ImageFormatException("LERC: invalid header");

            int p = FixedSize;
            if (!zPartOnly)
            {
                int tv = BinaryPrimitives.ReadInt32LittleEndian(d.Slice(p));
                int th = BinaryPrimitives.ReadInt32LittleEndian(d.Slice(p + 4));
                h.MaskBytes = BinaryPrimitives.ReadInt32LittleEndian(d.Slice(p + 8));
                h.MaskValue = BinaryPrimitives.ReadSingleLittleEndian(d.Slice(p + 12));
                p += PartHeaderSize;
                if (tv != 0 || th != 0) throw new ImageNotSupportedException("LERC: LERC1 with a non-binary count image is not supported");
                if (h.MaskBytes < 0 || h.MaskBytes > int.MaxValue - 2 * PartHeaderSize - FixedSize) throw new ImageFormatException("LERC: invalid mask size");
                h.MaskOffset = p;
                p += h.MaskBytes;
                if (d.Length - PartHeaderSize < p) throw new ImageFormatException("LERC: header is truncated");
            }
            h.TilesVert = BinaryPrimitives.ReadInt32LittleEndian(d.Slice(p));
            h.TilesHori = BinaryPrimitives.ReadInt32LittleEndian(d.Slice(p + 4));
            h.DataBytes = BinaryPrimitives.ReadInt32LittleEndian(d.Slice(p + 8));
            h.MaxValue = BinaryPrimitives.ReadSingleLittleEndian(d.Slice(p + 12));
            p += PartHeaderSize;
            if (h.TilesVert <= 0 || h.TilesHori <= 0 || h.TilesVert > h.Height || h.TilesHori > h.Width) throw new ImageFormatException("LERC: invalid tile count");
            if (h.DataBytes < 0 || h.DataBytes > int.MaxValue - p) throw new ImageFormatException("LERC: invalid data size");
            h.DataOffset = p;
            h.BlobSize = p + h.DataBytes;
            return h;
        }
    }

    internal static class Lerc1Decoder
    {
        /// <summary>Decodes one band; see <see cref="Lerc2Decoder.DecodeBand"/> for the conventions.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void DecodeBand(ReadOnlySpan<byte> blob, in Lerc1Header h, Span<float> dst, int stride, int chan, LercMask mask)
        {
            int width = h.Width, height = h.Height, pixels = width * height;
            if (h.HasMaskPart)
            {
                if (h.MaskBytes == 0)
                {
                    bool all = h.MaskValue > 0;
                    mask.Bytes = all ? null : new byte[pixels];
                    mask.ValidCount = all ? pixels : 0;
                }
                else
                {
                    int planeBytes = (pixels + 7) >> 3;
                    if (planeBytes > ((long)h.MaskBytes / 3 + 1) * 32767) throw new ImageFormatException("LERC: mask data is too short");
                    byte[] plane = new byte[planeBytes];
                    LercBits.DecodeMaskRle(blob.Slice(h.MaskOffset, h.MaskBytes), plane);
                    byte[] bytes = new byte[pixels];
                    mask.ValidCount = LercBits.ExpandMask(plane, bytes);
                    mask.Bytes = mask.ValidCount == pixels ? null : bytes;
                }
            }
            byte[]? valid = mask.Bytes;
            for (int k = 0, di = chan; k < pixels; k++, di += stride) dst[di] = 0;

            var data = blob.Slice(h.DataOffset, h.DataBytes);
            int pos = 0;
            int tileH = height / h.TilesVert, tileW = width / h.TilesHori;
            if ((long)h.TilesVert * h.TilesHori > data.Length) throw new ImageFormatException("LERC: pixel data is truncated");
            uint[] q = new uint[checked((tileH + 1) * (tileW + 1))];
            double invScale = 2 * h.MaxZError;
            float maxValue = h.MaxValue;

            // regular tiles, then a narrower last row / column for the remainder
            for (int it = 0; it <= h.TilesVert; it++)
            {
                int i0 = it * tileH, th = it == h.TilesVert ? height % h.TilesVert : tileH;
                if (th == 0) continue;
                for (int jt = 0; jt <= h.TilesHori; jt++)
                {
                    int j0 = jt * tileW, tw = jt == h.TilesHori ? width % h.TilesHori : tileW;
                    if (tw == 0) continue;
                    if (pos >= data.Length) throw new ImageFormatException("LERC: pixel data is truncated");
                    int flag = data[pos++];
                    int sizeCode = flag >> 6, kind = flag & 63;
                    if (kind > 3) throw new ImageFormatException("LERC: invalid block type");
                    if (LercStats.Current != null) LercStats.Current.Lerc1Blocks[kind]++;
                    if (kind == 2) continue; // all 0

                    int count = tw * th;
                    if (valid != null)
                    {
                        count = 0;
                        for (int r = 0; r < th; r++)
                            for (int c = 0; c < tw; c++) count += valid[(i0 + r) * width + j0 + c];
                    }

                    if (kind == 0)
                    {
                        if (data.Length - pos < (long)count * 4) throw new ImageFormatException("LERC: pixel data is truncated");
                        for (int r = 0; r < th; r++)
                        {
                            int mi = (i0 + r) * width + j0, di = mi * stride + chan;
                            for (int c = 0; c < tw; c++, di += stride)
                            {
                                if (valid != null && valid[mi + c] == 0) continue;
                                dst[di] = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(pos));
                                pos += 4;
                            }
                        }
                        continue;
                    }

                    // offset as float, int16 or int8
                    if (sizeCode == 3) throw new ImageFormatException("LERC: invalid block offset type");
                    double offset = LercTypes.ReadValue(data, ref pos, sizeCode == 0 ? LercTypes.Float : sizeCode == 1 ? LercTypes.Short : LercTypes.Char);
                    Span<uint> values = default;
                    if (kind == 1)
                    {
                        int n = ReadStuffed(data, ref pos, q);
                        if (n < count) throw new ImageFormatException("LERC: block holds too few values");
                        values = q;
                    }
                    int qi = 0;
                    for (int r = 0; r < th; r++)
                    {
                        int mi = (i0 + r) * width + j0, di = mi * stride + chan;
                        for (int c = 0; c < tw; c++, di += stride)
                        {
                            if (valid != null && valid[mi + c] == 0) continue;
                            if (kind == 3) dst[di] = (float)offset;
                            else
                            {
                                float z = (float)(offset + values[qi++] * invScale);
                                dst[di] = maxValue < z ? maxValue : z;
                            }
                        }
                    }
                }
            }
        }

        static int ReadStuffed(ReadOnlySpan<byte> data, ref int pos, Span<uint> dst)
        {
            if (pos >= data.Length) throw new ImageFormatException("LERC: data ends inside a block");
            int head = data[pos++];
            int code = head >> 6, numBits = head & 63;
            int nb = code == 0 ? 4 : 3 - code;
            if (nb <= 0 || data.Length - pos < nb || numBits >= 32) throw new ImageFormatException("LERC: corrupt bit stuffing header");
            uint count = nb == 1 ? data[pos] : nb == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(pos)) : BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos));
            pos += nb;
            if (count > (uint)dst.Length) throw new ImageFormatException("LERC: bit stuffed array is larger than its block");
            var values = dst.Slice(0, (int)count);
            if (numBits == 0 || count == 0) { values.Clear(); return (int)count; }
            int size = LercBits.StuffedSizeWords((int)count, numBits);
            if (data.Length - pos < size) throw new ImageFormatException("LERC: data ends inside a block");
            LercBits.UnstuffWords(data.Slice(pos, size), values, numBits);
            pos += size;
            return (int)count;
        }
    }
}
