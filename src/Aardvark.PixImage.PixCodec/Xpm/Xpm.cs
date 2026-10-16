using System;
using System.IO;
using Aardvark.ImageCodecs.XpmImpl;

namespace Aardvark.ImageCodecs
{
    public sealed class XpmDecodeOptions
    {
        /// <summary>Upper bound for the image in pixels; larger files are rejected with <see cref="ImageNotSupportedException"/>.</summary>
        public long MaxPixels { get; set; } = 1L << 28;

        internal static readonly XpmDecodeOptions Default = new();
    }

    public sealed class XpmEncodeOptions
    {
        /// <summary>The C identifier of the array (default "image"); other characters become '_'.</summary>
        public string Name { get; set; } = "image";
    }

    /// <summary>
    /// X PixMap: XPM 3 (the C source form starting with <c>/* XPM */</c>) and XPM 2 (<c>! XPM2</c>, plain lines), any number of
    /// characters per pixel (up to 8), hotspot and extensions (skipped). Of a colour's visuals the colour one (c) is used,
    /// else gray (g, g4) or mono (m). Colours are <c>#</c> followed by 1 to 4 hex digits per component (scaled to 8 bits,
    /// as ImageMagick does), X11 colour names (rgb.txt, case and spaces ignored), and <c>None</c> for transparent. Results are
    /// UInt8 Rgb, Rgba when a colour is None, Gray / GrayAlpha when every colour is gray. Writes UInt8 images as XPM 3 with
    /// <c>#RRGGBB</c> colours (pixels with alpha below 128 become None), using as many characters per pixel as the colour
    /// count needs.
    /// <para>The format has no binary signature: <see cref="IsMatch"/> requires the text to start with the XPM comment.</para>
    /// </summary>
    public static class Xpm
    {
        public static bool IsMatch(ReadOnlySpan<byte> header) => XpmCodec.IsMatch(header);

        public static ImageInfo GetInfo(ReadOnlySpan<byte> data) => XpmCodec.GetInfo(data);

        public static RawImage Decode(ReadOnlySpan<byte> data, XpmDecodeOptions? options = null)
            => XpmCodec.Decode(data, options ?? XpmDecodeOptions.Default);

        public static void Encode(RawImage image, Stream output, XpmEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            ArgumentNullException.ThrowIfNull(output);
            output.Write(XpmCodec.Encode(image, options ?? new XpmEncodeOptions()));
        }

        public static byte[] Encode(RawImage image, XpmEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            return XpmCodec.Encode(image, options ?? new XpmEncodeOptions());
        }
    }
}
