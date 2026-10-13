using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace PixCodec.JpegImpl
{
    /// <summary>How a component plane is brought to output resolution.</summary>
    internal enum JpegUpsample { Direct, Replicate, Fractional, H2V1, H2V2, H1V2 }

    internal sealed unsafe class JpegComponent
    {
        public int Id, H, V, Tq;
        /// <summary>Allocated size in data units (blocks, or samples for lossless): whole MCUs of an interleaved scan.</summary>
        public int BlocksW, BlocksH;
        /// <summary>Size in data units of a non-interleaved scan of this component.</summary>
        public int ScanBlocksW, ScanBlocksH;
        /// <summary>Output samples per data unit edge: 8, or 4 / 2 / 1 with DCT domain downscaling (1 for lossless).</summary>
        public int S = 8;
        /// <summary>Samples that carry image content (the rest of the plane is padding).</summary>
        public int SampledW, SampledH;
        public int Stride;
        public byte[]? PlaneArr;
        public byte* Plane;
        public ushort[]? Plane16Arr;
        public ushort* Plane16;
        public short[]? CoefArr;
        public short* Coefs;
        public int[]? QuantArr;
        public int* Quant;
        public int DcPred;
        public HuffDecodeTable? Dc, Ac;
        // arithmetic coding: conditioning table numbers and the DC context carried from block to block
        public int DcTableId, AcTableId, DcContext;
        /// <summary>Lossless point transform of the scan that coded this component.</summary>
        public int PointTransform;
        public bool Decoded;
        public JpegUpsample Upsample;
        public int HExp = 1, VExp = 1;
        // fractional ratios: source step per output sample as HNum / HDen
        public int HNum = 1, HDen = 1, VNum = 1, VDen = 1;
    }

    internal enum JpegColorSpace
    {
        Gray, YCbCr, Rgb, Cmyk, Ycck,
        /// <summary>All components as stored, interleaved.</summary>
        Raw,
        /// <summary>YCCK with only the YCbCr part converted: 4 channels in the stored (Adobe: inverted) CMYK convention.</summary>
        YcckToCmyk,
    }

    internal sealed unsafe class JpegDecoder
    {
        const int MaxScans = 1000;

        readonly byte* _d;
        readonly int _len;

        /// <summary>Frame size as coded.</summary>
        public int Width, Height;
        /// <summary>Size after DCT domain downscaling.</summary>
        public int OutWidth, OutHeight;
        public int Precision = 8;
        public bool Progressive, Lossless, Arithmetic;
        /// <summary>1, 2, 4 or 8; set before parsing. Ignored by lossless frames.</summary>
        public int ScaleDenominator = 1;
        public JpegComponent[] Comps = Array.Empty<JpegComponent>();
        public int HMax, VMax;
        int _minS = 8;
        int _mcusX, _mcusY;
        bool _geometry, _allocated;
        int _scans;
        int _restartInterval;

        readonly int[]?[] _quant = new int[4][];
        readonly HuffDecodeTable?[] _dc = new HuffDecodeTable[4];
        readonly HuffDecodeTable?[] _ac = new HuffDecodeTable[4];
        JpegArith? _arith;

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

        /// <summary>Samples wider than 8 bit, or lossless: planes are 16 bit.</summary>
        public bool Wide => Precision > 8 || Lossless;

        /// <summary>Set for a tables-only stream (TIFF JPEGTables): no frame or scan is expected.</summary>
        public bool TablesOnly;

        /// <summary>Starts out with the tables defined by another (tables-only) stream. Tables are immutable, so sharing is safe.</summary>
        public void ImportTables(JpegDecoder other)
        {
            for (int i = 0; i < 4; i++)
            {
                _quant[i] = other._quant[i];
                _dc[i] = other._dc[i];
                _ac[i] = other._ac[i];
            }
            _restartInterval = other._restartInterval;
            if (other._arith != null)
            {
                _arith = new JpegArith();
                other._arith.DcL.CopyTo(_arith.DcL, 0);
                other._arith.DcU.CopyTo(_arith.DcU, 0);
                other._arith.AcK.CopyTo(_arith.AcK, 0);
            }
        }

        public void SetTables(int[]?[] quant, HuffDecodeTable?[] dc, HuffDecodeTable?[] ac, int restartInterval)
        {
            for (int i = 0; i < 4; i++)
            {
                _quant[i] = quant[i];
                _dc[i] = dc[i];
                _ac[i] = ac[i];
            }
            _restartInterval = restartInterval;
        }

        public void GetTables(int[]?[] quant, HuffDecodeTable?[] dc, HuffDecodeTable?[] ac, out int restartInterval)
        {
            for (int i = 0; i < 4; i++)
            {
                quant[i] = _quant[i];
                dc[i] = _dc[i];
                ac[i] = _ac[i];
            }
            restartInterval = _restartInterval;
        }

        /// <summary>The color space libjpeg would assume from the markers and component ids.</summary>
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
                    case 4:
                        return _adobe && _adobeTransform != 0 ? JpegColorSpace.Ycck : JpegColorSpace.Cmyk;
                    default:
                        return JpegColorSpace.Raw;
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

        /// <summary>Walks the marker segments. With <paramref name="headerOnly"/> it stops once the frame geometry is known.</summary>
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
                    case 0xC0: case 0xC1: case 0xC2: case 0xC3: case 0xC9: case 0xCA:
                        ReadFrame(m, p, n);
                        if (headerOnly && Height != 0) { SetupGeometry(); return; }
                        break;
                    case 0xCB:
                        throw new ImageNotSupportedException("arithmetic coded lossless JPEG is not supported");
                    case 0xC5: case 0xC6: case 0xC7: case 0xCD: case 0xCE: case 0xCF:
                        throw new ImageNotSupportedException("hierarchical JPEG is not supported");
                    case 0xC4: ReadHuffmanTables(p, n); break;
                    case 0xCC: ReadConditioning(p, n); break;
                    case 0xDB: ReadQuantTables(p, n); break;
                    case 0xDD:
                        if (n != 2) throw Bad("bad DRI segment");
                        _restartInterval = U16(p);
                        break;
                    case 0xDA:
                        if (TablesOnly) break;
                        if (Comps.Length == 0) throw Bad("scan before frame header");
                        if (Height == 0) FindNumberOfLines(pos);
                        if (headerOnly) { SetupGeometry(); return; }
                        pos = ReadScan(p, n);
                        break;
                    case 0xE0: ReadApp0(p, n); break;
                    case 0xE1: ReadApp1(p, n); break;
                    case 0xE2: ReadApp2(p, n); break;
                    case 0xEE: ReadApp14(p, n); break;
                }
            }
            if (TablesOnly) return;
            if (Comps.Length == 0) throw Bad("no frame header");
            if (headerOnly)
            {
                if (Height == 0) throw Bad("image height is not defined");
                SetupGeometry();
                return;
            }
            if (_scans == 0) throw Bad("no image data");
            if (Progressive) IdctAll();
            if (Lossless) FinishLossless();
            FinishIcc();
        }

        // ---------------------------------------------------------------- headers

        void ReadFrame(int marker, int p, int n)
        {
            if (TablesOnly) return;
            if (Comps.Length != 0) throw Bad("multiple frame headers");
            if (n < 6) throw Bad("bad frame header");
            Precision = _d[p];
            Height = U16(p + 1);
            Width = U16(p + 3);
            int nc = _d[p + 5];
            if (n != 6 + 3 * nc) throw Bad("bad frame header");
            Lossless = marker == 0xC3;
            Progressive = marker == 0xC2 || marker == 0xCA;
            Arithmetic = marker == 0xC9 || marker == 0xCA;
            bool precisionOk = Lossless ? Precision >= 2 && Precision <= 16 : Precision == 8 || (Precision == 12 && marker != 0xC0);
            if (!precisionOk) throw Bad("unsupported sample precision " + Precision);
            if (Width == 0) throw Bad("zero width");
            if (nc == 0) throw Bad("no components");
            if (nc > 4) throw new ImageNotSupportedException($"JPEG with {nc} components is not supported");

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
            Comps = comps;
        }

        /// <summary>
        /// A frame header with height 0 leaves the number of lines to a DNL segment after the first scan (T.81 B.2.5).
        /// Looks ahead over the entropy coded data starting at <paramref name="pos"/> for it.
        /// </summary>
        void FindNumberOfLines(int pos)
        {
            for (; pos + 1 < _len; pos++)
            {
                if (_d[pos] != 0xFF) continue;
                int m = _d[pos + 1];
                if (m == 0 || m == 0xFF || (m >= 0xD0 && m <= 0xD7)) continue;
                if (m == 0xDC && pos + 6 <= _len && U16(pos + 2) == 4) Height = U16(pos + 4);
                break;
            }
            if (Height == 0) throw Bad("image height is not defined");
        }

        /// <summary>Derives all sizes from the frame header and the requested scale.</summary>
        void SetupGeometry()
        {
            if (_geometry) return;
            _geometry = true;
            int unit = Lossless ? 1 : 8;
            _minS = Lossless ? 1 : 8 / ScaleDenominator;
            _mcusX = (Width + unit * HMax - 1) / (unit * HMax);
            _mcusY = (Height + unit * VMax - 1) / (unit * VMax);
            OutWidth = (Width * _minS + unit - 1) / unit;
            OutHeight = (Height * _minS + unit - 1) / unit;
            foreach (var c in Comps)
            {
                int s = _minS;
                // a subsampled component can make up (part of) its deficit by using a larger inverse DCT than the
                // others instead of being upsampled afterwards; only while that keeps both ratios whole
                if (!Lossless)
                    while (s < 8 && HMax * _minS % (c.H * s * 2) == 0 && VMax * _minS % (c.V * s * 2) == 0) s *= 2;
                c.S = s;
                c.ScanBlocksW = ((Width * c.H + HMax - 1) / HMax + unit - 1) / unit;
                c.ScanBlocksH = ((Height * c.V + VMax - 1) / VMax + unit - 1) / unit;
                c.SampledW = (int)(((long)Width * c.H * s + (long)HMax * unit - 1) / ((long)HMax * unit));
                c.SampledH = (int)(((long)Height * c.V * s + (long)VMax * unit - 1) / ((long)VMax * unit));
                c.BlocksW = _mcusX * c.H;
                c.BlocksH = _mcusY * c.V;
                c.Stride = c.BlocksW * s;

                c.HNum = c.H * s; c.HDen = HMax * _minS;
                c.VNum = c.V * s; c.VDen = VMax * _minS;
                if (c.HDen % c.HNum != 0 || c.VDen % c.VNum != 0) { c.Upsample = JpegUpsample.Fractional; continue; }
                c.HExp = c.HDen / c.HNum;
                c.VExp = c.VDen / c.VNum;
                // triangle filters for the 2:1 cases, like libjpeg's "fancy upsampling" (which needs 3+ columns for
                // the horizontal filter); plain replication otherwise
                if (c.HExp == 1 && c.VExp == 1) c.Upsample = JpegUpsample.Direct;
                else if (c.HExp == 2 && c.VExp == 1 && c.SampledW > 2) c.Upsample = JpegUpsample.H2V1;
                else if (c.HExp == 2 && c.VExp == 2 && c.SampledW > 2) c.Upsample = JpegUpsample.H2V2;
                else if (c.HExp == 1 && c.VExp == 2) c.Upsample = JpegUpsample.H1V2;
                else c.Upsample = JpegUpsample.Replicate;
            }
        }

        void Allocate(int remainingBytes)
        {
            SetupGeometry();
            long pixels = (long)Width * Height;
            // Even a flat image needs a bit per block (512 pixels per byte at best) resp. per sample; refuse headers
            // that promise far more than the data can hold. Small images are exempt so that heavily truncated files still decode.
            if (pixels > 1 << 16 && pixels / (Lossless ? 16 : 1024) > remainingBytes) throw Bad("image dimensions do not fit the amount of data");
            if ((long)OutWidth * OutHeight * Comps.Length > Array.MaxLength) throw new ImageNotSupportedException("JPEG image is too large");
            foreach (var c in Comps)
            {
                long size = (long)c.Stride * c.BlocksH * c.S;
                long coefs = (long)c.BlocksW * c.BlocksH * 64;
                if (size > Array.MaxLength || (Progressive && coefs > Array.MaxLength)) throw new ImageNotSupportedException("JPEG image is too large");
                // data units that never get decoded (truncated file) come out mid gray
                if (Wide)
                {
                    c.Plane16Arr = GC.AllocateUninitializedArray<ushort>((int)size, pinned: true);
                    fixed (ushort* q = c.Plane16Arr) c.Plane16 = q;
                    c.Plane16Arr.AsSpan().Fill((ushort)(1 << (Precision - 1)));
                }
                else
                {
                    c.PlaneArr = GC.AllocateUninitializedArray<byte>((int)size, pinned: true);
                    fixed (byte* q = c.PlaneArr) c.Plane = q;
                    c.PlaneArr.AsSpan().Fill(128);
                }
                if (Progressive)
                {
                    c.CoefArr = GC.AllocateArray<short>((int)coefs, pinned: true);
                    fixed (short* q = c.CoefArr) c.Coefs = q;
                }
            }
            _allocated = true;
        }

        void ReadConditioning(int p, int n)
        {
            if ((n & 1) != 0) throw Bad("bad DAC segment");
            _arith ??= new JpegArith();
            for (int end = p + n; p < end; p += 2)
            {
                int tc = _d[p] >> 4, tb = _d[p] & 15, cs = _d[p + 1];
                if (tc > 1 || tb > 3) throw Bad("bad arithmetic conditioning table id");
                if (tc == 0)
                {
                    if ((cs & 15) > cs >> 4) throw Bad("bad DC conditioning");
                    _arith.DcL[tb] = (byte)(cs & 15);
                    _arith.DcU[tb] = (byte)(cs >> 4);
                }
                else
                {
                    if (cs < 1 || cs > 63) throw Bad("bad AC conditioning");
                    _arith.AcK[tb] = (byte)cs;
                }
            }
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
            if (Lossless)
            {
                // ss selects the predictor, al is the point transform
                if (ss < 1 || ss > 7 || al >= Precision) throw Bad("bad lossless scan parameters");
            }
            else if (!Progressive) { ss = 0; se = 63; ah = 0; al = 0; }
            else if (ss > se || se > 63 || al > 13 || ah > 13 || (ss == 0 && se != 0) || (ss != 0 && ns != 1))
                throw Bad("bad progressive scan parameters");
            if (!_allocated) Allocate(_len - p);
            if (Arithmetic) _arith ??= new JpegArith();

            for (int i = 0; i < ns; i++)
            {
                int id = _d[p + 1 + 2 * i], tables = _d[p + 2 + 2 * i];
                JpegComponent? c = null;
                foreach (var cand in Comps) if (cand.Id == id) c = cand;
                if (c == null) throw Bad("scan refers to an unknown component");
                for (int j = 0; j < i; j++) if (sc[j] == c) throw Bad("component listed twice in a scan");
                int td = tables >> 4, ta = tables & 15;
                if (td > 3 || ta > 3) throw Bad("bad entropy table selector");
                c.DcTableId = td;
                c.AcTableId = ta;
                if (!Arithmetic)
                {
                    c.Dc = _dc[td];
                    c.Ac = _ac[ta];
                    if ((Lossless || (ss == 0 && ah == 0)) && c.Dc == null) throw Bad("missing DC Huffman table");
                    if (!Lossless && se != 0 && c.Ac == null) throw Bad("missing AC Huffman table");
                }
                if (!Lossless && c.Quant == null)
                {
                    // the table in effect at the component's first scan is the one that applies
                    var q = _quant[c.Tq] ?? throw Bad("missing quantization table");
                    c.QuantArr = GC.AllocateUninitializedArray<int>(64, pinned: true);
                    q.CopyTo(c.QuantArr, 0);
                    fixed (int* qp = c.QuantArr) c.Quant = qp;
                }
                c.DcPred = 0;
                c.DcContext = 0;
                c.Decoded = true;
                if (Lossless) c.PointTransform = al;
                sc[i] = c;
            }

            var br = new JpegBitReader { End = _d + _len };
            br.Reset(_d + p + n);
            if (Arithmetic) _arith!.Start(_d + p + n, _d + _len);
            if (Lossless) DecodeLosslessScan(sc, ss, al, ref br);
            else if (Progressive) DecodeProgressiveScan(sc, ss, se, ah, al, ref br);
            else DecodeSequentialScan(sc, ref br);
            return (int)((Arithmetic ? _arith!.Position : br.P) - _d);
        }

        /// <summary>
        /// Called at a restart boundary with the current read position: finds the next marker and decides how to
        /// continue. Returns false when the coming interval has no data (lost marker or end of scan) and its MCUs
        /// have to be skipped.
        /// </summary>
        static bool NextRestart(ref byte* q, byte* end, ref int expected)
        {
            int want = expected;
            expected = (expected + 1) & 7;
            while (true)
            {
                while (end - q >= 2 && !(q[0] == 0xFF && q[1] != 0 && q[1] != 0xFF)) q++;
                if (end - q < 2) { q = end; return false; }
                int m = q[1];
                if (m < 0xD0 || m > 0xD7) return false; // some other marker: the scan ended early
                int d = (m - 0xD0 - want) & 7;
                if (d == 1 || d == 2) return false; // a marker got lost; this interval is gone
                q += 2;
                if (d >= 6) continue; // stale marker, keep looking
                return true;
            }
        }

        /// <summary>Restart boundary handling shared by all scan types. Returns true if the next interval must be skipped.</summary>
        bool Restart(JpegComponent[] sc, ref JpegBitReader br, ref int expected)
        {
            byte* q = Arithmetic ? _arith!.Position : br.P;
            bool ok = NextRestart(ref q, _d + _len, ref expected);
            if (Arithmetic) _arith!.Start(q, _d + _len); else br.Reset(q);
            foreach (var c in sc) { c.DcPred = 0; c.DcContext = 0; }
            return !ok;
        }

        /// <summary>Inverse transforms a natural order block into its place in the component plane.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void PutBlock(JpegComponent c, short* block, bool dcOnly, int col, int row)
        {
            if (c.Plane != null && c.S == 8)
            {
                byte* dst = c.Plane + ((nint)row * c.Stride + col) * 8;
                if (dcOnly) JpegDct.IdctDc(block[0] * c.Quant[0], dst, c.Stride);
                else JpegDct.Idct(block, c.Quant, dst, c.Stride);
            }
            else PutBlockGeneric(c, block, col, row);
        }

        [SkipLocalsInit]
        void PutBlockGeneric(JpegComponent c, short* block, int col, int row)
        {
            int* v = stackalloc int[64];
            int s = c.S;
            JpegDct.IdctGeneric(block, c.Quant, s, Precision == 8 ? 2 : 1, v);
            int center = 1 << (Precision - 1), max = (1 << Precision) - 1;
            if (c.Plane != null)
            {
                byte* dst = c.Plane + ((nint)row * c.Stride + col) * s;
                for (int y = 0; y < s; y++, dst += c.Stride)
                    for (int x = 0; x < s; x++) dst[x] = (byte)Math.Clamp(v[y * s + x] + center, 0, max);
            }
            else
            {
                ushort* dst = c.Plane16 + ((nint)row * c.Stride + col) * s;
                for (int y = 0; y < s; y++, dst += c.Stride)
                    for (int x = 0; x < s; x++) dst[x] = (ushort)Math.Clamp(v[y * s + x] + center, 0, max);
            }
        }

        void DecodeSequentialScan(JpegComponent[] sc, ref JpegBitReader br)
        {
            int ns = sc.Length;
            bool single = ns == 1, arith = Arithmetic;
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
                            skip = Restart(sc, ref br, ref expected);
                            todo = ri;
                        }
                        todo--;
                    }
                    if (skip) continue;
                    foreach (var c in sc)
                    {
                        int h = single ? 1 : c.H, v = single ? 1 : c.V;
                        for (int by = 0; by < v; by++)
                        {
                            for (int bx = 0; bx < h; bx++)
                            {
                                Unsafe.InitBlockUnaligned(block, 0, 128);
                                int last = arith ? _arith!.DecodeBlock(c, block) : DecodeBlock(ref br, c.Dc!, c.Ac!, block, ref c.DcPred);
                                PutBlock(c, block, last == 0, mx * h + bx, my * v + by);
                            }
                        }
                    }
                    if (arith ? _arith!.Corrupt : br.Overrun)
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
            bool single = ns == 1, arith = Arithmetic;
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
                            skip = Restart(sc, ref br, ref expected);
                            todo = ri;
                            eobrun = 0;
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
                                if (arith)
                                {
                                    if (ss == 0)
                                    {
                                        if (ah == 0) _arith!.DecodeDcFirst(c, blk, al);
                                        else _arith!.DecodeDcRefine(blk, al);
                                    }
                                    else if (ah == 0) _arith!.DecodeAcFirst(c, blk, ss, se, al);
                                    else _arith!.DecodeAcRefine(c, blk, ss, se, al);
                                }
                                else if (ss == 0)
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
                    if (arith ? _arith!.Corrupt : br.Overrun)
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
                for (int by = 0; by < c.ScanBlocksH; by++)
                {
                    short* blk = c.Coefs + (nint)by * c.BlocksW * 64;
                    for (int bx = 0; bx < c.ScanBlocksW; bx++, blk += 64)
                    {
                        ulong mask = JpegTables.NonZeroMask(blk) & ~1UL;
                        // zigzag -> natural order
                        Unsafe.InitBlockUnaligned(nat, 0, 128);
                        nat[0] = blk[0];
                        for (ulong m = mask; m != 0; m &= m - 1)
                        {
                            int k = System.Numerics.BitOperations.TrailingZeroCount(m);
                            nat[zz[k]] = blk[k];
                        }
                        PutBlock(c, nat, mask == 0, bx, by);
                    }
                }
            }
        }

        // ---------------------------------------------------------------- lossless (T.81 Annex H)

        /// <summary>One prediction difference: Huffman coded magnitude category 0..16, then that many bits (none for 16 = 32768).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int DecodeDifference(ref JpegBitReader br, HuffDecodeTable table)
        {
            if (br.Cnt < 32) br.Refill();
            int e = table.Fast[(int)(br.Buf >> (64 - HuffDecodeTable.LookBits))], s;
            if (e != 0)
            {
                int n = e & 31;
                br.Buf <<= n; br.Cnt -= n;
                // the table was built for run/size symbols: category 16 looks like "run 1, size 0"
                if ((e & HuffDecodeTable.NoValueFlag) != 0) return (e & 0x1E0) != 0 ? 32768 : 0;
                s = (e >> 9) & 15;
                if (s == 0) return e >> 16;
            }
            else
            {
                s = br.DecodeSymbol(table);
                if (s == 0) return 0;
                if (s >= 16) return 32768;
            }
            int v = (int)(br.Buf >> (64 - s));
            br.Buf <<= s; br.Cnt -= s;
            return v + (((v - (1 << (s - 1))) >> 31) & (1 - (1 << s)));
        }

        void DecodeLosslessScan(JpegComponent[] sc, int predictor, int pt, ref JpegBitReader br)
        {
            int ns = sc.Length;
            bool single = ns == 1;
            int mcusX = single ? sc[0].ScanBlocksW : _mcusX, mcusY = single ? sc[0].ScanBlocksH : _mcusY;
            int initial = 1 << (Precision - pt - 1);
            int ri = _restartInterval, todo = ri, expected = 0;
            // prediction restarts at the top of the scan and after every restart marker
            int topRow = 0, firstCol = 0;
            bool skip = false;
            for (int my = 0; my < mcusY; my++)
            {
                for (int mx = 0; mx < mcusX; mx++)
                {
                    if (ri != 0)
                    {
                        if (todo == 0)
                        {
                            skip = Restart(sc, ref br, ref expected);
                            todo = ri;
                            topRow = my; firstCol = mx;
                        }
                        todo--;
                    }
                    if (skip) continue;
                    foreach (var c in sc)
                    {
                        int h = single ? 1 : c.H, v = single ? 1 : c.V;
                        nint stride = c.Stride;
                        var table = c.Dc!;
                        for (int by = 0; by < v; by++)
                        {
                            ushort* row = c.Plane16 + (nint)(my * v + by) * stride;
                            bool top = my == topRow && by == 0;
                            for (int bx = 0; bx < h; bx++)
                            {
                                int x = mx * h + bx, pred;
                                if (top) pred = mx == firstCol && bx == 0 ? initial : row[x - 1];
                                else if (x == 0) pred = row[-stride];
                                else
                                {
                                    int ra = row[x - 1], rb = row[x - stride], rc = row[x - stride - 1];
                                    pred = predictor switch
                                    {
                                        1 => ra,
                                        2 => rb,
                                        3 => rc,
                                        4 => ra + rb - rc,
                                        5 => ra + ((rb - rc) >> 1),
                                        6 => rb + ((ra - rc) >> 1),
                                        _ => (ra + rb) >> 1,
                                    };
                                }
                                row[x] = (ushort)(pred + DecodeDifference(ref br, table));
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

        /// <summary>Undoes the point transform and blanks components no scan delivered.</summary>
        void FinishLossless()
        {
            foreach (var c in Comps)
            {
                if (c.PointTransform == 0) continue;
                var plane = c.Plane16Arr.AsSpan();
                for (int i = 0; i < plane.Length; i++) plane[i] = (ushort)(plane[i] << c.PointTransform);
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

        /// <summary>
        /// Brings pixels stored with the given EXIF orientation (2..8) upright. Orientations 5..8 swap width and height.
        /// </summary>
        public static T[] Reorient<T>(T[] src, ref int w, ref int h, int ch, int orientation) where T : unmanaged
        {
            if (orientation < 2 || orientation > 8) return src;
            int sw = w, sh = h;
            bool swap = orientation >= 5;
            int dw = swap ? sh : sw, dh = swap ? sw : sh;
            var dst = GC.AllocateUninitializedArray<T>(src.Length);
            for (int y = 0; y < dh; y++)
                for (int x = 0; x < dw; x++)
                {
                    // source position of destination pixel (x, y)
                    int sx, sy;
                    switch (orientation)
                    {
                        case 2: sx = sw - 1 - x; sy = y; break;          // mirrored left-right
                        case 3: sx = sw - 1 - x; sy = sh - 1 - y; break; // rotated 180
                        case 4: sx = x; sy = sh - 1 - y; break;          // mirrored top-bottom
                        case 5: sx = y; sy = x; break;                   // transposed
                        case 6: sx = y; sy = sh - 1 - x; break;          // needs 90 clockwise
                        case 7: sx = sw - 1 - y; sy = sh - 1 - x; break; // transversed
                        default: sx = sw - 1 - y; sy = x; break;         // needs 90 counterclockwise
                    }
                    int s = (sy * sw + sx) * ch, d = (y * dw + x) * ch;
                    for (int c = 0; c < ch; c++) dst[d + c] = src[s + c];
                }
            w = dw; h = dh;
            return dst;
        }

        static int R16(ReadOnlySpan<byte> e, int o, bool le) => le ? e[o] | e[o + 1] << 8 : e[o] << 8 | e[o + 1];
        static uint R32(ReadOnlySpan<byte> e, int o, bool le)
            => le ? (uint)(e[o] | e[o + 1] << 8 | e[o + 2] << 16 | e[o + 3] << 24) : (uint)(e[o] << 24 | e[o + 1] << 16 | e[o + 2] << 8 | e[o + 3]);
    }
}
