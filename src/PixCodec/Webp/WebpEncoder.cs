using System;
using System.Collections.Generic;
using System.IO;

namespace PixCodec
{
    internal static class WebpEncoder
    {
        public static void EncodeStill(RawImage image, Stream output, WebpEncodeOptions opt) => throw new ImageNotSupportedException("todo");
        public static void EncodeAnimation(IEnumerable<WebpInputFrame> frames, Stream output, WebpEncodeOptions opt) => throw new ImageNotSupportedException("todo");
    }
}
