using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;
using static Aardvark.ImageCodecs.Tests.TestUtil;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>
/// A fact that needs ARM astcenc (run only; $ASTCENC or on the PATH, see <see cref="AstcFileTests.Astcenc"/>): skipped
/// without it, failing then under PIXCODEC_REQUIRE_ORACLES=1.
/// </summary>
[XunitTestCaseDiscoverer("Aardvark.ImageCodecs.Tests.OracleFactDiscoverer", "Aardvark.PixImage.PixCodec.Codecs.Tests")]
public sealed class AstcencFactAttribute : FactAttribute, IOracleAttribute
{
    public AstcencFactAttribute()
    {
        Missing = AstcFileTests.Astcenc() == null ? "set ASTCENC to an astcenc binary (or put astcenc on the PATH) to run" : null;
        if (!Oracles.Enforced(Gate)) Skip = Missing;
    }
    public string Gate => "astcenc";
    public string? Missing { get; }
}

/// <summary>
/// The ASTC block encoder (<see cref="AstcEncoder"/>), checked through the specification exact decoder
/// (<see cref="AstcBlocks"/>). Quality gates come from ARM astcenc's encodings of the same images
/// (tests/fixtures/astc_encode, see tests/oracle/gen_astc_encode.py): our encoding must be about as good as astcenc's
/// -fast / -medium / -thorough one at Fast / Normal / High, both decoded by the same decoder.
/// </summary>
public class AstcEncodeTests
{
    readonly ITestOutputHelper _out;
    public AstcEncodeTests(ITestOutputHelper output) => _out = output;

    // ------------------------------------------------------------------ helpers

    static (byte[] px, int w, int h) Ldr(string name)
    {
        var img = LoadRef(Fixture("astc_encode/" + name + ".ref"));
        return (img.GetData<byte>(), img.Width, img.Height);
    }

    static (ushort[] px, int w, int h) Hdr()
    {
        var img = LoadRef(Fixture("astc_encode/photo_hdr.ref"));
        var h = img.GetData<Half>();
        var o = new ushort[h.Length];
        for (int i = 0; i < h.Length; i++) o[i] = BitConverter.HalfToUInt16Bits(h[i]);
        return (o, img.Width, img.Height);
    }

    /// <summary>The blocks of an astcenc .astc file of the fixtures.</summary>
    static byte[] Oracle(string variant, int bw, int bh, string preset)
        => File.ReadAllBytes(Fixture($"astc_encode/{variant}_{bw}x{bh}_{preset}.astc"))[16..];

    static string Preset(BcQuality q) => q switch { BcQuality.Fast => "fast", BcQuality.Normal => "medium", _ => "thorough" };

    static byte[] DecodeUnorm8(byte[] blocks, int w, int h, int bw, int bh, bool srgb)
    {
        int bx = (w + bw - 1) / bw, by = (h + bh - 1) / bh;
        var o = new byte[w * h * 4];
        var t = new byte[bw * bh * 4];
        for (int y = 0; y < by; y++)
            for (int x = 0; x < bx; x++)
            {
                AstcBlocks.DecodeUnorm8(blocks.AsSpan((y * bx + x) * 16, 16), bw, bh, srgb, t);
                for (int j = 0; j < bh; j++)
                    for (int i = 0; i < bw; i++)
                    {
                        int px = x * bw + i, py = y * bh + j;
                        if (px < w && py < h) t.AsSpan((j * bw + i) * 4, 4).CopyTo(o.AsSpan((py * w + px) * 4));
                    }
            }
        return o;
    }

    static ushort[] DecodeFloat16(byte[] blocks, int w, int h, int bw, int bh)
    {
        int bx = (w + bw - 1) / bw, by = (h + bh - 1) / bh;
        var o = new ushort[w * h * 4];
        var t = new ushort[bw * bh * 4];
        for (int y = 0; y < by; y++)
            for (int x = 0; x < bx; x++)
            {
                AstcBlocks.DecodeFloat16(blocks.AsSpan((y * bx + x) * 16, 16), bw, bh, t);
                for (int j = 0; j < bh; j++)
                    for (int i = 0; i < bw; i++)
                    {
                        int px = x * bw + i, py = y * bh + j;
                        if (px < w && py < h) t.AsSpan((j * bw + i) * 4, 4).CopyTo(o.AsSpan((py * w + px) * 4));
                    }
            }
        return o;
    }

    static double Psnr8(byte[] a, byte[] b, int channels)
    {
        double se = 0;
        long n = 0;
        for (int i = 0; i < a.Length; i += 4)
            for (int c = 0; c < channels; c++) { int d = a[i + c] - b[i + c]; se += d * d; n++; }
        return se == 0 ? 99 : 10 * Math.Log10(255.0 * 255 * n / se);
    }

    /// <summary>RMS of log2(decoded / original) over RGB, both floored at 2^-14.</summary>
    static double LogRms(ushort[] a, ushort[] b)
    {
        double se = 0;
        long n = 0;
        for (int i = 0; i < a.Length; i += 4)
            for (int c = 0; c < 3; c++)
            {
                double x = Math.Max((double)BitConverter.UInt16BitsToHalf(a[i + c]), 1.0 / 16384);
                double y = Math.Max((double)BitConverter.UInt16BitsToHalf(b[i + c]), 1.0 / 16384);
                double d = Math.Log2(y / x);
                se += d * d; n++;
            }
        return Math.Sqrt(se / n);
    }

    // ------------------------------------------------------------------ quality against astcenc

    public static IEnumerable<object[]> LdrCases()
    {
        foreach (var v in new[] { "ldr", "alpha", "srgb" })
            foreach (var (bw, bh) in new[] { (4, 4), (6, 6), (8, 8) })
                foreach (var q in new[] { BcQuality.Fast, BcQuality.Normal, BcQuality.High })
                    yield return new object[] { v, bw, bh, q };
    }

    /// <summary>
    /// PSNR (RGB, with alpha RGBA) of our encoding against astcenc's at the matching preset: at most 0.5 dB below
    /// -fast / -medium (Fast / Normal) and 0.3 dB below -thorough (High).
    /// </summary>
    [Theory, MemberData(nameof(LdrCases))]
    public void LdrQualityAgainstAstcenc(string variant, int bw, int bh, BcQuality q)
    {
        var (px, w, h) = Ldr(variant == "alpha" ? "photo_alpha" : "photo");
        bool srgb = variant == "srgb";
        int ch = variant == "alpha" ? 4 : 3;
        var ours = AstcEncoder.EncodeImageUnorm8(px, w, h, bw, bh, srgb, q);
        Assert.Equal((w + bw - 1) / bw * ((h + bh - 1) / bh) * 16, ours.Length);
        double p = Psnr8(px, DecodeUnorm8(ours, w, h, bw, bh, srgb), ch);
        double r = Psnr8(px, DecodeUnorm8(Oracle(variant, bw, bh, Preset(q)), w, h, bw, bh, srgb), ch);
        _out.WriteLine($"{variant} {bw}x{bh} {q}: {p:F3} dB, astcenc -{Preset(q)} {r:F3} dB");
        Assert.True(p >= r - (q == BcQuality.High ? 0.3 : 0.5), $"{p:F3} dB against astcenc {r:F3} dB");
    }

    public static IEnumerable<object[]> FootprintCases()
    {
        foreach (var (bw, bh) in new[] { (4, 4), (5, 4), (5, 5), (6, 5), (6, 6), (8, 5), (8, 6), (10, 5), (10, 6), (8, 8), (10, 8), (10, 10), (12, 10), (12, 12) })
            yield return new object[] { bw, bh };
    }

    /// <summary>Every 2D footprint at Normal: within 0.5 dB of astcenc -medium, and the encoder's own error equals the
    /// decoded error for every block.</summary>
    [Theory, MemberData(nameof(FootprintCases))]
    public void EveryFootprint(int bw, int bh)
    {
        var (px, w, h) = Ldr("photo");
        var ours = AstcEncoder.EncodeImageUnorm8(px, w, h, bw, bh, false, BcQuality.Normal);
        double p = Psnr8(px, DecodeUnorm8(ours, w, h, bw, bh, false), 3);
        double r = Psnr8(px, DecodeUnorm8(Oracle("ldr", bw, bh, "medium"), w, h, bw, bh, false), 3);
        _out.WriteLine($"{bw}x{bh}: {p:F3} dB, astcenc -medium {r:F3} dB");
        Assert.True(p >= r - 0.5, $"{p:F3} dB against astcenc {r:F3} dB");
    }

    /// <summary>HDR (RGB HDR, alpha LDR): RMS of log2 ratios at most 15 % above astcenc -ch at the matching preset.</summary>
    [Theory]
    [InlineData(4, 4, BcQuality.Fast)]
    [InlineData(4, 4, BcQuality.Normal)]
    [InlineData(4, 4, BcQuality.High)]
    [InlineData(6, 6, BcQuality.Normal)]
    [InlineData(8, 8, BcQuality.Fast)]
    [InlineData(8, 8, BcQuality.Normal)]
    [InlineData(8, 8, BcQuality.High)]
    public void HdrQualityAgainstAstcenc(int bw, int bh, BcQuality q)
    {
        var (px, w, h) = Hdr();
        var ours = AstcEncoder.EncodeImageFloat16(px, w, h, bw, bh, q);
        double e = LogRms(px, DecodeFloat16(ours, w, h, bw, bh));
        double r = LogRms(px, DecodeFloat16(Oracle("hdr", bw, bh, Preset(q)), w, h, bw, bh));
        _out.WriteLine($"hdr {bw}x{bh} {q}: log2 RMS {e:F4}, astcenc -{Preset(q)} {r:F4}");
        Assert.True(e <= r * 1.15 + 0.002, $"log2 RMS {e:F4} against astcenc {r:F4}");
    }

    // ------------------------------------------------------------------ legality and exactness

    static readonly (int, int)[] AllFootprints =
        { (4, 4), (5, 4), (5, 5), (6, 5), (6, 6), (8, 5), (8, 6), (10, 5), (10, 6), (8, 8), (10, 8), (10, 10), (12, 10), (12, 12) };

    /// <summary>One random block of a few kinds of content (noise, gradients, mosaics, independent alpha, grey, extremes).</summary>
    static byte[] RandomBlock(Random r, int n, int bw)
    {
        var b = new byte[n * 4];
        int kind = r.Next(9);
        var c = new byte[4 * 4];
        r.NextBytes(c);
        for (int t = 0; t < n; t++)
        {
            int x = t % bw, y = t / bw;
            for (int k = 0; k < 4; k++)
            {
                int v = kind switch
                {
                    0 => r.Next(256),                                                         // noise
                    1 => c[k] + (c[4 + k] - c[k]) * (x + y) / (bw + n / bw),                   // gradient
                    2 => c[((x * 3 + y * 5) / 4 % 4) * 4 + k],                                // mosaic of 4 colours
                    3 => k == 3 ? r.Next(256) : c[k] + (c[4 + k] - c[k]) * x / bw,             // independent alpha
                    4 => k == 3 ? 255 : c[0] + (c[4] - c[0]) * y / Math.Max(1, n / bw),        // grey
                    5 => r.Next(2) * 255,                                                     // extremes
                    6 => c[k] + r.Next(5) - 2,                                                // near constant
                    7 => k == 3 ? (x < bw / 2 ? 0 : 255) : c[(x < bw / 2 ? 0 : 4) + k],         // hard edge
                    _ => k == 3 ? 255 : c[k] + (c[4 + k] - c[k]) * x / bw + r.Next(9) - 4,      // noisy opaque gradient
                };
                b[t * 4 + k] = (byte)Math.Clamp(v, 0, 255);
            }
        }
        return b;
    }

    static long DecodedError(byte[] src, byte[] dec, bool alpha)
    {
        long e = 0;
        for (int i = 0; i < src.Length; i += 4)
            for (int k = 0; k < (alpha ? 4 : 3); k++) { int d = src[i + k] - dec[i + k]; e += d * d; }
        return e;
    }

    /// <summary>
    /// Random blocks for every footprint and quality: never the error colour, and the error the encoder computed for its
    /// choice equals the error of the decoded block exactly (its model of the decoder is bit exact).
    /// </summary>
    [Fact]
    public void LegalityFuzz()
    {
        var r = new Random(7);
        var blk = new byte[16];
        foreach (var (bw, bh) in AllFootprints)
        {
            int n = bw * bh;
            var dec = new byte[n * 4];
            for (int i = 0; i < 60; i++)
            {
                var src = RandomBlock(r, n, bw);
                var q = i % 10 == 0 ? BcQuality.High : i % 3 == 0 ? BcQuality.Normal : BcQuality.Fast;
                bool srgb = i % 4 == 1;
                AstcEncoder.EncodeUnorm8(src, bw, bh, srgb, q, blk);
                long predicted = AstcEncoder.LastErr;
                AstcBlocks.DecodeUnorm8(blk, bw, bh, srgb, dec);
                bool magenta = true, opaque = true;
                for (int t = 0; t < n; t++)
                {
                    if (dec[t * 4] != 255 || dec[t * 4 + 1] != 0 || dec[t * 4 + 2] != 255 || dec[t * 4 + 3] != 255) magenta = false;
                    if (src[t * 4 + 3] != 255) opaque = false;
                }
                bool srcMagenta = true;
                for (int t = 0; t < n; t++)
                    if (src[t * 4] != 255 || src[t * 4 + 1] != 0 || src[t * 4 + 2] != 255 || src[t * 4 + 3] != 255) srcMagenta = false;
                Assert.False(magenta && !srcMagenta, $"{bw}x{bh} block {i}: error colour");
                Assert.Equal(predicted, DecodedError(src, dec, !opaque));
                if (opaque) for (int t = 0; t < n; t++) Assert.Equal(255, dec[t * 4 + 3]);
            }
        }
    }

    /// <summary>HDR random blocks, including zero, tiny, huge, negative, infinite and NaN values: legal, finite output.</summary>
    [Fact]
    public void HdrLegalityFuzz()
    {
        var r = new Random(11);
        var blk = new byte[16];
        ushort[] specials = { 0, 1, 0x3C00, 0x7BFF, 0x7C00, 0xFC00, 0x7E00, 0x8001, 0xBC00, 0x0400, 0x03FF };
        foreach (var (bw, bh) in AllFootprints)
        {
            int n = bw * bh;
            var dec = new ushort[n * 4];
            for (int i = 0; i < 24; i++)
            {
                var src = new ushort[n * 4];
                int kind = i % 4;
                double b0 = Math.Pow(2, r.NextDouble() * 24 - 12), b1 = b0 * Math.Pow(2, r.NextDouble() * 8 - 4);
                for (int t = 0; t < n; t++)
                    for (int k = 0; k < 4; k++)
                    {
                        double f = (double)(t % bw) / bw;
                        double v = kind switch
                        {
                            0 => Math.Pow(2, r.NextDouble() * 32 - 16),
                            1 => b0 + (b1 - b0) * f * (k + 1) / 4,
                            2 => b0 * Math.Pow(b1 / b0, f),
                            _ => k == 3 ? r.NextDouble() : -1,
                        };
                        src[t * 4 + k] = v < 0 ? specials[r.Next(specials.Length)]
                            : BitConverter.HalfToUInt16Bits((Half)(k == 3 && kind < 3 ? 1.0 : v));
                    }
                var q = i % 8 == 0 ? BcQuality.High : i % 3 == 0 ? BcQuality.Normal : BcQuality.Fast;
                AstcEncoder.EncodeFloat16(src, bw, bh, q, blk);
                AstcBlocks.DecodeFloat16(blk, bw, bh, dec);
                for (int j = 0; j < dec.Length; j++)
                {
                    Assert.True((dec[j] & 0x7C00) != 0x7C00, $"{bw}x{bh} block {i}: non-finite output {dec[j]:X4}");
                    Assert.True((dec[j] & 0x8000) == 0, $"{bw}x{bh} block {i}: negative output {dec[j]:X4}");
                }
                if (kind == 2)
                    for (int t = 0; t < n; t++)   // a smooth ramp over at most 4 stops: within a factor 2 at worst
                        for (int k = 0; k < 3; k++)
                        {
                            double x = Math.Max((double)BitConverter.UInt16BitsToHalf(src[t * 4 + k]), 1e-4);
                            double y = Math.Max((double)BitConverter.UInt16BitsToHalf(dec[t * 4 + k]), 1e-4);
                            Assert.True(Math.Abs(Math.Log2(y / x)) < 1, $"{bw}x{bh} block {i} texel {t}: {x} -> {y}");
                        }
            }
        }
    }

    /// <summary>
    /// Regression: HDR RGBA content whose channels run independently down to 0 (37 x 21, partial edge blocks). The
    /// logarithmic fit once chose endpoints far above the data, decoding values up to 45 from data within 0..2.5. The
    /// decoded values must stay in a sane range (at most about one stop above the brightest input) and the RGB error
    /// bounded. For reference astcenc -ch on the same pattern (numpy noise instead of System.Random): RMSE 0.29 / 0.22
    /// at 4x4 -fast / -medium, 0.59 / 0.75 at 8x8, with maxima of 2.1 to 17.
    /// </summary>
    [Theory]
    [InlineData(4, BcQuality.Fast, 0.35)]
    [InlineData(4, BcQuality.Normal, 0.35)]
    [InlineData(6, BcQuality.Normal, 0.6)]
    [InlineData(8, BcQuality.Fast, 0.9)]
    [InlineData(8, BcQuality.Normal, 0.9)]
    [InlineData(8, BcQuality.High, 0.9)]
    [InlineData(12, BcQuality.Normal, 1.0)]
    public void HdrIndependentChannelsStaySane(int bs, BcQuality q, double maxRmse)
    {
        int w = 37, h = 21, seed = 7;
        var r = new Random(seed);
        var px = new ushort[w * h * 4];
        float max = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int k = 0; k < 4; k++)
                {
                    double v = 0.5 + 0.4 * Math.Sin(x * 0.4 + k) * Math.Cos(y * 0.3 + seed) + 0.02 * r.NextDouble();
                    float f = Math.Max(0, (float)(v * 3 - 0.5));
                    px[(y * w + x) * 4 + k] = BitConverter.HalfToUInt16Bits((Half)f);
                    if (k < 3) max = Math.Max(max, f);
                }
        var dec = DecodeFloat16(AstcEncoder.EncodeImageFloat16(px, w, h, bs, bs, q), w, h, bs, bs);
        double se = 0, worst = 0;
        for (int i = 0; i < w * h; i++)
            for (int k = 0; k < 3; k++)
            {
                double a = (double)BitConverter.UInt16BitsToHalf(px[i * 4 + k]), b = (double)BitConverter.UInt16BitsToHalf(dec[i * 4 + k]);
                Assert.True(b >= 0 && b <= 2.2 * max, $"texel {i} channel {k}: {a} decoded as {b}");
                se += (a - b) * (a - b);
                worst = Math.Max(worst, Math.Abs(a - b));
            }
        double rmse = Math.Sqrt(se / (w * h * 3));
        _out.WriteLine($"{bs}x{bs} {q}: rmse {rmse:F3}, max error {worst:F2}");
        Assert.True(rmse <= maxRmse, $"rmse {rmse:F3}");
    }

    /// <summary>Constant blocks decode exactly (void extent), LDR, sRGB and HDR.</summary>
    [Fact]
    public void ConstantBlocksExact()
    {
        var r = new Random(3);
        var blk = new byte[16];
        foreach (var (bw, bh) in AllFootprints)
        {
            int n = bw * bh;
            var c = new byte[4];
            r.NextBytes(c);
            var src = new byte[n * 4];
            for (int t = 0; t < n; t++) c.CopyTo(src, t * 4);
            var dec = new byte[n * 4];
            foreach (bool srgb in new[] { false, true })
            {
                AstcEncoder.EncodeUnorm8(src, bw, bh, srgb, BcQuality.Fast, blk);
                AstcBlocks.DecodeUnorm8(blk, bw, bh, srgb, dec);
                Assert.Equal(src, dec);
            }
            var hs = new ushort[n * 4];
            ushort[] hv = { BitConverter.HalfToUInt16Bits((Half)(r.NextDouble() * 1000)), 0x3555, 0, 0x3C00 };
            for (int t = 0; t < n; t++) hv.CopyTo(hs, t * 4);
            var hd = new ushort[n * 4];
            AstcEncoder.EncodeFloat16(hs, bw, bh, BcQuality.Fast, blk);
            AstcBlocks.DecodeFloat16(blk, bw, bh, hd);
            Assert.Equal(hs, hd);
        }
    }

    /// <summary>Opaque grey and opaque colour content keeps alpha exactly 255; grey stays grey.</summary>
    [Fact]
    public void GreyAndOpaque()
    {
        var (px, w, h) = Ldr("photo");
        var grey = (byte[])px.Clone();
        for (int i = 0; i < grey.Length; i += 4) grey[i + 1] = grey[i + 2] = grey[i];
        foreach (var q in new[] { BcQuality.Fast, BcQuality.Normal })
        {
            var dec = DecodeUnorm8(AstcEncoder.EncodeImageUnorm8(grey, w, h, 6, 6, false, q), w, h, 6, 6, false);
            for (int i = 0; i < dec.Length; i += 4)
            {
                Assert.Equal(255, dec[i + 3]);
                Assert.True(dec[i] == dec[i + 1] && dec[i] == dec[i + 2]);
            }
            Assert.True(Psnr8(grey, dec, 3) > 30);
        }
    }

    /// <summary>The same input gives the same blocks, sequentially and in parallel.</summary>
    [Fact]
    public void Deterministic()
    {
        var (px, w, h) = Ldr("photo_alpha");
        foreach (var q in new[] { BcQuality.Fast, BcQuality.Normal, BcQuality.High })
        {
            var a = AstcEncoder.EncodeImageUnorm8(px, w, h, 5, 5, false, q, parallel: true);
            var b = AstcEncoder.EncodeImageUnorm8(px, w, h, 5, 5, false, q, parallel: false);
            Assert.Equal(a, b);
        }
        var (hp, hw, hh) = Hdr();
        Assert.Equal(AstcEncoder.EncodeImageFloat16(hp, hw, hh, 6, 6, BcQuality.Normal, parallel: true),
                     AstcEncoder.EncodeImageFloat16(hp, hw, hh, 6, 6, BcQuality.Normal, parallel: false));
    }

    /// <summary>Image sizes that are not multiples of the footprint: the edge blocks repeat the last row / column.</summary>
    [Fact]
    public void PartialEdgeBlocks()
    {
        var (px, w, h) = Ldr("photo");
        int cw = 37, chh = 23;
        var crop = new byte[cw * chh * 4];
        for (int y = 0; y < chh; y++) Array.Copy(px, y * w * 4, crop, y * cw * 4, cw * 4);
        var blocks = AstcEncoder.EncodeImageUnorm8(crop, cw, chh, 10, 6, false, BcQuality.Normal);
        Assert.Equal(4 * 4 * 16, blocks.Length);
        Assert.True(Psnr8(crop, DecodeUnorm8(blocks, cw, chh, 10, 6, false), 3) > 25);
    }

    // ------------------------------------------------------------------ astcenc decodes our blocks like our decoder

    static void WriteAstc(string path, byte[] blocks, int bw, int bh, int w, int h)
    {
        var hdr = new byte[16];
        BitConverter.GetBytes(0x5CA1AB13u).CopyTo(hdr, 0);
        hdr[4] = (byte)bw; hdr[5] = (byte)bh; hdr[6] = 1;
        hdr[7] = (byte)w; hdr[8] = (byte)(w >> 8); hdr[9] = (byte)(w >> 16);
        hdr[10] = (byte)h; hdr[11] = (byte)(h >> 8); hdr[12] = (byte)(h >> 16);
        hdr[13] = 1;
        File.WriteAllBytes(path, hdr.Concat(blocks).ToArray());
    }

    static void RunAstcenc(params string[] args)
    {
        var psi = new ProcessStartInfo(AstcFileTests.Astcenc()!) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("-silent");
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd());
    }

    /// <summary>
    /// Our blocks (LDR, alpha and HDR, three footprints, all qualities) written as .astc files and decoded by astcenc
    /// (-dl to KTX, -dh to KTX float16): identical to our decoder's output.
    /// </summary>
    [AstcencFact]
    public void AstcencDecodesOurBlocksIdentically()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pixcodec-astc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var (bw, bh) in new[] { (4, 4), (6, 5), (12, 12) })
                foreach (var q in new[] { BcQuality.Fast, BcQuality.Normal, BcQuality.High })
                {
                    foreach (var name in new[] { "photo", "photo_alpha" })
                    {
                        var (px, w, h) = Ldr(name);
                        var blocks = AstcEncoder.EncodeImageUnorm8(px, w, h, bw, bh, false, q);
                        string a = Path.Combine(dir, "t.astc"), k = Path.Combine(dir, "t.ktx");
                        WriteAstc(a, blocks, bw, bh, w, h);
                        RunAstcenc("-dl", a, k);
                        var theirs = ReadKtx(k, out int kw, out int kh);
                        Assert.Equal((w, h), (kw, kh));
                        Assert.Equal(DecodeUnorm8(blocks, w, h, bw, bh, false), (byte[])theirs);
                    }
                    var (hp, hw, hh) = Hdr();
                    var hb = AstcEncoder.EncodeImageFloat16(hp, hw, hh, bw, bh, q);
                    string ha = Path.Combine(dir, "h.astc"), hk = Path.Combine(dir, "h.ktx");
                    WriteAstc(ha, hb, bw, bh, hw, hh);
                    RunAstcenc("-dh", ha, hk);
                    var ht = (ushort[])ReadKtx(hk, out _, out _);
                    Assert.Equal(DecodeFloat16(hb, hw, hh, bw, bh), ht);
                }
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>astcenc's KTX 1 output (uint8 or float16, RGB or RGBA; alpha filled in as opaque).</summary>
    static Array ReadKtx(string path, out int w, out int h)
    {
        var b = File.ReadAllBytes(path);
        uint glType = BitConverter.ToUInt32(b, 16), fmt = BitConverter.ToUInt32(b, 24);
        w = BitConverter.ToInt32(b, 36); h = BitConverter.ToInt32(b, 40);
        int kv = BitConverter.ToInt32(b, 60), o = 64 + kv + 4;
        int ch = fmt == 0x1908 ? 4 : 3, n = w * h;
        if (glType == 0x1401)
        {
            var r = new byte[n * 4];
            for (int i = 0; i < n; i++) for (int c = 0; c < 4; c++) r[i * 4 + c] = c < ch ? b[o + i * ch + c] : (byte)255;
            return r;
        }
        var u = new ushort[n * 4];
        for (int i = 0; i < n; i++)
            for (int c = 0; c < 4; c++) u[i * 4 + c] = c < ch ? BitConverter.ToUInt16(b, o + (i * ch + c) * 2) : (ushort)0x3C00;
        return u;
    }
}
