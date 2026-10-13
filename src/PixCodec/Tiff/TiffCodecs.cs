using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PixCodec
{
    /// <summary>TIFF flavoured LZW (compression 5): MSB-first codes with "early change", plus the pre-6.0 LSB-first variant on decode.</summary>
    internal static unsafe class TiffLzw
    {
        const int Clear = 256, Eoi = 257, First = 258, MaxCodes = 4096;

        /// <summary>
        /// Decodes into <paramref name="dst"/> and returns the number of bytes produced (at most <paramref name="dstLen"/>).
        /// The string table stores, per code, where in the output that string was last written, so emitting a code is a
        /// plain forward copy inside the output buffer - no prefix chain walking and no per-code allocations.
        /// </summary>
        public static int Decode(ReadOnlySpan<byte> src, byte* dst, int dstLen)
        {
            // Old-style LZW (libtiff's "compat" mode) packs codes LSB first, so its 9-bit Clear code reads 00 01.
            if (src.Length >= 2 && src[0] == 0 && (src[1] & 1) != 0) return DecodeOldStyle(src, dst, dstLen);

            int* offs = stackalloc int[MaxCodes];
            int* lens = stackalloc int[MaxCodes];
            int n = src.Length, sp = 0, op = 0;
            ulong buf = 0;
            int bits = 0, width = 9, next = First;
            int prevPos = 0, prevLen = 0;
            fixed (byte* s = src)
            {
                while (op < dstLen)
                {
                    if (bits < width)
                    {
                        if (sp + 8 <= n)
                        {
                            ulong v = Unsafe.ReadUnaligned<ulong>(s + sp);
                            if (BitConverter.IsLittleEndian) v = BinaryPrimitives.ReverseEndianness(v);
                            buf |= v >> bits;
                            int adv = (63 - bits) >> 3;
                            sp += adv; bits += adv << 3;
                        }
                        else
                        {
                            while (bits <= 56 && sp < n) { buf |= (ulong)s[sp++] << (56 - bits); bits += 8; }
                            if (bits < width) break; // data ends without EOI: tolerated, the caller checks the length
                        }
                    }
                    int code = (int)(buf >> (64 - width));
                    buf <<= width; bits -= width;

                    int len;
                    if (code < 256)
                    {
                        dst[op] = (byte)code;
                        len = 1;
                    }
                    else if (code == Clear)
                    {
                        width = 9; next = First; prevLen = 0;
                        continue;
                    }
                    else if (code == Eoi) break;
                    else
                    {
                        int so;
                        if (code < next) { so = offs[code]; len = lens[code]; }
                        else if (code == next && prevLen > 0) { so = prevPos; len = prevLen + 1; } // KwKwK: overlaps its own output
                        else throw new ImageFormatException("TIFF: corrupt LZW data");
                        if (len > dstLen - op) len = dstLen - op;
                        byte* d = dst + op, f = dst + so;
                        if (so + len <= op)
                        {
                            if (len <= 8 && op + 8 <= dstLen) Unsafe.WriteUnaligned(d, Unsafe.ReadUnaligned<ulong>(f));
                            else Buffer.MemoryCopy(f, d, len, len);
                        }
                        else
                            for (int i = 0; i < len; i++) d[i] = f[i];
                    }
                    if (prevLen > 0 && next < MaxCodes)
                    {
                        offs[next] = prevPos; lens[next] = prevLen + 1;
                        next++;
                        // early change: the width grows one code before the table would actually need it
                        if (next == (1 << width) - 1 && width < 12) width++;
                    }
                    prevPos = op; prevLen = len;
                    op += len;
                }
            }
            return op;
        }

        static int DecodeOldStyle(ReadOnlySpan<byte> src, byte* dst, int dstLen)
        {
            int* offs = stackalloc int[MaxCodes];
            int* lens = stackalloc int[MaxCodes];
            int n = src.Length, sp = 0, op = 0;
            uint buf = 0;
            int bits = 0, width = 9, next = First;
            int prevPos = 0, prevLen = 0;
            while (op < dstLen)
            {
                while (bits < width && sp < n) { buf |= (uint)src[sp++] << bits; bits += 8; }
                if (bits < width) break;
                int code = (int)(buf & ((1u << width) - 1));
                buf >>= width; bits -= width;

                int len;
                if (code < 256) { dst[op] = (byte)code; len = 1; }
                else if (code == Clear) { width = 9; next = First; prevLen = 0; continue; }
                else if (code == Eoi) break;
                else
                {
                    int so;
                    if (code < next) { so = offs[code]; len = lens[code]; }
                    else if (code == next && prevLen > 0) { so = prevPos; len = prevLen + 1; }
                    else throw new ImageFormatException("TIFF: corrupt LZW data");
                    if (len > dstLen - op) len = dstLen - op;
                    byte* d = dst + op, f = dst + so;
                    for (int i = 0; i < len; i++) d[i] = f[i];
                }
                if (prevLen > 0 && next < MaxCodes)
                {
                    offs[next] = prevPos; lens[next] = prevLen + 1;
                    next++;
                    if (next == 1 << width && width < 12) width++;
                }
                prevPos = op; prevLen = len;
                op += len;
            }
            return op;
        }

        const int HashBits = 13, HashSize = 1 << HashBits;

        public static int MaxEncodedSize(int n) => n + n / 2 + n / 1024 + 64;

        /// <summary>Encodes <paramref name="src"/> into <paramref name="dst"/> (at least <see cref="MaxEncodedSize"/> bytes), returns the length.</summary>
        public static int Encode(ReadOnlySpan<byte> src, Span<byte> dst)
        {
            int* keys = stackalloc int[HashSize];
            ushort* vals = stackalloc ushort[HashSize];
            new Span<int>(keys, HashSize).Fill(-1);
            ulong acc = 0; int nbits = 0, op = 0;
            int width = 9, next = First;

            void Put(int code, ref ulong a, ref int nb, ref int o, Span<byte> d, int w)
            {
                a = (a << w) | (uint)code; nb += w;
                while (nb >= 8) { nb -= 8; d[o++] = (byte)(a >> nb); }
            }

            Put(Clear, ref acc, ref nbits, ref op, dst, width);
            if (src.Length > 0)
            {
                int prefix = src[0];
                for (int i = 1; i < src.Length; i++)
                {
                    int c = src[i];
                    int key = (prefix << 8) | c;
                    int h = (int)(((uint)key * 0x9E3779B1u) >> (32 - HashBits));
                    while (true)
                    {
                        int k = keys[h];
                        if (k == key) { prefix = vals[h]; break; }
                        if (k < 0)
                        {
                            Put(prefix, ref acc, ref nbits, ref op, dst, width);
                            keys[h] = key; vals[h] = (ushort)next;
                            next++;
                            if (next == 1 << width && width < 12) width++;
                            if (next >= MaxCodes - 2)
                            {
                                Put(Clear, ref acc, ref nbits, ref op, dst, width);
                                new Span<int>(keys, HashSize).Fill(-1);
                                width = 9; next = First;
                            }
                            prefix = c;
                            break;
                        }
                        h = (h + 1) & (HashSize - 1);
                    }
                }
                Put(prefix, ref acc, ref nbits, ref op, dst, width);
                // the decoder adds a table entry for this last code as well, which may widen the EOI code
                next++;
                if (next == 1 << width && width < 12) width++;
            }
            Put(Eoi, ref acc, ref nbits, ref op, dst, width);
            if (nbits > 0) dst[op++] = (byte)(acc << (8 - nbits));
            return op;
        }
    }

    /// <summary>Apple PackBits (compression 32773).</summary>
    internal static unsafe class TiffPackBits
    {
        public static int Decode(ReadOnlySpan<byte> src, byte* dst, int dstLen)
        {
            int sp = 0, op = 0, n = src.Length;
            while (sp < n && op < dstLen)
            {
                int c = (sbyte)src[sp++];
                if (c >= 0)
                {
                    int len = c + 1;
                    if (len > n - sp) len = n - sp;
                    if (len > dstLen - op) len = dstLen - op;
                    src.Slice(sp, len).CopyTo(new Span<byte>(dst + op, len));
                    sp += c + 1; op += len;
                }
                else if (c != -128)
                {
                    if (sp >= n) break;
                    int len = 1 - c;
                    if (len > dstLen - op) len = dstLen - op;
                    new Span<byte>(dst + op, len).Fill(src[sp++]);
                    op += len;
                }
            }
            return op;
        }

        public static int MaxEncodedSize(int n, int rowBytes) => n + (n / 128 + 1) + (rowBytes > 0 ? n / rowBytes + 1 : 1) + 16;

        /// <summary>Encodes row by row (runs never cross a row boundary, as the spec requires).</summary>
        public static int Encode(ReadOnlySpan<byte> src, int rowBytes, Span<byte> dst)
        {
            int op = 0;
            for (int r = 0; r < src.Length; r += rowBytes)
                op = EncodeRow(src.Slice(r, Math.Min(rowBytes, src.Length - r)), dst, op);
            return op;
        }

        static int EncodeRow(ReadOnlySpan<byte> s, Span<byte> d, int op)
        {
            int i = 0, n = s.Length;
            while (i < n)
            {
                int run = 1;
                byte b = s[i];
                while (i + run < n && run < 128 && s[i + run] == b) run++;
                if (run >= 2)
                {
                    d[op++] = (byte)(1 - run); d[op++] = b;
                    i += run;
                    continue;
                }
                // literal: extend until a run of 3 starts (a run of 2 inside a literal is not worth breaking for)
                int start = i;
                i++;
                while (i < n && i - start < 128)
                {
                    if (i + 2 < n && s[i] == s[i + 1] && s[i] == s[i + 2]) break;
                    i++;
                }
                int len = i - start;
                d[op++] = (byte)(len - 1);
                s.Slice(start, len).CopyTo(d.Slice(op));
                op += len;
            }
            return op;
        }
    }

    /// <summary>TIFF predictors (tag 317) and small byte level helpers.</summary>
    internal static unsafe class TiffPredict
    {
        /// <summary>Undoes horizontal differencing on one row of <paramref name="count"/> samples (native byte order).</summary>
        public static void UndoHorizontal(byte* row, int count, int stride, int bps)
        {
            if (count <= stride) return;
            switch (bps)
            {
                case 1:
                    if (stride == 3)
                    {
                        byte r = row[0], g = row[1], b = row[2];
                        byte* p = row + 3, end = row + count - 2;
                        for (; p < end; p += 3)
                        {
                            p[0] = r = (byte)(r + p[0]); p[1] = g = (byte)(g + p[1]); p[2] = b = (byte)(b + p[2]);
                        }
                    }
                    else if (stride == 4)
                    {
                        byte r = row[0], g = row[1], b = row[2], a = row[3];
                        byte* p = row + 4, end = row + count - 3;
                        for (; p < end; p += 4)
                        {
                            p[0] = r = (byte)(r + p[0]); p[1] = g = (byte)(g + p[1]); p[2] = b = (byte)(b + p[2]); p[3] = a = (byte)(a + p[3]);
                        }
                    }
                    else if (stride == 1)
                    {
                        byte v = row[0];
                        for (int i = 1; i < count; i++) row[i] = v = (byte)(v + row[i]);
                    }
                    else
                        for (int i = stride; i < count; i++) row[i] = (byte)(row[i] + row[i - stride]);
                    break;
                case 2:
                {
                    ushort* p = (ushort*)row;
                    if (stride == 1)
                    {
                        ushort v = p[0];
                        for (int i = 1; i < count; i++) p[i] = v = (ushort)(v + p[i]);
                    }
                    else
                        for (int i = stride; i < count; i++) p[i] = (ushort)(p[i] + p[i - stride]);
                    break;
                }
                case 4:
                {
                    uint* p = (uint*)row;
                    for (int i = stride; i < count; i++) p[i] += p[i - stride];
                    break;
                }
                case 8:
                {
                    ulong* p = (ulong*)row;
                    for (int i = stride; i < count; i++) p[i] += p[i - stride];
                    break;
                }
            }
        }

        public static void ApplyHorizontal(byte* row, int count, int stride, int bps)
        {
            switch (bps)
            {
                case 1:
                    for (int i = count - 1; i >= stride; i--) row[i] = (byte)(row[i] - row[i - stride]);
                    break;
                case 2:
                {
                    ushort* p = (ushort*)row;
                    for (int i = count - 1; i >= stride; i--) p[i] = (ushort)(p[i] - p[i - stride]);
                    break;
                }
                case 4:
                {
                    uint* p = (uint*)row;
                    for (int i = count - 1; i >= stride; i--) p[i] -= p[i - stride];
                    break;
                }
                case 8:
                {
                    ulong* p = (ulong*)row;
                    for (int i = count - 1; i >= stride; i--) p[i] -= p[i - stride];
                    break;
                }
            }
        }

        /// <summary>
        /// Undoes the floating point predictor on one row: byte-wise differencing over the whole row, with the bytes of
        /// all samples stored as planes (most significant byte plane first, independent of the file byte order).
        /// The result is in machine byte order. <paramref name="tmp"/> needs count * bps bytes.
        /// </summary>
        public static void UndoFloat(byte* row, int count, int stride, int bps, byte* tmp)
        {
            int n = count * bps;
            for (int i = stride; i < n; i++) row[i] = (byte)(row[i] + row[i - stride]);
            Buffer.MemoryCopy(row, tmp, n, n);
            for (int b = 0; b < bps; b++)
            {
                byte* plane = tmp + (long)b * count;
                byte* o = row + (BitConverter.IsLittleEndian ? bps - 1 - b : b);
                for (int i = 0; i < count; i++) o[(long)i * bps] = plane[i];
            }
        }

        public static void ApplyFloat(byte* row, int count, int stride, int bps, byte* tmp)
        {
            int n = count * bps;
            for (int b = 0; b < bps; b++)
            {
                byte* plane = tmp + (long)b * count;
                byte* o = row + (BitConverter.IsLittleEndian ? bps - 1 - b : b);
                for (int i = 0; i < count; i++) plane[i] = o[(long)i * bps];
            }
            for (int i = n - 1; i >= stride; i--) row[i] = (byte)(tmp[i] - tmp[i - stride]);
            for (int i = Math.Min(stride, n) - 1; i >= 0; i--) row[i] = tmp[i];
        }

        public static void SwapBytes(byte* p, long bytes, int bps)
        {
            while (bytes > 0)
            {
                int n = (int)Math.Min(bytes, 1 << 30);
                switch (bps)
                {
                    case 2: { var s = new Span<ushort>(p, n / 2); BinaryPrimitives.ReverseEndianness(s, s); break; }
                    case 4: { var s = new Span<uint>(p, n / 4); BinaryPrimitives.ReverseEndianness(s, s); break; }
                    case 8: { var s = new Span<ulong>(p, n / 8); BinaryPrimitives.ReverseEndianness(s, s); break; }
                }
                p += n; bytes -= n;
            }
        }

        static readonly byte[] Reversed = MakeReversed();

        static byte[] MakeReversed()
        {
            var t = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                int v = 0;
                for (int b = 0; b < 8; b++) if ((i & (1 << b)) != 0) v |= 0x80 >> b;
                t[i] = (byte)v;
            }
            return t;
        }

        /// <summary>Reverses the bit order inside each byte (FillOrder 2).</summary>
        public static void ReverseBits(ReadOnlySpan<byte> src, Span<byte> dst)
        {
            var t = Reversed;
            for (int i = 0; i < src.Length; i++) dst[i] = t[src[i]];
        }
    }
}
