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

        // ------------------------------------------------------------------ RGB to Y'CbCr (encoder side)

        const int YuvFix = 16, YuvHalf = 1 << 15;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static byte ToY(int r, int g, int b) => (byte)((16839 * r + 33059 * g + 6420 * b + YuvHalf + (16 << YuvFix)) >> YuvFix);

        // chroma from the sum of four pixels
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static byte ToUv(int v) => (byte)Math.Clamp((v + (YuvHalf << 2) + (128 << (YuvFix + 2))) >> (YuvFix + 2), 0, 255);

        /// <summary>
        /// Converts ARGB pixels to planes padded to whole macroblocks (edge pixels repeated). Chroma is the plain
        /// average of each 2x2 block: averaging in linear light looks a little better on saturated edges but costs
        /// about 2 dB of PSNR on drawings, as the decoder's upsampling works on the gamma encoded values too.
        /// </summary>
        public static Vp8Frame FromArgb(uint[] argb, int w, int h)
        {
            int mbW = (w + 15) >> 4, mbH = (h + 15) >> 4;
            var f = new Vp8Frame
            {
                Width = w, Height = h, YStride = mbW * 16, UVStride = mbW * 8,
                Y = new byte[mbW * 16 * mbH * 16], U = new byte[mbW * 8 * mbH * 8], V = new byte[mbW * 8 * mbH * 8],
            };
            int uvW = (w + 1) >> 1, uvH = (h + 1) >> 1;
            fixed (uint* src = argb)
            fixed (byte* py = f.Y, pu = f.U, pv = f.V)
            {
                for (int y = 0; y < h; y++)
                {
                    uint* s = src + (long)y * w;
                    byte* d = py + (long)y * f.YStride;
                    for (int x = 0; x < w; x++)
                    {
                        uint v = s[x];
                        d[x] = ToY((int)(v >> 16) & 0xff, (int)(v >> 8) & 0xff, (int)v & 0xff);
                    }
                    for (int x = w; x < f.YStride; x++) d[x] = d[w - 1];
                }
                for (int y = h; y < mbH * 16; y++)
                    Buffer.MemoryCopy(py + (long)(h - 1) * f.YStride, py + (long)y * f.YStride, f.YStride, f.YStride);

                for (int y = 0; y < uvH; y++)
                {
                    uint* s0 = src + (long)(2 * y) * w;
                    uint* s1 = 2 * y + 1 < h ? s0 + w : s0;
                    byte* du = pu + (long)y * f.UVStride, dv = pv + (long)y * f.UVStride;
                    for (int x = 0; x < uvW; x++)
                    {
                        int x0 = 2 * x, x1 = 2 * x + 1 < w ? x0 + 1 : x0;
                        uint a = s0[x0], b = s0[x1], c = s1[x0], e = s1[x1];
                        int r = (int)(((a >> 16) & 0xff) + ((b >> 16) & 0xff) + ((c >> 16) & 0xff) + ((e >> 16) & 0xff));
                        int g = (int)(((a >> 8) & 0xff) + ((b >> 8) & 0xff) + ((c >> 8) & 0xff) + ((e >> 8) & 0xff));
                        int bl = (int)((a & 0xff) + (b & 0xff) + (c & 0xff) + (e & 0xff));
                        du[x] = ToUv(-9719 * r - 19081 * g + 28800 * bl);
                        dv[x] = ToUv(28800 * r - 24116 * g - 4684 * bl);
                    }
                    for (int x = uvW; x < f.UVStride; x++) { du[x] = du[uvW - 1]; dv[x] = dv[uvW - 1]; }
                }
                for (int y = uvH; y < mbH * 8; y++)
                {
                    Buffer.MemoryCopy(pu + (long)(uvH - 1) * f.UVStride, pu + (long)y * f.UVStride, f.UVStride, f.UVStride);
                    Buffer.MemoryCopy(pv + (long)(uvH - 1) * f.UVStride, pv + (long)y * f.UVStride, f.UVStride, f.UVStride);
                }
            }
            return f;
        }
    }
}
