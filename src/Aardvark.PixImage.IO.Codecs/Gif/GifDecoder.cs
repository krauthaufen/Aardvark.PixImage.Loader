using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Aardvark.ImageCodecs
{
    /// <summary>
    /// Walks the block structure of a GIF and produces one frame per <see cref="Next"/> call. It keeps no reference to
    /// the data, so the same code serves the span based entry points and the lazy enumerator.
    /// </summary>
    internal sealed unsafe class GifDecoderState
    {
        public enum Mode
        {
            /// <summary>Frame headers only, no LZW decoding and no pixel allocations.</summary>
            Skip,
            Composited,
            Indexed,
            /// <summary>First frame as the Rgb or Rgba image <see cref="Gif.Decode(ReadOnlySpan{byte}, GifDecodeOptions?)"/> returns.</summary>
            FirstFrame,
        }

        // An LZW code is at least 3 bits wide and expands to at most 4095 pixels, so a file cannot paint more than
        // this many pixels per byte.
        const long MaxPixelsPerByte = 10923;

        public int Width, Height, FrameCount;
        public readonly Mode FrameMode;

        readonly long _maxPixels, _maxUnbackedPixels;
        readonly bool _clampDelays, _rejectIncomplete, _incompleteAsRgba;
        byte[]? _icc;
        readonly bool _is89a;
        readonly int _backgroundIndex, _aspect;
        readonly uint[]? _globalPalette;
        readonly byte[]? _globalRgb;
        int? _loopCount;
        List<string>? _comments;

        int _pos;
        bool _done;

        // Graphic control extension waiting for its image.
        int _gceDelay, _gceDisposal, _gceTransparent = -1;

        // Compositing state.
        byte[]? _canvas;
        int _prevDisposal, _prevX, _prevY, _prevW, _prevH;
        uint[]? _backup;
        uint[]? _localPalette;
        int[]? _rowMap;

        static readonly uint OpaqueBlack = Pack(0, 0, 0);
        static readonly GifDecodeOptions DefaultOptions = new GifDecodeOptions();

        public GifDecoderState(ReadOnlySpan<byte> data, GifDecodeOptions? options)
        {
            FrameMode = options?.FrameMode == GifFrameMode.Indexed ? Mode.Indexed : Mode.Composited;
            var o = options ?? DefaultOptions;
            _maxPixels = Math.Clamp(o.MaxPixels, 1, int.MaxValue / 4);
            _maxUnbackedPixels = Math.Max(0, o.MaxUnbackedPixels);
            _clampDelays = o.ClampDelays;
            _rejectIncomplete = o.RejectIncompleteFrames;
            _incompleteAsRgba = o.IncompleteFirstFrameAsRgba;
            if (!Gif.IsMatch(data)) throw new ImageFormatException("not a GIF file");
            if (data.Length < 13) throw new ImageFormatException("truncated GIF header");
            _is89a = data[4] == '9';
            Width = data[6] | data[7] << 8;
            Height = data[8] | data[9] << 8;
            int packed = data[10];
            _backgroundIndex = data[11];
            _aspect = data[12];
            _pos = 13;
            if ((packed & 0x80) != 0)
            {
                int count = 2 << (packed & 7);
                if (data.Length - _pos < count * 3) throw new ImageFormatException("truncated GIF color table");
                _globalRgb = data.Slice(_pos, count * 3).ToArray();
                _globalPalette = new uint[256];
                LoadPalette(_globalRgb, _globalPalette);
                _pos += count * 3;
            }
        }

        // R, G, B, A bytes in memory order (little-endian hosts only, like the rest of the library).
        static uint Pack(byte r, byte g, byte b) => r | (uint)g << 8 | (uint)b << 16 | 0xFF000000u;

        static void LoadPalette(ReadOnlySpan<byte> rgb, uint[] palette)
        {
            int n = rgb.Length / 3;
            for (int i = 0; i < n; i++) palette[i] = Pack(rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2]);
            for (int i = n; i < 256; i++) palette[i] = OpaqueBlack;
        }

        public void FillInfo(GifAnimationInfo info)
        {
            info.Width = Width;
            info.Height = Height;
            info.FrameCount = FrameCount;
            info.LoopCount = _loopCount;
            info.BackgroundIndex = _backgroundIndex;
            info.GlobalPalette = _globalRgb;
            if (_globalRgb != null && _backgroundIndex * 3 + 2 < _globalRgb.Length)
                info.BackgroundColor = (_globalRgb[_backgroundIndex * 3], _globalRgb[_backgroundIndex * 3 + 1], _globalRgb[_backgroundIndex * 3 + 2]);
            info.PixelAspectRatio = _aspect;
            info.IccProfile = _icc;
            info.IsGif89a = _is89a;
            if (_comments != null) info.Comments = _comments;
        }

        public bool FirstFrameHasAlpha(GifFrame f)
            => f.TransparentIndex >= 0 || f.X != 0 || f.Y != 0 || f.Width < Width || f.Height < Height;

        /// <summary>
        /// The stream ended, or stopped making sense, at a block boundary. Browsers show what they got so far; with
        /// nothing to show it is an error.
        /// </summary>
        GifFrame? End(string what)
        {
            _done = true;
            if (FrameCount == 0) throw new ImageFormatException(what);
            return null;
        }

        /// <summary>Skips a chain of data sub-blocks; false if the data ends inside it.</summary>
        static bool SkipSubBlocks(ReadOnlySpan<byte> data, ref int pos)
        {
            while (true)
            {
                if (pos >= data.Length) return false;
                int n = data[pos++];
                if (n == 0) return true;
                if (data.Length - pos < n) { pos = data.Length; return false; }
                pos += n;
            }
        }

        public GifFrame? Next(ReadOnlySpan<byte> data, Mode mode)
        {
            if (_done) return null;
            while (true)
            {
                if (_pos >= data.Length) return End("GIF contains no image");   // missing trailer is tolerated
                byte b = data[_pos++];
                if (b == 0x3B) return End("GIF contains no image");
                if (b == 0x21)
                {
                    if (!ReadExtension(data)) return End("truncated GIF extension");
                    continue;
                }
                if (b != 0x2C) return End("invalid GIF block");
                var frame = ReadImage(data, mode);
                if (frame == null) return End("truncated GIF image header");
                return frame;
            }
        }

        bool ReadExtension(ReadOnlySpan<byte> data)
        {
            if (_pos >= data.Length) return false;
            byte label = data[_pos++];
            int start = _pos;
            if (!SkipSubBlocks(data, ref _pos)) return false;
            int first = data[start];
            var block = data.Slice(start + 1, first);
            switch (label)
            {
                case 0xF9:
                    if (first >= 4)
                    {
                        _gceDisposal = (block[0] >> 2) & 7;
                        _gceDelay = block[1] | block[2] << 8;
                        _gceTransparent = (block[0] & 1) != 0 ? block[3] : -1;
                    }
                    break;
                case 0xFF:
                    if (first == 11 && (block.SequenceEqual("NETSCAPE2.0"u8) || block.SequenceEqual("ANIMEXTS1.0"u8)))
                    {
                        int p = start + 1 + first;
                        while (data[p] != 0)
                        {
                            int n = data[p];
                            if (n >= 3 && data[p + 1] == 1) _loopCount = data[p + 2] | data[p + 3] << 8;
                            p += n + 1;
                        }
                    }
                    else if (first == 11 && block.SequenceEqual("ICCRGBG1012"u8))
                    {
                        // ICC profile, the convention ImageMagick introduced and browsers read
                        var ms = new System.IO.MemoryStream();
                        for (int p = start + 1 + first; data[p] != 0; p += data[p] + 1) ms.Write(data.Slice(p + 1, data[p]));
                        if (ms.Length > 0) _icc = ms.ToArray();
                    }
                    break;
                case 0xFE:
                    {
                        var sb = new StringBuilder();
                        int p = start;
                        while (data[p] != 0)
                        {
                            int n = data[p];
                            sb.Append(Encoding.Latin1.GetString(data.Slice(p + 1, n)));
                            p += n + 1;
                        }
                        (_comments ??= new List<string>()).Add(sb.ToString());
                    }
                    break;
            }
            return true;
        }

        GifFrame? ReadImage(ReadOnlySpan<byte> data, Mode mode)
        {
            if (data.Length - _pos < 9) return null;
            var d = data.Slice(_pos, 9);
            int fx = d[0] | d[1] << 8, fy = d[2] | d[3] << 8, fw = d[4] | d[5] << 8, fh = d[6] | d[7] << 8;
            int packed = d[8];
            _pos += 9;
            ReadOnlySpan<byte> localRgb = default;
            if ((packed & 0x80) != 0)
            {
                int count = 2 << (packed & 7);
                if (data.Length - _pos < count * 3) return null;
                localRgb = data.Slice(_pos, count * 3);
                _pos += count * 3;
            }
            if (_pos >= data.Length) return null;
            int minCodeSize = data[_pos++];
            if (fw == 0 || fh == 0) throw new ImageFormatException("GIF frame with zero size");
            if (minCodeSize < 1 || minCodeSize > 11) throw new ImageFormatException("invalid GIF LZW code size");
            // Like Chromium: a first frame that reaches beyond the logical screen (or a screen left at 0 x 0) enlarges
            // the screen to hold it; later frames are clipped.
            if (FrameCount == 0)
            {
                Width = Math.Max(Width, fx + fw);
                Height = Math.Max(Height, fy + fh);
            }

            var frame = new GifFrame
            {
                Index = FrameCount,
                X = fx, Y = fy, Width = fw, Height = fh,
                DelayCs = _gceDelay,
                // What browsers play: 0 and 1/100 s are too fast to be meant and become 1/10 s.
                DelayMs = _clampDelays && _gceDelay <= 1 ? 100 : _gceDelay * 10,
                // Like browsers: method 4 is an old alias of "restore previous", 5..7 mean nothing.
                Disposal = _gceDisposal == 4 ? GifDisposal.RestorePrevious : _gceDisposal > 4 ? GifDisposal.Unspecified : (GifDisposal)_gceDisposal,
                Interlaced = (packed & 0x40) != 0,
                TransparentIndex = _gceTransparent,
                HasLocalPalette = !localRgb.IsEmpty,
            };
            _gceDelay = 0; _gceDisposal = 0; _gceTransparent = -1;
            FrameCount++;

            int dataStart = _pos;
            bool complete = SkipSubBlocks(data, ref _pos);
            if (mode == Mode.Skip)
            {
                frame.Truncated = !complete;
                return frame;
            }

            // Reassemble the sub-blocks into one contiguous code stream.
            var pool = ArrayPool<byte>.Shared;
            byte[] codes = pool.Rent(Math.Max(1, _pos - dataStart));
            byte[]? rented = null;
            try
            {
                int codeLen = 0;
                for (int p = dataStart; p < data.Length;)
                {
                    int n = data[p++];
                    if (n == 0) break;
                    n = Math.Min(n, data.Length - p);
                    data.Slice(p, n).CopyTo(codes.AsSpan(codeLen));
                    codeLen += n;
                    p += n;
                }

                // Sizes come from headers that may lie. Arrays the caller gets (canvas, indexed frame) follow
                // CheckSize; the scratch buffer for the indices is never larger than what the data can fill.
                long framePixels = (long)fw * fh;
                CheckSize(framePixels, codeLen, "frame");
                if (mode != Mode.Indexed) CheckSize((long)Width * Height, data.Length, "logical screen");
                int total = (int)framePixels;
                int room = (int)Math.Min(total, (long)codeLen * 8 / (minCodeSize + 1) * 4096 + 1);

                byte[] indices;
                if (mode == Mode.Indexed && !frame.Interlaced) indices = new byte[total];
                else indices = rented = pool.Rent(room);
                int decoded = GifLzw.Decode(codes.AsSpan(0, codeLen), minCodeSize, indices.AsSpan(0, room));
                frame.Truncated = decoded < total;
                if (frame.Truncated && _rejectIncomplete)
                    throw new ImageFormatException($"GIF frame {frame.Index} has data for {decoded} of its {total} pixels");

                // An opaque, interlaced first frame that stops early is shown the way browsers show one that is still
                // loading: every finished row also fills the rows below it that later passes would have delivered.
                // (Later or transparent frames just keep what they got - Chromium does the same.)
                int[]? rowMap = frame.Interlaced ? RowMap(fh) : null;
                int[]? rowValid = null;
                byte[]? spread = null;
                if (frame.Interlaced && decoded < total && frame.Index == 0 && frame.TransparentIndex < 0)
                {
                    spread = SpreadInterlaced(indices, decoded, fw, fh, out rowValid);
                    rowMap = null;
                }

                if (mode == Mode.Indexed)
                {
                    byte fill = (byte)Math.Max(0, frame.TransparentIndex);
                    if (spread != null)
                    {
                        for (int y = 0; y < fh; y++) spread.AsSpan(y * fw + rowValid![y], fw - rowValid[y]).Fill(fill);
                        indices = spread;
                    }
                    else if (frame.Interlaced)
                    {
                        var dst = new byte[total];
                        if (decoded < total) dst.AsSpan().Fill(fill);
                        for (int y = 0; y < fh; y++)
                        {
                            long at = (long)rowMap![y] * fw;
                            int have = (int)Math.Clamp(decoded - at, 0, fw);
                            if (have > 0) indices.AsSpan((int)at, have).CopyTo(dst.AsSpan(y * fw));
                        }
                        indices = dst;
                    }
                    else indices.AsSpan(decoded, total - decoded).Fill(fill);
                    frame.Indices = indices;
                    frame.Palette = !localRgb.IsEmpty ? localRgb.ToArray() : _globalRgb != null ? (byte[])_globalRgb.Clone() : new byte[768];
                    return frame;
                }
                if (spread != null) indices = spread;

                uint[] palette;
                if (!localRgb.IsEmpty)
                {
                    palette = _localPalette ??= new uint[256];
                    LoadPalette(localRgb, palette);
                }
                else if (_globalPalette != null) palette = _globalPalette;
                else
                {
                    // No color table at all: the spec leaves it to the decoder; Chromium shows black.
                    palette = _localPalette ??= new uint[256];
                    LoadPalette(default, palette);
                }

                if (mode == Mode.FirstFrame && !FirstFrameHasAlpha(frame) && !(frame.Truncated && _incompleteAsRgba))
                {
                    var rgb = new byte[checked(Width * Height * 3)];
                    DrawRgb(indices, decoded, frame, palette, rowMap, rowValid, rgb);
                    frame.Image = new RawImage(Width, Height, ColorModel.Rgb, rgb, 0, Metadata());
                    return frame;
                }

                _canvas ??= new byte[checked(Width * Height * 4)];
                var canvas = MemoryMarshal.Cast<byte, uint>(_canvas.AsSpan());
                DisposePrevious(canvas);

                // Clipped frame rectangle.
                int cw = Math.Max(0, Math.Min(fw, Width - fx)), ch = Math.Max(0, Math.Min(fh, Height - fy));
                if (cw == 0) ch = 0;
                if (frame.Disposal == GifDisposal.RestorePrevious && ch > 0 && mode != Mode.FirstFrame)
                {
                    if (_backup == null || _backup.Length < cw * ch) _backup = new uint[cw * ch];
                    for (int y = 0; y < ch; y++)
                        canvas.Slice((fy + y) * Width + fx, cw).CopyTo(_backup.AsSpan(y * cw));
                }
                _prevDisposal = (int)frame.Disposal; _prevX = fx; _prevY = fy; _prevW = cw; _prevH = ch;

                Draw(indices, decoded, frame, palette, rowMap, rowValid, canvas, cw, ch);

                byte[] pixels = mode == Mode.FirstFrame ? _canvas : (byte[])_canvas.Clone();
                frame.Image = new RawImage(Width, Height, ColorModel.Rgba, pixels, 0, Metadata());
                return frame;
            }
            finally
            {
                pool.Return(codes);
                if (rented != null) pool.Return(rented);
            }
        }

        public bool HasIccProfile => _icc != null;

        ImageMetadata? Metadata() => _icc == null ? null : new ImageMetadata { IccProfile = _icc };

        /// <summary>
        /// MaxPixels is a hard limit. Below it, anything up to MaxUnbackedPixels is taken at its word; beyond that the
        /// data has to be long enough to paint that many pixels.
        /// </summary>
        void CheckSize(long pixels, long backingBytes, string what)
        {
            if (pixels > _maxPixels) throw new ImageNotSupportedException($"GIF {what} of {pixels} pixels exceeds {nameof(GifDecodeOptions)}.{nameof(GifDecodeOptions.MaxPixels)} ({_maxPixels})");
            if (pixels > _maxUnbackedPixels && pixels > backingBytes * MaxPixelsPerByte)
                throw new ImageFormatException($"GIF {what} of {pixels} pixels is implausible for {backingBytes} bytes of data; " +
                    $"if the file really has a mostly empty canvas this large, raise {nameof(GifDecodeOptions)}.{nameof(GifDecodeOptions.MaxUnbackedPixels)} (now {_maxUnbackedPixels})");
        }

        /// <summary>
        /// De-interlaces an incomplete frame: rows of pass 1/2/3 are repeated over the 8/4/2 rows they stand for until a
        /// later pass overwrites them. rowValid[y] is the number of pixels row y ended up with.
        /// </summary>
        static byte[] SpreadInterlaced(byte[] fileOrder, int decoded, int fw, int fh, out int[] rowValid)
        {
            var dst = new byte[fw * fh];
            rowValid = new int[fh];
            int n = 0;
            for (int pass = 0; pass < 4; pass++)
            {
                int first = pass == 0 ? 0 : 8 >> pass, step = pass == 0 ? 8 : 16 >> pass, repeat = 8 >> pass;
                for (int y = first; y < fh; y += step, n++)
                {
                    int have = (int)Math.Min(fw, decoded - (long)n * fw);
                    if (have <= 0) return dst;
                    var row = fileOrder.AsSpan(n * fw, have);
                    // an unfinished row only lands in its own place
                    int rows = have == fw ? Math.Min(repeat, fh - y) : 1;
                    for (int k = 0; k < rows; k++)
                    {
                        row.CopyTo(dst.AsSpan((y + k) * fw));
                        rowValid[y + k] = Math.Max(rowValid[y + k], have);
                    }
                }
            }
            return dst;
        }

        void DisposePrevious(Span<uint> canvas)
        {
            if (_prevH == 0) return;
            if (_prevDisposal == (int)GifDisposal.RestoreBackground)
            {
                for (int y = 0; y < _prevH; y++) canvas.Slice((_prevY + y) * Width + _prevX, _prevW).Clear();
            }
            else if (_prevDisposal == (int)GifDisposal.RestorePrevious)
            {
                for (int y = 0; y < _prevH; y++)
                    _backup.AsSpan(y * _prevW, _prevW).CopyTo(canvas.Slice((_prevY + y) * Width + _prevX));
            }
        }

        /// <summary>Display row -> row in file order for the four pass interlace (rows 0,8,..; 4,12,..; 2,6,..; 1,3,..).</summary>
        int[] RowMap(int height)
        {
            if (_rowMap == null || _rowMap.Length < height) _rowMap = new int[height];
            var map = _rowMap;
            int n = 0;
            for (int y = 0; y < height; y += 8) map[y] = n++;
            for (int y = 4; y < height; y += 8) map[y] = n++;
            for (int y = 2; y < height; y += 4) map[y] = n++;
            for (int y = 1; y < height; y += 2) map[y] = n++;
            return map;
        }

        void Draw(byte[] indices, int decoded, GifFrame f, uint[] palette, int[]? rowMap, int[]? rowValid, Span<uint> canvas, int cw, int ch)
        {
            int fw = f.Width, trans = f.TransparentIndex;
            fixed (byte* src0 = indices)
            fixed (uint* pal = palette)
            fixed (uint* dst0 = canvas)
            {
                for (int y = 0; y < ch; y++)
                {
                    long start = (long)(rowMap != null ? rowMap[y] : y) * fw;
                    // pixels of this row that were actually decoded
                    int n = rowValid != null ? Math.Min(cw, rowValid[y]) : (int)Math.Min(cw, decoded - start);
                    if (n <= 0) continue;
                    byte* s = src0 + start;
                    uint* d = dst0 + (long)(f.Y + y) * Width + f.X;
                    if (trans < 0)
                    {
                        for (int x = 0; x < n; x++) d[x] = pal[s[x]];
                    }
                    else
                    {
                        for (int x = 0; x < n; x++)
                        {
                            int i = s[x];
                            if (i != trans) d[x] = pal[i];
                        }
                    }
                }
            }
        }

        /// <summary>Opaque frame covering the whole screen, written as packed RGB; undecoded pixels stay black.</summary>
        void DrawRgb(byte[] indices, int decoded, GifFrame f, uint[] palette, int[]? rowMap, int[]? rowValid, byte[] rgb)
        {
            int fw = f.Width, w = Width;
            fixed (byte* src0 = indices)
            fixed (uint* pal = palette)
            fixed (byte* dst0 = rgb)
            {
                for (int y = 0; y < Height; y++)
                {
                    long start = (long)(rowMap != null ? rowMap[y] : y) * fw;
                    int n = rowValid != null ? Math.Min(w, rowValid[y]) : (int)Math.Min(w, decoded - start);
                    if (n <= 0) continue;
                    byte* s = src0 + start;
                    byte* d = dst0 + (long)y * w * 3;
                    int x = 0;
                    // 4 byte stores spill into the next pixel, so the last one of a row is written bytewise
                    // (the following row may already be there when interlaced, or never come when truncated).
                    for (; x < n - 1; x++, d += 3) *(uint*)d = pal[s[x]];
                    for (; x < n; x++, d += 3)
                    {
                        byte* c = (byte*)(pal + s[x]);
                        d[0] = c[0]; d[1] = c[1]; d[2] = c[2];
                    }
                }
            }
        }
    }
}
