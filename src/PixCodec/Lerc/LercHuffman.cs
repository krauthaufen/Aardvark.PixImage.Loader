using System;
using System.Runtime.CompilerServices;
using System.Buffers.Binary;

namespace PixCodec
{
    /// <summary>
    /// The Huffman coder of LERC2 (8 bit images, and the byte planes of lossless floating point data). The code book
    /// is stored explicitly (lengths, then the codes themselves), codes are written MSB first into little-endian
    /// 32 bit words.
    /// </summary>
    internal sealed unsafe class LercHuffman
    {
        public const int MaxCodeLength = 32;
        const int LutBits = 12;

        // decoding: codes of up to LutBits bits resolve through the table (length << 16 | symbol, 0 = longer code),
        // the others walk the binary tree (two ints per node: > 0 child node, < 0 ~symbol, 0 missing)
        readonly int[] _lut = new int[1 << LutBits];
        int[] _tree = new int[64];
        int _nodes = 1;

        /// <summary>Reads a code book for symbols 0..255.</summary>
        public static LercHuffman ReadTable(ReadOnlySpan<byte> data, ref int pos, int lercVersion)
        {
            if (data.Length - pos < 16) throw new ImageFormatException("LERC: Huffman table is truncated");
            int version = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(pos));
            int size = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(pos + 4));
            int i0 = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(pos + 8));
            int i1 = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(pos + 12));
            pos += 16;
            if (version < 2) throw new ImageFormatException("LERC: unknown Huffman table version");
            if (size <= 0 || size > 256 || i0 < 0 || i0 >= i1 || i1 - i0 > size || i1 > 2 * size)
                throw new ImageFormatException("LERC: corrupt Huffman table");

            int n = i1 - i0;
            Span<uint> lengths = stackalloc uint[256];
            lengths = lengths.Slice(0, n);
            if (LercBits.DecodeArray(data, ref pos, lengths, lercVersion) != n) throw new ImageFormatException("LERC: corrupt Huffman table");

            var h = new LercHuffman();
            // the codes follow in the same order, each with its own length, MSB first in 32 bit words
            ulong acc = 0;
            int have = 0;
            for (int i = 0; i < n; i++)
            {
                int len = (int)lengths[i];
                if (len == 0) continue;
                if (len > MaxCodeLength) throw new ImageFormatException("LERC: Huffman code is too long");
                if (have < len)
                {
                    if (data.Length - pos < 4) throw new ImageFormatException("LERC: Huffman table is truncated");
                    acc = acc << 32 | BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos));
                    pos += 4;
                    have += 32;
                }
                have -= len;
                uint code = (uint)(acc >> have) & (uint)((1UL << len) - 1);
                int symbol = i0 + i;
                if (symbol >= size) symbol -= size;
                h.Add(symbol, len, code);
            }
            return h;
        }

        void Add(int symbol, int len, uint code)
        {
            int node = 0;
            for (int b = len - 1; b >= 0; b--)
            {
                int slot = node * 2 + (int)(code >> b & 1);
                int next = _tree[slot];
                if (next < 0) throw new ImageFormatException("LERC: Huffman codes are not prefix free");
                if (b == 0)
                {
                    if (next != 0) throw new ImageFormatException("LERC: Huffman codes are not prefix free");
                    _tree[slot] = ~symbol;
                    break;
                }
                if (next == 0)
                {
                    if (_nodes * 2 == _tree.Length) Array.Resize(ref _tree, _tree.Length * 2);
                    next = _nodes++;
                    _tree[slot] = next;
                }
                node = next;
            }
            if (len <= LutBits)
            {
                int first = (int)code << (LutBits - len), entry = len << 16 | symbol;
                _lut.AsSpan(first, 1 << (LutBits - len)).Fill(entry);
            }
        }

        /// <summary>Sequential reader of the code stream; create one per stream and call <see cref="Read"/> as often as needed.</summary>
        public ref struct Reader
        {
            readonly LercHuffman _h;
            readonly ReadOnlySpan<byte> _data;
            int _pos;
            ulong _acc;
            int _have;

            public Reader(LercHuffman h, ReadOnlySpan<byte> data, int pos)
            {
                _h = h; _data = data; _pos = pos; _acc = 0; _have = 0;
            }

            /// <summary>Decodes dst.Length symbols.</summary>
            [MethodImpl(MethodImplOptions.AggressiveOptimization)]
            public void Read(Span<byte> dst)
            {
                ulong acc = _acc;
                int have = _have, pos = _pos, n = dst.Length, len = _data.Length;
                fixed (byte* src = _data)
                fixed (byte* d = dst)
                fixed (int* lut = _h._lut)
                {
                    int i = 0;
                    while (i < n)
                    {
                        if (have < 32)
                        {
                            // past the end zeros are supplied; whether real bits ran out is checked below
                            uint word = pos + 4 <= len ? *(uint*)(src + pos) : Tail(pos);
                            pos += 4;
                            acc = acc << 32 | word;
                            have += 32;
                        }
                        int e = lut[(int)(acc >> (have - LutBits)) & ((1 << LutBits) - 1)];
                        if (e == 0)
                        {
                            d[i++] = (byte)_h.Walk(acc, ref have);
                            continue;
                        }
                        have -= e >> 16;
                        d[i++] = (byte)e;
                        if (i < n) // at least 20 bits are left: one more table step needs no refill
                        {
                            e = lut[(int)(acc >> (have - LutBits)) & ((1 << LutBits) - 1)];
                            if (e != 0)
                            {
                                have -= e >> 16;
                                d[i++] = (byte)e;
                            }
                        }
                    }
                }
                if ((long)pos * 8 - have > (long)len * 8) throw new ImageFormatException("LERC: Huffman data is truncated");
                _acc = acc; _have = have; _pos = pos;
            }

            uint Tail(int pos)
            {
                uint w = 0;
                for (int k = 0; k < 4; k++)
                    if ((uint)(pos + k) < (uint)_data.Length) w |= (uint)_data[pos + k] << (8 * k);
                return w;
            }
        }

        int Walk(ulong acc, ref int have)
        {
            int node = 0;
            for (int depth = 0; depth < MaxCodeLength; depth++)
            {
                have--;
                int next = _tree[node * 2 + (int)(acc >> have & 1)];
                if (next < 0) return ~next;
                if (next == 0) break;
                node = next;
            }
            throw new ImageFormatException("LERC: invalid Huffman code in the data");
        }
    }
}
