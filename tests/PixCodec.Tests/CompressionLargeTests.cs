using System.Diagnostics;
using PixCodec.Compression;
using Xunit;
using Xunit.Abstractions;

namespace PixCodec.Tests;

/// <summary>
/// Inputs and outputs of several hundred megabytes up to the 2 GiB array limit, and streams beyond 4 GiB.
/// These need several gigabytes of memory and minutes of time, so they only run with PIXCODEC_LARGE=1
/// (PIXCODEC_LARGE=2 adds the slowest ones).
/// </summary>
public class CompressionLargeTests
{
    readonly ITestOutputHelper log;
    public CompressionLargeTests(ITestOutputHelper o) => log = o;

    static int Mode => int.TryParse(Environment.GetEnvironmentVariable("PIXCODEC_LARGE"), out int v) ? v : 0;

    /// <summary>Compressible data any of the formats handles quickly: a 20 KB unit repeated with sparse changes.</summary>
    static void Fill(Span<byte> data, int seed)
    {
        var rnd = new Random(seed);
        var unit = new byte[20_000];
        rnd.NextBytes(unit);
        for (long i = 0; i < data.Length; i += unit.Length)
        {
            if (rnd.Next(8) == 0) unit[rnd.Next(unit.Length)] = (byte)rnd.Next(256);
            unit.AsSpan(0, (int)Math.Min(unit.Length, data.Length - i)).CopyTo(data.Slice((int)i));
        }
        // some incompressible stretches as well
        for (long i = 50_000_000; i + 3_000_000 < data.Length; i += 400_000_000) rnd.NextBytes(data.Slice((int)i, 3_000_000));
    }

    static void AssertSame(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, string what)
    {
        Assert.True(a.Length == b.Length, $"{what}: length {b.Length}, expected {a.Length}");
        for (long i = 0; i < a.Length; i += 1 << 26)
        {
            int n = (int)Math.Min(1 << 26, a.Length - i);
            Assert.True(a.Slice((int)i, n).SequenceEqual(b.Slice((int)i, n)), $"{what}: difference in the block at {i}");
        }
    }

    public static IEnumerable<object[]> Sizes => new[]
    {
        new object[] { 700_000_000 },
        new object[] { Array.MaxLength },               // 2^31 - 57: the largest array there is
        new object[] { (int)((1L << 31) - 65536 - 1) },
    };

    [Theory, MemberData(nameof(Sizes))]
    public void OneShotRoundTrips(int size)
    {
        if (Mode < 1) return;
        var data = GC.AllocateUninitializedArray<byte>(size);
        Fill(data, size & 0xFFFF);
        var back = GC.AllocateUninitializedArray<byte>(size);
        void Check(string name, Func<byte[]> compress, Func<byte[], int> decompress)
        {
            var sw = Stopwatch.StartNew();
            var z = compress();
            double tc = sw.Elapsed.TotalSeconds;
            sw.Restart();
            back.AsSpan(0, 1 << 20).Clear();
            int n = decompress(z);
            log.WriteLine($"{name}: {size} -> {z.Length} bytes, compress {size / 1e6 / tc:F0} MB/s, decompress {size / 1e6 / sw.Elapsed.TotalSeconds:F0} MB/s");
            Assert.Equal(size, n);
            AssertSame(data, back, name);
        }
        Check("deflate level 1", () => Deflater.Compress(data, 1, true), z => InflateCore.Decompress(z, back, true, true));
        Check("deflate level 6", () => Deflater.Compress(data, 6, true), z => InflateCore.Decompress(z, back, true, true));
        Check("zstd level 1", () => Zstd.Compress(data, 1, true), z => Zstd.Decompress(z, back));
        Check("zstd level 3", () => Zstd.Compress(data, 3, false), z => Zstd.Decompress(z, back));
        Check("zstd level 5", () => Zstd.Compress(data, 5, false), z => Zstd.Decompress(z, back));
        Check("xz level 1", () => Xz.Compress(data, 1, 0, XzCheck.Crc64), z => Xz.Decompress(z, back));
        if (size < 1_000_000_000 || Mode >= 2)
        {
            Check("xz level 6 + delta", () => Xz.Compress(data, 6, 4, XzCheck.Crc32), z => Xz.Decompress(z, back));
            Check("lzma alone", () => Xz.CompressLzma(data, 2), z => Xz.DecompressLzma(z, back));
            // unknown size: the growing-output paths up to the array limit
            var zz = Zstd.Compress(data, 1);
            AssertSame(data, Zstd.Decompress(zz), "zstd, growing output");
            var zx = Xz.Compress(data, 1);
            AssertSame(data, Xz.Decompress(zx), "xz, growing output");
            var zd = Deflater.Compress(data, 1, true);
            AssertSame(data, InflateCore.Decompress(zd, 0, true, true), "inflate, growing output");
        }
    }

    [Fact]
    public void OutputThatCannotFitAnArrayIsRefused()
    {
        if (Mode < 1) return;
        // 2 GiB of noise does not shrink: stored blocks plus framing exceed the largest array
        var data = GC.AllocateUninitializedArray<byte>(Array.MaxLength);
        new Random(5).NextBytes(data.AsSpan(0, 1 << 30));
        data.AsSpan(0, Array.MaxLength - (1 << 30)).CopyTo(data.AsSpan(1 << 30)); // 1 GiB apart: out of reach for deflate
        Assert.Throws<IOException>(() => Deflater.Compress(data, 0, true));
        Assert.Throws<IOException>(() => Deflater.Compress(data, 1, true));
        // zstd and xz reach across the gigabyte? Not with these windows, so they must refuse as well - or succeed and round trip
        foreach (var compress in Mode >= 2 ? new Func<byte[]>[] { () => Zstd.Compress(data, 1), () => Xz.Compress(data, 0, 0, XzCheck.None) } : new Func<byte[]>[] { () => Zstd.Compress(data, 1) })
        {
            try
            {
                var z = compress();
                Assert.True(z.Length <= Array.MaxLength);
            }
            catch (IOException) { }
        }
    }

    /// <summary>More than 4 GiB through the streaming compressor and the streaming inflater without holding it in memory.</summary>
    [Fact]
    public void StreamingBeyondFourGigabytes()
    {
        if (Mode < 1) return;
        long total = 5L << 30;
        var unit = new byte[1 << 20];
        Fill(unit, 3);
        foreach (int level in new[] { 0, 1, 6 })
        {
            var sink = new InflatingSink(unit);
            var sw = Stopwatch.StartNew();
            using (var z = new DeflaterStream(sink, level, true))
            {
                var rnd = new Random(1);
                for (long done = 0; done < total;)
                {
                    int at = (int)(done % unit.Length);
                    int n = (int)Math.Min(Math.Min(unit.Length - at, total - done), rnd.Next(1, 300_000));
                    z.Write(unit.AsSpan(at, n));
                    done += n;
                }
            }
            sink.Finish();
            log.WriteLine($"level {level}: {total} bytes -> {sink.CompressedBytes} compressed, {total / 1e6 / sw.Elapsed.TotalSeconds:F0} MB/s (deflate + inflate + compare)");
            Assert.Equal(total, sink.Inflater.TotalOut);
            Assert.Equal(sink.CompressedBytes, sink.Inflater.TotalIn);
            Assert.True(sink.Inflater.IsFinished);
            if (level == 0) Assert.True(sink.CompressedBytes > total);
        }
    }

    /// <summary>Feeds everything written to it into an Inflater and compares the output with the repeating unit.</summary>
    sealed class InflatingSink : Stream
    {
        public readonly Inflater Inflater = new Inflater();
        readonly byte[] unit, buffer = new byte[1 << 16];
        long position;
        public long CompressedBytes;
        bool done;

        public InflatingSink(byte[] unit) => this.unit = unit;

        public override void Write(ReadOnlySpan<byte> data)
        {
            CompressedBytes += data.Length;
            while (data.Length > 0 || !done)
            {
                var st = Inflater.Inflate(data, buffer, out int c, out int w);
                data = data.Slice(c);
                for (int i = 0; i < w;)
                {
                    int at = (int)(position % unit.Length);
                    int n = Math.Min(w - i, unit.Length - at);
                    if (!buffer.AsSpan(i, n).SequenceEqual(unit.AsSpan(at, n))) throw new Xunit.Sdk.XunitException("inflated data differs near " + position);
                    position += n; i += n;
                }
                if (st == InflateStatus.Done) { done = true; break; }
                if (st == InflateStatus.NeedInput && data.Length == 0) break;
            }
        }

        public void Finish() => Assert.True(done, "the stream did not end");
        public override void Write(byte[] b, int o, int n) => Write(b.AsSpan(o, n));
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] b, int o, int n) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
    }

    /// <summary>zstd's largest window (2 GiB, --long=31) with matches more than a gigabyte back.</summary>
    [Fact]
    public void ZstdWindowOfTwoGigabytes()
    {
        if (Mode < 1) return;
        int size = 1_900_000_000;
        var data = CompressionDecodeTests.LongRangeData(size, 1_200_000_000);
        var z = CompressionDecodeTests.ZstdTool(data, "-3", "--long=31");
        if (z == null) return;
        log.WriteLine($"zstd --long=31: {size} -> {z.Length}");
        Assert.True(z.Length < 1_350_000_000, "the tool did not use the far matches");
        var back = GC.AllocateUninitializedArray<byte>(size);
        Assert.Equal(size, Zstd.Decompress(z, back));
        AssertSame(data, back, "zstd --long=31");
        if (Mode >= 2)
        {
            var z22 = CompressionDecodeTests.ZstdTool(data.AsSpan(0, 300_000_000).ToArray(), "--ultra", "-22", "--long=31");
            Assert.Equal(300_000_000, Zstd.Decompress(z22!, back));
            AssertSame(data.AsSpan(0, 300_000_000), back.AsSpan(0, 300_000_000), "zstd --ultra -22 --long=31");
        }
    }
}
