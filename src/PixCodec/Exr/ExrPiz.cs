using System;
using System.Buffers.Binary;

namespace PixCodec
{
    /// <summary>
    /// PIZ: the chunk is viewed as 16 bit words, gathered per channel, mapped through a table of the values that
    /// actually occur, transformed with a 2D Haar-like wavelet and entropy coded with a canonical Huffman code
    /// that has an extra run-length symbol.
    /// </summary>
    internal static unsafe class ExrPiz
    {
        const int BitmapSize = 8192;
        const int EncSize = 65537;       // 65536 word values + run length symbol
        const int MaxCodeLen = 58;
        const int TabBits = 12;

        internal sealed class Tables
        {
            public readonly byte[] Bitmap = new byte[BitmapSize];
            public readonly ushort[] Lut = new ushort[65536];
            public readonly byte[] Len = new byte[EncSize];
            public readonly int[] Sorted = new int[EncSize];
            public readonly int[] Fast = new int[1 << TabBits];
            public readonly long[] Base = new long[MaxCodeLen + 2];
            public readonly int[] Count = new int[MaxCodeLen + 2];
            public readonly int[] Offset = new int[MaxCodeLen + 2];
            public readonly int[] Fill = new int[MaxCodeLen + 2];
            // encoder only
            public int[]? Freq, Parent, Order;
            public long[]? Weight, Codes;
        }

        static ImageFormatException Corrupt(string what) => new("EXR PIZ data is corrupt: " + what);

        // ------------------------------------------------------------------ decode

        public static void Decode(ReadOnlySpan<byte> src, Span<byte> dst, in ExrChunk c, ExrScratch s)
        {
            var t = s.Piz ??= new Tables();
            int total = dst.Length / 2;
            var words = s.Words(total);
            if (src.Length < 4) throw Corrupt("truncated");
            int minNZ = BinaryPrimitives.ReadUInt16LittleEndian(src), maxNZ = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(2));
            int p = 4;
            if (minNZ >= BitmapSize || maxNZ >= BitmapSize) throw Corrupt("bitmap range");
            Array.Clear(t.Bitmap);
            if (minNZ <= maxNZ)
            {
                int len = maxNZ - minNZ + 1;
                if (len > src.Length - p) throw Corrupt("truncated bitmap");
                src.Slice(p, len).CopyTo(t.Bitmap.AsSpan(minNZ));
                p += len;
            }
            // reverse lookup table: dense index -> original word value (zero is always present)
            var lut = t.Lut;
            int k = 0;
            lut[k++] = 0;
            for (int i = 1; i < 65536; i++)
                if ((t.Bitmap[i >> 3] & (1 << (i & 7))) != 0) lut[k++] = (ushort)i;
            int maxValue = k - 1;
            Array.Clear(lut, k, 65536 - k);

            if (src.Length - p < 4) throw Corrupt("truncated");
            int hufLen = BinaryPrimitives.ReadInt32LittleEndian(src.Slice(p));
            p += 4;
            if (hufLen < 0 || hufLen > src.Length - p) throw Corrupt("huffman block size");
            HufDecode(src.Slice(p, hufLen), words.AsSpan(0, total), t, s);

            fixed (ushort* w = words)
            fixed (ushort* plut = lut)
            fixed (byte* pd = dst)
            {
                int off = 0;
                for (int ci = 0; ci < c.Chans.Length; ci++)
                {
                    int nx = c.Nx[ci], ny = c.Ny[ci], size = c.Chans[ci].Bytes / 2;
                    if (nx > 0 && ny > 0)
                        for (int j = 0; j < size; j++)
                            WaveletDecode(w + off + j, nx, size, ny, nx * size, maxValue);
                    off += nx * ny * size;
                }
                for (int i = 0; i < total; i++) w[i] = plut[w[i]];

                // channel planes -> line layout
                int nc = c.Chans.Length;
                int* cur = stackalloc int[nc];
                off = 0;
                for (int ci = 0; ci < nc; ci++) { cur[ci] = off; off += c.Nx[ci] * c.Ny[ci] * (c.Chans[ci].Bytes / 2); }
                byte* d = pd;
                for (int y = c.Y0; y <= c.Y1; y++)
                    for (int ci = 0; ci < nc; ci++)
                    {
                        if (!ExrChunk.IsSampled(y, c.Chans[ci].YS)) continue;
                        int bytes = c.Nx[ci] * c.Chans[ci].Bytes;
                        Buffer.MemoryCopy(w + cur[ci], d, bytes, bytes);
                        cur[ci] += bytes / 2;
                        d += bytes;
                    }
            }
        }

        static void HufDecode(ReadOnlySpan<byte> src, Span<ushort> dst, Tables t, ExrScratch s)
        {
            if (dst.Length == 0) return;
            if (src.Length < 20) throw Corrupt("truncated huffman header");
            int im = BinaryPrimitives.ReadInt32LittleEndian(src), iM = BinaryPrimitives.ReadInt32LittleEndian(src.Slice(4));
            int nBits = BinaryPrimitives.ReadInt32LittleEndian(src.Slice(12));
            if (im < 0 || im >= EncSize || iM < 0 || iM >= EncSize || im > iM) throw Corrupt("huffman symbol range");

            // code length table: 6 bit lengths, with codes for runs of zero lengths
            var len = t.Len;
            Array.Clear(len);
            int p = 20, lc = 0;
            uint acc = 0;
            for (int sym = im; sym <= iM; sym++)
            {
                if (lc < 6) { if (p >= src.Length) throw Corrupt("truncated code table"); acc = (acc << 8) | src[p++]; lc += 8; }
                lc -= 6;
                int l = (int)(acc >> lc) & 63;
                if (l >= 59)
                {
                    int run;
                    if (l == 63)
                    {
                        if (lc < 8) { if (p >= src.Length) throw Corrupt("truncated code table"); acc = (acc << 8) | src[p++]; lc += 8; }
                        lc -= 8;
                        run = ((int)(acc >> lc) & 255) + 6;
                    }
                    else run = l - 59 + 2;
                    if (sym + run > iM + 1) throw Corrupt("code table overruns");
                    sym += run - 1;
                }
                else len[sym] = (byte)l;
            }
            if (nBits < 0 || nBits > 8L * (src.Length - p)) throw Corrupt("huffman bit count");

            // canonical codes: the longest codes get the numerically smallest values
            var count = t.Count; var bas = t.Base; var offset = t.Offset;
            Array.Clear(count);
            for (int i = im; i <= iM; i++) count[len[i]]++;
            count[0] = 0;
            long code = 0;
            for (int l = MaxCodeLen; l >= 1; l--)
            {
                bas[l] = code;
                if (code + count[l] > (1L << l)) throw Corrupt("over-subscribed huffman code");
                code = (code + count[l]) >> 1;
            }
            int o = 0;
            var fill = t.Fill;
            for (int l = 1; l <= MaxCodeLen; l++) { offset[l] = fill[l] = o; o += count[l]; }
            var sorted = t.Sorted;
            var fast = t.Fast;
            Array.Clear(fast);
            for (int i = im; i <= iM; i++)
            {
                int l = len[i];
                if (l == 0) continue;
                int idx = fill[l]++;
                sorted[idx] = i;
                if (l <= TabBits)
                {
                    int cv = (int)bas[l] + (idx - offset[l]);
                    fast.AsSpan(cv << (TabBits - l), 1 << (TabBits - l)).Fill((i << 6) | l);
                }
            }

            // bit stream, most significant bit first; a padded copy allows unconditional 8 byte reads
            int dataLen = src.Length - p;
            var buf = s.Bytes(3, dataLen + 32);
            src.Slice(p).CopyTo(buf);
            buf.AsSpan(dataLen, 32).Clear();
            long endBit = nBits;
            int rlc = iM, total = dst.Length;
            fixed (byte* b = buf)
            fixed (ushort* pd = dst)
            fixed (int* pf = fast)
            {
                ulong bb = 0;
                int bc = 0, n = 0;
                long bp = 0;      // next byte to load
                while (true)
                {
                    bb |= BinaryPrimitives.ReverseEndianness(*(ulong*)(b + bp)) >> bc;
                    bp += (63 - bc) >> 3;
                    bc |= 56;
                    long pos = 8 * bp - bc;
                    if (pos >= endBit) { if (pos > endBit) throw Corrupt("huffman data overruns"); break; }
                    int e = pf[(int)(bb >> (64 - TabBits))], l = e & 63, sym = e >> 6;
                    if (l != 0) { bb <<= l; bc -= l; }
                    else
                    {
                        // long code: take 64 bits at the absolute position and search by length
                        long bytePos = pos >> 3; int sh = (int)(pos & 7);
                        ulong w = BinaryPrimitives.ReverseEndianness(*(ulong*)(b + bytePos)) << sh;
                        if (sh != 0) w |= (ulong)b[bytePos + 8] >> (8 - sh);
                        sym = -1;
                        for (l = TabBits + 1; l <= MaxCodeLen; l++)
                        {
                            if (count[l] == 0) continue;
                            long v = (long)(w >> (64 - l)) - bas[l];
                            if (v >= 0) { if (v < count[l]) sym = sorted[offset[l] + (int)v]; break; }
                        }
                        if (sym < 0) throw Corrupt("invalid huffman code");
                        pos += l;
                        if (pos > endBit) throw Corrupt("huffman data overruns");
                        // reload at the new position; keep the invariant that the bc valid bits end at byte bp, bc <= 56
                        bp = pos >> 3; sh = (int)(pos & 7);
                        bb = BinaryPrimitives.ReverseEndianness(*(ulong*)(b + bp)) << sh;
                        bc = 56 - sh;
                        bp += 7;
                    }
                    if (sym == rlc)
                    {
                        int run = (int)(bb >> 56);
                        bb <<= 8; bc -= 8;
                        if (8 * bp - bc > endBit) throw Corrupt("huffman data overruns");
                        if (n == 0 || run > total - n) throw Corrupt("bad run");
                        ushort v = pd[n - 1];
                        for (int i = 0; i < run; i++) pd[n + i] = v;
                        n += run;
                    }
                    else
                    {
                        if (n >= total) throw Corrupt("too many symbols");
                        pd[n++] = (ushort)sym;
                    }
                }
                if (n != total) throw Corrupt("too few symbols");
            }
        }

        static void WaveletDecode(ushort* data, int nx, int ox, int ny, int oy, int maxValue)
        {
            bool w14 = maxValue < (1 << 14);
            int n = Math.Min(nx, ny);
            int p = 1;
            while (p <= n) p <<= 1;
            p >>= 1;
            int p2 = p;
            p >>= 1;
            while (p >= 1)
            {
                ushort* py = data, ey = data + (long)oy * (ny - p2);
                int oy1 = oy * p, oy2 = oy * p2, ox1 = ox * p, ox2 = ox * p2;
                for (; py <= ey; py += oy2)
                {
                    ushort* px = py, ex = py + ox * (nx - p2);
                    if (w14)
                        for (; px <= ex; px += ox2)
                        {
                            ushort* p01 = px + ox1, p10 = px + oy1, p11 = p10 + ox1;
                            int h, a, i00, i10, i01, i11;
                            h = (short)*p10; a = (short)*px + (h & 1) + (h >> 1); i00 = (short)a; i10 = (short)(a - h);
                            h = (short)*p11; a = (short)*p01 + (h & 1) + (h >> 1); i01 = (short)a; i11 = (short)(a - h);
                            h = i01; a = i00 + (h & 1) + (h >> 1); *px = (ushort)a; *p01 = (ushort)(a - h);
                            h = i11; a = i10 + (h & 1) + (h >> 1); *p10 = (ushort)a; *p11 = (ushort)(a - h);
                        }
                    else
                        for (; px <= ex; px += ox2)
                        {
                            ushort* p01 = px + ox1, p10 = px + oy1, p11 = p10 + ox1;
                            int d, b, i00, i10, i01, i11;
                            d = *p10; b = (*px - (d >> 1)) & 0xffff; i10 = b; i00 = (d + b - 0x8000) & 0xffff;
                            d = *p11; b = (*p01 - (d >> 1)) & 0xffff; i11 = b; i01 = (d + b - 0x8000) & 0xffff;
                            d = i01; b = (i00 - (d >> 1)) & 0xffff; *p01 = (ushort)b; *px = (ushort)(d + b - 0x8000);
                            d = i11; b = (i10 - (d >> 1)) & 0xffff; *p11 = (ushort)b; *p10 = (ushort)(d + b - 0x8000);
                        }
                    if ((nx & p) != 0)
                    {
                        ushort* p10 = px + oy1;
                        Wdec(w14, *px, *p10, out ushort a, out ushort b);
                        *px = a; *p10 = b;
                    }
                }
                if ((ny & p) != 0)
                {
                    ushort* px = py, ex = py + ox * (nx - p2);
                    for (; px <= ex; px += ox2)
                    {
                        ushort* p01 = px + ox1;
                        Wdec(w14, *px, *p01, out ushort a, out ushort b);
                        *px = a; *p01 = b;
                    }
                }
                p2 = p;
                p >>= 1;
            }
        }

        static void Wdec(bool w14, ushort l, ushort h, out ushort a, out ushort b)
        {
            if (w14)
            {
                int hi = (short)h, ai = (short)l + (hi & 1) + (hi >> 1);
                a = (ushort)ai; b = (ushort)(ai - hi);
            }
            else
            {
                int d = h, bb = (l - (d >> 1)) & 0xffff;
                b = (ushort)bb; a = (ushort)(d + bb - 0x8000);
            }
        }

        static void Wenc(bool w14, ushort a, ushort b, out ushort l, out ushort h)
        {
            if (w14)
            {
                int sa = (short)a, sb = (short)b;
                l = (ushort)((sa + sb) >> 1); h = (ushort)(sa - sb);
            }
            else
            {
                int ao = (a + 0x8000) & 0xffff, m = (ao + b) >> 1, d = ao - b;
                if (d < 0) m = (m + 0x8000) & 0xffff;
                l = (ushort)m; h = (ushort)d;
            }
        }

        // ------------------------------------------------------------------ encode

        static void WaveletEncode(ushort* data, int nx, int ox, int ny, int oy, int maxValue)
        {
            bool w14 = maxValue < (1 << 14);
            int n = Math.Min(nx, ny);
            int p = 1, p2 = 2;
            while (p2 <= n)
            {
                ushort* py = data, ey = data + (long)oy * (ny - p2);
                int oy1 = oy * p, oy2 = oy * p2, ox1 = ox * p, ox2 = ox * p2;
                for (; py <= ey; py += oy2)
                {
                    ushort* px = py, ex = py + ox * (nx - p2);
                    for (; px <= ex; px += ox2)
                    {
                        ushort* p01 = px + ox1, p10 = px + oy1, p11 = p10 + ox1;
                        Wenc(w14, *px, *p01, out ushort i00, out ushort i01);
                        Wenc(w14, *p10, *p11, out ushort i10, out ushort i11);
                        Wenc(w14, i00, i10, out *px, out *p10);
                        Wenc(w14, i01, i11, out *p01, out *p11);
                    }
                    if ((nx & p) != 0)
                    {
                        ushort* p10 = px + oy1;
                        Wenc(w14, *px, *p10, out ushort l, out ushort h);
                        *px = l; *p10 = h;
                    }
                }
                if ((ny & p) != 0)
                {
                    ushort* px = py, ex = py + ox * (nx - p2);
                    for (; px <= ex; px += ox2)
                    {
                        ushort* p01 = px + ox1;
                        Wenc(w14, *px, *p01, out ushort l, out ushort h);
                        *px = l; *p01 = h;
                    }
                }
                p = p2;
                p2 <<= 1;
            }
        }

        public static ReadOnlySpan<byte> Encode(ReadOnlySpan<byte> raw, scoped in ExrChunk c, ExrScratch s)
        {
            var t = s.Piz ??= new Tables();
            int total = raw.Length / 2;
            var words = s.Words(total);
            // worst case: bitmap + code table (6 bits per symbol) + at most 17 bits per word for an optimal prefix code
            var outBuf = s.Bytes(1, 4 + BitmapSize + 4 + 20 + EncSize + (int)Math.Min(int.MaxValue / 2, total * 17L / 8) + 64);
            int nc = c.Chans.Length;
            fixed (ushort* w = words)
            fixed (byte* pr = raw)
            fixed (ushort* plut = t.Lut)
            {
                // line layout -> channel planes
                int* cur = stackalloc int[nc];
                int off = 0;
                for (int ci = 0; ci < nc; ci++) { cur[ci] = off; off += c.Nx[ci] * c.Ny[ci] * (c.Chans[ci].Bytes / 2); }
                byte* r = pr;
                for (int y = c.Y0; y <= c.Y1; y++)
                    for (int ci = 0; ci < nc; ci++)
                    {
                        if (!ExrChunk.IsSampled(y, c.Chans[ci].YS)) continue;
                        int bytes = c.Nx[ci] * c.Chans[ci].Bytes;
                        Buffer.MemoryCopy(r, w + cur[ci], bytes, bytes);
                        cur[ci] += bytes / 2;
                        r += bytes;
                    }

                var bitmap = t.Bitmap;
                Array.Clear(bitmap);
                fixed (byte* bm = bitmap)
                    for (int i = 0; i < total; i++) bm[w[i] >> 3] |= (byte)(1 << (w[i] & 7));
                bitmap[0] &= 0xfe;      // zero is implicit
                int minNZ = BitmapSize - 1, maxNZ = 0;
                for (int i = 0; i < BitmapSize; i++)
                    if (bitmap[i] != 0) { if (i < minNZ) minNZ = i; if (i > maxNZ) maxNZ = i; }
                int k = 0;
                plut[0] = (ushort)k++;
                for (int i = 1; i < 65536; i++)
                    plut[i] = (bitmap[i >> 3] & (1 << (i & 7))) != 0 ? (ushort)k++ : (ushort)0;
                int maxValue = k - 1;
                for (int i = 0; i < total; i++) w[i] = plut[w[i]];

                off = 0;
                for (int ci = 0; ci < nc; ci++)
                {
                    int nx = c.Nx[ci], ny = c.Ny[ci], size = c.Chans[ci].Bytes / 2;
                    if (nx > 0 && ny > 0)
                        for (int j = 0; j < size; j++)
                            WaveletEncode(w + off + j, nx, size, ny, nx * size, maxValue);
                    off += nx * ny * size;
                }

                var o = outBuf.AsSpan();
                BinaryPrimitives.WriteUInt16LittleEndian(o, (ushort)minNZ);
                BinaryPrimitives.WriteUInt16LittleEndian(o.Slice(2), (ushort)maxNZ);
                int p = 4;
                if (minNZ <= maxNZ)
                {
                    bitmap.AsSpan(minNZ, maxNZ - minNZ + 1).CopyTo(o.Slice(p));
                    p += maxNZ - minNZ + 1;
                }
                int hufLen = HufEncode(w, total, outBuf, p + 4, t);
                BinaryPrimitives.WriteInt32LittleEndian(o.Slice(p), hufLen);
                return outBuf.AsSpan(0, p + 4 + hufLen);
            }
        }

        static int HufEncode(ushort* w, int n, byte[] outBuf, int start, Tables t)
        {
            if (n == 0) return 0;
            var freq = t.Freq ??= new int[EncSize];
            Array.Clear(freq);
            fixed (int* pf = freq)
                for (int i = 0; i < n; i++) pf[w[i]]++;
            int im = 0, iM = 65535;
            while (freq[im] == 0) im++;
            while (freq[iM] == 0) iM--;
            int rlc = ++iM;         // pseudo symbol for run lengths
            freq[rlc] = 1;

            // Huffman tree by merging two sorted queues (leaves by weight, internal nodes in creation order)
            int nsym = 0;
            var order = t.Order ??= new int[EncSize];
            for (int i = im; i <= iM; i++) if (freq[i] != 0) order[nsym++] = i;
            var keys = new int[nsym];
            for (int i = 0; i < nsym; i++) keys[i] = freq[order[i]];
            Array.Sort(keys, order, 0, nsym);
            var weight = t.Weight ??= new long[2 * EncSize];
            var parent = t.Parent ??= new int[2 * EncSize];
            for (int i = 0; i < nsym; i++) weight[i] = keys[i];
            int leaf = 0, inner = nsym, next = nsym;
            while (next < 2 * nsym - 1)
            {
                int a = leaf < nsym && (inner >= next || weight[leaf] <= weight[inner]) ? leaf++ : inner++;
                int b = leaf < nsym && (inner >= next || weight[leaf] <= weight[inner]) ? leaf++ : inner++;
                weight[next] = weight[a] + weight[b];
                parent[a] = next; parent[b] = next;
                next++;
            }
            var len = t.Len;
            Array.Clear(len);
            int root = 2 * nsym - 2;
            // depth of internal nodes top down (parents are created after children), reuse weight[] for depths
            weight[root] = 0;
            for (int i = root - 1; i >= 0; i--) weight[i] = weight[parent[i]] + 1;
            for (int i = 0; i < nsym; i++) len[order[i]] = (byte)weight[i];

            var count = t.Count; var bas = t.Base;
            Array.Clear(count);
            for (int i = im; i <= iM; i++) count[len[i]]++;
            count[0] = 0;
            long code = 0;
            for (int l = MaxCodeLen; l >= 1; l--) { bas[l] = code; code = (code + count[l]) >> 1; }
            var codes = t.Codes ??= new long[EncSize];
            for (int i = im; i <= iM; i++) codes[i] = len[i] == 0 ? 0 : bas[len[i]]++;

            fixed (byte* ob = outBuf)
            fixed (long* pc = codes)
            fixed (byte* pl = len)
            {
                byte* o = ob + start + 20;
                ulong acc = 0;
                int cnt = 0;

                // packed code length table
                for (int i = im; i <= iM; i++)
                {
                    int l = len[i];
                    if (l == 0)
                    {
                        int zerun = 1;
                        while (i + zerun <= iM && zerun < 261 && len[i + zerun] == 0) zerun++;
                        if (zerun >= 6) { acc = (acc << 14) | (63u << 8) | (uint)(zerun - 6); cnt += 14; i += zerun - 1; }
                        else if (zerun >= 2) { acc = (acc << 6) | (uint)(59 + zerun - 2); cnt += 6; i += zerun - 1; }
                        else { acc <<= 6; cnt += 6; }
                    }
                    else { acc = (acc << 6) | (uint)l; cnt += 6; }
                    while (cnt >= 8) { cnt -= 8; *o++ = (byte)(acc >> cnt); }
                }
                if (cnt > 0) *o++ = (byte)(acc << (8 - cnt));
                byte* dataStart = o;
                acc = 0; cnt = 0;

                long rcode = codes[rlc];
                int rlen = len[rlc];
                long nBits = 0;
                int pos = 0;
                while (pos < n)
                {
                    int sym = w[pos], run = 1;
                    while (pos + run < n && run < 256 && w[pos + run] == sym) run++;
                    pos += run;
                    int sl = pl[sym];
                    long sc = pc[sym];
                    int reps = run;
                    // symbol + run code + 8 bit count, when that is shorter than repeating the symbol
                    bool useRun = sl + rlen + 8 < sl * (run - 1);
                    if (useRun) reps = 1;
                    for (int i = 0; i < reps; i++)
                    {
                        Put(ref acc, ref cnt, ref o, sc, sl);
                        nBits += sl;
                    }
                    if (useRun)
                    {
                        Put(ref acc, ref cnt, ref o, rcode, rlen);
                        Put(ref acc, ref cnt, ref o, run - 1, 8);
                        nBits += rlen + 8;
                    }
                }
                if (cnt > 0) *o++ = (byte)(acc << (8 - cnt));

                var hdr = new Span<byte>(ob + start, 20);
                BinaryPrimitives.WriteInt32LittleEndian(hdr, im);
                BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(4), iM);
                BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(8), (int)(dataStart - (ob + start + 20)));
                BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(12), checked((int)nBits));
                BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(16), 0);
                return (int)(o - (ob + start));
            }
        }

        static void Put(ref ulong acc, ref int cnt, ref byte* o, long code, int len)
        {
            if (len > 32)
            {
                Put(ref acc, ref cnt, ref o, code >> 32, len - 32);
                code &= 0xffffffffL; len = 32;
            }
            acc = (acc << len) | (ulong)code;
            cnt += len;
            while (cnt >= 8) { cnt -= 8; *o++ = (byte)(acc >> cnt); }
        }
    }
}
