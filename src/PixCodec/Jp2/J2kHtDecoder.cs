using System;
using System.Numerics;

namespace PixCodec
{
    internal sealed class J2kHtScratch
    {
    }

    internal static unsafe class J2kHtDecoder
    {
        public static bool Decode<T>(J2kBlockCoder coder, T* data, int w, int h, int cupStart, int lcup, int refStart, int lref,
            int zblk, int p, bool causal) where T : unmanaged, IBinaryInteger<T>
            => throw new ImageNotSupportedException("HTJ2K is not implemented yet");
    }
}
