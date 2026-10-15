using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using PixCodec.Compression;
using PixCodec.DdsImpl;

namespace PixCodec.KtxImpl
{
    internal static class KtxWriter
    {
        static bool ResolveSrgb(RawImage img, KtxEncodeOptions opt)
            => opt.Srgb ?? (img.Metadata?.Transfer switch
            {
                TransferFunction.Srgb => true,
                TransferFunction.Unknown or null => img.SampleType == SampleType.UInt8 && img.Color != ColorModel.Other,
                _ => false,
            });

        /// <summary>The image, followed by computed mip levels when the options ask for them.</summary>
        public static IReadOnlyList<RawImage> Chain(RawImage image, KtxEncodeOptions opt)
        {
            if (!opt.GenerateMipmaps) return new[] { image };
            var levels = new List<RawImage> { image };
            int w = image.Width, h = image.Height;
            var ro = new ResizeOptions { Filter = opt.MipmapFilter, MaxDegreeOfParallelism = opt.MaxDegreeOfParallelism, LinearLight = ResolveSrgb(image, opt) };
            while (w > 1 || h > 1)
            {
                w = Math.Max(1, w / 2); h = Math.Max(1, h / 2);
                levels.Add(ImageOps.Resize(image, w, h, ro));
            }
            return levels;
        }

        /// <summary>[layer][face][mip] from per layer face lists.</summary>
        public static IReadOnlyList<IReadOnlyList<IReadOnlyList<RawImage>>> Layers(IReadOnlyList<IReadOnlyList<RawImage>>[] layers) => layers;

        sealed class Target
        {
            public int Vk;
            public int Channels;               // stored channels (images may be expanded)
            public TextureCompression Bc;
            public bool Signed;
            public bool Srgb;
            public string? Swizzle;
            public KtxFormat Format = null!;
        }

        static Target Choose(RawImage img, KtxEncodeOptions opt)
        {
            if (img.Channels > 4) throw new ImageNotSupportedException("KTX: at most four channels can be written");
            bool srgb = ResolveSrgb(img, opt);
            var t = new Target { Bc = opt.Compression };
            bool signedInt = img.SampleType is SampleType.Int8 or SampleType.Int16 or SampleType.Int32 or SampleType.Int64;
            if (opt.Compression != TextureCompression.None)
            {
                bool colour = opt.Compression is TextureCompression.BC1 or TextureCompression.BC2 or TextureCompression.BC3 or TextureCompression.BC7;
                t.Srgb = srgb && colour;
                switch (opt.Compression)
                {
                    case TextureCompression.BC1: t.Vk = img.HasAlpha ? (t.Srgb ? 134 : 133) : (t.Srgb ? 132 : 131); break;
                    case TextureCompression.BC2: t.Vk = t.Srgb ? 136 : 135; break;
                    case TextureCompression.BC3: t.Vk = t.Srgb ? 138 : 137; break;
                    case TextureCompression.BC4: t.Signed = signedInt; t.Vk = signedInt ? 140 : 139; break;
                    case TextureCompression.BC5:
                        if (img.Channels < 2) throw new ImageNotSupportedException("KTX: BC5 needs two channels");
                        t.Signed = signedInt; t.Vk = signedInt ? 142 : 141; break;
                    case TextureCompression.BC6H: t.Signed = DdsWriter.HasNegative(img); t.Vk = t.Signed ? 144 : 143; break;
                    case TextureCompression.BC7: t.Vk = t.Srgb ? 146 : 145; break;
                    default: throw new ArgumentOutOfRangeException(nameof(opt.Compression));
                }
                if (colour && img.Color == ColorModel.Gray) t.Swizzle = img.HasAlpha ? null : "rrr1";
                t.Format = KtxFormats.FromVk(t.Vk)!;
                return t;
            }
            int ch = img.Channels;
            bool u8 = img.SampleType == SampleType.UInt8;
            t.Srgb = srgb && u8 && img.Color != ColorModel.Other;
            // sRGB applies to R, G, B: gray + alpha would have its alpha decoded as colour in R8G8_SRGB, so it becomes RGBA
            if (t.Srgb && img.Color == ColorModel.GrayAlpha) ch = 4;
            if (img.Color == ColorModel.Gray) t.Swizzle = "rrr1";
            else if (img.Color == ColorModel.GrayAlpha && ch == 2) t.Swizzle = "rrrg";
            t.Channels = ch;
            int[] row = img.SampleType switch
            {
                SampleType.UInt8 => t.Srgb ? new[] { 15, 22, 29, 43 } : new[] { 9, 16, 23, 37 },
                SampleType.Int8 => new[] { 10, 17, 24, 38 },
                SampleType.UInt16 => new[] { 70, 77, 84, 91 },
                SampleType.Int16 => new[] { 71, 78, 85, 92 },
                SampleType.UInt32 => new[] { 98, 101, 104, 107 },
                SampleType.Int32 => new[] { 99, 102, 105, 108 },
                SampleType.Float16 => new[] { 76, 83, 90, 97 },
                SampleType.Float32 => new[] { 100, 103, 106, 109 },
                _ => throw new ImageNotSupportedException($"KTX: {img.SampleType} samples cannot be written"),
            };
            t.Vk = row[ch - 1];
            t.Format = KtxFormats.FromVk(t.Vk)!;
            return t;
        }

        static DdsCompression ToDds(TextureCompression c) => c switch
        {
            TextureCompression.BC1 => DdsCompression.BC1,
            TextureCompression.BC2 => DdsCompression.BC2,
            TextureCompression.BC3 => DdsCompression.BC3,
            TextureCompression.BC4 => DdsCompression.BC4,
            TextureCompression.BC5 => DdsCompression.BC5,
            TextureCompression.BC6H => DdsCompression.BC6H,
            TextureCompression.BC7 => DdsCompression.BC7,
            _ => DdsCompression.None,
        };

        /// <summary>The stored bytes of one image.</summary>
        static byte[] Image(RawImage img, Target t, KtxEncodeOptions opt)
        {
            if (t.Bc != TextureCompression.None)
                return DdsWriter.CompressSurface(img, ToDds(t.Bc), t.Signed, new DdsEncodeOptions { Quality = opt.Quality, MaxDegreeOfParallelism = opt.MaxDegreeOfParallelism });
            if (t.Channels == img.Channels) return img.GetBytes().ToArray();
            // gray + alpha expanded to RGBA (8 bit sRGB)
            var s = (byte[])img.Data;
            int n = img.Width * img.Height;
            var o = new byte[n * 4];
            for (int i = 0; i < n; i++) { o[i * 4] = o[i * 4 + 1] = o[i * 4 + 2] = s[i * 2]; o[i * 4 + 3] = s[i * 2 + 1]; }
            return o;
        }

        public static void Write(Stream output, IReadOnlyList<IReadOnlyList<IReadOnlyList<RawImage>>> layers, KtxTextureKind kind, bool isArray, KtxEncodeOptions opt)
        {
            var first = layers[0][0][0];
            int faces = layers[0].Count, mips = layers[0][0].Count;
            int w = first.Width, h = first.Height;
            if (kind == KtxTextureKind.CubeMap && w != h) throw new ArgumentException("KTX: cube faces must be square");
            if (mips > 1 + System.Numerics.BitOperations.Log2((uint)Math.Max(w, h))) throw new ArgumentException("KTX: too many mip levels");
            foreach (var layer in layers)
            {
                if (layer.Count != faces) throw new ArgumentException("KTX: every layer needs the same faces");
                foreach (var chain in layer)
                {
                    if (chain.Count != mips) throw new ArgumentException("KTX: every face needs the same number of mip levels");
                    for (int m = 0; m < mips; m++)
                    {
                        var l = chain[m];
                        int sw = Math.Max(1, w >> m), sh = Math.Max(1, h >> m);
                        if (l.Width != sw || l.Height != sh) throw new ArgumentException($"KTX: mip level {m} must be {sw} x {sh}, not {l.Width} x {l.Height}");
                        if (l.SampleType != first.SampleType || l.Color != first.Color || l.Channels != first.Channels)
                            throw new ArgumentException("KTX: all images need the same sample type and colour model");
                    }
                }
            }
            var t = Choose(first, opt);
            var levels = new byte[mips][];
            for (int m = 0; m < mips; m++)
            {
                var ms = new MemoryStream();
                foreach (var layer in layers)
                    foreach (var chain in layer)
                        ms.Write(Image(chain[m], t, opt));
                levels[m] = ms.ToArray();
            }
            Emit(output, t, w, h, 0, isArray ? layers.Count : 0, faces, levels, kind, opt);
        }

        public static void WriteVolume(Stream output, IReadOnlyList<RawImage> slices, KtxEncodeOptions opt)
        {
            var first = slices[0];
            foreach (var s in slices)
                if (s.Width != first.Width || s.Height != first.Height || s.SampleType != first.SampleType || s.Color != first.Color || s.Channels != first.Channels)
                    throw new ArgumentException("KTX: all slices need the same size, sample type and colour model");
            var chain = new List<IReadOnlyList<RawImage>> { slices };
            if (opt.GenerateMipmaps)
            {
                var ro = new ResizeOptions { Filter = ResampleFilter.Box, MaxDegreeOfParallelism = opt.MaxDegreeOfParallelism, LinearLight = ResolveSrgb(first, opt) };
                var cur = slices;
                int w = first.Width, h = first.Height, d = slices.Count;
                while (w > 1 || h > 1 || d > 1)
                {
                    int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2), nd = Math.Max(1, d / 2);
                    var next = new RawImage[nd];
                    for (int z = 0; z < nd; z++)
                    {
                        var a = ImageOps.Resize(cur[Math.Min(2 * z, d - 1)], nw, nh, ro);
                        next[z] = d > 1 ? Average(a, ImageOps.Resize(cur[Math.Min(2 * z + 1, d - 1)], nw, nh, ro), ro.LinearLight) : a;
                    }
                    chain.Add(next);
                    cur = next; w = nw; h = nh; d = nd;
                }
            }
            var t = Choose(first, opt);
            var levels = new byte[chain.Count][];
            for (int m = 0; m < chain.Count; m++)
            {
                var ms = new MemoryStream();
                foreach (var s in chain[m]) ms.Write(Image(s, t, opt));
                levels[m] = ms.ToArray();
            }
            Emit(output, t, first.Width, first.Height, slices.Count, 0, 1, levels, KtxTextureKind.Texture3D, opt);
        }

        /// <summary>Sample-wise mean of two images (in linear light for sRGB data).</summary>
        static RawImage Average(RawImage a, RawImage b, bool srgb)
        {
            if (srgb)
            {
                var la = ImageOps.SrgbToLinear(a).GetData<float>();
                var lb = ImageOps.SrgbToLinear(b).GetData<float>();
                var m = new float[la.Length];
                for (int i = 0; i < m.Length; i++) m[i] = (la[i] + lb[i]) * 0.5f;
                var lin = new RawImage(a.Width, a.Height, a.Color, m, a.Channels);
                return ImageOps.LinearToSrgb(lin, a.SampleType);
            }
            var r = RawImage.Create(a.Width, a.Height, a.Color, a.SampleType, a.Channels);
            // by sample type: sbyte[] / short[] / int[] would also match the unsigned patterns (CLR array covariance)
            switch (a.SampleType)
            {
                case SampleType.UInt8: { var x = (byte[])a.Data; var y = (byte[])b.Data; var o = (byte[])r.Data; for (int i = 0; i < o.Length; i++) o[i] = (byte)((x[i] + y[i] + 1) >> 1); break; }
                case SampleType.Int8: { var x = (sbyte[])a.Data; var y = (sbyte[])b.Data; var o = (sbyte[])r.Data; for (int i = 0; i < o.Length; i++) o[i] = (sbyte)Math.Round((x[i] + y[i]) / 2.0, MidpointRounding.AwayFromZero); break; }
                case SampleType.UInt16: { var x = (ushort[])a.Data; var y = (ushort[])b.Data; var o = (ushort[])r.Data; for (int i = 0; i < o.Length; i++) o[i] = (ushort)((x[i] + y[i] + 1) >> 1); break; }
                case SampleType.Int16: { var x = (short[])a.Data; var y = (short[])b.Data; var o = (short[])r.Data; for (int i = 0; i < o.Length; i++) o[i] = (short)Math.Round((x[i] + y[i]) / 2.0, MidpointRounding.AwayFromZero); break; }
                case SampleType.UInt32: { var x = (uint[])a.Data; var y = (uint[])b.Data; var o = (uint[])r.Data; for (int i = 0; i < o.Length; i++) o[i] = (uint)(((ulong)x[i] + y[i] + 1) >> 1); break; }
                case SampleType.Int32: { var x = (int[])a.Data; var y = (int[])b.Data; var o = (int[])r.Data; for (int i = 0; i < o.Length; i++) o[i] = (int)Math.Round(((long)x[i] + y[i]) / 2.0, MidpointRounding.AwayFromZero); break; }
                case SampleType.Float16: { var x = (Half[])a.Data; var y = (Half[])b.Data; var o = (Half[])r.Data; for (int i = 0; i < o.Length; i++) o[i] = (Half)(((float)x[i] + (float)y[i]) * 0.5f); break; }
                case SampleType.Float32: { var x = (float[])a.Data; var y = (float[])b.Data; var o = (float[])r.Data; for (int i = 0; i < o.Length; i++) o[i] = (x[i] + y[i]) * 0.5f; break; }
                default: throw new ImageNotSupportedException($"KTX: {a.SampleType} volumes cannot be mipmapped");
            }
            return r;
        }

        static void Emit(Stream output, Target t, int w, int h, int depth, int layers, int faces, byte[][] levels, KtxTextureKind kind, KtxEncodeOptions opt)
        {
            int levelCount = levels.Length;
            var scheme = opt.Supercompression;
            if (scheme is not (KtxSupercompression.None or KtxSupercompression.Zstd or KtxSupercompression.Zlib))
                throw new ArgumentException("KTX: only Zstd, Zlib or no supercompression can be written");
            var stored = new byte[levelCount][];
            for (int m = 0; m < levelCount; m++)
                stored[m] = scheme switch
                {
                    KtxSupercompression.Zstd => Zstd.Compress(levels[m], Math.Clamp(opt.ZstdLevel, 1, 22)),
                    KtxSupercompression.Zlib => Zlib.Compress(levels[m], 9),
                    _ => levels[m],
                };
            var dfd = Dfd(t);
            var kv = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
            if (opt.KeyValues != null) foreach (var p in opt.KeyValues) kv[p.Key] = p.Value;
            kv["KTXwriter"] = Str("PixCodec");
            kv["KTXorientation"] = Str(kind == KtxTextureKind.Texture3D ? "rdi" : "rd");
            if (t.Swizzle != null) kv["KTXswizzle"] = Str(t.Swizzle);
            var kvd = new MemoryStream();
            foreach (var p in kv)
            {
                var key = Encoding.UTF8.GetBytes(p.Key);
                int len = key.Length + 1 + p.Value.Length;
                var lenBytes = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(lenBytes, (uint)len);
                kvd.Write(lenBytes); kvd.Write(key); kvd.WriteByte(0); kvd.Write(p.Value);
                while (kvd.Length % 4 != 0) kvd.WriteByte(0);
            }
            var kvBytes = kvd.ToArray();

            int indexEnd = 80 + 24 * levelCount;
            int dfdOff = indexEnd, kvdOff = dfdOff + dfd.Length;
            long pos = kvdOff + kvBytes.Length;
            // level data: smallest level first, aligned to lcm(texel block size, 4) unless supercompressed
            int align = scheme != KtxSupercompression.None ? 1 : Lcm(t.Format.BlockBytes, 4);
            var offsets = new long[levelCount];
            for (int m = levelCount - 1; m >= 0; m--)
            {
                pos = (pos + align - 1) / align * align;
                offsets[m] = pos;
                pos += stored[m].Length;
            }
            var head = new byte[indexEnd];
            var s = head.AsSpan();
            new byte[] { 0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(s);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(12), (uint)t.Vk);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(16), (uint)(t.Format.IsBlockCompressed ? 1 : TypeSize(t.Format)));
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(20), (uint)w);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(24), (uint)(kind == KtxTextureKind.Texture1D ? 0 : h));
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(28), (uint)depth);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(32), (uint)layers);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(36), (uint)faces);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(40), (uint)levelCount);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(44), (uint)scheme);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(48), (uint)dfdOff);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(52), (uint)dfd.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(56), (uint)kvdOff);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(60), (uint)kvBytes.Length);
            for (int m = 0; m < levelCount; m++)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(80 + 24 * m), (ulong)offsets[m]);
                BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(88 + 24 * m), (ulong)stored[m].Length);
                BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(96 + 24 * m), (ulong)levels[m].Length);
            }
            output.Write(head);
            output.Write(dfd);
            output.Write(kvBytes);
            long written = kvdOff + kvBytes.Length;
            for (int m = levelCount - 1; m >= 0; m--)
            {
                while (written < offsets[m]) { output.WriteByte(0); written++; }
                output.Write(stored[m]);
                written += stored[m].Length;
            }
        }

        static byte[] Str(string s) { var b = Encoding.UTF8.GetBytes(s); Array.Resize(ref b, b.Length + 1); return b; }

        static int Lcm(int a, int b) { int x = a, y = b; while (y != 0) (x, y) = (y, x % y); return a / x * b; }

        static int TypeSize(KtxFormat f)
        {
            if (f.Layout == KtxLayout.Packed) return f.PackBits / 8;
            return f.Bits.Length > 0 ? f.Bits[0] / 8 : 1;
        }

        // ------------------------------------------------------------------ data format descriptor (Khronos Data Format 1.3, basic block)

        const byte QLinear = 0x10, QExponent = 0x20, QSigned = 0x40, QFloat = 0x80;

        struct Sample { public int Offset, Length, Channel; public byte Qualifiers; public uint Lower, Upper; }

        static byte[] Dfd(Target t)
        {
            var f = t.Format;
            var samples = new List<Sample>();
            byte model;
            byte[] dims = { 0, 0, 0, 0 };
            int bytesPlane0;
            if (f.IsBlockCompressed)
            {
                dims[0] = 3; dims[1] = 3;
                bytesPlane0 = f.BlockBytes;
                switch (t.Bc)
                {
                    case TextureCompression.BC1:
                        model = 128;
                        samples.Add(new Sample { Offset = 0, Length = 64, Channel = f.Color == ColorModel.Rgba ? 1 : 0, Upper = uint.MaxValue });
                        break;
                    case TextureCompression.BC2:
                    case TextureCompression.BC3:
                        model = (byte)(t.Bc == TextureCompression.BC2 ? 129 : 130);
                        samples.Add(new Sample { Offset = 0, Length = 64, Channel = 15, Qualifiers = t.Srgb ? QLinear : (byte)0, Upper = uint.MaxValue });
                        samples.Add(new Sample { Offset = 64, Length = 64, Channel = 0, Upper = uint.MaxValue });
                        break;
                    case TextureCompression.BC4:
                        model = 131;
                        samples.Add(Bc45Sample(0, t.Signed));
                        break;
                    case TextureCompression.BC5:
                        model = 132;
                        samples.Add(Bc45Sample(0, t.Signed));
                        var g = Bc45Sample(1, t.Signed); g.Offset = 64;
                        samples.Add(g);
                        break;
                    case TextureCompression.BC6H:
                        model = 133;
                        samples.Add(new Sample
                        {
                            Offset = 0, Length = 128, Channel = 0, Qualifiers = (byte)(QFloat | (t.Signed ? QSigned : 0)),
                            Lower = t.Signed ? 0xBF800000 : 0, Upper = 0x3F800000,
                        });
                        break;
                    default:
                        model = 134;
                        samples.Add(new Sample { Offset = 0, Length = 128, Channel = 0, Upper = uint.MaxValue });
                        break;
                }
            }
            else
            {
                model = 1;   // RGBSDA
                bytesPlane0 = f.BlockBytes;
                int offset = 0;
                for (int c = 0; c < f.Components.Length; c++)
                {
                    int bits = f.Bits[c];
                    int channel = f.Components[c] switch { 'R' => 0, 'G' => 1, 'B' => 2, 'D' => 14, 'A' => 15, _ => 0 };
                    var smp = new Sample { Offset = offset, Length = bits, Channel = channel };
                    switch (f.Numeric)
                    {
                        case "UNORM":
                        case "SRGB":
                            smp.Upper = bits >= 32 ? uint.MaxValue : (1u << bits) - 1;
                            if (f.Numeric == "SRGB" && channel == 15) smp.Qualifiers = QLinear;
                            break;
                        case "SNORM":
                            smp.Qualifiers = QSigned;
                            smp.Lower = (uint)-(int)((1u << (bits - 1)) - 1);
                            smp.Upper = (1u << (bits - 1)) - 1;
                            break;
                        case "UINT":
                            smp.Upper = 1;
                            break;
                        case "SINT":
                            smp.Qualifiers = QSigned;
                            smp.Lower = uint.MaxValue;   // -1
                            smp.Upper = 1;
                            break;
                        case "SFLOAT":
                            smp.Qualifiers = QFloat | QSigned;
                            smp.Lower = 0xBF800000; smp.Upper = 0x3F800000;   // -1.0f, 1.0f whatever the sample size
                            break;
                    }
                    samples.Add(smp);
                    offset += bits;
                }
            }
            int blockSize = 24 + 16 * samples.Count;
            var d = new byte[4 + blockSize];
            var s = d.AsSpan();
            BinaryPrimitives.WriteUInt32LittleEndian(s, (uint)d.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(4), 0);                       // vendor 0 (Khronos), type 0 (basic)
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(8), 2u | (uint)blockSize << 16); // version 2
            d[12] = model;
            d[13] = 1;                                   // BT.709 primaries
            d[14] = (byte)(t.Srgb ? 2 : 1);              // transfer: sRGB or linear
            d[15] = 0;                                   // straight alpha
            dims.CopyTo(s.Slice(16));
            d[20] = (byte)bytesPlane0;
            for (int i = 0; i < samples.Count; i++)
            {
                var o = s.Slice(28 + 16 * i);
                var smp = samples[i];
                BinaryPrimitives.WriteUInt32LittleEndian(o, (uint)(smp.Offset | (smp.Length - 1) << 16 | (smp.Channel | smp.Qualifiers) << 24));
                // sample position 0, 0, 0, 0
                BinaryPrimitives.WriteUInt32LittleEndian(o.Slice(8), smp.Lower);
                BinaryPrimitives.WriteUInt32LittleEndian(o.Slice(12), smp.Upper);
            }
            return d;
        }

        static Sample Bc45Sample(int channel, bool signed)
            => signed ? new Sample { Length = 64, Channel = channel, Qualifiers = QSigned, Lower = 0x80000000, Upper = 0x7FFFFFFF }
                      : new Sample { Length = 64, Channel = channel, Upper = uint.MaxValue };
    }
}
