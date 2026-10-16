using Aardvark.ImageCodecs;
using Aardvark.ImageCodecs.JpegXl;
using Xunit;
using Xunit.Abstractions;
using static Aardvark.ImageCodecs.Tests.TestUtil;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>
/// Points of ISO/IEC 18181-1:2024 and 18181-2 the conformance files do not reach, checked on hand-built codestreams
/// (lossless modular float frames with patches, splines or noise written by the test) decoded by us and by libjxl.
/// </summary>
public class JxlSpecTests(ITestOutputHelper output)
{
    // ------------------------------------------------------------------ codestream builder

    sealed class Plane
    {
        public readonly float[] V;
        public Plane(int w, int h, Func<int, int, float> f)
        {
            V = new float[w * h];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) V[y * w + x] = f(x, y);
        }
    }

    /// <summary>A float32 RGB image (sRGB, not XYB) with the given extra channels, coded as lossless modular frames.</summary>
    sealed class Stream
    {
        public readonly JxlImageHeader Header;
        readonly JxlBitWriter w = new();

        public Stream(int width, int height, params JxlExtraChannelType[] extra)
        {
            var bd = new JxlBitDepth { Float = true, Bits = 32, ExpBits = 8 };
            Header = new JxlImageHeader
            {
                Width = width, Height = height, BitDepth = bd, XybEncoded = false, Modular16BitBuffers = false,
                ExtraChannels = extra.Select(t => new JxlExtraChannel { Type = t, BitDepth = bd }).ToArray(),
            };
            JxlHeaderWriter.WriteImageHeader(w, Header);
            w.ZeroPadToByte();
        }

        public JxlFrameHeader NewFrame(int width, int height)
        {
            int ne = Header.ExtraChannels.Length;
            var fh = new JxlFrameHeader
            {
                Modular = true, Width = width, Height = height, EcUpsampling = Enumerable.Repeat(1, ne).ToArray(),
                EcBlending = new JxlBlendingInfo[ne], Filter = new JxlRestorationFilter { Gab = false, EpfIters = 0 },
            };
            fh.HaveCrop = width != Header.Width || height != Header.Height;
            return fh;
        }

        /// <summary>Appends a frame; the planes are colour (3) then extra channels, at frame size before upsampling.</summary>
        public void Frame(JxlFrameHeader fh, Plane[] planes, Action<JxlBitWriter>? features = null)
        {
            int fw = (fh.Width + fh.Upsampling - 1) / fh.Upsampling, fhh = (fh.Height + fh.Upsampling - 1) / fh.Upsampling;
            var chans = planes.Select(p =>
            {
                var c = new JxlChannel(fw, fhh, 0, 0);
                for (int i = 0; i < c.Data.Length; i++) c.Data[i] = BitConverter.SingleToInt32Bits(p.V[i]);
                return c;
            }).ToList();
            JxlLosslessEncoder.EncodeFrame(w, chans, Header, fh, JxlLosslessParams.For(2), false, features);
        }

        public byte[] ToArray() => w.ToArray();
    }

    /// <summary>Hybrid uint tokens of one entropy coded stream with the given number of contexts, then written.</summary>
    sealed class Tokens(int contexts)
    {
        readonly JxlTokens t = new();
        public void Add(int ctx, long v) => t.Add(ctx, (uint)v);
        public void AddSigned(int ctx, long v) => t.Add(ctx, JxlBitWriter.PackSigned((int)v));
        public void WriteTo(JxlBitWriter w)
        {
            var enc = new JxlEntropyEncoder(contexts) { MaxClusters = contexts };
            enc.Build(new[] { t }, false);
            enc.WriteCode(w);
            enc.WriteTokens(w, t);
        }
    }

    static float Rand(int seed, int x, int y) => (float)(((uint)(x * 73856093 ^ y * 19349663 ^ seed * 83492791) % 10007) / 10007.0);

    static Plane[] Planes(int w, int h, int n, int seed) => Enumerable.Range(0, n).Select(c => new Plane(w, h, (x, y) => Rand(seed + c, x, y))).ToArray();

    /// <summary>Our float decode and libjxl's (null without the oracle), both as interleaved floats.</summary>
    (float[] ours, float[]? lib, int channels) DecodeBoth(byte[] data, string name)
    {
        var ours = Jxl.Decode(data, new JxlDecodeOptions { ApplyOrientation = false });
        Assert.Equal(SampleType.Float32, ours.SampleType);
        var lib = JxlEncodeTests.Libjxl(data);
        if (lib == null) output.WriteLine($"{name}: libjxl not available, decoded by us only");
        else Assert.Equal((ours.Width, ours.Height, ours.Channels), (lib.Width, lib.Height, lib.Channels));
        return ((float[])ours.Data, (float[]?)lib?.Data, ours.Channels);
    }

    void AssertClose(byte[] data, string name, double tol)
    {
        if (Environment.GetEnvironmentVariable("PIXCODEC_JXLSPEC_DUMP") is string dump) File.WriteAllBytes(Path.Combine(dump, name.Replace(" ", "_").Replace(",", "") + ".jxl"), data);
        var (ours, lib, nc) = DecodeBoth(data, name);
        if (lib == null) return;
        double m = 0;
        for (int i = 0; i < ours.Length; i++) m = Math.Max(m, Math.Abs(ours[i] - lib[i]));
        output.WriteLine($"{name}: largest difference to libjxl {m:g3} ({nc} channels)");
        Assert.True(m <= tol, $"{name}: {m} > {tol}");
    }

    // ------------------------------------------------------------------ K.3 patches

    /// <summary>
    /// A reference frame, then a frame drawing patches from it with every blend mode (K.3.1, Table K.1), with and
    /// without clamping, on colour and on the extra channels.
    /// </summary>
    static byte[] PatchStream(JxlExtraChannelType[] extra, int alphaIndex, Func<int, bool> modeOk)
    {
        const int W = 48, H = 40;
        int ne = extra.Length;
        var s = new Stream(W, H, extra);
        var r = s.NewFrame(W, H);
        r.Type = JxlFrameType.ReferenceOnly; r.IsLast = false; r.SaveAsReference = 1; r.SaveBeforeColourTransform = true;   // libjxl wants that for patches
        // reference: values beyond [0, 1] too, so clamping matters
        s.Frame(r, Enumerable.Range(0, 3 + ne).Select(c => new Plane(W, H, (x, y) => Rand(7 + c, x, y) * 1.6f - 0.3f)).ToArray());
        var f = s.NewFrame(W, H);
        f.Flags = JxlFrameHeader.FlagPatches;
        s.Frame(f, Planes(W, H, 3 + ne, 20), bw =>
        {
            var t = new Tokens(10);
            var modes = Enumerable.Range(0, 8).Where(modeOk).ToArray();
            t.Add(0, modes.Length * 2);   // patch types
            for (int k = 0; k < modes.Length * 2; k++)
            {
                int mode = modes[k / 2];
                bool clamp = (k & 1) != 0;
                t.Add(1, 1);                       // reference 1
                t.Add(3, 3 + k); t.Add(3, 2 + k);  // x0, y0
                t.Add(2, 5 - 1); t.Add(2, 4 - 1);  // 5 x 4
                t.Add(7, 2 - 1);                   // two positions
                for (int j = 0; j < 2; j++)
                {
                    if (j == 0) { t.Add(4, k * 2 % 20); t.Add(4, k % 5 * 7); }
                    else { t.AddSigned(6, 20); t.AddSigned(6, 3 - k % 3); }
                    for (int c = 0; c <= ne; c++)
                    {
                        // colour gets the mode, extra channels cycle through the modes too
                        int m = c == 0 ? mode : modes[(k + c) % modes.Length];
                        t.Add(5, m);
                        if (m > 3 && ne > 1) t.Add(8, alphaIndex);
                        if (m > 2) t.Add(9, clamp ? 1 : 0);
                    }
                }
            }
            t.WriteTo(bw);
        });
        return s.ToArray();
    }

    [Fact]
    public void PatchesBlendLikeLibjxl()
    {
        var a = PatchStream(new[] { JxlExtraChannelType.Alpha }, 0, _ => true);
        AssertClose(a, "patches, alpha", 1e-4);
        var b = PatchStream(new[] { JxlExtraChannelType.Alpha, JxlExtraChannelType.Alpha }, 1, _ => true);
        AssertClose(b, "patches, two alpha channels", 1e-4);
    }

    /// <summary>
    /// K.3.1 reads a patch's alpha_channel when its mode uses alpha and "there is more than 1 alpha channel"; libjxl (and
    /// we) read it whenever there is more than one extra channel. This stream has one alpha channel and a depth channel
    /// and carries the index; a decoder following the letter of the text would lose sync.
    /// </summary>
    [Fact]
    public void PatchAlphaChannelIsReadWithSeveralExtraChannels()
    {
        var d = PatchStream(new[] { JxlExtraChannelType.Depth, JxlExtraChannelType.Alpha }, 1, _ => true);
        AssertClose(d, "patches, depth + alpha", 1e-4);
    }

    // ------------------------------------------------------------------ fixtures of gen_jxl_spec.py

    static byte[] SpecFixture(string name) => File.ReadAllBytes(Path.Combine(FixtureDir, "jxl-spec", name));

    /// <summary>E.4.4: after an rXYZ / gXYZ / bXYZ run the next tag start is predicted from the rXYZ tag.</summary>
    [Fact]
    public void IccTagStartAfterXyzRun()
    {
        var img = Jxl.Decode(SpecFixture("icc_xyz_shared.jxl"));
        Assert.Equal(SpecFixture("icc_xyz_shared.icc"), img.Metadata?.IccProfile);
    }

    /// <summary>G.2.2: with an LF frame, the LF thresholds of the block context see no quantized LF (index 0).</summary>
    [Theory]
    [InlineData("lf_frame_e7.jxl")]
    [InlineData("lf_frame_e9.jxl")]
    public void LfFrameBlockContexts(string name)
    {
        var img = Jxl.Decode(SpecFixture(name), new JxlDecodeOptions { ForceFloat = true });
        var reference = LoadRef(Path.Combine(FixtureDir, "jxl-spec", name + ".ref"));
        Assert.Equal((reference.Width, reference.Height, reference.Channels), (img.Width, img.Height, img.Channels));
        var a = (float[])img.Data; var e = (float[])reference.Data;
        double m = 0;
        for (int i = 0; i < a.Length; i++) m = Math.Max(m, Math.Abs(Math.Clamp(a[i], 0, 1) - Math.Clamp(e[i], 0, 1)));
        output.WriteLine($"{name}: largest difference to libjxl {m:g3}");
        Assert.True(m < 5e-4, $"{m}");
    }
}
