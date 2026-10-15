using System.Text.Json;
using Xunit;
using Xunit.Abstractions;
using static Aardvark.ImageCodecs.Tests.TestUtil;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>
/// Metadata rewrites of camera files (ExifTool's sample images, drone images of the test-data repository): the edit is
/// seen, the image data is untouched, and with ExifTool installed every other tag ExifTool reads, maker notes included,
/// comes out the same, without new validation warnings.
/// </summary>
public class MetadataWriteCameraTests
{
    readonly ITestOutputHelper _out;
    public MetadataWriteCameraTests(ITestOutputHelper output) => _out = output;

    public static IEnumerable<object[]> Cameras()
        => ThirdPartyFiles("metadata/exiftool", ".jpg", ".tif", ".png", ".webp", ".jp2", ".dng", ".btf").Concat(ThirdPartyFiles("metadata/odm", ".jpg", ".JPG"));

    static MetadataEdit Edit() => new()
    {
        Artist = "Re-tagged", Title = "camera file", DateTaken = new DateTime(2020, 1, 2, 3, 4, 5), DateTakenOffset = TimeSpan.FromHours(2),
        Gps = new GpsEdit { Latitude = 47.07, Longitude = 15.44, Altitude = 350, ProcessingMethod = "MANUAL" },
    };

    /// <summary>Tags that the edit changes, or that describe the file and its layout rather than the content.</summary>
    static bool Expected(string key) =>
        key == "SourceFile" || key.StartsWith("System:") || key.StartsWith("File:") || key.StartsWith("ExifTool:") || key.StartsWith("Composite:") || key.StartsWith("GPS:")
        || key.StartsWith("XMP-") || key.StartsWith("IPTC:")
        || key is "IFD0:Artist" or "ExifIFD:DateTimeOriginal" or "ExifIFD:OffsetTimeOriginal" or "ExifIFD:SubSecTimeOriginal" or "ExifIFD:ImageTitle"
            or "IFD1:ThumbnailOffset" or "IFD1:StripOffsets" or "Photoshop:IPTCDigest" or "IFD0:XPTitle" or "IFD0:XPAuthor" or "IPTC:CurrentIPTCDigest"
            // (raised to 2.31 for OffsetTimeOriginal)
            or "ExifIFD:ExifVersion" or "GPS:GPSVersionID";

    [TestDataTheory("fixtures/metadata/exiftool/README.md"), MemberData(nameof(Cameras))]
    public void OtherTagsSurvive(string file)
    {
        var path = Fixture(file);
        var src = File.ReadAllBytes(path);
        byte[] dst;
        try { dst = ImageCodec.WriteMetadata(src, Edit()); }
        catch (ImageNotSupportedException e) { _out.WriteLine("not supported: " + e.Message); return; }
        var m = ImageCodec.ReadMetadata(dst);
        Assert.Equal("Re-tagged", m.Artist);
        Assert.Equal(new DateTime(2020, 1, 2, 3, 4, 5), m.DateTaken);
        Assert.Equal(47.07, m.Gps!.Latitude!.Value, 9);
        Assert.Equal(ImageCodec.ReadMetadata(src).Make, m.Make);
        Assert.True(MetadataWriteTests.ImageData(src).AsSpan().SequenceEqual(MetadataWriteTests.ImageData(dst)), "image data changed");

        var et = MetadataWriteTests.ExifTool;
        if (et == null) return;
        var tmp = Path.Combine(Path.GetTempPath(), "pixcodec-metawrite-" + Guid.NewGuid().ToString("N") + Path.GetExtension(file));
        File.WriteAllBytes(tmp, dst);
        try
        {
            var before = MetadataWriteTests.RunJson(et, path);
            var after = MetadataWriteTests.RunJson(et, tmp);
            var diffs = new List<string>();
            foreach (var p in before.EnumerateObject())
            {
                if (Expected(p.Name)) continue;
                if (!after.TryGetProperty(p.Name, out var v)) { diffs.Add($"{p.Name}: lost ({Short(p.Value)})"); continue; }
                if (p.Value.GetRawText() == v.GetRawText()) continue;
                if (p.Name.EndsWith(":StripOffsets") && before.TryGetProperty(p.Name[..^7] + "ByteCounts", out var bc) && bc.ToString() == "0")
                    continue;   // a file offset of nothing (GeoJP2's empty image)
                if (p.Name.EndsWith(":MPImageStart") && p.Value.ValueKind == JsonValueKind.Number && v.ValueKind == JsonValueKind.Number
                    && v.GetInt64() - p.Value.GetInt64() == dst.Length - src.Length)
                    continue;   // the images behind the first one move with the end of the file
                if (p.Name.EndsWith("ImageStart") || p.Name.EndsWith("ImageOffset"))
                {
                    // embedded previews / MPF images: ExifTool gives file offsets, which move with the metadata; the images must not change
                    int cut = p.Name.EndsWith("Start") ? p.Name.Length - 5 : p.Name.Length - 6;
                    var tag = p.Name[..cut];
                    var a = MetadataWriteTests.RunBytes(et, "-b", "-" + tag, path);
                    var b = MetadataWriteTests.RunBytes(et, "-b", "-" + tag, tmp);
                    if (a.Length > 0 && a.AsSpan().SequenceEqual(b)) { _out.WriteLine($"  {tag}: {a.Length} bytes, same"); continue; }
                }
                diffs.Add($"{p.Name}: {Short(p.Value)} -> {Short(v)}");
            }
            foreach (var d in diffs) _out.WriteLine("  " + d);
            // (" [x2]": how often a warning occurs)
            static string[] Norm(string[] l) => l.Select(x => System.Text.RegularExpressions.Regex.Replace(x, @" \[x\d+\]$", "")).ToArray();
            var vb = Norm(MetadataWriteTests.Validate(et, path));
            var va = Norm(MetadataWriteTests.Validate(et, tmp));
            // (AFCP trailers hold file offsets, which ExifTool adjusts for; they are not rewritten)
            var added = va.Where(l => !char.IsDigit(l.FirstOrDefault()) && l != "OK" && !vb.Contains(l) && !l.Contains("Adjusted AFCP offsets")
                && !(file.EndsWith(".btf") && l.Contains("GPSInfo:InteropIndex"))).ToList();
            foreach (var l in added) _out.WriteLine("  new warning: " + l);
            _out.WriteLine($"{Path.GetFileName(file)}: {before.EnumerateObject().Count()} tags compared");
            Assert.True(diffs.Count == 0 && added.Count == 0, $"{diffs.Count} tags changed, {added.Count} new warnings");
        }
        finally { File.Delete(tmp); }
    }

    static string Short(JsonElement v) { var s = v.GetRawText(); return s.Length > 60 ? s[..60] + "..." : s; }
}
