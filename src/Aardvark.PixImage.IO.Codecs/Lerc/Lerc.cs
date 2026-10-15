using System;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Aardvark.ImageCodecs
{
    public sealed class LercDecodeOptions
    {
        /// <summary>Verify the Fletcher-32 checksum of LERC2 v3+ blobs (a mismatch throws <see cref="ImageFormatException"/>).</summary>
        public bool VerifyChecksum { get; set; } = true;

        /// <summary>Hard upper bound for width * height; anything larger is rejected with <see cref="ImageNotSupportedException"/>.</summary>
        public long MaxPixels { get; set; } = 1L << 28;

        /// <summary>
        /// LERC stores constant or empty rasters of any size in a few bytes, so the size in a header is not tied to
        /// the amount of data. A raster of up to this many pixels is accepted as announced; a larger one only if the
        /// blob is long enough to plausibly hold it (at least one byte per 32 x 32 block), otherwise it is rejected
        /// as malformed. Counted in samples (pixels times values per pixel). The default allows 64 million.
        /// </summary>
        public long MaxUnbackedPixels { get; set; } = 1L << 26;
    }

    /// <summary>What a LERC blob contains, read from its header(s).</summary>
    public readonly record struct LercInfo
    {
        /// <summary>1 for LERC1 ("CntZImage "), 2..6 for the LERC2 codec version.</summary>
        public int Version { get; init; }
        public SampleType SampleType { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        /// <summary>Values per pixel within one band.</summary>
        public int Depth { get; init; }
        /// <summary>Number of concatenated band blobs.</summary>
        public int Bands { get; init; }
        /// <summary>Channels of the decoded image: Bands * Depth.</summary>
        public int Channels => Bands * Depth;
        /// <summary>Valid pixels of the first band.</summary>
        public int ValidPixelCount { get; init; }
        /// <summary>
        /// Smallest / largest value over the bands that have valid pixels, as the headers state it (0 when no pixel is
        /// valid). For lossy blobs the decoded values lie within this range. A version 6 blob with no-data values can
        /// include the encoder's internal no-data value in it. LERC1 headers lack the minimum: there it is taken from
        /// the decoded data.
        /// </summary>
        public double MinValue { get; init; }
        /// <inheritdoc cref="MinValue"/>
        public double MaxValue { get; init; }
        /// <summary>Largest error the encoder allowed; 0 (or 0.5 for integer data) means lossless.</summary>
        public double MaxZError { get; init; }
        /// <summary>Size of all band blobs together in bytes.</summary>
        public int BlobSize { get; init; }
        /// <summary>LERC2 v6: some band keeps no-data values inside valid pixels; see <see cref="NoDataValue"/>.</summary>
        public bool HasNoDataValue { get; init; }
        /// <summary>The no-data value of the first band that has one.</summary>
        public double NoDataValue { get; init; }
    }

    /// <summary>Result of the internal decode entry point used by the TIFF codec.</summary>
    internal readonly struct LercBlobResult
    {
        /// <summary>Bands * depth found in the blob.</summary>
        public readonly int Channels;
        public readonly bool HasInvalidPixels;
        public readonly bool HasNoData;
        public readonly double NoDataValue;

        public LercBlobResult(int channels, bool hasInvalid, bool hasNoData, double noData)
        {
            Channels = channels; HasInvalidPixels = hasInvalid; HasNoData = hasNoData; NoDataValue = noData;
        }
    }

    /// <summary>
    /// LERC (Limited Error Raster Compression) blobs, as used standalone and inside TIFF (compression 34887).
    /// Not an image file format of its own in <see cref="ImageCodec"/>.
    /// </summary>
    public static class Lerc
    {
        /// <summary>Returns whether the data starts with a LERC1 ("CntZImage ") or LERC2 ("Lerc2 ") blob header.</summary>
        public static bool IsMatch(ReadOnlySpan<byte> header)
            => header.Length >= 10 && (header.Slice(0, 10).SequenceEqual("CntZImage "u8) || header.Slice(0, 6).SequenceEqual("Lerc2 "u8));

        /// <summary>
        /// Reads the blob header(s). For LERC2 only the headers are looked at, so a prefix of the blob is enough (the
        /// bands of a blob older than version 6 are then counted as far as the data reaches). LERC1 has no value range
        /// in its header: there the whole blob is needed and decoded.
        /// </summary>
        public static LercInfo GetInfo(ReadOnlySpan<byte> data)
        {
            var layout = Layout.Scan(data, false);
            if (layout.IsLerc1) return Lerc1Info(data, layout);
            double min = double.PositiveInfinity, max = double.NegativeInfinity;
            bool hasNoData = false;
            double noData = 0;
            foreach (var h in layout.Bands2)
            {
                if (h.ValidCount > 0) { min = Math.Min(min, h.ZMin); max = Math.Max(max, h.ZMax); }
                if (h.PassNoData && !hasNoData) { hasNoData = true; noData = h.NoDataOrig; }
            }
            var first = layout.Bands2[0];
            return new LercInfo
            {
                Version = first.Version, SampleType = layout.Type, Width = layout.Width, Height = layout.Height,
                Depth = layout.Depth, Bands = layout.BandCount, ValidPixelCount = first.ValidCount,
                MinValue = min <= max ? min : 0, MaxValue = min <= max ? max : 0, MaxZError = first.MaxZError,
                BlobSize = layout.TotalSize, HasNoDataValue = hasNoData, NoDataValue = noData,
            };
        }

        static LercInfo Lerc1Info(ReadOnlySpan<byte> data, Layout layout)
        {
            var img = Decode(data, out byte[]? mask, null);
            float[] z = img.GetData<float>();
            int pixels = layout.Width * layout.Height, bands = layout.BandCount, validCount = pixels;
            double min = double.PositiveInfinity, max = double.NegativeInfinity;
            if (mask != null)
            {
                validCount = 0;
                for (int k = 0; k < pixels; k++) validCount += mask[k];
            }
            for (int k = 0; k < pixels; k++)
            {
                for (int b = 0; b < bands; b++)
                {
                    if (mask != null && mask[mask.Length == pixels ? k : b * pixels + k] == 0) continue;
                    float v = z[k * bands + b];
                    if (v < min) min = v;
                    if (v > max) max = v;
                }
            }
            return new LercInfo
            {
                Version = 1, SampleType = SampleType.Float32, Width = layout.Width, Height = layout.Height, Depth = 1,
                Bands = bands, ValidPixelCount = validCount, MinValue = min <= max ? min : 0, MaxValue = min <= max ? max : 0,
                MaxZError = layout.Bands1[0].MaxZError, BlobSize = layout.TotalSize,
            };
        }

        /// <summary>
        /// Decodes one LERC blob (all bands). The result has the blob's data type and one channel per band / depth
        /// value (ColorModel by channel count). Invalid pixels are returned as stored (0); use the overload with a
        /// mask to tell them apart.
        /// </summary>
        public static RawImage Decode(ReadOnlySpan<byte> data) => Decode(data, out _, null);

        /// <summary>
        /// Decodes one LERC blob (all bands): channel = band * depth + depthIndex. <paramref name="validMask"/> is
        /// null when every pixel is valid, otherwise one byte per pixel (1 = valid, 0 = invalid; invalid pixels are 0
        /// in the image): width * height bytes when all bands share the mask, Bands * width * height (band after band)
        /// when they differ. No-data values of a version 6 blob are returned in place, see <see cref="LercInfo.NoDataValue"/>.
        /// </summary>
        public static RawImage Decode(ReadOnlySpan<byte> data, out byte[]? validMask, LercDecodeOptions? options = null)
        {
            options ??= new LercDecodeOptions();
            var layout = Layout.Scan(data, true);
            long pixels = (long)layout.Width * layout.Height;
            long sampleCount = pixels * layout.BandCount * layout.Depth; // at most 2^31 * 2^16 * 2^31: no overflow
            if (sampleCount < 0 || sampleCount > long.MaxValue / 16) throw new ImageNotSupportedException("LERC: image is too large");
            if (pixels > options.MaxPixels || sampleCount > options.MaxPixels * 64) throw new ImageNotSupportedException("LERC: image exceeds LercDecodeOptions.MaxPixels");
            if (sampleCount > Array.MaxLength) throw new ImageNotSupportedException("LERC: image is too large");
            if (sampleCount > options.MaxUnbackedPixels && layout.TotalSize < sampleCount / 1024)
                throw new ImageFormatException("LERC: blob is too short for the announced image size");
            int channels = layout.BandCount * layout.Depth;

            // every sample gets written (invalid ones as 0), so the array need not be cleared first
            Array samples = Uninitialized(layout.Type, (int)sampleCount);
            var masks = new byte[layout.BandCount][];
            switch (layout.Type)
            {
                case SampleType.Int8: DecodeBands<sbyte>(data, layout, (sbyte[])samples, channels, masks, options.VerifyChecksum); break;
                case SampleType.UInt8: DecodeBands<byte>(data, layout, (byte[])samples, channels, masks, options.VerifyChecksum); break;
                case SampleType.Int16: DecodeBands<short>(data, layout, (short[])samples, channels, masks, options.VerifyChecksum); break;
                case SampleType.UInt16: DecodeBands<ushort>(data, layout, (ushort[])samples, channels, masks, options.VerifyChecksum); break;
                case SampleType.Int32: DecodeBands<int>(data, layout, (int[])samples, channels, masks, options.VerifyChecksum); break;
                case SampleType.UInt32: DecodeBands<uint>(data, layout, (uint[])samples, channels, masks, options.VerifyChecksum); break;
                case SampleType.Float32: DecodeBands<float>(data, layout, (float[])samples, channels, masks, options.VerifyChecksum); break;
                default: DecodeBands<double>(data, layout, (double[])samples, channels, masks, options.VerifyChecksum); break;
            }
            validMask = CombineMasks(masks, (int)pixels);
            return new RawImage(layout.Width, layout.Height, RawImage.DefaultColorModel(channels), samples, channels);
        }

        static Array Uninitialized(SampleType t, int n) => t switch
        {
            SampleType.Int8 => GC.AllocateUninitializedArray<sbyte>(n),
            SampleType.UInt8 => GC.AllocateUninitializedArray<byte>(n),
            SampleType.Int16 => GC.AllocateUninitializedArray<short>(n),
            SampleType.UInt16 => GC.AllocateUninitializedArray<ushort>(n),
            SampleType.Int32 => GC.AllocateUninitializedArray<int>(n),
            SampleType.UInt32 => GC.AllocateUninitializedArray<uint>(n),
            SampleType.Float32 => GC.AllocateUninitializedArray<float>(n),
            _ => GC.AllocateUninitializedArray<double>(n),
        };

        static byte[]? CombineMasks(byte[]?[] masks, int pixels)
        {
            bool same = true;
            for (int b = 1; b < masks.Length; b++)
                same &= ReferenceEquals(masks[b], masks[0]) || (masks[b] != null && masks[0] != null && masks[b].AsSpan().SequenceEqual(masks[0]));
            if (same) return masks[0];
            byte[] all = new byte[(long)pixels * masks.Length];
            for (int b = 0; b < masks.Length; b++)
            {
                var part = all.AsSpan(b * pixels, pixels);
                if (masks[b] == null) part.Fill(1); else masks[b].CopyTo(part);
            }
            return all;
        }

        /// <summary>Decodes all bands into the interleaved <paramref name="dst"/>; masks[b] receives band b's mask (null = all valid).</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void DecodeBands<T>(ReadOnlySpan<byte> data, Layout layout, Span<T> dst, int stride, byte[]?[] masks, bool verifyChecksum)
            where T : unmanaged
        {
            var mask = new LercMask();
            int pos = 0, depth = layout.Depth, pixels = layout.Width * layout.Height;
            for (int b = 0; b < layout.BandCount; b++)
            {
                if (layout.IsLerc1)
                {
                    var h = layout.Bands1[b];
                    Lerc1Decoder.DecodeBand(data.Slice(pos, h.BlobSize), h, MemoryMarshal.Cast<T, float>(dst), stride, b, mask);
                    pos += h.BlobSize;
                }
                else
                {
                    var h = layout.Bands2[b];
                    Lerc2Decoder.DecodeBand(data.Slice(pos, h.BlobSize), h, dst, stride, b * depth, mask, verifyChecksum);
                    pos += h.BlobSize;
                    if (h.PassNoData && h.NoData != h.NoDataOrig)
                    {
                        // the encoder moved the no-data value out of the way of the data: put the caller's back
                        T from = LercTypes.FromDouble<T>(h.NoData), to = LercTypes.FromDouble<T>(h.NoDataOrig);
                        if (LercStats.Current != null) LercStats.Current.NoDataRemapped++;
                        byte[]? valid = mask.Bytes;
                        for (int k = 0; k < pixels; k++)
                        {
                            if (valid != null && valid[k] == 0) continue;
                            var px = dst.Slice(k * stride + b * depth, depth);
                            for (int m = 0; m < depth; m++)
                                if (EqualityComparer<T>.Default.Equals(px[m], from)) px[m] = to;
                        }
                    }
                }
                masks[b] = mask.Bytes;
            }
        }

        /// <summary>
        /// Decodes one blob into a caller buffer (TIFF strips / tiles). <paramref name="destination"/> holds
        /// width * height * destChannels native-endian samples of <paramref name="type"/>; the blob's channels (at
        /// most destChannels) go to channels 0..n-1 of every pixel, the others are not touched. Invalid pixels are
        /// set to 0. <paramref name="validMask"/> (width * height, or empty to skip) receives 1 = valid / 0 = invalid
        /// of the first band. The blob's size and type must match, else <see cref="ImageFormatException"/>.
        /// Stateless and thread safe.
        /// </summary>
        internal static LercBlobResult DecodeInto(ReadOnlySpan<byte> blob, SampleType type, int width, int height,
            Span<byte> destination, int destChannels, Span<byte> validMask, bool verifyChecksum = true)
        {
            var layout = Layout.Scan(blob, true);
            if (layout.Width != width || layout.Height != height) throw new ImageFormatException("LERC: blob size does not match the image");
            if (layout.Type != type) throw new ImageFormatException("LERC: blob data type does not match the image");
            long channels = (long)layout.BandCount * layout.Depth;
            if (channels > destChannels) throw new ImageFormatException("LERC: blob has more values per pixel than the image");
            long pixels = (long)width * height;
            if (destination.Length < pixels * destChannels * RawImage.BytesPerSample(type)) throw new ArgumentException("destination is too small");
            if (validMask.Length != 0 && validMask.Length < pixels) throw new ArgumentException("validMask is too small");

            var masks = new byte[layout.BandCount][];
            switch (type)
            {
                case SampleType.Int8: DecodeBands(blob, layout, MemoryMarshal.Cast<byte, sbyte>(destination), destChannels, masks, verifyChecksum); break;
                case SampleType.UInt8: DecodeBands(blob, layout, destination, destChannels, masks, verifyChecksum); break;
                case SampleType.Int16: DecodeBands(blob, layout, MemoryMarshal.Cast<byte, short>(destination), destChannels, masks, verifyChecksum); break;
                case SampleType.UInt16: DecodeBands(blob, layout, MemoryMarshal.Cast<byte, ushort>(destination), destChannels, masks, verifyChecksum); break;
                case SampleType.Int32: DecodeBands(blob, layout, MemoryMarshal.Cast<byte, int>(destination), destChannels, masks, verifyChecksum); break;
                case SampleType.UInt32: DecodeBands(blob, layout, MemoryMarshal.Cast<byte, uint>(destination), destChannels, masks, verifyChecksum); break;
                case SampleType.Float32: DecodeBands(blob, layout, MemoryMarshal.Cast<byte, float>(destination), destChannels, masks, verifyChecksum); break;
                default: DecodeBands(blob, layout, MemoryMarshal.Cast<byte, double>(destination), destChannels, masks, verifyChecksum); break;
            }

            bool invalid = false;
            foreach (var m in masks) invalid |= m != null;
            if (validMask.Length != 0)
            {
                if (masks[0] == null) validMask.Slice(0, (int)pixels).Fill(1);
                else masks[0].CopyTo(validMask);
            }
            bool hasNoData = false;
            double noData = 0;
            if (!layout.IsLerc1)
                foreach (var h in layout.Bands2)
                    if (h.PassNoData && !hasNoData) { hasNoData = true; noData = h.NoDataOrig; }
            return new LercBlobResult((int)channels, invalid, hasNoData, noData);
        }
        /// <summary>The band blobs found in the data.</summary>
        sealed class Layout
        {
            public bool IsLerc1;
            public int Width, Height, Depth, BandCount, TotalSize;
            public SampleType Type;
            public readonly List<Lerc2Header> Bands2 = new();
            public readonly List<Lerc1Header> Bands1 = new();

            /// <summary>
            /// Walks the band headers. With <paramref name="complete"/> every band must lie fully inside the data;
            /// without it (header information only) the walk stops where the data ends.
            /// </summary>
            public static Layout Scan(ReadOnlySpan<byte> data, bool complete)
            {
                var l = new Layout();
                if (Lerc1Header.IsMagic(data))
                {
                    l.IsLerc1 = true;
                    int pos = 0;
                    while (true)
                    {
                        var rest = data.Slice(pos);
                        if (l.BandCount > 0 && (!Lerc1Header.IsMagic(rest) || rest.Length < Lerc1Header.FixedSize + Lerc1Header.PartHeaderSize)) break;
                        // later bands: Esri's writer leaves out the count (mask) part and reuses the mask, GDAL's MRF
                        // driver writes complete blobs; a count part is recognised by its zero tile counts
                        bool zOnly = l.BandCount > 0 && !Lerc1Header.HasCountPart(rest);
                        var h = Lerc1Header.Parse(rest, zOnly);
                        if (l.BandCount > 0 && (h.Width != l.Width || h.Height != l.Height)) break;
                        if (h.BlobSize > rest.Length) throw new ImageFormatException("LERC: blob is truncated");
                        l.Width = h.Width; l.Height = h.Height;
                        l.Bands1.Add(h);
                        l.BandCount++;
                        pos += h.BlobSize;
                    }
                    l.Depth = 1;
                    l.Type = SampleType.Float32;
                    l.TotalSize = pos;
                    return l;
                }

                if (!Lerc2Header.IsMagic(data)) throw new ImageFormatException("LERC: not a LERC blob");
                var first = Lerc2Header.Parse(data);
                l.Width = first.Cols; l.Height = first.Rows; l.Depth = first.Depth;
                l.Type = LercTypes.ToSampleType(first.DataType);
                int p = 0;
                var cur = first;
                while (true)
                {
                    if (cur.BlobSize > data.Length - p)
                    {
                        if (complete) throw new ImageFormatException("LERC: blob is truncated");
                        l.Bands2.Add(cur);
                        l.BandCount = first.Version >= 6 ? first.BlobsMore + 1 : l.BandCount + 1;
                        l.TotalSize = p + cur.BlobSize;
                        return l;
                    }
                    l.Bands2.Add(cur);
                    l.BandCount++;
                    p += cur.BlobSize;
                    bool more = first.Version >= 6 ? l.BandCount <= first.BlobsMore : Lerc2Header.IsMagic(data.Slice(p));
                    if (!more) break;
                    if (!complete && data.Length - p < Lerc2Header.SizeOf(first.Version))
                    {
                        if (first.Version >= 6) l.BandCount = first.BlobsMore + 1;
                        break;
                    }
                    cur = Lerc2Header.Parse(data.Slice(p));
                    if (cur.Cols != l.Width || cur.Rows != l.Height || cur.Depth != l.Depth || cur.DataType != first.DataType || cur.Version != first.Version)
                        throw new ImageFormatException("LERC: the bands of the blob differ in size or type");
                    if (l.BandCount > 1 << 16) throw new ImageFormatException("LERC: too many bands");
                }
                l.TotalSize = p;
                return l;
            }
        }
    }
}
