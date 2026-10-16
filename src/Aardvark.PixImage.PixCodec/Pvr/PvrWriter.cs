using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Aardvark.ImageCodecs.KtxImpl;

namespace Aardvark.ImageCodecs.PvrImpl
{
    internal static class PvrWriter
    {
        /// <summary>What is written: the v3 pixel format, channel type, colour space and how an image becomes bytes.</summary>
        sealed class Target
        {
            public ulong PixelFormat;
            public PvrChannelType ChannelType;
            public bool Srgb;
            public KtxWriter.Target? Block;   // compressed: the KTX encoder's choice
            public KtxEncodeOptions Ktx = null!;
        }

        static Target Choose(RawImage img, PvrEncodeOptions opt)
        {
            if (img.Channels > 4) throw new ImageNotSupportedException("PVR: at most four channels can be written");
            var ko = opt.ToKtx();
            var t = new Target { Ktx = ko };
            if (opt.Compression != TextureCompression.None)
            {
                if (opt.Compression is TextureCompression.Etc1s or TextureCompression.Uastc)
                    throw new ImageNotSupportedException("PVR: the Basis Universal formats are written as KTX 2 only");
                var b = KtxWriter.Choose(img, ko);
                t.Block = b;
                t.Srgb = b.Srgb;
                int vk = b.Vk;
                (int id, PvrChannelType ct) = vk switch
                {
                    131 or 132 or 133 or 134 => (7, PvrChannelType.UnsignedByteNorm),
                    135 or 136 => (9, PvrChannelType.UnsignedByteNorm),
                    137 or 138 => (11, PvrChannelType.UnsignedByteNorm),
                    139 => (12, PvrChannelType.UnsignedByteNorm),
                    140 => (12, PvrChannelType.SignedByteNorm),
                    141 => (13, PvrChannelType.UnsignedByteNorm),
                    142 => (13, PvrChannelType.SignedByteNorm),
                    143 => (14, PvrChannelType.UnsignedFloat),
                    144 => (14, PvrChannelType.SignedFloat),
                    145 or 146 => (15, PvrChannelType.UnsignedByteNorm),
                    147 or 148 => (22, PvrChannelType.UnsignedByteNorm),
                    149 or 150 => (24, PvrChannelType.UnsignedByteNorm),
                    151 or 152 => (23, PvrChannelType.UnsignedByteNorm),
                    153 => (25, PvrChannelType.UnsignedShortNorm),
                    154 => (25, PvrChannelType.SignedShortNorm),
                    155 => (26, PvrChannelType.UnsignedShortNorm),
                    156 => (26, PvrChannelType.SignedShortNorm),
                    >= 157 and <= 184 => (27 + (vk - 157) / 2, PvrChannelType.UnsignedByteNorm),
                    >= 1000066000 and <= 1000066013 => (27 + vk - 1000066000, PvrChannelType.SignedFloat),
                    _ => throw new ImageNotSupportedException($"PVR: {KtxFormats.VkName(vk)} cannot be written"),
                };
                t.PixelFormat = (ulong)id;
                t.ChannelType = ct;
                return t;
            }
            int ch = img.Channels, bits = RawImage.BytesPerSample(img.SampleType) * 8;
            string names = img.Color switch
            {
                ColorModel.Gray => "l",
                ColorModel.GrayAlpha => "la",
                ColorModel.Rgb => "rgb",
                ColorModel.Rgba => "rgba",
                _ => "rgba".Substring(0, ch),
            };
            t.ChannelType = img.SampleType switch
            {
                SampleType.UInt8 => PvrChannelType.UnsignedByteNorm,
                SampleType.Int8 => PvrChannelType.SignedByteNorm,
                SampleType.UInt16 => PvrChannelType.UnsignedShortNorm,
                SampleType.Int16 => PvrChannelType.SignedShortNorm,
                SampleType.UInt32 => PvrChannelType.UnsignedInt,
                SampleType.Int32 => PvrChannelType.SignedInt,
                SampleType.Float16 or SampleType.Float32 => PvrChannelType.SignedFloat,
                _ => throw new ImageNotSupportedException($"PVR: {img.SampleType} samples cannot be written"),
            };
            ulong pf = 0;
            for (int k = 0; k < names.Length; k++) pf |= (ulong)names[k] << (8 * k) | (ulong)bits << (32 + 8 * k);
            t.PixelFormat = pf;
            t.Srgb = img.SampleType == SampleType.UInt8 && img.Color != ColorModel.Other && KtxWriter.ResolveSrgb(img, ko);
            return t;
        }

        static byte[] Image(RawImage img, Target t) => t.Block != null ? KtxWriter.Image(img, t.Block, t.Ktx) : img.GetBytes().ToArray();

        /// <summary>[surface][face][mip] images.</summary>
        public static void Write(Stream output, IReadOnlyList<IReadOnlyList<IReadOnlyList<RawImage>>> surfaces, int depth, PvrEncodeOptions opt)
        {
            var first = surfaces[0][0][0];
            int faces = surfaces[0].Count, mips = surfaces[0][0].Count;
            int w = first.Width, h = first.Height;
            if (mips > 1 + System.Numerics.BitOperations.Log2((uint)Math.Max(w, h))) throw new ArgumentException("PVR: too many mip levels");
            foreach (var surface in surfaces)
            {
                if (surface.Count != faces) throw new ArgumentException("PVR: every surface needs the same faces");
                foreach (var chain in surface)
                {
                    if (chain.Count != mips) throw new ArgumentException("PVR: every face needs the same number of mip levels");
                    for (int m = 0; m < mips; m++)
                    {
                        var l = chain[m];
                        int sw = Math.Max(1, w >> m), sh = Math.Max(1, h >> m);
                        if (l.Width != sw || l.Height != sh) throw new ArgumentException($"PVR: mip level {m} must be {sw} x {sh}, not {l.Width} x {l.Height}");
                        if (l.SampleType != first.SampleType || l.Color != first.Color || l.Channels != first.Channels)
                            throw new ArgumentException("PVR: all images need the same sample type and colour model");
                    }
                }
            }
            var t = Choose(first, opt);
            Header(output, t, w, h, 1, surfaces.Count, faces, mips);
            for (int m = 0; m < mips; m++)
                foreach (var surface in surfaces)
                    foreach (var chain in surface)
                        output.Write(Image(chain[m], t));
        }

        public static void WriteVolume(Stream output, IReadOnlyList<RawImage> slices, PvrEncodeOptions opt)
        {
            var chain = KtxWriter.VolumeChain(slices, opt.ToKtx());
            var first = slices[0];
            var t = Choose(first, opt);
            Header(output, t, first.Width, first.Height, slices.Count, 1, 1, chain.Count);
            foreach (var level in chain)
                foreach (var s in level)
                    output.Write(Image(s, t));
        }

        static void Header(Stream o, Target t, int w, int h, int depth, int surfaces, int faces, int mips)
        {
            Span<byte> b = stackalloc byte[52 + 15];
            BinaryPrimitives.WriteUInt32LittleEndian(b, PvrReader.Magic3);
            BinaryPrimitives.WriteUInt32LittleEndian(b.Slice(4), 0);
            BinaryPrimitives.WriteUInt64LittleEndian(b.Slice(8), t.PixelFormat);
            BinaryPrimitives.WriteUInt32LittleEndian(b.Slice(16), t.Srgb ? 1u : 0u);
            BinaryPrimitives.WriteUInt32LittleEndian(b.Slice(20), (uint)t.ChannelType);
            BinaryPrimitives.WriteUInt32LittleEndian(b.Slice(24), (uint)h);
            BinaryPrimitives.WriteUInt32LittleEndian(b.Slice(28), (uint)w);
            BinaryPrimitives.WriteUInt32LittleEndian(b.Slice(32), (uint)depth);
            BinaryPrimitives.WriteUInt32LittleEndian(b.Slice(36), (uint)surfaces);
            BinaryPrimitives.WriteUInt32LittleEndian(b.Slice(40), (uint)faces);
            BinaryPrimitives.WriteUInt32LittleEndian(b.Slice(44), (uint)mips);
            BinaryPrimitives.WriteUInt32LittleEndian(b.Slice(48), 15);
            // orientation metadata: x to the right, y down, z inwards (what is stored: top row first)
            BinaryPrimitives.WriteUInt32LittleEndian(b.Slice(52), PvrReader.Magic3);
            BinaryPrimitives.WriteUInt32LittleEndian(b.Slice(56), 3);
            BinaryPrimitives.WriteUInt32LittleEndian(b.Slice(60), 3);
            b.Slice(64, 3).Clear();
            o.Write(b);
        }
    }
}
