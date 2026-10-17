using System;

namespace Aardvark.ImageCodecs.JpegXrImpl
{
    /// <summary>A parsed CODED_IMAGE( ) (8.2): headers, index table and the position of the tile data.</summary>
    internal sealed class JxrCodestream
    {
        public JxrImageHeader Ih = null!;
        public JxrPlaneHeader Primary = null!;
        public JxrPlaneHeader? Alpha;
        public long[] TileOffsets = Array.Empty<long>();
        public int TilesStart;
        public byte[] Data = Array.Empty<byte>();   // the codestream with 16 bytes of padding
        public int Length;
        public int ProfileIdc = 111, LevelIdc = 255;

        public static JxrCodestream Parse(ReadOnlySpan<byte> codestream, bool headersOnly)
        {
            var cs = new JxrCodestream();
            int n = codestream.Length;
            // headers only: a prefix is enough (the index table can be long, but is bounded by the tile count)
            int copy = headersOnly ? Math.Min(n, 1 << 16) : n;
            cs.Data = new byte[copy + 16];
            codestream.Slice(0, copy).CopyTo(cs.Data);
            cs.Length = copy;
            var r = new JxrBitReader(cs.Data, copy, 0);
            cs.Ih = JxrImageHeader.Read(r);
            cs.Primary = JxrPlaneHeader.Read(r, cs.Ih, false);
            if (cs.Ih.AlphaPlane) cs.Alpha = JxrPlaneHeader.Read(r, cs.Ih, true);
            if (r.PastEnd) throw new ImageFormatException("JPEG XR: codestream ends inside the headers");
            Validate(cs.Ih, cs.Primary);
            if (headersOnly) return cs;
            var ih = cs.Ih;
            int tiles = checked(ih.TileCols * ih.TileRows);
            int entries = ih.Frequency ? tiles * cs.Primary.NumBands : tiles;
            cs.TileOffsets = new long[entries];
            if (ih.IndexTable)
            {
                if (r.Bits(16) != 1) throw new ImageFormatException("JPEG XR: bad INDEX_TABLE_STARTCODE");
                for (int i = 0; i < entries; i++) cs.TileOffsets[i] = r.VlwEsc();
            }
            else if (entries > 1) throw new ImageFormatException("JPEG XR: several tile packets without an index table");
            long subsequent = r.VlwEsc();
            if (subsequent > 0)
            {
                int bytes = 0;
                for (bool last = false; !last && !r.PastEnd;)
                {
                    cs.ProfileIdc = r.Bits(8);
                    cs.LevelIdc = r.Bits(8);
                    r.Bits(15);
                    last = r.Bit();
                    bytes += 4;
                }
                for (long i = bytes; i < subsequent && !r.PastEnd; i++) r.Bits(8);
            }
            if (r.PastEnd) throw new ImageFormatException("JPEG XR: codestream ends inside the headers");
            cs.TilesStart = r.BitPosition >> 3;
            return cs;
        }

        /// <summary>The combinations of internal and output colour format and bit depth of Tables 176, 179 and 183.</summary>
        static void Validate(JxrImageHeader ih, JxrPlaneHeader ph)
        {
            var i = ph.Format;
            var bd = ih.BitDepth;
            bool yuv = i is JxrInternalFormat.Yuv420 or JxrInternalFormat.Yuv422 or JxrInternalFormat.Yuv444;
            bool ok = ih.OutputFormat switch
            {
                JxrOutputFormat.YOnly => i == JxrInternalFormat.YOnly,
                JxrOutputFormat.Yuv420 => i is JxrInternalFormat.Yuv420 or JxrInternalFormat.YOnly,
                JxrOutputFormat.Yuv422 => i is JxrInternalFormat.Yuv422 or JxrInternalFormat.YOnly,
                JxrOutputFormat.Yuv444 or JxrOutputFormat.Rgb => yuv || i == JxrInternalFormat.YOnly,
                JxrOutputFormat.Rgbe => i == JxrInternalFormat.Yuv444,
                JxrOutputFormat.Cmyk or JxrOutputFormat.CmykDirect => i == JxrInternalFormat.Yuvk,
                _ => i == JxrInternalFormat.NComponent,
            };
            if (bd is JxrBitDepth.Bd1White1 or JxrBitDepth.Bd1Black1 && ih.OutputFormat != JxrOutputFormat.YOnly) ok = false;
            if (bd is JxrBitDepth.Bd5 or JxrBitDepth.Bd565 && ih.OutputFormat != JxrOutputFormat.Rgb) ok = false;
            if (ih.OutputFormat == JxrOutputFormat.Rgbe && bd != JxrBitDepth.Bd8) ok = false;
            if (ph.NumComponents < 1) ok = false;
            if (!ok) throw new ImageFormatException($"JPEG XR: output format {ih.OutputFormat} / {bd} from internal format {i}");
        }
    }

    /// <summary>Parses and decodes all tiles of a codestream into the planes, then reconstructs the samples (clause 9).</summary>
    internal static class JxrDecoder
    {
        public sealed class Result
        {
            public JxrCodestream Cs = null!;
            public JxrPlane Primary = null!;
            public JxrPlane? Alpha;
        }

        public static Result Decode(JxrCodestream cs, bool parallel)
        {
            var res = DecodeParseOnly(cs);
            res.Primary.Reconstruct(parallel);
            res.Alpha?.Reconstruct(parallel);
            return res;
        }

        /// <summary>Parsing and coefficient decoding of all tiles, without the sample reconstruction.</summary>
        public static Result DecodeParseOnly(JxrCodestream cs)
        {
            var ih = cs.Ih;
            var primary = new JxrPlane(ih, cs.Primary, false);
            var alpha = cs.Alpha != null ? new JxrPlane(ih, cs.Alpha, true) : null;
            var r = new JxrBitReader(cs.Data, cs.Length, 0);
            int cols = ih.TileCols, tiles = cols * ih.TileRows;
            int bands = cs.Primary.NumBands;
            Span<int> dcIn = stackalloc int[Math.Max(primary.Nc, 4)];
            for (int t = 0; t < tiles; t++)
            {
                int tx = t % cols, ty = t / cols;
                int l = ih.TileLeft[tx], rr = ih.TileLeft[tx + 1], tp = ih.TileTop[ty], bt = ih.TileTop[ty + 1];
                if (!ih.Frequency)
                {
                    Seek(r, cs, cs.TileOffsets[t]);
                    if (ih.TrimFlexbits)
                    {
                        int trim = r.Bits(4);
                        primary.TrimFlexbits = trim;
                        if (alpha != null) alpha.TrimFlexbits = trim;
                    }
                    TileHeaders(r, primary, true, true, true);
                    if (alpha != null) TileHeaders(r, alpha, true, true, true);
                    for (int y = tp; y < bt; y++)
                        for (int x = l; x < rr; x++)
                        {
                            Spatial(r, primary, x, y, l, rr, tp, dcIn);
                            if (alpha != null) Spatial(r, alpha, x, y, l, rr, tp, dcIn);
                        }
                }
                else
                {
                    // DC packet
                    Seek(r, cs, cs.TileOffsets[t * bands]);
                    TileHeaders(r, primary, true, false, false);
                    if (alpha != null) TileHeaders(r, alpha, true, false, false);
                    for (int y = tp; y < bt; y++)
                        for (int x = l; x < rr; x++)
                            foreach (var p in Planes(primary, alpha))
                            {
                                p.SetMacroblock(x, y, l, rr, tp);
                                var din = p.DcInputOf(p.MbIndex);
                                p.ParseMbDc(r, din);
                                if (p.Bands == JxrBands.DcOnly || bands == 1) p.DecodeDcLp(din, false);
                            }
                    if (bands > 1)
                    {
                        Seek(r, cs, cs.TileOffsets[t * bands + 1]);
                        foreach (var p in Planes(primary, alpha))
                            if (p.Bands != JxrBands.DcOnly) TileHeaders(r, p, false, true, false);
                        for (int y = tp; y < bt; y++)
                            for (int x = l; x < rr; x++)
                                foreach (var p in Planes(primary, alpha))
                                {
                                    if (p.Bands == JxrBands.DcOnly) continue;
                                    p.SetMacroblock(x, y, l, rr, tp);
                                    p.ReadLpQpIndex(r, p.MbIndex);
                                    p.ParseMbLp(r);
                                    p.DecodeDcLp(p.DcInputOf(p.MbIndex), true);
                                }
                    }
                    if (bands > 2)
                    {
                        Seek(r, cs, cs.TileOffsets[t * bands + 2]);
                        foreach (var p in Planes(primary, alpha))
                            if (HasHp(p)) TileHeaders(r, p, false, false, true);
                        for (int y = tp; y < bt; y++)
                            for (int x = l; x < rr; x++)
                                foreach (var p in Planes(primary, alpha))
                                {
                                    if (!HasHp(p)) continue;
                                    p.SetMacroblock(x, y, l, rr, tp);
                                    p.ReadHpQpIndex(r, p.MbIndex);
                                    p.ParseMbCbphp(r);
                                    p.ParseMbHp(r, false);
                                    if (p.Bands == JxrBands.NoFlexbits || bands == 3)
                                    {
                                        p.ParseMbFlexbits(null);
                                        p.DecodeHp();
                                    }
                                }
                    }
                    if (bands > 3)
                    {
                        // an index entry of 0 marks an empty flexbits packet (jxrlib writes those when no bits are left)
                        long fo = cs.TileOffsets[t * bands + 3];
                        bool present = fo != 0 || t * bands + 3 == 0;
                        if (present)
                        {
                            Seek(r, cs, fo);
                            int trim = ih.TrimFlexbits ? r.Bits(4) : 0;
                            primary.TrimFlexbits = trim;
                            if (alpha != null) alpha.TrimFlexbits = trim;
                        }
                        for (int y = tp; y < bt; y++)
                            for (int x = l; x < rr; x++)
                                foreach (var p in Planes(primary, alpha))
                                {
                                    if (p.Bands != JxrBands.All) continue;
                                    p.SetMacroblock(x, y, l, rr, tp);
                                    p.ParseMbFlexbits(present ? r : null);
                                    p.DecodeHp();
                                }
                    }
                }
            }
            return new Result { Cs = cs, Primary = primary, Alpha = alpha };
        }

        static bool HasHp(JxrPlane p) => p.Bands is JxrBands.All or JxrBands.NoFlexbits;

        static JxrPlane[] Planes(JxrPlane primary, JxrPlane? alpha) => alpha == null ? new[] { primary } : new[] { primary, alpha };

        static void Seek(JxrBitReader r, JxrCodestream cs, long offset)
        {
            long pos = cs.TilesStart + offset;
            if (pos + 4 > cs.Length) throw new ImageFormatException("JPEG XR: tile packet outside the codestream");
            r.Seek((int)pos);
            if (r.Bits(24) != 1) throw new ImageFormatException("JPEG XR: missing TILE_STARTCODE");
            r.Bits(8);   // ARBITRARY_BYTE
        }

        static void TileHeaders(JxrBitReader r, JxrPlane p, bool dc, bool lp, bool hp)
        {
            if (dc) p.ReadTileHeaderDc(r);
            if (p.Bands == JxrBands.DcOnly) { if (dc) p.NoLpHeader(); return; }
            if (lp) p.ReadTileHeaderLp(r);
            if (hp && p.Bands != JxrBands.NoHighpass) p.ReadTileHeaderHp(r);
        }

        static void Spatial(JxrBitReader r, JxrPlane p, int x, int y, int l, int rr, int tp, Span<int> dcIn)
        {
            p.SetMacroblock(x, y, l, rr, tp);
            int mb = p.MbIndex;
            bool lp = p.Bands != JxrBands.DcOnly, hp = HasHp(p);
            if (lp)
            {
                p.ReadLpQpIndex(r, mb);
                if (hp) p.ReadHpQpIndex(r, mb);
            }
            p.ParseMbDc(r, dcIn);
            if (lp) p.ParseMbLp(r);
            p.DecodeDcLp(dcIn, lp);
            if (hp)
            {
                p.ParseMbCbphp(r);
                p.ParseMbHp(r, p.Bands == JxrBands.All);
                if (p.Bands == JxrBands.NoFlexbits) p.ParseMbFlexbits(null);
                p.DecodeHp();
            }
        }
    }
}
