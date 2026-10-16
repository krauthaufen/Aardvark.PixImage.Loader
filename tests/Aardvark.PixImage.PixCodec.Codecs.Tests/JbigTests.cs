using Aardvark.ImageCodecs;
using Xunit;

namespace Aardvark.ImageCodecs.Tests;

public class JbigTests
{
    public static IEnumerable<object[]> Fixtures() => TestUtil.Files("jbig", ".jbg");

    static byte[] Load(string file) => File.ReadAllBytes(TestUtil.Fixture(file));

    /// <summary>
    /// Every fixture (written by jbigkit's pbmtojbg across its whole option matrix) decodes to exactly what jbigkit's
    /// own decoder gives - JBIG is lossless, so there is no tolerance.
    /// </summary>
    [Theory]
    [MemberData(nameof(Fixtures))]
    public void DecodeMatchesReference(string file)
    {
        var data = Load(file);
        var expected = TestUtil.LoadRef(TestUtil.Fixture(file) + ".ref");
        var options = new JbigDecodeOptions { BinaryPlanes = file.Contains("_binary") };
        var actual = Jbig.Decode(data, options);
        TestUtil.AssertEqual(expected, actual, 0);
        Assert.Equal(ColorModel.Gray, actual.Color);
        var info = Jbig.GetInfo(data);
        Assert.Equal((actual.Channels, actual.SampleType), (info.Channels, info.SampleType));
        if (file.Contains("concat.")) Assert.Equal((actual.Width / 2, actual.Height / 2), (info.Width, info.Height)); // the first entity stops one layer short
        else if (file.Contains("newlen")) Assert.True(info.Width == actual.Width && info.Height > actual.Height);
        else Assert.Equal((actual.Width, actual.Height), (info.Width, info.Height));
        Assert.InRange(info.BitsPerSample, 1, 8);
    }

    // ------------------------------------------------------------------ constructed streams

    /// <summary>
    /// Private deterministic prediction tables: the default table moved into the header (DPPRIV), and an entity
    /// that reuses the table of the previous one (DPLAST), must decode like the plain stream.
    /// </summary>
    [Theory]
    [InlineData("jbig/layers2_p28.jbg")]
    [InlineData("jbig/layers3_p4_noise.jbg")]
    [InlineData("jbig/layers5_200x150.jbg")]
    public void PrivateDeterministicPredictionTable(string file)
    {
        var data = Load(file);
        var expected = Jbig.Decode(data);
        Assert.True((data[19] & 0x04) != 0, "fixture uses deterministic prediction");
        var table = Aardvark.ImageCodecs.JbigImpl.JbigTables.ToHeader(Aardvark.ImageCodecs.JbigImpl.JbigTables.Default());
        Assert.Equal(1728, table.Length);
        var priv = data.Take(20).Concat(table).Concat(data.Skip(20)).ToArray();
        priv[19] |= 0x02;
        TestUtil.AssertEqual(expected, Jbig.Decode(priv), 0);

        // a table that predicts nothing breaks the stream (the encoder skipped pixels the decoder now expects)
        var none = (byte[])priv.Clone();
        for (int i = 0; i < 1728; i++) none[20 + i] = 0xAA;
        try { Assert.NotEqual(expected.GetData<byte>(), Jbig.Decode(none).GetData<byte>()); }
        catch (ImageFormatException) { }

        // "use the last table" needs one
        var last = (byte[])data.Clone();
        last[19] |= 0x03;
        Assert.Throws<ImageFormatException>(() => Jbig.Decode(last));
        // table round trip
        var back = Aardvark.ImageCodecs.JbigImpl.JbigTables.FromHeader(table);
        Assert.Equal(Aardvark.ImageCodecs.JbigImpl.JbigTables.Default(), back);
    }

    [Fact]
    public void MarkersAndErrors()
    {
        var data = Load("jbig/seq_s7.jbg");
        var expected = Jbig.Decode(data);
        // a comment and a reserved-looking but valid sequence of floating markers before the first stripe
        var comment = new byte[] { 0xFF, 0x07, 0, 0, 0, 5, 0xFF, 0x02, 0xFF, 0x03, 0x41 };
        var withComment = data.Take(20).Concat(comment).Concat(data.Skip(20)).ToArray();
        TestUtil.AssertEqual(expected, Jbig.Decode(withComment), 0);

        // ABORT ends the stream
        int firstEnd = data.AsSpan(20).IndexOf(new byte[] { 0xFF, 0x02 }) + 22;
        var aborted = data.Take(firstEnd).Concat(new byte[] { 0xFF, 0x04 }).ToArray();
        Assert.Throws<ImageFormatException>(() => Jbig.Decode(aborted));
        Assert.Throws<ImageFormatException>(() => Jbig.Decode(data.AsSpan(0, firstEnd)));
        Assert.Throws<ImageFormatException>(() => Jbig.Decode(data.AsSpan(0, 19)));
        Assert.Throws<ImageFormatException>(() => Jbig.GetInfo(data.AsSpan(0, 19)));
        Assert.Throws<ImageFormatException>(() => Jbig.Decode(ReadOnlySpan<byte>.Empty));

        // an entity that starts above layer 0 cannot be decoded on its own
        Assert.Throws<ImageNotSupportedException>(() => Jbig.Decode(Load("jbig/unsupported/upper_only.jbg")));

        // size limit: a header may announce anything
        var huge = (byte[])data.Clone();
        huge[4] = 0x10; huge[8] = 0x10; huge[12] = 0x10; // 268 million pixels wide and high, in one stripe
        Assert.Throws<ImageNotSupportedException>(() => Jbig.Decode(huge));
        Assert.Equal(expected.Width, Jbig.Decode(data, new JbigDecodeOptions { MaxPixels = expected.Width * expected.Height }).Width);
        Assert.Throws<ImageNotSupportedException>(() => Jbig.Decode(data, new JbigDecodeOptions { MaxPixels = expected.Width * expected.Height - 1 }));
        // many stripes need many bytes
        var manyStripes = (byte[])data.Clone();
        manyStripes[8] = 0; manyStripes[9] = 0x10; manyStripes[10] = 0; manyStripes[11] = 0; // 1048576 lines of 1 line stripes...
        manyStripes[12] = 0; manyStripes[13] = 0; manyStripes[14] = 0; manyStripes[15] = 1;
        Assert.Throws<ImageFormatException>(() => Jbig.Decode(manyStripes));
    }

    // ------------------------------------------------------------------ robustness

    static void DecodeDamaged(ReadOnlySpan<byte> data, string what)
    {
        try
        {
            var img = Jbig.Decode(data, new JbigDecodeOptions { MaxPixels = 1 << 20 });
            Assert.True(img.Width > 0 && img.Height > 0);
        }
        catch (ImageFormatException) { }
        catch (ImageNotSupportedException) { }
        catch (Exception e)
        {
            Assert.Fail($"{what}: unexpected {e.GetType().Name}: {e}");
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void DamageNeverCrashes(string file)
    {
        var data = Load(file);
        int step = Math.Max(1, data.Length / 60);
        for (int n = 0; n < data.Length; n += step) DecodeDamaged(data.AsSpan(0, n), $"{file} truncated at {n}");
        var rnd = new Random(data.Length * 7919 + data[data.Length / 2]);
        for (int round = 0; round < 60; round++)
        {
            var d = (byte[])data.Clone();
            int flips = 1 + rnd.Next(4);
            for (int i = 0; i < flips; i++)
            {
                // half of the damage goes into the header
                int pos = rnd.Next(2) == 0 ? rnd.Next(Math.Min(d.Length, 20)) : rnd.Next(d.Length);
                d[pos] = rnd.Next(3) == 0 ? (byte)0xFF : (byte)rnd.Next(256);
            }
            DecodeDamaged(d, $"{file} corruption round {round}");
        }
    }

    // ------------------------------------------------------------------ encoding

    /// <summary>jbigkit's decoder on our output, or null when the tool is not installed (exit status 77 of the oracle script).</summary>
    static RawImage? OracleDecode(byte[] bie)
    {
        if (!TestUtil.PythonAvailable) return null;
        var tmp = Path.Combine(TestUtil.FixtureDir, "_tmp");
        Directory.CreateDirectory(tmp);
        var f = Path.Combine(tmp, Guid.NewGuid().ToString("N") + ".jbg");
        try
        {
            File.WriteAllBytes(f, bie);
            var (exit, output) = TestUtil.RunPython($"decode_to_ref.py \"{f}\" \"{f}.ref\"");
            if (TestUtil.CannotVerify(exit, output)) return null;
            Assert.True(exit == 0, "oracle decode failed: " + output);
            return TestUtil.LoadRef(f + ".ref");
        }
        finally
        {
            File.Delete(f); File.Delete(f + ".ref");
        }
    }

    static RawImage Document(int w, int h, int seed, double density = 0.08)
    {
        var img = RawImage.Create(w, h, ColorModel.Gray, SampleType.UInt8);
        var d = img.GetData<byte>();
        Array.Fill(d, (byte)255);
        var rnd = new Random(seed);
        // text-like runs, a rule, a box, and some lines that repeat the one above
        for (int y = 0; y < h; y++)
        {
            if (y > 0 && rnd.Next(3) == 0) { Array.Copy(d, (y - 1) * w, d, y * w, w); continue; }
            for (int x = 0; x < w; x++)
                if (rnd.NextDouble() < density || (x > 0 && d[y * w + x - 1] == 0 && rnd.Next(2) == 0)) d[y * w + x] = 0;
        }
        return img;
    }

    public static IEnumerable<object[]> EncodeCases()
    {
        foreach (var (w, h) in new[] { (1, 1), (1, 9), (9, 1), (7, 5), (8, 8), (33, 17), (129, 66), (300, 200) })
            foreach (bool twoLine in new[] { false, true })
                foreach (bool tp in new[] { true, false })
                    foreach (int stripe in new[] { 0, 1, 7, 128 })
                        yield return new object[] { w, h, twoLine, tp, stripe, 1, false };
        foreach (int planes in new[] { 2, 3, 4, 8 })
            foreach (bool binary in new[] { false, true })
                yield return new object[] { 67, 45, false, true, 16, planes, binary };
    }

    /// <summary>Our encoder's output is read back exactly by our decoder and by jbigkit.</summary>
    [Theory]
    [MemberData(nameof(EncodeCases))]
    public void EncodeRoundTrips(int w, int h, bool twoLine, bool tp, int stripe, int planes, bool binary)
    {
        RawImage src;
        if (planes == 1) src = Document(w, h, w * 31 + h + stripe);
        else
        {
            src = RawImage.Create(w, h, ColorModel.Gray, SampleType.UInt8);
            var d = src.GetData<byte>();
            var rnd = new Random(planes);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    d[y * w + x] = (byte)Math.Clamp(128 + 100 * Math.Sin(x * 0.2) * Math.Cos(y * 0.15) + rnd.Next(-6, 7), 0, 255);
        }
        var opt = new JbigEncodeOptions { TwoLineTemplate = twoLine, TypicalPrediction = tp, LinesPerStripe = stripe, BitPlanes = planes, BinaryPlanes = binary };
        using var ms = new MemoryStream();
        Jbig.Encode(src, ms, opt);
        var bie = ms.ToArray();

        var expected = RawImage.Create(w, h, ColorModel.Gray, SampleType.UInt8);
        var e = expected.GetData<byte>(); var s8 = src.GetData<byte>();
        for (int i = 0; i < e.Length; i++) e[i] = planes == 1 ? (byte)(s8[i] < 128 ? 0 : 255) : (byte)(s8[i] >> (8 - planes));

        var back = Jbig.Decode(bie, new JbigDecodeOptions { BinaryPlanes = binary });
        TestUtil.AssertEqual(expected, back, 0);
        var info = Jbig.GetInfo(bie);
        Assert.Equal((w, h, planes), (info.Width, info.Height, info.BitsPerSample));

        if (binary) return; // jbgtopbm would need its -b switch; the Gray coded variant covers the bit stream
        var oracle = OracleDecode(bie);
        if (oracle != null) TestUtil.AssertEqual(oracle, back, 0);
    }

    [Fact]
    public void EncodeCompressesAndRejects()
    {
        var page = Document(600, 400, 3, density: 0.02);
        using var ms = new MemoryStream();
        Jbig.Encode(page, ms);
        Assert.True(ms.Length < 600 * 400 / 8 / 3, $"{ms.Length} bytes for a sparse 600 x 400 page");
        using var plain = new MemoryStream();
        Jbig.Encode(page, plain, new JbigEncodeOptions { TypicalPrediction = false });
        Assert.True(ms.Length <= plain.Length);

        Assert.Throws<ImageNotSupportedException>(() => Jbig.Encode(RawImage.Create(4, 4, ColorModel.Rgb, SampleType.UInt8), new MemoryStream()));
        Assert.Throws<ImageNotSupportedException>(() => Jbig.Encode(RawImage.Create(4, 4, ColorModel.Gray, SampleType.UInt16), new MemoryStream()));
        Assert.Throws<ArgumentException>(() => Jbig.Encode(RawImage.Create(4, 4, ColorModel.Gray, SampleType.UInt8), new MemoryStream(), new JbigEncodeOptions { BitPlanes = 9 }));
    }

    // ------------------------------------------------------------------ TIFF strips

    [Fact]
    public void TiffStrips()
    {
        foreach (var (w, h) in new[] { (200, 150), (61, 47), (7, 5), (1, 1), (8, 3) })
        {
            var page = Document(w, h, w + h);
            int rowBytes = (w + 7) / 8;
            var packed = new byte[rowBytes * h];
            var src = page.GetData<byte>();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++) if (src[y * w + x] == 0) packed[y * rowBytes + (x >> 3)] |= (byte)(0x80 >> (x & 7));

            using var ms = new MemoryStream();
            Jbig.EncodeForTiff(packed, w, h, new JbigEncodeOptions { LinesPerStripe = 16 }, ms);
            var bie = ms.ToArray();
            var back = new byte[packed.Length];
            Jbig.DecodeForTiff(bie, w, h, back);
            Assert.Equal(packed, back);
            Assert.Equal(packed, Jbig.DecodeBits(bie, out int bw, out int bh));
            Assert.Equal((w, h), (bw, bh));
            // the same entity through the public decoder: 1 bits are black
            TestUtil.AssertEqual(page, Jbig.Decode(bie), 0);

            // fewer lines than the entity holds: the top part
            if (h > 2)
            {
                var top = new byte[rowBytes * (h - 2)];
                Jbig.DecodeForTiff(bie, w, h - 2, top);
                Assert.True(packed.AsSpan(0, top.Length).SequenceEqual(top));
            }
            Assert.Throws<ImageFormatException>(() => Jbig.DecodeForTiff(bie, w, h + 1, new byte[rowBytes * (h + 1)]));
            Assert.Throws<ImageFormatException>(() => Jbig.DecodeForTiff(bie, w + 1, h, new byte[((w + 8) / 8) * h]));
            Assert.Throws<ArgumentException>(() => Jbig.DecodeForTiff(bie, w, h, new byte[packed.Length + 1]));
            Parallel.For(0, 16, _ =>
            {
                var b = new byte[packed.Length];
                Jbig.DecodeForTiff(bie, w, h, b);
                Assert.Equal(packed, b);
            });
        }
        // a jbigkit stream with layers and NEWLEN through the strip entry point
        var data = Load("jbig/newlen_layers.jbg");
        var img = Jbig.Decode(data);
        var rows = new byte[(img.Width + 7) / 8 * img.Height];
        Jbig.DecodeForTiff(data, img.Width, img.Height, rows);
        var px = img.GetData<byte>();
        for (int y = 0; y < img.Height; y++)
            for (int x = 0; x < img.Width; x++)
                Assert.Equal(px[y * img.Width + x] == 0, (rows[y * ((img.Width + 7) / 8) + (x >> 3)] & (0x80 >> (x & 7))) != 0);
        Assert.Throws<ImageFormatException>(() => Jbig.DecodeForTiff(Load("jbig/planes2_gray.jbg"), 70, 50, new byte[9 * 50]));
        Assert.Throws<ImageNotSupportedException>(() => Jbig.DecodeBits(Load("jbig/planes2_gray.jbg"), out _, out _));
        var nl = Jbig.DecodeBits(data, out int nw, out int nh);
        Assert.Equal((img.Width, img.Height), (nw, nh));
        Assert.Equal(rows, nl);
    }
}
