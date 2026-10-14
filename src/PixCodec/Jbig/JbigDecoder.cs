using System;
using System.Collections.Generic;
using PixCodec.JpegImpl;

namespace PixCodec.JbigImpl
{
    /// <summary>The fields of a bi-level image header (T.82 6.2.2).</summary>
    internal struct JbigHeader
    {
        public const int Size = 20;

        public int DL, D, P;
        public long XD, YD, L0;
        public int MX, MY;
        public bool HiToLo, Seq, ILeave, SMid;
        public bool TwoLine, VLength, TpDiff, TpBottom, Dp, DpPrivate, DpLast;

        public static JbigHeader Parse(ReadOnlySpan<byte> d)
        {
            if (d.Length < Size) throw new ImageFormatException("truncated JBIG header");
            var h = new JbigHeader
            {
                DL = d[0], D = d[1], P = d[2],
                XD = U32(d, 4), YD = U32(d, 8), L0 = U32(d, 12),
                MX = d[16], MY = d[17],
                HiToLo = (d[18] & 8) != 0, Seq = (d[18] & 4) != 0, ILeave = (d[18] & 2) != 0, SMid = (d[18] & 1) != 0,
                TwoLine = (d[19] & 0x40) != 0, VLength = (d[19] & 0x20) != 0, TpDiff = (d[19] & 0x10) != 0, TpBottom = (d[19] & 0x08) != 0,
                Dp = (d[19] & 0x04) != 0, DpPrivate = (d[19] & 0x02) != 0, DpLast = (d[19] & 0x01) != 0,
            };
            if (d[3] != 0 || (d[18] & 0xF0) != 0 || (d[19] & 0x80) != 0) throw new ImageFormatException("invalid JBIG header (fill bits set)");
            if (h.DL > h.D || h.P == 0 || h.XD == 0 || h.YD == 0 || h.L0 == 0 || h.MX > 127) throw new ImageFormatException("invalid JBIG header");
            // only six of the eight orderings exist (T.82 Table 11)
            int order = d[18] & 7;
            if (order == 1 || order == 7) throw new ImageFormatException("invalid JBIG stripe ordering");
            return h;
        }

        public static long U32(ReadOnlySpan<byte> d, int o) => (long)d[o] << 24 | (long)d[o + 1] << 16 | (long)d[o + 2] << 8 | d[o + 3];
    }

    /// <summary>
    /// JBIG1 decoder (ITU-T T.82): any number of bit planes and resolution layers, both lowest-layer templates,
    /// typical and deterministic prediction, adaptive template moves, all stripe orderings.
    /// </summary>
    internal sealed unsafe class JbigDecoder
    {
        const int Esc = 0xFF, Stuff = 0x00, SdNorm = 0x02, SdRst = 0x03, Abort = 0x04, NewLen = 0x05, AtMove = 0x06, Comment = 0x07;

        /// <summary>Zero columns around every bitmap, wide enough for any template or adaptive pixel reference.</summary>
        const int Pad = 132;

        sealed class Layer
        {
            public int Width, Height, Stride;
            public long LinesPerStripe;
        }

        /// <summary>What persists from stripe to stripe for one plane of one layer.</summary>
        sealed class State
        {
            public byte[] Stats = new byte[4096];
            public byte[] Pixels = Array.Empty<byte>(); // one byte per pixel, 0 or 1, rows of Layer.Stride with Pad columns left of column 0
            public int Tx, Ty;      // adaptive pixel offset, (0, 0) = default position
            public int Lntp = 1;    // lowest layer typical prediction: "line not typical" of the previous line
            public bool Reset;      // the previous stripe ended with SDRST
        }

        JbigHeader _h;
        Layer[] _layers = Array.Empty<Layer>();
        State[,] _states = new State[0, 0]; // [layer, plane]
        byte[][] _dp = Array.Empty<byte[]>();
        byte[] _zeroRow = Array.Empty<byte>();
        long _stripes;
        readonly JpegArith _coder = new JpegArith { UnlimitedPadding = true };
        readonly List<(long Line, int Tx, int Ty)> _moves = new();

        public JbigHeader Header => _h;
        public int Width => (int)_h.XD;
        public int Height => (int)_h.YD;

        /// <summary>Plane k (0 = most significant) of the full resolution image, and its row stride; column 0 is at offset Pad.</summary>
        public (byte[] Pixels, int Stride, int Offset) Plane(int k) => (_states[_h.D, k].Pixels, _layers[_h.D].Stride, Pad);

        /// <summary>Decodes a BIE, or several concatenated ones that add resolution layers. Returns the number of bytes used.</summary>
        public int Decode(ReadOnlySpan<byte> data, long maxPixels, byte[][]? lastDpTable)
        {
            int pos = DecodeEntity(data, maxPixels, lastDpTable, first: true);
            // further BIEs may carry the next resolution layers of the same image
            while (data.Length - pos >= JbigHeader.Size && data[pos] == _h.D + 1 && data[pos + 1] > _h.D && data[pos + 2] == _h.P && data[pos + 3] == 0)
                pos += DecodeEntity(data.Slice(pos), maxPixels, _dp, first: false);
            return pos;
        }

        int DecodeEntity(ReadOnlySpan<byte> data, long maxPixels, byte[][]? lastDpTable, bool first)
        {
            var h = JbigHeader.Parse(data);
            int pos = JbigHeader.Size;
            if (h.Dp)
            {
                if (!h.DpPrivate) _dp = JbigTables.Default();
                else if (!h.DpLast)
                {
                    if (data.Length < pos + 1728) throw new ImageFormatException("truncated JBIG deterministic prediction table");
                    _dp = JbigTables.FromHeader(data.Slice(pos, 1728));
                    pos += 1728;
                }
                else _dp = lastDpTable ?? throw new ImageFormatException("JBIG stream refers to a deterministic prediction table of an earlier image");
            }
            if (h.VLength) h.YD = FindNewLength(data.Slice(pos), h.YD);

            // every stripe of every layer and plane takes at least its two terminating bytes
            long y0 = h.YD;
            for (int d = 0; d < h.D; d++) y0 = (y0 + 1) / 2;
            if ((y0 + h.L0 - 1) / h.L0 > (data.Length - pos) / (2L * (h.D - h.DL + 1) * h.P))
                throw new ImageFormatException("JBIG data is too short for its number of stripes");
            if (first)
            {
                if (h.DL != 0) throw new ImageNotSupportedException("JBIG entity starts at a higher resolution layer; the entity with the lower layers is needed first");
                if (h.P > 16) throw new ImageNotSupportedException($"JBIG with {h.P} bit planes is not supported");
                if (h.XD > int.MaxValue / 2 || h.YD > int.MaxValue / 2 || h.XD * h.YD > maxPixels || (h.XD + 2 * Pad) * h.YD > 4 * maxPixels)
                    throw new ImageNotSupportedException($"JBIG image of {h.XD} x {h.YD} pixels exceeds the size limit");
                Setup(h);
            }
            else
            {
                // same image, more layers: the announced final size must be what the earlier layers implied
                var old = _h;
                long scale = 1L << (h.D - old.D);
                if ((h.XD + scale - 1) / scale != old.XD || (h.YD + scale - 1) / scale != old.YD || h.L0 != old.L0)
                    throw new ImageFormatException("JBIG entities do not belong to the same image");
                if (h.XD > int.MaxValue / 2 || h.YD > int.MaxValue / 2 || h.XD * h.YD > maxPixels || (h.XD + 2 * Pad) * h.YD > 4 * maxPixels)
                    throw new ImageNotSupportedException($"JBIG image of {h.XD} x {h.YD} pixels exceeds the size limit");
                Extend(h);
            }

            // stripe data entities in the order the header announces (T.82 Table 11)
            int layers = h.D - h.DL + 1;
            long total = _stripes * layers * h.P;
            if (total > data.Length) throw new ImageFormatException("JBIG data is too short for its number of stripes"); // each takes 2 bytes at least
            Span<long> count = stackalloc long[3];
            Span<long> index = stackalloc long[3];
            // loop roles: 0 = stripe, 1 = layer, 2 = plane; listed outermost first
            ReadOnlySpan<byte> roles = (h.Seq, h.ILeave, h.SMid) switch
            {
                (false, false, false) => [2, 1, 0],
                (false, true, false) => [1, 2, 0],
                (false, true, true) => [1, 0, 2],
                (true, false, false) => [0, 2, 1],
                (true, false, true) => [2, 0, 1],
                _ => [0, 1, 2],
            };
            for (int i = 0; i < 3; i++) count[i] = roles[i] == 0 ? _stripes : roles[i] == 1 ? layers : h.P;
            index.Clear();
            // First locate every stripe: the stream may bring high layers before the low ones they depend on (HITOLO).
            var entries = new Sde[layers, h.P][];
            for (int d = 0; d < layers; d++)
                for (int p = 0; p < h.P; p++) entries[d, p] = new Sde[_stripes];
            for (long n = 0; n < total; n++)
            {
                long s = 0; int d = 0, p = 0;
                for (int i = 0; i < 3; i++)
                {
                    if (roles[i] == 0) s = index[i];
                    else if (roles[i] == 1) d = h.HiToLo ? h.D - (int)index[i] : h.DL + (int)index[i];
                    else p = (int)index[i];
                }
                entries[d - h.DL, p][s] = LocateStripe(data, ref pos);
                for (int i = 2; i >= 0 && ++index[i] == count[i]; i--) index[i] = 0;
            }
            // then decode layer by layer, plane by plane, top to bottom
            for (int d = h.DL; d <= h.D; d++)
                for (int p = 0; p < h.P; p++)
                    for (long s = 0; s < _stripes; s++)
                        DecodeStripe(data, entries[d - h.DL, p][s], s, d, p);
            return pos;
        }

        /// <summary>One stripe data entity: where its coded data lies, how it ends, and the template moves announced in front of it.</summary>
        struct Sde
        {
            public int Start, End;
            public bool Reset;
            public (long Line, int Tx, int Ty)[]? Moves;
        }

        /// <summary>A NEWLEN marker segment anywhere in the data replaces the image height of the header (T.82 6.2.6.2).</summary>
        static long FindNewLength(ReadOnlySpan<byte> d, long yd)
        {
            for (int i = 0; i + 1 < d.Length;)
            {
                if (d[i] != Esc) { i++; continue; }
                switch (d[i + 1])
                {
                    case NewLen:
                        if (i + 6 > d.Length) return yd;
                        long n = JbigHeader.U32(d, i + 2);
                        if (n == 0 || n > yd) throw new ImageFormatException("invalid JBIG NEWLEN marker");
                        return n;
                    case AtMove: i += 8; break;
                    case Comment:
                        if (i + 6 > d.Length) return yd;
                        long len = JbigHeader.U32(d, i + 2);
                        if (len > d.Length) return yd;
                        i += 6 + (int)len;
                        break;
                    default: i += 2; break;
                }
            }
            return yd;
        }

        void Setup(JbigHeader h)
        {
            _h = h;
            _layers = new Layer[h.D + 1];
            long w = h.XD, ht = h.YD;
            for (int d = h.D; d >= 0; d--)
            {
                _layers[d] = new Layer { Width = (int)w, Height = (int)ht, Stride = (int)w + 2 * Pad };
                w = (w + 1) / 2; ht = (ht + 1) / 2;
            }
            _stripes = (_layers[0].Height + h.L0 - 1) / h.L0;
            for (int d = 0; d <= h.D; d++) _layers[d].LinesPerStripe = h.L0 << Math.Min(d, 40);
            _states = new State[h.D + 1, h.P];
            for (int d = 0; d <= h.D; d++)
                for (int p = 0; p < h.P; p++)
                    _states[d, p] = new State { Pixels = new byte[(long)_layers[d].Stride * _layers[d].Height] };
            _zeroRow = new byte[_layers[h.D].Stride];
        }

        /// <summary>A following entity adds layers on top of the ones decoded so far.</summary>
        void Extend(JbigHeader h)
        {
            var oldLayers = _layers; var oldStates = _states;
            int oldD = _h.D;
            Setup(h);
            for (int d = 0; d <= oldD; d++)
            {
                if (_layers[d].Width != oldLayers[d].Width || _layers[d].Height != oldLayers[d].Height) throw new ImageFormatException("JBIG entities do not belong to the same image");
                for (int p = 0; p < h.P; p++) _states[d, p] = oldStates[d, p];
            }
        }

        /// <summary>Reads the marker segments in front of a stripe and finds the extent of the stripe itself; advances pos past it.</summary>
        Sde LocateStripe(ReadOnlySpan<byte> data, ref int pos)
        {
            List<(long, int, int)>? moves = null;
            // floating marker segments
            while (pos + 1 < data.Length && data[pos] == Esc && data[pos + 1] != Stuff)
            {
                switch (data[pos + 1])
                {
                    case AtMove:
                        if (pos + 8 > data.Length) throw new ImageFormatException("truncated JBIG ATMOVE marker");
                        int tx = (sbyte)data[pos + 6], ty = data[pos + 7];
                        moves ??= new();
                        if (Math.Abs(tx) > _h.MX || ty > _h.MY || (ty == 0 && tx < 0) || moves.Count == 4) throw new ImageFormatException("invalid JBIG ATMOVE marker");
                        moves.Add((JbigHeader.U32(data, pos + 2), tx, ty));
                        pos += 8;
                        break;
                    case NewLen:
                        if (pos + 6 > data.Length) throw new ImageFormatException("truncated JBIG NEWLEN marker");
                        pos += 6; // applied up front, see FindNewLength
                        break;
                    case Comment:
                        if (pos + 6 > data.Length) throw new ImageFormatException("truncated JBIG COMMENT marker");
                        long len = JbigHeader.U32(data, pos + 2);
                        if (len > data.Length - pos - 6) throw new ImageFormatException("truncated JBIG COMMENT marker");
                        pos += 6 + (int)len;
                        break;
                    case Abort:
                        throw new ImageFormatException("JBIG stream was aborted by its encoder");
                    case SdNorm: case SdRst:
                        // an empty stripe: no coded data at all (everything comes from the zero bits the decoder assumes)
                        goto stripe;
                    default:
                        throw new ImageFormatException("unknown JBIG marker");
                }
            }
        stripe:
            // the protected stripe coded data runs up to ESC SDNORM / ESC SDRST; ESC STUFF inside stands for a 0xFF data byte
            int end = pos;
            while (true)
            {
                int i = data.Slice(end).IndexOf((byte)Esc);
                if (i < 0 || end + i + 1 >= data.Length) throw new ImageFormatException("JBIG stripe is not terminated");
                end += i;
                int m = data[end + 1];
                if (m == Stuff) { end += 2; continue; }
                if (m == SdNorm || m == SdRst) break;
                if (m == Abort) throw new ImageFormatException("JBIG stream was aborted by its encoder");
                throw new ImageFormatException("unexpected JBIG marker inside a stripe");
            }
            var sde = new Sde { Start = pos, End = end, Reset = data[end + 1] == SdRst, Moves = moves?.ToArray() };
            pos = end + 2;
            return sde;
        }

        void DecodeStripe(ReadOnlySpan<byte> data, Sde sde, long s, int d, int p)
        {
            _moves.Clear();
            if (sde.Moves != null) _moves.AddRange(sde.Moves);
            var st = _states[d, p];
            var layer = _layers[d];
            long y0 = s * layer.LinesPerStripe, y1 = Math.Min(layer.Height, y0 + layer.LinesPerStripe);
            if (y0 < layer.Height)
            {
                fixed (byte* src = data)
                {
                    _coder.Start(src + sde.Start, src + sde.End);
                    if (d == 0) DecodeLowest(st, layer, (int)y0, (int)y1);
                    else DecodeDifferential(st, layer, _states[d - 1, p], _layers[d - 1], (int)y0, (int)y1);
                }
            }
            st.Reset = sde.Reset;
            if (st.Reset)
            {
                // SDRST: the next stripe of this plane and layer starts like the top of an image
                Array.Clear(st.Stats);
                st.Tx = st.Ty = 0;
                st.Lntp = 1;
            }
        }

        /// <summary>Row y of a bitmap for reading; rows above the image, or above a stripe that follows a reset, read as background.</summary>
        byte* Row(byte* pixels, int stride, int y, int stripeStart, bool reset, byte* zero)
            => y < 0 || (reset && y < stripeStart) ? zero + Pad : pixels + (nint)y * stride + Pad;

        void ApplyMoves(State st, int lineInStripe)
        {
            foreach (var (line, tx, ty) in _moves)
                if (line == lineInStripe) { st.Tx = tx; st.Ty = ty; }
        }

        void DecodeLowest(State st, Layer layer, int y0, int y1)
        {
            int w = layer.Width, stride = layer.Stride;
            bool twoLine = _h.TwoLine, tp = _h.TpBottom;
            fixed (byte* pixels = st.Pixels, stats = st.Stats, zero = _zeroRow)
            {
                for (int y = y0; y < y1; y++)
                {
                    if (_moves.Count != 0) ApplyMoves(st, y - y0);
                    byte* row = pixels + (nint)y * stride + Pad;
                    byte* up1 = Row(pixels, stride, y - 1, y0, st.Reset, zero), up2 = Row(pixels, stride, y - 2, y0, st.Reset, zero);
                    if (tp)
                    {
                        // the pseudo-pixel says whether this line's "same as the line above" status changed
                        int slntp = _coder.Decode(stats + (twoLine ? 0x195 : 0x0E5));
                        st.Lntp = slntp ^ st.Lntp ^ 1;
                        if (st.Lntp == 0)
                        {
                            new ReadOnlySpan<byte>(up1, w).CopyTo(new Span<byte>(row, w));
                            continue;
                        }
                    }
                    // the adaptive pixel: by default right of the template in the line above
                    bool moved = st.Tx != 0 || st.Ty != 0;
                    byte* at = moved ? Row(pixels, stride, y - st.Ty, y0, st.Reset, zero) - st.Tx : up1 + 2;
                    DecodeLowestLine(row, up1, up2, at, w, stats, twoLine);
                }
            }
        }

        /// <summary>
        /// One line of the lowest resolution layer. The template is kept in shift registers and the arithmetic decoder
        /// runs on local copies of its registers (T.82 6.8.3, the same coder as in JpegArith.Decode).
        /// </summary>
        void DecodeLowestLine(byte* row, byte* up1, byte* up2, byte* at, int w, byte* stats, bool twoLine)
        {
            uint* table = JpegArith.Table;
            _coder.GetRegisters(out uint a, out uint c, out int ct);
            // template bits that move along: two lines above, the line above, the current line
            int r2 = up2[-1] << 2 | up2[0] << 1 | up2[1];
            int r1 = twoLine ? up1[-3] << 4 | up1[-2] << 3 | up1[-1] << 2 | up1[0] << 1 | up1[1] : up1[-2] << 3 | up1[-1] << 2 | up1[0] << 1 | up1[1];
            int r0 = 0;
            for (int x = 0; x < w; x++)
            {
                int cx = twoLine ? r1 << 5 | at[x] << 4 | r0 : r2 << 7 | r1 << 3 | at[x] << 2 | r0;
                byte* st = stats + cx;
                uint sv = *st, e = table[sv], qe = e & 0xFFFF;
                int d = (int)(sv >> 7);
                a -= qe;
                if (c >> 16 < a)
                {
                    if (a >= 0x8000) goto decided; // the common case: more probable symbol, no renormalization
                    if (a < qe) { d ^= 1; *st = (byte)(e >> 24); }
                    else *st = (byte)(e >> 16);
                }
                else
                {
                    c -= a << 16;
                    if (a < qe) *st = (byte)(e >> 16);
                    else { d ^= 1; *st = (byte)(e >> 24); }
                    a = qe;
                }
                do
                {
                    if (ct == 0)
                    {
                        _coder.SetRegisters(a, c, ct);
                        _coder.NextByte();
                        _coder.GetRegisters(out a, out c, out _);
                        ct = 8;
                    }
                    a <<= 1; c <<= 1; ct--;
                } while (a < 0x8000);
            decided:
                row[x] = (byte)d;
                r0 = (r0 << 1 | d) & (twoLine ? 15 : 3);
                r1 = (r1 << 1 | up1[x + 2]) & (twoLine ? 31 : 15);
                r2 = (r2 << 1 | up2[x + 2]) & 7;
            }
            _coder.SetRegisters(a, c, ct);
        }

        // context of the differential layer typical prediction pseudo-pixel: phase 3, all high resolution template
        // pixels foreground, all low resolution ones background (T.82 Figure 8)
        const int TpDiffContext = 3 << 10 | 0x3F0;

        void DecodeDifferential(State st, Layer layer, State low, Layer lowLayer, int y0, int y1)
        {
            int w = layer.Width, stride = layer.Stride, lowStride = lowLayer.Stride;
            bool tp = _h.TpDiff, dp = _h.Dp;
            // the low resolution lines this stripe may look at: references below it repeat its last line
            int lowLast = Math.Min(lowLayer.Height, (y1 + 1) / 2) - 1, lowFirst = y0 / 2;
            int lntp = 1;
            fixed (byte* pixels = st.Pixels, stats = st.Stats, zero = _zeroRow, lowPixels = low.Pixels)
            fixed (byte* dp0 = dp ? _dp[0] : null, dp1 = dp ? _dp[1] : null, dp2 = dp ? _dp[2] : null, dp3 = dp ? _dp[3] : null)
            {
                for (int y = y0; y < y1; y++)
                {
                    if (_moves.Count != 0) ApplyMoves(st, y - y0);
                    int j = y >> 1, py = y & 1;
                    byte* row = pixels + (nint)y * stride + Pad;
                    byte* up1 = Row(pixels, stride, y - 1, y0, st.Reset, zero), up2 = Row(pixels, stride, y - 2, y0, st.Reset, zero);
                    byte* lj = lowPixels + (nint)j * lowStride + Pad;
                    byte* lj1 = lowPixels + (nint)Math.Min(j + 1, lowLast) * lowStride + Pad;
                    byte* ljm = Row(lowPixels, lowStride, j - 1, lowFirst, st.Reset, zero);
                    // the 3 x 3 high resolution neighbourhood of deterministic prediction: the line above the pair, and the pair
                    byte* b0 = Row(pixels, stride, 2 * j - 1, y0, st.Reset, zero), b1 = pixels + (nint)(2 * j) * stride + Pad;
                    byte* at = st.Tx != 0 || st.Ty != 0 ? Row(pixels, stride, y - st.Ty, y0, st.Reset, zero) - st.Tx : up1 - 1;

                    if (tp && py == 0) lntp = _coder.Decode(stats + TpDiffContext);
                    for (int x = 0; x < w; x++)
                    {
                        int i = x >> 1, px = x & 1;
                        if (tp && lntp == 0)
                        {
                            // a low resolution pixel in a uniform neighbourhood stands for four pixels of the same color
                            int c = lj[i];
                            if (ljm[i - 1] == c && ljm[i] == c && ljm[i + 1] == c && lj[i - 1] == c && lj[i + 1] == c
                                && lj1[i - 1] == c && lj1[i] == c && lj1[i + 1] == c)
                            {
                                row[x] = (byte)c;
                                continue;
                            }
                        }
                        if (dp)
                        {
                            int idx = ljm[i - 1] | ljm[i] << 1 | lj[i - 1] << 2 | lj[i] << 3
                                | b0[2 * i - 1] << 4 | b0[2 * i] << 5 | b0[2 * i + 1] << 6 | b1[2 * i - 1] << 7;
                            int v;
                            if (py == 0)
                            {
                                if (px == 0) v = dp0[idx];
                                else v = dp1[idx | b1[2 * i] << 8];
                            }
                            else
                            {
                                idx |= b1[2 * i] << 8 | b1[2 * i + 1] << 9 | row[2 * i - 1] << 10;
                                if (px == 0) v = dp2[idx];
                                else v = dp3[idx | row[2 * i] << 11];
                            }
                            if (v < 2)
                            {
                                row[x] = (byte)v;
                                continue;
                            }
                        }
                        // low resolution template pixels: the two columns around x, lines j and j + 1
                        int il = (x - 1) >> 1;
                        int cx = (py << 1 | px) << 10 | up2[x] << 9 | at[x] << 8 | up1[x] << 7 | up1[x + 1] << 6 | row[x - 2] << 5 | row[x - 1] << 4
                            | lj[il] << 3 | lj[il + 1] << 2 | lj1[il] << 1 | lj1[il + 1];
                        row[x] = (byte)_coder.Decode(stats + cx);
                    }
                }
            }
        }
    }
}
