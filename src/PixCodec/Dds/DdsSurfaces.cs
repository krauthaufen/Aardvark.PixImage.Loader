using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace PixCodec.DdsImpl
{
    /// <summary>Turns the stored bytes of one surface into a RawImage.</summary>
    internal static class DdsSurfaces
    {
        // ------------------------------------------------------------------ block compressed

        public static RawImage Blocks(DdsFormat f, ReadOnlySpan<byte> src, int w, int h)
        {
            int bw = Math.Max(1, (w + 3) / 4), bh = Math.Max(1, (h + 3) / 4), ch = f.Channels;
            var img = RawImage.Create(w, h, f.Color, f.Type, ch);
            int bpb = f.BlockBytes;
            Span<byte> t8 = stackalloc byte[64];
            Span<sbyte> ts = stackalloc sbyte[32];
            Span<ushort> t16 = stackalloc ushort[48];
            var dst = img.GetBytes();
            int sampleBytes = RawImage.BytesPerSample(f.Type), pixelBytes = ch * sampleBytes;
            for (int by = 0; by < bh; by++)
                for (int bx = 0; bx < bw; bx++)
                {
                    var b = src.Slice((by * bw + bx) * bpb, bpb);
                    scoped ReadOnlySpan<byte> texels;
                    switch (f.Dxgi)
                    {
                        case 71: case 72: BcBlocks.Color(b, t8, false); texels = t8; break;
                        case 74: case 75: BcBlocks.Color(b.Slice(8), t8, true); BcBlocks.ExplicitAlpha(b, t8); texels = t8; break;
                        case 77: case 78: BcBlocks.Color(b.Slice(8), t8, true); BcBlocks.Channel(b, t8.Slice(3), 4); texels = t8; break;
                        case 80: BcBlocks.Channel(b, t8, 1); texels = t8; break;
                        case 83: BcBlocks.Channel(b, t8, 2); BcBlocks.Channel(b.Slice(8), t8.Slice(1), 2); texels = t8; break;
                        case 81: BcBlocks.ChannelSigned(b, ts, 1); texels = MemoryMarshal.AsBytes(ts); break;
                        case 84: BcBlocks.ChannelSigned(b, ts, 2); BcBlocks.ChannelSigned(b.Slice(8), ts.Slice(1), 2); texels = MemoryMarshal.AsBytes(ts); break;
                        case 95: case 96: Bptc.Bc6h(b, t16, f.Dxgi == 96); texels = MemoryMarshal.AsBytes(t16); break;
                        case 98: case 99: Bptc.Bc7(b, t8); texels = t8; break;
                        default: throw new ImageNotSupportedException($"DDS: {f.Name}");
                    }
                    int x0 = bx * 4, y0 = by * 4, cw = Math.Min(4, w - x0), chh = Math.Min(4, h - y0);
                    for (int y = 0; y < chh; y++)
                        texels.Slice(y * 4 * pixelBytes, cw * pixelBytes).CopyTo(dst.Slice(((y0 + y) * w + x0) * pixelBytes));
                }
            return img;
        }

        // ------------------------------------------------------------------ uncompressed

        public static RawImage Pixels(DdsFormat f, ReadOnlySpan<byte> src, int w, int h)
        {
            var img = RawImage.Create(w, h, f.Color, f.Type, f.Channels);
            long n = (long)w * h;
            if (f.Masks != null) { Masked(f, src, img); return img; }
            var d = img.GetBytes();
            switch (f.Dxgi)
            {
                case 87:   // B8G8R8A8
                    for (long i = 0; i < n; i++) { d[(int)(i * 4)] = src[(int)(i * 4 + 2)]; d[(int)(i * 4 + 1)] = src[(int)(i * 4 + 1)]; d[(int)(i * 4 + 2)] = src[(int)(i * 4)]; d[(int)(i * 4 + 3)] = src[(int)(i * 4 + 3)]; }
                    break;
                case 91: goto case 87;
                case 88:   // B8G8R8X8
                case 93:
                    for (long i = 0; i < n; i++) { d[(int)(i * 3)] = src[(int)(i * 4 + 2)]; d[(int)(i * 3 + 1)] = src[(int)(i * 4 + 1)]; d[(int)(i * 3 + 2)] = src[(int)(i * 4)]; }
                    break;
                case 24:
                case 25:   // R10G10B10A2: UNORM scaled to 16 bits, UINT as stored
                    {
                        var o = img.GetData<ushort>();
                        bool norm = f.Dxgi == 24;
                        for (long i = 0; i < n; i++)
                        {
                            uint v = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice((int)(i * 4)));
                            for (int c = 0; c < 3; c++) { uint x = v >> (10 * c) & 1023; o[i * 4 + c] = (ushort)(norm ? (x * 65535 + 511) / 1023 : x); }
                            uint a = v >> 30;
                            o[i * 4 + 3] = (ushort)(norm ? a * 21845 : a);
                        }
                        break;
                    }
                case 85: Packed16(src, d, n, 3, new[] { (11, 5), (5, 6), (0, 5) }); break;
                case 86: Packed16(src, d, n, 4, new[] { (10, 5), (5, 5), (0, 5), (15, 1) }); break;
                case 115: Packed16(src, d, n, 4, new[] { (8, 4), (4, 4), (0, 4), (12, 4) }); break;
                case 26:   // R11G11B10_FLOAT
                    {
                        var o = img.GetData<float>();
                        for (long i = 0; i < n; i++)
                        {
                            uint v = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice((int)(i * 4)));
                            o[i * 3] = SmallFloat(v & 0x7FF, 6); o[i * 3 + 1] = SmallFloat(v >> 11 & 0x7FF, 6); o[i * 3 + 2] = SmallFloat(v >> 22, 5);
                        }
                        break;
                    }
                case 67:   // R9G9B9E5_SHAREDEXP
                    {
                        var o = img.GetData<float>();
                        for (long i = 0; i < n; i++)
                        {
                            uint v = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice((int)(i * 4)));
                            float scale = MathF.Pow(2, (int)(v >> 27) - 15 - 9);
                            o[i * 3] = (v & 511) * scale; o[i * 3 + 1] = (v >> 9 & 511) * scale; o[i * 3 + 2] = (v >> 18 & 511) * scale;
                        }
                        break;
                    }
                default:
                    // the stored samples are the result's samples (little endian, channel interleaved)
                    if (f.Channels * RawImage.BytesPerSample(f.Type) * 8 != f.PixelBits) throw new ImageNotSupportedException($"DDS: {f.Name}");
                    src.Slice(0, d.Length).CopyTo(d);
                    break;
            }
            return img;
        }

        /// <summary>16 bit packed pixels: (shift, bits) per output channel, scaled to 8 bits rounding to nearest.</summary>
        static void Packed16(ReadOnlySpan<byte> src, Span<byte> d, long n, int ch, (int shift, int bits)[] fields)
        {
            for (long i = 0; i < n; i++)
            {
                int v = src[(int)(i * 2)] | src[(int)(i * 2 + 1)] << 8;
                for (int c = 0; c < ch; c++)
                {
                    int max = (1 << fields[c].bits) - 1;
                    d[(int)(i * ch + c)] = (byte)(((v >> fields[c].shift & max) * 255 + max / 2) / max);
                }
            }
        }

        /// <summary>The unsigned 11 / 10 bit floats of R11G11B10_FLOAT: 5 exponent bits (bias 15) and <paramref name="mantissaBits"/>.</summary>
        static float SmallFloat(uint v, int mantissaBits)
        {
            uint e = v >> mantissaBits, m = v & ((1u << mantissaBits) - 1);
            if (e == 0) return m * MathF.Pow(2, -14 - mantissaBits);
            if (e == 31) return m == 0 ? float.PositiveInfinity : float.NaN;
            return (1 + m / (float)(1 << mantissaBits)) * MathF.Pow(2, (int)e - 15);
        }

        /// <summary>Legacy bit mask formats, fields scaled to the output size rounding to nearest (wider fields keep their top bits).</summary>
        static void Masked(DdsFormat f, ReadOnlySpan<byte> src, RawImage img)
        {
            int bytes = f.PixelBits / 8, ch = f.Channels;
            long n = (long)img.Width * img.Height;
            var m = f.Masks!;
            uint[] use = f.Luminance ? (ch == 2 ? new[] { m[0], m[3] } : new[] { m[0] }) : f.AlphaOnly ? new[] { m[3] } : ch == 4 ? m : new[] { m[0], m[1], m[2] };
            int outBits = f.Type == SampleType.UInt16 ? 16 : 8;
            var shifts = new int[ch]; var maxes = new uint[ch]; var widths = new int[ch];
            for (int c = 0; c < ch; c++)
            {
                shifts[c] = use[c] == 0 ? 0 : BitOperations.TrailingZeroCount(use[c]);
                maxes[c] = use[c] >> shifts[c];
                widths[c] = BitOperations.PopCount(use[c]);
            }
            uint outMax = (1u << outBits) - 1;
            byte[]? o8 = outBits == 8 ? img.GetData<byte>() : null;
            ushort[]? o16 = outBits == 16 ? img.GetData<ushort>() : null;
            for (long i = 0; i < n; i++)
            {
                uint v = 0;
                for (int k = 0; k < bytes; k++) v |= (uint)src[(int)(i * bytes + k)] << (8 * k);
                for (int c = 0; c < ch; c++)
                {
                    uint x = maxes[c] == 0 ? 0 : v >> shifts[c] & maxes[c];
                    uint s = maxes[c] == 0 ? 0 : widths[c] >= outBits ? x >> (widths[c] - outBits) : (uint)(((ulong)x * outMax + maxes[c] / 2) / maxes[c]);
                    if (o8 != null) o8[i * ch + c] = (byte)s; else o16![i * ch + c] = (ushort)s;
                }
            }
        }

        // ------------------------------------------------------------------ premultiplied alpha

        public static void Unpremultiply(RawImage img)
        {
            int ch = img.Channels;
            long n = (long)img.Width * img.Height;
            switch (img.SampleType)
            {
                case SampleType.UInt8:
                    {
                        var d = img.GetData<byte>();
                        for (long i = 0; i < n; i++)
                        {
                            int a = d[i * ch + ch - 1];
                            if (a == 0 || a == 255) continue;
                            for (int c = 0; c < ch - 1; c++) d[i * ch + c] = (byte)Math.Min(255, (d[i * ch + c] * 255 + a / 2) / a);
                        }
                        break;
                    }
                case SampleType.UInt16:
                    {
                        var d = img.GetData<ushort>();
                        for (long i = 0; i < n; i++)
                        {
                            long a = d[i * ch + ch - 1];
                            if (a == 0 || a == 65535) continue;
                            for (int c = 0; c < ch - 1; c++) d[i * ch + c] = (ushort)Math.Min(65535, (d[i * ch + c] * 65535L + a / 2) / a);
                        }
                        break;
                    }
                case SampleType.Float16:
                    {
                        var d = img.GetData<Half>();
                        for (long i = 0; i < n; i++)
                        {
                            float a = (float)d[i * ch + ch - 1];
                            if (a == 0 || a == 1) continue;
                            for (int c = 0; c < ch - 1; c++) d[i * ch + c] = (Half)((float)d[i * ch + c] / a);
                        }
                        break;
                    }
                case SampleType.Float32:
                    {
                        var d = img.GetData<float>();
                        for (long i = 0; i < n; i++)
                        {
                            float a = d[i * ch + ch - 1];
                            if (a == 0 || a == 1) continue;
                            for (int c = 0; c < ch - 1; c++) d[i * ch + c] /= a;
                        }
                        break;
                    }
            }
        }
    }
}
