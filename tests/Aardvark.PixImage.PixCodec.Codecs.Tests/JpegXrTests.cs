using System.Buffers.Binary;
using Aardvark.ImageCodecs;
using Aardvark.ImageCodecs.JpegXrImpl;
using Xunit;
using static Aardvark.ImageCodecs.Tests.TestUtil;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>
/// JPEG XR: jxrlib written fixtures decoded bit-exactly (tests/oracle/gen_jpegxr.py), our encoder's files decoded by
/// jxrlib (tests/oracle/jxr_oracle.py) to what we decode, lossless round trips, container metadata.
/// </summary>
public class JpegXrTests
{
    public static IEnumerable<object[]> Fixtures => Files("jpegxr", ".jxr");

    [Fact]
    public void FixturesArePresent() => Assert.True(Fixtures.Count() > 150);

    /// <summary>
    /// Our reference output (the values after ClippingAndPackingStage( )) in the PXRF form jxr_oracle.py writes: one channel
    /// per output array, alpha last; packed words for BD5 / BD565 / BD10 RGB, 0 / 1 per pixel for BlackWhite.
    /// </summary>
    internal static RawImage Reference(byte[] data)
    {
        var f = JxrFile.Open(data, false);
        var (p, a) = f.DecodeArrays(data, false);
        var ih = f.Cs.Ih;
        int w = ih.Width, h = ih.Height;
        var arrays = p.A.ToList();
        if (a != null) arrays.Add(a.A[0]);
        int ch = arrays.Count;
        var bd = ih.BitDepth;
        bool packed = ih.OutputFormat == JxrOutputFormat.Rgb && bd is JxrBitDepth.Bd5 or JxrBitDepth.Bd565 or JxrBitDepth.Bd10;
        var type = ih.OutputFormat == JxrOutputFormat.Rgbe ? SampleType.UInt8 : bd switch
        {
            JxrBitDepth.Bd8 or JxrBitDepth.Bd1White1 or JxrBitDepth.Bd1Black1 => SampleType.UInt8,
            JxrBitDepth.Bd16 => SampleType.UInt16,
            JxrBitDepth.Bd16S => SampleType.Int16,
            JxrBitDepth.Bd32S => SampleType.Int32,
            JxrBitDepth.Bd16F => SampleType.Float16,
            JxrBitDepth.Bd32F => SampleType.Float32,
            JxrBitDepth.Bd10 => packed ? SampleType.UInt32 : SampleType.UInt16,
            _ => SampleType.UInt16,
        };
        var img = RawImage.Create(w, h, RawImage.DefaultColorModel(ch), type, ch);
        var bytes = img.GetBytes();
        int size = RawImage.BytesPerSample(type);
        for (int c = 0; c < ch; c++)
        {
            var arr = arrays[c];
            int aw = c < p.W.Length ? p.W[c] : a!.W[0];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int v = bd is JxrBitDepth.Bd1White1 or JxrBitDepth.Bd1Black1
                        ? (arr[y * aw + (x >> 3)] >> (7 - (x & 7))) & 1
                        : arr[y * aw + x];
                    var s = bytes.Slice(((y * w + x) * ch + c) * size, size);
                    switch (size)
                    {
                        case 1: s[0] = (byte)v; break;
                        case 2: BinaryPrimitives.WriteInt16LittleEndian(s, (short)v); break;
                        default: BinaryPrimitives.WriteInt32LittleEndian(s, v); break;
                    }
                }
        }
        return img;
    }

    [Theory, MemberData(nameof(Fixtures))]
    public void DecodesFixtureBitExactly(string file)
    {
        var data = File.ReadAllBytes(Fixture(file));
        Assert.Equal(ImageFormat.JpegXr, ImageCodec.DetectFormat(data));
        var expected = LoadRef(Fixture(file) + ".ref");
        AssertEqual(expected, Reference(data));
        // the public entry points describe and return the same image
        var img = JpegXr.Decode(data);
        var info = JpegXr.GetInfo(data);
        Assert.Equal((img.Width, img.Height, img.Channels, img.SampleType, img.Color), (info.Width, info.Height, info.Channels, info.SampleType, info.Color));
        Assert.Equal((expected.Width, expected.Height), (img.Width, img.Height));
        var generic = ImageCodec.Decode(data);
        Assert.Equal(img.GetBytes().ToArray(), generic.GetBytes().ToArray());
        var gi = ImageCodec.GetInfo(new MemoryStream(data));
        Assert.Equal((info.Width, info.Height, info.Channels), (gi.Width, gi.Height, gi.Channels));
        AssertEqual(PublicOf(expected, data), JpegXr.Decode(data, new JpegXrDecodeOptions { ConvertToRgb = false, Unpremultiply = false, Multithreaded = false }));
    }

    /// <summary>What JpegXr.Decode (without CMYK conversion and unpremultiplying) makes of reference output values.</summary>
    static RawImage PublicOf(RawImage r, byte[] data)
    {
        var f = JxrFile.Open(data, true);
        var ih = f.Cs.Ih;
        var bd = ih.BitDepth;
        int w = r.Width, h = r.Height, ch = r.Channels;
        long n = (long)w * h;
        bool alpha = f.Cs.Alpha != null || f.AlphaCs != null;
        var raw = ToDoubles(r);
        switch (ih.OutputFormat)
        {
            case JxrOutputFormat.Rgbe:
                {
                    var o = new float[n * 3];
                    for (long i = 0; i < n; i++)
                    {
                        int e = (int)raw[i * 4 + 3];
                        if (e == 0) continue;
                        for (int c = 0; c < 3; c++) o[i * 3 + c] = (float)(raw[i * 4 + c] * Math.ScaleB(1.0, e - 136));
                    }
                    return new RawImage(w, h, ColorModel.Rgb, o, 3);
                }
            case JxrOutputFormat.Rgb when bd is JxrBitDepth.Bd5 or JxrBitDepth.Bd565 or JxrBitDepth.Bd10:
                {
                    var o = RawImage.Create(w, h, ColorModel.Rgb, bd == JxrBitDepth.Bd10 ? SampleType.UInt16 : SampleType.UInt8, 3);
                    for (long i = 0; i < n; i++)
                    {
                        long v = (long)raw[i];
                        int[] f3 = bd switch
                        {
                            JxrBitDepth.Bd10 => new[] { (int)(v >> 20) & 1023, (int)(v >> 10) & 1023, (int)v & 1023 },
                            JxrBitDepth.Bd5 => new[] { (int)(v >> 10) & 31, (int)(v >> 5) & 31, (int)v & 31 },
                            _ => new[] { (int)(v >> 11) & 31, (int)(v >> 5) & 63, (int)v & 31 },
                        };
                        for (int c = 0; c < 3; c++)
                        {
                            // 10 bit values as they are, 5 and 6 bit ones stretched to 0..255
                            int max = bd == JxrBitDepth.Bd565 && c == 1 ? 63 : 31;
                            if (bd == JxrBitDepth.Bd10) ((ushort[])o.Data)[i * 3 + c] = (ushort)f3[c];
                            else ((byte[])o.Data)[i * 3 + c] = (byte)Math.Round(f3[c] * 255.0 / max, MidpointRounding.AwayFromZero);
                        }
                    }
                    return o;
                }
        }
        var model = ih.OutputFormat switch
        {
            JxrOutputFormat.YOnly => alpha ? ColorModel.GrayAlpha : ColorModel.Gray,
            JxrOutputFormat.Rgb => alpha ? ColorModel.Rgba : ColorModel.Rgb,
            _ => ColorModel.Other,
        };
        switch (bd)
        {
            case JxrBitDepth.Bd1White1 or JxrBitDepth.Bd1Black1:
                return new RawImage(w, h, model, Array.ConvertAll(raw, v => (byte)((v != 0) != (bd == JxrBitDepth.Bd1Black1) ? 255 : 0)), ch);
            case JxrBitDepth.Bd8: return new RawImage(w, h, model, Array.ConvertAll(raw, v => (byte)v), ch);
            case JxrBitDepth.Bd16: return new RawImage(w, h, model, Array.ConvertAll(raw, v => (ushort)v), ch);
            case JxrBitDepth.Bd10: return new RawImage(w, h, model, Array.ConvertAll(raw, v => (ushort)v), ch);
            case JxrBitDepth.Bd16S: return new RawImage(w, h, model, Array.ConvertAll(raw, v => (float)(v / 8192)), ch);
            case JxrBitDepth.Bd32S: return new RawImage(w, h, model, Array.ConvertAll(raw, v => (float)(v / 16777216)), ch);
            case JxrBitDepth.Bd16F: return new RawImage(w, h, model, (Half[])r.Data.Clone(), ch);
            default: return new RawImage(w, h, model, (float[])r.Data.Clone(), ch);
        }
    }

    // ------------------------------------------------------------------ header information

    [Fact]
    public void ReportsHeaderDetails()
    {
        ImageInfo Info(string f) => JpegXr.GetInfo(File.ReadAllBytes(Fixture("jpegxr/" + f + ".jxr")));
        var i = Info("rgb24_lossless");
        Assert.Equal((70, 45, 3, SampleType.UInt8, ColorModel.Rgb, 8), (i.Width, i.Height, i.Channels, i.SampleType, i.Color, i.BitsPerSample));
        Assert.Equal(TransferFunction.Srgb, i.Transfer);
        i = Info("rgba64_interleaved");
        Assert.Equal((4, SampleType.UInt16, ColorModel.Rgba), (i.Channels, i.SampleType, i.Color));
        i = Info("bgra32_planar_q");
        Assert.Equal((4, SampleType.UInt8, ColorModel.Rgba), (i.Channels, i.SampleType, i.Color));
        i = Info("grayhalf_lossless");
        Assert.Equal((1, SampleType.Float16, ColorModel.Gray), (i.Channels, i.SampleType, i.Color));
        Assert.Equal(TransferFunction.Linear, i.Transfer);
        i = Info("rgbfix96_lossless");
        Assert.Equal((3, SampleType.Float32, 32), (i.Channels, i.SampleType, i.BitsPerSample));
        i = Info("rgbe_lossless");
        Assert.Equal((3, SampleType.Float32, ColorModel.Rgb), (i.Channels, i.SampleType, i.Color));
        i = Info("cmyk32_lossless");
        Assert.Equal((3, ColorModel.Rgb), (i.Channels, i.Color));
        Assert.Equal((4, ColorModel.Other), (JpegXr.GetInfo(File.ReadAllBytes(Fixture("jpegxr/cmyk32_lossless.jxr")), new JpegXrDecodeOptions { ConvertToRgb = false }) is var c ? (c.Channels, c.Color) : default));
        i = Info("channels6_8_lossless");
        Assert.Equal((6, ColorModel.Other), (i.Channels, i.Color));
        i = Info("blackwhite_lossless");
        Assert.Equal((1, SampleType.UInt8, 1), (i.Channels, i.SampleType, i.BitsPerSample));
        Assert.Equal(ImageFormat.JpegXr, ImageCodec.FormatFromExtension("a.JXR"));
        Assert.Equal(ImageFormat.JpegXr, ImageCodec.FormatFromExtension("a.wdp"));
        Assert.Equal(ImageFormat.JpegXr, ImageCodec.FormatFromExtension("a.hdp"));
    }

    [Fact]
    public void DecodesBareCodestream()
    {
        var data = File.ReadAllBytes(Fixture("jpegxr/rgb24_tiles_u23.jxr"));
        var c = JxrContainer.Parse(data);
        var cs = c.Image(data).ToArray();
        Assert.Equal(ImageFormat.JpegXr, ImageCodec.DetectFormat(cs));
        AssertEqual(JpegXr.Decode(data), JpegXr.Decode(cs));
        Assert.Equal(JpegXr.GetInfo(data).Width, JpegXr.GetInfo(cs).Width);
    }

    [Fact]
    public void BlackWhiteAndCmykConversions()
    {
        // BlackWhite: 0 / 255 gray
        var bw = JpegXr.Decode(File.ReadAllBytes(Fixture("jpegxr/blackwhite_lossless.jxr")));
        Assert.All((byte[])bw.Data, v => Assert.True(v is 0 or 255));
        // CMYK to RGB (naive, as TIFF does): white where all inks are 0
        var d = File.ReadAllBytes(Fixture("jpegxr/cmyk32_lossless.jxr"));
        var stored = JpegXr.Decode(d, new JpegXrDecodeOptions { ConvertToRgb = false });
        var rgb = JpegXr.Decode(d);
        var s = (byte[])stored.Data; var r = (byte[])rgb.Data;
        for (int i = 0; i < stored.Width * stored.Height; i++)
        {
            int k = 255 - s[i * 4 + 3];
            for (int c = 0; c < 3; c++) Assert.Equal((255 - s[i * 4 + c]) * k / 255.0, r[i * 3 + c], 0.51);
        }
    }

    // ------------------------------------------------------------------ encoder

    /// <summary>Deterministic gradients, checker board and noise in [0, 1].</summary>
    static double[] Pattern(int w, int h, int c, int seed)
    {
        var r = new double[w * h * c];
        uint s = (uint)seed * 2654435761u + 1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int k = 0; k < c; k++)
                {
                    s = s * 1664525 + 1013904223;
                    double v = 0.5 + 0.5 * Math.Sin(x / (7.0 + 3 * k) + k) * Math.Cos(y / (5.0 + 2 * k));
                    v = 0.6 * v + 0.3 * ((x / 8 + y / 8 + k) % 2) + 0.1 * (s >> 8) / 16777216.0;
                    r[(y * w + x) * c + k] = Math.Clamp(v, 0, 1);
                }
        return r;
    }

    internal static RawImage Synthetic(string kind, int w = 77, int h = 53, int seed = 1)
    {
        var (m, c, fmt) = kind switch
        {
            var k when k.StartsWith("gray") => (ColorModel.Gray, 1, k[4..]),
            var k when k.StartsWith("rgba") => (ColorModel.Rgba, 4, k[4..]),
            var k when k.StartsWith("rgb") => (ColorModel.Rgb, 3, k[3..]),
            var k when k.StartsWith("ga") => (ColorModel.GrayAlpha, 2, k[2..]),
            var k when k.StartsWith("cmyk") => (ColorModel.Other, 4, k[4..]),
            var k when k.StartsWith("n5") => (ColorModel.Other, 5, k[2..]),
            var k when k.StartsWith("ycc") => (ColorModel.Other, 3, k[3..]),
            _ => throw new ArgumentException(kind),
        };
        var p = Pattern(w, h, c, seed);
        Array data = fmt switch
        {
            "8" => Array.ConvertAll(p, v => (byte)Math.Round(v * 255)),
            "16" => Array.ConvertAll(p, v => (ushort)Math.Round(v * 65535)),
            "h" => Array.ConvertAll(p, v => (Half)(v * 3 - 1)),
            "f" => Array.ConvertAll(p, v => (float)(v * 3 - 1)),
            "hdr" => Array.ConvertAll(p, v => (float)Math.Exp(v * 10 - 5)),
            _ => throw new ArgumentException(kind),
        };
        return new RawImage(w, h, m, data, c);
    }

    /// <summary>jxrlib's reference output of a file (jxr_oracle.py), or null when it cannot be verified here.</summary>
    static RawImage? Jxrlib(byte[] encoded)
    {
        if (!PythonAvailable) return null;
        var tmp = Path.Combine(FixtureDir, "_tmp");
        Directory.CreateDirectory(tmp);
        var f = Path.Combine(tmp, Guid.NewGuid().ToString("N") + ".jxr");
        try
        {
            File.WriteAllBytes(f, encoded);
            var (exit, output) = RunPython($"jxr_oracle.py \"{f}\" \"{f}.ref\"");
            if (CannotVerify(exit, output)) return null;
            Assert.True(exit == 0, "jxrlib oracle failed: " + output);
            return LoadRef(f + ".ref");
        }
        finally
        {
            File.Delete(f);
            File.Delete(f + ".ref");
        }
    }

    static JpegXrEncodeOptions Options(string spec)
    {
        var o = new JpegXrEncodeOptions();
        foreach (var kv in spec.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = kv.Split('=');
            var prop = typeof(JpegXrEncodeOptions).GetProperty(p[0], System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var t = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            prop.SetValue(o, t.IsEnum ? Enum.Parse(t, p[1]) : Convert.ChangeType(p[1], t, System.Globalization.CultureInfo.InvariantCulture));
        }
        return o;
    }

    public static IEnumerable<object[]> EncodeCases()
    {
        foreach (var k in new[] { "rgb8", "gray8", "rgba8", "rgb16", "gray16", "rgba16", "rgbh", "grayh", "rgbah", "rgbf", "grayf", "rgbaf", "ga8" })
        {
            yield return new object[] { k, "" };
            yield return new object[] { k, "Quality=0.8" };
            yield return new object[] { k, "Quality=0.3" };
            yield return new object[] { k, "Quality=0.6 Overlap=2 FrequencyOrder=true" };
            yield return new object[] { k, "Quality=0.7 Overlap=0 TileWidth=48 TileHeight=32" };
        }
        foreach (var s in new[]
        {
            "Quality=0.8 Subsampling=Yuv420", "Quality=0.8 Subsampling=Yuv422 Overlap=2", "Quality=0.8 Subsampling=YOnly",
            "Qp=1", "Qp=40 FrequencyOrder=true", "Qp=120 Overlap=2",
            "Bands=NoFlexbits Quality=0.8", "Bands=NoHighpass Quality=0.8 FrequencyOrder=true", "Bands=DcOnly Quality=0.8",
            "TrimFlexbits=3 Quality=0.9", "TrimFlexbits=6 Quality=0.9 FrequencyOrder=true",
            "TileWidth=16 TileHeight=16", "TileWidth=32 Quality=0.6 Overlap=2 Subsampling=Yuv420 FrequencyOrder=true",
            "TopMargin=5 LeftMargin=9", "TopMargin=33 LeftMargin=40 Quality=0.6 Overlap=2 Subsampling=Yuv420",
            "PixelFormat=Bgr24", "PixelFormat=Bgr32 Quality=0.7", "PixelFormat=Bgr555", "PixelFormat=Bgr565 Quality=0.8",
            "PixelFormat=Bgr101010 Quality=0.4 FrequencyOrder=true",
        })
            yield return new object[] { "rgb8", s };
        foreach (var s in new[] { "AlphaMode=Interleaved", "AlphaMode=Interleaved Quality=0.7 AlphaQuality=0.6",
                     "AlphaMode=Interleaved Quality=0.5 AlphaQuality=0.4 FrequencyOrder=true TileWidth=32 TileHeight=32",
                     "Quality=0.7 AlphaQuality=0.6 TopMargin=16 LeftMargin=3", "PixelFormat=Pbgra32" })
        {
            yield return new object[] { "rgba8", s };
            yield return new object[] { "rgba16", s.Replace("Pbgra32", "Prgba64") };
        }
        yield return new object[] { "rgbaf", "AlphaMode=Interleaved Quality=0.7 AlphaQuality=0.6" };
        yield return new object[] { "rgbah", "AlphaMode=Interleaved" };
        foreach (var s in new[] { "PixelFormat=Rgb48Fixed", "PixelFormat=Rgb96Fixed Quality=0.75", "PixelFormat=Rgbe", "PixelFormat=Rgbe Quality=0.75" })
            yield return new object[] { s.Contains("Rgbe") ? "rgbhdr" : "rgbf", s };
        yield return new object[] { "grayf", "PixelFormat=Gray16Fixed" };
        yield return new object[] { "grayf", "PixelFormat=Gray32Fixed Quality=0.75" };
        yield return new object[] { "rgbaf", "PixelFormat=Rgba64Fixed" };
        yield return new object[] { "rgbaf", "PixelFormat=Rgba128Fixed Quality=0.75" };
        yield return new object[] { "gray8", "PixelFormat=BlackWhite" };
        yield return new object[] { "gray8", "PixelFormat=BlackWhite Quality=0.8" };
        yield return new object[] { "cmyk8", "PixelFormat=Cmyk32" };
        yield return new object[] { "cmyk16", "PixelFormat=Cmyk64 Quality=0.75" };
        yield return new object[] { "n58", "PixelFormat=CmykAlpha40 LastChannelIsAlpha=true" };
        yield return new object[] { "n58", "" };
        yield return new object[] { "n516", "Quality=0.75" };
        yield return new object[] { "n58", "LastChannelIsAlpha=true" };
    }

    [Theory, MemberData(nameof(EncodeCases))]
    public void EncodesFilesJxrlibDecodesLikeUs(string kind, string spec)
    {
        var img = Synthetic(kind);
        var o = Options(spec);
        var enc = JpegXr.Encode(img, o);
        var ours = Reference(enc);
        var back = JpegXr.Decode(enc, new JpegXrDecodeOptions { ConvertToRgb = false });
        Assert.Equal((img.Width, img.Height), (back.Width, back.Height));
        bool lossless = o.Quality >= 1 && o.Qp == 0 && o.Bands == JpegXrBands.All && o.Subsampling is null or JpegXrSubsampling.Yuv444
            && o.PixelFormat is JpegXrPixelFormat.Auto or JpegXrPixelFormat.Bgr24 or JpegXrPixelFormat.Bgr32 or JpegXrPixelFormat.Cmyk32 or JpegXrPixelFormat.CmykAlpha40
            && kind is not ("rgbf" or "grayf" or "rgbaf" or "ga8");
        if (lossless) AssertEqual(img, back);
        var oracle = Jxrlib(enc);
        if (oracle != null) AssertEqual(oracle, ours);
    }

    [Fact]
    public void LosslessRoundTripsOtherModels()
    {
        // n channels, CMYK and YCC formats (the latter beyond jxrlib) are lossless at quality 1
        foreach (var (kind, pf, alpha) in new[] { ("n58", JpegXrPixelFormat.Auto, false), ("n516", JpegXrPixelFormat.Auto, true), ("cmyk8", JpegXrPixelFormat.CmykDirect32, false),
                     ("cmyk16", JpegXrPixelFormat.Cmyk64, false), ("ycc8", JpegXrPixelFormat.Ycc444, false), ("ycc16", JpegXrPixelFormat.Ycc444_16, false) })
        {
            var img = Synthetic(kind);
            var enc = JpegXr.Encode(img, new JpegXrEncodeOptions { PixelFormat = pf, LastChannelIsAlpha = alpha });
            var back = JpegXr.Decode(enc, new JpegXrDecodeOptions { ConvertToRgb = false });
            Assert.Equal(img.GetBytes().ToArray(), back.GetBytes().ToArray());
            Assert.Equal(img.Channels, back.Channels);
        }
        // float formats keep at least 22 bits
        var f = Synthetic("rgbf");
        var fb = JpegXr.Decode(JpegXr.Encode(f));
        Assert.True(MaxDiff(f, fb) < 1e-4);
    }

    [Fact]
    public void SubsampledYccFormats()
    {
        // 4:2:0 / 4:2:2 YCC from full size Y, Cb, Cr: luma exact, chroma averaged and replicated on decoding
        var img = Synthetic("ycc8", 64, 48);
        foreach (var pf in new[] { JpegXrPixelFormat.Ycc420, JpegXrPixelFormat.Ycc422 })
        {
            var back = JpegXr.Decode(JpegXr.Encode(img, new JpegXrEncodeOptions { PixelFormat = pf }));
            var a = (byte[])img.Data; var b = (byte[])back.Data;
            for (int i = 0; i < a.Length; i += 3) Assert.Equal(a[i], b[i]);
            Assert.True(Psnr(img, back, 255) > 25);
        }
    }

    [Fact]
    public void HardTiles()
    {
        // jxrlib applies the overlap filters of hard tiles on the transposed tile grid: it agrees where both grids coincide
        var img = Synthetic("rgb8", 96, 96);
        foreach (var spec in new[] { "HardTiling=true TileWidth=32 TileHeight=32", "HardTiling=true TileWidth=32 TileHeight=32 Quality=0.6 Overlap=2 Subsampling=Yuv420",
                     "HardTiling=true TileWidth=32 TileHeight=32 Quality=0.6 Overlap=2 Subsampling=Yuv422 FrequencyOrder=true" })
        {
            var enc = JpegXr.Encode(img, Options(spec));
            var oracle = Jxrlib(enc);
            if (oracle != null) AssertEqual(oracle, Reference(enc));
        }
        // other grids: lossless round trips
        var wide = Synthetic("rgb8", 211, 133);
        AssertEqual(wide, JpegXr.Decode(JpegXr.Encode(wide, Options("HardTiling=true TileWidth=48 TileHeight=32"))));
        AssertEqual(wide, JpegXr.Decode(JpegXr.Encode(wide, Options("HardTiling=true TileWidth=80 TileHeight=16 Overlap=2"))));
    }

    [Fact]
    public void LossyQualityOrdersSize()
    {
        var img = Synthetic("rgb8", 128, 96, 3);
        int last = int.MaxValue;
        double lastPsnr = double.PositiveInfinity;
        foreach (var q in new[] { 1.0, 0.9, 0.7, 0.5, 0.3, 0.1 })
        {
            var enc = JpegXr.Encode(img, new JpegXrEncodeOptions { Quality = q });
            double p = Psnr(img, JpegXr.Decode(enc), 255);
            Assert.True(enc.Length < last, $"quality {q}: {enc.Length} bytes");
            Assert.True(p <= lastPsnr);
            if (q >= 0.7) Assert.True(p > 35, $"quality {q}: PSNR {p}");
            last = enc.Length;
            lastPsnr = p;
        }
    }

    // ------------------------------------------------------------------ metadata, orientation

    [Fact]
    public void WritesAndReadsMetadata()
    {
        var img = Synthetic("rgb8", 40, 30);
        var icc = IccProfile.Srgb.ToBytes();
        var xmp = System.Text.Encoding.UTF8.GetBytes("<x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'><rdf:Description xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:creator>pixcodec</dc:creator></rdf:Description></rdf:RDF></x:xmpmeta>");
        var exif = new byte[] { (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0, 1, 0, 0x0F, 0x01, 2, 0, 6, 0, 0, 0, 26, 0, 0, 0, 0, 0, 0, 0, (byte)'A', (byte)'c', (byte)'m', (byte)'e', 0, 0 };
        img.Metadata = new ImageMetadata { IccProfile = icc, Xmp = xmp, DpiX = 300, DpiY = 150, Exif = exif };
        var enc = JpegXr.Encode(img);
        var info = ImageCodec.GetInfo(enc);
        Assert.True(info.HasIccProfile);
        Assert.Equal((300.0, 150.0), (info.DpiX, info.DpiY));
        Assert.Equal("Acme", info.Metadata?.Make);
        var back = JpegXr.Decode(enc);
        Assert.Equal(icc, back.Metadata!.IccProfile);
        Assert.Equal(xmp, back.Metadata.Xmp);
        Assert.Equal((300.0, 150.0), (back.Metadata.DpiX, back.Metadata.DpiY));
        var md = ImageCodec.ReadMetadata(enc);
        Assert.Equal("Acme", md.Make);
        Assert.Contains(md.Xmp!.Packets, x => x.Contains("pixcodec"));
        AssertEqual(img, back);
    }

    [Fact]
    public void Orientation()
    {
        var img = Synthetic("rgb8", 40, 30);
        img.Metadata = new ImageMetadata { Orientation = 6 };
        var enc = JpegXr.Encode(img);
        var info = JpegXr.GetInfo(enc);
        Assert.Equal((30, 40, 6, true), (info.Width, info.Height, info.Orientation, info.OrientationApplied));
        var upright = JpegXr.Decode(enc);
        AssertEqual(ImageOps.ApplyOrientation(new RawImage(img.Width, img.Height, img.Color, img.Data, img.Channels), 6), upright);
        Assert.Equal(1, upright.Metadata!.Orientation);
        var stored = JpegXr.Decode(enc, new JpegXrDecodeOptions { ApplyOrientation = false });
        Assert.Equal((40, 30, 6), (stored.Width, stored.Height, stored.Metadata!.Orientation));
        AssertEqual(img, stored);
    }

    // ------------------------------------------------------------------ robustness

    [Fact]
    public void DamagedFilesFailCleanly()
    {
        var rng = new Random(7);
        foreach (var f in new[] { "rgb24_q0.5_l2", "rgba64_interleaved_q", "rgb24_tiles_vh", "rgb24_420_l2_spatial", "cmyk32_q70", "rgbe_q60" })
        {
            var data = File.ReadAllBytes(Fixture("jpegxr/" + f + ".jxr"));
            for (int k = 0; k < 40; k++)
            {
                var d = (byte[])data.Clone();
                if (k % 4 == 0) d = d.AsSpan(0, rng.Next(8, d.Length)).ToArray();
                else for (int j = 0; j < 1 + k % 5; j++) d[rng.Next(8, d.Length)] ^= (byte)(1 << rng.Next(8));
                try { JpegXr.Decode(d); }
                catch (Exception e) when (e is ImageFormatException or ImageNotSupportedException) { }
            }
        }
    }

    [Fact]
    public void RejectsOversizedImages()
    {
        var d = File.ReadAllBytes(Fixture("jpegxr/rgb24_lossless.jxr"));
        Assert.Throws<ImageNotSupportedException>(() => JpegXr.Decode(d, new JpegXrDecodeOptions { MaxPixels = 1000 }));
    }

    [Fact]
    public void ParallelDecodeMatches()
    {
        var img = Synthetic("rgba16", 600, 400, 5);
        var enc = JpegXr.Encode(img, new JpegXrEncodeOptions { Quality = 0.8, Overlap = 2, TileWidth = 256, TileHeight = 256, AlphaMode = JpegXrAlphaMode.Interleaved });
        var a = JpegXr.Decode(enc, new JpegXrDecodeOptions { Multithreaded = true });
        var b = JpegXr.Decode(enc, new JpegXrDecodeOptions { Multithreaded = false });
        AssertEqual(a, b);
        Assert.Equal(enc, JpegXr.Encode(img, new JpegXrEncodeOptions { Quality = 0.8, Overlap = 2, TileWidth = 256, TileHeight = 256, AlphaMode = JpegXrAlphaMode.Interleaved, Multithreaded = false }));
    }

    [Fact]
    public void TransformsInvert()
    {
        // the pre-filters and forward transforms are exact inverses of the decoder's post-filters and inverse transforms
        var rng = new Random(3);
        var p = new int[32 * 32];
        for (int i = 0; i < p.Length; i++) p[i] = rng.Next(-40000, 40000);
        var q = (int[])p.Clone();
        JxrTransform.Overlap4(q, 32, 0, 0, 32, 32, true);
        JxrTransform.Overlap4(q, 32, 0, 0, 32, 32, false);
        Assert.Equal(p, q);
        JxrTransform.Overlap2(q, 32, 0, 0, 32, 32, true);
        JxrTransform.Overlap2(q, 32, 0, 0, 32, 32, false);
        Assert.Equal(p, q);
        for (int o = 0; o < 32 * 28; o += 4 * 32 + 4)
        {
            JxrTransform.Fct4x4(q, o, 32);
            JxrTransform.Ict4x4(q, o, 32);
        }
        Assert.Equal(p, q);
        // VLC tables are complete prefix codes
        foreach (var v in new[] { JxrTables.ValDcYuv, JxrTables.CbplpYuv444, JxrTables.CbplpYuv42x, JxrTables.ChrCbphp, JxrTables.NumChBlk, JxrTables.RefCbphp1, JxrTables.RunIndex, JxrTables.IndexB }
                     .Concat(JxrTables.AbsLevelIndex).Concat(JxrTables.NumCbphp).Concat(JxrTables.NumBlkCbphpYuv).Concat(JxrTables.IndexA).Concat(JxrTables.FirstIndex))
            Assert.Equal(1.0, v.Lengths.Sum(l => Math.Pow(2, -l)), 9);
        // the block-major array helpers and overlap filters agree with the raster ones
        var blocks = new int[p.Length];
        JxrTransform.ToBlocks(p, 32, 32, blocks);
        Assert.Equal(p, JxrTransform.ToRaster(blocks, 32, 32));
        var row = new int[32];
        for (int y = 0; y < 32; y++)
        {
            JxrTransform.ReadRow(blocks, 8, y, 0, 32, row);
            Assert.Equal(p.AsSpan(y * 32, 32).ToArray(), row);
            JxrTransform.WriteRow(blocks, 8, y, row);
        }
        foreach (bool forward in new[] { true, false })
        {
            JxrTransform.Overlap4(q, 32, 0, 0, 32, 32, forward);
            JxrTransform.Overlap4B(blocks, 8, 0, 0, 8, 8, forward);
            Assert.Equal(q, JxrTransform.ToRaster(blocks, 32, 32));
        }
    }

    [Fact]
    public void QuantizerDividesExactly()
    {
        var rng = new Random(5);
        foreach (int f in new[] { 1, 2, 3, 5, 7, 16, 100, 255, 1000, 65535, 1 << 20, 123456789, int.MaxValue / 3 })
            foreach (bool deadzone in new[] { true, false })
            {
                var q = new JxrQuantizer(f, deadzone);
                foreach (int v in new[] { 0, 1, -1, f, -f, f - 1, 1 - f, int.MaxValue, int.MinValue + 1, int.MaxValue / 3, -(int.MaxValue / 3) })
                    Assert.Equal(JxrPlaneEncoder.Quant(v, f, deadzone), q.Quant(v));
                for (int i = 0; i < 20000; i++)
                {
                    int v = rng.Next(int.MinValue + 1, int.MaxValue) >> rng.Next(31);
                    Assert.Equal(JxrPlaneEncoder.Quant(v, f, deadzone), q.Quant(v));
                }
            }
    }
}
