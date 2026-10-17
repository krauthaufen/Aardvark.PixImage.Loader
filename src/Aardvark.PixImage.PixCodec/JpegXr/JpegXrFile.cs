using System;

namespace Aardvark.ImageCodecs.JpegXrImpl
{
    /// <summary>A JPEG XR file (Annex A container or bare codestream) opened for <see cref="JpegXr"/>.</summary>
    internal sealed class JxrFile
    {
        public JxrContainer? Container;
        public JxrCodestream Cs = null!;
        /// <summary>The separate alpha plane codestream (ALPHA_OFFSET), if any.</summary>
        public JxrCodestream? AlphaCs;
        public JxrPixelFormat? PixelFormat;
        public double DpiX, DpiY;
        public int Orientation = 1;

        public static JxrFile Open(ReadOnlySpan<byte> d, bool headersOnly)
        {
            var f = new JxrFile();
            if (JxrContainer.IsCodestream(d))
            {
                f.Cs = JxrCodestream.Parse(d, headersOnly);
                f.Orientation = JxrContainer.Orientation(f.Cs.Ih.SpatialXfrm);
                return f;
            }
            var c = JxrContainer.Parse(d);
            f.Container = c;
            f.PixelFormat = c.PixelFormat;
            f.Cs = JxrCodestream.Parse(c.Image(d), headersOnly);
            if (c.AlphaOffset > 0) f.AlphaCs = JxrCodestream.Parse(c.Alpha(d), headersOnly);
            f.DpiX = c.DpiX;
            f.DpiY = c.DpiY;
            f.Orientation = JxrContainer.Orientation(c.SpatialXfrm);
            return f;
        }

        bool HasAlpha => Cs.Alpha != null || AlphaCs != null;

        bool Premultiplied => PixelFormat?.Premultiplied ?? Cs.Ih.Premultiplied || (AlphaCs?.Ih.Premultiplied ?? false);

        /// <summary>Channels, sample type, colour model and bits per sample of what Decode returns.</summary>
        public (int ch, SampleType type, ColorModel model, int bits) Plan(JpegXrDecodeOptions o)
        {
            var ih = Cs.Ih;
            var bd = ih.BitDepth;
            int alpha = HasAlpha ? 1 : 0;
            int color = ih.OutputFormat switch
            {
                JxrOutputFormat.YOnly => 1,
                JxrOutputFormat.Rgb or JxrOutputFormat.Rgbe or JxrOutputFormat.Yuv420 or JxrOutputFormat.Yuv422 or JxrOutputFormat.Yuv444 => 3,
                JxrOutputFormat.Cmyk or JxrOutputFormat.CmykDirect => o.ConvertToRgb ? 3 : 4,
                _ => Cs.Primary.NumComponents,
            };
            int ch = color + alpha;
            SampleType type;
            int bits;
            if (ih.OutputFormat == JxrOutputFormat.Rgbe) { type = SampleType.Float32; bits = 32; }
            else
                (type, bits) = bd switch
                {
                    JxrBitDepth.Bd1White1 or JxrBitDepth.Bd1Black1 => (SampleType.UInt8, 1),
                    JxrBitDepth.Bd5 => (SampleType.UInt8, 5),
                    JxrBitDepth.Bd565 => (SampleType.UInt8, 6),
                    JxrBitDepth.Bd8 => (SampleType.UInt8, 8),
                    JxrBitDepth.Bd10 => (SampleType.UInt16, 10),
                    JxrBitDepth.Bd16 => (SampleType.UInt16, 16),
                    JxrBitDepth.Bd16F => (SampleType.Float16, 16),
                    JxrBitDepth.Bd16S => (SampleType.Float32, 16),
                    _ => (SampleType.Float32, 32),
                };
            bool colorModel = ih.OutputFormat is JxrOutputFormat.YOnly or JxrOutputFormat.Rgb or JxrOutputFormat.Rgbe
                || ih.OutputFormat is JxrOutputFormat.Cmyk or JxrOutputFormat.CmykDirect && o.ConvertToRgb;
            var model = !colorModel ? ColorModel.Other
                : color == 1 ? (alpha != 0 ? ColorModel.GrayAlpha : ColorModel.Gray)
                : alpha != 0 ? ColorModel.Rgba : ColorModel.Rgb;
            return (ch, type, model, bits);
        }

        /// <summary>The reference output of the primary planes and, if present, the alpha plane (interleaved or separate).</summary>
        public (JxrOutput.Arrays primary, JxrOutput.Arrays? alpha) DecodeArrays(ReadOnlySpan<byte> d, bool parallel)
        {
            var full = Container != null ? JxrCodestream.Parse(Container.Image(d), false) : JxrCodestream.Parse(d, false);
            var res = JxrDecoder.Decode(full, parallel);
            var primary = JxrOutput.Format(res.Primary, false, parallel);
            JxrOutput.Arrays? alpha = null;
            if (res.Alpha != null) alpha = JxrOutput.Format(res.Alpha, true, parallel);
            else if (Container != null && Container.AlphaOffset > 0)
            {
                var acs = JxrCodestream.Parse(Container.Alpha(d), false);
                var ares = JxrDecoder.Decode(acs, parallel);
                // a separate alpha plane is a coded image of its own: its primary plane (or its interleaved alpha plane)
                alpha = ares.Alpha != null ? JxrOutput.Format(ares.Alpha, true, parallel) : JxrOutput.Format(ares.Primary, false, parallel);
                if (ares.Primary.Ih.Width != full.Ih.Width || ares.Primary.Ih.Height != full.Ih.Height)
                    throw new ImageFormatException("JPEG XR: alpha plane size differs from the image size");
            }
            return (primary, alpha);
        }

        public RawImage Decode(ReadOnlySpan<byte> d, JpegXrDecodeOptions o)
        {
            var (prim, alpha) = DecodeArrays(d, o.Multithreaded);
            var (ch, type, model, _) = Plan(o);
            var ih = Cs.Ih;
            int w = ih.Width, h = ih.Height;
            long n = (long)w * h;
            var px = RawImage.CreateArray(type, checked((int)(n * ch)));
            int color = ch - (alpha != null ? 1 : 0);
            var bd = ih.BitDepth;
            var fmt = ih.OutputFormat;

            if (fmt == JxrOutputFormat.Rgbe)
            {
                var f = (float[])px;
                for (long i = 0; i < n; i++)
                {
                    int e = prim.A[3][i];
                    if (e == 0) continue;
                    double s = Math.ScaleB(1.0, e - 136);
                    f[i * ch] = (float)(prim.A[0][i] * s);
                    f[i * ch + 1] = (float)(prim.A[1][i] * s);
                    f[i * ch + 2] = (float)(prim.A[2][i] * s);
                }
            }
            else if (bd is JxrBitDepth.Bd1White1 or JxrBitDepth.Bd1Black1)
            {
                // the reference output packs 8 samples per byte, MSB first; 1 means white for BD1WHITE1, black for BD1BLACK1
                var b = (byte[])px;
                var a = prim.A[0];
                int ow = prim.W[0];
                bool black1 = bd == JxrBitDepth.Bd1Black1;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int bit = (a[y * ow + (x >> 3)] >> (7 - (x & 7))) & 1;
                        b[((long)y * w + x) * ch] = (byte)((bit != 0) != black1 ? 255 : 0);
                    }
            }
            else if (fmt == JxrOutputFormat.Rgb && bd is JxrBitDepth.Bd5 or JxrBitDepth.Bd565 or JxrBitDepth.Bd10)
            {
                var a = prim.A[0];
                bool swapped = !ih.RedBlueNotSwapped;   // R in the most significant field
                for (long i = 0; i < n; i++)
                {
                    int v = a[i];
                    int lo, mid, hi;
                    if (bd == JxrBitDepth.Bd10) { lo = v & 1023; mid = (v >> 10) & 1023; hi = (v >> 20) & 1023; }
                    else if (bd == JxrBitDepth.Bd5) { lo = v & 31; mid = (v >> 5) & 31; hi = (v >> 10) & 31; }
                    else { lo = v & 31; mid = (v >> 5) & 63; hi = (v >> 11) & 31; }
                    int r = swapped ? hi : lo, g = mid, bl = swapped ? lo : hi;
                    if (bd == JxrBitDepth.Bd10)
                    {
                        var u = (ushort[])px;
                        u[i * ch] = (ushort)(r << 6 | r >> 4);
                        u[i * ch + 1] = (ushort)(g << 6 | g >> 4);
                        u[i * ch + 2] = (ushort)(bl << 6 | bl >> 4);
                    }
                    else
                    {
                        var b = (byte[])px;
                        b[i * ch] = (byte)(r << 3 | r >> 2);
                        b[i * ch + 1] = bd == JxrBitDepth.Bd565 ? (byte)(g << 2 | g >> 4) : (byte)(g << 3 | g >> 2);
                        b[i * ch + 2] = (byte)(bl << 3 | bl >> 2);
                    }
                }
            }
            else if (fmt is JxrOutputFormat.Cmyk or JxrOutputFormat.CmykDirect && o.ConvertToRgb)
            {
                int max = bd == JxrBitDepth.Bd16 ? 65535 : 255;
                for (long i = 0; i < n; i++)
                {
                    long k = max - prim.A[3][i];
                    for (int c = 0; c < 3; c++)
                    {
                        long v = ((max - prim.A[c][i]) * k + max / 2) / max;
                        Store(px, type, i * ch + c, (int)v, bd);
                    }
                }
            }
            else
            {
                // one output array per channel, at full size or subsampled (YCC interchange formats: replicated)
                int arrays = prim.A.Length;
                for (int c = 0; c < Math.Min(arrays, color); c++)
                {
                    var a = prim.A[c];
                    int aw = prim.W[c], ah = prim.H[c];
                    int sx = aw < w ? 1 : 0, sy = ah < h ? 1 : 0;
                    for (int y = 0; y < h; y++)
                    {
                        int ro = (y >> sy) * aw;
                        for (int x = 0; x < w; x++) Store(px, type, ((long)y * w + x) * ch + c, a[ro + (x >> sx)], bd);
                    }
                }
            }
            if (alpha != null)
                for (long i = 0; i < n; i++) Store(px, type, i * ch + ch - 1, alpha.A[0][i], bd);

            ImageMetadata? meta = null;
            var cont = Container;
            if (cont != null || Orientation > 1)
            {
                meta = new ImageMetadata { DpiX = DpiX, DpiY = DpiY, Orientation = Orientation };
                if (cont != null)
                {
                    meta.IccProfile = cont.Icc;
                    meta.Xmp = cont.Xmp;
                    meta.Exif = JxrMetadata.Exif(d);
                }
                if (type is SampleType.Float16 or SampleType.Float32) meta.Transfer = TransferFunction.Linear;
            }
            var img = new RawImage(w, h, model, px, ch, meta);
            if (alpha != null && Premultiplied && o.Unpremultiply && img.HasAlpha) DdsImpl.DdsSurfaces.Unpremultiply(img);
            return img;
        }

        /// <summary>One reference output value as a sample of the RawImage (fixed point and float bit patterns converted).</summary>
        static void Store(Array px, SampleType type, long i, int v, JxrBitDepth bd)
        {
            switch (bd)
            {
                case JxrBitDepth.Bd8: ((byte[])px)[i] = (byte)v; break;
                case JxrBitDepth.Bd16: ((ushort[])px)[i] = (ushort)v; break;
                case JxrBitDepth.Bd10: ((ushort[])px)[i] = (ushort)(v << 6 | v >> 4); break;
                case JxrBitDepth.Bd16S: ((float[])px)[i] = v / 8192f; break;
                case JxrBitDepth.Bd32S: ((float[])px)[i] = (float)(v / 16777216.0); break;
                case JxrBitDepth.Bd16F: ((Half[])px)[i] = BitConverter.UInt16BitsToHalf((ushort)v); break;
                case JxrBitDepth.Bd32F: ((float[])px)[i] = BitConverter.Int32BitsToSingle(v); break;
                default:
                    if (type == SampleType.UInt8) ((byte[])px)[i] = (byte)v;
                    else ((ushort[])px)[i] = (ushort)v;
                    break;
            }
        }
    }
}
