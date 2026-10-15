using System;

namespace Aardvark.ImageCodecs
{
    /// <summary>Colour science helpers: 3x3 matrices, Bradford adaptation, primaries to XYZ, CIE Lab.</summary>
    internal static class IccMath
    {
        public static double[] Identity3 => new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };

        /// <summary>Row-major 3x3 product a * b.</summary>
        public static double[] Mul(double[] a, double[] b)
        {
            var r = new double[9];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    r[i * 3 + j] = a[i * 3] * b[j] + a[i * 3 + 1] * b[3 + j] + a[i * 3 + 2] * b[6 + j];
            return r;
        }

        public static (double, double, double) Mul(double[] m, double x, double y, double z)
            => (m[0] * x + m[1] * y + m[2] * z, m[3] * x + m[4] * y + m[5] * z, m[6] * x + m[7] * y + m[8] * z);

        public static CieXyz Mul(double[] m, CieXyz v)
        {
            var (x, y, z) = Mul(m, v.X, v.Y, v.Z);
            return new CieXyz(x, y, z);
        }

        public static double Det(double[] m)
            => m[0] * (m[4] * m[8] - m[5] * m[7]) - m[1] * (m[3] * m[8] - m[5] * m[6]) + m[2] * (m[3] * m[7] - m[4] * m[6]);

        /// <summary>Inverse of a 3x3 matrix; null when it is (nearly) singular.</summary>
        public static double[]? Invert(double[] m)
        {
            double d = Det(m);
            if (!(Math.Abs(d) > 1e-12) || !double.IsFinite(d)) return null;
            double id = 1 / d;
            return new[]
            {
                (m[4] * m[8] - m[5] * m[7]) * id, (m[2] * m[7] - m[1] * m[8]) * id, (m[1] * m[5] - m[2] * m[4]) * id,
                (m[5] * m[6] - m[3] * m[8]) * id, (m[0] * m[8] - m[2] * m[6]) * id, (m[2] * m[3] - m[0] * m[5]) * id,
                (m[3] * m[7] - m[4] * m[6]) * id, (m[1] * m[6] - m[0] * m[7]) * id, (m[0] * m[4] - m[1] * m[3]) * id,
            };
        }

        static readonly double[] Bradford = { 0.8951, 0.2664, -0.1614, -0.7502, 1.7135, 0.0367, 0.0389, -0.0685, 1.0296 };
        static readonly double[] BradfordInv = Invert(Bradford)!;

        /// <summary>The linear Bradford chromatic adaptation matrix from white <paramref name="from"/> to white <paramref name="to"/>.</summary>
        public static double[] BradfordAdaptation(CieXyz from, CieXyz to)
        {
            var (fr, fg, fb) = Mul(Bradford, from.X, from.Y, from.Z);
            var (tr, tg, tb) = Mul(Bradford, to.X, to.Y, to.Z);
            var d = new double[] { tr / fr, 0, 0, 0, tg / fg, 0, 0, 0, tb / fb };
            return Mul(BradfordInv, Mul(d, Bradford));
        }

        /// <summary>The matrix from linear RGB to XYZ (columns: the XYZ of the primaries) for the given chromaticities, white Y = 1.</summary>
        public static double[] RgbToXyz(CieXy white, CieXy r, CieXy g, CieXy b)
        {
            var p = new double[]
            {
                r.X / r.Y, g.X / g.Y, b.X / b.Y,
                1, 1, 1,
                (1 - r.X - r.Y) / r.Y, (1 - g.X - g.Y) / g.Y, (1 - b.X - b.Y) / b.Y,
            };
            var pi = Invert(p) ?? throw new ArgumentException("the primaries are collinear");
            var w = white.ToXyz();
            var (sr, sg, sb) = Mul(pi, w.X, w.Y, w.Z);
            return new[] { p[0] * sr, p[1] * sg, p[2] * sb, p[3] * sr, p[4] * sg, p[5] * sb, p[6] * sr, p[7] * sg, p[8] * sb };
        }

        // CIE Lab relative to D50 (the PCS white)
        const double Eps = 216.0 / 24389, Kappa = 24389.0 / 27;
        public const double D50X = 0.9642, D50Y = 1.0, D50Z = 0.8249;

        public static double LabF(double t) => t > Eps ? Math.Cbrt(t) : (Kappa * t + 16) / 116;
        public static double LabFInv(double f) { double t3 = f * f * f; return t3 > Eps ? t3 : (116 * f - 16) / Kappa; }

        public static (double L, double a, double b) XyzToLab(double x, double y, double z)
        {
            double fx = LabF(x / D50X), fy = LabF(y / D50Y), fz = LabF(z / D50Z);
            return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
        }

        public static (double X, double Y, double Z) LabToXyz(double l, double a, double b)
        {
            double fy = (l + 16) / 116, fx = fy + a / 500, fz = fy - b / 200;
            return (LabFInv(fx) * D50X, LabFInv(fy) * D50Y, LabFInv(fz) * D50Z);
        }

        /// <summary>Rounds to the s15Fixed16Number grid of ICC files.</summary>
        public static double Q15(double v) => Math.Round(v * 65536) / 65536;

        public static CieXyz Q15(CieXyz v) => new(Q15(v.X), Q15(v.Y), Q15(v.Z));
    }
}
