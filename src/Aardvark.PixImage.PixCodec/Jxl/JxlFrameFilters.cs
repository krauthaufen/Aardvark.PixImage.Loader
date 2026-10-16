using System;

namespace Aardvark.ImageCodecs.JpegXl
{
    /// <summary>Restoration filters and upsampling of a frame.</summary>
    internal sealed partial class JxlFrameDecoder
    {
        void ApplyRestorationFilters(float[][] colour)
        {
            var f = fh.Filter;
            int w = dims.XSize, h = dims.YSize;
            if (!f.Gab && f.EpfIters == 0) return;
            int bx = (w + 7) / 8, by = (h + 7) / 8;
            float[]? inv = null;
            if (f.EpfIters > 0)
            {
                inv = new float[bx * by];
                if (fh.Modular)
                {
                    float v = f.EpfSigmaForModular > 0 ? JxlFilters.InvSigmaNum / f.EpfSigmaForModular : float.NegativeInfinity;
                    Array.Fill(inv, v);
                }
                else
                {
                    float scale = globalScale / 65536.0f;
                    for (int y = 0; y < by; y++)
                        for (int x = 0; x < bx; x++)
                        {
                            int b = y * dcStride + x;
                            float sigma = f.EpfQuantMul / (scale * qfField![b]) * f.EpfSharpLut[sharpness![b]];
                            inv[y * bx + x] = sigma > 0 ? JxlFilters.InvSigmaNum / sigma : float.NegativeInfinity;
                        }
                }
            }
            int[] passes = f.EpfIters switch { 0 => Array.Empty<int>(), 1 => new[] { 1 }, 2 => new[] { 1, 2 }, _ => new[] { 0, 1, 2 } };
            var res = JxlFilters.Restore(colour, w, h, f, passes, inv, bx, parallel);
            for (int c = 0; c < colour.Length; c++) colour[c] = res[c];
        }

        float[] Upsample(float[] src, int w, int h, int up, int ow, int oh)
        {
            float[] weights = up switch
            {
                2 => img.Up2Weights ?? JxlUpsampling.Default2,
                4 => img.Up4Weights ?? JxlUpsampling.Default4,
                _ => img.Up8Weights ?? JxlUpsampling.Default8,
            };
            return JxlUpsampling.Upsample(src, w, h, up, JxlUpsampling.Kernel(up, weights), ow, oh, parallel);
        }
    }
}
