using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Aardvark.ImageCodecs.JpegXrImpl
{
    /// <summary>What an encode target of <see cref="JpegXrPixelFormat"/> means for the codestream.</summary>
    internal sealed record JxrTarget(string Name, JxrBitDepth Bd, JxrOutputFormat Of, int Color, bool Alpha, bool Premultiplied = false);

    /// <summary>
    /// The JPEG XR encoder: input conversion (the inverse of output formatting, D.2 / D.3), the planes, tiles and packets of
    /// the codestream, and the Annex A container.
    /// </summary>
    internal static class JxrEncoder
    {
        public static JxrTarget Target(RawImage img, JpegXrEncodeOptions o)
        {
            var t = img.SampleType;
            bool f16 = t == SampleType.Float16, f32 = t is SampleType.Float32 or SampleType.Float64;
            bool wide = t is SampleType.UInt16 or SampleType.UInt32 or SampleType.Int16 or SampleType.Int32;
            var pf = o.PixelFormat;
            if (pf == JpegXrPixelFormat.Auto)
            {
                switch (img.Color)
                {
                    case ColorModel.Gray:
                        pf = f16 ? JpegXrPixelFormat.Gray16Half : f32 ? JpegXrPixelFormat.Gray32Float : wide ? JpegXrPixelFormat.Gray16 : JpegXrPixelFormat.Gray8;
                        break;
                    case ColorModel.Rgb:
                        pf = f16 ? JpegXrPixelFormat.Rgb48Half : f32 ? JpegXrPixelFormat.Rgb128Float : wide ? JpegXrPixelFormat.Rgb48 : JpegXrPixelFormat.Rgb24;
                        break;
                    case ColorModel.GrayAlpha:   // there are no gray + alpha formats: gray is coded as RGB
                    case ColorModel.Rgba:
                        pf = f16 ? JpegXrPixelFormat.Rgba64Half : f32 ? JpegXrPixelFormat.Rgba128Float : wide ? JpegXrPixelFormat.Rgba64 : JpegXrPixelFormat.Bgra32;
                        break;
                    default:
                        if (img.Channels is < 3 or > 9 || f16 || f32) throw new ArgumentException($"JPEG XR: cannot store {img.Channels} channels of {t} (n-channel formats have 3 .. 8 channels of 8 or 16 bits, plus alpha)");
                        pf = wide ? JpegXrPixelFormat.Channels16 : JpegXrPixelFormat.Channels8;
                        break;
                }
            }
            int nOther = img.Channels - (o.LastChannelIsAlpha ? 1 : 0);
            return pf switch
            {
                JpegXrPixelFormat.Gray8 => new("8bppGray", JxrBitDepth.Bd8, JxrOutputFormat.YOnly, 1, false),
                JpegXrPixelFormat.Gray16 => new("16bppGray", JxrBitDepth.Bd16, JxrOutputFormat.YOnly, 1, false),
                JpegXrPixelFormat.Gray16Fixed => new("16bppGrayFixedPoint", JxrBitDepth.Bd16S, JxrOutputFormat.YOnly, 1, false),
                JpegXrPixelFormat.Gray16Half => new("16bppGrayHalf", JxrBitDepth.Bd16F, JxrOutputFormat.YOnly, 1, false),
                JpegXrPixelFormat.Gray32Fixed => new("32bppGrayFixedPoint", JxrBitDepth.Bd32S, JxrOutputFormat.YOnly, 1, false),
                JpegXrPixelFormat.Gray32Float => new("32bppGrayFloat", JxrBitDepth.Bd32F, JxrOutputFormat.YOnly, 1, false),
                JpegXrPixelFormat.BlackWhite => new("BlackWhite", JxrBitDepth.Bd1White1, JxrOutputFormat.YOnly, 1, false),
                JpegXrPixelFormat.Rgb24 => new("24bppRGB", JxrBitDepth.Bd8, JxrOutputFormat.Rgb, 3, false),
                JpegXrPixelFormat.Bgr24 => new("24bppBGR", JxrBitDepth.Bd8, JxrOutputFormat.Rgb, 3, false),
                JpegXrPixelFormat.Bgr32 => new("32bppBGR", JxrBitDepth.Bd8, JxrOutputFormat.Rgb, 3, false),
                JpegXrPixelFormat.Rgb48 => new("48bppRGB", JxrBitDepth.Bd16, JxrOutputFormat.Rgb, 3, false),
                JpegXrPixelFormat.Rgb48Fixed => new("48bppRGBFixedPoint", JxrBitDepth.Bd16S, JxrOutputFormat.Rgb, 3, false),
                JpegXrPixelFormat.Rgb48Half => new("48bppRGBHalf", JxrBitDepth.Bd16F, JxrOutputFormat.Rgb, 3, false),
                JpegXrPixelFormat.Rgb64Fixed => new("64bppRGBFixedPoint", JxrBitDepth.Bd16S, JxrOutputFormat.Rgb, 3, false),
                JpegXrPixelFormat.Rgb64Half => new("64bppRGBHalf", JxrBitDepth.Bd16F, JxrOutputFormat.Rgb, 3, false),
                JpegXrPixelFormat.Rgb96Fixed => new("96bppRGBFixedPoint", JxrBitDepth.Bd32S, JxrOutputFormat.Rgb, 3, false),
                JpegXrPixelFormat.Rgb128Fixed => new("128bppRGBFixedPoint", JxrBitDepth.Bd32S, JxrOutputFormat.Rgb, 3, false),
                JpegXrPixelFormat.Rgb128Float => new("128bppRGBFloat", JxrBitDepth.Bd32F, JxrOutputFormat.Rgb, 3, false),
                JpegXrPixelFormat.Bgr555 => new("16bppBGR555", JxrBitDepth.Bd5, JxrOutputFormat.Rgb, 3, false),
                JpegXrPixelFormat.Bgr565 => new("16bppBGR565", JxrBitDepth.Bd565, JxrOutputFormat.Rgb, 3, false),
                JpegXrPixelFormat.Bgr101010 => new("32bppBGR101010", JxrBitDepth.Bd10, JxrOutputFormat.Rgb, 3, false),
                JpegXrPixelFormat.Rgbe => new("32bppRGBE", JxrBitDepth.Bd8, JxrOutputFormat.Rgbe, 3, false),
                JpegXrPixelFormat.Bgra32 => new("32bppBGRA", JxrBitDepth.Bd8, JxrOutputFormat.Rgb, 3, true),
                JpegXrPixelFormat.Rgba64 => new("64bppRGBA", JxrBitDepth.Bd16, JxrOutputFormat.Rgb, 3, true),
                JpegXrPixelFormat.Rgba64Fixed => new("64bppRGBAFixedPoint", JxrBitDepth.Bd16S, JxrOutputFormat.Rgb, 3, true),
                JpegXrPixelFormat.Rgba64Half => new("64bppRGBAHalf", JxrBitDepth.Bd16F, JxrOutputFormat.Rgb, 3, true),
                JpegXrPixelFormat.Rgba128Fixed => new("128bppRGBAFixedPoint", JxrBitDepth.Bd32S, JxrOutputFormat.Rgb, 3, true),
                JpegXrPixelFormat.Rgba128Float => new("128bppRGBAFloat", JxrBitDepth.Bd32F, JxrOutputFormat.Rgb, 3, true),
                JpegXrPixelFormat.Pbgra32 => new("32bppPBGRA", JxrBitDepth.Bd8, JxrOutputFormat.Rgb, 3, true, true),
                JpegXrPixelFormat.Prgba64 => new("64bppPRGBA", JxrBitDepth.Bd16, JxrOutputFormat.Rgb, 3, true, true),
                JpegXrPixelFormat.Prgba128Float => new("128bppPRGBAFloat", JxrBitDepth.Bd32F, JxrOutputFormat.Rgb, 3, true, true),
                JpegXrPixelFormat.Cmyk32 => new("32bppCMYK", JxrBitDepth.Bd8, JxrOutputFormat.Cmyk, 4, false),
                JpegXrPixelFormat.Cmyk64 => new("64bppCMYK", JxrBitDepth.Bd16, JxrOutputFormat.Cmyk, 4, false),
                JpegXrPixelFormat.CmykAlpha40 => new("40bppCMYKAlpha", JxrBitDepth.Bd8, JxrOutputFormat.Cmyk, 4, true),
                JpegXrPixelFormat.CmykAlpha80 => new("80bppCMYKAlpha", JxrBitDepth.Bd16, JxrOutputFormat.Cmyk, 4, true),
                JpegXrPixelFormat.CmykDirect32 => new("32bppCMYKDIRECT", JxrBitDepth.Bd8, JxrOutputFormat.CmykDirect, 4, false),
                JpegXrPixelFormat.CmykDirect64 => new("64bppCMYKDIRECT", JxrBitDepth.Bd16, JxrOutputFormat.CmykDirect, 4, false),
                JpegXrPixelFormat.CmykDirectAlpha40 => new("40bppCMYKDIRECTAlpha", JxrBitDepth.Bd8, JxrOutputFormat.CmykDirect, 4, true),
                JpegXrPixelFormat.CmykDirectAlpha80 => new("80bppCMYKDIRECTAlpha", JxrBitDepth.Bd16, JxrOutputFormat.CmykDirect, 4, true),
                JpegXrPixelFormat.Channels8 => nOther is >= 3 and <= 8
                    ? new(o.LastChannelIsAlpha ? $"{8 * (nOther + 1)}bpp{nOther}ChannelsAlpha" : $"{8 * nOther}bpp{nOther}Channels", JxrBitDepth.Bd8, JxrOutputFormat.NComponent, nOther, o.LastChannelIsAlpha)
                    : throw new ArgumentException("JPEG XR: n-channel formats have 3 .. 8 channels (plus alpha)"),
                JpegXrPixelFormat.Channels16 => nOther is >= 3 and <= 8
                    ? new(o.LastChannelIsAlpha ? $"{16 * (nOther + 1)}bpp{nOther}ChannelsAlpha" : $"{16 * nOther}bpp{nOther}Channels", JxrBitDepth.Bd16, JxrOutputFormat.NComponent, nOther, o.LastChannelIsAlpha)
                    : throw new ArgumentException("JPEG XR: n-channel formats have 3 .. 8 channels (plus alpha)"),
                JpegXrPixelFormat.Ycc420 => new("12bppYCC420", JxrBitDepth.Bd8, JxrOutputFormat.Yuv420, 3, false),
                JpegXrPixelFormat.Ycc422 => new("16bppYCC422", JxrBitDepth.Bd8, JxrOutputFormat.Yuv422, 3, false),
                JpegXrPixelFormat.Ycc444 => new("24bppYCC444", JxrBitDepth.Bd8, JxrOutputFormat.Yuv444, 3, false),
                JpegXrPixelFormat.Ycc422_10 => new("20bppYCC422", JxrBitDepth.Bd10, JxrOutputFormat.Yuv422, 3, false),
                JpegXrPixelFormat.Ycc444_10 => new("30bppYCC444", JxrBitDepth.Bd10, JxrOutputFormat.Yuv444, 3, false),
                JpegXrPixelFormat.Ycc422_16 => new("32bppYCC422", JxrBitDepth.Bd16, JxrOutputFormat.Yuv422, 3, false),
                JpegXrPixelFormat.Ycc444_16 => new("48bppYCC444", JxrBitDepth.Bd16, JxrOutputFormat.Yuv444, 3, false),
                JpegXrPixelFormat.Ycc420Alpha => new("20bppYCC420Alpha", JxrBitDepth.Bd8, JxrOutputFormat.Yuv420, 3, true),
                JpegXrPixelFormat.Ycc422Alpha => new("24bppYCC422Alpha", JxrBitDepth.Bd8, JxrOutputFormat.Yuv422, 3, true),
                JpegXrPixelFormat.Ycc444Alpha => new("32bppYCC444Alpha", JxrBitDepth.Bd8, JxrOutputFormat.Yuv444, 3, true),
                _ => throw new ArgumentException($"JPEG XR: unknown pixel format {pf}"),
            };
        }

        // ------------------------------------------------------------------ input samples

        /// <summary>
        /// The samples of the image as reference output values of the target (what the decoder's output formatting
        /// produces): one int per channel, color channels first, alpha last.
        /// </summary>
        sealed class Source
        {
            public readonly int W, H, Ch;
            readonly RawImage _img;
            readonly JxrTarget _t;
            readonly int[] _map;   // RawImage channel of each target channel (-1: constant)
            readonly int _maxIn;

            public Source(RawImage img, JxrTarget t)
            {
                _img = img;
                _t = t;
                W = img.Width;
                H = img.Height;
                Ch = t.Color + (t.Alpha ? 1 : 0);
                _map = new int[Ch];
                int ic = img.Channels;
                if (t.Color == 3 && img.Color is ColorModel.Gray or ColorModel.GrayAlpha)
                {
                    _map[0] = _map[1] = _map[2] = 0;
                    if (t.Alpha) _map[3] = img.HasAlpha ? 1 : -1;
                }
                else
                {
                    for (int c = 0; c < t.Color; c++) _map[c] = c < ic - (img.HasAlpha ? 1 : 0) ? c : -1;
                    if (t.Alpha) _map[t.Color] = img.HasAlpha || ic > t.Color ? ic - 1 : -1;
                }
                _maxIn = img.SampleType switch { SampleType.UInt8 => 255, SampleType.UInt16 => 65535, _ => 0 };
            }

            /// <summary>Row y of target channel c as reference output values.</summary>
            public void Row(int y, int c, Span<int> o)
            {
                int ic = _map[c];
                bool alpha = _t.Alpha && c == _t.Color;
                // RGBE: float bits (converted later); BD565: 5 bit red and blue
                var bd = _t.Of == JxrOutputFormat.Rgbe ? JxrBitDepth.Bd32F : _t.Bd == JxrBitDepth.Bd565 && c != 1 ? JxrBitDepth.Bd5 : _t.Bd;
                if (ic < 0)
                {
                    // missing channel: opaque alpha, black colour
                    int v = alpha ? MaxOf(bd, true) : 0;
                    o.Slice(0, W).Fill(v);
                    return;
                }
                int ch = _img.Channels;
                long b = (long)y * W * ch + ic;
                switch (_img.SampleType)
                {
                    case SampleType.UInt8:
                        {
                            var d = (byte[])_img.Data;
                            for (int x = 0; x < W; x++) o[x] = FromUnorm(d[b + (long)x * ch], 255, bd);
                            break;
                        }
                    case SampleType.UInt16:
                        {
                            var d = (ushort[])_img.Data;
                            for (int x = 0; x < W; x++) o[x] = FromUnorm(d[b + (long)x * ch], 65535, bd);
                            break;
                        }
                    case SampleType.Float16:
                        {
                            var d = (Half[])_img.Data;
                            if (bd == JxrBitDepth.Bd16F)
                                for (int x = 0; x < W; x++) o[x] = BitConverter.HalfToUInt16Bits(d[b + (long)x * ch]);
                            else
                                for (int x = 0; x < W; x++) o[x] = FromFloat((float)d[b + (long)x * ch], bd);
                            break;
                        }
                    case SampleType.Float32:
                        {
                            var d = (float[])_img.Data;
                            for (int x = 0; x < W; x++) o[x] = FromFloat(d[b + (long)x * ch], bd);
                            break;
                        }
                    case SampleType.Float64:
                        {
                            var d = (double[])_img.Data;
                            for (int x = 0; x < W; x++) o[x] = FromFloat((float)d[b + (long)x * ch], bd);
                            break;
                        }
                    default:
                        {
                            var a = _img.Data;
                            for (int x = 0; x < W; x++)
                            {
                                double v = Convert.ToDouble(a.GetValue(b + (long)x * ch));
                                o[x] = _img.SampleType is SampleType.UInt32 ? FromUnorm((int)(v / 65537.0), 65535, bd) : FromFloat((float)(v / 65535.0), bd);
                            }
                            break;
                        }
                }
            }

            static int MaxOf(JxrBitDepth bd, bool alpha) => bd switch
            {
                JxrBitDepth.Bd8 => 255,
                JxrBitDepth.Bd16 => 65535,
                JxrBitDepth.Bd16S => 8192,
                JxrBitDepth.Bd32S => 1 << 24,
                JxrBitDepth.Bd16F => BitConverter.HalfToUInt16Bits((Half)1),
                JxrBitDepth.Bd32F => BitConverter.SingleToInt32Bits(1f),
                JxrBitDepth.Bd5 => 31,
                JxrBitDepth.Bd565 => 63,
                JxrBitDepth.Bd10 => 1023,
                _ => 1,
            };

            static int FromUnorm(int v, int max, JxrBitDepth bd)
            {
                switch (bd)
                {
                    case JxrBitDepth.Bd8: return max == 255 ? v : (v * 255 + 32767) / 65535;
                    case JxrBitDepth.Bd16: return max == 65535 ? v : v * 257;
                    case JxrBitDepth.Bd5: return (v * 31 + max / 2) / max;
                    case JxrBitDepth.Bd565: return (v * 63 + max / 2) / max;   // the 6 bit field; R and B are rescaled by the caller
                    case JxrBitDepth.Bd10: return max == 65535 ? Math.Min(v, 1023) : (v * 1023 + max / 2) / max;   // UInt16: 10 bit values
                    case JxrBitDepth.Bd1White1: case JxrBitDepth.Bd1Black1: return v * 2 >= max ? 1 : 0;
                    default: return FromFloat(v / (float)max, bd);
                }
            }

            static int FromFloat(float f, JxrBitDepth bd)
            {
                if (float.IsNaN(f)) f = 0;
                switch (bd)
                {
                    case JxrBitDepth.Bd16F: return BitConverter.HalfToUInt16Bits((Half)f);
                    case JxrBitDepth.Bd32F: return BitConverter.SingleToInt32Bits(f);
                    case JxrBitDepth.Bd16S: return (int)Math.Clamp(Math.Round(f * 8192.0), -32768, 32767);
                    case JxrBitDepth.Bd32S: return (int)Math.Clamp(Math.Round(f * 16777216.0), int.MinValue, int.MaxValue);
                    case JxrBitDepth.Bd8: return (int)Math.Clamp(Math.Round(f * 255.0), 0, 255);
                    case JxrBitDepth.Bd16: return (int)Math.Clamp(Math.Round(f * 65535.0), 0, 65535);
                    case JxrBitDepth.Bd5: return (int)Math.Clamp(Math.Round(f * 31.0), 0, 31);
                    case JxrBitDepth.Bd565: return (int)Math.Clamp(Math.Round(f * 63.0), 0, 63);
                    case JxrBitDepth.Bd10: return (int)Math.Clamp(Math.Round(f * 1023.0), 0, 1023);
                    default: return f >= 0.5f ? 1 : 0;
                }
            }
        }

        // ------------------------------------------------------------------ pre-scaling (D.2) and colour conversion (D.3)

        /// <summary>The internal value of a reference output value of a component (bias removed, scaled).</summary>
        static int PreScale(int v, JxrBitDepth bd, JxrPlaneHeader ph, int bias, int scale)
        {
            switch (bd)
            {
                case JxrBitDepth.Bd16: return ((v >> ph.ShiftBits) - bias) << scale;
                case JxrBitDepth.Bd16S:
                case JxrBitDepth.Bd32S: return (v >> ph.ShiftBits) << scale;
                case JxrBitDepth.Bd16F:
                    {
                        int m = v & 0x7FFF;
                        return ((v & 0x8000) != 0 ? -m : m) << scale;
                    }
                case JxrBitDepth.Bd32F: return PreScale32F(v, ph.LenMantissa, ph.ExpBias) << scale;
                default: return (v - bias) << scale;
            }
        }

        /// <summary>PreScalingBD32F( ) (Table D.3).</summary>
        static int PreScale32F(int fv, int lenMantissa, int expBias)
        {
            if ((fv & 0x7FFFFFFF) == 0) return 0;
            int s = (fv >> 31) & 1;
            int e = (fv >> 23) & 0xFF;
            int m = (fv & 0x7FFFFF) | 0x800000;
            if (e == 0) { m ^= 0x800000; e++; }
            if (e == 0xFF) { e = 0xFE; m = 0xFFFFFF; }   // infinities and NaNs: the largest finite value
            e = e - 127 + expBias;
            if (e <= 1)
            {
                if (e < 1) m = (1 - e) > 31 ? 0 : m >> (1 - e);
                e = 1;
                if ((m & 0x800000) == 0) e = 0;
            }
            long mm = ((m & 0x7FFFFF) + (1L << (23 - lenMantissa - 1))) >> (23 - lenMantissa);
            long x = ((long)e << lenMantissa) + mm;   // a carry of the rounding moves into the exponent
            long lim = (1L << 30) - 1;
            if (x > lim) x = lim;
            return (int)(s != 0 ? -x : x);
        }

        /// <summary>RGBEForwardConversion( ) (Table D.5).</summary>
        static int RgbeForward(int v, int e)
        {
            if (e == 0) return 0;
            int append = 1;
            e--;
            while ((v & 0x80) == 0 && e > 0)
            {
                v = (v << 1) + append;
                append = 0;
                e--;
            }
            if (e == 0) return v;
            e++;
            return (v & 0x7F) + (e << 7);
        }

        // ------------------------------------------------------------------ encoding

        public static byte[] Encode(RawImage img, JpegXrEncodeOptions o)
        {
            var t = Target(img, o);
            var pf = JxrPixelFormat.Get(t.Name);
            int w = img.Width, h = img.Height;
            if (w < 1 || h < 1) throw new ArgumentException("JPEG XR: empty image");
            if (t.Of is JxrOutputFormat.Yuv420 && (w % 2 != 0 || h % 2 != 0) || t.Of is JxrOutputFormat.Yuv422 && w % 2 != 0)
                throw new ArgumentException("JPEG XR: subsampled YCC formats need even dimensions");
            if (t.Premultiplied && t.Alpha) img = Premultiply(img);
            bool lossless = o.Quality >= 1;
            var src = new Source(img, t);

            var ih = MakeImageHeader(w, h, t, o, lossless);
            var primaryPh = MakePlaneHeader(t, ih, o, lossless, false, o.Quality);
            JxrPlaneHeader? alphaPh = null;
            bool interleaved = t.Alpha && o.AlphaMode == JpegXrAlphaMode.Interleaved;
            double aq = o.AlphaQuality ?? 1.0;
            if (t.Alpha) alphaPh = MakePlaneHeader(t, ih, o, aq >= 1, true, aq);
            ih.AlphaPlane = interleaved;
            ih.Premultiplied = interleaved && t.Premultiplied;

            var primary = new JxrPlaneEncoder(ih, primaryPh);
            Fill(primary, src, t, false, o.Multithreaded);
            var planes = new List<JxrPlaneEncoder> { primary };
            JxrPlaneEncoder? alpha = null;
            if (t.Alpha)
            {
                var aih = interleaved ? ih : MakeImageHeader(w, h, t, o, aq >= 1);
                if (!interleaved) { aih.OutputFormat = JxrOutputFormat.YOnly; aih.Premultiplied = false; aih.AlphaPlane = false; }
                alpha = new JxrPlaneEncoder(aih, alphaPh!);
                Fill(alpha, src, t, true, o.Multithreaded);
                if (interleaved) planes.Add(alpha);
            }
            bool parallel = o.Multithreaded;
            foreach (var p in planes) p.Forward(parallel);
            var main = WriteCodestream(ih, planes, o, parallel);
            byte[]? alphaCs = null;
            if (alpha != null && !interleaved)
            {
                alpha.Forward(parallel);
                alphaCs = WriteCodestream(alpha.Ih, new List<JxrPlaneEncoder> { alpha }, o, parallel);
            }
            return Container(pf, w, h, main, alphaCs, img.Metadata, o, primaryPh.Bands);
        }

        static RawImage Premultiply(RawImage img)
        {
            var c = new RawImage(img.Width, img.Height, img.Color, (Array)img.Data.Clone(), img.Channels, img.Metadata);
            int ch = c.Channels;
            long n = (long)c.Width * c.Height;
            switch (c.SampleType)
            {
                case SampleType.UInt8:
                    {
                        var d = (byte[])c.Data;
                        for (long i = 0; i < n; i++)
                        {
                            int a = d[i * ch + ch - 1];
                            for (int k = 0; k < ch - 1; k++) d[i * ch + k] = (byte)((d[i * ch + k] * a + 127) / 255);
                        }
                        break;
                    }
                case SampleType.UInt16:
                    {
                        var d = (ushort[])c.Data;
                        for (long i = 0; i < n; i++)
                        {
                            long a = d[i * ch + ch - 1];
                            for (int k = 0; k < ch - 1; k++) d[i * ch + k] = (ushort)((d[i * ch + k] * a + 32767) / 65535);
                        }
                        break;
                    }
                case SampleType.Float32:
                    {
                        var d = (float[])c.Data;
                        for (long i = 0; i < n; i++)
                        {
                            float a = d[i * ch + ch - 1];
                            for (int k = 0; k < ch - 1; k++) d[i * ch + k] *= a;
                        }
                        break;
                    }
                default: throw new ArgumentException("JPEG XR: premultiplied formats need UInt8, UInt16 or Float32 samples");
            }
            return c;
        }

        static JxrImageHeader MakeImageHeader(int w, int h, JxrTarget t, JpegXrEncodeOptions o, bool lossless)
        {
            var ih = new JxrImageHeader
            {
                Width = w,
                Height = h,
                OutputFormat = t.Of,
                BitDepth = t.Bd,
                LongWord = true,
                Frequency = o.FrequencyOrder,
                HardTiling = o.HardTiling,
                TrimFlexbits = o.TrimFlexbits > 0,
                Overlap = o.Overlap ?? (lossless || o.Quality >= 0.5 ? 1 : 2),
            };
            // windowing (8.3.27 .. 8.3.30): top / left margins only on request (tests), the right / bottom ones pad to 16
            ih.TopMargin = o.TopMargin;
            ih.LeftMargin = o.LeftMargin;
            ih.Windowing = o.TopMargin > 0 || o.LeftMargin > 0;
            ih.RightMargin = (16 - (w + ih.LeftMargin) % 16) % 16;
            ih.BottomMargin = (16 - (h + ih.TopMargin) % 16) % 16;
            ih.ExtWidth = w + ih.LeftMargin + ih.RightMargin;
            ih.ExtHeight = h + ih.TopMargin + ih.BottomMargin;
            ih.MbWidth = ih.ExtWidth / 16;
            ih.MbHeight = ih.ExtHeight / 16;
            int tw = o.TileWidth > 0 ? Math.Max(1, (o.TileWidth + 15) / 16) : ih.MbWidth;
            int th = o.TileHeight > 0 ? Math.Max(1, (o.TileHeight + 15) / 16) : ih.MbHeight;
            int cols = Math.Min(4096, (ih.MbWidth + tw - 1) / tw), rows = Math.Min(4096, (ih.MbHeight + th - 1) / th);
            ih.TileCols = cols;
            ih.TileRows = rows;
            ih.Tiling = cols > 1 || rows > 1;
            ih.TileLeft = new int[cols + 1];
            for (int i = 0; i < cols; i++) ih.TileLeft[i] = i * tw;
            ih.TileLeft[cols] = ih.MbWidth;
            ih.TileTop = new int[rows + 1];
            for (int i = 0; i < rows; i++) ih.TileTop[i] = i * th;
            ih.TileTop[rows] = ih.MbHeight;
            ih.IndexTable = ih.Frequency || ih.Tiling;
            ih.ShortHeader = w <= 65536 && h <= 65536 && tw <= 255 && th <= 255;
            return ih;
        }

        /// <summary>Luma QP for a quality in (0, 1): 0 at quality 1, growing to 80 at quality 0.</summary>
        static int LumaQp(double q) => (int)Math.Clamp(Math.Round(80 * (1 - q)), 1, 255);

        static JxrPlaneHeader MakePlaneHeader(JxrTarget t, JxrImageHeader ih, JpegXrEncodeOptions o, bool lossless, bool alpha, double quality)
        {
            var ph = new JxrPlaneHeader();
            JxrInternalFormat fmt;
            if (alpha) fmt = JxrInternalFormat.YOnly;
            else
                fmt = t.Of switch
                {
                    JxrOutputFormat.YOnly => JxrInternalFormat.YOnly,
                    JxrOutputFormat.Cmyk or JxrOutputFormat.CmykDirect => JxrInternalFormat.Yuvk,
                    JxrOutputFormat.NComponent => JxrInternalFormat.NComponent,
                    JxrOutputFormat.Yuv420 => JxrInternalFormat.Yuv420,
                    JxrOutputFormat.Yuv422 => JxrInternalFormat.Yuv422,
                    JxrOutputFormat.Yuv444 => JxrInternalFormat.Yuv444,
                    _ => SubsampledRgb(t, o, lossless),
                };
            ph.Format = fmt;
            ph.NumComponents = fmt switch
            {
                JxrInternalFormat.YOnly => 1,
                JxrInternalFormat.Yuvk => 4,
                JxrInternalFormat.NComponent => t.Color,
                _ => 3,
            };
            ph.Scaled = !lossless && t.Bd is not (JxrBitDepth.Bd1White1 or JxrBitDepth.Bd1Black1);
            ph.Bands = o.Bands switch
            {
                JpegXrBands.NoFlexbits => JxrBands.NoFlexbits,
                JpegXrBands.NoHighpass => JxrBands.NoHighpass,
                JpegXrBands.DcOnly => JxrBands.DcOnly,
                _ => JxrBands.All,
            };
            if (t.Bd == JxrBitDepth.Bd32S) ph.ShiftBits = 10;     // s7.24 into 22 bits: lossy, as in every 32 bit format
            if (t.Bd == JxrBitDepth.Bd32F) { ph.LenMantissa = 13; ph.ExpBias = 4; }
            int nc = ph.NumComponents;
            byte[] Q(int luma, int chroma1, int chroma2)
            {
                var q = new byte[nc];
                for (int c = 0; c < nc; c++) q[c] = (byte)Math.Clamp(c == 0 ? luma : c == 1 ? chroma1 : chroma2, 0, 255);
                return q;
            }
            ph.DcUniform = ph.LpUniform = ph.HpUniform = true;
            if (lossless)
            {
                ph.DcQp = ph.LpQp = ph.HpQp = Q(0, 0, 0);
            }
            else
            {
                int y = o.Qp > 0 && !alpha ? o.Qp : LumaQp(quality);
                bool yuv = fmt is JxrInternalFormat.Yuv444 or JxrInternalFormat.Yuv422 or JxrInternalFormat.Yuv420 && t.Of is JxrOutputFormat.Rgb or JxrOutputFormat.Rgbe;
                int du = 0, dv = 0;
                if (yuv && o.Qp == 0)
                    (du, dv) = fmt switch { JxrInternalFormat.Yuv444 => (12, 16), JxrInternalFormat.Yuv422 => (4, 6), _ => (2, 3) };
                ph.DcQp = Q(y, y + du, y + dv);
                ph.LpQp = Q(y, y + du, y + dv);
                int hy = o.Qp > 0 ? y : y + 2;
                ph.HpQp = Q(hy, hy + du, hy + dv);
            }
            return ph;
        }

        static JxrInternalFormat SubsampledRgb(JxrTarget t, JpegXrEncodeOptions o, bool lossless)
        {
            var s = o.Subsampling ?? (lossless || o.Quality >= 0.5 ? JpegXrSubsampling.Yuv444 : JpegXrSubsampling.Yuv420);
            if (t.Of == JxrOutputFormat.Rgbe || t.Bd is JxrBitDepth.Bd16F or JxrBitDepth.Bd32F) s = JpegXrSubsampling.Yuv444;   // Table 179
            if (o.Subsampling == JpegXrSubsampling.YOnly) return JxrInternalFormat.YOnly;
            return s switch { JpegXrSubsampling.Yuv420 => JxrInternalFormat.Yuv420, JpegXrSubsampling.Yuv422 => JxrInternalFormat.Yuv422, _ => JxrInternalFormat.Yuv444 };
        }

        /// <summary>The internal sample values of a plane, padded by replicating the last column and row (D.3.5).</summary>
        static void Fill(JxrPlaneEncoder p, Source src, JxrTarget t, bool alphaPlane, bool parallel)
        {
            var ih = p.Ih;
            var ph = p.Ph;
            var bd = ih.BitDepth;
            int W = src.W, H = src.H, ew = ih.ExtWidth, eh = ih.ExtHeight;
            int scale = ph.Scaled ? 3 : 0;
            int bias = bd switch
            {
                JxrBitDepth.Bd5 => 1 << 4,
                JxrBitDepth.Bd565 => 1 << 5,
                JxrBitDepth.Bd8 => 1 << 7,
                JxrBitDepth.Bd10 => 1 << 9,
                JxrBitDepth.Bd16 => 1 << 15,
                _ => 0,
            };
            if (bd is JxrBitDepth.Bd16 or JxrBitDepth.Bd16S or JxrBitDepth.Bd32S) bias >>= ph.ShiftBits;
            var fmt = p.Fmt;
            var of = alphaPlane ? JxrOutputFormat.YOnly : t.Of;
            int nc = alphaPlane ? 1 : t.Color;
            int ncInt = p.Nc;
            int top = ih.TopMargin, left = ih.LeftMargin;
            // full resolution internal components (42x chroma is subsampled afterwards)
            var full = new int[ncInt][];
            for (int c = 0; c < ncInt; c++) full[c] = p.Is42x && c > 0 ? new int[ew * eh] : p.Px[c];
            if (of == JxrOutputFormat.Rgbe) FillRgbe(src, full, ew, scale, top, left);
            else
            {
                void Row(int y)
                {
                    var rows = new int[nc][];
                    for (int c = 0; c < nc; c++)
                    {
                        rows[c] = new int[W];
                        src.Row(y, alphaPlane ? t.Color : c, rows[c]);
                    }
                    int yo = (y + top) * ew + left;
                    for (int x = 0; x < W; x++)
                    {
                        int o = yo + x;
                        switch (of)
                        {
                            case JxrOutputFormat.YOnly:
                                full[0][o] = PreScale(rows[0][x], bd, ph, bias, scale);
                                break;
                            case JxrOutputFormat.Rgb:
                                {
                                    int r, g, b;
                                    // packed formats with RED_BLUE_NOT_SWAPPED_FLAG 0: the internal R is blue (the least
                                    // significant field), the internal B red
                                    bool swap = bd is JxrBitDepth.Bd5 or JxrBitDepth.Bd565 or JxrBitDepth.Bd10;
                                    int ri = swap ? 2 : 0, bi = swap ? 0 : 2;
                                    if (bd == JxrBitDepth.Bd565)
                                    {
                                        // R and B are 5 bit fields in the 6 bit domain of G (ComputeScaling shifts them once more)
                                        r = ((rows[ri][x] << 1) - bias) << scale;
                                        g = (rows[1][x] - bias) << scale;
                                        b = ((rows[bi][x] << 1) - bias) << scale;
                                    }
                                    else
                                    {
                                        r = PreScale(rows[ri][x], bd, ph, bias, scale);
                                        g = PreScale(rows[1][x], bd, ph, bias, scale);
                                        b = PreScale(rows[bi][x], bd, ph, bias, scale);
                                    }
                                    // FwdColorFmtConvert1( ) (Table D.6); YONLY keeps its luma only
                                    int V = b - r;
                                    int tt = r - g + ((V + 1) >> 1);
                                    full[0][o] = g + (tt >> 1);
                                    if (fmt != JxrInternalFormat.YOnly)
                                    {
                                        full[1][o] = -tt;
                                        full[2][o] = V;
                                    }
                                    break;
                                }
                            case JxrOutputFormat.Cmyk:
                                {
                                    int bs = bias >> 1;
                                    int c = (rows[0][x] - bs) << scale, m = (rows[1][x] - bs) << scale, yy = (rows[2][x] - bs) << scale, k = (rows[3][x] + bs) << scale;
                                    // FwdColorFmtConvert2( ) (Table D.7; its comments have the signs that invert Table 186)
                                    int V = c - yy;
                                    int U = c - m - (V >> 1);
                                    int Y = k - m - (U >> 1);
                                    full[0][o] = Y;
                                    full[1][o] = U;
                                    full[2][o] = V;
                                    full[3][o] = k - (Y >> 1);
                                    break;
                                }
                            case JxrOutputFormat.CmykDirect:
                                full[0][o] = PreScale(rows[3][x], bd, ph, bias, scale);
                                full[1][o] = PreScale(rows[0][x], bd, ph, bias, scale);
                                full[2][o] = PreScale(rows[1][x], bd, ph, bias, scale);
                                full[3][o] = PreScale(rows[2][x], bd, ph, bias, scale);
                                break;
                            default:   // NCOMPONENT, YUV444 / 422 / 420 (Y, U, V as given)
                                for (int c = 0; c < nc; c++) full[c][o] = PreScale(rows[c][x], bd, ph, bias, scale);
                                break;
                        }
                    }
                    PadRow(full, ncInt, yo - left, left, W, ew);
                }
                if (parallel && H >= 256) Parallel.For(0, H, Row);
                else for (int y = 0; y < H; y++) Row(y);
            }
            for (int c = 0; c < ncInt; c++)
            {
                var a = full[c];
                for (int y = 0; y < top; y++) Array.Copy(a, top * ew, a, y * ew, ew);
                for (int y = top + H; y < eh; y++) Array.Copy(a, (top + H - 1) * ew, a, y * ew, ew);
            }
            if (p.Is42x)
                for (int c = 1; c < 3; c++) Downsample(full[c], ew, eh, p.Px[c], p.PxW[c], p.PxH[c], p.Is420);
        }

        /// <summary>Replicates the first and last sample of a row into the left and right margins.</summary>
        static void PadRow(int[][] full, int n, int rowStart, int left, int w, int ew)
        {
            for (int c = 0; c < n; c++)
            {
                var a = full[c];
                int first = a[rowStart + left], last = a[rowStart + left + w - 1];
                for (int x = 0; x < left; x++) a[rowStart + x] = first;
                for (int x = left + w; x < ew; x++) a[rowStart + x] = last;
            }
        }

        static void FillRgbe(Source src, int[][] full, int ew, int scale, int top, int left)
        {
            Span<int> r = stackalloc int[src.W], g = stackalloc int[src.W], b = stackalloc int[src.W], e = stackalloc int[src.W];
            for (int y = 0; y < src.H; y++)
            {
                src.Row(y, 0, r);
                src.Row(y, 1, g);
                src.Row(y, 2, b);
                int yo = (y + top) * ew + left;
                for (int x = 0; x < src.W; x++)
                {
                    // the reference output of RGBE is R, G, B and E bytes; Source gives the float bits of each channel
                    float fr = BitConverter.Int32BitsToSingle(r[x]), fg = BitConverter.Int32BitsToSingle(g[x]), fb = BitConverter.Int32BitsToSingle(b[x]);
                    var (mr, mg, mb, ex) = ToRgbe(fr, fg, fb);
                    int R = RgbeForward(mr, ex) << scale, G = RgbeForward(mg, ex) << scale, B = RgbeForward(mb, ex) << scale;
                    int V = B - R;
                    int tt = R - G + ((V + 1) >> 1);
                    full[0][yo + x] = G + (tt >> 1);
                    full[1][yo + x] = -tt;
                    full[2][yo + x] = V;
                }
                PadRow(full, 3, yo - left, left, src.W, ew);
            }
        }

        /// <summary>Radiance RGBE of three floats (mantissas m * 2^(e - 136)).</summary>
        static (int, int, int, int) ToRgbe(float r, float g, float b)
        {
            r = Math.Max(r, 0); g = Math.Max(g, 0); b = Math.Max(b, 0);
            float m = Math.Max(r, Math.Max(g, b));
            if (!(m >= 1e-32f)) return (0, 0, 0, 0);
            int e = Math.ILogB(m) + 1;
            if (e > 127) { e = 127; r = Math.Min(r, 1.7e38f); g = Math.Min(g, 1.7e38f); b = Math.Min(b, 1.7e38f); }
            double s = Math.ScaleB(256.0, -e);
            return ((int)Math.Min(255, r * s), (int)Math.Min(255, g * s), (int)Math.Min(255, b * s), e + 128);
        }

        /// <summary>Co-sited chroma downsampling ([1 2 1] / 4), the counterpart of Upsample( ) with CHROMA_CENTERING 0.</summary>
        static void Downsample(int[] s, int sw, int sh, int[] d, int dw, int dh, bool vertical)
        {
            int[] h = new int[dw * sh];
            for (int y = 0; y < sh; y++)
            {
                int r = y * sw;
                for (int x = 0; x < dw; x++)
                {
                    int c = 2 * x;
                    int a = s[r + Math.Max(c - 1, 0)], b = s[r + c], e = s[r + Math.Min(c + 1, sw - 1)];
                    h[y * dw + x] = (a + 2 * b + e + 2) >> 2;
                }
            }
            if (!vertical) { Array.Copy(h, d, dw * dh); return; }
            for (int y = 0; y < dh; y++)
            {
                int c = 2 * y;
                int ra = Math.Max(c - 1, 0) * dw, rb = c * dw, re = Math.Min(c + 1, sh - 1) * dw;
                for (int x = 0; x < dw; x++) d[y * dw + x] = (h[ra + x] + 2 * h[rb + x] + h[re + x] + 2) >> 2;
            }
        }

        // ------------------------------------------------------------------ codestream

        static void WriteImageHeader(JxrBitWriter w, JxrImageHeader ih)
        {
            w.WriteBytes("WMPHOTO\0"u8);
            w.Bits(1, 4);
            w.Bit(ih.HardTiling);
            w.Bits(1, 3);
            w.Bit(ih.Tiling);
            w.Bit(ih.Frequency);
            w.Bits(ih.SpatialXfrm, 3);
            w.Bit(ih.IndexTable);
            w.Bits(ih.Overlap, 2);
            w.Bit(ih.ShortHeader);
            w.Bit(ih.LongWord);
            w.Bit(ih.Windowing);
            w.Bit(ih.TrimFlexbits);
            w.Bit(false);
            w.Bit(ih.RedBlueNotSwapped);
            w.Bit(ih.Premultiplied);
            w.Bit(ih.AlphaPlane);
            w.Bits((int)ih.OutputFormat, 4);
            w.Bits((int)ih.BitDepth, 4);
            int n = ih.ShortHeader ? 16 : 32;
            w.Bits((uint)(ih.Width - 1), n);
            w.Bits((uint)(ih.Height - 1), n);
            if (ih.Tiling)
            {
                w.Bits(ih.TileCols - 1, 12);
                w.Bits(ih.TileRows - 1, 12);
            }
            for (int i = 0; i < ih.TileCols - 1; i++) w.Bits(ih.TileLeft[i + 1] - ih.TileLeft[i], ih.ShortHeader ? 8 : 16);
            for (int i = 0; i < ih.TileRows - 1; i++) w.Bits(ih.TileTop[i + 1] - ih.TileTop[i], ih.ShortHeader ? 8 : 16);
            if (ih.Windowing)
            {
                w.Bits(ih.TopMargin, 6);
                w.Bits(ih.LeftMargin, 6);
                w.Bits(ih.BottomMargin, 6);
                w.Bits(ih.RightMargin, 6);
            }
        }

        static void WritePlaneHeader(JxrBitWriter w, JxrPlaneHeader ph, JxrImageHeader ih)
        {
            w.Bits((int)ph.Format, 3);
            w.Bit(ph.Scaled);
            w.Bits((int)ph.Bands, 4);
            switch (ph.Format)
            {
                case JxrInternalFormat.Yuv444: w.Bits(0, 4); w.Bits(0, 4); break;
                case JxrInternalFormat.Yuv422: w.Bit(false); w.Bits(ph.ChromaCenteringX, 3); w.Bits(0, 4); break;
                case JxrInternalFormat.Yuv420: w.Bit(false); w.Bits(ph.ChromaCenteringX, 3); w.Bit(false); w.Bits(ph.ChromaCenteringY, 3); break;
                case JxrInternalFormat.NComponent:
                    if (ph.NumComponents >= 16) { w.Bits(15, 4); w.Bits(ph.NumComponents - 16, 12); }
                    else { w.Bits(ph.NumComponents - 1, 4); w.Bits(0, 4); }
                    break;
            }
            if (ih.BitDepth is JxrBitDepth.Bd16 or JxrBitDepth.Bd16S or JxrBitDepth.Bd32S) w.Bits(ph.ShiftBits, 8);
            if (ih.BitDepth == JxrBitDepth.Bd32F) { w.Bits(ph.LenMantissa, 8); w.Bits(ph.ExpBias & 0xFF, 8); }
            w.Bit(true);
            JxrQp.Write(w, ph.DcQp!);
            if (ph.Bands != JxrBands.DcOnly)
            {
                w.Bit(false);
                w.Bit(true);
                JxrQp.Write(w, ph.LpQp!);
                if (ph.Bands != JxrBands.NoHighpass)
                {
                    w.Bit(false);
                    w.Bit(true);
                    JxrQp.Write(w, ph.HpQp!);
                }
            }
            w.AlignByte();
        }

        static byte[] WriteCodestream(JxrImageHeader ih, List<JxrPlaneEncoder> planes, JpegXrEncodeOptions o, bool parallel)
        {
            int tiles = ih.TileCols * ih.TileRows;
            int bands = ih.Frequency ? planes[0].Ph.NumBands : 1;
            var packets = new byte[tiles * bands][];
            int trim = Math.Clamp(o.TrimFlexbits, 0, 15);
            void Tile(int t)
            {
                int tx = t % ih.TileCols, ty = t / ih.TileCols;
                int l = ih.TileLeft[tx], r = ih.TileLeft[tx + 1], tp = ih.TileTop[ty], bt = ih.TileTop[ty + 1];
                var coders = planes.ConvertAll(p => new JxrTileCoder(p));
                bool Hp(JxrPlaneEncoder p) => p.Ph.Bands is JxrBands.All or JxrBands.NoFlexbits;
                JxrBitWriter Start(int band)
                {
                    var w = new JxrBitWriter();
                    w.Bits(1, 24);
                    w.Byte((t << 3) | band);
                    return w;
                }
                if (!ih.Frequency)
                {
                    var w = Start(0);
                    if (ih.TrimFlexbits) w.Bits(trim, 4);
                    for (int y = tp; y < bt; y++)
                        for (int x = l; x < r; x++)
                            for (int k = 0; k < planes.Count; k++)
                            {
                                var p = planes[k];
                                var c = coders[k];
                                c.SetMacroblock(x, y, l, r, tp);
                                c.PrepareDcLp();
                                c.WriteMbDc(w);
                                if (p.Ph.Bands != JxrBands.DcOnly) c.WriteMbLp(w);
                                if (Hp(p))
                                {
                                    c.PrepareHp();
                                    c.WriteMbCbphp(w);
                                    c.WriteMbHp(w, p.Ph.Bands == JxrBands.All, trim);
                                }
                            }
                    w.AlignByte();
                    packets[t] = w.Span.ToArray();
                    return;
                }
                // frequency order: the coefficients of the whole tile first, then one packet per band
                for (int y = tp; y < bt; y++)
                    for (int x = l; x < r; x++)
                        for (int k = 0; k < planes.Count; k++)
                        {
                            var c = coders[k];
                            c.SetMacroblock(x, y, l, r, tp);
                            c.PrepareDcLp();
                            if (Hp(planes[k])) c.PrepareHp();
                        }
                for (int band = 0; band < bands; band++)
                {
                    var w = Start(band);
                    if (band == 3 && ih.TrimFlexbits) w.Bits(trim, 4);
                    for (int y = tp; y < bt; y++)
                        for (int x = l; x < r; x++)
                            for (int k = 0; k < planes.Count; k++)
                            {
                                var p = planes[k];
                                var c = coders[k];
                                c.SetMacroblock(x, y, l, r, tp);
                                switch (band)
                                {
                                    case 0: c.WriteMbDc(w); break;
                                    case 1: if (p.Ph.Bands != JxrBands.DcOnly) c.WriteMbLp(w); break;
                                    case 2: if (Hp(p)) { c.WriteMbCbphp(w); c.WriteMbHp(w, false, trim); } break;
                                    case 3: if (p.Ph.Bands == JxrBands.All) c.WriteMbFlexbits(w, trim); break;
                                }
                            }
                    w.AlignByte();
                    packets[t * bands + band] = w.Span.ToArray();
                }
            }
            if (parallel && tiles > 1) Parallel.For(0, tiles, Tile);
            else for (int t = 0; t < tiles; t++) Tile(t);

            var hw = new JxrBitWriter(1 << 12);
            WriteImageHeader(hw, ih);
            foreach (var p in planes) WritePlaneHeader(hw, p.Ph, ih);
            if (ih.IndexTable)
            {
                hw.Bits(1, 16);
                long off = 0;
                foreach (var pk in packets) { hw.VlwEsc(off); off += pk.Length; }
            }
            hw.VlwEsc(0);   // no PROFILE_LEVEL_INFO( )
            var head = hw.Span;
            long total = head.Length;
            foreach (var pk in packets) total += pk.Length;
            if (total > int.MaxValue - 1024) throw new ImageNotSupportedException("JPEG XR: codestream exceeds 2 GB");
            var res = new byte[total];
            head.CopyTo(res);
            int pos = head.Length;
            foreach (var pk in packets) { pk.CopyTo(res, pos); pos += pk.Length; }
            return res;
        }

        // ------------------------------------------------------------------ container (Annex A)

        static byte[] Container(JxrPixelFormat pf, int w, int h, byte[] image, byte[]? alpha, ImageMetadata? meta, JpegXrEncodeOptions o, JxrBands bands)
        {
            // the image file directory is a little endian TIFF directory (Annex A), with EXIF / GPS directories as children
            var root = new TiffIfdNode();
            var exif = meta?.Exif is { Length: > 0 } ex ? TiffExif.Parse(ex) : null;
            if (exif != null)
                foreach (var f in exif.Fields)
                    if (f.Tag is not (274 or 282 or 283 or 296)) root.Set(f);   // orientation and resolution have JPEG XR tags
            TiffField Bytes(int tag, int type, byte[] d) => new() { Tag = (ushort)tag, Type = (ushort)type, Count = d.Length, Data = d };
            TiffField U32(int tag, long v) => TiffField.Longs(tag, false, v);
            if (meta?.Xmp is { Length: > 0 } xmp) root.Set(Bytes(0x02BC, 1, ExifTags.XmpForWriting(meta)!));
            if (meta?.IccProfile is { Length: > 0 } icc) root.Set(Bytes(0x8773, 7, icc));
            root.Set(Bytes(0xBC01, 1, pf.Guid()));
            int orientation = meta?.Orientation ?? 1;
            if (orientation is >= 2 and <= 8) root.Set(U32(0xBC02, Xfrm(orientation)));
            root.Set(U32(0xBC80, w));
            root.Set(U32(0xBC81, h));
            double dx = meta?.DpiX > 0 ? meta.DpiX : 96, dy = meta?.DpiY > 0 ? meta.DpiY : 96;
            var rx = new byte[4]; BinaryPrimitives.WriteSingleLittleEndian(rx, (float)dx);
            var ry = new byte[4]; BinaryPrimitives.WriteSingleLittleEndian(ry, (float)dy);
            root.Set(new TiffField { Tag = 0xBC82, Type = 11, Count = 1, Data = rx });
            root.Set(new TiffField { Tag = 0xBC83, Type = 11, Count = 1, Data = ry });
            root.Set(U32(0xBCC0, 0));
            root.Set(U32(0xBCC1, image.Length));
            if (alpha != null)
            {
                root.Set(U32(0xBCC2, 0));
                root.Set(U32(0xBCC3, alpha.Length));
            }
            if (bands != JxrBands.All) root.Set(Bytes(0xBCC4, 1, new[] { (byte)bands }));
            long ifdSize = root.Size(false);   // does not depend on the offsets
            long imageOffset = 8 + ifdSize;
            long alphaOffset = imageOffset + image.Length + (image.Length & 1);
            long total = alpha != null ? alphaOffset + alpha.Length : imageOffset + image.Length;
            if (total > uint.MaxValue) throw new ImageNotSupportedException("JPEG XR: file exceeds 4 GB");
            root.Set(U32(0xBCC0, imageOffset));
            if (alpha != null) root.Set(U32(0xBCC2, alphaOffset));
            var dir = root.Serialize(8, 0, false, false);
            var res = new byte[total];
            res[0] = res[1] = (byte)'I';
            res[2] = 0xBC;
            res[3] = 1;
            BinaryPrimitives.WriteUInt32LittleEndian(res.AsSpan(4), 8);
            dir.CopyTo(res, 8);
            image.CopyTo(res, imageOffset);
            alpha?.CopyTo(res, alphaOffset);
            return res;
        }

        /// <summary>The SPATIAL_XFRM value of an EXIF / TIFF orientation (the inverse of the NOTE to 8.3.8).</summary>
        static int Xfrm(int orientation) => orientation switch { 4 => 1, 2 => 2, 3 => 3, 6 => 4, 7 => 5, 5 => 6, 8 => 7, _ => 0 };
    }
}
