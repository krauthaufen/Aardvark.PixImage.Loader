using System.Buffers.Binary;
using Aardvark.ImageCodecs.KtxImpl;
using Xunit;
using static Aardvark.ImageCodecs.Tests.TestUtil;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>
/// The Basis Universal encoders (ETC1S with BasisLZ, UASTC LDR 4x4) and their KTX 2 output. Oracles: fixtures written by
/// our encoder and decoded by basisu (tests/oracle/gen_basis_encode.py), basisu's quality numbers on the sources in
/// fixtures/basis_enc/quality, and, when the tools are around, basisu ($PIXIMAGEIO_BASISU) and ktx validate
/// ($PIXIMAGEIO_KTX) on freshly encoded files.
/// </summary>
public class BasisEncodeTests
{
    public static IEnumerable<object[]> Fixtures => Files("basis_enc", ".ktx2");

    // ------------------------------------------------------------------ oracle fixtures

    /// <summary>basisu decodes the files our encoder wrote exactly as our decoder does.</summary>
    [Theory, MemberData(nameof(Fixtures))]
    public void BasisuDecodesOurFilesLikeWeDo(string file)
    {
        var path = Fixture(file);
        var t = Ktx.DecodeTexture(File.ReadAllBytes(path));
        var refs = Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*.*.*.ref");
        Assert.Equal(t.MipLevels * t.Layers * t.Faces, refs.Length);
        foreach (var r in refs)
        {
            var key = r[(path.Length + 1)..^4].Split('.').Select(int.Parse).ToArray();
            AssertSame(LoadRef(r), t.GetImage(key[0], key[1], key[2]), Path.GetFileName(r));
        }
    }

    [Theory, MemberData(nameof(Fixtures))]
    public void FixturesValidate(string file) => KtxTests.Validate(File.ReadAllBytes(Fixture(file)));

    static void AssertSame(RawImage expected, RawImage got, string what)
    {
        Assert.Equal((expected.Width, expected.Height, 4), (got.Width, got.Height, got.Channels));
        var e = expected.GetData<byte>();
        var g = got.GetBytes();
        int bad = -1;
        for (int i = 0; i < e.Length && bad < 0; i++) if (e[i] != g[i]) bad = i;
        Assert.True(bad < 0, bad < 0 ? "" :
            $"{what}: first difference at x {bad / 4 % expected.Width} y {bad / 4 / expected.Width} channel {bad % 4}: expected {e[bad]}, got {g[bad]}");
    }

    public static IEnumerable<object[]> HdrFixtures => Files("basis_hdr", ".ktx2");

    /// <summary>
    /// UASTC HDR 4x4 (KTX2: vkFormat ASTC_4x4_SFLOAT_BLOCK, DFD model 167) decodes to the Float16 RGBA basisu's own
    /// decoder gives, bit exact.
    /// </summary>
    [Theory, MemberData(nameof(HdrFixtures))]
    public void UastcHdrMatchesBasisu(string file)
    {
        var path = Fixture(file);
        var data = File.ReadAllBytes(path);
        var info = Ktx.GetTextureInfo(data);
        Assert.Equal("UASTC_HDR_4x4", info.Format);
        Assert.Equal(1000066000, info.VkFormat);
        Assert.Equal(TransferFunction.Linear, info.Transfer);
        var t = Ktx.DecodeTexture(data);
        var refs = Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*.*.*.ref");
        Assert.Equal(t.MipLevels, refs.Length);
        foreach (var r in refs)
        {
            var key = r[(path.Length + 1)..^4].Split('.').Select(int.Parse).ToArray();
            var e = LoadRef(r);
            var g = t.GetImage(key[0], key[1], key[2]);
            Assert.Equal((SampleType.Float16, 4), (g.SampleType, g.Channels));
            Assert.Equal((e.Width, e.Height), (g.Width, g.Height));
            Assert.True(e.GetBytes().SequenceEqual(g.GetBytes()), Path.GetFileName(r));
        }
        KtxTests.Validate(data);
    }

    // ------------------------------------------------------------------ quality against basisu's own numbers

    static IEnumerable<(string image, string format, double psnr, double bpp)> Oracle()
    {
        foreach (var line in File.ReadAllLines(Fixture("basis_enc/quality/oracle.txt")))
        {
            if (line.StartsWith('#') || line.Trim().Length == 0) continue;
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            yield return (p[0], p[1], double.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture), double.Parse(p[3], System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    static (double psnr, double bpp) Measure(RawImage src, KtxEncodeOptions o)
    {
        var k = Ktx.Encode(src, o);
        var dec = Ktx.Decode(k);
        return (PsnrRgb(KtxWriter.ToRgba8(src), dec.GetData<byte>()), k.Length * 8.0 / (src.Width * src.Height));
    }

    static double PsnrRgb(byte[] a, byte[] b)
    {
        double se = 0;
        long n = 0;
        for (int i = 0; i < a.Length; i += 4)
            for (int c = 0; c < 3; c++) { double d = a[i + c] - b[i + c]; se += d * d; n++; }
        return 10 * Math.Log10(255.0 * 255 * n / se);
    }

    /// <summary>UASTC (default effort) is within 0.5 dB of basisu -uastc (level 2) and not larger after zstd.</summary>
    [Fact]
    public void UastcQualityAgainstBasisu()
    {
        foreach (var (image, format, psnr, bpp) in Oracle().Where(o => o.format == "uastc"))
        {
            var src = ImageCodec.Decode(Fixture($"basis_enc/quality/{image}.png"));
            var (p, b) = Measure(src, new KtxEncodeOptions { Compression = TextureCompression.Uastc, Srgb = false });
            Assert.True(p >= psnr - 0.5, $"{image}: {p:F2} dB, basisu {psnr:F2} dB");
            Assert.True(b <= bpp * 1.05, $"{image}: {b:F3} bpp, basisu {bpp:F3} bpp");
        }
    }

    /// <summary>
    /// ETC1S: at the bit rate basisu -q 128 reaches, our PSNR (interpolated between our quality levels in log bit rate)
    /// is within 0.5 dB of basisu's.
    /// </summary>
    [Fact]
    public void Etc1sQualityAgainstBasisuAtEqualRate()
    {
        foreach (var (image, format, psnr, bpp) in Oracle().Where(o => o.format == "etc1s"))
        {
            var src = ImageCodec.Decode(Fixture($"basis_enc/quality/{image}.png"));
            var curve = new List<(double bpp, double psnr)>();
            foreach (int q in new[] { 64, 96, 128, 160, 192, 224, 255 })
            {
                var (p, b) = Measure(src, new KtxEncodeOptions { Compression = TextureCompression.Etc1s, Etc1sQuality = q, Srgb = false });
                curve.Add((b, p));
            }
            curve.Sort();
            Assert.True(curve[0].bpp <= bpp && curve[^1].bpp >= bpp, $"{image}: our rates {curve[0].bpp:F2}..{curve[^1].bpp:F2} miss basisu's {bpp:F2}");
            double at = 0;
            for (int i = 1; i < curve.Count; i++)
                if (curve[i].bpp >= bpp)
                {
                    var (b0, p0) = curve[i - 1];
                    var (b1, p1) = curve[i];
                    double t = b1 == b0 ? 1 : (Math.Log(bpp) - Math.Log(b0)) / (Math.Log(b1) - Math.Log(b0));
                    at = p0 + t * (p1 - p0);
                    break;
                }
            Assert.True(at >= psnr - 0.5, $"{image}: {at:F2} dB at {bpp:F2} bpp, basisu {psnr:F2} dB");
        }
    }

    // ------------------------------------------------------------------ round trips

    static RawImage Photo(int w, int h, bool alpha, int seed)
    {
        var r = new Random(seed);
        var img = RawImage.Create(w, h, alpha ? ColorModel.Rgba : ColorModel.Rgb, SampleType.UInt8);
        var d = img.GetData<byte>();
        int ch = img.Channels;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int c = 0; c < ch; c++)
                {
                    double v = 128 + 90 * Math.Sin(x * 0.11 + c) * Math.Cos(y * 0.07 + seed) + 25 * Math.Sin(x * 0.9 + y * 0.4 * c) + r.Next(-6, 7);
                    if ((x / 13 + y / 9) % 5 == 0 && c < 3) v = 40 + 60 * c;
                    d[(y * w + x) * ch + c] = (byte)Math.Clamp(v, 0, 255);
                }
        return img;
    }

    [Theory]
    [InlineData(TextureCompression.Etc1s, false, 37, 21)]
    [InlineData(TextureCompression.Etc1s, true, 64, 48)]
    [InlineData(TextureCompression.Uastc, false, 37, 21)]
    [InlineData(TextureCompression.Uastc, true, 64, 48)]
    public void RoundTrip(TextureCompression c, bool alpha, int w, int h)
    {
        var src = Photo(w, h, alpha, 3);
        foreach (var effort in new[] { BcQuality.Fast, BcQuality.Normal, BcQuality.High })
        {
            var k = Ktx.Encode(src, new KtxEncodeOptions { Compression = c, Quality = effort });
            KtxTests.Validate(k);
            var info = Ktx.GetTextureInfo(k);
            Assert.Equal(c == TextureCompression.Etc1s ? "ETC1S" : "UASTC", info.Format);
            Assert.Equal(0, info.VkFormat);
            Assert.Equal(c == TextureCompression.Etc1s ? KtxSupercompression.BasisLZ : KtxSupercompression.Zstd, info.Supercompression);
            Assert.True(info.IsSrgb);
            var dec = Ktx.Decode(k);
            Assert.Equal((w, h, 4), (dec.Width, dec.Height, dec.Channels));
            double p = PsnrRgb(KtxWriter.ToRgba8(src), dec.GetData<byte>());
            Assert.True(p > (c == TextureCompression.Etc1s ? 18 : 28), $"{c} {effort}: {p:F2} dB");   // loose sanity bounds (noisy content)
            if (!alpha) Assert.All(Enumerable.Range(0, w * h), i => Assert.Equal(255, dec.GetData<byte>()[i * 4 + 3]));
            else
            {
                double ae = 0;
                var s = src.GetData<byte>(); var o = dec.GetData<byte>();
                for (int i = 0; i < w * h; i++) ae += Math.Abs(s[i * 4 + 3] - o[i * 4 + 3]);
                Assert.True(ae / (w * h) < (c == TextureCompression.Etc1s ? 16 : 8), $"alpha error {ae / (w * h):F2}");
            }
        }
    }

    /// <summary>Data format descriptor: model 163 with one or two (alpha) samples, model 166 with channel RGB / RGBA.</summary>
    [Fact]
    public void DescriptorChannels()
    {
        foreach (var c in new[] { TextureCompression.Etc1s, TextureCompression.Uastc })
            foreach (bool alpha in new[] { false, true })
            {
                var k = Ktx.Encode(Photo(16, 8, alpha, 1), new KtxEncodeOptions { Compression = c, Srgb = false });
                int dfd = (int)BinaryPrimitives.ReadUInt32LittleEndian(k.AsSpan(48));
                int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(k.AsSpan(dfd));
                Assert.Equal(c == TextureCompression.Etc1s ? 163 : 166, k[dfd + 12]);
                Assert.Equal(1, k[dfd + 14]);   // linear
                if (c == TextureCompression.Etc1s)
                {
                    Assert.Equal(alpha ? 60 : 44, len);
                    if (alpha) Assert.Equal(15, k[dfd + 28 + 16 + 3]);   // AAA
                }
                else Assert.Equal(alpha ? 3 : 0, k[dfd + 28 + 3] & 15);
                Assert.Equal(TransferFunction.Linear, Ktx.GetTextureInfo(k).Transfer);
            }
    }

    [Fact]
    public void ChainsArraysCubes()
    {
        foreach (var c in new[] { TextureCompression.Etc1s, TextureCompression.Uastc })
        {
            var o = new KtxEncodeOptions { Compression = c, GenerateMipmaps = true };
            var mips = Ktx.Encode(Photo(45, 27, true, 2), o);
            KtxTests.Validate(mips);
            var t = Ktx.DecodeTexture(mips);
            Assert.Equal(6, t.MipLevels);
            Assert.Equal((1, 1), (t.GetImage(5).Width, t.GetImage(5).Height));

            var arr = Ktx.EncodeArray(new[] { new[] { Photo(20, 12, false, 1) }, new[] { Photo(20, 12, false, 2) }, new[] { Photo(20, 12, false, 3) } }, o);
            KtxTests.Validate(arr);
            t = Ktx.DecodeTexture(arr);
            Assert.Equal((3, 5, true), (t.Layers, t.MipLevels, t.IsArray));
            Assert.True(PsnrRgb(KtxWriter.ToRgba8(Photo(20, 12, false, 3)), t.GetImage(0, 2).GetData<byte>()) > 22);

            var faces = Enumerable.Range(0, 6).Select(i => (IReadOnlyList<RawImage>)new[] { Photo(8, 8, false, i) }).ToList();
            var cube = Ktx.EncodeCube(faces, new KtxEncodeOptions { Compression = c });
            KtxTests.Validate(cube);
            t = Ktx.DecodeTexture(cube);
            Assert.Equal((6, KtxTextureKind.CubeMap), (t.Faces, t.Kind));
        }
    }

    [Fact]
    public void UastcWithoutSupercompression()
    {
        var src = Photo(24, 20, true, 4);
        var plain = Ktx.Encode(src, new KtxEncodeOptions { Compression = TextureCompression.Uastc, Supercompression = KtxSupercompression.None });
        var z = Ktx.Encode(src, new KtxEncodeOptions { Compression = TextureCompression.Uastc });
        KtxTests.Validate(plain);
        Assert.Equal(KtxSupercompression.None, Ktx.GetTextureInfo(plain).Supercompression);
        Assert.Equal(Ktx.Decode(z).GetBytes().ToArray(), Ktx.Decode(plain).GetBytes().ToArray());
    }

    /// <summary>Gray, 16 bit and float images go through the 8 bit RGB(A) conversion.</summary>
    [Fact]
    public void OtherSampleTypes()
    {
        var gray = KtxTests.Pattern(33, 17, ColorModel.Gray, SampleType.UInt8, 1);
        foreach (var c in new[] { TextureCompression.Etc1s, TextureCompression.Uastc })
        {
            var d = Ktx.Decode(Ktx.Encode(gray, new KtxEncodeOptions { Compression = c })).GetData<byte>();
            for (int i = 0; i < 33 * 17; i++) Assert.True(d[i * 4] == d[i * 4 + 1] && d[i * 4] == d[i * 4 + 2]);
            var u16 = KtxTests.Pattern(16, 16, ColorModel.Rgba, SampleType.UInt16, 2);
            Assert.Equal(16, Ktx.Decode(Ktx.Encode(u16, new KtxEncodeOptions { Compression = c })).Width);
            var f = KtxTests.Pattern(9, 7, ColorModel.Rgb, SampleType.Float32, 3);
            var k = Ktx.Encode(f, new KtxEncodeOptions { Compression = c });
            Assert.False(Ktx.GetTextureInfo(k).IsSrgb);
        }
    }

    [Fact]
    public void Etc1sQualityControlsSize()
    {
        var src = Photo(96, 64, false, 5);
        var low = Ktx.Encode(src, new KtxEncodeOptions { Compression = TextureCompression.Etc1s, Etc1sQuality = 16 });
        var high = Ktx.Encode(src, new KtxEncodeOptions { Compression = TextureCompression.Etc1s, Etc1sQuality = 255 });
        Assert.True(low.Length < high.Length);
        double pl = PsnrRgb(KtxWriter.ToRgba8(src), Ktx.Decode(low).GetData<byte>());
        double ph = PsnrRgb(KtxWriter.ToRgba8(src), Ktx.Decode(high).GetData<byte>());
        Assert.True(ph > pl + 1, $"{pl:F2} / {ph:F2}");
        // explicit codebook sizes
        var k = Ktx.Encode(src, new KtxEncodeOptions { Compression = TextureCompression.Etc1s, Etc1sMaxEndpoints = 5, Etc1sMaxSelectors = 7 });
        var sgd = (int)BinaryPrimitives.ReadUInt64LittleEndian(k.AsSpan(64));
        Assert.True(BinaryPrimitives.ReadUInt16LittleEndian(k.AsSpan(sgd)) <= 5);
        Assert.True(BinaryPrimitives.ReadUInt16LittleEndian(k.AsSpan(sgd + 2)) <= 7);
        Assert.Equal(96, Ktx.Decode(k).Width);
    }

    // ------------------------------------------------------------------ UASTC blocks

    /// <summary>
    /// Every encoded block decodes to exactly the error the encoder fitted (so the packing matches the decoder), and a
    /// spread of block contents reaches every UASTC mode.
    /// </summary>
    [Fact]
    public void UastcBlocksDecodeAsFitted()
    {
        var rnd = new Random(7);
        var seen = new HashSet<int>();
        var px = new byte[64]; var blk = new byte[16]; var dec = new byte[64];
        for (int n = 0; n < 6000; n++)
        {
            int kind = n % 8;
            int a0 = rnd.Next(256), a1 = rnd.Next(256);
            for (int t = 0; t < 16; t++)
            {
                int x = t & 3, y = t >> 2;
                double f = (x + y * (kind % 3)) / 9.0;
                for (int c = 0; c < 3; c++)
                {
                    int v = kind switch
                    {
                        0 => rnd.Next(256),                                              // noise
                        1 => (int)(a0 + (a1 - a0) * f) + rnd.Next(-2, 3) * c,            // gradient
                        2 => (x < 2 ^ y < 2) ? a0 + c * 20 : a1 - c * 15,                // two colours
                        3 => (x + y) % 3 == 0 ? a0 : (x + y) % 3 == 1 ? a1 : (a0 + a1) / 2 + c * 30, // three regions
                        _ => (int)(a0 + (a1 - a0) * f),
                    };
                    px[t * 4 + c] = (byte)Math.Clamp(v, 0, 255);
                }
                if (kind >= 5) { px[t * 4 + 1] = px[t * 4 + 2] = px[t * 4]; }  // gray
                px[t * 4 + 3] = kind switch { 4 or 6 => (byte)Math.Clamp(255 - (int)(f * a1), 0, 255), 7 => (byte)rnd.Next(256), 3 => (byte)(x < 2 ? 255 : 90), _ => 255 };
            }
            if (n % 97 == 0) for (int t = 1; t < 16; t++) Array.Copy(px, 0, px, t * 4, 4);   // solid
            int effort = n % 3;
            UastcEncoder.LastError = -1;
            UastcEncoder.EncodeBlock(px, blk, effort);
            BasisUastc.DecodeBlock(blk, dec);
            long e = 0;
            for (int i = 0; i < 64; i++) { int d = px[i] - dec[i]; e += d * d; }
            int mode = UastcMode(blk[0]);
            seen.Add(mode);
            if (mode == 8) Assert.Equal(0, e);
            else Assert.Equal(UastcEncoder.LastError, e);
        }
        var missing = Enumerable.Range(0, 19).Where(m => !seen.Contains(m)).ToArray();
        Assert.True(missing.Length == 0, "UASTC modes never chosen: " + string.Join(", ", missing));
    }

    static int UastcMode(byte b)
    {
        (int code, int len)[] codes =
        {
            (0x1, 4), (0x35, 6), (0x1D, 5), (0x3, 5), (0x13, 5), (0xB, 5), (0x1B, 5), (0x7, 5), (0x17, 5), (0xF, 5),
            (0x2, 3), (0x0, 2), (0x6, 3), (0x1F, 5), (0xD, 5), (0x5, 7), (0x15, 6), (0x25, 6), (0x9, 4),
        };
        for (int m = 0; m < codes.Length; m++)
            if ((b & ((1 << codes[m].len) - 1)) == codes[m].code) return m;
        return -1;
    }

    // ------------------------------------------------------------------ basisu at test time

    static string? Basisu()
    {
        var env = Environment.GetEnvironmentVariable("PIXIMAGEIO_BASISU");
        if (!string.IsNullOrEmpty(env)) return File.Exists(env) ? env : null;
        return MiscFormatTests.ToolAvailable("basisu") ? "basisu" : null;
    }

    /// <summary>When basisu is available: it decodes freshly encoded textures (every effort, alpha, mips) like our decoder.</summary>
    [Fact]
    public void BasisuDecodesFreshEncodes()
    {
        var tool = Basisu();
        if (tool == null) return;
        var cases = new List<(string name, byte[] data)>();
        foreach (var c in new[] { TextureCompression.Etc1s, TextureCompression.Uastc })
            foreach (var q in new[] { BcQuality.Fast, BcQuality.Normal, BcQuality.High })
            {
                cases.Add(($"{c}_{q}_rgb", Ktx.Encode(Photo(130, 70, false, 8), new KtxEncodeOptions { Compression = c, Quality = q, Etc1sQuality = 40 + 90 * (int)q })));
                cases.Add(($"{c}_{q}_rgba_mips", Ktx.Encode(Photo(67, 45, true, 9), new KtxEncodeOptions { Compression = c, Quality = q, GenerateMipmaps = true })));
            }
        var dir = Path.Combine(Path.GetTempPath(), "pixcodec_basisu_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var (name, data) in cases)
            {
                var file = Path.Combine(dir, name + ".ktx2");
                File.WriteAllBytes(file, data);
                var od = Path.Combine(dir, name);
                Directory.CreateDirectory(od);
                MiscFormatTests.RunTool(tool, $"-unpack \"{file}\" -no_ktx -output_path \"{od}\"");
                var t = Ktx.DecodeTexture(data);
                int n = 0;
                foreach (var png in Directory.GetFiles(od, "*_unpacked_rgba_RGBA32_level_*.png"))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(png, @"_level_(\d+)_face_(\d+)_layer(\d+)\.png$");
                    int level = int.Parse(m.Groups[1].Value), face = int.Parse(m.Groups[2].Value), layer = int.Parse(m.Groups[3].Value);
                    var r = ImageCodec.Decode(File.ReadAllBytes(png));
                    var rgba = new RawImage(r.Width, r.Height, ColorModel.Rgba, KtxWriter.ToRgba8(r), 4);
                    AssertSame(rgba, t.GetImage(level, layer, face), $"{name} level {level}");
                    n++;
                }
                Assert.Equal(t.MipLevels * t.Layers * t.Faces, n);
            }
        }
        finally { Directory.Delete(dir, true); }
    }
}
