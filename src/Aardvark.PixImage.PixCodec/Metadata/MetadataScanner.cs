using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Aardvark.ImageCodecs
{
    /// <summary>The metadata blocks found in a file, before interpretation.</summary>
    internal sealed class MetadataBlocks
    {
        public ImageFormat Format;
        public byte[]? Exif;                  // TIFF structured EXIF block
        public ExifData? TiffIfd0;            // TIFF files: the first page's directories
        public readonly List<byte[]> Xmp = new();
        public byte[]? Iptc;
        public byte[]? Icc;
        public readonly List<string> Comments = new();
        public readonly List<KeyValuePair<string, string>> Text = new();
        public IReadOnlyList<ExrAttribute>? ExrAttributes;
    }

    /// <summary>Walks the container of each format and collects EXIF, XMP, IPTC, ICC and text blocks; never decodes pixels.</summary>
    internal static class MetadataScanner
    {
        const int MaxInflated = 16 << 20;

        public static MetadataBlocks Scan(ReadOnlySpan<byte> d)
        {
            var b = new MetadataBlocks { Format = ImageCodec.DetectFormat(d) };
            try
            {
                switch (b.Format)
                {
                    case ImageFormat.Jpeg: case ImageFormat.JpegLs: Jpeg(d, b); break;
                    case ImageFormat.Png: Png(d, b); break;
                    case ImageFormat.Tiff: Tiff(d, b); break;
                    case ImageFormat.Webp: Webp(d, b); break;
                    case ImageFormat.Gif: Gif(d, b); break;
                    case ImageFormat.Jp2: Jp2(d, b); break;
                    case ImageFormat.Exr: b.ExrAttributes = Aardvark.ImageCodecs.Exr.ReadHeaders(d)[0].Attributes; break;
                    case ImageFormat.Unknown: throw new ImageFormatException("unknown image format");
                }
            }
            catch (ArgumentOutOfRangeException) { }   // truncated containers: keep what was found
            catch (IndexOutOfRangeException) { }
            return b;
        }

        // ------------------------------------------------------------------ JPEG

        static void Jpeg(ReadOnlySpan<byte> d, MetadataBlocks b)
        {
            var icc = new SortedDictionary<int, byte[]>();
            var ext = new Dictionary<string, (int total, SortedDictionary<int, byte[]> parts)>();
            int p = 2;
            while (p + 4 <= d.Length)
            {
                if (d[p] != 0xFF) { p++; continue; }
                int m = d[p + 1];
                if (m == 0xFF) { p++; continue; }
                if (m == 0xD8 || m is >= 0xD0 and <= 0xD7 || m == 0x01) { p += 2; continue; }
                if (m == 0xD9 || m == 0xDA) break;   // metadata precedes the scan
                int len = BinaryPrimitives.ReadUInt16BigEndian(d.Slice(p + 2));
                if (len < 2 || p + 2 + len > d.Length) break;
                var s = d.Slice(p + 4, len - 2);
                if (m == 0xE1)
                {
                    if (s.Length > 6 && s.StartsWith("Exif\0"u8)) b.Exif ??= s.Slice(6).ToArray();
                    else if (s.StartsWith("http://ns.adobe.com/xap/1.0/\0"u8)) b.Xmp.Add(s.Slice(29).ToArray());
                    else if (s.StartsWith("http://ns.adobe.com/xmp/extension/\0"u8) && s.Length > 35 + 40)
                    {
                        var guid = Encoding.ASCII.GetString(s.Slice(35, 32));
                        int total = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(s.Slice(67)), MaxInflated);
                        int off = (int)BinaryPrimitives.ReadUInt32BigEndian(s.Slice(71));
                        if (!ext.TryGetValue(guid, out var e)) ext[guid] = e = (total, new SortedDictionary<int, byte[]>());
                        e.parts[off] = s.Slice(75).ToArray();
                    }
                }
                else if (m == 0xED && s.StartsWith("Photoshop 3.0\0"u8))
                {
                    var iptc = IptcData.FromPhotoshopResources(s.Slice(14));
                    if (!iptc.IsEmpty) b.Iptc ??= iptc.ToArray();
                }
                else if (m == 0xE2 && s.Length > 14 && s.StartsWith("ICC_PROFILE\0"u8)) icc[s[12]] = s.Slice(14).ToArray();
                else if (m == 0xFE) b.Comments.Add(TextOf(s));
                p += 2 + len;
            }
            if (icc.Count > 0) b.Icc = Concat(icc.Values);
            foreach (var (_, (total, parts)) in ext)
            {
                var all = new byte[total];
                foreach (var (off, part) in parts)
                    if (off >= 0 && off < total) part.AsSpan(0, Math.Min(part.Length, total - off)).CopyTo(all.AsSpan(off));
                b.Xmp.Add(all);
            }
        }

        static byte[] Concat(IEnumerable<byte[]> parts)
        {
            var o = new List<byte>();
            foreach (var p in parts) o.AddRange(p);
            return o.ToArray();
        }

        static string TextOf(ReadOnlySpan<byte> s)
        {
            int z = s.IndexOf((byte)0);
            if (z >= 0) s = s.Slice(0, z);
            return (System.Text.Unicode.Utf8.IsValid(s) ? Encoding.UTF8 : Encoding.Latin1).GetString(s).TrimEnd();
        }

        // ------------------------------------------------------------------ PNG

        static void Png(ReadOnlySpan<byte> d, MetadataBlocks b)
        {
            int p = 8;
            while (p + 12 <= d.Length)
            {
                long len = BinaryPrimitives.ReadUInt32BigEndian(d.Slice(p));
                if (p + 12 + len > d.Length) break;
                var type = d.Slice(p + 4, 4);
                var s = d.Slice(p + 8, (int)len);
                if (type.SequenceEqual("eXIf"u8)) b.Exif ??= (s.StartsWith("Exif\0\0"u8) ? s.Slice(6) : s).ToArray();
                else if (type.SequenceEqual("iCCP"u8))
                {
                    int z = s.IndexOf((byte)0);
                    if (z > 0 && z + 2 <= s.Length) b.Icc ??= Inflate(s.Slice(z + 2));
                }
                else if (type.SequenceEqual("tEXt"u8) || type.SequenceEqual("zTXt"u8) || type.SequenceEqual("iTXt"u8)) PngText(type, s, b);
                else if (type.SequenceEqual("tIME"u8) && s.Length >= 7)
                    b.Text.Add(new("tIME", $"{BinaryPrimitives.ReadUInt16BigEndian(s):D4}-{s[2]:D2}-{s[3]:D2}T{s[4]:D2}:{s[5]:D2}:{s[6]:D2}Z"));
                else if (type.SequenceEqual("IEND"u8)) break;
                p += 12 + (int)len;
            }
        }

        static void PngText(ReadOnlySpan<byte> type, ReadOnlySpan<byte> s, MetadataBlocks b)
        {
            int z = s.IndexOf((byte)0);
            if (z <= 0) return;
            string key = Encoding.Latin1.GetString(s.Slice(0, z));
            byte[]? value;
            bool utf8 = false;
            if (type[0] == 't') value = s.Slice(z + 1).ToArray();
            else if (type[0] == 'z') value = z + 2 <= s.Length ? Inflate(s.Slice(z + 2)) : null;
            else
            {
                // iTXt: compression flag, method, language\0, translated keyword\0, text
                if (z + 3 > s.Length) return;
                bool compressed = s[z + 1] != 0;
                var rest = s.Slice(z + 3);
                int l1 = rest.IndexOf((byte)0);
                if (l1 < 0) return;
                rest = rest.Slice(l1 + 1);
                int l2 = rest.IndexOf((byte)0);
                if (l2 < 0) return;
                rest = rest.Slice(l2 + 1);
                value = compressed ? Inflate(rest) : rest.ToArray();
                utf8 = true;
            }
            if (value == null) return;
            if (key == "XML:com.adobe.xmp") { b.Xmp.Add(value); return; }
            if (key.StartsWith("Raw profile type ", StringComparison.Ordinal))
            {
                // ImageMagick: "\n<name>\n<length>\n<hex data>"
                var raw = HexProfile(value);
                if (raw == null) return;
                string kind = key.Substring(17).ToLowerInvariant();
                if (kind is "exif" or "app1")
                {
                    var r = raw.AsSpan();
                    if (r.StartsWith("Exif\0\0"u8)) r = r.Slice(6);
                    if (r.Length > 8 && (r[0] == 'I' || r[0] == 'M')) b.Exif ??= r.ToArray();
                }
                else if (kind == "xmp") b.Xmp.Add(raw);
                else if (kind is "iptc" or "8bim")
                {
                    var iptc = IptcData.FromPhotoshopResources(raw);
                    b.Iptc ??= iptc.IsEmpty ? raw : iptc.ToArray();
                }
                else if (kind is "icc" or "icm") b.Icc ??= raw;
                return;
            }
            b.Text.Add(new(key, (utf8 ? Encoding.UTF8 : Encoding.Latin1).GetString(value)));
        }

        static byte[]? HexProfile(byte[] v)
        {
            var s = Encoding.ASCII.GetString(v);
            var parts = s.Split('\n', 4, StringSplitOptions.None);
            if (parts.Length < 4 || !int.TryParse(parts[2].Trim(), out int n) || n <= 0 || n > MaxInflated) return null;
            var o = new byte[n];
            int k = 0, hi = -1;
            foreach (char c in parts[3])
            {
                int x = c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'f' ? c - 'a' + 10 : c is >= 'A' and <= 'F' ? c - 'A' + 10 : -1;
                if (x < 0) continue;
                if (hi < 0) hi = x;
                else { if (k == n) break; o[k++] = (byte)(hi << 4 | x); hi = -1; }
            }
            return k == n ? o : o.AsSpan(0, k).ToArray();
        }

        /// <summary>zlib data, at most 16 MB of output; null when corrupt.</summary>
        internal static byte[]? Inflate(ReadOnlySpan<byte> s)
        {
            for (int size = 64 << 10; ; size *= 4)
            {
                var buf = new byte[Math.Min(size, MaxInflated)];
                int n;
                try { n = Aardvark.ImageCodecs.Compression.Zlib.Decompress(s, buf, false); }
                catch (Exception e) when (e is ImageFormatException or System.IO.InvalidDataException) { return null; }
                if (n < buf.Length || buf.Length == MaxInflated) return buf.AsSpan(0, n).ToArray();
            }
        }

        // ------------------------------------------------------------------ TIFF

        static void Tiff(ReadOnlySpan<byte> d, MetadataBlocks b)
        {
            var ifd = ExifReader.Parse(d, true);
            if (ifd == null) return;
            b.TiffIfd0 = ifd;
            var xmp = ifd.Get(ExifIfd.Image, 700);
            if (xmp != null) b.Xmp.Add(RawBytes(xmp, ifd.BigEndian));
            var iptc = ifd.Get(ExifIfd.Image, 33723);
            if (iptc != null) b.Iptc = RawBytes(iptc, ifd.BigEndian);
            var ps = ifd.Get(ExifIfd.Image, 34377);
            if (ps != null && b.Iptc == null)
            {
                var r = IptcData.FromPhotoshopResources(RawBytes(ps, ifd.BigEndian));
                if (!r.IsEmpty) b.Iptc = r.ToArray();
            }
            var icc = ifd.Get(ExifIfd.Image, 34675);
            if (icc != null) b.Icc = RawBytes(icc, ifd.BigEndian);
        }

        /// <summary>The field's bytes as stored (IPTC and XMP blocks are often typed LONG or BYTE).</summary>
        static byte[] RawBytes(ExifEntry e, bool be)
        {
            switch (e.Value)
            {
                case byte[] x: return x;
                case string s: return Encoding.UTF8.GetBytes(s);
                case uint[] u:
                    {
                        var o = new byte[u.Length * 4];
                        for (int i = 0; i < u.Length; i++)
                            if (be) BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(i * 4), u[i]); else BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(i * 4), u[i]);
                        return o;
                    }
                case sbyte[] sb: return Array.ConvertAll(sb, v => (byte)v);
                default: return Array.Empty<byte>();
            }
        }

        // ------------------------------------------------------------------ WebP

        static void Webp(ReadOnlySpan<byte> d, MetadataBlocks b)
        {
            int p = 12;
            while (p + 8 <= d.Length)
            {
                long len = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(p + 4));
                if (p + 8 + len > d.Length) len = d.Length - p - 8;
                var id = d.Slice(p, 4);
                var s = d.Slice(p + 8, (int)len);
                if (id.SequenceEqual("EXIF"u8)) b.Exif ??= (s.StartsWith("Exif\0\0"u8) ? s.Slice(6) : s).ToArray();
                else if (id.SequenceEqual("XMP "u8)) b.Xmp.Add(s.ToArray());
                else if (id.SequenceEqual("ICCP"u8)) b.Icc ??= s.ToArray();
                p += 8 + (int)((len + 1) & ~1L);
            }
        }

        // ------------------------------------------------------------------ GIF

        static void Gif(ReadOnlySpan<byte> d, MetadataBlocks b)
        {
            if (d.Length < 13) return;
            int p = 13;
            if ((d[10] & 0x80) != 0) p += 3 << ((d[10] & 7) + 1);
            while (p < d.Length)
            {
                int k = d[p];
                if (k == 0x3B) break;
                if (k == 0x21 && p + 2 <= d.Length)
                {
                    int label = d[p + 1];
                    int q = p + 2;
                    int start = q;
                    var data = new List<byte>();
                    while (q < d.Length && d[q] != 0)
                    {
                        int n = d[q];
                        if (q + 1 + n > d.Length) return;
                        data.AddRange(d.Slice(q + 1, n).ToArray());
                        q += 1 + n;
                    }
                    var raw = d.Slice(start, Math.Min(q, d.Length) - start);
                    q++;
                    if (label == 0xFE) b.Comments.Add(TextOf(data.ToArray()));
                    else if (label == 0xFF && data.Count >= 11)
                    {
                        var app = Encoding.ASCII.GetString(data.GetRange(0, 11).ToArray());
                        if (app == "XMP DataXMP")
                        {
                            // the packet is stored raw (its bytes double as sub-block lengths), followed by a "magic trailer"
                            var x = raw.Slice(12);
                            int endTag = x.IndexOf("<?xpacket end"u8);
                            if (endTag >= 0)
                            {
                                int close = x.Slice(endTag).IndexOf("?>"u8);
                                if (close >= 0) x = x.Slice(0, endTag + close + 2);
                            }
                            b.Xmp.Add(x.ToArray());
                        }
                    }
                    p = q;
                }
                else if (k == 0x2C && p + 10 <= d.Length)
                {
                    int q = p + 10;
                    if ((d[p + 9] & 0x80) != 0) q += 3 << ((d[p + 9] & 7) + 1);
                    q++;   // LZW minimum code size
                    while (q < d.Length && d[q] != 0) q += 1 + d[q];
                    p = q + 1;
                }
                else break;
            }
        }

        // ------------------------------------------------------------------ JPEG 2000

        static readonly byte[] UuidXmp = { 0xBE, 0x7A, 0xCF, 0xCB, 0x97, 0xA9, 0x42, 0xE8, 0x9C, 0x71, 0x99, 0x94, 0x91, 0xE3, 0xAF, 0xAC };
        static readonly byte[] UuidIptc = { 0x33, 0xC7, 0xA4, 0xD2, 0xB8, 0x1D, 0x47, 0x23, 0xA0, 0xBA, 0xF1, 0xA3, 0xE0, 0x97, 0xAD, 0x38 };

        static void Jp2(ReadOnlySpan<byte> d, MetadataBlocks b)
        {
            if (d.Length >= 4 && d[0] == 0xFF && d[1] == 0x4F) { J2kComments(d, b); return; }
            Boxes(d, b, 0);
        }

        static void Boxes(ReadOnlySpan<byte> d, MetadataBlocks b, int depth)
        {
            int p = 0;
            while (p + 8 <= d.Length)
            {
                long len = BinaryPrimitives.ReadUInt32BigEndian(d.Slice(p));
                var type = d.Slice(p + 4, 4);
                int head = 8;
                if (len == 1 && p + 16 <= d.Length) { len = (long)BinaryPrimitives.ReadUInt64BigEndian(d.Slice(p + 8)); head = 16; }
                else if (len == 0) len = d.Length - p;
                if (len < head || p + len > d.Length) len = d.Length - p;
                var s = d.Slice(p + head, (int)len - head);
                if (type.SequenceEqual("uuid"u8) && s.Length >= 16)
                {
                    var id = s.Slice(0, 16);
                    var body = s.Slice(16);
                    if (id.SequenceEqual(UuidXmp)) b.Xmp.Add(body.ToArray());
                    else if (id.SequenceEqual("JpgTiffExif->JP2"u8)) b.Exif ??= (body.StartsWith("Exif\0\0"u8) ? body.Slice(6) : body).ToArray();
                    else if (id.SequenceEqual(UuidIptc)) b.Iptc ??= body.ToArray();
                }
                else if (type.SequenceEqual("xml "u8)) b.Xmp.Add(s.ToArray());
                else if (type.SequenceEqual("jp2h"u8) && depth == 0) Boxes(s, b, 1);
                else if (type.SequenceEqual("colr"u8) && s.Length > 3 && s[0] == 2) b.Icc ??= s.Slice(3).ToArray();
                else if (type.SequenceEqual("jp2c"u8)) J2kComments(s, b);
                p += (int)len;
            }
        }

        static void J2kComments(ReadOnlySpan<byte> d, MetadataBlocks b)
        {
            int p = 2;
            while (p + 4 <= d.Length && d[p] == 0xFF)
            {
                int m = d[p + 1];
                if (m == 0x90 || m == 0x93) break;   // tile part: main header done
                int len = BinaryPrimitives.ReadUInt16BigEndian(d.Slice(p + 2));
                if (len < 2 || p + 2 + len > d.Length) break;
                if (m == 0x64 && len > 4 && BinaryPrimitives.ReadUInt16BigEndian(d.Slice(p + 4)) == 1) b.Comments.Add(TextOf(d.Slice(p + 6, len - 4)));
                p += 2 + len;
            }
        }
    }
}
