using Aardvark.Data;
using Aardvark.Base;
using Aardvark.ImageCodecs;
using Xunit;

namespace PixCodecTests;

using PixImage = Aardvark.Base.PixImage;

/// <summary>Gain map JPEGs (Ultra HDR / ISO 21496-1) and MPO files through the PixImage loader.</summary>
public class GainMapLoaderTests
{
    static string Fixture(string rel)
    {
        var d = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(d, "tests", "fixtures"))) d = Path.GetDirectoryName(d)!;
        return Path.Combine(d, "tests", "fixtures", rel);
    }

    static RawImage Hdr(int w, int h)
    {
        var f = new float[w * h * 3];
        for (int i = 0; i < w * h; i++) { f[i * 3] = 6f * (i % w) / w; f[i * 3 + 1] = 0.5f; f[i * 3 + 2] = (float)(i / w) / h; }
        return new RawImage(w, h, ColorModel.Rgb, f);
    }

    [Fact]
    public void LoadGivesTheBaselineAndLoadHdrTheAlternate()
    {
        var ms = new MemoryStream();
        Jpeg.EncodeUltraHdr(Hdr(40, 30), ms);
        var jpeg = ms.ToArray();
        var sdr = PixCodec.Load(jpeg);
        Assert.Equal(typeof(byte), sdr.PixFormat.Type);
        Assert.Equal(new V2i(40, 30), sdr.Size);
        var hdr = PixCodec.LoadHdr(jpeg);
        Assert.Equal(Col.Format.RGB, hdr.Format);
        Assert.Equal(new V2i(40, 30), hdr.Size);
        var expected = (float[])Jpeg.DecodeHdr(jpeg).Data;
        Assert.Equal(expected, hdr.ToImageLayout().Volume.CopyToImage().Data);
        Assert.True(expected.Max() > 1.5);
        // the SDR look at headroom 0, from a stream
        var flat = PixCodec.LoadHdr(new MemoryStream(jpeg), 0).ToImageLayout().Volume.CopyToImage().Data;
        Assert.True(flat.Max() <= 1.0001f);
        // gain map fixtures ultrahdr_app wrote
        var f = Fixture("gainmap/default.jpg");
        Assert.Equal(new V2i(64, 48), PixCodec.LoadHdr(f).Size);
        Assert.True(PixCodec.GetInfo(f).HasGainMap);
    }

    [Fact]
    public void FilesWithoutGainMapLoadAsLinear()
    {
        var img = new RawImage(2, 1, ColorModel.Rgb, new byte[] { 255, 128, 0, 0, 0, 255 });
        var ms = new MemoryStream();
        Png.Encode(img, ms);
        var lin = PixCodec.LoadHdr(ms.ToArray()).ToImageLayout().Volume.CopyToImage().Data;
        Assert.Equal(1f, lin[0], 5);
        Assert.Equal(0.2158605f, lin[1], 5);
        Assert.Equal(1f, lin[5], 5);
    }

    [Fact]
    public void LoadAllGivesTheViewsOfAnMpo()
    {
        var a = new RawImage(16, 8, ColorModel.Rgb, Enumerable.Range(0, 384).Select(i => (byte)i).ToArray());
        var b = new RawImage(16, 8, ColorModel.Rgb, Enumerable.Range(0, 384).Select(i => (byte)(255 - i)).ToArray());
        var ms = new MemoryStream();
        Jpeg.EncodeMpo(new[] { a, b }, ms);
        var views = PixCodec.LoadAll(ms.ToArray());
        Assert.Equal(2, views.Length);
        Assert.All(views, v => Assert.Equal(new V2i(16, 8), v.Size));
        Assert.Equal(2, PixCodec.GetInfo(new MemoryStream(ms.ToArray())).FrameCount);
    }
}
