using System;
using System.IO;
using Aardvark.ImageCodecs.DpxImpl;

namespace Aardvark.ImageCodecs
{
    public sealed class DpxDecodeOptions
    {
        /// <summary>
        /// Rotate / mirror the pixels according to the image orientation field (default), so that the image comes back upright;
        /// its metadata then says orientation 1. False returns the pixels as stored with the EXIF equivalent of the DPX
        /// orientation in the metadata.
        /// </summary>
        public bool ApplyOrientation { get; set; } = true;

        /// <summary>
        /// The image element to decode (0..7, as in <see cref="DpxHeader.Elements"/>). Null (the default) takes the first colour
        /// element (RGB, RGBA, ABGR or Y'CbCr), else combines separate R, G, B (and A) elements, else the first element that can be
        /// decoded.
        /// </summary>
        public int? ElementIndex { get; set; }

        /// <summary>Upper bound for an image element in pixels; larger ones are rejected with <see cref="ImageNotSupportedException"/>.</summary>
        public long MaxPixels { get; set; } = 1L << 28;

        /// <summary>Decode the lines of large uncompressed elements on several threads.</summary>
        public bool Parallel { get; set; } = true;

        internal static readonly DpxDecodeOptions Default = new();
    }

    public sealed class DpxEncodeOptions
    {
        /// <summary>
        /// Bits per component: 8, 10, 12, 16 (integer), 32 or 64 (float). Null (the default) takes it from the sample type: 8 for
        /// UInt8, 16 for UInt16, 32 for Float16 / Float32, 64 for Float64. UInt8 samples are scaled to deeper integer depths;
        /// UInt16 samples are written as they are and have to fit (a 10 bit file takes UInt16 values 0..1023, which is what the
        /// decoder returns: write a decoded 10 bit image back with BitDepth = 10).
        /// </summary>
        public int? BitDepth { get; set; }

        /// <summary>Packing of 10 and 12 bit components (other depths need none). Default: filled method A, what every tool reads.</summary>
        public DpxPacking Packing { get; set; } = DpxPacking.FilledA;

        /// <summary>Byte order of the file. Default: most significant byte first ("SDPX"), the traditional form.</summary>
        public bool BigEndian { get; set; } = true;

        /// <summary>
        /// The transfer characteristic to record. Null (the default) derives it from <see cref="ImageMetadata.Transfer"/>: Linear
        /// (also for float samples without a tag) as linear, Gamma as unspecified video with the television gamma field set, and
        /// sRGB / unknown as user defined (DPX has no sRGB code).
        /// </summary>
        public DpxTransfer? Transfer { get; set; }

        /// <summary>The colorimetric specification to record. Null (the default) pairs it with the transfer characteristic.</summary>
        public DpxColorimetric? Colorimetric { get; set; }

        /// <summary>
        /// The descriptor of a one channel image: <see cref="DpxDescriptor.Luma"/> (default), Red, Green, Blue, Alpha, Depth or
        /// UserDefined. Other images get theirs from the colour model (RGB, RGBA; GrayAlpha is written as RGBA; Other with 2..8
        /// channels as the user defined 2..8 component descriptors).
        /// </summary>
        public DpxDescriptor? Descriptor { get; set; }

        /// <summary>
        /// Descriptive header fields to write (file name, creation time, creator, project, copyright, image source, film and
        /// television information, user data), e.g. those of <see cref="Dpx.GetHeader"/>. The structural fields are always those
        /// of the image. Without it the creator is "Aardvark.PixImage.PixCodec" and everything else is left undefined.
        /// </summary>
        public DpxHeader? Header { get; set; }
    }

    /// <summary>
    /// SMPTE 268M DPX (Digital Moving-Picture Exchange, versions 1.0 and 2.0), both byte orders.
    /// <para>Decoding: descriptors user defined (one component), R, G, B, A, luma and depth (each as Gray), RGB, RGBA, ABGR (as
    /// Rgba), Cb Y Cr Y 4:2:2, Cb Y A Cr Y A 4:2:2:4, Cb Y Cr 4:4:4 and Cb Y Cr A 4:4:4:4 (converted to RGB / RGBA, see below),
    /// and the user defined 2..8 component descriptors (as <see cref="ColorModel.Other"/>); 1, 8, 10, 12, 16 bit integer and 32,
    /// 64 bit float components, packed / filled method A / filled method B, run length encoding (integer depths), signed
    /// components (as Int8 / Int16), end-of-line / end-of-image padding, data offsets, several image elements
    /// (<see cref="DecodeAll"/>), and the orientation field (applied by default).</para>
    /// <para>Sample types: 8 bit decodes to UInt8, 10, 12 and 16 bit to UInt16 holding the stored values (10 bit 0..1023, 12 bit
    /// 0..4095, as TIFF does with such depths), 1 bit to UInt8 0 / 255, 32 bit to Float32, 64 bit to Float64.</para>
    /// <para>Y'CbCr: BT.709 coefficients for SMPTE 274M / ITU-R 709 colorimetry (or transfer), BT.601 for ITU-R 601 and composite
    /// video, else by size (709 from 1280 pixels per line). Video range (16..235 / 16..240 at 8 bit) unless the element's
    /// reference codes give another range. The result is R'G'B' over the full code range of the bit depth.</para>
    /// <para>Transfer: linear and linear depth are <see cref="TransferFunction.Linear"/>; the video characteristics are
    /// <see cref="TransferFunction.Gamma"/> with the television header's gamma (or 0.45, PAL 1/2.8) as the exponent; printing
    /// density, logarithmic and user defined are Unknown (float data without a characteristic is Linear). The raw codes are in
    /// <see cref="DpxImageElement.Transfer"/> / <see cref="DpxImageElement.Colorimetric"/> of <see cref="GetHeader"/>.</para>
    /// <para>Line padding: SMPTE 268M breaks the 32-bit packing at every line end; for 8 bit lines ImageMagick follows that,
    /// OpenImageIO does not, and nobody pads 16 bit (and 12 bit filled) lines. Without an end-of-line padding field the decoder
    /// takes the layout the data size fits. 1 bit lines are read as an LSB first stream through 32-bit words (the standard's
    /// "left-most pixel in the least significant bit").</para>
    /// <para>Encoding: Gray, GrayAlpha (as RGBA), Rgb, Rgba and Other (2..8 channels) at 8, 10, 12, 16 bit (UInt8 / UInt16) and 32,
    /// 64 bit float; 10 / 12 bit filled method A (default), filled method B or packed; either byte order; data at offset 8192.
    /// 8 bit lines are padded to 32 bits with the padding recorded as end-of-line padding, so that all readers agree.</para>
    /// </summary>
    public static class Dpx
    {
        /// <summary>Whether the data starts with a DPX magic number ("SDPX" or "XPDS").</summary>
        public static bool IsMatch(ReadOnlySpan<byte> header) => DpxDecoder.IsMatch(header);

        /// <summary>Reads header level information about what <see cref="Decode"/> returns (only the 2048 byte header is needed).</summary>
        public static ImageInfo GetInfo(ReadOnlySpan<byte> data, DpxDecodeOptions? options = null)
            => Guard(data, d => DpxDecoder.GetInfo(d, options ?? DpxDecodeOptions.Default));

        /// <summary>Reads all header fields (only the 2048 byte header and the user data are needed).</summary>
        public static DpxHeader GetHeader(ReadOnlySpan<byte> data) => Guard(data, DpxDecoder.ReadHeader);

        /// <summary>Decodes one image element (see <see cref="DpxDecodeOptions.ElementIndex"/>).</summary>
        public static RawImage Decode(ReadOnlySpan<byte> data, DpxDecodeOptions? options = null)
            => Guard(data, d => DpxDecoder.Decode(d, options ?? DpxDecodeOptions.Default));

        /// <summary>Decodes every image element that this library can decode, in header order.</summary>
        public static RawImage[] DecodeAll(ReadOnlySpan<byte> data, DpxDecodeOptions? options = null)
            => Guard(data, d => DpxDecoder.DecodeAll(d, options ?? DpxDecodeOptions.Default));

        public static void Encode(RawImage image, Stream output, DpxEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            ArgumentNullException.ThrowIfNull(output);
            DpxEncoder.Encode(image, output, options ?? new DpxEncodeOptions());
        }

        public static byte[] Encode(RawImage image, DpxEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            var ms = new MemoryStream((int)Math.Min(int.MaxValue / 2, 8192 + (long)image.Width * image.Height * image.Channels * RawImage.BytesPerSample(image.SampleType)));
            Encode(image, ms, options);
            return ms.ToArray();
        }

        internal delegate T SpanFunc<T>(ReadOnlySpan<byte> data);

        /// <summary>Whatever a corrupt file manages to trip inside the decoder surfaces as ImageFormatException.</summary>
        internal static T Guard<T>(ReadOnlySpan<byte> data, SpanFunc<T> f, string format = "DPX")
        {
            try { return f(data); }
            catch (AggregateException a) when (a.InnerException is ImageFormatException or ImageNotSupportedException) { throw a.InnerException; }
            catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException or InvalidCastException
                                       or DivideByZeroException or AggregateException)
            {
                throw new ImageFormatException($"{format}: corrupt data", e);
            }
        }
    }
}
