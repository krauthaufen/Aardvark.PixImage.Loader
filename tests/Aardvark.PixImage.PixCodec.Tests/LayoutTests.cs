using Aardvark.Base;
using PixCodec;
using Xunit;

namespace Aardvark.PixImage.PixCodec.Tests;

using PixImage = Aardvark.Base.PixImage;

/// <summary>Every PixImage handed out has the canonical dense image layout and is upright.</summary>
public class LayoutTests
{
    static void AssertDense(PixImage pi)
    {
        var (info, length) = pi switch
        {
            PixImage<byte> p => (p.Volume.Info, p.Volume.Data.LongLength),
            PixImage<sbyte> p => (p.Volume.Info, p.Volume.Data.LongLength),
            PixImage<ushort> p => (p.Volume.Info, p.Volume.Data.LongLength),
            PixImage<short> p => (p.Volume.Info, p.Volume.Data.LongLength),
            PixImage<uint> p => (p.Volume.Info, p.Volume.Data.LongLength),
            PixImage<int> p => (p.Volume.Info, p.Volume.Data.LongLength),
            PixImage<ulong> p => (p.Volume.Info, p.Volume.Data.LongLength),
            PixImage<long> p => (p.Volume.Info, p.Volume.Data.LongLength),
            PixImage<float> p => (p.Volume.Info, p.Volume.Data.LongLength),
            PixImage<double> p => (p.Volume.Info, p.Volume.Data.LongLength),
            PixImage<Aardvark.Base.Half> p => (p.Volume.Info, p.Volume.Data.LongLength),
            _ => throw new NotSupportedException(pi.PixFormat.ToString()),
        };
        long w = info.SX, h = info.SY, c = info.SZ;
        Assert.Equal((0L, 1L, c, w * c), (info.Origin, info.DZ, info.DX, info.DY));
        Assert.Equal(w * h * c, length);
    }

    // (extension, image) with orientation 6 where the format can say so; samples mark the stored top-left pixel
    public static IEnumerable<object[]> Files()
    {
        RawImage Make(ColorModel color, SampleType type, int channels = 0)
        {
            var r = RawImage.Create(6, 4, color, type, channels);
            var b = r.GetBytes();
            for (int i = 0; i < RawImage.BytesPerSample(type) * r.Channels; i++) b[i] = 0x40;
            r.Metadata = new ImageMetadata { Orientation = 6 };
            return r;
        }
        yield return new object[] { ".png", Make(ColorModel.Gray, SampleType.UInt8) };
        yield return new object[] { ".png", Make(ColorModel.Rgba, SampleType.UInt16) };
        yield return new object[] { ".jpg", Make(ColorModel.Rgb, SampleType.UInt8) };
        yield return new object[] { ".webp", Make(ColorModel.Rgba, SampleType.UInt8) };
        yield return new object[] { ".jp2", Make(ColorModel.GrayAlpha, SampleType.UInt16) };
        yield return new object[] { ".tif", Make(ColorModel.Rgb, SampleType.Float32) };
        yield return new object[] { ".tif", Make(ColorModel.Gray, SampleType.Int16) };
        yield return new object[] { ".tif", Make(ColorModel.Rgba, SampleType.Float16) };
        yield return new object[] { ".tif", Make(ColorModel.Other, SampleType.UInt32, 5) };
        yield return new object[] { ".gif", Make(ColorModel.Rgb, SampleType.UInt8) };
        yield return new object[] { ".exr", Make(ColorModel.Rgba, SampleType.Float32) };
        yield return new object[] { ".bmp", Make(ColorModel.Rgba, SampleType.UInt8) };
        yield return new object[] { ".bmp", Make(ColorModel.Gray, SampleType.UInt8) };
        yield return new object[] { ".tga", Make(ColorModel.Rgb, SampleType.UInt8) };
        yield return new object[] { ".tga", Make(ColorModel.GrayAlpha, SampleType.UInt8) };
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void EveryLoadPathIsDenseAndUpright(string ext, RawImage raw)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pixcodec-layout-{Guid.NewGuid():N}{ext}");
        try
        {
            ImageCodec.Encode(raw, path);
            bool oriented = ext is not (".gif" or ".exr" or ".bmp" or ".tga");   // formats without an orientation tag
            var size = oriented ? new V2i(4, 6) : new V2i(6, 4);
            var bytes = File.ReadAllBytes(path);
            var images = new List<PixImage>
            {
                PixImagePixCodec.Load(path), PixImagePixCodec.Load(bytes), PixImagePixCodec.Load(new MemoryStream(bytes)),
                PixImagePixCodec.LoadAll(path)[0], PixImagePixCodec.Loader.LoadFromFile(path),
                PixImagePixCodec.Loader.LoadFromStream(new MemoryStream(bytes)), PixImage.LoadRaw(path, PixImagePixCodec.Loader),
                ImageCodec.Decode(path).ToPixImage(),
            };
            // converted on loading (Aardvark converts only between some types, and only colour formats)
            if (raw.SampleType is not (SampleType.Int16 or SampleType.UInt32))
                images.AddRange(new PixImage[] { PixImage.Load(path, PixImagePixCodec.Loader), PixImagePixCodec.Load<byte>(path), PixImagePixCodec.Load<float>(path), PixImagePixCodec.Load<ushort>(bytes) });
            foreach (var pi in images)
            {
                AssertDense(pi);
                Assert.Equal(size, pi.Size);
            }
            Assert.Equal(size, PixImagePixCodec.Loader.GetInfoFromFile(path).Size);
            var info = PixImagePixCodec.GetInfo(path);
            Assert.Equal((size.X, size.Y), (info.Width, info.Height));
            // the marked stored top-left pixel ends up top-right after the clockwise turn of orientation 6
            var up = ImageCodec.Decode(path);
            int px = RawImage.BytesPerSample(up.SampleType) * up.Channels;
            if (oriented && ext != ".jpg")
                Assert.False(up.GetBytes().Slice(3 * px, px).SequenceEqual(up.GetBytes().Slice(0, px)));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ProcessorResultsAreDense()
    {
        // a strided view (every other column of a BGRA image) as input
        var big = new PixImage<byte>(Col.Format.BGRA, new V2i(40, 30));
        var data = big.Volume.Data;
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 7);
        var view = new PixImage<byte>(Col.Format.BGRA, big.Volume.SubVolume(new V3l(1, 2, 0), new V3l(20, 25, 4), new V3l(8, 160, 1)));
        PixImage.AddProcessor(PixImagePixCodec.Processor);
        foreach (var pi in new PixImage[]
        {
            view.Scaled(0.5), view.Scaled(new V2d(1.7, 1.3), ImageInterpolation.Lanczos), view.Resized(new V2i(33, 9)), view.Rotated(0.3),
            view.Rotated(Constant.PiHalf), view.Resized(13, 7, ResampleFilter.Mitchell), view.Oriented(6), view.WarpedAffine(20, 25, new[] { 1.0, 0.1, 0, 0, 1, 0 }),
            view.ToRawImage().ToPixImage(),
        })
            AssertDense(pi);
    }
}
