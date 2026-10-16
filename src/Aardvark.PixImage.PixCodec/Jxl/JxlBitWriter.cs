using System;
using System.Runtime.CompilerServices;

namespace Aardvark.ImageCodecs.JpegXl
{
    /// <summary>
    /// Bit writer of the JPEG XL codestream: bits are written least significant first, the counterpart of
    /// <see cref="JxlBitReader"/>.
    /// </summary>
    internal sealed class JxlBitWriter
    {
        byte[] buf;
        int len;          // complete bytes in buf
        ulong acc;        // pending bits, LSB first
        int nacc;         // number of pending bits (< 64)

        public JxlBitWriter(int capacity = 256) { buf = new byte[Math.Max(16, capacity)]; }

        /// <summary>Bits written so far.</summary>
        public long BitLength => (long)len * 8 + nacc;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(int n, ulong v)
        {
            if (n == 0) return;
            if (n > 32) { Write(32, v & 0xFFFFFFFF); Write(n - 32, v >> 32); return; }
            v &= (1UL << n) - 1;
            acc |= v << nacc;
            nacc += n;
            if (nacc >= 32) Flush32();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void Flush32()
        {
            if (len + 8 > buf.Length) Array.Resize(ref buf, Math.Max(buf.Length * 2, len + 64));
            Unsafe.WriteUnaligned(ref buf[len], (uint)acc);
            len += 4;
            acc >>= 32;
            nacc -= 32;
        }

        public void WriteBool(bool b) => Write(1, b ? 1u : 0u);

        public void ZeroPadToByte()
        {
            int r = (int)(BitLength & 7);
            if (r != 0) Write(8 - r, 0);
        }

        /// <summary>U32 with the four distributions packed as in <see cref="JxlBitReader.ReadU32"/>.</summary>
        public void WriteU32(uint value, ulong d0, ulong d1, ulong d2, ulong d3)
        {
            Span<ulong> ds = stackalloc ulong[4] { d0, d1, d2, d3 };
            for (int i = 0; i < 4; i++)
            {
                int bits = (int)(ds[i] >> 32);
                uint off = (uint)ds[i];
                if (bits == 0) { if (value == off) { Write(2, (uint)i); return; } continue; }
                if (value >= off && (ulong)(value - off) < (1UL << bits)) { Write(2, (uint)i); Write(bits, value - off); return; }
            }
            throw new ArgumentOutOfRangeException(nameof(value), "JPEG XL: value not representable in U32 field");
        }

        public void WriteU64(ulong v)
        {
            if (v == 0) { Write(2, 0); return; }
            if (v <= 16) { Write(2, 1); Write(4, v - 1); return; }
            if (v <= 272) { Write(2, 2); Write(8, v - 17); return; }
            Write(2, 3);
            Write(12, v & 0xFFF);
            v >>= 12;
            int shift = 12;
            while (v != 0)
            {
                WriteBool(true);
                if (shift == 60) { Write(4, v & 0xF); return; }
                Write(8, v & 0xFF);
                v >>= 8;
                shift += 8;
            }
            WriteBool(false);
        }

        public void WriteEnum(int v) => WriteU32((uint)v, JxlBitReader.Val(0), JxlBitReader.Val(1), JxlBitReader.BitsOffset(4, 2), JxlBitReader.BitsOffset(6, 18));

        public void WriteF16(float f)
        {
            ushort h = BitConverter.HalfToUInt16Bits((Half)f);
            if ((h & 0x7C00) == 0x7C00) throw new ArgumentOutOfRangeException(nameof(f), "JPEG XL: value not representable as half float");
            Write(16, h);
        }

        /// <summary>Appends the bits of another writer (any bit alignment).</summary>
        public void Append(JxlBitWriter o)
        {
            var data = o.buf;
            int full = o.len;
            if (nacc == 0)
            {
                Reserve(full);
                Buffer.BlockCopy(data, 0, buf, len, full);
                len += full;
            }
            else
            {
                int i = 0;
                for (; i + 4 <= full; i += 4) Write(32, Unsafe.ReadUnaligned<uint>(ref data[i]));
                for (; i < full; i++) Write(8, data[i]);
            }
            if (o.nacc > 0) Write(o.nacc, o.acc);
        }

        /// <summary>Appends whole bytes; the writer must be byte aligned.</summary>
        public void AppendBytes(byte[] data)
        {
            if ((nacc & 7) != 0) throw new InvalidOperationException("JPEG XL encoder: unaligned byte append");
            while (nacc > 0)
            {
                Reserve(1);
                buf[len++] = (byte)acc;
                acc >>= 8;
                nacc -= 8;
            }
            acc = 0;
            Reserve(data.Length);
            Buffer.BlockCopy(data, 0, buf, len, data.Length);
            len += data.Length;
        }

        void Reserve(int extra)
        {
            if (len + extra + 8 > buf.Length) Array.Resize(ref buf, Math.Max(buf.Length * 2, len + extra + 64));
        }

        /// <summary>Byte aligned bytes (pads with zeros).</summary>
        public byte[] ToArray()
        {
            ZeroPadToByte();
            while (nacc > 0)
            {
                Reserve(1);
                buf[len++] = (byte)acc;
                acc >>= 8;
                nacc -= 8;
            }
            nacc = 0; acc = 0;
            var r = new byte[len];
            Buffer.BlockCopy(buf, 0, r, 0, len);
            return r;
        }

        /// <summary>Writes the byte aligned content to a span-consuming sink without copying the internal buffer twice.</summary>
        public void CopyTo(System.IO.Stream s)
        {
            var a = ToArray();
            s.Write(a, 0, a.Length);
        }

        public static int UnsignedOf(int v) => v >= 0 ? 2 * v : -2 * v - 1;
        public static uint PackSigned(int v) => (uint)((v << 1) ^ (v >> 31));
        public static ulong PackSigned64(long v) => (ulong)((v << 1) ^ (v >> 63));
    }
}
