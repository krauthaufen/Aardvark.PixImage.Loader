using Xunit;
using static Aardvark.ImageCodecs.Tests.TestUtil;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>PVR v3 / legacy v2 textures: PVRTexTool fixtures (tests/oracle/gen_pvr.py), round trips, KTX cross checks.</summary>
public class PvrTests
{
    public static IEnumerable<object[]> Fixtures => Files("pvr", ".pvr");

    /// <summary>Tolerance in 8 bit steps against PVRTexTool's 8 bit decode (0: bit exact).</summary>
    static double Tolerance(string name) => name switch
    {
        _ when name.StartsWith("bc") => 4,          // PVRTexTool interpolates the BC1 / BC2 / BC3 colours with other rounding
        _ when name.StartsWith("eac") => 1,         // we return 16 bit EAC values
        "rgbe9995.pvr" => 1,
        "srgb.pvr" => 1,                            // the reference rounds the sRGB encoding PVRTexTool applied
        "r16f.pvr" => 0.125,                        // half float rounding of the reference
        _ => 0,
    };

    /// <summary>Samples scaled to 8 bit units (integers by their range, floats by 255).</summary>
    static double[] Units(RawImage img)
    {
        var d = ToDoubles(img);
        double s = img.SampleType switch
        {
            SampleType.UInt16 => 255.0 / 65535, SampleType.Int16 => 255.0 / 32767, SampleType.UInt32 => 255.0 / uint.MaxValue,
            SampleType.Float16 or SampleType.Float32 or SampleType.Float64 => 255, _ => 1,
        };
        for (int i = 0; i < d.Length; i++) d[i] *= s;
        return d;
    }

    static void AssertClose(RawImage expected, RawImage actual, double tol, string what)
    {
        Assert.Equal((expected.Width, expected.Height, expected.Channels), (actual.Width, actual.Height, actual.Channels));
        if (tol == 0 && expected.SampleType == actual.SampleType) { AssertEqual(expected, actual); return; }
        var e = Units(expected); var a = Units(actual);
        double m = 0;
        for (int i = 0; i < e.Length; i++) m = Math.Max(m, Math.Abs(e[i] - a[i]));
        Assert.True(m <= tol + 1e-9, $"{what}: max difference {m} (8 bit units) exceeds {tol}");
    }

    [Theory, MemberData(nameof(Fixtures))]
    public void DecodeMatchesReference(string file)
    {
        var data = File.ReadAllBytes(Fixture(file));
        var name = Path.GetFileName(file);
        var info = ImageCodec.GetInfo(data);
        Assert.Equal(ImageFormat.Pvr, info.Format);
        Assert.Equal(ImageFormat.Pvr, ImageCodec.FormatFromExtension(file));
        var all = ImageCodec.DecodeAll(data);
        Assert.Equal(info.FrameCount, all.Length);
        var top = ImageCodec.Decode(data);
        Assert.Equal((top.Width, top.Height, top.Channels, top.SampleType, top.Color), (info.Width, info.Height, info.Channels, info.SampleType, info.Color));
        Assert.Equal(info.Transfer == TransferFunction.Unknown ? TransferFunction.Unknown : info.Transfer, top.Metadata?.Transfer ?? TransferFunction.Unknown);
        if (File.Exists(Fixture(file) + ".ref")) AssertClose(LoadRef(Fixture(file) + ".ref"), top, Tolerance(name), name);
        for (int i = 0; File.Exists(Fixture(file) + $".{i}.ref"); i++) AssertClose(LoadRef(Fixture(file) + $".{i}.ref"), all[i], 0, $"{name} image {i}");
        var mips = ImageCodec.DecodeMipmaps(data);
        Assert.Equal(info.MipLevels, mips.Length);
        for (int m = 1; m < mips.Length; m++)
        {
            Assert.Equal((Math.Max(1, info.Width >> m), Math.Max(1, info.Height >> m)), (mips[m].Width, mips[m].Height));
            if (File.Exists(Fixture(file) + $".m{m}.ref")) AssertClose(LoadRef(Fixture(file) + $".m{m}.ref"), mips[m], 0, $"{name} level {m}");
        }
        Assert.Equal(info, ImageCodec.GetInfo(new MemoryStream(data)) with { Metadata = info.Metadata });
    }

    [Fact]
    public void HeaderValues()
    {
        var t = Pvr.GetTextureInfo(File.ReadAllBytes(Fixture("pvr/cube.pvr")));
        Assert.Equal((3, 32, 32, 6, 6, 1), (t.Version, t.Width, t.Height, t.Faces, t.MipLevels, t.Surfaces));
        Assert.Equal("r8g8b8a8", t.Format);
        var a = Pvr.GetTextureInfo(File.ReadAllBytes(Fixture("pvr/array.pvr")));
        Assert.Equal(3, a.Surfaces);
        var p = Pvr.GetTextureInfo(File.ReadAllBytes(Fixture("pvr/pvrtc2.pvr")));
        Assert.Equal(("PVRTC1_2", true), (p.Format, p.IsCompressed));
        Assert.True(Pvr.GetTextureInfo(File.ReadAllBytes(Fixture("pvr/srgb.pvr"))).IsSrgb);
        Assert.Equal(TransferFunction.Srgb, ImageCodec.GetInfo(File.ReadAllBytes(Fixture("pvr/srgb.pvr"))).Transfer);
        var v2 = Pvr.GetTextureInfo(File.ReadAllBytes(Fixture("pvr/v2cubem.pvr")));
        Assert.Equal((2, 6, 6), (v2.Version, v2.Faces, v2.MipLevels));
        var f = Pvr.GetTextureInfo(File.ReadAllBytes(Fixture("pvr/flip.pvr")));
        Assert.True(f.YUp);
        // the flagged file is stored bottom-up: without orientation handling it is the mirror image
        var raw = Pvr.Decode(File.ReadAllBytes(Fixture("pvr/flip.pvr")), new PvrDecodeOptions { ApplyOrientation = false });
        AssertEqual(ImageOps.FlipVertical(LoadRef(Fixture("pvr/flip.pvr.ref"))), raw);
    }

    // ------------------------------------------------------------------ encoding

    static RawImage Sample(int w, int h, ColorModel c, SampleType t, int seed)
    {
        int ch = c == ColorModel.Other ? 2 : RawImage.ChannelCount(c);
        var img = RawImage.Create(w, h, c, t, ch);
        var r = new Random(seed);
        var d = img.Data;
        for (int i = 0; i < d.Length; i++)
        {
            double v = r.NextDouble();
            switch (t)
            {
                case SampleType.UInt8: ((byte[])d)[i] = (byte)(v * 255); break;
                case SampleType.Int8: ((sbyte[])d)[i] = (sbyte)(v * 255 - 128); break;
                case SampleType.UInt16: ((ushort[])d)[i] = (ushort)(v * 65535); break;
                case SampleType.Int16: ((short[])d)[i] = (short)(v * 65535 - 32768); break;
                case SampleType.UInt32: ((uint[])d)[i] = (uint)(v * uint.MaxValue); break;
                case SampleType.Int32: ((int[])d)[i] = (int)(v * uint.MaxValue - 2147483648.0); break;
                case SampleType.Float16: ((Half[])d)[i] = (Half)(v * 8 - 2); break;
                case SampleType.Float32: ((float[])d)[i] = (float)(v * 100 - 20); break;
            }
        }
        return img;
    }

    public static IEnumerable<object[]> PlainCases()
    {
        foreach (var c in new[] { ColorModel.Gray, ColorModel.GrayAlpha, ColorModel.Rgb, ColorModel.Rgba, ColorModel.Other })
            foreach (var t in new[] { SampleType.UInt8, SampleType.Int8, SampleType.UInt16, SampleType.Int16, SampleType.UInt32, SampleType.Int32, SampleType.Float16, SampleType.Float32 })
                yield return new object[] { c, t };
    }

    [Theory, MemberData(nameof(PlainCases))]
    public void UncompressedRoundTrip(ColorModel c, SampleType t)
    {
        var src = Sample(13, 6, c, t, (int)c * 16 + (int)t);
        var enc = Pvr.Encode(src);
        var dec = Pvr.Decode(enc);
        AssertEqual(src, dec);
        Assert.Equal(c, dec.Color);
        var info = ImageCodec.GetInfo(enc);
        Assert.Equal((13, 6, src.Channels, t, c), (info.Width, info.Height, info.Channels, info.SampleType, info.Color));
    }

    public static IEnumerable<object[]> CompressedCases()
    {
        foreach (var c in new[] { TextureCompression.BC1, TextureCompression.BC2, TextureCompression.BC3, TextureCompression.BC4, TextureCompression.BC5,
                                  TextureCompression.BC7, TextureCompression.Etc2, TextureCompression.Eac, TextureCompression.Astc })
            yield return new object[] { c, false };
        yield return new object[] { TextureCompression.BC6H, true };
        yield return new object[] { TextureCompression.Astc, true };
    }

    /// <summary>Our block encoders in a PVR file: the same blocks, decoded the same, as in a KTX file.</summary>
    [Theory, MemberData(nameof(CompressedCases))]
    public void CompressedMatchesKtx(TextureCompression c, bool hdr)
    {
        var src = hdr ? Sample(21, 14, ColorModel.Rgba, SampleType.Float16, 5)
            : c is TextureCompression.BC4 or TextureCompression.BC5 or TextureCompression.Eac ? Sample(21, 14, ColorModel.Other, SampleType.UInt8, 6)
            : MiscFormatTests.RandomInt(21, 14, ColorModel.Rgba, SampleType.UInt8, 7, smooth: true);
        var po = new PvrEncodeOptions { Compression = c, AstcBlockWidth = 6, AstcBlockHeight = 5, GenerateMipmaps = true, Srgb = false };
        var ko = new KtxEncodeOptions { Compression = c, AstcBlockWidth = 6, AstcBlockHeight = 5, GenerateMipmaps = true, Srgb = false, Supercompression = KtxSupercompression.None };
        var bytes = Pvr.Encode(src, po);
        var pvr = Pvr.DecodeMipmaps(bytes);
        var ktx = Ktx.DecodeMipmaps(Ktx.Encode(src, ko));
        Assert.Equal(ktx.Length, pvr.Length);
        for (int m = 0; m < ktx.Length; m++) AssertEqual(ktx[m], pvr[m]);
        Assert.True(Pvr.GetTextureInfo(bytes).IsCompressed);
    }

    [Fact]
    public void StructuresRoundTrip()
    {
        var faces = Enumerable.Range(0, 6).Select(i => (IReadOnlyList<RawImage>)new[] { MiscFormatTests.RandomInt(16, 16, ColorModel.Rgba, SampleType.UInt8, 20 + i) }).ToArray();
        var cube = Pvr.DecodeTexture(Pvr.EncodeCube(faces, new PvrEncodeOptions { GenerateMipmaps = true }));
        Assert.Equal((6, 5), (cube.Faces, cube.MipLevels));
        var kcube = Ktx.DecodeTexture(Ktx.EncodeCube(faces, new KtxEncodeOptions { GenerateMipmaps = true }));
        for (int f = 0; f < 6; f++)
            for (int m = 0; m < 5; m++) AssertEqual(kcube.GetImage(m, 0, f), cube.GetImage(m, 0, f));
        var layers = Enumerable.Range(0, 3).Select(i => (IReadOnlyList<RawImage>)new[] { Sample(12, 5, ColorModel.Rgb, SampleType.UInt16, 30 + i) }).ToArray();
        var arr = Pvr.DecodeTexture(Pvr.EncodeArray(layers));
        Assert.Equal(3, arr.Surfaces);
        for (int i = 0; i < 3; i++) AssertEqual(layers[i][0], arr.GetImage(0, i));
        Assert.Equal(3, ImageCodec.DecodeAll(Pvr.EncodeArray(layers)).Length);
        var slices = Enumerable.Range(0, 5).Select(i => Sample(9, 7, ColorModel.Gray, SampleType.Float32, 40 + i)).ToArray();
        var vol = Pvr.DecodeTexture(Pvr.EncodeVolume(slices, new PvrEncodeOptions { GenerateMipmaps = true }));
        var kvol = Ktx.DecodeTexture(Ktx.EncodeVolume(slices, new KtxEncodeOptions { GenerateMipmaps = true }));
        Assert.Equal((5, kvol.MipLevels), (vol.Depth, vol.MipLevels));
        for (int m = 0; m < vol.MipLevels; m++)
            for (int z = 0; z < Math.Max(1, 5 >> m); z++) AssertEqual(kvol.GetImage(m, 0, 0, z), vol.GetImage(m, 0, 0, z));
        var levels = new[] { Sample(8, 4, ColorModel.Rgba, SampleType.UInt8, 50), Sample(4, 2, ColorModel.Rgba, SampleType.UInt8, 51), Sample(2, 1, ColorModel.Rgba, SampleType.UInt8, 52) };
        var mm = Pvr.DecodeMipmaps(Pvr.EncodeMipmaps(levels));
        for (int m = 0; m < 3; m++) AssertEqual(levels[m], mm[m]);
        Assert.Throws<ArgumentException>(() => Pvr.EncodeMipmaps(new[] { levels[0], levels[2] }));
        Assert.Throws<ImageNotSupportedException>(() => Pvr.Encode(levels[0], new PvrEncodeOptions { Compression = TextureCompression.Uastc }));
        var srgb = Pvr.Encode(levels[0], new PvrEncodeOptions { Srgb = true });
        Assert.True(Pvr.GetTextureInfo(srgb).IsSrgb);
        Assert.Equal(TransferFunction.Srgb, Pvr.Decode(srgb).Metadata!.Transfer);
    }

    /// <summary>Hand built files: astcenc's ASTC blocks (2D, 3D, HDR) behind a PVR v3 header decode like the .astc files.</summary>
    [Theory]
    [InlineData("ldr_5x5", 29, false)]
    [InlineData("hdr_4x4", 27, true)]
    [InlineData("vol_4x4x4", 44, false)]
    [InlineData("vol_3x3x3", 41, false)]
    [InlineData("vol_hdr_4x4x4", 44, true)]
    public void HandBuiltAstc(string name, int id, bool hdr)
    {
        var astc = File.ReadAllBytes(Fixture($"astc/{name}.astc"));
        var h = Astc.ReadHeader(astc);
        var pvr = Header((ulong)id, hdr ? PvrChannelType.SignedFloat : PvrChannelType.UnsignedByteNorm, h.Width, h.Height, h.Depth, astc.AsSpan(16));
        var expected = Astc.DecodeVolume(astc, new AstcDecodeOptions { Profile = hdr ? AstcProfile.Hdr : AstcProfile.Ldr });
        var t = Pvr.DecodeTexture(pvr);
        Assert.Equal(h.Depth, t.Depth);
        for (int z = 0; z < h.Depth; z++) AssertEqual(expected[z], t.GetImage(0, 0, 0, z));
        Assert.Equal(h.Depth, ImageCodec.DecodeAll(pvr).Length);
    }

    /// <summary>A v3 header (no metadata) followed by the given data.</summary>
    internal static byte[] Header(ulong pixelFormat, PvrChannelType ct, int w, int h, int d, ReadOnlySpan<byte> data, int faces = 1, int surfaces = 1, int mips = 1, uint flags = 0)
    {
        var b = new byte[52 + data.Length];
        var s = b.AsSpan();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(s, 0x03525650);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(4), flags);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(8), pixelFormat);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(20), (uint)ct);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(24), (uint)h);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(28), (uint)w);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(32), (uint)d);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(36), (uint)surfaces);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(40), (uint)faces);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(44), (uint)mips);
        data.CopyTo(s.Slice(52));
        return b;
    }

    static ulong Channels(string names, params int[] bits)
    {
        ulong pf = 0;
        for (int k = 0; k < names.Length; k++) pf |= (ulong)names[k] << (8 * k) | (ulong)bits[k] << (32 + 8 * k);
        return pf;
    }

    /// <summary>Hand built uncompressed layouts the tool did not write: intensity, padding channels, packed float, signed and integer types, premultiplied alpha.</summary>
    [Fact]
    public void HandBuiltLayouts()
    {
        // r11g11b10 unsigned float, packed with r in the top bits: r = 1.0, g = 0.5, b = 2.0
        uint word = (15u << 6) << 21 | (14u << 6) << 10 | (16u << 5);
        var f = Pvr.Decode(Header(Channels("rgb", 11, 11, 10), PvrChannelType.UnsignedFloat, 1, 1, 1, BitConverter.GetBytes(word)));
        Assert.Equal(new[] { (Half)1, (Half)0.5, (Half)2 }, f.GetData<Half>());
        // x8 padding, signed bytes, unsigned 16 bit integers
        var s = Pvr.Decode(Header(Channels("rxgb", 8, 8, 8, 8), PvrChannelType.SignedByteNorm, 1, 1, 1, new byte[] { 0x80, 9, 0x7F, 0xFF }));
        Assert.Equal(new sbyte[] { -128, 127, -1 }, s.GetData<sbyte>());
        var u = Pvr.Decode(Header(Channels("gr", 16, 16), PvrChannelType.UnsignedShort, 1, 1, 1, new byte[] { 1, 2, 3, 4 }));
        Assert.Equal(new ushort[] { 0x0403, 0x0201 }, u.GetData<ushort>());
        Assert.Equal(ColorModel.Other, u.Color);
        // intensity alone as gray; 4 bit luminance + alpha packed in a byte (l in the top bits)
        var i = Pvr.Decode(Header(Channels("i", 8), PvrChannelType.UnsignedByteNorm, 2, 1, 1, new byte[] { 7, 200 }));
        Assert.Equal(ColorModel.Gray, i.Color);
        Assert.Equal(new byte[] { 7, 200 }, i.GetData<byte>());
        var la = Pvr.Decode(Header(Channels("la", 4, 4), PvrChannelType.UnsignedByteNorm, 1, 1, 1, new byte[] { 0xF3 }));
        Assert.Equal(ColorModel.GrayAlpha, la.Color);
        Assert.Equal(new byte[] { 255, 51 }, la.GetData<byte>());
        // premultiplied: divided out by default
        var pm = Header(Channels("rgba", 8, 8, 8, 8), PvrChannelType.UnsignedByteNorm, 1, 1, 1, new byte[] { 64, 32, 0, 128 }, flags: 2);
        Assert.Equal(new byte[] { 128, 64, 0, 128 }, Pvr.Decode(pm).GetData<byte>());
        Assert.Equal(new byte[] { 64, 32, 0, 128 }, Pvr.Decode(pm, new PvrDecodeOptions { Unpremultiply = false }).GetData<byte>());
    }

    [Fact]
    public void CubeFaceOrderMetadata()
    {
        // six 1 x 1 faces stored in the order "ZzYyXx"; decoded in +X, -X, +Y, -Y, +Z, -Z order
        var faces = new byte[6 * 4];
        for (int k = 0; k < 6; k++) faces[k * 4] = (byte)k;
        var b = Header(Channels("rgba", 8, 8, 8, 8), PvrChannelType.UnsignedByteNorm, 1, 1, 1, faces, faces: 6).ToList();
        var meta = new List<byte>();
        meta.AddRange(BitConverter.GetBytes(0x03525650u)); meta.AddRange(BitConverter.GetBytes(2u)); meta.AddRange(BitConverter.GetBytes(6u));
        meta.AddRange("ZzYyXx"u8.ToArray());
        b.InsertRange(52, meta);
        var arr = b.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(arr.AsSpan(48), (uint)meta.Count);
        var t = Pvr.DecodeTexture(arr);
        Assert.Equal("ZzYyXx", t.CubeFaceOrder);
        Assert.Equal(new byte[] { 4, 5, 2, 3, 0, 1 }, Enumerable.Range(0, 6).Select(f => t.GetImage(0, 0, f).GetData<byte>()[0]));
    }

    [Theory, MemberData(nameof(Fixtures))]
    public void Robustness(string file)
    {
        var data = File.ReadAllBytes(Fixture(file));
        IconTextureTestUtil.Fuzz(data, d => Pvr.DecodeTexture(d), variants: 30);
        IconTextureTestUtil.AssertRejects(() => Pvr.DecodeTexture(data.AsSpan(0, data.Length - 1).ToArray()));
        IconTextureTestUtil.AssertRejects(() => Pvr.GetInfo(data.AsSpan(0, 30).ToArray()));
    }

    [Fact]
    public void HugeHeaderIsRejected()
    {
        var b = Header(Channels("rgba", 8, 8, 8, 8), PvrChannelType.UnsignedByteNorm, 1 << 20, 1 << 20, 1, new byte[16]);
        IconTextureTestUtil.AssertRejects(() => Pvr.Decode(b));
        var p = Header(3, PvrChannelType.UnsignedByteNorm, 4096, 4096, 1, new byte[64]);
        IconTextureTestUtil.AssertRejects(() => Pvr.Decode(p));
        IconTextureTestUtil.AssertRejects(() => Pvr.Decode(Header(5, PvrChannelType.UnsignedByteNorm, 8, 8, 1, new byte[64])));   // PVRTC2
    }
}
