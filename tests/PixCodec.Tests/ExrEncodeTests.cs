using PixCodec;
using Xunit;

namespace PixCodec.Tests;

public class ExrEncodeTests
{
    public static IEnumerable<object[]> Compressions =>
        new[] { ExrCompression.None, ExrCompression.Rle, ExrCompression.Zips, ExrCompression.Zip, ExrCompression.Piz,
                ExrCompression.Pxr24, ExrCompression.B44, ExrCompression.B44A }.Select(c => new object[] { c });

    static float[] Pattern(int w, int h, int c, int seed)
    {
        var rnd = new Random(seed);
        var f = new float[w * h * c];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int k = 0; k < c; k++)
                {
                    double v = Math.Sin(x / (5.0 + k) + k) * Math.Cos(y / (3.0 + k)) * 2 + ((x / 6 + y / 5 + k) % 2) + 0.05 * rnd.NextDouble();
                    if (rnd.Next(40) == 0) v = rnd.Next(5) switch { 0 => 0, 1 => -0.0, 2 => 1e-6, 3 => 1234.5678, _ => -77.125 };
                    f[(y * w + x) * c + k] = (float)v;
                }
        return f;
    }

    /// <summary>kind: 0 float32 stored as HALF, 1 float32 stored as FLOAT, 2 Half, 3 uint.</summary>
    static RawImage MakeImage(int w, int h, ColorModel color, int channels, int kind, int seed = 1)
    {
        var f = Pattern(w, h, channels, seed);
        Array data = kind switch
        {
            2 => Array.ConvertAll(f, v => (Half)v),
            3 => Array.ConvertAll(f, v => (uint)(BitConverter.SingleToUInt32Bits(v) * 2654435761u)),
            _ => f,
        };
        return new RawImage(w, h, color, data, channels);
    }

    static byte[] Encode(RawImage img, ExrEncodeOptions opt)
    {
        var ms = new MemoryStream();
        Exr.Encode(img, new WriteOnlyStream(ms), opt);
        return ms.ToArray();
    }

    static int Ordered(Half h)
    {
        int b = BitConverter.HalfToUInt16Bits(h);
        return (b & 0x8000) != 0 ? ~b & 0xffff : b | 0x8000;
    }

    /// <summary>
    /// B44 stores, per 4x4 block, differences quantized by a common shift that is the smallest one for which
    /// neighbouring differences fit in 6 bits. In the ordered integer domain of the half bit patterns the error per
    /// sample is therefore at most (block range) / 31, and zero for blocks with a small range.
    /// </summary>
    static void AssertB44Bound(Half[] expected, Half[] actual, int w, int h, int c)
    {
        for (int k = 0; k < c; k++)
            for (int by = 0; by < h; by += 4)
                for (int bx = 0; bx < w; bx += 4)
                {
                    int min = int.MaxValue, max = int.MinValue;
                    for (int y = by; y < Math.Min(by + 4, h); y++)
                        for (int x = bx; x < Math.Min(bx + 4, w); x++)
                        {
                            int t = Ordered(expected[(y * w + x) * c + k]);
                            min = Math.Min(min, t); max = Math.Max(max, t);
                        }
                    int bound = (max - min) / 31 + 1;
                    for (int y = by; y < Math.Min(by + 4, h); y++)
                        for (int x = bx; x < Math.Min(bx + 4, w); x++)
                        {
                            int i = (y * w + x) * c + k;
                            int err = Math.Abs(Ordered(expected[i]) - Ordered(actual[i]));
                            Assert.True(err <= bound, $"B44 error {err} > {bound} at {x},{y},{k}");
                        }
                }
    }

    static readonly (ColorModel color, int channels, string[]? names)[] Models =
    {
        (ColorModel.Gray, 1, null), (ColorModel.GrayAlpha, 2, null), (ColorModel.Rgb, 3, null), (ColorModel.Rgba, 4, null),
        (ColorModel.Other, 5, new[] { "Z", "N.X", "N.Y", "N.Z", "weight" }),
    };

    [Theory]
    [MemberData(nameof(Compressions))]
    public void RoundTrip(ExrCompression comp)
    {
        foreach (var (w, h) in new[] { (1, 1), (37, 41), (3, 70), (64, 32), (130, 5) })
            foreach (var (color, channels, names) in Models)
                for (int kind = 0; kind < 4; kind++)
                {
                    var img = MakeImage(w, h, color, channels, kind, w + kind);
                    var opt = new ExrEncodeOptions { Compression = comp, ChannelNames = names };
                    if (kind == 1) opt.PixelType = ExrPixelType.Float;
                    var data = Encode(img, opt);
                    var hdr = Exr.ReadHeaders(data)[0];
                    Assert.Equal(comp, hdr.Compression);
                    Assert.All(hdr.Channels, c => Assert.Equal(kind == 3 ? ExrPixelType.UInt : kind == 1 ? ExrPixelType.Float : ExrPixelType.Half, c.PixelType));

                    // "Other" images come back in file (alphabetical) order unless the channels are named explicitly
                    var back = names == null
                        ? Exr.Decode(data, new ExrDecodeOptions { HalfAsFloat = kind != 2 })
                        : Exr.DecodeChannels(data, names, new ExrDecodeOptions { HalfAsFloat = kind != 2 });
                    Assert.Equal(color == ColorModel.Other ? ColorModel.Other : color, back.Color);
                    string what = $"{comp} {w}x{h} {color} kind {kind}";
                    bool b44 = comp is ExrCompression.B44 or ExrCompression.B44A;
                    switch (kind)
                    {
                        case 0:
                        {
                            var stored = Array.ConvertAll(img.GetData<float>(), v => (Half)v);
                            var got = Array.ConvertAll(back.GetData<float>(), v => (Half)v);
                            Assert.Equal(back.GetData<float>(), Array.ConvertAll(got, v => (float)v));   // values are halfs
                            if (b44) AssertB44Bound(stored, got, w, h, channels);
                            else Assert.True(stored.AsSpan().SequenceEqual(got), what);
                            break;
                        }
                        case 1:
                        {
                            var src = img.GetData<float>(); var got = back.GetData<float>();
                            if (comp == ExrCompression.Pxr24)
                                for (int i = 0; i < src.Length; i++)
                                {
                                    // PXR24 keeps 24 bits of a float: the bit pattern is rounded to a multiple of 256
                                    long a = BitConverter.SingleToUInt32Bits(src[i]) & 0x7fffffff, b = BitConverter.SingleToUInt32Bits(got[i]) & 0x7fffffff;
                                    // (unless the chunk was stored raw because compression did not shrink it)
                                    Assert.True(a == b || (Math.Abs(a - b) <= 128 && (b & 0xff) == 0), what);
                                    Assert.True(Math.Sign(src[i]) == Math.Sign(got[i]), what);
                                }
                            else Assert.True(System.Runtime.InteropServices.MemoryMarshal.AsBytes<float>(src).SequenceEqual(back.GetBytes()), what);
                            break;
                        }
                        case 2:
                            if (b44) AssertB44Bound(img.GetData<Half>(), back.GetData<Half>(), w, h, channels);
                            else Assert.True(img.GetBytes().SequenceEqual(back.GetBytes()), what);
                            break;
                        default:
                            Assert.True(img.GetBytes().SequenceEqual(back.GetBytes()), what);
                            break;
                    }
                }
    }

    [Theory]
    [MemberData(nameof(Compressions))]
    public void ReferenceDecoderAgrees(ExrCompression comp)
    {
        if (!TestUtil.PythonAvailable) return;
        const int w = 43, h = 70;
        // colour image through the simple API
        foreach (int kind in new[] { 0, 1, 2 })
        {
            var img = MakeImage(w, h, ColorModel.Rgba, 4, kind, 3);
            var data = Encode(img, new ExrEncodeOptions { Compression = comp, PixelType = kind == 1 ? ExrPixelType.Float : null });
            var oracle = TestUtil.OracleDecode(data, ".exr");
            if (oracle == null) return;
            TestUtil.AssertEqual(oracle, Exr.Decode(data));
        }

        // arbitrary channels of all three types, from separate planes and from interleaved data
        var f = Pattern(w, h, 4, 5);
        var halves = Array.ConvertAll(Pattern(w, h, 2, 6), v => (Half)v);
        var ids = Array.ConvertAll(Pattern(w, h, 1, 7), v => (uint)Math.Abs(v * 4000));     // < 2^24: exact as float32
        var chans = new[]
        {
            new ExrChannelData("R", f, null, 0, 4), new ExrChannelData("G", f, null, 1, 4), new ExrChannelData("B", f, null, 2, 4),
            new ExrChannelData("Z", f, ExrPixelType.Float, 3, 4),
            new ExrChannelData("diffuse.R", halves, null, 0, 2), new ExrChannelData("normal.X", halves, ExrPixelType.Float, 1, 2),
            new ExrChannelData("id", ids),
            new ExrChannelData("a channel name that is longer than thirty-one bytes", halves, null, 1, 2) { PLinear = true },
        };
        var ms = new MemoryStream();
        Exr.EncodeChannels(w, h, chans, ms, new ExrEncodeOptions { Compression = comp, DataWindowX = -5, DataWindowY = 100 });
        var bytes = ms.ToArray();
        var hdr = Exr.ReadHeaders(bytes)[0];
        Assert.Equal(new ExrBox2i(-5, 100, w - 6, h + 99), hdr.DataWindow);
        var all = TestUtil.OracleDecode(bytes, ".all.exr");
        if (all == null) return;
        TestUtil.AssertEqual(all, Exr.DecodeChannels(bytes, hdr.Channels.Select(c => c.Name).ToArray()));
        // lossless types really are lossless
        if (comp != ExrCompression.Pxr24)
            Assert.Equal(Enumerable.Range(0, w * h).Select(i => f[i * 4 + 3]), Exr.DecodeChannels(bytes, new[] { "Z" }).GetData<float>());
        Assert.Equal(ids, Exr.DecodeChannels(bytes, new[] { "id" }).GetData<uint>());

        // UINT only, full 32 bit range
        var u = MakeImage(w, h, ColorModel.Other, 2, 3, 9);
        var ub = Encode(u, new ExrEncodeOptions { Compression = comp, ChannelNames = new[] { "idA", "idB" }, LineOrder = ExrLineOrder.DecreasingY });
        Assert.Equal(ExrLineOrder.DecreasingY, Exr.ReadHeaders(ub)[0].LineOrder);
        var uo = TestUtil.OracleDecode(ub, ".all.exr");
        Assert.Equal(SampleType.UInt32, uo!.SampleType);
        TestUtil.AssertEqual(uo, Exr.Decode(ub));
        TestUtil.AssertEqual(u, Exr.Decode(ub));
    }

    [Fact]
    public void GrayModelsAgreeWithReference()
    {
        foreach (var color in new[] { ColorModel.Gray, ColorModel.GrayAlpha, ColorModel.Rgb })
        {
            var img = MakeImage(21, 19, color, RawImage.ChannelCount(color), 0, 4);
            var data = Encode(img, new ExrEncodeOptions());
            Assert.Equal(ExrCompression.Zip, Exr.ReadHeaders(data)[0].Compression);
            var oracle = TestUtil.OracleDecode(data, ".exr");
            if (oracle == null) return;
            var back = Exr.Decode(data);
            Assert.Equal(color, back.Color);
            TestUtil.AssertEqual(oracle, back);
            Assert.Equal(Array.ConvertAll(img.GetData<float>(), v => (float)(Half)v), back.GetData<float>());
        }
    }

    [Theory]
    [MemberData(nameof(Compressions))]
    public void ParallelMatchesSequential(ExrCompression comp)
    {
        var img = MakeImage(700, 300, ColorModel.Rgba, 4, 0, 8);
        var a = Encode(img, new ExrEncodeOptions { Compression = comp });
        var b = Encode(img, new ExrEncodeOptions { Compression = comp, Parallel = false });
        Assert.Equal(a, b);
        var da = Exr.Decode(a);
        var db = Exr.Decode(a, new ExrDecodeOptions { Parallel = false });
        TestUtil.AssertEqual(da, db);
        if (comp is not (ExrCompression.B44 or ExrCompression.B44A))
            Assert.Equal(Array.ConvertAll(img.GetData<float>(), v => (float)(Half)v), da.GetData<float>());
    }

    [Theory]
    [MemberData(nameof(Compressions))]
    public void ManyChannels(ExrCompression comp)
    {
        // AOV heavy files: hundreds of channels, and a pathological count that must not be slow or deep on the stack
        foreach (var (count, w, h) in new[] { (300, 9, 40), (40000, 2, 1) })
        {
            var rnd = new Random(count);
            var planes = new Half[count][];
            var chans = new ExrChannelData[count];
            for (int i = 0; i < count; i++)
            {
                planes[i] = new Half[w * h];
                for (int k = 0; k < planes[i].Length; k++) planes[i][k] = (Half)(float)(rnd.Next(64) / 8.0);   // exact in B44 too
                chans[i] = new ExrChannelData($"aov{i:D5}.V", planes[i]);
            }
            var ms = new MemoryStream();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Exr.EncodeChannels(w, h, chans, ms, new ExrEncodeOptions { Compression = comp });
            var data = ms.ToArray();
            var names = chans.Select(c => c.Name).ToArray();
            var back = Exr.DecodeChannels(data, names, new ExrDecodeOptions { HalfAsFloat = false });
            Assert.True(sw.Elapsed.TotalSeconds < 20);
            Assert.Equal((count, ColorModel.Other), (back.Channels, back.Color));
            var b = back.GetData<Half>();
            if (comp is ExrCompression.B44 or ExrCompression.B44A) continue;   // lossy, covered elsewhere
            for (int i = 0; i < count; i += 7)
                for (int k = 0; k < w * h; k++) Assert.Equal(planes[i][k], b[k * count + i]);
        }
    }

    [Fact]
    public void FloatToHalfRounding()
    {
        var rnd = new Random(5);
        var f = new float[20000];
        for (int i = 0; i < f.Length; i++)
            f[i] = i switch
            {
                < 4000 => BitConverter.UInt32BitsToSingle((uint)rnd.NextInt64(0, 1L << 32)),        // any bit pattern
                < 8000 => (float)((rnd.NextDouble() - 0.5) * 2e-4),                                // around the subnormal range
                < 12000 => (float)(Half)(float)((rnd.NextDouble() - 0.5) * 100) * (1 + (rnd.Next(3) - 1) * 0.00048828125f),   // near ties
                _ => (float)((rnd.NextDouble() - 0.5) * 131000),
            };
        f[0] = 65504f; f[1] = 65519.99f; f[2] = 65520f; f[3] = 1e30f; f[4] = -1e30f; f[5] = float.PositiveInfinity;
        f[6] = float.NegativeInfinity; f[7] = float.NaN; f[8] = float.Epsilon; f[9] = 2.9802322e-8f; f[10] = 2.9802326e-8f; f[11] = -0f;
        var data = Encode(new RawImage(f.Length, 1, ColorModel.Gray, f), new ExrEncodeOptions { Compression = ExrCompression.None });
        var back = Exr.Decode(data, new ExrDecodeOptions { HalfAsFloat = false }).GetData<Half>();
        for (int i = 0; i < f.Length; i++)
        {
            Half expected = (Half)f[i];
            // documented difference to IEEE: finite values beyond the half range clamp to +-65504 instead of overflowing
            if (float.IsFinite(f[i]) && Half.IsInfinity(expected)) expected = f[i] > 0 ? Half.MaxValue : Half.MinValue;
            if (float.IsNaN(f[i])) Assert.True(Half.IsNaN(back[i]));
            else Assert.True(BitConverter.HalfToUInt16Bits(expected) == BitConverter.HalfToUInt16Bits(back[i]), $"{f[i]:R}: {expected} vs {back[i]}");
        }
    }

    [Fact]
    public void AttributesAndWindows()
    {
        var img = MakeImage(9, 7, ColorModel.Rgb, 3, 2);
        var opt = new ExrEncodeOptions
        {
            Compression = ExrCompression.Piz, DataWindowX = 3, DataWindowY = -2, DisplayWindow = new ExrBox2i(0, 0, 19, 9),
        };
        opt.Attributes.Add(ExrAttribute.String("comment", "héllo"));
        opt.Attributes.Add(ExrAttribute.Float("pixelAspectRatio", 1.5f));
        opt.Attributes.Add(ExrAttribute.Chromaticities("chromaticities", new ExrChromaticities(0.64f, 0.33f, 0.3f, 0.6f, 0.15f, 0.06f, 0.3127f, 0.329f)));
        opt.Attributes.Add(ExrAttribute.Int("myInt", -42));
        opt.Attributes.Add(ExrAttribute.Double("myDouble", Math.PI));
        opt.Attributes.Add(ExrAttribute.V2i("myV2i", 1, 2));
        opt.Attributes.Add(ExrAttribute.V3f("myV3f", 1, 2, 3));
        opt.Attributes.Add(ExrAttribute.M44f("worldToCamera", Enumerable.Range(0, 16).Select(i => (float)i).ToArray()));
        opt.Attributes.Add(new ExrAttribute("blob", "myCustomType", new byte[] { 1, 2, 3, 4, 5 }));
        var data = Encode(img, opt);
        var h = Exr.ReadHeaders(data)[0];
        Assert.Equal(new ExrBox2i(3, -2, 11, 4), h.DataWindow);
        Assert.Equal(new ExrBox2i(0, 0, 19, 9), h.DisplayWindow);
        Assert.Equal(1.5f, h.PixelAspectRatio);
        Assert.Equal(0.3127f, h.Chromaticities!.Value.WhiteX);
        Assert.Equal("héllo", h.FindAttribute("comment")!.AsString());
        Assert.Equal(-42, h.FindAttribute("myInt")!.AsInt());
        Assert.Equal(Math.PI, h.FindAttribute("myDouble")!.AsDouble());
        Assert.Equal((1, 2), h.FindAttribute("myV2i")!.AsV2i());
        Assert.Equal((1f, 2f, 3f), h.FindAttribute("myV3f")!.AsV3f());
        Assert.Equal(15f, h.FindAttribute("worldToCamera")!.AsFloats(16)[15]);
        Assert.Equal("myCustomType", h.FindAttribute("blob")!.TypeName);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, h.FindAttribute("blob")!.Value);
        Assert.Throws<ImageFormatException>(() => h.FindAttribute("myInt")!.AsDouble());
        TestUtil.AssertEqual(img, Exr.Decode(data, new ExrDecodeOptions { HalfAsFloat = false }));
        // the reference library accepts the file including the custom attributes
        var oracle = TestUtil.OracleDecode(data, ".exr");
        if (oracle != null) TestUtil.AssertEqual(oracle, Exr.Decode(data));

        opt.Attributes.Add(ExrAttribute.Int("chunkCount", 1));
        Assert.Throws<ArgumentException>(() => Encode(img, opt));
    }

    [Fact]
    public void RejectsWhatItCannotStore()
    {
        var ms = new MemoryStream();
        Assert.Throws<ImageNotSupportedException>(() => Exr.Encode(RawImage.Create(4, 4, ColorModel.Rgb, SampleType.UInt8), ms));
        Assert.Throws<ImageNotSupportedException>(() => Exr.Encode(RawImage.Create(4, 4, ColorModel.Rgb, SampleType.UInt16), ms));
        Assert.Throws<ImageNotSupportedException>(() => Exr.Encode(RawImage.Create(4, 4, ColorModel.Gray, SampleType.Float64), ms));
        var f = RawImage.Create(4, 4, ColorModel.Rgb, SampleType.Float32);
        Assert.Throws<ImageNotSupportedException>(() => Exr.Encode(f, ms, new ExrEncodeOptions { PixelType = ExrPixelType.UInt }));
        Assert.Throws<ImageNotSupportedException>(() => Exr.Encode(RawImage.Create(4, 4, ColorModel.Gray, SampleType.UInt32), ms, new ExrEncodeOptions { PixelType = ExrPixelType.Half }));
        Assert.Throws<ImageNotSupportedException>(() => Exr.Encode(f, ms, new ExrEncodeOptions { Compression = (ExrCompression)10 }));
        var other = RawImage.Create(4, 4, ColorModel.Other, SampleType.Float32, 5);
        Assert.Throws<ArgumentException>(() => Exr.Encode(other, ms));
        Assert.Throws<ArgumentException>(() => Exr.Encode(other, ms, new ExrEncodeOptions { ChannelNames = new[] { "a", "b" } }));
        Assert.Throws<ArgumentException>(() => Exr.Encode(other, ms, new ExrEncodeOptions { ChannelNames = new[] { "a", "b", "c", "d", "a" } }));
        Assert.Throws<ArgumentException>(() => Exr.EncodeChannels(4, 4, new[] { new ExrChannelData("Z", new float[15]) }, ms));
        Assert.Equal(0, ms.Length);
    }

    /// <summary>A stream that can only be written sequentially.</summary>
    sealed class WriteOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }
}
