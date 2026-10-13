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
            // Table entry: prefix code (bits 0..11) | last byte of the string (16..23) | first byte of the string (24..31).
            uint* entry = stackalloc uint[MaxCodes];
            ushort* length = stackalloc ushort[MaxCodes];

            int clear = 1 << minCodeSize, eoi = clear + 1;
            for (int i = 0; i < clear; i++)
            {
                entry[i] = (uint)(byte)i << 16 | (uint)(byte)i << 24;
                length[i] = 1;
            }
            // Never produced by a valid stream; defined so that corrupt data cannot read uninitialized stack.
            entry[clear] = entry[eoi] = 0;
            length[clear] = length[eoi] = 1;

            int codeSize = minCodeSize + 1;
            uint mask = (1u << codeSize) - 1;
            int avail = clear + 2;
            int prev = -1;

            ulong bits = 0;
            int nbits = 0;
            int n = dst.Length, op = 0;
            if (n == 0) return 0;

            fixed (byte* s0 = src)
            fixed (byte* d0 = dst)
            {
                byte* sp = s0, send = s0 + src.Length;
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
                        prev = -1;
                        continue;
                    }
                    if (code == eoi) break;

                    if (prev < 0)
                    {
                        if (code > clear) throw new ImageFormatException("invalid GIF LZW code");
                        d0[op++] = (byte)code;
                        prev = code;
                        if (op == n) break;
                        // Only with min code size 1: the first table entry (4) already needs a third bit.
                        if (avail == 1 << codeSize) { codeSize++; mask = (1u << codeSize) - 1; }
                        continue;
                    }

                    uint pe = entry[prev];
                    uint firstOfCode;
                    if (code < avail) firstOfCode = entry[code] >> 24;
                    else if (code == avail && avail < MaxCodes) firstOfCode = pe >> 24; // the string being defined right now
                    else throw new ImageFormatException("invalid GIF LZW code");

                    if (avail < MaxCodes)
                    {
                        entry[avail] = (uint)prev | firstOfCode << 16 | (pe & 0xFF000000u);
                        length[avail] = (ushort)(length[prev] + 1);
                        avail++;
                        // When the table is full the width stays at 12 bits until the encoder sends a clear code.
                        if (avail == 1 << codeSize && codeSize < 12)
                        {
                            codeSize++;
                            mask = (1u << codeSize) - 1;
                        }
                    }

                    // Strings are written back to front straight into the output.
                    int len = length[code];
                    int c = code;
                    if (len <= n - op)
                    {
                        byte* p = d0 + op + len;
                        op += len;
                        do
                        {
                            uint e = entry[c];
                            *--p = (byte)(e >> 16);
                            c = (int)(e & 0xFFF);
                        } while (--len > 0);
                        if (op == n) break;
                    }
                    else
                    {
                        for (int q = op + len - 1; q >= op; q--)
                        {
                            uint e = entry[c];
                            if (q < n) d0[q] = (byte)(e >> 16);
                            c = (int)(e & 0xFFF);
                        }
                        op = n;
                        break;
                    }
                    prev = code;
                }
            }
            return op;
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
