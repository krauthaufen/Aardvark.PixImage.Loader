using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using Aardvark.ImageCodecs;
using Xunit;
using static Aardvark.ImageCodecs.Tests.PngTests;

namespace Aardvark.ImageCodecs.Tests;

public class PngAnimationTests
{
    public static IEnumerable<object[]> Animations() => TestUtil.Files("png/apng", ".png");

    /// <summary>Splits a .frames.ref (frames stacked vertically) into images.</summary>
    static List<RawImage> LoadFrames(string rel, int height)
    {
        var all = TestUtil.LoadRef(TestUtil.Fixture(rel) + ".frames.ref");
        Assert.Equal(4, all.Channels);
        Assert.Equal(0, all.Height % height);
        var list = new List<RawImage>();
        int rowBytes = all.GetBytes().Length / all.Height;
        for (int i = 0; i < all.Height / height; i++)
        {
            var f = RawImage.Create(all.Width, height, ColorModel.Rgba, all.SampleType);
            all.GetBytes().Slice(i * height * rowBytes, height * rowBytes).CopyTo(f.GetBytes());
            list.Add(f);
        }
        return list;
    }

    static JsonElement Json(string path) => JsonDocument.Parse(File.ReadAllText(path)).RootElement;

    /// <summary>The image a still decode yields, expanded to Rgba the way animation frames are.</summary>
    internal static RawImage ToRgba(RawImage img)
    {
        var o = RawImage.Create(img.Width, img.Height, ColorModel.Rgba, img.SampleType);
        int n = img.Width * img.Height, ch = img.Channels;
        if (img.SampleType == SampleType.UInt8)
        {
            var s = img.GetData<byte>(); var d = o.GetData<byte>();
            for (int i = 0; i < n; i++)
            {
                d[i * 4] = s[i * ch]; d[i * 4 + 1] = s[i * ch + (ch >= 3 ? 1 : 0)]; d[i * 4 + 2] = s[i * ch + (ch >= 3 ? 2 : 0)];
                d[i * 4 + 3] = ch is 2 or 4 ? s[i * ch + ch - 1] : (byte)255;
            }
        }
        else
        {
            var s = img.GetData<ushort>(); var d = o.GetData<ushort>();
            for (int i = 0; i < n; i++)
            {
                d[i * 4] = s[i * ch]; d[i * 4 + 1] = s[i * ch + (ch >= 3 ? 1 : 0)]; d[i * 4 + 2] = s[i * ch + (ch >= 3 ? 2 : 0)];
                d[i * 4 + 3] = ch is 2 or 4 ? s[i * ch + ch - 1] : (ushort)65535;
            }
        }
        return o;
    }

    [Fact]
    public void FixturesArePresent() => Assert.True(Animations().Count() >= 30);

    [Theory]
    [MemberData(nameof(Animations))]
    public void DecodesAnimation(string rel)
    {
        var data = Load(rel);
        var info = Png.GetAnimationInfo(data);
        var refs = LoadFrames(rel, info.Height);
        var anim = Png.DecodeAnimation(data);

        Assert.True(info.IsAnimated);
        Assert.Equal(refs.Count, anim.Frames.Count);
        Assert.Equal(refs.Count, info.FrameCount);
        Assert.Equal(refs.Count, Png.GetInfo(data).FrameCount);
        Assert.Equal((info.Width, info.Height, info.LoopCount, info.DefaultImageIsFirstFrame, info.SampleType),
                     (anim.Width, anim.Height, anim.LoopCount, anim.DefaultImageIsFirstFrame, anim.SampleType));
        for (int i = 0; i < refs.Count; i++)
        {
            var f = anim.Frames[i];
            Assert.Equal(i, f.Index);
            Assert.Equal(ColorModel.Rgba, f.Image.Color);
            Assert.Equal(info.SampleType, f.Image.SampleType);
            TestUtil.AssertEqual(refs[i], f.Image);
        }

        // lazy enumeration yields the same frames, each owning its pixels
        int n = 0;
        foreach (var f in Png.EnumerateFrames(data))
        {
            TestUtil.AssertEqual(refs[n], f.Image);
            Assert.NotSame(anim.Frames[n].Image.Data, f.Image.Data);
            n++;
        }
        Assert.Equal(refs.Count, n);

        // frame geometry and timing as the generator wrote them
        var desc = Json(TestUtil.Fixture(rel) + ".json");
        bool defaultIsFrame = !desc.TryGetProperty("default_is_frame", out var dif) || dif.GetBoolean();
        if (desc.GetProperty("frames").ValueKind == JsonValueKind.Array)
        {
            Assert.Equal(desc.GetProperty("plays").GetInt32(), info.LoopCount);
            Assert.Equal(defaultIsFrame, info.DefaultImageIsFirstFrame);
            int k = 0;
            foreach (var e in desc.GetProperty("frames").EnumerateArray())
            {
                var f = anim.Frames[k++];
                Assert.Equal((e.GetProperty("x").GetInt32(), e.GetProperty("y").GetInt32(), e.GetProperty("w").GetInt32(), e.GetProperty("h").GetInt32()),
                             (f.X, f.Y, f.Width, f.Height));
                Assert.Equal((e.GetProperty("delay")[0].GetInt32(), e.GetProperty("delay")[1].GetInt32()), (f.DelayNumerator, f.DelayDenominator));
                Assert.Equal((PngDisposal)e.GetProperty("dispose").GetInt32(), f.Disposal);
                Assert.Equal((PngBlend)e.GetProperty("blend").GetInt32(), f.Blend);
            }
        }

        // frame count, loop count and frame durations as Pillow reads them (it counts a separate default image as a frame)
        var meta = Json(TestUtil.Fixture(rel) + ".meta.json");
        int extra = info.DefaultImageIsFirstFrame ? 0 : 1;
        Assert.Equal(meta.GetProperty("frames").GetInt32() - extra, info.FrameCount);
        Assert.Equal(meta.GetProperty("loop").GetInt32(), info.LoopCount);
        if (meta.TryGetProperty("durations", out var durations))
            for (int i = 0; i < info.FrameCount; i++)
                Assert.Equal(durations[i + extra].GetDouble(), anim.Frames[i].DelayMs, 6);
    }

    [Theory]
    [MemberData(nameof(Animations))]
    public void RawFramesCompositeToTheSameAnimation(string rel)
    {
        var data = Load(rel);
        var composited = Png.DecodeAnimation(data);
        var raw = Png.DecodeAnimation(data, new PngDecodeOptions { FrameMode = PngFrameMode.Raw });
        var still = Png.Decode(data);
        Assert.Equal(composited.Frames.Count, raw.Frames.Count);
        for (int i = 0; i < raw.Frames.Count; i++)
        {
            var f = raw.Frames[i];
            Assert.Equal((f.Width, f.Height), (f.Image.Width, f.Image.Height));
            Assert.Equal((still.Color, still.SampleType), (f.Image.Color, f.Image.SampleType));
            // a frame drawn with "source" shows up unchanged in its region of the composited canvas
            if (f.Blend != PngBlend.Source) continue;
            var expect = ToRgba(f.Image).GetBytes();
            var canvas = composited.Frames[i].Image.GetBytes();
            int px = expect.Length / (f.Width * f.Height);
            for (int y = 0; y < f.Height; y++)
                Assert.True(expect.Slice(y * f.Width * px, f.Width * px)
                    .SequenceEqual(canvas.Slice(((f.Y + y) * composited.Width + f.X) * px, f.Width * px)));
        }
        if (composited.DefaultImageIsFirstFrame) TestUtil.AssertEqual(still, raw.Frames[0].Image);
    }

    [Fact]
    public void PlainPngIsASingleFrame()
    {
        foreach (var name in new[] { "m_c2_d08_n_13x9", "m_c0_d16_i_13x9", "t_pal_d4_n", "m_c4_d08_n_5x3" })
        {
            var data = Load($"png/{name}.png");
            var info = Png.GetAnimationInfo(data);
            Assert.False(info.IsAnimated);
            Assert.Equal((1, 0, true), (info.FrameCount, info.LoopCount, info.DefaultImageIsFirstFrame));
            var anim = Png.DecodeAnimation(data);
            var f = Assert.Single(anim.Frames);
            Assert.Equal((0, 0, info.Width, info.Height), (f.X, f.Y, f.Width, f.Height));
            TestUtil.AssertEqual(ToRgba(Png.Decode(data)), f.Image);
            var raw = Assert.Single(Png.DecodeAnimation(data, new PngDecodeOptions { FrameMode = PngFrameMode.Raw }).Frames);
            TestUtil.AssertEqual(Png.Decode(data), raw.Image);
            Assert.Single(Png.EnumerateFrames(data));
            Assert.Equal(1, Png.GetInfo(data).FrameCount);
        }
    }

    // ------------------------------------------------------------------ hand assembled files

    record Frame(RawImage Image, int X = 0, int Y = 0, int Dispose = 0, int Blend = 0, int Num = 1, int Den = 10);

    static byte[] FrameControl(uint seq, Frame f)
    {
        var b = new byte[26];
        BinaryPrimitives.WriteUInt32BigEndian(b, seq);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(4), f.Image.Width);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(8), f.Image.Height);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(12), f.X);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(16), f.Y);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(20), (ushort)f.Num);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(22), (ushort)f.Den);
        b[24] = (byte)f.Dispose; b[25] = (byte)f.Blend;
        return Chunk("fcTL", b);
    }

    /// <summary>Builds an APNG from frames by borrowing the compressed streams of stills written by our encoder.</summary>
    static List<byte[]> ApngChunks(int width, int height, IReadOnlyList<Frame> frames)
    {
        var first = Chunks(Png.Encode(frames[0].Image));
        var ihdr = (byte[])first[0].bytes.Clone();
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(8), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(12), height);
        var list = new List<byte[]> { Chunk("IHDR", ihdr.AsSpan(8, 13).ToArray()) };
        var actl = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(actl, frames.Count);
        list.Add(Chunk("acTL", actl));
        uint seq = 0;
        for (int i = 0; i < frames.Count; i++)
        {
            list.Add(FrameControl(seq++, frames[i]));
            foreach (var c in Chunks(Png.Encode(frames[i].Image)).Where(c => c.type == "IDAT"))
            {
                if (i == 0) { list.Add(c.bytes); continue; }
                var body = new byte[c.bytes.Length - 12 + 4];
                BinaryPrimitives.WriteUInt32BigEndian(body, seq++);
                c.bytes.AsSpan(8, c.bytes.Length - 12).CopyTo(body.AsSpan(4));
                list.Add(Chunk("fdAT", body));
            }
        }
        list.Add(Chunk("IEND", Array.Empty<byte>()));
        return list;
    }

    [Theory]
    [InlineData(SampleType.UInt8)]
    [InlineData(SampleType.UInt16)]
    public void OverBlendingIsExactlyRounded(SampleType type)
    {
        // random translucent pixels over random translucent pixels, against exact rational arithmetic
        const int W = 64, H = 48;
        var back = MakeImage(W, H, ColorModel.Rgba, type, 11, noise: true);
        var front = MakeImage(W, H, ColorModel.Rgba, type, 12, noise: true);
        long max = type == SampleType.UInt8 ? 255 : 65535;
        long At(RawImage img, int i) => type == SampleType.UInt8 ? img.GetData<byte>()[i] : img.GetData<ushort>()[i];
        void Set(RawImage img, int i, long v) { if (type == SampleType.UInt8) img.GetData<byte>()[i] = (byte)v; else img.GetData<ushort>()[i] = (ushort)v; }
        // make sure the special alphas occur on both sides
        for (int p = 0; p < W * H; p += 7) Set(front, p * 4 + 3, p % 3 == 0 ? 0 : max);
        for (int p = 3; p < W * H; p += 11) Set(back, p * 4 + 3, p % 2 == 0 ? 0 : max);

        var png = Assemble(ApngChunks(W, H, new[] { new Frame(back), new Frame(front, Blend: 1) }));
        var result = Png.DecodeAnimation(png).Frames[1].Image;
        TestUtil.AssertEqual(back, Png.DecodeAnimation(png).Frames[0].Image);
        for (int p = 0; p < W * H; p++)
        {
            BigInteger sa = At(front, p * 4 + 3), da = At(back, p * 4 + 3);
            // alpha = sa + da (1 - sa), colour = (sc sa + dc da (1 - sa)) / alpha, everything scaled by max * max
            BigInteger sw = sa * max, dw = da * (max - sa), total = sw + dw;
            for (int c = 0; c < 4; c++)
            {
                long expect;
                if (total.IsZero) expect = At(sa.IsZero ? back : front, p * 4 + c);
                else if (sa.IsZero) expect = At(back, p * 4 + c);
                else if (sa == max || da.IsZero) expect = At(front, p * 4 + c);
                else if (c == 3) expect = (long)((2 * total + max) / (2 * max));
                else expect = (long)((2 * (At(front, p * 4 + c) * sw + At(back, p * 4 + c) * dw) + total) / (2 * total));
                Assert.Equal(expect, At(result, p * 4 + c));
            }
        }
    }

    [Fact]
    public void AnimationStructureErrors()
    {
        var a = MakeImage(8, 6, ColorModel.Rgba, SampleType.UInt8, 1);
        var b = MakeImage(4, 3, ColorModel.Rgba, SampleType.UInt8, 2);
        var frames = new[] { new Frame(a), new Frame(b, 2, 1), new Frame(b, 4, 3, 1, 1) };
        var good = ApngChunks(8, 6, frames);
        var still = Png.Decode(Assemble(good));
        Assert.Equal(3, Png.DecodeAnimation(Assemble(good)).Frames.Count);

        void Broken(Func<List<byte[]>, IEnumerable<byte[]>> edit)
        {
            var png = Assemble(edit(good.Select(c => (byte[])c.Clone()).ToList()));
            Assert.Throws<ImageFormatException>(() => Png.DecodeAnimation(png));
            Assert.Throws<ImageFormatException>(() => Png.GetAnimationInfo(png));
            Assert.Throws<ImageFormatException>(() => Png.EnumerateFrames(png).ToList());
            // none of this concerns the default image
            TestUtil.AssertEqual(still, Png.Decode(png));
        }
        string Type(byte[] c) => System.Text.Encoding.ASCII.GetString(c, 4, 4);
        byte[] Patch(byte[] c, int offset, uint value)
        {
            var body = c.AsSpan(8, c.Length - 12).ToArray();
            BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(offset), value);
            return Chunk(Type(c), body);
        }
        int second = good.FindIndex(3, c => Type(c) == "fcTL");
        int data = good.FindIndex(c => Type(c) == "fdAT");

        int third = good.FindIndex(second + 1, c => Type(c) == "fcTL");
        Broken(l => l.Where((c, i) => i < second || i >= third));                       // a frame missing
        Broken(l => { l[second] = Patch(l[second], 0, 7); return l; });                 // sequence number out of order
        Broken(l => { l[data] = Patch(l[data], 0, 99); return l; });
        Broken(l => { l[second] = Patch(l[second], 12, 6); return l; });                // region leaves the canvas
        Broken(l => { l[second] = Patch(l[second], 4, 0); return l; });                 // empty region
        Broken(l => { l[second] = Patch(l[second], 4, 0x80000001); return l; });
        Broken(l => { var body = l[second].AsSpan(8, 26).ToArray(); body[24] = 3; l[second] = Chunk("fcTL", body); return l; });   // dispose op
        Broken(l => { var body = l[second].AsSpan(8, 26).ToArray(); body[25] = 2; l[second] = Chunk("fcTL", body); return l; });   // blend op
        Broken(l => { l[second][^1] ^= 1; return l; });                                 // CRC of an animation chunk
        Broken(l => { l[1] = Patch(l[1], 0, 5); return l; });                           // acTL announces more frames
        Broken(l => { l[1] = Patch(l[1], 0, 0); return l; });
        Broken(l => { l[2] = Patch(l[2], 4, 4); return l; });                           // first frame smaller than the image
        Broken(l => l.Where((c, i) => Type(c) != "fdAT" || i < second));                 // frame without data
        Broken(l => { l.Insert(2, Patch(l[data], 0, 0)); return l; });                   // fdAT before any frame
        // truncated frame data: the structure is fine, decoding that frame fails
        var cut = good.Select(c => (byte[])c.Clone()).ToList();
        var half = cut[data].AsSpan(8, cut[data].Length - 12).ToArray();
        cut[data] = Chunk("fdAT", half[..(half.Length / 2)]);
        Assert.Equal(3, Png.GetAnimationInfo(Assemble(cut)).FrameCount);
        Assert.Throws<ImageFormatException>(() => Png.DecodeAnimation(Assemble(cut)));
        Assert.Single(Png.EnumerateFrames(Assemble(cut)).Take(1));

        // an acTL after the image data does not make an animation; neither do stray fcTL/fdAT chunks without acTL
        var late = good.Where((c, i) => i != 1).ToList();
        var plain = Png.DecodeAnimation(Assemble(late));
        Assert.False(plain.IsAnimated);
        TestUtil.AssertEqual(ToRgba(still), Assert.Single(plain.Frames).Image);
        Assert.Equal(1, Png.GetInfo(Assemble(late)).FrameCount);
    }

    [Fact]
    public void LyingCanvasSizeDoesNotAllocate()
    {
        // 20000 x 20000 canvas (1.6 GB as Rgba) backed by a few bytes of image data and one tiny frame
        var tiny = MakeImage(2, 2, ColorModel.Rgba, SampleType.UInt8, 1);
        var chunks = ApngChunks(20000, 20000, new[] { new Frame(tiny), new Frame(tiny, 5, 5) });
        var body = chunks[2].AsSpan(8, 26).ToArray();
        BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(4), 20000);
        BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(8), 20000);
        chunks[2] = Chunk("fcTL", body);
        var png = Assemble(chunks);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<ImageFormatException>(() => Png.DecodeAnimation(png));
        Assert.Throws<ImageFormatException>(() => Png.DecodeAnimation(png, new PngDecodeOptions { FrameMode = PngFrameMode.Raw }));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1 << 22);
    }

    [Fact]
    public void DamagedAnimationsAreRejectedCleanly()
    {
        var rnd = new Random(4711);
        var noCrc = new PngDecodeOptions { VerifyChecksums = false };
        int n = 0;
        foreach (var row in Animations())
        {
            var bytes = Load((string)row[0]);
            for (int round = 0; round < 60; round++)
            {
                byte[] copy;
                if (round % 3 == 0) copy = bytes.AsSpan(0, rnd.Next(bytes.Length)).ToArray();
                else
                {
                    copy = (byte[])bytes.Clone();
                    for (int k = 1 + rnd.Next(3); k > 0; k--)
                    {
                        int at = rnd.Next(copy.Length);
                        copy[at] = rnd.Next(4) == 0 ? (byte)rnd.Next(256) : (byte)(copy[at] ^ (1 << rnd.Next(8)));
                    }
                }
                foreach (var opt in new[] { null, noCrc })
                {
                    try
                    {
                        foreach (var f in Png.EnumerateFrames(copy, opt)) Assert.True(f.Image.Width > 0);
                        Png.GetAnimationInfo(copy);
                    }
                    catch (ImageFormatException) { }
                    catch (ImageNotSupportedException) { }
                    try { Png.DecodeAnimation(copy, new PngDecodeOptions { FrameMode = PngFrameMode.Raw, VerifyChecksums = opt == null }); }
                    catch (ImageFormatException) { }
                    catch (ImageNotSupportedException) { }
                }
                n++;
            }
        }
        Assert.True(n > 1500);
    }

    // ------------------------------------------------------------------ encoding

    static List<RawImage> MovingBox(ColorModel color, SampleType type, int count, int w = 48, int h = 36)
    {
        // a static background with a small noisy box moving across it
        var background = MakeImage(w, h, color, type, 21);
        var list = new List<RawImage>();
        for (int k = 0; k < count; k++)
        {
            var img = RawImage.Create(w, h, color, type);
            background.GetBytes().CopyTo(img.GetBytes());
            var patch = MakeImage(9, 7, color, type, 30 + k, noise: true);
            int px = img.Channels * RawImage.BytesPerSample(type);
            for (int y = 0; y < 7; y++)
                patch.GetBytes().Slice(y * 9 * px, 9 * px).CopyTo(img.GetBytes().Slice(((3 + 4 * k + y) * w + 2 + 6 * k) * px));
            list.Add(img);
        }
        return list;
    }

    public static IEnumerable<object[]> EncodeCases() => PngTests.EncodeCases();

    [Theory]
    [MemberData(nameof(EncodeCases))]
    public void EncodeAnimationRoundTrips(ColorModel color, SampleType type)
    {
        var images = MovingBox(color, type, 5);
        images.Insert(2, images[1]); // a repeated frame
        var frames = images.Select((img, i) => new PngInputFrame(img, i + 1, 25)).ToList();
        foreach (bool diff in new[] { true, false })
            foreach (bool interlace in new[] { false, true })
            {
                var opt = new PngEncodeOptions { FrameDifferencing = diff, Interlace = interlace, LoopCount = 4 };
                var png = Png.EncodeAnimation(frames, opt);
                var anim = Png.DecodeAnimation(png);
                Assert.True(anim.IsAnimated);
                Assert.Equal((images.Count, 4, true), (anim.FrameCount, anim.LoopCount, anim.DefaultImageIsFirstFrame));
                for (int i = 0; i < images.Count; i++)
                {
                    var f = anim.Frames[i];
                    TestUtil.AssertEqual(ToRgba(images[i]), f.Image);
                    Assert.Equal((i + 1, 25), (f.DelayNumerator, f.DelayDenominator));
                    Assert.Equal((i + 1) * 40.0, f.DelayMs, 9);
                    bool full = (f.X, f.Y, f.Width, f.Height) == (0, 0, anim.Width, anim.Height);
                    Assert.Equal(!diff || i == 0, full);
                }
                if (diff) Assert.Equal((1, 1), (anim.Frames[2].Width, anim.Frames[2].Height));
                // the default image is the first frame, in the color model that went in
                TestUtil.AssertEqual(images[0], Png.Decode(png));
                Assert.Equal(color, Png.Decode(png).Color);
                Assert.Equal(images.Count, Png.GetInfo(png).FrameCount);
            }
        int small = Png.EncodeAnimation(frames).Length, large = Png.EncodeAnimation(frames, new PngEncodeOptions { FrameDifferencing = false }).Length;
        Assert.True(small < large / 2, $"{small} vs {large}");
    }

    [Theory]
    [MemberData(nameof(EncodeCases))]
    public void EncodeAnimationIsReadByOracles(ColorModel color, SampleType type)
    {
        var images = MovingBox(color, type, 4);
        var frames = images.Select((img, i) => new PngInputFrame(img, 30 * (i + 1), 1000)).ToList();
        var png = Png.EncodeAnimation(frames, new PngEncodeOptions { LoopCount = 2 });

        // composited frames by ffmpeg (cross-checked with Pillow at 8 bit inside the oracle), stacked vertically
        var oracle = PngFeatureTests.OracleDecode(png, ".anim.png");
        if (oracle == null) return;
        Assert.Equal(images[0].Height * images.Count, oracle.Height);
        var expect = RawImage.Create(images[0].Width, oracle.Height, ColorModel.Rgba, type);
        int frameBytes = expect.GetBytes().Length / images.Count;
        for (int i = 0; i < images.Count; i++) ToRgba(images[i]).GetBytes().CopyTo(expect.GetBytes().Slice(i * frameBytes));
        TestUtil.AssertEqual(expect, oracle);

        // frame count, loop count and durations as Pillow reads them
        var meta = PngFeatureTests.OracleMeta(png);
        Assert.Equal(images.Count, meta.GetProperty("frames").GetInt32());
        Assert.Equal(2, meta.GetProperty("loop").GetInt32());
        for (int i = 0; i < images.Count; i++) Assert.Equal(30.0 * (i + 1), meta.GetProperty("durations")[i].GetDouble(), 6);
    }

    // ------------------------------------------------------------------ optimized frames, shared transforms

    static void Fill(RawImage img, int x, int y, int w, int h, params long[] sample)
    {
        int ch = img.Channels;
        for (int row = y; row < y + h; row++)
            for (int col = x; col < x + w; col++)
                for (int c = 0; c < ch; c++)
                {
                    int i = (row * img.Width + col) * ch + c;
                    if (img.SampleType == SampleType.UInt8) img.GetData<byte>()[i] = (byte)sample[c];
                    else img.GetData<ushort>()[i] = (ushort)sample[c];
                }
    }

    static RawImage Clone(RawImage img)
    {
        var o = RawImage.Create(img.Width, img.Height, img.Color, img.SampleType);
        img.GetBytes().CopyTo(o.GetBytes());
        return o;
    }

    /// <summary>Three kinds of animation, each of which a different disposal suits best.</summary>
    static List<RawImage> Scenario(string kind, ColorModel color, SampleType type)
    {
        const int W = 40, H = 30;
        long max = type == SampleType.UInt8 ? 255 : 65535;
        int ch = RawImage.ChannelCount(color);
        bool alpha = color is ColorModel.GrayAlpha or ColorModel.Rgba;
        long[] Sample(long v, long a = -1) => Enumerable.Range(0, ch).Select(c => alpha && c == ch - 1 ? (a < 0 ? max : a) : (v * (c + 3) / 3) % (max + 1)).ToArray();
        var list = new List<RawImage>();
        switch (kind)
        {
            case "sprite":
            {
                // a small sprite hopping over an otherwise empty (transparent, or black) canvas
                for (int k = 0; k < 6; k++)
                {
                    var img = RawImage.Create(W, H, color, type);
                    var sprite = MakeImage(7, 6, color, type, 40 + k, noise: true);
                    int px = ch * RawImage.BytesPerSample(type);
                    for (int y = 0; y < 6; y++)
                        sprite.GetBytes().Slice(y * 7 * px, 7 * px).CopyTo(img.GetBytes().Slice(((2 + 4 * k + y) * W + 3 + 5 * k) * px));
                    // opaque sprite pixels
                    if (alpha) for (int y = 0; y < 6; y++) for (int x = 0; x < 7; x++)
                        Fill(img, 3 + 5 * k + x, 2 + 4 * k + y, 1, 1, Enumerable.Range(0, ch).Select(c => c == ch - 1 ? max : (long)(x * 31 + y * 17 + c * 5 + k) * max / 255 % (max + 1)).ToArray());
                    list.Add(img);
                }
                break;
            }
            case "flash":
            {
                // a busy picture over which a box flashes up in every other frame
                var background = MakeImage(W, H, color, type, 50, noise: true);
                if (alpha) for (int p = 0; p < W * H; p++) Fill(background, p % W, p / W, 1, 1, Sample(p * 37 % 251 * max / 255));
                for (int k = 0; k < 6; k++)
                {
                    var img = Clone(background);
                    if (k % 2 == 1) Fill(img, 8 + k, 6, 12, 10, Sample(k * 40 * max / 255));
                    list.Add(img);
                }
                break;
            }
            default:
            {
                // two small changes far apart on a busy picture: their bounding box is mostly unchanged pixels
                var img = MakeImage(W, H, color, type, 60, noise: true);
                if (alpha) for (int p = 0; p < W * H; p++) Fill(img, p % W, p / W, 1, 1, Sample(p * 53 % 241 * max / 255));
                for (int k = 0; k < 5; k++)
                {
                    img = Clone(img);
                    Fill(img, 1 + k, 1, 2, 2, Sample((k + 1) * 30 * max / 255));
                    Fill(img, W - 4 - k, H - 4, 2, 2, Sample((k + 1) * 45 * max / 255));
                    list.Add(img);
                }
                break;
            }
        }
        return list;
    }

    static RawImage StackVisible(IReadOnlyList<RawImage> frames)
    {
        // what the animation oracle returns: Rgba frames on top of each other, fully transparent pixels zeroed
        var first = ToRgba(frames[0]);
        var all = RawImage.Create(first.Width, first.Height * frames.Count, ColorModel.Rgba, first.SampleType);
        int frameBytes = first.GetBytes().Length, px = frameBytes / (first.Width * first.Height);
        for (int i = 0; i < frames.Count; i++)
        {
            var dst = all.GetBytes().Slice(i * frameBytes, frameBytes);
            ToRgba(frames[i]).GetBytes().CopyTo(dst);
            for (int p = 0; p < dst.Length; p += px)
                if (dst.Slice(p + px * 3 / 4, px / 4).IndexOfAnyExcept((byte)0) < 0) dst.Slice(p, px).Clear();
        }
        return all;
    }

    public static IEnumerable<object[]> OptimizeCases()
    {
        foreach (var kind in new[] { "sprite", "flash", "spots" })
            foreach (var row in PngTests.EncodeCases())
                yield return new object[] { kind, row[0], row[1] };
    }

    [Theory]
    [MemberData(nameof(OptimizeCases))]
    public void OptimizedFramesDecodeToTheSameAnimation(string kind, ColorModel color, SampleType type)
    {
        var images = Scenario(kind, color, type);
        var frames = images.Select((img, i) => new PngInputFrame(img, 1 + i, 20)).ToList();
        bool alpha = color is ColorModel.GrayAlpha or ColorModel.Rgba;
        byte[] plain = Png.EncodeAnimation(frames);
        foreach (bool interlace in new[] { false, true })
        {
            var png = Png.EncodeAnimation(frames, new PngEncodeOptions { OptimizeFrames = true, Interlace = interlace, LoopCount = 2 });
            var anim = Png.DecodeAnimation(png);
            Assert.Equal((images.Count, 2), (anim.FrameCount, anim.LoopCount));
            for (int i = 0; i < images.Count; i++)
            {
                TestUtil.AssertEqual(ToRgba(images[i]), anim.Frames[i].Image);
                Assert.Equal((1 + i, 20), (anim.Frames[i].DelayNumerator, anim.Frames[i].DelayDenominator));
            }
            TestUtil.AssertEqual(images[0], Png.Decode(png));
            if (interlace) continue;

            Assert.True(png.Length <= plain.Length, $"{png.Length} > {plain.Length}");
            var ops = anim.Frames.Select(f => (f.Disposal, f.Blend)).ToList();
            Assert.Equal(PngDisposal.None, ops[^1].Disposal);
            Assert.Equal(PngBlend.Source, ops[0].Blend);
            switch (kind)
            {
                case "sprite" when alpha:
                    // clearing the old sprite beats redrawing the union of old and new position
                    Assert.Contains(ops, o => o.Disposal == PngDisposal.Background);
                    Assert.True(png.Length < plain.Length, $"{png.Length} vs {plain.Length}");
                    break;
                case "flash":
                    // the box goes away by restoring what was there: the following frame has nothing left to draw
                    Assert.Contains(ops, o => o.Disposal == PngDisposal.Previous);
                    Assert.Contains(anim.Frames, f => f.Index > 1 && (f.Width, f.Height) == (1, 1));
                    Assert.True(png.Length < plain.Length, $"{png.Length} vs {plain.Length}");
                    break;
                case "spots" when alpha:
                    // unchanged pixels inside the bounding box become transparent
                    Assert.Contains(ops, o => o.Blend == PngBlend.Over);
                    Assert.True(png.Length < plain.Length, $"{png.Length} vs {plain.Length}");
                    break;
            }
            if (!alpha) Assert.DoesNotContain(ops, o => o.Blend == PngBlend.Over);

            // third-party decoders show the same frames (null where the installed ones cannot verify the file:
            // 16 bit animations need libjxl next to ffmpeg, see decode_to_ref.py)
            var oracle = PngFeatureTests.OracleDecode(png, ".anim.png");
            if (oracle != null) TestUtil.AssertEqual(StackVisible(images), oracle);
        }
    }

    [Fact]
    public void OptimizedFramesHandleTranslucentPixels()
    {
        // translucent pixels can only be drawn by replacing, or by blending onto a fully transparent canvas
        foreach (var type in new[] { SampleType.UInt8, SampleType.UInt16 })
        {
            var images = new List<RawImage>();
            for (int k = 0; k < 6; k++)
            {
                var img = MakeImage(33, 21, ColorModel.Rgba, type, 70, noise: true);   // same translucent noise in every frame
                var patch = MakeImage(6, 5, ColorModel.Rgba, type, 80 + k, noise: true);
                int px = 4 * RawImage.BytesPerSample(type);
                for (int y = 0; y < 5; y++) patch.GetBytes().Slice(y * 6 * px, 6 * px).CopyTo(img.GetBytes().Slice(((3 + 2 * k + y) * 33 + 4 * k) * px));
                if (k == 3) img.GetBytes().Slice(0, 33 * 7 * px).Clear();               // a transparent band appears ...
                images.Add(img);
            }
            var frames = images.Select(i => new PngInputFrame(i)).ToList();
            var png = Png.EncodeAnimation(frames, new PngEncodeOptions { OptimizeFrames = true });
            var anim = Png.DecodeAnimation(png);
            for (int i = 0; i < images.Count; i++) TestUtil.AssertEqual(images[i], anim.Frames[i].Image);
            Assert.True(png.Length <= Png.EncodeAnimation(frames).Length);
        }
    }

    [Fact]
    public void AnimationsShareLosslessTransforms()
    {
        // palette: 5 frames drawn from 40 colors in total
        var rnd = new Random(9);
        var colors = new byte[40 * 4];
        rnd.NextBytes(colors);
        for (int i = 0; i < 40; i++) colors[i * 4 + 3] = (byte)(i % 5 == 0 ? rnd.Next(256) : 255);
        List<RawImage> Paletted(ColorModel color, int perFrame)
        {
            var list = new List<RawImage>();
            int ch = RawImage.ChannelCount(color);
            for (int k = 0; k < 5; k++)
            {
                var img = RawImage.Create(24, 18, color, SampleType.UInt8);
                var d = img.GetData<byte>();
                for (int p = 0; p < 24 * 18; p++)
                    colors.AsSpan(((p / 24 < 9 ? p % 7 : k * 8 + rnd.Next(perFrame)) % 40) * 4, ch).CopyTo(d.AsSpan(p * ch));
                list.Add(img);
            }
            return list;
        }
        foreach (var color in new[] { ColorModel.Rgb, ColorModel.Rgba })
            foreach (bool optimize in new[] { false, true })
            {
                var images = Paletted(color, 8);
                var frames = images.Select(i => new PngInputFrame(i)).ToList();
                var opt = new PngEncodeOptions { AutoPalette = true, OptimizeFrames = optimize };
                var png = Png.EncodeAnimation(frames, opt);
                Assert.Equal((8, 3), (png[24], png[25]));   // 40 colors: 8 bit palette
                var anim = Png.DecodeAnimation(png);
                for (int i = 0; i < 5; i++) TestUtil.AssertEqual(ToRgba(images[i]), anim.Frames[i].Image);
                Assert.Equal(color, Png.Decode(png).Color);
                TestUtil.AssertEqual(images[0], Png.Decode(png));
                Assert.True(png.Length < Png.EncodeAnimation(frames, new PngEncodeOptions { OptimizeFrames = optimize }).Length);
                Assert.InRange(Png.ReadPalette(png)!.Length / 4, 30, 40);
                var oracle = PngFeatureTests.OracleDecode(png, ".anim.png");
                if (oracle != null) TestUtil.AssertEqual(StackVisible(images), oracle);
            }
        // one frame with too many colors keeps the whole animation in true color
        var many = Paletted(ColorModel.Rgb, 8);
        new Random(1).NextBytes(many[3].GetData<byte>());
        var truecolor = Png.EncodeAnimation(many.Select(i => new PngInputFrame(i)), new PngEncodeOptions { AutoPalette = true });
        Assert.Equal(2, truecolor[25]);
        for (int i = 0; i < 5; i++) TestUtil.AssertEqual(ToRgba(many[i]), Png.DecodeAnimation(truecolor).Frames[i].Image);

        // gray depth: every frame has to fit
        List<RawImage> Grays(int step, int lastStep)
        {
            var list = new List<RawImage>();
            for (int k = 0; k < 4; k++)
            {
                var img = RawImage.Create(20, 10, ColorModel.Gray, SampleType.UInt8);
                int s = k == 3 ? lastStep : step;
                for (int p = 0; p < 200; p++) img.GetData<byte>()[p] = (byte)(rnd.Next(255 / s + 1) * s);
                img.GetData<byte>()[0] = (byte)s;
                list.Add(img);
            }
            return list;
        }
        foreach (var (step, last, depth) in new[] { (255, 255, 1), (255, 85, 2), (85, 17, 4), (17, 3, 8) })
        {
            var images = Grays(step, last);
            var png = Png.EncodeAnimation(images.Select(i => new PngInputFrame(i)), new PngEncodeOptions { ReduceGrayDepth = true, OptimizeFrames = step == 85 });
            Assert.Equal((depth, 0), (png[24], png[25]));
            var anim = Png.DecodeAnimation(png);
            for (int i = 0; i < 4; i++) TestUtil.AssertEqual(ToRgba(images[i]), anim.Frames[i].Image);
            TestUtil.AssertEqual(images[0], Png.Decode(png));
        }

        // color key: one transparent color across all frames
        foreach (var type in new[] { SampleType.UInt8, SampleType.UInt16 })
            foreach (var color in new[] { ColorModel.GrayAlpha, ColorModel.Rgba })
            {
                long max = type == SampleType.UInt8 ? 255 : 65535;
                int ch = RawImage.ChannelCount(color);
                var images = new List<RawImage>();
                for (int k = 0; k < 4; k++)
                {
                    var img = MakeImage(22, 14, color, type, 90 + k, noise: true);
                    for (int p = 0; p < 22 * 14; p++)
                    {
                        bool hole = (p % 22 + k) % 6 == 0;
                        var sample = new long[ch];
                        for (int c = 0; c < ch - 1; c++) sample[c] = hole ? 7 + c : 100 + (p * 13 + c * 50 + k) % 100;
                        sample[ch - 1] = hole ? 0 : max;
                        Fill(img, p % 22, p / 22, 1, 1, sample);
                    }
                    images.Add(img);
                }
                var frames = images.Select(i => new PngInputFrame(i)).ToList();
                var opt = new PngEncodeOptions { ColorKeyTransparency = true };
                var png = Png.EncodeAnimation(frames, opt);
                Assert.Equal(color == ColorModel.Rgba ? 2 : 0, png[25]);
                var anim = Png.DecodeAnimation(png);
                for (int i = 0; i < 4; i++) TestUtil.AssertEqual(images[i], anim.Frames[i].Image.Color == color ? anim.Frames[i].Image : FromRgba(anim.Frames[i].Image, color));
                Assert.Equal(color, Png.Decode(png).Color);
                // a second transparent color in a later frame: the alpha channel stays
                Fill(images[2], 1, 1, 1, 1, Enumerable.Repeat(0L, ch).ToArray());
                Assert.Equal(color == ColorModel.Rgba ? 6 : 4, Png.EncodeAnimation(frames, opt)[25]);
            }
    }

    static RawImage FromRgba(RawImage rgba, ColorModel color)
    {
        if (color == ColorModel.Rgba) return rgba;
        var o = RawImage.Create(rgba.Width, rgba.Height, color, rgba.SampleType);
        int n = rgba.Width * rgba.Height;
        if (rgba.SampleType == SampleType.UInt8)
            for (int i = 0; i < n; i++) { o.GetData<byte>()[i * 2] = rgba.GetData<byte>()[i * 4]; o.GetData<byte>()[i * 2 + 1] = rgba.GetData<byte>()[i * 4 + 3]; }
        else
            for (int i = 0; i < n; i++) { o.GetData<ushort>()[i * 2] = rgba.GetData<ushort>()[i * 4]; o.GetData<ushort>()[i * 2 + 1] = rgba.GetData<ushort>()[i * 4 + 3]; }
        return o;
    }

    [Fact]
    public void EncodeAnimationValidatesInput()
    {
        var a = MakeImage(8, 6, ColorModel.Rgb, SampleType.UInt8, 1);
        Assert.Throws<ArgumentException>(() => Png.EncodeAnimation(Array.Empty<PngInputFrame>()));
        Assert.Throws<ArgumentException>(() => Png.EncodeAnimation(new[] { new PngInputFrame(a), new PngInputFrame(MakeImage(8, 7, ColorModel.Rgb, SampleType.UInt8, 1)) }));
        Assert.Throws<ArgumentException>(() => Png.EncodeAnimation(new[] { new PngInputFrame(a), new PngInputFrame(MakeImage(8, 6, ColorModel.Rgba, SampleType.UInt8, 1)) }));
        Assert.Throws<ArgumentException>(() => Png.EncodeAnimation(new[] { new PngInputFrame(a), new PngInputFrame(MakeImage(8, 6, ColorModel.Rgb, SampleType.UInt16, 1)) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Png.EncodeAnimation(new[] { new PngInputFrame(a, 70000, 1) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Png.EncodeAnimation(new[] { new PngInputFrame(a) }, new PngEncodeOptions { LoopCount = -1 }));
        Assert.Throws<ImageNotSupportedException>(() => Png.EncodeAnimation(new[] { new PngInputFrame(RawImage.Create(2, 2, ColorModel.Rgb, SampleType.Float32)) }));

        // a lazily produced sequence, a single frame, and the millisecond helper
        IEnumerable<PngInputFrame> Lazy() { yield return PngInputFrame.FromMilliseconds(a, 250); }
        var anim = Png.DecodeAnimation(Png.EncodeAnimation(Lazy()));
        Assert.Equal(250.0, Assert.Single(anim.Frames).DelayMs);
        Assert.True(anim.IsAnimated);

        // metadata of the first frame is written
        a.Metadata = new ImageMetadata { DpiX = 96, DpiY = 96 };
        Assert.Equal(96.0, Png.Decode(Png.EncodeAnimation(new[] { new PngInputFrame(a), new PngInputFrame(a) })).Metadata!.DpiX);
    }
}
