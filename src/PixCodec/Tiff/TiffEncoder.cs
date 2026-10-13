using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using PixCodec.Compression;

namespace PixCodec
{
    /// <summary>
    /// TIFF writer. File layout: header, then per image its directory (with the out-of-line values and EXIF
    /// directories) followed by its strip/tile data. A directory needs the sizes of the compressed strips, so
    /// compressed output to a non-seekable stream is buffered one image at a time; a seekable stream gets a
    /// placeholder directory that is rewritten once the data is out, and uncompressed images have a layout that is
    /// known up front. All three ways produce the same bytes.
    /// </summary>
    internal static unsafe class TiffEncoder
    {
        struct Chunk
        {
            public byte[] Buf;
            public int Len;
            public bool Pooled;
        }

        const string TooBig = "TIFF: the output exceeds 4 GB, which needs BigTiff (leave BigTiff unset or set it to true)";
        const int None = 1, Ccitt4 = 4, Lzw = 5, JpegComp = 7, Deflate = 8, PackBits = 32773;

        /// <summary>Everything derived from one image and its options before a byte is written.</summary>
        sealed class Layout
        {
            public RawImage Image = null!;
            public TiffEncodeOptions Options = null!;
            public int W, H, Spp, Bps, Comp, Pred, Cw, Ch, Across, Down, N;
            public bool Tiled, Bilevel, Direct;
            public long RowBytes, ChunkRowBytes, RawTotal;
            public int SubH = 1, SubV = 1;
            public JpegEncodeOptions? Jpeg;
            public long[] Offsets = Array.Empty<long>(), Counts = Array.Empty<long>();

            public Layout(RawImage image, TiffEncodeOptions o, bool bigEndian)
            {
                Image = image ?? throw new ArgumentNullException(nameof(image));
                Options = o;
                W = image.Width; H = image.Height; Spp = image.Channels;
                var type = image.SampleType;
                Bps = RawImage.BytesPerSample(type);
                bool isFloat = type is SampleType.Float16 or SampleType.Float32 or SampleType.Float64;
                Comp = o.Compression switch
                {
                    TiffCompression.None => None,
                    TiffCompression.Lzw => Lzw,
                    TiffCompression.Deflate => Deflate,
                    TiffCompression.PackBits => PackBits,
                    TiffCompression.Jpeg => JpegComp,
                    TiffCompression.CcittGroup4 => Ccitt4,
                    _ => throw new ArgumentException("unknown TIFF compression"),
                };
                Bilevel = o.Bilevel || Comp == Ccitt4;
                if (Bilevel && (type != SampleType.UInt8 || Spp != 1))
                    throw new ArgumentException("bilevel TIFF output needs an 8-bit single channel image");
                if (Comp == JpegComp)
                {
                    if (type != SampleType.UInt8 || !(image.Color is ColorModel.Gray or ColorModel.Rgb))
                        throw new ArgumentException("JPEG compressed TIFF output needs an 8-bit Gray or Rgb image");
                    Jpeg = new JpegEncodeOptions { Quality = o.JpegQuality, Subsampling = o.JpegSubsampling, OptimizeHuffman = true };
                    if (Spp == 3) (SubH, SubV) = PixCodec.Jpeg.ResolveSubsampling(Jpeg);
                }
                if (o.Predictor == TiffPredictor.FloatingPoint && !isFloat)
                    throw new ArgumentException("the floating point predictor needs float samples");
                // libtiff implements predictors only for LZW and Deflate and ignores the tag otherwise, so a predictor
                // combined with PackBits or no compression would make files that most readers show as garbage.
                Pred = (Comp != Lzw && Comp != Deflate) || Bilevel ? 1 : o.Predictor switch
                {
                    TiffPredictor.None => 1,
                    TiffPredictor.Horizontal => 2,
                    TiffPredictor.FloatingPoint => 3,
                    _ => isFloat ? 3 : 2,
                };

                RowBytes = Bilevel ? (W + 7) / 8 : (long)W * Spp * Bps;
                Tiled = o.TileWidth > 0 && o.TileHeight > 0;
                if (Tiled)
                {
                    Cw = (o.TileWidth + 15) / 16 * 16; Ch = (o.TileHeight + 15) / 16 * 16;
                }
                else
                {
                    Cw = W;
                    long rps = o.RowsPerStrip > 0 ? o.RowsPerStrip : Math.Max(1, (65536 + RowBytes - 1) / RowBytes);
                    // JPEG strips have to hold whole MCU rows
                    if (Comp == JpegComp) rps = Math.Max(16, (rps + 15) / 16 * 16);
                    Ch = (int)Math.Min(rps, H);
                }
                ChunkRowBytes = Bilevel ? (Cw + 7) / 8 : (long)Cw * Spp * Bps;
                // the pixel array already is the file content
                Direct = Comp == None && !Tiled && !Bilevel && (!bigEndian || Bps == 1);
                // (strips written straight from the pixel array may be of any size)
                if (!Direct && ChunkRowBytes * Ch > int.MaxValue / 2)
                    throw new ArgumentException("TIFF: strip/tile size too large, use smaller strips or tiles");
                Across = (W + Cw - 1) / Cw; Down = (H + Ch - 1) / Ch;
                N = checked(Across * Down);
                RawTotal = RowBytes * H;
                Offsets = new long[N]; Counts = new long[N];
            }

            /// <summary>Upper bound of what this image adds to the file.</summary>
            public long WorstCase()
            {
                long worst = Tiled ? ChunkRowBytes * Ch * N : RawTotal;
                if (Comp != None) worst += worst / 2 + 4096L * N;
                var m = Image.Metadata;
                worst += 8192 + 16L * N + (m?.IccProfile?.Length ?? 0) + (m?.Exif?.Length ?? 0);
                foreach (var t in Options.ExtraTags) worst += 64 + (long)t.Count * 16;
                return worst;
            }
        }

        public static void Encode(IEnumerable<TiffPage> pages, Stream output, TiffEncodeOptions o)
        {
            if (pages == null) throw new ArgumentNullException(nameof(pages));
            if (output == null) throw new ArgumentNullException(nameof(output));
            bool be = o.BigEndian;
            bool big;
            if (o.BigTiff.HasValue) big = o.BigTiff.Value;
            else
            {
                // the choice between classic and BigTIFF needs the total size: look at all pages first
                var list = pages as IReadOnlyCollection<TiffPage> ?? pages.ToList();
                pages = list;
                double worst = 64 + o.TestPadding;
                foreach (var p in list) worst += new Layout(p.Image, p.Options ?? o, be).WorstCase();
                big = worst > uint.MaxValue - 65536.0;
            }

            using var e = pages.GetEnumerator();
            if (!e.MoveNext()) throw new ArgumentException("TIFF: no image to write");
            long origin = output.CanSeek ? output.Position : 0;
            long pos = big ? 16 : 8;
            long first = pos + o.TestPadding + (o.TestPadding & 1);
            if (!big && first > uint.MaxValue) throw new InvalidOperationException(TooBig);
            output.Write(Header(big, be, first));
            if (first > pos)
            {
                var zeros = new byte[1 << 20];
                for (long left = first - pos; left > 0;) { int n = (int)Math.Min(left, zeros.Length); output.Write(zeros, 0, n); left -= n; }
                pos = first;
            }
            var cur = e.Current;
            while (true)
            {
                bool more = e.MoveNext();
                if (cur?.Image == null) throw new ArgumentException("TIFF: page without an image");
                pos = WritePage(cur, cur.Options ?? o, output, origin, pos, big, be, more);
                if (!big && pos > uint.MaxValue) throw new InvalidOperationException(TooBig);
                if (!more) break;
                cur = e.Current;
            }
        }

        static byte[] Header(bool big, bool be, long ifdOffset)
        {
            var b = new byte[big ? 16 : 8];
            b[0] = b[1] = (byte)(be ? 'M' : 'I');
            void U16(int p, int v) { if (be) BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(p), (ushort)v); else BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p), (ushort)v); }
            if (big)
            {
                U16(2, 43); U16(4, 8);
                if (be) BinaryPrimitives.WriteInt64BigEndian(b.AsSpan(8), ifdOffset); else BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(8), ifdOffset);
            }
            else
            {
                U16(2, 42);
                if (be) BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), (uint)ifdOffset); else BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)ifdOffset);
            }
            return b;
        }

        /// <summary>Writes one image at the (even) position <paramref name="pos"/> and returns the position after it.</summary>
        static long WritePage(TiffPage page, TiffEncodeOptions o, Stream output, long origin, long pos, bool big, bool be, bool more)
        {
            var l = new Layout(page.Image, o, be);
            var node = BuildDirectory(l, page, big);
            long start = pos;
            long dirSize = node.Size(big);
            long dataStart = start + dirSize;

            var pin = GCHandle.Alloc(l.Image.Data, GCHandleType.Pinned);
            try
            {
                var ctx = new Context { L = l, Pixels = (byte*)pin.AddrOfPinnedObject(), BigEndian = be };
                if (l.Comp == None)
                {
                    // sizes are known: directory first, then the data, no seeking
                    long p = dataStart;
                    for (int i = 0; i < l.N; i++)
                    {
                        l.Offsets[i] = p;
                        l.Counts[i] = l.Tiled ? l.ChunkRowBytes * l.Ch : Math.Min(l.Ch, l.H - (long)i * l.Ch) * l.RowBytes;
                        p += l.Counts[i];
                    }
                    long end = p + (p & 1);
                    if (!big && end > uint.MaxValue) throw new InvalidOperationException(TooBig);
                    output.Write(Finish(node, l, start, more ? end : 0, big, be));
                    if (l.Direct)
                    {
                        for (long done = 0; done < l.RawTotal;)
                        {
                            int len = (int)Math.Min(l.RawTotal - done, 1 << 26);
                            output.Write(new ReadOnlySpan<byte>(ctx.Pixels + done, len));
                            done += len;
                        }
                    }
                    else
                        for (int i = 0; i < l.N; i++)
                        {
                            var c = ctx.Compress(i);
                            output.Write(c.Buf, 0, c.Len);
                            if (c.Pooled) ArrayPool<byte>.Shared.Return(c.Buf);
                        }
                    if ((p & 1) != 0) output.WriteByte(0);
                    return end;
                }

                bool buffered = !output.CanSeek;
                var held = buffered ? new List<Chunk>(l.N) : null;
                if (!buffered) output.Write(new byte[dirSize]); // placeholder, rewritten below
                bool par = o.Parallel && l.N > 1 && l.RawTotal >= 256 * 1024;
                int batch = par ? Math.Min(l.N, Environment.ProcessorCount * 4) : 1;
                var results = new Chunk[batch];
                long q = dataStart;
                for (int s = 0; s < l.N; s += batch)
                {
                    int cnt = Math.Min(batch, l.N - s);
                    if (cnt > 1)
                    {
                        int s0 = s;
                        Parallel.For(0, cnt, i => results[i] = ctx.Compress(s0 + i));
                    }
                    else results[0] = ctx.Compress(s);
                    for (int i = 0; i < cnt; i++)
                    {
                        var c = results[i];
                        l.Offsets[s + i] = q; l.Counts[s + i] = c.Len;
                        q += c.Len;
                        if (buffered) held!.Add(c);
                        else
                        {
                            output.Write(c.Buf, 0, c.Len);
                            if (c.Pooled) ArrayPool<byte>.Shared.Return(c.Buf);
                        }
                    }
                }
                long stop = q + (q & 1);
                if (!big && stop > uint.MaxValue) throw new InvalidOperationException(TooBig);
                var dir = Finish(node, l, start, more ? stop : 0, big, be);
                if (buffered)
                {
                    output.Write(dir);
                    foreach (var c in held!)
                    {
                        output.Write(c.Buf, 0, c.Len);
                        if (c.Pooled) ArrayPool<byte>.Shared.Return(c.Buf);
                    }
                    if ((q & 1) != 0) output.WriteByte(0);
                }
                else
                {
                    if ((q & 1) != 0) output.WriteByte(0);
                    output.Position = origin + start;
                    output.Write(dir);
                    output.Position = origin + stop;
                }
                return stop;
            }
            finally
            {
                pin.Free();
            }
        }

        static byte[] Finish(TiffIfdNode node, Layout l, long at, long next, bool big, bool be)
        {
            node.Set(TiffField.Longs(l.Tiled ? 324 : 273, big, l.Offsets));
            node.Set(TiffField.Longs(l.Tiled ? 325 : 279, big, l.Counts));
            return node.Serialize(at, next, big, be);
        }

        static int[] Repeat(int value, int n)
        {
            var r = new int[n];
            Array.Fill(r, value);
            return r;
        }

        // tags the encoder always writes itself; callers may not override them through ExtraTags
        static bool IsStructural(int tag) => tag is 254 or 256 or 257 or 258 or 259 or 262 or 266 or 273 or 277 or 278 or 279 or 284 or 317
            or 322 or 323 or 324 or 325 or 338 or 339 or 347 or 530 or 34665 or 34853;

        static TiffIfdNode BuildDirectory(Layout l, TiffPage page, bool big)
        {
            var image = l.Image;
            var o = l.Options;
            int spp = l.Spp;
            var type = image.SampleType;
            int bits = l.Bilevel ? 1 : l.Bps * 8;
            int format = type switch
            {
                SampleType.UInt8 or SampleType.UInt16 or SampleType.UInt32 or SampleType.UInt64 => 1,
                SampleType.Int8 or SampleType.Int16 or SampleType.Int32 or SampleType.Int64 => 2,
                _ => 3,
            };
            // Colour mapping: Gray/GrayAlpha -> MinIsBlack, Rgb/Rgba -> RGB with unassociated alpha as the extra sample.
            // ColorModel.Other is written as MinIsBlack with (channels - 1) unspecified extra samples.
            // JPEG stores RGB as YCbCr; CCITT fax data is MinIsWhite by convention.
            int photometric = image.Color is ColorModel.Rgb or ColorModel.Rgba ? 2 : 1;
            if (l.Comp == JpegComp && spp == 3) photometric = 6;
            if (l.Comp == Ccitt4) photometric = 0;
            int[] extra = image.Color switch
            {
                ColorModel.GrayAlpha or ColorModel.Rgba => new[] { 2 },
                ColorModel.Other => Repeat(0, spp - 1),
                _ => Array.Empty<int>(),
            };
            var meta = image.Metadata;
            var n = new TiffIfdNode();
            if (page.ReducedResolution) n.Set(TiffField.Longs(254, false, 1));
            n.Set(TiffField.Longs(256, false, l.W));
            n.Set(TiffField.Longs(257, false, l.H));
            n.Set(TiffField.Shorts(258, Repeat(bits, spp)));
            n.Set(TiffField.Shorts(259, l.Comp));
            n.Set(TiffField.Shorts(262, photometric));
            n.Set(TiffField.Shorts(277, spp));
            n.Set(TiffField.Shorts(284, 1));
            if (l.Tiled)
            {
                n.Set(TiffField.Longs(322, false, l.Cw));
                n.Set(TiffField.Longs(323, false, l.Ch));
            }
            else n.Set(TiffField.Longs(278, false, l.Ch));
            // placeholders with the final sizes; the values are filled in once the data is laid out
            n.Set(TiffField.Longs(l.Tiled ? 324 : 273, big, l.Offsets));
            n.Set(TiffField.Longs(l.Tiled ? 325 : 279, big, l.Counts));
            if (l.Pred != 1) n.Set(TiffField.Shorts(317, l.Pred));
            if (extra.Length > 0) n.Set(TiffField.Shorts(338, extra));
            if (!l.Bilevel) n.Set(TiffField.Shorts(339, Repeat(format, spp)));
            if (photometric == 6)
            {
                n.Set(TiffField.Shorts(530, l.SubH, l.SubV));
                n.Set(TiffField.FromTag(TiffTag.Rationals(532, 0, 255, 128, 255, 128, 255), big));
            }

            // EXIF: descriptive IFD0 tags and the Exif / GPS directories travel along
            TiffIfdNode? exif = meta?.Exif is { Length: > 8 } blob ? TiffExif.Parse(blob) : null;
            if (exif != null)
                foreach (var f in exif.Fields)
                    if (f.Child != null || TiffExif.IsDescriptive(f.Tag)) n.Set(f);

            if (meta != null && meta.Orientation >= 1 && meta.Orientation <= 8) n.Set(TiffField.Shorts(274, meta.Orientation));
            else if (exif?.Find(274) is { } eo) n.Set(eo);
            if (meta != null && meta.DpiX > 0 && meta.DpiY > 0 && double.IsFinite(meta.DpiX) && double.IsFinite(meta.DpiY))
            {
                n.Set(TiffField.Rational(282, meta.DpiX));
                n.Set(TiffField.Rational(283, meta.DpiY));
                n.Set(TiffField.Shorts(296, 2));
            }
            else if (exif?.Find(282) is { } ex && exif.Find(283) is { } ey)
            {
                n.Set(ex); n.Set(ey);
                if (exif.Find(296) is { } eu) n.Set(eu);
            }
            if (meta?.IccProfile is { Length: > 0 } icc) n.Set(new TiffField { Tag = 34675, Type = 7, Count = icc.Length, Data = icc });

            foreach (var t in o.ExtraTags)
            {
                if (t == null) continue;
                if (IsStructural(t.Id)) throw new ArgumentException($"TIFF: tag {t.Id} is written by the encoder and cannot be set through ExtraTags");
                n.Set(TiffField.FromTag(t, big));
            }
            return n;
        }

        sealed class Context
        {
            public Layout L = null!;
            public byte* Pixels;
            public bool BigEndian;

            /// <summary>Gathers, predicts and compresses one strip or tile.</summary>
            public Chunk Compress(int index)
            {
                var l = L;
                int x0 = index % l.Across * l.Cw, y0 = index / l.Across * l.Ch;
                int rows = l.Tiled ? l.Ch : Math.Min(l.Ch, l.H - y0);
                int copyCols = Math.Min(l.Cw, l.W - x0), copyRows = Math.Min(rows, l.H - y0);
                if (l.Comp == JpegComp) return CompressJpeg(x0, y0, rows, copyCols, copyRows);

                int pix = l.Spp * l.Bps;
                int rowBytes = (int)l.ChunkRowBytes;
                int size = rowBytes * rows;
                var raw = ArrayPool<byte>.Shared.Rent(Math.Max(size, 1));
                bool keepRaw = false;
                try
                {
                    fixed (byte* r = raw)
                    {
                        if (l.Bilevel)
                        {
                            // 1 bit per pixel, MSB first. CCITT: 1 = black (MinIsWhite); otherwise 1 = white (MinIsBlack).
                            new Span<byte>(r, size).Clear();
                            bool blackIsOne = l.Comp == Ccitt4;
                            for (int y = 0; y < copyRows; y++)
                            {
                                byte* s = Pixels + (long)(y0 + y) * l.W + x0;
                                byte* d = r + (long)y * rowBytes;
                                for (int x = 0; x < copyCols; x++)
                                    if ((s[x] >= 128) != blackIsOne) d[x >> 3] |= (byte)(0x80 >> (x & 7));
                            }
                            if (!blackIsOne && (copyCols < l.Cw || copyRows < rows))
                            {
                                // tile padding is white
                                for (int y = 0; y < rows; y++)
                                    for (int x = y < copyRows ? copyCols : 0; x < l.Cw; x++) r[(long)y * rowBytes + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                            }
                        }
                        else
                        {
                            long srcRow = (long)l.W * pix;
                            if (copyCols < l.Cw || copyRows < rows) new Span<byte>(r, size).Clear();
                            for (int y = 0; y < copyRows; y++)
                                Buffer.MemoryCopy(Pixels + (y0 + y) * srcRow + (long)x0 * pix, r + (long)y * rowBytes, copyCols * pix, copyCols * pix);

                            if (l.Pred == 2)
                            {
                                for (int y = 0; y < rows; y++) TiffPredict.ApplyHorizontal(r + (long)y * rowBytes, l.Cw * l.Spp, l.Spp, l.Bps);
                            }
                            else if (l.Pred == 3)
                            {
                                var tmp = ArrayPool<byte>.Shared.Rent(rowBytes);
                                fixed (byte* t = tmp)
                                    for (int y = 0; y < rows; y++) TiffPredict.ApplyFloat(r + (long)y * rowBytes, l.Cw * l.Spp, l.Spp, l.Bps, t);
                                ArrayPool<byte>.Shared.Return(tmp);
                            }
                            // predictor 3 output is byte planes, the same for both byte orders
                            if (BigEndian && l.Bps > 1 && l.Pred != 3) TiffPredict.SwapBytes(r, size, l.Bps);
                        }

                        if (l.Comp == Ccitt4)
                        {
                            var g4 = TiffCcitt.EncodeG4(r, l.Cw, rows, rowBytes, out int len);
                            return new Chunk { Buf = g4, Len = len };
                        }
                    }
                    var src = raw.AsSpan(0, size);
                    switch (l.Comp)
                    {
                        case Lzw:
                        {
                            var dst = ArrayPool<byte>.Shared.Rent(TiffLzw.MaxEncodedSize(size));
                            return new Chunk { Buf = dst, Len = TiffLzw.Encode(src, dst), Pooled = true };
                        }
                        case PackBits:
                        {
                            var dst = ArrayPool<byte>.Shared.Rent(TiffPackBits.MaxEncodedSize(size, rowBytes));
                            return new Chunk { Buf = dst, Len = TiffPackBits.Encode(src, rowBytes, dst), Pooled = true };
                        }
                        case Deflate:
                        {
                            var z = Zlib.Compress(src, l.Options.CompressionLevel);
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

            /// <summary>One strip/tile as a complete JPEG stream. Tiles are padded by repeating the edge pixels.</summary>
            Chunk CompressJpeg(int x0, int y0, int rows, int copyCols, int copyRows)
            {
                var l = L;
                int spp = l.Spp;
                var px = new byte[(long)l.Cw * rows * spp];
                fixed (byte* d0 = px)
                {
                    for (int y = 0; y < rows; y++)
                    {
                        byte* d = d0 + (long)y * l.Cw * spp;
                        byte* s = Pixels + ((long)(y0 + Math.Min(y, copyRows - 1)) * l.W + x0) * spp;
                        Buffer.MemoryCopy(s, d, (long)copyCols * spp, (long)copyCols * spp);
                        for (int x = copyCols; x < l.Cw; x++)
                            for (int c = 0; c < spp; c++) d[x * spp + c] = d[(copyCols - 1) * spp + c];
                    }
                }
                var ms = new MemoryStream();
                TiffJpegBridge.Encode(px, l.Cw, rows, spp, l.Jpeg!, ms);
                return new Chunk { Buf = ms.GetBuffer(), Len = (int)ms.Length };
            }
        }
    }
}
