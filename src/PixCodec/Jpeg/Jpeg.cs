using System;
using System.IO;
using PixCodec.JpegImpl;

namespace PixCodec
{
    public sealed class JpegDecodeOptions
    {
        /// <summary>
        /// Decode at 1/1, 1/2, 1/4 or 1/8 of the size (value 1, 2, 4 or 8) by using smaller inverse DCTs: much faster
        /// than decoding and shrinking, with the same result as libjpeg's scaled decoding. The output is
        /// ceil(size / denominator). Ignored for lossless JPEG.
        /// </summary>
        public int ScaleDenominator { get; set; } = 1;

        /// <summary>
        /// Return 4 component (CMYK / YCCK) images as 4 channel <see cref="ColorModel.Other"/> data instead of converting
        /// them to RGB. The samples are C, M, Y, K as stored, which for the usual Adobe files means inverted (255 = no ink);
        /// YCCK is brought to that same form.
        /// </summary>
        public bool RawCmyk { get; set; }

        /// <summary>
        /// Skip the YCbCr to RGB conversion: YCbCr images come back as 3 channel <see cref="ColorModel.Other"/> data
        /// holding Y, Cb, Cr at full resolution (and YCCK, together with <see cref="RawCmyk"/>, as Y, Cb, Cr, K).
        /// </summary>
        public bool KeepYCbCr { get; set; }

        /// <summary>Rotate / mirror the pixels according to the EXIF orientation; the reported orientation then is 1.</summary>
        public bool ApplyOrientation { get; set; }

        /// <summary>
        /// By default <see cref="Jpeg.Decode"/> and <see cref="Jpeg.GetInfo(ReadOnlySpan{byte})"/> skip junk in front of the
        /// first SOI marker (some cameras and container formats prepend data). Set to require the data to start with it.
        /// </summary>
        public bool RequireSoiAtStart { get; set; }
    }

    /// <summary>Chroma resolution of an encoded color JPEG.</summary>
    public enum JpegSubsampling
    {
        /// <summary>4:2:0 below quality 90, 4:4:4 from 90 up.</summary>
        Auto = 0,
        /// <summary>Full resolution chroma.</summary>
        Chroma444,
        /// <summary>Chroma halved horizontally.</summary>
        Chroma422,
        /// <summary>Chroma halved in both directions.</summary>
        Chroma420,
    }

    public sealed class JpegEncodeOptions
    {
        /// <summary>Quality 1..100.</summary>
        public int Quality { get; set; } = 90;

        /// <summary>Chroma subsampling for color images (ignored for grayscale).</summary>
        public JpegSubsampling Subsampling { get; set; } = JpegSubsampling.Auto;

        /// <summary>Build Huffman tables for this image instead of using the standard ones (smaller files, slightly slower).</summary>
        public bool OptimizeHuffman { get; set; } = true;

        /// <summary>Write a progressive (multi scan) JPEG instead of a baseline one. Always uses optimized Huffman tables.</summary>
        public bool Progressive { get; set; }

        /// <summary>
        /// Emit a restart marker every this many MCUs (0 = none, at most 65535). In lossless mode the interval is
        /// rounded up to whole lines.
        /// </summary>
        public int RestartInterval { get; set; }

        /// <summary>
        /// Write lossless JPEG (predictive coding, T.81 Annex H) instead of a DCT based one: exact for UInt8 and UInt16
        /// Gray / Rgb images (an alpha channel is dropped) and for <see cref="ColorModel.Other"/> images with up to 4
        /// channels, which are stored as they are. Quality, subsampling and <see cref="Progressive"/> do not apply.
        /// </summary>
        public bool Lossless { get; set; }

        /// <summary>Lossless predictor 1..7 (1 = left neighbour, 2 = above, 4 = plane fit a + b - c, 7 = average; 0 picks 1).</summary>
        public int Predictor { get; set; }

        /// <summary>
        /// Sample precision in bits; 0 picks the natural one. DCT based JPEG knows 8 bit (UInt8 images) and 12 bit
        /// (UInt16 images whose values must lie in 0..4095). Lossless JPEG takes 2..16 (default 8 for UInt8, 16 for
        /// UInt16; the values have to fit).
        /// </summary>
        public int Precision { get; set; }

        /// <summary>Test hook: explicit sampling factors h, v for Y, Cb, Cr (1..4 each) instead of <see cref="Subsampling"/>.</summary>
        internal int[]? SamplingFactors { get; set; }
    }

    /// <summary>
    /// JPEG codec.
    /// <para>Decoding: baseline, extended sequential and progressive Huffman coded JPEG with 8 bit samples, any
    /// sampling factors, restart intervals; grayscale, YCbCr and RGB decode to Gray / Rgb, CMYK and YCCK (Adobe,
    /// stored inverted) are converted to Rgb with R = (1 - C)(1 - K). The inverse DCT, the 2:1 chroma upsampling
    /// filters and the color conversion use the same integer arithmetic as libjpeg, so the output is bit identical
    /// to libjpeg(-turbo) based decoders with default settings. JFIF density, EXIF (incl. orientation) and ICC
    /// profiles are returned as metadata. Not supported: 12 bit samples, lossless, hierarchical and arithmetic coded files.</para>
    /// <para>Encoding: baseline or progressive JPEG from 8 bit Gray or Rgb images (an alpha channel is dropped).</para>
    /// </summary>
    public static unsafe class Jpeg
    {
        /// <summary>Returns whether the data starts with a JPEG signature (needs at most 16 bytes).</summary>
        public static bool IsMatch(ReadOnlySpan<byte> header)
            => header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF;

        static readonly JpegDecodeOptions s_defaults = new JpegDecodeOptions();

        /// <summary>Offset of the SOI marker that starts the image.</summary>
        static int FindStart(ReadOnlySpan<byte> data, JpegDecodeOptions o)
        {
            if (IsMatch(data)) return 0;
            int i = o.RequireSoiAtStart ? -1 : data.IndexOf((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]);
            if (i < 0) throw new ImageFormatException("invalid JPEG: missing SOI marker");
            return i;
        }

        static JpegDecoder Open(byte* p, ReadOnlySpan<byte> data, JpegDecodeOptions o, bool headerOnly)
        {
            if (o.ScaleDenominator is not (1 or 2 or 4 or 8)) throw new ArgumentException("ScaleDenominator must be 1, 2, 4 or 8");
            int start = FindStart(data, o);
            var d = new JpegDecoder(p + start, data.Length - start) { ScaleDenominator = o.ScaleDenominator };
            d.Parse(headerOnly);
            return d;
        }

        /// <summary>What the decoder returns for this frame under the given options.</summary>
        static (JpegColorSpace Conversion, ColorModel Model, int Channels) Plan(JpegDecoder d, JpegDecodeOptions o)
        {
            int nc = d.Comps.Length;
            if (nc == 1) return (JpegColorSpace.Gray, ColorModel.Gray, 1);
            // lossless frames are never color transformed (there is no agreed convention, and it would not be lossless)
            var cs = d.Lossless ? JpegColorSpace.Raw : d.ColorSpace;
            switch (nc)
            {
                case 3:
                    if (cs == JpegColorSpace.YCbCr && !o.KeepYCbCr) return (cs, ColorModel.Rgb, 3);
                    return (JpegColorSpace.Raw, cs == JpegColorSpace.YCbCr ? ColorModel.Other : ColorModel.Rgb, 3);
                case 4:
                    if (cs == JpegColorSpace.Raw || (o.RawCmyk && (cs == JpegColorSpace.Cmyk || o.KeepYCbCr))) return (JpegColorSpace.Raw, ColorModel.Other, 4);
                    if (o.RawCmyk) return (JpegColorSpace.YcckToCmyk, ColorModel.Other, 4);
                    return (cs, ColorModel.Rgb, 3);
                default:
                    return (JpegColorSpace.Raw, ColorModel.Other, nc);
            }
        }

        /// <summary>Reads header level information without decoding pixel data (what <see cref="Decode"/> with default options returns).</summary>
        public static ImageInfo GetInfo(ReadOnlySpan<byte> data) => GetInfo(data, null);

        /// <summary>Reads header level information for a decode with the given options (scaled size, channel layout, orientation).</summary>
        public static ImageInfo GetInfo(ReadOnlySpan<byte> data, JpegDecodeOptions? options)
        {
            options ??= s_defaults;
            fixed (byte* p = data)
            {
                var d = Open(p, data, options, headerOnly: true);
                var (_, model, channels) = Plan(d, options);
                bool swap = options.ApplyOrientation && d.Metadata.Orientation >= 5;
                return new ImageInfo(ImageFormat.Jpeg, swap ? d.OutHeight : d.OutWidth, swap ? d.OutWidth : d.OutHeight, channels,
                    d.Precision > 8 ? SampleType.UInt16 : SampleType.UInt8, model);
            }
        }

        /// <summary>
        /// Decodes a complete JPEG file held in memory. 8 bit frames give UInt8 samples, deeper ones (12 bit DCT, lossless
        /// up to 16 bit) UInt16 holding the values as coded, e.g. 0..4095 for 12 bit - they are not scaled to the full
        /// 16 bit range. Files that end early decode as far as the data goes (the remainder is gray). The EXIF orientation
        /// is reported in the metadata and only applied on request.
        /// </summary>
        public static RawImage Decode(ReadOnlySpan<byte> data, JpegDecodeOptions? options = null)
        {
            options ??= s_defaults;
            fixed (byte* p = data)
            {
                var d = Open(p, data, options, headerOnly: false);
                var (cs, model, channels) = Plan(d, options);
                int w = d.OutWidth, h = d.OutHeight;
                int count = checked(w * h * channels);
                Array pixels;
                if (!d.Wide)
                {
                    var bytes = GC.AllocateUninitializedArray<byte>(count);
                    JpegPost.Assemble(d, bytes, w, h, cs);
                    pixels = bytes;
                }
                else
                {
                    var words = GC.AllocateUninitializedArray<ushort>(count);
                    JpegPost16.Assemble(d, words, w, h, cs);
                    if (d.Precision > 8) pixels = words;
                    else
                    {
                        // lossless with up to 8 bit
                        var bytes = GC.AllocateUninitializedArray<byte>(count);
                        for (int i = 0; i < count; i++) bytes[i] = (byte)words[i];
                        pixels = bytes;
                    }
                }
                var meta = d.HasMetadata ? d.Metadata : null;
                if (options.ApplyOrientation && meta != null && meta.Orientation > 1)
                {
                    pixels = pixels is byte[] b8
                        ? JpegMeta.Reorient(b8, ref w, ref h, channels, meta.Orientation)
                        : JpegMeta.Reorient((ushort[])pixels, ref w, ref h, channels, meta.Orientation);
                    if (meta.Orientation >= 5) (meta.DpiX, meta.DpiY) = (meta.DpiY, meta.DpiX);
                    meta.Orientation = 1;
                }
                return new RawImage(w, h, model, pixels, channels, meta);
            }
        }

        /// <summary>
        /// Decodes one JPEG compressed TIFF strip or tile. <paramref name="jpegTables"/> is the content of the JPEGTables
        /// tag (a tables-only stream, empty when absent), <paramref name="data"/> an abbreviated or complete stream whose
        /// frame may be larger than width x height; the top left width x height pixels are written interleaved with
        /// <paramref name="components"/> samples each. No color space guessing: YCbCr is converted to RGB only when asked
        /// (and the stream has 3 components), otherwise the components come out as stored. Thread safe.
        /// </summary>
        internal static void DecodeForTiff(ReadOnlySpan<byte> jpegTables, ReadOnlySpan<byte> data, int width, int height, int components, bool convertYCbCrToRgb, Span<byte> destination)
        {
            if (width <= 0 || height <= 0 || components <= 0 || destination.Length != (long)width * height * components)
                throw new ArgumentException("destination does not match width x height x components");
            fixed (byte* t = jpegTables)
            fixed (byte* p = data)
            {
                var d = new JpegDecoder(p, data.Length);
                if (jpegTables.Length > 0)
                {
                    var tables = new JpegDecoder(t, jpegTables.Length) { TablesOnly = true };
                    tables.Parse(headerOnly: false);
                    d.ImportTables(tables);
                }
                d.Parse(headerOnly: false);
                if (d.Comps.Length != components)
                    throw new ImageFormatException($"JPEG data in TIFF has {d.Comps.Length} components, expected {components}");
                if (d.Width < width || d.Height < height)
                    throw new ImageFormatException($"JPEG data in TIFF is {d.Width}x{d.Height}, smaller than the {width}x{height} strip or tile");
                var cs = components == 3 && convertYCbCrToRgb ? JpegColorSpace.YCbCr : components == 1 ? JpegColorSpace.Gray : JpegColorSpace.Raw;
                if (d.Wide) throw new ImageFormatException("JPEG data in TIFF is not an 8 bit DCT stream");
                JpegPost.Assemble(d, destination, width, height, cs);
            }
        }

        /// <summary>Encodes an image as JPEG to the stream (which only needs to be writable, not seekable).</summary>
        public static void Encode(RawImage image, Stream output, JpegEncodeOptions? options = null)
            => JpegEncoder.Encode(image, output, options ?? new JpegEncodeOptions());
    }
}
