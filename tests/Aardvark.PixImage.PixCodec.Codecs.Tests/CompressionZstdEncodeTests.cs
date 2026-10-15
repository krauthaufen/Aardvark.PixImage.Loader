using Aardvark.ImageCodecs.Compression;
using Xunit;
using Xunit.Abstractions;
using static Aardvark.ImageCodecs.Tests.CompressionTestData;

namespace Aardvark.ImageCodecs.Tests;

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

    // decodes with the zstd tool and a dictionary file; null when the tool is not installed
    static byte[]? ToolDecode(byte[] frame, string dictionaryFile)
    {
        var dir = Path.Combine(TestUtil.FixtureDir, "_tmp");
        Directory.CreateDirectory(dir);
        var f = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".zst");
        try
        {
            File.WriteAllBytes(f, frame);
            var psi = new System.Diagnostics.ProcessStartInfo("zstd") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "-d", "-c", "-q", "-D", dictionaryFile, f }) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi)!;
            var ms = new MemoryStream();
            var copy = p.StandardOutput.BaseStream.CopyToAsync(ms);
            var err = p.StandardError.ReadToEndAsync();
            copy.Wait(); p.WaitForExit();
            Assert.True(p.ExitCode == 0, "zstd -D failed: " + err.Result);
            return ms.ToArray();
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
        finally { File.Delete(f); }
    }

    /// <summary>Compression with a trained (structured) and a plain-content dictionary, read back by us and by <c>zstd -D</c>.</summary>
    [Fact]
    public void DictionaryCompression()
    {
        bool toolSeen = false;
        foreach (var dictName in new[] { "trained.zdict", "rawcontent.zdict" })
        {
            var dict = Load(dictName);
            var other = Load(dictName == "trained.zdict" ? "rawcontent.zdict" : "trained.zdict");
            foreach (var dataName in new[] { "dictsmall.bin", "dictlarge.bin", "text.bin", "empty.bin", "one.bin" })
            {
                var data = Load(dataName);
                foreach (var (level, checksum) in new[] { (1, false), (2, true), (3, true), (5, false), (9, true), (19, false) })
                {
                    string what = $"{dataName} with {dictName}, level {level}";
                    var z = Zstd.Compress(data, dict, level, checksum);
                    Assert.True(data.AsSpan().SequenceEqual(Zstd.DecompressWithDictionary(z, dict)), what);
                    var dst = new byte[data.Length];
                    Assert.Equal(data.Length, Zstd.Decompress(z, dst, dict));
                    Assert.True(data.AsSpan().SequenceEqual(dst), what);
                    var tool = ToolDecode(z, Fixture(dictName));
                    if (tool != null) { toolSeen = true; Assert.True(data.AsSpan().SequenceEqual(tool), what + ": zstd -D"); }
                    if (dataName.StartsWith("dict"))
                    {
                        // the dictionary is really used: the frame is smaller, and useless without it
                        Assert.True(z.Length < Zstd.Compress(data, level, checksum).Length, what + ": no gain from the dictionary");
                        var ex = Record.Exception(() => Zstd.Decompress(z, dst));
                        Assert.True(ex is ImageFormatException or ImageNotSupportedException, what + ": decoded without its dictionary");
                        if (checksum) Assert.Throws<ImageFormatException>(() => Zstd.Decompress(z, dst, other)); // (without a checksum a wrong dictionary can go unnoticed)
                    }
                }
            }
        }
        log.WriteLine(toolSeen ? "verified with zstd -D" : "zstd tool not available: only self-checked");
        // records like the dictionary's samples shrink a lot more with it
        var small = Load("dictsmall.bin");
        Assert.True(Zstd.Compress(small, Load("trained.zdict")).Length < Zstd.Compress(small).Length * 0.7);
    }

    [Fact]
    public void DictionaryFuzz()
    {
        var rnd = new Random(808);
        var dicts = new[] { Load("trained.zdict"), Load("rawcontent.zdict"), Make("text").AsSpan(0, 20_000).ToArray(), new byte[] { 1, 2, 3 }, Make("random").AsSpan(0, 3000).ToArray() };
        for (int it = 0; it < 250 * FuzzScale; it++)
        {
            var dict = dicts[rnd.Next(dicts.Length)];
            var data = CompressionTestData.Fuzz(rnd);
            // sometimes splice dictionary content into the data so that there is something to find
            if (dict.Length > 100 && data.Length > 200 && rnd.Next(2) == 0)
                dict.AsSpan(dict.Length - 100, 100).CopyTo(data.AsSpan(rnd.Next(data.Length - 100)));
            int level = new[] { 1, 2, 3, 4, 6, 9, 16 }[rnd.Next(7)];
            var z = Zstd.Compress(data, dict, level, rnd.Next(2) == 0);
            byte[] back;
            try { back = Zstd.DecompressWithDictionary(z, dict); }
            catch (Exception e) { throw new Xunit.Sdk.XunitException($"iteration {it}, {data.Length} bytes, level {level}: {e.Message}"); }
            Assert.True(data.AsSpan().SequenceEqual(back), $"iteration {it}, {data.Length} bytes, level {level}");
        }
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
