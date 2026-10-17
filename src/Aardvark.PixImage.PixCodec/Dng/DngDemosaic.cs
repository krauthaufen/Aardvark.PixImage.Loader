using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Aardvark.ImageCodecs
{
    /// <summary>
    /// Demosaicing of colour filter array data (one float plane, white balanced) into full colour planes.
    /// <list type="bullet">
    /// <item>Bayer, <see cref="DngDemosaicAlgorithm.Directional"/>: directional filtering with a posteriori decision after
    /// D. Menon, S. Andriani, G. Calvagno, "Demosaicing With Directional Filtering and a Posteriori Decision", IEEE Trans.
    /// Image Processing 16(1), 2007 - green is interpolated horizontally and vertically with the 5-tap filter
    /// [-1/4, 1/2, 1/2, 1/2, -1/4], the direction is chosen per pixel by comparing the sums of the gradients of the two
    /// colour difference (G - R / G - B) estimates over a 5 x 5 window, red and blue follow as bilinearly interpolated colour
    /// differences (at red / blue pixels along the chosen direction). The paper's final refinement step is not done.</item>
    /// <item>Bayer, <see cref="DngDemosaicAlgorithm.Bilinear"/>: the mean of the nearest pixels of each colour.</item>
    /// <item>Other patterns (X-Trans 6 x 6, four colour CFAs): the most frequent plane ("green") as the inverse squared
    /// distance weighted mean of its nearest samples, the other planes as that plus the weighted mean of their colour
    /// differences to it within a 5 x 5 (at most 7 x 7) window; bilinear: weighted means of each plane directly.</item>
    /// </list>
    /// </summary>
    internal static class DngDemosaic
    {
        static int Reflect(int i, int n) => i < 0 ? Math.Min(-i, n - 1) : i >= n ? Math.Max(2 * (n - 1) - i, 0) : i;

        public static DngPlanes Run(float[] m, int w, int h, int[] cfa, int rr, int rc, int planes, DngDemosaicAlgorithm alg, ParallelOptions po)
        {
            bool bayer = rr == 2 && rc == 2 && planes == 3 && IsBayer(cfa);
            if (bayer && alg == DngDemosaicAlgorithm.Directional && w >= 8 && h >= 8) return Menon(m, w, h, cfa, po);
            return Generic(m, w, h, cfa, rr, rc, planes, alg == DngDemosaicAlgorithm.Directional, po);
        }

        static bool IsBayer(int[] p)
        {
            var set = new HashSet<int>(p);
            return set.Count == 3 && !set.Contains(-1) && (p[0] == p[3] ? p[1] != p[2] : p[1] == p[2]);
        }

        // ------------------------------------------------------------------------------------------ Menon et al. 2007

        static DngPlanes Menon(float[] m, int w, int h, int[] cfa, ParallelOptions po)
        {
            // the green plane is the one on the diagonal pair
            int green = cfa[0] == cfa[3] ? cfa[0] : cfa[1];
            var o = new DngPlanes(w, h, 3);
            var G = o.Data[green];
            var dir = new byte[(long)w * h];   // 0 horizontal, 1 vertical, 2 both
            bool IsG(int x, int y) => cfa[(y & 1) * 2 + (x & 1)] == green;
            float M(int x, int y) => m[(long)Reflect(y, h) * w + Reflect(x, w)];

            const int Band = 32;
            int bands = (h + Band - 1) / Band;
            Parallel.For(0, bands, po, b =>
            {
                int y0 = b * Band, y1 = Math.Min(h, y0 + Band);
                // rows y0-2 .. y1+2 of the directional estimates and colour differences (gradients need d(y+2))
                int r0 = y0 - 2, rows = y1 - y0 + 6;
                var gh = new float[rows * w]; var gv = new float[rows * w];
                var dh = new float[rows * w]; var dv = new float[rows * w];
                for (int r = 0; r < rows; r++)
                {
                    int y = Reflect(r0 + r, h);
                    for (int x = 0; x < w; x++)
                    {
                        float c = M(x, y);
                        float eh = 0.5f * (M(x - 1, y) + M(x + 1, y)) + 0.25f * (2 * c - M(x - 2, y) - M(x + 2, y));
                        float ev = 0.5f * (M(x, y - 1) + M(x, y + 1)) + 0.25f * (2 * c - M(x, y - 2) - M(x, y + 2));
                        int k = r * w + x;
                        if (IsG(x, y)) { gh[k] = gv[k] = c; dh[k] = c - eh; dv[k] = c - ev; }
                        else { gh[k] = eh; gv[k] = ev; dh[k] = eh - c; dv[k] = ev - c; }
                    }
                }
                // gradients of the colour differences (same colour sites two pixels apart), summed over 5 x 5
                int cr = y1 - y0 + 4;   // rows y0-2 .. y1+2
                var ch = new float[cr * w]; var cv = new float[cr * w];
                for (int r = 0; r < cr; r++)
                    for (int x = 0; x < w; x++)
                    {
                        int k = (r + 0) * w + x;   // estimate row index r (= y0-2+r)
                        ch[r * w + x] = Math.Abs(dh[k] - dh[r * w + Reflect(x + 2, w)]);
                        cv[r * w + x] = Math.Abs(dv[k] - dv[(r + 2) * w + x]);
                    }
                var sh = new float[w]; var sv = new float[w];
                for (int y = y0; y < y1; y++)
                {
                    int rc0 = y - y0;   // rows rc0 .. rc0+4 of ch / cv are y-2 .. y+2
                    for (int x = 0; x < w; x++)
                    {
                        float a = 0, c = 0;
                        for (int j = 0; j < 5; j++) { a += ch[(rc0 + j) * w + x]; c += cv[(rc0 + j) * w + x]; }
                        sh[x] = a; sv[x] = c;
                    }
                    int er = y - r0;   // estimate row of y
                    for (int x = 0; x < w; x++)
                    {
                        long k = (long)y * w + x;
                        if (IsG(x, y)) { G[k] = m[k]; dir[k] = 2; continue; }
                        float dH = 0, dV = 0;
                        for (int i = -2; i <= 2; i++) { int xx = Reflect(x + i, w); dH += sh[xx]; dV += sv[xx]; }
                        int e = er * w + x;
                        if (dV < dH) { G[k] = gv[e]; dir[k] = 1; }
                        else if (dH < dV) { G[k] = gh[e]; dir[k] = 0; }
                        else { G[k] = 0.5f * (gh[e] + gv[e]); dir[k] = 2; }
                    }
                }
            });

            int pa = green == 0 ? 1 : 0, pb = 3 - green - pa;   // the two other planes
            var A = o.Data[pa]; var B = o.Data[pb];
            float Gq(int x, int y) => G[(long)Reflect(y, h) * w + Reflect(x, w)];
            int Pl(int x, int y) => cfa[(y & 1) * 2 + (x & 1)];
            // the known samples, and red / blue at green pixels from the colour differences of their two neighbours
            Parallel.For(0, h, po, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    long k = (long)y * w + x;
                    int p = Pl(x, y);
                    if (p == pa) A[k] = m[k];
                    else if (p == pb) B[k] = m[k];
                    if (p != green) continue;
                    // the horizontal neighbours carry one of the two colours, the vertical ones the other
                    int hp = Pl(x + 1, y);
                    float dHor = 0.5f * (M(x - 1, y) - Gq(x - 1, y) + M(x + 1, y) - Gq(x + 1, y));
                    float dVer = 0.5f * (M(x, y - 1) - Gq(x, y - 1) + M(x, y + 1) - Gq(x, y + 1));
                    if (hp == pa) { A[k] = m[k] + dHor; B[k] = m[k] + dVer; }
                    else { B[k] = m[k] + dHor; A[k] = m[k] + dVer; }
                }
            });
            // the missing colour at red / blue pixels: colour differences of the neighbours along the chosen direction
            Parallel.For(0, h, po, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    long k = (long)y * w + x;
                    int p = Pl(x, y);
                    if (p == green) continue;
                    var T = p == pa ? B : A;
                    float Diff(int xx, int yy) { long q = (long)Reflect(yy, h) * w + Reflect(xx, w); return T[q] - G[q]; }
                    float d = dir[k] switch
                    {
                        0 => 0.5f * (Diff(x - 1, y) + Diff(x + 1, y)),
                        1 => 0.5f * (Diff(x, y - 1) + Diff(x, y + 1)),
                        _ => 0.25f * (Diff(x - 1, y) + Diff(x + 1, y) + Diff(x, y - 1) + Diff(x, y + 1)),
                    };
                    T[k] = G[k] + d;
                }
            });
            return o;
        }

        // ------------------------------------------------------------------------------------------ any pattern

        readonly struct Tap
        {
            public readonly int Dx, Dy;
            public readonly float W;
            public Tap(int dx, int dy, float w) { Dx = dx; Dy = dy; W = w; }
        }

        /// <summary>Same-plane neighbours of a pattern cell: the smallest window (radius 1..3) with at least <paramref name="min"/> of them, weights 1/d² normalized.</summary>
        static Tap[] Taps(int[] cfa, int rr, int rc, int cy, int cx, int plane, int min, bool nearestOnly)
        {
            for (int rad = 1; rad <= 3; rad++)
            {
                var list = new List<(int dx, int dy, double d2)>();
                for (int dy = -rad; dy <= rad; dy++)
                    for (int dx = -rad; dx <= rad; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int py = ((cy + dy) % rr + rr) % rr, px = ((cx + dx) % rc + rc) % rc;
                        if (cfa[py * rc + px] == plane) list.Add((dx, dy, dx * dx + dy * dy));
                    }
                if (list.Count < min && rad < 3) continue;
                if (list.Count == 0) return Array.Empty<Tap>();
                if (nearestOnly)
                {
                    double best = double.MaxValue;
                    foreach (var t in list) best = Math.Min(best, t.d2);
                    list.RemoveAll(t => t.d2 > best + 1e-9);
                }
                double sum = 0;
                foreach (var t in list) sum += 1 / t.d2;
                var r = new Tap[list.Count];
                for (int i = 0; i < r.Length; i++) r[i] = new Tap(list[i].dx, list[i].dy, (float)(1 / list[i].d2 / sum));
                return r;
            }
            return Array.Empty<Tap>();
        }

        static DngPlanes Generic(float[] m, int w, int h, int[] cfa, int rr, int rc, int planes, bool guided, ParallelOptions po)
        {
            var o = new DngPlanes(w, h, planes);
            var count = new int[planes];
            foreach (int p in cfa) if (p >= 0 && p < planes) count[p]++;
            int guide = 0;
            for (int p = 1; p < planes; p++) if (count[p] > count[guide]) guide = p;
            // taps per cell and plane
            var taps = new Tap[rr * rc][][];
            for (int cy = 0; cy < rr; cy++)
                for (int cx = 0; cx < rc; cx++)
                {
                    var t = taps[cy * rc + cx] = new Tap[planes][];
                    for (int p = 0; p < planes; p++)
                        t[p] = cfa[cy * rc + cx] == p ? Array.Empty<Tap>()
                            : !guided ? Taps(cfa, rr, rc, cy, cx, p, 2, true)
                            : p == guide ? Taps(cfa, rr, rc, cy, cx, p, 2, false) : Taps(cfa, rr, rc, cy, cx, p, 2, false);
                }
            float M(int x, int y) => m[(long)Reflect(y, h) * w + Reflect(x, w)];

            if (!guided)
            {
                Parallel.For(0, h, po, y =>
                {
                    for (int x = 0; x < w; x++)
                    {
                        int cell = (y % rr) * rc + x % rc, own = cfa[cell];
                        long k = (long)y * w + x;
                        for (int p = 0; p < planes; p++)
                        {
                            if (p == own) { o.Data[p][k] = m[k]; continue; }
                            float s = 0;
                            foreach (var t in taps[cell][p]) s += t.W * M(x + t.Dx, y + t.Dy);
                            o.Data[p][k] = s;
                        }
                    }
                });
                return o;
            }

            var G = o.Data[guide];
            Parallel.For(0, h, po, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    int cell = (y % rr) * rc + x % rc;
                    long k = (long)y * w + x;
                    if (cfa[cell] == guide) { G[k] = m[k]; continue; }
                    float s = 0;
                    foreach (var t in taps[cell][guide]) s += t.W * M(x + t.Dx, y + t.Dy);
                    G[k] = s;
                }
            });
            float Gq(int x, int y) => G[(long)Reflect(y, h) * w + Reflect(x, w)];
            Parallel.For(0, h, po, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    int cell = (y % rr) * rc + x % rc, own = cfa[cell];
                    long k = (long)y * w + x;
                    for (int p = 0; p < planes; p++)
                    {
                        if (p == guide) continue;
                        if (p == own) { o.Data[p][k] = m[k]; continue; }
                        float s = 0;
                        foreach (var t in taps[cell][p])
                        {
                            int xx = x + t.Dx, yy = y + t.Dy;
                            s += t.W * (M(xx, yy) - Gq(xx, yy));
                        }
                        o.Data[p][k] = G[k] + s;
                    }
                }
            });
            return o;
        }
    }
}
