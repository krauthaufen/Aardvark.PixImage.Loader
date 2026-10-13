using System;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace PixCodec.JpegImpl
{
    /// <summary>
    /// Baseline / progressive Huffman JPEG encoder. All quantized coefficients are kept in memory (zigzag order),
    /// which makes optimized Huffman tables and multi scan output a matter of walking them more than once.
    /// </summary>
    internal sealed unsafe class JpegEncoder
    {
        sealed class Comp
        {
            public int Id, H, V, Tq;
            public int BlocksW, BlocksH;         // whole MCUs
            public int ScanBlocksW, ScanBlocksH; // blocks covering the image, for non-interleaved scans
            public int Stride;
            public byte[] Plane = null!;
            public short[] Coefs = null!;
            public int Pred;
            public HuffEnc Dc = null!, Ac = null!;
        }

        sealed class HuffEnc
        {
            public readonly int[] Freq = new int[257];
            public readonly uint[] Code = new uint[256];
            public readonly byte[] Size = new byte[256];
            public byte[] Counts = new byte[16];
            public byte[] Values = Array.Empty<byte>();

            public HuffEnc() { }

            public HuffEnc(ReadOnlySpan<byte> counts, ReadOnlySpan<byte> values)
            {
                Counts = counts.ToArray();
                Values = values.ToArray();
                AssignCodes();
            }

            void AssignCodes()
            {
                Array.Clear(Size);
                uint code = 0;
                int k = 0;
                for (int len = 1; len <= 16; len++, code <<= 1)
                    for (int i = 0; i < Counts[len - 1]; i++, k++)
                    {
                        Code[Values[k]] = code++;
                        Size[Values[k]] = (byte)len;
                    }
            }

            /// <summary>Builds length limited (16 bit) Huffman codes from <see cref="Freq"/> following T.81 Annex K.2.</summary>
            public void BuildFromFrequencies()
            {
                Span<int> freq = stackalloc int[257];
                Span<int> codeSize = stackalloc int[257];
                Span<int> others = stackalloc int[257];
                Freq.CopyTo(freq);
                freq[256] = 1; // reserves one code point so no real symbol gets the all-ones code
                others.Fill(-1);
                while (true)
                {
                    // the two least frequent trees; ties go to the larger index
                    int c1 = -1, c2 = -1;
                    for (int i = 0; i <= 256; i++)
                        if (freq[i] != 0 && (c1 < 0 || freq[i] <= freq[c1])) c1 = i;
                    for (int i = 0; i <= 256; i++)
                        if (freq[i] != 0 && i != c1 && (c2 < 0 || freq[i] <= freq[c2])) c2 = i;
                    if (c2 < 0) break;
                    freq[c1] += freq[c2];
                    freq[c2] = 0;
                    // every symbol of both trees moves one level down; then chain tree 2 onto tree 1
                    codeSize[c1]++;
                    while (others[c1] >= 0) { c1 = others[c1]; codeSize[c1]++; }
                    others[c1] = c2;
                    codeSize[c2]++;
                    while (others[c2] >= 0) { c2 = others[c2]; codeSize[c2]++; }
                }

                Span<int> bits = stackalloc int[258];
                bits.Clear();
                for (int i = 0; i <= 256; i++) bits[codeSize[i]]++;
                // shorten codes longer than 16 bits: move a pair of the longest up next to a split shorter code
                for (int i = 257; i > 16; i--)
                {
                    while (bits[i] > 0)
                    {
                        int j = i - 2;
                        while (bits[j] == 0) j--;
                        bits[i] -= 2;
                        bits[i - 1]++;
                        bits[j + 1] += 2;
                        bits[j]--;
                    }
                }
                int longest = 16;
                while (bits[longest] == 0) longest--;
                bits[longest]--; // drop the reserved code point

                Counts = new byte[16];
                int total = 0;
                for (int i = 1; i <= 16; i++) { Counts[i - 1] = (byte)bits[i]; total += bits[i]; }
                Values = new byte[total];
                int k = 0;
                for (int len = 1; len <= 256 && k < total; len++)
                    for (int s = 0; s < 256; s++)
                        if (codeSize[s] == len) Values[k++] = (byte)s;
                AssignCodes();
            }
        }

        // ---------------------------------------------------------------- output

        readonly Stream _out;
        readonly byte[] _buf = new byte[1 << 16];
        int _pos;
        ulong _acc;
        int _accBits;
        bool _gather; // statistics pass: count symbols instead of writing them

        void Reserve(int n)
        {
            if (_pos + n > _buf.Length)
            {
                _out.Write(_buf, 0, _pos);
                _pos = 0;
            }
        }

        void Byte(int b) { Reserve(1); _buf[_pos++] = (byte)b; }
        void U16(int v) { Reserve(2); _buf[_pos++] = (byte)(v >> 8); _buf[_pos++] = (byte)v; }
        void Marker(int m) { Byte(0xFF); Byte(m); }

        void Bytes(ReadOnlySpan<byte> data)
        {
            while (data.Length > 0)
            {
                Reserve(1);
                int n = Math.Min(data.Length, _buf.Length - _pos);
                data.Slice(0, n).CopyTo(_buf.AsSpan(_pos));
                _pos += n;
                data = data.Slice(n);
            }
        }

        /// <summary>Appends up to 32 bits. The caller reserves buffer space (8 bytes per call) beforehand.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Put(uint bits, int n)
        {
            _acc = _acc << n | bits;
            _accBits += n;
            if (_accBits >= 32)
            {
                _accBits -= 32;
                uint w = (uint)(_acc >> _accBits);
                if (((~w - 0x01010101u) & w & 0x80808080u) == 0)
                {
                    Unsafe.WriteUnaligned(ref _buf[_pos], System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(w));
                    _pos += 4;
                }
                else
                {
                    for (int s = 24; s >= 0; s -= 8)
                    {
                        byte b = (byte)(w >> s);
                        _buf[_pos++] = b;
                        if (b == 0xFF) _buf[_pos++] = 0;
                    }
                }
            }
        }

        /// <summary>Pads the pending bits with ones to a byte boundary and writes them out.</summary>
        void FlushBits()
        {
            Reserve(16);
            while (_accBits > 0)
            {
                int take = Math.Min(8, _accBits);
                byte b = (byte)((_acc >> (_accBits - take) << (8 - take)) | (0xFFu >> take));
                _accBits -= take;
                _buf[_pos++] = b;
                if (b == 0xFF) _buf[_pos++] = 0;
            }
            _acc = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Symbol(HuffEnc t, int sym)
        {
            if (_gather) t.Freq[sym]++;
            else Put(t.Code[sym], t.Size[sym]);
        }

        /// <summary>A symbol followed by nbits raw bits (taken from the low end of <paramref name="bits"/>).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Symbol(HuffEnc t, int sym, int bits, int nbits)
        {
            if (_gather) t.Freq[sym]++;
            else Put(t.Code[sym] << nbits | ((uint)bits & ((1u << nbits) - 1)), t.Size[sym] + nbits);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Raw(int bits, int nbits)
        {
            if (!_gather) Put((uint)bits & ((1u << nbits) - 1), nbits);
        }

        // ---------------------------------------------------------------- setup

        readonly JpegEncodeOptions _opt;
        readonly int _w, _h;
        readonly Comp[] _comps;
        readonly int _hMax, _vMax, _mcusX, _mcusY;
        readonly byte[][] _quant = new byte[2][];
        readonly int _restart;

        public static void Encode(RawImage image, Stream output, JpegEncodeOptions options)
        {
            ArgumentNullException.ThrowIfNull(image);
            ArgumentNullException.ThrowIfNull(output);
            if (image.SampleType != SampleType.UInt8)
                throw new ImageNotSupportedException($"JPEG encoding supports 8 bit samples only, not {image.SampleType}");
            if (image.Color == ColorModel.Other)
                throw new ImageNotSupportedException("JPEG encoding needs a gray or RGB image");
            if (image.Width > 65535 || image.Height > 65535)
                throw new ImageNotSupportedException("image is too large for JPEG (65535 x 65535 at most)");
            var e = new JpegEncoder(image, output, options);
            e.Run(image);
        }

        JpegEncoder(RawImage image, Stream output, JpegEncodeOptions opt)
        {
            _out = output; _opt = opt;
            _w = image.Width; _h = image.Height;
            _restart = Math.Clamp(opt.RestartInterval, 0, 65535);
            bool gray = image.Color == ColorModel.Gray || image.Color == ColorModel.GrayAlpha;
            int quality = Math.Clamp(opt.Quality, 1, 100);
            var ss = opt.Subsampling;
            if (ss == JpegSubsampling.Auto) ss = quality >= 90 ? JpegSubsampling.Chroma444 : JpegSubsampling.Chroma420;
            _hMax = gray || ss == JpegSubsampling.Chroma444 ? 1 : 2;
            _vMax = !gray && ss == JpegSubsampling.Chroma420 ? 2 : 1;
            _mcusX = (_w + 8 * _hMax - 1) / (8 * _hMax);
            _mcusY = (_h + 8 * _vMax - 1) / (8 * _vMax);

            _comps = new Comp[gray ? 1 : 3];
            for (int i = 0; i < _comps.Length; i++)
            {
                var c = new Comp { Id = i + 1, H = i == 0 ? _hMax : 1, V = i == 0 ? _vMax : 1, Tq = i == 0 ? 0 : 1 };
                c.BlocksW = _mcusX * c.H;
                c.BlocksH = _mcusY * c.V;
                c.ScanBlocksW = ((_w * c.H + _hMax - 1) / _hMax + 7) / 8;
                c.ScanBlocksH = ((_h * c.V + _vMax - 1) / _vMax + 7) / 8;
                c.Stride = c.BlocksW * 8;
                long size = (long)c.Stride * c.BlocksH * 8;
                if (size > Array.MaxLength) throw new ImageNotSupportedException("image is too large to encode");
                c.Plane = GC.AllocateUninitializedArray<byte>((int)size);
                c.Coefs = GC.AllocateUninitializedArray<short>((int)size);
                _comps[i] = c;
            }

            // IJG quality scaling of the Annex K tables
            int scale = quality < 50 ? 5000 / quality : 200 - quality * 2;
            for (int t = 0; t < 2; t++)
            {
                var basis = t == 0 ? JpegTables.LumaQuant : JpegTables.ChromaQuant;
                var q = new byte[64];
                for (int i = 0; i < 64; i++) q[i] = (byte)Math.Clamp((basis[i] * scale + 50) / 100, 1, 255);
                _quant[t] = q;
            }
        }

        void Run(RawImage image)
        {
            FillPlanes(image);
            Transform();
            WriteHeaders(image.Metadata);
            if (_opt.Progressive) WriteProgressive();
            else WriteBaseline();
            Marker(0xD9);
            _out.Write(_buf, 0, _pos);
            _pos = 0;
        }

        // ---------------------------------------------------------------- color conversion and downsampling

        void FillPlanes(RawImage image)
        {
            int ch = image.Channels, w = _w, h = _h;
            var src = image.GetData<byte>();
            var yc = _comps[0];
            int fullW = yc.Stride; // luma plane width: a whole number of MCUs
            if (_comps.Length == 1)
            {
                for (int y = 0; y < h; y++)
                {
                    var row = yc.Plane.AsSpan(y * fullW, fullW);
                    if (ch == 1) src.AsSpan(y * w, w).CopyTo(row);
                    else for (int x = 0; x < w; x++) row[x] = src[(y * w + x) * ch];
                    row.Slice(w).Fill(row[w - 1]);
                }
            }
            else
            {
                var cbc = _comps[1]; var crc = _comps[2];
                // full resolution chroma rows, two of them when halving vertically
                var cb = new byte[fullW * 2]; var cr = new byte[fullW * 2];
                int rowsPerPass = _vMax;
                for (int y = 0; y < h; y += rowsPerPass)
                {
                    for (int r = 0; r < rowsPerPass; r++)
                    {
                        int sy = Math.Min(y + r, h - 1); // odd height: the last row counts twice
                        var yrow = yc.Plane.AsSpan((y + r) * fullW, fullW);
                        var cbr = cb.AsSpan(r * fullW, fullW); var crr = cr.AsSpan(r * fullW, fullW);
                        RgbToYcc(src.AsSpan(sy * w * ch, w * ch), ch, yrow, cbr, crr, w);
                        yrow.Slice(w).Fill(yrow[w - 1]);
                        cbr.Slice(w).Fill(cbr[w - 1]);
                        crr.Slice(w).Fill(crr[w - 1]);
                    }
                    int cy = y / rowsPerPass;
                    Downsample(cb, fullW, cbc.Plane.AsSpan(cy * cbc.Stride, cbc.Stride));
                    Downsample(cr, fullW, crc.Plane.AsSpan(cy * crc.Stride, crc.Stride));
                }
            }
            // pad downwards to whole blocks by repeating the last row
            foreach (var c in _comps)
            {
                int rows = (h * c.V + _vMax - 1) / _vMax;
                if (_vMax > 1 && c.V == _vMax) rows = (h + 1) / 2 * 2; // luma rows were written in pairs
                var last = c.Plane.AsSpan((rows - 1) * c.Stride, c.Stride);
                for (int y = rows; y < c.BlocksH * 8; y++) last.CopyTo(c.Plane.AsSpan(y * c.Stride, c.Stride));
            }
        }

        void Downsample(byte[] full, int fullW, Span<byte> dst)
        {
            if (_hMax == 1) full.AsSpan(0, fullW).CopyTo(dst);
            else if (_vMax == 1)
            {
                // alternate the rounding bias so flat areas do not drift
                for (int x = 0; x < dst.Length; x++) dst[x] = (byte)((full[2 * x] + full[2 * x + 1] + (x & 1)) >> 1);
            }
            else
            {
                var a = full.AsSpan(0, fullW); var b = full.AsSpan(fullW, fullW);
                for (int x = 0; x < dst.Length; x++)
                    dst[x] = (byte)((a[2 * x] + a[2 * x + 1] + b[2 * x] + b[2 * x + 1] + 1 + (x & 1)) >> 2);
            }
        }

        /// <summary>JFIF (BT.601 full range) RGB to YCbCr in 16 bit fixed point.</summary>
        static void RgbToYcc(ReadOnlySpan<byte> src, int ch, Span<byte> yo, Span<byte> cbo, Span<byte> cro, int w)
        {
            const int half = 32768, center = (128 << 16) + 32767;
            fixed (byte* sp = src, yp = yo, cbp = cbo, crp = cro)
            {
                byte* s = sp;
                for (int x = 0; x < w; x++, s += ch)
                {
                    int r = s[0], g = s[1], b = s[2];
                    yp[x] = (byte)((19595 * r + 38470 * g + 7471 * b + half) >> 16);
                    cbp[x] = (byte)((-11059 * r - 21709 * g + 32768 * b + center) >> 16);
                    crp[x] = (byte)((32768 * r - 27439 * g - 5329 * b + center) >> 16);
                }
            }
        }

        // ---------------------------------------------------------------- DCT

        void Transform()
        {
            float* recip = stackalloc float[64];
            foreach (var c in _comps)
            {
                var q = _quant[c.Tq];
                // FdctQuantize wants the divisors transposed and scaled by the DCT's factor of 8
                for (int i = 0; i < 64; i++) recip[(i & 7) * 8 + (i >> 3)] = 1f / (8 * q[i]);
                fixed (byte* plane = c.Plane)
                fixed (short* coefs = c.Coefs)
                {
                    short* o = coefs;
                    for (int by = 0; by < c.BlocksH; by++)
                    {
                        byte* row = plane + (nint)by * 8 * c.Stride;
                        for (int bx = 0; bx < c.BlocksW; bx++, o += 64)
                            JpegDct.FdctQuantize(row + bx * 8, c.Stride, recip, o);
                    }
                }
            }
        }

        // ---------------------------------------------------------------- headers

        void WriteHeaders(ImageMetadata? meta)
        {
            Marker(0xD8);
            // JFIF 1.01
            Marker(0xE0); U16(16);
            Bytes("JFIF\0"u8); Byte(1); Byte(1);
            int dx = meta == null ? 0 : (int)Math.Round(meta.DpiX), dy = meta == null ? 0 : (int)Math.Round(meta.DpiY);
            if (dx >= 1 && dy >= 1 && dx <= 65535 && dy <= 65535) { Byte(1); U16(dx); U16(dy); }
            else { Byte(0); U16(1); U16(1); }
            Byte(0); Byte(0);

            byte[]? exif = meta?.Exif;
            if (exif == null && meta != null && meta.Orientation >= 1 && meta.Orientation <= 8)
            {
                // minimal little endian TIFF structure holding just the Orientation tag
                exif = [(byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0, 1, 0, 0x12, 0x01, 3, 0, 1, 0, 0, 0, (byte)meta.Orientation, 0, 0, 0, 0, 0, 0, 0];
            }
            if (exif != null && exif.Length <= 65533 - 6)
            {
                Marker(0xE1); U16(exif.Length + 8);
                Bytes("Exif\0\0"u8);
                Bytes(exif);
            }

            byte[]? icc = meta?.IccProfile;
            const int iccChunk = 65533 - 14;
            if (icc != null && icc.Length > 0 && icc.Length <= iccChunk * 255)
            {
                int count = (icc.Length + iccChunk - 1) / iccChunk;
                for (int i = 0; i < count; i++)
                {
                    int n = Math.Min(iccChunk, icc.Length - i * iccChunk);
                    Marker(0xE2); U16(n + 16);
                    Bytes("ICC_PROFILE\0"u8); Byte(i + 1); Byte(count);
                    Bytes(icc.AsSpan(i * iccChunk, n));
                }
            }

            for (int t = 0; t < (_comps.Length == 1 ? 1 : 2); t++)
            {
                Marker(0xDB); U16(67); Byte(t);
                for (int i = 0; i < 64; i++) Byte(_quant[t][JpegTables.ZigZag[i]]);
            }

            Marker(_opt.Progressive ? 0xC2 : 0xC0);
            U16(8 + 3 * _comps.Length); Byte(8); U16(_h); U16(_w); Byte(_comps.Length);
            foreach (var c in _comps) { Byte(c.Id); Byte(c.H << 4 | c.V); Byte(c.Tq); }

            if (_restart != 0) { Marker(0xDD); U16(4); U16(_restart); }
        }

        void WriteHuffmanTable(HuffEnc t, int cls, int id)
        {
            Marker(0xC4); U16(19 + t.Values.Length); Byte(cls << 4 | id);
            Bytes(t.Counts); Bytes(t.Values);
        }

        void WriteScanHeader(Comp[] comps, int[] dcIds, int[] acIds, int ss, int se, int ah, int al)
        {
            Marker(0xDA); U16(6 + 2 * comps.Length); Byte(comps.Length);
            for (int i = 0; i < comps.Length; i++) { Byte(comps[i].Id); Byte(dcIds[i] << 4 | acIds[i]); }
            Byte(ss); Byte(se); Byte(ah << 4 | al);
        }

        // ---------------------------------------------------------------- scans

        void WriteBaseline()
        {
            bool color = _comps.Length == 3;
            HuffEnc dc0, ac0, dc1, ac1;
            if (_opt.OptimizeHuffman)
            {
                dc0 = new HuffEnc(); ac0 = new HuffEnc(); dc1 = new HuffEnc(); ac1 = new HuffEnc();
            }
            else
            {
                dc0 = new HuffEnc(JpegTables.DcLumaCounts, JpegTables.DcValues);
                ac0 = new HuffEnc(JpegTables.AcLumaCounts, JpegTables.AcLumaValues);
                dc1 = new HuffEnc(JpegTables.DcChromaCounts, JpegTables.DcValues);
                ac1 = new HuffEnc(JpegTables.AcChromaCounts, JpegTables.AcChromaValues);
            }
            for (int i = 0; i < _comps.Length; i++)
            {
                _comps[i].Dc = i == 0 ? dc0 : dc1;
                _comps[i].Ac = i == 0 ? ac0 : ac1;
            }
            if (_opt.OptimizeHuffman)
            {
                _gather = true;
                EncodeScan(_comps, 0, 63, 0, 0);
                _gather = false;
                dc0.BuildFromFrequencies(); ac0.BuildFromFrequencies();
                if (color) { dc1.BuildFromFrequencies(); ac1.BuildFromFrequencies(); }
            }
            WriteHuffmanTable(dc0, 0, 0); WriteHuffmanTable(ac0, 1, 0);
            if (color) { WriteHuffmanTable(dc1, 0, 1); WriteHuffmanTable(ac1, 1, 1); }
            WriteScanHeader(_comps, color ? [0, 1, 1] : [0], color ? [0, 1, 1] : [0], 0, 63, 0, 0);
            EncodeScan(_comps, 0, 63, 0, 0);
        }

        void WriteProgressive()
        {
            // DC first with one bit held back, then the AC bands coarse to fine, each refined one bit at a time
            ProgressiveScan(_comps, 0, 0, 0, 1);
            var y = new[] { _comps[0] };
            if (_comps.Length == 3)
            {
                var cb = new[] { _comps[1] }; var cr = new[] { _comps[2] };
                ProgressiveScan(y, 1, 5, 0, 2);
                ProgressiveScan(cb, 1, 63, 0, 1);
                ProgressiveScan(cr, 1, 63, 0, 1);
                ProgressiveScan(y, 6, 63, 0, 2);
                ProgressiveScan(y, 1, 63, 2, 1);
                ProgressiveScan(_comps, 0, 0, 1, 0);
                ProgressiveScan(cb, 1, 63, 1, 0);
                ProgressiveScan(cr, 1, 63, 1, 0);
                ProgressiveScan(y, 1, 63, 1, 0);
            }
            else
            {
                ProgressiveScan(y, 1, 5, 0, 2);
                ProgressiveScan(y, 6, 63, 0, 2);
                ProgressiveScan(y, 1, 63, 2, 1);
                ProgressiveScan(y, 0, 0, 1, 0);
                ProgressiveScan(y, 1, 63, 1, 0);
            }
        }

        void ProgressiveScan(Comp[] comps, int ss, int se, int ah, int al)
        {
            var ids = new int[comps.Length];
            bool dcRefine = ss == 0 && ah != 0; // raw bits only, no table
            if (!dcRefine)
            {
                for (int i = 0; i < comps.Length; i++)
                {
                    // one table for luma and one shared by the chroma components, rebuilt for every scan
                    ids[i] = comps[i] == _comps[0] ? 0 : 1;
                    var t = i > 0 && ids[i] == ids[i - 1] ? (ss == 0 ? comps[i - 1].Dc : comps[i - 1].Ac) : new HuffEnc();
                    if (ss == 0) comps[i].Dc = t; else comps[i].Ac = t;
                }
                _gather = true;
                EncodeScan(comps, ss, se, ah, al);
                _gather = false;
                for (int i = 0; i < comps.Length; i++)
                {
                    if (i > 0 && ids[i] == ids[i - 1]) continue;
                    var t = ss == 0 ? comps[i].Dc : comps[i].Ac;
                    t.BuildFromFrequencies();
                    WriteHuffmanTable(t, ss == 0 ? 0 : 1, ids[i]);
                }
            }
            WriteScanHeader(comps, ss == 0 ? ids : new int[comps.Length], ss == 0 ? new int[comps.Length] : ids, ss, se, ah, al);
            EncodeScan(comps, ss, se, ah, al);
        }

        // end-of-band run state of progressive AC scans
        int _eobRun;
        HuffEnc _eobTable = null!;
        readonly byte[] _corr = new byte[1024]; // buffered correction bits (refinement scans)
        int _corrCount;

        /// <summary>Runs over all MCUs of a scan, either counting symbols (<see cref="_gather"/>) or writing them.</summary>
        void EncodeScan(Comp[] comps, int ss, int se, int ah, int al)
        {
            bool single = comps.Length == 1, progressive = _opt.Progressive;
            int mcusX = single ? comps[0].ScanBlocksW : _mcusX, mcusY = single ? comps[0].ScanBlocksH : _mcusY;
            int todo = _restart, rst = 0;
            foreach (var c in comps) c.Pred = 0;
            _eobRun = 0; _corrCount = 0;
            if (ss != 0) _eobTable = comps[0].Ac;

            for (int my = 0; my < mcusY; my++)
            {
                for (int mx = 0; mx < mcusX; mx++)
                {
                    if (_restart != 0)
                    {
                        if (todo == 0)
                        {
                            if (ss != 0) FlushEobRun();
                            if (!_gather) { FlushBits(); Marker(0xD0 + rst); }
                            rst = (rst + 1) & 7;
                            todo = _restart;
                            foreach (var c in comps) c.Pred = 0;
                        }
                        todo--;
                    }
                    foreach (var c in comps)
                    {
                        int h = single ? 1 : c.H, v = single ? 1 : c.V;
                        for (int by = 0; by < v; by++)
                        {
                            fixed (short* coefs = c.Coefs)
                            {
                                short* blk = coefs + ((nint)(my * v + by) * c.BlocksW + mx * h) * 64;
                                for (int bx = 0; bx < h; bx++, blk += 64)
                                {
                                    Reserve(1200);
                                    if (!progressive) EncodeBlock(blk, c);
                                    else if (ss == 0)
                                    {
                                        if (ah == 0) EncodeDc(blk[0] >> al, c);
                                        else Raw(blk[0] >> al, 1);
                                    }
                                    else if (ah == 0) EncodeAcFirst(blk, c.Ac, ss, se, al);
                                    else EncodeAcRefine(blk, c.Ac, ss, se, al);
                                }
                            }
                        }
                    }
                }
            }
            if (ss != 0) FlushEobRun();
            if (!_gather) FlushBits();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int BitLength(int abs) => 32 - BitOperations.LeadingZeroCount((uint)abs);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void EncodeDc(int value, Comp c)
        {
            int diff = value - c.Pred;
            c.Pred = value;
            int s = BitLength(Math.Abs(diff));
            // negative values are sent as the low bits of (diff - 1)
            Symbol(c.Dc, s, diff < 0 ? diff - 1 : diff, s);
        }

        /// <summary>Bit i is set when coefficient i (zigzag order) is nonzero.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ulong NonZeroMask(short* blk)
        {
            if (Vector128.IsHardwareAccelerated)
            {
                ulong zero = 0;
                for (int i = 0; i < 8; i++)
                    zero |= (ulong)Vector128.Equals(Vector128.Load(blk + i * 8), Vector128<short>.Zero).ExtractMostSignificantBits() << (i * 8);
                return ~zero;
            }
            ulong m = 0;
            for (int i = 0; i < 64; i++) if (blk[i] != 0) m |= 1UL << i;
            return m;
        }

        void EncodeBlock(short* blk, Comp c)
        {
            EncodeDc(blk[0], c);
            var ac = c.Ac;
            ulong mask = NonZeroMask(blk) & ~1UL;
            int prev = 0; // index of the previous nonzero coefficient
            while (mask != 0)
            {
                int k = BitOperations.TrailingZeroCount(mask);
                mask &= mask - 1;
                int run = k - prev - 1;
                prev = k;
                while (run > 15) { Symbol(ac, 0xF0); run -= 16; }
                int v = blk[k], s = BitLength(Math.Abs(v));
                if (s > 10) { v = v < 0 ? -1023 : 1023; s = 10; } // cannot happen for real DCT output, but keep the stream legal
                Symbol(ac, run << 4 | s, v < 0 ? v - 1 : v, s);
            }
            if (prev != 63) Symbol(ac, 0);
        }

        void FlushEobRun()
        {
            if (_eobRun > 0)
            {
                int n = BitLength(_eobRun) - 1;
                Symbol(_eobTable, n << 4, _eobRun, n);
                _eobRun = 0;
            }
            // correction bits of the blocks inside the run follow its symbol
            for (int i = 0; i < _corrCount; i++) Raw(_corr[i], 1);
            _corrCount = 0;
        }

        void EncodeAcFirst(short* blk, HuffEnc ac, int ss, int se, int al)
        {
            int run = 0;
            for (int k = ss; k <= se; k++)
            {
                int v = blk[k];
                int t = Math.Abs(v) >> al; // the point transform truncates toward zero
                if (t == 0) { run++; continue; }
                FlushEobRun();
                Reserve(64);
                while (run > 15) { Symbol(ac, 0xF0); run -= 16; }
                int s = BitLength(t);
                Symbol(ac, run << 4 | s, v < 0 ? ~t : t, s);
                run = 0;
            }
            if (run > 0 && ++_eobRun == 0x7FFF) FlushEobRun();
        }

        void EncodeAcRefine(short* blk, HuffEnc ac, int ss, int se, int al)
        {
            // position of the last coefficient that becomes nonzero in this scan: zero runs after it fold into the EOB
            int* abs = stackalloc int[64];
            int eob = 0;
            for (int k = ss; k <= se; k++)
            {
                abs[k] = Math.Abs(blk[k]) >> al;
                if (abs[k] == 1) eob = k;
            }

            int run = 0;
            int start = _corrCount, pending = 0; // this block's correction bits sit at _corr[start .. start + pending)
            for (int k = ss; k <= se; k++)
            {
                int t = abs[k];
                if (t == 0) { run++; continue; }
                while (run > 15 && k <= eob)
                {
                    FlushEobRun();
                    Reserve(256);
                    Symbol(ac, 0xF0);
                    run -= 16;
                    for (int i = 0; i < pending; i++) Raw(_corr[start + i], 1);
                    start = 0; pending = 0;
                }
                if (t > 1)
                {
                    // was nonzero before this scan: only its next magnitude bit is sent, after the following symbol
                    _corr[start + pending++] = (byte)(t & 1);
                    continue;
                }
                FlushEobRun();
                Reserve(256);
                Symbol(ac, run << 4 | 1, blk[k] < 0 ? 0 : 1, 1);
                for (int i = 0; i < pending; i++) Raw(_corr[start + i], 1);
                start = 0; pending = 0;
                run = 0;
            }
            if (run > 0 || pending > 0)
            {
                _eobRun++;
                _corrCount = start + pending;
                // flush before the run counter or the bit buffer could overflow with the next block
                if (_eobRun == 0x7FFF || _corrCount > _corr.Length - 64) FlushEobRun();
            }
        }
    }
}
