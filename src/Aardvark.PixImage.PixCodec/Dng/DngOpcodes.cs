using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Aardvark.ImageCodecs
{
    /// <summary>An image as float planes (one array per colour plane, row-major), what the opcodes and the renderer work on.</summary>
    internal sealed class DngPlanes
    {
        public int Width, Height;
        public float[][] Data;

        public DngPlanes(int width, int height, int planes)
        {
            Width = width; Height = height;
            Data = new float[planes][];
            for (int p = 0; p < planes; p++) Data[p] = new float[(long)width * height];
        }

        public DngPlanes(int width, int height, float[][] data) { Width = width; Height = height; Data = data; }

        public int Planes => Data.Length;
    }

    /// <summary>
    /// The opcodes of DNG 1.7.1 chapter 7. Each opcode works on the image of its list (stored values for OpcodeList1, linear
    /// values for lists 2 and 3) and the result is clipped to the list's logical range afterwards.
    /// </summary>
    internal static class DngOpcodes
    {
        public const int WarpRectilinear = 1, WarpFisheye = 2, FixVignetteRadial = 3, FixBadPixelsConstant = 4, FixBadPixelsList = 5,
            TrimBounds = 6, MapTable = 7, MapPolynomial = 8, GainMap = 9, DeltaPerRow = 10, DeltaPerColumn = 11, ScalePerRow = 12,
            ScalePerColumn = 13, WarpRectilinear2 = 14;

        /// <summary>Highest DNG version whose opcodes this reader implements (1.7.1.0).</summary>
        const uint ReaderVersion = 0x01070100;

        static readonly string[] Names =
        {
            "Unknown", "WarpRectilinear", "WarpFisheye", "FixVignetteRadial", "FixBadPixelsConstant", "FixBadPixelsList", "TrimBounds",
            "MapTable", "MapPolynomial", "GainMap", "DeltaPerRow", "DeltaPerColumn", "ScalePerRow", "ScalePerColumn", "WarpRectilinear2",
        };

        public static string NameOf(int id) => id >= 1 && id < Names.Length ? Names[id] : "Unknown";

        public static bool IsSupported(int id) => id >= 1 && id <= 14;

        /// <summary>Big endian parameter reader.</summary>
        ref struct P
        {
            readonly ReadOnlySpan<byte> _b;
            int _p;
            public P(ReadOnlySpan<byte> b) { _b = b; _p = 0; }
            void Need(int n) { if (_p + n > _b.Length) throw new ImageFormatException("DNG: opcode parameters are truncated"); }
            public uint U32() { Need(4); var v = BinaryPrimitives.ReadUInt32BigEndian(_b.Slice(_p)); _p += 4; return v; }
            public int I32() => (int)Math.Min(U32(), int.MaxValue);
            public ushort U16() { Need(2); var v = BinaryPrimitives.ReadUInt16BigEndian(_b.Slice(_p)); _p += 2; return v; }
            public double F64() { Need(8); var v = BinaryPrimitives.ReadDoubleBigEndian(_b.Slice(_p)); _p += 8; return v; }
            public float F32() { Need(4); var v = BinaryPrimitives.ReadSingleBigEndian(_b.Slice(_p)); _p += 4; return v; }
        }

        /// <summary>
        /// Offset and size of the image after the TrimBounds opcodes of lists 2 and 3 (relative to the active area), for
        /// reporting the final size without decoding. Malformed parameters are ignored here (decoding reports them).
        /// </summary>
        public static (int X, int Y, int W, int H) TrimmedArea(int w, int h, params IReadOnlyList<DngOpcode>[] lists)
        {
            int x = 0, y = 0;
            foreach (var list in lists)
                foreach (var op in list)
                {
                    if (op.Id != TrimBounds || op.Parameters.Length < 16) continue;
                    var p = new P(op.Parameters);
                    int t = Math.Clamp(p.I32(), 0, h), l = Math.Clamp(p.I32(), 0, w);
                    int b = Math.Clamp(p.I32(), t, h), r = Math.Clamp(p.I32(), l, w);
                    if (b - t <= 0 || r - l <= 0) continue;
                    x += l; y += t; w = r - l; h = b - t;
                }
            return (x, y, w, h);
        }

        /// <summary>The default crop (relative to the active area) in the coordinates of an image that starts at <paramref name="trim"/>, clipped to it.</summary>
        public static (int X, int Y, int W, int H) CropRect(double cx, double cy, double cw, double ch, (int X, int Y) trim, int w, int h)
        {
            int x0 = (int)Math.Round(cx) - trim.X, y0 = (int)Math.Round(cy) - trim.Y;
            int x1 = x0 + (int)Math.Round(cw), y1 = y0 + (int)Math.Round(ch);
            x0 = Math.Clamp(x0, 0, w - 1); y0 = Math.Clamp(y0, 0, h - 1);
            x1 = Math.Clamp(x1, x0 + 1, w); y1 = Math.Clamp(y1, y0 + 1, h);
            return (x0, y0, x1 - x0, y1 - y0);
        }

        /// <summary>Per-pixel colour of a Bayer image for the bad pixel opcodes, from BayerPhase (0 red, 1 / 2 green, 3 blue at the top-left).</summary>
        static int BayerColour(int phase, int x, int y)
        {
            // phase 0: R G / G B, 1: G R / B G, 2: G B / R G, 3: B G / G R  (0 red, 1 green, 2 blue)
            int cell = (y & 1) * 2 + (x & 1);
            return phase switch
            {
                0 => cell switch { 0 => 0, 3 => 2, _ => 1 },
                1 => cell switch { 1 => 0, 2 => 2, _ => 1 },
                2 => cell switch { 1 => 2, 2 => 0, _ => 1 },
                _ => cell switch { 0 => 2, 3 => 0, _ => 1 },
            };
        }

        /// <summary>
        /// Applies an opcode list. <paramref name="range"/> is the logical maximum per plane (65535 or 2^32-1 for list 1, 1 for
        /// lists 2 and 3 - or what 1 became through white balancing), <paramref name="integer"/> marks list 1 (stored values).
        /// <paramref name="trim"/> accumulates the TrimBounds offsets. Unknown or too new opcodes that are not optional fail.
        /// </summary>
        public static DngPlanes Apply(IReadOnlyList<DngOpcode> list, DngPlanes img, float[] range, bool integer, bool cfa, ref (int X, int Y) trim)
        {
            bool skipWarp = false;
            foreach (var op in list)
            {
                bool skipNext = false;
                if (skipWarp && op.Id is WarpRectilinear or WarpFisheye) { skipWarp = false; continue; }
                skipWarp = false;
                if (!IsSupported(op.Id) || op.Version > ReaderVersion)
                {
                    if (op.Optional) continue;
                    throw new ImageNotSupportedException($"DNG: opcode {op.Id} ({op.Name}, version {op.Version >> 24}.{op.Version >> 16 & 255}.{op.Version >> 8 & 255}.{op.Version & 255}) is not supported and not optional");
                }
                switch (op.Id)
                {
                    case WarpRectilinear: case WarpRectilinear2: case WarpFisheye:
                        Warp(op, img);
                        skipNext = op.Id == WarpRectilinear2 && op.Optional;
                        break;
                    case FixVignetteRadial: Vignette(op, img); break;
                    case FixBadPixelsConstant: BadConstant(op, img, cfa); break;
                    case FixBadPixelsList: BadList(op, img, cfa); break;
                    case TrimBounds:
                    {
                        var p = new P(op.Parameters);
                        int t = p.I32(), l = p.I32(), b = p.I32(), r = p.I32();
                        t = Math.Clamp(t, 0, img.Height); b = Math.Clamp(b, t, img.Height); l = Math.Clamp(l, 0, img.Width); r = Math.Clamp(r, l, img.Width);
                        if (b - t <= 0 || r - l <= 0) throw new ImageFormatException("DNG: TrimBounds leaves an empty image");
                        if (t == 0 && l == 0 && b == img.Height && r == img.Width) break;
                        var o = new DngPlanes(r - l, b - t, img.Planes);
                        for (int pl = 0; pl < img.Planes; pl++)
                            for (int y = 0; y < o.Height; y++)
                                Array.Copy(img.Data[pl], (long)(y + t) * img.Width + l, o.Data[pl], (long)y * o.Width, o.Width);
                        img = o;
                        trim = (trim.X + l, trim.Y + t);
                        break;
                    }
                    default: AreaOp(op, img, range, integer); break;
                }
                Clip(img, range);
                skipWarp = skipNext;
            }
            return img;
        }

        static void Clip(DngPlanes img, float[] ranges)
        {
            for (int p = 0; p < img.Planes; p++)
            {
                var d = img.Data[p];
                float range = ranges[Math.Min(p, ranges.Length - 1)];
                Parallel.For(0, img.Height, y =>
                {
                    var s = d.AsSpan(y * img.Width, img.Width);
                    for (int i = 0; i < s.Length; i++) { float v = s[i]; s[i] = v < 0 ? 0 : v > range ? range : float.IsNaN(v) ? 0 : v; }
                });
            }
        }

        // ------------------------------------------------------------------------------------------ area opcodes

        static void AreaOp(DngOpcode op, DngPlanes img, float[] ranges, bool integer)
        {
            var p = new P(op.Parameters);
            int top = p.I32(), left = p.I32(), bottom = p.I32(), right = p.I32(), plane = p.I32(), planes = p.I32(), rowPitch = p.I32(), colPitch = p.I32();
            top = Math.Clamp(top, 0, img.Height); bottom = Math.Clamp(bottom, top, img.Height);
            left = Math.Clamp(left, 0, img.Width); right = Math.Clamp(right, left, img.Width);
            rowPitch = Math.Max(1, rowPitch); colPitch = Math.Max(1, colPitch);
            int p1 = Math.Min(img.Planes, plane + Math.Max(0, planes));
            if (plane >= p1 || top >= bottom || left >= right) return;
            int nRows = (bottom - top + rowPitch - 1) / rowPitch, nCols = (right - left + colPitch - 1) / colPitch;
            int w = img.Width, h = img.Height;
            switch (op.Id)
            {
                case MapTable:
                {
                    int size = p.I32();
                    if (size <= 0 || size > 65536) throw new ImageFormatException("DNG: MapTable size out of range");
                    var table = new float[size];
                    for (int i = 0; i < size; i++) table[i] = p.U16();
                    PerPixel(img, plane, p1, top, nRows, rowPitch, left, nCols, colPitch, (v, _, _, pl) =>
                    {
                        if (integer) return table[(int)Math.Clamp(MathF.Round(v), 0, size - 1)];
                        // linear values: the table maps the 16-bit encoding of 0..1
                        float range = ranges[Math.Min(pl, ranges.Length - 1)];
                        float x = Math.Clamp(v / range * 65535f, 0, size - 1);
                        int i0 = (int)x, i1 = Math.Min(i0 + 1, size - 1);
                        return (table[i0] + (table[i1] - table[i0]) * (x - i0)) / 65535f * range;
                    });
                    break;
                }
                case MapPolynomial:
                {
                    int degree = p.I32();
                    if (degree > 8) throw new ImageFormatException("DNG: MapPolynomial degree above 8");
                    var c = new double[degree + 1];
                    for (int i = 0; i <= degree; i++) c[i] = p.F64();
                    PerPixel(img, plane, p1, top, nRows, rowPitch, left, nCols, colPitch, (v, _, _, pl) =>
                    {
                        // evaluated on the normalized value (0..1 of the list's range)
                        float range = ranges[Math.Min(pl, ranges.Length - 1)];
                        double x = v / range, s = c[degree];
                        for (int i = degree - 1; i >= 0; i--) s = s * x + c[i];
                        return (float)(s * range);
                    });
                    break;
                }
                case GainMap:
                {
                    int mv = p.I32(), mh = p.I32();
                    double sv = p.F64(), sh = p.F64(), ov = p.F64(), oh = p.F64();
                    int mp = p.I32();
                    if (mv <= 0 || mh <= 0 || mp <= 0 || (long)mv * mh * mp > 1 << 24) throw new ImageFormatException("DNG: GainMap size out of range");
                    var g = new float[mv * mh * mp];
                    for (int i = 0; i < g.Length; i++) g[i] = p.F32();
                    PerPixel(img, plane, p1, top, nRows, rowPitch, left, nCols, colPitch, (v, x, y, pl) =>
                    {
                        // position of the pixel centre relative to the image (1 = its width / height), in map points
                        double fy = mv > 1 && sv != 0 ? ((y + 0.5) / h - ov) / sv : 0, fx = mh > 1 && sh != 0 ? ((x + 0.5) / w - oh) / sh : 0;
                        fy = Math.Clamp(fy, 0, mv - 1); fx = Math.Clamp(fx, 0, mh - 1);
                        int y0 = (int)fy, x0 = (int)fx, y1 = Math.Min(y0 + 1, mv - 1), x1 = Math.Min(x0 + 1, mh - 1);
                        double ty = fy - y0, tx = fx - x0;
                        int k = Math.Min(pl - plane, mp - 1);
                        double a = g[(y0 * mh + x0) * mp + k], b = g[(y0 * mh + x1) * mp + k], c0 = g[(y1 * mh + x0) * mp + k], d = g[(y1 * mh + x1) * mp + k];
                        double gain = (a + (b - a) * tx) * (1 - ty) + (c0 + (d - c0) * tx) * ty;
                        return (float)(v * gain);
                    });
                    break;
                }
                case DeltaPerRow: case ScalePerRow: case DeltaPerColumn: case ScalePerColumn:
                {
                    int count = p.I32();
                    bool rows = op.Id is DeltaPerRow or ScalePerRow;
                    if (count != (rows ? nRows : nCols)) throw new ImageFormatException($"DNG: {op.Name} has {count} values for {(rows ? nRows : nCols)} {(rows ? "rows" : "columns")}");
                    var vals = new float[count];
                    for (int i = 0; i < count; i++) vals[i] = p.F32();
                    bool scale = op.Id is ScalePerRow or ScalePerColumn;
                    PerPixel(img, plane, p1, top, nRows, rowPitch, left, nCols, colPitch, (v, x, y, pl) =>
                    {
                        float k = rows ? vals[(y - top) / rowPitch] : vals[(x - left) / colPitch];
                        return scale ? v * k : v + k * ranges[Math.Min(pl, ranges.Length - 1)];
                    });
                    break;
                }
            }
        }

        delegate float PixelFunc(float v, int x, int y, int plane);

        static void PerPixel(DngPlanes img, int p0, int p1, int top, int nRows, int rowPitch, int left, int nCols, int colPitch, PixelFunc f)
        {
            int w = img.Width;
            for (int pl = p0; pl < p1; pl++)
            {
                var d = img.Data[pl];
                int plane = pl;
                Parallel.For(0, nRows, r =>
                {
                    int y = top + r * rowPitch;
                    long row = (long)y * w;
                    for (int c = 0, x = left; c < nCols; c++, x += colPitch) d[row + x] = f(d[row + x], x, y, plane);
                });
            }
        }

        // ------------------------------------------------------------------------------------------ vignette, warps

        static (double cx, double cy, double m) Centre(double ncx, double ncy, int w, int h)
        {
            double x1 = w - 1, y1 = h - 1;
            double cx = ncx * x1, cy = ncy * y1;
            double mx = Math.Max(Math.Abs(cx), Math.Abs(x1 - cx)), my = Math.Max(Math.Abs(cy), Math.Abs(y1 - cy));
            return (cx, cy, Math.Sqrt(mx * mx + my * my));
        }

        static void Vignette(DngOpcode op, DngPlanes img)
        {
            var p = new P(op.Parameters);
            double k0 = p.F64(), k1 = p.F64(), k2 = p.F64(), k3 = p.F64(), k4 = p.F64(), ncx = p.F64(), ncy = p.F64();
            var (cx, cy, m) = Centre(ncx, ncy, img.Width, img.Height);
            if (m <= 0) return;
            int w = img.Width;
            Parallel.For(0, img.Height, y =>
            {
                double dy = (y - cy) / m;
                for (int x = 0; x < w; x++)
                {
                    double dx = (x - cx) / m, r2 = dx * dx + dy * dy;
                    float g = (float)(1 + r2 * (k0 + r2 * (k1 + r2 * (k2 + r2 * (k3 + r2 * k4)))));
                    foreach (var d in img.Data) d[(long)y * w + x] *= g;
                }
            });
        }

        /// <summary>Cubic convolution kernel (Keys, a = -0.5).</summary>
        static float Cubic(float t)
        {
            t = Math.Abs(t);
            return t < 1 ? (1.5f * t - 2.5f) * t * t + 1 : t < 2 ? ((-0.5f * t + 2.5f) * t - 4) * t + 2 : 0;
        }

        static float Sample(float[] src, int w, int h, double x, double y)
        {
            int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
            float fx = (float)(x - ix), fy = (float)(y - iy);
            float sum = 0;
            for (int j = -1; j <= 2; j++)
            {
                int yy = Math.Clamp(iy + j, 0, h - 1);
                float wy = Cubic(j - fy), row = 0;
                for (int i = -1; i <= 2; i++) row += src[(long)yy * w + Math.Clamp(ix + i, 0, w - 1)] * Cubic(i - fx);
                sum += row * wy;
            }
            return sum;
        }

        static void Warp(DngOpcode op, DngPlanes img)
        {
            var p = new P(op.Parameters);
            int n = p.I32();
            if (n <= 0 || n > 16) throw new ImageFormatException("DNG: warp opcode with an invalid number of coefficient sets");
            int radial = op.Id == WarpRectilinear2 ? 15 : 4;
            bool tangential = op.Id != WarpFisheye;
            var kr = new double[n][];
            var kt = new double[n][];
            var rng = new (double Min, double Max)[n];
            for (int i = 0; i < n; i++)
            {
                kr[i] = new double[radial];
                for (int k = 0; k < radial; k++) kr[i][k] = p.F64();
                kt[i] = tangential ? new[] { p.F64(), p.F64() } : new double[2];
                rng[i] = op.Id == WarpRectilinear2 ? (p.F64(), p.F64()) : (0, 1);
            }
            double ncx = p.F64(), ncy = p.F64();
            bool reciprocal = op.Id == WarpRectilinear2 && p.U32() != 0;
            int w = img.Width, h = img.Height;
            var (cx, cy, m) = Centre(ncx, ncy, w, h);
            if (m <= 0) return;
            for (int pl = 0; pl < img.Planes; pl++)
            {
                int set = n == 1 ? 0 : Math.Min(pl, n - 1);
                var r_ = kr[set]; var t_ = kt[set]; var (rmin, rmax) = rng[set];
                var src = (float[])img.Data[pl].Clone();
                var dst = img.Data[pl];
                Parallel.For(0, h, y =>
                {
                    double dy = (y - cy) / m;
                    for (int x = 0; x < w; x++)
                    {
                        double dx = (x - cx) / m, r2 = dx * dx + dy * dy, r = Math.Sqrt(r2);
                        double sx, sy;
                        if (op.Id == WarpFisheye)
                        {
                            double th = Math.Atan(r), t2 = th * th;
                            double f = r > 1e-12 ? th * (r_[0] + t2 * (r_[1] + t2 * (r_[2] + t2 * r_[3]))) / r : r_[0];
                            sx = f * dx; sy = f * dy;
                        }
                        else
                        {
                            double f;
                            if (op.Id == WarpRectilinear) f = r_[0] + r2 * (r_[1] + r2 * (r_[2] + r2 * r_[3]));
                            else
                            {
                                double rc = Math.Clamp(r, rmin, rmax), s = 0;
                                for (int k = radial - 1; k >= 0; k--) s = s * rc + r_[k];
                                f = reciprocal ? (s != 0 ? 1 / s : 1) : s;
                            }
                            sx = f * dx + t_[0] * 2 * dx * dy + t_[1] * (r2 + 2 * dx * dx);
                            sy = f * dy + t_[1] * 2 * dx * dy + t_[0] * (r2 + 2 * dy * dy);
                        }
                        dst[(long)y * w + x] = Sample(src, w, h, cx + m * sx, cy + m * sy);
                    }
                });
            }
        }

        // ------------------------------------------------------------------------------------------ bad pixels

        /// <summary>Replaces each bad pixel by the mean of the closest good pixels of its colour (rings of growing radius).</summary>
        static void Patch(DngPlanes img, bool cfa, int phase, List<(int X, int Y)> bad, Func<int, int, bool> isBad)
        {
            int w = img.Width, h = img.Height;
            foreach (var d in img.Data)
            {
                var src = (float[])d.Clone();
                Parallel.For(0, bad.Count, i =>
                {
                    var (x, y) = bad[i];
                    int col = cfa ? BayerColour(phase, x, y) : 0;
                    for (int rad = cfa && col != 1 ? 2 : 1; rad <= 16; rad++)
                    {
                        double sum = 0; int cnt = 0;
                        for (int dy = -rad; dy <= rad; dy++)
                            for (int dx = -rad; dx <= rad; dx++)
                            {
                                if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != rad) continue;
                                int xx = x + dx, yy = y + dy;
                                if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                                if (cfa && BayerColour(phase, xx, yy) != col) continue;
                                if (isBad(xx, yy)) continue;
                                sum += src[(long)yy * w + xx]; cnt++;
                            }
                        if (cnt > 0) { d[(long)y * w + x] = (float)(sum / cnt); break; }
                    }
                });
            }
        }

        static void BadConstant(DngOpcode op, DngPlanes img, bool cfa)
        {
            var p = new P(op.Parameters);
            float constant = p.U32();
            int phase = (int)(p.U32() & 3);
            cfa &= img.Planes == 1;
            int w = img.Width;
            var plane0 = img.Data[0];
            var bad = new List<(int, int)>();
            for (int y = 0; y < img.Height; y++)
                for (int x = 0; x < w; x++)
                    if (plane0[(long)y * w + x] == constant) bad.Add((x, y));
            if (bad.Count == 0) return;
            var src = (float[])plane0.Clone();
            Patch(img, cfa, phase, bad, (x, y) => src[(long)y * w + x] == constant);
        }

        static void BadList(DngOpcode op, DngPlanes img, bool cfa)
        {
            var p = new P(op.Parameters);
            int phase = (int)(p.U32() & 3);
            int points = p.I32(), rects = p.I32();
            cfa &= img.Planes == 1;
            int w = img.Width, h = img.Height;
            var mask = new HashSet<long>();
            var bad = new List<(int, int)>();
            void Add(int x, int y) { if (x >= 0 && y >= 0 && x < w && y < h && mask.Add((long)y * w + x)) bad.Add((x, y)); }
            for (int i = 0; i < points; i++) { int row = p.I32(), colm = p.I32(); Add(colm, row); }
            for (int i = 0; i < rects; i++)
            {
                int t = p.I32(), l = p.I32(), b = p.I32(), r = p.I32();
                for (int y = Math.Max(0, t); y < Math.Min(b, h); y++)
                    for (int x = Math.Max(0, l); x < Math.Min(r, w); x++) Add(x, y);
            }
            if (bad.Count > 0) Patch(img, cfa, phase, bad, (x, y) => mask.Contains((long)y * w + x));
        }
    }
}
