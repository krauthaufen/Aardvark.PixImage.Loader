using Xunit;
using Xunit.Abstractions;
using static Aardvark.ImageCodecs.Tests.TestUtil;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>Block compressed and volume DDS writing.</summary>
public class DdsEncodeTests
{
    readonly ITestOutputHelper _out;
    public DdsEncodeTests(ITestOutputHelper output) => _out = output;

    static RawImage Photo() => ImageCodec.Decode(File.ReadAllBytes(Fixture("jpeg/photo_444.jpg")));

    /// <summary>The photo with an alpha channel holding a smooth ramp, a hard edge and a fully transparent corner.</summary>
    static RawImage PhotoAlpha()
    {
        var p = Photo();
        var o = RawImage.Create(p.Width, p.Height, ColorModel.Rgba, SampleType.UInt8);
        var s = p.GetData<byte>(); var d = o.GetData<byte>();
        for (int y = 0; y < p.Height; y++)
            for (int x = 0; x < p.Width; x++)
            {
                int i = y * p.Width + x;
                for (int c = 0; c < 3; c++) d[i * 4 + c] = s[i * 3 + c];
                d[i * 4 + 3] = (byte)(x < 20 && y < 20 ? 0 : x > p.Width / 2 ? 255 : x * 255 / (p.Width / 2));
            }
        return o;
    }

    /// <summary>HDR: the photo in linear light times an exposure ramp over 12 stops; optionally negative in the upper half.</summary>
    static RawImage Hdr(bool negative, bool smooth = false)
    {
        var p = Photo();
        var o = RawImage.Create(p.Width, p.Height, ColorModel.Rgb, SampleType.Float32);
        var s = p.GetData<byte>(); var d = o.GetData<float>();
        for (int y = 0; y < p.Height; y++)
            for (int x = 0; x < p.Width; x++)
                for (int c = 0; c < 3; c++)
                {
                    int i = (y * p.Width + x) * 3 + c;
                    double v = smooth ? 0.5 + 0.4 * Math.Sin(x / (9.0 + c) + c) * Math.Cos(y / (7.0 + c)) : Math.Pow(s[i] / 255.0, 2.2);
                    double lin = v * Math.Pow(2, 12.0 * x / p.Width - 6);
                    d[i] = (float)(negative && y < p.Height / 2 ? -lin : lin);
                }
        return o;
    }

    /// <summary>PSNR over the first channels (from channel <paramref name="first"/>), of the pixels where <paramref name="where"/> holds.</summary>
    static double Psnr8(RawImage a, RawImage b, int channels, int first = 0, Func<int, bool>? where = null)
    {
        var x = ToDoubles(a); var y = ToDoubles(b);
        int ca = a.Channels, cb = b.Channels;
        double se = 0; long n = 0;
        for (int i = 0; i < a.Width * a.Height; i++)
            if (where == null || where(i))
                for (int c = first; c < first + channels; c++) { double e = x[i * ca + c] - y[i * cb + c]; se += e * e; n++; }
        return se == 0 ? 99 : 10 * Math.Log10(255.0 * 255 * n / se);
    }

    /// <summary>RMS of log2(decoded / original) over values above 2^-10 (BC6H works on a roughly logarithmic scale).</summary>
    static double LogRms(RawImage original, RawImage decoded)
    {
        var x = ToDoubles(original); var y = ToDoubles(decoded);
        double se = 0; long n = 0;
        for (int i = 0; i < x.Length; i++)
        {
            if (Math.Abs(x[i]) < 1.0 / 1024) continue;
            double r = Math.Log2(Math.Max(Math.Abs(y[i]), 1e-6) / Math.Abs(x[i]));
            if (Math.Sign(y[i]) != Math.Sign(x[i]) && y[i] != 0) r = 10;
            se += r * r; n++;
        }
        return Math.Sqrt(se / n);
    }

    // minimum colour PSNR per format on the photo. References on the same images, decoded with bcdec: DirectXTex
    // (Compress, uniform weights): BC1 28.50 dB, BC7 33.60 dB (with 3 subsets); with alpha BC7 32.79 / alpha 40.75 dB,
    // BC3 28.50 / alpha 56.52 dB. ImageMagick cluster fit DXT1 28.83 dB; bc7enc (etcpak 0.9, linear weights, uber 4)
    // BC7 32.12 dB; etcpak BC4 41.63 dB
    public static IEnumerable<object[]> Cases()
    {
        foreach (var q in new[] { BcQuality.Fast, BcQuality.Normal, BcQuality.High })
        {
            yield return new object[] { DdsCompression.BC1, q, false };
            yield return new object[] { DdsCompression.BC1, q, true };
            yield return new object[] { DdsCompression.BC2, q, true };
            yield return new object[] { DdsCompression.BC3, q, true };
            yield return new object[] { DdsCompression.BC4, q, false };
            yield return new object[] { DdsCompression.BC5, q, false };
            yield return new object[] { DdsCompression.BC7, q, false };
            yield return new object[] { DdsCompression.BC7, q, true };
        }
    }

    static double MinPsnr(DdsCompression f, BcQuality q, bool alpha) => f switch
    {
        DdsCompression.BC7 when alpha => q == BcQuality.Fast ? 32.2 : 33.2,
        DdsCompression.BC1 or DdsCompression.BC2 or DdsCompression.BC3 => q == BcQuality.Fast ? 28.4 : q == BcQuality.Normal ? 28.7 : 28.8,
        DdsCompression.BC4 => q == BcQuality.Fast ? 43.5 : 45.5,
        DdsCompression.BC5 => q == BcQuality.Fast ? 39.5 : 41,
        _ => q == BcQuality.Fast ? 32.8 : q == BcQuality.Normal ? 33.7 : 34,
    };

    [Theory, MemberData(nameof(Cases))]
    public void CompressedQualityAndValidity(DdsCompression format, BcQuality quality, bool alpha)
    {
        var src = alpha ? PhotoAlpha() : Photo();
        if (format == DdsCompression.BC4) src = ImageOps.Crop(Gray(src), 0, 0, src.Width, src.Height);
        if (format == DdsCompression.BC5) src = TwoChannels(src);
        var bytes = Dds.Encode(src, new DdsEncodeOptions { Compression = format, Quality = quality });
        var info = Dds.GetTextureInfo(bytes);
        Assert.True(info.IsBlockCompressed);
        var back = Dds.Decode(bytes);
        Assert.Equal((src.Width, src.Height), (back.Width, back.Height));
        int channels = format switch { DdsCompression.BC4 => 1, DdsCompression.BC5 => 2, _ => 4 };
        var sa = src.GetData<byte>();
        // colour where visible (BC1 drops the colour of texels below alpha 128)
        Func<int, bool>? visible = format == DdsCompression.BC1 && alpha ? i => sa[i * 4 + 3] >= 128 : null;
        double psnr = Psnr8(src, back, Math.Min(channels, 3), 0, visible);
        double alphaPsnr = alpha && format != DdsCompression.BC1 ? Psnr8(src, back, 1, 3) : 0;
        _out.WriteLine($"{format} {quality} alpha={alpha}: {psnr:F2} dB" + (alphaPsnr > 0 ? $", alpha {alphaPsnr:F2} dB" : ""));
        Assert.True(psnr >= MinPsnr(format, quality, alpha), $"{format} {quality}: {psnr:F2} dB");
        if (alphaPsnr > 0) Assert.True(alphaPsnr >= (format == DdsCompression.BC2 ? 36 : format == DdsCompression.BC3 ? 56 : 40.5), $"alpha {alphaPsnr:F2} dB");
        if (format == DdsCompression.BC1 && alpha)
        {
            // punch-through: transparent where alpha < 128, opaque elsewhere
            var s = src.GetData<byte>(); var b = back.GetData<byte>();
            for (int i = 0; i < src.Width * src.Height; i++) Assert.Equal(s[i * 4 + 3] < 128 ? 0 : 255, b[i * 4 + 3]);
        }
        if (format == DdsCompression.BC2)
        {
            var s = src.GetData<byte>(); var b = back.GetData<byte>();
            for (int i = 0; i < src.Width * src.Height; i++) Assert.True(Math.Abs(s[i * 4 + 3] - b[i * 4 + 3]) <= 8);
        }
        // an independent decoder reads the same pixels
        var oracle = OracleDecode(bytes, ".dds");
        if (oracle != null) SameSamples(oracle, back, format == DdsCompression.BC7 ? 0 : 1);
    }

    static RawImage Gray(RawImage rgb)
    {
        var o = RawImage.Create(rgb.Width, rgb.Height, ColorModel.Gray, SampleType.UInt8);
        var s = rgb.GetData<byte>(); var d = o.GetData<byte>();
        for (int i = 0; i < d.Length; i++) d[i] = (byte)((s[i * rgb.Channels] * 3 + s[i * rgb.Channels + 1] * 5 + s[i * rgb.Channels + 2] * 2 + 5) / 10);
        return o;
    }

    static RawImage TwoChannels(RawImage rgb)
    {
        var o = RawImage.Create(rgb.Width, rgb.Height, ColorModel.Other, SampleType.UInt8, 2);
        var s = rgb.GetData<byte>(); var d = o.GetData<byte>();
        for (int i = 0; i < rgb.Width * rgb.Height; i++) { d[i * 2] = s[i * rgb.Channels]; d[i * 2 + 1] = s[i * rgb.Channels + 2]; }
        return o;
    }

    static void SameSamples(RawImage expected, RawImage actual, double tol)
    {
        Assert.Equal((expected.Width, expected.Height, expected.Channels), (actual.Width, actual.Height, actual.Channels));
        var e = ToDoubles(expected); var a = ToDoubles(actual);
        double worst = 0;
        for (int i = 0; i < e.Length; i++) worst = Math.Max(worst, Math.Abs(e[i] - a[i]));
        Assert.True(worst <= tol, $"max difference {worst}");
    }

    [Theory]
    [InlineData(BcQuality.Fast, false)]
    [InlineData(BcQuality.Normal, false)]
    [InlineData(BcQuality.High, false)]
    [InlineData(BcQuality.Normal, true)]
    public void Bc6h(BcQuality quality, bool negative)
    {
        foreach (bool smooth in new[] { true, false })
        {
            var src = Hdr(negative, smooth);
            var bytes = Dds.Encode(src, new DdsEncodeOptions { Compression = DdsCompression.BC6H, Quality = quality });
            var info = Dds.GetTextureInfo(bytes);
            Assert.Equal(negative ? "BC6H_SF16" : "BC6H_UF16", info.Format);
            var back = Dds.Decode(bytes);
            double rms = LogRms(src, back);
            _out.WriteLine($"BC6H {quality} negative={negative} smooth={smooth}: log2 rms {rms:F4}");
            // DirectXTex on the same images: 0.0361 smooth, 0.1770 photo (whose 4x4 blocks span up to 7 stops: dark noise in
            // linear light, which 16 interpolated values in BC6H's roughly logarithmic domain can only approximate)
            Assert.True(rms < (smooth ? 0.0361 : 0.177), $"log2 rms {rms:F4}");
            var oracle = OracleDecode(bytes, ".dds");
            if (oracle != null) SameSamples(oracle, back, 0);
        }
    }

    [Fact]
    public void HardBlocks()
    {
        // solid colours of every value, noise, two-colour checkers, partial blocks
        var rnd = new Random(7);
        var img = RawImage.Create(64 + 3, 64 + 2, ColorModel.Rgba, SampleType.UInt8);
        var d = img.GetData<byte>();
        for (int y = 0; y < img.Height; y++)
            for (int x = 0; x < img.Width; x++)
            {
                int i = (y * img.Width + x) * 4, b = (y / 4) * 17 + x / 4;
                for (int c = 0; c < 4; c++)
                    d[i + c] = c == 3 && y < 32 ? (byte)255 : (byte)(y < 32 ? (b * 37 + c * 91) % 256
                        : x < 32 ? rnd.Next(256)
                        : ((x + y) % 2 == 0 ? (b * 13 + c * 50) % 256 : 255 - (b * 13 + c * 50) % 256));
            }
        foreach (var f in new[] { DdsCompression.BC1, DdsCompression.BC2, DdsCompression.BC3, DdsCompression.BC7 })
            foreach (var q in new[] { BcQuality.Fast, BcQuality.Normal, BcQuality.High })
            {
                var bytes = Dds.Encode(img, new DdsEncodeOptions { Compression = f, Quality = q });
                var back = Dds.Decode(bytes);
                var oracle = OracleDecode(bytes, ".dds");
                if (oracle != null) SameSamples(oracle, back, f == DdsCompression.BC7 ? 0 : 1);
                // solid blocks (the upper half, x < 64) come back almost exactly
                var s = img.GetData<byte>(); var r = back.GetData<byte>();
                int worst = 0;
                for (int y = 0; y < 32; y++)
                    for (int x = 0; x < 64; x++)
                        for (int c = 0; c < 3; c++)
                            worst = Math.Max(worst, Math.Abs(s[(y * img.Width + x) * 4 + c] - r[(y * img.Width + x) * 4 + c]));
                _out.WriteLine($"{f} {q}: solid block max error {worst}");
                Assert.True(worst <= (f == DdsCompression.BC7 ? 1 : 3), $"{f} {q}: {worst}");
            }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Bc6hNoise(bool signed)
    {
        // wild blocks: every mode, endpoints far apart (deltas that do not fit), infinities and NaN
        var rnd = new Random(11);
        var img = RawImage.Create(33, 21, ColorModel.Rgb, SampleType.Float32);
        var d = img.GetData<float>();
        for (int i = 0; i < d.Length; i++)
        {
            double v = Math.Pow(2, rnd.NextDouble() * 30 - 15) * (signed && rnd.Next(2) == 0 ? -1 : 1);
            d[i] = i % 97 == 0 ? float.PositiveInfinity : i % 101 == 0 ? float.NaN : (float)((i / 3 / 7) % 3 == 0 ? v : Math.Abs(v) % 4);
        }
        foreach (var q in new[] { BcQuality.Fast, BcQuality.High })
        {
            var bytes = Dds.Encode(img, new DdsEncodeOptions { Compression = DdsCompression.BC6H, Quality = q });
            Assert.Equal(signed ? "BC6H_SF16" : "BC6H_UF16", Dds.GetTextureInfo(bytes).Format);
            var back = Dds.Decode(bytes);
            var oracle = OracleDecode(bytes, ".dds");
            if (oracle != null) SameSamples(oracle, back, 0);
            foreach (var h in back.GetData<Half>()) Assert.True(Half.IsFinite(h));
        }
    }

    [Fact]
    public void SignedBc4Bc5()
    {
        var img = RawImage.Create(13, 9, ColorModel.Other, SampleType.Int8, 2);
        var d = img.GetData<sbyte>();
        for (int y = 0; y < 9; y++)
            for (int x = 0; x < 13; x++)
                for (int c = 0; c < 2; c++) d[(y * 13 + x) * 2 + c] = (sbyte)Math.Clamp((int)(127 * Math.Sin(x * 0.3 + y * 0.2 + c * 2)), -127, 127);
        var bytes = Dds.Encode(img, new DdsEncodeOptions { Compression = DdsCompression.BC5 });
        Assert.Equal("BC5_SNORM", Dds.GetTextureInfo(bytes).Format);
        var back = Dds.Decode(bytes);
        Assert.Equal(SampleType.Int8, back.SampleType);
        AssertEqual(img, back, 12);
        var g = RawImage.Create(5, 6, ColorModel.Gray, SampleType.Int8);
        var gd = g.GetData<sbyte>();
        for (int i = 0; i < gd.Length; i++) gd[i] = (sbyte)(i * 9 - 127);
        var b4 = Dds.Encode(g, new DdsEncodeOptions { Compression = DdsCompression.BC4 });
        Assert.Equal("BC4_SNORM", Dds.GetTextureInfo(b4).Format);
        AssertEqual(g, Dds.Decode(b4), 12);   // a ramp of 160 within a block: 8 levels
    }

    [Fact]
    public void HeadersAndConversions()
    {
        var img = Photo();
        Assert.Equal("BC1_UNORM", Dds.GetTextureInfo(Dds.Encode(img, new DdsEncodeOptions { Compression = DdsCompression.BC1 })).Format);
        var bc1 = Dds.Encode(img, new DdsEncodeOptions { Compression = DdsCompression.BC1 });
        Assert.Equal("DXT1"u8.ToArray(), bc1.AsSpan(84, 4).ToArray());
        Assert.Equal(128 + (160 / 4) * (120 / 4) * 8, bc1.Length);
        Assert.Equal("BC7_UNORM_SRGB", Dds.GetTextureInfo(Dds.Encode(img, new DdsEncodeOptions { Compression = DdsCompression.BC7, Srgb = true })).Format);
        Assert.Equal("BC3_UNORM_SRGB", Dds.GetTextureInfo(Dds.Encode(img, new DdsEncodeOptions { Compression = DdsCompression.BC3, Srgb = true })).Format);

        // 16 bit and float images are scaled to 8 bit for the 8 bit formats
        var wide = RawImage.Create(img.Width, img.Height, ColorModel.Rgb, SampleType.UInt16);
        var s = img.GetData<byte>(); var w = wide.GetData<ushort>();
        for (int i = 0; i < s.Length; i++) w[i] = (ushort)(s[i] * 257);
        var opt = new DdsEncodeOptions { Compression = DdsCompression.BC7 };
        Assert.Equal(Dds.Encode(img, opt), Dds.Encode(wide, opt));

        // gray goes to all three colour channels
        var gray = Gray(img);
        var back = Dds.Decode(Dds.Encode(gray, opt));
        var g = gray.GetData<byte>(); var b = back.GetData<byte>();
        for (int i = 0; i < g.Length; i++) Assert.True(b[i * 4] == b[i * 4 + 1] && b[i * 4] == b[i * 4 + 2] && b[i * 4 + 3] == 255, $"texel {i}");
        var gb = RawImage.Create(gray.Width, gray.Height, ColorModel.Gray, SampleType.UInt8);
        for (int i = 0; i < g.Length; i++) gb.GetData<byte>()[i] = b[i * 4];
        Assert.True(Psnr8(gray, gb, 1) > 40, $"{Psnr8(gray, gb, 1):F2} dB");

        Assert.Throws<ImageNotSupportedException>(() => Dds.Encode(gray, new DdsEncodeOptions { Compression = DdsCompression.BC5 }));
    }

    [Fact]
    public void CompressedMipmapsAndCube()
    {
        var img = PhotoAlpha();
        var bytes = Dds.Encode(img, new DdsEncodeOptions { Compression = DdsCompression.BC3, Mipmaps = DdsMipmaps.Generate });
        var chain = Dds.DecodeMipmaps(bytes);
        Assert.Equal(8, chain.Length);   // 160x120 .. 1x1
        var exp = DdsWriterChain(img);
        for (int m = 0; m < chain.Length; m++)
        {
            Assert.Equal((exp[m].Width, exp[m].Height), (chain[m].Width, chain[m].Height));
            // each level is compressed like a 2D image of its own
            AssertEqual(Dds.Decode(Dds.Encode(exp[m], new DdsEncodeOptions { Compression = DdsCompression.BC3 })), chain[m]);
        }
        var oracle = OracleDecode(bytes, ".dds");
        if (oracle != null) SameSamples(oracle, chain[0], 1);

        var faces = Enumerable.Range(0, 6).Select(i => (IReadOnlyList<RawImage>)new[] { ImageOps.Crop(img, i * 8, 0, 64, 64) }).ToList();
        var cube = Dds.EncodeCube(faces, new DdsEncodeOptions { Compression = DdsCompression.BC1, Mipmaps = DdsMipmaps.Generate });
        var t = Dds.DecodeTexture(cube);
        Assert.Equal((DdsTextureKind.CubeMap, 6, 7, "BC1_UNORM"), (t.Kind, t.Faces.Count, t.MipLevels, t.Format));
        for (int f = 0; f < 6; f++) AssertEqual(Dds.Decode(Dds.Encode(faces[f][0], new DdsEncodeOptions { Compression = DdsCompression.BC1 })), t.GetImage(0, 0, f));
    }

    static RawImage[] DdsWriterChain(RawImage img)
    {
        var r = new List<RawImage> { img };
        int w = img.Width, h = img.Height;
        while (w > 1 || h > 1)
        {
            w = Math.Max(1, w / 2); h = Math.Max(1, h / 2);
            r.Add(ImageOps.Resize(img, w, h, ResampleFilter.Box));
        }
        return r.ToArray();
    }

    public static IEnumerable<object[]> VolumeCases()
    {
        foreach (var t in new[] { SampleType.UInt8, SampleType.UInt16, SampleType.Float16, SampleType.Float32 })
            foreach (var c in new[] { ColorModel.Gray, ColorModel.Rgba })
                yield return new object[] { t, c, DdsCompression.None };
        foreach (var f in new[] { DdsCompression.BC1, DdsCompression.BC3, DdsCompression.BC4, DdsCompression.BC7 })
            yield return new object[] { SampleType.UInt8, f == DdsCompression.BC4 ? ColorModel.Gray : ColorModel.Rgba, f };
        yield return new object[] { SampleType.Float32, ColorModel.Rgb, DdsCompression.BC6H };
    }

    [Theory, MemberData(nameof(VolumeCases))]
    public void Volumes(SampleType type, ColorModel color, DdsCompression format)
    {
        const int W = 11, H = 6, D = 5;
        var slices = new RawImage[D];
        var rnd = new Random(3);
        for (int z = 0; z < D; z++)
        {
            var s = RawImage.Create(W, H, color, type);
            var v = new double[s.Width * s.Height * s.Channels];
            for (int i = 0; i < v.Length; i++) v[i] = 0.5 + 0.4 * Math.Sin(i * 0.3 + z) + 0.05 * rnd.NextDouble();
            switch (type)
            {
                case SampleType.UInt8: { var d = s.GetData<byte>(); for (int i = 0; i < v.Length; i++) d[i] = (byte)(v[i] * 255); break; }
                case SampleType.UInt16: { var d = s.GetData<ushort>(); for (int i = 0; i < v.Length; i++) d[i] = (ushort)(v[i] * 65535); break; }
                case SampleType.Float16: { var d = s.GetData<Half>(); for (int i = 0; i < v.Length; i++) d[i] = (Half)v[i]; break; }
                default: { var d = s.GetData<float>(); for (int i = 0; i < v.Length; i++) d[i] = (float)v[i]; break; }
            }
            slices[z] = s;
        }
        var opt = new DdsEncodeOptions { Compression = format };
        var bytes = Dds.EncodeVolume(slices, opt);
        var t = Dds.DecodeTexture(bytes);
        Assert.Equal((DdsTextureKind.Texture3D, W, H, D, 1), (t.Kind, t.Width, t.Height, t.Depth, t.MipLevels));
        var back = t.GetSlices();
        Assert.Equal(D, back.Length);
        for (int z = 0; z < D; z++)
        {
            if (format == DdsCompression.None) Assert.Equal(slices[z].GetBytes().ToArray(), back[z].GetBytes().ToArray());
            else
                // each slice is compressed like a 2D image of its own
                AssertEqual(Dds.Decode(Dds.Encode(slices[z], opt)), back[z]);
        }
        Assert.Equal(1, Dds.GetInfo(bytes).FrameCount);
        // the legacy flags and the DX10 dimension agree
        Assert.True((BitConverter.ToUInt32(bytes, 112) & 0x200000) != 0 && BitConverter.ToUInt32(bytes, 24) == D);
        if (Dds.GetTextureInfo(bytes) is { DxgiFormat: not 0 } && bytes.AsSpan(84, 4).SequenceEqual("DX10"u8)) Assert.Equal(4u, BitConverter.ToUInt32(bytes, 132));
    }

    [Fact]
    public void VolumeRejections()
    {
        var a = RawImage.Create(4, 4, ColorModel.Rgb, SampleType.UInt8);
        var b = RawImage.Create(4, 3, ColorModel.Rgb, SampleType.UInt8);
        Assert.Throws<ArgumentException>(() => Dds.EncodeVolume(new[] { a, b }));
        Assert.Throws<ArgumentException>(() => Dds.EncodeVolume(Array.Empty<RawImage>()));
        // mip generation is ignored for volumes
        var t = Dds.DecodeTexture(Dds.EncodeVolume(new[] { a, a }, new DdsEncodeOptions { Mipmaps = DdsMipmaps.Generate }));
        Assert.Equal((2, 1), (t.Depth, t.MipLevels));
    }
}
