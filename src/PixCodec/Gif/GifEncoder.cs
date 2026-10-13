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
        void Convert(RawImage image, uint[] dst)
        {
            byte[] src = image.GetData<byte>();
            int n = _w * _h, thr = _o.AlphaThreshold;
            switch (image.Color)
            {
                case ColorModel.Gray:
                    for (int i = 0; i < n; i++) dst[i] = src[i] * 0x010101u | 0xFF000000u;
                    break;
                case ColorModel.GrayAlpha:
                    for (int i = 0; i < n; i++) dst[i] = src[2 * i + 1] >= thr ? src[2 * i] * 0x010101u | 0xFF000000u : 0;
                    break;
                case ColorModel.Rgb:
                    for (int i = 0, s = 0; i < n; i++, s += 3)
                        dst[i] = src[s] | (uint)src[s + 1] << 8 | (uint)src[s + 2] << 16 | 0xFF000000u;
                    break;
                default:
                    for (int i = 0, s = 0; i < n; i++, s += 4)
                        dst[i] = src[s + 3] >= thr ? src[s] | (uint)src[s + 1] << 8 | (uint)src[s + 2] << 16 | 0xFF000000u : 0;
                    break;
            }
        }

        public void Run(IEnumerable<GifInputFrame> frames)
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
                    cur = spare ?? new uint[_w * _h];
                    spare = null;
                    Convert(f.Image, cur);
                }
                if (pending != null)
                {
                    bool cleared = WriteFrame(pending, before, cur, pendingDelay);
                    // 'before' becomes what the viewer shows once the pending frame was drawn and disposed.
                    if (cleared) { spare = pending; Array.Clear(before ??= new uint[_w * _h]); }
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
            var buf = new uint[_w * _h];
            firstPixels = list.Count == 1 ? buf : null;
            var set = new GifColorSet();
            foreach (var f in list)
            {
                Convert(f.Image, buf);
                set.AddAll(buf);
                if (set.Overflow) break;
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
            if (list.Count == 1) q.Add(buf);
            else
                foreach (var f in list) { Convert(f.Image, buf); q.Add(buf); }
            var colors = q.BuildPalette(_maxColors - (slot ? 1 : 0), _o.RefineIterations);
            return new GifPalette(colors, colors.Length, slot, null);
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
            bool clear = next != null && NeedsClear(next, target);
            bool substitute = before != null && _o.FrameDifferencing && (_global == null || _global.TransparentIndex >= 0);

            int rx = 0, ry = 0, rw = w, rh = h;
            if (before != null && _o.FrameDifferencing && !clear) ChangedBounds(target, before, out rx, out ry, out rw, out rh);

            uint[] sub = substitute || rw != w || rh != h ? Cut(target, substitute ? before : null, rx, ry, rw, rh) : target;

            GifPalette palette;
            if (_global != null) palette = _global;
            else
            {
                var set = new GifColorSet();
                set.AddAll(sub);
                if (!set.Overflow && set.Count + (set.HasTransparent ? 1 : 0) <= _maxColors)
                    palette = new GifPalette(set.Colors, set.Count, set.HasTransparent, set);
                else
                {
                    palette = null!;
                    if (substitute)
                    {
                        // The transparent slot for unchanged pixels costs a color; if the rectangle fits the
                        // palette exactly without it, prefer lossless.
                        var plain = Cut(target, null, rx, ry, rw, rh);
                        var set2 = new GifColorSet();
                        set2.AddAll(plain);
                        if (!set2.Overflow && !set2.HasTransparent && set2.Count <= _maxColors)
                        {
                            sub = plain;
                            palette = new GifPalette(set2.Colors, set2.Count, false, set2);
                        }
                    }
                    if (palette == null)
                    {
                        var q = new GifQuantizer();
                        q.Add(sub);
                        var colors = q.BuildPalette(_maxColors - (set.HasTransparent ? 1 : 0), _o.RefineIterations);
                        palette = new GifPalette(colors, colors.Length, set.HasTransparent, null);
                    }
                }
            }

            var indices = new byte[rw * rh];
            palette.Map(sub, indices, rw, rh, _o.Dither);
            bool usesTransparency = palette.TransparentIndex >= 0 && (_global == null || sub.AsSpan(0, rw * rh).IndexOf(0u) >= 0);
            if (_o.Interlace && rh > 1) indices = Interlace(indices, rw, rh);

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
            GifLzw.Encode(indices, Math.Max(2, bits), _out);
            return clear;
        }

        /// <summary>True if some pixel is opaque on the canvas but transparent in the next picture.</summary>
        static bool NeedsClear(uint[] next, uint[] canvas)
        {
            if (next.AsSpan().IndexOf(0u) < 0) return false;
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
            var sub = new uint[rw * rh];
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

        static byte[] Interlace(byte[] rows, int w, int h)
        {
            var dst = new byte[rows.Length];
            int n = 0;
            for (int y = 0; y < h; y += 8) rows.AsSpan(y * w, w).CopyTo(dst.AsSpan(n++ * w));
            for (int y = 4; y < h; y += 8) rows.AsSpan(y * w, w).CopyTo(dst.AsSpan(n++ * w));
            for (int y = 2; y < h; y += 4) rows.AsSpan(y * w, w).CopyTo(dst.AsSpan(n++ * w));
            for (int y = 1; y < h; y += 2) rows.AsSpan(y * w, w).CopyTo(dst.AsSpan(n++ * w));
            return dst;
        }
    }
}
