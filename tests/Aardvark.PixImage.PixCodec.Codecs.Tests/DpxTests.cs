using Xunit;
using static Aardvark.ImageCodecs.Tests.TestUtil;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>DPX and Cineon: fixtures against their references (tests/oracle/gen_dpx.py), headers, encoders against the oracles, robustness.</summary>
public class DpxTests
{
    public static IEnumerable<object[]> DpxFixtures => Files("dpx", ".dpx");
    public static IEnumerable<object[]> CineonFixtures => Files("cineon", ".cin");

    static bool IsDpx(string file) => Path.GetExtension(file) == ".dpx";
    static RawImage DecodeAny(string file, ReadOnlySpan<byte> d) => IsDpx(file) ? Dpx.Decode(d) : Cineon.Decode(d);
    static ImageInfo InfoAny(string file, ReadOnlySpan<byte> d) => IsDpx(file) ? Dpx.GetInfo(d) : Cineon.GetInfo(d);

    // ------------------------------------------------------------------ decoding

    [Theory, MemberData(nameof(DpxFixtures)), MemberData(nameof(CineonFixtures))]
    public void DecodeMatchesReference(string file)
    {
        var data = File.ReadAllBytes(Fixture(file));
        var expected = LoadRef(Fixture(file) + ".ref");
        var img = DecodeAny(file, data);
        AssertEqual(expected, img);

        // the header information describes what Decode returns, also through the format independent entry points
        var info = InfoAny(file, data);
        Assert.Equal((img.Width, img.Height, img.Channels, img.SampleType, img.Color), (info.Width, info.Height, info.Channels, info.SampleType, info.Color));
        Assert.Equal(IsDpx(file) ? ImageFormat.Dpx : ImageFormat.Cineon, info.Format);
        Assert.Equal(info.Format, ImageCodec.DetectFormat(data));
        Assert.Equal(info, ImageCodec.GetInfo(data));
        Assert.Equal(info, ImageCodec.GetInfo(new MemoryStream(data)));
        Assert.Equal(info, ImageCodec.GetInfo(new NonSeekable(data)));
        Assert.Equal(img.Metadata!.Transfer, info.Transfer);
        AssertEqual(img, ImageCodec.Decode(data));
        if (info.OrientationApplied) Assert.Equal(1, img.Metadata.Orientation);
    }

    [Theory, MemberData(nameof(DpxFixtures))]
    public void DecodeAllGivesEveryElement(string file)
    {
        var data = File.ReadAllBytes(Fixture(file));
        var all = Dpx.DecodeAll(data);
        Assert.Equal(Dpx.GetInfo(data).FrameCount, all.Length);
        Assert.Equal(all.Length, ImageCodec.DecodeAll(data).Length);
        if (!File.Exists(Fixture(file) + ".0.ref"))
        {
            Assert.Single(all);
            return;
        }
        for (int i = 0; i < all.Length; i++)
        {
            AssertEqual(LoadRef(Fixture(file) + $".{i}.ref"), all[i]);
            AssertEqual(all[i], Dpx.Decode(data, new DpxDecodeOptions { ElementIndex = i }));
            Assert.Equal(1, Dpx.GetInfo(data, new DpxDecodeOptions { ElementIndex = i }).FrameCount);
        }
        Assert.Throws<ArgumentException>(() => Dpx.Decode(data, new DpxDecodeOptions { ElementIndex = 8 }));
    }

    [Fact]
    public void SeparateElementsAndTheirTypes()
    {
        var planes = File.ReadAllBytes(Fixture("dpx/multi_rgb_planes.dpx"));
        Assert.Equal((ColorModel.Rgb, 3, 3), (Dpx.Decode(planes).Color, Dpx.GetInfo(planes).FrameCount, Dpx.DecodeAll(planes).Length));
        var ld = Dpx.DecodeAll(File.ReadAllBytes(Fixture("dpx/multi_luma_depth.dpx")));
        Assert.Equal((SampleType.UInt16, SampleType.Float32), (ld[0].SampleType, ld[1].SampleType));
        Assert.Equal((TransferFunction.Linear, TransferFunction.Linear), (ld[0].Metadata!.Transfer, ld[1].Metadata!.Transfer));
        var h = Dpx.GetHeader(File.ReadAllBytes(Fixture("dpx/multi_luma_depth.dpx")));
        Assert.Equal((DpxDescriptor.Luma, DpxDescriptor.Depth), (h.Elements[0].Descriptor, h.Elements[1].Descriptor));
        Assert.Equal(DpxTransfer.DepthLinear, h.Elements[1].Transfer);
    }

    [Fact]
    public void OrientationIsAppliedOrLeft()
    {
        var stored = LoadRef(Fixture("dpx/orient0.dpx.ref"));
        int[] exif = { 1, 2, 4, 3, 5, 6, 8, 7 };
        for (int code = 0; code < 8; code++)
        {
            var data = File.ReadAllBytes(Fixture($"dpx/orient{code}.dpx"));
            var info = Dpx.GetInfo(data);
            Assert.Equal((exif[code], code > 0), (info.Orientation, info.OrientationApplied));
            Assert.Equal(code >= 4 ? (3, 5) : (5, 3), (info.Width, info.Height));
            Assert.Equal(code, Dpx.GetHeader(data).Orientation);
            Assert.Equal(1, Dpx.Decode(data).Metadata!.Orientation);
            var o = new DpxDecodeOptions { ApplyOrientation = false };
            var raw = Dpx.Decode(data, o);
            AssertEqual(stored, raw);
            Assert.Equal(exif[code], raw.Metadata!.Orientation);
            Assert.Equal((5, 3, false), (Dpx.GetInfo(data, o).Width, Dpx.GetInfo(data, o).Height, Dpx.GetInfo(data, o).OrientationApplied));
        }
        var cs = LoadRef(Fixture("cineon/orient0.cin.ref"));
        int[] cexif = { 1, 4, 2, 3, 5, 6, 8, 7 };
        for (int code = 0; code < 8; code++)
        {
            var data = File.ReadAllBytes(Fixture($"cineon/orient{code}.cin"));
            Assert.Equal(cexif[code], Cineon.GetInfo(data).Orientation);
            AssertEqual(cs, Cineon.Decode(data, new CineonDecodeOptions { ApplyOrientation = false }));
        }
    }

    [Fact]
    public void TransferCharacteristics()
    {
        TransferFunction T(string f) => ImageCodec.GetInfo(File.ReadAllBytes(Fixture(f))).Transfer;
        Assert.Equal(TransferFunction.Linear, T("dpx/rgb10a_be.dpx"));     // linear
        Assert.Equal(TransferFunction.Unknown, T("dpx/user1_16.dpx"));     // user defined
        Assert.Equal(TransferFunction.Unknown, T("dpx/im_rgb10.dpx"));     // ImageMagick: user defined
        Assert.Equal(TransferFunction.Linear, T("dpx/oiio_rgbf32.dpx"));   // float, undefined
        Assert.Equal(TransferFunction.Linear, T("dpx/depth_f32.dpx"));     // linear depth
        Assert.Equal(TransferFunction.Gamma, T("dpx/cbycry8_709.dpx"));    // ITU-R 709
        var full = File.ReadAllBytes(Fixture("dpx/header_full.dpx"));
        var m = Dpx.Decode(full).Metadata!;
        Assert.Equal(TransferFunction.Gamma, m.Transfer);
        Assert.Equal(1 / 2.2, m.Gamma, 5);                               // the television header's gamma
        Assert.Equal(TransferFunction.Unknown, T("cineon/rgb10_be.cin"));  // printing density
        Assert.Equal(TransferFunction.Gamma, T("cineon/rgb10_video.cin")); // CCIR XA/11
        // printing density: Unknown, with the raw code in the header
        var log = Dpx.Encode(RandomInt(4, 3, ColorModel.Rgb, SampleType.UInt16, 1, 1023), new DpxEncodeOptions { BitDepth = 10, Transfer = DpxTransfer.PrintingDensity });
        Assert.Equal(TransferFunction.Unknown, Dpx.GetInfo(log).Transfer);
        Assert.Equal((DpxTransfer.PrintingDensity, DpxColorimetric.PrintingDensity), (Dpx.GetHeader(log).Elements[0].Transfer, Dpx.GetHeader(log).Elements[0].Colorimetric));
    }

    [Fact]
    public void HeaderFields()
    {
        var data = File.ReadAllBytes(Fixture("dpx/header_full.dpx"));
        var h = Dpx.GetHeader(data);
        Assert.True(h.BigEndian);
        Assert.Equal("V2.0", h.Version);
        Assert.Equal("frame.0001.dpx", h.ImageFileName);
        Assert.Equal("2024:03:05:14:30:15+0100", h.CreationDateTime);
        Assert.Equal(new DateTimeOffset(2024, 3, 5, 14, 30, 15, TimeSpan.FromHours(1)), h.CreationTime);
        Assert.Equal(("gen_dpx.py creator", "Test Project", "(c) nobody"), (h.Creator, h.Project, h.Copyright));
        Assert.Equal((4, 2, 1), (h.PixelsPerLine, h.Lines, h.Elements.Count));
        var e = h.Elements[0];
        Assert.Equal((DpxDescriptor.Rgb, 10, DpxPacking.FilledA, DpxTransfer.ItuR709, DpxColorimetric.ItuR709, "main plate"),
            (e.Descriptor, e.BitDepth, e.Packing, e.Transfer, e.Colorimetric, e.Description));
        Assert.Equal(((uint?)10, (uint?)20, (float?)2.5f, (float?)1.5f, (uint?)1920, (uint?)1080), (h.XOffset, h.YOffset, h.XCenter, h.YCenter, h.XOriginalSize, h.YOriginalSize));
        Assert.Equal(("source.dpx", "2024:03:04:10:00:00Z", "Scanner X", "SN123"), (h.SourceFileName, h.SourceDateTime, h.InputDevice, h.InputDeviceSerial));
        Assert.Equal(((ushort)1, (ushort)2, (ushort)3, (ushort)4), h.Border);
        Assert.Equal((1u, 1u), h.PixelAspectRatio);
        Assert.Equal(24.89f, h.XScannedSize);
        Assert.Equal(("12", "34", "01", "123456", "7890", "Academy"), (h.FilmManufacturerId, h.FilmType, h.PerfsOffset, h.FilmPrefix, h.FilmCount, h.FilmFormat));
        Assert.Equal(((uint?)86400, (uint?)1000, (uint?)1, (float?)24f, (float?)180f), (h.FramePosition, h.SequenceLength, h.HeldCount, h.FrameRate, h.ShutterAngle));
        Assert.Equal(("keyframe", "Scene 1 Take 2"), (h.FrameId, h.SlateInfo));
        Assert.Equal("01:02:03:04", h.TimeCodeText);
        Assert.Equal(DpxHeader.MakeTimeCode(1, 2, 3, 4), h.TimeCode);
        Assert.Equal((uint?)0x11223344, h.UserBits);
        Assert.Equal(((byte?)0, (byte?)1, (byte?)203), (h.Interlace, h.FieldNumber, h.VideoSignalStandard));
        Assert.Equal((23.976f, 2.2f), (h.TemporalSamplingRate, h.Gamma));
        Assert.Null(h.BlackGain);
        Assert.Equal("USERID", h.UserId);
        Assert.Equal("user data!"u8.ToArray(), h.UserData);

        // dates and the creator also reach the format independent metadata
        var md = ImageCodec.GetInfo(data).Metadata!;
        Assert.Equal("gen_dpx.py creator", md.Software);
        Assert.Equal(new DateTime(2024, 3, 5, 14, 30, 15), md.DateTaken);
        Assert.Equal(TimeSpan.FromHours(1), md.DateTakenOffset);
        Assert.Equal(("(c) nobody", "Test Project"), (md.Copyright, md.Title));
        Assert.Equal(ImageFormat.Dpx, md.Format);

        // the descriptive fields go into a new file
        var img = Dpx.Decode(data);
        var re = Dpx.GetHeader(Dpx.Encode(img, new DpxEncodeOptions { Header = h, BitDepth = 10, BigEndian = false }));
        Assert.Equal(h with
        {
            BigEndian = false, ImageDataOffset = re.ImageDataOffset, FileSize = re.FileSize, DittoKey = re.DittoKey, UserDataSize = re.UserDataSize,
            Elements = re.Elements, Gamma = h.Gamma,
        }, re with { UserData = h.UserData });
        Assert.Equal(h.UserData, re.UserData);
        // the decoded image says Gamma 1 / 2.2: recorded as unspecified video with the television gamma
        Assert.Equal(DpxTransfer.UnspecifiedVideo, re.Elements[0].Transfer);
    }

    [Fact]
    public void CineonHeaderFields()
    {
        var data = File.ReadAllBytes(Fixture("cineon/rgb10_be.cin"));
        var h = Cineon.GetHeader(data);
        Assert.Equal(("V4.5", "gen.cin", "1999:12:31", "23:59:58UTC"), (h.Version, h.ImageFileName, h.CreationDate, h.CreationTimeOfDay));
        Assert.Equal(new DateTimeOffset(1999, 12, 31, 23, 59, 58, TimeSpan.Zero), h.CreationTime);
        Assert.Equal(3, h.Channels.Count);
        Assert.Equal((0, 1, 10, 7, 5), (h.Channels[0].Metric, h.Channels[0].Designator, h.Channels[0].BitDepth, h.Channels[0].PixelsPerLine, h.Channels[0].Lines));
        Assert.Equal((1023f, 2.048f), (h.Channels[0].MaximumData, h.Channels[0].MaximumQuantity));
        Assert.Equal((0, 5, false, false), (h.Interleave, h.Packing, h.Signed, h.Negative));
        Assert.Equal(((uint?)7, (float?)24f, (float?)1f), (h.FramePosition, h.FrameRate, h.Gamma));
        Assert.Equal(new DateTime(1999, 12, 31, 23, 59, 58), ImageCodec.GetInfo(data).Metadata!.DateTaken);
        var im = Cineon.GetHeader(File.ReadAllBytes(Fixture("cineon/im_rgb10.cin")));
        Assert.Equal((0.64f, 0.33f), im.RedPrimary);
    }

    // ------------------------------------------------------------------ encoding

    internal static RawImage RandomInt(int w, int h, ColorModel c, SampleType t, int seed, int max)
    {
        var r = new Random(seed);
        var img = RawImage.Create(w, h, c, t, c == ColorModel.Other ? 2 : 0);
        switch (t)
        {
            case SampleType.UInt8: { var d = img.GetData<byte>(); for (int i = 0; i < d.Length; i++) d[i] = (byte)r.Next(max + 1); break; }
            case SampleType.UInt16: { var d = img.GetData<ushort>(); for (int i = 0; i < d.Length; i++) d[i] = (ushort)r.Next(max + 1); break; }
            case SampleType.Float32: { var d = img.GetData<float>(); for (int i = 0; i < d.Length; i++) d[i] = (float)(r.NextDouble() * 3 - 1); break; }
            case SampleType.Float64: { var d = img.GetData<double>(); for (int i = 0; i < d.Length; i++) d[i] = r.NextDouble() * 3 - 1; break; }
        }
        return img;
    }

    public static IEnumerable<object[]> EncodeCases()
    {
        foreach (var c in new[] { ColorModel.Gray, ColorModel.Rgb, ColorModel.Rgba })
            foreach (bool be in new[] { true, false })
            {
                foreach (int bits in new[] { 8, 16, 32, 64 }) yield return new object[] { c, bits, DpxPacking.FilledA, be };
                foreach (int bits in new[] { 10, 12 })
                    foreach (var p in new[] { DpxPacking.FilledA, DpxPacking.FilledB, DpxPacking.Packed })
                        yield return new object[] { c, bits, p, be };
            }
    }

    static SampleType TypeFor(int bits) => bits switch { 8 => SampleType.UInt8, 32 => SampleType.Float32, 64 => SampleType.Float64, _ => SampleType.UInt16 };

    [Theory, MemberData(nameof(EncodeCases))]
    public void EncodeRoundTrip(ColorModel c, int bits, DpxPacking packing, bool be)
    {
        foreach (int w in new[] { 1, 5, 6, 7, 31 })
        {
            var src = RandomInt(w, 4, c, TypeFor(bits), w * 7 + bits, bits >= 16 ? 65535 : (1 << bits) - 1);
            var enc = Dpx.Encode(src, new DpxEncodeOptions { BitDepth = bits, Packing = packing, BigEndian = be });
            var back = Dpx.Decode(enc);
            AssertEqual(src, back);
            var h = Dpx.GetHeader(enc);
            Assert.Equal((be, bits, bits is 10 or 12 ? packing : DpxPacking.Packed), (h.BigEndian, h.Elements[0].BitDepth, h.Elements[0].Packing));
            Assert.Equal((uint)enc.Length, h.FileSize);
            Assert.Equal(bits >= 32 ? TransferFunction.Linear : TransferFunction.Unknown, Dpx.GetInfo(enc).Transfer);
        }
    }

    [Fact]
    public void EncodeConversionsAndErrors()
    {
        // UInt8 is scaled to deeper integer depths
        var b = RandomInt(9, 3, ColorModel.Rgb, SampleType.UInt8, 3, 255);
        foreach (int bits in new[] { 10, 12, 16 })
        {
            var back = Dpx.Decode(Dpx.Encode(b, new DpxEncodeOptions { BitDepth = bits }));
            int max = (1 << bits) - 1;
            var s = b.GetData<byte>();
            var d = back.GetData<ushort>();
            for (int i = 0; i < s.Length; i++) Assert.Equal((s[i] * max + 127) / 255, d[i]);
        }
        // GrayAlpha becomes RGBA, Half becomes Float32, Other is user defined
        var ga = RandomInt(5, 3, ColorModel.GrayAlpha, SampleType.UInt8, 4, 255);
        var rgba = Dpx.Decode(Dpx.Encode(ga));
        Assert.Equal(ColorModel.Rgba, rgba.Color);
        Assert.Equal(ga.GetData<byte>()[1], rgba.GetData<byte>()[3]);
        var half = new RawImage(2, 2, ColorModel.Gray, new[] { (Half)0.5f, (Half)1, (Half)(-2), (Half)3 });
        Assert.Equal(new[] { 0.5f, 1, -2, 3 }, Dpx.Decode(Dpx.Encode(half)).GetData<float>());
        var other = RandomInt(4, 3, ColorModel.Other, SampleType.UInt16, 5, 1023);
        var o = Dpx.Encode(other, new DpxEncodeOptions { BitDepth = 10 });
        Assert.Equal(DpxDescriptor.UserDefined2, Dpx.GetHeader(o).Elements[0].Descriptor);
        AssertEqual(other, Dpx.Decode(o));
        var z = RandomInt(4, 3, ColorModel.Gray, SampleType.Float32, 6, 0);
        Assert.Equal(DpxDescriptor.Depth, Dpx.GetHeader(Dpx.Encode(z, new DpxEncodeOptions { Descriptor = DpxDescriptor.Depth })).Elements[0].Descriptor);
        // transfer from the metadata
        var g = RandomInt(4, 3, ColorModel.Rgb, SampleType.UInt16, 7, 65535);
        g.Metadata = new ImageMetadata { Transfer = TransferFunction.Gamma, Gamma = 1 / 2.4 };
        var gm = Dpx.Decode(Dpx.Encode(g)).Metadata!;
        Assert.Equal(TransferFunction.Gamma, gm.Transfer);
        Assert.Equal(1 / 2.4, gm.Gamma, 5);
        g.Metadata.Transfer = TransferFunction.Linear;
        Assert.Equal(TransferFunction.Linear, Dpx.GetInfo(Dpx.Encode(g)).Transfer);

        Assert.Throws<ArgumentException>(() => Dpx.Encode(RandomInt(4, 3, ColorModel.Rgb, SampleType.UInt16, 1, 65535), new DpxEncodeOptions { BitDepth = 10 }));
        Assert.Throws<ArgumentException>(() => Dpx.Encode(z, new DpxEncodeOptions { BitDepth = 16 }));
        Assert.Throws<ArgumentException>(() => Dpx.Encode(b, new DpxEncodeOptions { BitDepth = 32 }));
        Assert.Throws<ArgumentException>(() => Dpx.Encode(b, new DpxEncodeOptions { BitDepth = 11 }));
        Assert.Throws<ArgumentException>(() => Dpx.Encode(RawImage.Create(2, 2, ColorModel.Gray, SampleType.Int16)));
        Assert.Throws<ArgumentException>(() => Dpx.Encode(RandomInt(2, 2, ColorModel.Gray, SampleType.UInt8, 1, 255), new DpxEncodeOptions { Descriptor = DpxDescriptor.Rgb }));
        // 8 bit lines are padded to 32 bits, with the padding recorded
        var e8 = Dpx.GetHeader(Dpx.Encode(RandomInt(5, 2, ColorModel.Rgb, SampleType.UInt8, 1, 255)));
        Assert.Equal((uint?)1, e8.Elements[0].EndOfLinePadding);
        Assert.Equal((uint)(8192 + 2 * 16), e8.FileSize);
    }

    static string Temp(string ext) => Path.Combine(Path.GetTempPath(), "pixcodec-dpx-" + Guid.NewGuid().ToString("N") + ext);

    /// <summary>
    /// Decodes a file with an oracle: OpenImageIO's python module (tests/oracle/decode_to_ref.py; integers scaled to 16 bit,
    /// alpha as stored - oiiotool would premultiply it on the way), or ImageMagick through a 16 bit TIFF. Null when missing.
    /// </summary>
    static RawImage? ToolDecode(string tool, string file)
    {
        if (tool == "oiio")
        {
            if (!PythonAvailable) return null;
            try
            {
                var (exit, output) = RunPython($"decode_to_ref.py \"{file}\" \"{file}.ref\"");
                if (exit == 77) return null;
                Assert.True(exit == 0, "OpenImageIO decode failed: " + output);
                return LoadRef(file + ".ref");
            }
            finally { File.Delete(file + ".ref"); }
        }
        if (!MiscFormatTests.ToolAvailable("magick")) return null;
        var tmp = Temp(".tif");
        try
        {
            MiscFormatTests.RunTool("magick", $"\"{file}\" -set colorspace sRGB -depth 16 -compress none \"{tmp}\"");
            return Tiff.Decode(File.ReadAllBytes(tmp));
        }
        finally { File.Delete(tmp); }
    }

    /// <summary>Asserts the oracle's 16 bit (or float) decode matches ours, normalized: within one code of our depth.</summary>
    static void AssertOracle(RawImage ours, RawImage theirs, int bits, string what)
    {
        Assert.Equal((ours.Width, ours.Height), (theirs.Width, theirs.Height));
        int c = ours.Channels;
        var a = ToDoubles(ours);
        var t = ToDoubles(theirs);
        int tc = theirs.Channels;
        double scaleA = bits >= 32 ? 1 : 1.0 / ((1 << bits) - 1), scaleT = bits >= 32 ? 1 : 1.0 / 65535;
        double tol = bits >= 32 ? 1e-6 : 1.01 / ((1 << bits) - 1) + 1.0 / 65535;
        for (int p = 0; p < ours.Width * ours.Height; p++)
            for (int k = 0; k < c; k++)
            {
                double d = Math.Abs(a[p * c + k] * scaleA - t[p * tc + Math.Min(k, tc - 1)] * scaleT);
                Assert.True(d <= tol, $"{what}: sample {p}/{k} differs by {d}");
            }
    }

    public static IEnumerable<object[]> OracleCases()
    {
        foreach (var c in new[] { ColorModel.Gray, ColorModel.Rgb, ColorModel.Rgba })
            foreach (bool be in new[] { true, false })
            {
                foreach (int bits in new[] { 8, 16, 32 }) yield return new object[] { c, bits, DpxPacking.FilledA, be };
                foreach (int bits in new[] { 10, 12 })
                    foreach (var p in new[] { DpxPacking.FilledA, DpxPacking.FilledB, DpxPacking.Packed })
                        yield return new object[] { c, bits, p, be };
            }
    }

    /// <summary>
    /// OpenImageIO and ImageMagick read what we write (where they read the layout at all, see gen_dpx.py: ImageMagick has no
    /// method B, no packed one component data and no float).
    /// </summary>
    [Theory, MemberData(nameof(OracleCases))]
    public void OraclesReadOurDpx(ColorModel c, int bits, DpxPacking packing, bool be)
    {
        // width 9: no partly filled 10 bit words for one or three components, but some for RGBA
        var src = RandomInt(9, 5, c, TypeFor(bits), bits * 3 + (int)packing, bits >= 16 ? 65535 : (1 << bits) - 1);
        var f = Temp(".dpx");
        try
        {
            File.WriteAllBytes(f, Dpx.Encode(src, new DpxEncodeOptions { BitDepth = bits, Packing = packing, BigEndian = be }));
            if (ToolDecode("oiio", f) is RawImage o) AssertOracle(src, o, bits, "OpenImageIO");
            // (nor one component 10 bit filled words of little endian files, which it reads high bits first, nor 12 bit and
            // packed RGBA, which it writes and reads in a layout of its own)
            bool imReads = bits < 32 && packing != DpxPacking.FilledB && !(c == ColorModel.Gray && packing == DpxPacking.Packed && bits is 10 or 12)
                && !(c == ColorModel.Gray && bits == 10 && !be) && !(c == ColorModel.Rgba && (bits == 12 || packing == DpxPacking.Packed));
            if (imReads && ToolDecode("magick", f) is RawImage m) AssertOracle(src, m, bits, "ImageMagick");
        }
        finally { File.Delete(f); }
    }

    /// <summary>Our DPX against ImageMagick's DPX of the same pixels (its defaults: 8 / 10 / 16 bit, big endian, filled A).</summary>
    [Theory]
    [InlineData(ColorModel.Rgb, 8), InlineData(ColorModel.Rgb, 10), InlineData(ColorModel.Rgb, 12), InlineData(ColorModel.Rgb, 16),
     InlineData(ColorModel.Rgba, 8), InlineData(ColorModel.Rgba, 10), InlineData(ColorModel.Rgba, 16)]
    public void SameDataAsImageMagick(ColorModel c, int bits)
    {
        if (!MiscFormatTests.ToolAvailable("magick")) return;
        var src = RandomInt(12, 5, c, bits == 8 ? SampleType.UInt8 : SampleType.UInt16, bits, (1 << bits) - 1);
        var ppm = Temp(c == ColorModel.Rgb ? ".ppm" : ".pam");
        var theirs = Temp(".dpx");
        try
        {
            // scaled to 16 bit with bit replication, which every tool maps back to the same code
            var wide = RawImage.Create(src.Width, src.Height, c, SampleType.UInt16);
            var s = ToDoubles(src); var d = wide.GetData<ushort>();
            for (int i = 0; i < s.Length; i++) d[i] = (ushort)Math.Round(s[i] * 65535.0 / ((1 << bits) - 1));
            File.WriteAllBytes(ppm, Pnm.Encode(wide));
            MiscFormatTests.RunTool("magick", $"\"{ppm}\" -depth {bits} \"{theirs}\"");
            var im = File.ReadAllBytes(theirs);
            var ih = Dpx.GetHeader(im);
            var ours = Dpx.Encode(src, new DpxEncodeOptions { BitDepth = bits });
            var oh = Dpx.GetHeader(ours);
            Assert.Equal((ih.Elements[0].Descriptor, ih.Elements[0].BitDepth, ih.Elements[0].Packing), (oh.Elements[0].Descriptor, oh.Elements[0].BitDepth, oh.Elements[0].Packing));
            // the image data: identical bytes (up to ImageMagick's rounding of the 16 bit input)
            var a = Dpx.Decode(im);
            AssertEqual(src, a, 2);
            var dataIm = im.AsSpan((int)ih.Elements[0].DataOffset!.Value);
            var dataOurs = Dpx.Encode(a, new DpxEncodeOptions { BitDepth = bits }).AsSpan((int)oh.Elements[0].DataOffset!.Value);
            Assert.True(dataIm.Slice(0, dataOurs.Length).SequenceEqual(dataOurs), "pixel data differs from ImageMagick's");
        }
        finally { File.Delete(ppm); File.Delete(theirs); }
    }

    [Theory]
    [InlineData(ColorModel.Gray, 8), InlineData(ColorModel.Gray, 10), InlineData(ColorModel.Gray, 16),
     InlineData(ColorModel.Rgb, 8), InlineData(ColorModel.Rgb, 10), InlineData(ColorModel.Rgb, 16)]
    public void CineonRoundTripAndOracles(ColorModel c, int bits)
    {
        foreach (bool be in new[] { true, false })
            foreach (int w in new[] { 1, 6, 7, 9 })
            {
                var src = RandomInt(w, 4, c, bits == 8 ? SampleType.UInt8 : SampleType.UInt16, w + bits, (1 << bits) - 1);
                var enc = Cineon.Encode(src, new CineonEncodeOptions { BitDepth = bits, BigEndian = be });
                AssertEqual(src, Cineon.Decode(enc));
                var h = Cineon.GetHeader(enc);
                Assert.Equal((be, src.Channels, bits, 5), (h.BigEndian, h.Channels.Count, h.Channels[0].BitDepth, h.Packing));
                Assert.Equal(TransferFunction.Unknown, Cineon.GetInfo(enc).Transfer);
                if (w != 9 || !be) continue;
                var f = Temp(".cin");
                try
                {
                    File.WriteAllBytes(f, enc);
                    // OpenImageIO takes one channel cells high bits first like us, ImageMagick does not; both read RGB
                    if (!(bits == 8 && w * src.Channels % 4 != 0) && ToolDecode("oiio", f) is RawImage o)
                        AssertOracle(src, o, bits, "OpenImageIO");
                    if (c == ColorModel.Rgb && ToolDecode("magick", f) is RawImage m)
                        AssertOracle(src, m, bits, "ImageMagick");
                }
                finally { File.Delete(f); }
            }
        // UInt8 to 10 bit is scaled; values that do not fit are refused
        var b = RandomInt(3, 2, ColorModel.Rgb, SampleType.UInt8, 1, 255);
        var d = Cineon.Decode(Cineon.Encode(b)).GetData<ushort>();
        for (int i = 0; i < d.Length; i++) Assert.Equal((b.GetData<byte>()[i] * 1023 + 127) / 255, d[i]);
        Assert.Throws<ArgumentException>(() => Cineon.Encode(RandomInt(3, 2, ColorModel.Rgb, SampleType.UInt16, 1, 65535)));
        Assert.Throws<ArgumentException>(() => Cineon.Encode(RandomInt(3, 2, ColorModel.Rgba, SampleType.UInt8, 1, 255)));
        Assert.Throws<ArgumentException>(() => Cineon.Encode(b, new CineonEncodeOptions { BitDepth = 12 }));
        // header fields are carried
        var hd = Cineon.GetHeader(File.ReadAllBytes(Fixture("cineon/rgb10_be.cin")));
        var re = Cineon.GetHeader(Cineon.Encode(b, new CineonEncodeOptions { Header = hd, RgbDesignator = 4 }));
        Assert.Equal((hd.ImageFileName, hd.CreationDate, hd.CreationTimeOfDay, hd.FrameRate, hd.FramePosition), (re.ImageFileName, re.CreationDate, re.CreationTimeOfDay, re.FrameRate, re.FramePosition));
        Assert.Equal(TransferFunction.Gamma, Cineon.GetInfo(Cineon.Encode(b, new CineonEncodeOptions { RgbDesignator = 4 })).Transfer);
    }

    // ------------------------------------------------------------------ robustness

    static void Robust(Func<byte[], object> decode, byte[] data, int seed)
    {
        void Try(byte[] d)
        {
            try { decode(d); }
            catch (ImageFormatException) { }
            catch (ImageNotSupportedException) { }
        }
        for (int n = 0; n < data.Length; n += n < 2100 ? 37 : 211) Try(data.AsSpan(0, n).ToArray());
        var r = new Random(seed);
        for (int k = 0; k < 300; k++)
        {
            var d = (byte[])data.Clone();
            int flips = 1 + r.Next(4);
            for (int i = 0; i < flips; i++)
            {
                // mostly the header fields that matter
                int at = r.Next(4) == 0 ? r.Next(d.Length) : r.Next(4) switch { 0 => 768 + r.Next(64), 1 => 780 + r.Next(72 * 2), 2 => 4 + r.Next(30), _ => 180 + r.Next(520) };
                if (at < d.Length) d[at] = r.Next(3) == 0 ? (byte)0xFF : (byte)r.Next(256);
            }
            Try(d);
        }
    }

    [Theory]
    [InlineData("dpx/rgb10a_be.dpx"), InlineData("dpx/rle10a_rgb.dpx"), InlineData("dpx/rle8_rgb.dpx"), InlineData("dpx/cbycry10_601.dpx"),
     InlineData("dpx/multi_rgb_alpha.dpx"), InlineData("dpx/multi_no_offsets.dpx"), InlineData("dpx/rgbf32_be.dpx"), InlineData("dpx/luma1_be.dpx"),
     InlineData("cineon/rgb10_be.cin"), InlineData("cineon/rgb10_bitfields.cin"), InlineData("cineon/rgb10_channel.cin"), InlineData("cineon/gray1.cin")]
    public void TruncatedAndCorrupted(string file)
    {
        var data = File.ReadAllBytes(Fixture(file));
        if (IsDpx(file))
        {
            Robust(d => Dpx.Decode(d), data, 1);
            Robust(d => Dpx.DecodeAll(d), data, 2);
            Robust(d => Dpx.GetInfo(d), data, 3);
            Robust(d => ImageCodec.GetInfo(d), data, 4);
        }
        else
        {
            Robust(d => Cineon.Decode(d), data, 1);
            Robust(d => ImageCodec.GetInfo(d), data, 4);
        }
        Assert.Throws<ImageFormatException>(() => DecodeAny(file, data.AsSpan(0, 1000)));
        // cut in the middle of the pixel data (a few bytes less can fit the other line padding layout, see Dpx)
        int cut = 2048 + (data.Length - 2048) / 2;
        if (!file.Contains("multi")) Assert.Throws<ImageFormatException>(() => DecodeAny(file, data.AsSpan(0, cut)));
        else Assert.Throws<ImageFormatException>(() => Dpx.DecodeAll(data.AsSpan(0, data.Length - 12)));   // (the last element)
    }

    [Fact]
    public void HugeDimensionsAreRefused()
    {
        var d = File.ReadAllBytes(Fixture("dpx/rgb10a_be.dpx"));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(772), 100000);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(776), 100000);
        Assert.Throws<ImageNotSupportedException>(() => Dpx.Decode(d));
        Assert.Equal((100000, 100000), (Dpx.GetInfo(d).Width, Dpx.GetInfo(d).Height));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(772), 10000);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(776), 10000);
        Assert.Throws<ImageFormatException>(() => Dpx.Decode(d));   // not backed by data: refused before allocating
        var c = File.ReadAllBytes(Fixture("cineon/rgb10_be.cin"));
        for (int ch = 0; ch < 3; ch++) System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(c.AsSpan(196 + 28 * ch + 4), 30000);
        Assert.Throws<ImageFormatException>(() => Cineon.Decode(c));
    }

    [Fact]
    public void DetectionIsStrict()
    {
        Assert.Equal(ImageFormat.Dpx, ImageCodec.FormatFromExtension("a.DPX"));
        Assert.Equal(ImageFormat.Cineon, ImageCodec.FormatFromExtension("a.cin"));
        foreach (var dir in new[] { "png", "jpeg", "tiff", "tga", "bmp", "exr" })
            foreach (var f in Directory.GetFiles(Path.Combine(FixtureDir, dir)).Where(f => !f.EndsWith(".ref") && !f.EndsWith(".json")).Take(20))
            {
                var fmt = ImageCodec.DetectFormat(File.ReadAllBytes(f));
                Assert.NotEqual(ImageFormat.Dpx, fmt);
                Assert.NotEqual(ImageFormat.Cineon, fmt);
            }
        Assert.Throws<ImageFormatException>(() => Dpx.Decode("SDPX"u8));
        Assert.Throws<ImageFormatException>(() => Cineon.Decode(new byte[] { 0x80, 0x2A, 0x5F, 0xD7 }));
    }

    sealed class NonSeekable(byte[] data) : Stream
    {
        readonly MemoryStream inner = new(data);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, 100));
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
