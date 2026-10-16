using System;
using System.Collections.Generic;
using System.IO;

namespace Aardvark.ImageCodecs
{
    /// <summary>Format independent entry points: sniff the format, then dispatch to the codec.</summary>
    public static partial class ImageCodec
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
            if (Bmp.IsMatch(header)) return ImageFormat.Bmp;
            if (Dds.IsMatch(header)) return ImageFormat.Dds;
            if (Ktx.IsMatch(header)) return ImageFormat.Ktx;
            if (Qoi.IsMatch(header)) return ImageFormat.Qoi;
            if (Dpx.IsMatch(header)) return ImageFormat.Dpx;
            if (Cineon.IsMatch(header)) return ImageFormat.Cineon;
            if (Hdr.IsMatch(header)) return ImageFormat.Hdr;
            if (Pfm.IsMatch(header)) return ImageFormat.Pfm;
            if (Pnm.IsMatch(header)) return ImageFormat.Pnm;
            if (Ico.IsMatch(header)) return ImageFormat.Ico;
            if (Tga.IsMatch(header)) return ImageFormat.Tga;   // no signature: last
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
                ".bmp" or ".dib" => ImageFormat.Bmp,
                ".dds" => ImageFormat.Dds,
                ".tga" or ".targa" or ".icb" or ".vda" or ".vst" => ImageFormat.Tga,
                ".hdr" or ".rgbe" or ".pic" => ImageFormat.Hdr,
                ".pbm" or ".pgm" or ".ppm" or ".pnm" or ".pam" => ImageFormat.Pnm,
                ".pfm" => ImageFormat.Pfm,
                ".qoi" => ImageFormat.Qoi,
                ".ico" or ".cur" => ImageFormat.Ico,
                ".ktx" or ".ktx2" => ImageFormat.Ktx,
                ".dpx" => ImageFormat.Dpx,
                ".cin" or ".cineon" => ImageFormat.Cineon,
                _ => ImageFormat.Unknown,
            };

        /// <summary>
        /// Reads format, dimensions, channels, sample type, frame count and the descriptive metadata
        /// (<see cref="ImageInfo.Metadata"/>) of an encoded image without decoding its pixels.
        /// </summary>
        public static ImageInfo GetInfo(ReadOnlySpan<byte> data) => HeaderInfo(data) with { Metadata = TryReadMetadata(data) };

        static ImageInfo HeaderInfo(ReadOnlySpan<byte> data) => DetectFormat(data) switch
        {
            ImageFormat.Png => Png.GetInfo(data),
            ImageFormat.Jpeg => Jpeg.GetInfo(data),
            ImageFormat.Gif => Gif.GetInfo(data),
            ImageFormat.Tiff => Tiff.GetInfo(data),
            ImageFormat.Exr => Exr.GetInfo(data),
            ImageFormat.Webp => Webp.GetInfo(data),
            ImageFormat.Jp2 => Jp2.GetInfo(data),
            ImageFormat.Bmp => Bmp.GetInfo(data),
            ImageFormat.Tga => Tga.GetInfo(data),
            ImageFormat.Dds => Dds.GetInfo(data),
            ImageFormat.Hdr => Hdr.GetInfo(data),
            ImageFormat.Pnm => Pnm.GetInfo(data),
            ImageFormat.Pfm => Pfm.GetInfo(data),
            ImageFormat.Qoi => Qoi.GetInfo(data),
            ImageFormat.Ico => Ico.GetInfo(data),
            ImageFormat.Ktx => Ktx.GetInfo(data),
            ImageFormat.Dpx => Dpx.GetInfo(data),
            ImageFormat.Cineon => Cineon.GetInfo(data),
            _ => throw new ImageFormatException("unknown image format"),
        };

        /// <summary>Metadata problems never make the header information unreadable.</summary>
        static PhotoMetadata? TryReadMetadata(ReadOnlySpan<byte> data)
        {
            try { return MetadataReader.Read(data); }
            catch (Exception e) when (e is ImageFormatException or ImageNotSupportedException or ArgumentException or IndexOutOfRangeException
                                       or InvalidOperationException or OverflowException or System.Xml.XmlException) { return null; }
        }

        /// <summary>
        /// Reads format, dimensions, channels, sample type, frame count and the descriptive metadata of an image file without
        /// decoding it. The file is memory mapped, so only the parts holding headers and metadata are read from disk (GIF frame
        /// counting and run length coded TGA / BMP walk all data).
        /// </summary>
        public static unsafe ImageInfo GetInfo(string path)
        {
            long length = new FileInfo(path).Length;
            if (length == 0) throw new ImageFormatException("empty file");
            if (length > int.MaxValue)
            {
                // beyond what a span can address: headers through the stream, metadata from the first 2 GB
                ImageInfo info;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.None))
                    info = GetInfo(fs, 8192, false);
                return info with { Metadata = Mapped(path, int.MaxValue, d => TryReadMetadata(d)) };
            }
            return Mapped(path, length, d => GetInfo(d));
        }

        delegate T SpanFunc<T>(ReadOnlySpan<byte> data);

        static unsafe T Mapped<T>(string path, long length, SpanFunc<T> f)
        {
            using var mmf = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
            using var view = mmf.CreateViewAccessor(0, length, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
            byte* p = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            try { return f(new ReadOnlySpan<byte>(p + view.PointerOffset, (int)length)); }
            finally { view.SafeMemoryMappedViewHandle.ReleasePointer(); }
        }

        /// <summary>
        /// Like <see cref="GetInfo(string)"/> for a stream positioned at the start of the image (the position afterwards is
        /// unspecified). Works on non-seekable streams. Pixel data is not read: file streams are memory mapped, other
        /// seekable streams are walked by seeking past the image data (PNG, WebP, JPEG 2000 chunks, TIFF directories). From
        /// non-seekable streams the metadata comes from the part read for the headers (for JPEG, up to the scan); metadata
        /// stored after the pixels (possible in PNG, WebP) is then missed.
        /// </summary>
        public static ImageInfo GetInfo(Stream stream) => GetInfo(stream, 8192, true);

        /// <summary>
        /// Reads the descriptive metadata (EXIF, XMP, IPTC, ICC, comments and text chunks, OpenEXR attributes) and the
        /// common values taken from them: camera, capture settings, dates, GPS, drone attitude, description. Pixels are not
        /// decoded. Damaged blocks are skipped.
        /// </summary>
        public static PhotoMetadata ReadMetadata(ReadOnlySpan<byte> data) => MetadataReader.Read(data);

        /// <inheritdoc cref="ReadMetadata(ReadOnlySpan{byte})"/>
        /// <remarks>The file is memory mapped: only the parts holding metadata are read from disk.</remarks>
        public static PhotoMetadata ReadMetadata(string path)
        {
            long length = new FileInfo(path).Length;
            if (length == 0) throw new ImageFormatException("empty file");
            return Mapped(path, Math.Min(length, int.MaxValue), d => MetadataReader.Read(d));   // (metadata sits in the first 2 GB)
        }

        /// <inheritdoc cref="ReadMetadata(ReadOnlySpan{byte})"/>
        public static PhotoMetadata ReadMetadata(Stream stream) => ReadMetadata(ReadAll(stream));

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

        internal static ImageInfo GetInfo(Stream stream, int initialSize) => GetInfo(stream, initialSize, true);

        static ImageInfo GetInfo(Stream stream, int initialSize, bool metadata)
        {
            long start = stream.CanSeek ? stream.Position : -1;
            var info = HeaderInfo(stream, initialSize, out var prefix);
            if (!metadata) return info;
            try
            {
                if (stream is FileStream fs && start >= 0 && fs.Length - start > 0)
                {
                    // memory mapped: only the pages holding metadata are read
                    long len = Math.Min(fs.Length - start, int.MaxValue);
                    using var mmf = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(fs, null, 0,
                        System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
                    using var view = mmf.CreateViewAccessor(start, len, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
                    unsafe
                    {
                        byte* p = null;
                        view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
                        try { return info with { Metadata = TryReadMetadata(new ReadOnlySpan<byte>(p + view.PointerOffset, (int)len)) }; }
                        finally { view.SafeMemoryMappedViewHandle.ReleasePointer(); }
                    }
                }
                if (stream is MemoryStream ms && start >= 0 && ms.TryGetBuffer(out var seg))
                    return info with { Metadata = TryReadMetadata(seg.AsSpan((int)start)) };
                return info with { Metadata = TryReadMetadata(StreamMetadata.Collect(stream, start, info.Format, prefix)) };
            }
            catch (IOException) { return info with { Metadata = TryReadMetadata(prefix) }; }
            catch (NotSupportedException) { return info with { Metadata = TryReadMetadata(prefix) }; }
        }

        static ImageInfo HeaderInfo(Stream stream, int initialSize, out byte[] prefix)
        {
            prefix = Array.Empty<byte>();
            var buf = new byte[Math.Max(initialSize, 64)];
            int n = Fill(stream, buf, 0);
            bool eof = n < buf.Length;
            var format = DetectFormat(buf.AsSpan(0, n));
            if (format == ImageFormat.Unknown) throw new ImageFormatException("unknown image format");
            if (format == ImageFormat.Tiff && stream.CanSeek)
            {
                prefix = buf.AsSpan(0, n).ToArray();
                stream.Seek(-n, SeekOrigin.Current);
                return Tiff.GetInfo(stream);
            }
            // TIFF directories can be anywhere, GIF frames have to be counted, the TGA footer is at the end and BMP run
            // codes have to be walked: these need all the data. The others are parsed from a growing prefix; their
            // header parsers throw ImageFormatException when the prefix ends inside the headers, which is the signal
            // to read more.
            bool needAll = format is ImageFormat.Tiff or ImageFormat.Gif or ImageFormat.Tga or ImageFormat.Ico
                || format == ImageFormat.Bmp && Bmp.InfoNeedsAllData(buf.AsSpan(0, n));
            while (true)
            {
                if (eof) { prefix = buf.AsSpan(0, n).ToArray(); return HeaderInfo(buf.AsSpan(0, n)); }
                if (!needAll && (format != ImageFormat.Png || PngHeadersComplete(buf.AsSpan(0, n)))
                    && (format != ImageFormat.Bmp || n >= Math.Min(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(10)), 1u << 20)))
                {
                    try { var r = HeaderInfo(buf.AsSpan(0, n)); prefix = buf.AsSpan(0, n).ToArray(); return r; }
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

        public static RawImage Decode(ReadOnlySpan<byte> data) => ColorTags.Tag(DecodeCore(data), data, DetectFormat(data));

        static RawImage DecodeCore(ReadOnlySpan<byte> data) => DetectFormat(data) switch
        {
            ImageFormat.Png => Png.Decode(data),
            ImageFormat.Jpeg => Jpeg.Decode(data),
            ImageFormat.Gif => Gif.Decode(data),
            ImageFormat.Tiff => Tiff.Decode(data),
            ImageFormat.Exr => Exr.Decode(data),
            ImageFormat.Webp => Webp.Decode(data),
            ImageFormat.Jp2 => Jp2.Decode(data),
            ImageFormat.Bmp => Bmp.Decode(data),
            ImageFormat.Tga => Tga.Decode(data),
            ImageFormat.Dds => Dds.Decode(data),
            ImageFormat.Hdr => Hdr.Decode(data),
            ImageFormat.Pnm => Pnm.Decode(data),
            ImageFormat.Pfm => Pfm.Decode(data),
            ImageFormat.Qoi => Qoi.Decode(data),
            ImageFormat.Ico => Ico.Decode(data),
            ImageFormat.Ktx => Ktx.Decode(data),
            ImageFormat.Dpx => Dpx.Decode(data),
            ImageFormat.Cineon => Cineon.Decode(data),
            _ => throw new ImageFormatException("unknown image format"),
        };

        public static RawImage Decode(Stream stream) => Decode(ReadAll(stream));
        public static RawImage Decode(string path) => Decode(File.ReadAllBytes(path));

        /// <summary>
        /// Decodes every image in the file: the pages of a TIFF, the frames of an animated GIF, PNG or WebP (each the whole canvas
        /// as a viewer shows it), the array elements / cube faces of a DDS texture (top mip level), the image
        /// elements of a DPX file, or else the single image.
        /// </summary>
        public static RawImage[] DecodeAll(ReadOnlySpan<byte> data) => ColorTags.Tag(DecodeAllCore(data), data, DetectFormat(data));

        static RawImage[] DecodeAllCore(ReadOnlySpan<byte> data)
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
                case ImageFormat.Dds:
                    {
                        var t = Dds.DecodeTexture(data);
                        var r = new List<RawImage>();
                        for (int item = 0; item < t.ArraySize; item++)
                            for (int face = 0; face < Math.Max(1, t.Faces.Count); face++) r.Add(t.GetImage(0, item, face));
                        return r.ToArray();
                    }
                case ImageFormat.Ico:
                    return Ico.DecodeAll(data);
                case ImageFormat.Ktx:
                    return Ktx.DecodeAll(data);
                case ImageFormat.Dpx:
                    return Dpx.DecodeAll(data);
                default:
                    return new[] { Decode(data) };
            }
        }

        public static RawImage[] DecodeAll(Stream stream) => DecodeAll(ReadAll(stream));
        public static RawImage[] DecodeAll(string path) => DecodeAll(File.ReadAllBytes(path));

        /// <summary>
        /// The image with its stored mip levels, largest first: the mip chain of a DDS texture (first array element / face) or
        /// the mip levels of an OpenEXR file (first part). Formats without stored levels give just the image; use
        /// <see cref="ImageOps.Resize(RawImage, int, int, ResizeOptions?)"/> to compute levels.
        /// </summary>
        public static RawImage[] DecodeMipmaps(ReadOnlySpan<byte> data) => ColorTags.Tag(DecodeMipmapsCore(data), data, DetectFormat(data));

        static RawImage[] DecodeMipmapsCore(ReadOnlySpan<byte> data) => DetectFormat(data) switch
        {
            ImageFormat.Dds => Dds.DecodeMipmaps(data),
            ImageFormat.Exr => Exr.DecodeMipmaps(data),
            ImageFormat.Ktx => Ktx.DecodeMipmaps(data),
            _ => new[] { Decode(data) },
        };

        public static RawImage[] DecodeMipmaps(Stream stream) => DecodeMipmaps(ReadAll(stream));
        public static RawImage[] DecodeMipmaps(string path) => DecodeMipmaps(File.ReadAllBytes(path));

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
                case ImageFormat.Bmp: Bmp.Encode(image, output); break;
                case ImageFormat.Tga: Tga.Encode(image, output); break;
                case ImageFormat.Dds: Dds.Encode(image, output); break;
                case ImageFormat.Hdr: Hdr.Encode(image, output); break;
                case ImageFormat.Pnm: Pnm.Encode(image, output); break;
                case ImageFormat.Pfm: Pfm.Encode(image, output); break;
                case ImageFormat.Qoi: Qoi.Encode(image, output); break;
                case ImageFormat.Ico: Ico.Encode(new[] { image }, output); break;
                case ImageFormat.Ktx: Ktx.Encode(image, output); break;
                case ImageFormat.Dpx: Dpx.Encode(image, output); break;
                case ImageFormat.Cineon: Cineon.Encode(image, output); break;
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
