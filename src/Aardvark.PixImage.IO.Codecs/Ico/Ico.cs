using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Aardvark.ImageCodecs
{
    /// <summary>One image of an ICO / CUR file as its directory describes it.</summary>
    public sealed record IcoEntry(int Width, int Height, int BitCount, bool IsPng, int HotspotX, int HotspotY, int Offset, int Size);

    public sealed class IcoDecodeOptions
    {
        /// <summary>Which directory entry <see cref="Ico.Decode"/> returns; -1 (the default) picks the largest, then the deepest.</summary>
        public int Index { get; set; } = -1;

        internal static readonly IcoDecodeOptions Default = new();
    }

    public sealed class IcoEncodeOptions
    {
        /// <summary>Images at least this wide or high are stored as PNG, smaller ones as 32 bit bitmaps (what Windows itself writes). Default 256.</summary>
        public int PngFromSize { get; set; } = 256;

        /// <summary>Write a cursor (CUR) instead of an icon.</summary>
        public bool Cursor { get; set; }

        /// <summary>Cursor hotspot of every image (pixels from the top left corner); null = (0, 0).</summary>
        public IReadOnlyList<(int X, int Y)>? Hotspots { get; set; }
    }

    /// <summary>
    /// Windows icons and cursors (ICO, CUR): a directory of images, each a PNG or a DIB with an AND mask. DIB entries (1 to 32
    /// bit, any BMP header) decode to UInt8 Rgba, with transparency from the alpha channel of 32 bit images or else the mask;
    /// PNG entries decode as PNG does. <see cref="Decode"/> returns the largest image, <see cref="ImageCodec.DecodeAll(ReadOnlySpan{byte})"/>
    /// all of them. Writes icons and cursors from UInt8 images (up to 256 x 256).
    /// </summary>
    public static class Ico
    {
        /// <summary>Whether the data starts with a plausible ICO / CUR directory (reserved 0, type 1 or 2, entries that fit).</summary>
        public static bool IsMatch(ReadOnlySpan<byte> h)
        {
            if (h.Length < 22 || h[0] != 0 || h[1] != 0 || h[3] != 0 || (h[2] != 1 && h[2] != 2)) return false;
            int n = BinaryPrimitives.ReadUInt16LittleEndian(h.Slice(4));
            if (n == 0) return false;
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(h.Slice(14)), offset = BinaryPrimitives.ReadUInt32LittleEndian(h.Slice(18));
            return h[9] == 0 && size >= 12 && offset >= 6 + 16u * n;
        }

        /// <summary>Reads the directory.</summary>
        public static IReadOnlyList<IcoEntry> ReadEntries(ReadOnlySpan<byte> d)
        {
            if (!IsMatch(d)) throw new ImageFormatException("ICO: not an icon or cursor file");
            bool cursor = d[2] == 2;
            int n = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(4));
            var r = new List<IcoEntry>(n);
            for (int i = 0; i < n; i++)
            {
                int e = 6 + 16 * i;
                if (e + 16 > d.Length) break;
                int w = d[e] == 0 ? 256 : d[e], h = d[e + 1] == 0 ? 256 : d[e + 1];
                int f1 = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(e + 4)), f2 = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(e + 6));
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(e + 8)), off = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(e + 12));
                if (off >= d.Length || size == 0) continue;
                size = (uint)Math.Min(size, d.Length - off);
                var img = d.Slice((int)off, (int)size);
                bool png = Png.IsMatch(img);
                int bits = cursor ? 0 : f2;
                // the directory's size and depth are hints; the image's own header decides
                if (png)
                {
                    try { var pi = Png.GetInfo(img); w = pi.Width; h = pi.Height; bits = pi.BitsPerSample * pi.Channels; } catch (ImageFormatException) { }
                }
                else if (img.Length >= 16)
                {
                    uint hs = BinaryPrimitives.ReadUInt32LittleEndian(img);
                    if (hs == 12) { w = BinaryPrimitives.ReadUInt16LittleEndian(img.Slice(4)); h = BinaryPrimitives.ReadUInt16LittleEndian(img.Slice(6)) / 2; bits = BinaryPrimitives.ReadUInt16LittleEndian(img.Slice(10)); }
                    else if (hs >= 16)
                    {
                        w = BinaryPrimitives.ReadInt32LittleEndian(img.Slice(4));
                        h = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(img.Slice(8))) / 2;
                        bits = BinaryPrimitives.ReadUInt16LittleEndian(img.Slice(14));
                    }
                }
                r.Add(new IcoEntry(w, h, bits, png, cursor ? f1 : 0, cursor ? f2 : 0, (int)off, (int)size));
            }
            if (r.Count == 0) throw new ImageFormatException("ICO: no images");
            return r;
        }

        static int Best(IReadOnlyList<IcoEntry> es)
        {
            int best = 0;
            for (int i = 1; i < es.Count; i++)
            {
                long a = (long)es[i].Width * es[i].Height, b = (long)es[best].Width * es[best].Height;
                if (a > b || a == b && es[i].BitCount > es[best].BitCount) best = i;
            }
            return best;
        }

        public static ImageInfo GetInfo(ReadOnlySpan<byte> data)
        {
            var es = ReadEntries(data);
            var e = es[Best(es)];
            var img = data.Slice(e.Offset, e.Size);
            var info = e.IsPng ? Png.GetInfo(img) : new ImageInfo(ImageFormat.Ico, e.Width, e.Height, 4, SampleType.UInt8, ColorModel.Rgba)
            {
                BitsPerSample = 8,
            };
            return info with { Format = ImageFormat.Ico, FrameCount = es.Count, Transfer = TransferFunction.Srgb };
        }

        public static RawImage Decode(ReadOnlySpan<byte> data, IcoDecodeOptions? options = null)
        {
            var es = ReadEntries(data);
            int i = (options ?? IcoDecodeOptions.Default).Index;
            if (i < 0) i = Best(es);
            if (i >= es.Count) throw new ArgumentOutOfRangeException(nameof(options), "ICO: no such image");
            return DecodeEntry(data, es[i]);
        }

        /// <summary>Decodes every image of the file in directory order.</summary>
        public static RawImage[] DecodeAll(ReadOnlySpan<byte> data)
        {
            var es = ReadEntries(data);
            var r = new RawImage[es.Count];
            for (int i = 0; i < r.Length; i++) r[i] = DecodeEntry(data, es[i]);
            return r;
        }

        static RawImage DecodeEntry(ReadOnlySpan<byte> data, IcoEntry e)
        {
            var src = data.Slice(e.Offset, e.Size);
            RawImage img;
            if (e.IsPng) img = Png.Decode(src);
            else
            {
                // the DIB's height counts the XOR image and the AND mask; decode the XOR part with the real height
                var dib = src.ToArray();
                if (dib.Length < 16) throw new ImageFormatException("ICO: bitmap header truncated");
                uint hs = BinaryPrimitives.ReadUInt32LittleEndian(dib);
                int w, h, bpp;
                if (hs == 12)
                {
                    w = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(4));
                    h = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(6)) / 2;
                    bpp = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(10));
                    BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(6), (ushort)h);
                }
                else
                {
                    w = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(4));
                    int h2 = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(8));
                    h = Math.Abs(h2) / 2;
                    bpp = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14));
                    BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), h2 < 0 ? -h : h);
                }
                if (w <= 0 || h <= 0) throw new ImageFormatException("ICO: bad bitmap dimensions");
                var bmp = Bmp.DecodeDib(dib, new BmpDecodeOptions { AlphaFromReservedByte = true });
                img = ToRgba8(bmp);
                // 32 bit pixels with any non-zero fourth byte carry alpha, and the mask is then ignored (as Windows does)
                bool hasAlpha = false;
                if (bpp == 32 && hs >= 40)
                {
                    long end = Math.Min(dib.Length, hs + (long)w * h * 4);
                    for (long i = hs + 3; i < end; i += 4) if (dib[i] != 0) { hasAlpha = true; break; }
                }
                if (!hasAlpha)
                {
                    // AND mask after the XOR pixels: 1 bit per pixel, rows padded to 32 bits, bottom-up; 1 = transparent
                    int palette = 0;
                    if (bpp <= 8)
                    {
                        int used = hs >= 36 ? (int)BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(32)) : 0;
                        palette = (used > 0 ? used : 1 << bpp) * (hs == 12 ? 3 : 4);
                    }
                    else if (hs == 40 && BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(16)) == 3) palette = 12; // BI_BITFIELDS masks
                    long xorStride = ((long)w * bpp + 31) / 32 * 4;
                    long maskStart = hs + palette + xorStride * h;
                    long maskStride = (w + 31) / 32 * 4;
                    var px = (byte[])img.Data;
                    for (int y = 0; y < h; y++)
                    {
                        long row = maskStart + (long)(h - 1 - y) * maskStride;
                        for (int x = 0; x < w; x++)
                        {
                            long o = row + (x >> 3);
                            bool transparent = o < dib.Length && (dib[o] >> (7 - (x & 7)) & 1) == 1;
                            px[((long)y * w + x) * 4 + 3] = transparent ? (byte)0 : (byte)255;
                        }
                    }
                }
            }
            (img.Metadata ??= new ImageMetadata()).Transfer = TransferFunction.Srgb;
            return img;
        }

        static RawImage ToRgba8(RawImage b)
        {
            if (b.SampleType == SampleType.UInt16)
            {
                var u = (ushort[])b.Data;
                var t = new byte[u.Length];
                for (int i = 0; i < u.Length; i++) t[i] = (byte)((u[i] * 255 + 32767) / 65535);
                b = new RawImage(b.Width, b.Height, b.Color, t, b.Channels);
            }
            var s = (byte[])b.Data;
            int n = b.Width * b.Height, ch = b.Channels;
            var o = new byte[n * 4];
            for (int i = 0; i < n; i++)
            {
                byte r, g, bl, a = 255;
                switch (b.Color)
                {
                    case ColorModel.Gray: r = g = bl = s[i]; break;
                    case ColorModel.GrayAlpha: r = g = bl = s[i * 2]; a = s[i * 2 + 1]; break;
                    case ColorModel.Rgb: r = s[i * 3]; g = s[i * 3 + 1]; bl = s[i * 3 + 2]; break;
                    default: r = s[i * ch]; g = s[i * ch + 1]; bl = s[i * ch + 2]; a = s[i * ch + 3]; break;
                }
                o[i * 4] = r; o[i * 4 + 1] = g; o[i * 4 + 2] = bl; o[i * 4 + 3] = a;
            }
            return new RawImage(b.Width, b.Height, ColorModel.Rgba, o, 4);
        }

        /// <summary>Writes an icon (or, with <see cref="IcoEncodeOptions.Cursor"/>, a cursor) holding the given UInt8 images, each at most 256 x 256.</summary>
        public static void Encode(IReadOnlyList<RawImage> images, Stream output, IcoEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(images);
            ArgumentNullException.ThrowIfNull(output);
            options ??= new IcoEncodeOptions();
            if (images.Count == 0 || images.Count > 65535) throw new ArgumentException("ICO: 1 to 65535 images needed");
            var blobs = new List<byte[]>();
            var bits = new List<int>();
            foreach (var im in images)
            {
                if (im.Width > 256 || im.Height > 256) throw new ArgumentException("ICO: images are at most 256 x 256");
                if (im.SampleType != SampleType.UInt8 || im.Color == ColorModel.Other) throw new ArgumentException("ICO: UInt8 Gray, GrayAlpha, Rgb or Rgba images needed");
                var rgba = ToRgba8(im);
                if (im.Width >= options.PngFromSize || im.Height >= options.PngFromSize) { blobs.Add(Png.Encode(rgba)); bits.Add(32); }
                else { blobs.Add(Dib32(rgba)); bits.Add(32); }
            }
            int n = images.Count;
            var head = new byte[6 + 16 * n];
            head[2] = (byte)(options.Cursor ? 2 : 1);
            BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(4), (ushort)n);
            long offset = head.Length;
            for (int i = 0; i < n; i++)
            {
                int e = 6 + 16 * i;
                head[e] = (byte)(images[i].Width & 255); head[e + 1] = (byte)(images[i].Height & 255);
                if (options.Cursor)
                {
                    var hs = options.Hotspots != null && i < options.Hotspots.Count ? options.Hotspots[i] : (0, 0);
                    BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(e + 4), (ushort)hs.Item1);
                    BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(e + 6), (ushort)hs.Item2);
                }
                else
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(e + 4), 1);
                    BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(e + 6), (ushort)bits[i]);
                }
                BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(e + 8), (uint)blobs[i].Length);
                BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(e + 12), (uint)offset);
                offset += blobs[i].Length;
            }
            output.Write(head);
            foreach (var b in blobs) output.Write(b);
        }

        public static byte[] Encode(IReadOnlyList<RawImage> images, IcoEncodeOptions? options = null)
        {
            var ms = new MemoryStream();
            Encode(images, ms, options);
            return ms.ToArray();
        }

        /// <summary>A 32 bit BGRA DIB, bottom-up, with the AND mask derived from alpha (transparent where alpha is 0).</summary>
        static byte[] Dib32(RawImage rgba)
        {
            int w = rgba.Width, h = rgba.Height;
            int maskStride = (w + 31) / 32 * 4;
            var o = new byte[40 + w * h * 4 + maskStride * h];
            BinaryPrimitives.WriteUInt32LittleEndian(o, 40);
            BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(4), w);
            BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(8), h * 2);
            BinaryPrimitives.WriteUInt16LittleEndian(o.AsSpan(12), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(o.AsSpan(14), 32);
            BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(20), (uint)(w * h * 4 + maskStride * h));
            var s = (byte[])rgba.Data;
            int p = 40;
            for (int y = h - 1; y >= 0; y--)
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    o[p++] = s[i + 2]; o[p++] = s[i + 1]; o[p++] = s[i]; o[p++] = s[i + 3];
                }
            for (int y = h - 1; y >= 0; y--, p += maskStride)
                for (int x = 0; x < w; x++)
                    if (s[(y * w + x) * 4 + 3] == 0) o[p + (x >> 3)] |= (byte)(0x80 >> (x & 7));
            return o;
        }
    }
}
