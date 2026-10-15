using System.Buffers.Binary;
using System.Diagnostics;
using Aardvark.ImageCodecs.Compression;
using Xunit;
using static Aardvark.ImageCodecs.Tests.CompressionTestData;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>
/// Damaged input: only ImageFormatException / ImageNotSupportedException or a (possibly short or different)
/// successful result, never another exception, never a write outside the destination, never a hang.
/// </summary>
public class CompressionRobustnessTests
{
    delegate int Decoder(ReadOnlySpan<byte> source, Span<byte> destination);

    static readonly Decoder InflateZlib = (s, d) => InflateCore.Decompress(s, d, true, true);
    static readonly Decoder InflateRaw = (s, d) => InflateCore.Decompress(s, d, false, false);
    static readonly Decoder ZstdDec = Zstd.Decompress;
    static readonly Decoder XzDec = Xz.Decompress;
    static readonly Decoder LzmaDec = Xz.DecompressLzma;

    static Decoder For(string name) => Path.GetExtension(name) switch
    {
        ".zlib" => InflateZlib, ".deflate" => InflateRaw, ".zst" => ZstdDec, ".xz" => XzDec, ".lzma" => LzmaDec,
        _ => throw new ArgumentException(name),
    };

    public static IEnumerable<object[]> AllFiles => Fixtures(".zlib", ".deflate", ".zst", ".xz", ".lzma");

    const byte Guard = 0xC7;

    /// <summary>Runs a decoder on damaged input inside guard bytes. Returns the count, or -1 for a format error.</summary>
    static int Run(Decoder decode, ReadOnlySpan<byte> source, byte[] buffer, int room, string what)
    {
        buffer.AsSpan(room).Fill(Guard);
        int n;
        try { n = decode(source, buffer.AsSpan(0, room)); }
        catch (ImageFormatException) { n = -1; }
        catch (ImageNotSupportedException) { n = -1; }
        catch (Exception e) { throw new Xunit.Sdk.XunitException($"{what}: unexpected {e.GetType().Name}: {e.Message}\n{e.StackTrace}"); }
        Assert.True(n <= room, what + ": returned more than the destination holds");
        Assert.True(buffer.AsSpan(room).IndexOfAnyExcept(Guard) < 0, what + ": wrote past the destination");
        return n;
    }

    static IEnumerable<int> Offsets(int length, int many, Random rnd)
    {
        if (length <= 600) { for (int i = 0; i < length; i++) yield return i; yield break; }
        for (int i = 0; i < 64; i++) yield return i;              // headers
        for (int i = length - 40; i < length; i++) yield return i; // trailers
        for (int i = 0; i < many; i++) yield return rnd.Next(length);
    }

    [Theory, MemberData(nameof(AllFiles))]
    public void Truncation(string name)
    {
        var z = Load(name);
        var expected = Expected(name);
        var decode = For(name);
        var rnd = new Random(name.GetHashCode(StringComparison.Ordinal) ^ 0x51ED);
        var buffer = new byte[expected.Length + 64];
        bool inflate = name.EndsWith(".zlib") || name.EndsWith(".deflate");
        // a multi frame / multi stream file cut at a frame boundary is a valid shorter file
        bool multi = name.Contains("multi") || name.Contains("flush");
        var sw = Stopwatch.StartNew();
        foreach (int cut in Offsets(z.Length, 150, rnd))
        {
            int n = Run(decode, z.AsSpan(0, cut), buffer, expected.Length, $"{name} cut at {cut}");
            if (inflate)
            {
                // inflate reports truncation as a short count: whatever it returns is a correct prefix
                if (n >= 0) Assert.True(expected.AsSpan(0, n).SequenceEqual(buffer.AsSpan(0, n)), $"{name} cut at {cut}: wrong prefix");
                if (n == expected.Length && expected.Length > 0)
                    Assert.True(cut >= z.Length - (name.EndsWith(".zlib") ? 4 : 1) - 5, $"{name} cut at {cut}: complete output from truncated input");
            }
            else if (!multi && !name.Contains("sized.lzma"))
                Assert.True(n == -1 || cut == 0, $"{name} cut at {cut}: truncated input was accepted ({n} bytes)");
            else if (n >= 0)
                Assert.True(expected.AsSpan(0, n).SequenceEqual(buffer.AsSpan(0, n)), $"{name} cut at {cut}: wrong prefix");
        }
        Assert.True(sw.Elapsed.TotalSeconds < 30, name + ": too slow");
    }

    [Theory, MemberData(nameof(AllFiles))]
    public void ByteFlips(string name)
    {
        var z = Load(name);
        var expected = Expected(name);
        var decode = For(name);
        var rnd = new Random(name.GetHashCode(StringComparison.Ordinal) ^ 0x0F11B5);
        var buffer = new byte[expected.Length + 4096 + 64];
        var sw = Stopwatch.StartNew();
        int flips = z.Length == 0 ? 0 : 220;
        for (int i = 0; i < flips; i++)
        {
            var bad = (byte[])z.Clone();
            // mostly single bytes (early ones more often: that is where the headers and tables are), sometimes a burst
            int at = rnd.Next(3) == 0 ? rnd.Next(Math.Min(z.Length, 48)) : rnd.Next(z.Length);
            int burst = rnd.Next(8) == 0 ? rnd.Next(2, 9) : 1;
            for (int k = 0; k < burst && at + k < bad.Length; k++)
                bad[at + k] = rnd.Next(4) == 0 ? (byte)rnd.Next(256) : (byte)(bad[at + k] ^ (1 << rnd.Next(8)));
            // alternate between an exact destination and one with spare room
            int room = expected.Length + (i % 2 == 0 ? 0 : 4096);
            Run(decode, bad, buffer, room, $"{name} flip {i} at {at}");
        }
        Assert.True(sw.Elapsed.TotalSeconds < 30, name + ": too slow");
    }

    /// <summary>The growing-output overloads on damaged input: bounded output, same exception rules.</summary>
    [Theory, MemberData(nameof(AllFiles))]
    public void ByteFlipsUnknownSize(string name)
    {
        var z = Load(name);
        if (z.Length == 0) return;
        var expected = Expected(name);
        var rnd = new Random(name.GetHashCode(StringComparison.Ordinal) ^ 0x6A09);
        string ext = Path.GetExtension(name);
        long bound = Math.Max(expected.Length, 1 << 16) * 64L + (long)z.Length * 1100;
        for (int i = 0; i < 60; i++)
        {
            var bad = (byte[])z.Clone();
            int at = rnd.Next(2) == 0 ? rnd.Next(Math.Min(z.Length, 32)) : rnd.Next(z.Length);
            bad[at] = rnd.Next(4) == 0 ? (byte)rnd.Next(256) : (byte)(bad[at] ^ (1 << rnd.Next(8)));
            try
            {
                byte[] r = ext switch
                {
                    ".zlib" => InflateCore.Decompress(bad, 0, true, true),
                    ".deflate" => InflateCore.Decompress(bad, 0, false, false),
                    ".zst" => Zstd.Decompress(bad),
                    ".xz" => Xz.Decompress(bad),
                    _ => Xz.DecompressLzma(bad),
                };
                Assert.True(r.Length <= bound, $"{name} flip {i} at {at}: {r.Length} bytes of output");
            }
            catch (ImageFormatException) { }
            catch (ImageNotSupportedException) { }
        }
    }

    [Theory, MemberData(nameof(AllFiles))]
    public void StreamingInflateOfDamagedInput(string name)
    {
        if (!name.EndsWith(".zlib")) return;
        var z = Load(name);
        var expected = Expected(name);
        var rnd = new Random(name.GetHashCode(StringComparison.Ordinal) ^ 0x33);
        var buffer = new byte[4096];
        for (int i = 0; i < 40 && z.Length > 0; i++)
        {
            var bad = (byte[])z.Clone();
            int at = rnd.Next(z.Length);
            bad[at] ^= (byte)(1 << rnd.Next(8));
            int cut = rnd.Next(3) == 0 ? rnd.Next(z.Length) : z.Length;
            var inf = new Inflater();
            long total = 0;
            int p = 0;
            try
            {
                for (int guard = 0; guard < 10_000_000; guard++)
                {
                    int ni = Math.Min(cut - p, rnd.Next(1, 3000));
                    var st = inf.Inflate(bad.AsSpan(p, ni), buffer, out int c, out int w);
                    p += c; total += w;
                    if (st == InflateStatus.Done || st == InflateStatus.NeedInput && p == cut) break;
                }
            }
            catch (ImageFormatException) { }
            // deflate cannot expand beyond 1032:1
            Assert.True(total <= (long)z.Length * 1032 + 1024, $"{name}: {total} bytes from {z.Length}");
        }
    }

    // ---------------------------------------------------------------------------------- hand made bad streams

    static byte[] Z(params byte[] deflate) => new byte[] { 0x78, 0x9C }.Concat(deflate).Concat(new byte[4]).ToArray();

    [Fact]
    public void InflateRejectsStructuralErrors()
    {
        var dst = new byte[1000];
        // reserved block type 3
        Assert.Throws<ImageFormatException>(() => InflateCore.Decompress(Z(0x07, 0, 0, 0), dst, true, true));
        // stored block whose length and complement disagree
        Assert.Throws<ImageFormatException>(() => InflateCore.Decompress(Z(0x01, 0x05, 0x00, 0x00, 0x00, 1, 2, 3, 4, 5), dst, true, true));
        // zlib header: wrong method, wrong check bits, preset dictionary, window too large
        Assert.Throws<ImageFormatException>(() => InflateCore.Decompress(new byte[] { 0x79, 0x9C, 3, 0 }, dst, true, true));
        Assert.Throws<ImageFormatException>(() => InflateCore.Decompress(new byte[] { 0x78, 0x9D, 3, 0 }, dst, true, true));
        Assert.Throws<ImageFormatException>(() => InflateCore.Decompress(new byte[] { 0x78, 0xBB, 0, 0, 0, 0, 3, 0 }, dst, true, true));
        Assert.Throws<ImageFormatException>(() => InflateCore.Decompress(new byte[] { 0x88, 0x1C, 3, 0 }, dst, true, true));
        // fixed block: a match before any output (distance too far back)
        // bits: final=1, type=01, then length code 257 (7 bits 0000001), distance code 0 (5 bits)
        Assert.Throws<ImageFormatException>(() => InflateCore.Decompress(new byte[] { 0x03, 0x02, 0x00 }, dst, false, false));
        // fixed block using the reserved length symbol 286 (8 bit code 11000110)
        Assert.Throws<ImageFormatException>(() => InflateCore.Decompress(new byte[] { 0x1B, 0x03, 0x00, 0x00 }, dst, false, false));
        // dynamic block: HLIT says 288 codes (more than 286)
        Assert.Throws<ImageFormatException>(() => InflateCore.Decompress(new byte[] { 0xFD, 0xFF, 0xFF, 0xFF, 0xFF }, dst, false, false));
        // dynamic block: all code length codes have length 1 (over-subscribed)
        Assert.Throws<ImageFormatException>(() => InflateCore.Decompress(new byte[] { 0x05, 0x00, 0x92, 0x24, 0x49, 0x92, 0x24, 0x49, 0x92, 0x24 }, dst, false, false));
        // wrong Adler-32, and the same stream accepted when verification is off
        var good = Deflater.Compress(Make("text"), 6, true);
        good[^2] ^= 0x10;
        var big = new byte[70_000];
        Assert.Throws<ImageFormatException>(() => InflateCore.Decompress(good, big, true, true));
        Assert.Equal(60_000, InflateCore.Decompress(good, big, true, false));
        Assert.Throws<ImageFormatException>(() => InflateCore.Decompress(good, 0, true, true));
        var inf = new Inflater();
        Assert.Throws<ImageFormatException>(() => inf.Inflate(good, big, out _, out _));
        Assert.Equal(InflateStatus.Done, new Inflater(true, false).Inflate(good, big, out _, out _));
    }

    [Fact]
    public void InflateBombIsBoundedByTheDestination()
    {
        // 60 MB of zeros shrink to about 58 KB; a small destination must stop the decoder at once
        var zeros = new byte[60_000_000];
        var z = Deflater.Compress(zeros, 9, true);
        Assert.True(z.Length < 70_000);
        var small = new byte[1000 + 16];
        small.AsSpan(1000).Fill(Guard);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 200; i++) Assert.Equal(1000, InflateCore.Decompress(z, small.AsSpan(0, 1000), true, true));
        Assert.True(sw.ElapsedMilliseconds < 2000, "decoding continued past the destination");
        Assert.True(small.AsSpan(1000).IndexOfAnyExcept(Guard) < 0);
    }

    static byte[] ZstdFrame(ulong? contentSize, params byte[][] blocks)
    {
        var o = new List<byte> { 0x28, 0xB5, 0x2F, 0xFD };
        if (contentSize is ulong cs)
        {
            o.Add(0xE0); // 8 byte content size, single segment
            var b = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(b, cs);
            o.AddRange(b);
        }
        else { o.Add(0x00); o.Add(0x70); } // window descriptor: 16 MiB
        foreach (var b in blocks) o.AddRange(b);
        return o.ToArray();
    }

    static byte[] RleBlock(int size, byte value, bool last)
    {
        int h = (last ? 1 : 0) | 1 << 1 | size << 3;
        return new[] { (byte)h, (byte)(h >> 8), (byte)(h >> 16), value };
    }

    [Fact]
    public void ZstdBombIsBoundedByTheDestination()
    {
        // 2000 RLE blocks of 128 KiB each: 8 KB of input describing 262 MB
        var blocks = Enumerable.Range(0, 2000).Select(i => RleBlock(131072, 7, i == 1999)).ToArray();
        var bomb = ZstdFrame(null, blocks);
        var sw = Stopwatch.StartNew();
        Assert.Throws<ImageFormatException>(() => Zstd.Decompress(bomb, new byte[1_000_000]));
        Assert.True(sw.ElapsedMilliseconds < 2000);
        // a header that announces an enormous size must fail without allocating it
        long before = GC.GetTotalAllocatedBytes(true);
        Assert.Throws<ImageFormatException>(() => Zstd.Decompress(ZstdFrame(1UL << 31, RleBlock(10, 1, true))));
        Assert.Throws<ImageFormatException>(() => Zstd.Decompress(ZstdFrame(1UL << 50, RleBlock(10, 1, true))));
        Assert.Throws<ImageFormatException>(() => Zstd.Decompress(ZstdFrame(1UL << 30, RleBlock(10, 1, true)), new byte[100]));
        Assert.True(GC.GetTotalAllocatedBytes(true) - before < 64 << 20, "a lying header caused a large allocation");
        // and a truthful small frame of the same construction decodes
        var ok = Zstd.Decompress(ZstdFrame(300, RleBlock(100, 1, false), RleBlock(200, 2, true)));
        Assert.Equal(300, ok.Length);
        Assert.Equal(1, ok[99]);
        Assert.Equal(2, ok[100]);
    }

    [Fact]
    public void ZstdRejectsStructuralErrors()
    {
        var dst = new byte[1000];
        Assert.Throws<ImageFormatException>(() => Zstd.Decompress(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, dst));
        Assert.Throws<ImageFormatException>(() => Zstd.Decompress(new byte[] { 0x28, 0xB5, 0x2F }, dst));
        // reserved block type 3
        Assert.Throws<ImageFormatException>(() => Zstd.Decompress(ZstdFrame(null, new byte[] { 0x07, 0, 0 }), dst));
        // reserved frame header bit
        Assert.Throws<ImageFormatException>(() => Zstd.Decompress(new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 0x08, 0x70, 1, 0, 0 }, dst));
        // content size does not match
        Assert.Throws<ImageFormatException>(() => Zstd.Decompress(ZstdFrame(11, RleBlock(10, 1, true)), dst));
        Assert.Throws<ImageFormatException>(() => Zstd.Decompress(ZstdFrame(9, RleBlock(10, 1, true)), dst));
        // checksum damaged
        var z = Load("text__l3check.zst");
        z[^1] ^= 1;
        Assert.Throws<ImageFormatException>(() => Zstd.Decompress(z, new byte[30_000]));
        // skippable frame that claims more bytes than there are
        Assert.Throws<ImageFormatException>(() => Zstd.Decompress(new byte[] { 0x50, 0x2A, 0x4D, 0x18, 100, 0, 0, 0, 1, 2 }, dst));
        // trailing garbage after a valid frame
        Assert.Throws<ImageFormatException>(() => Zstd.Decompress(Load("one__l3.zst").Concat(new byte[] { 9, 9, 9, 9, 9 }).ToArray(), dst));
    }

    [Fact]
    public void XzRejectsStructuralErrors()
    {
        var good = Load("text__crc32.xz");
        var dst = new byte[30_000];
        Assert.Equal(24_000, Xz.Decompress(good, dst));
        foreach (int at in new[] { 0, 5, 7, 8, 12, 13, 14, good.Length - 1, good.Length - 2, good.Length - 5, good.Length - 9, good.Length - 12, good.Length - 16 })
        {
            var bad = (byte[])good.Clone();
            bad[at] ^= 0x01;
            var ex = Record.Exception(() => Xz.Decompress(bad, dst));
            Assert.True(ex is ImageFormatException or ImageNotSupportedException, $"flip at {at}: {ex?.GetType().Name ?? "accepted"}");
        }
        // every check type notices a damaged payload byte
        foreach (var name in new[] { "text__crc32.xz", "text__sha256.xz", "text__p6.xz" })
        {
            var bad = Load(name);
            bad[bad.Length / 2] ^= 0x40;
            Assert.Throws<ImageFormatException>(() => Xz.Decompress(bad, dst));
        }
        // stream padding must come in multiples of four
        Assert.Equal(24_000, Xz.Decompress(good.Concat(new byte[8]).ToArray(), dst));
        Assert.Throws<ImageFormatException>(() => Xz.Decompress(good.Concat(new byte[3]).ToArray(), dst));
        Assert.Throws<ImageFormatException>(() => Xz.Decompress(new byte[4].Concat(good).ToArray(), dst));
        Assert.Throws<ImageFormatException>(() => Xz.Decompress(good.Concat(new byte[] { 1, 2, 3, 4 }).ToArray(), dst));
    }

    [Fact]
    public void LzmaHeaderSizesAreNotTrusted()
    {
        var z = Load("text__alone.lzma");
        long before = GC.GetTotalAllocatedBytes(true);
        foreach (ulong claim in new[] { 1UL << 31, 1UL << 40, (1UL << 31) - 100, 500_000_000UL })
        {
            var bad = (byte[])z.Clone();
            BinaryPrimitives.WriteUInt64LittleEndian(bad.AsSpan(5), claim);
            Assert.Throws<ImageFormatException>(() => Xz.DecompressLzma(bad));
            Assert.Throws<ImageFormatException>(() => Xz.DecompressLzma(bad, new byte[30_000]));
        }
        Assert.True(GC.GetTotalAllocatedBytes(true) - before < 64 << 20, "a lying header caused a large allocation");
        // a declared size smaller than the data just stops there
        var shorter = (byte[])z.Clone();
        BinaryPrimitives.WriteUInt64LittleEndian(shorter.AsSpan(5), 1000);
        Assert.True(Expected("text__alone.lzma").AsSpan(0, 1000).SequenceEqual(Xz.DecompressLzma(shorter)));
    }
}
