using System.Globalization;
using System.Security.Cryptography;
using PixCodec;
using Xunit;
using Xunit.Abstractions;
using static PixCodec.Tests.TestUtil;

namespace PixCodec.Tests;

/// <summary>
/// One line of tests/fixtures/lerc/manifest.tsv (our fixtures) or of fixtures/lerc/manifest.tsv in the test-data
/// repository (Esri's sample blobs): what Esri's liblerc reports about a blob.
/// </summary>
public sealed record LercManifestEntry(string Name, int Version, int DataType, int Cols, int Rows, int Depth, int Bands,
    int ValidPixels, int Masks, int UsesNoData, double ZMin, double ZMax, double MaxZError, int BlobSize, string NoData, string Sha256)
{
    static Dictionary<string, LercManifestEntry>? s_own, s_thirdParty, s_all;

    /// <summary>The fixtures of this repository.</summary>
    public static Dictionary<string, LercManifestEntry> Own => s_own ??= Load(Path.Combine(FixtureDir, "lerc", "manifest.tsv"));

    /// <summary>Esri's sample blobs in the test-data repository (none without it).</summary>
    public static Dictionary<string, LercManifestEntry> ThirdParty
        => s_thirdParty ??= TestDataFixtureDir == null ? new() : Load(Path.Combine(TestDataFixtureDir, "lerc", "manifest.tsv"));

    public static Dictionary<string, LercManifestEntry> All => s_all ??= Own.Concat(ThirdParty).ToDictionary(p => p.Key, p => p.Value);

    static Dictionary<string, LercManifestEntry> Load(string path)
    {
        var d = new Dictionary<string, LercManifestEntry>();
        if (!File.Exists(path)) return d;
        foreach (var line in File.ReadAllLines(path))
        {
            if (line.StartsWith('#') || line.Length == 0) continue;
            string[] c = line.Split('\t');
            int I(int k) => int.Parse(c[k], CultureInfo.InvariantCulture);
            double D(int k) => c[k] switch
            {
                "nan" => double.NaN, "inf" => double.PositiveInfinity, "-inf" => double.NegativeInfinity,
                _ => double.Parse(c[k], CultureInfo.InvariantCulture),
            };
            d[c[0]] = new LercManifestEntry(c[0], I(1), I(2), I(3), I(4), I(5), I(6), I(7), I(8), I(9), D(10), D(11), D(12), I(13), c[14], c[15]);
        }
        return d;
    }
}

public class LercDecodeTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Fixtures => Rows(LercManifestEntry.Own);

    /// <summary>Esri's sample blobs: MemberData of <see cref="TestDataTheoryAttribute"/> theories.</summary>
    public static IEnumerable<object[]> EsriSamples => Rows(LercManifestEntry.ThirdParty);

    static IEnumerable<object[]> Rows(Dictionary<string, LercManifestEntry> entries)
        => entries.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => new object[] { k });

    static readonly SampleType[] s_types =
    {
        SampleType.Int8, SampleType.UInt8, SampleType.Int16, SampleType.UInt16,
        SampleType.Int32, SampleType.UInt32, SampleType.Float32, SampleType.Float64,
    };

    /// <summary>The mask in the sidecar layout: (rows, cols, 1) or (rows, cols, bands) interleaved.</summary>
    static byte[]? MaskAsRef(byte[]? mask, int pixels)
    {
        if (mask == null || mask.Length == pixels) return mask;
        int bands = mask.Length / pixels;
        var r = new byte[mask.Length];
        for (int b = 0; b < bands; b++)
            for (int k = 0; k < pixels; k++) r[k * bands + b] = mask[b * pixels + k];
        return r;
    }

    [Theory, MemberData(nameof(Fixtures))]
    public void DecodeMatchesReference(string file)
    {
        var e = LercManifestEntry.All[file];
        var path = Fixture("lerc/" + file);
        var data = File.ReadAllBytes(path);
        Assert.True(Lerc.IsMatch(data));

        var img = Lerc.Decode(data, out var mask);
        Assert.Equal((e.Cols, e.Rows, e.Bands * e.Depth), (img.Width, img.Height, img.Channels));
        Assert.Equal(s_types[e.DataType], img.SampleType);
        int pixels = e.Cols * e.Rows;
        var refMask = MaskAsRef(mask, pixels);

        if (File.Exists(path + ".ref")) AssertEqual(LoadRef(path + ".ref"), img);
        if (File.Exists(path + ".mask.ref"))
        {
            Assert.NotNull(refMask);
            Assert.Equal(LoadRef(path + ".mask.ref").GetBytes().ToArray(), refMask);
        }
        else if (File.Exists(path + ".ref")) Assert.Null(mask);

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(img.GetBytes());
        if (refMask != null) sha.AppendData(refMask);
        Assert.Equal(e.Sha256, Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant());

        // the plain overload returns the same samples
        Assert.True(img.GetBytes().SequenceEqual(Lerc.Decode(data).GetBytes()));
    }

    [Theory, MemberData(nameof(Fixtures))]
    public void InfoMatchesReference(string file)
    {
        var e = LercManifestEntry.All[file];
        var data = File.ReadAllBytes(Fixture("lerc/" + file));
        var info = Lerc.GetInfo(data);
        Assert.Equal(e.Version, info.Version);
        Assert.Equal(s_types[e.DataType], info.SampleType);
        Assert.Equal((e.Cols, e.Rows, e.Depth, e.Bands), (info.Width, info.Height, info.Depth, info.Bands));
        Assert.Equal(e.ValidPixels, info.ValidPixelCount);
        Assert.Equal(e.MaxZError, info.MaxZError);
        Assert.Equal(e.UsesNoData != 0, info.HasNoDataValue);
        Assert.Equal(e.BlobSize, info.BlobSize);
        // liblerc's lerc_getBlobInfo reports -1 / -1 as range of a blob with no-data values and counts an empty band
        // as 0: compare with it where it reports the plain range, and always with the decoded data
        var img = Lerc.Decode(data, out var mask);
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        var v = ToDoublesForTest(img);
        int pixels = img.Width * img.Height;
        for (int i = 0; i < v.Length; i++)
        {
            int k = i / img.Channels, b = i % img.Channels / info.Depth;
            if (mask != null && mask[mask.Length == pixels ? k : b * pixels + k] == 0) continue;
            if (info.HasNoDataValue && v[i] == info.NoDataValue) continue;
            lo = Math.Min(lo, v[i]); hi = Math.Max(hi, v[i]);
        }
        if (lo <= hi)
        {
            Assert.True(info.MinValue <= lo && info.MaxValue >= hi, $"range {info.MinValue}..{info.MaxValue} vs data {lo}..{hi}");
            bool lossless = info.MaxZError == 0 || (info.MaxZError == 0.5 && img.SampleType is not (SampleType.Float32 or SampleType.Float64));
            // (with no-data values the header range includes the encoder's internal no-data value when it lies below)
            if (lossless && !info.HasNoDataValue) Assert.Equal((lo, hi), (info.MinValue, info.MaxValue));
        }
        bool plainRange = e.UsesNoData == 0 && !(info.Bands > 1 && mask != null && mask.Length > pixels);
        if (info.ValidPixelCount > 0 && plainRange)
        {
            Assert.Equal(e.ZMin, info.MinValue);
            Assert.Equal(e.ZMax, info.MaxValue);
        }
        if (info.HasNoDataValue)
        {
            var first = e.NoData.Split(',').First(s => s != "x");
            Assert.Equal(double.Parse(first, CultureInfo.InvariantCulture), info.NoDataValue);
        }
        // LERC2 info comes from the headers alone
        if (info.Version >= 2 && info.Bands == 1) Assert.Equal(info, Lerc.GetInfo(data.AsSpan(0, 100 < data.Length ? 100 : data.Length)));
    }

    [Theory, MemberData(nameof(Fixtures))]
    public void DecodeIntoCallerBuffer(string file)
    {
        var data = File.ReadAllBytes(Fixture("lerc/" + file));
        var img = Lerc.Decode(data, out var mask);
        int w = img.Width, h = img.Height, ch = img.Channels, bps = RawImage.BytesPerSample(img.SampleType);
        // one extra channel that must stay untouched
        var dst = new byte[w * h * (ch + 1) * bps];
        new Random(1).NextBytes(dst);
        var before = (byte[])dst.Clone();
        var validMask = new byte[w * h];
        var r = Lerc.DecodeInto(data, img.SampleType, w, h, dst, ch + 1, validMask);
        Assert.Equal(ch, r.Channels);
        Assert.Equal(mask != null, r.HasInvalidPixels);
        var src = img.GetBytes();
        for (int k = 0; k < w * h; k++)
        {
            Assert.True(src.Slice(k * ch * bps, ch * bps).SequenceEqual(dst.AsSpan(k * (ch + 1) * bps, ch * bps)), $"pixel {k}");
            Assert.True(before.AsSpan((k * (ch + 1) + ch) * bps, bps).SequenceEqual(dst.AsSpan((k * (ch + 1) + ch) * bps, bps)));
        }
        if (mask == null) Assert.All(validMask, b => Assert.Equal(1, b));
        else Assert.Equal(mask.AsSpan(0, w * h).ToArray(), validMask);

        Assert.Throws<ImageFormatException>(() => Lerc.DecodeInto(data, img.SampleType, w + 1, h, new byte[(w + 1) * h * ch * bps], ch, default));
        var other = img.SampleType == SampleType.UInt8 ? SampleType.Int8 : SampleType.UInt8;
        Assert.Throws<ImageFormatException>(() => Lerc.DecodeInto(data, other, w, h, new byte[w * h * ch * 8], ch, default));
    }

    [TestDataTheory("fixtures/lerc/manifest.tsv"), MemberData(nameof(EsriSamples))]
    public void EsriSampleMatchesReference(string file)
    {
        DecodeMatchesReference(file);
        InfoMatchesReference(file);
        DecodeIntoCallerBuffer(file);
    }

    static double[] ToDoublesForTest(RawImage img) => img.SampleType switch
    {
        SampleType.UInt8 => Array.ConvertAll((byte[])img.Data, x => (double)x),
        SampleType.Int8 => Array.ConvertAll((sbyte[])img.Data, x => (double)x),
        SampleType.UInt16 => Array.ConvertAll((ushort[])img.Data, x => (double)x),
        SampleType.Int16 => Array.ConvertAll((short[])img.Data, x => (double)x),
        SampleType.UInt32 => Array.ConvertAll((uint[])img.Data, x => (double)x),
        SampleType.Int32 => Array.ConvertAll((int[])img.Data, x => (double)x),
        SampleType.Float32 => Array.ConvertAll((float[])img.Data, x => (double)x),
        _ => (double[])img.Data,
    };

    /// <summary>Our fixtures together must reach every part of the format the decoder implements.</summary>
    [Fact]
    public void FixturesCoverTheFormat()
    {
        var s = new LercStats();
        LercStats.Current = s;
        try
        {
            foreach (var f in LercManifestEntry.Own.Keys) Lerc.Decode(File.ReadAllBytes(Fixture("lerc/" + f)));
        }
        finally { LercStats.Current = null; }

        output.WriteLine($"bands {s.Bands}: empty {s.EmptyBands} const {s.ConstantBands} raw {s.RawBands} tiles {s.TileBands} " +
            $"dHuff {s.DeltaHuffmanBands} huff {s.PlainHuffmanBands} float {s.FloatLosslessBands}");
        output.WriteLine($"mask stored {s.MaskStored} reused {s.MaskReused}, nodata remapped {s.NoDataRemapped}");
        output.WriteLine($"blocks {string.Join(",", s.Blocks)} lut {s.LutBlocks} relative {s.RelativeBlocks} zero-width {s.ZeroWidthBlocks}");
        output.WriteLine($"offset types {string.Join(",", s.OffsetTypes)} block sizes 8:{s.BlockSizes[8]} 16:{s.BlockSizes[16]}");
        output.WriteLine($"float predictors {string.Join(",", s.FloatPredictors)} planes {string.Join(",", s.PlaneCodings)} deltas {string.Join(",", s.PlaneDeltas)}");
        output.WriteLine($"lerc1 blocks {string.Join(",", s.Lerc1Blocks)}");

        Assert.All(new[] { s.EmptyBands, s.ConstantBands, s.RawBands, s.TileBands, s.DeltaHuffmanBands, s.PlainHuffmanBands,
            s.FloatLosslessBands, s.MaskStored, s.MaskReused, s.LutBlocks, s.RelativeBlocks, s.BlockSizes[8] },
            v => Assert.True(v > 0));
        Assert.All(s.Blocks, v => Assert.True(v > 0));
        Assert.All(s.FloatPredictors, v => Assert.True(v > 0));
        Assert.All(s.PlaneCodings, v => Assert.True(v > 0));
        Assert.All(s.Lerc1Blocks, v => Assert.True(v > 0));
        for (int d = 0; d <= 2; d++) Assert.True(s.PlaneDeltas[d] > 0, $"byte plane delta {d}");
        for (int t = 0; t < 8; t++) Assert.True(s.OffsetTypes[t] > 0, $"offset type {t}");
    }
}
