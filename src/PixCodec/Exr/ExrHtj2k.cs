using System;

namespace PixCodec
{
    /// <summary>
    /// HTJ2K compression (OpenEXR 3.4): placeholder until the JPEG 2000 codec provides its codestream entry points.
    /// </summary>
    internal static class ExrHtj2k
    {
        public static void Decode(ReadOnlySpan<byte> src, Span<byte> dst, in ExrChunk c, ExrScratch s)
            => throw new ImageNotSupportedException("EXR HTJ2K compression is not supported yet");

        public static byte[] Encode(ReadOnlySpan<byte> raw, in ExrChunk c, ExrScratch s)
            => throw new ImageNotSupportedException("EXR HTJ2K compression is not supported yet");
    }
}
