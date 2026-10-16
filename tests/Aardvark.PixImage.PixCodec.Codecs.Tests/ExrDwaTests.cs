using System.Buffers.Binary;
using Aardvark.ImageCodecs;
using Xunit;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>
/// DWAA / DWAB. Decoding the fixtures in exr/dwa is covered by ExrDecodeTests (bit exact against the reference
/// library); here: the encoder and the layout of chunks written before the classification rules were stored.
/// </summary>
public class ExrDwaTests
{
    static float[] Smooth(int w, int h, int c, int seed, float scale = 1)
    {
        var rnd = new Random(seed);
        var f = new float[w * h * c];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int k = 0; k < c; k++)
                    f[(y * w + x) * c + k] = scale * (float)(0.5 + 0.4 * Math.Sin(x / (9.0 + 2 * k) + k) * Math.Cos(y / (7.0 + k))
                        + ((x / 16 + y / 16 + k) % 2) * 0.2 + 0.02 * rnd.NextDouble());
        return f;
    }

    static byte[] Encode(RawImage img, ExrEncodeOptions opt)
    {
        var ms = new MemoryStream();
        Exr.Encode(img, ms, opt);
        return ms.ToArray();
    }

    [Theory]
    [InlineData(ExrCompression.Dwaa, 45f)]
    [InlineData(ExrCompression.Dwab, 45f)]
    [InlineData(ExrCompression.Dwaa, 0f)]
    [InlineData(ExrCompression.Dwab, 5f)]
    [InlineData(ExrCompression.Dwaa, 300f)]
    public void EncodeIsReadByReferenceWithinBound(ExrCompression comp, float level)
    {
        const int w = 150, h = 300;     // several DWAA chunks, two DWAB chunks, partial 8x8 blocks
        var src = Smooth(w, h, 4, 3);
        var img = new RawImage(w, h, ColorModel.Rgba, src);
        var data = Encode(img, new ExrEncodeOptions { Compression = comp, DwaCompressionLevel = level });
        var hdr = Exr.ReadHeaders(data)[0];
        Assert.Equal(comp, hdr.Compression);
        Assert.Equal(level, hdr.FindAttribute("dwaCompressionLevel")!.AsFloat());
        var back = Exr.Decode(data);
        var b = back.GetData<float>();

        // alpha is stored losslessly (as a half)
        for (int i = 3; i < src.Length; i += 4) Assert.Equal((float)(Half)src[i], b[i]);

        // Error bound. Every DCT coefficient is off by at most level / 100000 times its (normalised JPEG) table entry
        // plus half float rounding; a basis function is at most 1/4 (1/8 for DC), so a sample of the perceptually
        // encoded Y'CbCr image is off by at most 0.25 * base * sum(table) (368.8 for luma, 323.8 for chroma, entries
        // divided by the smallest one: 10 and 17). Going back to R'G'B' multiplies chroma errors by at most 1.8556,
        // and the perceptual-to-linear curve has slope <= 2.2 * max(1, value). Rounding of the stored halves adds
        // a little, covered by the constant term.
        double baseErr = level / 100000.0;
        double nl = 0.25 * baseErr * (3688.0 / 10 + 1.8556 * 5505.0 / 17) + 4e-3;
        double max = 0, sum = 0;
        for (int i = 0; i < src.Length; i++)
        {
            if (i % 4 == 3) continue;
            double e = Math.Abs(src[i] - b[i]);
            Assert.True(e <= 2.2 * Math.Max(1, Math.Abs(src[i])) * nl, $"{comp} level {level}: error {e} at {i}");
            max = Math.Max(max, e); sum += e * e;
        }
        // and in practice it is far below the worst case
        double rms = Math.Sqrt(sum / (src.Length * 3 / 4));
        Assert.True(rms < 0.002 + level * 2e-4, $"rms {rms}");
        if (level >= 45) Assert.True(data.Length < w * h * 8 * 0.45, $"size {data.Length}");

        var oracle = ExrOracle.Decode(data, ".exr");
        if (oracle != null) ExrOracle.AssertMatchesLossy(oracle, back);     // the reference decodes our file to the same bits
    }

    [Fact]
    public void HigherLevelMeansSmallerFile()
    {
        var img = new RawImage(200, 160, ColorModel.Rgb, Smooth(200, 160, 3, 5));
        int last = int.MaxValue;
        foreach (float level in new[] { 0f, 20f, 45f, 150f, 500f })
        {
            int size = Encode(img, new ExrEncodeOptions { Compression = ExrCompression.Dwaa, DwaCompressionLevel = level }).Length;
            Assert.True(size < last, $"level {level}: {size} >= {last}");
            last = size;
        }
    }

    [Fact]
    public void EncodeMixedChannelsAndSubsampling()
    {
        // every coding class in one file: an R,G,B triple (as FLOAT), a lone layer.R, Y with subsampled RY / BY,
        // alpha (RLE), depth and ids (lossless zlib)
        const int w = 64, h = 70;
        var f = Smooth(w, h, 4, 7, 3f);
        var g = Array.ConvertAll(Smooth(w, h, 2, 8), v => (Half)v);
        var c2 = Array.ConvertAll(Smooth(w / 2, h / 2, 2, 9, 0.2f), v => (Half)v);
        var ids = new uint[w * h];
        for (int i = 0; i < ids.Length; i++) ids[i] = (uint)(i * 2654435761u);
        var chans = new[]
        {
            new ExrChannelData("R", f, ExrPixelType.Float, 0, 4), new ExrChannelData("G", f, ExrPixelType.Float, 1, 4),
            new ExrChannelData("B", f, ExrPixelType.Float, 2, 4), new ExrChannelData("A", f, null, 3, 4),
            new ExrChannelData("spec.R", g, null, 0, 2), new ExrChannelData("lum.Y", g, null, 1, 2),
            new ExrChannelData("lum.RY", c2, null, 0, 2) { XSampling = 2, YSampling = 2 },
            new ExrChannelData("lum.BY", c2, null, 1, 2) { XSampling = 2, YSampling = 2 },
            new ExrChannelData("Z", f, ExrPixelType.Float, 0, 4), new ExrChannelData("id", ids),
        };
        foreach (var comp in new[] { ExrCompression.Dwaa, ExrCompression.Dwab })
        {
            var ms = new MemoryStream();
            Exr.EncodeChannels(w, h, chans, ms, new ExrEncodeOptions { Compression = comp, DwaCompressionLevel = 20 });
            var data = ms.ToArray();
            var names = Exr.ReadHeaders(data)[0].Channels.Select(c => c.Name).ToArray();
            var back = Exr.DecodeChannels(data, names);
            var oracle = ExrOracle.Decode(data, ".all.exr");
            if (oracle != null) ExrOracle.AssertMatchesLossy(oracle, back);
            Assert.Equal(ids, Exr.DecodeChannels(data, new[] { "id" }).GetData<uint>());
            Assert.Equal(Enumerable.Range(0, w * h).Select(i => f[i * 4]), Exr.DecodeChannels(data, new[] { "Z" }).GetData<float>());
            Assert.Equal(Enumerable.Range(0, w * h).Select(i => (float)(Half)f[i * 4 + 3]), Exr.DecodeChannels(data, new[] { "A" }).GetData<float>());
            var rgb = Exr.Decode(data).GetData<float>();
            for (int i = 0; i < w * h; i++)
                for (int k = 0; k < 3; k++) Assert.InRange(rgb[i * 4 + k] - f[i * 4 + k], -0.15f, 0.15f);
            var lone = Exr.DecodeChannels(data, new[] { "spec.R", "lum.Y" }).GetData<float>();
            for (int i = 0; i < lone.Length; i++) Assert.InRange(lone[i] - (float)g[i], -0.05f, 0.05f);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ChunksWithoutStoredRules(int version)
    {
        // Before version 2 a DWA chunk had no classification rules; channels were classified by fixed, case
        // insensitive names. No such files are around, so one is made from a current chunk (R,G,B,A, one chunk): the
        // rules are cut out and the version field lowered. The reference library still reads this layout.
        var img = new RawImage(40, 24, ColorModel.Rgba, Smooth(40, 24, 4, 11));
        var data = Encode(img, new ExrEncodeOptions { Compression = ExrCompression.Dwaa });
        var expected = Exr.Decode(data);

        int p = 8;
        while (data[p] != 0)
        {
            p = Array.IndexOf(data, (byte)0, p) + 1;
            p = Array.IndexOf(data, (byte)0, p) + 1;
            p += 4 + BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(p));
        }
        int chunk = (int)BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(p + 1));
        int size = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(chunk + 4));
        Assert.Equal(data.Length, chunk + 8 + size);                                  // a single chunk
        Assert.Equal(2, BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(chunk + 8)));
        int rules = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(chunk + 8 + 88));
        var legacy = data.AsSpan(0, chunk + 8 + 88).ToArray().Concat(data.Skip(chunk + 8 + 88 + rules)).ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(legacy.AsSpan(chunk + 4), size - rules);
        BinaryPrimitives.WriteInt64LittleEndian(legacy.AsSpan(chunk + 8), version);

        TestUtil.AssertEqual(expected, Exr.Decode(legacy));
        var oracle = ExrOracle.Decode(legacy, ".exr");
        if (oracle != null) TestUtil.AssertEqual(oracle, expected);
    }
}
