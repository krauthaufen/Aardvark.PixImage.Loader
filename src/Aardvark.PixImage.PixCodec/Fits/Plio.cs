using System;
using System.Buffers.Binary;

namespace Aardvark.ImageCodecs
{
    /// <summary>
    /// IRAF PLIO line lists (PLIO_1 of the FITS tiled image convention, section 6.3): 16 bit instructions with a 3 bit opcode
    /// and 12 bits of data that reconstruct one line from runs of zeros and a tracked "high value".
    /// </summary>
    internal static class Plio
    {
        static short W(ReadOnlySpan<byte> c, int i) => BinaryPrimitives.ReadInt16BigEndian(c.Slice(2 * i));

        public static void Decode(ReadOnlySpan<byte> c, char elemType, Span<long> dst)
        {
            if (elemType != 'I') throw new ImageFormatException("FITS: PLIO data must be 16 bit words");
            int nw = c.Length / 2;
            if (nw < 3) throw new ImageFormatException("FITS: PLIO line list too short");
            int start, len;
            if (W(c, 2) > 0)
            {
                // old format: length, then two more header words
                start = 3; len = W(c, 0);
            }
            else
            {
                if (nw < 7) throw new ImageFormatException("FITS: PLIO line list too short");
                start = W(c, 1); len = (ushort)W(c, 3) + (W(c, 4) << 15);
            }
            if (start < 0 || len > nw || start > len) throw new ImageFormatException("FITS: bad PLIO header");
            dst.Clear();
            long high = 1;
            int x = 0, n = dst.Length;
            for (int i = start; i < len; i++)
            {
                int w = W(c, i);
                int op = w >> 12 & 7, data = w & 0xFFF;
                switch (op)
                {
                    case 0: // ZN
                        x += data;
                        break;
                    case 4: // HN
                        for (int k = 0; k < data; k++) { if (x >= n) break; dst[x++] = high; }
                        if (x > n) x = n;
                        break;
                    case 5: // PN
                        x += data - 1;
                        if (data > 0 && x >= 0 && x < n) dst[x] = high;
                        x++;
                        break;
                    case 1: // SH
                        if (++i >= len) throw new ImageFormatException("FITS: PLIO list ends inside SH");
                        high = ((long)W(c, i) << 12) | (uint)data;
                        break;
                    case 2: high += data; break;
                    case 3: high -= data; break;
                    case 6: high += data; if (x < n) dst[x] = high; x++; break;
                    case 7: high -= data; if (x < n) dst[x] = high; x++; break;
                }
                if (x >= n && op is 0 or 4 or 5 or 6 or 7) { if (x > n) x = n; }
            }
        }
    }
}
