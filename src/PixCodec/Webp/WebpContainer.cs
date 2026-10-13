using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace PixCodec
{
    /// <summary>One image of a WebP file: the still picture or an animation frame, with the location of its chunks.</summary>
    internal sealed class WebpFrameEntry
    {
        public int X, Y, Width, Height, Duration;
        public bool NoBlend, DisposeBackground;
        public int AlphOffset, AlphLength = -1;     // payload of the ALPH chunk, length -1 if absent
        public int DataOffset, DataLength;          // payload of the VP8 / VP8L chunk
        public bool Lossless;
        /// <summary>The frame carries transparency: an ALPH chunk next to lossy data or the alpha hint of lossless data.</summary>
        public bool HasAlpha;
    }

    /// <summary>The chunk structure of a WebP file (RFC 9649 section 2).</summary>
    internal sealed class WebpContainer
    {
        public int Width, Height;
        public bool HasVp8x, Animated, AlphaFlag;
        public int IccOffset, IccLength = -1, ExifOffset, ExifLength = -1, XmpOffset, XmpLength = -1;
        public uint Background;
        public int LoopCount;
        public readonly List<WebpFrameEntry> Frames = new List<WebpFrameEntry>();

        const uint Vp8 = 0x20385056, Vp8l = 0x4c385056, Vp8x = 0x58385056, Alph = 0x48504c41, Anim = 0x4d494e41,
            Anmf = 0x464d4e41, Iccp = 0x50434349, Exif = 0x46495845, Xmp = 0x20504d58;

        static ImageFormatException Bad(string what) => new ImageFormatException("invalid WebP file: " + what);

        static int U24(ReadOnlySpan<byte> d, int o) => d[o] | d[o + 1] << 8 | d[o + 2] << 16;

        /// <summary>
        /// Walks the chunks. Chunk sizes must stay inside the RIFF size; a missing final padding byte, data after the
        /// RIFF payload and unknown chunks are tolerated. The bitstream headers are checked, the pixel data is not
        /// touched. ICCP, EXIF and XMP chunks count only when the VP8X header flags them.
        /// <para>
        /// With <paramref name="infoOnly"/> the data may be a prefix of the file: the walk stops as soon as a still
        /// image's header and every flagged ICCP / EXIF chunk have been seen, and throws when it needs bytes the
        /// prefix does not have. An animation always needs the whole file (its frames are counted).
        /// </para>
        /// </summary>
        public static WebpContainer Parse(ReadOnlySpan<byte> data, bool infoOnly = false, bool unflaggedMetadata = false)
        {
            if (!Webp.IsMatch(data)) throw Bad("no RIFF/WEBP signature");
            long riffEnd = 8L + BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4));
            bool prefix = riffEnd > data.Length;
            if (prefix && !infoOnly) throw Bad("file is truncated");
            if (riffEnd > int.MaxValue) throw new ImageNotSupportedException("WebP files beyond 2 GB are not supported");
            int end = (int)riffEnd;
            // with unflaggedMetadata the chunks count wherever they are found, also in a file without VP8X header
            bool wantIcc = unflaggedMetadata, wantExif = unflaggedMetadata, wantXmp = unflaggedMetadata;
            var c = new WebpContainer();
            int pos = 12;
            WebpFrameEntry? still = null;
            int alphOffset = 0, alphLength = -1;
            bool first = true;
            while (pos + 8 <= end)
            {
                if (infoOnly && still != null && (!wantIcc || c.IccLength >= 0) && (!wantExif || c.ExifLength >= 0)) break;
                if (pos + 8 > data.Length) throw Bad("more data needed");
                uint tag = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos));
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos + 4));
                int payload = pos + 8;
                if (size > (uint)(end - payload)) throw Bad("chunk exceeds the file");
                int len = (int)size;
                // what the walk reads of a payload: all of VP8X and EXIF, the header of a bitstream
                int need = tag == Vp8x || tag == Exif ? len : tag == Vp8 ? Math.Min(len, 10) : tag == Vp8l ? Math.Min(len, 5) : 0;
                if (payload + need > data.Length) throw Bad("more data needed");
                switch (tag)
                {
                    case Vp8x:
                        if (!first) break;
                        if (len < 10) throw Bad("VP8X chunk too short");
                        c.HasVp8x = true;
                        c.Animated = (data[payload] & 0x02) != 0;
                        c.AlphaFlag = (data[payload] & 0x10) != 0;
                        wantIcc |= (data[payload] & 0x20) != 0; wantExif |= (data[payload] & 0x08) != 0; wantXmp |= (data[payload] & 0x04) != 0;
                        if (c.Animated && prefix) throw Bad("more data needed");
                        c.Width = U24(data, payload + 4) + 1;
                        c.Height = U24(data, payload + 7) + 1;
                        break;
                    case Anim:
                        if (c.Animated && len >= 6)
                        {
                            c.Background = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(payload));
                            c.LoopCount = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(payload + 4));
                        }
                        break;
                    case Anmf:
                        if (c.Animated) c.Frames.Add(ParseFrame(data, payload, len, c));
                        break;
                    case Alph:
                        if (still == null && alphLength < 0) { alphOffset = payload; alphLength = len; }
                        break;
                    case Vp8:
                    case Vp8l:
                        if (still == null)
                        {
                            still = new WebpFrameEntry { AlphOffset = alphOffset, AlphLength = alphLength };
                            SetBitstream(still, data, tag == Vp8l, payload, len);
                        }
                        break;
                    case Iccp:
                        if (wantIcc && c.IccLength < 0) { c.IccOffset = payload; c.IccLength = len; }
                        break;
                    case Exif:
                        if (wantExif && c.ExifLength < 0) { c.ExifOffset = payload; c.ExifLength = len; }
                        break;
                    case Xmp:
                        if (wantXmp && c.XmpLength < 0) { c.XmpOffset = payload; c.XmpLength = len; }
                        break;
                }
                first = false;
                pos = payload + len + (len & 1);
            }

            if (c.Animated)
            {
                if (c.Frames.Count == 0) throw Bad("animation without frames");
            }
            else
            {
                if (still == null) throw Bad("no image data");
                // like libwebp: without the VP8X alpha flag an ALPH chunk next to a still image is not used
                if (!c.AlphaFlag && !still.Lossless) { still.AlphLength = -1; still.HasAlpha = false; }
                if (!c.HasVp8x) { c.Width = still.Width; c.Height = still.Height; }
                else if (c.Width != still.Width || c.Height != still.Height) throw Bad("canvas and image size differ");
                c.Frames.Add(still);
            }
            return c;
        }

        static void SetBitstream(WebpFrameEntry f, ReadOnlySpan<byte> data, bool lossless, int offset, int length)
        {
            f.DataOffset = offset; f.DataLength = length; f.Lossless = lossless;
            var d = data.Slice(offset, Math.Min(length, data.Length - offset));
            if (lossless)
            {
                if (!Vp8lDecoder.TryReadHeader(d, out f.Width, out f.Height, out f.HasAlpha)) throw Bad("lossless bitstream header");
                f.AlphLength = -1;      // lossless data brings its own alpha
            }
            else
            {
                if (!Vp8Decoder.TryReadHeader(d, out f.Width, out f.Height)) throw Bad("lossy bitstream header");
                f.HasAlpha = f.AlphLength >= 0;
            }
        }

        static WebpFrameEntry ParseFrame(ReadOnlySpan<byte> data, int payload, int len, WebpContainer c)
        {
            if (len < 16) throw Bad("ANMF chunk too short");
            var f = new WebpFrameEntry
            {
                X = 2 * U24(data, payload), Y = 2 * U24(data, payload + 3),
                Duration = U24(data, payload + 12),
                NoBlend = (data[payload + 15] & 2) != 0, DisposeBackground = (data[payload + 15] & 1) != 0,
            };
            int w = U24(data, payload + 6) + 1, h = U24(data, payload + 9) + 1;
            int pos = payload + 16, end = payload + len;
            bool found = false;
            while (pos + 8 <= end && !found)
            {
                uint tag = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos));
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos + 4));
                if (size > (uint)(end - pos - 8)) throw Bad("frame chunk exceeds its ANMF chunk");
                if (tag == Alph && f.AlphLength < 0) { f.AlphOffset = pos + 8; f.AlphLength = (int)size; }
                else if (tag == Vp8 || tag == Vp8l)
                {
                    SetBitstream(f, data, tag == Vp8l, pos + 8, (int)size);
                    found = true;
                }
                pos += 8 + (int)size + (int)(size & 1);
            }
            if (!found) throw Bad("animation frame without image data");
            if (f.Width != w || f.Height != h) throw Bad("frame and bitstream size differ");
            if ((long)f.X + w > c.Width || (long)f.Y + h > c.Height) throw Bad("frame exceeds the canvas");
            return f;
        }
    }
}
