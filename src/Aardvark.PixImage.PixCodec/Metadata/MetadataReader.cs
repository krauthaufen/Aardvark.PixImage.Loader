using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Aardvark.ImageCodecs
{
    /// <summary>Interprets the collected metadata blocks into a <see cref="PhotoMetadata"/>.</summary>
    internal static class MetadataReader
    {
        public static PhotoMetadata Read(ReadOnlySpan<byte> data)
        {
            var b = MetadataScanner.Scan(data);
            var m = new PhotoMetadata { Format = b.Format };
            m.Exif = b.TiffIfd0 ?? (b.Exif != null ? ExifReader.Parse(b.Exif, false) : null);
            if (b.Xmp.Count > 0)
            {
                var x = new XmpData();
                bool any = false;
                foreach (var p in b.Xmp) any |= x.Add(p);
                if (any) m.Xmp = x;
            }
            if (b.Iptc != null) m.Iptc = IptcData.Parse(b.Iptc);
            m.IccProfile = b.Icc;
            m.IccDescription = b.Icc != null ? IccDescription(b.Icc) : null;
            m.Comments = b.Comments;
            m.Text = b.Text;
            m.ExrAttributes = b.ExrAttributes;
            m.Thumbnail = m.Exif?.Thumbnail;
            Fill(m);
            m.HasGainMap |= b.GainMap;
            return m;
        }

        static void Fill(PhotoMetadata m)
        {
            var e = m.Exif;
            var x = m.Xmp;
            var i = m.Iptc;

            string? S(ExifIfd ifd, ushort tag) => e?.Get(ifd, tag) is ExifEntry v && v.ToString() is { Length: > 0 } s ? s.Trim() : null;
            double? D(ExifIfd ifd, ushort tag, int k = 0) => e?.Get(ifd, tag) is ExifEntry v && v.GetDouble(k) is double d && !double.IsNaN(d) && !double.IsInfinity(d) ? d : null;
            string? X(string key) => x?[key] is { Length: > 0 } s ? s.Trim() : null;
            double? XD(string key) => ParseNumber(X(key));
            string? I(string name) => i?[name] is { Length: > 0 } s ? s.Trim() : null;
            string? T(string key) { foreach (var kv in m.Text) if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase) && kv.Value.Length > 0) return kv.Value.Trim(); return null; }
            ExrAttribute? A(string name) => m.ExrAttributes?.FirstOrDefault(a => a.Name == name);
            double? AF(string name) => A(name) is ExrAttribute a ? a.TypeName switch { "float" => a.AsFloat(), "double" => a.AsDouble(), "int" => a.AsInt(), "rational" => a.AsRational() is var r && r.Denominator != 0 ? (double)r.Numerator / r.Denominator : null, _ => null } : null;
            string? AS(string name) => A(name) is ExrAttribute a && a.TypeName == "string" ? a.AsString() : null;

            const ExifIfd Im = ExifIfd.Image, Ex = ExifIfd.Exif;

            m.Make = S(Im, 0x010F) ?? X("tiff:Make");
            m.Model = S(Im, 0x0110) ?? X("tiff:Model");
            m.SerialNumber = S(Ex, 0xA431) ?? S(Im, 0xC62F) ?? X("exifEX:BodySerialNumber") ?? X("exifEX:SerialNumber") ?? X("aux:SerialNumber") ?? X("drone-dji:CameraSerialNumber") ?? X("Camera:BodySerialNumber");
            m.LensMake = S(Ex, 0xA433) ?? X("exifEX:LensMake");
            m.LensModel = S(Ex, 0xA434) ?? X("exifEX:LensModel") ?? X("aux:Lens");
            m.LensSerialNumber = S(Ex, 0xA435) ?? X("exifEX:LensSerialNumber") ?? X("aux:LensSerialNumber");
            if (e?.Get(Ex, 0xA432) is ExifEntry ls && ls.Length == 4) m.LensSpecification = Enumerable.Range(0, 4).Select(k => ls.GetDouble(k)).ToArray();
            m.Software = S(Im, 0x0131) ?? X("tiff:Software") ?? X("xmp:CreatorTool") ?? T("Software") ?? AS("software");
            m.OwnerName = S(Ex, 0xA430) ?? X("exifEX:CameraOwnerName") ?? X("aux:OwnerName");

            // dates
            (m.DateTaken, m.DateTakenOffset) = ExifDate(S(Ex, 0x9003), S(Ex, 0x9291), S(Ex, 0x9011) ?? S(Im, 0x882A));
            if (m.DateTaken == null) (m.DateTaken, m.DateTakenOffset) = IsoDate(X("exif:DateTimeOriginal") ?? X("photoshop:DateCreated") ?? X("xmp:CreateDate"));
            if (m.DateTaken == null) (m.DateTaken, m.DateTakenOffset) = IptcDate(I("DateCreated"), I("TimeCreated"));
            if (m.DateTaken == null && AS("capDate") is string cap)
            {
                (m.DateTaken, _) = ExifDate(cap, null, null);
                if (m.DateTaken != null && AF("utcOffset") is double uo) m.DateTakenOffset = TimeSpan.FromSeconds(-Math.Round(uo));
            }
            if (m.DateTaken == null) (m.DateTaken, m.DateTakenOffset) = LooseDate(T("Creation Time"));
            (m.DateDigitized, m.DateDigitizedOffset) = ExifDate(S(Ex, 0x9004), S(Ex, 0x9292), S(Ex, 0x9012));
            if (m.DateDigitized == null) (m.DateDigitized, m.DateDigitizedOffset) = IsoDate(X("exif:DateTimeDigitized") ?? X("xmp:CreateDate"));
            (m.DateModified, m.DateModifiedOffset) = ExifDate(S(Im, 0x0132), S(Ex, 0x9290), S(Ex, 0x9010));
            if (m.DateModified == null) (m.DateModified, m.DateModifiedOffset) = IsoDate(X("xmp:ModifyDate"));
            if (m.DateModified == null) (m.DateModified, m.DateModifiedOffset) = IsoDate(T("tIME"));

            // exposure
            m.ExposureTime = D(Ex, 0x829A) ?? D(Im, 0x829A) ?? XD("exif:ExposureTime") ?? AF("expTime");
            m.FNumber = D(Ex, 0x829D) ?? D(Im, 0x829D) ?? XD("exif:FNumber") ?? AF("aperture");
            double? iso = D(Ex, 0x8827);
            if (iso is null or >= 65535) iso = D(Ex, 0x8832) ?? D(Ex, 0x8833) ?? iso;
            iso ??= XD("exif:ISOSpeedRatings") ?? XD("exifEX:PhotographicSensitivity") ?? AF("isoSpeed");
            m.Iso = iso is double isv && isv > 0 ? (int)Math.Round(isv) : null;
            m.FocalLength = D(Ex, 0x920A) ?? XD("exif:FocalLength");
            m.FocalLengthIn35mm = D(Ex, 0xA405) is double f35 && f35 > 0 ? f35 : XD("exif:FocalLengthIn35mmFilm");
            m.ExposureBias = D(Ex, 0x9204) ?? XD("exif:ExposureBiasValue");
            m.Flash = (int?)D(Ex, 0x9209);
            if (m.Flash == null && X("exif:Flash/exif:Fired") is string fired) m.Flash = fired.Equals("True", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            m.ExposureProgram = (int?)(D(Ex, 0x8822) ?? XD("exif:ExposureProgram"));
            m.MeteringMode = (int?)(D(Ex, 0x9207) ?? XD("exif:MeteringMode"));
            m.WhiteBalance = (int?)(D(Ex, 0xA403) ?? XD("exif:WhiteBalance"));
            m.DigitalZoomRatio = D(Ex, 0xA404) ?? XD("exif:DigitalZoomRatio");
            m.SubjectDistance = D(Ex, 0x9206) ?? XD("exif:SubjectDistance") ?? AF("focus");
            m.Orientation = (int)(D(Im, 0x0112) ?? XD("tiff:Orientation") ?? 0);
            if (m.Orientation is < 0 or > 8) m.Orientation = 0;

            // description
            m.Title = X("dc:title") ?? I("ObjectName") ?? S(Ex, 0xA436) ?? S(Im, 0x9C9B) ?? T("Title");
            m.Description = S(Im, 0x010E) ?? X("dc:description") ?? X("tiff:ImageDescription") ?? I("Caption-Abstract") ?? T("Description") ?? S(Im, 0x9C9C) ?? AS("comments");
            m.Artist = S(Im, 0x013B) ?? X("dc:creator") ?? X("tiff:Artist") ?? I("By-line") ?? S(Im, 0x9C9D) ?? T("Author") ?? AS("owner");
            m.Copyright = S(Im, 0x8298) ?? X("dc:rights") ?? X("tiff:Copyright") ?? I("CopyrightNotice") ?? T("Copyright");
            var kw = new List<string>();
            void Kw(IEnumerable<string>? ks) { if (ks != null) foreach (var k in ks) { var t = k.Trim(); if (t.Length > 0 && !kw.Contains(t, StringComparer.OrdinalIgnoreCase)) kw.Add(t); } }
            Kw(x?.Get("dc:subject")?.Values);
            Kw(i?.GetAll("Keywords"));
            Kw(S(Im, 0x9C9E)?.Split(';'));
            m.Keywords = kw;
            var rating = XD("xmp:Rating") ?? D(Im, 0x4746);
            m.Rating = rating is double r ? (int)Math.Round(r) : null;
            m.UserComment = S(Ex, 0x9286) ?? X("exif:UserComment");
            m.City = X("photoshop:City") ?? I("City");
            m.State = X("photoshop:State") ?? I("Province-State");
            m.Country = X("photoshop:Country") ?? I("Country-PrimaryLocationName");
            m.Sublocation = X("Iptc4xmpCore:Location") ?? I("Sub-location");
            m.ImageUniqueId = S(Ex, 0xA420) ?? X("exifEX:ImageUniqueID") ?? X("exif:ImageUniqueID");
            m.HasGainMap = x?.Properties.Any(p => p.Namespace == "http://ns.adobe.com/hdr-gain-map/1.0/") == true;

            m.Gps = Gps(e, x, AF);
            m.Pose = Pose(x);
            m.Panorama = Panorama(x);
        }

        // ------------------------------------------------------------------ GPS

        static GpsInfo? Gps(ExifData? e, XmpData? x, Func<string, double?> exr)
        {
            var g = new GpsInfo();
            bool any = false;
            const ExifIfd G = ExifIfd.Gps;
            string? S(ushort tag) => e?.Get(G, tag) is ExifEntry v && v.ToString() is { Length: > 0 } s ? s.Trim() : null;
            ExifEntry? E(ushort tag) => e?.Get(G, tag);
            double? D(ushort tag) => E(tag) is ExifEntry v && v.GetDouble() is double d && !double.IsNaN(d) && !double.IsInfinity(d) ? d : null;
            string? X(string key) => x?[key] is { Length: > 0 } s ? s.Trim() : null;

            double? Dms(ExifEntry? v, string? reference, char negative)
            {
                if (v == null || v.Length == 0) return null;
                double deg = v.GetDouble(0), min = v.Length > 1 ? v.GetDouble(1) : 0, sec = v.Length > 2 ? v.GetDouble(2) : 0;
                if (double.IsNaN(min)) min = 0;
                if (double.IsNaN(sec)) sec = 0;
                if (double.IsNaN(deg)) return null;
                double r = deg + min / 60 + sec / 3600;
                return reference != null && reference.Length > 0 && char.ToUpperInvariant(reference[0]) == negative ? -r : r;
            }

            g.Latitude = Dms(E(0x0002), S(0x0001), 'S');
            g.Longitude = Dms(E(0x0004), S(0x0003), 'W');
            if (g.Latitude is double la && g.Longitude is double lo && la == 0 && lo == 0 && E(0x0001) == null) g.Latitude = g.Longitude = null;   // empty placeholders
            if (D(0x0006) is double alt)
            {
                int aref = (int)(E(0x0005)?.GetDouble() is double ar && !double.IsNaN(ar) ? ar : 0);
                g.Altitude = aref is 1 or 3 ? -alt : alt;
                g.AltitudeReference = E(0x0005) != null ? aref : null;
            }
            // GPS time: date stamp + hh:mm:ss
            if (S(0x001D) is string ds && DateTime.TryParseExact(ds.Replace('-', ':'), "yyyy:MM:dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            {
                var ts = E(0x0007);
                double secs = ts != null && ts.Length >= 3 ? ts.GetDouble(0) * 3600 + ts.GetDouble(1) * 60 + ts.GetDouble(2) : 0;
                if (!double.IsNaN(secs) && secs >= 0 && secs < 86400 * 2) g.TimeUtc = DateTime.SpecifyKind(day.AddSeconds(secs), DateTimeKind.Utc);
            }
            if (D(0x000D) is double sp)
            {
                string unit = S(0x000C) ?? "K";
                g.Speed = unit.ToUpperInvariant() switch { "M" => sp * 1609.344 / 3600, "N" => sp * 1852 / 3600, _ => sp / 3.6 };
            }
            g.Track = D(0x000F); g.TrackIsMagnetic = S(0x000E) == "M";
            g.ImageDirection = D(0x0011); g.ImageDirectionIsMagnetic = S(0x0010) == "M";
            g.DestinationBearing = D(0x0018);
            g.Dop = D(0x000B);
            g.HorizontalError = D(0x001F);
            g.MapDatum = S(0x0012);
            g.Satellites = S(0x0008);
            g.Status = S(0x0009);
            g.MeasureMode = S(0x000A);
            g.Differential = D(0x001E) is double dd ? dd == 1 : null;
            g.ProcessingMethod = E(0x001B) is ExifEntry pm ? (pm.Value is byte[] pb ? ExifReader.ExifText(0x9286, pb) : pm.ToString()) : null;
            if (string.IsNullOrWhiteSpace(g.ProcessingMethod)) g.ProcessingMethod = null;

            // XMP: exif:GPS* as "DDD,MM.mmK" / "DDD,MM,SSK", drone and camera namespaces as decimal degrees
            g.Latitude ??= XmpCoordinate(X("exif:GPSLatitude")) ?? ParseNumber(X("drone-dji:GpsLatitude")) ?? ParseNumber(X("drone-parrot:GPSLatitude")) ?? exr("latitude");
            g.Longitude ??= XmpCoordinate(X("exif:GPSLongitude")) ?? ParseNumber(X("drone-dji:GpsLongitude")) ?? ParseNumber(X("drone-dji:GpsLongtitude"))
                ?? ParseNumber(X("drone-parrot:GPSLongitude")) ?? exr("longitude");
            if (g.Altitude == null)
            {
                if (ParseNumber(X("exif:GPSAltitude")) is double xa) g.Altitude = X("exif:GPSAltitudeRef") == "1" ? -xa : xa;
                else g.Altitude = ParseNumber(X("drone-dji:AbsoluteAltitude")) ?? exr("altitude");
            }
            g.TimeUtc ??= IsoDate(X("exif:GPSTimeStamp")) is (DateTime t, var off) ? DateTime.SpecifyKind(off is TimeSpan o ? t - o : t, DateTimeKind.Utc) : null;
            g.ImageDirection ??= ParseNumber(X("exif:GPSImgDirection"));
            g.Track ??= ParseNumber(X("exif:GPSTrack"));
            g.MapDatum ??= X("exif:GPSMapDatum");
            g.Dop ??= ParseNumber(X("exif:GPSDOP"));
            if (g.Speed == null && ParseNumber(X("exif:GPSSpeed")) is double xs)
                g.Speed = (X("exif:GPSSpeedRef") ?? "K").ToUpperInvariant() switch { "M" => xs * 1609.344 / 3600, "N" => xs * 1852 / 3600, _ => xs / 3.6 };
            if (g.ImageDirection != null && X("exif:GPSImgDirectionRef") == "M" && e?.Get(G, 0x0011) == null) g.ImageDirectionIsMagnetic = true;
            g.HorizontalError ??= ParseNumber(X("exif:GPSHPositioningError")) ?? ParseNumber(X("Camera:GPSXYAccuracy"));
            g.VerticalError ??= ParseNumber(X("Camera:GPSZAccuracy"));

            any = g.Latitude != null || g.Longitude != null || g.Altitude != null || g.TimeUtc != null || g.ImageDirection != null
                || g.Speed != null || g.Track != null || e?.Entries.Any(v => v.Ifd == G && v.Tag != 0) == true;
            return any ? g : null;
        }

        /// <summary>XMP GPSCoordinate: "DDD,MM,SSk" or "DDD,MM.mmk" with k = N, S, E, W.</summary>
        internal static double? XmpCoordinate(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            char dir = char.ToUpperInvariant(s[^1]);
            bool hasDir = dir is 'N' or 'S' or 'E' or 'W';
            var body = hasDir ? s[..^1] : s;
            var parts = body.Split(',');
            double v = 0, scale = 1;
            foreach (var p in parts)
            {
                if (!double.TryParse(p.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) return null;
                v += x / scale;
                scale *= 60;
            }
            return dir is 'S' or 'W' ? -v : v;
        }

        // ------------------------------------------------------------------ drone / camera attitude

        static CameraPose? Pose(XmpData? x)
        {
            if (x == null) return null;
            double? N(string key) => ParseNumber(x[key]);
            var p = new CameraPose();
            foreach (var ns in new[] { "drone-dji", "drone-parrot" })
            {
                double? cy = N($"{ns}:GimbalYawDegree") ?? N($"{ns}:CameraYawDegree"), cp = N($"{ns}:GimbalPitchDegree") ?? N($"{ns}:CameraPitchDegree"),
                    cr = N($"{ns}:GimbalRollDegree") ?? N($"{ns}:CameraRollDegree");
                double? vy = N($"{ns}:FlightYawDegree"), vp = N($"{ns}:FlightPitchDegree"), vr = N($"{ns}:FlightRollDegree");
                double? rel = N($"{ns}:RelativeAltitude"), abs = N($"{ns}:AbsoluteAltitude");
                if (cy == null && cp == null && cr == null && vy == null && vp == null && vr == null && rel == null && abs == null) continue;
                p.Source = ns;
                p.CameraYaw = cy; p.CameraPitch = cp; p.CameraRoll = cr;
                p.VehicleYaw = vy; p.VehiclePitch = vp; p.VehicleRoll = vr;
                p.RelativeAltitude = rel; p.AbsoluteAltitude = abs;
                p.SpeedX = N($"{ns}:FlightXSpeed"); p.SpeedY = N($"{ns}:FlightYSpeed"); p.SpeedZ = N($"{ns}:FlightZSpeed");
                p.RtkFlag = N($"{ns}:RtkFlag") is double rf ? (int)rf : null;
                p.RtkStdLatitude = N($"{ns}:RtkStdLat"); p.RtkStdLongitude = N($"{ns}:RtkStdLon"); p.RtkStdHeight = N($"{ns}:RtkStdHgt");
                p.CalibratedFocalLength = N($"{ns}:CalibratedFocalLength");
                p.CalibratedOpticalCenterX = N($"{ns}:CalibratedOpticalCenterX"); p.CalibratedOpticalCenterY = N($"{ns}:CalibratedOpticalCenterY");
                return p;
            }
            // Pix4D camera namespace (Parrot Sequoia, MicaSense, Sentera, ...)
            if (N("Camera:Yaw") != null || N("Camera:Pitch") != null || N("Camera:Roll") != null || N("Camera:AboveGroundAltitude") != null)
            {
                p.Source = "Camera";
                p.CameraYaw = N("Camera:Yaw"); p.CameraPitch = N("Camera:Pitch"); p.CameraRoll = N("Camera:Roll");
                p.RelativeAltitude = N("Camera:AboveGroundAltitude");
                return p;
            }
            return null;
        }

        static PanoramaInfo? Panorama(XmpData? x)
        {
            if (x?["GPano:ProjectionType"] == null && x?["GPano:FullPanoWidthPixels"] == null) return null;
            int? I(string k) => ParseNumber(x[k]) is double v ? (int)v : null;
            return new PanoramaInfo
            {
                ProjectionType = x["GPano:ProjectionType"],
                FullWidth = I("GPano:FullPanoWidthPixels"), FullHeight = I("GPano:FullPanoHeightPixels"),
                CroppedWidth = I("GPano:CroppedAreaImageWidthPixels"), CroppedHeight = I("GPano:CroppedAreaImageHeightPixels"),
                CroppedLeft = I("GPano:CroppedAreaLeftPixels"), CroppedTop = I("GPano:CroppedAreaTopPixels"),
                PoseHeading = ParseNumber(x["GPano:PoseHeadingDegrees"]), PosePitch = ParseNumber(x["GPano:PosePitchDegrees"]),
                PoseRoll = ParseNumber(x["GPano:PoseRollDegrees"]),
            };
        }

        // ------------------------------------------------------------------ values

        /// <summary>A number, a rational "a/b" or a signed decimal ("+12.30"); null otherwise.</summary>
        internal static double? ParseNumber(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            int slash = s.IndexOf('/');
            if (slash > 0)
            {
                if (double.TryParse(s[..slash], NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
                    && double.TryParse(s[(slash + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && b != 0) return a / b;
                return null;
            }
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && !double.IsNaN(v) && !double.IsInfinity(v) ? v : null;
        }

        /// <summary>"YYYY:MM:DD HH:MM:SS" (also with '-' / 'T'), sub seconds digits, "+HH:MM" offset.</summary>
        internal static (DateTime?, TimeSpan?) ExifDate(string? s, string? subSec, string? offset)
        {
            if (string.IsNullOrWhiteSpace(s)) return (null, null);
            s = s.Trim().TrimEnd('\0');
            if (s.Length < 10) return (null, null);
            var t = s.Replace('T', ' ');
            if (!int.TryParse(t.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out int y)
                || !int.TryParse(t.AsSpan(5, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int mo)
                || !int.TryParse(t.AsSpan(8, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int d)) return (null, null);
            int h = 0, mi = 0, se = 0;
            if (t.Length >= 19)
            {
                if (!int.TryParse(t.AsSpan(11, 2), NumberStyles.None, CultureInfo.InvariantCulture, out h)
                    || !int.TryParse(t.AsSpan(14, 2), NumberStyles.None, CultureInfo.InvariantCulture, out mi)
                    || !int.TryParse(t.AsSpan(17, 2), NumberStyles.None, CultureInfo.InvariantCulture, out se)) h = mi = se = 0;
            }
            if (y < 1 || mo is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, mo) || h > 23 || mi > 59 || se > 60) return (null, null);
            var dt = new DateTime(y, mo, d, h, mi, Math.Min(se, 59), DateTimeKind.Unspecified);
            // sub seconds: in the string ("...:SS.123") or the SubSecTime tag
            string? frac = t.Length > 20 && t[19] == '.' ? new string(t.Skip(20).TakeWhile(char.IsDigit).ToArray()) : subSec?.Trim();
            if (!string.IsNullOrEmpty(frac) && frac.All(char.IsDigit))
                dt = dt.AddTicks((long)(double.Parse("0." + frac, CultureInfo.InvariantCulture) * TimeSpan.TicksPerSecond));
            TimeSpan? off = ParseOffset(offset);
            if (off == null && t.Length > 19)
            {
                // an offset written into the date ("2020:01:01 10:00:00+02:00")
                int k = t.IndexOfAny(new[] { '+', '-', 'Z' }, 19);
                if (k > 0) off = ParseOffset(t[k..]);
            }
            return (dt, off);
        }

        static TimeSpan? ParseOffset(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            if (s == "Z") return TimeSpan.Zero;
            if (s.Length < 3 || s[0] is not ('+' or '-')) return null;
            var digits = s[1..].Replace(":", "");
            if (digits.Length is not (2 or 4) || !digits.All(char.IsDigit)) return null;
            int h = int.Parse(digits[..2], CultureInfo.InvariantCulture), m = digits.Length == 4 ? int.Parse(digits[2..], CultureInfo.InvariantCulture) : 0;
            if (h > 14 || m > 59) return null;
            var o = new TimeSpan(h, m, 0);
            return s[0] == '-' ? -o : o;
        }

        static readonly System.Text.RegularExpressions.Regex Iso = new(
            @"^(\d{4})(?:-(\d{2})(?:-(\d{2}))?)?(?:[T ](\d{2}):(\d{2})(?::(\d{2})(?:[.,](\d+))?)?\s*(Z|[+-]\d{2}:?\d{2})?)?$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>ISO 8601 as XMP writes it ("YYYY", "YYYY-MM", "YYYY-MM-DD", "YYYY-MM-DDThh:mm[:ss[.s]][TZD]").</summary>
        internal static (DateTime?, TimeSpan?) IsoDate(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return (null, null);
            var m = Iso.Match(s.Trim());
            if (!m.Success) return ExifDate(s, null, null);   // some writers use EXIF syntax in XMP
            int G(int k, int dflt) => m.Groups[k].Success ? int.Parse(m.Groups[k].Value, CultureInfo.InvariantCulture) : dflt;
            int y = G(1, 1), mo = G(2, 1), d = G(3, 1), h = G(4, 0), mi = G(5, 0), se = G(6, 0);
            if (y < 1 || mo is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, mo) || h > 23 || mi > 59 || se > 60) return (null, null);
            var dt = new DateTime(y, mo, d, h, mi, Math.Min(se, 59), DateTimeKind.Unspecified);
            if (m.Groups[7].Success) dt = dt.AddTicks((long)(double.Parse("0." + m.Groups[7].Value, CultureInfo.InvariantCulture) * TimeSpan.TicksPerSecond));
            return (dt, m.Groups[8].Success ? ParseOffset(m.Groups[8].Value) : null);
        }

        /// <summary>IPTC DateCreated "CCYYMMDD" and TimeCreated "HHMMSS±HHMM".</summary>
        static (DateTime?, TimeSpan?) IptcDate(string? date, string? time)
        {
            if (date == null || date.Length < 8 || !date[..8].All(char.IsDigit)) return (null, null);
            var s = $"{date[..4]}:{date[4..6]}:{date[6..8]}";
            TimeSpan? off = null;
            if (time != null && time.Length >= 6 && time[..6].All(char.IsDigit))
            {
                s += $" {time[..2]}:{time[2..4]}:{time[4..6]}";
                if (time.Length >= 11) off = ParseOffset(time[6..]);
            }
            var (dt, _) = ExifDate(s, null, null);
            return (dt, dt != null ? off : null);
        }

        static (DateTime?, TimeSpan?) LooseDate(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return (null, null);
            var r = ExifDate(s, null, null);
            if (r.Item1 != null) return r;
            r = IsoDate(s);
            if (r.Item1 != null) return r;
            return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dto)
                ? (dto.DateTime, s.Contains('+') || s.Contains("GMT") || s.Contains("UTC") || s.EndsWith("Z") ? dto.Offset : null) : (null, null);
        }

        // ------------------------------------------------------------------ ICC

        /// <summary>The 'desc' tag of an ICC profile (v2 textDescriptionType or v4 multiLocalizedUnicodeType).</summary>
        internal static string? IccDescription(byte[] p)
        {
            try
            {
                if (p.Length < 132) return null;
                int n = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(128)), 1000);
                for (int k = 0; k < n; k++)
                {
                    int o = 132 + k * 12;
                    if (o + 12 > p.Length) return null;
                    if (!p.AsSpan(o, 4).SequenceEqual("desc"u8)) continue;
                    int off = (int)BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(o + 4)), size = (int)BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(o + 8));
                    if (off < 0 || size < 12 || off + (long)size > p.Length) return null;
                    var t = p.AsSpan(off, size);
                    if (t.StartsWith("desc"u8))
                    {
                        int len = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(t.Slice(8)), (uint)(size - 12));
                        return Encoding.Latin1.GetString(t.Slice(12, len)).TrimEnd('\0', ' ');
                    }
                    if (t.StartsWith("mluc"u8) && size >= 28)
                    {
                        int len = (int)BinaryPrimitives.ReadUInt32BigEndian(t.Slice(20)), at = (int)BinaryPrimitives.ReadUInt32BigEndian(t.Slice(24));
                        if (at < 0 || len < 0 || at + (long)len > size) return null;
                        return Encoding.BigEndianUnicode.GetString(t.Slice(at, len)).TrimEnd('\0', ' ');
                    }
                    return null;
                }
            }
            catch (ArgumentOutOfRangeException) { }
            return null;
        }
    }
}
