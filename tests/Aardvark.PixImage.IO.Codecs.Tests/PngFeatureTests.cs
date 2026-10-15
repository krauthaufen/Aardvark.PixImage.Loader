using System.Text.Json;
using Aardvark.ImageCodecs;
using Aardvark.ImageCodecs.Compression;
using Xunit;
using static Aardvark.ImageCodecs.Tests.PngTests;

namespace Aardvark.ImageCodecs.Tests;

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

    /// <summary>
    /// Decodes a PNG we wrote with the third-party decoders of tests/oracle/decode_to_ref.py. Returns null when
    /// python is unavailable, and also when the oracle exits with status 77: that means the decoders installed on
    /// this machine cannot verify the file (some need tools that `gen_png.py --fetch-tools` installs).
    /// </summary>
    internal static RawImage? OracleDecode(byte[] encoded, string extension)
    {
        if (!TestUtil.PythonAvailable) return null;
        var tmp = Path.Combine(TestUtil.FixtureDir, "_tmp");
        Directory.CreateDirectory(tmp);
        var f = Path.Combine(tmp, Guid.NewGuid().ToString("N") + extension);
        try
        {
            File.WriteAllBytes(f, encoded);
            var (exit, output) = TestUtil.RunPython($"decode_to_ref.py \"{f}\" \"{f}.ref\"");
            if (exit == 77) return null;
            Assert.True(exit == 0, "oracle decode failed: " + output);
            return TestUtil.LoadRef(f + ".ref");
        }
        finally
        {
            File.Delete(f); File.Delete(f + ".ref");
        }
    }

    static void AssertOracle(RawImage expected, byte[] png)
    {
        var oracle = PngFeatureTests.OracleDecode(png, ".png");
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
            // packing must not cost more than a few bytes even on tiny images, where the Huffman table for
            // packed pairs can outweigh the halved row length
            if (expectedDepth < 8) { int full = Png.Encode(img).Length; Assert.True(png.Length <= full + 16, $"{w}x{h}: {png.Length} > {full}"); }
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
        foreach (var name in new[] { "m_c3_d01_n_33x17", "m_c3_d04_i_33x17", "t_pal_d2_n", "t_pal_d8_i", "pal_short" })
            EncodeIndexedPair(name);
    }

    [TestDataFact]
    public void EncodeIndexedPairsWithKeepIndexedSuite()
    {
        foreach (var name in new[] { "pngsuite/tbbn3p08", "pngsuite/basn3p08" })
            EncodeIndexedPair(name);
    }

    static void EncodeIndexedPair(string name)
    {
        var source = Load($"png/{name}.png");
        var indices = Png.Decode(source, new PngDecodeOptions { KeepIndexed = true });
        var palette = Png.ReadPalette(source)!;
        var png = Png.EncodeIndexed(indices, palette);
        Assert.Equal(3, png[25]);
        TestUtil.AssertEqual(Png.Decode(source), Png.Decode(png));
        Assert.Equal(palette, Png.ReadPalette(png));
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

    public static IEnumerable<object[]> MetadataFixtures() => MetadataFiles(TestUtil.FixtureDir, "png", "png/apng");

    /// <summary>The PngSuite files with metadata sidecars (in the test-data repository).</summary>
    public static IEnumerable<object[]> SuiteMetadataFixtures()
        => TestUtil.TestDataFixtureDir == null ? Enumerable.Empty<object[]>() : MetadataFiles(TestUtil.TestDataFixtureDir, "png/pngsuite");

    static IEnumerable<object[]> MetadataFiles(string fixtures, params string[] dirs)
    {
        foreach (var dir in dirs)
            foreach (var f in Directory.GetFiles(Path.Combine(fixtures, dir), "*.png.meta.json").OrderBy(f => f, StringComparer.Ordinal))
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

        // sBIT: values from pngcheck and exiftool (only "present" where neither tool was available)
        if (oracle.TryGetProperty("sbit", out var sbit))
        {
            Assert.NotNull(meta.SignificantBits);
            Assert.Equal(colorType switch { 0 => 1, 4 => 2, 6 => 4, _ => 3 }, meta.SignificantBits!.Length);
            if (sbit.ValueKind == JsonValueKind.Array) Assert.Equal(sbit.EnumerateArray().Select(v => (byte)v.GetInt32()), meta.SignificantBits);
        }
        else Assert.Null(meta.SignificantBits);
        _ = header;

        // the rarer chunks, as pngcheck prints them (sidecars are always written with it; a live check may lack it)
        bool pngcheck = !oracle.TryGetProperty("tools", out var tools) || tools.EnumerateArray().Any(t => t.GetString() == "pngcheck");
        bool exiftool = !oracle.TryGetProperty("tools", out _) || tools.EnumerateArray().Any(t => t.GetString() == "exiftool");
        if (oracle.TryGetProperty("hist", out var hist)) Assert.Equal(hist.EnumerateArray().Select(v => (ushort)v.GetInt32()), meta.Histogram);
        else if (pngcheck) Assert.Null(meta.Histogram);

        if (oracle.TryGetProperty("splt", out var splt))
        {
            Assert.Equal(splt.GetArrayLength(), meta.SuggestedPalettes.Count);
            int k = 0;
            foreach (var e in splt.EnumerateArray())
            {
                var mine = meta.SuggestedPalettes[k++];
                // pngcheck prints the name unpadded; ours is exact
                Assert.Equal(e.GetProperty("name").GetString(), mine.Name);
                Assert.Equal(e.GetProperty("depth").GetInt32(), mine.SampleDepth);
                Assert.Equal(e.GetProperty("entries").EnumerateArray().Select(v => (v[0].GetInt32(), v[1].GetInt32(), v[2].GetInt32(), v[3].GetInt32(), v[4].GetInt32())),
                             mine.Entries.Select(v => ((int)v.R, (int)v.G, (int)v.B, (int)v.A, (int)v.Frequency)));
            }
        }
        else if (pngcheck) Assert.Empty(meta.SuggestedPalettes);

        if (oracle.TryGetProperty("offs", out var offs))
        {
            // pngcheck prints the signed positions as unsigned numbers
            var o = meta.Offset!.Value;
            Assert.Equal((offs[0].GetUInt32(), offs[1].GetUInt32(), offs[2].GetString() == "micrometers" ? PngOffsetUnit.Micrometer : PngOffsetUnit.Pixel),
                         ((uint)o.X, (uint)o.Y, o.Unit));
        }
        else if (pngcheck) Assert.Null(meta.Offset);

        static double Num(string s) => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
        if (oracle.TryGetProperty("pcal", out var pcal))
        {
            var c = meta.PixelCalibration!;
            Assert.Equal((pcal.GetProperty("name").GetString(), pcal.GetProperty("unit").GetString(), unchecked((int)pcal.GetProperty("x0").GetInt64()), unchecked((int)pcal.GetProperty("x1").GetInt64()), pcal.GetProperty("type").GetInt32()),   // pngcheck prints them unsigned
                         (c.Name, c.Unit, c.X0, c.X1, (int)c.Equation));
            Assert.Equal(pcal.GetProperty("params").EnumerateArray().Select(v => Num(v.GetString()!)), c.Parameters);
        }
        else if (pngcheck) Assert.Null(meta.PixelCalibration);

        if (oracle.TryGetProperty("scal", out var scal))
        {
            var c = meta.PhysicalScale!.Value;
            Assert.Equal((scal[0].GetString() == "meters" ? PngScaleUnit.Meter : PngScaleUnit.Radian, Num(scal[1].GetString()!), Num(scal[2].GetString()!)),
                         (c.Unit, c.PixelWidth, c.PixelHeight));
        }
        else if (pngcheck) Assert.Null(meta.PhysicalScale);

        if (oracle.TryGetProperty("ster", out var ster)) Assert.Equal((PngStereoMode)ster.GetInt32(), meta.StereoMode);
        else if (pngcheck || exiftool) Assert.Null(meta.StereoMode);

        if (oracle.TryGetProperty("cicp", out var cicp))
            Assert.Equal(new PngCodingIndependentCodePoints((byte)cicp[0].GetInt32(), (byte)cicp[1].GetInt32(), (byte)cicp[2].GetInt32(), cicp[3].GetInt32() == 1),
                         meta.CodingIndependentCodePoints);
        else if (exiftool) Assert.Null(meta.CodingIndependentCodePoints);

        // mDCV / cLLI: pngcheck prints six significant digits. Their absence is not asserted, because we also read
        // the draft spellings (mDCv, cLLi), which pngcheck rejects.
        if (oracle.TryGetProperty("mdcv", out var mdcv))
        {
            var d = meta.MasteringDisplay!.Value;
            var mine = new[] { d.RedX, d.RedY, d.GreenX, d.GreenY, d.BlueX, d.BlueY, d.WhiteX, d.WhiteY, d.MaxLuminance, d.MinLuminance };
            for (int i = 0; i < 10; i++) Assert.Equal(mdcv[i].GetDouble(), mine[i], 5 - (int)Math.Max(0, Math.Ceiling(Math.Log10(Math.Max(mine[i], 1)))));
        }
        if (oracle.TryGetProperty("clli", out var clli))
        {
            var l = meta.ContentLightLevel!.Value;
            Assert.Equal(clli[0].GetDouble(), l.MaxContentLightLevel, 2);
            Assert.Equal(clli[1].GetDouble(), l.MaxFrameAverageLightLevel, 2);
        }
    }

    [Fact]
    public void MetadataFixturesArePresent() => Assert.True(MetadataFixtures().Count() > 40);

    [TestDataFact]
    public void SuiteMetadataFixturesArePresent() => Assert.True(SuiteMetadataFixtures().Count() > 140);

    [Theory]
    [MemberData(nameof(MetadataFixtures))]
    public void ReadsMetadataLikeThirdPartyReaders(string rel) => ReadsMetadataLikeOracle(rel);

    [TestDataTheory]
    [MemberData(nameof(SuiteMetadataFixtures))]
    public void ReadsSuiteMetadataLikeThirdPartyReaders(string rel) => ReadsMetadataLikeOracle(rel);

    static void ReadsMetadataLikeOracle(string rel)
        => AssertMatchesOracle(JsonDocument.Parse(File.ReadAllText(TestUtil.Fixture(rel) + ".meta.json")).RootElement, Load(rel));

    [TestDataFact]
    public void ReadsSuiteSignificantBits()
    {
        // PngSuite documents the significant bits: cs3n2c16 = 13 bits, cs3n3p08 = 3, cs5 = 5 (cs8, the reference, has no sBIT)
        foreach (var (name, bits) in new[] { ("cs3n2c16", 13), ("cs3n3p08", 3), ("cs5n2c08", 5), ("cs5n3p08", 5) })
            Assert.Equal(new[] { (byte)bits, (byte)bits, (byte)bits }, Png.ReadMetadata(Load($"png/pngsuite/{name}.png")).SignificantBits);
    }

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

        Assert.True(Png.ReadMetadata(Load("png/m_c2_d08_n_13x9.png")).IsEmpty);

        // the rare chunks, exactly as the generator stored them
        var r = Png.ReadMetadata(Load("png/meta_rare_chunks.png"));
        Assert.Equal(new PngCodingIndependentCodePoints(9, 16, 0, true), r.CodingIndependentCodePoints);
        var md = r.MasteringDisplay!.Value;
        Assert.Equal((0.708, 0.292, 0.17, 0.797, 0.131, 0.046, 0.3127, 0.329, 1000.0, 0.005),
                     (Math.Round(md.RedX, 5), Math.Round(md.RedY, 5), Math.Round(md.GreenX, 5), Math.Round(md.GreenY, 5), Math.Round(md.BlueX, 5),
                      Math.Round(md.BlueY, 5), Math.Round(md.WhiteX, 5), Math.Round(md.WhiteY, 5), Math.Round(md.MaxLuminance, 4), Math.Round(md.MinLuminance, 4)));
        Assert.Equal((1000.0, 250.0), (Math.Round(r.ContentLightLevel!.Value.MaxContentLightLevel, 4), Math.Round(r.ContentLightLevel.Value.MaxFrameAverageLightLevel, 4)));
        Assert.Equal(new PngOffset(-1234, 56789, PngOffsetUnit.Micrometer), r.Offset);
        Assert.Equal(new PngPhysicalScale(PngScaleUnit.Meter, 0.00025, 0.00025), r.PhysicalScale);
        Assert.Equal(PngStereoMode.DivergingFuse, r.StereoMode);
        Assert.Equal(("elevation", 0, 65535, PngCalibrationEquation.Linear, "m"), (r.PixelCalibration!.Name, r.PixelCalibration.X0, r.PixelCalibration.X1, r.PixelCalibration.Equation, r.PixelCalibration.Unit));
        Assert.Equal(new[] { -12.5, 0.001953125 }, r.PixelCalibration.Parameters);
        Assert.Equal(new[] { ("web safe", 8, 6), ("deep", 16, 4) }, r.SuggestedPalettes.Select(p => (p.Name, p.SampleDepth, p.Entries.Length)));
        Assert.Equal(new PngSuggestedPaletteEntry(51, 204, 7, 254, 999), r.SuggestedPalettes[0].Entries[1]);
        Assert.Equal(new PngSuggestedPaletteEntry(27000, 38535, 369, 65535, 3), r.SuggestedPalettes[1].Entries[3]);

        var h = Png.ReadMetadata(Load("png/meta_rare_pcal_hyperbolic.png"));
        Assert.Equal(("density", 10, 250, PngCalibrationEquation.Hyperbolic, "g/cm\u00b3"), (h.PixelCalibration!.Name, h.PixelCalibration.X0, h.PixelCalibration.X1, h.PixelCalibration.Equation, h.PixelCalibration.Unit));
        Assert.Equal(new[] { 1.0, -25.0, 0.125, 4.0 }, h.PixelCalibration.Parameters);
        Assert.Equal(new PngOffset(7, -8), h.Offset);
        Assert.Equal(PngStereoMode.CrossFuse, h.StereoMode);
        Assert.Equal(new PngPhysicalScale(PngScaleUnit.Radian, 0.001, 0.002), h.PhysicalScale);
        // the draft spellings mDCv / cLLi are still read
        Assert.Equal(new PngMasteringDisplay(0, 0, 0, 0, 0, 0, 0, 0, 0, 0), h.MasteringDisplay);
        Assert.Equal(0.5, h.ContentLightLevel!.Value.MaxContentLightLevel, 9);

        Assert.Equal(new ushort[] { 5, 0, 65535, 100, 7, 7, 1, 30000 }, Png.ReadMetadata(Load("png/meta_hist_palette.png")).Histogram);
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
        CodingIndependentCodePoints = new PngCodingIndependentCodePoints(9, 18, 0, true),
        MasteringDisplay = new PngMasteringDisplay(0.68, 0.32, 0.265, 0.69, 0.15, 0.06, 0.3127, 0.329, 4000, 0.0005),
        ContentLightLevel = new PngContentLightLevel(1200.5, 300.25),
        Offset = new PngOffset(-40, 1000000, PngOffsetUnit.Micrometer),
        PixelCalibration = new PngPixelCalibration("depth", -5, 4095, PngCalibrationEquation.ArbitraryBaseExponential, "\u00b5m", new[] { 0.5, 1e-7, 10.0 }),
        PhysicalScale = new PngPhysicalScale(PngScaleUnit.Radian, 1.25e-5, 3.0),
        StereoMode = PngStereoMode.CrossFuse,
        SuggestedPalettes =
        {
            new PngSuggestedPalette("eight", 8, new[] { new PngSuggestedPaletteEntry(1, 2, 3, 255, 9), new PngSuggestedPaletteEntry(255, 0, 128, 0, 65535) }),
            new PngSuggestedPalette("sixteen bit", 16, new[] { new PngSuggestedPaletteEntry(65535, 256, 3, 40000, 1) }),
            new PngSuggestedPalette("empty", 8, Array.Empty<PngSuggestedPaletteEntry>()),
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
        Assert.Equal(meta.CodingIndependentCodePoints, back.CodingIndependentCodePoints);
        Assert.Equal(meta.Offset, back.Offset);
        Assert.Equal(meta.PhysicalScale, back.PhysicalScale);
        Assert.Equal(meta.StereoMode, back.StereoMode);
        Assert.Equal(meta.ContentLightLevel, back.ContentLightLevel);
        var (m1, m2) = (meta.MasteringDisplay!.Value, back.MasteringDisplay!.Value);
        Assert.Equal((m1.RedX, m1.GreenY, m1.WhiteX, m1.MaxLuminance, m1.MinLuminance),
                     (Math.Round(m2.RedX, 9), Math.Round(m2.GreenY, 9), Math.Round(m2.WhiteX, 9), Math.Round(m2.MaxLuminance, 9), Math.Round(m2.MinLuminance, 9)));
        Assert.Equal((meta.PixelCalibration!.Name, meta.PixelCalibration.X0, meta.PixelCalibration.X1, meta.PixelCalibration.Equation, meta.PixelCalibration.Unit),
                     (back.PixelCalibration!.Name, back.PixelCalibration.X0, back.PixelCalibration.X1, back.PixelCalibration.Equation, back.PixelCalibration.Unit));
        Assert.Equal(meta.PixelCalibration.Parameters, back.PixelCalibration.Parameters);
        Assert.Equal(meta.SuggestedPalettes.Select(p => (p.Name, p.SampleDepth)), back.SuggestedPalettes.Select(p => (p.Name, p.SampleDepth)));
        for (int i = 0; i < meta.SuggestedPalettes.Count; i++) Assert.Equal(meta.SuggestedPalettes[i].Entries, back.SuggestedPalettes[i].Entries);
        Assert.Null(back.Histogram); // only written next to an explicit palette

        // chunk order: color space before PLTE/IDAT, everything before the pixel data
        Assert.Equal(new[] { "IHDR", "sRGB", "gAMA", "cHRM" }.Concat(channels >= 3 ? new[] { "sBIT" } : Array.Empty<string>())
                .Concat(new[] { "cICP", "mDCV", "cLLI", "bKGD", "sPLT", "sPLT", "sPLT", "oFFs", "pCAL", "sCAL", "sTER",
                                "tIME", "tEXt", "zTXt", "iTXt", "iTXt", "iTXt", "tEXt", "IDAT", "IEND" }),
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
    public void WritesHistogramWithExplicitPalette()
    {
        var indices = RawImage.Create(5, 3, ColorModel.Gray, SampleType.UInt8);
        for (int i = 0; i < 15; i++) indices.GetData<byte>()[i] = (byte)(i % 3);
        var palette = new byte[] { 255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255 };
        var meta = new PngMetadata { Histogram = new ushort[] { 5, 65535, 0 } };
        var png = Png.EncodeIndexed(indices, palette, new PngEncodeOptions { Metadata = meta });
        Assert.Equal(new[] { "IHDR", "PLTE", "hIST", "IDAT", "IEND" }, Chunks(png).Select(c => c.type));
        Assert.Equal(meta.Histogram, Png.ReadMetadata(png).Histogram);
        if (TestUtil.PythonAvailable) AssertMatchesOracle(OracleMeta(png), png);

        // wrong number of entries, or a palette the encoder derived itself: no chunk
        meta.Histogram = new ushort[] { 1, 2 };
        Assert.Null(Png.ReadMetadata(Png.EncodeIndexed(indices, palette, new PngEncodeOptions { Metadata = meta })).Histogram);
        var rgb = RawImage.Create(2, 1, ColorModel.Rgb, SampleType.UInt8);
        rgb.GetData<byte>()[0] = 9;
        var auto = Png.Encode(rgb, new PngEncodeOptions { AutoPalette = true, Metadata = new PngMetadata { Histogram = new ushort[] { 1, 1 } } });
        Assert.Equal(3, auto[25]);
        Assert.Null(Png.ReadMetadata(auto).Histogram);
    }

    [Fact]
    public void RejectsInvalidRareChunkValues()
    {
        var img = RawImage.Create(1, 1, ColorModel.Gray, SampleType.UInt8);
        void Bad<T>(Action<PngMetadata> set) where T : Exception
        {
            var m = new PngMetadata();
            set(m);
            Assert.Throws<T>(() => Png.Encode(img, new PngEncodeOptions { Metadata = m }));
        }
        Bad<ArgumentException>(m => m.SuggestedPalettes.Add(new PngSuggestedPalette("", 8, Array.Empty<PngSuggestedPaletteEntry>())));
        Bad<ArgumentException>(m => m.SuggestedPalettes.Add(new PngSuggestedPalette("p", 12, Array.Empty<PngSuggestedPaletteEntry>())));
        Bad<ArgumentException>(m => m.SuggestedPalettes.Add(new PngSuggestedPalette("p", 8, new[] { new PngSuggestedPaletteEntry(256, 0, 0, 0, 0) })));
        Bad<ArgumentException>(m => m.PixelCalibration = new PngPixelCalibration("c", 0, 1, PngCalibrationEquation.Linear, "m", new[] { 1.0 }));
        Bad<ArgumentException>(m => m.PixelCalibration = new PngPixelCalibration("c", 3, 3, PngCalibrationEquation.Linear, "m", new[] { 1.0, 2.0 }));
        Bad<ArgumentException>(m => m.PixelCalibration = new PngPixelCalibration(" c", 0, 1, PngCalibrationEquation.Linear, "m", new[] { 1.0, 2.0 }));
        Bad<ArgumentOutOfRangeException>(m => m.PixelCalibration = new PngPixelCalibration("c", 0, 1, PngCalibrationEquation.Linear, "m", new[] { 1.0, double.NaN }));
        Bad<ArgumentOutOfRangeException>(m => m.PhysicalScale = new PngPhysicalScale(PngScaleUnit.Meter, 0, 1));
        Bad<ArgumentOutOfRangeException>(m => m.PhysicalScale = new PngPhysicalScale((PngScaleUnit)0, 1, 1));
        Bad<ArgumentOutOfRangeException>(m => m.MasteringDisplay = new PngMasteringDisplay(2, 0, 0, 0, 0, 0, 0, 0, 1, 0));
        Bad<ArgumentOutOfRangeException>(m => m.ContentLightLevel = new PngContentLightLevel(-1, 0));
        Bad<ArgumentOutOfRangeException>(m => m.StereoMode = (PngStereoMode)2);
        Bad<ArgumentOutOfRangeException>(m => m.Offset = new PngOffset(0, 0, (PngOffsetUnit)2));
    }

    [Fact]
    public void MalformedRareChunksAreSkipped()
    {
        var good = Chunks(Load("png/meta_rare_chunks.png"));
        byte[] Replace(string type, byte[] body) => Assemble(good.Select(c => c.type == type ? Chunk(type, body) : c.bytes));
        Assert.Null(Png.ReadMetadata(Replace("cICP", new byte[] { 9, 16, 0, 2 })).CodingIndependentCodePoints);
        Assert.Null(Png.ReadMetadata(Replace("mDCV", new byte[23])).MasteringDisplay);
        Assert.Null(Png.ReadMetadata(Replace("cLLI", new byte[9])).ContentLightLevel);
        Assert.Null(Png.ReadMetadata(Replace("oFFs", new byte[] { 0, 0, 0, 1, 0, 0, 0, 1, 2 })).Offset);
        Assert.Null(Png.ReadMetadata(Replace("sTER", new byte[] { 2 })).StereoMode);
        Assert.Null(Png.ReadMetadata(Replace("sCAL", "\u0001abc\0001"u8.ToArray())).PhysicalScale);
        Assert.Null(Png.ReadMetadata(Replace("sCAL", new byte[] { 3, 49, 0, 49 })).PhysicalScale);          // unit 3
        Assert.Null(Png.ReadMetadata(Replace("sCAL", new byte[] { 1, 49, 0, 48 })).PhysicalScale);          // zero height
        Assert.Null(Png.ReadMetadata(Replace("pCAL", "name only"u8.ToArray())).PixelCalibration);
        Assert.Null(Png.ReadMetadata(Replace("pCAL", new byte[] { 99, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 3, 109, 0, 49, 0, 50, 0, 51 })).PixelCalibration); // 3 parameters for a linear equation
        Assert.Empty(Png.ReadMetadata(Replace("sPLT", "name\0\u0008\u0001\u0002"u8.ToArray())).SuggestedPalettes);  // partial entry
        Assert.Empty(Png.ReadMetadata(Replace("sPLT", "name\0\u0007"u8.ToArray())).SuggestedPalettes);              // depth 7
        var hist = Chunks(Load("png/meta_hist_palette.png"));
        Assert.Null(Png.ReadMetadata(Assemble(hist.Select(c => c.type == "hIST" ? Chunk("hIST", new byte[14]) : c.bytes))).Histogram);
        // none of it touches the pixels
        TestUtil.AssertEqual(Png.Decode(Load("png/meta_rare_chunks.png")), Png.Decode(Replace("pCAL", new byte[3])));
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
