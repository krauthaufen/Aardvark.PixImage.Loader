using System;
using System.IO;
using Aardvark.ImageCodecs.XbmImpl;

namespace Aardvark.ImageCodecs
{
    public sealed class XbmDecodeOptions
    {
        /// <summary>Upper bound for the image in pixels; larger files are rejected with <see cref="ImageNotSupportedException"/>.</summary>
        public long MaxPixels { get; set; } = 1L << 28;

        internal static readonly XbmDecodeOptions Default = new();
    }

    public sealed class XbmEncodeOptions
    {
        /// <summary>The C identifier prefix of the defines and the array (default "image"); other characters become '_'.</summary>
        public string Name { get; set; } = "image";

        /// <summary>Write the X10 form (an array of 16 bit shorts) instead of the X11 form (unsigned chars).</summary>
        public bool X10 { get; set; }

        /// <summary>Hotspot (cursor click point) written as _x_hot / _y_hot defines, if set.</summary>
        public (int X, int Y)? HotSpot { get; set; }
    }

    /// <summary>
    /// X BitMap: the C source form written by X10 (<c>static short name_bits[]</c>, 16 bit words) and X11 (<c>static
    /// [unsigned] char name_bits[]</c>), with <c>#define name_width</c> / <c>name_height</c> and optional hotspot defines
    /// (<see cref="GetHotSpot"/>). Bits are least significant first, rows padded to whole bytes / words, 1 is black. Results
    /// are UInt8 Gray 0 / 255. Writes either form from any UInt8 image (luma at or above 128, or alpha below 128, is white).
    /// <para>The format has no binary signature: <see cref="IsMatch"/> requires the text to start (after white space and
    /// comments) with the width and height defines.</para>
    /// </summary>
    public static class Xbm
    {
        public static bool IsMatch(ReadOnlySpan<byte> header) => XbmCodec.IsMatch(header);

        public static ImageInfo GetInfo(ReadOnlySpan<byte> data) => XbmCodec.GetInfo(data);

        /// <summary>The hotspot given by the _x_hot / _y_hot defines, or null.</summary>
        public static (int X, int Y)? GetHotSpot(ReadOnlySpan<byte> data) => XbmCodec.ParseHeader(data).Hot;

        public static RawImage Decode(ReadOnlySpan<byte> data, XbmDecodeOptions? options = null)
            => XbmCodec.Decode(data, options ?? XbmDecodeOptions.Default);

        public static void Encode(RawImage image, Stream output, XbmEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            ArgumentNullException.ThrowIfNull(output);
            output.Write(XbmCodec.Encode(image, options ?? new XbmEncodeOptions()));
        }

        public static byte[] Encode(RawImage image, XbmEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            return XbmCodec.Encode(image, options ?? new XbmEncodeOptions());
        }
    }
}
