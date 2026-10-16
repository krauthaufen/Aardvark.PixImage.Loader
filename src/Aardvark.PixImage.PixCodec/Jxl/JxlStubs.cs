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
