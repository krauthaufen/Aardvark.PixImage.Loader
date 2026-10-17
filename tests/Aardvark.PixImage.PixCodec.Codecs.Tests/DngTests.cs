using System.Text.Json;
using Aardvark.ImageCodecs;
using Xunit;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>
/// DNG: the synthetic fixtures of tests/oracle/gen_dng.py (stored samples against tifffile, linear values against the
/// generator's model of chapters 5 / 7, colours against LibRaw and the model of chapter 6) and the camera files that
/// tests/oracle/fetch_dng_samples.py downloads into the test-data repository (stored samples against tifffile and LibRaw,
/// rendering against LibRaw, metadata against ExifTool).
/// </summary>
public class DngTests
{
    public static IEnumerable<object[]> Fixtures => TestUtil.Files("dng", ".dng");

    static string F(string name) => TestUtil.Fixture(Path.Combine("dng", name));

    static JsonElement Json(string path) => JsonDocument.Parse(File.ReadAllText(path + ".json")).RootElement;

    static int[] Ints(JsonElement e) => e.EnumerateArray().Select(x => x.GetInt32()).ToArray();

    static readonly DngDecodeOptions LinearSrgb = new() { OutputType = SampleType.Float32 };

    // ------------------------------------------------------------------------------------------ synthetic fixtures

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void StoredSamplesMatchReference(string file)
    {
        var path = TestUtil.Fixture(file);
        if (!File.Exists(path + ".ref")) return;
        var bytes = File.ReadAllBytes(path);
        Assert.True(Dng.IsMatch(bytes));
        Assert.Equal(ImageFormat.Dng, ImageCodec.DetectFormat(bytes));
        var raw = Dng.DecodeRaw(bytes);
        var expected = TestUtil.LoadRef(path + ".ref");
        var img = raw.Image;
        if (expected.SampleType != img.SampleType)
        {
            // tifffile returns 12 / 14-bit samples as UInt16 like we do; floats keep their width
            Assert.Fail($"{file}: sample type {img.SampleType}, reference {expected.SampleType}");
        }
        // lossy JPEG: the IDCT may round differently from libjpeg-turbo
        double tol = raw.Info.Compression == 34892 ? 1 : 0;
        TestUtil.AssertEqual(expected, new RawImage(img.Width, img.Height, expected.Color, img.Data, img.Channels), tol);
        using var ms = new MemoryStream(bytes);
        TestUtil.AssertEqual(img, Dng.DecodeRaw(ms).Image);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void LinearValuesMatchModel(string file)
    {
        var path = TestUtil.Fixture(file);
        if (!File.Exists(path + ".lin.ref")) return;
        var raw = Dng.DecodeRaw(File.ReadAllBytes(path), DngRawStage.Linear);
        var expected = TestUtil.LoadRef(path + ".lin.ref");
        Assert.Equal(SampleType.Float32, raw.Image.SampleType);
        TestUtil.AssertEqual(expected, new RawImage(raw.Image.Width, raw.Image.Height, expected.Color, raw.Image.Data, raw.Image.Channels), 2e-5);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void InfoMatchesFixture(string file)
    {
        var path = TestUtil.Fixture(file);
        if (!File.Exists(path + ".json")) return;
        var bytes = File.ReadAllBytes(path);
        var j = Json(path);
        var info = Dng.ReadInfo(bytes);
        Assert.Equal(j.GetProperty("width").GetInt32(), info.RawWidth);
        Assert.Equal(j.GetProperty("height").GetInt32(), info.RawHeight);
        Assert.Equal(j.GetProperty("bits").GetInt32(), info.BitsPerSample);
        Assert.Equal(j.GetProperty("layout").GetString() == "cfa" ? DngRawLayout.ColorFilterArray : DngRawLayout.LinearRaw, info.Layout);
        Assert.Equal(j.GetProperty("planes").GetInt32(), info.ColorPlanes);
        Assert.Equal(new byte[] { 1, 7, 1, 0 }.Length, info.Version.Length);
        Assert.Equal("PixCodec", info.Make);
        if (j.TryGetProperty("cfa", out var cfa)) Assert.Equal(Ints(cfa).Select(x => (byte)x).ToArray(), info.CfaPattern);
        if (j.TryGetProperty("active", out var a))
        {
            var r = Ints(a);
            Assert.Equal(new DngRect(r[0], r[1], r[2], r[3]), info.ActiveArea);
        }
        var gi = ImageCodec.GetInfo(bytes);
        Assert.Equal(ImageFormat.Dng, gi.Format);
        if (j.TryGetProperty("final", out var fin))
        {
            var fw = Ints(fin);
            Assert.Equal((fw[0], fw[1]), (info.FinalWidth, info.FinalHeight));
            Assert.Equal((fw[0], fw[1]), (gi.Width, gi.Height));
        }
        if (j.TryGetProperty("final_upright", out var fu))
        {
            var fw = Ints(fu);
            Assert.Equal((fw[0], fw[1]), (gi.Width, gi.Height));
            Assert.True(gi.OrientationApplied);
        }
        if (j.TryGetProperty("opcodes", out var ops))
            Assert.Equal(Ints(ops), new[] { info.OpcodeList1.Count, info.OpcodeList2.Count, info.OpcodeList3.Count });
        if (j.TryGetProperty("baseline_exposure", out var be)) Assert.Equal(be.GetDouble(), info.BaselineExposure, 4);
        if (j.TryGetProperty("temperatures", out var ts))
            Assert.Equal(ts.EnumerateArray().Select(x => x.GetDouble()).ToArray(), info.Calibrations.Select(c => c.Temperature).ToArray());
    }

    /// <summary>Mean of each patch's interior (a margin of 4 pixels stays out of the demosaicing's reach), one pixel per patch.</summary>
    static double[,,] PatchMeans(RawImage img, int pw, int ph, int cols, int rows, int x0 = 0, int y0 = 0)
    {
        var d = TestUtil.ToDoubles(img);
        int c = img.Channels;
        var r = new double[rows, cols, c];
        for (int py = 0; py < rows; py++)
            for (int px = 0; px < cols; px++)
            {
                int n = 0;
                for (int y = py * ph + 4 - y0; y < (py + 1) * ph - 4 - y0; y++)
                    for (int x = px * pw + 4 - x0; x < (px + 1) * pw - 4 - x0; x++)
                    {
                        if (x < 0 || y < 0 || x >= img.Width || y >= img.Height) continue;
                        for (int k = 0; k < c; k++) r[py, px, k] += d[((long)y * img.Width + x) * c + k];
                        n++;
                    }
                for (int k = 0; k < c; k++) r[py, px, k] /= Math.Max(1, n);
            }
        return r;
    }

    static void AssertPatches(RawImage expected, double[,,] actual, double tol, string what)
    {
        var e = TestUtil.ToDoubles(expected);
        int rows = actual.GetLength(0), cols = actual.GetLength(1), c = actual.GetLength(2);
        Assert.Equal((cols, rows), (expected.Width, expected.Height));
        double worst = 0;
        for (int y = 0; y < rows; y++)
            for (int x = 0; x < cols; x++)
                for (int k = 0; k < c; k++) worst = Math.Max(worst, Math.Abs(e[(y * cols + x) * c + k] - actual[y, x, k]));
        Assert.True(worst <= tol, $"{what}: patch colour difference {worst:F5} > {tol}");
    }

    [Fact]
    public void FlatPatchesMatchLibRaw()
    {
        // one D65 calibration and a D65 as-shot neutral: LibRaw's colour pipeline is then the specification's
        var path = F("bayer16_rggb.dng");
        var j = Json(path);
        var pr = Ints(j.GetProperty("patch"));
        var bytes = File.ReadAllBytes(path);
        var img = Dng.Decode(bytes, new DngDecodeOptions { OutputType = SampleType.Float32, ApplyDefaultCrop = false });
        var rawpy = TestUtil.LoadRef(path + ".rawpy.ref");
        Assert.Equal((rawpy.Width, rawpy.Height), (img.Width, img.Height));
        var ours = PatchMeans(img, pr[0], pr[1], 6, 4);
        var theirs = PatchMeans(rawpy, pr[0], pr[1], 6, 4);
        // LibRaw scales its output by its own exposure convention (here 1.32 times brighter than the DNG definition, where
        // the as-shot neutral at the white level is Y = 1): normalize by the mid grey patches, then compare colours
        double ratio = 0;
        for (int x = 1; x <= 3; x++) for (int k = 0; k < 3; k++) ratio += theirs[3, x, k] / ours[3, x, k] / 9;
        Assert.InRange(ratio, 1.0, 1.5);
        double worst = 0;
        for (int y = 0; y < 4; y++) for (int x = 0; x < 6; x++) for (int k = 0; k < 3; k++) worst = Math.Max(worst, Math.Abs(Math.Clamp(ours[y, x, k] * ratio, 0, 1) - Math.Clamp(theirs[y, x, k], 0, 1)));
        Assert.True(worst <= 0.002, $"patch colours differ from LibRaw by {worst:F5}");
        // and against the model of chapter 6 (unclipped)
        AssertPatches(TestUtil.LoadRef(path + ".color.ref"), ours, 0.0005, "model");
        // the default crop moves the image by the crop origin
        var cropped = Dng.Decode(bytes, LinearSrgb);
        var c = Ints(j.GetProperty("crop"));
        AssertPatches(TestUtil.LoadRef(path + ".color.ref"), PatchMeans(cropped, pr[0], pr[1], 6, 4, c[0], c[1]), 0.0005, "cropped");
    }

    [Fact]
    public void DualIlluminantForwardMatricesMatchModel()
    {
        // two calibrations interpolated by inverse temperature, forward matrices, big endian 12-bit packed data through a
        // linearization table with BlackLevelDeltaH / V, baseline exposure +0.5 EV
        var path = F("bayer12_be_lin_dual.dng");
        var j = Json(path);
        var pr = Ints(j.GetProperty("patch"));
        var img = Dng.Decode(File.ReadAllBytes(path), LinearSrgb);
        AssertPatches(TestUtil.LoadRef(path + ".color.ref"), PatchMeans(img, pr[0], pr[1], 4, 6), 0.004, "dual illuminant");
    }

    [Fact]
    public void XTransFlatPatches()
    {
        var path = F("xtrans16.dng");
        var j = Json(path);
        var pr = Ints(j.GetProperty("patch"));
        foreach (var alg in new[] { DngDemosaicAlgorithm.Directional, DngDemosaicAlgorithm.Bilinear })
        {
            var img = Dng.Decode(File.ReadAllBytes(path), new DngDecodeOptions { OutputType = SampleType.Float32, Demosaic = alg });
            AssertPatches(TestUtil.LoadRef(path + ".color.ref"), PatchMeans(img, pr[0], pr[1], 6, 4), 0.0005, alg.ToString());
        }
    }

    [Fact]
    public void OpcodeList3TrimAndOrientation()
    {
        // FixVignetteRadial, WarpRectilinear, TrimBounds on LinearRaw data without colour profile, orientation 6
        var path = F("linearraw_list3_orient6.dng");
        var img = Dng.Decode(File.ReadAllBytes(path), LinearSrgb);
        var expected = TestUtil.LoadRef(path + ".render.ref");
        TestUtil.AssertEqual(expected, img, 2e-4);
        Assert.Equal(1, img.Metadata!.Orientation);
        var stored = Dng.Decode(File.ReadAllBytes(path), new DngDecodeOptions { OutputType = SampleType.Float32, ApplyOrientation = false });
        Assert.Equal((expected.Height, expected.Width), (stored.Width, stored.Height));
        Assert.Equal(6, stored.Metadata!.Orientation);
    }

    [Fact]
    public void Monochrome()
    {
        var path = F("mono12.dng");
        var bytes = File.ReadAllBytes(path);
        var img = Dng.Decode(bytes, LinearSrgb);
        Assert.Equal(ColorModel.Gray, img.Color);
        TestUtil.AssertEqual(TestUtil.LoadRef(path + ".render.ref"), img, 1e-5);
        var gi = Dng.GetInfo(bytes);
        Assert.Equal((1, ColorModel.Gray), (gi.Channels, gi.Color));
        var u16 = Dng.Decode(bytes);
        Assert.Equal((SampleType.UInt16, 1), (u16.SampleType, u16.Channels));
        Assert.Equal(TransferFunction.Srgb, u16.Metadata!.Transfer);
    }

    [Fact]
    public void DemosaicQuality()
    {
        // a known full colour image, mosaiced: the directional algorithm must beat bilinear clearly
        var path = F("demosaic_truth.dng");
        var truth = TestUtil.LoadRef(path + ".truth.ref");
        var bytes = File.ReadAllBytes(path);
        RawImage Run(DngDemosaicAlgorithm a) => Dng.Decode(bytes, new DngDecodeOptions { OutputType = SampleType.Float32, ColorSpace = DngColorSpace.Camera, Demosaic = a });
        var dir = Run(DngDemosaicAlgorithm.Directional);
        var bil = Run(DngDemosaicAlgorithm.Bilinear);
        double pd = TestUtil.Psnr(truth, Clip(dir), 1), pb = TestUtil.Psnr(truth, Clip(bil), 1);
        Assert.True(pd > pb + 3, $"directional {pd:F2} dB, bilinear {pb:F2} dB");
        Assert.True(pd > 30, $"directional {pd:F2} dB");
        Assert.True(pb > 25, $"bilinear {pb:F2} dB");
        // the result does not depend on threading
        TestUtil.AssertEqual(dir, Dng.Decode(bytes, new DngDecodeOptions { OutputType = SampleType.Float32, ColorSpace = DngColorSpace.Camera, Parallel = false }));
    }

    static RawImage Clip(RawImage f)
    {
        var d = (float[])f.GetData<float>().Clone();
        for (int i = 0; i < d.Length; i++) d[i] = Math.Clamp(d[i], 0, 1);
        return new RawImage(f.Width, f.Height, f.Color, d, f.Channels);
    }

    [Fact]
    public void UnknownRequiredOpcodeFails()
    {
        var bytes = File.ReadAllBytes(F("unknown_required_opcode.dng"));
        var info = Dng.ReadInfo(bytes);
        Assert.False(info.OpcodeList2[0].Supported);
        Assert.Throws<ImageNotSupportedException>(() => Dng.Decode(bytes));
        Assert.Equal(16, Dng.Decode(bytes, new DngDecodeOptions { ApplyOpcodes = false }).Width);
    }

    [Fact]
    public void Previews()
    {
        var path = F("lossy_linearraw_subifd.dng");
        var bytes = File.ReadAllBytes(path);
        var pv = Dng.GetPreviews(bytes);
        Assert.Single(pv);
        Assert.Equal((30, 24, true), (pv[0].Width, pv[0].Height, pv[0].IsJpegFile));
        Assert.Equal(File.ReadAllBytes(path + ".preview.jpg"), Dng.ExtractPreviewJpeg(bytes));
        var img = Dng.DecodePreview(bytes)!;
        Assert.Equal((30, 24, 3), (img.Width, img.Height, img.Channels));
        TestUtil.AssertEqual(Jpeg.Decode(File.ReadAllBytes(path + ".preview.jpg")), new RawImage(img.Width, img.Height, ColorModel.Rgb, img.Data), 2);
        // the raw image itself sits in a SubIFD
        var info = Dng.ReadInfo(bytes);
        Assert.Equal(new[] { 0 }, info.RawSubImagePath);
        Assert.Equal((80, 64), (Dng.Decode(bytes).Width, Dng.Decode(bytes).Height));
        Assert.Null(Dng.DecodePreview(File.ReadAllBytes(F("mono12.dng"))));
    }

    [Fact]
    public void FormatDetectionAndStreams()
    {
        var path = F("bayer16_rggb.dng");
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(ImageFormat.Dng, ImageCodec.FormatFromExtension("x.DNG"));
        var info = ImageCodec.GetInfo(path);
        Assert.Equal((ImageFormat.Dng, 92, 60, 3, SampleType.UInt16), (info.Format, info.Width, info.Height, info.Channels, info.SampleType));
        Assert.Equal("PixCodec", info.Metadata?.Make);
        using (var fs = File.OpenRead(path)) Assert.Equal(info, ImageCodec.GetInfo(fs));
        // a seekable stream that is not a file, and one whose first 8 KB do not reach the end of IFD 0
        using (var ms = new MemoryStream(bytes)) Assert.Equal(info, ImageCodec.GetInfo(ms));
        var dec = ImageCodec.Decode(bytes);
        Assert.Equal((92, 60, SampleType.UInt16), (dec.Width, dec.Height, dec.SampleType));
        using (var fs = File.OpenRead(path)) TestUtil.AssertEqual(dec, Dng.Decode(fs));
        // a TIFF without DNGVersion stays a TIFF, also when only a prefix is looked at
        Assert.Equal(ImageFormat.Tiff, ImageCodec.DetectFormat(bytes.AsSpan(0, 16)));
        var tiff = new MemoryStream();
        Tiff.Encode(RawImage.Create(4, 4, ColorModel.Gray, SampleType.UInt8), tiff);
        Assert.Equal(ImageFormat.Tiff, ImageCodec.DetectFormat(tiff.ToArray()));
        Assert.False(Dng.IsMatch(tiff.ToArray()));
        Assert.Throws<ImageFormatException>(() => Dng.ReadInfo(tiff.ToArray()));
    }

    [Fact]
    public void Options()
    {
        var bytes = File.ReadAllBytes(F("bayer16_rggb.dng"));
        var u8 = Dng.Decode(bytes, new DngDecodeOptions { OutputType = SampleType.UInt8 });
        var u16 = Dng.Decode(bytes);
        Assert.Equal(SampleType.UInt8, u8.SampleType);
        var a = u8.GetData<byte>(); var b = u16.GetData<ushort>();
        for (int i = 0; i < a.Length; i++) Assert.True(Math.Abs(a[i] - b[i] / 257.0) <= 0.51);
        // +1 EV doubles the linear values (below clipping)
        var f0 = Dng.Decode(bytes, LinearSrgb).GetData<float>();
        var f1 = Dng.Decode(bytes, new DngDecodeOptions { OutputType = SampleType.Float32, ExposureCompensation = 1 }).GetData<float>();
        for (int i = 0; i < f0.Length; i += 97) Assert.Equal(2 * f0[i], f1[i], 4);
        // a custom white balance equal to the as-shot one changes nothing; XYZ output maps back to the sRGB result
        var info = Dng.ReadInfo(bytes);
        var same = Dng.Decode(bytes, new DngDecodeOptions { OutputType = SampleType.Float32, WhiteBalanceNeutral = info.AsShotNeutral });
        TestUtil.AssertEqual(Dng.Decode(bytes, LinearSrgb), same, 1e-6);
        var xyz = Dng.Decode(bytes, new DngDecodeOptions { OutputType = SampleType.Float32, ColorSpace = DngColorSpace.XyzD50 });
        Assert.Equal(TransferFunction.Linear, xyz.Metadata!.Transfer);
        Assert.Throws<ArgumentException>(() => Dng.Decode(bytes, new DngDecodeOptions { WhiteBalanceNeutral = new[] { 1.0 } }));
        Assert.Throws<ArgumentException>(() => Dng.Decode(bytes, new DngDecodeOptions { OutputType = SampleType.Int16 }));
    }

    [Fact]
    public void CorruptFilesFailCleanly()
    {
        var bytes = File.ReadAllBytes(F("ljpeg14_tiles.dng"));
        var rnd = new Random(3);
        for (int i = 0; i < 60; i++)
        {
            var b = (byte[])bytes.Clone();
            int n = rnd.Next(1, 12);
            for (int k = 0; k < n; k++) b[rnd.Next(8, b.Length)] = (byte)rnd.Next(256);
            if (i % 3 == 0) Array.Resize(ref b, rnd.Next(16, b.Length));
            try { Dng.Decode(b); }
            catch (ImageFormatException) { }
            catch (ImageNotSupportedException) { }
        }
    }
}
