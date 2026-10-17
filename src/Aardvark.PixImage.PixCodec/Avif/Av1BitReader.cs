using System;

namespace Aardvark.ImageCodecs.Av1
{
    /// <summary>MSB first bit reader for OBU headers (AV1 spec 4.10 descriptors, 8.1).</summary>
    internal sealed class Av1BitReader
    {
        readonly byte[] data;
        readonly int start, end;   // byte range
        long pos;                  // bit position relative to the start of data

        public Av1BitReader(byte[] data, int offset, int length)
        {
            if (offset < 0 || length < 0 || offset + (long)length > data.Length) throw new ImageFormatException("AV1: bad OBU range");
            this.data = data; start = offset; end = offset + length; pos = (long)offset * 8;
        }

        /// <summary>Bit position relative to the start of the range.</summary>
        public long Position => pos - (long)start * 8;
        public int BytePosition => (int)((pos + 7) >> 3) - start;
        public int ByteOffsetAbsolute => (int)(pos >> 3);
        public int End => end;
        public byte[] Data => data;

        public int Bit()
        {
            long byteIdx = pos >> 3;
            if (byteIdx >= end) throw new ImageFormatException("AV1: truncated OBU");
            int b = (data[byteIdx] >> (7 - (int)(pos & 7))) & 1;
            pos++;
            return b;
        }

        /// <summary>f(n), n up to 32.</summary>
        public uint F(int n)
        {
            uint x = 0;
            for (int i = 0; i < n; i++) x = (x << 1) | (uint)Bit();
            return x;
        }

        public int I(int n) => (int)F(n);
        public bool Flag() => Bit() != 0;

        public uint Uvlc()
        {
            int leadingZeros = 0;
            while (Bit() == 0)
            {
                leadingZeros++;
                if (leadingZeros >= 32) return uint.MaxValue;
            }
            if (leadingZeros >= 32) return uint.MaxValue;
            uint value = F(leadingZeros);
            return (uint)(value + (1L << leadingZeros) - 1);
        }

        public ulong Leb128()
        {
            ulong value = 0;
            for (int i = 0; i < 8; i++)
            {
                uint b = F(8);
                value |= (ulong)(b & 0x7f) << (i * 7);
                if ((b & 0x80) == 0) break;
            }
            return value;
        }

        public int Su(int n)
        {
            int value = (int)F(n);
            int signMask = 1 << (n - 1);
            if ((value & signMask) != 0) value -= 2 * signMask;
            return value;
        }

        public int Ns(int n)
        {
            int w = FloorLog2(n) + 1;
            int m = (1 << w) - n;
            int v = (int)F(w - 1);
            if (v < m) return v;
            int extra = Bit();
            return (v << 1) - m + extra;
        }

        public uint Le(int n)
        {
            uint t = 0;
            for (int i = 0; i < n; i++) t += F(8) << (i * 8);
            return t;
        }

        public void ByteAlign()
        {
            while ((pos & 7) != 0) Bit();
        }

        public void SkipBits(long n) => pos += n;
        public void SeekByte(int absoluteByte) => pos = (long)absoluteByte * 8;

        public static int FloorLog2(int x)
        {
            int s = 0;
            while (x > 1) { x >>= 1; s++; }
            return s;
        }

        public static int CeilLog2(int x)
        {
            if (x < 2) return 0;
            int i = 1, p = 2;
            while (p < x) { i++; p <<= 1; }
            return i;
        }
    }
}
