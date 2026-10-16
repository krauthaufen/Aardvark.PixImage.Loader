using System.Diagnostics;
using System.Text.Json;
using Aardvark.ImageCodecs;
using Xunit;
using static Aardvark.ImageCodecs.Tests.TestUtil;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>JPEG-LS: CharLS written and hand built fixtures (tests/oracle/gen_jpegls.py), our encoder against CharLS's bytes.</summary>
public class JpegLsTests
{
    public static IEnumerable<object[]> Fixtures
        => Files("jpegls", ".jls").Where(r => !Path.GetFileName((string)r[0]).StartsWith("enc_"));

    [Fact]
    public void FixturesArePresent() => Assert.True(Fixtures.Count() > 100);

    [Theory, MemberData(nameof(Fixtures))]
    public void DecodesFixture(string file)
    {
        var data = File.ReadAllBytes(Fixture(file));
        var expected = LoadRef(Fixture(file) + ".ref");
        Assert.Equal(ImageFormat.JpegLs, ImageCodec.DetectFormat(data));
        var img = JpegLs.Decode(data);
        AssertEqual(expected, img);
        var info = JpegLs.GetInfo(data);
        Assert.Equal((img.Width, img.Height, img.Channels, img.SampleType, img.Color), (info.Width, info.Height, info.Channels, info.SampleType, info.Color));
        // the generic entry points route here
        AssertEqual(expected, ImageCodec.Decode(data));
        var gi = ImageCodec.GetInfo(new MemoryStream(data));
        Assert.Equal((info.Width, info.Height, info.Channels), (gi.Width, gi.Height, gi.Channels));
    }

    [Fact]
    public void ReportsHeaderDetails()
    {
        var i = JpegLs.GetInfo(File.ReadAllBytes(Fixture("jpegls/gray12_pattern.jls")));
        Assert.Equal((41, 23, 1, SampleType.UInt16, ColorModel.Gray, 12), (i.Width, i.Height, i.Channels, i.SampleType, i.Color, i.BitsPerSample));
        i = JpegLs.GetInfo(File.ReadAllBytes(Fixture("jpegls/gray5_noise.jls")));
        Assert.Equal((SampleType.UInt8, 5), (i.SampleType, i.BitsPerSample));
        i = JpegLs.GetInfo(File.ReadAllBytes(Fixture("jpegls/spiff_rgb_dpi.jls")));
        Assert.Equal((96.0, 72.0, ColorModel.Rgb), (i.DpiX, i.DpiY, i.Color));
        var img = JpegLs.Decode(File.ReadAllBytes(Fixture("jpegls/spiff_rgb_dpi.jls")));
        Assert.Equal((96.0, 72.0), (img.Metadata!.DpiX, img.Metadata.DpiY));
        i = JpegLs.GetInfo(File.ReadAllBytes(Fixture("jpegls/oversize_70000x2.jls")));
        Assert.Equal((70000, 2), (i.Width, i.Height));
        i = JpegLs.GetInfo(File.ReadAllBytes(Fixture("jpegls/palette_rgb.jls")));
        Assert.Equal((3, ColorModel.Rgb, SampleType.UInt8), (i.Channels, i.Color, i.SampleType));
        i = JpegLs.GetInfo(File.ReadAllBytes(Fixture("jpegls/palette_gray16.jls")));
        Assert.Equal((1, SampleType.UInt16), (i.Channels, i.SampleType));
        i = JpegLs.GetInfo(File.ReadAllBytes(Fixture("jpegls/dnl.jls")));
        Assert.Equal((21, 13), (i.Width, i.Height));
        i = JpegLs.GetInfo(File.ReadAllBytes(Fixture("jpegls/exif_orientation6.jls")));
        Assert.Equal((11, 20, 6, true), (i.Width, i.Height, i.Orientation, i.OrientationApplied));
        Assert.Equal(TransferFunction.Srgb, i.Transfer);
    }

    [Fact]
    public void OptionsReturnStoredValues()
    {
        // palette indices instead of colours
        var d = File.ReadAllBytes(Fixture("jpegls/palette_rgb.jls"));
        var raw = JpegLs.Decode(d, new JpegLsDecodeOptions { ApplyMappingTables = false });
        Assert.Equal((1, SampleType.UInt8), (raw.Channels, raw.SampleType));
        Assert.True(((byte[])raw.Data).All(v => v < 16));
        Assert.Equal(1, JpegLs.GetInfo(d, new JpegLsDecodeOptions { ApplyMappingTables = false }).Channels);
        // HP transformed components
        d = File.ReadAllBytes(Fixture("jpegls/hp1_b8_ilv2.jls"));
        var expected = LoadRef(Fixture("jpegls/hp1_b8_ilv2.jls.ref"));
        raw = JpegLs.Decode(d, new JpegLsDecodeOptions { ApplyColorTransform = false });
        var e = (byte[])expected.Data; var r = (byte[])raw.Data;
        for (int i = 0; i < e.Length; i += 3)
        {
            Assert.Equal(e[i + 1], r[i + 1]);
            Assert.Equal((byte)(e[i] - e[i + 1] + 128), r[i]);
            Assert.Equal((byte)(e[i + 2] - e[i + 1] + 128), r[i + 2]);
        }
        // stored orientation
        d = File.ReadAllBytes(Fixture("jpegls/exif_orientation6.jls"));
        var stored = JpegLs.Decode(d, new JpegLsDecodeOptions { ApplyOrientation = false });
        Assert.Equal((20, 11), (stored.Width, stored.Height));
    }

    // ------------------------------------------------------------------ encoder

    public sealed record EncodeCase(string name, int bits, int near, int ilv, int tf, int[]? preset);

    public static IEnumerable<object[]> EncodeCases()
    {
        var json = File.ReadAllText(Path.Combine(FixtureDir, "jpegls", "encode_cases.json"));
        foreach (var c in JsonSerializer.Deserialize<EncodeCase[]>(json)!) yield return new object[] { c.name };
    }

    static EncodeCase Case(string name)
        => JsonSerializer.Deserialize<EncodeCase[]>(File.ReadAllText(Path.Combine(FixtureDir, "jpegls", "encode_cases.json")))!.Single(c => c.name == name);

    static JpegLsEncodeOptions Options(EncodeCase c) => new()
    {
        BitsPerSample = c.bits, Near = c.near, Interleave = (JpegLsInterleave)c.ilv, ColorTransform = (JpegLsColorTransform)c.tf,
        Preset = c.preset is { } p ? new JpegLsPresetParameters(p[0], p[1], p[2], p[3], p[4]) : null,
    };

    [Theory, MemberData(nameof(EncodeCases))]
    public void EncoderWritesTheBytesCharLsWrites(string name)
    {
        var c = Case(name);
        var src = LoadRef(Fixture($"jpegls/enc_{name}.ref"));
        var expected = File.ReadAllBytes(Fixture($"jpegls/enc_{name}.jls"));
        var ours = JpegLs.Encode(src, Options(c));
        Assert.Equal(expected.Length, ours.Length);
        Assert.True(expected.AsSpan().SequenceEqual(ours), $"first difference at byte {expected.AsSpan().CommonPrefixLength(ours)}");
        var back = JpegLs.Decode(ours);
        if (c.near == 0) AssertEqual(src, back);
        else AssertEqual(src, back, c.near);
    }

    public static IEnumerable<object[]> RoundTripCases()
    {
        foreach (var (ch, t) in new[] { (1, SampleType.UInt8), (3, SampleType.UInt8), (4, SampleType.UInt8), (2, SampleType.UInt8), (3, SampleType.UInt16), (1, SampleType.UInt16), (6, SampleType.UInt8) })
            foreach (var ilv in new[] { 0, 1, 2 })
                foreach (var near in new[] { 0, 2 })
                    if (ch > 1 || ilv == 0) yield return new object[] { ch, t, ilv, near };
    }

    static RawImage Synthetic(int w, int h, int ch, SampleType t, int seed, int maxVal)
    {
        var img = RawImage.Create(w, h, RawImage.DefaultColorModel(ch), t, ch);
        var rnd = new Random(seed);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int c = 0; c < ch; c++)
                {
                    int i = (y * w + x) * ch + c;
                    // smooth areas, flat runs and noise
                    double v = x < w / 3 ? 0.3 : (Math.Sin(x * 0.11 + c) * Math.Cos(y * 0.07) * 0.5 + 0.5) * 0.8 + (y % 9 == 0 ? rnd.NextDouble() * 0.2 : 0);
                    int s = Math.Clamp((int)(v * maxVal), 0, maxVal);
                    if (t == SampleType.UInt8) ((byte[])img.Data)[i] = (byte)s; else ((ushort[])img.Data)[i] = (ushort)s;
                }
        return img;
    }

    [Theory, MemberData(nameof(RoundTripCases))]
    public void RoundTrips(int ch, SampleType t, int ilv, int near)
    {
        var src = Synthetic(57, 31, ch, t, ch * 7 + ilv, t == SampleType.UInt8 ? 255 : 65535);
        var o = new JpegLsEncodeOptions { Interleave = (JpegLsInterleave)ilv, Near = near };
        var enc = JpegLs.Encode(src, o);
        var back = JpegLs.Decode(enc);
        AssertEqual(src, back, near);
        if (ch is 1 or 3 or 4 && (ilv != 1 || ch != 4))
            if (OracleDecode(enc, ".jls") is { } oracle) AssertEqual(back, oracle);
    }

    [Theory]
    [InlineData(2), InlineData(7), InlineData(12), InlineData(15)]
    public void RoundTripsOtherBitDepths(int bits)
    {
        var t = bits <= 8 ? SampleType.UInt8 : SampleType.UInt16;
        var src = Synthetic(40, 26, 3, t, bits, (1 << bits) - 1);
        foreach (var near in new[] { 0, 1 })
        {
            var enc = JpegLs.Encode(src, new JpegLsEncodeOptions { BitsPerSample = bits, Near = near });
            Assert.Equal(bits, JpegLs.GetInfo(enc).BitsPerSample);
            var back = JpegLs.Decode(enc);
            AssertEqual(src, back, near);
            if (OracleDecode(enc, ".jls") is { } oracle) AssertEqual(back, oracle);
        }
    }

    [Theory]
    [InlineData(1, 0, 1), InlineData(3, 2, 4), InlineData(3, 1, 2), InlineData(1, 0, 7), InlineData(4, 0, 3)]
    public void RestartIntervals(int ch, int ilv, int interval)
    {
        var src = Synthetic(33, 29, ch, SampleType.UInt8, interval, 255);
        foreach (var near in new[] { 0, 3 })
        {
            var enc = JpegLs.Encode(src, new JpegLsEncodeOptions { Interleave = (JpegLsInterleave)ilv, RestartInterval = interval, Near = near });
            Assert.True(enc.AsSpan().IndexOf((ReadOnlySpan<byte>)[0xFF, 0xD0]) > 0);
            var back = JpegLs.Decode(enc);
            AssertEqual(src, back, near);
            if (ch != 4 && OracleDecode(enc, ".jls") is { } oracle) AssertEqual(back, oracle);
        }
    }

    [Fact]
    public void WritesSpiffAndHeaders()
    {
        var src = Synthetic(20, 10, 3, SampleType.UInt8, 1, 255);
        src.Metadata = new ImageMetadata { DpiX = 300, DpiY = 150 };
        var enc = JpegLs.Encode(src, new JpegLsEncodeOptions { Spiff = true });
        Assert.Equal((0xFF, 0xE8), (enc[2], enc[3]));
        Assert.True(enc.AsSpan(6, 6).SequenceEqual("SPIFF\0"u8));
        var i = JpegLs.GetInfo(enc);
        Assert.Equal((300.0, 150.0), (i.DpiX, i.DpiY));
        AssertEqual(src, JpegLs.Decode(enc));
        if (OracleDecode(enc, ".jls") is { } oracle) AssertEqual(src, oracle);
        // > 65535: oversize LSE
        var wide = RawImage.Create(70001, 2, ColorModel.Gray, SampleType.UInt8);
        ((byte[])wide.Data)[70001 + 5] = 77;
        var e2 = JpegLs.Encode(wide);
        Assert.Equal((70001, 2), (JpegLs.GetInfo(e2).Width, JpegLs.GetInfo(e2).Height));
        AssertEqual(wide, JpegLs.Decode(e2));
        // through ImageCodec
        var e3 = ImageCodec.Encode(src, ImageFormat.JpegLs);
        AssertEqual(src, ImageCodec.Decode(e3));
        Assert.Equal(ImageFormat.JpegLs, ImageCodec.FormatFromExtension("x.JLS"));
    }

    [Fact]
    public void PresetParametersRoundTrip()
    {
        var src = Synthetic(40, 20, 1, SampleType.UInt16, 3, 4000);
        var o = new JpegLsEncodeOptions { BitsPerSample = 12, Preset = new JpegLsPresetParameters(MaxVal: 4000, T1: 9, T2: 30, T3: 90, Reset: 40) };
        var enc = JpegLs.Encode(src, o);
        AssertEqual(src, JpegLs.Decode(enc));
        o.Near = 4;
        AssertEqual(src, JpegLs.Decode(JpegLs.Encode(src, o)), 4);
    }

    [Fact]
    public void RejectsBadEncoderArguments()
    {
        var img = Synthetic(8, 8, 1, SampleType.UInt8, 1, 255);
        Assert.Throws<ArgumentException>(() => JpegLs.Encode(img, new JpegLsEncodeOptions { BitsPerSample = 6 }));   // samples exceed 63
        Assert.Throws<ArgumentException>(() => JpegLs.Encode(img, new JpegLsEncodeOptions { BitsPerSample = 12 }));  // UInt8 holds at most 8
        Assert.Throws<ArgumentException>(() => JpegLs.Encode(img, new JpegLsEncodeOptions { Near = 200 }));
        Assert.Throws<ArgumentException>(() => JpegLs.Encode(img, new JpegLsEncodeOptions { ColorTransform = JpegLsColorTransform.Hp1 }));
        Assert.Throws<ArgumentException>(() => JpegLs.Encode(img, new JpegLsEncodeOptions { Preset = new JpegLsPresetParameters(T1: 50, T2: 10) }));
        Assert.Throws<ArgumentException>(() => JpegLs.Encode(RawImage.Create(4, 4, ColorModel.Gray, SampleType.Float32)));
    }

    // ------------------------------------------------------------------ detection

    [Fact]
    public void DetectionSeparatesJpegAndJpegLs()
    {
        var jls = File.ReadAllBytes(Fixture("jpegls/gray8_pattern.jls"));
        var jpg = ImageCodec.Encode(Synthetic(16, 16, 3, SampleType.UInt8, 1, 255), ImageFormat.Jpeg);
        Assert.Equal(ImageFormat.JpegLs, ImageCodec.DetectFormat(jls));
        Assert.Equal(ImageFormat.Jpeg, ImageCodec.DetectFormat(jpg));
        Assert.False(JpegLs.IsMatch(jpg));
        Assert.True(Jpeg.IsMatch(jls));   // JPEG's own check is only the SOI prefix
        // the JPEG decoder says what it is instead of failing on the scan
        Assert.Throws<ImageNotSupportedException>(() => Jpeg.Decode(jls));
        // an application segment in front of the frame header: detection walks the segments
        var app = new byte[2 + 2 + 20000];
        app[0] = 0xFF; app[1] = 0xEB; app[2] = (byte)((app.Length - 2) >> 8); app[3] = (byte)(app.Length - 2);
        var big = jls.AsSpan(0, 2).ToArray().Concat(app).Concat(jls.Skip(2)).ToArray();
        Assert.Equal(ImageFormat.JpegLs, ImageCodec.DetectFormat(big));
        AssertEqual(LoadRef(Fixture("jpegls/gray8_pattern.jls.ref")), ImageCodec.Decode(big));
        // the stream path starts from an 8 KB prefix, which here ends inside the segment
        var si = ImageCodec.GetInfo(new NonSeekable(big));
        Assert.Equal((ImageFormat.JpegLs, 41, 23), (si.Format, si.Width, si.Height));
        var bigJpg = jpg.AsSpan(0, 2).ToArray().Concat(app).Concat(jpg.Skip(2)).ToArray();
        Assert.Equal(ImageFormat.Jpeg, ImageCodec.DetectFormat(bigJpg));
    }

    sealed class NonSeekable(byte[] data) : Stream
    {
        int _pos;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = Math.Min(count, data.Length - _pos);
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ------------------------------------------------------------------ robustness

    static void MustFailCleanly(byte[] data)
    {
        try { JpegLs.GetInfo(data); } catch (ImageFormatException) { } catch (ImageNotSupportedException) { }
        try { JpegLs.Decode(data); } catch (ImageFormatException) { } catch (ImageNotSupportedException) { }
    }

    public static IEnumerable<object[]> RobustnessFixtures => new[]
    {
        "rgb8_ilv2_pattern.jls", "rgb12_ilv1_mixed.jls", "near3_b8_c3_ilv2.jls", "restart_c3_ilv2_ri4.jls", "palette_rgb.jls",
        "subsampled_2x2.jls", "hp3_b16_ilv1.jls", "preset_4_9_30_10_n0.jls", "dnl.jls", "scans_per_component.jls", "gray16_noise.jls",
    }.Select(f => new object[] { f });

    [Theory, MemberData(nameof(RobustnessFixtures))]
    public void TruncatedInputFailsCleanly(string file)
    {
        var data = File.ReadAllBytes(Fixture("jpegls/" + file));
        var sw = Stopwatch.StartNew();
        for (int len = 0; len < data.Length; len += len < 200 ? 1 : 7)
            MustFailCleanly(data.AsSpan(0, len).ToArray());
        Assert.True(sw.Elapsed.TotalSeconds < 20);
    }

    [Theory, MemberData(nameof(RobustnessFixtures))]
    public void CorruptedInputFailsCleanly(string file)
    {
        var data = File.ReadAllBytes(Fixture("jpegls/" + file));
        var rnd = new Random(file.Length);
        var sw = Stopwatch.StartNew();
        for (int t = 0; t < 400; t++)
        {
            var d = (byte[])data.Clone();
            int n = 1 + rnd.Next(4);
            for (int k = 0; k < n; k++)
            {
                int p = t < 200 ? rnd.Next(Math.Min(d.Length, 80)) : rnd.Next(d.Length);
                d[p] = rnd.Next(3) == 0 ? (byte)0xFF : (byte)rnd.Next(256);
            }
            MustFailCleanly(d);
        }
        Assert.True(sw.Elapsed.TotalSeconds < 20);
    }

    [Fact]
    public void RejectsUnsupportedAndBrokenHeaders()
    {
        var d = File.ReadAllBytes(Fixture("jpegls/gray8_pattern.jls"));
        var x = (byte[])d.Clone();
        int sof = x.AsSpan().IndexOf((ReadOnlySpan<byte>)[0xFF, 0xF7]);
        x[sof + 1] = 0xF9;   // SOF57: ISO 14495-2
        Assert.Throws<ImageNotSupportedException>(() => JpegLs.Decode(x));
        x = (byte[])d.Clone();
        x[sof + 4] = 17;     // precision
        Assert.Throws<ImageFormatException>(() => JpegLs.Decode(x));
        int sos = d.AsSpan().IndexOf((ReadOnlySpan<byte>)[0xFF, 0xDA]);
        x = (byte[])d.Clone();
        x[sos + 7] = 200;    // NEAR beyond MAXVAL / 2
        Assert.Throws<ImageFormatException>(() => JpegLs.Decode(x));
        x = (byte[])d.Clone();
        x[sos + 8] = 3;      // interleave mode
        Assert.Throws<ImageFormatException>(() => JpegLs.Decode(x));
        Assert.Throws<ImageFormatException>(() => JpegLs.Decode(d.AsSpan(0, sos).ToArray()));   // no scan
        Assert.Throws<ImageFormatException>(() => JpegLs.Decode(ImageCodec.Encode(Synthetic(8, 8, 1, SampleType.UInt8, 1, 255), ImageFormat.Jpeg)));
    }
}
