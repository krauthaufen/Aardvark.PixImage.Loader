using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Aardvark.ImageCodecs
{
    /// <summary>How the pixels of an ICNS element are stored.</summary>
    public enum IcnsElementKind
    {
        /// <summary>1 bit black and white (ICON, ICN#, ics#, icm#, ich#), the # types with a 1 bit mask.</summary>
        Mono,
        /// <summary>4 bit indices into the Mac OS 16 colour palette (icl4, ics4, icm4, ich4).</summary>
        Palette4,
        /// <summary>8 bit indices into the Mac OS 256 colour palette (icl8, ics8, icm8, ich8).</summary>
        Palette8,
        /// <summary>24 bit RGB, run length coded per channel (is32, il32, ih32, it32), alpha from the matching 8 bit mask.</summary>
        Rle24,
        /// <summary>32 bit ARGB, run length coded per channel ("ARGB" data of ic04, ic05, icsb).</summary>
        Argb,
        /// <summary>A PNG stream.</summary>
        Png,
        /// <summary>A JPEG 2000 stream (JP2 file or codestream).</summary>
        Jpeg2000,
    }

    /// <summary>One image element of an ICNS file. <see cref="Offset"/> / <see cref="Length"/> locate its data (after the 8 byte element header).</summary>
    public sealed record IcnsEntry(string Type, int Width, int Height, int Scale, IcnsElementKind Kind, int Offset, int Length)
    {
        /// <summary>Bits per pixel of the stored colour (1, 4, 8, 24 or 32; 32 for PNG / JPEG 2000).</summary>
        public int Depth => Kind switch { IcnsElementKind.Mono => 1, IcnsElementKind.Palette4 => 4, IcnsElementKind.Palette8 => 8, IcnsElementKind.Rle24 => 24, _ => 32 };
    }

    public sealed class IcnsDecodeOptions
    {
        /// <summary>Which entry of <see cref="Icns.ReadEntries"/> <see cref="Icns.Decode"/> returns; -1 (the default) picks the largest, then the deepest.</summary>
        public int Index { get; set; } = -1;

        internal static readonly IcnsDecodeOptions Default = new();
    }

    public sealed class IcnsEncodeOptions
    {
        /// <summary>
        /// Also write the run length coded types of Mac OS 8.5 to 10.6 (is32 / il32 / ih32 / it32 with s8mk / l8mk / h8mk / t8mk)
        /// for the 16, 32, 48 and 128 pixel images. Default false.
        /// </summary>
        public bool LegacyTypes { get; set; }

        /// <summary>
        /// Store the 16 and 32 pixel images as PNG (icp4 / icp5) instead of the run length coded ARGB of ic04 / ic05 that
        /// iconutil writes. Default false.
        /// </summary>
        public bool SmallAsPng { get; set; }

        /// <summary>
        /// Also store each image under the Retina (@2x) type of half its size where there is one (32 as ic11, 64 as ic12,
        /// 256 as ic13, 512 as ic14), as iconutil does for a complete icon set. Default false: 64 is written as ic12, 1024 as
        /// ic10, the others under their 1x type only.
        /// </summary>
        public bool RetinaDuplicates { get; set; }
    }

    /// <summary>
    /// Apple icon files (ICNS): a list of typed elements. Reads every image type: 1 bit (ICON, ICN#, ics#, icm#, ich#), 4 and
    /// 8 bit with the Mac OS system palettes, 24 bit run length coded (is32, il32, ih32, it32) with their 8 bit masks
    /// (s8mk, l8mk, h8mk, t8mk) or else the 1 bit mask of the same size, ARGB (ic04, ic05, icsb, icsB, sb24, SB24) and the
    /// PNG / JPEG 2000 types (icp4..icp6, ic07..ic14). Legacy elements decode to UInt8 Rgba, PNG and JPEG 2000 ones as those
    /// codecs decode them. <see cref="Decode"/> returns the largest image, <see cref="DecodeAll"/> every one in file order.
    /// Writes the types macOS uses today (ARGB for 16 and 32 pixels, PNG above), optionally with the legacy RLE types.
    /// </summary>
    public static class Icns
    {
        static readonly Dictionary<string, (int w, int h, int scale, IcnsElementKind kind)> Types = new()
        {
            ["ICON"] = (32, 32, 1, IcnsElementKind.Mono),
            ["ICN#"] = (32, 32, 1, IcnsElementKind.Mono),
            ["icm#"] = (16, 12, 1, IcnsElementKind.Mono),
            ["icm4"] = (16, 12, 1, IcnsElementKind.Palette4),
            ["icm8"] = (16, 12, 1, IcnsElementKind.Palette8),
            ["ics#"] = (16, 16, 1, IcnsElementKind.Mono),
            ["ics4"] = (16, 16, 1, IcnsElementKind.Palette4),
            ["ics8"] = (16, 16, 1, IcnsElementKind.Palette8),
            ["icl4"] = (32, 32, 1, IcnsElementKind.Palette4),
            ["icl8"] = (32, 32, 1, IcnsElementKind.Palette8),
            ["ich#"] = (48, 48, 1, IcnsElementKind.Mono),
            ["ich4"] = (48, 48, 1, IcnsElementKind.Palette4),
            ["ich8"] = (48, 48, 1, IcnsElementKind.Palette8),
            ["is32"] = (16, 16, 1, IcnsElementKind.Rle24),
            ["il32"] = (32, 32, 1, IcnsElementKind.Rle24),
            ["ih32"] = (48, 48, 1, IcnsElementKind.Rle24),
            ["it32"] = (128, 128, 1, IcnsElementKind.Rle24),
            // PNG / JPEG 2000 / ARGB: the kind is decided by the data
            ["icp4"] = (16, 16, 1, IcnsElementKind.Png),
            ["icp5"] = (32, 32, 1, IcnsElementKind.Png),
            ["icp6"] = (64, 64, 1, IcnsElementKind.Png),
            ["ic04"] = (16, 16, 1, IcnsElementKind.Argb),
            ["ic05"] = (32, 32, 1, IcnsElementKind.Argb),
            ["icsb"] = (18, 18, 1, IcnsElementKind.Argb),
            ["icsB"] = (36, 36, 2, IcnsElementKind.Png),
            ["sb24"] = (24, 24, 1, IcnsElementKind.Png),
            ["SB24"] = (48, 48, 2, IcnsElementKind.Png),
            ["ic07"] = (128, 128, 1, IcnsElementKind.Png),
            ["ic08"] = (256, 256, 1, IcnsElementKind.Png),
            ["ic09"] = (512, 512, 1, IcnsElementKind.Png),
            ["ic10"] = (1024, 1024, 2, IcnsElementKind.Png),
            ["ic11"] = (32, 32, 2, IcnsElementKind.Png),
            ["ic12"] = (64, 64, 2, IcnsElementKind.Png),
            ["ic13"] = (256, 256, 2, IcnsElementKind.Png),
            ["ic14"] = (512, 512, 2, IcnsElementKind.Png),
        };

        /// <summary>Whether the data starts with the "icns" signature and a plausible length.</summary>
        public static bool IsMatch(ReadOnlySpan<byte> h)
            => h.Length >= 8 && h.Slice(0, 4).SequenceEqual("icns"u8) && BinaryPrimitives.ReadUInt32BigEndian(h.Slice(4)) >= 8;

        static string TypeName(ReadOnlySpan<byte> t)
        {
            Span<char> c = stackalloc char[4];
            for (int i = 0; i < 4; i++) c[i] = (char)t[i];
            return new string(c);
        }

        /// <summary>The elements of the file: (type, data offset, data length), unknown ones and masks included.</summary>
        static List<(string type, int off, int len)> Elements(ReadOnlySpan<byte> d)
        {
            if (!IsMatch(d)) throw new ImageFormatException("ICNS: not an Apple icon file");
            long end = Math.Min(d.Length, BinaryPrimitives.ReadUInt32BigEndian(d.Slice(4)));
            var r = new List<(string, int, int)>();
            long p = 8;
            while (p + 8 <= end)
            {
                uint len = BinaryPrimitives.ReadUInt32BigEndian(d.Slice((int)p + 4));
                if (len < 8) throw new ImageFormatException("ICNS: element length below 8");
                long dataLen = Math.Min(len - 8L, end - p - 8);
                r.Add((TypeName(d.Slice((int)p, 4)), (int)p + 8, (int)dataLen));
                p += len;
            }
            return r;
        }

        /// <summary>The image elements in file order (masks, table of contents and other metadata are left out).</summary>
        public static IReadOnlyList<IcnsEntry> ReadEntries(ReadOnlySpan<byte> d)
        {
            var r = new List<IcnsEntry>();
            foreach (var (type, off, len) in Elements(d))
            {
                if (!Types.TryGetValue(type, out var t)) continue;
                var data = d.Slice(off, len);
                var kind = t.kind;
                int w = t.w, h = t.h;
                if (kind is IcnsElementKind.Png or IcnsElementKind.Argb)
                {
                    if (Png.IsMatch(data))
                    {
                        kind = IcnsElementKind.Png;
                        try { var pi = Png.GetInfo(data); w = pi.Width; h = pi.Height; } catch (ImageFormatException) { }
                    }
                    else if (Jp2.IsMatch(data))
                    {
                        kind = IcnsElementKind.Jpeg2000;
                        try { var ji = Jp2.GetInfo(data); w = ji.Width; h = ji.Height; } catch (Exception e) when (e is ImageFormatException or ImageNotSupportedException) { }
                    }
                    else if (data.Length >= 4 && data.Slice(0, 4).SequenceEqual("ARGB"u8)) kind = IcnsElementKind.Argb;
                    else if (type is "icp4" or "icp5") kind = IcnsElementKind.Rle24;   // early 10.7 files: RLE like is32 / il32
                    else continue;   // unknown payload
                }
                r.Add(new IcnsEntry(type, w, h, t.scale, kind, off, len));
            }
            if (r.Count == 0) throw new ImageFormatException("ICNS: no images");
            return r;
        }

        static int Best(IReadOnlyList<IcnsEntry> es)
        {
            int best = 0;
            for (int i = 1; i < es.Count; i++)
            {
                long a = (long)es[i].Width * es[i].Height, b = (long)es[best].Width * es[best].Height;
                if (a > b || a == b && es[i].Depth > es[best].Depth) best = i;
            }
            return best;
        }

        /// <summary>Header information on the largest image; FrameCount is the number of images.</summary>
        public static ImageInfo GetInfo(ReadOnlySpan<byte> data)
        {
            var es = ReadEntries(data);
            var e = es[Best(es)];
            var src = data.Slice(e.Offset, e.Length);
            var info = e.Kind switch
            {
                IcnsElementKind.Png => Png.GetInfo(src),
                IcnsElementKind.Jpeg2000 => Jp2.GetInfo(src),
                _ => new ImageInfo(ImageFormat.Icns, e.Width, e.Height, 4, SampleType.UInt8, ColorModel.Rgba) { BitsPerSample = 8 },
            };
            return info with { Format = ImageFormat.Icns, FrameCount = es.Count, Transfer = info.Transfer == TransferFunction.Unknown ? TransferFunction.Srgb : info.Transfer, Metadata = null };
        }

        /// <summary>Decodes the largest image (or the one <see cref="IcnsDecodeOptions.Index"/> selects).</summary>
        public static RawImage Decode(ReadOnlySpan<byte> data, IcnsDecodeOptions? options = null)
        {
            var es = ReadEntries(data);
            int i = (options ?? IcnsDecodeOptions.Default).Index;
            if (i < 0) i = Best(es);
            if (i >= es.Count) throw new ArgumentOutOfRangeException(nameof(options), "ICNS: no such image");
            return DecodeEntry(data, es[i], Elements(data));
        }

        /// <summary>Decodes every image of the file in the order of <see cref="ReadEntries"/>.</summary>
        public static RawImage[] DecodeAll(ReadOnlySpan<byte> data)
        {
            var es = ReadEntries(data);
            var els = Elements(data);
            var r = new RawImage[es.Count];
            for (int i = 0; i < r.Length; i++) r[i] = DecodeEntry(data, es[i], els);
            return r;
        }

        static RawImage DecodeEntry(ReadOnlySpan<byte> data, IcnsEntry e, List<(string type, int off, int len)> els)
        {
            var src = data.Slice(e.Offset, e.Length);
            RawImage img;
            switch (e.Kind)
            {
                case IcnsElementKind.Png: img = Png.Decode(src); break;
                case IcnsElementKind.Jpeg2000: img = Jp2.Decode(src); break;
                case IcnsElementKind.Argb:
                    {
                        int n = e.Width * e.Height;
                        var planes = new byte[n * 4];
                        Unpack(src.Slice(4), planes, 4 * n);
                        var o = new byte[n * 4];
                        for (int i = 0; i < n; i++)
                        {
                            o[i * 4] = planes[n + i]; o[i * 4 + 1] = planes[2 * n + i]; o[i * 4 + 2] = planes[3 * n + i]; o[i * 4 + 3] = planes[i];
                        }
                        img = new RawImage(e.Width, e.Height, ColorModel.Rgba, o, 4);
                        break;
                    }
                default:
                    {
                        int w = e.Width, h = e.Height, n = w * h;
                        var o = new byte[n * 4];
                        bool ownAlpha = false;
                        switch (e.Kind)
                        {
                            case IcnsElementKind.Mono:
                                {
                                    int rb = (w + 7) / 8;
                                    Need(src, rb * h, e.Type);
                                    bool mask = e.Type != "ICON" && src.Length >= 2 * rb * h;
                                    for (int y = 0; y < h; y++)
                                        for (int x = 0; x < w; x++)
                                        {
                                            int bit = 7 - (x & 7), i = y * w + x;
                                            byte v = (src[y * rb + (x >> 3)] >> bit & 1) != 0 ? (byte)0 : (byte)255;
                                            o[i * 4] = o[i * 4 + 1] = o[i * 4 + 2] = v;
                                            o[i * 4 + 3] = !mask || (src[rb * h + y * rb + (x >> 3)] >> bit & 1) != 0 ? (byte)255 : (byte)0;
                                        }
                                    ownAlpha = true;
                                    break;
                                }
                            case IcnsElementKind.Palette4:
                                {
                                    Need(src, (w + 1) / 2 * h, e.Type);
                                    var pal = MacPalettes.Palette4;
                                    int rb = (w + 1) / 2;
                                    for (int y = 0; y < h; y++)
                                        for (int x = 0; x < w; x++)
                                        {
                                            int b = src[y * rb + (x >> 1)], idx = (x & 1) == 0 ? b >> 4 : b & 15, i = y * w + x;
                                            o[i * 4] = pal[idx * 3]; o[i * 4 + 1] = pal[idx * 3 + 1]; o[i * 4 + 2] = pal[idx * 3 + 2];
                                        }
                                    break;
                                }
                            case IcnsElementKind.Palette8:
                                {
                                    Need(src, n, e.Type);
                                    var pal = MacPalettes.Palette8;
                                    for (int i = 0; i < n; i++)
                                    {
                                        int idx = src[i];
                                        o[i * 4] = pal[idx * 3]; o[i * 4 + 1] = pal[idx * 3 + 1]; o[i * 4 + 2] = pal[idx * 3 + 2];
                                    }
                                    break;
                                }
                            case IcnsElementKind.Rle24:
                                {
                                    var s = src;
                                    if (e.Type == "it32" && s.Length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(s) == 0) s = s.Slice(4);
                                    if (s.Length == n * 4)
                                    {
                                        // uncompressed (some writers): XRGB
                                        for (int i = 0; i < n; i++) { o[i * 4] = s[i * 4 + 1]; o[i * 4 + 1] = s[i * 4 + 2]; o[i * 4 + 2] = s[i * 4 + 3]; }
                                    }
                                    else
                                    {
                                        var planes = new byte[n * 3];
                                        Unpack(s, planes, 3 * n);
                                        for (int i = 0; i < n; i++) { o[i * 4] = planes[i]; o[i * 4 + 1] = planes[n + i]; o[i * 4 + 2] = planes[2 * n + i]; }
                                    }
                                    break;
                                }
                        }
                        if (!ownAlpha) ApplyMask(data, els, w, h, o);
                        img = new RawImage(w, h, ColorModel.Rgba, o, 4);
                        break;
                    }
            }
            (img.Metadata ??= new ImageMetadata()).Transfer = img.Metadata.Transfer == TransferFunction.Unknown ? TransferFunction.Srgb : img.Metadata.Transfer;
            return img;
        }

        static void Need(ReadOnlySpan<byte> src, int bytes, string type)
        {
            if (src.Length < bytes) throw new ImageFormatException($"ICNS: {type} element truncated");
        }

        /// <summary>Alpha of a legacy colour element: the 8 bit mask of its size, else the 1 bit mask of the # element, else opaque.</summary>
        static void ApplyMask(ReadOnlySpan<byte> d, List<(string type, int off, int len)> els, int w, int h, byte[] o)
        {
            int n = w * h;
            string? m8 = (w, h) switch { (16, 16) => "s8mk", (32, 32) => "l8mk", (48, 48) => "h8mk", (128, 128) => "t8mk", _ => null };
            string? m1 = (w, h) switch { (16, 16) => "ics#", (32, 32) => "ICN#", (48, 48) => "ich#", (16, 12) => "icm#", _ => null };
            foreach (var (type, off, len) in els)
                if (type == m8 && len >= n)
                {
                    for (int i = 0; i < n; i++) o[i * 4 + 3] = d[off + i];
                    return;
                }
            int rb = (w + 7) / 8;
            foreach (var (type, off, len) in els)
                if (type == m1 && len >= 2 * rb * h)
                {
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                            o[(y * w + x) * 4 + 3] = (d[off + rb * h + y * rb + (x >> 3)] >> (7 - (x & 7)) & 1) != 0 ? (byte)255 : (byte)0;
                    return;
                }
            for (int i = 0; i < n; i++) o[i * 4 + 3] = 255;
        }

        /// <summary>
        /// The ICNS run length code: a byte below 0x80 is followed by that many plus one literal bytes, a byte b from 0x80 by
        /// one byte repeated b - 125 times. Fills <paramref name="count"/> bytes; missing data reads as zeros.
        /// </summary>
        static void Unpack(ReadOnlySpan<byte> s, Span<byte> o, int count)
        {
            int p = 0, q = 0;
            while (q < count && p < s.Length)
            {
                int b = s[p++];
                if (b < 0x80)
                {
                    int k = Math.Min(b + 1, Math.Min(count - q, s.Length - p));
                    s.Slice(p, k).CopyTo(o.Slice(q));
                    p += b + 1; q += k;
                }
                else
                {
                    if (p >= s.Length) break;
                    int k = Math.Min(b - 125, count - q);
                    o.Slice(q, k).Fill(s[p++]);
                    q += k;
                }
            }
        }

        static void Pack(ReadOnlySpan<byte> s, Stream o)
        {
            int i = 0, n = s.Length;
            Span<byte> lit = stackalloc byte[129];
            while (i < n)
            {
                int run = 1;
                while (i + run < n && run < 130 && s[i + run] == s[i]) run++;
                if (run >= 3)
                {
                    o.WriteByte((byte)(run + 125));
                    o.WriteByte(s[i]);
                    i += run;
                    continue;
                }
                // literals up to the next run of three
                int start = i, k = 0;
                while (i < n && k < 128)
                {
                    if (i + 2 < n && s[i] == s[i + 1] && s[i] == s[i + 2]) break;
                    i++; k++;
                }
                o.WriteByte((byte)(k - 1));
                o.Write(s.Slice(start, k));
            }
        }

        /// <summary>
        /// Writes an icon from square images of 16, 32, 48, 64, 128, 256, 512 or 1024 pixels (UInt8 Gray, GrayAlpha, Rgb or
        /// Rgba; other sample types are scaled to 8 bits): 16 and 32 as ARGB (ic04, ic05), 48 as ih32 + h8mk, 64 as ic12,
        /// 128 to 1024 as PNG (ic07, ic08, ic09, ic10). See <see cref="IcnsEncodeOptions"/> for the variants.
        /// </summary>
        public static void Encode(IReadOnlyList<RawImage> images, Stream output, IcnsEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(images);
            options ??= new IcnsEncodeOptions();
            if (images.Count == 0) throw new ArgumentException("ICNS: at least one image needed");
            var els = new List<(string, RawImage)>();
            foreach (var im in images)
            {
                if (im.Width != im.Height) throw new ArgumentException("ICNS: icons are square");
                int s = im.Width;
                switch (s)
                {
                    case 16: els.Add((options.SmallAsPng ? "icp4" : "ic04", im)); if (options.LegacyTypes) els.Add(("is32", im)); break;
                    case 32:
                        els.Add((options.SmallAsPng ? "icp5" : "ic05", im));
                        if (options.LegacyTypes) els.Add(("il32", im));
                        if (options.RetinaDuplicates) els.Add(("ic11", im));
                        break;
                    case 48: els.Add(("ih32", im)); break;
                    case 64: els.Add(("ic12", im)); break;
                    case 128: els.Add(("ic07", im)); if (options.LegacyTypes) els.Add(("it32", im)); break;
                    case 256: els.Add(("ic08", im)); if (options.RetinaDuplicates) els.Add(("ic13", im)); break;
                    case 512: els.Add(("ic09", im)); if (options.RetinaDuplicates) els.Add(("ic14", im)); break;
                    case 1024: els.Add(("ic10", im)); break;
                    default: throw new ArgumentException($"ICNS: no icon type holds {s} x {s} pixels");
                }
            }
            EncodeElements(els, output);
        }

        public static byte[] Encode(IReadOnlyList<RawImage> images, IcnsEncodeOptions? options = null)
        {
            var ms = new MemoryStream();
            Encode(images, ms, options);
            return ms.ToArray();
        }

        /// <summary>
        /// Writes the given elements in order: PNG types (icp4..icp6, ic07..ic14, sb24, SB24, icsB) as PNG, ic04 / ic05 / icsb
        /// as ARGB, is32 / il32 / ih32 / it32 as 24 bit RLE followed by their 8 bit mask element. Each image must have the
        /// type's size (PNG types take any size).
        /// </summary>
        public static void EncodeElements(IReadOnlyList<(string Type, RawImage Image)> elements, Stream output)
        {
            ArgumentNullException.ThrowIfNull(elements);
            ArgumentNullException.ThrowIfNull(output);
            var body = new MemoryStream();
            foreach (var (type, im) in elements)
            {
                if (!Types.TryGetValue(type, out var t) || t.kind is IcnsElementKind.Mono or IcnsElementKind.Palette4 or IcnsElementKind.Palette8)
                    throw new ArgumentException($"ICNS: cannot write elements of type '{type}'");
                var rgba = ToRgba8(im);
                bool png = t.kind == IcnsElementKind.Png;
                if (!png && (im.Width != t.w || im.Height != t.h)) throw new ArgumentException($"ICNS: {type} holds {t.w} x {t.h} pixels, not {im.Width} x {im.Height}");
                int n = im.Width * im.Height;
                var px = (byte[])rgba.Data;
                if (png) { WriteElement(body, type, Png.Encode(rgba)); continue; }
                var ms = new MemoryStream();
                var plane = new byte[n];
                if (t.kind == IcnsElementKind.Argb)
                {
                    ms.Write("ARGB"u8);
                    foreach (int c in new[] { 3, 0, 1, 2 })
                    {
                        for (int i = 0; i < n; i++) plane[i] = px[i * 4 + c];
                        Pack(plane, ms);
                    }
                    WriteElement(body, type, ms.ToArray());
                    continue;
                }
                if (type == "it32") ms.Write(new byte[4]);
                for (int c = 0; c < 3; c++)
                {
                    for (int i = 0; i < n; i++) plane[i] = px[i * 4 + c];
                    Pack(plane, ms);
                }
                WriteElement(body, type, ms.ToArray());
                for (int i = 0; i < n; i++) plane[i] = px[i * 4 + 3];
                WriteElement(body, type switch { "is32" => "s8mk", "il32" => "l8mk", "ih32" => "h8mk", _ => "t8mk" }, plane);
            }
            Span<byte> head = stackalloc byte[8];
            "icns"u8.CopyTo(head);
            BinaryPrimitives.WriteUInt32BigEndian(head.Slice(4), checked((uint)(body.Length + 8)));
            output.Write(head);
            body.Position = 0;
            body.CopyTo(output);
        }

        static void WriteElement(Stream o, string type, ReadOnlySpan<byte> data)
        {
            Span<byte> head = stackalloc byte[8];
            for (int i = 0; i < 4; i++) head[i] = (byte)type[i];
            BinaryPrimitives.WriteUInt32BigEndian(head.Slice(4), checked((uint)(data.Length + 8)));
            o.Write(head);
            o.Write(data);
        }

        static RawImage ToRgba8(RawImage im)
        {
            if (im.Color == ColorModel.Other && im.Channels > 4) throw new ArgumentException("ICNS: at most four channels");
            var b = KtxImpl.KtxWriter.ToRgba8(im);
            return new RawImage(im.Width, im.Height, ColorModel.Rgba, b, 4);
        }
    }

    /// <summary>
    /// The Mac OS system colour tables ('clut' resources 4 and 8) used by the 4 and 8 bit icon types, as documented in
    /// Inside Macintosh: Imaging With QuickDraw (Apple, 1994), the default colour tables. The 16 bit components are scaled to
    /// 8 bits (round(v / 257)).
    /// </summary>
    internal static class MacPalettes
    {
        // 16 colour table: white, yellow, orange, red, magenta, purple, blue, cyan, green, dark green, brown, tan, light gray,
        // medium gray, dark gray, black (16 bit RGB)
        static readonly ushort[] Clut4 =
        {
            0xFFFF, 0xFFFF, 0xFFFF, 0xFC00, 0xF37D, 0x052F, 0xFFFF, 0x648A, 0x028C, 0xDD6B, 0x08C2, 0x06A2,
            0xF2D7, 0x0856, 0x84EC, 0x46E3, 0x0000, 0xA53E, 0x0000, 0x0000, 0xD400, 0x0241, 0xAB54, 0xEAFF,
            0x1F21, 0xB793, 0x1431, 0x0000, 0x64AF, 0x11B0, 0x5600, 0x2C9D, 0x0524, 0x90D7, 0x7160, 0x3A34,
            0xC000, 0xC000, 0xC000, 0x8000, 0x8000, 0x8000, 0x4000, 0x4000, 0x4000, 0x0000, 0x0000, 0x0000,
        };

        public static readonly byte[] Palette4 = Build4();
        public static readonly byte[] Palette8 = Build8();

        static byte[] Build4()
        {
            var p = new byte[48];
            for (int i = 0; i < 48; i++) p[i] = (byte)((Clut4[i] + 128) / 257);
            return p;
        }

        /// <summary>
        /// The 256 colour table: entries 0 to 214 the 6 x 6 x 6 cube from white (each component FF, CC, 99, 66, 33, 00, blue
        /// fastest, black left out), then ten step ramps (EE, DD, BB, AA, 88, 77, 55, 44, 22, 11) of red, green, blue and gray,
        /// and black last.
        /// </summary>
        static byte[] Build8()
        {
            var p = new byte[768];
            int k = 0;
            for (int r = 0; r < 6; r++)
                for (int g = 0; g < 6; g++)
                    for (int b = 0; b < 6; b++)
                    {
                        if (r == 5 && g == 5 && b == 5) continue;
                        p[k++] = (byte)(255 - 51 * r); p[k++] = (byte)(255 - 51 * g); p[k++] = (byte)(255 - 51 * b);
                    }
            byte[] ramp = { 0xEE, 0xDD, 0xBB, 0xAA, 0x88, 0x77, 0x55, 0x44, 0x22, 0x11 };
            for (int c = 0; c < 4; c++)
                foreach (var v in ramp)
                {
                    p[k++] = c is 0 or 3 ? v : (byte)0;
                    p[k++] = c is 1 or 3 ? v : (byte)0;
                    p[k++] = c is 2 or 3 ? v : (byte)0;
                }
            // black
            k += 3;
            if (k != 768) throw new InvalidOperationException();
            return p;
        }
    }
}
