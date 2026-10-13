using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;

namespace PixCodec
{
    /// <summary>Tag tree encoder (B.10.2). Leaf values are set first, <see cref="Finish"/> fills in the inner nodes.</summary>
    internal sealed class J2kTagTreeEncoder
    {
        readonly int[] _low, _val, _off, _lw, _lh;
        readonly bool[] _known;
        readonly int _levels;

        public J2kTagTreeEncoder(int w, int h)
        {
            int levels = 1;
            for (int a = w, b = h; a > 1 || b > 1; a = (a + 1) >> 1, b = (b + 1) >> 1) levels++;
            _levels = levels;
            _off = new int[levels]; _lw = new int[levels]; _lh = new int[levels];
            int n = 0;
            for (int k = 0; k < levels; k++) { _off[k] = n; _lw[k] = w; _lh[k] = h; n += w * h; w = (w + 1) >> 1; h = (h + 1) >> 1; }
            _low = new int[n]; _val = new int[n]; _known = new bool[n];
        }

        public void SetValue(int x, int y, int value) => _val[y * _lw[0] + x] = value;

        public void Finish()
        {
            for (int k = 1; k < _levels; k++)
                for (int y = 0; y < _lh[k]; y++)
                    for (int x = 0; x < _lw[k]; x++)
                    {
                        int m = int.MaxValue;
                        for (int dy = 0; dy < 2; dy++)
                            for (int dx = 0; dx < 2; dx++)
                            {
                                int cx = 2 * x + dx, cy = 2 * y + dy;
                                if (cx < _lw[k - 1] && cy < _lh[k - 1]) m = Math.Min(m, _val[_off[k - 1] + cy * _lw[k - 1] + cx]);
                            }
                        _val[_off[k] + y * _lw[k] + x] = m;
                    }
        }

        public void Encode(J2kPacketBits bw, int x, int y, int threshold)
        {
            int low = 0;
            for (int k = _levels - 1; k >= 0; k--)
            {
                int i = _off[k] + (y >> k) * _lw[k] + (x >> k);
                if (low > _low[i]) _low[i] = low; else low = _low[i];
                while (low < threshold)
                {
                    if (low >= _val[i])
                    {
                        if (!_known[i]) { bw.Bit(1); _known[i] = true; }
                        break;
                    }
                    bw.Bit(0);
                    low++;
                }
                _low[i] = low;
            }
        }
    }

    /// <summary>Packet header bit writer: MSB first, a 0 bit stuffed after a 0xFF byte.</summary>
    internal sealed class J2kPacketBits
    {
        public byte[] Buf = new byte[256];
        public int Pos;
        uint _cur;
        int _free = 8;

        public void Reset() { Pos = 0; _cur = 0; _free = 8; }

        public void Bit(int b)
        {
            _cur = _cur << 1 | (uint)b;
            if (--_free == 0)
            {
                if (Pos == Buf.Length) Array.Resize(ref Buf, Buf.Length * 2);
                Buf[Pos++] = (byte)_cur;
                _free = _cur == 0xFF ? 7 : 8;
                _cur = 0;
            }
        }

        public void Bits(int v, int n)
        {
            for (int i = n - 1; i >= 0; i--) Bit(v >> i & 1);
        }

        /// <summary>Pads to a byte boundary; a final 0xFF is followed by a zero byte.</summary>
        public void Flush()
        {
            int empty = Pos > 0 && Buf[Pos - 1] == 0xFF ? 7 : 8;
            if (Pos + 2 > Buf.Length) Array.Resize(ref Buf, Buf.Length * 2);
            if (_free != empty) Buf[Pos++] = (byte)(_cur << _free);
            if (Pos > 0 && Buf[Pos - 1] == 0xFF) Buf[Pos++] = 0;
            _cur = 0; _free = 8;
        }
    }

    /// <summary>JPEG 2000 encoder: wavelet, quantization, block coding, rate allocation and codestream assembly.</summary>
    internal sealed unsafe class J2kEncoder
    {
        readonly J2kPlanes _planes;
        readonly Jp2EncodeOptions _o;
        readonly J2kCodestream _cs = new J2kCodestream(null, 0);
        readonly bool _reversible, _ht, _mct, _parallel;
        readonly int _nc, _levels, _layers, _style;
        readonly bool[] _wide;
        readonly double[] _compNorm;
        J2kTile[] _tiles = Array.Empty<J2kTile>();
        readonly ConcurrentBag<J2kBlockCoder> _coders = new ConcurrentBag<J2kBlockCoder>();
        /// <summary>Test hook: code the HT cleanup pass one bit plane up and add SigProp and MagRef passes.</summary>
        internal static bool HtRefinementPasses;

        // ---------------- entry points ----------------

        public static void EncodeImage(RawImage image, Stream output, Jp2EncodeOptions o)
        {
            int n = image.Channels, w = image.Width, h = image.Height;
            int typeBits; bool signed;
            switch (image.SampleType)
            {
                case SampleType.UInt8: typeBits = 8; signed = false; break;
                case SampleType.Int8: typeBits = 8; signed = true; break;
                case SampleType.UInt16: typeBits = 16; signed = false; break;
                case SampleType.Int16: typeBits = 16; signed = true; break;
                case SampleType.UInt32: typeBits = 32; signed = false; break;
                case SampleType.Int32: typeBits = 32; signed = true; break;
                default: throw new ImageNotSupportedException($"JPEG 2000 cannot store {image.SampleType} samples");
            }
            int prec = o.BitsPerSample > 0 ? o.BitsPerSample : typeBits;
            if (prec < 1 || prec > typeBits) throw new ArgumentException("BitsPerSample does not fit the sample type");
            if (n > 16384) throw new ImageNotSupportedException("JPEG 2000 holds at most 16384 components");
            var planes = new J2kPlanes { Width = w, Height = h, Planes = new J2kPlane[n] };
            long count = (long)w * h;
            long lo = signed ? -(1L << (prec - 1)) : 0, hi = signed ? (1L << (prec - 1)) - 1 : (1L << prec) - 1;
            for (int c = 0; c < n; c++)
            {
                var d = new int[count];
                planes.Planes[c] = new J2kPlane { Width = w, Height = h, Precision = prec, Signed = signed, Data = d };
                bool bad = false;
                switch (image.SampleType)
                {
                    case SampleType.UInt8: { var s = (byte[])image.Data; for (long i = 0, j = c; i < count; i++, j += n) d[i] = s[j]; break; }
                    case SampleType.Int8: { var s = (sbyte[])image.Data; for (long i = 0, j = c; i < count; i++, j += n) d[i] = s[j]; break; }
                    case SampleType.UInt16: { var s = (ushort[])image.Data; for (long i = 0, j = c; i < count; i++, j += n) d[i] = s[j]; break; }
                    case SampleType.Int16: { var s = (short[])image.Data; for (long i = 0, j = c; i < count; i++, j += n) d[i] = s[j]; break; }
                    case SampleType.UInt32: { var s = (uint[])image.Data; for (long i = 0, j = c; i < count; i++, j += n) d[i] = (int)s[j]; break; }
                    default: { var s = (int[])image.Data; for (long i = 0, j = c; i < count; i++, j += n) d[i] = s[j]; break; }
                }
                if (prec < typeBits)
                {
                    if (signed) { for (long i = 0; i < count; i++) bad |= d[i] < lo || d[i] > hi; }
                    else for (long i = 0; i < count; i++) bad |= (uint)d[i] > (ulong)hi;
                    if (bad) throw new ArgumentException($"samples do not fit into {prec} bits (Jp2EncodeOptions.BitsPerSample)");
                }
            }
            bool rgb = image.Color == ColorModel.Rgb || image.Color == ColorModel.Rgba;
            var enc = new J2kEncoder(planes, o, rgb && o.ColorTransform);
            long extra = 0;
            int colours = image.Color == ColorModel.Gray || image.Color == ColorModel.GrayAlpha ? 1 : rgb ? 3 : n >= 3 ? 3 : 1;
            var meta = o.WriteMetadata ? image.Metadata : null;
            if (!o.RawCodestream)
            {
                // the boxes are small: measure them so that a rate target covers the whole file
                var probe = new MemoryStream();
                Jp2Header.WriteHeader(probe, w, h, new int[n], new bool[n], colours, image.HasAlpha, o.HighThroughput, meta, 0);
                extra = probe.Length;
            }
            var codestream = enc.Run(extra);
            if (!o.RawCodestream)
            {
                var depth = new int[n]; var sg = new bool[n];
                Array.Fill(depth, prec); Array.Fill(sg, signed);
                Jp2Header.WriteHeader(output, w, h, depth, sg, colours, image.HasAlpha, o.HighThroughput, meta, codestream.Length);
            }
            codestream.WriteTo(output);
        }

        public static byte[] EncodePlanes(J2kPlanes planes, Jp2EncodeOptions o)
        {
            if (planes.Planes.Length == 0) throw new ArgumentException("no components");
            bool mct = o.ColorTransform && planes.Planes.Length >= 3;
            if (mct)
            {
                var a = planes.Planes[0];
                for (int c = 1; c < 3; c++)
                {
                    var b = planes.Planes[c];
                    mct &= a.Width == b.Width && a.Height == b.Height && a.Dx == b.Dx && a.Dy == b.Dy && a.Precision == b.Precision && a.Signed == b.Signed;
                }
            }
            return new J2kEncoder(planes, o, mct).Run(0).ToArray();
        }

        static int Log2Exact(int v, string what)
        {
            if (v <= 0 || (v & (v - 1)) != 0) throw new ArgumentException(what + " must be a power of two");
            return BitOperations.Log2((uint)v);
        }

        J2kEncoder(J2kPlanes planes, Jp2EncodeOptions o, bool mct)
        {
            _planes = planes; _o = o;
            _nc = planes.Planes.Length;
            _reversible = o.Lossless; _ht = o.HighThroughput; _mct = mct; _parallel = o.Parallel;
            if (planes.Width <= 0 || planes.Height <= 0) throw new ArgumentException("empty image");
            var siz = _cs.Siz;
            siz.X1 = planes.Width; siz.Y1 = planes.Height;
            siz.TileW = o.TileWidth > 0 ? o.TileWidth : planes.Width;
            siz.TileH = o.TileHeight > 0 ? o.TileHeight : planes.Height;
            siz.NumComps = _nc;
            siz.Prec = new int[_nc]; siz.Signed = new bool[_nc]; siz.Dx = new int[_nc]; siz.Dy = new int[_nc];
            _wide = new bool[_nc];
            int minDim = int.MaxValue;
            for (int c = 0; c < _nc; c++)
            {
                var p = planes.Planes[c];
                if (p.Precision < 1 || p.Precision > 32 || p.Data == null) throw new ImageNotSupportedException("JPEG 2000 encoding handles 1 to 32 bit components");
                if (p.Dx < 1 || p.Dx > 255 || p.Dy < 1 || p.Dy > 255) throw new ArgumentException("bad subsampling factor");
                if (p.Width != (planes.Width + p.Dx - 1) / p.Dx || p.Height != (planes.Height + p.Dy - 1) / p.Dy)
                    throw new ArgumentException("component size does not match its subsampling");
                if (!_reversible && p.Precision > 16) throw new ImageNotSupportedException("irreversible JPEG 2000 encoding handles up to 16 bit components");
                siz.Prec[c] = p.Precision; siz.Signed[c] = p.Signed; siz.Dx[c] = p.Dx; siz.Dy[c] = p.Dy;
                siz.MaxPrec = Math.Max(siz.MaxPrec, p.Precision);
                _wide[c] = _reversible && p.Precision > 25;
                minDim = Math.Min(minDim, Math.Min((int)Math.Min(siz.TileW, planes.Width) / p.Dx, (int)Math.Min(siz.TileH, planes.Height) / p.Dy));
            }
            if (_mct && (_wide[0] || _wide[1] || _wide[2])) _wide[0] = _wide[1] = _wide[2] = true;
            long tx = (siz.X1 + siz.TileW - 1) / siz.TileW, ty = (siz.Y1 + siz.TileH - 1) / siz.TileH;
            if (tx * ty > 65535) throw new ArgumentException("more than 65535 tiles; use a larger tile size");
            siz.TilesX = (int)tx; siz.TilesY = (int)ty;
            siz.Rsiz = _ht ? 0x4000 : 0;

            int levels = Math.Clamp(o.Levels, 0, 32);
            while (levels > 0 && (1 << levels) > Math.Max(minDim, 1)) levels--;
            _levels = levels;
            int cbw = Log2Exact(o.CodeBlockWidth, "CodeBlockWidth"), cbh = Log2Exact(o.CodeBlockHeight, "CodeBlockHeight");
            if (cbw < 2 || cbw > 10 || cbh < 2 || cbh > 10 || cbw + cbh > 12) throw new ArgumentException("code-block size must be 4..1024 with at most 4096 samples");
            _style = _ht ? J2kCod.Ht | ((int)(o.BlockStyle & Jp2BlockStyle.VerticalCausal)) : (int)o.BlockStyle & 63;
            bool predOnly = (_style & J2kCod.PredTerm) != 0 && (_style & J2kCod.TermAll) == 0;
            _layers = _ht || predOnly ? 1 : Math.Clamp(o.Layers, 1, 65535);
            var cod = new J2kCod { Levels = levels, CbW = cbw, CbH = cbh, Style = _style, Reversible = _reversible, PPx = new int[levels + 1], PPy = new int[levels + 1] };
            for (int r = 0; r <= levels; r++)
            {
                cod.PPx[r] = cod.PPy[r] = 15;
                if (o.Precincts is { Length: > 0 } pp)
                {
                    var (pw, ph) = pp[Math.Min(levels - r, pp.Length - 1)];
                    cod.PPx[r] = Log2Exact(pw, "precinct width"); cod.PPy[r] = Log2Exact(ph, "precinct height");
                    if (cod.PPx[r] > 15 || cod.PPy[r] > 15 || (r > 0 && (cod.PPx[r] < 1 || cod.PPy[r] < 1))) throw new ArgumentException("bad precinct size");
                }
            }
            var main = new J2kParams(_nc) { HasCod = true, Order = (int)o.Progression, Layers = _layers, Mct = _mct ? 1 : 0, Cod = cod, Sop = o.PacketMarkers, Eph = o.PacketMarkers };
            if (main.Order < 0 || main.Order > 4) throw new ArgumentException("bad progression order");
            _cs.Main = main;
            _cs.Tiles = new J2kTileHeader?[siz.TilesX * siz.TilesY];

            // how much an error in a component shows after the inverse colour transform
            _compNorm = new double[_nc];
            Array.Fill(_compNorm, 1.0);
            if (_mct)
            {
                if (_reversible) { _compNorm[0] = Math.Sqrt(3); _compNorm[1] = _compNorm[2] = Math.Sqrt(0.6875); }
                else { _compNorm[0] = Math.Sqrt(3); _compNorm[1] = Math.Sqrt(0.34413 * 0.34413 + 1.772 * 1.772); _compNorm[2] = Math.Sqrt(1.402 * 1.402 + 0.71414 * 0.71414); }
            }
            for (int c = 0; c < _nc; c++) main.Qcc[c] = new J2kQcd { Style = _reversible ? 0 : 2, Guard = 2, Exp = new int[3 * levels + 1], Mant = new int[3 * levels + 1] };
            main.Qcd = main.Qcc[0];
        }

        static double BandNorm(bool reversible, int levels, int r, int orient)
        {
            var t = reversible ? J2kDwt.Norm53 : J2kDwt.Norm97;
            if (r == 0) return t[0][levels] * t[0][levels];
            int d = levels - r + 1;
            return t[orient & 1][d] * t[orient >> 1][d];
        }

        /// <summary>Sets the quantization parameters of all components for a base step size (irreversible) or none (reversible).</summary>
        void SetQuantization(double baseStep)
        {
            for (int c = 0; c < _nc; c++)
            {
                var q = _cs.Main.Qcc[c]!;
                int prec = _cs.Siz.Prec[c];
                for (int r = 0; r <= _levels; r++)
                    for (int b = 0; b < (r == 0 ? 1 : 3); b++)
                    {
                        int orient = r == 0 ? 0 : b + 1;
                        int i = r == 0 ? 0 : 3 * (r - 1) + orient;
                        int gain = orient == 0 ? 0 : orient == 3 ? 2 : 1;
                        if (_reversible)
                        {
                            // chroma of the reversible colour transform has one bit more
                            q.Exp[i] = Math.Min(31, prec + gain + (_mct && (c == 1 || c == 2) ? 1 : 0));
                            q.Mant[i] = 0;
                        }
                        else
                        {
                            double step = baseStep / (BandNorm(false, _levels, r, orient) * _compNorm[c]);
                            int rb = prec + gain;
                            int e = (int)Math.Floor(Math.Log2(step));
                            e = Math.Clamp(e, rb - 31, rb);
                            int mant = (int)Math.Round((step / Math.Pow(2, e) - 1) * 2048);
                            if (mant >= 2048) { if (e < rb) { e++; mant = 0; } else mant = 2047; }
                            if (mant < 0) mant = 0;
                            q.Exp[i] = rb - e; q.Mant[i] = mant;
                        }
                    }
            }
        }

        /// <summary>
        /// Quantizer step (for 8 bit data) of a quality setting. The scale follows the feel of JPEG's: on photographs
        /// 95 gives about 42 dB, 85 about 40 dB, 75 about 38 dB, 50 about 37 dB and 25 about 35 dB PSNR.
        /// </summary>
        static double QualityStep(int quality)
        {
            ReadOnlySpan<double> q = stackalloc double[] { 1, 10, 25, 50, 75, 85, 95, 100 };
            ReadOnlySpan<double> log2 = stackalloc double[] { 7.5, 6.8, 5.75, 4.9, 4.15, 3.65, 2.9, -0.5 };
            double x = Math.Clamp(quality, 1, 100);
            int i = 0;
            while (i < q.Length - 2 && x > q[i + 1]) i++;
            double t = (x - q[i]) / (q[i + 1] - q[i]);
            return Math.Pow(2, log2[i] + t * (log2[i + 1] - log2[i]));
        }

        J2kBlockCoder RentCoder() => _coders.TryTake(out var c) ? c : new J2kBlockCoder();

        // ---------------- pipeline ----------------

        MemoryStream Run(long extraBytes)
        {
            var siz = _cs.Siz;
            int nt = siz.TilesX * siz.TilesY;
            long pixels = (long)_planes.Width * _planes.Height;
            double targetBytes = !_reversible && _o.TargetBitsPerPixel > 0 ? _o.TargetBitsPerPixel * pixels / 8 - extraBytes : 0;
            bool pcrd = !_ht && ((_style & J2kCod.PredTerm) == 0 || (_style & J2kCod.TermAll) != 0);
            bool rateControl = pcrd && (targetBytes > 0 || _layers > 1);
            // without truncation the size is steered by the quantizer alone
            bool stepSearch = targetBytes > 0 && !pcrd;

            double range = Math.Pow(2, siz.MaxPrec);
            double baseStep;
            if (_reversible) baseStep = 1;
            else if (targetBytes > 0 && pcrd)
            {
                // fine enough for the wanted rate: the truncation does the rest
                double bps = _o.TargetBitsPerPixel / Math.Max(1, _nc >= 3 ? 1.5 : _nc);
                baseStep = range / 256.0 * Math.Clamp(Math.Pow(2, 1.0 - bps * 1.2), 1 / 64.0, 2.0);
            }
            else baseStep = range / 256.0 * QualityStep(_o.Quality);

            _tiles = new J2kTile[nt];
            MemoryStream result;
            if (!stepSearch)
            {
                SetQuantization(baseStep);
                ForTiles(t => { Transform(t); CodeBlocks(t, rateControl, true); });
                result = Assemble(rateControl, targetBytes);
            }
            else
            {
                // HT (or predictable termination): one pass per block, so search the step that meets the size
                ForTiles(Transform);
                double lo = 0, hi = 0, step = baseStep;
                result = null!;
                MemoryStream? best = null;
                for (int iter = 0; iter < 12; iter++)
                {
                    SetQuantization(step);
                    ForTiles(t => CodeBlocks(t, false, false));
                    var ms = Assemble(false, 0);
                    if (ms.Length <= targetBytes)
                    {
                        best = ms; hi = step;
                        if (ms.Length > targetBytes * 0.97) break;
                        step = lo > 0 ? Math.Sqrt(lo * hi) : step / (iter < 3 ? 2 : 1.4);
                    }
                    else
                    {
                        lo = step;
                        step = hi > 0 ? Math.Sqrt(lo * hi) : step * Math.Clamp(Math.Pow(ms.Length / targetBytes, 1.2), 1.3, 8);
                    }
                    if (lo > 0 && hi > 0 && hi / lo < 1.02 && best != null) break;
                    result = ms;
                }
                if (best != null) result = best;
            }
            return result;
        }

        void ForTiles(Action<int> body)
        {
            int nt = _cs.Siz.TilesX * _cs.Siz.TilesY;
            if (_parallel && nt > 1) Parallel.For(0, nt, body);
            else for (int t = 0; t < nt; t++) body(t);
        }

        /// <summary>Level shift, colour transform and wavelet analysis of a tile; the subbands stay in the level buffers.</summary>
        void Transform(int index)
        {
            var tile = J2kGeometry.BuildTile(_cs, index);
            _tiles[index] = tile;
            for (int c = 0; c < _nc; c++)
            {
                var tc = tile.Comps[c];
                tc.Wide = _wide[c];
                tc.OutRes = tc.Cod.Levels;
                J2kGeometry.SetupWindows(tc, tc.Res[tc.OutRes].Rect);
                var top = tc.Res[tc.OutRes];
                var plane = _planes.Planes[c];
                int w = top.W, h = top.H;
                long n = (long)w * h;
                long shift = tc.Signed ? 0 : 1L << (tc.Prec - 1);
                int x0 = (int)tc.Rect.X0, y0 = (int)tc.Rect.Y0;
                bool u32 = !tc.Signed && tc.Prec == 32;
                if (!_reversible)
                {
                    var buf = new float[n];
                    for (int y = 0; y < h; y++)
                    {
                        var src = plane.Data.AsSpan((y0 + y) * plane.Width + x0, w);
                        var dst = buf.AsSpan(y * w, w);
                        for (int x = 0; x < w; x++) dst[x] = src[x] - (int)shift;
                    }
                    top.Buffer = buf;
                }
                else if (tc.Wide)
                {
                    var buf = new long[n];
                    for (int y = 0; y < h; y++)
                    {
                        var src = plane.Data.AsSpan((y0 + y) * plane.Width + x0, w);
                        var dst = buf.AsSpan(y * w, w);
                        if (u32) for (int x = 0; x < w; x++) dst[x] = (uint)src[x] - shift;
                        else for (int x = 0; x < w; x++) dst[x] = src[x] - shift;
                    }
                    top.Buffer = buf;
                }
                else
                {
                    var buf = new int[n];
                    int s = (int)shift;
                    for (int y = 0; y < h; y++)
                    {
                        var src = plane.Data.AsSpan((y0 + y) * plane.Width + x0, w);
                        var dst = buf.AsSpan(y * w, w);
                        for (int x = 0; x < w; x++) dst[x] = src[x] - s;
                    }
                    top.Buffer = buf;
                }
            }
            if (_mct) ForwardMct(tile.Comps[0].Res[_levels].Buffer!, tile.Comps[1].Res[_levels].Buffer!, tile.Comps[2].Res[_levels].Buffer!);
            void Wavelet(int c)
            {
                var tc = tile.Comps[c];
                for (int r = tc.OutRes; r >= 1; r--) J2kDwt.Analyze(tc, r, _parallel);
            }
            if (_parallel && _nc > 1) Parallel.For(0, _nc, Wavelet);
            else for (int c = 0; c < _nc; c++) Wavelet(c);

            // precinct and code-block structure
            foreach (var tc in tile.Comps)
                for (int r = 0; r <= tc.OutRes; r++)
                {
                    var res = tc.Res[r];
                    int np = res.Pw * res.Ph;
                    res.Precincts = new J2kPrecinct?[np];
                    for (int k = 0; k < np; k++)
                    {
                        var prec = J2kGeometry.CreatePrecinct(res, r, k);
                        res.Precincts[k] = prec;
                        foreach (var pb in prec.Bands)
                        {
                            pb.Cw = (int)pb.CwLong; pb.Ch = (int)pb.ChLong;
                            pb.Enc = new J2kEncBlock[pb.Cw * pb.Ch];
                            for (int i = 0; i < pb.Enc.Length; i++) pb.Enc[i] = new J2kEncBlock();
                        }
                    }
                }
        }

        static void ForwardMct(Array b0, Array b1, Array b2)
        {
            if (b0 is int[] r && b1 is int[] g && b2 is int[] b)
            {
                for (int i = 0; i < r.Length; i++)
                {
                    int rr = r[i], gg = g[i], bb = b[i];
                    r[i] = (rr + 2 * gg + bb) >> 2; g[i] = bb - gg; b[i] = rr - gg;
                }
            }
            else if (b0 is long[] lr && b1 is long[] lg && b2 is long[] lb)
            {
                for (int i = 0; i < lr.Length; i++)
                {
                    long rr = lr[i], gg = lg[i], bb = lb[i];
                    lr[i] = (rr + 2 * gg + bb) >> 2; lg[i] = bb - gg; lb[i] = rr - gg;
                }
            }
            else if (b0 is float[] fr && b1 is float[] fg && b2 is float[] fb)
            {
                for (int i = 0; i < fr.Length; i++)
                {
                    float rr = fr[i], gg = fg[i], bb = fb[i];
                    fr[i] = 0.299f * rr + 0.587f * gg + 0.114f * bb;
                    fg[i] = -0.168736f * rr - 0.331264f * gg + 0.5f * bb;
                    fb[i] = 0.5f * rr - 0.418688f * gg - 0.081312f * bb;
                }
            }
        }

        struct Job { public J2kTileComp Tc; public J2kResolution Res; public J2kBand Band; public J2kPrecinctBand Pb; public int Block; public int R; }

        /// <summary>Quantizes and block-codes all code-blocks of a tile.</summary>
        void CodeBlocks(int index, bool rateControl, bool release)
        {
            var tile = _tiles[index];
            var jobs = new List<Job>();
            foreach (var tc in tile.Comps)
            {
                var q = _cs.Main.Qcc[tc.Index]!;
                for (int r = 0; r <= tc.OutRes; r++)
                {
                    var res = tc.Res[r];
                    for (int b = 0; b < res.Bands.Length; b++)
                    {
                        var band = res.Bands[b];
                        int qi = r == 0 ? 0 : 3 * (r - 1) + band.Orient;
                        int gain = band.Orient == 0 ? 0 : band.Orient == 3 ? 2 : 1;
                        band.Step = (float)((1.0 + q.Mant[qi] / 2048.0) * Math.Pow(2, tc.Prec + gain - q.Exp[qi]));
                    }
                    if (res.W == 0 || res.H == 0 || res.Precincts == null) continue;
                    foreach (var prec in res.Precincts)
                        for (int b = 0; b < prec!.Bands.Length; b++)
                        {
                            var pb = prec.Bands[b];
                            for (int i = 0; i < pb.Enc!.Length; i++)
                                jobs.Add(new Job { Tc = tc, Res = res, Band = res.Bands[b], Pb = pb, Block = i, R = r });
                        }
                }
            }
            if (_parallel && jobs.Count > 1)
                Parallel.For(0, jobs.Count, RentCoder, (i, _, coder) => { CodeBlock(jobs[i], coder, rateControl); return coder; }, c => _coders.Add(c));
            else
            {
                var coder = RentCoder();
                for (int i = 0; i < jobs.Count; i++) CodeBlock(jobs[i], coder, rateControl);
                _coders.Add(coder);
            }
            if (release)
                foreach (var tc in tile.Comps)
                    foreach (var res in tc.Res) res.Buffer = null;
        }

        void CodeBlock(in Job job, J2kBlockCoder coder, bool rateControl)
        {
            var res = job.Res; var band = job.Band; var pb = job.Pb; var tc = job.Tc;
            long gx = pb.Cbx0 + job.Block % pb.Cw, gy = pb.Cby0 + job.Block / pb.Cw;
            var rect = new J2kRect(gx << res.CbW, gy << res.CbH, (gx + 1) << res.CbW, (gy + 1) << res.CbH).Intersect(pb.Region);
            int w = (int)rect.W, h = (int)rect.H;
            var eb = pb.Enc![job.Block];
            eb.Included = 0; eb.Lblock = 3;
            int col = band.ColOff + (int)(rect.X0 - band.Win.X0);
            int stride = res.W;
            bool wide = tc.Wide;
            if (wide && (coder.Data64 == null || coder.Data64.Length < w * h)) coder.Data64 = new ulong[Math.Max(w * h, 4096)];
            if (coder.Data32.Length < w * h) coder.Data32 = new uint[w * h];
            double norm = BandNorm(_reversible, tc.Cod.Levels, job.R, band.Orient) * _compNorm[tc.Index];
            double unit = _reversible ? norm : norm * band.Step;
            eb.Weight = (float)(unit * unit);
            fixed (uint* d32 = coder.Data32)
            fixed (ulong* d64 = coder.Data64)
            {
                for (int y = 0; y < h; y++)
                {
                    long row = band.RowOff + (rect.Y0 + y - band.Win.Y0) * band.RowStep;
                    long si = row * stride + col;
                    switch (res.Buffer)
                    {
                        case int[] ib:
                        {
                            uint* dst = d32 + y * w;
                            fixed (int* src = &ib[si])
                                for (int x = 0; x < w; x++)
                                {
                                    int v = src[x];
                                    int s = v >> 31;
                                    dst[x] = (uint)((v ^ s) - s) | (uint)s << 31;
                                }
                            break;
                        }
                        case long[] lb:
                        {
                            ulong* dst = d64 + y * w;
                            for (int x = 0; x < w; x++)
                            {
                                long v = lb[si + x];
                                long s = v >> 63;
                                dst[x] = (ulong)((v ^ s) - s) | (ulong)s << 63;
                            }
                            break;
                        }
                        case float[] fb:
                        {
                            uint* dst = d32 + y * w;
                            float inv = 1f / band.Step;
                            fixed (float* src = &fb[si])
                                for (int x = 0; x < w; x++)
                                {
                                    float v = src[x];
                                    float a = MathF.Abs(v) * inv;
                                    uint m = a >= 1073741824f ? 0x3FFFFFFFu : (uint)a;
                                    dst[x] = m | (v < 0 && m != 0 ? 0x80000000u : 0u);
                                }
                            break;
                        }
                    }
                }
                if (_ht)
                {
                    bool ok = wide
                        ? J2kHtEncoder.Encode(coder, d64, w, h, HtRefinementPasses ? 1 : 0, HtRefinementPasses, (_style & J2kCod.Causal) != 0, eb)
                        : J2kHtEncoder.Encode(coder, d32, w, h, HtRefinementPasses ? 1 : 0, HtRefinementPasses, (_style & J2kCod.Causal) != 0, eb);
                    if (!ok) throw new ImageNotSupportedException("code-block too complex for the HT block coder; use smaller code-blocks");
                }
                else if (wide) coder.Encode(d64, w, h, band.Orient, _style, eb, rateControl);
                else coder.Encode(d32, w, h, band.Orient, _style, eb, rateControl);
            }
        }

        // ---------------- rate allocation ----------------

        IEnumerable<J2kEncBlock> AllBlocks()
        {
            foreach (var tile in _tiles)
                foreach (var tc in tile.Comps)
                    foreach (var res in tc.Res)
                    {
                        if (res.Precincts == null) continue;
                        foreach (var prec in res.Precincts)
                            foreach (var pb in prec!.Bands)
                                foreach (var eb in pb.Enc!) yield return eb;
                    }
        }

        /// <summary>Truncation points on the upper convex hull of a block's rate-distortion curve.</summary>
        static void BuildHull(J2kEncBlock b)
        {
            var hull = new List<int>();
            for (int k = 1; k <= b.Passes; k++)
            {
                double rk = b.PassLen[k - 1], dk = b.PassDist[k - 1];
                while (hull.Count > 0)
                {
                    int j = hull[^1];
                    double rj = b.PassLen[j - 1], dj = b.PassDist[j - 1];
                    if (dk <= dj) break;
                    double rp = hull.Count > 1 ? b.PassLen[hull[^2] - 1] : 0, dp = hull.Count > 1 ? b.PassDist[hull[^2] - 1] : 0;
                    // drop j if the step to k is at least as steep as the step into j
                    if ((dk - dj) * (rj - rp) >= (dj - dp) * (rk - rj)) hull.RemoveAt(hull.Count - 1);
                    else break;
                }
                if (hull.Count > 0 && dk <= b.PassDist[hull[^1] - 1]) continue;
                if (hull.Count == 0 && dk <= 0) continue;
                hull.Add(k);
            }
            b.Hull = hull.ToArray();
            b.Slope = new float[hull.Count];
            double pr = 0, pd = 0;
            for (int i = 0; i < hull.Count; i++)
            {
                double r = b.PassLen[hull[i] - 1], d = b.PassDist[hull[i] - 1];
                double slope = r > pr ? (d - pd) * b.Weight / (r - pr) : double.MaxValue;
                b.Slope[i] = (float)Math.Min(slope, float.MaxValue);
                pr = r; pd = d;
            }
        }

        /// <summary>
        /// Distributes the coding passes over the layers. Layer l gets the passes with the steepest distortion-rate
        /// slopes that fit into its byte budget; the last layer takes everything unless a size is asked for.
        /// </summary>
        void AllocateLayers(bool rateControl, double lastLayerBytes)
        {
            var blocks = new List<J2kEncBlock>(AllBlocks());
            foreach (var b in blocks) b.LayerPasses = new int[_layers];
            if (!rateControl)
            {
                foreach (var b in blocks) Array.Fill(b.LayerPasses, b.Passes);
                return;
            }
            long all = 0;
            foreach (var b in blocks) all += b.Passes > 0 ? b.PassLen[b.Passes - 1] : 0;
            // every hull step, steepest first
            var steps = new List<(float slope, int bytes)>();
            foreach (var b in blocks)
            {
                int prev = 0;
                for (int i = 0; i < b.Hull.Length; i++)
                {
                    int len = b.PassLen[b.Hull[i] - 1];
                    steps.Add((b.Slope[i], len - prev));
                    prev = len;
                }
            }
            steps.Sort((x, y) => y.slope.CompareTo(x.slope));
            bool everythingLast = lastLayerBytes <= 0;
            double last = everythingLast ? all : lastLayerBytes;
            for (int l = 0; l < _layers; l++)
            {
                if (l == _layers - 1 && everythingLast)
                {
                    foreach (var b in blocks) b.LayerPasses[l] = b.Passes;
                    break;
                }
                double budget = last * Math.Pow(2, l + 1 - _layers);
                float threshold = float.MaxValue;
                double sum = 0;
                bool cut = false;
                foreach (var s in steps)
                {
                    if (sum + s.bytes > budget) { cut = true; break; }
                    sum += s.bytes;
                    threshold = s.slope;
                }
                if (!cut) threshold = 0;
                foreach (var b in blocks)
                {
                    int passes = 0;
                    for (int i = 0; i < b.Hull.Length; i++)
                        if (b.Slope[i] >= threshold && threshold < float.MaxValue) passes = b.Hull[i]; else break;
                    if (!cut) passes = Math.Max(passes, b.Hull.Length > 0 ? b.Hull[^1] : 0);
                    b.LayerPasses[l] = Math.Max(passes, l > 0 ? b.LayerPasses[l - 1] : 0);
                }
            }
        }

        // ---------------- codestream assembly ----------------

        MemoryStream Assemble(bool rateControl, double targetBytes)
        {
            var siz = _cs.Siz;
            // guard bits: enough for the largest magnitude that occurs in each component
            for (int c = 0; c < _nc; c++)
            {
                var q = _cs.Main.Qcc[c]!;
                int guard = 1;
                foreach (var tile in _tiles)
                {
                    var tc = tile.Comps[c];
                    for (int r = 0; r <= tc.OutRes; r++)
                    {
                        var res = tc.Res[r];
                        if (res.Precincts == null) continue;
                        for (int b = 0; b < res.Bands.Length; b++)
                        {
                            int qi = r == 0 ? 0 : 3 * (r - 1) + res.Bands[b].Orient;
                            foreach (var prec in res.Precincts)
                                foreach (var eb in prec!.Bands[b].Enc!)
                                    guard = Math.Max(guard, eb.MagBits - q.Exp[qi] + 1);
                        }
                    }
                }
                if (guard > 7) throw new ImageNotSupportedException("sample values need more bit planes than JPEG 2000 can signal");
                q.Guard = guard;
                foreach (var tile in _tiles)
                {
                    var tc = tile.Comps[c];
                    for (int r = 0; r <= tc.OutRes; r++)
                        foreach (var band in tc.Res[r].Bands)
                            band.Mb = guard + q.Exp[r == 0 ? 0 : 3 * (r - 1) + band.Orient] - 1;
                }
            }

            var header = new MemoryStream();
            WriteMainHeader(header);
            int nt = _tiles.Length;
            int tlmBytes = _o.TileLengthMarkers ? ((nt + 10920) / 10921) * 6 + nt * 6 : 0;
            double budget = targetBytes > 0 ? targetBytes - header.Length - tlmBytes - 2 - nt * 14 : 0;
            if (rateControl) foreach (var b in AllBlocks()) BuildHull(b);

            var bodies = new byte[nt][];
            for (int attempt = 0; ; attempt++)
            {
                AllocateLayers(rateControl, budget);
                void One(int t) => bodies[t] = WriteTile(_tiles[t]);
                if (_parallel && nt > 1) Parallel.For(0, nt, One); else for (int t = 0; t < nt; t++) One(t);
                if (targetBytes <= 0 || !rateControl) break;
                long total = header.Length + tlmBytes + 2;
                foreach (var b in bodies) total += b.Length;
                // packet headers are not part of the allocation: take what they cost off the budget and redo
                if (total <= targetBytes || attempt >= 6 || budget <= 0) break;
                budget -= Math.Max(total - targetBytes, 1) * 1.05;
            }

            var ms = new MemoryStream();
            header.WriteTo(ms);
            Span<byte> buf = stackalloc byte[8];
            if (_o.TileLengthMarkers)
            {
                for (int first = 0; first < nt; first += 10921)
                {
                    int count = Math.Min(10921, nt - first);
                    BinaryPrimitives.WriteUInt16BigEndian(buf, J2kCodestream.TLM);
                    BinaryPrimitives.WriteUInt16BigEndian(buf.Slice(2), (ushort)(4 + 6 * count));
                    buf[4] = (byte)(first / 10921);
                    buf[5] = 0x60;   // 16 bit tile index, 32 bit length
                    ms.Write(buf.Slice(0, 6));
                    for (int t = first; t < first + count; t++)
                    {
                        BinaryPrimitives.WriteUInt16BigEndian(buf, (ushort)t);
                        BinaryPrimitives.WriteUInt32BigEndian(buf.Slice(2), (uint)bodies[t].Length);
                        ms.Write(buf.Slice(0, 6));
                    }
                }
            }
            foreach (var b in bodies) ms.Write(b);
            ms.WriteByte(0xFF); ms.WriteByte(0xD9);
            return ms;
        }

        void WriteMainHeader(MemoryStream ms)
        {
            var siz = _cs.Siz;
            var cod = _cs.Main.Cod!;
            void U16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
            void U32(long v) { U16((int)(v >> 16)); U16((int)(v & 0xFFFF)); }
            U16(J2kCodestream.SOC);
            U16(J2kCodestream.SIZ); U16(38 + 3 * _nc); U16(siz.Rsiz);
            U32(siz.X1); U32(siz.Y1); U32(0); U32(0); U32(siz.TileW); U32(siz.TileH); U32(0); U32(0);
            U16(_nc);
            for (int c = 0; c < _nc; c++)
            {
                ms.WriteByte((byte)(siz.Prec[c] - 1 | (siz.Signed[c] ? 0x80 : 0)));
                ms.WriteByte((byte)siz.Dx[c]); ms.WriteByte((byte)siz.Dy[c]);
            }
            if (_ht)
            {
                // largest cleanup magnitude decides the MAGB capability (T.814 A.3.7)
                int bits = 8;
                foreach (var b in AllBlocks()) bits = Math.Max(bits, b.MagBits);
                int p = bits <= 8 ? 0 : bits <= 27 ? bits - 8 : bits <= 71 ? 19 + (bits - 27 + 3) / 4 : 31;
                int ccap = (_reversible ? 0 : 0x20) | p;
                U16(J2kCodestream.CAP); U16(8); U32(0x00020000); U16(ccap);
            }
            bool precincts = false;
            for (int r = 0; r <= _levels; r++) precincts |= cod.PPx[r] != 15 || cod.PPy[r] != 15;
            U16(J2kCodestream.COD); U16(12 + (precincts ? _levels + 1 : 0));
            ms.WriteByte((byte)((precincts ? 1 : 0) | (_o.PacketMarkers ? 6 : 0)));
            ms.WriteByte((byte)_cs.Main.Order); U16(_layers); ms.WriteByte((byte)_cs.Main.Mct);
            ms.WriteByte((byte)_levels); ms.WriteByte((byte)(cod.CbW - 2)); ms.WriteByte((byte)(cod.CbH - 2));
            ms.WriteByte((byte)_style); ms.WriteByte((byte)(_reversible ? 1 : 0));
            if (precincts) for (int r = 0; r <= _levels; r++) ms.WriteByte((byte)(cod.PPx[r] | cod.PPy[r] << 4));

            void Quant(J2kQcd q)
            {
                ms.WriteByte((byte)(q.Style | q.Guard << 5));
                for (int i = 0; i < q.Exp.Length; i++)
                {
                    if (q.Style == 0) ms.WriteByte((byte)(q.Exp[i] << 3));
                    else U16(q.Exp[i] << 11 | q.Mant[i]);
                }
            }
            int qlen = 1 + (3 * _levels + 1) * (_reversible ? 1 : 2);
            var q0 = _cs.Main.Qcc[0]!;
            U16(J2kCodestream.QCD); U16(2 + qlen); Quant(q0);
            for (int c = 1; c < _nc; c++)
            {
                var q = _cs.Main.Qcc[c]!;
                if (q.Guard == q0.Guard && q.Exp.AsSpan().SequenceEqual(q0.Exp) && q.Mant.AsSpan().SequenceEqual(q0.Mant)) continue;
                int cw = _nc > 256 ? 2 : 1;
                U16(J2kCodestream.QCC); U16(2 + cw + qlen);
                if (cw == 2) U16(c); else ms.WriteByte((byte)c);
                Quant(q);
            }
            if (_planes.NltSignMagnitude != null)
                for (int c = 0; c < _nc; c++)
                    if (_planes.NltSignMagnitude[c])
                    {
                        // NLT, type 3: sign-magnitude to two's complement
                        U16(0xFF76); U16(6); U16(c); ms.WriteByte((byte)(siz.Prec[c] - 1 | (siz.Signed[c] ? 0x80 : 0))); ms.WriteByte(3);
                    }
        }

        /// <summary>One tile-part holding all packets of the tile, with SOT and SOD.</summary>
        byte[] WriteTile(J2kTile tile)
        {
            var ms = new MemoryStream();
            ms.Write(stackalloc byte[14]);   // SOT (12 bytes) and SOD, filled in below
            // tag trees
            foreach (var tc in tile.Comps)
                for (int r = 0; r <= tc.OutRes; r++)
                {
                    var res = tc.Res[r];
                    if (res.Precincts == null) continue;
                    foreach (var prec in res.Precincts)
                        for (int b = 0; b < prec!.Bands.Length; b++)
                        {
                            var pb = prec.Bands[b];
                            if (pb.Enc!.Length == 0) continue;
                            pb.InclEnc = new J2kTagTreeEncoder(pb.Cw, pb.Ch);
                            pb.ZbpEnc = new J2kTagTreeEncoder(pb.Cw, pb.Ch);
                            int mb = res.Bands[b].Mb;
                            for (int i = 0; i < pb.Enc.Length; i++)
                            {
                                var eb = pb.Enc[i];
                                eb.Included = 0; eb.Lblock = 3;
                                int first = _layers;
                                for (int l = 0; l < _layers; l++) if (eb.LayerPasses[l] > 0) { first = l; break; }
                                pb.InclEnc.SetValue(i % pb.Cw, i / pb.Cw, first);
                                pb.ZbpEnc.SetValue(i % pb.Cw, i / pb.Cw, first < _layers ? mb - eb.NumBps : 255);
                            }
                            pb.InclEnc.Finish(); pb.ZbpEnc.Finish();
                        }
                }
            var bits = new J2kPacketBits();
            var pieces = new List<(int passes, int len)>();
            var bodies = new List<(byte[] data, int start, int len)>();
            int sop = 0;
            var marker = new byte[6];
            bool Packet(int l, int r, int c, int k)
            {
                var res = tile.Comps[c].Res[r];
                var prec = res.Precincts![k]!;
                if (_o.PacketMarkers)
                {
                    marker[0] = 0xFF; marker[1] = 0x91; marker[2] = 0; marker[3] = 4; marker[4] = (byte)(sop >> 8); marker[5] = (byte)sop;
                    ms.Write(marker);
                    sop = (sop + 1) & 0xFFFF;
                }
                bits.Reset();
                bodies.Clear();
                bool any = false;
                foreach (var pb in prec.Bands)
                    foreach (var eb in pb.Enc!) any |= eb.LayerPasses[l] > eb.Included;
                bits.Bit(any ? 1 : 0);
                if (any)
                    foreach (var pb in prec.Bands)
                        for (int i = 0; i < pb.Enc!.Length; i++)
                        {
                            var eb = pb.Enc[i];
                            int now = eb.LayerPasses[l], add = now - eb.Included;
                            if (eb.Included == 0)
                            {
                                pb.InclEnc!.Encode(bits, i % pb.Cw, i / pb.Cw, l + 1);
                                if (add == 0) continue;
                                pb.ZbpEnc!.Encode(bits, i % pb.Cw, i / pb.Cw, int.MaxValue);
                            }
                            else
                            {
                                bits.Bit(add > 0 ? 1 : 0);
                                if (add == 0) continue;
                            }
                            // number of coding passes (B.10.6)
                            if (add == 1) bits.Bit(0);
                            else if (add == 2) bits.Bits(2, 2);
                            else if (add <= 5) bits.Bits(0xC | (add - 3), 4);
                            else if (add <= 36) bits.Bits(0x1E0 | (add - 6), 9);
                            else if (add <= 164) bits.Bits(0xFF80 | (add - 37), 16);
                            else throw new ImageNotSupportedException("more than 164 coding passes in one layer");
                            // codeword segment lengths (B.10.7)
                            pieces.Clear();
                            int pass = eb.Included, need = 0;
                            while (pass < now)
                            {
                                int m = Math.Min(now - pass, eb.Ht ? J2kPacketReader.HtSegmentPasses(0, pass) : J2kBlockCoder.SegmentPasses(_style, pass));
                                int len = eb.PassLen[pass + m - 1] - (pass == 0 ? 0 : eb.PassLen[pass - 1]);
                                pieces.Add((m, len));
                                need = Math.Max(need, 32 - BitOperations.LeadingZeroCount((uint)len) - BitOperations.Log2((uint)m));
                                pass += m;
                            }
                            for (; eb.Lblock < need; eb.Lblock++) bits.Bit(1);
                            bits.Bit(0);
                            foreach (var (m, len) in pieces) bits.Bits(len, eb.Lblock + BitOperations.Log2((uint)m));
                            int from = eb.Included == 0 ? 0 : eb.PassLen[eb.Included - 1];
                            bodies.Add((eb.Data, from, eb.PassLen[now - 1] - from));
                            eb.Included = now;
                        }
                bits.Flush();
                ms.Write(bits.Buf, 0, bits.Pos);
                if (_o.PacketMarkers) { ms.WriteByte(0xFF); ms.WriteByte(0x92); }
                foreach (var (data, start, len) in bodies) ms.Write(data, start, len);
                return true;
            }
            int spin = 0;
            int maxRes = _levels;
            J2kProgression.Run(tile, _cs.Siz, new J2kPoc { ResStart = 0, CompStart = 0, LayerEnd = _layers, ResEnd = maxRes + 1, CompEnd = _nc, Order = _cs.Main.Order },
                Packet, ref spin, int.MaxValue);

            var bytes = ms.ToArray();
            BinaryPrimitives.WriteUInt16BigEndian(bytes, J2kCodestream.SOT);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), 10);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), (ushort)tile.Index);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(6), (uint)bytes.Length);
            bytes[10] = 0; bytes[11] = 1;
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(12), J2kCodestream.SOD);
            return bytes;
        }
    }
}
