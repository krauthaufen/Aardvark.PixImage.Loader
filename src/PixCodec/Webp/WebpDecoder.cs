using System;
using System.Buffers.Binary;

namespace PixCodec
{
    /// <summary>Decodes the frames of a parsed WebP file one after the other and composites animations.</summary>
    internal sealed class WebpDecoder
    {
        readonly WebpContainer _c;
        readonly WebpDecodeOptions _opt;
        byte[]? _canvas;
        int _index;
        WebpFrameEntry? _prev;
        bool _prevKey;

        public WebpDecoder(WebpContainer c, WebpDecodeOptions options)
        {
            _c = c; _opt = options;
        }

        public static bool OutputHasAlpha(WebpContainer c)
        {
            var f = c.Frames[0];
            if (c.AlphaFlag || f.HasAlpha) return true;
            return c.Animated && !IsFull(c, f);
        }

        static bool IsFull(WebpContainer c, WebpFrameEntry f) => f.Width == c.Width && f.Height == c.Height;

        public static RawImage DecodeFirst(WebpContainer c, ReadOnlySpan<byte> data, WebpDecodeOptions opt)
        {
            bool alpha = OutputHasAlpha(c);
            byte[] px;
            if (!c.Animated) px = DecodeFrame(data, c.Frames[0], alpha ? 4 : 3, opt.MaxPixels);
            else
            {
                var st = new WebpDecoder(c, new WebpDecodeOptions { MaxPixels = opt.MaxPixels });
                px = (byte[])st.Next(data)!.Image.Data;
                if (!alpha)
                {
                    var rgb = new byte[px.Length / 4 * 3];
                    for (int i = 0, o = 0; i < px.Length; i += 4, o += 3) { rgb[o] = px[i]; rgb[o + 1] = px[i + 1]; rgb[o + 2] = px[i + 2]; }
                    px = rgb;
                }
            }
            return new RawImage(c.Width, c.Height, alpha ? ColorModel.Rgba : ColorModel.Rgb, px, 0, Metadata(c, data));
        }

        /// <summary>The frame's own pixels as RGB or RGBA; alpha is 255 where the frame has none.</summary>
        static byte[] DecodeFrame(ReadOnlySpan<byte> data, WebpFrameEntry f, int channels, long maxPixels)
        {
            var bits = data.Slice(f.DataOffset, f.DataLength);
            if (f.Lossless)
            {
                var argb = Vp8lDecoder.Decode(bits, out int w, out int h, maxPixels);
                var px = new byte[w * h * channels];
                if (channels == 4)
                    for (int i = 0, o = 0; i < argb.Length; i++, o += 4)
                    {
                        uint v = argb[i];
                        px[o] = (byte)(v >> 16); px[o + 1] = (byte)(v >> 8); px[o + 2] = (byte)v; px[o + 3] = (byte)(v >> 24);
                    }
                else
                    for (int i = 0, o = 0; i < argb.Length; i++, o += 3)
                    {
                        uint v = argb[i];
                        px[o] = (byte)(v >> 16); px[o + 1] = (byte)(v >> 8); px[o + 2] = (byte)v;
                    }
                return px;
            }
            var frame = Vp8Decoder.Decode(bits, maxPixels);
            var dst = new byte[frame.Width * frame.Height * channels];
            WebpYuv.ToRgb(frame, dst, channels);
            if (channels == 4)
            {
                if (f.AlphLength >= 0)
                {
                    var a = WebpAlpha.Decode(data.Slice(f.AlphOffset, f.AlphLength), frame.Width, frame.Height);
                    for (int i = 0; i < a.Length; i++) dst[4 * i + 3] = a[i];
                }
                else
                    for (int i = 3; i < dst.Length; i += 4) dst[i] = 255;
            }
            return dst;
        }

        public WebpFrame? Next(ReadOnlySpan<byte> data)
        {
            if (_index >= _c.Frames.Count) return null;
            var f = _c.Frames[_index];
            var frame = new WebpFrame
            {
                Index = _index, X = f.X, Y = f.Y, Width = f.Width, Height = f.Height, DurationMs = f.Duration,
                Blend = f.NoBlend ? WebpBlend.NoBlend : WebpBlend.AlphaBlend,
                Disposal = f.DisposeBackground ? WebpDisposal.Background : WebpDisposal.None,
                IsLossless = f.Lossless, HasAlpha = f.HasAlpha,
            };
            if (_opt.FrameMode == WebpFrameMode.Raw)
            {
                int ch = f.HasAlpha ? 4 : 3;
                frame.Image = new RawImage(f.Width, f.Height, ch == 4 ? ColorModel.Rgba : ColorModel.Rgb, DecodeFrame(data, f, ch, _opt.MaxPixels));
            }
            else if (!_c.Animated)
                frame.Image = new RawImage(f.Width, f.Height, ColorModel.Rgba, DecodeFrame(data, f, 4, _opt.MaxPixels));
            else
                frame.Image = new RawImage(_c.Width, _c.Height, ColorModel.Rgba, Composite(data, f));
            _index++;
            return frame;
        }

        /// <summary>
        /// Draws the frame onto the canvas the way libwebp's animation decoder does. A "key frame" starts from an empty
        /// canvas and is copied as stored; other frames are blended over what the previous frame left behind.
        /// </summary>
        byte[] Composite(ReadOnlySpan<byte> data, WebpFrameEntry f)
        {
            int cw = _c.Width, chh = _c.Height;
            if ((long)cw * chh > _opt.MaxPixels)
                throw new ImageNotSupportedException($"WebP canvas of {cw}x{chh} exceeds the configured pixel limit");
            var px = DecodeFrame(data, f, 4, _opt.MaxPixels);       // before the canvas: garbage must not allocate one
            var canvas = _canvas ??= new byte[cw * chh * 4];
            var prev = _prev;
            bool key = prev == null
                || ((!f.HasAlpha || f.NoBlend) && IsFull(_c, f))
                || (prev.DisposeBackground && (IsFull(_c, prev) || _prevKey));
            if (key)
            {
                if (prev != null && !IsFull(_c, f)) Array.Clear(canvas);
            }
            else if (prev!.DisposeBackground)
            {
                for (int y = 0; y < prev.Height; y++)
                    canvas.AsSpan(((prev.Y + y) * cw + prev.X) * 4, prev.Width * 4).Clear();
            }

            bool blend = !key && !f.NoBlend && f.HasAlpha;
            // pixels inside a rectangle that was just cleared are copied, not blended with transparency
            int clearX0 = 0, clearX1 = 0, clearY0 = 0, clearY1 = 0;
            if (blend && prev!.DisposeBackground)
            {
                clearX0 = prev.X; clearX1 = prev.X + prev.Width; clearY0 = prev.Y; clearY1 = prev.Y + prev.Height;
            }
            for (int y = 0; y < f.Height; y++)
            {
                var src = px.AsSpan(y * f.Width * 4, f.Width * 4);
                var dst = canvas.AsSpan(((f.Y + y) * cw + f.X) * 4, f.Width * 4);
                if (!blend) { src.CopyTo(dst); continue; }
                bool rowCleared = f.Y + y >= clearY0 && f.Y + y < clearY1;
                for (int x = 0; x < f.Width; x++)
                {
                    var s = src.Slice(x * 4, 4);
                    var d = dst.Slice(x * 4, 4);
                    uint sa = s[3];
                    if (sa == 255 || (rowCleared && f.X + x >= clearX0 && f.X + x < clearX1)) { s.CopyTo(d); continue; }
                    if (sa == 0) continue;
                    uint da = d[3] * (256 - sa) >> 8;
                    uint ba = sa + da;
                    uint scale = (1u << 24) / ba;
                    d[0] = (byte)((s[0] * sa + d[0] * da) * scale >> 24);
                    d[1] = (byte)((s[1] * sa + d[1] * da) * scale >> 24);
                    d[2] = (byte)((s[2] * sa + d[2] * da) * scale >> 24);
                    d[3] = (byte)ba;
                }
            }
            _prev = f; _prevKey = key;
            return (byte[])canvas.Clone();
        }

        // ------------------------------------------------------------------ metadata

        public static void FillInfo(WebpContainer c, ReadOnlySpan<byte> data, WebpAnimationInfo info)
        {
            info.Width = c.Width; info.Height = c.Height;
            info.FrameCount = c.Frames.Count;
            info.IsAnimated = c.Animated;
            info.LoopCount = c.Animated ? c.LoopCount : 1;
            uint bg = c.Background;     // stored as B, G, R, A
            info.BackgroundColor = ((byte)(bg >> 16), (byte)(bg >> 8), (byte)bg, (byte)(bg >> 24));
            info.HasAlpha = OutputHasAlpha(c);
            var m = Metadata(c, data);
            info.IccProfile = m?.IccProfile;
            info.Exif = m?.Exif;
            info.Xmp = c.XmpLength < 0 ? null : data.Slice(c.XmpOffset, c.XmpLength).ToArray();
        }

        static ImageMetadata? Metadata(WebpContainer c, ReadOnlySpan<byte> data)
        {
            if (c.IccLength < 0 && c.ExifLength < 0) return null;
            var m = new ImageMetadata();
            if (c.IccLength >= 0) m.IccProfile = data.Slice(c.IccOffset, c.IccLength).ToArray();
            if (c.ExifLength >= 0)
            {
                var exif = data.Slice(c.ExifOffset, c.ExifLength);
                // some writers keep the JPEG APP1 prefix
                if (exif.Length >= 6 && exif.Slice(0, 6).SequenceEqual("Exif\0\0"u8)) exif = exif.Slice(6);
                m.Exif = exif.ToArray();
                m.Orientation = ExifOrientation(exif);
            }
            return m;
        }

        /// <summary>Orientation tag (0x0112) of the first IFD, 0 if absent or the block is malformed.</summary>
        internal static int ExifOrientation(ReadOnlySpan<byte> t)
        {
            if (t.Length < 8) return 0;
            bool le;
            if (t[0] == 'I' && t[1] == 'I') le = true;
            else if (t[0] == 'M' && t[1] == 'M') le = false;
            else return 0;
            uint ifd = le ? BinaryPrimitives.ReadUInt32LittleEndian(t.Slice(4)) : BinaryPrimitives.ReadUInt32BigEndian(t.Slice(4));
            if (ifd > (uint)t.Length - 2) return 0;
            int n = le ? BinaryPrimitives.ReadUInt16LittleEndian(t.Slice((int)ifd)) : BinaryPrimitives.ReadUInt16BigEndian(t.Slice((int)ifd));
            for (int i = 0; i < n; i++)
            {
                long e = ifd + 2 + 12L * i;
                if (e + 12 > t.Length) return 0;
                var entry = t.Slice((int)e, 12);
                int tag = le ? BinaryPrimitives.ReadUInt16LittleEndian(entry) : BinaryPrimitives.ReadUInt16BigEndian(entry);
                if (tag != 0x0112) continue;
                int v = le ? BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(8)) : BinaryPrimitives.ReadUInt16BigEndian(entry.Slice(8));
                return v >= 1 && v <= 8 ? v : 0;
            }
            return 0;
        }
    }
}
