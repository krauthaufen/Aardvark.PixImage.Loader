using System.Text.Json;
using PixCodec;
using PixCodec.Compression;
using Xunit;
using static PixCodec.Tests.PngTests;

namespace PixCodec.Tests;

/// <summary>Interlaced / low bit depth / palette / color key output and the PNG specific ancillary chunks.</summary>
public class PngFeatureTests
{
    /// <summary>What third-party readers report about a PNG we wrote (tests/oracle/gen_png.py --meta).</summary>
    internal static JsonElement OracleMeta(byte[] png)
    {
        var tmp = Path.Combine(TestUtil.FixtureDir, "_tmp");
        Directory.CreateDirectory(tmp);
        var f = Path.Combine(tmp, Guid.NewGuid().ToString("N") + ".png");
        try
        {
            File.WriteAllBytes(f, png);
            var (exit, output) = TestUtil.RunPython($"gen_png.py --meta \"{f}\"");
            Assert.True(exit == 0, "metadata oracle failed: " + output);
            return JsonDocument.Parse(output.Trim().Split('\n').Last(l => l.StartsWith('{'))).RootElement;
        }
        finally { File.Delete(f); }
    }

    static void AssertOracle(RawImage expected, byte[] png)
    {
        var oracle = TestUtil.OracleDecode(png, ".png");
        if (oracle != null) TestUtil.AssertEqual(expected, oracle);
    }

    // ------------------------------------------------------------------ interlaced output

    [Theory]
    [MemberData(nameof(PngTests.EncodeCases), MemberType = typeof(PngTests))]
    public void InterlacedEncodeRoundTrips(ColorModel color, SampleType type)
    {
        int seed = 300 + (int)color * 10 + (int)type;
        // sizes around the 8x8 Adam7 cell: some passes are empty for the small ones
        foreach (var (w, h) in new[] { (1, 1), (2, 1), (1, 3), (3, 2), (4, 4), (5, 7), (8, 8), (9, 17), (67, 21) })
        {
            var img = MakeImage(w, h, color, type, seed + w, noise: w % 2 == 0);
            foreach (var filter in new[] { PngFilterMode.Auto, PngFilterMode.None, PngFilterMode.Paeth, PngFilterMode.Average })
            {
                var png = Png.Encode(img, new PngEncodeOptions { Interlace = true, Filter = filter });
                Assert.Equal(1, png[28]); // interlace method
                TestUtil.AssertEqual(img, Png.Decode(png));
            }
        }
        // read by a third-party decoder
        var big = MakeImage(67, 21, color, type, seed);
        AssertOracle(big, Png.Encode(big, new PngEncodeOptions { Interlace = true }));
        var odd = MakeImage(5, 7, color, type, seed + 1, noise: true);
        AssertOracle(odd, Png.Encode(odd, new PngEncodeOptions { Interlace = true, Filter = PngFilterMode.Paeth, CompressionLevel = 1 }));
    }

    [Fact]
    public void InterlacedEncodeOfPackedFormats()
    {
        // palette and sub-byte gray: rows are packed per pass
        var rnd = new Random(3);
        foreach (int colors in new[] { 2, 4, 16, 200 })
            foreach (var (w, h) in new[] { (1, 1), (3, 5), (17, 9), (40, 23) })
            {
                var img = RawImage.Create(w, h, ColorModel.Rgb, SampleType.UInt8);
                var d = img.GetData<byte>();
                for (int p = 0; p < w * h; p++) { int c = p < colors ? p : rnd.Next(colors); d[p * 3] = (byte)c; d[p * 3 + 1] = (byte)(c * 7); d[p * 3 + 2] = (byte)(255 - c); }
                var png = Png.Encode(img, new PngEncodeOptions { Interlace = true, AutoPalette = true });
                Assert.Equal((3, 1), (png[25], png[28]));
                TestUtil.AssertEqual(img, Png.Decode(png));
                if (w == 40) AssertOracle(img, png);
            }
        foreach (int step in new[] { 255, 85, 17 })
        {
            var img = RawImage.Create(37, 11, ColorModel.Gray, SampleType.UInt8);
            var d = img.GetData<byte>();
            for (int i = 0; i < d.Length; i++) d[i] = (byte)(rnd.Next(255 / step + 1) * step);
            var png = Png.Encode(img, new PngEncodeOptions { Interlace = true, ReduceGrayDepth = true });
            Assert.Equal((step == 255 ? 1 : step == 85 ? 2 : 4, 0, 1), (png[24], png[25], png[28]));
            TestUtil.AssertEqual(img, Png.Decode(png));
            AssertOracle(img, png);
        }
    }

    // ------------------------------------------------------------------ low bit depth gray

    [Theory]
    [InlineData(255, 1)]
    [InlineData(85, 2)]
    [InlineData(17, 4)]
    [InlineData(51, 4)]   // multiples of 51 are multiples of 17 but not of 85
    [InlineData(1, 8)]
    [InlineData(5, 8)]
    public void ReduceGrayDepthPicksTheSmallestDepth(int step, int expectedDepth)
    {
        var rnd = new Random(step);
        foreach (var (w, h) in new[] { (1, 1), (7, 3), (8, 1), (33, 9), (64, 5) })
        {
            var img = RawImage.Create(w, h, ColorModel.Gray, SampleType.UInt8);
            var d = img.GetData<byte>();
            for (int i = 0; i < d.Length; i++) d[i] = (byte)(rnd.Next(255 / step + 1) * step);
            d[0] = (byte)step; // the value that decides the depth is always there
            if (w * h > 1) d[^1] = 0;
            var png = Png.Encode(img, new PngEncodeOptions { ReduceGrayDepth = true });
            Assert.Equal((expectedDepth, 0), (png[24], png[25]));
            var back = Png.Decode(png);
            Assert.Equal(ColorModel.Gray, back.Color);
            TestUtil.AssertEqual(img, back);
            if (expectedDepth < 8) Assert.True(png.Length <= Png.Encode(img).Length);
            if (w == 33) AssertOracle(img, png);
            // every filter works on packed rows too
            foreach (var filter in new[] { PngFilterMode.Sub, PngFilterMode.Up, PngFilterMode.Average, PngFilterMode.Paeth, PngFilterMode.Adaptive })
                TestUtil.AssertEqual(img, Png.Decode(Png.Encode(img, new PngEncodeOptions { ReduceGrayDepth = true, Filter = filter })));
        }
    }

    [Fact]
    public void ReduceGrayDepthLeavesOtherImagesAlone()
    {
        var opt = new PngEncodeOptions { ReduceGrayDepth = true };
        var bw16 = RawImage.Create(4, 4, ColorModel.Gray, SampleType.UInt16);
        Assert.Equal(16, Png.Encode(bw16, opt)[24]);
        var ga = RawImage.Create(4, 4, ColorModel.GrayAlpha, SampleType.UInt8);
        Assert.Equal((8, 4), (Png.Encode(ga, opt)[24], Png.Encode(ga, opt)[25]));
        var rgb = RawImage.Create(4, 4, ColorModel.Rgb, SampleType.UInt8);
        Assert.Equal((8, 2), (Png.Encode(rgb, opt)[24], Png.Encode(rgb, opt)[25]));
    }

    // ------------------------------------------------------------------ tRNS color key

    static RawImage Keyed(ColorModel color, SampleType type, int seed, int w = 31, int h = 13)
    {
        // opaque noise with a block of one transparent color
        var img = MakeImage(w, h, color, type, seed, noise: true);
        int ch = img.Channels;
        long max = type == SampleType.UInt8 ? 255 : 65535;
        void Set(int i, long v) { if (type == SampleType.UInt8) img.GetData<byte>()[i] = (byte)v; else img.GetData<ushort>()[i] = (ushort)v; }
        long Get(int i) => type == SampleType.UInt8 ? img.GetData<byte>()[i] : img.GetData<ushort>()[i];
        for (int p = 0; p < w * h; p++)
        {
            bool transparent = p % w is > 3 and < 12 && p / w is > 2 and < 8;
            Set(p * ch + ch - 1, transparent ? 0 : max);
            for (int c = 0; c < ch - 1; c++)
            {
                long key = max - 3 - c;
                if (transparent) Set(p * ch + c, key);
                else if (Get(p * ch + c) == key) Set(p * ch + c, key - 1); // keep the key color out of the opaque pixels
            }
        }
        return img;
    }

    [Theory]
    [InlineData(ColorModel.GrayAlpha, SampleType.UInt8)]
    [InlineData(ColorModel.GrayAlpha, SampleType.UInt16)]
    [InlineData(ColorModel.Rgba, SampleType.UInt8)]
    [InlineData(ColorModel.Rgba, SampleType.UInt16)]
    public void ColorKeyTransparencyRoundTrips(ColorModel color, SampleType type)
    {
        var opt = new PngEncodeOptions { ColorKeyTransparency = true };
        var img = Keyed(color, type, 7);
        foreach (bool interlace in new[] { false, true })
        {
            opt.Interlace = interlace;
            var png = Png.Encode(img, opt);
            Assert.Equal(color == ColorModel.Rgba ? 2 : 0, png[25]); // no alpha channel in the file
            var trns = Chunks(png).Single(c => c.type == "tRNS").bytes;
            Assert.Equal(12 + (color == ColorModel.Rgba ? 6 : 2), trns.Length);
            var back = Png.Decode(png);
            Assert.Equal(color, back.Color);
            TestUtil.AssertEqual(img, back);
            Assert.True(png.Length < Png.Encode(img, new PngEncodeOptions { Interlace = interlace }).Length);
            AssertOracle(img, png);
        }
        opt.Interlace = false;
        long max = type == SampleType.UInt8 ? 255 : 65535;
        void Set(RawImage i, int at, long v) { if (type == SampleType.UInt8) i.GetData<byte>()[at] = (byte)v; else i.GetData<ushort>()[at] = (ushort)v; }
        int ch = img.Channels, alphaType = color == ColorModel.Rgba ? 6 : 4;
        int transparentPixel = 3 * 31 + 5, opaquePixel = 0;

        // not expressible as a color key -> the alpha channel stays
        var partial = Keyed(color, type, 7);
        Set(partial, opaquePixel * ch + ch - 1, max / 2);                      // translucent pixel
        var twoColors = Keyed(color, type, 7);
        Set(twoColors, transparentPixel * ch, 1);                              // a second transparent color
        var clash = Keyed(color, type, 7);
        for (int c = 0; c < ch - 1; c++) Set(clash, opaquePixel * ch + c, max - 3 - c); // an opaque pixel in the key color
        var opaque = MakeImage(9, 9, color, type, 8);
        for (int p = 0; p < 81; p++) Set(opaque, p * ch + ch - 1, max);        // nothing transparent at all
        foreach (var other in new[] { partial, twoColors, clash, opaque })
        {
            var png = Png.Encode(other, opt);
            Assert.Equal(alphaType, png[25]);
            TestUtil.AssertEqual(other, Png.Decode(png));
        }
        // a fully transparent image has a key and nothing else
        var empty = RawImage.Create(5, 4, color, type);
        Assert.NotEqual(alphaType, Png.Encode(empty, opt)[25]);
        TestUtil.AssertEqual(empty, Png.Decode(Png.Encode(empty, opt)));
    }

    // ------------------------------------------------------------------ explicit palette

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(9)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(256)]
    public void EncodeIndexedRoundTrips(int count)
    {
        var rnd = new Random(count);
        var palette = new byte[count * 4];
        rnd.NextBytes(palette);
        // the last third is opaque: tRNS must stop before it
        for (int i = count - count / 3; i < count; i++) palette[i * 4 + 3] = 255;
        if (count > 2) palette[3] = 255; // an opaque entry in front of translucent ones
        foreach (var (w, h) in new[] { (1, 1), (13, 5), (64, 9) })
            foreach (bool interlace in new[] { false, true })
            {
                var indices = RawImage.Create(w, h, ColorModel.Gray, SampleType.UInt8);
                var d = indices.GetData<byte>();
                for (int i = 0; i < d.Length; i++) d[i] = (byte)(i < count ? i : rnd.Next(count));
                var png = Png.EncodeIndexed(indices, palette, new PngEncodeOptions { Interlace = interlace });
                Assert.Equal((count <= 2 ? 1 : count <= 4 ? 2 : count <= 16 ? 4 : 8, 3), (png[24], png[25]));

                // indices and palette come back exactly, in the given order
                TestUtil.AssertEqual(indices, Png.Decode(png, new PngDecodeOptions { KeepIndexed = true }));
                Assert.Equal(palette, Png.ReadPalette(png));
                int lastTranslucent = -1;
                for (int i = 0; i < count; i++) if (palette[i * 4 + 3] != 255) lastTranslucent = i;
                var trns = Chunks(png).SingleOrDefault(c => c.type == "tRNS").bytes;
                Assert.Equal(lastTranslucent < 0 ? null : (int?)(lastTranslucent + 1), trns == null ? null : trns.Length - 12);

                // the expanded image
                bool alpha = lastTranslucent >= 0;
                var full = Png.Decode(png);
                Assert.Equal(alpha ? ColorModel.Rgba : ColorModel.Rgb, full.Color);
                var f = full.GetData<byte>();
                for (int p = 0; p < d.Length; p++)
                    for (int c = 0; c < full.Channels; c++) Assert.Equal(palette[d[p] * 4 + c], f[p * full.Channels + c]);
                if (w == 64) AssertOracle(full, png);
            }
    }

    [Fact]
    public void EncodeIndexedPairsWithKeepIndexed()
    {
        // decode -> encode keeps a palette file intact
        foreach (var name in new[] { "m_c3_d01_n_33x17", "m_c3_d04_i_33x17", "t_pal_d2_n", "t_pal_d8_i", "pal_short", "pngsuite/tbbn3p08", "pngsuite/basn3p08" })
        {
            var source = Load($"png/{name}.png");
            var indices = Png.Decode(source, new PngDecodeOptions { KeepIndexed = true });
            var palette = Png.ReadPalette(source)!;
            var png = Png.EncodeIndexed(indices, palette);
            Assert.Equal(3, png[25]);
            TestUtil.AssertEqual(Png.Decode(source), Png.Decode(png));
            Assert.Equal(palette, Png.ReadPalette(png));
        }
    }

    [Fact]
    public void EncodeIndexedValidatesInput()
    {
        var indices = RawImage.Create(2, 2, ColorModel.Gray, SampleType.UInt8);
        indices.GetData<byte>()[3] = 2;
        Assert.Throws<ArgumentException>(() => Png.EncodeIndexed(indices, new byte[8]));          // index 2 with two entries
        Assert.Throws<ArgumentException>(() => Png.EncodeIndexed(indices, Array.Empty<byte>()));
        Assert.Throws<ArgumentException>(() => Png.EncodeIndexed(indices, new byte[13]));
        Assert.Throws<ArgumentException>(() => Png.EncodeIndexed(indices, new byte[257 * 4]));
        Assert.Throws<ArgumentException>(() => Png.EncodeIndexed(RawImage.Create(2, 2, ColorModel.Gray, SampleType.UInt16), new byte[12]));
        Assert.Throws<ArgumentException>(() => Png.EncodeIndexed(RawImage.Create(2, 2, ColorModel.Rgb, SampleType.UInt8), new byte[12]));
        Assert.Equal(3, Png.EncodeIndexed(indices, new byte[12])[25]);
    }

    // ------------------------------------------------------------------ ancillary chunks

    public static IEnumerable<object[]> MetadataFixtures()
    {
        foreach (var dir in new[] { "png", "png/pngsuite", "png/apng" })
            foreach (var f in Directory.GetFiles(Path.Combine(TestUtil.FixtureDir, dir), "*.png.meta.json").OrderBy(f => f, StringComparer.Ordinal))
                yield return new object[] { Path.Combine(dir, Path.GetFileName(f)[..^".meta.json".Length]) };
    }

    /// <summary>Compares what we read with what Pillow / ImageMagick / pngtopam report (see gen_png.py read_meta).</summary>
    static void AssertMatchesOracle(JsonElement oracle, byte[] png)
    {
        var meta = Png.ReadMetadata(png);
        var common = Png.Decode(png).Metadata;
        var header = Png.GetInfo(png);
        int depth = png[24], colorType = png[25];

        if (oracle.TryGetProperty("gamma", out var gamma)) Assert.Equal(gamma.GetDouble(), common!.Gamma, 9);
        else Assert.True(common == null || common.Gamma == 0);

        if (oracle.TryGetProperty("chromaticity", out var chroma))
        {
            var c = meta.Chromaticities!.Value;
            var mine = new[] { c.WhiteX, c.WhiteY, c.RedX, c.RedY, c.GreenX, c.GreenY, c.BlueX, c.BlueY };
            for (int i = 0; i < 8; i++) Assert.Equal(chroma[i].GetDouble(), mine[i], 9);
        }
        else Assert.Null(meta.Chromaticities);

        if (oracle.TryGetProperty("srgb", out var srgb)) Assert.Equal((PngRenderingIntent)srgb.GetInt32(), meta.SrgbIntent);
        else Assert.Null(meta.SrgbIntent);

        if (oracle.TryGetProperty("time", out var time))
            Assert.Equal(DateTime.Parse(time.GetString()!, null, System.Globalization.DateTimeStyles.AdjustToUniversal), meta.LastModified);
        else Assert.Null(meta.LastModified);

        if (oracle.TryGetProperty("text", out var text))
        {
            // Pillow keeps one entry per keyword (the last one)
            var mine = new Dictionary<string, PngTextEntry>();
            foreach (var e in meta.Text) mine[e.Keyword] = e;
            Assert.Equal(text.GetArrayLength(), mine.Count);
            foreach (var e in text.EnumerateArray())
            {
                var m = mine[e.GetProperty("keyword").GetString()!];
                Assert.Equal(e.GetProperty("text").GetString(), m.Text);
                Assert.Equal(e.GetProperty("itxt").GetBoolean(), m.International);
                Assert.Equal(e.GetProperty("lang").GetString(), m.LanguageTag);
                Assert.Equal(e.GetProperty("tkey").GetString(), m.TranslatedKeyword);
            }
        }
        else Assert.Empty(meta.Text);

        if (oracle.TryGetProperty("bkgd", out var bkgd))
        {
            // pngtopam prints the raw chunk values: {index, gray, red, green, blue}
            var b = meta.Background!.Value;
            if (colorType == 3) Assert.Equal(bkgd[0].GetInt32(), b.PaletteIndex);
            else if (colorType is 0 or 4)
            {
                int scale = depth < 8 ? 255 / ((1 << depth) - 1) : 1; // ours is in the scale of the decoded samples
                Assert.Equal((bkgd[1].GetInt32() * scale, bkgd[1].GetInt32() * scale, bkgd[1].GetInt32() * scale, -1), ((int)b.R, (int)b.G, (int)b.B, b.PaletteIndex));
            }
            else Assert.Equal((bkgd[2].GetInt32(), bkgd[3].GetInt32(), bkgd[4].GetInt32(), -1), ((int)b.R, (int)b.G, (int)b.B, b.PaletteIndex));
        }
        else Assert.Null(meta.Background);

        Assert.Equal(oracle.TryGetProperty("sbit", out _), meta.SignificantBits != null);
        if (meta.SignificantBits != null) Assert.Equal(colorType switch { 0 => 1, 4 => 2, 6 => 4, _ => 3 }, meta.SignificantBits.Length);
        _ = header;
    }

    [Fact]
    public void MetadataFixturesArePresent() => Assert.True(MetadataFixtures().Count() > 150);

    [Theory]
    [MemberData(nameof(MetadataFixtures))]
    public void ReadsMetadataLikeThirdPartyReaders(string rel)
        => AssertMatchesOracle(JsonDocument.Parse(File.ReadAllText(TestUtil.Fixture(rel) + ".meta.json")).RootElement, Load(rel));

    [Fact]
    public void ReadsKnownChunkValues()
    {
        var m = Png.ReadMetadata(Load("png/meta_png_chunks.png"));
        Assert.Equal(PngRenderingIntent.RelativeColorimetric, m.SrgbIntent);
        Assert.Equal(new PngChromaticities(0.3127, 0.329, 0.64, 0.33, 0.3, 0.6, 0.15, 0.06), m.Chromaticities);
        Assert.Equal(new byte[] { 5, 6, 5 }, m.SignificantBits);
        Assert.Equal(new PngBackground(255, 128, 3), m.Background);
        Assert.Equal(new DateTime(2024, 2, 29, 23, 59, 58, DateTimeKind.Utc), m.LastModified);
        Assert.Equal(DateTimeKind.Utc, m.LastModified!.Value.Kind);
        Assert.Equal(new[] { "Title", "Description", "Comment", "Warning", "Software" }, m.Text.Select(t => t.Keyword));
        Assert.Equal("Latin-1 text: café", m.Text[0].Text);
        Assert.Equal((false, false), (m.Text[0].International, m.Text[0].Compressed));
        Assert.Equal((false, true), (m.Text[1].International, m.Text[1].Compressed));
        Assert.Equal(1800, m.Text[1].Text.Length);
        Assert.Equal((true, false, "de-AT", "Kommentar"), (m.Text[2].International, m.Text[2].Compressed, m.Text[2].LanguageTag, m.Text[2].TranslatedKeyword));
        Assert.Equal("Grüße, 日本語, \U0001F600", m.Text[2].Text);
        Assert.Equal((true, true), (m.Text[3].International, m.Text[3].Compressed));
        Assert.EndsWith(" ✓", m.Text[3].Text);
        Assert.Equal("after the image data", m.Text[4].Text); // text behind IDAT is found too

        // low bit depth gray background is scaled like the pixels; palette background carries index and color
        Assert.Equal(new PngBackground(153, 153, 153), Png.ReadMetadata(Load("png/meta_bkgd_gray4.png")).Background);
        Assert.Equal(new byte[] { 3 }, Png.ReadMetadata(Load("png/meta_bkgd_gray4.png")).SignificantBits);
        var pal = Png.ReadPalette(Load("png/meta_bkgd_palette.png"))!;
        Assert.Equal(new PngBackground(pal[16], pal[17], pal[18], 4), Png.ReadMetadata(Load("png/meta_bkgd_palette.png")).Background);

        // PngSuite documents the significant bits: cs3n2c16 = 13 bits, cs3n3p08 = 3, cs5 = 5 (cs8, the reference, has no sBIT)
        foreach (var (name, bits) in new[] { ("cs3n2c16", 13), ("cs3n3p08", 3), ("cs5n2c08", 5), ("cs5n3p08", 5) })
            Assert.Equal(new[] { (byte)bits, (byte)bits, (byte)bits }, Png.ReadMetadata(Load($"png/pngsuite/{name}.png")).SignificantBits);

        Assert.True(Png.ReadMetadata(Load("png/m_c2_d08_n_13x9.png")).IsEmpty);
    }

    static PngMetadata FullMetadata() => new()
    {
        SrgbIntent = PngRenderingIntent.Saturation,
        Chromaticities = new PngChromaticities(0.3127, 0.329, 0.64, 0.33, 0.3, 0.6, 0.15, 0.06),
        SignificantBits = new byte[] { 5, 6, 5, 8 },
        Background = new PngBackground(17, 34, 51),
        LastModified = new DateTime(2031, 12, 31, 23, 59, 59, DateTimeKind.Utc),
        Text =
        {
            new PngTextEntry("Title", "plain Latin-1: äöü"),
            new PngTextEntry("Long", new string('x', 5000)) { Compressed = true },
            new PngTextEntry("Unicode", "日本語 \U0001F600"),                       // switches to iTXt by itself
            new PngTextEntry("Tagged", "bonjour") { LanguageTag = "fr-CA", TranslatedKeyword = "Étiqueté" },
            new PngTextEntry("Packed", string.Concat(Enumerable.Repeat("✓ ", 900))) { International = true, Compressed = true },
            new PngTextEntry("Empty", ""),
        },
    };

    [Theory]
    [MemberData(nameof(PngTests.EncodeCases), MemberType = typeof(PngTests))]
    public void WritesMetadata(ColorModel color, SampleType type)
    {
        var img = MakeImage(19, 11, color, type, 5);
        img.Metadata = new ImageMetadata { Gamma = 0.45455 };
        var meta = FullMetadata();
        if (type == SampleType.UInt16) meta.Background = new PngBackground(1000, 20000, 65535);
        bool gray = color is ColorModel.Gray or ColorModel.GrayAlpha;
        int channels = img.Channels;
        var png = Png.Encode(img, new PngEncodeOptions { Metadata = meta });
        TestUtil.AssertEqual(img, Png.Decode(png));

        var back = Png.ReadMetadata(png);
        Assert.Equal(meta.SrgbIntent, back.SrgbIntent);
        Assert.Equal(meta.Chromaticities, back.Chromaticities);
        Assert.Equal(meta.LastModified, back.LastModified);
        // sBIT follows the stored channels; with 4 entries given, 3 channel output drops the alpha entry
        if (channels is 4 or 3) Assert.Equal(meta.SignificantBits.AsSpan(0, channels).ToArray(), back.SignificantBits);
        else Assert.Null(back.SignificantBits);
        var bg = meta.Background!.Value;
        Assert.Equal(gray ? new PngBackground(bg.R, bg.R, bg.R) : bg, back.Background);
        Assert.Equal(meta.Text.Count, back.Text.Count);
        for (int i = 0; i < meta.Text.Count; i++)
        {
            Assert.Equal((meta.Text[i].Keyword, meta.Text[i].Text, meta.Text[i].LanguageTag, meta.Text[i].TranslatedKeyword, meta.Text[i].Compressed),
                         (back.Text[i].Keyword, back.Text[i].Text, back.Text[i].LanguageTag, back.Text[i].TranslatedKeyword, back.Text[i].Compressed));
        }
        Assert.Equal(new[] { false, false, true, true, true, false }, back.Text.Select(t => t.International));

        // chunk order: color space before PLTE/IDAT, everything before the pixel data
        Assert.Equal(new[] { "IHDR", "sRGB", "gAMA", "cHRM" }.Concat(channels >= 3 ? new[] { "sBIT" } : Array.Empty<string>())
                .Concat(new[] { "bKGD", "tIME", "tEXt", "zTXt", "iTXt", "iTXt", "iTXt", "tEXt", "IDAT", "IEND" }),
            Chunks(png).Select(c => c.type));

        // third-party readers see the same
        if (!TestUtil.PythonAvailable) return;
        AssertMatchesOracle(OracleMeta(png), png);
        AssertOracle(img, png);
    }

    [Fact]
    public void WritesMetadataForTransformedOutput()
    {
        var meta = new PngMetadata { SignificantBits = new byte[] { 4, 4, 4, 4 }, Background = new PngBackground(10, 20, 30) };
        // palette output: sBIT has 3 entries, bKGD is the index of the color (dropped when the palette lacks it)
        var img = RawImage.Create(6, 2, ColorModel.Rgba, SampleType.UInt8);
        var d = img.GetData<byte>();
        for (int p = 0; p < 12; p++) { d[p * 4] = (byte)(p % 3 == 0 ? 10 : 200); d[p * 4 + 1] = (byte)(p % 3 == 0 ? 20 : p); d[p * 4 + 2] = (byte)(p % 3 == 0 ? 30 : 7); d[p * 4 + 3] = 255; }
        var png = Png.Encode(img, new PngEncodeOptions { AutoPalette = true, Metadata = meta });
        Assert.Equal(3, png[25]);
        var back = Png.ReadMetadata(png);
        Assert.Equal(new byte[] { 4, 4, 4 }, back.SignificantBits);
        Assert.Equal((10, 20, 30), ((int)back.Background!.Value.R, (int)back.Background.Value.G, (int)back.Background.Value.B));
        Assert.InRange(back.Background.Value.PaletteIndex, 0, 255);
        meta.Background = new PngBackground(1, 2, 3);
        Assert.Null(Png.ReadMetadata(Png.Encode(img, new PngEncodeOptions { AutoPalette = true, Metadata = meta })).Background);
        if (TestUtil.PythonAvailable) AssertMatchesOracle(OracleMeta(png), png);

        // explicit palette: the caller's index wins
        var indices = RawImage.Create(2, 2, ColorModel.Gray, SampleType.UInt8);
        var explicitPalette = new byte[] { 1, 1, 1, 255, 9, 9, 9, 255, 5, 5, 5, 255 };
        png = Png.EncodeIndexed(indices, explicitPalette, new PngEncodeOptions { Metadata = new PngMetadata { Background = new PngBackground(0, 0, 0, 2) } });
        Assert.Equal(new PngBackground(5, 5, 5, 2), Png.ReadMetadata(png).Background);

        // reduced gray: bKGD and sBIT are expressed at the stored depth
        var gray = RawImage.Create(4, 1, ColorModel.Gray, SampleType.UInt8);
        gray.GetData<byte>()[1] = 85; gray.GetData<byte>()[2] = 170; gray.GetData<byte>()[3] = 255;
        png = Png.Encode(gray, new PngEncodeOptions { ReduceGrayDepth = true, Metadata = new PngMetadata { Background = new PngBackground(170, 170, 170), SignificantBits = new byte[] { 8 } } });
        Assert.Equal(2, png[24]);
        Assert.Equal(new PngBackground(170, 170, 170), Png.ReadMetadata(png).Background);
        Assert.Equal(new byte[] { 2 }, Png.ReadMetadata(png).SignificantBits);
        if (TestUtil.PythonAvailable) AssertMatchesOracle(OracleMeta(png), png);

        // color key output keeps the opaque channels' entries
        var keyed = Keyed(ColorModel.Rgba, SampleType.UInt8, 3);
        png = Png.Encode(keyed, new PngEncodeOptions { ColorKeyTransparency = true, Metadata = new PngMetadata { SignificantBits = new byte[] { 8, 7, 6, 1 } } });
        Assert.Equal(new byte[] { 8, 7, 6 }, Png.ReadMetadata(png).SignificantBits);

        // sRGB yields to an embedded profile
        var rgb = MakeImage(4, 4, ColorModel.Rgb, SampleType.UInt8, 1);
        rgb.Metadata = new ImageMetadata { IccProfile = new byte[200] };
        png = Png.Encode(rgb, new PngEncodeOptions { Metadata = new PngMetadata { SrgbIntent = PngRenderingIntent.Perceptual } });
        Assert.Null(Png.ReadMetadata(png).SrgbIntent);
        Assert.NotNull(Png.Decode(png).Metadata!.IccProfile);
    }

    [Fact]
    public void RejectsInvalidText()
    {
        var img = RawImage.Create(1, 1, ColorModel.Gray, SampleType.UInt8);
        byte[] With(PngTextEntry e) => Png.Encode(img, new PngEncodeOptions { Metadata = new PngMetadata { Text = { e } } });
        foreach (var keyword in new[] { "", " lead", "trail ", "two  spaces", new string('k', 80), "ctrl\u0001", "uni✓" })
            Assert.Throws<ArgumentException>(() => With(new PngTextEntry(keyword, "x")));
        Assert.Throws<ArgumentException>(() => With(new PngTextEntry("Key", "nul\0inside")));
        Assert.Throws<ArgumentException>(() => With(new PngTextEntry("Key", "x") { LanguageTag = "not a tag" }));
        Assert.Equal("x", Assert.Single(Png.ReadMetadata(With(new PngTextEntry(new string('k', 79), "x"))).Text).Text);
    }

    [Fact]
    public void MalformedAncillaryChunksAreSkipped()
    {
        var good = Chunks(Load("png/meta_png_chunks.png"));
        byte[] Replace(string type, byte[] body) => Assemble(good.Select(c => c.type == type ? Chunk(type, body) : c.bytes));
        var reference = Png.Decode(Assemble(good.Select(c => c.bytes)));

        Assert.Null(Png.ReadMetadata(Replace("sRGB", new byte[] { 9 })).SrgbIntent);
        Assert.Null(Png.ReadMetadata(Replace("cHRM", new byte[31])).Chromaticities);
        Assert.Null(Png.ReadMetadata(Replace("sBIT", new byte[] { 5, 6 })).SignificantBits);
        Assert.Null(Png.ReadMetadata(Replace("bKGD", new byte[] { 1 })).Background);
        Assert.Null(Png.ReadMetadata(Replace("tIME", new byte[] { 7, 232, 13, 1, 0, 0, 0 })).LastModified);   // month 13
        Assert.Null(Png.ReadMetadata(Replace("tIME", new byte[] { 7, 231, 2, 29, 0, 0, 0 })).LastModified);   // 29 Feb 2023
        // corrupt compressed text, missing terminators
        var m = Png.ReadMetadata(Replace("zTXt", "Description\0\0not zlib"u8.ToArray()));
        Assert.DoesNotContain(m.Text, t => t.Keyword == "Description");
        Assert.Equal(4, m.Text.Count);
        Assert.Equal(3, Png.ReadMetadata(Replace("iTXt", "Comment\0\0\0no terminators"u8.ToArray())).Text.Count);
        Assert.Equal(3, Png.ReadMetadata(Replace("tEXt", "no terminator at all"u8.ToArray())).Text.Count);   // both tEXt chunks
        // a text chunk with a wrong CRC is dropped, the rest survives; pixels are never affected
        var bad = good.Select(c => (byte[])c.bytes.Clone()).ToList();
        bad[good.FindIndex(c => c.type == "tEXt")][^1] ^= 1;
        Assert.Equal(4, Png.ReadMetadata(Assemble(bad)).Text.Count);
        TestUtil.AssertEqual(reference, Png.Decode(Assemble(bad)));

        // a compressed text that inflates far beyond any sane size is skipped, not allocated
        var bomb = Zlib.Compress(new byte[80 << 20], 9);
        long before = GC.GetTotalMemory(false);
        var meta = Png.ReadMetadata(Replace("zTXt", "Description\0\0"u8.ToArray().Concat(bomb).ToArray()));
        Assert.DoesNotContain(meta.Text, t => t.Keyword == "Description");
        _ = before;
    }
}
