using System;
using System.Buffers.Binary;

namespace Aardvark.ImageCodecs.DdsImpl
{
    /// <summary>
    /// BC6H and BC7 (BPTC) block decoding, written from Microsoft's BC6H / BC7 format documentation and the Khronos Data
    /// Format Specification; both describe bit exact results. The partition and anchor tables are the specification's
    /// (see THIRD-PARTY-NOTICES.md).
    /// </summary>
    internal static class Bptc
    {
        // ------------------------------------------------------------------ tables (Khronos Data Format Specification, BPTC)

        // 2 subsets: bit i = subset of texel i
        internal static readonly ushort[] Partition2 =
        {
            0xCCCC, 0x8888, 0xEEEE, 0xECC8, 0xC880, 0xFEEC, 0xFEC8, 0xEC80,
            0xC800, 0xFFEC, 0xFE80, 0xE800, 0xFFE8, 0xFF00, 0xFFF0, 0xF000,
            0xF710, 0x008E, 0x7100, 0x08CE, 0x008C, 0x7310, 0x3100, 0x8CCE,
            0x088C, 0x3110, 0x6666, 0x366C, 0x17E8, 0x0FF0, 0x718E, 0x399C,
            0xAAAA, 0xF0F0, 0x5A5A, 0x33CC, 0x3C3C, 0x55AA, 0x9696, 0xA55A,
            0x73CE, 0x13C8, 0x324C, 0x3BDC, 0x6996, 0xC33C, 0x9966, 0x0660,
            0x0272, 0x04E4, 0x4E40, 0x2720, 0xC936, 0x936C, 0x39C6, 0x639C,
            0x9336, 0x9CC6, 0x817E, 0xE718, 0xCCF0, 0x0FCC, 0x7744, 0xEE22,
        };
        // 3 subsets: 2 bits per texel, texel i at bits 2i
        internal static readonly uint[] Partition3 =
        {
            0xAA685050, 0x6A5A5040, 0x5A5A4200, 0x5450A0A8, 0xA5A50000, 0xA0A05050, 0x5555A0A0, 0x5A5A5050,
            0xAA550000, 0xAA555500, 0xAAAA5500, 0x90909090, 0x94949494, 0xA4A4A4A4, 0xA9A59450, 0x2A0A4250,
            0xA5945040, 0x0A425054, 0xA5A5A500, 0x55A0A0A0, 0xA8A85454, 0x6A6A4040, 0xA4A45000, 0x1A1A0500,
            0x0050A4A4, 0xAAA59090, 0x14696914, 0x69691400, 0xA08585A0, 0xAA821414, 0x50A4A450, 0x6A5A0200,
            0xA9A58000, 0x5090A0A8, 0xA8A09050, 0x24242424, 0x00AA5500, 0x24924924, 0x24499224, 0x50A50A50,
            0x500AA550, 0xAAAA4444, 0x66660000, 0xA5A0A5A0, 0x50A050A0, 0x69286928, 0x44AAAA44, 0x66666600,
            0xAA444444, 0x54A854A8, 0x95809580, 0x96969600, 0xA85454A8, 0x80959580, 0xAA141414, 0x96960000,
            0xAAAA1414, 0xA05050A0, 0xA0A5A5A0, 0x96000000, 0x40804080, 0xA9A8A9A8, 0xAAAAAA44, 0x2A4A5254,
        };
        internal static readonly byte[] Anchor2 =
        {
            15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15,
            15, 2, 8, 2, 2, 8, 8, 15, 2, 8, 2, 2, 8, 8, 2, 2,
            15, 15, 6, 8, 2, 8, 15, 15, 2, 8, 2, 2, 2, 15, 15, 6,
            6, 2, 6, 8, 15, 15, 2, 2, 15, 15, 15, 15, 15, 2, 2, 15,
        };
        internal static readonly byte[] Anchor3Second =
        {
            3, 3, 15, 15, 8, 3, 15, 15, 8, 8, 6, 6, 6, 5, 3, 3,
            3, 3, 8, 15, 3, 3, 6, 10, 5, 8, 8, 6, 8, 5, 15, 15,
            8, 15, 3, 5, 6, 10, 8, 15, 15, 3, 15, 5, 15, 15, 15, 15,
            3, 15, 5, 5, 5, 8, 5, 10, 5, 10, 8, 13, 15, 12, 3, 3,
        };
        internal static readonly byte[] Anchor3Third =
        {
            15, 8, 8, 3, 15, 15, 3, 8, 15, 15, 15, 15, 15, 15, 15, 8,
            15, 8, 15, 3, 15, 8, 15, 8, 3, 15, 6, 10, 15, 15, 10, 8,
            15, 3, 15, 10, 10, 8, 9, 10, 6, 15, 8, 15, 3, 6, 6, 8,
            15, 3, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 3, 15, 15, 8,
        };

        internal static readonly byte[] W2 = { 0, 21, 43, 64 };
        internal static readonly byte[] W3 = { 0, 9, 18, 27, 37, 46, 55, 64 };
        internal static readonly byte[] W4 = { 0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64 };

        internal static ReadOnlySpan<byte> Weights(int bits) => bits == 2 ? W2 : bits == 3 ? W3 : W4;

        /// <summary>Reads the bits of a 128 bit block, least significant first.</summary>
        ref struct Bits
        {
            readonly ulong lo, hi;
            public int Pos;
            public Bits(ReadOnlySpan<byte> b) { lo = BinaryPrimitives.ReadUInt64LittleEndian(b); hi = BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(8)); Pos = 0; }
            public int Read(int n)
            {
                if (n == 0) return 0;
                ulong v = Pos >= 64 ? hi >> (Pos - 64) : Pos + n <= 64 ? lo >> Pos : lo >> Pos | hi << (64 - Pos);
                Pos += n;
                return (int)(v & ((1UL << n) - 1));
            }
        }

        // ------------------------------------------------------------------ BC7

        // mode: subsets, partition bits, rotation bits, index selection bit, colour bits, alpha bits, endpoint P-bits,
        // shared P-bits, index bits, second index bits
        internal static readonly byte[,] Bc7Modes =
        {
            { 3, 4, 0, 0, 4, 0, 1, 0, 3, 0 },
            { 2, 6, 0, 0, 6, 0, 0, 1, 3, 0 },
            { 3, 6, 0, 0, 5, 0, 0, 0, 2, 0 },
            { 2, 6, 0, 0, 7, 0, 1, 0, 2, 0 },
            { 1, 0, 2, 1, 5, 6, 0, 0, 2, 3 },
            { 1, 0, 2, 0, 7, 8, 0, 0, 2, 2 },
            { 1, 0, 0, 0, 7, 7, 1, 0, 4, 0 },
            { 2, 6, 0, 0, 5, 5, 1, 0, 2, 0 },
        };

        /// <summary>16 RGBA texels (row-major) of a BC7 block.</summary>
        public static void Bc7(ReadOnlySpan<byte> b, Span<byte> o)
        {
            if (b[0] == 0) { o.Slice(0, 64).Clear(); return; }   // reserved mode 8: all zero
            int mode = System.Numerics.BitOperations.TrailingZeroCount(b[0]);
            var r = new Bits(b) { Pos = mode + 1 };
            int ns = Bc7Modes[mode, 0], cb = Bc7Modes[mode, 4], ab = Bc7Modes[mode, 5];
            bool epb = Bc7Modes[mode, 6] != 0, spb = Bc7Modes[mode, 7] != 0;
            int ib = Bc7Modes[mode, 8], ib2 = Bc7Modes[mode, 9];
            int part = r.Read(Bc7Modes[mode, 1]);
            int rotation = r.Read(Bc7Modes[mode, 2]);
            int idxMode = r.Read(Bc7Modes[mode, 3]);

            Span<int> ep = stackalloc int[6 * 4];   // endpoint e, channel c at e * 4 + c
            int ne = ns * 2;
            for (int c = 0; c < 3; c++)
                for (int e = 0; e < ne; e++) ep[e * 4 + c] = r.Read(cb);
            for (int e = 0; e < ne; e++) ep[e * 4 + 3] = ab > 0 ? r.Read(ab) : 0;
            int cbits = cb, abits = ab;
            if (epb)
            {
                for (int e = 0; e < ne; e++)
                {
                    int p = r.Read(1);
                    for (int c = 0; c < 4; c++) ep[e * 4 + c] = ep[e * 4 + c] << 1 | p;
                }
                cbits++;
                if (ab > 0) abits++;
            }
            else if (spb)
            {
                for (int s = 0; s < ns; s++)
                {
                    int p = r.Read(1);
                    for (int e = 2 * s; e < 2 * s + 2; e++)
                        for (int c = 0; c < 3; c++) ep[e * 4 + c] = ep[e * 4 + c] << 1 | p;
                }
                cbits++;
            }
            for (int e = 0; e < ne; e++)
            {
                for (int c = 0; c < 3; c++) ep[e * 4 + c] = Expand(ep[e * 4 + c], cbits);
                ep[e * 4 + 3] = ab > 0 ? Expand(ep[e * 4 + 3], abits) : 255;
            }

            Span<byte> subset = stackalloc byte[16];
            int a1 = 0, a2 = 0;   // anchors of subsets 1 and 2
            if (ns == 2) { ushort m = Partition2[part]; for (int i = 0; i < 16; i++) subset[i] = (byte)(m >> i & 1); a1 = Anchor2[part]; }
            else if (ns == 3) { uint m = Partition3[part]; for (int i = 0; i < 16; i++) subset[i] = (byte)(m >> (2 * i) & 3); a1 = Anchor3Second[part]; a2 = Anchor3Third[part]; }
            else subset.Clear();

            Span<byte> idx = stackalloc byte[16];
            Span<byte> idx2 = stackalloc byte[16];
            for (int i = 0; i < 16; i++)
            {
                bool anchor = i == 0 || ns >= 2 && i == a1 || ns == 3 && i == a2;
                idx[i] = (byte)r.Read(anchor ? ib - 1 : ib);
            }
            if (ib2 > 0)
                for (int i = 0; i < 16; i++) idx2[i] = (byte)r.Read(i == 0 ? ib2 - 1 : ib2);

            // colour indices / bits, alpha indices / bits
            bool swap = idxMode == 1;
            var wc = Weights(ib2 > 0 && swap ? ib2 : ib);
            var wa = Weights(ib2 > 0 ? (swap ? ib : ib2) : ib);
            for (int i = 0; i < 16; i++)
            {
                int s = subset[i] * 2;
                int ci = ib2 > 0 && swap ? idx2[i] : idx[i];
                int ai = ib2 > 0 ? (swap ? idx[i] : idx2[i]) : idx[i];
                int w = wc[ci], wA = wa[ai];
                var px = o.Slice(i * 4, 4);
                for (int c = 0; c < 3; c++) px[c] = (byte)(((64 - w) * ep[s * 4 + c] + w * ep[(s + 1) * 4 + c] + 32) >> 6);
                px[3] = (byte)(((64 - wA) * ep[s * 4 + 3] + wA * ep[(s + 1) * 4 + 3] + 32) >> 6);
                if (rotation != 0) (px[rotation - 1], px[3]) = (px[3], px[rotation - 1]);
            }
        }

        internal static int Expand(int v, int bits) { v <<= 8 - bits; return v | v >> bits; }

        // ------------------------------------------------------------------ BC6H

        internal sealed class Bc6Mode
        {
            public int Prec, Regions;
            public int[] Delta = new int[3];
            public bool Transformed;
            public (int field, int bit)[] Layout = Array.Empty<(int, int)>();
            public int ModeBits;
        }

        // fields: rw gw bw rx gx bx ry gy by rz gz bz d (endpoint value of channel c: A0 = c, B0 = 3 + c, A1 = 6 + c, B1 = 9 + c)
        internal static readonly string[] FieldNames = { "rw", "gw", "bw", "rx", "gx", "bx", "ry", "gy", "by", "rz", "gz", "bz", "d" };

        // bit layouts after the mode bits, in reading order ("a..b": bits a to b, "rw:15..10" reads bit 15 first), from
        // the endpoint format table of the BC6H documentation
        internal static readonly (int code, int modeBits, int prec, int dr, int dg, int db, bool transformed, int regions, string layout)[] Bc6Defs =
        {
            (0x00, 2, 10, 5, 5, 5, true, 2, "gy:4 by:4 bz:4 rw:0..9 gw:0..9 bw:0..9 rx:0..4 gz:4 gy:0..3 gx:0..4 bz:0 gz:0..3 bx:0..4 bz:1 by:0..3 ry:0..4 bz:2 rz:0..4 bz:3 d:0..4"),
            (0x01, 2, 7, 6, 6, 6, true, 2, "gy:5 gz:4 gz:5 rw:0..6 bz:0 bz:1 by:4 gw:0..6 by:5 bz:2 gy:4 bw:0..6 bz:3 bz:5 bz:4 rx:0..5 gy:0..3 gx:0..5 gz:0..3 bx:0..5 by:0..3 ry:0..5 rz:0..5 d:0..4"),
            (0x02, 5, 11, 5, 4, 4, true, 2, "rw:0..9 gw:0..9 bw:0..9 rx:0..4 rw:10 gy:0..3 gx:0..3 gw:10 bz:0 gz:0..3 bx:0..3 bw:10 bz:1 by:0..3 ry:0..4 bz:2 rz:0..4 bz:3 d:0..4"),
            (0x06, 5, 11, 4, 5, 4, true, 2, "rw:0..9 gw:0..9 bw:0..9 rx:0..3 rw:10 gz:4 gy:0..3 gx:0..4 gw:10 gz:0..3 bx:0..3 bw:10 bz:1 by:0..3 ry:0..3 bz:0 bz:2 rz:0..3 gy:4 bz:3 d:0..4"),
            (0x0A, 5, 11, 4, 4, 5, true, 2, "rw:0..9 gw:0..9 bw:0..9 rx:0..3 rw:10 by:4 gy:0..3 gx:0..3 gw:10 bz:0 gz:0..3 bx:0..4 bw:10 by:0..3 ry:0..3 bz:1 bz:2 rz:0..3 bz:4 bz:3 d:0..4"),
            (0x0E, 5, 9, 5, 5, 5, true, 2, "rw:0..8 by:4 gw:0..8 gy:4 bw:0..8 bz:4 rx:0..4 gz:4 gy:0..3 gx:0..4 bz:0 gz:0..3 bx:0..4 bz:1 by:0..3 ry:0..4 bz:2 rz:0..4 bz:3 d:0..4"),
            (0x12, 5, 8, 6, 5, 5, true, 2, "rw:0..7 gz:4 by:4 gw:0..7 bz:2 gy:4 bw:0..7 bz:3 bz:4 rx:0..5 gy:0..3 gx:0..4 bz:0 gz:0..3 bx:0..4 bz:1 by:0..3 ry:0..5 rz:0..5 d:0..4"),
            (0x16, 5, 8, 5, 6, 5, true, 2, "rw:0..7 bz:0 by:4 gw:0..7 gy:5 gy:4 bw:0..7 gz:5 bz:4 rx:0..4 gz:4 gy:0..3 gx:0..5 gz:0..3 bx:0..4 bz:1 by:0..3 ry:0..4 bz:2 rz:0..4 bz:3 d:0..4"),
            (0x1A, 5, 8, 5, 5, 6, true, 2, "rw:0..7 bz:1 by:4 gw:0..7 by:5 gy:4 bw:0..7 bz:5 bz:4 rx:0..4 gz:4 gy:0..3 gx:0..4 bz:0 gz:0..3 bx:0..5 by:0..3 ry:0..4 bz:2 rz:0..4 bz:3 d:0..4"),
            (0x1E, 5, 6, 6, 6, 6, false, 2, "rw:0..5 gz:4 bz:0 bz:1 by:4 gw:0..5 gy:5 by:5 bz:2 gy:4 bw:0..5 gz:5 bz:3 bz:5 bz:4 rx:0..5 gy:0..3 gx:0..5 gz:0..3 bx:0..5 by:0..3 ry:0..5 rz:0..5 d:0..4"),
            (0x03, 5, 10, 10, 10, 10, false, 1, "rw:0..9 gw:0..9 bw:0..9 rx:0..9 gx:0..9 bx:0..9"),
            (0x07, 5, 11, 9, 9, 9, true, 1, "rw:0..9 gw:0..9 bw:0..9 rx:0..8 rw:10 gx:0..8 gw:10 bx:0..8 bw:10"),
            (0x0B, 5, 12, 8, 8, 8, true, 1, "rw:0..9 gw:0..9 bw:0..9 rx:0..7 rw:11..10 gx:0..7 gw:11..10 bx:0..7 bw:11..10"),
            (0x0F, 5, 16, 4, 4, 4, true, 1, "rw:0..9 gw:0..9 bw:0..9 rx:0..3 rw:15..10 gx:0..3 gw:15..10 bx:0..3 bw:15..10"),
        };

        internal static readonly Bc6Mode?[] Bc6Modes = BuildBc6Modes();

        static Bc6Mode?[] BuildBc6Modes()
        {
            var modes = new Bc6Mode?[32];
            foreach (var d in Bc6Defs)
            {
                var layout = new System.Collections.Generic.List<(int, int)>();
                foreach (var item in d.layout.Split(' '))
                {
                    var p = item.Split(':');
                    int f = Array.IndexOf(FieldNames, p[0]);
                    var rng = p[1].Split("..");
                    int a = int.Parse(rng[0]), z = rng.Length > 1 ? int.Parse(rng[1]) : a;
                    for (int k = a; ; k += a <= z ? 1 : -1)
                    {
                        layout.Add((f, k));
                        if (k == z) break;
                    }
                }
                if (d.modeBits + layout.Count != (d.regions == 2 ? 82 : 65)) throw new InvalidOperationException("BC6H layout table");
                var m = new Bc6Mode { Prec = d.prec, Regions = d.regions, Transformed = d.transformed, Layout = layout.ToArray(), ModeBits = d.modeBits };
                m.Delta[0] = d.dr; m.Delta[1] = d.dg; m.Delta[2] = d.db;
                modes[d.code] = m;   // two bit codes (0, 1) are looked up by those two bits alone
            }
            return modes;
        }

        /// <summary>16 RGB texels of a BC6H block as half float bit patterns (3 per texel, row-major).</summary>
        public static void Bc6h(ReadOnlySpan<byte> b, Span<ushort> o, bool signed)
        {
            int m2 = b[0] & 3;
            var mode = Bc6Modes[m2 < 2 ? m2 : b[0] & 31];
            if (mode == null) { o.Slice(0, 48).Clear(); return; }   // reserved modes: zero
            var r = new Bits(b) { Pos = mode.ModeBits };
            Span<int> f = stackalloc int[13];
            foreach (var (field, bit) in mode.Layout) f[field] |= r.Read(1) << bit;
            int part = f[12];

            Span<int> e = stackalloc int[12];   // A0 rgb, B0 rgb, A1 rgb, B1 rgb
            for (int i = 0; i < 12; i++) e[i] = f[i];
            int prec = mode.Prec, ne = mode.Regions * 2;
            for (int c = 0; c < 3; c++)
            {
                if (signed) e[c] = SignExtend(e[c], prec);
                if (mode.Transformed || signed)
                    for (int k = 1; k < ne; k++) e[k * 3 + c] = SignExtend(e[k * 3 + c], mode.Delta[c]);
                if (mode.Transformed)
                    for (int k = 1; k < ne; k++)
                    {
                        e[k * 3 + c] = e[k * 3 + c] + e[c] & (1 << prec) - 1;
                        if (signed) e[k * 3 + c] = SignExtend(e[k * 3 + c], prec);
                    }
            }
            for (int i = 0; i < ne * 3; i++) e[i] = Unquantize(e[i], prec, signed);

            int ib = mode.Regions == 2 ? 3 : 4;
            int anchor = mode.Regions == 2 ? Anchor2[part] : 0;
            ushort pm = mode.Regions == 2 ? Partition2[part] : (ushort)0;
            var w = Weights(ib);
            r.Pos = mode.Regions == 2 ? 82 : 65;
            for (int i = 0; i < 16; i++)
            {
                int index = r.Read(i == 0 || i == anchor && mode.Regions == 2 ? ib - 1 : ib);
                int s = (pm >> i & 1) * 6;
                for (int c = 0; c < 3; c++)
                {
                    int v = (e[s + c] * (64 - w[index]) + e[s + 3 + c] * w[index] + 32) >> 6;
                    o[i * 3 + c] = Finish(v, signed);
                }
            }
        }

        internal static int SignExtend(int v, int bits) => v << (32 - bits) >> (32 - bits);

        internal static int Unquantize(int comp, int bits, bool signed)
        {
            if (!signed)
            {
                if (bits >= 15) return comp;
                if (comp == 0) return 0;
                if (comp == (1 << bits) - 1) return 0xFFFF;
                return ((comp << 16) + 0x8000) >> bits;
            }
            if (bits >= 16) return comp;
            bool neg = comp < 0;
            if (neg) comp = -comp;
            int unq = comp == 0 ? 0 : comp >= (1 << (bits - 1)) - 1 ? 0x7FFF : ((comp << 15) + 0x4000) >> (bits - 1);
            return neg ? -unq : unq;
        }

        internal static ushort Finish(int comp, bool signed)
        {
            if (!signed) return (ushort)((comp * 31) >> 6);
            comp = comp < 0 ? -(((-comp) * 31) >> 5) : (comp * 31) >> 5;
            return comp < 0 ? (ushort)(0x8000 | -comp) : (ushort)comp;
        }
    }
}
