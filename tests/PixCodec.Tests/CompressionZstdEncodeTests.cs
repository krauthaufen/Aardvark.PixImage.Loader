using PixCodec.Compression;
using Xunit;
using Xunit.Abstractions;
using static PixCodec.Tests.CompressionTestData;

namespace PixCodec.Tests;

/// <summary>The simple zstd compressor: its frames must be read by our decoder and by the reference library.</summary>
public class CompressionZstdEncodeTests
{
    readonly ITestOutputHelper log;
    public CompressionZstdEncodeTests(ITestOutputHelper o) => log = o;

    // decodes every item with libzstd (python imagecodecs); null when unavailable
    static List<byte[]>? Reference(IReadOnlyList<byte[]> frames)
        => PythonBatch("import imagecodecs\nresults=[bytes(imagecodecs.zstd_decode(x)) for x in items]", frames);

    static bool ReferenceAvailable { get; } = Probe();

    static bool Probe()
    {
        try { return TestUtil.PythonAvailable && TestUtil.RunPython("-c \"import imagecodecs; imagecodecs.zstd_decode\"").exit == 0; }
        catch { return false; }
    }

    [Fact]
    public void ShapesRoundTrip()
    {
        var frames = new List<byte[]>();
        var originals = new List<byte[]>();
        foreach (var shape in Shapes)
            foreach (var (level, checksum) in new[] { (1, false), (1, true), (2, false), (3, true), (3, false), (5, false), (7, true), (9, false), (12, false), (19, true), (22, false) })
            {
                var data = Make(shape);
                var z = Zstd.Compress(data, level, checksum);
                Assert.True(data.AsSpan().SequenceEqual(Zstd.Decompress(z)), shape);
                var dst = new byte[data.Length];
                Assert.Equal(data.Length, Zstd.Decompress(z, dst));
                Assert.True(data.AsSpan().SequenceEqual(dst), shape);
                Assert.True(z.Length <= data.Length + 18 + 3 * (data.Length / 131072 + 1) + 4, $"{shape}: {z.Length} bytes for {data.Length}");
                frames.Add(z); originals.Add(data);
                if (!checksum) log.WriteLine($"{shape,-8} L{level,-2} {data.Length,7} -> {z.Length,7}  ({(data.Length == 0 ? 0 : 100.0 * z.Length / data.Length):F1}%)");
            }
        if (!ReferenceAvailable) return;
        var py = Reference(frames)!;
        for (int i = 0; i < frames.Count; i++) Assert.True(originals[i].AsSpan().SequenceEqual(py[i]), "libzstd decode of frame " + i);
    }

    [Fact]
    public void ActuallyCompresses()
    {
        // not a level 19, but real entropy coding and matching: these shapes must shrink clearly
        Assert.True(Zstd.Compress(Make("text")).Length < 60_000 * 0.40);
        Assert.True(Zstd.Compress(Make("image")).Length < 61_498 * 0.70);
        Assert.True(Zstd.Compress(Make("zeros")).Length < 100);
        Assert.True(Zstd.Compress(Make("runs")).Length < 2500);
        Assert.True(Zstd.Compress(Make("max")).Length < 2500);
    }

    [Fact]
    public void LargeAndMultiBlock()
    {
        // several megabytes: many blocks, matches reaching back across blocks, raw and RLE blocks in between
        var rnd = new Random(31);
        var unit = Make("big");
        var data = new byte[6_000_000];
        for (int i = 0; i < data.Length;)
        {
            int n = Math.Min(data.Length - i, rnd.Next(1, 300_000));
            int kind = rnd.Next(5);
            if (kind == 0) rnd.NextBytes(data.AsSpan(i, n));
            else if (kind == 1) data.AsSpan(i, n).Fill((byte)rnd.Next(256));
            else unit.AsSpan(rnd.Next(unit.Length - Math.Min(n, unit.Length - 1)), Math.Min(n, unit.Length - 1)).CopyTo(data.AsSpan(i));
            i += kind >= 2 ? Math.Min(n, unit.Length - 1) : n;
        }
        foreach (int level in new[] { 1, 3, 9 })
        {
            var zl = Zstd.Compress(data, level, true);
            Assert.True(zl.Length < data.Length);
            var back = new byte[data.Length];
            Assert.Equal(data.Length, Zstd.Decompress(zl, back));
            Assert.True(data.AsSpan().SequenceEqual(back), "level " + level);
            if (ReferenceAvailable) Assert.True(data.AsSpan().SequenceEqual(Reference(new[] { zl })![0]), "libzstd, level " + level);
        }
        var z = Zstd.Compress(data, true);
        Assert.True(z.Length < data.Length);
        var dst = new byte[data.Length];
        Assert.Equal(data.Length, Zstd.Decompress(z, dst));
        Assert.True(data.AsSpan().SequenceEqual(dst));
        if (!ReferenceAvailable) return;
        Assert.True(data.AsSpan().SequenceEqual(Reference(new[] { z })![0]));
    }

    [Fact]
    public void Fuzz()
    {
        var rnd = new Random(4242);
        var frames = new List<byte[]>();
        var originals = new List<byte[]>();
        for (int it = 0; it < 500 * FuzzScale; it++)
        {
            var data = CompressionTestData.Fuzz(rnd);
            var z = Zstd.Compress(data, new[] { 1, 2, 3, 3, 4, 6, 9, 13, 19 }[rnd.Next(9)], rnd.Next(2) == 0);
            byte[] back;
            try { back = Zstd.Decompress(z); }
            catch (Exception e) { throw new Xunit.Sdk.XunitException($"iteration {it}, {data.Length} bytes: {e.Message}"); }
            Assert.True(data.AsSpan().SequenceEqual(back), $"iteration {it}, {data.Length} bytes");
            if (it % 4 == 0) { frames.Add(z); originals.Add(data); }
        }
        if (!ReferenceAvailable) return;
        var py = Reference(frames)!;
        for (int i = 0; i < frames.Count; i++) Assert.True(originals[i].AsSpan().SequenceEqual(py[i]), "libzstd decode of fuzz frame " + i);
    }
}
