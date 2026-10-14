using PixCodec;
using Xunit;

namespace PixCodec.Tests;

public class InfoTests
{
    public static IEnumerable<object[]> AllFixtures() => Chunks(TestUtil.FixtureDir);

    /// <summary>The image files in fixtures/ of the test-data repository.</summary>
    public static IEnumerable<object[]> ThirdPartyFixtures()
        => TestUtil.TestDataFixtureDir == null ? Enumerable.Empty<object[]>() : Chunks(TestUtil.TestDataFixtureDir);

    static IEnumerable<object[]> Chunks(string fixtures)
    {
        var exts = new HashSet<string> { ".png", ".jpg", ".jpeg", ".gif", ".tif", ".tiff", ".exr", ".webp", ".jp2", ".j2k", ".j2c", ".jph", ".jhc" };
        return Directory.EnumerateFiles(fixtures, "*", SearchOption.AllDirectories)
            .Where(f => exts.Contains(Path.GetExtension(f).ToLowerInvariant()) && !f.Contains("_tmp"))
            .OrderBy(f => f, StringComparer.Ordinal)
            .Chunk(100)
            .Select(c => new object[] { Path.GetRelativePath(fixtures, c[0]), c });
    }

    sealed class CountingStream(Stream inner, bool seekable) : Stream
    {
        public long BytesRead;
        public override bool CanRead => true;
        public override bool CanSeek => seekable;
        public override bool CanWrite => false;
        public override long Length => seekable ? inner.Length : throw new NotSupportedException();
        public override long Position { get => inner.Position; set { if (!seekable) throw new NotSupportedException(); inner.Position = value; } }
        public override int Read(byte[] buffer, int offset, int count)
        {
            // short reads, as sockets and pipes give
            int r = inner.Read(buffer, offset, Math.Min(count, 1000));
            BytesRead += r;
            return r;
        }
        public override long Seek(long offset, SeekOrigin origin) => seekable ? inner.Seek(offset, origin) : throw new NotSupportedException();
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// The header-only entry points must report exactly what parsing the complete file reports - for every fixture
    /// of every format, from a tiny first read (forcing the grow path), seekable or not.
    /// </summary>
    [TestDataTheory]
    [MemberData(nameof(ThirdPartyFixtures))]
    public void ThirdPartyStreamInfoEqualsFullInfo(string first, string[] files) => StreamInfoEqualsFullInfo(first, files);

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void StreamInfoEqualsFullInfo(string first, string[] files)
    {
        _ = first;
        foreach (var f in files)
        {
            var bytes = File.ReadAllBytes(f);
            ImageInfo expected;
            try { expected = ImageCodec.GetInfo(bytes); }
            catch (Exception e) when (e is ImageFormatException or ImageNotSupportedException)
            {
                // unreadable in full: must be unreadable through the stream path too, with the same kind of error
                Assert.Throws(e.GetType(), () => ImageCodec.GetInfo(new MemoryStream(bytes), 64));
                Assert.False(ImageCodec.TryGetInfo(f, out _));
                continue;
            }
            foreach (int initial in new[] { 64, 300, 8192 })
                foreach (bool seekable in new[] { true, false })
                {
                    var info = ImageCodec.GetInfo(new CountingStream(new MemoryStream(bytes), seekable), initial);
                    Assert.True(expected == info, $"{f} (initial {initial}, seekable {seekable}): {info} != {expected}");
                }
            Assert.Equal(expected, ImageCodec.GetInfo(f));
            Assert.True(ImageCodec.TryGetInfo(f, out var viaTry));
            Assert.Equal(expected, viaTry);
        }
    }

    [Fact]
    public void ReadsOnlyTheHeaders()
    {
        // a 12 MP PNG and JPEG: the info must come from the first read
        var img = RawImage.Create(4000, 3000, ColorModel.Rgb, SampleType.UInt8);
        new Random(5).NextBytes(img.GetData<byte>());
        foreach (var format in new[] { ImageFormat.Png, ImageFormat.Jpeg, ImageFormat.Tiff, ImageFormat.Exr })
        {
            var src = format == ImageFormat.Exr ? RawImage.Create(4000, 3000, ColorModel.Rgb, SampleType.Float32) : img;
            var bytes = ImageCodec.Encode(src, format);
            var s = new CountingStream(new MemoryStream(bytes), true);
            var info = ImageCodec.GetInfo(s);
            Assert.Equal((format, 4000, 3000, 3), (info.Format, info.Width, info.Height, info.Channels));
            Assert.True(s.BytesRead <= 128 * 1024, $"{format}: read {s.BytesRead} of {bytes.Length} bytes");
        }
    }

    [Fact]
    public void MissingAndForeignFiles()
    {
        Assert.False(ImageCodec.TryGetInfo(Path.Combine(TestUtil.FixtureDir, "does-not-exist.png"), out _));
        Assert.Throws<ImageFormatException>(() => ImageCodec.GetInfo(new MemoryStream(new byte[5000])));
        Assert.Throws<ImageFormatException>(() => ImageCodec.GetInfo(new MemoryStream()));
    }
}
