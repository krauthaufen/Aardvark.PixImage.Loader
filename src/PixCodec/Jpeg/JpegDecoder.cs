using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace PixCodec.JpegImpl
{
    internal sealed unsafe class JpegComponent
    {
        public int Id, H, V, Tq;
        /// <summary>Allocated size in blocks (whole MCUs of an interleaved scan).</summary>
        public int BlocksW, BlocksH;
        /// <summary>Size in blocks of a non-interleaved scan of this component.</summary>
        public int ScanBlocksW, ScanBlocksH;
        /// <summary>Samples that carry image content (the rest of the plane is block padding).</summary>
        public int SampledW, SampledH;
        public int Stride;
        public byte[]? PlaneArr;
        public byte* Plane;
        public short[]? CoefArr;
        public short* Coefs;
        public int[]? QuantArr;
        public int* Quant;
        public int DcPred;
        public HuffDecodeTable? Dc, Ac;
    }

    internal enum JpegColorSpace { Gray, YCbCr, Rgb, Cmyk, Ycck }

    internal sealed unsafe class JpegDecoder
    {
        const int MaxScans = 1000;

        readonly byte* _d;
        readonly int _len;

        public int Width, Height;
        public bool Progressive;
        public JpegComponent[] Comps = Array.Empty<JpegComponent>();
        public int HMax, VMax;
        int _mcusX, _mcusY;
        bool _allocated;
        int _scans;
        int _restartInterval;

        readonly int[]?[] _quant = new int[4][];
        readonly HuffDecodeTable?[] _dc = new HuffDecodeTable[4];
        readonly HuffDecodeTable?[] _ac = new HuffDecodeTable[4];

        bool _jfif, _adobe;
        int _adobeTransform;
        public readonly ImageMetadata Metadata = new ImageMetadata();
        bool _hasMetadata;
        List<(int seq, int count, byte[] data)>? _icc;

        public JpegDecoder(byte* data, int length)
        {
            _d = data; _len = length;
        }

        public bool HasMetadata => _hasMetadata;

        public JpegColorSpace ColorSpace
        {
            get
            {
                switch (Comps.Length)
                {
                    case 1: return JpegColorSpace.Gray;
                    case 3:
                        if (_jfif) return JpegColorSpace.YCbCr;
                        if (_adobe) return _adobeTransform == 0 ? JpegColorSpace.Rgb : JpegColorSpace.YCbCr;
                        if (Comps[0].Id == 'R' && Comps[1].Id == 'G' && Comps[2].Id == 'B') return JpegColorSpace.Rgb;
                        return JpegColorSpace.YCbCr;
                    default:
                        return _adobe && _adobeTransform != 0 ? JpegColorSpace.Ycck : JpegColorSpace.Cmyk;
                }
            }
        }

        static ImageFormatException Bad(string what) => new ImageFormatException("invalid JPEG: " + what);

        int U16(int pos) => _d[pos] << 8 | _d[pos + 1];

        /// <summary>Advances to the next marker (skipping garbage and fill bytes); returns its code or -1 at the end of the data.</summary>
        int NextMarker(ref int pos)
        {
            while (true)
            {
                while (pos < _len && _d[pos] != 0xFF) pos++;
                while (pos < _len && _d[pos] == 0xFF) pos++;
                if (pos >= _len) return -1;
                int code = _d[pos++];
                if (code != 0) return code;
            }
        }

        /// <summary>Walks the marker segments. With <paramref name="headerOnly"/> it stops after the frame header.</summary>
        public void Parse(bool headerOnly)
        {
            if (_len < 4 || _d[0] != 0xFF || _d[1] != 0xD8) throw Bad("missing SOI marker");
            int pos = 2;
            while (true)
            {
                int m = NextMarker(ref pos);
                if (m < 0 || m == 0xD9) break;
                if (m == 0xD8 || m == 0x01 || (m >= 0xD0 && m <= 0xD7)) continue; // parameterless
                if (pos + 2 > _len) { if (_scans > 0) break; throw Bad("truncated header"); }
                int len = U16(pos);
                if (len < 2) throw Bad("bad segment length");
                if (pos + len > _len) { if (_scans > 0) break; throw Bad("truncated header"); }
                int p = pos + 2, n = len - 2;
                pos += len;
                switch (m)
                {
                    case 0xC0: case 0xC1: case 0xC2:
                        ReadFrame(m, p, n);
                        if (headerOnly) return;
                        break;
                    case 0xC3: case 0xC7: case 0xCB: case 0xCF:
                        throw new ImageNotSupportedException("lossless JPEG is not supported");
                    case 0xC5: case 0xC6: case 0xCD: case 0xCE:
                        throw new ImageNotSupportedException("hierarchical JPEG is not supported");
                    case 0xC9: case 0xCA:
                        throw new ImageNotSupportedException("arithmetic coded JPEG is not supported");
                    case 0xC4: ReadHuffmanTables(p, n); break;
                    case 0xDB: ReadQuantTables(p, n); break;
                    case 0xDD:
                        if (n != 2) throw Bad("bad DRI segment");
                        _restartInterval = U16(p);
                        break;
                    case 0xDA:
                        if (Comps.Length == 0) throw Bad("scan before frame header");
                        if (headerOnly) return;
                        pos = ReadScan(p, n);
                        break;
                    case 0xE0: ReadApp0(p, n); break;
                    case 0xE1: ReadApp1(p, n); break;
                    case 0xE2: ReadApp2(p, n); break;
                    case 0xEE: ReadApp14(p, n); break;
                }
            }
            if (Comps.Length == 0) throw Bad("no frame header");
            if (headerOnly) return;
            if (_scans == 0) throw Bad("no image data");
            if (Progressive) IdctAll();
            FinishIcc();
        }

        // ---------------------------------------------------------------- headers

        void ReadFrame(int marker, int p, int n)
        {
            if (Comps.Length != 0) throw Bad("multiple frame headers");
            if (n < 6) throw Bad("bad frame header");
            int precision = _d[p];
            Height = U16(p + 1);
            Width = U16(p + 3);
            int nc = _d[p + 5];
            if (n != 6 + 3 * nc) throw Bad("bad frame header");
            if (precision != 8)
            {
                if (precision == 12 && marker != 0xC0) throw new ImageNotSupportedException("12 bit JPEG is not supported");
                throw Bad("unsupported sample precision " + precision);
            }
            if (Width == 0) throw Bad("zero width");
            if (Height == 0) throw new ImageNotSupportedException("JPEG with DNL defined height is not supported");
            if (nc == 0) throw Bad("no components");
            if (nc != 1 && nc != 3 && nc != 4) throw new ImageNotSupportedException($"JPEG with {nc} components is not supported");
            Progressive = marker == 0xC2;

            var comps = new JpegComponent[nc];
            for (int i = 0; i < nc; i++)
            {
                var c = new JpegComponent { Id = _d[p + 6 + i * 3], H = _d[p + 7 + i * 3] >> 4, V = _d[p + 7 + i * 3] & 15, Tq = _d[p + 8 + i * 3] };
                if (c.H < 1 || c.H > 4 || c.V < 1 || c.V > 4) throw Bad("bad sampling factor");
                if (c.Tq > 3) throw Bad("bad quantization table index");
                for (int j = 0; j < i; j++) if (comps[j].Id == c.Id) throw Bad("duplicate component id");
                HMax = Math.Max(HMax, c.H);
                VMax = Math.Max(VMax, c.V);
                comps[i] = c;
            }
            _mcusX = (Width + 8 * HMax - 1) / (8 * HMax);
            _mcusY = (Height + 8 * VMax - 1) / (8 * VMax);
            foreach (var c in comps)
            {
                c.SampledW = (Width * c.H + HMax - 1) / HMax;
                c.SampledH = (Height * c.V + VMax - 1) / VMax;
                c.ScanBlocksW = (c.SampledW + 7) / 8;
                c.ScanBlocksH = (c.SampledH + 7) / 8;
                c.BlocksW = _mcusX * c.H;
                c.BlocksH = _mcusY * c.V;
                c.Stride = c.BlocksW * 8;
            }
            Comps = comps;
        }

        void Allocate(int remainingBytes)
        {
            long pixels = (long)Width * Height;
            // Even a flat image needs a bit per block (512 pixels per byte at best); refuse headers that promise far
            // more than the data can hold. Small images are exempt so that heavily truncated files still decode.
            if (pixels > 1 << 16 && pixels / 1024 > remainingBytes) throw Bad("image dimensions do not fit the amount of data");
            if (pixels * (Comps.Length == 1 ? 1 : 3) > Array.MaxLength) throw new ImageNotSupportedException("JPEG image is too large");
            foreach (var c in Comps)
            {
                long size = (long)c.Stride * c.BlocksH * 8;
                if (size > Array.MaxLength) throw new ImageNotSupportedException("JPEG image is too large");
                c.PlaneArr = GC.AllocateUninitializedArray<byte>((int)size, pinned: true);
                fixed (byte* q = c.PlaneArr) c.Plane = q;
                // blocks that never get decoded (truncated file) come out mid gray
                c.PlaneArr.AsSpan().Fill(128);
                if (Progressive)
                {
                    c.CoefArr = GC.AllocateArray<short>((int)size, pinned: true);
                    fixed (short* q = c.CoefArr) c.Coefs = q;
                }
            }
            _allocated = true;
        }

        void ReadQuantTables(int p, int n)
        {
            int end = p + n;
            while (p < end)
            {
                int pq = _d[p] >> 4, tq = _d[p] & 15;
                p++;
                if (pq > 1 || tq > 3) throw Bad("bad quantization table");
                if (p + 64 * (pq + 1) > end) throw Bad("truncated quantization table");
                var t = new int[64];
                for (int i = 0; i < 64; i++)
                {
                    t[JpegTables.ZigZag[i]] = pq == 0 ? _d[p] : U16(p);
                    p += pq + 1;
                }
                _quant[tq] = t;
            }
        }

        void ReadHuffmanTables(int p, int n)
        {
            int end = p + n;
            while (p < end)
            {
                int tc = _d[p] >> 4, th = _d[p] & 15;
                p++;
                if (tc > 1 || th > 3) throw Bad("bad Huffman table id");
                if (p + 16 > end) throw Bad("truncated Huffman table");
                var counts = new ReadOnlySpan<byte>(_d + p, 16);
                int total = 0;
                for (int i = 0; i < 16; i++) total += counts[i];
                p += 16;
                if (total > 256 || p + total > end) throw Bad("bad Huffman table");
                var table = new HuffDecodeTable(counts, new ReadOnlySpan<byte>(_d + p, total));
                p += total;
                if (tc == 0) _dc[th] = table; else _ac[th] = table;
            }
        }

        void ReadApp0(int p, int n)
        {
            if (n < 12 || !new ReadOnlySpan<byte>(_d + p, 5).SequenceEqual("JFIF\0"u8)) return;
            _jfif = true;
            int units = _d[p + 7], xd = U16(p + 8), yd = U16(p + 10);
            double scale = units == 1 ? 1 : units == 2 ? 2.54 : 0;
            if (scale != 0 && xd != 0 && yd != 0)
            {
                Metadata.DpiX = xd * scale;
                Metadata.DpiY = yd * scale;
                _hasMetadata = true;
            }
        }

        void ReadApp1(int p, int n)
        {
            if (n < 6 || Metadata.Exif != null || !new ReadOnlySpan<byte>(_d + p, 6).SequenceEqual("Exif\0\0"u8)) return;
            var exif = new ReadOnlySpan<byte>(_d + p + 6, n - 6).ToArray();
            Metadata.Exif = exif;
            Metadata.Orientation = JpegMeta.ExifOrientation(exif);
            _hasMetadata = true;
        }

        void ReadApp2(int p, int n)
        {
            if (n < 14 || !new ReadOnlySpan<byte>(_d + p, 12).SequenceEqual("ICC_PROFILE\0"u8)) return;
            (_icc ??= new()).Add((_d[p + 12], _d[p + 13], new ReadOnlySpan<byte>(_d + p + 14, n - 14).ToArray()));
        }

        void FinishIcc()
        {
            if (_icc == null) return;
            // chunks are numbered 1..count; only accept a complete, consistent set
            int count = _icc[0].count;
            if (count != _icc.Count) return;
            var parts = new byte[count][];
            int total = 0;
            foreach (var (seq, cnt, data) in _icc)
            {
                if (cnt != count || seq < 1 || seq > count || parts[seq - 1] != null) return;
                parts[seq - 1] = data;
                total += data.Length;
            }
            var icc = new byte[total];
            int o = 0;
            foreach (var part in parts) { part.CopyTo(icc, o); o += part.Length; }
            Metadata.IccProfile = icc;
            _hasMetadata = true;
        }

        void ReadApp14(int p, int n)
        {
            if (n < 12 || !new ReadOnlySpan<byte>(_d + p, 5).SequenceEqual("Adobe"u8)) return;
            _adobe = true;
            _adobeTransform = _d[p + 11];
        }

        // ---------------------------------------------------------------- scans

        /// <summary>Parses the scan header at p and decodes the entropy coded data after it. Returns the position to resume marker parsing.</summary>
        int ReadScan(int p, int n)
        {
            if (n < 1) throw Bad("bad scan header");
            int ns = _d[p];
            if (ns < 1 || ns > 4 || n != 4 + 2 * ns) throw Bad("bad scan header");
            if (++_scans > MaxScans) throw Bad("too many scans");
            var sc = new JpegComponent[ns];
            int ss = _d[p + 1 + 2 * ns], se = _d[p + 2 + 2 * ns], ah = _d[p + 3 + 2 * ns] >> 4, al = _d[p + 3 + 2 * ns] & 15;
            if (!Progressive) { ss = 0; se = 63; ah = 0; al = 0; }
            else if (ss > se || se > 63 || al > 13 || ah > 13 || (ss == 0 && se != 0) || (ss != 0 && ns != 1))
                throw Bad("bad progressive scan parameters");
            if (!_allocated) Allocate(_len - p);

            for (int i = 0; i < ns; i++)
            {
                int id = _d[p + 1 + 2 * i], tables = _d[p + 2 + 2 * i];
                JpegComponent? c = null;
                foreach (var cand in Comps) if (cand.Id == id) c = cand;
                if (c == null) throw Bad("scan refers to an unknown component");
                for (int j = 0; j < i; j++) if (sc[j] == c) throw Bad("component listed twice in a scan");
                int td = tables >> 4, ta = tables & 15;
                if (td > 3 || ta > 3) throw Bad("bad Huffman table selector");
                c.Dc = _dc[td];
                c.Ac = _ac[ta];
                if (ss == 0 && ah == 0 && c.Dc == null) throw Bad("missing DC Huffman table");
                if (se != 0 && c.Ac == null) throw Bad("missing AC Huffman table");
                if (c.Quant == null)
                {
                    // the table in effect at the component's first scan is the one that applies
                    var q = _quant[c.Tq] ?? throw Bad("missing quantization table");
                    c.QuantArr = GC.AllocateUninitializedArray<int>(64, pinned: true);
                    q.CopyTo(c.QuantArr, 0);
                    fixed (int* qp = c.QuantArr) c.Quant = qp;
                }
                c.DcPred = 0;
                sc[i] = c;
            }

            var br = new JpegBitReader { End = _d + _len };
            br.Reset(_d + p + n);
            if (Progressive) DecodeProgressiveScan(sc, ss, se, ah, al, ref br);
            else DecodeSequentialScan(sc, ref br);
            return (int)(br.P - _d);
        }

        /// <summary>
        /// Called at a restart boundary: finds the next marker and decides how to continue. Returns false when the
        /// coming interval has no data (lost marker or end of scan) and its MCUs have to be skipped.
        /// </summary>
        static bool NextRestart(ref JpegBitReader br, ref int expected)
        {
            byte* q = br.P, end = br.End;
            int want = expected;
            expected = (expected + 1) & 7;
            while (true)
            {
                while (end - q >= 2 && !(q[0] == 0xFF && q[1] != 0 && q[1] != 0xFF)) q++;
                if (end - q < 2) { br.Reset(end); return false; }
                int m = q[1];
                if (m < 0xD0 || m > 0xD7) { br.Reset(q); return false; } // some other marker: the scan ended early
                int d = (m - 0xD0 - want) & 7;
                if (d == 1 || d == 2) { br.Reset(q); return false; } // a marker got lost; this interval is gone
                if (d >= 6) { q += 2; continue; } // stale marker, keep looking
                br.Reset(q + 2);
                return true;
            }
        }

        void DecodeSequentialScan(JpegComponent[] sc, ref JpegBitReader br)
        {
            int ns = sc.Length;
            bool single = ns == 1;
            int mcusX = single ? sc[0].ScanBlocksW : _mcusX, mcusY = single ? sc[0].ScanBlocksH : _mcusY;
            short* block = stackalloc short[64];
            int ri = _restartInterval, todo = ri, expected = 0;
            bool skip = false;
            for (int my = 0; my < mcusY; my++)
            {
                for (int mx = 0; mx < mcusX; mx++)
                {
                    if (ri != 0)
                    {
                        if (todo == 0)
                        {
                            skip = !NextRestart(ref br, ref expected);
                            todo = ri;
                            foreach (var c in sc) c.DcPred = 0;
                        }
                        todo--;
                    }
                    if (skip) continue;
                    foreach (var c in sc)
                    {
                        int h = single ? 1 : c.H, v = single ? 1 : c.V;
                        nint stride = c.Stride;
                        byte* row = c.Plane + (nint)my * v * 8 * stride + mx * h * 8;
                        for (int by = 0; by < v; by++, row += 8 * stride)
                        {
                            for (int bx = 0; bx < h; bx++)
                            {
                                Unsafe.InitBlockUnaligned(block, 0, 128);
                                int last = DecodeBlock(ref br, c.Dc!, c.Ac!, block, ref c.DcPred);
                                if (last == 0) JpegDct.IdctDc(block[0] * c.Quant[0], row + bx * 8, stride);
                                else JpegDct.Idct(block, c.Quant, row + bx * 8, stride);
                            }
                        }
                    }
                    if (br.Overrun)
                    {
                        // ran past the end of the data: nothing more to decode until the next restart marker
                        if (ri == 0) return;
                        skip = true;
                    }
                }
            }
        }

        /// <summary>Decodes one sequential block into natural order; returns the zigzag index of the last coefficient written.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static int DecodeBlock(ref JpegBitReader br, HuffDecodeTable dcTable, HuffDecodeTable acTable, short* block, ref int dcPred)
        {
            // the bit reader state lives in locals here so the JIT keeps it in registers
            ulong buf = br.Buf;
            int cnt = br.Cnt;
            byte* p = br.P, end = br.End;
            byte* zz = JpegTables.ZigZag;
            int* fast = dcTable.Fast;
            int e, s, v, last = 0;

            if (cnt < 32)
            {
                br.Buf = buf; br.Cnt = cnt; br.P = p;
                br.Refill();
                buf = br.Buf; cnt = br.Cnt; p = br.P;
            }
            e = fast[(int)(buf >> (64 - HuffDecodeTable.LookBits))];
            if (e != 0)
            {
                int n = e & 31;
                buf <<= n; cnt -= n;
                s = (e >> 9) & 15;
                v = e >> 16;
            }
            else
            {
                int peek = (int)(buf >> 48), len = HuffDecodeTable.LookBits + 1;
                int* limit = dcTable.Limit;
                while (peek >= limit[len]) len++;
                if (len > 16) { len = 16; s = 0; }
                else s = dcTable.Values[((peek >> (16 - len)) + dcTable.Offset[len]) & 255] & 15;
                buf <<= len; cnt -= len;
                v = 0;
            }
            if (s != 0)
            {
                v = (int)(buf >> (64 - s));
                buf <<= s; cnt -= s;
                v += ((v - (1 << (s - 1))) >> 31) & (1 - (1 << s));
            }
            dcPred += v;
            block[0] = (short)dcPred;

            fast = acTable.Fast;
            int k = 1;
            do
            {
                if (cnt < 32)
                {
                    if (end - p >= 8)
                    {
                        ulong w = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(p));
                        if (((~w - 0x0101010101010101UL) & w & 0x8080808080808080UL) == 0)
                        {
                            buf |= w >> cnt;
                            p += (63 - cnt) >> 3;
                            cnt |= 56;
                            goto filled;
                        }
                    }
                    br.Buf = buf; br.Cnt = cnt; br.P = p;
                    br.RefillSlow();
                    buf = br.Buf; cnt = br.Cnt; p = br.P;
                }
            filled:
                e = fast[(int)(buf >> (64 - HuffDecodeTable.LookBits))];
                if (e != 0)
                {
                    int n = e & 31;
                    buf <<= n; cnt -= n;
                    if ((e & HuffDecodeTable.NoValueFlag) != 0)
                    {
                        if ((e & 0x1E0) != 0x1E0) break; // EOB
                        k += 16; // ZRL
                        continue;
                    }
                    k += (e >> 5) & 15;
                    s = (e >> 9) & 15;
                    v = e >> 16;
                }
                else
                {
                    int peek = (int)(buf >> 48), len = HuffDecodeTable.LookBits + 1;
                    int* limit = acTable.Limit;
                    while (peek >= limit[len]) len++;
                    int sym;
                    if (len > 16) { len = 16; sym = 0; }
                    else sym = acTable.Values[((peek >> (16 - len)) + acTable.Offset[len]) & 255];
                    buf <<= len; cnt -= len;
                    s = sym & 15;
                    if (s == 0)
                    {
                        if (sym != 0xF0) break;
                        k += 16;
                        continue;
                    }
                    k += sym >> 4;
                    v = 0;
                }
                if (s != 0)
                {
                    v = (int)(buf >> (64 - s));
                    buf <<= s; cnt -= s;
                    v += ((v - (1 << (s - 1))) >> 31) & (1 - (1 << s));
                }
                block[zz[k]] = (short)v;
                last = k;
                k++;
            } while (k < 64);

            br.Buf = buf; br.Cnt = cnt; br.P = p;
            return last;
        }

        void DecodeProgressiveScan(JpegComponent[] sc, int ss, int se, int ah, int al, ref JpegBitReader br)
        {
            int ns = sc.Length;
            bool single = ns == 1;
            int mcusX = single ? sc[0].ScanBlocksW : _mcusX, mcusY = single ? sc[0].ScanBlocksH : _mcusY;
            int ri = _restartInterval, todo = ri, expected = 0, eobrun = 0;
            bool skip = false;
            for (int my = 0; my < mcusY; my++)
            {
                for (int mx = 0; mx < mcusX; mx++)
                {
                    if (ri != 0)
                    {
                        if (todo == 0)
                        {
                            skip = !NextRestart(ref br, ref expected);
                            todo = ri;
                            eobrun = 0;
                            foreach (var c in sc) c.DcPred = 0;
                        }
                        todo--;
                    }
                    if (skip) continue;
                    foreach (var c in sc)
                    {
                        int h = single ? 1 : c.H, v = single ? 1 : c.V;
                        for (int by = 0; by < v; by++)
                        {
                            short* blk = c.Coefs + ((nint)(my * v + by) * c.BlocksW + mx * h) * 64;
                            for (int bx = 0; bx < h; bx++, blk += 64)
                            {
                                if (ss == 0)
                                {
                                    if (ah == 0)
                                    {
                                        int s = br.DecodeSymbol(c.Dc!) & 15;
                                        if (s != 0) c.DcPred += br.ReceiveExtend(s);
                                        blk[0] = (short)(c.DcPred << al);
                                    }
                                    else if (br.GetBit() != 0) blk[0] |= (short)(1 << al);
                                }
                                else if (ah == 0)
                                {
                                    if (eobrun > 0) eobrun--;
                                    else DecodeAcFirst(ref br, c.Ac!, blk, ss, se, al, ref eobrun);
                                }
                                else DecodeAcRefine(ref br, c.Ac!, blk, ss, se, al, ref eobrun);
                            }
                        }
                    }
                    if (br.Overrun)
                    {
                        if (ri == 0) return;
                        skip = true;
                    }
                }
            }
        }

        /// <summary>First pass over an AC band (progressive coefficients are stored in zigzag order). The caller handles a pending EOB run.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void DecodeAcFirst(ref JpegBitReader br, HuffDecodeTable table, short* blk, int ss, int se, int al, ref int eobrun)
        {
            // same structure as DecodeBlock: bit reader state in locals
            ulong buf = br.Buf;
            int cnt = br.Cnt;
            byte* p = br.P, end = br.End;
            int* fast = table.Fast;
            int k = ss;
            do
            {
                if (cnt < 32)
                {
                    if (end - p >= 8)
                    {
                        ulong w = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(p));
                        if (((~w - 0x0101010101010101UL) & w & 0x8080808080808080UL) == 0)
                        {
                            buf |= w >> cnt;
                            p += (63 - cnt) >> 3;
                            cnt |= 56;
                            goto filled;
                        }
                    }
                    br.Buf = buf; br.Cnt = cnt; br.P = p;
                    br.RefillSlow();
                    buf = br.Buf; cnt = br.Cnt; p = br.P;
                }
            filled:
                int e = fast[(int)(buf >> (64 - HuffDecodeTable.LookBits))];
                int r, s, v;
                if (e != 0)
                {
                    int n = e & 31;
                    buf <<= n; cnt -= n;
                    r = (e >> 5) & 15;
                    s = (e >> 9) & 15;
                    v = e >> 16;
                    if ((e & HuffDecodeTable.NoValueFlag) != 0) goto noValue;
                }
                else
                {
                    int peek = (int)(buf >> 48), len = HuffDecodeTable.LookBits + 1;
                    int* limit = table.Limit;
                    while (peek >= limit[len]) len++;
                    int sym;
                    if (len > 16) { len = 16; sym = 0; }
                    else sym = table.Values[((peek >> (16 - len)) + table.Offset[len]) & 255];
                    buf <<= len; cnt -= len;
                    r = sym >> 4;
                    s = sym & 15;
                    v = 0;
                    if (s == 0) goto noValue;
                }
                k += r;
                if (s != 0)
                {
                    v = (int)(buf >> (64 - s));
                    buf <<= s; cnt -= s;
                    v += ((v - (1 << (s - 1))) >> 31) & (1 - (1 << s));
                }
                blk[k & 63] = (short)(v << al);
                k++;
                continue;

            noValue:
                if (r == 15) { k += 16; continue; } // ZRL
                // EOBn: this band is finished here and in the next eobrun blocks
                eobrun = (1 << r) - 1;
                if (r != 0)
                {
                    eobrun += (int)(buf >> (64 - r));
                    buf <<= r; cnt -= r;
                }
                break;
            } while (k <= se);
            br.Buf = buf; br.Cnt = cnt; br.P = p;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Refine(ref JpegBitReader br, short* c, int plus, int minus)
        {
            // one more magnitude bit for a coefficient that is already nonzero
            if (br.GetBit() != 0 && (*c & plus) == 0) *c += (short)(*c >= 0 ? plus : minus);
        }

        static void DecodeAcRefine(ref JpegBitReader br, HuffDecodeTable table, short* blk, int ss, int se, int al, ref int eobrun)
        {
            int plus = 1 << al, minus = -1 << al;
            // coefficients of the band that were nonzero before this scan; everything else in it is still zero
            ulong old = JpegTables.NonZeroMask(blk) & JpegTables.BandMask(ss, se);
            int k = ss;
            if (eobrun == 0)
            {
                while (k <= se)
                {
                    int sym = br.DecodeSymbol(table), r = sym >> 4, s = sym & 15, value = 0;
                    if (s == 0)
                    {
                        if (r < 15)
                        {
                            eobrun = 1 << r;
                            if (r != 0) eobrun += br.GetBits(r);
                            break;
                        }
                        // ZRL: skip 16 zero coefficients (r stays 15)
                    }
                    else value = br.GetBit() != 0 ? plus : minus; // a new coefficient always has magnitude 1

                    // step over r still-zero coefficients, stopping at the one after them; every old nonzero
                    // coefficient on the way gets a correction bit
                    while (k <= se)
                    {
                        ulong ahead = old >> k;
                        int gap = ahead == 0 ? 64 : System.Numerics.BitOperations.TrailingZeroCount(ahead);
                        if (gap > r) { k += r; break; }
                        r -= gap;
                        k += gap;
                        Refine(ref br, blk + k, plus, minus);
                        k++;
                    }
                    if (k > se) break;
                    if (value != 0) blk[k] = (short)value;
                    k++;
                }
            }
            if (eobrun > 0)
            {
                // inside an end-of-band run only correction bits remain
                for (ulong rest = k > 63 ? 0 : old >> k << k; rest != 0; rest &= rest - 1)
                    Refine(ref br, blk + System.Numerics.BitOperations.TrailingZeroCount(rest), plus, minus);
                eobrun--;
            }
        }

        /// <summary>Progressive: turn the accumulated coefficients into samples once all scans are in.</summary>
        void IdctAll()
        {
            short* nat = stackalloc short[64];
            byte* zz = JpegTables.ZigZag;
            foreach (var c in Comps)
            {
                if (c.Quant == null) continue; // never appeared in a scan: stays gray
                nint stride = c.Stride;
                for (int by = 0; by < c.ScanBlocksH; by++)
                {
                    short* blk = c.Coefs + (nint)by * c.BlocksW * 64;
                    byte* dst = c.Plane + by * 8 * stride;
                    for (int bx = 0; bx < c.ScanBlocksW; bx++, blk += 64, dst += 8)
                    {
                        ulong mask = JpegTables.NonZeroMask(blk) & ~1UL;
                        if (mask == 0) { JpegDct.IdctDc(blk[0] * c.Quant[0], dst, stride); continue; }
                        // zigzag -> natural order
                        Unsafe.InitBlockUnaligned(nat, 0, 128);
                        nat[0] = blk[0];
                        for (; mask != 0; mask &= mask - 1)
                        {
                            int k = System.Numerics.BitOperations.TrailingZeroCount(mask);
                            nat[zz[k]] = blk[k];
                        }
                        JpegDct.Idct(nat, c.Quant, dst, stride);
                    }
                }
            }
        }
    }

    internal static class JpegMeta
    {
        /// <summary>Extracts the Orientation tag (0x0112) from IFD0 of an EXIF blob; 0 if absent or malformed.</summary>
        public static int ExifOrientation(ReadOnlySpan<byte> e)
        {
            if (e.Length < 8) return 0;
            bool le;
            if (e[0] == 'I' && e[1] == 'I') le = true;
            else if (e[0] == 'M' && e[1] == 'M') le = false;
            else return 0;
            if (R16(e, 2, le) != 42) return 0;
            long ifd = R32(e, 4, le);
            if (ifd + 2 > e.Length) return 0;
            int count = R16(e, (int)ifd, le);
            for (int i = 0; i < count; i++)
            {
                long entry = ifd + 2 + i * 12L;
                if (entry + 12 > e.Length) return 0;
                if (R16(e, (int)entry, le) != 0x0112) continue;
                int type = R16(e, (int)entry + 2, le);
                int v = type == 3 ? R16(e, (int)entry + 8, le) : type == 4 || type == 9 ? (int)R32(e, (int)entry + 8, le) : type == 1 ? e[(int)entry + 8] : 0;
                return v >= 1 && v <= 8 ? v : 0;
            }
            return 0;
        }

        static int R16(ReadOnlySpan<byte> e, int o, bool le) => le ? e[o] | e[o + 1] << 8 : e[o] << 8 | e[o + 1];
        static uint R32(ReadOnlySpan<byte> e, int o, bool le)
            => le ? (uint)(e[o] | e[o + 1] << 8 | e[o + 2] << 16 | e[o + 3] << 24) : (uint)(e[o] << 24 | e[o + 1] << 16 | e[o + 2] << 8 | e[o + 3]);
    }
}
