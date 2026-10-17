using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Aardvark.ImageCodecs
{
    /// <summary>The MP Type of an individual image of a Multi-Picture Format file (CIPA DC-007 Table 4).</summary>
    public enum MpfImageType
    {
        /// <summary>Undefined (000000.H), allowed in Extended MP files (.MPO).</summary>
        Undefined,
        /// <summary>Baseline MP Primary Image (030000.H): the main image of a Baseline MP file (.JPG).</summary>
        BaselinePrimary,
        /// <summary>Large Thumbnail (01000x.H), class in <see cref="MpfImage.ThumbnailClass"/>.</summary>
        LargeThumbnail,
        /// <summary>Original preservation image (040000.H, Exif Annex K).</summary>
        OriginalPreservation,
        /// <summary>Gain map image (050000.H, ISO 21496-1 Annex C).</summary>
        GainMap,
        /// <summary>Multi-Frame Image, Panorama (020001.H).</summary>
        Panorama,
        /// <summary>Multi-Frame Image, Disparity (020002.H): stereo and other multi-viewpoint shots.</summary>
        Disparity,
        /// <summary>Multi-Frame Image, Multi-Angle (020003.H).</summary>
        MultiAngle,
        /// <summary>A code Table 4 does not list (see <see cref="MpfImage.TypeCode"/>).</summary>
        Other,
    }

    /// <summary>One individual image of a Multi-Picture Format file: its MP Entry and, when present, its MP Attribute IFD.</summary>
    public sealed class MpfImage
    {
        /// <summary>Position in the MP Entry list, 0 for the first individual image.</summary>
        public int Index { get; internal set; }
        public MpfImageType Type { get; internal set; }
        /// <summary>The 24 bit MP Type Code as stored.</summary>
        public int TypeCode { get; internal set; }
        /// <summary>Class 1..5 of a large thumbnail (VGA, Full HD, 4K, 8K, 16K), 0 otherwise.</summary>
        public int ThumbnailClass { get; internal set; }
        /// <summary>Image Data Format (0 = JPEG).</summary>
        public int DataFormat { get; internal set; }
        public bool IsRepresentative { get; internal set; }
        public bool IsDependentParent { get; internal set; }
        public bool IsDependentChild { get; internal set; }
        /// <summary>Entry numbers (1 based) of the dependent images, 0 for none.</summary>
        public int DependentImage1 { get; internal set; }
        public int DependentImage2 { get; internal set; }
        /// <summary>Byte offset of the image's SOI marker from the start of the data passed in.</summary>
        public long Offset { get; internal set; }
        /// <summary>Size in bytes from SOI to EOI.</summary>
        public long Size { get; internal set; }
        /// <summary>Whether the image lies within the data that was parsed and starts with an SOI marker.</summary>
        public bool IsPresent { get; internal set; }

        // MP Attribute IFD (CIPA DC-007 5.2.4); null when absent or not read
        /// <summary>MPIndividualNum (B101.H): viewpoint or panorama position, 1 based.</summary>
        public uint? IndividualNumber { get; internal set; }
        /// <summary>PanOrientation (B201.H).</summary>
        public uint? PanoramaOrientation { get; internal set; }
        /// <summary>PanOverlap_H / PanOverlap_V (B202.H, B203.H) in percent.</summary>
        public double? PanoramaOverlapH { get; internal set; }
        public double? PanoramaOverlapV { get; internal set; }
        /// <summary>BaseViewpointNum (B204.H).</summary>
        public uint? BaseViewpoint { get; internal set; }
        /// <summary>ConvergenceAngle (B205.H) in degrees, null when unknown.</summary>
        public double? ConvergenceAngle { get; internal set; }
        /// <summary>BaselineLength (B206.H) in metres, null when unknown.</summary>
        public double? BaselineLength { get; internal set; }
        /// <summary>VerticalDivergence (B207.H) in degrees.</summary>
        public double? DivergenceAngle { get; internal set; }
        /// <summary>AxisDistance_X / _Y / _Z (B208.H .. B20A.H) in metres.</summary>
        public double? AxisDistanceX { get; internal set; }
        public double? AxisDistanceY { get; internal set; }
        public double? AxisDistanceZ { get; internal set; }
        /// <summary>YawAngle / PitchAngle / RollAngle (B20B.H .. B20D.H) in degrees.</summary>
        public double? Yaw { get; internal set; }
        public double? Pitch { get; internal set; }
        public double? Roll { get; internal set; }

        /// <summary>Whether this is one of the views of an Extended MP file (panorama, disparity, multi-angle or undefined).</summary>
        public bool IsMultiFrame => Type is MpfImageType.Panorama or MpfImageType.Disparity or MpfImageType.MultiAngle or MpfImageType.Undefined;

        public override string ToString() => $"#{Index} {Type} {Size} bytes at {Offset}" + (IsRepresentative ? " (representative)" : "");
    }

    /// <summary>The MP Index IFD of a Multi-Picture Format JPEG (CIPA DC-007): MPO stereo / panorama files, Ultra HDR gain maps, large thumbnails.</summary>
    public sealed class MpfInfo
    {
        /// <summary>MPFVersion, normally "0100".</summary>
        public string Version { get; internal set; } = "";
        public bool BigEndian { get; internal set; }
        /// <summary>The individual images in MP Entry order (the first one is the primary image).</summary>
        public IReadOnlyList<MpfImage> Images { get; internal set; } = Array.Empty<MpfImage>();
        /// <summary>TotalFrames (B004.H), when present.</summary>
        public uint? TotalFrames { get; internal set; }

        /// <summary>
        /// Whether the file is an Extended MP file (.MPO): its first image is a multi-frame (multi-view) image. (A Baseline
        /// MP file starts with a Baseline MP Primary Image; images of undefined type in it, as some gain map writers
        /// use, are not views.)
        /// </summary>
        public bool IsExtended => Images.Count > 0 && Images[0].IsMultiFrame;

        /// <summary>The number of views of an Extended MP file (1 for Baseline MP files): what ImageInfo.FrameCount reports.</summary>
        public int ViewCount
        {
            get
            {
                if (!IsExtended) return 1;
                int n = 0;
                foreach (var i in Images) if (i.IsMultiFrame) n++;
                return n;
            }
        }

        public override string ToString() => $"MPF {Version}: " + string.Join(", ", Images);
    }

    /// <summary>The marker segments in front of the first scan of one JPEG stream, and the place where it ends.</summary>
    internal sealed class JpegHeaders
    {
        public readonly List<(int Marker, int Start, int Length)> Segments = new();   // Start: the payload, Length: its size
        public byte[]? Xmp;            // standard packet, extended part merged in as a second packet
        public byte[]? XmpExtended;
        public int IsoAt = -1, IsoLength;
        public int MpfAt = -1, MpfLength;
        public byte[]? Exif;
        public byte[]? Icc;
        public int Sos = -1;

        /// <summary>Walks the segments from the SOI at <paramref name="start"/> up to SOS; null when no SOI is there or the structure breaks before.</summary>
        public static JpegHeaders? Read(ReadOnlySpan<byte> d, long start)
        {
            if (start < 0 || start + 4 > d.Length || d[(int)start] != 0xFF || d[(int)start + 1] != 0xD8) return null;
            var h = new JpegHeaders();
            int p = (int)start + 2;
            var icc = new SortedDictionary<int, byte[]>();
            var ext = new SortedDictionary<long, byte[]>();
            long extTotal = -1;
            while (p + 4 <= d.Length)
            {
                if (d[p] != 0xFF) return h.Segments.Count > 0 ? h : null;
                int m = d[p + 1];
                if (m == 0xFF) { p++; continue; }
                if (m is 0xD8 or 0xD9 or 0x01 or (>= 0xD0 and <= 0xD7)) break;
                if (m == 0xDA) { h.Sos = p; break; }
                int len = BinaryPrimitives.ReadUInt16BigEndian(d.Slice(p + 2));
                if (len < 2 || p + 2 + len > d.Length) break;
                int s = p + 4, n = len - 2;
                h.Segments.Add((m, s, n));
                var pl = d.Slice(s, n);
                if (m == 0xE1)
                {
                    if (pl.StartsWith("Exif\0"u8) && n > 6) h.Exif ??= pl.Slice(6).ToArray();
                    else if (pl.StartsWith(XmpEditor.JpegStandardId)) h.Xmp ??= pl.Slice(XmpEditor.JpegStandardId.Length).ToArray();
                    else if (pl.StartsWith(XmpEditor.JpegExtendedId) && n >= 75)
                    {
                        long total = BinaryPrimitives.ReadUInt32BigEndian(pl.Slice(67)), off = BinaryPrimitives.ReadUInt32BigEndian(pl.Slice(71));
                        if (extTotal < 0) extTotal = total;
                        if (total == extTotal && total <= 64 << 20) ext[off] = pl.Slice(75).ToArray();
                    }
                }
                else if (m == 0xE2)
                {
                    if (pl.StartsWith(GainMapMetadata.Urn)) { if (h.IsoAt < 0) { h.IsoAt = s + GainMapMetadata.Urn.Length; h.IsoLength = n - GainMapMetadata.Urn.Length; } }
                    else if (pl.StartsWith("MPF\0"u8)) { if (h.MpfAt < 0) { h.MpfAt = s + 4; h.MpfLength = n - 4; } }
                    else if (pl.StartsWith("ICC_PROFILE\0"u8) && n > 14) icc[pl[12]] = pl.Slice(14).ToArray();
                }
                p += 2 + len;
            }
            if (icc.Count > 0)
            {
                var ms = new MemoryStream();
                foreach (var part in icc.Values) ms.Write(part);
                h.Icc = ms.ToArray();
            }
            if (ext.Count > 0 && extTotal > 0)
            {
                var all = new byte[extTotal];
                foreach (var (off, part) in ext)
                    if (off < extTotal) part.AsSpan(0, (int)Math.Min(part.Length, extTotal - off)).CopyTo(all.AsSpan((int)off));
                h.XmpExtended = all;
            }
            return h;
        }

        public ReadOnlySpan<byte> Iso(ReadOnlySpan<byte> d) => IsoAt < 0 ? default : d.Slice(IsoAt, IsoLength);

        public XmpData? ParseXmp()
        {
            if (Xmp == null) return null;
            var x = XmpData.Parse(Xmp);
            if (x != null && XmpExtended != null) x.Add(XmpExtended);
            return x;
        }

        /// <summary>
        /// The position after the EOI of the stream starting at <paramref name="start"/>: walks segments and skips entropy
        /// coded data (stuffed bytes and restart markers); -1 when it runs out of data first.
        /// </summary>
        public static long FindEnd(ReadOnlySpan<byte> d, long start)
        {
            if (start < 0 || start + 2 > d.Length || d[(int)start] != 0xFF || d[(int)start + 1] != 0xD8) return -1;
            int p = (int)start + 2;
            while (p + 2 <= d.Length)
            {
                if (d[p] != 0xFF) { p++; continue; }   // (inside entropy coded data)
                int m = d[p + 1];
                if (m == 0xFF) { p++; continue; }
                if (m == 0x00 || m is >= 0xD0 and <= 0xD7 || m == 0x01) { p += 2; continue; }
                if (m == 0xD9) return p + 2;
                if (m == 0xD8) return -1;
                if (p + 4 > d.Length) return -1;
                int len = BinaryPrimitives.ReadUInt16BigEndian(d.Slice(p + 2));
                if (len < 2) return -1;
                p += 2 + len;
            }
            return -1;
        }
    }

    /// <summary>CIPA DC-007 Multi-Picture Format: parsing the MP Index / Attribute IFDs and writing MP files.</summary>
    internal static class Mpf
    {
        public const int TypeBaselinePrimary = 0x030000, TypeGainMap = 0x050000, TypeUndefined = 0;
        public const int TypePanorama = 0x020001, TypeDisparity = 0x020002, TypeMultiAngle = 0x020003;
        const uint FlagParent = 0x80000000, FlagChild = 0x40000000, FlagRepresentative = 0x20000000;

        static MpfImageType TypeOf(int code) => code switch
        {
            TypeUndefined => MpfImageType.Undefined,
            TypeBaselinePrimary => MpfImageType.BaselinePrimary,
            >= 0x010001 and <= 0x010005 => MpfImageType.LargeThumbnail,
            0x040000 => MpfImageType.OriginalPreservation,
            TypeGainMap => MpfImageType.GainMap,
            TypePanorama => MpfImageType.Panorama,
            TypeDisparity => MpfImageType.Disparity,
            TypeMultiAngle => MpfImageType.MultiAngle,
            _ => MpfImageType.Other,
        };

        public static int CodeOf(MpfImageType t) => t switch
        {
            MpfImageType.Undefined => TypeUndefined,
            MpfImageType.BaselinePrimary => TypeBaselinePrimary,
            MpfImageType.OriginalPreservation => 0x040000,
            MpfImageType.GainMap => TypeGainMap,
            MpfImageType.Panorama => TypePanorama,
            MpfImageType.Disparity => TypeDisparity,
            MpfImageType.MultiAngle => TypeMultiAngle,
            _ => throw new ArgumentException($"MP type {t} needs an explicit code"),
        };

        /// <summary>
        /// The MP Index IFD of the primary image's MPF segment (payload after "MPF\0" at <paramref name="at"/>), with the
        /// attribute IFDs of the images that lie within <paramref name="d"/>. <paramref name="primaryStart"/> is where the
        /// primary's SOI is. Null whenever the structure is malformed: callers fall back to the primary image.
        /// </summary>
        public static MpfInfo? Parse(ReadOnlySpan<byte> d, int at, int length, long primaryStart)
        {
            try { return ParseCore(d, at, length, primaryStart); }
            catch (ArgumentOutOfRangeException) { return null; }
            catch (IndexOutOfRangeException) { return null; }
        }

        static MpfInfo? ParseCore(ReadOnlySpan<byte> d, int at, int length, long primaryStart)
        {
            var t = d.Slice(at, length);
            if (t.Length < 8) return null;
            bool be;
            if (t[0] == 'M' && t[1] == 'M' && t[2] == 0 && t[3] == 42) be = true;
            else if (t[0] == 'I' && t[1] == 'I' && t[2] == 42 && t[3] == 0) be = false;
            else return null;
            var r = new Ifd(t, be);
            long ifd = r.U32(4);
            var info = new MpfInfo { BigEndian = be };
            if (!r.Entries(ifd, out var entries, out long next)) return null;
            long count = -1, entryAt = -1, entryLen = 0;
            foreach (var (tag, type, cnt, value, valueAt) in entries)
            {
                switch (tag)
                {
                    case 0xB000: if (cnt == 4) info.Version = Encoding.ASCII.GetString(t.Slice((int)valueAt, 4)); break;
                    case 0xB001: if (type == 4 && cnt == 1) count = value; break;
                    case 0xB002: if (type == 7) { entryAt = cnt > 4 ? value : valueAt; entryLen = cnt; } break;
                    case 0xB004: if (type == 4 && cnt == 1) info.TotalFrames = (uint)value; break;
                }
            }
            if (count <= 0 || count > 4096 || entryAt < 0 || entryLen < count * 16 || entryAt + count * 16 > t.Length) return null;
            long header = at;   // MP Endian field, the base of all offsets
            var images = new List<MpfImage>();
            for (int i = 0; i < count; i++)
            {
                int e = (int)(entryAt + i * 16);
                uint attr = r.U32(e);
                var img = new MpfImage
                {
                    Index = i,
                    TypeCode = (int)(attr & 0xFFFFFF),
                    DataFormat = (int)(attr >> 24 & 7),
                    IsRepresentative = (attr & FlagRepresentative) != 0,
                    IsDependentParent = (attr & FlagParent) != 0,
                    IsDependentChild = (attr & FlagChild) != 0,
                    Size = r.U32(e + 4),
                    DependentImage1 = r.U16(e + 12),
                    DependentImage2 = r.U16(e + 14),
                };
                img.Type = TypeOf(img.TypeCode);
                if (img.Type == MpfImageType.LargeThumbnail) img.ThumbnailClass = img.TypeCode & 0xF;
                uint off = r.U32(e + 8);
                img.Offset = i == 0 ? primaryStart : header + off;
                img.IsPresent = Soi(d, img.Offset) && (i == 0 || off != 0);
                if (!img.IsPresent && i > 0 && off != 0 && img.Offset < d.Length)
                {
                    // writers that pad the MP header without correcting the offsets: an SOI shortly after is taken
                    int k = d.Slice((int)img.Offset, (int)Math.Min(64, d.Length - img.Offset)).IndexOf((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]);
                    if (k > 0) { img.Offset += k; img.IsPresent = true; }
                }
                images.Add(img);
            }
            info.Images = images;
            // attribute IFDs: the primary's follows the index IFD, the others are in each image's own MPF segment
            if (next > 0) Attributes(r, next, images[0]);
            for (int i = 1; i < images.Count; i++)
            {
                var img = images[i];
                if (!img.IsPresent) continue;
                var h = JpegHeaders.Read(d, img.Offset);
                if (h == null || h.MpfAt < 0) continue;
                var s = d.Slice(h.MpfAt, h.MpfLength);
                if (s.Length < 8) continue;
                bool ibe = s[0] == 'M';
                if (!(ibe && s[1] == 'M' || !ibe && s[0] == 'I' && s[1] == 'I')) continue;
                var ir = new Ifd(s, ibe);
                Attributes(ir, ir.U32(4), img);
            }
            return info;
        }

        static bool Soi(ReadOnlySpan<byte> d, long at) => at >= 0 && at + 3 <= d.Length && d[(int)at] == 0xFF && d[(int)at + 1] == 0xD8 && d[(int)at + 2] == 0xFF;

        static void Attributes(Ifd r, long ifd, MpfImage img)
        {
            if (!r.Entries(ifd, out var entries, out _)) return;
            foreach (var (tag, type, cnt, value, valueAt) in entries)
            {
                double? Rational()
                {
                    if (cnt != 1 || type is not (5 or 10) || value + 8 > r.Length) return null;
                    uint n = r.U32(value), dd = r.U32(value + 4);
                    if (n == 0xFFFFFFFF && dd == 0xFFFFFFFF || dd == 0) return null;
                    return type == 10 ? (int)n / (double)(int)dd : n / (double)dd;
                }
                switch (tag)
                {
                    case 0xB101: if (type == 4) img.IndividualNumber = (uint)value; break;
                    case 0xB201: if (type == 4) img.PanoramaOrientation = (uint)value; break;
                    case 0xB202: img.PanoramaOverlapH = Rational(); break;
                    case 0xB203: img.PanoramaOverlapV = Rational(); break;
                    case 0xB204: if (type == 4) img.BaseViewpoint = (uint)value; break;
                    case 0xB205: img.ConvergenceAngle = Rational(); break;
                    case 0xB206: img.BaselineLength = Rational(); break;
                    case 0xB207: img.DivergenceAngle = Rational(); break;
                    case 0xB208: img.AxisDistanceX = Rational(); break;
                    case 0xB209: img.AxisDistanceY = Rational(); break;
                    case 0xB20A: img.AxisDistanceZ = Rational(); break;
                    case 0xB20B: img.Yaw = Rational(); break;
                    case 0xB20C: img.Pitch = Rational(); break;
                    case 0xB20D: img.Roll = Rational(); break;
                }
            }
        }

        /// <summary>A TIFF style IFD reader over the MP extensions (offsets from the MP Endian field).</summary>
        sealed class Ifd
        {
            readonly byte[] t;
            readonly bool be;
            public Ifd(ReadOnlySpan<byte> t, bool be) { this.t = t.ToArray(); this.be = be; }
            public int Length => t.Length;
            public uint U32(long o) => be ? BinaryPrimitives.ReadUInt32BigEndian(t.AsSpan((int)o)) : BinaryPrimitives.ReadUInt32LittleEndian(t.AsSpan((int)o));
            public int U16(long o) => be ? BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan((int)o)) : BinaryPrimitives.ReadUInt16LittleEndian(t.AsSpan((int)o));

            /// <summary>(tag, type, count, value or offset, where the 4 byte value field is) of each entry.</summary>
            public bool Entries(long ifd, out List<(int Tag, int Type, long Count, long Value, long ValueAt)> list, out long next)
            {
                list = new();
                next = 0;
                if (ifd < 8 || ifd + 2 > t.Length) return false;
                int n = U16(ifd);
                if (n > 1000 || ifd + 2 + n * 12L + 4 > t.Length) return false;
                for (int k = 0; k < n; k++)
                {
                    long e = ifd + 2 + k * 12L;
                    int type = U16(e + 2);
                    long value = type == 3 ? U16(e + 8) : U32(e + 8);
                    list.Add((U16(e), type, U32(e + 4), value, e + 8));
                }
                next = U32(ifd + 2 + n * 12L);
                return true;
            }
        }

        // ------------------------------------------------------------------ writing

        /// <summary>What to write for one individual image.</summary>
        public sealed class Part
        {
            public byte[] Jpeg = Array.Empty<byte>();
            public int TypeCode;
            public bool Representative, Parent, Child;
            public int Dependent1, Dependent2;
            /// <summary>MP Attribute IFD entries (tag, type, values: LONG as one uint, (S)RATIONAL as two), written in tag order.</summary>
            public readonly SortedDictionary<int, (int Type, uint[] Values)> Attributes = new();
        }

        /// <summary>
        /// Writes an MP file: every part gets an MPF APP2 segment (the first one with the MP Index IFD) inserted after
        /// its leading APP0 / APP1 segments, as DC-007 5.1 wants it behind the Exif segment; images that would carry
        /// neither index nor attributes (gain maps, large thumbnails: 6.1.2.1) get none. <paramref name="extra"/>(i) gives
        /// further segments (complete with marker) to insert right after it.
        /// </summary>
        public static void Write(IReadOnlyList<Part> parts, Stream output, Func<int, IReadOnlyList<byte[]>>? extra = null)
        {
            int n = parts.Count;
            var segs = new byte[n][];
            for (int i = 0; i < n; i++)
            {
                if (i > 0 && parts[i].Attributes.Count == 0) { segs[i] = Array.Empty<byte>(); continue; }
                segs[i] = Segment(parts, i, new long[n], new long[n]);
            }
            var bodies = new byte[n][];
            for (int i = 0; i < n; i++) bodies[i] = Insert(parts[i].Jpeg, Concat(segs[i], extra?.Invoke(i)));
            // the index segment has a fixed size: place it, then fill in sizes and offsets
            int at = Locate(bodies[0], segs[0]);
            long headerPos = at + 8;   // marker, length, "MPF\0"
            var sizes = new long[n]; var offsets = new long[n];
            long pos = 0;
            for (int i = 0; i < n; i++)
            {
                sizes[i] = bodies[i].Length;
                offsets[i] = i == 0 ? 0 : pos - headerPos;
                pos += bodies[i].Length;
            }
            if (pos > uint.MaxValue) throw new ImageNotSupportedException("MP files are limited to 4 GB");
            var seg0 = Segment(parts, 0, sizes, offsets);
            seg0.CopyTo(bodies[0], at);
            foreach (var b in bodies) output.Write(b);
        }

        static byte[] Concat(byte[] a, IReadOnlyList<byte[]>? more)
        {
            var ms = new MemoryStream();
            ms.Write(a);
            if (more != null) foreach (var m in more) ms.Write(m);
            return ms.ToArray();
        }

        static int Locate(byte[] jpeg, byte[] seg)
        {
            int i = jpeg.AsSpan().IndexOf(seg);
            if (i < 0) throw new InvalidOperationException("MPF segment not found");
            return i;
        }

        /// <summary>The MPF APP2 segment of part <paramref name="i"/> (big endian).</summary>
        static byte[] Segment(IReadOnlyList<Part> parts, int i, long[] sizes, long[] offsets)
        {
            var t = new List<byte>();
            void U16(int v) { t.Add((byte)(v >> 8)); t.Add((byte)v); }
            void U32(uint v) { t.Add((byte)(v >> 24)); t.Add((byte)(v >> 16)); t.Add((byte)(v >> 8)); t.Add((byte)v); }
            void Set32(int o, uint v) { t[o] = (byte)(v >> 24); t[o + 1] = (byte)(v >> 16); t[o + 2] = (byte)(v >> 8); t[o + 3] = (byte)v; }
            t.AddRange("MM\0*"u8.ToArray());
            U32(8);
            var attrs = parts[i].Attributes;
            void AttributeIfd()
            {
                // MPFVersion + the given tags; values after the IFD
                int count = 1 + attrs.Count;
                int start = t.Count;
                U16(count);
                int valuesAt = start + 2 + count * 12 + 4;
                var values = new List<byte>();
                void Entry(int tag, int type, uint cnt, uint inline) { U16(tag); U16(type); U32(cnt); U32(inline); }
                Entry(0xB000, 7, 4, 0x30313030);   // "0100"
                foreach (var (tag, (type, v)) in attrs)
                {
                    if (type == 4) Entry(tag, 4, 1, v[0]);
                    else
                    {
                        Entry(tag, type, 1, (uint)(valuesAt + values.Count));
                        foreach (var x in v) { values.Add((byte)(x >> 24)); values.Add((byte)(x >> 16)); values.Add((byte)(x >> 8)); values.Add((byte)x); }
                    }
                }
                U32(0);
                t.AddRange(values);
            }
            if (i == 0)
            {
                int n = parts.Count;
                // MP Index IFD: version, number of images, entries; next IFD: the attribute IFD, if any
                U16(3);
                U16(0xB000); U16(7); U32(4); U32(0x30313030);
                U16(0xB001); U16(4); U32(1); U32((uint)n);
                int entryOffsetField = t.Count + 8;
                U16(0xB002); U16(7); U32((uint)(16 * n)); U32(0);
                int nextField = t.Count;
                U32(0);
                Set32(entryOffsetField, (uint)t.Count);
                for (int k = 0; k < n; k++)
                {
                    var p = parts[k];
                    uint attr = (uint)(p.TypeCode & 0xFFFFFF) | (p.Representative ? FlagRepresentative : 0) | (p.Parent ? FlagParent : 0) | (p.Child ? FlagChild : 0);
                    U32(attr); U32((uint)sizes[k]); U32((uint)offsets[k]); U16(p.Dependent1); U16(p.Dependent2);
                }
                if (attrs.Count > 0) { Set32(nextField, (uint)t.Count); AttributeIfd(); }
            }
            else AttributeIfd();
            var seg = new byte[4 + 4 + t.Count];
            seg[0] = 0xFF; seg[1] = 0xE2;
            BinaryPrimitives.WriteUInt16BigEndian(seg.AsSpan(2), (ushort)(seg.Length - 2));
            "MPF\0"u8.CopyTo(seg.AsSpan(4));
            t.CopyTo(seg, 8);
            return seg;
        }

        /// <summary>
        /// Inserts <paramref name="segments"/> after the leading APP0 and Exif APP1 segments of a JPEG stream (any number
        /// of APP0, then at most one Exif APP1).
        /// </summary>
        public static byte[] Insert(byte[] jpeg, byte[] segments)
        {
            if (segments.Length == 0) return jpeg;
            if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) throw new ArgumentException("not a JPEG stream");
            int p = 2;
            while (p + 4 <= jpeg.Length && jpeg[p] == 0xFF && jpeg[p + 1] == 0xE0) p += 2 + BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(p + 2));
            if (p + 10 <= jpeg.Length && jpeg[p] == 0xFF && jpeg[p + 1] == 0xE1 && jpeg.AsSpan(p + 4).StartsWith("Exif\0"u8))
                p += 2 + BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(p + 2));
            var r = new byte[jpeg.Length + segments.Length];
            jpeg.AsSpan(0, p).CopyTo(r);
            segments.CopyTo(r, p);
            jpeg.AsSpan(p).CopyTo(r.AsSpan(p + segments.Length));
            return r;
        }

        /// <summary>A complete APPn segment from an identifier and a payload.</summary>
        public static byte[] App(int marker, ReadOnlySpan<byte> id, ReadOnlySpan<byte> payload)
        {
            int len = 2 + id.Length + payload.Length;
            if (len > 65535) throw new ImageNotSupportedException("JPEG segment exceeds 64 KB");
            var s = new byte[2 + len];
            s[0] = 0xFF; s[1] = (byte)marker;
            BinaryPrimitives.WriteUInt16BigEndian(s.AsSpan(2), (ushort)len);
            id.CopyTo(s.AsSpan(4));
            payload.CopyTo(s.AsSpan(4 + id.Length));
            return s;
        }
    }
}
