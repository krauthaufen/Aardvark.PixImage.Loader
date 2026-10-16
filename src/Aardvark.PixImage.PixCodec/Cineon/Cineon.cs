using System;
using System.Collections.Generic;
using System.IO;
using Aardvark.ImageCodecs.CineonImpl;

namespace Aardvark.ImageCodecs
{
    /// <summary>One channel of a Cineon file (fields 1.14 - 1.21).</summary>
    public sealed record CineonChannel
    {
        /// <summary>Designator byte 0: 0 = universal metric, 1..254 vendor specific.</summary>
        public byte Metric { get; init; }
        /// <summary>
        /// Designator byte 1: with the universal metric 0 = B&amp;W, 1 / 2 / 3 = red / green / blue printing density,
        /// 4 / 5 / 6 = red / green / blue CCIR XA/11 (video).
        /// </summary>
        public byte Designator { get; init; }
        public int BitDepth { get; init; }
        public int PixelsPerLine { get; init; }
        public int Lines { get; init; }
        public float? MinimumData { get; init; }
        public float? MinimumQuantity { get; init; }
        public float? MaximumData { get; init; }
        public float? MaximumQuantity { get; init; }
    }

    /// <summary>
    /// The header of a Kodak Cineon file (4.5 draft file format): generic file, image, data format and origination information and
    /// the motion picture film information. Undefined numeric fields are null, undefined strings empty. Read with
    /// <see cref="Cineon.GetHeader"/>; passed in <see cref="CineonEncodeOptions.Header"/>, the descriptive fields are written.
    /// </summary>
    public sealed record CineonHeader
    {
        public bool BigEndian { get; init; } = true;
        public uint ImageDataOffset { get; init; }
        public uint? GenericHeaderSize { get; init; }
        public uint? IndustryHeaderSize { get; init; }
        public uint? UserDataSize { get; init; }
        public uint? FileSize { get; init; }
        public string Version { get; init; } = "";
        public string ImageFileName { get; init; } = "";
        /// <summary>Creation date as stored, "yyyy:mm:dd".</summary>
        public string CreationDate { get; init; } = "";
        /// <summary>Creation time as stored, "hh:mm:ssxxx" (xxx a time zone such as EST).</summary>
        public string CreationTimeOfDay { get; init; } = "";
        /// <summary>Cineon orientation code 0..7 as stored (it differs from DPX in codes 1 and 2); <see cref="ImageInfo.Orientation"/> has the EXIF equivalent.</summary>
        public int Orientation { get; init; }
        public IReadOnlyList<CineonChannel> Channels { get; init; } = Array.Empty<CineonChannel>();
        public (float X, float Y)? WhitePoint { get; init; }
        public (float X, float Y)? RedPrimary { get; init; }
        public (float X, float Y)? GreenPrimary { get; init; }
        public (float X, float Y)? BluePrimary { get; init; }
        public string Label { get; init; } = "";
        /// <summary>Data interleave: 0 pixel, 1 line, 2 channel.</summary>
        public int Interleave { get; init; }
        /// <summary>Packing code (field 1.29): low bits 0 = bit fields, 1/2 byte, 3/4 word, 5/6 longword cells (left / right justified); 0x80 = fields of several pixels per cell.</summary>
        public int Packing { get; init; }
        public bool Signed { get; init; }
        /// <summary>Image sense: true for a negative image.</summary>
        public bool Negative { get; init; }
        public uint? EndOfLinePadding { get; init; }
        public uint? EndOfChannelPadding { get; init; }
        public int? XOffset { get; init; }
        public int? YOffset { get; init; }
        public string SourceFileName { get; init; } = "";
        public string SourceDate { get; init; } = "";
        public string SourceTimeOfDay { get; init; } = "";
        public string InputDevice { get; init; } = "";
        public string InputDeviceModel { get; init; } = "";
        public string InputDeviceSerial { get; init; } = "";
        /// <summary>Input device pitch (samples per millimetre).</summary>
        public float? XDevicePitch { get; init; }
        /// <inheritdoc cref="XDevicePitch"/>
        public float? YDevicePitch { get; init; }
        /// <summary>Image gamma of the capture device.</summary>
        public float? Gamma { get; init; }
        // motion picture industry specific
        public byte? FilmManufacturerId { get; init; }
        public byte? FilmType { get; init; }
        public byte? PerfsOffset { get; init; }
        public uint? FilmPrefix { get; init; }
        public uint? FilmCount { get; init; }
        public string FilmFormat { get; init; } = "";
        public uint? FramePosition { get; init; }
        public float? FrameRate { get; init; }
        public string FrameAttribute { get; init; } = "";
        public string SlateInfo { get; init; } = "";
        /// <summary>The user defined area (after the 2048 byte header), null when there is none.</summary>
        public byte[]? UserData { get; init; }

        /// <summary>Creation date and time, null when missing or not parseable (time zone names other than UTC / GMT / Z give offset 0).</summary>
        public DateTimeOffset? CreationTime
            => DpxHeaderIo.ParseDate(CreationDate + (CreationTimeOfDay.Length > 0 ? ":" + CreationTimeOfDay : ""), out var t) ? t : null;
    }

    public sealed class CineonDecodeOptions
    {
        /// <summary>Rotate / mirror the pixels according to the orientation field (default) so that the image comes back upright.</summary>
        public bool ApplyOrientation { get; set; } = true;

        /// <summary>Upper bound for the image in pixels; larger files are rejected with <see cref="ImageNotSupportedException"/>.</summary>
        public long MaxPixels { get; set; } = 1L << 28;

        internal static readonly CineonDecodeOptions Default = new();
    }

    public sealed class CineonEncodeOptions
    {
        /// <summary>
        /// Bits per sample: 10 (default, the classic log encoding), 8 or 16. UInt8 samples are scaled to deeper depths, UInt16 samples
        /// are written as they are and have to fit (a 10 bit file takes UInt16 values 0..1023, what the decoder returns).
        /// </summary>
        public int BitDepth { get; set; } = 10;

        /// <summary>Byte order. Default: most significant byte first, the form of the specification.</summary>
        public bool BigEndian { get; set; } = true;

        /// <summary>
        /// Channel designator byte 1 for the red channel of an RGB image: 1 (default) records red / green / blue printing density
        /// (log film scans), 4 CCIR XA/11 video. Gray images are recorded as B&amp;W (0).
        /// </summary>
        public byte RgbDesignator { get; set; } = 1;

        /// <summary>Descriptive header fields to write (file name, dates, label, origination and film information, user data).</summary>
        public CineonHeader? Header { get; set; }
    }

    /// <summary>
    /// Kodak Cineon (.cin, 4.5 draft image file format): the 10 bit log film scan format DPX grew out of.
    /// <para>Decoding: 1 to 8 channels of equal size and depth (1..16 and 32 bits), pixel, line or channel interleave, all packing
    /// codes (bit fields, byte / word / longword cells, left or right justified, one or several pixels per cell), both byte orders,
    /// end-of-line and end-of-channel padding, signed samples, and the orientation (applied by default). One channel is Gray;
    /// three are Rgb, ordered by their designators (red, green, blue); other counts are <see cref="ColorModel.Other"/>.
    /// Samples keep their stored values: 10 bit as UInt16 0..1023, other depths up to 8 bit as UInt8 (1, 2 and 4 bit stretched to
    /// 0..255 as TIFF does), up to 16 bit as UInt16, 32 bit as UInt32 (Int8 / Int16 / Int32 when signed). Printing density is
    /// logarithmic: <see cref="ImageInfo.Transfer"/> is Unknown for it (and for B&amp;W), Gamma 0.45 for CCIR XA/11 video.</para>
    /// <para>8 bit pixel interleaved data is read with four samples per 32-bit cell, as ImageMagick writes and every reader expects,
    /// although the specification would put one pixel per cell for packing code 5. Lines start on a new cell; files written
    /// without that (OpenImageIO's reading) are recognised by their size.</para>
    /// <para>Encoding: Gray and Rgb at 10 (default), 8 or 16 bit, pixel interleaved, packing 5 (32-bit cells, left justified).</para>
    /// </summary>
    public static class Cineon
    {
        public static bool IsMatch(ReadOnlySpan<byte> header) => CineonCodec.IsMatch(header);

        public static ImageInfo GetInfo(ReadOnlySpan<byte> data, CineonDecodeOptions? options = null)
            => Dpx.Guard(data, d => CineonCodec.GetInfo(d, options ?? CineonDecodeOptions.Default), "Cineon");

        public static CineonHeader GetHeader(ReadOnlySpan<byte> data) => Dpx.Guard(data, CineonCodec.ReadHeader, "Cineon");

        public static RawImage Decode(ReadOnlySpan<byte> data, CineonDecodeOptions? options = null)
            => Dpx.Guard(data, d => CineonCodec.Decode(d, options ?? CineonDecodeOptions.Default), "Cineon");

        public static void Encode(RawImage image, Stream output, CineonEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            ArgumentNullException.ThrowIfNull(output);
            CineonCodec.Encode(image, output, options ?? new CineonEncodeOptions());
        }

        public static byte[] Encode(RawImage image, CineonEncodeOptions? options = null)
        {
            var ms = new MemoryStream();
            Encode(image, ms, options);
            return ms.ToArray();
        }
    }
}
