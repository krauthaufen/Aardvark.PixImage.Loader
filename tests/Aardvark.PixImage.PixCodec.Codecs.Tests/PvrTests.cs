using Xunit;
using static Aardvark.ImageCodecs.Tests.TestUtil;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>PVR v3 / legacy v2 textures: PVRTexTool fixtures (tests/oracle/gen_pvr.py), round trips, KTX cross checks.</summary>
public class PvrTests
{
    public static IEnumerable<object[]> Fixtures => Files("pvr", ".pvr");

    /// <summary>Tolerance in 8 bit steps against PVRTexTool's 8 bit decode (0: bit exact).</summary>
    static double Tolerance(string name) => name switch
    {
        _ when name.StartsWith("bc") => 4,          // PVRTexTool interpolates the BC1 / BC2 / BC3 colours with other rounding
        _ when name.StartsWith("eac") => 1,         // we return 16 bit EAC values
        "rgbe9995.pvr" => 1,
        "srgb.pvr" => 1,                            // the reference rounds the sRGB encoding PVRTexTool applied
        "r16f.pvr" => 0.125,                        // half float rounding of the reference
        _ => 0,
    };

    /// <summary>Samples scaled to 8 bit units (integers by their range, floats by 255).</summary>
    static double[] Units(RawImage img)
    {
        var d = ToDoubles(img);
        double s = img.SampleType switch
        {
            SampleType.UInt16 => 255.0 / 65535, SampleType.Int16 => 255.0 / 32767, SampleType.UInt32 => 255.0 / uint.MaxValue,
            SampleType.Float16 or SampleType.Float32 or SampleType.Float64 => 255, _ => 1,
        };
        for (int i = 0; i < d.Length; i++) d[i] *= s;
        return d;
    }

    static void AssertClose(RawImage expected, RawImage actual, double tol, string what)
    {
        Assert.Equal((expected.Width, expected.Height, expected.Channels), (actual.Width, actual.Height, actual.Channels));
        if (tol == 0 && expected.SampleType == actual.SampleType) { AssertEqual(expected, actual); return; }
        var e = Units(expected); var a = Units(actual);
        double m = 0;
        for (int i = 0; i < e.Length; i++) m = Math.Max(m, Math.Abs(e[i] - a[i]));
        Assert.True(m <= tol + 1e-9, $"{what}: max difference {m} (8 bit units) exceeds {tol}");
    }

    [Theory, MemberData(nameof(Fixtures))]
    public void DecodeMatchesReference(string file)
    {
        var data = File.ReadAllBytes(Fixture(file));
        var name = Path.GetFileName(file);
        var info = ImageCodec.GetInfo(data);
        Assert.Equal(ImageFormat.Pvr, info.Format);
        Assert.Equal(ImageFormat.Pvr, ImageCodec.FormatFromExtension(file));
        var all = ImageCodec.DecodeAll(data);
        Assert.Equal(info.FrameCount, all.Length);
        var top = ImageCodec.Decode(data);
        Assert.Equal((top.Width, top.Height, top.Channels, top.SampleType, top.Color), (info.Width, info.Height, info.Channels, info.SampleType, info.Color));
        Assert.Equal(info.Transfer == TransferFunction.Unknown ? TransferFunction.Unknown : info.Transfer, top.Metadata?.Transfer ?? TransferFunction.Unknown);
        if (File.Exists(Fixture(file) + ".ref")) AssertClose(LoadRef(Fixture(file) + ".ref"), top, Tolerance(name), name);
        for (int i = 0; File.Exists(Fixture(file) + $".{i}.ref"); i++) AssertClose(LoadRef(Fixture(file) + $".{i}.ref"), all[i], 0, $"{name} image {i}");
        var mips = ImageCodec.DecodeMipmaps(data);
        Assert.Equal(info.MipLevels, mips.Length);
        for (int m = 1; m < mips.Length; m++)
        {
            Assert.Equal((Math.Max(1, info.Width >> m), Math.Max(1, info.Height >> m)), (mips[m].Width, mips[m].Height));
            if (File.Exists(Fixture(file) + $".m{m}.ref")) AssertClose(LoadRef(Fixture(file) + $".m{m}.ref"), mips[m], 0, $"{name} level {m}");
        }
        Assert.Equal(info, ImageCodec.GetInfo(new MemoryStream(data)) with { Metadata = info.Metadata });
    }

    [Fact]
    public void HeaderValues()
    {
        var t = Pvr.GetTextureInfo(File.ReadAllBytes(Fixture("pvr/cube.pvr")));
        Assert.Equal((3, 32, 32, 6, 6, 1), (t.Version, t.Width, t.Height, t.Faces, t.MipLevels, t.Surfaces));
        Assert.Equal("r8g8b8a8", t.Format);
        var a = Pvr.GetTextureInfo(File.ReadAllBytes(Fixture("pvr/array.pvr")));
        Assert.Equal(3, a.Surfaces);
        var p = Pvr.GetTextureInfo(File.ReadAllBytes(Fixture("pvr/pvrtc2.pvr")));
        Assert.Equal(("PVRTC1_2", true), (p.Format, p.IsCompressed));
        Assert.True(Pvr.GetTextureInfo(File.ReadAllBytes(Fixture("pvr/srgb.pvr"))).IsSrgb);
        Assert.Equal(TransferFunction.Srgb, ImageCodec.GetInfo(File.ReadAllBytes(Fixture("pvr/srgb.pvr"))).Transfer);
        var v2 = Pvr.GetTextureInfo(File.ReadAllBytes(Fixture("pvr/v2cubem.pvr")));
        Assert.Equal((2, 6, 6), (v2.Version, v2.Faces, v2.MipLevels));
        var f = Pvr.GetTextureInfo(File.ReadAllBytes(Fixture("pvr/flip.pvr")));
        Assert.True(f.YUp);
        // the flagged file is stored bottom-up: without orientation handling it is the mirror image
        var raw = Pvr.Decode(File.ReadAllBytes(Fixture("pvr/flip.pvr")), new PvrDecodeOptions { ApplyOrientation = false });
        AssertEqual(ImageOps.FlipVertical(LoadRef(Fixture("pvr/flip.pvr.ref"))), raw);
    }
}
