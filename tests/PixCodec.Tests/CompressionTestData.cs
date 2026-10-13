using System.Diagnostics;
using PixCodec.Compression;
using Xunit;

namespace PixCodec.Tests;

/// <summary>Deterministic data shapes and helpers shared by the compression tests.</summary>
public static class CompressionTestData
{
    public static readonly string[] Shapes =
    {
        "empty", "one", "two", "three", "zeros", "random", "text", "image", "image16", "runs", "far", "max", "mixed", "sparse", "big",
    };

    public static byte[] Make(string shape, int seed = 1)
    {
        var rnd = new Random(seed * 7919 + shape.Length);
        switch (shape)
        {
            case "empty": return Array.Empty<byte>();
            case "one": return new byte[] { 42 };
            case "two": return new byte[] { 7, 7 };
            case "three": return new byte[] { 1, 2, 3 };
            case "zeros": return new byte[70_000];
            case "random": { var b = new byte[40_000]; rnd.NextBytes(b); return b; }
            case "text": return Text(rnd, 60_000);
            case "image": return FilteredImage(rnd, 211, 97, 3, false);
            case "image16": return FilteredImage(rnd, 150, 60, 3, true);
            case "runs":
            {
                var b = new byte[50_000];
                for (int i = 0; i < b.Length;)
                {
                    int n = Math.Min(b.Length - i, 1 + rnd.Next(rnd.Next(4) == 0 ? 2000 : 12));
                    byte v = (byte)rnd.Next(5);
                    b.AsSpan(i, n).Fill(v);
                    i += n;
                }
                return b;
            }
            case "far":
            {
                // repeats at distances right up to the 32768 window limit
                var b = new byte[140_000];
                rnd.NextBytes(b.AsSpan(0, 32768));
                for (int i = 32768; i < b.Length; i++)
                    b[i] = rnd.Next(300) == 0 ? (byte)rnd.Next(256) : b[i - 32768 + (i / 7000 % 3)];
                return b;
            }
            case "max":
            {
                // long matches (258) with all kinds of short distances
                var b = new byte[60_000];
                int period = 1;
                for (int i = 0; i < b.Length; i++)
                {
                    if (i % 3000 == 0) period = 1 + rnd.Next(40);
                    b[i] = i < period || i % 3000 < period ? (byte)rnd.Next(256) : b[i - period];
                }
                return b;
            }
            case "mixed":
            {
                var parts = new[] { Text(rnd, 30_000), FilteredImage(rnd, 100, 80, 4, false), new byte[9000], Make("random", seed + 1).AsSpan(0, 20_000).ToArray(), Text(rnd, 20_000) };
                return parts.SelectMany(x => x).ToArray();
            }
            case "sparse":
            {
                var b = new byte[90_000];
                for (int i = 0; i < 400; i++) b[rnd.Next(b.Length)] = (byte)rnd.Next(256);
                return b;
            }
            case "big":
            {
                var parts = new List<byte[]>();
                for (int i = 0; i < 4; i++) { parts.Add(FilteredImage(rnd, 300, 120, 3, false)); parts.Add(Text(rnd, 50_000)); }
                return parts.SelectMany(x => x).ToArray();
            }
            default: throw new ArgumentException(shape);
        }
    }

    static readonly string[] Words =
    {
        "the", "of", "and", "image", "codec", "pixel", "stream", "window", "a", "to", "in", "is", "that", "block", "huffman",
        "distance", "length", "literal", "compress", "table", "buffer", "value", "for", "with", "this", "from", "data", "byte",
    };

    public static byte[] Text(Random rnd, int length)
    {
        var sb = new System.Text.StringBuilder(length + 32);
        while (sb.Length < length)
        {
            sb.Append(Words[(int)(Math.Pow(rnd.NextDouble(), 2) * Words.Length)]);
            sb.Append(rnd.Next(12) == 0 ? ".\n" : rnd.Next(20) == 0 ? " " + rnd.Next(100000) + " " : " ");
        }
        return System.Text.Encoding.ASCII.GetBytes(sb.ToString(0, length));
    }

    /// <summary>Scanlines of a smooth, slightly noisy image, PNG filtered (filter byte + Sub/Up/Paeth-like residuals).</summary>
    public static byte[] FilteredImage(Random rnd, int width, int height, int channels, bool sixteen)
    {
        int bps = sixteen ? 2 : 1, bpp = channels * bps, rowBytes = width * bpp;
        var img = new byte[height * rowBytes];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                for (int c = 0; c < channels; c++)
                {
                    double v = 0.5 + 0.3 * Math.Sin(x * 0.031 + c) * Math.Cos(y * 0.047 - c) + 0.15 * Math.Sin((x + y) * 0.11);
                    if ((x / 40 + y / 30) % 5 == 0) v = 0.25 + 0.1 * c; // flat patches
                    v += (rnd.NextDouble() - 0.5) * (y % 64 < 40 ? 0.012 : 0.06);
                    int q = (int)Math.Clamp(v * (sixteen ? 65535 : 255), 0, sixteen ? 65535 : 255);
                    int o = y * rowBytes + x * bpp + c * bps;
                    if (sixteen) { img[o] = (byte)(q >> 8); img[o + 1] = (byte)q; } else img[o] = (byte)q;
                }
        var output = new byte[height * (rowBytes + 1)];
        for (int y = 0; y < height; y++)
        {
            int filter = y == 0 ? 1 : (y % 3 == 0 ? 2 : y % 3 == 1 ? 4 : 1);
            int o = y * (rowBytes + 1);
            output[o] = (byte)filter;
            for (int i = 0; i < rowBytes; i++)
            {
                int a = i >= bpp ? img[y * rowBytes + i - bpp] : 0;
                int b = y > 0 ? img[(y - 1) * rowBytes + i] : 0;
                int c = i >= bpp && y > 0 ? img[(y - 1) * rowBytes + i - bpp] : 0;
                int pred = filter == 1 ? a : filter == 2 ? b : Paeth(a, b, c);
                output[o + 1 + i] = (byte)(img[y * rowBytes + i] - pred);
            }
        }
        return output;
    }

    static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>Random data from a random generator family, for the differential fuzz tests.</summary>
    public static byte[] Fuzz(Random rnd)
    {
        int kind = rnd.Next(9);
        int n = rnd.Next(4) == 0 ? rnd.Next(300_000) : rnd.Next(5000);
        var b = new byte[n];
        switch (kind)
        {
            case 0: rnd.NextBytes(b); break;
            case 1: break;
            case 2: { int alpha = 1 + rnd.Next(6); for (int i = 0; i < n; i++) b[i] = (byte)rnd.Next(alpha); break; }
            case 3: return Text(rnd, n);
            case 4:
            {
                // copies from random earlier places with random lengths: exercises every distance and length
                int i = 0;
                while (i < n)
                {
                    if (i == 0 || rnd.Next(5) == 0) { b[i++] = (byte)rnd.Next(256); continue; }
                    int dist = 1 + rnd.Next(Math.Min(i, rnd.Next(3) == 0 ? 40_000 : 300));
                    int len = Math.Min(n - i, 1 + rnd.Next(rnd.Next(4) == 0 ? 600 : 20));
                    for (int k = 0; k < len; k++, i++) b[i] = b[i - dist];
                }
                break;
            }
            case 5: { int w = 1 + rnd.Next(64), h = Math.Max(1, n / (w * 3 + 1)); return FilteredImage(rnd, w, h, 3, false); }
            case 6: { for (int i = 0; i < n; i++) b[i] = (byte)(i < 1 ? 0 : b[i - 1] + rnd.Next(3) - 1); break; }
            case 7: { for (int i = 0; i < n; i++) b[i] = (byte)(rnd.Next(50) == 0 ? rnd.Next(256) : 0); break; }
            default:
            {
                int i = 0;
                while (i < n)
                {
                    int len = Math.Min(n - i, 1 + rnd.Next(3000));
                    if (rnd.Next(2) == 0) rnd.NextBytes(b.AsSpan(i, len)); else b.AsSpan(i, len).Fill((byte)rnd.Next(256));
                    i += len;
                }
                break;
            }
        }
        return b;
    }

    /// <summary>Iteration multiplier for the fuzz tests (PIXCODEC_FUZZ=10 runs ten times as many).</summary>
    public static int FuzzScale { get; } = int.TryParse(Environment.GetEnvironmentVariable("PIXCODEC_FUZZ"), out int v) && v > 0 ? v : 1;

    // ---- the BCL (native zlib) used directly, independent of Zlib.Backend

    public static byte[] BclCompress(byte[] data, System.IO.Compression.CompressionLevel level, bool zlibWrapper = true)
    {
        using var o = new MemoryStream();
        using (Stream z = zlibWrapper ? new System.IO.Compression.ZLibStream(o, level, true) : new System.IO.Compression.DeflateStream(o, level, true))
            z.Write(data);
        return o.ToArray();
    }

    public static byte[] BclDecompress(byte[] z, bool zlibWrapper = true)
    {
        using var ms = new MemoryStream(z);
        using Stream s = zlibWrapper ? new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionMode.Decompress) : new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionMode.Decompress);
        using var o = new MemoryStream();
        s.CopyTo(o);
        return o.ToArray();
    }

    // ---- fixtures: <shape>.bin is the raw data of every <shape>__<variant>.<ext>

    public static IEnumerable<object[]> Fixtures(params string[] extensions)
    {
        var dir = Path.Combine(TestUtil.FixtureDir, "compression");
        if (!Directory.Exists(dir)) yield break;
        foreach (var f in Directory.GetFiles(dir).OrderBy(f => f, StringComparer.Ordinal))
            if (extensions.Contains(Path.GetExtension(f))) yield return new object[] { Path.GetFileName(f) };
    }

    public static byte[] Load(string name) => File.ReadAllBytes(Fixture(name));

    public static byte[] Expected(string fixture) => Load(fixture.Substring(0, fixture.IndexOf("__", StringComparison.Ordinal)) + ".bin");

    /// <summary>
    /// Runs many byte strings through one python process. The script sees `items` (list of bytes) and must
    /// set `results` (list of bytes). Returns null when python is unavailable.
    /// </summary>
    public static List<byte[]>? PythonBatch(string code, IReadOnlyList<byte[]> items)
    {
        var input = new MemoryStream();
        var w = new BinaryWriter(input);
        w.Write(items.Count);
        foreach (var it in items) { w.Write(it.Length); w.Write(it); }
        var output = Python(
            "import struct\nn=struct.unpack_from('<i',d,0)[0]\np=4\nitems=[]\n" +
            "for _ in range(n):\n l=struct.unpack_from('<i',d,p)[0]\n items.append(d[p+4:p+4+l])\n p+=4+l\n" +
            code + "\nsys.stdout.buffer.write(struct.pack('<i',len(results)))\n" +
            "for r in results:\n sys.stdout.buffer.write(struct.pack('<i',len(r)))\n sys.stdout.buffer.write(r)\n", input.ToArray());
        if (output == null) return null;
        var r = new BinaryReader(new MemoryStream(output));
        int count = r.ReadInt32();
        var list = new List<byte[]>(count);
        for (int i = 0; i < count; i++) list.Add(r.ReadBytes(r.ReadInt32()));
        return list;
    }

    public static string Fixture(string name) => Path.Combine(TestUtil.FixtureDir, "compression", name);

    /// <summary>
    /// Pipes <paramref name="input"/> through a python one-liner that reads sys.stdin.buffer and writes
    /// sys.stdout.buffer. Returns null when python is unavailable.
    /// </summary>
    public static byte[]? Python(string code, byte[] input)
    {
        if (!TestUtil.PythonAvailable) return null;
        var psi = new ProcessStartInfo("python3")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("import sys,zlib,lzma\nd=sys.stdin.buffer.read()\n" + code);
        using var p = Process.Start(psi)!;
        var so = new MemoryStream();
        var copy = p.StandardOutput.BaseStream.CopyToAsync(so);
        var se = p.StandardError.ReadToEndAsync();
        p.StandardInput.BaseStream.Write(input);
        p.StandardInput.Close();
        copy.Wait();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, "python failed: " + se.Result);
        return so.ToArray();
    }
}

/// <summary>Tests that switch <see cref="Zlib.Backend"/> must not run in parallel with other tests using Zlib.</summary>
[CollectionDefinition("ZlibBackend", DisableParallelization = true)]
public class ZlibBackendCollection { }
