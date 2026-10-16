using System;
using System.Collections.Generic;
using System.IO;
using Aardvark.ImageCodecs.JpegXl;

namespace Aardvark.ImageCodecs
{
    public sealed class JxlDecodeOptions
    {
        /// <summary>
        /// Rotate / mirror the pixels according to the orientation in the codestream header (default), so that the image
        /// comes back upright with metadata orientation 1. False returns the pixels as stored, with the orientation in the
        /// metadata.
        /// </summary>
        public bool ApplyOrientation { get; set; } = true;

        /// <summary>
        /// Upper bound for the image and for each frame, in pixels; larger files are rejected with an
        /// <see cref="ImageNotSupportedException"/>. A JPEG XL file of a few bytes can describe a huge image.
        /// </summary>
        public long MaxPixels { get; set; } = 1L << 28;

        /// <summary>Decode groups, LF groups and colour conversion on several threads (same result). Default true.</summary>
        public bool Multithreaded { get; set; } = true;

        /// <summary>
        /// Draw spot colour extra channels onto the colour channels (as libjxl does by default). Default true.
        /// </summary>
        public bool RenderSpotColours { get; set; } = true;

        /// <summary>Test hook: return Float32 samples whatever the header says.</summary>
        internal bool ForceFloat { get; set; }

        internal static readonly JxlDecodeOptions Default = new();
    }

    /// <summary>One displayed frame of a JPEG XL animation (or the single composited still image).</summary>
    public sealed class JxlFrame
    {
        public int Index { get; internal set; }
        /// <summary>Display time in animation ticks (see <see cref="JxlAnimation.TicksPerSecond"/>); 0 for still images.</summary>
        public uint DurationTicks { get; internal set; }
        /// <summary>Display time in milliseconds (rounded), 0 for still images.</summary>
        public int DurationMs { get; internal set; }
        /// <summary>SMPTE timecode of the frame when the animation has timecodes.</summary>
        public uint Timecode { get; internal set; }
        public string Name { get; internal set; } = "";
        /// <summary>The whole canvas after this frame, as <see cref="Jxl.Decode"/> returns images.</summary>
        public RawImage Image { get; internal set; } = null!;
    }

    public sealed class JxlAnimation
    {
        public int Width { get; internal set; }
        public int Height { get; internal set; }
        public bool IsAnimated { get; internal set; }
        /// <summary>Number of repetitions, 0 = forever.</summary>
        public int LoopCount { get; internal set; }
        public double TicksPerSecond { get; internal set; }
        public IReadOnlyList<JxlFrame> Frames { get; internal set; } = Array.Empty<JxlFrame>();
    }

    /// <summary>
    /// JPEG XL (ISO/IEC 18181-1 codestream, 18181-2 container) decoder: VarDCT and modular coding, all entropy coding
    /// modes, filters, image features, extra channels, frames and animation. Output is 8 / 16 bit (UInt8 / UInt16), 17 to 31
    /// bit (UInt32) integer or float (Float16 / Float32) as the header's bit depth says, in the colour space the header
    /// describes (XYB is converted to it); alpha is returned unpremultiplied.
    /// </summary>
    public static class Jxl
    {
        public static bool IsMatch(ReadOnlySpan<byte> header) => JxlContainer.IsMatch(header);

        /// <summary>Reads size, channels, sample type, orientation and colour description from the headers.</summary>
        public static ImageInfo GetInfo(ReadOnlySpan<byte> data, JxlDecodeOptions? options = null)
        {
            var opt = options ?? JxlDecodeOptions.Default;
            var c = JxlContainer.Parse(data);
            var cs = c.CodestreamPrefix(data, int.MaxValue);
            var br = new JxlBitReader(cs);
            var h = JxlImageHeader.Read(br);
            int frames = 1;
            bool animated = h.Animation != null;
            if (animated)
            {
                if (h.Colour.WantIcc) JxlIcc.Read(br);
                br.ZeroPadToByte();
                frames = CountFrames(cs, br.BytePosition, h);
            }
            var (color, ch, type) = OutputFormat(h);
            int ow = h.Width, oh = h.Height;
            bool applied = opt.ApplyOrientation && h.Orientation > 1;
            if (applied && h.Orientation >= 5) (ow, oh) = (oh, ow);
            var tf = TransferOf(h, out _);
            var info = new ImageInfo(ImageFormat.Jxl, ow, oh, ch, type, color, frames)
            {
                BitsPerSample = h.BitDepth.Bits,
                Orientation = h.Orientation,
                OrientationApplied = applied,
                HasIccProfile = h.Colour.WantIcc,
                IsAnimated = animated,
                Transfer = tf,
            };
            return info;
        }

        static int CountFrames(byte[] cs, int pos, JxlImageHeader h)
        {
            if (h.HavePreview)
            {
                var ph = new JxlImageHeader { Width = h.PreviewWidth, Height = h.PreviewHeight, ExtraChannels = h.ExtraChannels, XybEncoded = h.XybEncoded, Colour = h.Colour };
                pos = SkipFrame(cs, pos, ph, out _);
            }
            int n = 0;
            while (true)
            {
                pos = SkipFrame(cs, pos, h, out var fh);
                if ((fh.Type == JxlFrameType.Regular || fh.Type == JxlFrameType.SkipProgressive) && (fh.IsLast || fh.Duration != 0)) n++;
                if (fh.IsLast) return Math.Max(1, n);
                if (n > 1 << 16) throw new ImageNotSupportedException("JPEG XL: too many frames");
            }
        }

        static int SkipFrame(byte[] cs, int pos, JxlImageHeader h, out JxlFrameHeader fh)
        {
            if (pos >= cs.Length) throw new ImageFormatException("JPEG XL: truncated codestream");
            var br = new JxlBitReader(cs, pos, cs.Length - pos);
            fh = JxlFrameHeader.Read(br, h);
            var toc = JxlToc.Read(br, JxlFrameDims.Of(fh));
            long next = (long)pos + br.BytePosition + toc.TotalSize;
            if (next > cs.Length) throw new ImageFormatException("JPEG XL: truncated codestream");
            return (int)next;
        }

        static (ColorModel color, int channels, SampleType type) OutputFormat(JxlImageHeader h)
        {
            bool gray = h.Colour.IsGray;
            bool alpha = h.AlphaChannel >= 0;
            var color = gray ? (alpha ? ColorModel.GrayAlpha : ColorModel.Gray) : (alpha ? ColorModel.Rgba : ColorModel.Rgb);
            var bd = h.BitDepth;
            SampleType t = bd.Float ? (bd.Bits <= 16 ? SampleType.Float16 : SampleType.Float32)
                : bd.Bits <= 8 ? SampleType.UInt8 : bd.Bits <= 16 ? SampleType.UInt16 : SampleType.UInt32;
            return (color, RawImage.ChannelCount(color), t);
        }

        static TransferFunction TransferOf(JxlImageHeader h, out double gamma)
        {
            gamma = 0;
            if (h.Colour.WantIcc) return TransferFunction.Unknown;
            switch (h.Colour.Transfer)
            {
                case JxlTransfer.Srgb: return TransferFunction.Srgb;
                case JxlTransfer.Linear: return TransferFunction.Linear;
                case JxlTransfer.Pq: return TransferFunction.Pq;
                case JxlTransfer.Hlg: return TransferFunction.Hlg;
                case JxlTransfer.Dci: gamma = 1 / 2.6; return TransferFunction.Gamma;
                case JxlTransfer.Gamma: gamma = h.Colour.Gamma; return TransferFunction.Gamma;
                default: return TransferFunction.Unknown;
            }
        }

        /// <summary>Decodes the image (for an animation: the first frame).</summary>
        public static RawImage Decode(ReadOnlySpan<byte> data, JxlDecodeOptions? options = null)
        {
            var opt = options ?? JxlDecodeOptions.Default;
            var c = JxlContainer.Parse(data);
            var dec = new JxlDecoder(c.Codestream(data), opt);
            var f = dec.Next() ?? throw new ImageFormatException("JPEG XL: no displayed frame");
            var meta = Metadata(c, data, dec.Header);
            return Output(dec.Header, f.Canvas, meta, opt);
        }

        public static RawImage Decode(Stream stream, JxlDecodeOptions? options = null) => Decode(ImageCodec.ReadAll(stream), options);

        /// <summary>Decodes all displayed frames (each the whole canvas).</summary>
        public static JxlAnimation DecodeAnimation(ReadOnlySpan<byte> data, JxlDecodeOptions? options = null)
        {
            var opt = options ?? JxlDecodeOptions.Default;
            var c = JxlContainer.Parse(data);
            var dec = new JxlDecoder(c.Codestream(data), opt);
            var h = dec.Header;
            var frames = new List<JxlFrame>();
            double tps = h.Animation != null ? (double)h.Animation.TpsNumerator / h.Animation.TpsDenominator : 0;
            while (dec.Next() is { } f)
            {
                var meta = Metadata(c, data, h);
                frames.Add(new JxlFrame
                {
                    Index = frames.Count, DurationTicks = f.Duration, Timecode = f.Timecode, Name = f.Name,
                    DurationMs = tps > 0 ? (int)Math.Round(f.Duration * 1000.0 / tps) : 0,
                    Image = Output(h, f.Canvas, meta, opt),
                });
            }
            var first = frames.Count > 0 ? frames[0].Image : null;
            return new JxlAnimation
            {
                Width = first?.Width ?? h.Width, Height = first?.Height ?? h.Height, IsAnimated = h.Animation != null,
                LoopCount = (int)(h.Animation?.NumLoops ?? 1), TicksPerSecond = tps, Frames = frames,
            };
        }

        /// <summary>Every displayed frame (one for a still image).</summary>
        public static RawImage[] DecodeAll(ReadOnlySpan<byte> data, JxlDecodeOptions? options = null)
        {
            var a = DecodeAnimation(data, options);
            var r = new RawImage[a.Frames.Count];
            for (int i = 0; i < r.Length; i++) r[i] = a.Frames[i].Image;
            return r;
        }

        /// <summary>EXIF (TIFF structure), XMP and JUMBF of the container, Brotli compressed boxes included.</summary>
        public static (byte[]? Exif, byte[]? Xmp, byte[]? Jumbf) GetMetadataBoxes(ReadOnlySpan<byte> data)
        {
            var c = JxlContainer.Parse(data);
            byte[]? exif = null, xmp = null, jumb = null;
            foreach (var e in c.Metadata(data, "Exif")) { exif = JxlContainer.ExifTiff(e); if (exif != null) break; }
            foreach (var x in c.Metadata(data, "xml ")) { xmp = x; break; }
            foreach (var j in c.Metadata(data, "jumb")) { jumb = j; break; }
            return (exif, xmp, jumb);
        }

        static ImageMetadata Metadata(JxlContainer c, ReadOnlySpan<byte> data, JxlImageHeader h)
        {
            var m = new ImageMetadata { Orientation = h.Orientation, IccProfile = h.Icc };
            if (c.IsContainer)
            {
                var (exif, xmp, _) = GetMetadataBoxes(data);
                m.Exif = exif; m.Xmp = xmp;
            }
            m.Transfer = TransferOf(h, out m.Gamma);
            return m;
        }

        // ------------------------------------------------------------------ output conversion

        static RawImage Output(JxlImageHeader h, JxlFrameImage canvas, ImageMetadata meta, JxlDecodeOptions opt)
        {
            var (color, nch, type) = OutputFormat(h);
            if (opt.ForceFloat) type = SampleType.Float32;
            int w = canvas.Width, hh = canvas.Height, n = w * hh;
            int ncol = h.Colour.IsGray ? 1 : 3;
            int alpha = h.AlphaChannel;
            var planes = new float[nch][];
            int[][]? ints = canvas.IntPlanes;
            var p = canvas.Planes;
            if (ints != null && (opt.ForceFloat || opt.RenderSpotColours && HasSpot(h) || alpha >= 0 && h.ExtraChannels[alpha].AlphaAssociated)) ints = null;
            if (ints == null)
            {
                // colour planes in the output colour space
                float[][] col = new float[ncol][];
                if (h.XybEncoded)
                {
                    var r = (float[])p[0].Clone(); var g = (float[])p[1].Clone(); var b = (float[])p[2].Clone();
                    if (!h.Colour.WantIcc && JxlColour.NeedsPrimaryConversion(h.Colour))
                        JxlColour.ApplyMatrix(r, g, b, n, JxlColour.SrgbToTargetMatrix(h.Colour));
                    var ce = h.Colour.WantIcc ? new JxlColourEncoding() : h.Colour;
                    if (ncol == 1)
                    {
                        // grey: the luminance of the (achromatic) linear RGB
                        var gr = new float[n];
                        for (int i = 0; i < n; i++) gr[i] = 0.2126f * r[i] + 0.7152f * g[i] + 0.0722f * b[i];
                        JxlColour.FromLinear(gr, n, ce, h.ToneMapping.IntensityTarget, opt.Multithreaded);
                        col[0] = gr;
                    }
                    else
                    {
                        JxlColour.FromLinear(r, n, ce, h.ToneMapping.IntensityTarget, opt.Multithreaded);
                        JxlColour.FromLinear(g, n, ce, h.ToneMapping.IntensityTarget, opt.Multithreaded);
                        JxlColour.FromLinear(b, n, ce, h.ToneMapping.IntensityTarget, opt.Multithreaded);
                        col[0] = r; col[1] = g; col[2] = b;
                    }
                    if (h.Colour.WantIcc) meta.IccProfile = null;   // converted to sRGB
                }
                else
                {
                    for (int c = 0; c < ncol; c++) col[c] = p[Math.Min(c, canvas.NumColour - 1)];
                }
                if (opt.RenderSpotColours && HasSpot(h)) col = RenderSpot(h, canvas, col, n);
                for (int c = 0; c < ncol; c++) planes[c] = col[c];
                if (alpha >= 0)
                {
                    var a = p[canvas.NumColour + alpha];
                    planes[ncol] = a;
                    if (h.ExtraChannels[alpha].AlphaAssociated)
                    {
                        for (int c = 0; c < ncol; c++)
                        {
                            var src = planes[c];
                            var o = new float[n];
                            for (int i = 0; i < n; i++) o[i] = a[i] > 0 ? src[i] / a[i] : 0;
                            planes[c] = o;
                        }
                    }
                }
            }
            Array data = RawImage.CreateArray(type, checked(n * nch));
            int maxv = h.BitDepth.Float ? 0 : (int)Math.Min((1L << h.BitDepth.Bits) - 1, int.MaxValue);
            for (int c = 0; c < nch; c++)
            {
                bool isAlpha = c == ncol;
                if (ints != null)
                {
                    var src = isAlpha ? ints[canvas.NumColour + alpha] : ints[c];
                    int srcBits = isAlpha ? h.ExtraChannels[alpha].BitDepth.Bits : h.BitDepth.Bits;
                    WriteInts(data, type, c, nch, src, srcBits, h.BitDepth.Bits);
                }
                else WriteFloats(data, type, c, nch, planes[c], maxv);
            }
            var img = new RawImage(w, hh, color, data, nch, meta);
            if (opt.ApplyOrientation && h.Orientation > 1) img = ImageOps.ApplyOrientation(img, h.Orientation);
            return img;
        }

        static bool HasSpot(JxlImageHeader h)
        {
            foreach (var e in h.ExtraChannels) if (e.Type == JxlExtraChannelType.SpotColour) return true;
            return false;
        }

        static float[][] RenderSpot(JxlImageHeader h, JxlFrameImage canvas, float[][] col, int n)
        {
            var r = new float[col.Length][];
            for (int c = 0; c < col.Length; c++) r[c] = (float[])col[c].Clone();
            for (int e = 0; e < h.ExtraChannels.Length; e++)
            {
                var ec = h.ExtraChannels[e];
                if (ec.Type != JxlExtraChannelType.SpotColour) continue;
                var s = canvas.Planes[canvas.NumColour + e];
                float[] sc = { ec.SpotRed, ec.SpotGreen, ec.SpotBlue };
                for (int c = 0; c < col.Length; c++)
                {
                    var o = r[c];
                    float v = col.Length == 1 ? (sc[0] + sc[1] + sc[2]) / 3 : sc[c];
                    for (int i = 0; i < n; i++)
                    {
                        float mix = ec.SpotSolidity * s[i];
                        o[i] = mix * v + (1 - mix) * o[i];
                    }
                }
            }
            return r;
        }

        static void WriteInts(Array data, SampleType type, int c, int nch, int[] src, int srcBits, int dstBits)
        {
            int n = src.Length;
            long maxSrc = (1L << srcBits) - 1, maxDst = (1L << dstBits) - 1;
            bool rescale = srcBits != dstBits;
            for (int i = 0; i < n; i++)
            {
                long v = src[i];
                if (v < 0) v = 0; else if (v > maxSrc) v = maxSrc;
                if (rescale) v = (v * maxDst + maxSrc / 2) / maxSrc;
                int o = i * nch + c;
                switch (type)
                {
                    case SampleType.UInt8: ((byte[])data)[o] = (byte)v; break;
                    case SampleType.UInt16: ((ushort[])data)[o] = (ushort)v; break;
                    default: ((uint[])data)[o] = (uint)v; break;
                }
            }
        }

        static void WriteFloats(Array data, SampleType type, int c, int nch, float[] src, int maxv)
        {
            int n = src.Length;
            switch (type)
            {
                case SampleType.Float32:
                    {
                        var d = (float[])data;
                        for (int i = 0; i < n; i++) d[i * nch + c] = src[i];
                        break;
                    }
                case SampleType.Float16:
                    {
                        var d = (Half[])data;
                        for (int i = 0; i < n; i++) d[i * nch + c] = (Half)src[i];
                        break;
                    }
                case SampleType.UInt8:
                    {
                        var d = (byte[])data;
                        for (int i = 0; i < n; i++)
                        {
                            float v = src[i] * maxv;
                            d[i * nch + c] = (byte)(v <= 0 ? 0 : v >= maxv ? maxv : (int)(v + 0.5f));
                        }
                        break;
                    }
                case SampleType.UInt16:
                    {
                        var d = (ushort[])data;
                        for (int i = 0; i < n; i++)
                        {
                            float v = src[i] * maxv;
                            d[i * nch + c] = (ushort)(v <= 0 ? 0 : v >= maxv ? maxv : (int)(v + 0.5f));
                        }
                        break;
                    }
                default:
                    {
                        var d = (uint[])data;
                        for (int i = 0; i < n; i++)
                        {
                            double v = (double)src[i] * maxv;
                            d[i * nch + c] = (uint)(v <= 0 ? 0 : v >= maxv ? maxv : (long)(v + 0.5));
                        }
                        break;
                    }
            }
        }
    }
}
