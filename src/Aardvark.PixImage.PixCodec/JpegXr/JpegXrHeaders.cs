using System;

namespace Aardvark.ImageCodecs.JpegXrImpl
{
    /// <summary>INTERNAL_CLR_FMT (Table 28).</summary>
    internal enum JxrInternalFormat { YOnly = 0, Yuv420 = 1, Yuv422 = 2, Yuv444 = 3, Yuvk = 4, NComponent = 6 }

    /// <summary>OUTPUT_CLR_FMT (Table 22).</summary>
    internal enum JxrOutputFormat { YOnly = 0, Yuv420 = 1, Yuv422 = 2, Yuv444 = 3, Cmyk = 4, CmykDirect = 5, NComponent = 6, Rgb = 7, Rgbe = 8 }

    /// <summary>OUTPUT_BITDEPTH (Table 23).</summary>
    internal enum JxrBitDepth { Bd1White1 = 0, Bd8 = 1, Bd16 = 2, Bd16S = 3, Bd16F = 4, Bd32S = 6, Bd32F = 7, Bd5 = 8, Bd10 = 9, Bd565 = 10, Bd1Black1 = 15 }

    /// <summary>BANDS_PRESENT (Table 29).</summary>
    internal enum JxrBands { All = 0, NoFlexbits = 1, NoHighpass = 2, DcOnly = 3 }

    /// <summary>IMAGE_HEADER( ) (8.3) with the derived tile layout.</summary>
    internal sealed class JxrImageHeader
    {
        public bool HardTiling, Tiling, Frequency, IndexTable, ShortHeader, LongWord, Windowing, TrimFlexbits, RedBlueNotSwapped,
            Premultiplied, AlphaPlane;
        public int SpatialXfrm, Overlap;
        public JxrOutputFormat OutputFormat;
        public JxrBitDepth BitDepth;
        public int Width, Height;
        public int TileCols, TileRows;
        /// <summary>LeftMBIndexOfTile[0 .. TileCols] and TopMBIndexOfTile[0 .. TileRows].</summary>
        public int[] TileLeft = Array.Empty<int>(), TileTop = Array.Empty<int>();
        public int TopMargin, LeftMargin, BottomMargin, RightMargin;
        public int ExtWidth, ExtHeight, MbWidth, MbHeight;

        public static JxrImageHeader Read(JxrBitReader r)
        {
            var h = new JxrImageHeader();
            Span<byte> sig = stackalloc byte[8];
            for (int i = 0; i < 8; i++) sig[i] = r.Byte();
            if (!sig.SequenceEqual("WMPHOTO\0"u8)) throw new ImageFormatException("JPEG XR: missing WMPHOTO codestream signature");
            int reservedB = r.Bits(4);
            if (reservedB != 1) throw new ImageNotSupportedException($"JPEG XR: codestream version {reservedB} (RESERVED_B) is not supported");
            h.HardTiling = r.Bit();
            r.Bits(3);   // RESERVED_C, ignored
            h.Tiling = r.Bit();
            h.Frequency = r.Bit();
            h.SpatialXfrm = r.Bits(3);
            h.IndexTable = r.Bit();
            h.Overlap = r.Bits(2);
            if (h.Overlap == 3) throw new ImageFormatException("JPEG XR: reserved OVERLAP_MODE 3");
            h.ShortHeader = r.Bit();
            h.LongWord = r.Bit();
            h.Windowing = r.Bit();
            h.TrimFlexbits = r.Bit();
            r.Bit();   // RESERVED_D
            h.RedBlueNotSwapped = r.Bit();
            h.Premultiplied = r.Bit();
            h.AlphaPlane = r.Bit();
            int ofmt = r.Bits(4), obd = r.Bits(4);
            if (ofmt > 8) throw new ImageFormatException($"JPEG XR: reserved OUTPUT_CLR_FMT {ofmt}");
            if (obd is 5 or (>= 11 and <= 14)) throw new ImageFormatException($"JPEG XR: reserved OUTPUT_BITDEPTH {obd}");
            h.OutputFormat = (JxrOutputFormat)ofmt;
            h.BitDepth = (JxrBitDepth)obd;
            long w, hh;
            if (h.ShortHeader) { w = r.Bits(16); hh = r.Bits(16); }
            else { w = (uint)r.Bits(32); hh = (uint)r.Bits(32); }
            if (w + 1 > int.MaxValue / 2 || hh + 1 > int.MaxValue / 2) throw new ImageNotSupportedException("JPEG XR: image dimensions too large");
            h.Width = (int)w + 1;
            h.Height = (int)hh + 1;
            int cols = 1, rows = 1;
            if (h.Tiling) { cols = r.Bits(12) + 1; rows = r.Bits(12) + 1; }
            h.TileCols = cols;
            h.TileRows = rows;
            var tw = new int[cols];
            var th = new int[rows];
            for (int n = 0; n < cols - 1; n++) tw[n] = r.Bits(h.ShortHeader ? 8 : 16);
            for (int n = 0; n < rows - 1; n++) th[n] = r.Bits(h.ShortHeader ? 8 : 16);
            if (h.Windowing)
            {
                h.TopMargin = r.Bits(6);
                h.LeftMargin = r.Bits(6);
                h.BottomMargin = r.Bits(6);
                h.RightMargin = r.Bits(6);
            }
            else
            {
                h.BottomMargin = (16 - h.Height % 16) % 16;
                h.RightMargin = (16 - h.Width % 16) % 16;
            }
            long ew = (long)h.Width + h.LeftMargin + h.RightMargin, eh = (long)h.Height + h.TopMargin + h.BottomMargin;
            if (ew % 16 != 0 || eh % 16 != 0) throw new ImageFormatException("JPEG XR: extended image size is not a multiple of 16");
            if (ew > int.MaxValue / 4 || eh > int.MaxValue / 4) throw new ImageNotSupportedException("JPEG XR: image dimensions too large");
            h.ExtWidth = (int)ew;
            h.ExtHeight = (int)eh;
            h.MbWidth = h.ExtWidth / 16;
            h.MbHeight = h.ExtHeight / 16;
            h.TileLeft = new int[cols + 1];
            for (int n = 0; n < cols - 1; n++) h.TileLeft[n + 1] = h.TileLeft[n] + tw[n];
            h.TileLeft[cols] = h.MbWidth;
            h.TileTop = new int[rows + 1];
            for (int n = 0; n < rows - 1; n++) h.TileTop[n + 1] = h.TileTop[n] + th[n];
            h.TileTop[rows] = h.MbHeight;
            for (int n = 0; n < cols; n++)
                if (h.TileLeft[n + 1] <= h.TileLeft[n]) throw new ImageFormatException("JPEG XR: tile columns do not partition the image");
            for (int n = 0; n < rows; n++)
                if (h.TileTop[n + 1] <= h.TileTop[n]) throw new ImageFormatException("JPEG XR: tile rows do not partition the image");
            return h;
        }
    }

    /// <summary>A QP set: one quantization parameter per component.</summary>
    internal static class JxrQp
    {
        /// <summary>DC_QP( ) / one entry of LP_QP( ) and HP_QP( ) (8.4.22): COMPONENT_MODE and the QPs it implies.</summary>
        public static byte[] Read(JxrBitReader r, int nc)
        {
            var q = new byte[nc];
            int mode = nc != 1 ? r.Bits(2) : 0;
            switch (mode)
            {
                case 0:
                    {
                        byte v = r.Byte();
                        for (int i = 0; i < nc; i++) q[i] = v;
                        break;
                    }
                case 1:
                    {
                        byte l = r.Byte(), c = r.Byte();
                        q[0] = l;
                        for (int i = 1; i < nc; i++) q[i] = c;
                        break;
                    }
                case 2:
                    for (int i = 0; i < nc; i++) q[i] = r.Byte();
                    break;
                default: throw new ImageFormatException("JPEG XR: reserved COMPONENT_MODE");
            }
            return q;
        }

        public static void Write(JxrBitWriter w, byte[] q)
        {
            int nc = q.Length;
            if (nc == 1) { w.Byte(q[0]); return; }
            bool uniform = true, separate = true;
            for (int i = 1; i < nc; i++)
            {
                if (q[i] != q[0]) uniform = false;
                if (i > 1 && q[i] != q[1]) separate = false;
            }
            if (uniform) { w.Bits(0, 2); w.Byte(q[0]); }
            else if (separate) { w.Bits(1, 2); w.Byte(q[0]); w.Byte(q[1]); }
            else { w.Bits(2, 2); foreach (var v in q) w.Byte(v); }
        }

        /// <summary>QuantMap( ) (Table 149).</summary>
        public static int Map(int qp, int scaledShift, bool scaled)
        {
            if (qp == 0) return 1;
            int man, exp;
            if (!scaled)
            {
                if (qp < 32) { man = (qp + 3) >> 2; exp = 0; }
                else if (qp < 48) { man = (16 + qp % 16 + 1) >> 1; exp = (qp >> 4) - 2; }
                else { man = 16 + qp % 16; exp = (qp >> 4) - 1 - 2; }
            }
            else if (qp < 16) { man = qp; exp = scaledShift; }
            else { man = 16 + qp % 16; exp = (qp >> 4) - 1 + scaledShift; }
            return man << exp;
        }
    }

    /// <summary>IMAGE_PLANE_HEADER( ) (8.4).</summary>
    internal sealed class JxrPlaneHeader
    {
        public JxrInternalFormat Format;
        public bool Scaled;
        public JxrBands Bands;
        public int ChromaCenteringX, ChromaCenteringY;
        public int NumComponents;
        public int ShiftBits, LenMantissa, ExpBias;
        public bool DcUniform, LpUniform, HpUniform;
        public byte[]? DcQp;
        public byte[]? LpQp, HpQp;   // the single image plane level set (NumLPQPs = NumHPQPs = 1)

        public int NumBands => Bands switch { JxrBands.All => 4, JxrBands.NoFlexbits => 3, JxrBands.NoHighpass => 2, _ => 1 };

        public static JxrPlaneHeader Read(JxrBitReader r, JxrImageHeader ih, bool alpha)
        {
            var p = new JxrPlaneHeader();
            int fmt = r.Bits(3);
            if (fmt is 5 or 7) throw new ImageFormatException($"JPEG XR: reserved INTERNAL_CLR_FMT {fmt}");
            p.Format = (JxrInternalFormat)fmt;
            p.Scaled = r.Bit();
            int bands = r.Bits(4);
            if (bands > 3) throw new ImageFormatException($"JPEG XR: reserved BANDS_PRESENT {bands}");
            p.Bands = (JxrBands)bands;
            p.NumComponents = p.Format switch
            {
                JxrInternalFormat.YOnly => 1,
                JxrInternalFormat.Yuvk => 4,
                JxrInternalFormat.NComponent => 0,
                _ => 3,
            };
            if (p.Format is JxrInternalFormat.Yuv444 or JxrInternalFormat.Yuv420 or JxrInternalFormat.Yuv422)
            {
                if (p.Format != JxrInternalFormat.Yuv444)
                {
                    r.Bit();
                    p.ChromaCenteringX = r.Bits(3);
                }
                else r.Bits(4);
                if (p.Format == JxrInternalFormat.Yuv420)
                {
                    r.Bit();
                    p.ChromaCenteringY = r.Bits(3);
                }
                else r.Bits(4);
            }
            else if (p.Format == JxrInternalFormat.NComponent)
            {
                int n = r.Bits(4);
                if (n == 0xF) p.NumComponents = r.Bits(12) + 16;
                else
                {
                    p.NumComponents = n + 1;
                    r.Bits(4);
                }
            }
            if (alpha && p.Format != JxrInternalFormat.YOnly) throw new ImageFormatException("JPEG XR: alpha image plane is not YONLY");
            if (ih.BitDepth is JxrBitDepth.Bd16 or JxrBitDepth.Bd16S or JxrBitDepth.Bd32S) p.ShiftBits = r.Bits(8);
            if (ih.BitDepth == JxrBitDepth.Bd32F)
            {
                p.LenMantissa = r.Bits(8);
                p.ExpBias = (sbyte)r.Bits(8);
            }
            int nc = p.NumComponents;
            p.DcUniform = r.Bit();
            if (p.DcUniform) p.DcQp = JxrQp.Read(r, nc);
            if (p.Bands != JxrBands.DcOnly)
            {
                r.Bit();
                p.LpUniform = r.Bit();
                if (p.LpUniform) p.LpQp = JxrQp.Read(r, nc);
                if (p.Bands != JxrBands.NoHighpass)
                {
                    r.Bit();
                    p.HpUniform = r.Bit();
                    if (p.HpUniform) p.HpQp = JxrQp.Read(r, nc);
                }
            }
            r.AlignByte();
            return p;
        }
    }
}
