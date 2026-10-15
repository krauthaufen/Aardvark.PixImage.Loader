using System.Buffers.Binary;
using System.Text;
using Xunit;
using static PixCodec.Tests.TestUtil;

namespace PixCodec.Tests;

/// <summary>
/// The metadata writing API beyond the golden cases: writing through ImageMetadata and the encoders, extended XMP, raw
/// EXIF / XMP / IPTC edits, removal leaving no traces, multi-page TIFF, in place and stream writing, idempotence and damaged input.
/// </summary>
public class MetadataWriteApiTests
{
    static RawImage Gradient(int w = 24, int h = 16)
    {
        var img = RawImage.Create(w, h, ColorModel.Rgb, SampleType.UInt8);
        var b = img.GetBytes();
        for (int i = 0; i < b.Length; i++) b[i] = (byte)(i * 7);
        return img;
    }

    static readonly ImageFormat[] Writable = { ImageFormat.Jpeg, ImageFormat.Png, ImageFormat.Tiff, ImageFormat.Webp, ImageFormat.Jp2 };

    static MetadataEdit Drone() => new()
    {
        Make = "DJI", Model = "FC6310R", DateTaken = new DateTime(2024, 6, 15, 14, 23, 45, 120), DateTakenOffset = TimeSpan.FromHours(2),
        Title = "pier 3", Keywords = new[] { "bridge", "pier" },
        Gps = new GpsEdit { Latitude = 47.0707123, Longitude = -15.4395456, Altitude = 412.35, AltitudeAboveEllipsoid = true, HorizontalError = 0.02 },
    };

    static void CheckDrone(PhotoMetadata m, string what)
    {
        Assert.True(("DJI", "FC6310R", "pier 3") == (m.Make, m.Model, m.Title), what);
        Assert.Equal(new DateTime(2024, 6, 15, 14, 23, 45, 120), m.DateTaken);
        Assert.Equal(TimeSpan.FromHours(2), m.DateTakenOffset);
        Assert.Equal(new[] { "bridge", "pier" }, m.Keywords);
        Assert.Equal(47.0707123, m.Gps!.Latitude!.Value, 9);
        Assert.Equal(-15.4395456, m.Gps.Longitude!.Value, 9);
        Assert.Equal(412.35, m.Gps.Altitude!.Value, 9);
        Assert.Equal(2, m.Gps.AltitudeReference);
        Assert.Equal(0.02, m.Gps.HorizontalError!.Value, 12);
    }

    [Fact]
    public void ApplyToMetadataThenEncode()
    {
        foreach (var f in Writable)
        {
            var img = Gradient();
            Drone().ApplyTo(img);
            var bytes = ImageCodec.Encode(img, f);
            CheckDrone(ImageCodec.ReadMetadata(bytes), f.ToString());
            // decode -> encode keeps it, also across formats
            var again = ImageCodec.Decode(bytes);
            foreach (var g in Writable) CheckDrone(ImageCodec.ReadMetadata(ImageCodec.Encode(again, g)), $"{f} -> {g}");
        }
    }

    [Fact]
    public void ApplyToChangesExistingMetadata()
    {
        var img = ImageCodec.Decode(File.ReadAllBytes(Fixture("metadata/meta.jpg")));
        new MetadataEdit { Artist = "Someone Else", RemoveGps = true }.ApplyTo(img);
        foreach (var f in Writable)
        {
            var m = ImageCodec.ReadMetadata(ImageCodec.Encode(img, f));
            Assert.Equal("Someone Else", m.Artist);
            Assert.Equal("DJI", m.Make);
            Assert.Null(m.Gps?.Latitude);
            Assert.Equal(-45.3, m.Pose!.CameraYaw);
        }
    }

    [Fact]
    public void Jp2KeepsXmpThroughDecodeAndEncode()
    {
        var src = File.ReadAllBytes(Fixture("metadata/meta.jp2"));
        var img = ImageCodec.Decode(src);
        Assert.NotNull(img.Metadata?.Xmp);
        var m = ImageCodec.ReadMetadata(ImageCodec.Encode(img, ImageFormat.Jp2));
        Assert.Equal(-45.3, m.Pose!.CameraYaw);
        Assert.Equal("Pier 3 north face", m.Title);
        Assert.Contains("inspection", m.Keywords);
    }

    // ------------------------------------------------------------------ extended XMP

    static readonly string Long = string.Concat(Enumerable.Range(0, 9000).Select(i => $"entry {i};"));

    [Fact]
    public void JpegEncoderWritesExtendedXmp()
    {
        var img = Gradient();
        new MetadataEdit { Title = "t" }.SetXmpNamespace("pcx", "http://pixcodec.example/ns/1.0/").SetXmp("pcx:Long", Long).ApplyTo(img);
        Assert.True(img.Metadata!.Xmp!.Length > 65536);
        var jpg = ImageCodec.Encode(img, ImageFormat.Jpeg);
        var m = ImageCodec.ReadMetadata(jpg);
        Assert.Equal(Long.Trim(), m.Xmp!["pcx:Long"]);
        Assert.Equal("t", m.Title);
        var guid = m.Xmp!["xmpNote:HasExtendedXMP"]!;
        Assert.Equal(32, guid.Length);
        // GUID: MD5 of the extended part, which the segments carry in pieces of at most 64 KB
        var parts = Segments(jpg).Where(s => s.Marker == 0xE1 && s.Payload.AsSpan().StartsWith("http://ns.adobe.com/xmp/extension/\0"u8)).ToList();
        Assert.True(parts.Count >= 2);
        int total = (int)BinaryPrimitives.ReadUInt32BigEndian(parts[0].Payload.AsSpan(67));
        var ext = new byte[total];
        foreach (var p in parts)
        {
            Assert.Equal(guid, Encoding.ASCII.GetString(p.Payload, 35, 32));
            p.Payload.AsSpan(75).CopyTo(ext.AsSpan((int)BinaryPrimitives.ReadUInt32BigEndian(p.Payload.AsSpan(71))));
        }
        Assert.Equal(guid, Convert.ToHexString(System.Security.Cryptography.MD5.HashData(ext)));
        // and a decoded JPEG with extended XMP keeps the whole of it when re-encoded
        var back = ImageCodec.Decode(jpg);
        var m2 = ImageCodec.ReadMetadata(ImageCodec.Encode(back, ImageFormat.Png));
        Assert.Equal("t", m2.Title);
    }

    [Fact]
    public void RewriteKeepsAndRebuildsExtendedXmp()
    {
        var big = ImageCodec.WriteMetadata(File.ReadAllBytes(Fixture("metadata/meta.jpg")),
            new MetadataEdit().SetXmpNamespace("pcx", "http://pixcodec.example/ns/1.0/").SetXmp("pcx:Long", Long));
        // an edit of something else keeps the extended part (merged and split again)
        var again = ImageCodec.WriteMetadata(big, new MetadataEdit { Artist = "x" });
        var m = ImageCodec.ReadMetadata(again);
        Assert.Equal(Long.Trim(), m.Xmp!["pcx:Long"]);
        Assert.Equal("x", m.Artist);
        Assert.Equal(-45.3, m.Pose!.CameraYaw);
        Assert.Single(m.Xmp!.Packets, p => p.Contains("HasExtendedXMP"));
        // removing the big property makes it a single packet again
        var small = ImageCodec.WriteMetadata(again, new MetadataEdit().RemoveXmp("pcx:Long"));
        Assert.DoesNotContain(Segments(small), s => s.Payload.AsSpan().StartsWith("http://ns.adobe.com/xmp/extension/\0"u8));
        Assert.Null(ImageCodec.ReadMetadata(small).Xmp!["xmpNote:HasExtendedXMP"]);
    }

    record Seg(int Marker, byte[] Payload);

    static List<Seg> Segments(byte[] jpg)
    {
        var r = new List<Seg>();
        for (int p = 2; p + 4 <= jpg.Length && jpg[p] == 0xFF && jpg[p + 1] != 0xDA;)
        {
            int len = BinaryPrimitives.ReadUInt16BigEndian(jpg.AsSpan(p + 2));
            r.Add(new Seg(jpg[p + 1], jpg.AsSpan(p + 4, len - 2).ToArray()));
            p += 2 + len;
        }
        return r;
    }

    // ------------------------------------------------------------------ raw tags

    [Fact]
    public void RawExifXmpAndIptc()
    {
        var src = File.ReadAllBytes(Fixture("metadata/meta.jpg"));
        var edit = new MetadataEdit()
            .SetExif(ExifIfd.Exif, 0xA420, ExifType.Ascii, "feedfacefeedfacefeedfacefeedface")      // ImageUniqueID
            .SetExif(ExifIfd.Image, 0x0131, ExifType.Ascii, "raw software")
            .SetExif(ExifIfd.Exif, 0x9204, ExifType.SRational, -1.5)
            .SetExif(ExifIfd.Gps, 0x0011, ExifType.Rational, new ExifRational(1234, 10))
            .SetExif(ExifIfd.Exif, 0xC000, ExifType.Short, new ushort[] { 1, 2, 3 })                  // a private tag
            .RemoveExif(ExifIfd.Image, 0x013B)                                                          // Artist
            .SetXmp("drone-dji:GimbalYawDegree", "+12.50")
            .SetXmp("exif:Flash/exif:Fired", "True")
            .SetXmp("Iptc4xmpCore:CreatorContactInfo/Iptc4xmpCore:CiEmailWork", "a@b.c")
            .SetXmp("dc:subject", new[] { "x", "y" }, XmpArrayType.Bag)
            .RemoveXmp("drone-dji:RtkFlag")
            .RemoveXmp("crs:*")
            .SetIptc(2, 105, "Headline ä")
            .RemoveIptc(2, 90);
        var m = ImageCodec.ReadMetadata(ImageCodec.WriteMetadata(src, edit));
        Assert.Equal("feedfacefeedfacefeedfacefeedface", m.ImageUniqueId);
        Assert.Equal("raw software", m.Software);
        Assert.Equal(-1.5, m.ExposureBias);
        Assert.Equal(123.4, m.Gps!.ImageDirection!.Value, 9);
        Assert.Equal(new ushort[] { 1, 2, 3 }, (ushort[])m.Exif!.Get(ExifIfd.Exif, 0xC000)!.Value);
        Assert.Null(m.Exif!.Get(ExifIfd.Image, 0x013B));
        Assert.Equal(12.5, m.Pose!.CameraYaw);
        Assert.Null(m.Pose.RtkFlag);
        Assert.Equal("True", m.Xmp!["exif:Flash/exif:Fired"]);
        Assert.Equal("a@b.c", m.Xmp!["Iptc4xmpCore:CreatorContactInfo/Iptc4xmpCore:CiEmailWork"]);
        Assert.Equal(new[] { "x", "y" }, m.Xmp!.Get("dc:subject")!.Values);
        Assert.Equal("Headline ä", m.Iptc!["Headline"]);
        Assert.Null(m.Iptc!["City"]);
        Assert.Equal("Graz", m.City);   // (still in XMP)

        Assert.Throws<ArgumentException>(() => new MetadataEdit().SetExif(ExifIfd.Image, 273, ExifType.Long, 5u));
        Assert.Throws<ArgumentException>(() => new MetadataEdit().SetExif(ExifIfd.Image, 0x8769, ExifType.Long, 5u));
        Assert.Throws<ArgumentException>(() => new MetadataEdit().SetExif(ExifIfd.Image, 0x010F, ExifType.Short, "text"));
        Assert.Throws<ArgumentException>(() => new MetadataEdit().SetXmp("noprefix", "v"));
        Assert.Throws<ArgumentException>(() => ImageCodec.WriteMetadata(src, new MetadataEdit().SetXmp("unknown:Thing", "v")));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageCodec.WriteMetadata(src, new MetadataEdit { Gps = new GpsEdit { Latitude = 91 } }));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageCodec.WriteMetadata(src, new MetadataEdit { Orientation = 9 }));
    }

    [Fact]
    public void IptcSwitchesToUtf8AndTracksThePhotoshopDigest()
    {
        // ExifTool's IPTC sample has Latin-1 IPTC and an IPTC digest
        var src = ImageCodec.WriteMetadata(File.ReadAllBytes(Fixture("metadata/meta.jpg")), new MetadataEdit { City = "Zürich", Title = "Ünïcödé" });
        var m = ImageCodec.ReadMetadata(src);
        Assert.Equal("Zürich", m.Iptc!["City"]);
        Assert.Equal("Ünïcödé", m.Iptc!["ObjectName"]);
        // dataset length limits of IIM: By-line is at most 32 bytes
        var cut = ImageCodec.ReadMetadata(ImageCodec.WriteMetadata(src, new MetadataEdit { Artist = new string('ä', 40) }));
        Assert.Equal(new string('ä', 16), cut.Iptc!["By-line"]);
        Assert.Equal(new string('ä', 40), cut.Artist);
    }

    [Fact]
    public void RemovedValuesLeaveNoTraces()
    {
        // GPS: the rationals of the latitude must be gone from the file, not just unreferenced
        foreach (var file in new[] { "metadata/meta.jpg", "metadata/meta.tif", "metadata/meta.png", "metadata/meta.webp", "metadata/meta.jp2" })
        {
            var src = File.ReadAllBytes(Fixture(file));
            var lat = ImageCodec.ReadMetadata(src).Exif!.Get(ExifIfd.Gps, 2)!;
            var be = ImageCodec.ReadMetadata(src).Exif!.BigEndian;
            var r = (ExifRational[])lat.Value;
            var pattern = new byte[24];
            for (int i = 0; i < 3; i++)
            {
                if (be) { BinaryPrimitives.WriteUInt32BigEndian(pattern.AsSpan(i * 8), (uint)r[i].Numerator); BinaryPrimitives.WriteUInt32BigEndian(pattern.AsSpan(i * 8 + 4), (uint)r[i].Denominator); }
                else { BinaryPrimitives.WriteUInt32LittleEndian(pattern.AsSpan(i * 8), (uint)r[i].Numerator); BinaryPrimitives.WriteUInt32LittleEndian(pattern.AsSpan(i * 8 + 4), (uint)r[i].Denominator); }
            }
            Assert.True(src.AsSpan().IndexOf(pattern) >= 0, file);
            var dst = ImageCodec.WriteMetadata(src, new MetadataEdit { RemoveGps = true });
            Assert.True(dst.AsSpan().IndexOf(pattern) < 0, $"{file}: GPS latitude still in the file");
            Assert.True(dst.AsSpan().IndexOf("47,4.24"u8) < 0, $"{file}: XMP latitude still in the file");
            var all = ImageCodec.WriteMetadata(src, new MetadataEdit { RemoveAll = true });
            Assert.True(all.AsSpan().IndexOf("Jane Pilot"u8) < 0, $"{file}: artist still in the file");
            Assert.True(all.AsSpan().IndexOf("FC6310R"u8) < 0, $"{file}: model still in the file");
            Assert.True(all.AsSpan().IndexOf(pattern) < 0, $"{file}: GPS still in the file");
        }
    }

    [Fact]
    public void TiffEditsTheFirstPageOnly()
    {
        var pages = ImageCodec.Encode(Gradient(), ImageFormat.Tiff);
        using (var ms = new MemoryStream())
        {
            var p2 = Gradient(8, 8);
            new MetadataEdit { Artist = "page two" }.ApplyTo(p2);
            var p1 = Gradient();
            new MetadataEdit { Artist = "page one" }.ApplyTo(p1);
            Tiff.Encode(new[] { p1, p2 }, ms);
            pages = ms.ToArray();
        }
        var dst = ImageCodec.WriteMetadata(pages, new MetadataEdit { Artist = "first", RemoveAll = false });
        var all = ImageCodec.DecodeAll(dst);
        Assert.Equal(2, all.Length);
        Assert.Equal("first", ImageCodec.ReadMetadata(dst).Artist);
        Assert.True(dst.AsSpan().IndexOf("page two"u8) >= 0);
        Assert.True(dst.AsSpan().IndexOf("page one"u8) < 0);   // replaced and zeroed
        Assert.Equal(pages.Length, dst.AsSpan(0, pages.Length).ToArray().Length);
    }

    [Fact]
    public void SecondIdenticalEditChangesNothing()
    {
        foreach (var file in new[] { "metadata/meta.jpg", "metadata/meta.tif", "metadata/meta.png", "metadata/meta.webp", "metadata/meta.jp2" })
        {
            var once = ImageCodec.WriteMetadata(File.ReadAllBytes(Fixture(file)), Drone());
            var twice = ImageCodec.WriteMetadata(once, Drone());
            Assert.True(once.AsSpan().SequenceEqual(twice), $"{file}: {once.Length} -> {twice.Length} bytes, first difference at {once.AsSpan().CommonPrefixLength(twice)}");
        }
    }

    [Fact]
    public void PathAndStreamApis()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pixcodec-metawrite-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var file in new[] { "metadata/meta.jpg", "metadata/meta.tif", "metadata/meta.png", "metadata/meta.webp", "metadata/meta.jp2" })
            {
                var src = File.ReadAllBytes(Fixture(file));
                var want = ImageCodec.WriteMetadata(src, Drone());
                // stream to stream (a seekable TIFF input is patched while copied)
                var o = new MemoryStream();
                ImageCodec.WriteMetadata(new MemoryStream(src), o, Drone());
                Assert.True(want.AsSpan().SequenceEqual(o.ToArray()), file + " stream");
                // file to another file and in place
                var a = Path.Combine(dir, "a" + Path.GetExtension(file));
                var b = Path.Combine(dir, "b" + Path.GetExtension(file));
                File.WriteAllBytes(a, src);
                ImageCodec.WriteMetadata(a, b, Drone());
                Assert.True(want.AsSpan().SequenceEqual(File.ReadAllBytes(b)), file + " path");
                ImageCodec.WriteMetadata(a, a, Drone());
                Assert.True(want.AsSpan().SequenceEqual(File.ReadAllBytes(a)), file + " in place");
                Assert.Empty(Directory.GetFiles(dir, "*.pixcodec-tmp"));
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void FormatsWithoutAPlaceForMetadata()
    {
        var img = Gradient();
        foreach (var f in new[] { ImageFormat.Bmp, ImageFormat.Tga, ImageFormat.Gif })
            Assert.Throws<ImageNotSupportedException>(() => ImageCodec.WriteMetadata(ImageCodec.Encode(img, f), Drone()));
        // a raw JPEG 2000 codestream has no boxes
        var j2k = Jp2.Encode(img, new Jp2EncodeOptions { RawCodestream = true });
        Assert.Throws<ImageNotSupportedException>(() => ImageCodec.WriteMetadata(j2k, Drone()));
    }

    [Theory, MemberData(nameof(Inputs))]
    public void DamagedInputs(string file)
    {
        var data = File.ReadAllBytes(Fixture(file));
        var rnd = new Random(file.Length * 31);
        for (int k = 0; k < 150; k++)
        {
            var d = k < 50 ? data.AsSpan(0, rnd.Next(1, data.Length)).ToArray() : (byte[])data.Clone();
            if (k >= 50)
                for (int j = 0; j < 1 + k % 8; j++) d[rnd.Next(Math.Min(d.Length, 4096))] = (byte)rnd.Next(256);
            byte[] r;
            try { r = ImageCodec.WriteMetadata(d, k % 2 == 0 ? Drone() : new MetadataEdit { RemoveGps = true, Artist = "a" }); }
            catch (ImageFormatException) { continue; }
            catch (ImageNotSupportedException) { continue; }
            catch (Exception e) { Assert.Fail($"case {k}: {e}"); throw; }
            // what was written can be read
            try { _ = ImageCodec.ReadMetadata(r).ToString(); }
            catch (ImageFormatException) { }
            catch (ImageNotSupportedException) { }
        }
    }

    public static IEnumerable<object[]> Inputs() => new[] { "metadata/meta.jpg", "metadata/meta.tif", "metadata/meta.png", "metadata/meta.webp", "metadata/meta.jp2", "metadata-write/in/big.btf" }.Select(f => new object[] { f });
}
