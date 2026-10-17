using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Aardvark.ImageCodecs.JpegXrImpl
{
    /// <summary>A PIXEL_FORMAT of Table A.6.</summary>
    internal sealed record JxrPixelFormat(byte Id, string Name, int Channels, bool Alpha, JxrBitDepth BitDepth, JxrOutputFormat Format, bool Premultiplied = false)
    {
        /// <summary>The 16 bytes of a PIXEL_FORMAT value as stored: 24C3DD6F034EFE4BB1853D77768DC9 and the id byte.</summary>
        public static readonly byte[] Prefix = { 0x24, 0xC3, 0xDD, 0x6F, 0x03, 0x4E, 0xFE, 0x4B, 0xB1, 0x85, 0x3D, 0x77, 0x76, 0x8D, 0xC9 };

        public byte[] Guid()
        {
            var g = new byte[16];
            Prefix.CopyTo(g, 0);
            g[15] = Id;
            return g;
        }

        static readonly Dictionary<byte, JxrPixelFormat> ById = new();
        static readonly Dictionary<string, JxrPixelFormat> ByName = new(StringComparer.OrdinalIgnoreCase);

        static JxrPixelFormat()
        {
            void A(byte id, string name, int nc, bool alpha, JxrBitDepth bd, JxrOutputFormat f, bool pre = false)
            {
                var p = new JxrPixelFormat(id, name, nc, alpha, bd, f, pre);
                ById[id] = p;
                ByName[name] = p;
            }
            const JxrBitDepth B8 = JxrBitDepth.Bd8, B16 = JxrBitDepth.Bd16, B16S = JxrBitDepth.Bd16S, B16F = JxrBitDepth.Bd16F,
                B32S = JxrBitDepth.Bd32S, B32F = JxrBitDepth.Bd32F, B10 = JxrBitDepth.Bd10;
            const JxrOutputFormat Rgb = JxrOutputFormat.Rgb, Cmyk = JxrOutputFormat.Cmyk, Nc = JxrOutputFormat.NComponent,
                Y = JxrOutputFormat.YOnly, Cd = JxrOutputFormat.CmykDirect;
            A(0x0D, "24bppRGB", 3, false, B8, Rgb);
            A(0x0C, "24bppBGR", 3, false, B8, Rgb);
            A(0x0E, "32bppBGR", 3, false, B8, Rgb);
            A(0x15, "48bppRGB", 3, false, B16, Rgb);
            A(0x12, "48bppRGBFixedPoint", 3, false, B16S, Rgb);
            A(0x3B, "48bppRGBHalf", 3, false, B16F, Rgb);
            A(0x18, "96bppRGBFixedPoint", 3, false, B32S, Rgb);
            A(0x40, "64bppRGBFixedPoint", 3, false, B16S, Rgb);
            A(0x42, "64bppRGBHalf", 3, false, B16F, Rgb);
            A(0x41, "128bppRGBFixedPoint", 3, false, B32S, Rgb);
            A(0x1B, "128bppRGBFloat", 3, false, B32F, Rgb);
            A(0x0F, "32bppBGRA", 4, true, B8, Rgb);
            A(0x16, "64bppRGBA", 4, true, B16, Rgb);
            A(0x1D, "64bppRGBAFixedPoint", 4, true, B16S, Rgb);
            A(0x3A, "64bppRGBAHalf", 4, true, B16F, Rgb);
            A(0x1E, "128bppRGBAFixedPoint", 4, true, B32S, Rgb);
            A(0x19, "128bppRGBAFloat", 4, true, B32F, Rgb);
            A(0x10, "32bppPBGRA", 4, true, B8, Rgb, true);
            A(0x17, "64bppPRGBA", 4, true, B16, Rgb, true);
            A(0x1A, "128bppPRGBAFloat", 4, true, B32F, Rgb, true);
            A(0x1C, "32bppCMYK", 4, false, B8, Cmyk);
            A(0x2C, "40bppCMYKAlpha", 5, true, B8, Cmyk);
            A(0x1F, "64bppCMYK", 4, false, B16, Cmyk);
            A(0x2D, "80bppCMYKAlpha", 5, true, B16, Cmyk);
            for (int n = 3; n <= 8; n++)
            {
                A((byte)(0x20 + n - 3), $"{8 * n}bpp{n}Channels", n, false, B8, Nc);
                A((byte)(0x2E + n - 3), $"{8 * (n + 1)}bpp{n}ChannelsAlpha", n + 1, true, B8, Nc);
                A((byte)(0x26 + n - 3), $"{16 * n}bpp{n}Channels", n, false, B16, Nc);
                A((byte)(0x34 + n - 3), $"{16 * (n + 1)}bpp{n}ChannelsAlpha", n + 1, true, B16, Nc);
            }
            A(0x08, "8bppGray", 1, false, B8, Y);
            A(0x0B, "16bppGray", 1, false, B16, Y);
            A(0x13, "16bppGrayFixedPoint", 1, false, B16S, Y);
            A(0x3E, "16bppGrayHalf", 1, false, B16F, Y);
            A(0x3F, "32bppGrayFixedPoint", 1, false, B32S, Y);
            A(0x11, "32bppGrayFloat", 1, false, B32F, Y);
            A(0x05, "BlackWhite", 1, false, JxrBitDepth.Bd1White1, Y);
            A(0x09, "16bppBGR555", 3, false, JxrBitDepth.Bd5, Rgb);
            A(0x0A, "16bppBGR565", 3, false, JxrBitDepth.Bd565, Rgb);
            A(0x14, "32bppBGR101010", 3, false, B10, Rgb);
            A(0x3D, "32bppRGBE", 3, false, B8, JxrOutputFormat.Rgbe);
            A(0x54, "32bppCMYKDIRECT", 4, false, B8, Cd);
            A(0x55, "64bppCMYKDIRECT", 4, false, B16, Cd);
            A(0x56, "40bppCMYKDIRECTAlpha", 5, true, B8, Cd);
            A(0x43, "80bppCMYKDIRECTAlpha", 5, true, B16, Cd);
            A(0x44, "12bppYCC420", 3, false, B8, JxrOutputFormat.Yuv420);
            A(0x45, "16bppYCC422", 3, false, B8, JxrOutputFormat.Yuv422);
            A(0x46, "20bppYCC422", 3, false, B10, JxrOutputFormat.Yuv422);
            A(0x47, "32bppYCC422", 3, false, B16, JxrOutputFormat.Yuv422);
            A(0x48, "24bppYCC444", 3, false, B8, JxrOutputFormat.Yuv444);
            A(0x49, "30bppYCC444", 3, false, B10, JxrOutputFormat.Yuv444);
            A(0x4A, "48bppYCC444", 3, false, B16, JxrOutputFormat.Yuv444);
            A(0x4B, "48bppYCC444FixedPoint", 3, false, B16S, JxrOutputFormat.Yuv444);
            A(0x4C, "20bppYCC420Alpha", 4, true, B8, JxrOutputFormat.Yuv420);
            A(0x4D, "24bppYCC422Alpha", 4, true, B8, JxrOutputFormat.Yuv422);
            A(0x4E, "30bppYCC422Alpha", 4, true, B10, JxrOutputFormat.Yuv422);
            A(0x4F, "48bppYCC422Alpha", 4, true, B16, JxrOutputFormat.Yuv422);
            A(0x50, "32bppYCC444Alpha", 4, true, B8, JxrOutputFormat.Yuv444);
            A(0x51, "40bppYCC444Alpha", 4, true, B10, JxrOutputFormat.Yuv444);
            A(0x52, "64bppYCC444Alpha", 4, true, B16, JxrOutputFormat.Yuv444);
            A(0x53, "64bppYCC444AlphaFixedPoint", 4, true, B16S, JxrOutputFormat.Yuv444);
        }

        public static JxrPixelFormat? FromGuid(ReadOnlySpan<byte> g)
            => g.Length == 16 && g.Slice(0, 15).SequenceEqual(Prefix) && ById.TryGetValue(g[15], out var p) ? p : null;

        public static JxrPixelFormat Get(string name) => ByName[name];

        public static IEnumerable<JxrPixelFormat> All => ById.Values;
    }

    /// <summary>The tag based file format of T.832 Annex A: FILE_HEADER( ) and the first IMAGE_FILE_DIRECTORY( ).</summary>
    internal sealed class JxrContainer
    {
        public byte[]? PixelFormatGuid;
        public JxrPixelFormat? PixelFormat;
        public long Width, Height;
        public long ImageOffset, ImageByteCount;
        public long AlphaOffset = -1, AlphaByteCount;
        public double DpiX, DpiY;
        public int SpatialXfrm;
        public int ColorSpace = -1;
        public byte[]? Icc, Xmp, Iptc;
        public long ExifIfd, GpsIfd;
        public int ImageBandPresence = -1, AlphaBandPresence = -1;
        public byte[]? PtmColorInfo;
        /// <summary>The IFD entries as (tag, type, count, value or offset).</summary>
        public readonly List<(int tag, int type, long count, long value)> Entries = new();

        public static bool IsMatch(ReadOnlySpan<byte> h) => h.Length >= 4 && h[0] == 'I' && h[1] == 'I' && h[2] == 0xBC && h[3] <= 1;

        public static bool IsCodestream(ReadOnlySpan<byte> h) => h.Length >= 8 && h.Slice(0, 8).SequenceEqual("WMPHOTO\0"u8);

        static int TypeSize(int t) => t switch { 1 or 2 or 6 or 7 => 1, 3 or 8 => 2, 4 or 9 or 11 => 4, 5 or 10 or 12 => 8, _ => 0 };

        public static JxrContainer Parse(ReadOnlySpan<byte> d)
        {
            if (!IsMatch(d) || d.Length < 8) throw new ImageFormatException("not a JPEG XR file");
            var c = new JxrContainer();
            long ifd = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(4));
            if (ifd + 2 > d.Length) throw new ImageFormatException("JPEG XR: image file directory outside the file");
            int n = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice((int)ifd));
            if (ifd + 2 + 12L * n > d.Length) throw new ImageFormatException("JPEG XR: image file directory truncated");
            for (int i = 0; i < n; i++)
            {
                var e = d.Slice((int)ifd + 2 + 12 * i, 12);
                int tag = BinaryPrimitives.ReadUInt16LittleEndian(e);
                int type = BinaryPrimitives.ReadUInt16LittleEndian(e.Slice(2));
                long count = BinaryPrimitives.ReadUInt32LittleEndian(e.Slice(4));
                long value = BinaryPrimitives.ReadUInt32LittleEndian(e.Slice(8));
                c.Entries.Add((tag, type, count, value));
                int size = TypeSize(type);
                long bytes = size * count;
                ReadOnlySpan<byte> data;
                if (bytes <= 4) data = e.Slice(8, (int)bytes);
                else if (value + bytes <= d.Length && bytes < int.MaxValue) data = d.Slice((int)value, (int)bytes);
                else continue;   // broken entry
                if (count == 0) continue;
                long num = type switch
                {
                    1 => data[0],
                    3 => BinaryPrimitives.ReadUInt16LittleEndian(data),
                    4 => BinaryPrimitives.ReadUInt32LittleEndian(data),
                    _ => 0,
                };
                switch (tag)
                {
                    case 0xBC01: if (bytes == 16) { c.PixelFormatGuid = data.ToArray(); c.PixelFormat = JxrPixelFormat.FromGuid(data); } break;
                    case 0xBC02: c.SpatialXfrm = (int)num; break;
                    case 0xBC05: c.PtmColorInfo = data.ToArray(); break;
                    case 0xBC80: c.Width = num; break;
                    case 0xBC81: c.Height = num; break;
                    case 0xBC82: if (type == 11) c.DpiX = BinaryPrimitives.ReadSingleLittleEndian(data); break;
                    case 0xBC83: if (type == 11) c.DpiY = BinaryPrimitives.ReadSingleLittleEndian(data); break;
                    case 0xBCC0: c.ImageOffset = num; break;
                    case 0xBCC1: c.ImageByteCount = num; break;
                    case 0xBCC2: c.AlphaOffset = num; break;
                    case 0xBCC3: c.AlphaByteCount = num; break;
                    case 0xBCC4: c.ImageBandPresence = (int)num; break;
                    case 0xBCC5: c.AlphaBandPresence = (int)num; break;
                    case 0xA001: c.ColorSpace = (int)num; break;
                    case 0x8773: c.Icc = data.ToArray(); break;
                    case 0x02BC: c.Xmp = data.ToArray(); break;
                    case 0x83BB: c.Iptc = data.ToArray(); break;
                    case 0x8769: c.ExifIfd = num; break;
                    case 0x8825: c.GpsIfd = num; break;
                }
            }
            if (c.PixelFormatGuid == null) throw new ImageFormatException("JPEG XR: no PIXEL_FORMAT");
            if (c.ImageOffset <= 0 || c.ImageOffset >= d.Length) throw new ImageFormatException("JPEG XR: IMAGE_OFFSET outside the file");
            if (c.SpatialXfrm is < 0 or > 7) c.SpatialXfrm = 0;
            return c;
        }

        /// <summary>The CODED_IMAGE( ) of the primary planes (IMAGE_BYTE_COUNT 0 runs to the end of the file).</summary>
        public ReadOnlySpan<byte> Image(ReadOnlySpan<byte> d)
        {
            long len = ImageByteCount == 0 ? d.Length - ImageOffset : Math.Min(ImageByteCount, d.Length - ImageOffset);
            return d.Slice((int)ImageOffset, (int)len);
        }

        public ReadOnlySpan<byte> Alpha(ReadOnlySpan<byte> d)
        {
            if (AlphaOffset <= 0 || AlphaOffset >= d.Length) throw new ImageFormatException("JPEG XR: ALPHA_OFFSET outside the file");
            long len = AlphaByteCount == 0 ? d.Length - AlphaOffset : Math.Min(AlphaByteCount, d.Length - AlphaOffset);
            return d.Slice((int)AlphaOffset, (int)len);
        }

        /// <summary>SPATIAL_XFRM values 0 .. 7 as TIFF / EXIF orientations (NOTE to 8.3.8).</summary>
        public static int Orientation(int xfrm) => xfrm switch { 1 => 4, 2 => 2, 3 => 3, 4 => 6, 5 => 7, 6 => 5, 7 => 8, _ => 1 };
    }
}
