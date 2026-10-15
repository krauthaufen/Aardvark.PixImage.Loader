using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Aardvark.ImageCodecs.KtxImpl
{
    /// <summary>Decoding of the block formats KTX adds to the DDS ones: ETC, ASTC and the Basis Universal formats.</summary>
    internal static class KtxBlocks
    {
        public static RawImage Uastc(ReadOnlySpan<byte> blocks, int w, int h)
        {
            var o = new byte[(long)w * h * 4];
            BasisUastc.DecodeImage(blocks, w, h, o);
            return new RawImage(w, h, ColorModel.Rgba, o, 4);
        }

        public static RawImage Etc(KtxFormat f, ReadOnlySpan<byte> src, int w, int h)
        {
            var img = RawImage.Create(w, h, f.Color, f.Type, f.Channels);
            int bw = (w + 3) / 4, bh = (h + 3) / 4, bb = f.BlockBytes, ch = f.Channels;
            int kind = f.Name switch
            {
                _ when f.Name.StartsWith("ETC2_R8G8B8_") => 0,
                _ when f.Name.StartsWith("ETC2_R8G8B8A1_") => 1,
                _ when f.Name.StartsWith("ETC2_R8G8B8A8_") => 2,
                _ when f.Name.StartsWith("EAC_R11G11_") => 4,
                _ => 3,
            };
            var data = src.ToArray();
            var dst = img.Data;
            Parallel.For(0, bh, by =>
            {
                Span<byte> rgba = stackalloc byte[64];
                Span<ushort> u = stackalloc ushort[32];
                Span<short> s = stackalloc short[32];
                for (int bx = 0; bx < bw; bx++)
                {
                    var b = data.AsSpan((by * bw + bx) * bb, bb);
                    switch (kind)
                    {
                        case 0: EtcBlocks.Etc2Rgb(b, rgba); break;
                        case 1: EtcBlocks.Etc2PunchThrough(b, rgba); break;
                        case 2: EtcBlocks.Etc2Rgba(b, rgba); break;
                        case 3: if (f.Signed) EtcBlocks.EacR11Signed(b, s, 1); else EtcBlocks.EacR11(b, u, 1); break;
                        default: if (f.Signed) EtcBlocks.EacRg11Signed(b, s); else EtcBlocks.EacRg11(b, u); break;
                    }
                    int cw = Math.Min(4, w - bx * 4), chh = Math.Min(4, h - by * 4);
                    for (int y = 0; y < chh; y++)
                        for (int x = 0; x < cw; x++)
                        {
                            long o = ((long)(by * 4 + y) * w + bx * 4 + x) * ch;
                            int t = y * 4 + x;
                            // by sample type: short[] would also match a ushort[] pattern (CLR array covariance)
                            switch (f.Type)
                            {
                                case SampleType.UInt8: { var d = (byte[])dst; for (int c = 0; c < ch; c++) d[o + c] = rgba[t * 4 + c]; break; }
                                case SampleType.UInt16: { var d = (ushort[])dst; for (int c = 0; c < ch; c++) d[o + c] = u[t * ch + c]; break; }
                                default: { var d = (short[])dst; for (int c = 0; c < ch; c++) d[o + c] = s[t * ch + c]; break; }
                            }
                        }
                }
            });
            return img;
        }

        /// <summary>3D ASTC: <paramref name="d"/> slices from block slices of BlockD texels each.</summary>
        public static RawImage[] Astc3D(KtxFormat f, ReadOnlySpan<byte> src, int w, int h, int d)
        {
            int tw = f.BlockW, th = f.BlockH, td = f.BlockD;
            int bw = (w + tw - 1) / tw, bh = (h + th - 1) / th, bz = (d + td - 1) / td;
            var slices = new RawImage[d];
            for (int z = 0; z < d; z++) slices[z] = RawImage.Create(w, h, ColorModel.Rgba, f.Hdr ? SampleType.Float16 : SampleType.UInt8, 4);
            var data = src.ToArray();
            Parallel.For(0, bz * bh, row =>
            {
                int zb = row / bh, by = row % bh;
                int n = tw * th * td * 4;
                Span<byte> rgba = stackalloc byte[n];
                Span<ushort> half = stackalloc ushort[n];
                for (int bx = 0; bx < bw; bx++)
                {
                    var b = data.AsSpan(((zb * bh + by) * bw + bx) * 16, 16);
                    if (f.Hdr) AstcBlocks.DecodeFloat16(b, tw, th, td, half);
                    else AstcBlocks.DecodeUnorm8(b, tw, th, td, f.Srgb, rgba);
                    int cw = Math.Min(tw, w - bx * tw), chh = Math.Min(th, h - by * th), cd = Math.Min(td, d - zb * td);
                    for (int z = 0; z < cd; z++)
                        for (int y = 0; y < chh; y++)
                        {
                            int src0 = ((z * th + y) * tw) * 4;
                            long o = ((long)(by * th + y) * w + bx * tw) * 4;
                            var dst = slices[zb * td + z].Data;
                            if (f.Hdr) System.Runtime.InteropServices.MemoryMarshal.Cast<ushort, Half>(half.Slice(src0, cw * 4)).CopyTo(((Half[])dst).AsSpan((int)o));
                            else rgba.Slice(src0, cw * 4).CopyTo(((byte[])dst).AsSpan((int)o));
                        }
                }
            });
            return slices;
        }

        public static RawImage Astc(KtxFormat f, ReadOnlySpan<byte> src, int w, int h)
        {
            var img = RawImage.Create(w, h, ColorModel.Rgba, f.Hdr ? SampleType.Float16 : SampleType.UInt8, 4);
            int bw = (w + f.BlockW - 1) / f.BlockW, bh = (h + f.BlockH - 1) / f.BlockH;
            var data = src.ToArray();
            var dst = img.Data;
            int tw = f.BlockW, th = f.BlockH;
            Parallel.For(0, bh, by =>
            {
                Span<byte> rgba = stackalloc byte[12 * 12 * 4];
                Span<ushort> half = stackalloc ushort[12 * 12 * 4];
                for (int bx = 0; bx < bw; bx++)
                {
                    var b = data.AsSpan((by * bw + bx) * 16, 16);
                    if (f.Hdr) AstcBlocks.DecodeFloat16(b, tw, th, half);
                    else AstcBlocks.DecodeUnorm8(b, tw, th, f.Srgb, rgba);
                    int cw = Math.Min(tw, w - bx * tw), chh = Math.Min(th, h - by * th);
                    for (int y = 0; y < chh; y++)
                    {
                        long o = ((long)(by * th + y) * w + bx * tw) * 4;
                        if (f.Hdr) System.Runtime.InteropServices.MemoryMarshal.Cast<ushort, Half>(half.Slice(y * tw * 4, cw * 4)).CopyTo(((Half[])dst).AsSpan((int)o));
                        else rgba.Slice(y * tw * 4, cw * 4).CopyTo(((byte[])dst).AsSpan((int)o));
                    }
                }
            });
            return img;
        }
    }

    /// <summary>BasisLZ global data of one file, and the video state of P-frames per level.</summary>
    internal sealed class BasisLzState
    {
        readonly BasisLzGlobal global;
        readonly Dictionary<int, BasisLzVideoState> video = new();
        readonly KtxTextureInfo info;

        BasisLzState(BasisLzGlobal g, KtxTextureInfo info) { global = g; this.info = info; }

        public static BasisLzState Create(ReadOnlySpan<byte> d, KtxParsed p)
        {
            var i = p.Info;
            if (p.SgdLength <= 0 || p.SgdOffset + p.SgdLength > d.Length) throw new ImageFormatException("KTX2: BasisLZ global data missing");
            int count = BasisEtc1s.ImageCount(i.MipLevels, i.IsArray ? i.Layers : 0, i.Faces, i.Kind == KtxTextureKind.Texture3D ? i.Depth : 0);
            return new BasisLzState(BasisEtc1s.ParseGlobal(d.Slice((int)p.SgdOffset, (int)p.SgdLength), count), i);
        }

        public RawImage Decode(int imageIndex, byte[] level, int w, int h)
        {
            var o = new byte[(long)w * h * 4];
            // P-frames (KTXanimData) predict from the previous image of the same level
            int levelOfImage = LevelOf(imageIndex);
            if (!video.TryGetValue(levelOfImage, out var v)) video[levelOfImage] = v = new BasisLzVideoState();
            BasisEtc1s.DecodeImage(global, imageIndex, level, w, h, o, v);
            return new RawImage(w, h, ColorModel.Rgba, o, 4);
        }

        int LevelOf(int imageIndex)
        {
            int n = 0;
            for (int m = 0; m < info.MipLevels; m++)
            {
                n += info.Layers * info.Faces * Math.Max(1, info.Depth >> m);
                if (imageIndex < n) return m;
            }
            return info.MipLevels - 1;
        }
    }
}
