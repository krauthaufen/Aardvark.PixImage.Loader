using PixCodec;
using PixCodec.JpegImpl;
using Xunit;

namespace PixCodec.Tests;

/// <summary>Rate-distortion optimized quantization (trellis) and the tuned quantization tables of the JPEG encoder.</summary>
public class JpegTrellisTests
{
    static readonly object PathLock = new();

    /// <summary>A real photograph (200 x 152): the decoded pixels of a fixture.</summary>
    static RawImage Photo() => TestUtil.LoadRef(TestUtil.Fixture("jpeg/photo_420.jpg.ref"));

    static RawImage Synthetic(int w, int h, ColorModel color, int seed = 1)
    {
        var img = RawImage.Create(w, h, color, SampleType.UInt8);
        var d = img.GetData<byte>();
        int ch = img.Channels;
        var rnd = new Random(seed);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int c = 0; c < ch; c++)
                {
                    double v = 0.5 + 0.3 * Math.Sin(x * (0.13 + 0.04 * c) + seed) * Math.Cos(y * (0.09 + 0.02 * c)) + 0.08 * Math.Sin((x - y) * 0.45 + c);
                    v += (rnd.NextDouble() - 0.5) * 0.1;
                    d[(y * w + x) * ch + c] = (byte)Math.Clamp(v * 255, 0, 255);
                }
        return img;
    }

    static byte[] Encode(RawImage img, JpegEncodeOptions o)
    {
        using var ms = new MemoryStream();
        Jpeg.Encode(img, ms, o);
        return ms.ToArray();
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (var color in new[] { ColorModel.Gray, ColorModel.Rgb })
            foreach (var (w, h) in new[] { (1, 1), (9, 7), (33, 17), (64, 48), (83, 61) })
                foreach (int q in new[] { 15, 75, 95 })
                    yield return new object[] { color, w, h, q, JpegSubsampling.Auto, true, false, 0 };
        foreach (var ss in new[] { JpegSubsampling.Chroma444, JpegSubsampling.Chroma422, JpegSubsampling.Chroma420 })
        {
            yield return new object[] { ColorModel.Rgb, 70, 50, 80, ss, false, false, 0 };
            yield return new object[] { ColorModel.Rgb, 70, 50, 50, ss, true, true, 0 };
            yield return new object[] { ColorModel.Rgb, 70, 50, 60, ss, true, false, 3 };
            yield return new object[] { ColorModel.Rgb, 70, 50, 85, ss, false, true, 2 };
        }
        yield return new object[] { ColorModel.Gray, 70, 50, 40, JpegSubsampling.Auto, true, true, 5 };
        yield return new object[] { ColorModel.Gray, 70, 50, 100, JpegSubsampling.Auto, false, false, 0 };
        yield return new object[] { ColorModel.Rgb, 70, 50, 1, JpegSubsampling.Auto, true, false, 0 };
    }

    /// <summary>Trellis output is an ordinary stream: libjpeg-turbo (Pillow) reads exactly the pixels our decoder reads.</summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void TrellisOutputDecodesEverywhere(ColorModel color, int w, int h, int quality, JpegSubsampling ss, bool optimize, bool progressive, int restart)
    {
        var src = Synthetic(w, h, color, seed: w * 7 + h + quality);
        var opt = new JpegEncodeOptions { Trellis = true, Quality = quality, Subsampling = ss, OptimizeHuffman = optimize, Progressive = progressive, RestartInterval = restart };
        var bytes = Encode(src, opt);
        var back = Jpeg.Decode(bytes);
        Assert.Equal((w, h), (back.Width, back.Height));
        if (w >= 16 && h >= 16)
        {
            double floor = quality >= 90 ? 30 : quality >= 50 ? 24 : quality >= 10 ? 19 : 10;
            Assert.True(TestUtil.Psnr(src, back, 255) > floor, $"PSNR {TestUtil.Psnr(src, back, 255):F1}");
        }
        var oracle = TestUtil.OracleDecode(bytes, ".jpg");
        if (oracle != null) TestUtil.AssertEqual(oracle, back, 0);
    }

    /// <summary>The trellis picks the coefficients once; baseline and progressive files carry the same ones.</summary>
    [Theory]
    [InlineData(ColorModel.Rgb, 0)]
    [InlineData(ColorModel.Gray, 4)]
    public void ProgressiveAndBaselineAgree(ColorModel color, int restart)
    {
        var src = color == ColorModel.Rgb ? Photo() : Synthetic(120, 90, ColorModel.Gray, 3);
        foreach (int q in new[] { 30, 75, 92 })
        {
            var a = Jpeg.Decode(Encode(src, new JpegEncodeOptions { Trellis = true, Quality = q, RestartInterval = restart }));
            var b = Jpeg.Decode(Encode(src, new JpegEncodeOptions { Trellis = true, Quality = q, RestartInterval = restart, Progressive = true }));
            TestUtil.AssertEqual(a, b, 0);
        }
    }

    [Fact]
    public void QualityOrdersSizeAndFidelity()
    {
        foreach (var src in new[] { Photo(), Synthetic(160, 120, ColorModel.Rgb, 5) })
        {
            long lastSize = 0; double lastPsnr = 0;
            foreach (int q in new[] { 5, 20, 40, 60, 75, 85, 92, 98, 100 })
            {
                var bytes = Encode(src, new JpegEncodeOptions { Trellis = true, Quality = q, Subsampling = JpegSubsampling.Chroma420 });
                double psnr = TestUtil.Psnr(src, Jpeg.Decode(bytes), 255);
                Assert.True(bytes.Length > lastSize, $"q{q}: size {bytes.Length} <= {lastSize}");
                Assert.True(psnr > lastPsnr, $"q{q}: psnr {psnr:F2} <= {lastPsnr:F2}");
                lastSize = bytes.Length; lastPsnr = psnr;
            }
        }
    }

    /// <summary>
    /// The trellis spends fewer bytes at a given quality, and buys that with little error: per byte saved it gives up
    /// less than plain rounding at a lower quality would.
    /// </summary>
    [Fact]
    public void TrellisSavesBytes()
    {
        var src = Photo();
        foreach (int q in new[] { 40, 75, 90 })
        {
            var plain = Encode(src, new JpegEncodeOptions { Quality = q, Trellis = false });
            var trellis = Encode(src, new JpegEncodeOptions { Trellis = true, Quality = q });
            Assert.True(trellis.Length < plain.Length * (q < 90 ? 0.97 : 0.99), $"q{q}: {trellis.Length} vs {plain.Length}");
            double pPlain = TestUtil.Psnr(src, Jpeg.Decode(plain), 255), pTrellis = TestUtil.Psnr(src, Jpeg.Decode(trellis), 255);
            Assert.True(pTrellis > pPlain - 1.0, $"q{q}: {pTrellis:F2} dB vs {pPlain:F2} dB");
            // plain rounding at the quality that gives about the trellis' size is worse
            int lower = q;
            byte[] smaller;
            do smaller = Encode(src, new JpegEncodeOptions { Quality = --lower, Trellis = false });
            while (smaller.Length > trellis.Length && lower > 1);
            Assert.True(pTrellis > TestUtil.Psnr(src, Jpeg.Decode(smaller), 255), $"q{q}: q{lower} without trellis is as good");
        }
    }

    /// <summary>The tuned tables give smaller files than the Annex K ones for the same error (here: PSNR at matched size).</summary>
    [Fact]
    public void TunedTablesBeatAnnexK()
    {
        var src = Photo();
        foreach (int q in new[] { 50, 80 })
        {
            var k = Encode(src, new JpegEncodeOptions { Quality = q, QuantTables = JpegQuantTables.AnnexK, Trellis = false });
            double pk = TestUtil.Psnr(src, Jpeg.Decode(k), 255);
            // the tuned table at the largest quality that still fits in the Annex K file's size
            int t = 100;
            byte[] tuned;
            do tuned = Encode(src, new JpegEncodeOptions { Quality = t--, Trellis = false });
            while (tuned.Length > k.Length && t > 1);
            Assert.True(TestUtil.Psnr(src, Jpeg.Decode(tuned), 255) > pk - 0.1, $"q{q}");
        }
    }

    /// <summary>
    /// Without trellis and with the Annex K tables the encoder is a classic libjpeg encode: the quantization tables are
    /// those libjpeg-turbo (through Pillow) writes for the same quality, and the file size agrees closely.
    /// </summary>
    [Theory]
    [InlineData(30)]
    [InlineData(75)]
    [InlineData(95)]
    public void AnnexKWithoutTrellisIsClassic(int quality)
    {
        var src = Photo();
        var o = new JpegEncodeOptions { Quality = quality, Trellis = false, QuantTables = JpegQuantTables.AnnexK, Subsampling = JpegSubsampling.Chroma420 };
        var bytes = Encode(src, o);
        Assert.Equal(bytes, Encode(src, o));
        Assert.NotEqual(bytes, Encode(src, new JpegEncodeOptions { Trellis = true, Quality = quality, Subsampling = JpegSubsampling.Chroma420 }));
        if (!TestUtil.PythonAvailable) return;

        var tmp = Path.Combine(TestUtil.FixtureDir, "_tmp");
        Directory.CreateDirectory(tmp);
        var f = Path.Combine(tmp, Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllBytes(f + ".rgb", src.GetData<byte>());
            var (exit, output) = TestUtil.RunPython($"-c \"from PIL import Image; Image.frombytes('RGB', ({src.Width}, {src.Height}), open(r'{f}.rgb', 'rb').read()).save(r'{f}.jpg', quality={quality}, optimize=True, subsampling=2)\"");
            Assert.True(exit == 0, output);
            var reference = File.ReadAllBytes(f + ".jpg");
            Assert.Equal(Dqt(reference), Dqt(bytes));
            Assert.InRange(bytes.Length, reference.Length * 0.99, reference.Length * 1.01);
        }
        finally
        {
            File.Delete(f + ".rgb"); File.Delete(f + ".jpg");
        }

        static byte[] Dqt(byte[] jpeg)
        {
            var all = new List<byte>();
            for (int i = 2; i + 4 <= jpeg.Length && jpeg[i] == 0xFF && jpeg[i + 1] != 0xDA;)
            {
                int len = jpeg[i + 2] << 8 | jpeg[i + 3];
                if (jpeg[i + 1] == 0xDB) all.AddRange(jpeg.AsSpan(i + 4, len - 2).ToArray());
                i += 2 + len;
            }
            return all.ToArray();
        }
    }

    /// <summary>Same bytes from the AVX2 and the scalar code of the trellis and the DCT, and from concurrent encodes.</summary>
    [Fact]
    public void DeterministicAcrossPathsAndThreads()
    {
        var images = new[] { Photo(), Synthetic(77, 45, ColorModel.Gray, 9) };
        var options = new[]
        {
            new JpegEncodeOptions { Trellis = true, Quality = 75 },
            new JpegEncodeOptions { Trellis = true, Quality = 35, Progressive = true },
            new JpegEncodeOptions { Trellis = true, Quality = 90, OptimizeHuffman = false, RestartInterval = 2 },
            new JpegEncodeOptions { Trellis = true, Quality = 60, Subsampling = JpegSubsampling.Chroma444, QuantTables = JpegQuantTables.AnnexK },
        };
        lock (PathLock)
        {
            foreach (var img in images)
                foreach (var o in options)
                {
                    var expected = Encode(img, o);
                    Parallel.For(0, 8, _ => Assert.Equal(expected, Encode(img, o)));
                    try
                    {
                        JpegEncoder.TrellisForceScalar = true;
                        Assert.Equal(expected, Encode(img, o));
                        JpegDct.ForceLevel = 1;
                        Assert.Equal(expected, Encode(img, o));
                        JpegDct.ForceLevel = 0;
                        Assert.Equal(expected, Encode(img, o));
                    }
                    finally
                    {
                        JpegEncoder.TrellisForceScalar = false;
                        JpegDct.ForceLevel = -1;
                    }
                }
        }
    }

    /// <summary>12 bit samples are rounded as before; arithmetic coding carries the same coefficients as Huffman coding.</summary>
    [Fact]
    public void TrellisWithOtherCodings()
    {
        var wide = RawImage.Create(40, 30, ColorModel.Rgb, SampleType.UInt16);
        var w = wide.GetData<ushort>();
        for (int i = 0; i < w.Length; i++) w[i] = (ushort)(2048 + 1500 * Math.Sin(i * 0.37));
        Assert.Equal(Encode(wide, new JpegEncodeOptions { Trellis = true, Quality = 80 }), Encode(wide, new JpegEncodeOptions { Quality = 80, Trellis = false }));

        var src = Photo();
        foreach (bool progressive in new[] { false, true })
        {
            var arith = Encode(src, new JpegEncodeOptions { Trellis = true, Quality = 70, Arithmetic = true, Progressive = progressive });
            var huffman = Encode(src, new JpegEncodeOptions { Trellis = true, Quality = 70, Progressive = progressive });
            TestUtil.AssertEqual(Jpeg.Decode(huffman), Jpeg.Decode(arith), 0);
            Assert.True(arith.Length < Encode(src, new JpegEncodeOptions { Quality = 70, Arithmetic = true, Progressive = progressive, Trellis = false }).Length);
        }
    }
}
