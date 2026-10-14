using System.Globalization;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;
using static PixCodec.Tests.TestUtil;

namespace PixCodec.Tests;

/// <summary>
/// ImageCodec.ReadMetadata: typed values on our own fixtures (written by tests/oracle/gen_meta.py through ExifTool), and
/// every EXIF field, XMP property and IPTC dataset compared with ExifTool's reading (*.exiftool.json) of those and of
/// ExifTool's sample images (test data repository).
/// </summary>
public class MetadataTests
{
    readonly ITestOutputHelper _out;
    public MetadataTests(ITestOutputHelper output) => _out = output;

    public static IEnumerable<object[]> Own() => Files("metadata", ".jpg", ".png", ".tif", ".webp", ".gif", ".jp2");

    public static IEnumerable<object[]> Samples()
        => ThirdPartyFiles("metadata/exiftool", ".jpg", ".tif", ".png", ".webp", ".gif", ".jp2", ".exr", ".dng", ".btf")
            .Concat(ThirdPartyFiles("metadata/odm", ".jpg", ".JPG"));

    static void Near(double expected, double? actual, double tol = 1e-6, string what = "")
    {
        Assert.True(actual.HasValue, $"{what}: missing");
        Assert.True(Math.Abs(expected - actual!.Value) <= tol * Math.Max(1, Math.Abs(expected)), $"{what}: {actual} != {expected}");
    }

    [Theory, MemberData(nameof(Own))]
    public void TypedValues(string file)
    {
        var m = ImageCodec.ReadMetadata(Fixture(file));
        bool gif = file.EndsWith(".gif"), iptc = !gif && !file.EndsWith(".webp");
        Assert.Equal(("DJI", "FC6310R", "v01.20.5021"), (m.Make, m.Model, m.Software));
        Assert.Equal(("0K8TF7R0012345", "DJI", "24mm F2.8", "Survey Team"), (m.SerialNumber, m.LensMake, m.LensModel, m.OwnerName));
        Assert.Equal(new DateTime(2024, 6, 15, 14, 23, 45), m.DateTaken!.Value.AddTicks(-(m.DateTaken.Value.Ticks % TimeSpan.TicksPerSecond)));
        if (!gif)
        {
            // (XMP has no sub seconds / offset tags of its own: ExifTool's XMP date has neither)
            Assert.Equal(new DateTime(2024, 6, 15, 14, 23, 45, 123), m.DateTaken);
            Assert.Equal(TimeSpan.FromHours(2), m.DateTakenOffset);
            Assert.Equal(new DateTimeOffset(2024, 6, 15, 14, 23, 45, 123, TimeSpan.FromHours(2)), m.DateTakenWithOffset);
        }
        Assert.Equal(new DateTime(2024, 6, 16, 9, 0, 0), m.DateModified);
        Near(0.002, m.ExposureTime, what: "exposure"); Near(5.6, m.FNumber, what: "f"); Assert.Equal(100, m.Iso);
        Near(8.8, m.FocalLength); Near(24, m.FocalLengthIn35mm); Near(-0.7, m.ExposureBias);
        if (!gif) Assert.Equal((16, false, 2, 2, 0), (m.Flash!.Value, m.FlashFired!.Value, m.ExposureProgram!.Value, m.MeteringMode!.Value, m.WhiteBalance!.Value));
        Assert.Equal(1, m.Orientation);
        Assert.Equal(("Pier 3 north face", "Bridge inspection, pier 3", "Jane Pilot", "(c) 2024 Example"), (m.Title, m.Description, m.Artist, m.Copyright));
        Assert.Equal(4, m.Rating);
        Assert.Equal("flight 17 / grid A", m.UserComment);
        Assert.Equal("0123456789abcdef0123456789abcdef", m.ImageUniqueId);
        Assert.Equal(("Graz", "Styria", "Austria"), (m.City, m.State, m.Country));
        var kw = new List<string> { "bridge", "inspection", "Grüße" };
        if (iptc) { kw.Add("concrete"); Assert.Equal("Pier 3", m.Sublocation); }
        Assert.Equal(kw, m.Keywords);

        var g = m.Gps!;
        Near(47.0707123, g.Latitude, 1e-9, "lat"); Near(-15.4395456, g.Longitude, 1e-9, "lon"); Near(412.35, g.Altitude, 1e-9, "alt");
        Near(123.4, g.ImageDirection); Assert.False(g.ImageDirectionIsMagnetic);
        Near(10, g.Speed, 1e-9, "speed");
        Assert.Equal("WGS-84", g.MapDatum);
        Near(0.8, g.HorizontalError); Near(1.2, g.Dop);
        if (!gif)
        {
            Assert.Equal(new DateTime(2024, 6, 15, 12, 23, 45, 500, DateTimeKind.Utc), g.TimeUtc);
            Assert.Equal(0, g.AltitudeReference);
        }

        var p = m.Pose!;
        Assert.Equal("drone-dji", p.Source);
        Near(-45.3, p.CameraYaw); Near(-90, p.CameraPitch); Near(0, p.CameraRoll);
        Near(-44.9, p.VehicleYaw); Near(2.1, p.VehiclePitch); Near(-1.3, p.VehicleRoll);
        Near(80.1, p.RelativeAltitude); Near(412.35, p.AbsoluteAltitude);
        Near(3.2, p.SpeedX); Near(-0.4, p.SpeedY); Near(0, p.SpeedZ);
        Assert.Equal(50, p.RtkFlag); Near(0.012, p.RtkStdLatitude); Near(0.011, p.RtkStdLongitude); Near(0.025, p.RtkStdHeight);
        Near(3666.666504, p.CalibratedFocalLength); Near(2736, p.CalibratedOpticalCenterX); Near(1824, p.CalibratedOpticalCenterY);

        // the same through a stream and a span
        var bytes = File.ReadAllBytes(Fixture(file));
        Assert.Equal(m.ToString(), ImageCodec.ReadMetadata(bytes).ToString());
        Assert.Equal(m.ToString(), ImageCodec.ReadMetadata(new MemoryStream(bytes)).ToString());

        // and as part of the header information
        Assert.Equal(m.ToString(), ImageCodec.GetInfo(Fixture(file)).Metadata!.ToString());
        Assert.Equal(m.ToString(), ImageCodec.GetInfo(bytes).Metadata!.ToString());
        Assert.Equal(m.ToString(), ImageCodec.GetInfo(new MemoryStream(bytes)).Metadata!.ToString());
        Assert.Equal(-45.3, ImageCodec.GetInfo(Fixture(file)).Metadata!.Pose!.CameraYaw);
    }

    [Theory, MemberData(nameof(Own))]
    public void OwnAgainstExifTool(string file) => CompareWithExifTool(Fixture(file));

    [TestDataTheory("fixtures/metadata/exiftool/README.md"), MemberData(nameof(Samples))]
    public void SamplesAgainstExifTool(string file) => CompareWithExifTool(Fixture(file));

    // ------------------------------------------------------------------ ExifTool comparison

    static readonly Dictionary<string, string> ExifToolNames = new()
    {
        ["ExposureBiasValue"] = "ExposureCompensation", ["PhotographicSensitivity"] = "ISO", ["DateTimeDigitized"] = "CreateDate",
        ["DateTime"] = "ModifyDate", ["PixelXDimension"] = "ExifImageWidth", ["PixelYDimension"] = "ExifImageHeight",
        ["FocalLengthIn35mmFilm"] = "FocalLengthIn35mmFormat", ["BodySerialNumber"] = "SerialNumber", ["CameraOwnerName"] = "OwnerName",
        ["LensSpecification"] = "LensInfo", ["InteroperabilityIndex"] = "InteropIndex", ["InteroperabilityVersion"] = "InteropVersion",
        ["JPEGInterchangeFormat"] = "ThumbnailOffset", ["JPEGInterchangeFormatLength"] = "ThumbnailLength", ["ImageLength"] = "ImageHeight",
        ["ExifIFDPointer"] = "ExifOffset", ["GPSInfoIFDPointer"] = "GPSInfo", ["InteroperabilityIFDPointer"] = "InteropOffset",
        ["PlanarConfiguration"] = "PlanarConfiguration", ["PhotometricInterpretation"] = "PhotometricInterpretation",
        ["SamplesPerPixel"] = "SamplesPerPixel", ["StripByteCounts"] = "StripByteCounts", ["ImageTitle"] = "ImageTitle",
        ["RelatedImageLength"] = "RelatedImageHeight", ["SensingMethod2"] = "SensingMethod", ["CFAPattern2"] = "CFAPattern2",
        ["RecommendedExposureIndex"] = "RecommendedExposureIndex", ["ISOSpeed"] = "ISOSpeed", ["InterColorProfile"] = "ICC_Profile",
        ["XMLPacket"] = "XMP", ["IPTCNAA"] = "IPTC-NAA", ["ImageResources"] = "PhotoshopSettings", ["TimeZoneOffset"] = "TimeZoneOffset",
        ["DNGVersion"] = "DNGVersion", ["CameraSerialNumber"] = "CameraSerialNumber", ["LensInfo"] = "DNGLensInfo",
        ["ProcessingSoftware"] = "ProcessingSoftware", ["CameraFirmware"] = "CameraFirmware", ["Photographer"] = "Photographer",
    };

    static readonly Dictionary<ExifIfd, string> Groups = new()
    {
        [ExifIfd.Image] = "IFD0", [ExifIfd.Thumbnail] = "IFD1", [ExifIfd.Exif] = "ExifIFD", [ExifIfd.Gps] = "GPS", [ExifIfd.Interop] = "InteropIFD",
    };

    void CompareWithExifTool(string path)
    {
        var refPath = path + ".exiftool.json";
        if (!File.Exists(refPath)) return;
        var et = JsonDocument.Parse(File.ReadAllText(refPath)).RootElement;
        var m = ImageCodec.ReadMetadata(path);
        var bad = new List<string>();
        int compared = 0;
        bool tiff = m.Format == ImageFormat.Tiff;

        // EXIF
        if (m.Exif != null)
            foreach (var e in m.Exif.Entries)
            {
                // TIFF files: IFD0 is the image's own directory, ExifTool groups it the same
                var name = ExifToolNames.TryGetValue(e.Name, out var n) ? n : e.Name;
                if (!et.TryGetProperty($"{Groups[e.Ifd]}:{name}", out var v)) { _out.WriteLine($"  (not compared: {Groups[e.Ifd]}:{e.Name})"); continue; }
                if (e.Ifd == ExifIfd.Gps && e.Tag is 2 or 4 or 0x14 or 0x16)
                {
                    // ExifTool gives the coordinate as decimal degrees
                    double deg = e.GetDouble(0) + (e.Length > 1 ? e.GetDouble(1) / 60 : 0) + (e.Length > 2 ? e.GetDouble(2) / 3600 : 0);
                    if (v.ValueKind == JsonValueKind.Number) { Check(bad, ref compared, e.Name, Math.Abs(deg - v.GetDouble()) < 1e-6, deg, v); }
                    continue;
                }
                if (e.Ifd == ExifIfd.Gps && e.Tag == 7)
                {
                    var t = v.ToString().Split(':');
                    if (t.Length == 3)
                    {
                        double secs = double.Parse(t[0], CultureInfo.InvariantCulture) * 3600 + double.Parse(t[1], CultureInfo.InvariantCulture) * 60 + double.Parse(t[2].TrimEnd('Z'), CultureInfo.InvariantCulture);
                        double ours = Z(e.GetDouble(0)) * 3600 + Z(e.GetDouble(1)) * 60 + Z(e.GetDouble(2));
                        static double Z(double x) => double.IsNaN(x) ? 0 : x;
                        Check(bad, ref compared, e.Name, Math.Abs(secs - ours) < 1e-3, ours, v);
                    }
                    continue;
                }
                if (e.Ifd == ExifIfd.Thumbnail && e.Tag == 0x0201) continue;   // ExifTool reports the offset in the file
                if (Same(e, v) || SameApex(e.Name, e.ToString(), v) || SameUndef(e.ToString(), v)) compared++;
                else if (!Skippable(e, v)) bad.Add($"EXIF {Groups[e.Ifd]}:{e.Name} ({e.Type}): ours '{Show(e)}' ExifTool '{v}'");
            }

        // XMP: simple top level properties
        if (m.Xmp != null)
            foreach (var p in m.Xmp.Properties)
            {
                if (p.Key.Contains('/') || p.Key.Contains('[')) continue;
                int colon = p.Key.IndexOf(':');
                string prefix = p.Key[..colon], local = p.Key[(colon + 1)..];
                if (!et.TryGetProperty($"XMP-{prefix}:{char.ToUpperInvariant(local[0])}{local[1..]}", out var v)) continue;
                var ours = string.Join("; ", p.Values);
                if (SameText(ours, v) || p.Values.Count > 0 && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() == p.Values.Count
                    && p.Values.Select((s, i) => SameText(s, v[i])).All(b => b)) compared++;
                else if (!SameDate(ours, v) && !SameCoordinate(ours, v) && !SameApex(local, ours, v) && !SameUndef(ours, v)) bad.Add($"XMP {p.Key}: ours '{ours}' ExifTool '{v}'");
                else compared++;
            }

        // IPTC
        if (m.Iptc != null)
            foreach (var g in m.Iptc.Entries.GroupBy(e => e.Name))
            {
                if (!et.TryGetProperty($"IPTC:{g.Key}", out var v)) continue;
                var ours = g.Select(e => e.Text).ToList();
                bool ok = v.ValueKind == JsonValueKind.Array
                    ? v.GetArrayLength() == ours.Count && ours.Select((s, i) => SameText(s, v[i])).All(b => b)
                    : ours.Count == 1 && (SameText(ours[0], v) || Digits(ours[0]) == Digits(v.ToString()));
                if (ok) compared++;
                else if (!(g.Key is "CodedCharacterSet" or "ApplicationRecordVersion" or "EnvelopeRecordVersion" or "RecordVersion")) bad.Add($"IPTC {g.Key}: ours '{string.Join("; ", ours)}' ExifTool '{v}'");
            }

        // GPS position as ExifTool composes it
        if (et.TryGetProperty("Composite:GPSLatitude", out var lat) && lat.ValueKind == JsonValueKind.Number && m.Gps?.Latitude != null)
            Check(bad, ref compared, "GPS latitude", Math.Abs(lat.GetDouble() - m.Gps.Latitude.Value) < 1e-6, m.Gps.Latitude, lat);
        if (et.TryGetProperty("Composite:GPSLongitude", out var lon) && lon.ValueKind == JsonValueKind.Number && m.Gps?.Longitude != null)
            Check(bad, ref compared, "GPS longitude", Math.Abs(lon.GetDouble() - m.Gps.Longitude.Value) < 1e-6, m.Gps.Longitude, lon);
        if (et.TryGetProperty("Composite:GPSAltitude", out var alt) && alt.ValueKind == JsonValueKind.Number && m.Gps?.Altitude != null)
            Check(bad, ref compared, "GPS altitude", Math.Abs(alt.GetDouble() - m.Gps.Altitude.Value) < 1e-6, m.Gps.Altitude, alt);

        // camera / drone attitude
        foreach (var (key, get) in new (string, Func<CameraPose, double?>)[]
        {
            ("XMP-Camera:Yaw", p => p.CameraYaw), ("XMP-Camera:Pitch", p => p.CameraPitch), ("XMP-Camera:Roll", p => p.CameraRoll),
            ("XMP-drone-dji:GimbalYawDegree", p => p.CameraYaw), ("XMP-drone-dji:GimbalPitchDegree", p => p.CameraPitch),
            ("XMP-drone-dji:FlightYawDegree", p => p.VehicleYaw), ("XMP-drone-dji:RelativeAltitude", p => p.RelativeAltitude),
        })
            if (et.TryGetProperty(key, out var pv) && double.TryParse(pv.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var want))
                Check(bad, ref compared, key, m.Pose != null && get(m.Pose) is double got && Math.Abs(got - want) < 1e-9, m.Pose == null ? null : get(m.Pose), pv);

        _out.WriteLine($"{Path.GetFileName(path)}: {compared} values agree, EXIF {m.Exif?.Entries.Count ?? 0} XMP {m.Xmp?.Properties.Count ?? 0} IPTC {m.Iptc?.Entries.Count ?? 0}");
        foreach (var b in bad) _out.WriteLine("  " + b);
        Assert.True(bad.Count == 0, string.Join("\n", bad));
        _ = tiff;
    }

    static void Check(List<string> bad, ref int compared, string what, bool ok, object? ours, JsonElement v)
    {
        if (ok) compared++;
        else bad.Add($"{what}: ours {ours} ExifTool {v}");
    }

    static string Digits(string s) => new(s.Where(char.IsDigit).ToArray());

    static string Show(ExifEntry e) => e.ToString() is var s && s.Length > 80 ? s[..80] + "..." : e.ToString();

    /// <summary>ExifTool converts APEX values to seconds / f-numbers even with -n.</summary>
    static bool SameApex(string name, string ours, JsonElement v)
    {
        if (name is not ("ShutterSpeedValue" or "ApertureValue" or "MaxApertureValue")
            || !double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var theirs)) return false;
        double? a = ours.Contains('/') ? ParseRational(ours) : double.TryParse(ours, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : null;
        if (a == null) return false;
        double conv = name == "ShutterSpeedValue" ? Math.Pow(2, -a.Value) : Math.Pow(2, a.Value / 2);
        return Math.Abs(conv - theirs) <= 1e-9 * Math.Max(1, Math.Abs(conv));
    }

    /// <summary>Rationals with a zero denominator: ExifTool writes "undef".</summary>
    static bool SameUndef(string ours, JsonElement v)
    {
        var o = ours.Split(' ');
        var e = v.ToString().Split(' ');
        return o.Length == e.Length && o.Zip(e).All(t => t.First == "0/0" ? t.Second == "undef" : t.Second != "undef"
            && (t.First.Contains('/') ? ParseRational(t.First) is double r && double.TryParse(t.Second, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && Close(r, d) : t.First == t.Second));
    }

    static bool Skippable(ExifEntry e, JsonElement v)
    {
        var s = v.ToString();
        if (s.StartsWith("(Binary data") || s.StartsWith("base64:")) return true;
        // undefined data that ExifTool interprets (CFA pattern, scene type, ...) and maker notes
        if (e.Type == ExifType.Undefined && e.Tag is not (0x9286 or 0x9000 or 0xA000 or 0x0002 or 0x9101 or 0x001B or 0x001C)) return true;
        return false;
    }

    static bool Same(ExifEntry e, JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.Number)
            return e.Length >= 1 && Close(e.GetDouble(0), v.GetDouble()) && (e.Length == 1 || e.Value is string);
        var s = v.ToString().Trim();
        var ours = e.ToString().Trim();
        if (ours == s) return true;
        if (e.Value is byte[] raw && string.Equals(Convert.ToHexString(raw), s, StringComparison.OrdinalIgnoreCase)) return true;
        // numeric arrays: space separated numbers
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (e.Value is not string && e.Value is not byte[] { } && parts.Length == e.Length)
            return parts.Select((p, i) => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && Close(e.GetDouble(i), d)).All(b => b);
        if (e.Value is byte[] && e.Type == ExifType.Byte && parts.Length == e.Length)
            return parts.Select((p, i) => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && Close(e.GetDouble(i), d)).All(b => b);
        if (double.TryParse(ours, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var b2)) return Close(a, b2);
        return false;
    }

    static bool Close(double a, double b) => a == b || Math.Abs(a - b) <= 1e-6 * Math.Max(1, Math.Abs(b)) || double.IsNaN(a) && double.IsNaN(b);

    static bool SameText(string ours, JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.Number)
            return double.TryParse(ours.TrimStart('+'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && Close(d, v.GetDouble())
                || ours.Contains('/') && ParseRational(ours) is double r && Close(r, v.GetDouble());
        if (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) return string.Equals(ours, v.ToString(), StringComparison.OrdinalIgnoreCase);
        return ours.Trim() == v.ToString().Trim();
    }

    static double? ParseRational(string s)
    {
        var p = s.Split('/');
        return p.Length == 2 && double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
            && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && b != 0 ? a / b : null;
    }

    static bool SameDate(string ours, JsonElement v)
    {
        // XMP ISO dates against ExifTool's "YYYY:MM:DD hh:mm:ss[.s][+hh:mm]"
        var s = v.ToString();
        if (s.Length < 10 || ours.Length < 4) return false;
        string a = new(ours.Where(char.IsDigit).ToArray()), b = new(s.Where(char.IsDigit).ToArray());
        return a.Length > 0 && (a == b || b.StartsWith(a) || a.StartsWith(b));
    }

    static bool SameCoordinate(string ours, JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.Number || ours.Length < 2 || !char.IsLetter(ours[^1])) return false;
        var parts = ours[..^1].Split(',');
        double d = 0, scale = 1;
        foreach (var p in parts) { if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) return false; d += x / scale; scale *= 60; }
        return Math.Abs(Math.Abs(d) - Math.Abs(v.GetDouble())) < 1e-6;
    }

    [Theory]
    [InlineData("metadata/meta.jpg")]
    [InlineData("metadata/meta.png")]
    [InlineData("metadata/meta.tif")]
    [InlineData("metadata/meta.webp")]
    public void SurvivesDecodeAndEncode(string file)
    {
        // decode, then encode to each format that keeps EXIF and XMP: the metadata comes back
        var img = ImageCodec.Decode(File.ReadAllBytes(Fixture(file)));
        Assert.NotNull(img.Metadata?.Xmp);
        Assert.NotNull(img.Metadata?.Exif);
        foreach (var f in new[] { ImageFormat.Jpeg, ImageFormat.Png, ImageFormat.Tiff, ImageFormat.Webp })
        {
            var m = ImageCodec.ReadMetadata(ImageCodec.Encode(img, f));
            Assert.Equal(("DJI", "FC6310R"), (m.Make, m.Model));
            Near(-45.3, m.Pose?.CameraYaw, what: $"{m.Format} gimbal yaw");
            Near(-15.4395456, m.Gps?.Longitude, 1e-9, $"{m.Format} longitude");
            Assert.Equal(new DateTime(2024, 6, 15, 14, 23, 45, 123), m.DateTaken);
            Assert.Contains("inspection", m.Keywords);
        }
    }

    [Fact]
    public void XmpOrientationFollowsTheBakedImage()
    {
        // an image stored rotated: decoding turns it upright, and both EXIF and XMP then say orientation 1
        var img = RawImage.Create(6, 4, ColorModel.Rgb, SampleType.UInt8);
        img.Metadata = new ImageMetadata
        {
            Orientation = 6,
            Xmp = System.Text.Encoding.UTF8.GetBytes("<x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'>"
                + "<rdf:Description xmlns:tiff='http://ns.adobe.com/tiff/1.0/' tiff:Orientation='6' tiff:Make='X'/></rdf:RDF></x:xmpmeta>"),
        };
        foreach (var bytes in new[] { ImageCodec.Encode(img, ImageFormat.Jpeg), ImageCodec.Encode(img, ImageFormat.Png), ImageCodec.Encode(img, ImageFormat.Tiff) })
        {
            Assert.Equal(6, ImageCodec.ReadMetadata(bytes).Orientation);
            Assert.Equal("6", ImageCodec.ReadMetadata(bytes).Xmp!["tiff:Orientation"]);
            var up = ImageCodec.Decode(bytes);
            Assert.Equal((4, 6, 1), (up.Width, up.Height, up.Metadata!.Orientation));
            var again = ImageCodec.ReadMetadata(ImageCodec.Encode(up, ImageFormat.Png));
            Assert.Equal(1, again.Orientation);
            Assert.Equal("1", again.Xmp!["tiff:Orientation"]);
            Assert.Equal("X", again.Xmp!["tiff:Make"]);
        }
    }

    // ------------------------------------------------------------------ robustness

    [Theory, MemberData(nameof(Own))]
    public void DamagedFilesDoNotThrow(string file)
    {
        var data = File.ReadAllBytes(Fixture(file));
        var rnd = new Random(file.Length);
        for (int k = 0; k < 200; k++)
        {
            var d = k < 60 ? data.AsSpan(0, rnd.Next(1, data.Length)).ToArray() : (byte[])data.Clone();
            if (k >= 60)
                for (int j = 0; j < 1 + k % 8; j++) d[rnd.Next(d.Length)] = (byte)rnd.Next(256);
            try { _ = ImageCodec.ReadMetadata(d).ToString(); }
            catch (ImageFormatException) { }
            catch (ImageNotSupportedException) { }
        }
    }

    public static IEnumerable<object[]> AllFiles() => Own().Concat(Samples());

    /// <summary>GetInfo on a seekable stream that is neither a file nor a MemoryStream walks the file structure: same metadata.</summary>
    [Theory, MemberData(nameof(AllFiles))]
    public void SeekableStreamsGiveTheSameMetadata(string file)
    {
        var path = Fixture(file);
        var want = ImageCodec.ReadMetadata(path);
        var bytes = File.ReadAllBytes(path);
        var s = new InfoTests.CountingStream(new MemoryStream(bytes), true);
        ImageInfo info;
        try { info = ImageCodec.GetInfo(s); }
        catch (Exception e) when (e is ImageFormatException or ImageNotSupportedException) { return; }   // (headers we do not decode)
        var got = info.Metadata!;
        Assert.Equal(want.ToString(), got.ToString());
        Assert.Equal(Describe(want.Exif), Describe(got.Exif));
        Assert.Equal(want.Xmp?.Properties.Count, got.Xmp?.Properties.Count);
        Assert.Equal(want.Iptc?.Entries.Count, got.Iptc?.Entries.Count);
        Assert.Equal(want.Pose?.CameraYaw, got.Pose?.CameraYaw);
        Assert.Equal(want.Gps?.ToString(), got.Gps?.ToString());
    }

    static string Describe(ExifData? e)
        => e == null ? "" : string.Join("\n", e.Entries
            .Where(x => !(x.Ifd == ExifIfd.Image && x.Tag is 273 or 279 or 324 or 325 or 288 or 289 or 330 or 0x8769 or 0x8825 or 0x0201) && !(x.Ifd == ExifIfd.Exif && x.Tag == 0xA005))
            .Where(x => x.Ifd != ExifIfd.Thumbnail)
            .Select(x => $"{x.Ifd}:{x.Name}={x}").OrderBy(x => x, StringComparer.Ordinal));

    [Fact]
    public void NonSeekableStreamGivesTheMetadataBeforeThePixels()
    {
        // a JPEG's metadata precedes the scan: the header pass sees it
        var bytes = File.ReadAllBytes(Fixture("metadata/meta.jpg"));
        var info = ImageCodec.GetInfo(new ForwardOnly(bytes));
        Assert.Equal(("DJI", 32, 24), (info.Metadata!.Make, info.Width, info.Height));
        Assert.Equal(-90, info.Metadata.Pose!.CameraPitch);
    }

    sealed class ForwardOnly(byte[] data) : Stream
    {
        int _pos;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { int n = Math.Min(count, data.Length - _pos); Array.Copy(data, _pos, buffer, offset, n); _pos += n; return n; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void FormatsWithoutMetadata()
    {
        var img = RawImage.Create(4, 3, ColorModel.Rgb, SampleType.UInt8);
        foreach (var bytes in new[] { Bmp.Encode(img), Tga.Encode(img), Dds.Encode(img), Png.Encode(img) })
        {
            var m = ImageCodec.ReadMetadata(bytes);
            Assert.Null(m.Exif); Assert.Null(m.Gps); Assert.Null(m.Pose); Assert.Null(m.DateTaken);
        }
        Assert.Throws<ImageFormatException>(() => ImageCodec.ReadMetadata(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
    }

    [Fact]
    public void Parsers()
    {
        Assert.Equal(47.07071, MetadataReader.XmpCoordinate("47,4.2426N")!.Value, 5);
        Assert.Equal(-15.5, MetadataReader.XmpCoordinate("15,30,0W")!.Value, 9);
        Assert.Equal((new DateTime(2020, 1, 2, 3, 4, 5, 600), (TimeSpan?)TimeSpan.FromHours(-5)), MetadataReader.IsoDate("2020-01-02T03:04:05.6-05:00"));
        Assert.Equal((new DateTime(2020, 1, 2, 3, 4, 0), (TimeSpan?)TimeSpan.Zero), MetadataReader.IsoDate("2020-01-02T03:04Z"));
        Assert.Equal((new DateTime(2020, 1, 1), (TimeSpan?)null), MetadataReader.IsoDate("2020"));
        Assert.Equal((new DateTime(2019, 12, 31, 23, 59, 59), (TimeSpan?)null), MetadataReader.ExifDate("2019:12:31 23:59:59", null, null));
        Assert.Equal(((DateTime?)null, (TimeSpan?)null), MetadataReader.ExifDate("    :  :     :  :  ", null, null));
        Assert.Equal(((DateTime?)null, (TimeSpan?)null), MetadataReader.ExifDate("2019:02:30 10:00:00", null, null));
    }
}
