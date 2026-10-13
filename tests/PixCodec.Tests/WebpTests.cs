using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using PixCodec;
using Xunit;
using Xunit.Abstractions;
using static PixCodec.Tests.TestUtil;

namespace PixCodec.Tests;

public class WebpTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Fixtures => Files("webp", ".webp");

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
        var img = Webp.Decode(data);
        AssertEqual(expected, img);
        Assert.Equal(SampleType.UInt8, img.SampleType);
        Assert.Equal(expected.Channels == 4 ? ColorModel.Rgba : ColorModel.Rgb, img.Color);

        int frames = Math.Max(1, FrameRefs(path).Count);
        var info = Webp.GetInfo(data);
        Assert.Equal(new ImageInfo(ImageFormat.Webp, img.Width, img.Height, img.Channels, SampleType.UInt8, img.Color, frames)
        {
            BitsPerSample = 8, IsAnimated = frames > 1 || Webp.GetAnimationInfo(data).IsAnimated,
            Orientation = img.Metadata?.Orientation ?? 0, HasIccProfile = img.Metadata?.IccProfile != null,
            DpiX = img.Metadata?.DpiX ?? 0, DpiY = img.Metadata?.DpiY ?? 0,
        }, info);
        // Any prefix either gives the same answer or asks for more; a still image without flagged metadata is
        // answered from its first chunks, an animation needs everything.
        int needed = data.Length;
        for (int n = data.Length - 1; n >= 0; n--)
        {
            try { Assert.Equal(info, Webp.GetInfo(data.AsSpan(0, n))); needed = n; }
            catch (ImageFormatException) { break; }
        }
        for (int n = 0; n < needed; n += Math.Max(1, needed / 40)) Assert.Throws<ImageFormatException>(() => Webp.GetInfo(data.AsSpan(0, n)));
        // a RIFF size beyond the data is what a prefix looks like, so it is the trailing bytes that do not count
        int riff = 8 + BitConverter.ToInt32(data, 4);
        if (info.IsAnimated) Assert.True(needed >= Math.Min(riff, data.Length) - 1);
        else if (!data.AsSpan(12, 4).SequenceEqual("VP8X"u8)) Assert.True(needed <= 64, $"needs {needed} of {data.Length} bytes");
        Assert.True(Webp.IsMatch(data));
        Assert.Equal(ImageFormat.Webp, ImageCodec.DetectFormat(data));
        AssertEqual(expected, ImageCodec.Decode(data));
    }

    /// <summary>
    /// The conformance vectors of libwebp; references are hashes of libwebp's output (vectors/refs.txt). The vector
    /// files themselves are not in the repository (their upstream carries no licence file): fetch them with
    /// tests/oracle/fetch_webp_vectors.sh. Without them this test checks nothing.
    /// </summary>
    [Fact]
    public void VectorsMatchReference()
    {
        var dir = Fixture(Path.Combine("webp", "vectors"));
        foreach (var refLine in File.ReadAllLines(Path.Combine(dir, "refs.txt")))
        {
            var line = refLine.Split(' ');
            var path = Path.Combine(dir, line[0]);
            if (!File.Exists(path)) continue;
            var data = File.ReadAllBytes(path);
            var img = Webp.Decode(data);
            Assert.Equal((int.Parse(line[1]), int.Parse(line[2]), int.Parse(line[3])), (img.Width, img.Height, img.Channels));
            Assert.True(line[4] == Convert.ToHexString(SHA256.HashData(img.GetBytes())).ToLowerInvariant(), line[0]);
            var info = Webp.GetInfo(data);
            Assert.Equal((img.Width, img.Height, img.Channels, 1), (info.Width, info.Height, info.Channels, info.FrameCount));
        }
    }

    [Theory, MemberData(nameof(Fixtures))]
    public void AnimationMatchesReference(string file)
    {
        var path = Fixture(file);
        var data = File.ReadAllBytes(path);
        var refs = FrameRefs(path);
        var anim = Webp.DecodeAnimation(data);
        var info = Webp.GetAnimationInfo(data);
        Assert.Equal(anim.Frames.Count, info.FrameCount);
        Assert.Equal((anim.Width, anim.Height, anim.LoopCount, anim.BackgroundColor, anim.IsAnimated), (info.Width, info.Height, info.LoopCount, info.BackgroundColor, info.IsAnimated));
        if (refs.Count == 0)
        {
            // a still image is an animation of one frame: the same pixels as Decode, always with an alpha channel
            var single = Assert.Single(anim.Frames);
            var still = Webp.Decode(data);
            Assert.Equal(ColorModel.Rgba, single.Image.Color);
            Assert.Equal((still.Width, still.Height), (single.Image.Width, single.Image.Height));
            var a = (byte[])single.Image.Data;
            var s = (byte[])still.Data;
            for (int i = 0; i < still.Width * still.Height; i++)
            {
                Assert.True(a.AsSpan(i * 4, 3).SequenceEqual(s.AsSpan(i * still.Channels, 3)));
                if (still.Channels == 4) Assert.Equal(s[i * 4 + 3], a[i * 4 + 3]);
            }
            return;
        }

        Assert.Equal(refs.Count, anim.Frames.Count);
        for (int i = 0; i < refs.Count; i++)
        {
            Assert.Equal(i, anim.Frames[i].Index);
            Assert.Equal(ColorModel.Rgba, anim.Frames[i].Image.Color);
            AssertEqual(refs[i], anim.Frames[i].Image);
        }
        // The lazy variant yields the same frames, each with pixels of its own.
        int n = 0;
        foreach (var f in Webp.EnumerateFrames(data))
        {
            AssertEqual(refs[n], f.Image);
            Assert.NotSame(anim.Frames[n].Image.Data, f.Image.Data);
            n++;
        }
        Assert.Equal(refs.Count, n);
    }

    /// <summary>Re-does the compositing from the raw frames with a naive implementation of the rules.</summary>
    [Theory, MemberData(nameof(Fixtures))]
    public void RawFramesComposeToReference(string file)
    {
        var path = Fixture(file);
        var data = File.ReadAllBytes(path);
        var refs = FrameRefs(path);
        if (refs.Count == 0) return;
        var anim = Webp.DecodeAnimation(data, new WebpDecodeOptions { FrameMode = WebpFrameMode.Raw });
        Assert.Equal(refs.Count, anim.Frames.Count);
        int w = anim.Width, h = anim.Height;
        var canvas = new byte[w * h * 4];
        WebpFrame? prev = null;
        bool prevKey = false;
        for (int k = 0; k < anim.Frames.Count; k++)
        {
            var f = anim.Frames[k];
            Assert.Equal((f.Width, f.Height), (f.Image.Width, f.Image.Height));
            Assert.Equal(f.HasAlpha ? ColorModel.Rgba : ColorModel.Rgb, f.Image.Color);
            Assert.True(f.X % 2 == 0 && f.Y % 2 == 0 && f.X + f.Width <= w && f.Y + f.Height <= h);
            bool full = f.Width == w && f.Height == h;
            bool key = prev == null || ((!f.HasAlpha || f.Blend == WebpBlend.NoBlend) && full)
                || (prev.Disposal == WebpDisposal.Background && ((prev.Width == w && prev.Height == h) || prevKey));
            if (key) Array.Clear(canvas);
            else if (prev!.Disposal == WebpDisposal.Background)
                for (int y = prev.Y; y < prev.Y + prev.Height; y++) canvas.AsSpan((y * w + prev.X) * 4, prev.Width * 4).Clear();
            var px = (byte[])f.Image.Data;
            int ch = f.Image.Channels;
            for (int y = 0; y < f.Height; y++)
                for (int x = 0; x < f.Width; x++)
                {
                    var s = px.AsSpan((y * f.Width + x) * ch, ch);
                    var d = canvas.AsSpan(((f.Y + y) * w + f.X + x) * 4, 4);
                    int sa = ch == 4 ? s[3] : 255;
                    bool cleared = !key && prev!.Disposal == WebpDisposal.Background
                        && f.X + x >= prev.X && f.X + x < prev.X + prev.Width && f.Y + y >= prev.Y && f.Y + y < prev.Y + prev.Height;
                    if (key || f.Blend == WebpBlend.NoBlend || sa == 255 || cleared)
                    {
                        s.Slice(0, 3).CopyTo(d);
                        d[3] = (byte)sa;
                    }
                    else if (sa != 0)
                    {
                        // libwebp's integer version of "source over destination" for straight alpha
                        int da = d[3] * (256 - sa) >> 8, ba = sa + da;
                        uint scale = (1u << 24) / (uint)ba;
                        for (int c = 0; c < 3; c++) d[c] = (byte)((uint)(s[c] * sa + d[c] * da) * scale >> 24);
                        d[3] = (byte)ba;
                    }
                }
            prev = f; prevKey = key;
            AssertEqual(refs[k], new RawImage(w, h, ColorModel.Rgba, (byte[])canvas.Clone()));
        }
    }

    [Fact]
    public void AnimationProperties()
    {
        byte[] Load(string name) => File.ReadAllBytes(Fixture("webp/" + name));

        var a = Webp.DecodeAnimation(Load("anim_lossless.webp"));
        Assert.Equal((32, 24, 5), (a.Width, a.Height, a.FrameCount));
        Assert.True(a.IsAnimated);
        Assert.Equal(0, a.LoopCount);
        Assert.Equal(new[] { 30, 70, 110, 200, 40 }, a.Frames.Select(f => f.DurationMs));
        Assert.All(a.Frames, f => Assert.True(f.IsLossless));
        Assert.Equal(3, Webp.GetAnimationInfo(Load("anim_lossy.webp")).LoopCount);
        Assert.All(Webp.DecodeAnimation(Load("anim_lossy.webp")).Frames, f => Assert.False(f.IsLossless));

        var bg = Webp.GetAnimationInfo(Load("anim_background.webp"));
        Assert.Equal(65535, bg.LoopCount);
        Assert.Equal(((byte)10, (byte)20, (byte)30, (byte)40), bg.BackgroundColor);
        Assert.Equal(new[] { 65535, 65535 }, Webp.DecodeAnimation(Load("anim_background.webp")).Frames.Select(f => f.DurationMs));
        Assert.Equal(new[] { 0, 1, 16777215 / 2 }, Webp.DecodeAnimation(Load("anim_palette.webp")).Frames.Select(f => f.DurationMs));

        var m = Webp.DecodeAnimation(Load("hand_anim_blend_keep_soft_lossless.webp"), new WebpDecodeOptions { FrameMode = WebpFrameMode.Raw });
        Assert.Equal(7, m.LoopCount);
        Assert.Equal(((byte)0x40, (byte)0x20, (byte)0x10, (byte)0x80), m.BackgroundColor);     // stored as B, G, R, A
        Assert.Equal(new[] { 10, 20, 30, 40, 50, 60, 70 }, m.Frames.Select(f => f.DurationMs));
        Assert.Equal((4, 2, 12, 10), (m.Frames[1].X, m.Frames[1].Y, m.Frames[1].Width, m.Frames[1].Height));
        Assert.Equal((8, 10, 9, 7), (m.Frames[6].X, m.Frames[6].Y, m.Frames[6].Width, m.Frames[6].Height));
        Assert.Equal(new[] { 1, 0, 0, 1, 0, 0, 1 }, m.Frames.Select(f => (int)f.Blend));          // blend unless i % 3 == 0
        Assert.Equal(new[] { 0, 1, 0, 1, 0, 1, 0 }, m.Frames.Select(f => (int)f.Disposal));
        Assert.All(m.Frames, f => Assert.True(f.HasAlpha && f.IsLossless));
        var lossy = Webp.DecodeAnimation(Load("hand_anim_noblend_dispose_none_lossy.webp"), new WebpDecodeOptions { FrameMode = WebpFrameMode.Raw });
        Assert.Equal(new[] { false, false, false, false, false, true, false }, lossy.Frames.Select(f => f.HasAlpha));
        Assert.All(lossy.Frames, f => Assert.Equal(f.HasAlpha ? 4 : 3, f.Image.Channels));

        // a still image through the animation API
        var still = Webp.DecodeAnimation(Load("lossy_q050.webp"));
        Assert.False(still.IsAnimated);
        Assert.Equal((1, 1, false), (still.FrameCount, still.LoopCount, still.HasAlpha));
        Assert.Equal((0, 0, 48, 40, 0), (still.Frames[0].X, still.Frames[0].Y, still.Frames[0].Width, still.Frames[0].Height, still.Frames[0].DurationMs));
        Assert.True(Webp.GetAnimationInfo(Load("lossy_alpha_q100.webp")).HasAlpha);

        // an animation whose frames are opaque and cover the canvas decodes to Rgb, any other one to Rgba
        Assert.Equal(3, Webp.GetInfo(Load("hand_anim_opaque_no_alpha_flag.webp")).Channels);
        Assert.Equal(4, Webp.GetInfo(Load("hand_anim_no_alpha_flag.webp")).Channels);
    }

    [Fact]
    public void Metadata()
    {
        byte[] Load(string name) => File.ReadAllBytes(Fixture("webp/" + name));

        var data = Load("edge_metadata.webp");
        var img = Webp.Decode(data);
        Assert.Equal(600, img.Metadata!.IccProfile!.Length);
        Assert.Equal((byte)199, img.Metadata.IccProfile[199]);
        Assert.Equal(6, img.Metadata.Orientation);
        Assert.Equal((72.0, 0.0), (img.Metadata.DpiX, img.Metadata.DpiY));     // the fixture's EXIF has an XResolution only
        Assert.Equal((72.0, 0.0), (Webp.GetInfo(data).DpiX, Webp.GetInfo(data).DpiY));
        Assert.True(img.Metadata.Exif!.AsSpan(0, 4).SequenceEqual("II*\0"u8));
        Assert.StartsWith("<?xpacket", System.Text.Encoding.ASCII.GetString(Webp.GetXmp(data)!));
        var info = Webp.GetAnimationInfo(data);
        Assert.Equal(img.Metadata.IccProfile, info.IccProfile);
        Assert.Equal(img.Metadata.Exif, info.Exif);
        Assert.Equal(Webp.GetXmp(data), info.Xmp);

        // the JPEG style "Exif\0\0" prefix is dropped; big endian EXIF
        var before = Webp.Decode(Load("edge_metadata_before_image.webp")).Metadata!;
        Assert.True(before.Exif!.AsSpan(0, 4).SequenceEqual("MM\0*"u8));
        Assert.Equal(3, before.Orientation);
        Assert.Null(before.IccProfile);
        Assert.Equal("<x/>"u8.ToArray(), Webp.GetXmp(Load("edge_metadata_before_image.webp")));

        // metadata chunks count only when the VP8X header flags them (GetInfo must know from the header whether
        // to look for them) - unless asked to take them wherever they are; of duplicates the first one
        var unflagged = Load("edge_metadata_without_flags.webp");
        Assert.Null(Webp.Decode(unflagged).Metadata);
        Assert.Null(Webp.GetXmp(unflagged));
        Assert.Null(Webp.GetAnimationInfo(unflagged).Exif);
        Assert.Equal((0, false), (Webp.GetInfo(unflagged).Orientation, Webp.GetInfo(unflagged).HasIccProfile));
        var accept = new WebpDecodeOptions { AcceptUnflaggedMetadata = true };
        var noFlags = Webp.Decode(unflagged, accept).Metadata!;
        Assert.Equal("icc"u8.ToArray(), noFlags.IccProfile);
        Assert.Equal(8, noFlags.Orientation);
        Assert.Equal("xmp"u8.ToArray(), Webp.GetXmp(unflagged, accept));
        Assert.Equal("xmp"u8.ToArray(), Webp.GetAnimationInfo(unflagged, accept).Xmp);
        Assert.Equal(8, Webp.DecodeAnimation(unflagged, accept).Frames[0].Image.Width > 0 ? Webp.GetAnimationInfo(unflagged, accept).Exif is { } e ? e[18] : -1 : -1);
        AssertEqual(Webp.Decode(unflagged), Webp.Decode(unflagged, accept));
        // also in a file without VP8X header, where chunks after the image are otherwise just skipped
        var simple = Load("lossy_q050.webp");
        var withExif = new byte[simple.Length + 8 + 26];
        simple.CopyTo(withExif, 0);
        "EXIF"u8.CopyTo(withExif.AsSpan(simple.Length));
        withExif[simple.Length + 4] = 26;
        new byte[] { (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0, 1, 0, 0x12, 0x01, 3, 0, 1, 0, 0, 0, 7, 0, 0, 0, 0, 0, 0, 0 }.CopyTo(withExif, simple.Length + 8);
        BinaryPrimitives.WriteUInt32LittleEndian(withExif.AsSpan(4), (uint)withExif.Length - 8);
        Assert.Null(Webp.Decode(withExif).Metadata);
        Assert.Equal(7, Webp.Decode(withExif, accept).Metadata!.Orientation);
        var dup = Webp.Decode(Load("edge_duplicate_metadata.webp")).Metadata!;
        Assert.Equal("first"u8.ToArray(), dup.IccProfile);
        Assert.Equal(2, dup.Orientation);

        Assert.Null(Webp.Decode(Load("lossy_q050.webp")).Metadata);
        Assert.Null(Webp.GetXmp(Load("lossy_q050.webp")));
        var anim = Webp.GetAnimationInfo(Load("hand_anim_unknown_chunks.webp"));
        Assert.Equal("fake icc profile"u8.ToArray(), anim.IccProfile);
        Assert.Equal("<x:xmpmeta/>"u8.ToArray(), anim.Xmp);
        Assert.Equal(6, Webp.Decode(Load("hand_anim_unknown_chunks.webp")).Metadata!.Orientation);
    }

    // ------------------------------------------------------------------ test images

    /// <summary>Smooth content with texture and some hard edges, like a photograph.</summary>
    static RawImage Photo(int w, int h, ColorModel color, int seed = 1)
    {
        int ch = RawImage.ChannelCount(color);
        var rng = new Random(seed);
        var d = new byte[w * h * ch];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                // texture and edges are the same in all channels: what 4:2:0 chroma cannot carry would only cap the PSNR
                double detail = 30 * (((x / 9 + y / 11) & 1) - 0.5) + rng.Next(-5, 6);
                for (int c = 0; c < ch; c++)
                {
                    double v = 128 + 70 * Math.Sin(x / (17.0 + 3 * c) + seed) * Math.Cos(y / (13.0 + 2 * c)) + detail;
                    bool isAlpha = (color == ColorModel.Rgba && c == 3) || (color == ColorModel.GrayAlpha && c == 1);
                    if (isAlpha) v = x < w / 3 && y < h / 3 ? 0 : x > w / 2 && y > h / 2 ? 255 : 128 + 127 * Math.Sin(x / 5.0) * Math.Cos(y / 6.0);
                    d[(y * w + x) * ch + c] = (byte)Math.Clamp(v, 0, 255);
                }
            }
        return new RawImage(w, h, color, d);
    }

    /// <summary>Flat areas in a limited number of colors.</summary>
    static RawImage Drawing(int w, int h, ColorModel color, int colors, int seed = 1)
    {
        int ch = RawImage.ChannelCount(color);
        var rng = new Random(seed);
        var pal = new byte[colors * ch];
        rng.NextBytes(pal);
        var idx = new int[w * h];
        for (int k = 0; k < colors * 3; k++)
        {
            int x0 = rng.Next(w), y0 = rng.Next(h), x1 = Math.Min(w, x0 + 1 + rng.Next(Math.Max(1, w / 2))), y1 = Math.Min(h, y0 + 1 + rng.Next(Math.Max(1, h / 3)));
            for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) idx[y * w + x] = k % colors;
        }
        var d = new byte[w * h * ch];
        for (int i = 0; i < idx.Length; i++) pal.AsSpan(idx[i] * ch, ch).CopyTo(d.AsSpan(i * ch));
        return new RawImage(w, h, color, d);
    }

    /// <summary>What a decoder returns for the image: gray expanded to RGB.</summary>
    static RawImage Expanded(RawImage img)
    {
        if (img.Color == ColorModel.Rgb || img.Color == ColorModel.Rgba) return img;
        var s = (byte[])img.Data;
        int n = img.Width * img.Height, ch = img.Color == ColorModel.Gray ? 3 : 4;
        var d = new byte[n * ch];
        for (int i = 0; i < n; i++)
        {
            byte g = s[i * img.Channels];
            d[i * ch] = d[i * ch + 1] = d[i * ch + 2] = g;
            if (ch == 4) d[i * ch + 3] = s[i * 2 + 1];
        }
        return new RawImage(img.Width, img.Height, ch == 4 ? ColorModel.Rgba : ColorModel.Rgb, d);
    }

    static byte[] Encode(RawImage img, WebpEncodeOptions? opt = null)
    {
        using var ms = new MemoryStream();
        Webp.Encode(img, ms, opt);
        return ms.ToArray();
    }

    // ------------------------------------------------------------------ lossless encoder

    public static IEnumerable<object[]> LosslessCases()
    {
        foreach (var color in new[] { ColorModel.Gray, ColorModel.GrayAlpha, ColorModel.Rgb, ColorModel.Rgba })
            foreach (int method in new[] { 0, 1, 2, 4, 6 })
                yield return new object[] { color, method };
    }

    [Theory, MemberData(nameof(LosslessCases))]
    public void LosslessRoundTrip(ColorModel color, int method)
    {
        var opt = new WebpEncodeOptions { Lossless = true, Method = method };
        var images = new List<RawImage>
        {
            Photo(1, 1, color), Photo(1, 17, color), Photo(17, 1, color), Photo(16, 16, color), Photo(17, 33, color), Photo(65, 65, color), Photo(130, 9, color),
            Drawing(40, 30, color, 2), Drawing(41, 30, color, 4, 2), Drawing(43, 30, color, 16, 3), Drawing(64, 48, color, 200, 4), Drawing(3, 3, color, 1, 5),
        };
        foreach (var img in images)
        {
            var data = Encode(img, opt);
            var expected = Expanded(img);
            var back = Webp.Decode(data);
            Assert.Equal(expected.Color, back.Color);
            AssertEqual(expected, back);
            Assert.Equal(new ImageInfo(ImageFormat.Webp, img.Width, img.Height, expected.Channels, SampleType.UInt8, expected.Color) { BitsPerSample = 8 }, Webp.GetInfo(data));
            var oracle = OracleDecode(data, ".webp");
            if (oracle == null) return;
            AssertEqual(expected, oracle);
        }
    }

    [Fact]
    public void LosslessLargerImages()
    {
        // enough pixels for the entropy image, several clusters and long matches
        foreach (int method in new[] { 0, 2, 4, 5 })
        {
            var opt = new WebpEncodeOptions { Lossless = true, Method = method };
            foreach (var img in new[] { Photo(300, 220, ColorModel.Rgb, 7), Photo(260, 200, ColorModel.Rgba, 8), Photo(256, 256, ColorModel.Gray, 9), Drawing(320, 240, ColorModel.Rgb, 12, 10), Drawing(320, 240, ColorModel.Rgba, 180, 11) })
            {
                var data = Encode(img, opt);
                AssertEqual(Expanded(img), Webp.Decode(data));
                var oracle = OracleDecode(data, ".webp");
                if (oracle != null) AssertEqual(Expanded(img), oracle);
            }
        }
    }

    [Fact]
    public void LosslessBigImages()
    {
        // above half a megapixel the encoder probes whether the hash chain is worth building: a drawing says yes,
        // a photograph no, and both must come back exactly
        foreach (var img in new[] { Drawing(1100, 520, ColorModel.Rgb, 60, 21), Photo(900, 640, ColorModel.Rgb, 22), Photo(800, 700, ColorModel.Rgba, 23) })
            foreach (int method in new[] { 1, 4 })
            {
                var data = Encode(img, new WebpEncodeOptions { Lossless = true, Method = method });
                AssertEqual(img, Webp.Decode(data));
                if (method == 4)
                {
                    var oracle = OracleDecode(data, ".webp");
                    if (oracle != null) AssertEqual(img, oracle);
                }
            }
    }

    [Fact]
    public void LosslessNoise()
    {
        // every symbol near the longest code length, in all four channels: the bit reader has to keep up
        foreach (var color in new[] { ColorModel.Rgb, ColorModel.Rgba })
        {
            var img = RawImage.Create(300, 200, color, SampleType.UInt8);
            var rng = new Random(77);
            var d = img.GetData<byte>();
            for (int i = 0; i < d.Length; i++) d[i] = (byte)(rng.Next(7) == 0 ? rng.Next(256) : (int)(128 + 100 * Math.Tan(rng.NextDouble() * 3 - 1.5)));
            foreach (int method in new[] { 0, 4 })
            {
                var data = Encode(img, new WebpEncodeOptions { Lossless = true, Method = method });
                AssertEqual(img, Webp.Decode(data));
                var oracle = OracleDecode(data, ".webp");
                if (oracle != null) AssertEqual(img, oracle);
            }
        }
    }

    [Fact]
    public void LosslessEffortPaysOff()
    {
        var photo = Photo(256, 192, ColorModel.Rgb, 3);
        var drawing = Drawing(256, 192, ColorModel.Rgb, 40, 3);
        foreach (var img in new[] { photo, drawing })
        {
            int fast = Encode(img, new WebpEncodeOptions { Lossless = true, Method = 0 }).Length;
            int normal = Encode(img, new WebpEncodeOptions { Lossless = true, Method = 4 }).Length;
            output.WriteLine($"lossless {img.Width}x{img.Height}: method 0 {fast} bytes, method 4 {normal} bytes, raw {img.Width * img.Height * 3}");
            Assert.True(normal < fast);
            Assert.True(normal < img.Width * img.Height * 3 / 2);
        }
        // a solid image costs next to nothing
        Assert.True(Encode(RawImage.Create(500, 300, ColorModel.Rgb, SampleType.UInt8), new WebpEncodeOptions { Lossless = true }).Length < 100);
    }

    [Fact]
    public void LosslessExactKeepsHiddenColors()
    {
        var img = Photo(40, 30, ColorModel.Rgba, 5);
        var exact = Encode(img, new WebpEncodeOptions { Lossless = true });
        AssertEqual(img, Webp.Decode(exact));
        var loose = Encode(img, new WebpEncodeOptions { Lossless = true, Exact = false });
        Assert.True(loose.Length < exact.Length);
        var back = (byte[])Webp.Decode(loose).Data;
        var src = (byte[])img.Data;
        for (int i = 0; i < src.Length; i += 4)
        {
            Assert.Equal(src[i + 3], back[i + 3]);
            if (src[i + 3] != 0) Assert.True(src.AsSpan(i, 3).SequenceEqual(back.AsSpan(i, 3)));
        }
        var oracle = OracleDecode(loose, ".webp");
        if (oracle != null) AssertEqual(Webp.Decode(loose), oracle);
    }

    [Fact]
    public void AlphaChannelIsKeptEvenWhenOpaque()
    {
        var img = Photo(20, 12, ColorModel.Rgba);
        var d = (byte[])img.Data;
        for (int i = 3; i < d.Length; i += 4) d[i] = 255;
        foreach (bool lossless in new[] { true, false })
        {
            var data = Encode(img, new WebpEncodeOptions { Lossless = lossless });
            Assert.Equal(ColorModel.Rgba, Webp.GetInfo(data).Color);
            var back = Webp.Decode(data);
            Assert.Equal(ColorModel.Rgba, back.Color);
            Assert.All(Enumerable.Range(0, 20 * 12), i => Assert.Equal(255, ((byte[])back.Data)[i * 4 + 3]));
        }
        Assert.Equal(ColorModel.Rgb, Webp.GetInfo(Encode(Photo(20, 12, ColorModel.Rgb), new WebpEncodeOptions { Lossless = true })).Color);
    }

    // ------------------------------------------------------------------ lossy encoder

    public static IEnumerable<object[]> LossyCases()
    {
        foreach (int method in new[] { 0, 1, 2, 4, 6 })
            foreach (int quality in new[] { 0, 30, 75, 95, 100 })
                yield return new object[] { method, quality };
    }

    [Theory, MemberData(nameof(LossyCases))]
    public void LossyDecodesIdenticallyEverywhere(int method, int quality)
    {
        var opt = new WebpEncodeOptions { Quality = quality, Method = method };
        foreach (var img in new[] { Photo(1, 1, ColorModel.Rgb), Photo(17, 33, ColorModel.Rgb, 2), Photo(64, 48, ColorModel.Rgb, 3), Photo(100, 37, ColorModel.Gray, 4), Drawing(49, 50, ColorModel.Rgb, 9, 5) })
        {
            var data = Encode(img, opt);
            var back = Webp.Decode(data);
            Assert.Equal(ColorModel.Rgb, back.Color);
            Assert.Equal((img.Width, img.Height), (back.Width, back.Height));
            if (img.Width >= 64 && img.Color == ColorModel.Rgb)
            {
                double psnr = Psnr(img, back, 255);
                Assert.True(psnr > (quality >= 95 ? 38 : quality >= 75 ? 33 : quality >= 30 ? 28 : 20), $"PSNR {psnr:F2} at quality {quality}");
            }
            var oracle = OracleDecode(data, ".webp");
            if (oracle == null) return;
            AssertEqual(back, oracle);
        }
    }

    [Fact]
    public void LossyOptions()
    {
        var img = Photo(120, 150, ColorModel.Rgb, 6);
        var variants = new (string name, WebpEncodeOptions opt)[]
        {
            ("partitions 2", new WebpEncodeOptions { Partitions = 2 }), ("partitions 4", new WebpEncodeOptions { Partitions = 4 }), ("partitions 8", new WebpEncodeOptions { Partitions = 8 }),
            ("segments 2", new WebpEncodeOptions { Segments = 2 }), ("segments 3", new WebpEncodeOptions { Segments = 3 }), ("segments 4", new WebpEncodeOptions { Segments = 4, Quality = 40 }),
            ("simple filter", new WebpEncodeOptions { SimpleFilter = true, Quality = 30 }), ("no filter", new WebpEncodeOptions { FilterStrength = 0 }),
            ("strong filter", new WebpEncodeOptions { FilterStrength = 63, FilterSharpness = 3 }), ("sharpness 7", new WebpEncodeOptions { FilterSharpness = 7, Quality = 20 }),
            ("everything", new WebpEncodeOptions { Partitions = 8, Segments = 4, SimpleFilter = true, FilterStrength = 40, FilterSharpness = 5, Quality = 50, Method = 6 }),
        };
        foreach (var (name, opt) in variants)
        {
            var data = Encode(img, opt);
            var back = Webp.Decode(data);
            double psnr = Psnr(img, back, 255);
            output.WriteLine($"{name}: {data.Length} bytes, {psnr:F2} dB");
            Assert.True(psnr > 27, name);
            var oracle = OracleDecode(data, ".webp");
            if (oracle != null) AssertEqual(back, oracle);
        }
    }

    /// <summary>
    /// Parts of the key frame syntax that no encoder at hand writes (libwebp always sends absolute segment values, a
    /// segment map, skip flags ...): our encoder writes them in a test mode and libwebp has to decode the files to
    /// the very pixels our decoder gives.
    /// </summary>
    [Fact]
    public void UnusualLossySyntaxDecodesLikeLibwebp()
    {
        var img = Photo(120, 104, ColorModel.Rgb, 31);
        var variants = new (string name, Vp8Syntax sx, WebpEncodeOptions opt)[]
        {
            ("segment deltas", new Vp8Syntax { SegmentDeltas = true }, new WebpEncodeOptions { Segments = 4, Quality = 50 }),
            ("segment deltas, own filter levels", new Vp8Syntax { SegmentDeltas = true, SegmentFilterLevels = new[] { 0, 63, 7, 30 } }, new WebpEncodeOptions { Segments = 4, Quality = 30 }),
            ("absolute, own filter levels", new Vp8Syntax { SegmentFilterLevels = new[] { 40, 0, 1, 63 } }, new WebpEncodeOptions { Segments = 4, Quality = 30 }),
            ("three segments as deltas, simple filter", new Vp8Syntax { SegmentDeltas = true }, new WebpEncodeOptions { Segments = 3, Quality = 70, SimpleFilter = true }),
            ("segmentation without map", new Vp8Syntax { NoSegmentMap = true }, new WebpEncodeOptions { Segments = 4, Quality = 40 }),
            ("segmentation without map, deltas", new Vp8Syntax { NoSegmentMap = true, SegmentDeltas = true }, new WebpEncodeOptions { Segments = 2, Quality = 40 }),
            ("segment map without data", new Vp8Syntax { NoSegmentData = true }, new WebpEncodeOptions { Segments = 4, Quality = 40 }),
            ("default segment probabilities", new Vp8Syntax { DefaultSegmentProbabilities = true }, new WebpEncodeOptions { Segments = 4, Quality = 60 }),
            ("no skip flags", new Vp8Syntax { NoSkipFlags = true }, new WebpEncodeOptions { Quality = 20 }),
            ("default token probabilities", new Vp8Syntax { DefaultTokenProbabilities = true }, new WebpEncodeOptions { Quality = 60 }),
            ("header bits", new Vp8Syntax { ColorSpaceBit = true, ClampingBit = true, RefreshProbabilitiesBit = true }, new WebpEncodeOptions { Quality = 90 }),
            ("quantizer offsets", new Vp8Syntax { DqY1Dc = 9, DqY2Dc = -15, DqY2Ac = 15, DqUvDc = 7, DqUvAc = -11 }, new WebpEncodeOptions { Quality = 55 }),
            ("quantizer offsets at the ends", new Vp8Syntax { DqY1Dc = -15, DqY2Dc = 15, DqY2Ac = -15, DqUvDc = 15, DqUvAc = 15 }, new WebpEncodeOptions { Quality = 100 }),
            ("quantizer offsets, coarse", new Vp8Syntax { DqY1Dc = 15, DqY2Dc = 15, DqY2Ac = 15, DqUvDc = 15, DqUvAc = 15 }, new WebpEncodeOptions { Quality = 0 }),
            ("filter deltas", new Vp8Syntax { FilterDeltaIntra = -20, FilterDelta4x4 = 30 }, new WebpEncodeOptions { Quality = 35, FilterStrength = 25 }),
            ("filter deltas clamp", new Vp8Syntax { FilterDeltaIntra = 63, FilterDelta4x4 = -63 }, new WebpEncodeOptions { Quality = 35, FilterStrength = 40, FilterSharpness = 6 }),
            ("filter deltas to zero", new Vp8Syntax { FilterDeltaIntra = -63 }, new WebpEncodeOptions { Quality = 35 }),
            ("filter deltas without update", new Vp8Syntax { FilterDeltasWithoutUpdate = true }, new WebpEncodeOptions { Quality = 35 }),
            ("filter deltas with segments", new Vp8Syntax { FilterDeltaIntra = 5, FilterDelta4x4 = -9, SegmentDeltas = true, SegmentFilterLevels = new[] { 3, 20, 44, 60 } }, new WebpEncodeOptions { Segments = 4, Quality = 45, SimpleFilter = true }),
            ("everything", new Vp8Syntax { SegmentDeltas = true, DefaultSegmentProbabilities = true, NoSkipFlags = true, DefaultTokenProbabilities = true, ClampingBit = true, DqY1Dc = -3, DqY2Ac = 4, FilterDelta4x4 = 12 },
                new WebpEncodeOptions { Segments = 4, Quality = 50, Partitions = 4 }),
        };
        foreach (var (name, sx, opt) in variants)
        {
            opt.Vp8Syntax = sx;
            foreach (int method in new[] { 0, 4 })
            {
                opt.Method = method;
                var data = Encode(img, opt);
                var back = Webp.Decode(data);
                double psnr = Psnr(img, back, 255);
                Assert.True(psnr > (opt.Quality == 0 ? 18 : 24), $"{name}: {psnr:F2} dB");
                // The one place where libwebp is not the reference: a key frame with a segment map but no segment data.
                // RFC 6386 and its reference decoder start a key frame with delta mode and zeros, i.e. the frame's
                // quantizer and filter level for every segment (libvpx 1.x and ffmpeg's own VP8 decoder both show
                // this file like we do, checked by hand); libwebp takes absolute zeros and shows garbage.
                if (sx.NoSegmentData)
                {
                    Assert.True(psnr > 30, $"{name}: {psnr:F2} dB");
                    continue;
                }
                var oracle = OracleDecode(data, ".webp");
                if (oracle == null) return;
                Assert.True(oracle.GetBytes().SequenceEqual(back.GetBytes()), $"{name} (method {method}): libwebp decodes other pixels, max difference {MaxDiff(oracle, back)}");
            }
        }
    }

    [Fact]
    public void LossyQualityScalesSizeAndFidelity()
    {
        var img = Photo(160, 120, ColorModel.Rgb, 2);
        int lastSize = 0;
        double lastPsnr = 0;
        foreach (int q in new[] { 10, 40, 75, 90, 100 })
        {
            var data = Encode(img, new WebpEncodeOptions { Quality = q });
            double psnr = Psnr(img, Webp.Decode(data), 255);
            output.WriteLine($"quality {q}: {data.Length} bytes, {psnr:F2} dB");
            Assert.True(data.Length > lastSize && psnr > lastPsnr);
            lastSize = data.Length; lastPsnr = psnr;
        }
        Assert.True(lastPsnr > 40);
    }

    [Fact]
    public void LossyWithAlpha()
    {
        foreach (var color in new[] { ColorModel.Rgba, ColorModel.GrayAlpha })
            foreach (int method in new[] { 0, 4 })
            {
                var img = Photo(70, 50, color, 4);
                var data = Encode(img, new WebpEncodeOptions { Quality = 80, Method = method });
                var back = Webp.Decode(data);
                var expected = Expanded(img);
                Assert.Equal(ColorModel.Rgba, back.Color);
                var b = (byte[])back.Data;
                var e = (byte[])expected.Data;
                for (int i = 3; i < e.Length; i += 4) Assert.Equal(e[i], b[i]);      // alpha is stored lossless
                Assert.True(Psnr(expected, back, 255) > 30);
                var oracle = OracleDecode(data, ".webp");
                if (oracle != null) AssertEqual(back, oracle);
            }
    }

    [Fact]
    public void LossyAlphaQuality()
    {
        var img = Photo(90, 70, ColorModel.Rgba, 4);
        var src = (byte[])img.Data;
        int lastSize = 0, lastError = 255;
        foreach (int q in new[] { 100, 90, 50, 10, 0 })
        {
            var data = Encode(img, new WebpEncodeOptions { Quality = 80, AlphaQuality = q });
            var back = (byte[])Webp.Decode(data).Data;
            int maxError = 0;
            var levels = new HashSet<byte>();
            for (int i = 3; i < src.Length; i += 4)
            {
                maxError = Math.Max(maxError, Math.Abs(src[i] - back[i]));
                levels.Add(back[i]);
                if (src[i] == 0 || src[i] == 255) Assert.Equal(src[i], back[i]);
            }
            output.WriteLine($"alpha quality {q}: {data.Length} bytes, {levels.Count} levels, max error {maxError}");
            if (q == 100) Assert.Equal(0, maxError);
            else
            {
                // near 100 the few merged levels can cost as much as they save; lower down the file has to shrink
                Assert.True((q > 50 || data.Length < lastSize) && maxError >= lastError, $"quality {q}");
                Assert.True(levels.Count <= 2 + q * q * 254 / 10000 && maxError <= 128);
            }
            if (q == 0) Assert.Equal(2, levels.Count);
            lastSize = data.Length; lastError = maxError;
            var oracle = OracleDecode(data, ".webp");
            if (oracle != null) AssertEqual(Webp.Decode(data), oracle);
        }
    }

    [Fact]
    public void ChromaModes()
    {
        // colored text: the place where subsampled chroma hurts
        var img = RawImage.Create(128, 96, ColorModel.Rgb, SampleType.UInt8);
        var d = (byte[])img.Data;
        for (int y = 0; y < 96; y++)
            for (int x = 0; x < 128; x++)
            {
                bool stroke = y < 48 ? x % 9 < 3 : (x + y) % 11 < 2;
                var c = stroke ? (y < 48 ? (220, 30, 40) : (20, 60, 230)) : (235, 235, 228);
                d[(y * 128 + x) * 3] = (byte)c.Item1; d[(y * 128 + x) * 3 + 1] = (byte)c.Item2; d[(y * 128 + x) * 3 + 2] = (byte)c.Item3;
            }
        var psnr = new Dictionary<WebpChroma, double>();
        foreach (var mode in new[] { WebpChroma.Average, WebpChroma.LinearLight, WebpChroma.Sharp })
        {
            var data = Encode(img, new WebpEncodeOptions { Quality = 90, Chroma = mode });
            var back = Webp.Decode(data);
            psnr[mode] = Psnr(img, back, 255);
            output.WriteLine($"{mode}: {data.Length} bytes, {psnr[mode]:F2} dB");
            var oracle = OracleDecode(data, ".webp");
            if (oracle != null) AssertEqual(back, oracle);
        }
        Assert.True(psnr[WebpChroma.Sharp] > psnr[WebpChroma.Average] + 0.3);
        Assert.True(psnr[WebpChroma.Average] > psnr[WebpChroma.LinearLight]);
        Assert.Equal(WebpChroma.Sharp, new WebpEncodeOptions().Chroma);
        // gray input has no chroma to get wrong: all modes give the same file
        var gray = Photo(64, 48, ColorModel.Gray, 2);
        Assert.Equal(Encode(gray, new WebpEncodeOptions { Chroma = WebpChroma.Average }), Encode(gray, new WebpEncodeOptions { Chroma = WebpChroma.Sharp }));
    }

    [Fact]
    public void NearLossless()
    {
        foreach (var color in new[] { ColorModel.Rgb, ColorModel.Rgba })
        {
            var img = Photo(160, 120, color, 6);
            var src = (byte[])img.Data;
            int ch = img.Channels;
            // a flat patch, which has to stay exact
            for (int y = 10; y < 40; y++) for (int x = 10; x < 60; x++) { src[(y * 160 + x) * ch] = 201; src[(y * 160 + x) * ch + 1] = 77; src[(y * 160 + x) * ch + 2] = 3; if (ch == 4) src[(y * 160 + x) * ch + 3] = 255; }
            int lossless = Encode(img, new WebpEncodeOptions { Lossless = true }).Length, last = lossless;
            foreach (int q in new[] { 90, 70, 50, 30, 0 })
            {
                var data = Encode(img, new WebpEncodeOptions { Lossless = true, NearLossless = q });
                var back = (byte[])Webp.Decode(data).Data;
                int limit = 1 << (4 - q / 20), maxError = 0;
                for (int i = 0; i < src.Length; i++)
                {
                    int e = Math.Abs(src[i] - back[i]);
                    if (ch == 4 && (i % 4 == 3 || src[i | 3] == 0)) Assert.Equal(0, e);       // alpha and hidden colors
                    maxError = Math.Max(maxError, e);
                }
                for (int y = 11; y < 39; y++) for (int x = 11; x < 59; x++) Assert.Equal(201, back[(y * 160 + x) * ch]);
                output.WriteLine($"{color} near-lossless {q}: {data.Length} bytes (lossless {lossless}), max error {maxError}");
                Assert.True(maxError <= limit && maxError > 0, $"max error {maxError} at {q}");
                Assert.True(data.Length < last, $"{data.Length} >= {last} at {q}");
                last = data.Length;
                var oracle = OracleDecode(data, ".webp");
                if (oracle != null) AssertEqual(Webp.Decode(data), oracle);
            }
            Assert.True(last < lossless * 0.6);
            AssertEqual(img, Webp.Decode(Encode(img, new WebpEncodeOptions { Lossless = true, NearLossless = 100 })));
        }
    }

    // ------------------------------------------------------------------ animation encoder

    static List<WebpInputFrame> MovingBox(int w, int h, ColorModel color, int count)
    {
        var frames = new List<WebpInputFrame>();
        var background = Photo(w, h, color, 11);
        int ch = background.Channels;
        for (int i = 0; i < count; i++)
        {
            var d = (byte[])((byte[])background.Data).Clone();
            for (int y = 3 + 2 * i; y < Math.Min(h, 12 + 2 * i); y++)
                for (int x = 5 + 5 * i; x < Math.Min(w, 17 + 5 * i); x++)
                    for (int c = 0; c < ch; c++) d[(y * w + x) * ch + c] = (byte)(c == 3 ? (i % 2 == 0 ? 255 : 90) : 40 * i + 30 * c);
            if (i == count - 1 && count > 2) d = (byte[])((byte[])frames[i - 1].Image.Data).Clone();      // a repeated picture
            frames.Add(new WebpInputFrame(new RawImage(w, h, color, d), 20 + 15 * i));
        }
        return frames;
    }

    [Theory]
    [InlineData(ColorModel.Rgb, true, true), InlineData(ColorModel.Rgb, true, false), InlineData(ColorModel.Rgba, true, true), InlineData(ColorModel.Rgba, true, false)]
    [InlineData(ColorModel.Gray, true, true), InlineData(ColorModel.GrayAlpha, true, true)]
    [InlineData(ColorModel.Rgb, false, true), InlineData(ColorModel.Rgb, false, false), InlineData(ColorModel.Rgba, false, true), InlineData(ColorModel.Rgba, false, false)]
    public void AnimationRoundTrip(ColorModel color, bool lossless, bool differencing)
    {
        var frames = MovingBox(48, 36, color, 6);
        var opt = new WebpEncodeOptions { Lossless = lossless, FrameDifferencing = differencing, LoopCount = 5, BackgroundColor = (1, 2, 3, 4), Quality = 85 };
        var data = Webp.EncodeAnimation(frames, opt);
        var anim = Webp.DecodeAnimation(data);
        Assert.True(anim.IsAnimated);
        Assert.Equal((48, 36, 6, 5), (anim.Width, anim.Height, anim.FrameCount, anim.LoopCount));
        Assert.Equal(((byte)1, (byte)2, (byte)3, (byte)4), anim.BackgroundColor);
        Assert.Equal(frames.Select(f => f.DurationMs), anim.Frames.Select(f => f.DurationMs));
        bool alpha = color == ColorModel.Rgba || color == ColorModel.GrayAlpha;
        // Lossless differencing stores unchanged pixels as transparent ones, so the file says "has alpha" (and decodes
        // to Rgba with every alpha at 255) although the pictures are opaque.
        bool transparencyTrick = lossless && differencing;
        Assert.Equal(alpha || transparencyTrick ? 4 : 3, Webp.GetInfo(data).Channels);
        var first = Webp.Decode(data);
        Assert.Equal(Webp.GetInfo(data).Channels, first.Channels);
        for (int p = 0; p < 48 * 36; p++)
            Assert.True(((byte[])first.Data).AsSpan(p * first.Channels, 3).SequenceEqual(((byte[])anim.Frames[0].Image.Data).AsSpan(p * 4, 3)));
        for (int i = 0; i < frames.Count; i++)
        {
            var expected = Expanded(frames[i].Image);
            var got = anim.Frames[i].Image;
            Assert.Equal(ColorModel.Rgba, got.Color);
            var g = (byte[])got.Data;
            var e = (byte[])expected.Data;
            double se = 0;
            for (int p = 0; p < 48 * 36; p++)
            {
                Assert.Equal(alpha ? e[p * 4 + 3] : 255, g[p * 4 + 3]);
                for (int c = 0; c < 3; c++) { double d = g[p * 4 + c] - e[p * expected.Channels + c]; se += d * d; }
            }
            if (lossless) Assert.Equal(0, se);
            else Assert.True(10 * Math.Log10(255.0 * 255 / (se / (48 * 36 * 3))) > 30, $"frame {i}: {10 * Math.Log10(255.0 * 255 / (se / (48 * 36 * 3))):F2} dB");
        }
        if (differencing)
        {
            // only the first frame covers the canvas; the repeated last picture costs a single pixel
            var raw = Webp.DecodeAnimation(data, new WebpDecodeOptions { FrameMode = WebpFrameMode.Raw });
            Assert.Equal((48, 36), (raw.Frames[0].Width, raw.Frames[0].Height));
            Assert.All(raw.Frames.Skip(1), f => Assert.True(f.Width < 48 && f.Height < 36));
            Assert.Equal((1, 1), (raw.Frames[5].Width, raw.Frames[5].Height));
            int full = Webp.EncodeAnimation(frames, new WebpEncodeOptions { Lossless = lossless, FrameDifferencing = false, Quality = 85 }).Length;
            Assert.True(data.Length < full);
        }
        var oracle = OracleDecode(data, ".anim.webp");
        if (oracle == null) return;
        Assert.Equal((48, 36 * 6, 4), (oracle.Width, oracle.Height, oracle.Channels));
        var all = new byte[48 * 36 * 6 * 4];
        for (int i = 0; i < 6; i++) ((byte[])anim.Frames[i].Image.Data).CopyTo(all, i * 48 * 36 * 4);
        AssertEqual(oracle, new RawImage(48, 36 * 6, ColorModel.Rgba, all));
    }

    [Fact]
    public void AnimationEncoderIsLazyAndChecksItsInput()
    {
        int pulled = 0;
        IEnumerable<WebpInputFrame> Source()
        {
            for (int i = 0; i < 3; i++) { pulled++; yield return new WebpInputFrame(Photo(16, 16, ColorModel.Rgb, i), 10); }
        }
        var data = Webp.EncodeAnimation(Source(), new WebpEncodeOptions { Lossless = true });
        Assert.Equal(3, pulled);
        Assert.Equal(3, Webp.GetInfo(data).FrameCount);
        Assert.Equal(0, Webp.GetAnimationInfo(data).LoopCount);

        Assert.Throws<ArgumentException>(() => Webp.EncodeAnimation(Array.Empty<WebpInputFrame>()));
        Assert.Throws<ArgumentException>(() => Webp.EncodeAnimation(new[] { new WebpInputFrame(Photo(8, 8, ColorModel.Rgb)), new WebpInputFrame(Photo(8, 9, ColorModel.Rgb)) }));
        Assert.Throws<ArgumentNullException>(() => Webp.EncodeAnimation(null!));
        // a single frame is still an animation
        var one = Webp.EncodeAnimation(new[] { new WebpInputFrame(Photo(9, 7, ColorModel.Rgba), 123) }, new WebpEncodeOptions { Lossless = true });
        var a = Webp.DecodeAnimation(one);
        Assert.True(a.IsAnimated);
        Assert.Equal(123, Assert.Single(a.Frames).DurationMs);
        AssertEqual(Photo(9, 7, ColorModel.Rgba), Webp.Decode(one));
    }

    // ------------------------------------------------------------------ metadata, unsupported input

    [Fact]
    public void MetadataRoundTrip()
    {
        var img = Photo(21, 13, ColorModel.Rgb);
        var exif = new byte[] { (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0, 1, 0, 0x12, 0x01, 3, 0, 1, 0, 0, 0, 5, 0, 0, 0, 0, 0, 0, 0 };
        img.Metadata = new ImageMetadata { IccProfile = new byte[] { 1, 2, 3, 4, 5 }, Exif = exif };
        var xmp = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>"u8.ToArray();
        foreach (bool lossless in new[] { true, false })
        {
            var data = Encode(img, new WebpEncodeOptions { Lossless = lossless, Xmp = xmp });
            var back = Webp.Decode(data);
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, back.Metadata!.IccProfile);
            Assert.Equal(exif, back.Metadata.Exif);
            Assert.Equal(5, back.Metadata.Orientation);
            Assert.Equal(xmp, Webp.GetXmp(data));
            Assert.Equal(ColorModel.Rgb, back.Color);
            // libwebp reads the file with its extended header and odd sized chunks as well
            var oracle = OracleDecode(data, ".webp");
            if (oracle != null) AssertEqual(back, oracle);

            var plain = Encode(img, new WebpEncodeOptions { Lossless = lossless, Xmp = xmp, WriteMetadata = false });
            Assert.Null(Webp.Decode(plain).Metadata);
            Assert.Null(Webp.GetXmp(plain));
            Assert.True(plain.Length < data.Length);
        }
        var anim = Webp.EncodeAnimation(new[] { new WebpInputFrame(img), new WebpInputFrame(Photo(21, 13, ColorModel.Rgb, 2)) }, new WebpEncodeOptions { Xmp = xmp, Lossless = true });
        var info = Webp.GetAnimationInfo(anim);
        Assert.Equal((5, exif.Length, xmp.Length), (info.IccProfile!.Length, info.Exif!.Length, info.Xmp!.Length));
    }

    static byte[] ExifWithResolution(bool bigEndian, uint xNum, uint xDen, uint yNum, uint yDen, int unit, int orientation)
    {
        var ms = new MemoryStream();
        void U16(int v) { var b = BitConverter.GetBytes((ushort)v); if (bigEndian) Array.Reverse(b); ms.Write(b); }
        void U32(uint v) { var b = BitConverter.GetBytes(v); if (bigEndian) Array.Reverse(b); ms.Write(b); }
        ms.Write(bigEndian ? "MM"u8 : "II"u8); U16(42); U32(8);
        int entries = unit >= 0 ? 4 : 3;
        U16(entries);
        int values = 8 + 2 + entries * 12 + 4;
        U16(0x0112); U16(3); U32(1); U16(orientation); U16(0);
        U16(0x011a); U16(5); U32(1); U32((uint)values);
        U16(0x011b); U16(5); U32(1); U32((uint)values + 8);
        if (unit >= 0) { U16(0x0128); U16(3); U32(1); U16(unit); U16(0); }
        U32(0);
        U32(xNum); U32(xDen); U32(yNum); U32(yDen);
        return ms.ToArray();
    }

    [Fact]
    public void ResolutionComesFromExif()
    {
        var img = Photo(16, 12, ColorModel.Rgb);
        (double, double) Dpi(byte[] exif, out ImageInfo info)
        {
            img.Metadata = new ImageMetadata { Exif = exif };
            var data = Encode(img, new WebpEncodeOptions { Lossless = true });
            info = Webp.GetInfo(data);
            var m = Webp.Decode(data).Metadata!;
            Assert.Equal((m.DpiX, m.DpiY, m.Orientation), (info.DpiX, info.DpiY, info.Orientation));
            return (m.DpiX, m.DpiY);
        }
        Assert.Equal((300.0, 150.0), Dpi(ExifWithResolution(false, 300, 1, 300, 2, 2, 3), out var i1));
        Assert.Equal(3, i1.Orientation);
        Assert.Equal((96.0, 96.0), Dpi(ExifWithResolution(true, 9600, 100, 96, 1, -1, 1), out _));      // no unit tag: inches
        var cm = Dpi(ExifWithResolution(true, 100, 1, 50, 1, 3, 8), out _);                              // per centimeter
        Assert.Equal((254.0, 127.0), (Math.Round(cm.Item1, 6), Math.Round(cm.Item2, 6)));
        Assert.Equal((0.0, 0.0), Dpi(ExifWithResolution(false, 72, 1, 72, 1, 1, 1), out _));             // "no absolute unit"
        Assert.Equal((0.0, 72.0), Dpi(ExifWithResolution(false, 72, 0, 72, 1, 2, 1), out _));            // zero denominator
        // damaged blocks give nothing and do not throw
        var good = ExifWithResolution(false, 300, 1, 300, 1, 2, 6);
        for (int cut = 0; cut < good.Length; cut++)
        {
            img.Metadata = new ImageMetadata { Exif = good.AsSpan(0, cut).ToArray() };
            if (cut == 0) continue;
            var m = Webp.Decode(Encode(img, new WebpEncodeOptions { Lossless = true })).Metadata!;
            Assert.True(m.DpiX is 0 or 300 && m.DpiY is 0 or 300 && m.Orientation is 0 or 6);
        }
        var bad = (byte[])good.Clone();
        bad[4] = 0xff; bad[5] = 0xff; bad[6] = 0xff; bad[7] = 0xff;     // IFD offset far outside
        Assert.Equal((0.0, 0.0), Dpi(bad, out _));
    }

    [Fact]
    public void PixelLimitNamesTheOption()
    {
        foreach (bool lossless in new[] { true, false })
        {
            var data = Encode(Photo(40, 30, ColorModel.Rgb), new WebpEncodeOptions { Lossless = lossless });
            var e = Assert.Throws<ImageNotSupportedException>(() => Webp.Decode(data, new WebpDecodeOptions { MaxPixels = 1000 }));
            Assert.Contains("WebpDecodeOptions.MaxPixels", e.Message);
            Assert.Contains("1000", e.Message);
            Assert.Equal(40, Webp.Decode(data, new WebpDecodeOptions { MaxPixels = 1200 }).Width);
        }
        var anim = Webp.EncodeAnimation(new[] { new WebpInputFrame(Photo(40, 30, ColorModel.Rgb)), new WebpInputFrame(Photo(40, 30, ColorModel.Rgb, 2)) });
        Assert.Contains("WebpDecodeOptions.MaxPixels", Assert.Throws<ImageNotSupportedException>(() => Webp.DecodeAnimation(anim, new WebpDecodeOptions { MaxPixels = 1000 })).Message);
        Assert.Equal(1L << 28, new WebpDecodeOptions().MaxPixels);
    }

    [Fact]
    public void UnsupportedInput()
    {
        using var ms = new MemoryStream();
        Assert.Throws<ImageNotSupportedException>(() => Webp.Encode(RawImage.Create(4, 4, ColorModel.Rgb, SampleType.UInt16), ms));
        Assert.Throws<ImageNotSupportedException>(() => Webp.Encode(RawImage.Create(4, 4, ColorModel.Gray, SampleType.Float32), ms));
        Assert.Throws<ImageNotSupportedException>(() => Webp.Encode(RawImage.Create(4, 4, ColorModel.Rgb, SampleType.Int8), ms));
        Assert.Throws<ImageNotSupportedException>(() => Webp.Encode(RawImage.Create(4, 4, ColorModel.Other, SampleType.UInt8, 5), ms));
        Assert.Throws<ImageNotSupportedException>(() => Webp.Encode(RawImage.Create(16384, 1, ColorModel.Gray, SampleType.UInt8), ms, new WebpEncodeOptions { Lossless = true }));
        Assert.Throws<ArgumentNullException>(() => Webp.Encode(null!, ms));
        Assert.Equal(0, ms.Length);
        // the largest sizes that fit
        var wide = Encode(RawImage.Create(16383, 2, ColorModel.Gray, SampleType.UInt8), new WebpEncodeOptions { Lossless = true });
        Assert.Equal((16383, 2), (Webp.GetInfo(wide).Width, Webp.GetInfo(wide).Height));
        var tall = Encode(RawImage.Create(1, 16383, ColorModel.Rgb, SampleType.UInt8));
        Assert.Equal((1, 16383), (Webp.GetInfo(tall).Width, Webp.GetInfo(tall).Height));
        AssertEqual(RawImage.Create(1, 16383, ColorModel.Rgb, SampleType.UInt8), Webp.Decode(tall), 2);

        Assert.False(Webp.IsMatch(default));
        Assert.False(Webp.IsMatch("RIFF"u8));
        Assert.False(Webp.IsMatch("RIFF\0\0\0\0WAVEfmt "u8));
        Assert.Throws<ImageFormatException>(() => Webp.Decode("RIFF\0\0\0\0WAVEfmt "u8));
        Assert.Throws<ImageFormatException>(() => Webp.GetInfo(default));
    }

    // ------------------------------------------------------------------ robustness

    static void MustFailCleanly(Action a)
    {
        try { a(); }
        catch (ImageFormatException) { }
        catch (ImageNotSupportedException) { }
    }

    static void DecodeEverything(byte[] data, WebpDecodeOptions opt)
    {
        MustFailCleanly(() => Webp.GetInfo(data));
        MustFailCleanly(() => Webp.GetAnimationInfo(data));
        MustFailCleanly(() => Webp.Decode(data, opt));
        MustFailCleanly(() => Webp.DecodeAnimation(data, opt));
    }

    [Theory, MemberData(nameof(Fixtures))]
    public void TruncatedAndCorruptedFilesFailCleanly(string file)
    {
        var data = File.ReadAllBytes(Fixture(file));
        // a flipped size field must not turn into a huge allocation: bound the canvas for this test
        var opt = new WebpDecodeOptions { MaxPixels = 1 << 20 };
        var sw = Stopwatch.StartNew();
        var rng = new Random(file.GetHashCode(StringComparison.Ordinal) ^ 12345);
        var cuts = new HashSet<int> { 0, 1, 11, 12, 13, 19, 20, 21, 25, 29, 30, 31, data.Length - 1, data.Length - 2, data.Length / 2 };
        for (int i = 0; i < 16; i++) cuts.Add(rng.Next(data.Length));
        foreach (int cut in cuts)
        {
            if (cut < 0 || cut >= data.Length) continue;
            DecodeEverything(data.AsSpan(0, cut).ToArray(), opt);
            // truncated but with a RIFF size that claims to be complete
            if (cut >= 12)
            {
                var t = data.AsSpan(0, cut).ToArray();
                BinaryPrimitives.WriteUInt32LittleEndian(t.AsSpan(4), (uint)(cut - 8));
                DecodeEverything(t, opt);
            }
        }
        for (int i = 0; i < 60; i++)
        {
            var d = (byte[])data.Clone();
            int flips = 1 + rng.Next(3);
            for (int k = 0; k < flips; k++)
            {
                // most damage goes to the first bytes, where the headers live
                int pos = rng.Next(4) == 0 ? rng.Next(d.Length) : rng.Next(Math.Min(d.Length, 96));
                d[pos] = rng.Next(3) == 0 ? (byte)rng.Next(256) : (byte)(d[pos] ^ (1 << rng.Next(8)));
            }
            DecodeEverything(d, opt);
        }
        Assert.True(sw.ElapsedMilliseconds < 20000, $"took {sw.ElapsedMilliseconds} ms");
    }

    static byte[] Riff(params (string tag, byte[] data)[] chunks)
    {
        var ms = new MemoryStream();
        ms.Write("RIFF\0\0\0\0WEBP"u8);
        foreach (var (tag, d) in chunks)
        {
            ms.Write(System.Text.Encoding.ASCII.GetBytes(tag));
            ms.Write(BitConverter.GetBytes(d.Length));
            ms.Write(d);
            if ((d.Length & 1) != 0) ms.WriteByte(0);
        }
        var res = ms.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(res.AsSpan(4), (uint)(res.Length - 8));
        return res;
    }

    static byte[] Vp8x(int flags, int w, int h)
        => new byte[] { (byte)flags, 0, 0, 0, (byte)(w - 1), (byte)((w - 1) >> 8), (byte)((w - 1) >> 16), (byte)(h - 1), (byte)((h - 1) >> 8), (byte)((h - 1) >> 16) };

    static byte[] Chunk(byte[] file, string tag)
    {
        for (int p = 12; p + 8 <= file.Length;)
        {
            int size = BitConverter.ToInt32(file, p + 4);
            if (System.Text.Encoding.ASCII.GetString(file, p, 4) == tag) return file.AsSpan(p + 8, size).ToArray();
            p += 8 + size + (size & 1);
        }
        throw new InvalidOperationException(tag);
    }

    [Fact]
    public void LyingHeadersDoNotAllocate()
    {
        var lossless = Chunk(Encode(Photo(8, 8, ColorModel.Rgb), new WebpEncodeOptions { Lossless = true }), "VP8L");
        var lossy = Chunk(Encode(Photo(8, 8, ColorModel.Rgb)), "VP8 ");

        // lossless stream of an 8x8 image that claims to be 16384 x 16384
        var big = (byte[])lossless.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(big.AsSpan(1), 0x0fffffffu | (BinaryPrimitives.ReadUInt32LittleEndian(big.AsSpan(1)) & 0xf0000000u));
        // lossy stream that claims 16383 x 16383
        var bigLossy = (byte[])lossy.Clone();
        bigLossy[6] = 0xff; bigLossy[7] = 0x3f; bigLossy[8] = 0xff; bigLossy[9] = 0x3f;
        // an animation on the largest canvas there is
        var frame = new byte[16 + 8 + lossless.Length + (lossless.Length & 1)];
        frame[6] = 7; frame[9] = 7;     // 8 x 8 at 0, 0
        "VP8L"u8.CopyTo(frame.AsSpan(16));
        BitConverter.GetBytes(lossless.Length).CopyTo(frame, 20);
        lossless.CopyTo(frame, 24);
        var hugeCanvas = Riff(("VP8X", Vp8x(0x12, 1 << 24, 1 << 24)), ("ANIM", new byte[6]), ("ANMF", frame));
        var bigCanvas = Riff(("VP8X", Vp8x(0x12, 16384, 16384)), ("ANIM", new byte[6]), ("ANMF", frame));

        long before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        Assert.Throws<ImageFormatException>(() => Webp.Decode(Riff(("VP8L", big))));
        Assert.Throws<ImageFormatException>(() => Webp.Decode(Riff(("VP8 ", bigLossy))));
        Assert.Throws<ImageFormatException>(() => Webp.Decode(Riff(("VP8X", Vp8x(0, 16384, 16384)), ("VP8L", big))));
        Assert.Throws<ImageNotSupportedException>(() => Webp.Decode(hugeCanvas));
        Assert.Throws<ImageNotSupportedException>(() => Webp.DecodeAnimation(hugeCanvas));
        Assert.Throws<ImageNotSupportedException>(() => Webp.Decode(bigCanvas, new WebpDecodeOptions { MaxPixels = 1 << 24 }));
        Assert.Throws<ImageNotSupportedException>(() => Webp.Decode(Riff(("VP8L", big)), new WebpDecodeOptions { MaxPixels = 1 << 20 }));
        Assert.Throws<ImageNotSupportedException>(() => Webp.Decode(Riff(("VP8 ", bigLossy)), new WebpDecodeOptions { MaxPixels = 1 << 20 }));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"{allocated} bytes allocated, {sw.ElapsedMilliseconds} ms");
        Assert.True(allocated < 32 << 20, $"{allocated} bytes allocated");
        Assert.True(sw.ElapsedMilliseconds < 2000);

        // the header level information is still available
        Assert.Equal((1 << 24, 1 << 24, 1), (Webp.GetInfo(hugeCanvas).Width, Webp.GetInfo(hugeCanvas).Height, Webp.GetInfo(hugeCanvas).FrameCount));
        Assert.Equal((16384, 16384), (Webp.GetInfo(Riff(("VP8L", big))).Width, Webp.GetInfo(Riff(("VP8L", big))).Height));

        // structural lies
        Assert.Throws<ImageFormatException>(() => Webp.GetInfo(Riff(("VP8X", Vp8x(0, 9, 8)), ("VP8L", lossless))));               // canvas != image
        Assert.Throws<ImageFormatException>(() => Webp.GetInfo(Riff(("VP8X", Vp8x(0x02, 8, 8)), ("ANIM", new byte[6]))));            // no frames
        Assert.Throws<ImageFormatException>(() => Webp.GetInfo(Riff(("VP8X", Vp8x(0x02, 6, 8)), ("ANIM", new byte[6]), ("ANMF", frame))));   // frame exceeds canvas
        Assert.Throws<ImageFormatException>(() => Webp.GetInfo(Riff(("VP8X", Vp8x(0, 8, 8)))));                                     // no image
        Assert.Throws<ImageFormatException>(() => Webp.GetInfo(Riff(("VP8 ", new byte[5]))));
        Assert.Throws<ImageFormatException>(() => Webp.GetInfo(Riff(("VP8L", new byte[] { 0x2f, 0, 0, 0, 0x20 }))));               // version bits
        var chunkTooLong = Riff(("VP8L", lossless));
        BinaryPrimitives.WriteUInt32LittleEndian(chunkTooLong.AsSpan(16), (uint)lossless.Length + 100);
        Assert.Throws<ImageFormatException>(() => Webp.GetInfo(chunkTooLong));
        var riffTooLong = Riff(("VP8L", lossless));
        BinaryPrimitives.WriteUInt32LittleEndian(riffTooLong.AsSpan(4), (uint)riffTooLong.Length);
        Assert.Throws<ImageFormatException>(() => Webp.Decode(riffTooLong));
        Assert.Equal(8, Webp.GetInfo(riffTooLong).Width);        // for GetInfo this is a prefix that holds all it needs
        // raw alpha shorter than the image
        Assert.Throws<ImageFormatException>(() => Webp.Decode(Riff(("VP8X", Vp8x(0x10, 8, 8)), ("ALPH", new byte[20]), ("VP8 ", lossy))));
        Assert.Throws<ImageFormatException>(() => Webp.Decode(Riff(("VP8X", Vp8x(0x10, 8, 8)), ("ALPH", new byte[] { 3 }), ("VP8 ", lossy))));
    }

    [Fact]
    public void ThroughputSmokeTest()
    {
        // not a benchmark: just a guard against something going quadratic
        var img = Photo(1024, 768, ColorModel.Rgb, 4);
        var sw = Stopwatch.StartNew();
        var lossy = Encode(img, new WebpEncodeOptions { Quality = 80 });
        long encLossy = sw.ElapsedMilliseconds; sw.Restart();
        var back = Webp.Decode(lossy);
        long decLossy = sw.ElapsedMilliseconds; sw.Restart();
        var lossless = Encode(img, new WebpEncodeOptions { Lossless = true });
        long encLossless = sw.ElapsedMilliseconds; sw.Restart();
        AssertEqual(img, Webp.Decode(lossless));
        long decLossless = sw.ElapsedMilliseconds;
        output.WriteLine($"0.79 MP: lossy encode {encLossy} ms ({lossy.Length} bytes, {Psnr(img, back, 255):F2} dB), decode {decLossy} ms; lossless encode {encLossless} ms ({lossless.Length} bytes), decode {decLossless} ms");
        Assert.True(encLossy + decLossy + encLossless + decLossless < 60000);
    }
}
