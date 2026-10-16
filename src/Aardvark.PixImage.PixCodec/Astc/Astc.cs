using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Aardvark.ImageCodecs.KtxImpl;

namespace Aardvark.ImageCodecs
{
    /// <summary>How the blocks of an .astc file (which does not record it) are decoded.</summary>
    public enum AstcProfile
    {
        /// <summary>HDR (Float16) when any block uses an HDR endpoint mode or HDR void extent, else LDR (UInt8).</summary>
        Auto,
        /// <summary>LDR, linear UNORM8 (what <c>astcenc -dl</c> gives).</summary>
        Ldr,
        /// <summary>LDR with the sRGB decode (what an _SRGB texture format gives), tagged sRGB.</summary>
        LdrSrgb,
        /// <summary>HDR: half floats (<c>astcenc -dh</c>).</summary>
        Hdr,
    }

    public sealed class AstcDecodeOptions
    {
        public AstcProfile Profile { get; set; } = AstcProfile.Auto;

        /// <summary>Upper bound on the pixels of the image (all slices); larger files are rejected with <see cref="ImageNotSupportedException"/>.</summary>
        public long MaxPixels { get; set; } = 1L << 28;

        internal static readonly AstcDecodeOptions Default = new();
    }

    public sealed class AstcEncodeOptions
    {
        /// <summary>Footprint (4x4 by default): 4x4, 5x4, 5x5, 6x5, 6x6, 8x5, 8x6, 8x8, 10x5, 10x6, 10x8, 10x10, 12x10, 12x12.</summary>
        public int BlockWidth { get; set; } = 4;
        /// <inheritdoc cref="BlockWidth"/>
        public int BlockHeight { get; set; } = 4;

        /// <summary>Speed versus quality of the encoder.</summary>
        public BcQuality Quality { get; set; } = BcQuality.Normal;

        /// <summary>
        /// Encode for the sRGB decode (errors are measured on sRGB values decoded as such). Null (the default) takes it from
        /// the image's <see cref="ImageMetadata.Transfer"/>; untagged UInt8 colour images count as sRGB. Float images are
        /// always encoded for the HDR profile.
        /// </summary>
        public bool? Srgb { get; set; }

        /// <summary>Upper bound on the threads used, &lt;= 0 for all cores.</summary>
        public int MaxDegreeOfParallelism { get; set; }
    }

    /// <summary>The 16 byte header of an .astc file.</summary>
    public sealed record AstcHeader(int BlockWidth, int BlockHeight, int BlockDepth, int Width, int Height, int Depth)
    {
        public bool Is3DFootprint => BlockDepth > 1;
        /// <summary>Number of 16 byte blocks that follow the header.</summary>
        public long BlockCount => (long)((Width + BlockWidth - 1) / BlockWidth) * ((Height + BlockHeight - 1) / BlockHeight) * ((Depth + BlockDepth - 1) / BlockDepth);
    }

    /// <summary>
    /// The plain ASTC file of ARM's astcenc (.astc): magic 0x5CA1AB13, the block footprint and 24 bit image dimensions, then the
    /// blocks in raster order (x, then y, then z). 2D and 3D footprints, 2D images and volumes; decodes to UInt8 Rgba (LDR) or
    /// Float16 Rgba (HDR), see <see cref="AstcProfile"/>. Writes 2D footprints (volumes slice by slice, with a block depth of 1)
    /// with the ASTC encoder KTX uses: LDR from integer images, HDR from float images.
    /// </summary>
    public static class Astc
    {
        static readonly (int, int)[] Footprints2D =
        {
            (4, 4), (5, 4), (5, 5), (6, 5), (6, 6), (8, 5), (8, 6), (8, 8), (10, 5), (10, 6), (10, 8), (10, 10), (12, 10), (12, 12),
        };

        static readonly (int, int, int)[] Footprints3D =
        {
            (3, 3, 3), (4, 3, 3), (4, 4, 3), (4, 4, 4), (5, 4, 4), (5, 5, 4), (5, 5, 5), (6, 5, 5), (6, 6, 5), (6, 6, 6),
        };

        /// <summary>Whether the data starts with the .astc magic number.</summary>
        public static bool IsMatch(ReadOnlySpan<byte> h) => h.Length >= 16 && BinaryPrimitives.ReadUInt32LittleEndian(h) == 0x5CA1AB13;

        public static AstcHeader ReadHeader(ReadOnlySpan<byte> d)
        {
            if (!IsMatch(d)) throw new ImageFormatException("ASTC: not an .astc file");
            int bw = d[4], bh = d[5], bd = d[6];
            int w = d[7] | d[8] << 8 | d[9] << 16, h = d[10] | d[11] << 8 | d[12] << 16, z = d[13] | d[14] << 8 | d[15] << 16;
            bool ok = bd <= 1 ? Array.IndexOf(Footprints2D, (bw, bh)) >= 0 : Array.IndexOf(Footprints3D, (bw, bh, bd)) >= 0;
            if (!ok) throw new ImageFormatException($"ASTC: {bw}x{bh}x{bd} is not an ASTC footprint");
            if (w == 0 || h == 0 || z == 0) throw new ImageFormatException("ASTC: empty image");
            return new AstcHeader(bw, bh, Math.Max(1, bd), w, h, z);
        }

        /// <summary>Header information; FrameCount is the number of slices (1 for 2D images).</summary>
        public static ImageInfo GetInfo(ReadOnlySpan<byte> data, AstcDecodeOptions? options = null)
        {
            var hd = ReadHeader(data);
            var profile = Resolve(data, hd, options ?? AstcDecodeOptions.Default);
            bool hdr = profile == AstcProfile.Hdr;
            return new ImageInfo(ImageFormat.Astc, hd.Width, hd.Height, 4, hdr ? SampleType.Float16 : SampleType.UInt8, ColorModel.Rgba, hd.Depth)
            {
                BitsPerSample = hdr ? 16 : 8,
                Transfer = Transfer(profile),
            };
        }

        static TransferFunction Transfer(AstcProfile p) => p switch
        {
            AstcProfile.LdrSrgb => TransferFunction.Srgb,
            AstcProfile.Hdr => TransferFunction.Linear,
            _ => TransferFunction.Unknown,
        };

        static AstcProfile Resolve(ReadOnlySpan<byte> d, AstcHeader hd, AstcDecodeOptions opt)
        {
            if (opt.Profile != AstcProfile.Auto) return opt.Profile;
            long n = Math.Min(hd.BlockCount, (d.Length - 16) / 16);
            for (long i = 0; i < n; i++)
                if (AstcBlocks.IsHdr(d.Slice((int)(16 + i * 16), 16), hd.Is3DFootprint)) return AstcProfile.Hdr;
            return AstcProfile.Ldr;
        }

        /// <summary>Decodes the image (the first slice of a volume).</summary>
        public static RawImage Decode(ReadOnlySpan<byte> data, AstcDecodeOptions? options = null)
        {
            var hd = ReadHeader(data);
            return DecodeSlices(data, hd, options ?? AstcDecodeOptions.Default, 1)[0];
        }

        /// <summary>Decodes every slice, front to back (one image for 2D files).</summary>
        public static RawImage[] DecodeVolume(ReadOnlySpan<byte> data, AstcDecodeOptions? options = null)
        {
            var hd = ReadHeader(data);
            return DecodeSlices(data, hd, options ?? AstcDecodeOptions.Default, hd.Depth);
        }

        static RawImage[] DecodeSlices(ReadOnlySpan<byte> data, AstcHeader hd, AstcDecodeOptions opt, int slices)
        {
            if ((long)hd.Width * hd.Height * hd.Depth > opt.MaxPixels) throw new ImageNotSupportedException("ASTC: image exceeds MaxPixels");
            long bytes = hd.BlockCount * 16;
            if (data.Length - 16 < bytes) throw new ImageFormatException("ASTC: block data truncated");
            var profile = Resolve(data, hd, opt);
            bool hdr = profile == AstcProfile.Hdr, srgb = profile == AstcProfile.LdrSrgb;
            var f = KtxFormats.Astc(hd.BlockWidth, hd.BlockHeight, hd.BlockDepth, srgb, hdr);
            int bw = (hd.Width + hd.BlockWidth - 1) / hd.BlockWidth, bh = (hd.Height + hd.BlockHeight - 1) / hd.BlockHeight;
            long sliceBlocks = (long)bw * bh * 16;
            RawImage[] r;
            if (hd.BlockDepth == 1)
            {
                r = new RawImage[slices];
                for (int z = 0; z < slices; z++) r[z] = KtxBlocks.Astc(f, data.Slice((int)(16 + z * sliceBlocks), (int)sliceBlocks), hd.Width, hd.Height);
            }
            else
            {
                // 3D blocks: decode the block slices that cover the wanted image slices
                int zb = (slices + hd.BlockDepth - 1) / hd.BlockDepth;
                int d = Math.Min(hd.Depth, zb * hd.BlockDepth);
                var all = KtxBlocks.Astc3D(f, data.Slice(16, (int)(zb * sliceBlocks)), hd.Width, hd.Height, d);
                r = all.Length == slices ? all : all.AsSpan(0, slices).ToArray();
            }
            var tf = Transfer(profile);
            foreach (var img in r) img.Metadata = new ImageMetadata { Transfer = tf };
            return r;
        }

        /// <summary>Writes one image (any colour model with up to 4 channels; integer images LDR, float images HDR).</summary>
        public static void Encode(RawImage image, Stream output, AstcEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            EncodeVolume(new[] { image }, output, options);
        }

        public static byte[] Encode(RawImage image, AstcEncodeOptions? options = null)
        {
            var ms = new MemoryStream();
            Encode(image, ms, options);
            return ms.ToArray();
        }

        /// <summary>Writes a volume from its slices, front to back, all of the same size and format (2D footprint, block depth 1).</summary>
        public static void EncodeVolume(IReadOnlyList<RawImage> slices, Stream output, AstcEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(slices);
            ArgumentNullException.ThrowIfNull(output);
            options ??= new AstcEncodeOptions();
            if (slices.Count == 0) throw new ArgumentException("ASTC: at least one slice needed");
            int bw = options.BlockWidth, bh = options.BlockHeight;
            if (Array.IndexOf(Footprints2D, (bw, bh)) < 0) throw new ArgumentException($"ASTC: {bw}x{bh} is not a 2D ASTC footprint");
            var first = slices[0];
            if (first.Width > 0xFFFFFF || first.Height > 0xFFFFFF || slices.Count > 0xFFFFFF) throw new ArgumentException("ASTC: dimensions are at most 2^24 - 1");
            var ko = new KtxEncodeOptions
            {
                Compression = TextureCompression.Astc, AstcBlockWidth = bw, AstcBlockHeight = bh, Quality = options.Quality,
                Srgb = options.Srgb, MaxDegreeOfParallelism = options.MaxDegreeOfParallelism,
            };
            var t = KtxWriter.Choose(first, ko);
            Span<byte> head = stackalloc byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(head, 0x5CA1AB13);
            head[4] = (byte)bw; head[5] = (byte)bh; head[6] = 1;
            void U24(Span<byte> s, int v) { s[0] = (byte)v; s[1] = (byte)(v >> 8); s[2] = (byte)(v >> 16); }
            U24(head.Slice(7), first.Width); U24(head.Slice(10), first.Height); U24(head.Slice(13), slices.Count);
            output.Write(head);
            foreach (var s in slices)
            {
                if (s.Width != first.Width || s.Height != first.Height || s.SampleType != first.SampleType || s.Color != first.Color || s.Channels != first.Channels)
                    throw new ArgumentException("ASTC: all slices need the same size, sample type and colour model");
                output.Write(KtxWriter.Image(s, t, ko));
            }
        }

        public static byte[] EncodeVolume(IReadOnlyList<RawImage> slices, AstcEncodeOptions? options = null)
        {
            var ms = new MemoryStream();
            EncodeVolume(slices, ms, options);
            return ms.ToArray();
        }
    }
}
