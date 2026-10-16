using System;

namespace Aardvark.ImageCodecs.JpegXl
{
    /// <summary>Image features of a frame: patches, splines and noise.</summary>
    internal sealed partial class JxlFrameDecoder
    {
        JxlPatches? patches;
        object? splines;
        JxlNoise? noise;

        void DecodePatches(JxlBitReader br) => patches = JxlPatches.Read(br, img.ExtraChannels.Length, (long)dims.Width * dims.Height);
        void DecodeSplines(JxlBitReader br) => throw new ImageNotSupportedException("JPEG XL: splines");
        void DecodeNoise(JxlBitReader br) => noise = JxlNoise.Read(br);

        void ApplyFeatures(JxlFrameImage f)
        {
            patches?.Apply(f, state, img);
            if (noise != null && f.Planes.Length >= 3)
                noise.Apply(f.Planes[0], f.Planes[1], f.Planes[2], f.Width, f.Height, dims.GroupDim * fh.Upsampling,
                    state.VisibleFrames, state.NonVisibleFrames, baseCorrX, baseCorrB);
        }
    }
}
