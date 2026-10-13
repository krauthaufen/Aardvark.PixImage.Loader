using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using PixCodec.Compression;

namespace PixCodec
{
    /// <summary>
    /// Decodes one strip or tile of a JPEG-compressed TIFF (compression 7).
    /// <paramref name="jpegTables"/> is the content of the JPEGTables tag (an abbreviated "tables only" JPEG stream, empty
    /// when the tag is absent), <paramref name="data"/> the strip/tile (an abbreviated or complete JPEG stream).
    /// The decoder must write the top-left <paramref name="width"/> x <paramref name="height"/> pixels as 8-bit samples,
    /// interleaved, upsampled to full resolution, <paramref name="components"/> samples per pixel, into
    /// <paramref name="destination"/> (width * height * components bytes). No colour transform may be guessed from the
    /// stream: convert YCbCr to RGB exactly when <paramref name="convertYCbCrToRgb"/> is set, otherwise return the
    /// components as stored. Must be thread safe (strips/tiles are decoded in parallel) and throw
    /// ImageFormatException on corrupt data.
    /// </summary>
    internal delegate void TiffJpegDecoder(ReadOnlySpan<byte> jpegTables, ReadOnlySpan<byte> data, int width, int height,
        int components, bool convertYCbCrToRgb, Span<byte> destination);

    /// <summary>Thrown for bad caller arguments so the "corrupt file" safety net does not relabel them.</summary>
    internal sealed class TiffArgumentException : ArgumentOutOfRangeException
    {
        public TiffArgumentException(string name, string message) : base(name, message) { }
    }

    /// <summary>One IFD: parsed layout, and the strip/tile decoder for it.</summary>
    internal sealed unsafe class TiffImage
    {
        public readonly TiffFile File;
        public readonly TiffEntry[] Entries;
        public readonly int Width, Height, Spp, Bits, Format, Compression, Photometric, Planar, Predictor, FillOrder;
        public readonly bool Tiled;
        public readonly int ChunkW, ChunkH, Across, Down, ChunksPerPlane;
        public readonly int[] Extra;
        public readonly int SubH = 1, SubV = 1;

        // raw = samples as stored (after unpacking), out = what the caller gets
        public readonly SampleType RawType, OutType;
        public readonly int Bps, ColorSamples, RawChannels, OutChannels;
        public readonly ColorModel OutColor;
        public readonly bool HasAlpha, AssociatedAlpha, YccPacked, IsJpeg, IsCcitt;
        readonly int _scale;

        long[] _offsets = Array.Empty<long>(), _counts = Array.Empty<long>();
        ushort[]? _colorMap;
        byte[] _jpegTables = Array.Empty<byte>();
        long _t4Options;
        float[]? _yccY, _yccCrR, _yccCbB, _yccCrG, _yccCbG;

        // per decode call
        int _rx, _ry, _rw, _rh;
        byte* _dst;
        long _dstRowBytes;

        public TiffImage(TiffFile file, long ifdOffset)
        {
            File = file;
            var e = Entries = file.ReadIfd(ifdOffset);
            long w = file.Integer(e, 256, 0), h = file.Integer(e, 257, 0);
            if (w <= 0 || h <= 0 || w > int.MaxValue || h > int.MaxValue) throw new ImageFormatException("TIFF: invalid image dimensions");
            Width = (int)w; Height = (int)h;
            long spp = file.Integer(e, 277, 1);
            if (spp < 1 || spp > 65535) throw new ImageFormatException("TIFF: invalid SamplesPerPixel");
            Spp = (int)spp;
            Bits = (int)Uniform(file.Integers(e, 258), 1, "BitsPerSample");
            Format = (int)Uniform(file.Integers(e, 339), 1, "SampleFormat");
            if (Format == 4) Format = 1; // "undefined" data is treated as unsigned integers
            Compression = (int)file.Integer(e, 259, 1);
            IsJpeg = Compression == 7;
            IsCcitt = Compression is 2 or 3 or 4;
            long pm = file.Integer(e, 262, -1);
            if (pm < 0) pm = IsCcitt ? 0 : Spp >= 3 ? 2 : 1; // required tag, but be lenient
            Photometric = (int)pm;
            Planar = Spp == 1 ? 1 : (int)file.Integer(e, 284, 1);
            if (Planar != 1 && Planar != 2) throw new ImageFormatException("TIFF: invalid PlanarConfiguration");
            Predictor = (int)file.Integer(e, 317, 1);
            FillOrder = (int)file.Integer(e, 266, 1);
            Extra = Array.ConvertAll(file.Integers(e, 338) ?? Array.Empty<long>(), x => (int)x);

            if (TiffFile.Find(e, 322, out _))
            {
                long tw = file.Integer(e, 322, 0), th = file.Integer(e, 323, 0);
                if (tw <= 0 || th <= 0 || tw > int.MaxValue || th > int.MaxValue) throw new ImageFormatException("TIFF: invalid tile size");
                Tiled = true; ChunkW = (int)tw; ChunkH = (int)th;
            }
            else
            {
                long rps = file.Integer(e, 278, uint.MaxValue);
                if (rps <= 0 || rps > Height) rps = Height;
                ChunkW = Width; ChunkH = (int)rps;
            }
            Across = (int)(((long)Width + ChunkW - 1) / ChunkW);
            Down = (int)(((long)Height + ChunkH - 1) / ChunkH);
            if ((long)Across * Down > int.MaxValue / Spp) throw new ImageFormatException("TIFF: too many strips or tiles");
            ChunksPerPlane = Across * Down;

            ColorSamples = Photometric switch
            {
                0 or 1 or 3 or 4 => 1,
                2 or 6 => 3,
                5 => 4,
                _ => throw new ImageNotSupportedException($"TIFF: photometric interpretation {Photometric} is not supported"),
            };
            if (Spp < ColorSamples) throw new ImageFormatException("TIFF: SamplesPerPixel too small for the photometric interpretation");
            RawType = (Format, Bits) switch
            {
                (1, 1) or (1, 2) or (1, 4) or (1, 8) => SampleType.UInt8,
                (1, 16) => SampleType.UInt16,
                (1, 32) => SampleType.UInt32,
                (2, 8) => SampleType.Int8,
                (2, 16) => SampleType.Int16,
                (2, 32) => SampleType.Int32,
                (3, 16) => SampleType.Float16,
                (3, 32) => SampleType.Float32,
                (3, 64) => SampleType.Float64,
                _ => throw new ImageNotSupportedException($"TIFF: {Bits}-bit samples of format {Format} are not supported"),
            };
            Bps = RawImage.BytesPerSample(RawType);
            RawChannels = Spp;
            OutType = RawType;
            int extra = Spp - ColorSamples;
            int kind = extra >= 1 && Extra.Length >= 1 ? Extra[0] : 0;
            HasAlpha = extra == 1 && (kind == 1 || kind == 2);
            AssociatedAlpha = HasAlpha && kind == 1;

            switch (Photometric)
            {
                case 3:
                    if (Format != 1 || Bits > 8) throw new ImageNotSupportedException("TIFF: only palettes with up to 8 bits are supported");
                    OutType = SampleType.UInt8;
                    OutColor = HasAlpha ? ColorModel.Rgba : ColorModel.Rgb;
                    break;
                case 5:
                    if (Format != 1 || (Bits != 8 && Bits != 16)) throw new ImageNotSupportedException("TIFF: CMYK needs 8 or 16 bit unsigned samples");
                    OutColor = HasAlpha ? ColorModel.Rgba : ColorModel.Rgb;
                    break;
                case 6:
                    if (Format != 1 || Bits != 8 || Spp != 3) throw new ImageNotSupportedException("TIFF: YCbCr needs three 8-bit samples");
                    OutColor = ColorModel.Rgb;
                    break;
                default:
                    // Extra samples rule: exactly one extra sample tagged as alpha gives GrayAlpha/Rgba, any other
                    // surplus (unspecified extras, several extras) keeps ALL samples untouched as ColorModel.Other.
                    if (extra == 0) OutColor = ColorSamples == 1 ? ColorModel.Gray : ColorModel.Rgb;
                    else if (HasAlpha) OutColor = ColorSamples == 1 ? ColorModel.GrayAlpha : ColorModel.Rgba;
                    else OutColor = ColorModel.Other;
                    break;
            }
            OutChannels = OutColor == ColorModel.Other ? Spp : RawImage.ChannelCount(OutColor);
            _scale = Bits < 8 && Photometric != 3 ? 255 / ((1 << Bits) - 1) : 1;

            if (Photometric == 6 && !IsJpeg)
            {
                // Without JPEG, subsampled YCbCr data is stored as packed blocks of (SubH*SubV luma, Cb, Cr).
                YccPacked = true;
                if (Planar != 1) throw new ImageNotSupportedException("TIFF: planar YCbCr is not supported");
                var sub = file.Integers(e, 530);
                if (sub != null && sub.Length >= 2) { SubH = (int)sub[0]; SubV = (int)sub[1]; }
                else { SubH = 2; SubV = 2; }
                if (!(SubH is 1 or 2 or 4) || !(SubV is 1 or 2 or 4)) throw new ImageFormatException("TIFF: invalid YCbCr subsampling");
            }
            if (Photometric == 6 && IsJpeg && Planar != 1) throw new ImageNotSupportedException("TIFF: planar YCbCr is not supported");
            if (IsCcitt && (Bits != 1 || Spp != 1)) throw new ImageFormatException("TIFF: CCITT compression needs bilevel data");
            if (IsJpeg && (Bits != 8 || Format != 1)) throw new ImageNotSupportedException("TIFF: only 8-bit JPEG compression is supported");
        }

        long Uniform(long[]? v, long def, string name)
        {
            if (v == null || v.Length == 0) return def;
            int n = Math.Min(v.Length, Spp);
            for (int i = 1; i < n; i++)
                if (v[i] != v[0]) throw new ImageNotSupportedException($"TIFF: differing {name} per sample is not supported");
            return v[0];
        }

        public ImageInfo Info(int frameCount) => new ImageInfo(ImageFormat.Tiff, Width, Height, OutChannels, OutType, OutColor, frameCount);

        int PlaneSamples => Planar == 2 ? 1 : Spp;

        /// <summary>Encoded bytes per chunk row, and the decoded size of a chunk holding <paramref name="rows"/> rows.</summary>
        long ChunkBytes(int rows, out int rowBytes)
        {
            if (YccPacked)
            {
                long across = (ChunkW + SubH - 1) / SubH, down = (rows + SubV - 1) / SubV;
                long rb = across * (SubH * SubV + 2);
                rowBytes = (int)Math.Min(rb, int.MaxValue);
                return rb * down;
            }
            long bitsPerRow = (long)ChunkW * PlaneSamples * Bits;
            long r = (bitsPerRow + 7) / 8;
            rowBytes = (int)Math.Min(r, int.MaxValue);
            return r * rows;
        }

        void EnsureDecodable()
        {
            switch (Compression)
            {
                case 1: case 5: case 8: case 32946: case 32773: case 2: case 3: case 4: break;
                case 7:
                    if (Tiff.JpegDecoder == null) throw new ImageNotSupportedException("TIFF: JPEG compression is not supported");
                    break;
                case 6: throw new ImageNotSupportedException("TIFF: old-style JPEG compression is not supported");
                default: throw new ImageNotSupportedException($"TIFF: compression {Compression} is not supported");
            }
            if (IsJpeg || YccPacked || IsCcitt)
            {
                if (Predictor != 1) throw new ImageNotSupportedException("TIFF: predictor not supported with this compression");
            }
            else if (Predictor == 2)
            {
                if (Bits < 8) throw new ImageNotSupportedException("TIFF: horizontal predictor on packed samples is not supported");
            }
            else if (Predictor == 3)
            {
                if (Format != 3) throw new ImageFormatException("TIFF: floating point predictor on integer data");
            }
            else if (Predictor != 1) throw new ImageNotSupportedException($"TIFF: predictor {Predictor} is not supported");

            if (ChunkBytes(ChunkH, out _) > int.MaxValue - 64 || (long)ChunkW * ChunkH * Spp > int.MaxValue - 64)
                throw new ImageNotSupportedException("TIFF: strips or tiles larger than 2 GB are not supported");
        }

        void LoadChunks()
        {
            var e = Entries;
            int total = ChunksPerPlane * (Planar == 2 ? Spp : 1);
            var offs = File.Integers(e, Tiled ? 324 : 273) ?? (Tiled ? File.Integers(e, 273) : null);
            if (offs == null) throw new ImageFormatException("TIFF: missing strip/tile offsets");
            if (offs.Length < total) throw new ImageFormatException("TIFF: too few strip/tile offsets");
            var cnts = File.Integers(e, Tiled ? 325 : 279) ?? (Tiled ? File.Integers(e, 279) : null);
            long len = File.Src.Length;
            if (cnts == null || cnts.Length < total)
            {
                // StripByteCounts is missing in some old files: everything up to the end of the file may belong to the strip
                cnts = new long[total];
                for (int i = 0; i < total; i++)
                {
                    long avail = offs[i] > 0 && offs[i] < len ? len - offs[i] : 0;
                    cnts[i] = Compression == 1 ? Math.Min(avail, ChunkBytes(ChunkH, out _)) : avail;
                }
            }
            long sum = 0; int empty = 0;
            for (int i = 0; i < total; i++)
            {
                long c = cnts[i], o = offs[i];
                if (c == 0 || o == 0) { cnts[i] = 0; empty++; continue; } // sparse file: the chunk is not stored
                if (o < 0 || c < 0 || o > len || c > len - o) throw new ImageFormatException("TIFF: strip/tile data outside of the file");
                sum += c;
            }
            _offsets = offs; _counts = cnts;

            // A header can claim any size. Refuse images that cannot possibly come out of the stored bytes
            // before allocating for them.
            long raw = ((long)Width * Spp * Bits + 7) / 8 * Height;
            if (IsCcitt)
            {
                // every coded row needs at least one bit
                if (Height > sum * 8 + (long)empty * ChunkH) throw new ImageFormatException("TIFF: image size inconsistent with the data size");
            }
            else
            {
                long ratio = Compression switch { 1 => 1, 32773 => 64, 8 or 32946 => 1040, _ => 4096 };
                long allowed = sum > long.MaxValue / 8192 ? long.MaxValue : sum * ratio + 1024;
                if (empty > 0) allowed += Math.Min((long)empty * ChunkBytes(ChunkH, out _), 256L << 20);
                if (YccPacked) raw = raw / 3 + 2; // at least one luma byte per pixel
                if (raw > allowed) throw new ImageFormatException("TIFF: image size inconsistent with the data size");
            }

            if (Photometric == 3)
            {
                var cm = File.Integers(e, 320);
                if (cm == null || cm.Length < 3 << Bits) throw new ImageFormatException("TIFF: missing or short ColorMap");
                _colorMap = Array.ConvertAll(cm, x => (ushort)x);
            }
            if (IsJpeg) _jpegTables = File.Bytes(e, 347) ?? Array.Empty<byte>();
            if (IsCcitt) _t4Options = File.Integer(e, Compression == 3 ? 292 : 293, 0);
            if (YccPacked) BuildYccTables();
        }

        void BuildYccTables()
        {
            var co = File.Doubles(Entries, 529);
            double lr = 0.299, lg = 0.587, lb = 0.114;
            if (co != null && co.Length >= 3 && co[1] > 0) { lr = co[0]; lg = co[1]; lb = co[2]; }
            var rb = File.Doubles(Entries, 532);
            if (rb == null || rb.Length < 6 || rb[1] == rb[0] || rb[3] == rb[2] || rb[5] == rb[4])
                rb = new double[] { 0, 255, 128, 255, 128, 255 };
            _yccY = new float[256]; _yccCrR = new float[256]; _yccCbB = new float[256]; _yccCrG = new float[256]; _yccCbG = new float[256];
            for (int i = 0; i < 256; i++)
            {
                double y = (i - rb[0]) * 255.0 / (rb[1] - rb[0]);
                double cb = (i - rb[2]) * 127.0 / (rb[3] - rb[2]);
                double cr = (i - rb[4]) * 127.0 / (rb[5] - rb[4]);
                _yccY[i] = (float)y;
                _yccCrR[i] = (float)(cr * (2 - 2 * lr));
                _yccCbB[i] = (float)(cb * (2 - 2 * lb));
                _yccCrG[i] = (float)(-cr * (2 - 2 * lr) * lr / lg);
                _yccCbG[i] = (float)(-cb * (2 - 2 * lb) * lb / lg);
            }
        }

        public RawImage Decode(int rx, int ry, int rw, int rh, TiffDecodeOptions opts)
        {
            if (rx < 0 || ry < 0 || rw <= 0 || rh <= 0 || rx > Width - rw || ry > Height - rh)
                throw new TiffArgumentException("region", "TIFF: region lies outside of the image");
            EnsureDecodable();
            if ((long)rw * rh * RawChannels > Array.MaxLength || (long)rw * rh * OutChannels > Array.MaxLength)
                throw new ImageNotSupportedException("TIFF: image too large to decode into a single array");
            LoadChunks();

            _rx = rx; _ry = ry; _rw = rw; _rh = rh;
            _dstRowBytes = (long)rw * RawChannels * Bps;
            var raw = RawImage.CreateArray(RawType, rw * rh * RawChannels);

            // chunks intersecting the region
            int cx0 = rx / ChunkW, cx1 = (rx + rw - 1) / ChunkW, cy0 = ry / ChunkH, cy1 = (ry + rh - 1) / ChunkH;
            int planes = Planar == 2 ? Spp : 1;
            var list = new int[(cx1 - cx0 + 1) * (cy1 - cy0 + 1) * planes];
            int n = 0;
            for (int p = 0; p < planes; p++)
                for (int cy = cy0; cy <= cy1; cy++)
                    for (int cx = cx0; cx <= cx1; cx++)
                        list[n++] = p * ChunksPerPlane + cy * Across + cx;

            var pin = GCHandle.Alloc(raw, GCHandleType.Pinned);
            try
            {
                _dst = (byte*)pin.AddrOfPinnedObject();
                if (opts.Parallel && n > 1 && _dstRowBytes * rh >= opts.ParallelThreshold)
                {
                    try
                    {
                        Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = opts.MaxDegreeOfParallelism > 0 ? opts.MaxDegreeOfParallelism : -1 },
                            i => DecodeChunk(list[i]));
                    }
                    catch (AggregateException ae)
                    {
                        foreach (var inner in ae.Flatten().InnerExceptions)
                            if (inner is ImageFormatException || inner is ImageNotSupportedException)
                                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(inner).Throw();
                        throw new ImageFormatException("TIFF: corrupt image data", ae.InnerException ?? ae);
                    }
                }
                else
                    for (int i = 0; i < n; i++) DecodeChunk(list[i]);
            }
            finally
            {
                _dst = null;
                pin.Free();
            }

            var data = TiffColor.Finish(this, raw, rw, rh, _colorMap);
            return new RawImage(rw, rh, OutColor, data, OutChannels, ReadMetadata());
        }

        ImageMetadata ReadMetadata()
        {
            var m = new ImageMetadata();
            var e = Entries;
            try
            {
                long o = File.Integer(e, 274, 0);
                if (o >= 1 && o <= 8) m.Orientation = (int)o;
                long unit = File.Integer(e, 296, 2);
                double f = unit == 2 ? 1 : unit == 3 ? 2.54 : 0;
                var xr = File.Doubles(e, 282); var yr = File.Doubles(e, 283);
                if (xr != null && xr.Length > 0 && double.IsFinite(xr[0]) && xr[0] > 0) m.DpiX = xr[0] * f;
                if (yr != null && yr.Length > 0 && double.IsFinite(yr[0]) && yr[0] > 0) m.DpiY = yr[0] * f;
                var icc = File.Bytes(e, 34675);
                if (icc != null && icc.Length > 0) m.IccProfile = icc;
            }
            catch (ImageFormatException) { } // broken metadata must not make the pixels unreadable
            return m;
        }

        void DecodeChunk(int index)
        {
            int plane = index / ChunksPerPlane, ci = index % ChunksPerPlane;
            int x0 = ci % Across * ChunkW, y0 = ci / Across * ChunkH;
            int rows = Tiled ? ChunkH : Math.Min(ChunkH, Height - y0);
            int ix0 = Math.Max(x0, _rx), ix1 = (int)Math.Min(Math.Min((long)x0 + ChunkW, Width), (long)_rx + _rw);
            int iy0 = Math.Max(y0, _ry), iy1 = (int)Math.Min(Math.Min((long)y0 + rows, Height), (long)_ry + _rh);
            if (ix0 >= ix1 || iy0 >= iy1) return;
            long count = _counts[index];
            if (count == 0) return; // sparse chunk: stays zero
            if (count > int.MaxValue) throw new ImageNotSupportedException("TIFF: strips or tiles larger than 2 GB are not supported");
            int ps = PlaneSamples;
            int expected = (int)ChunkBytes(rows, out int rowBytes);

            byte[]? srcRent = null, bufRent = null, rgbRent = null;
            try
            {
                if (IsJpeg)
                {
                    int size = ChunkW * rows * ps;
                    bufRent = ArrayPool<byte>.Shared.Rent(size);
                    var src = File.Src.Get(_offsets[index], (int)count, ref srcRent);
                    Tiff.JpegDecoder!(_jpegTables, src, ChunkW, rows, ps, Photometric == 6, bufRent.AsSpan(0, size));
                    fixed (byte* b = bufRent) Transfer(b, ChunkW * ps, ps, plane, x0, y0, ix0, ix1, iy0, iy1, 8);
                    return;
                }

                bool direct = !Tiled && Planar == 1 && Bits >= 8 && !YccPacked && _rx == 0 && _rw == Width && y0 >= _ry && y0 + rows <= _ry + _rh;
                if (direct)
                {
                    byte* b = _dst + (y0 - _ry) * _dstRowBytes;
                    Decompress(index, b, expected, (int)count, ref srcRent);
                    Unpredict(b, rows, rowBytes, ps);
                    return;
                }

                bufRent = ArrayPool<byte>.Shared.Rent(expected);
                fixed (byte* b = bufRent)
                {
                    Decompress(index, b, expected, (int)count, ref srcRent);
                    if (YccPacked)
                    {
                        int size = ChunkW * rows * 3;
                        rgbRent = ArrayPool<byte>.Shared.Rent(size);
                        fixed (byte* rgb = rgbRent)
                        {
                            YccToRgb(b, rgb, rows);
                            Transfer(rgb, ChunkW * 3, 3, 0, x0, y0, ix0, ix1, iy0, iy1, 8);
                        }
                    }
                    else
                    {
                        Unpredict(b, rows, rowBytes, ps);
                        Transfer(b, rowBytes, ps, plane, x0, y0, ix0, ix1, iy0, iy1, Bits);
                    }
                }
            }
            finally
            {
                if (srcRent != null) ArrayPool<byte>.Shared.Return(srcRent);
                if (bufRent != null) ArrayPool<byte>.Shared.Return(bufRent);
                if (rgbRent != null) ArrayPool<byte>.Shared.Return(rgbRent);
            }
        }

        void Decompress(int index, byte* dst, int expected, int count, ref byte[]? srcRent)
        {
            long offset = _offsets[index];
            if (Compression == 1)
            {
                if (count < expected) throw new ImageFormatException("TIFF: strip/tile data is too short");
                var d = new Span<byte>(dst, expected);
                File.Src.Read(offset, d);
                if (FillOrder == 2) TiffPredict.ReverseBits(d, d);
                return;
            }
            var src = File.Src.Get(offset, count, ref srcRent);
            byte[]? rev = null;
            try
            {
                // FillOrder 2 only makes sense for bit oriented data; byte oriented deflate streams are left alone.
                if (FillOrder == 2 && Compression != 8 && Compression != 32946)
                {
                    rev = ArrayPool<byte>.Shared.Rent(count);
                    TiffPredict.ReverseBits(src, rev);
                    src = rev.AsSpan(0, count);
                }
                int got;
                switch (Compression)
                {
                    case 5: got = TiffLzw.Decode(src, dst, expected); break;
                    case 32773: got = TiffPackBits.Decode(src, dst, expected); break;
                    case 8:
                    case 32946:
                        try { got = Zlib.Decompress(src, new Span<byte>(dst, expected)); }
                        catch (Exception ex) when (ex is InvalidDataException || ex is IOException)
                        {
                            throw new ImageFormatException("TIFF: corrupt deflate data", ex);
                        }
                        break;
                    case 2: case 3: case 4:
                        int rows = expected / Math.Max(1, (ChunkW + 7) / 8);
                        TiffCcitt.Decode(src, dst, ChunkW, rows, Compression, _t4Options);
                        got = expected;
                        break;
                    default: throw new ImageNotSupportedException($"TIFF: compression {Compression} is not supported");
                }
                if (got < expected) throw new ImageFormatException("TIFF: compressed strip/tile data is truncated");
            }
            finally
            {
                if (rev != null) ArrayPool<byte>.Shared.Return(rev);
            }
        }

        /// <summary>Brings a decompressed chunk into machine byte order and undoes the predictor.</summary>
        void Unpredict(byte* b, int rows, int rowBytes, int ps)
        {
            if (Bits < 8) return;
            int samples = ChunkW * ps;
            if (Predictor == 3)
            {
                var tmp = ArrayPool<byte>.Shared.Rent(rowBytes);
                try
                {
                    fixed (byte* t = tmp)
                        for (int y = 0; y < rows; y++) TiffPredict.UndoFloat(b + (long)y * rowBytes, samples, ps, Bps, t);
                }
                finally { ArrayPool<byte>.Shared.Return(tmp); }
                return;
            }
            if (File.Swap && Bps > 1) TiffPredict.SwapBytes(b, (long)rowBytes * rows, Bps);
            if (Predictor == 2)
                for (int y = 0; y < rows; y++) TiffPredict.UndoHorizontal(b + (long)y * rowBytes, samples, ps, Bps);
        }

        /// <summary>Copies the part of a decoded chunk that lies inside the requested region into the raw image.</summary>
        void Transfer(byte* buf, int rowBytes, int ps, int plane, int x0, int y0, int ix0, int ix1, int iy0, int iy1, int bits)
        {
            int n = ix1 - ix0, sx = ix0 - x0, rc = RawChannels, bps = Bps;
            for (int y = iy0; y < iy1; y++)
            {
                byte* s = buf + (long)(y - y0) * rowBytes;
                byte* d = _dst + (y - _ry) * _dstRowBytes + (long)(ix0 - _rx) * rc * bps;
                if (bits >= 8)
                {
                    if (ps == rc)
                    {
                        long len = (long)n * ps * bps;
                        Buffer.MemoryCopy(s + (long)sx * ps * bps, d, len, len);
                        continue;
                    }
                    s += (long)sx * bps; d += plane * bps;
                    long step = (long)rc * bps;
                    switch (bps)
                    {
                        case 1: for (int i = 0; i < n; i++) d[i * step] = s[i]; break;
                        case 2: for (int i = 0; i < n; i++) *(ushort*)(d + i * step) = ((ushort*)s)[i]; break;
                        case 4: for (int i = 0; i < n; i++) *(uint*)(d + i * step) = ((uint*)s)[i]; break;
                        default: for (int i = 0; i < n; i++) *(ulong*)(d + i * step) = ((ulong*)s)[i]; break;
                    }
                    continue;
                }
                int mask = (1 << bits) - 1, scale = _scale;
                long bit = (long)sx * ps * bits;
                long total = (long)n * ps;
                if (ps == rc)
                {
                    long i = 0;
                    if (bits == 1 && (bit & 7) == 0)
                    {
                        // whole bytes at once for bilevel rows
                        byte* sp = s + (bit >> 3);
                        byte on = (byte)scale;
                        for (; i + 8 <= total; i += 8)
                        {
                            int v = *sp++;
                            d[i] = (v & 0x80) != 0 ? on : (byte)0; d[i + 1] = (v & 0x40) != 0 ? on : (byte)0;
                            d[i + 2] = (v & 0x20) != 0 ? on : (byte)0; d[i + 3] = (v & 0x10) != 0 ? on : (byte)0;
                            d[i + 4] = (v & 0x08) != 0 ? on : (byte)0; d[i + 5] = (v & 0x04) != 0 ? on : (byte)0;
                            d[i + 6] = (v & 0x02) != 0 ? on : (byte)0; d[i + 7] = (v & 0x01) != 0 ? on : (byte)0;
                        }
                        bit += i;
                    }
                    for (; i < total; i++, bit += bits)
                        d[i] = (byte)(((s[bit >> 3] >> (8 - bits - (int)(bit & 7))) & mask) * scale);
                }
                else
                {
                    d += plane;
                    for (long i = 0; i < total; i++, bit += bits)
                        d[i * rc] = (byte)(((s[bit >> 3] >> (8 - bits - (int)(bit & 7))) & mask) * scale);
                }
            }
        }

        void YccToRgb(byte* src, byte* rgb, int rows)
        {
            int w = ChunkW, sh = SubH, sv = SubV;
            int across = (w + sh - 1) / sh, down = (rows + sv - 1) / sv;
            float[] ty = _yccY!, crR = _yccCrR!, cbB = _yccCbB!, crG = _yccCrG!, cbG = _yccCbG!;
            byte* p = src;
            for (int by = 0; by < down; by++)
                for (int bx = 0; bx < across; bx++)
                {
                    int cb = p[sh * sv], cr = p[sh * sv + 1];
                    float fr = crR[cr], fg = crG[cr] + cbG[cb], fb = cbB[cb];
                    for (int j = 0; j < sv; j++)
                    {
                        int y = by * sv + j;
                        if (y >= rows) break;
                        for (int i = 0; i < sh; i++)
                        {
                            int x = bx * sh + i;
                            if (x >= w) break;
                            float l = ty[p[j * sh + i]];
                            byte* o = rgb + ((long)y * w + x) * 3;
                            o[0] = Clamp(l + fr); o[1] = Clamp(l + fg); o[2] = Clamp(l + fb);
                        }
                    }
                    p += sh * sv + 2;
                }
        }

        static byte Clamp(float v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5f);
    }

    /// <summary>Photometric post-processing of the raw sample image (inversion, palette, CMYK, alpha).</summary>
    internal static class TiffColor
    {
        public static Array Finish(TiffImage img, Array raw, int w, int h, ushort[]? colorMap)
        {
            int rc = img.RawChannels;
            long pixels = (long)w * h;
            switch (img.Photometric)
            {
                case 0:
                    Invert(raw, img.RawType, rc);
                    break;
                case 3:
                    return Palette((byte[])raw, pixels, rc, img.HasAlpha, img.Bits, colorMap!);
                case 5:
                    raw = Cmyk(raw, pixels, rc, img.HasAlpha);
                    break;
            }
            if (img.AssociatedAlpha && img.Photometric != 3) Unpremultiply(raw, img.RawType, img.OutChannels);
            return raw;
        }

        /// <summary>MinIsWhite: flips the colour sample of unsigned data so that 0 is black. Signed and float data is left as stored.</summary>
        static void Invert(Array raw, SampleType type, int step)
        {
            // (switching on the sample type, not the array type: an sbyte[] also matches "byte[]" at runtime)
            switch (type)
            {
                case SampleType.UInt8:
                {
                    var a = (byte[])raw;
                    if (step == 1)
                    {
                        int i = 0;
                        var ones = new System.Numerics.Vector<byte>(255);
                        for (; i + System.Numerics.Vector<byte>.Count <= a.Length; i += System.Numerics.Vector<byte>.Count)
                            (ones - new System.Numerics.Vector<byte>(a, i)).CopyTo(a, i);
                        for (; i < a.Length; i++) a[i] = (byte)(255 - a[i]);
                    }
                    else
                        for (int i = 0; i < a.Length; i += step) a[i] = (byte)(255 - a[i]);
                    break;
                }
                case SampleType.UInt16:
                {
                    var a = (ushort[])raw;
                    for (int i = 0; i < a.Length; i += step) a[i] = (ushort)(65535 - a[i]);
                    break;
                }
                case SampleType.UInt32:
                {
                    var a = (uint[])raw;
                    for (int i = 0; i < a.Length; i += step) a[i] = uint.MaxValue - a[i];
                    break;
                }
            }
        }

        static byte[] Palette(byte[] raw, long pixels, int rc, bool alpha, int bits, ushort[] cm)
        {
            int n = 1 << bits;
            // Colormaps are 16 bit; some writers store 8-bit values unscaled, which is detected like libtiff does.
            bool is8 = true;
            for (int i = 0; i < 3 * n; i++) if (cm[i] >= 256) { is8 = false; break; }
            var lut = new byte[3 * 256];
            for (int i = 0; i < n; i++)
                for (int c = 0; c < 3; c++)
                    lut[i * 3 + c] = (byte)(is8 ? cm[c * n + i] : cm[c * n + i] >> 8);
            int oc = alpha ? 4 : 3;
            var o = new byte[pixels * oc];
            int ascale = 255 / (n - 1);
            int si = 0, di = 0;
            for (long p = 0; p < pixels; p++, si += rc, di += oc)
            {
                int l = raw[si] * 3;
                o[di] = lut[l]; o[di + 1] = lut[l + 1]; o[di + 2] = lut[l + 2];
                if (alpha) o[di + 3] = (byte)(raw[si + 1] * ascale);
            }
            return o;
        }

        /// <summary>Naive CMYK to RGB: c' = (max - c) * (max - k) / max, rounded. Extra samples other than alpha are dropped.</summary>
        static Array Cmyk(Array raw, long pixels, int rc, bool alpha)
        {
            int oc = alpha ? 4 : 3;
            if (raw is byte[] a)
            {
                var o = new byte[pixels * oc];
                int si = 0, di = 0;
                for (long p = 0; p < pixels; p++, si += rc, di += oc)
                {
                    int k = 255 - a[si + 3];
                    o[di] = (byte)(((255 - a[si]) * k + 127) / 255);
                    o[di + 1] = (byte)(((255 - a[si + 1]) * k + 127) / 255);
                    o[di + 2] = (byte)(((255 - a[si + 2]) * k + 127) / 255);
                    if (alpha) o[di + 3] = a[si + 4];
                }
                return o;
            }
            else
            {
                var s = (ushort[])raw;
                var o = new ushort[pixels * oc];
                int si = 0, di = 0;
                for (long p = 0; p < pixels; p++, si += rc, di += oc)
                {
                    uint k = 65535u - s[si + 3];
                    o[di] = (ushort)(((65535u - s[si]) * k + 32767) / 65535);
                    o[di + 1] = (ushort)(((65535u - s[si + 1]) * k + 32767) / 65535);
                    o[di + 2] = (ushort)(((65535u - s[si + 2]) * k + 32767) / 65535);
                    if (alpha) o[di + 3] = s[si + 4];
                }
                return o;
            }
        }

        /// <summary>
        /// Associated alpha to straight alpha: c = round(c * max / a), clamped; pixels with zero alpha are left as stored.
        /// Signed integer data is not touched.
        /// </summary>
        static void Unpremultiply(Array raw, SampleType type, int ch)
        {
            int cc = ch - 1;
            switch (type)
            {
                case SampleType.UInt8:
                {
                    var a = (byte[])raw;
                    for (int i = 0; i + ch <= a.Length; i += ch)
                    {
                        int al = a[i + cc];
                        if (al == 0 || al == 255) continue;
                        for (int c = 0; c < cc; c++) a[i + c] = (byte)Math.Min(255, (a[i + c] * 255 + al / 2) / al);
                    }
                    break;
                }
                case SampleType.UInt16:
                {
                    var a = (ushort[])raw;
                    for (int i = 0; i + ch <= a.Length; i += ch)
                    {
                        uint al = a[i + cc];
                        if (al == 0 || al == 65535) continue;
                        for (int c = 0; c < cc; c++) a[i + c] = (ushort)Math.Min(65535u, (a[i + c] * 65535u + al / 2) / al);
                    }
                    break;
                }
                case SampleType.UInt32:
                {
                    var a = (uint[])raw;
                    for (int i = 0; i + ch <= a.Length; i += ch)
                    {
                        ulong al = a[i + cc];
                        if (al == 0 || al == uint.MaxValue) continue;
                        for (int c = 0; c < cc; c++) a[i + c] = (uint)Math.Min(uint.MaxValue, (a[i + c] * (ulong)uint.MaxValue + al / 2) / al);
                    }
                    break;
                }
                case SampleType.Float16:
                {
                    var a = (Half[])raw;
                    for (int i = 0; i + ch <= a.Length; i += ch)
                    {
                        float al = (float)a[i + cc];
                        if (al == 0 || al == 1) continue;
                        for (int c = 0; c < cc; c++) a[i + c] = (Half)((float)a[i + c] / al);
                    }
                    break;
                }
                case SampleType.Float32:
                {
                    var a = (float[])raw;
                    for (int i = 0; i + ch <= a.Length; i += ch)
                    {
                        float al = a[i + cc];
                        if (al == 0 || al == 1) continue;
                        for (int c = 0; c < cc; c++) a[i + c] /= al;
                    }
                    break;
                }
                case SampleType.Float64:
                {
                    var a = (double[])raw;
                    for (int i = 0; i + ch <= a.Length; i += ch)
                    {
                        double al = a[i + cc];
                        if (al == 0 || al == 1) continue;
                        for (int c = 0; c < cc; c++) a[i + c] /= al;
                    }
                    break;
                }
            }
        }
    }
}
