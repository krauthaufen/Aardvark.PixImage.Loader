using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Aardvark.ImageCodecs.PcxImpl
{
    internal static class PcxEncoder
    {
        public static unsafe byte[] Encode(RawImage img, PcxEncodeOptions opt)
        {
            Indexed.RequireUInt8(img, "PCX");
            int w = img.Width, h = img.Height, ch = img.Channels;
            if (w > 65535 || h > 65535) throw new ImageNotSupportedException("PCX: width and height are limited to 65535");
            var src = img.GetData<byte>();
            bool gray = img.Color is ColorModel.Gray or ColorModel.GrayAlpha;

            var mode = opt.Mode;
            byte[]? indices = null;
            uint[]? colors = null;
            if (mode == PcxColorMode.Auto)
            {
                if (img.HasAlpha) mode = PcxColorMode.TrueColor;
                else if (gray) mode = src.AsSpan().IndexOfAnyExcept((byte)0, (byte)255) < 0 ?PcxColorMode.Bilevel : PcxColorMode.Palette;
                else
                {
                    var (c, i, exact) = Indexed.Palette(img, 256);
                    if (exact) { mode = PcxColorMode.Palette; colors = c; indices = i; }
                    else mode = PcxColorMode.TrueColor;
                }
            }

            int bits, planes;
            byte[] pal = new byte[768];
            switch (mode)
            {
                case PcxColorMode.Bilevel: bits = 1; planes = 1; indices = Indexed.Bilevel(img); break;
                case PcxColorMode.Palette:
                    bits = 8; planes = 1;
                    if (gray && !img.HasAlpha) indices = src;
                    else if (gray)
                    {
                        indices = new byte[w * h];
                        for (int i = 0; i < indices.Length; i++) indices[i] = src[i * 2];
                    }
                    if (gray) for (int i = 0; i < 256; i++) pal[i * 3] = pal[i * 3 + 1] = pal[i * 3 + 2] = (byte)i;
                    else
                    {
                        if (indices == null) (colors, indices, _) = Indexed.Palette(img, 256);
                        for (int i = 0; i < colors!.Length; i++)
                        {
                            pal[i * 3] = (byte)colors[i]; pal[i * 3 + 1] = (byte)(colors[i] >> 8); pal[i * 3 + 2] = (byte)(colors[i] >> 16);
                        }
                    }
                    break;
                case PcxColorMode.TrueColor: bits = 8; planes = img.HasAlpha && opt.Mode == PcxColorMode.Auto ? 4 : 3; break;
                default: throw new ArgumentException($"unknown PCX colour mode {opt.Mode}");
            }

            int bpl = ((w * bits + 7) / 8 + 1) & ~1;    // even, as the format asks
            var ms = new MemoryStream(128 + h * planes * (bpl + bpl / 32 + 2) + 769);
            var head = new byte[128];
            head[0] = 0x0A; head[1] = 5; head[2] = 1; head[3] = (byte)bits;
            BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(8), (ushort)(w - 1));
            BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(10), (ushort)(h - 1));
            var m = img.Metadata;
            if (m != null && m.DpiX > 0 && m.DpiY > 0)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(12), (ushort)Math.Clamp(Math.Round(m.DpiX), 1, 65535));
                BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(14), (ushort)Math.Clamp(Math.Round(m.DpiY), 1, 65535));
            }
            if (bits == 1) { head[19] = head[20] = head[21] = 255; }   // header palette: black, white
            head[65] = (byte)planes;
            BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(66), (ushort)bpl);
            head[68] = (byte)(gray ? 2 : 1);
            ms.Write(head);

            var line = new byte[bpl];
            var packed = new byte[bpl * 2];
            for (int y = 0; y < h; y++)
            {
                for (int p = 0; p < planes; p++)
                {
                    Array.Clear(line);
                    if (bits == 1)
                    {
                        var s = indices.AsSpan(y * w, w);
                        for (int x = 0; x < w; x++) line[x >> 3] |= (byte)(s[x] << (7 - (x & 7)));
                    }
                    else if (indices != null) indices.AsSpan(y * w, w).CopyTo(line);
                    else
                        fixed (byte* s0 = src)
                        {
                            byte* s = s0 + (long)y * w * ch;
                            if (gray)
                                for (int x = 0; x < w; x++) line[x] = s[x * ch + (p == 3 ? 1 : 0)];
                            else
                                for (int x = 0; x < w; x++) line[x] = s[x * ch + p];
                        }
                    ms.Write(packed, 0, Rle(line, packed));
                }
            }
            if (bits == 8 && planes == 1)
            {
                ms.WriteByte(0x0C);
                ms.Write(pal);
            }
            return ms.ToArray();
        }

        static int Rle(byte[] line, byte[] o)
        {
            int n = 0, x = 0, len = line.Length;
            while (x < len)
            {
                byte v = line[x];
                int run = 1;
                while (x + run < len && run < 63 && line[x + run] == v) run++;
                if (run > 1 || (v & 0xC0) == 0xC0) o[n++] = (byte)(0xC0 | run);
                o[n++] = v;
                x += run;
            }
            return n;
        }

        public static void EncodeDcx(IReadOnlyList<RawImage> pages, Stream output, PcxEncodeOptions opt)
        {
            if (pages.Count == 0) throw new ArgumentException("DCX: no pages");
            if (pages.Count > 1023) throw new ImageNotSupportedException("DCX: at most 1023 pages");
            var data = new byte[pages.Count][];
            for (int i = 0; i < data.Length; i++) data[i] = Encode(pages[i], opt);
            var head = new byte[4 + 4 * (pages.Count + 1)];
            BinaryPrimitives.WriteUInt32LittleEndian(head, 987654321);
            long at = head.Length;
            for (int i = 0; i < data.Length; i++)
            {
                if (at > uint.MaxValue) throw new ImageNotSupportedException("DCX: the file would exceed 4 GB");
                BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(4 + 4 * i), (uint)at);
                at += data[i].Length;
            }
            output.Write(head);
            foreach (var d in data) output.Write(d);
        }
    }
}
