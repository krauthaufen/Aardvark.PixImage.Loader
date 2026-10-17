using System;
using T = Aardvark.ImageCodecs.Av1.Av1Tables;

namespace Aardvark.ImageCodecs.Av1
{
    /// <summary>The horizontal super resolution upscaling process of AV1 spec 7.16.</summary>
    internal static class Av1SuperRes
    {
        public static Av1Frame Upscale(Av1FrameState f, Av1Frame input)
        {
            var h = f.Hdr;
            var s = f.Seq;
            int allocW = Math.Max(input.AllocW[0], (h.UpscaledWidth + 7) & ~7);
            var output = new Av1Frame(s.NumPlanes, allocW, input.AllocH[0], s.SubsamplingX, s.SubsamplingY, s.BitDepth)
            {
                Width = h.UpscaledWidth, Height = h.FrameHeight,
            };
            int maxV = (1 << s.BitDepth) - 1;
            var filt = T.UpscaleFilter;
            for (int plane = 0; plane < s.NumPlanes; plane++)
            {
                int sx = plane > 0 ? s.SubsamplingX : 0, sy = plane > 0 ? s.SubsamplingY : 0;
                int downscaledPlaneW = Av1FrameState.Round2(h.FrameWidth, sx);
                int upscaledPlaneW = Av1FrameState.Round2(h.UpscaledWidth, sx);
                int planeH = Av1FrameState.Round2(h.FrameHeight, sy);
                int stepX = ((downscaledPlaneW << Av1C.SuperresScaleBits) + upscaledPlaneW / 2) / upscaledPlaneW;
                int err = upscaledPlaneW * stepX - (downscaledPlaneW << Av1C.SuperresScaleBits);
                int initialSubpelX = (-((upscaledPlaneW - downscaledPlaneW) << (Av1C.SuperresScaleBits - 1)) + upscaledPlaneW / 2) / upscaledPlaneW
                    + (1 << (Av1C.SuperresExtraBits - 1)) - err / 2;
                initialSubpelX &= Av1C.SuperresScaleMask;
                int miW = f.MiCols >> sx;
                int minX = 0, maxX = miW * Av1C.MiSize - 1;
                var src = input.Planes[plane];
                var dst = output.Planes[plane];
                int ss = input.Stride[plane], ds = output.Stride[plane];
                for (int y = 0; y < planeH; y++)
                    for (int x = 0; x < upscaledPlaneW; x++)
                    {
                        int srcX = -(1 << Av1C.SuperresScaleBits) + initialSubpelX + x * stepX;
                        int srcXPx = srcX >> Av1C.SuperresScaleBits;
                        int srcXSubpel = (srcX & Av1C.SuperresScaleMask) >> Av1C.SuperresExtraBits;
                        int sum = 0;
                        for (int k = 0; k < Av1C.SuperresFilterTaps; k++)
                        {
                            int sampleX = Math.Clamp(srcXPx + (k - Av1C.SuperresFilterOffset), minX, maxX);
                            sum += src[y * ss + sampleX] * filt[srcXSubpel * 8 + k];
                        }
                        dst[y * ds + x] = (ushort)Math.Clamp((sum + 64) >> Av1C.FilterBits, 0, maxV);
                    }
            }
            return output;
        }
    }
}
