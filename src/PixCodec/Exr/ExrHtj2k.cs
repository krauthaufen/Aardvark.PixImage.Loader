using System;
using System.Buffers.Binary;

namespace PixCodec
{
    /// <summary>
    /// HTJ2K compression (OpenEXR 3.4). A chunk is "HT", a big-endian 32 bit header length, the header (16 bit channel
    /// count and for each codestream component the 16 bit index of the file channel it holds), and one High Throughput
    /// JPEG 2000 codestream: lossless (5/3 wavelet), one component per channel - HALF as signed 16 bit integers,
    /// FLOAT as signed and UINT as unsigned 32 bit integers (the bit patterns) - with the reversible colour transform
    /// on the first three components when they are R, G, B. The float components carry the sign-magnitude
    /// non-linearity marker (NLT type 3): negative values are stored as the complemented magnitude, which keeps the
    /// integers in the order of the float values. The codestream itself is handled by the JPEG 2000 codec.
    /// </summary>
    internal static class ExrHtj2k
    {
        static ImageFormatException Corrupt(string what) => new("EXR HTJ2K data is corrupt: " + what);

        public static void Decode(ReadOnlySpan<byte> src, Span<byte> dst, in ExrChunk c, ExrScratch s)
        {
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
            // chunks are decoded in parallel by the caller; one chunk is one small codestream
            var decoded = Jp2.DecodeCodestreamPlanes(src.Slice(6 + (int)headerLength), new Jp2DecodeOptions { Parallel = false });
            var planes = decoded.Planes;
            if (planes.Length != nc) throw Corrupt("component count");
            var byChannel = new J2kPlane[nc];
            for (int i = 0; i < nc; i++)
            {
                var p = planes[i];
                int ch = map[i];
                if (p.Width != c.Nx[ch] || p.Height != c.Ny[ch] || p.Data == null || p.Data.Length < (long)p.Width * p.Height
                    || p.Precision != c.Chans[ch].Bytes * 8)
                    throw Corrupt("component geometry");
                byChannel[ch] = p;
                // Components flagged with the sign-magnitude non-linearity (NLT type 3) hold the float bit patterns as
                // order preserving integers: negative values are the complemented magnitude.
                if (decoded.NltSignMagnitude != null && decoded.NltSignMagnitude[i])
                {
                    var d = p.Data;
                    int flip = p.Precision == 16 ? 0x8000 : int.MinValue;
                    for (int k = 0; k < d.Length; k++) if (d[k] < 0) d[k] = ~d[k] ^ flip;
                }
            }

            var row = new int[nc];
            int o = 0;
            for (int y = c.Y0; y <= c.Y1; y++)
                for (int ch = 0; ch < nc; ch++)
                {
                    if (!ExrChunk.IsSampled(y, c.Chans[ch].YS)) continue;
                    var p = byChannel[ch];
                    var line = p.Data.AsSpan(row[ch]++ * p.Width, p.Width);
                    if (c.Chans[ch].Bytes == 2)
                        for (int x = 0; x < line.Length; x++, o += 2) BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(o), (ushort)line[x]);
                    else
                        for (int x = 0; x < line.Length; x++, o += 4) BinaryPrimitives.WriteInt32LittleEndian(dst.Slice(o), line[x]);
                }
        }

        public static byte[] Encode(ReadOnlySpan<byte> raw, in ExrChunk c, ExrScratch s)
        {
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
            // the reference keeps R, G, B in front but drops the colour transform as soon as any channel of the
            // part is subsampled; its reader misreads G and B of a file that has both
            for (int i = 0; i < nc; i++)
                if (c.Chans[i].XS != 1 || c.Chans[i].YS != 1) rct = false;

            var byChannel = new J2kPlane[nc];
            for (int i = 0; i < nc; i++)
                byChannel[i] = new J2kPlane
                {
                    Width = c.Nx[i], Height = c.Ny[i], Precision = c.Chans[i].Bytes * 8, Signed = c.Chans[i].Type != 0,
                    Dx = c.Chans[i].XS, Dy = c.Chans[i].YS, Data = new int[c.Nx[i] * c.Ny[i]],
                };
            var row = new int[nc];
            int o = 0;
            for (int y = c.Y0; y <= c.Y1; y++)
                for (int ch = 0; ch < nc; ch++)
                {
                    if (!ExrChunk.IsSampled(y, c.Chans[ch].YS)) continue;
                    var p = byChannel[ch];
                    var line = p.Data.AsSpan(row[ch]++ * p.Width, p.Width);
                    if (c.Chans[ch].Bytes == 2)
                        for (int x = 0; x < line.Length; x++, o += 2) line[x] = BinaryPrimitives.ReadInt16LittleEndian(raw.Slice(o));
                    else
                        for (int x = 0; x < line.Length; x++, o += 4) line[x] = BinaryPrimitives.ReadInt32LittleEndian(raw.Slice(o));
                }
            var planes = new J2kPlane[nc];
            var nlt = new bool[nc];
            for (int i = 0; i < nc; i++)
            {
                planes[i] = byChannel[map[i]];
                // float bit patterns go in as order preserving integers (NLT type 3, as the reference writer does)
                if (!planes[i].Signed) continue;
                nlt[i] = true;
                var d = planes[i].Data!;
                if (planes[i].Precision == 16)
                    for (int j = 0; j < d.Length; j++) { if (d[j] < 0) d[j] = (short)(~d[j] ^ 0x8000); }
                else
                    for (int j = 0; j < d.Length; j++) { if (d[j] < 0) d[j] = ~d[j] ^ int.MinValue; }
            }
            // the parameters of the reference writer: lossless 5/3, HT block coder, 5 levels, 128x32 code blocks, RPCL
            var codestream = Jp2.EncodeCodestreamPlanes(new J2kPlanes { Width = c.X1 - c.X0 + 1, Height = c.Y1 - c.Y0 + 1, Planes = planes, NltSignMagnitude = nlt }, new Jp2EncodeOptions
            {
                Lossless = true, HighThroughput = true, RawCodestream = true, ColorTransform = rct, Levels = 5,
                CodeBlockWidth = 128, CodeBlockHeight = 32, Progression = Jp2Progression.Rpcl, Parallel = false,
            });

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
