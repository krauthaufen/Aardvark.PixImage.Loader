using System.Buffers.Binary;
using PixCodec;
using Xunit;
using static PixCodec.Tests.TestUtil;

namespace PixCodec.Tests;

public class Jp2RobustnessTests
{
    public static IEnumerable<object[]> Fixtures() => Jp2TestUtil.Fixtures();

    static readonly Jp2DecodeOptions Strict = new Jp2DecodeOptions { Parallel = false };
    static readonly Jp2DecodeOptions Tolerant = new Jp2DecodeOptions { Parallel = false, AllowTruncated = true };

    /// <summary>Decoding damaged data either works or fails with one of the two documented exceptions.</summary>
    static bool TryDecode(byte[] data, Jp2DecodeOptions o, string what)
    {
        try
        {
            var img = Jp2.Decode(data, o);
            Assert.True(img.Width > 0 && img.Height > 0);
            return true;
        }
        catch (ImageFormatException) { return false; }
        catch (ImageNotSupportedException) { return false; }
        catch (Exception e)
        {
            Assert.Fail($"{what}: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
            return false;
        }
    }

    static void TryInfo(byte[] data, string what)
    {
        try { Jp2.GetInfo(data); }
        catch (ImageFormatException) { }
        catch (ImageNotSupportedException) { }
        catch (Exception e) { Assert.Fail($"{what} (GetInfo): {e.GetType().Name}: {e.Message}\n{e.StackTrace}"); }
    }

    [Theory, MemberData(nameof(Fixtures))]
    public void Truncation(string file)
    {
        var data = File.ReadAllBytes(Fixture(file));
        var rng = new Random(file.GetHashCode(StringComparison.Ordinal) ^ 77);
        var cuts = new SortedSet<int> { 0, 1, 2, 3, 4, 11, 12, 13, data.Length - 1, data.Length - 2, data.Length - 3 };
        for (int i = 0; i < 16; i++) cuts.Add(rng.Next(data.Length));
        // dense around the headers
        for (int i = 0; i < 8; i++) cuts.Add(rng.Next(Math.Min(data.Length, 300)));
        int width = 0;
        try { width = Jp2.GetInfo(data).Width; } catch (ImageFormatException) { }
        foreach (int cut in cuts)
        {
            if (cut < 0 || cut >= data.Length) continue;
            var part = data.AsSpan(0, cut).ToArray();
            TryDecode(part, Strict, $"{file} cut at {cut}");
            bool ok = TryDecode(part, Tolerant, $"{file} cut at {cut} (tolerant)");
            TryInfo(part, $"{file} cut at {cut}");
            // what the tolerant decoder returns still has the size the header gives
            if (ok && width > 0) Assert.Equal(width, Jp2.Decode(part, Tolerant).Width);
        }
    }

    [Theory, MemberData(nameof(Fixtures))]
    public void ByteFlips(string file)
    {
        var data = File.ReadAllBytes(Fixture(file));
        var rng = new Random(file.GetHashCode(StringComparison.Ordinal) ^ 1234);
        for (int i = 0; i < 30; i++)
        {
            var copy = (byte[])data.Clone();
            // single flips, bursts, and flips concentrated in the headers
            int n = i % 4 == 3 ? 8 : 1;
            int range = i % 3 == 0 ? Math.Min(data.Length, 200) : data.Length;
            for (int k = 0; k < n; k++)
            {
                int pos = rng.Next(range);
                copy[pos] = i % 5 == 4 ? (byte)rng.Next(256) : (byte)(copy[pos] ^ (1 << rng.Next(8)));
            }
            TryDecode(copy, Strict, $"{file} flip {i}");
            if (i % 2 == 0) TryDecode(copy, Tolerant, $"{file} flip {i} (tolerant)");
            TryInfo(copy, $"{file} flip {i}");
        }
    }

    [Fact]
    public void TruncatedFilesDecodeWhatIsThere()
    {
        // a layered image cut in half: an error by default, a coarser image on request
        var data = File.ReadAllBytes(Fixture("jp2/layers_irr.j2k"));
        var full = Jp2.Decode(data);
        var half = data.AsSpan(0, data.Length / 2).ToArray();
        Assert.Throws<ImageFormatException>(() => Jp2.Decode(half));
        var img = Jp2.Decode(half, new Jp2DecodeOptions { AllowTruncated = true });
        Assert.Equal((full.Width, full.Height, full.Channels), (img.Width, img.Height, img.Channels));
        double psnr = Psnr(full, img, 255);
        Assert.InRange(psnr, 20, 60);
        // the more data, the better
        var most = Jp2.Decode(data.AsSpan(0, data.Length * 9 / 10).ToArray(), new Jp2DecodeOptions { AllowTruncated = true });
        Assert.True(Psnr(full, most, 255) > psnr);
        // a missing EOC alone is no reason to complain
        AssertEqual(full, Jp2.Decode(data.AsSpan(0, data.Length - 2).ToArray()));
        // HT blocks cannot be decoded partially: cut blocks are dropped, the rest decodes
        var ht = File.ReadAllBytes(Fixture("jp2/ht_rgb8_rev.jhc"));
        Assert.Throws<ImageFormatException>(() => Jp2.Decode(ht.AsSpan(0, ht.Length * 2 / 3).ToArray()));
        Assert.Equal(45, Jp2.Decode(ht.AsSpan(0, ht.Length * 2 / 3).ToArray(), new Jp2DecodeOptions { AllowTruncated = true }).Width);
    }

    /// <summary>A header that claims a huge image over a few bytes must fail without allocating for it.</summary>
    [Fact]
    public void LyingHeaders()
    {
        var data = File.ReadAllBytes(Fixture("jp2/gray_1x1.j2k"));
        Assert.True(data.Length < 200);
        var jp2 = File.ReadAllBytes(Fixture("jp2/gray_9x1.jp2"));
        int cs = Jp2TestUtil.CodestreamStart(jp2);
        foreach (var (w, h) in new[] { (uint.MaxValue, uint.MaxValue), (1u << 31, 1u << 31), (60000u, 60000u), (17000u, 17000u), (uint.MaxValue, 1u), (1u, uint.MaxValue), (70000u, 3u), (3000u, 2000u) })
            foreach (bool tiled in new[] { false, true })
                foreach (var (src, start) in new[] { (data, 0), (jp2, cs) })
                {
                    var lie = (byte[])src.Clone();
                    // SIZ: Xsiz, Ysiz at 8 and 12 behind SOC + marker + length + Rsiz; tile size at 24 and 28
                    BinaryPrimitives.WriteUInt32BigEndian(lie.AsSpan(start + 8), w);
                    BinaryPrimitives.WriteUInt32BigEndian(lie.AsSpan(start + 12), h);
                    BinaryPrimitives.WriteUInt32BigEndian(lie.AsSpan(start + 24), tiled ? 1024u : w);
                    BinaryPrimitives.WriteUInt32BigEndian(lie.AsSpan(start + 28), tiled ? 1024u : h);
                    // MaxPixels is what keeps a small file from claiming a large image (a few bytes can legitimately
                    // describe a blank one of any size), so lifting it is tried only where the size is beyond any
                    // array, or for a small window.
                    bool beyondArrays = (ulong)w * h > int.MaxValue;
                    var options = new List<Jp2DecodeOptions>
                    {
                        new Jp2DecodeOptions(), new Jp2DecodeOptions { AllowTruncated = true },
                        new Jp2DecodeOptions { MaxPixels = long.MaxValue, Region = (0, 0, 100, 1) },
                    };
                    if (beyondArrays)
                    {
                        options.Add(new Jp2DecodeOptions { MaxPixels = long.MaxValue });
                        options.Add(new Jp2DecodeOptions { MaxPixels = long.MaxValue, AllowTruncated = true, Parallel = false });
                    }
                    foreach (var o in options)
                    {
                        long before = GC.GetTotalAllocatedBytes(true);
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        Exception? ex = null;
                        try
                        {
                            // A small window may succeed, and so may an image of a few megapixels: a codestream of a
                            // few bytes can legitimately describe a blank image (MaxPixels is the guard against that).
                            var img = Jp2.Decode(lie, o);
                            Assert.True((long)img.Width * img.Height <= 6_000_000);
                        }
                        catch (Exception e) { ex = e; }
                        long allocated = GC.GetTotalAllocatedBytes(true) - before;
                        Assert.True(ex is null or ImageFormatException or ImageNotSupportedException, $"{w}x{h}: {ex}");
                        Assert.True(allocated < (w == 3000 ? 256 : 64) << 20, $"{w}x{h} tiled={tiled}: allocated {allocated >> 20} MB");
                        Assert.True(sw.ElapsedMilliseconds < 5000, $"{w}x{h}: {sw.ElapsedMilliseconds} ms");
                    }
                    try
                    {
                        var info = Jp2.GetInfo(lie);
                        Assert.Equal((uint)info.Width, w);
                    }
                    catch (ImageNotSupportedException) { Assert.True(w > int.MaxValue || h > int.MaxValue); }
                    catch (ImageFormatException) { Assert.True(tiled); }   // more than 65535 tiles
                }
    }

    [Fact]
    public void HostileStructure()
    {
        // many layers / precincts / code-blocks over almost no data must not loop or allocate for long
        var data = File.ReadAllBytes(Fixture("jp2/gray8_rev.j2k"));
        int cod = -1;
        for (int i = 2; i < data.Length - 1; i++) if (data[i] == 0xFF && data[i + 1] == 0x52) { cod = i; break; }
        Assert.True(cod > 0);
        var rng = new Random(9);
        for (int i = 0; i < 300; i++)
        {
            var copy = (byte[])data.Clone();
            // random coding style: progression, layers, levels, block sizes, style, precincts flag
            copy[cod + 4] = (byte)rng.Next(8);
            copy[cod + 5] = (byte)rng.Next(5);
            BinaryPrimitives.WriteUInt16BigEndian(copy.AsSpan(cod + 6), (ushort)(rng.Next(3) == 0 ? 65535 : rng.Next(1, 40)));
            copy[cod + 9] = (byte)rng.Next(34);
            copy[cod + 10] = (byte)rng.Next(9);
            copy[cod + 11] = (byte)rng.Next(9);
            copy[cod + 12] = (byte)rng.Next(256);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            TryDecode(copy, Strict, $"cod {i}");
            TryDecode(copy, Tolerant, $"cod {i} (tolerant)");
            Assert.True(sw.ElapsedMilliseconds < 3000, $"cod variant {i} took {sw.ElapsedMilliseconds} ms");
        }
    }

    [Fact]
    public void GarbageInput()
    {
        var rng = new Random(1);
        Assert.Throws<ImageFormatException>(() => Jp2.Decode(Array.Empty<byte>()));
        Assert.Throws<ImageFormatException>(() => Jp2.GetInfo(Array.Empty<byte>()));
        for (int i = 0; i < 300; i++)
        {
            var junk = new byte[rng.Next(1, 400)];
            rng.NextBytes(junk);
            // behind a valid start
            if (i % 2 == 0) new byte[] { 0xFF, 0x4F, 0xFF, 0x51, 0, 41 }.AsSpan(0, Math.Min(6, junk.Length)).CopyTo(junk);
            else new byte[] { 0, 0, 0, 12, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A }.AsSpan(0, Math.Min(12, junk.Length)).CopyTo(junk);
            TryDecode(junk, Strict, $"junk {i}");
            TryDecode(junk, Tolerant, $"junk {i}");
            TryInfo(junk, $"junk {i}");
        }
    }
}
