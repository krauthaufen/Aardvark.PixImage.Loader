using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Aardvark.ImageCodecs.SgiImpl
{
    internal sealed class SgiHeader
    {
        public int Storage, Bpc, Dimension, Width, Height, Channels, ColorMap;

        static int U16(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt16BigEndian(d.Slice(o));

        public static bool IsMatch(ReadOnlySpan<byte> h)
            => h.Length >= 108 && h[0] == 0x01 && h[1] == 0xDA && h[2] <= 1 && h[3] is 1 or 2 && U16(h, 4) is >= 1 and <= 3
               && U16(h, 6) > 0 && (U16(h, 4) == 1 || U16(h, 8) > 0) && (U16(h, 4) < 3 || U16(h, 10) > 0)
               && BinaryPrimitives.ReadUInt32BigEndian(h.Slice(104)) <= 3;

        public static SgiHeader Parse(ReadOnlySpan<byte> d)
        {
            if (d.Length < 512 || !IsMatch(d)) throw new ImageFormatException("SGI: not a valid SGI image header");
            int dim = U16(d, 4);
            var h = new SgiHeader
            {
                Storage = d[2], Bpc = d[3], Dimension = dim, Width = U16(d, 6),
                Height = dim == 1 ? 1 : U16(d, 8), Channels = dim < 3 ? 1 : U16(d, 10),
                ColorMap = (int)BinaryPrimitives.ReadUInt32BigEndian(d.Slice(104)),
            };
            if (h.ColorMap is 2 or 3)
                throw new ImageNotSupportedException(h.ColorMap == 2 ? "SGI: SCREEN images (obsolete) are not supported" : "SGI: the file holds a colour map, not an image");
            if (h.ColorMap == 1 && (h.Channels != 1 || h.Bpc != 1)) throw new ImageFormatException("SGI: DITHERED images have one 8 bit channel");
            return h;
        }

        public bool Dithered => ColorMap == 1;
        public ColorModel Color => Dithered ? ColorModel.Rgb : RawImage.DefaultColorModel(Channels);
        public int OutChannels => Dithered ? 3 : Channels;
    }

    internal static class SgiCodec
    {
        public static ImageInfo GetInfo(ReadOnlySpan<byte> data)
        {
            var h = SgiHeader.Parse(data);
            return new ImageInfo(ImageFormat.Sgi, h.Width, h.Height, h.OutChannels, h.Bpc == 2 ? SampleType.UInt16 : SampleType.UInt8, h.Color)
            {
                BitsPerSample = h.Dithered ? 3 : h.Bpc * 8,
            };
        }

        public static unsafe RawImage Decode(ReadOnlySpan<byte> data, SgiDecodeOptions opt)
        {
            var h = SgiHeader.Parse(data);
            int w = h.Width, hh = h.Height, zs = h.Channels, bpc = h.Bpc;
            long pixels = (long)w * hh;
            if (pixels > opt.MaxPixels || pixels * zs > int.MaxValue / 2)
                throw new ImageNotSupportedException($"SGI: {w}x{hh}x{zs} exceeds SgiDecodeOptions.MaxPixels");
            long need = h.Storage == 0 ? pixels * zs * bpc : 8L * hh * zs;
            if (pixels > opt.MaxUnbackedPixels && data.Length - 512 < need)
                throw new ImageFormatException($"SGI: {w}x{hh} image with only {data.Length - 512} bytes of data");

            int n = (int)(pixels * zs);
            Array arr = bpc == 1 ? new byte[n] : new ushort[n];
            int len = data.Length;
            fixed (byte* s = data)
            {
                if (bpc == 1)
                {
                    fixed (byte* d = (byte[])arr)
                        for (int z = 0; z < zs; z++)
                            for (int y = 0; y < hh; y++)
                            {
                                byte* o = d + ((long)(hh - 1 - y) * w * zs + z);
                                if (h.Storage == 0)
                                {
                                    long at = 512 + ((long)z * hh + y) * w;
                                    int k = (int)Math.Clamp(len - at, 0, w);
                                    for (int x = 0; x < k; x++) o[(long)x * zs] = s[at + x];
                                }
                                else RleRow8(s, len, RowStart(s, len, hh, zs, y, z), o, w, zs);
                            }
                }
                else
                {
                    fixed (ushort* d = (ushort[])arr)
                        for (int z = 0; z < zs; z++)
                            for (int y = 0; y < hh; y++)
                            {
                                ushort* o = d + ((long)(hh - 1 - y) * w * zs + z);
                                if (h.Storage == 0)
                                {
                                    long at = 512 + ((long)z * hh + y) * w * 2;
                                    int k = (int)Math.Clamp((len - at) / 2, 0, w);
                                    for (int x = 0; x < k; x++) o[(long)x * zs] = (ushort)(s[at + 2 * x] << 8 | s[at + 2 * x + 1]);
                                }
                                else RleRow16(s, len, RowStart(s, len, hh, zs, y, z), o, w, zs);
                            }
                }
            }
            if (h.Dithered) return new RawImage(w, hh, ColorModel.Rgb, Undither((byte[])arr));
            return new RawImage(w, hh, h.Color, arr, zs);
        }

        /// <summary>Start of a row in the RLE offset table, or -1 when the table is cut off.</summary>
        static unsafe long RowStart(byte* s, int len, int hh, int zs, int y, int z)
        {
            long at = 512 + 4L * ((long)z * hh + y);
            if (at + 4 > len) return -1;
            return (uint)(s[at] << 24 | s[at + 1] << 16 | s[at + 2] << 8 | s[at + 3]);
        }

        static unsafe void RleRow8(byte* s, int len, long p, byte* o, int w, int zs)
        {
            if (p < 0) return;
            int x = 0;
            while (p < len && x < w)
            {
                int b = s[p++], count = b & 0x7F;
                if (count == 0) break;
                if ((b & 0x80) != 0)
                {
                    int k = (int)Math.Min(Math.Min(count, w - x), len - p);
                    for (int i = 0; i < k; i++) o[(long)(x + i) * zs] = s[p + i];
                    p += count;
                    x += k;
                }
                else
                {
                    if (p >= len) break;
                    byte v = s[p++];
                    int k = Math.Min(count, w - x);
                    for (int i = 0; i < k; i++) o[(long)(x + i) * zs] = v;
                    x += k;
                }
            }
        }

        static unsafe void RleRow16(byte* s, int len, long p, ushort* o, int w, int zs)
        {
            if (p < 0) return;
            int x = 0;
            while (p + 2 <= len && x < w)
            {
                int b = s[p + 1], count = b & 0x7F;
                p += 2;
                if (count == 0) break;
                if ((b & 0x80) != 0)
                {
                    int k = (int)Math.Min(Math.Min(count, w - x), (len - p) / 2);
                    for (int i = 0; i < k; i++) o[(long)(x + i) * zs] = (ushort)(s[p + 2 * i] << 8 | s[p + 2 * i + 1]);
                    p += 2L * count;
                    x += k;
                }
                else
                {
                    if (p + 2 > len) break;
                    ushort v = (ushort)(s[p] << 8 | s[p + 1]);
                    p += 2;
                    int k = Math.Min(count, w - x);
                    for (int i = 0; i < k; i++) o[(long)(x + i) * zs] = v;
                    x += k;
                }
            }
        }

        /// <summary>DITHERED: each byte holds red in bits 0-2, green in bits 3-5 and blue in bits 6-7.</summary>
        static byte[] Undither(byte[] v)
        {
            var d = new byte[v.Length * 3];
            for (int i = 0; i < v.Length; i++)
            {
                int b = v[i];
                d[i * 3] = (byte)((b & 7) * 255 / 7);
                d[i * 3 + 1] = (byte)((b >> 3 & 7) * 255 / 7);
                d[i * 3 + 2] = (byte)((b >> 6) * 255 / 3);
            }
            return d;
        }

        // ------------------------------------------------------------------------------------------------ encoding

        public static unsafe byte[] Encode(RawImage img, SgiEncodeOptions opt)
        {
            if (img.SampleType is not (SampleType.UInt8 or SampleType.UInt16))
                throw new ImageNotSupportedException($"SGI: only UInt8 and UInt16 samples can be written, not {img.SampleType}");
            int w = img.Width, h = img.Height, zs = img.Channels, bpc = img.SampleType == SampleType.UInt16 ? 2 : 1;
            if (w > 65535 || h > 65535 || zs > 65535) throw new ImageNotSupportedException("SGI: width, height and channels are limited to 65535");

            var head = new byte[512];
            head[0] = 0x01; head[1] = 0xDA; head[2] = (byte)(opt.Rle ? 1 : 0); head[3] = (byte)bpc;
            BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(4), (ushort)(zs == 1 ? 2 : 3));
            BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(6), (ushort)w);
            BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(8), (ushort)h);
            BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(10), (ushort)zs);
            BinaryPrimitives.WriteInt32BigEndian(head.AsSpan(16), bpc == 1 ? 255 : 65535);   // PIXMIN 0, PIXMAX
            if (opt.Name != null)
            {
                int k = Encoding.ASCII.GetBytes(opt.Name.AsSpan(0, Math.Min(79, opt.Name.Length)), head.AsSpan(24, 79));
                head.AsSpan(24 + k, 80 - k).Clear();
            }

            var row = new ushort[w];
            var src8 = bpc == 1 ? img.GetData<byte>() : null;
            var src16 = bpc == 2 ? img.GetData<ushort>() : null;
            void Row(int z, int fileRow)
            {
                long at = (long)(h - 1 - fileRow) * w * zs + z;
                if (src8 != null) for (int x = 0; x < w; x++) row[x] = src8[at + (long)x * zs];
                else for (int x = 0; x < w; x++) row[x] = src16![at + (long)x * zs];
            }

            if (!opt.Rle)
            {
                long size = 512 + (long)w * h * zs * bpc;
                if (size > Array.MaxLength) throw new ImageNotSupportedException("SGI: image too large");
                var r = new byte[size];
                head.CopyTo(r, 0);
                long p = 512;
                for (int z = 0; z < zs; z++)
                    for (int y = 0; y < h; y++)
                    {
                        Row(z, y);
                        if (bpc == 1) for (int x = 0; x < w; x++) r[p++] = (byte)row[x];
                        else for (int x = 0; x < w; x++) { r[p++] = (byte)(row[x] >> 8); r[p++] = (byte)row[x]; }
                    }
                return r;
            }

            int rows = h * zs;
            var ms = new MemoryStream();
            ms.Write(head);
            ms.Write(new byte[8L * rows]);
            var starts = new uint[rows];
            var lengths = new uint[rows];
            var packed = new byte[(w + w / 127 + 2) * bpc + 2];
            for (int z = 0; z < zs; z++)
                for (int y = 0; y < h; y++)
                {
                    Row(z, y);
                    int n = PackRow(row, w, bpc, packed);
                    if (ms.Length > uint.MaxValue) throw new ImageNotSupportedException("SGI: the file would exceed 4 GB");
                    starts[z * h + y] = (uint)ms.Length;
                    lengths[z * h + y] = (uint)n;
                    ms.Write(packed, 0, n);
                }
            var res = ms.ToArray();
            for (int i = 0; i < rows; i++)
            {
                BinaryPrimitives.WriteUInt32BigEndian(res.AsSpan(512 + 4 * i), starts[i]);
                BinaryPrimitives.WriteUInt32BigEndian(res.AsSpan(512 + 4 * rows + 4 * i), lengths[i]);
            }
            return res;
        }

        /// <summary>RLE: runs of three or more equal values as (count, value), the rest as (0x80 | count, values...), then 0.</summary>
        static int PackRow(ushort[] row, int w, int bpc, byte[] o)
        {
            int n = 0, x = 0;
            void Put(int v)
            {
                if (bpc == 2) o[n++] = (byte)(v >> 8);
                o[n++] = (byte)v;
            }
            while (x < w)
            {
                int run = 1;
                while (x + run < w && run < 127 && row[x + run] == row[x]) run++;
                if (run >= 3)
                {
                    Put(run);
                    Put(row[x]);
                    x += run;
                    continue;
                }
                int lit = 0;
                while (x + lit < w && lit < 127
                       && !(x + lit + 2 < w && row[x + lit] == row[x + lit + 1] && row[x + lit] == row[x + lit + 2])) lit++;
                Put(0x80 | lit);
                for (int i = 0; i < lit; i++) Put(row[x + i]);
                x += lit;
            }
            Put(0);
            return n;
        }
    }
}
