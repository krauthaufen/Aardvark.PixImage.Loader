using System.Diagnostics;
using PixCodec;
using Xunit;
using Xunit.Abstractions;
using static PixCodec.Tests.TestUtil;

namespace PixCodec.Tests;

public class GifTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Fixtures => Files("gif", ".gif");

    static List<RawImage> FrameRefs(string path)
    {
        var refs = new List<RawImage>();
        for (int i = 0; File.Exists($"{path}.{i}.ref"); i++) refs.Add(LoadRef($"{path}.{i}.ref"));
        return refs;
    }

    // ------------------------------------------------------------------ decoder vs. references

    [Theory, MemberData(nameof(Fixtures))]
    public void DecodeMatchesReference(string file)
    {
        var path = Fixture(file);
        var data = File.ReadAllBytes(path);
        var expected = LoadRef(path + ".ref");
        var img = Gif.Decode(data);
        AssertEqual(expected, img);
        Assert.Equal(expected.Channels == 4 ? ColorModel.Rgba : ColorModel.Rgb, img.Color);

        var info = Gif.GetInfo(data);
        Assert.Equal(new ImageInfo(ImageFormat.Gif, img.Width, img.Height, img.Channels, SampleType.UInt8, img.Color, FrameRefs(path).Count), info);
        Assert.True(Gif.IsMatch(data));
    }

    [Theory, MemberData(nameof(Fixtures))]
    public void AnimationMatchesReference(string file)
    {
        var path = Fixture(file);
        var data = File.ReadAllBytes(path);
        var refs = FrameRefs(path);
        Assert.NotEmpty(refs);

        var anim = Gif.DecodeAnimation(data);
        Assert.Equal(refs.Count, anim.Frames.Count);
        Assert.Equal(refs.Count, Gif.GetAnimationInfo(data).FrameCount);
        for (int i = 0; i < refs.Count; i++)
        {
            Assert.Equal(i, anim.Frames[i].Index);
            Assert.Equal(ColorModel.Rgba, anim.Frames[i].Image!.Color);
            AssertEqual(refs[i], anim.Frames[i].Image!);
        }

        // The lazy variant yields the same frames, each with pixels of its own.
        int n = 0;
        foreach (var f in Gif.EnumerateFrames(data))
        {
            AssertEqual(refs[n], f.Image!);
            Assert.NotSame(anim.Frames[n].Image!.Data, f.Image!.Data);
            n++;
        }
        Assert.Equal(refs.Count, n);
    }

    /// <summary>Re-does the compositing from the raw indexed frames with a naive implementation.</summary>
    [Theory, MemberData(nameof(Fixtures))]
    public void IndexedFramesComposeToReference(string file)
    {
        var path = Fixture(file);
        var data = File.ReadAllBytes(path);
        var refs = FrameRefs(path);
        var anim = Gif.DecodeAnimation(data, new GifDecodeOptions { FrameMode = GifFrameMode.Indexed });
        Assert.Equal(refs.Count, anim.Frames.Count);
        int w = anim.Width, h = anim.Height;
        var canvas = new byte[w * h * 4];
        GifFrame? prev = null;
        byte[]? backup = null;
        for (int k = 0; k < anim.Frames.Count; k++)
        {
            var f = anim.Frames[k];
            Assert.Null(f.Image);
            Assert.Equal(f.Width * f.Height, f.Indices!.Length);
            if (prev != null)
            {
                if (prev.Disposal == GifDisposal.RestoreBackground)
                    for (int y = prev.Y; y < Math.Min(h, prev.Y + prev.Height); y++)
                        for (int x = prev.X; x < Math.Min(w, prev.X + prev.Width); x++)
                            canvas.AsSpan((y * w + x) * 4, 4).Clear();
                else if (prev.Disposal == GifDisposal.RestorePrevious) canvas = backup!;
            }
            backup = (byte[])canvas.Clone();
            for (int y = 0; y < f.Height; y++)
                for (int x = 0; x < f.Width; x++)
                {
                    int i = f.Indices[y * f.Width + x];
                    int cx = f.X + x, cy = f.Y + y;
                    if (i == f.TransparentIndex || cx >= w || cy >= h) continue;
                    var p = canvas.AsSpan((cy * w + cx) * 4, 4);
                    p.Clear();
                    if (i * 3 < f.Palette!.Length) f.Palette.AsSpan(i * 3, 3).CopyTo(p);
                    p[3] = 255;
                }
            prev = f;
            // A frame cut short has no way to say "pixel missing" in indexed form unless it has a transparent index.
            if (f.Truncated && f.TransparentIndex < 0) return;
            AssertEqual(refs[k], new RawImage(w, h, ColorModel.Rgba, (byte[])canvas.Clone()));
        }
    }

    [Fact]
    public void AnimationProperties()
    {
        byte[] Load(string name) => File.ReadAllBytes(Fixture("gif/" + name));

        var a = Gif.DecodeAnimation(Load("pil_anim_disposal2.gif"));
        Assert.Equal((32, 24, 4), (a.Width, a.Height, a.FrameCount));
        Assert.Equal(0, a.LoopCount);
        Assert.Equal(new[] { 3, 7, 11, 20 }, a.Frames.Select(f => f.DelayCs));
        Assert.Equal(new[] { 30, 70, 110, 200 }, a.Frames.Select(f => f.DelayMs));
        Assert.All(a.Frames, f => Assert.Equal(GifDisposal.RestoreBackground, f.Disposal));
        Assert.NotNull(a.GlobalPalette);
        Assert.True(a.IsGif89a);

        Assert.Equal(3, Gif.GetAnimationInfo(Load("pil_anim_loop3.gif")).LoopCount);
        Assert.Null(Gif.GetAnimationInfo(Load("pil_anim_noloop.gif")).LoopCount);
        Assert.Equal(7, Gif.GetAnimationInfo(Load("hand_loop_animexts.gif")).LoopCount);
        Assert.Equal(65535, Gif.GetAnimationInfo(Load("hand_loop_65535.gif")).LoopCount);
        Assert.Equal(1, Gif.GetAnimationInfo(Load("im_anim.gif")).LoopCount);     // magick -loop 2 stores 1
        Assert.False(Gif.GetAnimationInfo(Load("hand_gif87a.gif")).IsGif89a);

        Assert.Equal(string.Concat(Enumerable.Repeat("hello gif ", 40)), Assert.Single(Gif.GetAnimationInfo(Load("pil_comment.gif")).Comments));
        Assert.Equal("a comment that is long enough to need more than one tiny block",
            Assert.Single(Gif.GetAnimationInfo(Load("hand_extensions.gif")).Comments));
        Assert.Single(Gif.GetAnimationInfo(Load("hand_comment_after_image.gif")).Comments);

        var m = Gif.DecodeAnimation(Load("hand_anim_disposal_mixed.gif"));
        Assert.Equal(new[] { 1, 2, 3, 0, 3, 3, 2, 1 }, m.Frames.Select(f => (int)f.Disposal));
        Assert.Equal((2, 3, 9, 6), (m.Frames[1].X, m.Frames[1].Y, m.Frames[1].Width, m.Frames[1].Height));
        Assert.Equal(-1, m.Frames[0].TransparentIndex);
        Assert.Equal(1, m.Frames[1].TransparentIndex);
        Assert.Equal(1, m.LoopCount);

        var bg = Gif.GetAnimationInfo(Load("hand_background_index.gif"));
        Assert.Equal(5, bg.BackgroundIndex);
        Assert.Equal(((byte)255, (byte)0, (byte)255), bg.BackgroundColor);

        var loc = Gif.DecodeAnimation(Load("hand_anim_local_palettes_clipped.gif"), new GifDecodeOptions { FrameMode = GifFrameMode.Indexed });
        Assert.Equal(new[] { false, true, true, false }, loc.Frames.Select(f => f.HasLocalPalette));
        Assert.Equal(new[] { 24, 12, 48, 24 }, loc.Frames.Select(f => f.Palette!.Length));
        Assert.Equal((18, 12, 10, 6), (loc.Frames[1].X, loc.Frames[1].Y, loc.Frames[1].Width, loc.Frames[1].Height)); // unclipped

        Assert.True(Gif.DecodeAnimation(Load("hand_truncated_data.gif")).Frames[0].Truncated);
        Assert.True(Gif.DecodeAnimation(Load("hand_no_end_code.gif")).Frames[0].Truncated == false);
        Assert.True(Gif.DecodeAnimation(Load("pil_pal16_interlaced.gif")).Frames[0].Interlaced);
    }

    // ------------------------------------------------------------------ LZW

    [Fact]
    public void LzwRoundTrip()
    {
        var rnd = new Random(7);
        foreach (int bits in new[] { 2, 3, 4, 5, 6, 7, 8 })
            foreach (int n in new[] { 1, 2, 3, 17, 300, 5000, 70000 })
                foreach (int kind in new[] { 0, 1, 2 })
                {
                    var px = new byte[n];
                    for (int i = 0; i < n; i++)
                        px[i] = kind switch
                        {
                            0 => (byte)rnd.Next(1 << bits),                         // noise: table fills fast, many clears
                            1 => (byte)((i / 37 + i / 1000) & ((1 << bits) - 1)),   // runs
                            _ => 0,                                                  // one value: longest strings
                        };
                    var ms = new MemoryStream();
                    GifLzw.Encode(px, bits, ms);
                    var enc = ms.ToArray();
                    Assert.Equal(bits, enc[0]);
                    // strip the sub-block framing
                    var codes = new MemoryStream();
                    int p = 1;
                    while (enc[p] != 0) { codes.Write(enc, p + 1, enc[p]); p += enc[p] + 1; }
                    Assert.Equal(enc.Length - 1, p);
                    var back = new byte[n];
                    Assert.Equal(n, GifLzw.Decode(codes.ToArray(), bits, back));
                    Assert.Equal(px, back);
                    // a smaller destination takes the first pixels and stops
                    var part = new byte[n / 2];
                    Assert.Equal(n / 2, GifLzw.Decode(codes.ToArray(), bits, part));
                    Assert.True(px.AsSpan(0, n / 2).SequenceEqual(part));
                }
    }

    // ------------------------------------------------------------------ encoder

    static RawImage Pattern(int w, int h, ColorModel color, int levels, int seed = 1)
    {
        // 'levels' values per channel: few colors when small, a photo-like mess at 256.
        var rnd = new Random(seed);
        int c = RawImage.ChannelCount(color);
        var d = new byte[w * h * c];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int k = 0; k < c; k++)
                {
                    double v = 0.5 + 0.5 * Math.Sin(x / (7.0 + 3 * k) + k) * Math.Cos(y / (5.0 + 2 * k));
                    v = 0.7 * v + 0.2 * (((x / 8 + y / 8 + k) & 1)) + 0.1 * rnd.NextDouble();
                    int q = (int)Math.Round(Math.Clamp(v, 0, 1) * (levels - 1));
                    d[(y * w + x) * c + k] = (byte)(q * 255 / Math.Max(1, levels - 1));
                }
        return new RawImage(w, h, color, d);
    }

    /// <summary>What a GIF can hold of an image: Rgba with 1 bit alpha, transparent pixels zeroed.</summary>
    static RawImage ToRgba(RawImage img, int threshold = 128)
    {
        var s = img.GetData<byte>();
        int n = img.Width * img.Height, c = img.Channels;
        var d = new byte[n * 4];
        for (int i = 0; i < n; i++)
        {
            int a = img.HasAlpha ? s[i * c + c - 1] : 255;
            if (a < threshold) continue;
            d[i * 4] = s[i * c];
            d[i * 4 + 1] = c >= 3 ? s[i * c + 1] : s[i * c];
            d[i * 4 + 2] = c >= 3 ? s[i * c + 2] : s[i * c];
            d[i * 4 + 3] = 255;
        }
        return new RawImage(img.Width, img.Height, ColorModel.Rgba, d);
    }

    static RawImage AsRgba(RawImage decoded)
    {
        if (decoded.Color == ColorModel.Rgba) return decoded;
        return ToRgba(decoded);
    }

    /// <summary>PSNR over the color channels of the opaque pixels (alpha has to match exactly and would only flatter the number).</summary>
    static double ColorPsnr(RawImage expected, RawImage actual)
    {
        var e = expected.GetData<byte>(); var a = actual.GetData<byte>();
        Assert.Equal(e.Length, a.Length);
        double se = 0; long n = 0;
        for (int i = 0; i < e.Length; i += 4)
        {
            Assert.Equal(e[i + 3], a[i + 3]);
            if (e[i + 3] == 0) continue;
            for (int k = 0; k < 3; k++) { double d = e[i + k] - a[i + k]; se += d * d; }
            n += 3;
        }
        return se == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 * n / se);
    }

    static byte[] Encode(RawImage img, GifEncodeOptions? o = null)
    {
        var ms = new MemoryStream();
        Gif.Encode(img, ms, o);
        return ms.ToArray();
    }

    static bool HasTransparency(RawImage rgba)
    {
        var d = rgba.GetData<byte>();
        for (int i = 3; i < d.Length; i += 4) if (d[i] == 0) return true;
        return false;
    }

    public static IEnumerable<object[]> LosslessCases()
    {
        foreach (var color in new[] { ColorModel.Gray, ColorModel.GrayAlpha, ColorModel.Rgb, ColorModel.Rgba })
            foreach (var (w, h) in new[] { (1, 1), (2, 1), (1, 7), (13, 9), (64, 37), (130, 90) })
                foreach (bool interlace in new[] { false, true })
                    yield return new object[] { color, w, h, interlace };
    }

    [Theory, MemberData(nameof(LosslessCases))]
    public void EncodeLossless(ColorModel color, int w, int h, bool interlace)
    {
        // Gray has at most 256 colors anyway; color images get 6 levels on three channels = up to 216 colors.
        var img = Pattern(w, h, color, color is ColorModel.Gray or ColorModel.GrayAlpha ? 256 : 6, seed: w * 31 + h);
        if (color == ColorModel.GrayAlpha)
        {
            // 256 gray levels plus transparency would not fit: keep 200 levels
            var d = img.GetData<byte>();
            for (int i = 0; i < d.Length; i += 2) d[i] = (byte)(d[i] * 199 / 255);
        }
        var expected = ToRgba(img);
        var gif = Encode(img, new GifEncodeOptions { Interlace = interlace });

        var back = Gif.Decode(gif);
        Assert.Equal(HasTransparency(expected) ? ColorModel.Rgba : ColorModel.Rgb, back.Color);
        AssertEqual(expected, AsRgba(back));
        var info = Gif.GetInfo(gif);
        Assert.Equal((w, h, 1), (info.Width, info.Height, info.FrameCount));

        var oracle = OracleDecode(gif, ".gif");
        if (oracle == null) return;
        AssertEqual(expected, oracle);
    }

    [Theory]
    [InlineData(2), InlineData(3), InlineData(4), InlineData(5), InlineData(16), InlineData(17), InlineData(128), InlineData(255), InlineData(256)]
    public void EncodeLosslessPaletteSizes(int colors)
    {
        // exactly 'colors' distinct colors, the last one transparent in the second variant
        foreach (bool transparent in new[] { false, true })
        {
            int w = 40, h = 23;
            var d = new byte[w * h * 4];
            var rnd = new Random(colors);
            for (int i = 0; i < w * h; i++)
            {
                int c = i < colors ? i : rnd.Next(colors);
                d[i * 4] = (byte)(c * 7); d[i * 4 + 1] = (byte)(255 - c); d[i * 4 + 2] = (byte)(c ^ 0x55);
                d[i * 4 + 3] = (byte)(transparent && c == colors - 1 ? 0 : 255);
            }
            var img = new RawImage(w, h, ColorModel.Rgba, d);
            var expected = ToRgba(img);
            var gif = Encode(img);
            // smallest table that holds the colors
            int tableBits = (gif[10] & 7) + 1;
            Assert.True(1 << tableBits >= colors && (tableBits == 1 || 1 << (tableBits - 1) < colors), $"table of {1 << tableBits} for {colors} colors");
            AssertEqual(expected, AsRgba(Gif.Decode(gif)));
            var oracle = OracleDecode(gif, ".gif");
            if (oracle != null) AssertEqual(expected, oracle);
        }
    }

    [Fact]
    public void EncodeOptionsAndErrors()
    {
        var img = Pattern(20, 10, ColorModel.Rgba, 4);
        var d = img.GetData<byte>();
        for (int i = 3; i < d.Length; i += 4) d[i] = (byte)(i * 5);
        foreach (int thr in new[] { 0, 1, 77, 128, 255, 256 })
        {
            var gif = Encode(img, new GifEncodeOptions { AlphaThreshold = thr });
            AssertEqual(ToRgba(img, thr), AsRgba(Gif.Decode(gif)));
        }
        var withComment = Encode(img, new GifEncodeOptions { Comment = new string('x', 700) });
        Assert.Equal(new string('x', 700), Assert.Single(Gif.GetAnimationInfo(withComment).Comments));
        Assert.Null(Gif.GetAnimationInfo(withComment).LoopCount);

        Assert.Throws<ImageNotSupportedException>(() => Encode(RawImage.Create(4, 4, ColorModel.Rgb, SampleType.UInt16)));
        Assert.Throws<ImageNotSupportedException>(() => Encode(RawImage.Create(4, 4, ColorModel.Gray, SampleType.Float32)));
        Assert.Throws<ImageNotSupportedException>(() => Encode(RawImage.Create(4, 4, ColorModel.Other, SampleType.UInt8, 5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Encode(img, new GifEncodeOptions { MaxColors = 1 }));
        Assert.Throws<ArgumentException>(() => Gif.EncodeAnimation(Array.Empty<GifInputFrame>()));
        Assert.Throws<ArgumentException>(() => Gif.EncodeAnimation(new[] { new GifInputFrame(img), new GifInputFrame(Pattern(21, 10, ColorModel.Rgb, 4)) }));

        var viaCodec = ImageCodec.Encode(img, ImageFormat.Gif);
        Assert.True(Gif.IsMatch(viaCodec));
    }

    /// <summary>
    /// Quantization quality on a deliberately hard image: gradients, edges and 10% independent noise per channel
    /// (~49k distinct colors in 256x192). PSNR is over the color channels of the opaque pixels. Measured at 256 colors
    /// without dithering: 28.8 dB, where Pillow 11.1 reaches 26.4 dB (median cut), 27.3 dB (max coverage) and 29.0 dB
    /// (median cut + 5 k-means passes) on the same pixels. The bounds sit 0.5 to 1 dB below what this encoder
    /// measures (64 colors 24.7, 16 colors 20.6, 2 colors 15.6 / 13.8 with a transparent slot; Floyd-Steinberg costs
    /// about 1.5 dB of PSNR in exchange for the absence of banding), so they trip on a broken palette or a wrong
    /// nearest-color mapping but not on rounding changes.
    /// </summary>
    [Theory]
    [InlineData(256, GifDither.None, 28.0)]
    [InlineData(256, GifDither.FloydSteinberg, 26.5)]
    [InlineData(64, GifDither.None, 24.0)]
    [InlineData(16, GifDither.None, 20.0)]
    [InlineData(16, GifDither.FloydSteinberg, 18.5)]
    [InlineData(2, GifDither.None, 13.0)]
    public void EncodeQuantized(int maxColors, GifDither dither, double minPsnr)
    {
        foreach (var color in new[] { ColorModel.Rgb, ColorModel.Rgba })
        {
            var img = Pattern(256, 192, color, 256, seed: 3);
            if (color == ColorModel.Rgba)
            {
                var d = img.GetData<byte>();
                for (int i = 0; i < d.Length; i += 4) d[i + 3] = (byte)((i / 4 % 256 + i / 4 / 256) % 64 < 12 ? 0 : 255);
            }
            var expected = ToRgba(img);
            var gif = Encode(img, new GifEncodeOptions { MaxColors = maxColors, Dither = dither });
            var back = AsRgba(Gif.Decode(gif));
            // transparency is never approximated
            var e = expected.GetData<byte>(); var b = back.GetData<byte>();
            for (int i = 3; i < e.Length; i += 4) Assert.Equal(e[i], b[i]);
            var colors = new HashSet<int>();
            for (int i = 0; i < b.Length; i += 4) colors.Add(b[i] | b[i + 1] << 8 | b[i + 2] << 16 | b[i + 3] << 24);
            Assert.True(colors.Count <= maxColors, $"{colors.Count} colors");
            double psnr = ColorPsnr(expected, back);
            output.WriteLine($"{color} {maxColors} colors, {dither}: {psnr:F2} dB, {gif.Length} bytes");
            Assert.True(psnr > minPsnr, $"PSNR {psnr:F2} dB");

            var oracle = OracleDecode(gif, ".gif");
            if (oracle != null) AssertEqual(back, oracle);
        }
    }

    /// <summary>
    /// The case the palette is made for: a smooth two-axis color gradient with a little (1%) noise, 256 colors, no
    /// dithering. Measured 34.2 dB (31.9 dB with the default Floyd-Steinberg dithering); Pillow 11.1 gets 34.0 dB with
    /// median cut and 34.6 dB with 5 extra k-means passes on the same image.
    /// </summary>
    [Fact]
    public void EncodeQuantizedSmoothGradient()
    {
        int w = 256, h = 192;
        var rnd = new Random(4);
        var d = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double u = x / (w - 1.0), v = y / (h - 1.0);
                double[] c = { u, v, 0.5 + 0.5 * Math.Sin(3 * u + 2 * v) };
                for (int k = 0; k < 3; k++)
                    d[(y * w + x) * 3 + k] = (byte)Math.Clamp(Math.Round((c[k] + 0.01 * (rnd.NextDouble() - 0.5)) * 255), 0, 255);
            }
        var img = new RawImage(w, h, ColorModel.Rgb, d);
        if (Environment.GetEnvironmentVariable("PIXCODEC_DUMP") is { } dump) SaveRef(Path.Combine(dump, "smooth.ref"), img);
        var gif = Encode(img, new GifEncodeOptions { Dither = GifDither.None });
        double psnr = ColorPsnr(ToRgba(img), AsRgba(Gif.Decode(gif)));
        output.WriteLine($"smooth gradient: {psnr:F2} dB");
        Assert.True(psnr > 33.5, $"PSNR {psnr:F2} dB");
        double dithered = ColorPsnr(ToRgba(img), AsRgba(Gif.Decode(Encode(img))));
        output.WriteLine($"smooth gradient, default options (Floyd-Steinberg): {dithered:F2} dB");
        Assert.True(dithered > 31, $"PSNR {dithered:F2} dB");
    }

    [Fact]
    public void QuantizerUsesTheWholePalette()
    {
        // 300 well separated colors: 256 of them (or close to it) must survive.
        var d = new byte[300 * 4 * 3];
        for (int i = 0; i < 300 * 4; i++)
        {
            int c = i % 300;
            d[i * 3] = (byte)(c % 7 * 40); d[i * 3 + 1] = (byte)(c / 7 % 7 * 40); d[i * 3 + 2] = (byte)(c / 49 * 40);
        }
        var img = new RawImage(300, 4, ColorModel.Rgb, d);
        var back = Gif.Decode(Encode(img, new GifEncodeOptions { Dither = GifDither.None })).GetData<byte>();
        var colors = new HashSet<int>();
        for (int i = 0; i < back.Length; i += 3) colors.Add(back[i] | back[i + 1] << 8 | back[i + 2] << 16);
        Assert.InRange(colors.Count, 250, 256);
    }

    // ------------------------------------------------------------------ animation encoder

    /// <summary>Frames with a moving block, optionally with holes that open and close again.</summary>
    static List<RawImage> Movie(int n, int w, int h, int levels, bool transparency, ColorModel color = ColorModel.Rgba)
    {
        var frames = new List<RawImage>();
        var bgd = Pattern(w, h, ColorModel.Rgb, levels, seed: 5).GetData<byte>();
        for (int k = 0; k < n; k++)
        {
            var d = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 4;
                    d[o] = bgd[(y * w + x) * 3]; d[o + 1] = bgd[(y * w + x) * 3 + 1]; d[o + 2] = bgd[(y * w + x) * 3 + 2]; d[o + 3] = 255;
                    int bx = 3 + k * 5 % Math.Max(1, w - 10), by = 2 + k * 3 % Math.Max(1, h - 8);
                    if (x >= bx && x < bx + 9 && y >= by && y < by + 6) { d[o] = 255; d[o + 1] = (byte)(k * 20); d[o + 2] = 0; }
                    if (transparency && k % 4 != 1)
                    {
                        // a hole that moves: pixels turn transparent and opaque again
                        int hx = w - 12 - k * 2, hy = h / 2;
                        if (Math.Abs(x - hx) + Math.Abs(y - hy) < 5 + k % 3) d[o] = d[o + 1] = d[o + 2] = d[o + 3] = 0;
                    }
                }
            var img = new RawImage(w, h, ColorModel.Rgba, d);
            if (color == ColorModel.Rgb)
            {
                var rgb = new byte[w * h * 3];
                for (int i = 0; i < w * h; i++) d.AsSpan(i * 4, 3).CopyTo(rgb.AsSpan(i * 3));
                img = new RawImage(w, h, ColorModel.Rgb, rgb);
            }
            frames.Add(img);
        }
        return frames;
    }

    static RawImage Stack(IReadOnlyList<RawImage> frames)
    {
        int w = frames[0].Width, h = frames[0].Height;
        var d = new byte[w * h * 4 * frames.Count];
        for (int i = 0; i < frames.Count; i++) frames[i].GetData<byte>().CopyTo(d, i * w * h * 4);
        return new RawImage(w, h * frames.Count, ColorModel.Rgba, d);
    }

    public static IEnumerable<object[]> AnimationCases()
    {
        foreach (var palette in new[] { GifPaletteMode.Global, GifPaletteMode.PerFrame })
            foreach (bool diff in new[] { true, false })
                foreach (bool transparency in new[] { false, true })
                    foreach (bool interlace in new[] { false, true })
                        yield return new object[] { palette, diff, transparency, interlace };
    }

    [Theory, MemberData(nameof(AnimationCases))]
    public void EncodeAnimationLossless(GifPaletteMode palette, bool diff, bool transparency, bool interlace)
    {
        var frames = Movie(9, 48, 30, 5, transparency);
        frames.Insert(4, frames[3]);        // an unchanged frame must still take its time
        var o = new GifEncodeOptions { PaletteMode = palette, FrameDifferencing = diff, Interlace = interlace, LoopCount = 5 };
        var gif = Gif.EncodeAnimation(frames.Select((f, i) => new GifInputFrame(f, 40 + 10 * i)), o);

        var anim = Gif.DecodeAnimation(gif);
        Assert.Equal(frames.Count, anim.Frames.Count);
        Assert.Equal(5, anim.LoopCount);
        for (int i = 0; i < frames.Count; i++)
        {
            AssertEqual(ToRgba(frames[i]), anim.Frames[i].Image!);
            Assert.Equal(4 + i, anim.Frames[i].DelayCs);
            Assert.Equal(palette == GifPaletteMode.PerFrame, anim.Frames[i].HasLocalPalette);
        }
        Assert.Equal(palette == GifPaletteMode.Global, anim.GlobalPalette != null);
        if (diff && !transparency)
            Assert.Contains(anim.Frames, f => f.Width < 48 && f.Height < 30);

        var oracle = OracleDecode(gif, ".anim.gif");
        if (oracle == null) return;
        AssertEqual(Stack(frames.Select(f => ToRgba(f)).ToList()), oracle);
    }

    [Fact]
    public void FrameDifferencingSavesBytes()
    {
        var frames = Movie(12, 96, 64, 6, false).Select(f => new GifInputFrame(f, 50)).ToList();
        int with = Gif.EncodeAnimation(frames, new GifEncodeOptions { FrameDifferencing = true }).Length;
        int without = Gif.EncodeAnimation(frames, new GifEncodeOptions { FrameDifferencing = false }).Length;
        output.WriteLine($"differencing {with} bytes, full frames {without} bytes");
        Assert.True(with * 3 < without);
    }

    /// <summary>256 opaque colors leave no palette slot for "unchanged": the encoder must stay lossless anyway.</summary>
    [Theory]
    [InlineData(GifPaletteMode.Global), InlineData(GifPaletteMode.PerFrame)]
    public void EncodeAnimationFullPalette(GifPaletteMode palette)
    {
        var frames = new List<RawImage>();
        for (int k = 0; k < 4; k++)
        {
            var d = new byte[32 * 16 * 3];
            for (int i = 0; i < 32 * 16; i++)
            {
                int c = (i + k * 37) % 256;                    // all 256 colors in every frame, shifted
                if (i >= 256) c = i % 256;                     // lower half static
                d[i * 3] = (byte)c; d[i * 3 + 1] = (byte)(c * 3); d[i * 3 + 2] = (byte)(255 - c);
            }
            frames.Add(new RawImage(32, 16, ColorModel.Rgb, d));
        }
        var gif = Gif.EncodeAnimation(frames.Select(f => new GifInputFrame(f, 100)), new GifEncodeOptions { PaletteMode = palette });
        var anim = Gif.DecodeAnimation(gif);
        for (int i = 0; i < frames.Count; i++) AssertEqual(ToRgba(frames[i]), anim.Frames[i].Image!);
        Assert.Equal(8, anim.Frames[1].Height);                // only the moving half is stored
        var oracle = OracleDecode(gif, ".anim.gif");
        if (oracle != null) AssertEqual(Stack(frames.Select(f => ToRgba(f)).ToList()), oracle);
    }

    [Theory]
    [InlineData(GifPaletteMode.Global, GifDither.None, 29.0)]       // measured 30.1 dB
    [InlineData(GifPaletteMode.PerFrame, GifDither.None, 29.0)]     // 30.3 dB
    [InlineData(GifPaletteMode.Global, GifDither.FloydSteinberg, 27.5)]     // 28.4 dB
    [InlineData(GifPaletteMode.PerFrame, GifDither.FloydSteinberg, 27.5)]   // 28.7 dB
    public void EncodeAnimationQuantized(GifPaletteMode palette, GifDither dither, double minPsnr)
    {
        foreach (bool transparency in new[] { false, true })
        {
            var frames = Movie(6, 96, 64, 256, transparency, transparency ? ColorModel.Rgba : ColorModel.Rgb);
            var gif = Gif.EncodeAnimation(frames.Select(f => new GifInputFrame(f, 70)), new GifEncodeOptions { PaletteMode = palette, Dither = dither });
            var anim = Gif.DecodeAnimation(gif);
            Assert.Equal(frames.Count, anim.Frames.Count);
            var decoded = Stack(anim.Frames.Select(f => f.Image!).ToList());
            var expected = Stack(frames.Select(f => ToRgba(f)).ToList());
            var e = expected.GetData<byte>(); var b = decoded.GetData<byte>();
            for (int i = 3; i < e.Length; i += 4) Assert.Equal(e[i], b[i]);
            double psnr = ColorPsnr(expected, decoded);
            output.WriteLine($"{palette} {dither} transparency={transparency}: {psnr:F2} dB, {gif.Length} bytes");
            Assert.True(psnr > minPsnr, $"PSNR {psnr:F2} dB");
            var oracle = OracleDecode(gif, ".anim.gif");
            if (oracle != null) AssertEqual(decoded, oracle);
        }
    }

    [Fact]
    public void PerFramePaletteEncoderIsLazy()
    {
        var ms = new MemoryStream();
        long whenThirdWasPulled = -1;
        IEnumerable<GifInputFrame> Source()
        {
            int n = 0;
            foreach (var f in Movie(5, 20, 12, 4, false))
            {
                if (++n == 3) whenThirdWasPulled = ms.Length;
                yield return new GifInputFrame(f, 30);
            }
        }
        Gif.EncodeAnimation(Source(), ms, new GifEncodeOptions { PaletteMode = GifPaletteMode.PerFrame });
        Assert.True(whenThirdWasPulled > 40, "the first frame should be on the stream before the third is requested");
        Assert.Equal(5, Gif.GetInfo(ms.ToArray()).FrameCount);
    }

    // ------------------------------------------------------------------ robustness

    static void MustFailCleanly(byte[] data)
    {
        ImageInfo? info = null;
        RawImage? first = null;
        try { info = Gif.GetInfo(data); } catch (ImageFormatException) { } catch (ImageNotSupportedException) { }
        try { first = Gif.Decode(data); } catch (ImageFormatException) { } catch (ImageNotSupportedException) { }
        if (info != null && first != null)
            Assert.Equal((info.Value.Width, info.Value.Height, info.Value.Channels), (first.Width, first.Height, first.Channels));
        foreach (var mode in new[] { GifFrameMode.Composited, GifFrameMode.Indexed })
        {
            try
            {
                var a = Gif.DecodeAnimation(data, new GifDecodeOptions { FrameMode = mode });
                Assert.Equal(info!.Value.FrameCount, a.Frames.Count);
            }
            catch (ImageFormatException) { }
            catch (ImageNotSupportedException) { }
        }
        try { foreach (var _ in Gif.EnumerateFrames(data)) { } } catch (ImageFormatException) { } catch (ImageNotSupportedException) { }
    }

    [Theory, MemberData(nameof(Fixtures))]
    public void TruncatedInputFailsCleanly(string file)
    {
        var data = File.ReadAllBytes(Fixture(file));
        var sw = Stopwatch.StartNew();
        int step = Math.Max(1, data.Length / 150);
        for (int len = 0; len < data.Length; len += len < 64 ? 1 : step)
            MustFailCleanly(data.AsSpan(0, len).ToArray());
        Assert.True(sw.Elapsed.TotalSeconds < 20, $"took {sw.Elapsed.TotalSeconds:F1}s");
    }

    [Theory, MemberData(nameof(Fixtures))]
    public void CorruptedInputFailsCleanly(string file)
    {
        var original = File.ReadAllBytes(Fixture(file));
        var rnd = new Random(12345 + original.Length);
        var sw = Stopwatch.StartNew();
        for (int round = 0; round < 150; round++)
        {
            var data = (byte[])original.Clone();
            int flips = 1 + round % 4;
            for (int i = 0; i < flips; i++)
            {
                // half of the damage goes to the first bytes, where the structure lives
                int pos = round % 2 == 0 ? rnd.Next(Math.Min(data.Length, 64)) : rnd.Next(data.Length);
                data[pos] = round % 3 == 0 ? (byte)rnd.Next(256) : (byte)(data[pos] ^ 1 << rnd.Next(8));
            }
            MustFailCleanly(data);
        }
        Assert.True(sw.Elapsed.TotalSeconds < 30, $"took {sw.Elapsed.TotalSeconds:F1}s");
    }

    [Fact]
    public void LyingHeadersDoNotAllocate()
    {
        // 65535 x 65535 screen and frame, a handful of data bytes
        var data = new List<byte>();
        data.AddRange("GIF89a"u8.ToArray());
        data.AddRange(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x80, 0, 0, 0, 0, 0, 255, 255, 255 });
        data.AddRange(new byte[] { 0x2C, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0, 2, 2, 0x4C, 0x01, 0, 0x3B });
        var bytes = data.ToArray();
        long before = GC.GetTotalAllocatedBytes(true);
        var info = Gif.GetInfo(bytes);
        Assert.Equal((65535, 65535, 1), (info.Width, info.Height, info.FrameCount));
        Assert.Throws<ImageFormatException>(() => Gif.Decode(bytes));
        Assert.Throws<ImageFormatException>(() => Gif.DecodeAnimation(bytes));
        Assert.Throws<ImageFormatException>(() => Gif.DecodeAnimation(bytes, new GifDecodeOptions { FrameMode = GifFrameMode.Indexed }));
        Assert.True(GC.GetTotalAllocatedBytes(true) - before < 1 << 20);

        // small frame on a huge screen
        bytes[24] = 1; bytes[25] = 0; bytes[26] = 1; bytes[27] = 0;
        Assert.Throws<ImageFormatException>(() => Gif.Decode(bytes));
        // plausible but above the caller's limit
        bytes[6] = 100; bytes[7] = 0; bytes[8] = 100; bytes[9] = 0;
        Assert.Equal(100, Gif.Decode(bytes).Width);
        Assert.Throws<ImageNotSupportedException>(() => Gif.Decode(bytes, new GifDecodeOptions { MaxPixels = 9999 }));
    }

    [Fact]
    public void StructuralGarbageThrows()
    {
        Assert.False(Gif.IsMatch("GIF88a"u8));
        Assert.False(Gif.IsMatch("GIF8"u8));
        Assert.Throws<ImageFormatException>(() => Gif.Decode("PNG89a\0\0\0\0\0\0\0"u8));
        Assert.Throws<ImageFormatException>(() => Gif.Decode("GIF89a"u8));
        var good = File.ReadAllBytes(Fixture("gif/hand_transparent.gif"));
        // no image at all: header + trailer
        var none = good.AsSpan(0, 13 + 12).ToArray().Append((byte)0x3B).ToArray();
        Assert.Throws<ImageFormatException>(() => Gif.Decode(none));
        Assert.Throws<ImageFormatException>(() => Gif.GetInfo(none));
        Assert.Throws<ImageFormatException>(() => Gif.GetAnimationInfo(none));
        Assert.Throws<ImageFormatException>(() => Gif.EnumerateFrames(none).ToList());
        // unknown block where the first image should be
        var junk = (byte[])good.Clone();
        junk[25] = 0x99;
        Assert.Throws<ImageFormatException>(() => Gif.Decode(junk));
        // LZW code size out of range
        int img = Array.IndexOf(good, (byte)0x2C, 25);
        foreach (byte bad in new byte[] { 0, 12, 200 })
        {
            var b = (byte[])good.Clone();
            b[img + 10] = bad;
            Assert.Throws<ImageFormatException>(() => Gif.Decode(b));
        }
        // a code that cannot exist yet
        var code = (byte[])good.Clone();
        code[img + 12] = 0xFC;      // clear (4), then 7 as the very first code
        Assert.Throws<ImageFormatException>(() => Gif.Decode(code));
    }

    // ------------------------------------------------------------------ speed (PIXCODEC_PERF=1)

    [Fact]
    public void Performance()
    {
        if (Environment.GetEnvironmentVariable("PIXCODEC_PERF") != "1") return;

        static double Best(int runs, Action a)
        {
            double best = double.MaxValue;
            for (int i = 0; i < runs; i++) { var sw = Stopwatch.StartNew(); a(); best = Math.Min(best, sw.Elapsed.TotalSeconds); }
            return best;
        }

        const int W = 2000, H = 2000;
        double mp = W * H / 1e6;
        var photo = Pattern(W, H, ColorModel.Rgb, 256, seed: 9);
        foreach (var dither in new[] { GifDither.None, GifDither.FloydSteinberg })
        {
            byte[] gif = null!;
            double t = Best(3, () => gif = Encode(photo, new GifEncodeOptions { Dither = dither }));
            output.WriteLine($"encode {W}x{H} photo-like, quantize to 256, dither {dither}: {mp / t:F1} MP/s ({gif.Length / 1024} KiB)");
            RawImage back = null!;
            t = Best(5, () => back = Gif.Decode(gif));
            output.WriteLine($"decode that file: {mp / t:F1} MP/s, PSNR {Psnr(photo, back, 255):F1} dB");
        }
        {
            // an image that already has 256 colors (the decode of the above): lossless path
            var indexed = Gif.Decode(Encode(photo, new GifEncodeOptions { Dither = GifDither.None }));
            byte[] gif = null!;
            double t = Best(3, () => gif = Encode(indexed));
            output.WriteLine($"encode {W}x{H} 256 colors (exact palette): {mp / t:F1} MP/s ({gif.Length / 1024} KiB)");
            t = Best(5, () => Gif.Decode(gif));
            output.WriteLine($"decode {W}x{H} 256 colors: {mp / t:F1} MP/s");
            var inter = Encode(indexed, new GifEncodeOptions { Interlace = true });
            t = Best(5, () => Gif.Decode(inter));
            output.WriteLine($"decode {W}x{H} 256 colors interlaced: {mp / t:F1} MP/s");
            var flat = Pattern(W, H, ColorModel.Rgb, 3, seed: 2);
            t = Best(3, () => gif = Encode(flat));
            output.WriteLine($"encode {W}x{H} 27 colors, large flat areas: {mp / t:F1} MP/s ({gif.Length / 1024} KiB)");
            t = Best(5, () => Gif.Decode(gif));
            output.WriteLine($"decode {W}x{H} 27 colors: {mp / t:F1} MP/s");
        }
        {
            const int FW = 640, FH = 480, N = 100;
            double amp = (double)FW * FH * N / 1e6;
            var frames = Movie(N, FW, FH, 256, false, ColorModel.Rgb).Select(f => new GifInputFrame(f, 40)).ToList();
            foreach (var (palette, diff) in new[] { (GifPaletteMode.Global, true), (GifPaletteMode.Global, false), (GifPaletteMode.PerFrame, true) })
            {
                byte[] gif = null!;
                double t = Best(2, () => gif = Gif.EncodeAnimation(frames, new GifEncodeOptions { PaletteMode = palette, FrameDifferencing = diff, Dither = GifDither.None }));
                output.WriteLine($"encode {N} x {FW}x{FH} animation, {palette} palette, differencing {diff}: {amp / t:F1} MP/s ({gif.Length / 1024} KiB)");
                t = Best(3, () => { foreach (var f in Gif.EnumerateFrames(gif)) { } });
                output.WriteLine($"decode it, composited RGBA frames: {amp / t:F1} MP/s of canvas");
                t = Best(3, () => Gif.DecodeAnimation(gif, new GifDecodeOptions { FrameMode = GifFrameMode.Indexed }));
                output.WriteLine($"decode it, indexed frames: {amp / t:F1} MP/s of canvas");
            }
        }
    }
}
