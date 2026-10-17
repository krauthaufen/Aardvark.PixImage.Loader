using Aardvark.Data;
using Aardvark.Base;
using Aardvark.ImageCodecs;
using Xunit;

namespace PixCodecTests;

using PixImage = Aardvark.Base.PixImage;

/// <summary>AVIF (decode only) through the PixImage loader.</summary>
public class AvifLoaderTests
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
    [InlineData("avif/nat_420_s6.avif", typeof(byte), Col.Format.RGB)]
    [InlineData("avif/alpha.avif", typeof(byte), Col.Format.RGBA)]
    [InlineData("avif/gray_400.avif", typeof(byte), Col.Format.Gray)]
    [InlineData("avif/nat_420_10.avif", typeof(ushort), Col.Format.RGB)]
    [InlineData("avif/irot1.avif", typeof(byte), Col.Format.RGB)]
    [InlineData("avif/grid_2x2.avif", typeof(byte), Col.Format.RGB)]
    public void LoadsLikeTheCodec(string file, Type type, Col.Format format)
    {
        var path = Fixture(file);
        var raw = ImageCodec.Decode(path);
        foreach (var p in new[] { Loader.LoadFromFile(path), Loader.LoadFromStream(new MemoryStream(File.ReadAllBytes(path))), PixCodec.Load(path) })
        {
            Assert.Equal(type, p.PixFormat.Type);
            Assert.Equal(format, p.Format);
            Assert.Equal(new V2i(raw.Width, raw.Height), p.Size);
            if (type == typeof(byte)) Assert.Equal((byte[])raw.Data, Samples<byte>(p));
            else Assert.Equal((ushort[])raw.Data, Samples<ushort>(p));
        }
        var info = Loader.GetInfoFromFile(path);
        Assert.Equal(new V2i(raw.Width, raw.Height), info.Size);
        Assert.Equal(type, info.Format.Type);
        Assert.Equal(format, info.Format.Format);
    }

    [Fact]
    public void SequenceFrames()
    {
        var path = Fixture("avif/seq.avif");
        var frames = PixCodec.LoadAll(path);
        Assert.Equal(8, frames.Length);
        var raw = Avif.DecodeAll(File.ReadAllBytes(path));
        for (int i = 0; i < frames.Length; i++) Assert.Equal((byte[])raw[i].Data, Samples<byte>(frames[i]));
    }

    [Fact]
    public void AvifsExtension()
    {
        var src = Fixture("avif/seq.avif");
        var path = Path.Combine(Path.GetTempPath(), $"pixcodec-{Guid.NewGuid():N}.avifs");
        try
        {
            File.Copy(src, path);
            Assert.Equal(ImageFormat.Avif, ImageCodec.FormatFromExtension(path));
            Assert.Equal(8, PixCodec.LoadAll(path).Length);
        }
        finally { File.Delete(path); }
    }
}
