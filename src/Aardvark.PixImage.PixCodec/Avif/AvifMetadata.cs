using System;

namespace Aardvark.ImageCodecs.Heif
{
    /// <summary>Collects the Exif / XMP items and the ICC profile of an AVIF file for the metadata reader.</summary>
    internal static class AvifMetadata
    {
        public static void Scan(ReadOnlySpan<byte> d, MetadataBlocks b)
        {
            AvifReader rd;
            try { rd = new AvifReader(d, AvifDecodeOptions.Default); }
            catch (Exception e) when (e is ImageFormatException or ImageNotSupportedException) { return; }
            var primary = rd.Primary;
            var (exif, xmp) = rd.MetadataItems(primary);
            if (exif != null) b.Exif ??= exif;
            if (xmp != null) b.Xmp.Add(xmp);
            if (primary != null)
            {
                var c = rd.Colour(primary);
                if (c.Icc != null) b.Icc ??= c.Icc;
            }
            foreach (var id in rd.File.ItemOrder)
                if (rd.File.Items[id].Type == "tmap") b.GainMap = true;
        }
    }
}
