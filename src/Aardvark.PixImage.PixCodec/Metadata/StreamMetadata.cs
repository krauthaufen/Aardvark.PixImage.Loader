using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Aardvark.ImageCodecs
{
    /// <summary>
    /// Collects the metadata of an image in a stream without reading its pixel data: chunk based formats are walked by
    /// seeking past image chunks, TIFF directories are compacted into a small TIFF holding just the tags (with their
    /// values), JPEG is read up to its scan. The result is a small file of the same format for <see cref="MetadataReader"/>.
    /// </summary>
    internal static class StreamMetadata
    {
        const int MaxBlock = 16 << 20;

        /// <summary><paramref name="prefix"/>: what was already read from <paramref name="start"/> on.</summary>
        public static byte[] Collect(Stream s, long start, ImageFormat format, byte[] prefix)
        {
            if (!s.CanSeek)
                return format is ImageFormat.Jpeg or ImageFormat.JpegLs ? JpegForward(s, prefix) : prefix;
            switch (format)
            {
                case ImageFormat.Jpeg: case ImageFormat.JpegLs: s.Position = start + prefix.Length; return JpegForward(s, prefix);
                case ImageFormat.Png: return Chunks(s, start, 8, png: true);
                case ImageFormat.Webp: return Chunks(s, start, 12, png: false);
                case ImageFormat.Jp2: return Boxes(s, start, prefix);
                case ImageFormat.Tiff: return Tiff(s, start) ?? prefix;
                default: return prefix;
            }
        }

        static int ReadFully(Stream s, Span<byte> b)
        {
            int n = 0;
            while (n < b.Length)
            {
                int k = s.Read(b.Slice(n));
                if (k <= 0) break;
                n += k;
            }
            return n;
        }

        static byte[] ReadAt(Stream s, long pos, int length)
        {
            s.Position = pos;
            var b = new byte[length];
            int n = ReadFully(s, b);
            return n == length ? b : b.AsSpan(0, n).ToArray();
        }

        /// <summary>JPEG: extend the prefix up to the first scan (metadata segments precede it).</summary>
        static byte[] JpegForward(Stream s, byte[] prefix)
        {
            var o = new List<byte>(prefix);
            int p = 2;
            while (true)
            {
                while (p + 4 > o.Count) if (!More(s, o)) return o.ToArray();
                if (o[p] != 0xFF) { p++; continue; }
                int m = o[p + 1];
                if (m == 0xFF) { p++; continue; }
                if (m == 0xD8 || m is >= 0xD0 and <= 0xD7 || m == 0x01) { p += 2; continue; }
                if (m == 0xDA || m == 0xD9) return o.ToArray();
                int len = o[p + 2] << 8 | o[p + 3];
                p += 2 + len;
                while (p > o.Count) if (!More(s, o)) return o.ToArray();
                if (o.Count > 64 << 20) return o.ToArray();
            }
        }

        static bool More(Stream s, List<byte> o)
        {
            var b = new byte[65536];
            int n = s.Read(b, 0, b.Length);
            if (n <= 0) return false;
            o.AddRange(b.AsSpan(0, n).ToArray());
            return true;
        }

        /// <summary>PNG / WebP: the file with the image data chunks left out.</summary>
        static byte[] Chunks(Stream s, long start, int headerSize, bool png)
        {
            var o = new MemoryStream();
            o.Write(ReadAt(s, start, headerSize));
            long pos = start + headerSize, end = s.Length;
            var h = new byte[8];
            while (pos + 8 <= end)
            {
                s.Position = pos;
                if (ReadFully(s, h) < 8) break;
                long len = png ? BinaryPrimitives.ReadUInt32BigEndian(h) : BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4));
                var type = png ? h.AsSpan(4, 4) : h.AsSpan(0, 4);
                long total = png ? 12 + len : 8 + ((len + 1) & ~1L);
                bool image = png ? type.SequenceEqual("IDAT"u8) || type.SequenceEqual("fdAT"u8)
                    : type.SequenceEqual("VP8 "u8) || type.SequenceEqual("VP8L"u8) || type.SequenceEqual("ANMF"u8) || type.SequenceEqual("ALPH"u8);
                if (!image && len <= MaxBlock) o.Write(ReadAt(s, pos, (int)Math.Min(total, end - pos)));
                if (png && type.SequenceEqual("IEND"u8)) break;
                pos += total;
            }
            return o.ToArray();
        }

        /// <summary>JPEG 2000: the boxes without the codestream (its header comments come from the prefix).</summary>
        static byte[] Boxes(Stream s, long start, byte[] prefix)
        {
            if (prefix.Length >= 2 && prefix[0] == 0xFF && prefix[1] == 0x4F) return prefix;   // raw codestream
            var o = new MemoryStream();
            long pos = start, end = s.Length;
            var h = new byte[16];
            while (pos + 8 <= end)
            {
                s.Position = pos;
                int got = ReadFully(s, h);
                if (got < 8) break;
                long len = BinaryPrimitives.ReadUInt32BigEndian(h);
                if (len == 1 && got >= 16) len = (long)BinaryPrimitives.ReadUInt64BigEndian(h.AsSpan(8));
                else if (len == 0) len = end - pos;
                if (len < 8) break;
                if (!h.AsSpan(4, 4).SequenceEqual("jp2c"u8) && len <= MaxBlock) o.Write(ReadAt(s, pos, (int)Math.Min(len, end - pos)));
                pos += len;
            }
            return o.ToArray();
        }

        // ------------------------------------------------------------------ TIFF

        /// <summary>
        /// A classic TIFF of the same byte order holding the first page's directory and its EXIF, GPS and interoperability
        /// directories with their values (strip / tile offset arrays and values over 16 MB left out).
        /// </summary>
        static byte[]? Tiff(Stream s, long start)
        {
            var head = ReadAt(s, start, 16);
            if (head.Length < 8) return null;
            bool be = head[0] == 'M';
            int magic = U16(head, 2, be);
            bool big = magic == 43;
            long first = big ? (head.Length >= 16 ? (long)U64(head, 8, be) : 0) : U32(head, 4, be);
            var w = new TiffWriter(be);
            var ifd0 = ReadIfd(s, start, first, be, big);
            if (ifd0 == null) return null;
            var exif = Pointer(ifd0, 0x8769) is long e ? ReadIfd(s, start, e, be, big) : null;
            var gps = Pointer(ifd0, 0x8825) is long g ? ReadIfd(s, start, g, be, big) : null;
            var interop = exif != null && Pointer(exif, 0xA005) is long i ? ReadIfd(s, start, i, be, big) : null;
            return w.Write(ifd0, exif, gps, interop);
        }

        sealed record Field(ushort Tag, ushort Type, uint Count, byte[] Value);

        static long? Pointer(List<Field> ifd, ushort tag)
        {
            foreach (var f in ifd)
                if (f.Tag == tag && f.Type == 16)
                    return (long)BinaryPrimitives.ReadUInt64LittleEndian(f.Value);
            return null;
        }

        static int TypeSize(int t) => t switch { 1 or 2 or 6 or 7 or 129 => 1, 3 or 8 => 2, 4 or 9 or 11 or 13 => 4, 5 or 10 or 12 or 16 or 17 or 18 => 8, _ => 0 };

        /// <summary>Reads a directory; values are kept in file byte order. Pointer values are resolved by the caller.</summary>
        static List<Field>? ReadIfd(Stream s, long start, long off, bool be, bool big)
        {
            if (off <= 0 || off < 0) return null;
            int countSize = big ? 8 : 2, entrySize = big ? 20 : 12, inline = big ? 8 : 4;
            var c = ReadAt(s, start + off, countSize);
            if (c.Length < countSize) return null;
            long n = big ? (long)U64(c, 0, be) : U16(c, 0, be);
            if (n is <= 0 or > 4096) return null;
            var entries = ReadAt(s, start + off + countSize, (int)(n * entrySize));
            var r = new List<Field>();
            for (int k = 0; k + entrySize <= entries.Length; k += entrySize)
            {
                ushort tag = (ushort)U16(entries, k, be), type = (ushort)U16(entries, k + 2, be);
                long count = big ? (long)U64(entries, k + 4, be) : U32(entries, k + 4, be);
                int size = TypeSize(type);
                if (size == 0 || count < 0 || count > uint.MaxValue) continue;
                long bytes = count * size;
                // image data locations are of no use here
                if (tag is 273 or 279 or 324 or 325 or 288 or 289 or 330 && bytes > inline) continue;
                if (bytes > MaxBlock) continue;
                byte[] value;
                if (bytes <= inline) value = entries.AsSpan(k + 4 + (big ? 8 : 4), (int)bytes).ToArray();
                else
                {
                    long at = big ? (long)U64(entries, k + 12, be) : U32(entries, k + 8, be);
                    value = ReadAt(s, start + at, (int)bytes);
                    if (value.Length < bytes) continue;
                }
                // directory pointers: resolved to absolute offsets (as little endian 8 bytes, type 16 marks them)
                if (tag is 0x8769 or 0x8825 or 0xA005)
                {
                    long ptr = size == 8 ? (long)U64(value, 0, be) : U32(value, 0, be);
                    var pv = new byte[8];
                    BinaryPrimitives.WriteUInt64LittleEndian(pv, (ulong)ptr);
                    r.Add(new Field(tag, 16, 1, pv));
                    continue;
                }
                // BigTIFF 8 byte integers become 4 byte ones when they fit (classic TIFF has no LONG8)
                if (type is 16 or 17 or 18)
                {
                    var nv = new byte[count * 4];
                    for (int i = 0; i < count; i++)
                    {
                        ulong v = U64(value, i * 8, be);
                        if (be) BinaryPrimitives.WriteUInt32BigEndian(nv.AsSpan(i * 4), (uint)v); else BinaryPrimitives.WriteUInt32LittleEndian(nv.AsSpan(i * 4), (uint)v);
                    }
                    r.Add(new Field(tag, (ushort)(type == 17 ? 9 : 4), (uint)count, nv));
                    continue;
                }
                r.Add(new Field(tag, type, (uint)count, value));
            }
            return r;
        }

        /// <summary>Writes the compacted classic TIFF.</summary>
        sealed class TiffWriter(bool be)
        {
            readonly MemoryStream _o = new();

            void W16(int v) { Span<byte> b = stackalloc byte[2]; if (be) BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); else BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)v); _o.Write(b); }
            void W32(long v) { Span<byte> b = stackalloc byte[4]; if (be) BinaryPrimitives.WriteUInt32BigEndian(b, (uint)v); else BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)v); _o.Write(b); }

            public byte[] Write(List<Field> ifd0, List<Field>? exif, List<Field>? gps, List<Field>? interop)
            {
                _o.Write(be ? "MM"u8 : "II"u8); W16(42); W32(8);
                // layout: each directory followed by its out of line values; pointers patched afterwards
                var patches = new List<(long at, ushort tag)>();
                var placed = new Dictionary<ushort, long>();
                void Dir(List<Field> f, ExifIfd which)
                {
                    long dirPos = _o.Position;
                    var fields = f.FindAll(x => x.Tag != 0xA005 || which == ExifIfd.Exif);
                    fields.Sort((a, b) => a.Tag.CompareTo(b.Tag));
                    W16(fields.Count);
                    long valuePos = dirPos + 2 + fields.Count * 12 + 4;
                    var values = new MemoryStream();
                    foreach (var x in fields)
                    {
                        W16(x.Tag);
                        if (x.Type == 16)   // directory pointer: LONG, patched later
                        {
                            W16(4); W32(1);
                            patches.Add((_o.Position, x.Tag));
                            W32(0);
                            continue;
                        }
                        W16(x.Type); W32(x.Count);
                        if (x.Value.Length <= 4)
                        {
                            _o.Write(x.Value);
                            for (int k = x.Value.Length; k < 4; k++) _o.WriteByte(0);
                        }
                        else
                        {
                            W32(valuePos + values.Length);
                            values.Write(x.Value);
                            if ((values.Length & 1) != 0) values.WriteByte(0);
                        }
                    }
                    W32(0);   // no next directory
                    values.Position = 0;
                    values.CopyTo(_o);
                }
                Dir(ifd0, ExifIfd.Image);
                if (exif != null) { placed[0x8769] = _o.Position; Dir(exif, ExifIfd.Exif); }
                if (gps != null) { placed[0x8825] = _o.Position; Dir(gps, ExifIfd.Gps); }
                if (interop != null) { placed[0xA005] = _o.Position; Dir(interop, ExifIfd.Interop); }
                foreach (var (at, tag) in patches)
                {
                    _o.Position = at;
                    W32(placed.TryGetValue(tag, out var p) ? p : 0);
                }
                return _o.ToArray();
            }
        }

        static int U16(byte[] d, int o, bool be) => be ? BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(o)) : BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o));
        static uint U32(byte[] d, int o, bool be) => be ? BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(o)) : BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));
        static ulong U64(byte[] d, int o, bool be) => be ? BinaryPrimitives.ReadUInt64BigEndian(d.AsSpan(o)) : BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(o));
    }
}
