using System;

namespace Aardvark.ImageCodecs.JpegXl
{
    internal sealed partial class JxlFrameDecoder
    {
        object? patches, splines, noise;
        void DecodePatches(JxlBitReader br) => throw new ImageNotSupportedException("patches");
        void DecodeSplines(JxlBitReader br) => throw new ImageNotSupportedException("splines");
        void DecodeNoise(JxlBitReader br) => throw new ImageNotSupportedException("noise");
        void ApplyRestorationFilters(float[][] c) { if (fh.Filter.Gab || fh.Filter.EpfIters > 0) throw new ImageNotSupportedException("filters"); }
        void ApplyFeatures(JxlFrameImage f) { }
        float[] Upsample(float[] s, int w, int h, int up, int ow, int oh) => throw new ImageNotSupportedException("upsampling");
    }
    internal static class JxlIcc { public static byte[] Read(JxlBitReader br) => throw new ImageNotSupportedException("icc"); }
}
namespace Aardvark.ImageCodecs.Compression
{
    internal static class Brotli { public static byte[] Decompress(ReadOnlySpan<byte> s, int max) => throw new ImageNotSupportedException("brotli"); }
}
