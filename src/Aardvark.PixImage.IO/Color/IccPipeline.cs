using System;
using System.Collections.Generic;

namespace Aardvark.ImageCodecs
{
    /// <summary>
    /// One processing element of a colour transform. Stages work in place on a buffer of pixels with a fixed stride
    /// (the largest channel count of the pipeline), in single precision.
    /// </summary>
    internal abstract class IccStage
    {
        public int In { get; protected init; }
        public int Out { get; protected init; }
        public abstract void Apply(float[] buf, int count, int stride);
    }

    /// <summary>Per channel forward curves.</summary>
    internal sealed class CurvesStage : IccStage
    {
        public readonly IccCurve[] Curves;
        public CurvesStage(IccCurve[] curves) { Curves = curves; In = Out = curves.Length; }

        public bool IsIdentity { get { foreach (var c in Curves) if (!c.IsIdentity) return false; return true; } }

        public override void Apply(float[] buf, int count, int stride)
        {
            for (int ch = 0; ch < Curves.Length; ch++)
            {
                var c = Curves[ch];
                if (c.IsIdentity) continue;
                for (int i = 0, p = ch; i < count; i++, p += stride) buf[p] = c.Eval(buf[p]);
            }
        }
    }

    /// <summary>Per channel inverse curves (the output side of matrix / TRC profiles).</summary>
    internal sealed class InverseCurvesStage : IccStage
    {
        public readonly IccCurve[] Curves;
        public InverseCurvesStage(IccCurve[] curves) { Curves = curves; In = Out = curves.Length; }

        public override void Apply(float[] buf, int count, int stride)
        {
            for (int ch = 0; ch < Curves.Length; ch++)
            {
                var c = Curves[ch];
                if (c.IsIdentity) continue;
                double pg = c.PureGamma;
                if (!double.IsNaN(pg))
                {
                    float ig = (float)(1 / pg);
                    for (int i = 0, p = ch; i < count; i++, p += stride)
                    {
                        float v = buf[p];
                        buf[p] = v > 0 ? MathF.Pow(v, ig) : v < 0 ? -MathF.Pow(-v, ig) : 0;
                    }
                }
                else
                    for (int i = 0, p = ch; i < count; i++, p += stride) buf[p] = (float)c.EvaluateInverse(buf[p]);
            }
        }
    }

    /// <summary>out = M * in + offset (M has Out rows and In columns).</summary>
    internal sealed class AffineStage : IccStage
    {
        public readonly double[] M, Offset;
        readonly float[] m, o;

        public AffineStage(int inCh, int outCh, double[] matrix, double[]? offset = null)
        {
            if (matrix.Length != inCh * outCh) throw new ArgumentException("matrix size");
            In = inCh; Out = outCh; M = matrix; Offset = offset ?? new double[outCh];
            m = Array.ConvertAll(M, v => (float)v);
            o = Array.ConvertAll(Offset, v => (float)v);
        }

        public static AffineStage Diagonal(double[] scale, double[] offset)
        {
            int n = scale.Length;
            var m = new double[n * n];
            for (int i = 0; i < n; i++) m[i * n + i] = scale[i];
            return new AffineStage(n, n, m, offset);
        }

        public bool IsIdentity
        {
            get
            {
                if (In != Out) return false;
                for (int r = 0; r < Out; r++)
                {
                    if (Math.Abs(Offset[r]) > 1e-9) return false;
                    for (int c = 0; c < In; c++) if (Math.Abs(M[r * In + c] - (r == c ? 1 : 0)) > 1e-9) return false;
                }
                return true;
            }
        }

        /// <summary>The affine map "this after <paramref name="first"/>".</summary>
        public AffineStage After(AffineStage first)
        {
            int n = first.In, k = first.Out, mOut = Out;
            var r = new double[mOut * n];
            var off = new double[mOut];
            for (int i = 0; i < mOut; i++)
            {
                double s = Offset[i];
                for (int j = 0; j < k; j++) s += M[i * k + j] * first.Offset[j];
                off[i] = s;
                for (int c = 0; c < n; c++)
                {
                    double acc = 0;
                    for (int j = 0; j < k; j++) acc += M[i * k + j] * first.M[j * n + c];
                    r[i * n + c] = acc;
                }
            }
            return new AffineStage(n, mOut, r, off);
        }

        public override void Apply(float[] buf, int count, int stride)
        {
            int ni = In, no = Out;
            if (ni == 3 && no == 3)
            {
                float m0 = m[0], m1 = m[1], m2 = m[2], m3 = m[3], m4 = m[4], m5 = m[5], m6 = m[6], m7 = m[7], m8 = m[8];
                float o0 = o[0], o1 = o[1], o2 = o[2];
                for (int i = 0, p = 0; i < count; i++, p += stride)
                {
                    float x = buf[p], y = buf[p + 1], z = buf[p + 2];
                    buf[p] = m0 * x + m1 * y + m2 * z + o0;
                    buf[p + 1] = m3 * x + m4 * y + m5 * z + o1;
                    buf[p + 2] = m6 * x + m7 * y + m8 * z + o2;
                }
                return;
            }
            Span<float> t = stackalloc float[16];
            for (int i = 0, p = 0; i < count; i++, p += stride)
            {
                for (int c = 0; c < ni; c++) t[c] = buf[p + c];
                for (int r = 0; r < no; r++)
                {
                    float s = o[r];
                    for (int c = 0; c < ni; c++) s += m[r * ni + c] * t[c];
                    buf[p + r] = s;
                }
            }
        }
    }

    /// <summary>CIE Lab (L 0..100, a, b) to XYZ (D50 white, Y = 1).</summary>
    internal sealed class LabToXyzStage : IccStage
    {
        public LabToXyzStage() { In = Out = 3; }
        public override void Apply(float[] buf, int count, int stride)
        {
            for (int i = 0, p = 0; i < count; i++, p += stride)
            {
                var (x, y, z) = IccMath.LabToXyz(buf[p], buf[p + 1], buf[p + 2]);
                buf[p] = (float)x; buf[p + 1] = (float)y; buf[p + 2] = (float)z;
            }
        }
    }

    /// <summary>XYZ (D50 white, Y = 1) to CIE Lab.</summary>
    internal sealed class XyzToLabStage : IccStage
    {
        public XyzToLabStage() { In = Out = 3; }
        public override void Apply(float[] buf, int count, int stride)
        {
            for (int i = 0, p = 0; i < count; i++, p += stride)
            {
                var (l, a, b) = IccMath.XyzToLab(buf[p], buf[p + 1], buf[p + 2]);
                buf[p] = (float)l; buf[p + 1] = (float)a; buf[p + 2] = (float)b;
            }
        }
    }

    /// <summary>Clamps every channel to 0..1.</summary>
    internal sealed class ClampStage : IccStage
    {
        public ClampStage(int n) { In = Out = n; }
        public override void Apply(float[] buf, int count, int stride)
        {
            for (int i = 0, p = 0; i < count; i++, p += stride)
                for (int c = 0; c < In; c++)
                {
                    float v = buf[p + c];
                    buf[p + c] = v > 0 ? (v < 1 ? v : 1) : 0;
                }
        }
    }

    /// <summary>
    /// A multi-dimensional lookup table (inputs clamped to 0..1): tetrahedral interpolation for 3 inputs (or trilinear),
    /// bilinear for 2, linear for 1, and for more inputs linear interpolation along the first one between the
    /// interpolations of the remaining ones.
    /// </summary>
    internal sealed class ClutStage : IccStage
    {
        readonly int[] grid, strides;
        readonly float[] table;
        readonly bool trilinear;

        public ClutStage(int[] grid, int outCh, float[] table, bool trilinear = false)
        {
            In = grid.Length; Out = outCh;
            this.grid = grid; this.table = table; this.trilinear = trilinear;
            strides = new int[grid.Length];
            int s = outCh;
            for (int i = grid.Length - 1; i >= 0; i--) { strides[i] = s; s *= grid[i]; }
            if (s != table.Length) throw new ArgumentException("CLUT size");
        }

        public override void Apply(float[] buf, int count, int stride)
        {
            int ni = In, no = Out;
            Span<float> x = stackalloc float[16];
            Span<float> res = stackalloc float[16];
            Span<float> scratch = stackalloc float[16 * 2 * 16];
            for (int i = 0, p = 0; i < count; i++, p += stride)
            {
                for (int c = 0; c < ni; c++)
                {
                    float v = buf[p + c];
                    x[c] = v > 0 ? (v < 1 ? v : 1) : 0;
                }
                if (ni == 4 && !trilinear) Interp4(x, res);
                else Interp(0, 0, x, res, scratch);
                for (int c = 0; c < no; c++) buf[p + c] = res[c];
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        void Locate(int dim, float v, out int i0, out float f, out int step)
        {
            int g = grid[dim];
            if (g < 2) { i0 = 0; f = 0; step = 0; return; }
            float pos = v * (g - 1);
            i0 = (int)pos;
            if (i0 >= g - 1) i0 = g - 2;
            f = pos - i0;
            step = strides[dim];
        }

        /// <summary>The 6 tetrahedra of a cube: the corner offsets and weights for fractions fx, fy, fz.</summary>
        static void Tetra(float fx, float fy, float fz, int sx, int sy, int sz,
            out int d1, out int d2, out int d3, out float w1, out float w2, out float w3)
        {
            d3 = sx + sy + sz;
            if (fx >= fy)
            {
                if (fy >= fz) { d1 = sx; d2 = sx + sy; w1 = fx; w2 = fy; w3 = fz; }
                else if (fx >= fz) { d1 = sx; d2 = sx + sz; w1 = fx; w2 = fz; w3 = fy; }
                else { d1 = sz; d2 = sx + sz; w1 = fz; w2 = fx; w3 = fy; }
            }
            else
            {
                if (fx >= fz) { d1 = sy; d2 = sx + sy; w1 = fy; w2 = fx; w3 = fz; }
                else if (fy >= fz) { d1 = sy; d2 = sy + sz; w1 = fy; w2 = fz; w3 = fx; }
                else { d1 = sz; d2 = sy + sz; w1 = fz; w2 = fy; w3 = fx; }
            }
        }

        /// <summary>4 inputs (CMYK): tetrahedral in the last three, the same tetrahedron in both slices of the first, linear between them.</summary>
        void Interp4(ReadOnlySpan<float> x, Span<float> res)
        {
            Locate(0, x[0], out int ic, out float fc, out int sc);
            Locate(1, x[1], out int ix, out float fx, out int sx);
            Locate(2, x[2], out int iy, out float fy, out int sy);
            Locate(3, x[3], out int iz, out float fz, out int sz);
            int b = ic * strides[0] + ix * strides[1] + iy * strides[2] + iz * strides[3];
            Tetra(fx, fy, fz, sx, sy, sz, out int d1, out int d2, out int d3, out float w1, out float w2, out float w3);
            var t = table;
            for (int o = 0, no = Out; o < no; o++)
            {
                int q = b + o;
                float c0 = t[q], c1 = t[q + d1], c2 = t[q + d2], c3 = t[q + d3];
                float lo = c0 + w1 * (c1 - c0) + w2 * (c2 - c1) + w3 * (c3 - c2);
                if (fc == 0) { res[o] = lo; continue; }
                q += sc;
                c0 = t[q]; c1 = t[q + d1]; c2 = t[q + d2]; c3 = t[q + d3];
                float hi = c0 + w1 * (c1 - c0) + w2 * (c2 - c1) + w3 * (c3 - c2);
                res[o] = lo + (hi - lo) * fc;
            }
        }

        void Interp(int dim, int baseOff, ReadOnlySpan<float> x, Span<float> res, Span<float> scratch)
        {
            int rem = In - dim, no = Out;
            var t = table;
            if (rem == 3)
            {
                Locate(dim, x[dim], out int ix, out float fx, out int sx);
                Locate(dim + 1, x[dim + 1], out int iy, out float fy, out int sy);
                Locate(dim + 2, x[dim + 2], out int iz, out float fz, out int sz);
                int b = baseOff + ix * strides[dim] + iy * strides[dim + 1] + iz * strides[dim + 2];
                if (trilinear)
                {
                    for (int o = 0; o < no; o++)
                    {
                        int q = b + o;
                        float c00 = t[q] + (t[q + sx] - t[q]) * fx;
                        float c01 = t[q + sz] + (t[q + sx + sz] - t[q + sz]) * fx;
                        float c10 = t[q + sy] + (t[q + sx + sy] - t[q + sy]) * fx;
                        float c11 = t[q + sy + sz] + (t[q + sx + sy + sz] - t[q + sy + sz]) * fx;
                        float c0 = c00 + (c10 - c00) * fy, c1 = c01 + (c11 - c01) * fy;
                        res[o] = c0 + (c1 - c0) * fz;
                    }
                    return;
                }
                // the six tetrahedra of the cube, chosen by the order of the fractions
                Tetra(fx, fy, fz, sx, sy, sz, out int d1, out int d2, out int d3, out float w1, out float w2, out float w3);
                for (int o = 0; o < no; o++)
                {
                    int q = b + o;
                    float c0 = t[q], c1 = t[q + d1], c2 = t[q + d2], c3 = t[q + d3];
                    res[o] = c0 + w1 * (c1 - c0) + w2 * (c2 - c1) + w3 * (c3 - c2);
                }
                return;
            }
            if (rem == 2)
            {
                Locate(dim, x[dim], out int ix, out float fx, out int sx);
                Locate(dim + 1, x[dim + 1], out int iy, out float fy, out int sy);
                int b = baseOff + ix * strides[dim] + iy * strides[dim + 1];
                for (int o = 0; o < no; o++)
                {
                    int q = b + o;
                    float c0 = t[q] + (t[q + sx] - t[q]) * fx;
                    float c1 = t[q + sy] + (t[q + sx + sy] - t[q + sy]) * fx;
                    res[o] = c0 + (c1 - c0) * fy;
                }
                return;
            }
            if (rem == 1)
            {
                Locate(dim, x[dim], out int ix, out float fx, out int sx);
                int b = baseOff + ix * strides[dim];
                for (int o = 0; o < no; o++) res[o] = t[b + o] + (t[b + o + sx] - t[b + o]) * fx;
                return;
            }
            {
                Locate(dim, x[dim], out int ix, out float fx, out int sx);
                var lo = scratch.Slice(0, 16);
                var hi = scratch.Slice(16, 16);
                var rest = scratch.Slice(32);
                int b = baseOff + ix * strides[dim];
                Interp(dim + 1, b, x, lo, rest);
                if (sx == 0 || fx == 0) { lo.Slice(0, no).CopyTo(res); return; }
                Interp(dim + 1, b + sx, x, hi, rest);
                for (int o = 0; o < no; o++) res[o] = lo[o] + (hi[o] - lo[o]) * fx;
            }
        }
    }

    internal static class IccPipeline
    {
        /// <summary>Merges neighbouring affine stages and drops identities.</summary>
        public static List<IccStage> Optimize(List<IccStage> stages)
        {
            var r = new List<IccStage>();
            foreach (var s in stages)
            {
                if (s is CurvesStage cs && cs.IsIdentity) continue;
                if (s is AffineStage a && r.Count > 0 && r[^1] is AffineStage prev)
                {
                    r[^1] = a.After(prev);
                    continue;
                }
                r.Add(s);
            }
            r.RemoveAll(s => s is AffineStage a && a.IsIdentity);
            // a clamp right before a CLUT is implied by the CLUT
            for (int i = r.Count - 2; i >= 0; i--)
                if (r[i] is ClampStage && r[i + 1] is ClutStage) r.RemoveAt(i);
            return r;
        }

        public static int MaxChannels(List<IccStage> stages, int inCh)
        {
            int m = inCh;
            foreach (var s in stages) m = Math.Max(m, Math.Max(s.In, s.Out));
            return m;
        }

        /// <summary>Evaluates the stages for one pixel.</summary>
        public static float[] Eval(List<IccStage> stages, ReadOnlySpan<float> input)
        {
            int stride = Math.Max(MaxChannels(stages, input.Length), 1);
            var buf = new float[stride];
            input.CopyTo(buf);
            foreach (var s in stages) s.Apply(buf, 1, stride);
            int n = stages.Count > 0 ? stages[^1].Out : input.Length;
            return buf.AsSpan(0, n).ToArray();
        }
    }
}
