using System;

namespace Aardvark.ImageCodecs.JpegXl
{
    internal sealed partial class JxlFrameDecoder
    {
        object? patches, splines, noise;
        void DecodePatches(JxlBitReader br) => throw new ImageNotSupportedException("patches");
        void DecodeSplines(JxlBitReader br) => throw new ImageNotSupportedException("splines");
        void DecodeNoise(JxlBitReader br) => throw new ImageNotSupportedException("noise");
        void ApplyFeatures(JxlFrameImage f) { }
    }
}
namespace Aardvark.ImageCodecs.Compression
{
    internal static class Brotli { public static byte[] Decompress(ReadOnlySpan<byte> s, int max) => throw new ImageNotSupportedException("brotli"); }
}
