using PixCodec;
using Xunit;
using Xunit.Abstractions;
using static PixCodec.Tests.TestUtil;

namespace PixCodec.Tests;

public class ImageOpsTests
{
    readonly ITestOutputHelper output;
    public ImageOpsTests(ITestOutputHelper output) => this.output = output;

    static string Dir => Path.Combine(FixtureDir, "resize");

    static readonly Dictionary<string, ResampleFilter> Filters = new()
    {
        ["nearest"] = ResampleFilter.Nearest, ["box"] = ResampleFilter.Box, ["bilinear"] = ResampleFilter.Bilinear,
        ["hamming"] = ResampleFilter.Hamming, ["bicubic"] = ResampleFilter.Bicubic, ["lanczos3"] = ResampleFilter.Lanczos3,
    };

    public static IEnumerable<object[]> PillowCases(string prefix)
    {
        foreach (var f in Directory.GetFiles(Path.Combine(FixtureDir, "resize"), $"rs_{prefix}_*.ref").OrderBy(f => f, StringComparer.Ordinal))
            yield return new object[] { Path.GetFileNameWithoutExtension(f) };
    }

    static (int w, int h, ResampleFilter f) Parse(string name)
    {
        var p = name.Split('_');
        var wh = p[2].Split('x');
        return (int.Parse(wh[0]), int.Parse(wh[1]), Filters[p[3]]);
    }

    // Pillow computes 'F' images in double precision without rounding: the strict check of sampling positions,
    // filter widening, edge renormalisation and kernels
    [Theory, MemberData(nameof(PillowCases), "f32")]
    public void ResizeMatchesPillowFloat(string name)
    {
        var (w, h, f) = Parse(name);
        var src = LoadRef(Path.Combine(Dir, "in_f32.ref"));
        var got = ImageOps.Resize(src, w, h, f);
        var exp = LoadRef(Path.Combine(Dir, name + ".ref"));
        double d = MaxDiff(exp, got);
        Assert.True(d < 2e-5, $"{name}: max diff {d}");
    }

    // 8 bit: Pillow uses fixed point weights and rounds and clips to 0..255 between its two passes. For filters without
    // negative lobes that costs at most a code; filters that overshoot (Hamming, bicubic, Lanczos) lose their undershoot
    // after the first pass there, which changes the second pass near hard edges by a few codes.
    static int Tolerance8(ResampleFilter f) => f.IsNearest ? 0 : f == ResampleFilter.Box || f == ResampleFilter.Bilinear ? 1 : 8;

    [Theory, MemberData(nameof(PillowCases), "rgb8")]
    public void ResizeMatchesPillowRgb8(string name)
    {
        var (w, h, f) = Parse(name);
        var src = LoadRef(Path.Combine(Dir, "in_rgb8.ref"));
        var got = ImageOps.Resize(src, w, h, f);
        var exp = LoadRef(Path.Combine(Dir, name + ".ref"));
        double d = MaxDiff(exp, got);
        Assert.True(d <= Tolerance8(f), $"{name}: max diff {d}");
    }

    // RGBA: Pillow also filters premultiplied; colour is compared where the result is not nearly transparent
    // (dividing by a small alpha magnifies Pillow's 8 bit rounding of the premultiplied values)
    [Theory, MemberData(nameof(PillowCases), "rgba8")]
    public void ResizeMatchesPillowRgba8(string name)
    {
        var (w, h, f) = Parse(name);
        var src = LoadRef(Path.Combine(Dir, "in_rgba8.ref"));
        var got = (byte[])ImageOps.Resize(src, w, h, f).Data;
        var exp = (byte[])LoadRef(Path.Combine(Dir, name + ".ref")).Data;
        // Pillow keeps the premultiplied colour as 8 bit integers: dividing by alpha again magnifies that rounding by 255 / alpha
        int worstA = 0;
        double worstC = 0;
        for (int i = 0; i < exp.Length; i += 4)
        {
            worstA = Math.Max(worstA, Math.Abs(exp[i + 3] - got[i + 3]));
            if (exp[i + 3] < 64) continue;
            double slack = 2 * 255.0 / exp[i + 3]; // rounded on premultiplying and again with the rounded alpha
            for (int c = 0; c < 3; c++) worstC = Math.Max(worstC, Math.Abs(exp[i + c] - got[i + c]) - slack);
        }
        // (alpha has hard 0 / 60+ steps, where the clipped undershoot of the sharp filters matters most)
        int tol = Tolerance8(f);
        Assert.True(worstA <= (tol == 8 ? 20 : tol) && worstC <= (tol == 8 ? 10 : tol + 1), $"{name}: alpha {worstA}, colour {worstC}");
    }

    [Theory]
    [InlineData("constant", BorderMode.Constant)]
    [InlineData("clamp", BorderMode.Clamp)]
    [InlineData("wrap", BorderMode.Wrap)]
    [InlineData("mirror", BorderMode.Mirror)]
    [InlineData("mirrorrepeat", BorderMode.MirrorRepeat)]
    public void RemapMatchesScipy(string mode, BorderMode border)
    {
        var src = LoadRef(Path.Combine(Dir, "in_rm.ref"));
        var map = LoadRef(Path.Combine(Dir, "map_rm.ref"));
        var m = (float[])map.Data;
        var mx = new float[map.Width * map.Height];
        var my = new float[mx.Length];
        for (int i = 0; i < mx.Length; i++) { mx[i] = m[2 * i]; my[i] = m[2 * i + 1]; }
        var got = ImageOps.Remap(src, map.Width, map.Height, mx, my,
            new WarpOptions { Filter = ResampleFilter.Bilinear, Border = border, BorderValue = new[] { 0.25 } });
        var exp = LoadRef(Path.Combine(Dir, $"rm_rm_{mode}.ref"));
        double d = MaxDiff(exp, got);
        Assert.True(d < 1e-5, $"{mode}: max diff {d}");
    }

    #region Direct reference implementation

    // straight from the definition, in double, one output sample at a time
    static double[] Reference(double[] src, int sw, int sh, int nc, int dw, int dh, ResampleFilter f, int alpha)
    {
        double[] W(int sn, int dn, int j, out int lo)
        {
            double ratio = (double)sn / dn, c = (j + 0.5) * ratio;
            if (f.IsNearest) { lo = Math.Min((int)Math.Floor(c), sn - 1); return new[] { 1.0 }; }
            double fs = Math.Max(1, ratio);
            var w = new double[sn];
            double sum = 0;
            for (int i = 0; i < sn; i++) { w[i] = f.Evaluate((i + 0.5 - c) / fs); sum += w[i]; }
            for (int i = 0; i < sn; i++) w[i] /= sum;
            lo = 0;
            return w;
        }
        var r = new double[dw * dh * nc];
        for (int y = 0; y < dh; y++)
        {
            var wy = W(sh, dh, y, out int ly);
            for (int x = 0; x < dw; x++)
            {
                var wx = W(sw, dw, x, out int lx);
                var acc = new double[nc];
                for (int ty = 0; ty < wy.Length; ty++)
                    for (int tx = 0; tx < wx.Length; tx++)
                    {
                        double w = wy[ty] * wx[tx];
                        if (w == 0) continue;
                        int p = ((ly + ty) * sw + lx + tx) * nc;
                        double a = alpha >= 0 ? src[p + alpha] : 1;
                        for (int c = 0; c < nc; c++) acc[c] += w * (c == alpha ? a : src[p + c] * a);
                    }
                for (int c = 0; c < nc; c++)
                {
                    double v = acc[c];
                    if (alpha >= 0 && c != alpha) v = acc[alpha] > 0 ? v / acc[alpha] : 0;
                    r[(y * dw + x) * nc + c] = v;
                }
            }
        }
        return r;
    }

    static RawImage Random(int w, int h, int nc, SampleType t, int seed)
    {
        // alpha (last channel of 2 and 4 channel images) is never negative
        bool A(int i) => (nc == 2 || nc == 4) && i % nc == nc - 1;
        var rng = new Random(seed);
        var img = RawImage.Create(w, h, RawImage.DefaultColorModel(nc), t, nc);
        int n = w * h * nc;
        switch (t)
        {
            case SampleType.UInt8: { var a = (byte[])img.Data; for (int i = 0; i < n; i++) a[i] = (byte)rng.Next(256); break; }
            case SampleType.Int8: { var a = (sbyte[])img.Data; for (int i = 0; i < n; i++) a[i] = (sbyte)rng.Next(A(i) ? 0 : -128, 128); break; }
            case SampleType.UInt16: { var a = (ushort[])img.Data; for (int i = 0; i < n; i++) a[i] = (ushort)rng.Next(65536); break; }
            case SampleType.Int16: { var a = (short[])img.Data; for (int i = 0; i < n; i++) a[i] = (short)rng.Next(A(i) ? 0 : -32768, 32768); break; }
            case SampleType.UInt32: { var a = (uint[])img.Data; for (int i = 0; i < n; i++) a[i] = (uint)rng.NextInt64(uint.MaxValue); break; }
            case SampleType.Int32: { var a = (int[])img.Data; for (int i = 0; i < n; i++) a[i] = rng.Next(A(i) ? 0 : int.MinValue, int.MaxValue); break; }
            case SampleType.UInt64: { var a = (ulong[])img.Data; for (int i = 0; i < n; i++) a[i] = (ulong)rng.NextInt64(1L << 40); break; }
            case SampleType.Int64: { var a = (long[])img.Data; for (int i = 0; i < n; i++) a[i] = rng.NextInt64(A(i) ? 0 : -(1L << 40), 1L << 40); break; }
            case SampleType.Float16: { var a = (Half[])img.Data; for (int i = 0; i < n; i++) a[i] = (Half)(rng.NextDouble() * 4 - (A(i) ? 0 : 1)); break; }
            case SampleType.Float32: { var a = (float[])img.Data; for (int i = 0; i < n; i++) a[i] = (float)(rng.NextDouble() * 4 - (A(i) ? 0 : 1)); break; }
            case SampleType.Float64: { var a = (double[])img.Data; for (int i = 0; i < n; i++) a[i] = rng.NextDouble() * 4 - (A(i) ? 0 : 1); break; }
        }
        return img;
    }

    static (double lo, double hi) Range(SampleType t) => t switch
    {
        SampleType.UInt8 => (0, 255), SampleType.Int8 => (-128, 127), SampleType.UInt16 => (0, 65535),
        SampleType.Int16 => (-32768, 32767), SampleType.UInt32 => (0, uint.MaxValue), SampleType.Int32 => (int.MinValue, int.MaxValue),
        SampleType.UInt64 => (0, ulong.MaxValue), SampleType.Int64 => (long.MinValue, long.MaxValue),
        _ => (double.NegativeInfinity, double.PositiveInfinity),
    };

    public static IEnumerable<object[]> ReferenceCases()
    {
        var types = Enum.GetValues<SampleType>();
        string[] filters = { "nearest", "box", "bilinear", "hamming", "bicubic", "lanczos3" };
        (int sw, int sh, int dw, int dh)[] sizes = { (19, 13, 7, 5), (9, 11, 23, 30), (17, 17, 17, 17), (40, 3, 13, 9), (5, 5, 1, 1), (1, 1, 4, 3) };
        int k = 0;
        foreach (var t in types)
            foreach (var s in sizes)
                foreach (var nc in new[] { 1, 2, 3, 4, 5 })
                    yield return new object[] { t, s.sw, s.sh, s.dw, s.dh, nc, filters[k++ % filters.Length] };
    }

    [Theory, MemberData(nameof(ReferenceCases))]
    public void ResizeMatchesDirectDefinition(SampleType t, int sw, int sh, int dw, int dh, int nc, string filter)
    {
        var f = Filters[filter];
        var src = Random(sw, sh, nc, t, sw * 31 + nc);
        if (nc == 5) src = new RawImage(sw, sh, ColorModel.Other, src.Data, 5);
        int alpha = nc is 2 or 4 ? nc - 1 : -1;
        var got = ImageOps.Resize(src, dw, dh, f);
        var exp = Reference(ToDoubles(src), sw, sh, nc, dw, dh, f, alpha);
        var g = ToDoubles(got);
        var (lo, hi) = Range(t);
        bool isFloat = t is SampleType.Float16 or SampleType.Float32 or SampleType.Float64;
        double scale = isFloat ? 1 : Math.Min(hi - lo, 1e15);
        // integer results are rounded (and float math is used up to 16 bit): allow 1 code plus relative float error
        double tol = t switch
        {
            SampleType.Float16 => 4e-3, SampleType.Float32 => 2e-5, SampleType.Float64 => 1e-12,
            SampleType.UInt8 or SampleType.Int8 or SampleType.UInt16 or SampleType.Int16 => 1.0,
            _ => 1 + 1e-9 * scale,
        };
        for (int i = 0; i < exp.Length; i++)
        {
            double e = isFloat ? exp[i] : Math.Clamp(exp[i], lo, hi);
            if (alpha >= 0 && i % nc != alpha)
            {
                // colour is undefined where alpha vanishes and gets imprecise where it is tiny
                double a = exp[i - i % nc + alpha];
                if (Math.Abs(a) < (isFloat ? 1e-2 : scale * 1e-2)) continue;
            }
            Assert.True(Math.Abs(e - g[i]) <= tol, $"{t} {sw}x{sh}->{dw}x{dh} c{nc} {filter}: [{i}] expected {e}, got {g[i]}");
        }
    }

    #endregion

    [Fact]
    public void IdentityResizeIsExactForInterpolatingFilters()
    {
        var src = Random(31, 17, 3, SampleType.UInt16, 1);
        foreach (var f in new[] { ResampleFilter.Nearest, ResampleFilter.Box, ResampleFilter.Bilinear, ResampleFilter.Bicubic, ResampleFilter.Lanczos3 })
            AssertEqual(src, ImageOps.Resize(src, 31, 17, f));
    }

    [Fact]
    public void BoxDownscaleIsAreaAverage()
    {
        var src = Random(64, 48, 1, SampleType.Float64, 2);
        var got = (double[])ImageOps.Resize(src, 16, 12, ResampleFilter.Box).Data;
        var s = (double[])src.Data;
        for (int y = 0; y < 12; y++)
            for (int x = 0; x < 16; x++)
            {
                double sum = 0;
                for (int j = 0; j < 4; j++) for (int i = 0; i < 4; i++) sum += s[(y * 4 + j) * 64 + x * 4 + i];
                Assert.Equal(sum / 16, got[y * 16 + x], 12);
            }
    }

    [Fact]
    public void PremultipliedAlphaKeepsHiddenColourOut()
    {
        // transparent pixels are pure red, opaque ones pure green: no red may appear
        var img = RawImage.Create(32, 32, ColorModel.Rgba, SampleType.UInt8);
        var d = (byte[])img.Data;
        for (int i = 0; i < 32 * 32; i++)
        {
            bool on = ((i % 32) / 3 + (i / 32) / 3) % 2 == 0;
            d[i * 4 + (on ? 1 : 0)] = 255;
            d[i * 4 + 3] = (byte)(on ? 255 : 0);
        }
        var r = (byte[])ImageOps.Resize(img, 11, 13, ResampleFilter.Lanczos3).Data;
        for (int i = 0; i < r.Length; i += 4)
            if (r[i + 3] > 0) Assert.True(r[i] == 0 && r[i + 1] == 255, $"pixel {i / 4}: {r[i]},{r[i + 1]},{r[i + 2]},{r[i + 3]}");
        var straight = (byte[])ImageOps.Resize(img, 11, 13, new ResizeOptions { PremultiplyAlpha = false }).Data;
        Assert.Contains(Enumerable.Range(0, straight.Length / 4), i => straight[i * 4] > 50);
    }

    [Fact]
    public void LinearLightAveragesLight()
    {
        // a 0 / 255 checkerboard averages to half the light: sRGB 188, not 128
        var img = RawImage.Create(16, 16, ColorModel.Gray, SampleType.UInt8);
        var d = (byte[])img.Data;
        for (int i = 0; i < d.Length; i++) d[i] = (byte)(((i % 16) + i / 16) % 2 == 0 ? 255 : 0);
        var lin = (byte[])ImageOps.Resize(img, 8, 8, new ResizeOptions { Filter = ResampleFilter.Box, LinearLight = true }).Data;
        var enc = (byte[])ImageOps.Resize(img, 8, 8, ResampleFilter.Box).Data;
        Assert.All(lin, v => Assert.Equal(188, v));
        Assert.All(enc, v => Assert.Equal(128, v));
        var img16 = RawImage.Create(16, 16, ColorModel.Gray, SampleType.UInt16);
        var d16 = (ushort[])img16.Data;
        for (int i = 0; i < d16.Length; i++) d16[i] = (ushort)(d[i] * 257);
        var lin16 = (ushort[])ImageOps.Resize(img16, 8, 8, new ResizeOptions { Filter = ResampleFilter.Box, LinearLight = true }).Data;
        Assert.All(lin16, v => Assert.InRange(v, 48180, 48200)); // 0.73536 * 65535
    }

    [Fact]
    public void FloatResultsAreNotClamped()
    {
        var img = RawImage.Create(8, 1, ColorModel.Gray, SampleType.Float32);
        var d = (float[])img.Data;
        for (int i = 4; i < 8; i++) d[i] = 1;
        var r = (float[])ImageOps.Resize(img, 32, 1, ResampleFilter.Lanczos3).Data;
        Assert.True(r.Max() > 1.0f && r.Min() < 0.0f, "Lanczos overshoots on a step");
        var b = RawImage.Create(8, 1, ColorModel.Gray, SampleType.UInt8);
        for (int i = 4; i < 8; i++) ((byte[])b.Data)[i] = 255;
        var rb = (byte[])ImageOps.Resize(b, 32, 1, ResampleFilter.Lanczos3).Data;
        Assert.Equal(0, rb.Min()); Assert.Equal(255, rb.Max());
    }

    [Fact]
    public void LargeImageParallelMatchesSingleThread()
    {
        var src = Random(1003, 757, 4, SampleType.UInt8, 9);
        foreach (var (w, h) in new[] { (311, 211), (1500, 1100), (1003, 300) })
        {
            var a = ImageOps.Resize(src, w, h, new ResizeOptions { MaxDegreeOfParallelism = 1 });
            var b = ImageOps.Resize(src, w, h);
            AssertEqual(a, b);
        }
    }

    [Fact]
    public void ScaleAndFit()
    {
        var src = Random(100, 50, 3, SampleType.UInt8, 3);
        var s = ImageOps.Scale(src, 0.333, 0.5);
        Assert.Equal((33, 25), (s.Width, s.Height));
        var f = ImageOps.ResizeToFit(src, 40, 40);
        Assert.Equal((40, 20), (f.Width, f.Height));
        Assert.Same(src, ImageOps.ResizeToFit(src, 200, 200));
        var u = ImageOps.ResizeToFit(src, 200, 200, allowUpscale: true);
        Assert.Equal((200, 100), (u.Width, u.Height));
    }

    #region Lossless rearrangements

    // destination (x, y) -> source (x, y) for EXIF orientations, from the TIFF 6.0 definitions
    static (int sx, int sy) OrientSource(int o, int x, int y, int w, int h) => o switch
    {
        1 => (x, y),
        2 => (w - 1 - x, y),
        3 => (w - 1 - x, h - 1 - y),
        4 => (x, h - 1 - y),
        5 => (y, x),
        6 => (y, h - 1 - x),
        7 => (w - 1 - y, h - 1 - x),
        8 => (w - 1 - y, x),
        _ => throw new ArgumentException(),
    };

    [Theory]
    [InlineData(SampleType.UInt8, 1)] [InlineData(SampleType.UInt8, 3)] [InlineData(SampleType.UInt8, 4)]
    [InlineData(SampleType.UInt16, 3)] [InlineData(SampleType.Float32, 1)] [InlineData(SampleType.Float32, 3)]
    [InlineData(SampleType.Float32, 4)] [InlineData(SampleType.Float64, 3)] [InlineData(SampleType.Int8, 5)]
    [InlineData(SampleType.UInt16, 1)] [InlineData(SampleType.UInt16, 4)] [InlineData(SampleType.Float64, 4)]
    public void OrientationsArePermutations(SampleType t, int nc)
    {
        foreach (var (w, h) in new[] { (1, 1), (7, 3), (130, 67), (64, 64) })
        {
            var src = Random(w, h, nc, t, w + nc);
            if (nc == 5) src = new RawImage(w, h, ColorModel.Other, src.Data, 5);
            int bpp = nc * RawImage.BytesPerSample(t);
            var sb = src.GetBytes().ToArray();
            for (int o = 1; o <= 8; o++)
            {
                var r = ImageOps.ApplyOrientation(src, o);
                Assert.Equal(o >= 5 ? (h, w) : (w, h), (r.Width, r.Height));
                var rb = r.GetBytes();
                for (int y = 0; y < r.Height; y++)
                    for (int x = 0; x < r.Width; x++)
                    {
                        var (sx, sy) = OrientSource(o, x, y, w, h);
                        Assert.True(rb.Slice((y * r.Width + x) * bpp, bpp).SequenceEqual(sb.AsSpan((sy * w + sx) * bpp, bpp)), $"o{o} {w}x{h} at {x},{y}");
                    }
            }
        }
    }

    [Fact]
    public void NamedRearrangementsAndInverses()
    {
        var src = Random(13, 7, 3, SampleType.UInt8, 5);
        AssertEqual(ImageOps.ApplyOrientation(src, 6), ImageOps.Rotate90(src));
        AssertEqual(ImageOps.ApplyOrientation(src, 8), ImageOps.Rotate270(src));
        AssertEqual(ImageOps.ApplyOrientation(src, 3), ImageOps.Rotate180(src));
        AssertEqual(ImageOps.ApplyOrientation(src, 2), ImageOps.FlipHorizontal(src));
        AssertEqual(ImageOps.ApplyOrientation(src, 4), ImageOps.FlipVertical(src));
        AssertEqual(ImageOps.ApplyOrientation(src, 5), ImageOps.Transpose(src));
        AssertEqual(ImageOps.ApplyOrientation(src, 7), ImageOps.Transverse(src));
        AssertEqual(src, ImageOps.Rotate270(ImageOps.Rotate90(src)));
        AssertEqual(src, ImageOps.Transpose(ImageOps.Transpose(src)));
        AssertEqual(src, ImageOps.Rotate90(ImageOps.Rotate90(ImageOps.Rotate180(src))));

        src.Metadata = new ImageMetadata { Orientation = 6, DpiX = 72, DpiY = 300 };
        var up = ImageOps.ApplyOrientation(src);
        Assert.Equal((7, 13), (up.Width, up.Height));
        Assert.Equal(1, up.Metadata!.Orientation);
        Assert.Equal((300.0, 72.0), (up.Metadata.DpiX, up.Metadata.DpiY));
        Assert.Equal(6, src.Metadata.Orientation); // source untouched
    }

    [Fact]
    public void Crop()
    {
        var src = Random(20, 10, 2, SampleType.Int16, 6);
        var c = ImageOps.Crop(src, 3, 2, 5, 4);
        var s = (short[])src.Data; var d = (short[])c.Data;
        for (int y = 0; y < 4; y++) for (int x = 0; x < 5; x++) for (int ch = 0; ch < 2; ch++)
            Assert.Equal(s[((y + 2) * 20 + x + 3) * 2 + ch], d[(y * 5 + x) * 2 + ch]);
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageOps.Crop(src, 16, 0, 5, 4));
    }

    #endregion

    #region Warps

    [Theory]
    [InlineData(90)] [InlineData(-90)] [InlineData(180)] [InlineData(270)] [InlineData(360)] [InlineData(450)]
    public void RotateByRightAnglesIsExact(double degrees)
    {
        var src = Random(13, 8, 4, SampleType.UInt8, 7);
        foreach (var f in new[] { ResampleFilter.Nearest, ResampleFilter.Bilinear, ResampleFilter.Bicubic, ResampleFilter.Lanczos3 })
        {
            var r = ImageOps.Rotate(src, degrees, true, new WarpOptions { Filter = f });
            int k = (((int)degrees / 90) % 4 + 4) % 4;
            var exp = k switch { 0 => src, 1 => ImageOps.Rotate270(src), 2 => ImageOps.Rotate180(src), _ => ImageOps.Rotate90(src) };
            AssertEqual(exp, r);
        }
    }

    [Fact]
    public void RotateArbitraryAngle()
    {
        // a centred disc stays a centred disc; the expanded canvas holds the rotated rectangle, the corners are border
        int w = 41, h = 21;
        var img = RawImage.Create(w, h, ColorModel.Gray, SampleType.Float32);
        var d = (float[])img.Data;
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            d[y * w + x] = (x + 0.5 - w / 2.0) * (x + 0.5 - w / 2.0) + (y + 0.5 - h / 2.0) * (y + 0.5 - h / 2.0) < 64 ? 1 : 0;
        var r = ImageOps.Rotate(img, 30, true, new WarpOptions { Filter = ResampleFilter.Bilinear, BorderValue = new[] { -1.0 } });
        double c = Math.Cos(Math.PI / 6), s = Math.Sin(Math.PI / 6);
        Assert.Equal((int)Math.Ceiling(w * c + h * s), r.Width);
        Assert.Equal((int)Math.Ceiling(w * s + h * c), r.Height);
        var rd = (float[])r.Data;
        Assert.Equal(1f, rd[(r.Height / 2) * r.Width + r.Width / 2]);
        Assert.Equal(-1f, rd[0]);
        Assert.Equal(-1f, rd[rd.Length - 1]);
        var same = ImageOps.Rotate(img, 30, false);
        Assert.Equal((w, h), (same.Width, same.Height));

        // counter-clockwise: a mark right of the centre moves up
        var m = RawImage.Create(21, 21, ColorModel.Gray, SampleType.Float32);
        ((float[])m.Data)[10 * 21 + 18] = 1;
        var mr = (float[])ImageOps.Rotate(m, 90, false, new WarpOptions { Filter = ResampleFilter.Nearest }).Data;
        Assert.Equal(1f, mr[2 * 21 + 10]);
    }

    [Fact]
    public void AffineAndPerspectiveAgree()
    {
        var src = Random(30, 20, 3, SampleType.Float32, 8);
        double[] a = { 0.8, 0.2, 3.5, -0.1, 1.1, -2.25 };
        double[] p = { 0.8, 0.2, 3.5, -0.1, 1.1, -2.25, 0, 0, 1 };
        var o = new WarpOptions { Filter = ResampleFilter.Bicubic, Border = BorderMode.Mirror };
        var ra = ImageOps.WarpAffine(src, 25, 22, a, o);
        var rp = ImageOps.WarpPerspective(src, 25, 22, p, o);
        Assert.True(MaxDiff(ra, rp) < 1e-6);
        // translation by whole pixels is exact
        var t = ImageOps.WarpAffine(src, 30, 20, new double[] { 1, 0, 2, 0, 1, 1 }, new WarpOptions { Filter = ResampleFilter.Lanczos3, Border = BorderMode.Wrap });
        var s = (float[])src.Data; var td = (float[])t.Data;
        for (int y = 0; y < 20; y++) for (int x = 0; x < 30; x++) for (int c = 0; c < 3; c++)
            Assert.Equal(s[(((y + 1) % 20) * 30 + (x + 2) % 30) * 3 + c], td[(y * 30 + x) * 3 + c], 5);
    }

    [Theory]
    [InlineData(BorderMode.Clamp, new[] { 0, 0, 0, 0, 1, 2, 3, 4, 4 })]
    [InlineData(BorderMode.Wrap, new[] { 2, 3, 4, 0, 1, 2, 3, 4, 0 })]
    [InlineData(BorderMode.Mirror, new[] { 3, 2, 1, 0, 1, 2, 3, 4, 3 })]
    [InlineData(BorderMode.MirrorRepeat, new[] { 2, 1, 0, 0, 1, 2, 3, 4, 4 })]
    public void BorderModes(BorderMode mode, int[] expected)
    {
        // source 0..4, sampled at -3..5
        var img = RawImage.Create(5, 1, ColorModel.Gray, SampleType.Float32);
        for (int i = 0; i < 5; i++) ((float[])img.Data)[i] = i;
        var r = ImageOps.WarpAffine(img, 9, 1, new double[] { 1, 0, -3, 0, 1, 0 }, new WarpOptions { Filter = ResampleFilter.Nearest, Border = mode });
        Assert.Equal(expected.Select(v => (float)v), (float[])r.Data);
    }

    #endregion

    [Fact]
    public void DecodeAllReturnsPagesAndFrames()
    {
        var a = Random(12, 9, 3, SampleType.UInt8, 1);
        var b = Random(12, 9, 3, SampleType.UInt8, 2);
        var ms = new MemoryStream();
        Tiff.Encode(new[] { a, b, a }, ms);
        var pages = ImageCodec.DecodeAll(ms.ToArray());
        Assert.Equal(3, pages.Length);
        AssertEqual(b, pages[1]);

        var gs = new MemoryStream();
        Gif.EncodeAnimation(new[] { new GifInputFrame(a), new GifInputFrame(b) }, gs);
        Assert.Equal(2, ImageCodec.DecodeAll(gs.ToArray()).Length);

        var ps = new MemoryStream();
        Png.Encode(a, ps);
        var single = ImageCodec.DecodeAll(ps.ToArray());
        Assert.Single(single);
        AssertEqual(a, single[0]);
    }

    [Fact]
    public void ResizeSpeed()
    {
        var src = Random(4000, 3000, 3, SampleType.UInt8, 1);
        ImageOps.Resize(src, 1000, 750);
        foreach (var (f, w, h) in new[] { (ResampleFilter.Lanczos3, 1000, 750), (ResampleFilter.Bilinear, 1000, 750), (ResampleFilter.Lanczos3, 6000, 4500) })
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ImageOps.Resize(src, w, h, f);
            output.WriteLine($"{f} 4000x3000 RGB8 -> {w}x{h}: {sw.ElapsedMilliseconds} ms");
        }
    }
}
