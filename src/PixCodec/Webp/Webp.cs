using System;
using System.Collections.Generic;
using System.IO;

namespace PixCodec
{
    /// <summary>What a decoded animation frame carries.</summary>
    public enum WebpFrameMode
    {
        /// <summary>Every frame is the complete canvas as a viewer would show it (UInt8 Rgba), blending and disposal applied.</summary>
        Composited = 0,
        /// <summary>Every frame is its own decoded rectangle (UInt8 Rgba if the frame has alpha, else Rgb); no compositing.</summary>
        Raw = 1,
    }

    /// <summary>How a frame is drawn onto the canvas.</summary>
    public enum WebpBlend : byte
    {
        /// <summary>Alpha-blend the frame over the canvas.</summary>
        AlphaBlend = 0,
        /// <summary>Replace the frame rectangle, transparency included.</summary>
        NoBlend = 1,
    }

    /// <summary>What happens to a frame's rectangle before the next frame is drawn.</summary>
    public enum WebpDisposal : byte
    {
        /// <summary>Leave the frame in place.</summary>
        None = 0,
        /// <summary>Clear the frame rectangle. Like libwebp and browsers this clears to transparent, not to the ANIM background color.</summary>
        Background = 1,
    }

    public sealed class WebpDecodeOptions
    {
        /// <summary>Content of the frames returned by the animation API. <see cref="Webp.Decode"/> ignores it.</summary>
        public WebpFrameMode FrameMode { get; set; } = WebpFrameMode.Composited;

        /// <summary>
        /// Upper bound for the canvas and for a single frame, in pixels; larger files are rejected with
        /// <see cref="ImageNotSupportedException"/>. The default admits the largest lossless image (16384 x 16384).
        /// Note that a lossless WebP of a few bytes can legitimately describe an image of that size (a single color
        /// costs no bits per pixel), so lower this when decoding untrusted data with a memory budget.
        /// </summary>
        public long MaxPixels { get; set; } = 1L << 28;
    }

    public sealed class WebpEncodeOptions
    {
        /// <summary>Quality 0..100 for lossy output.</summary>
        public int Quality { get; set; } = 75;
        /// <summary>Write lossless (VP8L) instead of lossy (VP8).</summary>
        public bool Lossless { get; set; }

        /// <summary>
        /// Speed versus size, 0 (fastest) .. 6 (smallest), the same scale as libwebp's "method".
        /// Lossless: 0 uses one predictor and greedy matching, higher levels add per-block predictor and cross-color
        /// selection, an entropy image, lazy matching and trials of several color cache sizes and transform sets.
        /// Lossy: 0 predicts whole macroblocks only; from 1 on 4x4 prediction is tried, with more candidate modes
        /// run through a full encode-and-measure trial the higher the level.
        /// </summary>
        public int Method { get; set; } = 4;

        /// <summary>
        /// Lossless only: keep the color of fully transparent pixels. When false their color is replaced by whatever
        /// compresses best (only alpha 0 is guaranteed to come back).
        /// </summary>
        public bool Exact { get; set; } = true;

        /// <summary>Lossy only: number of token partitions, 1, 2, 4 or 8.</summary>
        public int Partitions { get; set; } = 1;

        /// <summary>Lossy only: number of segments (1..4) with their own quantizer, chosen by local activity (flat areas
        /// are quantized finer, busy ones coarser). 1 gives the best PSNR for the size.</summary>
        public int Segments { get; set; } = 1;

        /// <summary>Lossy only: loop filter strength 0..63, or -1 to derive it from the quantizer.</summary>
        public int FilterStrength { get; set; } = -1;

        /// <summary>Lossy only: loop filter sharpness 0..7.</summary>
        public int FilterSharpness { get; set; }

        /// <summary>Lossy only: use the simple loop filter (luma only) instead of the normal one.</summary>
        public bool SimpleFilter { get; set; }

        /// <summary>
        /// Write the ICC profile, EXIF block and <see cref="Xmp"/> packet. ICC and EXIF come from
        /// <see cref="RawImage.Metadata"/> (animation: of the first frame).
        /// </summary>
        public bool WriteMetadata { get; set; } = true;

        /// <summary>Optional XMP packet stored in an 'XMP ' chunk.</summary>
        public byte[]? Xmp { get; set; }

        /// <summary>Animation only: number of repetitions, 0 = forever.</summary>
        public int LoopCount { get; set; }

        /// <summary>Animation only: the background color hint of the ANIM chunk.</summary>
        public (byte R, byte G, byte B, byte A) BackgroundColor { get; set; }

        /// <summary>
        /// Animation only: store just the rectangle that changed since the previous frame (and, lossless, mark unchanged
        /// pixels inside it transparent). The decoded animation is the same either way.
        /// </summary>
        public bool FrameDifferencing { get; set; } = true;
    }

    /// <summary>One frame handed to <see cref="Webp.EncodeAnimation(IEnumerable{WebpInputFrame}, Stream, WebpEncodeOptions?)"/>:
    /// the complete picture to show and for how long.</summary>
    public readonly record struct WebpInputFrame(RawImage Image, int DurationMs = 100);

    /// <summary>File level information about a WebP file.</summary>
    public class WebpAnimationInfo
    {
        /// <summary>Canvas size.</summary>
        public int Width { get; internal set; }
        public int Height { get; internal set; }
        public int FrameCount { get; internal set; }
        /// <summary>The file is an animation (VP8X animation flag); otherwise it holds one still image.</summary>
        public bool IsAnimated { get; internal set; }
        /// <summary>Number of repetitions, 0 = forever. 1 for a still image.</summary>
        public int LoopCount { get; internal set; }
        /// <summary>Background color of the ANIM chunk. Informational: the canvas starts out transparent, as in browsers.</summary>
        public (byte R, byte G, byte B, byte A) BackgroundColor { get; internal set; }
        /// <summary>Whether <see cref="Webp.Decode"/> returns Rgba for this file.</summary>
        public bool HasAlpha { get; internal set; }
        public byte[]? IccProfile { get; internal set; }
        /// <summary>EXIF block starting at the TIFF header.</summary>
        public byte[]? Exif { get; internal set; }
        /// <summary>XMP packet.</summary>
        public byte[]? Xmp { get; internal set; }
    }

    public sealed class WebpAnimation : WebpAnimationInfo
    {
        public IReadOnlyList<WebpFrame> Frames { get; internal set; } = Array.Empty<WebpFrame>();
    }

    public sealed class WebpFrame
    {
        public int Index { get; internal set; }
        /// <summary>Frame rectangle on the canvas (offsets are always even).</summary>
        public int X { get; internal set; }
        public int Y { get; internal set; }
        public int Width { get; internal set; }
        public int Height { get; internal set; }
        /// <summary>Raw display time in milliseconds. No browser-style clamping of tiny values is applied.</summary>
        public int DurationMs { get; internal set; }
        public WebpBlend Blend { get; internal set; }
        public WebpDisposal Disposal { get; internal set; }
        /// <summary>The frame is stored lossless (VP8L) rather than lossy (VP8).</summary>
        public bool IsLossless { get; internal set; }
        /// <summary>The frame carries transparency (an ALPH chunk, or the alpha hint of a lossless frame).</summary>
        public bool HasAlpha { get; internal set; }
        /// <summary>
        /// <see cref="WebpFrameMode.Composited"/>: the canvas (UInt8 Rgba) after this frame.
        /// <see cref="WebpFrameMode.Raw"/>: the frame rectangle alone, Rgba if <see cref="HasAlpha"/>, else Rgb.
        /// </summary>
        public RawImage Image { get; internal set; } = null!;
    }

    /// <summary>
    /// WebP codec: lossy (VP8 key frames), lossless (VP8L), alpha, animation and metadata, reading and writing.
    /// <para>
    /// Alpha rule: <see cref="Decode"/> returns UInt8 Rgba when the VP8X header has its alpha flag set, when the image
    /// has an ALPH chunk, or when a lossless image sets its alpha_is_used bit; for an animation also when the first
    /// frame does not cover the canvas. Otherwise it returns UInt8 Rgb. <see cref="GetInfo"/> reports the same.
    /// Alpha is straight; the color of fully transparent pixels is returned as stored.
    /// </para>
    /// <para>
    /// Lossy images decode bit exact with libwebp (default settings: fancy upsampling, no dithering). Animations are
    /// composited like libwebp's WebPAnimDecoder: transparent canvas, disposal to transparent, non-premultiplied blending.
    /// </para>
    /// </summary>
    public static class Webp
    {
        /// <summary>Returns whether the data starts with a WebP signature (RIFF....WEBP, needs 12 bytes). Must not throw on short input.</summary>
        public static bool IsMatch(ReadOnlySpan<byte> header)
            => header.Length >= 12 && header.Slice(0, 4).SequenceEqual("RIFF"u8) && header.Slice(8, 4).SequenceEqual("WEBP"u8);

        /// <summary>
        /// Reads header level information without decoding pixel data. Width/Height are the canvas; Channels/Color are
        /// what <see cref="Decode"/> returns; FrameCount is the number of animation frames (1 for a still image).
        /// </summary>
        public static ImageInfo GetInfo(ReadOnlySpan<byte> data)
        {
            var c = WebpContainer.Parse(data);
            bool alpha = WebpDecoder.OutputHasAlpha(c);
            return new ImageInfo(ImageFormat.Webp, c.Width, c.Height, alpha ? 4 : 3, SampleType.UInt8,
                alpha ? ColorModel.Rgba : ColorModel.Rgb, c.Frames.Count);
        }

        /// <summary>Decodes a complete WebP file held in memory (first frame of an animation, composited).</summary>
        public static RawImage Decode(ReadOnlySpan<byte> data, WebpDecodeOptions? options = null)
        {
            var c = WebpContainer.Parse(data);
            return WebpDecoder.DecodeFirst(c, data, options ?? new WebpDecodeOptions());
        }

        /// <summary>Reads the file level properties (and metadata) without decoding pixel data.</summary>
        public static WebpAnimationInfo GetAnimationInfo(ReadOnlySpan<byte> data)
        {
            var c = WebpContainer.Parse(data);
            var info = new WebpAnimationInfo();
            WebpDecoder.FillInfo(c, data, info);
            return info;
        }

        /// <summary>The XMP packet of the file ('XMP ' chunk), or null.</summary>
        public static byte[]? GetXmp(ReadOnlySpan<byte> data)
        {
            var c = WebpContainer.Parse(data);
            return c.XmpLength < 0 ? null : data.Slice(c.XmpOffset, c.XmpLength).ToArray();
        }

        /// <summary>Decodes all frames (a still image yields one). See <see cref="WebpDecodeOptions.FrameMode"/>.</summary>
        public static WebpAnimation DecodeAnimation(ReadOnlySpan<byte> data, WebpDecodeOptions? options = null)
        {
            var c = WebpContainer.Parse(data);
            var st = new WebpDecoder(c, options ?? new WebpDecodeOptions());
            var frames = new List<WebpFrame>(c.Frames.Count);
            while (st.Next(data) is { } f) frames.Add(f);
            var anim = new WebpAnimation { Frames = frames };
            WebpDecoder.FillInfo(c, data, anim);
            return anim;
        }

        /// <summary>
        /// Decodes frame by frame, so that a long animation never has to be held in memory at once. Every frame owns its
        /// pixels. The file structure is validated on the first MoveNext.
        /// </summary>
        public static IEnumerable<WebpFrame> EnumerateFrames(ReadOnlyMemory<byte> data, WebpDecodeOptions? options = null)
        {
            var c = WebpContainer.Parse(data.Span);
            var st = new WebpDecoder(c, options ?? new WebpDecodeOptions());
            while (st.Next(data.Span) is { } f) yield return f;
        }

        /// <summary>
        /// Encodes an image (UInt8 Gray, GrayAlpha, Rgb or Rgba) as WebP to the stream (which only needs to be writable,
        /// not seekable). Images with an alpha channel keep it, also when it is fully opaque.
        /// </summary>
        public static void Encode(RawImage image, Stream output, WebpEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            ArgumentNullException.ThrowIfNull(output);
            WebpEncoder.EncodeStill(image, output, options ?? new WebpEncodeOptions());
        }

        /// <summary>
        /// Encodes an animation. All frames must have the same size (the canvas) and are complete pictures; the encoder
        /// picks frame rectangles, blending and disposal itself. Frames are consumed lazily, one at a time.
        /// </summary>
        public static void EncodeAnimation(IEnumerable<WebpInputFrame> frames, Stream output, WebpEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(frames);
            ArgumentNullException.ThrowIfNull(output);
            WebpEncoder.EncodeAnimation(frames, output, options ?? new WebpEncodeOptions());
        }

        public static byte[] EncodeAnimation(IEnumerable<WebpInputFrame> frames, WebpEncodeOptions? options = null)
        {
            using var ms = new MemoryStream();
            EncodeAnimation(frames, ms, options);
            return ms.ToArray();
        }
    }
}
