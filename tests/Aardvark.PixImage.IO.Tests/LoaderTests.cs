using Aardvark.Data;
using Aardvark.Base;
using Xunit;

namespace PixCodecTests;

using PixImage = Aardvark.Base.PixImage;

public class LoaderTests
{
    static readonly PixCodecLoader Loader = PixCodec.Loader;

    static PixImage<T> Make<T>(Col.Format format, int w, int h, Func<int, T> value)
    {
        var img = new PixImage<T>(format, new V2i(w, h));
        var data = img.Volume.Data;
        for (int i = 0; i < data.Length; i++) data[i] = value(i);
        return img;
    }

    static PixImage<T> RoundTrip<T>(PixImage image, PixSaveParams p)
    {
        using var ms = new MemoryStream();
        Loader.SaveToStream(ms, image, p);
        ms.Position = 0;
        var info = Loader.GetInfoFromStream(ms);
        ms.Position = 0;
        var back = Loader.LoadFromStream(ms);
        Assert.Equal(back.Size, info.Size);
        Assert.Equal(back.PixFormat, info.Format);
        return Assert.IsType<PixImage<T>>(back);
    }

    static void AssertSame<T>(PixImage<T> expected, PixImage<T> actual)
    {
        Assert.Equal(expected.Size, actual.Size);
        Assert.Equal(expected.Format, actual.Format);
        Assert.Equal(expected.ToImageLayout().Volume.CopyToImage().Data, actual.Volume.Data);
    }

    [Theory]
    [InlineData(Col.Format.Gray), InlineData(Col.Format.GrayAlpha), InlineData(Col.Format.RGB), InlineData(Col.Format.RGBA)]
    public void PngAndTiffByte(Col.Format format)
    {
        var img = Make(format, 37, 21, i => (byte)(i * 7 + i / 13));
        AssertSame(img, RoundTrip<byte>(img, new PixPngSaveParams()));
        foreach (var c in new[] { PixTiffCompression.Default, PixTiffCompression.None, PixTiffCompression.Lzw, PixTiffCompression.Deflate, PixTiffCompression.PackBits })
            AssertSame(img, RoundTrip<byte>(img, new PixTiffSaveParams(c)));
    }

    [Fact]
    public void BgraIsSwappedToRgba()
    {
        var bgra = Make(Col.Format.BGRA, 9, 5, i => (byte)(i * 11));
        var back = RoundTrip<byte>(bgra, new PixPngSaveParams());
        Assert.Equal(Col.Format.RGBA, back.Format);
        AssertSame(bgra.ToFormat(Col.Format.RGBA), back);
        var bgr = Make(Col.Format.BGR, 9, 5, i => (byte)(i * 11));
        AssertSame(bgr.ToFormat(Col.Format.RGB), RoundTrip<byte>(bgr, new PixTiffSaveParams()));
    }

    [Fact]
    public void SubImageWithNonDenseVolume()
    {
        var img = Make(Col.Format.RGB, 40, 30, i => (byte)(i * 5 + i / 7));
        var sub = img.SubImage(new V2i(3, 4), new V2i(17, 11));
        var back = RoundTrip<byte>(sub, new PixPngSaveParams());
        Assert.Equal(sub.Size, back.Size);
        for (int y = 0; y < 11; y++)
            for (int x = 0; x < 17; x++)
                for (int c = 0; c < 3; c++)
                    Assert.Equal(sub.Volume[x, y, c], back.Volume[x, y, c]);
    }

    [Fact]
    public void UShort()
    {
        var img = Make(Col.Format.RGBA, 19, 23, i => (ushort)(i * 977));
        AssertSame(img, RoundTrip<ushort>(img, new PixPngSaveParams()));
        AssertSame(img, RoundTrip<ushort>(img, new PixTiffSaveParams()));
    }

    [Fact]
    public void TiffKeepsNativeTypes()
    {
        var f = Make(Col.Format.RGB, 13, 7, i => i * 0.37f - 20f);
        AssertSame(f, RoundTrip<float>(f, new PixTiffSaveParams()));
        var d = Make(Col.Format.Gray, 13, 7, i => i * 0.37 - 20.0);
        AssertSame(d, RoundTrip<double>(d, new PixTiffSaveParams(PixTiffCompression.Lzw)));
        var u = Make(Col.Format.Gray, 13, 7, i => (uint)i * 40000001u);
        AssertSame(u, RoundTrip<uint>(u, new PixTiffSaveParams()));
        var s = Make(Col.Format.Gray, 13, 7, i => (short)(i * 331 - 15000));
        AssertSame(s, RoundTrip<short>(s, new PixTiffSaveParams()));
        var i32 = Make(Col.Format.Gray, 13, 7, i => i * 20000003 - 5);
        AssertSame(i32, RoundTrip<int>(i32, new PixTiffSaveParams()));
        var sb = Make(Col.Format.Gray, 13, 7, i => (sbyte)(i * 3 - 100));
        AssertSame(sb, RoundTrip<sbyte>(sb, new PixTiffSaveParams()));
    }

    [Theory]
    [InlineData(PixExrCompression.Default), InlineData(PixExrCompression.None), InlineData(PixExrCompression.Zip), InlineData(PixExrCompression.Piz)]
    public void ExrFloat(PixExrCompression compression)
    {
        // values exactly representable as half, so the default HALF storage is lossless
        var img = Make(Col.Format.RGBA, 33, 17, i => (i % 512) / 64f - 2f);
        AssertSame(img, RoundTrip<float>(img, new PixExrSaveParams(compression)));
        var gray = Make(Col.Format.Gray, 33, 17, i => (i % 512) / 64f - 2f);
        AssertSame(gray, RoundTrip<float>(gray, new PixExrSaveParams(compression)));
    }

    [Fact]
    public void ExrLuminanceChroma()
    {
        var img = new PixImage<float>(Col.Format.RGB, new V2i(32, 16));
        img.Volume.SetByCoord((x, y, c) => 0.5f + 0.4f * MathF.Sin(x / 9f + c) * MathF.Cos(y / 7f));
        var back = RoundTrip<float>(img, new PixExrSaveParams(PixExrCompression.Zip, luminanceChroma: true));
        Assert.Equal(Col.Format.RGB, back.Format);
        var a = img.Volume.Data; var b = back.Volume.Data;
        double se = 0;
        for (int i = 0; i < a.Length; i++) se += (a[i] - b[i]) * (double)(a[i] - b[i]);
        Assert.True(Math.Sqrt(se / a.Length) < 0.05); // lossy: chroma is subsampled 2x2
    }

    [Fact]
    public void ExrFromByteIsNormalized()
    {
        var img = Make(Col.Format.RGB, 8, 8, i => (byte)(i * 5));
        var back = RoundTrip<float>(img, new PixExrSaveParams());
        var exp = img.ToPixImage<float>().Volume.Data;
        for (int i = 0; i < exp.Length; i++) Assert.True(Math.Abs(exp[i] - back.Volume.Data[i]) < 1e-3f); // stored as half
    }

    [Theory]
    [InlineData(Col.Format.Gray), InlineData(Col.Format.RGB), InlineData(Col.Format.RGBA), InlineData(Col.Format.GrayAlpha)]
    public void BmpAndTga(Col.Format format)
    {
        var img = Make(format, 23, 9, i => (byte)(i * 11 + 1));
        var bmpBack = RoundTrip<byte>(img, new PixSaveParams(PixFileFormat.Bmp));
        // BMP has no gray + alpha: it comes back as RGBA
        AssertSame(format == Col.Format.GrayAlpha ? img.ToFormat(Col.Format.RGBA) : img, bmpBack);
        AssertSame(img, RoundTrip<byte>(img, new PixSaveParams(PixFileFormat.Targa)));
        var path = Path.Combine(Path.GetTempPath(), $"pixcodec-{Guid.NewGuid():N}.tga");
        try
        {
            PixCodec.Save(img, path);
            AssertSame(img, PixCodec.Load<byte>(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Gif()
    {
        var img = Make(Col.Format.RGB, 16, 16, i => (byte)((i / 3 % 16) * 17)); // 16 colours: lossless
        AssertSame(img, RoundTrip<byte>(img, new PixSaveParams(PixFileFormat.Gif)));
    }

    [Theory]
    [InlineData(Col.Format.Gray), InlineData(Col.Format.RGB)]
    public void Jpeg(Col.Format format)
    {
        var img = new PixImage<byte>(format, new V2i(64, 48));
        img.Volume.SetByCoord((x, y, c) => (byte)(128 + 100 * Math.Sin(x / 9.0 + c) * Math.Cos(y / 7.0)));
        var back = RoundTrip<byte>(img, new PixJpegSaveParams(95));
        Assert.Equal(img.Format, back.Format);
        var a = img.Volume.Data; var b = back.Volume.Data;
        double se = 0;
        for (int i = 0; i < a.Length; i++) se += (a[i] - b[i]) * (double)(a[i] - b[i]);
        Assert.True(10 * Math.Log10(255 * 255 / (se / a.Length + 1e-9)) > 38);
        // 16 bit input is reduced to 8 bit
        Assert.Equal(img.Size, RoundTrip<byte>(img.ToPixImage<ushort>(), new PixJpegSaveParams()).Size);
    }

    [Fact]
    public void RegisteredLoaderIsUsedByPixImage()
    {
        PixCodec.Init();
        Assert.Contains(PixImage.GetLoaders(), l => l is PixCodecLoader);
        var img = Make(Col.Format.RGBA, 12, 10, i => (byte)(i * 3));
        var path = Path.Combine(Path.GetTempPath(), $"pixcodec-{Guid.NewGuid():N}.png");
        try
        {
            img.Save(path);
            var back = PixImage.Load(path, Loader);
            AssertSame(img, Assert.IsType<PixImage<byte>>(back));
            Assert.Equal(new V2i(12, 10), PixImage.GetInfoFromFile(path, Loader).Size);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GarbageThrows()
    {
        Assert.ThrowsAny<Exception>(() => Loader.LoadFromStream(new MemoryStream(new byte[100])));
    }
}
