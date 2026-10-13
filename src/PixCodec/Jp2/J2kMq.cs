using System;
using System.Runtime.CompilerServices;

namespace PixCodec
{
    /// <summary>Tables shared by the MQ decoder and encoder and by the Tier-1 context modelling (T.800 Annex C and D).</summary>
    internal static class J2kT1
    {
        // probability state table C.2: Qe, next state after MPS, next after LPS, switch
        static readonly ushort[] QeTable =
        {
            0x5601, 0x3401, 0x1801, 0x0AC1, 0x0521, 0x0221, 0x5601, 0x5401, 0x4801, 0x3801, 0x3001, 0x2401, 0x1C01, 0x1601,
            0x5601, 0x5401, 0x5101, 0x4801, 0x3801, 0x3401, 0x3001, 0x2801, 0x2401, 0x2201, 0x1C01, 0x1801, 0x1601, 0x1401,
            0x1201, 0x1101, 0x0AC1, 0x09C1, 0x08A1, 0x0521, 0x0441, 0x02A1, 0x0221, 0x0141, 0x0111, 0x0085, 0x0049, 0x0025,
            0x0015, 0x0009, 0x0005, 0x0001, 0x5601,
        };
        static readonly byte[] NmpsTable =
        {
            1, 2, 3, 4, 5, 38, 7, 8, 9, 10, 11, 12, 13, 29, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30,
            31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 45, 46,
        };
        static readonly byte[] NlpsTable =
        {
            1, 6, 9, 12, 29, 33, 6, 14, 14, 14, 17, 18, 20, 21, 14, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 23, 24, 25, 26,
            27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 46,
        };

        // Indexed by 2 * state + mps: Qe, and the combined index after coding an MPS / LPS.
        public static readonly ushort[] Qe = new ushort[94];
        public static readonly byte[] Nmps = new byte[94];
        public static readonly byte[] Nlps = new byte[94];

        public const int CtxRun = 17, CtxUni = 18, NumCtx = 19;

        // flag bits of a sample (see J2kBlockDecoder for the layout)
        public const int FW = 1, FE = 2, FN = 4, FS = 8, FNW = 16, FNE = 32, FSW = 64, FSE = 128;
        public const int FSig = 0x1000, FVisit = 0x2000, FRefined = 0x4000;

        /// <summary>Zero coding context by band orientation (0 LL, 1 HL, 2 LH, 3 HH) and the 8 neighbour significance bits.</summary>
        public static readonly byte[] Zc = new byte[4 * 256];
        /// <summary>Sign coding: context (9..13) in the low bits, xor bit in bit 7; index = sig W,E,N,S | sign W,E,N,S &lt;&lt; 4.</summary>
        public static readonly byte[] Sc = new byte[256];

        static J2kT1()
        {
            for (int i = 0; i < 47; i++)
                for (int m = 0; m < 2; m++)
                {
                    Qe[2 * i + m] = QeTable[i];
                    Nmps[2 * i + m] = (byte)(2 * NmpsTable[i] + m);
                    bool sw = i == 0 || i == 6 || i == 14;
                    Nlps[2 * i + m] = (byte)(2 * NlpsTable[i] + (sw ? 1 - m : m));
                }
            for (int o = 0; o < 4; o++)
                for (int f = 0; f < 256; f++)
                {
                    int h = (f & FW) + ((f & FE) >> 1);
                    int v = ((f & FN) >> 2) + ((f & FS) >> 3);
                    int d = ((f & FNW) >> 4) + ((f & FNE) >> 5) + ((f & FSW) >> 6) + ((f & FSE) >> 7);
                    int c;
                    if (o == 3)
                    {
                        int hv = h + v;
                        c = d >= 3 ? 8 : d == 2 ? (hv >= 1 ? 7 : 6) : d == 1 ? (hv >= 2 ? 5 : hv == 1 ? 4 : 3) : (hv >= 2 ? 2 : hv);
                    }
                    else
                    {
                        if (o == 1) (h, v) = (v, h);
                        c = h == 2 ? 8 : h == 1 ? (v >= 1 ? 7 : d >= 1 ? 6 : 5) : v == 2 ? 4 : v == 1 ? 3 : d >= 2 ? 2 : d;
                    }
                    Zc[o * 256 + f] = (byte)c;
                }
            for (int f = 0; f < 256; f++)
            {
                int Contribution(int sig, int neg) => (f & sig) == 0 ? 0 : (f & neg) != 0 ? -1 : 1;
                int h = Math.Clamp(Contribution(1, 16) + Contribution(2, 32), -1, 1);
                int v = Math.Clamp(Contribution(4, 64) + Contribution(8, 128), -1, 1);
                int x = 0;
                if (h < 0 || (h == 0 && v < 0)) { x = 1; h = -h; v = -v; }
                int c = h == 1 ? (v == 1 ? 13 : v == 0 ? 12 : 11) : (v == 0 ? 9 : 10);
                Sc[f] = (byte)(c | x << 7);
            }
        }

        public static void ResetContexts(Span<byte> cx)
        {
            cx.Clear();
            cx[0] = 4 << 1;
            cx[CtxRun] = 3 << 1;
            cx[CtxUni] = 46 << 1;
        }
    }

    /// <summary>The MQ arithmetic decoder (T.800 Annex C). The data must be followed by two 0xFF bytes.</summary>
    internal unsafe struct J2kMqDecoder
    {
        uint _a, _c;
        int _ct;
        byte* _bp;

        public void Init(byte* data)
        {
            _bp = data;
            _c = (uint)data[0] << 16;
            ByteIn();
            _c <<= 7;
            _ct -= 7;
            _a = 0x8000;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void ByteIn()
        {
            if (_bp[0] == 0xFF)
            {
                if (_bp[1] > 0x8F) { _c += 0xFF00; _ct = 8; }
                else { _bp++; _c += (uint)_bp[0] << 9; _ct = 7; }
            }
            else { _bp++; _c += (uint)_bp[0] << 8; _ct = 8; }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Decode(ref byte cx)
        {
            int s = cx;
            uint qe = J2kT1.Qe[s];
            int d = s & 1;
            _a -= qe;
            if ((_c >> 16) < qe)
            {
                if (_a < qe) cx = J2kT1.Nmps[s];
                else { d ^= 1; cx = J2kT1.Nlps[s]; }
                _a = qe;
                Renorm();
            }
            else
            {
                _c -= qe << 16;
                if ((_a & 0x8000) == 0)
                {
                    if (_a < qe) { d ^= 1; cx = J2kT1.Nlps[s]; }
                    else cx = J2kT1.Nmps[s];
                    Renorm();
                }
            }
            return d;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Renorm()
        {
            do
            {
                if (_ct == 0) ByteIn();
                _a <<= 1; _c <<= 1; _ct--;
            } while ((_a & 0x8000) == 0);
        }
    }

    /// <summary>Raw (bypass) bit reader: MSB first, a byte after 0xFF carries 7 bits. Past its end it reads ones.</summary>
    internal unsafe struct J2kRawDecoder
    {
        byte* _bp, _end;
        uint _cur;
        int _ct;

        public void Init(byte* data, int length) { _bp = data; _end = data + length; _cur = 0; _ct = 0; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Bit()
        {
            if (_ct == 0)
            {
                uint prev = _cur;
                _cur = _bp < _end ? *_bp++ : 0xFFu;
                _ct = prev == 0xFF ? 7 : 8;
            }
            _ct--;
            return (int)(_cur >> _ct) & 1;
        }
    }

    /// <summary>The MQ arithmetic encoder (T.800 Annex C).</summary>
    internal unsafe struct J2kMqEncoder
    {
        uint _a, _c;
        int _ct;
        byte* _start, _bp;   // _bp points at the byte being completed (start - 1 initially)

        /// <summary>data[-1] must be writable (it is used as scratch).</summary>
        public void Init(byte* data)
        {
            _start = data; _bp = data - 1;
            _a = 0x8000; _c = 0; _ct = 12;
            if (*_bp == 0xFF) _ct = 13;
        }

        public int Position => (int)(_bp + 1 - _start);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Encode(ref byte cx, int d)
        {
            int s = cx;
            uint qe = J2kT1.Qe[s];
            _a -= qe;
            if (d == (s & 1))
            {
                if ((_a & 0x8000) == 0)
                {
                    if (_a < qe) _a = qe; else _c += qe;
                    cx = J2kT1.Nmps[s];
                    Renorm();
                }
                else _c += qe;
            }
            else
            {
                if (_a < qe) _c += qe; else _a = qe;
                cx = J2kT1.Nlps[s];
                Renorm();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Renorm()
        {
            do
            {
                _a <<= 1; _c <<= 1; _ct--;
                if (_ct == 0) ByteOut();
            } while ((_a & 0x8000) == 0);
        }

        void ByteOut()
        {
            if (*_bp == 0xFF)
            {
                _bp++; *_bp = (byte)(_c >> 20); _c &= 0xFFFFF; _ct = 7;
            }
            else if ((_c & 0x8000000) == 0)
            {
                _bp++; *_bp = (byte)(_c >> 19); _c &= 0x7FFFF; _ct = 8;
            }
            else
            {
                (*_bp)++;
                if (*_bp == 0xFF)
                {
                    _c &= 0x7FFFFFF;
                    _bp++; *_bp = (byte)(_c >> 20); _c &= 0xFFFFF; _ct = 7;
                }
                else
                {
                    _bp++; *_bp = (byte)(_c >> 19); _c &= 0x7FFFF; _ct = 8;
                }
            }
        }

        /// <summary>
        /// Terminates the codeword (FLUSH of C.2.9) so that any decoder reads the coded decisions back, and returns
        /// the number of bytes. A trailing 0xFF is dropped as the decoder supplies it. The coder can be restarted
        /// behind the returned length with <see cref="Restart"/>.
        /// </summary>
        public int Flush()
        {
            // SETBITS
            uint t = _c + _a;
            _c |= 0xFFFF;
            if (_c >= t) _c -= 0x8000;
            _c <<= _ct; ByteOut();
            _c <<= _ct; ByteOut();
            if (*_bp != 0xFF) _bp++;
            return (int)(_bp - _start);
        }

        /// <summary>
        /// The termination of D.4.2 that the predictable termination flag asks for: exactly the bits still in the
        /// code register are pushed out, so a decoder knows what the unused bits of the last byte must be.
        /// </summary>
        public int FlushPredictable()
        {
            int k = 11 - _ct + 1;
            while (k > 0)
            {
                _c <<= _ct;
                _ct = 0;
                ByteOut();
                k -= _ct;
            }
            if (*_bp != 0xFF) _bp++;
            return (int)(_bp - _start);
        }

        /// <summary>Starts a new codeword at <paramref name="at"/> (the byte before it must be the previous segment's last byte or scratch).</summary>
        public void Restart(byte* at)
        {
            _start = at; _bp = at - 1;
            _a = 0x8000; _c = 0; _ct = 12;
            if (*_bp == 0xFF) _ct = 13;
        }
    }
}
