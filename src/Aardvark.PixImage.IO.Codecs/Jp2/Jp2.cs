using System;
using System.IO;

namespace Aardvark.ImageCodecs
{
    /// <summary>Order in which a JPEG 2000 codestream sends its packets (layer, resolution, component, position).</summary>
    public enum Jp2Progression
    {
        Lrcp = 0,
        Rlcp = 1,
        Rpcl = 2,
        Pcrl = 3,
        Cprl = 4,
    }

    /// <summary>Where a JPEG 2000 codestream keeps its packet headers.</summary>
    public enum Jp2PacketHeaders
    {
        /// <summary>In front of each packet's data (the usual layout).</summary>
        InPackets = 0,
        /// <summary>Collected in PPT marker segments of the tile-part header.</summary>
        TileHeader = 1,
        /// <summary>Collected in PPM marker segments of the main header.</summary>
        MainHeader = 2,
    }

    /// <summary>Code-block coding style switches of the classic (Part 1) block coder.</summary>
    [Flags]
    public enum Jp2BlockStyle
    {
        None = 0,
        /// <summary>Selective arithmetic coding bypass ("lazy" mode).</summary>
        Bypass = 1,
        /// <summary>Reset the context probabilities after every coding pass.</summary>
        ResetContexts = 2,
        /// <summary>Terminate the arithmetic coder after every coding pass.</summary>
        TerminateAll = 4,
        /// <summary>Vertically causal context formation.</summary>
        VerticalCausal = 8,
        /// <summary>Predictable termination.</summary>
        PredictableTermination = 16,
        /// <summary>Segmentation symbols at the end of every cleanup pass.</summary>
        SegmentationSymbols = 32,
    }

    /// <summary>
    /// Options of the JPEG 2000 decoder.
    ///
    /// Output mapping. Components up to 8 bit give UInt8, up to 16 bit UInt16, up to 32 bit UInt32 and up to 38 bit
    /// UInt64 samples (Int8 .. Int64 if a component is signed); sample values are returned as stored, i.e. a 12 bit
    /// image yields values 0..4095 in UInt16 (<see cref="ImageInfo.BitsPerSample"/> tells the depth). Two exceptions,
    /// both for unsigned data only: images of less than 8 bit are scaled to the full 8 bit range (a bilevel image
    /// comes out as 0 / 255), and when the channels of an image differ in depth (8 bit colour with 1 bit alpha, say)
    /// all are scaled to the depth of the deepest one. The image size is that of the reference grid (Xsiz - XOsiz by
    /// Ysiz - YOsiz), also when every component is subsampled (where OpenJPEG's tools return the component size). One / two / three / four channels are Gray / GrayAlpha / Rgb / Rgba unless a JP2 channel
    /// definition box says otherwise; anything else is <see cref="ColorModel.Other"/>. Palettes are expanded, sYCC
    /// and CMYK are converted to Rgb, premultiplied alpha is undone. Subsampled components are brought to the
    /// reference grid by sample replication, as OpenJPEG does.
    /// </summary>
    public sealed class Jp2DecodeOptions
    {
        /// <summary>Number of highest resolution levels to discard: the result is 2^Reduce times smaller in each direction.</summary>
        public int Reduce { get; set; }
        /// <summary>
        /// Rotate / mirror the pixels according to the EXIF orientation (default), so that the image comes back upright; its
        /// metadata then says orientation 1. False returns the pixels as stored with the file's orientation in the metadata.
        /// </summary>
        public bool ApplyOrientation { get; set; } = true;
        /// <summary>Decode only the first quality layers (0 = all).</summary>
        public int MaxLayers { get; set; }
        /// <summary>
        /// Decode only this window, given in full resolution pixels of the image as returned, i.e. upright unless
        /// <see cref="ApplyOrientation"/> is off (it is clipped to the image; with <see cref="Reduce"/> the result is the
        /// correspondingly smaller window). Only the tiles and code-blocks that contribute to the window are decoded.
        /// </summary>
        public (int X, int Y, int Width, int Height)? Region { get; set; }
        /// <summary>
        /// Decode only these components (indices into the codestream's components, in the order wanted). The result
        /// then is as with <see cref="RawComponents"/>.
        /// </summary>
        public int[]? Components { get; set; }
        /// <summary>Upper bound for the decoded area in pixels; larger requests throw <see cref="ImageNotSupportedException"/>.</summary>
        public long MaxPixels { get; set; } = 1L << 28;
        /// <summary>Decode tiles, code-blocks and wavelet transforms on several threads.</summary>
        public bool Parallel { get; set; } = true;
        /// <summary>
        /// Return the codestream components as they are, one channel each (Gray for one component, otherwise
        /// <see cref="ColorModel.Other"/>): no palette, no channel reordering, no sYCC / CMYK conversion, no depth
        /// scaling. Subsampled components are still replicated to the reference grid.
        /// </summary>
        public bool RawComponents { get; set; }
        /// <summary>
        /// Decode what is there when the codestream ends early or a tile is missing (as OpenJPEG does in its default,
        /// non-strict mode) instead of throwing <see cref="ImageFormatException"/>, which is the default here:
        /// a silently incomplete image is the worse failure for most uses. Missing tiles come out as 0.
        /// </summary>
        public bool AllowTruncated { get; set; }

        internal Jp2DecodeOptions With((int, int, int, int) region)
        {
            var o = (Jp2DecodeOptions)MemberwiseClone();
            o.Region = region;
            return o;
        }
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
        /// <summary>
        /// Irreversible coding: target size in bits per pixel (of the whole file, all components together); 0 lets
        /// <see cref="Quality"/> decide. The code-block truncation points are chosen by rate-distortion optimization.
        /// </summary>
        public double TargetBitsPerPixel { get; set; }
        /// <summary>Tile size; 0 (default) codes the image as a single tile.</summary>
        public int TileWidth { get; set; }
        /// <inheritdoc cref="TileWidth"/>
        public int TileHeight { get; set; }
        /// <summary>Number of wavelet decomposition levels, 0..32. Reduced automatically for small images / tiles. Default 5.</summary>
        public int Levels { get; set; } = 5;
        /// <summary>Code-block size, a power of two from 4 to 1024 with width * height at most 4096. Default 64 x 64.</summary>
        public int CodeBlockWidth { get; set; } = 64;
        /// <inheritdoc cref="CodeBlockWidth"/>
        public int CodeBlockHeight { get; set; } = 64;
        public Jp2Progression Progression { get; set; } = Jp2Progression.Lrcp;
        /// <summary>
        /// Number of quality layers. With more than one, the layers are spaced by rate-distortion slope (irreversible:
        /// each roughly halves the remaining error; reversible: the last layer completes the lossless image).
        /// </summary>
        public int Layers { get; set; } = 1;
        /// <summary>
        /// Precinct sizes (powers of two), from the highest resolution level down; the last entry is used for the
        /// remaining levels. Null (default) = no precinct partition (one precinct per level).
        /// </summary>
        public (int Width, int Height)[]? Precincts { get; set; }
        /// <summary>Classic block coder only: coding style switches.</summary>
        public Jp2BlockStyle BlockStyle { get; set; }
        /// <summary>Bits per sample to declare (1..bits of the sample type); 0 = the full depth of the sample type. Samples must fit.</summary>
        public int BitsPerSample { get; set; }
        /// <summary>Write SOP marker segments before and EPH markers after every packet header.</summary>
        public bool PacketMarkers { get; set; }
        /// <summary>Where the packet headers go; packing them lets a reader see the structure of a tile without its data.</summary>
        public Jp2PacketHeaders PacketHeaders { get; set; }
        /// <summary>Write a TLM (tile-part lengths) marker segment in the main header, which lets readers find tiles without scanning.</summary>
        public bool TileLengthMarkers { get; set; } = true;
        /// <summary>JP2 container: write ICC profile, EXIF block (or orientation) and resolution from <see cref="RawImage.Metadata"/>.</summary>
        public bool WriteMetadata { get; set; } = true;
        /// <summary>Encode code-blocks and tiles on several threads.</summary>
        public bool Parallel { get; set; } = true;

        /// <summary>Test hook: code the HT cleanup pass one bit plane up and add SigProp and MagRef passes for the plane below.</summary>
        internal bool HtRefinementPasses { get; set; }
        /// <summary>
        /// Test hook: with several layers, put this many sets of placeholder passes in front of every HT block's
        /// cleanup pass, spread over the layers before the last one (what a transcoder does to keep the layer
        /// boundaries of a Part 1 codestream, T.814 Annex B).
        /// </summary>
        internal int HtPlaceholderSets { get; set; }
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
        /// <summary>Decoding: component coordinates (at the decoded resolution) of the first sample.</summary>
        public long X0, Y0;
    }

    /// <summary>The components of a codestream on a reference grid of Width x Height.</summary>
    internal sealed class J2kPlanes
    {
        /// <summary>Size of the image area on the reference grid (Xsiz - XOsiz, Ysiz - YOsiz; after Reduce / Region when decoding).</summary>
        public int Width, Height;
        public J2kPlane[] Planes = Array.Empty<J2kPlane>();
        /// <summary>Decoding: whether the codestream used the multi-component transform (already undone).</summary>
        public bool UsedColorTransform;
        /// <summary>Decoding with AllowTruncated: data was missing.</summary>
        public bool Truncated;
        /// <summary>Decoding: reference grid coordinates (at the decoded resolution) of the first pixel.</summary>
        public long X0, Y0;
        /// <summary>Decoding: components carrying the sign-magnitude float mapping of Part 2 NLT type 3 (not undone).</summary>
        public bool[]? NltSignMagnitude;
    }

    /// <summary>JPEG 2000 codec (ISO/IEC 15444-1 codestreams and JP2 files, plus High Throughput JPEG 2000, 15444-15).</summary>
    public static unsafe class Jp2
    {
        /// <summary>Returns whether the data starts with a JP2 signature box or a raw codestream (SOC + SIZ). Must not throw on short input.</summary>
        public static bool IsMatch(ReadOnlySpan<byte> header)
            => (header.Length >= 12 && header.Slice(0, 12).SequenceEqual(new byte[] { 0, 0, 0, 12, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A }))
            || (header.Length >= 4 && header[0] == 0xFF && header[1] == 0x4F && header[2] == 0xFF && header[3] == 0x51);

        /// <summary>
        /// Reads header level information without decoding pixel data: the JP2 boxes in front of the codestream and
        /// its SIZ marker segment. The data may be a prefix of the file; <see cref="ImageFormatException"/> is thrown
        /// when it ends before the SIZ marker segment is complete. Metadata boxes BEHIND the codestream (an EXIF uuid
        /// box written there, say) are not looked at, so Orientation is 0 for such files while
        /// <see cref="Decode"/> does report it in <see cref="RawImage.Metadata"/>.
        /// </summary>
        public static ImageInfo GetInfo(ReadOnlySpan<byte> data)
        {
            var hdr = Jp2Header.Parse(data, true);
            J2kSiz siz;
            fixed (byte* p = data)
            {
                var cs = new J2kCodestream(p + hdr.CodestreamStart, hdr.CodestreamLength);
                cs.ParseSiz();
                siz = cs.Siz;
            }
            if (siz.Width > int.MaxValue || siz.Height > int.MaxValue) throw new ImageNotSupportedException("JPEG 2000 image larger than 2^31 pixels across");
            var layout = Jp2Layout.Create(hdr, siz, false, null);
            return ExifTags.Upright(new ImageInfo(ImageFormat.Jp2, (int)siz.Width, (int)siz.Height, layout.OutChannels, layout.Type, layout.Color)
            {
                BitsPerSample = layout.BitsPerSample,
                Orientation = hdr.Orientation,
                DpiX = hdr.DpiX, DpiY = hdr.DpiY,
                HasIccProfile = hdr.Icc != null,
            });
        }

        /// <summary>Decodes a complete JP2 / JPH file or raw codestream held in memory.</summary>
        public static RawImage Decode(ReadOnlySpan<byte> data, Jp2DecodeOptions? options = null)
        {
            options ??= new Jp2DecodeOptions();
            var hdr = Jp2Header.Parse(data, false);
            var codestream = data.Slice(hdr.CodestreamStart, hdr.CodestreamLength);
            bool orient = options.ApplyOrientation && hdr.Orientation > 1;
            if (orient && options.Region is { } region)
            {
                J2kSiz siz;
                fixed (byte* p = codestream)
                {
                    var cs = new J2kCodestream(p, codestream.Length);
                    cs.ParseSiz();
                    siz = cs.Siz;
                }
                int sw = (int)Math.Min(siz.Width, int.MaxValue), sh = (int)Math.Min(siz.Height, int.MaxValue);
                int uw = hdr.Orientation >= 5 ? sh : sw, uh = hdr.Orientation >= 5 ? sw : sh;
                if (region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0) throw new ArgumentException("bad region");
                // clipped to the upright image here, as the decoder clips unturned regions
                int x1 = (int)Math.Min((long)region.X + region.Width, uw), y1 = (int)Math.Min((long)region.Y + region.Height, uh);
                if (region.X >= x1 || region.Y >= y1) throw new ArgumentException("the region lies outside the image");
                options = options.With(ExifTags.StoredRegion(hdr.Orientation, sw, sh, (region.X, region.Y, x1 - region.X, y1 - region.Y)));
            }
            var img = DecodeImage(codestream, options, hdr, options.RawComponents || options.Components != null);
            if (hdr.Icc != null || hdr.Exif != null || hdr.Xmp != null || hdr.DpiX > 0 || hdr.Orientation != 0)
                img.Metadata = new ImageMetadata { IccProfile = hdr.Icc, Exif = hdr.Exif, Xmp = hdr.Xmp, DpiX = hdr.DpiX, DpiY = hdr.DpiY, Orientation = hdr.Orientation };
            return orient ? ImageOps.ApplyOrientation(img) : img;
        }

        /// <summary>The XMP packet of a JP2 file (uuid box or xml box holding one), null if there is none.</summary>
        public static byte[]? GetXmp(ReadOnlySpan<byte> data) => Jp2Header.Parse(data, false).Xmp;

        static RawImage DecodeImage(ReadOnlySpan<byte> codestream, Jp2DecodeOptions options, Jp2Header hdr, bool raw)
        {
            try
            {
                fixed (byte* p = codestream)
                {
                    var cs = new J2kCodestream(p, codestream.Length);
                    cs.Parse();
                    var layout = Jp2Layout.Create(hdr, cs.Siz, raw, options.Components);
                    var sink = layout.TrySink(cs.Siz);
                    var planes = J2kDecoder.Decode(cs, options, options.Components, sink);
                    return layout.Assemble(planes, hdr, sink);
                }
            }
            catch (AggregateException e) when (Unwrap(e) is ImageFormatException or ImageNotSupportedException or ArgumentException)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(Unwrap(e)).Throw();
                throw;
            }
        }

        static Exception Unwrap(AggregateException e)
        {
            Exception inner = e;
            while (inner is AggregateException a && a.InnerException != null) inner = a.InnerException;
            return inner;
        }

        static J2kPlanes DecodePlanes(ReadOnlySpan<byte> codestream, Jp2DecodeOptions options)
        {
            try
            {
                fixed (byte* p = codestream)
                {
                    var cs = new J2kCodestream(p, codestream.Length);
                    cs.Parse();
                    return J2kDecoder.Decode(cs, options, options.Components);
                }
            }
            catch (AggregateException e) when (Unwrap(e) is ImageFormatException or ImageNotSupportedException or ArgumentException)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(Unwrap(e)).Throw();
                throw;
            }
        }

        /// <summary>Encodes an image as JPEG 2000 to the stream (which only needs to be writable, not seekable).</summary>
        public static void Encode(RawImage image, Stream output, Jp2EncodeOptions? options = null)
        {
            options ??= new Jp2EncodeOptions();
            J2kEncoder.EncodeImage(image, output, options);
        }

        public static byte[] Encode(RawImage image, Jp2EncodeOptions? options = null)
        {
            using var ms = new MemoryStream();
            Encode(image, ms, options);
            return ms.ToArray();
        }

        // ---- internal entry points for other codecs (JPEG 2000 in TIFF, HTJ2K in OpenEXR) ----

        static ReadOnlySpan<byte> Codestream(ReadOnlySpan<byte> data)
        {
            if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0x4F) return data;
            var hdr = Jp2Header.Parse(data, false);
            return data.Slice(hdr.CodestreamStart, hdr.CodestreamLength);
        }

        /// <summary>
        /// Decodes a raw codestream (Part 1 or HT; a JP2 wrapper is skipped) into one plane per component, each at
        /// its own (possibly subsampled) size. The codestream's own multi-component transform and DC level shift are
        /// undone, nothing else: no colour interpretation, no upsampling, no scaling. Honours Reduce, MaxLayers,
        /// Region, Components (others have no Data), Parallel, MaxPixels and AllowTruncated of the options.
        /// </summary>
        internal static J2kPlanes DecodeCodestreamPlanes(ReadOnlySpan<byte> codestream, Jp2DecodeOptions? options = null)
            => DecodePlanes(Codestream(codestream), options ?? new Jp2DecodeOptions());

        /// <summary>
        /// Decodes a raw codestream to interleaved samples without any colour guessing: Gray for one component,
        /// otherwise <see cref="ColorModel.Other"/> with one channel per component in codestream order (subsampled
        /// components replicated to the reference grid). Sample type by the deepest component: up to 8 / 16 / 32 / 38
        /// bit give (U)Int8 / 16 / 32 / 64, signed if any component is signed; values are as stored.
        /// </summary>
        internal static RawImage DecodeCodestream(ReadOnlySpan<byte> codestream, Jp2DecodeOptions? options = null)
        {
            return DecodeImage(Codestream(codestream), options ?? new Jp2DecodeOptions(), new Jp2Header(), true);
        }

        /// <summary>
        /// Encodes planes as a raw codestream. All planes are coded reversibly (5/3) when options.Lossless, with the
        /// reversible colour transform over the first three planes only if options.ColorTransform is set and they
        /// have equal geometry and depth. options.HighThroughput selects the HT block coder (Part 15).
        /// planes.NltSignMagnitude, if set, writes the NLT type 3 marker segments OpenEXR / OpenJPH use for floats.
        /// </summary>
        internal static byte[] EncodeCodestreamPlanes(J2kPlanes planes, Jp2EncodeOptions? options = null)
            => J2kEncoder.EncodePlanes(planes, options ?? new Jp2EncodeOptions());
    }
}
