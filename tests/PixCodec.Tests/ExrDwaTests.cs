using PixCodec;
using Xunit;

namespace PixCodec.Tests;

/// <summary>
/// DWAA / DWAB store quantized DCT coefficients of perceptually encoded values. The inverse DCT runs in float32 and
/// its result is rounded to a half before the (steep) perceptual-to-linear table, so two correct decoders differ
/// where that rounding flips: OpenEXR's own SIMD and scalar paths do as well. The tests therefore require that almost
/// all samples are bit identical with the reference and the rest are within one perceptual half step (about 1%).
/// Channels that are not DCT coded (alpha, depth, ids) must be exact.
/// </summary>
public class ExrDwaTests
{
    public static IEnumerable<object[]> Fixtures => TestUtil.Files(Path.Combine("exr", "dwa"), ".exr");

    static void AssertClose(RawImage expected, RawImage actual, bool[] lossy)
    {
        Assert.Equal((expected.Width, expected.Height, expected.Channels, expected.SampleType),
            (actual.Width, actual.Height, actual.Channels, actual.SampleType));
        int n = expected.Width * expected.Height, c = expected.Channels;
        double Get(RawImage img, int i) => img.Data switch
        {
            Half[] a => (double)a[i],
            float[] a => a[i],
            uint[] a => a[i],
            _ => throw new InvalidOperationException(),
        };
        for (int k = 0; k < c; k++)
        {
            int same = 0;
            for (int i = 0; i < n; i++)
            {
                double e = Get(expected, i * c + k), a = Get(actual, i * c + k);
                if (e == a) { same++; continue; }
                Assert.True(lossy[k], $"channel {k} is not DCT coded and must be exact ({e} vs {a})");
                Assert.True(Math.Abs(e - a) <= 0.015 * Math.Max(Math.Abs(e), Math.Abs(a)) + 1e-5, $"channel {k}: {e} vs {a}");
            }
            Assert.True(same >= n * 0.99, $"channel {k}: only {same} of {n} samples are identical to the reference");
        }
    }

    static bool IsDct(string name) => name.Substring(name.LastIndexOf('.') + 1) is "R" or "G" or "B" or "Y";

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void DecodesCloseToReference(string file)
    {
        var path = TestUtil.Fixture(file);
        var data = File.ReadAllBytes(path);
        var header = Exr.ReadHeaders(data)[0];
        Assert.True(header.Compression is ExrCompression.Dwaa or ExrCompression.Dwab);
        var expected = TestUtil.LoadRef(path + ".ref");
        var img = Exr.Decode(data, new ExrDecodeOptions { HalfAsFloat = expected.SampleType != SampleType.Float16 });
        var lossy = Enumerable.Repeat(true, 3).Concat(new[] { false }).Take(img.Channels).ToArray();    // R,G,B,(A) or Y
        AssertClose(expected, img, lossy);
        TestUtil.AssertEqual(img, Exr.Decode(data, new ExrDecodeOptions { HalfAsFloat = expected.SampleType != SampleType.Float16, Parallel = false }));
        if (expected.SampleType == SampleType.Float16)
            TestUtil.AssertEqual(ExrDecodeTests.HalfToFloat(img), Exr.Decode(data));

        if (File.Exists(path + ".all.ref"))
        {
            var names = header.Channels.Select(c => c.Name).ToArray();
            AssertClose(TestUtil.LoadRef(path + ".all.ref"), Exr.DecodeChannels(data, names), names.Select(IsDct).ToArray());
        }
    }

    [Fact]
    public void EncodingIsNotSupported()
    {
        var img = RawImage.Create(8, 8, ColorModel.Rgb, SampleType.Float32);
        Assert.Throws<ImageNotSupportedException>(() => Exr.Encode(img, new MemoryStream(), new ExrEncodeOptions { Compression = ExrCompression.Dwab }));
    }
}
