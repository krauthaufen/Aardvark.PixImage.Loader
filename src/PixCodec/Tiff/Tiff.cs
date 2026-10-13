using System;
using System.IO;

namespace PixCodec
{
    public sealed class TiffDecodeOptions
    {
        /// <summary>Which image (IFD) of a multi-page / pyramid file to decode, 0 = first.</summary>
        public int ImageIndex { get; set; }

        /// <summary>Decode strips/tiles on several threads when the image is large enough to benefit.</summary>
        public bool Parallel { get; set; } = true;

        /// <summary>Upper bound for the number of decoding threads, 0 or less = no limit.</summary>
        public int MaxDegreeOfParallelism { get; set; }

        /// <summary>Decoded size in bytes from which <see cref="Parallel"/> kicks in.</summary>
        internal long ParallelThreshold = 512 * 1024;
    }

    public enum TiffPredictor
    {
        /// <summary>Horizontal differencing for integers and the floating point predictor for floats.</summary>
        Auto,
        None,
        /// <summary>Horizontal differencing (predictor 2). Valid for all sample types.</summary>
        Horizontal,
        /// <summary>Floating point predictor (predictor 3). Float sample types only.</summary>
        FloatingPoint,
    }

    public sealed class TiffEncodeOptions
    {
        public TiffCompression Compression { get; set; } = TiffCompression.Deflate;

        /// <summary>
        /// Predictor applied before LZW / Deflate. Ignored for None and PackBits, where other readers (libtiff)
        /// would not undo it.
        /// </summary>
        public TiffPredictor Predictor { get; set; } = TiffPredictor.Auto;

        /// <summary>Deflate level 0..9.</summary>
        public int CompressionLevel { get; set; } = 6;

        /// <summary>Rows per strip, 0 = pick so that a strip holds about 64 KB of samples. Ignored when writing tiles.</summary>
        public int RowsPerStrip { get; set; }

        /// <summary>Set both to write tiles instead of strips. Rounded up to a multiple of 16 as the format requires.</summary>
        public int TileWidth { get; set; }
        public int TileHeight { get; set; }

        /// <summary>true/false forces BigTIFF or classic TIFF; null picks BigTIFF only when the file could exceed 4 GB.</summary>
        public bool? BigTiff { get; set; }

        /// <summary>Compress strips/tiles on several threads.</summary>
        public bool Parallel { get; set; } = true;
    }

    /// <summary>
    /// TIFF codec: classic TIFF and BigTIFF, strips and tiles, both byte orders.
    /// <para>Decoding rules worth knowing:</para>
    /// <list type="bullet">
    /// <item>1/2/4-bit gray is scaled to the full UInt8 range; MinIsWhite is inverted for unsigned integer data
    /// (signed and float data is returned as stored).</item>
    /// <item>Palette images are expanded to UInt8 Rgb; CMYK is converted naively to Rgb.</item>
    /// <item>Exactly one extra sample marked as alpha gives GrayAlpha/Rgba (associated alpha is un-premultiplied).
    /// Any other surplus samples (unspecified extras, more than one extra) keep ALL samples as stored and report
    /// <see cref="ColorModel.Other"/>.</item>
    /// <item>Orientation is reported in the metadata, never applied.</item>
    /// </list>
    /// </summary>
    public static unsafe class Tiff
    {
        /// <summary>
        /// Decoder for JPEG-in-TIFF (compression 7) strips and tiles, see <see cref="TiffJpegDecoder"/>.
        /// A field so tests can substitute it.
        /// </summary>
        internal static TiffJpegDecoder? JpegDecoder = Jpeg.DecodeForTiff;

        /// <summary>Returns whether the data starts with a TIFF signature (needs at most 16 bytes).</summary>
        public static bool IsMatch(ReadOnlySpan<byte> header) => TiffFile.IsMatch(header);

        /// <summary>Reads header level information of the first image without decoding pixel data. FrameCount is the number of images.</summary>
        public static ImageInfo GetInfo(ReadOnlySpan<byte> data)
        {
            fixed (byte* p = data)
            {
                var src = new TiffMemorySource(p, data.Length);
                return Guard(() => InfoCore(src));
            }
        }

        public static ImageInfo GetInfo(Stream seekableStream)
        {
            var src = new TiffStreamSource(seekableStream);
            return Guard(() => InfoCore(src), seekableStream, src.Origin);
        }

        /// <summary>Number of images (IFDs in the main chain) in the file.</summary>
        public static int GetImageCount(ReadOnlySpan<byte> data)
        {
            fixed (byte* p = data)
            {
                var src = new TiffMemorySource(p, data.Length);
                return Guard(() => new TiffFile(src).IfdOffsets().Count);
            }
        }

        public static int GetImageCount(Stream seekableStream)
        {
            var src = new TiffStreamSource(seekableStream);
            return Guard(() => new TiffFile(src).IfdOffsets().Count, seekableStream, src.Origin);
        }

        /// <summary>Header level information for every image of a multi-page / pyramid file.</summary>
        public static ImageInfo[] GetInfos(ReadOnlySpan<byte> data)
        {
            fixed (byte* p = data)
            {
                var src = new TiffMemorySource(p, data.Length);
                return Guard(() => InfosCore(src));
            }
        }

        public static ImageInfo[] GetInfos(Stream seekableStream)
        {
            var src = new TiffStreamSource(seekableStream);
            return Guard(() => InfosCore(src), seekableStream, src.Origin);
        }

        /// <summary>Decodes a complete TIFF file held in memory.</summary>
        public static RawImage Decode(ReadOnlySpan<byte> data, TiffDecodeOptions? options = null)
        {
            fixed (byte* p = data)
            {
                var src = new TiffMemorySource(p, data.Length);
                return Guard(() => DecodeCore(src, options, null));
            }
        }

        /// <summary>
        /// Decodes from a seekable stream, reading the directory and the strips/tiles on demand instead of loading
        /// the whole file. The TIFF is taken to start at the current stream position, and the position is restored
        /// to that start afterwards (also for the other stream overloads), so repeated calls need no rewinding.
        /// </summary>
        public static RawImage Decode(Stream seekableStream, TiffDecodeOptions? options = null)
        {
            var src = new TiffStreamSource(seekableStream);
            return Guard(() => DecodeCore(src, options, null), seekableStream, src.Origin);
        }

        /// <summary>Decodes only the given pixel rectangle; only the strips/tiles it touches are read and decompressed.</summary>
        public static RawImage DecodeRegion(ReadOnlySpan<byte> data, int x, int y, int width, int height, TiffDecodeOptions? options = null)
        {
            fixed (byte* p = data)
            {
                var src = new TiffMemorySource(p, data.Length);
                return Guard(() => DecodeCore(src, options, (x, y, width, height)));
            }
        }

        public static RawImage DecodeRegion(Stream seekableStream, int x, int y, int width, int height, TiffDecodeOptions? options = null)
        {
            var src = new TiffStreamSource(seekableStream);
            return Guard(() => DecodeCore(src, options, (x, y, width, height)), seekableStream, src.Origin);
        }

        /// <summary>
        /// Reads all tags of an image directory as raw values, e.g. the GeoTIFF tags ModelPixelScale (33550),
        /// ModelTiepoint (33922), GeoKeyDirectory (34735) or GDAL_NODATA (42113). No pixel data is touched.
        /// </summary>
        public static TiffTag[] ReadTags(ReadOnlySpan<byte> data, int imageIndex = 0)
        {
            fixed (byte* p = data)
            {
                var src = new TiffMemorySource(p, data.Length);
                return Guard(() => TagsCore(src, imageIndex));
            }
        }

        public static TiffTag[] ReadTags(Stream seekableStream, int imageIndex = 0)
        {
            var src = new TiffStreamSource(seekableStream);
            return Guard(() => TagsCore(src, imageIndex), seekableStream, src.Origin);
        }

        /// <summary>Encodes an image as TIFF to the stream (which only needs to be writable, not seekable).</summary>
        public static void Encode(RawImage image, Stream output, TiffEncodeOptions? options = null)
            => TiffEncoder.Encode(image, output, options ?? new TiffEncodeOptions());

        static ImageInfo InfoCore(TiffSource src)
        {
            var file = new TiffFile(src);
            var offs = file.IfdOffsets();
            return new TiffImage(file, offs[0]).Info(offs.Count);
        }

        static ImageInfo[] InfosCore(TiffSource src)
        {
            var file = new TiffFile(src);
            var offs = file.IfdOffsets();
            var r = new ImageInfo[offs.Count];
            for (int i = 0; i < r.Length; i++) r[i] = new TiffImage(file, offs[i]).Info(offs.Count);
            return r;
        }

        static long Locate(TiffFile file, int index)
        {
            if (index < 0) throw new TiffArgumentException("imageIndex", "TIFF: image index must not be negative");
            var offs = file.IfdOffsets(index + 1);
            if (index >= offs.Count) throw new TiffArgumentException("imageIndex", $"TIFF: the file has only {offs.Count} image(s)");
            return offs[index];
        }

        static TiffTag[] TagsCore(TiffSource src, int index)
        {
            var file = new TiffFile(src);
            return file.ReadTags(file.ReadIfd(Locate(file, index)));
        }

        static RawImage DecodeCore(TiffSource src, TiffDecodeOptions? options, (int x, int y, int w, int h)? region)
        {
            options ??= new TiffDecodeOptions();
            var file = new TiffFile(src);
            var img = new TiffImage(file, Locate(file, options.ImageIndex));
            return region is var (x, y, w, h)
                ? img.Decode(x, y, w, h, options)
                : img.Decode(0, 0, img.Width, img.Height, options);
        }

        /// <summary>Safety net: whatever a corrupt file manages to trip inside the decoder surfaces as ImageFormatException.</summary>
        static T Guard<T>(Func<T> f, Stream? stream = null, long origin = 0)
        {
            try { return f(); }
            catch (Exception e) when (e is IndexOutOfRangeException || e is OverflowException || e is InvalidCastException
                || e is DivideByZeroException || e is EndOfStreamException || e is InvalidDataException
                || (e is ArgumentException && e is not TiffArgumentException))
            {
                throw new ImageFormatException("TIFF: corrupt file", e);
            }
            finally
            {
                // leave the stream where the TIFF starts so that further calls (other regions, pages) just work
                if (stream != null) stream.Position = origin;
            }
        }
    }
}

namespace PixCodec
{
    public enum TiffCompression { None, Lzw, Deflate, PackBits }
}
