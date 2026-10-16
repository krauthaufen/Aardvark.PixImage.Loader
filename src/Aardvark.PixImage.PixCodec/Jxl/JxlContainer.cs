using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Aardvark.ImageCodecs.JpegXl
{
    /// <summary>A box of the JPEG XL container (ISO/IEC 18181-2).</summary>
    internal readonly record struct JxlBox(string Type, int Offset, int Length, int HeaderSize);

    /// <summary>
    /// The ISOBMFF style container of JPEG XL: signature box, ftyp, jxll, jxlc / jxlp codestream boxes and metadata boxes
    /// (Exif, xml, jumb, possibly Brotli compressed as brob).
    /// </summary>
    internal sealed class JxlContainer
    {
        public static ReadOnlySpan<byte> Signature => new byte[] { 0, 0, 0, 0x0C, (byte)'J', (byte)'X', (byte)'L', (byte)' ', 0x0D, 0x0A, 0x87, 0x0A };

        public bool IsContainer;
        public readonly List<JxlBox> Boxes = new();
        /// <summary>Offset and length of the codestream parts, in order (one part for a bare codestream or a jxlc box).</summary>
        public readonly List<(int Offset, int Length)> Parts = new();
        public int Level = 5;
        /// <summary>True when the last jxlp box was found (or the codestream is in one piece).</summary>
        public bool Complete;

        public static bool IsBareCodestream(ReadOnlySpan<byte> d) => d.Length >= 2 && d[0] == 0xFF && d[1] == 0x0A;

        public static bool IsMatch(ReadOnlySpan<byte> d) => IsBareCodestream(d) || d.Length >= 12 && d.Slice(0, 12).SequenceEqual(Signature);

        public static JxlContainer Parse(ReadOnlySpan<byte> d)
        {
            var c = new JxlContainer();
            if (IsBareCodestream(d))
            {
                c.Parts.Add((0, d.Length));
                c.Complete = true;
                return c;
            }
            if (!(d.Length >= 12 && d.Slice(0, 12).SequenceEqual(Signature))) throw new ImageFormatException("JPEG XL: not a JPEG XL file");
            c.IsContainer = true;
            int p = 0;
            var jxlp = new List<(uint idx, int off, int len)>();
            bool sawJxlc = false;
            while (p + 8 <= d.Length)
            {
                long len = BinaryPrimitives.ReadUInt32BigEndian(d.Slice(p));
                string type = System.Text.Encoding.ASCII.GetString(d.Slice(p + 4, 4));
                int head = 8;
                if (len == 1)
                {
                    if (p + 16 > d.Length) break;
                    ulong l64 = BinaryPrimitives.ReadUInt64BigEndian(d.Slice(p + 8));
                    len = l64 > int.MaxValue ? long.MaxValue : (long)l64;
                    head = 16;
                }
                else if (len == 0) len = d.Length - p;
                if (len < head) throw new ImageFormatException("JPEG XL: bad box size");
                bool truncated = p + len > d.Length;
                int bodyLen = (int)Math.Min(len - head, d.Length - p - head);
                c.Boxes.Add(new JxlBox(type, p + head, bodyLen, head));
                switch (type)
                {
                    case "jxlc":
                        if (sawJxlc || jxlp.Count > 0) throw new ImageFormatException("JPEG XL: more than one codestream box");
                        sawJxlc = true;
                        c.Parts.Add((p + head, bodyLen));
                        c.Complete = !truncated;
                        break;
                    case "jxlp":
                        if (sawJxlc) throw new ImageFormatException("JPEG XL: jxlp next to jxlc");
                        if (bodyLen >= 4)
                        {
                            uint idx = BinaryPrimitives.ReadUInt32BigEndian(d.Slice(p + head));
                            jxlp.Add((idx, p + head + 4, bodyLen - 4));
                            if ((idx & 0x80000000) != 0 && !truncated) c.Complete = true;
                        }
                        break;
                    case "jxll":
                        if (bodyLen >= 1) c.Level = d[p + head];
                        break;
                }
                if (truncated) break;
                p += (int)len;
            }
            if (jxlp.Count > 0)
            {
                for (int i = 0; i < jxlp.Count; i++)
                {
                    if ((jxlp[i].idx & 0x7FFFFFFF) != (uint)i) throw new ImageFormatException("JPEG XL: jxlp boxes out of order");
                    c.Parts.Add((jxlp[i].off, jxlp[i].len));
                }
            }
            if (c.Parts.Count == 0) throw new ImageFormatException("JPEG XL: container without a codestream");
            return c;
        }

        /// <summary>The codestream in one array (a copy).</summary>
        public byte[] Codestream(ReadOnlySpan<byte> d)
        {
            long total = 0;
            foreach (var (_, l) in Parts) total += l;
            if (total > Array.MaxLength) throw new ImageNotSupportedException("JPEG XL: codestream too large");
            var r = new byte[total];
            int o = 0;
            foreach (var (off, l) in Parts) { d.Slice(off, l).CopyTo(r.AsSpan(o)); o += l; }
            return r;
        }

        /// <summary>The first codestream bytes (up to max), enough to parse the headers.</summary>
        public byte[] CodestreamPrefix(ReadOnlySpan<byte> d, int max)
        {
            var ms = new MemoryStream();
            foreach (var (off, l) in Parts)
            {
                int take = (int)Math.Min(l, max - ms.Length);
                if (take <= 0) break;
                ms.Write(d.Slice(off, take));
            }
            return ms.ToArray();
        }

        /// <summary>Contents of a metadata box ("Exif", "xml ", "jumb"), Brotli compressed boxes (brob) included.</summary>
        public List<byte[]> Metadata(ReadOnlySpan<byte> d, string type)
        {
            var r = new List<byte[]>();
            foreach (var b in Boxes)
            {
                if (b.Type == type) r.Add(d.Slice(b.Offset, b.Length).ToArray());
                else if (b.Type == "brob" && b.Length >= 4 && System.Text.Encoding.ASCII.GetString(d.Slice(b.Offset, 4)) == type)
                {
                    try { r.Add(Compression.Brotli.Decompress(d.Slice(b.Offset + 4, b.Length - 4), 64 << 20)); }
                    catch (ImageFormatException) { }
                }
            }
            return r;
        }

        /// <summary>The TIFF structure of an Exif box (whose payload starts with a 4 byte offset to the TIFF header).</summary>
        public static byte[]? ExifTiff(byte[] box)
        {
            if (box.Length < 4) return null;
            uint off = BinaryPrimitives.ReadUInt32BigEndian(box);
            if (off > box.Length - 4) return null;
            return box.AsSpan(4 + (int)off).ToArray();
        }
    }
}
