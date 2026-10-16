using System.Text;
using Xunit;
using static Aardvark.ImageCodecs.Tests.TestUtil;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>PCX / DCX, SGI, Sun Raster, XBM, XPM and WBMP: fixtures, header information, round trips, oracles, robustness.</summary>
public class LegacyFormatTests
{
    public static IEnumerable<object[]> Fixtures()
        => Files("pcx", ".pcx", ".dcx").Concat(Files("sgi", ".sgi", ".rgb", ".rgba", ".bw")).Concat(Files("sun", ".ras"))
           .Concat(Files("xbm", ".xbm")).Concat(Files("xpm", ".xpm")).Concat(Files("wbmp", ".wbmp"));

    static RawImage DecodeAs(ImageFormat f, ReadOnlySpan<byte> d) => f switch
    {
        ImageFormat.Pcx or ImageFormat.Dcx => Pcx.Decode(d),
        ImageFormat.Sgi => Sgi.Decode(d),
        ImageFormat.SunRaster => SunRaster.Decode(d),
        ImageFormat.Xbm => Xbm.Decode(d),
        ImageFormat.Xpm => Xpm.Decode(d),
        ImageFormat.Wbmp => Wbmp.Decode(d),
        _ => throw new ArgumentException(f.ToString()),
    };

    static ImageInfo InfoAs(ImageFormat f, ReadOnlySpan<byte> d) => f switch
    {
        ImageFormat.Pcx or ImageFormat.Dcx => Pcx.GetInfo(d),
        ImageFormat.Sgi => Sgi.GetInfo(d),
        ImageFormat.SunRaster => SunRaster.GetInfo(d),
        ImageFormat.Xbm => Xbm.GetInfo(d),
        ImageFormat.Xpm => Xpm.GetInfo(d),
        ImageFormat.Wbmp => Wbmp.GetInfo(d),
        _ => throw new ArgumentException(f.ToString()),
    };

    static readonly ImageFormat[] Legacy = { ImageFormat.Pcx, ImageFormat.Dcx, ImageFormat.Sgi, ImageFormat.SunRaster, ImageFormat.Xbm, ImageFormat.Xpm, ImageFormat.Wbmp };

    [Theory, MemberData(nameof(Fixtures))]
    public void DecodeMatchesReference(string file)
    {
        var format = ImageCodec.FormatFromExtension(file);
        var data = File.ReadAllBytes(Fixture(file));
        var img = DecodeAs(format, data);
        AssertEqual(LoadRef(Fixture(file) + ".ref"), img);
        Assert.Equal(RawImage.DefaultColorModel(img.Channels), img.Color);

        var info = InfoAs(format, data);
        Assert.Equal(format, info.Format);
        Assert.Equal((img.Width, img.Height, img.Channels, img.SampleType, img.Color), (info.Width, info.Height, info.Channels, info.SampleType, info.Color));
        var m = img.Metadata ?? new ImageMetadata();
        Assert.Equal((m.DpiX, m.DpiY), (info.DpiX, info.DpiY));
        Assert.Equal(img.Color == ColorModel.Other ? TransferFunction.Unknown : TransferFunction.Srgb, info.Transfer);
        // a truncated WBMP is not recognized by content (its length must match the header)
        if (format == ImageFormat.Wbmp && file.Contains("truncated"))
        {
            Assert.Equal(ImageFormat.Unknown, ImageCodec.DetectFormat(data));
            return;
        }
        Assert.Equal(format, ImageCodec.DetectFormat(data));
        Assert.Equal(info, ImageCodec.GetInfo(data));
        Assert.Equal(info, ImageCodec.GetInfo(new MemoryStream(data)));
        Assert.Equal(info, ImageCodec.GetInfo(new NonSeekable(data)));
        AssertEqual(img, ImageCodec.Decode(data));

        if (format == ImageFormat.Dcx)
        {
            var pages = ImageCodec.DecodeAll(data);
            Assert.Equal(info.FrameCount, pages.Length);
            AssertEqual(img, pages[0]);
            for (int i = 1; i < pages.Length; i++) AssertEqual(LoadRef(Fixture(file) + $".p{i}.ref"), pages[i]);
        }
        else Assert.Single(ImageCodec.DecodeAll(data));
    }

    /// <summary>Damaged files decode or fail with the library's exceptions, never anything else.</summary>
    [Theory, MemberData(nameof(Fixtures))]
    public void DamagedFilesFailCleanly(string file)
    {
        var format = ImageCodec.FormatFromExtension(file);
        var data = File.ReadAllBytes(Fixture(file));
        var rnd = new Random(file.Length * 31 + data.Length);
        for (int k = 0; k < 80; k++)
        {
            var d = k < 25 ? data.AsSpan(0, rnd.Next(data.Length)).ToArray() : (byte[])data.Clone();
            if (k >= 25)
                for (int j = 0; j < 1 + k % 5; j++) d[rnd.Next(Math.Min(d.Length, k < 50 ? 140 : d.Length))] = (byte)rnd.Next(256);
            try { DecodeAs(format, d); }
            catch (ImageFormatException) { }
            catch (ImageNotSupportedException) { }
            try { InfoAs(format, d); }
            catch (ImageFormatException) { }
            catch (ImageNotSupportedException) { }
            try { ImageCodec.Decode(d); }
            catch (ImageFormatException) { }
            catch (ImageNotSupportedException) { }
        }
    }

    [Fact]
    public void HugeHeadersWithoutDataAreRejected()
    {
        // 12000 x 12000 (more than MaxUnbackedPixels, less than MaxPixels) announced by tiny files
        var pcx = new byte[200];
        pcx[0] = 10; pcx[1] = 5; pcx[2] = 1; pcx[3] = 8; pcx[65] = 3;
        BitConverter.GetBytes((ushort)11999).CopyTo(pcx, 8);
        BitConverter.GetBytes((ushort)11999).CopyTo(pcx, 10);
        BitConverter.GetBytes((ushort)12000).CopyTo(pcx, 66);
        Assert.Throws<ImageFormatException>(() => Pcx.Decode(pcx));
        var sgi = Sgi.Encode(RawImage.Create(2, 2, ColorModel.Rgb, SampleType.UInt8));
        sgi[6] = sgi[8] = 0x2E; sgi[7] = sgi[9] = 0xE0;
        Assert.Throws<ImageFormatException>(() => Sgi.Decode(sgi));
        var ras = SunRaster.Encode(RawImage.Create(2, 2, ColorModel.Rgb, SampleType.UInt8));
        ras[6] = ras[10] = 0x2E; ras[7] = ras[11] = 0xE0;
        Assert.Throws<ImageFormatException>(() => SunRaster.Decode(ras));
        var wbmp = new byte[] { 0, 0, 0xDD, 0x60, 0xDD, 0x60, 0, 0 };
        Assert.Throws<ImageFormatException>(() => Wbmp.Decode(wbmp));
        var xpm = Encoding.ASCII.GetBytes("/* XPM */\nstatic char *a[] = {\"12000 12000 1 1\", \"a c red\", \"aaa\"};");
        Assert.Throws<ImageFormatException>(() => Xpm.Decode(xpm));
        var xbm = Encoding.ASCII.GetBytes("#define a_width 12000\n#define a_height 12000\nstatic char a_bits[] = { 0x00 };");
        Assert.Throws<ImageFormatException>(() => Xbm.Decode(xbm));
        // and beyond MaxPixels
        BitConverter.GetBytes((ushort)29999).CopyTo(pcx, 8);
        BitConverter.GetBytes((ushort)29999).CopyTo(pcx, 10);
        BitConverter.GetBytes((ushort)30000).CopyTo(pcx, 66);
        Assert.Throws<ImageNotSupportedException>(() => Pcx.Decode(pcx));
    }

    [Fact]
    public void UnsupportedVariantsAreReported()
    {
        var sgi = Sgi.Encode(RawImage.Create(2, 2, ColorModel.Gray, SampleType.UInt8));
        sgi[107] = 2;   // SCREEN
        Assert.Throws<ImageNotSupportedException>(() => Sgi.Decode(sgi));
        sgi[107] = 3;   // a colour map file
        Assert.Throws<ImageNotSupportedException>(() => Sgi.GetInfo(sgi));
        var ras = SunRaster.Encode(RawImage.Create(2, 2, ColorModel.Rgb, SampleType.UInt8));
        ras[23] = 4;    // RT_FORMAT_TIFF
        Assert.Throws<ImageNotSupportedException>(() => SunRaster.Decode(ras));
        var xpm = Encoding.ASCII.GetBytes("/* XPM */\nstatic char *a[] = {\"1 1 1 9\", \"aaaaaaaaa c red\", \"aaaaaaaaa\"};");
        Assert.Throws<ImageNotSupportedException>(() => Xpm.Decode(xpm));
        // a colour nobody knows, with no other visual
        var bogus = Encoding.ASCII.GetBytes("/* XPM */\nstatic char *a[] = {\"1 1 1 1\", \"a c Bogus\", \"a\"};");
        Assert.Throws<ImageFormatException>(() => Xpm.Decode(bogus));
        var mono = Encoding.ASCII.GetBytes("/* XPM */\nstatic char *a[] = {\"1 1 1 1\", \"a c Bogus m white\", \"a\"};");
        Assert.Equal(new byte[] { 255 }, Xpm.Decode(mono).GetData<byte>());
    }

    // ------------------------------------------------------------------------------------------------ detection

    [Fact]
    public void OtherFormatsAreNotTaken()
    {
        foreach (var dir in new[] { "png", "jpeg", "gif", "webp", "tiff", "exr", "bmp", "tga", "pnm", "qoi", "ico", "hdr", "dds", "ktx", "jp2" })
            foreach (var f in Directory.GetFiles(Path.Combine(FixtureDir, dir)).Where(f => !f.EndsWith(".ref") && !f.EndsWith(".json")).Take(40))
                Assert.DoesNotContain(ImageCodec.DetectFormat(File.ReadAllBytes(f)), Legacy);
    }

    [Theory]
    [InlineData("#define FOO 1\nint x;")]
    [InlineData("#define a_width 3\nint x;")]
    [InlineData("#define a_height 3\n#define a_width 3\n")]
    [InlineData("/* XPM is great */ static char *x[] = {};")]
    [InlineData("! XPM2x\n1 1 1 1\n")]
    [InlineData("hello world")]
    [InlineData("\0\0\x05\x01\xff\xff")]          // WBMP header, but too much data
    [InlineData("\0\0\x80\x05\x01\x00")]           // not minimal
    [InlineData("\0\0\x05\x01")]                   // no pixel data
    public void TextAndWbmpDetectionIsStrict(string text)
        => Assert.Equal(ImageFormat.Unknown, ImageCodec.DetectFormat(Encoding.Latin1.GetBytes(text)));

    [Fact]
    public void WbmpIsDetectedFromStreams()
    {
        // larger than the first read of the stream GetInfo path
        var img = Pattern(400, 300, ColorModel.Gray, 3, bilevel: true);
        var w = Wbmp.Encode(img);
        Assert.True(w.Length > 8192);
        var info = ImageCodec.GetInfo(new NonSeekable(w));
        Assert.Equal((ImageFormat.Wbmp, 400, 300), (info.Format, info.Width, info.Height));
        Assert.Equal(info, ImageCodec.GetInfo(new MemoryStream(w)));
        Assert.Throws<ImageFormatException>(() => ImageCodec.GetInfo(new NonSeekable(new byte[20000])));
    }

    [Fact]
    public void XbmHotSpot()
    {
        var img = Pattern(10, 4, ColorModel.Gray, 1, bilevel: true);
        var x = Xbm.Encode(img, new XbmEncodeOptions { Name = "my cursor", HotSpot = (3, 2) });
        Assert.Contains("#define my_cursor_x_hot 3", Encoding.ASCII.GetString(x));
        Assert.Equal((3, 2), Xbm.GetHotSpot(x));
        Assert.Null(Xbm.GetHotSpot(Xbm.Encode(img)));
        AssertEqual(img, Xbm.Decode(x));
    }

    // ------------------------------------------------------------------------------------------------ encoding

    /// <summary>A test image: noise, runs, and (with alpha) some transparent pixels, never all.</summary>
    static RawImage Pattern(int w, int h, ColorModel color, int seed, SampleType type = SampleType.UInt8, int channels = 0,
        bool bilevel = false, int colors = 0, bool binaryAlpha = false)
    {
        var img = RawImage.Create(w, h, color, type, channels);
        var rnd = new Random(seed);
        int ch = img.Channels;
        if (type == SampleType.UInt16)
        {
            var s = img.GetData<ushort>();
            for (int i = 0; i < s.Length; i++) s[i] = (ushort)rnd.Next(65536);
            s.AsSpan(0, Math.Min(s.Length, w * ch * 2)).Fill(4321);
            return img;
        }
        var b = img.GetData<byte>();
        var palette = new byte[Math.Max(colors, 1) * 4];
        rnd.NextBytes(palette);
        for (int p = 0; p < w * h; p++)
        {
            int k = colors > 0 ? rnd.Next(colors) : -1;
            for (int c = 0; c < ch; c++)
                b[p * ch + c] = bilevel ? (byte)(rnd.Next(2) * 255) : k >= 0 ? palette[k * 4 + c] : (byte)((p * 7 + p / 13 + c * 50) % 251 ^ rnd.Next(4));
        }
        // a run: the first half row repeats the first pixel
        for (int p = 1; p < w / 2; p++) b.AsSpan(0, ch).CopyTo(b.AsSpan(p * ch));
        if (img.HasAlpha)
            for (int p = 0; p < w * h; p++)
            {
                ref byte a = ref b[p * ch + ch - 1];
                if (binaryAlpha) a = (byte)(p % 3 == 0 ? 0 : 255);
                else a = p % 5 == 1 ? (byte)0 : Math.Max(a, (byte)1);
            }
        if (binaryAlpha)
            for (int p = 0; p < w * h; p++)
                if (b[p * ch + ch - 1] == 0) b.AsSpan(p * ch, ch).Clear();   // XPM None decodes as 0, 0, 0, 0
        return img;
    }

    static RawImage ToRgba(RawImage img)
    {
        int n = img.Width * img.Height, ch = img.Channels;
        bool gray = img.Color is ColorModel.Gray or ColorModel.GrayAlpha;
        if (img.SampleType == SampleType.UInt16)
        {
            var s = img.GetData<ushort>();
            var d = new ushort[n * 4];
            for (int i = 0; i < n; i++)
                for (int c = 0; c < 4; c++)
                    d[i * 4 + c] = c == 3 ? (img.HasAlpha ? s[i * ch + ch - 1] : (ushort)65535) : s[i * ch + (gray ? 0 : c)];
            return new RawImage(img.Width, img.Height, ColorModel.Rgba, d);
        }
        var sb = img.GetData<byte>();
        var db = new byte[n * 4];
        for (int i = 0; i < n; i++)
            for (int c = 0; c < 4; c++)
                db[i * 4 + c] = c == 3 ? (img.HasAlpha ? sb[i * ch + ch - 1] : (byte)255) : sb[i * ch + (gray ? 0 : c)];
        return new RawImage(img.Width, img.Height, ColorModel.Rgba, db);
    }

    /// <summary>Our image as RGBA against the reference tool's decode (tests/oracle/decode_to_ref.py), null when it cannot tell.</summary>
    static void CheckOracle(RawImage expected, byte[] encoded, string ext)
    {
        if (!PythonAvailable) return;
        var tmp = Path.Combine(FixtureDir, "_tmp");
        Directory.CreateDirectory(tmp);
        var f = Path.Combine(tmp, Guid.NewGuid().ToString("N") + ext);
        try
        {
            File.WriteAllBytes(f, encoded);
            var (exit, output) = RunPython($"decode_to_ref.py \"{f}\" \"{f}.ref\"");
            if (TestUtil.CannotVerify(exit, output)) return;
            Assert.True(exit == 0, "oracle decode failed: " + output);
            AssertEqual(ToRgba(expected), LoadRef(f + ".ref"));
        }
        finally { File.Delete(f); File.Delete(f + ".ref"); }
    }

    static readonly (int w, int h)[] Sizes = { (1, 1), (3, 2), (31, 17), (130, 5) };

    public static IEnumerable<object[]> PcxCases()
    {
        foreach (var (w, h) in Sizes)
        {
            yield return new object[] { "gray", w, h };
            yield return new object[] { "bilevel", w, h };
            yield return new object[] { "pal", w, h };
            yield return new object[] { "rgb", w, h };
            yield return new object[] { "rgba", w, h };
            yield return new object[] { "graya", w, h };
        }
    }

    [Theory, MemberData(nameof(PcxCases))]
    public void PcxRoundTrip(string kind, int w, int h)
    {
        var img = kind switch
        {
            "gray" => Pattern(w, h, ColorModel.Gray, w + h),
            "bilevel" => Pattern(w, h, ColorModel.Gray, w + h, bilevel: true),
            "pal" => Pattern(w, h, ColorModel.Rgb, w + h, colors: 200),
            "rgb" => Pattern(w, h, ColorModel.Rgb, w + h),
            "rgba" => Pattern(w, h, ColorModel.Rgba, w + h),
            _ => Pattern(w, h, ColorModel.GrayAlpha, w + h),
        };
        img.Metadata = new ImageMetadata { DpiX = 300, DpiY = 150 };
        var bytes = Pcx.Encode(img);
        var back = Pcx.Decode(bytes);
        var expected = kind == "graya" ? ToRgba(img) : img;
        // Auto picks the smallest lossless form; 'rgb' with few pixels may be written as 8 bit too
        AssertEqual(expected, back);
        Assert.Equal(expected.Color, back.Color);
        Assert.Equal((300.0, 150.0), (back.Metadata!.DpiX, back.Metadata.DpiY));
        int bits = bytes[3], planes = bytes[65];
        bool binary = img.GetData<byte>().AsSpan().IndexOfAnyExcept((byte)0, (byte)255) < 0;
        Assert.Equal(kind switch { "bilevel" => (1, 1), "gray" => binary ? (1, 1) : (8, 1), "pal" => (8, 1), "rgba" or "graya" => (8, 4), _ => (bits, planes) }, (bits, planes));
        Assert.Equal(ImageFormat.Pcx, ImageCodec.DetectFormat(bytes));
        CheckOracle(back, bytes, ".pcx");
    }

    [Fact]
    public void PcxModes()
    {
        var rgb = Pattern(40, 30, ColorModel.Rgb, 5);
        // more than 256 colours: quantized to the palette
        var pal = Pcx.Encode(rgb, new PcxEncodeOptions { Mode = PcxColorMode.Palette });
        Assert.Equal((8, 1), (pal[3], pal[65]));
        var q = Pcx.Decode(pal);
        Assert.Equal(ColorModel.Rgb, q.Color);
        Assert.True(Psnr(rgb, q, 255) > 25);
        CheckOracle(q, pal, ".pcx");
        var tc = Pcx.Encode(rgb, new PcxEncodeOptions { Mode = PcxColorMode.TrueColor });
        Assert.Equal((8, 3), (tc[3], tc[65]));
        AssertEqual(rgb, Pcx.Decode(tc));
        var rgba = Pattern(40, 30, ColorModel.Rgba, 5);
        AssertEqual(StripAlpha(rgba), Pcx.Decode(Pcx.Encode(rgba, new PcxEncodeOptions { Mode = PcxColorMode.TrueColor })));
        var bw = Pcx.Decode(Pcx.Encode(rgb, new PcxEncodeOptions { Mode = PcxColorMode.Bilevel }));
        Assert.Equal(ColorModel.Gray, bw.Color);
        Assert.True(bw.GetData<byte>().All(v => v is 0 or 255));
        Assert.Throws<ImageNotSupportedException>(() => Pcx.Encode(RawImage.Create(2, 2, ColorModel.Rgb, SampleType.UInt16)));
        Assert.Throws<ImageNotSupportedException>(() => Pcx.Encode(RawImage.Create(2, 2, ColorModel.Other, SampleType.UInt8, 5)));
        Assert.Throws<ImageNotSupportedException>(() => Pcx.Encode(RawImage.Create(70000, 1, ColorModel.Gray, SampleType.UInt8)));
    }

    static RawImage StripAlpha(RawImage img)
    {
        var s = img.GetData<byte>();
        int n = img.Width * img.Height;
        var d = new byte[n * 3];
        for (int i = 0; i < n; i++) { d[i * 3] = s[i * 4]; d[i * 3 + 1] = s[i * 4 + 1]; d[i * 3 + 2] = s[i * 4 + 2]; }
        return new RawImage(img.Width, img.Height, ColorModel.Rgb, d);
    }

    [Fact]
    public void DcxPages()
    {
        var pages = new[] { Pattern(31, 17, ColorModel.Rgb, 1), Pattern(9, 4, ColorModel.Gray, 2), Pattern(5, 5, ColorModel.Gray, 3, bilevel: true), Pattern(12, 3, ColorModel.Rgba, 4) };
        var dcx = Pcx.EncodeDcx(pages);
        Assert.Equal(ImageFormat.Dcx, ImageCodec.DetectFormat(dcx));
        var info = ImageCodec.GetInfo(dcx);
        Assert.Equal((ImageFormat.Dcx, 4, 31, 17), (info.Format, info.FrameCount, info.Width, info.Height));
        var all = ImageCodec.DecodeAll(dcx);
        Assert.Equal(4, all.Length);
        for (int i = 0; i < 4; i++) AssertEqual(pages[i], all[i]);
        AssertEqual(pages[0], ImageCodec.Decode(dcx));
        if (PythonAvailable)
        {
            // ImageMagick's view of every page (its first page through decode_to_ref)
            CheckOracle(pages[0], dcx, ".dcx");
        }
        // through the format independent encoder: one page
        var one = ImageCodec.Encode(pages[1], ImageFormat.Dcx);
        Assert.Single(ImageCodec.DecodeAll(one));
        Assert.Throws<ArgumentException>(() => Pcx.EncodeDcx(Array.Empty<RawImage>()));
    }

    public static IEnumerable<object[]> SgiCases()
    {
        foreach (var (w, h) in Sizes)
            foreach (var t in new[] { SampleType.UInt8, SampleType.UInt16 })
                foreach (var ch in new[] { 1, 2, 3, 4, 5 })
                    foreach (bool rle in new[] { false, true })
                        yield return new object[] { w, h, t, ch, rle };
    }

    [Theory, MemberData(nameof(SgiCases))]
    public void SgiRoundTrip(int w, int h, SampleType type, int channels, bool rle)
    {
        var img = Pattern(w, h, RawImage.DefaultColorModel(channels), w * 7 + h, type, channels);
        var bytes = Sgi.Encode(img, new SgiEncodeOptions { Rle = rle, Name = "pixcodec" });
        var back = Sgi.Decode(bytes);
        AssertEqual(img, back);
        Assert.Equal(img.Color, back.Color);
        Assert.Equal(ImageFormat.Sgi, ImageCodec.DetectFormat(bytes));
        if (channels <= 4) CheckOracle(img, bytes, ".sgi");
    }

    public static IEnumerable<object[]> SunCases()
    {
        foreach (var (w, h) in Sizes)
            foreach (var kind in new[] { "gray", "bilevel", "rgb", "rgba", "graya", "pal" })
                foreach (bool rle in new[] { false, true })
                    yield return new object[] { kind, w, h, rle };
    }

    [Theory, MemberData(nameof(SunCases))]
    public void SunRoundTrip(string kind, int w, int h, bool rle)
    {
        var (img, opt) = kind switch
        {
            "gray" => (Pattern(w, h, ColorModel.Gray, w + h), new SunRasterEncodeOptions()),
            "bilevel" => (Pattern(w, h, ColorModel.Gray, w + h, bilevel: true), new SunRasterEncodeOptions { Bilevel = true }),
            "rgb" => (Pattern(w, h, ColorModel.Rgb, w + h), new SunRasterEncodeOptions { RgbOrder = w % 2 == 1 }),
            "rgba" => (Pattern(w, h, ColorModel.Rgba, w + h), new SunRasterEncodeOptions()),
            "graya" => (Pattern(w, h, ColorModel.GrayAlpha, w + h), new SunRasterEncodeOptions()),
            _ => (Pattern(w, h, ColorModel.Rgb, w + h, colors: 100), new SunRasterEncodeOptions { Palette = true }),
        };
        opt.Rle = rle;
        var bytes = SunRaster.Encode(img, opt);
        var back = SunRaster.Decode(bytes);
        var expected = kind == "graya" ? ToRgba(img) : img;
        AssertEqual(expected, back);
        Assert.Equal(expected.Color, back.Color);
        Assert.Equal(ImageFormat.SunRaster, ImageCodec.DetectFormat(bytes));
        CheckOracle(back, bytes, ".ras");
    }

    [Theory]
    [InlineData(false), InlineData(true)]
    public void XbmRoundTrip(bool x10)
    {
        foreach (var (w, h) in Sizes.Append((16, 3)).Append((17, 2)))
        {
            var img = Pattern(w, h, ColorModel.Gray, w + h, bilevel: true);
            var bytes = Xbm.Encode(img, new XbmEncodeOptions { X10 = x10, Name = "9 lives" });
            AssertEqual(img, Xbm.Decode(bytes));
            Assert.Equal(ImageFormat.Xbm, ImageCodec.DetectFormat(bytes));
            CheckOracle(img, bytes, ".xbm");
        }
        // colour input is thresholded on luma, transparent pixels are white
        var rgba = new RawImage(3, 1, ColorModel.Rgba, new byte[] { 255, 255, 255, 255, 10, 20, 30, 255, 0, 0, 0, 0 });
        Assert.Equal(new byte[] { 255, 0, 255 }, Xbm.Decode(Xbm.Encode(rgba)).GetData<byte>());
    }

    public static IEnumerable<object[]> XpmCases()
    {
        foreach (var (w, h) in Sizes)
            foreach (var c in new[] { ColorModel.Gray, ColorModel.GrayAlpha, ColorModel.Rgb, ColorModel.Rgba })
                foreach (int colors in new[] { 0, 7 })
                    yield return new object[] { c, w, h, colors };
    }

    [Theory, MemberData(nameof(XpmCases))]
    public void XpmRoundTrip(ColorModel color, int w, int h, int colors)
    {
        var img = Pattern(w, h, color, w + h, colors: colors, binaryAlpha: true);
        var bytes = Xpm.Encode(img, new XpmEncodeOptions { Name = "x-y" });
        var back = Xpm.Decode(bytes);
        // alpha 0 / 255 only: images whose pixels are all opaque come back without alpha
        var expected = img.HasAlpha && !HasTransparent(img) ? StripAlphaAny(img) : img;
        AssertEqual(ToRgba(expected), ToRgba(back));
        // (an image that is all transparent has only gray colours: GrayAlpha)
        if (w * h > 1) Assert.Equal(expected.Color, back.Color);
        Assert.Equal(ImageFormat.Xpm, ImageCodec.DetectFormat(bytes));
        CheckOracle(back, bytes, ".xpm");
    }

    static bool HasTransparent(RawImage img)
    {
        var b = img.GetData<byte>();
        for (int i = img.Channels - 1; i < b.Length; i += img.Channels) if (b[i] == 0) return true;
        return false;
    }

    static RawImage StripAlphaAny(RawImage img)
    {
        if (img.Color == ColorModel.Rgba) return StripAlpha(img);
        var s = img.GetData<byte>();
        var d = new byte[img.Width * img.Height];
        for (int i = 0; i < d.Length; i++) d[i] = s[i * 2];
        return new RawImage(img.Width, img.Height, ColorModel.Gray, d);
    }

    [Fact]
    public void XpmManyColours()
    {
        // 256 x 64 distinct colours: two characters per pixel are not enough (93^2 = 8649 < 16384)
        var img = RawImage.Create(256, 64, ColorModel.Rgb, SampleType.UInt8);
        var b = img.GetData<byte>();
        for (int i = 0; i < 256 * 64; i++) { b[i * 3] = (byte)i; b[i * 3 + 1] = (byte)(i >> 8); b[i * 3 + 2] = 99; }
        var bytes = Xpm.Encode(img);
        Assert.Contains("\"256 64 16384 3\"", Encoding.ASCII.GetString(bytes));
        AssertEqual(img, Xpm.Decode(bytes));
        CheckOracle(img, bytes, ".xpm");
        // partial alpha is thresholded
        var a = new RawImage(2, 1, ColorModel.Rgba, new byte[] { 1, 2, 3, 100, 4, 5, 6, 200 });
        Assert.Equal(new byte[] { 0, 0, 0, 0, 4, 5, 6, 255 }, Xpm.Decode(Xpm.Encode(a)).GetData<byte>());
    }

    [Fact]
    public void WbmpRoundTrip()
    {
        foreach (var (w, h) in Sizes.Append((200, 130)))
        {
            var img = Pattern(w, h, ColorModel.Gray, w + h, bilevel: true);
            var bytes = Wbmp.Encode(img);
            AssertEqual(img, Wbmp.Decode(bytes));
            Assert.Equal(ImageFormat.Wbmp, ImageCodec.DetectFormat(bytes));
            CheckOracle(img, bytes, ".wbmp");
        }
    }

    [Theory]
    [InlineData(".pcx"), InlineData(".dcx"), InlineData(".sgi"), InlineData(".rgb"), InlineData(".ras"), InlineData(".xbm"), InlineData(".xpm"), InlineData(".wbmp")]
    public void EncodeByExtension(string ext)
    {
        var img = Pattern(13, 7, ColorModel.Gray, 1, bilevel: true);
        var path = Path.Combine(Path.GetTempPath(), $"pixcodec-{Guid.NewGuid():N}{ext}");
        try
        {
            ImageCodec.Encode(img, path);
            AssertEqual(img, ImageCodec.Decode(path));
            Assert.Equal(ImageCodec.FormatFromExtension(path), ImageCodec.GetInfo(path).Format);
        }
        finally { File.Delete(path); }
    }

    sealed class NonSeekable(byte[] data) : Stream
    {
        readonly MemoryStream inner = new(data);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, 100));
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
