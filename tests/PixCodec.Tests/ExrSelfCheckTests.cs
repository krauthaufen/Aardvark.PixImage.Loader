using System.Runtime.InteropServices;
using PixCodec;
using Xunit;

namespace PixCodec.Tests;

/// <summary>
/// Files written by OUR encoder that only the C++ reference helper can check completely (mip / rip levels above 0,
/// deep data). They were written once into tests/fixtures/exr/selfcheck (run this class with EXR_WRITE_SELFCHECK=1,
/// then tests/oracle/gen_exr.py) and the reference library's reading of every level / every deep sample is stored
/// next to them. The tests check that (1) our reader agrees with those references on the committed files, (2) what
/// the encoder produces today decodes to exactly the same data, and (3) for compressions that do not depend on the
/// zlib backend, that today's file is byte for byte the committed one - i.e. the file the reference library verified.
/// </summary>
public class ExrSelfCheckTests
{
    static string Dir => Path.Combine(TestUtil.FixtureDir, "exr", "selfcheck");
    static bool Writing => Environment.GetEnvironmentVariable("EXR_WRITE_SELFCHECK") == "1";

    static float[] Pattern(int w, int h, int c, int seed)
    {
        var rnd = new Random(seed);
        var f = new float[w * h * c];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int k = 0; k < c; k++)
                    f[(y * w + x) * c + k] = (float)(Half)(float)(Math.Abs(Math.Sin(x / (5.0 + k) + k) * Math.Cos(y / (3.0 + k))) + ((x / 6 + y / 5 + k) % 2) + 0.05 * rnd.NextDouble());
        return f;
    }

    static bool ZlibFree(ExrCompression c) => c is ExrCompression.None or ExrCompression.Rle or ExrCompression.Piz or ExrCompression.B44 or ExrCompression.B44A;

    static byte[] Check(string name, byte[] fresh, ExrCompression comp)
    {
        var path = Path.Combine(Dir, name);
        if (Writing) { Directory.CreateDirectory(Dir); File.WriteAllBytes(path, fresh); }
        var committed = File.ReadAllBytes(path);
        if (ZlibFree(comp)) Assert.True(committed.AsSpan().SequenceEqual(fresh), name + ": the encoder output changed; regenerate the selfcheck fixtures and their references");
        return committed;
    }

    public static IEnumerable<object[]> LevelCases() => new[]
    {
        new object[] { "mip_down_piz.exr", ExrCompression.Piz, ExrLevelMode.MipMap, false, 21, 13 },
        new object[] { "mip_up_rle.exr", ExrCompression.Rle, ExrLevelMode.MipMap, true, 19, 11 },
        new object[] { "rip_down_b44.exr", ExrCompression.B44, ExrLevelMode.RipMap, false, 12, 10 },
        new object[] { "rip_up_none.exr", ExrCompression.None, ExrLevelMode.RipMap, true, 13, 6 },
        new object[] { "mip_up_zip.exr", ExrCompression.Zip, ExrLevelMode.MipMap, true, 37, 9 },
        new object[] { "rip_up_dwaa.exr", ExrCompression.Dwaa, ExrLevelMode.RipMap, true, 20, 17 },
        new object[] { "mip_down_pxr24.exr", ExrCompression.Pxr24, ExrLevelMode.MipMap, false, 16, 16 },
    };

    [Theory]
    [MemberData(nameof(LevelCases))]
    public void GeneratedLevels(string name, ExrCompression comp, ExrLevelMode mode, bool roundUp, int w, int h)
    {
        // R,G half, Z float, id uint: every channel type in every level
        var f = Pattern(w, h, 3, w * 31 + h);
        var ids = new uint[w * h];
        for (int i = 0; i < ids.Length; i++) ids[i] = (uint)(i * 2654435761u);
        var chans = new[]
        {
            new ExrChannelData("R", f, ExrPixelType.Half, 0, 3), new ExrChannelData("G", f, ExrPixelType.Half, 1, 3),
            new ExrChannelData("Z", f, ExrPixelType.Float, 2, 3), new ExrChannelData("id", ids),
        };
        var ms = new MemoryStream();
        Exr.EncodeChannels(w, h, chans, ms, new ExrEncodeOptions
        {
            Compression = comp, TileWidth = 8, TileHeight = 4, LevelMode = mode, LevelRoundUp = roundUp, DataWindowX = -3, DataWindowY = 2,
            DisplayWindow = new ExrBox2i(0, 0, 39, 19),
        });
        var fresh = ms.ToArray();
        var committed = Check(name, fresh, comp);
        if (Writing) return;

        var hd = Exr.ReadHeaders(committed)[0];
        Assert.Equal((mode, roundUp, w, h), (hd.LevelMode, hd.LevelRoundUp, hd.Width, hd.Height));
        var names = new[] { "G", "R", "Z", "id" };
        int levels = 0;
        for (int ly = 0; ly < hd.LevelCountY; ly++)
            for (int lx = 0; lx < hd.LevelCountX; lx++)
            {
                if (!hd.HasLevel(lx, ly)) continue;
                var reference = TestUtil.LoadRef(Path.Combine(Dir, $"{name}.l{lx}_{ly}.ref"));       // read by libOpenEXR
                var o = new ExrDecodeOptions { LevelX = lx, LevelY = ly };
                Assert.Equal((reference.Width, reference.Height), hd.GetLevelSize(lx, ly));
                TestUtil.AssertEqual(reference, Exr.DecodeChannels(committed, names, o));
                TestUtil.AssertEqual(reference, Exr.DecodeChannels(fresh, names, o));
                levels++;
            }
        Assert.Equal(Directory.GetFiles(Dir, name + ".l*.ref").Length, levels);
        Assert.True(levels >= 4);
    }

    static ExrDeepImage MakeDeep(int w, int h, int seed, bool volumes)
    {
        var rnd = new Random(seed);
        var counts = new int[w * h];
        for (int i = 0; i < counts.Length; i++) { int r = rnd.Next(10); counts[i] = r < 2 ? 0 : r < 5 ? 1 : r - 3; }
        int total = counts.Sum();
        float[] a = new float[total], z = new float[total], zb = new float[total], r_ = new float[total], g = new float[total], b = new float[total];
        var id = new uint[total];
        int p = 0;
        foreach (int n in counts)
        {
            float depth = 1 + (float)rnd.NextDouble() * 3;
            for (int s = 0; s < n; s++, p++)
            {
                a[p] = rnd.Next(7) == 0 ? 1f : rnd.Next(5) == 0 ? 0f : (float)(Half)(float)rnd.NextDouble();
                float thick = volumes && rnd.Next(3) != 0 ? (float)rnd.NextDouble() * 2 : 0;
                z[p] = depth; zb[p] = depth + thick;
                float scale = a[p] > 0 ? a[p] : 0.3f;
                r_[p] = (float)(Half)(float)(rnd.NextDouble() * scale); g[p] = (float)(Half)(float)(rnd.NextDouble() * scale); b[p] = (float)(Half)(float)(rnd.NextDouble() * scale);
                id[p] = (uint)rnd.NextInt64(0, 1L << 32);
                depth += rnd.Next(4) == 0 ? -0.4f : thick * 0.5f + (float)rnd.NextDouble();
            }
        }
        var names = new List<string> { "R", "G", "B", "A", "Z", "id" };
        var data = new List<Array> { r_, g, b, a, z, id };
        if (volumes) { names.Add("ZBack"); data.Add(zb); }
        return new ExrDeepImage(w, h, counts, names, data);
    }

    internal static (int w, int h, int[] counts, Dictionary<string, Array> channels) LoadDump(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.AsSpan(0, 4).SequenceEqual("PXDP"u8));
        int w = BitConverter.ToInt32(bytes, 4), h = BitConverter.ToInt32(bytes, 8), nc = BitConverter.ToInt32(bytes, 12), total = BitConverter.ToInt32(bytes, 16);
        var counts = MemoryMarshal.Cast<byte, int>(bytes.AsSpan(20, 4 * w * h)).ToArray();
        int p = 20 + 4 * w * h;
        var ch = new Dictionary<string, Array>();
        for (int k = 0; k < nc; k++)
        {
            int e = Array.IndexOf(bytes, (byte)0, p);
            var raw = bytes.AsSpan(e + 2, total * 4);
            ch[System.Text.Encoding.ASCII.GetString(bytes, p, e - p)] = bytes[e + 1] == 0 ? MemoryMarshal.Cast<byte, uint>(raw).ToArray() : MemoryMarshal.Cast<byte, float>(raw).ToArray();
            p = e + 2 + total * 4;
        }
        return (w, h, counts, ch);
    }

    static void AssertDeepEquals(ExrDeepImage expected, (int w, int h, int[] counts, Dictionary<string, Array> channels) dump)
    {
        Assert.Equal((expected.Width, expected.Height), (dump.w, dump.h));
        Assert.Equal(expected.SampleCounts, dump.counts);
        Assert.Equal(expected.ChannelNames.Count, dump.channels.Count);
        for (int k = 0; k < expected.ChannelNames.Count; k++)
        {
            var theirs = dump.channels[expected.ChannelNames[k]];
            if (expected.ChannelData[k] is uint[] u) Assert.Equal(u, (uint[])theirs);
            else Assert.True(MemoryMarshal.AsBytes<float>((float[])expected.ChannelData[k]).SequenceEqual(MemoryMarshal.AsBytes<float>((float[])theirs)), expected.ChannelNames[k]);
        }
    }

    public static IEnumerable<object[]> DeepCases() => new[]
    {
        new object[] { "deep_scan_rle.exr", ExrCompression.Rle, 0, 0, 13, 9, false },
        new object[] { "deep_scan_none.exr", ExrCompression.None, 0, 0, 7, 5, true },
        new object[] { "deep_scan_zips.exr", ExrCompression.Zips, 0, 0, 17, 6, true },
        new object[] { "deep_tile_rle.exr", ExrCompression.Rle, 8, 5, 21, 13, true },
        new object[] { "deep_tile_none.exr", ExrCompression.None, 8, 5, 13, 9, false },     // raw tables of clipped edge tiles
        new object[] { "deep_tile_zips.exr", ExrCompression.Zips, 16, 16, 10, 12, false },   // one tile larger than the image
    };

    [Theory]
    [MemberData(nameof(DeepCases))]
    public void DeepOutput(string name, ExrCompression comp, int tileW, int tileH, int w, int h, bool volumes)
    {
        var deep = MakeDeep(w, h, w * 7 + h, volumes);
        var ms = new MemoryStream();
        Exr.EncodeDeep(deep, ms, new ExrEncodeOptions { Compression = comp, TileWidth = tileW, TileHeight = tileH, DataWindowX = -3, DataWindowY = 2, DisplayWindow = new ExrBox2i(0, 0, 30, 20) });
        var fresh = ms.ToArray();
        var committed = Check(name, fresh, comp);
        if (Writing) return;

        var hd = Exr.ReadHeaders(committed)[0];
        Assert.Equal((tileW > 0 ? ExrPartType.DeepTiled : ExrPartType.DeepScanLine, comp, new ExrBox2i(-3, 2, w - 4, h + 1)), (hd.Type, hd.Compression, hd.DataWindow));
        Assert.Equal(ExrPixelType.Float, hd.FindChannel("Z")!.PixelType);
        Assert.Equal(ExrPixelType.Half, hd.FindChannel("R")!.PixelType);
        Assert.Equal(deep.SampleCounts.Max(), hd.FindAttribute("maxSamplesPerPixel")!.AsInt());

        // what libOpenEXR read from the committed file is exactly what went in
        AssertDeepEquals(deep, LoadDump(Path.Combine(Dir, name + ".part0.deep")));
        // and our reader gets it back from both the committed and today's file
        foreach (var file in new[] { committed, fresh })
        {
            var back = Exr.DecodeDeep(file);
            Assert.Equal(deep.SampleCounts, back.SampleCounts);
            foreach (var n in deep.ChannelNames)
            {
                int k = deep.ChannelNames.ToList().IndexOf(n);
                if (deep.ChannelData[k] is uint[] u) Assert.Equal(u, back.GetUIntChannel(n));
                else Assert.Equal((float[])deep.ChannelData[k], back.GetFloatChannel(n));
                Assert.Equal(deep.ChannelTypes[k], back.ChannelTypes[back.ChannelNames.ToList().IndexOf(n)]);
            }
            TestUtil.AssertEqual(deep.Flatten(new[] { "R", "G", "B", "A" }), Exr.Decode(file));
        }
    }

    [Theory]
    [InlineData("deep_mip_rle.exr", ExrCompression.Rle, ExrLevelMode.MipMap, false, 13, 9)]
    [InlineData("deep_rip_up_none.exr", ExrCompression.None, ExrLevelMode.RipMap, true, 7, 5)]
    [InlineData("deep_mip_up_zips.exr", ExrCompression.Zips, ExrLevelMode.MipMap, true, 11, 6)]
    public void DeepLevelsOutput(string name, ExrCompression comp, ExrLevelMode mode, bool roundUp, int w, int h)
    {
        // every level has its own, caller supplied samples (deep levels cannot be generated)
        int Half(int n) => Math.Max(1, roundUp ? (n + 1) / 2 : n / 2);
        var sizes = new List<(int lx, int ly, int w, int h)>();
        if (mode == ExrLevelMode.MipMap)
            for (int l = 0, lw = w, lh = h; ; l++, lw = Half(lw), lh = Half(lh)) { sizes.Add((l, l, lw, lh)); if (lw == 1 && lh == 1) break; }
        else
            for (int ly = 0, lh = h; ; ly++, lh = Half(lh))
            {
                for (int lx = 0, lw = w; ; lx++, lw = Half(lw)) { sizes.Add((lx, ly, lw, lh)); if (lw == 1) break; }
                if (lh == 1) break;
            }
        var levels = sizes.Select((sz, i) => MakeDeep(sz.w, sz.h, 100 + i * 7 + w, true)).ToList();
        var ms = new MemoryStream();
        Exr.EncodeDeep(levels, ms, new ExrEncodeOptions { Compression = comp, TileWidth = 4, TileHeight = 3, LevelMode = mode, LevelRoundUp = roundUp, DataWindowX = 1, DataWindowY = -2 });
        var fresh = ms.ToArray();
        var committed = Check(name, fresh, comp);
        if (Writing) return;

        var hd = Exr.ReadHeaders(committed)[0];
        Assert.Equal((ExrPartType.DeepTiled, mode, roundUp, sizes.Count >= 4), (hd.Type, hd.LevelMode, hd.LevelRoundUp, true));
        for (int i = 0; i < sizes.Count; i++)
        {
            var (lx, ly, lw, lh) = sizes[i];
            Assert.Equal((lw, lh), hd.GetLevelSize(lx, ly));
            // libOpenEXR's reading of this level of the committed file is what went in
            AssertDeepEquals(levels[i], LoadDump(Path.Combine(Dir, i == 0 ? name + ".part0.deep" : $"{name}.part0.l{lx}_{ly}.deep")));
            foreach (var file in new[] { committed, fresh })
            {
                var back = Exr.DecodeDeep(file, null, new ExrDecodeOptions { LevelX = lx, LevelY = ly });
                Assert.Equal(levels[i].SampleCounts, back.SampleCounts);
                Assert.Equal(levels[i].GetFloatChannel("Z"), back.GetFloatChannel("Z"));
                Assert.Equal(levels[i].GetUIntChannel("id"), back.GetUIntChannel("id"));
            }
        }
        // the level list must match the level structure
        var bad = new MemoryStream();
        Assert.Throws<ArgumentException>(() => Exr.EncodeDeep(levels.Take(2).ToList(), bad, new ExrEncodeOptions { TileWidth = 4, TileHeight = 3, LevelMode = mode, LevelRoundUp = roundUp }));
        Assert.Throws<ArgumentException>(() => Exr.EncodeDeep(levels, bad, new ExrEncodeOptions { TileWidth = 4, TileHeight = 3, LevelMode = mode, LevelRoundUp = !roundUp }));
        Assert.Throws<ArgumentException>(() => Exr.EncodeDeep(levels, bad, new ExrEncodeOptions { LevelMode = mode, LevelRoundUp = roundUp }));
        Assert.Equal(0, bad.Length);
    }

    [Fact]
    public void MultiPartWithDeepParts()
    {
        const string name = "multi_deep.exr";
        var display = new ExrBox2i(0, 0, 30, 20);
        var deepA = MakeDeep(13, 9, 5, false);
        var deepB = MakeDeep(11, 12, 6, true);
        var flat = Pattern(9, 7, 3, 4);
        var parts = new[]
        {
            new ExrPart("flat", 9, 7, new[] { new ExrChannelData("R", flat, null, 0, 3), new ExrChannelData("G", flat, null, 1, 3), new ExrChannelData("B", flat, null, 2, 3) },
                new ExrEncodeOptions { Compression = ExrCompression.Piz, DisplayWindow = display }),
            new ExrPart("deepscan", deepA, new ExrEncodeOptions { Compression = ExrCompression.Rle, DisplayWindow = display, LineOrder = ExrLineOrder.DecreasingY }),
            new ExrPart("deeptile", deepB, new ExrEncodeOptions { Compression = ExrCompression.Rle, DisplayWindow = display, TileWidth = 4, TileHeight = 8, DataWindowX = 2, DataWindowY = 1 }),
        };
        var ms = new MemoryStream();
        Exr.EncodeParts(parts, ms);
        var fresh = ms.ToArray();
        var committed = Check(name, fresh, ExrCompression.Rle);
        if (Writing) return;

        var hd = Exr.ReadHeaders(committed);
        Assert.Equal(new[] { ExrPartType.ScanLine, ExrPartType.DeepScanLine, ExrPartType.DeepTiled }, hd.Select(x => x.Type));
        Assert.Equal(new[] { "flat", "deepscan", "deeptile" }, hd.Select(x => x.Name));
        TestUtil.AssertEqual(TestUtil.LoadRef(Path.Combine(Dir, name + ".part0.ref")), Exr.DecodeChannels(committed, new[] { "B", "G", "R" }, new ExrDecodeOptions { HalfAsFloat = false }));
        AssertDeepEquals(deepA, LoadDump(Path.Combine(Dir, name + ".part1.deep")));
        AssertDeepEquals(deepB, LoadDump(Path.Combine(Dir, name + ".part2.deep")));
        Assert.Equal(deepA.GetFloatChannel("Z"), Exr.DecodeDeep(committed, null, new ExrDecodeOptions { PartIndex = 1 }).GetFloatChannel("Z"));
        Assert.Equal(deepB.GetUIntChannel("id"), Exr.DecodeDeep(committed, null, new ExrDecodeOptions { PartIndex = 2 }).GetUIntChannel("id"));
        Assert.Equal(3, Exr.GetInfo(committed).FrameCount);
    }

    [Fact]
    public void DeepOutputChecksItsInput()
    {
        var ms = new MemoryStream();
        var deep = MakeDeep(4, 4, 1, false);
        Assert.Throws<ImageNotSupportedException>(() => Exr.EncodeDeep(deep, ms, new ExrEncodeOptions { Compression = ExrCompression.Piz }));
        // deep levels are never generated
        Assert.Throws<ArgumentException>(() => Exr.EncodeDeep(deep, ms, new ExrEncodeOptions { TileWidth = 4, TileHeight = 4, LevelMode = ExrLevelMode.MipMap }));
        Assert.Equal(0, ms.Length);
        Assert.Throws<ArgumentException>(() => new ExrDeepImage(2, 2, new[] { 1, 1, 1 }, new[] { "Z" }, new Array[] { new float[3] }));
        Assert.Throws<ArgumentException>(() => new ExrDeepImage(2, 2, new[] { 1, 1, 1, 1 }, new[] { "Z" }, new Array[] { new float[3] }));
        Assert.Throws<ArgumentException>(() => new ExrDeepImage(2, 2, new[] { 1, 1, 1, 1 }, new[] { "Z" }, new Array[] { new double[4] }));
        Assert.Throws<ArgumentException>(() => new ExrDeepImage(2, 2, new[] { 1, 1, 1, 1 }, new[] { "id" }, new Array[] { new uint[4] }, new[] { ExrPixelType.Half }));
        // defaults: Z / ZBack FLOAT, other float channels HALF, uint UINT; the default Zip means Zips for deep data
        var d = new ExrDeepImage(2, 1, new[] { 2, 0 }, new[] { "Z", "A", "id" }, new Array[] { new[] { 1f, 2f }, new[] { 0.5f, 1f }, new uint[] { 7, 8 } });
        Assert.Equal(new[] { ExrPixelType.Float, ExrPixelType.Half, ExrPixelType.UInt }, d.ChannelTypes);
        Exr.EncodeDeep(d, ms);
        var hd = Exr.ReadHeaders(ms.ToArray())[0];
        Assert.Equal((ExrCompression.Zips, ExrPartType.DeepScanLine), (hd.Compression, hd.Type));
        var back = Exr.DecodeDeep(ms.ToArray());
        Assert.Equal(new[] { 2, 0 }, back.SampleCounts);
        Assert.Equal(new[] { 1f, 2f }, back.GetFloatChannel("Z"));
        Assert.Equal(1f, Exr.Decode(ms.ToArray()).GetData<float>()[0]);      // alpha: 0.5 + 0.5 * 1; channels in file order A, Z, id
    }
}
