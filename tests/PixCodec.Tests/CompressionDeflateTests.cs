using System.IO.Compression;
using PixCodec.Compression;
using Xunit;
using Xunit.Abstractions;
using static PixCodec.Tests.CompressionTestData;

namespace PixCodec.Tests;

/// <summary>The managed deflate compressor: its output must be readable by everyone and about as small as zlib's.</summary>
public class CompressionDeflateTests
{
    readonly ITestOutputHelper log;
    public CompressionDeflateTests(ITestOutputHelper o) => log = o;

    public static IEnumerable<object[]> ShapeNames => Shapes.Select(s => new object[] { s });

    [Theory, MemberData(nameof(ShapeNames))]
    public void EveryLevelRoundTrips(string shape)
    {
        var data = Make(shape);
        var streams = new List<byte[]>();
        for (int level = 0; level <= 9; level++)
        {
            var z = Deflater.Compress(data, level, true);
            streams.Add(z);
            // our decoder, one-shot and streaming
            var back = new byte[data.Length];
            Assert.Equal(data.Length, InflateCore.Decompress(z, back, true, true));
            Assert.True(data.AsSpan().SequenceEqual(back), $"{shape} level {level}: managed decode");
            Assert.True(data.AsSpan().SequenceEqual(InflateCore.Decompress(z, 0, true, true)));
            // the BCL (native zlib)
            Assert.True(data.AsSpan().SequenceEqual(BclDecompress(z)), $"{shape} level {level}: BCL decode");
            // raw deflate
            var raw = Deflater.Compress(data, level, false);
            Assert.Equal(z.Length - 6, raw.Length);
            Assert.True(data.AsSpan().SequenceEqual(BclDecompress(raw, false)), $"{shape} level {level}: BCL raw decode");
            Assert.True(data.AsSpan().SequenceEqual(InflateCore.Decompress(raw, 0, false, false)));
            // never much larger than the input (stored blocks are the fallback)
            Assert.True(z.Length <= data.Length + 6 + 5 * (data.Length / 65535 + 1), $"{shape} level {level}: {z.Length} bytes for {data.Length}");
        }
        // an independent zlib
        var py = PythonBatch("results=[zlib.decompress(x) for x in items]", streams);
        if (py == null) return;
        for (int level = 0; level <= 9; level++)
            Assert.True(data.AsSpan().SequenceEqual(py[level]), $"{shape} level {level}: python zlib decode");
    }

    /// <summary>Compressed sizes against zlib at the same level (python's zlib is the reference).</summary>
    [Fact]
    public void RatioIsCloseToZlib()
    {
        var shapes = Shapes.Where(s => Make(s).Length > 1000).ToArray();
        var items = new List<byte[]>();
        foreach (var s in shapes)
            for (int level = 1; level <= 9; level++) items.Add(new[] { (byte)level }.Concat(Make(s)).ToArray());
        var py = PythonBatch("results=[struct.pack('<i',len(zlib.compress(x[1:],x[0]))) for x in items]", items);
        if (py == null) return;
        int k = 0;
        long ours6 = 0, zlib6 = 0;
        foreach (var s in shapes)
        {
            var data = Make(s);
            var line = $"{s,-8} {data.Length,7}:";
            for (int level = 1; level <= 9; level++)
            {
                int zlibSize = BitConverter.ToInt32(py[k++]);
                int ours = Deflater.Compress(data, level, true).Length;
                line += $"  L{level} {ours}/{zlibSize}";
                if (level == 6) { ours6 += ours; zlib6 += zlibSize; }
                // levels 6 and 9 are the ones that matter for files; the others get a little more slack
                double slack = level == 6 || level == 9 ? 1.02 : 1.06;
                Assert.True(ours <= zlibSize * slack + 24, $"{s} level {level}: {ours} bytes, zlib {zlibSize}");
            }
            log.WriteLine(line);
        }
        log.WriteLine($"level 6 total: ours {ours6}, zlib {zlib6} ({100.0 * ours6 / zlib6:F2}%)");
    }

    /// <summary>Length limited Huffman codes must be complete prefix codes within the limit, whatever the frequencies.</summary>
    [Fact]
    public unsafe void HuffmanLengthsAreValid()
    {
        var rnd = new Random(8);
        for (int it = 0; it < 3000; it++)
        {
            int n = it % 3 == 0 ? 19 : it % 3 == 1 ? 30 : 286;
            int maxBits = n == 19 ? 7 : 15;
            var freq = new uint[n];
            int kind = rnd.Next(6);
            int used = rnd.Next(1, n + 1);
            ulong fa = 1, fb = 1;
            for (int i = 0; i < used; i++)
            {
                int sym = kind == 5 ? i : rnd.Next(n);
                switch (kind)
                {
                    case 0: freq[sym] = (uint)rnd.Next(1, 1000); break;
                    case 1: freq[sym] = 1; break;
                    case 2: freq[sym] = (uint)(1 << rnd.Next(0, 24)); break;          // very skewed
                    case 3: freq[sym] = (uint)Math.Max(1, 60000 >> rnd.Next(0, 17)); break;
                    default:                                                          // Fibonacci: the deepest possible tree
                        freq[sym] = (uint)Math.Min(fa, 1_000_000_000); (fa, fb) = (fb, fa + fb); break;
                }
            }
            var lens = new byte[n];
            fixed (uint* f = freq) fixed (byte* l = lens) Deflater.BuildLengths(f, n, maxBits, l);
            double kraft = 0;
            int coded = 0;
            for (int i = 0; i < n; i++)
            {
                Assert.InRange(lens[i], 0, maxBits);
                if (freq[i] != 0) Assert.NotEqual(0, lens[i]);
                if (lens[i] != 0) { kraft += Math.Pow(2, -lens[i]); coded++; }
            }
            Assert.True(coded >= 2);
            Assert.Equal(1.0, kraft, 12);
            // more frequent symbols never get longer codes
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    if (freq[i] > freq[j] && freq[j] != 0) Assert.True(lens[i] <= lens[j]);
        }
    }

    [Fact]
    public void HigherLevelsAreNotLarger()
    {
        foreach (var s in new[] { "text", "image", "mixed", "big" })
        {
            var data = Make(s);
            int l1 = Deflater.Compress(data, 1, true).Length, l6 = Deflater.Compress(data, 6, true).Length, l9 = Deflater.Compress(data, 9, true).Length;
            Assert.True(l6 <= l1 * 1.01 && l9 <= l6 * 1.005, $"{s}: {l1} {l6} {l9}");
        }
    }

    [Fact]
    public void ZlibHeaderAndTrailer()
    {
        var data = Make("text");
        foreach (int level in new[] { 0, 1, 5, 6, 9 })
        {
            var z = Deflater.Compress(data, level, true);
            Assert.Equal(0x78, z[0]);
            Assert.Equal(0, (z[0] << 8 | z[1]) % 31);
            Assert.Equal(Zlib.Adler32(data), System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(z.AsSpan(z.Length - 4)));
        }
    }

    [Fact]
    public void StreamingCompressor()
    {
        var rnd = new Random(17);
        foreach (var shape in new[] { "empty", "one", "text", "image", "random", "big", "zeros" })
            foreach (int level in new[] { 0, 1, 4, 6, 9 })
            {
                var data = Make(shape);
                var ms = new MemoryStream();
                using (var z = new DeflaterStream(ms, level, true))
                {
                    int i = 0;
                    while (i < data.Length)
                    {
                        int n = Math.Min(data.Length - i, rnd.Next(1, rnd.Next(3) == 0 ? 70_000 : 900));
                        if (rnd.Next(10) == 0) { z.WriteByte(data[i]); n = 1; }
                        else if (rnd.Next(2) == 0) z.Write(data, i, n);
                        else z.Write(data.AsSpan(i, n));
                        i += n;
                    }
                }
                Assert.True(ms.CanWrite, "the output stream must stay open");
                var bytes = ms.ToArray();
                Assert.True(data.AsSpan().SequenceEqual(InflateCore.Decompress(bytes, 0, true, true)), $"{shape} level {level}");
                Assert.True(data.AsSpan().SequenceEqual(BclDecompress(bytes)), $"{shape} level {level} (BCL)");
            }
    }

    [Fact]
    public void StreamingCompressorSlidesItsWindow()
    {
        // several megabytes: the internal buffer wraps many times; matches reach back across the moves
        var rnd = new Random(23);
        var unit = Make("big");
        var data = new byte[5_000_000];
        for (int i = 0; i < data.Length;)
        {
            int n = Math.Min(data.Length - i, rnd.Next(1, 40_000));
            unit.AsSpan(rnd.Next(unit.Length - n), n).CopyTo(data.AsSpan(i));
            i += n;
        }
        foreach (int level in new[] { 1, 6 })
        {
            var ms = new MemoryStream();
            using (var z = new DeflaterStream(ms, level, true))
                for (int i = 0; i < data.Length; i += 123_457) z.Write(data.AsSpan(i, Math.Min(123_457, data.Length - i)));
            var bytes = ms.ToArray();
            Assert.True(data.AsSpan().SequenceEqual(BclDecompress(bytes)));
            var back = new byte[data.Length];
            Assert.Equal(data.Length, InflateCore.Decompress(bytes, back, true, true));
            Assert.True(data.AsSpan().SequenceEqual(back));
            // streaming must cost next to nothing compared with compressing everything at once
            int oneShot = Deflater.Compress(data, level, true).Length;
            Assert.True(bytes.Length <= oneShot * 1.01, $"level {level}: streamed {bytes.Length}, one-shot {oneShot}");
        }
    }

    [Fact]
    public void StreamingCompressorFlush()
    {
        var data = Make("text");
        var ms = new MemoryStream();
        using (var z = new DeflaterStream(ms, 6, true))
        {
            z.Write(data.AsSpan(0, 10_000));
            z.Flush();
            // everything written so far must be decodable from what has reached the output
            var partial = new byte[10_000];
            var inf = new Inflater();
            inf.Inflate(ms.ToArray(), partial, out _, out int got);
            Assert.Equal(10_000, got);
            Assert.True(data.AsSpan(0, 10_000).SequenceEqual(partial));
            z.Flush(); // a second flush with nothing new is harmless
            z.Write(data.AsSpan(10_000));
        }
        Assert.True(data.AsSpan().SequenceEqual(BclDecompress(ms.ToArray())));
        Assert.True(data.AsSpan().SequenceEqual(InflateCore.Decompress(ms.ToArray(), 0, true, true)));
    }

    [Fact]
    public void LargeInputOneShot()
    {
        // > 64 KiB stored blocks, > 65536 symbols per block, many blocks
        var rnd = new Random(3);
        var data = new byte[3_000_000];
        rnd.NextBytes(data.AsSpan(0, 400_000));
        for (int i = 400_000; i < data.Length; i++) data[i] = (byte)(rnd.Next(4) == 0 ? rnd.Next(256) : data[i - 1 - rnd.Next(64)]);
        foreach (int level in new[] { 0, 1, 3, 6 })
        {
            var z = Deflater.Compress(data, level, true);
            Assert.True(data.AsSpan().SequenceEqual(BclDecompress(z)), "level " + level);
            var back = new byte[data.Length];
            Assert.Equal(data.Length, InflateCore.Decompress(z, back, true, true));
            Assert.True(data.AsSpan().SequenceEqual(back));
        }
    }

    /// <summary>Random data shapes x random levels through every pairing of the two implementations.</summary>
    [Fact]
    public void DifferentialFuzz()
    {
        var rnd = new Random(20261013);
        var bclLevels = new[] { CompressionLevel.NoCompression, CompressionLevel.Fastest, CompressionLevel.Optimal, CompressionLevel.SmallestSize };
        var forPython = new List<byte[]>();
        var originals = new List<byte[]>();
        int iterations = 400 * FuzzScale;
        for (int it = 0; it < iterations; it++)
        {
            var data = Fuzz(rnd);
            int level = rnd.Next(10);
            bool wrapper = rnd.Next(4) != 0;
            string what = $"iteration {it}, {data.Length} bytes, level {level}, wrapper {wrapper}";

            // managed -> managed and managed -> BCL
            var z = Deflater.Compress(data, level, wrapper);
            Assert.True(data.AsSpan().SequenceEqual(InflateCore.Decompress(z, 0, wrapper, true)), what + ": managed -> managed");
            Assert.True(data.AsSpan().SequenceEqual(BclDecompress(z, wrapper)), what + ": managed -> BCL");
            var exact = new byte[data.Length];
            Assert.Equal(data.Length, InflateCore.Decompress(z, exact, wrapper, true));
            Assert.True(data.AsSpan().SequenceEqual(exact), what + ": managed -> managed (sized)");
            if (wrapper && it % 8 == 0) { forPython.Add(z); originals.Add(data); }

            // BCL -> managed (one-shot and streaming with random piece sizes)
            var bz = BclCompress(data, bclLevels[rnd.Next(4)], wrapper);
            if (bz.Length == 0) continue; // the BCL writes nothing at all for empty input
            Assert.Equal(data.Length, InflateCore.Decompress(bz, exact, wrapper, true));
            Assert.True(data.AsSpan().SequenceEqual(exact), what + ": BCL -> managed");
            var inf = new Inflater(wrapper);
            var streamed = new byte[data.Length + 8];
            int ip = 0, op = 0;
            for (int guard = 0; ; guard++)
            {
                Assert.True(guard < 5_000_000, what + ": streaming made no progress");
                int ni = Math.Min(bz.Length - ip, rnd.Next(0, 2000)), no = Math.Min(streamed.Length - op, rnd.Next(0, 5000));
                var st = inf.Inflate(bz.AsSpan(ip, ni), streamed.AsSpan(op, no), out int c, out int w);
                ip += c; op += w;
                if (st == InflateStatus.Done) break;
            }
            Assert.Equal(data.Length, op);
            Assert.True(data.AsSpan().SequenceEqual(streamed.AsSpan(0, op)), what + ": BCL -> managed streaming");

            // managed streaming compressor -> BCL
            if (it % 4 == 0)
            {
                var ms = new MemoryStream();
                using (var s = new DeflaterStream(ms, level, wrapper))
                    for (int i = 0; i < data.Length;)
                    {
                        int n = Math.Min(data.Length - i, rnd.Next(1, 30_000));
                        s.Write(data.AsSpan(i, n));
                        if (rnd.Next(6) == 0) s.Flush();
                        i += n;
                    }
                Assert.True(data.AsSpan().SequenceEqual(BclDecompress(ms.ToArray(), wrapper)), what + ": managed stream -> BCL");
            }
        }
        var py = PythonBatch("results=[zlib.decompress(x) for x in items]", forPython);
        if (py == null) return;
        for (int i = 0; i < py.Count; i++) Assert.True(originals[i].AsSpan().SequenceEqual(py[i]), "python decode of fuzz stream " + i);
    }
}

/// <summary>The public Zlib facade with either backend. Not parallel with other tests: it switches the global backend.</summary>
[Collection("ZlibBackend")]
public class CompressionBackendTests
{
    [Fact]
    public void BothBackendsInterchange()
    {
        var old = Zlib.Backend;
        try
        {
            foreach (var shape in new[] { "empty", "one", "text", "image", "random", "zeros" })
            {
                var data = Make(shape);
                foreach (var enc in new[] { ZlibBackend.Managed, ZlibBackend.Bcl })
                    foreach (int level in new[] { 0, 1, 6, 9 })
                    {
                        Zlib.Backend = enc;
                        var z = Zlib.Compress(data, level);
                        var raw = Zlib.CompressRaw(data, level);
                        var ms = new MemoryStream();
                        using (var s = Zlib.CreateCompressor(ms, level)) s.Write(data);
                        var streamed = ms.ToArray();
                        foreach (var dec in new[] { ZlibBackend.Managed, ZlibBackend.Bcl })
                        {
                            Zlib.Backend = dec;
                            string what = $"{shape} level {level} {enc} -> {dec}";
                            foreach (var stream in new[] { z, streamed })
                            {
                                if (stream.Length == 0) { Assert.Equal(ZlibBackend.Bcl, enc); Assert.Empty(data); continue; }
                                Assert.True(data.AsSpan().SequenceEqual(Zlib.Decompress(stream)), what);
                                Assert.True(data.AsSpan().SequenceEqual(Zlib.Decompress(stream, data.Length)), what);
                                var dst = new byte[data.Length];
                                Assert.Equal(data.Length, Zlib.Decompress(stream, dst));
                                Assert.True(data.AsSpan().SequenceEqual(dst), what);
                                Assert.Equal(data.Length, Zlib.Decompress(stream, dst, verifyChecksum: false));
                            }
                            if (raw.Length == 0) continue;
                            var rdst = new byte[data.Length];
                            Assert.Equal(data.Length, Zlib.DecompressRaw(raw, rdst));
                            Assert.True(data.AsSpan().SequenceEqual(rdst), what + " raw");
                            Assert.True(data.AsSpan().SequenceEqual(Zlib.DecompressRaw(raw)), what + " raw");
                        }
                    }
            }
        }
        finally { Zlib.Backend = old; }
    }

    /// <summary>What the codecs rely on: both backends treat truncated and corrupt input the same way.</summary>
    [Fact]
    public void BackendsAgreeOnDamagedInput()
    {
        var old = Zlib.Backend;
        try
        {
            var data = Make("text");
            var z = Deflater.Compress(data, 6, true);
            foreach (var backend in new[] { ZlibBackend.Managed, ZlibBackend.Bcl })
            {
                Zlib.Backend = backend;
                // truncated: a short count, no exception
                var dst = new byte[data.Length];
                int n = Zlib.Decompress(z.AsSpan(0, z.Length / 2), dst);
                Assert.InRange(n, 1, data.Length - 1);
                Assert.True(data.AsSpan(0, n).SequenceEqual(dst.AsSpan(0, n)));
                // destination smaller than the data: filled
                Assert.Equal(100, Zlib.Decompress(z, new byte[100]));
                // wrong checksum: one of the exceptions the codecs catch
                var bad = (byte[])z.Clone();
                bad[^1] ^= 0x55;
                var ex = Record.Exception(() => Zlib.Decompress(bad, new byte[data.Length]));
                Assert.True(ex is ImageFormatException or InvalidDataException or IOException, backend + ": " + ex);
                // garbage
                ex = Record.Exception(() => Zlib.Decompress(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, new byte[10]));
                Assert.True(ex is ImageFormatException or InvalidDataException or IOException, backend + ": " + ex);
            }
        }
        finally { Zlib.Backend = old; }
    }

    [Fact]
    public void BackendSelection()
    {
        var env = Environment.GetEnvironmentVariable("PIXCODEC_ZLIB");
        var expected = string.Equals(env, "bcl", StringComparison.OrdinalIgnoreCase) ? ZlibBackend.Bcl
            : string.Equals(env, "managed", StringComparison.OrdinalIgnoreCase) ? ZlibBackend.Managed : Zlib.DefaultBackend;
        Assert.Equal(expected, Zlib.Backend);
    }
}
