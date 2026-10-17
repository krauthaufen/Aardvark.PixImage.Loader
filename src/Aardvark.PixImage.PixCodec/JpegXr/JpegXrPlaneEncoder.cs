using System;
using System.Numerics;
using System.Threading.Tasks;

namespace Aardvark.ImageCodecs.JpegXrImpl
{
    /// <summary>
    /// The encoder side of one image plane: forward lapped transform (Annex D), quantization, coefficient prediction and the
    /// entropy coding that the parsing process of clause 8 reads back. The adaptive coding state lives in
    /// <see cref="JxrTileCoder"/> instances, one per tile being coded, so that tiles can be coded in parallel.
    /// </summary>
    internal sealed class JxrPlaneEncoder
    {
        public readonly JxrImageHeader Ih;
        public readonly JxrPlaneHeader Ph;
        public readonly JxrInternalFormat Fmt;
        public readonly int Nc;
        public readonly bool Is42x, Is420, Is422;
        public readonly int MbW, MbH;
        public readonly int[][] Px;
        public readonly int[] PxW, PxH;
        public readonly int[][] Dc;
        public readonly int[] DcW, DcH;
        public readonly int[] MbPxW, MbPxH;

        // quantization (one QP set per band for the whole plane)
        public readonly int[] DcFactor, LpFactor, HpFactor;
        public bool HpDeadzone = true;

        // per macroblock state shared by the tiles
        public readonly int[] Pred;
        public readonly int[] Cbphp;
        public readonly byte[] HpMode;
        public readonly byte[] ModelBitsHp;
        /// <summary>Prediction residuals of the quantized DC and LP coefficients per macroblock (the HP ones replace the HP coefficients in Px).</summary>
        public readonly int[] DcRes, LpRes;

        public JxrPlaneEncoder(JxrImageHeader ih, JxrPlaneHeader ph)
        {
            Ih = ih;
            Ph = ph;
            Fmt = ph.Format;
            Nc = ph.NumComponents;
            Is420 = Fmt == JxrInternalFormat.Yuv420;
            Is422 = Fmt == JxrInternalFormat.Yuv422;
            Is42x = Is420 || Is422;
            MbW = ih.MbWidth;
            MbH = ih.MbHeight;
            int mbs = MbW * MbH;
            Px = new int[Nc][];
            PxW = new int[Nc];
            PxH = new int[Nc];
            Dc = new int[Nc][];
            DcW = new int[Nc];
            DcH = new int[Nc];
            MbPxW = new int[Nc];
            MbPxH = new int[Nc];
            for (int c = 0; c < Nc; c++)
            {
                MbPxW[c] = c > 0 && Is42x ? 8 : 16;
                MbPxH[c] = c > 0 && Is420 ? 8 : 16;
                PxW[c] = MbW * MbPxW[c];
                PxH[c] = MbH * MbPxH[c];
                Px[c] = new int[checked(PxW[c] * PxH[c])];
                DcW[c] = PxW[c] / 4;
                DcH[c] = PxH[c] / 4;
                Dc[c] = new int[DcW[c] * DcH[c]];
            }
            DcFactor = new int[Nc];
            LpFactor = new int[Nc];
            HpFactor = new int[Nc];
            for (int c = 0; c < Nc; c++)
            {
                DcFactor[c] = JxrQp.Map(ph.DcQp![c], c == 0 ? 1 : 0, ph.Scaled);
                LpFactor[c] = ph.LpQp != null ? JxrQp.Map(ph.LpQp[c], c == 0 ? 1 : 0, ph.Scaled) : 1;
                HpFactor[c] = ph.HpQp != null ? JxrQp.Map(ph.HpQp[c], 1, ph.Scaled) : 1;
            }
            Pred = new int[mbs * Nc * 7];
            Cbphp = new int[mbs * Nc];
            HpMode = new byte[mbs];
            ModelBitsHp = new byte[mbs * 2];
            DcRes = new int[mbs * Nc];
            LpRes = new int[mbs * Nc * 16];
        }

        /// <summary>Offset of block <paramref name="blk"/> (raster index in the macroblock) of component c of macroblock (mbx, mby) in Px.</summary>
        public int BlockOffset(int c, int mbx, int mby, int blk)
        {
            int bw = MbPxW[c] >> 2;
            return (mby * MbPxH[c] + 4 * (blk / bw)) * PxW[c] + mbx * MbPxW[c] + 4 * (blk % bw);
        }

        // ------------------------------------------------------------------ forward transform (D.4, D.5)

        public void Forward(bool parallel)
        {
            var ih = Ih;
            // second level: pre-filter on the samples, then the core transform of every 4x4 block
            if (ih.Overlap != 0)
                for (int c = 0; c < Nc; c++)
                {
                    int mw = MbPxW[c], mh = MbPxH[c];
                    var px = Px[c];
                    int s = PxW[c];
                    ForRegions((x0, y0, x1, y1) =>
                    {
                        int X0 = x0 * mw, Y0 = y0 * mh, X1 = x1 * mw, Y1 = y1 * mh;
                        // edges first: the decoder runs them after the interior filters, but the two sets are disjoint
                        JxrTransform.Overlap4Edges(px, s, X0, Y0, X1, Y1, true);
                        if (parallel && Y1 - Y0 >= 256)
                        {
                            int bands = (Y1 - Y0) / 128;
                            Parallel.For(0, bands, k =>
                            {
                                int from = Y0 + (Y1 - Y0) * k / bands, to = Y0 + (Y1 - Y0) * (k + 1) / bands;
                                JxrTransform.Overlap4Interior(px, s, X0, Y0, X1, Y1, from, to, true);
                            });
                        }
                        else JxrTransform.Overlap4Interior(px, s, X0, Y0, X1, Y1, Y0 + 2, Y1 - 2, true);
                    });
                }
            for (int c = 0; c < Nc; c++)
            {
                var px = Px[c];
                var dc = Dc[c];
                int s = PxW[c], dw = DcW[c], dh = DcH[c];
                void Rows(int r0, int r1)
                {
                    for (int y = r0; y < r1; y++)
                    {
                        int po = 4 * y * s;
                        for (int x = 0; x < dw; x++)
                        {
                            JxrTransform.Fct4x4(px, po + 4 * x, s);
                            dc[y * dw + x] = px[po + 4 * x];
                        }
                    }
                }
                if (parallel && dh >= 64) Parallel.For(0, (dh + 31) / 32, k => Rows(k * 32, Math.Min(dh, k * 32 + 32)));
                else Rows(0, dh);
            }
            // first level: pre-filter on the DC-LP arrays, then the macroblock transforms
            if (ih.Overlap == 2)
                for (int c = 0; c < Nc; c++)
                {
                    int bx = DcW[c] / MbW, by = DcH[c] / MbH;
                    ForRegions((x0, y0, x1, y1) =>
                    {
                        if (c == 0 || !Is42x) JxrTransform.Overlap4(Dc[c], DcW[c], x0 * bx, y0 * by, x1 * bx, y1 * by, true);
                        else JxrTransform.Overlap2(Dc[c], DcW[c], x0 * bx, y0 * by, x1 * bx, y1 * by, true);
                    });
                }
            Span<int> a = stackalloc int[16];
            for (int c = 0; c < Nc; c++)
            {
                var dc = Dc[c];
                int w = DcW[c];
                for (int mby = 0; mby < MbH; mby++)
                    for (int mbx = 0; mbx < MbW; mbx++)
                    {
                        if (c == 0 || !Is42x)
                        {
                            int o = 4 * mby * w + 4 * mbx;
                            for (int j = 0; j < 16; j++) a[j] = dc[o + (j >> 2) * w + (j & 3)];
                            if (c > 0 && Ph.Scaled) for (int j = 0; j < 16; j++) a[j] = (a[j] + 1) >> 1;
                            JxrTransform.Fct4x4(a.Slice(0, 16));
                            for (int j = 0; j < 16; j++) dc[o + (j >> 2) * w + (j & 3)] = a[j];
                        }
                        else if (Is420)
                        {
                            int o = 2 * mby * w + 2 * mbx;
                            a[0] = dc[o]; a[1] = dc[o + 1]; a[2] = dc[o + w]; a[3] = dc[o + w + 1];
                            if (Ph.Scaled) for (int j = 0; j < 4; j++) a[j] = (a[j] + 1) >> 1;
                            (a[1], a[2]) = (a[2], a[1]);
                            JxrTransform.T2x2hInverse(ref a[0], ref a[1], ref a[2], ref a[3], 0);
                            dc[o] = a[0]; dc[o + 1] = a[1]; dc[o + w] = a[2]; dc[o + w + 1] = a[3];
                        }
                        else
                        {
                            int o = 4 * mby * w + 2 * mbx;
                            for (int j = 0; j < 8; j++) a[j] = dc[o + (j >> 1) * w + (j & 1)];
                            if (Ph.Scaled) for (int j = 0; j < 8; j++) a[j] = (a[j] + 1) >> 1;
                            (a[5], a[6]) = (a[6], a[5]);
                            JxrTransform.T2x2hInverse(ref a[4], ref a[6], ref a[5], ref a[7], 0);
                            (a[1], a[2]) = (a[2], a[1]);
                            JxrTransform.T2x2hInverse(ref a[0], ref a[1], ref a[2], ref a[3], 0);
                            // inverse of T2pt( ): a -= (b + 1) >> 1; b += a
                            a[4] -= a[0];
                            a[0] += (a[4] + 1) >> 1;
                            for (int j = 0; j < 8; j++) dc[o + (j >> 1) * w + (j & 1)] = a[j];
                        }
                    }
            }
        }

        void ForRegions(Action<int, int, int, int> f)
        {
            var ih = Ih;
            if (!ih.HardTiling) { f(0, 0, MbW, MbH); return; }
            for (int ty = 0; ty < ih.TileRows; ty++)
                for (int tx = 0; tx < ih.TileCols; tx++)
                    f(ih.TileLeft[tx], ih.TileTop[ty], ih.TileLeft[tx + 1], ih.TileTop[ty + 1]);
        }

        /// <summary>Quantization with rounding to nearest (DC, LP) or a small dead zone (HP).</summary>
        public static int Quant(int v, int f, bool deadzone)
        {
            if (f == 1) return v;
            int a = Math.Abs(v);
            int q = deadzone ? (int)(((long)a * 3 + f) / (3L * f) ) : (int)((a + (long)(f >> 1)) / f);
            return v < 0 ? -q : q;
        }
    }

    /// <summary>The adaptive coding state of one plane while one tile is coded, and the macroblock coding itself.</summary>
    internal sealed class JxrTileCoder
    {
        readonly JxrPlaneEncoder _p;
        readonly JxrInternalFormat _fmt;
        readonly int _nc;
        readonly bool _is42x, _is420, _is422;

        AdaptiveVlc _absDcLum, _absDcChr;
        AdaptiveVlc _firstLpLum, _indLpLum0, _indLpLum1, _firstLpChr, _indLpChr0, _indLpChr1, _absLp0, _absLp1;
        AdaptiveVlc _firstHpLum, _indHpLum0, _indHpLum1, _firstHpChr, _indHpChr0, _indHpChr1, _absHp0, _absHp1;
        AdaptiveVlc _numCbphp, _numBlkCbphp;
        JxrModel _modelDc, _modelLp, _modelHp;
        int _cbphpState0, _cbphpState1, _countOnes0, _countOnes1, _countZeroes0, _countZeroes1;
        int _countZeroCbplp, _countMaxCbplp;
        readonly int[] _lpScan = new int[16], _lpTotals = new int[16];
        readonly int[] _hpHor = new int[16], _hpHorTotals = new int[16], _hpVer = new int[16], _hpVerTotals = new int[16];

        // current macroblock
        int _mbx, _mby, _mbi, _tileLeft, _tileRight;
        bool _leftEdge, _topEdge, _tileStart, _resetContext, _resetTotals;
        readonly int[] _vals = new int[16];
        readonly int[] _diff;

        public JxrTileCoder(JxrPlaneEncoder p)
        {
            _p = p;
            _fmt = p.Fmt;
            _nc = p.Nc;
            _is420 = p.Is420;
            _is422 = p.Is422;
            _is42x = p.Is42x;
            _diff = new int[_nc];
        }

        public void SetMacroblock(int mbx, int mby, int tileLeft, int tileRight, int tileTop)
        {
            _mbx = mbx;
            _mby = mby;
            _mbi = mby * _p.MbW + mbx;
            _tileLeft = tileLeft;
            _tileRight = tileRight;
            _leftEdge = mbx == tileLeft;
            _topEdge = mby == tileTop;
            _tileStart = _leftEdge && _topEdge;
            _resetTotals = (mbx - tileLeft) % 16 == 0;
            _resetContext = mbx == tileRight - 1 || _resetTotals;
        }

        // ------------------------------------------------------------------ coefficients of a macroblock

        /// <summary>
        /// Quantizes the DC and LP coefficients of the current macroblock, forms the prediction residuals (mirroring 9.6.1 /
        /// 9.6.2 on the quantized values) and the HP prediction direction.
        /// </summary>
        public void PrepareDcLp()
        {
            var p = _p;
            int nc = _nc;
            Span<int> q = stackalloc int[nc * 16];
            for (int c = 0; c < nc; c++)
            {
                var dc = p.Dc[c];
                int w = p.DcW[c];
                int n = c > 0 && _is420 ? 4 : c > 0 && _is422 ? 8 : 16;
                int bw = c > 0 && _is42x ? 2 : 4;
                int bh = n / bw;
                int o = bh * _mby * w + bw * _mbx;
                for (int j = 0; j < n; j++)
                {
                    int v = dc[o + (j / bw) * w + (j % bw)];
                    q[c * 16 + j] = JxrPlaneEncoder.Quant(v, j == 0 ? p.DcFactor[c] : p.LpFactor[c], false);
                }
            }
            if (p.Ph.Bands == JxrBands.DcOnly)
                for (int c = 0; c < nc; c++) q.Slice(c * 16 + 1, 15).Clear();

            var pred = p.Pred;
            int predBase = _mbi * nc * 7, leftBase = predBase - nc * 7, topBase = predBase - p.MbW * nc * 7;
            int dcMode;
            if (_leftEdge && _topEdge) dcMode = 3;
            else if (_leftEdge) dcMode = 1;
            else if (_topEdge) dcMode = 0;
            else
            {
                int tlBase = topBase - nc * 7;
                int left = pred[leftBase], top = pred[topBase], topLeft = pred[tlBase];
                int strHor, strVer;
                if (_fmt is JxrInternalFormat.YOnly or JxrInternalFormat.NComponent)
                {
                    strHor = Math.Abs(topLeft - left);
                    strVer = Math.Abs(topLeft - top);
                }
                else
                {
                    int scale = _is420 ? 8 : _is422 ? 4 : 2;
                    strHor = Math.Abs(topLeft - left) * scale + Math.Abs(pred[tlBase + 7] - pred[leftBase + 7]) + Math.Abs(pred[tlBase + 14] - pred[leftBase + 14]);
                    strVer = Math.Abs(topLeft - top) * scale + Math.Abs(pred[tlBase + 7] - pred[topBase + 7]) + Math.Abs(pred[tlBase + 14] - pred[topBase + 14]);
                }
                dcMode = strHor * 4 < strVer ? 1 : strVer * 4 < strHor ? 0 : 2;
            }
            for (int c = 0; c < nc; c++)
            {
                int v = q[c * 16], pr = 0;
                if (dcMode == 0) pr = pred[leftBase + c * 7];
                else if (dcMode == 1) pr = pred[topBase + c * 7];
                else if (dcMode == 2)
                {
                    int l = pred[leftBase + c * 7], t = pred[topBase + c * 7];
                    pr = c == 0 || !_is42x ? (t + l) >> 1 : (t + l + 1) >> 1;
                }
                p.DcRes[_mbi * nc + c] = v - pr;
                pred[predBase + c * 7] = v;
            }
            // LP (all macroblocks of a plane share QP index 0)
            int lpMode = dcMode == 0 ? 0 : dcMode == 1 ? 1 : 2;
            var _lp = p.LpRes.AsSpan(_mbi * nc * 16, nc * 16);
            _lp.Clear();
            for (int c = 0; c < nc; c++)
            {
                int m = c * 16, l = leftBase + c * 7, t = topBase + c * 7, pb = predBase + c * 7;
                int n = c > 0 && _is420 ? 4 : c > 0 && _is422 ? 8 : 16;
                for (int j = 1; j < n; j++) _lp[m + j] = q[m + j];
                if (c == 0 || !_is42x)
                {
                    if (lpMode == 0) { _lp[m + 4] -= pred[l + 4]; _lp[m + 8] -= pred[l + 5]; _lp[m + 12] -= pred[l + 6]; }
                    else if (lpMode == 1) { _lp[m + 1] -= pred[t + 1]; _lp[m + 2] -= pred[t + 2]; _lp[m + 3] -= pred[t + 3]; }
                    pred[pb + 1] = q[m + 1]; pred[pb + 2] = q[m + 2]; pred[pb + 3] = q[m + 3];
                    pred[pb + 4] = q[m + 4]; pred[pb + 5] = q[m + 8]; pred[pb + 6] = q[m + 12];
                }
                else if (_is420)
                {
                    if (lpMode == 0) _lp[m + 2] -= pred[l + 2];
                    else if (lpMode == 1) _lp[m + 1] -= pred[t + 1];
                    pred[pb + 1] = q[m + 1]; pred[pb + 2] = q[m + 2];
                }
                else
                {
                    if (lpMode == 0) { _lp[m + 4] -= pred[l + 4]; _lp[m + 2] -= pred[l + 2]; _lp[m + 6] -= pred[l + 6]; }
                    else if (lpMode == 1)
                    {
                        _lp[m + 4] -= pred[t + 4];
                        _lp[m + 1] -= pred[t + 5];
                        _lp[m + 5] -= q[m + 1];
                    }
                    else if (dcMode == 1) _lp[m + 5] -= q[m + 1];
                    pred[pb + 1] = q[m + 1]; pred[pb + 2] = q[m + 2]; pred[pb + 4] = q[m + 4];
                    pred[pb + 5] = q[m + 5]; pred[pb + 6] = q[m + 6];
                }
            }
            // HP prediction direction from the quantized LP coefficients
            int strH = Math.Abs(q[1]) + Math.Abs(q[2]) + Math.Abs(q[3]);
            int strV = Math.Abs(q[4]) + Math.Abs(q[8]) + Math.Abs(q[12]);
            if (_fmt != JxrInternalFormat.YOnly && _fmt != JxrInternalFormat.NComponent)
                for (int c = 1; c <= 2; c++)
                {
                    int m = c * 16;
                    strH += Math.Abs(q[m + 1]);
                    if (_is420) strV += Math.Abs(q[m + 2]);
                    else if (_is422)
                    {
                        strV += Math.Abs(q[m + 2]) + Math.Abs(q[m + 6]);
                        strH += Math.Abs(q[m + 5]);
                    }
                    else strV += Math.Abs(q[m + 4]);
                }
            p.HpMode[_mbi] = (byte)(strH * 4 < strV ? 0 : strV * 4 < strH ? 1 : 2);
        }

        /// <summary>Quantizes the HP coefficients of the current macroblock with the closed loop prediction of 9.6.3.3.</summary>
        public void PrepareHp()
        {
            var p = _p;
            int mode = p.HpMode[_mbi];
            for (int c = 0; c < _nc; c++)
            {
                var px = p.Px[c];
                int s = p.PxW[c];
                int f = p.HpFactor[c];
                int bw = p.MbPxW[c] >> 2, bh = p.MbPxH[c] >> 2;
                int o0 = _mby * p.MbPxH[c] * s + _mbx * p.MbPxW[c];
                // final (dequantized, predicted) values of the predicted coefficient positions, per block
                Span<int> fin = stackalloc int[16 * 3];
                for (int by = 0; by < bh; by++)
                    for (int bx = 0; bx < bw; bx++)
                    {
                        int blk = by * bw + bx;
                        int o = o0 + 4 * by * s + 4 * bx;
                        for (int k = 1; k < 16; k++)
                        {
                            ref int cell = ref px[o + (k >> 2) * s + (k & 3)];
                            int v = cell;
                            bool predicted = mode == 0 && bx > 0 && (k & 3) == 0 || mode == 1 && by > 0 && k < 4;
                            int pv = 0;
                            if (predicted) pv = mode == 0 ? fin[(blk - 1) * 3 + (k >> 2) - 1] : fin[(blk - bw) * 3 + k - 1];
                            int r = JxrPlaneEncoder.Quant(v - pv, f, p.HpDeadzone);
                            cell = r;
                            if (mode == 0 && (k & 3) == 0) fin[blk * 3 + (k >> 2) - 1] = r * f + pv;
                            else if (mode == 1 && k < 4) fin[blk * 3 + k - 1] = r * f + pv;
                        }
                    }
            }
        }

        // ------------------------------------------------------------------ DC band

        public void WriteMbDc(JxrBitWriter w)
        {
            var _dc = _p.DcRes.AsSpan(_mbi * _nc, _nc);
            if (_tileStart)
            {
                _absDcLum.Init1();
                _absDcChr.Init1();
                _modelDc.Init(0);
            }
            int lap0 = 0, lap1 = 0;
            if (_fmt is JxrInternalFormat.YOnly or JxrInternalFormat.Yuvk or JxrInternalFormat.NComponent)
            {
                for (int n = 0; n < _nc; n++)
                {
                    int bits = n == 0 ? _modelDc.Bits0 : _modelDc.Bits1;
                    bool abs = (Math.Abs(_dc[n]) >> bits) != 0;
                    w.Bit(abs);
                    if (abs) { if (n == 0) lap0++; else lap1++; }
                    WriteDc(w, _dc[n], bits, ref _absDcLum);
                }
            }
            else
            {
                int b0 = _modelDc.Bits0, b1 = _modelDc.Bits1;
                bool a0 = (Math.Abs(_dc[0]) >> b0) != 0, a1 = (Math.Abs(_dc[1]) >> b1) != 0, a2 = (Math.Abs(_dc[2]) >> b1) != 0;
                w.Write(JxrTables.ValDcYuv, (a0 ? 4 : 0) | (a1 ? 2 : 0) | (a2 ? 1 : 0));
                if (a0) lap0++;
                WriteDc(w, _dc[0], b0, ref _absDcLum);
                if (a1) lap1++;
                WriteDc(w, _dc[1], b1, ref _absDcChr);
                if (a2) lap1++;
                WriteDc(w, _dc[2], b1, ref _absDcChr);
            }
            _modelDc.Update(lap0, lap1, 0, _fmt, _nc);
            if (_resetContext)
            {
                _absDcLum.Adapt1();
                _absDcChr.Adapt1();
            }
        }

        static void WriteDc(JxrBitWriter w, int v, int bits, ref AdaptiveVlc a)
        {
            int abs = Math.Abs(v);
            int hi = abs >> bits;
            if (hi != 0) WriteAbsLevel(w, hi + 1, ref a);
            if (bits != 0) w.Bits(abs & ((1 << bits) - 1), bits);
            if (abs != 0) w.Bit(v < 0);
        }

        static void WriteAbsLevel(JxrBitWriter w, int level, ref AdaptiveVlc a)
        {
            int idx;
            if (level < 18)
            {
                idx = level < 4 ? level - 2 : level < 6 ? 2 : level < 10 ? 3 : level < 14 ? 4 : 5;
                w.Write(JxrTables.AbsLevelIndex[a.Table], idx);
                a.Disc1 += JxrTables.AbsLevelIndexDelta[idx];
                int fl = JxrTables.AbsFixedLen[idx];
                if (fl > 0) w.Bits(level - JxrTables.AbsRemap[idx], fl);
                return;
            }
            idx = 6;
            w.Write(JxrTables.AbsLevelIndex[a.Table], idx);
            a.Disc1 += JxrTables.AbsLevelIndexDelta[idx];
            int f = 31 - BitOperations.LeadingZeroCount((uint)(level - 2));
            if (f < 19) w.Bits(f - 4, 4);
            else
            {
                w.Bits(15, 4);
                if (f < 22) w.Bits(f - 19, 2);
                else { w.Bits(3, 2); w.Bits(f - 22, 3); }
            }
            w.Bits(level - 2 - (1 << f), f);
        }

        // ------------------------------------------------------------------ block coding

        ref AdaptiveVlc AbsVlc(int band, int ctx)
            => ref band == 1 ? ref (ctx != 0 ? ref _absLp1 : ref _absLp0) : ref (ctx != 0 ? ref _absHp1 : ref _absHp0);

        /// <summary>
        /// The inverse of DECODE_BLOCK( ): the values at locations location0 .. 15 (index 0 = location0); at least one is
        /// non-zero. Returns the number of non-zero values.
        /// </summary>
        int WriteBlock(JxrBitWriter w, bool chroma, ReadOnlySpan<int> vals, int band, int location0)
        {
            int count = 16 - location0;
            Span<int> pos = stackalloc int[17];
            int n = 0;
            for (int i = 0; i < count; i++) if (vals[i] != 0) pos[n++] = i;
            // whether the run before the next non-zero value is zero (1) / non-zero (2) / there is none (0)
            Span<int> next = stackalloc int[16];
            for (int k = 0; k < n; k++) next[k] = k + 1 >= n ? 0 : pos[k + 1] - pos[k] - 1 == 0 ? 1 : 2;
            int run0 = pos[0];
            int srn = next[0];
            int sr = run0 == 0 ? 1 : 0;
            int v0 = vals[pos[0]];
            int first = srn << 2 | (Math.Abs(v0) > 1 ? 2 : 0) | sr;
            ref AdaptiveVlc fv = ref band == 1 ? ref (chroma ? ref _firstLpChr : ref _firstLpLum) : ref (chroma ? ref _firstHpChr : ref _firstHpLum);
            w.Write(JxrTables.FirstIndex[fv.Table], first);
            fv.Disc1 += JxrTables.FirstIndexDelta[fv.Delta][first];
            fv.Disc2 += JxrTables.FirstIndexDelta[fv.Delta2][first];
            w.Bit(v0 < 0);
            int ctx = sr & srn;
            if (Math.Abs(v0) > 1) WriteAbsLevel(w, Math.Abs(v0), ref AbsVlc(band, ctx));
            int location = location0;
            if (sr == 0) WriteRun(w, run0, 15 - location);
            location += run0 + 1;
            for (int k = 1; k < n; k++)
            {
                int run = pos[k] - pos[k - 1] - 1;
                if (run != 0) WriteRun(w, run, 15 - location);
                location += run + 1;
                int v = vals[pos[k]];
                int nsrn = next[k];
                int mag = Math.Abs(v) > 1 ? 1 : 0;
                if (location < 15)
                {
                    int idx = nsrn << 1 | mag;
                    ref AdaptiveVlc iv = ref band == 1
                        ? ref (chroma ? ref (ctx != 0 ? ref _indLpChr1 : ref _indLpChr0) : ref (ctx != 0 ? ref _indLpLum1 : ref _indLpLum0))
                        : ref (chroma ? ref (ctx != 0 ? ref _indHpChr1 : ref _indHpChr0) : ref (ctx != 0 ? ref _indHpLum1 : ref _indHpLum0));
                    w.Write(JxrTables.IndexA[iv.Table], idx);
                    iv.Disc1 += JxrTables.Index1Delta[iv.Delta][idx];
                    iv.Disc2 += JxrTables.Index1Delta[iv.Delta2][idx];
                }
                else if (location == 15) w.Write(JxrTables.IndexB, nsrn << 1 | mag);
                else w.Bit(mag != 0);
                ctx &= nsrn;
                w.Bit(v < 0);
                if (mag != 0) WriteAbsLevel(w, Math.Abs(v), ref AbsVlc(band, ctx));
            }
            return n;
        }

        static void WriteRun(JxrBitWriter w, int run, int maxRun)
        {
            if (maxRun < 5)
            {
                if (maxRun > 1) w.Write(JxrTables.RunValue[maxRun], run - 1);
                return;
            }
            int b = 5 * JxrTables.RunBin[maxRun];
            for (int i = 4; i >= 0; i--)
                if (run >= JxrTables.RunRemap[b + i])
                {
                    w.Write(JxrTables.RunIndex, i);
                    int f = JxrTables.RunFixedLength[b + i];
                    if (f > 0) w.Bits(run - JxrTables.RunRemap[b + i], f);
                    return;
                }
        }

        // ------------------------------------------------------------------ LP band

        static readonly int[] RemapArr = { 4, 1, 2, 3, 5, 6, 7 };

        public void WriteMbLp(JxrBitWriter w)
        {
            if (_tileStart)
            {
                _countZeroCbplp = _countMaxCbplp = 1;
                _firstLpLum.Init2(); _indLpLum0.Init2(); _indLpLum1.Init2();
                _firstLpChr.Init2(); _indLpChr0.Init2(); _indLpChr1.Init2();
                _absLp0.Init1(); _absLp1.Init1();
                for (int i = 1; i <= 15; i++) { _lpScan[i] = JxrTables.ScanOrder0[i]; _lpTotals[i] = JxrTables.ScanTotals[i]; }
                _modelLp.Init(1);
            }
            if (_resetTotals)
                for (int i = 1; i <= 15; i++) _lpTotals[i] = JxrTables.ScanTotals[i];
            int nc = _nc;
            var _lp = _p.LpRes.AsSpan(_mbi * nc * 16, nc * 16);
            int fullPlanes = _is42x ? 2 : nc;
            int b0 = _modelLp.Bits0, b1 = _modelLp.Bits1;
            // VLC parts (magnitude >> model bits, signed)
            Span<int> hi = stackalloc int[nc * 16];
            for (int c = 0; c < nc; c++)
            {
                int bits = c == 0 ? b0 : b1;
                for (int j = 1; j < 16; j++)
                {
                    int v = _lp[c * 16 + j];
                    int h = Math.Abs(v) >> bits;
                    hi[c * 16 + j] = v < 0 ? -h : h;
                }
            }
            int cbplp = 0;
            for (int n = 0; n < fullPlanes; n++)
            {
                bool nz = false;
                if (_is42x && n == 1)
                    for (int j = 1; j < 16 && !nz; j++) nz = hi[16 + j] != 0 || hi[32 + j] != 0;
                else
                    for (int j = 1; j < 16 && !nz; j++) nz = hi[n * 16 + j] != 0;
                if (nz) cbplp |= 1 << n;
            }
            if (_fmt is JxrInternalFormat.Yuv420 or JxrInternalFormat.Yuv422 or JxrInternalFormat.Yuv444)
            {
                int max = fullPlanes * 4 - 5;
                if (_countZeroCbplp <= 0 || _countMaxCbplp < 0)
                {
                    int v = _countMaxCbplp < _countZeroCbplp ? max - cbplp : cbplp;
                    w.Write(_fmt == JxrInternalFormat.Yuv444 ? JxrTables.CbplpYuv444 : JxrTables.CbplpYuv42x, v);
                }
                else w.Bits(cbplp, fullPlanes);
                _countZeroCbplp = Math.Clamp(_countZeroCbplp + 1 - (cbplp == 0 ? 4 : 0), -8, 7);
                _countMaxCbplp = Math.Clamp(_countMaxCbplp + 1 - (cbplp == max ? 4 : 0), -8, 7);
            }
            else
                for (int n = 0; n < nc; n++) w.Bit(((cbplp >> n) & 1) != 0);
            int lap0 = 0, lap1 = 0;
            Span<int> vals = stackalloc int[16];
            for (int n = 0; n < fullPlanes; n++)
            {
                int index = n == 0 ? 0 : 1;
                int numNz = 0;
                if (((cbplp >> n) & 1) != 0)
                {
                    vals.Clear();
                    if (_is42x && n != 0)
                    {
                        int remapOffset = _is420 ? 1 : 0, count = _is422 ? 14 : 6;
                        var tr = _is420 ? JxrTables.Transpose420 : JxrTables.Transpose422;
                        for (int k = 0; k < count; k++) vals[k] = hi[((k & 1) + 1) * 16 + tr[RemapArr[(k >> 1) + remapOffset]]];
                        numNz = WriteBlock(w, true, vals, 1, _is420 ? 10 : 2);
                    }
                    else
                    {
                        for (int i = 1; i <= 15; i++) vals[i - 1] = hi[n * 16 + _lpScan[i]];
                        numNz = WriteBlock(w, index != 0, vals, 1, 1);
                        // AdaptiveLPScan( ) updates in coding order
                        for (int i = 1; i <= 15; i++)
                        {
                            if (vals[i - 1] == 0) continue;
                            _lpTotals[i]++;
                            if (i > 1 && _lpTotals[i] > _lpTotals[i - 1])
                            {
                                (_lpTotals[i], _lpTotals[i - 1]) = (_lpTotals[i - 1], _lpTotals[i]);
                                (_lpScan[i], _lpScan[i - 1]) = (_lpScan[i - 1], _lpScan[i]);
                            }
                        }
                    }
                }
                int bits = index == 0 ? b0 : b1;
                if (index == 0) lap0 += numNz; else lap1 += numNz;
                if (bits != 0)
                {
                    if (_is420 && n != 0)
                        for (int k = 1; k < 4; k++)
                        {
                            int t = JxrTables.Transpose420[k];
                            Refine(w, _lp[16 + t], hi[16 + t], bits);
                            Refine(w, _lp[32 + t], hi[32 + t], bits);
                        }
                    else if (_is422 && n != 0)
                        for (int k = 1; k < 8; k++)
                        {
                            int t = JxrTables.Transpose422[k];
                            Refine(w, _lp[16 + t], hi[16 + t], bits);
                            Refine(w, _lp[32 + t], hi[32 + t], bits);
                        }
                    else
                        for (int k = 1; k < 16; k++)
                        {
                            int t = n * 16 + JxrTables.Transpose444[k];
                            Refine(w, _lp[t], hi[t], bits);
                        }
                }
            }
            _modelLp.Update(lap0, lap1, 1, _fmt, nc);
            if (_resetContext)
            {
                _firstLpLum.Adapt2(4); _indLpLum0.Adapt2(3); _indLpLum1.Adapt2(3);
                _firstLpChr.Adapt2(4); _indLpChr0.Adapt2(3); _indLpChr1.Adapt2(3);
                _absLp0.Adapt1(); _absLp1.Adapt1();
            }
        }

        /// <summary>The inverse of REFINE_LP( ) / DECODE_FLEX( ): the low bits, and the sign when the VLC part is zero.</summary>
        static void Refine(JxrBitWriter w, int v, int hi, int bits)
        {
            int lo = Math.Abs(v) & ((1 << bits) - 1);
            w.Bits(lo, bits);
            if (hi == 0 && lo != 0) w.Bit(v < 0);
        }

        // ------------------------------------------------------------------ CBPHP and HP band

        /// <summary>
        /// The HP VLC parts of the current macroblock (from the quantized residuals and the model bits), its coded block
        /// pattern MBCBPHP, and MB_CBPHP( ) coding it.
        /// </summary>
        public void WriteMbCbphp(JxrBitWriter w)
        {
            if (_tileStart)
            {
                _numCbphp.Init1(); _numBlkCbphp.Init1();
                // MB_HP( ) state: initialized here already since the coded block pattern depends on the model bits
                _firstHpLum.Init2(); _indHpLum0.Init2(); _indHpLum1.Init2();
                _firstHpChr.Init2(); _indHpChr0.Init2(); _indHpChr1.Init2();
                _absHp0.Init1(); _absHp1.Init1();
                for (int i = 1; i <= 15; i++)
                {
                    _hpHor[i] = JxrTables.ScanOrder0[i];
                    _hpVer[i] = JxrTables.ScanOrder1[i];
                    _hpHorTotals[i] = _hpVerTotals[i] = JxrTables.ScanTotals[i];
                }
                _modelHp.Init(2);
            }
            var p = _p;
            int nc = _nc;
            int b0 = _modelHp.Bits0, b1 = _modelHp.Bits1;
            int baseIdx = _mbi * nc;
            // actual coded block patterns in hierarchical order
            for (int c = 0; c < nc; c++)
            {
                int bits = c == 0 ? b0 : b1;
                int nBlocks = c > 0 && _is420 ? 1 : c > 0 && _is422 ? 2 : 4;
                int cb = 0;
                var px = p.Px[c];
                int s = p.PxW[c];
                for (int b = 0; b < nBlocks * 4; b++)
                {
                    int map = nBlocks == 4 ? JxrTables.HierScanOrder[b] : b;
                    int o = p.BlockOffset(c, _mbx, _mby, map);
                    for (int k = 1; k < 16; k++)
                        if (Math.Abs(px[o + (k >> 2) * s + (k & 3)]) >> bits != 0) { cb |= 1 << b; break; }
                }
                p.Cbphp[baseIdx + c] = cb;
            }
            // prediction residuals (the inverse of PredCBPHP( ), 8.7.17.5)
            if (_tileStart)
            {
                _cbphpState0 = _cbphpState1 = 0;
                _countOnes0 = _countOnes1 = -4;
                _countZeroes0 = _countZeroes1 = 4;
            }
            var diff = _diff;
            int mbw = p.MbW;
            int comps = _is42x ? 1 : nc;
            for (int i = 0; i < comps; i++)
            {
                int c1 = i > 0 ? 1 : 0;
                int cb = p.Cbphp[baseIdx + i];
                int st = c1 == 0 ? _cbphpState0 : _cbphpState1;
                int d = cb;
                if (st == 0)
                {
                    d ^= (d & 0x3300) << 2;
                    d ^= (d & 0x00CC) << 6;
                    d ^= (d & 0x33) << 2;
                    d ^= 0x20 & (d << 1);
                    d ^= 0x10 & (d << 3);
                    d ^= 0x02 & (d << 1);
                    if (_leftEdge) d ^= _topEdge ? 1 : (p.Cbphp[baseIdx - mbw * nc + i] >> 10) & 1;
                    else d ^= (p.Cbphp[baseIdx - nc + i] >> 5) & 1;
                }
                else if (st == 2) d ^= 0xFFFF;
                diff[i] = d;
                UpdateCbphpModel(c1, BitOperations.PopCount((uint)cb));
            }
            if (_is42x)
                for (int i = 1; i <= 2; i++)
                {
                    int cb = p.Cbphp[baseIdx + i];
                    int d = cb;
                    if (_cbphpState1 == 0)
                    {
                        if (_is422)
                        {
                            d ^= (d & 0x30) << 2;
                            d ^= (d & 0x0C) << 2;
                            d ^= (d & 0x03) << 2;
                            d ^= (d & 0x01) << 1;
                        }
                        else
                        {
                            d ^= (d & 0x3) << 2;
                            d ^= 0x02 & (d << 1);
                        }
                        if (_leftEdge) d ^= _topEdge ? 1 : (p.Cbphp[baseIdx - mbw * nc + i] >> (_is422 ? 6 : 2)) & 1;
                        else d ^= (p.Cbphp[baseIdx - nc + i] >> 1) & 1;
                    }
                    else if (_cbphpState1 == 2) d ^= _is422 ? 0xFF : 0x0F;
                    diff[i] = d;
                    UpdateCbphpModel(1, BitOperations.PopCount((uint)cb) * (_is422 ? 2 : 4));
                }

            // MB_CBPHP( )
            bool yuv = _fmt is JxrInternalFormat.Yuv420 or JxrInternalFormat.Yuv422 or JxrInternalFormat.Yuv444;
            int cl = _fmt is JxrInternalFormat.Yuvk or JxrInternalFormat.NComponent ? nc : 1;
            ReadOnlySpan<int> shift422 = stackalloc int[] { 0, 1, 4, 5 };
            for (int i = 0; i < cl; i++)
            {
                // block groups (2x2 luma blocks and their chroma) with non-zero residual patterns
                int groups = 0;
                for (int g = 0; g < 4; g++)
                {
                    bool nz = ((diff[i] >> (g * 4)) & 0xF) != 0;
                    if (yuv && !nz) nz = ChromaBits(g, shift422) != 0;
                    if (nz) groups |= 1 << g;
                }
                int num = BitOperations.PopCount((uint)groups);
                w.Write(JxrTables.NumCbphp[_numCbphp.Table], num);
                _numCbphp.Disc1 += JxrTables.NumCbphpDelta[num];
                WriteRefineCbphp(w, num, groups);
                for (int g = 0; g < 4; g++)
                {
                    if ((groups & (1 << g)) == 0) continue;
                    int luma = (diff[i] >> (g * 4)) & 0xF;
                    int chroma = yuv ? ChromaBits(g, shift422) : 0;
                    int code = Array.IndexOf(JxrTables.CbphpOut, luma);
                    int val = code == 0 ? 0 : code == 1 ? 5 : code < 4 ? 2 : code < 8 ? 1 : code < 12 ? 3 : 4;
                    int nb, inc = -1;
                    if (chroma == 0) nb = val - 1;
                    else if (val >= 3) { nb = 8; inc = val - 3; }
                    else nb = val + 5;
                    if (yuv)
                    {
                        w.Write(JxrTables.NumBlkCbphpYuv[_numBlkCbphp.Table], nb);
                        _numBlkCbphp.Disc1 += JxrTables.NumBlkCbphpDeltaYuv[nb];
                    }
                    else
                    {
                        w.Write(JxrTables.NumCbphp[_numBlkCbphp.Table], nb);
                        _numBlkCbphp.Disc1 += JxrTables.NumCbphpDelta[nb];
                    }
                    if (chroma != 0)
                    {
                        w.Write(JxrTables.ChrCbphp, chroma - 1);
                        if (inc >= 0) w.Write(JxrTables.ChrCbphp, inc);
                    }
                    int flc = JxrTables.CbphpFlc[val];
                    if (flc != 0) w.Bits(code - JxrTables.CbphpOff[val], flc);
                    if (_fmt == JxrInternalFormat.Yuv444)
                        for (int k = 0; k < 2; k++)
                        {
                            if ((chroma & (1 << k)) == 0) continue;
                            int ch = (diff[k + 1] >> (g * 4)) & 0xF;
                            int n = BitOperations.PopCount((uint)ch);
                            w.Write(JxrTables.NumChBlk, n - 1);
                            WriteRefineCbphp(w, n, ch);
                        }
                    else if (_fmt == JxrInternalFormat.Yuv422)
                        for (int k = 0; k < 2; k++)
                        {
                            if ((chroma & (1 << k)) == 0) continue;
                            int ch = (diff[k + 1] >> shift422[g]) & 5;
                            w.Write(JxrTables.ChrCbphp, ch == 1 ? 0 : ch == 4 ? 1 : 2);
                        }
                }
            }
        }

        /// <summary>Which chroma components have non-zero residual patterns in block group g (bit 0: U, bit 1: V).</summary>
        int ChromaBits(int g, ReadOnlySpan<int> shift422)
        {
            var diff = _diff;
            int r = 0;
            for (int k = 0; k < 2; k++)
            {
                int d = diff[k + 1];
                bool nz = _fmt switch
                {
                    JxrInternalFormat.Yuv444 => ((d >> (g * 4)) & 0xF) != 0,
                    JxrInternalFormat.Yuv422 => ((d >> shift422[g]) & 5) != 0,
                    _ => ((d >> g) & 1) != 0,
                };
                if (nz) r |= 1 << k;
            }
            return r;
        }

        static void WriteRefineCbphp(JxrBitWriter w, int num, int mask)
        {
            switch (num)
            {
                case 1: w.Bits(BitOperations.TrailingZeroCount(mask), 2); break;
                case 2: w.Write(JxrTables.RefCbphp1, Array.IndexOf(JxrTables.RefCbphp1Values, mask)); break;
                case 3: w.Bits(BitOperations.TrailingZeroCount(~mask & 0xF), 2); break;
            }
        }

        void UpdateCbphpModel(int i, int nOrig)
        {
            ref int ones = ref i == 0 ? ref _countOnes0 : ref _countOnes1;
            ref int zeroes = ref i == 0 ? ref _countZeroes0 : ref _countZeroes1;
            ref int state = ref i == 0 ? ref _cbphpState0 : ref _cbphpState1;
            ones = Math.Clamp(ones + nOrig - 3, -16, 15);
            zeroes = Math.Clamp(zeroes + 16 - nOrig - 3, -16, 15);
            if (ones < 0) state = ones < zeroes ? 1 : 2;
            else if (zeroes < 0) state = 2;
            else state = 0;
        }

        /// <summary>
        /// MB_HP( ) / MB_HP_FLEX( ): the VLC parts of the HP coefficients, with the flexbits after each block when
        /// <paramref name="flex"/> is set (spatial mode). <see cref="WriteMbCbphp"/> must come first (it also picks up the
        /// model bits this function then updates).
        /// </summary>
        public void WriteMbHp(JxrBitWriter w, bool flex, int trim)
        {
            if (_resetTotals)
                for (int i = 1; i <= 15; i++) _hpHorTotals[i] = _hpVerTotals[i] = JxrTables.ScanTotals[i];
            var p = _p;
            int lap0 = 0, lap1 = 0;
            bool vertical = p.HpMode[_mbi] == 1;
            var scan = vertical ? _hpVer : _hpHor;
            var totals = vertical ? _hpVerTotals : _hpHorTotals;
            var vals = _vals;
            Span<int> hi = stackalloc int[16];
            for (int c = 0; c < _nc; c++)
            {
                bool chroma = c > 0;
                int nBlocks = chroma && _is420 ? 1 : chroma && _is422 ? 2 : 4;
                int bits = chroma ? _modelHp.Bits1 : _modelHp.Bits0;
                int cb = p.Cbphp[_mbi * _nc + c];
                var px = p.Px[c];
                int s = p.PxW[c];
                for (int b = 0; b < nBlocks * 4; b++, cb >>= 1)
                {
                    int map = nBlocks == 4 ? JxrTables.HierScanOrder[b] : b;
                    int o = p.BlockOffset(c, _mbx, _mby, map);
                    for (int k = 1; k < 16; k++)
                    {
                        int v = px[o + (k >> 2) * s + (k & 3)];
                        int h = Math.Abs(v) >> bits;
                        hi[k] = v < 0 ? -h : h;
                    }
                    int numNz = 0;
                    if ((cb & 1) != 0)
                    {
                        for (int i = 1; i <= 15; i++) vals[i - 1] = hi[scan[i]];
                        numNz = WriteBlock(w, chroma, vals, 2, 1);
                        for (int i = 1; i <= 15; i++)
                        {
                            if (vals[i - 1] == 0) continue;
                            totals[i]++;
                            if (i > 1 && totals[i] > totals[i - 1])
                            {
                                (totals[i], totals[i - 1]) = (totals[i - 1], totals[i]);
                                (scan[i], scan[i - 1]) = (scan[i - 1], scan[i]);
                            }
                        }
                    }
                    if (flex) WriteFlex(w, px, o, s, hi, bits, trim);
                    if (chroma) lap1 += numNz; else lap0 += numNz;
                }
            }
            p.ModelBitsHp[_mbi * 2] = (byte)_modelHp.Bits0;
            p.ModelBitsHp[_mbi * 2 + 1] = (byte)_modelHp.Bits1;
            _modelHp.Update(lap0, lap1, 2, _fmt, _nc);
            if (_resetContext)
            {
                _firstHpLum.Adapt2(4); _indHpLum0.Adapt2(3); _indHpLum1.Adapt2(3);
                _firstHpChr.Adapt2(4); _indHpChr0.Adapt2(3); _indHpChr1.Adapt2(3);
                _absHp0.Adapt1(); _absHp1.Adapt1();
                _numCbphp.Adapt1(); _numBlkCbphp.Adapt1();
            }
        }

        /// <summary>BLOCK_FLEXBITS( ) of one block: the low bits (above the trimmed ones) in transposed order.</summary>
        static void WriteFlex(JxrBitWriter w, int[] px, int o, int s, ReadOnlySpan<int> hi, int bits, int trim)
        {
            int left = bits - trim;
            if (left <= 0) return;
            var t = JxrTables.Transpose444;
            for (int n = 1; n < 16; n++)
            {
                int k = t[n];
                int v = px[o + (k >> 2) * s + (k & 3)];
                int lo = (Math.Abs(v) & ((1 << bits) - 1)) >> trim;
                w.Bits(lo, left);
                if (hi[k] == 0 && lo != 0) w.Bit(v < 0);
            }
        }

        /// <summary>MB_FLEXBITS( ) of the frequency mode flexbits packet, with the model bits stored by <see cref="WriteMbHp"/>.</summary>
        public void WriteMbFlexbits(JxrBitWriter w, int trim)
        {
            var p = _p;
            Span<int> hi = stackalloc int[16];
            for (int c = 0; c < _nc; c++)
            {
                bool chroma = c > 0;
                int nBlocks = chroma && _is420 ? 1 : chroma && _is422 ? 2 : 4;
                int bits = p.ModelBitsHp[_mbi * 2 + (chroma ? 1 : 0)];
                var px = p.Px[c];
                int s = p.PxW[c];
                for (int b = 0; b < nBlocks * 4; b++)
                {
                    int map = nBlocks == 4 ? JxrTables.HierScanOrder[b] : b;
                    int o = p.BlockOffset(c, _mbx, _mby, map);
                    for (int k = 1; k < 16; k++) hi[k] = Math.Abs(px[o + (k >> 2) * s + (k & 3)]) >> bits;
                    WriteFlex(w, px, o, s, hi, bits, trim);
                }
            }
        }
    }
}
