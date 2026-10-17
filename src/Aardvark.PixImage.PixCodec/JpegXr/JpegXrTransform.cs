using System;
using System.Runtime.CompilerServices;

namespace Aardvark.ImageCodecs.JpegXrImpl
{
    /// <summary>
    /// The lifting steps of the JPEG XR lapped transform: the inverse core transform and overlap post-filters of T.832
    /// 9.9.7 / 9.9.8, the forward core transform and overlap pre-filters of Annex D, and the geometric application of the
    /// overlap filters to a component (9.9.3, 9.9.6, D.5).
    /// </summary>
    internal static class JxrTransform
    {
        // ------------------------------------------------------------------ inverse (9.9.7, 9.9.8)

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void T2x2h(ref int a, ref int b, ref int c, ref int d, int round)
        {
            a += d;
            b -= c;
            int t1 = (a - b + round) >> 1;
            int t2 = c;
            c = t1 - d;
            d = t1 - t2;
            a -= d;
            b += c;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void InvTodd(ref int a, ref int b, ref int c, ref int d)
        {
            b += d;
            a -= c;
            d -= b >> 1;
            c += (a + 1) >> 1;
            a -= (3 * b + 4) >> 3;
            b += (3 * a + 4) >> 3;
            c -= (3 * d + 4) >> 3;
            d += (3 * c + 4) >> 3;
            c -= (b + 1) >> 1;
            d = ((a + 1) >> 1) - d;
            b += c;
            a -= d;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void InvToddOdd(ref int a, ref int b, ref int c, ref int d)
        {
            d += a;
            c -= b;
            int t1 = d >> 1, t2 = c >> 1;
            a -= t1;
            b += t2;
            a -= (b * 3 + 3) >> 3;
            b += (a * 3 + 3) >> 2;
            a -= (b * 3 + 4) >> 3;
            b -= t2;
            a += t1;
            c += b;
            d -= a;
            b = -b;
            c = -c;
        }

        /// <summary>ICT4x4( ) on 16 coefficients in raster order (Table 160), including InvPermute( ).</summary>
        public static void Ict4x4(Span<int> c)
        {
            Span<int> t = stackalloc int[16];
            var perm = JxrTables.InvPermArr;
            for (int i = 0; i < 16; i++) t[perm[i]] = c[i];
            Ict4x4Permuted(t);
            t.CopyTo(c);
        }

        /// <summary>ICT4x4( ) after the permutation.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Ict4x4Permuted(Span<int> c)
        {
            T2x2h(ref c[0], ref c[1], ref c[4], ref c[5], 1);
            InvTodd(ref c[2], ref c[3], ref c[6], ref c[7]);
            InvTodd(ref c[8], ref c[12], ref c[9], ref c[13]);
            InvToddOdd(ref c[10], ref c[11], ref c[14], ref c[15]);
            T2x2h(ref c[0], ref c[3], ref c[12], ref c[15], 0);
            T2x2h(ref c[5], ref c[6], ref c[9], ref c[10], 0);
            T2x2h(ref c[1], ref c[2], ref c[13], ref c[14], 0);
            T2x2h(ref c[4], ref c[7], ref c[8], ref c[11], 0);
        }

        /// <summary>
        /// ICT4x4( ) of the 4x4 block at <paramref name="o"/> of a sample array with row stride <paramref name="s"/> (9.9.5):
        /// the permutation is folded into the reads.
        /// </summary>
        public static void Ict4x4(int[] p, int o, int s)
        {
            Span<int> t = stackalloc int[16];
            // t[InvPermArr[i]] = block[i], block in raster order
            ref int r0 = ref p[o];
            t[0] = r0; t[8] = Unsafe.Add(ref r0, 1); t[4] = Unsafe.Add(ref r0, 2); t[13] = Unsafe.Add(ref r0, 3);
            ref int r1 = ref Unsafe.Add(ref r0, s);
            t[2] = r1; t[15] = Unsafe.Add(ref r1, 1); t[3] = Unsafe.Add(ref r1, 2); t[14] = Unsafe.Add(ref r1, 3);
            ref int r2 = ref Unsafe.Add(ref r1, s);
            t[1] = r2; t[12] = Unsafe.Add(ref r2, 1); t[5] = Unsafe.Add(ref r2, 2); t[9] = Unsafe.Add(ref r2, 3);
            ref int r3 = ref Unsafe.Add(ref r2, s);
            t[7] = r3; t[11] = Unsafe.Add(ref r3, 1); t[6] = Unsafe.Add(ref r3, 2); t[10] = Unsafe.Add(ref r3, 3);
            Ict4x4Permuted(t);
            r0 = t[0]; Unsafe.Add(ref r0, 1) = t[1]; Unsafe.Add(ref r0, 2) = t[2]; Unsafe.Add(ref r0, 3) = t[3];
            r1 = t[4]; Unsafe.Add(ref r1, 1) = t[5]; Unsafe.Add(ref r1, 2) = t[6]; Unsafe.Add(ref r1, 3) = t[7];
            r2 = t[8]; Unsafe.Add(ref r2, 1) = t[9]; Unsafe.Add(ref r2, 2) = t[10]; Unsafe.Add(ref r2, 3) = t[11];
            r3 = t[12]; Unsafe.Add(ref r3, 1) = t[13]; Unsafe.Add(ref r3, 2) = t[14]; Unsafe.Add(ref r3, 3) = t[15];
        }

        /// <summary>T2pt( ) (Table 167).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void T2pt(ref int a, ref int b)
        {
            a -= (b + 1) >> 1;
            b += a;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void InvRotate(ref int a, ref int b)
        {
            a -= (b + 1) >> 1;
            b += (a + 1) >> 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void InvScale(ref int a, ref int b)
        {
            a += b;
            b = (a >> 1) - b;
            a += (b * 3) >> 3;
            b += (a * 3) >> 4;
            b += a >> 7;
            b -= a >> 10;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void T2x2hPost(ref int a, ref int b, ref int c, ref int d)
        {
            b -= c;
            a += (d * 3 + 4) >> 3;
            d -= b >> 1;
            c = ((a - b) >> 1) - c;
            int t = c;
            c = d;
            d = t;
            a -= d;
            b += c;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void InvToddOddPost(ref int a, ref int b, ref int c, ref int d)
        {
            d += a;
            c -= b;
            int t1 = d >> 1, t2 = c >> 1;
            a -= t1;
            b += t2;
            a -= (b * 3 + 6) >> 3;
            b += (a * 3 + 2) >> 2;
            a -= (b * 3 + 4) >> 3;
            b -= t2;
            a += t1;
            c += b;
            d -= a;
        }

        /// <summary>OverlapPostFilter4x4( ) (Table 168) on 16 values in raster order.</summary>
        static void PostFilter4x4(Span<int> c)
        {
            T2x2h(ref c[0], ref c[3], ref c[12], ref c[15], 0);
            T2x2h(ref c[1], ref c[2], ref c[13], ref c[14], 0);
            T2x2h(ref c[4], ref c[7], ref c[8], ref c[11], 0);
            T2x2h(ref c[5], ref c[6], ref c[9], ref c[10], 0);
            InvRotate(ref c[13], ref c[12]);
            InvRotate(ref c[9], ref c[8]);
            InvRotate(ref c[7], ref c[3]);
            InvRotate(ref c[6], ref c[2]);
            InvToddOddPost(ref c[10], ref c[11], ref c[14], ref c[15]);
            InvScale(ref c[0], ref c[15]);
            InvScale(ref c[1], ref c[14]);
            InvScale(ref c[4], ref c[11]);
            InvScale(ref c[5], ref c[10]);
            T2x2hPost(ref c[0], ref c[3], ref c[12], ref c[15]);
            T2x2hPost(ref c[1], ref c[2], ref c[13], ref c[14]);
            T2x2hPost(ref c[4], ref c[7], ref c[8], ref c[11]);
            T2x2hPost(ref c[5], ref c[6], ref c[9], ref c[10]);
        }

        /// <summary>OverlapPostFilter4( ) (Table 169).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void PostFilter4(ref int a, ref int b, ref int c, ref int d)
        {
            a += d;
            b += c;
            d -= (a + 1) >> 1;
            c -= (b + 1) >> 1;
            InvScale(ref a, ref d);
            InvScale(ref b, ref c);
            a += (d * 3 + 4) >> 3;
            b += (c * 3 + 4) >> 3;
            d -= a >> 1;
            c -= b >> 1;
            a += d;
            b += c;
            d = -d;
            c = -c;
            InvRotate(ref c, ref d);
            d += (a + 1) >> 1;
            c += (b + 1) >> 1;
            a -= d;
            b -= c;
        }

        /// <summary>OverlapPostFilter2x2( ) (Table 170).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void PostFilter2x2(ref int a, ref int b, ref int c, ref int d)
        {
            a += d;
            b += c;
            d -= (a + 1) >> 1;
            c -= (b + 1) >> 1;
            b += (a + 2) >> 2;
            a += (b + 1) >> 1;
            a += b >> 5;
            a += b >> 9;
            a += b >> 13;
            b += (a + 2) >> 2;
            d += (a + 1) >> 1;
            c += (b + 1) >> 1;
            a -= d;
            b -= c;
        }

        /// <summary>OverlapPostFilter2( ) (Table 171).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void PostFilter2(ref int a, ref int b)
        {
            b += (a + 2) >> 2;
            a += (b + 1) >> 1;
            a += b >> 5;
            a += b >> 9;
            a += b >> 13;
            b += (a + 2) >> 2;
        }

        // ------------------------------------------------------------------ forward (Annex D)

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void FwdTodd(ref int a, ref int b, ref int c, ref int d)
        {
            b -= c;
            a += d;
            c += (b + 1) >> 1;
            d = ((a + 1) >> 1) - d;
            b -= (3 * a + 4) >> 3;
            a += (3 * b + 4) >> 3;
            d -= (3 * c + 4) >> 3;
            c += (3 * d + 4) >> 3;
            d += b >> 1;
            c -= (a + 1) >> 1;
            b -= d;
            a += c;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void FwdToddOdd(ref int a, ref int b, ref int c, ref int d)
        {
            b = -b;
            c = -c;
            d += a;
            c -= b;
            int t1 = d >> 1, t2 = c >> 1;
            a -= t1;
            b += t2;
            a += (b * 3 + 4) >> 3;
            b -= (a * 3 + 3) >> 2;
            a += (b * 3 + 3) >> 3;
            b -= t2;
            a += t1;
            c += b;
            d -= a;
        }

        /// <summary>
        /// The exact inverse of <see cref="T2x2h"/> with the same rounding (Annex D uses T2x2h( ) for both directions, which
        /// is an involution only for even sums).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void T2x2hInverse(ref int a, ref int b, ref int c, ref int d, int round)
        {
            // undo: a -= d; b += c (last two steps), then t1 = c + d_orig ... solved step by step
            b -= c;
            a += d;
            // now a = a1, b = b1 (after the first two steps), c = t1 - d0, d = t1 - c0
            int t1 = (a - b + round) >> 1;
            int d0 = t1 - c;
            int c0 = t1 - d;
            c = c0;
            d = d0;
            a -= d;
            b += c;
        }

        /// <summary>FCT4x4( ) (Table D.13) on 16 samples in raster order, including FwdPermute( ).</summary>
        public static void Fct4x4(Span<int> c)
        {
            T2x2hInverse(ref c[0], ref c[3], ref c[12], ref c[15], 0);
            T2x2hInverse(ref c[5], ref c[6], ref c[9], ref c[10], 0);
            T2x2hInverse(ref c[1], ref c[2], ref c[13], ref c[14], 0);
            T2x2hInverse(ref c[4], ref c[7], ref c[8], ref c[11], 0);
            T2x2hInverse(ref c[0], ref c[1], ref c[4], ref c[5], 1);
            FwdTodd(ref c[2], ref c[3], ref c[6], ref c[7]);
            FwdTodd(ref c[8], ref c[12], ref c[9], ref c[13]);
            FwdToddOdd(ref c[10], ref c[11], ref c[14], ref c[15]);
            Span<int> t = stackalloc int[16];
            var perm = JxrTables.InvPermArr;
            for (int i = 0; i < 16; i++) t[i] = c[perm[i]];   // inverse of ICT's t[perm[i]] = c[i]
            t.CopyTo(c);
        }

        public static void Fct4x4(int[] p, int o, int s)
        {
            Span<int> t = stackalloc int[16];
            for (int y = 0; y < 4; y++)
                for (int x = 0; x < 4; x++) t[y * 4 + x] = p[o + y * s + x];
            Fct4x4(t);
            for (int y = 0; y < 4; y++)
                for (int x = 0; x < 4; x++) p[o + y * s + x] = t[y * 4 + x];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void FwdRotate(ref int a, ref int b)
        {
            b -= (a + 1) >> 1;
            a += (b + 1) >> 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void FwdScale(ref int a, ref int b)
        {
            b -= (a * 3) >> 4;
            b -= a >> 7;
            b += a >> 10;
            a -= (b * 3) >> 3;
            b = (a >> 1) - b;
            a -= b;
        }

        // inverse of InvRotate: a' = a - ((b+1)>>1); b' = b + ((a'+1)>>1)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void UndoInvRotate(ref int a, ref int b)
        {
            b -= (a + 1) >> 1;
            a += (b + 1) >> 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void UndoT2x2hPost(ref int a, ref int b, ref int c, ref int d)
        {
            b -= c;
            a += d;
            int t = c;
            c = d;
            d = t;
            c = ((a - b) >> 1) - c;
            d += b >> 1;
            a -= (d * 3 + 4) >> 3;
            b += c;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void UndoInvToddOddPost(ref int a, ref int b, ref int c, ref int d)
        {
            d += a;
            c -= b;
            int t1 = d >> 1, t2 = c >> 1;
            a -= t1;
            b += t2;
            a += (b * 3 + 4) >> 3;
            b -= (a * 3 + 2) >> 2;
            a += (b * 3 + 6) >> 3;
            b -= t2;
            a += t1;
            c += b;
            d -= a;
        }

        /// <summary>The exact inverse of <see cref="PostFilter4x4"/> (Table D.18 as derived from Table 168).</summary>
        static void PreFilter4x4(Span<int> c)
        {
            UndoT2x2hPost(ref c[5], ref c[6], ref c[9], ref c[10]);
            UndoT2x2hPost(ref c[4], ref c[7], ref c[8], ref c[11]);
            UndoT2x2hPost(ref c[1], ref c[2], ref c[13], ref c[14]);
            UndoT2x2hPost(ref c[0], ref c[3], ref c[12], ref c[15]);
            FwdScale(ref c[5], ref c[10]);
            FwdScale(ref c[4], ref c[11]);
            FwdScale(ref c[1], ref c[14]);
            FwdScale(ref c[0], ref c[15]);
            UndoInvToddOddPost(ref c[10], ref c[11], ref c[14], ref c[15]);
            UndoInvRotate(ref c[6], ref c[2]);
            UndoInvRotate(ref c[7], ref c[3]);
            UndoInvRotate(ref c[9], ref c[8]);
            UndoInvRotate(ref c[13], ref c[12]);
            T2x2hInverse(ref c[5], ref c[6], ref c[9], ref c[10], 0);
            T2x2hInverse(ref c[4], ref c[7], ref c[8], ref c[11], 0);
            T2x2hInverse(ref c[1], ref c[2], ref c[13], ref c[14], 0);
            T2x2hInverse(ref c[0], ref c[3], ref c[12], ref c[15], 0);
        }

        /// <summary>The exact inverse of <see cref="PostFilter4"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void PreFilter4(ref int a, ref int b, ref int c, ref int d)
        {
            a += d;
            b += c;
            d -= (a + 1) >> 1;
            c -= (b + 1) >> 1;
            UndoInvRotate(ref c, ref d);
            d = -d;
            c = -c;
            a -= d;
            b -= c;
            d += a >> 1;
            c += b >> 1;
            a -= (d * 3 + 4) >> 3;
            b -= (c * 3 + 4) >> 3;
            FwdScale(ref a, ref d);
            FwdScale(ref b, ref c);
            d += (a + 1) >> 1;
            c += (b + 1) >> 1;
            a -= d;
            b -= c;
        }

        /// <summary>The exact inverse of <see cref="PostFilter2x2"/> (Table D.20).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void PreFilter2x2(ref int a, ref int b, ref int c, ref int d)
        {
            a += d;
            b += c;
            d -= (a + 1) >> 1;
            c -= (b + 1) >> 1;
            b -= (a + 2) >> 2;
            a -= b >> 13;
            a -= b >> 9;
            a -= b >> 5;
            a -= (b + 1) >> 1;
            b -= (a + 2) >> 2;
            d += (a + 1) >> 1;
            c += (b + 1) >> 1;
            a -= d;
            b -= c;
        }

        /// <summary>The exact inverse of <see cref="PostFilter2"/> (Table D.21).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void PreFilter2(ref int a, ref int b)
        {
            b -= (a + 2) >> 2;
            a -= b >> 13;
            a -= b >> 9;
            a -= b >> 5;
            a -= (b + 1) >> 1;
            b -= (a + 2) >> 2;
        }

        // ------------------------------------------------------------------ geometric application

        /// <summary>
        /// The 4x4 / 4-point overlap filters of one region (the image, or a tile with hard tiling) of a sample array with block
        /// size 4 (9.9.6, and 9.9.3.2 for the DC-LP array): every interior block corner, the two outer rows and columns, the
        /// four corners. <paramref name="forward"/> runs the pre-filters of Annex D instead. Rows <paramref name="rowFrom"/> ..
        /// <paramref name="rowTo"/> of interior junctions restrict the work for parallel bands (the edges are done when
        /// <paramref name="edges"/> is set).
        /// </summary>
        public static void Overlap4(int[] p, int stride, int x0, int y0, int x1, int y1, bool forward)
        {
            Overlap4Interior(p, stride, x0, y0, x1, y1, y0 + 2, y1 - 2, forward);
            Overlap4Edges(p, stride, x0, y0, x1, y1, forward);
        }

        /// <summary>Interior 4x4 filters whose top row y satisfies yFrom &lt;= y &lt; yTo (with y = y0 + 2 + 4k).</summary>
        public static void Overlap4Interior(int[] p, int s, int x0, int y0, int x1, int y1, int yFrom, int yTo, bool forward)
        {
            Span<int> t = stackalloc int[16];
            int yStart = y0 + 2;
            if (yFrom > yStart) yStart += (yFrom - yStart + 3) / 4 * 4;
            int yEnd = Math.Min(yTo, y1 - 2);
            for (int y = yStart; y < yEnd; y += 4)
                for (int x = x0 + 2; x < x1 - 2; x += 4)
                {
                    int o = y * s + x;
                    for (int r = 0; r < 4; r++)
                    {
                        int q = o + r * s;
                        t[r * 4] = p[q]; t[r * 4 + 1] = p[q + 1]; t[r * 4 + 2] = p[q + 2]; t[r * 4 + 3] = p[q + 3];
                    }
                    if (forward) PreFilter4x4(t); else PostFilter4x4(t);
                    for (int r = 0; r < 4; r++)
                    {
                        int q = o + r * s;
                        p[q] = t[r * 4]; p[q + 1] = t[r * 4 + 1]; p[q + 2] = t[r * 4 + 2]; p[q + 3] = t[r * 4 + 3];
                    }
                }
        }

        public static void Overlap4Edges(int[] p, int s, int x0, int y0, int x1, int y1, bool forward)
        {
            // left and right two columns: vertical 4-point filters across block boundaries
            for (int y = y0 + 2; y < y1 - 2; y += 4)
                foreach (int x in stackalloc int[] { x0, x0 + 1, x1 - 2, x1 - 1 })
                {
                    int o = y * s + x;
                    F4(p, o, o + s, o + 2 * s, o + 3 * s, forward);
                }
            // top and bottom two rows: horizontal
            for (int x = x0 + 2; x < x1 - 2; x += 4)
                foreach (int y in stackalloc int[] { y0, y0 + 1, y1 - 2, y1 - 1 })
                {
                    int o = y * s + x;
                    F4(p, o, o + 1, o + 2, o + 3, forward);
                }
            // corners (top-left, top-right, bottom-left, bottom-right), 2x2 in raster order
            foreach (var (cx, cy) in new[] { (x0, y0), (x1 - 2, y0), (x0, y1 - 2), (x1 - 2, y1 - 2) })
            {
                int o = cy * s + cx;
                F4(p, o, o + 1, o + s, o + s + 1, forward);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void F4(int[] p, int i0, int i1, int i2, int i3, bool forward)
        {
            if (forward) PreFilter4(ref p[i0], ref p[i1], ref p[i2], ref p[i3]);
            else PostFilter4(ref p[i0], ref p[i1], ref p[i2], ref p[i3]);
        }

        /// <summary>
        /// The 2x2 / 2-point overlap filters of a chroma DC-LP array with block size 2 (YUV420, YUV422; 9.9.3.3, 9.9.3.4), with
        /// the corner difference ("OverlapPostFilter1") before and the addition after.
        /// </summary>
        public static void Overlap2(int[] p, int s, int x0, int y0, int x1, int y1, bool forward)
        {
            int tl = y0 * s + x0, tr = y0 * s + x1 - 1, bl = (y1 - 1) * s + x0, br = (y1 - 1) * s + x1 - 1;
            if (!forward)
            {
                p[tl] -= p[tl + 1];
                p[tr] -= p[tr - 1];
                p[bl] -= p[bl + 1];
                p[br] -= p[br - 1];
            }
            else
            {
                // the encoder undoes the decoder's final additions first
                p[br] -= p[br - 1];
                p[bl] -= p[bl + 1];
                p[tr] -= p[tr - 1];
                p[tl] -= p[tl + 1];
            }
            for (int y = y0 + 1; y < y1 - 1; y += 2)
                for (int x = x0 + 1; x < x1 - 1; x += 2)
                {
                    int o = y * s + x;
                    if (forward) PreFilter2x2(ref p[o], ref p[o + 1], ref p[o + s], ref p[o + s + 1]);
                    else PostFilter2x2(ref p[o], ref p[o + 1], ref p[o + s], ref p[o + s + 1]);
                }
            for (int y = y0 + 1; y < y1 - 1; y += 2)
                foreach (int x in stackalloc int[] { x0, x1 - 1 })
                {
                    int o = y * s + x;
                    if (forward) PreFilter2(ref p[o], ref p[o + s]); else PostFilter2(ref p[o], ref p[o + s]);
                }
            for (int x = x0 + 1; x < x1 - 1; x += 2)
                foreach (int y in stackalloc int[] { y0, y1 - 1 })
                {
                    int o = y * s + x;
                    if (forward) PreFilter2(ref p[o], ref p[o + 1]); else PostFilter2(ref p[o], ref p[o + 1]);
                }
            if (!forward)
            {
                p[tl] += p[tl + 1];
                p[tr] += p[tr - 1];
                p[bl] += p[bl + 1];
                p[br] += p[br - 1];
            }
            else
            {
                p[br] += p[br - 1];
                p[bl] += p[bl + 1];
                p[tr] += p[tr - 1];
                p[tl] += p[tl + 1];
            }
        }
    }
}
