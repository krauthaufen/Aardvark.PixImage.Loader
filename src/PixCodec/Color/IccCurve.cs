using System;
using System.Runtime.CompilerServices;

namespace PixCodec
{
    /// <summary>
    /// A one dimensional ICC curve (curveType or parametricCurveType): the identity, a power law, one of the five parametric
    /// function types of the ICC specification, or a sampled table. Inputs and outputs are normalised to 0..1.
    /// </summary>
    public sealed class IccCurve
    {
        enum Kind : byte { Identity, Parametric, Table }

        readonly Kind kind;
        readonly int type;
        readonly double g, a = 1, b, c, d, e, f;
        readonly double[] parameters;
        readonly ushort[]? raw;
        readonly float[]? table;

        IccCurve() { kind = Kind.Identity; type = -1; parameters = Array.Empty<double>(); }

        IccCurve(int type, double[] p)
        {
            int n = ParameterCount(type);
            if (n < 0) throw new ArgumentOutOfRangeException(nameof(type), "parametric function types are 0..4");
            if (p.Length != n) throw new ArgumentException($"function type {type} takes {n} parameters");
            foreach (var v in p) if (!double.IsFinite(v)) throw new ArgumentException("curve parameters must be finite");
            kind = Kind.Parametric;
            this.type = type;
            parameters = (double[])p.Clone();
            g = p[0];
            if (type >= 1) { a = p[1]; b = p[2]; }
            if (type == 2) c = p[3];
            if (type >= 3) { c = p[3]; d = p[4]; }
            if (type == 4) { e = p[5]; f = p[6]; }
        }

        IccCurve(ushort[] values)
        {
            kind = Kind.Table;
            type = -1;
            parameters = Array.Empty<double>();
            raw = values;
            table = new float[values.Length];
            for (int i = 0; i < values.Length; i++) table[i] = values[i] / 65535f;
        }

        /// <summary>y = x.</summary>
        public static IccCurve Identity { get; } = new();

        /// <summary>y = x^gamma (function type 0).</summary>
        public static IccCurve Gamma(double gamma) => new(0, new[] { gamma });

        /// <summary>
        /// A parametric curve of ICC function type 0..4 with the parameters g, a, b, c, d, e, f as far as the type uses them:
        /// 0: x^g; 1: (ax+b)^g for x &gt;= -b/a, else 0; 2: (ax+b)^g + c for x &gt;= -b/a, else c; 3: (ax+b)^g for x &gt;= d,
        /// else cx; 4: (ax+b)^g + e for x &gt;= d, else cx + f.
        /// </summary>
        public static IccCurve Parametric(int functionType, params double[] parameters) => new(functionType, parameters);

        /// <summary>A sampled curve: entries spread evenly over 0..1, 65535 = 1, linearly interpolated.</summary>
        public static IccCurve Table(ReadOnlySpan<ushort> values)
        {
            if (values.Length < 2) throw new ArgumentException("a table needs at least 2 entries");
            return new IccCurve(values.ToArray());
        }

        /// <summary>The sRGB transfer function (IEC 61966-2-1) as function type 3.</summary>
        public static IccCurve Srgb { get; } = Parametric(3, 2.4, 1 / 1.055, 0.055 / 1.055, 1 / 12.92, 0.04045);

        /// <summary>Parametric function type 0..4, or -1 for the identity and tables.</summary>
        public int FunctionType => type;
        /// <summary>The parameters of a parametric curve (g, a, b, c, d, e, f as far as used).</summary>
        public ReadOnlySpan<double> Parameters => parameters;
        /// <summary>The entries of a sampled curve, empty otherwise.</summary>
        public ReadOnlySpan<ushort> TableValues => raw;
        public bool IsTable => kind == Kind.Table;

        /// <summary>True for y = x (also a gamma of 1 and the two entry table 0, 65535).</summary>
        public bool IsIdentity => kind == Kind.Identity
            || kind == Kind.Parametric && type == 0 && g == 1
            || kind == Kind.Table && raw!.Length == 2 && raw[0] == 0 && raw[1] == 65535;

        /// <summary>A pure power law (identity or function type 0): its exponent, else NaN.</summary>
        public double PureGamma => kind == Kind.Identity ? 1 : kind == Kind.Parametric && type == 0 ? g : double.NaN;

        static int ParameterCount(int type) => type switch { 0 => 1, 1 => 3, 2 => 4, 3 => 5, 4 => 7, _ => -1 };

        static double Pow(double v, double p) => v <= 0 ? 0 : Math.Pow(v, p);

        /// <summary>The curve value at <paramref name="x"/>. Tables clamp to 0..1; parametric curves are evaluated as given.</summary>
        public double Evaluate(double x)
        {
            switch (kind)
            {
                case Kind.Identity: return x;
                case Kind.Table:
                    {
                        var t = raw!;
                        if (!(x > 0)) return t[0] / 65535.0;
                        if (x >= 1) return t[^1] / 65535.0;
                        double p = x * (t.Length - 1);
                        int i = (int)p;
                        if (i >= t.Length - 1) return t[^1] / 65535.0;
                        double fr = p - i;
                        return (t[i] + (t[i + 1] - t[i]) * fr) / 65535.0;
                    }
            }
            switch (type)
            {
                case 0: return x < 0 ? -Pow(-x, g) : Pow(x, g);
                case 1: return a != 0 && x < -b / a ? 0 : Pow(a * x + b, g);
                case 2: return a != 0 && x < -b / a ? c : Pow(a * x + b, g) + c;
                case 3: return x >= d ? Pow(a * x + b, g) : c * x;
                default: return x >= d ? Pow(a * x + b, g) + e : c * x + f;
            }
        }

        /// <summary>Single precision evaluation for the per-pixel pipelines.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal float Eval(float x)
        {
            if (kind == Kind.Table)
            {
                var t = table!;
                if (!(x > 0)) return t[0];
                float p = x * (t.Length - 1);
                int i = (int)p;
                if (i >= t.Length - 1) return t[^1];
                float fr = p - i;
                return t[i] + (t[i + 1] - t[i]) * fr;
            }
            if (kind == Kind.Identity) return x;
            if (type == 0)
            {
                if (g == 1) return x;
                return x < 0 ? -MathF.Pow(-x, (float)g) : x > 0 ? MathF.Pow(x, (float)g) : 0;
            }
            return (float)Evaluate(x);
        }

        /// <summary>
        /// The inverse: an x with Evaluate(x) = <paramref name="y"/> (for curves that are not monotonic one of them; flat
        /// stretches of tables give their lowest x). Tables clamp to 0..1.
        /// </summary>
        public double EvaluateInverse(double y)
        {
            switch (kind)
            {
                case Kind.Identity: return y;
                case Kind.Table: return InverseTable(y);
            }
            switch (type)
            {
                case 0:
                    if (g == 0) return 0;
                    return y < 0 ? -Pow(-y, 1 / g) : Pow(y, 1 / g);
                case 1:
                    if (a == 0 || g == 0) return 0;
                    return y <= 0 ? -b / a : (Pow(y, 1 / g) - b) / a;
                case 2:
                    if (a == 0 || g == 0) return 0;
                    return y <= c ? -b / a : (Pow(y - c, 1 / g) - b) / a;
                case 3:
                    {
                        if (a == 0 || g == 0) return c != 0 ? y / c : 0;
                        double yd = Pow(a * d + b, g);
                        if (y >= yd) return (Pow(y, 1 / g) - b) / a;
                        return c != 0 ? Math.Min(y / c, d) : d;
                    }
                default:
                    {
                        if (a == 0 || g == 0) return c != 0 ? (y - f) / c : 0;
                        double yd = Pow(a * d + b, g) + e;
                        if (y >= yd) return (Pow(y - e, 1 / g) - b) / a;
                        return c != 0 ? Math.Min((y - f) / c, d) : d;
                    }
            }
        }

        double InverseTable(double y)
        {
            var t = raw!;
            int n = t.Length;
            double v = y * 65535;
            bool asc = t[n - 1] >= t[0];
            if (asc)
            {
                if (v <= t[0]) return 0;
                if (v >= t[n - 1]) return 1;
                // first i with t[i] >= v (the table may not be strictly monotonic: search linearly through flat parts)
                int lo = 0, hi = n - 1;
                while (lo < hi) { int m = (lo + hi) >> 1; if (t[m] >= v) hi = m; else lo = m + 1; }
                int i = lo;
                // for non-monotonic tables the binary search may land wrong; fix up locally
                while (i > 0 && t[i - 1] >= v) i--;
                if (i == 0) return 0;
                double t0 = t[i - 1], t1 = t[i];
                return (i - 1 + (t1 > t0 ? (v - t0) / (t1 - t0) : 0)) / (n - 1);
            }
            else
            {
                if (v >= t[0]) return 0;
                if (v <= t[n - 1]) return 1;
                int lo = 0, hi = n - 1;
                while (lo < hi) { int m = (lo + hi) >> 1; if (t[m] <= v) hi = m; else lo = m + 1; }
                int i = lo;
                while (i > 0 && t[i - 1] <= v) i--;
                if (i == 0) return 0;
                double t0 = t[i - 1], t1 = t[i];
                return (i - 1 + (t1 < t0 ? (t0 - v) / (t0 - t1) : 0)) / (n - 1);
            }
        }

        /// <summary>Whether the curve does not decrease anywhere on 0..1 (sampled).</summary>
        internal bool IsNonDecreasing()
        {
            if (kind == Kind.Identity) return true;
            if (kind == Kind.Table)
            {
                var t = raw!;
                for (int i = 1; i < t.Length; i++) if (t[i] < t[i - 1]) return false;
                return true;
            }
            double prev = Evaluate(0);
            for (int i = 1; i <= 4096; i++)
            {
                double v = Evaluate(i / 4096.0);
                if (!(v >= prev - 1e-12)) return false;
                prev = v;
            }
            return true;
        }

        /// <summary>Approximate equality of two curves on 0..1.</summary>
        internal bool Same(IccCurve o, double tol = 1e-6)
        {
            if (ReferenceEquals(this, o)) return true;
            for (int i = 0; i <= 256; i++)
                if (Math.Abs(Evaluate(i / 256.0) - o.Evaluate(i / 256.0)) > tol) return false;
            return true;
        }

        /// <summary>This curve with its parameters on the s15Fixed16 grid, as a profile stores it.</summary>
        internal IccCurve Quantized(bool v2)
        {
            if (kind != Kind.Parametric) return this;
            if (type == 0 && g == 1) return Identity;
            if (v2)
            {
                if (type == 0) return Gamma(Math.Round(g * 256) / 256);
                var tbl = new ushort[4096];
                for (int i = 0; i < tbl.Length; i++)
                    tbl[i] = (ushort)Math.Clamp(Math.Round(Evaluate(i / 4095.0) * 65535), 0, 65535);
                return new IccCurve(tbl);
            }
            var q = new double[parameters.Length];
            for (int i = 0; i < q.Length; i++) q[i] = IccMath.Q15(parameters[i]);
            return new IccCurve(type, q);
        }

        // -------------------------------------------------------------- serialisation

        /// <summary>Reads a curv or para element at <paramref name="pos"/>; <paramref name="length"/> receives its unpadded size.</summary>
        internal static IccCurve Read(ReadOnlySpan<byte> d, int pos, out int length)
        {
            uint sig = IccRead.U32(d, pos);
            if (sig == IccSig.TCurv)
            {
                uint n = IccRead.U32(d, pos + 8);
                if (n > (uint)(d.Length - pos - 12) / 2) throw IccRead.Bad("curve table is truncated");
                length = 12 + 2 * (int)n;
                if (n == 0) return Identity;
                if (n == 1)
                {
                    double gm = IccRead.U8F8(d, pos + 12);
                    return gm == 1 ? Identity : Gamma(gm);
                }
                var t = new ushort[n];
                for (int i = 0; i < t.Length; i++) t[i] = IccRead.U16(d, pos + 12 + 2 * i);
                return new IccCurve(t);
            }
            if (sig == IccSig.TPara)
            {
                int type = IccRead.U16(d, pos + 8);
                int np = ParameterCount(type);
                if (np < 0) throw IccRead.Bad($"unknown parametric curve type {type}");
                var p = new double[np];
                for (int i = 0; i < np; i++) p[i] = IccRead.S15(d, pos + 12 + 4 * i);
                length = 12 + 4 * np;
                return new IccCurve(type, p);
            }
            throw IccRead.Bad($"curve expected, found '{IccSig.ToText(sig)}'");
        }

        /// <summary>Writes the curve as para (v4, parametric) or curv.</summary>
        internal void Write(IccWrite w, bool v2)
        {
            if (kind == Kind.Parametric && !v2)
            {
                w.Sig(IccSig.TPara); w.U32(0); w.U16(type); w.U16(0);
                foreach (var p in parameters) w.S15(p);
                return;
            }
            w.Sig(IccSig.TCurv); w.U32(0);
            if (kind == Kind.Parametric && type == 0 && g < 256)
            {
                w.U32(1); w.U16((int)Math.Round(g * 256));
                return;
            }
            if (kind == Kind.Identity) { w.U32(0); return; }
            var t = raw ?? Sampled(4096);
            w.U32((uint)t.Length);
            foreach (var v in t) w.U16(v);
        }

        ushort[] Sampled(int n)
        {
            var t = new ushort[n];
            for (int i = 0; i < n; i++) t[i] = (ushort)Math.Clamp(Math.Round(Evaluate(i / (n - 1.0)) * 65535), 0, 65535);
            return t;
        }
    }
}
