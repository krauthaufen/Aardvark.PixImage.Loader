using System;
using System.Buffers.Binary;

namespace PixCodec
{
    /// <summary>One component of a JPEG 2000 codestream as OpenEXR uses it: full 16 or 32 bit integers.</summary>
    internal sealed class ExrHtPlane
    {
        public int Width, Height, BitDepth, XSub = 1, YSub = 1;
        public bool Signed;
        /// <summary>Row major samples; for unsigned 32 bit components the int holds the bit pattern.</summary>
        public int[] Samples = Array.Empty<int>();
    }

    /// <summary>
    /// HTJ2K compression (OpenEXR 3.4). A chunk is "HT", a big-endian 32 bit header length, the header (16 bit channel
    /// count and for each codestream component the 16 bit index of the file channel it holds), and one High Throughput
    /// JPEG 2000 codestream: lossless (5/3 wavelet), one component per channel - HALF as signed 16 bit integers,
    /// FLOAT as signed and UINT as unsigned 32 bit integers (the bit patterns) - with the reversible colour transform
    /// on the first three components when they are R, G, B. The codestream itself is handled by the JPEG 2000 codec.
    /// </summary>
    internal static class ExrHtj2k
    {
        /// <summary>Set by the JPEG 2000 codec: raw codestream -> planes (after inverse colour transform).</summary>
        internal static Func<byte[], ExrHtPlane[]>? DecodeCodestream;
        /// <summary>Set by the JPEG 2000 codec: planes -> lossless HT codestream; the flag asks for the reversible colour transform on the first three.</summary>
        internal static Func<int, int, ExrHtPlane[], bool, byte[]>? EncodeCodestream;

        internal const string Missing = "EXR HTJ2K compression needs the JPEG 2000 codec, which is not available in this build";

        static ImageFormatException Corrupt(string what) => new("EXR HTJ2K data is corrupt: " + what);

        public static void Decode(ReadOnlySpan<byte> src, Span<byte> dst, in ExrChunk c, ExrScratch s)
        {
            var decode = DecodeCodestream ?? throw new ImageNotSupportedException(Missing);
            int nc = c.Chans.Length;
            if (src.Length < 8 || src[0] != (byte)'H' || src[1] != (byte)'T') throw Corrupt("chunk header");
            uint headerLength = BinaryPrimitives.ReadUInt32BigEndian(src.Slice(2));
            if (headerLength < 2 || headerLength > src.Length - 6) throw Corrupt("header length");
            int count = BinaryPrimitives.ReadUInt16BigEndian(src.Slice(6));
            if (count != nc || headerLength < 2 + 2 * (uint)nc) throw Corrupt("channel map");
            var map = new int[nc];
            var seen = new bool[nc];
            for (int i = 0; i < nc; i++)
            {
                map[i] = BinaryPrimitives.ReadUInt16BigEndian(src.Slice(8 + 2 * i));
                if (map[i] >= nc || seen[map[i]]) throw Corrupt("channel map");
                seen[map[i]] = true;
            }
            var planes = decode(src.Slice(6 + (int)headerLength).ToArray());
            if (planes.Length != nc) throw Corrupt("component count");
            var byChannel = new ExrHtPlane[nc];
            for (int i = 0; i < nc; i++)
            {
                var p = planes[i];
                int ch = map[i];
                if (p.Width != c.Nx[ch] || p.Height != c.Ny[ch] || p.Samples.Length < (long)p.Width * p.Height
                    || p.BitDepth != c.Chans[ch].Bytes * 8)
                    throw Corrupt("component geometry");
                byChannel[ch] = p;
            }

            var row = new int[nc];
            int o = 0;
            for (int y = c.Y0; y <= c.Y1; y++)
                for (int ch = 0; ch < nc; ch++)
                {
                    if (!ExrChunk.IsSampled(y, c.Chans[ch].YS)) continue;
                    var p = byChannel[ch];
                    var line = p.Samples.AsSpan(row[ch]++ * p.Width, p.Width);
                    if (c.Chans[ch].Bytes == 2)
                        for (int x = 0; x < line.Length; x++, o += 2) BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(o), (ushort)line[x]);
                    else
                        for (int x = 0; x < line.Length; x++, o += 4) BinaryPrimitives.WriteInt32LittleEndian(dst.Slice(o), line[x]);
                }
        }

        public static byte[] Encode(ReadOnlySpan<byte> raw, in ExrChunk c, ExrScratch s)
        {
            var encode = EncodeCodestream ?? throw new ImageNotSupportedException(Missing);
            int nc = c.Chans.Length;
            if (nc > 16384) throw new ImageNotSupportedException("EXR HTJ2K compression supports at most 16384 channels");

            // component order: R, G, B first (for the colour transform) when the part has them, then the rest in file order
            int r = -1, g = -1, b = -1;
            for (int i = 0; i < nc; i++)
            {
                if (c.Chans[i].Name == "R") r = i; else if (c.Chans[i].Name == "G") g = i; else if (c.Chans[i].Name == "B") b = i;
            }
            bool rct = r >= 0 && g >= 0 && b >= 0;
            if (rct)
                foreach (int i in new[] { g, b })
                    rct &= c.Chans[i].Type == c.Chans[r].Type && c.Chans[i].XS == 1 && c.Chans[i].YS == 1 && c.Chans[r].XS == 1 && c.Chans[r].YS == 1;
            var map = new int[nc];
            int k = 0;
            if (rct) { map[0] = r; map[1] = g; map[2] = b; k = 3; }
            for (int i = 0; i < nc; i++)
                if (!rct || (i != r && i != g && i != b)) map[k++] = i;

            var byChannel = new ExrHtPlane[nc];
            for (int i = 0; i < nc; i++)
                byChannel[i] = new ExrHtPlane
                {
                    Width = c.Nx[i], Height = c.Ny[i], BitDepth = c.Chans[i].Bytes * 8, Signed = c.Chans[i].Type != 0,
                    XSub = c.Chans[i].XS, YSub = c.Chans[i].YS, Samples = new int[c.Nx[i] * c.Ny[i]],
                };
            var row = new int[nc];
            int o = 0;
            for (int y = c.Y0; y <= c.Y1; y++)
                for (int ch = 0; ch < nc; ch++)
                {
                    if (!ExrChunk.IsSampled(y, c.Chans[ch].YS)) continue;
                    var p = byChannel[ch];
                    var line = p.Samples.AsSpan(row[ch]++ * p.Width, p.Width);
                    if (c.Chans[ch].Bytes == 2)
                        for (int x = 0; x < line.Length; x++, o += 2) line[x] = BinaryPrimitives.ReadInt16LittleEndian(raw.Slice(o));
                    else
                        for (int x = 0; x < line.Length; x++, o += 4) line[x] = BinaryPrimitives.ReadInt32LittleEndian(raw.Slice(o));
                }
            var planes = new ExrHtPlane[nc];
            for (int i = 0; i < nc; i++) planes[i] = byChannel[map[i]];
            var codestream = encode(c.X1 - c.X0 + 1, c.Y1 - c.Y0 + 1, planes, rct);

            int headerLength = 2 + 2 * nc;
            var result = new byte[6 + headerLength + codestream.Length];
            result[0] = (byte)'H'; result[1] = (byte)'T';
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(2), (uint)headerLength);
            BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(6), (ushort)nc);
            for (int i = 0; i < nc; i++) BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(8 + 2 * i), (ushort)map[i]);
            codestream.CopyTo(result, 6 + headerLength);
            return result;
        }
    }
}
