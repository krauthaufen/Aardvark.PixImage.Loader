using System;
using System.Collections.Generic;

namespace Aardvark.ImageCodecs
{
    /// <summary>
    /// The colour model of DNG 1.7.1 chapter 6: illuminant temperatures, interpolation of the calibrations by inverse
    /// correlated colour temperature, as-shot neutral / white point, camera to XYZ D50 (forward matrices or inverted colour
    /// matrices with linear Bradford adaptation) and XYZ D50 to linear sRGB.
    /// Matrices are row-major double arrays.
    /// </summary>
    internal static class DngColor
    {
        /// <summary>D50 as DNG and ICC use it, x-y.</summary>
        public static readonly (double X, double Y) D50 = (0.34567, 0.35850);
        public static readonly (double X, double Y) D65 = (0.31271, 0.32902);

        /// <summary>
        /// Correlated colour temperature of an EXIF LightSource code in kelvin, NaN when unknown. The CIE illuminants have their
        /// defined temperatures; for the generic EXIF names the middle of the range the EXIF / JIS fluorescent classes give.
        /// </summary>
        public static double IlluminantTemperature(int code) => code switch
        {
            1 => 5500,     // daylight
            2 => 4150,     // fluorescent (cool white class)
            3 => 2850,     // tungsten
            4 => 5500,     // flash
            9 => 5500,     // fine weather
            10 => 6500,    // cloudy
            11 => 7500,    // shade
            12 => 6400,    // daylight fluorescent (D 5700 - 7100 K)
            13 => 5000,    // day white fluorescent (N 4600 - 5500 K)
            14 => 4150,    // cool white fluorescent (W 3800 - 4500 K)
            15 => 3525,    // white fluorescent (WW 3250 - 3800 K)
            16 => 2925,    // warm white fluorescent (L 2600 - 3250 K)
            17 => 2856,    // standard illuminant A
            18 => 4874,    // B
            19 => 6774,    // C
            20 => 5503,    // D55
            21 => 6504,    // D65
            22 => 7504,    // D75
            23 => 5003,    // D50
            24 => 3200,    // ISO studio tungsten
            _ => double.NaN,
        };

        // ------------------------------------------------------------------------------------------ temperature

        /// <summary>
        /// Point of the Planckian locus in CIE 1960 (u, v) at temperature t (kelvin), from the rational approximation of
        /// Krystek (1985), valid from 1000 K to 15000 K.
        /// </summary>
        static (double U, double V) Planck(double t)
        {
            double u = (0.860117757 + 1.54118254e-4 * t + 1.28641212e-7 * t * t) / (1 + 8.42420235e-4 * t + 7.08145163e-7 * t * t);
            double v = (0.317398726 + 4.22806245e-5 * t + 4.20481691e-8 * t * t) / (1 - 2.89741816e-5 * t + 1.61456053e-7 * t * t);
            return (u, v);
        }

        /// <summary>
        /// Correlated colour temperature of an x-y chromaticity: the temperature of the closest point of the Planckian locus
        /// in CIE 1960 (u, v) (the definition of CCT), searched over 1000..15000 K in reciprocal temperature. Clamped to that range.
        /// </summary>
        public static double Temperature(double x, double y)
        {
            double den = -2 * x + 12 * y + 3;
            if (!(den > 0)) return double.NaN;
            double u = 4 * x / den, v = 6 * y / den;
            double Dist(double mired)
            {
                var p = Planck(1e6 / mired);
                return (p.U - u) * (p.U - u) + (p.V - v) * (p.V - v);
            }
            // coarse scan, then golden section around the best sample
            double lo = 1e6 / 15000, hi = 1e6 / 1000, best = lo, bd = double.MaxValue;
            const int N = 120;
            for (int i = 0; i <= N; i++)
            {
                double m = lo + (hi - lo) * i / N, d = Dist(m);
                if (d < bd) { bd = d; best = m; }
            }
            double step = (hi - lo) / N, a = Math.Max(lo, best - step), b = Math.Min(hi, best + step);
            double g = (Math.Sqrt(5) - 1) / 2, c = b - g * (b - a), e = a + g * (b - a), fc = Dist(c), fe = Dist(e);
            for (int i = 0; i < 60; i++)
            {
                if (fc < fe) { b = e; e = c; fe = fc; c = b - g * (b - a); fc = Dist(c); }
                else { a = c; c = e; fc = fe; e = a + g * (b - a); fe = Dist(e); }
            }
            return 1e6 / ((a + b) / 2);
        }

        // ------------------------------------------------------------------------------------------ matrices

        public static double[] Mul(double[] a, int ar, int ac, double[] b, int bc)
        {
            var r = new double[ar * bc];
            for (int i = 0; i < ar; i++)
                for (int j = 0; j < bc; j++)
                {
                    double s = 0;
                    for (int k = 0; k < ac; k++) s += a[i * ac + k] * b[k * bc + j];
                    r[i * bc + j] = s;
                }
            return r;
        }

        public static double[] Identity(int n)
        {
            var r = new double[n * n];
            for (int i = 0; i < n; i++) r[i * n + i] = 1;
            return r;
        }

        public static double[] Diagonal(double[] d)
        {
            int n = d.Length;
            var r = new double[n * n];
            for (int i = 0; i < n; i++) r[i * n + i] = d[i];
            return r;
        }

        public static double[] Transpose(double[] a, int r, int c)
        {
            var t = new double[r * c];
            for (int i = 0; i < r; i++)
                for (int j = 0; j < c; j++) t[j * r + i] = a[i * c + j];
            return t;
        }

        /// <summary>Inverse of an n x n matrix (Gauss-Jordan with partial pivoting).</summary>
        public static double[] Invert(double[] m, int n)
        {
            var a = (double[])m.Clone();
            var inv = Identity(n);
            for (int col = 0; col < n; col++)
            {
                int piv = col;
                for (int r = col + 1; r < n; r++) if (Math.Abs(a[r * n + col]) > Math.Abs(a[piv * n + col])) piv = r;
                if (Math.Abs(a[piv * n + col]) < 1e-12) throw new ImageFormatException("DNG: singular colour matrix");
                if (piv != col)
                    for (int k = 0; k < n; k++)
                    {
                        (a[col * n + k], a[piv * n + k]) = (a[piv * n + k], a[col * n + k]);
                        (inv[col * n + k], inv[piv * n + k]) = (inv[piv * n + k], inv[col * n + k]);
                    }
                double d = a[col * n + col];
                for (int k = 0; k < n; k++) { a[col * n + k] /= d; inv[col * n + k] /= d; }
                for (int r = 0; r < n; r++)
                {
                    if (r == col) continue;
                    double f = a[r * n + col];
                    if (f == 0) continue;
                    for (int k = 0; k < n; k++) { a[r * n + k] -= f * a[col * n + k]; inv[r * n + k] -= f * inv[col * n + k]; }
                }
            }
            return inv;
        }

        /// <summary>Moore-Penrose pseudo inverse of an r x c matrix of full column rank (c x r result): (AᵀA)⁻¹Aᵀ.</summary>
        public static double[] PseudoInverse(double[] a, int r, int c)
        {
            var t = Transpose(a, r, c);
            return Mul(Invert(Mul(t, c, r, a, c), c), c, c, t, r);
        }

        public static double[] Apply(double[] m, int r, int c, double[] v)
        {
            var o = new double[r];
            for (int i = 0; i < r; i++)
            {
                double s = 0;
                for (int k = 0; k < c; k++) s += m[i * c + k] * v[k];
                o[i] = s;
            }
            return o;
        }

        static double[] Blend(double[]? a, double[]? b, double w, int n)
        {
            if (a == null && b == null) return Identity(n);
            a ??= Identity(n); b ??= Identity(n);
            var r = new double[a.Length];
            for (int i = 0; i < r.Length; i++) r[i] = w * a[i] + (1 - w) * b[i];
            return r;
        }

        public static double[] XyToXyz((double X, double Y) xy) => new[] { xy.X / xy.Y, 1.0, (1 - xy.X - xy.Y) / xy.Y };

        static readonly double[] BradfordMa =
        {
            0.8951, 0.2664, -0.1614,
            -0.7502, 1.7135, 0.0367,
            0.0389, -0.0685, 1.0296,
        };

        /// <summary>Linear Bradford chromatic adaptation from white point <paramref name="src"/> to <paramref name="dst"/> (XYZ, 3 x 3).</summary>
        public static double[] Bradford((double X, double Y) src, (double X, double Y) dst)
        {
            var s = Apply(BradfordMa, 3, 3, XyToXyz(src));
            var d = Apply(BradfordMa, 3, 3, XyToXyz(dst));
            var scale = Diagonal(new[] { d[0] / s[0], d[1] / s[1], d[2] / s[2] });
            return Mul(Invert(BradfordMa, 3), 3, 3, Mul(scale, 3, 3, BradfordMa, 3), 3);
        }

        /// <summary>RGB to XYZ of a colour space given by its primaries and white point (both x-y).</summary>
        static double[] RgbToXyz((double X, double Y) r, (double X, double Y) g, (double X, double Y) b, (double X, double Y) w)
        {
            var p = Transpose(new[] { r.X / r.Y, 1, (1 - r.X - r.Y) / r.Y, g.X / g.Y, 1, (1 - g.X - g.Y) / g.Y, b.X / b.Y, 1, (1 - b.X - b.Y) / b.Y }, 3, 3);
            var s = Apply(Invert(p, 3), 3, 3, XyToXyz(w));
            return Mul(p, 3, 3, Diagonal(s), 3);
        }

        /// <summary>XYZ (D50) to linear sRGB (IEC 61966-2-1 primaries, D65 white), with Bradford adaptation from D50 to D65.</summary>
        public static readonly double[] XyzD50ToLinearSrgb =
            Invert(Mul(Bradford(D65, D50), 3, 3, RgbToXyz((0.64, 0.33), (0.30, 0.60), (0.15, 0.06), D65), 3), 3);

        /// <summary>Linear ProPhoto RGB (RIMM: ROMM primaries, D50 white) to XYZ D50, and back.</summary>
        public static readonly double[] ProPhotoToXyzD50 = RgbToXyz((0.7347, 0.2653), (0.1596, 0.8404), (0.0366, 0.0001), D50);
        public static readonly double[] XyzD50ToProPhoto = Invert(ProPhotoToXyzD50, 3);

        // ------------------------------------------------------------------------------------------ the DNG model

        /// <summary>The calibration matrices interpolated for one white balance (definitions of chapter 6).</summary>
        public sealed class Interpolated
        {
            public double[] CM = Array.Empty<double>();   // n x 3
            public double[] CC = Array.Empty<double>();   // n x n
            public double[]? RM, FM;                      // 3 x n
            public double[] AB = Array.Empty<double>();   // n x n
            public double[] XyzToCamera => Mul(Mul(AB, N, N, CC, N), N, N, CM, 3);
            public int N;
        }

        /// <summary>
        /// Weights of the calibrations for a white balance of temperature <paramref name="t"/>: with two calibrations linear
        /// interpolation in inverse temperature between them, the closer one outside of their range (DNG 1.2+ rule); with
        /// three the same rule between the two calibrations whose temperatures enclose t (sorted by temperature), as the
        /// specification leaves the triple-illuminant weighting to the reader. Calibrations of unknown temperature only count
        /// when no other is usable.
        /// </summary>
        public static double[] Weights(IReadOnlyList<DngCalibration> cals, double t)
        {
            var w = new double[cals.Count];
            var known = new List<int>();
            for (int i = 0; i < cals.Count; i++) if (double.IsFinite(cals[i].Temperature) && cals[i].Temperature > 0) known.Add(i);
            if (known.Count == 0 || !double.IsFinite(t)) { w[known.Count > 0 ? known[0] : 0] = 1; return w; }
            known.Sort((a, b) => cals[a].Temperature.CompareTo(cals[b].Temperature));
            if (t <= cals[known[0]].Temperature) { w[known[0]] = 1; return w; }
            if (t >= cals[known[^1]].Temperature) { w[known[^1]] = 1; return w; }
            for (int k = 0; k + 1 < known.Count; k++)
            {
                double t1 = cals[known[k]].Temperature, t2 = cals[known[k + 1]].Temperature;
                if (t >= t1 && t <= t2)
                {
                    double f = t2 == t1 ? 1 : (1 / t - 1 / t2) / (1 / t1 - 1 / t2);
                    w[known[k]] = f; w[known[k + 1]] = 1 - f;
                    return w;
                }
            }
            w[known[0]] = 1;
            return w;
        }

        static double[] Normalized(double[] cm, int n)
        {
            if (cm.Length != n * 3) return cm;
            var v = Apply(cm, n, 3, XyToXyz(D50));
            double max = 0;
            foreach (var x in v) max = Math.Max(max, x);
            if (!(max > 0)) return cm;
            var r = new double[cm.Length];
            for (int i = 0; i < r.Length; i++) r[i] = cm[i] / max;
            return r;
        }

        public static Interpolated Interpolate(DngInfo info, double t, bool useCameraCalibration)
        {
            int n = info.ColorPlanes;
            var cals = info.Calibrations;
            var w = Weights(cals, t);
            var r = new Interpolated { N = n };
            double[] Sum(Func<DngCalibration, double[]?> sel, int len, bool identity)
            {
                var acc = new double[len];
                for (int i = 0; i < cals.Count; i++)
                {
                    if (w[i] == 0) continue;
                    var m = sel(cals[i]) ?? (identity ? Identity(n) : null);
                    if (m == null || m.Length != len) m = identity ? Identity(n) : new double[len];
                    for (int k = 0; k < len; k++) acc[k] += w[i] * m[k];
                }
                return acc;
            }
            // colour matrices scaled so that D50 maps to a camera neutral whose largest component is 1: the scale of a
            // matrix is not part of its meaning, and writers that do not normalize would otherwise shift the exposure
            r.CM = Sum(c => Normalized(c.ColorMatrix, n), n * 3, false);
            r.CC = useCameraCalibration ? Sum(c => c.CameraCalibration, n * n, true) : Identity(n);
            bool allFm = cals.Count > 0, allRm = cals.Count > 0 && n > 3;
            for (int i = 0; i < cals.Count; i++)
            {
                if (w[i] == 0) continue;
                allFm &= cals[i].ForwardMatrix is { } f && f.Length == 3 * n;
                allRm &= cals[i].ReductionMatrix is { } m && m.Length == 3 * n;
            }
            if (allFm) r.FM = Sum(c => c.ForwardMatrix, 3 * n, false);
            if (allRm) r.RM = Sum(c => c.ReductionMatrix, 3 * n, false);
            var ab = new double[n];
            for (int i = 0; i < n; i++) ab[i] = info.AnalogBalance is { } a && i < a.Length && a[i] > 0 ? a[i] : 1;
            r.AB = Diagonal(ab);
            return r;
        }

        /// <summary>XYZ (Y = 1) to camera neutral and back: XYZ = inverse(XYZtoCamera) * neutral (pseudo inverse when n != 3).</summary>
        static double[] CameraToXyzRaw(Interpolated m)
        {
            int n = m.N;
            var x2c = m.XyzToCamera;   // n x 3
            return n == 3 ? Invert(x2c, 3) : PseudoInverse(x2c, n, 3);   // 3 x n
        }

        static (double X, double Y) ToXy(double[] xyz)
        {
            double s = xyz[0] + xyz[1] + xyz[2];
            return s > 0 ? (xyz[0] / s, xyz[1] / s) : D50;
        }

        /// <summary>
        /// The white point (x-y) of a camera neutral by the iteration of chapter 6: guess, interpolate the calibrations for the
        /// guess's temperature, map the neutral to XYZ, repeat until the x-y values converge.
        /// </summary>
        public static (double X, double Y) NeutralToXy(DngInfo info, double[] neutral, bool useCc)
        {
            var xy = D50;
            for (int it = 0; it < 50; it++)
            {
                var m = Interpolate(info, Temperature(xy.X, xy.Y), useCc);
                var nxy = ToXy(Apply(CameraToXyzRaw(m), 3, m.N, neutral));
                if (!double.IsFinite(nxy.X) || !double.IsFinite(nxy.Y) || nxy.Y <= 0) break;
                bool done = Math.Abs(nxy.X - xy.X) < 1e-7 && Math.Abs(nxy.Y - xy.Y) < 1e-7;
                xy = nxy;
                if (done) break;
            }
            return xy;
        }

        public static double[] XyToNeutral(DngInfo info, (double X, double Y) xy, bool useCc)
        {
            var m = Interpolate(info, Temperature(xy.X, xy.Y), useCc);
            return Apply(m.XyzToCamera, m.N, 3, XyToXyz(xy));
        }

        /// <summary>
        /// Camera space to XYZ D50 for the white balance given by <paramref name="neutral"/> / <paramref name="white"/>
        /// (3 x n): FM * D * inverse(AB * CC) with forward matrices, else CA * inverse(XYZtoCamera) (inverse(RM * XYZtoCamera) * RM,
        /// or the pseudo inverse, for n &gt; 3) with linear Bradford adaptation CA from the white point to D50.
        /// </summary>
        public static double[] CameraToXyzD50(DngInfo info, double[] neutral, (double X, double Y) white, bool useCc)
        {
            var m = Interpolate(info, Temperature(white.X, white.Y), useCc);
            int n = m.N;
            if (m.FM != null)
            {
                var abcc = Mul(m.AB, n, n, m.CC, n);
                var inv = Invert(abcc, n);
                var refNeutral = Apply(inv, n, n, neutral);
                var d = new double[n];
                for (int i = 0; i < n; i++) d[i] = refNeutral[i] != 0 ? 1 / refNeutral[i] : 1;
                return Mul(Mul(m.FM, 3, n, Diagonal(d), n), 3, n, inv, n);
            }
            double[] c2x;
            var x2c = m.XyzToCamera;
            if (n == 3) c2x = Invert(x2c, 3);
            else if (m.RM != null) c2x = Mul(Invert(Mul(m.RM, 3, n, x2c, 3), 3), 3, 3, m.RM, n);
            else c2x = PseudoInverse(x2c, n, 3);
            return Mul(Bradford(white, D50), 3, 3, c2x, n);
        }
    }
}
