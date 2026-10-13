using PixCodec;
using PixCodec.JpegImpl;
using Xunit;

namespace PixCodec.Tests;

public class JpegTests
{
    public static IEnumerable<object[]> Fixtures() => TestUtil.Files("jpeg", ".jpg");
    public static IEnumerable<object[]> MiscWithRef() => TestUtil.Files("jpeg/misc", ".jpg").Where(f => File.Exists(TestUtil.Fixture((string)f[0]) + ".ref"));
    public static IEnumerable<object[]> Unsupported() => TestUtil.Files("jpeg/unsupported", ".jpg");

    static byte[] Load(string file) => File.ReadAllBytes(TestUtil.Fixture(file));

    // ------------------------------------------------------------------ decoding

    /// <summary>
    /// Every fixture decodes to exactly what Pillow (libjpeg-turbo) produces: the IDCT, the chroma upsampling
    /// filters and the color conversion reproduce libjpeg's arithmetic, so the tolerance is zero.
    /// </summary>
    [Theory]
    [MemberData(nameof(Fixtures))]
    [MemberData(nameof(MiscWithRef))]
    public void DecodeMatchesReference(string file)
    {
        var expected = TestUtil.LoadRef(TestUtil.Fixture(file) + ".ref");
        var data = Load(file);
        var actual = Jpeg.Decode(data);
        TestUtil.AssertEqual(expected, actual, 0);

        Assert.True(Jpeg.IsMatch(data));
        var info = Jpeg.GetInfo(data);
        Assert.Equal(new ImageInfo(ImageFormat.Jpeg, actual.Width, actual.Height, actual.Channels, actual.SampleType, actual.Color), info);
    }

    /// <summary>The scalar and the generic vector code paths give the same pixels as the AVX2 one.</summary>
    [Theory]
    [MemberData(nameof(Fixtures))]
    public void DecodePathsAgree(string file)
    {
        var data = Load(file);
        var expected = TestUtil.LoadRef(TestUtil.Fixture(file) + ".ref");
        lock (PathLock)
        {
            try
            {
                JpegDct.ForceLevel = 0; JpegPost.ForceScalar = true;
                TestUtil.AssertEqual(expected, Jpeg.Decode(data), 0);
                JpegDct.ForceLevel = 1; JpegPost.ForceScalar = false;
                TestUtil.AssertEqual(expected, Jpeg.Decode(data), 0);
            }
            finally
            {
                JpegDct.ForceLevel = -1; JpegPost.ForceScalar = false;
            }
        }
    }

    static readonly object PathLock = new();

    [Theory]
    [MemberData(nameof(Unsupported))]
    public void UnsupportedVariantsAreRejected(string file)
    {
        var data = Load(file);
        Assert.True(Jpeg.IsMatch(data));
        Assert.Throws<ImageNotSupportedException>(() => Jpeg.Decode(data));
    }

    [Fact]
    public void Metadata()
    {
        var img = Jpeg.Decode(Load("jpeg/meta_exif_icc.jpg"));
        var m = img.Metadata!;
        Assert.Equal(300, m.DpiX);
        Assert.Equal(150, m.DpiY);
        Assert.Equal(6, m.Orientation);
        Assert.NotNull(m.Exif);
        Assert.True(m.Exif!.AsSpan(0, 2).SequenceEqual("II"u8) || m.Exif.AsSpan(0, 2).SequenceEqual("MM"u8));
        Assert.Equal(File.ReadAllBytes(TestUtil.Fixture("jpeg/meta_exif_icc.icc")), m.IccProfile);

        m = Jpeg.Decode(Load("jpeg/meta_dpi.jpg")).Metadata!;
        Assert.Equal((72.0, 96.0), (m.DpiX, m.DpiY));
        Assert.Null(m.Exif);
        Assert.Null(m.IccProfile);
        Assert.Equal(0, m.Orientation);
    }

    [Fact]
    public void TruncatedScanDecodesWhatIsThere()
    {
        var full = Jpeg.Decode(Load("jpeg/misc/truncated_444_full.jpg"));
        var cut = Jpeg.Decode(Load("jpeg/misc/truncated_444.jpg"));
        Assert.Equal((full.Width, full.Height), (cut.Width, cut.Height));
        var f = full.GetData<byte>(); var c = cut.GetData<byte>();
        // two thirds of the data are present: the top half must be intact, the bottom rows are filled with gray
        int half = f.Length / 2;
        Assert.True(f.AsSpan(0, half).SequenceEqual(c.AsSpan(0, half)));
        Assert.All(c.Skip(f.Length - 8 * 64 * 3).ToArray(), v => Assert.Equal(128, v));
    }

    [Fact]
    public void LostRestartMarkerOnlyDamagesItsInterval()
    {
        var full = Jpeg.Decode(Load("jpeg/misc/rst_lost_full.jpg"));
        var cut = Jpeg.Decode(Load("jpeg/misc/rst_lost.jpg"));
        var f = full.GetData<byte>(); var c = cut.GetData<byte>();
        int differing = 0;
        for (int i = 0; i < f.Length; i += 3) if (f[i] != c[i] || f[i + 1] != c[i + 1] || f[i + 2] != c[i + 2]) differing++;
        // restart interval = 4 blocks: at most two intervals (the one that ran into the next and the skipped one) are affected
        Assert.InRange(differing, 1, 2 * 4 * 64);
    }

    [Fact]
    public void HeaderErrors()
    {
        Assert.False(Jpeg.IsMatch(new byte[] { 0xFF, 0xD8 }));
        Assert.False(Jpeg.IsMatch(new byte[] { 0x89, 0x50, 0x4E, 0x47 }));
        Assert.Throws<ImageFormatException>(() => Jpeg.Decode(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }));
        Assert.Throws<ImageFormatException>(() => Jpeg.GetInfo(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }));
        Assert.Throws<ImageFormatException>(() => Jpeg.Decode(ReadOnlySpan<byte>.Empty));

        var data = Load("jpeg/rgb_420_17x33.jpg");
        // cut inside the headers: nothing to decode
        int sos = data.AsSpan().IndexOf(new byte[] { 0xFF, 0xDA });
        Assert.Throws<ImageFormatException>(() => Jpeg.Decode(data.AsSpan(0, sos)));
        Assert.Throws<ImageFormatException>(() => Jpeg.Decode(data.AsSpan(0, sos + 5)));
        Assert.Throws<ImageFormatException>(() => Jpeg.Decode(data.AsSpan(0, 30)));

        // a header that claims a gigantic image backed by almost no data must not allocate it
        var lie = (byte[])data.Clone();
        int sof = lie.AsSpan().IndexOf(new byte[] { 0xFF, 0xC0 });
        lie[sof + 5] = 0xFF; lie[sof + 6] = 0xFF; lie[sof + 7] = 0xFF; lie[sof + 8] = 0xFF;
        Assert.Equal((65535, 65535), (Jpeg.GetInfo(lie).Width, Jpeg.GetInfo(lie).Height));
        Assert.ThrowsAny<Exception>(() => Jpeg.Decode(lie));
        try { Jpeg.Decode(lie); } catch (Exception e) { Assert.True(e is ImageFormatException or ImageNotSupportedException, e.ToString()); }
    }

    // ------------------------------------------------------------------ JPEG in TIFF

    public static IEnumerable<object[]> TiffStreams()
        => Directory.GetFiles(TestUtil.Fixture("jpeg/tiff"), "*.ref").OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => new object[] { Path.GetFileNameWithoutExtension(f) });

    static RawImage Crop(RawImage img, int w, int h)
    {
        var o = RawImage.Create(w, h, img.Color, SampleType.UInt8, img.Channels);
        var s = img.GetData<byte>(); var d = o.GetData<byte>();
        for (int y = 0; y < h; y++) s.AsSpan(y * img.Width * img.Channels, w * img.Channels).CopyTo(d.AsSpan(y * w * img.Channels));
        return o;
    }

    /// <summary>Tables-only + abbreviated stream pairs; the name says whether YCbCr is to be converted (_rgb) or samples returned as stored (_raw).</summary>
    [Theory]
    [MemberData(nameof(TiffStreams))]
    public void DecodeForTiff(string name)
    {
        var dir = TestUtil.Fixture("jpeg/tiff");
        var tables = File.ReadAllBytes(Path.Combine(dir, name + ".tables.bin"));
        var data = File.ReadAllBytes(Path.Combine(dir, name + ".data.bin"));
        var full = TestUtil.LoadRef(Path.Combine(dir, name + ".ref"));
        bool convert = name.EndsWith("_rgb");
        int ch = full.Channels;
        // whole frame, and strips / tiles smaller than the (padded) frame
        foreach (var (w, h) in new[] { (full.Width, full.Height), (full.Width - 1, full.Height), (full.Width, full.Height - 3), (17, 9), (1, 1) })
        {
            var dst = new byte[w * h * ch];
            Jpeg.DecodeForTiff(tables, data, w, h, ch, convert, dst);
            Assert.True(Crop(full, w, h).GetData<byte>().AsSpan().SequenceEqual(dst), $"{name} {w}x{h}");
        }

        // the abbreviated stream is useless without its tables; wrong geometry or component count is an error
        var buf = new byte[full.Width * full.Height * ch];
        Assert.Throws<ImageFormatException>(() => Jpeg.DecodeForTiff(ReadOnlySpan<byte>.Empty, data, full.Width, full.Height, ch, convert, buf));
        Assert.Throws<ImageFormatException>(() => Jpeg.DecodeForTiff(tables, data, full.Width + 1, full.Height, ch, convert, new byte[(full.Width + 1) * full.Height * ch]));
        Assert.Throws<ImageFormatException>(() => Jpeg.DecodeForTiff(tables, data, full.Width, full.Height, ch + 1, convert, new byte[full.Width * full.Height * (ch + 1)]));
        Assert.Throws<ImageFormatException>(() => Jpeg.DecodeForTiff(tables.AsSpan(0, tables.Length / 2), data, full.Width, full.Height, ch, convert, buf));

        // damaged strips never escape with anything but the codec's exceptions
        var rnd = new Random(name.Length * 31 + data.Length);
        for (int round = 0; round < 200; round++)
        {
            var bad = (byte[])data.Clone();
            if (round % 2 == 0) bad = bad.AsSpan(0, rnd.Next(bad.Length)).ToArray();
            else for (int i = 0; i < 3; i++) bad[rnd.Next(bad.Length)] = (byte)rnd.Next(256);
            try { Jpeg.DecodeForTiff(tables, bad, full.Width, full.Height, ch, convert, buf); }
            catch (ImageFormatException) { }
            catch (ImageNotSupportedException) { }
        }
    }

    /// <summary>Complete streams with no JPEGTables, markers ignored for the color decision, and concurrent use.</summary>
    [Fact]
    public void DecodeForTiffCompleteStreamsAndThreads()
    {
        // a JFIF YCbCr file: converted on request, raw YCbCr otherwise (never guessed from the JFIF marker)
        var ycc = Load("jpeg/photo_420.jpg");
        var rgb = TestUtil.LoadRef(TestUtil.Fixture("jpeg/photo_420.jpg.ref"));
        var a = new byte[rgb.Width * rgb.Height * 3]; var b = new byte[a.Length];
        Jpeg.DecodeForTiff(default, ycc, rgb.Width, rgb.Height, 3, true, a);
        Assert.Equal(rgb.GetData<byte>(), a);
        Jpeg.DecodeForTiff(default, ycc, rgb.Width, rgb.Height, 3, false, b);
        Assert.NotEqual(a, b);

        // an Adobe RGB file decodes the same as stored, and "converting" it applies the YCbCr matrix regardless of the marker
        var stored = TestUtil.LoadRef(TestUtil.Fixture("jpeg/rgb_adobe.jpg.ref"));
        var c = new byte[stored.Width * stored.Height * 3];
        Jpeg.DecodeForTiff(default, Load("jpeg/rgb_adobe.jpg"), stored.Width, stored.Height, 3, false, c);
        Assert.Equal(stored.GetData<byte>(), c);
        Jpeg.DecodeForTiff(default, Load("jpeg/rgb_adobe.jpg"), stored.Width, stored.Height, 3, true, c);
        Assert.NotEqual(stored.GetData<byte>(), c);

        var dir = TestUtil.Fixture("jpeg/tiff");
        var tables = File.ReadAllBytes(Path.Combine(dir, "ycc420_rgb.tables.bin"));
        var data = File.ReadAllBytes(Path.Combine(dir, "ycc420_rgb.data.bin"));
        var expected = TestUtil.LoadRef(Path.Combine(dir, "ycc420_rgb.ref"));
        Parallel.For(0, 64, i =>
        {
            var dst = new byte[expected.Width * expected.Height * 3];
            Jpeg.DecodeForTiff(tables, data, expected.Width, expected.Height, 3, true, dst);
            Assert.Equal(expected.GetData<byte>(), dst);
        });
    }

    // ------------------------------------------------------------------ robustness

    static void DecodeDamaged(ReadOnlySpan<byte> data, string what)
    {
        try
        {
            var img = Jpeg.Decode(data);
            Assert.True(img.Width > 0 && img.Height > 0);
        }
        catch (ImageFormatException) { }
        catch (ImageNotSupportedException) { }
        catch (Exception e)
        {
            Assert.Fail($"{what}: unexpected {e.GetType().Name}: {e}");
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void TruncationNeverCrashes(string file)
    {
        var data = Load(file);
        if (data.Length > 20000) return; // the big ICC fixture: covered by the smaller ones
        int step = Math.Max(1, data.Length / 150);
        for (int n = 0; n < data.Length; n += step) DecodeDamaged(data.AsSpan(0, n), $"{file} truncated at {n}");
        for (int n = Math.Max(0, data.Length - 12); n < data.Length; n++) DecodeDamaged(data.AsSpan(0, n), $"{file} truncated at {n}");
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void CorruptionNeverCrashes(string file)
    {
        var data = Load(file);
        if (data.Length > 20000) return;
        var rnd = new Random(file.GetHashCode(StringComparison.Ordinal) ^ 12345);
        rnd = new Random(data.Length * 7919 + data[data.Length / 2]);
        for (int round = 0; round < 120; round++)
        {
            var d = (byte[])data.Clone();
            int flips = 1 + rnd.Next(4);
            for (int i = 0; i < flips; i++)
            {
                // bias half of the damage into the first bytes, where the headers live
                int pos = rnd.Next(2) == 0 ? rnd.Next(Math.Min(d.Length, 700)) : rnd.Next(d.Length);
                d[pos] = rnd.Next(3) == 0 ? (byte)0xFF : (byte)rnd.Next(256);
            }
            DecodeDamaged(d, $"{file} corruption round {round}");
        }
    }

    // ------------------------------------------------------------------ encoding

    /// <summary>Smooth blobs with a little texture and a hard edge: representative for a photo codec.</summary>
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
                    double v = 0.5 + 0.35 * Math.Sin(x * (0.11 + 0.05 * c) + seed) * Math.Cos(y * (0.07 + 0.03 * c)) + 0.1 * Math.Sin((x + y) * 0.31 + c);
                    if (x > w * 2 / 3 && y < h / 3) v = c == 0 ? 0.9 : 0.15;
                    v += (rnd.NextDouble() - 0.5) * 0.04;
                    bool alpha = (color == ColorModel.Rgba && c == 3) || (color == ColorModel.GrayAlpha && c == 1);
                    d[(y * w + x) * ch + c] = alpha ? (byte)rnd.Next(256) : (byte)Math.Clamp(v * 255, 0, 255);
                }
        return img;
    }

    /// <summary>Drops the alpha channel: what the encoder is expected to store.</summary>
    static RawImage Opaque(RawImage img)
    {
        if (!img.HasAlpha) return img;
        int ch = img.Channels - 1;
        var o = RawImage.Create(img.Width, img.Height, ch == 1 ? ColorModel.Gray : ColorModel.Rgb, SampleType.UInt8);
        var s = img.GetData<byte>(); var d = o.GetData<byte>();
        for (int i = 0, n = img.Width * img.Height; i < n; i++)
            for (int c = 0; c < ch; c++) d[i * ch + c] = s[i * img.Channels + c];
        return o;
    }

    static byte[] Encode(RawImage img, JpegEncodeOptions? o = null)
    {
        using var ms = new MemoryStream();
        Jpeg.Encode(img, ms, o);
        return ms.ToArray();
    }

    public static IEnumerable<object[]> EncodeCases()
    {
        var sizes = new[] { (1, 1), (7, 5), (8, 8), (17, 33), (33, 17), (64, 48), (130, 97) };
        foreach (var color in new[] { ColorModel.Gray, ColorModel.Rgb })
            foreach (var (w, h) in sizes)
                foreach (var ss in color == ColorModel.Gray ? new[] { JpegSubsampling.Auto } : new[] { JpegSubsampling.Chroma444, JpegSubsampling.Chroma422, JpegSubsampling.Chroma420 })
                    foreach (bool progressive in new[] { false, true })
                        yield return new object[] { color, w, h, 85, ss, true, progressive, 0 };
        foreach (var color in new[] { ColorModel.Gray, ColorModel.GrayAlpha, ColorModel.Rgb, ColorModel.Rgba })
        {
            foreach (int q in new[] { 1, 25, 50, 75, 95, 100 })
            {
                yield return new object[] { color, 67, 45, q, JpegSubsampling.Auto, true, false, 0 };
                yield return new object[] { color, 67, 45, q, JpegSubsampling.Chroma420, false, false, 0 };
                yield return new object[] { color, 67, 45, q, JpegSubsampling.Chroma422, true, true, 0 };
            }
            foreach (int restart in new[] { 1, 3, 100 })
            {
                yield return new object[] { color, 70, 50, 80, JpegSubsampling.Chroma420, true, false, restart };
                yield return new object[] { color, 70, 50, 80, JpegSubsampling.Chroma444, false, false, restart };
                yield return new object[] { color, 70, 50, 80, JpegSubsampling.Chroma422, true, true, restart };
            }
        }
    }

    [Theory]
    [MemberData(nameof(EncodeCases))]
    public void EncodeRoundTrips(ColorModel color, int w, int h, int quality, JpegSubsampling ss, bool optimize, bool progressive, int restart)
    {
        var src = Synthetic(w, h, color, seed: w + h + quality);
        var opt = new JpegEncodeOptions { Quality = quality, Subsampling = ss, OptimizeHuffman = optimize, Progressive = progressive, RestartInterval = restart };
        var bytes = Encode(src, opt);
        var expected = Opaque(src);

        var back = Jpeg.Decode(bytes);
        Assert.Equal(expected.Color, back.Color);
        double psnr = TestUtil.Psnr(expected, back, 255);
        // chroma subsampling of tiny / hard edged content costs fidelity; the floors are far below what a working codec gives
        double floor = quality >= 95 ? 33 : quality >= 75 ? 27 : quality >= 50 ? 25 : quality >= 25 ? 22 : 12;
        bool subsampled = color is ColorModel.Rgb or ColorModel.Rgba && (ss is JpegSubsampling.Chroma420 or JpegSubsampling.Chroma422 || (ss == JpegSubsampling.Auto && quality < 90));
        if (subsampled) floor = Math.Min(floor, 23); // the saturated hard edge in the test image dominates the error
        if (w >= 16 && h >= 16) Assert.True(psnr >= floor, $"PSNR {psnr:F1} dB below {floor}");

        var info = Jpeg.GetInfo(bytes);
        Assert.Equal((w, h, expected.Channels), (info.Width, info.Height, info.Channels));

        // an independent decoder must read the same pixels out of our stream (our decoder matches libjpeg exactly)
        var oracle = TestUtil.OracleDecode(bytes, ".jpg");
        if (oracle == null) return;
        TestUtil.AssertEqual(oracle, back, 0);
    }

    /// <summary>More than 32767 blocks with empty AC bands: end-of-band runs and the correction bit buffer have to be split.</summary>
    [Theory]
    [InlineData(ColorModel.Gray, 0, false)]
    [InlineData(ColorModel.Rgb, 0, false)]
    [InlineData(ColorModel.Gray, 5, false)]
    [InlineData(ColorModel.Gray, 0, true)]
    [InlineData(ColorModel.Rgb, 0, true)]
    public void EncodeProgressiveLongRuns(ColorModel color, int restart, bool flat)
    {
        int w = 1808, h = 1210; // 34352 luma blocks
        var src = RawImage.Create(w, h, color, SampleType.UInt8);
        var d = src.GetData<byte>();
        int ch = src.Channels;
        var rnd = new Random(9);
        if (flat)
        {
            // every AC band is empty up to the very last block
            Array.Fill(d, (byte)90);
            d[^1] = 255;
        }
        else
        {
            // faint texture everywhere: after the first AC passes most blocks only refine existing coefficients,
            // which piles up correction bits inside long end-of-band runs
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    for (int c = 0; c < ch; c++)
                        d[(y * w + x) * ch + c] = (byte)(100 + c * 20 + (int)(14 * Math.Sin(x * 0.8) * Math.Sin(y * 0.8)));
            for (int i = 0; i < 300; i++) d[rnd.Next(d.Length)] = (byte)rnd.Next(256);
        }

        var bytes = Encode(src, new JpegEncodeOptions { Quality = 92, Progressive = true, RestartInterval = restart, Subsampling = JpegSubsampling.Chroma420 });
        var back = Jpeg.Decode(bytes);
        Assert.True(TestUtil.Psnr(src, back, 255) > 30);
        var oracle = TestUtil.OracleDecode(bytes, ".jpg");
        if (oracle != null) TestUtil.AssertEqual(oracle, back, 0);

        // the same coefficients written sequentially decode to the same pixels
        var baseline = Jpeg.Decode(Encode(src, new JpegEncodeOptions { Quality = 92, RestartInterval = restart, Subsampling = JpegSubsampling.Chroma420 }));
        TestUtil.AssertEqual(baseline, back, 0);
    }

    /// <summary>
    /// Blocks whose 63 AC coefficients are all significant from the first pass on: the refinement scans then consist of
    /// end-of-band runs carrying 63 correction bits per block, which overflows the encoder's correction bit buffer
    /// and forces it to cut the run.
    /// </summary>
    [Fact]
    public void EncodeProgressiveDenseRefinement()
    {
        int w = 320, h = 64;
        var src = RawImage.Create(w, h, ColorModel.Gray, SampleType.UInt8);
        var d = src.GetData<byte>();
        var rnd = new Random(4);
        for (int by = 0; by < h / 8; by++)
            for (int bx = 0; bx < w / 8; bx++)
            {
                // synthesize the block from AC coefficients of magnitude 24 with random signs
                var coef = new double[64];
                for (int i = 1; i < 64; i++) coef[i] = rnd.Next(2) == 0 ? 24 : -24;
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                    {
                        double v = 0;
                        for (int vv = 0; vv < 8; vv++)
                            for (int u = 0; u < 8; u++)
                                v += (u == 0 ? Math.Sqrt(0.5) : 1) * (vv == 0 ? Math.Sqrt(0.5) : 1) / 4 * coef[vv * 8 + u]
                                     * Math.Cos((2 * x + 1) * u * Math.PI / 16) * Math.Cos((2 * y + 1) * vv * Math.PI / 16);
                        d[(by * 8 + y) * w + bx * 8 + x] = (byte)Math.Clamp(Math.Round(v + 128), 0, 255);
                    }
            }
        var bytes = Encode(src, new JpegEncodeOptions { Quality = 100, Progressive = true });
        var back = Jpeg.Decode(bytes);
        Assert.True(TestUtil.Psnr(src, back, 255) > 45);
        TestUtil.AssertEqual(Jpeg.Decode(Encode(src, new JpegEncodeOptions { Quality = 100 })), back, 0);
        var oracle = TestUtil.OracleDecode(bytes, ".jpg");
        if (oracle != null) TestUtil.AssertEqual(oracle, back, 0);
    }

    [Fact]
    public void EncodeQualityOrdersSizeAndFidelity()
    {
        var src = Synthetic(160, 120, ColorModel.Rgb, 3);
        long lastSize = 0; double lastPsnr = 0;
        foreach (int q in new[] { 10, 40, 70, 90, 98 })
        {
            var bytes = Encode(src, new JpegEncodeOptions { Quality = q, Subsampling = JpegSubsampling.Chroma444 });
            double psnr = TestUtil.Psnr(src, Jpeg.Decode(bytes), 255);
            Assert.True(bytes.Length > lastSize, $"q{q}: size {bytes.Length} <= {lastSize}");
            Assert.True(psnr > lastPsnr, $"q{q}: psnr {psnr} <= {lastPsnr}");
            lastSize = bytes.Length; lastPsnr = psnr;
        }
        Assert.True(lastPsnr > 40);

        // optimized tables never lose against the standard ones, progressive is in the same ballpark
        var o = new JpegEncodeOptions { Quality = 80 };
        int plain = Encode(src, new JpegEncodeOptions { Quality = 80, OptimizeHuffman = false }).Length;
        int optimized = Encode(src, o).Length;
        int progressive = Encode(src, new JpegEncodeOptions { Quality = 80, Progressive = true }).Length;
        Assert.True(optimized <= plain, $"{optimized} > {plain}");
        Assert.True(progressive < plain * 1.05, $"{progressive} vs {plain}");
    }

    [Fact]
    public void EncodeMetadataRoundTrips()
    {
        var src = Synthetic(20, 12, ColorModel.Rgb);
        var icc = new byte[150_000];
        new Random(5).NextBytes(icc);
        var exif = Jpeg.Decode(Load("jpeg/meta_exif_icc.jpg")).Metadata!.Exif!;
        src.Metadata = new ImageMetadata { DpiX = 300, DpiY = 72, IccProfile = icc, Exif = exif };
        foreach (bool progressive in new[] { false, true })
        {
            var bytes = Encode(src, new JpegEncodeOptions { Progressive = progressive });
            var m = Jpeg.Decode(bytes).Metadata!;
            Assert.Equal((300.0, 72.0), (m.DpiX, m.DpiY));
            Assert.Equal(icc, m.IccProfile);
            Assert.Equal(exif, m.Exif);
            Assert.Equal(6, m.Orientation);
        }

        // orientation without an EXIF blob is still carried
        src.Metadata = new ImageMetadata { Orientation = 8 };
        var m2 = Jpeg.Decode(Encode(src)).Metadata!;
        Assert.Equal(8, m2.Orientation);
        Assert.Equal(0, m2.DpiX);

        src.Metadata = null;
        Assert.Null(Jpeg.Decode(Encode(src)).Metadata);
    }

    [Fact]
    public void EncodeRejectsUnsupportedInput()
    {
        Assert.Throws<ImageNotSupportedException>(() => Encode(RawImage.Create(4, 4, ColorModel.Rgb, SampleType.UInt16)));
        Assert.Throws<ImageNotSupportedException>(() => Encode(RawImage.Create(4, 4, ColorModel.Gray, SampleType.Float32)));
        Assert.Throws<ImageNotSupportedException>(() => Encode(RawImage.Create(4, 4, ColorModel.Other, SampleType.UInt8, 5)));
    }

    /// <summary>All forward / inverse DCT code paths are bit identical, and a round trip is accurate to the rounding.</summary>
    [Fact]
    public unsafe void DctPathsAgree()
    {
        var rnd = new Random(42);
        short* coef = stackalloc short[64];
        int* quant = stackalloc int[64];
        float* recip = stackalloc float[64];
        byte* a = stackalloc byte[64]; byte* b = stackalloc byte[64]; byte* src = stackalloc byte[64];
        short* za = stackalloc short[64]; short* zb = stackalloc short[64];
        lock (PathLock)
        {
            try
            {
                for (int iter = 0; iter < 3000; iter++)
                {
                    int density = rnd.Next(1, 65), range = iter % 3 == 0 ? 32768 : 300;
                    for (int i = 0; i < 64; i++)
                    {
                        coef[i] = (short)(rnd.Next(64) < density ? rnd.Next(-range, range) : 0);
                        quant[i] = iter % 5 == 0 ? rnd.Next(1, 256) : rnd.Next(1, 20);
                        recip[i] = 1f / (8 * rnd.Next(1, 50));
                        src[i] = (byte)rnd.Next(256);
                    }
                    JpegDct.ForceLevel = -1; JpegDct.Idct(coef, quant, a, 8);
                    for (int level = 0; level <= 1; level++)
                    {
                        JpegDct.ForceLevel = level; JpegDct.Idct(coef, quant, b, 8);
                        Assert.True(new Span<byte>(a, 64).SequenceEqual(new Span<byte>(b, 64)), $"idct level {level} iteration {iter}");
                    }
                    JpegDct.ForceLevel = -1; JpegDct.FdctQuantize(src, 8, recip, za);
                    for (int level = 0; level <= 1; level++)
                    {
                        JpegDct.ForceLevel = level; JpegDct.FdctQuantize(src, 8, recip, zb);
                        Assert.True(new Span<short>(za, 64).SequenceEqual(new Span<short>(zb, 64)), $"fdct level {level} iteration {iter}");
                    }

                    // forward then inverse with unit quantization reproduces the samples to within one level
                    for (int i = 0; i < 64; i++) { recip[i] = 1f / 8; quant[i] = 1; }
                    JpegDct.ForceLevel = -1;
                    JpegDct.FdctQuantize(src, 8, recip, za);
                    for (int i = 0; i < 64; i++) coef[JpegTables.ZigZag[i]] = za[i];
                    JpegDct.Idct(coef, quant, b, 8);
                    for (int i = 0; i < 64; i++) Assert.InRange(b[i] - src[i], -1, 1);
                }
            }
            finally
            {
                JpegDct.ForceLevel = -1;
            }
        }
    }
}
