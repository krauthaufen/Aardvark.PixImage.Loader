using Aardvark.Base;
using PixCodec;
using Xunit;

namespace Aardvark.PixImage.PixCodec.Tests;

using PixImage = Aardvark.Base.PixImage;

/// <summary>Mip maps (IPixMipmapLoader), DDS cube maps and two channel images.</summary>
public class MipmapTests
{
    static string Fixture(string rel)
    {
        var d = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(d, "tests", "fixtures"))) d = Path.GetDirectoryName(d)!;
        return Path.Combine(d, "tests", "fixtures", rel);
    }

    static byte[] Bytes(PixImage<byte> p) => p.ToImageLayout().Volume.CopyToImage().Data;

    [Fact]
    public void DdsMipChainThroughTheLoaderInterface()
    {
        var path = Fixture("dds/mips_rgba_37x20.dds");
        var expected = Dds.DecodeMipmaps(File.ReadAllBytes(path));
        foreach (var mm in new[] { PixImageMipMap.Load(path, PixImagePixCodec.Loader), PixImagePixCodec.LoadMipmap(path),
                                   PixImageMipMap.Load(new MemoryStream(File.ReadAllBytes(path)), PixImagePixCodec.Loader) })
        {
            Assert.Equal(6, mm.LevelCount);
            for (int m = 0; m < 6; m++)
            {
                var level = (PixImage<byte>)mm.ImageArray[m];
                Assert.Equal(new V2i(expected[m].Width, expected[m].Height), level.Size);
                Assert.Equal(expected[m].Data, Bytes(level));
            }
        }
        // files without levels: one level
        var png = Path.Combine(Path.GetTempPath(), $"pixcodec-{Guid.NewGuid():N}.png");
        try
        {
            ImageCodec.Encode(RawImage.Create(5, 4, ColorModel.Rgb, SampleType.UInt8), png);
            Assert.Equal(1, PixImageMipMap.Load(png, PixImagePixCodec.Loader).LevelCount);
        }
        finally { File.Delete(png); }
    }

    [Fact]
    public void ExrMipLevels()
    {
        var img = RawImage.Create(16, 8, ColorModel.Rgb, SampleType.Float32);
        var d = img.GetData<float>();
        for (int i = 0; i < d.Length; i++) d[i] = i * 0.01f;
        var ms = new MemoryStream();
        Exr.Encode(img, ms, new ExrEncodeOptions { TileWidth = 8, TileHeight = 8, LevelMode = ExrLevelMode.MipMap });
        var bytes = ms.ToArray();
        Assert.Equal(5, ImageCodec.GetInfo(bytes).MipLevels);
        var mm = PixImagePixCodec.LoadMipmap(new MemoryStream(bytes));
        Assert.Equal(new[] { 16, 8, 4, 2, 1 }, mm.ImageArray.Select(p => p.Size.X));
        var levels = Exr.DecodeMipmaps(bytes);
        for (int m = 0; m < levels.Length; m++) Assert.Equal(levels[m].Data, ((PixImage<float>)mm.ImageArray[m]).Volume.Data);
        Assert.Equal(levels[2].Data, Exr.Decode(bytes, new ExrDecodeOptions { Level = 2 }).Data);
    }

    [Fact]
    public void CubeMaps()
    {
        var cube = PixImagePixCodec.LoadCube(Fixture("dds/cube_dx10_mips.dds"));
        var t = Dds.DecodeTexture(File.ReadAllBytes(Fixture("dds/cube_dx10_mips.dds")));
        for (int f = 0; f < 6; f++)
        {
            var side = cube[(CubeSide)f];
            Assert.Equal(4, side.LevelCount);
            Assert.Equal(t.GetImage(2, 0, f).Data, Bytes((PixImage<byte>)side.ImageArray[2]));
        }
        var some = PixImagePixCodec.LoadCube(Fixture("dds/cube_legacy_some_faces.dds"));
        Assert.NotNull(some[CubeSide.PositiveX]);
        Assert.Null(some[CubeSide.NegativeX]);
        Assert.NotNull(some[CubeSide.NegativeY]);
        Assert.Throws<ImageLoadException>(() => PixImagePixCodec.LoadCube(Fixture("dds/mips_rgba_37x20.dds")));

        // and back
        var ms = new MemoryStream();
        PixImagePixCodec.SaveDds(cube, ms);
        var again = PixImagePixCodec.LoadCube(new MemoryStream(ms.ToArray()));
        for (int f = 0; f < 6; f++)
            for (int m = 0; m < 4; m++)
                Assert.Equal(Bytes((PixImage<byte>)cube[(CubeSide)f].ImageArray[m]), Bytes((PixImage<byte>)again[(CubeSide)f].ImageArray[m]));
    }

    [Fact]
    public void SaveMipmapAndRgTextures()
    {
        var mm = PixImagePixCodec.LoadMipmap(Fixture("dds/mips_rgba_37x20.dds"));
        var ms = new MemoryStream();
        PixImagePixCodec.SaveDds(mm, ms);
        var back = PixImagePixCodec.LoadMipmap(new MemoryStream(ms.ToArray()));
        Assert.Equal(mm.LevelCount, back.LevelCount);
        for (int m = 0; m < mm.LevelCount; m++) Assert.Equal(Bytes((PixImage<byte>)mm.ImageArray[m]), Bytes((PixImage<byte>)back.ImageArray[m]));

        // BC5 (two channel normal map data) comes as RG and is saved as such
        var rg = PixImagePixCodec.Load(Fixture("dds/bc5u_13x7.dds"));
        Assert.Equal(Col.Format.RG, rg.Format);
        var ms2 = new MemoryStream();
        PixImagePixCodec.Save(rg, ms2, PixFileFormat.Dds);
        var rg2 = PixImagePixCodec.Load(new MemoryStream(ms2.ToArray()));
        Assert.Equal(Col.Format.RG, rg2.Format);
        Assert.Equal(Bytes((PixImage<byte>)rg), Bytes((PixImage<byte>)rg2));
        Assert.Equal(new V2i(13, 7), PixImagePixCodec.Loader.GetInfoFromFile(Fixture("dds/bc7_13x7.dds")).Size);
    }
}
