using PixCodec;
using Xunit;

namespace PixCodec.Tests;

/// <summary>
/// The reference decoder for EXR tests (tests/oracle/decode_to_ref.py with the OpenEXR python bindings). Returns null
/// when the file cannot be verified on this machine: no python, no bindings (exit status 77), or bindings older than
/// the compression used. Extension tokens: ".all" all channels, ".partN" part N, ".deep" deep sample summary.
/// </summary>
public static class ExrOracle
{
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
            if (exit == 77) return null;
            Assert.True(exit == 0, "oracle decode failed: " + output);
            return TestUtil.LoadRef(f + ".ref");
        }
        finally
        {
            File.Delete(f); File.Delete(f + ".ref");
        }
    }
}
