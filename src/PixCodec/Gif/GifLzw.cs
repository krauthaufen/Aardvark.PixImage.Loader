using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;

namespace PixCodec
{
    /// <summary>GIF flavoured LZW: LSB-first variable width codes of up to 12 bits, clear and end codes.</summary>
    internal static unsafe class GifLzw
    {
        const int MaxCodes = 4096;

        /// <summary>
        /// Decodes the (already de-blocked) code stream into <paramref name="dst"/> and returns the number of pixels
        /// produced. Running out of input or hitting the end code early is not an error; surplus pixels are dropped.
        /// A code that cannot exist yet is.
        /// </summary>
        public static int Decode(ReadOnlySpan<byte> src, int minCodeSize, Span<byte> dst)
        {
            // The string of a table entry is "previous string + first byte of the current one", and exactly those bytes
            // sit next to each other in the output: where the previous string was written, one byte longer. So an
            // entry is just (address, length) and emitting a code is a copy from earlier output - no prefix chains.
            // Single byte root codes point into a small identity table instead.
            byte** start = stackalloc byte*[MaxCodes];
            ushort* length = stackalloc ushort[MaxCodes];

            int clear = 1 << minCodeSize, eoi = clear + 1;
            byte* roots = stackalloc byte[clear + 2 + 8];      // 8 bytes of slack for the wide loads below
            for (int i = 0; i < clear + 2 + 8; i++) roots[i] = (byte)i;
            for (int i = 0; i < clear + 2; i++)                 // clear/end are never looked up, but defined
            {
                start[i] = roots + i;
                length[i] = 1;
            }

            int codeSize = minCodeSize + 1;
            uint mask = (1u << codeSize) - 1;
            int avail = clear + 2;
            bool first = true;          // next code is the first after a clear: nothing to add to the table
            byte* prevStart = null;     // where the previous string went in the output
            int prevLength = 0;

            ulong bits = 0;
            int nbits = 0;
            int n = dst.Length;
            if (n == 0) return 0;

            fixed (byte* s0 = src)
            fixed (byte* d0 = dst)
            {
                byte* sp = s0, send = s0 + src.Length;
                byte* dp = d0, dend = d0 + n;
                while (true)
                {
                    if (nbits < codeSize)
                    {
                        if (send - sp >= 8)
                        {
                            ulong w = BinaryPrimitives.ReadUInt64LittleEndian(new ReadOnlySpan<byte>(sp, 8));
                            bits |= w << nbits;
                            sp += (63 - nbits) >> 3;
                            nbits |= 56;
                        }
                        else
                        {
                            while (nbits <= 56 && sp < send) { bits |= (ulong)*sp++ << nbits; nbits += 8; }
                            if (nbits < codeSize) break;
                        }
                    }
                    int code = (int)((uint)bits & mask);
                    bits >>= codeSize;
                    nbits -= codeSize;

                    if (code == clear)
                    {
                        codeSize = minCodeSize + 1;
                        mask = (1u << codeSize) - 1;
                        avail = clear + 2;
                        first = true;
                        continue;
                    }
                    if (code == eoi) break;

                    byte* from;
                    int len;
                    if (first)
                    {
                        if (code > clear) throw new ImageFormatException("invalid GIF LZW code");
                        first = false;
                        from = roots + code;
                        len = 1;
                        // Only with min code size 1: the first table entry (4) already needs a third bit.
                        if (avail == 1 << codeSize) { codeSize++; mask = (1u << codeSize) - 1; }
                    }
                    else
                    {
                        if (code < avail)
                        {
                            from = start[code];
                            len = length[code];
                        }
                        else if (code == avail && avail < MaxCodes)
                        {
                            // The string being defined right now: the previous one plus its own first byte. Its last
                            // byte is not in the output yet; it is patched in after the copy.
                            from = prevStart;
                            len = prevLength + 1;
                        }
                        else throw new ImageFormatException("invalid GIF LZW code");

                        if (avail < MaxCodes)
                        {
                            start[avail] = prevStart;
                            length[avail] = (ushort)(prevLength + 1);
                            avail++;
                            // When the table is full the width stays at 12 bits until the encoder sends a clear code.
                            if (avail == 1 << codeSize && codeSize < 12)
                            {
                                codeSize++;
                                mask = (1u << codeSize) - 1;
                            }
                        }
                    }

                    prevStart = dp;
                    prevLength = len;
                    if (dend - dp >= len + 8)
                    {
                        // 8 bytes at a time; what is written beyond the string is overwritten by the next one.
                        // Source and destination are at least 8 apart whenever a second step is needed.
                        *(ulong*)dp = *(ulong*)from;
                        for (int k = 8; k < len; k += 8) *(ulong*)(dp + k) = *(ulong*)(from + k);
                        if (from + len > dp) dp[len - 1] = *dp;
                        dp += len;
                    }
                    else
                    {
                        // Close to the end of the output: bytewise, dropping what does not fit.
                        int room = (int)(dend - dp);
                        int m = Math.Min(len, room);
                        for (int k = 0; k < m; k++) dp[k] = from[k];
                        if (from + len > dp && len <= room) dp[len - 1] = *dp;
                        dp += m;
                        if (dp == dend) break;
                    }
                }
                return (int)(dp - d0);
            }
        }

        const int HashBits = 13;
        const int HashSize = 1 << HashBits;

        /// <summary>Writes the min code size byte, the code stream in sub-blocks and the block terminator.</summary>
        public static void Encode(ReadOnlySpan<byte> pixels, int minCodeSize, Stream output)
        {
            // Hash slot: (prefix << 8 | byte) << 12 | code. Codes are never 0, so 0 marks an empty slot.
            var pool = ArrayPool<byte>.Shared;
            // Worst case is one 12 bit code per pixel plus clear/end codes.
            byte[] buf = pool.Rent(checked(pixels.Length + pixels.Length / 2 + 64));
            uint* table = stackalloc uint[HashSize];
            new Span<uint>(table, HashSize).Clear();

            int clear = 1 << minCodeSize, eoi = clear + 1;
            int next = clear + 2;
            int codeSize = minCodeSize + 1;
            ulong bits = (ulong)clear;
            int nbits = codeSize;
            int bp = 0;
            try
            {
                fixed (byte* p0 = pixels)
                fixed (byte* b0 = buf)
                {
                    int n = pixels.Length;
                    uint prefix = n > 0 ? p0[0] : 0u;
                    for (int i = 1; i < n; i++)
                    {
                        uint c = p0[i];
                        uint key = prefix << 8 | c;
                        uint h = (key * 0x9E3779B1u) >> (32 - HashBits);
                        uint e;
                        while ((e = table[h]) != 0)
                        {
                            if (e >> 12 == key) break;
                            h = (h + 1) & (HashSize - 1);
                        }
                        if (e != 0) { prefix = e & 0xFFF; continue; }

                        bits |= (ulong)prefix << nbits;
                        nbits += codeSize;
                        if (nbits >= 32)
                        {
                            BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(b0 + bp, 4), (uint)bits);
                            bp += 4; bits >>= 32; nbits -= 32;
                        }
                        if (next < MaxCodes)
                        {
                            table[h] = key << 12 | (uint)next;
                            if (next == 1 << codeSize) codeSize++;
                            next++;
                        }
                        else
                        {
                            bits |= (ulong)clear << nbits;
                            nbits += codeSize;
                            if (nbits >= 32)
                            {
                                BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(b0 + bp, 4), (uint)bits);
                                bp += 4; bits >>= 32; nbits -= 32;
                            }
                            new Span<uint>(table, HashSize).Clear();
                            next = clear + 2;
                            codeSize = minCodeSize + 1;
                        }
                        prefix = c;
                    }
                    if (n > 0)
                    {
                        bits |= (ulong)prefix << nbits;
                        nbits += codeSize;
                        // The decoder adds a table entry for this code too, which may widen the end code.
                        if (next < MaxCodes && next == 1 << codeSize) codeSize++;
                        if (nbits >= 32)
                        {
                            BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(b0 + bp, 4), (uint)bits);
                            bp += 4; bits >>= 32; nbits -= 32;
                        }
                    }
                    bits |= (ulong)eoi << nbits;
                    nbits += codeSize;
                    while (nbits > 0) { b0[bp++] = (byte)bits; bits >>= 8; nbits -= 8; }
                }

                // Sub-block framing: up to 255 data bytes behind a length byte.
                byte[] chunk = pool.Rent(256 * 64);
                try
                {
                    output.WriteByte((byte)minCodeSize);
                    int cp = 0;
                    for (int i = 0; i < bp; i += 255)
                    {
                        int len = Math.Min(255, bp - i);
                        if (cp + len + 1 > 256 * 64) { output.Write(chunk, 0, cp); cp = 0; }
                        chunk[cp++] = (byte)len;
                        Buffer.BlockCopy(buf, i, chunk, cp, len);
                        cp += len;
                    }
                    output.Write(chunk, 0, cp);
                    output.WriteByte(0);
                }
                finally { pool.Return(chunk); }
            }
            finally { pool.Return(buf); }
        }
    }
}
