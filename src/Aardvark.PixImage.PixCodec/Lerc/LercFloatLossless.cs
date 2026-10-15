using System;
using System.Runtime.CompilerServices;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Aardvark.ImageCodecs
{
    /// <summary>
    /// The lossless floating point mode of LERC2 v6. All pixels (valid or not) form a grid of 32 or 64 bit patterns:
    /// rows x cols for one value per pixel, (rows * cols) x depth otherwise. For float the pattern is rearranged to
    /// exponent, sign, mantissa. A predictor replaces each pattern by its difference to the left neighbour (1) and
    /// then also to the one above (2); the differences are taken separately for the mantissa and for the
    /// exponent+sign field. The result is split into byte planes, each plane is differenced bytewise 0..5 more times
    /// and stored on its own: Huffman coded, as one repeated byte, raw, or run length coded.
    /// </summary>
    internal static unsafe class LercFloatLossless
    {
        const int PlaneHuffman = 0, PlaneConstant = 1, PlaneRaw = 2, PlaneRuns = 3;
        const int MaxDelta = 5;

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Decode<T>(ReadOnlySpan<byte> blob, int pos, in Lerc2Header h, Span<T> dst, int stride, int chan, byte[]? valid)
            where T : unmanaged
        {
            int unit = sizeof(T), depth = h.Depth, pixels = h.Rows * h.Cols;
            int n = checked(pixels * depth);
            if (pos >= blob.Length) throw new ImageFormatException("LERC: pixel data is missing");
            int predictor = blob[pos++];
            if (predictor > 2) throw new ImageFormatException("LERC: unknown floating point predictor");
            var stats = LercStats.Current;
            if (stats != null) stats.FloatPredictors[predictor]++;

            // the bit patterns are assembled in place when the band fills the destination, in a scratch array otherwise
            bool inPlace = stride == depth;
            Span<T> work = inPlace ? dst.Slice(0, n) : new T[n];
            Span<byte> workBytes = MemoryMarshal.AsBytes(work);
            byte[] plane = new byte[n];
            int seen = 0;
            for (int k = 0; k < unit; k++)
            {
                if (blob.Length - pos < 6) throw new ImageFormatException("LERC: byte plane header is truncated");
                int index = blob[pos], delta = blob[pos + 1];
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(pos + 2));
                pos += 6;
                if (index >= unit || (seen >> index & 1) != 0 || delta > MaxDelta) throw new ImageFormatException("LERC: corrupt byte plane header");
                if (size == 0 || size > (uint)(blob.Length - pos)) throw new ImageFormatException("LERC: byte plane is truncated");
                seen |= 1 << index;
                if (stats != null) { stats.PlaneDeltas[delta]++; if (blob[pos] < 4) stats.PlaneCodings[blob[pos]]++; }
                ReadPlane(blob.Slice(pos, (int)size), plane, h.Version);
                pos += (int)size;

                fixed (byte* p = plane)
                fixed (byte* w = workBytes)
                {
                    // level L of the bytewise differencing left the first L bytes alone
                    for (int level = delta; level >= 1; level--)
                        for (int i = level; i < n; i++) p[i] += p[i - 1];
                    byte* o = w + index;
                    for (int i = 0; i < n; i++, o += unit) *o = p[i];
                }
            }

            int width = depth == 1 ? h.Cols : depth, height = depth == 1 ? h.Rows : pixels;
            if (unit == 4) Restore32(MemoryMarshal.Cast<byte, uint>(workBytes), width, height, predictor);
            else Restore64(MemoryMarshal.Cast<byte, ulong>(workBytes), width, height, predictor);

            if (!inPlace)
                for (int k = 0, di = chan, si = 0; k < pixels; k++, di += stride, si += depth)
                    work.Slice(si, depth).CopyTo(dst.Slice(di, depth));
            if (valid != null)
                for (int k = 0, di = chan; k < pixels; k++, di += stride)
                    if (valid[k] == 0) dst.Slice(di, depth).Clear();
        }

        static void ReadPlane(ReadOnlySpan<byte> src, Span<byte> plane, int lercVersion)
        {
            int n = plane.Length;
            switch (src[0])
            {
                case PlaneHuffman:
                {
                    int pos = 1;
                    var table = LercHuffman.ReadTable(src, ref pos, lercVersion);
                    if ((src.Length - pos) * 8L < n) throw new ImageFormatException("LERC: byte plane is truncated");
                    new LercHuffman.Reader(table, src, pos).Read(plane);
                    break;
                }
                case PlaneConstant:
                    if (src.Length < 6 || BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(2)) != (uint)n) throw new ImageFormatException("LERC: corrupt byte plane");
                    plane.Fill(src[1]);
                    break;
                case PlaneRaw:
                    if (src.Length - 1 != n) throw new ImageFormatException("LERC: corrupt byte plane");
                    src.Slice(1).CopyTo(plane);
                    break;
                case PlaneRuns:
                {
                    // a byte c <= 127 is followed by c + 1 literal bytes, a larger one by a byte to repeat c - 126 times
                    int p = 1, o = 0;
                    while (p < src.Length)
                    {
                        int c = src[p++];
                        if (c <= 127)
                        {
                            c++;
                            if (c > src.Length - p || c > n - o) throw new ImageFormatException("LERC: corrupt byte plane");
                            src.Slice(p, c).CopyTo(plane.Slice(o));
                            p += c;
                        }
                        else
                        {
                            c -= 126;
                            if (p >= src.Length || c > n - o) throw new ImageFormatException("LERC: corrupt byte plane");
                            plane.Slice(o, c).Fill(src[p++]);
                        }
                        o += c;
                    }
                    if (o != n) throw new ImageFormatException("LERC: corrupt byte plane");
                    break;
                }
                default:
                    throw new ImageFormatException("LERC: unknown byte plane coding");
            }
        }

        // float: [exponent 8 | sign 1 | mantissa 23], differences wrap within [exponent|sign] and within the mantissa
        const uint Lo32 = 0x007fffff, Hi32 = ~Lo32;
        // double: plain IEEE layout, fields [sign 1 | exponent 11] and mantissa 52
        const ulong Lo64 = 0x000fffffffffffff, Hi64 = ~Lo64;

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void Restore32(Span<uint> data, int width, int height, int predictor)
        {
            fixed (uint* d = data)
            {
                if (predictor == 2)
                    for (long i = width, end = (long)width * height; i < end; i++)
                    {
                        uint a = d[i], b = d[i - width];
                        d[i] = ((a & Hi32) + (b & Hi32)) | ((a + b) & Lo32);
                    }
                for (int r = 0; r < height; r++)
                {
                    uint* row = d + (long)r * width;
                    uint prev = row[0];
                    if (predictor >= 1)
                        for (int c = 1; c < width; c++)
                        {
                            uint a = row[c];
                            prev = ((a & Hi32) + (prev & Hi32)) | ((a + prev) & Lo32);
                            row[c] = prev;
                        }
                    for (int c = 0; c < width; c++)
                    {
                        uint t = row[c];
                        row[c] = (t >> 24) << 23 | (t >> 23 & 1) << 31 | (t & Lo32);
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        static void Restore64(Span<ulong> data, int width, int height, int predictor)
        {
            fixed (ulong* d = data)
            {
                if (predictor == 2)
                    for (long i = width, end = (long)width * height; i < end; i++)
                    {
                        ulong a = d[i], b = d[i - width];
                        d[i] = ((a & Hi64) + (b & Hi64)) | ((a + b) & Lo64);
                    }
                if (predictor >= 1)
                    for (int r = 0; r < height; r++)
                    {
                        ulong* row = d + (long)r * width;
                        ulong prev = row[0];
                        for (int c = 1; c < width; c++)
                        {
                            ulong a = row[c];
                            prev = ((a & Hi64) + (prev & Hi64)) | ((a + prev) & Lo64);
                            row[c] = prev;
                        }
                    }
            }
        }
    }
}
