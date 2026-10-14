using System;
using System.Buffers.Binary;

namespace PixCodec
{
    /// <summary>The Orientation tag (0x0112) in IFD0 of an EXIF blob (a standalone TIFF structure).</summary>
    internal static class ExifTags
    {
        /// <summary>Byte offset of the tag's value in the blob, or -1 when the tag is absent, malformed or the blob is damaged.</summary>
        static int ValueOffset(ReadOnlySpan<byte> exif, out bool little)
        {
            little = false;
            if (exif.Length < 8) return -1;
            if (exif[0] == 'I' && exif[1] == 'I') little = true;
            else if (exif[0] != 'M' || exif[1] != 'M') return -1;
            if (U16(exif, 2, little) != 42) return -1;
            uint ifd = U32(exif, 4, little);
            if (ifd > (uint)exif.Length - 2) return -1;
            int count = U16(exif, (int)ifd, little);
            long entry = ifd + 2;
            for (int i = 0; i < count && entry + 12 <= exif.Length; i++, entry += 12)
            {
                if (U16(exif, (int)entry, little) != 0x0112) continue;
                if (U16(exif, (int)entry + 2, little) != 3 || U32(exif, (int)entry + 4, little) != 1) return -1;
                return (int)entry + 8;
            }
            return -1;
        }

        /// <summary>The orientation 1..8, 0 if absent or malformed.</summary>
        public static int Read(ReadOnlySpan<byte> exif)
        {
            int at = ValueOffset(exif, out bool little);
            if (at < 0) return 0;
            int v = U16(exif, at, little);
            return v is >= 1 and <= 8 ? v : 0;
        }

        /// <summary>
        /// The blob to write for the metadata: its EXIF blob with the Orientation tag set to <see cref="ImageMetadata.Orientation"/>
        /// (a copy, the metadata stays untouched), or a minimal blob holding just the orientation when there is no EXIF blob.
        /// Null when there is nothing to write.
        /// </summary>
        public static byte[]? ForWriting(ImageMetadata? meta)
        {
            if (meta == null) return null;
            int o = meta.Orientation;
            if (meta.Exif is { Length: > 0 } exif)
                return o is >= 1 and <= 8 ? WithOrientation(exif, o) : exif;
            if (o is >= 1 and <= 8)
                // minimal little endian TIFF structure holding just the Orientation tag
                return [(byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0, 1, 0, 0x12, 0x01, 3, 0, 1, 0, 0, 0, (byte)o, 0, 0, 0, 0, 0, 0, 0];
            return null;
        }

        /// <summary>
        /// The blob with its Orientation tag set to <paramref name="orientation"/>: the blob itself when it already says so or has no
        /// such tag (an absent tag means 1), else a patched copy.
        /// </summary>
        public static byte[] WithOrientation(byte[] exif, int orientation)
        {
            int at = ValueOffset(exif, out bool little);
            if (at < 0 || U16(exif, at, little) == orientation) return exif;
            var r = (byte[])exif.Clone();
            if (little) BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(at), (ushort)orientation);
            else BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(at), (ushort)orientation);
            return r;
        }

        static readonly System.Text.RegularExpressions.Regex XmpOrientation = new(
            @"(tiff:Orientation\s*=\s*[""'])\s*\d\s*([""'])|(<tiff:Orientation>)\s*\d\s*(</tiff:Orientation>)",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>The XMP packet with its tiff:Orientation (if any) set to <paramref name="orientation"/>.</summary>
        public static byte[] XmpWithOrientation(byte[] xmp, int orientation)
        {
            var text = System.Text.Encoding.UTF8.GetString(xmp);
            var r = XmpOrientation.Replace(text, m => m.Groups[1].Success
                ? m.Groups[1].Value + orientation + m.Groups[2].Value
                : m.Groups[3].Value + orientation + m.Groups[4].Value);
            return ReferenceEquals(r, text) || r == text ? xmp : System.Text.Encoding.UTF8.GetBytes(r);
        }

        /// <summary>The XMP packet to write: <see cref="ImageMetadata.Xmp"/> with tiff:Orientation set to the metadata's orientation.</summary>
        public static byte[]? XmpForWriting(ImageMetadata? meta)
        {
            if (meta?.Xmp is not { Length: > 0 } xmp) return null;
            return meta.Orientation is >= 1 and <= 8 ? XmpWithOrientation(xmp, meta.Orientation) : xmp;
        }

        /// <summary>
        /// Describes the decoded result when the decoder turns the image upright: size and resolution as returned, OrientationApplied set.
        /// </summary>
        public static ImageInfo Upright(ImageInfo info)
        {
            if (info.Orientation < 2) return info;
            if (info.Orientation >= 5)
                info = info with { Width = info.Height, Height = info.Width, DpiX = info.DpiY, DpiY = info.DpiX };
            return info with { OrientationApplied = true };
        }

        /// <summary>
        /// The rectangle of the stored pixels (<paramref name="storedWidth"/> x <paramref name="storedHeight"/>) that becomes the given
        /// rectangle of the upright image once orientation <paramref name="orientation"/> is applied. The rectangle must lie inside the
        /// upright image.
        /// </summary>
        public static (int X, int Y, int Width, int Height) StoredRegion(int orientation, int storedWidth, int storedHeight, (int X, int Y, int Width, int Height) r)
        {
            if (orientation is < 2 or > 8) return r;
            var (ax, ay) = Source(r.X, r.Y);
            var (bx, by) = Source(r.X + r.Width - 1, r.Y + r.Height - 1);
            return (Math.Min(ax, bx), Math.Min(ay, by), Math.Abs(bx - ax) + 1, Math.Abs(by - ay) + 1);

            // stored position of upright pixel (x, y), as ImageOps.ApplyOrientation maps it
            (int, int) Source(int x, int y)
            {
                int w1 = storedWidth - 1, h1 = storedHeight - 1;
                return orientation switch
                {
                    2 => (w1 - x, y),
                    3 => (w1 - x, h1 - y),
                    4 => (x, h1 - y),
                    5 => (y, x),
                    6 => (y, h1 - x),
                    7 => (w1 - y, h1 - x),
                    _ => (w1 - y, x),
                };
            }
        }

        static int U16(ReadOnlySpan<byte> s, int o, bool le)
            => le ? BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(o)) : BinaryPrimitives.ReadUInt16BigEndian(s.Slice(o));
        static uint U32(ReadOnlySpan<byte> s, int o, bool le)
            => le ? BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(o)) : BinaryPrimitives.ReadUInt32BigEndian(s.Slice(o));
    }
}
