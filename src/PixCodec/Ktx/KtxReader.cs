using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading.Tasks;
using PixCodec.Compression;
using PixCodec.DdsImpl;

namespace PixCodec.KtxImpl
{
    internal sealed class KtxParsed
    {
        public required KtxTextureInfo Info;
        public KtxFormat? Format;          // null for Basis Universal ETC1S / UASTC
        public KtxLayout BasisKind;        // Etc1s / Uastc when Format is null
        public bool BigEndian;             // KTX 1 written on a big endian machine
        public int TypeSize = 1;           // KTX 1 glTypeSize (byte swapping unit)
        public bool FlipY;                 // stored bottom-up
        // KTX 2
        public (long Offset, long Length, long Uncompressed)[] Levels = Array.Empty<(long, long, long)>();
        public long SgdOffset, SgdLength;
        // KTX 1: offset of each level's imageSize field
        public long[] Level1Offsets = Array.Empty<long>();
    }

    internal static class KtxReader
    {
        static ReadOnlySpan<byte> Id1 => new byte[] { 0xAB, 0x4B, 0x54, 0x58, 0x20, 0x31, 0x31, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A };
        static ReadOnlySpan<byte> Id2 => new byte[] { 0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A };

        public static int Version(ReadOnlySpan<byte> h)
            => h.Length < 12 ? 0 : h.Slice(0, 12).SequenceEqual(Id1) ? 1 : h.Slice(0, 12).SequenceEqual(Id2) ? 2 : 0;

        // data format descriptor colour models of the Basis Universal formats
        const int ModelAstc = 162, ModelEtc1s = 163, ModelUastc = 166;

        public static KtxParsed Parse(ReadOnlySpan<byte> d)
        {
            return Version(d) switch
            {
                1 => Parse1(d),
                2 => Parse2(d),
                _ => throw new ImageFormatException("KTX: not a KTX file"),
            };
        }

        static KtxParsed Parse2(ReadOnlySpan<byte> d)
        {
            if (d.Length < 80) throw new ImageFormatException("KTX2: header truncated");
            static uint U(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(o));
            static ulong U64(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt64LittleEndian(d.Slice(o));
            int vk = (int)U(d, 12);
            uint typeSize = U(d, 16), w = U(d, 20), h = U(d, 24), depth = U(d, 28), layers = U(d, 32), faces = U(d, 36), levels = U(d, 40), scheme = U(d, 44);
            uint dfdOff = U(d, 48), dfdLen = U(d, 52), kvdOff = U(d, 56), kvdLen = U(d, 60);
            ulong sgdOff = U64(d, 64), sgdLen = U64(d, 72);
            if (w == 0 || w > 1 << 24 || h > 1 << 24 || depth > 1 << 24 || layers > 1 << 20) throw new ImageFormatException("KTX2: bad dimensions");
            if (faces != 1 && faces != 6) throw new ImageFormatException("KTX2: faceCount must be 1 or 6");
            if (faces == 6 && (depth != 0 || h == 0 || w != h)) throw new ImageFormatException("KTX2: bad cube map");
            int lv = (int)Math.Max(1, levels);
            if (lv > 32) throw new ImageFormatException("KTX2: too many levels");
            if (80 + 24L * lv > d.Length) throw new ImageFormatException("KTX2: level index truncated");
            var p = new KtxParsed
            {
                Info = new KtxTextureInfo
                {
                    Version = 2, Width = (int)w, Height = (int)Math.Max(1, h), Depth = (int)Math.Max(1, depth), Layers = (int)Math.Max(1, layers),
                    Faces = (int)faces, MipLevels = lv, IsArray = layers > 0, VkFormat = vk, Supercompression = (KtxSupercompression)scheme,
                    Kind = faces == 6 ? KtxTextureKind.CubeMap : depth > 0 ? KtxTextureKind.Texture3D : h == 0 ? KtxTextureKind.Texture1D : KtxTextureKind.Texture2D,
                },
                Levels = new (long, long, long)[lv],
                SgdOffset = (long)sgdOff, SgdLength = (long)sgdLen,
            };
            if (scheme > 3) throw new ImageNotSupportedException($"KTX2: supercompression scheme {scheme}");
            for (int i = 0; i < lv; i++)
            {
                ulong off = U64(d, 80 + 24 * i), len = U64(d, 88 + 24 * i), unc = U64(d, 96 + 24 * i);
                if (off > (ulong)d.Length) throw new ImageFormatException($"KTX2: level {i} outside the file");
                if (len > (ulong)d.Length - off)
                {
                    // truncated: uncompressed data decodes as far as it goes, supercompressed data cannot
                    if (scheme != 0) throw new ImageFormatException($"KTX2: level {i} truncated");
                    len = (ulong)d.Length - off;
                }
                p.Levels[i] = ((long)off, (long)len, (long)Math.Min(unc, long.MaxValue));
            }
            // data format descriptor: transfer function, premultiplied alpha, Basis colour models
            int model = 0;
            int[] blockDims = { 1, 1, 1 };
            bool floatSamples = false;
            if (dfdLen >= 4 + 24 && (ulong)dfdOff + dfdLen <= (ulong)d.Length)
            {
                var b = d.Slice((int)dfdOff + 4);
                model = b[8];
                blockDims = new[] { b[12] + 1, b[13] + 1, b[14] + 1 };
                if (dfdLen >= 4 + 24 + 16) floatSamples = (b[24 + 3] & 0x80) != 0;
                int transfer = b[10], flags = b[11];
                p.Info.Transfer = transfer switch { 1 => TransferFunction.Linear, 2 => TransferFunction.Srgb, 13 => TransferFunction.Pq, 15 => TransferFunction.Hlg, _ => TransferFunction.Unknown };
                p.Info.IsPremultiplied = (flags & 1) != 0;
            }
            if (kvdLen > 0 && (ulong)kvdOff + kvdLen <= (ulong)d.Length) p.Info.KeyValues = KeyValues(d.Slice((int)kvdOff, (int)kvdLen), false);
            p.FlipY = Orientation(p.Info, 2);
            if (vk == 0 && model == ModelAstc && scheme != 1)
            {
                // ASTC described by the data format descriptor alone (3D footprints have no Vulkan format)
                p.Format = KtxFormats.Astc(blockDims[0], blockDims[1], blockDims[2], p.Info.Transfer == TransferFunction.Srgb, floatSamples);
                SetFormat(p);
            }
            else if (vk == 0)
            {
                if (scheme == 1 || model == ModelEtc1s) p.BasisKind = KtxLayout.Etc1s;
                else if (model == ModelUastc) p.BasisKind = KtxLayout.Uastc;
                else throw new ImageNotSupportedException("KTX2: undefined format");
                p.Info.Format = p.BasisKind == KtxLayout.Etc1s ? "ETC1S" : "UASTC";
                p.Info.IsBlockCompressed = true;
                p.Info.IsSrgb = p.Info.Transfer == TransferFunction.Srgb;
                p.Info.Color = ColorModel.Rgba; p.Info.Channels = 4; p.Info.SampleType = SampleType.UInt8;
            }
            else
            {
                p.Format = KtxFormats.FromVk(vk) ?? throw new ImageNotSupportedException($"KTX2: {KtxFormats.VkName(vk)} is not supported");
                if (scheme == 1) throw new ImageFormatException("KTX2: BasisLZ needs an undefined vkFormat");
                SetFormat(p);
            }
            return p;
        }

        static void SetFormat(KtxParsed p)
        {
            var f = p.Format!;
            var i = p.Info;
            i.Format = f.Name;
            i.IsSrgb = f.Srgb;
            i.IsBlockCompressed = f.IsBlockCompressed;
            i.Color = f.Color; i.Channels = f.Channels; i.SampleType = f.Type;
            if (f.Srgb) i.Transfer = TransferFunction.Srgb;
            else if (i.Transfer == TransferFunction.Unknown || i.Version == 1)
                i.Transfer = f.Type is SampleType.Float16 or SampleType.Float32 or SampleType.Float64 || f.Hdr ? TransferFunction.Linear
                    : f.Color == ColorModel.Other || f.Type is not (SampleType.UInt8 or SampleType.UInt16) ? TransferFunction.Unknown : TransferFunction.Linear;
        }

        static KtxParsed Parse1(ReadOnlySpan<byte> d)
        {
            if (d.Length < 64) throw new ImageFormatException("KTX: header truncated");
            uint endian = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(12));
            bool be = endian == 0x01020304;
            if (!be && endian != 0x04030201) throw new ImageFormatException("KTX: bad endianness field");
            static uint U(ReadOnlySpan<byte> d, bool be, int o) => be ? BinaryPrimitives.ReadUInt32BigEndian(d.Slice(o)) : BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(o));
            uint glType = U(d, be, 16), glTypeSize = U(d, be, 20), glFormat = U(d, be, 24), glInternal = U(d, be, 28);
            uint w = U(d, be, 36), h = U(d, be, 40), depth = U(d, be, 44), elems = U(d, be, 48), faces = U(d, be, 52), levels = U(d, be, 56), kvLen = U(d, be, 60);
            if (w == 0 || w > 1 << 24 || h > 1 << 24 || depth > 1 << 24 || elems > 1 << 20) throw new ImageFormatException("KTX: bad dimensions");
            if (faces != 1 && faces != 6) throw new ImageFormatException("KTX: numberOfFaces must be 1 or 6");
            int lv = (int)Math.Max(1, levels);
            if (lv > 32) throw new ImageFormatException("KTX: too many levels");
            if (64L + kvLen > d.Length) throw new ImageFormatException("KTX: key / value data truncated");
            int vk = KtxFormats.VkFromGl(glInternal, glFormat, glType);
            var p = new KtxParsed
            {
                Info = new KtxTextureInfo
                {
                    Version = 1, Width = (int)w, Height = (int)Math.Max(1, h), Depth = (int)Math.Max(1, depth), Layers = (int)Math.Max(1, elems),
                    Faces = (int)faces, MipLevels = lv, IsArray = elems > 0, VkFormat = vk, GlInternalFormat = (int)glInternal,
                    Kind = faces == 6 ? KtxTextureKind.CubeMap : depth > 0 ? KtxTextureKind.Texture3D : h == 0 ? KtxTextureKind.Texture1D : KtxTextureKind.Texture2D,
                },
                BigEndian = be, TypeSize = (int)Math.Max(1, glTypeSize),
            };
            if (kvLen > 0) p.Info.KeyValues = KeyValues(d.Slice(64, (int)kvLen), be);
            p.Format = KtxFormats.FromVk(vk) ?? KtxFormats.FromGl3D(glInternal) ?? throw new ImageNotSupportedException($"KTX: OpenGL format 0x{glInternal:X4} is not supported");
            SetFormat(p);
            p.FlipY = Orientation(p.Info, 1);
            // walk the levels: imageSize, images (rows padded to 4 bytes for uncompressed data), padding to 4 bytes
            p.Level1Offsets = new long[lv];
            long pos = 64 + kvLen;
            for (int m = 0; m < lv; m++)
            {
                p.Level1Offsets[m] = pos;
                if (pos + 4 > d.Length) { for (int k = m; k < lv; k++) p.Level1Offsets[k] = d.Length; break; }
                pos += 4 + Level1Bytes(p, m);
            }
            return p;
        }

        /// <summary>Bytes of one KTX 1 level after its imageSize field, padding included.</summary>
        static long Level1Bytes(KtxParsed p, int m)
        {
            var i = p.Info;
            int w = Math.Max(1, i.Width >> m), h = Math.Max(1, i.Height >> m), dep = Math.Max(1, i.Depth >> m);
            var f = p.Format!;
            long face = f.BlockD > 1 ? f.ImageBytes(w, h) * ((dep + f.BlockD - 1) / f.BlockD) : Image1Bytes(p, w, h) * dep;
            face = (face + 3) & ~3L;   // cube padding
            long level = face * i.Faces * i.Layers;
            return (level + 3) & ~3L;  // mip padding
        }

        /// <summary>One KTX 1 image (2D slice): rows padded to 4 bytes when uncompressed.</summary>
        static long Image1Bytes(KtxParsed p, int w, int h)
        {
            var f = p.Format!;
            if (f.IsBlockCompressed) return f.ImageBytes(w, h);
            long row = ((long)w * f.BlockBytes + 3) & ~3L;
            return row * h;
        }

        static bool Orientation(KtxTextureInfo info, int version)
        {
            var s = info.GetString("KTXorientation");
            if (s == null) return false;
            if (version == 1) return s.Contains("T=u");
            return s.Length >= 2 && s[1] == 'u';
        }

        static Dictionary<string, byte[]> KeyValues(ReadOnlySpan<byte> kv, bool be)
        {
            var r = new Dictionary<string, byte[]>();
            int pos = 0;
            while (pos + 4 <= kv.Length)
            {
                int len = (int)(be ? BinaryPrimitives.ReadUInt32BigEndian(kv.Slice(pos)) : BinaryPrimitives.ReadUInt32LittleEndian(kv.Slice(pos)));
                pos += 4;
                if (len < 0 || len > kv.Length - pos) break;
                var pair = kv.Slice(pos, len);
                int z = pair.IndexOf((byte)0);
                if (z > 0)
                {
                    var key = System.Text.Encoding.UTF8.GetString(pair.Slice(0, z));
                    var val = pair.Slice(z + 1);
                    if (val.Length > 0 && val[^1] == 0) val = val.Slice(0, val.Length - 1);
                    r.TryAdd(key, val.ToArray());
                }
                pos += (len + 3) & ~3;
            }
            return r;
        }

        public static ImageInfo GetInfo(ReadOnlySpan<byte> d)
        {
            var p = Parse(d);
            var i = p.Info;
            return new ImageInfo(ImageFormat.Ktx, i.Width, i.Height, i.Channels, i.SampleType, i.Color, i.Layers * i.Faces)
            {
                MipLevels = i.MipLevels,
                BitsPerSample = p.Format is { Layout: KtxLayout.Plain } f ? f.Bits[0] : 0,
                Transfer = i.Transfer,
            };
        }

        public static KtxTexture Decode(ReadOnlySpan<byte> d, KtxDecodeOptions opt, bool topOnly)
        {
            var p = Parse(d);
            var i = p.Info;
            int mips = topOnly ? 1 : i.MipLevels;
            long total = 0;
            for (int m = 0; m < mips; m++)
                total += (long)Math.Max(1, i.Width >> m) * Math.Max(1, i.Height >> m) * Math.Max(1, i.Depth >> m) * i.Layers * i.Faces;
            if (total > opt.MaxPixels) throw new ImageNotSupportedException($"KTX: {total} pixels exceed MaxPixels");
            var t = new KtxTexture
            {
                Version = i.Version, Kind = i.Kind, Width = i.Width, Height = i.Height, Depth = i.Depth, MipLevels = i.MipLevels,
                Layers = i.Layers, Faces = i.Faces, IsArray = i.IsArray, VkFormat = i.VkFormat, GlInternalFormat = i.GlInternalFormat,
                Format = i.Format, IsSrgb = i.IsSrgb, IsBlockCompressed = i.IsBlockCompressed, Supercompression = i.Supercompression,
                IsPremultiplied = i.IsPremultiplied, Transfer = i.Transfer, KeyValues = i.KeyValues, Color = i.Color,
                SampleType = i.SampleType, Channels = i.Channels,
            };
            t.Images = new RawImage[mips][][][];
            BasisLzState? basis = null;
            for (int m = 0; m < mips; m++)
            {
                int w = Math.Max(1, i.Width >> m), h = Math.Max(1, i.Height >> m), dep = Math.Max(1, i.Depth >> m);
                byte[] level = LevelBytes(d, p, m);
                var lv = t.Images[m] = new RawImage[i.Layers][][];
                long pos = 0;
                for (int l = 0; l < i.Layers; l++)
                {
                    lv[l] = new RawImage[i.Faces][];
                    for (int f = 0; f < i.Faces; f++)
                    {
                        lv[l][f] = new RawImage[dep];
                        RawImage[]? volume = null;
                        if (p.Format is { BlockD: > 1 } vf)
                        {
                            // 3D blocks span several slices: the image is a stack of block slices
                            long bytes = vf.ImageBytes(w, h) * ((dep + vf.BlockD - 1) / vf.BlockD);
                            volume = KtxBlocks.Astc3D(vf, Slice(level, pos, bytes), w, h, dep);
                            pos += bytes;
                        }
                        for (int z = 0; z < dep; z++)
                        {
                            RawImage img;
                            if (volume != null) img = volume[z];
                            else if (p.Format == null)
                            {
                                int imageIndex = ((m == 0 ? 0 : ImagesBefore(i, m)) + (l * i.Faces + f) * dep + z);
                                if (p.BasisKind == KtxLayout.Etc1s)
                                {
                                    basis ??= BasisLzState.Create(d, p);
                                    img = basis.Decode(imageIndex, level, w, h);
                                }
                                else
                                {
                                    long bytes = (long)((w + 3) / 4) * ((h + 3) / 4) * 16;
                                    img = KtxBlocks.Uastc(Slice(level, pos, bytes), w, h);
                                    pos += bytes;
                                }
                            }
                            else
                            {
                                long bytes = i.Version == 1 ? Image1Bytes(p, w, h) : p.Format.ImageBytes(w, h);
                                img = Surface(p, Slice(level, pos, bytes), w, h);
                                pos += bytes;
                            }
                            if (p.FlipY && opt.ApplyOrientation) img = ImageOps.FlipVertical(img);
                            if (i.IsPremultiplied && opt.Unpremultiply && img.HasAlpha) DdsSurfaces.Unpremultiply(img);
                            img.Metadata = new ImageMetadata { Transfer = i.Transfer };
                            lv[l][f][z] = img;
                        }
                        if (i.Version == 1) pos = (pos + 3) & ~3L;   // cube padding
                    }
                }
            }
            return t;
        }

        /// <summary>BasisLZ image descriptors are ordered by level, then layer, face and slice.</summary>
        static int ImagesBefore(KtxTextureInfo i, int mip)
        {
            int n = 0;
            for (int m = 0; m < mip; m++) n += i.Layers * i.Faces * Math.Max(1, i.Depth >> m);
            return n;
        }

        static ReadOnlySpan<byte> Slice(byte[] level, long pos, long bytes)
        {
            // truncated data reads as zeros
            if (pos + bytes <= level.Length) return level.AsSpan((int)pos, (int)bytes);
            var t = new byte[bytes];
            if (pos < level.Length) level.AsSpan((int)pos).CopyTo(t);
            return t;
        }

        /// <summary>The (uncompressed) bytes of one mip level.</summary>
        static byte[] LevelBytes(ReadOnlySpan<byte> d, KtxParsed p, int m)
        {
            if (p.Info.Version == 1)
            {
                long start = p.Level1Offsets[m] + 4;
                long len = Level1Bytes(p, m);
                if (start >= d.Length) return Array.Empty<byte>();
                var b = d.Slice((int)start, (int)Math.Min(len, d.Length - start)).ToArray();
                if (p.BigEndian && p.TypeSize > 1) Swap(b, p.TypeSize);
                return b;
            }
            var (off, length, unc) = p.Levels[m];
            var src = d.Slice((int)off, (int)length);
            switch (p.Info.Supercompression)
            {
                case KtxSupercompression.Zstd:
                    {
                        if (unc > Array.MaxLength) throw new ImageNotSupportedException("KTX2: level too large");
                        var o = new byte[unc];
                        int n = Zstd.Decompress(src, o);
                        if (n != unc) throw new ImageFormatException("KTX2: zstd level has the wrong size");
                        return o;
                    }
                case KtxSupercompression.Zlib:
                    {
                        if (unc > Array.MaxLength) throw new ImageNotSupportedException("KTX2: level too large");
                        var o = new byte[unc];
                        int n = Zlib.Decompress(src, o);
                        if (n != unc) throw new ImageFormatException("KTX2: zlib level has the wrong size");
                        return o;
                    }
                default:
                    return src.ToArray();
            }
        }

        static void Swap(byte[] b, int unit)
        {
            if (unit == 2) for (int i = 0; i + 1 < b.Length; i += 2) (b[i], b[i + 1]) = (b[i + 1], b[i]);
            else if (unit == 4) for (int i = 0; i + 3 < b.Length; i += 4) { (b[i], b[i + 3]) = (b[i + 3], b[i]); (b[i + 1], b[i + 2]) = (b[i + 2], b[i + 1]); }
            else if (unit == 8) for (int i = 0; i + 7 < b.Length; i += 8) Array.Reverse(b, i, 8);
        }

        /// <summary>One 2D image of the stored format.</summary>
        static RawImage Surface(KtxParsed p, ReadOnlySpan<byte> src, int w, int h)
        {
            var f = p.Format!;
            switch (f.Layout)
            {
                case KtxLayout.Dxgi:
                    {
                        var df = DdsReader.FromDxgi(f.Dxgi)!;
                        var img = df.BlockBytes > 0 ? DdsSurfaces.Blocks(df, src, w, h) : DdsSurfaces.Pixels(df, src, w, h);
                        if (f.Color == ColorModel.Rgb && img.Color == ColorModel.Rgba) img = DropAlpha(img);   // BC1 RGB: no punch-through
                        return img;
                    }
                case KtxLayout.Etc: return KtxBlocks.Etc(f, src, w, h);
                case KtxLayout.Astc: return KtxBlocks.Astc(f, src, w, h);
                case KtxLayout.Packed: return Packed(f, src, w, h);
                default: return Plain(f, src, w, h, p.Info.Version == 1);
            }
        }

        static RawImage DropAlpha(RawImage img)
        {
            var s = (byte[])img.Data;
            int n = img.Width * img.Height;
            var o = new byte[n * 3];
            for (int i = 0; i < n; i++) { o[i * 3] = s[i * 4]; o[i * 3 + 1] = s[i * 4 + 1]; o[i * 3 + 2] = s[i * 4 + 2]; }
            return new RawImage(img.Width, img.Height, ColorModel.Rgb, o, 3);
        }

        /// <summary>Output position of each stored component: R, G, B (or D) first, then A.</summary>
        static int[] OutputIndex(string comps)
        {
            var r = new int[comps.Length];
            int k = 0;
            foreach (char want in "RGBD")
                for (int c = 0; c < comps.Length; c++) if (comps[c] == want) r[c] = k++;
            for (int c = 0; c < comps.Length; c++) if (comps[c] == 'A') r[c] = k++;
            return r;
        }

        static RawImage Plain(KtxFormat f, ReadOnlySpan<byte> src, int w, int h, bool rowPadding)
        {
            var img = RawImage.Create(w, h, f.Color, f.Type, f.Channels);
            var dst = img.GetBytes();
            int bps = RawImage.BytesPerSample(f.Type);
            int ch = f.Channels;
            var idx = OutputIndex(f.Components);
            long rowIn = (long)w * f.BlockBytes;
            if (rowPadding) rowIn = (rowIn + 3) & ~3L;
            bool identity = true;
            for (int c = 0; c < idx.Length; c++) identity &= idx[c] == c && f.Bits[c] == bps * 8;
            for (int y = 0; y < h; y++)
            {
                var row = src.Slice((int)(y * rowIn), w * f.BlockBytes);
                var o = dst.Slice(y * w * ch * bps, w * ch * bps);
                if (identity) { row.CopyTo(o); continue; }
                for (int x = 0; x < w; x++)
                {
                    int ip = x * f.BlockBytes;
                    for (int c = 0; c < idx.Length; c++)
                    {
                        int cb = f.Bits[c] / 8;
                        row.Slice(ip, cb).CopyTo(o.Slice((x * ch + idx[c]) * bps, cb));
                        ip += cb;
                    }
                }
            }
            return img;
        }

        static RawImage Packed(KtxFormat f, ReadOnlySpan<byte> src, int w, int h)
        {
            var img = RawImage.Create(w, h, f.Color, f.Type, f.Channels);
            var idx = OutputIndex(f.Components);
            int words = f.PackBits / 8;
            bool norm = f.Numeric is "UNORM" or "SRGB";
            int outBits = f.Type is SampleType.UInt8 or SampleType.Int8 ? 8 : 16;
            int ch = f.Channels;
            long n = (long)w * h;
            var shifts = new int[f.Bits.Length];
            int sh = f.PackBits;
            for (int c = 0; c < f.Bits.Length; c++) { sh -= f.Bits[c]; shifts[c] = sh; }
            byte[]? o8 = img.Data as byte[];
            ushort[]? o16 = img.Data as ushort[];
            if (o8 == null && o16 == null) throw new ImageNotSupportedException($"KTX: {f.Name}");
            for (long i = 0; i < n; i++)
            {
                long o = i * words;
                if (o + words > src.Length) break;
                uint v = words switch
                {
                    1 => src[(int)o],
                    2 => BinaryPrimitives.ReadUInt16LittleEndian(src.Slice((int)o)),
                    _ => BinaryPrimitives.ReadUInt32LittleEndian(src.Slice((int)o)),
                };
                for (int c = 0; c < f.Bits.Length; c++)
                {
                    uint max = (1u << f.Bits[c]) - 1;
                    uint x = v >> shifts[c] & max;
                    uint outMax = (1u << outBits) - 1;
                    uint s = norm ? (uint)(((ulong)x * outMax + max / 2) / max) : x;
                    if (o8 != null) o8[i * ch + idx[c]] = (byte)s; else o16![i * ch + idx[c]] = (ushort)s;
                }
            }
            return img;
        }
    }
}
