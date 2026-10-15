using Aardvark.Base;
using Aardvark.ImageCodecs;
using Xunit;

namespace PixImageIOTests;

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
        foreach (var mm in new[] { PixImageMipMap.Load(path, PixImageIO.Loader), PixImageIO.LoadMipmap(path),
                                   PixImageMipMap.Load(new MemoryStream(File.ReadAllBytes(path)), PixImageIO.Loader) })
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
            Assert.Equal(1, PixImageMipMap.Load(png, PixImageIO.Loader).LevelCount);
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
        var mm = PixImageIO.LoadMipmap(new MemoryStream(bytes));
        Assert.Equal(new[] { 16, 8, 4, 2, 1 }, mm.ImageArray.Select(p => p.Size.X));
        var levels = Exr.DecodeMipmaps(bytes);
        for (int m = 0; m < levels.Length; m++) Assert.Equal(levels[m].Data, ((PixImage<float>)mm.ImageArray[m]).Volume.Data);
        Assert.Equal(levels[2].Data, Exr.Decode(bytes, new ExrDecodeOptions { Level = 2 }).Data);
    }

    [Fact]
    public void CubeMaps()
    {
        var cube = PixImageIO.LoadCube(Fixture("dds/cube_dx10_mips.dds"));
        var t = Dds.DecodeTexture(File.ReadAllBytes(Fixture("dds/cube_dx10_mips.dds")));
        for (int f = 0; f < 6; f++)
        {
            var side = cube[(CubeSide)f];
            Assert.Equal(4, side.LevelCount);
            Assert.Equal(t.GetImage(2, 0, f).Data, Bytes((PixImage<byte>)side.ImageArray[2]));
        }
        var some = PixImageIO.LoadCube(Fixture("dds/cube_legacy_some_faces.dds"));
        Assert.NotNull(some[CubeSide.PositiveX]);
        Assert.Null(some[CubeSide.NegativeX]);
        Assert.NotNull(some[CubeSide.NegativeY]);
        Assert.Throws<ImageLoadException>(() => PixImageIO.LoadCube(Fixture("dds/mips_rgba_37x20.dds")));

        // and back
        var ms = new MemoryStream();
        PixImageIO.SaveDds(cube, ms);
        var again = PixImageIO.LoadCube(new MemoryStream(ms.ToArray()));
        for (int f = 0; f < 6; f++)
            for (int m = 0; m < 4; m++)
                Assert.Equal(Bytes((PixImage<byte>)cube[(CubeSide)f].ImageArray[m]), Bytes((PixImage<byte>)again[(CubeSide)f].ImageArray[m]));
    }

    [Fact]
    public void SaveMipmapAndRgTextures()
    {
        var mm = PixImageIO.LoadMipmap(Fixture("dds/mips_rgba_37x20.dds"));
        var ms = new MemoryStream();
        PixImageIO.SaveDds(mm, ms);
        var back = PixImageIO.LoadMipmap(new MemoryStream(ms.ToArray()));
        Assert.Equal(mm.LevelCount, back.LevelCount);
        for (int m = 0; m < mm.LevelCount; m++) Assert.Equal(Bytes((PixImage<byte>)mm.ImageArray[m]), Bytes((PixImage<byte>)back.ImageArray[m]));

        // BC5 (two channel normal map data) comes as RG and is saved as such
        var rg = PixImageIO.Load(Fixture("dds/bc5u_13x7.dds"));
        Assert.Equal(Col.Format.RG, rg.Format);
        var ms2 = new MemoryStream();
        PixImageIO.Save(rg, ms2, PixFileFormat.Dds);
        var rg2 = PixImageIO.Load(new MemoryStream(ms2.ToArray()));
        Assert.Equal(Col.Format.RG, rg2.Format);
        Assert.Equal(Bytes((PixImage<byte>)rg), Bytes((PixImage<byte>)rg2));
        Assert.Equal(new V2i(13, 7), PixImageIO.Loader.GetInfoFromFile(Fixture("dds/bc7_13x7.dds")).Size);
    }

    [Fact]
    public void CompressedAndVolumeDds()
    {
        // block compressed with a computed mip chain
        var img = PixImageIO.Load(Fixture("jpeg/photo_444.jpg"));
        var ms = new MemoryStream();
        PixImageIO.SaveDds(img, ms, new DdsEncodeOptions { Compression = DdsCompression.BC7, Mipmaps = DdsMipmaps.Generate });
        var info = Dds.GetTextureInfo(ms.ToArray());
        Assert.Equal(("BC7_UNORM", 8), (info.Format, info.MipLevels));
        var mm = PixImageIO.LoadMipmap(new MemoryStream(ms.ToArray()));
        Assert.Equal((8, Col.Format.RGBA, new V2i(160, 120)), (mm.LevelCount, mm.ImageArray[0].Format, mm.ImageArray[0].Size));

        // a BGR volume: slices come back in RGB order
        var vol = new PixVolume<byte>(Col.Format.BGR, 7, 5, 3);
        var t = vol.Tensor4;
        for (int z = 0; z < 3; z++)
            for (int y = 0; y < 5; y++)
                for (int x = 0; x < 7; x++)
                    for (int c = 0; c < 3; c++) t[x, y, z, c] = (byte)(x * 30 + y * 7 + z * 50 + c * 3);
        var vs = new MemoryStream();
        PixImageIO.SaveDds(vol, vs);
        var tex = Dds.DecodeTexture(vs.ToArray());
        Assert.Equal((DdsTextureKind.Texture3D, 7, 5, 3), (tex.Kind, tex.Width, tex.Height, tex.Depth));
        var slices = tex.GetSlices();
        for (int z = 0; z < 3; z++)
        {
            var d = slices[z].GetData<byte>();
            int cs = slices[z].Channels;
            for (int y = 0; y < 5; y++)
                for (int x = 0; x < 7; x++)
                    for (int c = 0; c < 3; c++) Assert.Equal(t[x, y, z, 2 - c], d[(y * 7 + x) * cs + c]);
        }
        // and compressed
        var bs = new MemoryStream();
        PixImageIO.SaveDds(vol, bs, new DdsEncodeOptions { Compression = DdsCompression.BC1 });
        Assert.Equal((DdsTextureKind.Texture3D, 3, "BC1_UNORM"), (Dds.GetTextureInfo(bs.ToArray()).Kind, Dds.GetTextureInfo(bs.ToArray()).Depth, Dds.GetTextureInfo(bs.ToArray()).Format));
    }

    [Fact]
    public void MetadataWithTheInfo()
    {
        var info = PixImageIO.GetInfo(Fixture("metadata/meta.jpg"));
        Assert.Equal(("DJI", -90.0), (info.Metadata!.Make, info.Metadata.Pose!.CameraPitch));
        Assert.Equal(47.0707123, info.Metadata.Gps!.Latitude!.Value, 9);
        Assert.Equal("FC6310R", PixImageIO.ReadMetadata(Fixture("metadata/meta.png")).Model);
    }

    [Fact]
    public void KtxTexturesAndVolumes()
    {
        // image with a computed BC7 mip chain
        var img = PixImageIO.Load(Fixture("jpeg/photo_444.jpg"));
        var ms = new MemoryStream();
        PixImageIO.SaveKtx(img, ms, new KtxEncodeOptions { Compression = TextureCompression.BC7, GenerateMipmaps = true });
        var info = Ktx.GetTextureInfo(ms.ToArray());
        Assert.Equal(("BC7_SRGB_BLOCK", 8, TransferFunction.Srgb), (info.Format, info.MipLevels, info.Transfer));
        var mm = PixImageIO.LoadMipmap(new MemoryStream(ms.ToArray()));
        Assert.Equal((8, Col.Format.RGBA, new V2i(160, 120)), (mm.LevelCount, mm.ImageArray[0].Format, mm.ImageArray[0].Size));

        // a volume round trip, with a 3D mip chain
        var vol = new PixVolume<ushort>(Col.Format.Gray, 9, 6, 4);
        var t = vol.Tensor4;
        for (int z = 0; z < 4; z++)
            for (int y = 0; y < 6; y++)
                for (int x = 0; x < 9; x++) t[x, y, z, 0] = (ushort)(x * 1000 + y * 100 + z * 7);
        var vs = new MemoryStream();
        PixImageIO.SaveKtx(vol, vs, new KtxEncodeOptions { GenerateMipmaps = true });
        var back = (PixVolume<ushort>)PixImageIO.LoadVolume(new MemoryStream(vs.ToArray()));
        Assert.Equal(new V3i(9, 6, 4), back.Size);
        for (int z = 0; z < 4; z++)
            for (int y = 0; y < 6; y++)
                for (int x = 0; x < 9; x++) Assert.Equal(t[x, y, z, 0], back.Tensor4[x, y, z, 0]);
        Assert.Equal(new V3i(4, 3, 2), PixImageIO.LoadVolume(vs.ToArray(), mip: 1).Size);
        // DDS volumes load the same way
        var ds = new MemoryStream();
        PixImageIO.SaveDds(vol, ds);
        var dv = (PixVolume<ushort>)PixImageIO.LoadVolume(ds.ToArray());
        Assert.Equal(t[5, 4, 3, 0], dv.Tensor4[5, 4, 3, 0]);

        // cube maps
        var face = new PixImage<byte>(Col.Format.RGBA, 8, 8);
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) for (int c = 0; c < 4; c++) face.Volume[x, y, c] = (byte)(x * 30 + y * 5 + c * 60);
        var cube = new PixCube(Enumerable.Range(0, 6).Select(_ => new PixImageMipMap(face)).ToArray());
        var cs = new MemoryStream();
        PixImageIO.SaveKtx(cube, cs);
        var c2 = PixImageIO.LoadCube(cs.ToArray());
        Assert.Equal(Bytes(face), Bytes((PixImage<byte>)c2.MipMapArray[3].ImageArray[0]));
    }

    [Fact]
    public void SmallFormatsByExtension()
    {
        var img = (PixImage<byte>)PixImageIO.Load(Fixture("png/f0_c6_d08.png"));
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var ext in new[] { ".qoi", ".ktx2", ".ico", ".pam", ".ppm", ".hdr", ".pfm", ".pgm", ".pbm" })
            {
                var path = Path.Combine(dir, "x" + ext);
                PixImageIO.Save(img, path);
                var back = PixImageIO.Load(path);
                Assert.Equal(img.Size, back.Size);
                if (ext is ".qoi" or ".ktx2" or ".ico" or ".pam" or ".ppm") Assert.Equal(Bytes(img), Bytes((PixImage<byte>)back));
                Assert.Equal(ImageCodec.FormatFromExtension(path), PixImageIO.GetInfo(path).Format);
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void MetadataWritingAndSrgbLoading()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "a.jpg");
            File.Copy(Fixture("jpeg/photo_444.jpg"), path);
            PixImageIO.WriteMetadata(path, path, new MetadataEdit { Gps = new GpsEdit { Latitude = 47.07, Longitude = 15.44 }, Title = "pier" });
            var m = PixImageIO.ReadMetadata(path);
            Assert.Equal(47.07, m.Gps!.Latitude!.Value, 6);
            Assert.Equal("pier", m.Title);
            // no embedded profile: the pixels as they are
            var plain = (PixImage<byte>)PixImageIO.Load(path);
            var srgb = (PixImage<byte>)PixImageIO.LoadSrgb(path);
            Assert.Equal(Bytes(plain), Bytes(srgb));
        }
        finally { Directory.Delete(dir, true); }
    }
}
