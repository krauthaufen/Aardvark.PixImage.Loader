using System.Buffers.Binary;
using System.Diagnostics;
using PixCodec;
using PixCodec.Compression;
using Xunit;

namespace PixCodec.Tests;

public class PngTests
{
    // ------------------------------------------------------------------ helpers

    static bool IsCorruptSuiteFile(string rel) => Path.GetFileName(rel).StartsWith('x');

    public static IEnumerable<object[]> Fixtures() => TestUtil.Files("png", ".png").Concat(TestUtil.Files("png/apng", ".png"));

    /// <summary>The valid files of the PngSuite (in the test-data repository).</summary>
    public static IEnumerable<object[]> SuiteFiles()
        => TestUtil.ThirdPartyFiles("png/pngsuite", ".png").Where(r => !IsCorruptSuiteFile((string)r[0]));

    public static IEnumerable<object[]> CorruptSuiteFiles()
        => TestUtil.ThirdPartyFiles("png/pngsuite", ".png").Where(r => IsCorruptSuiteFile((string)r[0]));

    /// <summary>Every decodable fixture: ours and, when the test data is there, the PngSuite.</summary>
    static IEnumerable<object[]> AllDecodable()
        => TestUtil.Files("png", ".png").Concat(SuiteFiles()).Concat(TestUtil.Files("png/apng", ".png"));

    internal static byte[] Load(string name) => File.ReadAllBytes(TestUtil.Fixture(name));

    /// <summary>Deterministic image: smooth gradients with edges, or pure noise.</summary>
    internal static RawImage MakeImage(int w, int h, ColorModel color, SampleType type, int seed, bool noise = false)
    {
        var img = RawImage.Create(w, h, color, type);
        int ch = img.Channels;
        var rnd = new Random(seed);
        int max = type == SampleType.UInt8 ? 255 : 65535;
        var values = new int[w * h * ch];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int c = 0; c < ch; c++)
                {
                    double v = noise
                        ? rnd.NextDouble()
                        : 0.5 + 0.35 * Math.Sin(x / (5.0 + c) + seed) * Math.Cos(y / (4.0 + c)) + (((x / 6 + y / 5 + c) & 1) * 0.1) + rnd.NextDouble() * 0.04;
                    values[(y * w + x) * ch + c] = (int)Math.Clamp(Math.Round(v * max), 0, max);
                }
        if (type == SampleType.UInt8) { var d = img.GetData<byte>(); for (int i = 0; i < d.Length; i++) d[i] = (byte)values[i]; }
        else { var d = img.GetData<ushort>(); for (int i = 0; i < d.Length; i++) d[i] = (ushort)values[i]; }
        return img;
    }

    internal static byte[] Chunk(string type, byte[] body)
    {
        var c = new byte[12 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(c, (uint)body.Length);
        for (int i = 0; i < 4; i++) c[4 + i] = (byte)type[i];
        body.CopyTo(c, 8);
        BinaryPrimitives.WriteUInt32BigEndian(c.AsSpan(8 + body.Length), Zlib.Crc32(c.AsSpan(4, 4 + body.Length)));
        return c;
    }

    /// <summary>Splits a PNG into (type, whole chunk bytes) after the signature.</summary>
    internal static List<(string type, byte[] bytes)> Chunks(byte[] png)
    {
        var list = new List<(string, byte[])>();
        for (int pos = 8; pos < png.Length;)
        {
            int len = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos));
            list.Add((System.Text.Encoding.ASCII.GetString(png, pos + 4, 4), png.AsSpan(pos, 12 + len).ToArray()));
            pos += 12 + len;
        }
        return list;
    }

    internal static byte[] Assemble(IEnumerable<byte[]> chunks)
    {
        var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        foreach (var c in chunks) ms.Write(c);
        return ms.ToArray();
    }

    internal static byte[] Ihdr(int w, int h, int depth, int colorType, int interlace = 0)
    {
        var b = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(b, w);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(4), h);
        b[8] = (byte)depth; b[9] = (byte)colorType; b[12] = (byte)interlace;
        return Chunk("IHDR", b);
    }

    // ------------------------------------------------------------------ decoding

    [Fact]
    public void FixturesArePresent() => Assert.True(Fixtures().Count() > 250);

    [TestDataFact]
    public void SuiteFilesArePresent()
    {
        Assert.True(SuiteFiles().Count() > 150);
        Assert.True(CorruptSuiteFiles().Count() >= 14);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void DecodesFixture(string rel) => Decodes(rel);

    [TestDataTheory]
    [MemberData(nameof(SuiteFiles))]
    public void DecodesSuiteFile(string rel) => Decodes(rel);

    static void Decodes(string rel)
    {
        var bytes = Load(rel);
        var expected = TestUtil.LoadRef(TestUtil.Fixture(rel) + ".ref");
        Assert.True(Png.IsMatch(bytes));
        var img = Png.Decode(bytes);
        TestUtil.AssertEqual(expected, img);
        Assert.Equal(RawImage.DefaultColorModel(expected.Channels), img.Color);

        var info = Png.GetInfo(bytes);
        // header level fields: bit depth as stored (8 for palette entries), and exactly what Decode reports as metadata
        var meta = img.Metadata;
        Assert.Equal(new ImageInfo(ImageFormat.Png, img.Width, img.Height, img.Channels, img.SampleType, img.Color, info.FrameCount)
        {
            BitsPerSample = bytes[25] == 3 ? 8 : bytes[24],
            Orientation = meta?.Orientation ?? 0,
            DpiX = meta?.DpiX ?? 0,
            DpiY = meta?.DpiY ?? 0,
            HasIccProfile = meta?.IccProfile != null,
            IsAnimated = rel.Contains("apng"),
        }, info);
        Assert.Equal(rel.Contains("apng") && !rel.Contains("single_frame"), info.FrameCount > 1);

        // same result through the generic entry point and without CRC checks
        TestUtil.AssertEqual(expected, ImageCodec.Decode(bytes));
        TestUtil.AssertEqual(expected, Png.Decode(bytes, new PngDecodeOptions { VerifyChecksums = false }));
    }

    [TestDataTheory]
    [MemberData(nameof(CorruptSuiteFiles))]
    public void RejectsCorruptSuiteFile(string rel)
    {
        var bytes = Load(rel);
        Assert.Throws<ImageFormatException>(() => Png.Decode(bytes));
    }

    [Fact]
    public void ReadsMetadata()
    {
        var img = Png.Decode(Load("png/meta_all.png"));
        var m = img.Metadata;
        Assert.NotNull(m);
        Assert.Equal(0.45455, m!.Gamma, 9);
        Assert.Equal(300.0, m.DpiX);
        Assert.Equal(150.0, m.DpiY);
        Assert.Equal(Load("png/meta_all.icc"), m.IccProfile);
        Assert.Equal(Load("png/meta_all.exif"), m.Exif);
        Assert.Equal(6, m.Orientation);

        // pHYs with unit 0 only gives an aspect ratio
        Assert.Null(Png.Decode(Load("png/meta_phys_aspect.png")).Metadata);
        Assert.Null(Png.Decode(Load("png/m_c2_d08_n_13x9.png")).Metadata);
    }

    [Fact]
    public void IgnoreTransparencyKeepsChannels()
    {
        var opt = new PngDecodeOptions { IgnoreTransparency = true };
        foreach (var (name, channels) in new[] { ("t_gray_d08_n", 1), ("t_gray_d16_i", 1), ("t_gray_d02_n", 1), ("t_rgb_d08_n", 3), ("t_rgb_d16_n", 3), ("t_pal_d4_n", 3), ("t_pal_d8_i", 3) })
        {
            var bytes = Load($"png/{name}.png");
            var full = Png.Decode(bytes);
            var plain = Png.Decode(bytes, opt);
            Assert.Equal(channels, plain.Channels);
            Assert.Equal(channels + 1, full.Channels);
            Assert.Equal(full.SampleType, plain.SampleType);
            var a = full.GetBytes(); var b = plain.GetBytes();
            int bps = RawImage.BytesPerSample(full.SampleType);
            for (int p = 0; p < full.Width * full.Height; p++)
                Assert.True(a.Slice(p * (channels + 1) * bps, channels * bps).SequenceEqual(b.Slice(p * channels * bps, channels * bps)));
        }
    }

    [Fact]
    public void KeepIndexedReturnsIndicesAndPalette()
    {
        foreach (var name in new[] { "m_c3_d01_n_33x17", "m_c3_d02_i_13x9", "m_c3_d04_n_33x17", "m_c3_d08_i_33x17", "t_pal_d2_n", "t_pal_d8_n", "pal_short_d4" })
        {
            var bytes = Load($"png/{name}.png");
            var full = Png.Decode(bytes);
            var idx = Png.Decode(bytes, new PngDecodeOptions { KeepIndexed = true });
            var pal = Png.ReadPalette(bytes);
            Assert.NotNull(pal);
            Assert.Equal(ColorModel.Gray, idx.Color);
            Assert.Equal(SampleType.UInt8, idx.SampleType);
            var i = idx.GetData<byte>(); var f = full.GetData<byte>();
            for (int p = 0; p < i.Length; p++)
                for (int c = 0; c < full.Channels; c++)
                    Assert.Equal(pal![i[p] * 4 + c], f[p * full.Channels + c]);
        }
        Assert.Null(Png.ReadPalette(Load("png/m_c2_d08_n_13x9.png")));
        // no effect on non-palette images
        var rgb = Load("png/m_c2_d08_n_13x9.png");
        TestUtil.AssertEqual(Png.Decode(rgb), Png.Decode(rgb, new PngDecodeOptions { KeepIndexed = true }));
    }

    [Fact]
    public void PaletteIndexBeyondPaletteIsRejectedUnlessAllowed()
    {
        var allow = new PngDecodeOptions { AllowInvalidPaletteIndices = true };
        // 8 bit and 2 bit palette images whose last pixel uses an index the palette does not have
        var wide = new byte[41]; wide[40] = 5;
        var png8 = Assemble(new[]
        {
            Ihdr(40, 1, 8, 3), Chunk("PLTE", new byte[] { 10, 20, 30 }),
            Chunk("IDAT", Zlib.Compress(wide)), Chunk("IEND", Array.Empty<byte>()),
        });
        var png2 = Assemble(new[]
        {
            Ihdr(4, 1, 2, 3), Chunk("PLTE", new byte[] { 10, 20, 30, 40, 50, 60 }),
            Chunk("IDAT", Zlib.Compress(new byte[] { 0, 0b00_01_00_11 })), Chunk("IEND", Array.Empty<byte>()),
        });
        foreach (var png in new[] { png8, png2 })
        {
            Assert.Throws<ImageFormatException>(() => Png.Decode(png));
            Assert.Throws<ImageFormatException>(() => Png.Decode(png, new PngDecodeOptions { KeepIndexed = true }));
            var rgb = Png.Decode(png, allow).GetData<byte>();
            Assert.Equal(new byte[] { 10, 20, 30 }, rgb[..3]);
            Assert.Equal(new byte[] { 0, 0, 0 }, rgb[^3..]);
        }
        Assert.Equal(new byte[] { 0, 1, 0, 3 }, Png.Decode(png2, new PngDecodeOptions { KeepIndexed = true, AllowInvalidPaletteIndices = true }).GetData<byte>());
        Assert.Equal(5, Png.Decode(png8, new PngDecodeOptions { KeepIndexed = true, AllowInvalidPaletteIndices = true }).GetData<byte>()[39]);
    }

    [Fact]
    public void AdlerChecksumIsVerified()
    {
        var good = Load("png/idat_split_small.png");
        var chunks = Chunks(good);
        var ihdr = chunks[0].bytes;
        var iend = chunks[^1].bytes;
        var stream = chunks.Where(c => c.type == "IDAT").SelectMany(c => c.bytes.AsSpan(8, c.bytes.Length - 12).ToArray()).ToArray();
        var noVerify = new PngDecodeOptions { VerifyChecksums = false };

        // wrong Adler-32 (chunk CRCs are valid)
        var bad = (byte[])stream.Clone();
        bad[^1] ^= 1;
        var png = Assemble(new[] { ihdr, Chunk("IDAT", bad), iend });
        Assert.Throws<ImageFormatException>(() => Png.Decode(png));
        TestUtil.AssertEqual(Png.Decode(good), Png.Decode(png, noVerify));

        // all pixels present but the trailer is cut off
        png = Assemble(new[] { ihdr, Chunk("IDAT", stream[..^3]), iend });
        Assert.Throws<ImageFormatException>(() => Png.Decode(png));
        TestUtil.AssertEqual(Png.Decode(good), Png.Decode(png, noVerify));
    }

    [Fact]
    public void PixelStreamInAwkwardPieces()
    {
        // the compressed stream handed over one byte per IDAT chunk, with empty chunks in between
        foreach (var name in new[] { "m_c2_d08_n_33x17", "m_c6_d16_i_33x17", "m_c3_d02_i_13x9", "t_gray_d04_n", "pil_rgb", "stored_level0" })
        {
            var good = Load($"png/{name}.png");
            var chunks = Chunks(good);
            var stream = chunks.Where(c => c.type == "IDAT").SelectMany(c => c.bytes.AsSpan(8, c.bytes.Length - 12).ToArray()).ToArray();
            var list = chunks.TakeWhile(c => c.type != "IDAT").Select(c => c.bytes).ToList();
            list.Add(Chunk("IDAT", Array.Empty<byte>()));
            for (int i = 0; i < stream.Length; i++)
            {
                list.Add(Chunk("IDAT", new[] { stream[i] }));
                if (i % 97 == 0) list.Add(Chunk("IDAT", Array.Empty<byte>()));
            }
            list.Add(chunks[^1].bytes);
            TestUtil.AssertEqual(Png.Decode(good), Png.Decode(Assemble(list)));
            // cut anywhere, it is a truncated image
            for (int cut = 0; cut < stream.Length; cut += Math.Max(1, stream.Length / 23))
            {
                var part = Assemble(new[] { chunks[0].bytes }.Concat(chunks.Skip(1).TakeWhile(c => c.type != "IDAT").Select(c => c.bytes))
                    .Append(Chunk("IDAT", stream[..cut])).Append(chunks[^1].bytes));
                Assert.Throws<ImageFormatException>(() => Png.Decode(part));
                // without verification a cut that only costs the checksum still yields the complete image
                try { TestUtil.AssertEqual(Png.Decode(good), Png.Decode(part, new PngDecodeOptions { VerifyChecksums = false })); }
                catch (ImageFormatException) { }
            }
            foreach (int cut in new[] { 0, 1, 2, 3 })
                Assert.Throws<ImageFormatException>(() => Png.Decode(Assemble(new[] { chunks[0].bytes }
                    .Concat(chunks.Skip(1).TakeWhile(c => c.type != "IDAT").Select(c => c.bytes)).Append(Chunk("IDAT", stream[..cut])).Append(chunks[^1].bytes))));
        }
    }

    [Fact]
    public void SurplusPixelDataIsTolerated()
    {
        // a stream holding more rows than the image has: the image decodes, with and without verification
        var rows = new byte[6 * (1 + 5 * 3)];
        new Random(2).NextBytes(rows);
        for (int y = 0; y < 6; y++) rows[y * 16] = (byte)(y % 5);
        var iend = Chunk("IEND", Array.Empty<byte>());
        var exact = Assemble(new[] { Ihdr(5, 4, 8, 2), Chunk("IDAT", Zlib.Compress(rows.AsSpan(0, 4 * 16))), iend });
        var surplus = Assemble(new[] { Ihdr(5, 4, 8, 2), Chunk("IDAT", Zlib.Compress(rows)), iend });
        var oneByte = Assemble(new[] { Ihdr(5, 4, 8, 2), Chunk("IDAT", Zlib.Compress(rows.AsSpan(0, 4 * 16 + 1))), iend });
        var expected = Png.Decode(exact);
        foreach (var png in new[] { surplus, oneByte })
        {
            TestUtil.AssertEqual(expected, Png.Decode(png));
            TestUtil.AssertEqual(expected, Png.Decode(png, new PngDecodeOptions { VerifyChecksums = false }));
        }
    }

    [Fact]
    public void GetInfoReportsHeaderFields()
    {
        var all = Load("png/meta_all.png");
        var info = Png.GetInfo(all);
        Assert.Equal((8, 6, 300.0, 150.0, true, false), (info.BitsPerSample, info.Orientation, info.DpiX, info.DpiY, info.HasIccProfile, info.IsAnimated));
        Assert.Equal((info.Height, info.Width), (info.OrientedWidth, info.OrientedHeight));

        foreach (var (name, bits) in new[] { ("m_c0_d01_n_13x9", 1), ("m_c0_d02_i_13x9", 2), ("m_c0_d04_n_13x9", 4), ("m_c0_d16_n_13x9", 16),
                                             ("m_c3_d01_n_13x9", 8), ("m_c3_d04_n_13x9", 8), ("m_c6_d16_n_13x9", 16), ("t_gray_d02_n", 2) })
            Assert.Equal(bits, Png.GetInfo(Load($"png/{name}.png")).BitsPerSample);
        Assert.True(Png.GetInfo(Load("png/apng/single_frame.png")).IsAnimated);
        Assert.Equal((true, 12), (Png.GetInfo(Load("png/apng/ops_rgba8_binary.png")).IsAnimated, Png.GetInfo(Load("png/apng/ops_rgba8_binary.png")).FrameCount));

        // everything GetInfo reports sits in front of the first IDAT: that prefix of the file gives the same answer
        int idat = all.AsSpan().IndexOf("IDAT"u8);
        Assert.Equal(info, Png.GetInfo(all.AsSpan(0, idat + 4)));
        Assert.Equal(info, ImageCodec.GetInfo(new MemoryStream(all), 40));

        // eXIf may also follow the pixel data; then only a full decode sees it
        var chunks = Chunks(all);
        var moved = Assemble(chunks.Where(c => c.type != "eXIf" && c.type != "IEND").Select(c => c.bytes)
            .Append(chunks.Single(c => c.type == "eXIf").bytes).Append(chunks[^1].bytes));
        Assert.Equal(0, Png.GetInfo(moved).Orientation);
        Assert.Equal(6, Png.Decode(moved).Metadata!.Orientation);
        Assert.Equal(info with { Orientation = 0 }, Png.GetInfo(moved));

        // ancillary chunks with a wrong CRC are dropped by both
        foreach (var type in new[] { "pHYs", "iCCP", "eXIf" })
        {
            var damaged = Assemble(chunks.Select(c => { var b = (byte[])c.bytes.Clone(); if (c.type == type) b[^1] ^= 1; return b; }));
            var i = Png.GetInfo(damaged);
            var m = Png.Decode(damaged).Metadata!;
            Assert.Equal((m.DpiX, m.DpiY, m.Orientation, m.IccProfile != null), (i.DpiX, i.DpiY, i.Orientation, i.HasIccProfile));
            Assert.NotEqual(info, i);
        }

        // the profile is not inflated for the info: one whose compressed data is corrupt is still announced
        var badProfile = Assemble(chunks.Select(c => c.type == "iCCP" ? Chunk("iCCP", "name\0\0not a zlib stream"u8.ToArray()) : c.bytes));
        Assert.True(Png.GetInfo(badProfile).HasIccProfile);
        Assert.Null(Png.Decode(badProfile).Metadata!.IccProfile);
    }

    [Fact]
    public void GetInfoAgreesWithDecodeOnDamagedTransparency()
    {
        foreach (var name in new[] { "t_gray_d08_n", "t_rgb_d16_n", "t_pal_d4_n" })
        {
            var chunks = Chunks(Load($"png/{name}.png"));
            var damaged = Assemble(chunks.Select(c =>
            {
                var b = (byte[])c.bytes.Clone();
                if (c.type == "tRNS") b[^1] ^= 0x10;
                return b;
            }));
            var img = Png.Decode(damaged);   // the damaged chunk is dropped
            Assert.False(img.HasAlpha);
            var info = Png.GetInfo(damaged);
            Assert.Equal((img.Channels, img.Color), (info.Channels, info.Color));
        }
    }

    [Fact]
    public void ChecksumHandling()
    {
        var good = Load("png/meta_all.png");
        var chunks = Chunks(good);

        byte[] Damage(string type)
        {
            var copy = chunks.Select(c => (c.type, bytes: (byte[])c.bytes.Clone())).ToList();
            var target = copy.First(c => c.type == type).bytes;
            target[^1] ^= 0x55; // last CRC byte
            return Assemble(copy.Select(c => c.bytes));
        }

        foreach (var critical in new[] { "IHDR", "IDAT", "IEND" })
        {
            var bad = Damage(critical);
            Assert.Throws<ImageFormatException>(() => Png.Decode(bad));
            TestUtil.AssertEqual(Png.Decode(good), Png.Decode(bad, new PngDecodeOptions { VerifyChecksums = false }));
        }

        // a damaged ancillary chunk is dropped, the image still decodes
        var badGamma = Png.Decode(Damage("gAMA"));
        TestUtil.AssertEqual(Png.Decode(good), badGamma);
        Assert.Equal(0, badGamma.Metadata!.Gamma);
        Assert.Equal(300.0, badGamma.Metadata.DpiX);
        // an unknown ancillary chunk is never checked
        TestUtil.AssertEqual(Png.Decode(good), Png.Decode(Damage("zzZz")));
    }

    [Fact]
    public void StructuralErrors()
    {
        var good = Load("png/idat_split_small.png");
        var chunks = Chunks(good);
        var ihdr = chunks[0].bytes;
        var idats = chunks.Where(c => c.type == "IDAT").Select(c => c.bytes).ToList();
        var iend = chunks[^1].bytes;
        Assert.True(idats.Count > 3);

        Assert.Throws<ImageFormatException>(() => Png.Decode(Array.Empty<byte>()));
        Assert.Throws<ImageFormatException>(() => Png.Decode(new byte[] { 1, 2, 3 }));
        Assert.Throws<ImageFormatException>(() => Png.Decode(good.AsSpan(0, 8)));
        Assert.Throws<ImageFormatException>(() => Png.GetInfo(good.AsSpan(0, 20)));
        Assert.False(Png.IsMatch(good.AsSpan(0, 7)));
        Assert.False(Png.IsMatch(new byte[16]));

        // missing IEND, missing IDAT, IHDR not first, duplicate IHDR
        Assert.Throws<ImageFormatException>(() => Png.Decode(Assemble(idats.Prepend(ihdr))));
        Assert.Throws<ImageFormatException>(() => Png.Decode(Assemble(new[] { ihdr, iend })));
        Assert.Throws<ImageFormatException>(() => Png.Decode(Assemble(idats.Append(ihdr).Append(iend))));
        Assert.Throws<ImageFormatException>(() => Png.Decode(Assemble(idats.Prepend(ihdr).Prepend(ihdr).Append(iend))));
        // IDAT chunks separated by another chunk
        var split = new List<byte[]> { ihdr, idats[0], Chunk("tEXt", new byte[] { 65, 0, 66 }) };
        split.AddRange(idats.Skip(1)); split.Add(iend);
        Assert.Throws<ImageFormatException>(() => Png.Decode(Assemble(split)));
        // some IDATs missing -> truncated stream
        Assert.Throws<ImageFormatException>(() => Png.Decode(Assemble(idats.Take(idats.Count / 2).Prepend(ihdr).Append(iend))));
        // unknown critical chunk
        Assert.Throws<ImageNotSupportedException>(() => Png.Decode(Assemble(idats.Prepend(Chunk("ABCD", new byte[3])).Prepend(ihdr).Append(iend))));
        // chunk type with a non letter
        Assert.Throws<ImageFormatException>(() => Png.Decode(Assemble(idats.Prepend(Chunk("ab1d", new byte[3])).Prepend(ihdr).Append(iend))));
        // palette image without palette / bad palette length
        var pal = Chunks(Load("png/m_c3_d08_n_13x9.png"));
        Assert.Throws<ImageFormatException>(() => Png.Decode(Assemble(pal.Where(c => c.type != "PLTE").Select(c => c.bytes))));
        Assert.Throws<ImageFormatException>(() => Png.Decode(Assemble(pal.Select(c => c.type == "PLTE" ? Chunk("PLTE", new byte[7]) : c.bytes))));
        // invalid row filter type
        var raw = new byte[] { 9, 1, 2, 3 };
        Assert.Throws<ImageFormatException>(() => Png.Decode(Assemble(new[] { Ihdr(3, 1, 8, 0), Chunk("IDAT", Zlib.Compress(raw)), iend })));
        // garbage instead of a zlib stream
        Assert.Throws<ImageFormatException>(() => Png.Decode(Assemble(new[] { Ihdr(3, 1, 8, 0), Chunk("IDAT", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }), iend })));
        // invalid header fields
        Assert.Throws<ImageFormatException>(() => Png.Decode(Assemble(new[] { Ihdr(0, 1, 8, 0), idats[0], iend })));
        Assert.Throws<ImageFormatException>(() => Png.Decode(Assemble(new[] { Ihdr(3, 1, 8, 0, interlace: 2), idats[0], iend })));
        Assert.Throws<ImageFormatException>(() => Png.Decode(Assemble(new[] { Ihdr(3, 1, 16, 3), idats[0], iend })));
        // trailing bytes after IEND are ignored
        TestUtil.AssertEqual(Png.Decode(good), Png.Decode(good.Concat(new byte[] { 1, 2, 3 }).ToArray()));
    }

    [Fact]
    public void LyingHeaderDoesNotAllocate()
    {
        // claims 40000 x 40000 RGBA 16 bit (12.8 GB) with a few bytes of pixel data
        var tiny = Zlib.Compress(new byte[64]);
        var png = Assemble(new[] { Ihdr(40000, 40000, 16, 6), Chunk("IDAT", tiny), Chunk("IEND", Array.Empty<byte>()) });
        long before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        Assert.Throws<ImageFormatException>(() => Png.Decode(png));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1 << 20);
        Assert.True(sw.ElapsedMilliseconds < 2000);
        Assert.Equal(40000, Png.GetInfo(png).Width);

        // plausible compression ratio but far less data than promised: 4000 x 4000 gray, stream holds one row
        var oneRow = Zlib.Compress(new byte[4001]);
        var png2 = Assemble(new[] { Ihdr(4000, 4000, 8, 0), Chunk("IDAT", oneRow), Chunk("IEND", Array.Empty<byte>()) });
        Assert.Throws<ImageFormatException>(() => Png.Decode(png2));
    }

    // ------------------------------------------------------------------ robustness

    internal static void MustDecodeOrReject(byte[] data, PngDecodeOptions options)
    {
        try
        {
            var img = Png.Decode(data, options);
            Assert.True(img.Width > 0 && img.Height > 0);
        }
        catch (ImageFormatException) { }
        catch (ImageNotSupportedException) { }
        try { Png.GetInfo(data); }
        catch (ImageFormatException) { }
        catch (ImageNotSupportedException) { }
    }

    [Fact]
    public void TruncatedFilesAreRejectedCleanly()
    {
        var sw = Stopwatch.StartNew();
        var noCrc = new PngDecodeOptions { VerifyChecksums = false };
        var def = new PngDecodeOptions();
        int n = 0;
        foreach (var row in AllDecodable())
        {
            var bytes = Load((string)row[0]);
            var offsets = new SortedSet<int>();
            for (int i = 0; i < Math.Min(bytes.Length, 48); i++) offsets.Add(i);
            for (int i = 0; i < 48; i++) offsets.Add((int)((long)bytes.Length * i / 48));
            for (int i = 1; i <= 16 && i < bytes.Length; i++) offsets.Add(bytes.Length - i);
            foreach (int cut in offsets)
            {
                var part = bytes.AsSpan(0, cut).ToArray();
                // a cut file can never decode: the IEND chunk is always lost
                Assert.Throws<ImageFormatException>(() => Png.Decode(part, def));
                MustDecodeOrReject(part, noCrc);
                n++;
            }
        }
        Assert.True(n > 20000);
        Assert.True(sw.Elapsed.TotalSeconds < 60, $"took {sw.Elapsed.TotalSeconds:F1}s");
    }

    [Fact]
    public void CorruptedFilesAreRejectedCleanly()
    {
        var sw = Stopwatch.StartNew();
        var noCrc = new PngDecodeOptions { VerifyChecksums = false };
        var def = new PngDecodeOptions();
        var rnd = new Random(12345);
        int n = 0;
        foreach (var row in AllDecodable())
        {
            var bytes = Load((string)row[0]);
            for (int round = 0; round < 40; round++)
            {
                var copy = (byte[])bytes.Clone();
                int flips = 1 + rnd.Next(3);
                for (int k = 0; k < flips; k++)
                {
                    // bias half of the damage towards the headers, where the structure lives
                    int at = rnd.Next(2) == 0 ? rnd.Next(Math.Min(copy.Length, 64)) : rnd.Next(copy.Length);
                    copy[at] = rnd.Next(4) == 0 ? (byte)rnd.Next(256) : (byte)(copy[at] ^ (1 << rnd.Next(8)));
                }
                MustDecodeOrReject(copy, def);
                MustDecodeOrReject(copy, noCrc);
                n++;
            }
        }
        Assert.True(n > 10000);
        Assert.True(sw.Elapsed.TotalSeconds < 60, $"took {sw.Elapsed.TotalSeconds:F1}s");
    }

    [Fact]
    public void RandomPixelStreamsNeverCrash()
    {
        // valid container, random (but well formed zlib) scanline data: exercises every filter with arbitrary input
        var rnd = new Random(99);
        var iend = Chunk("IEND", Array.Empty<byte>());
        foreach (var (ct, depth) in new[] { (0, 1), (0, 2), (0, 4), (0, 8), (0, 16), (2, 8), (2, 16), (3, 1), (3, 2), (3, 4), (3, 8), (4, 8), (4, 16), (6, 8), (6, 16) })
            foreach (int interlace in new[] { 0, 1 })
                for (int round = 0; round < 6; round++)
                {
                    int w = 1 + rnd.Next(70), h = 1 + rnd.Next(12);
                    var raw = new byte[h * (w * 8 + 1) + 64];
                    rnd.NextBytes(raw);
                    // keep most filter bytes valid so decoding gets past the first row
                    if (round > 0) for (int i = 0; i < raw.Length; i++) if (rnd.Next(3) > 0 && raw[i] > 4 && i % 7 == 0) raw[i] %= 5;
                    var list = new List<byte[]> { Ihdr(w, h, depth, ct, interlace) };
                    if (ct == 3) list.Add(Chunk("PLTE", new byte[3 * (1 + rnd.Next(1 << depth))]));
                    if (ct != 4 && ct != 6 && rnd.Next(2) == 0) list.Add(Chunk("tRNS", new byte[ct == 2 ? 6 : ct == 0 ? 2 : 1]));
                    list.Add(Chunk("IDAT", Zlib.Compress(raw)));
                    list.Add(iend);
                    MustDecodeOrReject(Assemble(list), new PngDecodeOptions());
                }
    }

    // ------------------------------------------------------------------ encoding

    public static IEnumerable<object[]> EncodeCases()
    {
        foreach (var color in new[] { ColorModel.Gray, ColorModel.GrayAlpha, ColorModel.Rgb, ColorModel.Rgba })
            foreach (var type in new[] { SampleType.UInt8, SampleType.UInt16 })
                yield return new object[] { color, type };
    }

    [Theory]
    [MemberData(nameof(EncodeCases))]
    public void EncodeRoundTrips(ColorModel color, SampleType type)
    {
        int seed = (int)color * 10 + (int)type;
        var images = new[]
        {
            MakeImage(1, 1, color, type, seed),
            MakeImage(2, 3, color, type, seed + 1),
            MakeImage(67, 21, color, type, seed + 2),
            MakeImage(131, 9, color, type, seed + 3, noise: true),
        };
        foreach (var img in images)
        {
            foreach (var filter in Enum.GetValues<PngFilterMode>())
                foreach (int level in new[] { 0, 1, 6, 9 })
                {
                    if (level is 0 or 9 && filter is not (PngFilterMode.Auto or PngFilterMode.Paeth)) continue;
                    var png = Png.Encode(img, new PngEncodeOptions { Filter = filter, CompressionLevel = level });
                    var back = Png.Decode(png);
                    Assert.Equal(img.Color, back.Color);
                    TestUtil.AssertEqual(img, back);
                    Assert.Equal(new ImageInfo(ImageFormat.Png, img.Width, img.Height, img.Channels, type, color) { BitsPerSample = type == SampleType.UInt8 ? 8 : 16 }, Png.GetInfo(png));
                }
        }
    }

    [Theory]
    [MemberData(nameof(EncodeCases))]
    public void EncodeIsReadByOracle(ColorModel color, SampleType type)
    {
        int seed = 50 + (int)color * 10 + (int)type;
        // one python start per case: gradient + noise, default (adaptive) and a fixed filter
        var cases = new (RawImage img, PngEncodeOptions opt)[]
        {
            (MakeImage(67, 21, color, type, seed), new PngEncodeOptions()),
            (MakeImage(131, 9, color, type, seed + 1, noise: true), new PngEncodeOptions { CompressionLevel = 9 }),
            (MakeImage(40, 7, color, type, seed + 2), new PngEncodeOptions { Filter = PngFilterMode.Paeth, CompressionLevel = 1 }),
            (MakeImage(40, 7, color, type, seed + 3, noise: true), new PngEncodeOptions { Filter = PngFilterMode.Average, CompressionLevel = 0 }),
        };
        foreach (var (img, opt) in cases)
        {
            var oracle = PngFeatureTests.OracleDecode(Png.Encode(img, opt), ".png");
            if (oracle == null) return;
            TestUtil.AssertEqual(img, oracle);
        }
    }

    [Fact]
    public void EncodeFilterModesAreUsed()
    {
        var img = MakeImage(64, 16, ColorModel.Rgb, SampleType.UInt8, 3);
        foreach (var (mode, expected) in new[] { (PngFilterMode.None, 0), (PngFilterMode.Sub, 1), (PngFilterMode.Up, 2), (PngFilterMode.Average, 3), (PngFilterMode.Paeth, 4) })
        {
            var png = Png.Encode(img, new PngEncodeOptions { Filter = mode });
            var idat = Chunks(png).Single(c => c.type == "IDAT").bytes;
            var raw = Zlib.Decompress(idat.AsSpan(8, idat.Length - 12));
            Assert.Equal(16 * (64 * 3 + 1), raw.Length);
            for (int y = 0; y < 16; y++) Assert.Equal(expected, raw[y * (64 * 3 + 1)]);
        }
        // adaptive beats no filtering on smooth data
        var smooth = MakeImage(200, 100, ColorModel.Rgb, SampleType.UInt8, 4);
        int adaptive = Png.Encode(smooth).Length;
        int none = Png.Encode(smooth, new PngEncodeOptions { Filter = PngFilterMode.None }).Length;
        Assert.True(adaptive < none, $"{adaptive} vs {none}");
    }

    [Fact]
    public void EncodeSplitsLargeStreamsIntoIdatChunks()
    {
        var img = MakeImage(700, 500, ColorModel.Rgb, SampleType.UInt8, 8, noise: true);
        var ms = new WriteOnlyStream();
        Png.Encode(img, ms, new PngEncodeOptions { CompressionLevel = 1 });
        var png = ms.ToArray();
        var chunks = Chunks(png);
        Assert.True(chunks.Count(c => c.type == "IDAT") > 2);
        Assert.Equal("IEND", chunks[^1].type);
        TestUtil.AssertEqual(img, Png.Decode(png));
        var oracle = PngFeatureTests.OracleDecode(png, ".png");
        if (oracle != null) TestUtil.AssertEqual(img, oracle);
    }

    /// <summary>Neither seekable nor readable, like a network stream.</summary>
    sealed class WriteOnlyStream : Stream
    {
        readonly MemoryStream _inner = new();
        public byte[] ToArray() => _inner.ToArray();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    }

    [Fact]
    public void EncodeRejectsUnsupportedImages()
    {
        foreach (var type in new[] { SampleType.UInt32, SampleType.Int8, SampleType.Int16, SampleType.Int32, SampleType.Float16, SampleType.Float32, SampleType.Float64 })
            Assert.Throws<ImageNotSupportedException>(() => Png.Encode(RawImage.Create(2, 2, ColorModel.Rgb, type)));
        Assert.Throws<ImageNotSupportedException>(() => Png.Encode(RawImage.Create(2, 2, ColorModel.Other, SampleType.UInt8, 5)));
        Assert.Throws<ImageNotSupportedException>(() => Png.Encode(RawImage.Create(2, 2, ColorModel.Other, SampleType.UInt8, 3)));
    }

    [Fact]
    public void EncodeWritesMetadata()
    {
        var img = MakeImage(9, 5, ColorModel.Rgb, SampleType.UInt8, 1);
        var icc = new byte[700];
        new Random(4).NextBytes(icc);
        var exif = Load("png/meta_all.exif");
        img.Metadata = new ImageMetadata { Gamma = 0.45455, DpiX = 72, DpiY = 300, IccProfile = icc, Exif = exif };
        var png = Png.Encode(img);
        var back = Png.Decode(png);
        TestUtil.AssertEqual(img, back);
        var m = back.Metadata!;
        Assert.Equal(0.45455, m.Gamma, 9);
        Assert.Equal(72.0, m.DpiX);
        Assert.Equal(300.0, m.DpiY);
        Assert.Equal(icc, m.IccProfile);
        Assert.Equal(exif, m.Exif);
        Assert.Equal(6, m.Orientation);
        // chunk order: color space information before the pixel data
        var types = Chunks(png).Select(c => c.type).ToList();
        Assert.Equal(new[] { "IHDR", "gAMA", "iCCP", "pHYs", "eXIf", "IDAT", "IEND" }, types);

        var oracle = PngFeatureTests.OracleDecode(png, ".png");
        if (oracle != null) TestUtil.AssertEqual(img, oracle);

        // orientation without an EXIF blob is written as a minimal one
        img.Metadata = new ImageMetadata { Orientation = 8 };
        var o = Png.Decode(Png.Encode(img)).Metadata!;
        Assert.Equal(8, o.Orientation);
        Assert.NotNull(o.Exif);
        Assert.Equal(0, o.Gamma);

        // non integer dpi survives within the precision of pHYs (whole pixels per meter)
        img.Metadata = new ImageMetadata { DpiX = 96.5, DpiY = 96.5 };
        Assert.Equal(96.5, Png.Decode(Png.Encode(img)).Metadata!.DpiX, 1);

        img.Metadata = new ImageMetadata { Gamma = 2.2, DpiX = 96, DpiY = 96 };
        Assert.Null(Png.Decode(Png.Encode(img, new PngEncodeOptions { WriteMetadata = false })).Metadata);
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(11, true)]
    [InlineData(16, false)]
    [InlineData(17, true)]
    [InlineData(200, false)]
    [InlineData(256, true)]
    public void AutoPaletteRoundTrips(int colors, bool alpha)
    {
        var rnd = new Random(colors);
        var color = alpha ? ColorModel.Rgba : ColorModel.Rgb;
        int ch = alpha ? 4 : 3;
        var table = new byte[colors * ch];
        rnd.NextBytes(table);
        // distinct colors, a mix of opaque and translucent entries
        for (int i = 0; i < colors; i++)
        {
            table[i * ch] = (byte)i;
            if (alpha && i % 3 != 0) table[i * ch + 3] = 255;
        }
        var img = RawImage.Create(45, 23, color, SampleType.UInt8);
        var d = img.GetData<byte>();
        for (int p = 0; p < 45 * 23; p++)
        {
            int i = p < colors ? p : rnd.Next(colors);
            table.AsSpan(i * ch, ch).CopyTo(d.AsSpan(p * ch));
        }

        var opt = new PngEncodeOptions { AutoPalette = true };
        var png = Png.Encode(img, opt);
        Assert.Equal(3, png[25]); // color type
        Assert.Equal(colors <= 2 ? 1 : colors <= 4 ? 2 : colors <= 16 ? 4 : 8, png[24]);
        Assert.True(png.Length < Png.Encode(img).Length);
        var back = Png.Decode(png);
        Assert.Equal(color, back.Color);
        TestUtil.AssertEqual(img, back);
        Assert.Equal(colors, Png.ReadPalette(png)!.Length / 4);

        var oracle = PngFeatureTests.OracleDecode(png, ".png");
        if (oracle != null) TestUtil.AssertEqual(img, oracle);
    }

    [Fact]
    public void AutoPaletteFallsBack()
    {
        var opt = new PngEncodeOptions { AutoPalette = true };
        // 257 colors: stays true color
        var img = RawImage.Create(257, 1, ColorModel.Rgb, SampleType.UInt8);
        var d = img.GetData<byte>();
        for (int i = 0; i < 257; i++) { d[i * 3] = (byte)i; d[i * 3 + 1] = (byte)(i >> 8); }
        var png = Png.Encode(img, opt);
        Assert.Equal(2, png[25]);
        TestUtil.AssertEqual(img, Png.Decode(png));

        // opaque Rgba keeps its alpha channel through the palette
        var rgba = RawImage.Create(5, 5, ColorModel.Rgba, SampleType.UInt8);
        rgba.GetData<byte>().AsSpan().Fill(255);
        png = Png.Encode(rgba, opt);
        Assert.Equal(3, png[25]);
        var back = Png.Decode(png);
        Assert.Equal(ColorModel.Rgba, back.Color);
        TestUtil.AssertEqual(rgba, back);

        // gray and 16 bit images are never palettized
        var gray = MakeImage(8, 8, ColorModel.Gray, SampleType.UInt8, 1);
        Assert.Equal(0, Png.Encode(gray, opt)[25]);
        var deep = RawImage.Create(4, 4, ColorModel.Rgb, SampleType.UInt16);
        Assert.Equal(2, Png.Encode(deep, opt)[25]);
    }

    [Fact]
    public void GenericEntryPointsUsePng()
    {
        var img = MakeImage(20, 10, ColorModel.Rgba, SampleType.UInt16, 2);
        var png = ImageCodec.Encode(img, ImageFormat.Png);
        Assert.Equal(ImageFormat.Png, ImageCodec.DetectFormat(png));
        Assert.Equal(ImageFormat.Png, ImageCodec.GetInfo(png).Format);
        TestUtil.AssertEqual(img, ImageCodec.Decode(new MemoryStream(png)));
    }
}
