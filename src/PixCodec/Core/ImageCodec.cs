using System;
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
            _ => throw new ImageFormatException("unknown image format"),
        };

        public static ImageInfo GetInfo(Stream stream) => GetInfo(ReadAll(stream));
        public static ImageInfo GetInfo(string path) => GetInfo(File.ReadAllBytes(path));

        public static RawImage Decode(ReadOnlySpan<byte> data) => DetectFormat(data) switch
        {
            ImageFormat.Png => Png.Decode(data),
            ImageFormat.Jpeg => Jpeg.Decode(data),
            ImageFormat.Gif => Gif.Decode(data),
            ImageFormat.Tiff => Tiff.Decode(data),
            ImageFormat.Exr => Exr.Decode(data),
            ImageFormat.Webp => Webp.Decode(data),
            _ => throw new ImageFormatException("unknown image format"),
        };

        public static RawImage Decode(Stream stream) => Decode(ReadAll(stream));
        public static RawImage Decode(string path) => Decode(File.ReadAllBytes(path));

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
