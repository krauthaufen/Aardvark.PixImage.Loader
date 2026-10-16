using Aardvark.Data;
using Aardvark.Base;
using Aardvark.ImageCodecs;
using Xunit;

namespace PixCodecTests;

using PixImage = Aardvark.Base.PixImage;

/// <summary>JPEG XL (decode only) through the PixImage loader.</summary>
public class JxlLoaderTests
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
    [InlineData("jxl/ll_rgb_e3.jxl", typeof(byte), Col.Format.RGB)]
    [InlineData("jxl/ll_rgba.jxl", typeof(byte), Col.Format.RGBA)]
    [InlineData("jxl/ll_gray.jxl", typeof(byte), Col.Format.Gray)]
    [InlineData("jxl/ll_rgb16.jxl", typeof(ushort), Col.Format.RGB)]
    [InlineData("jxl/ll_float32.jxl", typeof(float), Col.Format.RGB)]
    [InlineData("jxl/vd_e7.jxl", typeof(byte), Col.Format.RGB)]
    [InlineData("jxl/orient6.jxl", typeof(byte), Col.Format.RGB)]
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
    public void AnimationFrames()
    {
        var path = Fixture("jxl/anim_lossless.jxl");
        var frames = PixCodec.LoadAll(path);
        Assert.Equal(5, frames.Length);
        var raw = Jxl.DecodeAll(File.ReadAllBytes(path));
        for (int i = 0; i < frames.Length; i++) Assert.Equal((byte[])raw[i].Data, Samples<byte>(frames[i]));
    }

    [Fact]
    public void HalfFloatBecomesFloat()
    {
        var p = PixCodec.Load(Fixture("jxl/ll_float16.jxl"));
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
        var path = Path.Combine(Path.GetTempPath(), $"pixcodec-{Guid.NewGuid():N}.jxl");
        try
        {
            // default: lossy (distance 1), alpha stays exact
            PixCodec.Save(img, path);
            var back = PixCodec.Load(path);
            Assert.Equal(img.Size, back.Size);
            Assert.Equal(Col.Format.RGBA, back.Format);
            var a = Samples<byte>(img); var b = Samples<byte>(back);
            double err = 0;
            for (int i = 0; i < a.Length; i++)
            {
                if (i % 4 == 3) Assert.Equal(a[i], b[i]);
                else err += Math.Abs(a[i] - b[i]);
            }
            Assert.True(err / (a.Length * 3 / 4) < 6, $"mean error {err / (a.Length * 3 / 4)}");
            // lossless through SaveJxl
            using (var fs = File.Create(path)) PixCodec.SaveJxl(img, fs, new JxlEncodeOptions { Lossless = true });
            Assert.Equal(a, Samples<byte>(PixCodec.Load(path)));
            // other sample types are written as float
            var f = new PixImage<double>(Col.Format.Gray, new V2i(5, 3));
            for (int y = 0; y < 3; y++) for (int x = 0; x < 5; x++) f.Volume[x, y, 0] = x * 0.125 + y * 0.25;
            PixCodec.SaveJxl(f, path, new JxlEncodeOptions { Lossless = true });
            var fb = PixCodec.Load(path);
            Assert.Equal(typeof(float), fb.PixFormat.Type);
            Assert.Equal(new[] { 0f, 0.125f, 0.25f, 0.375f, 0.5f }, Samples<float>(fb).Take(5));
        }
        finally { File.Delete(path); }
    }
}
