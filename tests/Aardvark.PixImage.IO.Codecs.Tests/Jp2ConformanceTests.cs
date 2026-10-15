using System.Security.Cryptography;
using Aardvark.ImageCodecs;
using Xunit;
using static Aardvark.ImageCodecs.Tests.TestUtil;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>
/// The decoder conformance test of Rec. ITU-T T.803 | ISO/IEC 15444-4 (02/2024), class 1: the Part 1 codestreams of
/// profile 0 and 1, the HTJ2K sets derived from them (HT only and mixed with Part 1 code-blocks, with placeholder
/// passes, several layers, SigProp / MagRef passes) and the high fidelity pair. Every component has to stay within the
/// peak error and mean squared error the standard allows against its reference image (limits.txt).
/// The suite may only be used for conformance testing and is not committed anywhere: fetch/fetch_jp2_conformance.sh of
/// the test-data repository downloads it (93 MB) next to limits.txt there. Without the files these tests are skipped.
/// </summary>
public class Jp2ConformanceTests
{
    const string Fetched = "fixtures/jp2/conformance/codestreams_profile0";

    static string Dir => TestData(Path.Combine("fixtures", "jp2", "conformance"));

    public static IEnumerable<object[]> Streams()
    {
        if (TestDataSkipReason(new[] { Fetched }) != null) yield break;
        foreach (var line in File.ReadAllLines(Path.Combine(Dir, "limits.txt")))
            if (!line.StartsWith('#') && line.Length > 0) yield return new object[] { line.Split(' ')[0] };
    }

    static (long[] samples, int width, int height) ReadPgx(string path)
    {
        var b = File.ReadAllBytes(path);
        int nl = Array.IndexOf(b, (byte)'\n');
        var head = System.Text.Encoding.ASCII.GetString(b, 0, nl).Replace("+", "+ ").Replace("-", "- ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("PG", head[0]);
        bool big = head[1] == "ML";
        int i = 2;
        bool signed = head[i] == "-";
        if (head[i] is "+" or "-") i++;
        int depth = int.Parse(head[i]), w = int.Parse(head[i + 1]), h = int.Parse(head[i + 2]);
        int bytes = depth <= 8 ? 1 : depth <= 16 ? 2 : 4;
        var s = new long[w * h];
        for (int k = 0, p = nl + 1; k < s.Length; k++, p += bytes)
        {
            long v = 0;
            for (int j = 0; j < bytes; j++) v = v << 8 | b[big ? p + j : p + bytes - 1 - j];
            if (signed) v = v << (64 - 8 * bytes) >> (64 - 8 * bytes);
            s[k] = v;
        }
        return (s, w, h);
    }

    [TestDataTheory(Fetched), MemberData(nameof(Streams))]
    public void Class1(string stream)
    {
        var line = File.ReadAllLines(Path.Combine(Dir, "limits.txt")).First(l => l.StartsWith(stream + " ")).Split(' ');
        var path = Path.Combine(Dir, stream);
        Assert.True(File.Exists(path), "codestream missing: run fetch/fetch_jp2_conformance.sh of the test-data repository again");
        var data = File.ReadAllBytes(path);
        Assert.Equal(line[1], Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant());
        int reduce = int.Parse(line[2]);
        var planes = Jp2.DecodeCodestreamPlanes(data, new Jp2DecodeOptions { Reduce = reduce });
        // the same through the public entry point, on one thread
        var img = Jp2.Decode(data, new Jp2DecodeOptions { Reduce = reduce, RawComponents = true, Parallel = false });
        Assert.Equal((planes.Width, planes.Height, planes.Planes.Length), (img.Width, img.Height, img.Channels));
        for (int c = 0; 3 + 4 * c < line.Length; c++)
        {
            var refPath = Path.Combine(Dir, line[3 + 4 * c]);
            Assert.True(File.Exists(refPath), "reference image missing: run fetch/fetch_jp2_conformance.sh of the test-data repository again");
            Assert.Equal(line[4 + 4 * c], Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(refPath))).ToLowerInvariant());
            int peakLimit = int.Parse(line[5 + 4 * c]);
            double mseLimit = double.Parse(line[6 + 4 * c], System.Globalization.CultureInfo.InvariantCulture);
            var (expected, w, h) = ReadPgx(refPath);
            var plane = planes.Planes[c];
            Assert.Equal((w, h), (plane.Width, plane.Height));
            long peak = 0;
            double sum = 0;
            for (int i = 0; i < expected.Length; i++)
            {
                long d = Math.Abs(plane.Data![i] - expected[i]);
                peak = Math.Max(peak, d);
                sum += (double)d * d;
            }
            double mse = sum / expected.Length;
            Assert.True(peak <= peakLimit && mse <= mseLimit, $"{stream} component {c}: peak {peak} (allowed {peakLimit}), MSE {mse:F3} (allowed {mseLimit})");
        }
    }
}
