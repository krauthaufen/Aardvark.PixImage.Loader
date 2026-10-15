using System.Buffers.Binary;
using Aardvark.ImageCodecs.Compression;
using Xunit;
using static Aardvark.ImageCodecs.Tests.TestUtil;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>
/// Just enough KTX2 to feed the Basis Universal decoders in tests: header, level index, DFD colour model and the
/// supercompression global data. Stand-in until the library has its own KTX2 reader.
/// </summary>
internal sealed class Ktx2Mini
{
    public int VkFormat, Width, Height, Depth, Layers, Faces, Levels, Scheme, ColorModel;
    public byte[] Data = Array.Empty<byte>();
    public (long offset, long length, long uncompressed)[] LevelIndex = Array.Empty<(long, long, long)>();
    public byte[] Sgd = Array.Empty<byte>();

    static readonly byte[] Magic = { 0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A };

    public static Ktx2Mini Parse(byte[] d)
    {
        Assert.True(d.AsSpan(0, 12).SequenceEqual(Magic), "not KTX2");
        int U32(int o) => (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));
        long U64(int o) => (long)BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(o));
        var k = new Ktx2Mini
        {
            Data = d, VkFormat = U32(12), Width = U32(20), Height = U32(24), Depth = U32(28), Layers = U32(32),
            Faces = U32(36), Levels = Math.Max(1, U32(40)), Scheme = U32(44),
        };
        int dfd = U32(48);
        k.ColorModel = d[dfd + 4 + 8];   // after totalSize, vendor/type, version/size
        long sgdOff = U64(64), sgdLen = U64(72);
        k.Sgd = d.AsSpan((int)sgdOff, (int)sgdLen).ToArray();
        k.LevelIndex = new (long, long, long)[k.Levels];
        for (int l = 0; l < k.Levels; l++) k.LevelIndex[l] = (U64(80 + 24 * l), U64(88 + 24 * l), U64(96 + 24 * l));
        return k;
    }

    public int LevelWidth(int l) => Math.Max(1, Width >> l);
    public int LevelHeight(int l) => Math.Max(1, Height >> l);

    /// <summary>The level's data, zstd decompressed where needed.</summary>
    public byte[] LevelData(int l)
    {
        var (o, n, u) = LevelIndex[l];
        var raw = Data.AsSpan((int)o, (int)n);
        if (Scheme != 2) return raw.ToArray();
        var outp = new byte[u];
        Assert.Equal((int)u, Zstd.Decompress(raw, outp));
        return outp;
    }
}

public class BasisTests
{
    public static IEnumerable<object[]> Fixtures => Files("basis", ".ktx2");

    const int ModelEtc1s = 163, ModelUastc = 166;

    /// <summary>Decodes every image of the file, (level, layer, face) -> RGBA8.</summary>
    static Dictionary<(int, int, int), byte[]> DecodeAll(Ktx2Mini k)
    {
        var res = new Dictionary<(int, int, int), byte[]>();
        int layers = Math.Max(1, k.Layers);
        Assert.Equal(0, k.VkFormat);
        Assert.True(k.Depth <= 1);
        if (k.Scheme == 1)
        {
            Assert.Equal(ModelEtc1s, k.ColorModel);
            var g = BasisEtc1s.ParseGlobal(k.Sgd, BasisEtc1s.ImageCount(k.Levels, k.Layers, k.Faces, k.Depth));
            for (int l = 0; l < k.Levels; l++)
            {
                var level = k.LevelData(l);
                int w = k.LevelWidth(l), h = k.LevelHeight(l);
                var video = new BasisLzVideoState();
                for (int layer = 0; layer < layers; layer++)
                    for (int f = 0; f < k.Faces; f++)
                    {
                        var px = new byte[w * h * 4];
                        BasisEtc1s.DecodeImage(g, BasisEtc1s.ImageIndex(l, layer, f, 0, k.Layers, k.Faces, k.Depth), level, w, h, px, video);
                        res[(l, layer, f)] = px;
                    }
            }
        }
        else
        {
            Assert.Equal(ModelUastc, k.ColorModel);
            for (int l = 0; l < k.Levels; l++)
            {
                var level = k.LevelData(l);
                int w = k.LevelWidth(l), h = k.LevelHeight(l);
                int size = ((w + 3) / 4) * ((h + 3) / 4) * 16;
                Assert.Equal(size * layers * k.Faces, level.Length);
                for (int layer = 0; layer < layers; layer++)
                    for (int f = 0; f < k.Faces; f++)
                    {
                        var px = new byte[w * h * 4];
                        BasisUastc.DecodeImage(level.AsSpan((layer * k.Faces + f) * size, size), w, h, px);
                        res[(l, layer, f)] = px;
                    }
            }
        }
        return res;
    }

    [Theory, MemberData(nameof(Fixtures))]
    public void DecodeMatchesBasisu(string file)
    {
        var path = Fixture(file);
        var k = Ktx2Mini.Parse(File.ReadAllBytes(path));
        var all = DecodeAll(k);
        var refs = Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*.*.*.ref");
        Assert.Equal(all.Count, refs.Length);
        foreach (var r in refs)
        {
            var key = r[(path.Length + 1)..^4].Split('.').Select(int.Parse).ToArray();
            var expected = LoadRef(r);
            var got = all[(key[0], key[1], key[2])];
            Assert.Equal((k.LevelWidth(key[0]), k.LevelHeight(key[0]), 4), (expected.Width, expected.Height, expected.Channels));
            var e = expected.GetData<byte>();
            int bad = -1;
            for (int i = 0; i < e.Length && bad < 0; i++) if (e[i] != got[i]) bad = i;
            Assert.True(bad < 0, bad < 0 ? "" :
                $"{Path.GetFileName(r)}: first difference at x {bad / 4 % expected.Width} y {bad / 4 / expected.Width} channel {bad % 4}: " +
                $"expected {e[bad]}, got {got[bad]}");
        }
    }

    /// <summary>The fixtures reach every UASTC mode (the reserved one aside).</summary>
    [Fact]
    public void UastcFixturesCoverAllModes()
    {
        var seen = new HashSet<int>();
        foreach (var row in Fixtures)
        {
            var k = Ktx2Mini.Parse(File.ReadAllBytes(Fixture((string)row[0])));
            if (k.Scheme == 1) continue;
            for (int l = 0; l < k.Levels; l++)
            {
                var d = k.LevelData(l);
                for (int i = 0; i < d.Length; i += 16) seen.Add(UastcMode(d[i]));
            }
        }
        var missing = Enumerable.Range(0, 19).Where(m => !seen.Contains(m)).ToArray();
        Assert.True(missing.Length == 0, "UASTC modes not covered: " + string.Join(", ", missing));
    }

    static int UastcMode(byte b)
    {
        // the mode's prefix code, low bit first (UASTC specification)
        (int code, int len)[] codes =
        {
            (0x1, 4), (0x35, 6), (0x1D, 5), (0x3, 5), (0x13, 5), (0xB, 5), (0x1B, 5), (0x7, 5), (0x17, 5), (0xF, 5),
            (0x2, 3), (0x0, 2), (0x6, 3), (0x1F, 5), (0xD, 5), (0x5, 7), (0x15, 6), (0x25, 6), (0x9, 4), (0x45, 7),
        };
        for (int m = 0; m < codes.Length; m++)
            if ((b & ((1 << codes[m].len) - 1)) == codes[m].code) return m;
        return -1;
    }

    /// <summary>Endpoint unquantization agrees with the UASTC specification's dequantization tables.</summary>
    [Fact]
    public void UastcUnquantizeMatchesSpecTables()
    {
        // BISE integer i -> 8 bit value, from the "BISE Endpoint Decoding Dequantization Tables" (ranges UASTC uses)
        var tables = new Dictionary<int, int[]>
        {
            [7] = new[] { 0, 255, 69, 186, 23, 232, 92, 163, 46, 209, 116, 139 },
            [12] = new[] { 0, 255, 32, 223, 65, 190, 97, 158, 6, 249, 39, 216, 71, 184, 104, 151, 13, 242, 45, 210, 78, 177, 110, 145, 19, 236, 52, 203, 84, 171, 117, 138, 26, 229, 58, 197, 91, 164, 123, 132 },
            [13] = new[] { 0, 255, 16, 239, 32, 223, 48, 207, 65, 190, 81, 174, 97, 158, 113, 142, 5, 250, 21, 234, 38, 217, 54, 201, 70, 185, 86, 169, 103, 152, 119, 136, 11, 244, 27, 228, 43, 212, 59, 196, 76, 179, 92, 163, 108, 147, 124, 131 },
        };
        foreach (var (range, t) in tables)
            for (int i = 0; i < t.Length; i++) Assert.Equal(t[i], BasisUastc.Unquantize(i, range));
        // ranges 18 and 19: spot checks
        Assert.Equal(new[] { 0, 255, 8, 247, 16, 239 }, Enumerable.Range(0, 6).Select(i => BasisUastc.Unquantize(i, 18)));
        Assert.Equal(new[] { 1, 254, 9, 246 }, Enumerable.Range(32, 4).Select(i => BasisUastc.Unquantize(i, 18)));
        Assert.Equal(new[] { 0, 255, 4, 251, 8, 247 }, Enumerable.Range(0, 6).Select(i => BasisUastc.Unquantize(i, 19)));
        Assert.Equal(new[] { 1, 254, 5, 250 }, Enumerable.Range(64, 4).Select(i => BasisUastc.Unquantize(i, 19)));
        // binary ranges replicate bits
        Assert.Equal(new[] { 0, 17, 255 }, new[] { 0, 1, 15 }.Select(i => BasisUastc.Unquantize(i, 8)));
        Assert.Equal(new[] { 0, 8, 255 }, new[] { 0, 1, 31 }.Select(i => BasisUastc.Unquantize(i, 11)));
    }

    [Fact]
    public void VideoHasPFrames()
    {
        var k = Ktx2Mini.Parse(File.ReadAllBytes(Fixture("basis/etc1s_video_32x24.ktx2")));
        var g = BasisEtc1s.ParseGlobal(k.Sgd, BasisEtc1s.ImageCount(k.Levels, k.Layers, k.Faces, k.Depth));
        Assert.Equal(4, k.Layers);
        Assert.Contains(g.Images, d => d.IsPFrame);
        Assert.All(g.Images, d => Assert.True(d.HasAlpha));
        // a P-frame needs the previous frame
        int p = Array.FindIndex(g.Images, d => d.IsPFrame);
        var px = new byte[32 * 24 * 4];
        Assert.Throws<ImageFormatException>(() => BasisEtc1s.DecodeImage(g, p, k.LevelData(0), 32, 24, px));
    }

    [Fact]
    public void ImageIndexOrder()
    {
        // level-major, then layer, face, z slice (KTX 2.0 Appendix C)
        Assert.Equal(18, BasisEtc1s.ImageCount(3, 6, 1, 0));
        Assert.Equal(18, BasisEtc1s.ImageCount(3, 0, 6, 0));
        Assert.Equal(3 * 6 + 2 * 6 + 4, BasisEtc1s.ImageIndex(1, 2, 4, 0, 3, 6, 0));
        Assert.Equal(4 + 2 + 1 + 1, BasisEtc1s.ImageCount(4, 0, 1, 4));
        Assert.Equal(4 + 2 + 1, BasisEtc1s.ImageIndex(2, 0, 0, 1, 0, 1, 4));
    }

    [Fact]
    public void RejectsCorruptData()
    {
        var k = Ktx2Mini.Parse(File.ReadAllBytes(Fixture("basis/etc1s_rgba_37x21.ktx2")));
        Assert.Throws<ImageFormatException>(() => BasisEtc1s.ParseGlobal(k.Sgd.AsSpan(0, 30), 1));
        var g = BasisEtc1s.ParseGlobal(k.Sgd, 1);
        var level = k.LevelData(0);
        var px = new byte[37 * 21 * 4];
        Assert.Throws<ImageFormatException>(() => BasisEtc1s.DecodeImage(g, 0, level.AsSpan(0, 3), 37, 21, px));
        // random slice data: an exception of the library's type or a decoded image, never anything else
        var rnd = new Random(1);
        for (int i = 0; i < 200; i++)
        {
            var junk = (byte[])level.Clone();
            for (int j = 0; j < 4; j++) junk[rnd.Next(junk.Length)] = (byte)rnd.Next(256);
            try { BasisEtc1s.DecodeImage(g, 0, junk, 37, 21, px); }
            catch (ImageFormatException) { }
        }
    }
}
