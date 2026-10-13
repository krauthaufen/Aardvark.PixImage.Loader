using PixCodec;
using PixCodec.Compression;
using Xunit;

namespace PixCodec.Tests;

public class CoreTests
{
    [Fact]
    public void ZlibRoundTrip()
    {
        var rnd = new Random(3);
        var data = new byte[100_000];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(rnd.Next(8) * 31 + (i >> 6));
        foreach (var level in new[] { 0, 1, 6, 9 })
        {
            var z = Zlib.Compress(data, level);
            var back = new byte[data.Length];
            Assert.Equal(data.Length, Zlib.Decompress(z, back));
            Assert.Equal(data, back);
            Assert.Equal(data, Zlib.Decompress(z));
        }
    }

    [Fact]
    public void Checksums()
    {
        var s = "123456789"u8;
        Assert.Equal(0xCBF43926u, Zlib.Crc32(s));
        Assert.Equal(0x091E01DEu, Zlib.Adler32(s));
        var big = new byte[1000];
        new Random(1).NextBytes(big);
        Assert.Equal(Zlib.Crc32(big), Zlib.Crc32(big.AsSpan(333), Zlib.Crc32(big.AsSpan(0, 333))));
    }

    [Fact]
    public void RawImageValidates()
    {
        var img = RawImage.Create(3, 2, ColorModel.Rgb, SampleType.UInt16);
        Assert.Equal(18, img.GetData<ushort>().Length);
        Assert.Equal(36, img.GetBytes().Length);
        Assert.Throws<ArgumentException>(() => new RawImage(3, 2, ColorModel.Rgb, new byte[17]));
    }
}
