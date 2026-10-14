using Xunit;
using static PixCodec.Tests.TestUtil;

namespace PixCodec.Tests;

/// <summary>BMP and TGA: fixtures against their references, header information, round trips, robustness.</summary>
public class BmpTgaTests
{
    public static IEnumerable<object[]> BmpFixtures => Files("bmp", ".bmp");
    public static IEnumerable<object[]> TgaFixtures => Files("tga", ".tga");

    static RawImage DecodeAny(string ext, ReadOnlySpan<byte> d) => ext == ".bmp" ? Bmp.Decode(d) : Tga.Decode(d);
    static ImageInfo InfoAny(string ext, ReadOnlySpan<byte> d) => ext == ".bmp" ? Bmp.GetInfo(d) : Tga.GetInfo(d);

    [Theory, MemberData(nameof(BmpFixtures)), MemberData(nameof(TgaFixtures))]
    public void DecodeMatchesReference(string file)
    {
        string ext = Path.GetExtension(file);
        var data = File.ReadAllBytes(Fixture(file));
        var expected = LoadRef(Fixture(file) + ".ref");
        var img = DecodeAny(ext, data);
        // JPEG inside BMP: our JPEG decoder against Pillow's
        AssertEqual(expected, img, file.Contains("embedded_jpeg") ? 2 : 0);
        Assert.Equal(RawImage.DefaultColorModel(img.Channels), img.Color);

        // header information describes what Decode returns, also through the generic entry points and streams
        var info = InfoAny(ext, data);
        Assert.Equal((img.Width, img.Height, img.Channels, img.SampleType, img.Color), (info.Width, info.Height, info.Channels, info.SampleType, info.Color));
        Assert.Equal(ext == ".bmp" ? ImageFormat.Bmp : ImageFormat.Tga, info.Format);
        Assert.Equal(info.Format, ImageCodec.DetectFormat(data));
        Assert.Equal(info, ImageCodec.GetInfo(data));
        Assert.Equal(info, ImageCodec.GetInfo(new MemoryStream(data)));
        Assert.Equal(info, ImageCodec.GetInfo(new NonSeekable(data)));
        AssertEqual(img, ImageCodec.Decode(data));
        var m = img.Metadata ?? new ImageMetadata();
        Assert.Equal((m.DpiX, m.DpiY, m.IccProfile != null), (info.DpiX, info.DpiY, info.HasIccProfile));
    }

    [Fact]
    public void BmpMetadata()
    {
        var m = Bmp.Decode(File.ReadAllBytes(Fixture("bmp/rgb24_dpi.bmp"))).Metadata!;
        Assert.Equal((300.0, 100.0), (Math.Round(m.DpiX), Math.Round(m.DpiY)));
        var icc = Bmp.Decode(File.ReadAllBytes(Fixture("bmp/v5_icc.bmp"))).Metadata!.IccProfile!;
        Assert.Equal(768, icc.Length);
        Assert.Equal(17, icc[17]);
        var wide = Bmp.Decode(File.ReadAllBytes(Fixture("bmp/bf101010_2_v5.bmp")));
        Assert.Equal((SampleType.UInt16, ColorModel.Rgba), (wide.SampleType, wide.Color));
    }

    [Fact]
    public void ReservedByteAsAlphaOnRequest()
    {
        var data = File.ReadAllBytes(Fixture("bmp/rgb32_reserved.bmp"));
        Assert.Equal(ColorModel.Rgb, Bmp.Decode(data).Color);
        var o = new BmpDecodeOptions { AlphaFromReservedByte = true };
        var a = Bmp.Decode(data, o);
        Assert.Equal(ColorModel.Rgba, a.Color);
        Assert.Equal(4, Bmp.GetInfo(data, o).Channels);
        var rgb = LoadRef(Fixture("bmp/rgb32_reserved.bmp.ref"));
        for (int i = 0; i < a.Width * a.Height; i++)
            Assert.True(a.GetData<byte>().AsSpan(i * 4, 3).SequenceEqual(rgb.GetData<byte>().AsSpan(i * 3, 3)));
    }

    static RawImage Pattern(int w, int h, ColorModel color, int seed)
    {
        var img = RawImage.Create(w, h, color, SampleType.UInt8);
        var b = img.GetData<byte>();
        var rnd = new Random(seed);
        for (int i = 0; i < b.Length; i++) b[i] = (byte)((i * 7 + i / 13) % 251 ^ rnd.Next(4));
        if (img.HasAlpha)
            for (int i = img.Channels - 1; i < b.Length; i += img.Channels * 3) b[i] = 0;   // some transparency, never all
        // long runs too, for RLE
        b.AsSpan(0, Math.Min(b.Length, w * img.Channels * 2)).Fill(77);
        return img;
    }

    public static IEnumerable<object[]> RoundTripCases()
    {
        foreach (var fmt in new[] { ".bmp", ".tga" })
            foreach (var color in new[] { ColorModel.Gray, ColorModel.GrayAlpha, ColorModel.Rgb, ColorModel.Rgba })
                foreach (var (w, h) in new[] { (1, 1), (3, 2), (31, 17), (130, 5) })
                    foreach (bool flag in new[] { false, true })
                        yield return new object[] { fmt, color, w, h, flag };
    }

    [Theory, MemberData(nameof(RoundTripCases))]
    public void RoundTrip(string fmt, ColorModel color, int w, int h, bool flag)
    {
        var img = Pattern(w, h, color, w * 31 + h);
        img.Metadata = new ImageMetadata { DpiX = 300, DpiY = 72 };
        var bytes = fmt == ".bmp" ? Bmp.Encode(img, new BmpEncodeOptions { TopDown = flag }) : Tga.Encode(img, new TgaEncodeOptions { Rle = flag, TopDown = w % 2 == 1 });
        var back = DecodeAny(fmt, bytes);
        // BMP stores gray + alpha as RGBA
        var expected = fmt == ".bmp" && color == ColorModel.GrayAlpha ? ToRgba(img) : img;
        AssertEqual(expected, back);
        Assert.Equal(expected.Color, back.Color);
        if (fmt == ".bmp") Assert.Equal((300.0, 72.0), (Math.Round(back.Metadata!.DpiX), Math.Round(back.Metadata.DpiY)));
        Assert.Equal(ImageCodec.DetectFormat(bytes), fmt == ".bmp" ? ImageFormat.Bmp : ImageFormat.Tga);
        // and an independent decoder reads the same
        var oracle = OracleDecode(bytes, fmt);
        if (oracle != null) AssertEqual(fmt == ".bmp" && color == ColorModel.GrayAlpha ? ToRgba(img) : img, oracle);
    }

    static RawImage ToRgba(RawImage ga)
    {
        var s = ga.GetData<byte>();
        var d = new byte[ga.Width * ga.Height * 4];
        for (int i = 0; i < ga.Width * ga.Height; i++) { d[i * 4] = d[i * 4 + 1] = d[i * 4 + 2] = s[i * 2]; d[i * 4 + 3] = s[i * 2 + 1]; }
        return new RawImage(ga.Width, ga.Height, ColorModel.Rgba, d);
    }

    [Fact]
    public void BmpIccRoundTrips()
    {
        var img = Pattern(9, 4, ColorModel.Rgb, 1);
        var icc = new byte[1000];
        new Random(2).NextBytes(icc);
        img.Metadata = new ImageMetadata { IccProfile = icc };
        var bytes = Bmp.Encode(img);
        var back = Bmp.Decode(bytes);
        AssertEqual(img, back);
        Assert.Equal(icc, back.Metadata!.IccProfile);
        Assert.True(Bmp.GetInfo(bytes).HasIccProfile);
        var oracle = OracleDecode(bytes, ".bmp");
        if (oracle != null) AssertEqual(img, oracle);
    }

    [Fact]
    public void RejectsWhatCannotBeWritten()
    {
        Assert.Throws<ImageNotSupportedException>(() => Bmp.Encode(RawImage.Create(2, 2, ColorModel.Rgb, SampleType.UInt16)));
        Assert.Throws<ImageNotSupportedException>(() => Tga.Encode(RawImage.Create(2, 2, ColorModel.Other, SampleType.UInt8, 5)));
        Assert.Throws<ImageNotSupportedException>(() => Tga.Encode(RawImage.Create(70000, 1, ColorModel.Gray, SampleType.UInt8)));
    }

    /// <summary>Damaged files decode or fail with the library's exceptions, never anything else.</summary>
    [Theory, MemberData(nameof(BmpFixtures)), MemberData(nameof(TgaFixtures))]
    public void DamagedFilesFailCleanly(string file)
    {
        string ext = Path.GetExtension(file);
        var data = File.ReadAllBytes(Fixture(file));
        var rnd = new Random(file.Length);
        for (int k = 0; k < 60; k++)
        {
            var d = k < 20 ? data.AsSpan(0, rnd.Next(data.Length)).ToArray() : (byte[])data.Clone();
            if (k >= 20)
                for (int j = 0; j < 1 + k % 5; j++) d[rnd.Next(Math.Min(d.Length, k < 40 ? 80 : d.Length))] = (byte)rnd.Next(256);
            try { DecodeAny(ext, d); InfoAny(ext, d); }
            catch (ImageFormatException) { }
            catch (ImageNotSupportedException) { }
        }
    }

    [Fact]
    public void HugeHeadersWithoutDataAreRejected()
    {
        // 8448 x 8448 (more than MaxUnbackedPixels) announced by a 70 byte file
        var bmp = File.ReadAllBytes(Fixture("bmp/rgb24_dpi.bmp")).AsSpan(0, 70).ToArray();
        BitConverter.GetBytes(8448).CopyTo(bmp, 18);
        BitConverter.GetBytes(8448).CopyTo(bmp, 22);
        Assert.Throws<ImageFormatException>(() => Bmp.Decode(bmp));
        var tga = new byte[40];
        tga[2] = 2; tga[13] = 0x21; tga[15] = 0x21; tga[16] = 24;
        Assert.Throws<ImageFormatException>(() => Tga.Decode(tga));
    }

    [Fact]
    public void TgaIsTriedLast()
    {
        // every other format's files are never taken for TGA
        foreach (var dir in new[] { "png", "jpeg", "gif", "webp", "tiff", "exr", "bmp" })
            foreach (var f in Directory.GetFiles(Path.Combine(FixtureDir, dir)).Where(f => !f.EndsWith(".ref") && !f.EndsWith(".json")).Take(30))
                Assert.NotEqual(ImageFormat.Tga, ImageCodec.DetectFormat(File.ReadAllBytes(f)));
    }

    sealed class NonSeekable(byte[] data) : Stream
    {
        readonly MemoryStream inner = new(data);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, 100));
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
