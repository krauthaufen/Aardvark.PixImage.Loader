using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using PixCodec.Compression;
using Xunit;

namespace PixCodec.Tests;

public class CompressionChecksumTests
{
    // bit-at-a-time reference implementations
    static uint RefCrc32(ReadOnlySpan<byte> d, uint crc)
    {
        uint c = ~crc;
        foreach (byte b in d)
        {
            c ^= b;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        }
        return ~c;
    }

    static uint RefAdler32(ReadOnlySpan<byte> d, uint adler)
    {
        ulong a = adler & 0xFFFF, b = adler >> 16;
        foreach (byte x in d) { a = (a + x) % 65521; b = (b + a) % 65521; }
        return (uint)(b << 16 | a);
    }

    static ulong RefCrc64(ReadOnlySpan<byte> d)
    {
        ulong c = ~0UL;
        foreach (byte b in d)
        {
            c ^= b;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xC96C5795D7870F42UL ^ (c >> 1) : c >> 1;
        }
        return ~c;
    }

    static IEnumerable<byte[]> Inputs()
    {
        var rnd = new Random(11);
        foreach (int n in new[] { 0, 1, 2, 3, 7, 8, 15, 16, 17, 31, 32, 33, 63, 64, 65, 79, 80, 127, 128, 129, 255, 256, 1000, 5551, 5552, 5553, 11104, 65536, 200_001 })
        {
            var b = new byte[n];
            rnd.NextBytes(b);
            yield return b;
            // worst case for the Adler sums: all 0xFF
            yield return Enumerable.Repeat((byte)0xFF, n).ToArray();
        }
    }

    [Fact]
    public void KnownValues()
    {
        var s = "123456789"u8;
        Assert.Equal(0xCBF43926u, Zlib.Crc32(s));
        Assert.Equal(0x091E01DEu, Zlib.Adler32(s));
        Assert.Equal(0x995DC9BBDF1939FAUL, Checksums.Crc64(s));
        Assert.Equal(0xEF46DB3751D8E999UL, Checksums.XxHash64(ReadOnlySpan<byte>.Empty));
        Assert.Equal(0x44BC2CF5AD770999UL, Checksums.XxHash64("abc"u8));
        Span<byte> digest = stackalloc byte[32];
        Checksums.Sha256("abc"u8, digest);
        Assert.Equal("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", Convert.ToHexString(digest));
    }

    [Fact]
    public void Crc32AllPathsAgree()
    {
        foreach (var d in Inputs())
            foreach (uint seed in new uint[] { 0, 1, 0xDEADBEEF, 0xFFFFFFFF })
            {
                uint expected = RefCrc32(d, seed);
                Assert.Equal(expected, Zlib.Crc32(d, seed));
                Assert.Equal(expected, Checksums.Crc32Scalar(d, seed));
                if (Pclmulqdq.IsSupported && Sse41.IsSupported) Assert.Equal(expected, Checksums.Crc32Pclmul(d, seed));
                if (System.Runtime.Intrinsics.Arm.Crc32.Arm64.IsSupported) Assert.Equal(expected, Checksums.Crc32Arm(d, seed));
            }
    }

    [Fact]
    public void Adler32AllPathsAgree()
    {
        foreach (var d in Inputs())
            foreach (uint seed in new uint[] { 1, 0, 0xFFF0FFF0 % 65521 << 16 | 65520, 0x12345678 % 65521 })
            {
                uint expected = RefAdler32(d, seed);
                Assert.Equal(expected, Zlib.Adler32(d, seed));
                Assert.Equal(expected, Checksums.Adler32Scalar(d, seed));
                if (Avx2.IsSupported) Assert.Equal(expected, Checksums.Adler32Avx2(d, seed));
                if (Vector128.IsHardwareAccelerated) Assert.Equal(expected, Checksums.Adler32Vector128(d, seed));
            }
    }

    [Fact]
    public void ChecksumsAreIncremental()
    {
        var rnd = new Random(5);
        var d = new byte[100_000];
        rnd.NextBytes(d);
        uint crc = 0, adler = 1;
        for (int i = 0; i < d.Length;)
        {
            int n = Math.Min(d.Length - i, rnd.Next(1, 700));
            crc = Zlib.Crc32(d.AsSpan(i, n), crc);
            adler = Zlib.Adler32(d.AsSpan(i, n), adler);
            i += n;
        }
        Assert.Equal(RefCrc32(d, 0), crc);
        Assert.Equal(RefAdler32(d, 1), adler);
    }

    [Fact]
    public void Crc64AndHashesMatchReferences()
    {
        foreach (var d in Inputs().Take(40))
        {
            Assert.Equal(RefCrc64(d), Checksums.Crc64(d));
            var digest = new byte[32];
            Checksums.Sha256(d, digest);
            Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(d)), Convert.ToHexString(digest));
        }
    }
}
