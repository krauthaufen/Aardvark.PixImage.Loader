using Aardvark.ImageCodecs;
using Xunit;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>
/// The reference decoder for EXR tests (tests/oracle/decode_to_ref.py with the OpenEXR python bindings). Returns null
/// when the file cannot be verified on this machine: no python, no bindings (exit status 77), or bindings older than
/// the compression used. Extension tokens: ".all" all channels, ".partN" part N, ".deep" deep sample summary.
/// </summary>
public static class ExrOracle
{
    /// <summary>
    /// The reference library's decode of a DWA file, or its RGBA (luminance / chroma) interface, against ours: bit exact on
    /// Linux x64, where the fixture references were made. Elsewhere the reference library's float arithmetic rounds
    /// differently (Windows x64: MSVC builds, macOS arm64) - our decoder still matches the fixture references bit for bit
    /// there (ExrDecodeTests) - so up to 4 half float steps are allowed.
    /// </summary>
    public static void AssertMatchesLossy(RawImage reference, RawImage ours)
    {
        if (OperatingSystem.IsLinux() && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64)
        {
            TestUtil.AssertEqual(reference, ours);
            return;
        }
        TestUtil.MaxDiff(reference, ours);  // geometry and sample type
        var e = TestUtil.ToDoubles(reference); var a = TestUtil.ToDoubles(ours);
        for (int i = 0; i < e.Length; i++)
        {
            if (e[i] == a[i] || double.IsNaN(e[i]) && double.IsNaN(a[i])) continue;
            double m = Math.Max(Math.Abs(e[i]), Math.Abs(a[i]));
            double step = m < 6.103515625e-05 ? Math.ScaleB(1, -24) : Math.ScaleB(1, Math.ILogB(m) - 10);   // half float spacing at m
            Assert.True(Math.Abs(e[i] - a[i]) <= 4 * step, $"sample {i}: reference {e[i]}, ours {a[i]}");
        }
    }

    public static RawImage? Decode(byte[] encoded, string extension)
    {
        if (!TestUtil.PythonAvailable) return null;
        // tell the script which newer features the file needs, so that it can tell "too old" from "wrong"
        foreach (var h in Exr.ReadHeaders(encoded))
        {
            if (h.Compression == ExrCompression.Zstd && !extension.Contains(".zstd")) extension = ".zstd" + extension;
            if (h.Compression is ExrCompression.Htj2k256 or ExrCompression.Htj2k32 && !extension.Contains(".htj2k")) extension = ".htj2k" + extension;
        }
        var tmp = Path.Combine(TestUtil.FixtureDir, "_tmp");
        Directory.CreateDirectory(tmp);
        var f = Path.Combine(tmp, Guid.NewGuid().ToString("N") + extension);
        try
        {
            File.WriteAllBytes(f, encoded);
            var (exit, output) = TestUtil.RunPython($"decode_to_ref.py \"{f}\" \"{f}.ref\"");
            if (TestUtil.CannotVerify(exit, output)) return null;
            Assert.True(exit == 0, "oracle decode failed: " + output);
            return TestUtil.LoadRef(f + ".ref");
        }
        finally
        {
            File.Delete(f); File.Delete(f + ".ref");
        }
    }
}
