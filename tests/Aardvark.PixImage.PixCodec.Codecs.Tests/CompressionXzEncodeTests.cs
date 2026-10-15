using System.Diagnostics;
using Aardvark.ImageCodecs.Compression;
using Xunit;
using Xunit.Abstractions;
using static Aardvark.ImageCodecs.Tests.CompressionTestData;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>The LZMA2 / .xz and .lzma writers: read back by our decoder, by liblzma (python) and by 7-Zip.</summary>
public class CompressionXzEncodeTests
{
    readonly ITestOutputHelper log;
    public CompressionXzEncodeTests(ITestOutputHelper o) => log = o;

    [Fact]
    public void ShapesLevelsAndOptions()
    {
        var streams = new List<byte[]>();
        var originals = new List<byte[]>();
        foreach (var shape in Shapes)
        {
            var data = Make(shape);
            var line = $"{shape,-8} {data.Length,7}:";
            foreach (var (level, delta, check) in new[]
            {
                (0, 0, XzCheck.None), (1, 0, XzCheck.Crc32), (3, 3, XzCheck.Crc64), (4, 1, XzCheck.Sha256), (6, 0, XzCheck.Crc64),
                (6, 256, XzCheck.Crc32), (9, 0, XzCheck.Crc64),
            })
            {
                var z = Xz.Compress(data, level, delta, check);
                Assert.True(Xz.IsXz(z));
                Assert.True(data.AsSpan().SequenceEqual(Xz.Decompress(z)), $"{shape} level {level} delta {delta}");
                var dst = new byte[data.Length];
                Assert.Equal(data.Length, Xz.Decompress(z, dst));
                Assert.True(data.AsSpan().SequenceEqual(dst));
                // stored chunks bound the expansion
                Assert.True(z.Length <= data.Length + data.Length / 65536 * 3 + 100, $"{shape} level {level}: {z.Length} bytes for {data.Length}");
                streams.Add(z); originals.Add(data);
                if (delta == 0) line += $"  L{level} {z.Length}";
            }
            log.WriteLine(line);
        }
        var py = PythonBatch("results=[lzma.decompress(x) for x in items]", streams);
        if (py == null) return;
        for (int i = 0; i < streams.Count; i++) Assert.True(originals[i].AsSpan().SequenceEqual(py[i]), "liblzma decode of stream " + i);
    }

    [Fact]
    public void CompressesReasonablyWell()
    {
        // no optimal parser, so not liblzma's ratio - but well ahead of deflate on these
        var text = Make("text");
        Assert.True(Xz.Compress(text, 9).Length < Deflater.Compress(text, 9, true).Length);
        var image = Make("image");
        Assert.True(Xz.Compress(image, 6).Length < Deflater.Compress(image, 9, true).Length * 0.95);
        var big = Make("big");
        Assert.True(Xz.Compress(big, 6).Length < Deflater.Compress(big, 9, true).Length);
        Assert.True(Xz.Compress(Make("zeros"), 6).Length < 200);
        // the delta filter pays off on data it is meant for (16 bit samples, 3 channels)
        var img = Make("image16");
        var raw = new byte[img.Length];
        for (int i = 0; i < raw.Length; i++) raw[i] = (byte)(i < 6 ? img[i] : img[i] + raw[i - 6]); // undo a distance 6 difference
        Assert.True(Xz.Compress(raw, 6, 6).Length < Xz.Compress(raw, 6).Length);
    }

    [Fact]
    public void LargeInputManyChunks()
    {
        // > 2 MiB chunks, incompressible stretches (stored chunks, model resets), matches across chunk borders
        var rnd = new Random(12);
        var unit = Make("big");
        var data = new byte[9_000_000];
        for (int i = 0; i < data.Length;)
        {
            int n = Math.Min(data.Length - i, rnd.Next(1, 500_000));
            int kind = rnd.Next(4);
            if (kind == 0) rnd.NextBytes(data.AsSpan(i, n));
            else if (kind == 1) data.AsSpan(i, n).Fill((byte)rnd.Next(256));
            else { n = Math.Min(n, unit.Length - 1); unit.AsSpan(rnd.Next(unit.Length - n), n).CopyTo(data.AsSpan(i)); }
            i += n;
        }
        foreach (int level in new[] { 1, 6 })
        {
            var z = Xz.Compress(data, level);
            Assert.True(z.Length < data.Length);
            var dst = new byte[data.Length];
            Assert.Equal(data.Length, Xz.Decompress(z, dst));
            Assert.True(data.AsSpan().SequenceEqual(dst));
            var py = PythonBatch("results=[lzma.decompress(x) for x in items]", new[] { z });
            if (py != null) Assert.True(data.AsSpan().SequenceEqual(py[0]), "liblzma, level " + level);
        }
    }

    /// <summary>Independent blocks, compressed one after the other or in parallel: same bytes either way, read by everyone.</summary>
    [Fact]
    public void MultiBlockAndThreads()
    {
        var streams = new List<byte[]>();
        var originals = new List<byte[]>();
        foreach (var shape in new[] { "big", "mixed", "image16", "one", "empty" })
        {
            var data = Make(shape);
            foreach (var (level, delta, blockSize) in new[] { (1, 0, 4096), (4, 3, 50_000), (6, 0, 100_000), (7, 0, 200_000), (9, 2, 65_536), (6, 0, 1 << 30) })
            {
                var single = Xz.Compress(data, level, delta, XzCheck.Crc32, threads: 1, blockSize: blockSize);
                var parallel = Xz.Compress(data, level, delta, XzCheck.Crc32, threads: 4, blockSize: blockSize);
                var auto = Xz.Compress(data, level, delta, XzCheck.Crc32, threads: 0, blockSize: blockSize);
                Assert.True(single.AsSpan().SequenceEqual(parallel) && single.AsSpan().SequenceEqual(auto), $"{shape} level {level}: thread count changes the output");
                Assert.True(data.AsSpan().SequenceEqual(Xz.Decompress(single)), $"{shape} level {level} block size {blockSize}");
                var dst = new byte[data.Length];
                Assert.Equal(data.Length, Xz.Decompress(single, dst));
                Assert.True(data.AsSpan().SequenceEqual(dst));
                streams.Add(single); originals.Add(data);
            }
        }
        // the default block size splits only large inputs: several megabytes at a fast level give several blocks
        var rnd = new Random(3);
        var large = new byte[7_000_000];
        var unit = Make("big");
        for (int i = 0; i < large.Length;) { int n = Math.Min(large.Length - i, rnd.Next(1000, unit.Length)); unit.AsSpan(0, n).CopyTo(large.AsSpan(i)); i += n; }
        var z = Xz.Compress(large, 2, threads: 0);
        Assert.True(large.AsSpan().SequenceEqual(Xz.Decompress(z)));
        streams.Add(z); originals.Add(large);
        var py = PythonBatch("results=[lzma.decompress(x) for x in items]", streams);
        if (py == null) return;
        for (int i = 0; i < streams.Count; i++) Assert.True(originals[i].AsSpan().SequenceEqual(py[i]), "liblzma decode of stream " + i);
    }

    /// <summary>The optimal parser (levels 7 and up) must not lose to the lazy one, and is what closes the gap on text.</summary>
    [Fact]
    public void OptimalParserPaysOff()
    {
        foreach (var shape in new[] { "text", "mixed", "big" })
        {
            var data = Make(shape);
            int lazy = Xz.Compress(data, 6).Length, optimal = Xz.Compress(data, 7).Length;
            log.WriteLine($"{shape}: level 6 {lazy}, level 7 {optimal}");
            Assert.True(optimal < lazy, $"{shape}: level 7 {optimal} vs level 6 {lazy}");
        }
        // against liblzma itself at its default preset (same dictionary reach on inputs this small)
        var shapes = new[] { "text", "mixed", "big", "image" };
        var py = PythonBatch("results=[struct.pack('<i',len(lzma.compress(x,preset=6))) for x in items]", shapes.Select(x => Make(x)).ToList());
        if (py == null) return;
        for (int i = 0; i < shapes.Length; i++)
        {
            int ours = Xz.Compress(Make(shapes[i]), 7).Length, reference = BitConverter.ToInt32(py[i]);
            log.WriteLine($"{shapes[i]}: level 7 {ours}, xz -6 {reference} ({100.0 * ours / reference:F1}%)");
            Assert.True(ours <= reference * 1.04, $"{shapes[i]}: {ours} bytes, liblzma {reference}");
        }
    }

    [Fact]
    public void Fuzz()
    {
        var rnd = new Random(606);
        var streams = new List<byte[]>();
        var originals = new List<byte[]>();
        for (int it = 0; it < 300 * FuzzScale; it++)
        {
            var data = CompressionTestData.Fuzz(rnd);
            bool alone = rnd.Next(4) == 0;
            int level = rnd.Next(10);
            byte[] z = alone ? Xz.CompressLzma(data, level) : Xz.Compress(data, level, rnd.Next(3) == 0 ? rnd.Next(1, 257) : 0, (XzCheck)new[] { 0, 1, 4, 10 }[rnd.Next(4)]);
            byte[] back;
            try { back = alone ? Xz.DecompressLzma(z) : Xz.Decompress(z); }
            catch (Exception e) { throw new Xunit.Sdk.XunitException($"iteration {it}, {data.Length} bytes, level {level}, alone {alone}: {e.Message}"); }
            Assert.True(data.AsSpan().SequenceEqual(back), $"iteration {it}, {data.Length} bytes, level {level}, alone {alone}");
            if (it % 3 == 0) { streams.Add(z); originals.Add(data); }
        }
        var py = PythonBatch("results=[lzma.decompress(x) for x in items]", streams);
        if (py == null) return;
        for (int i = 0; i < streams.Count; i++) Assert.True(originals[i].AsSpan().SequenceEqual(py[i]), "liblzma decode of fuzz stream " + i);
    }

    static byte[]? SevenZip(byte[] lzmaFile)
    {
        // 7-Zip's LZMA decoder accepts the full property range (lc <= 8, lp <= 4), liblzma only lc + lp <= 4
        try
        {
            var dir = Path.Combine(TestUtil.FixtureDir, "_tmp");
            Directory.CreateDirectory(dir);
            var f = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".lzma");
            File.WriteAllBytes(f, lzmaFile);
            try
            {
                var psi = new ProcessStartInfo("7z") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                foreach (var a in new[] { "x", "-so", "-tlzma", f }) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi)!;
                var ms = new MemoryStream();
                var copy = p.StandardOutput.BaseStream.CopyToAsync(ms);
                var err = p.StandardError.ReadToEndAsync();
                copy.Wait(); p.WaitForExit();
                Assert.True(p.ExitCode == 0, "7z failed: " + err.Result);
                return ms.ToArray();
            }
            finally { File.Delete(f); }
        }
        catch (System.ComponentModel.Win32Exception) { return null; } // 7z not installed
    }

    /// <summary>
    /// Literal context / position bits beyond what liblzma supports (lc + lp > 4): written by our encoder, read by
    /// our decoder and - as the independent reference - by 7-Zip.
    /// </summary>
    [Fact]
    public void LzmaAloneWithWideLiteralContext()
    {
        bool sevenZipSeen = false;
        foreach (var shape in new[] { "text", "image", "image16", "mixed", "one", "empty" })
            foreach (var (lc, lp, pb) in new[] { (8, 4, 4), (8, 0, 0), (4, 4, 2), (0, 4, 0), (5, 1, 3), (8, 4, 0), (3, 0, 2), (4, 0, 4) })
            {
                var data = Make(shape);
                var z = Xz.CompressLzma(data, 5, lc, lp, pb);
                Assert.Equal((pb * 5 + lp) * 9 + lc, z[0]);
                Assert.True(data.AsSpan().SequenceEqual(Xz.DecompressLzma(z)), $"{shape} lc={lc} lp={lp} pb={pb}");
                var dst = new byte[data.Length];
                Assert.Equal(data.Length, Xz.DecompressLzma(z, dst));
                Assert.True(data.AsSpan().SequenceEqual(dst));
                var ref7 = SevenZip(z);
                if (ref7 != null) { sevenZipSeen = true; Assert.True(data.AsSpan().SequenceEqual(ref7), $"7-Zip: {shape} lc={lc} lp={lp} pb={pb}"); }
                if (lc + lp <= 4)
                {
                    var py = PythonBatch("results=[lzma.decompress(x) for x in items]", new[] { z });
                    if (py != null) Assert.True(data.AsSpan().SequenceEqual(py[0]), $"liblzma: {shape} lc={lc} lp={lp} pb={pb}");
                }
            }
        log.WriteLine(sevenZipSeen ? "verified with 7-Zip" : "7z not available: only self-checked");
    }
}
