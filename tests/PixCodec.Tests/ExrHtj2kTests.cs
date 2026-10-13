using PixCodec;
using Xunit;

namespace PixCodec.Tests;

/// <summary>
/// HTJ2K compression (OpenEXR 3.4, compression values 10 and 11). The codestream coding belongs to the JPEG 2000
/// codec (Jp2.DecodeCodestreamPlanes / EncodeCodestreamPlanes). Decoding is bit exact against the reference; the
/// encoding test is skipped until the JPEG 2000 codec can encode HT planes.
/// </summary>
public class ExrHtj2kTests
{
    const string Skip = "needs HT plane encoding in the JPEG 2000 codec (Jp2.EncodeCodestreamPlanes, called by ExrHtj2k); remove this Skip when it is implemented";

    public static IEnumerable<object[]> Fixtures => TestUtil.Files(Path.Combine("exr", "htj2k"), ".exr");

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void DecodesLikeReference(string file)
    {
        var path = TestUtil.Fixture(file);
        var data = File.ReadAllBytes(path);
        var expected = TestUtil.LoadRef(path + ".ref");
        foreach (bool parallel in new[] { true, false })
            TestUtil.AssertEqual(expected, Exr.Decode(data, new ExrDecodeOptions { HalfAsFloat = expected.SampleType != SampleType.Float16, Parallel = parallel }));
        if (File.Exists(path + ".all.ref"))
            TestUtil.AssertEqual(TestUtil.LoadRef(path + ".all.ref"), Exr.DecodeChannels(data, Exr.ReadHeaders(data)[0].Channels.Select(c => c.Name).ToArray()));
        var win = new ExrBox2i(2, 3, 9, 40);
        var full = Exr.Decode(data);
        var region = Exr.Decode(data, new ExrDecodeOptions { Region = win });
        Assert.Equal((8, 38), (region.Width, region.Height));
    }

    [Theory(Skip = Skip)]
    [InlineData(ExrCompression.Htj2k256)]
    [InlineData(ExrCompression.Htj2k32)]
    public void EncodeIsLosslessAndReadByReference(ExrCompression comp)
    {
        var rnd = new Random(4);
        const int w = 150, h = 300;
        var f = new float[w * h * 4];
        for (int i = 0; i < f.Length; i++) f[i] = (float)(Half)(float)(Math.Sin(i % w / 9.0) + rnd.NextDouble() * 0.1);
        var halves = Array.ConvertAll(f, v => (Half)v);
        var ids = new uint[w * h];
        for (int i = 0; i < ids.Length; i++) ids[i] = (uint)rnd.NextInt64(0, 1L << 32);
        var sub = new Half[w / 2 * (h / 2)];
        for (int i = 0; i < sub.Length; i++) sub[i] = (Half)(float)rnd.NextDouble();
        var chans = new[]
        {
            new ExrChannelData("R", halves, null, 0, 4), new ExrChannelData("G", halves, null, 1, 4), new ExrChannelData("B", halves, null, 2, 4),
            new ExrChannelData("A", halves, null, 3, 4), new ExrChannelData("Z", f, ExrPixelType.Float, 0, 4), new ExrChannelData("id", ids),
            new ExrChannelData("sub", sub) { XSampling = 2, YSampling = 2 },
        };
        foreach (int tile in new[] { 0, 64 })
        {
            var ms = new MemoryStream();
            Exr.EncodeChannels(w, h, tile == 0 ? chans : chans.Take(6).ToArray(), ms, new ExrEncodeOptions { Compression = comp, TileWidth = tile, TileHeight = tile });
            var data = ms.ToArray();
            var hd = Exr.ReadHeaders(data)[0];
            Assert.Equal(comp, hd.Compression);
            Assert.True(data.Length < w * h * 16);
            Assert.Equal(ids, Exr.DecodeChannels(data, new[] { "id" }).GetData<uint>());
            Assert.Equal(Enumerable.Range(0, w * h).Select(i => f[i * 4]), Exr.DecodeChannels(data, new[] { "Z" }).GetData<float>());
            Assert.True(halves.AsSpan().SequenceEqual(Exr.Decode(data, new ExrDecodeOptions { HalfAsFloat = false }).GetData<Half>()));
            var oracle = ExrOracle.Decode(data, ".all.exr");
            if (oracle != null) TestUtil.AssertEqual(oracle, Exr.DecodeChannels(data, hd.Channels.Select(c => c.Name).ToArray()));
        }
    }

    [Fact]
    public void EncodingWaitsForTheJp2Encoder()
    {
        // delete this test together with the Skip above once the JPEG 2000 codec can encode HT planes
        Assert.Throws<ImageNotSupportedException>(() => Exr.Encode(RawImage.Create(64, 64, ColorModel.Rgb, SampleType.Float32), new MemoryStream(), new ExrEncodeOptions { Compression = ExrCompression.Htj2k32 }));
    }
}
