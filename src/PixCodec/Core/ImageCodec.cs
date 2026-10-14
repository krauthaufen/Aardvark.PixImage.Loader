using System;
using System.Collections.Generic;
using System.IO;

namespace PixCodec
{
    /// <summary>Format independent entry points: sniff the format, then dispatch to the codec.</summary>
    public static class ImageCodec
    {
        public static ImageFormat DetectFormat(ReadOnlySpan<byte> header)
        {
            if (Png.IsMatch(header)) return ImageFormat.Png;
            if (Jpeg.IsMatch(header)) return ImageFormat.Jpeg;
            if (Gif.IsMatch(header)) return ImageFormat.Gif;
            if (Tiff.IsMatch(header)) return ImageFormat.Tiff;
            if (Exr.IsMatch(header)) return ImageFormat.Exr;
            if (Webp.IsMatch(header)) return ImageFormat.Webp;
            if (Jp2.IsMatch(header)) return ImageFormat.Jp2;
            return ImageFormat.Unknown;
        }

        public static ImageFormat FormatFromExtension(string path)
            => Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".png" => ImageFormat.Png,
                ".jpg" or ".jpeg" or ".jpe" or ".jfif" => ImageFormat.Jpeg,
                ".tif" or ".tiff" => ImageFormat.Tiff,
                ".gif" => ImageFormat.Gif,
                ".exr" => ImageFormat.Exr,
                ".webp" => ImageFormat.Webp,
                ".jp2" or ".j2k" or ".j2c" or ".jpc" or ".jpf" or ".jpx" or ".jph" or ".jhc" => ImageFormat.Jp2,
                _ => ImageFormat.Unknown,
            };

        public static ImageInfo GetInfo(ReadOnlySpan<byte> data) => DetectFormat(data) switch
        {
            ImageFormat.Png => Png.GetInfo(data),
            ImageFormat.Jpeg => Jpeg.GetInfo(data),
            ImageFormat.Gif => Gif.GetInfo(data),
            ImageFormat.Tiff => Tiff.GetInfo(data),
            ImageFormat.Exr => Exr.GetInfo(data),
            ImageFormat.Webp => Webp.GetInfo(data),
            ImageFormat.Jp2 => Jp2.GetInfo(data),
            _ => throw new ImageFormatException("unknown image format"),
        };

        /// <summary>
        /// Reads format, dimensions, channels, sample type and frame count of an image file without decoding it and
        /// without reading the whole file: only the leading part holding the headers is read (TIFF directories are
        /// located by seeking). GIF is the exception, counting its frames needs the whole file.
        /// </summary>
        public static ImageInfo GetInfo(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.None);
            return GetInfo(fs);
        }

        /// <summary>
        /// Like <see cref="GetInfo(string)"/> for a stream positioned at the start of the image. Reads only as far as
        /// the headers need (the stream position afterwards is unspecified). Works on non-seekable streams.
        /// </summary>
        public static ImageInfo GetInfo(Stream stream) => GetInfo(stream, 8192);

        /// <summary>Returns false instead of throwing when the file is missing, unreadable or not a supported image.</summary>
        public static bool TryGetInfo(string path, out ImageInfo info)
        {
            try { info = GetInfo(path); return true; }
            catch (Exception e) when (e is ImageFormatException or ImageNotSupportedException or IOException or UnauthorizedAccessException)
            {
                info = default;
                return false;
            }
        }

        internal static ImageInfo GetInfo(Stream stream, int initialSize)
        {
            var buf = new byte[Math.Max(initialSize, 64)];
            int n = Fill(stream, buf, 0);
            bool eof = n < buf.Length;
            var format = DetectFormat(buf.AsSpan(0, n));
            if (format == ImageFormat.Unknown) throw new ImageFormatException("unknown image format");
            if (format == ImageFormat.Tiff && stream.CanSeek)
            {
                stream.Seek(-n, SeekOrigin.Current);
                return Tiff.GetInfo(stream);
            }
            // TIFF directories can be anywhere and GIF frames have to be counted: these need all the data.
            // The others are parsed from a growing prefix; their header parsers throw ImageFormatException when
            // the prefix ends inside the headers, which is the signal to read more.
            bool needAll = format is ImageFormat.Tiff or ImageFormat.Gif;
            while (true)
            {
                if (eof) return GetInfo(buf.AsSpan(0, n));
                if (!needAll && (format != ImageFormat.Png || PngHeadersComplete(buf.AsSpan(0, n))))
                {
                    try { return GetInfo(buf.AsSpan(0, n)); }
                    catch (ImageFormatException) { }
                }
                Array.Resize(ref buf, (int)Math.Min((long)buf.Length * 4, Array.MaxLength));
                int got = Fill(stream, buf, n);
                eof = n + got < buf.Length;
                n += got;
                if (!eof && n == Array.MaxLength) throw new ImageNotSupportedException("image headers exceed 2 GB");
            }
        }

        /// <summary>
        /// PNG's header parser stops quietly at the end of its input, so decide here whether the prefix reaches the
        /// first IDAT (everything that affects the info - tRNS, acTL - has to precede it).
        /// </summary>
        static bool PngHeadersComplete(ReadOnlySpan<byte> prefix)
        {
            long pos = 8;
            while (pos + 8 <= prefix.Length)
            {
                var head = prefix.Slice((int)pos, 8);
                uint length = (uint)(head[0] << 24 | head[1] << 16 | head[2] << 8 | head[3]);
                if (head.Slice(4).SequenceEqual("IDAT"u8) || head.Slice(4).SequenceEqual("IEND"u8)) return true;
                pos += 12L + length;
            }
            return false;
        }

        static int Fill(Stream stream, byte[] buffer, int offset)
        {
            int total = 0;
            while (offset + total < buffer.Length)
            {
                int r = stream.Read(buffer, offset + total, buffer.Length - offset - total);
                if (r <= 0) break;
                total += r;
            }
            return total;
        }

        public static RawImage Decode(ReadOnlySpan<byte> data) => DetectFormat(data) switch
        {
            ImageFormat.Png => Png.Decode(data),
            ImageFormat.Jpeg => Jpeg.Decode(data),
            ImageFormat.Gif => Gif.Decode(data),
            ImageFormat.Tiff => Tiff.Decode(data),
            ImageFormat.Exr => Exr.Decode(data),
            ImageFormat.Webp => Webp.Decode(data),
            ImageFormat.Jp2 => Jp2.Decode(data),
            _ => throw new ImageFormatException("unknown image format"),
        };

        public static RawImage Decode(Stream stream) => Decode(ReadAll(stream));
        public static RawImage Decode(string path) => Decode(File.ReadAllBytes(path));

        /// <summary>
        /// Decodes every image in the file: the pages of a TIFF, the frames of an animated GIF, PNG or WebP (each the whole canvas
        /// as a viewer shows it), or else the single image.
        /// </summary>
        public static RawImage[] DecodeAll(ReadOnlySpan<byte> data)
        {
            switch (DetectFormat(data))
            {
                case ImageFormat.Tiff:
                    {
                        int n = Tiff.GetImageCount(data);
                        var r = new RawImage[n];
                        for (int i = 0; i < n; i++) r[i] = Tiff.Decode(data, new TiffDecodeOptions { ImageIndex = i });
                        return r;
                    }
                case ImageFormat.Gif:
                    return Frames(Gif.DecodeAnimation(data).Frames, f => f.Image!); // composited mode always sets it
                case ImageFormat.Png when GetInfo(data).IsAnimated:
                    return Frames(Png.DecodeAnimation(data).Frames, f => f.Image);
                case ImageFormat.Webp when GetInfo(data).IsAnimated:
                    return Frames(Webp.DecodeAnimation(data).Frames, f => f.Image);
                default:
                    return new[] { Decode(data) };
            }
        }

        public static RawImage[] DecodeAll(Stream stream) => DecodeAll(ReadAll(stream));
        public static RawImage[] DecodeAll(string path) => DecodeAll(File.ReadAllBytes(path));

        static RawImage[] Frames<TF>(IReadOnlyList<TF> frames, Func<TF, RawImage> image)
        {
            var r = new RawImage[frames.Count];
            for (int i = 0; i < r.Length; i++) r[i] = image(frames[i]);
            return r;
        }

        /// <summary>Encodes with default options for the format.</summary>
        public static void Encode(RawImage image, Stream output, ImageFormat format)
        {
            switch (format)
            {
                case ImageFormat.Png: Png.Encode(image, output); break;
                case ImageFormat.Jpeg: Jpeg.Encode(image, output); break;
                case ImageFormat.Gif: Gif.Encode(image, output); break;
                case ImageFormat.Tiff: Tiff.Encode(image, output); break;
                case ImageFormat.Exr: Exr.Encode(image, output); break;
                case ImageFormat.Webp: Webp.Encode(image, output); break;
                case ImageFormat.Jp2: Jp2.Encode(image, output); break;
                default: throw new ArgumentException("unknown image format", nameof(format));
            }
        }

        public static void Encode(RawImage image, string path)
        {
            var format = FormatFromExtension(path);
            if (format == ImageFormat.Unknown) throw new ArgumentException($"cannot infer image format from '{path}'");
            using var fs = File.Create(path);
            Encode(image, fs, format);
        }

        public static byte[] Encode(RawImage image, ImageFormat format)
        {
            using var ms = new MemoryStream();
            Encode(image, ms, format);
            return ms.ToArray();
        }

        /// <summary>Reads the remainder of a stream into an array.</summary>
        public static byte[] ReadAll(Stream stream)
        {
            if (stream is MemoryStream m && m.TryGetBuffer(out var seg) && m.Position == 0 && seg.Offset == 0 && seg.Count == seg.Array!.Length)
            {
                m.Position = m.Length;
                return seg.Array;
            }
            if (stream.CanSeek)
            {
                long len = stream.Length - stream.Position;
                var buf = new byte[len];
                stream.ReadExactly(buf);
                return buf;
            }
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }
    }
}
