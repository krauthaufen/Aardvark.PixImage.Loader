using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Aardvark.ImageCodecs
{
    /// <summary>LERC data type codes and the conversions between them, doubles and <see cref="SampleType"/>.</summary>
    internal static class LercTypes
    {
        public const int Char = 0, Byte = 1, Short = 2, UShort = 3, Int = 4, UInt = 5, Float = 6, Double = 7;

        static readonly SampleType[] s_sample =
        {
            SampleType.Int8, SampleType.UInt8, SampleType.Int16, SampleType.UInt16,
            SampleType.Int32, SampleType.UInt32, SampleType.Float32, SampleType.Float64,
        };

        public static SampleType ToSampleType(int dt) => s_sample[dt];

        public static int Size(int dt) => dt < Short ? 1 : dt < Int ? 2 : dt < Double ? 4 : 8;

        /// <summary>
        /// The type a block offset is stored in: the block header's two top bits select a smaller type than the
        /// image's where the value fits. Returns -1 for a combination the format does not define.
        /// </summary>
        public static int ReducedType(int dt, int code)
        {
            switch (dt)
            {
                case Short: return code <= 2 ? dt - code : -1;
                case Int: return dt - code;
                case UShort: return code <= 1 ? dt - 2 * code : -1;
                case UInt: return code <= 2 ? dt - 2 * code : -1;
                case Float: return code == 0 ? Float : code == 1 ? Short : code == 2 ? Byte : -1;
                case Double: return code == 0 ? Double : Double - 2 * code + 1;
                default: return code == 0 ? dt : -1;
            }
        }

        public static double ReadValue(ReadOnlySpan<byte> data, ref int pos, int dt)
        {
            int size = Size(dt);
            if ((uint)pos > (uint)data.Length || data.Length - pos < size) throw new ImageFormatException("LERC: data ends inside a block header");
            var s = data.Slice(pos);
            pos += size;
            switch (dt)
            {
                case Char: return (sbyte)s[0];
                case Byte: return s[0];
                case Short: return BinaryPrimitives.ReadInt16LittleEndian(s);
                case UShort: return BinaryPrimitives.ReadUInt16LittleEndian(s);
                case Int: return BinaryPrimitives.ReadInt32LittleEndian(s);
                case UInt: return BinaryPrimitives.ReadUInt32LittleEndian(s);
                case Float: return BinaryPrimitives.ReadSingleLittleEndian(s);
                default: return BinaryPrimitives.ReadDoubleLittleEndian(s);
            }
        }

        // The typeof tests are resolved by the JIT per instantiation, so these compile to a single conversion.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T FromDouble<T>(double v) where T : unmanaged
        {
            if (typeof(T) == typeof(float)) { float x = (float)v; return Unsafe.As<float, T>(ref x); }
            if (typeof(T) == typeof(double)) return Unsafe.As<double, T>(ref v);
            if (typeof(T) == typeof(byte)) { byte x = (byte)v; return Unsafe.As<byte, T>(ref x); }
            if (typeof(T) == typeof(sbyte)) { sbyte x = (sbyte)v; return Unsafe.As<sbyte, T>(ref x); }
            if (typeof(T) == typeof(ushort)) { ushort x = (ushort)v; return Unsafe.As<ushort, T>(ref x); }
            if (typeof(T) == typeof(short)) { short x = (short)v; return Unsafe.As<short, T>(ref x); }
            if (typeof(T) == typeof(uint)) { uint x = (uint)v; return Unsafe.As<uint, T>(ref x); }
            if (typeof(T) == typeof(int)) { int x = (int)v; return Unsafe.As<int, T>(ref x); }
            throw new NotSupportedException();
        }

        /// <summary>std::min(z, max) as the reference decoder computes it (keeps z when equal, also -0.0 against +0.0).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Clamp(double z, double max) => max < z ? max : z;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T FromLong<T>(long v) where T : unmanaged
        {
            if (typeof(T) == typeof(byte)) { byte x = (byte)v; return Unsafe.As<byte, T>(ref x); }
            if (typeof(T) == typeof(sbyte)) { sbyte x = (sbyte)v; return Unsafe.As<sbyte, T>(ref x); }
            if (typeof(T) == typeof(ushort)) { ushort x = (ushort)v; return Unsafe.As<ushort, T>(ref x); }
            if (typeof(T) == typeof(short)) { short x = (short)v; return Unsafe.As<short, T>(ref x); }
            if (typeof(T) == typeof(uint)) { uint x = (uint)v; return Unsafe.As<uint, T>(ref x); }
            if (typeof(T) == typeof(int)) { int x = (int)v; return Unsafe.As<int, T>(ref x); }
            return FromDouble<T>(v);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double ToDouble<T>(T v) where T : unmanaged
        {
            if (typeof(T) == typeof(float)) return Unsafe.As<T, float>(ref v);
            if (typeof(T) == typeof(double)) return Unsafe.As<T, double>(ref v);
            if (typeof(T) == typeof(byte)) return Unsafe.As<T, byte>(ref v);
            if (typeof(T) == typeof(sbyte)) return Unsafe.As<T, sbyte>(ref v);
            if (typeof(T) == typeof(ushort)) return Unsafe.As<T, ushort>(ref v);
            if (typeof(T) == typeof(short)) return Unsafe.As<T, short>(ref v);
            if (typeof(T) == typeof(uint)) return Unsafe.As<T, uint>(ref v);
            if (typeof(T) == typeof(int)) return Unsafe.As<T, int>(ref v);
            throw new NotSupportedException();
        }
    }

    /// <summary>The validity mask of one band while a (multi-band) blob is decoded: bands may reuse the previous one.</summary>
    internal sealed class LercMask
    {
        /// <summary>One byte per pixel, 1 = valid; null when all pixels are valid.</summary>
        public byte[]? Bytes;
        public int ValidCount;
    }

    internal static unsafe class LercBits
    {
        // ---- Fletcher-32 over big-endian 16 bit words (LERC2 v3+) ----

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static uint Fletcher32(ReadOnlySpan<byte> data)
        {
            // sums are folded only every 359 words (the longest run that cannot overflow); within a run sum2 grows
            // by sum1 after every word, computed four words at a time
            ulong sum1 = 0xffff, sum2 = 0xffff;
            int words = data.Length / 2, i = 0;
            fixed (byte* p = data)
            {
                while (words > 0)
                {
                    int n = Math.Min(words, 359);
                    words -= n;
                    for (; n >= 4; n -= 4, i += 8)
                    {
                        ulong w0 = (uint)(p[i] << 8 | p[i + 1]), w1 = (uint)(p[i + 2] << 8 | p[i + 3]);
                        ulong w2 = (uint)(p[i + 4] << 8 | p[i + 5]), w3 = (uint)(p[i + 6] << 8 | p[i + 7]);
                        sum2 += 4 * sum1 + 4 * w0 + 3 * w1 + 2 * w2 + w3;
                        sum1 += w0 + w1 + w2 + w3;
                    }
                    for (; n > 0; n--, i += 2)
                    {
                        sum1 += (uint)(p[i] << 8 | p[i + 1]);
                        sum2 += sum1;
                    }
                    sum1 = (sum1 & 0xffff) + (sum1 >> 16);
                    sum2 = (sum2 & 0xffff) + (sum2 >> 16);
                }
            }
            if ((data.Length & 1) != 0)
            {
                sum1 += (uint)data[i] << 8;
                sum2 += sum1;
            }
            sum1 = (sum1 & 0xffff) + (sum1 >> 16);
            sum2 = (sum2 & 0xffff) + (sum2 >> 16);
            return (uint)(sum2 << 16 | sum1);
        }

        // ---- validity mask: bit plane (MSB first), run length coded ----

        /// <summary>
        /// Expands the run length coded mask: signed 16 bit counts, a positive one is followed by that many literal
        /// bytes, a negative one by a single byte to repeat, -32768 ends the stream.
        /// </summary>
        public static void DecodeMaskRle(ReadOnlySpan<byte> src, Span<byte> bits)
        {
            int p = 0, o = 0;
            while (true)
            {
                if (src.Length - p < 2) throw new ImageFormatException("LERC: mask data is truncated");
                int cnt = BinaryPrimitives.ReadInt16LittleEndian(src.Slice(p));
                p += 2;
                if (cnt == short.MinValue) break;
                if (cnt > 0)
                {
                    if (cnt > src.Length - p || cnt > bits.Length - o) throw new ImageFormatException("LERC: mask data is corrupt");
                    src.Slice(p, cnt).CopyTo(bits.Slice(o));
                    p += cnt; o += cnt;
                }
                else if (cnt < 0)
                {
                    cnt = -cnt;
                    if (p >= src.Length || cnt > bits.Length - o) throw new ImageFormatException("LERC: mask data is corrupt");
                    bits.Slice(o, cnt).Fill(src[p++]);
                    o += cnt;
                }
                else throw new ImageFormatException("LERC: mask data is corrupt");
            }
            if (o != bits.Length) throw new ImageFormatException("LERC: mask data is too short");
        }
        /// <summary>Bit plane (bit 7 of byte 0 is pixel 0) to one byte per pixel; returns the number of valid pixels.</summary>
        public static int ExpandMask(ReadOnlySpan<byte> bits, Span<byte> mask)
        {
            int n = mask.Length, count = 0;
            for (int k = 0; k < n; k++)
            {
                int v = (bits[k >> 3] >> (7 - (k & 7))) & 1;
                mask[k] = (byte)v;
                count += v;
            }
            return count;
        }

        // ---- fixed width bit stuffing ----

        /// <summary>Bytes taken by <paramref name="count"/> values of <paramref name="numBits"/> bits (LERC2 v3+).</summary>
        public static int StuffedSize(int count, int numBits) => (int)(((long)count * numBits + 7) >> 3);

        /// <summary>Bytes taken by the 32 bit word oriented packing of LERC1 and LERC2 v2: whole words, minus the unused bytes of the last one.</summary>
        public static int StuffedSizeWords(int count, int numBits)
        {
            long bitCount = (long)count * numBits;
            long words = (bitCount + 31) >> 5;
            int tailBits = (int)(bitCount & 31);
            int tailBytes = (tailBits + 7) >> 3;
            return (int)(words * 4 - (tailBytes > 0 ? 4 - tailBytes : 0));
        }

        /// <summary>LERC2 v3+: values are packed LSB first into a little-endian bit stream.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Unstuff(ReadOnlySpan<byte> src, Span<uint> dst, int numBits)
        {
            int count = dst.Length, len = src.Length;
            ulong mask = (1UL << numBits) - 1;
            fixed (byte* p = src)
            fixed (uint* d = dst)
            {
                long bitPos = 0;
                int i = 0;
                for (; i < count; i++, bitPos += numBits)
                {
                    long b = bitPos >> 3;
                    if (b + 8 > len) break;
                    d[i] = (uint)((*(ulong*)(p + b) >> (int)(bitPos & 7)) & mask);
                }
                for (; i < count; i++, bitPos += numBits)
                {
                    int b = (int)(bitPos >> 3);
                    ulong v = 0;
                    for (int k = 0; k < 8 && b + k < len; k++) v |= (ulong)p[b + k] << (8 * k);
                    d[i] = (uint)((v >> (int)(bitPos & 7)) & mask);
                }
            }
        }

        /// <summary>
        /// LERC1 and LERC2 v2: values are packed MSB first into little-endian 32 bit words; of the last word only the
        /// bytes that hold data are stored (its top bytes, moved down).
        /// </summary>
        public static void UnstuffWords(ReadOnlySpan<byte> src, Span<uint> dst, int numBits)
        {
            int count = dst.Length;
            long bitCount = (long)count * numBits;
            int words = (int)((bitCount + 31) >> 5);
            int lastBytes = src.Length - (words - 1) * 4; // 1..4
            ulong acc = 0;
            int have = 0, w = 0;
            for (int i = 0; i < count; i++)
            {
                if (have < numBits)
                {
                    uint word;
                    if (w < words - 1) word = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(w * 4));
                    else
                    {
                        word = 0;
                        for (int k = 0; k < lastBytes; k++) word |= (uint)src[w * 4 + k] << (8 * k);
                        word <<= 8 * (4 - lastBytes);
                    }
                    w++;
                    acc = acc << 32 | word;
                    have += 32;
                }
                have -= numBits;
                dst[i] = (uint)(acc >> have) & (uint)((1UL << numBits) - 1);
            }
        }

        public static int BitsFor(uint maxValue) => 32 - System.Numerics.BitOperations.LeadingZeroCount(maxValue);

        /// <summary>
        /// Reads one bit stuffed array (header byte: bits 0-4 width, bit 5 lookup table mode, bits 6-7 size of the
        /// element count). Returns the element count; the values are in <paramref name="dst"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static int DecodeArray(ReadOnlySpan<byte> data, ref int pos, Span<uint> dst, int lercVersion)
        {
            if ((uint)pos >= (uint)data.Length) throw new ImageFormatException("LERC: data ends inside a block");
            int head = data[pos++];
            int numBits = head & 31;
            bool lut = (head & 32) != 0;
            int code = head >> 6;
            int nb = code == 0 ? 4 : 3 - code;
            if (nb <= 0 || data.Length - pos < nb) throw new ImageFormatException("LERC: corrupt bit stuffing header");
            uint count = nb == 1 ? data[pos] : nb == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(pos)) : BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos));
            pos += nb;
            if (count > (uint)dst.Length) throw new ImageFormatException("LERC: bit stuffed array is larger than its block");
            var values = dst.Slice(0, (int)count);
            if (!lut)
            {
                if (numBits == 0) values.Clear();
                else ReadStuffed(data, ref pos, values, numBits, lercVersion);
                return (int)count;
            }

            if (numBits == 0 || pos >= data.Length) throw new ImageFormatException("LERC: corrupt lookup table block");
            int lutSize = data[pos++]; // counts the leading 0, which is not stored
            if (lutSize < 2) throw new ImageFormatException("LERC: corrupt lookup table block");
            Span<uint> table = stackalloc uint[256];
            table[0] = 0;
            ReadStuffed(data, ref pos, table.Slice(1, lutSize - 1), numBits, lercVersion);
            int indexBits = BitsFor((uint)(lutSize - 1));
            ReadStuffed(data, ref pos, values, indexBits, lercVersion);
            for (int i = 0; i < values.Length; i++)
            {
                uint ix = values[i];
                if (ix >= (uint)lutSize) throw new ImageFormatException("LERC: lookup table index out of range");
                values[i] = table[(int)ix];
            }
            return (int)count;
        }

        static void ReadStuffed(ReadOnlySpan<byte> data, ref int pos, Span<uint> values, int numBits, int lercVersion)
        {
            if (values.Length == 0) return;
            int size = lercVersion >= 3 ? StuffedSize(values.Length, numBits) : StuffedSizeWords(values.Length, numBits);
            if (data.Length - pos < size) throw new ImageFormatException("LERC: data ends inside a block");
            if (lercVersion >= 3) Unstuff(data.Slice(pos, size), values, numBits);
            else UnstuffWords(data.Slice(pos, size), values, numBits);
            pos += size;
        }
    }
}
