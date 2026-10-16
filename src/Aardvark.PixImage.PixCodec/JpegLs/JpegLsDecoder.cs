using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Aardvark.ImageCodecs
{
    /// <summary>Marker parsing and scan decoding of a JPEG-LS file (T.87 Annex C, D).</summary>
    [SkipLocalsInit]
    internal sealed unsafe class JpegLsDecoder
    {
        readonly byte* _d;
        readonly int _len;

        // frame
        public int Bits, Width, Height, Nf;
        public byte[] CompId = Array.Empty<byte>();
        public int[] H = Array.Empty<int>(), V = Array.Empty<int>();
        int _hmax = 1, _vmax = 1;
        bool _frame;

        // tables and state
        int _maxVal, _t1, _t2, _t3, _reset;
        int _restart;
        readonly Dictionary<int, (int wt, List<byte> data)> _tables = new();
        /// <summary>Mapping table id used for each component (the last scan coding it decides), 0 = none.</summary>
        public int[] CompTable = Array.Empty<int>();
        public int Transform;          // HP colour transform from APP8 "mrfx": 0 none, 1..3 HP1..HP3
        public int SpiffColorSpace = -1;
        public double DpiX, DpiY;
        public byte[]? Exif, Icc;
        public int Orientation;
        readonly SortedDictionary<int, byte[]> _icc = new();
        int _iccCount;
        int _scans;
        bool _oversize;

        // output
        Array? _out;
        byte* _outPtr;
        ushort[][]? _planes;

        public JpegLsDecoder(byte* data, int length) { _d = data; _len = length; }

        static ImageFormatException Bad(string msg) => new("JPEG-LS: " + msg);

        int U16(int p) => _d[p] << 8 | _d[p + 1];

        public bool Subsampled => _hmax > 1 || _vmax > 1 || Array.Exists(H, h => h != _hmax) || Array.Exists(V, v => v != _vmax);
        public int CompWidth(int i) => (int)(((long)Width * H[i] + _hmax - 1) / _hmax);
        public int CompHeight(int i) => (int)(((long)Height * V[i] + _vmax - 1) / _vmax);

        /// <summary>Position of the next marker (0xFF followed by a code) at or after p, skipping fill bytes; -1 if none.</summary>
        int NextMarker(int p)
        {
            while (true)
            {
                int i = new ReadOnlySpan<byte>(_d + p, _len - p).IndexOf((byte)0xFF);
                if (i < 0) return -1;
                p += i;
                if (p + 1 >= _len) return -1;
                byte c = _d[p + 1];
                if (c == 0xFF) { p++; continue; }
                if (c >= 0x80) return p;
                p += 2;   // stuffed data byte
            }
        }

        /// <summary>
        /// Walks the markers. <paramref name="headerOnly"/> stops at the first scan header (later scans are walked over too
        /// when only they can tell the channel count: separate scans with mapping tables) and at the DNL segment when the
        /// frame leaves the height open.
        /// </summary>
        public void Parse(bool headerOnly, long maxPixels = long.MaxValue)
        {
            if (_len < 4 || _d[0] != 0xFF || _d[1] != 0xD8) throw Bad("missing SOI marker");
            int pos = 2;
            while (true)
            {
                if (pos + 2 > _len) { if (_scans > 0 && !headerOnly) break; throw Bad("truncated file"); }
                if (_d[pos] != 0xFF)
                {
                    int q = NextMarker(pos);
                    if (q < 0) { if (_scans > 0 && !headerOnly) break; throw Bad("truncated file"); }
                    pos = q;
                }
                while (pos + 1 < _len && _d[pos + 1] == 0xFF) pos++;
                if (pos + 2 > _len) { if (_scans > 0 && !headerOnly) break; throw Bad("truncated file"); }
                int m = _d[pos + 1];
                pos += 2;
                if (m == 0xD9) break;
                if (m == 0xD8 || m == 0x01 || (m >= 0xD0 && m <= 0xD7)) continue;
                if (pos + 2 > _len) { if (_scans > 0 && !headerOnly) break; throw Bad("truncated header"); }
                int len = U16(pos);
                if (len < 2) throw Bad("bad segment length");
                if (pos + len > _len) { if (_scans > 0 && !headerOnly) break; throw Bad("truncated header"); }
                int p = pos + 2, n = len - 2;
                pos += len;
                switch (m)
                {
                    case 0xF7: ReadFrame(p, n); break;
                    case 0xF9: throw new ImageNotSupportedException("JPEG-LS: extended (ISO 14495-2, SOF57) coding is not supported");
                    case 0xF8: ReadLse(p, n); break;
                    case 0xDD: ReadDri(p, n); break;
                    case 0xDC:
                        if (n < 2) throw Bad("bad DNL segment");
                        if (Height == 0) Height = U16(p);
                        break;
                    case 0xE8: ReadApp8(p, n); break;
                    case 0xE1:
                        if (n > 6 && new ReadOnlySpan<byte>(_d + p, 6).SequenceEqual("Exif\0\0"u8))
                        {
                            Exif = new ReadOnlySpan<byte>(_d + p + 6, n - 6).ToArray();
                            Orientation = JpegImpl.JpegMeta.ExifOrientation(Exif);
                        }
                        break;
                    case 0xE2:
                        if (n > 14 && new ReadOnlySpan<byte>(_d + p, 12).SequenceEqual("ICC_PROFILE\0"u8))
                        {
                            _icc[_d[p + 12]] = new ReadOnlySpan<byte>(_d + p + 14, n - 14).ToArray();
                            _iccCount = _d[p + 13];
                        }
                        break;
                    case 0xDA:
                        {
                            if (!_frame) throw Bad("scan before frame header");
                            var scan = ReadScanHeader(p, n);
                            if (Height == 0) FindNumberOfLines(pos);
                            if (Width == 0 || Height == 0) throw Bad("image size is not defined");
                            if (_scans == 0)
                            {
                                if ((long)Width * Height > maxPixels || (long)Width * Height * Nf > Array.MaxLength)
                                    throw new ImageNotSupportedException($"JPEG-LS: {Width}x{Height} exceeds MaxPixels");
                            }
                            foreach (var ci in scan.comps) CompTable[ci] = scan.tables[Array.IndexOf(scan.comps, ci)];
                            if (headerOnly)
                            {
                                _scans++;
                                // later scans matter only when they can change the channel layout
                                if (_tables.Count == 0 || scan.ilv != 0 || Nf == 1) { FinishHeaders(); return; }
                                int q = NextMarker(pos);
                                if (q < 0) { FinishHeaders(); return; }
                                pos = q;
                                break;
                            }
                            if (_out == null) Allocate();
                            pos = DecodeScan(scan, pos);
                            _scans++;
                            break;
                        }
                    default:
                        if (m >= 0xC0 && m <= 0xCF && m != 0xC4 && m != 0xC8 && m != 0xCC)
                            throw Bad("not a JPEG-LS file (DCT / lossless JPEG frame)");
                        break;
                }
            }
            if (!_frame) throw Bad("no frame header");
            if (headerOnly)
            {
                if (_scans == 0) throw Bad("truncated file");
                FinishHeaders();
                return;
            }
            if (_scans == 0) throw Bad("no image data");
            FinishHeaders();
        }

        void FinishHeaders()
        {
            if (_iccCount > 0 && _icc.Count == _iccCount)
            {
                var all = new List<byte>();
                foreach (var kv in _icc) all.AddRange(kv.Value);
                Icc = all.ToArray();
            }
        }

        void ReadFrame(int p, int n)
        {
            if (_frame) throw Bad("second frame header");
            if (n < 6) throw Bad("bad frame header");
            Bits = _d[p];
            if (Bits < 2 || Bits > 16) throw Bad($"bad sample precision {Bits}");
            int y = U16(p + 1), x = U16(p + 3);
            if (!_oversize) { Height = y; Width = x; }
            else
            {
                if (y != 0) Height = y;
                if (x != 0) Width = x;
            }
            Nf = _d[p + 5];
            if (Nf == 0 || n < 6 + 3 * Nf) throw Bad("bad frame header");
            CompId = new byte[Nf]; H = new int[Nf]; V = new int[Nf]; CompTable = new int[Nf];
            for (int i = 0; i < Nf; i++)
            {
                CompId[i] = _d[p + 6 + 3 * i];
                H[i] = _d[p + 7 + 3 * i] >> 4;
                V[i] = _d[p + 7 + 3 * i] & 15;
                if (H[i] < 1 || H[i] > 4 || V[i] < 1 || V[i] > 4) throw Bad("bad sampling factors");
                for (int j = 0; j < i; j++) if (CompId[j] == CompId[i]) throw Bad("duplicate component id");
                _hmax = Math.Max(_hmax, H[i]); _vmax = Math.Max(_vmax, V[i]);
            }
            _frame = true;
        }

        void ReadDri(int p, int n)
        {
            if (n < 2 || n > 4) throw Bad("bad DRI segment");
            int v = 0;
            for (int i = 0; i < n; i++) v = v << 8 | _d[p + i];
            _restart = v;
        }

        void ReadLse(int p, int n)
        {
            if (n < 1) throw Bad("bad LSE segment");
            int id = _d[p];
            switch (id)
            {
                case 1:
                    if (n < 11) throw Bad("bad preset parameters");
                    _maxVal = U16(p + 1); _t1 = U16(p + 3); _t2 = U16(p + 5); _t3 = U16(p + 7); _reset = U16(p + 9);
                    break;
                case 2:
                case 3:
                    {
                        if (n < 3) throw Bad("bad mapping table");
                        int tid = _d[p + 1], wt = _d[p + 2];
                        if (tid == 0 || wt < 1 || wt > 4) throw new ImageNotSupportedException($"JPEG-LS: mapping table entries of {wt} bytes are not supported");
                        int bytes = n - 3;
                        if (id == 2) _tables[tid] = (wt, new List<byte>(bytes));
                        else if (!_tables.ContainsKey(tid)) throw Bad("mapping table continuation without table");
                        var t = _tables[tid];
                        if (t.wt != wt) throw Bad("mapping table continuation with another entry size");
                        t.data.AddRange(new ReadOnlySpan<byte>(_d + p + 3, bytes).ToArray());
                        break;
                    }
                case 4:
                    {
                        if (n < 2) throw Bad("bad oversize segment");
                        int wxy = _d[p + 1];
                        if (wxy < 2 || wxy > 4 || n < 2 + 2 * wxy) throw Bad("bad oversize segment");
                        long y = 0, x = 0;
                        for (int i = 0; i < wxy; i++) y = y << 8 | _d[p + 2 + i];
                        for (int i = 0; i < wxy; i++) x = x << 8 | _d[p + 2 + wxy + i];
                        if (x > int.MaxValue || y > int.MaxValue) throw new ImageNotSupportedException("JPEG-LS: image dimensions exceed 2^31");
                        Width = (int)x; Height = (int)y;
                        _oversize = true;
                        break;
                    }
                default:
                    throw new ImageNotSupportedException($"JPEG-LS: LSE segment id {id} (ISO 14495-2 extension) is not supported");
            }
        }

        void ReadApp8(int p, int n)
        {
            var s = new ReadOnlySpan<byte>(_d + p, n);
            if (n == 5 && s.StartsWith("mrfx"u8))
            {
                Transform = s[4];
                if (Transform > 3) throw new ImageNotSupportedException($"JPEG-LS: HP colour transform {Transform} is not supported");
            }
            else if (n >= 30 && s.StartsWith("SPIFF\0"u8))
            {
                // version (2), profile, components, height (4), width (4), colour space, bits, compression, units, vres (4), hres (4)
                SpiffColorSpace = s[18];
                int units = s[21];
                double vres = (uint)(s[22] << 24 | s[23] << 16 | s[24] << 8 | s[25]);
                double hres = (uint)(s[26] << 24 | s[27] << 16 | s[28] << 8 | s[29]);
                if (units == 1) { DpiX = hres; DpiY = vres; }
                else if (units == 2) { DpiX = hres * 2.54; DpiY = vres * 2.54; }
            }
        }

        record struct Scan(int[] comps, int[] tables, int near, int ilv);

        Scan ReadScanHeader(int p, int n)
        {
            if (n < 1) throw Bad("bad scan header");
            int ns = _d[p];
            if (ns < 1 || ns > Nf || n < 4 + 2 * ns) throw Bad("bad scan header");
            var comps = new int[ns];
            var tables = new int[ns];
            for (int i = 0; i < ns; i++)
            {
                int id = _d[p + 1 + 2 * i];
                int ci = Array.IndexOf(CompId, (byte)id);
                if (ci < 0) throw Bad($"scan references unknown component {id}");
                for (int j = 0; j < i; j++) if (comps[j] == ci) throw Bad("component twice in a scan");
                comps[i] = ci;
                tables[i] = _d[p + 2 + 2 * i];
                if (tables[i] != 0 && !_tables.ContainsKey(tables[i])) throw Bad($"undefined mapping table {tables[i]}");
            }
            int q = p + 1 + 2 * ns;
            int near = _d[q], ilv = _d[q + 1], pt = _d[q + 2] & 15;
            if (ilv > 2) throw Bad($"bad interleave mode {ilv}");
            if (ilv == 0 && ns != 1) throw Bad("non-interleaved scan with several components");
            if (pt != 0) throw new ImageNotSupportedException("JPEG-LS: point transform is not supported");
            if (ilv == 2)
                foreach (var ci in comps)
                    if (H[ci] != H[comps[0]] || V[ci] != V[comps[0]]) throw new ImageNotSupportedException("JPEG-LS: sample interleaved scan with subsampled components");
            return new Scan(comps, tables, near, ilv);
        }

        void FindNumberOfLines(int pos)
        {
            int p = pos;
            while (true)
            {
                int q = NextMarker(p);
                if (q < 0) throw Bad("image height is not defined (no DNL segment)");
                int m = _d[q + 1];
                if (m == 0xDC)
                {
                    if (q + 6 > _len) throw Bad("truncated DNL segment");
                    Height = U16(q + 4);
                    if (Height == 0) throw Bad("bad DNL segment");
                    return;
                }
                if (m == 0xD9) throw Bad("image height is not defined (no DNL segment)");
                p = q + 2;
            }
        }

        public JlsParams ScanParams(int near) => JlsParams.Resolve(Bits, near, _maxVal, _t1, _t2, _t3, _reset);

        public int MaxVal => _maxVal != 0 ? _maxVal : (1 << Bits) - 1;

        // ================================================================== output

        GCHandle _pin;

        public bool Wide => Bits > 8;

        void Allocate()
        {
            long count = (long)Width * Height * Nf;
            if (count > Array.MaxLength) throw new ImageNotSupportedException("JPEG-LS: image too large");
            _out = Wide ? new ushort[count] : new byte[count];
            _pin = GCHandle.Alloc(_out, GCHandleType.Pinned);
            _outPtr = (byte*)_pin.AddrOfPinnedObject();
            if (Subsampled)
            {
                _planes = new ushort[Nf][];
                for (int i = 0; i < Nf; i++)
                    if (H[i] != _hmax || V[i] != _vmax) _planes[i] = new ushort[(long)CompWidth(i) * CompHeight(i)];
            }
        }

        public Array TakeOutput()
        {
            if (_pin.IsAllocated) _pin.Free();
            var o = _out!;
            if (_planes != null) Upsample(o);
            return o;
        }

        public void Release() { if (_pin.IsAllocated) _pin.Free(); }

        void Upsample(Array o)
        {
            for (int c = 0; c < Nf; c++)
            {
                var pl = _planes![c];
                if (pl == null) continue;
                int cw = CompWidth(c), ch = CompHeight(c);
                int fx = _hmax / H[c], fy = _vmax / V[c];
                bool exact = _hmax % H[c] == 0 && _vmax % V[c] == 0;
                for (int y = 0; y < Height; y++)
                {
                    int sy = exact ? y / fy : (int)((long)y * V[c] / _vmax);
                    if (sy >= ch) sy = ch - 1;
                    long row = (long)sy * cw;
                    long d = (long)y * Width * Nf + c;
                    for (int x = 0; x < Width; x++, d += Nf)
                    {
                        int sx = exact ? x / fx : (int)((long)x * H[c] / _hmax);
                        if (sx >= cw) sx = cw - 1;
                        ushort v = pl[row + sx];
                        if (o is ushort[] w) w[d] = v; else ((byte[])o)[d] = (byte)v;
                    }
                }
            }
        }

        /// <summary>Stores a decoded line of component c (line y of that component).</summary>
        void Store(int c, int y, int* line, int w)
        {
            var pl = _planes?[c];
            if (pl != null)
            {
                fixed (ushort* dst = &pl[(long)y * w])
                    for (int x = 0; x < w; x++) dst[x] = (ushort)line[x];
                return;
            }
            long baseIdx = (long)y * Width * Nf + c;
            int nf = Nf;
            if (Wide)
            {
                ushort* dst = (ushort*)_outPtr + baseIdx;
                if (nf == 1) for (int x = 0; x < w; x++) dst[x] = (ushort)line[x];
                else for (int x = 0; x < w; x++) dst[(long)x * nf] = (ushort)line[x];
            }
            else
            {
                byte* dst = _outPtr + baseIdx;
                if (nf == 1) for (int x = 0; x < w; x++) dst[x] = (byte)line[x];
                else for (int x = 0; x < w; x++) dst[(long)x * nf] = (byte)line[x];
            }
        }

        // ================================================================== scans

        /// <summary>Line buffers of one component: two lines of w + 2 ints (guard sample on each side).</summary>
        sealed class Lines
        {
            public readonly int[] Buf;
            public readonly int W;
            public int Cur;   // 0 or 1
            public Lines(int w) { W = w; Buf = new int[2 * (w + 2)]; }
        }

        int DecodeScan(Scan s, int pos)
        {
            var coder = new JlsCoder(ScanParams(s.near));
            int ns = s.comps.Length;
            var lines = new Lines[ns];
            var handles = new GCHandle[ns];
            var runIndex = new int[ns];
            for (int i = 0; i < ns; i++)
            {
                lines[i] = new Lines(CompWidth(s.comps[i]));
                handles[i] = GCHandle.Alloc(lines[i].Buf, GCHandleType.Pinned);
            }
            try
            {
                var r = new JlsBitReader(_d + pos, _d + _len);
                int** prevs = stackalloc int*[ns];
                int** curs = stackalloc int*[ns];
                var done = new int[ns];
                int restart = _restart, intervalLines = 0;
                int groups = s.ilv == 0 ? CompHeight(s.comps[0]) : s.ilv == 2 ? CompHeight(s.comps[0]) : 0;
                if (s.ilv == 1)
                {
                    for (int i = 0; i < ns; i++)
                        groups = Math.Max(groups, (CompHeight(s.comps[i]) + V[s.comps[i]] - 1) / V[s.comps[i]]);
                }
                for (int g = 0; g < groups; g++)
                {
                    if (restart > 0 && intervalLines == restart)
                    {
                        // restart marker: realign, reset the coder state
                        int q = NextMarker((int)(r.Position - _d));
                        if (q >= 0 && _d[q + 1] >= 0xD0 && _d[q + 1] <= 0xD7) q += 2;
                        else if (q < 0) q = _len;
                        r = new JlsBitReader(_d + q, _d + _len);
                        coder.ResetContexts();
                        Array.Clear(runIndex);
                        for (int i = 0; i < ns; i++) Array.Clear(lines[i].Buf);
                        intervalLines = 0;
                    }
                    intervalLines++;
                    // data exhausted (truncated or corrupt): the rest stays zero, and a bogus size costs no time
                    if (r.Exhausted) break;
                    if (s.ilv == 2)
                    {
                        for (int i = 0; i < ns; i++) Prepare(lines[i], (int*)handles[i].AddrOfPinnedObject(), out prevs[i], out curs[i]);
                        coder.DecodeLineSample(ref r, prevs, curs, ns, lines[0].W, ref runIndex[0]);
                        for (int i = 0; i < ns; i++) { Store(s.comps[i], g, curs[i], lines[i].W); lines[i].Cur ^= 1; }
                        continue;
                    }
                    for (int i = 0; i < ns; i++)
                    {
                        int c = s.comps[i];
                        int nl = s.ilv == 0 ? 1 : V[c];
                        for (int v = 0; v < nl && done[i] < CompHeight(c); v++)
                        {
                            Prepare(lines[i], (int*)handles[i].AddrOfPinnedObject(), out int* prev, out int* cur);
                            coder.DecodeLine(ref r, prev, cur, lines[i].W, ref runIndex[i]);
                            Store(c, done[i]++, cur, lines[i].W);
                            lines[i].Cur ^= 1;
                        }
                    }
                }
                int end = NextMarker((int)(r.Position - _d));
                return end < 0 ? _len : end;
            }
            finally
            {
                foreach (var h in handles) if (h.IsAllocated) h.Free();
            }
        }

        static void Prepare(Lines l, int* buf, out int* prev, out int* cur)
        {
            int stride = l.W + 2;
            prev = buf + (l.Cur ^ 1) * stride + 1;
            cur = buf + l.Cur * stride + 1;
            int w = l.W;
            prev[w] = prev[w - 1];
            cur[-1] = prev[0];
        }

        public Dictionary<int, (int wt, List<byte> data)> Tables => _tables;
    }
}
