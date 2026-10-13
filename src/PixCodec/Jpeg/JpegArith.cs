using System;
using System.Runtime.CompilerServices;

namespace PixCodec.JpegImpl
{
    /// <summary>
    /// Adaptive binary arithmetic decoder of T.81 Annex D (the "QM coder") together with the statistics models of
    /// the DCT based processes (F.1.4 sequential, G.1.3 progressive).
    /// </summary>
    internal sealed unsafe class JpegArith
    {
        // Probability state machine, T.81 Table D.3: Qe value, next state after an LPS / MPS, and whether an LPS
        // flips the sense of the more probable symbol. Entry 113 is the fixed 0.5 estimate that never adapts.
        static ReadOnlySpan<ushort> QeTable =>
        [
            0x5A1D, 0x2586, 0x1114, 0x080B, 0x03D8, 0x01DA, 0x00E5, 0x006F, 0x0036, 0x001A, 0x000D, 0x0006, 0x0003, 0x0001,
            0x5A7F, 0x3F25, 0x2CF2, 0x207C, 0x17B9, 0x1182, 0x0CEF, 0x09A1, 0x072F, 0x055C, 0x0406, 0x0303, 0x0240, 0x01B1,
            0x0144, 0x00F5, 0x00B7, 0x008A, 0x0068, 0x004E, 0x003B, 0x002C, 0x5AE1, 0x484C, 0x3A0D, 0x2EF1, 0x261F, 0x1F33,
            0x19A8, 0x1518, 0x1177, 0x0E74, 0x0BFB, 0x09F8, 0x0861, 0x0706, 0x05CD, 0x04DE, 0x040F, 0x0363, 0x02D4, 0x025C,
            0x01F8, 0x01A4, 0x0160, 0x0125, 0x00F6, 0x00CB, 0x00AB, 0x008F, 0x5B12, 0x4D04, 0x412C, 0x37D8, 0x2FE8, 0x293C,
            0x2379, 0x1EDF, 0x1AA9, 0x174E, 0x1424, 0x119C, 0x0F6B, 0x0D51, 0x0BB6, 0x0A40, 0x5832, 0x4D1C, 0x438E, 0x3BDD,
            0x34EE, 0x2EAE, 0x299A, 0x2516, 0x5570, 0x4CA9, 0x44D9, 0x3E22, 0x3824, 0x32B4, 0x2E17, 0x56A8, 0x4F46, 0x47E5,
            0x41CF, 0x3C3D, 0x375E, 0x5231, 0x4C0F, 0x4639, 0x415E, 0x5627, 0x50E7, 0x4B85, 0x5597, 0x504F, 0x5A10, 0x5522,
            0x59EB, 0x5A1D,
        ];

        static ReadOnlySpan<byte> NextLps =>
        [
            1, 14, 16, 18, 20, 23, 25, 28, 30, 33, 35, 9, 10, 12, 15, 36, 38, 39, 40, 42, 43, 45, 46, 48, 49, 51, 52, 54,
            56, 57, 59, 60, 62, 63, 32, 33, 37, 64, 65, 67, 68, 69, 70, 72, 73, 74, 75, 77, 78, 79, 48, 50, 50, 51, 52, 53,
            54, 55, 56, 57, 58, 59, 61, 61, 65, 80, 81, 82, 83, 84, 86, 87, 87, 72, 72, 74, 74, 75, 77, 77, 80, 88, 89, 90,
            91, 92, 93, 86, 88, 95, 96, 97, 99, 99, 93, 95, 101, 102, 103, 104, 99, 105, 106, 107, 103, 105, 108, 109, 110, 111, 110, 112,
            112, 113,
        ];

        static ReadOnlySpan<byte> NextMps =>
        [
            1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 13, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28,
            29, 30, 31, 32, 33, 34, 35, 9, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56,
            57, 58, 59, 60, 61, 62, 63, 32, 65, 66, 67, 68, 69, 70, 71, 72, 73, 74, 75, 76, 77, 78, 79, 48, 81, 82, 83, 84,
            85, 86, 87, 71, 89, 90, 91, 92, 93, 94, 86, 96, 97, 98, 99, 100, 93, 102, 103, 104, 99, 106, 107, 103, 109, 107, 111, 109,
            111, 113,
        ];

        // states whose LPS exchanges the MPS sense
        static ReadOnlySpan<byte> Switch => [0, 14, 36, 64, 80, 88, 95, 105, 110, 112];

        // state byte (index | mps << 7) -> Qe | next after MPS << 16 | next after LPS << 24 (both again with the mps bit)
        static readonly uint[] s_table = BuildTable();
        static readonly uint* s_tablePtr;

        static uint[] BuildTable()
        {
            var t = GC.AllocateArray<uint>(256, pinned: true);
            for (int mps = 0; mps < 2; mps++)
                for (int i = 0; i < 114; i++)
                {
                    int lpsSense = Switch.IndexOf((byte)i) >= 0 ? 1 - mps : mps;
                    t[i | mps << 7] = QeTable[i] | (uint)(NextMps[i] | mps << 7) << 16 | (uint)(NextLps[i] | lpsSense << 7) << 24;
                }
            return t;
        }

        static JpegArith()
        {
            fixed (uint* p = s_table) s_tablePtr = p;
        }

        const byte FixedState = 113;

        // per table statistics: DC 64 bins (5 contexts x 4, 15 magnitude category bins, 14 magnitude bit bins),
        // AC 256 bins (3 per coefficient, then two sets of category + bit bins for low / high frequencies)
        readonly byte[] _stats = GC.AllocateArray<byte>(4 * 64 + 4 * 256, pinned: true);
        readonly byte* _dcStats, _acStats;
        /// <summary>Conditioning from the DAC marker: DC bounds L and U, AC threshold Kx, per table.</summary>
        public readonly byte[] DcL = new byte[4], DcU = [1, 1, 1, 1], AcK = [5, 5, 5, 5];

        byte* _p, _end;
        uint _c;
        uint _a;
        int _ct;
        int _padding;
        /// <summary>Set when the code stream turned out to be inconsistent; the rest of the scan is not decodable.</summary>
        public bool Corrupt;

        public JpegArith()
        {
            fixed (byte* p = _stats) _dcStats = p;
            _acStats = _dcStats + 4 * 64;
        }

        public byte* Position => _p;

        /// <summary>Starts decoding at p with fresh statistics (scan start and after every restart marker).</summary>
        public void Start(byte* p, byte* end)
        {
            _stats.AsSpan().Clear();
            _losslessStats.AsSpan().Clear();
            _p = p; _end = end;
            _padding = 0;
            Corrupt = false;
            _c = 0;
            ByteIn(); _c <<= 8;
            ByteIn(); _c <<= 8;
            _ct = 0;
            _a = 0x10000;
        }

        void ByteIn()
        {
            uint b = _p < _end ? *_p : 0xFFu;
            if (b == 0xFF)
            {
                if (_end - _p >= 2 && _p[1] == 0) _p += 2; // stuffed zero
                else
                {
                    // A marker (or the end of the data) ends the segment. The coder may have dropped a few trailing
                    // zero bytes, so zeros are supplied - but a decoder that keeps asking has lost the stream.
                    if (++_padding > 64) Corrupt = true;
                    return;
                }
            }
            else _p++;
            _c += b << 8;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int Decode(byte* st)
        {
            uint sv = *st, e = s_tablePtr[sv], qe = e & 0xFFFF;
            int d = (int)(sv >> 7);
            _a -= qe;
            if (_c >> 16 < _a)
            {
                if (_a >= 0x8000) return d; // MPS without renormalization: the common case
                if (_a < qe) { d ^= 1; *st = (byte)(e >> 24); }
                else *st = (byte)(e >> 16);
            }
            else
            {
                _c -= _a << 16;
                if (_a < qe) *st = (byte)(e >> 16);
                else { d ^= 1; *st = (byte)(e >> 24); }
                _a = qe;
            }
            do
            {
                if (_ct == 0) { ByteIn(); _ct = 8; }
                _a <<= 1; _c <<= 1; _ct--;
            } while (_a < 0x8000);
            return d;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int DecodeFixed()
        {
            byte st = FixedState;
            return Decode(&st);
        }

        /// <summary>DC difference (F.1.4.4.1). The context for the next block depends on the size of this one.</summary>
        int DecodeDcDiff(int table, ref int context)
        {
            byte* stats = _dcStats + table * 64, st = stats + context;
            if (Decode(st) == 0) { context = 0; return 0; }
            int sign = Decode(st + 1);
            st += 2 + sign;
            int m = Decode(st);
            if (m != 0)
            {
                st = stats + 20;
                while (Decode(st) != 0)
                {
                    if ((m <<= 1) == 0x8000) { Corrupt = true; return 0; }
                    st++;
                }
            }
            if (m < (1 << DcL[table]) >> 1) context = 0;
            else if (m > (1 << DcU[table]) >> 1) context = 12 + sign * 4;
            else context = 4 + sign * 4;
            int v = m;
            st += 14;
            while ((m >>= 1) != 0) if (Decode(st) != 0) v |= m;
            v++;
            return sign != 0 ? -v : v;
        }

        // lossless (H.1.2.3): 25 contexts x 4 bins from the classes of the left and upper difference, then two sets of
        // magnitude category / magnitude bit bins (15 + 14 each)
        const int LosslessBins = 100 + 2 * 29;
        readonly byte[] _losslessStats = GC.AllocateArray<byte>(4 * LosslessBins, pinned: true);

        /// <summary>0 = zero, 1 / 2 = small positive / negative, 3 / 4 = large positive / negative.</summary>
        int Classify(int d, int table)
        {
            int a = Math.Abs(d);
            if (a <= (1 << DcL[table]) >> 1) return 0;
            return (a <= 1 << DcU[table] ? 1 : 3) + (d < 0 ? 1 : 0);
        }

        /// <summary>One prediction difference of a lossless scan, given the differences to the left (da) and above (db).</summary>
        public int DecodeLosslessDifference(int table, int da, int db)
        {
            fixed (byte* all = _losslessStats)
            {
                byte* stats = all + table * LosslessBins;
                int ca = Classify(da, table), cb = Classify(db, table);
                byte* st = stats + 4 * (LosslessContextOrder == 0 ? 5 * ca + cb : 5 * cb + ca);
                if (Decode(st) == 0) return 0;
                int sign = Decode(st + 1);
                st += 2 + sign;
                int m = Decode(st);
                if (m != 0)
                {
                    st = stats + ((LosslessLargeFrom == 0 ? cb : ca) >= 3 ? 129 : 100);
                    while (Decode(st) != 0)
                    {
                        if (m == 0x8000) { Corrupt = true; return 0; }
                        m <<= 1;
                        st++;
                    }
                }
                int v = m;
                st += 14;
                while ((m >>= 1) != 0) if (Decode(st) != 0) v |= m;
                v++;
                return sign != 0 ? -v : v;
            }
        }
        internal static int LosslessContextOrder, LosslessLargeFrom;

        /// <summary>Magnitude and sign of a nonzero AC coefficient at zigzag position k (F.1.4.4.2), after its zero run.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int DecodeAcValue(byte* stats, byte* st, int k, int kx)
        {
            int sign = DecodeFixed();
            st += 2;
            int m = Decode(st);
            if (m != 0 && Decode(st) != 0)
            {
                m <<= 1;
                st = stats + (k <= kx ? 189 : 217);
                while (Decode(st) != 0)
                {
                    if ((m <<= 1) == 0x8000) { Corrupt = true; return 0; }
                    st++;
                }
            }
            int v = m;
            st += 14;
            while ((m >>= 1) != 0) if (Decode(st) != 0) v |= m;
            v++;
            return sign != 0 ? -v : v;
        }

        /// <summary>Sequential block into natural order; returns the zigzag index of the last coefficient.</summary>
        public int DecodeBlock(JpegComponent c, short* block)
        {
            c.DcPred += DecodeDcDiff(c.DcTableId, ref c.DcContext);
            block[0] = (short)c.DcPred;
            byte* zz = JpegTables.ZigZag;
            byte* stats = _acStats + c.AcTableId * 256;
            int kx = AcK[c.AcTableId], last = 0;
            for (int k = 1; k <= 63 && !Corrupt; k++)
            {
                byte* st = stats + 3 * (k - 1);
                if (Decode(st) != 0) break; // end of block
                while (Decode(st + 1) == 0)
                {
                    st += 3;
                    if (++k > 63) { Corrupt = true; return last; }
                }
                block[zz[k]] = (short)DecodeAcValue(stats, st, k, kx);
                last = k;
            }
            return last;
        }

        public void DecodeDcFirst(JpegComponent c, short* blk, int al)
        {
            c.DcPred += DecodeDcDiff(c.DcTableId, ref c.DcContext);
            blk[0] = (short)(c.DcPred << al);
        }

        public void DecodeDcRefine(short* blk, int al)
        {
            if (DecodeFixed() != 0) blk[0] |= (short)(1 << al);
        }

        /// <summary>Progressive first pass over a band; blk is in zigzag order.</summary>
        public void DecodeAcFirst(JpegComponent c, short* blk, int ss, int se, int al)
        {
            byte* stats = _acStats + c.AcTableId * 256;
            int kx = AcK[c.AcTableId];
            for (int k = ss; k <= se && !Corrupt; k++)
            {
                byte* st = stats + 3 * (k - 1);
                if (Decode(st) != 0) break;
                while (Decode(st + 1) == 0)
                {
                    st += 3;
                    if (++k > se) { Corrupt = true; return; }
                }
                blk[k] = (short)(DecodeAcValue(stats, st, k, kx) << al);
            }
        }

        /// <summary>Progressive refinement of a band (G.1.3.3); blk is in zigzag order.</summary>
        public void DecodeAcRefine(JpegComponent c, short* blk, int ss, int se, int al)
        {
            byte* stats = _acStats + c.AcTableId * 256;
            int plus = 1 << al, minus = -1 << al;
            // end of block of the earlier passes: the end-of-block decision is only coded beyond it
            int kex = se;
            while (kex > 0 && blk[kex] == 0) kex--;
            for (int k = ss; k <= se; k++)
            {
                byte* st = stats + 3 * (k - 1);
                if (k > kex && Decode(st) != 0) break;
                while (true)
                {
                    if (blk[k] != 0)
                    {
                        if (Decode(st + 2) != 0) blk[k] += (short)(blk[k] < 0 ? minus : plus);
                        break;
                    }
                    if (Decode(st + 1) != 0)
                    {
                        blk[k] = (short)(DecodeFixed() != 0 ? minus : plus);
                        break;
                    }
                    st += 3;
                    if (++k > se) { Corrupt = true; return; }
                }
            }
        }
    }
}
