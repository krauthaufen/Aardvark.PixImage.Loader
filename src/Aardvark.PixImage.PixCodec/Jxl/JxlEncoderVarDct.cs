using System;

namespace Aardvark.ImageCodecs.JpegXl
{
    /// <summary>Lossy VarDCT frames in XYB.</summary>
    internal static partial class JxlVarDctEncoder
    {
        public static void EncodeFrame(JxlBitWriter cs, RawImage img, JxlImageHeader h, JxlFrameHeader fh, JxlEncodeOptions opt)
            => throw new ImageNotSupportedException("JPEG XL: lossy encoding is not available yet");
    }
}
