using Xunit;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>Helpers shared by the ICNS, ANI, ASTC file and PVR tests.</summary>
internal static class IconTextureTestUtil
{
    /// <summary>Asserts that decoding damaged data fails the way the library promises: ImageFormatException or ImageNotSupportedException.</summary>
    public static void AssertRejects(Action decode)
    {
        try { decode(); }
        catch (Exception e) when (e is ImageFormatException or ImageNotSupportedException) { return; }
        Assert.Fail("damaged data was accepted");
    }

    /// <summary>Decoding either succeeds or fails with the documented exceptions (for fuzzed input that may still be valid).</summary>
    public static void DecodesOrRejects(Action decode)
    {
        try { decode(); }
        catch (Exception e) when (e is ImageFormatException or ImageNotSupportedException) { }
    }

    /// <summary>UInt8 Rgb / Gray / GrayAlpha / Rgba as Rgba (opaque where there is no alpha).</summary>
    public static RawImage ToRgba(RawImage s)
    {
        var src = s.GetData<byte>();
        int n = s.Width * s.Height, ch = s.Channels;
        var o = new byte[n * 4];
        for (int i = 0; i < n; i++)
        {
            bool gray = s.Color is ColorModel.Gray or ColorModel.GrayAlpha;
            for (int c = 0; c < 3; c++) o[i * 4 + c] = src[i * ch + (gray ? 0 : c)];
            o[i * 4 + 3] = s.HasAlpha ? src[i * ch + ch - 1] : (byte)255;
        }
        return new RawImage(s.Width, s.Height, ColorModel.Rgba, o, 4);
    }

    /// <summary>Flips the bytes of the data at evenly spread positions after <paramref name="skip"/> bytes and runs the decoder on every variant, and on every truncation step.</summary>
    public static void Fuzz(byte[] data, Action<byte[]> decode, int skip = 0, int variants = 40, int seed = 1)
    {
        var r = new Random(seed);
        for (int v = 0; v < variants; v++)
        {
            var d = (byte[])data.Clone();
            int flips = 1 + r.Next(4);
            for (int k = 0; k < flips; k++) d[skip + r.Next(d.Length - skip)] = (byte)r.Next(256);
            DecodesOrRejects(() => decode(d));
        }
        foreach (int len in new[] { 0, 1, 4, 8, 12, 16, 20, 32, 52, data.Length / 3, data.Length / 2, data.Length - 1 })
            if (len < data.Length) DecodesOrRejects(() => decode(data.AsSpan(0, len).ToArray()));
    }
}
