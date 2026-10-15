using Aardvark.Data;
using Aardvark.Base;
using Xunit;

namespace PixCodecTests;

using PixImage = Aardvark.Base.PixImage;

public class WebpLoaderTests
{
    static readonly PixCodecLoader Loader = PixCodec.Loader;

    static PixImage<byte> Make(Col.Format format, int w, int h)
    {
        var img = new PixImage<byte>(format, new V2i(w, h));
        int alpha = format == Col.Format.RGBA || format == Col.Format.BGRA ? 3 : format == Col.Format.GrayAlpha ? 1 : -1;
        img.Volume.SetByCoord((x, y, c) => c == alpha
            ? (byte)(x < w / 3 ? 0 : x > 2 * w / 3 ? 255 : 100 + x)
            : (byte)Math.Clamp(128 + 90 * Math.Sin(x / 9.0 + c) * Math.Cos(y / 7.0) + ((x / 8 + y / 8) % 2) * 20, 0, 255));
        return img;
    }

    static PixImage<byte> RoundTrip(PixImage image, PixSaveParams p)
    {
        using var ms = new MemoryStream();
        Loader.SaveToStream(ms, image, p);
        ms.Position = 0;
        var info = Loader.GetInfoFromStream(ms);
        ms.Position = 0;
        var back = Loader.LoadFromStream(ms);
        Assert.Equal(back.Size, info.Size);
        Assert.Equal(back.PixFormat, info.Format);
        return Assert.IsType<PixImage<byte>>(back);
    }

    static double Psnr(PixImage<byte> a, PixImage<byte> b)
    {
        Assert.Equal(a.Size, b.Size);
        Assert.Equal(a.Format, b.Format);
        var x = a.ToImageLayout().Volume.CopyToImage().Data;
        var y = b.Volume.Data;
        double se = 0;
        for (int i = 0; i < x.Length; i++) se += (x[i] - y[i]) * (double)(x[i] - y[i]);
        return 10 * Math.Log10(255 * 255 / (se / x.Length + 1e-9));
    }

    [Theory]
    [InlineData(Col.Format.RGB), InlineData(Col.Format.RGBA)]
    public void LosslessIsExact(Col.Format format)
    {
        var img = Make(format, 67, 41);
        var back = RoundTrip(img, new PixWebpSaveParams(PixWebpSaveParams.DefaultQuality, true));
        Assert.Equal(format, back.Format);
        Assert.Equal(img.ToImageLayout().Volume.CopyToImage().Data, back.Volume.Data);
    }

    [Fact]
    public void LosslessSwapsBgrAndExpandsGray()
    {
        var bgra = Make(Col.Format.BGRA, 23, 17);
        var back = RoundTrip(bgra, new PixWebpSaveParams(75, true));
        Assert.Equal(Col.Format.RGBA, back.Format);
        Assert.Equal(bgra.ToFormat(Col.Format.RGBA).ToImageLayout().Volume.CopyToImage().Data, back.Volume.Data);

        // WebP has no gray: it comes back as RGB with equal channels
        var gray = Make(Col.Format.Gray, 23, 17);
        var rgb = RoundTrip(gray, new PixWebpSaveParams(75, true));
        Assert.Equal(Col.Format.RGB, rgb.Format);
        for (int y = 0; y < 17; y++)
            for (int x = 0; x < 23; x++)
                for (int c = 0; c < 3; c++) Assert.Equal(gray.Volume[x, y, 0], rgb.Volume[x, y, c]);
        var ga = RoundTrip(Make(Col.Format.GrayAlpha, 23, 17), new PixWebpSaveParams(75, true));
        Assert.Equal(Col.Format.RGBA, ga.Format);
    }

    [Theory]
    [InlineData(Col.Format.RGB), InlineData(Col.Format.RGBA)]
    public void LossyByPsnr(Col.Format format)
    {
        var img = Make(format, 96, 72);
        double last = 0;
        long lastSize = 0;
        foreach (int q in new[] { 30, 75, 95 })
        {
            using var ms = new MemoryStream();
            Loader.SaveToStream(ms, img, new PixWebpSaveParams(q));
            var back = RoundTrip(img, new PixWebpSaveParams(q));
            Assert.Equal(format, back.Format);
            double psnr = Psnr(img, back);
            Assert.True(psnr > (q >= 95 ? 40 : q >= 75 ? 35 : 30), $"PSNR {psnr:F2} at quality {q}");
            Assert.True(psnr > last && ms.Length > lastSize);
            last = psnr; lastSize = ms.Length;
        }
        // the default parameters are lossy
        Assert.True(Psnr(img, RoundTrip(img, new PixWebpSaveParams())) > 33);
        // 16 bit input is reduced to 8 bit
        Assert.Equal(img.Size, RoundTrip(img.ToPixImage<ushort>(), new PixWebpSaveParams()).Size);
    }

    [Fact]
    public void FileRoundTrip()
    {
        var img = Make(Col.Format.RGBA, 31, 19);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".webp");
        try
        {
            Loader.SaveToFile(path, img, new PixWebpSaveParams(80, true));
            Assert.Equal(new V2i(31, 19), Loader.GetInfoFromFile(path).Size);
            var back = Assert.IsType<PixImage<byte>>(Loader.LoadFromFile(path));
            Assert.Equal(img.Volume.Data, back.Volume.Data);
        }
        finally { File.Delete(path); }
    }
}
