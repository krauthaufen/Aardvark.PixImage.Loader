using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace PixCodec
{
    /// <summary>
    /// Wavelet transforms of T.800 Annex F: reversible 5/3 on int / long, irreversible 9/7 on float.
    /// A 1-D signal covering [i0, i1) is held as its low-pass samples (even coordinates) followed by its high-pass
    /// samples; <c>a = i0 &amp; 1</c> tells which comes first in the interleaved signal. Symmetric extension shows up
    /// as index clamping: the missing neighbour of a border sample is its other neighbour.
    /// </summary>
    internal static unsafe class J2kDwt
    {
        public const float Alpha = -1.586134342059924f, Beta = -0.052980118572961f, Gamma = 0.882911075530934f,
            Delta = 0.443506852043971f, K = 1.230174104914001f, InvK = 1f / 1.230174104914001f;

        // ---------------- inverse, one level, in place in the level buffer ----------------

        /// <summary>
        /// Reconstructs resolution level r of a tile-component: the level buffer holds the subbands (columns split
        /// low | high, rows interleaved), the LL part is fetched from the level below.
        /// </summary>
        public static void Synthesize(J2kTileComp tc, int r, bool parallel)
        {
            var res = tc.Res[r];
            var low = tc.Res[r - 1];
            int w = res.W, h = res.H;
            if (w == 0 || h == 0) return;
            int a = (int)(res.C.X0 & 1), b = (int)(res.C.Y0 & 1);
            int nL = res.NLow;
            long lx0 = (res.C.X0 + 1) >> 1;
            int srcCol = (int)(lx0 - low.C.X0);
            switch (res.Buffer)
            {
                case int[] buf:
                {
                    var src = (int[]?)low.Buffer;
                    Rows(h, parallel && (long)w * h > 1 << 16, w, (y0, y1, tmp) =>
                    {
                        fixed (int* p = buf, t = tmp)
                        fixed (int* s = src)
                            for (int j = y0; j < y1; j++)
                            {
                                int* row = p + (long)j * w;
                                if (((j + b) & 1) == 0 && nL > 0)
                                {
                                    long v = (res.C.Y0 + j) >> 1;
                                    if (s != null) Buffer.MemoryCopy(s + (v - low.C.Y0) * low.W + srcCol, row, nL * 4L, nL * 4L);
                                    else new Span<int>(row, nL).Clear();
                                }
                                Synth53Row(row, nL, w - nL, a, t);
                            }
                    });
                    fixed (int* p = buf) Vertical53(p, w, h, b, parallel);
                    break;
                }
                case long[] buf:
                {
                    var src = (long[]?)low.Buffer;
                    var tmp = new long[w + 8];
                    fixed (long* p = buf, t = tmp)
                    fixed (long* s = src)
                    {
                        for (int j = 0; j < h; j++)
                        {
                            long* row = p + (long)j * w;
                            if (((j + b) & 1) == 0 && nL > 0)
                            {
                                long v = (res.C.Y0 + j) >> 1;
                                if (s != null) Buffer.MemoryCopy(s + (v - low.C.Y0) * low.W + srcCol, row, nL * 8L, nL * 8L);
                                else new Span<long>(row, nL).Clear();
                            }
                            Synth53Row(row, nL, w - nL, a, t);
                        }
                        Vertical53(p, w, h, b);
                    }
                    break;
                }
                case float[] buf:
                {
                    var src = (float[]?)low.Buffer;
                    Rows(h, parallel && (long)w * h > 1 << 16, w, (y0, y1, tmpI) =>
                    {
                        fixed (float* p = buf)
                        fixed (int* ti = tmpI)
                        fixed (float* s = src)
                            for (int j = y0; j < y1; j++)
                            {
                                float* row = p + (long)j * w;
                                if (((j + b) & 1) == 0 && nL > 0)
                                {
                                    long v = (res.C.Y0 + j) >> 1;
                                    if (s != null) Buffer.MemoryCopy(s + (v - low.C.Y0) * low.W + srcCol, row, nL * 4L, nL * 4L);
                                    else new Span<float>(row, nL).Clear();
                                }
                                Synth97Row(row, nL, w - nL, a, (float*)ti);
                            }
                    });
                    fixed (float* p = buf) Vertical97(p, w, h, b, parallel);
                    break;
                }
            }
        }

        delegate void RowRange(int y0, int y1, int[] tmp);

        static void Rows(int h, bool parallel, int w, RowRange body)
        {
            if (!parallel || h < 32)
            {
                body(0, h, new int[w + 16]);
                return;
            }
            int chunks = Math.Min(Environment.ProcessorCount, h / 16);
            Parallel.For(0, chunks, i => body((int)((long)h * i / chunks), (int)((long)h * (i + 1) / chunks), new int[w + 16]));
        }

        // ---- 5/3, rows ----

        /// <summary>row = L[0..nL) H[0..nH)  ->  interleaved signal, in place (tmp: w entries).</summary>
        static void Synth53Row(int* row, int nL, int nH, int a, int* tmp)
        {
            int w = nL + nH;
            if (w == 1) { if (a == 1) row[0] >>= 1; return; }
            int* L = row, H = row + nL, E = tmp, O = tmp + nL;
            // E[n] = L[n] - ((H[n + a - 1] + H[n + a] + 2) >> 2)
            {
                int off = a - 1;
                int n = 0;
                int lo = Math.Min(Math.Max(-off, 0), nL), hi = Math.Max(Math.Min(nH - 1 - off, nL), lo);
                for (; n < lo; n++) E[n] = L[n] - ((H[Clamp(n + off, nH)] + H[Clamp(n + off + 1, nH)] + 2) >> 2);
                if (Vector.IsHardwareAccelerated)
                {
                    var two = new Vector<int>(2);
                    for (; n <= hi - Vector<int>.Count; n += Vector<int>.Count)
                    {
                        var s = Unsafe.ReadUnaligned<Vector<int>>(H + n + off) + Unsafe.ReadUnaligned<Vector<int>>(H + n + off + 1) + two;
                        Unsafe.WriteUnaligned(E + n, Unsafe.ReadUnaligned<Vector<int>>(L + n) - Vector.ShiftRightArithmetic(s, 2));
                    }
                }
                for (; n < hi; n++) E[n] = L[n] - ((H[n + off] + H[n + off + 1] + 2) >> 2);
                for (; n < nL; n++) E[n] = L[n] - ((H[Clamp(n + off, nH)] + H[Clamp(n + off + 1, nH)] + 2) >> 2);
            }
            // O[n] = H[n] + ((E[n - a] + E[n - a + 1]) >> 1)
            {
                int off = -a;
                int n = 0;
                int lo = Math.Min(Math.Max(-off, 0), nH), hi = Math.Max(Math.Min(nL - 1 - off, nH), lo);
                for (; n < lo; n++) O[n] = H[n] + ((E[Clamp(n + off, nL)] + E[Clamp(n + off + 1, nL)]) >> 1);
                if (Vector.IsHardwareAccelerated)
                    for (; n <= hi - Vector<int>.Count; n += Vector<int>.Count)
                    {
                        var s = Unsafe.ReadUnaligned<Vector<int>>(E + n + off) + Unsafe.ReadUnaligned<Vector<int>>(E + n + off + 1);
                        Unsafe.WriteUnaligned(O + n, Unsafe.ReadUnaligned<Vector<int>>(H + n) + Vector.ShiftRightArithmetic(s, 1));
                    }
                for (; n < hi; n++) O[n] = H[n] + ((E[n + off] + E[n + off + 1]) >> 1);
                for (; n < nH; n++) O[n] = H[n] + ((E[Clamp(n + off, nL)] + E[Clamp(n + off + 1, nL)]) >> 1);
            }
            Interleave(E, nL, O, nH, a, row);
        }

        static void Synth53Row(long* row, int nL, int nH, int a, long* tmp)
        {
            int w = nL + nH;
            if (w == 1) { if (a == 1) row[0] >>= 1; return; }
            long* L = row, H = row + nL, E = tmp, O = tmp + nL;
            for (int n = 0; n < nL; n++) E[n] = L[n] - ((H[Clamp(n + a - 1, nH)] + H[Clamp(n + a, nH)] + 2) >> 2);
            for (int n = 0; n < nH; n++) O[n] = H[n] + ((E[Clamp(n - a, nL)] + E[Clamp(n - a + 1, nL)]) >> 1);
            for (int n = 0; n < nL; n++) row[2 * n + a] = E[n];
            for (int n = 0; n < nH; n++) row[2 * n + 1 - a] = O[n];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Clamp(int i, int n) => i < 0 ? 0 : i >= n ? n - 1 : i;

        /// <summary>out[2n + a] = E[n], out[2n + 1 - a] = O[n].</summary>
        static void Interleave(int* E, int nL, int* O, int nH, int a, int* dst)
        {
            int* first = a == 0 ? E : O, second = a == 0 ? O : E;
            int nF = a == 0 ? nL : nH, nS = a == 0 ? nH : nL;
            int n = 0;
            if (System.Runtime.Intrinsics.X86.Sse2.IsSupported)
            {
                for (; n <= nS - 4; n += 4)
                {
                    var x = System.Runtime.Intrinsics.X86.Sse2.LoadVector128(first + n);
                    var y = System.Runtime.Intrinsics.X86.Sse2.LoadVector128(second + n);
                    System.Runtime.Intrinsics.X86.Sse2.Store(dst + 2 * n, System.Runtime.Intrinsics.X86.Sse2.UnpackLow(x, y));
                    System.Runtime.Intrinsics.X86.Sse2.Store(dst + 2 * n + 4, System.Runtime.Intrinsics.X86.Sse2.UnpackHigh(x, y));
                }
            }
            else if (System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported)
            {
                for (; n <= nS - 4; n += 4)
                {
                    var x = System.Runtime.Intrinsics.Arm.AdvSimd.LoadVector128(first + n);
                    var y = System.Runtime.Intrinsics.Arm.AdvSimd.LoadVector128(second + n);
                    System.Runtime.Intrinsics.Arm.AdvSimd.Store(dst + 2 * n, System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.ZipLow(x, y));
                    System.Runtime.Intrinsics.Arm.AdvSimd.Store(dst + 2 * n + 4, System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.ZipHigh(x, y));
                }
            }
            for (; n < nS; n++) { dst[2 * n] = first[n]; dst[2 * n + 1] = second[n]; }
            if (nF > nS) dst[2 * nS] = first[nS];
        }

        // ---- 5/3, columns (whole rows at a time) ----

        static void Vertical53(int* p, int w, int h, int b, bool parallel)
        {
            if (h == 1)
            {
                if (b == 1) for (int x = 0; x < w; x++) p[x] >>= 1;
                return;
            }
            // column bands keep the working set in cache and can run on several threads
            int bandW = Math.Max(256, Math.Min(w, 4096));
            int bands = (w + bandW - 1) / bandW;
            void Band(int i)
            {
                int x0 = i * bandW, n = Math.Min(bandW, w - x0);
                int* q = p + x0;
                // each even row, then the odd row above it as soon as both of its neighbours are done
                int last = -1;
                for (int j = b; j < h; j += 2)
                {
                    Lift53(q + (long)j * w, q + (long)(j > 0 ? j - 1 : j + 1) * w, q + (long)(j + 1 < h ? j + 1 : j - 1) * w, n, true);
                    if (j > 0) Lift53(q + (long)(j - 1) * w, q + (long)(j > 1 ? j - 2 : j) * w, q + (long)j * w, n, false);
                    last = j;
                }
                if (last + 1 < h) Lift53(q + (long)(last + 1) * w, q + (long)last * w, q + (long)last * w, n, false);
            }
            if (parallel && bands > 1 && (long)w * h > 1 << 16) Parallel.For(0, bands, Band);
            else for (int i = 0; i < bands; i++) Band(i);
        }

        /// <summary>even: d -= (a + b + 2) >> 2; odd: d += (a + b) >> 1.</summary>
        static void Lift53(int* d, int* a, int* b, int n, bool even)
        {
            int i = 0;
            if (Vector.IsHardwareAccelerated)
            {
                if (even)
                {
                    var two = new Vector<int>(2);
                    for (; i <= n - Vector<int>.Count; i += Vector<int>.Count)
                        Unsafe.WriteUnaligned(d + i, Unsafe.ReadUnaligned<Vector<int>>(d + i)
                            - Vector.ShiftRightArithmetic(Unsafe.ReadUnaligned<Vector<int>>(a + i) + Unsafe.ReadUnaligned<Vector<int>>(b + i) + two, 2));
                }
                else
                {
                    for (; i <= n - Vector<int>.Count; i += Vector<int>.Count)
                        Unsafe.WriteUnaligned(d + i, Unsafe.ReadUnaligned<Vector<int>>(d + i)
                            + Vector.ShiftRightArithmetic(Unsafe.ReadUnaligned<Vector<int>>(a + i) + Unsafe.ReadUnaligned<Vector<int>>(b + i), 1));
                }
            }
            if (even) for (; i < n; i++) d[i] -= (a[i] + b[i] + 2) >> 2;
            else for (; i < n; i++) d[i] += (a[i] + b[i]) >> 1;
        }

        static void Vertical53(long* p, int w, int h, int b)
        {
            if (h == 1)
            {
                if (b == 1) for (int x = 0; x < w; x++) p[x] >>= 1;
                return;
            }
            for (int j = b; j < h; j += 2)
            {
                long* d = p + (long)j * w, u = p + (long)(j > 0 ? j - 1 : j + 1) * w, v = p + (long)(j + 1 < h ? j + 1 : j - 1) * w;
                for (int x = 0; x < w; x++) d[x] -= (u[x] + v[x] + 2) >> 2;
            }
            for (int j = 1 - b; j < h; j += 2)
            {
                long* d = p + (long)j * w, u = p + (long)(j > 0 ? j - 1 : j + 1) * w, v = p + (long)(j + 1 < h ? j + 1 : j - 1) * w;
                for (int x = 0; x < w; x++) d[x] += (u[x] + v[x]) >> 1;
            }
        }

        // ---- 9/7 ----

        /// <summary>d[n] = c0 * s[n] + c1 * (x[n + off] + x[n + off + 1]) with the x index clamped to [0, nx).</summary>
        static void Step97(float* d, float* s, int n, float c0, float* x, int nx, int off, float c1)
        {
            int i = 0;
            int lo = Math.Min(Math.Max(-off, 0), n), hi = Math.Max(Math.Min(nx - 1 - off, n), lo);
            for (; i < lo; i++) d[i] = c0 * s[i] + c1 * (x[Clamp(i + off, nx)] + x[Clamp(i + off + 1, nx)]);
            if (Vector.IsHardwareAccelerated)
            {
                var v0 = new Vector<float>(c0); var v1 = new Vector<float>(c1);
                for (; i <= hi - Vector<float>.Count; i += Vector<float>.Count)
                    Unsafe.WriteUnaligned(d + i, v0 * Unsafe.ReadUnaligned<Vector<float>>(s + i)
                        + v1 * (Unsafe.ReadUnaligned<Vector<float>>(x + i + off) + Unsafe.ReadUnaligned<Vector<float>>(x + i + off + 1)));
            }
            for (; i < hi; i++) d[i] = c0 * s[i] + c1 * (x[i + off] + x[i + off + 1]);
            for (; i < n; i++) d[i] = c0 * s[i] + c1 * (x[Clamp(i + off, nx)] + x[Clamp(i + off + 1, nx)]);
        }

        static void Synth97Row(float* row, int nL, int nH, int a, float* tmp)
        {
            int w = nL + nH;
            if (w == 1) { if (a == 1) row[0] *= 0.5f; return; }
            float* L = row, H = row + nL, E = tmp, O = tmp + nL;
            int eo = a - 1, oo = -a;   // neighbour offsets: odd samples around an even one, even ones around an odd one
            Step97(E, L, nL, K, H, nH, eo, -Delta * InvK);
            Step97(O, H, nH, InvK, E, nL, oo, -Gamma);
            Step97(E, E, nL, 1f, O, nH, eo, -Beta);
            Step97(O, O, nH, 1f, E, nL, oo, -Alpha);
            Interleave((int*)E, nL, (int*)O, nH, a, (int*)row);
        }

        /// <summary>d = c0 * d + c1 * (a + b)</summary>
        static void Lift97(float* d, float* a, float* b, int n, float c0, float c1)
        {
            int i = 0;
            if (Vector.IsHardwareAccelerated)
            {
                var v0 = new Vector<float>(c0); var v1 = new Vector<float>(c1);
                for (; i <= n - Vector<float>.Count; i += Vector<float>.Count)
                    Unsafe.WriteUnaligned(d + i, v0 * Unsafe.ReadUnaligned<Vector<float>>(d + i)
                        + v1 * (Unsafe.ReadUnaligned<Vector<float>>(a + i) + Unsafe.ReadUnaligned<Vector<float>>(b + i)));
            }
            for (; i < n; i++) d[i] = c0 * d[i] + c1 * (a[i] + b[i]);
        }

        static void Vertical97(float* p, int w, int h, int b, bool parallel)
        {
            if (h == 1)
            {
                if (b == 1) for (int x = 0; x < w; x++) p[x] *= 0.5f;
                return;
            }

            int bandW = Math.Max(256, Math.Min(w, 1024));
            int bands = (w + bandW - 1) / bandW;
            void Band(int i)
            {
                int x0 = i * bandW, n = Math.Min(bandW, w - x0);
                float* q = p + x0;
                for (int step = 0; step < 4; step++)
                {
                    float c0 = step == 0 ? K : step == 1 ? InvK : 1f;
                    float c1 = step == 0 ? -Delta * InvK : step == 1 ? -Gamma : step == 2 ? -Beta : -Alpha;
                    for (int j = (step & 1) == 0 ? b : 1 - b; j < h; j += 2)
                        Lift97(q + (long)j * w, q + (long)(j > 0 ? j - 1 : j + 1) * w, q + (long)(j + 1 < h ? j + 1 : j - 1) * w, n, c0, c1);
                }
            }
            if (parallel && bands > 1 && (long)w * h > 1 << 16) Parallel.For(0, bands, Band);
            else for (int i = 0; i < bands; i++) Band(i);
        }
    }
}
