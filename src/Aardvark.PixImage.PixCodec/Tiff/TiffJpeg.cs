using System.Runtime.InteropServices;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Aardvark.ImageCodecs
{
    /// <summary>The calls into the JPEG codec that TIFF needs, in one place.</summary>
    internal static class TiffJpegBridge
    {
        /// <summary>Parses the JPEGTables tag once per image (null when the tag is absent).</summary>
        public static JpegTiffTables? Prepare(byte[] jpegTables) => jpegTables.Length > 0 ? JpegTiffTables.Parse(jpegTables) : null;

        /// <summary>One strip/tile with up to 8 bits per sample, see <see cref="TiffJpegDecoder"/> for the contract.</summary>
        public static void Decode8(JpegTiffTables? prepared, ReadOnlySpan<byte> tables, ReadOnlySpan<byte> data, int width, int height, int components, bool ycc, Span<byte> dst)
        {
            if (Tiff.JpegDecoder != null) Tiff.JpegDecoder(tables, data, width, height, components, ycc, dst);
            else Jpeg.DecodeForTiff(prepared, data, width, height, components, ycc, dst);
        }

        /// <summary>
        /// One strip/tile with 9..16 bits per sample (12-bit DCT or lossless), values as coded. Lossless frames may be
        /// shaped differently from the tile, the way DNG stores colour filter array data (a W x H single sample tile as
        /// a W/2 x H two component frame): such a frame is read as flat rows of samples.
        /// </summary>
        public static void Decode16(JpegTiffTables? prepared, ReadOnlySpan<byte> data, int width, int height, int components, bool ycc, Span<ushort> dst)
        {
            var info = Jpeg.GetTiffStreamInfo(data);
            long rowSamples = (long)info.Width * info.Components, need = (long)width * components;
            bool flatShape = info.Lossless && rowSamples * info.Height == need * height && rowSamples != need;
            if (!info.Lossless || (info.Components == components && info.Width >= width && !flatShape))
            {
                Jpeg.DecodeForTiff(prepared, data, width, height, components, ycc, dst);
                return;
            }
            if (flatShape)
            {
                // DNG: "only the total sample counts need to match" - the frame is the tile's samples in scan order
                // (e.g. two tile rows per frame row, W*2 x H/2)
                if (need * height > Array.MaxLength) throw new ImageNotSupportedException("TIFF: lossless JPEG frame too large");
                var flat = new ushort[need * height];
                Jpeg.DecodeForTiff(prepared, data, info.Width, info.Height, info.Components, false, flat);
                flat.CopyTo(dst);
                return;
            }
            if (rowSamples < need || info.Height < height || rowSamples * info.Height > Array.MaxLength)
                throw new ImageFormatException($"TIFF: lossless JPEG frame {info.Width}x{info.Height}x{info.Components} does not cover the {width}x{height}x{components} strip or tile");
            var frame = new ushort[rowSamples * info.Height];
            Jpeg.DecodeForTiff(prepared, data, info.Width, info.Height, info.Components, false, frame);
            for (int y = 0; y < height; y++)
                frame.AsSpan((int)(y * rowSamples), (int)need).CopyTo(dst.Slice((int)(y * need), (int)need));
        }
    }

    /// <summary>
    /// Old-style JPEG (compression 6, TIFF 6.0 section 22). A frame (the whole image, one tile, or one plane of
    /// them with PlanarConfiguration 2) is spread over tags: an optional "interchange format" stream
    /// (JPEGInterchangeFormat, either complete or only the header with the tables), separate quantization / Huffman
    /// tables (JPEGQTables, JPEGDCTables, JPEGACTables) and the entropy coded data in the strips or the tile.
    /// JPEGProc 14 selects lossless coding (JPEGLosslessPredictors, JPEGPointTransforms, DC tables only).
    /// This class glues whatever is there into a normal JPEG stream.
    /// </summary>
    internal sealed class TiffOldJpeg
    {
        readonly TiffImage _img;
        readonly long[] _offs, _cnts;
        readonly int _stripsPerPlane;

        public TiffOldJpeg(TiffImage img, long[] offs, long[] cnts, int stripsPerPlane)
        {
            _img = img; _offs = offs; _cnts = cnts; _stripsPerPlane = stripsPerPlane;
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

        /// <summary>
        /// The JPEG stream of one frame of <paramref name="width"/> x <paramref name="height"/>:
        /// <paramref name="tile"/> &gt;= 0 selects that tile (index into the tile arrays), otherwise all strips of
        /// <paramref name="plane"/> are joined.
        /// </summary>
        public byte[] BuildStream(int plane, int tile, int width, int height)
        {
            var img = _img;
            var file = img.File;
            var e = img.Entries;
            long len = file.Src.Length;
            bool planar = img.Planar == 2;
            bool lossless = file.Integer(e, 512, 1) == 14;
            int n = planar ? 1 : img.Spp;
            int first = tile >= 0 ? tile : plane * _stripsPerPlane, chunks = tile >= 0 ? 1 : _stripsPerPlane;
            long jo = file.Integer(e, 513, 0), jl = file.Integer(e, 514, 0);
            int dri = (int)file.Integer(e, 515, 0);
            var dqt = new List<byte[]>();
            var dht = new List<byte[]>();
            byte[]? sof = null, sos = null;

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
                    else if (m is 0xC0 or 0xC1 or 0xC2 or 0xC3) sof = seg;
                    else if (m == 0xDD && sl >= 4) dri = h[p + 4] << 8 | h[p + 5];
                    else if (m == 0xDA) { sos = seg; break; }
                    p += 2 + sl;
                }
                // A complete stream that holds the strips (or is all there is): take it as it stands.
                bool hasStrips = false;
                foreach (long c in _cnts) hasStrips |= c > 0;
                if (tile < 0 && !planar && sof != null && sos != null && jl > 0 && (!hasStrips || (_offs[0] >= jo && _offs[0] < jo + jl)))
                    return Read(jo, limit - jo);
                // a frame / scan header found there describes the whole image, not a tile or a plane
                if (tile >= 0 || planar) { sof = null; sos = null; }
            }

            long total = 0;
            for (int i = first; i < first + chunks; i++) total += _cnts[i];
            if ((double)width * height * n > (total + 1024.0) * 4096)
                throw new ImageFormatException("TIFF: image size inconsistent with the data size");

            var ms = new MemoryStream();
            // data that is a JPEG stream of its own needs nothing added
            Span<byte> probe = stackalloc byte[2];
            bool selfContained = false;
            if (_cnts[first] >= 2) { file.Src.Read(_offs[first], probe); selfContained = probe[0] == 0xFF && probe[1] == 0xD8; }
            if (!selfContained)
            {
                int all = img.Spp;
                ms.Write(new byte[] { 0xFF, 0xD8 });
                if (dqt.Count > 0) foreach (var s in dqt) ms.Write(s);
                else if (!lossless)
                {
                    var q = file.Integers(e, 519);
                    if (q == null || q.Length < all) throw new ImageFormatException("TIFF: old-style JPEG without quantization tables");
                    for (int i = 0; i < all; i++)
                    {
                        ms.Write(new byte[] { 0xFF, 0xDB, 0, 67, (byte)i });
                        ms.Write(Read(q[i], 64));
                    }
                }
                if (dht.Count > 0) foreach (var s in dht) ms.Write(s);
                else
                {
                    var dc = file.Integers(e, 520); var ac = lossless ? null : file.Integers(e, 521);
                    if (dc == null || dc.Length < all || (!lossless && (ac == null || ac.Length < all)))
                        throw new ImageFormatException("TIFF: old-style JPEG without Huffman tables");
                    for (int i = 0; i < all; i++)
                    {
                        WriteHuffman(ms, dc[i], i);
                        if (ac != null) WriteHuffman(ms, ac[i], 0x10 | i);
                    }
                }
                if (dri > 0) ms.Write(new byte[] { 0xFF, 0xDD, 0, 4, (byte)(dri >> 8), (byte)dri });
                if (sof == null || sos == null)
                {
                    // components numbered like the samples, each with the tables of its number, luma carrying the
                    // subsampling factors; a plane is a frame with that single component
                    var f = new byte[10 + 3 * n];
                    f[0] = 0xFF; f[1] = (byte)(lossless ? 0xC3 : 0xC0); f[3] = (byte)(8 + 3 * n); f[4] = (byte)(lossless ? img.Bits : 8);
                    BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(5), (ushort)Math.Min(height, 65535));
                    BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(7), (ushort)Math.Min(width, 65535));
                    f[9] = (byte)n;
                    var s = new byte[8 + 2 * n];
                    s[0] = 0xFF; s[1] = 0xDA; s[3] = (byte)(6 + 2 * n); s[4] = (byte)n;
                    for (int i = 0; i < n; i++)
                    {
                        int id = planar ? plane : i;
                        f[10 + 3 * i] = (byte)id;
                        f[11 + 3 * i] = (byte)(i == 0 && !planar && img.Photometric == 6 ? img.SubH << 4 | img.SubV : 0x11);
                        f[12 + 3 * i] = (byte)(lossless ? 0 : id);
                        s[5 + 2 * i] = (byte)id;
                        s[6 + 2 * i] = (byte)(lossless ? id << 4 : id << 4 | id);
                    }
                    if (lossless)
                    {
                        // Ss = predictor, Al = point transform
                        s[5 + 2 * n] = (byte)Math.Clamp(file.Integer(e, 517, 1), 1, 7);
                        s[6 + 2 * n] = 0;
                        s[7 + 2 * n] = (byte)Math.Clamp(file.Integer(e, 518, 0), 0, 15);
                    }
                    else { s[5 + 2 * n] = 0; s[6 + 2 * n] = 63; s[7 + 2 * n] = 0; }
                    sof = f; sos = s;
                }
                ms.Write(sof);
                ms.Write(sos);
            }
            for (int i = first; i < first + chunks; i++)
            {
                if (_cnts[i] == 0) continue;
                var d = Read(_offs[i], _cnts[i]);
                ms.Write(d);
                // the strips are restart intervals of one scan: make sure a restart marker separates them
                bool last = i == first + chunks - 1;
                bool hasMarker = d.Length >= 2 && d[^2] == 0xFF && d[^1] >= 0xD0 && d[^1] <= 0xD9;
                if (!last && !selfContained && dri > 0 && !hasMarker) ms.Write(new byte[] { 0xFF, (byte)(0xD0 + ((i - first) & 7)) });
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

namespace Aardvark.ImageCodecs
{
    /// <summary>The calls into the LERC and JPEG 2000 codecs that the TIFF codec needs, in one place.</summary>
    internal static class TiffForeignCodecs
    {
        /// <summary>
        /// Decodes one LERC blob of width x height pixels into interleaved native samples (channels per pixel in
        /// <paramref name="destination"/>; the blob may hold fewer). Blob channel = band * depth + index.
        /// <paramref name="masks"/> (channels * width * height bytes) receives 1 = valid, band after band, for as many
        /// bands as the blob has masks: one (shared by all bands) or one per band.
        /// </summary>
        public static void LercDecode(ReadOnlySpan<byte> blob, SampleType type, int width, int height, Span<byte> destination, int channels,
            Span<byte> masks, out int bands, out int depth, out int maskCount)
        {
            var info = Lerc.GetInfo(blob);
            if (info.Width != width || info.Height != height) throw new ImageFormatException("TIFF: LERC strip/tile does not match the image layout");
            if (info.SampleType != type) throw new ImageFormatException("TIFF: LERC strip/tile holds another data type than the image");
            bands = info.Bands; depth = info.Depth;
            if (info.Channels > channels || info.Channels < 1) throw new ImageFormatException("TIFF: LERC strip/tile does not match the image layout");
            var img = Lerc.Decode(blob, out var valid, new LercDecodeOptions { MaxPixels = (long)width * height });
            int n = width * height, bps = RawImage.BytesPerSample(type), have = img.Channels;
            var src = img.GetBytes();
            if (have == channels) src.CopyTo(destination);
            else
                for (int i = 0; i < n; i++) src.Slice(i * have * bps, have * bps).CopyTo(destination.Slice(i * channels * bps));
            maskCount = valid == null ? 0 : valid.Length / n;
            valid?.CopyTo(masks);
        }

        /// <summary>
        /// Encodes width x height pixels of <paramref name="samples"/> interleaved native samples as one strip / tile.
        /// <paramref name="rgb"/>: the first three samples are RGB (JPEG 2000 then uses its reversible colour transform).
        /// </summary>
        public static void Encode(int compression, byte[] pixels, int width, int height, int samples, SampleType type, bool rgb, TiffEncodeOptions options, Stream output)
        {
            if (compression != 34712) throw new ArgumentException("unknown compression");
            int bps = RawImage.BytesPerSample(type);
            bool signed = type is SampleType.Int8 or SampleType.Int16;
            var planes = new J2kPlanes { Width = width, Height = height, Planes = new J2kPlane[samples] };
            int n = width * height;
            for (int c = 0; c < samples; c++)
            {
                var d = new int[n];
                if (bps == 1)
                    for (int i = 0, k = c; i < n; i++, k += samples) d[i] = signed ? (sbyte)pixels[k] : pixels[k];
                else
                {
                    var px = MemoryMarshal.Cast<byte, ushort>(pixels);
                    for (int i = 0, k = c; i < n; i++, k += samples) d[i] = signed ? (short)px[k] : px[k];
                }
                planes.Planes[c] = new J2kPlane { Width = width, Height = height, Precision = bps * 8, Signed = signed, Data = d };
            }
            // (strips / tiles are encoded in parallel already)
            output.Write(Jp2.EncodeCodestreamPlanes(planes, new Jp2EncodeOptions { Lossless = true, RawCodestream = true, ColorTransform = rgb && samples >= 3, Parallel = false }));
        }
    }
}
