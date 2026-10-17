using System;
using System.Threading.Tasks;

namespace Aardvark.ImageCodecs.JpegXrImpl
{
    internal sealed partial class JxrPlane
    {
        // ------------------------------------------------------------------ DC and LP coefficient decoding (9.4.2, 9.4.3)

        /// <summary>
        /// Remapping, prediction and dequantization of the DC and LP coefficients of the current macroblock (from
        /// <paramref name="dcIn"/> and the LP coefficients last parsed), the HP prediction direction (9.6.3.2), and the first
        /// level inverse transform; the result goes into the DC-LP arrays.
        /// </summary>
        public void DecodeDcLp(ReadOnlySpan<int> dcIn, bool haveLp)
        {
            var mb = _mb;
            Array.Clear(mb);
            for (int c = 0; c < Nc; c++) mb[c * 16] = dcIn[c];
            int predBase = _mbi * Nc * 7;
            int leftBase = predBase - Nc * 7, topBase = predBase - _mbw * Nc * 7;
            var pred = _pred;

            // DC prediction (9.6.1)
            int dcMode;
            if (_leftEdge && _topEdge) dcMode = 3;
            else if (_leftEdge) dcMode = 1;
            else if (_topEdge) dcMode = 0;
            else
            {
                int tlBase = topBase - Nc * 7;
                int left = pred[leftBase], top = pred[topBase], topLeft = pred[tlBase];
                int strHor, strVer;
                if (Fmt is JxrInternalFormat.YOnly or JxrInternalFormat.NComponent)
                {
                    strHor = Math.Abs(topLeft - left);
                    strVer = Math.Abs(topLeft - top);
                }
                else
                {
                    int scale = Is420 ? 8 : Is422 ? 4 : 2;
                    strHor = Math.Abs(topLeft - left) * scale + Math.Abs(pred[tlBase + 7] - pred[leftBase + 7]) + Math.Abs(pred[tlBase + 14] - pred[leftBase + 14]);
                    strVer = Math.Abs(topLeft - top) * scale + Math.Abs(pred[tlBase + 7] - pred[topBase + 7]) + Math.Abs(pred[tlBase + 14] - pred[topBase + 14]);
                }
                if (strHor * 4 < strVer) dcMode = 1;
                else if (strVer * 4 < strHor) dcMode = 0;
                else dcMode = 2;
            }
            if (dcMode != 3)
                for (int c = 0; c < Nc; c++)
                {
                    int left = dcMode != 1 ? pred[leftBase + c * 7] : 0, top = dcMode != 0 ? pred[topBase + c * 7] : 0;
                    if (dcMode == 0) mb[c * 16] += left;
                    else if (dcMode == 1) mb[c * 16] += top;
                    else mb[c * 16] += c == 0 || !Is42x ? (top + left) >> 1 : (top + left + 1) >> 1;
                }
            for (int c = 0; c < Nc; c++)
            {
                pred[predBase + c * 7] = mb[c * 16];
                mb[c * 16] *= _dcFactor[c];
            }

            // LP remapping (9.5.2) and prediction (9.6.2)
            if (haveLp)
                for (int c = 0; c < Nc; c++)
                {
                    int jMax = c > 0 && Is420 ? 3 : c > 0 && Is422 ? 7 : 15;
                    for (int j = 1; j <= jMax; j++) mb[c * 16 + j] = _lpIn[c * 16 + j];
                }
            int qpLp = _qpLp[_mbi];
            int lpMode = 2;
            if (dcMode == 0 && qpLp == _qpLp[_mbi - 1]) lpMode = 0;
            else if (dcMode == 1 && qpLp == _qpLp[_mbi - _mbw]) lpMode = 1;
            for (int c = 0; c < Nc; c++)
            {
                int m = c * 16, l = leftBase + c * 7, t = topBase + c * 7;
                if (c == 0 || !Is42x)
                {
                    if (lpMode == 0) { mb[m + 4] += pred[l + 4]; mb[m + 8] += pred[l + 5]; mb[m + 12] += pred[l + 6]; }
                    else if (lpMode == 1) { mb[m + 1] += pred[t + 1]; mb[m + 2] += pred[t + 2]; mb[m + 3] += pred[t + 3]; }
                }
                else if (Is420)
                {
                    if (lpMode == 0) mb[m + 2] += pred[l + 2];
                    else if (lpMode == 1) mb[m + 1] += pred[t + 1];
                }
                else
                {
                    if (lpMode == 0) { mb[m + 4] += pred[l + 4]; mb[m + 2] += pred[l + 2]; mb[m + 6] += pred[l + 6]; }
                    else if (lpMode == 1)
                    {
                        mb[m + 4] += pred[t + 4];
                        mb[m + 1] += pred[t + 5];
                        mb[m + 5] += mb[m + 1];
                    }
                    else if (dcMode == 1) mb[m + 5] += mb[m + 1];
                }
                int p = predBase + c * 7;
                if (c == 0 || !Is42x)
                {
                    pred[p + 1] = mb[m + 1]; pred[p + 2] = mb[m + 2]; pred[p + 3] = mb[m + 3];
                    pred[p + 4] = mb[m + 4]; pred[p + 5] = mb[m + 8]; pred[p + 6] = mb[m + 12];
                }
                else if (Is420) { pred[p + 1] = mb[m + 1]; pred[p + 2] = mb[m + 2]; }
                else
                {
                    pred[p + 1] = mb[m + 1]; pred[p + 2] = mb[m + 2]; pred[p + 4] = mb[m + 4];
                    pred[p + 5] = mb[m + 5]; pred[p + 6] = mb[m + 6];
                }
            }
            // HP prediction direction (9.6.3.2), from the LP coefficients before dequantization: what jxrlib does (and what
            // the files it writes need); Table 122 would take the dequantized ones
            if (Bands is JxrBands.All or JxrBands.NoFlexbits)
            {
                int strHor = Math.Abs(mb[1]) + Math.Abs(mb[2]) + Math.Abs(mb[3]);
                int strVer = Math.Abs(mb[4]) + Math.Abs(mb[8]) + Math.Abs(mb[12]);
                if (Fmt != JxrInternalFormat.YOnly && Fmt != JxrInternalFormat.NComponent)
                    for (int c = 1; c <= 2; c++)
                    {
                        int m = c * 16;
                        strHor += Math.Abs(mb[m + 1]);
                        if (Is420) strVer += Math.Abs(mb[m + 2]);
                        else if (Is422)
                        {
                            strVer += Math.Abs(mb[m + 2]) + Math.Abs(mb[m + 6]);
                            strHor += Math.Abs(mb[m + 5]);
                        }
                        else strVer += Math.Abs(mb[m + 4]);
                    }
                _hpMode[_mbi] = (byte)(strHor * 4 < strVer ? 0 : strVer * 4 < strHor ? 1 : 2);
            }

            // LP dequantization (9.8.2)
            var lf = _lpFactor[qpLp];
            for (int c = 0; c < Nc; c++)
            {
                int jMax = c > 0 && Is420 ? 3 : c > 0 && Is422 ? 7 : 15;
                int f = lf[c];
                for (int j = 1; j <= jMax; j++) mb[c * 16 + j] *= f;
            }

            // first level inverse transform (9.9.2) into the DC-LP arrays
            Span<int> t16 = stackalloc int[16];
            for (int c = 0; c < Nc; c++)
            {
                var a = mb.AsSpan(c * 16, 16);
                var dc = Dc[c];
                int w = DcW[c];
                if (c == 0 || !Is42x)
                {
                    JxrTransform.Ict4x4(a);
                    if (c > 0 && Ph.Scaled) for (int j = 0; j < 16; j++) a[j] *= 2;
                    int o = 4 * _mby * w + 4 * _mbx;
                    for (int j = 0; j < 16; j++) dc[o + (j >> 2) * w + (j & 3)] = a[j];
                }
                else if (Is420)
                {
                    JxrTransform.T2x2h(ref a[0], ref a[1], ref a[2], ref a[3], 0);
                    (a[1], a[2]) = (a[2], a[1]);
                    if (Ph.Scaled) for (int j = 0; j < 4; j++) a[j] *= 2;
                    int o = 2 * _mby * w + 2 * _mbx;
                    dc[o] = a[0]; dc[o + 1] = a[1]; dc[o + w] = a[2]; dc[o + w + 1] = a[3];
                }
                else
                {
                    JxrTransform.T2pt(ref a[0], ref a[4]);
                    JxrTransform.T2x2h(ref a[0], ref a[1], ref a[2], ref a[3], 0);
                    (a[1], a[2]) = (a[2], a[1]);
                    JxrTransform.T2x2h(ref a[4], ref a[6], ref a[5], ref a[7], 0);
                    (a[5], a[6]) = (a[6], a[5]);
                    if (Ph.Scaled) for (int j = 0; j < 8; j++) a[j] *= 2;
                    int o = 4 * _mby * w + 2 * _mbx;
                    for (int j = 0; j < 8; j++) dc[o + (j >> 1) * w + (j & 1)] = a[j];
                }
            }
        }

        /// <summary>LPInput of the current macroblock, for the frequency mode where DC and LP are parsed from different packets.</summary>
        public void ClearLp() => Array.Clear(_lpIn);

        // ------------------------------------------------------------------ HP coefficient decoding (9.4.4)

        /// <summary>Dequantization (9.8.3) and prediction (9.6.3.3) of the HP coefficients of the current macroblock in Px.</summary>
        public void DecodeHp()
        {
            var hf = _hpFactor[_qpHp[_mbi]];
            int mode = _hpMode[_mbi];
            for (int c = 0; c < Nc; c++)
            {
                var px = Px[c];
                int s = PxW[c];
                int f = hf[c];
                int bw = _mbPxW[c] >> 2, bh = _mbPxH[c] >> 2;
                int o0 = _mby * _mbPxH[c] * s + _mbx * _mbPxW[c];
                if (f != 1)
                    for (int y = 0; y < bh * 4; y++)
                    {
                        int o = o0 + y * s;
                        for (int x = 0; x < bw * 4; x++)
                            if (((x | y) & 3) != 0) px[o + x] *= f;
                    }
                if (mode == 2) continue;
                // prediction across the blocks of the macroblock (all of luma / 444 chroma, the 2 or 4 x 2 blocks of 42x chroma)
                if (mode == 0)
                {
                    // from the left: the first column (coefficients 4, 8, 12) of each block but those of the first block column
                    for (int by = 0; by < bh; by++)
                        for (int bx = 1; bx < bw; bx++)
                        {
                            int o = o0 + 4 * by * s + 4 * bx;
                            px[o + s] += px[o + s - 4];
                            px[o + 2 * s] += px[o + 2 * s - 4];
                            px[o + 3 * s] += px[o + 3 * s - 4];
                        }
                }
                else
                {
                    // from the top: the first row (coefficients 1, 2, 3)
                    for (int by = 1; by < bh; by++)
                        for (int bx = 0; bx < bw; bx++)
                        {
                            int o = o0 + 4 * by * s + 4 * bx;
                            px[o + 1] += px[o + 1 - 4 * s];
                            px[o + 2] += px[o + 2 - 4 * s];
                            px[o + 3] += px[o + 3 - 4 * s];
                        }
                }
            }
        }

        // ------------------------------------------------------------------ sample reconstruction (9.9)

        /// <summary>First level overlap filtering, coefficient combination, second level transform and overlap filtering.</summary>
        public void Reconstruct(bool parallel)
        {
            var ih = Ih;
            if (ih.Overlap == 2)
                for (int c = 0; c < Nc; c++)
                {
                    int bx = DcW[c] / _mbw, by = DcH[c] / _mbh;   // DC-LP samples per macroblock (4 or 2)
                    ForRegions((x0, y0, x1, y1) =>
                    {
                        if (c == 0 || !Is42x) JxrTransform.Overlap4(Dc[c], DcW[c], x0 * bx, y0 * by, x1 * bx, y1 * by, false);
                        else JxrTransform.Overlap2(Dc[c], DcW[c], x0 * bx, y0 * by, x1 * bx, y1 * by, false);
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
                        int po = 4 * y * s, dofs = y * dw;
                        for (int x = 0; x < dw; x++) px[po + 4 * x] = dc[dofs + x];
                        for (int x = 0; x < dw; x++) JxrTransform.Ict4x4(px, po + 4 * x, s);
                    }
                }
                if (parallel && dh >= 64) Parallel.For(0, (dh + 31) / 32, k => Rows(k * 32, Math.Min(dh, k * 32 + 32)));
                else Rows(0, dh);
            }
            if (ih.Overlap != 0)
                for (int c = 0; c < Nc; c++)
                {
                    int mw = _mbPxW[c], mh = _mbPxH[c];
                    var px = Px[c];
                    int s = PxW[c];
                    ForRegions((x0, y0, x1, y1) =>
                    {
                        int X0 = x0 * mw, Y0 = y0 * mh, X1 = x1 * mw, Y1 = y1 * mh;
                        if (parallel && Y1 - Y0 >= 256)
                        {
                            int bands = (Y1 - Y0) / 128;
                            Parallel.For(0, bands, k =>
                            {
                                int from = Y0 + (Y1 - Y0) * k / bands, to = Y0 + (Y1 - Y0) * (k + 1) / bands;
                                JxrTransform.Overlap4Interior(px, s, X0, Y0, X1, Y1, from, to, false);
                            });
                            JxrTransform.Overlap4Edges(px, s, X0, Y0, X1, Y1, false);
                        }
                        else JxrTransform.Overlap4(px, s, X0, Y0, X1, Y1, false);
                    });
                }
        }

        /// <summary>The regions overlap filtering works on, in macroblocks: the whole image, or each tile with hard tiling.</summary>
        void ForRegions(Action<int, int, int, int> f)
        {
            var ih = Ih;
            if (!ih.HardTiling) { f(0, 0, _mbw, _mbh); return; }
            for (int ty = 0; ty < ih.TileRows; ty++)
                for (int tx = 0; tx < ih.TileCols; tx++)
                    f(ih.TileLeft[tx], ih.TileTop[ty], ih.TileLeft[tx + 1], ih.TileTop[ty + 1]);
        }
    }
}
