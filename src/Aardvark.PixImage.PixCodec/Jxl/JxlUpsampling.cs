using System;
using System.Threading.Tasks;

namespace Aardvark.ImageCodecs.JpegXl
{
    /// <summary>
    /// Non-separable 2x / 4x / 8x upsampling: each output sample is a weighted sum of the 5x5 input samples around its
    /// source sample, clamped to their range. The weights of the top-left quadrant of sub-positions form a symmetric
    /// (5 k / 2) x (5 k / 2) matrix stored as its upper triangle; the other quadrants mirror them.
    /// </summary>
    internal static class JxlUpsampling
    {
        public static readonly float[] Default2 =
        {
            -0.01716200f, -0.03452303f, -0.04022174f, -0.02921014f, -0.00624645f, 0.14111091f, 0.28896755f, 0.00278718f,
            -0.01610267f, 0.56661550f, 0.03777607f, -0.01986694f, -0.03144731f, -0.01185068f, -0.00213539f,
        };

        public static readonly float[] Default4 =
        {
            -0.02419067f, -0.03491987f, -0.03693351f, -0.03094285f, -0.00529785f, -0.01663432f, -0.03556863f, -0.03888905f,
            -0.03516850f, -0.00989469f, 0.23651958f, 0.33392945f, -0.01073543f, -0.01313181f, -0.03556694f, 0.13048175f,
            0.40103025f, 0.03951150f, -0.02077584f, 0.46914198f, -0.00209270f, -0.01484589f, -0.04064806f, 0.18942530f,
            0.56279892f, 0.06674400f, -0.02335494f, -0.03551682f, -0.00754830f, -0.02267919f, -0.02363578f, 0.00315804f,
            -0.03399098f, -0.01359519f, -0.00091653f, -0.00335467f, -0.01163294f, -0.01610294f, -0.00974088f, -0.00191622f,
            -0.01095446f, -0.03198464f, -0.04455121f, -0.02799790f, -0.00645912f, 0.06390599f, 0.22963888f, 0.00630981f,
            -0.01897349f, 0.67537268f, 0.08483369f, -0.02534994f, -0.02205197f, -0.01667999f, -0.00384443f,
        };

        public static float[] Default8 = Array.Empty<float>();   // filled from Default8Values

        /// <summary>Full kernels: [((iy * k + ix) * 5 + wy) * 5 + wx] for every sub-position.</summary>
        public static float[] Kernel(int k, float[] weights)
        {
            int half = k / 2, n = 5 * half;
            if (weights.Length != n * (n + 1) / 2) throw new ImageFormatException("JPEG XL: bad upsampling weights");
            var kern = new float[k * k * 25];
            for (int iy = 0; iy < k; iy++)
                for (int ix = 0; ix < k; ix++)
                {
                    bool fy = iy >= half, fx = ix >= half;
                    int ry = fy ? k - 1 - iy : iy, rx = fx ? k - 1 - ix : ix;
                    for (int wy = 0; wy < 5; wy++)
                        for (int wx = 0; wx < 5; wx++)
                        {
                            int sy = fy ? 4 - wy : wy, sx = fx ? 4 - wx : wx;
                            int a = ry * 5 + sy, b = rx * 5 + sx;
                            int lo = Math.Min(a, b), hi = Math.Max(a, b);
                            kern[((iy * k + ix) * 5 + wy) * 5 + wx] = weights[n * lo - lo * (lo - 1) / 2 + hi - lo];
                        }
                }
            return kern;
        }

        public static float[] Upsample(float[] src, int w, int h, int k, float[] kern, int ow, int oh, bool parallel)
        {
            var o = new float[(long)ow * oh];
            void Row(int y)
            {
                Span<int> rows = stackalloc int[5];
                for (int d = 0; d < 5; d++) rows[d] = JxlFilters.Mirror(y + d - 2, h) * w;
                Span<float> win = stackalloc float[25];
                for (int x = 0; x < w; x++)
                {
                    float mn = float.MaxValue, mx = float.MinValue;
                    for (int wy = 0; wy < 5; wy++)
                        for (int wx = 0; wx < 5; wx++)
                        {
                            float v = src[rows[wy] + JxlFilters.Mirror(x + wx - 2, w)];
                            win[wy * 5 + wx] = v;
                            if (v < mn) mn = v;
                            if (v > mx) mx = v;
                        }
                    for (int iy = 0; iy < k; iy++)
                    {
                        int oy = y * k + iy;
                        if (oy >= oh) break;
                        for (int ix = 0; ix < k; ix++)
                        {
                            int ox = x * k + ix;
                            if (ox >= ow) break;
                            int kb = (iy * k + ix) * 25;
                            float s = 0;
                            for (int t = 0; t < 25; t++) s += kern[kb + t] * win[t];
                            o[(long)oy * ow + ox] = s < mn ? mn : s > mx ? mx : s;
                        }
                    }
                }
            }
            if (parallel && h >= 32) Parallel.For(0, h, Row);
            else for (int y = 0; y < h; y++) Row(y);
            return o;
        }
    }
}
