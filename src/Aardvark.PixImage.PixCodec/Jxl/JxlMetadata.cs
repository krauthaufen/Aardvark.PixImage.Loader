using System;

namespace Aardvark.ImageCodecs.JpegXl
{
    /// <summary>Collects EXIF, XMP and the ICC profile of a JPEG XL file for the metadata reader.</summary>
    internal static class JxlMetadata
    {
        public static void Scan(ReadOnlySpan<byte> d, MetadataBlocks b)
        {
            JxlContainer c;
            try { c = JxlContainer.Parse(d); }
            catch (ImageFormatException) { return; }
            if (c.IsContainer)
            {
                foreach (var e in c.Metadata(d, "Exif"))
                {
                    var t = JxlContainer.ExifTiff(e);
                    if (t != null) { b.Exif ??= t; break; }
                }
                foreach (var x in c.Metadata(d, "xml ")) b.Xmp.Add(x);
                foreach (var box in c.Boxes)
                    if (box.Type == "jhgm" || box.Type == "brob" && box.Length >= 4 && d.Slice(box.Offset, 4).SequenceEqual("jhgm"u8)) b.GainMap = true;
            }
            try
            {
                var cs = c.CodestreamPrefix(d, int.MaxValue);
                var br = new JxlBitReader(cs);
                var h = JxlImageHeader.Read(br);
                if (h.Colour.WantIcc) b.Icc ??= JxlIcc.Read(br);
            }
            catch (Exception e) when (e is ImageFormatException or ImageNotSupportedException) { }
        }
    }
}
