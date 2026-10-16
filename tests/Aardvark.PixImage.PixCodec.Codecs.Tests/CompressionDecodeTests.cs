using Aardvark.ImageCodecs.Compression;
using Xunit;
using static Aardvark.ImageCodecs.Tests.CompressionTestData;

namespace Aardvark.ImageCodecs.Tests;

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

    /// <summary>Random data shapes compressed by the reference libraries with random settings (needs python).</summary>
    [Fact]
    public void ReferenceEncodersOnRandomData()
    {
        var rnd = new Random(77);
        var items = new List<byte[]>();
        for (int i = 0; i < 120 * FuzzScale; i++) items.Add(Fuzz(rnd));
        // per item: xz with a random preset / filter chain / check, .lzma, and (when imagecodecs is there) zstd
        var py = PythonBatch(
            "import random\nrandom.seed(5)\n" +
            "try:\n import imagecodecs\n zs=imagecodecs.zstd_encode\nexcept Exception:\n zs=None\n" +
            "results=[]\n" +
            "for x in items:\n" +
            " f=[]\n" +
            " if random.random()<0.4: f.append({'id':lzma.FILTER_DELTA,'dist':random.choice([1,2,3,4,6,8,100,256])})\n" +
            " lc=random.randint(0,4); lp=random.randint(0,4-lc)\n" +
            " f.append({'id':lzma.FILTER_LZMA2,'preset':random.randint(0,9),'lc':lc,'lp':lp,'pb':random.randint(0,4),'dict_size':random.choice([4096,65536,1<<20])})\n" +
            " results.append(lzma.compress(x,lzma.FORMAT_XZ,random.choice([lzma.CHECK_NONE,lzma.CHECK_CRC32,lzma.CHECK_CRC64,lzma.CHECK_SHA256]),filters=f))\n" +
            " results.append(lzma.compress(x,lzma.FORMAT_ALONE,preset=random.randint(0,9)))\n" +
            " results.append(zs(x,level=random.choice([-5,-1,1,2,3,4,5,7,9,12,15,19,22])) if zs else b'')\n" +
            " results.append(zlib.compress(x,random.randint(0,9)))\n", items);
        if (py == null) return;
        Assert.Equal(items.Count * 4, py.Count);
        for (int i = 0; i < items.Count; i++)
        {
            var data = items[i];
            Assert.True(data.AsSpan().SequenceEqual(Xz.Decompress(py[4 * i])), $"xz item {i} ({data.Length} bytes)");
            var dst = new byte[data.Length];
            Assert.Equal(data.Length, Xz.Decompress(py[4 * i], dst));
            Assert.True(data.AsSpan().SequenceEqual(dst), $"xz item {i}");
            Assert.True(data.AsSpan().SequenceEqual(Xz.DecompressLzma(py[4 * i + 1])), $"lzma item {i} ({data.Length} bytes)");
            Assert.Equal(data.Length, Xz.DecompressLzma(py[4 * i + 1], dst));
            Assert.True(data.AsSpan().SequenceEqual(dst), $"lzma item {i}");
            if (py[4 * i + 2].Length > 0)
            {
                Assert.True(data.AsSpan().SequenceEqual(Zstd.Decompress(py[4 * i + 2])), $"zstd item {i} ({data.Length} bytes)");
                Assert.Equal(data.Length, Zstd.Decompress(py[4 * i + 2], dst));
                Assert.True(data.AsSpan().SequenceEqual(dst), $"zstd item {i}");
            }
            Assert.True(data.AsSpan().SequenceEqual(InflateCore.Decompress(py[4 * i + 3], 0, true, true)), $"zlib item {i} ({data.Length} bytes)");
        }
    }

    [Fact]
    public void XzUnsupportedFilter()
    {
        // an x86 filter id rewritten to an id nobody has assigned (with the block header CRC fixed up)
        var z = Load("text__x86.xz");
        int header = 12, size = (z[header] + 1) * 4;
        Assert.Equal(0x04, z[header + 2]);
        z[header + 2] = 0x3E;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(z.AsSpan(header + size - 4), Zlib.Crc32(z.AsSpan(header, size - 4)));
        Assert.Throws<ImageNotSupportedException>(() => Xz.Decompress(z, new byte[4000]));
        Assert.Throws<ImageNotSupportedException>(() => Xz.Decompress(z));
    }

    /// <summary>Check ids nobody has defined yet have a fixed size by the format; such a check is skipped, not an error.</summary>
    [Fact]
    public void XzUnknownCheckIdIsSkipped()
    {
        foreach (var (file, newId) in new[] { ("text__crc32.xz", 2), ("text__crc32.xz", 3), ("text__p6.xz", 5), ("text__p6.xz", 6), ("text__sha256.xz", 11), ("text__sha256.xz", 12) })
        {
            var z = Load(file);
            // stream flags sit in the header (bytes 6..7, CRC at 8) and in the footer (bytes -4..-3, CRC over the 6 bytes before the magic)
            z[7] = (byte)newId;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(z.AsSpan(8), Zlib.Crc32(z.AsSpan(6, 2)));
            z[^3] = (byte)newId;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(z.AsSpan(z.Length - 12), Zlib.Crc32(z.AsSpan(z.Length - 8, 6)));
            Assert.True(Expected(file).AsSpan().SequenceEqual(Xz.Decompress(z)), $"{file} with check id {newId}");
            // the bytes of an unknown check cannot be verified
            z[^30] ^= 0xFF;
        }
    }

    /// <summary>The BCJ fixtures only prove something if every filter really converts instructions in that data.</summary>
    [Fact]
    public void BcjFiltersAllChangeTheTestData()
    {
        var data = Load("bcj.bin");
        for (int id = BcjFilters.X86; id <= BcjFilters.RiscV; id++)
        {
            var copy = (byte[])data.Clone();
            BcjFilters.Decode(id, copy, 0);
            int changed = 0;
            for (int i = 0; i < data.Length; i++) if (copy[i] != data[i]) changed++;
            Assert.True(changed > 12, $"filter {id}: only {changed} bytes changed");
            // buffers shorter than one instruction are left alone
            for (int n = 0; n < 20; n++) BcjFilters.Decode(id, data.AsSpan(0, n).ToArray(), 0);
        }
    }

    public static IEnumerable<object[]> ZstdDictFiles => Fixtures(".zstd");

    /// <summary>Frames written by <c>zstd -D</c> with a trained (structured) and a plain-content dictionary.</summary>
    [Theory, MemberData(nameof(ZstdDictFiles))]
    public void DecompressZstdWithDictionary(string name)
    {
        var z = Load(name);
        var expected = Expected(name);
        bool trained = name.Contains("trained");
        var dict = Load(trained ? "trained.zdict" : "rawcontent.zdict");
        var other = Load(trained ? "rawcontent.zdict" : "trained.zdict");
        var dst = new byte[expected.Length];
        Assert.Equal(expected.Length, Zstd.Decompress(z, dst, dict));
        Assert.True(expected.AsSpan().SequenceEqual(dst));
        Assert.True(expected.AsSpan().SequenceEqual(Zstd.DecompressWithDictionary(z, dict)));
        // without the dictionary, or with the wrong one, the frame must not decode
        bool hasId = trained && !name.Contains("noid");
        if (hasId) Assert.Throws<ImageNotSupportedException>(() => Zstd.Decompress(z, dst));
        else Assert.Throws<ImageFormatException>(() => Zstd.Decompress(z, dst));
        Assert.Throws<ImageFormatException>(() => Zstd.Decompress(z, dst, other));
        // damage: same rules as everywhere
        var rnd = new Random(name.Length);
        for (int i = 0; i < 150; i++)
        {
            var bad = (byte[])z.Clone();
            bad[rnd.Next(bad.Length)] ^= (byte)(1 << rnd.Next(8));
            try { Zstd.Decompress(bad, dst, dict); } catch (ImageFormatException) { } catch (ImageNotSupportedException) { }
            var badDict = (byte[])dict.Clone();
            badDict[rnd.Next(Math.Min(badDict.Length, 400))] ^= (byte)(1 << rnd.Next(8));
            try { Zstd.Decompress(z, dst, badDict); } catch (ImageFormatException) { } catch (ImageNotSupportedException) { }
            try { Zstd.Decompress(z, dst, dict.AsSpan(0, rnd.Next(dict.Length))); } catch (ImageFormatException) { } catch (ImageNotSupportedException) { }
        }
    }

    [Fact]
    public void ZstdDictionaryRules()
    {
        // frame header: magic, descriptor with a 1 byte dictionary id, window descriptor, dictionary id 7
        var z = new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 0x01, 0x00, 0x07, 0x01, 0x00, 0x00 };
        Assert.Throws<ImageNotSupportedException>(() => Zstd.Decompress(z, new byte[10]));
        // dictionary id 0 means "none"
        z[6] = 0;
        Assert.Equal(0, Zstd.Decompress(z, new byte[10]));
        // a structured dictionary with another id is refused; frames that need none ignore a supplied dictionary
        z[6] = 7;
        var dict = Load("trained.zdict");
        Assert.Throws<ImageFormatException>(() => Zstd.Decompress(z, new byte[10], dict));
        var plain = Load("text__l3.zst");
        Assert.True(Expected("text__l3.zst").AsSpan().SequenceEqual(Zstd.DecompressWithDictionary(plain, dict)));
    }

    /// <summary>
    /// Long distance matching: tens of megabytes generated here (not stored in the repository), compressed by the
    /// zstd tool with a 128 MiB window, so that matches reach far beyond any block or the usual 8 MiB window.
    /// </summary>
    [Fact]
    public void ZstdLongDistanceMatches()
    {
        int size = 40_000_000;
        var data = LongRangeData(size, 9_000_000);
        var z = ZstdTool(data, "-3", "--long=27");
        if (z == null) return; // zstd tool not installed
        Assert.True(z.Length < size / 3, "the tool did not use the far matches: " + z.Length);
        var dst = new byte[size];
        Assert.Equal(size, Zstd.Decompress(z, dst));
        Assert.True(data.AsSpan().SequenceEqual(dst));
        // our own compressor's large-window levels on the same data
        var own = Zstd.Compress(data.AsSpan(0, 24_000_000), 13);
        Assert.True(own.Length < 24_000_000 / 2, "own level 13: " + own.Length);
        Assert.Equal(24_000_000, Zstd.Decompress(own, dst));
        Assert.True(data.AsSpan(0, 24_000_000).SequenceEqual(dst.AsSpan(0, 24_000_000)));
    }

    /// <summary>Random blocks, each later repeated (with small edits) at a distance of about <paramref name="distance"/>.</summary>
    internal static byte[] LongRangeData(long size, int distance)
    {
        var data = new byte[size];
        var rnd = new Random(99);
        rnd.NextBytes(data.AsSpan(0, (int)Math.Min(size, distance)));
        for (long i = distance; i < size;)
        {
            int n = (int)Math.Min(size - i, rnd.Next(2000, 200_000));
            data.AsSpan((int)(i - distance), n).CopyTo(data.AsSpan((int)i));
            for (int k = 0; k < n; k += rnd.Next(500, 4000)) data[i + k] ^= 0x55;
            i += n;
        }
        return data;
    }

    internal static byte[]? ZstdTool(byte[] data, params string[] args)
    {
        var dir = Path.Combine(TestUtil.FixtureDir, "_tmp");
        Directory.CreateDirectory(dir);
        var f = Path.Combine(dir, Guid.NewGuid().ToString("N"));
        try
        {
            using (var fs = File.Create(f)) fs.Write(data);
            var psi = new System.Diagnostics.ProcessStartInfo("zstd") { RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            foreach (var a in new[] { "-q", "-f", "-T0", f, "-o", f + ".zst" }) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi)!;
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.True(p.ExitCode == 0, "zstd failed: " + err);
            return File.ReadAllBytes(f + ".zst");
        }
        catch (System.ComponentModel.Win32Exception) { Oracles.Missing("zstd", "the zstd command line tool"); return null; }
        finally { File.Delete(f); File.Delete(f + ".zst"); }
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
