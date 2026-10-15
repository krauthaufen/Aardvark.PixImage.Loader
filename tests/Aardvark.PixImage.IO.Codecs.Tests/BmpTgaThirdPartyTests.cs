using Xunit;
using Xunit.Abstractions;
using static Aardvark.ImageCodecs.Tests.TestUtil;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>
/// The BMP Suite (Jason Summers) against its author's reference renderings, and GIMP-written TGA files (Pillow's test
/// images) against their PNG renderings. Both live in the test-data repository.
/// </summary>
public class BmpTgaThirdPartyTests(ITestOutputHelper output)
{
    /// <summary>The image as 8 bit RGBA, transparent pixels as (0,0,0,0).</summary>
    static byte[] Rgba8(RawImage img)
    {
        int n = img.Width * img.Height, c = img.Channels;
        var v = ToDoubles(img);
        double scale = img.SampleType == SampleType.UInt16 ? 255.0 / 65535 : 1;
        var o = new byte[n * 4];
        for (int i = 0; i < n; i++)
        {
            double g(int k) => Math.Round(v[i * c + k] * scale);
            byte r = (byte)g(0), gg = c >= 3 ? (byte)g(1) : r, b = c >= 3 ? (byte)g(2) : r;
            byte a = c == 2 ? (byte)g(1) : c == 4 ? (byte)g(3) : (byte)255;
            if (a == 0) r = gg = b = 0;
            o[i * 4] = r; o[i * 4 + 1] = gg; o[i * 4 + 2] = b; o[i * 4 + 3] = a;
        }
        return o;
    }

    static int MaxDiff(byte[] a, byte[] b)
    {
        int m = 0;
        for (int i = 0; i < a.Length; i++) m = Math.Max(m, Math.Abs(a[i] - b[i]));
        return m;
    }

    static readonly Dictionary<string, string[]> SuiteRefs = LoadSuiteRefs();

    static Dictionary<string, string[]> LoadSuiteRefs()
    {
        var d = new Dictionary<string, string[]>();
        if (TestDataFixtureDir == null) return d;
        var tsv = Path.Combine(TestDataFixtureDir, "bmp/bmpsuite/refs.tsv");
        if (!File.Exists(tsv)) return d;
        foreach (var line in File.ReadAllLines(tsv).Where(l => !l.StartsWith('#')))
        {
            var p = line.Split('\t');
            d[p[0]] = p.Length > 1 ? p[1].Split(' ', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
        }
        return d;
    }

    public static IEnumerable<object[]> SuiteFiles()
        => new[] { "g", "q", "b" }.SelectMany(d => ThirdPartyFiles("bmp/bmpsuite/" + d, ".bmp"));

    // questionable files whose rendering differs from the reference on purpose (see the comments at the expectations)
    static readonly Dictionary<string, string> Deviations = new()
    {
        // the reference shows the colours after applying the embedded ICC profile (which swaps red and green); colour
        // management is left to the caller, the profile is handed out in the metadata
        ["rgb24prof2.bmp"] = "ICC profile not applied",
    };

    /// <summary>
    /// "Good" files must match one of their reference renderings; "questionable" ones too unless listed above or the
    /// format is not supported (cleanly rejected); "bad" ones decode or fail cleanly.
    /// </summary>
    [TestDataTheory("fixtures/bmp/bmpsuite/refs.tsv"), MemberData(nameof(SuiteFiles))]
    public void BmpSuite(string file)
    {
        var data = File.ReadAllBytes(Fixture(file));
        string kind = Path.GetFileName(Path.GetDirectoryName(file))!, name = Path.GetFileName(file);
        string key = kind + "/" + name;
        RawImage img;
        try { img = Bmp.Decode(data); }
        catch (Exception e) when (e is ImageFormatException or ImageNotSupportedException)
        {
            output.WriteLine($"{key}: {e.GetType().Name}: {e.Message}");
            Assert.True(kind != "g", $"{key} must decode: {e.Message}");
            if (kind == "q") Assert.IsType<ImageNotSupportedException>(e);
            return;
        }
        var info = Bmp.GetInfo(data);
        Assert.Equal((img.Width, img.Height, img.Channels, img.Color), (info.Width, info.Height, info.Channels, info.Color));

        var refs = SuiteRefs.GetValueOrDefault(key, Array.Empty<string>());
        var ours = Rgba8(img);
        var results = new List<string>();
        bool comparable = false, matched = false;
        foreach (var r in refs)
        {
            var refImg = ImageCodec.Decode(Fixture("bmp/bmpsuite/ref/" + r));
            if (refImg.Width != img.Width || refImg.Height != img.Height) { results.Add($"{r}: other size"); continue; }
            comparable = true;
            int d = MaxDiff(Rgba8(refImg), ours);
            results.Add($"{r}: {d}");
            int tol = r.EndsWith(".jpg") ? 3 : img.SampleType == SampleType.UInt16 ? 1 : 0;
            matched |= d <= tol;
        }
        output.WriteLine($"{key}: {img.Width}x{img.Height} {img.Color} {img.SampleType}; {string.Join(", ", results)}");
        if (kind == "b" || !comparable) return;
        if (Deviations.TryGetValue(name, out var why)) { Assert.False(matched, $"{key} now matches although listed ({why})"); return; }
        Assert.True(matched, $"{key} matches none of {string.Join(", ", results)}");
    }

    public static IEnumerable<object[]> GimpTga() => ThirdPartyFiles("tga/gimp", ".tga");

    [TestDataTheory("fixtures/tga/gimp/200x32_rgba_tl_rle.tga"), MemberData(nameof(GimpTga))]
    public void GimpWrittenTga(string file)
    {
        var data = File.ReadAllBytes(Fixture(file));
        var img = Tga.Decode(data);
        var name = Path.GetFileName(file);
        var png = Fixture(Path.Combine("tga/gimp", name[..name.IndexOf('_', name.IndexOf('_') + 1)] + ".png"));
        var expected = ImageCodec.Decode(png);
        Assert.Equal((expected.Width, expected.Height), (img.Width, img.Height));
        Assert.Equal(0, MaxDiff(Rgba8(expected), Rgba8(img)));
        var info = Tga.GetInfo(data);
        Assert.Equal((img.Width, img.Height, img.Channels, img.Color), (info.Width, info.Height, info.Channels, info.Color));
    }
}
