using System.Buffers.Binary;
using PixCodec;
using Xunit;
using static PixCodec.Tests.TestUtil;

namespace PixCodec.Tests;

/// <summary>Helpers shared by the JPEG 2000 test classes.</summary>
public static class Jp2TestUtil
{
    public static readonly string[] Extensions = { ".jp2", ".j2k", ".j2c", ".jph", ".jhc" };

    public static IEnumerable<object[]> Fixtures() => Files("jp2", Extensions);

    /// <summary>The codestream inside a file (the file itself for raw codestreams).</summary>
    public static int CodestreamStart(byte[] data)
    {
        if (data[0] == 0xFF) return 0;
        for (int i = 0; i + 4 <= data.Length; i++)
            if (data[i] == 0xFF && data[i + 1] == 0x4F && data[i + 2] == 0xFF && data[i + 3] == 0x51) return i;
        throw new InvalidOperationException("no codestream");
    }

    /// <summary>Whether a component of the codestream uses the irreversible wavelet (COD / COC in the main header).</summary>
    public static bool IsLossy(byte[] data)
    {
        int p = CodestreamStart(data) + 2;
        int comps = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p + 38));
        while (p + 4 <= data.Length)
        {
            int marker = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p));
            if (marker == 0xFF90 || marker == 0xFFD9) break;
            int len = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p + 2));
            if (marker == 0xFF52 && data[p + 13] == 0) return true;
            if (marker == 0xFF53 && data[p + 4 + (comps > 256 ? 2 : 1) + 5] == 0) return true;
            p += 2 + len;
        }
        return false;
    }

    /// <summary>
    /// Largest difference to the reference decoder that is accepted for a file. Reversible files must match exactly.
    /// Irreversible ones are float arithmetic on both sides: 1 level at up to 8 bit, and the same relative amount
    /// (2^(depth - 8)) for deeper samples. A CMYK conversion can add 1.
    /// </summary>
    public static double Tolerance(string file, byte[] data, int depth)
    {
        double t = IsLossy(data) ? Math.Max(1, 1 << Math.Max(0, depth - 8)) : 0;
        if (file.Contains("cmyk")) t += 1;
        return t;
    }

    /// <summary>
    /// Decodes with the python oracle (OpenJPEG / OpenJPH through imagecodecs). Null when it is not available or
    /// cannot read this kind of file (exit status 77), so that a test checks less instead of failing.
    /// </summary>
    public static RawImage? Oracle(byte[] encoded, string extension)
    {
        if (!PythonAvailable) return null;
        var tmp = Path.Combine(FixtureDir, "_tmp");
        Directory.CreateDirectory(tmp);
        var f = Path.Combine(tmp, Guid.NewGuid().ToString("N") + extension);
        try
        {
            File.WriteAllBytes(f, encoded);
            var (exit, output) = RunPython($"decode_to_ref.py \"{f}\" \"{f}.ref\"");
            if (exit == 77) return null;
            Assert.True(exit == 0, "oracle decode failed: " + output);
            return LoadRef(f + ".ref");
        }
        finally
        {
            File.Delete(f); File.Delete(f + ".ref");
        }
    }

    /// <summary>The deterministic test image of the python generators, in C#: gradients, edges and some noise.</summary>
    public static RawImage Pattern(int w, int h, ColorModel color, SampleType type = SampleType.UInt8, int channels = 0, int seed = 1, int bits = 0)
    {
        var img = RawImage.Create(w, h, color, type, channels);
        int c = img.Channels;
        var rng = new Random(seed);
        int depth = bits > 0 ? bits : RawImage.BytesPerSample(type) * 8;
        bool signed = type is SampleType.Int8 or SampleType.Int16 or SampleType.Int32;
        double max = Math.Pow(2, depth) - 1, off = signed ? Math.Pow(2, depth - 1) : 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int i = 0; i < c; i++)
                {
                    double v = 0.5 + 0.5 * Math.Sin(x / (7.0 + 3 * i) + i) * Math.Cos(y / (5.0 + 2 * i));
                    v = 0.6 * v + 0.3 * ((x / 8 + y / 8 + i) % 2) + 0.1 * rng.NextDouble();
                    long s = (long)Math.Round(Math.Clamp(v, 0, 1) * max - off);
                    int k = (y * w + x) * c + i;
                    switch (type)
                    {
                        case SampleType.UInt8: ((byte[])img.Data)[k] = (byte)s; break;
                        case SampleType.Int8: ((sbyte[])img.Data)[k] = (sbyte)s; break;
                        case SampleType.UInt16: ((ushort[])img.Data)[k] = (ushort)s; break;
                        case SampleType.Int16: ((short[])img.Data)[k] = (short)s; break;
                        case SampleType.UInt32: ((uint[])img.Data)[k] = (uint)s; break;
                        case SampleType.Int32: ((int[])img.Data)[k] = (int)s; break;
                    }
                }
        return img;
    }

    /// <summary>A smooth photo-like image (low frequency structure plus a little texture) for the lossy tests.</summary>
    public static RawImage Photo(int w, int h, ColorModel color = ColorModel.Rgb, bool sixteen = false)
    {
        var img = RawImage.Create(w, h, color, sixteen ? SampleType.UInt16 : SampleType.UInt8);
        int c = img.Channels;
        var rng = new Random(11);
        double max = sixteen ? 65535 : 255;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int i = 0; i < c; i++)
                {
                    double v = 0.5 + 0.25 * Math.Sin(x / 23.0 + i) + 0.2 * Math.Cos(y / 17.0 + 2 * i) + 0.05 * Math.Sin((x + 2 * y) / 3.0)
                        + (x > w / 2 && y > h / 3 ? 0.1 : 0) + 0.02 * (rng.NextDouble() - 0.5);
                    long s = (long)Math.Round(Math.Clamp(v, 0, 1) * max);
                    int k = (y * w + x) * c + i;
                    if (sixteen) ((ushort[])img.Data)[k] = (ushort)s; else ((byte[])img.Data)[k] = (byte)s;
                }
        return img;
    }

    public static RawImage Crop(RawImage img, int x0, int y0, int w, int h)
    {
        var r = RawImage.Create(w, h, img.Color, img.SampleType, img.Channels);
        int bps = RawImage.BytesPerSample(img.SampleType) * img.Channels;
        var src = img.GetBytes(); var dst = r.GetBytes();
        for (int y = 0; y < h; y++)
            src.Slice(((y0 + y) * img.Width + x0) * bps, w * bps).CopyTo(dst.Slice(y * w * bps));
        return r;
    }

    public static RawImage Channels(RawImage img, params int[] channels)
    {
        var r = RawImage.Create(img.Width, img.Height, channels.Length == 1 ? ColorModel.Gray : ColorModel.Other, img.SampleType, channels.Length);
        int bps = RawImage.BytesPerSample(img.SampleType);
        var src = img.GetBytes(); var dst = r.GetBytes();
        for (int i = 0; i < img.Width * img.Height; i++)
            for (int c = 0; c < channels.Length; c++)
                src.Slice((i * img.Channels + channels[c]) * bps, bps).CopyTo(dst.Slice((i * channels.Length + c) * bps));
        return r;
    }

    /// <summary>The window the option references of gen_jp2.py were made for.</summary>
    public static (int X, int Y, int Width, int Height) OptionWindow(int w, int h) => (w / 4, h / 3, w / 2, h / 2);
}

public class Jp2Tests
{
    public static IEnumerable<object[]> Fixtures() => Jp2TestUtil.Fixtures();

    [Theory, MemberData(nameof(Fixtures))]
    public void DecodeMatchesReference(string file)
    {
        var data = File.ReadAllBytes(Fixture(file));
        var expected = LoadRef(Fixture(file) + ".ref");
        var info = Jp2.GetInfo(data);
        var img = Jp2.Decode(data);
        AssertEqual(expected, img, Jp2TestUtil.Tolerance(file, data, info.BitsPerSample));

        // header information describes what Decode returns
        Assert.Equal(ImageFormat.Jp2, info.Format);
        Assert.Equal((img.Width, img.Height, img.Channels, img.SampleType, img.Color), (info.Width, info.Height, info.Channels, info.SampleType, info.Color));
        Assert.Equal(1, info.FrameCount);
        Assert.InRange(info.BitsPerSample, 1, RawImage.BytesPerSample(img.SampleType) * 8);
        var m = img.Metadata ?? new ImageMetadata();
        Assert.Equal(m.IccProfile != null, info.HasIccProfile);
        Assert.Equal((m.DpiX, m.DpiY), (info.DpiX, info.DpiY));
        // an EXIF box behind the codestream is beyond what GetInfo reads
        Assert.Equal(file.Contains("exif_after") ? 0 : m.Orientation, info.Orientation);
        Assert.Equal(info, ImageCodec.GetInfo(data));
        Assert.True(Jp2.IsMatch(data));

        // the same pixels on one thread
        var seq = Jp2.Decode(data, new Jp2DecodeOptions { Parallel = false });
        AssertEqual(img, seq);
        // and through the format independent entry point
        AssertEqual(img, ImageCodec.Decode(data));
    }

    [Theory]
    [InlineData("layers_irr.j2k"), InlineData("layers_rev.j2k"), InlineData("prog_lrcp.j2k"), InlineData("prog_rpcl.j2k"), InlineData("prog_pcrl.j2k"), InlineData("offset_tiles.j2k")]
    [InlineData("tileparts_r.j2k"), InlineData("rgb_res7.j2k"), InlineData("precincts.j2k"), InlineData("poc_layers.j2k"), InlineData("style63_layers_irr.j2k")]
    [InlineData("roi_layers.j2k"), InlineData("offset_tiles_irr.j2k"), InlineData("ht_tiles.jhc"), InlineData("ht_precincts.jhc"), InlineData("ht_tiles_irr.jhc")]
    public void OptionsMatchReference(string name)
    {
        var path = Fixture(Path.Combine("jp2", name));
        var data = File.ReadAllBytes(path);
        var info = Jp2.GetInfo(data);
        double tol = Jp2TestUtil.Tolerance(name, data, info.BitsPerSample);
        var win = Jp2TestUtil.OptionWindow(info.Width, info.Height);
        var options = new (string tag, Jp2DecodeOptions o)[]
        {
            ("reduce1", new Jp2DecodeOptions { Reduce = 1 }),
            ("reduce2", new Jp2DecodeOptions { Reduce = 2 }),
            ("layers1", new Jp2DecodeOptions { MaxLayers = 1 }),
            ("layers2", new Jp2DecodeOptions { MaxLayers = 2 }),
            ("window", new Jp2DecodeOptions { Region = win }),
            ("window_reduce1", new Jp2DecodeOptions { Region = win, Reduce = 1 }),
            ("window_layers1", new Jp2DecodeOptions { Region = win, MaxLayers = 1 }),
        };
        int checkedCount = 0;
        foreach (var (tag, o) in options)
        {
            var refPath = $"{path}.{tag}.ref";
            if (!File.Exists(refPath)) continue;
            var expected = LoadRef(refPath);
            // the references are plain components
            o.RawComponents = true;
            var img = Jp2.Decode(data, o);
            Assert.True(MaxDiff(expected, img) <= tol, $"{name} {tag}: {MaxDiff(expected, img)} > {tol}");
            checkedCount++;
        }
        Assert.True(checkedCount >= 2);
    }

    /// <summary>A window is exactly the cut-out of the whole image, for every fixture, also reduced and on one thread.</summary>
    [Theory, MemberData(nameof(Fixtures))]
    public void RegionEqualsCropOfFullDecode(string file)
    {
        var data = File.ReadAllBytes(Fixture(file));
        var full = Jp2.Decode(data);
        int w = full.Width, h = full.Height;
        var windows = new List<(int, int, int, int)> { (w / 4, h / 3, Math.Max(1, w / 2), Math.Max(1, h / 2)), (0, 0, Math.Max(1, w / 3), h), (w - 1, h - 1, 1, 1) };
        if (w > 9 && h > 9) windows.Add((3, 5, 5, 3));
        foreach (var (x, y, ww, hh) in windows)
        {
            var region = Jp2.Decode(data, new Jp2DecodeOptions { Region = (x, y, ww, hh) });
            AssertEqual(Jp2TestUtil.Crop(full, x, y, ww, hh), region);
        }
        // a window reaching beyond the image is clipped
        var clipped = Jp2.Decode(data, new Jp2DecodeOptions { Region = (w / 2, h / 2, w, h), Parallel = false });
        AssertEqual(Jp2TestUtil.Crop(full, w / 2, h / 2, w - w / 2, h - h / 2), clipped);
        Assert.Throws<ArgumentException>(() => Jp2.Decode(data, new Jp2DecodeOptions { Region = (w, 0, 5, 5) }));
    }

    [Theory]
    [InlineData("rgb_res7.j2k", 3), InlineData("offset_tiles.j2k", 5), InlineData("ht_levels7.jhc", 7), InlineData("sub_420.j2k", 5)]
    public void ReduceGivesTheLowerResolutions(string name, int levels)
    {
        var data = File.ReadAllBytes(Fixture(Path.Combine("jp2", name)));
        var full = Jp2.Decode(data);
        for (int r = 1; r <= levels; r++)
        {
            var img = Jp2.Decode(data, new Jp2DecodeOptions { Reduce = r });
            // sizes follow the resolution level grid: ceil of the image corners divided by 2^r
            Assert.InRange(img.Width, full.Width >> r, (full.Width >> r) + 1);
            Assert.InRange(img.Height, full.Height >> r, (full.Height >> r) + 1);
            Assert.Equal(full.Channels, img.Channels);
            var w = Jp2.Decode(data, new Jp2DecodeOptions { Reduce = r, Region = (0, 0, full.Width, full.Height), Parallel = false });
            AssertEqual(img, w);
        }
        Assert.Throws<ArgumentException>(() => Jp2.Decode(data, new Jp2DecodeOptions { Reduce = levels + 1 }));
    }

    /// <summary>The first layers of an image are the same whatever order its packets come in.</summary>
    [Fact]
    public void LayersDoNotDependOnProgression()
    {
        var lrcp = File.ReadAllBytes(Fixture("jp2/prog_lrcp.j2k"));
        foreach (var other in new[] { "rlcp", "rpcl", "pcrl", "cprl" })
        {
            var data = File.ReadAllBytes(Fixture($"jp2/prog_{other}.j2k"));
            for (int l = 1; l <= 3; l++)
                AssertEqual(Jp2.Decode(lrcp, new Jp2DecodeOptions { MaxLayers = l }), Jp2.Decode(data, new Jp2DecodeOptions { MaxLayers = l }));
        }
    }

    [Fact]
    public void ComponentSelectionAndRawComponents()
    {
        var data = File.ReadAllBytes(Fixture("jp2/comps5_planar.j2k"));
        var all = Jp2.Decode(data);
        Assert.Equal((ColorModel.Other, 5), (all.Color, all.Channels));
        var some = Jp2.Decode(data, new Jp2DecodeOptions { Components = new[] { 3, 0 } });
        AssertEqual(Jp2TestUtil.Channels(all, 3, 0), some);
        var one = Jp2.Decode(data, new Jp2DecodeOptions { Components = new[] { 4 } });
        Assert.Equal(ColorModel.Gray, one.Color);
        AssertEqual(Jp2TestUtil.Channels(all, 4), one);
        Assert.Throws<ArgumentException>(() => Jp2.Decode(data, new Jp2DecodeOptions { Components = new[] { 5 } }));

        // a component of an image with a colour transform: all three are decoded, one is returned
        var rgb = File.ReadAllBytes(Fixture("jp2/rgb8_rev.j2k"));
        var full = Jp2.Decode(rgb);
        AssertEqual(Jp2TestUtil.Channels(full, 1), Jp2.Decode(rgb, new Jp2DecodeOptions { Components = new[] { 1 } }));

        // raw components: the palette index instead of the colour, 4 bit values as stored
        var pal = File.ReadAllBytes(Fixture("jp2/palette_rgb.jp2"));
        var index = Jp2.Decode(pal, new Jp2DecodeOptions { RawComponents = true });
        Assert.Equal((ColorModel.Gray, 1, SampleType.UInt8), (index.Color, index.Channels, index.SampleType));
        Assert.True(index.GetData<byte>().Max() <= 15);
        // raw components of a luma / chroma file: no conversion, chroma replicated
        var ycc = File.ReadAllBytes(Fixture("jp2/sub_420.j2k"));
        var raw = Jp2.Decode(ycc, new Jp2DecodeOptions { RawComponents = true });
        Assert.Equal((ColorModel.Other, 3), (raw.Color, raw.Channels));
        var d = raw.GetData<byte>();
        Assert.Equal(d[1], d[3 + 1]);          // one chroma sample covers two pixels
        Assert.NotEqual(Jp2.Decode(ycc).GetData<byte>().AsSpan(0, 30).ToArray(), d.AsSpan(0, 30).ToArray());
    }

    [Fact]
    public void Metadata()
    {
        byte[] Load(string n) => File.ReadAllBytes(Fixture("jp2/" + n));
        var m = Jp2.Decode(Load("res_300dpi.jp2")).Metadata!;
        Assert.Equal(300, m.DpiX, 2); Assert.Equal(300, m.DpiY, 2);
        m = Jp2.Decode(Load("res_capture_and_display.jp2")).Metadata!;
        Assert.Equal(72.0, m.DpiX, 1); Assert.Equal(144.0, m.DpiY, 1);
        m = Jp2.Decode(Load("res_display_only.jp2")).Metadata!;
        Assert.Equal(100.0, m.DpiX, 1); Assert.Equal(100.0, m.DpiY, 1);

        m = Jp2.Decode(Load("exif_before.jp2")).Metadata!;
        Assert.Equal(6, m.Orientation);
        Assert.Equal((byte)'I', m.Exif![0]);
        Assert.Equal(6, Jp2.GetInfo(Load("exif_before.jp2")).Orientation);
        Assert.Equal((37, 45), (Jp2.GetInfo(Load("exif_before.jp2")).OrientedWidth, Jp2.GetInfo(Load("exif_before.jp2")).OrientedHeight));
        m = Jp2.Decode(Load("exif_after.jp2")).Metadata!;
        Assert.Equal(8, m.Orientation);
        Assert.Equal((byte)'M', m.Exif![0]);
        Assert.Equal(0, Jp2.GetInfo(Load("exif_after.jp2")).Orientation);
        m = Jp2.Decode(Load("exif_prefixed.jp2")).Metadata!;
        Assert.Equal(3, m.Orientation);
        Assert.Equal((byte)'I', m.Exif![0]);   // the "Exif\0\0" prefix some writers add is dropped

        var icc = Jp2.Decode(Load("icc.jp2")).Metadata!.IccProfile!;
        Assert.True(icc.Length > 128);
        Assert.Equal("acsp", System.Text.Encoding.ASCII.GetString(icc, 36, 4));
        Assert.True(Jp2.GetInfo(Load("icc.jp2")).HasIccProfile);
        Assert.Null(Jp2.Decode(Load("rgb8_rev.jp2")).Metadata);
        Assert.Null(Jp2.Decode(Load("rgb8_rev.j2k")).Metadata);

        var xmp = Jp2.GetXmp(Load("xmp_uuid.jp2"));
        Assert.StartsWith("<?xpacket", System.Text.Encoding.ASCII.GetString(xmp!));
        Assert.Null(Jp2.GetXmp(Load("rgb8_rev.jp2")));
        Assert.Null(Jp2.GetXmp(Load("rgb8_rev.j2k")));
    }

    [Fact]
    public void ColourModels()
    {
        ImageInfo Info(string n) => Jp2.GetInfo(File.ReadAllBytes(Fixture("jp2/" + n)));
        Assert.Equal((ColorModel.Gray, 8), (Info("gray8_rev.j2k").Color, Info("gray8_rev.j2k").BitsPerSample));
        Assert.Equal(ColorModel.GrayAlpha, Info("graya8_rev.jp2").Color);
        Assert.Equal(ColorModel.Rgb, Info("rgb8_rev.j2k").Color);
        Assert.Equal(ColorModel.Rgba, Info("rgba8_rev.j2k").Color);
        Assert.Equal(ColorModel.Rgba, Info("rgba_no_cdef.jp2").Color);
        Assert.Equal(ColorModel.Rgba, Info("cdef_argb.jp2").Color);
        Assert.Equal(ColorModel.Rgba, Info("cdef_premultiplied.jp2").Color);
        Assert.Equal((ColorModel.Other, 4), (Info("cdef_unassociated.jp2").Color, Info("cdef_unassociated.jp2").Channels));
        Assert.Equal(ColorModel.GrayAlpha, Info("cdef_alpha_gray.jp2").Color);
        Assert.Equal(ColorModel.Rgb, Info("palette_rgb.jp2").Color);
        Assert.Equal((ColorModel.Rgb, SampleType.UInt16, 12), (Info("palette_12bit.jp2").Color, Info("palette_12bit.jp2").SampleType, Info("palette_12bit.jp2").BitsPerSample));
        Assert.Equal(ColorModel.Rgba, Info("palette_rgba.jp2").Color);
        Assert.Equal(ColorModel.Rgba, Info("palette_plus_alpha.jp2").Color);
        Assert.Equal(ColorModel.Gray, Info("palette_gray.jp2").Color);
        Assert.Equal(ColorModel.Rgb, Info("cmyk.jp2").Color);
        Assert.Equal(ColorModel.Rgb, Info("sycc.jp2").Color);
        Assert.Equal((ColorModel.Other, 5), (Info("comps5_planar.j2k").Color, Info("comps5_planar.j2k").Channels));
        Assert.Equal((SampleType.UInt16, 12), (Info("gray12_rev.j2k").SampleType, Info("gray12_rev.j2k").BitsPerSample));
        Assert.Equal((SampleType.Int16, 16), (Info("gray_s16_rev.j2k").SampleType, Info("gray_s16_rev.j2k").BitsPerSample));
        Assert.Equal((SampleType.Int8, 8), (Info("gray_s8_rev.j2k").SampleType, Info("gray_s8_rev.j2k").BitsPerSample));
        Assert.Equal((SampleType.UInt8, 1), (Info("gray1bit.j2k").SampleType, Info("gray1bit.j2k").BitsPerSample));
        Assert.Equal((SampleType.UInt16, 10), (Info("ht_depths_10_8_4.jhc").SampleType, Info("ht_depths_10_8_4.jhc").BitsPerSample));

        // a bilevel image comes out as 0 / 255
        var bits = Jp2.Decode(File.ReadAllBytes(Fixture("jp2/gray1bit.j2k"))).GetData<byte>();
        Assert.All(bits, b => Assert.True(b == 0 || b == 255));
        Assert.Contains((byte)255, bits);

        // CMYK kept as four channels on request
        var cmyk = Jp2.Decode(File.ReadAllBytes(Fixture("jp2/cmyk.jp2")), new Jp2DecodeOptions { RawComponents = true });
        Assert.Equal((ColorModel.Other, 4), (cmyk.Color, cmyk.Channels));
    }

    [Fact]
    public void MaxPixels()
    {
        var data = File.ReadAllBytes(Fixture("jp2/rgb8_rev.j2k"));
        var e = Assert.Throws<ImageNotSupportedException>(() => Jp2.Decode(data, new Jp2DecodeOptions { MaxPixels = 45 * 37 - 1 }));
        Assert.Contains("MaxPixels", e.Message);
        Assert.Equal(45, Jp2.Decode(data, new Jp2DecodeOptions { MaxPixels = 45 * 37 }).Width);
        // the bound is on what is decoded: a window or a reduced image of a larger picture passes
        Assert.Equal(10, Jp2.Decode(data, new Jp2DecodeOptions { MaxPixels = 100, Region = (5, 5, 10, 10) }).Width);
        Assert.Equal(23, Jp2.Decode(data, new Jp2DecodeOptions { MaxPixels = 23 * 19, Reduce = 1 }).Width);
    }

    [Fact]
    public void InternalEntryPoints()
    {
        // planes at their own size, no colour handling: what the TIFF and EXR codecs build on
        var data = File.ReadAllBytes(Fixture("jp2/sub_420.j2k"));
        var planes = Jp2.DecodeCodestreamPlanes(data);
        Assert.Equal((46, 38, 3), (planes.Width, planes.Height, planes.Planes.Length));
        Assert.Equal((46, 38, 1, 1), (planes.Planes[0].Width, planes.Planes[0].Height, planes.Planes[0].Dx, planes.Planes[0].Dy));
        Assert.Equal((23, 19, 2, 2), (planes.Planes[1].Width, planes.Planes[1].Height, planes.Planes[1].Dx, planes.Planes[1].Dy));
        Assert.All(planes.Planes, p => Assert.Equal((8, false), (p.Precision, p.Signed)));
        Assert.False(planes.UsedColorTransform);
        var raw = Jp2.DecodeCodestream(data);
        Assert.Equal((ColorModel.Other, 3, SampleType.UInt8), (raw.Color, raw.Channels, raw.SampleType));
        AssertEqual(Jp2.Decode(data, new Jp2DecodeOptions { RawComponents = true }), raw);
        for (int i = 0; i < 46 * 38; i++) Assert.Equal(planes.Planes[0].Data![i], raw.GetData<byte>()[3 * i]);

        // a JP2 wrapper is skipped, its palette ignored
        var pal = File.ReadAllBytes(Fixture("jp2/palette_rgb.jp2"));
        Assert.Equal(ColorModel.Gray, Jp2.DecodeCodestream(pal).Color);
        Assert.True(Jp2.DecodeCodestreamPlanes(File.ReadAllBytes(Fixture("jp2/rgb8_rev.jp2"))).UsedColorTransform);

        // encode planes with their own subsampling and depth, decode them back
        var src = new J2kPlanes { Width = 41, Height = 30, Planes = new J2kPlane[3] };
        var rng = new Random(4);
        (int dx, int dy, int prec, bool signed)[] layout = { (1, 1, 10, false), (2, 2, 7, true), (3, 1, 1, false) };
        for (int c = 0; c < 3; c++)
        {
            var (dx, dy, prec, signed) = layout[c];
            int w = (41 + dx - 1) / dx, h = (30 + dy - 1) / dy;
            var d = new int[w * h];
            for (int i = 0; i < d.Length; i++) d[i] = rng.Next(1 << prec) - (signed ? 1 << (prec - 1) : 0);
            src.Planes[c] = new J2kPlane { Width = w, Height = h, Dx = dx, Dy = dy, Precision = prec, Signed = signed, Data = d };
        }
        foreach (bool ht in new[] { false, true })
        {
            var cs = Jp2.EncodeCodestreamPlanes(src, new Jp2EncodeOptions { HighThroughput = ht, Levels = 3 });
            var back = Jp2.DecodeCodestreamPlanes(cs);
            Assert.Equal((41, 30), (back.Width, back.Height));
            for (int c = 0; c < 3; c++)
            {
                var (a, b) = (src.Planes[c], back.Planes[c]);
                Assert.Equal((a.Width, a.Height, a.Dx, a.Dy, a.Precision, a.Signed), (b.Width, b.Height, b.Dx, b.Dy, b.Precision, b.Signed));
                Assert.Equal(a.Data, b.Data);
            }
        }
    }

    [Fact]
    public void AbsentTilesOfAClosedCodestreamAreZero()
    {
        // drop the second tile-part: the codestream still ends with EOC, as the T.803 stream b2_mono does
        var data = File.ReadAllBytes(Fixture("jp2/rgb_tiles16.j2k"));
        int sot = FindMarker(data, 0xFF90, 0);
        int second = sot + (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(sot + 6));
        int third = second + (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(second + 6));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(second + 4)));
        var cut = data.AsSpan(0, second).ToArray().Concat(data.AsSpan(third).ToArray()).ToArray();
        var full = Jp2.Decode(data);
        var img = Jp2.Decode(cut);
        var a = full.GetData<byte>(); var b = img.GetData<byte>();
        for (int y = 0; y < full.Height; y++)
            for (int x = 0; x < full.Width; x++)
                for (int c = 0; c < 3; c++)
                {
                    int i = (y * full.Width + x) * 3 + c;
                    Assert.Equal(x >= 16 && x < 32 && y < 16 ? 0 : a[i], b[i]);
                }
        // without the closing EOC the same data is a truncated file
        var open = cut.AsSpan(0, cut.Length - 2).ToArray();
        Assert.Throws<ImageFormatException>(() => Jp2.Decode(open));
        AssertEqual(img, Jp2.Decode(open, new Jp2DecodeOptions { AllowTruncated = true }));
    }

    static int FindMarker(byte[] data, int marker, int from)
    {
        for (int i = from; i + 1 < data.Length; i++)
            if (data[i] == marker >> 8 && data[i + 1] == (marker & 0xFF)) return i;
        return -1;
    }

    [Fact]
    public void InfoReadsOnlyTheHeader()
    {
        var img = RawImage.Create(4000, 3000, ColorModel.Rgb, SampleType.UInt8);
        var d = img.GetData<byte>();
        for (int i = 0; i < d.Length; i++) d[i] = (byte)(i * 7 >> 3);
        foreach (bool raw in new[] { false, true })
        {
            var bytes = Jp2.Encode(img, new Jp2EncodeOptions { RawCodestream = raw, HighThroughput = true, Levels = 2 });
            var info = ImageCodec.GetInfo(new MemoryStream(bytes));
            Assert.Equal((ImageFormat.Jp2, 4000, 3000, 3, SampleType.UInt8, ColorModel.Rgb), (info.Format, info.Width, info.Height, info.Channels, info.SampleType, info.Color));
            // the first 200 bytes hold the boxes and SIZ
            Assert.Equal(info, Jp2.GetInfo(bytes.AsSpan(0, 200)));
            Assert.Throws<ImageFormatException>(() => Jp2.GetInfo(bytes.AsSpan(0, raw ? 40 : 60)));
        }
    }

    [Fact]
    public void IsMatch()
    {
        Assert.False(Jp2.IsMatch(default));
        Assert.False(Jp2.IsMatch(new byte[] { 0xFF, 0x4F, 0xFF }));
        Assert.False(Jp2.IsMatch(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }));
        Assert.True(Jp2.IsMatch(new byte[] { 0xFF, 0x4F, 0xFF, 0x51 }));
        Assert.Equal(ImageFormat.Jp2, ImageCodec.DetectFormat(File.ReadAllBytes(Fixture("jp2/ht_container_rev.jph"))));
        foreach (var ext in new[] { ".jp2", ".j2k", ".j2c", ".jph", ".jhc", ".JP2" }) Assert.Equal(ImageFormat.Jp2, ImageCodec.FormatFromExtension("a" + ext));
    }
}
