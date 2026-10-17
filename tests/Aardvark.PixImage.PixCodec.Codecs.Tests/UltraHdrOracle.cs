using System.Diagnostics;
using System.Globalization;
using Xunit;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>
/// libultrahdr's ultrahdr_app (Apache-2.0), run as the Ultra HDR / ISO 21496-1 oracle: $PIXCODEC_ULTRAHDR, ultrahdr_app on
/// the PATH, or the build of tests/oracle/ci/build.sh under .tmp/ci-tools. Built with UHDR_WRITE_XMP and UHDR_WRITE_ISO,
/// so its files carry both metadata forms.
/// </summary>
public static class UltraHdrOracle
{
    static readonly string? s_exe = Probe();

    static string? Probe()
    {
        var env = Environment.GetEnvironmentVariable("PIXCODEC_ULTRAHDR");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        var onPath = TestUtil.FindOnPath("ultrahdr_app");
        if (onPath != null) return onPath;
        var local = Path.Combine(TestUtil.FixtureDir, "..", "..", ".tmp", "ci-tools", "src", "libultrahdr", "build", OperatingSystem.IsWindows() ? "ultrahdr_app.exe" : "ultrahdr_app");
        return File.Exists(local) ? Path.GetFullPath(local) : null;
    }

    /// <summary>The executable, or null (a failure under PIXCODEC_REQUIRE_ORACLES=1).</summary>
    public static string? Exe => Oracles.Have("ultrahdr", s_exe != null, "libultrahdr's ultrahdr_app ($PIXCODEC_ULTRAHDR, on the PATH, or built by tests/oracle/ci/build.sh)") ? s_exe : null;

    static string Tmp()
    {
        var d = Path.Combine(TestUtil.FixtureDir, "_tmp", "uhdr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    static string Run(string exe, string args, string dir)
    {
        var psi = new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = dir };
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        string output = so.Result + se.Result;
        Assert.True(p.ExitCode == 0, $"ultrahdr_app {args} failed ({p.ExitCode}): {output}");
        return output;
    }

    /// <summary>
    /// Decodes with ultrahdr_app to linear half float RGBA (-o 0 -O 4) at its default (full) display boost and returns
    /// linear Float32 RGB, plus the gain map metadata it reports (-f).
    /// </summary>
    public static (RawImage Image, Dictionary<string, double> Metadata) Decode(string exe, byte[] jpeg)
    {
        var d = Tmp();
        try
        {
            File.WriteAllBytes(Path.Combine(d, "in.jpg"), jpeg);
            Run(exe, "-m 1 -j in.jpg -o 0 -O 4 -z out.raw -f meta.cfg", d);
            var info = Jpeg.GetInfo(jpeg);
            var raw = File.ReadAllBytes(Path.Combine(d, "out.raw"));
            int n = info.Width * info.Height;
            Assert.Equal(n * 8, raw.Length);
            var f = new float[n * 3];
            for (int i = 0; i < n; i++)
                for (int c = 0; c < 3; c++) f[i * 3 + c] = (float)BitConverter.ToHalf(raw, (i * 4 + c) * 2);
            var meta = new Dictionary<string, double>();
            foreach (var line in File.ReadAllLines(Path.Combine(d, "meta.cfg")))
            {
                var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[0].StartsWith("--") && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                    meta[parts[0][2..]] = v;
            }
            return (new RawImage(info.Width, info.Height, ColorModel.Rgb, f), meta);
        }
        finally { Directory.Delete(d, true); }
    }

    /// <summary>
    /// Encodes an Ultra HDR JPEG with ultrahdr_app from a linear HDR image (half float RGBA, -a 4 -t 0) and an 8 bit sRGB
    /// SDR image (rgba8888, -b 3), both BT.709 (-C 0 -c 0); <paramref name="extra"/> adds options (e.g. "-s 4 -M 0").
    /// </summary>
    public static byte[] Encode(string exe, RawImage hdrLinear, RawImage sdr8, string extra = "")
    {
        int w = hdrLinear.Width, h = hdrLinear.Height, n = w * h;
        var d = Tmp();
        try
        {
            var hb = new byte[n * 8];
            var hf = (float[])hdrLinear.Data;
            for (int i = 0; i < n; i++)
                for (int c = 0; c < 4; c++)
                    BitConverter.TryWriteBytes(hb.AsSpan((i * 4 + c) * 2), c == 3 ? (Half)1 : (Half)hf[i * 3 + c]);
            var sb = new byte[n * 4];
            var s8 = (byte[])sdr8.Data;
            for (int i = 0; i < n; i++) { sb[i * 4] = s8[i * 3]; sb[i * 4 + 1] = s8[i * 3 + 1]; sb[i * 4 + 2] = s8[i * 3 + 2]; sb[i * 4 + 3] = 255; }
            File.WriteAllBytes(Path.Combine(d, "hdr.raw"), hb);
            File.WriteAllBytes(Path.Combine(d, "sdr.raw"), sb);
            Run(exe, $"-m 0 -p hdr.raw -y sdr.raw -a 4 -b 3 -t 0 -C 0 -c 0 -w {w} -h {h} {extra} -z out.jpg", d);
            return File.ReadAllBytes(Path.Combine(d, "out.jpg"));
        }
        finally { Directory.Delete(d, true); }
    }
}
