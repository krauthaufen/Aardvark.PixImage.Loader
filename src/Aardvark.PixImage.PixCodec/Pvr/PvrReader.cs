using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Aardvark.ImageCodecs.DdsImpl;
using Aardvark.ImageCodecs.KtxImpl;

namespace Aardvark.ImageCodecs.PvrImpl
{
    internal enum PvrLayout { Uncompressed, Pvrtc, Bw1, Block }

    internal sealed class PvrParsed
    {
        public PvrTextureInfo Info = new();
        public PvrLayout Layout;
        public char[] Names = Array.Empty<char>();
        public int[] Bits = Array.Empty<int>();
        public bool PvrtcTwoBpp, PvrtcRgb;
        public KtxFormat? Block;
        public long DataOffset;
        public bool SurfaceMajor;   // legacy: all mips of a surface, then the next surface
        public bool Twiddled;       // legacy: uncompressed pixels in Morton order
        public int FaceSurfaces;    // legacy: surfaces that are cube faces (6) or 1

        public bool IsFloat => Info.ChannelType is PvrChannelType.SignedFloat or PvrChannelType.UnsignedFloat;
        public bool IsSigned => (int)Info.ChannelType is 1 or 3 or 5 or 7 or 9 or 11 or 12;
        public bool IsNormalized => (int)Info.ChannelType is 0 or 1 or 4 or 5 or 8 or 9;
    }

    internal static class PvrReader
    {
        public const uint Magic3 = 0x03525650, Magic3Swapped = 0x50565203;

        /// <summary>3 for a v3 header, 2 for a legacy one, -3 for a v3 header of the other byte order, 0 otherwise.</summary>
        public static int Version(ReadOnlySpan<byte> h)
        {
            if (h.Length >= 4)
            {
                uint v = BinaryPrimitives.ReadUInt32LittleEndian(h);
                if (v == Magic3) return 3;
                if (v == Magic3Swapped) return -3;
            }
            if (h.Length >= 52 && BinaryPrimitives.ReadUInt32LittleEndian(h) is 52 or 44 && h.Slice(44, 4).SequenceEqual("PVR!"u8)) return 2;
            return 0;
        }

        static readonly string[] CompressedNames =
        {
            "PVRTC1_2_RGB", "PVRTC1_2", "PVRTC1_4_RGB", "PVRTC1_4", "PVRTC2_2", "PVRTC2_4", "ETC1", "BC1", "DXT2", "BC2", "DXT4", "BC3",
            "BC4", "BC5", "BC6H", "BC7", "UYVY", "YUY2", "BW1bpp", "R9G9B9E5", "RGBG8888", "GRGB8888", "ETC2_RGB", "ETC2_RGBA",
            "ETC2_RGB_A1", "EAC_R11", "EAC_RG11",
            "ASTC_4x4", "ASTC_5x4", "ASTC_5x5", "ASTC_6x5", "ASTC_6x6", "ASTC_8x5", "ASTC_8x6", "ASTC_8x8", "ASTC_10x5", "ASTC_10x6",
            "ASTC_10x8", "ASTC_10x10", "ASTC_12x10", "ASTC_12x12",
            "ASTC_3x3x3", "ASTC_4x3x3", "ASTC_4x4x3", "ASTC_4x4x4", "ASTC_5x4x4", "ASTC_5x5x4", "ASTC_5x5x5", "ASTC_6x5x5", "ASTC_6x6x5", "ASTC_6x6x6",
            "BASISU_ETC1S", "BASISU_UASTC", "RGBM", "RGBD",
        };

        static readonly int[][] Astc3D =
        {
            new[] { 3, 3, 3 }, new[] { 4, 3, 3 }, new[] { 4, 4, 3 }, new[] { 4, 4, 4 }, new[] { 5, 4, 4 },
            new[] { 5, 5, 4 }, new[] { 5, 5, 5 }, new[] { 6, 5, 5 }, new[] { 6, 6, 5 }, new[] { 6, 6, 6 },
        };

        public static PvrParsed Parse(ReadOnlySpan<byte> d)
        {
            int v = Version(d);
            if (v == 0) throw new ImageFormatException("PVR: not a PVR texture");
            if (v == -3) throw new ImageNotSupportedException("PVR: big endian files are not supported");
            return v == 3 ? Parse3(d) : Parse2(d);
        }

        static uint U(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(o));

        static PvrParsed Parse3(ReadOnlySpan<byte> d)
        {
            if (d.Length < 52) throw new ImageFormatException("PVR: header truncated");
            var p = new PvrParsed();
            var i = p.Info;
            i.Version = 3;
            uint flags = U(d, 4);
            ulong pf = BinaryPrimitives.ReadUInt64LittleEndian(d.Slice(8));
            uint cs = U(d, 16), ct = U(d, 20);
            uint h = U(d, 24), w = U(d, 28), depth = U(d, 32), surfaces = U(d, 36), faces = U(d, 40), mips = U(d, 44), meta = U(d, 48);
            if (w == 0 || h == 0) throw new ImageFormatException("PVR: empty image");
            if (w > 1u << 24 || h > 1u << 24 || depth > 1u << 16 || surfaces > 1u << 16 || faces > 6 || mips > 32)
                throw new ImageFormatException("PVR: implausible dimensions");
            if (ct > 13) throw new ImageFormatException($"PVR: channel type {ct}");
            i.Width = (int)w; i.Height = (int)h; i.Depth = (int)Math.Max(1, depth); i.Surfaces = (int)Math.Max(1, surfaces);
            i.Faces = (int)Math.Max(1, faces); i.MipLevels = (int)Math.Max(1, mips);
            i.PixelFormat = pf;
            i.ChannelType = (PvrChannelType)ct;
            i.IsSrgb = cs == 1;
            i.IsPremultiplied = (flags & 2) != 0;
            if (52L + meta > d.Length) throw new ImageFormatException("PVR: metadata truncated");
            var md = new List<(uint, uint, byte[])>();
            long pos = 52, end = 52L + meta;
            while (pos + 12 <= end)
            {
                uint fourcc = U(d, (int)pos), key = U(d, (int)pos + 4), size = U(d, (int)pos + 8);
                if (pos + 12 + size > end) throw new ImageFormatException("PVR: metadata block truncated");
                var data = d.Slice((int)pos + 12, (int)size).ToArray();
                md.Add((fourcc, key, data));
                if (fourcc == Magic3)
                {
                    if (key == 2 && size >= 1) i.CubeFaceOrder = System.Text.Encoding.ASCII.GetString(data).TrimEnd('\0');
                    else if (key == 3 && size >= 3) { i.XLeft = data[0] != 0; i.YUp = data[1] != 0; i.ZOut = data[2] != 0; }
                }
                pos += 12 + size;
            }
            i.Metadata = md;
            p.DataOffset = end;
            SetFormat(p, pf);
            return p;
        }

        static PvrParsed Parse2(ReadOnlySpan<byte> d)
        {
            var p = new PvrParsed();
            var i = p.Info;
            i.Version = 2;
            uint hs = U(d, 0), h = U(d, 4), w = U(d, 8), mips = U(d, 12), flags = U(d, 16);
            uint surfaces = hs >= 52 ? U(d, 48) : 1;
            if (w == 0 || h == 0) throw new ImageFormatException("PVR: empty image");
            if (w > 1u << 24 || h > 1u << 24 || mips > 31 || surfaces > 1u << 16) throw new ImageFormatException("PVR: implausible dimensions");
            i.Width = (int)w; i.Height = (int)h;
            i.MipLevels = (flags & 0x100) != 0 ? (int)mips + 1 : 1;
            bool cube = (flags & 0x1000) != 0, volume = (flags & 0x4000) != 0;
            int n = (int)Math.Max(1, surfaces);
            if (cube) { i.Faces = 6; i.Surfaces = Math.Max(1, n / 6); }
            else if (volume) i.Depth = n;
            else i.Surfaces = n;
            i.YUp = (flags & 0x10000) != 0;
            p.SurfaceMajor = true;
            p.Twiddled = (flags & 0x200) != 0;
            p.DataOffset = hs;
            bool alpha = (flags & 0x8000) != 0;
            int code = (int)(flags & 0xFF);
            // the legacy pixel types as v3 pixel formats: (channel names, bits) or a compressed format id
            (string names, int[] bits)? plain = code switch
            {
                0x00 or 0x10 => code == 0 ? ("argb", new[] { 4, 4, 4, 4 }) : ("rgba", new[] { 4, 4, 4, 4 }),
                0x01 => ("argb", new[] { 1, 5, 5, 5 }),
                0x11 => ("rgba", new[] { 5, 5, 5, 1 }),
                0x02 or 0x13 => ("rgb", new[] { 5, 6, 5 }),
                0x03 or 0x14 => ("xrgb", new[] { 1, 5, 5, 5 }),
                0x04 => ("bgr", new[] { 8, 8, 8 }),
                0x15 => ("rgb", new[] { 8, 8, 8 }),
                0x05 or 0x1A => ("bgra", new[] { 8, 8, 8, 8 }),
                0x12 => ("rgba", new[] { 8, 8, 8, 8 }),
                0x06 => ("argb", new[] { 8, 3, 3, 2 }),
                0x07 or 0x16 => ("l", new[] { 8 }),
                0x08 or 0x17 => ("la", new[] { 8, 8 }),
                0x1B => ("a", new[] { 8 }),
                _ => null,
            };
            if (plain is { } pl)
            {
                ulong pf = 0;
                for (int k = 0; k < pl.names.Length; k++) pf |= (ulong)pl.names[k] << (8 * k) | (ulong)pl.bits[k] << (32 + 8 * k);
                i.PixelFormat = pf;
                SetFormat(p, pf);
                return p;
            }
            int id = code switch
            {
                0x0C or 0x18 => alpha ? 1 : 0,
                0x0D or 0x19 => alpha ? 3 : 2,
                0x09 => 18,
                0x20 => 7, 0x21 => 8, 0x22 => 9, 0x23 => 10, 0x24 => 11,
                0x36 => 6,
                _ => -1,
            };
            if (id < 0) throw new ImageNotSupportedException($"PVR: legacy pixel type 0x{code:X2} is not supported");
            i.PixelFormat = (ulong)id;
            SetFormat(p, (ulong)id);
            return p;
        }

        static void SetFormat(PvrParsed p, ulong pf)
        {
            var i = p.Info;
            if (pf >> 32 != 0)
            {
                var names = new List<char>();
                var bits = new List<int>();
                var sb = new System.Text.StringBuilder();
                for (int k = 0; k < 4; k++)
                {
                    char c = (char)(pf >> (8 * k) & 0xFF);
                    int b = (int)(pf >> (32 + 8 * k) & 0xFF);
                    if (c == 0 && b == 0) continue;
                    if (c == 0 || b == 0) throw new ImageFormatException("PVR: channel without name or size");
                    c = char.ToLowerInvariant(c);
                    if (c is not ('r' or 'g' or 'b' or 'a' or 'l' or 'i' or 'x' or 'd' or 's')) throw new ImageNotSupportedException($"PVR: channel '{c}'");
                    names.Add(c); bits.Add(b);
                    sb.Append(c).Append(b);
                }
                int total = 0, max = 0;
                foreach (var b in bits) { total += b; max = Math.Max(max, b); }
                if (names.Count == 0 || total % 8 != 0 || total > 128) throw new ImageFormatException("PVR: bad channel sizes");
                bool aligned = true;
                foreach (var b in bits) aligned &= b % 8 == 0;
                if (!aligned && total > 64) throw new ImageNotSupportedException("PVR: packed pixels over 64 bits");
                if (max > 64 || p.IsFloat && max is not (10 or 11 or 16 or 32)) throw new ImageNotSupportedException($"PVR: {max} bit {(p.IsFloat ? "float" : "integer")} channels");
                if (max > 32 && !p.IsFloat) throw new ImageNotSupportedException("PVR: 64 bit integer channels");
                p.Layout = PvrLayout.Uncompressed;
                p.Names = names.ToArray();
                p.Bits = bits.ToArray();
                i.Format = sb.ToString();
                i.IsCompressed = false;
                // what decoding gives
                var outIdx = OutputIndex(p.Names, out var color, out int ch);
                _ = outIdx;
                i.Color = color; i.Channels = ch;
                i.SampleType = p.IsFloat ? (max <= 16 ? SampleType.Float16 : SampleType.Float32)
                    : max <= 8 ? (p.IsSigned ? SampleType.Int8 : SampleType.UInt8)
                    : max <= 16 ? (p.IsSigned ? SampleType.Int16 : SampleType.UInt16)
                    : (p.IsSigned ? SampleType.Int32 : SampleType.UInt32);
                i.Transfer = i.IsSrgb ? TransferFunction.Srgb : p.IsFloat ? TransferFunction.Linear : TransferFunction.Unknown;
                return;
            }
            int id = (int)pf;
            if (id >= CompressedNames.Length) throw new ImageNotSupportedException($"PVR: pixel format {id}");
            i.Format = CompressedNames[id];
            i.IsCompressed = true;
            bool srgb = i.IsSrgb, signed = p.IsSigned, flt = p.IsFloat;
            int vk = 0;
            switch (id)
            {
                case <= 3:
                    p.Layout = PvrLayout.Pvrtc;
                    p.PvrtcTwoBpp = id <= 1;
                    p.PvrtcRgb = id is 0 or 2;
                    (i.Color, i.Channels, i.SampleType) = p.PvrtcRgb ? (ColorModel.Rgb, 3, SampleType.UInt8) : (ColorModel.Rgba, 4, SampleType.UInt8);
                    i.Transfer = srgb ? TransferFunction.Srgb : TransferFunction.Unknown;
                    return;
                case 4 or 5: throw new ImageNotSupportedException("PVR: PVRTC2 is not supported");
                case 18:
                    p.Layout = PvrLayout.Bw1;
                    (i.Color, i.Channels, i.SampleType) = (ColorModel.Gray, 1, SampleType.UInt8);
                    i.Transfer = TransferFunction.Unknown;
                    return;
                case 6: vk = 147; break;
                case 7: vk = srgb ? 134 : 133; break;
                case 8: vk = 135; i.IsPremultiplied = true; break;
                case 9: vk = srgb ? 136 : 135; break;
                case 10: vk = 137; i.IsPremultiplied = true; break;
                case 11: vk = srgb ? 138 : 137; break;
                case 12: vk = signed ? 140 : 139; break;
                case 13: vk = signed ? 142 : 141; break;
                case 14: vk = i.ChannelType == PvrChannelType.SignedFloat ? 144 : 143; break;
                case 15: vk = srgb ? 146 : 145; break;
                case 19: vk = 123; break;
                case 22: vk = srgb ? 148 : 147; break;
                case 23: vk = srgb ? 152 : 151; break;
                case 24: vk = srgb ? 150 : 149; break;
                case 25: vk = signed ? 154 : 153; break;
                case 26: vk = signed ? 156 : 155; break;
                case >= 27 and <= 40: vk = flt ? 1000066000 + (id - 27) : 157 + 2 * (id - 27) + (srgb ? 1 : 0); break;
                case >= 41 and <= 50:
                    {
                        var dm = Astc3D[id - 41];
                        p.Block = KtxFormats.Astc(dm[0], dm[1], dm[2], srgb && !flt, flt);
                        break;
                    }
                default: throw new ImageNotSupportedException($"PVR: {i.Format} is not supported");
            }
            p.Layout = PvrLayout.Block;
            p.Block ??= KtxFormats.FromVk(vk) ?? throw new ImageNotSupportedException($"PVR: {i.Format} is not supported");
            var f = p.Block;
            (i.Color, i.Channels, i.SampleType) = (f.Color, f.Channels, f.Type);
            i.Transfer = f.Srgb ? TransferFunction.Srgb : f.Type is SampleType.Float16 or SampleType.Float32 ? TransferFunction.Linear : TransferFunction.Unknown;
        }

        /// <summary>
        /// Output position of each stored channel (-1 to drop it): luminance / intensity first (gray), else R, G, B (D, S)
        /// in that order, then alpha. Alpha alone is returned as gray.
        /// </summary>
        public static int[] OutputIndex(char[] names, out ColorModel color, out int channels)
        {
            var r = new int[names.Length];
            int k = 0;
            bool gray = Array.IndexOf(names, 'l') >= 0 || Array.IndexOf(names, 'i') >= 0;
            foreach (char want in gray ? "li" : "rgbds")
                for (int c = 0; c < names.Length; c++)
                    if (names[c] == want) r[c] = k++;
            int colours = k;
            bool alpha = false;
            for (int c = 0; c < names.Length; c++)
            {
                if (names[c] == 'a') { r[c] = k++; alpha = true; }
                else if (names[c] == 'x' || gray && names[c] is not ('l' or 'i')) r[c] = -1;
            }
            channels = k;
            color = (colours, alpha, gray) switch
            {
                (0, true, _) => ColorModel.Gray,
                (1, false, _) => ColorModel.Gray,
                (1, true, _) => ColorModel.GrayAlpha,
                (3, false, false) => ColorModel.Rgb,
                (3, true, false) => ColorModel.Rgba,
                _ => ColorModel.Other,
            };
            return r;
        }

        public static ImageInfo GetInfo(ReadOnlySpan<byte> d)
        {
            var p = Parse(d);
            var i = p.Info;
            int bits = 0;
            foreach (var b in p.Bits) bits = Math.Max(bits, b);
            return new ImageInfo(ImageFormat.Pvr, i.Width, i.Height, i.Channels, i.SampleType, i.Color, i.Surfaces * i.Faces)
            {
                MipLevels = i.MipLevels,
                BitsPerSample = p.Layout == PvrLayout.Uncompressed ? bits : 0,
                Transfer = i.Transfer,
            };
        }

        /// <summary>Bytes of one 2D image (one slice) of the stored format; for 3D block formats the bytes of one block slice.</summary>
        static long ImageBytes(PvrParsed p, int w, int h) => p.Layout switch
        {
            PvrLayout.Uncompressed => (long)w * h * Total(p.Bits) / 8,
            PvrLayout.Pvrtc => Pvrtc.DataBytes(w, h, p.PvrtcTwoBpp),
            PvrLayout.Bw1 => (long)((w + 7) / 8) * h,
            _ => p.Block!.ImageBytes(w, h),
        };

        static int Total(int[] bits) { int t = 0; foreach (var b in bits) t += b; return t; }

        /// <summary>Stored slices of a level: the depth in block slices for 3D block formats.</summary>
        static int StoredSlices(PvrParsed p, int dep) => p.Block is { BlockD: > 1 } f ? (dep + f.BlockD - 1) / f.BlockD : dep;

        public static PvrTexture Decode(ReadOnlySpan<byte> d, PvrDecodeOptions opt, bool topOnly)
        {
            var p = Parse(d);
            var i = p.Info;
            int mips = topOnly ? 1 : i.MipLevels;
            long total = 0;
            for (int m = 0; m < mips; m++)
                total += (long)Math.Max(1, i.Width >> m) * Math.Max(1, i.Height >> m) * Math.Max(1, i.Depth >> m) * i.Surfaces * i.Faces;
            if (total > opt.MaxPixels) throw new ImageNotSupportedException($"PVR: {total} pixels exceed MaxPixels");
            // where every (mip, surface, face) starts
            int images = i.Surfaces * i.Faces;
            var levelBytes = new long[i.MipLevels];
            for (int m = 0; m < i.MipLevels; m++)
            {
                int w = Math.Max(1, i.Width >> m), h = Math.Max(1, i.Height >> m), dep = Math.Max(1, i.Depth >> m);
                levelBytes[m] = ImageBytes(p, w, h) * StoredSlices(p, dep);
            }
            long Offset(int m, int s, int f)
            {
                long o = p.DataOffset;
                int image = s * i.Faces + f;
                if (p.SurfaceMajor)
                {
                    long chain = 0;
                    foreach (var b in levelBytes) chain += b;
                    o += image * chain;
                    for (int k = 0; k < m; k++) o += levelBytes[k];
                }
                else
                {
                    for (int k = 0; k < m; k++) o += levelBytes[k] * images;
                    o += image * levelBytes[m];
                }
                return o;
            }
            // the faces in +X, -X, +Y, -Y, +Z, -Z order when the metadata names all six
            var faceOf = new int[i.Faces];
            for (int f = 0; f < faceOf.Length; f++) faceOf[f] = f;
            if (i.Faces == 6 && i.CubeFaceOrder is { Length: 6 } order)
            {
                var map = new int[6];
                bool ok = true;
                var seen = new bool[6];
                for (int k = 0; k < 6; k++)
                {
                    int c = "XxYyZz".IndexOf(order[k]);
                    if (c < 0 || seen[c]) { ok = false; break; }
                    seen[c] = true; map[c] = k;
                }
                if (ok) faceOf = map;
            }
            var t = new PvrTexture
            {
                Version = i.Version, Width = i.Width, Height = i.Height, Depth = i.Depth, MipLevels = i.MipLevels, Surfaces = i.Surfaces,
                Faces = i.Faces, PixelFormat = i.PixelFormat, Format = i.Format, ChannelType = i.ChannelType, IsSrgb = i.IsSrgb,
                IsCompressed = i.IsCompressed, IsPremultiplied = i.IsPremultiplied, CubeFaceOrder = i.CubeFaceOrder, XLeft = i.XLeft,
                YUp = i.YUp, ZOut = i.ZOut, Metadata = i.Metadata, Transfer = i.Transfer, Color = i.Color, SampleType = i.SampleType,
                Channels = i.Channels,
            };
            t.Images = new RawImage[mips][][][];
            for (int m = 0; m < mips; m++)
            {
                int w = Math.Max(1, i.Width >> m), h = Math.Max(1, i.Height >> m), dep = Math.Max(1, i.Depth >> m);
                long bytes = ImageBytes(p, w, h);
                var lv = t.Images[m] = new RawImage[i.Surfaces][][];
                for (int s = 0; s < i.Surfaces; s++)
                {
                    lv[s] = new RawImage[i.Faces][];
                    for (int f = 0; f < i.Faces; f++)
                    {
                        long o = Offset(m, s, faceOf[f]);
                        long need = bytes * StoredSlices(p, dep);
                        if (o + need > d.Length) throw new ImageFormatException("PVR: image data truncated");
                        RawImage[] slices;
                        if (p.Block is { BlockD: > 1 } vf)
                            slices = KtxBlocks.Astc3D(vf, d.Slice((int)o, (int)need), w, h, dep);
                        else
                        {
                            slices = new RawImage[dep];
                            for (int z = 0; z < dep; z++) slices[z] = Surface(p, d.Slice((int)(o + z * bytes), (int)bytes), w, h);
                        }
                        if (opt.ApplyOrientation && i.ZOut && dep > 1) Array.Reverse(slices);
                        for (int z = 0; z < slices.Length; z++)
                        {
                            var img = slices[z];
                            if (opt.ApplyOrientation && i.YUp) img = ImageOps.FlipVertical(img);
                            if (opt.ApplyOrientation && i.XLeft) img = ImageOps.FlipHorizontal(img);
                            if (i.IsPremultiplied && opt.Unpremultiply && img.HasAlpha) DdsSurfaces.Unpremultiply(img);
                            img.Metadata = new ImageMetadata { Transfer = i.Transfer };
                            slices[z] = img;
                        }
                        lv[s][f] = slices;
                    }
                }
            }
            return t;
        }

        static RawImage Surface(PvrParsed p, ReadOnlySpan<byte> src, int w, int h)
        {
            switch (p.Layout)
            {
                case PvrLayout.Pvrtc:
                    {
                        var rgba = Pvrtc.Decode(src, w, h, p.PvrtcTwoBpp);
                        if (!p.PvrtcRgb) return new RawImage(w, h, ColorModel.Rgba, rgba, 4);
                        int n = w * h;
                        var o = new byte[n * 3];
                        for (int k = 0; k < n; k++) { o[k * 3] = rgba[k * 4]; o[k * 3 + 1] = rgba[k * 4 + 1]; o[k * 3 + 2] = rgba[k * 4 + 2]; }
                        return new RawImage(w, h, ColorModel.Rgb, o, 3);
                    }
                case PvrLayout.Bw1:
                    {
                        var o = new byte[w * h];
                        int rb = (w + 7) / 8;
                        for (int y = 0; y < h; y++)
                            for (int x = 0; x < w; x++)
                                o[y * w + x] = (src[y * rb + (x >> 3)] >> (x & 7) & 1) != 0 ? (byte)255 : (byte)0;   // least significant bit first
                        return new RawImage(w, h, ColorModel.Gray, o, 1);
                    }
                case PvrLayout.Block:
                    {
                        var f = p.Block!;
                        switch (f.Layout)
                        {
                            case KtxLayout.Etc: return KtxBlocks.Etc(f, src, w, h);
                            case KtxLayout.Astc: return KtxBlocks.Astc(f, src, w, h);
                            default:
                                {
                                    var df = DdsReader.FromDxgi(f.Dxgi)!;
                                    var img = df.BlockBytes > 0 ? DdsSurfaces.Blocks(df, src, w, h) : DdsSurfaces.Pixels(df, src, w, h);
                                    return img;
                                }
                        }
                    }
                default:
                    return Uncompressed(p, p.Twiddled ? Untwiddle(src, w, h, Total(p.Bits) / 8) : src, w, h);
            }
        }

        static byte[] Untwiddle(ReadOnlySpan<byte> src, int w, int h, int bpp)
        {
            int tw = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)w), th = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)h);
            var o = new byte[(long)w * h * bpp];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    long s = (long)Pvrtc.MortonOffset(x, y, tw, th) * bpp;
                    if (s + bpp <= src.Length) src.Slice((int)s, bpp).CopyTo(o.AsSpan((y * w + x) * bpp));
                }
            return o;
        }

        static RawImage Uncompressed(PvrParsed p, ReadOnlySpan<byte> src, int w, int h)
        {
            var i = p.Info;
            var idx = OutputIndex(p.Names, out _, out int ch);
            var img = RawImage.Create(w, h, i.Color, i.SampleType, ch);
            int bps = RawImage.BytesPerSample(i.SampleType), outBits = bps * 8;
            var dst = img.GetBytes();
            int nc = p.Names.Length, pixelBytes = Total(p.Bits) / 8;
            long n = (long)w * h;
            bool aligned = true, identity = true;
            for (int c = 0; c < nc; c++)
            {
                aligned &= p.Bits[c] % 8 == 0;
                identity &= idx[c] == c && p.Bits[c] == outBits;
            }
            if (identity && nc == ch)
            {
                src.Slice(0, (int)(n * pixelBytes)).CopyTo(dst);
                return img;
            }
            if (aligned)
            {
                bool whole = true;
                for (int c = 0; c < nc; c++) whole &= p.Bits[c] == outBits;
                if (whole)
                {
                    // whole samples in another order (bgra8, argb16, ...) or with dropped channels: copy them over
                    for (int c = 0; c < nc; c++)
                    {
                        int o = idx[c];
                        if (o < 0) continue;
                        int si = c * bps, di = o * bps, os = ch * bps;
                        if (bps == 1)
                            for (long k = 0; k < n; k++) dst[(int)(k * os + di)] = src[(int)(k * pixelBytes + si)];
                        else
                            for (long k = 0; k < n; k++) src.Slice((int)(k * pixelBytes + si), bps).CopyTo(dst.Slice((int)(k * os + di)));
                    }
                    return img;
                }
            }
            var shifts = new int[nc];
            if (aligned) { int s = 0; for (int c = 0; c < nc; c++) { shifts[c] = s; s += p.Bits[c]; } }
            else { int s = Total(p.Bits); for (int c = 0; c < nc; c++) { s -= p.Bits[c]; shifts[c] = s; } }
            bool flt = p.IsFloat, signed = p.IsSigned, norm = p.IsNormalized;
            if (!flt && !signed && pixelBytes is 1 or 2 or 4 && bps <= 2)
            {
                // packed unsigned fields in an 8, 16 or 32 bit word (565, 4444, 1555, 10:10:10:2, ...): per field tables
                var tables = new ushort[nc][];
                for (int c = 0; c < nc; c++)
                {
                    int b = p.Bits[c];
                    if (b > 16 || idx[c] < 0) continue;
                    var t = tables[c] = new ushort[1 << b];
                    ulong max = (1UL << b) - 1, omax = (1UL << outBits) - 1;
                    for (int v = 0; v < t.Length; v++) t[v] = norm && b < outBits ? (ushort)(((ulong)v * omax + max / 2) / max) : (ushort)v;
                }
                bool ok = true;
                for (int c = 0; c < nc; c++) ok &= idx[c] < 0 || tables[c] != null;
                if (ok)
                {
                    var o16 = bps == 2 ? System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(dst) : default;
                    for (long k = 0; k < n; k++)
                    {
                        int at = (int)(k * pixelBytes);
                        uint v = pixelBytes switch { 1 => src[at], 2 => BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(at)), _ => BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(at)) };
                        for (int c = 0; c < nc; c++)
                        {
                            if (idx[c] < 0) continue;
                            ushort r = tables[c][v >> shifts[c] & (uint)(tables[c].Length - 1)];
                            if (bps == 1) dst[(int)(k * ch + idx[c])] = (byte)r; else o16[(int)(k * ch + idx[c])] = r;
                        }
                    }
                    return img;
                }
            }
            Span<byte> word = stackalloc byte[16];
            for (long k = 0; k < n; k++)
            {
                var px = src.Slice((int)(k * pixelBytes), pixelBytes);
                word.Clear();
                px.CopyTo(word);
                UInt128 v = BinaryPrimitives.ReadUInt128LittleEndian(word);
                for (int c = 0; c < nc; c++)
                {
                    int o = idx[c];
                    if (o < 0) continue;
                    int b = p.Bits[c];
                    ulong x = (ulong)(v >> shifts[c]) & (b == 64 ? ulong.MaxValue : (1UL << b) - 1);
                    var d = dst.Slice((int)((k * ch + o) * bps), bps);
                    if (flt)
                    {
                        float f = b switch
                        {
                            16 => (float)BitConverter.UInt16BitsToHalf((ushort)x),
                            32 => BitConverter.UInt32BitsToSingle((uint)x),
                            _ => SmallFloat((uint)x, b - 5),
                        };
                        if (bps == 2) BinaryPrimitives.WriteHalfLittleEndian(d, (Half)f); else BinaryPrimitives.WriteSingleLittleEndian(d, f);
                        continue;
                    }
                    ulong r;
                    if (signed)
                    {
                        long sv = (long)(x << (64 - b)) >> (64 - b);
                        if (norm && b < outBits && b > 1) sv = (long)Math.Round(sv * (double)((1L << (outBits - 1)) - 1) / ((1L << (b - 1)) - 1));
                        r = (ulong)sv;
                    }
                    else if (norm && b < outBits)
                    {
                        ulong max = (1UL << b) - 1, omax = outBits == 64 ? ulong.MaxValue : (1UL << outBits) - 1;
                        r = (ulong)(((UInt128)x * omax + max / 2) / max);
                    }
                    else r = x;
                    switch (bps)
                    {
                        case 1: d[0] = (byte)r; break;
                        case 2: BinaryPrimitives.WriteUInt16LittleEndian(d, (ushort)r); break;
                        default: BinaryPrimitives.WriteUInt32LittleEndian(d, (uint)r); break;
                    }
                }
            }
            return img;
        }

        /// <summary>Unsigned 10 / 11 bit floats: 5 bit exponent, <paramref name="mantissaBits"/> bit mantissa.</summary>
        static float SmallFloat(uint v, int mantissaBits)
        {
            int e = (int)(v >> mantissaBits) & 31, m = (int)(v & ((1u << mantissaBits) - 1));
            if (e == 0) return m / (float)(1 << mantissaBits) * MathF.Pow(2, -14);
            if (e == 31) return m == 0 ? float.PositiveInfinity : float.NaN;
            return (1 + m / (float)(1 << mantissaBits)) * MathF.Pow(2, e - 15);
        }
    }
}
