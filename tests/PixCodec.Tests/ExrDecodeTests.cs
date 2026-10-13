using System.Buffers.Binary;
using PixCodec;
using Xunit;

namespace PixCodec.Tests;

public class ExrDecodeTests
{
    public static IEnumerable<object[]> Fixtures => TestUtil.Files("exr", ".exr");
    public static IEnumerable<object[]> Dwa => TestUtil.Files(Path.Combine("exr", "dwa"), ".exr");
    public static IEnumerable<object[]> Others =>
        new[] { "yc", "levels", "deep", "encref" }.SelectMany(d => TestUtil.Files(Path.Combine("exr", d), ".exr"));

    internal static RawImage HalfToFloat(RawImage img)
    {
        var h = img.GetData<Half>();
        var f = new float[h.Length];
        for (int i = 0; i < h.Length; i++) f[i] = (float)h[i];
        return new RawImage(img.Width, img.Height, img.Color, f, img.Channels);
    }

    static string[] AllNames(byte[] data, int part = 0) => Exr.ReadHeaders(data)[part].Channels.Select(c => c.Name).ToArray();

    /// <summary>Compares against a sidecar; float16 sidecars are checked both as Float16 and converted to Float32.</summary>
    static void Check(string refPath, Func<ExrDecodeOptions, RawImage> decode, int part = 0)
    {
        var expected = TestUtil.LoadRef(refPath);
        foreach (bool parallel in new[] { true, false })
        {
            if (expected.SampleType == SampleType.Float16)
            {
                TestUtil.AssertEqual(expected, decode(new ExrDecodeOptions { HalfAsFloat = false, Parallel = parallel, PartIndex = part }));
                TestUtil.AssertEqual(HalfToFloat(expected), decode(new ExrDecodeOptions { Parallel = parallel, PartIndex = part }));
            }
            else
            {
                TestUtil.AssertEqual(expected, decode(new ExrDecodeOptions { Parallel = parallel, PartIndex = part }));
                TestUtil.AssertEqual(expected, decode(new ExrDecodeOptions { HalfAsFloat = false, Parallel = parallel, PartIndex = part }));
            }
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    [MemberData(nameof(Dwa))]
    public void DecodesLikeReference(string file)
    {
        var path = TestUtil.Fixture(file);
        var data = File.ReadAllBytes(path);
        Assert.True(Exr.IsMatch(data));
        Check(path + ".ref", o => Exr.Decode(data, o));

        var img = Exr.Decode(data);
        var info = Exr.GetInfo(data);
        Assert.Equal((ImageFormat.Exr, img.Width, img.Height, img.Channels, img.SampleType, img.Color),
            (info.Format, info.Width, info.Height, info.Channels, info.SampleType, info.Color));

        if (File.Exists(path + ".all.ref"))
            Check(path + ".all.ref", o => Exr.DecodeChannels(data, AllNames(data), o));
        var headers = Exr.ReadHeaders(data);
        for (int p = 1; p < headers.Length; p++)
            Check($"{path}.part{p}.ref", o => Exr.Decode(data, o), p);
    }

    [Fact]
    public void ColorModels()
    {
        ColorModel Model(string name) => Exr.Decode(File.ReadAllBytes(TestUtil.Fixture("exr/" + name))).Color;
        Assert.Equal(ColorModel.Rgb, Model("rgb_half_zip.exr"));
        Assert.Equal(ColorModel.Rgba, Model("mixed_piz.exr"));
        Assert.Equal(ColorModel.Gray, Model("y_half_zip.exr"));
        Assert.Equal(ColorModel.GrayAlpha, Model("ya_half_piz.exr"));
        Assert.Equal(ColorModel.Gray, Model("z_float_zips.exr"));
        Assert.Equal(ColorModel.Gray, Model("uint_zip.exr"));
        Assert.Equal(ColorModel.Other, Model("aov_only_zip.exr"));
        Assert.Equal(ColorModel.Rgb, Model("layer_only_piz.exr"));   // no root colour: first layer with R,G,B
        Assert.Equal(SampleType.UInt32, Exr.Decode(File.ReadAllBytes(TestUtil.Fixture("exr/uint_zip.exr"))).SampleType);
    }

    [Fact]
    public void LayersAndExplicitChannels()
    {
        var data = File.ReadAllBytes(TestUtil.Fixture("exr/mixed_zip.exr"));
        var h = Exr.ReadHeaders(data)[0];
        Assert.Equal(new[] { "A", "B", "G", "R", "Z", "diffuse.B", "diffuse.G", "diffuse.R", "id" }, h.Channels.Select(c => c.Name));
        Assert.Equal(new[] { "", "diffuse" }, h.Layers);
        Assert.Equal(ExrPixelType.Float, h.FindChannel("Z")!.PixelType);
        Assert.Equal(ExrPixelType.UInt, h.FindChannel("id")!.PixelType);
        Assert.Equal(("diffuse", "R"), (h.FindChannel("diffuse.R")!.Layer, h.FindChannel("diffuse.R")!.BaseName));
        Assert.Equal("mixed types", h.FindAttribute("comment")!.AsString());

        var all = TestUtil.LoadRef(TestUtil.Fixture("exr/mixed_zip.exr.all.ref"));   // float32, 9 channels in file order
        var allData = all.GetData<float>();
        RawImage Pick(params int[] ch)
        {
            var f = new float[all.Width * all.Height * ch.Length];
            for (int i = 0; i < all.Width * all.Height; i++)
                for (int k = 0; k < ch.Length; k++) f[i * ch.Length + k] = allData[i * 9 + ch[k]];
            return new RawImage(all.Width, all.Height, ColorModel.Other, f, ch.Length);
        }

        var diffuse = Exr.Decode(data, new ExrDecodeOptions { Layer = "diffuse" });
        Assert.Equal(ColorModel.Rgb, diffuse.Color);
        TestUtil.AssertEqual(Pick(7, 6, 5), diffuse);
        Assert.Equal(ColorModel.Rgba, Exr.Decode(data, new ExrDecodeOptions { Layer = "" }).Color);
        Assert.Throws<ArgumentException>(() => Exr.Decode(data, new ExrDecodeOptions { Layer = "nope" }));

        var z = Exr.DecodeChannels(data, new[] { "Z" });
        Assert.Equal((ColorModel.Gray, SampleType.Float32), (z.Color, z.SampleType));
        TestUtil.AssertEqual(Pick(4), z);
        var id = Exr.DecodeChannels(data, new[] { "id" });
        Assert.Equal(SampleType.UInt32, id.SampleType);
        var idf = Pick(8).GetData<float>();
        Assert.Equal(idf, id.GetData<uint>().Select(v => (float)v).ToArray());
        var odd = Exr.DecodeChannels(data, new[] { "id", "diffuse.G", "Z", "A" });
        Assert.Equal((ColorModel.Other, 4, SampleType.Float32), (odd.Color, odd.Channels, odd.SampleType));
        TestUtil.AssertEqual(Pick(8, 6, 4, 0), odd);
        Assert.Equal(ColorModel.Rgb, Exr.DecodeChannels(data, new[] { "diffuse.R", "diffuse.G", "diffuse.B" }).Color);
        Assert.Equal(SampleType.Float16, Exr.DecodeChannels(data, new[] { "A", "R" }, new ExrDecodeOptions { HalfAsFloat = false }).SampleType);
        Assert.Throws<ArgumentException>(() => Exr.DecodeChannels(data, new[] { "Z", "missing" }));
        Assert.Throws<ArgumentException>(() => Exr.DecodeChannels(data, new[] { "Z", "Z" }));
    }

    [Fact]
    public void Headers()
    {
        var h = Exr.ReadHeaders(File.ReadAllBytes(TestUtil.Fixture("exr/window_zip.exr")))[0];
        Assert.Equal(new ExrBox2i(-7, -40, 15, -6), h.DataWindow);
        Assert.Equal(new ExrBox2i(0, 0, 63, 47), h.DisplayWindow);
        Assert.Equal((23, 35), (h.Width, h.Height));
        Assert.Equal(2f, h.PixelAspectRatio);
        Assert.Equal(ExrCompression.Zip, h.Compression);
        Assert.Equal(ExrLineOrder.IncreasingY, h.LineOrder);
        Assert.Equal(ExrPartType.ScanLine, h.Type);
        Assert.Equal(1f, h.ScreenWindowWidth);
        Assert.Equal("box2i", h.FindAttribute("dataWindow")!.TypeName);
        Assert.Equal(16, h.FindAttribute("dataWindow")!.Value.Length);
        Assert.Null(h.FindAttribute("nope"));

        h = Exr.ReadHeaders(File.ReadAllBytes(TestUtil.Fixture("exr/decreasing_piz.exr")))[0];
        Assert.Equal((ExrLineOrder.DecreasingY, ExrCompression.Piz), (h.LineOrder, h.Compression));

        h = Exr.ReadHeaders(File.ReadAllBytes(TestUtil.Fixture("exr/tiled_ripmap_up.exr")))[0];
        Assert.Equal((ExrPartType.Tiled, 8, 8, ExrLevelMode.RipMap, true), (h.Type, h.TileWidth, h.TileHeight, h.LevelMode, h.LevelRoundUp));
        Assert.True(h.IsTiled);

        var parts = Exr.ReadHeaders(File.ReadAllBytes(TestUtil.Fixture("exr/multipart.exr")));
        Assert.Equal(new[] { "beauty", "depth", "third" }, parts.Select(p => p.Name));
        Assert.Equal(new[] { ExrPartType.ScanLine, ExrPartType.Tiled, ExrPartType.ScanLine }, parts.Select(p => p.Type));
        Assert.Equal(new[] { ExrCompression.Zip, ExrCompression.Piz, ExrCompression.B44 }, parts.Select(p => p.Compression));
        Assert.Equal(new ExrBox2i(3, 4, 14, 43), parts[2].DataWindow);
        Assert.Throws<ArgumentException>(() => Exr.Decode(File.ReadAllBytes(TestUtil.Fixture("exr/multipart.exr")), new ExrDecodeOptions { PartIndex = 3 }));

        h = Exr.ReadHeaders(File.ReadAllBytes(TestUtil.Fixture("exr/long_names_zip.exr")))[0];
        Assert.Contains(h.Channels, c => c.Name.Length > 31 && c.BaseName == "R");

        h = Exr.ReadHeaders(File.ReadAllBytes(TestUtil.Fixture("exr/yc/py_yc_zip.exr")))[0];
        Assert.Equal((2, 2), (h.FindChannel("RY")!.XSampling, h.FindChannel("RY")!.YSampling));
        h = Exr.ReadHeaders(File.ReadAllBytes(TestUtil.Fixture("exr/b44_plinear.exr")))[0];
        Assert.True(h.FindChannel("R")!.PLinear);
    }

    [Fact]
    public void HalfConversionTable()
    {
        // every half value through the decoder (vector and scalar paths: lengths that are not multiples of 8)
        foreach (int width in new[] { 65536, 4099, 7 })
        {
            var halves = new Half[width];
            for (int i = 0; i < width; i++) halves[i] = BitConverter.UInt16BitsToHalf((ushort)(i * 0x10001 >> (width == 65536 ? 0 : 3)));
            var ms = new MemoryStream();
            Exr.Encode(new RawImage(width, 1, ColorModel.Gray, halves), ms, new ExrEncodeOptions { Compression = ExrCompression.None });
            var back = Exr.Decode(ms.ToArray()).GetData<float>();
            for (int i = 0; i < width; i++)
            {
                // .NET quiets signalling NaNs when widening, the decoder keeps the payload bits as they are
                uint expected = BitConverter.SingleToUInt32Bits((float)halves[i]), actual = BitConverter.SingleToUInt32Bits(back[i]);
                if (Half.IsNaN(halves[i])) expected &= ~0x00400000u | (actual & 0x00400000u);
                Assert.Equal(expected, actual);
            }
        }
    }

    /// <summary>Position and length (in bytes) of the offset tables of a single part file.</summary>
    static (int pos, int chunkStart) OffsetTable(byte[] d)
    {
        int p = 8;
        while (d[p] != 0)
        {
            p = Array.IndexOf(d, (byte)0, p) + 1;
            p = Array.IndexOf(d, (byte)0, p) + 1;
            p += 4 + BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p));
        }
        p++;
        // the table ends where the first chunk (in file order) starts
        long first = long.MaxValue;
        for (int q = p; q < first; q += 8)
        {
            long o = BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(q));
            if (o > 0) first = Math.Min(first, o);   // unwritten mip levels have zero entries
        }
        return (p, (int)first);
    }

    [Theory]
    [InlineData("rgb_half_zip.exr")]
    [InlineData("decreasing_piz.exr")]
    [InlineData("tiled_piz.exr")]
    [InlineData("tiled_random_y.exr")]
    [InlineData("mixed_rle.exr")]
    [InlineData("tiled_mipmap_down.exr")]
    public void MissingOffsetTableIsRebuilt(string name)
    {
        var data = File.ReadAllBytes(TestUtil.Fixture("exr/" + name));
        var expected = Exr.Decode(data);
        var (pos, first) = OffsetTable(data);
        var zeroed = (byte[])data.Clone();
        Array.Clear(zeroed, pos, first - pos);
        TestUtil.AssertEqual(expected, Exr.Decode(zeroed));
        // only some entries damaged
        var partial = (byte[])data.Clone();
        Array.Clear(partial, pos + 8, 8);
        TestUtil.AssertEqual(expected, Exr.Decode(partial));
        // a table that is intact but a file that lost its last chunk: clean error
        Assert.Throws<ImageFormatException>(() => Exr.Decode(zeroed.AsSpan(0, zeroed.Length - 3)));
    }

    [Theory]
    [InlineData(58)]
    [InlineData(57)]
    [InlineData(48)]
    [InlineData(33)]
    [InlineData(20)]
    public void PizHuffmanLongCodes(int M)
    {
        // A hand made PIZ chunk whose Huffman code is maximally skewed: symbol k has the k+1 bit code 0..01, the last
        // data symbol and the run length symbol have M bit codes. Real encoders never produce this, readers must cope.
        const int width = 256;
        var ms = new MemoryStream();
        Exr.Encode(new RawImage(width, 1, ColorModel.Gray, new Half[width]), ms, new ExrEncodeOptions { Compression = ExrCompression.Piz });
        var file = ms.ToArray();
        var (_, chunk) = OffsetTable(file);

        var bits = new List<bool>();
        void Put(ulong value, int count) { for (int i = count - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0); }
        byte[] Pack()
        {
            var b = new byte[(bits.Count + 7) / 8];
            for (int i = 0; i < bits.Count; i++) if (bits[i]) b[i / 8] |= (byte)(0x80 >> (i % 8));
            return b;
        }
        int Len(int sym) => sym <= M - 4 ? sym + 1 : sym == M - 3 ? M - 2 : sym == M - 2 ? M - 1 : M;
        for (int sym = 0; sym <= M; sym++) Put((ulong)Len(sym), 6);
        var table = Pack();
        bits.Clear();
        var expected = new List<ushort>();
        void Sym(int sym) { sym = sym * (M - 1) / 57; Put(sym == M - 1 ? 0u : 1u, Len(sym)); expected.Add((ushort)sym); }
        void Run(int count) { Put(1, M); Put((ulong)count, 8); for (int i = 0; i < count; i++) expected.Add(expected[^1]); }
        Sym(57); Run(100); Sym(56); Sym(55); Sym(54); Sym(50); Run(3); Sym(45); Sym(13); Sym(14); Sym(15); Sym(16); Sym(12);
        Sym(0); Sym(57); Sym(57); Sym(1); Run(255 - 131); Sym(30); Sym(2); Sym(56); Sym(3); Sym(57); Sym(40); Sym(0);
        while (expected.Count < width) Sym(expected.Count % 9);
        Assert.Equal(width, expected.Count);
        int nBits = bits.Count;
        var huf = Pack();

        var w = new BinaryWriter(new MemoryStream());
        w.Write((ushort)0); w.Write((ushort)25);                       // bitmap bytes 0..25: values 1..200 are present
        w.Write((byte)0xfe); w.Write(Enumerable.Repeat((byte)0xff, 24).ToArray()); w.Write((byte)1);
        w.Write(20 + table.Length + huf.Length);
        w.Write(0); w.Write(M); w.Write(table.Length); w.Write(nBits); w.Write(0);
        w.Write(table); w.Write(huf);
        var data = ((MemoryStream)w.BaseStream).ToArray();
        Assert.True(data.Length < width * 2);

        var crafted = file.AsSpan(0, chunk + 4).ToArray().Concat(BitConverter.GetBytes(data.Length)).Concat(data).ToArray();
        var img = Exr.Decode(crafted, new ExrDecodeOptions { HalfAsFloat = false });
        Assert.Equal(expected, img.GetData<Half>().Select(BitConverter.HalfToUInt16Bits));
        // (OpenEXR 3.5 itself rejects tables with 58 bit codes, the longest the format allows; 57 bits it reads)
        var oracle = M < 58 ? TestUtil.OracleDecode(crafted, ".exr") : null;
        if (oracle != null) TestUtil.AssertEqual(oracle, Exr.Decode(crafted));

        // the same stream cut short or with a wrong bit count must fail cleanly
        for (int cut = 1; cut < 40; cut += 3)
        {
            var d2 = data.AsSpan(0, data.Length - cut).ToArray();
            BitConverter.TryWriteBytes(d2.AsSpan(30), d2.Length - 34);
            var bad = file.AsSpan(0, chunk + 4).ToArray().Concat(BitConverter.GetBytes(d2.Length)).Concat(d2).ToArray();
            Assert.Throws<ImageFormatException>(() => Exr.Decode(bad));
        }
    }

    [Fact]
    public void LyingHeadersAreRejected()
    {
        var data = File.ReadAllBytes(TestUtil.Fixture("exr/rgb_half_zip.exr"));
        int at = data.AsSpan().IndexOf("dataWindow\0box2i\0"u8) + 17 + 4;
        foreach (var (maxX, maxY) in new[] { (0x3fffffff, 36), (34, 0x3ffffffe), (100_000, 100_000), (2_000_000, 36), (34, 5000), (20000, 20000) })
        {
            var d = (byte[])data.Clone();
            BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(at + 8), maxX);
            BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(at + 12), maxY);
            var ex = Record.Exception(() => Exr.Decode(d));
            Assert.True(ex is ImageFormatException or ImageNotSupportedException, $"{maxX},{maxY}: {ex}");
        }
        var bad = (byte[])data.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(bad.AsSpan(at + 8), -5);     // max < min
        Assert.Throws<ImageFormatException>(() => Exr.Decode(bad));
        Assert.Throws<ImageFormatException>(() => Exr.Decode(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }));
        Assert.Throws<ImageFormatException>(() => Exr.Decode(data.AsSpan(0, 8)));
        Assert.False(Exr.IsMatch(new byte[] { 0x76, 0x2f, 0x31 }));
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    [MemberData(nameof(Dwa))]
    [MemberData(nameof(Others))]
    public void CorruptInputFailsCleanly(string file)
    {
        var data = File.ReadAllBytes(TestUtil.Fixture(file));
        var rnd = new Random(file.Length * 7919 + data.Length);
        void Try(ReadOnlySpan<byte> d)
        {
            try
            {
                var names = Exr.ReadHeaders(d)[0].Channels.Select(c => c.Name).ToArray();
                Exr.GetInfo(d);
                var headers = Exr.ReadHeaders(d);
                for (int p = 0; p < headers.Length; p++)
                {
                    try { Exr.Decode(d, new ExrDecodeOptions { PartIndex = p }); } catch (ImageNotSupportedException) { }
                    if (headers[p].IsDeep) Exr.DecodeDeep(d, null, new ExrDecodeOptions { PartIndex = p });
                    else Exr.DecodeChannels(d, headers[p].Channels.Select(c => c.Name).ToArray(), new ExrDecodeOptions { Parallel = false, PartIndex = p });
                    if (headers[p].LevelCountX > 1)
                        Exr.Decode(d, new ExrDecodeOptions { PartIndex = p, Level = 1 });
                }
            }
            catch (ImageFormatException) { }
            catch (ImageNotSupportedException) { }
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 48; i++)
        {
            int len = i < 16 ? i * 3 : rnd.Next(data.Length);
            Try(data.AsSpan(0, Math.Min(len, data.Length)));
        }
        Try(data.AsSpan(0, data.Length - 1));
        for (int i = 0; i < 150; i++)
        {
            var d = (byte[])data.Clone();
            int flips = 1 + rnd.Next(3);
            for (int k = 0; k < flips; k++)
            {
                // half of the damage goes into the header / tables, where it is most interesting
                int pos = rnd.Next(2) == 0 ? rnd.Next(Math.Min(d.Length, 700)) : rnd.Next(d.Length);
                d[pos] = rnd.Next(3) == 0 ? (byte)rnd.Next(256) : (byte)(d[pos] ^ (1 << rnd.Next(8)));
            }
            Try(d);
        }
        Assert.True(sw.Elapsed.TotalSeconds < 20, $"took {sw.Elapsed.TotalSeconds:F1}s");
    }
}
