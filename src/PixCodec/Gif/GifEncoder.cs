using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PixCodec
{
    /// <summary>
    /// GIF writer for stills and animations. Every input frame is a complete picture; the encoder keeps track of what
    /// the viewer's canvas holds and writes the cheapest frame that turns it into the next picture.
    /// </summary>
    internal sealed class GifEncoder
    {
        readonly Stream _out;
        readonly GifEncodeOptions _o;
        readonly bool _animation;
        readonly int _maxColors;
        readonly MemoryStream _head = new MemoryStream();
        int _w, _h;
        GifPalette? _global;
        byte[]? _icc;
        byte[]? _stillIndices;
        // Working buffers come from the shared pool (fresh megabyte arrays cost more than filling them) and are
        // therefore often longer than asked for: never use their Length.
        readonly List<uint[]> _pixelBuffers = new List<uint[]>();
        readonly List<byte[]> _frameBytes = new List<byte[]>();
        readonly List<uint[]> _framePixels = new List<uint[]>();

        uint[] RentCanvas()
        {
            var a = System.Buffers.ArrayPool<uint>.Shared.Rent(_w * _h);
            _pixelBuffers.Add(a);
            return a;
        }

        byte[] RentFrameBytes(int length)
        {
            var a = System.Buffers.ArrayPool<byte>.Shared.Rent(length);
            _frameBytes.Add(a);
            return a;
        }

        void ReturnFrameBuffers()
        {
            foreach (var a in _frameBytes) System.Buffers.ArrayPool<byte>.Shared.Return(a);
            foreach (var a in _framePixels) System.Buffers.ArrayPool<uint>.Shared.Return(a);
            _frameBytes.Clear(); _framePixels.Clear();
        }
      // indices of a still from the color scan, transparent pixels marked

        public GifEncoder(Stream output, GifEncodeOptions options, bool animation)
        {
            ArgumentNullException.ThrowIfNull(output);
            if (options.MaxColors < 2 || options.MaxColors > 256)
                throw new ArgumentOutOfRangeException(nameof(options), "MaxColors must be 2..256");
            _out = output; _o = options; _animation = animation; _maxColors = options.MaxColors;
        }

        void Check(RawImage image, bool first)
        {
            ArgumentNullException.ThrowIfNull(image);
            if (image.SampleType != SampleType.UInt8)
                throw new ImageNotSupportedException($"GIF cannot store {image.SampleType} samples");
            if (image.Color == ColorModel.Other)
                throw new ImageNotSupportedException("GIF needs a Gray, GrayAlpha, Rgb or Rgba image");
            if (image.Width > 65535 || image.Height > 65535)
                throw new ImageNotSupportedException("GIF images are limited to 65535 x 65535");
            if (first) { _w = image.Width; _h = image.Height; _icc = image.Metadata?.IccProfile; }
            else if (image.Width != _w || image.Height != _h)
                throw new ArgumentException("all frames of a GIF animation must have the same size");
        }

        /// <summary>To packed r | g &lt;&lt; 8 | b &lt;&lt; 16 | 255 &lt;&lt; 24, or 0 for pixels below the alpha threshold.</summary>
        void Convert(RawImage image, uint[] dst) => Convert(image, 0, _w * _h, dst);

        /// <summary>Converts <paramref name="n"/> pixels starting at pixel <paramref name="first"/> to the start of dst.</summary>
        unsafe void Convert(RawImage image, int first, int n, uint[] dst)
        {
            byte[] bytes = image.GetData<byte>();
            uint thr = (uint)Math.Clamp(_o.AlphaThreshold, 0, 256);
            fixed (byte* src0 = bytes)
            fixed (uint* d = dst)
            {
                byte* src = src0 + (long)first * image.Channels;
                switch (image.Color)
                {
                    case ColorModel.Gray:
                        for (int i = 0; i < n; i++) d[i] = src[i] * 0x010101u | 0xFF000000u;
                        break;
                    case ColorModel.GrayAlpha:
                        for (int i = 0; i < n; i++) d[i] = src[2 * i + 1] >= thr ? src[2 * i] * 0x010101u | 0xFF000000u : 0;
                        break;
                    case ColorModel.Rgb:
                        {
                            // four bytes at a time; the last pixel has no fourth byte to read
                            byte* p = src;
                            for (int i = 0; i < n - 1; i++, p += 3) d[i] = *(uint*)p | 0xFF000000u;
                            if (n > 0) d[n - 1] = p[0] | (uint)p[1] << 8 | (uint)p[2] << 16 | 0xFF000000u;
                        }
                        break;
                    default:
                        {
                            uint* p = (uint*)src;
                            for (int i = 0; i < n; i++) d[i] = p[i] >> 24 >= thr ? p[i] | 0xFF000000u : 0;
                        }
                        break;
                }
            }
        }

        public void Run(IEnumerable<GifInputFrame> frames)
        {
            try { Encode(frames); }
            finally
            {
                ReturnFrameBuffers();
                foreach (var a in _pixelBuffers) System.Buffers.ArrayPool<uint>.Shared.Return(a);
                _pixelBuffers.Clear();
            }
        }

        void Encode(IEnumerable<GifInputFrame> frames)
        {
            uint[]? firstPixels = null;
            if (!_animation || _o.PaletteMode == GifPaletteMode.Global)
            {
                var list = frames as IReadOnlyList<GifInputFrame> ?? frames.ToList();
                if (list.Count == 0) throw new ArgumentException("no frames to encode");
                for (int i = 0; i < list.Count; i++) Check(list[i].Image, i == 0);
                frames = list;
                _global = BuildGlobalPalette(list, out firstPixels);
                WriteHeader();
            }

            uint[]? before = null, pending = null, spare = null;
            int pendingDelay = 0, index = 0;
            foreach (var f in frames)
            {
                Check(f.Image, index == 0 && _global == null);
                if (index == 0 && _global == null) WriteHeader();
                uint[] cur;
                if (index == 0 && firstPixels != null) cur = firstPixels;
                else
                {
                    cur = spare ?? RentCanvas();
                    spare = null;
                    Convert(f.Image, cur);
                }
                if (pending != null)
                {
                    bool cleared = WriteFrame(pending, before, cur, pendingDelay);
                    // 'before' becomes what the viewer shows once the pending frame was drawn and disposed.
                    if (cleared) { spare = pending; Array.Clear(before ??= RentCanvas(), 0, _w * _h); }
                    else { spare = before; before = pending; }
                }
                pending = cur;
                pendingDelay = Math.Clamp((f.DelayMs + 5) / 10, 0, 65535);
                index++;
            }
            if (pending == null) throw new ArgumentException("no frames to encode");
            WriteFrame(pending, before, null, pendingDelay);
            _out.WriteByte(0x3B);
        }

        GifPalette BuildGlobalPalette(IReadOnlyList<GifInputFrame> list, out uint[]? firstPixels)
        {
            int n = _w * _h;
            var buf = RentCanvas();
            firstPixels = list.Count == 1 ? buf : null;
            var set = new GifColorSet();
            if (list.Count == 1)
            {
                // a still: the color scan yields the palette indices on the way
                Convert(list[0].Image, buf);
                var indices = RentFrameBytes(n);
                if (set.MapAll(buf.AsSpan(0, n), indices.AsSpan(0, n))) _stillIndices = indices;
            }
            else
            {
                // Animations mostly repeat themselves: only the pixels that differ from the frame before can bring
                // new colors.
                for (int k = 0; k < list.Count && !set.Overflow; k++)
                    foreach (var (at, len) in ChangedSpans(k == 0 ? null : list[k - 1].Image, list[k].Image))
                    {
                        Convert(list[k].Image, at, len, buf);
                        set.AddAll(buf.AsSpan(0, len));
                    }
            }
            // An animation wants a transparent slot for "pixel unchanged"; a still only if it has transparent pixels.
            if (!set.Overflow)
            {
                if (set.Count + 1 <= _maxColors && (_animation || set.HasTransparent))
                    return new GifPalette(set.Colors, set.Count, true, set);
                if (set.Count <= _maxColors && !set.HasTransparent)
                    return new GifPalette(set.Colors, set.Count, false, set);
            }
            bool slot = _animation || set.HasTransparent;
            var q = new GifQuantizer();
            _stillIndices = null;
            if (list.Count == 1) q.Add(buf.AsSpan(0, n));
            else
            {
                // Every frame counts once in the statistics. Instead of counting all pixels of all frames, the first
                // frame is counted for the whole length of the animation and each later change is booked for the
                // frames that remain: the new color is added, the one it replaces taken out. The sums are the same.
                for (int k = 0; k < list.Count; k++)
                {
                    long remaining = list.Count - k;
                    foreach (var (at, len) in ChangedSpans(k == 0 ? null : list[k - 1].Image, list[k].Image))
                    {
                        Convert(list[k].Image, at, len, buf);
                        q.Add(buf.AsSpan(0, len), remaining);
                        if (k == 0) continue;
                        Convert(list[k - 1].Image, at, len, buf);
                        q.Add(buf.AsSpan(0, len), -remaining);
                    }
                }
            }
            var colors = q.BuildPalette(_maxColors - (slot ? 1 : 0), _o.RefineIterations);
            return new GifPalette(colors, colors.Length, slot, null);
        }

        /// <summary>
        /// The runs of pixels (start, length) in which <paramref name="image"/> differs from the frame before it, row
        /// by row; everything if there is no previous frame.
        /// </summary>
        List<(int At, int Length)> ChangedSpans(RawImage? previous, RawImage image)
        {
            var spans = new List<(int, int)>();
            if (previous == null) { spans.Add((0, _w * _h)); return spans; }
            if (previous.Color != image.Color)
            {
                // different layouts cannot be compared byte by byte; go through the converted pixels
                var a = System.Buffers.ArrayPool<uint>.Shared.Rent(_w);
                var b = System.Buffers.ArrayPool<uint>.Shared.Rent(_w);
                for (int y = 0; y < _h; y++)
                {
                    Convert(previous, y * _w, _w, a);
                    Convert(image, y * _w, _w, b);
                    AddSpan<uint>(spans, a.AsSpan(0, _w), b.AsSpan(0, _w), y, 1);
                }
                System.Buffers.ArrayPool<uint>.Shared.Return(a);
                System.Buffers.ArrayPool<uint>.Shared.Return(b);
                return spans;
            }
            int c = image.Channels, stride = _w * c;
            byte[] p = previous.GetData<byte>(), q = image.GetData<byte>();
            for (int y = 0; y < _h; y++) AddSpan<byte>(spans, p.AsSpan(y * stride, stride), q.AsSpan(y * stride, stride), y, c);
            return spans;
        }

        void AddSpan<T>(List<(int, int)> spans, ReadOnlySpan<T> a, ReadOnlySpan<T> b, int y, int perPixel) where T : unmanaged, IEquatable<T>
        {
            int same = a.CommonPrefixLength(b);
            if (same == a.Length) return;
            int end = a.Length;
            while (a[end - 1].Equals(b[end - 1])) end--;
            int first = same / perPixel, last = (end - 1) / perPixel;
            spans.Add((y * _w + first, last - first + 1));
        }

        static int BitsFor(int paletteSize)
        {
            int bits = 1;
            while (1 << bits < paletteSize) bits++;
            return bits;
        }

        void WritePalette(Stream s, GifPalette p)
        {
            int entries = 1 << BitsFor(p.Size);
            Span<byte> t = stackalloc byte[768];
            t.Clear();
            for (int i = 0; i < p.ColorCount; i++)
            {
                uint c = p.Colors[i];
                t[i * 3] = (byte)c; t[i * 3 + 1] = (byte)(c >> 8); t[i * 3 + 2] = (byte)(c >> 16);
            }
            s.Write(t.Slice(0, entries * 3));
        }

        static void WriteU16(Stream s, int v) { s.WriteByte((byte)v); s.WriteByte((byte)(v >> 8)); }

        void WriteHeader()
        {
            var s = _head;
            s.SetLength(0);
            s.Write("GIF89a"u8);
            WriteU16(s, _w); WriteU16(s, _h);
            s.WriteByte((byte)(_global != null ? 0xF0 | (BitsFor(_global.Size) - 1) : 0x70));
            s.WriteByte(0);     // background index
            s.WriteByte(0);     // no pixel aspect ratio
            if (_global != null) WritePalette(s, _global);
            if (_animation && _o.LoopCount is int loops)
            {
                s.Write(stackalloc byte[] { 0x21, 0xFF, 11 });
                s.Write("NETSCAPE2.0"u8);
                s.Write(stackalloc byte[] { 3, 1, (byte)loops, (byte)(loops >> 8), 0 });
            }
            if (_icc is { Length: > 0 })
            {
                s.Write(stackalloc byte[] { 0x21, 0xFF, 11 });
                s.Write("ICCRGBG1012"u8);
                for (int i = 0; i < _icc.Length; i += 255)
                {
                    int n = Math.Min(255, _icc.Length - i);
                    s.WriteByte((byte)n);
                    s.Write(_icc, i, n);
                }
                s.WriteByte(0);
            }
            if (!string.IsNullOrEmpty(_o.Comment))
            {
                s.WriteByte(0x21); s.WriteByte(0xFE);
                var text = System.Text.Encoding.Latin1.GetBytes(_o.Comment);
                for (int i = 0; i < text.Length; i += 255)
                {
                    int n = Math.Min(255, text.Length - i);
                    s.WriteByte((byte)n);
                    s.Write(text, i, n);
                }
                s.WriteByte(0);
            }
            s.WriteTo(_out);
        }

        /// <summary>
        /// Writes <paramref name="target"/> as the frame that follows canvas state <paramref name="before"/> (null = empty
        /// canvas, first frame). <paramref name="next"/> is the picture after it, needed to choose the disposal: pixels
        /// can only become transparent again by clearing, in which case this frame is stored over the whole screen with
        /// "restore to background" and true is returned.
        /// </summary>
        bool WriteFrame(uint[] target, uint[]? before, uint[]? next, int delayCs)
        {
            int w = _w, h = _h;
            bool clear = next != null && NeedsClear(next.AsSpan(0, w * h), target);
            bool substitute = before != null && _o.FrameDifferencing && (_global == null || _global.TransparentIndex >= 0);

            int rx = 0, ry = 0, rw = w, rh = h;
            if (before != null && _o.FrameDifferencing && !clear) ChangedBounds(target, before, out rx, out ry, out rw, out rh);

            uint[] sub = substitute || rw != w || rh != h ? Cut(target, substitute ? before : null, rx, ry, rw, rh) : target;

            GifPalette palette;
            byte[]? indices = null;
            if (_global != null)
            {
                palette = _global;
                if (_stillIndices != null && palette.IsExact && ReferenceEquals(sub, target))
                {
                    indices = _stillIndices;
                    FixMarkers(indices.AsSpan(0, rw * rh), palette);
                }
            }
            else
            {
                var set = new GifColorSet();
                indices = RentFrameBytes(rw * rh);
                if (set.MapAll(sub.AsSpan(0, rw * rh), indices.AsSpan(0, rw * rh)) && set.Count + (set.HasTransparent ? 1 : 0) <= _maxColors)
                {
                    palette = new GifPalette(set.Colors, set.Count, set.HasTransparent, set);
                    FixMarkers(indices.AsSpan(0, rw * rh), palette);
                }
                else
                {
                    palette = null!;
                    indices = null;
                    if (substitute)
                    {
                        // The transparent slot for unchanged pixels costs a color; if the rectangle fits the
                        // palette exactly without it, prefer lossless.
                        var plain = Cut(target, null, rx, ry, rw, rh);
                        var set2 = new GifColorSet();
                        set2.AddAll(plain.AsSpan(0, rw * rh));
                        if (!set2.Overflow && !set2.HasTransparent && set2.Count <= _maxColors)
                        {
                            sub = plain;
                            palette = new GifPalette(set2.Colors, set2.Count, false, set2);
                        }
                    }
                    if (palette == null)
                    {
                        var q = new GifQuantizer();
                        q.Add(sub.AsSpan(0, rw * rh));
                        var colors = q.BuildPalette(_maxColors - (set.HasTransparent ? 1 : 0), _o.RefineIterations);
                        palette = new GifPalette(colors, colors.Length, set.HasTransparent, null);
                    }
                }
            }

            if (indices == null)
            {
                indices = RentFrameBytes(rw * rh);
                palette.Map(sub, indices, rw, rh, _o.Dither);
            }
            bool usesTransparency = palette.TransparentIndex >= 0 && (_global == null || sub.AsSpan(0, rw * rh).IndexOf(0u) >= 0);
            if (_o.Interlace && rh > 1) indices = Interlace(indices, RentFrameBytes(rw * rh), rw, rh);

            var s = _head;
            s.SetLength(0);
            if (_animation || usesTransparency)
            {
                int disposal = !_animation ? 0 : clear ? 2 : 1;
                s.Write(stackalloc byte[] { 0x21, 0xF9, 4, (byte)(disposal << 2 | (usesTransparency ? 1 : 0)) });
                WriteU16(s, delayCs);
                s.WriteByte((byte)(usesTransparency ? palette.TransparentIndex : 0));
                s.WriteByte(0);
            }
            s.WriteByte(0x2C);
            WriteU16(s, rx); WriteU16(s, ry); WriteU16(s, rw); WriteU16(s, rh);
            int bits = BitsFor(palette.Size);
            s.WriteByte((byte)((_global == null ? 0x80 | (bits - 1) : 0) | (_o.Interlace && rh > 1 ? 0x40 : 0)));
            if (_global == null) WritePalette(s, palette);
            s.WriteTo(_out);
            GifLzw.Encode(indices.AsSpan(0, rw * rh), Math.Max(2, bits), _out);
            ReturnFrameBuffers();
            return clear;
        }

        /// <summary>Turns the color scan's transparency marker into the palette's transparent index.</summary>
        static void FixMarkers(Span<byte> indices, GifPalette palette)
        {
            if (palette.TransparentIndex >= 0 && palette.TransparentIndex != GifColorSet.TransparentMarker)
                indices.Replace(GifColorSet.TransparentMarker, (byte)palette.TransparentIndex);
        }

        /// <summary>True if some pixel is opaque on the canvas but transparent in the next picture.</summary>
        static bool NeedsClear(ReadOnlySpan<uint> next, uint[] canvas)
        {
            if (next.IndexOf(0u) < 0) return false;
            for (int i = 0; i < next.Length; i++)
                if (next[i] == 0 && canvas[i] != 0) return true;
            return false;
        }

        /// <summary>Bounding rectangle of the pixels that differ; a single pixel if nothing changed (the delay still counts).</summary>
        void ChangedBounds(uint[] a, uint[] b, out int rx, out int ry, out int rw, out int rh)
        {
            int w = _w, h = _h;
            int top = 0, bottom = h - 1;
            while (top < h && a.AsSpan(top * w, w).SequenceEqual(b.AsSpan(top * w, w))) top++;
            if (top == h) { rx = ry = 0; rw = rh = 1; return; }
            while (a.AsSpan(bottom * w, w).SequenceEqual(b.AsSpan(bottom * w, w))) bottom--;
            int left = w, right = 0;
            for (int y = top; y <= bottom; y++)
            {
                var ra = a.AsSpan(y * w, w); var rb = b.AsSpan(y * w, w);
                int l = ra.Slice(0, left).CommonPrefixLength(rb.Slice(0, left));
                if (l < left) left = l;
                int r = w - 1;
                while (r > right && ra[r] == rb[r]) r--;
                if (r > right) right = r;
            }
            if (right < left) right = left;
            rx = left; ry = top; rw = right - left + 1; rh = bottom - top + 1;
        }

        /// <summary>Copies a rectangle; with <paramref name="before"/> given, pixels equal to the canvas become transparent.</summary>
        uint[] Cut(uint[] target, uint[]? before, int rx, int ry, int rw, int rh)
        {
            var sub = System.Buffers.ArrayPool<uint>.Shared.Rent(rw * rh);
            _framePixels.Add(sub);
            for (int y = 0; y < rh; y++)
            {
                var src = target.AsSpan((ry + y) * _w + rx, rw);
                var dst = sub.AsSpan(y * rw, rw);
                if (before == null) { src.CopyTo(dst); continue; }
                var old = before.AsSpan((ry + y) * _w + rx, rw);
                for (int x = 0; x < rw; x++) dst[x] = src[x] == old[x] ? 0 : src[x];
            }
            return sub;
        }

        static byte[] Interlace(byte[] rows, byte[] dst, int w, int h)
        {
            int n = 0;
            for (int y = 0; y < h; y += 8) rows.AsSpan(y * w, w).CopyTo(dst.AsSpan(n++ * w));
            for (int y = 4; y < h; y += 8) rows.AsSpan(y * w, w).CopyTo(dst.AsSpan(n++ * w));
            for (int y = 2; y < h; y += 4) rows.AsSpan(y * w, w).CopyTo(dst.AsSpan(n++ * w));
            for (int y = 1; y < h; y += 2) rows.AsSpan(y * w, w).CopyTo(dst.AsSpan(n++ * w));
            return dst;
        }
    }
}
