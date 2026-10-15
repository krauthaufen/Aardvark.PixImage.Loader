using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;
using static Aardvark.ImageCodecs.Tests.TestUtil;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>
/// Metadata writing: <see cref="ImageCodec.WriteMetadata(ReadOnlySpan{byte}, MetadataEdit)"/> (lossless rewrites of JPEG,
/// PNG, TIFF / BigTIFF, WebP and JPEG 2000) and <see cref="MetadataEdit.ApplyTo(ImageMetadata)"/> before encoding.
/// Results are read back with our reader and with ExifTool: live when ExifTool is installed, otherwise from the golden
/// outputs and ExifTool readings in tests/fixtures/metadata-write (made by tests/oracle/gen_meta_write.py, which runs the
/// <see cref="Golden"/> cases with PIXCODEC_WRITE_GOLDEN=1).
/// </summary>
public class MetadataWriteTests
{
    readonly ITestOutputHelper _out;
    public MetadataWriteTests(ITestOutputHelper output) => _out = output;

    // ------------------------------------------------------------------ edits

    static readonly DateTime Taken = new(2025, 3, 4, 5, 6, 7, 890);

    static MetadataEdit FullEdit() => new()
    {
        Make = "PixCodec Cam", Model = "Model X 1", SerialNumber = "SN-12345", LensMake = "Optic", LensModel = "12-24mm f/4",
        LensSerialNumber = "L-987", LensSpecification = new[] { 12.0, 24, 4, 4 }, Software = "PixCodec writer", OwnerName = "Survey Crew",
        DateTaken = Taken, DateTakenOffset = new TimeSpan(-5, -30, 0),
        DateDigitized = new DateTime(2025, 3, 4, 5, 6, 8), DateModified = new DateTime(2025, 3, 5, 12, 0, 0), DateModifiedOffset = TimeSpan.FromHours(1),
        ExposureTime = 1 / 250.0, FNumber = 4, Iso = 400, FocalLength = 18.5, FocalLengthIn35mm = 28, ExposureBias = -0.7, Flash = 1,
        ExposureProgram = 3, MeteringMode = 5, WhiteBalance = 1, Orientation = 1,
        Title = "Grüße aus Wien", Description = "Facade survey – north side", Artist = "Jane Doe", Copyright = "© 2025 PixCodec",
        Keywords = new[] { "alpha", "beta", "Grüße" }, Rating = 3, UserComment = "comment ✓", ImageUniqueId = "00112233445566778899aabbccddeeff",
        City = "Wien", State = "Wien", Country = "Austria", Sublocation = "Stephansplatz",
        Gps = new GpsEdit
        {
            Latitude = 48.2084931, Longitude = 16.3725042, Altitude = 171.25, TimeUtc = new DateTime(2025, 3, 4, 10, 36, 7, 500, DateTimeKind.Utc),
            Speed = 12.5, Track = 270.5, ImageDirection = 45.25, ImageDirectionIsMagnetic = true, HorizontalError = 0.015, Dop = 0.9,
            MapDatum = "WGS-84", Satellites = "12", MeasureMode = "3", Differential = true, ProcessingMethod = "GPS",
        },
    };

    /// <summary>What a drone / photogrammetry pipeline does: position (below sea level here) and capture time only.</summary>
    static MetadataEdit GpsEditOnly() => new()
    {
        DateTaken = new DateTime(2024, 12, 31, 23, 59, 59), DateTakenOffset = TimeSpan.FromHours(11),
        Gps = new GpsEdit { Latitude = -33.8567844, Longitude = 151.2152967, Altitude = -12.5, TimeUtc = new DateTime(2024, 12, 31, 12, 59, 59, DateTimeKind.Utc), ImageDirection = 359.9 },
    };

    static MetadataEdit StripEdit() => new MetadataEdit { RemoveAll = true, Copyright = "only this" };

    static readonly string BigText = string.Concat(Enumerable.Range(0, 3000).Select(i => $"line {i:D5} of a long note; "));

    static MetadataEdit BigXmpEdit() => new MetadataEdit { Title = "big XMP" }
        .SetXmpNamespace("pcx", "http://pixcodec.example/ns/1.0/")
        .SetXmp("pcx:Notes", BigText)
        .SetXmp("pcx:Small", "kept in the standard part");

    static MetadataEdit EditByName(string name) => name switch
    {
        "full" => FullEdit(), "gps" => GpsEditOnly(), "nogps" => new MetadataEdit { RemoveGps = true }, "strip" => StripEdit(), "bigxmp" => BigXmpEdit(),
        _ => throw new ArgumentException(name),
    };

    // ------------------------------------------------------------------ inputs

    const string Dir = "metadata-write";

    static RawImage Gradient(int w = 40, int h = 30)
    {
        var img = RawImage.Create(w, h, ColorModel.Rgb, SampleType.UInt8);
        var b = img.GetBytes();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 3;
                b[i] = (byte)(x * 6); b[i + 1] = (byte)(y * 8); b[i + 2] = (byte)((x + y) * 3);
            }
        return img;
    }

    /// <summary>Plain inputs made by our encoders (stored, so that encoder changes do not change the golden outputs).</summary>
    static readonly Dictionary<string, Func<byte[]>> PlainInputs = new()
    {
        ["plain.jpg"] = () => ImageCodec.Encode(Gradient(), ImageFormat.Jpeg),
        ["plain.png"] = () => ImageCodec.Encode(Gradient(), ImageFormat.Png),
        ["plain.webp"] = () => Enc(s => Webp.Encode(Gradient(), s, new WebpEncodeOptions { Lossless = true })),
        ["plain.jp2"] = () => ImageCodec.Encode(Gradient(), ImageFormat.Jp2),
        ["plain.tif"] = () => ImageCodec.Encode(Gradient(), ImageFormat.Tiff),
        ["tiled.tif"] = () => Enc(s => Tiff.Encode(Gradient(40, 40), s, new TiffEncodeOptions { TileWidth = 16, TileHeight = 16 })),
        ["big.btf"] = () => Enc(s => Tiff.Encode(Gradient(), s, new TiffEncodeOptions { BigTiff = true, RowsPerStrip = 8 })),
        ["be.tif"] = () => Enc(s => Tiff.Encode(Gradient(), s, new TiffEncodeOptions { BigEndian = true })),
        ["pages.tif"] = () => Enc(s => Tiff.Encode(new[] { Gradient(), Gradient(20, 10) }, s)),
    };

    static byte[] Enc(Action<Stream> f) { var m = new MemoryStream(); f(m); return m.ToArray(); }

    static bool WriteGolden => Environment.GetEnvironmentVariable("PIXCODEC_WRITE_GOLDEN") == "1";

    static string InputPath(string input) => input.StartsWith("meta.") ? Fixture("metadata/" + input) : Path.Combine(FixtureDir, Dir, "in", input);

    static byte[] Input(string input)
    {
        var p = InputPath(input);
        if (!File.Exists(p) && PlainInputs.TryGetValue(input, out var make))
        {
            var bytes = make();
            if (!WriteGolden) return bytes;
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, bytes);
        }
        return File.ReadAllBytes(p);
    }

    public static IEnumerable<object[]> GoldenCases()
    {
        foreach (var f in new[] { "meta.jpg", "meta.png", "meta.tif", "meta.webp", "meta.jp2" })
            foreach (var e in new[] { "full", "gps", "nogps", "strip" }) yield return new object[] { f, e };
        foreach (var f in new[] { "plain.jpg", "plain.png", "plain.webp", "plain.jp2", "plain.tif", "tiled.tif", "big.btf", "be.tif", "pages.tif" })
            yield return new object[] { f, "full" };
        yield return new object[] { "plain.jpg", "bigxmp" };
        yield return new object[] { "meta.jpg", "bigxmp" };
        yield return new object[] { "big.btf", "nogps" };
    }

    static string OutName(string input, string edit) => $"{Path.GetFileNameWithoutExtension(input)}.{edit}{Path.GetExtension(input)}";

    // ------------------------------------------------------------------ golden / ExifTool cases

    [Theory, MemberData(nameof(GoldenCases))]
    public void Golden(string input, string edit)
    {
        var src = Input(input);
        var dst = ImageCodec.WriteMetadata(src, EditByName(edit));
        var goldenPath = Path.Combine(FixtureDir, Dir, OutName(input, edit));
        if (WriteGolden) File.WriteAllBytes(goldenPath, dst);

        CheckOurs(src, dst, edit);
        AssertImageDataUnchanged(src, dst);

        // ExifTool's reading of the output: live, else the stored one of the golden output
        var et = ExifTool;
        if (et != null)
        {
            var tmp = TempFile(dst, Path.GetExtension(input));
            var tmpIn = File.Exists(InputPath(input)) ? null : TempFile(src, Path.GetExtension(input));
            try
            {
                CheckExifTool(RunJson(et, tmp), edit, input);
                CheckValidate(Validate(et, tmpIn ?? InputPath(input)), Validate(et, tmp), input);
            }
            finally { File.Delete(tmp); if (tmpIn != null) File.Delete(tmpIn); }
        }
        if (!File.Exists(goldenPath)) { _out.WriteLine("no golden output (run tests/oracle/gen_meta_write.py)"); return; }
        var golden = File.ReadAllBytes(goldenPath);
        Assert.True(golden.AsSpan().SequenceEqual(dst), $"{OutName(input, edit)} differs from the golden output: regenerate with tests/oracle/gen_meta_write.py if the change is intended");
        if (File.Exists(goldenPath + ".exiftool.json"))
            CheckExifTool(JsonDocument.Parse(File.ReadAllText(goldenPath + ".exiftool.json")).RootElement, edit, input);
        var inValidate = Path.Combine(FixtureDir, Dir, "in", input + ".validate.txt");
        if (File.Exists(goldenPath + ".validate.txt") && File.Exists(inValidate))
            CheckValidate(File.ReadAllLines(inValidate), File.ReadAllLines(goldenPath + ".validate.txt"), input);
    }

    static void Near(double expected, double? actual, double tol, string what)
    {
        Assert.True(actual.HasValue, $"{what}: missing");
        Assert.True(Math.Abs(expected - actual!.Value) <= tol, $"{what}: {actual} != {expected}");
    }

    /// <summary>Our reader sees the edit (and what the input had and the edit does not touch).</summary>
    static void CheckOurs(byte[] src, byte[] dst, string edit)
    {
        var before = ImageCodec.ReadMetadata(src);
        var m = ImageCodec.ReadMetadata(dst);
        bool tiff = m.Format == ImageFormat.Tiff;
        switch (edit)
        {
            case "full":
                Assert.Equal(("PixCodec Cam", "Model X 1", "SN-12345", "Optic", "12-24mm f/4", "L-987"), (m.Make, m.Model, m.SerialNumber, m.LensMake, m.LensModel, m.LensSerialNumber));
                Assert.Equal(new[] { 12.0, 24, 4, 4 }, m.LensSpecification);
                Assert.Equal(("PixCodec writer", "Survey Crew"), (m.Software, m.OwnerName));
                Assert.Equal(Taken, m.DateTaken);
                Assert.Equal(new TimeSpan(-5, -30, 0), m.DateTakenOffset);
                Assert.Equal(new DateTime(2025, 3, 4, 5, 6, 8), m.DateDigitized);
                Assert.Equal((new DateTime(2025, 3, 5, 12, 0, 0), TimeSpan.FromHours(1)), (m.DateModified!.Value, m.DateModifiedOffset!.Value));
                Assert.Equal((0.004, 4.0, 400, 18.5, 28.0, -0.7), (m.ExposureTime!.Value, m.FNumber!.Value, m.Iso!.Value, m.FocalLength!.Value, m.FocalLengthIn35mm!.Value, m.ExposureBias!.Value));
                Assert.Equal((1, 3, 5, 1, 1), (m.Flash!.Value, m.ExposureProgram!.Value, m.MeteringMode!.Value, m.WhiteBalance!.Value, m.Orientation));
                Assert.Equal(("Grüße aus Wien", "Facade survey – north side", "Jane Doe", "© 2025 PixCodec"), (m.Title, m.Description, m.Artist, m.Copyright));
                Assert.Equal(new[] { "alpha", "beta", "Grüße" }, m.Keywords);
                Assert.Equal((3, "comment ✓", "00112233445566778899aabbccddeeff"), (m.Rating!.Value, m.UserComment, m.ImageUniqueId));
                Assert.Equal(("Wien", "Wien", "Austria", "Stephansplatz"), (m.City, m.State, m.Country, m.Sublocation));
                var g = m.Gps!;
                Near(48.2084931, g.Latitude, 1e-9, "lat"); Near(16.3725042, g.Longitude, 1e-9, "lon"); Near(171.25, g.Altitude, 1e-9, "alt");
                Assert.Equal(new DateTime(2025, 3, 4, 10, 36, 7, 500, DateTimeKind.Utc), g.TimeUtc);
                Near(12.5, g.Speed, 1e-9, "speed"); Near(270.5, g.Track, 1e-9, "track"); Near(45.25, g.ImageDirection, 1e-9, "dir");
                Assert.True(g.ImageDirectionIsMagnetic);
                Near(0.015, g.HorizontalError, 1e-12, "hpe"); Near(0.9, g.Dop, 1e-12, "dop");
                Assert.Equal(("WGS-84", "12", "3", true, "GPS"), (g.MapDatum, g.Satellites, g.MeasureMode, g.Differential!.Value, g.ProcessingMethod));
                if (before.Pose != null) Assert.Equal(before.Pose.CameraYaw, m.Pose?.CameraYaw);
                break;
            case "gps":
                Near(-33.8567844, m.Gps?.Latitude, 1e-9, "lat"); Near(151.2152967, m.Gps?.Longitude, 1e-9, "lon"); Near(-12.5, m.Gps?.Altitude, 1e-9, "alt");
                Near(359.9, m.Gps?.ImageDirection, 1e-9, "dir");
                Assert.Equal(new DateTime(2024, 12, 31, 23, 59, 59), m.DateTaken);
                Assert.Equal(TimeSpan.FromHours(11), m.DateTakenOffset);
                // everything else stays
                Assert.Equal((before.Make, before.Model, before.Title, before.Artist), (m.Make, m.Model, m.Title, m.Artist));
                Assert.Equal(before.Keywords, m.Keywords);
                Assert.Equal(before.Pose?.CameraYaw, m.Pose?.CameraYaw);
                Assert.Equal(before.Gps?.Speed, m.Gps?.Speed);
                break;
            case "nogps":
                Assert.Null(m.Exif?.Entries.FirstOrDefault(e => e.Ifd == ExifIfd.Gps));
                Assert.Null(m.Gps?.Latitude);
                Assert.Null(m.Xmp?.Properties.FirstOrDefault(p => p.Key.StartsWith("exif:GPS")));
                Assert.Equal((before.Make, before.Title, before.DateTaken), (m.Make, m.Title, m.DateTaken));
                Assert.Equal(before.Pose?.CameraYaw, m.Pose?.CameraYaw);
                break;
            case "strip":
                Assert.Null(m.Make); Assert.Null(m.Title); Assert.Null(m.Gps); Assert.Null(m.Pose); Assert.Null(m.Iptc);
                Assert.Empty(m.Keywords);
                Assert.Equal("only this", m.Copyright);
                Assert.Equal(before.Orientation, m.Orientation);
                Assert.Equal(before.IccProfile?.Length, m.IccProfile?.Length);
                if (tiff) Assert.Null(m.Exif!.Entries.FirstOrDefault(e => e.Ifd is ExifIfd.Exif or ExifIfd.Gps));
                break;
            case "bigxmp":
                Assert.Equal(BigText.Trim(), m.Xmp!["pcx:Notes"]);
                Assert.Equal("kept in the standard part", m.Xmp!["pcx:Small"]);
                Assert.Equal("big XMP", m.Title);
                Assert.Equal(before.Make, m.Make);
                break;
        }
    }

    // ------------------------------------------------------------------ ExifTool

    internal static string? ExifTool { get; } = FindExifTool();

    /// <summary>ExifTool: $EXIFTOOL or ~/.local/share/exiftool/exiftool; PIXCODEC_NO_EXIFTOOL=1 tests the stored readings only.</summary>
    static string? FindExifTool()
    {
        if (Environment.GetEnvironmentVariable("PIXCODEC_NO_EXIFTOOL") == "1") return null;
        foreach (var p in new[] { Environment.GetEnvironmentVariable("EXIFTOOL"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/share/exiftool/exiftool") })
            if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
        return null;
    }

    static string Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return o;
    }

    internal static byte[] RunBytes(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var m = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(m);
        p.WaitForExit();
        return m.ToArray();
    }

    internal static JsonElement RunJson(string et, string path) => JsonDocument.Parse(Run(et, "-j", "-n", "-G1", "-a", "-struct", "-charset", "utf8", path)).RootElement[0];

    internal static string[] Validate(string et, string path)
        => Run(et, "-validate", "-warning", "-a", "-s3", path).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    static string TempFile(byte[] data, string ext)
    {
        var dir = Path.Combine(Path.GetTempPath(), "pixcodec-metawrite");
        Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, Guid.NewGuid().ToString("N") + ext);
        File.WriteAllBytes(p, data);
        return p;
    }

    /// <summary>
    /// ExifTool must not warn about anything the input did not already have (missing tags its validation wants that the input
    /// lacked, or ExifTool's own fixtures' quirks); the count line differs and is ignored.
    /// </summary>
    void CheckValidate(string[] before, string[] after, string what)
    {
        var old = new HashSet<string>(before.Where(l => !char.IsDigit(l.FirstOrDefault()) && l != "OK"));
        var added = after.Where(l => !char.IsDigit(l.FirstOrDefault()) && l != "OK" && !old.Contains(l)
            && !(what.EndsWith(".btf") && l.Contains("GPSInfo:InteropIndex"))).ToList();   // (ExifTool's reading of BigTIFF GPS, see CheckExifTool)
        foreach (var a in after) _out.WriteLine($"  validate: {a}");
        Assert.True(added.Count == 0, $"{what}: new ExifTool warnings: {string.Join(" | ", added)}");
    }

    static string? Str(JsonElement j, string key) => j.TryGetProperty(key, out var v) ? v.ToString() : null;
    static double? Num(JsonElement j, string key) => j.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    void CheckExifTool(JsonElement j, string edit, string input)
    {
        if (input.EndsWith(".btf"))
        {
            // ExifTool reads BigTIFF EXIF directories as "ExifOffset" and does not know the GPS directory there (it reads it
            // as an interoperability directory; tifffile reads it fine): rename the group, leave out the GPS checks
            var o = new Dictionary<string, JsonElement>();
            foreach (var p in j.EnumerateObject())
                if (!p.Name.StartsWith("GPSInfo:")) o[p.Name.StartsWith("ExifOffset:") ? "ExifIFD:" + p.Name[11..] : p.Name] = p.Value.Clone();
            if (edit == "full")
            {
                Assert.Equal("SN-12345", o["ExifIFD:SerialNumber"].ToString());
                Assert.Equal("2025:03:04 05:06:07", o["ExifIFD:DateTimeOriginal"].ToString());
                Assert.Equal("PixCodec Cam", o["IFD0:Make"].ToString());
                Assert.Equal("Grüße aus Wien", o["XMP-dc:Title"].ToString());
            }
            return;
        }
        _out.WriteLine($"ExifTool on {OutName(input, edit)}: {j.EnumerateObject().Count()} tags");
        if (j.TryGetProperty("ExifTool:Warning", out var w)) Assert.Fail($"ExifTool warning: {w}");
        if (j.TryGetProperty("ExifTool:Error", out var er)) Assert.Fail($"ExifTool error: {er}");
        switch (edit)
        {
            case "full":
                Assert.Equal("PixCodec Cam", Str(j, "IFD0:Make"));
                Assert.Equal("Model X 1", Str(j, "IFD0:Model"));
                Assert.Equal("SN-12345", Str(j, "ExifIFD:SerialNumber"));
                Assert.Equal("12-24mm f/4", Str(j, "ExifIFD:LensModel"));
                Assert.Equal("12 24 4 4", Str(j, "ExifIFD:LensInfo"));
                Assert.Equal("2025:03:04 05:06:07", Str(j, "ExifIFD:DateTimeOriginal"));
                Assert.Equal("89", Str(j, "ExifIFD:SubSecTimeOriginal"));
                Assert.Equal("-05:30", Str(j, "ExifIFD:OffsetTimeOriginal"));
                Assert.Equal(0.004, Num(j, "ExifIFD:ExposureTime"));
                Assert.Equal(4, Num(j, "ExifIFD:FNumber"));
                Assert.Equal(400, Num(j, "ExifIFD:ISO"));
                Assert.Equal(18.5, Num(j, "ExifIFD:FocalLength"));
                Assert.Equal(-0.7, Num(j, "ExifIFD:ExposureCompensation"));
                Assert.Equal("Grüße aus Wien", Str(j, "XMP-dc:Title"));
                Assert.Equal("Facade survey – north side", Str(j, "IFD0:ImageDescription"));
                Assert.Equal("Jane Doe", Str(j, "IFD0:Artist"));
                Assert.Equal("© 2025 PixCodec", Str(j, "IFD0:Copyright"));
                Assert.Equal("comment ✓", Str(j, "ExifIFD:UserComment"));
                Assert.Equal("[\"alpha\",\"beta\",\"Grüße\"]", j.GetProperty("XMP-dc:Subject").GetRawText().Replace(" ", "").Replace("\n", ""));
                Assert.Equal(3, Num(j, "XMP-xmp:Rating"));
                Assert.Equal("Stephansplatz", Str(j, "XMP-iptcCore:Location"));
                Near(48.2084931, Num(j, "GPS:GPSLatitude"), 1e-9, "ExifTool latitude");
                Near(16.3725042, Num(j, "GPS:GPSLongitude"), 1e-9, "ExifTool longitude");
                Near(171.25, Num(j, "GPS:GPSAltitude"), 1e-9, "ExifTool altitude");
                Assert.Equal("2025:03:04 10:36:07.5Z", Str(j, "Composite:GPSDateTime"));
                Near(45.25, Num(j, "GPS:GPSImgDirection"), 1e-9, "ExifTool direction");
                Assert.Equal("M", Str(j, "GPS:GPSImgDirectionRef"));
                Near(45, Num(j, "GPS:GPSSpeed"), 1e-9, "ExifTool speed (km/h)");
                Near(0.015, Num(j, "GPS:GPSHPositioningError"), 1e-12, "ExifTool HPE");
                Assert.Equal("GPS", Str(j, "GPS:GPSProcessingMethod"));
                if (input.StartsWith("meta.") && !input.EndsWith(".webp"))
                {
                    // IPTC is updated where it exists
                    Assert.Equal("Grüße aus Wien", Str(j, "IPTC:ObjectName"));
                    Assert.Equal("[\"alpha\",\"beta\",\"Grüße\"]", j.GetProperty("IPTC:Keywords").GetRawText().Replace(" ", "").Replace("\n", ""));
                    Assert.Equal("Wien", Str(j, "IPTC:City"));
                }
                if (input.StartsWith("meta.")) Assert.Equal(-45.3, Num(j, "XMP-drone-dji:GimbalYawDegree"));
                break;
            case "gps":
                Near(-33.8567844, Num(j, "Composite:GPSLatitude"), 1e-9, "ExifTool latitude");
                Near(151.2152967, Num(j, "Composite:GPSLongitude"), 1e-9, "ExifTool longitude");
                Near(-12.5, Num(j, "Composite:GPSAltitude"), 1e-9, "ExifTool altitude");
                Assert.Equal("2024:12:31 23:59:59", Str(j, "ExifIFD:DateTimeOriginal"));
                Assert.Equal("+11:00", Str(j, "ExifIFD:OffsetTimeOriginal"));
                Assert.Null(Str(j, "ExifIFD:SubSecTimeOriginal"));
                Assert.Equal("DJI", Str(j, "IFD0:Make"));
                break;
            case "nogps":
                Assert.DoesNotContain(j.EnumerateObject(), p => p.Name.StartsWith("GPS:") || p.Name.StartsWith("Composite:GPS") || p.Name.StartsWith("XMP-exif:GPS"));
                if (input.StartsWith("meta.")) Assert.Equal("DJI", Str(j, "IFD0:Make"));
                break;
            case "strip":
                Assert.DoesNotContain(j.EnumerateObject(), p => p.Name.StartsWith("GPS:") || p.Name.StartsWith("ExifIFD:") || p.Name.StartsWith("XMP-") && p.Name is not ("XMP-dc:Rights" or "XMP-x:XMPToolkit") || p.Name.StartsWith("IPTC:") || p.Name.StartsWith("Composite:GPS"));
                Assert.Equal("only this", Str(j, "IFD0:Copyright"));
                if (!input.EndsWith(".tif")) Assert.Equal(1, Num(j, "IFD0:Orientation"));
                break;
            case "bigxmp":
                Assert.Equal("big XMP", Str(j, "XMP-dc:Title"));
                // (ExifTool shows long values as binary data unless asked for them with -b)
                var notes = Str(j, "XMP-pcx:Notes");
                Assert.True(notes?.Trim() == BigText.Trim() || notes == $"(Binary data {BigText.Length} bytes, use -b option to extract)", notes);
                if (input.EndsWith(".jpg")) Assert.NotNull(Str(j, "XMP-xmpNote:HasExtendedXMP"));
                break;
        }
    }

    // ------------------------------------------------------------------ the image data is not touched

    /// <summary>The compressed image data of a file (concatenated): what a lossless rewrite must keep byte for byte.</summary>
    internal static byte[] ImageData(byte[] f)
    {
        var o = new MemoryStream();
        switch (ImageCodec.DetectFormat(f))
        {
            case ImageFormat.Jpeg:
            {
                int p = 2;
                while (p + 4 <= f.Length && !(f[p] == 0xFF && f[p + 1] == 0xDA))
                {
                    if (f[p] != 0xFF || f[p + 1] == 0xFF) { p++; continue; }
                    p += 2 + BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(p + 2));
                }
                o.Write(f.AsSpan(p));
                // and the tables
                break;
            }
            case ImageFormat.Png:
                for (int p = 8; p + 12 <= f.Length;)
                {
                    int len = (int)BinaryPrimitives.ReadUInt32BigEndian(f.AsSpan(p));
                    var t = Encoding.ASCII.GetString(f, p + 4, 4);
                    if (t is "IHDR" or "PLTE" or "tRNS" or "IDAT" or "fdAT" or "fcTL" or "acTL" or "IEND") o.Write(f.AsSpan(p, len + 12));
                    p += len + 12;
                }
                break;
            case ImageFormat.Webp:
                for (int p = 12; p + 8 <= f.Length;)
                {
                    int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan(p + 4));
                    var t = Encoding.ASCII.GetString(f, p, 4);
                    if (t is "VP8 " or "VP8L" or "ALPH" or "ANMF" or "ANIM" or "ICCP") o.Write(f.AsSpan(p, 8 + len));
                    p += 8 + len + (len & 1);
                }
                break;
            case ImageFormat.Jp2:
                for (int p = 0; p + 8 <= f.Length;)
                {
                    long len = BinaryPrimitives.ReadUInt32BigEndian(f.AsSpan(p));
                    var t = Encoding.ASCII.GetString(f, p + 4, 4);
                    if (len == 0) len = f.Length - p;
                    if (t is "jp2h" or "jp2c" or "ftyp") o.Write(f.AsSpan(p, (int)len));
                    p += (int)len;
                }
                break;
            case ImageFormat.Tiff:
            {
                // every page's strips / tiles
                foreach (var (off, cnt) in TiffChunks(f)) o.Write(f.AsSpan((int)off, (int)cnt));
                break;
            }
        }
        return o.ToArray();
    }

    /// <summary>(offset, byte count) of the strips / tiles of every page of a TIFF / BigTIFF file.</summary>
    static List<(long, long)> TiffChunks(byte[] f)
    {
        bool be = f[0] == 'M', big = (be ? BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(2)) : BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(2))) == 43;
        ulong U(long p, int n) => n switch
        {
            2 => be ? BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan((int)p)) : BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan((int)p)),
            4 => be ? BinaryPrimitives.ReadUInt32BigEndian(f.AsSpan((int)p)) : BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan((int)p)),
            _ => be ? BinaryPrimitives.ReadUInt64BigEndian(f.AsSpan((int)p)) : BinaryPrimitives.ReadUInt64LittleEndian(f.AsSpan((int)p)),
        };
        int os = big ? 8 : 4, cs = big ? 8 : 2, es = big ? 20 : 12;
        var r = new List<(long, long)>();
        long ifd = (long)U(big ? 8 : 4, os);
        for (int page = 0; ifd > 0 && page < 100; page++)
        {
            long n = (long)U(ifd, cs);
            var vals = new Dictionary<int, long[]>();
            for (long k = 0; k < n; k++)
            {
                long e = ifd + cs + k * es;
                int tag = (int)U(e, 2), type = (int)U(e + 2, 2);
                long count = (long)U(e + 4, big ? 8 : 4);
                if (tag is not (273 or 279 or 324 or 325)) continue;
                int size = type == 3 ? 2 : type == 4 ? 4 : 8;
                long at = count * size <= os ? e + 4 + (big ? 8 : 4) : (long)U(e + 4 + (big ? 8 : 4), os);
                vals[tag] = Enumerable.Range(0, (int)count).Select(i => (long)U(at + i * size, size)).ToArray();
            }
            var offs = vals.GetValueOrDefault(273) ?? vals.GetValueOrDefault(324) ?? Array.Empty<long>();
            var cnts = vals.GetValueOrDefault(279) ?? vals.GetValueOrDefault(325) ?? Array.Empty<long>();
            for (int i = 0; i < offs.Length; i++) r.Add((offs[i], cnts[i]));
            ifd = (long)U(ifd + cs + n * es, os);
        }
        return r;
    }

    static void AssertImageDataUnchanged(byte[] src, byte[] dst)
    {
        var a = ImageData(src);
        Assert.True(a.Length > 0, "no image data found");
        Assert.True(a.AsSpan().SequenceEqual(ImageData(dst)), "the compressed image data changed");
        var pa = ImageCodec.DecodeAll(src);
        var pb = ImageCodec.DecodeAll(dst);
        Assert.Equal(pa.Length, pb.Length);
        for (int i = 0; i < pa.Length; i++)
        {
            Assert.Equal((pa[i].Width, pa[i].Height), (pb[i].Width, pb[i].Height));
            Assert.True(pa[i].GetBytes().SequenceEqual(pb[i].GetBytes()), "decoded pixels differ");
        }
    }
}
