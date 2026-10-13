using System;
using System.Collections.Generic;
using System.Numerics;

namespace PixCodec
{
    internal delegate bool J2kPacketVisitor(int layer, int res, int comp, int precinct);

    /// <summary>The five progression orders of B.12.</summary>
    internal static class J2kProgression
    {
        /// <summary>Calls <paramref name="packet"/> for every packet of a progression volume in order; stops when it returns false.</summary>
        public static bool Run(J2kTile tile, J2kSiz siz, J2kPoc v, J2kPacketVisitor packet, ref int spin, int maxLayers)
        {
            int ncomp = tile.Comps.Length;
            int c0 = Math.Min(v.CompStart, ncomp), c1 = Math.Min(v.CompEnd, ncomp);
            int maxRes = 0;
            for (int c = c0; c < c1; c++) maxRes = Math.Max(maxRes, tile.Comps[c].Cod.Levels);
            int r0 = v.ResStart, r1 = Math.Min(v.ResEnd, maxRes + 1);
            int layers = Math.Min(v.LayerEnd, tile.Layers);
            switch (v.Order)
            {
                case 0: // LRCP
                    for (int l = 0; l < layers; l++)
                    {
                        // nothing after the wanted layers is needed when this is the only progression
                        if (l >= maxLayers) return true;
                        for (int r = r0; r < r1; r++)
                            for (int c = c0; c < c1; c++)
                            {
                                var tc = tile.Comps[c];
                                if (r > tc.Cod.Levels) continue;
                                int n = tc.Res[r].Pw * tc.Res[r].Ph;
                                for (int k = 0; k < n; k++)
                                    if (!packet(l, r, c, k)) return false;
                            }
                    }
                    return true;
                case 1: // RLCP
                    for (int r = r0; r < r1; r++)
                        for (int l = 0; l < layers; l++)
                            for (int c = c0; c < c1; c++)
                            {
                                var tc = tile.Comps[c];
                                if (r > tc.Cod.Levels) continue;
                                int n = tc.Res[r].Pw * tc.Res[r].Ph;
                                for (int k = 0; k < n; k++)
                                    if (!packet(l, r, c, k)) return false;
                            }
                    return true;
                case 2: // RPCL
                    for (int r = r0; r < r1; r++)
                    {
                        if (!Steps(tile, siz, c0, c1, r, r + 1, out long sx, out long sy)) continue;
                        for (long y = tile.Rect.Y0; y < tile.Rect.Y1; y = y - y % sy + sy)
                            for (long x = tile.Rect.X0; x < tile.Rect.X1; x = x - x % sx + sx)
                            {
                                if (++spin > 1 << 24) throw J2kCodestream.Bad("implausible precinct structure");
                                for (int c = c0; c < c1; c++)
                                    if (PrecinctAt(tile, siz, c, r, x, y, out int k, ref spin))
                                        for (int l = 0; l < layers; l++)
                                            if (!packet(l, r, c, k)) return false;
                            }
                    }
                    return true;
                case 3: // PCRL
                {
                    if (!Steps(tile, siz, c0, c1, r0, r1, out long sx, out long sy)) return true;
                    for (long y = tile.Rect.Y0; y < tile.Rect.Y1; y = y - y % sy + sy)
                        for (long x = tile.Rect.X0; x < tile.Rect.X1; x = x - x % sx + sx)
                        {
                            if (++spin > 1 << 24) throw J2kCodestream.Bad("implausible precinct structure");
                            for (int c = c0; c < c1; c++)
                                for (int r = r0; r < r1; r++)
                                    if (PrecinctAt(tile, siz, c, r, x, y, out int k, ref spin))
                                        for (int l = 0; l < layers; l++)
                                            if (!packet(l, r, c, k)) return false;
                        }
                    return true;
                }
                default: // CPRL
                    for (int c = c0; c < c1; c++)
                    {
                        if (!Steps(tile, siz, c, c + 1, r0, r1, out long sx, out long sy)) continue;
                        for (long y = tile.Rect.Y0; y < tile.Rect.Y1; y = y - y % sy + sy)
                            for (long x = tile.Rect.X0; x < tile.Rect.X1; x = x - x % sx + sx)
                            {
                                if (++spin > 1 << 24) throw J2kCodestream.Bad("implausible precinct structure");
                                for (int r = r0; r < r1; r++)
                                    if (PrecinctAt(tile, siz, c, r, x, y, out int k, ref spin))
                                        for (int l = 0; l < layers; l++)
                                            if (!packet(l, r, c, k)) return false;
                            }
                    }
                    return true;
            }
        }

        /// <summary>Smallest precinct spacing on the reference grid over the given components and resolution levels.</summary>
        static bool Steps(J2kTile tile, J2kSiz siz, int c0, int c1, int r0, int r1, out long sx, out long sy)
        {
            sx = sy = long.MaxValue;
            for (int c = c0; c < c1; c++)
            {
                var tc = tile.Comps[c];
                for (int r = r0; r < r1 && r <= tc.Cod.Levels; r++)
                {
                    int n = tc.Cod.Levels - r;
                    sx = Math.Min(sx, (long)siz.Dx[c] << (tc.Res[r].PPx + n));
                    sy = Math.Min(sy, (long)siz.Dy[c] << (tc.Res[r].PPy + n));
                }
            }
            return sx != long.MaxValue;
        }

        static bool PrecinctAt(J2kTile tile, J2kSiz siz, int c, int r, long x, long y, out int k, ref int spin)
        {
            k = 0;
            var tc = tile.Comps[c];
            if (r > tc.Cod.Levels) return false;
            var res = tc.Res[r];
            if (res.Pw == 0 || res.Ph == 0) return false;
            int n = tc.Cod.Levels - r;
            long dx = siz.Dx[c], dy = siz.Dy[c];
            if (!(y % (dy << (res.PPy + n)) == 0 || (y == tile.Rect.Y0 && ((res.Rect.Y0 << n) & ((1L << (res.PPy + n)) - 1)) != 0))) return false;
            if (!(x % (dx << (res.PPx + n)) == 0 || (x == tile.Rect.X0 && ((res.Rect.X0 << n) & ((1L << (res.PPx + n)) - 1)) != 0))) return false;
            long px = (J2kGeometry.CeilDiv(x, dx << n) >> res.PPx) - (res.Rect.X0 >> res.PPx);
            long py = (J2kGeometry.CeilDiv(y, dy << n) >> res.PPy) - (res.Rect.Y0 >> res.PPy);
            if (px < 0 || px >= res.Pw || py < 0 || py >= res.Ph) return false;
            k = (int)(px + py * res.Pw);
            spin = 0;
            return true;
        }
    }

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
        int _spin, _skipped;

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
                // Fewer packets than the progression calls for, ending on a packet boundary, is how some writers
                // drop layers; only data that stops inside a packet counts as truncated.
                _ = complete;
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

        bool RunVolume(J2kPoc v)
            => J2kProgression.Run(_tile, _cs.Siz, v, Packet, ref _spin, _tile.Poc == null ? _maxLayers : int.MaxValue);

        // ---- packets (B.9, B.10) ----

        J2kPrecinct CreatePrecinct(J2kResolution res, int rIndex, int k)
        {
            var prec = J2kGeometry.CreatePrecinct(res, rIndex, k);
            foreach (var pb in prec.Bands)
            {
                if (pb.Region.Empty) continue;
                long nodes = 2 * J2kTagTree.NodeCount((int)Math.Min(pb.CwLong, 1 << 20), (int)Math.Min(pb.ChLong, 1 << 20));
                _nodeBudget -= nodes + pb.CwLong * pb.ChLong;
                if (_nodeBudget < 0 || pb.CwLong * pb.ChLong > int.MaxValue) throw Bad("implausible code-block structure");
                pb.Cw = (int)pb.CwLong; pb.Ch = (int)pb.ChLong;
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
            if (layer < res.LayersDone[k])
            {
                // seen in an earlier progression volume; a header must not make us skip forever
                if (++_skipped > 1 << 26) throw Bad("implausible progression order changes");
                return true;
            }
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
                    var prec = res.Precincts![k] ??= CreatePrecinct(res, r, k);
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
                                bool ht = (style & J2kCod.Ht) != 0 && b->Phld != 255;
                                if (ht && !b->HtStarted)
                                {
                                    // T.814 Annex B. Until its first HT cleanup segment arrives a block contributes
                                    // placeholder passes only: one empty codeword segment per packet. A contribution
                                    // read that way comes out non-zero exactly if it does hold the cleanup pass (whose
                                    // length field lies inside the bits read and is at least 2); then the passes are
                                    // split after the last cleanup position, which fixes the number of placeholders.
                                    int bits = b->Lblock + BitOperations.Log2((uint)passes);
                                    if (bits > 31) throw Bad("bad code-block length");
                                    var saved = br;
                                    int len = br.Bits(bits);
                                    if (len == 0)
                                    {
                                        AddPending(pb, i, passes, 0);
                                        continue;
                                    }
                                    if ((style & J2kCod.HtMixed) != 0 && !(b->Lblock > 3 && len >> (bits - 1) == 0))
                                    {
                                        // mixed mode (A.4): an HT block announces itself by Lblock > 3 and a leading
                                        // 0 in its first length; this one is a Part 1 block with a single segment
                                        b->Phld = 255;
                                        AddPending(pb, i, passes, len);
                                        continue;
                                    }
                                    br = saved;
                                    b->Phld = (byte)Math.Min((pass + passes - 1) / 3, 250);
                                    b->HtStarted = true;
                                }
                                while (passes > 0)
                                {
                                    int m = Math.Min(passes, ht ? HtSegmentPasses(b->Phld, pass) : J2kBlockCoder.SegmentPasses(style & 63, pass));
                                    int bits = b->Lblock + BitOperations.Log2((uint)m);
                                    if (bits > 31) throw Bad("bad code-block length");
                                    int len = br.Bits(bits);
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
