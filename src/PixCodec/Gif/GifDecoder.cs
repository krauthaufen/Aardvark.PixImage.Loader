using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace PixCodec
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
            /// <summary>First frame as the Rgb or Rgba image <see cref="Gif.Decode"/> returns.</summary>
            FirstFrame,
        }

        // Plausibility bound for sizes read from headers: an LZW code is at least 3 bits wide and expands to at most
        // 4095 pixels, so a file cannot paint more than this many pixels per byte. Any image that fills its canvas
        // passes by a wide margin; small canvases are always allowed.
        const long MaxPixelsPerByte = 10923;
        const long AlwaysAllowedPixels = 1L << 20;

        public int Width, Height, FrameCount;
        public readonly Mode FrameMode;

        readonly long _maxPixels;
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

        public GifDecoderState(ReadOnlySpan<byte> data, GifDecodeOptions? options)
        {
            FrameMode = options?.FrameMode == GifFrameMode.Indexed ? Mode.Indexed : Mode.Composited;
            _maxPixels = Math.Clamp(options?.MaxPixels ?? (1L << 28), 1, int.MaxValue / 4);
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

        static uint Pack(byte r, byte g, byte b)
            => BitConverter.IsLittleEndian
                ? r | (uint)g << 8 | (uint)b << 16 | 0xFF000000u
                : (uint)r << 24 | (uint)g << 16 | (uint)b << 8 | 0xFFu;

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
            // Some writers leave the logical screen empty; the first frame then defines it.
            if (Width == 0 || Height == 0)
            {
                if (FrameCount != 0) throw new ImageFormatException("GIF logical screen has zero size");
                Width = Math.Min(fx + fw, 65535);
                Height = Math.Min(fy + fh, 65535);
            }

            var frame = new GifFrame
            {
                Index = FrameCount,
                X = fx, Y = fy, Width = fw, Height = fh,
                DelayCs = _gceDelay,
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

            long cap = Math.Max(AlwaysAllowedPixels, data.Length * MaxPixelsPerByte);
            long framePixels = (long)fw * fh, screenPixels = (long)Width * Height;
            if (framePixels > cap) throw new ImageFormatException("GIF frame size is implausible for the amount of data");
            if (framePixels > _maxPixels) throw new ImageNotSupportedException("GIF frame exceeds the configured pixel limit");
            if (mode != Mode.Indexed)
            {
                if (screenPixels > cap) throw new ImageFormatException("GIF screen size is implausible for the amount of data");
                if (screenPixels > _maxPixels) throw new ImageNotSupportedException("GIF screen exceeds the configured pixel limit");
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

                int total = (int)framePixels;
                byte[] indices;
                if (mode == Mode.Indexed && !frame.Interlaced) indices = new byte[total];
                else indices = rented = pool.Rent(total);
                int decoded = GifLzw.Decode(codes.AsSpan(0, codeLen), minCodeSize, indices.AsSpan(0, total));
                frame.Truncated = decoded < total;

                if (mode == Mode.Indexed)
                {
                    byte fill = (byte)Math.Max(0, frame.TransparentIndex);
                    indices.AsSpan(decoded, total - decoded).Fill(fill);
                    if (frame.Interlaced)
                    {
                        var map = RowMap(fh);
                        var dst = new byte[total];
                        for (int y = 0; y < fh; y++) indices.AsSpan(map[y] * fw, fw).CopyTo(dst.AsSpan(y * fw));
                        indices = dst;
                    }
                    frame.Indices = indices;
                    frame.Palette = !localRgb.IsEmpty ? localRgb.ToArray() : _globalRgb != null ? (byte[])_globalRgb.Clone() : GrayRamp();
                    return frame;
                }

                uint[] palette;
                if (!localRgb.IsEmpty)
                {
                    palette = _localPalette ??= new uint[256];
                    LoadPalette(localRgb, palette);
                }
                else if (_globalPalette != null) palette = _globalPalette;
                else
                {
                    // No color table at all: the spec leaves it to the decoder, use a gray ramp.
                    palette = _localPalette ??= new uint[256];
                    LoadPalette(GrayRamp(), palette);
                }

                int[]? rowMap = frame.Interlaced ? RowMap(fh) : null;
                if (mode == Mode.FirstFrame && !FirstFrameHasAlpha(frame))
                {
                    var rgb = new byte[checked(Width * Height * 3)];
                    DrawRgb(indices, decoded, frame, palette, rowMap, rgb);
                    frame.Image = new RawImage(Width, Height, ColorModel.Rgb, rgb);
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

                Draw(indices, decoded, frame, palette, rowMap, canvas, cw, ch);

                byte[] pixels = mode == Mode.FirstFrame ? _canvas : (byte[])_canvas.Clone();
                frame.Image = new RawImage(Width, Height, ColorModel.Rgba, pixels);
                return frame;
            }
            finally
            {
                pool.Return(codes);
                if (rented != null) pool.Return(rented);
            }
        }

        static byte[] GrayRamp()
        {
            var p = new byte[768];
            for (int i = 0; i < 256; i++) p[i * 3] = p[i * 3 + 1] = p[i * 3 + 2] = (byte)i;
            return p;
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

        void Draw(byte[] indices, int decoded, GifFrame f, uint[] palette, int[]? rowMap, Span<uint> canvas, int cw, int ch)
        {
            int fw = f.Width, trans = f.TransparentIndex;
            fixed (byte* src0 = indices)
            fixed (uint* pal = palette)
            fixed (uint* dst0 = canvas)
            {
                for (int y = 0; y < ch; y++)
                {
                    long start = (long)(rowMap != null ? rowMap[y] : y) * fw;
                    int n = (int)Math.Min(cw, decoded - start);     // pixels of this row that were actually decoded
                    if (n <= 0) { if (rowMap == null) break; continue; }
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
        void DrawRgb(byte[] indices, int decoded, GifFrame f, uint[] palette, int[]? rowMap, byte[] rgb)
        {
            int fw = f.Width, w = Width;
            bool le = BitConverter.IsLittleEndian;
            fixed (byte* src0 = indices)
            fixed (uint* pal = palette)
            fixed (byte* dst0 = rgb)
            {
                for (int y = 0; y < Height; y++)
                {
                    long start = (long)(rowMap != null ? rowMap[y] : y) * fw;
                    int n = (int)Math.Min(w, decoded - start);
                    if (n <= 0) { if (rowMap == null) break; continue; }
                    byte* s = src0 + start;
                    byte* d = dst0 + (long)y * w * 3;
                    int x = 0;
                    if (le)
                    {
                        // 4 byte stores spill into the next pixel, so the last one of a row is written bytewise
                        // (the following row may already be there when interlaced, or never come when truncated).
                        for (; x < n - 1; x++, d += 3) *(uint*)d = pal[s[x]];
                    }
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
