using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using Aardvark.ImageCodecs.PnmImpl;

namespace Aardvark.ImageCodecs
{
    public sealed class PnmDecodeOptions
    {
        /// <summary>
        /// Rescale samples whose maximum value (maxval) is not 255 or 65535 to the full range of the result type (rounding to
        /// nearest), so that white is always 255 / 65535. On by default; when off the stored values are returned as they are.
        /// </summary>
        public bool ScaleToFullRange { get; set; } = true;

        /// <summary>Upper bound for the image in pixels; larger files are rejected with <see cref="ImageNotSupportedException"/>.</summary>
        public long MaxPixels { get; set; } = 1L << 28;

        internal static readonly PnmDecodeOptions Default = new();
    }

    public sealed class PnmEncodeOptions
    {
        /// <summary>Write the "plain" text variants (P1, P2, P3) instead of binary ones. PAM has no text variant.</summary>
        public bool Plain { get; set; }

        /// <summary>Write Gray images as bilevel PBM (P4 / P1): samples at or above half the range are white.</summary>
        public bool Bilevel { get; set; }

        /// <summary>
        /// Store images with alpha as PAM (P7, the default); when false the alpha channel is dropped and a PGM / PPM is written,
        /// which more programs read.
        /// </summary>
        public bool KeepAlpha { get; set; } = true;
    }

    /// <summary>
    /// Netpbm formats: PBM (P1, P4), PGM (P2, P5), PPM (P3, P6) and PAM (P7 with the BLACKANDWHITE, GRAYSCALE, RGB and _ALPHA
    /// tuple types, other tuple types as <see cref="ColorModel.Other"/>). Any maxval up to 65535: results are UInt8 for maxval
    /// &lt; 256 and UInt16 above, scaled to the full range unless <see cref="PnmDecodeOptions.ScaleToFullRange"/> is off; PBM gives
    /// Gray 0 / 255. Only the first image of a file holding several is read. Writes UInt8 / UInt16 Gray, Rgb, GrayAlpha and Rgba
    /// (the latter two as PAM) in binary or plain form, and bilevel PBM.
    /// <para>The float variant PFM is <see cref="Pfm"/>.</para>
    /// </summary>
    public static class Pnm
    {
        /// <summary>Whether the data starts with a Netpbm magic number P1 to P7 followed by white space.</summary>
        public static bool IsMatch(ReadOnlySpan<byte> header)
            => header.Length >= 3 && header[0] == 'P' && header[1] is >= (byte)'1' and <= (byte)'7' && PnmReader.IsSpace(header[2]);

        public static ImageInfo GetInfo(ReadOnlySpan<byte> data)
        {
            var h = PnmReader.ParseHeader(data);
            return new ImageInfo(ImageFormat.Pnm, h.Width, h.Height, h.Depth, h.MaxVal > 255 ? SampleType.UInt16 : SampleType.UInt8, h.Color)
            {
                BitsPerSample = h.MaxVal <= 1 ? 1 : 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)h.MaxVal),
                Transfer = h.Color == ColorModel.Other ? TransferFunction.Unknown : TransferFunction.Srgb,
            };
        }

        public static RawImage Decode(ReadOnlySpan<byte> data, PnmDecodeOptions? options = null)
            => PnmReader.Decode(data, options ?? PnmDecodeOptions.Default);

        public static void Encode(RawImage image, Stream output, PnmEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            ArgumentNullException.ThrowIfNull(output);
            PnmWriter.Encode(image, output, options ?? new PnmEncodeOptions());
        }

        public static byte[] Encode(RawImage image, PnmEncodeOptions? options = null)
        {
            var ms = new MemoryStream();
            Encode(image, ms, options);
            return ms.ToArray();
        }
    }

    public sealed class PfmEncodeOptions
    {
        /// <summary>Write big endian samples (positive scale) instead of the usual little endian ones.</summary>
        public bool BigEndian { get; set; }
    }

    /// <summary>
    /// Portable float map (PFM): "PF" (RGB) and "Pf" (gray) 32 bit float images, either byte order, rows stored bottom to top
    /// (returned top row first). The non-standard "PF4" (RGBA) is read too. Decodes to Float32 (linear light by convention);
    /// the magnitude of the scale factor is ignored. Writes Gray and Rgb (alpha is dropped); Float16 / Float64 samples are
    /// converted, UInt8 / UInt16 ones are converted to linear light first according to their <see cref="ImageMetadata.Transfer"/>.
    /// </summary>
    public static class Pfm
    {
        /// <summary>Whether the data starts with "PF", "Pf" or "PF4" followed by white space.</summary>
        public static bool IsMatch(ReadOnlySpan<byte> header)
            => header.Length >= 3 && header[0] == 'P' && (header[1] == 'F' || header[1] == 'f')
               && (PnmReader.IsSpace(header[2]) || header[1] == 'F' && header[2] == '4' && header.Length >= 4 && PnmReader.IsSpace(header[3]));

        public static ImageInfo GetInfo(ReadOnlySpan<byte> data)
        {
            var h = PfmCodec.ParseHeader(data);
            return new ImageInfo(ImageFormat.Pfm, h.Width, h.Height, h.Channels, SampleType.Float32, RawImage.DefaultColorModel(h.Channels))
            {
                BitsPerSample = 32, Transfer = TransferFunction.Linear,
            };
        }

        public static RawImage Decode(ReadOnlySpan<byte> data, PnmDecodeOptions? options = null)
            => PfmCodec.Decode(data, options ?? PnmDecodeOptions.Default);

        public static void Encode(RawImage image, Stream output, PfmEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            ArgumentNullException.ThrowIfNull(output);
            PfmCodec.Encode(image, output, options ?? new PfmEncodeOptions());
        }

        public static byte[] Encode(RawImage image, PfmEncodeOptions? options = null)
        {
            var ms = new MemoryStream();
            Encode(image, ms, options);
            return ms.ToArray();
        }
    }
}

namespace Aardvark.ImageCodecs.PnmImpl
{
    internal sealed class PnmHeader
    {
        public char Kind;          // '1'..'7'
        public int Width, Height, Depth, MaxVal;
        public ColorModel Color;
        public bool InvertBits;    // PBM: 1 is black
        public int DataStart;
    }

    internal static class PnmReader
    {
        public static bool IsSpace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or 0x0B or 0x0C;

        /// <summary>Skips white space and # comments.</summary>
        public static void SkipSpace(ReadOnlySpan<byte> d, ref int pos)
        {
            while (pos < d.Length)
            {
                if (IsSpace(d[pos])) pos++;
                else if (d[pos] == '#') { while (pos < d.Length && d[pos] != '\n' && d[pos] != '\r') pos++; }
                else break;
            }
        }

        /// <summary>Reads a decimal number; in a header (<paramref name="header"/>) it must be followed by something, or the data is truncated.</summary>
        public static int ReadInt(ReadOnlySpan<byte> d, ref int pos, bool header = true)
        {
            SkipSpace(d, ref pos);
            if (pos >= d.Length || d[pos] < '0' || d[pos] > '9') throw new ImageFormatException("PNM: number expected");
            long v = 0;
            while (pos < d.Length && d[pos] >= '0' && d[pos] <= '9')
            {
                v = v * 10 + (d[pos++] - '0');
                if (v > int.MaxValue) throw new ImageFormatException("PNM: number too large");
            }
            if (header && pos >= d.Length) throw new ImageFormatException("PNM: header ends early");
            return (int)v;
        }

        static string ReadToken(ReadOnlySpan<byte> d, ref int pos)
        {
            SkipSpace(d, ref pos);
            int s = pos;
            while (pos < d.Length && !IsSpace(d[pos])) pos++;
            return Encoding.ASCII.GetString(d.Slice(s, pos - s));
        }

        public static PnmHeader ParseHeader(ReadOnlySpan<byte> d)
        {
            if (!Pnm.IsMatch(d)) throw new ImageFormatException("PNM: not a Netpbm file");
            var h = new PnmHeader { Kind = (char)d[1] };
            int pos = 2;
            if (h.Kind == '7') ParsePam(d, ref pos, h);
            else
            {
                h.Width = ReadInt(d, ref pos);
                h.Height = ReadInt(d, ref pos);
                bool bits = h.Kind is '1' or '4';
                h.MaxVal = bits ? 1 : ReadInt(d, ref pos);
                h.Depth = h.Kind is '3' or '6' ? 3 : 1;
                h.Color = h.Depth == 3 ? ColorModel.Rgb : ColorModel.Gray;
                h.InvertBits = bits;
                // a single white space character separates the header from binary data
                if (h.Kind != '1' && h.Kind != '2' && h.Kind != '3')
                {
                    if (pos >= d.Length || !IsSpace(d[pos])) throw new ImageFormatException("PNM: header ends early");
                    pos++;
                }
            }
            if (h.Width <= 0 || h.Height <= 0) throw new ImageFormatException("PNM: bad dimensions");
            if (h.MaxVal < 1 || h.MaxVal > 65535) throw new ImageFormatException($"PNM: bad maxval {h.MaxVal}");
            h.DataStart = pos;
            return h;
        }

        static void ParsePam(ReadOnlySpan<byte> d, ref int pos, PnmHeader h)
        {
            string tuple = "";
            h.Width = h.Height = h.Depth = h.MaxVal = -1;
            while (true)
            {
                SkipSpace(d, ref pos);
                if (pos >= d.Length) throw new ImageFormatException("PAM: header ends early");
                var key = ReadToken(d, ref pos);
                switch (key)
                {
                    case "WIDTH": h.Width = ReadInt(d, ref pos); break;
                    case "HEIGHT": h.Height = ReadInt(d, ref pos); break;
                    case "DEPTH": h.Depth = ReadInt(d, ref pos); break;
                    case "MAXVAL": h.MaxVal = ReadInt(d, ref pos); break;
                    case "TUPLTYPE":
                        {
                            // the rest of the line, appended to earlier TUPLTYPE lines
                            while (pos < d.Length && (d[pos] == ' ' || d[pos] == '\t')) pos++;
                            int s = pos;
                            while (pos < d.Length && d[pos] != '\n' && d[pos] != '\r') pos++;
                            var t = Encoding.ASCII.GetString(d.Slice(s, pos - s)).Trim();
                            tuple = tuple.Length == 0 ? t : tuple + " " + t;
                            break;
                        }
                    case "ENDHDR":
                        while (pos < d.Length && d[pos] != '\n') pos++;
                        if (pos >= d.Length) throw new ImageFormatException("PAM: header ends early");
                        pos++;
                        goto done;
                    default: throw new ImageFormatException($"PAM: unknown header line '{key}'");
                }
            }
        done:
            if (h.Width <= 0 || h.Height <= 0 || h.Depth <= 0 || h.MaxVal <= 0) throw new ImageFormatException("PAM: incomplete header");
            if (h.Depth > 1024) throw new ImageNotSupportedException("PAM: depth above 1024");
            h.Color = (tuple, h.Depth) switch
            {
                ("BLACKANDWHITE" or "GRAYSCALE", 1) => ColorModel.Gray,
                ("BLACKANDWHITE_ALPHA" or "GRAYSCALE_ALPHA", 2) => ColorModel.GrayAlpha,
                ("RGB", 3) => ColorModel.Rgb,
                ("RGB_ALPHA", 4) => ColorModel.Rgba,
                ("", 1) => ColorModel.Gray,
                ("", 3) => ColorModel.Rgb,
                _ => ColorModel.Other,
            };
        }

        public static RawImage Decode(ReadOnlySpan<byte> d, PnmDecodeOptions opt)
        {
            var h = ParseHeader(d);
            long n = (long)h.Width * h.Height;
            if (n > opt.MaxPixels) throw new ImageNotSupportedException($"PNM: {h.Width}x{h.Height} exceeds MaxPixels");
            long count = n * h.Depth;
            if (count > Array.MaxLength) throw new ImageNotSupportedException("PNM: too large");
            bool wide = h.MaxVal > 255;
            int pos = h.DataStart;
            int[] vals;
            int max = h.MaxVal;
            switch (h.Kind)
            {
                case '1':
                    {
                        vals = new int[count];
                        for (long i = 0; i < count; i++)
                        {
                            SkipSpace(d, ref pos);
                            if (pos >= d.Length) break;   // truncated: the rest stays 0
                            if (d[pos] != '0' && d[pos] != '1') throw new ImageFormatException("PBM: bad bit");
                            vals[i] = d[pos++] == '1' ? 0 : 1;
                        }
                        break;
                    }
                case '2':
                case '3':
                    {
                        vals = new int[count];
                        for (long i = 0; i < count; i++)
                        {
                            SkipSpace(d, ref pos);
                            if (pos >= d.Length) break;
                            vals[i] = Math.Min(ReadInt(d, ref pos, false), max);
                        }
                        break;
                    }
                case '4':
                    {
                        vals = new int[count];
                        int rowBytes = (h.Width + 7) >> 3;
                        for (int y = 0; y < h.Height; y++)
                            for (int x = 0; x < h.Width; x++)
                            {
                                long o = pos + (long)y * rowBytes + (x >> 3);
                                if (o >= d.Length) goto end4;
                                vals[(long)y * h.Width + x] = (d[(int)o] >> (7 - (x & 7)) & 1) == 1 ? 0 : 1;
                            }
                        end4:
                        break;
                    }
                default: // 5, 6, 7: binary samples
                    {
                        int bps = wide ? 2 : 1;
                        long avail = Math.Max(0, Math.Min(count, (d.Length - pos) / bps));
                        if (!wide && max == 255 || wide && max == 65535 || !opt.ScaleToFullRange && h.Kind != '7')
                        {
                            // fast paths: values used as they are
                            if (!wide)
                            {
                                var b = new byte[count];
                                d.Slice(pos, (int)avail).CopyTo(b);
                                if (max < 255) for (long i = 0; i < avail; i++) if (b[i] > max) b[i] = (byte)max;
                                return Result(h, b, opt);
                            }
                            var u = new ushort[count];
                            for (long i = 0; i < avail; i++) u[i] = Math.Min(BinaryPrimitives.ReadUInt16BigEndian(d.Slice(pos + (int)(i * 2))), (ushort)max);
                            return Result(h, u, opt);
                        }
                        vals = new int[count];
                        for (long i = 0; i < avail; i++)
                            vals[i] = Math.Min(wide ? BinaryPrimitives.ReadUInt16BigEndian(d.Slice(pos + (int)(i * 2))) : d[pos + (int)i], max);
                        break;
                    }
            }
            // PAM BLACKANDWHITE: 0 black, 1 white (no inversion); plain and raw PBM were inverted above
            if (h.MaxVal == 1 || opt.ScaleToFullRange)
            {
                int full = wide ? 65535 : 255;
                if (!wide)
                {
                    var b = new byte[count];
                    for (long i = 0; i < count; i++) b[i] = (byte)((vals[i] * full + max / 2) / max);
                    return Result(h, b, opt);
                }
                var u = new ushort[count];
                for (long i = 0; i < count; i++) u[i] = (ushort)(((long)vals[i] * full + max / 2) / max);
                return Result(h, u, opt);
            }
            if (!wide)
            {
                var b = new byte[count];
                for (long i = 0; i < count; i++) b[i] = (byte)vals[i];
                return Result(h, b, opt);
            }
            var w = new ushort[count];
            for (long i = 0; i < count; i++) w[i] = (ushort)vals[i];
            return Result(h, w, opt);
        }

        static RawImage Result(PnmHeader h, Array data, PnmDecodeOptions opt)
            => new(h.Width, h.Height, h.Color, data, h.Depth, new ImageMetadata
            {
                Transfer = h.Color == ColorModel.Other ? TransferFunction.Unknown : TransferFunction.Srgb,
            });
    }

    internal static class PnmWriter
    {
        public static void Encode(RawImage img, Stream output, PnmEncodeOptions opt)
        {
            if (img.SampleType is not (SampleType.UInt8 or SampleType.UInt16))
                throw new ArgumentException($"PNM: cannot write {img.SampleType} samples (UInt8 or UInt16 needed)");
            bool wide = img.SampleType == SampleType.UInt16;
            int max = wide ? 65535 : 255;
            int w = img.Width, h = img.Height, ch = img.Channels;
            var sb = new StringBuilder();
            if (opt.Bilevel)
            {
                if (img.Color != ColorModel.Gray) throw new ArgumentException("PNM: bilevel output needs a Gray image");
                sb.Append(opt.Plain ? "P1\n" : "P4\n").Append(w).Append(' ').Append(h).Append('\n');
                output.Write(Encoding.ASCII.GetBytes(sb.ToString()));
                int half = (max + 1) / 2;
                if (opt.Plain)
                {
                    var line = new StringBuilder();
                    for (int y = 0; y < h; y++)
                    {
                        line.Clear();
                        for (int x = 0; x < w; x++)
                        {
                            line.Append(Sample(img, (long)y * w + x) >= half ? '0' : '1');
                            if ((x & 63) == 63 && x + 1 < w) line.Append('\n');
                        }
                        line.Append('\n');
                        output.Write(Encoding.ASCII.GetBytes(line.ToString()));
                    }
                    return;
                }
                var row = new byte[(w + 7) >> 3];
                for (int y = 0; y < h; y++)
                {
                    Array.Clear(row);
                    for (int x = 0; x < w; x++) if (Sample(img, (long)y * w + x) < half) row[x >> 3] |= (byte)(0x80 >> (x & 7));
                    output.Write(row);
                }
                return;
            }
            bool alpha = img.HasAlpha;
            bool pam = img.Color == ColorModel.Other || alpha && opt.KeepAlpha;
            int outCh = pam ? ch : alpha ? ch - 1 : ch;
            if (pam)
            {
                if (opt.Plain) throw new ArgumentException("PNM: PAM (alpha or Other images) has no plain form");
                string tuple = img.Color switch
                {
                    ColorModel.GrayAlpha => "GRAYSCALE_ALPHA",
                    ColorModel.Rgba => "RGB_ALPHA",
                    _ => "",
                };
                sb.Append("P7\nWIDTH ").Append(w).Append("\nHEIGHT ").Append(h).Append("\nDEPTH ").Append(ch).Append("\nMAXVAL ").Append(max).Append('\n');
                if (tuple.Length > 0) sb.Append("TUPLTYPE ").Append(tuple).Append('\n');
                sb.Append("ENDHDR\n");
            }
            else
            {
                bool rgb = img.Color is ColorModel.Rgb or ColorModel.Rgba;
                char kind = rgb ? (opt.Plain ? '3' : '6') : (opt.Plain ? '2' : '5');
                sb.Append('P').Append(kind).Append('\n').Append(w).Append(' ').Append(h).Append('\n').Append(max).Append('\n');
            }
            output.Write(Encoding.ASCII.GetBytes(sb.ToString()));
            if (opt.Plain)
            {
                var line = new StringBuilder();
                for (int y = 0; y < h; y++)
                {
                    line.Clear();
                    int col = 0;
                    for (int x = 0; x < w; x++)
                        for (int c = 0; c < outCh; c++)
                        {
                            var s = Sample(img, ((long)y * w + x) * ch + c).ToString(System.Globalization.CultureInfo.InvariantCulture);
                            if (col > 0 && col + s.Length + 1 > 70) { line.Append('\n'); col = 0; }
                            else if (col > 0) { line.Append(' '); col++; }
                            line.Append(s); col += s.Length;
                        }
                    line.Append('\n');
                    output.Write(Encoding.ASCII.GetBytes(line.ToString()));
                }
                return;
            }
            int bps = wide ? 2 : 1;
            var buf = new byte[w * outCh * bps];
            for (int y = 0; y < h; y++)
            {
                int k = 0;
                for (int x = 0; x < w; x++)
                    for (int c = 0; c < outCh; c++)
                    {
                        long i = ((long)y * w + x) * ch + c;
                        if (wide) { BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(k), ((ushort[])img.Data)[i]); k += 2; }
                        else buf[k++] = ((byte[])img.Data)[i];
                    }
                output.Write(buf);
            }
        }

        static int Sample(RawImage img, long i) => img.Data is byte[] b ? b[i] : ((ushort[])img.Data)[i];
    }

    internal static class PfmCodec
    {
        internal struct Header { public int Width, Height, Channels, DataStart; public bool LittleEndian; }

        public static Header ParseHeader(ReadOnlySpan<byte> d)
        {
            if (!Pfm.IsMatch(d)) throw new ImageFormatException("PFM: not a PFM file");
            var h = new Header { Channels = d[1] == 'f' ? 1 : d[2] == '4' ? 4 : 3 };
            int pos = h.Channels == 4 ? 3 : 2;
            h.Width = PnmReader.ReadInt(d, ref pos);
            h.Height = PnmReader.ReadInt(d, ref pos);
            PnmReader.SkipSpace(d, ref pos);
            int s = pos;
            while (pos < d.Length && !PnmReader.IsSpace(d[pos])) pos++;
            if (!double.TryParse(Encoding.ASCII.GetString(d.Slice(s, pos - s)), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var scale) || scale == 0 || !double.IsFinite(scale))
                throw new ImageFormatException("PFM: bad scale");
            if (pos >= d.Length) throw new ImageFormatException("PFM: header ends early");
            pos++; // the single white space before the data
            if (h.Width <= 0 || h.Height <= 0) throw new ImageFormatException("PFM: bad dimensions");
            h.LittleEndian = scale < 0;
            h.DataStart = pos;
            return h;
        }

        public static RawImage Decode(ReadOnlySpan<byte> d, PnmDecodeOptions opt)
        {
            var h = ParseHeader(d);
            if ((long)h.Width * h.Height > opt.MaxPixels) throw new ImageNotSupportedException($"PFM: {h.Width}x{h.Height} exceeds MaxPixels");
            long count = (long)h.Width * h.Height * h.Channels;
            if (count > Array.MaxLength) throw new ImageNotSupportedException("PFM: too large");
            var f = new float[count];
            int rowLen = h.Width * h.Channels;
            for (int y = 0; y < h.Height; y++)
            {
                long src = h.DataStart + (long)y * rowLen * 4;
                int dst = (h.Height - 1 - y) * rowLen;   // bottom row first
                for (int i = 0; i < rowLen; i++)
                {
                    long o = src + i * 4L;
                    if (o + 4 > d.Length) goto done;
                    var b = d.Slice((int)o, 4);
                    f[dst + i] = h.LittleEndian ? BinaryPrimitives.ReadSingleLittleEndian(b) : BinaryPrimitives.ReadSingleBigEndian(b);
                }
            }
        done:
            return new RawImage(h.Width, h.Height, RawImage.DefaultColorModel(h.Channels), f, h.Channels,
                new ImageMetadata { Transfer = TransferFunction.Linear });
        }

        public static void Encode(RawImage image, Stream output, PfmEncodeOptions opt)
        {
            if (image.Color == ColorModel.Other) throw new ArgumentException("PFM: needs a Gray or Rgb image");
            var src = image.SampleType switch
            {
                SampleType.Float32 => image,
                SampleType.Float16 or SampleType.Float64 => ImageOps.ToLinear(image, TransferFunction.Linear),
                SampleType.UInt8 or SampleType.UInt16 => ImageOps.ToLinear(image),
                _ => throw new ArgumentException($"PFM: cannot write {image.SampleType} samples"),
            };
            var f = src.GetData<float>();
            int w = image.Width, h = image.Height, ch = image.Channels;
            int outCh = image.Color is ColorModel.Gray or ColorModel.GrayAlpha ? 1 : 3;
            var head = $"{(outCh == 1 ? "Pf" : "PF")}\n{w} {h}\n{(opt.BigEndian ? "1.0" : "-1.0")}\n";
            output.Write(Encoding.ASCII.GetBytes(head));
            var row = new byte[w * outCh * 4];
            for (int y = h - 1; y >= 0; y--)
            {
                int k = 0;
                for (int x = 0; x < w; x++)
                    for (int c = 0; c < outCh; c++, k += 4)
                    {
                        float v = f[((long)y * w + x) * ch + c];
                        if (opt.BigEndian) BinaryPrimitives.WriteSingleBigEndian(row.AsSpan(k), v);
                        else BinaryPrimitives.WriteSingleLittleEndian(row.AsSpan(k), v);
                    }
                output.Write(row);
            }
        }
    }
}
