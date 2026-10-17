using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Aardvark.ImageCodecs.JpegXrImpl
{
    /// <summary>
    /// MSB-first bit reader (T.832 5.3.3) over a copy of the codestream with 16 zero bytes of padding, so that reads past
    /// the end of a damaged stream return zeros instead of failing.
    /// </summary>
    internal sealed class JxrBitReader
    {
        readonly byte[] _d;
        readonly int _length;
        int _pos;   // bit position

        public JxrBitReader(byte[] padded, int length, int bytePos)
        {
            _d = padded;
            _length = length;
            _pos = bytePos << 3;
        }

        /// <summary>Byte length of the codestream (without the padding).</summary>
        public int Length => _length;
        public int BitPosition => _pos;
        public bool PastEnd => _pos > _length << 3;

        public void Seek(int bytePos) => _pos = Math.Clamp(bytePos, 0, _length) << 3;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint Peek(int n)
        {
            int b = _pos >> 3;
            if ((uint)b > (uint)_length) b = _length;   // past the end: zeros from the padding
            ulong v = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref _d[b]));
            return (uint)((v << (_pos & 7)) >> (64 - n));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Skip(int n) => _pos += n;

        /// <summary>u(n) for 0 &lt;= n &lt;= 32.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Bits(int n)
        {
            if (n == 0) return 0;
            uint v = Peek(n);
            _pos += n;
            return (int)v;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Bit()
        {
            uint v = Peek(1);
            _pos++;
            return v != 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Decode(Vlc vlc)
        {
            int e = vlc.Lookup[Peek(vlc.MaxLength)];
            _pos += e >> 8;
            return e & 0xFF;
        }

        public void AlignByte() => _pos = (_pos + 7) & ~7;

        public byte Byte() => (byte)Bits(8);

        /// <summary>VLW_ESC( ) (8.2.4): 0 for the escape codes 0xFD .. 0xFF.</summary>
        public long VlwEsc()
        {
            int first = Bits(8);
            if (first < 0xFB) return first * 256 + Bits(8);
            if (first == 0xFB) return (uint)Bits(32);
            if (first == 0xFC) return (long)((ulong)(uint)Bits(32) << 32 | (uint)Bits(32));
            return 0;
        }
    }

    /// <summary>MSB-first bit writer for the encoder.</summary>
    internal sealed class JxrBitWriter
    {
        byte[] _d;
        int _n;          // bytes written
        ulong _acc;      // pending bits, left aligned
        int _bits;       // number of pending bits

        public JxrBitWriter(int capacity = 1 << 16) => _d = new byte[Math.Max(capacity, 64)];

        /// <summary>Bits written so far.</summary>
        public long BitLength => (long)_n * 8 + _bits;
        public int ByteLength => _n + ((_bits + 7) >> 3);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Bits(uint v, int n)
        {
            if (n == 0) return;
            if (_bits + n > 64) Flush();
            _acc |= (ulong)(v & (n == 32 ? 0xFFFFFFFFu : (1u << n) - 1)) << (64 - _bits - n);
            _bits += n;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Bits(int v, int n) => Bits((uint)v, n);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Bit(bool b) => Bits(b ? 1u : 0u, 1);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(Vlc vlc, int value) => Bits(vlc.Codes[value], vlc.Lengths[value]);

        void Flush()
        {
            if (_n + 8 > _d.Length) Array.Resize(ref _d, _d.Length * 2);
            while (_bits >= 8)
            {
                _d[_n++] = (byte)(_acc >> 56);
                _acc <<= 8;
                _bits -= 8;
            }
        }

        public void AlignByte()
        {
            int pad = (8 - (_bits & 7)) & 7;
            Bits(0u, pad);
            Flush();
        }

        public void Byte(int b) => Bits((uint)b & 0xFF, 8);

        public void VlwEsc(long v)
        {
            if (v < 0xFB00) { Bits((uint)v, 16); }
            else if (v <= uint.MaxValue) { Byte(0xFB); Bits((uint)v, 32); }
            else { Byte(0xFC); Bits((uint)(v >> 32), 32); Bits((uint)v, 32); }
        }

        public void WriteBytes(ReadOnlySpan<byte> s)
        {
            AlignByte();
            if (_n + s.Length > _d.Length) Array.Resize(ref _d, Math.Max(_d.Length * 2, _n + s.Length + 64));
            s.CopyTo(_d.AsSpan(_n));
            _n += s.Length;
        }

        /// <summary>The bytes written, padded with zero bits to a byte boundary.</summary>
        public ReadOnlySpan<byte> Span
        {
            get { AlignByte(); return _d.AsSpan(0, _n); }
        }
    }
}
