using PixCodec.Compression;
using Xunit;
using static PixCodec.Tests.CompressionTestData;

namespace PixCodec.Tests;

/// <summary>Every decoder against streams written by the reference implementations (zlib, zstd, liblzma).</summary>
public class CompressionDecodeTests
{
    public static IEnumerable<object[]> ZlibFiles => Fixtures(".zlib");
    public static IEnumerable<object[]> DeflateFiles => Fixtures(".deflate");
    public static IEnumerable<object[]> ZstdFiles => Fixtures(".zst");
    public static IEnumerable<object[]> XzFiles => Fixtures(".xz");
    public static IEnumerable<object[]> LzmaFiles => Fixtures(".lzma");

    [Fact]
    public void FixturesExist()
    {
        Assert.True(ZlibFiles.Count() > 60);
        Assert.True(ZstdFiles.Count() > 50);
        Assert.True(XzFiles.Count() > 50);
        Assert.True(LzmaFiles.Count() > 10);
    }

    [Theory, MemberData(nameof(ZlibFiles))]
    public void InflateZlib(string name)
    {
        var z = Load(name);
        var expected = Expected(name);
        // exact destination
        var dst = new byte[expected.Length];
        Assert.Equal(expected.Length, InflateCore.Decompress(z, dst, true, true));
        Assert.True(expected.AsSpan().SequenceEqual(dst));
        // larger destination: stops at the end of the stream
        var big = new byte[expected.Length + 1000];
        Assert.Equal(expected.Length, InflateCore.Decompress(z, big, true, true));
        Assert.True(expected.AsSpan().SequenceEqual(big.AsSpan(0, expected.Length)));
        // smaller destination: filled, nothing written past it
        if (expected.Length > 1)
        {
            int n = expected.Length * 2 / 3;
            var small = new byte[n + 64];
            small.AsSpan(n).Fill(0xA5);
            Assert.Equal(n, InflateCore.Decompress(z, small.AsSpan(0, n), true, true));
            Assert.True(expected.AsSpan(0, n).SequenceEqual(small.AsSpan(0, n)));
            Assert.True(small.AsSpan(n).IndexOfAnyExcept((byte)0xA5) < 0, "wrote past the destination");
        }
        // unknown size, with and without a hint
        Assert.True(expected.AsSpan().SequenceEqual(InflateCore.Decompress(z, 0, true, true)));
        Assert.True(expected.AsSpan().SequenceEqual(InflateCore.Decompress(z, expected.Length, true, true)));
        Assert.True(expected.AsSpan().SequenceEqual(InflateCore.Decompress(z, int.MaxValue, true, true)));
        // the same bytes without the wrapper are a raw deflate stream
        var raw = z.AsSpan(2, z.Length - 6);
        Assert.Equal(expected.Length, InflateCore.Decompress(raw, dst, false, false));
        Assert.True(expected.AsSpan().SequenceEqual(dst));
    }

    [Theory, MemberData(nameof(DeflateFiles))]
    public void InflateRaw(string name)
    {
        var z = Load(name);
        var expected = Expected(name);
        var dst = new byte[expected.Length];
        Assert.Equal(expected.Length, InflateCore.Decompress(z, dst, false, false));
        Assert.True(expected.AsSpan().SequenceEqual(dst));
        Assert.True(expected.AsSpan().SequenceEqual(InflateCore.Decompress(z, 0, false, false)));
    }

    [Theory, MemberData(nameof(ZlibFiles))]
    public void InflateStreaming(string name)
    {
        var z = Load(name);
        var expected = Expected(name);
        var rnd = new Random(name.Length * 31 + z.Length);
        // (input piece size, output piece size); 0 = random sizes
        foreach (var (inStep, outStep) in new[] { (1, 1), (1, 4096), (4096, 1), (7, 13), (8192, 300), (65536, 100_000), (0, 0), (z.Length + 1, expected.Length + 1) })
        {
            var inf = new Inflater();
            var result = new byte[expected.Length + 16];
            int inPos = 0, outPos = 0, guard = 0;
            InflateStatus status = InflateStatus.NeedInput;
            while (status != InflateStatus.Done)
            {
                Assert.True(++guard < 10_000_000, "no progress");
                int ni = Math.Min(z.Length - inPos, inStep == 0 ? rnd.Next(0, 300) : inStep);
                int no = Math.Min(result.Length - outPos, outStep == 0 ? rnd.Next(0, 700) : outStep);
                status = inf.Inflate(z.AsSpan(inPos, ni), result.AsSpan(outPos, no), out int consumed, out int written);
                inPos += consumed; outPos += written;
                Assert.InRange(consumed, 0, ni);
                Assert.InRange(written, 0, no);
                if (status == InflateStatus.NeedInput) Assert.Equal(ni, consumed);
                if (status == InflateStatus.NeedInput && inPos == z.Length && ni == 0 && inStep != 0) Assert.Fail("stream did not end");
            }
            Assert.True(inf.IsFinished);
            Assert.Equal(expected.Length, outPos);
            Assert.Equal(z.Length, inPos);
            Assert.Equal(expected.Length, inf.TotalOut);
            Assert.Equal(z.Length, inf.TotalIn);
            Assert.True(expected.AsSpan().SequenceEqual(result.AsSpan(0, outPos)), $"steps {inStep}/{outStep}");
            // further calls stay Done and produce nothing
            Assert.Equal(InflateStatus.Done, inf.Inflate(default, result, out int c2, out int w2));
            Assert.Equal((0, 0), (c2, w2));
        }
    }

    [Fact]
    public void InflaterReuseAndRawMode()
    {
        var a = Load("text__l6.zlib");
        var b = Load("image__raw.deflate");
        var inf = new Inflater();
        var buf = new byte[100_000];
        for (int round = 0; round < 3; round++)
        {
            inf.Reset();
            Assert.Equal(InflateStatus.Done, inf.Inflate(a, buf, out _, out int n));
            Assert.True(Expected("text__l6.zlib").AsSpan().SequenceEqual(buf.AsSpan(0, n)));
        }
        var raw = Zlib.CreateInflater(zlibWrapper: false);
        Assert.Equal(InflateStatus.Done, raw.Inflate(b, buf, out int used, out int m));
        Assert.Equal(b.Length, used);
        Assert.True(Expected("image__raw.deflate").AsSpan().SequenceEqual(buf.AsSpan(0, m)));
    }

    [Fact]
    public void InflaterReportsTrailingInput()
    {
        // data after the end of the stream is not consumed
        var z = Load("text__l6.zlib").Concat(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }).ToArray();
        var inf = new Inflater();
        var buf = new byte[100_000];
        Assert.Equal(InflateStatus.Done, inf.Inflate(z, buf, out int used, out _));
        Assert.Equal(z.Length - 9, used);
    }

    /// <summary>The use the streaming API was designed for: IDAT pieces in, one scanline at a time out.</summary>
    [Fact]
    public void InflaterRowByRow()
    {
        var raw = FilteredImage(new Random(4), 333, 200, 3, false);
        int rowBytes = 333 * 3 + 1;
        var z = Deflater.Compress(raw, 6, true);
        var chunks = new List<byte[]>();
        for (int i = 0; i < z.Length; i += 8192) chunks.Add(z.AsSpan(i, Math.Min(8192, z.Length - i)).ToArray());
        var inf = new Inflater();
        var row = new byte[rowBytes];
        int rows = 0, filled = 0;
        foreach (var chunk in chunks)
        {
            int p = 0;
            while (p < chunk.Length || rows < 200)
            {
                var st = inf.Inflate(chunk.AsSpan(p), row.AsSpan(filled), out int c, out int w);
                p += c; filled += w;
                if (filled == rowBytes)
                {
                    Assert.True(raw.AsSpan(rows * rowBytes, rowBytes).SequenceEqual(row), "row " + rows);
                    rows++; filled = 0;
                }
                if (st == InflateStatus.NeedInput || st == InflateStatus.Done) break;
            }
        }
        Assert.Equal(200, rows);
        Assert.True(inf.IsFinished);
    }

    [Theory, MemberData(nameof(ZstdFiles))]
    public void DecompressZstd(string name)
    {
        var z = Load(name);
        var expected = Expected(name);
        var dst = new byte[expected.Length];
        Assert.Equal(expected.Length, Zstd.Decompress(z, dst));
        Assert.True(expected.AsSpan().SequenceEqual(dst));
        var big = new byte[expected.Length + 777];
        Assert.Equal(expected.Length, Zstd.Decompress(z, big));
        Assert.True(expected.AsSpan().SequenceEqual(big.AsSpan(0, expected.Length)));
        Assert.True(expected.AsSpan().SequenceEqual(Zstd.Decompress(z)));
        if (expected.Length > 0)
            Assert.Throws<ImageFormatException>(() => Zstd.Decompress(z, new byte[expected.Length - 1]));
    }

    [Theory, MemberData(nameof(XzFiles))]
    public void DecompressXz(string name)
    {
        var z = Load(name);
        var expected = Expected(name);
        Assert.True(Xz.IsXz(z));
        var dst = new byte[expected.Length];
        Assert.Equal(expected.Length, Xz.Decompress(z, dst));
        Assert.True(expected.AsSpan().SequenceEqual(dst));
        var big = new byte[expected.Length + 777];
        Assert.Equal(expected.Length, Xz.Decompress(z, big));
        Assert.True(expected.AsSpan().SequenceEqual(big.AsSpan(0, expected.Length)));
        Assert.True(expected.AsSpan().SequenceEqual(Xz.Decompress(z)));
        if (expected.Length > 0)
            Assert.Throws<ImageFormatException>(() => Xz.Decompress(z, new byte[expected.Length - 1]));
    }

    [Theory, MemberData(nameof(LzmaFiles))]
    public void DecompressLzmaAlone(string name)
    {
        var z = Load(name);
        var expected = Expected(name);
        var dst = new byte[expected.Length];
        Assert.Equal(expected.Length, Xz.DecompressLzma(z, dst));
        Assert.True(expected.AsSpan().SequenceEqual(dst));
        var big = new byte[expected.Length + 777];
        Assert.Equal(expected.Length, Xz.DecompressLzma(z, big));
        Assert.True(expected.AsSpan().SequenceEqual(big.AsSpan(0, expected.Length)));
        Assert.True(expected.AsSpan().SequenceEqual(Xz.DecompressLzma(z)));
        if (expected.Length > 0)
            Assert.Throws<ImageFormatException>(() => Xz.DecompressLzma(z, new byte[expected.Length - 1]));
    }

    [Fact]
    public void XzUnsupportedFilter()
    {
        var z = Load("text__bcj.xz.unsupported");
        Assert.Throws<ImageNotSupportedException>(() => Xz.Decompress(z, new byte[4000]));
        Assert.Throws<ImageNotSupportedException>(() => Xz.Decompress(z));
    }

    [Fact]
    public void ZstdDictionaryIsNotSupported()
    {
        // frame header: magic, descriptor with a 1 byte dictionary id, window descriptor, dictionary id 7
        var z = new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 0x01, 0x00, 0x07, 0x01, 0x00, 0x00 };
        Assert.Throws<ImageNotSupportedException>(() => Zstd.Decompress(z, new byte[10]));
        // dictionary id 0 means "none"
        z[6] = 0;
        Assert.Equal(0, Zstd.Decompress(z, new byte[10]));
    }

    [Fact]
    public void EmptyInputs()
    {
        Assert.Equal(0, InflateCore.Decompress(default, new byte[10], true, true));
        Assert.Empty(InflateCore.Decompress(default, 0, true, true));
        Assert.Equal(0, Zstd.Decompress(default, new byte[10]));
        Assert.Empty(Zstd.Decompress(default));
        Assert.Equal(0, Xz.Decompress(default, new byte[10]));
        Assert.Empty(Xz.Decompress(default));
        Assert.Throws<ImageFormatException>(() => Xz.DecompressLzma(default, new byte[10]));
    }
}
