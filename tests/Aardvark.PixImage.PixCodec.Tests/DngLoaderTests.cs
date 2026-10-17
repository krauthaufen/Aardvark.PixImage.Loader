using Aardvark.Data;
using Aardvark.Base;
using Aardvark.ImageCodecs;
using Xunit;

namespace PixCodecTests;

using PixImage = Aardvark.Base.PixImage;

/// <summary>DNG (decode only) through the PixImage loader: the rendered image, upright.</summary>
public class DngLoaderTests
{
    static readonly PixCodecLoader Loader = PixCodec.Loader;

    static string Fixture(string rel)
    {
        var d = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(d, "tests", "fixtures"))) d = Path.GetDirectoryName(d)!;
        return Path.Combine(d, "tests", "fixtures", rel);
    }

    static T[] Samples<T>(PixImage p) => ((PixImage<T>)p).ToImageLayout().Volume.CopyToImage().Data;

    [Theory]
    [InlineData("dng/bayer16_rggb.dng", Col.Format.RGB)]
    [InlineData("dng/xtrans16.dng", Col.Format.RGB)]
    [InlineData("dng/linearraw_list3_orient6.dng", Col.Format.RGB)]
    [InlineData("dng/mono12.dng", Col.Format.Gray)]
    public void LoadsLikeTheCodec(string file, Col.Format format)
    {
        var path = Fixture(file);
        var raw = ImageCodec.Decode(path);
        Assert.Equal(ImageFormat.Dng, ImageCodec.GetInfo(path).Format);
        foreach (var p in new[] { Loader.LoadFromFile(path), Loader.LoadFromStream(new MemoryStream(File.ReadAllBytes(path))), PixCodec.Load(path) })
        {
            Assert.Equal(typeof(ushort), p.PixFormat.Type);
            Assert.Equal(format, p.Format);
            Assert.Equal(new V2i(raw.Width, raw.Height), p.Size);
            Assert.Equal((ushort[])raw.Data, Samples<ushort>(p));
        }
        var info = Loader.GetInfoFromFile(path);
        Assert.Equal(new V2i(raw.Width, raw.Height), info.Size);
        Assert.Equal(typeof(ushort), info.Format.Type);
        Assert.Equal(format, info.Format.Format);
    }

    [Fact]
    public void LoadWithOptions()
    {
        var path = Fixture("dng/bayer16_rggb.dng");
        var f = PixCodec.LoadDng(path, new DngDecodeOptions { OutputType = SampleType.Float32 });
        Assert.Equal(typeof(float), f.PixFormat.Type);
        var raw = Dng.Decode(File.ReadAllBytes(path), new DngDecodeOptions { OutputType = SampleType.Float32 });
        Assert.Equal((float[])raw.Data, Samples<float>(f));
        var b = PixCodec.LoadDng(new MemoryStream(File.ReadAllBytes(path)), new DngDecodeOptions { OutputType = SampleType.UInt8 });
        Assert.Equal(typeof(byte), b.PixFormat.Type);
    }
}
