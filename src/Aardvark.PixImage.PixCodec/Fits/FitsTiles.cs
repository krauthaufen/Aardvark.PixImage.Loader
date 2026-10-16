using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Aardvark.ImageCodecs.Compression;

namespace Aardvark.ImageCodecs
{
    /// <summary>
    /// The FITS tiled image compression convention (White, Greenfield, Pence, Tody, Seaman, version 2.3): compressed images
    /// stored as binary tables, one tile per row.
    /// </summary>
    internal static class FitsTiles
    {
        const string Fmt = "FITS";

        sealed class Column
        {
            public string Name = "";
            public int Offset;          // within the row
            public char Type;           // B I J K E D A L X P Q ...
            public char ElemType;       // for P / Q: the element type
            public int Repeat;
        }

        static int TypeSize(char t) => t switch
        {
            'L' or 'B' or 'A' => 1,
            'I' => 2,
            'J' or 'E' => 4,
            'K' or 'D' or 'C' => 8,
            'M' => 16,
            'P' => 8,
            'Q' => 16,
            'X' => 1,
            _ => throw new ImageFormatException($"FITS: unknown column type '{t}'"),
        };

        static Column ParseForm(string name, string form)
        {
            var f = form.Trim();
            int i = 0, rep = 0;
            bool hasRep = false;
            while (i < f.Length && char.IsDigit(f[i])) { rep = rep * 10 + (f[i] - '0'); i++; hasRep = true; }
            if (i >= f.Length) throw new ImageFormatException($"FITS: bad TFORM '{form}'");
            var c = new Column { Name = name, Type = char.ToUpperInvariant(f[i]), Repeat = hasRep ? rep : 1 };
            if (c.Type is 'P' or 'Q')
            {
                if (i + 1 >= f.Length) throw new ImageFormatException($"FITS: bad TFORM '{form}'");
                c.ElemType = char.ToUpperInvariant(f[i + 1]);
            }
            return c;
        }

        static int Width(Column c) => c.Type == 'X' ? (c.Repeat + 7) / 8 : c.Repeat * TypeSize(c.Type);

        // ================================================================ random numbers (Appendix A)

        static readonly float[] Rand = MakeRandom();

        static float[] MakeRandom()
        {
            var r = new float[10000];
            double a = 16807.0, m = 2147483647.0, seed = 1;
            for (int i = 0; i < r.Length; i++)
            {
                double temp = a * seed;
                seed = temp - m * (int)(temp / m);
                r[i] = (float)(seed / m);
            }
            return r;
        }

        internal static double RandomSeedCheck()
        {
            double a = 16807.0, m = 2147483647.0, seed = 1;
            for (int i = 0; i < 10000; i++) { double temp = a * seed; seed = temp - m * (int)(temp / m); }
            return seed; // 1043618065
        }

        // ================================================================ decode

        public static void Decompress(Stream s, FitsHdu h, Array arr, out bool quantized)
        {
            long rowLen = h.GetLong("NAXIS1", 0), rows = h.GetLong("NAXIS2", 0), pcount = h.GetLong("PCOUNT", 0);
            if (rowLen < 0 || rows < 0 || rowLen * rows > int.MaxValue || pcount < 0 || rowLen * rows + pcount > int.MaxValue)
                throw new ImageNotSupportedException("FITS: compressed table above 2 GB");
            long size = rowLen * rows + pcount;
            if (s.CanSeek && h.DataOffset + size > s.Length) throw new ImageFormatException("FITS: data ends early");
            s.Position = h.DataOffset;
            var data = new byte[size];
            s.ReadExactly(data);
            long theap = h.GetLong("THEAP", rowLen * rows);
            if (theap < 0 || theap > size) throw new ImageFormatException("FITS: bad THEAP");

            int nf = (int)h.GetLong("TFIELDS", 0);
            var cols = new Dictionary<string, Column>(StringComparer.OrdinalIgnoreCase);
            int off = 0;
            for (int i = 1; i <= nf; i++)
            {
                var c = ParseForm((h.Get("TTYPE" + i) ?? "").Trim(), h.Get("TFORM" + i) ?? throw new ImageFormatException("FITS: missing TFORM"));
                c.Offset = off;
                off += Width(c);
                if (c.Name.Length > 0) cols[c.Name] = c;
            }
            if (off > rowLen) throw new ImageFormatException("FITS: columns exceed the row length");

            string cmp = (h.Get("ZCMPTYPE") ?? "").Trim().ToUpperInvariant();
            int zbitpix = h.BitPix;
            var axes = h.Axes;
            int nd = axes.Length;
            var tile = new int[nd];
            for (int i = 0; i < nd; i++)
            {
                long t = h.GetLong("ZTILE" + (i + 1), i == 0 ? axes[0] : 1);
                if (t <= 0) throw new ImageFormatException("FITS: bad ZTILE");
                tile[i] = (int)Math.Min(t, axes[i]);
            }
            // parameters
            int blocksize = 32, bytepix = 4;
            for (int i = 1; i < 100; i++)
            {
                var name = h.Get("ZNAME" + i);
                if (name == null) break;
                long val = h.Get("ZVAL" + i) is { } v && long.TryParse(v.Trim(), out var l) ? l : 0;
                switch (name.Trim().ToUpperInvariant())
                {
                    case "BLOCKSIZE": blocksize = (int)val; break;
                    case "BYTEPIX": bytepix = (int)val; break;
                }
            }
            if (blocksize <= 0 || blocksize > 1 << 16) throw new ImageFormatException("FITS: bad Rice block size");
            cols.TryGetValue("COMPRESSED_DATA", out var cdCol);
            cols.TryGetValue("GZIP_COMPRESSED_DATA", out var gzCol);
            cols.TryGetValue("UNCOMPRESSED_DATA", out var ucCol);
            cols.TryGetValue("ZSCALE", out var scCol);
            cols.TryGetValue("ZZERO", out var zzCol);
            cols.TryGetValue("ZBLANK", out var zbCol);
            if (cdCol == null && gzCol == null && ucCol == null) throw new ImageFormatException("FITS: no COMPRESSED_DATA column");
            bool isFloat = zbitpix < 0;
            quantized = isFloat && (scCol != null || h.Get("ZSCALE") != null);
            string zq = (h.Get("ZQUANTIZ") ?? "NO_DITHER").Trim().ToUpperInvariant();
            int dither = zq switch { "SUBTRACTIVE_DITHER_1" => 1, "SUBTRACTIVE_DITHER_2" => 2, "NO_DITHER" or "NONE" => 0, _ => throw new ImageNotSupportedException($"FITS: quantization '{zq}' is not supported") };
            if (zq == "NONE") quantized = false;
            long zdither0 = h.GetLong("ZDITHER0", 1);
            double kScale = h.GetDouble("ZSCALE", 1), kZero = h.GetDouble("ZZERO", 0);
            long? kBlank = h.Get("ZBLANK") != null ? h.GetLong("ZBLANK", 0) : null;
            if (cmp is not ("RICE_1" or "RICE_ONE" or "GZIP_1" or "GZIP_2" or "PLIO_1" or "NOCOMPRESS" or "HCOMPRESS_1"))
                throw new ImageNotSupportedException($"FITS: tile compression '{cmp}' is not supported");
            if (cmp == "HCOMPRESS_1") throw new ImageNotSupportedException("FITS: HCOMPRESS_1 is not supported");

            // tile grid
            var ntiles = new int[nd];
            long totalTiles = 1;
            for (int i = 0; i < nd; i++) { ntiles[i] = (axes[i] + tile[i] - 1) / tile[i]; totalTiles *= ntiles[i]; }
            if (totalTiles > rows) throw new ImageFormatException("FITS: fewer table rows than tiles");
            long maxTile = 1;
            foreach (var t in tile) maxTile *= t;
            if (maxTile > int.MaxValue / 8) throw new ImageNotSupportedException("FITS: tile too large");
            var ints = new long[maxTile];
            var floats = isFloat ? new double[maxTile] : null;
            var tcoord = new int[nd];
            var tsize = new int[nd];
            var outType = RawImage.SampleTypeOf(arr.GetType().GetElementType()!);

            for (long r = 0; r < totalTiles; r++)
            {
                // tile position and size
                long rem = r;
                long npix = 1;
                for (int i = 0; i < nd; i++)
                {
                    tcoord[i] = (int)(rem % ntiles[i]) * tile[i];
                    rem /= ntiles[i];
                    tsize[i] = Math.Min(tile[i], axes[i] - tcoord[i]);
                    npix *= tsize[i];
                }
                int n = (int)npix;
                var row = data.AsSpan((int)(r * rowLen), (int)rowLen);
                var cd = cdCol != null ? Heap(data, row, cdCol, theap) : default;
                bool asFloat = false; // floats[] holds the result
                if (cdCol != null && cd.Length > 0)
                {
                    int elem = quantized ? 4 : isFloat ? -zbitpix / 8 : zbitpix / 8;
                    switch (cmp)
                    {
                        case "RICE_1": case "RICE_ONE":
                            Rice.Decode(cd, ints.AsSpan(0, n), quantized || zbitpix == 32 && bytepix == 4 ? bytepix : bytepix, blocksize);
                            if (isFloat && !quantized) throw new ImageFormatException("FITS: Rice compressed float data without ZSCALE");
                            break;
                        case "GZIP_1": case "GZIP_2":
                            {
                                var raw = Gzip.Decompress(cd, n * elem);
                                if (raw.Length < (long)n * elem) throw new ImageFormatException("FITS: compressed tile too short");
                                if (cmp == "GZIP_2") raw = Unshuffle(raw, n, elem);
                                if (isFloat && !quantized) { Floats(raw, floats!, n, elem); asFloat = true; }
                                else Ints(raw, ints, n, elem, zbitpix == 8 && !quantized);
                                break;
                            }
                        case "NOCOMPRESS":
                            if (cd.Length < (long)n * elem) throw new ImageFormatException("FITS: tile too short");
                            if (isFloat && !quantized) { Floats(cd.ToArray(), floats!, n, elem); asFloat = true; }
                            else Ints(cd.ToArray(), ints, n, elem, zbitpix == 8 && !quantized);
                            break;
                        case "PLIO_1":
                            Plio.Decode(cd, cdCol.ElemType, ints.AsSpan(0, n));
                            break;
                    }
                }
                else if (gzCol != null && Heap(data, row, gzCol, theap) is var gz && gz.Length > 0)
                {
                    // losslessly gzipped original pixels
                    int elem = Math.Abs(zbitpix) / 8;
                    var raw = Gzip.Decompress(gz, n * elem);
                    if (raw.Length < (long)n * elem) throw new ImageFormatException("FITS: compressed tile too short");
                    if (isFloat) { Floats(raw, floats!, n, elem); asFloat = true; }
                    else Ints(raw, ints, n, elem, zbitpix == 8);
                }
                else if (ucCol != null && Heap(data, row, ucCol, theap) is var uc && uc.Length > 0)
                {
                    int elem = TypeSize(ucCol.ElemType);
                    if (uc.Length < (long)n * elem) throw new ImageFormatException("FITS: tile too short");
                    if (ucCol.ElemType is 'E' or 'D') { Floats(uc.ToArray(), floats ??= new double[maxTile], n, elem); asFloat = true; }
                    else Ints(uc.ToArray(), ints, n, elem, ucCol.ElemType == 'B');
                }
                else throw new ImageFormatException("FITS: empty tile");

                if (isFloat && !asFloat)
                {
                    if (!quantized) throw new ImageFormatException("FITS: integer tile data for a float image without ZSCALE");
                    double sc = scCol != null ? Number(row, scCol) : kScale;
                    double zz = zzCol != null ? Number(row, zzCol) : kZero;
                    long? blank = zbCol != null ? (long)Number(row, zbCol) : kBlank;
                    Dequantize(ints, floats!, n, sc, zz, blank, dither, r, zdither0);
                }
                Place(arr, outType, axes, tcoord, tsize, ints, asFloat || isFloat ? floats : null);
            }
        }

        static ReadOnlySpan<byte> Heap(byte[] data, ReadOnlySpan<byte> row, Column c, long theap)
        {
            if (c.Type is not ('P' or 'Q')) throw new ImageFormatException($"FITS: column {c.Name} is not a variable length array");
            long count, offset;
            if (c.Type == 'P') { count = BinaryPrimitives.ReadInt32BigEndian(row.Slice(c.Offset)); offset = BinaryPrimitives.ReadInt32BigEndian(row.Slice(c.Offset + 4)); }
            else { count = BinaryPrimitives.ReadInt64BigEndian(row.Slice(c.Offset)); offset = BinaryPrimitives.ReadInt64BigEndian(row.Slice(c.Offset + 8)); }
            long bytes = count * TypeSize(c.ElemType);
            if (count < 0 || offset < 0 || theap + offset + bytes > data.Length) throw new ImageFormatException("FITS: heap pointer out of range");
            return data.AsSpan((int)(theap + offset), (int)bytes);
        }

        static double Number(ReadOnlySpan<byte> row, Column c) => c.Type switch
        {
            'D' => BinaryPrimitives.ReadDoubleBigEndian(row.Slice(c.Offset)),
            'E' => BinaryPrimitives.ReadSingleBigEndian(row.Slice(c.Offset)),
            'J' => BinaryPrimitives.ReadInt32BigEndian(row.Slice(c.Offset)),
            'K' => BinaryPrimitives.ReadInt64BigEndian(row.Slice(c.Offset)),
            'I' => BinaryPrimitives.ReadInt16BigEndian(row.Slice(c.Offset)),
            'B' => row[c.Offset],
            _ => throw new ImageFormatException($"FITS: column {c.Name} has an unexpected type"),
        };

        static byte[] Unshuffle(byte[] raw, int n, int elem)
        {
            var r = new byte[raw.Length];
            for (int b = 0; b < elem; b++)
                for (int i = 0; i < n; i++) r[i * elem + b] = raw[b * n + i];
            return r;
        }

        static void Ints(byte[] raw, long[] dst, int n, int elem, bool unsigned8)
        {
            for (int i = 0; i < n; i++)
                dst[i] = elem switch
                {
                    1 => unsigned8 ? raw[i] : (sbyte)raw[i],
                    2 => BinaryPrimitives.ReadInt16BigEndian(raw.AsSpan(i * 2)),
                    4 => BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(i * 4)),
                    _ => BinaryPrimitives.ReadInt64BigEndian(raw.AsSpan(i * 8)),
                };
        }

        static void Floats(byte[] raw, double[] dst, int n, int elem)
        {
            for (int i = 0; i < n; i++)
                dst[i] = elem == 4 ? BinaryPrimitives.ReadSingleBigEndian(raw.AsSpan(i * 4)) : BinaryPrimitives.ReadDoubleBigEndian(raw.AsSpan(i * 8));
        }

        /// <summary>Inverse of the quantization (Eq. 1 / Eq. 3 with the dither sequence of section 4.1.2).</summary>
        static void Dequantize(long[] q, double[] dst, int n, double scale, double zero, long? blank, int dither, long tileRow, long zdither0)
        {
            if (dither == 0)
            {
                for (int i = 0; i < n; i++) dst[i] = blank.HasValue && q[i] == blank.Value ? double.NaN : q[i] * scale + zero;
                return;
            }
            int i0 = (int)((tileRow + zdither0 - 1) % 10000);
            if (i0 < 0) i0 += 10000;
            int i1 = (int)(Rand[i0] * 500);
            for (int i = 0; i < n; i++)
            {
                long v = q[i];
                if (blank.HasValue && v == blank.Value) dst[i] = double.NaN;
                else if (dither == 2 && v == -2147483646) dst[i] = 0;
                else dst[i] = (v - (double)Rand[i1] + 0.5) * scale + zero;
                if (++i1 == 10000)
                {
                    if (++i0 == 10000) i0 = 0;
                    i1 = (int)(Rand[i0] * 500);
                }
            }
        }

        /// <summary>Copies a decoded tile into the image (axis 1 fastest).</summary>
        static void Place(Array arr, SampleType t, int[] axes, int[] tcoord, int[] tsize, long[] ints, double[]? floats)
        {
            int nd = axes.Length;
            int w = tsize[0];
            var idx = new int[nd];
            int src = 0;
            long rowsInTile = 1;
            for (int i = 1; i < nd; i++) rowsInTile *= tsize[i];
            for (long rr = 0; rr < rowsInTile; rr++)
            {
                long rem = rr;
                long dst = tcoord[0], stride = axes[0];
                for (int i = 1; i < nd; i++)
                {
                    long c = rem % tsize[i] + tcoord[i];
                    rem /= tsize[i];
                    dst += c * stride;
                    stride *= axes[i];
                }
                switch (t)
                {
                    case SampleType.UInt8: { var a = (byte[])arr; for (int x = 0; x < w; x++) a[dst + x] = (byte)ints[src + x]; break; }
                    case SampleType.Int16: { var a = (short[])arr; for (int x = 0; x < w; x++) a[dst + x] = (short)ints[src + x]; break; }
                    case SampleType.Int32: { var a = (int[])arr; for (int x = 0; x < w; x++) a[dst + x] = (int)ints[src + x]; break; }
                    case SampleType.Int64: { var a = (long[])arr; for (int x = 0; x < w; x++) a[dst + x] = ints[src + x]; break; }
                    case SampleType.Float32: { var a = (float[])arr; for (int x = 0; x < w; x++) a[dst + x] = (float)floats![src + x]; break; }
                    case SampleType.Float64: { var a = (double[])arr; for (int x = 0; x < w; x++) a[dst + x] = floats![src + x]; break; }
                }
                src += w;
            }
        }

        // ================================================================ encode

        public static void WriteCompressed(Stream output, Array data, SampleType st, int bitpix, int[] axes, FitsEncodeOptions o, Action<List<string>> extra)
        {
            int nd = axes.Length;
            var tile = new int[nd];
            tile[0] = o.TileWidth > 0 ? Math.Min(o.TileWidth, axes[0]) : axes[0];
            for (int i = 1; i < nd; i++) tile[i] = 1;
            if (nd > 1 && o.TileHeight > 0) tile[1] = Math.Min(o.TileHeight, axes[1]);
            string cmp = o.Compression switch { FitsCompression.Rice => "RICE_1", FitsCompression.Gzip => "GZIP_1", FitsCompression.Gzip2 => "GZIP_2", _ => throw new ArgumentException() };
            int elem = RawImage.BytesPerSample(st);
            if (o.Compression == FitsCompression.Rice && (bitpix < 0 || bitpix == 64))
                throw new ArgumentException("FITS: Rice compression needs 8, 16 or 32 bit integer samples (use Gzip / Gzip2 for float and 64 bit data)");

            var ntiles = new int[nd];
            long total = 1;
            for (int i = 0; i < nd; i++) { ntiles[i] = (axes[i] + tile[i] - 1) / tile[i]; total *= ntiles[i]; }
            if (total > int.MaxValue) throw new ArgumentException("FITS: too many tiles");
            var heap = new MemoryStream();
            var desc = new byte[total * 8];
            long maxTile = 1;
            foreach (var t in tile) maxTile *= t;
            var vals = new long[maxTile];
            var tcoord = new int[nd]; var tsize = new int[nd];
            int maxLen = 0;
            var buf = new byte[maxTile * elem];
            for (long r = 0; r < total; r++)
            {
                long rem = r, npix = 1;
                for (int i = 0; i < nd; i++)
                {
                    tcoord[i] = (int)(rem % ntiles[i]) * tile[i];
                    rem /= ntiles[i];
                    tsize[i] = Math.Min(tile[i], axes[i] - tcoord[i]);
                    npix *= tsize[i];
                }
                int n = (int)npix;
                Gather(data, st, axes, tcoord, tsize, buf, elem);
                byte[] comp;
                if (o.Compression == FitsCompression.Rice)
                {
                    for (int i = 0; i < n; i++)
                        vals[i] = elem switch
                        {
                            1 => buf[i],
                            2 => BinaryPrimitives.ReadInt16BigEndian(buf.AsSpan(i * 2)),
                            _ => BinaryPrimitives.ReadInt32BigEndian(buf.AsSpan(i * 4)),
                        };
                    comp = Rice.Encode(vals.AsSpan(0, n), elem, 32);
                }
                else
                {
                    var raw = buf.AsSpan(0, n * elem).ToArray();
                    if (o.Compression == FitsCompression.Gzip2)
                    {
                        var sh = new byte[raw.Length];
                        for (int b = 0; b < elem; b++)
                            for (int i = 0; i < n; i++) sh[b * n + i] = raw[i * elem + b];
                        raw = sh;
                    }
                    comp = Gzip.Compress(raw, 6);
                }
                if (heap.Length + comp.Length > int.MaxValue) throw new ArgumentException("FITS: compressed image above 2 GB");
                BinaryPrimitives.WriteInt32BigEndian(desc.AsSpan((int)(r * 8)), comp.Length);
                BinaryPrimitives.WriteInt32BigEndian(desc.AsSpan((int)(r * 8 + 4)), (int)heap.Length);
                heap.Write(comp);
                maxLen = Math.Max(maxLen, comp.Length);
            }
            var cards = new List<string>
            {
                Fits.Str("XTENSION", "BINTABLE", "binary table extension"),
                Fits.Int("BITPIX", 8, null),
                Fits.Int("NAXIS", 2, null),
                Fits.Int("NAXIS1", 8, null),
                Fits.Int("NAXIS2", total, null),
                Fits.Int("PCOUNT", heap.Length, null),
                Fits.Int("GCOUNT", 1, null),
                Fits.Int("TFIELDS", 1, null),
                Fits.Str("TTYPE1", "COMPRESSED_DATA", null),
                Fits.Str("TFORM1", $"1PB({maxLen})", null),
                Fits.Logical("ZIMAGE", true, "extension contains compressed image"),
                Fits.Int("ZBITPIX", bitpix, null),
                Fits.Int("ZNAXIS", nd, null),
            };
            for (int i = 0; i < nd; i++) cards.Add(Fits.Int("ZNAXIS" + (i + 1), axes[i], null));
            for (int i = 0; i < nd; i++) cards.Add(Fits.Int("ZTILE" + (i + 1), tile[i], null));
            cards.Add(Fits.Str("ZCMPTYPE", cmp, null));
            if (o.Compression == FitsCompression.Rice)
            {
                cards.Add(Fits.Str("ZNAME1", "BLOCKSIZE", null));
                cards.Add(Fits.Int("ZVAL1", 32, null));
                cards.Add(Fits.Str("ZNAME2", "BYTEPIX", null));
                cards.Add(Fits.Int("ZVAL2", elem, null));
            }
            extra(cards);
            Fits.WriteHeader(output, cards);
            output.Write(desc);
            heap.Position = 0;
            heap.CopyTo(output);
            Fits.Pad(output, desc.Length + heap.Length, 0);
        }

        /// <summary>Big-endian bytes of one tile.</summary>
        static void Gather(Array data, SampleType t, int[] axes, int[] tcoord, int[] tsize, byte[] buf, int elem)
        {
            int nd = axes.Length, w = tsize[0];
            long rows = 1;
            for (int i = 1; i < nd; i++) rows *= tsize[i];
            int o = 0;
            for (long rr = 0; rr < rows; rr++)
            {
                long rem = rr, src = tcoord[0], stride = axes[0];
                for (int i = 1; i < nd; i++)
                {
                    long c = rem % tsize[i] + tcoord[i];
                    rem /= tsize[i];
                    src += c * stride;
                    stride *= axes[i];
                }
                var bytes = VolumeIO.ByteSpan(data, t, src, w);
                bytes.CopyTo(buf.AsSpan(o));
                if (elem > 1) VolumeIO.Swap(buf.AsSpan(o, bytes.Length), elem);
                o += bytes.Length;
            }
        }
    }

    /// <summary>The Rice coding of the FITS tiled image convention (block wise adaptive Golomb-Rice codes of pixel differences).</summary>
    internal static class Rice
    {
        static (int fsbits, int fsmax, int bbits) Params(int bytepix) => bytepix switch
        {
            1 => (3, 6, 8),
            2 => (4, 14, 16),
            4 => (5, 25, 32),
            _ => throw new ImageNotSupportedException($"FITS: Rice with BYTEPIX {bytepix} is not supported"),
        };

        ref struct Bits
        {
            readonly ReadOnlySpan<byte> d;
            int pos;
            ulong buf;
            int n;
            public Bits(ReadOnlySpan<byte> d, int pos) { this.d = d; this.pos = pos; buf = 0; n = 0; }

            void Fill()
            {
                while (n <= 56)
                {
                    if (pos < d.Length) buf |= (ulong)d[pos++] << (56 - n);
                    else if (pos++ > d.Length + 16) throw new ImageFormatException("FITS: Rice data ends early");
                    n += 8;
                }
            }

            public uint Read(int k)
            {
                if (k == 0) return 0;
                if (n < k) Fill();
                uint v = (uint)(buf >> (64 - k));
                buf <<= k; n -= k;
                return v;
            }

            /// <summary>Counts zero bits up to and including the next one bit; returns the zeros.</summary>
            public int Zeros()
            {
                int z = 0;
                while (true)
                {
                    if (n == 0) Fill();
                    if (buf == 0) { z += n; n = 0; buf = 0; if (z > 1 << 26) throw new ImageFormatException("FITS: bad Rice data"); continue; }
                    int lz = BitOperations.LeadingZeroCount(buf);
                    if (lz < n)
                    {
                        z += lz;
                        buf <<= lz + 1; n -= lz + 1;
                        return z;
                    }
                    z += n; n = 0; buf = 0;
                }
            }
        }

        public static void Decode(ReadOnlySpan<byte> c, Span<long> dst, int bytepix, int blocksize)
        {
            var (fsbits, fsmax, bbits) = Params(bytepix);
            if (c.Length < bytepix) throw new ImageFormatException("FITS: Rice data too short");
            long last = bytepix switch
            {
                1 => c[0],
                2 => BinaryPrimitives.ReadInt16BigEndian(c),
                _ => BinaryPrimitives.ReadInt32BigEndian(c),
            };
            var b = new Bits(c, bytepix);
            int nx = dst.Length;
            for (int i = 0; i < nx; i += blocksize)
            {
                int imax = Math.Min(i + blocksize, nx);
                int fs = (int)b.Read(fsbits) - 1;
                if (fs < 0)
                {
                    for (int j = i; j < imax; j++) dst[j] = last;
                }
                else if (fs == fsmax)
                {
                    for (int j = i; j < imax; j++)
                    {
                        uint diff = b.Read(bbits);
                        last = Wrap(last + Unmap(diff), bytepix);
                        dst[j] = last;
                    }
                }
                else if (fs > fsmax) throw new ImageFormatException("FITS: bad Rice block code");
                else
                {
                    for (int j = i; j < imax; j++)
                    {
                        int z = b.Zeros();
                        uint diff = (uint)z << fs | b.Read(fs);
                        last = Wrap(last + Unmap(diff), bytepix);
                        dst[j] = last;
                    }
                }
            }
        }

        static long Unmap(uint d) => (d & 1) == 0 ? (long)(d >> 1) : ~(long)(d >> 1);

        static long SignedWrap(long v, int bytepix) => bytepix switch
        {
            1 => (sbyte)v,
            2 => (short)v,
            _ => (int)v,
        };

        static long Wrap(long v, int bytepix) => bytepix switch
        {
            1 => (byte)v,
            2 => (short)v,
            _ => (int)v,
        };

        sealed class Writer
        {
            byte[] buf = new byte[256];
            int pos;
            ulong acc;
            int n;
            public void Put(uint v, int k)
            {
                if (k == 0) return;
                if (k > 32) { Put(v >> 16, k - 16); Put(v & 0xFFFF, 16); return; }
                acc = acc << k | (v & (uint)((1UL << k) - 1));
                n += k;
                while (n >= 8) { n -= 8; Byte((byte)(acc >> n)); }
            }
            public void Zeros(int z) { while (z >= 32) { Put(0, 32); z -= 32; } Put(1, z + 1); }
            public void Byte(byte b) { if (pos == buf.Length) Array.Resize(ref buf, buf.Length * 2); buf[pos++] = b; }
            public byte[] Finish() { if (n > 0) { Byte((byte)(acc << (8 - n))); n = 0; } return buf.AsSpan(0, pos).ToArray(); }
        }

        public static byte[] Encode(ReadOnlySpan<long> px, int bytepix, int blocksize)
        {
            var (fsbits, fsmax, bbits) = Params(bytepix);
            var w = new Writer();
            long last = px.Length > 0 ? px[0] : 0;
            for (int k = bytepix - 1; k >= 0; k--) w.Byte((byte)(last >> (8 * k)));
            Span<uint> diff = stackalloc uint[blocksize];
            long lastpix = last;
            for (int i = 0; i < px.Length; i += blocksize)
            {
                int n = Math.Min(blocksize, px.Length - i);
                double psum = 0;
                for (int j = 0; j < n; j++)
                {
                    long d = SignedWrap(px[i + j] - lastpix, bytepix);
                    lastpix = px[i + j];
                    uint m = d < 0 ? (uint)~(d << 1) : (uint)(d << 1);
                    if (bytepix == 1) m &= 0xFF; else if (bytepix == 2) m &= 0xFFFF;
                    diff[j] = m;
                    psum += m;
                }
                double dpsum = (psum - (n / 2) - 1) / n;
                if (dpsum < 0) dpsum = 0;
                ulong ps = (ulong)dpsum >> 1;
                int fs = 0;
                while (ps > 0) { ps >>= 1; fs++; }
                if (fs >= fsmax)
                {
                    w.Put((uint)(fsmax + 1), fsbits);
                    for (int j = 0; j < n; j++) w.Put(diff[j], bbits);
                }
                else if (fs == 0 && psum == 0)
                {
                    w.Put(0, fsbits);
                }
                else
                {
                    w.Put((uint)(fs + 1), fsbits);
                    uint mask = (uint)((1UL << fs) - 1);
                    for (int j = 0; j < n; j++)
                    {
                        uint top = diff[j] >> fs;
                        w.Zeros((int)top);
                        w.Put(diff[j] & mask, fs);
                    }
                }
            }
            return w.Finish();
        }
    }
}
