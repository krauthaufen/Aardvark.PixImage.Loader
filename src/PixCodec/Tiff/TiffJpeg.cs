using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace PixCodec
{
    /// <summary>The calls into the JPEG codec that TIFF needs, in one place.</summary>
    internal static class TiffJpegBridge
    {
        public static void Decode8(ReadOnlySpan<byte> tables, ReadOnlySpan<byte> data, int width, int height, int components, bool ycc, Span<byte> dst)
        {
            if (Tiff.JpegDecoder != null) Tiff.JpegDecoder(tables, data, width, height, components, ycc, dst);
            else Jpeg.DecodeForTiff(tables, data, width, height, components, ycc, dst);
        }

        public static void Decode16(ReadOnlySpan<byte> tables, ReadOnlySpan<byte> data, int width, int height, int components, bool ycc, Span<ushort> dst)
            => throw new ImageNotSupportedException("TIFF: JPEG compression with more than 8 bits per sample is not supported");
    }

    /// <summary>
    /// Old-style JPEG (compression 6, TIFF 6.0 section 22). The image is one JPEG frame whose pieces are spread over
    /// tags: an optional "interchange format" stream (JPEGInterchangeFormat, either complete or only the header with
    /// the tables), separate quantization / Huffman tables (JPEGQTables, JPEGDCTables, JPEGACTables) and the
    /// entropy coded data in the strips. This class glues whatever is there into a normal JPEG stream.
    /// </summary>
    internal sealed class TiffOldJpeg
    {
        readonly TiffImage _img;
        readonly long[] _offs, _cnts;

        public TiffOldJpeg(TiffImage img, long[] offs, long[] cnts)
        {
            _img = img; _offs = offs; _cnts = cnts;
        }

        byte[] Read(long offset, long count)
        {
            var src = _img.File.Src;
            if (offset < 0 || count < 0 || offset > src.Length || count > src.Length - offset || count > int.MaxValue - 64)
                throw new ImageFormatException("TIFF: old-style JPEG data outside of the file");
            var b = new byte[count];
            src.Read(offset, b);
            return b;
        }

        public byte[] BuildStream()
        {
            var img = _img;
            var file = img.File;
            var e = img.Entries;
            long len = file.Src.Length;
            long jo = file.Integer(e, 513, 0), jl = file.Integer(e, 514, 0);
            int dri = (int)file.Integer(e, 515, 0);
            var dqt = new List<byte[]>();
            var dht = new List<byte[]>();
            byte[]? sof = null, sos = null;
            long scanStart = -1;

            if (jo > 0 && jo < len)
            {
                long limit = jl > 0 ? Math.Min(jo + jl, len) : len;
                var h = Read(jo, Math.Min(limit - jo, 1 << 20));
                int p = 0;
                while (p + 4 <= h.Length && h[p] == 0xFF)
                {
                    int m = h[p + 1];
                    if (m == 0xD8 || m == 0xFF) { p += m == 0xFF ? 1 : 2; continue; }
                    if (m == 0xD9) break;
                    int sl = h[p + 2] << 8 | h[p + 3];
                    if (sl < 2 || p + 2 + sl > h.Length) break;
                    var seg = h.AsSpan(p, 2 + sl).ToArray();
                    if (m == 0xDB) dqt.Add(seg);
                    else if (m == 0xC4) dht.Add(seg);
                    else if (m is 0xC0 or 0xC1 or 0xC2) sof = seg;
                    else if (m == 0xDD && sl >= 4) dri = h[p + 4] << 8 | h[p + 5];
                    else if (m == 0xDA) { sos = seg; scanStart = jo + p + 2 + sl; break; }
                    p += 2 + sl;
                }
                // A complete stream that holds the strips (or is all there is): take it as it stands.
                bool hasStrips = false;
                foreach (long c in _cnts) hasStrips |= c > 0;
                if (sof != null && sos != null && jl > 0 && (!hasStrips || (_offs[0] >= jo && _offs[0] < jo + jl)))
                    return Read(jo, limit - jo);
            }

            long total = 0;
            foreach (long c in _cnts) total += c;
            if ((double)img.Width * img.Height * img.Spp > (total + 1024.0) * 4096)
                throw new ImageFormatException("TIFF: image size inconsistent with the data size");

            var ms = new MemoryStream();
            // strips that are JPEG streams of their own need nothing added
            Span<byte> probe = stackalloc byte[2];
            bool selfContained = false;
            if (_cnts[0] >= 2) { file.Src.Read(_offs[0], probe); selfContained = probe[0] == 0xFF && probe[1] == 0xD8; }
            if (!selfContained)
            {
                int n = img.Spp;
                ms.Write(new byte[] { 0xFF, 0xD8 });
                if (dqt.Count == 0)
                {
                    var q = file.Integers(e, 519);
                    if (q == null || q.Length < n) throw new ImageFormatException("TIFF: old-style JPEG without quantization tables");
                    for (int i = 0; i < n; i++)
                    {
                        ms.Write(new byte[] { 0xFF, 0xDB, 0, 67, (byte)i });
                        ms.Write(Read(q[i], 64));
                    }
                }
                else foreach (var s in dqt) ms.Write(s);
                if (dht.Count == 0)
                {
                    var dc = file.Integers(e, 520); var ac = file.Integers(e, 521);
                    if (dc == null || ac == null || dc.Length < n || ac.Length < n) throw new ImageFormatException("TIFF: old-style JPEG without Huffman tables");
                    for (int i = 0; i < n; i++)
                    {
                        WriteHuffman(ms, dc[i], i);
                        WriteHuffman(ms, ac[i], 0x10 | i);
                    }
                }
                else foreach (var s in dht) ms.Write(s);
                if (dri > 0) ms.Write(new byte[] { 0xFF, 0xDD, 0, 4, (byte)(dri >> 8), (byte)dri });
                if (sof == null || sos == null)
                {
                    // component ids 0..n-1 with tables of the same index, luma carrying the subsampling factors
                    var f = new byte[10 + 3 * n];
                    f[0] = 0xFF; f[1] = 0xC0; f[3] = (byte)(8 + 3 * n); f[4] = 8;
                    BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(5), (ushort)Math.Min(img.Height, 65535));
                    BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(7), (ushort)Math.Min(img.Width, 65535));
                    f[9] = (byte)n;
                    var s = new byte[8 + 2 * n];
                    s[0] = 0xFF; s[1] = 0xDA; s[3] = (byte)(6 + 2 * n); s[4] = (byte)n;
                    for (int i = 0; i < n; i++)
                    {
                        f[10 + 3 * i] = (byte)i;
                        f[11 + 3 * i] = (byte)(i == 0 && img.Photometric == 6 ? img.SubH << 4 | img.SubV : 0x11);
                        f[12 + 3 * i] = (byte)i;
                        s[5 + 2 * i] = (byte)i;
                        s[6 + 2 * i] = (byte)(i << 4 | i);
                    }
                    s[5 + 2 * n] = 0; s[6 + 2 * n] = 63; s[7 + 2 * n] = 0;
                    sof = f; sos = s;
                }
                ms.Write(sof);
                ms.Write(sos);
            }
            for (int i = 0; i < _cnts.Length; i++)
            {
                if (_cnts[i] == 0) continue;
                var d = Read(_offs[i], _cnts[i]);
                ms.Write(d);
                // the strips are restart intervals of one scan: make sure a restart marker separates them
                bool last = i == _cnts.Length - 1;
                bool hasMarker = d.Length >= 2 && d[^2] == 0xFF && d[^1] >= 0xD0 && d[^1] <= 0xD9;
                if (!last && !selfContained && dri > 0 && !hasMarker) ms.Write(new byte[] { 0xFF, (byte)(0xD0 + (i & 7)) });
            }
            ms.Write(new byte[] { 0xFF, 0xD9 });
            return ms.ToArray();
        }

        void WriteHuffman(MemoryStream ms, long offset, int id)
        {
            var counts = Read(offset, 16);
            int n = 0;
            foreach (byte c in counts) n += c;
            if (n > 256) throw new ImageFormatException("TIFF: corrupt old-style JPEG Huffman table");
            ms.Write(new byte[] { 0xFF, 0xC4, (byte)((19 + n) >> 8), (byte)(19 + n), (byte)id });
            ms.Write(counts);
            ms.Write(Read(offset + 16, n));
        }
    }
}
