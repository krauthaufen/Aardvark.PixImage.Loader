using System;
using System.Collections.Generic;
using System.Text;

namespace Aardvark.ImageCodecs.XpmImpl
{
    internal static class XpmCodec
    {
        static int SkipSpace(ReadOnlySpan<byte> d, int i)
        {
            while (i < d.Length && CText.IsSpace(d[i])) i++;
            return i;
        }

        /// <summary>0 = not XPM, 3 = <c>/* XPM */</c>, 2 = <c>! XPM2</c>.</summary>
        static int Version(ReadOnlySpan<byte> d)
        {
            int i = SkipSpace(d, 0);
            if (d.Slice(i).StartsWith("! XPM2"u8)) return i + 6 == d.Length || CText.IsSpace(d[i + 6]) ? 2 : 0;
            if (!d.Slice(i).StartsWith("/*"u8)) return 0;
            i += 2;
            while (i < d.Length && d[i] is (byte)' ' or (byte)'\t') i++;
            if (!d.Slice(i).StartsWith("XPM"u8)) return 0;
            i += 3;
            while (i < d.Length && d[i] is (byte)' ' or (byte)'\t') i++;
            return d.Slice(i).StartsWith("*/"u8) ? 3 : 0;
        }

        public static bool IsMatch(ReadOnlySpan<byte> d) => Version(d) != 0;

        /// <summary>The strings of the file in order: the C strings of XPM 3, the lines of XPM 2.</summary>
        ref struct Strings
        {
            readonly ReadOnlySpan<byte> d;
            readonly bool lines;
            CText t;
            bool done;

            public Strings(ReadOnlySpan<byte> data)
            {
                d = data;
                int v = Version(data);
                if (v == 0) throw new ImageFormatException("XPM: missing the XPM signature");
                lines = v == 2;
                t = new CText(data);
                done = false;
                if (lines)
                {
                    int e = data.IndexOf((byte)'\n');
                    t.Pos = e < 0 ? data.Length : e + 1;
                }
                else if (!t.SkipPast((byte)'{')) throw new ImageFormatException("XPM: no array");
            }

            public ReadOnlySpan<byte> Next()
            {
                if (done) throw new ImageFormatException("XPM: the file ends early");
                if (lines)
                {
                    while (true)
                    {
                        if (t.Pos >= d.Length) throw new ImageFormatException("XPM: the file ends early");
                        int e = d.Slice(t.Pos).IndexOf((byte)'\n');
                        var line = e < 0 ? d.Slice(t.Pos) : d.Slice(t.Pos, e);
                        t.Pos = e < 0 ? d.Length : t.Pos + e + 1;
                        if (!line.IsEmpty && line[^1] == '\r') line = line.Slice(0, line.Length - 1);
                        if (!line.IsEmpty) return line;
                    }
                }
                if (!t.String(out int s, out int n)) throw new ImageFormatException("XPM: expected a string");
                if (!t.Char((byte)',')) done = !t.Peek((byte)'}') ? throw new ImageFormatException("XPM: malformed array") : true;
                return d.Slice(s, n);
            }
        }

        sealed class Head
        {
            public int Width, Height, Count, Cpp;
            public uint[] Colors = Array.Empty<uint>();     // r | g << 8 | b << 16 | 255 << 24, 0 = None
            public ulong[] Keys = Array.Empty<ulong>();
            public bool AnyTransparent, AllGray;
            public ColorModel Color => AllGray ? (AnyTransparent ? ColorModel.GrayAlpha : ColorModel.Gray) : AnyTransparent ? ColorModel.Rgba : ColorModel.Rgb;
        }

        static ulong Key(ReadOnlySpan<byte> s)
        {
            ulong k = 0;
            for (int i = 0; i < s.Length; i++) k = k << 8 | s[i];
            return k;
        }

        static Head ParseHead(ref Strings strings)
        {
            var values = strings.Next();
            var nums = new long[4];
            int at = 0;
            for (int i = 0; i < 4; i++)
            {
                at = SkipSpace(values, at);
                int s = at;
                while (at < values.Length && !CText.IsSpace(values[at])) at++;
                if (!CText.TryParse(values.Slice(s, at - s), out nums[i]) || values[s] == '0' && at - s > 1)
                    throw new ImageFormatException("XPM: malformed values line");
            }
            if (nums[0] <= 0 || nums[1] <= 0 || nums[0] > int.MaxValue / 2 || nums[1] > int.MaxValue / 2) throw new ImageFormatException("XPM: invalid size");
            if (nums[2] <= 0 || nums[2] > 1 << 24) throw new ImageFormatException("XPM: invalid colour count");
            if (nums[3] <= 0) throw new ImageFormatException("XPM: invalid characters per pixel");
            if (nums[3] > 8) throw new ImageNotSupportedException("XPM: more than 8 characters per pixel are not supported");
            var h = new Head { Width = (int)nums[0], Height = (int)nums[1], Count = (int)nums[2], Cpp = (int)nums[3], AllGray = true };
            h.Colors = new uint[h.Count];
            h.Keys = new ulong[h.Count];
            for (int i = 0; i < h.Count; i++)
            {
                var line = strings.Next();
                if (line.Length < h.Cpp) throw new ImageFormatException("XPM: malformed colour line");
                h.Keys[i] = Key(line.Slice(0, h.Cpp));
                uint c = ParseColorLine(line.Slice(h.Cpp));
                h.Colors[i] = c;
                if (c == 0) h.AnyTransparent = true;
                else if ((c & 0xFF) != (c >> 8 & 0xFF) || (c & 0xFF) != (c >> 16 & 0xFF)) h.AllGray = false;
            }
            return h;
        }

        static bool IsKey(ReadOnlySpan<byte> w) => w.Length == 1 && w[0] is (byte)'c' or (byte)'m' or (byte)'g' or (byte)'s' || w.SequenceEqual("g4"u8);

        /// <summary>The colour of the best visual of a colour line: c, else g, g4, m (s is a symbolic name only).</summary>
        static uint ParseColorLine(ReadOnlySpan<byte> s)
        {
            // key value pairs; values may hold spaces ("light blue")
            Span<int> keyAt = stackalloc int[5];          // c, g, g4, m: start of value; length in valLen
            Span<int> valLen = stackalloc int[5];
            keyAt.Fill(-1);
            int cur = -1, i = 0;
            while (true)
            {
                i = SkipSpace(s, i);
                if (i >= s.Length) break;
                int st = i;
                while (i < s.Length && !CText.IsSpace(s[i])) i++;
                var w = s.Slice(st, i - st);
                // a key only where one can stand: at the start or after a value
                if (IsKey(w) && (cur == -1 || valLen[cur] > 0))
                {
                    cur = w[0] switch { (byte)'c' => 0, (byte)'g' => w.Length == 1 ? 1 : 2, (byte)'m' => 3, _ => 4 };
                    keyAt[cur] = -2;
                    valLen[cur] = 0;
                    continue;
                }
                if (cur < 0) throw new ImageFormatException("XPM: colour line without a key");
                if (keyAt[cur] < 0) keyAt[cur] = st;
                valLen[cur] = i - keyAt[cur];
            }
            for (int k = 0; k < 4; k++)
                if (keyAt[k] >= 0 && TryColor(s.Slice(keyAt[k], valLen[k]), out uint c)) return c;
            throw new ImageFormatException("XPM: a colour without a usable value");
        }

        static bool TryColor(ReadOnlySpan<byte> v, out uint c)
        {
            c = 0;
            if (v.Length == 4 && Ascii.EqualsIgnoreCase(v, "none"u8)) return true;
            if (v.Length > 1 && v[0] == '#')
            {
                var hex = v.Slice(1);
                if (hex.Length % 3 != 0 || hex.Length > 12) return false;
                int n = hex.Length / 3, max = (1 << 4 * n) - 1;
                Span<byte> buf = stackalloc byte[8];
                for (int k = 0; k < 3; k++)
                {
                    if (!CText.TryParse(Hex(hex.Slice(k * n, n), buf), out long x)) return false;
                    c |= (uint)((x * 255 + max / 2) / max) << 8 * k;
                }
                c |= 0xFF000000;
                return true;
            }
            if (v.Length > 64) return false;
            Span<char> name = stackalloc char[v.Length];
            for (int i = 0; i < v.Length; i++) name[i] = (char)v[i];
            int rgb = X11Colors.Lookup(name);
            if (rgb < 0) return false;
            c = (uint)(rgb >> 16 & 0xFF) | (uint)(rgb & 0xFF00) | (uint)(rgb & 0xFF) << 16 | 0xFF000000;
            return true;
        }

        static ReadOnlySpan<byte> Hex(ReadOnlySpan<byte> digits, Span<byte> buf)
        {
            buf[0] = (byte)'0'; buf[1] = (byte)'x';
            digits.CopyTo(buf.Slice(2));
            return buf.Slice(0, digits.Length + 2);
        }

        public static ImageInfo GetInfo(ReadOnlySpan<byte> data)
        {
            var s = new Strings(data);
            var h = ParseHead(ref s);
            var color = h.Color;
            return new ImageInfo(ImageFormat.Xpm, h.Width, h.Height, RawImage.ChannelCount(color), SampleType.UInt8, color) { BitsPerSample = 8 };
        }

        public static unsafe RawImage Decode(ReadOnlySpan<byte> data, XpmDecodeOptions opt)
        {
            var s = new Strings(data);
            var h = ParseHead(ref s);
            int w = h.Width, hh = h.Height, cpp = h.Cpp;
            long pixels = (long)w * hh;
            if (pixels > opt.MaxPixels) throw new ImageNotSupportedException($"XPM: {w}x{hh} exceeds XpmDecodeOptions.MaxPixels");
            if (pixels * cpp > data.Length) throw new ImageFormatException("XPM: the pixels end early");

            // character(s) -> colour index
            int[]? table = cpp <= 2 ? new int[cpp == 1 ? 256 : 65536] : null;
            Dictionary<ulong, int>? dict = table == null ? new Dictionary<ulong, int>(h.Count) : null;
            if (table != null) Array.Fill(table, -1);
            for (int i = 0; i < h.Count; i++)
                if (table != null) table[h.Keys[i]] = i;
                else dict![h.Keys[i]] = i;

            var color = h.Color;
            int ch = RawImage.ChannelCount(color);
            var dst = new byte[pixels * ch];
            var colors = h.Colors;
            fixed (byte* d0 = dst)
                for (int y = 0; y < hh; y++)
                {
                    var row = s.Next();
                    if (row.Length < w * cpp) throw new ImageFormatException("XPM: a pixel row is too short");
                    byte* d = d0 + (long)y * w * ch;
                    for (int x = 0; x < w; x++, d += ch)
                    {
                        int idx;
                        if (cpp == 1) idx = table![row[x]];
                        else if (cpp == 2) idx = table![row[2 * x] << 8 | row[2 * x + 1]];
                        else if (!dict!.TryGetValue(Key(row.Slice(x * cpp, cpp)), out idx)) idx = -1;
                        if (idx < 0) throw new ImageFormatException("XPM: a pixel with an undefined colour");
                        uint c = colors[idx];
                        switch (ch)
                        {
                            case 1: d[0] = (byte)c; break;
                            case 2: d[0] = (byte)c; d[1] = (byte)(c >> 24); break;
                            case 3: d[0] = (byte)c; d[1] = (byte)(c >> 8); d[2] = (byte)(c >> 16); break;
                            default: *(uint*)d = c; break;
                        }
                    }
                }
            return new RawImage(w, hh, color, dst, ch);
        }

        // ------------------------------------------------------------------------------------------------ encoding

        // printable ASCII except '"' and '\', letters and digits first
        const string Alphabet = " .abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789#$%&'()*+,-/:;<=>?@[]^_`{|}~!";

        public static byte[] Encode(RawImage img, XpmEncodeOptions opt)
        {
            Indexed.RequireUInt8(img, "XPM");
            int w = img.Width, h = img.Height;
            var px = Indexed.Pack(img, true);
            var index = new Dictionary<uint, int>();
            var order = new List<uint>();
            var idx = new int[px.Length];
            uint last = 1;
            int lastIdx = -1;
            for (int i = 0; i < px.Length; i++)
            {
                uint c = px[i];
                if (c != last)
                {
                    if (!index.TryGetValue(c, out lastIdx)) { lastIdx = order.Count; index[c] = lastIdx; order.Add(c); }
                    last = c;
                }
                idx[i] = lastIdx;
            }
            int n = order.Count, cpp = 1, b = Alphabet.Length;
            for (long cap = b; cap < n; cap *= b) cpp++;
            var keys = new char[n * cpp];
            for (int i = 0; i < n; i++)
                for (int k = cpp - 1, v = i; k >= 0; k--, v /= b) keys[i * cpp + k] = Alphabet[v % b];

            var name = new StringBuilder();
            foreach (char c in string.IsNullOrEmpty(opt.Name) ? "image" : opt.Name)
                name.Append(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' ? c : '_');
            if (char.IsDigit(name[0])) name.Insert(0, '_');
            var sb = new StringBuilder((int)Math.Min(int.MaxValue / 2, (long)w * h * cpp + h * 4 + n * (cpp + 16) + 64));
            sb.Append("/* XPM */\nstatic char *").Append(name).Append("[] = {\n");
            sb.Append('"').Append(w).Append(' ').Append(h).Append(' ').Append(n).Append(' ').Append(cpp).Append("\",\n");
            for (int i = 0; i < n; i++)
            {
                uint c = order[i];
                sb.Append('"').Append(keys, i * cpp, cpp).Append(" c ");
                if (c == 0) sb.Append("None");
                else sb.Append('#').Append((c & 0xFF).ToString("X2")).Append((c >> 8 & 0xFF).ToString("X2")).Append((c >> 16 & 0xFF).ToString("X2"));
                sb.Append("\",\n");
            }
            for (int y = 0; y < h; y++)
            {
                sb.Append('"');
                for (int x = 0; x < w; x++) sb.Append(keys, idx[y * w + x] * cpp, cpp);
                sb.Append(y + 1 < h ? "\",\n" : "\"\n");
            }
            sb.Append("};\n");
            return Encoding.ASCII.GetBytes(sb.ToString());
        }
    }
}
