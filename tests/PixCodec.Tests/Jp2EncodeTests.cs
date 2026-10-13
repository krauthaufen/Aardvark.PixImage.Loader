using PixCodec;
using Xunit;
using static PixCodec.Tests.TestUtil;
using static PixCodec.Tests.Jp2TestUtil;

namespace PixCodec.Tests;

public class Jp2EncodeTests
{
    /// <summary>Encodes, decodes with PixCodec and with the reference decoders; returns the encoded bytes.</summary>
    static byte[] RoundTrip(RawImage img, Jp2EncodeOptions o, bool exact = true, double minPsnr = 0)
    {
        var bytes = Jp2.Encode(img, o);
        var back = Jp2.Decode(bytes);
        var info = Jp2.GetInfo(bytes);
        Assert.Equal((img.Width, img.Height, img.Channels, img.SampleType, img.Color), (info.Width, info.Height, info.Channels, info.SampleType, info.Color));
        int bits = o.BitsPerSample > 0 ? o.BitsPerSample : RawImage.BytesPerSample(img.SampleType) * 8;
        Assert.Equal(bits, info.BitsPerSample);
        bool scaled = bits < 8;   // PixCodec returns such data scaled to 8 bit
        if (exact && !scaled) AssertEqual(img, back);
        else if (!exact) Assert.True(Psnr(img, back, Math.Pow(2, bits) - 1) >= minPsnr, $"PSNR {Psnr(img, back, Math.Pow(2, bits) - 1):F2} < {minPsnr}");
        // the independent decoders see the same samples (they return plain components)
        var oracle = Oracle(bytes, o.RawCodestream ? (o.HighThroughput ? ".jhc" : ".j2k") : (o.HighThroughput ? ".jph" : ".jp2"));
        if (oracle != null)
        {
            Assert.Equal((back.Width, back.Height, back.Channels, back.SampleType), (oracle.Width, oracle.Height, oracle.Channels, oracle.SampleType));
            double tol = exact ? 0 : Math.Max(1, 1 << Math.Max(0, bits - 8));
            double d = MaxDiff(oracle, back);
            Assert.True(d <= tol, $"reference decoder differs by {d}");
        }
        return bytes;
    }

    public static IEnumerable<object[]> Kinds()
    {
        foreach (var type in new[] { SampleType.UInt8, SampleType.UInt16, SampleType.Int8, SampleType.Int16, SampleType.UInt32, SampleType.Int32 })
            foreach (var (color, channels) in new[] { (ColorModel.Gray, 0), (ColorModel.GrayAlpha, 0), (ColorModel.Rgb, 0), (ColorModel.Rgba, 0), (ColorModel.Other, 5), (ColorModel.Other, 2) })
                yield return new object[] { type, color, channels };
    }

    [Theory, MemberData(nameof(Kinds))]
    public void LosslessRoundTrip(SampleType type, ColorModel color, int channels)
    {
        var img = Pattern(43, 31, color, type, channels, seed: (int)type * 7 + channels);
        foreach (bool ht in new[] { false, true })
            foreach (bool raw in new[] { false, true })
            {
                var o = new Jp2EncodeOptions { HighThroughput = ht, RawCodestream = raw };
                var bytes = Jp2.Encode(img, o);
                var back = Jp2.Decode(bytes);
                if (raw && color == ColorModel.Other && channels == 2)
                {
                    // a raw codestream cannot say that two components are not gray + alpha
                    Assert.Equal(ColorModel.GrayAlpha, back.Color);
                    Assert.True(img.GetBytes().SequenceEqual(back.GetBytes()));
                }
                else if (raw && color == ColorModel.Other) Assert.True(img.GetBytes().SequenceEqual(back.GetBytes()));
                else AssertEqual(img, back);
                var info = Jp2.GetInfo(bytes);
                Assert.Equal((back.Width, back.Height, back.Channels, back.SampleType, back.Color), (info.Width, info.Height, info.Channels, info.SampleType, info.Color));
                // the reference decoders on one container kind per block coder (the codestream inside is the same)
                var oracle = raw == ht ? Oracle(bytes, raw ? ".jhc" : ".jp2") : null;
                if (oracle != null) Assert.True(oracle.GetBytes().SequenceEqual(img.GetBytes()), $"{type} {color} ht={ht} raw={raw}: reference decoder disagrees");
            }
    }

    public static IEnumerable<object[]> OptionSets()
    {
        var sets = new (string, Action<Jp2EncodeOptions>)[]
        {
            ("tiles 64", o => { o.TileWidth = 64; o.TileHeight = 64; }),
            ("tiles 100x37, 3 levels", o => { o.TileWidth = 100; o.TileHeight = 37; o.Levels = 3; }),
            ("tiles 1x1", o => { o.TileWidth = 1; o.TileHeight = 1; }),
            ("tiles 3x5", o => { o.TileWidth = 3; o.TileHeight = 5; o.Levels = 2; }),
            ("levels 0", o => o.Levels = 0),
            ("levels 1", o => o.Levels = 1),
            ("levels 9", o => o.Levels = 9),
            ("blocks 4", o => { o.CodeBlockWidth = 4; o.CodeBlockHeight = 4; }),
            ("blocks 16x8", o => { o.CodeBlockWidth = 16; o.CodeBlockHeight = 8; }),
            ("blocks 1024x4", o => { o.CodeBlockWidth = 1024; o.CodeBlockHeight = 4; }),
            ("blocks 4x512", o => { o.CodeBlockWidth = 4; o.CodeBlockHeight = 512; }),
            ("RLCP", o => o.Progression = Jp2Progression.Rlcp),
            ("RPCL", o => o.Progression = Jp2Progression.Rpcl),
            ("PCRL", o => o.Progression = Jp2Progression.Pcrl),
            ("CPRL", o => o.Progression = Jp2Progression.Cprl),
            ("precincts", o => o.Precincts = new[] { (128, 128), (64, 64), (32, 32) }),
            ("precincts 32, PCRL, tiles", o => { o.Precincts = new[] { (32, 32) }; o.Progression = Jp2Progression.Pcrl; o.TileWidth = 128; o.TileHeight = 96; }),
            ("precincts 16x64, RPCL, blocks 8", o => { o.Precincts = new[] { (16, 64) }; o.Progression = Jp2Progression.Rpcl; o.CodeBlockWidth = 8; o.CodeBlockHeight = 8; }),
            ("precincts, CPRL, layers", o => { o.Precincts = new[] { (64, 64) }; o.Progression = Jp2Progression.Cprl; o.Layers = 4; o.TileWidth = 128; o.TileHeight = 128; }),
            ("no colour transform", o => o.ColorTransform = false),
            ("packet markers", o => o.PacketMarkers = true),
            ("packet markers, tiles, RPCL", o => { o.PacketMarkers = true; o.TileWidth = 64; o.TileHeight = 64; o.Progression = Jp2Progression.Rpcl; }),
            ("no TLM", o => o.TileLengthMarkers = false),
            ("sequential", o => o.Parallel = false),
        };
        foreach (var (name, _) in sets) yield return new object[] { name };
        _sets = sets;
    }
    static (string, Action<Jp2EncodeOptions>)[]? _sets;

    [Theory, MemberData(nameof(OptionSets))]
    public void StructureOptions(string name)
    {
        var set = _sets!.First(s => s.Item1 == name).Item2;
        var img = name.Contains("1x1") ? Pattern(23, 17, ColorModel.Rgb) : Pattern(213, 171, ColorModel.Rgb);
        foreach (bool ht in new[] { false, true })
        {
            var o = new Jp2EncodeOptions { RawCodestream = true, HighThroughput = ht };
            set(o);
            RoundTrip(img, o);
        }
        var g = Pattern(150, 131, ColorModel.Gray, SampleType.UInt16);
        var og = new Jp2EncodeOptions();
        set(og);
        RoundTrip(g, og);
    }

    [Theory]
    [InlineData(Jp2BlockStyle.Bypass), InlineData(Jp2BlockStyle.ResetContexts), InlineData(Jp2BlockStyle.TerminateAll), InlineData(Jp2BlockStyle.VerticalCausal)]
    [InlineData(Jp2BlockStyle.PredictableTermination), InlineData(Jp2BlockStyle.SegmentationSymbols)]
    [InlineData(Jp2BlockStyle.Bypass | Jp2BlockStyle.TerminateAll), InlineData(Jp2BlockStyle.TerminateAll | Jp2BlockStyle.PredictableTermination)]
    [InlineData(Jp2BlockStyle.Bypass | Jp2BlockStyle.PredictableTermination), InlineData((Jp2BlockStyle)63)]
    [InlineData(Jp2BlockStyle.Bypass | Jp2BlockStyle.ResetContexts | Jp2BlockStyle.VerticalCausal | Jp2BlockStyle.SegmentationSymbols)]
    public void BlockStyles(Jp2BlockStyle style)
    {
        var img = Pattern(150, 97, ColorModel.Rgb, seed: (int)style);
        RoundTrip(img, new Jp2EncodeOptions { BlockStyle = style, RawCodestream = true });
        RoundTrip(img, new Jp2EncodeOptions { BlockStyle = style, RawCodestream = true, Layers = 3, CodeBlockWidth = 32, CodeBlockHeight = 16 });
        // 16 bit data has enough bit planes for the bypass passes to carry real data
        RoundTrip(Pattern(64, 70, ColorModel.Gray, SampleType.UInt16, seed: 3), new Jp2EncodeOptions { BlockStyle = style, Layers = 2 });
        // lossy with truncated codewords
        var photo = Photo(160, 120);
        RoundTrip(photo, new Jp2EncodeOptions { BlockStyle = style, Lossless = false, TargetBitsPerPixel = 1.5, Layers = 3 }, exact: false, minPsnr: 30);
    }

    [Theory]
    [InlineData(1), InlineData(2), InlineData(4), InlineData(7), InlineData(8), InlineData(10), InlineData(12), InlineData(16), InlineData(20), InlineData(25), InlineData(26), InlineData(31), InlineData(32)]
    public void BitDepths(int bits)
    {
        var type = bits <= 8 ? SampleType.UInt8 : bits <= 16 ? SampleType.UInt16 : SampleType.UInt32;
        foreach (bool signed in new[] { false, true })
        {
            var t = !signed ? type : type == SampleType.UInt8 ? SampleType.Int8 : type == SampleType.UInt16 ? SampleType.Int16 : SampleType.Int32;
            var img = Pattern(61, 47, ColorModel.Rgb, t, seed: bits, bits: bits);
            foreach (bool ht in new[] { false, true })
            {
                var bytes = Jp2.Encode(img, new Jp2EncodeOptions { BitsPerSample = bits, HighThroughput = ht, RawCodestream = true });
                Assert.Equal(bits, Jp2.GetInfo(bytes).BitsPerSample);
                var raw = Jp2.Decode(bytes, new Jp2DecodeOptions { RawComponents = true });
                Assert.True(img.GetBytes().SequenceEqual(raw.GetBytes()), $"{bits} bit signed={signed} ht={ht}");
                var back = Jp2.Decode(bytes);
                if (bits >= 8 || signed) AssertEqual(img, back);
                var oracle = Oracle(bytes, ht ? ".jhc" : ".j2k");
                if (oracle != null) AssertEqual(oracle, back);
            }
        }
        var bad = Pattern(8, 8, ColorModel.Gray, type);
        if (bits < RawImage.BytesPerSample(type) * 8 && bits < 8)
            Assert.Throws<ArgumentException>(() => Jp2.Encode(bad, new Jp2EncodeOptions { BitsPerSample = bits }));
        Assert.Throws<ArgumentException>(() => Jp2.Encode(bad, new Jp2EncodeOptions { BitsPerSample = RawImage.BytesPerSample(type) * 8 + 1 }));
    }

    [Fact]
    public void OddSizes()
    {
        foreach (var (w, h) in new[] { (1, 1), (1, 2), (2, 1), (1, 77), (77, 1), (3, 3), (5, 2), (2, 5), (65, 64), (64, 65), (33, 129), (255, 3) })
            foreach (bool ht in new[] { false, true })
            {
                RoundTrip(Pattern(w, h, ColorModel.Rgb, seed: w + h), new Jp2EncodeOptions { HighThroughput = ht, RawCodestream = true });
                RoundTrip(Pattern(w, h, ColorModel.Gray, SampleType.UInt16, seed: w), new Jp2EncodeOptions { HighThroughput = ht, TileWidth = 32, TileHeight = 48 });
                RoundTrip(Photo(w, h), new Jp2EncodeOptions { HighThroughput = ht, Lossless = false, Quality = 90 }, exact: false, minPsnr: 30);
            }
    }

    [Fact]
    public void ExtremeContent()
    {
        foreach (var type in new[] { SampleType.UInt8, SampleType.UInt16, SampleType.Int16, SampleType.UInt32, SampleType.Int32 })
        {
            var noise = RawImage.Create(80, 70, ColorModel.Rgb, type);
            new Random(5).NextBytes(noise.GetBytes());
            var flat = RawImage.Create(80, 70, ColorModel.Rgb, type);
            flat.GetBytes().Fill(0xFF);
            var zero = RawImage.Create(80, 70, ColorModel.Rgb, type);
            // extremes next to each other: the largest wavelet coefficients the depth can produce
            var checker = RawImage.Create(80, 70, ColorModel.Rgb, type);
            var cb = checker.GetBytes();
            int bps = RawImage.BytesPerSample(type) * 3;
            for (int i = 0; i < 80 * 70; i++) if (((i % 80) + (i / 80)) % 2 == 0) cb.Slice(i * bps, bps).Fill(0xFF);
            if (type is SampleType.Int16 or SampleType.Int32)
            {
                // -2^(n-1) and 2^(n-1) - 1 alternating
                for (int i = 0; i < 80 * 70 * 3; i++)
                {
                    bool hi = ((i / 3 % 80) + (i / 3 / 80)) % 2 == 0;
                    if (type == SampleType.Int16) ((short[])checker.Data)[i] = hi ? short.MaxValue : short.MinValue;
                    else ((int[])checker.Data)[i] = hi ? int.MaxValue : int.MinValue;
                }
            }
            foreach (var img in new[] { noise, flat, zero, checker })
                foreach (bool ht in new[] { false, true })
                {
                    var bytes = Jp2.Encode(img, new Jp2EncodeOptions { HighThroughput = ht, RawCodestream = true });
                    AssertEqual(img, Jp2.Decode(bytes));
                    var oracle = Oracle(bytes, ht ? ".jhc" : ".j2k");
                    if (oracle != null) AssertEqual(img, oracle);
                }
        }
    }

    [Fact]
    public void QualityScale()
    {
        var photo = Photo(384, 256);
        foreach (bool ht in new[] { false, true })
        {
            double lastPsnr = 0;
            long lastSize = 0;
            foreach (int q in new[] { 1, 10, 25, 50, 75, 90, 100 })
            {
                var bytes = Jp2.Encode(photo, new Jp2EncodeOptions { Lossless = false, Quality = q, HighThroughput = ht });
                double psnr = Psnr(photo, Jp2.Decode(bytes), 255);
                Assert.True(psnr > lastPsnr && bytes.Length > lastSize, $"quality {q}: {psnr:F2} dB, {bytes.Length} bytes after {lastPsnr:F2} dB, {lastSize} bytes");
                (lastPsnr, lastSize) = (psnr, bytes.Length);
                if (q == 75) Assert.InRange(psnr, 36, 48);
                if (q == 100) Assert.True(psnr > 50);
                var oracle = Oracle(bytes, ht ? ".jph" : ".jp2");
                if (oracle != null) Assert.True(MaxDiff(oracle, Jp2.Decode(bytes)) <= 1);
            }
        }
        // 16 bit and gray
        var g16 = Photo(200, 150, ColorModel.Gray, sixteen: true);
        RoundTrip(g16, new Jp2EncodeOptions { Lossless = false, Quality = 80 }, exact: false, minPsnr: 36);
        RoundTrip(g16, new Jp2EncodeOptions { Lossless = false, Quality = 80, HighThroughput = true }, exact: false, minPsnr: 36);
        RoundTrip(Photo(200, 150, ColorModel.Rgba), new Jp2EncodeOptions { Lossless = false, Quality = 80, TileWidth = 128, TileHeight = 128 }, exact: false, minPsnr: 36);
        Assert.Throws<ImageNotSupportedException>(() => Jp2.Encode(Pattern(8, 8, ColorModel.Gray, SampleType.UInt32), new Jp2EncodeOptions { Lossless = false }));
    }

    [Theory]
    [InlineData(false), InlineData(true)]
    public void TargetRate(bool ht)
    {
        var photo = Photo(512, 384);
        double last = 0;
        foreach (double bpp in new[] { 0.1, 0.25, 0.5, 1.0, 2.0 })
            foreach (bool raw in new[] { true, false })
            {
                var bytes = Jp2.Encode(photo, new Jp2EncodeOptions { Lossless = false, TargetBitsPerPixel = bpp, HighThroughput = ht, RawCodestream = raw });
                double got = bytes.Length * 8.0 / (512 * 384);
                // never above the target; the classic coder lands within a few percent, HT (one pass per block, quantizer search) within 10
                Assert.True(got <= bpp * 1.0005 && got >= bpp * (ht ? 0.88 : bpp < 0.2 ? 0.9 : 0.95), $"{bpp} bpp asked, {got:F4} bpp written");
                double psnr = Psnr(photo, Jp2.Decode(bytes), 255);
                if (raw) { Assert.True(psnr > last, $"{bpp} bpp: {psnr:F2} dB"); last = psnr; }
                var oracle = Oracle(bytes, raw ? ".j2k" : ".jp2");
                if (oracle != null) Assert.True(MaxDiff(oracle, Jp2.Decode(bytes)) <= 1);
            }
        Assert.True(last > 40);
    }

    [Fact]
    public void QualityLayers()
    {
        var photo = Photo(256, 200);
        foreach (var lossless in new[] { true, false })
            foreach (var order in new[] { Jp2Progression.Lrcp, Jp2Progression.Rpcl })
            {
                var o = new Jp2EncodeOptions { Lossless = lossless, Quality = 90, Layers = 6, Progression = order, RawCodestream = true };
                var bytes = Jp2.Encode(photo, o);
                double last = 0;
                for (int l = 1; l <= 6; l++)
                {
                    var img = Jp2.Decode(bytes, new Jp2DecodeOptions { MaxLayers = l });
                    double psnr = Psnr(photo, img, 255);
                    Assert.True(psnr > last, $"layer {l}: {psnr:F2} dB after {last:F2} dB");
                    last = psnr;
                }
                if (lossless) Assert.True(double.IsPositiveInfinity(last));
                AssertEqual(Jp2.Decode(bytes), Jp2.Decode(bytes, new Jp2DecodeOptions { MaxLayers = 6 }));
                AssertEqual(Jp2.Decode(bytes), Jp2.Decode(bytes, new Jp2DecodeOptions { MaxLayers = 99 }));
                var oracle = Oracle(bytes, ".j2k");
                if (oracle != null) Assert.True(MaxDiff(oracle, Jp2.Decode(bytes)) <= (lossless ? 0 : 1));
            }
        // a rate target with layers: the last layer meets it, the earlier ones halve it
        var t = Jp2.Encode(photo, new Jp2EncodeOptions { Lossless = false, TargetBitsPerPixel = 1, Layers = 4, RawCodestream = true });
        Assert.InRange(t.Length * 8.0 / (256 * 200), 0.9, 1.0005);
    }

    [Fact]
    public void MetadataRoundTrip()
    {
        var img = Pattern(40, 30, ColorModel.Rgb);
        var icc = new byte[600];
        new Random(3).NextBytes(icc);
        "RGB "u8.CopyTo(icc.AsSpan(16));
        byte[] exif = { (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0, 1, 0, 0x12, 0x01, 3, 0, 1, 0, 0, 0, 5, 0, 0, 0, 0, 0, 0, 0 };
        img.Metadata = new ImageMetadata { IccProfile = icc, Exif = exif, DpiX = 300, DpiY = 72.5, Orientation = 5 };
        foreach (bool ht in new[] { false, true })
        {
            var bytes = Jp2.Encode(img, new Jp2EncodeOptions { HighThroughput = ht });
            var back = Jp2.Decode(bytes);
            AssertEqual(img, back);
            var m = back.Metadata!;
            Assert.Equal(icc, m.IccProfile);
            Assert.Equal(exif, m.Exif);
            Assert.Equal(5, m.Orientation);
            Assert.Equal(300, m.DpiX, 6); Assert.Equal(72.5, m.DpiY, 6);
            var info = Jp2.GetInfo(bytes);
            Assert.Equal((true, 5, 300.0, 72.5), (info.HasIccProfile, info.Orientation, Math.Round(info.DpiX, 6), Math.Round(info.DpiY, 6)));
            Assert.Equal((30, 40), (info.OrientedWidth, info.OrientedHeight));
            var oracle = Oracle(bytes, ht ? ".jph" : ".jp2");
            if (oracle != null) AssertEqual(img, oracle);
        }
        // orientation without an EXIF block is written as a minimal one
        img.Metadata = new ImageMetadata { Orientation = 8 };
        var o2 = Jp2.Decode(Jp2.Encode(img));
        Assert.Equal(8, o2.Metadata!.Orientation);
        Assert.Null(o2.Metadata.IccProfile);
        // not written on request, and never into a raw codestream
        img.Metadata = new ImageMetadata { IccProfile = icc, DpiX = 96, DpiY = 96 };
        Assert.Null(Jp2.Decode(Jp2.Encode(img, new Jp2EncodeOptions { WriteMetadata = false })).Metadata);
        Assert.Null(Jp2.Decode(Jp2.Encode(img, new Jp2EncodeOptions { RawCodestream = true })).Metadata);
        // odd resolutions survive the fraction of the resolution box
        foreach (double dpi in new[] { 1.0, 25.4, 96, 143.99, 600, 2400, 12345.678 })
        {
            img.Metadata = new ImageMetadata { DpiX = dpi, DpiY = dpi / 2 };
            var m = Jp2.Decode(Jp2.Encode(img)).Metadata!;
            Assert.Equal(dpi, m.DpiX, dpi * 1e-4); Assert.Equal(dpi / 2, m.DpiY, dpi * 1e-4);
        }
    }

    [Fact]
    public void AlphaIsDeclared()
    {
        // gray + alpha and RGBA carry a channel definition box; other channel counts are marked as unassociated
        foreach (var (color, channels) in new[] { (ColorModel.GrayAlpha, 0), (ColorModel.Rgba, 0), (ColorModel.Other, 4), (ColorModel.Other, 3), (ColorModel.Other, 1), (ColorModel.Other, 7) })
        {
            var img = Pattern(20, 14, color, SampleType.UInt8, channels);
            var back = Jp2.Decode(Jp2.Encode(img));
            // one "other" channel is a gray image
            Assert.Equal(color == ColorModel.Other && channels == 1 ? ColorModel.Gray : color, back.Color);
            Assert.True(img.GetBytes().SequenceEqual(back.GetBytes()));
        }
        // lossy with alpha keeps the alpha channel close
        var rgba = Photo(120, 90, ColorModel.Rgba);
        var lossy = Jp2.Decode(Jp2.Encode(rgba, new Jp2EncodeOptions { Lossless = false, Quality = 85 }));
        Assert.Equal(ColorModel.Rgba, lossy.Color);
        Assert.True(Psnr(rgba, lossy, 255) > 38);
    }

    [Fact]
    public void HtRefinementPasses()
    {
        // The encoder normally writes one HT cleanup pass. With this hook it codes the cleanup one bit plane up and
        // adds SigProp and MagRef passes, which is what transcoders and rate controlled HT encoders produce; the
        // result has to decode the same here and in OpenJPEG / OpenJPH.
        J2kEncoder.HtRefinementPasses = true;
        try
        {
            foreach (var style in new[] { Jp2BlockStyle.None, Jp2BlockStyle.VerticalCausal })
                foreach (var (w, h, color) in new[] { (64, 64, ColorModel.Gray), (200, 150, ColorModel.Rgb), (37, 53, ColorModel.Gray), (5, 3, ColorModel.Gray), (130, 97, ColorModel.Rgb) })
                {
                    var img = Pattern(w, h, color, seed: w);
                    foreach (var o in new[]
                    {
                        new Jp2EncodeOptions { HighThroughput = true, RawCodestream = true, BlockStyle = style },
                        new Jp2EncodeOptions { HighThroughput = true, RawCodestream = true, BlockStyle = style, TileWidth = 64, TileHeight = 64, CodeBlockWidth = 16, CodeBlockHeight = 16 },
                        new Jp2EncodeOptions { HighThroughput = true, RawCodestream = true, BlockStyle = style, Lossless = false, Quality = 60 },
                    })
                    {
                        var bytes = Jp2.Encode(img, o);
                        var back = Jp2.Decode(bytes);
                        // the bit plane below the cleanup is only coded where the refinement passes reach
                        Assert.True(MaxDiff(img, back) <= (o.Lossless ? 1 : 60));
                        var oracle = Oracle(bytes, ".jhc");
                        if (oracle != null) Assert.True(MaxDiff(oracle, back) <= (o.Lossless ? 0 : 1), $"{w}x{h} {style}");
                    }
                }
        }
        finally { J2kEncoder.HtRefinementPasses = false; }
    }

    [Fact]
    public void RejectsWhatCannotBeStored()
    {
        Assert.Throws<ImageNotSupportedException>(() => Jp2.Encode(RawImage.Create(4, 4, ColorModel.Gray, SampleType.Float32)));
        Assert.Throws<ImageNotSupportedException>(() => Jp2.Encode(RawImage.Create(4, 4, ColorModel.Gray, SampleType.UInt64)));
        var img = Pattern(16, 16, ColorModel.Gray);
        Assert.Throws<ArgumentException>(() => Jp2.Encode(img, new Jp2EncodeOptions { CodeBlockWidth = 48 }));
        Assert.Throws<ArgumentException>(() => Jp2.Encode(img, new Jp2EncodeOptions { CodeBlockWidth = 128, CodeBlockHeight = 64 }));
        Assert.Throws<ArgumentException>(() => Jp2.Encode(img, new Jp2EncodeOptions { Precincts = new[] { (100, 100) } }));
        Assert.Throws<ArgumentException>(() => Jp2.Encode(img, new Jp2EncodeOptions { Progression = (Jp2Progression)7 }));
        Assert.Throws<ArgumentException>(() => Jp2.Encode(Pattern(300, 300, ColorModel.Gray), new Jp2EncodeOptions { TileWidth = 1, TileHeight = 1 }));
        // writes to a stream that cannot seek
        using var ms = new MemoryStream();
        using (var w = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionLevel.Fastest, true)) Jp2.Encode(img, w);
        Assert.True(ms.Length > 0);
        // and through the format independent entry point (lossless by default)
        AssertEqual(img, ImageCodec.Decode(ImageCodec.Encode(img, ImageFormat.Jp2)));
    }
}
