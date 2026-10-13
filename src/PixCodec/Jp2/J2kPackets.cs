using System;
using System.Collections.Generic;
using System.Numerics;

namespace PixCodec
{
    /// <summary>Tier-2: walks the packets of one tile in progression order and collects the code-block contributions.</summary>
    internal sealed unsafe class J2kPacketReader
    {
        readonly J2kCodestream _cs;
        readonly J2kTile _tile;
        readonly J2kTileHeader _th;
        readonly int _maxLayers;
        readonly bool _strict;
        int _part = -1;
        byte* _body, _bodyEnd;
        readonly byte[]? _packed;            // packet headers from PPM / PPT, null if they are in the tile-parts
        int _packedPos;
        long _nodeBudget, _visitBudget, _precinctBudget;
        int _spin;

        struct Pending { public J2kPrecinctBand Band; public int Block, Passes, Len; }
        Pending[] _pending = new Pending[64];
        int _pendingCount;

        public J2kPacketReader(J2kCodestream cs, J2kTile tile, List<byte[]>? ppm, int maxLayers, bool strict)
        {
            _cs = cs; _tile = tile; _maxLayers = maxLayers; _strict = strict;
            _th = cs.Tiles[tile.Index]!;
            long bytes = 0;
            foreach (var p in _th.Parts) bytes += p.End - p.Start;
            if (ppm != null)
            {
                int n = 0;
                foreach (var p in _th.Parts) if (p.PpmIndex < ppm.Count) n += ppm[p.PpmIndex].Length;
                _packed = new byte[n];
                n = 0;
                foreach (var p in _th.Parts)
                    if (p.PpmIndex < ppm.Count) { ppm[p.PpmIndex].CopyTo(_packed, n); n += ppm[p.PpmIndex].Length; }
            }
            else if (_th.Ppt != null)
            {
                var segs = new List<(int z, int start, int length, int part)>(_th.Ppt);
                segs.Sort((a, b) => a.part != b.part ? a.part.CompareTo(b.part) : a.z.CompareTo(b.z));
                int n = 0;
                foreach (var s in segs) n += s.length;
                _packed = new byte[n];
                n = 0;
                foreach (var s in segs) { new ReadOnlySpan<byte>(cs.Data + s.start, s.length).CopyTo(_packed.AsSpan(n)); n += s.length; }
            }
            if (_packed != null) bytes += _packed.Length;
            // What a header can make us allocate or iterate is tied to the amount of data that is really there.
            _nodeBudget = (1L << 21) + 64 * bytes;
            _visitBudget = (1L << 24) + 1024 * bytes;
            _precinctBudget = (strict ? 4096 : 1L << 22) + 8 * bytes;
        }

        static Exception Bad(string what) => J2kCodestream.Bad(what);

        public void Run()
        {
            try
            {
                bool complete = true;
                if (_tile.Poc != null && _tile.Poc.Count > 0)
                {
                    foreach (var v in _tile.Poc)
                        if (!RunVolume(v)) { complete = false; break; }
                }
                else
                {
                    int maxRes = 0;
                    foreach (var tc in _tile.Comps) maxRes = Math.Max(maxRes, tc.Cod.Levels);
                    complete = RunVolume(new J2kPoc { ResStart = 0, CompStart = 0, LayerEnd = _tile.Layers, ResEnd = maxRes + 1, CompEnd = _tile.Comps.Length, Order = _tile.Order });
                }
                if (!complete) _tile.Truncated = true;
            }
            catch (J2kEndOfData)
            {
                _tile.Truncated = true;
            }
        }

        bool NextPart()
        {
            while (_part + 1 < _th.Parts.Count)
            {
                _part++;
                var p = _th.Parts[_part];
                _body = _cs.Data + p.Start; _bodyEnd = _cs.Data + p.End;
                if (_body < _bodyEnd) return true;
            }
            return false;
        }

        // ---- progression orders (B.12) ----

        bool RunVolume(J2kPoc v)
        {
            int ncomp = _tile.Comps.Length;
            int c0 = Math.Min(v.CompStart, ncomp), c1 = Math.Min(v.CompEnd, ncomp);
            int maxRes = 0;
            for (int c = c0; c < c1; c++) maxRes = Math.Max(maxRes, _tile.Comps[c].Cod.Levels);
            int r0 = v.ResStart, r1 = Math.Min(v.ResEnd, maxRes + 1);
            int layers = Math.Min(v.LayerEnd, _tile.Layers);
            switch (v.Order)
            {
                case 0: // LRCP
                    for (int l = 0; l < layers; l++)
                    {
                        // nothing after the wanted layers is needed when this is the only progression
                        if (l >= _maxLayers && _tile.Poc == null) return true;
                        for (int r = r0; r < r1; r++)
                            for (int c = c0; c < c1; c++)
                            {
                                var tc = _tile.Comps[c];
                                if (r > tc.Cod.Levels) continue;
                                int n = tc.Res[r].Pw * tc.Res[r].Ph;
                                for (int k = 0; k < n; k++)
                                    if (!Packet(l, r, c, k)) return false;
                            }
                    }
                    return true;
                case 1: // RLCP
                    for (int r = r0; r < r1; r++)
                        for (int l = 0; l < layers; l++)
                            for (int c = c0; c < c1; c++)
                            {
                                var tc = _tile.Comps[c];
                                if (r > tc.Cod.Levels) continue;
                                int n = tc.Res[r].Pw * tc.Res[r].Ph;
                                for (int k = 0; k < n; k++)
                                    if (!Packet(l, r, c, k)) return false;
                            }
                    return true;
                case 2: // RPCL
                    for (int r = r0; r < r1; r++)
                    {
                        if (!Steps(c0, c1, r, r + 1, out long sx, out long sy)) continue;
                        for (long y = _tile.Rect.Y0; y < _tile.Rect.Y1; y = y - y % sy + sy)
                            for (long x = _tile.Rect.X0; x < _tile.Rect.X1; x = x - x % sx + sx)
                            {
                                Spin();
                                for (int c = c0; c < c1; c++)
                                    if (PrecinctAt(c, r, x, y, out int k))
                                        for (int l = 0; l < layers; l++)
                                            if (!Packet(l, r, c, k)) return false;
                            }
                    }
                    return true;
                case 3: // PCRL
                {
                    if (!Steps(c0, c1, r0, r1, out long sx, out long sy)) return true;
                    for (long y = _tile.Rect.Y0; y < _tile.Rect.Y1; y = y - y % sy + sy)
                        for (long x = _tile.Rect.X0; x < _tile.Rect.X1; x = x - x % sx + sx)
                        {
                            Spin();
                            for (int c = c0; c < c1; c++)
                                for (int r = r0; r < r1; r++)
                                    if (PrecinctAt(c, r, x, y, out int k))
                                        for (int l = 0; l < layers; l++)
                                            if (!Packet(l, r, c, k)) return false;
                        }
                    return true;
                }
                default: // CPRL
                    for (int c = c0; c < c1; c++)
                    {
                        if (!Steps(c, c + 1, r0, r1, out long sx, out long sy)) continue;
                        for (long y = _tile.Rect.Y0; y < _tile.Rect.Y1; y = y - y % sy + sy)
                            for (long x = _tile.Rect.X0; x < _tile.Rect.X1; x = x - x % sx + sx)
                            {
                                Spin();
                                for (int r = r0; r < r1; r++)
                                    if (PrecinctAt(c, r, x, y, out int k))
                                        for (int l = 0; l < layers; l++)
                                            if (!Packet(l, r, c, k)) return false;
                            }
                    }
                    return true;
            }
        }

        void Spin()
        {
            // positions that hold no precinct cost no data; a header must not make us walk billions of them
            if (++_spin > 1 << 24) throw Bad("implausible precinct structure");
        }

        /// <summary>Smallest precinct spacing on the reference grid over the given components and resolution levels.</summary>
        bool Steps(int c0, int c1, int r0, int r1, out long sx, out long sy)
        {
            sx = sy = long.MaxValue;
            for (int c = c0; c < c1; c++)
            {
                var tc = _tile.Comps[c];
                for (int r = r0; r < r1 && r <= tc.Cod.Levels; r++)
                {
                    int n = tc.Cod.Levels - r;
                    sx = Math.Min(sx, (long)_cs.Siz.Dx[c] << (tc.Res[r].PPx + n));
                    sy = Math.Min(sy, (long)_cs.Siz.Dy[c] << (tc.Res[r].PPy + n));
                }
            }
            return sx != long.MaxValue;
        }

        bool PrecinctAt(int c, int r, long x, long y, out int k)
        {
            k = 0;
            var tc = _tile.Comps[c];
            if (r > tc.Cod.Levels) return false;
            var res = tc.Res[r];
            if (res.Pw == 0 || res.Ph == 0) return false;
            int n = tc.Cod.Levels - r;
            long dx = _cs.Siz.Dx[c], dy = _cs.Siz.Dy[c];
            if (!(y % (dy << (res.PPy + n)) == 0 || (y == _tile.Rect.Y0 && ((res.Rect.Y0 << n) & ((1L << (res.PPy + n)) - 1)) != 0))) return false;
            if (!(x % (dx << (res.PPx + n)) == 0 || (x == _tile.Rect.X0 && ((res.Rect.X0 << n) & ((1L << (res.PPx + n)) - 1)) != 0))) return false;
            long px = (J2kGeometry.CeilDiv(x, dx << n) >> res.PPx) - (res.Rect.X0 >> res.PPx);
            long py = (J2kGeometry.CeilDiv(y, dy << n) >> res.PPy) - (res.Rect.Y0 >> res.PPy);
            if (px < 0 || px >= res.Pw || py < 0 || py >= res.Ph) return false;
            k = (int)(px + py * res.Pw);
            _spin = 0;
            return true;
        }

        // ---- packets (B.9, B.10) ----

        J2kPrecinct CreatePrecinct(J2kTileComp tc, J2kResolution res, int rIndex, int k)
        {
            long px = (res.Rect.X0 >> res.PPx) + k % res.Pw, py = (res.Rect.Y0 >> res.PPy) + k / res.Pw;
            var prec = new J2kPrecinct { Bands = new J2kPrecinctBand[res.Bands.Length] };
            for (int b = 0; b < res.Bands.Length; b++)
            {
                var band = res.Bands[b];
                int sh = rIndex == 0 ? 0 : 1;
                var region = new J2kRect((px << res.PPx) >> sh, (py << res.PPy) >> sh, ((px + 1) << res.PPx) >> sh, ((py + 1) << res.PPy) >> sh)
                    .Intersect(band.Rect);
                var pb = new J2kPrecinctBand { Region = region };
                prec.Bands[b] = pb;
                if (region.Empty) continue;
                pb.Cbx0 = region.X0 >> res.CbW; pb.Cby0 = region.Y0 >> res.CbH;
                long cw = J2kGeometry.CeilShift(region.X1, res.CbW) - pb.Cbx0, ch = J2kGeometry.CeilShift(region.Y1, res.CbH) - pb.Cby0;
                long nodes = 2 * J2kTagTree.NodeCount((int)Math.Min(cw, 1 << 20), (int)Math.Min(ch, 1 << 20));
                _nodeBudget -= nodes + cw * ch;
                if (_nodeBudget < 0 || cw * ch > int.MaxValue) throw Bad("implausible code-block structure");
                pb.Cw = (int)cw; pb.Ch = (int)ch;
                pb.Incl = new J2kTagTree(pb.Cw, pb.Ch);
                pb.Zbp = new J2kTagTree(pb.Cw, pb.Ch);
                pb.Blocks = new J2kBlock[pb.Cw * pb.Ch];
            }
            return prec;
        }

        static int ReadPasses(ref J2kBitReader br)
        {
            if (br.Bit() == 0) return 1;
            if (br.Bit() == 0) return 2;
            int v = br.Bits(2);
            if (v < 3) return 3 + v;
            v = br.Bits(5);
            if (v < 31) return 6 + v;
            return 37 + br.Bits(7);
        }

        /// <summary>Number of passes from <paramref name="pass"/> to the end of its HT segment (T.814 B.2), given the placeholder sets.</summary>
        public static int HtSegmentPasses(int phld, int pass)
        {
            if (pass <= 3 * phld) return 3 * phld + 1 - pass;
            return (pass - 3 * phld - 1) % 3 == 0 ? 2 : 1;
        }

        void AddPending(J2kPrecinctBand band, int block, int passes, int len)
        {
            if (_pendingCount == _pending.Length) Array.Resize(ref _pending, _pending.Length * 2);
            _pending[_pendingCount++] = new Pending { Band = band, Block = block, Passes = passes, Len = len };
        }

        /// <summary>Reads one packet. False when the tile's data is used up.</summary>
        bool Packet(int layer, int r, int c, int k)
        {
            var tc = _tile.Comps[c];
            var res = tc.Res[r];
            if (res.LayersDone == null)
            {
                _precinctBudget -= (long)res.Pw * res.Ph;
                if (_precinctBudget < 0) throw Bad("more precincts than the data can hold");
                res.LayersDone = new int[res.Pw * res.Ph];
                res.Precincts = new J2kPrecinct?[res.Pw * res.Ph];
            }
            if (layer < res.LayersDone[k]) return true;   // seen in an earlier progression volume
            res.LayersDone[k] = layer + 1;

            if (_part < 0 || _body >= _bodyEnd)
            {
                bool more = NextPart();
                if (!more && _packed == null) return false;
            }
            if (_packed != null && _packedPos >= _packed.Length) return false;
            if (_bodyEnd - _body >= 6 && _body[0] == 0xFF && _body[1] == 0x91) _body += 6;   // SOP

            _pendingCount = 0;
            fixed (byte* packed = _packed)
            {
                var br = _packed != null ? new J2kBitReader(packed + _packedPos, packed + _packed.Length) : new J2kBitReader(_body, _bodyEnd);
                if (br.Bit() != 0)
                {
                    var prec = res.Precincts![k] ??= CreatePrecinct(tc, res, r, k);
                    int style = tc.Cod.Style;
                    foreach (var pb in prec.Bands)
                    {
                        int n = pb.Blocks.Length;
                        if (n == 0) continue;
                        _visitBudget -= n;
                        if (_visitBudget < 0) throw Bad("implausible code-block structure");
                        fixed (J2kBlock* blocks = pb.Blocks)
                            for (int i = 0; i < n; i++)
                            {
                                J2kBlock* b = blocks + i;
                                int bx = i % pb.Cw, by = i / pb.Cw;
                                bool first = !b->Included;
                                if (first)
                                {
                                    if (!pb.Incl!.Decode(ref br, bx, by, layer + 1)) continue;
                                    int z = 1;
                                    while (!pb.Zbp!.Decode(ref br, bx, by, z))
                                        if (++z > 100) throw Bad("bad zero bit-plane count");
                                    b->Zbp = (byte)(z - 1);
                                    b->Lblock = 3;
                                    b->Included = true;
                                }
                                else if (br.Bit() == 0) continue;
                                int passes = ReadPasses(ref br);
                                while (br.Bit() != 0)
                                    if (++b->Lblock > 32) throw Bad("bad code-block length indicator");
                                int pass = b->Passes;
                                b->Passes += passes;
                                bool ht = (style & J2kCod.Ht) != 0;
                                int lead = -1;
                                if (ht && (style & J2kCod.HtMixed) != 0)
                                {
                                    // T.814 A.4: an HT block is announced by Lblock > 3 and a 0 as first bit of its first length
                                    if (pass == 0)
                                    {
                                        if (b->Lblock > 3) { lead = br.Bit(); b->Phld = (byte)(lead == 0 ? 0 : 255); }
                                        else b->Phld = 255;
                                    }
                                    ht = b->Phld != 255;
                                }
                                if (ht && pass == 0) b->Phld = (byte)Math.Min((passes - 1) / 3, 100);
                                while (passes > 0)
                                {
                                    int m = Math.Min(passes, ht ? HtSegmentPasses(b->Phld, pass) : J2kBlockCoder.SegmentPasses(style & 63, pass));
                                    int bits = b->Lblock + BitOperations.Log2((uint)m);
                                    if (bits > 31) throw Bad("bad code-block length");
                                    int len;
                                    if (lead >= 0) { len = lead << (bits - 1) | br.Bits(bits - 1); lead = -1; }
                                    else len = br.Bits(bits);
                                    AddPending(pb, i, m, len);
                                    passes -= m; pass += m;
                                }
                            }
                    }
                }
                br.Align();
                if (br.End - br.P >= 2 && br.P[0] == 0xFF && br.P[1] == 0x92) br.P += 2;   // EPH
                if (_packed != null) _packedPos = (int)(br.P - packed); else _body = br.P;
            }

            bool keep = layer < _maxLayers && tc.Needed && r <= tc.OutRes;
            for (int i = 0; i < _pendingCount; i++)
            {
                ref var p = ref _pending[i];
                if (p.Len > 0 && _body >= _bodyEnd && _packed != null) NextPart();
                long avail = _bodyEnd - _body;
                int len = p.Len;
                bool cut = len > avail;
                if (cut) len = (int)avail;
                if (keep)
                {
                    int id = _tile.AddChunk((int)(_body - _cs.Data), len, p.Passes);
                    ref var b = ref p.Band.Blocks[p.Block];
                    if (b.LastChunk == 0) b.FirstChunk = id; else _tile.Chunks[b.LastChunk - 1].Next = id;
                    b.LastChunk = id;
                }
                _body += len;
                if (cut) throw J2kEndOfData.Instance;
            }
            return true;
        }
    }
}
