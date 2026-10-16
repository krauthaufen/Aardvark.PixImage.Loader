using Xunit;
using static Aardvark.ImageCodecs.Tests.TestUtil;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>Apple icons (ICNS) and animated cursors (ANI): fixtures (tests/oracle/gen_icns.py, gen_ani.py), round trips, Pillow.</summary>
public class IcnsAniTests
{
    // ------------------------------------------------------------------ ICNS

    public static IEnumerable<object[]> IcnsFixtures => Files("icns", ".icns");

    [Theory, MemberData(nameof(IcnsFixtures))]
    public void IcnsMatchesReference(string file)
    {
        var data = File.ReadAllBytes(Fixture(file));
        var all = ImageCodec.DecodeAll(data);
        var entries = Icns.ReadEntries(data);
        Assert.Equal(entries.Count, all.Length);
        for (int i = 0; i < all.Length; i++)
        {
            var expected = LoadRef(Fixture(file) + $".{i}.ref");
            AssertEqual(expected, all[i].Channels == 4 ? all[i] : IconTextureTestUtil.ToRgba(all[i]));
            Assert.Equal((entries[i].Width, entries[i].Height), (all[i].Width, all[i].Height));
        }
        Assert.False(File.Exists(Fixture(file) + $".{all.Length}.ref"));
        var info = ImageCodec.GetInfo(data);
        Assert.Equal(ImageFormat.Icns, info.Format);
        Assert.Equal(ImageFormat.Icns, ImageCodec.FormatFromExtension(file));
        Assert.Equal(entries.Count, info.FrameCount);
        var top = ImageCodec.Decode(data);
        Assert.Equal(entries.Max(e => e.Width * e.Height), top.Width * top.Height);
        Assert.Equal((top.Width, top.Height, top.Channels, top.SampleType), (info.Width, info.Height, info.Channels, info.SampleType));
        Assert.Equal(info, ImageCodec.GetInfo(new MemoryStream(data)) with { Metadata = info.Metadata });
        Assert.Equal(TransferFunction.Srgb, top.Metadata!.Transfer);
    }

    [Fact]
    public void IcnsEntryKinds()
    {
        var es = Icns.ReadEntries(File.ReadAllBytes(Fixture("icns/legacy.icns")));
        Assert.Equal(new[] { "ICN#", "ics#", "icm#", "ich#", "ICON", "icl4", "ics4", "icm4", "ich4", "icl8", "ics8", "icm8", "ich8", "is32", "il32", "ih32", "it32" }, es.Select(e => e.Type));
        Assert.Equal(IcnsElementKind.Palette8, es.First(e => e.Type == "icl8").Kind);
        Assert.Equal((16, 12), es.Where(e => e.Type == "icm#").Select(e => (e.Width, e.Height)).Single());
        var mac = Icns.ReadEntries(File.ReadAllBytes(Fixture("icns/mac.icns")));
        Assert.Equal(IcnsElementKind.Argb, mac.First(e => e.Type == "ic04").Kind);
        Assert.Equal(2, mac.First(e => e.Type == "ic12").Scale);
        Assert.Equal(IcnsElementKind.Jpeg2000, Icns.ReadEntries(File.ReadAllBytes(Fixture("icns/jp2.icns")))[0].Kind);
        // the 1 bit icons: black pixels, mask
        var icn = Icns.Decode(File.ReadAllBytes(Fixture("icns/legacy.icns")), new IcnsDecodeOptions { Index = 0 });
        Assert.Equal(32, icn.Width);
    }

    static RawImage Icon(int s, int seed)
    {
        var img = MiscFormatTests.RandomInt(s, s, ColorModel.Rgba, SampleType.UInt8, seed, smooth: true);
        var a = img.GetData<byte>();
        for (int i = 3; i < a.Length; i += 4) a[i] = (byte)(i / 4 % s < s / 3 ? 0 : a[i]);   // transparent and translucent parts
        return img;
    }

    [Fact]
    public void IcnsRoundTrip()
    {
        var images = new[] { 16, 32, 48, 64, 128, 256 }.Select(s => Icon(s, s)).ToArray();
        var enc = Icns.Encode(images);
        Assert.Equal(new[] { "ic04", "ic05", "ih32", "ic12", "ic07", "ic08" }, Icns.ReadEntries(enc).Select(e => e.Type));
        var dec = Icns.DecodeAll(enc);
        for (int i = 0; i < images.Length; i++) AssertEqual(images[i], dec[i]);
        // Pillow takes the largest (PNG)
        if (OracleDecode(enc, ".icns") is { } o) AssertEqual(images[^1], o);

        var all = Icns.Encode(images, new IcnsEncodeOptions { LegacyTypes = true, SmallAsPng = true, RetinaDuplicates = true });
        Assert.Equal(new[] { "icp4", "is32", "icp5", "il32", "ic11", "ih32", "ic12", "ic07", "it32", "ic08", "ic13" }, Icns.ReadEntries(all).Select(e => e.Type));
        foreach (var (e, img) in Icns.ReadEntries(all).Zip(Icns.DecodeAll(all)))
            AssertEqual(images.Single(i => i.Width == e.Width), img);
        Assert.Throws<ArgumentException>(() => Icns.Encode(new[] { Icon(20, 1) }));
        Assert.Throws<ArgumentException>(() => Icns.EncodeElements(new[] { ("icl8", Icon(32, 1)) }, new MemoryStream()));
        // other colour models and sample types are converted to RGBA8
        var gray = MiscFormatTests.RandomInt(32, 32, ColorModel.Gray, SampleType.UInt16, 3);
        var g = Icns.Decode(Icns.Encode(new[] { gray }));
        Assert.Equal(ColorModel.Rgba, g.Color);
    }

    /// <summary>Our RLE writer and masks, read back by Pillow: a file of only the 24 bit RLE types.</summary>
    [Theory]
    [InlineData("is32", 16)]
    [InlineData("il32", 32)]
    [InlineData("ih32", 48)]
    [InlineData("it32", 128)]
    public void IcnsRleReadByPillow(string type, int size)
    {
        var img = Icon(size, size + 1);
        var enc = new MemoryStream();
        Icns.EncodeElements(new[] { (type, img) }, enc);
        AssertEqual(img, Icns.Decode(enc.ToArray()));
        if (OracleDecode(enc.ToArray(), ".icns") is { } o) AssertEqual(img, o);
    }

    [Theory, MemberData(nameof(IcnsFixtures))]
    public void IcnsRobustness(string file)
    {
        var data = File.ReadAllBytes(Fixture(file));
        IconTextureTestUtil.Fuzz(data, d => Icns.DecodeAll(d), variants: 25);
        IconTextureTestUtil.AssertRejects(() => Icns.DecodeAll(data.AsSpan(0, 7).ToArray()));
        IconTextureTestUtil.AssertRejects(() => Icns.DecodeAll("icns\0\0\0\u0010ic08\0\0\0\u0003"u8.ToArray()));
    }

    // ------------------------------------------------------------------ ANI

    public static IEnumerable<object[]> AniFixtures => Files("ani", ".ani");

    [Theory, MemberData(nameof(AniFixtures))]
    public void AniMatchesReference(string file)
    {
        var data = File.ReadAllBytes(Fixture(file));
        var lines = File.ReadAllLines(Fixture(file) + ".txt").Select(l => l.Split(' ').Select(int.Parse).ToArray()).ToArray();
        var a = Ani.DecodeAnimation(data);
        Assert.Equal(lines.Length, a.StepCount);
        for (int s = 0; s < lines.Length; s++)
        {
            var st = a.Steps[s];
            AssertEqual(LoadRef(Fixture(file) + $".{s}.ref"), st.Image);
            Assert.Equal(lines[s], new[] { st.Frame, st.Jiffies, st.HotspotX, st.HotspotY });
            Assert.Equal(st.Jiffies * 1000.0 / 60, st.DelayMs);
        }
        var all = ImageCodec.DecodeAll(data);
        Assert.Equal(lines.Length, all.Length);
        var info = ImageCodec.GetInfo(data);
        Assert.Equal((ImageFormat.Ani, lines.Length, true), (info.Format, info.FrameCount, info.IsAnimated));
        Assert.Equal(ImageFormat.Ani, ImageCodec.FormatFromExtension(file));
        AssertEqual(all[0], ImageCodec.Decode(data));
        Assert.Equal(info, ImageCodec.GetInfo(new MemoryStream(data)) with { Metadata = info.Metadata });
        var ai = Ani.GetAnimationInfo(data);
        Assert.Equal((a.FrameCount, a.StepCount, a.Title, a.Artist), (ai.FrameCount, ai.StepCount, ai.Title, ai.Artist));
        // every frame is an icon / cursor file of its own
        foreach (var f in a.FrameData) Assert.True(Ico.IsMatch(f));
    }

    [Fact]
    public void AniTexts()
    {
        var a = Ani.GetAnimationInfo(File.ReadAllBytes(Fixture("ani/seq.ani")));
        Assert.Equal(("Spinner", "gen_ani.py", 4, 7), (a.Title, a.Artist, a.FrameCount, a.StepCount));
        Assert.Equal(new[] { 0, 1, 2, 1, 3, 3, 0 }, a.Sequence);
        var p = Ani.GetAnimationInfo(File.ReadAllBytes(Fixture("ani/plain.ani")));
        Assert.Equal(("Odd", "abc", 12), (p.Title, p.Artist, p.DefaultJiffies));
    }

    [Fact]
    public void AniRoundTrip()
    {
        var f0 = Icon(32, 1);
        var f1 = Icon(32, 2);
        var f2 = Icon(48, 3);
        var steps = new[]
        {
            new AniInputFrame(f0, 5, 3, 4), new AniInputFrame(f1, 10, 16, 16), AniInputFrame.FromMilliseconds(f0, 100, 3, 4), new AniInputFrame(f2, 5),
        };
        var enc = Ani.Encode(steps, new AniEncodeOptions { Title = "t", Artist = "me" });
        var a = Ani.DecodeAnimation(enc);
        Assert.Equal((3, 4, "t", "me"), (a.FrameCount, a.StepCount, a.Title, a.Artist));
        Assert.Equal(new[] { 0, 1, 0, 2 }, a.Sequence);
        Assert.Equal(new[] { 5, 10, 6, 5 }, a.Steps.Select(s => s.Jiffies));
        Assert.Equal(new[] { (3, 4), (16, 16), (3, 4), (0, 0) }, a.Steps.Select(s => (s.HotspotX, s.HotspotY)));
        for (int s = 0; s < 4; s++) AssertEqual(steps[s].Image, a.Steps[s].Image);
        // the cursors inside: hotspots in the directory, and Pillow reads them
        Assert.Equal((16, 16), Ico.ReadEntries(a.FrameData[1]).Select(e => (e.HotspotX, e.HotspotY)).Single());
        if (OracleDecode(a.FrameData[1], ".cur") is { } o) AssertEqual(f1, o);
        // no shared frames and equal durations: neither "seq " nor "rate"
        var simple = Ani.Encode(new[] { new AniInputFrame(f0), new AniInputFrame(f1) }, new AniEncodeOptions { Cursor = false });
        Assert.DoesNotContain("seq ", System.Text.Encoding.Latin1.GetString(simple));
        Assert.DoesNotContain("rate", System.Text.Encoding.Latin1.GetString(simple));
        var s2 = Ani.DecodeAnimation(simple);
        Assert.Equal((2, 10), (s2.StepCount, s2.Steps[1].Jiffies));
        Assert.Equal(1, Ico.ReadEntries(s2.FrameData[0]).Count);
        Assert.Throws<ArgumentException>(() => Ani.Encode(Array.Empty<AniInputFrame>()));
        Assert.Throws<ArgumentException>(() => Ani.Encode(new[] { new AniInputFrame(f0, 0) }));
        // through ImageCodec
        AssertEqual(f0, ImageCodec.Decode(ImageCodec.Encode(f0, ImageFormat.Ani)));
    }

    [Theory, MemberData(nameof(AniFixtures))]
    public void AniRobustness(string file)
    {
        var data = File.ReadAllBytes(Fixture(file));
        IconTextureTestUtil.Fuzz(data, d => Ani.DecodeAnimation(d), variants: 40);
        IconTextureTestUtil.AssertRejects(() => Ani.DecodeAnimation(data.AsSpan(0, 40).ToArray()));
        // a step that shows a frame the file does not have
        var bad = (byte[])data.Clone();
        int seq = System.Text.Encoding.Latin1.GetString(bad).IndexOf("seq ");
        if (seq > 0)
        {
            bad[seq + 8] = 99;
            IconTextureTestUtil.AssertRejects(() => Ani.DecodeAnimation(bad));
        }
    }

    // ------------------------------------------------------------------ CUR

    [Fact]
    public void CurHotspotsRoundTrip()
    {
        var imgs = new[] { Icon(32, 7), Icon(16, 8) };
        var cur = Ico.Encode(imgs, new IcoEncodeOptions { Cursor = true, Hotspots = new[] { (31, 2), (5, 15) } });
        Assert.Equal(new[] { (31, 2), (5, 15) }, Ico.ReadEntries(cur).Select(e => (e.HotspotX, e.HotspotY)));
        var magick = Ico.ReadEntries(File.ReadAllBytes(Fixture("ico/magick.cur")));
        Assert.All(magick, e => Assert.True(e.HotspotX >= 0 && e.HotspotY >= 0));
    }
}
