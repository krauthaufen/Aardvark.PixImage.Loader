using System;

namespace Aardvark.ImageCodecs.JpegXl
{
    internal sealed partial class JxlFrameDecoder
    {
        object? patches, splines, noise;
        void DecodePatches(JxlBitReader br) => throw new ImageNotSupportedException("patches");
        void DecodeSplines(JxlBitReader br) => throw new ImageNotSupportedException("splines");
        void DecodeNoise(JxlBitReader br) => throw new ImageNotSupportedException("noise");
        void DecodeVarDctGlobal(JxlBitReader br) => throw new ImageNotSupportedException("vardct");
        void DecodeLfCoefficients(JxlBitReader br, int i) => throw new ImageNotSupportedException("vardct");
        void DecodeHfMetadata(JxlBitReader br, int i, long m) => throw new ImageNotSupportedException("vardct");
        void DecodeHfGlobal(JxlBitReader br) => throw new ImageNotSupportedException("vardct");
        void DecodeHfCoefficients(JxlBitReader br, int p, int g) => throw new ImageNotSupportedException("vardct");
        void FinishVarDctGroup(int g) => throw new ImageNotSupportedException("vardct");
        float[][] VarDctColour() => throw new ImageNotSupportedException("vardct");
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
