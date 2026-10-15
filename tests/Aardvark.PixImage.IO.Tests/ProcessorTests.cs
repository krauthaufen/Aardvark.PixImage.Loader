using Aardvark.Base;
using Aardvark.ImageCodecs;
using Xunit;

namespace PixImageIOTests;

using PixImage = Aardvark.Base.PixImage;

public class ProcessorTests
{
    static ProcessorTests() => PixImageIO.Init();

    static PixImage<T> Make<T>(Col.Format format, int w, int h, Func<int, T> value)
    {
        var img = new PixImage<T>(format, new V2i(w, h));
        var data = img.Volume.Data;
        for (int i = 0; i < data.Length; i++) data[i] = value(i);
        return img;
    }

    static byte Noise(int i) => (byte)((i * 2654435761u) >> 24);

    [Fact]
    public void ProcessorIsRegisteredFirst()
    {
        Assert.Same(PixImageIO.Processor, PixImage.GetProcessors(PixProcessorCaps.Scale)[0]);
        Assert.Same(PixImageIO.Processor, PixImage.GetProcessors(PixProcessorCaps.Rotate)[0]);
        Assert.Same(PixImageIO.Processor, PixImage.GetProcessors(PixProcessorCaps.Remap)[0]);
    }

    [Theory]
    [InlineData(ImageInterpolation.Near), InlineData(ImageInterpolation.Linear), InlineData(ImageInterpolation.Cubic)]
    [InlineData(ImageInterpolation.Lanczos), InlineData(ImageInterpolation.SuperSample)]
    public void ResizedMatchesCore(ImageInterpolation ip)
    {
        var img = Make(Col.Format.RGB, 41, 29, Noise);
        foreach (var size in new[] { new V2i(17, 13), new V2i(80, 61), new V2i(41, 29), new V2i(1, 1) })
        {
            var r = img.Resized(size, ip);
            Assert.Equal(size, r.Size);
            Assert.Equal(Col.Format.RGB, r.Format);
            // (Aardvark itself replaces SuperSample by Cubic when enlarging)
            var eip = ip == ImageInterpolation.SuperSample && (size.X > 41 || size.Y > 29) ? ImageInterpolation.Cubic : ip;
            var exp = ImageOps.Resize(img.ToRawImage(), size.X, size.Y, PixImageIOProcessor.Filter(eip));
            Assert.Equal((byte[])exp.Data, r.Volume.Data);
        }
        var s = img.Scaled(0.5, ip);
        Assert.Equal(new V2i(21, 15), s.Size); // round(20.5), round(14.5) as Aardvark computes it
    }

    [Fact]
    public void BgraKeepsFormatAndAlpha()
    {
        // the alpha channel of BGRA is found from the format, colours stay in BGR order
        var bgra = Make(Col.Format.BGRA, 23, 19, i => i % 4 == 3 ? (byte)(i % 7 == 0 ? 0 : 255) : Noise(i));
        var r = bgra.Resized(new V2i(11, 9), ImageInterpolation.Lanczos);
        Assert.Equal(Col.Format.BGRA, r.Format);
        var viaRgba = bgra.ToFormat(Col.Format.RGBA).Resized(new V2i(11, 9), ImageInterpolation.Lanczos).ToFormat(Col.Format.BGRA);
        Assert.Equal(viaRgba.Volume.Data, r.Volume.Data);
    }

    [Fact]
    public void NonImageLayoutAndSubImages()
    {
        var img = Make(Col.Format.RGBA, 40, 30, Noise);
        var sub = img.SubImage(new V2i(5, 3), new V2i(20, 16)); // a window, not dense
        var r = sub.Resized(new V2i(10, 8), ImageInterpolation.Cubic);
        var exp = ImageOps.Resize(ImageOps.Crop(img.ToRawImage(), 5, 3, 20, 16), 10, 8, ResampleFilter.Bicubic);
        Assert.Equal((byte[])exp.Data, r.Volume.Data);
    }

    [Fact]
    public void AllSampleTypesIncludingAardvarkHalf()
    {
        Assert.Equal(new V2i(7, 5), Make(Col.Format.Gray, 14, 10, i => (float)i).Resized(7, 5).Size);
        Assert.Equal(new V2i(7, 5), Make(Col.Format.RGB, 14, 10, i => (double)i).Resized(7, 5).Size);
        Assert.Equal(new V2i(7, 5), Make(Col.Format.RGBA, 14, 10, i => (ushort)i).Resized(7, 5).Size);
        Assert.Equal(new V2i(7, 5), Make(Col.Format.Gray, 14, 10, i => (int)i).Resized(7, 5).Size);
        var h = Make(Col.Format.RGB, 14, 10, i => new Aardvark.Base.Half(i % 9 * 0.25f));
        var hr = h.Resized(new V2i(28, 20), ImageInterpolation.Linear);
        var fr = h.ToPixImage<float>().Resized(new V2i(28, 20), ImageInterpolation.Linear);
        for (int i = 0; i < fr.Volume.Data.Length; i++)
            Assert.Equal(fr.Volume.Data[i], (float)hr.Volume.Data[i], 2);
    }

    [Fact]
    public void RotatedIsCounterClockwise()
    {
        var img = Make(Col.Format.RGB, 13, 8, Noise);
        var r = img.Rotated(Constant.PiHalf, true, ImageInterpolation.Cubic);
        Assert.Equal(new V2i(8, 13), r.Size);
        Assert.Equal((byte[])ImageOps.Rotate270(img.ToRawImage()).Data, r.Volume.Data);
        var k = img.Rotated(0.3, false, ImageInterpolation.Linear);
        Assert.Equal(img.Size, k.Size);
    }

    [Fact]
    public void RemappedIdentityAndShift()
    {
        var img = Make(Col.Format.Gray, 16, 12, i => (float)(i % 16 + 100 * (i / 16)));
        var mx = new Matrix<float>(16, 12);
        var my = new Matrix<float>(16, 12);
        mx.SetByCoord((x, y) => (float)x + 1);
        my.SetByCoord((x, y) => (float)y);
        var r = img.Remapped(mx, my, ImageInterpolation.Linear);
        for (int y = 0; y < 12; y++)
            for (int x = 0; x < 15; x++)
                Assert.Equal(img.Volume[x + 1, y, 0], r.Volume[x, y, 0]);
        Assert.Equal(0f, r.Volume[15, 0, 0]); // constant border
    }

    [Fact]
    public void MipMapUsesProcessor()
    {
        var img = Make(Col.Format.RGBA, 64, 32, Noise);
        var mip = PixImageMipMap.Create(img, ImageInterpolation.Lanczos);
        Assert.Equal(7, mip.LevelCount);
        Assert.Equal(new V2i(1, 1), mip[6].Size);
    }

    [Fact]
    public void ExtensionsWithMoreFilters()
    {
        var img = Make(Col.Format.RGBA, 30, 20, Noise);
        foreach (var f in new[] { ResampleFilter.Mitchell, ResampleFilter.BSpline, ResampleFilter.Lanczos2, ResampleFilter.Hermite, ResampleFilter.Gaussian })
        {
            var r = img.Resized(13, 9, f);
            Assert.Equal(new V2i(13, 9), r.Size);
            Assert.Equal((byte[])ImageOps.Resize(img.ToRawImage(), 13, 9, f).Data, r.Volume.Data);
        }
        var lin = img.Resized(15, 10, new ResizeOptions { Filter = ResampleFilter.Box, LinearLight = true });
        Assert.Equal(new V2i(15, 10), lin.Size);
        var rot = img.Rotated(Constant.Pi / 6, true, new WarpOptions { Filter = ResampleFilter.Lanczos3, Border = BorderMode.Mirror });
        Assert.True(rot.Size.X > 30);
        var o6 = img.Oriented(6);
        Assert.Equal(new V2i(20, 30), o6.Size);
        Assert.Equal((byte[])ImageOps.Rotate90(img.ToRawImage()).Data, o6.Volume.Data);
    }

    #region Direct loading

    static string Temp(string ext) => Path.Combine(Path.GetTempPath(), $"pixcodec-{Guid.NewGuid():N}{ext}");

    [Fact]
    public void LoadGivesTypedPixImages()
    {
        var p8 = Temp(".png");
        var p16 = Temp(".png");
        var pexr = Temp(".exr");
        try
        {
            var rgb = Make(Col.Format.RGB, 9, 7, Noise);
            PixImageIO.Save(rgb, p8);
            var l = Assert.IsType<PixImage<byte>>(PixImageIO.Load(p8));
            Assert.Equal(rgb.Volume.Data, l.Volume.Data);

            var g16 = Make(Col.Format.Gray, 9, 7, i => (ushort)(i * 1000));
            PixImageIO.Save(g16, p16);
            Assert.IsType<PixImage<ushort>>(PixImageIO.Load(p16));
            var asByte = PixImageIO.Load<byte>(p16);
            Assert.Equal((byte)(g16.Volume.Data[5] >> 8), asByte.Volume.Data[5]);

            var f = Make(Col.Format.RGBA, 9, 7, i => i * 0.25f);
            PixImageIO.Save(f, pexr);
            var lf = Assert.IsType<PixImage<float>>(PixImageIO.Load(File.ReadAllBytes(pexr)));
            Assert.Equal(f.Volume.Data, lf.Volume.Data);

            var info = PixImageIO.GetInfo(p16);
            Assert.Equal((ImageFormat.Png, 9, 7, SampleType.UInt16), (info.Format, info.Width, info.Height, info.SampleType));
        }
        finally { File.Delete(p8); File.Delete(p16); File.Delete(pexr); }
    }

    [Fact]
    public void HalfFloatTiffLoadsAsFloat()
    {
        // 16 bit float samples come back as PixImage<float> (System.Half widened, not Aardvark's Half)
        var raw = RawImage.Create(5, 4, ColorModel.Rgb, SampleType.Float16);
        var d = (System.Half[])raw.Data;
        for (int i = 0; i < d.Length; i++) d[i] = (System.Half)(i * 0.5f);
        var ms = new MemoryStream();
        Tiff.Encode(raw, ms);
        var pi = Assert.IsType<PixImage<float>>(PixImageIO.Load(ms.ToArray()));
        Assert.Equal(7.5f, pi.Volume.Data[15]);
        var loader = Assert.IsType<PixImage<float>>(PixImageIO.Loader.LoadFromStream(new MemoryStream(ms.ToArray())));
        Assert.Equal(pi.Volume.Data, loader.Volume.Data);
    }

    [Fact]
    public void LoadAllPagesAndFrames()
    {
        var a = RawImage.Create(6, 4, ColorModel.Rgb, SampleType.UInt8);
        var b = RawImage.Create(6, 4, ColorModel.Rgb, SampleType.UInt8);
        ((byte[])b.Data).AsSpan().Fill(200);
        var tiff = new MemoryStream();
        Tiff.Encode(new[] { a, b }, tiff);
        var pages = PixImageIO.LoadAll(tiff.ToArray());
        Assert.Equal(2, pages.Length);
        Assert.Equal((byte)200, ((PixImage<byte>)pages[1]).Volume.Data[0]);

        var gif = new MemoryStream();
        Gif.EncodeAnimation(new[] { new GifInputFrame(a), new GifInputFrame(b), new GifInputFrame(a) }, gif);
        var frames = PixImageIO.LoadAll(gif.ToArray());
        Assert.Equal(3, frames.Length);
        Assert.All(frames, f => Assert.Equal(Col.Format.RGBA, f.Format));
    }

    [Theory]
    [InlineData(".jpg"), InlineData(".png"), InlineData(".tif")]
    public void LoadAppliesOrientation(string ext)
    {
        var raw = RawImage.Create(16, 8, ColorModel.Rgb, SampleType.UInt8);
        ((byte[])raw.Data)[0] = 255; // top-left red
        raw.Metadata = new ImageMetadata { Orientation = 6 };
        var path = Temp(ext);
        try
        {
            ImageCodec.Encode(raw, path);
            var info = PixImageIO.GetInfo(path);
            Assert.Equal((6, 8, 16), (info.Orientation, info.Width, info.Height));
            Assert.Equal(new V2i(8, 16), PixImage.GetInfoFromFile(path, PixImageIO.Loader).Size);
            foreach (var up in new[] { PixImageIO.Load<byte>(path), PixImageIO.LoadAll(path)[0].ToPixImage<byte>(), PixImage.Load(path, PixImageIO.Loader).ToPixImage<byte>() })
            {
                Assert.Equal(new V2i(8, 16), up.Size);
                Assert.True(up.Volume[7, 0, 0] > 128); // top-left went to top-right (rotated clockwise)
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RawImageExtensions()
    {
        var raw = ImageOps.Resize(RawImage.Create(4, 4, ColorModel.Gray, SampleType.UInt16), 2, 2);
        var pi = raw.ToPixImage();
        Assert.IsType<PixImage<ushort>>(pi);
        Assert.Same(raw.Data, ((PixImage<ushort>)pi).Volume.Data);
        Assert.Equal(raw.Data, pi.ToRawImage().Data);
    }

    #endregion
}
