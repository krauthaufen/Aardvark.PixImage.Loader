using System;
using System.Text;

namespace Aardvark.ImageCodecs.XbmImpl
{
    internal static class XbmCodec
    {
        internal sealed class Header
        {
            public int Width, Height;
            public (int X, int Y)? Hot;
            public bool Shorts;      // X10: 16 bit words
            public int DataPos;      // just after the opening brace
        }

        static bool EndsWith(ReadOnlySpan<byte> w, ReadOnlySpan<byte> suffix) => w.Length > suffix.Length - 1 && w.EndsWith(suffix);

        public static bool IsMatch(ReadOnlySpan<byte> d)
        {
            var t = new CText(d);
            return Define(ref t, "_width"u8, out long w) && w > 0 && Define(ref t, "_height"u8, out long h) && h > 0;
        }

        /// <summary><c>#define &lt;name&gt;&lt;suffix&gt; &lt;number&gt;</c></summary>
        static bool Define(ref CText t, ReadOnlySpan<byte> suffix, out long v)
        {
            v = 0;
            if (!t.Char((byte)'#') || !t.Word().SequenceEqual("define"u8)) return false;
            return EndsWith(t.Word(), suffix) && t.Number(out v);
        }

        public static Header ParseHeader(ReadOnlySpan<byte> d)
        {
            var t = new CText(d);
            long w = -1, h = -1, hx = -1, hy = -1;
            while (t.Char((byte)'#'))
            {
                var directive = t.Word();
                if (!directive.SequenceEqual("define"u8))
                {
                    // another preprocessor line: skip it
                    int e = d.Slice(t.Pos).IndexOf((byte)'\n');
                    if (e < 0) throw new ImageFormatException("XBM: no bitmap data");
                    t.Pos += e + 1;
                    continue;
                }
                var name = t.Word();
                if (!t.Number(out long v)) throw new ImageFormatException("XBM: a #define without a number");
                if (EndsWith(name, "_width"u8)) w = v;
                else if (EndsWith(name, "_height"u8)) h = v;
                else if (EndsWith(name, "_x_hot"u8)) hx = v;
                else if (EndsWith(name, "_y_hot"u8)) hy = v;
            }
            if (w <= 0 || h <= 0 || w > int.MaxValue / 2 || h > int.MaxValue / 2) throw new ImageFormatException("XBM: missing or invalid width / height");
            bool shorts = false;
            for (int i = 0; ; i++)
            {
                var word = t.Word();
                if (word.IsEmpty || i > 8) throw new ImageFormatException("XBM: no bits array");
                if (word.SequenceEqual("short"u8)) shorts = true;
                if (EndsWith(word, "_bits"u8)) break;
            }
            if (!t.Char((byte)'[')) throw new ImageFormatException("XBM: the bits array has no brackets");
            t.Number(out _);
            if (!t.Char((byte)']') || !t.Char((byte)'=') || !t.Char((byte)'{')) throw new ImageFormatException("XBM: malformed bits array");
            return new Header
            {
                Width = (int)w, Height = (int)h, Shorts = shorts, DataPos = t.Pos,
                Hot = hx >= 0 && hy >= 0 ? ((int)hx, (int)hy) : null,
            };
        }

        public static ImageInfo GetInfo(ReadOnlySpan<byte> data)
        {
            var h = ParseHeader(data);
            return new ImageInfo(ImageFormat.Xbm, h.Width, h.Height, 1, SampleType.UInt8, ColorModel.Gray) { BitsPerSample = 1 };
        }

        public static RawImage Decode(ReadOnlySpan<byte> data, XbmDecodeOptions opt)
        {
            var h = ParseHeader(data);
            int w = h.Width, hh = h.Height;
            long pixels = (long)w * hh;
            if (pixels > opt.MaxPixels) throw new ImageNotSupportedException($"XBM: {w}x{hh} exceeds XbmDecodeOptions.MaxPixels");
            int unit = h.Shorts ? 16 : 8;
            int perRow = (w + unit - 1) / unit;
            // every value takes at least two characters ("0," at the shortest)
            if ((long)perRow * hh > (data.Length - h.DataPos) / 2 + 1) throw new ImageFormatException("XBM: the bitmap data ends early");
            var dst = new byte[pixels];
            var t = new CText(data, h.DataPos);
            for (int y = 0; y < hh; y++)
                for (int k = 0; k < perRow; k++)
                {
                    if (!t.Number(out long v)) throw new ImageFormatException("XBM: the bitmap data ends early or holds something else");
                    if (!t.Char((byte)',') && !t.Peek((byte)'}')) throw new ImageFormatException("XBM: malformed bitmap data");
                    int x0 = k * unit, n = Math.Min(unit, w - x0);
                    int o = y * w + x0;
                    for (int i = 0; i < n; i++) dst[o + i] = (byte)((v >> i & 1) != 0 ? 0 : 255);
                }
            return new RawImage(w, hh, ColorModel.Gray, dst);
        }

        public static byte[] Encode(RawImage img, XbmEncodeOptions opt)
        {
            Indexed.RequireUInt8(img, "XBM");
            int w = img.Width, h = img.Height;
            var white = Indexed.Bilevel(img);
            var name = new StringBuilder();
            foreach (char c in string.IsNullOrEmpty(opt.Name) ? "image" : opt.Name)
                name.Append(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' ? c : '_');
            if (char.IsDigit(name[0])) name.Insert(0, '_');
            var sb = new StringBuilder();
            sb.Append("#define ").Append(name).Append("_width ").Append(w).Append('\n');
            sb.Append("#define ").Append(name).Append("_height ").Append(h).Append('\n');
            if (opt.HotSpot is { } hot)
            {
                sb.Append("#define ").Append(name).Append("_x_hot ").Append(hot.X).Append('\n');
                sb.Append("#define ").Append(name).Append("_y_hot ").Append(hot.Y).Append('\n');
            }
            int unit = opt.X10 ? 16 : 8, perRow = (w + unit - 1) / unit, perLine = opt.X10 ? 8 : 12;
            sb.Append(opt.X10 ? "static short " : "static unsigned char ").Append(name).Append("_bits[] = {");
            long count = (long)perRow * h, i = 0;
            for (int y = 0; y < h; y++)
                for (int k = 0; k < perRow; k++, i++)
                {
                    int v = 0, x0 = k * unit, n = Math.Min(unit, w - x0);
                    for (int b = 0; b < n; b++) v |= (white[y * w + x0 + b] ^ 1) << b;
                    sb.Append(i % perLine == 0 ? "\n   " : " ");
                    sb.Append("0x").Append(v.ToString(opt.X10 ? "x4" : "x2"));
                    if (i + 1 < count) sb.Append(',');
                }
            sb.Append(" };\n");
            return Encoding.ASCII.GetBytes(sb.ToString());
        }
    }
}
