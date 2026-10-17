using System;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Threading.Tasks;

namespace Aardvark.ImageCodecs.JpegXrImpl
{
    /// <summary>
    /// The output formatting stage (T.832 9.10): sampling conversion, colour format conversion, bias, scaling, post-scaling,
    /// clipping and packing, windowing. The result is the decoder's reference output: per output array the integer values of
    /// ImagePlane after ClippingAndPackingStage( ) (bit patterns for BD16F / BD32F, packed words for BD5 / BD565 / BD10 RGB
    /// and BD1).
    /// </summary>
    internal static class JxrOutput
    {
        public sealed class Arrays
        {
            public int[][] A = Array.Empty<int[]>();
            public int[] W = Array.Empty<int>(), H = Array.Empty<int>();
        }

        /// <summary>The components of a plane by rows: block-major, or raster for chroma upsampled to the luma size.</summary>
        sealed class Src
        {
            readonly int[][] _a;
            readonly bool[] _raster;
            readonly int[] _w, _bw;

            public Src(JxrPlane p, bool upsample)
            {
                int n = p.Nc;
                _a = (int[][])p.Px.Clone();
                _raster = new bool[n];
                _w = (int[])p.PxW.Clone();
                _bw = (int[])p.DcW.Clone();
                if (upsample && p.Is42x)
                {
                    var ph = p.Ph;
                    for (int c = 1; c < 3; c++)
                    {
                        _a[c] = Upsample(JxrTransform.ToRaster(p.Px[c], p.PxW[c], p.PxH[c]), p.PxW[c], p.PxH[c], p.Ih.ExtWidth, p.Ih.ExtHeight,
                            p.Is420, ph.ChromaCenteringX, ph.ChromaCenteringY);
                        _raster[c] = true;
                        _w[c] = p.Ih.ExtWidth;
                    }
                }
            }

            public int Width(int c) => _w[c];

            /// <summary>Row y of component c (its whole width).</summary>
            public void Row(int c, int y, Span<int> dst)
            {
                if (_raster[c]) _a[c].AsSpan(y * _w[c], _w[c]).CopyTo(dst);
                else JxrTransform.ReadRow(_a[c], _bw[c], y, 0, _w[c], dst);
            }
        }

        static bool Packed(JxrOutputFormat f, JxrBitDepth bd) => f == JxrOutputFormat.Rgb && bd is JxrBitDepth.Bd5 or JxrBitDepth.Bd565 or JxrBitDepth.Bd10;

        /// <summary>Output arrays of a plane; the alpha plane is formatted as YONLY.</summary>
        public static Arrays Format(JxrPlane p, bool alphaPlane, bool parallel)
        {
            bool interleavedAlpha = alphaPlane && p.Ih.AlphaPlane && p.Ph.Format == JxrInternalFormat.YOnly && p.IsAlpha;
            var ih = p.Ih;
            var ph = p.Ph;
            var ofmt = alphaPlane ? JxrOutputFormat.YOnly : ih.OutputFormat;
            var bd = ih.BitDepth;
            var ifmt = p.Fmt;
            int nc = p.Nc;
            int ew = ih.ExtWidth, eh = ih.ExtHeight;
            int W = ih.Width, H = ih.Height;

            // sampling conversion (9.10.3): chroma to full size for RGB / YUV444 output
            var src = new Src(p, ofmt is JxrOutputFormat.Rgb or JxrOutputFormat.Yuv444);

            int arrays = Packed(ofmt, bd) ? 1
                : ofmt is JxrOutputFormat.Rgb or JxrOutputFormat.Yuv444 or JxrOutputFormat.Yuv422 or JxrOutputFormat.Yuv420 ? 3
                : ofmt == JxrOutputFormat.Rgbe ? 4 : nc;
            var res = new Arrays { A = new int[arrays][], W = new int[arrays], H = new int[arrays] };
            for (int i = 0; i < arrays; i++)
            {
                bool sub = i > 0 && ofmt is JxrOutputFormat.Yuv422 or JxrOutputFormat.Yuv420;
                int w = bd is JxrBitDepth.Bd1White1 or JxrBitDepth.Bd1Black1 ? (W - 1) / 8 + 1 : sub ? W / 2 : W;
                int h = i > 0 && ofmt == JxrOutputFormat.Yuv420 ? H / 2 : H;
                res.W[i] = w;
                res.H[i] = h;
                res.A[i] = new int[checked(w * h)];
            }

            int scale = ph.Scaled ? 3 : 0;
            int bias = bd switch
            {
                JxrBitDepth.Bd5 => 1 << 4,
                JxrBitDepth.Bd565 => 1 << 5,
                JxrBitDepth.Bd8 => 1 << 7,
                JxrBitDepth.Bd10 => 1 << 9,
                JxrBitDepth.Bd16 => 1 << 15,
                _ => 0,
            };
            if (bd is JxrBitDepth.Bd16 or JxrBitDepth.Bd16S or JxrBitDepth.Bd32S) bias >>= ph.ShiftBits;
            int round = 0;
            if (ph.Scaled)
                // an interleaved alpha plane is rounded like BD16 whatever the bit depth: what jxrlib does (9.10.6 says 3)
                round = bd is JxrBitDepth.Bd1White1 or JxrBitDepth.Bd1Black1 or JxrBitDepth.Bd16 || interleavedAlpha ? 4 : 3;

            int top = ih.TopMargin, left = ih.LeftMargin;
            var ctx = new Ctx(ih, ph, ofmt, bd, ifmt, nc, scale, bias, round);

            if (bd is JxrBitDepth.Bd1White1 or JxrBitDepth.Bd1Black1)
            {
                var y0 = new int[ew];
                var o = res.A[0];
                int ow = res.W[0];
                for (int y = 0; y < H; y++)
                {
                    src.Row(0, y + top, y0);
                    for (int x = 0; x < W; x++)
                    {
                        int v = Math.Clamp((y0[left + x] + round) >> scale, 0, 1);
                        if (bd == JxrBitDepth.Bd1Black1) v = 1 - v;
                        o[y * ow + (x >> 3)] |= v << (7 - (x & 7));
                    }
                }
                if (bd == JxrBitDepth.Bd1Black1)   // padding bits of the last byte stay 0 after the inversion, as Table 199 says
                    for (int y = 0; y < H; y++)
                        if ((W & 7) != 0) o[y * ow + ow - 1] &= 0xFF << (8 - (W & 7)) & 0xFF;
                return res;
            }

            if (ofmt is JxrOutputFormat.Yuv422 or JxrOutputFormat.Yuv420)
            {
                // subsampled output: the components as they are (zero chroma for YONLY coded data)
                for (int i = 0; i < 3; i++)
                {
                    int cw = res.W[i], ch = res.H[i];
                    var o = res.A[i];
                    if (i > 0 && ifmt == JxrInternalFormat.YOnly) { o.AsSpan().Fill(ctx.Single(0, i)); continue; }
                    int cl = i > 0 ? left / 2 : left, ct = i > 0 && ofmt == JxrOutputFormat.Yuv420 ? top / 2 : top;
                    var row = new int[src.Width(i)];
                    for (int y = 0; y < ch; y++)
                    {
                        src.Row(i, y + ct, row);
                        for (int x = 0; x < cw; x++) o[y * cw + x] = ctx.Single(row[x + cl], i);
                    }
                }
                return res;
            }

            void Rows(int r0, int r1)
            {
                Span<int> v = stackalloc int[Math.Max(nc, 4)];
                Span<int> outv = stackalloc int[Math.Max(nc, 4)];
                int n = ifmt == JxrInternalFormat.YOnly ? 1 : nc;
                var rows = new int[n][];
                for (int c = 0; c < n; c++) rows[c] = new int[ew];
                for (int y = r0; y < r1; y++)
                {
                    for (int c = 0; c < n; c++) src.Row(c, y + top, rows[c]);
                    for (int x = 0; x < W; x++)
                    {
                        for (int c = 0; c < n; c++) v[c] = rows[c][left + x];
                        ctx.Pixel(v, outv);
                        int di = y * W + x;
                        if (arrays == 1 && ctx.Packed) res.A[0][di] = outv[0];
                        else for (int i = 0; i < arrays; i++) res.A[i][di] = outv[i];
                    }
                }
            }
            if (parallel && H >= 256) Parallel.For(0, (H + 63) / 64, k => Rows(k * 64, Math.Min(H, k * 64 + 64)));
            else Rows(0, H);
            return res;
        }

        /// <summary>
        /// The common cases straight into the samples of the result: 8 / 16 bit RGB (from YUV444, or 4:2:2 / 4:2:0 chroma
        /// upsampled first) and Y only output (also the alpha plane), into channel <paramref name="c0"/> of
        /// <paramref name="dst"/> with <paramref name="ch"/> channels. False for anything else.
        /// </summary>
        public static bool FormatInto(JxrPlane p, bool alphaPlane, Array dst, int ch, int c0, bool parallel)
        {
            var ih = p.Ih;
            var ph = p.Ph;
            var ofmt = alphaPlane ? JxrOutputFormat.YOnly : ih.OutputFormat;
            var bd = ih.BitDepth;
            if (bd is not (JxrBitDepth.Bd8 or JxrBitDepth.Bd16)) return false;
            bool rgb = ofmt == JxrOutputFormat.Rgb && p.Fmt != JxrInternalFormat.YOnly;
            if (!rgb && !(ofmt == JxrOutputFormat.YOnly || ofmt == JxrOutputFormat.Rgb && p.Fmt == JxrInternalFormat.YOnly)) return false;
            bool gray3 = ofmt == JxrOutputFormat.Rgb && p.Fmt == JxrInternalFormat.YOnly;
            bool interleavedAlpha = alphaPlane && p.IsAlpha;
            int scale = ph.Scaled ? 3 : 0;
            int round = !ph.Scaled ? 0 : bd == JxrBitDepth.Bd16 || interleavedAlpha ? 4 : 3;
            int shift = bd == JxrBitDepth.Bd16 ? ph.ShiftBits : 0;
            int bias = (bd == JxrBitDepth.Bd8 ? 1 << 7 : (1 << 15) >> shift) << scale;
            int max = bd == JxrBitDepth.Bd8 ? 255 : 65535;
            int ew = ih.ExtWidth, eh = ih.ExtHeight, W = ih.Width, H = ih.Height, top = ih.TopMargin, left = ih.LeftMargin;
            var src = new Src(p, rgb);
            var b8 = dst as byte[];
            var b16 = bd == JxrBitDepth.Bd16 ? (ushort[])dst : null;
            int add = bias + round;
            void Rows(int r0, int r1)
            {
                var Y = new int[ew];
                var U = rgb ? new int[ew] : Y;
                var V = rgb ? new int[ew] : Y;
                Span<int> t4 = stackalloc int[12];
                for (int y = r0; y < r1; y++)
                {
                    int so = left;
                    long d = (long)y * W * ch + c0;
                    src.Row(0, y + top, Y);
                    if (rgb)
                    {
                        src.Row(1, y + top, U);
                        src.Row(2, y + top, V);
                        int x = 0;
                        if (Vector128.IsHardwareAccelerated)
                        {
                            var vadd = Vector128.Create(add);
                            var vmax = Vector128.Create(max);
                            var one = Vector128.Create(1);
                            ref int t0 = ref MemoryMarshal.GetReference(t4);
                            for (; x + 4 <= W; x += 4)
                            {
                                var yv = Vector128.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(Y), (nuint)(so + x));
                                var u = Vector128.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(U), (nuint)(so + x));
                                var v = Vector128.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(V), (nuint)(so + x));
                                var t = -u;
                                var g = yv - Vector128.ShiftRightArithmetic(t, 1);
                                var r = t + g - Vector128.ShiftRightArithmetic(v + one, 1);
                                var b = v + r;
                                r = Vector128.Min(Vector128.Max(Vector128.ShiftLeft(Vector128.ShiftRightArithmetic(r + vadd, scale), shift), Vector128<int>.Zero), vmax);
                                g = Vector128.Min(Vector128.Max(Vector128.ShiftLeft(Vector128.ShiftRightArithmetic(g + vadd, scale), shift), Vector128<int>.Zero), vmax);
                                b = Vector128.Min(Vector128.Max(Vector128.ShiftLeft(Vector128.ShiftRightArithmetic(b + vadd, scale), shift), Vector128<int>.Zero), vmax);
                                r.StoreUnsafe(ref t0, 0); g.StoreUnsafe(ref t0, 4); b.StoreUnsafe(ref t0, 8);
                                if (b8 != null)
                                    for (int k = 0; k < 4; k++, d += ch) { b8[d] = (byte)t4[k]; b8[d + 1] = (byte)t4[4 + k]; b8[d + 2] = (byte)t4[8 + k]; }
                                else
                                    for (int k = 0; k < 4; k++, d += ch) { b16![d] = (ushort)t4[k]; b16[d + 1] = (ushort)t4[4 + k]; b16[d + 2] = (ushort)t4[8 + k]; }
                            }
                        }
                        for (; x < W; x++, d += ch)
                        {
                            int yv = Y[so + x], u = U[so + x], v = V[so + x];
                            int t = -u;
                            int g = yv - (t >> 1);
                            int r = t + g - ((v + 1) >> 1);
                            int b = v + r;
                            r = Math.Clamp(((r + add) >> scale) << shift, 0, max);
                            g = Math.Clamp(((g + add) >> scale) << shift, 0, max);
                            b = Math.Clamp(((b + add) >> scale) << shift, 0, max);
                            if (b8 != null) { b8[d] = (byte)r; b8[d + 1] = (byte)g; b8[d + 2] = (byte)b; }
                            else { b16![d] = (ushort)r; b16[d + 1] = (ushort)g; b16[d + 2] = (ushort)b; }
                        }
                    }
                    else
                    {
                        for (int x = 0; x < W; x++, d += ch)
                        {
                            int v = Math.Clamp(((Y[so + x] + add) >> scale) << shift, 0, max);
                            if (b8 != null)
                            {
                                b8[d] = (byte)v;
                                if (gray3) { b8[d + 1] = (byte)v; b8[d + 2] = (byte)v; }
                            }
                            else
                            {
                                b16![d] = (ushort)v;
                                if (gray3) { b16[d + 1] = (ushort)v; b16[d + 2] = (ushort)v; }
                            }
                        }
                    }
                }
            }
            if (parallel && H >= 256) Parallel.For(0, (H + 63) / 64, k => Rows(k * 64, Math.Min(H, k * 64 + 64)));
            else Rows(0, H);
            return true;
        }

        /// <summary>Per pixel conversion state.</summary>
        sealed class Ctx
        {
            readonly JxrImageHeader _ih;
            readonly JxrPlaneHeader _ph;
            readonly JxrOutputFormat _of;
            readonly JxrBitDepth _bd;
            readonly JxrInternalFormat _if;
            readonly int _nc, _scale, _bias, _round;
            public readonly bool Packed;

            public Ctx(JxrImageHeader ih, JxrPlaneHeader ph, JxrOutputFormat of, JxrBitDepth bd, JxrInternalFormat ifmt, int nc, int scale, int bias, int round)
            {
                _ih = ih; _ph = ph; _of = of; _bd = bd; _if = ifmt; _nc = nc; _scale = scale; _bias = bias; _round = round;
                Packed = JxrOutput.Packed(of, bd);
            }

            /// <summary>AddBias, scaling, post-scaling and clipping of a component without colour conversion.</summary>
            public int Single(int v, int i)
            {
                v += _bias << _scale;
                v = (v + _round) >> _scale;
                return Post(v);
            }

            int Post(int v)
            {
                switch (_bd)
                {
                    case JxrBitDepth.Bd8: return Math.Clamp(v, 0, 255);
                    case JxrBitDepth.Bd16: return Math.Clamp(v << _ph.ShiftBits, 0, 65535);
                    case JxrBitDepth.Bd16S: return Math.Clamp(v << _ph.ShiftBits, -32768, 32767);
                    case JxrBitDepth.Bd32S: return v << _ph.ShiftBits;
                    case JxrBitDepth.Bd16F:
                    case JxrBitDepth.Bd32F: return PostScalingFl(v);
                    case JxrBitDepth.Bd10: return Math.Clamp(v, 0, 1023);
                    default: return v;
                }
            }

            /// <summary>PostScalingFl( ) (Table 192).</summary>
            int PostScalingFl(int x)
            {
                int s = x < 0 ? 1 : 0;
                if (_bd == JxrBitDepth.Bd16F) return (s << 15) | (int)Math.Min(Math.Abs((long)x), 32767);
                long ax = Math.Abs((long)x);
                int lm = _ph.LenMantissa;
                long e = ax >> lm;
                long m = (ax & ((1L << lm) - 1)) | (1L << lm);
                if (e == 0)
                {
                    m ^= 1L << lm;
                    e = 1;
                }
                e = e - _ph.ExpBias + 127;
                while (m < (1L << lm) && e > 1 && m > 0)
                {
                    e--;
                    m <<= 1;
                }
                if (m < (1L << lm)) e = 0;
                else m ^= 1L << lm;
                m <<= 23 - lm;
                return (int)((uint)s << 31 | (uint)(e << 23) | (uint)m);
            }

            /// <summary>Colour conversion, bias, scaling, post-scaling, clipping and packing of one pixel.</summary>
            public void Pixel(Span<int> v, Span<int> o)
            {
                int scale = _scale;
                switch (_of)
                {
                    case JxrOutputFormat.Rgb:
                    case JxrOutputFormat.Rgbe:
                        {
                            int r, g, b;
                            if (_if == JxrInternalFormat.YOnly) r = g = b = v[0];
                            else
                            {
                                int t = -v[1];
                                g = v[0] - (t >> 1);
                                r = t + g - ((v[2] + 1) >> 1);
                                b = v[2] + r;
                            }
                            // packed formats: the computed R goes into the least significant field (jxrlib, and NOTE 3 of
                            // A.7.18: with RED_BLUE_NOT_SWAPPED_FLAG 0 that field is blue); Table 185 would swap here
                            if (_of == JxrOutputFormat.Rgbe)
                            {
                                r = (r + _round) >> scale; g = (g + _round) >> scale; b = (b + _round) >> scale;
                                Rgbe(r, g, b, o);
                                return;
                            }
                            int bs = _bias << scale;
                            r += bs; g += bs; b += bs;
                            if (_bd == JxrBitDepth.Bd565)
                            {
                                r = Math.Clamp((r + _round) >> (scale + 1), 0, 31);
                                g = Math.Clamp((g + _round) >> scale, 0, 63);
                                b = Math.Clamp((b + _round) >> (scale + 1), 0, 31);
                                o[0] = r + (g << 5) + (b << 11);
                                return;
                            }
                            r = (r + _round) >> scale; g = (g + _round) >> scale; b = (b + _round) >> scale;
                            if (_bd == JxrBitDepth.Bd5)
                            {
                                o[0] = Math.Clamp(r, 0, 31) + (Math.Clamp(g, 0, 31) << 5) + (Math.Clamp(b, 0, 31) << 10);
                                return;
                            }
                            if (_bd == JxrBitDepth.Bd10)
                            {
                                o[0] = Math.Clamp(r, 0, 1023) + (Math.Clamp(g, 0, 1023) << 10) + (Math.Clamp(b, 0, 1023) << 20);
                                return;
                            }
                            o[0] = Post(r); o[1] = Post(g); o[2] = Post(b);
                            return;
                        }
                    case JxrOutputFormat.Cmyk:
                        {
                            int Y = v[0], U = v[1], V = v[2], K = v[3];
                            int k = K + (Y >> 1);
                            int m = k - Y - (U >> 1);
                            int c = U + m + (V >> 1);
                            int yy = c - V;
                            int bs = (_bias >> 1) << scale;
                            o[0] = Post((c + bs + _round) >> scale);
                            o[1] = Post((m + bs + _round) >> scale);
                            o[2] = Post((yy + bs + _round) >> scale);
                            o[3] = Post((k - bs + _round) >> scale);
                            return;
                        }
                    case JxrOutputFormat.CmykDirect:
                        {
                            int bs = _bias << scale;
                            o[0] = Post((v[1] + bs + _round) >> scale);   // c = U
                            o[1] = Post((v[2] + bs + _round) >> scale);   // m = V
                            o[2] = Post((v[3] + bs + _round) >> scale);   // y = K
                            o[3] = Post((v[0] + bs + _round) >> scale);   // k = Y
                            return;
                        }
                    case JxrOutputFormat.Yuv444:
                    case JxrOutputFormat.Yuv422:
                    case JxrOutputFormat.Yuv420:
                        {
                            int bs = _bias << scale;
                            o[0] = Post((v[0] + bs + _round) >> scale);
                            int u = _if == JxrInternalFormat.YOnly ? 0 : v[1], w = _if == JxrInternalFormat.YOnly ? 0 : v[2];
                            o[1] = Post((u + bs + _round) >> scale);
                            o[2] = Post((w + bs + _round) >> scale);
                            return;
                        }
                    default:   // YONLY, NCOMPONENT
                        {
                            int bs = _bias << scale;
                            int n = _of == JxrOutputFormat.YOnly ? 1 : _nc;
                            for (int c = 0; c < n; c++) o[c] = Post((v[c] + bs + _round) >> scale);
                            return;
                        }
                }
            }

            /// <summary>PostScalingF2( ) (Table 193): R, G, B, E bytes.</summary>
            static void Rgbe(int r, int g, int b, Span<int> o)
            {
                static (int m, int e) Part(int v)
                {
                    if (v <= 0) return (0, 0);
                    if ((v >> 7) > 1) return ((v & 0x7F) + 128, v >> 7);
                    return (v, 1);
                }
                var (mr, er) = Part(r);
                var (mg, eg) = Part(g);
                var (mb, eb) = Part(b);
                int e = Math.Max(er, Math.Max(eg, eb));
                if (e > er) mr = (2 * mr + 1) >> (e - er + 1);
                if (e > eg) mg = (2 * mg + 1) >> (e - eg + 1);
                if (e > eb) mb = (2 * mb + 1) >> (e - eb + 1);
                o[0] = mr; o[1] = mg; o[2] = mb; o[3] = e;
            }
        }

        static readonly int[][] UpsampleTaps =
        {
            new[] { 4, 4, 0, 8 }, new[] { 5, 3, 1, 7 }, new[] { 6, 2, 2, 6 }, new[] { 7, 1, 3, 5 }, new[] { 8, 0, 4, 4 },
        };

        /// <summary>Upsample( ) (Table 180) vertically (YUV420) and horizontally to the luma size.</summary>
        static int[] Upsample(int[] s, int sw, int sh, int ew, int eh, bool vertical, int cx, int cy)
        {
            int[] cur = s;
            int ch = sh;
            if (vertical)
            {
                var h = UpsampleTaps[cy <= 4 ? cy : 0];
                var o = new int[sw * eh];
                for (int x = 0; x < sw; x++)
                    for (int k = 0; k < sh; k++)
                    {
                        int prev = cur[Math.Max(0, k - 1) * sw + x], v = cur[k * sw + x], next = cur[Math.Min(sh - 1, k + 1) * sw + x];
                        o[2 * k * sw + x] = (h[2] * prev + h[3] * v + 4) >> 3;
                        o[(2 * k + 1) * sw + x] = (h[0] * v + h[1] * next + 4) >> 3;
                    }
                cur = o;
                ch = eh;
            }
            {
                var h = UpsampleTaps[cx <= 4 ? cx : 0];
                var o = new int[ew * ch];
                for (int y = 0; y < ch; y++)
                {
                    int r = y * sw, ro = y * ew;
                    for (int k = 0; k < sw; k++)
                    {
                        int prev = cur[r + Math.Max(0, k - 1)], v = cur[r + k], next = cur[r + Math.Min(sw - 1, k + 1)];
                        o[ro + 2 * k] = (h[2] * prev + h[3] * v + 4) >> 3;
                        o[ro + 2 * k + 1] = (h[0] * v + h[1] * next + 4) >> 3;
                    }
                }
                return o;
            }
        }
    }
}
