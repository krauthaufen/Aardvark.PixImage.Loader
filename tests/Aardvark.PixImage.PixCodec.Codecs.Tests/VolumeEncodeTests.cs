using Xunit;
using static Aardvark.ImageCodecs.Tests.VolumeTestUtil;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>
/// NRRD, NIfTI, MetaImage and FITS encoders: our own round trip, and the files read by pynrrd, nibabel, SimpleITK and astropy
/// (skipped without them, see <see cref="VolumeTestUtil"/>).
/// </summary>
public class VolumeEncodeTests
{
    /// <summary>A volume with content over the type's range and a rotated, shifted, anisotropic LPS geometry.</summary>
    internal static RawVolume Sample(SampleType t, int channels = 1, int[]? sizes = null, int seed = 1)
    {
        sizes ??= new[] { 7, 5, 4 };
        var color = channels switch { 1 => ColorModel.Gray, 3 => ColorModel.Rgb, 4 => ColorModel.Rgba, _ => ColorModel.Other };
        var v = RawVolume.Create(sizes, color, t, channels);
        var rnd = new Random(seed);
        long n = v.Data.LongLength;
        var bytes = new RawImage(1, (int)n, ColorModel.Gray, v.Data).GetBytes();
        if (t is SampleType.Float32)
        {
            var f = v.GetData<float>();
            for (long i = 0; i < n; i++) f[i] = (float)(rnd.NextDouble() * 2000 - 1000);
        }
        else if (t is SampleType.Float64)
        {
            var f = v.GetData<double>();
            for (long i = 0; i < n; i++) f[i] = rnd.NextDouble() * 2e6 - 1e6;
        }
        else if (t is SampleType.Float16)
        {
            var f = v.GetData<Half>();
            for (long i = 0; i < n; i++) f[i] = (Half)(rnd.NextDouble() * 200 - 100);
        }
        else
        {
            rnd.NextBytes(bytes);
            // the extremes of the type
            int bps = RawImage.BytesPerSample(t);
            bytes.Slice(0, bps).Fill(0);
            bytes.Slice(bytes.Length - bps).Fill(0xFF);
        }
        double c = Math.Cos(0.3), s = Math.Sin(0.3);
        v.Metadata = new PhysicalMetadata
        {
            Space = "LPS",
            Spacing = sizes.Length >= 3 ? new[] { 0.5, 0.75, 2.0 }.Concat(Enumerable.Repeat(1.0, sizes.Length - 3)).ToArray() : new[] { 0.5, 0.75 },
            Origin = new[] { 10.0, -20.0, 5.5 },
            Direction = new[,] { { c, -s, 0 }, { s, c, 0 }, { 0, 0, 1 } },
            Units = Enumerable.Repeat<string?>("mm", sizes.Length).ToArray(),
        };
        return v;
    }

    static void AssertGeometry(PhysicalMetadata expected, string space, string json, int axes = 3)
    {
        // compare the affine in the oracle's space
        var e = space == "RAS" ? expected.GetVoxelToRas()! : expected.GetVoxelToLps()!;
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(json));
        var j = doc.RootElement;
        var sp = j.GetProperty("spacing").EnumerateArray().Select(x => x.GetDouble()).ToArray();
        var org = j.GetProperty("origin").EnumerateArray().Select(x => x.GetDouble()).ToArray();
        var dir = j.GetProperty("direction").EnumerateArray().Select(r => r.EnumerateArray().Select(x => x.GetDouble()).ToArray()).ToArray();
        for (int r = 0; r < 3; r++)
        {
            Assert.True(Math.Abs(e[r, 3] - org[r]) < 1e-4, $"origin {r}: {e[r, 3]} vs {org[r]}");
            for (int a = 0; a < axes; a++)
                Assert.True(Math.Abs(e[r, a] - dir[r][a] * sp[a]) < 1e-4, $"axis {a} [{r}]: {e[r, a]} vs {dir[r][a] * sp[a]}");
        }
    }

    static void RoundTrip(RawVolume src, string path, double tol = 0)
    {
        var back = ImageCodec.DecodeVolume(path);
        var expected = src.SampleType == SampleType.Float16
            ? new RawVolume(src.Sizes, src.Color, VolumeIOConvert(src), src.Channels) : src;
        AssertSame(expected, back, tol);
        // geometry survives (in the decoded file's own space)
        var e = src.Metadata!.GetVoxelToLps()!;
        var a = back.Metadata!.Space == "FITS-WCS" ? back.Metadata.GetVoxelToWorld() : back.Metadata.GetVoxelToLps()!;
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 4; c++)
                Assert.True(Math.Abs(e[r, c] - a[r, c]) < 1e-4, $"affine [{r},{c}]: {e[r, c]} vs {a[r, c]}");
    }

    static Array VolumeIOConvert(RawVolume v) => Array.ConvertAll(v.GetData<Half>(), h => (float)h);

    public static IEnumerable<object[]> AllTypes()
        => new[] { SampleType.UInt8, SampleType.Int8, SampleType.UInt16, SampleType.Int16, SampleType.UInt32, SampleType.Int32,
                   SampleType.UInt64, SampleType.Int64, SampleType.Float32, SampleType.Float64 }.Select(t => new object[] { t });

    // ------------------------------------------------------------------ NRRD

    [Theory, MemberData(nameof(AllTypes))]
    public void NrrdWrites(SampleType t)
    {
        var dir = TempDir();
        try
        {
            var v = Sample(t);
            foreach (var enc in new[] { NrrdEncoding.Raw, NrrdEncoding.Gzip, NrrdEncoding.Ascii, NrrdEncoding.Hex })
                foreach (var big in new[] { false, true })
                {
                    var p = Path.Combine(dir, $"a_{enc}_{big}.nrrd");
                    File.WriteAllBytes(p, Nrrd.Encode(v, new NrrdEncodeOptions { Encoding = enc, BigEndian = big }));
                    RoundTrip(v, p);
                    if (enc != NrrdEncoding.Hex && Oracle("nrrd", p) is var (o, json))
                    {
                        AssertSame(v, o);
                        AssertGeometry(v.Metadata!, "LPS", json);
                    }
                    if (enc is NrrdEncoding.Raw or NrrdEncoding.Gzip && t is not (SampleType.UInt64 or SampleType.Int64) && Oracle("sitk", p) is var (s, sj))
                    {
                        AssertSame(v, s);
                        AssertGeometry(v.Metadata!, "LPS", sj);
                    }
                }
            var h = Path.Combine(dir, "d.nhdr");
            ImageCodec.EncodeVolume(v, h);
            Assert.True(File.Exists(Path.Combine(dir, "d.raw.gz")));
            RoundTrip(v, h);
            if (Oracle("nrrd", h) is var (od, _)) AssertSame(v, od);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void NrrdChannelsAndFrames()
    {
        var dir = TempDir();
        try
        {
            var rgb = Sample(SampleType.UInt8, 3);
            var p = Path.Combine(dir, "rgb.nrrd");
            ImageCodec.EncodeVolume(rgb, p);
            var back = ImageCodec.DecodeVolume(p);
            Assert.Equal(ColorModel.Rgb, back.Color);
            AssertSame(rgb, back);
            if (Oracle("nrrd", p) is var (o, _)) AssertSame(rgb, o);
            if (Oracle("sitk", p) is var (s, _)) AssertSame(rgb, s);
            var t4 = Sample(SampleType.Int16, 1, new[] { 4, 3, 2, 5 });
            t4.Metadata!.Spacing[3] = 2.5;
            p = Path.Combine(dir, "t4.nrrd");
            ImageCodec.EncodeVolume(t4, p);
            var b4 = ImageCodec.DecodeVolume(p);
            AssertSame(t4, b4);
            Assert.Equal(2.5, b4.Metadata!.Spacing[3]);
            if (Oracle("nrrd", p) is var (o4, _)) AssertSame(t4, o4);
            // key/value pairs of a NRRD source survive
            var kv = ImageCodec.DecodeVolume(Path.Combine(TestUtil.FixtureDir, "volume", "nrrd", "f64_big.nrrd"));
            var again = Nrrd.DecodeVolume(Nrrd.Encode(kv));
            Assert.Equal("some value", again.Metadata!.Get("my key"));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ------------------------------------------------------------------ NIfTI

    [Theory, MemberData(nameof(AllTypes))]
    public void NiftiWrites(SampleType t)
    {
        var dir = TempDir();
        try
        {
            var v = Sample(t);
            foreach (var name in new[] { "a.nii", "a.nii.gz" })
            {
                var p = Path.Combine(dir, name);
                ImageCodec.EncodeVolume(v, p);
                if (name.EndsWith(".gz")) Assert.True(Compression.Gzip.IsGzip(File.ReadAllBytes(p)));
                RoundTrip(v, p);
                if (Oracle("nibabel", p) is var (o, json))
                {
                    AssertSame(v, o);
                    AssertGeometry(v.Metadata!, "RAS", json);
                }
                if (t is not (SampleType.UInt64 or SampleType.Int64) && Oracle("sitk", p) is var (s, sj))
                {
                    AssertSame(v, s);
                    AssertGeometry(v.Metadata!, "LPS", sj);
                }
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void NiftiChannelsAndFrames()
    {
        var dir = TempDir();
        try
        {
            foreach (var (t, c) in new[] { (SampleType.UInt8, 3), (SampleType.UInt8, 4), (SampleType.Float32, 3), (SampleType.Int16, 2) })
            {
                var v = Sample(t, c);
                var p = Path.Combine(dir, $"c{c}{t}.nii");
                ImageCodec.EncodeVolume(v, p);
                var back = ImageCodec.DecodeVolume(p);
                AssertSame(v, back);
                Assert.Equal(v.Color, back.Color);
                var h = Nifti.ReadHeader(File.ReadAllBytes(p));
                Assert.Equal(t == SampleType.UInt8 ? (c == 3 ? 128 : 2304) : t == SampleType.Float32 ? 16 : 4, h.DataType);
                if (t != SampleType.UInt8) Assert.Equal(c == 3 ? 2003 : 1007, h.IntentCode); // (RGB float data: the RGB vector intent)
                if (Oracle("nibabel", p) is var (o, _)) AssertSame(v, o);
            }
            var t4 = Sample(SampleType.Float32, 1, new[] { 4, 3, 2, 5 });
            var p4 = Path.Combine(dir, "t4.nii.gz");
            ImageCodec.EncodeVolume(t4, p4);
            AssertSame(t4, ImageCodec.DecodeVolume(p4));
            if (Oracle("nibabel", p4) is var (o4, _)) AssertSame(t4, o4);
            // a NIfTI source keeps intent, descrip and dim_info; raw (unscaled) data keep their scl_slope
            var src = ImageCodec.DecodeVolume(Path.Combine(TestUtil.FixtureDir, "volume", "nifti", "qform.nii"));
            var h2 = Nifti.ReadHeader(Nifti.Encode(src));
            Assert.Equal(1002, h2.IntentCode);
            Assert.Equal("labels", h2.IntentName);
            Assert.Equal(Nifti.ReadHeader(File.ReadAllBytes(Path.Combine(TestUtil.FixtureDir, "volume", "nifti", "qform.nii"))).DimInfo, h2.DimInfo);
            var raw = Nifti.DecodeVolume(File.ReadAllBytes(Path.Combine(TestUtil.FixtureDir, "volume", "nifti", "i16_scaled.nii")), new VolumeDecodeOptions { Scaling = DataScaling.Raw });
            var re = Nifti.Encode(raw);
            Assert.Equal(2.5, Nifti.ReadHeader(re).SclSlope);
            AssertSame(Nifti.DecodeVolume(File.ReadAllBytes(Path.Combine(TestUtil.FixtureDir, "volume", "nifti", "i16_scaled.nii"))), Nifti.DecodeVolume(re));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ------------------------------------------------------------------ MetaImage

    [Theory, MemberData(nameof(AllTypes))]
    public void MetaImageWrites(SampleType t)
    {
        var dir = TempDir();
        try
        {
            var v = Sample(t);
            foreach (var (name, compress) in new[] { ("a.mha", true), ("b.mha", false), ("c.mhd", true), ("d.mhd", false) })
            {
                var p = Path.Combine(dir, name);
                if (name.EndsWith(".mhd")) MetaImage.EncodeDetached(v, p, new MetaImageEncodeOptions { Compress = compress });
                else File.WriteAllBytes(p, MetaImage.Encode(v, new MetaImageEncodeOptions { Compress = compress }));
                RoundTrip(v, p);
                if (Oracle("sitk", p) is var (s, sj))
                {
                    AssertSame(v, s);
                    AssertGeometry(v.Metadata!, "LPS", sj);
                }
            }
            var vec = Sample(t, 3);
            var pv = Path.Combine(dir, "v.mha");
            ImageCodec.EncodeVolume(vec, pv);
            AssertSame(vec, ImageCodec.DecodeVolume(pv));
            if (Oracle("sitk", pv) is var (sv, _)) AssertSame(vec, sv);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ------------------------------------------------------------------ FITS

    [Theory, MemberData(nameof(AllTypes))]
    public void FitsWrites(SampleType t)
    {
        var dir = TempDir();
        try
        {
            foreach (var sizes in new[] { new[] { 7, 5 }, new[] { 7, 5, 4 } })
            {
                var v = Sample(t, 1, sizes);
                if (sizes.Length == 2) v.Metadata!.Direction = new[,] { { 0.8, -0.6 }, { 0.6, 0.8 }, { 0, 0 } };
                var modes = new List<FitsCompression> { FitsCompression.None, FitsCompression.Gzip, FitsCompression.Gzip2 };
                if (t is SampleType.UInt8 or SampleType.Int8 or SampleType.UInt16 or SampleType.Int16 or SampleType.UInt32 or SampleType.Int32) modes.Add(FitsCompression.Rice);
                foreach (var mode in modes)
                {
                    var p = Path.Combine(dir, $"a{sizes.Length}{mode}.fits");
                    File.WriteAllBytes(p, Fits.Encode(v, new FitsEncodeOptions { Compression = mode, TileWidth = mode == FitsCompression.Rice ? 3 : 0, TileHeight = mode == FitsCompression.Rice ? 2 : 0 }));
                    var back = Fits.DecodeVolume(File.ReadAllBytes(p));
                    AssertSame(v, back);
                    var e = v.Metadata!.GetVoxelToLps()!; var a = back.Metadata!.GetVoxelToWorld();
                    for (int r = 0; r < sizes.Length; r++)
                        for (int c = 0; c < sizes.Length; c++) Assert.True(Math.Abs(e[r, c] - a[r, c]) < 1e-9, $"WCS [{r},{c}]");
                    if (Oracle("astropy", p) is var (o, _)) AssertSame(v, o);
                }
            }
            if (t is SampleType.UInt64 or SampleType.Int64 or SampleType.Float64)
                Assert.Throws<ArgumentException>(() => Fits.Encode(Sample(t), new FitsEncodeOptions { Compression = FitsCompression.Rice }));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void FitsKeepsKeywordsAndChannels()
    {
        var src = Fits.DecodeVolume(File.ReadAllBytes(Path.Combine(TestUtil.FixtureDir, "volume", "fits", "wcs.fits")));
        var again = Fits.DecodeVolume(Fits.Encode(src));
        Assert.Equal("RA---TAN", again.Metadata!.Get("CTYPE1"));
        Assert.Equal(new string('x', 100), again.Metadata.Get("LONGSTR"));
        AssertSame(src, again);
        var a = src.Metadata!.GetVoxelToWorld(); var b = again.Metadata.GetVoxelToWorld();
        for (int r = 0; r < 2; r++) for (int c = 0; c < 4; c++) Assert.Equal(a[r, c], b[r, c], 9);
        // an RGB image becomes a cube of three planes
        var rgb = Sample(SampleType.UInt8, 3, new[] { 6, 4 });
        var cube = Fits.DecodeVolume(Fits.Encode(rgb));
        Assert.Equal(new[] { 6, 4, 3 }, cube.Sizes);
        Assert.Equal(rgb.GetData<byte>()[5 * 3 + 1], cube.GetData<byte>()[6 * 4 + 5]);
    }

    // ------------------------------------------------------------------ 2D through the image API

    [Fact]
    public void ImagesThroughTheImageApi()
    {
        var dir = TempDir();
        try
        {
            var img = new RawImage(9, 6, ColorModel.Gray, Enumerable.Range(0, 54).Select(i => (ushort)(i * 1000)).ToArray());
            foreach (var ext in new[] { ".nrrd", ".nhdr", ".nii", ".nii.gz", ".mha", ".mhd", ".fits", ".fits.gz" })
            {
                var p = Path.Combine(dir, "img" + ext);
                ImageCodec.Encode(img, p);
                var back = ImageCodec.Decode(p);
                TestUtil.AssertEqual(img, back);
                var info = ImageCodec.GetInfo(p);
                Assert.Equal((9, 6, 1, SampleType.UInt16), (info.Width, info.Height, info.Depth, info.SampleType));
                Assert.Equal(ImageCodec.FormatFromExtension(p), info.Format);
            }
        }
        finally { Directory.Delete(dir, true); }
    }
}
