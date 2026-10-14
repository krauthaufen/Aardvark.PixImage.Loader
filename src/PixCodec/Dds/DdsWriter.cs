using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace PixCodec.DdsImpl
{
    internal static class DdsWriter
    {
        /// <summary>The image, followed by computed mip levels when the options ask for them.</summary>
        public static IReadOnlyList<RawImage> Chain(RawImage image, DdsEncodeOptions opt)
        {
            if (opt.Mipmaps == DdsMipmaps.None) return new[] { image };
            var levels = new List<RawImage> { image };
            int w = image.Width, h = image.Height;
            var ro = new ResizeOptions { Filter = opt.MipmapFilter, MaxDegreeOfParallelism = opt.MaxDegreeOfParallelism };
            while (w > 1 || h > 1)
            {
                w = Math.Max(1, w / 2); h = Math.Max(1, h / 2);
                levels.Add(ImageOps.Resize(image, w, h, ro));
            }
            return levels;
        }

        sealed class Target
        {
            public int Dxgi;                       // 0: legacy header
            public uint PfFlags, Bits, R, G, B, A;
            public uint FourCC;                    // legacy block compressed formats
            public int Channels;                   // stored channels (images with fewer are expanded)
            public bool Bgr;                       // legacy 8 bit colour: B, G, R(, A) byte order
            public DdsCompression Bc;              // None: uncompressed
            public int BlockBytes;
            public bool Signed;                    // BC4 / BC5 SNORM, BC6H SF16
            public bool Alpha;                     // BC1: punch-through alpha
        }

        static uint FourCC(string s) => (uint)(s[0] | s[1] << 8 | s[2] << 16 | s[3] << 24);

        static Target Choose(RawImage img, bool srgb)
        {
            var c = img.Color;
            int ch = img.Channels;
            bool two = c == ColorModel.Other && ch == 2;
            if (c == ColorModel.Other && !two) throw new ImageNotSupportedException("DDS: only Gray, GrayAlpha, Rgb, Rgba and two channel images can be written");
            switch (img.SampleType)
            {
                case SampleType.UInt8:
                    if (two) return new Target { Dxgi = 49, Channels = 2 };
                    if (srgb) return new Target { Dxgi = 29, Channels = 4 };
                    return c switch
                    {
                        ColorModel.Gray => new Target { PfFlags = 0x20000, Bits = 8, R = 0xFF, Channels = 1 },
                        ColorModel.GrayAlpha => new Target { PfFlags = 0x20001, Bits = 16, R = 0xFF, A = 0xFF00, Channels = 2 },
                        ColorModel.Rgb => new Target { PfFlags = 0x40, Bits = 24, R = 0xFF0000, G = 0xFF00, B = 0xFF, Channels = 3, Bgr = true },
                        _ => new Target { PfFlags = 0x41, Bits = 32, R = 0xFF0000, G = 0xFF00, B = 0xFF, A = 0xFF000000, Channels = 4, Bgr = true },
                    };
                case SampleType.UInt16:
                    return two ? new Target { Dxgi = 35, Channels = 2 } : c == ColorModel.Gray ? new Target { Dxgi = 56, Channels = 1 } : new Target { Dxgi = 11, Channels = 4 };
                case SampleType.Float16:
                    return two ? new Target { Dxgi = 34, Channels = 2 } : c == ColorModel.Gray ? new Target { Dxgi = 54, Channels = 1 } : new Target { Dxgi = 10, Channels = 4 };
                case SampleType.Float32:
                    return two ? new Target { Dxgi = 16, Channels = 2 } : c switch
                    {
                        ColorModel.Gray => new Target { Dxgi = 41, Channels = 1 },
                        ColorModel.Rgb => new Target { Dxgi = 6, Channels = 3 },
                        _ => new Target { Dxgi = 2, Channels = 4 },
                    };
                default:
                    throw new ImageNotSupportedException($"DDS: {img.SampleType} samples cannot be written");
            }
        }

        static bool IsSignedType(SampleType t) => t is SampleType.Int8 or SampleType.Int16 or SampleType.Int32 or SampleType.Int64;

        /// <summary>The block compressed format for the options; <paramref name="anyNegative"/> decides BC6H SF16.</summary>
        static Target ChooseCompressed(RawImage img, DdsEncodeOptions opt, Func<bool> anyNegative)
        {
            bool srgb = opt.Srgb;
            bool signed = IsSignedType(img.SampleType);
            if (img.Color == ColorModel.Other && img.Channels > 4) throw new ImageNotSupportedException("DDS: too many channels for block compression");
            return opt.Compression switch
            {
                DdsCompression.BC1 => new Target { Bc = DdsCompression.BC1, BlockBytes = 8, Dxgi = srgb ? 72 : 0, FourCC = srgb ? 0 : FourCC("DXT1"), Alpha = img.HasAlpha },
                DdsCompression.BC2 => new Target { Bc = DdsCompression.BC2, BlockBytes = 16, Dxgi = srgb ? 75 : 0, FourCC = srgb ? 0 : FourCC("DXT3") },
                DdsCompression.BC3 => new Target { Bc = DdsCompression.BC3, BlockBytes = 16, Dxgi = srgb ? 78 : 0, FourCC = srgb ? 0 : FourCC("DXT5") },
                DdsCompression.BC4 => new Target { Bc = DdsCompression.BC4, BlockBytes = 8, Dxgi = signed ? 81 : 0, FourCC = signed ? 0 : FourCC("ATI1"), Signed = signed },
                DdsCompression.BC5 => img.Channels < 2 ? throw new ImageNotSupportedException("DDS: BC5 needs two channels")
                    : new Target { Bc = DdsCompression.BC5, BlockBytes = 16, Dxgi = signed ? 84 : 0, FourCC = signed ? 0 : FourCC("ATI2"), Signed = signed },
                DdsCompression.BC6H => anyNegative() ? new Target { Bc = DdsCompression.BC6H, BlockBytes = 16, Dxgi = 96, Signed = true }
                    : new Target { Bc = DdsCompression.BC6H, BlockBytes = 16, Dxgi = 95 },
                DdsCompression.BC7 => new Target { Bc = DdsCompression.BC7, BlockBytes = 16, Dxgi = srgb ? 99 : 98 },
                _ => throw new ArgumentOutOfRangeException(nameof(opt.Compression)),
            };
        }

        /// <summary>
        /// Writes a texture: <paramref name="elements"/> are the cube faces (or the single 2D / volume texture), each a list of
        /// mip levels, each a list of slices (one, except for volume textures).
        /// </summary>
        public static void Write(Stream output, IReadOnlyList<IReadOnlyList<IReadOnlyList<RawImage>>> elements, DdsTextureKind kind, DdsEncodeOptions opt)
        {
            var first = elements[0][0][0];
            int w = first.Width, h = first.Height, mips = elements[0].Count, depth = elements[0][0].Count;
            if (kind != DdsTextureKind.Texture3D && depth != 1) throw new ArgumentException("DDS: only volume textures have slices");
            if (depth < 1) throw new ArgumentException("DDS: a volume needs at least one slice");
            foreach (var chain in elements)
            {
                if (chain.Count != mips) throw new ArgumentException("DDS: every face needs the same number of mip levels");
                for (int m = 0; m < chain.Count; m++)
                {
                    int sw = Math.Max(1, w >> m), sh = Math.Max(1, h >> m), sd = Math.Max(1, depth >> m);
                    if (chain[m].Count != sd) throw new ArgumentException($"DDS: mip level {m} must have {sd} slices, not {chain[m].Count}");
                    foreach (var l in chain[m])
                    {
                        if (l.Width != sw || l.Height != sh)
                            throw new ArgumentException($"DDS: mip level {m} must be {sw} x {sh}, not {l.Width} x {l.Height}");
                        if (l.SampleType != first.SampleType || l.Color != first.Color || l.Channels != first.Channels)
                            throw new ArgumentException("DDS: all surfaces need the same sample type and colour model");
                    }
                }
            }
            if (kind == DdsTextureKind.CubeMap && w != h) throw new ArgumentException("DDS: cube faces must be square");
            if (mips > 1 + System.Numerics.BitOperations.Log2((uint)Math.Max(Math.Max(w, h), depth))) throw new ArgumentException("DDS: too many mip levels");

            var t = opt.Compression == DdsCompression.None ? Choose(first, opt.Srgb) : ChooseCompressed(first, opt, () => AnyNegative(elements));
            bool dx10 = t.Dxgi != 0;
            int pixelBytes = t.Bc != DdsCompression.None ? 0 : dx10 ? t.Channels * RawImage.BytesPerSample(first.SampleType) : (int)t.Bits / 8;
            bool volume = kind == DdsTextureKind.Texture3D;

            var head = new byte[dx10 ? 148 : 128];
            var s = head.AsSpan();
            BinaryPrimitives.WriteUInt32LittleEndian(s, 0x20534444);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(4), 124);
            uint flags = 0x1 | 0x2 | 0x4 | 0x1000 | (mips > 1 ? 0x20000u : 0) | (volume ? 0x800000u : 0);
            flags |= t.Bc != DdsCompression.None ? 0x80000u : 0x8u;   // linear size / pitch
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(8), flags);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(12), (uint)h);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(16), (uint)w);
            uint pitch = t.Bc != DdsCompression.None
                ? (uint)Math.Min(uint.MaxValue, (long)Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * t.BlockBytes)
                : (uint)(w * pixelBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(20), pitch);
            if (volume) BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(24), (uint)depth);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(28), (uint)mips);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(76), 32);
            if (dx10)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(80), 4);
                "DX10"u8.CopyTo(s.Slice(84));
            }
            else if (t.FourCC != 0)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(80), 4);
                BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(84), t.FourCC);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(80), t.PfFlags);
                BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(88), t.Bits);
                BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(92), t.R);
                BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(96), t.G);
                BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(100), t.B);
                BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(104), t.A);
            }
            bool complex = mips > 1 || kind == DdsTextureKind.CubeMap || volume;
            uint caps = 0x1000 | (complex ? 0x8u : 0) | (mips > 1 ? 0x400000u : 0);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(108), caps);
            uint caps2 = (kind == DdsTextureKind.CubeMap ? 0x200u | 0xFC00 : 0) | (volume ? 0x200000u : 0);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(112), caps2);
            if (dx10)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(128), (uint)t.Dxgi);
                BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(132), volume ? 4u : 3u);
                BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(136), kind == DdsTextureKind.CubeMap ? 4u : 0);
                BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(140), 1);
                bool alpha = t.Bc is DdsCompression.BC1 or DdsCompression.BC2 or DdsCompression.BC3 or DdsCompression.BC7 ? first.HasAlpha : t.Channels == 4;
                BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(144), alpha ? 1u : 0);   // straight alpha
            }
            output.Write(head);

            foreach (var chain in elements)
                foreach (var level in chain)
                    foreach (var slice in level)
                        output.Write(t.Bc != DdsCompression.None ? Compress(slice, t, opt) : Pixels(slice, t));
        }

        static bool AnyNegative(IReadOnlyList<IReadOnlyList<IReadOnlyList<RawImage>>> elements)
        {
            foreach (var chain in elements)
                foreach (var level in chain)
                    foreach (var img in level)
                    {
                        if (!IsSignedType(img.SampleType) && img.SampleType is not (SampleType.Float16 or SampleType.Float32 or SampleType.Float64)) continue;
                        long n = (long)img.Width * img.Height * img.Channels;
                        for (long i = 0; i < n; i++)
                            if (Normalized(img, i) < 0) return true;
                    }
            return false;
        }

        // ------------------------------------------------------------------ block compression

        /// <summary>Sample i as a number: integers scaled to [0, 1] ([-1, 1] signed), floats as they are.</summary>
        static float Normalized(RawImage img, long i) => img.SampleType switch
        {
            SampleType.UInt8 => ((byte[])img.Data)[i] / 255f,
            SampleType.Int8 => Math.Max(-1f, ((sbyte[])img.Data)[i] / 127f),
            SampleType.UInt16 => ((ushort[])img.Data)[i] / 65535f,
            SampleType.Int16 => Math.Max(-1f, ((short[])img.Data)[i] / 32767f),
            SampleType.UInt32 => (float)(((uint[])img.Data)[i] / (double)uint.MaxValue),
            SampleType.Int32 => (float)Math.Max(-1, ((int[])img.Data)[i] / (double)int.MaxValue),
            SampleType.Float16 => (float)((Half[])img.Data)[i],
            SampleType.Float32 => ((float[])img.Data)[i],
            SampleType.Float64 => (float)((double[])img.Data)[i],
            _ => throw new ImageNotSupportedException($"DDS: {img.SampleType} samples cannot be block compressed"),
        };

        static byte ToUnorm8(RawImage img, long i)
            => img.SampleType == SampleType.UInt8 ? ((byte[])img.Data)[i] : (byte)MathF.Round(Math.Clamp(Normalized(img, i), 0, 1) * 255);

        static int ToSnorm8(RawImage img, long i)
            => img.SampleType == SampleType.Int8 ? Math.Max(-127, (int)((sbyte[])img.Data)[i]) : (int)MathF.Round(Math.Clamp(Normalized(img, i), -1, 1) * 127);

        static byte[] Compress(RawImage img, Target t, DdsEncodeOptions opt)
        {
            int w = img.Width, h = img.Height, bw = Math.Max(1, (w + 3) / 4), bh = Math.Max(1, (h + 3) / 4);
            var o = new byte[(long)bw * bh * t.BlockBytes];
            var q = opt.Quality;
            int ch = img.Channels;
            bool gray = img.Color is ColorModel.Gray or ColorModel.GrayAlpha;
            bool alpha = img.HasAlpha;
            var par = new ParallelOptions { MaxDegreeOfParallelism = opt.MaxDegreeOfParallelism > 0 ? opt.MaxDegreeOfParallelism : -1 };
            Parallel.For(0, bh, par, by =>
            {
                Span<byte> rgba = stackalloc byte[64];
                Span<int> v = stackalloc int[16];
                Span<float> f = stackalloc float[48];
                for (int bx = 0; bx < bw; bx++)
                {
                    var dst = o.AsSpan((by * bw + bx) * t.BlockBytes, t.BlockBytes);
                    // texel k of the block, edge pixels repeated beyond the image
                    long Pix(int k) => ((long)Math.Min(by * 4 + k / 4, h - 1) * w + Math.Min(bx * 4 + k % 4, w - 1)) * ch;
                    switch (t.Bc)
                    {
                        case DdsCompression.BC4:
                            for (int k = 0; k < 16; k++) v[k] = t.Signed ? ToSnorm8(img, Pix(k)) : ToUnorm8(img, Pix(k));
                            BcEncoder.Channel(v, dst, t.Signed, q);
                            break;
                        case DdsCompression.BC5:
                            for (int c = 0; c < 2; c++)
                            {
                                for (int k = 0; k < 16; k++) v[k] = t.Signed ? ToSnorm8(img, Pix(k) + c) : ToUnorm8(img, Pix(k) + c);
                                BcEncoder.Channel(v, dst.Slice(c * 8, 8), t.Signed, q);
                            }
                            break;
                        case DdsCompression.BC6H:
                            for (int k = 0; k < 16; k++)
                            {
                                long p = Pix(k);
                                for (int c = 0; c < 3; c++) f[k * 3 + c] = Normalized(img, p + (gray ? 0 : Math.Min(c, ch - 1)));
                            }
                            Bc6hEncoder.Encode(f, dst, t.Signed, q);
                            break;
                        default:
                            for (int k = 0; k < 16; k++)
                            {
                                long p = Pix(k);
                                for (int c = 0; c < 3; c++) rgba[k * 4 + c] = ToUnorm8(img, p + (gray ? 0 : Math.Min(c, ch - 1)));
                                rgba[k * 4 + 3] = alpha ? ToUnorm8(img, p + ch - 1) : (byte)255;
                            }
                            switch (t.Bc)
                            {
                                case DdsCompression.BC1: BcEncoder.Color(rgba, dst, t.Alpha, false, q); break;
                                case DdsCompression.BC2: BcEncoder.ExplicitAlpha(rgba, dst); BcEncoder.Color(rgba, dst.Slice(8), false, true, q); break;
                                case DdsCompression.BC3:
                                    for (int k = 0; k < 16; k++) v[k] = rgba[k * 4 + 3];
                                    BcEncoder.Channel(v, dst, false, q);
                                    BcEncoder.Color(rgba, dst.Slice(8), false, true, q);
                                    break;
                                default: Bc7Encoder.Encode(rgba, dst, q); break;
                            }
                            break;
                    }
                }
            });
            return o;
        }

        // ------------------------------------------------------------------ uncompressed

        /// <summary>The stored bytes of one surface.</summary>
        static byte[] Pixels(RawImage img, Target t)
        {
            int n = img.Width * img.Height, ch = img.Channels;
            if (t.Dxgi == 0 && t.Bgr)
            {
                var src = img.GetData<byte>();
                var o = new byte[n * t.Channels];
                for (int i = 0; i < n; i++)
                {
                    o[i * t.Channels] = src[i * ch + 2]; o[i * t.Channels + 1] = src[i * ch + 1]; o[i * t.Channels + 2] = src[i * ch];
                    if (t.Channels == 4) o[i * 4 + 3] = src[i * ch + 3];
                }
                return o;
            }
            if (t.Channels == ch) return img.GetBytes().ToArray();
            // gray / gray + alpha / rgb expanded to rgba
            int bps = RawImage.BytesPerSample(img.SampleType);
            var s = img.GetBytes();
            var d = new byte[n * t.Channels * bps];
            var one = img.SampleType switch
            {
                SampleType.UInt8 => new byte[] { 255 },
                SampleType.UInt16 => BitConverter.GetBytes(ushort.MaxValue),
                SampleType.Float16 => BitConverter.GetBytes((Half)1),
                _ => BitConverter.GetBytes(1f),
            };
            for (int i = 0; i < n; i++)
            {
                var px = s.Slice(i * ch * bps, ch * bps);
                var o = d.AsSpan(i * 4 * bps, 4 * bps);
                bool gray = ch <= 2;
                for (int c = 0; c < 3; c++) px.Slice((gray ? 0 : c) * bps, bps).CopyTo(o.Slice(c * bps));
                if (ch == 2 || ch == 4) px.Slice((ch - 1) * bps, bps).CopyTo(o.Slice(3 * bps));
                else one.CopyTo(o.Slice(3 * bps));
            }
            return d;
        }
    }
}
