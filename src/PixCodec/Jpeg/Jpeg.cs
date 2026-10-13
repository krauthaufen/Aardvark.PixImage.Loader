using System;
using System.IO;
using PixCodec.JpegImpl;

namespace PixCodec
{
    /// <summary>Reserved for decoder settings; there are none yet.</summary>
    public sealed class JpegDecodeOptions
    {
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

        /// <summary>Emit a restart marker every this many MCUs (0 = none, at most 65535).</summary>
        public int RestartInterval { get; set; }
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

        /// <summary>Reads header level information without decoding pixel data.</summary>
        public static ImageInfo GetInfo(ReadOnlySpan<byte> data)
        {
            fixed (byte* p = data)
            {
                var d = new JpegDecoder(p, data.Length);
                d.Parse(headerOnly: true);
                bool gray = d.Comps.Length == 1;
                return new ImageInfo(ImageFormat.Jpeg, d.Width, d.Height, gray ? 1 : 3, SampleType.UInt8, gray ? ColorModel.Gray : ColorModel.Rgb);
            }
        }

        /// <summary>
        /// Decodes a complete JPEG file held in memory to 8 bit Gray or Rgb. Files that end early decode as far as the
        /// data goes (the remainder is gray); EXIF orientation is reported in the metadata, not applied.
        /// </summary>
        public static RawImage Decode(ReadOnlySpan<byte> data, JpegDecodeOptions? options = null)
        {
            fixed (byte* p = data)
            {
                var d = new JpegDecoder(p, data.Length);
                d.Parse(headerOnly: false);
                bool gray = d.Comps.Length == 1;
                var pixels = GC.AllocateUninitializedArray<byte>(checked(d.Width * d.Height * (gray ? 1 : 3)));
                JpegPost.Assemble(d, pixels);
                return new RawImage(d.Width, d.Height, gray ? ColorModel.Gray : ColorModel.Rgb, pixels, 0, d.HasMetadata ? d.Metadata : null);
            }
        }

        /// <summary>Encodes an image as JPEG to the stream (which only needs to be writable, not seekable).</summary>
        public static void Encode(RawImage image, Stream output, JpegEncodeOptions? options = null)
            => JpegEncoder.Encode(image, output, options ?? new JpegEncodeOptions());
    }
}
