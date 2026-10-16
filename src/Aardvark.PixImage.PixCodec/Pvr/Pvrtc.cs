using System;
using System.Buffers.Binary;
using System.Threading.Tasks;

namespace Aardvark.ImageCodecs.PvrImpl
{
    /// <summary>
    /// PVRTC1 decoding (2 and 4 bits per pixel), written from the Khronos Data Format Specification 1.3, section 24 (derived
    /// from Imagination's PVRTC Texture Compression User Guide). A PVRTC image is a grid of 64 bit words in reflected Morton
    /// order, each holding a colour of the two low resolution images A and B and the modulation of a 4 x 4 (8 x 4) texel area;
    /// both images are bilinearly upscaled (with wrap around) and blended per texel.
    /// </summary>
    internal static class Pvrtc
    {
        /// <summary>Words across / down for an image (powers of two, at least 2, as the stored data has them).</summary>
        public static (int ww, int wh) WordGrid(int w, int h, bool twoBpp)
        {
            int ww = Math.Max(2, (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)((w + (twoBpp ? 7 : 3)) / (twoBpp ? 8 : 4))));
            int wh = Math.Max(2, (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)((h + 3) / 4)));
            return (ww, wh);
        }

        public static long DataBytes(int w, int h, bool twoBpp)
        {
            var (ww, wh) = WordGrid(w, h, twoBpp);
            return (long)ww * wh * 8;
        }

        /// <summary>Index of word (x, y) in reflected Morton order (bits of x and y interleaved, y lowest, the rest of the longer side on top).</summary>
        public static int MortonOffset(int x, int y, int ww, int wh)
        {
            int min = Math.Min(ww, wh), offset = 0, shift = 0;
            for (int mask = 1; mask < min; mask <<= 1)
            {
                offset |= ((y & mask) | ((x & mask) << 1)) << shift;
                shift++;
            }
            return offset | ((x | y) >> shift) << (shift * 2);
        }

        /// <summary>Decodes to tightly packed RGBA8 of w x h texels.</summary>
        public static byte[] Decode(ReadOnlySpan<byte> src, int w, int h, bool twoBpp)
        {
            var (ww, wh) = WordGrid(w, h, twoBpp);
            int n = ww * wh;
            if (src.Length < n * 8) throw new ImageFormatException("PVRTC: data truncated");
            // the words in raster order, and their colours as ARGB 4555 (a in 0..15, r g b in 0..31)
            var mod = new uint[n];
            var colA = new int[n * 4];
            var colB = new int[n * 4];
            var flagM = new bool[n];
            for (int y = 0; y < wh; y++)
                for (int x = 0; x < ww; x++)
                {
                    int i = y * ww + x, o = MortonOffset(x, y, ww, wh) * 8;
                    mod[i] = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(o));
                    uint c = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(o + 4));
                    flagM[i] = (c & 1) != 0;
                    ColorB(c >> 16, colB.AsSpan(i * 4, 4));
                    ColorA(c & 0xFFFF, colA.AsSpan(i * 4, 4));
                }
            int bw = twoBpp ? 8 : 4;
            int pw = ww * bw, ph = wh * 4;
            int ow = Math.Min(w, pw), oh = Math.Min(h, ph);
            var o8 = new byte[(long)w * h * 4];
            Parallel.For(0, oh, y =>
            {
                Span<int> a = stackalloc int[4];
                Span<int> b = stackalloc int[4];
                for (int x = 0; x < ow; x++)
                {
                    Upscale(colA, ww, wh, x, y, twoBpp, a);
                    Upscale(colB, ww, wh, x, y, twoBpp, b);
                    int weight = twoBpp ? Weight2(mod, flagM, ww, wh, x, y, out bool punch) : Weight4(mod, flagM, ww, x, y, out punch);
                    long p = ((long)y * w + x) * 4;
                    for (int c = 0; c < 4; c++) o8[p + c] = (byte)((a[c] * (8 - weight) + b[c] * weight) >> 3);
                    if (punch) o8[p + 3] = 0;
                }
            });
            // texels beyond the stored grid (images smaller than a word's area never are; kept for safety)
            return o8;
        }

        static int Rep5From4(int v) => v << 1 | v >> 3;
        static int Rep5From3(int v) => v << 2 | v >> 1;

        /// <summary>Colour B: bits 31..16 of the colour word (bit 15 the opacity flag). Output A, R, G, B order: [r, g, b, a].</summary>
        static void ColorB(uint v, Span<int> o)
        {
            if ((v & 0x8000) != 0) { o[0] = (int)(v >> 10) & 31; o[1] = (int)(v >> 5) & 31; o[2] = (int)v & 31; o[3] = 15; }
            else
            {
                o[3] = ((int)(v >> 12) & 7) << 1;
                o[0] = Rep5From4((int)(v >> 8) & 15); o[1] = Rep5From4((int)(v >> 4) & 15); o[2] = Rep5From4((int)v & 15);
            }
        }

        /// <summary>Colour A: bits 15..1 of the colour word (bit 15 the opacity flag, bit 0 the modulation flag).</summary>
        static void ColorA(uint v, Span<int> o)
        {
            if ((v & 0x8000) != 0) { o[0] = (int)(v >> 10) & 31; o[1] = (int)(v >> 5) & 31; o[2] = Rep5From4((int)(v >> 1) & 15); o[3] = 15; }
            else
            {
                o[3] = ((int)(v >> 12) & 7) << 1;
                o[0] = Rep5From4((int)(v >> 8) & 15); o[1] = Rep5From4((int)(v >> 4) & 15); o[2] = Rep5From3((int)(v >> 1) & 7);
            }
        }

        /// <summary>Bilinear upscale of one low resolution image to 8 bit RGBA at texel (x, y).</summary>
        static void Upscale(int[] col, int ww, int wh, int x, int y, bool twoBpp, Span<int> o)
        {
            int bw = twoBpp ? 8 : 4, hx = bw / 2;
            int xl = (x - hx) >> (twoBpp ? 3 : 2), yl = (y - 2) >> 2;   // floor division
            int xr = x - hx - xl * bw, yr = y - 2 - yl * 4;
            int x0 = (xl % ww + ww) % ww, x1 = (x0 + 1) % ww, y0 = (yl % wh + wh) % wh, y1 = (y0 + 1) % wh;
            int w00 = (bw - xr) * (4 - yr), w10 = xr * (4 - yr), w01 = (bw - xr) * yr, w11 = xr * yr;
            int i00 = (y0 * ww + x0) * 4, i10 = (y0 * ww + x1) * 4, i01 = (y1 * ww + x0) * 4, i11 = (y1 * ww + x1) * 4;
            for (int c = 0; c < 4; c++)
            {
                int v = col[i00 + c] * w00 + col[i10 + c] * w10 + col[i01 + c] * w01 + col[i11 + c] * w11;
                if (!twoBpp) o[c] = c < 3 ? (v >> 1) + (v >> 6) : v + (v >> 4);
                else o[c] = c < 3 ? (v >> 2) + (v >> 7) : (v >> 1) + (v >> 5);
            }
        }

        static readonly int[] Std = { 0, 3, 5, 8 }, Punch = { 0, 4, 4, 8 };

        static int Weight4(uint[] mod, bool[] flagM, int ww, int x, int y, out bool punch)
        {
            int i = (y >> 2) * ww + (x >> 2);
            int v = (int)(mod[i] >> (2 * ((y & 3) * 4 + (x & 3)))) & 3;
            punch = flagM[i] && v == 2;
            return flagM[i] ? Punch[v] : Std[v];
        }

        static int Weight2(uint[] mod, bool[] flagM, int ww, int wh, int x, int y, out bool punch)
        {
            punch = false;
            int i = (y >> 2) * ww + (x >> 3);
            int xo = x & 7, yo = y & 3;
            uint m = mod[i];
            if (!flagM[i]) return (m >> (yo * 8 + xo) & 1) != 0 ? 8 : 0;
            if (((xo + yo) & 1) == 0)
            {
                // PVRTexTool decodes the stored values with the standard weights and without punch-through (the Khronos
                // text gives the weights of 4 bpp mode 1, which its decoder does not reproduce)
                return Std[Stored(m, xo, yo, out _)];
            }
            int pw = ww * 8, ph = wh * 4;
            int Wt(int px, int py)
            {
                px = (px % pw + pw) % pw; py = (py % ph + ph) % ph;
                int j = (py >> 2) * ww + (px >> 3);
                int ox = px & 7, oy = py & 3;
                if (!flagM[j]) return (mod[j] >> (oy * 8 + ox) & 1) != 0 ? 8 : 0;
                return Std[Stored(mod[j], ox, oy, out _)];
            }
            if ((m & 1) == 0) return (Wt(x - 1, y) + Wt(x, y - 1) + Wt(x + 1, y) + Wt(x, y + 1) + 2) >> 2;
            if ((m >> 20 & 1) != 0) return (Wt(x, y - 1) + Wt(x, y + 1) + 1) >> 1;
            return (Wt(x - 1, y) + Wt(x + 1, y) + 1) >> 1;
        }

        /// <summary>The stored 2 bit value of an even texel in the interpolated 2 bpp layout; texel (0, 0) (and (4, 2) when bit 0 is
        /// set) keep only their upper bit, which is duplicated.</summary>
        static int Stored(uint m, int xo, int yo, out bool single)
        {
            int k = yo * 4 + (xo >> 1);
            int v = (int)(m >> (2 * k)) & 3;
            single = k == 0 || k == 10 && (m & 1) != 0;
            if (single) v = (v >> 1) * 3;
            return v;
        }
    }
}
