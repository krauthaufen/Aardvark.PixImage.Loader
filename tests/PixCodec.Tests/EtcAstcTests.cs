using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Xunit;
using static PixCodec.Tests.TestUtil;

namespace PixCodec.Tests;

/// <summary>
/// ETC1 / ETC2 / EAC and ASTC block decoders against tests/fixtures/etc_astc (see tests/oracle/gen_etc_astc.py: raw
/// blocks of one image plus the oracle's decode). Everything is compared bit exact.
/// </summary>
public class EtcAstcTests
{
    public static IEnumerable<object[]> Fixtures => Files("etc_astc", ".bin");

    [Theory, MemberData(nameof(Fixtures))]
    public void DecodeMatchesReference(string file)
    {
        var path = Fixture(file);
        var data = File.ReadAllBytes(path);
        var expected = LoadRef(path + ".ref");
        var name = Path.GetFileNameWithoutExtension(path);
        if (name.StartsWith("astc_")) Astc(name, data, expected);
        else if (name.StartsWith("eac_")) Eac(name, data, expected);
        else Etc(name, data, expected);
    }

    // ------------------------------------------------------------------ ETC

    static void Etc(string name, byte[] data, RawImage expected)
    {
        int w = expected.Width, h = expected.Height, bpb = name.StartsWith("etc2_rgba") ? 16 : 8;
        var got = new byte[w * h * 4];
        Span<byte> blk = stackalloc byte[64];
        for (int by = 0, k = 0; by < h / 4; by++)
            for (int bx = 0; bx < w / 4; bx++, k++)
            {
                var b = data.AsSpan(k * bpb, bpb);
                if (name.StartsWith("etc1")) EtcBlocks.Etc1(b, blk);
                else if (name.StartsWith("etc2_rgba")) EtcBlocks.Etc2Rgba(b, blk);
                else if (name.StartsWith("etc2_a1")) EtcBlocks.Etc2PunchThrough(b, blk);
                else EtcBlocks.Etc2Rgb(b, blk);
                for (int y = 0; y < 4; y++) blk.Slice(y * 16, 16).CopyTo(got.AsSpan(((by * 4 + y) * w + bx * 4) * 4));
            }
        AssertSame(expected.GetBytes().ToArray(), got, w, 4, name);
    }

    // ------------------------------------------------------------------ EAC

    /// <summary>
    /// The oracle (texture2ddecoder) gives 8 bits per channel, the decoder 11: every 11 bit value v must map to the oracle's
    /// byte as v &gt;&gt; 3 (unsigned) or ((v - 1) &gt;&gt; 3) + 128 (signed; the oracle's own conversion, found empirically,
    /// consistent over every value the fixtures produce). The exact 11 bit values are pinned by the specification's
    /// worked examples in <see cref="EacSpecificationExamples"/>.
    /// </summary>
    static void Eac(string name, byte[] data, RawImage expected)
    {
        bool signed = name.StartsWith("eac_r11s") || name.StartsWith("eac_rg11s");
        int ch = name.StartsWith("eac_rg") ? 2 : 1, w = expected.Width, h = expected.Height;
        var exp = expected.GetBytes().ToArray();
        Span<int> v = stackalloc int[16];
        Span<ushort> u = stackalloc ushort[16];
        Span<short> s = stackalloc short[16];
        for (int by = 0, k = 0; by < h / 4; by++)
            for (int bx = 0; bx < w / 4; bx++, k++)
                for (int c = 0; c < ch; c++)
                {
                    var b = data.AsSpan(k * 8 * ch + c * 8, 8);
                    EtcBlocks.Eac11(b, signed, v);
                    if (signed) EtcBlocks.EacR11Signed(b, s, 1); else EtcBlocks.EacR11(b, u, 1);
                    for (int i = 0; i < 16; i++)
                    {
                        int x = bx * 4 + (i & 3), y = by * 4 + (i >> 2);
                        int e = exp[(y * w + x) * ch + c];
                        int want = signed ? ((v[i] - 1) >> 3) + 128 : v[i] >> 3;   // the oracle's 8 bit mapping
                        if (signed) Assert.Equal(EtcBlocks.Extend11Signed(v[i]), s[i]);
                        else Assert.Equal(EtcBlocks.Extend11(v[i]), u[i]);
                        if (want != e)
                            Assert.Fail($"{name}: block {k} texel {i} channel {c}: 11 bit {v[i]}, oracle {e}");
                    }
                }
    }

    [Fact]
    public void EacSpecificationExamples()
    {
        // Khronos Data Format Specification 22.5 / 22.7: base 103 (unsigned) or 60 (signed), table 13, pixel b with
        // index 3 (modifier -10), multiplier 2 -> 668 / 320, multiplier 0 -> 818 / 470
        static byte[] Block(int baseCw, int mul, int tab, int indexB)
        {
            ulong v = (ulong)(byte)baseCw << 56 | (ulong)mul << 52 | (ulong)tab << 48 | (ulong)indexB << 42;
            var b = new byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(b, v);
            return b;
        }
        Span<int> v = stackalloc int[16];
        EtcBlocks.Eac11(Block(103, 2, 13, 3), false, v);
        Assert.Equal(668, v[4]);   // pixel b is (x 0, y 1)
        EtcBlocks.Eac11(Block(103, 0, 13, 3), false, v);
        Assert.Equal(818, v[4]);
        EtcBlocks.Eac11(Block(60, 2, 13, 3), true, v);
        Assert.Equal(320, v[4]);
        EtcBlocks.Eac11(Block(60, 0, 13, 3), true, v);
        Assert.Equal(470, v[4]);
        Assert.Equal(21386, EtcBlocks.Extend11(668));
        Assert.Equal(15054, EtcBlocks.Extend11Signed(470));
        Assert.Equal(-15054, EtcBlocks.Extend11Signed(-470));
        Assert.Equal(32767, EtcBlocks.Extend11Signed(1023));
        Assert.Equal(65535, EtcBlocks.Extend11(2047));
        // RGBA ETC2 alpha: the same block gives 103 - 20 = 83, and with multiplier 0 the base codeword
        Span<byte> a = stackalloc byte[16];
        EtcBlocks.EacAlpha8(Block(103, 2, 13, 3), a, 1);
        Assert.Equal(83, a[4]);
        EtcBlocks.EacAlpha8(Block(103, 0, 13, 3), a, 1);
        Assert.Equal(103, a[4]);
    }

    [Fact]
    public void EtcSpecificationExamples()
    {
        // 22.1 planar example: origin (12, 64, 62), horizontal (50, 5, 37), vertical (40, 112, 45)
        ulong v = 0;
        void Put(int value, int at, int bits) => v |= (ulong)(value & ((1 << bits) - 1)) << at;
        int ro = 12, go = 64, bo = 62, rh = 50, gh = 5, bh = 37, rv = 40, gv = 112, bv = 45;
        Put(ro, 57, 6); Put(go >> 6, 56, 1); Put(go, 49, 6); Put(bo >> 5, 48, 1); Put(bo >> 3, 43, 2); Put(bo, 39, 3);
        Put(rh >> 1, 34, 5); Put(rh, 32, 1); Put(gh, 25, 7); Put(bh, 19, 6); Put(rv, 13, 6); Put(gv, 6, 7); Put(bv, 0, 6);
        Put(1, 33, 1);
        // the unused bits 47..45 select the mode: B (bits 47..43) = 31 plus dB (bits 42..40) = 3 overflows -> planar,
        // while R + dR = 3 + 1 and G + dG = 0 + 1 stay in range
        Put(7, 45, 3);
        var b = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(b, v);
        var o = new byte[64];
        EtcBlocks.Etc2Rgb(b, o);
        int[,] expect = { { 48, 129, 251 }, { 87, 99, 226 }, { 126, 70, 201 }, { 164, 40, 175 }, { 77, 153, 234 }, { 115, 123, 209 },
                          { 154, 94, 183 }, { 193, 64, 158 }, { 105, 177, 217 }, { 144, 147, 191 }, { 183, 118, 166 }, { 221, 88, 141 },
                          { 134, 201, 199 }, { 172, 171, 174 }, { 211, 142, 149 }, { 250, 112, 124 } };
        for (int i = 0; i < 16; i++)
            Assert.Equal((expect[i, 0], expect[i, 1], expect[i, 2], 255), (o[i * 4], o[i * 4 + 1], o[i * 4 + 2], o[i * 4 + 3]));
    }

    // ------------------------------------------------------------------ ASTC

    static void Astc(string name, byte[] data, RawImage expected)
    {
        var m = Regex.Match(name, @"^astc_(ldr|srgb|hdr)_(\d+)x(\d+)(?:x(\d+))?_");
        Assert.True(m.Success, name);
        string mode = m.Groups[1].Value;
        int bw = int.Parse(m.Groups[2].Value), bh = int.Parse(m.Groups[3].Value), bd = m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 1;
        int w = expected.Width, h = expected.Height / bd;   // 3D: the z slices are stacked vertically
        int nx = w / bw, ny = h / bh, n = bw * bh * bd;
        Assert.Equal(data.Length, nx * ny * 16);
        bool hdr = mode == "hdr";
        var got8 = new byte[w * h * bd * 4];
        var got16 = new ushort[w * h * bd * 4];
        var b8 = new byte[n * 4];
        var b16 = new ushort[n * 4];
        for (int by = 0, k = 0; by < ny; by++)
            for (int bx = 0; bx < nx; bx++, k++)
            {
                var blk = data.AsSpan(k * 16, 16);
                if (hdr) AstcBlocks.DecodeFloat16(blk, bw, bh, bd, b16);
                else AstcBlocks.DecodeUnorm8(blk, bw, bh, bd, mode == "srgb", b8);
                for (int z = 0; z < bd; z++)
                    for (int y = 0; y < bh; y++)
                    {
                        int src = ((z * bh + y) * bw) * 4, dst = (((z * h) + by * bh + y) * w + bx * bw) * 4;
                        if (hdr) b16.AsSpan(src, bw * 4).CopyTo(got16.AsSpan(dst));
                        else b8.AsSpan(src, bw * 4).CopyTo(got8.AsSpan(dst));
                    }
            }
        if (hdr)
        {
            var exp = MemoryMarshal.Cast<byte, ushort>(expected.GetBytes()).ToArray();
            AssertSame(exp, got16, w, bw, bh, name);
        }
        else AssertSame(expected.GetBytes().ToArray(), got8, w, bw, bh, name);
    }

    [Fact]
    public void AstcFloat32MatchesFloat16()
    {
        var data = File.ReadAllBytes(Fixture("etc_astc/astc_hdr_6x6x6_hdr.bin"));
        var h = new ushort[6 * 6 * 6 * 4];
        var f = new float[h.Length];
        for (int k = 0; k < data.Length; k += 16)
        {
            AstcBlocks.DecodeFloat16(data.AsSpan(k, 16), 6, 6, 6, h);
            AstcBlocks.DecodeFloat32(data.AsSpan(k, 16), 6, 6, 6, f);
            for (int i = 0; i < h.Length; i++) Assert.Equal((float)BitConverter.UInt16BitsToHalf(h[i]), f[i]);
        }
    }

    // ------------------------------------------------------------------ helpers

    static void AssertSame<T>(T[] expected, T[] got, int w, int bw, int bh, string name) where T : IEquatable<T>
    {
        Assert.Equal(expected.Length, got.Length);
        for (int i = 0; i < got.Length; i++)
            if (!expected[i].Equals(got[i]))
            {
                int px = i / 4, x = px % w, y = px / w;
                int count = 0;
                for (int j = i; j < got.Length; j++) if (!expected[j].Equals(got[j])) count++;
                Assert.Fail($"{name}: {count} values differ, first at ({x}, {y}) channel {i % 4} (block {x / bw}, {y / bh}): expected {expected[i]}, got {got[i]}");
            }
    }

    static void AssertSame(byte[] expected, byte[] got, int w, int bw, string name) => AssertSame(expected, got, w, bw, bw, name);
}
