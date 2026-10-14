using System.Buffers.Binary;
using System.Diagnostics;
using PixCodec;
using Xunit;
using static PixCodec.Tests.TestUtil;

namespace PixCodec.Tests;

public class LercRobustnessTests
{
    public static IEnumerable<object[]> Fixtures => LercDecodeTests.Fixtures;

    static readonly LercDecodeOptions NoChecksum = new() { VerifyChecksum = false };

    static void MustFailCleanly(byte[] data, LercDecodeOptions? options = null)
    {
        try { Lerc.GetInfo(data); } catch (ImageFormatException) { } catch (ImageNotSupportedException) { }
        try { Lerc.Decode(data, out _, options); } catch (ImageFormatException) { } catch (ImageNotSupportedException) { }
    }

    [Fact]
    public void ManifestCoversEveryFixture()
    {
        var files = Directory.GetFiles(Path.Combine(FixtureDir, "lerc"), "*.lerc").Select(Path.GetFileName).ToHashSet();
        Assert.True(files.Count > 300);
        Assert.True(files.SetEquals(LercManifestEntry.Own.Keys));
    }

    [TestDataFact("fixtures/lerc/manifest.tsv")]
    public void ManifestCoversEveryEsriSample()
    {
        var files = Directory.GetFiles(TestData("fixtures/lerc"), "*.lerc").Select(Path.GetFileName).ToHashSet();
        Assert.Equal(3, files.Count);
        Assert.True(files.SetEquals(LercManifestEntry.ThirdParty.Keys));
    }

    [TestDataTheory("fixtures/lerc/manifest.tsv"), MemberData(nameof(LercDecodeTests.EsriSamples), MemberType = typeof(LercDecodeTests))]
    public void EsriSampleFailsCleanlyWhenDamaged(string file)
    {
        TruncatedInputFailsCleanly(file);
        CorruptedInputFailsCleanly(file);
    }

    [Theory, MemberData(nameof(Fixtures))]
    public void TruncatedInputFailsCleanly(string file)
    {
        var data = File.ReadAllBytes(Fixture("lerc/" + file));
        var sw = Stopwatch.StartNew();
        int step = Math.Max(1, data.Length / 100);
        for (int len = 0; len < data.Length; len += len < 100 ? 1 : step)
        {
            var part = data.AsSpan(0, len).ToArray();
            MustFailCleanly(part);
            MustFailCleanly(part, NoChecksum);
        }
        Assert.True(sw.Elapsed.TotalSeconds < 20, $"took {sw.Elapsed.TotalSeconds:F1}s");
    }

    [Theory, MemberData(nameof(Fixtures))]
    public void CorruptedInputFailsCleanly(string file)
    {
        var original = File.ReadAllBytes(Fixture("lerc/" + file));
        var rnd = new Random(4711 + original.Length);
        var sw = Stopwatch.StartNew();
        for (int round = 0; round < 120; round++)
        {
            var data = (byte[])original.Clone();
            int flips = 1 + round % 4;
            for (int i = 0; i < flips; i++)
            {
                // half of the damage goes to the headers, where the structure lives
                int pos = round % 2 == 0 ? rnd.Next(Math.Min(data.Length, 100)) : rnd.Next(data.Length);
                data[pos] = round % 3 == 0 ? (byte)rnd.Next(256) : (byte)(data[pos] ^ 1 << rnd.Next(8));
            }
            MustFailCleanly(data, NoChecksum);
            if (round % 8 == 0) MustFailCleanly(data);
        }
        Assert.True(sw.Elapsed.TotalSeconds < 30, $"took {sw.Elapsed.TotalSeconds:F1}s");
    }

    [Fact]
    public void ChecksumIsVerifiedOrSkipped()
    {
        var data = File.ReadAllBytes(Fixture("lerc/a_u16_noise_v5.lerc"));
        var good = Lerc.Decode(data);

        // a wrong checksum field over intact data: rejected, or decoded unchanged without the check
        var wrongSum = (byte[])data.Clone();
        wrongSum[11] ^= 0x40;
        Assert.Throws<ImageFormatException>(() => Lerc.Decode(wrongSum));
        Assert.True(good.GetBytes().SequenceEqual(Lerc.Decode(wrongSum, out _, NoChecksum).GetBytes()));

        // damaged data is caught by the checksum
        var damaged = (byte[])data.Clone();
        damaged[^3] ^= 0x10;
        Assert.Throws<ImageFormatException>(() => Lerc.Decode(damaged));

        // the TIFF entry point checks too, unless told not to
        var dst = new byte[good.Width * good.Height * 2];
        Assert.Throws<ImageFormatException>(() => Lerc.DecodeInto(wrongSum, SampleType.UInt16, good.Width, good.Height, dst, 1, default));
        Lerc.DecodeInto(wrongSum, SampleType.UInt16, good.Width, good.Height, dst, 1, default, verifyChecksum: false);
        Assert.True(good.GetBytes().SequenceEqual(dst));
    }

    static long Allocated(Action a)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        a();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    static void Patch(byte[] d, int pos, int value) => BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(pos), value);

    [Fact]
    public void LyingHeadersDoNotAllocate()
    {
        // v3 header: magic(6) version(4) checksum(4) rows(4) cols(4) valid(4) blocksize(4) blobsize(4) type(4)
        var data = File.ReadAllBytes(Fixture("lerc/a_u8_noise_v3.lerc"));
        var big = (byte[])data.Clone();
        Patch(big, 14, 1 << 15);
        Patch(big, 18, 1 << 15);
        Patch(big, 22, 1 << 30);
        long bytes = Allocated(() => MustFailCleanly(big, NoChecksum));
        Assert.True(bytes < 1 << 20, $"allocated {bytes} bytes");

        // a constant raster is legitimately tiny: a huge one is accepted only up to MaxUnbackedPixels
        // (a 1 x 1 float blob, all valid, so the header alone describes it)
        var constant = File.ReadAllBytes(Fixture("lerc/c_1x1_f32_e0.05_v4.lerc"));
        float value = Lerc.Decode(constant).GetData<float>()[0];
        // v4: ... rows(14) cols(18) depth(22) valid(26)
        Patch(constant, 14, 12000);
        Patch(constant, 18, 12000);
        Patch(constant, 26, 12000 * 12000);
        bytes = Allocated(() => Assert.Throws<ImageFormatException>(() => Lerc.Decode(constant, out _, NoChecksum)));
        Assert.True(bytes < 1 << 20, $"allocated {bytes} bytes");
        Patch(constant, 14, 3000);
        Patch(constant, 18, 3000);
        Patch(constant, 26, 3000 * 3000);
        var ok = Lerc.Decode(constant, out _, NoChecksum);
        Assert.Equal(value, ((float[])ok.Data)[3000 * 3000 - 1]);
        Assert.Throws<ImageNotSupportedException>(() => Lerc.Decode(constant, out _, new LercDecodeOptions { VerifyChecksum = false, MaxPixels = 1000 }));

        // absurd depth, and a v6 band count that the data does not hold
        var deep = File.ReadAllBytes(Fixture("lerc/a_i8_steps_v6.lerc"));
        var d2 = (byte[])deep.Clone();
        Patch(d2, 22, int.MaxValue);
        bytes = Allocated(() => MustFailCleanly(d2, NoChecksum));
        Assert.True(bytes < 1 << 20, $"allocated {bytes} bytes");
        var d3 = (byte[])deep.Clone();
        Patch(d3, 42, 1 << 20); // number of blobs that follow
        bytes = Allocated(() => Assert.Throws<ImageFormatException>(() => Lerc.Decode(d3, out _, NoChecksum)));
        Assert.True(bytes < 1 << 20, $"allocated {bytes} bytes");

        // a huge mask announced by a few bytes
        var masked = File.ReadAllBytes(Fixture("lerc/d_random_u8_e0_v2.lerc"));
        Patch(masked, 10, 1 << 14); // v2: rows at 10, cols at 14
        Patch(masked, 14, 1 << 14);
        bytes = Allocated(() => MustFailCleanly(masked));
        Assert.True(bytes < 1 << 20, $"allocated {bytes} bytes");
    }

    [Fact]
    public void GarbageNeverHangs()
    {
        var rnd = new Random(99);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 3000; i++)
        {
            var d = new byte[rnd.Next(20, 400)];
            rnd.NextBytes(d);
            (i % 2 == 0 ? "Lerc2 "u8 : "CntZImage "u8).CopyTo(d);
            if (i % 2 == 0) BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(6), 2 + i % 5);
            else { BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(10), 11); BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(14), 8); }
            MustFailCleanly(d, NoChecksum);
        }
        Assert.True(sw.Elapsed.TotalSeconds < 20);
    }
}
