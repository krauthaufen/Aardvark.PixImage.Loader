using System;

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

        sealed class XorShift128Plus
        {
            const int N = 8;
            readonly ulong[] s0 = new ulong[N], s1 = new ulong[N];

            static ulong SplitMix64(ulong z)
            {
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }

            public XorShift128Plus(ulong seed0, ulong seed1)
            {
                for (int i = 0; i < N; i++)
                {
                    s0[i] = SplitMix64(seed0 + (ulong)i * 0x9E3779B97F4A7C15UL);
                    s1[i] = SplitMix64(seed1 + (ulong)i * 0x9E3779B97F4A7C15UL);
                }
            }

            /// <summary>16 random 32 bit values.</summary>
            public void Fill(Span<uint> bits)
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

        static void RandomPlane(XorShift128Plus rng, float[] plane, int stride, int x0, int y0, int w, int h)
        {
            Span<uint> batch = stackalloc uint[16];
            for (int y = 0; y < h; y++)
            {
                int row = (y0 + y) * stride + x0;
                int x = 0;
                for (; x + 16 < w; x += 16)
                {
                    rng.Fill(batch);
                    for (int i = 0; i < 16; i++) plane[row + x + i] = BitConverter.Int32BitsToSingle((int)((batch[i] >> 9) | 0x3F800000u));
                }
                rng.Fill(batch);
                for (int i = 0; x < w; x++, i++) plane[row + x] = BitConverter.Int32BitsToSingle((int)((batch[i] >> 9) | 0x3F800000u));
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

        /// <summary>Adds the noise to the XYB planes of a frame (full resolution).</summary>
        public void Apply(float[] X, float[] Y, float[] B, int w, int h, int groupDim, int visible, int nonVisible, float ytox, float ytob)
        {
            var raw = new[] { new float[w * h], new float[w * h], new float[w * h] };
            int gx = (w + groupDim - 1) / groupDim, gy = (h + groupDim - 1) / groupDim;
            for (int j = 0; j < gy; j++)
                for (int i = 0; i < gx; i++)
                {
                    int x0 = i * groupDim, y0 = j * groupDim;
                    int gw = Math.Min(groupDim, w - x0), gh = Math.Min(groupDim, h - y0);
                    var rng = new XorShift128Plus(((ulong)(uint)visible << 32) + (uint)nonVisible, ((ulong)(uint)x0 << 32) + (uint)y0);
                    for (int c = 0; c < 3; c++) RandomPlane(rng, raw[c], w, x0, y0, gw, gh);
                }
            // high pass: 0.16 * (sum of the 24 neighbours) - 3.84 * centre
            var nz = new float[3][];
            for (int c = 0; c < 3; c++)
            {
                var s = raw[c];
                var o = new float[w * h];
                for (int y = 0; y < h; y++)
                {
                    Span<int> rows = stackalloc int[5];
                    for (int k = 0; k < 5; k++) rows[k] = JxlFilters.Mirror(y + k - 2, h) * w;
                    for (int x = 0; x < w; x++)
                    {
                        float sum = 0;
                        for (int k = 0; k < 5; k++)
                            for (int l = 0; l < 5; l++)
                                if (k != 2 || l != 2) sum += s[rows[k] + JxlFilters.Mirror(x + l - 2, w)];
                        o[y * w + x] = sum * 0.16f + s[y * w + x] * -3.84f;
                    }
                }
                nz[c] = o;
            }
            const float norm = 0.22f, rgCorr = 0.9921875f, rgNCorr = 0.0078125f;
            for (int i = 0; i < w * h; i++)
            {
                float vx = X[i], vy = Y[i];
                float sg = Strength((vy - vx) * 0.5f), sr = Strength((vy + vx) * 0.5f);
                float nr = nz[0][i] * norm, ng = nz[1][i] * norm, nc = nz[2][i] * norm;
                float red = sr * (rgNCorr * nr + rgCorr * nc);
                float green = sg * (rgNCorr * ng + rgCorr * nc);
                float rg = red + green;
                X[i] += red - green + ytox * rg;
                Y[i] += rg;
                B[i] += ytob * rg;
            }
        }
    }
}
