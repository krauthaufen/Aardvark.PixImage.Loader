using System.Text.RegularExpressions;
using Xunit;
using static Aardvark.ImageCodecs.Tests.TestUtil;

namespace Aardvark.ImageCodecs.Tests;

public class DdsTests
{
    public static IEnumerable<object[]> Fixtures => Files("dds", ".dds");

    /// <summary>BC1 to BC5 are specified as real valued interpolations: decoders may round either way.</summary>
    static double Tolerance(string file)
        => Regex.IsMatch(Path.GetFileName(file), @"^(bc[1-5]|legacy_(dxt|ati|bc)|mips_bc1|pil_.*_dxt|im_dxt)") ? 1 : 0;

    [Theory, MemberData(nameof(Fixtures))]
    public void DecodeMatchesReference(string file)
    {
        var path = Fixture(file);
        var data = File.ReadAllBytes(path);
        double tol = Tolerance(file);
        var img = Dds.Decode(data);
        var tex = Dds.GetTextureInfo(data);
        if (tex.AlphaMode == DdsAlphaMode.Premultiplied)
        {
            // the reference holds the stored (premultiplied) colours: dividing by alpha would scale the allowed rounding
            var stored = Dds.Decode(data, new DdsDecodeOptions { Unpremultiply = false });
            AssertEqual(LoadRef(path + ".ref"), stored, tol);
            var a = stored.GetData<byte>();
            var u = img.GetData<byte>();
            for (int i = 0; i < a.Length; i += 4)
                for (int c = 0; c < 3; c++)
                {
                    int al = a[i + 3];
                    Assert.Equal(al is 0 or 255 ? a[i + c] : Math.Min(255, (a[i + c] * 255 + al / 2) / al), u[i + c]);
                }
            return;
        }
        AssertEqual(LoadRef(path + ".ref"), img, tol);

        var info = Dds.GetInfo(data);
        Assert.Equal((img.Width, img.Height, img.Channels, img.SampleType, img.Color), (info.Width, info.Height, info.Channels, info.SampleType, info.Color));
        Assert.Equal((ImageFormat.Dds, tex.MipLevels), (info.Format, info.MipLevels));
        Assert.Equal(info, ImageCodec.GetInfo(data));
        Assert.Equal(info, ImageCodec.GetInfo(new MemoryStream(data)));
        AssertEqual(img, ImageCodec.Decode(data));

        // every surface, where the generator wrote them
        var all = Dds.DecodeTexture(data);
        var refs = Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*.*.*.*.ref");
        foreach (var r in refs)
        {
            var k = r[(path.Length + 1)..^4].Split('.').Select(int.Parse).ToArray();
            AssertEqual(LoadRef(r), all.GetImage(k[2], k[0], k[1], k[3]), tol);
        }
        int faces = Math.Max(1, all.Faces.Count);
        Assert.Equal(info.FrameCount, all.ArraySize * faces);
        var chain = Dds.DecodeMipmaps(data);
        Assert.Equal(tex.MipLevels, chain.Length);
        for (int m = 0; m < chain.Length; m++) AssertEqual(all.GetImage(m), chain[m]);
        var generic = ImageCodec.DecodeMipmaps(data);
        Assert.Equal(chain.Length, generic.Length);
        var frames = ImageCodec.DecodeAll(data);
        Assert.Equal(all.ArraySize * faces, frames.Length);
    }

    [Fact]
    public void Structure()
    {
        var cube = Dds.GetTextureInfo(File.ReadAllBytes(Fixture("dds/cube_legacy_some_faces.dds")));
        Assert.Equal(DdsTextureKind.CubeMap, cube.Kind);
        Assert.Equal(new[] { 0, 3, 4 }, cube.Faces);
        var dx = Dds.GetTextureInfo(File.ReadAllBytes(Fixture("dds/cube_dx10_mips.dds")));
        Assert.Equal((DdsTextureKind.CubeMap, 6, 4), (dx.Kind, dx.Faces.Count, dx.MipLevels));
        var vol = Dds.DecodeTexture(File.ReadAllBytes(Fixture("dds/volume_legacy_mips.dds")));
        Assert.Equal((DdsTextureKind.Texture3D, 4, 3), (vol.Kind, vol.Depth, vol.MipLevels));
        Assert.Equal(new[] { 4, 2, 1 }, Enumerable.Range(0, 3).Select(m => vol.GetSlices(m).Length));
        var arr = Dds.GetTextureInfo(File.ReadAllBytes(Fixture("dds/array3_mips.dds")));
        Assert.Equal((DdsTextureKind.Texture2D, 3, 3), (arr.Kind, arr.ArraySize, arr.MipLevels));
        var bc7 = Dds.GetTextureInfo(File.ReadAllBytes(Fixture("dds/bc7_13x7.dds")));
        Assert.Equal(("BC7_UNORM", 98, true), (bc7.Format, bc7.DxgiFormat, bc7.IsBlockCompressed));
        Assert.Equal(DdsAlphaMode.Premultiplied, Dds.GetTextureInfo(File.ReadAllBytes(Fixture("dds/legacy_dxt2.dds"))).AlphaMode);
        Assert.Equal(SampleType.Float16, Dds.GetTextureInfo(File.ReadAllBytes(Fixture("dds/bc6h_sf16_13x7.dds"))).SampleType);
    }

    public static IEnumerable<object[]> EncodeCases()
    {
        foreach (var type in new[] { SampleType.UInt8, SampleType.UInt16, SampleType.Float16, SampleType.Float32 })
            foreach (var color in new[] { ColorModel.Gray, ColorModel.GrayAlpha, ColorModel.Rgb, ColorModel.Rgba })
                yield return new object[] { type, color };
        yield return new object[] { SampleType.UInt8, ColorModel.Other };
        yield return new object[] { SampleType.Float32, ColorModel.Other };
    }

    static RawImage Pattern(int w, int h, ColorModel color, SampleType type, int seed)
    {
        int ch = color == ColorModel.Other ? 2 : 0;
        var img = RawImage.Create(w, h, color, type, ch);
        var rnd = new Random(seed);
        var b = img.GetBytes();
        if (type is SampleType.Float16)
        {
            var d = img.GetData<Half>();
            for (int i = 0; i < d.Length; i++) d[i] = (Half)(rnd.NextDouble() * 4 - 1);
        }
        else if (type is SampleType.Float32)
        {
            var d = img.GetData<float>();
            for (int i = 0; i < d.Length; i++) d[i] = (float)(rnd.NextDouble() * 4 - 1);
        }
        else rnd.NextBytes(b);
        return img;
    }

    /// <summary>What comes back: gray + alpha and (for 16 bit / half) gray and rgb are stored as RGBA.</summary>
    static RawImage Expected(RawImage img)
    {
        bool expand = img.Color == ColorModel.GrayAlpha && img.SampleType != SampleType.UInt8
            || img.Color == ColorModel.Rgb && img.SampleType is SampleType.UInt16 or SampleType.Float16;
        if (!expand) return img;
        var v = ToDoubles(img);
        int n = img.Width * img.Height, ch = img.Channels;
        var o = RawImage.Create(img.Width, img.Height, ColorModel.Rgba, img.SampleType);
        double one = img.SampleType == SampleType.UInt16 ? 65535 : 1;
        var r = new double[n * 4];
        for (int i = 0; i < n; i++)
        {
            for (int c = 0; c < 3; c++) r[i * 4 + c] = v[i * ch + (ch <= 2 ? 0 : c)];
            r[i * 4 + 3] = ch == 2 ? v[i * 2 + 1] : one;
        }
        switch (img.SampleType)
        {
            case SampleType.UInt16: { var d = o.GetData<ushort>(); for (int i = 0; i < r.Length; i++) d[i] = (ushort)r[i]; break; }
            case SampleType.Float16: { var d = o.GetData<Half>(); for (int i = 0; i < r.Length; i++) d[i] = (Half)r[i]; break; }
            default: { var d = o.GetData<float>(); for (int i = 0; i < r.Length; i++) d[i] = (float)r[i]; break; }
        }
        return o;
    }

    [Theory, MemberData(nameof(EncodeCases))]
    public void EncodeRoundTrips(SampleType type, ColorModel color)
    {
        var img = Pattern(13, 6, color, type, (int)type * 10 + (int)color);
        var bytes = Dds.Encode(img);
        var back = Dds.Decode(bytes);
        var exp = Expected(img);
        AssertEqual(exp, back);
        Assert.Equal((exp.Color, exp.Channels), (back.Color, back.Channels));
        // with a computed mip chain
        var chainBytes = Dds.Encode(img, new DdsEncodeOptions { Mipmaps = DdsMipmaps.Generate });
        var chain = Dds.DecodeMipmaps(chainBytes);
        Assert.Equal(4, chain.Length);   // 13x6, 6x3, 3x1, 1x1
        Assert.Equal(new[] { (13, 6), (6, 3), (3, 1), (1, 1) }, chain.Select(c => (c.Width, c.Height)));
        AssertEqual(exp, chain[0]);
        for (int m = 1; m < chain.Length; m++)
            AssertEqual(Expected(ImageOps.Resize(img, chain[m].Width, chain[m].Height, new ResizeOptions { Filter = ResampleFilter.Box })), chain[m]);
    }

    [Fact]
    public void EncodedFilesReadByOthers()
    {
        foreach (var color in new[] { ColorModel.Gray, ColorModel.Rgb, ColorModel.Rgba })
        {
            var img = Pattern(16, 8, color, SampleType.UInt8, 3);
            var oracle = OracleDecode(Dds.Encode(img), ".dds");
            if (oracle == null) return;
            AssertEqual(img, oracle);
        }
    }

    [Fact]
    public void CubeMapRoundTrip()
    {
        var faces = Enumerable.Range(0, 6).Select(i => (IReadOnlyList<RawImage>)new[] { Pattern(8, 8, ColorModel.Rgba, SampleType.UInt8, i) }).ToList();
        var bytes = new MemoryStream();
        Dds.EncodeCube(faces, bytes, new DdsEncodeOptions { Mipmaps = DdsMipmaps.Generate });
        var t = Dds.DecodeTexture(bytes.ToArray());
        Assert.Equal((DdsTextureKind.CubeMap, 6, 4), (t.Kind, t.Faces.Count, t.MipLevels));
        for (int f = 0; f < 6; f++) AssertEqual(faces[f][0], t.GetImage(0, 0, f));
        Assert.Throws<ArgumentException>(() => Dds.EncodeCube(faces.Take(5).ToList(), new MemoryStream()));
    }

    [Fact]
    public void SrgbAndRejections()
    {
        var img = Pattern(5, 3, ColorModel.Rgb, SampleType.UInt8, 1);
        var bytes = Dds.Encode(img, new DdsEncodeOptions { Srgb = true });
        var info = Dds.GetTextureInfo(bytes);
        Assert.Equal(("R8G8B8A8_UNORM_SRGB", true), (info.Format, info.IsSrgb));
        Assert.Throws<ImageNotSupportedException>(() => Dds.Encode(RawImage.Create(2, 2, ColorModel.Rgb, SampleType.Int32)));
        Assert.Throws<ArgumentException>(() => Dds.EncodeMipmaps(new[] { img, Pattern(3, 2, ColorModel.Rgb, SampleType.UInt8, 2) }, new MemoryStream()));
    }

    [Theory, MemberData(nameof(Fixtures))]
    public void DamagedFilesFailCleanly(string file)
    {
        var data = File.ReadAllBytes(Fixture(file));
        var rnd = new Random(file.Length);
        for (int k = 0; k < 40; k++)
        {
            var d = k < 15 ? data.AsSpan(0, rnd.Next(data.Length)).ToArray() : (byte[])data.Clone();
            if (k >= 15)
                for (int j = 0; j < 1 + k % 4; j++) d[rnd.Next(Math.Min(d.Length, 160))] = (byte)rnd.Next(256);
            try { Dds.DecodeTexture(d); Dds.GetInfo(d); }
            catch (ImageFormatException) { }
            catch (ImageNotSupportedException) { }
        }
    }
}
