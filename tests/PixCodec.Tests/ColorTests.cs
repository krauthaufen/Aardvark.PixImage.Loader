using Xunit;
using static PixCodec.Tests.TestUtil;

namespace PixCodec.Tests;

/// <summary>Transfer functions: what files declare or imply, the conversions to and from linear light, writing them back.</summary>
public class ColorTests
{
    [Fact]
    public void ImpliedAndDeclared()
    {
        Assert.Equal(TransferFunction.Srgb, ImageCodec.GetInfo(Fixture("jpeg/photo_444.jpg")).Transfer);
        Assert.Equal(TransferFunction.Srgb, ImageCodec.GetInfo(Fixture("png/f0_c6_d08.png")).Transfer);
        var exr = ImageCodec.Encode(new RawImage(2, 2, ColorModel.Rgb, new float[12]), ImageFormat.Exr);
        Assert.Equal(TransferFunction.Linear, ImageCodec.GetInfo(exr).Transfer);
        var dds = Dds.Encode(new RawImage(4, 4, ColorModel.Rgba, new byte[64]), new DdsEncodeOptions { Compression = DdsCompression.BC7, Srgb = true });
        Assert.Equal(TransferFunction.Srgb, ImageCodec.GetInfo(dds).Transfer);
        var ddsLin = Dds.Encode(new RawImage(4, 4, ColorModel.Rgba, new byte[64]), new DdsEncodeOptions { Compression = DdsCompression.BC7 });
        Assert.Equal(TransferFunction.Unknown, ImageCodec.GetInfo(ddsLin).Transfer);
        var tiffF = ImageCodec.Encode(new RawImage(2, 2, ColorModel.Gray, new float[4]), ImageFormat.Tiff);
        Assert.Equal(TransferFunction.Linear, ImageCodec.GetInfo(tiffF).Transfer);
        var tiffI = ImageCodec.Encode(new RawImage(2, 2, ColorModel.Gray, new short[4]), ImageFormat.Tiff);
        Assert.Equal(TransferFunction.Unknown, ImageCodec.GetInfo(tiffI).Transfer);
    }

    [Fact]
    public void PngChunksDecide()
    {
        var img = new RawImage(3, 2, ColorModel.Rgb, new ushort[18]);
        // gAMA 1.0: linear, written for linear images
        img.Metadata = new ImageMetadata { Transfer = TransferFunction.Linear };
        var lin = Png.Encode(img);
        Assert.Equal(TransferFunction.Linear, Png.GetInfo(lin).Transfer);
        Assert.Equal(TransferFunction.Linear, Png.Decode(lin).Metadata!.Transfer);
        Assert.Equal(TransferFunction.Linear, ImageCodec.Decode(lin).Metadata!.Transfer);
        // another gAMA: a power law
        img.Metadata = new ImageMetadata { Gamma = 0.5 };
        var g = Png.Encode(img);
        Assert.Equal(TransferFunction.Gamma, Png.GetInfo(g).Transfer);
        var dg = Png.Decode(g);
        Assert.Equal((TransferFunction.Gamma, 0.5), (dg.Metadata!.Transfer, dg.Metadata.Gamma));
        // sRGB chunk wins over gAMA
        var s = Png.Encode(img, new PngEncodeOptions { Metadata = new PngMetadata { SrgbIntent = PngRenderingIntent.Perceptual } });
        Assert.Equal(TransferFunction.Srgb, Png.GetInfo(s).Transfer);
        // nothing: sRGB, and no metadata object just for that
        var plain = Png.Encode(new RawImage(3, 2, ColorModel.Rgb, new byte[18]));
        Assert.Equal(TransferFunction.Srgb, Png.GetInfo(plain).Transfer);
        Assert.Null(Png.Decode(plain).Metadata);
    }

    [Fact]
    public void LinearConversions()
    {
        var src = new RawImage(256, 1, ColorModel.Gray, Enumerable.Range(0, 256).Select(i => (byte)i).ToArray());
        var lin = ImageOps.SrgbToLinear(src);
        var f = lin.GetData<float>();
        Assert.Equal(SampleType.Float32, lin.SampleType);
        Assert.Equal(TransferFunction.Linear, lin.Metadata!.Transfer);
        Assert.Equal(0, f[0]);
        Assert.Equal(1, f[255], 6);
        Assert.Equal(0.21586, f[128], 4);    // IEC 61966-2-1
        Assert.Equal(10 / 255.0 / 12.92, f[10], 6);   // linear segment
        // back to 8 and 16 bit
        AssertEqual(src, ImageOps.LinearToSrgb(lin));
        var w = ImageOps.LinearToSrgb(lin, SampleType.UInt16).GetData<ushort>();
        Assert.Equal(128 * 257, w[128]);
        // alpha is only rescaled
        var ga = new RawImage(1, 1, ColorModel.GrayAlpha, new byte[] { 128, 128 });
        var l2 = ImageOps.SrgbToLinear(ga).GetData<float>();
        Assert.Equal(128 / 255f, l2[1], 6);
        Assert.NotEqual(l2[0], l2[1]);
        // floats pass through ToLinear unchanged; gamma and PQ curves
        var fl = new RawImage(2, 1, ColorModel.Gray, new float[] { 0.25f, 2f });
        Assert.Equal(new[] { 0.25f, 2f }, ImageOps.ToLinear(fl).GetData<float>());
        var gam = new RawImage(1, 1, ColorModel.Gray, new byte[] { 128 }) { Metadata = new ImageMetadata { Transfer = TransferFunction.Gamma, Gamma = 0.5 } };
        Assert.Equal(Math.Pow(128 / 255.0, 2), ImageOps.ToLinear(gam).GetData<float>()[0], 5);
        var pq = new RawImage(2, 1, ColorModel.Gray, new ushort[] { 0, 65535 });
        var pl = ImageOps.ToLinear(pq, TransferFunction.Pq).GetData<float>();
        Assert.Equal((0f, 1f), (pl[0], MathF.Round(pl[1], 5)));
        Assert.Throws<ArgumentException>(() => ImageOps.SrgbToLinear(new RawImage(1, 1, ColorModel.Gray, new short[1])));
    }

    [Fact]
    public void MipmapsInLinearLight()
    {
        // a 1 pixel checkerboard of black and white averages to 50 % light, which is sRGB 188, not 128
        var b = new byte[8 * 8 * 3];
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) if (((x + y) & 1) == 1) b[(y * 8 + x) * 3] = b[(y * 8 + x) * 3 + 1] = b[(y * 8 + x) * 3 + 2] = 255;
        var img = new RawImage(8, 8, ColorModel.Rgb, b);
        var k = Ktx.DecodeMipmaps(Ktx.Encode(img, new KtxEncodeOptions { GenerateMipmaps = true }));
        Assert.InRange(k[1].GetData<byte>()[0], 187, 189);
        var d = Dds.DecodeMipmaps(Dds.Encode(img, new DdsEncodeOptions { Mipmaps = DdsMipmaps.Generate, Srgb = true }));
        Assert.InRange(d[1].GetData<byte>()[0], 187, 189);
        // linear data is averaged as it is
        img.Metadata = new ImageMetadata { Transfer = TransferFunction.Linear };
        var kl = Ktx.DecodeMipmaps(Ktx.Encode(img, new KtxEncodeOptions { GenerateMipmaps = true }));
        Assert.InRange(kl[1].GetData<byte>()[0], 127, 128);
    }
}
