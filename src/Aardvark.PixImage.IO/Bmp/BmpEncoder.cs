using System;
using System.Buffers.Binary;
using System.IO;

namespace Aardvark.ImageCodecs.BmpImpl
{
    internal static class BmpEncoder
    {
        public static void Encode(RawImage img, Stream output, BmpEncodeOptions opt)
        {
            if (img.SampleType != SampleType.UInt8)
                throw new ImageNotSupportedException($"BMP: only UInt8 samples can be written, not {img.SampleType}");
            if (img.Color == ColorModel.Other)
                throw new ImageNotSupportedException("BMP: only Gray, GrayAlpha, Rgb and Rgba can be written");
            int w = img.Width, h = img.Height, ch = img.Channels;
            bool alpha = img.HasAlpha, gray = img.Color == ColorModel.Gray;
            int bpp = gray ? 8 : alpha ? 32 : 24;
            var meta = opt.WriteMetadata ? img.Metadata : null;
            byte[]? icc = meta?.IccProfile is { Length: > 0 } p ? p : null;
            // BITMAPINFOHEADER where it suffices; V4 for the alpha mask, V5 for an ICC profile
            int infoSize = icc != null ? 124 : alpha ? 108 : 40;
            int paletteBytes = gray ? 256 * 4 : 0;
            long stride = ((long)w * bpp + 31) / 32 * 4;
            long imageBytes = stride * h;
            long pixelOffset = 14 + infoSize + paletteBytes;
            long fileSize = pixelOffset + imageBytes + (icc?.Length ?? 0);
            if (fileSize > uint.MaxValue) throw new ImageNotSupportedException("BMP: file would exceed 4 GB");

            var head = new byte[pixelOffset];
            var s = head.AsSpan();
            s[0] = (byte)'B'; s[1] = (byte)'M';
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(2), (uint)fileSize);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(10), (uint)pixelOffset);
            var info = s.Slice(14, infoSize);
            BinaryPrimitives.WriteUInt32LittleEndian(info, (uint)infoSize);
            BinaryPrimitives.WriteInt32LittleEndian(info.Slice(4), w);
            BinaryPrimitives.WriteInt32LittleEndian(info.Slice(8), opt.TopDown ? -h : h);
            BinaryPrimitives.WriteUInt16LittleEndian(info.Slice(12), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(info.Slice(14), (ushort)bpp);
            BinaryPrimitives.WriteUInt32LittleEndian(info.Slice(16), alpha ? 3u : 0u);   // BI_BITFIELDS / BI_RGB
            BinaryPrimitives.WriteUInt32LittleEndian(info.Slice(20), imageBytes <= uint.MaxValue ? (uint)imageBytes : 0);
            BinaryPrimitives.WriteUInt32LittleEndian(info.Slice(24), Ppm(meta?.DpiX ?? 0));
            BinaryPrimitives.WriteUInt32LittleEndian(info.Slice(28), Ppm(meta?.DpiY ?? 0));
            BinaryPrimitives.WriteUInt32LittleEndian(info.Slice(32), gray ? 256u : 0u);
            if (infoSize >= 108)
            {
                if (alpha)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(info.Slice(40), 0x00FF0000);
                    BinaryPrimitives.WriteUInt32LittleEndian(info.Slice(44), 0x0000FF00);
                    BinaryPrimitives.WriteUInt32LittleEndian(info.Slice(48), 0x000000FF);
                    BinaryPrimitives.WriteUInt32LittleEndian(info.Slice(52), 0xFF000000);
                }
                // colour space: 'sRGB', or 'MBED' with the profile after the pixels
                BinaryPrimitives.WriteUInt32LittleEndian(info.Slice(56), icc != null ? 0x4D424544u : 0x73524742u);
                if (icc != null)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(info.Slice(108), 4);   // LCS_GM_IMAGES
                    BinaryPrimitives.WriteUInt32LittleEndian(info.Slice(112), (uint)(infoSize + paletteBytes + imageBytes));
                    BinaryPrimitives.WriteUInt32LittleEndian(info.Slice(116), (uint)icc.Length);
                }
            }
            if (gray)
                for (int i = 0; i < 256; i++)
                {
                    int e = 14 + infoSize + i * 4;
                    head[e] = head[e + 1] = head[e + 2] = (byte)i;
                }
            output.Write(head);

            var src = img.GetData<byte>();
            var row = new byte[stride];
            for (int r = 0; r < h; r++)
            {
                int y = opt.TopDown ? r : h - 1 - r;
                var line = src.AsSpan(y * w * ch, w * ch);
                switch (img.Color)
                {
                    case ColorModel.Gray: line.CopyTo(row); break;
                    case ColorModel.Rgb:
                        for (int x = 0; x < w; x++) { row[x * 3] = line[x * 3 + 2]; row[x * 3 + 1] = line[x * 3 + 1]; row[x * 3 + 2] = line[x * 3]; }
                        break;
                    case ColorModel.Rgba:
                        for (int x = 0; x < w; x++)
                        {
                            row[x * 4] = line[x * 4 + 2]; row[x * 4 + 1] = line[x * 4 + 1]; row[x * 4 + 2] = line[x * 4]; row[x * 4 + 3] = line[x * 4 + 3];
                        }
                        break;
                    case ColorModel.GrayAlpha:
                        for (int x = 0; x < w; x++)
                        {
                            row[x * 4] = row[x * 4 + 1] = row[x * 4 + 2] = line[x * 2]; row[x * 4 + 3] = line[x * 2 + 1];
                        }
                        break;
                }
                output.Write(row);
            }
            if (icc != null) output.Write(icc);
        }

        static uint Ppm(double dpi) => dpi > 0 && dpi < 1e6 ? (uint)Math.Round(dpi / 0.0254) : 0;
    }
}
