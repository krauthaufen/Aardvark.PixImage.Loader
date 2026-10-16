using Aardvark.ImageCodecs;
using Aardvark.ImageCodecs.JpegXl;
using Xunit;
using Xunit.Abstractions;
using static Aardvark.ImageCodecs.Tests.JxlEncodeTests;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>JPEG XL encoder: metadata boxes, ICC profiles, orientation, container, animation, headers.</summary>
public class JxlEncodeMetadataTests(ITestOutputHelper output)
{
    static byte[] ExifBlob(int orientation)
        => new byte[] { (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0, 2, 0, 0x12, 0x01, 3, 0, 1, 0, 0, 0, (byte)orientation, 0, 0, 0, 0x31, 0x01, 2, 0, 2, 0, 0, 0, (byte)'x', 0, 0, 0, 0, 0, 0, 0 };

    static readonly byte[] XmpPacket = System.Text.Encoding.UTF8.GetBytes(
        "<x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'>"
        + "<rdf:Description xmlns:dc='http://purl.org/dc/elements/1.1/' dc:format='image/jxl'/></rdf:RDF></x:xmpmeta>");

    static RawImage Strip(RawImage i) => new(i.Width, i.Height, i.Color, i.Data, i.Channels);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExifAndXmpGoIntoContainerBoxes(bool lossless)
    {
        var img = Synthetic(40, 30, ColorModel.Rgb, SampleType.UInt8, 0);
        img.Metadata = new ImageMetadata { Exif = ExifBlob(1), Xmp = XmpPacket };
        var enc = Jxl.Encode(img, new JxlEncodeOptions { Lossless = lossless });
        Assert.Equal(new byte[] { 0, 0, 0, 0x0C, (byte)'J', (byte)'X', (byte)'L', (byte)' ' }, enc.Take(8));
        var (exif, xmp, jumbf) = Jxl.GetMetadataBoxes(enc);
        Assert.Equal(ExifBlob(1), exif);
        Assert.Equal(XmpPacket, xmp);
        Assert.Null(jumbf);
        var dec = Jxl.Decode(enc);
        Assert.Equal(ExifBlob(1), dec.Metadata!.Exif);
        Assert.Equal(XmpPacket, dec.Metadata.Xmp);
        if (lossless)
        {
            AssertExact(img, Strip(dec));
            if (Libjxl(enc) is { } r) AssertSameSamples(img, r);
        }
        // without metadata: a bare codestream; KeepMetadata = false drops the boxes
        img.Metadata = null;
        Assert.Equal(new byte[] { 0xFF, 0x0A }, Jxl.Encode(img, new JxlEncodeOptions { Lossless = lossless }).Take(2));
        img.Metadata = new ImageMetadata { Exif = ExifBlob(1) };
        Assert.Equal(new byte[] { 0xFF, 0x0A }, Jxl.Encode(img, new JxlEncodeOptions { Lossless = lossless, KeepMetadata = false }).Take(2));
        var forced = Jxl.Encode(img, new JxlEncodeOptions { Lossless = lossless, KeepMetadata = false, ForceContainer = true });
        Assert.Equal(0x0C, forced[3]);
        Assert.Equal((40, 30), (Jxl.Decode(forced).Width, Jxl.Decode(forced).Height));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(8)]
    public void OrientationIsStoredInTheCodestream(int orientation)
    {
        var img = Synthetic(23, 11, ColorModel.Rgba, SampleType.UInt8, 0);
        img.Metadata = new ImageMetadata { Orientation = orientation, Exif = ExifBlob(1) };
        var enc = Jxl.Encode(img, new JxlEncodeOptions { Lossless = true });
        Assert.Equal(orientation, Jxl.GetInfo(enc).Orientation);
        var stored = Jxl.Decode(enc, new JxlDecodeOptions { ApplyOrientation = false });
        AssertExact(img, Strip(stored));
        Assert.Equal(orientation, stored.Metadata!.Orientation);
        // the EXIF copy says the same as the codestream
        Assert.Equal(orientation, ExifTags.Read(stored.Metadata.Exif!));
        var up = Jxl.Decode(enc);
        AssertExact(Strip(ImageOps.ApplyOrientation(img, orientation)), Strip(up));
        if (Libjxl(enc) is { } r) AssertSameSamples(img, r);
    }

    [Fact]
    public void IccProfileLossless()
    {
        var icc = IccProfile.DisplayP3.ToBytes();
        var img = Synthetic(64, 48, ColorModel.Rgb, SampleType.UInt16, 0);
        img.Metadata = new ImageMetadata { IccProfile = icc };
        var enc = Jxl.Encode(img, new JxlEncodeOptions { Lossless = true });
        Assert.True(Jxl.GetInfo(enc).HasIccProfile);
        var dec = Jxl.Decode(enc);
        Assert.Equal(icc, dec.Metadata!.IccProfile);
        AssertExact(img, Strip(dec));
        if (Libjxl(enc) is { } r) AssertSameSamples(img, r);
        // a gray profile with a gray image
        var g = Synthetic(30, 20, ColorModel.Gray, SampleType.UInt8, 0);
        var gicc = IccProfile.GrayGamma22.ToBytes();
        g.Metadata = new ImageMetadata { IccProfile = gicc };
        var gd = Jxl.Decode(Jxl.Encode(g, new JxlEncodeOptions { Lossless = true }));
        Assert.Equal(gicc, gd.Metadata!.IccProfile);
        Assert.Equal((byte[])g.Data, (byte[])gd.Data);
        // a profile of the wrong colour space is left out rather than mislabelling the samples
        var rgb = Synthetic(30, 20, ColorModel.Rgb, SampleType.UInt8, 0);
        rgb.Metadata = new ImageMetadata { IccProfile = gicc };
        Assert.False(Jxl.GetInfo(Jxl.Encode(rgb, new JxlEncodeOptions { Lossless = true })).HasIccProfile);
    }

    [Fact]
    public void IccProfileLossy()
    {
        // Display P3 samples: coded through linear sRGB (XYB), decoded back to the profile's space
        var icc = IccProfile.DisplayP3.ToBytes();
        var img = Synthetic(96, 64, ColorModel.Rgb, SampleType.UInt8, 4);
        img.Metadata = new ImageMetadata { IccProfile = icc };
        var enc = Jxl.Encode(img, new JxlEncodeOptions { Distance = 0.5f });
        var dec = Jxl.Decode(enc);
        Assert.Equal(icc, dec.Metadata!.IccProfile);
        double p = ColourPsnr(img, dec);
        output.WriteLine($"P3 lossy: {p:F2} dB");
        Assert.True(p > 38, $"PSNR {p:F2}");
    }

    [Fact]
    public void IntensityTargetIsWritten()
    {
        var img = Synthetic(32, 32, ColorModel.Rgb, SampleType.Float32, 4);
        var enc = Jxl.Encode(img, new JxlEncodeOptions { Distance = 1, IntensityTarget = 4000 });
        var cs = JxlContainer.Parse(enc).Codestream(enc);
        var h = JxlImageHeader.Read(new JxlBitReader(cs));
        Assert.Equal(4000f, h.ToneMapping.IntensityTarget);
        Assert.True(h.XybEncoded);
        // the decoded values come back on the scale of the input
        Assert.True(ColourPsnr(img, Jxl.Decode(enc)) > 28);
    }

    [Fact]
    public void FloatCodestreamsDeclareLevel10()
    {
        var enc = Jxl.Encode(Synthetic(16, 16, ColorModel.Rgb, SampleType.Float32, 0), new JxlEncodeOptions { Lossless = true });
        var c = JxlContainer.Parse(enc);
        Assert.True(c.IsContainer);
        Assert.Equal(10, c.Level);
        var half = Jxl.Encode(Synthetic(16, 16, ColorModel.Rgb, SampleType.Float16, 0), new JxlEncodeOptions { Lossless = true });
        Assert.False(JxlContainer.Parse(half).IsContainer);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnimationRoundTrips(bool lossless)
    {
        var frames = new List<JxlInputFrame>();
        int[] durations = { 100, 40, 250, 1 };
        for (int i = 0; i < durations.Length; i++)
            frames.Add(new JxlInputFrame(Synthetic(70, 50, ColorModel.Rgba, SampleType.UInt8, i == 2 ? 1 : 0, 10 + i), durations[i], $"frame {i}"));
        var enc = Jxl.EncodeAnimation(frames, new JxlEncodeOptions { Lossless = lossless }, loopCount: 3);
        var anim = Jxl.DecodeAnimation(enc);
        Assert.True(anim.IsAnimated);
        Assert.Equal(3, anim.LoopCount);
        Assert.Equal(1000.0, anim.TicksPerSecond);
        Assert.Equal(durations, anim.Frames.Select(f => f.DurationMs));
        Assert.Equal(frames.Select(f => f.Name), anim.Frames.Select(f => f.Name));
        Assert.Equal(4, Jxl.GetInfo(enc).FrameCount);
        for (int i = 0; i < frames.Count; i++)
        {
            var got = anim.Frames[i].Image;
            if (lossless) AssertExact(frames[i].Image, Strip(got));
            else Assert.True(ColourPsnr(frames[i].Image, got) > 25, $"frame {i}: {ColourPsnr(frames[i].Image, got):F2} dB");   // the right frame (a wrong one is far below)
        }
        if (Libjxl(enc) is { } r)
        {
            // all frames stacked vertically
            Assert.Equal((70, 50 * frames.Count, 4), (r.Width, r.Height, r.Channels));
            if (lossless)
                for (int i = 0; i < frames.Count; i++)
                    Assert.Equal((byte[])frames[i].Image.Data, ((byte[])r.Data).AsSpan(i * 70 * 50 * 4, 70 * 50 * 4).ToArray());
        }
        // frames of another size are rejected, as are empty animations
        frames.Add(new JxlInputFrame(Synthetic(10, 10, ColorModel.Rgba, SampleType.UInt8, 0), 10));
        Assert.Throws<ArgumentException>(() => Jxl.EncodeAnimation(frames, new JxlEncodeOptions { Lossless = lossless }));
        Assert.Throws<ArgumentException>(() => Jxl.EncodeAnimation(Array.Empty<JxlInputFrame>()));
        Assert.Throws<ArgumentOutOfRangeException>(() => Jxl.EncodeAnimation(frames.Take(1), loopCount: -1));
    }

    [Fact]
    public void HeadersReadBackAsWritten()
    {
        var h = new JxlImageHeader
        {
            Width = 1234, Height = 77, Orientation = 7, BitDepth = new JxlBitDepth { Bits = 12 }, XybEncoded = false,
            Animation = new JxlAnimationHeader { TpsNumerator = 30000, TpsDenominator = 1001, NumLoops = 5, HaveTimecodes = true },
            Colour = new JxlColourEncoding { ColourSpace = JxlColourSpace.Rgb, Transfer = JxlTransfer.Gamma, Gamma = 0.45455, Primaries = JxlPrimaries.P3 },
            ExtraChannels = new[] { new JxlExtraChannel { Type = JxlExtraChannelType.Alpha, BitDepth = new JxlBitDepth { Bits = 12 }, AlphaAssociated = true, Name = "a" } },
        };
        h.ToneMapping.IntensityTarget = 1000;
        var w = new JxlBitWriter();
        JxlHeaderWriter.WriteImageHeader(w, h);
        var f = new JxlFrameHeader
        {
            Modular = true, Width = 1234, Height = 77, Duration = 99, Timecode = 0x12345678, IsLast = false, SaveAsReference = 2, Name = "n",
            EcUpsampling = new[] { 1 }, EcBlending = new JxlBlendingInfo[1], GroupSizeShift = 2,
            Filter = new JxlRestorationFilter { Gab = false, EpfIters = 1, EpfSigmaForModular = 0.5f },
        };
        JxlHeaderWriter.WriteFrameHeader(w, f, h);
        var br = new JxlBitReader(w.ToArray());
        var r = JxlImageHeader.Read(br);
        Assert.Equal((1234, 77, 7, 12, false), (r.Width, r.Height, r.Orientation, r.BitDepth.Bits, r.XybEncoded));
        Assert.Equal((30000u, 1001u, 5u, true), (r.Animation!.TpsNumerator, r.Animation.TpsDenominator, r.Animation.NumLoops, r.Animation.HaveTimecodes));
        Assert.Equal((JxlTransfer.Gamma, JxlPrimaries.P3), (r.Colour.Transfer, r.Colour.Primaries));
        Assert.Equal(0.45455, r.Colour.Gamma, 6);
        Assert.Equal(1000f, r.ToneMapping.IntensityTarget);
        Assert.Equal(("a", true, 12), (r.ExtraChannels[0].Name, r.ExtraChannels[0].AlphaAssociated, r.ExtraChannels[0].BitDepth.Bits));
        var rf = JxlFrameHeader.Read(br, r);
        Assert.Equal((true, 99u, 0x12345678u, false, 2, "n", 2), (rf.Modular, rf.Duration, rf.Timecode, rf.IsLast, rf.SaveAsReference, rf.Name, rf.GroupSizeShift));
        Assert.Equal((false, 1, 0.5f), (rf.Filter.Gab, rf.Filter.EpfIters, rf.Filter.EpfSigmaForModular));
    }

    [Fact]
    public void BitWriterPrimitives()
    {
        var w = new JxlBitWriter(16);
        var vals = new ulong[] { 0, 1, 16, 17, 272, 273, 4096, 1UL << 40, ulong.MaxValue };
        foreach (var v in vals) w.WriteU64(v);
        w.WriteF16(0.5f); w.WriteF16(-3.25f);
        w.WriteEnum(5); w.WriteEnum(40);
        for (int i = 0; i < 100; i++) w.Write(i % 33, (ulong)i * 2654435761UL);
        var br = new JxlBitReader(w.ToArray());
        foreach (var v in vals) Assert.Equal(v, br.ReadU64());
        Assert.Equal(0.5f, br.ReadF16()); Assert.Equal(-3.25f, br.ReadF16());
        Assert.Equal(5, br.ReadEnum()); Assert.Equal(40, br.ReadEnum());
        for (int i = 0; i < 100; i++)
        {
            int n = i % 33;
            Assert.Equal(n == 0 ? 0UL : ((ulong)i * 2654435761UL) & ((1UL << n) - 1), br.ReadBits64(n));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => new JxlBitWriter().WriteU32(5, JxlBitReader.Val(1), JxlBitReader.Val(2), JxlBitReader.Val(3), JxlBitReader.Val(4)));
    }
}
