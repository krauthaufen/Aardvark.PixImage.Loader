using System;
using System.Buffers.Binary;
using System.IO;

namespace Aardvark.ImageCodecs
{
    public sealed class QoiDecodeOptions
    {
        /// <summary>Upper bound for the image in pixels; larger files are rejected with <see cref="ImageNotSupportedException"/>.</summary>
        public long MaxPixels { get; set; } = 1L << 28;

        internal static readonly QoiDecodeOptions Default = new();
    }

    public sealed class QoiEncodeOptions
    {
        /// <summary>
        /// Record the colour channels as linear (header colour space 1) instead of sRGB (0). Null (the default) takes it from the
        /// image's <see cref="ImageMetadata.Transfer"/>.
        /// </summary>
        public bool? Linear { get; set; }
    }

    /// <summary>
    /// The Quite OK Image format (QOI, qoiformat.org): lossless UInt8 Rgb / Rgba. Decodes to what the header declares (3 or 4
    /// channels); the header's colour space byte becomes <see cref="ImageInfo.Transfer"/>. Writes UInt8 Rgb and Rgba (Gray and
    /// GrayAlpha are expanded to them).
    /// </summary>
    public static class Qoi
    {
        const uint Magic = 0x716F6966; // "qoif"
        const int HeaderSize = 14;
        static ReadOnlySpan<byte> End => new byte[] { 0, 0, 0, 0, 0, 0, 0, 1 };

        public static bool IsMatch(ReadOnlySpan<byte> header)
            => header.Length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(header) == Magic;

        static (int w, int h, int ch, TransferFunction t) Header(ReadOnlySpan<byte> d)
        {
            if (d.Length < HeaderSize || !IsMatch(d)) throw new ImageFormatException("QOI: not a QOI file");
            uint w = BinaryPrimitives.ReadUInt32BigEndian(d.Slice(4)), h = BinaryPrimitives.ReadUInt32BigEndian(d.Slice(8));
            int ch = d[12];
            if (w == 0 || h == 0 || w > int.MaxValue || h > int.MaxValue) throw new ImageFormatException("QOI: bad dimensions");
            if (ch != 3 && ch != 4) throw new ImageFormatException($"QOI: bad channel count {ch}");
            return ((int)w, (int)h, ch, d[13] == 1 ? TransferFunction.Linear : TransferFunction.Srgb);
        }

        public static ImageInfo GetInfo(ReadOnlySpan<byte> data)
        {
            var (w, h, ch, t) = Header(data);
            return new ImageInfo(ImageFormat.Qoi, w, h, ch, SampleType.UInt8, ch == 4 ? ColorModel.Rgba : ColorModel.Rgb)
            {
                BitsPerSample = 8, Transfer = t,
            };
        }

        static int Hash(byte r, byte g, byte b, byte a) => (r * 3 + g * 5 + b * 7 + a * 11) & 63;

        /// <summary>Decodes a QOI file. Data that ends early leaves the remaining pixels at the last value.</summary>
        public static RawImage Decode(ReadOnlySpan<byte> data, QoiDecodeOptions? options = null)
        {
            options ??= QoiDecodeOptions.Default;
            var (w, h, ch, t) = Header(data);
            long n = (long)w * h;
            if (n > options.MaxPixels || n * ch > Array.MaxLength) throw new ImageNotSupportedException($"QOI: {w}x{h} exceeds MaxPixels");
            var o = new byte[n * ch];
            Span<byte> index = stackalloc byte[64 * 4];
            index.Clear();
            byte r = 0, g = 0, b = 0, a = 255;
            int pos = HeaderSize, run = 0;
            int end = data.Length;
            for (long p = 0; p < n; p++)
            {
                if (run > 0) run--;
                else if (pos < end)
                {
                    int b1 = data[pos++];
                    if (b1 == 0xFE) { if (pos + 3 > end) break; r = data[pos]; g = data[pos + 1]; b = data[pos + 2]; pos += 3; }
                    else if (b1 == 0xFF) { if (pos + 4 > end) break; r = data[pos]; g = data[pos + 1]; b = data[pos + 2]; a = data[pos + 3]; pos += 4; }
                    else
                    {
                        switch (b1 >> 6)
                        {
                            case 0:
                                {
                                    int k = b1 * 4;
                                    r = index[k]; g = index[k + 1]; b = index[k + 2]; a = index[k + 3];
                                    break;
                                }
                            case 1:
                                r += (byte)((b1 >> 4 & 3) - 2); g += (byte)((b1 >> 2 & 3) - 2); b += (byte)((b1 & 3) - 2);
                                break;
                            case 2:
                                {
                                    if (pos >= end) goto done;
                                    int b2 = data[pos++];
                                    int dg = (b1 & 0x3F) - 32;
                                    r += (byte)(dg - 8 + (b2 >> 4)); g += (byte)dg; b += (byte)(dg - 8 + (b2 & 15));
                                    break;
                                }
                            default:
                                run = b1 & 0x3F;
                                break;
                        }
                    }
                    int hk = Hash(r, g, b, a) * 4;
                    index[hk] = r; index[hk + 1] = g; index[hk + 2] = b; index[hk + 3] = a;
                }
                long q = p * ch;
                o[q] = r; o[q + 1] = g; o[q + 2] = b;
                if (ch == 4) o[q + 3] = a;
            }
        done:
            return new RawImage(w, h, ch == 4 ? ColorModel.Rgba : ColorModel.Rgb, o, ch, new ImageMetadata { Transfer = t });
        }

        public static void Encode(RawImage image, Stream output, QoiEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            ArgumentNullException.ThrowIfNull(output);
            output.Write(Encode(image, options));
        }

        public static byte[] Encode(RawImage image, QoiEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            if (image.SampleType != SampleType.UInt8) throw new ArgumentException($"QOI: cannot write {image.SampleType} samples (UInt8 needed)");
            if (image.Color == ColorModel.Other) throw new ArgumentException("QOI: needs a Gray, Rgb or Rgba image");
            int w = image.Width, h = image.Height, sch = image.Channels;
            bool alpha = image.HasAlpha;
            int ch = alpha ? 4 : 3;
            bool gray = image.Color is ColorModel.Gray or ColorModel.GrayAlpha;
            bool linear = options?.Linear ?? image.Metadata?.Transfer == TransferFunction.Linear;
            var src = (byte[])image.Data;
            long n = (long)w * h;
            long cap = HeaderSize + n * (ch + 1) + 8;
            if (cap > Array.MaxLength) throw new ImageNotSupportedException("QOI: image too large");
            var o = new byte[cap];
            BinaryPrimitives.WriteUInt32BigEndian(o, Magic);
            BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(4), (uint)w);
            BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(8), (uint)h);
            o[12] = (byte)ch; o[13] = (byte)(linear ? 1 : 0);
            int pos = HeaderSize;
            Span<byte> index = stackalloc byte[64 * 4];
            index.Clear();
            byte pr = 0, pg = 0, pb = 0, pa = 255;
            int run = 0;
            for (long p = 0; p < n; p++)
            {
                long s = p * sch;
                byte r, g, b, a;
                if (gray) { r = g = b = src[s]; a = alpha ? src[s + 1] : (byte)255; }
                else { r = src[s]; g = src[s + 1]; b = src[s + 2]; a = alpha ? src[s + 3] : (byte)255; }
                if (r == pr && g == pg && b == pb && a == pa)
                {
                    if (++run == 62 || p == n - 1) { o[pos++] = (byte)(0xC0 | (run - 1)); run = 0; }
                    continue;
                }
                if (run > 0) { o[pos++] = (byte)(0xC0 | (run - 1)); run = 0; }
                int hk = Hash(r, g, b, a);
                if (index[hk * 4] == r && index[hk * 4 + 1] == g && index[hk * 4 + 2] == b && index[hk * 4 + 3] == a)
                    o[pos++] = (byte)hk;
                else
                {
                    index[hk * 4] = r; index[hk * 4 + 1] = g; index[hk * 4 + 2] = b; index[hk * 4 + 3] = a;
                    if (a == pa)
                    {
                        int dr = (sbyte)(byte)(r - pr), dg = (sbyte)(byte)(g - pg), db = (sbyte)(byte)(b - pb);
                        int drg = dr - dg, dbg = db - dg;
                        if (dr is >= -2 and <= 1 && dg is >= -2 and <= 1 && db is >= -2 and <= 1)
                            o[pos++] = (byte)(0x40 | (dr + 2) << 4 | (dg + 2) << 2 | (db + 2));
                        else if (dg is >= -32 and <= 31 && drg is >= -8 and <= 7 && dbg is >= -8 and <= 7)
                        {
                            o[pos++] = (byte)(0x80 | (dg + 32));
                            o[pos++] = (byte)((drg + 8) << 4 | (dbg + 8));
                        }
                        else { o[pos++] = 0xFE; o[pos++] = r; o[pos++] = g; o[pos++] = b; }
                    }
                    else { o[pos++] = 0xFF; o[pos++] = r; o[pos++] = g; o[pos++] = b; o[pos++] = a; }
                }
                pr = r; pg = g; pb = b; pa = a;
            }
            End.CopyTo(o.AsSpan(pos));
            pos += 8;
            Array.Resize(ref o, pos);
            return o;
        }
    }
}
