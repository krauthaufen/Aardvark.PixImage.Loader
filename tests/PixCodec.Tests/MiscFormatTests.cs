using System.Diagnostics;
using Xunit;
using static PixCodec.Tests.TestUtil;

namespace PixCodec.Tests;

/// <summary>Radiance HDR, Netpbm (PNM, PAM, PFM), QOI and ICO / CUR: fixtures (tests/oracle/gen_misc.py), round trips, oracles.</summary>
public class MiscFormatTests
{
    public static IEnumerable<object[]> Fixtures
        => Files("hdr", ".hdr").Concat(Files("pnm", ".pbm", ".pgm", ".ppm", ".pam", ".pfm")).Concat(Files("qoi", ".qoi"));

    [Theory, MemberData(nameof(Fixtures))]
    public void DecodeMatchesReference(string file)
    {
        var data = File.ReadAllBytes(Fixture(file));
        var expected = LoadRef(Fixture(file) + ".ref");
        var img = ImageCodec.Decode(data);
        AssertEqual(expected, img, file.EndsWith("xyze.hdr") ? 1e-5 * MaxAbs(expected) : 0);
        var info = ImageCodec.GetInfo(data);
        Assert.Equal((img.Width, img.Height, img.Channels, img.SampleType, img.Color), (info.Width, info.Height, info.Channels, info.SampleType, info.Color));
        Assert.Equal(info.Format, ImageCodec.FormatFromExtension(file));
        Assert.Equal(info.Transfer, img.Metadata!.Transfer);
        Assert.Equal(info, ImageCodec.GetInfo(new MemoryStream(data)) with { Metadata = info.Metadata });
        using (var s = new InfoTests.CountingStream(new MemoryStream(data), seekable: false))
            Assert.Equal(info, ImageCodec.GetInfo(s) with { Metadata = info.Metadata });
    }

    static double MaxAbs(RawImage img) => ToDoubles(img).Max(Math.Abs);

    // ------------------------------------------------------------------ HDR

    /// <summary>A real HDRI (Poly Haven, CC0) against a hash of what an independent numpy decoder makes of it.</summary>
    [TestDataFact("fixtures/hdr/studio_small_09_1k.hdr")]
    public void HdrRealWorld()
    {
        var path = TestData("fixtures/hdr/studio_small_09_1k.hdr");
        var img = Hdr.Decode(File.ReadAllBytes(path));
        Assert.Equal((1024, 512, ColorModel.Rgb), (img.Width, img.Height, img.Color));
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(img.GetBytes())).ToLowerInvariant();
        Assert.Equal(File.ReadAllText(path + ".sha256").Trim(), hash);
        // and it survives our encoder exactly
        AssertEqual(img, Hdr.Decode(Hdr.Encode(img)));
    }

    [Fact]
    public void HdrHeaderValues()
    {
        var h = Hdr.ReadHeader(File.ReadAllBytes(Fixture("hdr/header.hdr")));
        Assert.Equal("RADIANCE", h.Program);
        Assert.Equal(1.0, h.Exposure, 12);
        Assert.Equal(2.2, h.Gamma);
        Assert.Equal(new[] { 0.64, 0.33, 0.29, 0.6, 0.15, 0.06, 0.3333, 0.3333 }, h.Primaries);
        Assert.Equal("-Y 23 +X 41", h.Resolution);
        Assert.Contains("SOFTWARE=gen_misc", h.Lines);
        Assert.Equal(TransferFunction.Linear, Hdr.GetInfo(File.ReadAllBytes(Fixture("hdr/rle.hdr"))).Transfer);
    }

    [Fact]
    public void HdrExposureOption()
    {
        var src = RandomFloat(17, 9, 3, 1);
        var data = Hdr.Encode(src, new HdrEncodeOptions { Exposure = 4 });
        Assert.Equal(4, Hdr.ReadHeader(data).Exposure);
        var plain = Hdr.Decode(data).GetData<float>();
        var scaled = Hdr.Decode(data, new HdrDecodeOptions { ApplyExposure = true }).GetData<float>();
        for (int i = 0; i < plain.Length; i++) Assert.Equal(plain[i] / 4, scaled[i]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HdrRoundTrip(bool rle)
    {
        // arbitrary floats: within half a mantissa step of the largest component
        var src = RandomFloat(300, 7, 3, 2);
        var f = src.GetData<float>();
        f[0] = 0; f[1] = 1e30f; f[5] = -3; f[6] = float.NaN;
        var enc = Hdr.Encode(src, new HdrEncodeOptions { Rle = rle });
        var dec = Hdr.Decode(enc);
        var d = dec.GetData<float>();
        for (int p = 0; p < 300 * 7; p++)
        {
            double max = Math.Max(Clean(f[p * 3]), Math.Max(Clean(f[p * 3 + 1]), Clean(f[p * 3 + 2])));
            for (int c = 0; c < 3; c++) Assert.True(Math.Abs(d[p * 3 + c] - Clean(f[p * 3 + c])) <= max / 256 + 1e-37, $"pixel {p}");
        }
        // decoded values survive a second round trip exactly
        AssertEqual(dec, Hdr.Decode(Hdr.Encode(dec, new HdrEncodeOptions { Rle = rle })));
        // gray, alpha, 8 bit sRGB input
        var ga = new RawImage(9, 9, ColorModel.GrayAlpha, RandomFloat(9, 9, 2, 3).Data, 2);
        var gray = Hdr.Decode(Hdr.Encode(ga)).GetData<float>();
        Assert.Equal(ColorModel.Rgb, Hdr.Decode(Hdr.Encode(ga)).Color);
        Assert.Equal(gray[3], gray[4]);
        var u8 = new RawImage(2, 1, ColorModel.Rgb, new byte[] { 255, 128, 0, 10, 20, 30 });
        var lin = Hdr.Decode(Hdr.Encode(u8)).GetData<float>();
        Assert.Equal(1, lin[0], 2);
        Assert.Equal(0.2158, lin[1], 2);   // sRGB 128 in linear light
    }

    static RawImage Clamp01(RawImage img)
    {
        var f = (float[])img.GetData<float>().Clone();
        for (int i = 0; i < f.Length; i++) f[i] = Math.Clamp(f[i], 0, 1);
        return new RawImage(img.Width, img.Height, img.Color, f, img.Channels);
    }

    static double Clean(float v) => float.IsNaN(v) || v < 0 ? 0 : Math.Min(v, Math.ScaleB(255.0 / 256, 127));

    [Fact]
    public void HdrOracleReadsOurFiles()
    {
        // ImageMagick reads our RLE files the same way (the build is not HDRI: values are clamped to 1)
        if (!ToolAvailable("magick")) return;
        var src = RandomFloat(40, 20, 3, 4, 1);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".hdr");
        try
        {
            File.WriteAllBytes(path, Hdr.Encode(src));
            var pfm = RunTool("magick", $"\"{path}\" -depth 32 -define quantum:format=floating-point pfm:-");
            var theirs = Pfm.Decode(pfm);
            AssertEqual(Clamp01(Hdr.Decode(File.ReadAllBytes(path))), theirs, 1.0 / 65535);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void HdrTruncatedAndBroken()
    {
        var data = File.ReadAllBytes(Fixture("hdr/rle.hdr"));
        var img = Hdr.Decode(data.AsSpan(0, data.Length / 2));
        Assert.Equal(41, img.Width);
        Assert.Throws<ImageFormatException>(() => Hdr.Decode("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y 2 +Z 3\n"u8));
        Assert.Throws<ImageFormatException>(() => Hdr.Decode("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n"u8));
        Assert.Throws<ImageNotSupportedException>(() => Hdr.Decode("#?RADIANCE\nFORMAT=16-bit\n\n-Y 2 +X 3\n"u8));
        // a run that would cross the end of a scanline
        var bad = "#?RADIANCE\n\n-Y 1 +X 8\n"u8.ToArray().Concat(new byte[] { 2, 2, 0, 8, 200, 1 }).ToArray();
        Assert.Throws<ImageFormatException>(() => Hdr.Decode(bad));
    }

    // ------------------------------------------------------------------ Netpbm

    public static IEnumerable<object[]> PnmCases()
    {
        foreach (var t in new[] { SampleType.UInt8, SampleType.UInt16 })
            foreach (var c in new[] { ColorModel.Gray, ColorModel.GrayAlpha, ColorModel.Rgb, ColorModel.Rgba })
                foreach (var plain in new[] { false, true })
                    if (!(plain && (c is ColorModel.GrayAlpha or ColorModel.Rgba)))
                        yield return new object[] { t, c, plain };
    }

    [Theory, MemberData(nameof(PnmCases))]
    public void PnmRoundTrip(SampleType t, ColorModel c, bool plain)
    {
        var src = RandomInt(13, 7, c, t, 5);
        var enc = Pnm.Encode(src, new PnmEncodeOptions { Plain = plain });
        AssertEqual(src, Pnm.Decode(enc));
        // Pillow reads the binary PGM / PPM variants
        if (!plain && t == SampleType.UInt8 && c is ColorModel.Gray or ColorModel.Rgb && OracleDecode(enc, c == ColorModel.Gray ? ".pgm" : ".ppm") is { } o)
            Assert.Equal(ToDoubles(src), ToDoubles(o));
        // netpbm reads every variant
        if (ToolAvailable("pamtopnm") && !plain && src.HasAlpha)
        {
            var back = Pnm.Decode(RunTool("pamtopnm", null, enc));
            Assert.Equal(src.Channels - 1, back.Channels);
        }
    }

    [Fact]
    public void PnmBilevelAndAlphaDrop()
    {
        var g = RandomInt(19, 5, ColorModel.Gray, SampleType.UInt8, 6);
        foreach (bool plain in new[] { false, true })
        {
            var enc = Pnm.Encode(g, new PnmEncodeOptions { Bilevel = true, Plain = plain });
            Assert.Equal(plain ? (byte)'1' : (byte)'4', enc[1]);
            var dec = Pnm.Decode(enc).GetData<byte>();
            var s = g.GetData<byte>();
            for (int i = 0; i < s.Length; i++) Assert.Equal(s[i] >= 128 ? 255 : 0, dec[i]);
        }
        var rgba = RandomInt(4, 4, ColorModel.Rgba, SampleType.UInt8, 7);
        var noAlpha = Pnm.Decode(Pnm.Encode(rgba, new PnmEncodeOptions { KeepAlpha = false }));
        Assert.Equal(ColorModel.Rgb, noAlpha.Color);
        // maxval kept as stored when asked
        var raw = Pnm.Decode(File.ReadAllBytes(Fixture("pnm/maxval100.pgm")), new PnmDecodeOptions { ScaleToFullRange = false });
        Assert.True(raw.GetData<byte>().Max() <= 100);
        Assert.Equal(7, Pnm.GetInfo(File.ReadAllBytes(Fixture("pnm/maxval100.pgm"))).BitsPerSample);
        Assert.Equal(ColorModel.Other, Pnm.GetInfo(File.ReadAllBytes(Fixture("pnm/depth5.pam"))).Color);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PfmRoundTrip(bool bigEndian)
    {
        foreach (var c in new[] { ColorModel.Gray, ColorModel.Rgb })
        {
            var src = RandomFloat(11, 6, c == ColorModel.Gray ? 1 : 3, 8);
            var enc = Pfm.Encode(src, new PfmEncodeOptions { BigEndian = bigEndian });
            AssertEqual(src, Pfm.Decode(enc));
            // ImageMagick reads it (and clamps to [0, 1]: this build is not HDRI)
            if (ToolAvailable("magick"))
                AssertEqual(Clamp01(src), Pfm.Decode(RunTool("magick", "pfm:- -depth 32 -define quantum:format=floating-point pfm:-", enc)), 1.0 / 65535);   // (Q16: 16 bit steps)
        }
    }

    [Fact]
    public void PnmBroken()
    {
        Assert.Throws<ImageFormatException>(() => Pnm.Decode("P6\n10"u8));
        Assert.Throws<ImageFormatException>(() => Pnm.Decode("P5\n2 2\n70000\n"u8));
        Assert.Throws<ImageFormatException>(() => Pnm.Decode("P7\nWIDTH 2\nENDHDR\n"u8));
        Assert.Throws<ImageFormatException>(() => Pfm.Decode("PF\n2 2\n0\n"u8));
        // truncated data: zeros
        var img = Pnm.Decode("P5\n2 2\n255\n\x01\x02"u8);
        Assert.Equal(new byte[] { 1, 2, 0, 0 }, img.GetData<byte>());
    }

    // ------------------------------------------------------------------ QOI

    [Theory]
    [InlineData(ColorModel.Rgb)]
    [InlineData(ColorModel.Rgba)]
    [InlineData(ColorModel.Gray)]
    [InlineData(ColorModel.GrayAlpha)]
    public void QoiRoundTrip(ColorModel c)
    {
        var src = RandomInt(70, 30, c, SampleType.UInt8, 9, smooth: true);
        var enc = Qoi.Encode(src);
        var dec = Qoi.Decode(enc);
        var expected = src.Color switch
        {
            ColorModel.Gray => Expand(src, false),
            ColorModel.GrayAlpha => Expand(src, true),
            _ => src,
        };
        AssertEqual(expected, dec);
        if (OracleDecode(enc, ".qoi") is { } o) AssertEqual(expected, o);
    }

    [Fact]
    public void QoiTransferAndBroken()
    {
        var src = RandomInt(4, 4, ColorModel.Rgb, SampleType.UInt8, 1);
        Assert.Equal(TransferFunction.Srgb, Qoi.GetInfo(Qoi.Encode(src)).Transfer);
        src.Metadata = new ImageMetadata { Transfer = TransferFunction.Linear };
        var lin = Qoi.Encode(src);
        Assert.Equal(TransferFunction.Linear, Qoi.GetInfo(lin).Transfer);
        Assert.Equal(TransferFunction.Linear, Qoi.Decode(lin).Metadata!.Transfer);
        Assert.Equal(TransferFunction.Linear, Qoi.GetInfo(File.ReadAllBytes(Fixture("qoi/linear.qoi"))).Transfer);
        Assert.Throws<ImageFormatException>(() => Qoi.Decode("qoif\0\0\0\x01\0\0\0\x01\x05\0"u8));
        var trunc = Qoi.Decode(lin.AsSpan(0, 20));
        Assert.Equal(4, trunc.Width);
    }

    static RawImage Expand(RawImage g, bool alpha)
    {
        var s = g.GetData<byte>();
        int n = g.Width * g.Height, ch = alpha ? 4 : 3;
        var o = new byte[n * ch];
        for (int i = 0; i < n; i++)
        {
            byte v = s[i * g.Channels];
            o[i * ch] = o[i * ch + 1] = o[i * ch + 2] = v;
            if (alpha) o[i * ch + 3] = s[i * 2 + 1];
        }
        return new RawImage(g.Width, g.Height, alpha ? ColorModel.Rgba : ColorModel.Rgb, o, ch);
    }

    // ------------------------------------------------------------------ ICO / CUR

    public static IEnumerable<object[]> IcoFixtures => Files("ico", ".ico", ".cur");

    [Theory, MemberData(nameof(IcoFixtures))]
    public void IcoEntriesMatchReference(string file)
    {
        var data = File.ReadAllBytes(Fixture(file));
        var all = ImageCodec.DecodeAll(data);
        var entries = Ico.ReadEntries(data);
        Assert.Equal(entries.Count, all.Length);
        for (int i = 0; i < all.Length; i++)
        {
            var expected = LoadRef(Fixture(file) + $".{i}.ref");
            var img = all[i].Channels == 4 && all[i].SampleType == SampleType.UInt8 ? all[i] : null;
            Assert.NotNull(img);
            AssertEqual(expected, img!);
            Assert.Equal((entries[i].Width, entries[i].Height), (img!.Width, img.Height));
        }
        var info = ImageCodec.GetInfo(data);
        Assert.Equal(ImageFormat.Ico, info.Format);
        Assert.Equal(entries.Count, info.FrameCount);
        var top = ImageCodec.Decode(data);
        Assert.Equal(entries.Max(e => e.Width * e.Height), top.Width * top.Height);
        Assert.Equal((top.Width, top.Height), (info.Width, info.Height));
    }

    [Fact]
    public void IcoRoundTrip()
    {
        var images = new[] { 16, 32, 48, 256 }.Select(s => RandomInt(s, s, ColorModel.Rgba, SampleType.UInt8, s)).ToArray();
        foreach (var img in images)
        {
            var a = img.GetData<byte>();
            for (int i = 3; i < a.Length; i += 4) if (a[i] < 40) a[i] = 0;   // some fully transparent pixels
        }
        var enc = Ico.Encode(images);
        var entries = Ico.ReadEntries(enc);
        Assert.Equal(new[] { false, false, false, true }, entries.Select(e => e.IsPng));
        var dec = Ico.DecodeAll(enc);
        for (int i = 0; i < images.Length; i++) AssertEqual(images[i], dec[i]);
        // Pillow takes the largest
        if (OracleDecode(enc, ".ico") is { } o) AssertEqual(images[3], o);
        // the bitmap entries read by Pillow too
        var small = Ico.Encode(new[] { images[1] });
        if (OracleDecode(small, ".ico") is { } o2) AssertEqual(images[1], o2);

        var cur = Ico.Encode(new[] { images[0] }, new IcoEncodeOptions { Cursor = true, Hotspots = new[] { (3, 5) } });
        var ce = Ico.ReadEntries(cur)[0];
        Assert.Equal((3, 5), (ce.HotspotX, ce.HotspotY));
        AssertEqual(images[0], Ico.Decode(cur));
        Assert.Equal(1, Ico.Decode(enc, new IcoDecodeOptions { Index = 1 }).Width / 32);
    }

    // ------------------------------------------------------------------ helpers

    internal static RawImage RandomFloat(int w, int h, int ch, int seed, double range = 1000)
    {
        var r = new Random(seed);
        var f = new float[w * h * ch];
        for (int i = 0; i < f.Length; i++) f[i] = (float)(Math.Pow(2, r.NextDouble() * 20 - 10) * (r.NextDouble() < 0.1 ? 0 : 1) * range / 1000);
        return new RawImage(w, h, RawImage.DefaultColorModel(ch), f, ch);
    }

    internal static RawImage RandomInt(int w, int h, ColorModel c, SampleType t, int seed, bool smooth = false)
    {
        var r = new Random(seed);
        var img = RawImage.Create(w, h, c, t);
        int n = img.Data.Length;
        for (int i = 0; i < n; i++)
        {
            double v = smooth ? ((i / img.Channels % w) / (double)w * 0.7 + (i / img.Channels / w) / (double)h * 0.3) : r.NextDouble();
            if (smooth && r.NextDouble() < 0.02) v = r.NextDouble();
            switch (img.Data)
            {
                case byte[] b: b[i] = (byte)(v * 255); break;
                case ushort[] u: u[i] = (ushort)(v * 65535); break;
            }
        }
        return img;
    }

    internal static bool ToolAvailable(string tool)
    {
        try { return RunTool("sh", $"-c \"command -v {tool}\"").Length > 0; } catch { return false; }
    }

    internal static byte[] RunTool(string exe, string? args, byte[]? stdin = null)
    {
        var psi = new ProcessStartInfo(exe, args ?? "") { RedirectStandardOutput = true, RedirectStandardInput = stdin != null, RedirectStandardError = true, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        var ms = new MemoryStream();
        var copy = p.StandardOutput.BaseStream.CopyToAsync(ms);
        var err = p.StandardError.ReadToEndAsync();
        if (stdin != null) { p.StandardInput.BaseStream.Write(stdin); p.StandardInput.Close(); }
        copy.Wait();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"{exe} failed: {err.Result}");
        return ms.ToArray();
    }
}
