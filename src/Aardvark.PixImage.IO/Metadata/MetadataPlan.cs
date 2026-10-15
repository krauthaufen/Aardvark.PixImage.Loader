using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Aardvark.ImageCodecs
{
    /// <summary>An XMP value to write: a simple value (Array null) or an array.</summary>
    internal sealed record XmpValue(XmpArrayType? Array, string[] Values);

    /// <summary>One XMP change; <see cref="IfPresent"/>: only where the property exists.</summary>
    internal readonly record struct XmpOp(string Key, XmpValue? Value, bool IfPresent = false);

    /// <summary>One IPTC-IIM change (values null: remove).</summary>
    internal readonly record struct IptcOp(int Record, int Dataset, string[]? Values);

    /// <summary>A <see cref="MetadataEdit"/> compiled into changes of the individual metadata blocks.</summary>
    internal sealed class MetadataPlan
    {
        public bool ClearAll, ClearGps, ClearThumbnail;
        public readonly List<ExifOp> Exif = new();
        public readonly List<XmpOp> Xmp = new();
        public readonly List<IptcOp> Iptc = new();
        /// <summary>PNG text chunks to update where they exist (value null: remove).</summary>
        public readonly List<(string Keyword, string? Value)> PngText = new();
        public Dictionary<string, string> Namespaces = new();

        public bool CreatesExif => Exif.Any(o => o.Value != null && !o.IfPresent);
        public bool HasExifChanges => ClearAll || ClearGps || ClearThumbnail || Exif.Count > 0;
        public bool CreatesXmp => Xmp.Any(o => o.Value != null && !o.IfPresent);
        public bool HasXmpChanges => ClearAll || ClearGps || Xmp.Count > 0;
        public bool HasIptcChanges => ClearAll || Iptc.Count > 0;

        const ExifIfd Im = ExifIfd.Image, Ex = ExifIfd.Exif, G = ExifIfd.Gps;

        // where each field lives (for removal; setting writes the first EXIF tag and XMP key, the others when present)
        static readonly Dictionary<MetadataField, ((ExifIfd, ushort)[] Exif, string[] Xmp, (int, int)[] Iptc, string[] Png)> Places = new()
        {
            [MetadataField.Make] = (new[] { (Im, (ushort)0x010F) }, new[] { "tiff:Make" }, new (int, int)[0], new string[0]),
            [MetadataField.Model] = (new[] { (Im, (ushort)0x0110) }, new[] { "tiff:Model" }, new (int, int)[0], new string[0]),
            [MetadataField.SerialNumber] = (new[] { (Ex, (ushort)0xA431) }, new[] { "exifEX:BodySerialNumber", "aux:SerialNumber" }, new (int, int)[0], new string[0]),
            [MetadataField.LensMake] = (new[] { (Ex, (ushort)0xA433) }, new[] { "exifEX:LensMake" }, new (int, int)[0], new string[0]),
            [MetadataField.LensModel] = (new[] { (Ex, (ushort)0xA434) }, new[] { "exifEX:LensModel", "aux:Lens" }, new (int, int)[0], new string[0]),
            [MetadataField.LensSerialNumber] = (new[] { (Ex, (ushort)0xA435) }, new[] { "exifEX:LensSerialNumber", "aux:LensSerialNumber" }, new (int, int)[0], new string[0]),
            [MetadataField.LensSpecification] = (new[] { (Ex, (ushort)0xA432) }, new[] { "exifEX:LensSpecification", "aux:LensInfo" }, new (int, int)[0], new string[0]),
            [MetadataField.Software] = (new[] { (Im, (ushort)0x0131) }, new[] { "tiff:Software", "xmp:CreatorTool" }, new (int, int)[0], new[] { "Software" }),
            [MetadataField.OwnerName] = (new[] { (Ex, (ushort)0xA430) }, new[] { "exifEX:CameraOwnerName", "aux:OwnerName" }, new (int, int)[0], new string[0]),
            [MetadataField.DateTaken] = (new[] { (Ex, (ushort)0x9003), (Ex, (ushort)0x9291), (Ex, (ushort)0x9011) }, new[] { "exif:DateTimeOriginal", "photoshop:DateCreated" }, new[] { (2, 55), (2, 60) }, new[] { "Creation Time" }),
            [MetadataField.DateTakenOffset] = (new[] { (Ex, (ushort)0x9011) }, new string[0], new (int, int)[0], new string[0]),
            [MetadataField.DateDigitized] = (new[] { (Ex, (ushort)0x9004), (Ex, (ushort)0x9292), (Ex, (ushort)0x9012) }, new[] { "exif:DateTimeDigitized", "xmp:CreateDate" }, new[] { (2, 62), (2, 63) }, new string[0]),
            [MetadataField.DateModified] = (new[] { (Im, (ushort)0x0132), (Ex, (ushort)0x9290), (Ex, (ushort)0x9010) }, new[] { "xmp:ModifyDate", "tiff:DateTime" }, new (int, int)[0], new string[0]),
            [MetadataField.ExposureTime] = (new[] { (Ex, (ushort)0x829A) }, new[] { "exif:ExposureTime" }, new (int, int)[0], new string[0]),
            [MetadataField.FNumber] = (new[] { (Ex, (ushort)0x829D) }, new[] { "exif:FNumber" }, new (int, int)[0], new string[0]),
            [MetadataField.Iso] = (new[] { (Ex, (ushort)0x8827), (Ex, (ushort)0x8830), (Ex, (ushort)0x8833) }, new[] { "exif:ISOSpeedRatings", "exifEX:PhotographicSensitivity" }, new (int, int)[0], new string[0]),
            [MetadataField.FocalLength] = (new[] { (Ex, (ushort)0x920A) }, new[] { "exif:FocalLength" }, new (int, int)[0], new string[0]),
            [MetadataField.FocalLengthIn35mm] = (new[] { (Ex, (ushort)0xA405) }, new[] { "exif:FocalLengthIn35mmFilm" }, new (int, int)[0], new string[0]),
            [MetadataField.ExposureBias] = (new[] { (Ex, (ushort)0x9204) }, new[] { "exif:ExposureBiasValue" }, new (int, int)[0], new string[0]),
            [MetadataField.Flash] = (new[] { (Ex, (ushort)0x9209) }, new[] { "exif:Flash" }, new (int, int)[0], new string[0]),
            [MetadataField.ExposureProgram] = (new[] { (Ex, (ushort)0x8822) }, new[] { "exif:ExposureProgram" }, new (int, int)[0], new string[0]),
            [MetadataField.MeteringMode] = (new[] { (Ex, (ushort)0x9207) }, new[] { "exif:MeteringMode" }, new (int, int)[0], new string[0]),
            [MetadataField.WhiteBalance] = (new[] { (Ex, (ushort)0xA403) }, new[] { "exif:WhiteBalance" }, new (int, int)[0], new string[0]),
            [MetadataField.DigitalZoomRatio] = (new[] { (Ex, (ushort)0xA404) }, new[] { "exif:DigitalZoomRatio" }, new (int, int)[0], new string[0]),
            [MetadataField.SubjectDistance] = (new[] { (Ex, (ushort)0x9206) }, new[] { "exif:SubjectDistance" }, new (int, int)[0], new string[0]),
            [MetadataField.Orientation] = (new[] { (Im, (ushort)0x0112) }, new[] { "tiff:Orientation" }, new (int, int)[0], new string[0]),
            [MetadataField.Title] = (new[] { (Ex, (ushort)0xA436), (Im, (ushort)0x9C9B) }, new[] { "dc:title" }, new[] { (2, 5) }, new[] { "Title" }),
            [MetadataField.Description] = (new[] { (Im, (ushort)0x010E), (Im, (ushort)0x9C9C) }, new[] { "dc:description", "tiff:ImageDescription" }, new[] { (2, 120) }, new[] { "Description" }),
            [MetadataField.Artist] = (new[] { (Im, (ushort)0x013B), (Im, (ushort)0x9C9D) }, new[] { "dc:creator", "tiff:Artist" }, new[] { (2, 80) }, new[] { "Author" }),
            [MetadataField.Copyright] = (new[] { (Im, (ushort)0x8298) }, new[] { "dc:rights", "tiff:Copyright" }, new[] { (2, 116) }, new[] { "Copyright" }),
            [MetadataField.Keywords] = (new[] { (Im, (ushort)0x9C9E) }, new[] { "dc:subject" }, new[] { (2, 25) }, new string[0]),
            [MetadataField.Rating] = (new[] { (Im, (ushort)0x4746), (Im, (ushort)0x4749) }, new[] { "xmp:Rating" }, new (int, int)[0], new string[0]),
            [MetadataField.UserComment] = (new[] { (Ex, (ushort)0x9286) }, new[] { "exif:UserComment" }, new (int, int)[0], new string[0]),
            [MetadataField.ImageUniqueId] = (new[] { (Ex, (ushort)0xA420) }, new[] { "exifEX:ImageUniqueID", "exif:ImageUniqueID" }, new (int, int)[0], new string[0]),
            [MetadataField.City] = (new (ExifIfd, ushort)[0], new[] { "photoshop:City" }, new[] { (2, 90) }, new string[0]),
            [MetadataField.State] = (new (ExifIfd, ushort)[0], new[] { "photoshop:State" }, new[] { (2, 95) }, new string[0]),
            [MetadataField.Country] = (new (ExifIfd, ushort)[0], new[] { "photoshop:Country" }, new[] { (2, 101) }, new string[0]),
            [MetadataField.Sublocation] = (new (ExifIfd, ushort)[0], new[] { "Iptc4xmpCore:Location" }, new[] { (2, 92) }, new string[0]),
        };

        public static MetadataPlan Build(MetadataEdit e)
        {
            var p = new MetadataPlan
            {
                ClearAll = e.RemoveAll, ClearGps = e.RemoveGps, ClearThumbnail = e.RemoveThumbnail,
                Namespaces = new Dictionary<string, string>(e.Namespaces),
            };
            foreach (var f in e.RemovedFields) p.RemoveField(f);
            p.Fields(e);
            if (e.Gps != null) p.Gps(e.Gps);
            foreach (var (ifd, tag, v) in e.RawExif) p.Exif.Add(new ExifOp(ifd, tag, v));
            foreach (var (key, v) in e.RawXmp) p.Xmp.Add(new XmpOp(key, v));
            foreach (var (r, d, v) in e.RawIptc) p.Iptc.Add(new IptcOp(r, d, v));
            return p;
        }

        void RemoveField(MetadataField f)
        {
            var (exif, xmp, iptc, png) = Places[f];
            foreach (var (ifd, tag) in exif) Exif.Add(new ExifOp(ifd, tag, null));
            foreach (var k in xmp) Xmp.Add(new XmpOp(k, null));
            foreach (var (r, d) in iptc) Iptc.Add(new IptcOp(r, d, null));
            foreach (var k in png) PngText.Add((k, null));
        }

        // ------------------------------------------------------------------ helpers

        static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        static string Frac(ExifRational r) => r.Denominator == 1 ? r.Numerator.ToString(CultureInfo.InvariantCulture) : $"{r.Numerator}/{r.Denominator}";

        void SetExif(ExifIfd ifd, ushort tag, ExifValue v, bool ifPresent = false)
            => Exif.Add(new ExifOp(ifd, tag, v, ifPresent, ifd == Ex ? MinVersion(tag) : ifd == G ? MinGpsVersion(tag) : 0));

        /// <summary>The GPS version that introduced a tag (2.2: processing method, area, date stamp, differential; 2.3: positioning error).</summary>
        static int MinGpsVersion(ushort tag) => tag switch { >= 0x001B and <= 0x001E => 220, 0x001F => 230, _ => 0 };

        /// <summary>The EXIF version that introduced a tag of the EXIF directory (2.3: owner, serial numbers, lens, sensitivity
        /// type; 2.31: offset times), 0 for older ones.</summary>
        static int MinVersion(ushort tag) => tag switch
        {
            >= 0x9010 and <= 0x9012 => 231,
            >= 0xA430 and <= 0xA435 or >= 0x8830 and <= 0x8835 => 230,
            _ => 0,
        };
        void RemoveExif(ExifIfd ifd, ushort tag) => Exif.Add(new ExifOp(ifd, tag, null));
        void SetXmp(string key, string value, bool ifPresent) => Xmp.Add(new XmpOp(key, new XmpValue(null, new[] { value }), ifPresent));
        void SetXmpArray(string key, XmpArrayType t, IEnumerable<string> values, bool ifPresent) => Xmp.Add(new XmpOp(key, new XmpValue(t, values.ToArray()), ifPresent));

        /// <summary>A text field: the first EXIF place as ASCII (UTF-8 bytes when needed), the XMP places when present.</summary>
        void Text(MetadataField f, string value, bool xmpAlways = false)
        {
            var (exif, xmp, iptc, png) = Places[f];
            if (exif.Length > 0) SetExif(exif[0].Item1, exif[0].Item2, ExifValue.Ascii(value));
            for (int i = 0; i < xmp.Length; i++) SetXmp(xmp[i], value, !(xmpAlways && i == 0));
            foreach (var (r, d) in iptc) Iptc.Add(new IptcOp(r, d, new[] { value }));
            foreach (var k in png) PngText.Add((k, value));
        }

        static ExifValue Xp(string s)
        {
            var b = Encoding.Unicode.GetBytes(s + "\0");
            return new ExifValue { Type = ExifType.Byte, Count = b.Length, Data = b };
        }

        /// <summary>UserComment / GPSProcessingMethod: character code prefix and text.</summary>
        internal static ExifValue CodedText(string s)
        {
            bool ascii = s.All(c => c < 0x80);
            var body = ascii ? Encoding.ASCII.GetBytes(s) : Encoding.Unicode.GetBytes(s);
            var d = new byte[8 + body.Length];
            (ascii ? "ASCII\0\0\0"u8 : "UNICODE\0"u8).CopyTo(d);
            body.CopyTo(d, 8);
            return new ExifValue { Type = ExifType.Undefined, Count = d.Length, Data = d, Utf16Body = !ascii };
        }

        static string ExifDate(DateTime t) => t.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture);

        static string? SubSec(DateTime t)
        {
            long f = t.Ticks % TimeSpan.TicksPerSecond;
            return f == 0 ? null : f.ToString("D7", CultureInfo.InvariantCulture).TrimEnd('0');
        }

        static string Offset(TimeSpan o) => (o < TimeSpan.Zero ? "-" : "+") + o.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);

        internal static string XmpDate(DateTime t, TimeSpan? off)
        {
            var s = t.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
            if (SubSec(t) is string ss) s += "." + ss;
            if (off is TimeSpan o) s += Offset(o);
            return s;
        }

        void Date(DateTime t, TimeSpan? off, (ExifIfd, ushort) date, ushort subSec, ushort offTag, string[] xmp, (int, int)[]? iptc)
        {
            if (off is TimeSpan o && (o.Ticks % TimeSpan.TicksPerMinute != 0 || o.Duration() > TimeSpan.FromHours(14))) throw new ArgumentException("bad UTC offset");
            SetExif(date.Item1, date.Item2, ExifValue.Ascii(ExifDate(t)));
            if (SubSec(t) is string ss) SetExif(Ex, subSec, ExifValue.Ascii(ss)); else RemoveExif(Ex, subSec);
            if (off is TimeSpan of) SetExif(Ex, offTag, ExifValue.Ascii(Offset(of)));
            foreach (var k in xmp) SetXmp(k, XmpDate(t, off), true);
            if (iptc != null)
            {
                Iptc.Add(new IptcOp(iptc[0].Item1, iptc[0].Item2, new[] { t.ToString("yyyyMMdd", CultureInfo.InvariantCulture) }));
                var time = t.ToString("HHmmss", CultureInfo.InvariantCulture) + (off is TimeSpan x ? Offset(x).Replace(":", "") : "+0000");
                Iptc.Add(new IptcOp(iptc[1].Item1, iptc[1].Item2, new[] { time }));
            }
        }

        // ------------------------------------------------------------------ the descriptive fields

        void Fields(MetadataEdit e)
        {
            if (e.Make != null) Text(MetadataField.Make, e.Make);
            if (e.Model != null) Text(MetadataField.Model, e.Model);
            if (e.SerialNumber != null) Text(MetadataField.SerialNumber, e.SerialNumber);
            if (e.LensMake != null) Text(MetadataField.LensMake, e.LensMake);
            if (e.LensModel != null) Text(MetadataField.LensModel, e.LensModel);
            if (e.LensSerialNumber != null) Text(MetadataField.LensSerialNumber, e.LensSerialNumber);
            if (e.Software != null) Text(MetadataField.Software, e.Software);
            if (e.OwnerName != null) Text(MetadataField.OwnerName, e.OwnerName);
            if (e.LensSpecification is { } ls)
            {
                if (ls.Length != 4) throw new ArgumentException("LensSpecification needs four values");
                var r = ls.Select(v => ExifValue.Rational(v, false)).ToArray();
                SetExif(Ex, 0xA432, ExifValue.Rationals(false, r));
                SetXmpArray("exifEX:LensSpecification", XmpArrayType.Seq, r.Select(Frac), true);
            }

            if (e.DateTaken is DateTime dt) Date(dt, e.DateTakenOffset, (Ex, 0x9003), 0x9291, 0x9011, Places[MetadataField.DateTaken].Xmp, new[] { (2, 55), (2, 60) });
            else if (e.DateTakenOffset is TimeSpan o) SetExif(Ex, 0x9011, ExifValue.Ascii(Offset(o)));
            if (e.DateDigitized is DateTime dd) Date(dd, e.DateDigitizedOffset, (Ex, 0x9004), 0x9292, 0x9012, Places[MetadataField.DateDigitized].Xmp, new[] { (2, 62), (2, 63) });
            else if (e.DateDigitizedOffset is TimeSpan o2) SetExif(Ex, 0x9012, ExifValue.Ascii(Offset(o2)));
            if (e.DateModified is DateTime dm) Date(dm, e.DateModifiedOffset, (Im, 0x0132), 0x9290, 0x9010, Places[MetadataField.DateModified].Xmp, null);
            else if (e.DateModifiedOffset is TimeSpan o3) SetExif(Ex, 0x9010, ExifValue.Ascii(Offset(o3)));
            if (e.DateTaken is DateTime ct)
                PngText.Add(("Creation Time", XmpDate(ct, e.DateTakenOffset)));

            void Rat(double? v, ushort tag, string xmp, bool signed = false)
            {
                if (v is not double x) return;
                var r = ExifValue.Rational(x, signed);
                SetExif(Ex, tag, ExifValue.Rationals(signed, r));
                SetXmp(xmp, Frac(r), true);
            }
            void Short(int? v, ushort tag, string xmp, int max = 65535)
            {
                if (v is not int x) return;
                if (x < 0 || x > max) throw new ArgumentOutOfRangeException(xmp, "value out of range");
                SetExif(Ex, tag, ExifValue.Shorts(x));
                SetXmp(xmp, x.ToString(CultureInfo.InvariantCulture), true);
            }
            if (e.ExposureTime is <= 0) throw new ArgumentOutOfRangeException(nameof(e.ExposureTime));
            Rat(e.ExposureTime, 0x829A, "exif:ExposureTime");
            Rat(e.FNumber, 0x829D, "exif:FNumber");
            Rat(e.FocalLength, 0x920A, "exif:FocalLength");
            Rat(e.ExposureBias, 0x9204, "exif:ExposureBiasValue", true);
            Rat(e.DigitalZoomRatio, 0xA404, "exif:DigitalZoomRatio");
            Rat(e.SubjectDistance, 0x9206, "exif:SubjectDistance");
            if (e.Iso is int iso)
            {
                if (iso <= 0) throw new ArgumentOutOfRangeException(nameof(e.Iso));
                SetExif(Ex, 0x8827, ExifValue.Shorts(Math.Min(iso, 65535)));
                if (iso >= 65535) { SetExif(Ex, 0x8830, ExifValue.Shorts(3)); SetExif(Ex, 0x8833, ExifValue.Longs((uint)iso)); }
                else RemoveExif(Ex, 0x8833);
                var s = iso.ToString(CultureInfo.InvariantCulture);
                SetXmpArray("exif:ISOSpeedRatings", XmpArrayType.Seq, new[] { s }, true);
                SetXmp("exifEX:PhotographicSensitivity", s, true);
            }
            Short(e.FocalLengthIn35mm, 0xA405, "exif:FocalLengthIn35mmFilm");
            if (e.Flash is int fl)
            {
                if (fl is < 0 or > 0x7F) throw new ArgumentOutOfRangeException(nameof(e.Flash));
                SetExif(Ex, 0x9209, ExifValue.Shorts(fl));
                Xmp.Add(new XmpOp("exif:Flash", null));   // a structure in XMP: dropped rather than left stale
            }
            Short(e.ExposureProgram, 0x8822, "exif:ExposureProgram");
            Short(e.MeteringMode, 0x9207, "exif:MeteringMode");
            Short(e.WhiteBalance, 0xA403, "exif:WhiteBalance");
            if (e.Orientation is int or)
            {
                if (or is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(e.Orientation), "orientation must be 1..8");
                SetExif(Im, 0x0112, ExifValue.Shorts(or));
                SetXmp("tiff:Orientation", or.ToString(CultureInfo.InvariantCulture), true);
            }

            if (e.Title != null)
            {
                Exif.Add(new ExifOp(Ex, 0xA436, ExifValue.Ascii(e.Title), MinVersion: -300));
                SetExif(Im, 0x9C9B, Xp(e.Title), true);
                SetXmpArray("dc:title", XmpArrayType.Alt, new[] { e.Title }, false);
                Iptc.Add(new IptcOp(2, 5, new[] { e.Title }));
                PngText.Add(("Title", e.Title));
            }
            if (e.Description != null)
            {
                SetExif(Im, 0x010E, ExifValue.Ascii(e.Description));
                SetExif(Im, 0x9C9C, Xp(e.Description), true);
                SetXmpArray("dc:description", XmpArrayType.Alt, new[] { e.Description }, false);
                SetXmp("tiff:ImageDescription", e.Description, true);
                Iptc.Add(new IptcOp(2, 120, new[] { e.Description }));
                PngText.Add(("Description", e.Description));
            }
            if (e.Artist != null)
            {
                SetExif(Im, 0x013B, ExifValue.Ascii(e.Artist));
                SetExif(Im, 0x9C9D, Xp(e.Artist), true);
                SetXmpArray("dc:creator", XmpArrayType.Seq, new[] { e.Artist }, false);
                SetXmp("tiff:Artist", e.Artist, true);
                Iptc.Add(new IptcOp(2, 80, new[] { e.Artist }));
                PngText.Add(("Author", e.Artist));
            }
            if (e.Copyright != null)
            {
                SetExif(Im, 0x8298, ExifValue.Ascii(e.Copyright));
                SetXmpArray("dc:rights", XmpArrayType.Alt, new[] { e.Copyright }, false);
                SetXmp("tiff:Copyright", e.Copyright, true);
                Iptc.Add(new IptcOp(2, 116, new[] { e.Copyright }));
                PngText.Add(("Copyright", e.Copyright));
            }
            if (e.Keywords != null)
            {
                var kw = e.Keywords.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()).Distinct().ToArray();
                SetExif(Im, 0x9C9E, Xp(string.Join(";", kw)), true);
                SetXmpArray("dc:subject", XmpArrayType.Bag, kw, false);
                Iptc.Add(new IptcOp(2, 25, kw));
            }
            if (e.Rating is int rating)
            {
                if (rating is < -1 or > 5) throw new ArgumentOutOfRangeException(nameof(e.Rating), "rating must be -1..5");
                SetXmp("xmp:Rating", rating.ToString(CultureInfo.InvariantCulture), false);
                SetExif(Im, 0x4746, ExifValue.Shorts(Math.Max(0, rating)), true);
                SetExif(Im, 0x4749, ExifValue.Shorts(rating switch { <= 0 => 0, 1 => 1, 5 => 99, _ => (rating - 1) * 25 }), true);
            }
            if (e.UserComment != null)
            {
                SetExif(Ex, 0x9286, CodedText(e.UserComment));
                SetXmpArray("exif:UserComment", XmpArrayType.Alt, new[] { e.UserComment }, true);
            }
            if (e.ImageUniqueId != null)
            {
                SetExif(Ex, 0xA420, ExifValue.Ascii(e.ImageUniqueId));
                SetXmp("exifEX:ImageUniqueID", e.ImageUniqueId, true);
                SetXmp("exif:ImageUniqueID", e.ImageUniqueId, true);
            }
            foreach (var (f, v) in new[] { (MetadataField.City, e.City), (MetadataField.State, e.State), (MetadataField.Country, e.Country), (MetadataField.Sublocation, e.Sublocation) })
                if (v != null) Text(f, v, xmpAlways: true);
        }

        // ------------------------------------------------------------------ GPS

        /// <summary>Degrees as degrees, minutes and seconds (micro-second resolution).</summary>
        static ExifValue Dms(double v)
        {
            long us = (long)Math.Round(Math.Abs(v) * 3600e6);
            long deg = us / 3_600_000_000L, min = us / 60_000_000L % 60, sec = us % 60_000_000L;
            long a = sec, b = 1_000_000;
            while (b != 0) (a, b) = (b, a % b);
            long g = Math.Max(1, a);
            return ExifValue.Rationals(false, new ExifRational(deg, 1), new ExifRational(min, 1), new ExifRational(sec / g, 1_000_000 / g));
        }

        /// <summary>XMP GPSCoordinate "DDD,MM.mmmmmmmmK".</summary>
        static string XmpCoordinate(double v, char pos, char neg)
        {
            double a = Math.Abs(v);
            int deg = (int)Math.Floor(a);
            double min = (a - deg) * 60;
            return deg.ToString(CultureInfo.InvariantCulture) + "," + min.ToString("0.########", CultureInfo.InvariantCulture) + (v < 0 ? neg : pos);
        }

        void Gps(GpsEdit g)
        {
            if (g.Latitude is double la)
            {
                if (!(la >= -90 && la <= 90)) throw new ArgumentOutOfRangeException(nameof(g.Latitude));
                SetExif(G, 0x0001, ExifValue.Ascii(la < 0 ? "S" : "N"));
                SetExif(G, 0x0002, Dms(la));
                SetXmp("exif:GPSLatitude", XmpCoordinate(la, 'N', 'S'), true);
                SetXmp("drone-dji:GpsLatitude", Num(la), true);
            }
            if (g.Longitude is double lo)
            {
                if (!(lo >= -180 && lo <= 180)) throw new ArgumentOutOfRangeException(nameof(g.Longitude));
                SetExif(G, 0x0003, ExifValue.Ascii(lo < 0 ? "W" : "E"));
                SetExif(G, 0x0004, Dms(lo));
                SetXmp("exif:GPSLongitude", XmpCoordinate(lo, 'E', 'W'), true);
                SetXmp("drone-dji:GpsLongitude", Num(lo), true);
                SetXmp("drone-dji:GpsLongtitude", Num(lo), true);
            }
            if (g.Altitude is double alt)
            {
                if (!double.IsFinite(alt)) throw new ArgumentOutOfRangeException(nameof(g.Altitude));
                int aref = (alt < 0 ? 1 : 0) + (g.AltitudeAboveEllipsoid ? 2 : 0);
                SetExif(G, 0x0005, ExifValue.Bytes((byte)aref));
                var r = ExifValue.Rational(Math.Abs(alt), false);
                SetExif(G, 0x0006, ExifValue.Rationals(false, r));
                SetXmp("exif:GPSAltitudeRef", aref.ToString(CultureInfo.InvariantCulture), true);
                SetXmp("exif:GPSAltitude", Frac(r), true);
            }
            if (g.TimeUtc is DateTime t)
            {
                if (t.Kind == DateTimeKind.Local) t = t.ToUniversalTime();
                SetExif(G, 0x001D, ExifValue.Ascii(t.ToString("yyyy:MM:dd", CultureInfo.InvariantCulture)));
                double sec = t.Second + (t.Ticks % TimeSpan.TicksPerSecond) / (double)TimeSpan.TicksPerSecond;
                SetExif(G, 0x0007, ExifValue.Rationals(false, new ExifRational(t.Hour, 1), new ExifRational(t.Minute, 1), ExifValue.Rational(Math.Round(sec, 6), false)));
                SetXmp("exif:GPSTimeStamp", XmpDate(new DateTime(t.Ticks - t.Ticks % 10, DateTimeKind.Unspecified), TimeSpan.Zero).Replace("+00:00", "Z"), true);
            }
            void Dir(double? v, ushort refTag, ushort tag, bool magnetic, string xmp)
            {
                if (v is not double d) return;
                if (!(d >= 0 && d < 360.0000001)) throw new ArgumentOutOfRangeException(xmp, "direction must be 0..360");
                var r = ExifValue.Rational(d, false);
                SetExif(G, refTag, ExifValue.Ascii(magnetic ? "M" : "T"));
                SetExif(G, tag, ExifValue.Rationals(false, r));
                SetXmp(xmp + "Ref", magnetic ? "M" : "T", true);
                SetXmp(xmp, Frac(r), true);
            }
            Dir(g.Track, 0x000E, 0x000F, g.TrackIsMagnetic, "exif:GPSTrack");
            Dir(g.ImageDirection, 0x0010, 0x0011, g.ImageDirectionIsMagnetic, "exif:GPSImgDirection");
            Dir(g.DestinationBearing, 0x0017, 0x0018, false, "exif:GPSDestBearing");
            if (g.Speed is double sp)
            {
                if (!(sp >= 0) || double.IsInfinity(sp)) throw new ArgumentOutOfRangeException(nameof(g.Speed));
                var r = ExifValue.Rational(Math.Round(sp * 3.6, 6), false);
                SetExif(G, 0x000C, ExifValue.Ascii("K"));
                SetExif(G, 0x000D, ExifValue.Rationals(false, r));
                SetXmp("exif:GPSSpeedRef", "K", true);
                SetXmp("exif:GPSSpeed", Frac(r), true);
            }
            if (g.HorizontalError is double he)
            {
                if (!(he >= 0) || double.IsInfinity(he)) throw new ArgumentOutOfRangeException(nameof(g.HorizontalError));
                var r = ExifValue.Rational(he, false);
                SetExif(G, 0x001F, ExifValue.Rationals(false, r));
                SetXmp("exif:GPSHPositioningError", Frac(r), true);
            }
            if (g.Dop is double dop)
            {
                if (!(dop >= 0) || double.IsInfinity(dop)) throw new ArgumentOutOfRangeException(nameof(g.Dop));
                var r = ExifValue.Rational(dop, false);
                SetExif(G, 0x000B, ExifValue.Rationals(false, r));
                SetXmp("exif:GPSDOP", Frac(r), true);
            }
            if (g.MapDatum != null) { SetExif(G, 0x0012, ExifValue.Ascii(g.MapDatum)); SetXmp("exif:GPSMapDatum", g.MapDatum, true); }
            if (g.Satellites != null) { SetExif(G, 0x0008, ExifValue.Ascii(g.Satellites)); SetXmp("exif:GPSSatellites", g.Satellites, true); }
            if (g.Status != null) { SetExif(G, 0x0009, ExifValue.Ascii(g.Status)); SetXmp("exif:GPSStatus", g.Status, true); }
            if (g.MeasureMode != null) { SetExif(G, 0x000A, ExifValue.Ascii(g.MeasureMode)); SetXmp("exif:GPSMeasureMode", g.MeasureMode, true); }
            if (g.Differential is bool df) { SetExif(G, 0x001E, ExifValue.Shorts(df ? 1 : 0)); SetXmp("exif:GPSDifferential", df ? "1" : "0", true); }
            if (g.ProcessingMethod != null) { SetExif(G, 0x001B, CodedText(g.ProcessingMethod)); SetXmp("exif:GPSProcessingMethod", g.ProcessingMethod, true); }
        }

        /// <summary>XMP properties that RemoveGps takes away.</summary>
        public static bool IsGpsProperty(string key)
        {
            int c = key.IndexOf(':');
            if (c <= 0) return false;
            string prefix = key[..c], local = key[(c + 1)..];
            int slash = local.IndexOfAny(new[] { '/', '[' });
            if (slash >= 0) local = local[..slash];
            if (prefix is "exif" or "exifEX" or "drone-dji" or "drone-parrot" or "Camera" && local.StartsWith("GPS", StringComparison.OrdinalIgnoreCase)) return true;
            return prefix == "drone-dji" && local is "AbsoluteAltitude" or "GpsStatus" or "RtkStdLat" or "RtkStdLon" or "RtkStdHgt";
        }
    }
}
