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
            if (f.Gab)
                for (int c = 0; c < colour.Length; c++)
                    colour[c] = JxlFilters.Gaborish(colour[c], w, h, f.GabWeights[2 * Math.Min(c, 2)], f.GabWeights[2 * Math.Min(c, 2) + 1], parallel);
            if (f.EpfIters == 0) return;
            int bx = (w + 7) / 8, by = (h + 7) / 8;
            var inv = new float[bx * by];
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
            var planes3 = colour.Length >= 3 ? new[] { colour[0], colour[1], colour[2] } : new[] { colour[0], colour[0], colour[0] };
            int[] passes = f.EpfIters == 1 ? new[] { 1 } : f.EpfIters == 2 ? new[] { 1, 2 } : new[] { 0, 1, 2 };
            foreach (int p in passes) planes3 = JxlFilters.EpfPass(planes3, w, h, p, inv, bx, f, parallel);
            for (int c = 0; c < colour.Length; c++) colour[c] = planes3[Math.Min(c, 2)];
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
