using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Aardvark.ImageCodecs.Av1
{
    /// <summary>
    /// The symbol (arithmetic) decoder of AV1 spec 8.2. The spec keeps a 15 bit SymbolValue that collects the inverted
    /// bitstream bits; here a 64 bit window holds that value followed by <c>cnt</c> further inverted bits, so that
    /// renormalisation is a shift of the bit count only and bytes are fetched in groups. Bits past the end of the tile
    /// data read as zero (inverted: one), as the spec pads them.
    /// </summary>
    internal sealed class Av1SymbolDecoder
    {
        byte[] buf = Array.Empty<byte>();
        int pos, end;
        ulong dif;     // SymbolValue << cnt | next cnt inverted bits
        int cnt;
        uint range;
        public bool DisableUpdate;

        public void Init(byte[] data, int offset, int size, bool disableCdfUpdate)
        {
            if (size < 1) throw new ImageFormatException("AV1: empty tile");
            buf = data; pos = offset; end = offset + size;
            DisableUpdate = disableCdfUpdate;
            dif = 0; cnt = -15;
            Refill();
            range = 1 << 15;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Refill()
        {
            // keep at least 16 window bits beyond the 15 value bits (one renormalisation shifts at most 15)
            while (cnt <= 40)
            {
                uint b = pos < end ? buf[pos] : 0u;
                pos++;
                dif = (dif << 8) | (~b & 0xFF);
                cnt += 8;
            }
        }

        /// <summary>read_symbol for a CDF of <paramref name="n"/> symbols at cdf[off .. off + n] (the last entry is the counter).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Read(ushort[] cdf, int off, int n)
        {
            ref ushort c = ref cdf[off];
            uint r8 = range >> 8;
            ulong d = dif;
            int sh = cnt;
            uint cur = range, prev;
            int symbol = -1;
            do
            {
                symbol++;
                prev = cur;
                uint f = 32768u - Unsafe.Add(ref c, symbol);
                cur = ((r8 * (f >> Av1C.EcProbShift)) >> (7 - Av1C.EcProbShift)) + (uint)(Av1C.EcMinProb * (n - symbol - 1));
            } while (d < ((ulong)cur << sh));
            uint rng = prev - cur;
            dif = d - ((ulong)cur << sh);
            int bits = BitOperations.LeadingZeroCount(rng) - 16;
            range = rng << bits;
            cnt = sh - bits;
            if (cnt < 16) Refill();
            if (!DisableUpdate) Update(ref c, n, symbol);
            return symbol;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Update(ref ushort c, int n, int symbol)
        {
            ref ushort count = ref Unsafe.Add(ref c, n);
            int rate = 3 + (count > 15 ? 1 : 0) + (count > 31 ? 1 : 0) + (n > 3 ? 2 : n > 1 ? 1 : 0);
            int i = 0;
            for (; i < symbol; i++)
            {
                ref ushort v = ref Unsafe.Add(ref c, i);
                v = (ushort)(v - (v >> rate));
            }
            for (; i < n - 1; i++)
            {
                ref ushort v = ref Unsafe.Add(ref c, i);
                v = (ushort)(v + ((32768 - v) >> rate));
            }
            if (count < 32) count++;
        }

        /// <summary>read_bool: a symbol with the fixed CDF { 1 &lt;&lt; 14, 1 &lt;&lt; 15 } (no adaptation).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Bool()
        {
            uint cur = ((range >> 8) << 7) + Av1C.EcMinProb;
            ulong c = (ulong)cur << cnt;
            int bit;
            uint rng;
            if (dif < c) { bit = 1; rng = cur; }
            else { bit = 0; rng = range - cur; dif -= c; }
            int bits = BitOperations.LeadingZeroCount(rng) - 16;
            range = rng << bits;
            cnt -= bits;
            if (cnt < 16) Refill();
            return bit;
        }

        /// <summary>read_literal( n ): L(n).</summary>
        public int Literal(int n)
        {
            int x = 0;
            for (int i = 0; i < n; i++) x = (x << 1) | Bool();
            return x;
        }

        /// <summary>NS( n ).</summary>
        public int Ns(int n)
        {
            int w = Av1BitReader.FloorLog2(n) + 1;
            int m = (1 << w) - n;
            int v = Literal(w - 1);
            if (v < m) return v;
            int extra = Literal(1);
            return (v << 1) - m + extra;
        }
    }
}
