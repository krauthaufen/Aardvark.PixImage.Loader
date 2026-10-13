using PixCodec;
using Xunit;

namespace PixCodec.Tests;

public class JpegTests
{
    public static IEnumerable<object[]> Fixtures() => TestUtil.Files("jpeg", ".jpg");
    public static IEnumerable<object[]> MiscWithRef() => TestUtil.Files("jpeg/misc", ".jpg").Where(f => File.Exists(TestUtil.Fixture((string)f[0]) + ".ref"));

    [Theory]
    [MemberData(nameof(Fixtures))]
    [MemberData(nameof(MiscWithRef))]
    public void DecodeMatchesReference(string file)
    {
        var path = TestUtil.Fixture(file);
        var expected = TestUtil.LoadRef(path + ".ref");
        var actual = Jpeg.Decode(File.ReadAllBytes(path));
        TestUtil.AssertEqual(expected, actual, 0);
    }
}
