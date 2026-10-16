using System;
using System.Buffers.Binary;
using System.IO;
using Aardvark.ImageCodecs.DpxImpl;

namespace Aardvark.ImageCodecs.CineonImpl
{
    internal static class CineonCodec
    {
        const uint Magic = 0x802A5FD7;
        const int GenericSize = 1024, HeaderSize = 2048;

        public static bool IsMatch(ReadOnlySpan<byte> h)
            => h.Length >= 4 && (BinaryPrimitives.ReadUInt32BigEndian(h) == Magic || BinaryPrimitives.ReadUInt32LittleEndian(h) == Magic);

        // ------------------------------------------------------------------ header

        public static CineonHeader ReadHeader(ReadOnlySpan<byte> d)
        {
            if (!IsMatch(d)) throw new ImageFormatException("Cineon: not a Cineon file");
            if (d.Length < GenericSize) throw new ImageFormatException("Cineon: truncated header");
            var f = new Fields(d, BinaryPrimitives.ReadUInt32BigEndian(d) == Magic);
            int n = d[193];
            if (n < 1 || n > 8) throw new ImageFormatException($"Cineon: bad number of channels {n}");
            var chans = new CineonChannel[n];
            for (int c = 0; c < n; c++)
            {
                int o = 196 + 28 * c;
                uint w = f.U32(o + 4), h = f.U32(o + 8);
                if (w == 0 || h == 0 || w > int.MaxValue || h > int.MaxValue) throw new ImageFormatException($"Cineon: bad dimensions {w} x {h}");
                chans[c] = new CineonChannel
                {
                    Metric = d[o], Designator = d[o + 1], BitDepth = d[o + 2], PixelsPerLine = (int)w, Lines = (int)h,
                    MinimumData = f.OptF32(o + 12), MinimumQuantity = f.OptF32(o + 16), MaximumData = f.OptF32(o + 20), MaximumQuantity = f.OptF32(o + 24),
                };
            }
            (float, float)? Pair(Fields fl, int o) => fl.OptF32(o) is float x && fl.OptF32(o + 4) is float y ? (x, y) : null;
            int? S32(Fields fl, int o) { uint v = fl.U32(o); return v == 0x80000000 ? null : (int)v; }
            bool ind = d.Length >= HeaderSize;
            uint offset = f.U32(4);
            uint? user = f.OptU32(16);
            byte[]? userData = null;
            if (user is uint us && us > 0 && d.Length > HeaderSize && us <= 1u << 24)
                userData = d.Slice(HeaderSize, (int)Math.Min(us, (uint)(d.Length - HeaderSize))).ToArray();
            return new CineonHeader
            {
                BigEndian = f.Be,
                ImageDataOffset = offset,
                GenericHeaderSize = f.OptU32(8),
                IndustryHeaderSize = f.OptU32(12),
                UserDataSize = user,
                FileSize = f.OptU32(20),
                Version = f.Str(24, 8),
                ImageFileName = f.Str(32, 100),
                CreationDate = f.Str(132, 12),
                CreationTimeOfDay = f.Str(144, 12),
                Orientation = d[192] == 0xFF ? 0 : d[192],
                Channels = chans,
                WhitePoint = Pair(f, 420),
                RedPrimary = Pair(f, 428),
                GreenPrimary = Pair(f, 436),
                BluePrimary = Pair(f, 444),
                Label = f.Str(452, 200),
                Interleave = d[680] == 0xFF ? 0 : d[680],
                Packing = d[681] == 0xFF ? 5 : d[681],
                Signed = d[682] == 1,
                Negative = d[683] == 1,
                EndOfLinePadding = f.OptU32(684),
                EndOfChannelPadding = f.OptU32(688),
                XOffset = S32(f, 712),
                YOffset = S32(f, 716),
                SourceFileName = f.Str(720, 100),
                SourceDate = f.Str(820, 12),
                SourceTimeOfDay = f.Str(832, 12),
                InputDevice = f.Str(844, 64),
                InputDeviceModel = f.Str(908, 32),
                InputDeviceSerial = f.Str(940, 32),
                XDevicePitch = f.OptF32(972),
                YDevicePitch = f.OptF32(976),
                Gamma = f.OptF32(980),
                FilmManufacturerId = ind ? f.OptU8(1024) : null,
                FilmType = ind ? f.OptU8(1025) : null,
                PerfsOffset = ind ? f.OptU8(1026) : null,
                FilmPrefix = ind ? f.OptU32(1028) : null,
                FilmCount = ind ? f.OptU32(1032) : null,
                FilmFormat = ind ? f.Str(1036, 32) : "",
                FramePosition = ind ? f.OptU32(1068) : null,
                FrameRate = ind ? f.OptF32(1072) : null,
                FrameAttribute = ind ? f.Str(1076, 32) : "",
                SlateInfo = ind ? f.Str(1108, 200) : "",
                UserData = userData,
            };
        }

        /// <summary>Cineon orientation code to EXIF orientation (codes 1 and 2 are swapped with respect to DPX).</summary>
        public static int ExifOrientation(int c) => c switch { 1 => 4, 2 => 2, 3 => 3, 4 => 5, 5 => 6, 6 => 8, 7 => 7, _ => 1 };

        sealed class Layout
        {
            public int W, H, N, Bits, CellBits, PerCell, Interleave;
            public bool Left, Signed;
            public SampleType Type;
            public ColorModel Color;
            public int[] Order = null!;   // decoded channel c comes from stored channel Order[c]
        }

        static Layout GetLayout(CineonHeader h)
        {
            var c0 = h.Channels[0];
            foreach (var c in h.Channels)
                if (c.BitDepth != c0.BitDepth || c.PixelsPerLine != c0.PixelsPerLine || c.Lines != c0.Lines)
                    throw new ImageNotSupportedException("Cineon: channels of different size or depth are not supported");
            int bits = c0.BitDepth;
            if (bits is < 1 or > 16 && bits != 32) throw new ImageNotSupportedException($"Cineon: {bits} bits per sample are not supported");
            if (h.Interleave > 2) throw new ImageNotSupportedException($"Cineon: data interleave {h.Interleave} is not supported");
            int p = h.Packing & 0x7F;
            if (p > 6) throw new ImageNotSupportedException($"Cineon: packing {h.Packing} is not supported");
            int cell = p switch { 0 => 0, 1 or 2 => 8, 3 or 4 => 16, _ => 32 };
            if (cell != 0 && bits > cell) throw new ImageNotSupportedException($"Cineon: {bits} bit samples do not fit packing {h.Packing}");
            int n = h.Channels.Count;
            int perCell = cell == 0 ? 0 : cell / bits;
            // at most one pixel per cell unless the high bit says otherwise; 8 bit data is written (ImageMagick) and read
            // (ImageMagick, OpenImageIO) four samples per 32-bit cell whatever the flag says
            if (cell != 0 && (h.Packing & 0x80) == 0 && h.Interleave == 0 && n > 1 && bits != 8) perCell = Math.Min(perCell, n);
            var l = new Layout
            {
                W = c0.PixelsPerLine, H = c0.Lines, N = n, Bits = bits, CellBits = cell, PerCell = perCell, Interleave = h.Interleave,
                Left = p is 1 or 3 or 5 || p == 0, Signed = h.Signed,
                Color = n == 1 ? ColorModel.Gray : n == 3 ? ColorModel.Rgb : ColorModel.Other,
            };
            l.Type = bits == 32 ? (l.Signed ? SampleType.Int32 : SampleType.UInt32)
                : bits > 8 ? (l.Signed ? SampleType.Int16 : SampleType.UInt16)
                : l.Signed ? SampleType.Int8 : SampleType.UInt8;
            l.Order = new int[n];
            for (int i = 0; i < n; i++) l.Order[i] = i;
            if (n == 3)
            {
                // red, green, blue by designator (printing density 1..3 or video 4..6), else as stored
                var d = new int[3];
                for (int i = 0; i < 3; i++) d[i] = h.Channels[i].Metric == 0 ? h.Channels[i].Designator : 0;
                bool pd = Array.TrueForAll(d, v => v is >= 1 and <= 3), video = Array.TrueForAll(d, v => v is >= 4 and <= 6);
                if ((pd || video) && d[0] != d[1] && d[1] != d[2] && d[0] != d[2])
                    for (int i = 0; i < 3; i++) l.Order[(d[i] - 1) % 3] = i;
            }
            return l;
        }

        static TransferFunction Transfer(CineonHeader h, out double gamma)
        {
            gamma = 0;
            foreach (var c in h.Channels) if (c.Metric != 0 || c.Designator is < 4 or > 6) return TransferFunction.Unknown;
            gamma = 0.45;
            return TransferFunction.Gamma;
        }

        public static ImageInfo GetInfo(ReadOnlySpan<byte> data, CineonDecodeOptions o)
        {
            var h = ReadHeader(data);
            var l = GetLayout(h);
            int exif = ExifOrientation(h.Orientation);
            bool applied = o.ApplyOrientation && exif >= 2, swap = applied && exif >= 5;
            return new ImageInfo(ImageFormat.Cineon, swap ? l.H : l.W, swap ? l.W : l.H, l.N, l.Type, l.Color)
            {
                BitsPerSample = l.Bits, Orientation = exif, OrientationApplied = applied, Transfer = Transfer(h, out _),
            };
        }

        // ------------------------------------------------------------------ decode

        /// <summary>Reads samples from cells (or a bit stream), one line at a time; lines start on a new cell.</summary>
        ref struct Reader
        {
            readonly ReadOnlySpan<byte> _d;
            readonly bool _be, _left;
            readonly int _bits, _cell, _perCell, _cellBytes;
            public long Pos;   // byte position (bit position for bit fields)
            int _slot;
            uint _cur;

            public Reader(ReadOnlySpan<byte> d, bool be, Layout l)
            {
                _d = d; _be = be; _left = l.Left; _bits = l.Bits; _cell = l.CellBits; _perCell = l.PerCell; _cellBytes = l.CellBits / 8;
                Pos = 0; _slot = 0; _cur = 0;
            }

            public uint Next()
            {
                if (_cell == 0)
                {
                    // bit fields, most significant bit first
                    uint v = 0;
                    for (int k = 0; k < _bits;)
                    {
                        long b = Pos >> 3;
                        if (b >= _d.Length) throw new ImageFormatException("Cineon: image data is truncated");
                        int bit = (int)(Pos & 7), take = Math.Min(8 - bit, _bits - k);
                        v = v << take | (uint)(_d[(int)b] >> (8 - bit - take) & ((1 << take) - 1));
                        k += take; Pos += take;
                    }
                    return v;
                }
                if (_slot == 0)
                {
                    if (Pos >= _d.Length) throw new ImageFormatException("Cineon: image data is truncated");
                    var s = _d.Slice((int)Pos);
                    if (s.Length < _cellBytes)
                    {
                        // a last cell cut short (ImageMagick ends 16 bit data with the last sample): zero filled
                        Span<byte> t = stackalloc byte[4];
                        t.Clear();
                        s.CopyTo(t);   // the bytes present are the high ones (big endian) or the low ones (little endian)
                        s = t.Slice(0, _cellBytes).ToArray();
                    }
                    _cur = _cellBytes == 1 ? s[0] : _cellBytes == 2 ? (_be ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s))
                        : _be ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s);
                    Pos += _cellBytes;
                }
                int shift = _left ? _cell - (_slot + 1) * _bits : (_perCell - 1 - _slot) * _bits;
                if (++_slot == _perCell) _slot = 0;
                return _bits == 32 ? _cur : _cur >> shift & ((1u << _bits) - 1);
            }

            /// <summary>Ends a line: the next one starts on a new cell (bit fields continue), then skips padding bytes.</summary>
            public void EndLine(long padding)
            {
                _slot = 0;
                if (_cell == 0) { if (padding > 0) Pos = ((Pos + 7) & ~7L) + 8 * padding; }
                else Pos += padding;
            }
        }

        public static RawImage Decode(ReadOnlySpan<byte> data, CineonDecodeOptions o)
        {
            var h = ReadHeader(data);
            var l = GetLayout(h);
            long px = (long)l.W * l.H;
            if (px > o.MaxPixels || px * l.N > Array.MaxLength) throw new ImageNotSupportedException($"Cineon: {l.W} x {l.H} exceeds MaxPixels");
            if (h.ImageDataOffset < GenericSize || h.ImageDataOffset > data.Length) throw new ImageFormatException("Cineon: bad image data offset");
            var src = data.Slice((int)h.ImageDataOffset);
            long samples = px * l.N;
            if (samples * l.Bits / 8 > src.Length) throw new ImageFormatException("Cineon: image data is truncated");
            long eol = h.EndOfLinePadding is uint e && e < 1u << 20 ? e : 0;
            long eoc = h.EndOfChannelPadding is uint c && c < 1u << 30 ? c : 0;

            // Lines start on a new cell, except that 16 bit data runs on across line ends (what ImageMagick writes and both
            // ImageMagick and OpenImageIO read); for 8 bit ImageMagick starts new cells, OpenImageIO does not. A pixel interleaved
            // file whose lines end inside a cell is taken the way its data size fits.
            bool continuous = false;
            long fpl = (long)l.W * l.N;
            if (l.CellBits != 0 && l.Interleave == 0 && eol == 0 && fpl % l.PerCell != 0)
            {
                long cb = l.CellBits / 8;
                long restart = (fpl + l.PerCell - 1) / l.PerCell * cb * l.H, cont = (samples + l.PerCell - 1) / l.PerCell * cb;
                // the other layout only when the data ends where it ends (a last cell may be cut short), so that truncated
                // files are not taken for it
                long len = src.Length;
                bool endsR = len > restart - cb && len < restart + 4, endsC = len > cont - cb && len < cont + 4;
                continuous = l.Bits == 16 ? !(endsR && !endsC) : endsC && !endsR;
            }

            var img = RawImage.Create(l.W, l.H, l.Color, l.Type, l.N);
            if (l.Interleave == 0 && l.CellBits != 0 && !continuous) Lines(src, h.BigEndian, l, eol, fpl, img);
            else Generic(src, h.BigEndian, l, eol, eoc, fpl, continuous, img);
            var m = img.Metadata = new ImageMetadata();
            m.Transfer = Transfer(h, out double g);
            m.Gamma = g;
            m.Orientation = ExifOrientation(h.Orientation);
            return o.ApplyOrientation && m.Orientation >= 2 ? ImageOps.ApplyOrientation(img) : img;
        }


        /// <summary>Pixel interleaved cells, every line starting on a new cell: unpacked a line at a time.</summary>
        static void Lines(ReadOnlySpan<byte> src, bool be, Layout l, long eol, long fpl, RawImage img)
        {
            int cb = l.CellBits / 8, k = l.PerCell;
            long cells = (fpl + k - 1) / k, lineBytes = cells * cb, stride = lineBytes + eol;
            if (cells * k > int.MaxValue / 2) throw new ImageNotSupportedException("Cineon: lines too long");
            long need = stride * (l.H - 1) + lineBytes;
            if (need > src.Length)
            {
                if (need - src.Length >= cb) throw new ImageFormatException("Cineon: image data is truncated");
                var padded = new byte[need];   // the last cell cut short
                src.CopyTo(padded);
                src = padded;
            }
            var line = new uint[cells * k];
            for (int y = 0; y < l.H; y++)
            {
                Unpack(src.Slice((int)(stride * y), (int)lineBytes), be, l, line);
                StoreLine(line, l, img, y);
            }
        }

        static void Unpack(ReadOnlySpan<byte> row, bool be, Layout l, uint[] dst)
        {
            int cb = l.CellBits / 8, k = l.PerCell, bits = l.Bits, cells = dst.Length / k;
            if (cb == 4 && k == 3 && bits == 10 && l.Left)
            {
                for (int c = 0, o = 0; c < cells; c++, o += 3)
                {
                    uint v = be ? BinaryPrimitives.ReadUInt32BigEndian(row.Slice(4 * c)) : BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(4 * c));
                    dst[o] = v >> 22 & 1023; dst[o + 1] = v >> 12 & 1023; dst[o + 2] = v >> 2 & 1023;
                }
                return;
            }
            uint mask = bits == 32 ? uint.MaxValue : (1u << bits) - 1;
            for (int c = 0, o = 0; c < cells; c++)
            {
                uint v = cb == 4 ? (be ? BinaryPrimitives.ReadUInt32BigEndian(row.Slice(4 * c)) : BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(4 * c)))
                    : cb == 2 ? (be ? BinaryPrimitives.ReadUInt16BigEndian(row.Slice(2 * c)) : BinaryPrimitives.ReadUInt16LittleEndian(row.Slice(2 * c)))
                    : row[c];
                for (int j = 0; j < k; j++) dst[o++] = v >> (l.Left ? l.CellBits - (j + 1) * bits : (k - 1 - j) * bits) & mask;
            }
        }

        static void StoreLine(uint[] v, Layout l, RawImage img, int y)
        {
            int n = l.N, w = l.W, bits = l.Bits, sh = 32 - bits;
            long o0 = (long)y * w * n;
            var ord = l.Order;
            bool direct = true;
            for (int c = 0; c < n; c++) direct &= ord[c] == c;
            int len = w * n;
            switch (l.Type)
            {
                case SampleType.UInt16:
                    {
                        var d = img.GetData<ushort>().AsSpan((int)o0, len);
                        if (direct) for (int i = 0; i < len; i++) d[i] = (ushort)v[i];
                        else for (int i = 0; i < len; i += n) for (int c = 0; c < n; c++) d[i + c] = (ushort)v[i + ord[c]];
                        return;
                    }
                case SampleType.UInt8:
                    {
                        var d = img.GetData<byte>().AsSpan((int)o0, len);
                        uint max = (1u << bits) - 1;
                        bool stretch = bits is 1 or 2 or 4;
                        for (int i = 0; i < len; i += n)
                            for (int c = 0; c < n; c++) { uint s = v[i + ord[c]]; d[i + c] = (byte)(stretch ? s * 255 / max : s); }
                        return;
                    }
                default:
                    for (int i = 0; i < len; i += n)
                        for (int c = 0; c < n; c++)
                        {
                            uint s = v[i + ord[c]];
                            long at = o0 + i + c;
                            switch (l.Type)
                            {
                                case SampleType.UInt32: ((uint[])img.Data)[at] = s; break;
                                case SampleType.Int8: ((sbyte[])img.Data)[at] = (sbyte)((int)s << sh >> sh); break;
                                case SampleType.Int16: ((short[])img.Data)[at] = (short)((int)s << sh >> sh); break;
                                default: ((int[])img.Data)[at] = (int)s; break;
                            }
                        }
                    return;
            }
        }

        /// <summary>Everything else: bit fields, line / channel interleave, lines running on across line ends.</summary>
        static void Generic(ReadOnlySpan<byte> src, bool be, Layout l, long eol, long eoc, long fpl, bool continuous, RawImage img)
        {
            long samples = (long)l.W * l.H * l.N;
            var vals = new uint[samples];
            var r = new Reader(src, be, l);
            if (l.Interleave == 0)
            {
                for (int y = 0; y < l.H; y++)
                {
                    long b = y * fpl;
                    for (long i = 0; i < fpl; i++) vals[b + i] = r.Next();
                    if (!continuous) r.EndLine(eol);
                }
            }
            else
            {
                // line interleave: every line holds a line of each channel; channel interleave: whole channel planes
                for (int a = 0; a < (l.Interleave == 1 ? l.H : l.N); a++)
                {
                    for (int b = 0; b < (l.Interleave == 1 ? l.N : l.H); b++)
                    {
                        int y = l.Interleave == 1 ? a : b, ch = l.Interleave == 1 ? b : a;
                        long o0 = (long)y * fpl + ch;
                        for (int x = 0; x < l.W; x++) vals[o0 + (long)x * l.N] = r.Next();
                        r.EndLine(eol);
                    }
                    if (l.Interleave == 2) r.Pos += l.CellBits == 0 ? 8 * eoc : eoc;
                }
            }
            Store(vals, l, img);
        }

        static void Store(uint[] v, Layout l, RawImage img)
        {
            int n = l.N;
            long px = (long)l.W * l.H;
            int bits = l.Bits;
            int sh = 32 - bits;
            uint max = bits == 32 ? uint.MaxValue : (1u << bits) - 1;
            bool stretch = !l.Signed && bits is 1 or 2 or 4;
            for (long p = 0; p < px; p++)
                for (int c = 0; c < n; c++)
                {
                    uint s = v[p * n + l.Order[c]];
                    long i = p * n + c;
                    switch (l.Type)
                    {
                        case SampleType.UInt8: ((byte[])img.Data)[i] = (byte)(stretch ? s * 255 / max : s); break;
                        case SampleType.UInt16: ((ushort[])img.Data)[i] = (ushort)s; break;
                        case SampleType.UInt32: ((uint[])img.Data)[i] = s; break;
                        case SampleType.Int8: ((sbyte[])img.Data)[i] = (sbyte)((int)s << sh >> sh); break;
                        case SampleType.Int16: ((short[])img.Data)[i] = (short)((int)s << sh >> sh); break;
                        default: ((int[])img.Data)[i] = (int)s; break;
                    }
                }
        }

        // ------------------------------------------------------------------ encode

        public static void Encode(RawImage img, Stream output, CineonEncodeOptions o)
        {
            if (img.Color is not (ColorModel.Gray or ColorModel.Rgb) && !(img.Color == ColorModel.Other && img.Channels is 1 or 3))
                throw new ArgumentException($"Cineon: cannot write {img.Color} images (Gray and Rgb; Cineon has no alpha channel)");
            if (img.SampleType is not (SampleType.UInt8 or SampleType.UInt16))
                throw new ArgumentException($"Cineon: cannot write {img.SampleType} samples (UInt8 and UInt16)");
            int bits = o.BitDepth;
            if (bits is not (8 or 10 or 16)) throw new ArgumentException($"Cineon: cannot write {bits} bit samples (8, 10, 16)");
            if (o.RgbDesignator is not (1 or 4)) throw new ArgumentException("Cineon: RgbDesignator is 1 (printing density) or 4 (CCIR XA/11)");
            int n = img.Channels, w = img.Width, h = img.Height;
            bool be = o.BigEndian;
            int perCell = 32 / bits;   // 8 bit: 4, 10 bit: 3, 16 bit: 2
            long fpl = (long)w * n;
            // 16 bit samples run on across line ends (see Decode), the others start every line on a new cell
            bool continuous = bits == 16;
            long cellsPerLine = (fpl + perCell - 1) / perCell;
            long dataSize = continuous ? (fpl * h + perCell - 1) / perCell * 4 : cellsPerLine * 4 * h;
            var hd = o.Header;
            int userSize = hd?.UserData?.Length ?? 0;
            int offset = HeaderSize + ((userSize + 3) & ~3);
            if (offset + dataSize > uint.MaxValue) throw new ArgumentException("Cineon: the image is too large for a Cineon file (4 GB)");

            var head = new byte[offset];
            head.AsSpan(0, HeaderSize).Fill(0xFF);
            var f = new FieldWriter(head, be);
            f.U32(0, Magic);
            f.U32(4, (uint)offset);
            f.U32(8, GenericSize);
            f.U32(12, HeaderSize - GenericSize);
            f.U32(16, (uint)userSize);
            f.U32(20, (uint)(offset + dataSize));
            f.Str(24, 8, "V4.5");
            f.Str(32, 100, hd?.ImageFileName);
            f.Str(132, 12, hd?.CreationDate);
            f.Str(144, 12, hd?.CreationTimeOfDay);
            head.AsSpan(156, 36).Clear();
            f.U8(192, 0);
            f.U8(193, (byte)n);
            f.U8(194, 0); f.U8(195, 0);
            for (int c = 0; c < 8; c++)
            {
                int b = 196 + 28 * c;
                if (c >= n) { head.AsSpan(b, 28).Fill(0xFF); continue; }   // unused channel specifiers stay undefined
                f.U8(b, 0);
                f.U8(b + 1, (byte)(n == 1 ? 0 : o.RgbDesignator + c));
                f.U8(b + 2, (byte)bits);
                f.U8(b + 3, 0);
                f.U32(b + 4, (uint)w);
                f.U32(b + 8, (uint)h);
                f.F32(b + 12, 0);
                f.F32(b + 16, 0);
                f.F32(b + 20, (1 << bits) - 1);
                f.F32(b + 24, 2.048f);
            }
            void Pair(int at, (float X, float Y)? v)
            {
                if (v is var (x, y)) { f.F32(at, x); f.F32(at + 4, y); }
                else { f.U32(at, 0x7F800000); f.U32(at + 4, 0x7F800000); }
            }
            Pair(420, hd?.WhitePoint); Pair(428, hd?.RedPrimary); Pair(436, hd?.GreenPrimary); Pair(444, hd?.BluePrimary);
            f.Str(452, 200, hd?.Label);
            head.AsSpan(652, 28).Clear();
            f.U8(680, 0);
            f.U8(681, 5);
            f.U8(682, 0);
            f.U8(683, hd?.Negative == true ? (byte)1 : (byte)0);
            f.U32(684, 0);
            f.U32(688, 0);
            head.AsSpan(692, 20).Clear();
            f.U32(712, hd?.XOffset is int xo ? (uint)xo : 0x80000000);
            f.U32(716, hd?.YOffset is int yo ? (uint)yo : 0x80000000);
            f.Str(720, 100, hd?.SourceFileName);
            f.Str(820, 12, hd?.SourceDate);
            f.Str(832, 12, hd?.SourceTimeOfDay);
            f.Str(844, 64, hd?.InputDevice);
            f.Str(908, 32, hd?.InputDeviceModel);
            f.Str(940, 32, hd?.InputDeviceSerial);
            void F(int at, float? v) { if (v is float x) f.F32(at, x); else f.U32(at, 0x7F800000); }
            F(972, hd?.XDevicePitch); F(976, hd?.YDevicePitch); F(980, hd?.Gamma);
            head.AsSpan(984, 40).Clear();
            f.OptU8(1024, hd?.FilmManufacturerId); f.OptU8(1025, hd?.FilmType); f.OptU8(1026, hd?.PerfsOffset); f.U8(1027, 0);
            f.OptU32(1028, hd?.FilmPrefix); f.OptU32(1032, hd?.FilmCount);
            f.Str(1036, 32, hd?.FilmFormat);
            f.OptU32(1068, hd?.FramePosition);
            F(1072, hd?.FrameRate);
            f.Str(1076, 32, hd?.FrameAttribute);
            f.Str(1108, 200, hd?.SlateInfo);
            head.AsSpan(1308, 740).Clear();
            hd?.UserData?.CopyTo(head, HeaderSize);
            output.Write(head);

            uint max = (1u << bits) - 1;
            // the samples at the target depth: UInt16 as they are (checked to fit), UInt8 scaled through a table
            ushort[] fields;
            if (img.SampleType == SampleType.UInt16)
            {
                fields = img.GetData<ushort>();
                foreach (var v in fields)
                    if (v > max) throw new ArgumentException($"Cineon: sample value {v} does not fit {bits} bits (UInt16 samples are written as they are)");
            }
            else
            {
                var lut = new ushort[256];
                for (int v = 0; v < 256; v++) lut[v] = (ushort)(bits == 8 ? v : bits == 16 ? v * 257 : (v * (int)max + 127) / 255);
                var src = img.GetData<byte>();
                fields = new ushort[src.Length];
                for (int k = 0; k < src.Length; k++) fields[k] = lut[src[k]];
            }
            long lineFields = continuous ? fpl * h : fpl;
            var row = new byte[Math.Min((lineFields + perCell - 1) / perCell * 4, 1 << 16)];
            for (int y = 0; y < (continuous ? 1 : h); y++)
            {
                var fl = fields.AsSpan((int)((long)y * fpl), (int)lineFields);
                int k = 0;
                for (int i = 0; i < fl.Length; i += perCell, k += 4)
                {
                    if (k == row.Length) { output.Write(row); k = 0; }
                    uint cell;
                    if (i + perCell <= fl.Length)
                        cell = perCell switch
                        {
                            3 => (uint)fl[i] << 22 | (uint)fl[i + 1] << 12 | (uint)fl[i + 2] << 2,
                            2 => (uint)fl[i] << 16 | fl[i + 1],
                            _ => (uint)fl[i] << 24 | (uint)fl[i + 1] << 16 | (uint)fl[i + 2] << 8 | fl[i + 3],
                        };
                    else
                    {
                        cell = 0;
                        for (int t = 0; i + t < fl.Length; t++) cell |= (uint)fl[i + t] << (32 - (t + 1) * bits);
                    }
                    if (be) BinaryPrimitives.WriteUInt32BigEndian(row.AsSpan(k), cell); else BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(k), cell);
                }
                output.Write(row, 0, k);
            }
        }
    }
}
