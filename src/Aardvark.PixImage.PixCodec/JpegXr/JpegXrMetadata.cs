using System;

namespace Aardvark.ImageCodecs.JpegXrImpl
{
    /// <summary>EXIF of a JPEG XR file: its IFD carries the EXIF and GPS pointers like a TIFF directory (Annex A, NOTE 3).</summary>
    internal static unsafe class JxrMetadata
    {
        /// <summary>A standalone EXIF blob (little TIFF) of the descriptive tags and the Exif / GPS directories, or null.</summary>
        public static byte[]? Exif(ReadOnlySpan<byte> d)
        {
            try
            {
                fixed (byte* p = d)
                {
                    var file = new TiffFile(new TiffMemorySource(p, d.Length));
                    return TiffExif.Extract(file, file.FirstIfd);
                }
            }
            catch (ImageFormatException) { return null; }
        }
    }
}
