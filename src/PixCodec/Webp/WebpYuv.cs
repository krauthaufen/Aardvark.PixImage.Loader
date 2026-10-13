using System;
using System.Runtime.CompilerServices;

namespace PixCodec
{
    /// <summary>
    /// Conversion between the Y'CbCr 4:2:0 planes of the lossy format and RGB: BT.601 limited range in 14 bit fixed
    /// point, chroma upsampled with the 9-3-3-1 bilinear kernel. The rounding follows libwebp so that decoded
    /// pixels are identical to what the reference implementation shows.
    /// </summary>
    internal static unsafe class WebpYuv
    {
        const int Fix2 = 6, Mask2 = (256 << Fix2) - 1;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static byte Clip8(int v) => (v & ~Mask2) == 0 ? (byte)(v >> Fix2) : v < 0 ? (byte)0 : (byte)255;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Store(int y, int u, int v, byte* dst)
        {
            int yy = (y * 19077) >> 8;
            dst[0] = Clip8(yy + ((v * 26149) >> 8) - 14234);
            dst[1] = Clip8(yy - ((u * 6419) >> 8) - ((v * 13320) >> 8) + 8708);
            dst[2] = Clip8(yy + ((u * 33050) >> 8) - 17685);
        }

        /// <summary>Writes the visible part of the frame as interleaved RGB with <paramref name="step"/> bytes per pixel.</summary>
        public static void ToRgb(Vp8Frame f, byte[] dst, int step)
        {
            int w = f.Width, h = f.Height, uvH = (h + 1) >> 1;
            fixed (byte* py = f.Y, pu = f.U, pv = f.V, pd = dst)
            {
                for (int y = 0; y < h; y++)
                {
                    // rows 2k-1 and 2k lie between the chroma rows k-1 and k; the nearer one weighs three times as much
                    int near = y >> 1, far = (y & 1) != 0 ? near + 1 : near - 1;
                    if (far < 0 || far >= uvH) far = near;
                    UpsampleRow(py + (long)y * f.YStride, pu + (long)near * f.UVStride, pu + (long)far * f.UVStride,
                        pv + (long)near * f.UVStride, pv + (long)far * f.UVStride, pd + (long)y * w * step, w, step);
                }
            }
        }

        static void UpsampleRow(byte* y, byte* nu, byte* fu, byte* nv, byte* fv, byte* dst, int w, int step)
        {
            Store(y[0], (3 * nu[0] + fu[0] + 2) >> 2, (3 * nv[0] + fv[0] + 2) >> 2, dst);
            int last = (w - 1) >> 1;
            for (int x = 1; x <= last; x++)
            {
                int nl = nu[x - 1], nr = nu[x], fl = fu[x - 1], fr = fu[x];
                int avg = nl + nr + fl + fr + 8;
                int ua = (((avg + 2 * (nr + fl)) >> 3) + nl) >> 1;
                int ub = (((avg + 2 * (nl + fr)) >> 3) + nr) >> 1;
                nl = nv[x - 1]; nr = nv[x]; fl = fv[x - 1]; fr = fv[x];
                avg = nl + nr + fl + fr + 8;
                int va = (((avg + 2 * (nr + fl)) >> 3) + nl) >> 1;
                int vb = (((avg + 2 * (nl + fr)) >> 3) + nr) >> 1;
                Store(y[2 * x - 1], ua, va, dst + (2 * x - 1) * step);
                Store(y[2 * x], ub, vb, dst + 2 * x * step);
            }
            if ((w & 1) == 0)
                Store(y[w - 1], (3 * nu[last] + fu[last] + 2) >> 2, (3 * nv[last] + fv[last] + 2) >> 2, dst + (w - 1) * step);
        }
    }
}
