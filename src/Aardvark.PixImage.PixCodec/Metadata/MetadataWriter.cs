using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Aardvark.ImageCodecs.Compression;

namespace Aardvark.ImageCodecs
{
    public static partial class ImageCodec
    {
        /// <summary>
        /// Rewrites the metadata of an encoded image (JPEG, PNG, TIFF / BigTIFF, WebP, JPEG 2000 JP2 / JPX) without
        /// re-encoding: the compressed pixel data stays byte for byte as it is. Returns the new file.
        /// </summary>
        /// <exception cref="ImageNotSupportedException">The format has no standard place for EXIF / XMP (BMP, TGA, GIF,
        /// OpenEXR, DDS, raw JPEG 2000 codestreams, ...), or a block exceeds what the container allows.</exception>
        public static byte[] WriteMetadata(ReadOnlySpan<byte> data, MetadataEdit edit)
        {
            if (edit == null) throw new ArgumentNullException(nameof(edit));
            var plan = MetadataPlan.Build(edit);
            XmpEditor.CheckPrefixes(plan);
            var format = DetectFormat(data);
            switch (format)
            {
                case ImageFormat.Jpeg: return MetadataWriter.Jpeg(data, plan);
                case ImageFormat.Png: return MetadataWriter.Png(data, plan);
                case ImageFormat.Webp: return MetadataWriter.Webp(data, plan);
                case ImageFormat.Jp2: return MetadataWriter.Jp2(data, plan);
                case ImageFormat.Tiff:
                {
                    var bytes = data.ToArray();
                    var src = new ArraySource(bytes);
                    var c = TiffMetadataWriter.Plan(src, plan);
                    if (c.IsEmpty) return bytes;
                    var o = new MemoryStream(bytes.Length + c.Tail.Length);
                    TiffMetadataWriter.Write(src, c, o);
                    return o.ToArray();
                }
                default: throw MetadataWriter.Unsupported(format);
            }
        }

        /// <inheritdoc cref="WriteMetadata(ReadOnlySpan{byte}, MetadataEdit)"/>
        /// <remarks>A seekable TIFF input is not read into memory: it is copied with the changed bytes patched in.</remarks>
        public static void WriteMetadata(Stream input, Stream output, MetadataEdit edit)
        {
            if (edit == null) throw new ArgumentNullException(nameof(edit));
            if (input.CanSeek)
            {
                long start = input.Position;
                var head = new byte[16];
                int n = input.Read(head, 0, head.Length);
                input.Position = start;
                if (DetectFormat(head.AsSpan(0, n)) == ImageFormat.Tiff)
                {
                    var plan = MetadataPlan.Build(edit);
                    XmpEditor.CheckPrefixes(plan);
                    var src = new StreamSource(input);
                    TiffMetadataWriter.Write(src, TiffMetadataWriter.Plan(src, plan), output);
                    return;
                }
            }
            output.Write(WriteMetadata(ReadAll(input), edit));
        }

        /// <inheritdoc cref="WriteMetadata(ReadOnlySpan{byte}, MetadataEdit)"/>
        /// <remarks>
        /// <paramref name="outputPath"/> may be <paramref name="inputPath"/>. A TIFF file is then changed in place (the new
        /// directories are appended before the header is switched to them), other formats are written to a temporary file
        /// that replaces the original.
        /// </remarks>
        public static void WriteMetadata(string inputPath, string outputPath, MetadataEdit edit)
        {
            if (edit == null) throw new ArgumentNullException(nameof(edit));
            bool same = string.Equals(Path.GetFullPath(inputPath), Path.GetFullPath(outputPath), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            if (same)
            {
                using (var f = new FileStream(inputPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var head = new byte[16];
                    int n = f.Read(head, 0, head.Length);
                    f.Position = 0;
                    if (DetectFormat(head.AsSpan(0, n)) == ImageFormat.Tiff)
                    {
                        var plan = MetadataPlan.Build(edit);
                        XmpEditor.CheckPrefixes(plan);
                        var c = TiffMetadataWriter.Plan(new StreamSource(f), plan);
                        if (!c.IsEmpty) TiffMetadataWriter.WriteInPlace(f, c);
                        return;
                    }
                }
                var tmp = outputPath + ".pixcodec-tmp";
                try
                {
                    using (var i = File.OpenRead(inputPath))
                    using (var o = File.Create(tmp)) WriteMetadata(i, o, edit);
                    File.Move(tmp, outputPath, true);
                }
                finally { if (File.Exists(tmp)) File.Delete(tmp); }
                return;
            }
            using (var i = File.OpenRead(inputPath))
            using (var o = File.Create(outputPath)) WriteMetadata(i, o, edit);
        }
    }

    /// <summary>Lossless metadata rewriting of the chunk / segment / box based formats.</summary>
    internal static class MetadataWriter
    {
        public static ImageNotSupportedException Unsupported(ImageFormat f) => new(f switch
        {
            ImageFormat.Unknown => "unknown image format",
            ImageFormat.Bmp or ImageFormat.Tga or ImageFormat.Pcx or ImageFormat.Dcx or ImageFormat.Sgi or ImageFormat.SunRaster
                or ImageFormat.Xbm or ImageFormat.Xpm or ImageFormat.Wbmp => $"{f}: the format has no standard place for EXIF / XMP metadata",
            _ => $"{f}: writing metadata into existing files is not supported for this format",
        });

        // ------------------------------------------------------------------ JPEG

        sealed class Segment
        {
            public int Marker, Start, Length;   // Length: marker included
            public byte[]? Replacement;          // new bytes for the whole segment (marker included); empty: drop
            public List<byte[]>? After;          // new segments (complete) to write behind this one
        }

        static byte[] JpegSegment(int marker, ReadOnlySpan<byte> payload)
        {
            if (payload.Length > 65533) throw new ImageNotSupportedException($"JPEG: segment of {payload.Length} bytes exceeds 64 KB");
            var b = new byte[payload.Length + 4];
            b[0] = 0xFF; b[1] = (byte)marker;
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), (ushort)(payload.Length + 2));
            payload.CopyTo(b.AsSpan(4));
            return b;
        }

        static byte[] Cat(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            var r = new byte[a.Length + b.Length];
            a.CopyTo(r); b.CopyTo(r.AsSpan(a.Length));
            return r;
        }

        public static byte[] Jpeg(ReadOnlySpan<byte> d, MetadataPlan plan)
        {
            if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) throw new ImageFormatException("JPEG: no SOI marker");
            var segs = new List<Segment>();
            int p = 2, sos = -1;
            while (p + 4 <= d.Length)
            {
                if (d[p] != 0xFF) throw new ImageFormatException("JPEG: marker expected");
                int m = d[p + 1];
                if (m == 0xFF) { p++; continue; }   // fill byte
                if (m == 0xDA) { sos = p; break; }
                if (m is 0xD8 or 0xD9 or 0x01 or (>= 0xD0 and <= 0xD7)) throw new ImageFormatException("JPEG: unexpected marker before the scan");
                int len = BinaryPrimitives.ReadUInt16BigEndian(d.Slice(p + 2));
                if (len < 2 || p + 2 + len > d.Length) throw new ImageFormatException("JPEG: segment exceeds the file");
                segs.Add(new Segment { Marker = m, Start = p, Length = len + 2 });
                p += 2 + len;
            }
            if (sos < 0) throw new ImageFormatException("JPEG: no scan");
            ReadOnlySpan<byte> Payload(Segment s, ReadOnlySpan<byte> data) => data.Slice(s.Start + 4, s.Length - 4);

            Segment? exifSeg = null, xmpSeg = null;
            var extSegs = new List<Segment>();
            var app13 = new List<Segment>();
            var ctx = new ExifContext { Jpeg = true };
            Segment? mpf = null;
            foreach (var s in segs)
            {
                var pl = Payload(s, d);
                if (s.Marker == 0xE1)
                {
                    if (pl.StartsWith("Exif\0"u8)) { if (exifSeg == null) exifSeg = s; else s.Replacement = plan.HasExifChanges ? Array.Empty<byte>() : null; }
                    else if (pl.StartsWith(XmpEditor.JpegStandardId)) { if (xmpSeg == null) xmpSeg = s; }
                    else if (pl.StartsWith(XmpEditor.JpegExtendedId)) extSegs.Add(s);
                }
                else if (s.Marker == 0xED && pl.StartsWith("Photoshop 3.0\0"u8)) app13.Add(s);
                else if (s.Marker == 0xE2 && pl.StartsWith("ICC_PROFILE\0"u8)) ctx.HasIcc = true;
                else if (s.Marker == 0xE2 && pl.StartsWith("MPF\0"u8)) mpf ??= s;
                else if (s.Marker == 0xE0 && pl.Length >= 12 && pl.StartsWith("JFIF\0"u8) && pl[7] == 1)
                {
                    ctx.DpiX = BinaryPrimitives.ReadUInt16BigEndian(pl.Slice(8)); ctx.DpiY = BinaryPrimitives.ReadUInt16BigEndian(pl.Slice(10));
                }
                else if (s.Marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC && pl.Length >= 5)
                {
                    ctx.Height = BinaryPrimitives.ReadUInt16BigEndian(pl.Slice(1)); ctx.Width = BinaryPrimitives.ReadUInt16BigEndian(pl.Slice(3));
                }
            }

            // where new segments go: after the leading APP0 segments (JFIF / JFXX)
            int insertAt = 0;
            while (insertAt < segs.Count && segs[insertAt].Marker == 0xE0) insertAt++;
            var newSegs = new List<byte[]>();

            if (plan.HasExifChanges)
            {
                byte[]? old = exifSeg != null ? Payload(exifSeg, d).Slice(Payload(exifSeg, d).Length > 5 && Payload(exifSeg, d)[5] == 0 ? 6 : 5).ToArray() : null;
                var blob = ExifEdit.EditBlob(old, plan, ctx);
                if (blob != null && blob.Length > 65533 - 6)
                    throw new ImageNotSupportedException($"JPEG: the EXIF block ({blob.Length} bytes) does not fit into a segment (remove the thumbnail?)");
                byte[]? seg = blob == null ? null : ReferenceEquals(blob, old) ? null : JpegSegment(0xE1, Cat("Exif\0\0"u8, blob));
                if (exifSeg != null) { if (blob == null) exifSeg.Replacement = Array.Empty<byte>(); else if (seg != null) exifSeg.Replacement = seg; }
                else if (seg != null) newSegs.Add(seg);
            }

            if (plan.HasXmpChanges)
            {
                byte[]? old = null;
                if (xmpSeg != null)
                {
                    old = Payload(xmpSeg, d).Slice(XmpEditor.JpegStandardId.Length).ToArray();
                    var ext = Extended(d, extSegs, old);
                    if (ext != null) old = XmpEditor.MergeExtended(old, ext);
                }
                var x = XmpEditor.Edit(old, plan, out bool changed);
                if (changed)
                {
                    foreach (var s in extSegs) s.Replacement = Array.Empty<byte>();
                    var parts = x == null ? new List<byte[]>() : XmpEditor.JpegSegments(x);
                    var full = parts.ConvertAll(pl => JpegSegment(0xE1, pl));
                    if (xmpSeg != null)
                    {
                        xmpSeg.Replacement = full.Count == 0 ? Array.Empty<byte>() : full[0];
                        if (full.Count > 1) (xmpSeg.After ??= new()).AddRange(full.GetRange(1, full.Count - 1));
                    }
                    else if (exifSeg != null) (exifSeg.After ??= new()).AddRange(full);   // XMP behind EXIF
                    else newSegs.AddRange(full);
                }
            }

            if (plan.HasIptcChanges && app13.Count > 0)
            {
                if (plan.ClearAll) foreach (var s in app13) s.Replacement = Array.Empty<byte>();
                else
                {
                    var res = new MemoryStream();
                    foreach (var s in app13) res.Write(Payload(s, d).Slice(14));
                    var n = IptcEditor.EditPhotoshop(res.ToArray(), plan, out bool changed);
                    if (changed)
                    {
                        foreach (var s in app13) s.Replacement = Array.Empty<byte>();
                        if (n != null)
                        {
                            var parts = new List<byte[]>();
                            for (int off = 0; off < n.Length; off += 65533 - 14)
                                parts.Add(JpegSegment(0xED, Cat("Photoshop 3.0\0"u8, n.AsSpan(off, Math.Min(65533 - 14, n.Length - off)))));
                            app13[0].Replacement = parts[0];
                            if (parts.Count > 1) (app13[0].After ??= new()).AddRange(parts.GetRange(1, parts.Count - 1));
                        }
                    }
                }
            }

            if (plan.ClearAll)
                foreach (var s in segs)
                    if (s.Marker == 0xFE || s.Marker == 0xE1 && s.Replacement == null && s != exifSeg && s != xmpSeg) s.Replacement = Array.Empty<byte>();

            // assemble
            var o = new MemoryStream(d.Length + 1024);
            o.Write(d.Slice(0, 2));
            long mpfOld = mpf != null ? mpf.Start + 8 : 0, mpfNew = 0;
            for (int i = 0; i <= segs.Count; i++)
            {
                if (i == insertAt) foreach (var n in newSegs) o.Write(n);
                if (i == segs.Count) break;
                var s = segs[i];
                if (s == mpf) mpfNew = o.Position + 8;
                if (s.Replacement != null) o.Write(s.Replacement);
                else o.Write(d.Slice(s.Start, s.Length));
                if (s.After != null) foreach (var n in s.After) o.Write(n);
            }
            // between the last header segment and the scan: fill bytes, if any
            int lastEnd = segs.Count > 0 ? segs[^1].Start + segs[^1].Length : 2;
            o.Write(d.Slice(lastEnd, sos - lastEnd));
            long sosNew = o.Position;
            o.Write(d.Slice(sos));
            var r = o.ToArray();
            if (mpf != null) FixMpf(r, mpfNew, (sosNew - sos) - (mpfNew - mpfOld));
            return r;
        }

        /// <summary>The extended XMP part that the standard packet's xmpNote:HasExtendedXMP names, if complete.</summary>
        static byte[]? Extended(ReadOnlySpan<byte> d, List<Segment> ext, byte[] standard)
        {
            if (ext.Count == 0) return null;
            var x = XmpData.Parse(standard);
            var guid = x?["xmpNote:HasExtendedXMP"];
            if (guid == null || guid.Length != 32) return null;
            byte[]? all = null;
            foreach (var s in ext)
            {
                var pl = d.Slice(s.Start + 4, s.Length - 4);
                if (pl.Length < 75 || Encoding.ASCII.GetString(pl.Slice(35, 32)) != guid) continue;
                long total = BinaryPrimitives.ReadUInt32BigEndian(pl.Slice(67)), off = BinaryPrimitives.ReadUInt32BigEndian(pl.Slice(71));
                if (total > 64 << 20) return null;
                all ??= new byte[total];
                if (all.Length != total || off + pl.Length - 75 > total) return null;
                pl.Slice(75).CopyTo(all.AsSpan((int)off));
            }
            return all;
        }

        /// <summary>
        /// CIPA DC-007 multi-picture format: the offsets of the following images count from the MP header, so they change
        /// when the distance between the header and the end of the first image changes.
        /// </summary>
        static void FixMpf(byte[] f, long header, long delta)
        {
            if (delta == 0 || header + 8 > f.Length) return;
            var t = f.AsSpan((int)header);
            bool be = t[0] == 'M';
            if (!(be || t[0] == 'I')) return;
            uint U32(ReadOnlySpan<byte> s) => be ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s);
            int U16(ReadOnlySpan<byte> s) => be ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s);
            long ifd = U32(t.Slice(4));
            if (ifd + 2 > t.Length) return;
            int n = U16(t.Slice((int)ifd));
            for (int k = 0; k < n; k++)
            {
                long e = ifd + 2 + k * 12;
                if (e + 12 > t.Length) return;
                if (U16(t.Slice((int)e)) != 0xB002) continue;
                long count = U32(t.Slice((int)e + 4)), at = U32(t.Slice((int)e + 8));
                if (at + count > t.Length) return;
                for (long i = 0; i + 16 <= count; i += 16)
                {
                    var off = t.Slice((int)(at + i + 8), 4);
                    long v = U32(off);
                    if (v == 0) continue;   // the first image
                    uint nv = (uint)(v + delta);
                    if (be) BinaryPrimitives.WriteUInt32BigEndian(off, nv); else BinaryPrimitives.WriteUInt32LittleEndian(off, nv);
                }
            }
        }

        // ------------------------------------------------------------------ PNG

        static byte[] PngChunk(string type, ReadOnlySpan<byte> data)
        {
            var b = new byte[12 + data.Length];
            BinaryPrimitives.WriteUInt32BigEndian(b, (uint)data.Length);
            Encoding.ASCII.GetBytes(type, b.AsSpan(4));
            data.CopyTo(b.AsSpan(8));
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8 + data.Length), Zlib.Crc32(b.AsSpan(4, 4 + data.Length)));
            return b;
        }

        static bool IsLatin1(string s) { foreach (var c in s) if (c > 0xFF) return false; return true; }

        /// <summary>A text chunk: tEXt / zTXt when the text is Latin-1 (as the original type), else iTXt.</summary>
        static byte[] PngText(string keyword, string value, string preferType)
        {
            var key = Encoding.Latin1.GetBytes(keyword);
            if (preferType != "iTXt" && IsLatin1(value))
            {
                var v = Encoding.Latin1.GetBytes(value);
                if (preferType == "zTXt") return PngChunk("zTXt", Cat(Cat(key, new byte[] { 0, 0 }), Zlib.Compress(v)));
                return PngChunk("tEXt", Cat(Cat(key, new byte[] { 0 }), v));
            }
            // iTXt: keyword, NUL, compression flag 0, method 0, empty language, NUL, empty translated keyword, NUL, UTF-8 text
            return PngChunk("iTXt", Cat(Cat(key, new byte[] { 0, 0, 0, 0, 0 }), Encoding.UTF8.GetBytes(value)));
        }

        sealed class Chunk { public string Type = ""; public int Start, Length; public byte[]? Replacement; public string Keyword = ""; }

        public static byte[] Png(ReadOnlySpan<byte> d, MetadataPlan plan)
        {
            if (d.Length < 8 || !d.Slice(0, 8).SequenceEqual(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10 })) throw new ImageFormatException("PNG: bad signature");
            var chunks = new List<Chunk>();
            int p = 8, firstIdat = -1;
            while (p + 12 <= d.Length)
            {
                long len = BinaryPrimitives.ReadUInt32BigEndian(d.Slice(p));
                if (len > int.MaxValue || p + 12 + len > d.Length) throw new ImageFormatException("PNG: chunk exceeds the file");
                var c = new Chunk { Type = Encoding.ASCII.GetString(d.Slice(p + 4, 4)), Start = p, Length = (int)len + 12 };
                if (c.Type is "tEXt" or "zTXt" or "iTXt")
                {
                    int z = d.Slice(p + 8, (int)len).IndexOf((byte)0);
                    if (z > 0) c.Keyword = Encoding.Latin1.GetString(d.Slice(p + 8, z));
                }
                if (c.Type is "IDAT" && firstIdat < 0) firstIdat = chunks.Count;
                chunks.Add(c);
                p += c.Length;
                if (c.Type == "IEND") break;
            }
            if (firstIdat < 0) throw new ImageFormatException("PNG: no image data");
            var before = new List<byte[]>();   // new chunks to put in front of the first IDAT

            // EXIF: eXIf, or an ImageMagick raw profile
            if (plan.HasExifChanges)
            {
                byte[]? old = null;
                foreach (var c in chunks)
                {
                    if (c.Type == "eXIf") { var s = PngData(d, c); old ??= (s.StartsWith("Exif\0\0"u8) ? s.Slice(6) : s).ToArray(); c.Replacement = Array.Empty<byte>(); }
                    else if (c.Keyword is "Raw profile type exif" or "Raw profile type APP1")
                    {
                        if (old == null && RawProfile(c, PngData(d, c)) is byte[] raw)
                        {
                            var r = raw.AsSpan();
                            if (r.StartsWith("Exif\0\0"u8)) r = r.Slice(6);
                            if (r.Length > 8 && (r[0] == 'I' || r[0] == 'M')) old = r.ToArray();
                        }
                        c.Replacement = Array.Empty<byte>();
                    }
                }
                var blob = ExifEdit.EditBlob(old, plan, null);
                if (blob != null) before.Add(PngChunk("eXIf", blob));
            }

            if (plan.HasXmpChanges)
            {
                byte[]? old = null;
                var xmpChunks = new List<Chunk>();
                foreach (var c in chunks)
                    if (c.Keyword == "XML:com.adobe.xmp" || c.Keyword == "Raw profile type xmp")
                    {
                        xmpChunks.Add(c);
                        if (old == null) old = c.Keyword == "Raw profile type xmp" ? RawProfile(c, PngData(d, c)) : TextValue(c, PngData(d, c));
                    }
                var x = XmpEditor.Edit(old, plan, out bool changed);
                if (changed)
                {
                    foreach (var c in xmpChunks) c.Replacement = Array.Empty<byte>();
                    if (x != null) before.Add(PngChunk("iTXt", Cat("XML:com.adobe.xmp\0\0\0\0\0"u8, x)));
                }
            }

            if (plan.HasIptcChanges)
                foreach (var c in chunks)
                {
                    if (c.Keyword is not ("Raw profile type iptc" or "Raw profile type 8bim")) continue;
                    if (plan.ClearAll) { c.Replacement = Array.Empty<byte>(); continue; }
                    var raw = RawProfile(c, PngData(d, c));
                    if (raw == null) continue;
                    byte[]? n;
                    if (raw.AsSpan().StartsWith("8BIM"u8)) { n = IptcEditor.EditPhotoshop(raw, plan, out bool ch); if (!ch) continue; }
                    else n = IptcEditor.EditIim(raw, plan);
                    c.Replacement = n == null ? Array.Empty<byte>() : RawProfileChunk(c.Keyword, c.Type, n);
                }

            // descriptive text chunks that already exist
            if (plan.ClearAll)
                foreach (var c in chunks) if (c.Type is "tEXt" or "zTXt" or "iTXt" or "tIME") c.Replacement ??= Array.Empty<byte>();
            foreach (var (key, value) in plan.PngText)
            {
                bool first = true;
                foreach (var c in chunks)
                {
                    if (c.Keyword != key || c.Replacement is { Length: 0 }) continue;
                    c.Replacement = value == null || !first ? Array.Empty<byte>() : PngText(key, value, c.Type);
                    first = false;
                }
            }

            var o = new MemoryStream(d.Length + 1024);
            o.Write(d.Slice(0, 8));
            for (int i = 0; i < chunks.Count; i++)
            {
                if (i == firstIdat) foreach (var b in before) o.Write(b);
                var c = chunks[i];
                if (c.Replacement != null) o.Write(c.Replacement);
                else o.Write(d.Slice(c.Start, c.Length));
            }
            int end = chunks[^1].Start + chunks[^1].Length;
            o.Write(d.Slice(end));
            return o.ToArray();
        }

        static ReadOnlySpan<byte> PngData(ReadOnlySpan<byte> d, Chunk c) => d.Slice(c.Start + 8, c.Length - 12);

        /// <summary>The text of a tEXt / zTXt / iTXt chunk as bytes (UTF-8 for iTXt).</summary>
        static byte[]? TextValue(Chunk c, ReadOnlySpan<byte> s)
        {
            int z = s.IndexOf((byte)0);
            if (z <= 0) return null;
            if (c.Type == "tEXt") return s.Slice(z + 1).ToArray();
            if (c.Type == "zTXt") return z + 2 <= s.Length ? MetadataScanner.Inflate(s.Slice(z + 2)) : null;
            if (z + 3 > s.Length) return null;
            bool compressed = s[z + 1] != 0;
            var rest = s.Slice(z + 3);
            int l1 = rest.IndexOf((byte)0);
            if (l1 < 0) return null;
            rest = rest.Slice(l1 + 1);
            int l2 = rest.IndexOf((byte)0);
            if (l2 < 0) return null;
            rest = rest.Slice(l2 + 1);
            return compressed ? MetadataScanner.Inflate(rest) : rest.ToArray();
        }

        /// <summary>ImageMagick's "Raw profile type" text: "\n&lt;name&gt;\n&lt;length&gt;\n&lt;hex&gt;".</summary>
        static byte[]? RawProfile(Chunk c, ReadOnlySpan<byte> s)
        {
            var v = TextValue(c, s);
            if (v == null) return null;
            var parts = Encoding.ASCII.GetString(v).Split('\n', 4);
            if (parts.Length < 4 || !int.TryParse(parts[2].Trim(), out int n) || n <= 0 || n > 16 << 20) return null;
            var o = new byte[n];
            int k = 0, hi = -1;
            foreach (char ch in parts[3])
            {
                int x = ch is >= '0' and <= '9' ? ch - '0' : ch is >= 'a' and <= 'f' ? ch - 'a' + 10 : ch is >= 'A' and <= 'F' ? ch - 'A' + 10 : -1;
                if (x < 0) continue;
                if (hi < 0) hi = x; else { if (k == n) break; o[k++] = (byte)(hi << 4 | x); hi = -1; }
            }
            return k == n ? o : null;
        }

        static byte[] RawProfileChunk(string keyword, string type, byte[] data)
        {
            var sb = new StringBuilder();
            sb.Append('\n').Append(keyword[17..].ToLowerInvariant()).Append('\n').Append(data.Length.ToString().PadLeft(8)).Append('\n');
            for (int i = 0; i < data.Length; i++)
            {
                sb.Append(data[i].ToString("x2"));
                if (i % 36 == 35) sb.Append('\n');
            }
            sb.Append('\n');
            return PngText(keyword, sb.ToString(), type == "iTXt" ? "zTXt" : type);
        }

        // ------------------------------------------------------------------ WebP

        public static byte[] Webp(ReadOnlySpan<byte> d, MetadataPlan plan)
        {
            var info = WebpContainer.Parse(d, false, true);
            long riffEnd = 8L + BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(4));
            var chunks = new List<(string Id, int Start, int Length)>();
            int p = 12;
            while (p + 8 <= riffEnd && p + 8 <= d.Length)
            {
                long len = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(p + 4));
                long total = 8 + len + (len & 1);
                if (p + 8 + len > d.Length) throw new ImageFormatException("WebP: chunk exceeds the file");
                chunks.Add((Encoding.ASCII.GetString(d.Slice(p, 4)), p, (int)Math.Min(total, d.Length - p)));
                p += (int)total;
            }

            byte[]? exif = null, xmp = null;
            bool exifChanged = false, xmpChanged = false;
            int exifAt = chunks.FindIndex(c => c.Id == "EXIF"), xmpAt = chunks.FindIndex(c => c.Id == "XMP ");
            if (plan.HasExifChanges)
            {
                byte[]? old = null;
                if (exifAt >= 0) { var s = WebpPayload(d, chunks[exifAt]); old = (s.StartsWith("Exif\0\0"u8) ? s.Slice(6) : s).ToArray(); }
                exif = ExifEdit.EditBlob(old, plan, null);
                exifChanged = !ReferenceEquals(exif, old) || exifAt >= 0 && chunks.FindLastIndex(c => c.Id == "EXIF") != exifAt;
            }
            if (plan.HasXmpChanges)
            {
                byte[]? old = xmpAt >= 0 ? WebpPayload(d, chunks[xmpAt]).ToArray() : null;
                xmp = XmpEditor.Edit(old, plan, out xmpChanged);
            }
            if (!exifChanged && !xmpChanged) return d.ToArray();
            if (!exifChanged && exifAt >= 0) exif = WebpPayload(d, chunks[exifAt]).ToArray();
            if (!xmpChanged && xmpAt >= 0) xmp = WebpPayload(d, chunks[xmpAt]).ToArray();

            var outChunks = new List<byte[]>();
            static byte[] Chunk(string id, ReadOnlySpan<byte> data)
            {
                var b = new byte[8 + data.Length + (data.Length & 1)];
                Encoding.ASCII.GetBytes(id, b);
                BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)data.Length);
                data.CopyTo(b.AsSpan(8));
                return b;
            }
            bool hasVp8x = chunks.Count > 0 && chunks[0].Id == "VP8X";
            int flagsExtra = (exif != null ? 0x08 : 0) | (xmp != null ? 0x04 : 0);
            if (!hasVp8x && flagsExtra != 0)
            {
                var still = info.Frames[0];
                var v = new byte[10];
                v[0] = (byte)(still.HasAlpha ? 0x10 : 0);
                int w = info.Width - 1, h = info.Height - 1;
                v[4] = (byte)w; v[5] = (byte)(w >> 8); v[6] = (byte)(w >> 16);
                v[7] = (byte)h; v[8] = (byte)(h >> 8); v[9] = (byte)(h >> 16);
                outChunks.Add(Chunk("VP8X", v));
            }
            // EXIF and XMP follow the image data (RFC 9649 chunk order), unknown chunks keep their place
            int lastImage = chunks.FindLastIndex(c => c.Id is "VP8 " or "VP8L" or "ALPH" or "ANMF" or "ANIM" or "ICCP" or "VP8X");
            for (int i = 0; i < chunks.Count; i++)
            {
                var c = chunks[i];
                if (c.Id == "VP8X" && i == 0)
                {
                    var v = WebpPayload(d, c).ToArray();
                    v[0] = (byte)(v[0] & ~0x0C | flagsExtra);
                    outChunks.Add(Chunk("VP8X", v));
                }
                else if (c.Id is not ("EXIF" or "XMP ")) outChunks.Add(d.Slice(c.Start, c.Length).ToArray());
                if (i == lastImage)
                {
                    if (exif != null) outChunks.Add(Chunk("EXIF", exif));
                    if (xmp != null) outChunks.Add(Chunk("XMP ", xmp));
                }
            }
            long size = 4;
            foreach (var c in outChunks) size += c.Length;
            if (size > uint.MaxValue - 9) throw new ImageNotSupportedException("WebP files are limited to 4 GB");
            var o = new MemoryStream((int)size + 8);
            o.Write("RIFF"u8);
            Span<byte> s4 = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(s4, (uint)size);
            o.Write(s4);
            o.Write("WEBP"u8);
            foreach (var c in outChunks) o.Write(c);
            return o.ToArray();
        }

        static ReadOnlySpan<byte> WebpPayload(ReadOnlySpan<byte> d, (string Id, int Start, int Length) c) => d.Slice(c.Start + 8, (int)BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(c.Start + 4)));

        // ------------------------------------------------------------------ JPEG 2000

        static readonly byte[] UuidExif = "JpgTiffExif->JP2"u8.ToArray();
        static readonly byte[] UuidXmp = { 0xBE, 0x7A, 0xCF, 0xCB, 0x97, 0xA9, 0x42, 0xE8, 0x9C, 0x71, 0x99, 0x94, 0x91, 0xE3, 0xAF, 0xAC };
        static readonly byte[] UuidIptc = { 0x33, 0xC7, 0xA4, 0xD2, 0xB8, 0x1D, 0x47, 0x23, 0xA0, 0xBA, 0xF1, 0xA3, 0xE0, 0x97, 0xAD, 0x38 };

        static byte[] Box(string type, params byte[][] parts)
        {
            long n = 8;
            foreach (var x in parts) n += x.Length;
            if (n > uint.MaxValue) throw new ImageNotSupportedException("JP2: box too large");
            var b = new byte[n];
            BinaryPrimitives.WriteUInt32BigEndian(b, (uint)n);
            Encoding.ASCII.GetBytes(type, b.AsSpan(4));
            int p = 8;
            foreach (var x in parts) { x.CopyTo(b, p); p += x.Length; }
            return b;
        }

        static ReadOnlySpan<byte> Body(ReadOnlySpan<byte> d, (string Type, int Start, int Length, int Head) b) => d.Slice(b.Start + b.Head, b.Length - b.Head);
        static bool IsUuid(ReadOnlySpan<byte> d, (string Type, int Start, int Length, int Head) b, byte[] id) => b.Type == "uuid" && Body(d, b).Length >= 16 && Body(d, b).Slice(0, 16).SequenceEqual(id);
        static bool IsXmpXml(ReadOnlySpan<byte> d, (string Type, int Start, int Length, int Head) b) => b.Type == "xml " && (Body(d, b).IndexOf("adobe:ns:meta"u8) >= 0 || Body(d, b).IndexOf("xmpmeta"u8) >= 0);

        public static byte[] Jp2(ReadOnlySpan<byte> d, MetadataPlan plan)
        {
            if (d.Length >= 2 && d[0] == 0xFF && d[1] == 0x4F) throw new ImageNotSupportedException("JPEG 2000 codestream (.j2k / .j2c) without a JP2 container has no place for metadata");
            var boxes = new List<(string Type, int Start, int Length, int Head)>();
            int p = 0;
            while (p + 8 <= d.Length)
            {
                long len = BinaryPrimitives.ReadUInt32BigEndian(d.Slice(p));
                int head = 8;
                if (len == 1) { if (p + 16 > d.Length) break; len = (long)BinaryPrimitives.ReadUInt64BigEndian(d.Slice(p + 8)); head = 16; }
                else if (len == 0) len = d.Length - p;
                if (len < head || p + len > d.Length) throw new ImageFormatException("JP2: box exceeds the file");
                boxes.Add((Encoding.ASCII.GetString(d.Slice(p + 4, 4)), p, (int)len, head));
                p += (int)len;
            }
            if (boxes.Exists(b => b.Type is "ftbl")) throw new ImageNotSupportedException("JPX files with fragment tables are not supported (offsets would move)");

            var drop = new HashSet<int>();
            var add = new List<byte[]>();
            var replace = new Dictionary<int, byte[]>();   // a new box in the place of the old one
            void Put(int at, byte[] box) { if (at >= 0) replace[at] = box; else add.Add(box); }
            if (plan.HasExifChanges)
            {
                byte[]? old = null;
                for (int i = 0; i < boxes.Count; i++)
                {
                    var b = boxes[i];
                    if (IsUuid(d, b, UuidExif)) { var s = Body(d, b).Slice(16); old ??= (s.StartsWith("Exif\0\0"u8) ? s.Slice(6) : s).ToArray(); drop.Add(i); }
                    else if (b.Type == "Exif")
                    {
                        var s = Body(d, b);
                        if (old == null && s.Length >= 4) { uint off = BinaryPrimitives.ReadUInt32BigEndian(s); if (off <= s.Length - 4) old = s.Slice(4 + (int)off).ToArray(); }
                        drop.Add(i);
                    }
                }
                var blob = ExifEdit.EditBlob(old, plan, null);
                if (ReferenceEquals(blob, old) && drop.Count == 1) drop.Clear();
                else if (blob != null) Put(drop.Count > 0 ? drop.Min() : -1, Box("uuid", UuidExif, blob));
            }
            if (plan.HasXmpChanges)
            {
                byte[]? old = null;
                var mine = new List<int>();
                for (int i = 0; i < boxes.Count; i++)
                {
                    var b = boxes[i];
                    if (IsUuid(d, b, UuidXmp)) { old ??= Body(d, b).Slice(16).ToArray(); mine.Add(i); }
                    else if (IsXmpXml(d, b)) { old ??= Body(d, b).ToArray(); mine.Add(i); }
                }
                var x = XmpEditor.Edit(old, plan, out bool changed);
                if (changed)
                {
                    foreach (var i in mine) drop.Add(i);
                    if (x != null) Put(mine.Count > 0 ? mine[0] : -1, Box("uuid", UuidXmp, x));
                }
            }
            if (plan.HasIptcChanges)
                for (int i = 0; i < boxes.Count; i++)
                {
                    if (!IsUuid(d, boxes[i], UuidIptc)) continue;
                    drop.Add(i);
                    var n = IptcEditor.EditIim(Body(d, boxes[i]).Slice(16), plan);
                    if (n != null) Put(i, Box("uuid", UuidIptc, n));
                }
            if (plan.ClearAll)
                for (int i = 0; i < boxes.Count; i++) if (boxes[i].Type == "xml ") drop.Add(i);
            if (drop.Count == 0 && add.Count == 0) return d.ToArray();

            // new boxes go behind the header box (in front of the codestream when that follows it)
            int after = boxes.FindIndex(b => b.Type == "jp2h");
            if (after < 0) after = boxes.FindIndex(b => b.Type == "ftyp");
            var o = new MemoryStream(d.Length + 4096);
            for (int i = 0; i < boxes.Count; i++)
            {
                if (replace.TryGetValue(i, out var nb)) o.Write(nb);
                else if (!drop.Contains(i))
                {
                    var b = boxes[i];
                    if (b.Type == "jp2c" && BinaryPrimitives.ReadUInt32BigEndian(d.Slice(b.Start)) == 0 && i != boxes.Count - 1)
                        throw new ImageFormatException("JP2: a codestream box of length 0 must be the last box");
                    o.Write(d.Slice(b.Start, b.Length));
                }
                if (i == after) foreach (var a in add) o.Write(a);
            }
            o.Write(d.Slice(p));
            return o.ToArray();
        }
    }
}
