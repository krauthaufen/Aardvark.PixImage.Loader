using System;
using System.Collections.Generic;

namespace Aardvark.ImageCodecs.JpegXl
{
    /// <summary>
    /// The compressed ICC stream of the image header (B.3 of ISO/IEC 18181-1): the header predicted, no tag list and no
    /// content commands (the rest stored as it is), entropy coded with the 41 byte contexts.
    /// </summary>
    internal static class JxlIccWriter
    {
        static readonly byte[] InitialHeader =
        {
            0, 0, 0, 0, 0, 0, 0, 0, 4, 0, 0, 0, (byte)'m', (byte)'n', (byte)'t', (byte)'r',
            (byte)'R', (byte)'G', (byte)'B', (byte)' ', (byte)'X', (byte)'Y', (byte)'Z', (byte)' ', 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, (byte)'a', (byte)'c', (byte)'s', (byte)'p', 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 246, 214, 0, 1, 0, 0, 0, 0, 211, 45,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        };

        static void PredictHeader(byte[] icc, byte[] header, int pos)
        {
            if (pos == 8) { header[80] = icc[4]; header[81] = icc[5]; header[82] = icc[6]; header[83] = icc[7]; }
            if (pos == 41)
            {
                if (icc[40] == 'A') { header[41] = (byte)'P'; header[42] = (byte)'P'; header[43] = (byte)'L'; }
                if (icc[40] == 'M') { header[41] = (byte)'S'; header[42] = (byte)'F'; header[43] = (byte)'T'; }
            }
            if (pos == 42)
            {
                if (icc[40] == 'S' && icc[41] == 'G') { header[42] = (byte)'I'; header[43] = (byte)' '; }
                if (icc[40] == 'S' && icc[41] == 'U') { header[42] = (byte)'N'; header[43] = (byte)'W'; }
            }
        }

        static void VarInt(List<byte> o, ulong v)
        {
            while (v >= 128) { o.Add((byte)(v & 127 | 128)); v >>= 7; }
            o.Add((byte)v);
        }

        public static byte[] Encode(byte[] icc)
        {
            var o = new List<byte>(icc.Length + 16);
            VarInt(o, (ulong)icc.Length);
            if (icc.Length > 128)
            {
                VarInt(o, 1);
                o.Add(0);   // no tags
            }
            else VarInt(o, 0);
            var header = (byte[])InitialHeader.Clone();
            uint size = (uint)icc.Length;
            header[0] = (byte)(size >> 24); header[1] = (byte)(size >> 16); header[2] = (byte)(size >> 8); header[3] = (byte)size;
            for (int i = 0; i < Math.Min(128, icc.Length); i++)
            {
                PredictHeader(icc, header, i);
                o.Add((byte)(icc[i] - header[i]));
            }
            for (int i = 128; i < icc.Length; i++) o.Add(icc[i]);
            return o.ToArray();
        }

        static int Kind1(int b)
        {
            if (b >= 'a' && b <= 'z' || b >= 'A' && b <= 'Z') return 0;
            if (b >= '0' && b <= '9' || b == '.' || b == ',') return 1;
            if (b == 0) return 2;
            if (b == 1) return 3;
            if (b < 16) return 4;
            if (b == 255) return 6;
            if (b > 240) return 5;
            return 7;
        }

        static int Kind2(int b)
        {
            if (b >= 'a' && b <= 'z' || b >= 'A' && b <= 'Z') return 0;
            if (b >= '0' && b <= '9' || b == '.' || b == ',') return 1;
            if (b < 16) return 2;
            if (b > 240) return 3;
            return 4;
        }

        public static void Write(JxlBitWriter w, byte[] icc)
        {
            if (icc.Length < 128) throw new ImageNotSupportedException("JPEG XL encoder: ICC profile shorter than its header");
            var enc = Encode(icc);
            w.WriteU64((ulong)enc.Length);
            var tok = new JxlTokens(enc.Length);
            for (int i = 0; i < enc.Length; i++)
            {
                int ctx = i <= 128 ? 0 : 1 + Kind1(enc[i - 1]) + Kind2(enc[i - 2]) * 8;
                tok.Add(ctx, enc[i]);
            }
            var code = new JxlEntropyEncoder(41) { MaxClusters = 16 };
            code.Build(new[] { tok }, false);
            code.WriteCode(w);
            code.WriteTokens(w, tok);
        }
    }
}
