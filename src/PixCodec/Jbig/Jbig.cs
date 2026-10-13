using System;

namespace PixCodec
{
    /// <summary>JBIG1 (ITU-T T.82) bi-level image entities, as used inside TIFF (compression 34661) and fax.</summary>
    public static class Jbig
    {
        /// <summary>
        /// Decodes a complete bi-level image entity (BIE) to a UInt8 Gray image with 0 = black (foreground, bit 1)
        /// and 255 = white (background, bit 0).
        /// </summary>
        public static RawImage Decode(ReadOnlySpan<byte> bie) => throw new ImageNotSupportedException("JBIG is not implemented yet");
    }
}
