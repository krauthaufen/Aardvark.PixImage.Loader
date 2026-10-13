using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

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
            public ushort[]? Plane16; // 12 bit samples
            public short[] Coefs = null!;
            public int Pred;
            public HuffEnc Dc = null!, Ac = null!;
        }

        sealed class HuffEnc
        {
            public readonly int[] Freq = new int[257];
            /// <summary>code &lt;&lt; 8 | length per symbol (pinned, so the block coder can index it without bounds checks).</summary>
            public readonly uint[] Packed = GC.AllocateArray<uint>(256, pinned: true);
            /// <summary>Which of the two tables of a progressive scan this is.</summary>
            public uint Slot;
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
                Array.Clear(Packed);
                uint code = 0;
                int k = 0;
                for (int len = 1; len <= 16; len++, code <<= 1)
                    for (int i = 0; i < Counts[len - 1]; i++, k++)
                        Packed[Values[k]] = code++ << 8 | (uint)len;
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
        readonly byte[] _buf = GC.AllocateArray<byte>(1 << 16, pinned: true);
        int _pos;
        ulong _acc;
        int _accBits;
        bool _gather; // statistics pass of a sequential scan: count symbols instead of writing them

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

        /// <summary>Writes 4 entropy coded bytes, stuffing a zero after every 0xFF. Returns the advanced pointer.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static byte* Write32(byte* o, uint w)
        {
            if (((~w - 0x01010101u) & w & 0x80808080u) == 0)
            {
                Unsafe.WriteUnaligned(o, System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(w));
                return o + 4;
            }
            for (int s = 24; s >= 0; s -= 8)
            {
                byte b = (byte)(w >> s);
                *o++ = b;
                if (b == 0xFF) *o++ = 0;
            }
            return o;
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

        // Progressive scans are coded in one pass that counts the symbols and records them as tokens; once the
        // Huffman tables are known the tokens are replayed into the bit stream.
        //   bits 0-7 symbol, 8-12 number of extra bits, 13-28 extra bits, 29 table slot, 30 restart marker, 31 raw bits only
        const uint TokenRestart = 1u << 30, TokenRaw = 1u << 31;
        uint[] _tokens = new uint[1 << 16];
        int _tokenCount;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Push(uint token)
        {
            if (_tokenCount == _tokens.Length) Array.Resize(ref _tokens, _tokens.Length * 2);
            _tokens[_tokenCount++] = token;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Symbol(HuffEnc t, int sym)
        {
            t.Freq[sym]++;
            Push((uint)sym | t.Slot << 29);
        }

        /// <summary>A symbol followed by nbits raw bits (taken from the low end of <paramref name="bits"/>).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Symbol(HuffEnc t, int sym, int bits, int nbits)
        {
            t.Freq[sym]++;
            Push((uint)sym | (uint)nbits << 8 | ((uint)bits & ((1u << nbits) - 1)) << 13 | t.Slot << 29);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void RawBit(int bit)
        {
            // consecutive raw bits share a token (up to 16)
            if (_tokenCount > 0)
            {
                uint last = _tokens[_tokenCount - 1];
                if ((last & TokenRaw) != 0 && (last & 0x1F00) < 16 << 8)
                {
                    uint bits = (last >> 13 & 0xFFFF) << 1 | (uint)(bit & 1);
                    _tokens[_tokenCount - 1] = TokenRaw | ((last & 0x1F00) + 0x100) | bits << 13;
                    return;
                }
            }
            Push(TokenRaw | 1 << 8 | (uint)(bit & 1) << 13);
        }

        /// <summary>Writes the recorded tokens with the (now final) tables of the two slots.</summary>
        void Replay(HuffEnc? slot0, HuffEnc? slot1)
        {
            slot0 ??= slot1 ?? new HuffEnc();
            slot1 ??= slot0;
            fixed (uint* tokens = _tokens, t0 = slot0.Packed, t1 = slot1.Packed)
            fixed (byte* buf = _buf)
            {
                ulong acc = _acc;
                int n = _accBits;
                for (int i = 0; i < _tokenCount;)
                {
                    // each token appends at most 8 bytes (4 plus stuffing)
                    _accBits = n; _acc = acc;
                    Reserve(64 * 8 + 16);
                    byte* o = buf + _pos;
                    int stop = Math.Min(_tokenCount, i + 64);
                    for (; i < stop; i++)
                    {
                        uint tok = tokens[i];
                        int nbits = (int)(tok >> 8) & 31;
                        uint bits = tok >> 13 & 0xFFFF;
                        if (tok < TokenRestart)
                        {
                            uint e = ((tok & 1u << 29) != 0 ? t1 : t0)[tok & 255];
                            int len = (int)(e & 255) + nbits;
                            acc = acc << len | (e >> 8) << nbits | bits;
                            n += len;
                        }
                        else if ((tok & TokenRaw) != 0)
                        {
                            acc = acc << nbits | bits;
                            n += nbits;
                        }
                        else
                        {
                            _acc = acc; _accBits = n; _pos = (int)(o - buf);
                            FlushBits();
                            Marker(0xD0 + (int)(tok & 7));
                            acc = 0; n = 0;
                            o = buf + _pos;
                            stop = i + 1; // the marker used up buffer space: start a fresh batch
                            continue;
                        }
                        if (n >= 32) { n -= 32; o = Write32(o, (uint)(acc >> n)); }
                    }
                    _pos = (int)(o - buf);
                }
                _acc = acc; _accBits = n;
            }
            _tokenCount = 0;
        }

        // ---------------------------------------------------------------- setup

        readonly JpegEncodeOptions _opt;
        readonly int _w, _h;
        readonly Comp[] _comps;
        readonly int _hMax, _vMax, _mcusX, _mcusY;
        readonly byte[][] _quant = new byte[2][];
        readonly int _restart;
        readonly bool _genericSampling;
        readonly int _precision = 8;
        readonly int _maxAcSize = 10; // largest AC magnitude category the precision allows

        public static void Encode(RawImage image, Stream output, JpegEncodeOptions options)
        {
            ArgumentNullException.ThrowIfNull(image);
            ArgumentNullException.ThrowIfNull(output);
            if (image.SampleType != SampleType.UInt8 && image.SampleType != SampleType.UInt16)
                throw new ImageNotSupportedException($"JPEG encoding supports UInt8 and UInt16 samples, not {image.SampleType}");
            if (image.Width > 65535 || image.Height > 65535)
                throw new ImageNotSupportedException("image is too large for JPEG (65535 x 65535 at most)");
            bool wide = image.SampleType == SampleType.UInt16;
            if (options.Lossless)
            {
                if (image.Color == ColorModel.Other && image.Channels > 4)
                    throw new ImageNotSupportedException("lossless JPEG holds at most 4 components");
                int precision = options.Precision != 0 ? options.Precision : wide ? 16 : 8;
                if (precision < 2 || precision > (wide ? 16 : 8)) throw new ArgumentException($"lossless JPEG precision {precision} is not possible for {image.SampleType} samples");
                new JpegEncoder(output, options).RunLossless(image, precision);
                return;
            }
            if (image.Color == ColorModel.Other)
                throw new ImageNotSupportedException("JPEG encoding needs a gray or RGB image (lossless mode also takes up to 4 arbitrary channels)");
            if (options.Precision != 0 && options.Precision != (wide ? 12 : 8))
                throw new ArgumentException(wide ? "UInt16 images are encoded with 12 bit precision (or lossless with up to 16)" : "UInt8 images are encoded with 8 bit precision");
            var e = new JpegEncoder(image, output, options);
            e.Run(image);
        }

        /// <summary>Lossless mode: only the output machinery is used.</summary>
        JpegEncoder(Stream output, JpegEncodeOptions opt)
        {
            _out = output; _opt = opt;
            _comps = Array.Empty<Comp>();
        }

        JpegEncoder(RawImage image, Stream output, JpegEncodeOptions opt)
        {
            _out = output; _opt = opt;
            _w = image.Width; _h = image.Height;
            _restart = Math.Clamp(opt.RestartInterval, 0, 65535);
            if (image.SampleType == SampleType.UInt16) { _precision = 12; _maxAcSize = 14; }
            bool gray = image.Color == ColorModel.Gray || image.Color == ColorModel.GrayAlpha;
            int quality = Math.Clamp(opt.Quality, 1, 100);
            var ss = opt.Subsampling;
            if (ss == JpegSubsampling.Auto) ss = quality >= 90 ? JpegSubsampling.Chroma444 : JpegSubsampling.Chroma420;
            int[] factors = gray ? [1, 1] : ss == JpegSubsampling.Chroma444 ? [1, 1, 1, 1, 1, 1] : ss == JpegSubsampling.Chroma422 ? [2, 1, 1, 1, 1, 1] : [2, 2, 1, 1, 1, 1];
            if (opt.SamplingFactors != null && !gray)
            {
                factors = opt.SamplingFactors;
                if (factors.Length != 6 || factors.Any(f => f < 1 || f > 4)) throw new ArgumentException("SamplingFactors needs 6 values between 1 and 4");
                if (factors[0] * factors[1] + factors[2] * factors[3] + factors[4] * factors[5] > 10) throw new ArgumentException("an MCU holds at most 10 blocks");
                _genericSampling = true;
            }
            _comps = new Comp[gray ? 1 : 3];
            for (int i = 0; i < _comps.Length; i++)
            {
                _comps[i] = new Comp { Id = i + 1, H = factors[2 * i], V = factors[2 * i + 1], Tq = i == 0 ? 0 : 1 };
                _hMax = Math.Max(_hMax, _comps[i].H);
                _vMax = Math.Max(_vMax, _comps[i].V);
            }
            _mcusX = (_w + 8 * _hMax - 1) / (8 * _hMax);
            _mcusY = (_h + 8 * _vMax - 1) / (8 * _vMax);
            foreach (var c in _comps)
            {
                c.BlocksW = _mcusX * c.H;
                c.BlocksH = _mcusY * c.V;
                c.ScanBlocksW = ((_w * c.H + _hMax - 1) / _hMax + 7) / 8;
                c.ScanBlocksH = ((_h * c.V + _vMax - 1) / _vMax + 7) / 8;
                c.Stride = c.BlocksW * 8;
                long size = (long)c.Stride * c.BlocksH * 8;
                if (size > Array.MaxLength) throw new ImageNotSupportedException("image is too large to encode");
                if (_precision == 8) c.Plane = GC.AllocateUninitializedArray<byte>((int)size);
                else c.Plane16 = GC.AllocateUninitializedArray<ushort>((int)size);
                c.Coefs = GC.AllocateUninitializedArray<short>((int)size);
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
            if (_precision != 8 || _genericSampling) FillPlanesGeneric(image);
            else FillPlanes(image);
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

        /// <summary>
        /// Straightforward version of <see cref="FillPlanes"/> for the cases without a fast path (12 bit samples,
        /// arbitrary sampling factors): full resolution planes first, then an area average down to each component's size.
        /// </summary>
        void FillPlanesGeneric(RawImage image)
        {
            int w = _w, h = _h, ch = image.Channels, nc = _comps.Length;
            int center = 1 << (_precision - 1), max = (1 << _precision) - 1;
            var full = new ushort[nc][];
            for (int i = 0; i < nc; i++) full[i] = new ushort[w * h];
            byte[]? s8 = image.SampleType == SampleType.UInt8 ? image.GetData<byte>() : null;
            ushort[]? s16 = s8 == null ? image.GetData<ushort>() : null;
            for (int i = 0, n = w * h; i < n; i++)
            {
                int r = s8 != null ? s8[i * ch] : s16![i * ch];
                if (r > max) throw new ArgumentException($"sample value {r} exceeds the {_precision} bit range");
                if (nc == 1) { full[0][i] = (ushort)r; continue; }
                int g = s8 != null ? s8[i * ch + 1] : s16![i * ch + 1], b = s8 != null ? s8[i * ch + 2] : s16![i * ch + 2];
                if (g > max || b > max) throw new ArgumentException($"sample value {Math.Max(g, b)} exceeds the {_precision} bit range");
                full[0][i] = (ushort)((19595 * r + 38470 * g + 7471 * b + 32768) >> 16);
                full[1][i] = (ushort)((-11059 * r - 21709 * g + 32768 * b + (center << 16) + 32767) >> 16);
                full[2][i] = (ushort)((32768 * r - 27439 * g - 5329 * b + (center << 16) + 32767) >> 16);
            }
            for (int i = 0; i < nc; i++)
            {
                var c = _comps[i];
                int sw = (w * c.H + _hMax - 1) / _hMax, sh = (h * c.V + _vMax - 1) / _vMax;
                // horizontal, then vertical box filter; source samples past the edge repeat the last one
                var tmp = new long[sw * h];
                for (int y = 0; y < h; y++) BoxFilter(full[i], y * w, 1, w, tmp, y * sw, 1, sw, c.H, _hMax);
                var col = new long[h];
                var outCol = new long[sh];
                int rows = c.BlocksH * 8, stride = c.Stride;
                for (int x = 0; x < sw; x++)
                {
                    for (int y = 0; y < h; y++) col[y] = tmp[y * sw + x];
                    BoxFilter(col, sh, outCol, c.V, _vMax);
                    for (int y = 0; y < rows; y++)
                    {
                        // both passes left the sums unscaled: divide by the two box sizes at once
                        long div = (long)_hMax * _vMax;
                        int v = (int)((outCol[Math.Min(y, sh - 1)] + div / 2) / div);
                        if (c.Plane16 != null) c.Plane16[y * stride + x] = (ushort)v; else c.Plane[y * stride + x] = (byte)v;
                    }
                }
                for (int y = 0; y < rows; y++)
                {
                    if (c.Plane16 != null) c.Plane16.AsSpan(y * stride + sw, stride - sw).Fill(c.Plane16[y * stride + sw - 1]);
                    else c.Plane.AsSpan(y * stride + sw, stride - sw).Fill(c.Plane[y * stride + sw - 1]);
                }
            }
        }

        /// <summary>
        /// Area average of n source samples onto m outputs where one output spans den / num source samples. The
        /// results are sums over a window of weight den (the caller divides).
        /// </summary>
        static void BoxFilter(ushort[] src, int srcStart, int srcStep, int n, long[] dst, int dstStart, int dstStep, int m, int num, int den)
        {
            for (int i = 0; i < m; i++)
            {
                // in units of 1/num source samples the output covers [i * den, (i + 1) * den)
                long lo = (long)i * den, hi = lo + den, sum = 0;
                for (long j = lo / num; j * num < hi; j++)
                {
                    long overlap = Math.Min(hi, (j + 1) * num) - Math.Max(lo, j * num);
                    sum += overlap * src[srcStart + (int)Math.Min(j, n - 1) * srcStep];
                }
                dst[dstStart + i * dstStep] = sum;
            }
        }

        static void BoxFilter(long[] src, int m, long[] dst, int num, int den)
        {
            int n = src.Length;
            for (int i = 0; i < m; i++)
            {
                long lo = (long)i * den, hi = lo + den, sum = 0;
                for (long j = lo / num; j * num < hi; j++)
                    sum += (Math.Min(hi, (j + 1) * num) - Math.Max(lo, j * num)) * src[Math.Min(j, n - 1)];
                dst[i] = sum;
            }
        }

        void Downsample(byte[] full, int fullW, Span<byte> dst)
        {
            if (_hMax == 1) { full.AsSpan(0, fullW).CopyTo(dst); return; }
            int n = dst.Length, x = 0;
            fixed (byte* a = full, d = dst)
            {
                byte* b = a + fullW;
                bool v2 = _vMax == 2;
                if (Vector.IsHardwareAccelerated && BitConverter.IsLittleEndian)
                {
                    // each 16 bit lane holds a horizontal pair of samples; the rounding bias alternates between
                    // output columns (0,1 or 1,2) so flat areas do not drift
                    int vc = Vector<ushort>.Count;
                    Span<ushort> biasInit = stackalloc ushort[vc];
                    for (int i = 0; i < vc; i++) biasInit[i] = (ushort)((v2 ? 1 : 0) + (i & 1));
                    var bias = new Vector<ushort>(biasInit);
                    var low = new Vector<ushort>(0xFF);
                    for (; x + 2 * vc <= n; x += 2 * vc)
                    {
                        var p0 = Vector.Load((ushort*)(a + 2 * x));
                        var p1 = Vector.Load((ushort*)(a + 2 * x) + vc);
                        var s0 = (p0 & low) + Vector.ShiftRightLogical(p0, 8) + bias;
                        var s1 = (p1 & low) + Vector.ShiftRightLogical(p1, 8) + bias;
                        if (v2)
                        {
                            var q0 = Vector.Load((ushort*)(b + 2 * x));
                            var q1 = Vector.Load((ushort*)(b + 2 * x) + vc);
                            s0 += (q0 & low) + Vector.ShiftRightLogical(q0, 8);
                            s1 += (q1 & low) + Vector.ShiftRightLogical(q1, 8);
                        }
                        int shift = v2 ? 2 : 1;
                        Vector.Store(Vector.Narrow(Vector.ShiftRightLogical(s0, shift), Vector.ShiftRightLogical(s1, shift)), d + x);
                    }
                }
                if (v2)
                    for (; x < n; x++) d[x] = (byte)((a[2 * x] + a[2 * x + 1] + b[2 * x] + b[2 * x + 1] + 1 + (x & 1)) >> 2);
                else
                    for (; x < n; x++) d[x] = (byte)((a[2 * x] + a[2 * x + 1] + (x & 1)) >> 1);
            }
        }

        /// <summary>Byte shuffle with a run time mask (Vector128.Shuffle is only accelerated for constant masks). Indices of 0x80 give zero.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector128<byte> Shuffle(Vector128<byte> v, Vector128<byte> idx)
            => Ssse3.IsSupported ? Ssse3.Shuffle(v, idx) : AdvSimd.Arm64.VectorTableLookup(v, idx);

        /// <summary>JFIF (BT.601 full range) RGB to YCbCr in 16 bit fixed point.</summary>
        static void RgbToYcc(ReadOnlySpan<byte> src, int ch, Span<byte> yo, Span<byte> cbo, Span<byte> cro, int w)
        {
            const int half = 32768, center = (128 << 16) + 32767;
            fixed (byte* sp = src, yp = yo, cbp = cbo, crp = cro)
            {
                byte* s = sp;
                int x = 0;
                if ((Ssse3.IsSupported || AdvSimd.Arm64.IsSupported) && BitConverter.IsLittleEndian)
                {
                    // 4 pixels per step in 32 bit lanes; reads 16 source bytes, so stop while that stays inside the row
                    var spread = ch == 3
                        ? Vector128.Create((byte)0, 1, 2, 0x80, 3, 4, 5, 0x80, 6, 7, 8, 0x80, 9, 10, 11, 0x80)
                        : Vector128.Create((byte)0, 1, 2, 0x80, 4, 5, 6, 0x80, 8, 9, 10, 0x80, 12, 13, 14, 0x80);
                    var gather = Vector128.Create((byte)0, 4, 8, 12, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
                    var m = Vector128.Create(255);
                    var vhalf = Vector128.Create(half); var vcenter = Vector128.Create(center);
                    int last = w - (ch == 3 ? 6 : 4);
                    for (; x <= last; x += 4, s += 4 * ch)
                    {
                        var px = Shuffle(Vector128.Load(s), spread).AsInt32();
                        var r = px & m;
                        var g = Vector128.ShiftRightLogical(px, 8) & m;
                        var b = Vector128.ShiftRightLogical(px, 16);
                        var yv = Vector128.ShiftRightLogical(r * 19595 + g * 38470 + b * 7471 + vhalf, 16);
                        var cb = Vector128.ShiftRightLogical(Vector128.ShiftLeft(b, 15) - r * 11059 - g * 21709 + vcenter, 16);
                        var cr = Vector128.ShiftRightLogical(Vector128.ShiftLeft(r, 15) - g * 27439 - b * 5329 + vcenter, 16);
                        Unsafe.WriteUnaligned(yp + x, Shuffle(yv.AsByte(), gather).AsUInt32().ToScalar());
                        Unsafe.WriteUnaligned(cbp + x, Shuffle(cb.AsByte(), gather).AsUInt32().ToScalar());
                        Unsafe.WriteUnaligned(crp + x, Shuffle(cr.AsByte(), gather).AsUInt32().ToScalar());
                    }
                }
                for (; x < w; x++, s += ch)
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
            int* m = stackalloc int[64];
            foreach (var c in _comps)
            {
                var q = _quant[c.Tq];
                // the quantizers want the divisors transposed and scaled by the DCT's factor of 8
                for (int i = 0; i < 64; i++) recip[(i & 7) * 8 + (i >> 3)] = 1f / (8 * q[i]);
                fixed (byte* plane = c.Plane)
                fixed (ushort* plane16 = c.Plane16)
                fixed (short* coefs = c.Coefs)
                {
                    short* o = coefs;
                    for (int by = 0; by < c.BlocksH; by++)
                    {
                        if (plane16 == null)
                        {
                            byte* row = plane + (nint)by * 8 * c.Stride;
                            for (int bx = 0; bx < c.BlocksW; bx++, o += 64)
                                JpegDct.FdctQuantize(row + bx * 8, c.Stride, recip, o);
                            continue;
                        }
                        for (int bx = 0; bx < c.BlocksW; bx++, o += 64)
                        {
                            ushort* src = plane16 + ((nint)by * c.Stride + bx) * 8;
                            for (int y = 0; y < 8; y++, src += c.Stride)
                                for (int x = 0; x < 8; x++) m[y * 8 + x] = src[x] - (1 << (_precision - 1));
                            JpegDct.FdctQuantizeGeneric(m, 1, recip, o);
                        }
                    }
                }
            }
        }

        // ---------------------------------------------------------------- headers

        void WriteHeaders(ImageMetadata? meta)
        {
            WriteMetadata(meta, jfif: true);
            for (int t = 0; t < (_comps.Length == 1 ? 1 : 2); t++)
            {
                Marker(0xDB); U16(67); Byte(t);
                for (int i = 0; i < 64; i++) Byte(_quant[t][JpegTables.ZigZag[i]]);
            }

            // baseline, extended sequential (needed beyond 8 bit) or progressive
            Marker(_opt.Progressive ? 0xC2 : _precision == 8 ? 0xC0 : 0xC1);
            U16(8 + 3 * _comps.Length); Byte(_precision); U16(_h); U16(_w); Byte(_comps.Length);
            foreach (var c in _comps) { Byte(c.Id); Byte(c.H << 4 | c.V); Byte(c.Tq); }

            if (_restart != 0) { Marker(0xDD); U16(4); U16(_restart); }
        }

        /// <summary>SOI and the application segments. A JFIF header implies gray or YCbCr, so RGB / raw data goes without.</summary>
        void WriteMetadata(ImageMetadata? meta, bool jfif)
        {
            Marker(0xD8);
            if (jfif)
            {
                // JFIF 1.01
                Marker(0xE0); U16(16);
                Bytes("JFIF\0"u8); Byte(1); Byte(1);
                int dx = meta == null ? 0 : (int)Math.Round(meta.DpiX), dy = meta == null ? 0 : (int)Math.Round(meta.DpiY);
                if (dx >= 1 && dy >= 1 && dx <= 65535 && dy <= 65535) { Byte(1); U16(dx); U16(dy); }
                else { Byte(0); U16(1); U16(1); }
                Byte(0); Byte(0);
            }

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
            if (_opt.OptimizeHuffman || _precision != 8)
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
            if (_opt.OptimizeHuffman || _precision != 8)
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
            var slots = new HuffEnc?[2];
            bool dcRefine = ss == 0 && ah != 0; // raw bits only, no table
            if (!dcRefine)
            {
                for (int i = 0; i < comps.Length; i++)
                {
                    // one table for luma and one shared by the chroma components, rebuilt for every scan
                    ids[i] = comps[i] == _comps[0] ? 0 : 1;
                    var t = slots[ids[i]] ??= new HuffEnc { Slot = (uint)ids[i] };
                    if (ss == 0) comps[i].Dc = t; else comps[i].Ac = t;
                }
            }
            EncodeScan(comps, ss, se, ah, al); // records tokens and symbol statistics
            for (int id = 0; id < 2; id++)
            {
                if (slots[id] == null) continue;
                slots[id]!.BuildFromFrequencies();
                WriteHuffmanTable(slots[id]!, ss == 0 ? 0 : 1, id);
            }
            WriteScanHeader(comps, ss == 0 ? ids : new int[comps.Length], ss == 0 ? new int[comps.Length] : ids, ss, se, ah, al);
            Replay(slots[0], slots[1]);
            FlushBits();
        }

        // end-of-band run state of progressive AC scans
        int _eobRun;
        HuffEnc _eobTable = null!;
        readonly byte[] _corr = new byte[1024]; // buffered correction bits (refinement scans)
        int _corrCount;

        /// <summary>
        /// Runs over all MCUs of a scan. Sequential scans either count symbols (<see cref="_gather"/>) or write them,
        /// progressive scans record tokens for <see cref="Replay"/>.
        /// </summary>
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
                            if (progressive) Push(TokenRestart | (uint)rst);
                            else if (!_gather) { FlushBits(); Marker(0xD0 + rst); }
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
                                    if (!progressive) { Reserve(600); EncodeBlock(blk, c); }
                                    else if (ss == 0)
                                    {
                                        if (ah == 0) EncodeDc(blk[0] >> al, c);
                                        else RawBit(blk[0] >> al);
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
            if (!progressive && !_gather) FlushBits();
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

        void EncodeBlock(short* blk, Comp c)
        {
            if (_gather) GatherBlock(blk, c, _maxAcSize);
            else fixed (byte* buf = _buf) fixed (uint* dc = c.Dc.Packed, ac = c.Ac.Packed) EmitBlock(blk, c, buf, dc, ac);
        }

        /// <summary>Statistics pass of the sequential block coder: counts the symbols <see cref="EmitBlock"/> would write.</summary>
        static void GatherBlock(short* blk, Comp c, int maxSize)
        {
            int diff = blk[0] - c.Pred;
            c.Pred = blk[0];
            c.Dc.Freq[BitLength(Math.Abs(diff))]++;
            fixed (int* freq = c.Ac.Freq)
            {
                ulong mask = JpegTables.NonZeroMask(blk) & ~1UL;
                int prev = 0;
                while (mask != 0)
                {
                    int k = BitOperations.TrailingZeroCount(mask);
                    mask &= mask - 1;
                    int run = k - prev - 1;
                    prev = k;
                    if (run > 15) { freq[0xF0] += run >> 4; run &= 15; }
                    freq[run << 4 | Math.Min(maxSize, BitLength(Math.Abs((int)blk[k])))]++;
                }
                if (prev != 63) freq[0]++;
            }
        }

        /// <summary>Sequential block coder. The bit accumulator and output pointer live in locals for the duration of the block.</summary>
        void EmitBlock(short* blk, Comp c, byte* buf, uint* dc, uint* ac)
        {
            ulong acc = _acc;
            int n = _accBits;
            byte* o = buf + _pos;

            int v = blk[0] - c.Pred;
            c.Pred = blk[0];
            int s = BitLength(Math.Abs(v));
            uint e = dc[s];
            int len = (int)(e & 255) + s;
            // negative values are sent as the low bits of (value - 1)
            acc = acc << len | (e >> 8) << s | ((uint)(v + (v >> 31)) & ((1u << s) - 1));
            n += len;
            if (n >= 32) { n -= 32; o = Write32(o, (uint)(acc >> n)); }

            ulong mask = JpegTables.NonZeroMask(blk) & ~1UL;
            int prev = 0; // index of the previous nonzero coefficient
            uint zrl = ac[0xF0];
            while (mask != 0)
            {
                int k = BitOperations.TrailingZeroCount(mask);
                mask &= mask - 1;
                int run = k - prev - 1;
                prev = k;
                while (run > 15)
                {
                    len = (int)(zrl & 255);
                    acc = acc << len | (zrl >> 8);
                    n += len;
                    if (n >= 32) { n -= 32; o = Write32(o, (uint)(acc >> n)); }
                    run -= 16;
                }
                v = blk[k];
                s = BitLength(Math.Abs(v));
                if (s > _maxAcSize) { s = _maxAcSize; v = v < 0 ? -((1 << s) - 1) : (1 << s) - 1; } // cannot happen for real DCT output, but keep the stream legal
                e = ac[run << 4 | s];
                len = (int)(e & 255) + s;
                acc = acc << len | (e >> 8) << s | ((uint)(v + (v >> 31)) & ((1u << s) - 1));
                n += len;
                if (n >= 32) { n -= 32; o = Write32(o, (uint)(acc >> n)); }
            }
            if (prev != 63)
            {
                e = ac[0];
                len = (int)(e & 255);
                acc = acc << len | (e >> 8);
                n += len;
                if (n >= 32) { n -= 32; o = Write32(o, (uint)(acc >> n)); }
            }
            _acc = acc; _accBits = n; _pos = (int)(o - buf);
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
            for (int i = 0; i < _corrCount; i++) RawBit(_corr[i]);
            _corrCount = 0;
        }

        void EncodeAcFirst(short* blk, HuffEnc ac, int ss, int se, int al)
        {
            ulong mask = JpegTables.NonZeroMask(blk) & JpegTables.BandMask(ss, se);
            int last = ss - 1; // position of the last coefficient sent
            while (mask != 0)
            {
                int k = BitOperations.TrailingZeroCount(mask);
                mask &= mask - 1;
                int v = blk[k];
                int t = Math.Abs(v) >> al; // the point transform truncates toward zero
                if (t == 0) continue;
                if (_eobRun > 0) FlushEobRun();
                int run = k - last - 1;
                last = k;
                while (run > 15) { Symbol(ac, 0xF0); run -= 16; }
                int s = BitLength(t);
                Symbol(ac, run << 4 | s, v < 0 ? ~t : t, s);
            }
            if (last != se && ++_eobRun == 0x7FFF) FlushEobRun();
        }

        void EncodeAcRefine(short* blk, HuffEnc ac, int ss, int se, int al)
        {
            ulong mask = JpegTables.NonZeroMask(blk) & JpegTables.BandMask(ss, se);
            // position of the last coefficient that becomes nonzero in this scan: zero runs after it fold into the EOB
            int eob = 0;
            for (ulong m = mask; m != 0;)
            {
                int k = 63 - BitOperations.LeadingZeroCount(m);
                if (Math.Abs((int)blk[k]) >> al == 1) { eob = k; break; }
                m &= ~(1UL << k);
            }

            int start = _corrCount, pending = 0; // this block's correction bits sit at _corr[start .. start + pending)
            int last = ss - 1; // position of the last newly nonzero coefficient
            int skipped = 0;   // positions since then that are not part of the zero run: old nonzero coefficients and zeros covered by a ZRL
            while (mask != 0)
            {
                int k = BitOperations.TrailingZeroCount(mask);
                mask &= mask - 1;
                int t = Math.Abs((int)blk[k]) >> al;
                if (t == 0) continue; // still zero at this precision
                int run = k - last - 1 - skipped;
                while (run > 15 && k <= eob)
                {
                    FlushEobRun();
                    Symbol(ac, 0xF0);
                    run -= 16; skipped += 16;
                    for (int i = 0; i < pending; i++) RawBit(_corr[start + i]);
                    start = 0; pending = 0;
                }
                if (t > 1)
                {
                    // was nonzero before this scan: only its next magnitude bit is sent, after the following symbol
                    _corr[start + pending++] = (byte)(t & 1);
                    skipped++;
                    continue;
                }
                FlushEobRun();
                Symbol(ac, run << 4 | 1, blk[k] < 0 ? 0 : 1, 1);
                for (int i = 0; i < pending; i++) RawBit(_corr[start + i]);
                start = 0; pending = 0;
                last = k; skipped = 0;
            }
            if (se - last - skipped > 0 || pending > 0)
            {
                _eobRun++;
                _corrCount = start + pending;
                // flush before the run counter or the bit buffer could overflow with the next block
                if (_eobRun == 0x7FFF || _corrCount > _corr.Length - 64) FlushEobRun();
            }
        }

        // ---------------------------------------------------------------- lossless (T.81 Annex H)

        void RunLossless(RawImage image, int precision)
        {
            int w = image.Width, h = image.Height, ch = image.Channels;
            int nc = image.Color switch { ColorModel.Gray or ColorModel.GrayAlpha => 1, ColorModel.Rgb or ColorModel.Rgba => 3, _ => ch };
            int predictor = _opt.Predictor == 0 ? 1 : _opt.Predictor;
            if (predictor < 1 || predictor > 7) throw new ArgumentException("lossless JPEG predictors are numbered 1 to 7");
            // restart intervals have to cover whole lines in lossless mode
            int restart = _opt.RestartInterval <= 0 ? 0 : (int)(((long)_opt.RestartInterval + w - 1) / w * w);
            if (restart > 65535) restart = 0;

            byte[]? s8 = image.SampleType == SampleType.UInt8 ? image.GetData<byte>() : null;
            ushort[]? s16 = s8 == null ? image.GetData<ushort>() : null;
            if (precision < (s8 != null ? 8 : 16))
            {
                int limit = 1 << precision;
                for (int i = 0, n = w * h; i < n; i++)
                    for (int c = 0; c < nc; c++)
                        if ((s8 != null ? s8[i * ch + c] : s16![i * ch + c]) >= limit) throw new ArgumentException($"sample values exceed the {precision} bit range");
            }

            var tables = new HuffEnc[nc];
            for (int c = 0; c < nc; c++) tables[c] = new HuffEnc();
            LosslessPass(s8, s16, w, h, ch, nc, precision, predictor, restart, tables, emit: false);

            bool rgb = image.Color is ColorModel.Rgb or ColorModel.Rgba;
            WriteMetadata(image.Metadata, jfif: nc == 1);
            if (rgb)
            {
                // Adobe segment with transform 0: the components are RGB, not YCbCr
                Marker(0xEE); U16(14); Bytes("Adobe"u8); U16(100); U16(0); U16(0); Byte(0);
            }
            Marker(0xC3); U16(8 + 3 * nc); Byte(precision); U16(h); U16(w); Byte(nc);
            for (int c = 0; c < nc; c++) { Byte(rgb ? "RGB"[c] : c + 1); Byte(0x11); Byte(0); }
            for (int c = 0; c < nc; c++)
            {
                tables[c].BuildFromFrequencies();
                WriteHuffmanTable(tables[c], 0, c);
            }
            if (restart != 0) { Marker(0xDD); U16(4); U16(restart); }
            Marker(0xDA); U16(6 + 2 * nc); Byte(nc);
            for (int c = 0; c < nc; c++) { Byte(rgb ? "RGB"[c] : c + 1); Byte(c << 4); }
            Byte(predictor); Byte(0); Byte(0);
            LosslessPass(s8, s16, w, h, ch, nc, precision, predictor, restart, tables, emit: true);
            FlushBits();
            Marker(0xD9);
            _out.Write(_buf, 0, _pos);
            _pos = 0;
        }

        /// <summary>Predicts every sample and either counts the difference categories or writes the coded differences.</summary>
        void LosslessPass(byte[]? s8, ushort[]? s16, int w, int h, int ch, int nc, int precision, int predictor, int restart, HuffEnc[] tables, bool emit)
        {
            // current and previous line as plain ints, components interleaved
            var cur = new int[w * nc];
            var prev = new int[w * nc];
            int rowsPerInterval = restart == 0 ? int.MaxValue : restart / w;
            int topRow = 0, rst = 0;
            ulong acc = _acc;
            int n = _accBits;
            fixed (byte* buf = _buf)
            {
                for (int y = 0; y < h; y++)
                {
                    if (y != 0 && y % rowsPerInterval == 0)
                    {
                        if (emit)
                        {
                            _acc = acc; _accBits = n;
                            FlushBits();
                            Marker(0xD0 + rst);
                            acc = 0; n = 0;
                        }
                        rst = (rst + 1) & 7;
                        topRow = y;
                    }
                    (cur, prev) = (prev, cur);
                    for (int x = 0, i = y * w * ch; x < w; x++, i += ch)
                        for (int c = 0; c < nc; c++) cur[x * nc + c] = s8 != null ? s8[i + c] : s16![i + c];

                    for (int x0 = 0; x0 < w; x0 += 256)
                    {
                        // every sample appends at most 8 bytes (31 bits plus stuffing)
                        if (emit) Reserve(256 * 4 * 8 + 16);
                        byte* o = buf + _pos;
                        int x1 = Math.Min(w, x0 + 256);
                        for (int x = x0; x < x1; x++)
                        {
                            for (int c = 0; c < nc; c++)
                            {
                                int k = x * nc + c, pred;
                                if (y == topRow) pred = x == 0 ? 1 << (precision - 1) : cur[k - nc];
                                else if (x == 0) pred = prev[k];
                                else
                                {
                                    int ra = cur[k - nc], rb = prev[k], rc = prev[k - nc];
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
                                // differences are taken modulo 65536; -32768 and +32768 coincide and get their own category
                                int d = (short)(cur[k] - pred);
                                int s = d == short.MinValue ? 16 : BitLength(Math.Abs(d));
                                if (!emit) { tables[c].Freq[s]++; continue; }
                                uint e = tables[c].Packed[s];
                                int vb = s == 16 ? 0 : s;
                                int len = (int)(e & 255) + vb;
                                acc = acc << len | (e >> 8) << vb | ((uint)(d + (d >> 31)) & ((1u << vb) - 1));
                                n += len;
                                if (n >= 32) { n -= 32; o = Write32(o, (uint)(acc >> n)); }
                            }
                        }
                        _pos = (int)(o - buf);
                    }
                }
            }
            _acc = acc; _accBits = n;
        }
    }
}
