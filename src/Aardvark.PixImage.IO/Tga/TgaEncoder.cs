using System;
using System.Buffers.Binary;
using System.IO;

namespace Aardvark.ImageCodecs.TgaImpl
{
    internal static class TgaEncoder
    {
        public static void Encode(RawImage img, Stream output, TgaEncodeOptions opt)
        {
            if (img.SampleType != SampleType.UInt8)
                throw new ImageNotSupportedException($"TGA: only UInt8 samples can be written, not {img.SampleType}");
            if (img.Color == ColorModel.Other)
                throw new ImageNotSupportedException("TGA: only Gray, GrayAlpha, Rgb and Rgba can be written");
            int w = img.Width, h = img.Height, ch = img.Channels;
            if (w > 65535 || h > 65535) throw new ImageNotSupportedException("TGA: width and height are limited to 65535");
            bool gray = img.Color is ColorModel.Gray or ColorModel.GrayAlpha;
            int bpp = ch;   // stored bytes per pixel: gray (+ alpha), B G R (A)

            var head = new byte[18];
            head[2] = (byte)((gray ? 3 : 2) + (opt.Rle ? 8 : 0));
            BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(12), (ushort)w);
            BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(14), (ushort)h);
            head[16] = (byte)(bpp * 8);
            head[17] = (byte)((img.HasAlpha ? 8 : 0) | (opt.TopDown ? 0x20 : 0));
            output.Write(head);
            long written = 18;

            var src = img.GetData<byte>();
            var row = new byte[w * bpp];
            var packed = opt.Rle ? new byte[w * (bpp + 1) + 1] : null;
            for (int r = 0; r < h; r++)
            {
                int y = opt.TopDown ? r : h - 1 - r;
                var line = src.AsSpan(y * w * ch, w * ch);
                if (gray) line.CopyTo(row);
                else
                    for (int x = 0; x < w; x++)
                    {
                        row[x * bpp] = line[x * ch + 2]; row[x * bpp + 1] = line[x * ch + 1]; row[x * bpp + 2] = line[x * ch];
                        if (ch == 4) row[x * 4 + 3] = line[x * 4 + 3];
                    }
                if (packed == null) { output.Write(row); written += row.Length; }
                else
                {
                    int n = PackRow(row, bpp, packed);
                    output.Write(packed, 0, n);
                    written += n;
                }
            }

            // TGA 2.0: extension area recording the alpha type, then the footer
            var ext = new byte[495];
            BinaryPrimitives.WriteUInt16LittleEndian(ext, 495);
            ext[494] = (byte)(img.HasAlpha ? 3 : 0);
            output.Write(ext);
            var foot = new byte[26];
            BinaryPrimitives.WriteUInt32LittleEndian(foot, (uint)written);
            "TRUEVISION-XFILE.\0"u8.CopyTo(foot.AsSpan(8));
            output.Write(foot);
        }

        /// <summary>RLE packets for one row (packets never cross rows, as TGA 2.0 asks).</summary>
        static int PackRow(byte[] row, int bpp, byte[] o)
        {
            int w = row.Length / bpp, n = 0, x = 0;
            bool Same(int a, int b) => row.AsSpan(a * bpp, bpp).SequenceEqual(row.AsSpan(b * bpp, bpp));
            while (x < w)
            {
                int run = 1;
                while (x + run < w && run < 128 && Same(x, x + run)) run++;
                if (run >= 2)
                {
                    o[n++] = (byte)(0x80 | (run - 1));
                    row.AsSpan(x * bpp, bpp).CopyTo(o.AsSpan(n));
                    n += bpp;
                    x += run;
                    continue;
                }
                // literal packet up to the next run of at least 2
                int lit = 1;
                while (x + lit < w && lit < 128 && !(x + lit + 1 < w && Same(x + lit, x + lit + 1))) lit++;
                o[n++] = (byte)(lit - 1);
                row.AsSpan(x * bpp, lit * bpp).CopyTo(o.AsSpan(n));
                n += lit * bpp;
                x += lit;
            }
            return n;
        }
    }
}
