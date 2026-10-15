using Aardvark.Base;
using Xunit;

namespace PixImageIOTests;

using PixImage = Aardvark.Base.PixImage;

public class Jp2LoaderTests
{
    static readonly PixImageIOLoader Loader = PixImageIO.Loader;

    static PixImage<T> Make<T>(Col.Format format, int w, int h, Func<int, T> value)
    {
        var img = new PixImage<T>(format, new V2i(w, h));
        var data = img.Volume.Data;
        for (int i = 0; i < data.Length; i++) data[i] = value(i);
        return img;
    }

    static PixImage<T> RoundTrip<T>(PixImage image, PixFileFormat format)
    {
        using var ms = new MemoryStream();
        Loader.SaveToStream(ms, image, new PixSaveParams(format));
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

    /// <summary>JPEG 2000 is written lossless: 8 and 16 bit images of every colour layout come back exactly.</summary>
    [Theory]
    [InlineData(Col.Format.Gray), InlineData(Col.Format.GrayAlpha), InlineData(Col.Format.RGB), InlineData(Col.Format.RGBA)]
    public void LosslessRoundTrip(Col.Format format)
    {
        foreach (var file in new[] { PixFileFormat.Jp2, PixFileFormat.J2k })
        {
            var b = Make(format, 67, 41, i => (byte)(i * 7 + i / 13));
            AssertSame(b, RoundTrip<byte>(b, file));
            var s = Make(format, 35, 52, i => (ushort)(i * 977 + i / 3));
            AssertSame(s, RoundTrip<ushort>(s, file));
        }
    }

    [Fact]
    public void BgrIsSwappedAndTheContainerIsRecognized()
    {
        var bgra = Make(Col.Format.BGRA, 23, 17, i => (byte)(i * 11));
        var back = RoundTrip<byte>(bgra, PixFileFormat.Jp2);
        Assert.Equal(Col.Format.RGBA, back.Format);
        AssertSame(bgra.ToFormat(Col.Format.RGBA), back);
        var bgr = Make(Col.Format.BGR, 23, 17, i => (ushort)(i * 1234));
        AssertSame(bgr.ToFormat(Col.Format.RGB), RoundTrip<ushort>(bgr, PixFileFormat.J2k));

        // a JP2 file starts with its signature box, a raw codestream with SOC
        using var jp2 = new MemoryStream();
        Loader.SaveToStream(jp2, bgr, new PixSaveParams(PixFileFormat.Jp2));
        Assert.Equal(new byte[] { 0, 0, 0, 12, 0x6A, 0x50, 0x20, 0x20 }, jp2.ToArray().AsSpan(0, 8).ToArray());
        using var j2k = new MemoryStream();
        Loader.SaveToStream(j2k, bgr, new PixSaveParams(PixFileFormat.J2k));
        Assert.Equal(new byte[] { 0xFF, 0x4F, 0xFF, 0x51 }, j2k.ToArray().AsSpan(0, 4).ToArray());
    }

    [Fact]
    public void FilesByExtension()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pixcodec-jp2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var img = Make(Col.Format.RGB, 40, 30, i => (byte)(i * 5));
            foreach (var (name, format) in new[] { ("a.jp2", PixFileFormat.Jp2), ("a.j2k", PixFileFormat.J2k) })
            {
                var path = Path.Combine(dir, name);
                Loader.SaveToFile(path, img, new PixSaveParams(format));
                Assert.Equal(new V2i(40, 30), Loader.GetInfoFromFile(path).Size);
                AssertSame(img, Assert.IsType<PixImage<byte>>(Loader.LoadFromFile(path)));
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void FloatImagesAreConvertedToWhatTheFormatHolds()
    {
        // the adapter writes 8 or 16 bit; a float image goes out as 16 bit
        var f = Make(Col.Format.RGB, 20, 10, i => (i % 100) / 100f);
        using var ms = new MemoryStream();
        Loader.SaveToStream(ms, f, new PixSaveParams(PixFileFormat.Jp2));
        ms.Position = 0;
        var back = Loader.LoadFromStream(ms);
        Assert.Equal(new V2i(20, 10), back.Size);
        Assert.Equal(Col.Format.RGB, back.Format);
        Assert.True(back is PixImage<ushort> || back is PixImage<byte>);
    }
}
