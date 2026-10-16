using System;
using System.IO;

namespace Aardvark.ImageCodecs
{
    public sealed class WbmpDecodeOptions
    {
        /// <summary>Upper bound for the image in pixels; larger files are rejected with <see cref="ImageNotSupportedException"/>.</summary>
        public long MaxPixels { get; set; } = 1L << 28;

        /// <summary>An image larger than this (in pixels) is only accepted when the file holds its pixel data.</summary>
        public long MaxUnbackedPixels { get; set; } = 1L << 26;

        internal static readonly WbmpDecodeOptions Default = new();
    }

    /// <summary>
    /// Wireless Bitmap (WAP WBMP) type 0: uncompressed 1 bit, rows padded to whole bytes, most significant bit first, 1 is
    /// white. Results are UInt8 Gray 0 / 255; files that end early decode as far as the data goes (the rest black). Writes any
    /// UInt8 image (luma at or above 128, or alpha below 128, is white).
    /// <para>WBMP has no signature: <see cref="IsMatch"/> wants the type 0 header and a file length that is exactly what the
    /// header announces, so the format independent entry points only take complete files for WBMP.</para>
    /// </summary>
    public static class Wbmp
    {
        /// <summary>Type 0 header with minimal multi-byte integers, and exactly the announced amount of data.</summary>
        public static bool IsMatch(ReadOnlySpan<byte> data)
            => TryHeader(data, out int w, out int h, out int at) && data.Length - at == (long)(w + 7) / 8 * h;

        static bool TryHeader(ReadOnlySpan<byte> d, out int w, out int h, out int at)
        {
            w = h = 0;
            at = 2;
            if (d.Length < 4 || d[0] != 0 || d[1] != 0) return false;
            return MultiByte(d, ref at, out w) && MultiByte(d, ref at, out h);
        }

        /// <summary>A WAP multi-byte integer: 7 bits per byte, most significant first, high bit = more follow. Positive, minimal.</summary>
        static bool MultiByte(ReadOnlySpan<byte> d, ref int at, out int v)
        {
            v = 0;
            if (at >= d.Length || d[at] == 0x80) return false;   // leading zero groups are not minimal
            for (int i = 0; i < 5; i++)
            {
                if (at >= d.Length) return false;
                byte b = d[at++];
                if (v > (int.MaxValue >> 7)) return false;
                v = v << 7 | (b & 0x7F);
                if ((b & 0x80) == 0) return v > 0;
            }
            return false;
        }

        static (int w, int h, int at) Header(ReadOnlySpan<byte> d)
        {
            if (!TryHeader(d, out int w, out int h, out int at)) throw new ImageFormatException("WBMP: not a type 0 WBMP header");
            return (w, h, at);
        }

        public static ImageInfo GetInfo(ReadOnlySpan<byte> data)
        {
            var (w, h, _) = Header(data);
            return new ImageInfo(ImageFormat.Wbmp, w, h, 1, SampleType.UInt8, ColorModel.Gray) { BitsPerSample = 1 };
        }

        public static unsafe RawImage Decode(ReadOnlySpan<byte> data, WbmpDecodeOptions? options = null)
        {
            var opt = options ?? WbmpDecodeOptions.Default;
            var (w, h, at) = Header(data);
            long pixels = (long)w * h;
            if (pixels > opt.MaxPixels) throw new ImageNotSupportedException($"WBMP: {w}x{h} exceeds WbmpDecodeOptions.MaxPixels");
            int rowBytes = (w + 7) / 8;
            if (pixels > opt.MaxUnbackedPixels && data.Length - at < (long)rowBytes * h)
                throw new ImageFormatException($"WBMP: {w}x{h} image with only {data.Length - at} bytes of pixel data");
            var dst = new byte[pixels];
            int rows = (int)Math.Min(h, (data.Length - at) / rowBytes);
            fixed (byte* s0 = data)
            fixed (byte* d0 = dst)
            {
                for (int y = 0; y < rows; y++)
                {
                    byte* s = s0 + at + (long)y * rowBytes;
                    byte* d = d0 + (long)y * w;
                    for (int x = 0; x < w; x++) d[x] = (byte)(-(s[x >> 3] >> (7 - (x & 7)) & 1));
                }
                // a partial last row
                int rest = data.Length - at - rows * rowBytes;
                if (rows < h && rest > 0)
                {
                    byte* s = s0 + at + (long)rows * rowBytes;
                    byte* d = d0 + (long)rows * w;
                    for (int x = 0; x < Math.Min(w, rest * 8); x++) d[x] = (byte)(-(s[x >> 3] >> (7 - (x & 7)) & 1));
                }
            }
            return new RawImage(w, h, ColorModel.Gray, dst);
        }

        /// <summary>Encodes any UInt8 image as black and white.</summary>
        public static void Encode(RawImage image, Stream output)
        {
            ArgumentNullException.ThrowIfNull(output);
            output.Write(Encode(image));
        }

        public static byte[] Encode(RawImage image)
        {
            ArgumentNullException.ThrowIfNull(image);
            Indexed.RequireUInt8(image, "WBMP");
            int w = image.Width, h = image.Height, rowBytes = (w + 7) / 8;
            var white = Indexed.Bilevel(image);
            Span<byte> head = stackalloc byte[12];
            int n = 2;
            head[0] = head[1] = 0;
            Put(head, ref n, w);
            Put(head, ref n, h);
            long size = n + (long)rowBytes * h;
            if (size > Array.MaxLength) throw new ImageNotSupportedException("WBMP: image too large");
            var r = new byte[size];
            head.Slice(0, n).CopyTo(r);
            for (int y = 0; y < h; y++)
            {
                long o = n + (long)y * rowBytes;
                for (int x = 0; x < w; x++) r[o + (x >> 3)] |= (byte)(white[(long)y * w + x] << (7 - (x & 7)));
            }
            return r;
        }

        static void Put(Span<byte> d, ref int n, int v)
        {
            int groups = 1;
            while (groups < 5 && v >> (7 * groups) != 0) groups++;
            for (int g = groups - 1; g >= 0; g--) d[n++] = (byte)((v >> (7 * g) & 0x7F) | (g > 0 ? 0x80 : 0));
        }
    }
}
