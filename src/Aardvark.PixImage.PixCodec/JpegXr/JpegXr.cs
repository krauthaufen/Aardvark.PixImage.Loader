using System;
using System.IO;
using Aardvark.ImageCodecs.JpegXrImpl;

namespace Aardvark.ImageCodecs
{
    public sealed class JpegXrDecodeOptions
    {
        /// <summary>
        /// Rotate / mirror the pixels according to the file's SPATIAL_XFRM_PRIMARY (default), so that the image comes back
        /// upright; the metadata then says orientation 1. False returns the pixels as coded, with the orientation (as an EXIF
        /// value) in the metadata.
        /// </summary>
        public bool ApplyOrientation { get; set; } = true;

        /// <summary>
        /// true (default): CMYK images (with or without alpha) are converted to RGB (naively, as TIFF does). false: they come
        /// back as their 4 (or 5) samples with <see cref="ColorModel.Other"/>.
        /// </summary>
        public bool ConvertToRgb { get; set; } = true;

        /// <summary>Divide premultiplied colours (32bppPBGRA, 64bppPRGBA, 128bppPRGBAFloat) by alpha (default), since RawImage alpha is straight.</summary>
        public bool Unpremultiply { get; set; } = true;

        /// <summary>Upper bound for the image in pixels; larger files are rejected with <see cref="ImageNotSupportedException"/>.</summary>
        public long MaxPixels { get; set; } = 1L << 30;

        /// <summary>Reconstruct and format large images on several threads (same result).</summary>
        public bool Multithreaded { get; set; } = true;

        internal static readonly JpegXrDecodeOptions Default = new();
    }

    /// <summary>
    /// JPEG XR (ITU-T T.832 | ISO/IEC 29199-2, formerly HD Photo / Windows Media Photo; .jxr, .wdp, .hdp).
    /// <para>Decoding: the tag based file format of Annex A (and bare codestreams), every pixel format of Table A.6, every
    /// codestream feature: spatial and frequency order, tiles (soft and hard), all overlap modes, windowing, all subband
    /// selections (DC only, no highpass, no flexbits, trimmed flexbits), per tile / band / component quantization with
    /// several QP sets per tile, interleaved and separate (planar) alpha, internal 4:2:0 / 4:2:2 / 4:4:4 / Y only / YUVK /
    /// n-component formats. Samples come back in the dense layout: UInt8 / UInt16 for unsigned integer formats (BGR orders
    /// as Rgb; 5 / 6 bit packed channels expanded to UInt8, 10 bit ones to UInt16), Float32 for fixed point (s2.13, s7.24)
    /// and float formats, Float16 for half floats, Float32 Rgb for RGBE, UInt8 0 / 255 Gray for BlackWhite, CMYK as Rgb (or
    /// stored, <see cref="JpegXrDecodeOptions.ConvertToRgb"/>), n-channel and YCC interchange formats as
    /// <see cref="ColorModel.Other"/> (YCC chroma replicated to the full size). Premultiplied alpha is undone. Metadata:
    /// resolution, ICC profile, XMP, EXIF and GPS directories, orientation (SPATIAL_XFRM_PRIMARY).</para>
    /// </summary>
    public static class JpegXr
    {
        /// <summary>Whether the data starts like a JPEG XR file (II 0xBC) or a bare JPEG XR codestream ("WMPHOTO").</summary>
        public static bool IsMatch(ReadOnlySpan<byte> header) => JxrContainer.IsMatch(header) || JxrContainer.IsCodestream(header);

        /// <summary>Reads header level information without decoding pixel data (what <see cref="Decode"/> returns).</summary>
        public static ImageInfo GetInfo(ReadOnlySpan<byte> data) => GetInfo(data, null);

        /// <inheritdoc cref="GetInfo(ReadOnlySpan{byte})"/>
        public static ImageInfo GetInfo(ReadOnlySpan<byte> data, JpegXrDecodeOptions? options)
        {
            options ??= JpegXrDecodeOptions.Default;
            var f = JxrFile.Open(data, true);
            var (ch, type, model, bits) = f.Plan(options);
            var info = new ImageInfo(ImageFormat.JpegXr, f.Cs.Ih.Width, f.Cs.Ih.Height, ch, type, model)
            {
                BitsPerSample = bits,
                DpiX = f.DpiX,
                DpiY = f.DpiY,
                HasIccProfile = f.Container?.Icc != null,
                Orientation = f.Orientation,
            };
            return options.ApplyOrientation ? ExifTags.Upright(info) : info;
        }

        /// <summary>Decodes a JPEG XR file held in memory (see the class summary for what is supported).</summary>
        public static RawImage Decode(ReadOnlySpan<byte> data, JpegXrDecodeOptions? options = null)
        {
            options ??= JpegXrDecodeOptions.Default;
            var f = JxrFile.Open(data, false);
            if ((long)f.Cs.Ih.Width * f.Cs.Ih.Height > options.MaxPixels)
                throw new ImageNotSupportedException($"JPEG XR: {f.Cs.Ih.Width} x {f.Cs.Ih.Height} exceeds MaxPixels ({options.MaxPixels})");
            var img = f.Decode(data, options);
            return options.ApplyOrientation && f.Orientation > 1 ? ImageOps.ApplyOrientation(img) : img;
        }

        public static RawImage Decode(Stream stream, JpegXrDecodeOptions? options = null) => Decode(ImageCodec.ReadAll(stream), options);
    }
}
