using Xunit;

namespace PixCodec.Tests;

/// <summary>Decoders return upright images by default, and saving those again does not turn them a second time.</summary>
public class OrientationTests
{
    static RawImage Pattern(int w, int h, ColorModel color, SampleType type = SampleType.UInt8)
    {
        var img = RawImage.Create(w, h, color, type);
        var b = img.GetBytes();
        for (int i = 0; i < b.Length; i++) b[i] = (byte)(i * 37 + i / 7);
        return img;
    }

    // EXIF blob holding an orientation tag (and a second tag after it), big or little endian
    static byte[] Exif(int orientation, bool bigEndian)
    {
        byte[] le = [(byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0, 2, 0, 0x12, 0x01, 3, 0, 1, 0, 0, 0, (byte)orientation, 0, 0, 0, 0x31, 0x01, 2, 0, 2, 0, 0, 0, (byte)'x', 0, 0, 0, 0, 0, 0, 0];
        if (!bigEndian) return le;
        return [(byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8, 0, 2, 0x01, 0x12, 0, 3, 0, 0, 0, 1, 0, (byte)orientation, 0, 0, 0x01, 0x31, 0, 2, 0, 0, 0, 2, (byte)'x', 0, 0, 0, 0, 0, 0, 0];
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (var format in new[] { ImageFormat.Png, ImageFormat.Jpeg, ImageFormat.Webp, ImageFormat.Jp2, ImageFormat.Tiff })
            for (int o = 1; o <= 8; o++)
                foreach (bool withBlob in new[] { false, true })
                    yield return new object[] { format, o, withBlob };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void DecodeIsUprightAndSavesBackUnturned(ImageFormat format, int orientation, bool withBlob)
    {
        var src = Pattern(12, 7, ColorModel.Rgb);
        src.Metadata = new ImageMetadata { Orientation = orientation, DpiX = 300, DpiY = 100, Exif = withBlob ? Exif(orientation, orientation % 2 == 0) : null };
        bool lossy = format == ImageFormat.Jpeg;
        var bytes = format switch
        {
            ImageFormat.Webp => ToBytes(s => Webp.Encode(src, s, new WebpEncodeOptions { Lossless = true })),
            _ => ImageCodec.Encode(src, format),
        };

        var info = ImageCodec.GetInfo(bytes);
        var up = ImageCodec.Decode(bytes);
        var expected = ImageOps.ApplyOrientation(src, orientation);
        if (!lossy) TestUtil.AssertEqual(expected, up);
        Assert.Equal((expected.Width, expected.Height), (up.Width, up.Height));
        Assert.Equal((up.Width, up.Height, orientation, orientation > 1), (info.Width, info.Height, info.Orientation, info.OrientationApplied));
        Assert.Equal((src.Width, src.Height), (info.StoredWidth, info.StoredHeight));
        // the resolution belongs to the upright axes
        var dpi = orientation >= 5 ? (100.0, 300.0) : (300.0, 100.0);
        if (format != ImageFormat.Webp)   // (WebP keeps resolution only inside an EXIF blob, written when there is one)
        {
            Assert.Equal(dpi, (Math.Round(up.Metadata!.DpiX, 3), Math.Round(up.Metadata.DpiY, 3)));
            Assert.Equal(dpi, (Math.Round(info.DpiX, 3), Math.Round(info.DpiY, 3)));
        }
        Assert.Equal(1, up.Metadata!.Orientation);
        if (up.Metadata.Exif != null && format != ImageFormat.Tiff) Assert.Equal(1, ExifTags.Read(up.Metadata.Exif));

        // saved and loaded again: unchanged
        var again = ImageCodec.Decode(format == ImageFormat.Webp
            ? ToBytes(s => Webp.Encode(up, s, new WebpEncodeOptions { Lossless = true }))
            : ImageCodec.Encode(up, format));
        Assert.Equal((up.Width, up.Height, 1), (again.Width, again.Height, again.Metadata?.Orientation ?? 1));
        if (!lossy) TestUtil.AssertEqual(up, again);

        // the stored pixels are still available
        var stored = format switch
        {
            ImageFormat.Png => Png.Decode(bytes, new PngDecodeOptions { ApplyOrientation = false }),
            ImageFormat.Jpeg => Jpeg.Decode(bytes, new JpegDecodeOptions { ApplyOrientation = false }),
            ImageFormat.Webp => Webp.Decode(bytes, new WebpDecodeOptions { ApplyOrientation = false }),
            ImageFormat.Jp2 => Jp2.Decode(bytes, new Jp2DecodeOptions { ApplyOrientation = false }),
            _ => Tiff.Decode(bytes, new TiffDecodeOptions { ApplyOrientation = false }),
        };
        Assert.Equal((src.Width, src.Height, orientation), (stored.Width, stored.Height, stored.Metadata!.Orientation));
        TestUtil.AssertEqual(ImageOps.ApplyOrientation(stored), up, 0);
    }

    static byte[] ToBytes(Action<Stream> write)
    {
        var ms = new MemoryStream();
        write(ms);
        return ms.ToArray();
    }

    [Fact]
    public void StoredRegionMapsUprightRectangles()
    {
        var src = Pattern(9, 5, ColorModel.Gray);
        var rnd = new Random(3);
        for (int o = 1; o <= 8; o++)
        {
            var up = ImageOps.ApplyOrientation(src, o);
            for (int k = 0; k < 40; k++)
            {
                int x = rnd.Next(up.Width), y = rnd.Next(up.Height);
                var r = (x, y, 1 + rnd.Next(up.Width - x), 1 + rnd.Next(up.Height - y));
                var s = ExifTags.StoredRegion(o, src.Width, src.Height, r);
                TestUtil.AssertEqual(ImageOps.Crop(up, r.x, r.y, r.Item3, r.Item4), ImageOps.ApplyOrientation(ImageOps.Crop(src, s.X, s.Y, s.Width, s.Height), o));
            }
        }
    }

    [Fact]
    public void RegionsAreUprightCoordinates()
    {
        var src = Pattern(40, 24, ColorModel.Rgb);
        for (int o = 1; o <= 8; o++)
        {
            src.Metadata = new ImageMetadata { Orientation = o };
            var tif = ToBytes(s => Tiff.Encode(src, s, new TiffEncodeOptions { TileWidth = 16, TileHeight = 16 }));
            var jp2 = Jp2.Encode(src, new Jp2EncodeOptions { TileWidth = 16, TileHeight = 16 });
            var up = ImageOps.ApplyOrientation(src, o);
            foreach (var (x, y, w, h) in new[] { (0, 0, up.Width, up.Height), (3, 5, 7, 2), (up.Width - 1, up.Height - 1, 1, 1), (1, 0, up.Width - 2, up.Height) })
            {
                var crop = ImageOps.Crop(up, x, y, w, h);
                TestUtil.AssertEqual(crop, Tiff.DecodeRegion(tif, x, y, w, h));
                TestUtil.AssertEqual(crop, Jp2.Decode(jp2, new Jp2DecodeOptions { Region = (x, y, w, h) }));
            }
            // JPEG 2000 clips a window reaching beyond the upright image, TIFF rejects it
            TestUtil.AssertEqual(ImageOps.Crop(up, 2, 3, up.Width - 2, up.Height - 3), Jp2.Decode(jp2, new Jp2DecodeOptions { Region = (2, 3, 500, 500) }));
            Assert.ThrowsAny<ArgumentException>(() => Tiff.DecodeRegion(tif, 0, 0, up.Width + 1, 1));
            Assert.ThrowsAny<ArgumentException>(() => Jp2.Decode(jp2, new Jp2DecodeOptions { Region = (up.Width, 0, 1, 1) }));
        }
    }

    [Fact]
    public void AnimationsAreUpright()
    {
        var frames = Enumerable.Range(0, 3).Select(i => Pattern(10, 6, ColorModel.Rgba)).ToList();
        for (int i = 0; i < frames.Count; i++) frames[i].GetBytes()[i * 4] ^= 0xFF;   // distinct frames
        frames[0].Metadata = new ImageMetadata { Orientation = 8 };
        var apng = Png.EncodeAnimation(frames.Select(f => new PngInputFrame(f)));
        var webp = Webp.EncodeAnimation(frames.Select(f => new WebpInputFrame(f)), new WebpEncodeOptions { Lossless = true });
        foreach (var data in new[] { apng, webp })
        {
            var all = ImageCodec.DecodeAll(data);
            Assert.Equal(3, all.Length);
            for (int i = 0; i < 3; i++) TestUtil.AssertEqual(ImageOps.ApplyOrientation(frames[i], 8), all[i]);
            var info = ImageCodec.GetInfo(data);
            Assert.Equal((6, 10, 8), (info.Width, info.Height, info.Orientation));
        }
        Assert.Equal((6, 10), (Png.GetAnimationInfo(apng).Width, Png.GetAnimationInfo(apng).Height));
        Assert.Equal((6, 10), (Webp.GetAnimationInfo(webp).Width, Webp.GetAnimationInfo(webp).Height));
        // raw frames are as stored
        var raw = Png.DecodeAnimation(apng, new PngDecodeOptions { FrameMode = PngFrameMode.Raw });
        Assert.Equal((10, 6), (raw.Frames[0].Image.Width, raw.Frames[0].Image.Height));
    }
}
