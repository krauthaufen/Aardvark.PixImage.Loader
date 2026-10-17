using Aardvark.Data;
using Aardvark.Base;
using Aardvark.ImageCodecs;
using Xunit;

namespace PixCodecTests;

using PixImage = Aardvark.Base.PixImage;

/// <summary>JPEG XR through the PixImage loader.</summary>
public class JpegXrLoaderTests
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
    [InlineData("jpegxr/rgb24_lossless.jxr", typeof(byte), Col.Format.RGB)]
    [InlineData("jpegxr/bgr24_q60.jxr", typeof(byte), Col.Format.RGB)]
    [InlineData("jpegxr/bgra32_planar_q.jxr", typeof(byte), Col.Format.RGBA)]
    [InlineData("jpegxr/rgba64_interleaved.jxr", typeof(ushort), Col.Format.RGBA)]
    [InlineData("jpegxr/gray8_lossless.jxr", typeof(byte), Col.Format.Gray)]
    [InlineData("jpegxr/gray16_q60.jxr", typeof(ushort), Col.Format.Gray)]
    [InlineData("jpegxr/rgbfloat128_lossless.jxr", typeof(float), Col.Format.RGB)]
    [InlineData("jpegxr/rgbfix48_q60.jxr", typeof(float), Col.Format.RGB)]
    [InlineData("jpegxr/rgbe_lossless.jxr", typeof(float), Col.Format.RGB)]
    [InlineData("jpegxr/cmyk32_lossless.jxr", typeof(byte), Col.Format.RGB)]
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
            else if (type == typeof(ushort)) Assert.Equal((ushort[])raw.Data, Samples<ushort>(p));
            else Assert.Equal((float[])raw.Data, Samples<float>(p));
        }
        var info = Loader.GetInfoFromFile(path);
        Assert.Equal(new V2i(raw.Width, raw.Height), info.Size);
        Assert.Equal(type, info.Format.Type);
        Assert.Equal(format, info.Format.Format);
    }

    [Fact]
    public void HalfFloatBecomesFloat()
    {
        var p = PixCodec.Load(Fixture("jpegxr/rgbhalf48_lossless.jxr"));
        Assert.Equal(typeof(float), p.PixFormat.Type);
    }

    [Fact]
    public void SaveRoundTrips()
    {
        var img = new PixImage<byte>(Col.Format.RGBA, new V2i(37, 21));
        var vol = img.Volume;
        for (int y = 0; y < 21; y++)
            for (int x = 0; x < 37; x++)
                for (int c = 0; c < 4; c++) vol[x, y, c] = (byte)(c == 3 ? 200 + x % 50 : (x * 5 + y * 3 + c * 40) % 256);
        foreach (var ext in new[] { ".jxr", ".wdp", ".hdp" })
        {
            var path = Path.Combine(Path.GetTempPath(), $"pixcodec-{Guid.NewGuid():N}{ext}");
            try
            {
                // default: lossless
                PixCodec.Save(img, path);
                Assert.Equal(ImageFormat.JpegXr, ImageCodec.GetInfo(path).Format);
                var back = PixCodec.Load(path);
                Assert.Equal(Col.Format.RGBA, back.Format);
                Assert.Equal(Samples<byte>(img), Samples<byte>(back));
                // lossy through SaveJpegXr
                using (var fs = File.Create(path)) PixCodec.SaveJpegXr(img, fs, new JpegXrEncodeOptions { Quality = 0.8 });
                var a = Samples<byte>(img); var b = Samples<byte>(PixCodec.Load(path));
                double err = 0;
                for (int i = 0; i < a.Length; i++)
                {
                    if (i % 4 == 3) Assert.Equal(a[i], b[i]);   // alpha stays lossless by default
                    else err += Math.Abs(a[i] - b[i]);
                }
                Assert.True(err / (a.Length * 3 / 4) < 6, $"mean error {err / (a.Length * 3 / 4)}");
            }
            finally { File.Delete(path); }
        }
        // other sample types are written as float
        var f = new PixImage<double>(Col.Format.Gray, new V2i(5, 3));
        for (int y = 0; y < 3; y++) for (int x = 0; x < 5; x++) f.Volume[x, y, 0] = x * 0.125 + y * 0.25;
        var fp = Path.Combine(Path.GetTempPath(), $"pixcodec-{Guid.NewGuid():N}.jxr");
        try
        {
            PixCodec.SaveJpegXr(f, fp);
            var fb = PixCodec.Load(fp);
            Assert.Equal(typeof(float), fb.PixFormat.Type);
            var s = Samples<float>(fb);
            for (int x = 0; x < 5; x++) Assert.Equal(x * 0.125f, s[x], 4);
        }
        finally { File.Delete(fp); }
    }
}
