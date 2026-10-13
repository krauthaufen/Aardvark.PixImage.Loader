using System;
using System.Collections.Generic;
using System.IO;

namespace PixCodec
{
    /// <summary>What a frame returned by the PNG animation API carries.</summary>
    public enum PngFrameMode
    {
        /// <summary>Every frame is the complete canvas as a viewer would show it (Rgba, UInt8 or UInt16), dispose and blend applied.</summary>
        Composited = 0,
        /// <summary>Every frame is just its own sub-image, decoded like a still PNG; nothing is composited.</summary>
        Raw = 1,
    }

    /// <summary>APNG dispose_op: what happens to the frame's region before the next frame is drawn.</summary>
    public enum PngDisposal : byte
    {
        /// <summary>Leave the frame in place.</summary>
        None = 0,
        /// <summary>Clear the region to fully transparent black.</summary>
        Background = 1,
        /// <summary>Restore what was there before the frame was drawn (on the first frame this acts like <see cref="Background"/>).</summary>
        Previous = 2,
    }

    /// <summary>APNG blend_op: how the frame is drawn into its region.</summary>
    public enum PngBlend : byte
    {
        /// <summary>The frame replaces the region, alpha included.</summary>
        Source = 0,
        /// <summary>The frame is alpha-composited over the region.</summary>
        Over = 1,
    }

    public sealed class PngDecodeOptions
    {
        /// <summary>
        /// Verify the CRC of critical chunks (IHDR, PLTE, IDAT, IEND), drop ancillary chunks whose CRC is wrong, and
        /// check the Adler-32 of the pixel stream. Turning it off saves two passes over the data and lets images
        /// decode whose checksums are damaged or missing.
        /// </summary>
        public bool VerifyChecksums { get; set; } = true;

        /// <summary>
        /// When set, a tRNS chunk is ignored: gray/RGB images keep their channel count and palette images decode
        /// to Rgb. The default (false) expands transparency into an alpha channel.
        /// </summary>
        public bool IgnoreTransparency { get; set; }

        /// <summary>
        /// When set, palette images are returned as their raw indices (UInt8 Gray, one index per pixel, not scaled)
        /// instead of being expanded to Rgb/Rgba. Fetch the colors with <see cref="Png.ReadPalette"/>.
        /// Has no effect on other color types.
        /// </summary>
        public bool KeepIndexed { get; set; }

        /// <summary>
        /// A pixel of a palette image that refers to an entry beyond the PLTE chunk makes the file invalid
        /// (<see cref="ImageFormatException"/>). When this is set such pixels decode as opaque black instead.
        /// </summary>
        public bool AllowInvalidPaletteIndices { get; set; }

        /// <summary>Content of the frames returned by the animation API. <see cref="Png.Decode"/> ignores it.</summary>
        public PngFrameMode FrameMode { get; set; } = PngFrameMode.Composited;
    }

    /// <summary>Row filter used by the PNG encoder.</summary>
    public enum PngFilterMode : byte
    {
        /// <summary><see cref="Adaptive"/> for 8/16 bit gray and true color data, <see cref="None"/> for palette output.</summary>
        Auto = 0,
        None,
        Sub,
        Up,
        Average,
        Paeth,
        /// <summary>Per row, the filter with the smallest sum of absolute (signed) residuals.</summary>
        Adaptive,
    }

    public sealed class PngEncodeOptions
    {
        /// <summary>zlib level 0..9.</summary>
        public int CompressionLevel { get; set; } = 6;

        /// <summary>Row filter strategy. Fixed filters are faster to encode, <see cref="PngFilterMode.Adaptive"/> usually compresses best.</summary>
        public PngFilterMode Filter { get; set; } = PngFilterMode.Auto;

        /// <summary>Write the image Adam7 interlaced (larger files; lets viewers show a coarse preview early).</summary>
        public bool Interlace { get; set; }

        /// <summary>
        /// Write a palette (color type 3) image when an 8 bit Rgb/Rgba image has at most 256 distinct colors.
        /// Lossless: decodes back to the same pixels and color model. Not applied to animations.
        /// </summary>
        public bool AutoPalette { get; set; }

        /// <summary>
        /// Write an 8 bit Gray image with 1, 2 or 4 bits per pixel when every sample is representable at that depth
        /// (multiples of 255, 85 or 17). Lossless: decodes back to the same UInt8 pixels. Not applied to animations.
        /// </summary>
        public bool ReduceGrayDepth { get; set; }

        /// <summary>
        /// Write a GrayAlpha/Rgba image (8 or 16 bit) without its alpha channel, marking one color as transparent
        /// with a tRNS chunk, when that is lossless: alpha is only ever 0 or the maximum, all transparent pixels have
        /// the same color and no opaque pixel has it. Decodes back to the same pixels and color model.
        /// Not applied to animations.
        /// </summary>
        public bool ColorKeyTransparency { get; set; }

        /// <summary>Write gAMA / pHYs / iCCP / eXIf chunks from <see cref="RawImage.Metadata"/>.</summary>
        public bool WriteMetadata { get; set; } = true;

        /// <summary>PNG specific chunks to write: sRGB, cHRM, sBIT, bKGD, tIME and text.</summary>
        public PngMetadata? Metadata { get; set; }

        /// <summary>Animation only: number of times to play, 0 = forever.</summary>
        public int LoopCount { get; set; }

        /// <summary>
        /// Animation only: store just the bounding rectangle of the pixels that differ from the previous frame.
        /// The decoded animation is the same either way.
        /// </summary>
        public bool FrameDifferencing { get; set; } = true;
    }

    /// <summary>sRGB chunk: the image is in sRGB and should be rendered with this intent.</summary>
    public enum PngRenderingIntent : byte
    {
        Perceptual = 0,
        RelativeColorimetric = 1,
        Saturation = 2,
        AbsoluteColorimetric = 3,
    }

    /// <summary>cHRM chunk: CIE 1931 xy chromaticities of the white point and the primaries (stored with 5 decimals).</summary>
    public readonly record struct PngChromaticities(
        double WhiteX, double WhiteY, double RedX, double RedY, double GreenX, double GreenY, double BlueX, double BlueY);

    /// <summary>
    /// bKGD chunk: suggested background color, in the scale of the decoded samples (0..255, or 0..65535 for 16 bit
    /// images). Gray images have R = G = B. For palette images <see cref="PaletteIndex"/> is the stored index and
    /// R, G, B its color; otherwise it is -1.
    /// </summary>
    public readonly record struct PngBackground(ushort R, ushort G, ushort B, int PaletteIndex = -1);

    /// <summary>
    /// A tEXt, zTXt or iTXt chunk. Keywords are 1..79 Latin-1 characters. Text of tEXt/zTXt is Latin-1; anything else
    /// needs <see cref="International"/> (iTXt, UTF-8), which the encoder switches to by itself when required.
    /// </summary>
    public sealed record PngTextEntry(string Keyword, string Text)
    {
        /// <summary>Stored as iTXt.</summary>
        public bool International { get; init; }
        /// <summary>Stored zlib compressed (zTXt, or iTXt with the compression flag).</summary>
        public bool Compressed { get; init; }
        /// <summary>iTXt language tag (e.g. "en-GB"), ASCII.</summary>
        public string LanguageTag { get; init; } = "";
        /// <summary>iTXt keyword translated into the language of the text.</summary>
        public string TranslatedKeyword { get; init; } = "";
    }

    /// <summary>
    /// PNG specific ancillary information that has no place in <see cref="ImageMetadata"/>.
    /// Read it with <see cref="Png.ReadMetadata"/>, write it through <see cref="PngEncodeOptions.Metadata"/>.
    /// </summary>
    public sealed class PngMetadata
    {
        /// <summary>sRGB chunk.</summary>
        public PngRenderingIntent? SrgbIntent { get; set; }
        /// <summary>cHRM chunk.</summary>
        public PngChromaticities? Chromaticities { get; set; }
        /// <summary>
        /// sBIT chunk: number of significant bits per channel of the stored color type (1 entry for gray, 2 for
        /// gray+alpha, 3 for RGB and palette, 4 for RGBA). The encoder drops surplus alpha entries and omits the chunk
        /// when there are too few.
        /// </summary>
        public byte[]? SignificantBits { get; set; }
        /// <summary>bKGD chunk.</summary>
        public PngBackground? Background { get; set; }
        /// <summary>tIME chunk: time of the last modification of the image (UTC).</summary>
        public DateTime? LastModified { get; set; }
        /// <summary>tEXt, zTXt and iTXt chunks in file order.</summary>
        public List<PngTextEntry> Text { get; } = new();

        public bool IsEmpty => SrgbIntent == null && Chromaticities == null && SignificantBits == null && Background == null
                               && LastModified == null && Text.Count == 0;
    }

    /// <summary>One frame handed to <see cref="Png.EncodeAnimation(IEnumerable{PngInputFrame}, Stream, PngEncodeOptions?)"/>:
    /// the complete picture to show, and for how long as a fraction of a second (default 1/10 s).</summary>
    public readonly record struct PngInputFrame(RawImage Image, int DelayNumerator = 1, int DelayDenominator = 10)
    {
        /// <summary>A frame shown for the given number of milliseconds.</summary>
        public static PngInputFrame FromMilliseconds(RawImage image, int delayMs) => new(image, delayMs, 1000);
    }

    /// <summary>File level information about a PNG seen as an animation.</summary>
    public class PngAnimationInfo
    {
        /// <summary>Canvas size (the size of the PNG image).</summary>
        public int Width { get; internal set; }
        public int Height { get; internal set; }
        /// <summary>Number of animation frames; 1 for a PNG that is not animated.</summary>
        public int FrameCount { get; internal set; }
        /// <summary>The file is an APNG (has an acTL chunk).</summary>
        public bool IsAnimated { get; internal set; }
        /// <summary>Number of times to play, 0 = forever (raw num_plays). 0 for a PNG that is not animated.</summary>
        public int LoopCount { get; internal set; }
        /// <summary>
        /// The image <see cref="Png.Decode"/> returns is also the first frame. When false the default image is a
        /// fallback for viewers without animation support and is not part of the animation.
        /// </summary>
        public bool DefaultImageIsFirstFrame { get; internal set; }
        /// <summary>Sample type of the frames: UInt16 for 16 bit files, else UInt8.</summary>
        public SampleType SampleType { get; internal set; }
    }

    public sealed class PngAnimation : PngAnimationInfo
    {
        public IReadOnlyList<PngFrame> Frames { get; internal set; } = Array.Empty<PngFrame>();
    }

    public sealed class PngFrame
    {
        public int Index { get; internal set; }
        /// <summary>Frame region on the canvas.</summary>
        public int X { get; internal set; }
        public int Y { get; internal set; }
        public int Width { get; internal set; }
        public int Height { get; internal set; }
        /// <summary>Raw delay fraction in seconds. A denominator of 0 means 100.</summary>
        public int DelayNumerator { get; internal set; }
        public int DelayDenominator { get; internal set; }
        /// <summary>Delay in milliseconds. No browser-style clamping of tiny values is applied.</summary>
        public double DelayMs => DelayNumerator * 1000.0 / (DelayDenominator == 0 ? 100 : DelayDenominator);
        public PngDisposal Disposal { get; internal set; }
        public PngBlend Blend { get; internal set; }
        /// <summary>
        /// <see cref="PngFrameMode.Composited"/>: the whole canvas after this frame was drawn, Rgba.
        /// <see cref="PngFrameMode.Raw"/>: the frame's own Width x Height pixels in the color model
        /// <see cref="Png.Decode"/> would produce for the file.
        /// </summary>
        public RawImage Image { get; internal set; } = null!;
    }

    /// <summary>
    /// PNG codec, including APNG animations.
    /// <para>
    /// Animation rules: the canvas starts fully transparent black. Frames are drawn with their blend operation and
    /// disposed as the file says; "over" blending uses exactly rounded straight-alpha compositing. Structural errors in
    /// the animation chunks (sequence numbers, frame regions outside the canvas, a frame count that does not match
    /// acTL) throw <see cref="ImageFormatException"/> from the animation API only; <see cref="Decode"/> never looks at them.
    /// </para>
    /// </summary>
    public static class Png
    {
        /// <summary>Returns whether the data starts with a PNG signature (needs at most 16 bytes).</summary>
        public static bool IsMatch(ReadOnlySpan<byte> header)
            => header.Length >= 8 && header.Slice(0, 8).SequenceEqual(PngChunks.Signature);

        /// <summary>
        /// Reads header level information without decoding pixel data. FrameCount is the frame count announced by
        /// an APNG's acTL chunk, 1 for a plain PNG.
        /// </summary>
        public static ImageInfo GetInfo(ReadOnlySpan<byte> data) => PngDecoder.GetInfo(data);

        /// <summary>Decodes a complete PNG file held in memory. For an APNG this is the default image.</summary>
        public static RawImage Decode(ReadOnlySpan<byte> data, PngDecodeOptions? options = null)
            => PngDecoder.Decode(data, options ?? DefaultDecode);

        /// <summary>
        /// Returns the palette of a color type 3 PNG as RGBA quadruples (4 bytes per entry, alpha from tRNS or 255),
        /// or null when the image has no palette. Reads only the chunks in front of the pixel data.
        /// </summary>
        public static byte[]? ReadPalette(ReadOnlySpan<byte> data) => PngDecoder.ReadPalette(data);

        /// <summary>
        /// Reads the PNG specific ancillary chunks (sRGB, cHRM, sBIT, bKGD, tIME, text) of the whole file without
        /// decoding pixel data. Chunks with a wrong CRC or malformed content are skipped.
        /// </summary>
        public static PngMetadata ReadMetadata(ReadOnlySpan<byte> data)
            => PngChunks.Parse(data, DefaultDecode, PngChunks.ParseFlags.PngMetadata).PngMeta ?? new PngMetadata();

        /// <summary>Reads the animation properties without decoding pixel data. Works for plain PNGs too (one frame).</summary>
        public static PngAnimationInfo GetAnimationInfo(ReadOnlySpan<byte> data)
        {
            var info = new PngAnimationInfo();
            new PngAnimationDecoder(data, null).FillInfo(info);
            return info;
        }

        /// <summary>
        /// Decodes all frames of an APNG. A PNG that is not animated yields its image as the only frame.
        /// See <see cref="PngDecodeOptions.FrameMode"/> for what each frame holds.
        /// </summary>
        public static PngAnimation DecodeAnimation(ReadOnlySpan<byte> data, PngDecodeOptions? options = null)
        {
            var dec = new PngAnimationDecoder(data, options);
            var frames = new List<PngFrame>();
            while (dec.Next(data) is { } f) frames.Add(f);
            var anim = new PngAnimation { Frames = frames };
            dec.FillInfo(anim);
            return anim;
        }

        /// <summary>
        /// Decodes frame by frame, so that a long animation never has to be held in memory at once. Every frame owns
        /// its pixels. Use <see cref="GetAnimationInfo"/> for the loop count and frame count. The file structure is
        /// validated on the first MoveNext.
        /// </summary>
        public static IEnumerable<PngFrame> EnumerateFrames(ReadOnlyMemory<byte> data, PngDecodeOptions? options = null)
        {
            var dec = new PngAnimationDecoder(data.Span, options);
            while (dec.Next(data.Span) is { } f) yield return f;
        }

        /// <summary>Encodes an image as PNG to the stream (which only needs to be writable, not seekable).</summary>
        public static void Encode(RawImage image, Stream output, PngEncodeOptions? options = null)
            => PngEncoder.Encode(image, output, options ?? DefaultEncode);

        /// <summary>Encodes an image as PNG into a new array.</summary>
        public static byte[] Encode(RawImage image, PngEncodeOptions? options = null)
        {
            using var ms = new MemoryStream();
            PngEncoder.Encode(image, ms, options ?? DefaultEncode);
            return ms.ToArray();
        }

        /// <summary>
        /// Encodes a palette image from explicit indices and palette, the counterpart of
        /// <see cref="PngDecodeOptions.KeepIndexed"/> and <see cref="ReadPalette"/>.
        /// <paramref name="indices"/> is a UInt8 Gray image of palette indices; <paramref name="paletteRgba"/> holds
        /// 1..256 RGBA quadruples and is stored in exactly that order. A tRNS chunk is written when any alpha is not
        /// 255. The bit depth is the smallest (1, 2, 4, 8) that fits the palette size.
        /// </summary>
        public static void EncodeIndexed(RawImage indices, ReadOnlySpan<byte> paletteRgba, Stream output, PngEncodeOptions? options = null)
            => PngEncoder.EncodeIndexed(indices, paletteRgba, output, options ?? DefaultEncode);

        public static byte[] EncodeIndexed(RawImage indices, ReadOnlySpan<byte> paletteRgba, PngEncodeOptions? options = null)
        {
            using var ms = new MemoryStream();
            PngEncoder.EncodeIndexed(indices, paletteRgba, ms, options ?? DefaultEncode);
            return ms.ToArray();
        }

        /// <summary>
        /// Encodes an APNG. All frames must have the same size, sample type (UInt8/UInt16) and color model and are
        /// complete pictures; the encoder picks the frame regions itself. The first frame doubles as the default
        /// image. Metadata is taken from the first frame.
        /// </summary>
        public static void EncodeAnimation(IEnumerable<PngInputFrame> frames, Stream output, PngEncodeOptions? options = null)
            => PngEncoder.EncodeAnimation(frames, output, options ?? DefaultEncode);

        public static byte[] EncodeAnimation(IEnumerable<PngInputFrame> frames, PngEncodeOptions? options = null)
        {
            using var ms = new MemoryStream();
            PngEncoder.EncodeAnimation(frames, ms, options ?? DefaultEncode);
            return ms.ToArray();
        }

        static readonly PngDecodeOptions DefaultDecode = new();
        static readonly PngEncodeOptions DefaultEncode = new();
    }
}
