using System;
using System.Runtime.CompilerServices;
using System.Buffers;
using System.Threading.Tasks;

namespace Aardvark.ImageCodecs.JpegXl
{
    /// <summary>Synthetic noise: per group pseudo random planes (XorShift128+), a high pass filter, an intensity LUT.</summary>
    internal sealed class JxlNoise
    {
        public readonly float[] Lut = new float[8];

        public static JxlNoise Read(JxlBitReader br)
        {
            var n = new JxlNoise();
            for (int i = 0; i < 8; i++) n.Lut[i] = br.ReadBits(10) / 1024f;
            return n;
        }

        // ------------------------------------------------------------------ random numbers

        /// <summary>Eight XorShift128+ generators run side by side; one step gives 16 32 bit values.</summary>
        struct XorShift128Plus
        {
            const int N = 8;
            unsafe fixed ulong s0[N];
            unsafe fixed ulong s1[N];

            static ulong SplitMix64(ulong z)
            {
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }

            public unsafe XorShift128Plus(ulong seed0, ulong seed1)
            {
                s0[0] = SplitMix64(seed0 + 0x9E3779B97F4A7C15UL);
                s1[0] = SplitMix64(seed1 + 0x9E3779B97F4A7C15UL);
                for (int i = 1; i < N; i++)
                {
                    s0[i] = SplitMix64(s0[i - 1]);
                    s1[i] = SplitMix64(s1[i - 1]);
                }
            }

            /// <summary>16 random 32 bit values: the low then the high half of each generator's output.</summary>
            public unsafe void Fill(Span<uint> bits)
            {
                for (int i = 0; i < N; i++)
                {
                    ulong a = s0[i];
                    ulong b = s1[i];
                    ulong r = a + b;
                    s0[i] = b;
                    a ^= a << 23;
                    s1[i] = a ^ b ^ (a >> 18) ^ (b >> 5);
                    bits[2 * i] = (uint)r;
                    bits[2 * i + 1] = (uint)(r >> 32);
                }
            }
        }

        /// <summary>Random samples in [1, 2) for one group of one channel, row by row in batches of 16.</summary>
        static void RandomPlane(ref XorShift128Plus rng, float[] plane, int stride, int x0, int y0, int w, int h)
        {
            Span<uint> batch = stackalloc uint[16];
            for (int y = 0; y < h; y++)
            {
                int row = (y0 + y) * stride + x0;
                for (int x = 0; x < w; x += 16)
                {
                    rng.Fill(batch);
                    for (int i = 0, n = Math.Min(16, w - x); i < n; i++)
                        plane[row + x + i] = BitConverter.Int32BitsToSingle((int)((batch[i] >> 9) | 0x3F800000u));
                }
            }
        }

        float Strength(float x)
        {
            float s = x * 6;
            if (s < 0) s = 0;
            if (s > 6.999999f) s = 6.999999f;
            int f = (int)s;
            float frac = s - f;
            float v = Lut[f] + (Lut[f + 1] - Lut[f]) * frac;
            return v < 0 ? 0 : v > 1 ? 1 : v;
        }

        /// <summary>
        /// Adds the noise to the XYB planes of a frame (full resolution). The generator of each group is seeded with
        /// <paramref name="frameSeed"/> (displayed frames so far, counting this one, in the high half; frames since the last
        /// displayed one, counting this one if it is not displayed, in the low half) and the group position.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Apply(float[] X, float[] Y, float[] B, int w, int h, int groupDim, ulong frameSeed, float ytox, float ytob, bool parallel)
        {
            int n = w * h;
            var raw = new[] { ArrayPool<float>.Shared.Rent(n), ArrayPool<float>.Shared.Rent(n), ArrayPool<float>.Shared.Rent(n) };
            try
            {
                int gx = (w + groupDim - 1) / groupDim, gy = (h + groupDim - 1) / groupDim;
                void Group(int g)
                {
                    int x0 = g % gx * groupDim, y0 = g / gx * groupDim;
                    int gw = Math.Min(groupDim, w - x0), gh = Math.Min(groupDim, h - y0);
                    var rng = new XorShift128Plus(frameSeed, ((ulong)(uint)x0 << 32) + (uint)y0);
                    for (int c = 0; c < 3; c++) RandomPlane(ref rng, raw[c], w, x0, y0, gw, gh);
                }
                if (parallel && gx * gy > 1) Parallel.For(0, gx * gy, Group);
                else for (int g = 0; g < gx * gy; g++) Group(g);

                void Row(int y)
                {
                    Span<int> rows = stackalloc int[5];
                    Span<int> cols = stackalloc int[5];
                    for (int k = 0; k < 5; k++) rows[k] = JxlFilters.Mirror(y + k - 2, h) * w;
                    const float norm = 0.22f, rgCorr = 0.9921875f, rgNCorr = 0.0078125f;
                    Span<float> nz = stackalloc float[3];
                    for (int x = 0; x < w; x++)
                    {
                        bool inner = x >= 2 && x + 2 < w;
                        if (!inner) for (int l = 0; l < 5; l++) cols[l] = JxlFilters.Mirror(x + l - 2, w);
                        for (int c = 0; c < 3; c++)
                        {
                            // high pass: 0.16 * (sum of the 24 neighbours) - 3.84 * centre
                            var s = raw[c];
                            float sum = 0;
                            for (int k = 0; k < 5; k++)
                            {
                                int r = rows[k];
                                if (inner) sum += s[r + x - 2] + s[r + x - 1] + s[r + x] + s[r + x + 1] + s[r + x + 2];
                                else sum += s[r + cols[0]] + s[r + cols[1]] + s[r + cols[2]] + s[r + cols[3]] + s[r + cols[4]];
                            }
                            float centre = s[y * w + x];
                            nz[c] = ((sum - centre) * 0.16f + centre * -3.84f) * norm;
                        }
                        int i = y * w + x;
                        float vx = X[i], vy = Y[i];
                        float sg = Strength((vy - vx) * 0.5f), sr = Strength((vy + vx) * 0.5f);
                        float red = sr * (rgNCorr * nz[0] + rgCorr * nz[2]);
                        float green = sg * (rgNCorr * nz[1] + rgCorr * nz[2]);
                        float rg = red + green;
                        X[i] += red - green + ytox * rg;
                        Y[i] += rg;
                        B[i] += ytob * rg;
                    }
                }
                if (parallel && h >= 64) Parallel.For(0, h, Row);
                else for (int y = 0; y < h; y++) Row(y);
            }
            finally
            {
                foreach (var r in raw) ArrayPool<float>.Shared.Return(r);
            }
        }
    }
}
