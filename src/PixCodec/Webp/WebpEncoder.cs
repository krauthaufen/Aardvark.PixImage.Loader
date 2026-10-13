using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace PixCodec
{
    /// <summary>Turns images into WebP files: picks the bitstream, builds the chunks and writes the RIFF container.</summary>
    internal static class WebpEncoder
    {
        const int FlagAnim = 0x02, FlagXmp = 0x04, FlagExif = 0x08, FlagAlpha = 0x10, FlagIcc = 0x20;

        /// <summary>The image as ARGB (0xAARRGGBB); gray is expanded.</summary>
        internal static uint[] ToArgb(RawImage image)
        {
            if (image.SampleType != SampleType.UInt8)
                throw new ImageNotSupportedException($"WebP stores 8 bit samples, not {image.SampleType}");
            var src = (byte[])image.Data;
            int n = image.Width * image.Height;
            var argb = new uint[n];
            switch (image.Color)
            {
                case ColorModel.Gray:
                    for (int i = 0; i < n; i++) argb[i] = 0xff000000 | src[i] * 0x010101u;
                    break;
                case ColorModel.GrayAlpha:
                    for (int i = 0; i < n; i++) argb[i] = (uint)src[2 * i + 1] << 24 | src[2 * i] * 0x010101u;
                    break;
                case ColorModel.Rgb:
                    for (int i = 0, o = 0; i < n; i++, o += 3) argb[i] = 0xff000000 | (uint)src[o] << 16 | (uint)src[o + 1] << 8 | src[o + 2];
                    break;
                case ColorModel.Rgba:
                    for (int i = 0, o = 0; i < n; i++, o += 4) argb[i] = (uint)src[o + 3] << 24 | (uint)src[o] << 16 | (uint)src[o + 1] << 8 | src[o + 2];
                    break;
                default:
                    throw new ImageNotSupportedException($"WebP cannot store {image.Channels} channels without a color model");
            }
            return argb;
        }

        static void CheckSize(int w, int h)
        {
            if (w > 16383 || h > 16383) throw new ImageNotSupportedException($"WebP images are limited to 16383 x 16383 pixels, got {w}x{h}");
        }

        /// <summary>The chunks of one image: an optional ALPH chunk followed by VP8 or VP8L.</summary>
        static List<(string tag, byte[] data)> EncodeImage(uint[] argb, int w, int h, bool hasAlpha, WebpEncodeOptions opt)
        {
            var chunks = new List<(string, byte[])>();
            if (opt.Lossless)
            {
                if (!opt.Exact && hasAlpha)
                    for (int i = 0; i < argb.Length; i++) if (argb[i] >> 24 == 0) argb[i] = 0;
                chunks.Add(("VP8L", Vp8lEncoder.Encode(argb, w, h, hasAlpha, opt.Method)));
            }
            else
            {
                if (hasAlpha) chunks.Add(("ALPH", EncodeAlpha(argb, w, h, opt.Method)));
                chunks.Add(("VP8 ", Vp8Encoder.Encode(argb, w, h, opt)));
            }
            return chunks;
        }

        static byte[] EncodeAlpha(uint[] argb, int w, int h, int method)
        {
            // the plane goes into the green channel of a lossless stream, which brings its own prediction
            var plane = new uint[argb.Length];
            for (int i = 0; i < plane.Length; i++) plane[i] = 0xff000000 | (argb[i] >> 24) << 8;
            var stream = Vp8lEncoder.EncodeHeaderless(plane, w, h, method);
            if (stream.Length < argb.Length)
            {
                var res = new byte[stream.Length + 1];
                res[0] = 1;     // lossless compression, no filter, no pre-processing
                stream.CopyTo(res, 1);
                return res;
            }
            var raw = new byte[argb.Length + 1];
            for (int i = 0; i < argb.Length; i++) raw[i + 1] = (byte)(argb[i] >> 24);
            return raw;
        }

        public static void EncodeStill(RawImage image, Stream output, WebpEncodeOptions opt)
        {
            CheckSize(image.Width, image.Height);
            var argb = ToArgb(image);
            bool alpha = image.HasAlpha;
            var chunks = EncodeImage(argb, image.Width, image.Height, alpha, opt);
            GetMetadata(image, opt, out var icc, out var exif, out var xmp);
            bool extended = chunks.Count > 1 || icc != null || exif != null || xmp != null;
            var file = new List<(string, byte[])>();
            if (extended)
            {
                int flags = (alpha ? FlagAlpha : 0) | (icc != null ? FlagIcc : 0) | (exif != null ? FlagExif : 0) | (xmp != null ? FlagXmp : 0);
                file.Add(("VP8X", Vp8x(flags, image.Width, image.Height)));
                if (icc != null) file.Add(("ICCP", icc));
            }
            file.AddRange(chunks);
            if (exif != null) file.Add(("EXIF", exif));
            if (xmp != null) file.Add(("XMP ", xmp));
            WriteRiff(output, file);
        }

        static void GetMetadata(RawImage image, WebpEncodeOptions opt, out byte[]? icc, out byte[]? exif, out byte[]? xmp)
        {
            icc = exif = xmp = null;
            if (!opt.WriteMetadata) return;
            icc = image.Metadata?.IccProfile;
            exif = image.Metadata?.Exif;
            xmp = opt.Xmp;
            if (icc != null && icc.Length == 0) icc = null;
            if (exif != null && exif.Length == 0) exif = null;
            if (xmp != null && xmp.Length == 0) xmp = null;
        }

        static byte[] Vp8x(int flags, int w, int h)
        {
            var b = new byte[10];
            b[0] = (byte)flags;
            b[4] = (byte)(w - 1); b[5] = (byte)((w - 1) >> 8); b[6] = (byte)((w - 1) >> 16);
            b[7] = (byte)(h - 1); b[8] = (byte)((h - 1) >> 8); b[9] = (byte)((h - 1) >> 16);
            return b;
        }

        static long ChunkSize(byte[] data) => 8L + data.Length + (data.Length & 1);

        static void WriteChunk(Stream s, string tag, byte[] data)
        {
            Span<byte> hdr = stackalloc byte[8];
            for (int i = 0; i < 4; i++) hdr[i] = (byte)tag[i];
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(4), (uint)data.Length);
            s.Write(hdr);
            s.Write(data);
            if ((data.Length & 1) != 0) s.WriteByte(0);
        }

        static void WriteRiff(Stream s, List<(string tag, byte[] data)> chunks)
        {
            long size = 4;
            foreach (var c in chunks) size += ChunkSize(c.data);
            if (size > uint.MaxValue - 9) throw new ImageNotSupportedException("WebP files are limited to 4 GB");
            Span<byte> hdr = stackalloc byte[12];
            "RIFF"u8.CopyTo(hdr);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(4), (uint)size);
            "WEBP"u8.CopyTo(hdr.Slice(8));
            s.Write(hdr);
            foreach (var c in chunks) WriteChunk(s, c.tag, c.data);
        }

        public static void EncodeAnimation(IEnumerable<WebpInputFrame> frames, Stream output, WebpEncodeOptions opt) => throw new ImageNotSupportedException("todo");
    }

    internal static class Vp8Encoder
    {
        public static byte[] Encode(uint[] argb, int w, int h, WebpEncodeOptions opt) => throw new ImageNotSupportedException("todo");
    }
}
