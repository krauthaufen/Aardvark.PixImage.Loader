using System.Text.Json;
using Aardvark.ImageCodecs.Compression;
using Xunit;
using static Aardvark.ImageCodecs.Tests.TestUtil;
using static Aardvark.ImageCodecs.Tests.VolumeTestUtil;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>NRRD, NIfTI, MetaImage and FITS decoding against the fixtures of tests/oracle/gen_volume.py.</summary>
public class VolumeTests
{
    public static IEnumerable<object[]> Fixtures()
    {
        var root = Path.Combine(FixtureDir, "volume");
        if (!Directory.Exists(root)) yield break;
        foreach (var dir in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
            foreach (var j in Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                var file = j.Substring(0, j.Length - 5);
                if (!File.Exists(file)) continue; // (sidecars of other HDUs)
                yield return new object[] { Path.GetRelativePath(FixtureDir, file) };
            }
    }

    static double Tol(string json)
    {
        using var d = JsonDocument.Parse(File.ReadAllText(json));
        return d.RootElement.TryGetProperty("tol", out var t) && d.RootElement.TryGetProperty("type", out var ty) && ty.GetString()!.StartsWith("Float") ? t.GetDouble() : 0;
    }

    [Theory, MemberData(nameof(Fixtures))]
    public void DecodeMatchesReference(string file)
    {
        var path = Fixture(file);
        var expected = LoadVref(path + ".vref");
        var v = ImageCodec.DecodeVolume(path);
        AssertSame(expected, v, Tol(path + ".json"));
        AssertMetadata(path + ".json", v);

        // header info agrees with what is decoded
        var info = ImageCodec.GetInfo(path);
        Assert.Equal(ImageCodec.DetectFormat(File.ReadAllBytes(path)), info.Format);
        Assert.Equal((v.Width, v.Height, v.Depth, v.FrameCount, v.Channels, v.SampleType, v.Color),
                     (info.Width, info.Height, info.Depth, info.FrameCount, info.Channels, info.SampleType, info.Color));
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(info with { Metadata = null }, ImageCodec.GetInfo(bytes) with { Metadata = null });
        using (var s = new InfoTests.CountingStream(new MemoryStream(bytes), seekable: false))
            Assert.Equal(info with { Metadata = null }, ImageCodec.GetInfo(s) with { Metadata = null });

        // the 2D path gives the first slice, DecodeAll every slice
        var slice = ImageCodec.Decode(path);
        AssertSame(new RawVolume(new[] { v.Width, v.Height }, v.Color, v.GetSlice(0).Data, v.Channels), RawVolume.FromImage(slice));
        Assert.NotNull(slice.Metadata?.Physical);

        // attached files decode from memory as well
        var vi = ImageCodec.DetectFormat(bytes) switch
        {
            ImageFormat.Nrrd => Nrrd.GetVolumeInfo(bytes),
            ImageFormat.MetaImage => MetaImage.GetVolumeInfo(bytes),
            ImageFormat.Nifti => Nifti.GetVolumeInfo(bytes),
            _ => Fits.GetVolumeInfo(bytes),
        };
        Assert.Equal(v.Sizes, vi.Sizes);
        Assert.Equal(v.SampleType, vi.SampleType);
        if (!vi.Detached) AssertSame(v, ImageCodec.DecodeVolume(bytes), 0);
        else Assert.Throws<ImageFormatException>(() => ImageCodec.DecodeVolume(bytes));
    }

    // ------------------------------------------------------------------ FITS specifics

    [Fact]
    public void FitsMultipleHdus()
    {
        var path = Fixture("volume/fits/multi.fits");
        var data = File.ReadAllBytes(path);
        var hdus = Fits.ReadHdus(data);
        Assert.Equal(3, hdus.Count);
        Assert.Equal(new[] { "PRIMARY", "BINTABLE", "IMAGE" }, hdus.Select(h => h.Type));
        Assert.Equal(new[] { true, false, true }, hdus.Select(h => h.IsImage));
        var all = Fits.DecodeAllVolumes(data);
        Assert.Equal(2, all.Length);
        AssertSame(LoadVref(path + ".hdu2.vref"), all[1]);
        AssertSame(all[1], Fits.DecodeVolume(data, new FitsDecodeOptions { Hdu = 2 }));
        Assert.Throws<ImageNotSupportedException>(() => Fits.DecodeVolume(data, new FitsDecodeOptions { Hdu = 1 }));
        // DecodeAll: the 2D primary plus the two slices of the cube
        Assert.Equal(3, ImageCodec.DecodeAll(data).Length);
        Assert.Equal("SECOND", hdus[2].Get("EXTNAME"));
    }

    [Fact]
    public void FitsOrientationOption()
    {
        var data = File.ReadAllBytes(Fixture("volume/fits/bitpix_int16.fits"));
        var top = Fits.DecodeVolume(data);
        var raw = Fits.DecodeVolume(data, new FitsDecodeOptions { ApplyOrientation = false });
        var t = top.GetData<short>(); var r = raw.GetData<short>();
        int w = top.Width, h = top.Height;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) Assert.Equal(t[y * w + x], r[(h - 1 - y) * w + x]);
    }

    [Fact]
    public void FitsRawScaling()
    {
        var data = File.ReadAllBytes(Fixture("volume/fits/scaled.fits"));
        var raw = Fits.DecodeVolume(data, new FitsDecodeOptions { Scaling = DataScaling.Raw });
        Assert.Equal(SampleType.Int16, raw.SampleType);
        Assert.False(raw.Metadata!.ScalingApplied);
        var phys = Fits.DecodeVolume(data);
        Assert.True(phys.Metadata!.ScalingApplied);
        var a = raw.GetData<short>(); var b = phys.GetData<float>();
        for (int i = 0; i < a.Length; i++)
            if (a[i] == -32768) Assert.True(float.IsNaN(b[i])); else Assert.Equal((float)(a[i] * 0.5 + 100), b[i]);
        Assert.Equal(SampleType.Float64, Fits.DecodeVolume(data, new FitsDecodeOptions { Scaling = DataScaling.Float64 }).SampleType);
        // unsigned convention: raw gives the stored signed values
        var u = File.ReadAllBytes(Fixture("volume/fits/bitpix_uint16.fits"));
        Assert.Equal(SampleType.Int16, Fits.DecodeVolume(u, new FitsDecodeOptions { Scaling = DataScaling.Raw }).SampleType);
        Assert.Equal(SampleType.UInt16, Fits.GetInfo(u).SampleType);
    }

    [Fact]
    public void FitsHcompressNotSupported()
    {
        var data = File.ReadAllBytes(Fixture("volume/fits/hcompress.fits"));
        Assert.Equal(ImageFormat.Fits, ImageCodec.DetectFormat(data));
        Assert.Throws<ImageNotSupportedException>(() => Fits.DecodeVolume(data));
    }

    [Fact]
    public void FitsRandomSequence() => Assert.Equal(1043618065.0, FitsTiles.RandomSeedCheck());

    [Fact]
    public void FitsWcsKeywordsExposed()
    {
        var v = Fits.DecodeVolume(File.ReadAllBytes(Fixture("volume/fits/wcs.fits")));
        Assert.Equal("RA---TAN", v.Metadata!.Get("CTYPE1"));
        Assert.Equal("4.5", v.Metadata.Get("CRPIX1"));
    }

    // ------------------------------------------------------------------ NIfTI specifics

    [Fact]
    public void NiftiHeaderFields()
    {
        var h = Nifti.ReadHeader(File.ReadAllBytes(Fixture("volume/nifti/qform.nii")));
        Assert.Equal(1, h.Version);
        Assert.True(h.SingleFile);
        Assert.Equal(1002, h.IntentCode);
        Assert.Equal("labels", h.IntentName);
        // (nibabel's set_dim_info takes 0-based axes, the header stores them 1-based)
        Assert.Equal(2, h.FrequencyDim);
        Assert.Equal(1, h.PhaseDim);
        Assert.Equal(3, h.SliceDim);
        Assert.Equal(0, h.SFormCode);
        Assert.Equal(1, h.QFormCode);
        Assert.Contains(h.Extensions, e => e.Code == 6 && System.Text.Encoding.ASCII.GetString(e.Data).TrimEnd('\0').StartsWith("hello volume"));
        var gz = Nifti.ReadHeader(File.ReadAllBytes(Fixture("volume/nifti/f32.nii.gz")));
        Assert.Equal(16, gz.DataType);
        var n2 = Nifti.ReadHeader(File.ReadAllBytes(Fixture("volume/nifti/nifti2_4d.nii")));
        Assert.Equal(2, n2.Version);
        Assert.Equal(4, n2.Dim[0]);
    }

    [Fact]
    public void NiftiRawScaling()
    {
        var data = File.ReadAllBytes(Fixture("volume/nifti/i16_scaled.nii"));
        var raw = Nifti.DecodeVolume(data, new VolumeDecodeOptions { Scaling = DataScaling.Raw });
        Assert.Equal(SampleType.Int16, raw.SampleType);
        Assert.Equal(2.5, raw.Metadata!.Slope);
        Assert.Equal(SampleType.Float32, Nifti.GetInfo(data).SampleType);
        Assert.Equal(SampleType.Int32, Nifti.GetInfo(File.ReadAllBytes(Fixture("volume/nifti/i16_intercept.nii"))).SampleType);
    }

    [Fact]
    public void NiftiPairNeedsImg()
    {
        var hdr = File.ReadAllBytes(Fixture("volume/nifti/pair.hdr"));
        Assert.Equal(ImageFormat.Nifti, ImageCodec.DetectFormat(hdr));
        Assert.Equal((5, 4, 3), (Nifti.GetInfo(hdr).Width, Nifti.GetInfo(hdr).Height, Nifti.GetInfo(hdr).Depth));
        Assert.Throws<ImageFormatException>(() => Nifti.DecodeVolume(hdr));
        var img = Fixture("volume/nifti/pair.img");
        var v = Nifti.DecodeVolume(hdr, new VolumeDecodeOptions { OpenDataFile = _ => File.OpenRead(img) });
        AssertSame(LoadVref(Fixture("volume/nifti/pair.hdr") + ".vref"), v);
    }

    // ------------------------------------------------------------------ general

    [Fact]
    public void DepthInImageInfo()
    {
        var info = ImageCodec.GetInfo(Fixture("volume/nrrd/u8_raw.nrrd"));
        Assert.Equal((7, 5, 4, 1), (info.Width, info.Height, info.Depth, info.FrameCount));
        var t = ImageCodec.GetInfo(Fixture("volume/nrrd/time_4d.nrrd"));
        Assert.Equal((4, 3, 2, 3), (t.Width, t.Height, t.Depth, t.FrameCount));
        Assert.NotEqual(info, info with { Depth = 2 });
        Assert.Equal(1, ImageCodec.GetInfo(Fixture("volume/nifti/slice2d.nii")).Depth);
        // volume textures report their slices too, and decode through the volume path
        var vol = ImageCodec.DecodeVolume(Fixture("volume/metaimage/u8.mha"));
        var dds = Dds.EncodeVolume(vol.GetSlices());
        Assert.Equal(4, ImageCodec.GetInfo(dds).Depth);
        AssertSame(new RawVolume(vol.Sizes, vol.Color, vol.Data), ImageCodec.DecodeVolume(dds));
        var ktx = new MemoryStream();
        ImageCodec.EncodeVolume(vol, ktx, ImageFormat.Ktx);
        Assert.Equal(4, ImageCodec.GetInfo(ktx.ToArray()).Depth);
    }

    [Fact]
    public void FramesAndIndexing()
    {
        var v = ImageCodec.DecodeVolume(Fixture("volume/nrrd/time_4d.nrrd"));
        Assert.Equal(4, v.Dimensions);
        Assert.Equal(3, v.FrameCount);
        var f2 = v.GetFrame(2);
        Assert.Equal(new[] { 4, 3, 2 }, f2.Sizes);
        var a = v.GetData<short>(); var b = f2.GetData<short>();
        Assert.Equal(a[v.GetIndex(1, 2, 1, 2)], b[f2.GetIndex(1, 2, 1)]);
        Assert.Equal(a[v.GetIndex(3, 0, 1, 1)], v.GetSlice(1, 1).GetData<short>()[3]);
        Assert.Equal(6, ImageCodec.DecodeAll(Fixture("volume/nrrd/time_4d.nrrd")).Length);
    }

    [Fact]
    public void ExtensionsMapToFormats()
    {
        Assert.Equal(ImageFormat.Nifti, ImageCodec.FormatFromExtension("a/b.nii.gz"));
        Assert.Equal(ImageFormat.Nifti, ImageCodec.FormatFromExtension("b.NII"));
        Assert.Equal(ImageFormat.Fits, ImageCodec.FormatFromExtension("x.fits.gz"));
        Assert.Equal(ImageFormat.Fits, ImageCodec.FormatFromExtension("x.fts"));
        Assert.Equal(ImageFormat.Nrrd, ImageCodec.FormatFromExtension("x.nhdr"));
        Assert.Equal(ImageFormat.MetaImage, ImageCodec.FormatFromExtension("x.mhd"));
        Assert.Equal(ImageFormat.Hdr, ImageCodec.FormatFromExtension("x.hdr")); // Radiance; NIfTI .hdr is found by content
        Assert.Equal(ImageFormat.Unknown, ImageCodec.FormatFromExtension("x.tar.gz"));
    }

    [Fact]
    public void DetectionDoesNotStealOtherFormats()
    {
        foreach (var dir in new[] { "png", "tga", "bmp", "pnm", "hdr", "qoi", "tiff" })
        {
            var d = Path.Combine(FixtureDir, dir);
            if (!Directory.Exists(d)) continue;
            foreach (var f in Directory.GetFiles(d).Take(40))
            {
                if (f.EndsWith(".ref") || f.EndsWith(".json") || f.EndsWith(".txt")) continue;
                var fmt = ImageCodec.DetectFormat(File.ReadAllBytes(f));
                Assert.False(fmt is ImageFormat.Nrrd or ImageFormat.Nifti or ImageFormat.MetaImage or ImageFormat.Fits, $"{f} detected as {fmt}");
            }
        }
        Assert.Equal(ImageFormat.Unknown, ImageCodec.DetectFormat("ObjectType = Table\nNDims = 2\n"u8));
        Assert.False(MetaImage.IsMatch("Hello = world\n"u8));
        Assert.True(MetaImage.IsMatch("NDims = 2\nDimSize = 2 2\nElementType = MET_UCHAR\nElementDataFile = LOCAL\n"u8));
    }

    // ------------------------------------------------------------------ robustness

    public static IEnumerable<object[]> AttachedFixtures()
        => Fixtures().Where(r =>
        {
            var p = Fixture((string)r[0]);
            var b = File.ReadAllBytes(p);
            try { return ImageCodec.DetectFormat(b) switch { ImageFormat.Nrrd => !Nrrd.GetVolumeInfo(b).Detached, ImageFormat.MetaImage => !MetaImage.GetVolumeInfo(b).Detached, ImageFormat.Nifti => !Nifti.GetVolumeInfo(b).Detached, _ => true }; }
            catch (Exception) { return false; }
        });

    [Theory, MemberData(nameof(AttachedFixtures))]
    public void TruncatedAndCorruptInputFailsCleanly(string file)
    {
        var data = File.ReadAllBytes(Fixture(file));
        var rnd = new Random(file.Length * 31 + data.Length);
        var cuts = new HashSet<int> { 0, 1, 10, 100, 347, 352, 2880, data.Length / 2, data.Length - 1 };
        for (int k = 0; k < 6; k++) cuts.Add(rnd.Next(data.Length));
        foreach (var cut in cuts.Where(c => c < data.Length))
            Check(data.AsSpan(0, cut).ToArray());
        int rounds = int.TryParse(Environment.GetEnvironmentVariable("PIXCODEC_FUZZ_ROUNDS"), out var fr) ? fr : 40;
        for (int k = 0; k < rounds; k++)
        {
            var bad = (byte[])data.Clone();
            int n = 1 + rnd.Next(4);
            for (int i = 0; i < n; i++) bad[rnd.Next(bad.Length)] = (byte)rnd.Next(256);
            Check(bad);
        }
    }

    static void Check(byte[] data)
    {
        try { ImageCodec.DecodeVolume(data, new FitsDecodeOptions { MaxSamples = 1 << 24 }); }
        catch (ImageFormatException) { }
        catch (ImageNotSupportedException) { }
        try { ImageCodec.GetInfo(data); }
        catch (ImageFormatException) { }
        catch (ImageNotSupportedException) { }
    }

    // ------------------------------------------------------------------ compression helpers

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1000)]
    [InlineData(300_000)]
    public void GzipRoundTrip(int n)
    {
        var rnd = new Random(n);
        var src = new byte[n];
        for (int i = 0; i < n; i++) src[i] = (byte)(rnd.Next(4) == 0 ? rnd.Next(256) : i / 100);
        var gz = Gzip.Compress(src);
        Assert.True(Gzip.IsGzip(gz));
        Assert.Equal(src, Gzip.Decompress(gz));
        // two members and zero padding
        var two = gz.Concat(Gzip.Compress(src.AsSpan(0, n / 2))).Concat(new byte[100]).ToArray();
        Assert.Equal(src.Concat(src.Take(n / 2)), Gzip.Decompress(two));
        if (n > 10)
        {
            var bad = (byte[])gz.Clone();
            bad[^6] ^= 1; // CRC
            Assert.Throws<ImageFormatException>(() => Gzip.Decompress(bad));
            Assert.Throws<ImageFormatException>(() => Gzip.Decompress(gz.AsSpan(0, gz.Length - 3)));
        }
    }

    [Fact]
    public void GzipAgainstPython()
    {
        // python's gzip module writes headers with a file name and mtime
        if (!PythonAvailable) return;
        var tmp = VolumeTestUtil.TempDir();
        try
        {
            var src = Path.Combine(tmp, "a.bin");
            var data = Enumerable.Range(0, 100_000).Select(i => (byte)(i * 7 % 251 ^ i / 1000)).ToArray();
            File.WriteAllBytes(src, data);
            var (exit, output) = RunPython($"-c \"import gzip,bz2,sys; d=open(sys.argv[1],'rb').read(); open(sys.argv[1]+'.gz','wb').write(gzip.compress(d)); " +
                                            $"open(sys.argv[1]+'.bz2','wb').write(bz2.compress(d,1)+bz2.compress(d[:777],9))\" \"{src}\"");
            Assert.True(exit == 0, output);
            Assert.Equal(data, Gzip.Decompress(File.ReadAllBytes(src + ".gz")));
            Assert.Equal(data.Concat(data.Take(777)), Bzip2.Decompress(File.ReadAllBytes(src + ".bz2")));
        }
        finally { Directory.Delete(tmp, true); }
    }

    [Theory]
    [InlineData("zeros")]
    [InlineData("random")]
    [InlineData("text")]
    [InlineData("runs")]
    [InlineData("image")]
    [InlineData("big")]
    public void Bzip2AgainstPython(string shape)
    {
        if (!PythonAvailable) return;
        var data = CompressionTestData.Make(shape);
        var tmp = VolumeTestUtil.TempDir();
        try
        {
            var src = Path.Combine(tmp, "a.bin");
            File.WriteAllBytes(src, data);
            foreach (var level in new[] { 1, 9 })
            {
                var (exit, output) = RunPython($"-c \"import bz2,sys; d=open(sys.argv[1],'rb').read(); open(sys.argv[1]+'.bz2','wb').write(bz2.compress(d,{level}))\" \"{src}\"");
                Assert.True(exit == 0, output);
                var bz = File.ReadAllBytes(src + ".bz2");
                Assert.Equal(data, Bzip2.Decompress(bz));
                // corrupt / truncated streams fail cleanly
                var rnd = new Random(level);
                for (int k = 0; k < 20; k++)
                {
                    var bad = (byte[])bz.Clone();
                    bad[4 + rnd.Next(bad.Length - 4)] ^= (byte)(1 << rnd.Next(8));
                    try { Bzip2.Decompress(bad); } catch (ImageFormatException) { } catch (ImageNotSupportedException) { }
                    try { Bzip2.Decompress(bz.AsSpan(0, rnd.Next(bz.Length))); } catch (ImageFormatException) { }
                }
            }
        }
        finally { Directory.Delete(tmp, true); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void RiceRoundTrip(int bytepix)
    {
        var rnd = new Random(bytepix);
        foreach (var n in new[] { 1, 31, 32, 33, 1000 })
            foreach (var spread in new[] { 0, 1, 7, 300, 1 << 20, int.MaxValue })
            {
                var px = new long[n];
                long v = 0;
                for (int i = 0; i < n; i++)
                {
                    v += rnd.NextInt64(-(long)spread, (long)spread + 1);
                    px[i] = bytepix switch { 1 => (byte)v, 2 => (short)v, _ => (int)v };
                }
                var enc = Rice.Encode(px, bytepix, 32);
                var dec = new long[n];
                Rice.Decode(enc, dec, bytepix, 32);
                Assert.Equal(px, dec);
            }
    }
}
