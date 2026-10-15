using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Aardvark.ImageCodecs;
using Xunit;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>
/// A fact on third-party test data (see <see cref="TestUtil.TestDataDir"/>): skipped when the test-data repository, or
/// one of the paths given (relative to it), is missing.
/// </summary>
public sealed class TestDataFactAttribute : FactAttribute
{
    public TestDataFactAttribute(params string[] requires) => Skip = TestUtil.TestDataSkipReason(requires);
}

/// <summary>A theory on third-party test data, skipped like <see cref="TestDataFactAttribute"/>.</summary>
public sealed class TestDataTheoryAttribute : TheoryAttribute
{
    public TestDataTheoryAttribute(params string[] requires) => Skip = TestUtil.TestDataSkipReason(requires);
}

public static class TestUtil
{
    /// <summary>tests/fixtures (found by walking up from the test binary).</summary>
    public static string FixtureDir { get; } = FindUp("tests/fixtures");
    public static string OracleDir { get; } = FindUp("tests/oracle");

    static string FindUp(string rel)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            var p = Path.Combine(d.FullName, rel);
            if (Directory.Exists(p)) return p;
            d = d.Parent;
        }
        throw new DirectoryNotFoundException(rel);
    }

    /// <summary>
    /// The test-data repository with the third-party test files (PngSuite, openexr-images samples, the lists of
    /// downloaded conformance files): $PIXCODEC_TESTDATA, or Aardvark.PixImage.PixCodec.TestData next to this repository.
    /// Null when there is none; the tests that need it are then skipped (<see cref="TestDataFactAttribute"/>).
    /// </summary>
    public static string? TestDataDir { get; } = FindTestData();

    /// <summary>fixtures/ of the test-data repository: the same layout as tests/fixtures.</summary>
    public static string? TestDataFixtureDir => TestDataDir == null ? null : Path.Combine(TestDataDir, "fixtures");

    static string TestDataCandidate()
    {
        var env = Environment.GetEnvironmentVariable("PIXCODEC_TESTDATA");
        return Path.GetFullPath(string.IsNullOrEmpty(env) ? Path.Combine(FixtureDir, "..", "..", "..", "Aardvark.PixImage.PixCodec.TestData") : env);
    }

    static string? FindTestData()
    {
        var d = TestDataCandidate();
        return Directory.Exists(Path.Combine(d, "fixtures")) ? d : null;
    }

    /// <summary>Why a test on third-party data can not run (null if it can): no test-data repository, or one of
    /// <paramref name="requires"/> (paths relative to it) is missing there.</summary>
    public static string? TestDataSkipReason(string[] requires)
    {
        if (TestDataDir == null)
            return $"no third-party test data at {TestDataCandidate()} (set PIXCODEC_TESTDATA or check out Aardvark.PixImage.PixCodec.TestData next to this repository)";
        foreach (var r in requires)
            if (!File.Exists(Path.Combine(TestDataDir, r)) && !Directory.Exists(Path.Combine(TestDataDir, r)))
                return $"{r} is missing in {TestDataDir} (downloaded files: see the README there)";
        return null;
    }

    /// <summary>A path in the test-data repository; only for tests marked <see cref="TestDataFactAttribute"/>.</summary>
    public static string TestData(string relative)
        => Path.Combine(TestDataDir ?? throw new InvalidOperationException("no test-data repository"), relative);

    /// <summary>A fixture in tests/fixtures, or else at the same place in fixtures/ of the test-data repository.</summary>
    public static string Fixture(string relative)
    {
        var own = Path.Combine(FixtureDir, relative);
        if (TestDataFixtureDir == null || File.Exists(own) || Directory.Exists(own)) return own;
        var third = Path.Combine(TestDataFixtureDir, relative);
        return File.Exists(third) || Directory.Exists(third) ? third : own;
    }

    /// <summary>All fixture files in tests/fixtures/&lt;dir&gt; with the extension, as xunit MemberData rows (relative paths).</summary>
    public static IEnumerable<object[]> Files(string dir, params string[] extensions) => FilesIn(FixtureDir, dir, extensions);

    /// <summary>
    /// <see cref="Files"/> for fixtures/&lt;dir&gt; of the test-data repository (no rows without it): MemberData of a
    /// <see cref="TestDataTheoryAttribute"/> theory. <see cref="Fixture"/> finds the files from the rows.
    /// </summary>
    public static IEnumerable<object[]> ThirdPartyFiles(string dir, params string[] extensions)
        => TestDataFixtureDir == null ? Enumerable.Empty<object[]>() : FilesIn(TestDataFixtureDir, dir, extensions);

    static IEnumerable<object[]> FilesIn(string fixtures, string dir, string[] extensions)
    {
        var root = Path.Combine(fixtures, dir);
        if (!Directory.Exists(root)) yield break;
        foreach (var f in Directory.GetFiles(root).OrderBy(f => f, StringComparer.Ordinal))
            if (extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                yield return new object[] { Path.Combine(dir, Path.GetFileName(f)) };
    }

    /// <summary>Loads a PXRF reference sidecar (see tests/oracle/pxref.py).</summary>
    public static RawImage LoadRef(string path)
    {
        var b = File.ReadAllBytes(path);
        Assert.True(b.AsSpan(0, 4).SequenceEqual("PXRF"u8), "bad ref magic");
        int w = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(4));
        int h = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(8));
        int c = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(12));
        var t = (SampleType)BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(16));
        var img = RawImage.Create(w, h, RawImage.DefaultColorModel(c), t, c);
        b.AsSpan(20).CopyTo(img.GetBytes());
        return img;
    }

    public static void SaveRef(string path, RawImage img)
    {
        using var f = File.Create(path);
        Span<byte> hdr = stackalloc byte[20];
        "PXRF"u8.CopyTo(hdr);
        BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(4), img.Width);
        BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(8), img.Height);
        BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(12), img.Channels);
        BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(16), (int)img.SampleType);
        f.Write(hdr);
        f.Write(img.GetBytes());
    }

    // switch on SampleType: sbyte[] also matches a `byte[]` pattern (CLR array covariance)
    public static double[] ToDoubles(RawImage img) => img.SampleType switch
    {
        SampleType.UInt8 => Array.ConvertAll((byte[])img.Data, x => (double)x),
        SampleType.Int8 => Array.ConvertAll((sbyte[])img.Data, x => (double)x),
        SampleType.UInt16 => Array.ConvertAll((ushort[])img.Data, x => (double)x),
        SampleType.Int16 => Array.ConvertAll((short[])img.Data, x => (double)x),
        SampleType.UInt32 => Array.ConvertAll((uint[])img.Data, x => (double)x),
        SampleType.Int32 => Array.ConvertAll((int[])img.Data, x => (double)x),
        SampleType.Float16 => Array.ConvertAll((Half[])img.Data, x => (double)x),
        SampleType.Float32 => Array.ConvertAll((float[])img.Data, x => (double)x),
        SampleType.Float64 => (double[])img.Data,
        // 64 bit integers lose precision as double: fine for tolerance checks, exact compares use the byte path
        SampleType.UInt64 => Array.ConvertAll((ulong[])img.Data, x => (double)x),
        SampleType.Int64 => Array.ConvertAll((long[])img.Data, x => (double)x),
        _ => throw new InvalidOperationException(),
    };

    /// <summary>Largest absolute per-sample difference; asserts matching geometry and sample type.</summary>
    public static double MaxDiff(RawImage expected, RawImage actual)
    {
        Assert.Equal((expected.Width, expected.Height, expected.Channels), (actual.Width, actual.Height, actual.Channels));
        Assert.Equal(expected.SampleType, actual.SampleType);
        var e = ToDoubles(expected); var a = ToDoubles(actual);
        double m = 0;
        for (int i = 0; i < e.Length; i++)
        {
            if (double.IsNaN(e[i]) && double.IsNaN(a[i])) continue;
            if (e[i] == a[i]) continue; // also covers equal infinities
            double d = Math.Abs(e[i] - a[i]);
            if (double.IsNaN(d)) d = double.PositiveInfinity;
            if (d > m) m = d;
        }
        return m;
    }

    /// <summary>Asserts geometry, sample type and samples match (bit exact when tolerance is 0).</summary>
    public static void AssertEqual(RawImage expected, RawImage actual, double tolerance = 0)
    {
        if (tolerance == 0 && expected.SampleType == actual.SampleType
            && (expected.Width, expected.Height, expected.Channels) == (actual.Width, actual.Height, actual.Channels)
            && expected.GetBytes().SequenceEqual(actual.GetBytes())) return;
        double m = MaxDiff(expected, actual);
        Assert.True(m <= tolerance, $"max sample difference {m} exceeds tolerance {tolerance}");
    }

    public static double Psnr(RawImage expected, RawImage actual, double peak)
    {
        MaxDiff(expected, actual);
        var e = ToDoubles(expected); var a = ToDoubles(actual);
        double se = 0;
        for (int i = 0; i < e.Length; i++) se += (e[i] - a[i]) * (e[i] - a[i]);
        return se == 0 ? double.PositiveInfinity : 10 * Math.Log10(peak * peak / (se / e.Length));
    }

    public static bool PythonAvailable { get; } = ProbePython();

    static bool ProbePython()
    {
        try { return RunPython("-c \"import numpy, PIL\"").exit == 0; } catch { return false; }
    }

    public static (int exit, string output) RunPython(string args)
    {
        var psi = new ProcessStartInfo("python3", args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            WorkingDirectory = OracleDir,
        };
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, so.Result + se.Result);
    }

    /// <summary>
    /// Decodes <paramref name="encoded"/> with an independent reference decoder (tests/oracle/decode_to_ref.py)
    /// and returns the result. Returns null when python is unavailable so tests can skip.
    /// </summary>
    public static RawImage? OracleDecode(byte[] encoded, string extension)
    {
        if (!PythonAvailable) return null;
        var tmp = Path.Combine(FixtureDir, "_tmp");
        Directory.CreateDirectory(tmp);
        var f = Path.Combine(tmp, Guid.NewGuid().ToString("N") + extension);
        try
        {
            File.WriteAllBytes(f, encoded);
            var (exit, output) = RunPython($"decode_to_ref.py \"{f}\" \"{f}.ref\"");
            Assert.True(exit == 0, "oracle decode failed: " + output);
            return LoadRef(f + ".ref");
        }
        finally
        {
            File.Delete(f); File.Delete(f + ".ref");
        }
    }
}
