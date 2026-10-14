using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

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
            var ro = new ResizeOptions { Filter = opt.MipmapFilter };
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
            public int Channels;                   // stored channels (images with fewer are expanded)
            public bool Bgr;                       // legacy 8 bit colour: B, G, R(, A) byte order
        }

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

        public static void Write(Stream output, IReadOnlyList<IReadOnlyList<RawImage>> chains, DdsTextureKind kind, DdsEncodeOptions opt)
        {
            var first = chains[0][0];
            int w = first.Width, h = first.Height, mips = chains[0].Count;
            foreach (var chain in chains)
            {
                if (chain.Count != mips) throw new ArgumentException("DDS: every face needs the same number of mip levels");
                for (int m = 0; m < chain.Count; m++)
                {
                    var l = chain[m];
                    if (l.Width != Math.Max(1, w >> m) || l.Height != Math.Max(1, h >> m))
                        throw new ArgumentException($"DDS: mip level {m} must be {Math.Max(1, w >> m)} x {Math.Max(1, h >> m)}, not {l.Width} x {l.Height}");
                    if (l.SampleType != first.SampleType || l.Color != first.Color || l.Channels != first.Channels)
                        throw new ArgumentException("DDS: all surfaces need the same sample type and colour model");
                }
            }
            if (kind == DdsTextureKind.CubeMap && w != h) throw new ArgumentException("DDS: cube faces must be square");
            if (mips > 1 + System.Numerics.BitOperations.Log2((uint)Math.Max(w, h))) throw new ArgumentException("DDS: too many mip levels");
            var t = Choose(first, opt.Srgb);
            int pixelBytes = t.Dxgi == 0 ? (int)t.Bits / 8 : t.Channels * RawImage.BytesPerSample(first.SampleType);

            var head = new byte[t.Dxgi != 0 ? 148 : 128];
            var s = head.AsSpan();
            BinaryPrimitives.WriteUInt32LittleEndian(s, 0x20534444);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(4), 124);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(8), 0x1 | 0x2 | 0x4 | 0x8 | 0x1000 | (mips > 1 ? 0x20000u : 0));
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(12), (uint)h);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(16), (uint)w);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(20), (uint)(w * pixelBytes));   // pitch
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(28), (uint)mips);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(76), 32);
            if (t.Dxgi != 0)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(80), 4);
                "DX10"u8.CopyTo(s.Slice(84));
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
            uint caps = 0x1000 | (mips > 1 ? 0x400008u : 0) | (kind == DdsTextureKind.CubeMap ? 0x8u : 0);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(108), caps);
            if (kind == DdsTextureKind.CubeMap) BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(112), 0x200 | 0xFC00);
            if (t.Dxgi != 0)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(128), (uint)t.Dxgi);
                BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(132), 3);   // 2D
                BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(136), kind == DdsTextureKind.CubeMap ? 4u : 0);
                BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(140), 1);
                BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(144), t.Channels == 4 ? 1u : 0);   // straight alpha
            }
            output.Write(head);

            foreach (var chain in chains)
                foreach (var level in chain) output.Write(Pixels(level, t));
        }

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
