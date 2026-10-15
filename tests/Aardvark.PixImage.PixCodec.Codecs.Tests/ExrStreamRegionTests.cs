using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Aardvark.ImageCodecs;
using Xunit;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>Decoding from streams, regions and the display window; streamed encoding; typed attributes; newer compressions.</summary>
public class ExrStreamRegionTests
{
    public static IEnumerable<object[]> AllExr =>
        new[] { "", "dwa", "yc", "levels", "deep", "selfcheck", "attributes" }.SelectMany(d => TestUtil.Files(Path.Combine("exr", d), ".exr"));

    /// <summary>A read-only seekable stream that counts what is read, and can pretend to have a gap of zeros in the middle.</summary>
    sealed class TracingStream : Stream
    {
        readonly byte[] _head, _tail;
        readonly long _gap;
        long _pos;
        public long BytesRead;
        public TracingStream(byte[] data) : this(data, data.Length, 0) { }
        public TracingStream(byte[] data, int split, long gap) { _head = data.AsSpan(0, split).ToArray(); _tail = data.AsSpan(split).ToArray(); _gap = gap; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _head.Length + _gap + _tail.Length;
        public override long Position { get => _pos; set => _pos = value; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = (int)Math.Min(Math.Min(count, 3000), Length - _pos);       // short reads
            for (int i = 0; i < n; i++)
            {
                long p = _pos + i;
                buffer[offset + i] = p < _head.Length ? _head[p] : p < _head.Length + _gap ? (byte)0 : _tail[p - _head.Length - _gap];
            }
            _pos += n; BytesRead += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => _pos = origin == SeekOrigin.Begin ? offset : origin == SeekOrigin.Current ? _pos + offset : Length + offset;
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    sealed class ForwardOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, 777));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    static string[] Names(ExrHeader h) => h.Channels.Select(c => c.Name).ToArray();

    /// <summary>What a region of an image covering the window src must be: the overlap copied, the rest 0.</summary>
    static RawImage Cut(RawImage full, ExrBox2i src, ExrBox2i dst)
    {
        var r = RawImage.Create((int)dst.Width, (int)dst.Height, full.Color, full.SampleType, full.Channels);
        int es = RawImage.BytesPerSample(full.SampleType) * full.Channels;
        var from = full.GetBytes(); var to = r.GetBytes();
        for (int y = dst.MinY; y <= dst.MaxY; y++)
            for (int x = dst.MinX; x <= dst.MaxX; x++)
                if (x >= src.MinX && x <= src.MaxX && y >= src.MinY && y <= src.MaxY)
                    from.Slice(((y - src.MinY) * full.Width + x - src.MinX) * es, es).CopyTo(to.Slice(((y - dst.MinY) * r.Width + x - dst.MinX) * es));
        return r;
    }

    [Theory]
    [MemberData(nameof(AllExr))]
    public void StreamsAndRegions(string file)
    {
        var data = File.ReadAllBytes(TestUtil.Fixture(file));
        var headers = Exr.ReadHeaders(data);
        Assert.Equal(headers.Length, Exr.ReadHeaders(new TracingStream(data)).Length);
        var rnd = new Random(file.Length);
        for (int p = 0; p < headers.Length; p++)
        {
            var h = headers[p];
            var o = new ExrDecodeOptions { PartIndex = p };
            var full = Exr.Decode(data, o);
            // seekable stream, stream starting at an offset, and a stream that cannot seek
            TestUtil.AssertEqual(full, Exr.Decode(new TracingStream(data), o));
            var shifted = new MemoryStream(new byte[37].Concat(data).ToArray()) { Position = 37 };
            TestUtil.AssertEqual(full, Exr.Decode(shifted, o));
            TestUtil.AssertEqual(full, Exr.Decode(new ForwardOnlyStream(new MemoryStream(data)), o));
            if (h.IsDeep)
            {
                var a = Exr.DecodeDeep(data, null, o); var b = Exr.DecodeDeep(new TracingStream(data), null, o);
                Assert.Equal(a.SampleCounts, b.SampleCounts);
                Assert.Equal(a.GetFloatChannel("Z"), b.GetFloatChannel("Z"));
            }
            else TestUtil.AssertEqual(Exr.DecodeChannels(data, Names(h), o), Exr.DecodeChannels(new TracingStream(data), Names(h), o));

            // the display window, and rectangles that lie inside, across and outside the data window
            var dw = h.DataWindow;
            var windows = new List<ExrBox2i> { h.DisplayWindow, new(dw.MinX, dw.MinY, dw.MinX, dw.MinY), new(dw.MaxX + 3, dw.MinY, dw.MaxX + 5, dw.MinY + 1) };
            for (int i = 0; i < 6; i++)
            {
                int x0 = dw.MinX + rnd.Next(-4, h.Width + 2), y0 = dw.MinY + rnd.Next(-4, h.Height + 2);
                windows.Add(new ExrBox2i(x0, y0, x0 + rnd.Next(0, h.Width + 3), y0 + rnd.Next(0, h.Height + 3)));
            }
            TestUtil.AssertEqual(Cut(full, dw, h.DisplayWindow), Exr.Decode(data, new ExrDecodeOptions { PartIndex = p, Window = ExrWindow.Display }));
            foreach (var win in windows)
            {
                var expected = Cut(full, dw, win);
                foreach (bool parallel in new[] { true, false })
                    TestUtil.AssertEqual(expected, Exr.Decode(data, new ExrDecodeOptions { PartIndex = p, Region = win, Parallel = parallel }));
                TestUtil.AssertEqual(expected, Exr.Decode(new TracingStream(data), new ExrDecodeOptions { PartIndex = p, Region = win }));
                if (h.IsDeep)
                {
                    // the deep samples of the region: same pixels, same samples
                    var all = Exr.DecodeDeep(data, null, o);
                    var part = Exr.DecodeDeep(data, null, new ExrDecodeOptions { PartIndex = p, Region = win });
                    Assert.Equal(((int)win.Width, (int)win.Height), (part.Width, part.Height));
                    float[] za = all.GetFloatChannel("Z"), zp = part.GetFloatChannel("Z"), ra = all.GetFloatChannel("R"), rp = part.GetFloatChannel("R");
                    for (int y = win.MinY; y <= win.MaxY; y++)
                        for (int x = win.MinX; x <= win.MaxX; x++)
                        {
                            int pi = (y - win.MinY) * part.Width + x - win.MinX;
                            bool inside = x >= dw.MinX && x <= dw.MaxX && y >= dw.MinY && y <= dw.MaxY;
                            int ai = inside ? (y - dw.MinY) * all.Width + x - dw.MinX : -1;
                            Assert.Equal(inside ? all.SampleCounts[ai] : 0, part.SampleCounts[pi]);
                            for (int s = 0; s < part.SampleCounts[pi]; s++)
                            {
                                Assert.Equal(za[all.SampleOffsets[ai] + s], zp[part.SampleOffsets[pi] + s]);
                                Assert.Equal(ra[all.SampleOffsets[ai] + s], rp[part.SampleOffsets[pi] + s]);
                            }
                        }
                }
                else
                {
                    var names = Names(h);
                    TestUtil.AssertEqual(Cut(Exr.DecodeChannels(data, names, o), dw, win), Exr.DecodeChannels(data, names, new ExrDecodeOptions { PartIndex = p, Region = win }));
                }
            }
            // regions of lower levels use the level's own pixel grid
            // (the old tiled_mipmap_* / tiled_ripmap_* fixtures of the python bindings only contain level 0)
            if (h.LevelCountX > 1 && h.HasLevel(1, 1) && (file.Contains("levels") || file.Contains("selfcheck")))
            {
                var lo = new ExrDecodeOptions { PartIndex = p, Level = 1 };
                var level = Exr.Decode(data, lo);
                var lw = new ExrBox2i(dw.MinX, dw.MinY, dw.MinX + level.Width - 1, dw.MinY + level.Height - 1);
                var win = new ExrBox2i(dw.MinX + level.Width / 2, dw.MinY - 1, dw.MinX + level.Width + 1, dw.MinY + level.Height / 2);
                TestUtil.AssertEqual(Cut(level, lw, win), Exr.Decode(data, new ExrDecodeOptions { PartIndex = p, Level = 1, Region = win }));
            }
        }
        Assert.Throws<ArgumentException>(() => Exr.Decode(data, new ExrDecodeOptions { Region = new ExrBox2i(5, 5, 4, 9) }));
    }

    static byte[] BigTiled(out RawImage img, ExrCompression comp, int tile)
    {
        const int w = 640, h = 512;
        var f = new float[w * h * 3];
        var rnd = new Random(1);
        for (int i = 0; i < f.Length; i++) f[i] = (float)(Half)(float)(i % 977 / 977.0 + rnd.NextDouble() * 0.2);
        img = new RawImage(w, h, ColorModel.Rgb, f);
        var ms = new MemoryStream();
        Exr.Encode(img, ms, new ExrEncodeOptions { Compression = comp, TileWidth = tile, TileHeight = tile, DataWindowX = 100, DataWindowY = -50 });
        return ms.ToArray();
    }

    [Theory]
    [InlineData(ExrCompression.Piz, 64)]
    [InlineData(ExrCompression.Zip, 0)]
    [InlineData(ExrCompression.Zstd, 32)]
    [InlineData(ExrCompression.None, 0)]
    public void RegionReadsOnlyTheChunksItNeeds(ExrCompression comp, int tile)
    {
        var data = BigTiled(out var img, comp, tile);
        var dw = new ExrBox2i(100, -50, 739, 461);
        var win = new ExrBox2i(300, 200, 363, 231);         // 64x32 of 640x512: 1/160 of the pixels
        var s = new TracingStream(data);
        var part = Exr.Decode(s, new ExrDecodeOptions { Region = win });
        TestUtil.AssertEqual(Cut(img, dw, win), part);
        // tiles: at most 4 tiles of 100 (plus headers and the offset table); scan lines: the blocks of 32 lines of 512
        Assert.True(s.BytesRead < data.Length / (tile > 0 ? 8 : 6), $"read {s.BytesRead} of {data.Length}");
        // nothing but headers for a region without data
        s = new TracingStream(data);
        var empty = Exr.Decode(s, new ExrDecodeOptions { Region = new ExrBox2i(-500, -500, -490, -490) });
        Assert.All(empty.GetData<float>(), v => Assert.Equal(0f, v));
        Assert.True(s.BytesRead <= 70000);
    }

    [Fact]
    public void FilesBeyondTwoGigabytes()
    {
        // a file whose chunks lie beyond 3 GB: the headers and tables of a real file, a gap, then its chunks
        foreach (int tile in new[] { 0, 64 })
        {
            var data = BigTiled(out var img, ExrCompression.Piz, tile);
            int p = 8;
            while (data[p] != 0)
            {
                p = Array.IndexOf(data, (byte)0, p) + 1;
                p = Array.IndexOf(data, (byte)0, p) + 1;
                p += 4 + BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(p));
            }
            p++;
            int first = (int)BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(p));
            const long gap = 3L << 30;
            var moved = (byte[])data.Clone();
            for (int q = p; q < first; q += 8)
                BinaryPrimitives.WriteInt64LittleEndian(moved.AsSpan(q), BinaryPrimitives.ReadInt64LittleEndian(moved.AsSpan(q)) + gap);
            var s = new TracingStream(moved, first, gap);
            Assert.True(s.Length > int.MaxValue);
            TestUtil.AssertEqual(img, Exr.Decode(s));
            var win = new ExrBox2i(120, 0, 200, 40);
            s.Position = 0;        // a stream is read from its current position
            TestUtil.AssertEqual(Cut(img, new ExrBox2i(100, -50, 739, 461), win), Exr.Decode(s, new ExrDecodeOptions { Region = win }));
            Assert.True(s.BytesRead < 2 * data.Length);
            // an offset table that is gone is rebuilt by walking the chunks, also beyond 2 GB (for tiles this reads only chunk headers)
            Array.Clear(moved, p, first - p);
            if (tile > 0) TestUtil.AssertEqual(img, Exr.Decode(new TracingStream(moved, first, 0)));
        }
    }

    [Fact]
    public void ResultsTooLargeForOneArrayPointToRegions()
    {
        var data = File.ReadAllBytes(TestUtil.Fixture("exr/rgb_half_zip.exr"));
        var ex = Assert.Throws<ImageNotSupportedException>(() => Exr.Decode(data, new ExrDecodeOptions { Region = new ExrBox2i(0, 0, 59999, 59999) }));
        Assert.Contains("Region", ex.Message);
    }

    // ------------------------------------------------------------------ streamed encoding

    sealed class WriteOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }

    [Fact]
    public void SeekableAndForwardOnlyOutputAreIdentical()
    {
        // a seekable output is written chunk batch by chunk batch with the offset table filled in at the end
        var f = new float[600 * 450 * 4];
        var rnd = new Random(2);
        for (int i = 0; i < f.Length; i++) f[i] = (float)rnd.NextDouble();
        var img = new RawImage(600, 450, ColorModel.Rgba, f);
        foreach (var opt in new Func<ExrEncodeOptions>[]
        {
            () => new ExrEncodeOptions { Compression = ExrCompression.Piz },
            () => new ExrEncodeOptions { Compression = ExrCompression.Rle, LineOrder = ExrLineOrder.DecreasingY },
            () => new ExrEncodeOptions { Compression = ExrCompression.Piz, TileWidth = 64, TileHeight = 32, LevelMode = ExrLevelMode.MipMap },
            () => new ExrEncodeOptions { Compression = ExrCompression.None, Parallel = false },
        })
        {
            var a = new MemoryStream();
            a.Write(new byte[11]);                 // not at the start of the stream
            Exr.Encode(img, a, opt());
            Assert.Equal(a.Length, a.Position);
            var b = new MemoryStream();
            Exr.Encode(img, new WriteOnlyStream(b), opt());
            Assert.True(a.ToArray().AsSpan(11).SequenceEqual(b.ToArray()));
            TestUtil.AssertEqual(Exr.Decode(b.ToArray()), Exr.Decode(a.ToArray().AsSpan(11)));
        }
        var parts = new[]
        {
            ExrPart.FromImage("a", img, new ExrEncodeOptions { Compression = ExrCompression.Piz }),
            ExrPart.FromImage("b", img, new ExrEncodeOptions { Compression = ExrCompression.B44, TileWidth = 100, TileHeight = 100 }),
        };
        var m1 = new MemoryStream(); var m2 = new MemoryStream();
        Exr.EncodeParts(parts, m1);
        Exr.EncodeParts(parts, new WriteOnlyStream(m2));
        Assert.Equal(m1.ToArray(), m2.ToArray());
    }

    // ------------------------------------------------------------------ attributes

    [Fact]
    public void TypedAttributesOfAReferenceFile()
    {
        var h = Exr.ReadHeaders(File.ReadAllBytes(TestUtil.Fixture("exr/attributes/attributes.exr")))[0];
        var tc = h.FindAttribute("timeCode")!.AsTimeCode();
        Assert.Equal((12, 34, 56, 23, true, false, 0x87654321u), (tc.Hours, tc.Minutes, tc.Seconds, tc.Frame, tc.DropFrame, tc.ColorFrame, tc.UserData));
        Assert.Equal("12:34:56;23", tc.ToString());
        Assert.Equal(new ExrKeyCode(1, 2, 3, 4, 5, 4, 64), h.FindAttribute("keyCode")!.AsKeyCode());
        Assert.Equal(ExrEnvMap.Cube, h.FindAttribute("envmap")!.AsEnvMap());
        Assert.Equal(new[] { "left", "right" }, h.FindAttribute("multiView")!.AsStringVector());
        Assert.Equal(Enumerable.Range(0, 16).Select(i => (float)i), h.FindAttribute("worldToCamera")!.AsM44f());
        Assert.Equal(Enumerable.Range(0, 9).Select(i => (float)i), h.FindAttribute("m33")!.AsM33f());
        Assert.Equal(Enumerable.Range(0, 16).Select(i => (double)i), h.FindAttribute("m44d")!.AsM44d());
        Assert.Equal((1, 2, 3), h.FindAttribute("v3i")!.AsV3i());
        Assert.Equal((1.5, 2.5), h.FindAttribute("v2d")!.AsV2d());
        Assert.Equal((1.5, 2.5, 3.5), h.FindAttribute("v3d")!.AsV3d());
        Assert.Equal((0.5f, 1.5f, 2.5f, 3.5f), h.FindAttribute("box2f")!.AsBox2f());
        Assert.Equal(new[] { 1f, 2f, 3.5f }, h.FindAttribute("fv")!.AsFloatVector());
        Assert.Equal(100f, h.FindAttribute("whiteLuminance")!.AsFloat());
        Assert.Equal((0.3127f, 0.329f), h.FindAttribute("adoptedNeutral")!.AsV2f());
        var preview = h.FindAttribute("preview")!.AsPreview();
        Assert.Equal((5, 3, ColorModel.Rgba, SampleType.UInt8), (preview.Width, preview.Height, preview.Color, preview.SampleType));
        var px = preview.GetData<byte>();
        for (int y = 0; y < 3; y++)
            for (int x = 0; x < 5; x++)
                Assert.Equal(new byte[] { (byte)(10 * y + x), 20, (byte)(30 + x), (byte)(255 - y) }, px.AsSpan((y * 5 + x) * 4, 4).ToArray());
        // an (empty) ID manifest: stored as uncompressed size + zlib stream
        var manifest = h.FindAttribute("idmanifest")!;
        Assert.Equal("idmanifest", manifest.TypeName);
        Assert.Equal(BinaryPrimitives.ReadInt64LittleEndian(manifest.Value), manifest.AsIdManifestData().Length);
        Assert.Throws<ImageFormatException>(() => h.FindAttribute("envmap")!.AsTimeCode());
        Assert.Throws<ImageFormatException>(() => new ExrAttribute("x", "preview", new byte[12]).AsPreview());
        Assert.Throws<ImageFormatException>(() => new ExrAttribute("x", "idmanifest", new byte[] { 255, 255, 255, 255, 0, 0, 0, 0, 1, 2 }).AsIdManifestData());
    }

    [Fact]
    public void AttributeFactoriesRoundTripAndAreReadByTheReference()
    {
        var pre = new RawImage(4, 2, ColorModel.Rgba, Enumerable.Range(0, 32).Select(i => (byte)(i * 7)).ToArray());
        var manifestData = Enumerable.Range(0, 300).Select(i => (byte)(i % 13)).ToArray();
        var opt = new ExrEncodeOptions();
        var attrs = new[]
        {
            ExrAttribute.TimeCode("timeCode", new ExrTimeCode(23, 59, 58, 29, dropFrame: true, userData: 0xdeadbeef)),
            ExrAttribute.KeyCode("keyCode", new ExrKeyCode(9, 8, 7, 6, 5, 4, 64)),
            ExrAttribute.EnvMap("envmap", ExrEnvMap.LatLong),
            ExrAttribute.Rational("framesPerSecond", 24000, 1001),
            ExrAttribute.StringVector("multiView", new[] { "left", "right", "" }),
            ExrAttribute.M33f("m33", Enumerable.Range(0, 9).Select(i => i * 0.5f).ToArray()),
            ExrAttribute.M33d("m33d", Enumerable.Range(0, 9).Select(i => i * 0.25).ToArray()),
            ExrAttribute.M44d("m44d", Enumerable.Range(0, 16).Select(i => i * 0.125).ToArray()),
            ExrAttribute.V3i("v3i", -1, 2, -3),
            ExrAttribute.V2d("v2d", 1e100, -2),
            ExrAttribute.V3d("v3d", 1, 2, 3),
            ExrAttribute.Box2f("box2f", -1, -2, 3, 4),
            ExrAttribute.FloatVector("fv", new[] { 1f, 2f }),
            ExrAttribute.DeepImageState("deepImageState", ExrDeepImageState.Tidy),
            ExrAttribute.Preview("preview", pre),
            ExrAttribute.IdManifest("idmanifest", manifestData),
        };
        foreach (var a in attrs) opt.Attributes.Add(a);
        var ms = new MemoryStream();
        var img = new RawImage(6, 5, ColorModel.Rgb, Enumerable.Range(0, 90).Select(i => (float)(Half)(i / 7f)).ToArray());
        Exr.Encode(img, ms, opt);
        var h = Exr.ReadHeaders(ms.ToArray())[0];
        var tc = h.FindAttribute("timeCode")!.AsTimeCode();
        Assert.Equal((23, 59, 58, 29, true, 0xdeadbeefu), (tc.Hours, tc.Minutes, tc.Seconds, tc.Frame, tc.DropFrame, tc.UserData));
        Assert.Equal(new ExrKeyCode(9, 8, 7, 6, 5, 4, 64), h.FindAttribute("keyCode")!.AsKeyCode());
        Assert.Equal(ExrEnvMap.LatLong, h.FindAttribute("envmap")!.AsEnvMap());
        Assert.Equal((24000, 1001u), h.FindAttribute("framesPerSecond")!.AsRational());
        Assert.Equal(new[] { "left", "right", "" }, h.FindAttribute("multiView")!.AsStringVector());
        Assert.Equal(4f, h.FindAttribute("m33")!.AsM33f()[8]);
        Assert.Equal(2.0, h.FindAttribute("m33d")!.AsM33d()[8]);
        Assert.Equal(1.875, h.FindAttribute("m44d")!.AsM44d()[15]);
        Assert.Equal((-1, 2, -3), h.FindAttribute("v3i")!.AsV3i());
        Assert.Equal((1e100, -2.0), h.FindAttribute("v2d")!.AsV2d());
        Assert.Equal((-1f, -2f, 3f, 4f), h.FindAttribute("box2f")!.AsBox2f());
        Assert.Equal(ExrDeepImageState.Tidy, h.FindAttribute("deepImageState")!.AsDeepImageState());
        TestUtil.AssertEqual(pre, h.FindAttribute("preview")!.AsPreview());
        Assert.Equal(manifestData, h.FindAttribute("idmanifest")!.AsIdManifestData());
        Assert.Throws<ArgumentException>(() => new ExrTimeCode(24, 0, 0, 0));
        Assert.Throws<ArgumentException>(() => ExrAttribute.Preview("preview", RawImage.Create(2, 2, ColorModel.Rgb, SampleType.UInt8)));
        // The reference library reads the file with these attributes. Its python bindings cannot convert the
        // types named in the environment variable below to python objects and refuse the whole file, so those are
        // left out of the file given to the oracle.
        // The manifest payload above is arbitrary bytes, which the library rightly rejects after decompressing it; the
        // oracle file carries the (empty) manifest of the reference fixture, re-wrapped by our factory.
        var skip = (Environment.GetEnvironmentVariable("EXR_ATTR_SKIP") ?? "deepImageState").Split(',');
        var o2 = new ExrEncodeOptions();
        foreach (var a in attrs) if (!skip.Contains(a.Name) && a.Name != "idmanifest") o2.Attributes.Add(a);
        var refManifest = Exr.ReadHeaders(File.ReadAllBytes(TestUtil.Fixture("exr/attributes/attributes.exr")))[0].FindAttribute("idmanifest")!.AsIdManifestData();
        o2.Attributes.Add(ExrAttribute.IdManifest("idmanifest", refManifest));
        var ms2 = new MemoryStream();
        Exr.Encode(img, ms2, o2);
        var oracle = ExrOracle.Decode(ms2.ToArray(), ".exr");
        if (oracle != null) TestUtil.AssertEqual(oracle, Exr.Decode(ms2.ToArray()));
    }

    // ------------------------------------------------------------------ level filter

    [Fact]
    public void TriangleLevelFilter()
    {
        const int w = 37, h = 22;
        var f = new float[w * h];
        for (int i = 0; i < f.Length; i++) f[i] = (i % w + i / w) % 2;       // checkerboard: the worst case for aliasing
        var img = new RawImage(w, h, ColorModel.Gray, f);
        RawImage Level(ExrLevelFilter filter, int level, RawImage src, bool roundUp = false)
        {
            var ms = new MemoryStream();
            Exr.Encode(src, ms, new ExrEncodeOptions { TileWidth = 16, TileHeight = 16, LevelMode = ExrLevelMode.MipMap, LevelFilter = filter, PixelType = ExrPixelType.Float, LevelRoundUp = roundUp });
            return Exr.Decode(ms.ToArray(), new ExrDecodeOptions { Level = level });
        }
        // both filters turn the checkerboard into flat grey; sizes are those of the level
        foreach (var filter in new[] { ExrLevelFilter.Box, ExrLevelFilter.Triangle })
        {
            var l1 = Level(filter, 1, img);
            Assert.Equal((18, 11), (l1.Width, l1.Height));
            // (the triangle filter repeats edge samples, so only the interior is exactly grey)
            var g = l1.GetData<float>();
            for (int y = 1; y < 10; y++) for (int x = 1; x < 17; x++) Assert.Equal(0.5f, g[y * 18 + x], 5);
            Assert.Equal((19, 11), (Level(filter, 1, img, true).Width, Level(filter, 1, img, true).Height));
            // a constant stays constant down to 1x1, and level 0 is untouched
            var c = new RawImage(w, h, ColorModel.Gray, Enumerable.Repeat(0.75f, w * h).ToArray());
            Assert.Equal(0.75f, Level(filter, 5, c).GetData<float>()[0]);
            TestUtil.AssertEqual(img, Level(filter, 0, img));
        }
        // a single bright pixel: the box filter puts it into one pixel of level 1, the triangle filter spreads it
        var dot = new float[w * h];
        dot[9 * w + 9] = 16;
        var box = Level(ExrLevelFilter.Box, 1, new RawImage(w, h, ColorModel.Gray, dot)).GetData<float>();
        var tri = Level(ExrLevelFilter.Triangle, 1, new RawImage(w, h, ColorModel.Gray, dot)).GetData<float>();
        Assert.Equal((4f, 1), (box.Max(), box.Count(v => v != 0)));
        Assert.Equal((16 * 0.375f * 0.375f, 4), (tri.Max(), tri.Count(v => v != 0)));
        Assert.Equal(box.Sum(), tri.Sum(), 4);
    }

    // ------------------------------------------------------------------ newer compressions

    [Fact]
    public void LossyHtj2kIsReportedButNotDecoded()
    {
        // LJ2K (OpenEXR 3.5): headers and info work, pixels throw ImageNotSupportedException
        var data = File.ReadAllBytes(TestUtil.Fixture("exr/rgb_half_zip.exr"));
        int at = data.AsSpan().IndexOf("compression\0compression\0"u8) + 24 + 4;
        Assert.Equal(3, data[at]);
        data[at] = 12;
        Assert.Equal(ExrCompression.Lj2k, Exr.ReadHeaders(data)[0].Compression);
        Assert.Equal(35, Exr.GetInfo(data).Width);
        Assert.Throws<ImageNotSupportedException>(() => Exr.Decode(data));
        data[at] = 14;
        Assert.Throws<ImageNotSupportedException>(() => Exr.Decode(data));
    }
}
