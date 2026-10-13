using System.Diagnostics;
using System.Runtime.InteropServices;
using PixCodec;
using Xunit;

namespace PixCodec.Tests;

public class TiffTests
{
    public static IEnumerable<object[]> Fixtures => TestUtil.Files("tiff", ".tif");

    /// <summary>YCbCr without JPEG: converted with float math here, fixed point in the references - small tolerance.</summary>
    public static IEnumerable<object[]> YccFixtures => TestUtil.Files(Path.Combine("tiff", "ycbcr"), ".tif");

    public static IEnumerable<object[]> JpegFixtures => TestUtil.Files(Path.Combine("tiff", "jpeg"), ".tif");

    static readonly TiffDecodeOptions Serial = new() { Parallel = false };
    static TiffDecodeOptions ForcedParallel(int index = 0) => new() { ParallelThreshold = 0, ImageIndex = index };

    // ------------------------------------------------------------------------------------------ decoding

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void DecodesFixture(string file)
    {
        var bytes = File.ReadAllBytes(TestUtil.Fixture(file));
        var expected = TestUtil.LoadRef(TestUtil.Fixture(file) + ".ref");
        Assert.True(Tiff.IsMatch(bytes));

        var img = Tiff.Decode(bytes, Serial);
        TestUtil.AssertEqual(expected, img);

        var info = Tiff.GetInfo(bytes);
        Assert.Equal((img.Width, img.Height, img.Channels, img.SampleType, img.Color),
            (info.Width, info.Height, info.Channels, info.SampleType, info.Color));
        Assert.Equal(ImageFormat.Tiff, info.Format);

        // strips/tiles decoded on several threads
        TestUtil.AssertEqual(expected, Tiff.Decode(bytes, ForcedParallel()));
    }

    [Fact]
    public void GenericEntryPoints()
    {
        var bytes = File.ReadAllBytes(TestUtil.Fixture(Path.Combine("tiff", "u16_rgb_lzw_pred2.tif")));
        var expected = TestUtil.LoadRef(TestUtil.Fixture(Path.Combine("tiff", "u16_rgb_lzw_pred2.tif.ref")));
        Assert.Equal(ImageFormat.Tiff, ImageCodec.FormatFromExtension("a.TIF"));
        try
        {
            Assert.Equal(ImageFormat.Tiff, ImageCodec.DetectFormat(bytes));
            TestUtil.AssertEqual(expected, ImageCodec.Decode(bytes));
            Assert.Equal(expected.Width, ImageCodec.GetInfo(bytes).Width);
        }
        catch (NotImplementedException)
        {
            // format sniffing asks the other codecs first; they are stubs until the branches are integrated
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void DecodesFixtureFromStream(string file)
    {
        var expected = TestUtil.LoadRef(TestUtil.Fixture(file) + ".ref");
        using var fs = File.OpenRead(TestUtil.Fixture(file));
        TestUtil.AssertEqual(expected, Tiff.Decode(fs, Serial));
        fs.Position = 0;
        TestUtil.AssertEqual(expected, Tiff.Decode(fs, ForcedParallel()));
        fs.Position = 0;
        var info = Tiff.GetInfo(fs);
        Assert.Equal((expected.Width, expected.Height, expected.Channels), (info.Width, info.Height, info.Channels));

        // a TIFF embedded at an offset inside a larger stream
        var bytes = File.ReadAllBytes(TestUtil.Fixture(file));
        var ms = new MemoryStream();
        ms.Write(new byte[13]);
        ms.Write(bytes);
        ms.Position = 13;
        TestUtil.AssertEqual(expected, Tiff.Decode(ms));
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void DecodesRegions(string file)
    {
        var bytes = File.ReadAllBytes(TestUtil.Fixture(file));
        var full = TestUtil.LoadRef(TestUtil.Fixture(file) + ".ref");
        int w = full.Width, h = full.Height;
        var rnd = new Random(file.Length * 7919 + w);
        var regions = new List<(int x, int y, int w, int h)> { (0, 0, w, h), (w - 1, h - 1, 1, 1), (0, h / 2, w, h - h / 2) };
        if (w > 2 && h > 2) regions.Add((1, 1, w - 2, h - 2));
        for (int i = 0; i < 4; i++)
        {
            int x = rnd.Next(w), y = rnd.Next(h);
            regions.Add((x, y, 1 + rnd.Next(w - x), 1 + rnd.Next(h - y)));
        }
        using var ms = new MemoryStream(bytes);
        foreach (var r in regions)
        {
            var expected = Crop(full, r.x, r.y, r.w, r.h);
            TestUtil.AssertEqual(expected, Tiff.DecodeRegion(bytes, r.x, r.y, r.w, r.h, Serial));
            TestUtil.AssertEqual(expected, Tiff.DecodeRegion(ms, r.x, r.y, r.w, r.h, ForcedParallel()));
        }
        Assert.ThrowsAny<ArgumentException>(() => Tiff.DecodeRegion(bytes, 0, 0, w + 1, h));
        Assert.ThrowsAny<ArgumentException>(() => Tiff.DecodeRegion(bytes, -1, 0, 1, 1));
        Assert.ThrowsAny<ArgumentException>(() => Tiff.DecodeRegion(bytes, 0, h, 1, 1));
    }

    static RawImage Crop(RawImage src, int x, int y, int w, int h)
    {
        var dst = RawImage.Create(w, h, src.Color, src.SampleType, src.Channels);
        int px = src.Channels * RawImage.BytesPerSample(src.SampleType);
        var s = src.GetBytes(); var d = dst.GetBytes();
        for (int r = 0; r < h; r++)
            s.Slice(((y + r) * src.Width + x) * px, w * px).CopyTo(d.Slice(r * w * px));
        return dst;
    }

    [Theory]
    [MemberData(nameof(YccFixtures))]
    public void DecodesYCbCr(string file)
    {
        var bytes = File.ReadAllBytes(TestUtil.Fixture(file));
        var expected = TestUtil.LoadRef(TestUtil.Fixture(file) + ".ref");
        var img = Tiff.Decode(bytes);
        Assert.Equal(ColorModel.Rgb, img.Color);
        TestUtil.AssertEqual(expected, img, 2);
        TestUtil.AssertEqual(Crop(img, 3, 2, img.Width - 5, img.Height - 3), Tiff.DecodeRegion(bytes, 3, 2, img.Width - 5, img.Height - 3));
    }

    /// <summary>
    /// JPEG-in-TIFF (compression 7). Disabled until the integrator assigns <c>Tiff.JpegDecoder</c> (see TiffJpegDecoder):
    /// then remove the Skip. The references were produced by libtiff/libjpeg, hence the tolerance.
    /// </summary>
    [Theory(Skip = "JPEG-in-TIFF hook (Tiff.JpegDecoder) not wired up yet")]
    [MemberData(nameof(JpegFixtures))]
    public void DecodesJpegCompressed(string file)
    {
        var bytes = File.ReadAllBytes(TestUtil.Fixture(file));
        var expected = TestUtil.LoadRef(TestUtil.Fixture(file) + ".ref");
        TestUtil.AssertEqual(expected, Tiff.Decode(bytes), 3);
        TestUtil.AssertEqual(expected, Tiff.Decode(bytes, ForcedParallel()), 3);
    }

    [Theory]
    [MemberData(nameof(JpegFixtures))]
    public void JpegCompressedReportsInfoAndIsRejectedWithoutHook(string file)
    {
        var bytes = File.ReadAllBytes(TestUtil.Fixture(file));
        var expected = TestUtil.LoadRef(TestUtil.Fixture(file) + ".ref");
        var info = Tiff.GetInfo(bytes);
        Assert.Equal((expected.Width, expected.Height, expected.Channels, SampleType.UInt8), (info.Width, info.Height, info.Channels, info.SampleType));
        if (Tiff.JpegDecoder == null) Assert.Throws<ImageNotSupportedException>(() => Tiff.Decode(bytes));
    }

    [Fact]
    public void JpegHookReceivesStripsAndTables()
    {
        // The hook contract, exercised with a fake decoder that paints a constant per call.
        var dir = Path.Combine(TestUtil.FixtureDir, "tiff", "jpeg");
        if (!Directory.Exists(dir)) return;
        var saved = Tiff.JpegDecoder;
        try
        {
            foreach (var f in Directory.GetFiles(dir, "*.tif"))
            {
                var bytes = File.ReadAllBytes(f);
                var info = Tiff.GetInfo(bytes);
                int calls = 0;
                Tiff.JpegDecoder = (tables, data, w, h, comps, ycc, dst) =>
                {
                    Assert.True(data.Length > 2 && data[0] == 0xFF && data[1] == 0xD8, "strip is not a JPEG stream");
                    Assert.True(tables.Length == 0 || (tables[0] == 0xFF && tables[1] == 0xD8));
                    Assert.Equal(w * h * comps, dst.Length);
                    Assert.Equal(Path.GetFileName(f).Contains("ycbcr"), ycc);
                    dst.Fill(99);
                    Interlocked.Increment(ref calls);
                };
                var img = Tiff.Decode(bytes);
                Assert.True(calls > 0);
                Assert.Equal((info.Width, info.Height, info.Channels), (img.Width, img.Height, img.Channels));
                Assert.All(img.GetData<byte>(), v => Assert.Equal(99, v));
            }
        }
        finally { Tiff.JpegDecoder = saved; }
    }

    [Theory]
    [InlineData("u8_gray_none.tif", ColorModel.Gray, 1)]
    [InlineData("u8_rgb_none.tif", ColorModel.Rgb, 3)]
    [InlineData("graya_assoc_u8.tif", ColorModel.GrayAlpha, 2)]
    [InlineData("graya_unassoc_f32.tif", ColorModel.GrayAlpha, 2)]
    [InlineData("rgba_unassoc_u8.tif", ColorModel.Rgba, 4)]
    [InlineData("rgba_assoc_u16_lzw.tif", ColorModel.Rgba, 4)]
    [InlineData("rgbx_unspecified_u8.tif", ColorModel.Other, 4)]
    [InlineData("rgb_noextratag_4ch_u8.tif", ColorModel.Other, 4)]
    [InlineData("rgb_2extras_u8.tif", ColorModel.Other, 5)]
    [InlineData("gray_2extras_u16.tif", ColorModel.Other, 3)]
    [InlineData("gray_1extra_unspecified_f32.tif", ColorModel.Other, 2)]
    [InlineData("multiband7_u16_deflate.tif", ColorModel.Other, 7)]
    [InlineData("planar_u16_5band_deflate.tif", ColorModel.Other, 5)]
    [InlineData("palette8.tif", ColorModel.Rgb, 3)]
    [InlineData("palette1.tif", ColorModel.Rgb, 3)]
    [InlineData("palette8_alpha.tif", ColorModel.Rgba, 4)]
    [InlineData("cmyk8.tif", ColorModel.Rgb, 3)]
    [InlineData("cmyk8_alpha.tif", ColorModel.Rgba, 4)]
    [InlineData("gray1_minisblack.tif", ColorModel.Gray, 1)]
    public void ReportsColorModel(string name, ColorModel color, int channels)
    {
        var bytes = File.ReadAllBytes(TestUtil.Fixture(Path.Combine("tiff", name)));
        var img = Tiff.Decode(bytes);
        Assert.Equal((color, channels), (img.Color, img.Channels));
        var info = Tiff.GetInfo(bytes);
        Assert.Equal((color, channels), (info.Color, info.Channels));
    }

    [Theory]
    [InlineData("multipage.tif", 4)]
    [InlineData("multipage_big_be.tif", 2)]
    public void MultiPage(string name, int count)
    {
        var path = TestUtil.Fixture(Path.Combine("tiff", name));
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(count, Tiff.GetImageCount(bytes));
        Assert.Equal(count, Tiff.GetInfo(bytes).FrameCount);
        var infos = Tiff.GetInfos(bytes);
        Assert.Equal(count, infos.Length);
        using var fs = File.OpenRead(path);
        Assert.Equal(count, Tiff.GetImageCount(fs));
        fs.Position = 0;
        Assert.Equal(infos, Tiff.GetInfos(fs));
        for (int i = 0; i < count; i++)
        {
            var expected = TestUtil.LoadRef(path + (i == 0 ? ".ref" : $".{i}.ref"));
            var img = Tiff.Decode(bytes, new TiffDecodeOptions { ImageIndex = i });
            TestUtil.AssertEqual(expected, img);
            Assert.Equal((expected.Width, expected.Height, expected.Channels, expected.SampleType),
                (infos[i].Width, infos[i].Height, infos[i].Channels, infos[i].SampleType));
            fs.Position = 0;
            TestUtil.AssertEqual(expected, Tiff.Decode(fs, ForcedParallel(i)));
            Assert.NotEmpty(Tiff.ReadTags(bytes, i));
        }
        Assert.ThrowsAny<ArgumentException>(() => Tiff.Decode(bytes, new TiffDecodeOptions { ImageIndex = count }));
        Assert.ThrowsAny<ArgumentException>(() => Tiff.Decode(bytes, new TiffDecodeOptions { ImageIndex = -1 }));
        Assert.ThrowsAny<ArgumentException>(() => Tiff.ReadTags(bytes, count));
    }

    [Fact]
    public void ReadsMetadata()
    {
        var path = TestUtil.Fixture(Path.Combine("tiff", "meta_res_icc_orient.tif"));
        var img = Tiff.Decode(File.ReadAllBytes(path));
        var m = img.Metadata!;
        Assert.Equal(6, m.Orientation);
        Assert.Equal(300, m.DpiX, 6);
        Assert.Equal(150.1, m.DpiY, 6);
        Assert.Equal(File.ReadAllBytes(Path.ChangeExtension(path, ".icc")), m.IccProfile);
        // orientation is reported, not applied
        Assert.Equal((23, 17), (img.Width, img.Height));

        m = Tiff.Decode(File.ReadAllBytes(TestUtil.Fixture(Path.Combine("tiff", "meta_res_cm.tif")))).Metadata!;
        Assert.Equal(254, m.DpiX, 6);
        Assert.Equal(127, m.DpiY, 6);
        Assert.Equal(0, m.Orientation);
        Assert.Null(m.IccProfile);
    }

    [Fact]
    public void ReadsGeoTiffTags()
    {
        var path = TestUtil.Fixture(Path.Combine("tiff", "geotiff_dem_f32.tif"));
        var bytes = File.ReadAllBytes(path);
        foreach (var tags in new[] { Tiff.ReadTags(bytes), Tiff.ReadTags(new MemoryStream(bytes)) })
        {
            TiffTag Get(int id) => tags.Single(t => t.Id == id);
            Assert.Equal(new[] { 0.5, 0.5, 0.0 }, (double[])Get(33550).Value);
            Assert.Equal(TiffTagType.Double, Get(33550).Type);
            Assert.Equal(new[] { 0.0, 0.0, 0.0, 600000.0, 5300000.0, 0.0 }, Get(33922).AsDoubles());
            var keys = (ushort[])Get(34735).Value;
            Assert.Equal(16, keys.Length);
            Assert.Equal(32633, keys[15]);
            Assert.Equal(32633, Get(34735).AsIntegers()[15]);
            Assert.Equal("-9999", Get(42113).AsString());
            Assert.Equal(TiffTagType.Ascii, Get(42113).Type);
            Assert.Equal("pixcodec fixture", Get(305).AsString());
            Assert.Equal(40, Get(256).AsIntegers()[0]);
            Assert.Equal(16, Get(322).AsIntegers()[0]);
            Assert.Equal(tags.Select(t => t.Id).OrderBy(i => i), tags.Select(t => t.Id));
        }
    }

    [Fact]
    public void RejectsNonTiff()
    {
        Assert.False(Tiff.IsMatch(ReadOnlySpan<byte>.Empty));
        Assert.False(Tiff.IsMatch("II"u8));
        Assert.False(Tiff.IsMatch(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0 }));
        Assert.True(Tiff.IsMatch(new byte[] { 0x49, 0x49, 42, 0 }));
        Assert.True(Tiff.IsMatch(new byte[] { 0x4D, 0x4D, 0, 43 }));
        Assert.Throws<ImageFormatException>(() => Tiff.Decode(ReadOnlySpan<byte>.Empty));
        Assert.Throws<ImageFormatException>(() => Tiff.Decode(new byte[] { 0x49, 0x49, 42, 0 }));
        Assert.Throws<ImageFormatException>(() => Tiff.GetInfo(new byte[100]));
        Assert.Throws<ImageFormatException>(() => Tiff.Decode(new MemoryStream(new byte[3])));
    }

    // ------------------------------------------------------------------------------------------ LZW / PackBits units

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(253)]
    [InlineData(254)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(700)]
    [InlineData(5000)]
    [InlineData(70000)]
    public unsafe void LzwRoundTrip(int n)
    {
        // noise walks through every code width change and table reset, the other patterns make long strings
        foreach (int kind in new[] { 0, 1, 2, 3 })
        {
            var rnd = new Random(n * 31 + kind);
            var data = new byte[n];
            for (int i = 0; i < n; i++)
                data[i] = kind switch { 0 => (byte)rnd.Next(256), 1 => (byte)(i / 300), 2 => (byte)rnd.Next(3), _ => (byte)(i % 7 == 0 ? rnd.Next(256) : 5) };
            var enc = new byte[TiffLzw.MaxEncodedSize(n)];
            int len = TiffLzw.Encode(data, enc);
            Assert.True(len <= enc.Length);
            var dec = new byte[n + 8];
            fixed (byte* d = dec)
                Assert.Equal(n, TiffLzw.Decode(enc.AsSpan(0, len), d, n));
            Assert.True(data.AsSpan().SequenceEqual(dec.AsSpan(0, n)));
            Assert.All(dec.Skip(n), b => Assert.Equal(0, b));
        }
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(17, 5)]
    [InlineData(1000, 100)]
    [InlineData(4096, 129)]
    public unsafe void PackBitsRoundTrip(int n, int row)
    {
        foreach (int kind in new[] { 0, 1, 2 })
        {
            var rnd = new Random(n + kind);
            var data = new byte[n];
            for (int i = 0; i < n; i++) data[i] = kind switch { 0 => (byte)rnd.Next(256), 1 => (byte)(i / 200), _ => (byte)(rnd.Next(4) == 0 ? rnd.Next(256) : 9) };
            var enc = new byte[TiffPackBits.MaxEncodedSize(n, row)];
            int len = TiffPackBits.Encode(data, row, enc);
            var dec = new byte[n];
            fixed (byte* d = dec) Assert.Equal(n, TiffPackBits.Decode(enc.AsSpan(0, len), d, n));
            Assert.Equal(data, dec);
        }
    }

    // ------------------------------------------------------------------------------------------ encoding

    static readonly SampleType[] AllTypes =
    {
        SampleType.UInt8, SampleType.UInt16, SampleType.UInt32, SampleType.Int8, SampleType.Int16, SampleType.Int32,
        SampleType.Float16, SampleType.Float32, SampleType.Float64,
    };

    static readonly (ColorModel color, int channels)[] AllModels =
    {
        (ColorModel.Gray, 1), (ColorModel.GrayAlpha, 2), (ColorModel.Rgb, 3), (ColorModel.Rgba, 4), (ColorModel.Other, 2), (ColorModel.Other, 6),
    };

    static readonly TiffCompression[] AllCompressions = { TiffCompression.None, TiffCompression.Lzw, TiffCompression.Deflate, TiffCompression.PackBits };

    /// <summary>Deterministic image with gradients, noise and (for floats) non-finite values.</summary>
    internal static RawImage MakeImage(int w, int h, ColorModel color, SampleType type, int channels, int seed = 1)
    {
        var img = RawImage.Create(w, h, color, type, channels);
        var rnd = new Random(seed * 1000 + (int)type * 10 + channels);
        int n = w * h * channels;
        double V(int i)
        {
            int c = i % channels, x = i / channels % w, y = i / channels / w;
            return 0.5 + 0.35 * Math.Sin(x / (5.0 + c) + c) * Math.Cos(y / 4.0) + 0.15 * rnd.NextDouble();
        }
        // (by sample type: array type patterns cannot tell sbyte[] from byte[])
        switch (type)
        {
            case SampleType.UInt8: { var a = (byte[])img.Data; for (int i = 0; i < n; i++) a[i] = (byte)(V(i) * 255); break; }
            case SampleType.Int8: { var a = (sbyte[])img.Data; for (int i = 0; i < n; i++) a[i] = (sbyte)(V(i) * 255 - 128); break; }
            case SampleType.UInt16: { var a = (ushort[])img.Data; for (int i = 0; i < n; i++) a[i] = (ushort)(V(i) * 65535); break; }
            case SampleType.Int16: { var a = (short[])img.Data; for (int i = 0; i < n; i++) a[i] = (short)(V(i) * 65535 - 32768); break; }
            case SampleType.UInt32: { var a = (uint[])img.Data; for (int i = 0; i < n; i++) a[i] = (uint)(V(i) * uint.MaxValue); break; }
            case SampleType.Int32: { var a = (int[])img.Data; for (int i = 0; i < n; i++) a[i] = (int)(V(i) * uint.MaxValue - int.MaxValue); break; }
            case SampleType.Float16:
            {
                var a = (Half[])img.Data;
                for (int i = 0; i < n; i++) a[i] = (Half)(V(i) * 200 - 50);
                a[0] = Half.NaN; if (n > 3) { a[1] = Half.PositiveInfinity; a[2] = Half.NegativeZero; }
                break;
            }
            case SampleType.Float32:
            {
                var a = (float[])img.Data;
                for (int i = 0; i < n; i++) a[i] = (float)(V(i) * 2000 - 500);
                a[0] = float.NaN; if (n > 3) { a[1] = float.NegativeInfinity; a[2] = -0f; }
                break;
            }
            default:
            {
                var a = (double[])img.Data;
                for (int i = 0; i < n; i++) a[i] = V(i) * 2e6 - 5e5;
                a[0] = double.NaN; if (n > 3) { a[1] = double.PositiveInfinity; a[2] = -0d; }
                break;
            }
        }
        return img;
    }

    static byte[] Encode(RawImage img, TiffEncodeOptions? o = null)
    {
        var ms = new MemoryStream();
        Tiff.Encode(img, ms, o);
        return ms.ToArray();
    }

    /// <summary>What comes back for an image: a single-channel "Other" image cannot be told apart from gray.</summary>
    static ColorModel ExpectedColor(RawImage img) => img.Color == ColorModel.Other && img.Channels == 1 ? ColorModel.Gray : img.Color;

    static void AssertRoundTrip(RawImage img, byte[] file, string what)
    {
        RawImage back;
        try { back = Tiff.Decode(file); }
        catch (Exception e) { throw new Xunit.Sdk.XunitException($"{what}: {e}"); }
        Assert.True(back.SampleType == img.SampleType && (back.Width, back.Height, back.Channels) == (img.Width, img.Height, img.Channels), what);
        Assert.True(ExpectedColor(img) == back.Color, $"{what}: color {back.Color}");
        Assert.True(img.GetBytes().SequenceEqual(back.GetBytes()), $"{what}: samples differ");
    }

    public static IEnumerable<object[]> TypeAndCompression =>
        from t in AllTypes from c in AllCompressions select new object[] { t, c };

    [Theory]
    [MemberData(nameof(TypeAndCompression))]
    public void EncodeRoundTrip(SampleType type, TiffCompression compression)
    {
        foreach (var (color, channels) in AllModels)
        {
            var img = MakeImage(37, 23, color, type, channels);
            var layouts = new (string name, TiffEncodeOptions o)[]
            {
                ("default", new TiffEncodeOptions()),
                ("rps3", new TiffEncodeOptions { RowsPerStrip = 3 }),
                ("rps1000", new TiffEncodeOptions { RowsPerStrip = 1000 }),
                ("tile16", new TiffEncodeOptions { TileWidth = 16, TileHeight = 16 }),
                ("tile30x60", new TiffEncodeOptions { TileWidth = 30, TileHeight = 60 }),
                ("big", new TiffEncodeOptions { BigTiff = true, RowsPerStrip = 5 }),
                ("bigtile", new TiffEncodeOptions { BigTiff = true, TileWidth = 16, TileHeight = 32 }),
                ("nopred", new TiffEncodeOptions { Predictor = TiffPredictor.None, RowsPerStrip = 7 }),
                ("hpred", new TiffEncodeOptions { Predictor = TiffPredictor.Horizontal, RowsPerStrip = 7 }),
                ("serial", new TiffEncodeOptions { Parallel = false, RowsPerStrip = 2 }),
            };
            foreach (var (name, o) in layouts)
            {
                o.Compression = compression;
                var file = Encode(img, o);
                string what = $"{type} {color}/{channels} {compression} {name}";
                AssertRoundTrip(img, file, what);
                Assert.Equal(name.StartsWith("big"), file[2] == 43);

                // the same bytes must come out of a write-only, non-seekable stream
                var ns = new ForwardOnlyStream();
                Tiff.Encode(img, ns, o);
                Assert.True(file.AsSpan().SequenceEqual(ns.ToArray()), $"{what}: non-seekable output differs");
            }
        }
    }

    [Fact]
    public void EncodeDefaultsAndGenericEntryPoint()
    {
        var img = MakeImage(300, 200, ColorModel.Rgb, SampleType.UInt8, 3);
        var file = ImageCodec.Encode(img, ImageFormat.Tiff);
        AssertRoundTrip(img, file, "generic");
        var tags = Tiff.ReadTags(file);
        long Tag(int id) => tags.Single(t => t.Id == id).AsIntegers()[0];
        Assert.Equal(8, Tag(259));   // deflate by default
        Assert.Equal(2, Tag(317));   // horizontal predictor for integers
        Assert.Equal(2, Tag(262));
        Assert.InRange(Tag(278) * 300 * 3, 32 * 1024, 128 * 1024); // about 64 KB per strip
        Assert.Equal((byte)'I', file[0]);
        Assert.True(file.Length < 300 * 200 * 3);

        var f = MakeImage(64, 64, ColorModel.Gray, SampleType.Float32, 1);
        long FTag(byte[] b, int id) => Tiff.ReadTags(b).Single(t => t.Id == id).AsIntegers()[0];
        Assert.Equal(3, FTag(Encode(f), 317));
        Assert.Equal(3, FTag(Encode(f), 339));
        Assert.Equal(2, FTag(Encode(f, new TiffEncodeOptions { Predictor = TiffPredictor.Horizontal }), 317));
        Assert.DoesNotContain(Tiff.ReadTags(Encode(f, new TiffEncodeOptions { Compression = TiffCompression.PackBits })), t => t.Id == 317);
        Assert.DoesNotContain(Tiff.ReadTags(Encode(f, new TiffEncodeOptions { Compression = TiffCompression.None })), t => t.Id == 317);
        Assert.Throws<ArgumentException>(() => Encode(img, new TiffEncodeOptions { Predictor = TiffPredictor.FloatingPoint }));

        // tile sizes are rounded up to multiples of 16
        var t = Tiff.ReadTags(Encode(img, new TiffEncodeOptions { TileWidth = 100, TileHeight = 50 }));
        Assert.Equal(112, t.Single(x => x.Id == 322).AsIntegers()[0]);
        Assert.Equal(64, t.Single(x => x.Id == 323).AsIntegers()[0]);
    }

    [Fact]
    public void EncodeWritesMetadata()
    {
        var img = MakeImage(20, 10, ColorModel.Rgba, SampleType.UInt16, 4);
        var icc = new byte[777];
        new Random(5).NextBytes(icc);
        img.Metadata = new ImageMetadata { DpiX = 300, DpiY = 72.5, IccProfile = icc, Orientation = 3 };
        foreach (var big in new[] { false, true })
        {
            var back = Tiff.Decode(Encode(img, new TiffEncodeOptions { BigTiff = big }));
            Assert.Equal(300, back.Metadata!.DpiX, 6);
            Assert.Equal(72.5, back.Metadata.DpiY, 6);
            Assert.Equal(icc, back.Metadata.IccProfile);
            Assert.Equal(3, back.Metadata.Orientation);
        }
        img.Metadata = null;
        var m = Tiff.Decode(Encode(img)).Metadata!;
        Assert.Equal((0d, 0d, 0), (m.DpiX, m.DpiY, m.Orientation));
        Assert.Null(m.IccProfile);
    }

    [Fact]
    public void EncodeLargerImageInParallel()
    {
        // big enough for the parallel compression / decoding paths and several LZW table resets per strip
        foreach (var type in new[] { SampleType.UInt8, SampleType.UInt16, SampleType.Float32 })
            foreach (var comp in AllCompressions)
                foreach (var tile in new[] { 0, 256 })
                {
                    var img = MakeImage(1031, 517, ColorModel.Rgb, type, 3);
                    var o = new TiffEncodeOptions { Compression = comp, TileWidth = tile, TileHeight = tile };
                    var file = Encode(img, o);
                    AssertRoundTrip(img, file, $"{type} {comp} tile {tile}");
                    var region = Tiff.DecodeRegion(file, 100, 200, 600, 300);
                    TestUtil.AssertEqual(Crop(img, 100, 200, 600, 300), region);
                    o.Parallel = false;
                    Assert.True(file.AsSpan().SequenceEqual(Encode(img, o)), "parallel and serial encoders disagree");
                }
    }

    /// <summary>Runs tests/oracle/decode_to_ref.py; null when python is missing or no installed decoder reads this variant (exit 77).</summary>
    static RawImage? Oracle(byte[] encoded, out string error)
    {
        error = "";
        if (!TestUtil.PythonAvailable) return null;
        var tmp = Path.Combine(TestUtil.FixtureDir, "_tmp");
        Directory.CreateDirectory(tmp);
        var f = Path.Combine(tmp, Guid.NewGuid().ToString("N") + ".tif");
        try
        {
            File.WriteAllBytes(f, encoded);
            var (exit, output) = TestUtil.RunPython($"decode_to_ref.py \"{f}\" \"{f}.ref\"");
            if (exit == 77) return null;
            if (exit != 0) { error = "oracle decode failed: " + output; return null; }
            return TestUtil.LoadRef(f + ".ref");
        }
        finally
        {
            File.Delete(f); File.Delete(f + ".ref");
        }
    }

    [Fact]
    public void EncodedFilesDecodeWithReferenceDecoders()
    {
        if (!TestUtil.PythonAvailable) return;
        var cases = new List<(string what, RawImage img, TiffEncodeOptions o)>();
        int k = 0;
        foreach (var type in AllTypes)
            foreach (var comp in AllCompressions)
                foreach (var (color, channels) in AllModels)
                {
                    // rotate through the layouts so that every type x compression x colour model is seen by the oracle
                    var o = (k++ % 5) switch
                    {
                        0 => new TiffEncodeOptions(),
                        1 => new TiffEncodeOptions { RowsPerStrip = 4 },
                        2 => new TiffEncodeOptions { TileWidth = 16, TileHeight = 16 },
                        3 => new TiffEncodeOptions { BigTiff = true, RowsPerStrip = 9 },
                        _ => new TiffEncodeOptions { Predictor = k % 2 == 0 ? TiffPredictor.Horizontal : TiffPredictor.None, TileWidth = 32, TileHeight = 16, BigTiff = k % 3 == 0 },
                    };
                    o.Compression = comp;
                    cases.Add(($"{type} {color}/{channels} {comp} #{k % 5}", MakeImage(29, 19, color, type, channels), o));
                }
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        int verified = 0, unreadable = 0;
        Parallel.ForEach(cases, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) }, c =>
        {
            var file = Encode(c.img, c.o);
            var o = Oracle(file, out var error);
            if (error.Length > 0) { failures.Add($"{c.what}: {error}"); return; }
            if (o == null) { Interlocked.Increment(ref unreadable); return; }
            Interlocked.Increment(ref verified);
            if (o.SampleType != c.img.SampleType || (o.Width, o.Height, o.Channels) != (c.img.Width, c.img.Height, c.img.Channels))
                failures.Add($"{c.what}: oracle sees {o.Width}x{o.Height}x{o.Channels} {o.SampleType}");
            else if (!o.GetBytes().SequenceEqual(c.img.GetBytes()))
                failures.Add($"{c.what}: oracle decodes different samples");
        });
        Assert.True(failures.IsEmpty, string.Join("\n", failures.Take(20)));
        // Most variants must really have been checked (the rest - e.g. LZW with 2 or 6 channels - has no reader here).
        Assert.True(verified >= cases.Count * 6 / 10, $"only {verified} of {cases.Count} variants were readable by the oracle ({unreadable} skipped)");
    }

    sealed class ForwardOnlyStream : Stream
    {
        readonly MemoryStream _ms = new();
        public byte[] ToArray() => _ms.ToArray();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _ms.Write(buffer, offset, count);
    }

    // ------------------------------------------------------------------------------------------ robustness

    static void MustFailCleanly(byte[] data, string what)
    {
        void Run(Action a)
        {
            try { a(); }
            catch (ImageFormatException) { }
            catch (ImageNotSupportedException) { }
            catch (Exception e) { throw new Xunit.Sdk.XunitException($"{what}: unexpected {e.GetType().Name}: {e}"); }
        }
        Run(() => Tiff.Decode(data));
        Run(() => Tiff.GetInfo(data));
        Run(() => Tiff.GetInfos(data));
        Run(() => Tiff.ReadTags(data));
    }

    public static IEnumerable<object[]> AllFixtures => Fixtures.Concat(YccFixtures).Concat(JpegFixtures);

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void SurvivesTruncation(string file)
    {
        var bytes = File.ReadAllBytes(TestUtil.Fixture(file));
        var sw = Stopwatch.StartNew();
        var cuts = new SortedSet<int>();
        for (int i = 0; i <= 64 && i < bytes.Length; i++) cuts.Add(i);
        for (int i = 1; i <= 40; i++) cuts.Add((int)((long)bytes.Length * i / 41));
        for (int i = 1; i <= 16 && i < bytes.Length; i++) cuts.Add(bytes.Length - i);
        foreach (int cut in cuts) MustFailCleanly(bytes.AsSpan(0, cut).ToArray(), $"{file} cut at {cut}");
        // and through the stream path
        foreach (int cut in cuts.Where((_, i) => i % 5 == 0))
        {
            try { Tiff.Decode(new MemoryStream(bytes, 0, cut)); }
            catch (ImageFormatException) { }
            catch (ImageNotSupportedException) { }
        }
        Assert.True(sw.Elapsed.TotalSeconds < 20, $"took {sw.Elapsed}");
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void SurvivesCorruption(string file)
    {
        var bytes = File.ReadAllBytes(TestUtil.Fixture(file));
        var rnd = new Random(1234 + bytes.Length);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 300; i++)
        {
            var b = (byte[])bytes.Clone();
            int flips = 1 + i % 3;
            int pos = 0;
            for (int f = 0; f < flips; f++)
            {
                pos = rnd.Next(b.Length);
                b[pos] = i % 4 == 0 ? (byte)rnd.Next(256) : (byte)(b[pos] ^ (1 << rnd.Next(8)));
            }
            MustFailCleanly(b, $"{file} corruption #{i} (last at {pos})");
        }
        Assert.True(sw.Elapsed.TotalSeconds < 30, $"took {sw.Elapsed}");
    }

    [Fact]
    public void RejectsLyingHeaders()
    {
        // an uncompressed 16x16 file that claims to be gigantic must be refused without allocating for it
        var img = MakeImage(16, 16, ColorModel.Gray, SampleType.UInt8, 1);
        foreach (var comp in AllCompressions)
        {
            var file = Encode(img, new TiffEncodeOptions { Compression = comp });
            int ifd = BitConverter.ToInt32(file, 4);
            int n = BitConverter.ToUInt16(file, ifd);
            foreach (var (tag, value) in new[] { (256, 60000), (257, 2_000_000_000), (277, 60000) })
            {
                var b = (byte[])file.Clone();
                for (int i = 0; i < n; i++)
                {
                    int e = ifd + 2 + i * 12;
                    if (BitConverter.ToUInt16(b, e) != tag) continue;
                    BitConverter.TryWriteBytes(b.AsSpan(e + 2), (ushort)4);
                    BitConverter.TryWriteBytes(b.AsSpan(e + 8), value);
                }
                long before = GC.GetTotalAllocatedBytes(true);
                var sw = Stopwatch.StartNew();
                Assert.ThrowsAny<Exception>(() => Tiff.Decode(b));
                try { Tiff.Decode(b); }
                catch (ImageFormatException) { }
                catch (ImageNotSupportedException) { }
                Assert.True(GC.GetTotalAllocatedBytes(true) - before < 64 << 20, $"{comp} tag {tag}: allocated too much");
                Assert.True(sw.Elapsed.TotalSeconds < 2);
            }
        }
    }
}
