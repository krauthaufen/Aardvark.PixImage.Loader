using System;
using System.IO;

namespace PixCodec
{
    public sealed class Jp2DecodeOptions
    {
        /// <summary>Number of highest resolution levels to discard: the result is 2^Reduce times smaller in each direction.</summary>
        public int Reduce { get; set; }
        /// <summary>Decode only the first quality layers (0 = all).</summary>
        public int MaxLayers { get; set; }
        /// <summary>Decode only this window, given in full resolution pixels relative to the image origin.</summary>
        public (int X, int Y, int Width, int Height)? Region { get; set; }
        /// <summary>Upper bound for the decoded area in pixels; larger requests throw <see cref="ImageNotSupportedException"/>.</summary>
        public long MaxPixels { get; set; } = 1L << 28;
        /// <summary>Decode tiles and code-blocks on several threads.</summary>
        public bool Parallel { get; set; } = true;
        /// <summary>Decode what is there when the codestream ends early instead of throwing.</summary>
        public bool AllowTruncated { get; set; }
    }

    public sealed class Jp2EncodeOptions
    {
        /// <summary>Reversible (5/3 wavelet, lossless) coding. Default true.</summary>
        public bool Lossless { get; set; } = true;
        /// <summary>Quality 1..100 for irreversible (9/7) coding; ignored when <see cref="Lossless"/> is set.</summary>
        public int Quality { get; set; } = 75;
        /// <summary>Write a raw codestream (.j2k / .j2c) instead of the JP2 file format.</summary>
        public bool RawCodestream { get; set; }
        /// <summary>Use the High Throughput block coder (HTJ2K, ISO/IEC 15444-15).</summary>
        public bool HighThroughput { get; set; }
        /// <summary>Apply the multi-component (RGB to YCbCr) transform to the first three components.</summary>
        public bool ColorTransform { get; set; } = true;
    }

    /// <summary>One component of a codestream: Width x Height samples that sit at every Dx-th / Dy-th position of the reference grid.</summary>
    internal sealed class J2kPlane
    {
        public int Width, Height;
        /// <summary>Subsampling factors XRsiz / YRsiz (1 = full resolution).</summary>
        public int Dx = 1, Dy = 1;
        /// <summary>Bit depth 1..38 and signedness as in the SIZ marker.</summary>
        public int Precision;
        public bool Signed;
        /// <summary>Row-major samples for Precision up to 32 (32 bit unsigned values are stored as their int bit pattern), null otherwise.</summary>
        public int[]? Data;
        /// <summary>Row-major samples when Precision is above 32, null otherwise.</summary>
        public long[]? Data64;
    }

    /// <summary>The components of a codestream on a reference grid of Width x Height.</summary>
    internal sealed class J2kPlanes
    {
        /// <summary>Size of the image area on the reference grid (Xsiz - XOsiz, Ysiz - YOsiz; after Reduce / Region when decoding).</summary>
        public int Width, Height;
        public J2kPlane[] Planes = System.Array.Empty<J2kPlane>();
        /// <summary>Decoding: whether the codestream used the multi-component transform (already undone).</summary>
        public bool UsedColorTransform;
    }

    /// <summary>JPEG 2000 codec (ISO/IEC 15444-1 codestreams and JP2 files, plus High Throughput JPEG 2000, 15444-15).</summary>
    public static class Jp2
    {
        /// <summary>Returns whether the data starts with a JP2 signature box or a raw codestream (SOC + SIZ). Must not throw on short input.</summary>
        public static bool IsMatch(ReadOnlySpan<byte> header)
            => (header.Length >= 12 && header.Slice(0, 12).SequenceEqual(new byte[] { 0, 0, 0, 12, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A }))
            || (header.Length >= 4 && header[0] == 0xFF && header[1] == 0x4F && header[2] == 0xFF && header[3] == 0x51);

        /// <summary>Reads header level information without decoding pixel data.</summary>
        public static ImageInfo GetInfo(ReadOnlySpan<byte> data) => throw new ImageNotSupportedException("JPEG 2000 is not implemented yet");

        /// <summary>Decodes a complete JP2 file or raw codestream held in memory.</summary>
        public static RawImage Decode(ReadOnlySpan<byte> data, Jp2DecodeOptions? options = null) => throw new ImageNotSupportedException("JPEG 2000 is not implemented yet");

        /// <summary>Encodes an image as JPEG 2000 to the stream (which only needs to be writable, not seekable).</summary>
        public static void Encode(RawImage image, Stream output, Jp2EncodeOptions? options = null) => throw new ImageNotSupportedException("JPEG 2000 is not implemented yet");

        // ---- internal entry points for other codecs (JPEG 2000 in TIFF, HTJ2K in OpenEXR) ----

        /// <summary>
        /// Decodes a raw codestream (Part 1 or HT) into one plane per component, each at its own (possibly
        /// subsampled) size. The codestream's own multi-component transform and DC level shift are undone, nothing
        /// else: no colour interpretation, no upsampling, no scaling. Honours Reduce, MaxLayers, Region, Parallel,
        /// MaxPixels and AllowTruncated of the options.
        /// </summary>
        internal static J2kPlanes DecodeCodestreamPlanes(ReadOnlySpan<byte> codestream, Jp2DecodeOptions? options = null)
            => throw new ImageNotSupportedException("JPEG 2000 is not implemented yet");

        /// <summary>
        /// Decodes a raw codestream to interleaved samples without any colour guessing: Gray for one component,
        /// otherwise <see cref="ColorModel.Other"/> with one channel per component in codestream order (subsampled
        /// components replicated to the reference grid). Sample type by the deepest component: up to 8 / 16 / 32 / 38
        /// bit give (U)Int8 / 16 / 32 / 64, signed if any component is signed; values are as stored.
        /// </summary>
        internal static RawImage DecodeCodestream(ReadOnlySpan<byte> codestream, Jp2DecodeOptions? options = null)
            => throw new ImageNotSupportedException("JPEG 2000 is not implemented yet");

        /// <summary>
        /// Encodes planes as a raw codestream. All planes are coded reversibly (5/3) when options.Lossless, with the
        /// reversible colour transform over the first three planes only if options.ColorTransform is set and they
        /// have equal geometry and depth. options.HighThroughput selects the HT block coder (Part 15).
        /// </summary>
        internal static byte[] EncodeCodestreamPlanes(J2kPlanes planes, Jp2EncodeOptions? options = null)
            => throw new ImageNotSupportedException("JPEG 2000 is not implemented yet");
    }
}
