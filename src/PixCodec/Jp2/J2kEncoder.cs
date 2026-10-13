using System;
using System.IO;

namespace PixCodec
{
    internal static class J2kEncoder
    {
        public static void EncodeImage(RawImage image, Stream output, Jp2EncodeOptions o) => throw new ImageNotSupportedException("JPEG 2000 encoding is not implemented yet");
        public static byte[] EncodePlanes(J2kPlanes planes, Jp2EncodeOptions o) => throw new ImageNotSupportedException("JPEG 2000 encoding is not implemented yet");
    }
}
