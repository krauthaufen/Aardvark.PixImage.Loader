using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using PixCodec.Compression;
using Xunit;
using Xunit.Abstractions;
using static PixCodec.Tests.CompressionTestData;

namespace PixCodec.Tests;

/// <summary>
/// Throughput measurements, managed versus BCL (native zlib). Not a test: it only runs when PIXCODEC_BENCH names a
/// directory with the data files (png.bin = PNG-filtered scanlines of a photo, text.bin, tiff16.bin and their
/// .zst / .xz versions; see the benchmark notes in the final report). Results go to bench.txt in that directory.
/// </summary>
public class CompressionBench
{
    readonly ITestOutputHelper log;
    public CompressionBench(ITestOutputHelper o) => log = o;

    static double Best(Action action, int minRuns = 5, double minSeconds = 1.5)
    {
        action(); action(); // warm up (tiered JIT)
        double best = double.MaxValue;
        var total = Stopwatch.StartNew();
        for (int i = 0; i < minRuns || total.Elapsed.TotalSeconds < minSeconds; i++)
        {
            var sw = Stopwatch.StartNew();
            action();
            best = Math.Min(best, sw.Elapsed.TotalSeconds);
            if (i > 40) break;
        }
        return best;
    }

    [Fact]
    public void Run()
    {
        var dir = Environment.GetEnvironmentVariable("PIXCODEC_BENCH");
        if (string.IsNullOrEmpty(dir)) return;
        var only = Environment.GetEnvironmentVariable("PIXCODEC_BENCH_ONLY") ?? "";
        var sb = new StringBuilder();
        void Out(string s) { sb.AppendLine(s); log.WriteLine(s); File.WriteAllText(Path.Combine(dir, "bench.txt"), sb.ToString()); }
        Out($"runtime {Environment.Version}, {Environment.ProcessorCount} logical cores, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");

        var sets = new List<(string name, byte[] data)>();
        foreach (var f in new[] { "png.bin", "tiff16.bin", "text.bin" })
            if (File.Exists(Path.Combine(dir, f))) sets.Add((f, File.ReadAllBytes(Path.Combine(dir, f))));
        var rnd = new byte[16 << 20];
        new Random(1).NextBytes(rnd);
        sets.Add(("random", rnd));

        var bclLevels = new[] { (1, CompressionLevel.Fastest), (6, CompressionLevel.Optimal), (9, CompressionLevel.SmallestSize) };
        if (only == "" || only.Contains("deflate"))
        {
            Out("");
            Out("| data | level | managed size | zlib size | size ratio | managed enc MB/s | BCL enc MB/s | enc slowdown | managed dec MB/s | BCL dec MB/s | dec slowdown |");
            Out("|---|---|---|---|---|---|---|---|---|---|---|");
            foreach (var (name, data) in sets)
            {
                double mb = data.Length / 1e6;
                foreach (var (level, bclLevel) in bclLevels)
                {
                    byte[] mz = Array.Empty<byte>(), bz = Array.Empty<byte>();
                    double tm = Best(() => mz = Deflater.Compress(data, level, true), 3);
                    double tb = Best(() => bz = BclCompress(data, bclLevel), 3);
                    // decode the stream native zlib wrote (what existing files look like)
                    var dst = new byte[data.Length];
                    double dm = Best(() => InflateCore.Decompress(bz, dst, true, true));
                    Assert.True(data.AsSpan().SequenceEqual(dst));
                    double db = Best(() =>
                    {
                        using var ms = new MemoryStream(bz);
                        using var z = new ZLibStream(ms, CompressionMode.Decompress);
                        int t = 0;
                        while (t < dst.Length) { int n = z.Read(dst, t, dst.Length - t); if (n <= 0) break; t += n; }
                    });
                    Out($"| {name} | {level} | {mz.Length} | {bz.Length} | {100.0 * mz.Length / bz.Length:F2}% | {mb / tm:F1} | {mb / tb:F1} | {tm / tb:F2}x | {mb / dm:F0} | {mb / db:F0} | {dm / db:F2}x |");
                }
            }
            Out("");
            Out("| data | managed levels 1..9: size (enc MB/s) |");
            Out("|---|---|");
            foreach (var (name, data) in sets)
            {
                if (name == "random") continue;
                var line = $"| {name} |";
                for (int level = 1; level <= 9; level++)
                {
                    byte[] mz = Array.Empty<byte>();
                    double tm = Best(() => mz = Deflater.Compress(data, level, true), 2, 0.5);
                    line += $" L{level} {mz.Length} ({data.Length / 1e6 / tm:F0})";
                }
                Out(line + " |");
            }
            Out("");
            Out("| data | inflate without Adler-32 MB/s | streaming Inflater (64 KiB in, 64 KiB out) MB/s | DeflaterStream L6 MB/s |");
            Out("|---|---|---|---|");
            foreach (var (name, data) in sets)
            {
                double mb = data.Length / 1e6;
                var bz = BclCompress(data, CompressionLevel.Optimal);
                var dst = new byte[data.Length];
                double noCheck = Best(() => InflateCore.Decompress(bz, dst, true, false));
                var inf = new Inflater();
                double streaming = Best(() =>
                {
                    inf.Reset();
                    int ip = 0, op = 0;
                    for (; ; )
                    {
                        var st = inf.Inflate(bz.AsSpan(ip, Math.Min(65536, bz.Length - ip)), dst.AsSpan(op, Math.Min(65536, dst.Length - op)), out int c, out int w);
                        ip += c; op += w;
                        if (st == InflateStatus.Done) break;
                    }
                });
                double stream = Best(() =>
                {
                    var ms = new MemoryStream(data.Length / 2);
                    using var z = new DeflaterStream(ms, 6, true);
                    for (int i = 0; i < data.Length; i += 100_000) z.Write(data.AsSpan(i, Math.Min(100_000, data.Length - i)));
                }, 3);
                Out($"| {name} | {mb / noCheck:F0} | {mb / streaming:F0} | {mb / stream:F1} |");
            }
        }

        if (only == "" || only.Contains("zstd") || only.Contains("xz"))
        {
            Out("");
            Out("| file | compressed | raw | managed dec MB/s |");
            Out("|---|---|---|---|");
            foreach (var f in Directory.GetFiles(dir).OrderBy(x => x))
            {
                bool zst = f.EndsWith(".zst"), xz = f.EndsWith(".xz");
                if (!(zst && (only == "" || only.Contains("zstd"))) && !(xz && (only == "" || only.Contains("xz")))) continue;
                var z = File.ReadAllBytes(f);
                var raw = zst ? Zstd.Decompress(z) : Xz.Decompress(z);
                var dst = new byte[raw.Length];
                double t = Best(() => { if (zst) Zstd.Decompress(z, dst); else Xz.Decompress(z, dst); }, 3);
                Assert.True(raw.AsSpan().SequenceEqual(dst));
                Out($"| {Path.GetFileName(f)} | {z.Length} | {raw.Length} | {raw.Length / 1e6 / t:F0} |");
            }
        }

        if (only == "" || only.Contains("checksum"))
        {
            var data = sets[0].data;
            double mb = data.Length / 1e6;
            Out("");
            Out("| checksum | MB/s |");
            Out("|---|---|");
            Out($"| CRC-32 (dispatch) | {mb / Best(() => Checksums.Crc32(data, 0)):F0} |");
            Out($"| CRC-32 slicing-by-8 | {mb / Best(() => Checksums.Crc32Scalar(data, 0)):F0} |");
            Out($"| Adler-32 (dispatch) | {mb / Best(() => Checksums.Adler32(data, 1)):F0} |");
            Out($"| Adler-32 Vector128 | {mb / Best(() => Checksums.Adler32Vector128(data, 1)):F0} |");
            Out($"| Adler-32 scalar | {mb / Best(() => Checksums.Adler32Scalar(data, 1)):F0} |");
            Out($"| CRC-64 | {mb / Best(() => Checksums.Crc64(data)):F0} |");
            Out($"| xxHash64 | {mb / Best(() => Checksums.XxHash64(data)):F0} |");
        }
    }
}
