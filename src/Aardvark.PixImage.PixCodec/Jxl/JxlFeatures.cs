using System;

namespace Aardvark.ImageCodecs.JpegXl
{
    /// <summary>Image features of a frame: patches, splines and noise.</summary>
    internal sealed partial class JxlFrameDecoder
    {
        JxlPatches? patches;
        JxlSplines? splines;
        JxlNoise? noise;

        void DecodePatches(JxlBitReader br) => patches = JxlPatches.Read(br, img.ExtraChannels.Length, (long)dims.Width * dims.Height);
        void DecodeSplines(JxlBitReader br) => splines = JxlSplines.Read(br, (long)dims.Width * dims.Height);
        void DecodeNoise(JxlBitReader br) => noise = JxlNoise.Read(br);

        /// <summary>
        /// Patches, then splines, then noise, on the full resolution frame before its colour transform. The noise groups
        /// are groups of the upsampled frame (group_dim, not group_dim times the upsampling; libjxl agrees).
        /// </summary>
        void ApplyFeatures(JxlFrameImage f)
        {
            patches?.Apply(f, state, img);
            if (splines != null && f.Planes.Length >= 3) splines.Draw(f.Planes, f.Width, f.Height, baseCorrX, baseCorrB);
            if (noise != null && f.Planes.Length >= 3)
                noise.Apply(f.Planes[0], f.Planes[1], f.Planes[2], f.Width, f.Height, dims.GroupDim,
                    ((ulong)(uint)state.VisibleFrames << 32) + (uint)state.NonVisibleFrames, baseCorrX, baseCorrB, parallel);
        }
    }
}
