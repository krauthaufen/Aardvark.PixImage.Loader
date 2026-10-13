using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using PixCodec.Compression;

namespace PixCodec
{
    /// <summary>
    /// TIFF writer. File layout: header, strip/tile data, then the IFD with its out-of-line values. The header needs
    /// the IFD offset, so compressed output to a non-seekable stream is buffered; seekable streams get the header
    /// patched at the end and uncompressed files have a layout that is known up front.
    /// </summary>
    internal static unsafe class TiffEncoder
    {
        sealed class Field
        {
            public ushort Tag, Type;
            public long Count;
            public byte[] Data = Array.Empty<byte>();
        }

        struct Chunk
        {
            public byte[] Buf;
            public int Len;
            public bool Pooled;
        }

        public static void Encode(RawImage image, Stream output, TiffEncodeOptions o)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));
            if (output == null) throw new ArgumentNullException(nameof(output));
            int w = image.Width, h = image.Height, spp = image.Channels;
            var type = image.SampleType;
            int bps = RawImage.BytesPerSample(type);
            bool isFloat = type is SampleType.Float16 or SampleType.Float32 or SampleType.Float64;
            int comp = o.Compression switch
            {
                TiffCompression.None => 1,
                TiffCompression.Lzw => 5,
                TiffCompression.Deflate => 8,
                TiffCompression.PackBits => 32773,
                _ => throw new ArgumentException("unknown TIFF compression"),
            };
            if (o.Predictor == TiffPredictor.FloatingPoint && !isFloat)
                throw new ArgumentException("the floating point predictor needs float samples");
            // libtiff implements predictors only for LZW and Deflate and ignores the tag otherwise, so a predictor
            // combined with PackBits or no compression would make files that most readers show as garbage.
            int pred = comp != 5 && comp != 8 ? 1 : o.Predictor switch
            {
                TiffPredictor.None => 1,
                TiffPredictor.Horizontal => 2,
                TiffPredictor.FloatingPoint => 3,
                _ => isFloat ? 3 : 2,
            };

            long rowBytes = (long)w * spp * bps;
            bool tiled = o.TileWidth > 0 && o.TileHeight > 0;
            int cw, ch;
            if (tiled)
            {
                cw = (o.TileWidth + 15) / 16 * 16; ch = (o.TileHeight + 15) / 16 * 16;
            }
            else
            {
                cw = w;
                long rps = o.RowsPerStrip > 0 ? o.RowsPerStrip : Math.Max(1, (65536 + rowBytes - 1) / rowBytes);
                ch = (int)Math.Min(rps, h);
            }
            long chunkRowBytes = (long)cw * spp * bps;
            if (chunkRowBytes * ch > int.MaxValue / 2)
                throw new ArgumentException("TIFF: strip/tile size too large, use smaller strips or tiles");
            int across = (w + cw - 1) / cw, down = (h + ch - 1) / ch;
            int n = checked(across * down);
            long rawTotal = rowBytes * h;

            long worst = tiled ? chunkRowBytes * ch * n : rawTotal;
            if (comp != 1) worst += worst / 2 + 4096L * n;
            var icc = image.Metadata?.IccProfile;
            worst += 4096 + 16L * n + (icc?.Length ?? 0);
            bool big = o.BigTiff ?? worst > uint.MaxValue - 65536L;
            int hs = big ? 16 : 8;

            var offsets = new long[n];
            var counts = new long[n];
            long origin = output.CanSeek ? output.Position : 0;
            var pin = GCHandle.Alloc(image.Data, GCHandleType.Pinned);
            try
            {
                byte* pixels = (byte*)pin.AddrOfPinnedObject();
                long pos = hs;
                if (comp == 1 && !tiled && BitConverter.IsLittleEndian)
                {
                    // the pixel array already is the file content
                    for (int i = 0; i < n; i++)
                    {
                        offsets[i] = pos;
                        counts[i] = Math.Min(ch, h - (long)i * ch) * rowBytes;
                        pos += counts[i];
                    }
                    output.Write(Header(big, pos + (pos & 1)));
                    for (long done = 0; done < rawTotal;)
                    {
                        int len = (int)Math.Min(rawTotal - done, 1 << 26);
                        output.Write(new ReadOnlySpan<byte>(pixels + done, len));
                        done += len;
                    }
                }
                else
                {
                    bool buffered = !output.CanSeek;
                    var held = buffered ? new List<Chunk>(n) : null;
                    if (!buffered) output.Write(Header(big, 0));
                    bool par = o.Parallel && n > 1 && comp != 1 && rawTotal >= 256 * 1024;
                    int batch = par ? Math.Min(n, Environment.ProcessorCount * 4) : 1;
                    var results = new Chunk[batch];
                    var ctx = new Context
                    {
                        Pixels = pixels, W = w, H = h, Spp = spp, Bps = bps, Cw = cw, Ch = ch, Across = across, Tiled = tiled,
                        Comp = comp, Pred = pred, Level = o.CompressionLevel,
                    };
                    for (int start = 0; start < n; start += batch)
                    {
                        int cnt = Math.Min(batch, n - start);
                        if (cnt > 1)
                        {
                            int s0 = start;
                            Parallel.For(0, cnt, i => results[i] = ctx.Compress(s0 + i));
                        }
                        else results[0] = ctx.Compress(start);
                        for (int i = 0; i < cnt; i++)
                        {
                            var c = results[i];
                            offsets[start + i] = pos; counts[start + i] = c.Len;
                            pos += c.Len;
                            if (buffered) held!.Add(c);
                            else
                            {
                                output.Write(c.Buf, 0, c.Len);
                                if (c.Pooled) ArrayPool<byte>.Shared.Return(c.Buf);
                            }
                        }
                    }
                    if (buffered)
                    {
                        output.Write(Header(big, pos + (pos & 1)));
                        foreach (var c in held!)
                        {
                            output.Write(c.Buf, 0, c.Len);
                            if (c.Pooled) ArrayPool<byte>.Shared.Return(c.Buf);
                        }
                    }
                }
                if ((pos & 1) != 0) { output.WriteByte(0); pos++; }
                if (!big && pos > uint.MaxValue - 65536L)
                    throw new InvalidOperationException("TIFF: output exceeds 4 GB, set BigTiff = true");

                var ifd = BuildIfd(image, big, pos, comp, pred, tiled, cw, ch, offsets, counts);
                output.Write(ifd);
                if (output.CanSeek && !(comp == 1 && !tiled && BitConverter.IsLittleEndian))
                {
                    long end = output.Position;
                    output.Position = origin;
                    output.Write(Header(big, pos));
                    output.Position = end;
                }
            }
            finally
            {
                pin.Free();
            }
        }

        static byte[] Header(bool big, long ifdOffset)
        {
            var b = new byte[big ? 16 : 8];
            b[0] = b[1] = (byte)'I';
            if (big)
            {
                b[2] = 43; b[4] = 8;
                BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(8), ifdOffset);
            }
            else
            {
                b[2] = 42;
                BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)ifdOffset);
            }
            return b;
        }

        sealed class Context
        {
            public byte* Pixels;
            public int W, H, Spp, Bps, Cw, Ch, Across, Comp, Pred, Level;
            public bool Tiled;

            /// <summary>Gathers, predicts and compresses one strip or tile.</summary>
            public Chunk Compress(int index)
            {
                int x0 = index % Across * Cw, y0 = index / Across * Ch;
                int rows = Tiled ? Ch : Math.Min(Ch, H - y0);
                int pix = Spp * Bps;
                int rowBytes = Cw * pix;
                int size = rowBytes * rows;
                var raw = ArrayPool<byte>.Shared.Rent(Math.Max(size, 1));
                bool keepRaw = false;
                try
                {
                    fixed (byte* r = raw)
                    {
                        int copyCols = Math.Min(Cw, W - x0), copyRows = Math.Min(rows, H - y0);
                        long srcRow = (long)W * pix;
                        if (copyCols < Cw || copyRows < rows) new Span<byte>(r, size).Clear();
                        for (int y = 0; y < copyRows; y++)
                            Buffer.MemoryCopy(Pixels + (y0 + y) * srcRow + (long)x0 * pix, r + (long)y * rowBytes, copyCols * pix, copyCols * pix);

                        if (Pred == 2)
                        {
                            for (int y = 0; y < rows; y++) TiffPredict.ApplyHorizontal(r + (long)y * rowBytes, Cw * Spp, Spp, Bps);
                        }
                        else if (Pred == 3)
                        {
                            var tmp = ArrayPool<byte>.Shared.Rent(rowBytes);
                            fixed (byte* t = tmp)
                                for (int y = 0; y < rows; y++) TiffPredict.ApplyFloat(r + (long)y * rowBytes, Cw * Spp, Spp, Bps, t);
                            ArrayPool<byte>.Shared.Return(tmp);
                        }
                        // the file is little endian; predictor 3 output is byte-order neutral
                        if (!BitConverter.IsLittleEndian && Bps > 1 && Pred != 3) TiffPredict.SwapBytes(r, size, Bps);
                    }
                    var src = raw.AsSpan(0, size);
                    switch (Comp)
                    {
                        case 5:
                        {
                            var dst = ArrayPool<byte>.Shared.Rent(TiffLzw.MaxEncodedSize(size));
                            return new Chunk { Buf = dst, Len = TiffLzw.Encode(src, dst), Pooled = true };
                        }
                        case 32773:
                        {
                            var dst = ArrayPool<byte>.Shared.Rent(TiffPackBits.MaxEncodedSize(size, rowBytes));
                            return new Chunk { Buf = dst, Len = TiffPackBits.Encode(src, rowBytes, dst), Pooled = true };
                        }
                        case 8:
                        {
                            var z = Zlib.Compress(src, Level);
                            return new Chunk { Buf = z, Len = z.Length };
                        }
                        default:
                            keepRaw = true;
                            return new Chunk { Buf = raw, Len = size, Pooled = true };
                    }
                }
                finally
                {
                    if (!keepRaw) ArrayPool<byte>.Shared.Return(raw);
                }
            }
        }

        static byte[] Shorts(params int[] v)
        {
            var b = new byte[v.Length * 2];
            for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(i * 2), (ushort)v[i]);
            return b;
        }

        static byte[] Longs(bool wide, params long[] v)
        {
            int s = wide ? 8 : 4;
            var b = new byte[v.Length * s];
            for (int i = 0; i < v.Length; i++)
            {
                if (wide) BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(i * 8), v[i]);
                else BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 4), (uint)v[i]);
            }
            return b;
        }

        static byte[] Rational(double v)
        {
            uint den = 10000;
            while (den > 1 && v * den > uint.MaxValue) den /= 10;
            var b = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)Math.Min(uint.MaxValue, Math.Round(v * den)));
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), den);
            return b;
        }

        static int[] Repeat(int value, int n)
        {
            var r = new int[n];
            Array.Fill(r, value);
            return r;
        }

        static byte[] BuildIfd(RawImage image, bool big, long ifdOffset, int comp, int pred, bool tiled, int cw, int ch, long[] offsets, long[] counts)
        {
            int spp = image.Channels;
            var type = image.SampleType;
            int bits = RawImage.BytesPerSample(type) * 8;
            int format = type switch
            {
                SampleType.UInt8 or SampleType.UInt16 or SampleType.UInt32 => 1,
                SampleType.Int8 or SampleType.Int16 or SampleType.Int32 => 2,
                _ => 3,
            };
            // Colour mapping: Gray/GrayAlpha -> MinIsBlack, Rgb/Rgba -> RGB with unassociated alpha as the extra sample.
            // ColorModel.Other is written as MinIsBlack with (channels - 1) unspecified extra samples.
            int photometric = image.Color is ColorModel.Rgb or ColorModel.Rgba ? 2 : 1;
            int[] extra = image.Color switch
            {
                ColorModel.GrayAlpha or ColorModel.Rgba => new[] { 2 },
                ColorModel.Other => Repeat(0, spp - 1),
                _ => Array.Empty<int>(),
            };
            var meta = image.Metadata;
            var f = new List<Field>();
            void Add(int tag, int t, long count, byte[] data) => f.Add(new Field { Tag = (ushort)tag, Type = (ushort)t, Count = count, Data = data });
            const int SHORT = 3, LONG = 4, RATIONAL = 5, UNDEFINED = 7, LONG8 = 16;

            Add(256, LONG, 1, Longs(false, image.Width));
            Add(257, LONG, 1, Longs(false, image.Height));
            Add(258, SHORT, spp, Shorts(Repeat(bits, spp)));
            Add(259, SHORT, 1, Shorts(comp));
            Add(262, SHORT, 1, Shorts(photometric));
            if (!tiled) Add(273, big ? LONG8 : LONG, offsets.Length, Longs(big, offsets));
            if (meta != null && meta.Orientation >= 1 && meta.Orientation <= 8) Add(274, SHORT, 1, Shorts(meta.Orientation));
            Add(277, SHORT, 1, Shorts(spp));
            if (!tiled)
            {
                Add(278, LONG, 1, Longs(false, ch));
                Add(279, big ? LONG8 : LONG, counts.Length, Longs(big, counts));
            }
            bool dpi = meta != null && meta.DpiX > 0 && meta.DpiY > 0 && double.IsFinite(meta.DpiX) && double.IsFinite(meta.DpiY);
            if (dpi)
            {
                Add(282, RATIONAL, 1, Rational(meta!.DpiX));
                Add(283, RATIONAL, 1, Rational(meta.DpiY));
            }
            Add(284, SHORT, 1, Shorts(1));
            if (dpi) Add(296, SHORT, 1, Shorts(2));
            if (pred != 1) Add(317, SHORT, 1, Shorts(pred));
            if (tiled)
            {
                Add(322, LONG, 1, Longs(false, cw));
                Add(323, LONG, 1, Longs(false, ch));
                Add(324, big ? LONG8 : LONG, offsets.Length, Longs(big, offsets));
                Add(325, big ? LONG8 : LONG, counts.Length, Longs(big, counts));
            }
            if (extra.Length > 0) Add(338, SHORT, extra.Length, Shorts(extra));
            Add(339, SHORT, spp, Shorts(Repeat(format, spp)));
            if (meta?.IccProfile is { Length: > 0 } icc) Add(34675, UNDEFINED, icc.Length, icc);
            f.Sort((a, b) => a.Tag.CompareTo(b.Tag));

            int cs = big ? 8 : 2, es = big ? 20 : 12, os = big ? 8 : 4;
            long dirSize = cs + (long)f.Count * es + os;
            long extraSize = 0;
            foreach (var x in f) if (x.Data.Length > os) extraSize += x.Data.Length + (x.Data.Length & 1);
            if (dirSize + extraSize > int.MaxValue - 64) throw new ImageNotSupportedException("TIFF: directory too large");
            var b = new byte[dirSize + extraSize];
            if (big) BinaryPrimitives.WriteInt64LittleEndian(b, f.Count);
            else BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)f.Count);
            long valuePos = dirSize;
            for (int i = 0; i < f.Count; i++)
            {
                var x = f[i];
                var e = b.AsSpan(cs + i * es, es);
                BinaryPrimitives.WriteUInt16LittleEndian(e, x.Tag);
                BinaryPrimitives.WriteUInt16LittleEndian(e.Slice(2), x.Type);
                Span<byte> v;
                if (big) { BinaryPrimitives.WriteInt64LittleEndian(e.Slice(4), x.Count); v = e.Slice(12); }
                else { BinaryPrimitives.WriteUInt32LittleEndian(e.Slice(4), (uint)x.Count); v = e.Slice(8); }
                if (x.Data.Length <= os) x.Data.CopyTo(v);
                else
                {
                    if (big) BinaryPrimitives.WriteInt64LittleEndian(v, ifdOffset + valuePos);
                    else BinaryPrimitives.WriteUInt32LittleEndian(v, checked((uint)(ifdOffset + valuePos)));
                    x.Data.CopyTo(b.AsSpan((int)valuePos));
                    valuePos += x.Data.Length + (x.Data.Length & 1);
                }
            }
            return b;
        }
    }

    /// <summary>CCITT Group 3 / Group 4 fax decoding (compression 2, 3, 4).</summary>
    internal static unsafe class TiffCcitt
    {
        public static void Decode(ReadOnlySpan<byte> src, byte* dst, int width, int rows, int compression, long options)
            => throw new ImageNotSupportedException("TIFF: CCITT compression is not supported");
    }
}
