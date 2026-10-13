using System;
using System.Collections.Generic;

namespace PixCodec
{
    /// <summary>Image and tile size (SIZ marker segment).</summary>
    internal sealed class J2kSiz
    {
        public int Rsiz;
        public long X1, Y1, X0, Y0;          // Xsiz, Ysiz, XOsiz, YOsiz
        public long TileW, TileH, TileX0, TileY0;
        public int NumComps;
        public int[] Prec = Array.Empty<int>();
        public bool[] Signed = Array.Empty<bool>();
        public int[] Dx = Array.Empty<int>(), Dy = Array.Empty<int>();
        public int TilesX, TilesY;
        public int MaxPrec;

        public long Width => X1 - X0;
        public long Height => Y1 - Y0;
    }

    /// <summary>Coding style of one tile-component (COD / COC).</summary>
    internal sealed class J2kCod
    {
        public int Levels;                   // number of decomposition levels N_L
        public int CbW, CbH;                 // code-block size exponents
        public int Style;                    // code-block style flags
        public bool Reversible;
        public int[] PPx = Array.Empty<int>(), PPy = Array.Empty<int>(); // per resolution level

        public const int Bypass = 1, Reset = 2, TermAll = 4, Causal = 8, PredTerm = 16, SegSym = 32, Ht = 64, HtMixed = 128;
    }

    /// <summary>Quantization of one tile-component (QCD / QCC).</summary>
    internal sealed class J2kQcd
    {
        public int Style;                    // 0 none, 1 scalar derived, 2 scalar expounded
        public int Guard;
        public int[] Exp = Array.Empty<int>();
        public int[] Mant = Array.Empty<int>();
    }

    internal struct J2kPoc
    {
        public int ResStart, CompStart, LayerEnd, ResEnd, CompEnd, Order;
    }

    /// <summary>Everything a tile (or the main header as the default for all tiles) can set.</summary>
    internal sealed class J2kParams
    {
        public bool HasCod;
        public int Order, Layers, Mct;
        public bool Sop, Eph;
        public J2kCod? Cod;
        public J2kCod?[] Coc;
        public J2kQcd? Qcd;
        public J2kQcd?[] Qcc;
        public int[] Roi;                    // -1 = not set here
        public List<J2kPoc>? Poc;

        public J2kParams(int comps)
        {
            Coc = new J2kCod?[comps];
            Qcc = new J2kQcd?[comps];
            Roi = new int[comps];
            Array.Fill(Roi, -1);
        }
    }

    internal struct J2kTilePart
    {
        public int Start, End;               // packet data
        public int PpmIndex;                 // index of this tile-part in codestream order (for PPM)
    }

    internal sealed class J2kTileHeader
    {
        public J2kParams? Params;
        public List<J2kTilePart> Parts = new List<J2kTilePart>();
        public List<(int z, int start, int length, int part)>? Ppt;
        public int NextPart;
    }

    /// <summary>Marker level structure of a codestream: main header, tile-part index.</summary>
    internal sealed unsafe class J2kCodestream
    {
        public const int SOC = 0xFF4F, CAP = 0xFF50, SIZ = 0xFF51, COD = 0xFF52, COC = 0xFF53, TLM = 0xFF55, PRF = 0xFF56,
            PLM = 0xFF57, PLT = 0xFF58, CPF = 0xFF59, QCD = 0xFF5C, QCC = 0xFF5D, RGN = 0xFF5E, POC = 0xFF5F, PPM = 0xFF60,
            PPT = 0xFF61, CRG = 0xFF63, COM = 0xFF64, SOT = 0xFF90, SOP = 0xFF91, EPH = 0xFF92, SOD = 0xFF93, EOC = 0xFFD9;

        public readonly byte* Data;
        public readonly int Length;
        public J2kSiz Siz = new J2kSiz();
        public J2kParams Main = null!;
        public bool IsHt;                    // CAP marker announces Part 15
        public J2kTileHeader?[] Tiles = Array.Empty<J2kTileHeader?>();
        public List<(int z, int start, int length)>? Ppm;
        public int MainHeaderEnd;
        /// <summary>Set when the tile-part chain ends early or a tile-part claims more data than there is.</summary>
        public bool Truncated;
        public int TilePartCount;

        public J2kCodestream(byte* data, int length) { Data = data; Length = length; }

        public static ImageFormatException Bad(string what) => new ImageFormatException("invalid JPEG 2000 codestream: " + what);

        int U8(int pos) => pos < Length ? Data[pos] : throw Bad("truncated marker segment");
        int U16(int pos) => pos + 2 <= Length ? Data[pos] << 8 | Data[pos + 1] : throw Bad("truncated marker segment");
        long U32(int pos) => pos + 4 <= Length ? (long)((uint)Data[pos] << 24 | (uint)Data[pos + 1] << 16 | (uint)Data[pos + 2] << 8 | Data[pos + 3]) : throw Bad("truncated marker segment");

        static long CeilDiv(long a, long b) => (a + b - 1) / b;

        /// <summary>Parses SOC and SIZ only (what GetInfo needs). Returns the position after SIZ.</summary>
        public int ParseSiz()
        {
            if (Length < 4 || U16(0) != SOC) throw Bad("missing SOC");
            int pos = 2;
            if (U16(pos) != SIZ) throw Bad("SIZ must follow SOC");
            int len = U16(pos + 2);
            if (len < 41 || pos + 2 + len > Length) throw Bad("truncated SIZ");
            int p = pos + 4;
            var s = Siz;
            s.Rsiz = U16(p);
            s.X1 = U32(p + 2); s.Y1 = U32(p + 6); s.X0 = U32(p + 10); s.Y0 = U32(p + 14);
            s.TileW = U32(p + 18); s.TileH = U32(p + 22); s.TileX0 = U32(p + 26); s.TileY0 = U32(p + 30);
            s.NumComps = U16(p + 34);
            if (s.NumComps < 1 || s.NumComps > 16384) throw Bad("bad component count");
            if (len != 38 + 3 * s.NumComps) throw Bad("bad SIZ length");
            if (s.X1 <= s.X0 || s.Y1 <= s.Y0) throw Bad("empty image area");
            if (s.TileW == 0 || s.TileH == 0) throw Bad("zero tile size");
            if (s.TileX0 > s.X0 || s.TileY0 > s.Y0 || s.TileX0 + s.TileW <= s.X0 || s.TileY0 + s.TileH <= s.Y0)
                throw Bad("bad tile offset");
            s.Prec = new int[s.NumComps]; s.Signed = new bool[s.NumComps]; s.Dx = new int[s.NumComps]; s.Dy = new int[s.NumComps];
            p += 36;
            for (int c = 0; c < s.NumComps; c++, p += 3)
            {
                int ssiz = Data[p];
                s.Signed[c] = (ssiz & 0x80) != 0;
                s.Prec[c] = (ssiz & 0x7F) + 1;
                s.Dx[c] = Data[p + 1]; s.Dy[c] = Data[p + 2];
                if (s.Prec[c] > 38) throw Bad("bit depth above 38");
                if (s.Dx[c] == 0 || s.Dy[c] == 0) throw Bad("zero subsampling factor");
                // a component without a single sample has nothing to decode
                if (CeilDiv(s.X1, s.Dx[c]) <= CeilDiv(s.X0, s.Dx[c]) || CeilDiv(s.Y1, s.Dy[c]) <= CeilDiv(s.Y0, s.Dy[c]))
                    throw Bad("component without samples");
                s.MaxPrec = Math.Max(s.MaxPrec, s.Prec[c]);
            }
            long tx = CeilDiv(s.X1 - s.TileX0, s.TileW), ty = CeilDiv(s.Y1 - s.TileY0, s.TileH);
            if (tx * ty > 65535) throw Bad("more than 65535 tiles");
            s.TilesX = (int)tx; s.TilesY = (int)ty;
            return pos + 2 + len;
        }

        /// <summary>Parses the main header and indexes all tile-parts.</summary>
        public void Parse()
        {
            int pos = ParseSiz();
            Main = new J2kParams(Siz.NumComps);
            Tiles = new J2kTileHeader?[Siz.TilesX * Siz.TilesY];
            while (true)
            {
                int marker = U16(pos);
                if (marker == SOT) break;
                if (marker == EOC || marker == SOD) throw Bad("no tile-part");
                if (marker < 0xFF00) throw Bad("expected a marker in the main header");
                int len = U16(pos + 2);
                if (len < 2 || pos + 2 + len > Length) throw Bad("truncated marker segment");
                ReadSegment(marker, pos + 4, len - 2, Main, null, 0);
                pos += 2 + len;
            }
            if (!Main.HasCod || Main.Cod == null) throw Bad("missing COD");
            if (Main.Qcd == null) throw Bad("missing QCD");
            MainHeaderEnd = pos;
            IndexTileParts(pos);
        }

        void IndexTileParts(int pos)
        {
            int index = 0;
            while (pos + 2 <= Length)
            {
                int marker = Data[pos] << 8 | Data[pos + 1];
                if (marker == EOC) break;
                if (marker != SOT)
                {
                    // garbage between tile-parts: stop here, what was indexed so far is used
                    if (index == 0) throw Bad("expected SOT");
                    Truncated = true;
                    break;
                }
                if (pos + 12 > Length) { Truncated = true; break; }
                if (U16(pos + 2) != 10) throw Bad("bad SOT length");
                int tile = U16(pos + 4);
                long psot = U32(pos + 6);
                int tpsot = Data[pos + 10];
                if (tile >= Tiles.Length) throw Bad("tile index out of range");
                long end = psot == 0 ? Length : pos + psot;
                if (psot != 0 && psot < 14) throw Bad("bad tile-part length");
                if (end > Length) { end = Length; Truncated = true; }
                var th = Tiles[tile] ??= new J2kTileHeader();
                int p = pos + 12;
                bool first = th.Parts.Count == 0;
                bool sod = false;
                while (p + 2 <= end)
                {
                    marker = Data[p] << 8 | Data[p + 1];
                    if (marker == SOD) { p += 2; sod = true; break; }
                    if (marker < 0xFF00 || p + 4 > end) throw Bad("expected a marker in the tile-part header");
                    int len = Data[p + 2] << 8 | Data[p + 3];
                    if (len < 2 || p + 2 + len > end)
                    {
                        if (Truncated) break;
                        throw Bad("truncated marker segment");
                    }
                    if (marker == COD || marker == COC || marker == QCD || marker == QCC || marker == RGN || marker == POC)
                        th.Params ??= new J2kParams(Siz.NumComps);
                    ReadSegment(marker, p + 4, len - 2, th.Params!, th, th.Parts.Count);
                    p += 2 + len;
                }
                _ = first; _ = tpsot;
                if (!sod) { Truncated = true; break; }
                // a tile-part running to the end of the data may be followed by EOC
                int dataEnd = (int)end;
                if (psot == 0 && dataEnd - p >= 2 && Data[dataEnd - 2] == 0xFF && Data[dataEnd - 1] == 0xD9) dataEnd -= 2;
                th.Parts.Add(new J2kTilePart { Start = p, End = dataEnd, PpmIndex = index });
                index++;
                pos = (int)end;
            }
            if (index == 0) throw Bad("no tile-part");
            TilePartCount = index;
        }

        void ReadSegment(int marker, int p, int len, J2kParams prm, J2kTileHeader? tile, int part)
        {
            int nc = Siz.NumComps;
            int cw = nc > 256 ? 2 : 1;
            switch (marker)
            {
                case COD:
                {
                    if (len < 10) throw Bad("bad COD length");
                    int scod = Data[p];
                    prm.HasCod = true;
                    prm.Sop = (scod & 2) != 0; prm.Eph = (scod & 4) != 0;
                    prm.Order = Data[p + 1];
                    prm.Layers = Data[p + 2] << 8 | Data[p + 3];
                    prm.Mct = Data[p + 4];
                    if (prm.Order > 4) throw Bad("bad progression order");
                    if (prm.Layers == 0) throw Bad("zero layers");
                    if (prm.Mct > 1) throw new ImageNotSupportedException("JPEG 2000 Part 2 multi-component transforms are not supported");
                    prm.Cod = ReadCodingStyle(p + 5, len - 5, (scod & 1) != 0);
                    break;
                }
                case COC:
                {
                    if (len < cw + 6) throw Bad("bad COC length");
                    int c = cw == 1 ? Data[p] : Data[p] << 8 | Data[p + 1];
                    if (c >= nc) throw Bad("COC component out of range");
                    prm.Coc[c] = ReadCodingStyle(p + cw + 1, len - cw - 1, (Data[p + cw] & 1) != 0);
                    break;
                }
                case QCD:
                    prm.Qcd = ReadQuant(p, len);
                    break;
                case QCC:
                {
                    if (len < cw + 2) throw Bad("bad QCC length");
                    int c = cw == 1 ? Data[p] : Data[p] << 8 | Data[p + 1];
                    if (c >= nc) throw Bad("QCC component out of range");
                    prm.Qcc[c] = ReadQuant(p + cw, len - cw);
                    break;
                }
                case RGN:
                {
                    if (len != cw + 2) throw Bad("bad RGN length");
                    int c = cw == 1 ? Data[p] : Data[p] << 8 | Data[p + 1];
                    if (c >= nc) throw Bad("RGN component out of range");
                    if (Data[p + cw] != 0) throw new ImageNotSupportedException("JPEG 2000 region of interest style other than maxshift");
                    int shift = Data[p + cw + 1];
                    if (shift > 37) throw Bad("bad ROI shift");
                    prm.Roi[c] = shift;
                    break;
                }
                case POC:
                {
                    int entry = 5 + 2 * cw;
                    if (len % entry != 0) throw Bad("bad POC length");
                    // POC segments of one header accumulate
                    prm.Poc ??= new List<J2kPoc>();
                    for (int i = 0; i < len / entry; i++, p += entry)
                    {
                        var e = new J2kPoc { ResStart = Data[p] };
                        int q = p + 1;
                        e.CompStart = cw == 1 ? Data[q] : Data[q] << 8 | Data[q + 1]; q += cw;
                        e.LayerEnd = Data[q] << 8 | Data[q + 1]; q += 2;
                        e.ResEnd = Data[q++];
                        e.CompEnd = cw == 1 ? Data[q] : Data[q] << 8 | Data[q + 1]; q += cw;
                        if (cw == 1 && e.CompEnd == 0) e.CompEnd = 256;
                        e.Order = Data[q];
                        if (e.Order > 4) throw Bad("bad progression order");
                        prm.Poc.Add(e);
                    }
                    break;
                }
                case PPM:
                    if (tile != null) throw Bad("PPM in a tile-part header");
                    if (len < 1) throw Bad("bad PPM length");
                    (Ppm ??= new List<(int, int, int)>()).Add((Data[p], p + 1, len - 1));
                    break;
                case PPT:
                    if (tile == null) throw Bad("PPT in the main header");
                    if (len < 1) throw Bad("bad PPT length");
                    (tile.Ppt ??= new List<(int, int, int, int)>()).Add((Data[p], p + 1, len - 1, part));
                    break;
                case CAP:
                {
                    if (len < 4) throw Bad("bad CAP length");
                    long pcap = U32(p);
                    if ((pcap & (1L << (32 - 15))) != 0) IsHt = true;
                    break;
                }
                case SIZ:
                    throw Bad("second SIZ");
                case 0xFF74: case 0xFF75: case 0xFF77: case 0xFF78: case 0xFF70: case 0xFF79: case 0xFF71: case 0xFF72: case 0xFF73:
                    // MCT, MCC, MCO, CBD, DCO, ATK, ADS, DFS, VMS of Part 2 change the decoded samples
                    throw new ImageNotSupportedException("JPEG 2000 Part 2 extensions (marker 0x" + marker.ToString("X4") + ") are not supported");
                case 0xFF76:
                    ReadNlt(p, len);
                    break;
                default:
                    // TLM, PLM, PLT, CRG, COM, CPF, PRF and unknown segments carry nothing the decoder needs
                    break;
            }
        }

        /// <summary>
        /// Non-linearity (Part 2 NLT): only type 3 - the sign-magnitude to two's complement mapping OpenJPH and OpenEXR
        /// use to carry float bit patterns - is understood; it is undone by the plane consumers that know about it.
        /// </summary>
        public bool[]? NltSignMagnitude;
        void ReadNlt(int p, int len)
        {
            if (len < 4) throw Bad("bad NLT length");
            int c = Data[p] << 8 | Data[p + 1];
            int type = Data[p + 3];
            if (type == 0) return;
            if (type != 3) throw new ImageNotSupportedException("JPEG 2000 Part 2 non-linearity type " + type + " is not supported");
            NltSignMagnitude ??= new bool[Siz.NumComps];
            if (c == 65535) Array.Fill(NltSignMagnitude, true);
            else if (c < Siz.NumComps) NltSignMagnitude[c] = true;
        }

        J2kCod ReadCodingStyle(int p, int len, bool precincts)
        {
            if (len < 5) throw Bad("bad coding style length");
            var c = new J2kCod { Levels = Data[p], CbW = Data[p + 1] + 2, CbH = Data[p + 2] + 2, Style = Data[p + 3] };
            int transform = Data[p + 4];
            if (c.Levels > 32) throw Bad("more than 32 decomposition levels");
            if (c.CbW > 10 || c.CbH > 10 || c.CbW + c.CbH > 12) throw Bad("bad code-block size");
            if (transform > 1) throw new ImageNotSupportedException("JPEG 2000 Part 2 wavelet kernels are not supported");
            if ((c.Style & J2kCod.HtMixed) != 0 && (c.Style & J2kCod.Ht) == 0) throw Bad("bad code-block style");
            c.Reversible = transform == 1;
            c.PPx = new int[c.Levels + 1]; c.PPy = new int[c.Levels + 1];
            if (precincts)
            {
                if (len < 5 + c.Levels + 1) throw Bad("bad coding style length");
                for (int r = 0; r <= c.Levels; r++)
                {
                    int v = Data[p + 5 + r];
                    c.PPx[r] = v & 15; c.PPy[r] = v >> 4;
                    if (r > 0 && (c.PPx[r] == 0 || c.PPy[r] == 0)) throw Bad("bad precinct size");
                }
            }
            else
            {
                Array.Fill(c.PPx, 15); Array.Fill(c.PPy, 15);
            }
            return c;
        }

        J2kQcd ReadQuant(int p, int len)
        {
            if (len < 1) throw Bad("bad quantization length");
            var q = new J2kQcd { Style = Data[p] & 31, Guard = Data[p] >> 5 };
            if (q.Style > 2) throw Bad("bad quantization style");
            int n = q.Style == 0 ? len - 1 : q.Style == 1 ? 1 : (len - 1) / 2;
            if (n < 1 || (q.Style == 1 && len < 3)) throw Bad("bad quantization length");
            if (n > 97) n = 97;
            q.Exp = new int[n]; q.Mant = new int[n];
            for (int i = 0; i < n; i++)
            {
                if (q.Style == 0) q.Exp[i] = Data[p + 1 + i] >> 3;
                else
                {
                    int v = Data[p + 1 + 2 * i] << 8 | Data[p + 2 + 2 * i];
                    q.Exp[i] = v >> 11; q.Mant[i] = v & 0x7FF;
                }
            }
            return q;
        }

        /// <summary>Concatenated PPM payload split into the per tile-part packet header blocks (Nppm / Ippm).</summary>
        public List<byte[]>? SplitPpm()
        {
            if (Ppm == null) return null;
            Ppm.Sort((a, b) => a.z.CompareTo(b.z));
            long total = 0;
            foreach (var s in Ppm) total += s.length;
            var all = new byte[total];
            int o = 0;
            foreach (var s in Ppm) { new ReadOnlySpan<byte>(Data + s.start, s.length).CopyTo(all.AsSpan(o)); o += s.length; }
            var list = new List<byte[]>();
            int pos = 0;
            while (pos + 4 <= all.Length)
            {
                long n = (long)((uint)all[pos] << 24 | (uint)all[pos + 1] << 16 | (uint)all[pos + 2] << 8 | all[pos + 3]);
                pos += 4;
                if (n > all.Length - pos) { n = all.Length - pos; Truncated = true; }
                list.Add(all.AsSpan(pos, (int)n).ToArray());
                pos += (int)n;
            }
            return list;
        }
    }
}
