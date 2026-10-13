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

    /// <summary>How the samples of a strip/tile are laid out once it is decompressed.</summary>
    internal enum TiffChunkKind
    {
        /// <summary>Whole 1/2/4/8 byte samples in file byte order.</summary>
        Samples,
        /// <summary>Samples of 1..32 bits packed MSB first, rows padded to a byte.</summary>
        Packed,
        /// <summary>24-bit floats, three bytes per sample.</summary>
        Float24,
        /// <summary>Subsampled YCbCr stored as blocks of (SubH*SubV luma, Cb, Cr).</summary>
        YccPacked,
        Jpeg,
        Webp,
        /// <summary>SGI LogL / LogLuv, converted to floats while decoding.</summary>
        LogLuv,
    }

    /// <summary>One IFD: parsed layout, and the strip/tile decoder for it.</summary>
    internal sealed unsafe class TiffImage
    {
        public readonly TiffFile File;
        public readonly TiffEntry[] Entries;
        public readonly int Width, Height, Spp, Bits, Format, Compression, Photometric, Planar, Predictor, FillOrder;
        /// <summary>Stored bits of every sample; <see cref="Bits"/> is the largest.</summary>
        public readonly int[] BitsPer;
        public readonly bool UniformBits;
        public readonly bool Tiled;
        public readonly int ChunkW, ChunkH, Across, Down, ChunksPerPlane;
        public readonly int[] Extra;
        public readonly int SubH = 1, SubV = 1;
        public readonly TiffChunkKind Kind;

        // raw = samples as stored (after unpacking), out = what the caller gets
        public readonly SampleType RawType, OutType;
        public readonly int Bps, ColorSamples, RawChannels, OutChannels;
        public readonly ColorModel OutColor;
        public readonly bool HasAlpha, AssociatedAlpha, IsJpeg, IsCcitt, IsLab, IsLogLuv, ConvertToRgb;
        /// <summary>Largest value of an unsigned sample as it ends up in the raw image (after optional scaling).</summary>
        public readonly ulong SampleMax;
        /// <summary>Planar YCbCr: the planes are assembled as full resolution YCbCr and converted at the end.</summary>
        public readonly bool YccPlanar;

        readonly bool[] _scale;      // per sample: stretch to the full range of the output type
        readonly ulong[] _inMax;
        readonly ulong _outMax;
        readonly int _pixelBits;     // stored bits of one contiguous pixel
        readonly bool _oldJpeg;
        readonly int _storedStrips;

        long[] _offsets = Array.Empty<long>(), _counts = Array.Empty<long>();
        ushort[]? _colorMap;
        byte[] _jpegTables = Array.Empty<byte>();
        TiffOldJpeg? _ojpeg;
        long _t4Options;
        float[]? _yccY, _yccCrR, _yccCbB, _yccCrG, _yccCbG;

        // per decode call
        int _rx, _ry, _rw, _rh;
        byte* _dst;
        long _dstRowBytes;

        public TiffImage(TiffFile file, long ifdOffset, TiffDecodeOptions? options = null)
        {
            options ??= TiffDecodeOptions.Default;
            ConvertToRgb = options.ConvertToRgb;
            File = file;
            var e = Entries = file.ReadIfd(ifdOffset);
            long w = file.Integer(e, 256, 0), h = file.Integer(e, 257, 0);
            if (w <= 0 || h <= 0 || w > int.MaxValue || h > int.MaxValue) throw new ImageFormatException("TIFF: invalid image dimensions");
            Width = (int)w; Height = (int)h;
            long spp = file.Integer(e, 277, 1);
            if (spp < 1 || spp > 65535) throw new ImageFormatException("TIFF: invalid SamplesPerPixel");
            Spp = (int)spp;
            Compression = (int)file.Integer(e, 259, 1);
            _oldJpeg = Compression == 6;
            IsJpeg = Compression == 7 || _oldJpeg;
            IsCcitt = Compression is 2 or 3 or 4;
            long pm = file.Integer(e, 262, -1);
            if (pm < 0) pm = IsCcitt ? 0 : Spp >= 3 ? 2 : 1; // required tag, but be lenient
            Photometric = (int)pm;
            IsLab = Photometric is 8 or 9 or 10;
            IsLogLuv = Photometric is 32844 or 32845;

            BitsPer = new int[Spp];
            var bits = file.Integers(e, 258);
            UniformBits = true;
            for (int i = 0; i < Spp; i++)
            {
                long b = bits == null || bits.Length == 0 ? 1 : bits[Math.Min(i, bits.Length - 1)];
                if (b < 1 || b > 64) throw new ImageFormatException("TIFF: invalid BitsPerSample");
                BitsPer[i] = (int)b;
                Bits = Math.Max(Bits, (int)b);
                _pixelBits += (int)b;
                if (b != BitsPer[0]) UniformBits = false;
            }
            Format = (int)Uniform(file.Integers(e, 339), 1, "SampleFormat");
            if (Format == 4) Format = 1; // "undefined" data is treated as unsigned integers

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
            if (_oldJpeg)
            {
                // the strips of an old-style JPEG file are pieces of ONE frame: treat the image as a single chunk
                if (Tiled || Planar != 1) throw new ImageNotSupportedException("TIFF: tiled or planar old-style JPEG is not supported");
                _storedStrips = (int)(((long)Height + ChunkH - 1) / ChunkH);
                ChunkH = Height;
            }
            Across = (int)(((long)Width + ChunkW - 1) / ChunkW);
            Down = (int)(((long)Height + ChunkH - 1) / ChunkH);
            if ((long)Across * Down > int.MaxValue / Spp) throw new ImageFormatException("TIFF: too many strips or tiles");
            ChunksPerPlane = Across * Down;

            ColorSamples = Photometric switch
            {
                0 or 1 or 3 or 4 or 32844 => 1,
                2 or 6 or 8 or 9 or 10 or 32845 => 3,
                5 => 4,
                _ => throw new ImageNotSupportedException($"TIFF: photometric interpretation {Photometric} is not supported"),
            };
            if (Spp < ColorSamples) throw new ImageFormatException("TIFF: SamplesPerPixel too small for the photometric interpretation");

            if (IsLogLuv)
            {
                // The tags describe the caller side format of the writer; the stored data always is 16-bit log
                // luminance (LogL) or a 32-bit LogLuv word per pixel.
                if (Compression != 34676) throw new ImageNotSupportedException($"TIFF: LogLuv with compression {Compression} is not supported");
                if (Spp != ColorSamples || Planar != 1) throw new ImageNotSupportedException("TIFF: LogLuv with extra samples is not supported");
                RawType = SampleType.Float32;
                Kind = TiffChunkKind.LogLuv;
            }
            else
            {
                RawType = Format switch
                {
                    1 when Bits <= 8 => SampleType.UInt8,
                    1 when Bits <= 16 => SampleType.UInt16,
                    1 when Bits <= 32 => SampleType.UInt32,
                    1 when Bits == 64 && UniformBits => SampleType.UInt64,
                    2 when Bits <= 8 => SampleType.Int8,
                    2 when Bits <= 16 => SampleType.Int16,
                    2 when Bits <= 32 => SampleType.Int32,
                    2 when Bits == 64 && UniformBits => SampleType.Int64,
                    3 when Bits == 16 && UniformBits => SampleType.Float16,
                    3 when (Bits == 24 || Bits == 32) && UniformBits => SampleType.Float32,
                    3 when Bits == 64 && UniformBits => SampleType.Float64,
                    _ => throw new ImageNotSupportedException($"TIFF: {Bits}-bit samples of format {Format} are not supported"),
                };
                bool whole = UniformBits && Bits is 8 or 16 or 32 or 64;
                if (IsJpeg) Kind = TiffChunkKind.Jpeg;
                else if (Compression == 50001) Kind = TiffChunkKind.Webp;
                else if (Format == 3 && Bits == 24) Kind = TiffChunkKind.Float24;
                else Kind = whole ? TiffChunkKind.Samples : TiffChunkKind.Packed;
            }
            Bps = RawImage.BytesPerSample(RawType);
            RawChannels = IsLogLuv ? ColorSamples : Spp;
            OutType = RawType;
            int extra = Spp - ColorSamples;
            int kind = extra >= 1 && Extra.Length >= 1 ? Extra[0] : 0;
            HasAlpha = extra == 1 && (kind == 1 || kind == 2);
            AssociatedAlpha = HasAlpha && kind == 1;

            // Bit depth rule: samples keep their stored values in the next larger type. The one exception (kept from
            // the common viewer behaviour) are 1, 2 and 4 bit images, which are stretched to 0..255 by default.
            _scale = new bool[Spp];
            _inMax = new ulong[Spp];
            _outMax = Bps >= 8 ? ulong.MaxValue : (1UL << (Bps * 8)) - 1;
            bool anyScale = false;
            for (int i = 0; i < Spp; i++)
            {
                int b = BitsPer[i];
                _inMax[i] = b >= 64 ? ulong.MaxValue : (1UL << b) - 1;
                bool odd = !(b is 8 or 16 or 32 or 64);
                _scale[i] = Format == 1 && Photometric != 3 && odd && Kind == TiffChunkKind.Packed && options.BitScaling switch
                {
                    TiffBitScaling.FullRange => true,
                    TiffBitScaling.None => false,
                    _ => UniformBits && b is 1 or 2 or 4,
                };
                anyScale |= _scale[i];
            }
            SampleMax = anyScale || !UniformBits ? _outMax : _inMax[0];

            switch (Photometric)
            {
                case 3:
                    if (Format != 1 || Bits > 16) throw new ImageNotSupportedException("TIFF: palettes need unsigned indices of up to 16 bits");
                    OutType = SampleType.UInt8;
                    OutColor = HasAlpha ? ColorModel.Rgba : ColorModel.Rgb;
                    break;
                case 5:
                    if (Format != 1 || !UniformBits || (Bits != 8 && Bits != 16)) throw new ImageNotSupportedException("TIFF: CMYK needs 8 or 16 bit unsigned samples");
                    OutColor = !ConvertToRgb ? ColorModel.Other : HasAlpha ? ColorModel.Rgba : ColorModel.Rgb;
                    break;
                case 6:
                    if (Format != 1 || !UniformBits || Bits != 8 || Spp != 3) throw new ImageNotSupportedException("TIFF: YCbCr needs three 8-bit samples");
                    OutColor = ColorModel.Rgb;
                    break;
                case 8: case 9: case 10:
                    if (Format != 1 || !UniformBits || (Bits != 8 && Bits != 16)) throw new ImageNotSupportedException("TIFF: Lab needs 8 or 16 bit samples");
                    OutColor = !ConvertToRgb ? ColorModel.Other : HasAlpha ? ColorModel.Rgba : extra == 0 ? ColorModel.Rgb : ColorModel.Other;
                    if (ConvertToRgb && OutColor == ColorModel.Other) throw new ImageNotSupportedException("TIFF: Lab with unspecified extra samples is not supported");
                    break;
                case 32844:
                    OutColor = ColorModel.Gray;
                    break;
                case 32845:
                    OutColor = ConvertToRgb ? ColorModel.Rgb : ColorModel.Other;
                    break;
                default:
                    // Extra samples rule: exactly one extra sample tagged as alpha gives GrayAlpha/Rgba, any other
                    // surplus (unspecified extras, several extras) keeps ALL samples untouched as ColorModel.Other.
                    if (extra == 0) OutColor = ColorSamples == 1 ? ColorModel.Gray : ColorModel.Rgb;
                    else if (HasAlpha) OutColor = ColorSamples == 1 ? ColorModel.GrayAlpha : ColorModel.Rgba;
                    else OutColor = ColorModel.Other;
                    break;
            }
            OutChannels = OutColor == ColorModel.Other ? RawChannels : RawImage.ChannelCount(OutColor);

            if (Photometric == 6)
            {
                var sub = file.Integers(e, 530);
                if (sub != null && sub.Length >= 2) { SubH = (int)sub[0]; SubV = (int)sub[1]; }
                else { SubH = 2; SubV = 2; }
                if (!(SubH is 1 or 2 or 4) || !(SubV is 1 or 2 or 4)) throw new ImageFormatException("TIFF: invalid YCbCr subsampling");
                if (Planar == 2) YccPlanar = true;
                else if (!IsJpeg)
                {
                    // Without JPEG, contiguous YCbCr is stored as packed blocks of (SubH*SubV luma, Cb, Cr).
                    if (Kind != TiffChunkKind.Samples) throw new ImageNotSupportedException("TIFF: YCbCr with this compression is not supported");
                    Kind = TiffChunkKind.YccPacked;
                }
            }
            if (IsCcitt && (Bits != 1 || Spp != 1)) throw new ImageFormatException("TIFF: CCITT compression needs bilevel data");
            if (IsJpeg && (Format != 1 || !UniformBits || Bits > 16)) throw new ImageNotSupportedException("TIFF: JPEG compression needs unsigned samples of up to 16 bits");
            if (Kind == TiffChunkKind.Webp && (Format != 1 || !UniformBits || Bits != 8 || Planar != 1 || Spp < 3 || Spp > 4))
                throw new ImageNotSupportedException("TIFF: WebP compression needs 8-bit RGB or RGBA");
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

        /// <summary>Bytes of one stored row of a chunk that is <paramref name="cw"/> samples wide (Samples / Packed / Float24 kinds).</summary>
        long RowBytes(int cw, int plane) => Kind switch
        {
            TiffChunkKind.Samples => (long)cw * PlaneSamples * Bps,
            TiffChunkKind.Float24 => (long)cw * PlaneSamples * 3,
            _ => ((long)cw * (Planar == 2 ? BitsPer[plane] : _pixelBits) + 7) / 8,
        };

        long YccBlockRowBytes => (long)((ChunkW + SubH - 1) / SubH) * (SubH * SubV + 2);

        /// <summary>Upper bound for the decompressed size of one chunk.</summary>
        long MaxChunkBytes() => Kind switch
        {
            TiffChunkKind.YccPacked => YccBlockRowBytes * ((ChunkH + SubV - 1) / SubV),
            TiffChunkKind.LogLuv => (long)ChunkW * ChunkH * 4,
            TiffChunkKind.Jpeg or TiffChunkKind.Webp => (long)ChunkW * ChunkH * Spp * Bps,
            _ => RowBytes(ChunkW, 0) * ChunkH,
        };

        void EnsureDecodable()
        {
            switch (Compression)
            {
                case 1: case 5: case 8: case 32946: case 32773: case 2: case 3: case 4: case 6: case 7:
                case 50000: case 34925: case 50001:
                    break;
                case 34676:
                    if (!IsLogLuv) throw new ImageFormatException("TIFF: SGILOG compression needs a LogL / LogLuv photometric interpretation");
                    break;
                case 34677: throw new ImageNotSupportedException("TIFF: 24-bit LogLuv (SGILOG24) is not supported");
                case 32909: throw new ImageNotSupportedException("TIFF: PixarLog compression is not supported");
                default: throw new ImageNotSupportedException($"TIFF: compression {Compression} is not supported");
            }
            bool plain = Kind is TiffChunkKind.Samples or TiffChunkKind.Float24 && !IsCcitt;
            if (Predictor == 1) { }
            else if (!plain || YccPlanar)
            {
                if (Kind == TiffChunkKind.Packed && !IsCcitt)
                    throw new ImageNotSupportedException($"TIFF: a predictor on {Bits}-bit samples is not supported");
                throw new ImageNotSupportedException("TIFF: predictor not supported with this compression");
            }
            else if (Predictor == 2)
            {
                if (Kind == TiffChunkKind.Float24) throw new ImageNotSupportedException("TIFF: horizontal predictor on 24-bit floats is not supported");
            }
            else if (Predictor == 3)
            {
                if (Format != 3) throw new ImageFormatException("TIFF: floating point predictor on integer data");
            }
            else throw new ImageNotSupportedException($"TIFF: predictor {Predictor} is not supported");

            if (YccPlanar && Kind != TiffChunkKind.Samples && Kind != TiffChunkKind.Jpeg)
                throw new ImageNotSupportedException("TIFF: planar YCbCr with this compression is not supported");
            // Kinds that go through intermediate buffers or codecs with 32-bit sizes
            if (!(Kind is TiffChunkKind.Samples or TiffChunkKind.Packed or TiffChunkKind.Float24) && MaxChunkBytes() > int.MaxValue - 64)
                throw new ImageNotSupportedException("TIFF: strips or tiles larger than 2 GB are not supported with this compression");
        }

        void LoadChunks()
        {
            var e = Entries;
            int total = _oldJpeg ? _storedStrips : ChunksPerPlane * (Planar == 2 ? Spp : 1);
            var offs = File.Integers(e, Tiled ? 324 : 273) ?? (Tiled ? File.Integers(e, 273) : null);
            if (offs == null) throw new ImageFormatException("TIFF: missing strip/tile offsets");
            if (offs.Length < total) throw new ImageFormatException("TIFF: too few strip/tile offsets");
            var cnts = File.Integers(e, Tiled ? 325 : 279) ?? (Tiled ? File.Integers(e, 279) : null);
            long len = File.Src.Length;
            bool noCounts = cnts == null || cnts.Length < total;
            if (noCounts) cnts = new long[total];
            long[]? sorted = null;
            long sum = 0; int empty = 0;
            long maxChunk = MaxChunkBytes();
            for (int i = 0; i < total; i++)
            {
                long c = cnts![i], o = offs[i];
                if (o == 0 && !noCounts) { cnts[i] = 0; empty++; continue; } // sparse file (GDAL): the chunk is not stored
                if (o <= 0 || o > len || c < 0) throw new ImageFormatException("TIFF: strip/tile data outside of the file");
                if (c == 0)
                {
                    // StripByteCounts missing or zero (old writers; libtiff recovers the same way):
                    // the chunk extends to the next chunk or the end of the file.
                    if (sorted == null) { sorted = (long[])offs.Clone(); Array.Sort(sorted); }
                    int k = Array.BinarySearch(sorted, o);
                    while (k >= 0 && k < sorted.Length && sorted[k] <= o) k++;
                    long end = k >= 0 && k < sorted.Length ? Math.Min(sorted[k], len) : len;
                    c = end - o;
                    if (Compression == 1) c = Math.Min(c, maxChunk);
                    cnts[i] = c;
                    if (c == 0) { empty++; continue; }
                }
                if (c > len - o) throw new ImageFormatException("TIFF: strip/tile data outside of the file");
                sum += c;
            }
            _offsets = offs; _counts = cnts;

            // A header can claim any size. Refuse images that cannot possibly come out of the stored bytes
            // before allocating for them.
            double raw = Math.Ceiling((double)Width * _pixelBits / 8) * Height; // double: a lying header may overflow long
            if (_oldJpeg)
            {
                // old-style JPEG may keep the whole stream outside of the strips; checked when the stream is located
            }
            else if (IsCcitt)
            {
                // every coded row needs at least one bit
                if (Height > sum * 8 + (long)empty * ChunkH) throw new ImageFormatException("TIFF: image size inconsistent with the data size");
            }
            else
            {
                long ratio = Compression switch { 1 => 1, 32773 => 64, 8 or 32946 => 1040, 50000 or 34925 => 1 << 17, _ => 4096 };
                long allowed = sum > long.MaxValue / (1 << 18) ? long.MaxValue : sum * ratio + 1024;
                if (empty > 0) allowed += (long)Math.Min((double)empty * maxChunk, 256L << 20);
                if (Kind == TiffChunkKind.YccPacked || YccPlanar) raw /= 3; // at least one luma byte per pixel
                if (raw > allowed) throw new ImageFormatException("TIFF: image size inconsistent with the data size");
            }

            if (Photometric == 3)
            {
                var cm = File.Integers(e, 320);
                if (cm == null || cm.Length < 3 << Bits) throw new ImageFormatException("TIFF: missing or short ColorMap");
                _colorMap = Array.ConvertAll(cm, x => (ushort)x);
            }
            if (Compression == 7) _jpegTables = File.Bytes(e, 347) ?? Array.Empty<byte>();
            if (_oldJpeg) _ojpeg = new TiffOldJpeg(this, offs, cnts!);
            if (IsCcitt) _t4Options = File.Integer(e, Compression == 3 ? 292 : 293, 0);
            if (Photometric == 6 && (Kind == TiffChunkKind.YccPacked || YccPlanar)) BuildYccTables();
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
                if (YccPlanar) YccInPlace(_dst, (long)rw * rh);
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

        // ------------------------------------------------------------------------------------------ chunks

        void DecodeChunk(int index)
        {
            int plane = index / ChunksPerPlane, ci = index % ChunksPerPlane;
            int x0 = ci % Across * ChunkW, y0 = ci / Across * ChunkH;
            int rows = Tiled ? ChunkH : Math.Min(ChunkH, Height - y0);
            int ix0 = Math.Max(x0, _rx), ix1 = (int)Math.Min(Math.Min((long)x0 + ChunkW, Width), (long)_rx + _rw);
            int iy0 = Math.Max(y0, _ry), iy1 = (int)Math.Min(Math.Min((long)y0 + rows, Height), (long)_ry + _rh);
            if (ix0 >= ix1 || iy0 >= iy1) return;
            long count = _counts[index], offset = _offsets[index];
            if (count == 0 && !_oldJpeg) return; // sparse chunk: stays zero
            int ps = PlaneSamples;
            // the chroma planes of planar YCbCr are stored subsampled
            int sh = 1, sv = 1;
            if (YccPlanar && plane > 0) { sh = SubH; sv = SubV; }
            int cw = (ChunkW + sh - 1) / sh, crows = (rows + sv - 1) / sv;
            // stored rows the region needs: [r0, r1)
            int r0 = (iy0 - y0) / sv, r1 = (iy1 - y0 + sv - 1) / sv;
            var g = new Geometry { Plane = plane, X0 = x0, Y0 = y0, Ix0 = ix0, Ix1 = ix1, Iy0 = iy0, Iy1 = iy1, Sh = sh, Sv = sv, Ps = ps };

            switch (Kind)
            {
                case TiffChunkKind.Samples:
                case TiffChunkKind.Packed:
                case TiffChunkKind.Float24:
                {
                    long rowBytes = RowBytes(cw, plane);
                    bool fullRows = Kind == TiffChunkKind.Samples && sh == 1 && !Tiled && Planar == 1 && _rx == 0 && _rw == Width;
                    if (Compression == 1)
                    {
                        // uncompressed: only the rows the region needs are read at all
                        long skip = r0 * rowBytes, size = (r1 - r0) * rowBytes;
                        if (count < skip + size) throw new ImageFormatException("TIFF: strip/tile data is too short");
                        if (fullRows)
                        {
                            byte* d = _dst + (iy0 - _ry) * _dstRowBytes;
                            ReadRaw(offset + skip, d, size);
                            Unpredict(d, r1 - r0, rowBytes, cw, ps);
                            return;
                        }
                        byte* t = (byte*)NativeMemory.Alloc((nuint)size);
                        try
                        {
                            ReadRaw(offset + skip, t, size);
                            Unpredict(t, r1 - r0, rowBytes, cw, ps);
                            g.BufRow0 = r0;
                            Transfer(t, rowBytes, g);
                        }
                        finally { NativeMemory.Free(t); }
                        return;
                    }
                    // compressed: decode from the top of the chunk, but no further than the last needed row
                    long need = r1 * rowBytes;
                    if (fullRows && y0 >= _ry)
                    {
                        byte* d = _dst + (y0 - _ry) * _dstRowBytes;
                        Decompress(offset, count, d, need, rowBytes);
                        Unpredict(d, r1, rowBytes, cw, ps);
                        return;
                    }
                    byte* b = (byte*)NativeMemory.Alloc((nuint)need);
                    try
                    {
                        Decompress(offset, count, b, need, rowBytes);
                        Unpredict(b, r1, rowBytes, cw, ps);
                        Transfer(b, rowBytes, g);
                    }
                    finally { NativeMemory.Free(b); }
                    return;
                }
                case TiffChunkKind.YccPacked:
                {
                    int blockRows = (iy1 - y0 + SubV - 1) / SubV;
                    int decoded = Math.Min(rows, blockRows * SubV);
                    long need = YccBlockRowBytes * blockRows;
                    byte* b = (byte*)NativeMemory.Alloc((nuint)(need + (long)ChunkW * decoded * 3));
                    try
                    {
                        byte* rgb = b + need;
                        Decompress(offset, count, b, need, YccBlockRowBytes);
                        YccToRgb(b, rgb, decoded);
                        g.Ps = 3;
                        Transfer(rgb, (long)ChunkW * 3, g, TiffChunkKind.Samples);
                    }
                    finally { NativeMemory.Free(b); }
                    return;
                }
                case TiffChunkKind.Jpeg:
                {
                    long size = (long)cw * crows * ps * Bps;
                    byte[]? srcRent = null;
                    byte* b = (byte*)NativeMemory.Alloc((nuint)size);
                    try
                    {
                        ReadOnlySpan<byte> src = _oldJpeg ? _ojpeg!.BuildStream()
                            : count > int.MaxValue ? throw new ImageNotSupportedException("TIFF: strips or tiles larger than 2 GB are not supported with this compression")
                            : File.Src.Get(offset, (int)count, ref srcRent);
                        bool ycc = Photometric == 6 && !YccPlanar;
                        ReadOnlySpan<byte> tables = _oldJpeg ? default : _jpegTables;
                        if (Bps == 1) TiffJpegBridge.Decode8(tables, src, cw, crows, ps, ycc, new Span<byte>(b, (int)size));
                        else TiffJpegBridge.Decode16(tables, src, cw, crows, ps, ycc, new Span<ushort>(b, (int)(size / 2)));
                        Transfer(b, (long)cw * ps * Bps, g, TiffChunkKind.Samples);
                    }
                    finally
                    {
                        NativeMemory.Free(b);
                        if (srcRent != null) ArrayPool<byte>.Shared.Return(srcRent);
                    }
                    return;
                }
                case TiffChunkKind.Webp:
                {
                    if (count > int.MaxValue) throw new ImageNotSupportedException("TIFF: strips or tiles larger than 2 GB are not supported with this compression");
                    byte[]? srcRent = null;
                    try
                    {
                        var img = Webp.Decode(File.Src.Get(offset, (int)count, ref srcRent));
                        if (img.SampleType != SampleType.UInt8 || img.Width < ChunkW || img.Height < rows || img.Channels < 3)
                            throw new ImageFormatException("TIFF: WebP strip/tile does not match the image layout");
                        var px = img.GetData<byte>();
                        int ic = img.Channels;
                        long size = (long)ChunkW * rows * Spp;
                        byte* b = (byte*)NativeMemory.Alloc((nuint)size);
                        try
                        {
                            for (int y = 0; y < rows; y++)
                            {
                                byte* d = b + (long)y * ChunkW * Spp;
                                int si = y * img.Width * ic;
                                for (int x = 0; x < ChunkW; x++, si += ic, d += Spp)
                                {
                                    d[0] = px[si]; d[1] = px[si + 1]; d[2] = px[si + 2];
                                    if (Spp == 4) d[3] = ic == 4 ? px[si + 3] : (byte)255;
                                }
                            }
                            Transfer(b, (long)ChunkW * Spp, g, TiffChunkKind.Samples);
                        }
                        finally { NativeMemory.Free(b); }
                    }
                    finally
                    {
                        if (srcRent != null) ArrayPool<byte>.Shared.Return(srcRent);
                    }
                    return;
                }
                case TiffChunkKind.LogLuv:
                {
                    if (count > int.MaxValue) throw new ImageNotSupportedException("TIFF: strips or tiles larger than 2 GB are not supported with this compression");
                    int bpp = Photometric == 32845 ? 4 : 2, rc = RawChannels;
                    int need = iy1 - y0;
                    long enc = (long)ChunkW * need * bpp;
                    byte[]? srcRent = null;
                    byte* b = (byte*)NativeMemory.Alloc((nuint)(enc + (long)ChunkW * need * rc * 4));
                    try
                    {
                        TiffSgiLog.Decode(File.Src.Get(offset, (int)count, ref srcRent), b, ChunkW, need, bpp);
                        float* f = (float*)(b + enc);
                        long pixels = (long)ChunkW * need;
                        if (bpp == 2)
                            for (long i = 0; i < pixels; i++) f[i] = TiffSgiLog.LogL16ToY(b[i * 2] << 8 | b[i * 2 + 1]);
                        else
                            for (long i = 0; i < pixels; i++)
                            {
                                uint p = (uint)(b[i * 4] << 24 | b[i * 4 + 1] << 16 | b[i * 4 + 2] << 8 | b[i * 4 + 3]);
                                TiffSgiLog.LogLuv32ToXyz(p, out float x, out float y, out float z);
                                if (ConvertToRgb) TiffColor.XyzToLinearRgb(ref x, ref y, ref z);
                                f[i * 3] = x; f[i * 3 + 1] = y; f[i * 3 + 2] = z;
                            }
                        g.Ps = rc;
                        Transfer((byte*)f, (long)ChunkW * rc * 4, g, TiffChunkKind.Samples);
                    }
                    finally
                    {
                        NativeMemory.Free(b);
                        if (srcRent != null) ArrayPool<byte>.Shared.Return(srcRent);
                    }
                    return;
                }
            }
        }

        /// <summary>Reads <paramref name="size"/> bytes (possibly more than 2 GB) of uncompressed data and applies FillOrder 2.</summary>
        void ReadRaw(long offset, byte* dst, long size)
        {
            for (long done = 0; done < size;)
            {
                int n = (int)Math.Min(size - done, 1 << 28);
                var d = new Span<byte>(dst + done, n);
                File.Src.Read(offset + done, d);
                if (FillOrder == 2) TiffPredict.ReverseBits(d, d);
                done += n;
            }
        }

        /// <summary>Decompresses the first <paramref name="expected"/> bytes of a chunk.</summary>
        void Decompress(long offset, long count, byte* dst, long expected, long rowBytes)
        {
            if (Compression == 1)
            {
                if (count < expected) throw new ImageFormatException("TIFF: strip/tile data is too short");
                ReadRaw(offset, dst, expected);
                return;
            }
            if (count > int.MaxValue) throw new ImageNotSupportedException("TIFF: compressed strips or tiles larger than 2 GB are not supported");
            byte[]? srcRent = null, rev = null;
            try
            {
                var src = File.Src.Get(offset, (int)count, ref srcRent);
                // FillOrder 2 only makes sense for bit oriented data; byte oriented deflate-like streams are left alone.
                if (FillOrder == 2 && (Compression is 5 or 32773 || IsCcitt))
                {
                    rev = ArrayPool<byte>.Shared.Rent((int)count);
                    TiffPredict.ReverseBits(src, rev);
                    src = rev.AsSpan(0, (int)count);
                }
                long got;
                switch (Compression)
                {
                    case 5: got = TiffLzw.Decode(src, dst, expected); break;
                    case 32773: got = TiffPackBits.Decode(src, dst, expected); break;
                    case 8: case 32946: case 50000: case 34925:
                        if (expected > int.MaxValue)
                            throw new ImageNotSupportedException("TIFF: strips or tiles larger than 2 GB are not supported with this compression");
                        var d = new Span<byte>(dst, (int)expected);
                        try
                        {
                            got = Compression == 50000 ? Zstd.Decompress(src, d) : Compression == 34925 ? Xz.Decompress(src, d) : Zlib.Decompress(src, d);
                        }
                        catch (Exception ex) when (ex is InvalidDataException || ex is IOException)
                        {
                            throw new ImageFormatException("TIFF: corrupt deflate data", ex);
                        }
                        break;
                    case 2: case 3: case 4:
                        if (expected > int.MaxValue)
                            throw new ImageNotSupportedException("TIFF: strips or tiles larger than 2 GB are not supported with this compression");
                        TiffCcitt.Decode(src, dst, ChunkW, (int)(expected / Math.Max(1, rowBytes)), Compression, _t4Options);
                        got = expected;
                        break;
                    default: throw new ImageNotSupportedException($"TIFF: compression {Compression} is not supported");
                }
                if (got < expected) throw new ImageFormatException("TIFF: compressed strip/tile data is truncated");
            }
            finally
            {
                if (srcRent != null) ArrayPool<byte>.Shared.Return(srcRent);
                if (rev != null) ArrayPool<byte>.Shared.Return(rev);
            }
        }

        /// <summary>Brings decompressed rows into machine byte order and undoes the predictor.</summary>
        void Unpredict(byte* b, int rows, long rowBytes, int cw, int ps)
        {
            int samples = cw * ps;
            if (Kind == TiffChunkKind.Packed) return;
            int width = Kind == TiffChunkKind.Float24 ? 3 : Bps;
            if (Predictor == 3)
            {
                byte* t = (byte*)NativeMemory.Alloc((nuint)rowBytes);
                try
                {
                    for (int y = 0; y < rows; y++) TiffPredict.UndoFloat(b + y * rowBytes, samples, ps, width, t);
                }
                finally { NativeMemory.Free(t); }
                return;
            }
            if (Kind == TiffChunkKind.Float24) return; // converted from the file byte order in Transfer
            if (File.BigEndian && Bps > 1) TiffPredict.SwapBytes(b, rowBytes * rows, Bps);
            if (Predictor == 2)
                for (int y = 0; y < rows; y++) TiffPredict.UndoHorizontal(b + y * rowBytes, samples, ps, Bps);
        }

        struct Geometry
        {
            public int Plane, X0, Y0, Ix0, Ix1, Iy0, Iy1, Sh, Sv, Ps;
            /// <summary>Stored row of the chunk that row 0 of the buffer holds.</summary>
            public int BufRow0;
        }

        void Transfer(byte* buf, long rowBytes, in Geometry g) => Transfer(buf, rowBytes, g, Kind);

        /// <summary>Copies the part of a decoded chunk that lies inside the requested region into the raw image.</summary>
        void Transfer(byte* buf, long rowBytes, in Geometry g, TiffChunkKind kind)
        {
            int n = g.Ix1 - g.Ix0, sx = g.Ix0 - g.X0, rc = RawChannels, bps = Bps, ps = g.Ps, plane = g.Plane;
            for (int y = g.Iy0; y < g.Iy1; y++)
            {
                byte* s = buf + ((y - g.Y0) / g.Sv - g.BufRow0) * rowBytes;
                byte* d = _dst + (y - _ry) * _dstRowBytes + (long)(g.Ix0 - _rx) * rc * bps;
                switch (kind)
                {
                    case TiffChunkKind.Samples:
                        if (g.Sh > 1 || g.Sv > 1)
                        {
                            // subsampled chroma plane (8 bit): replicate
                            d += plane;
                            for (int i = 0; i < n; i++) d[(long)i * rc] = s[(sx + i) / g.Sh];
                        }
                        else if (ps == rc)
                        {
                            long len = (long)n * ps * bps;
                            Buffer.MemoryCopy(s + (long)sx * ps * bps, d, len, len);
                        }
                        else
                        {
                            s += (long)sx * bps; d += plane * bps;
                            long step = (long)rc * bps;
                            switch (bps)
                            {
                                case 1: for (int i = 0; i < n; i++) d[i * step] = s[i]; break;
                                case 2: for (int i = 0; i < n; i++) *(ushort*)(d + i * step) = ((ushort*)s)[i]; break;
                                case 4: for (int i = 0; i < n; i++) *(uint*)(d + i * step) = ((uint*)s)[i]; break;
                                default: for (int i = 0; i < n; i++) *(ulong*)(d + i * step) = ((ulong*)s)[i]; break;
                            }
                        }
                        break;
                    case TiffChunkKind.Float24:
                    {
                        bool be = Predictor != 3 && File.BigEndian;
                        s += (long)sx * ps * 3;
                        float* f = (float*)d;
                        long total = (long)n * ps;
                        long step = ps == rc ? 1 : rc;
                        if (ps != rc) f += plane;
                        for (long i = 0; i < total; i++, s += 3)
                            f[i * step] = Float24ToSingle(be ? (uint)(s[0] << 16 | s[1] << 8 | s[2]) : (uint)(s[2] << 16 | s[1] << 8 | s[0]));
                        break;
                    }
                    default:
                        UnpackRow(s, d, sx, n, ps, plane);
                        break;
                }
            }
        }

        /// <summary>24-bit float: sign, 7 exponent bits (bias 63), 16 mantissa bits.</summary>
        static float Float24ToSingle(uint v)
        {
            uint sign = v >> 23 & 1, e = v >> 16 & 0x7F, m = v & 0xFFFF;
            if (e == 0)
            {
                float sub = MathF.ScaleB(m, -78); // subnormal: m * 2^(1 - 63 - 16)
                return sign != 0 ? -sub : sub;
            }
            uint bits = sign << 31 | (e == 127 ? 255u : e - 63 + 127) << 23 | m << 7;
            return BitConverter.UInt32BitsToSingle(bits);
        }

        /// <summary>Unpacks n pixels of bit packed samples starting at pixel sx of a stored row.</summary>
        void UnpackRow(byte* s, byte* d, int sx, int n, int ps, int plane)
        {
            int rc = RawChannels, bps = Bps;
            bool contig = ps == rc;
            if (contig && UniformBits && Bits == 1 && rc == 1)
            {
                // bilevel rows: whole bytes at a time
                byte on = (byte)(_scale[0] ? 255 : 1);
                int i = 0;
                long bit = sx;
                for (; (bit & 7) != 0 && i < n; i++, bit++) d[i] = (s[bit >> 3] >> (7 - (int)(bit & 7)) & 1) != 0 ? on : (byte)0;
                byte* sp = s + (bit >> 3);
                for (; i + 8 <= n; i += 8)
                {
                    int v = *sp++;
                    d[i] = (v & 0x80) != 0 ? on : (byte)0; d[i + 1] = (v & 0x40) != 0 ? on : (byte)0;
                    d[i + 2] = (v & 0x20) != 0 ? on : (byte)0; d[i + 3] = (v & 0x10) != 0 ? on : (byte)0;
                    d[i + 4] = (v & 0x08) != 0 ? on : (byte)0; d[i + 5] = (v & 0x04) != 0 ? on : (byte)0;
                    d[i + 6] = (v & 0x02) != 0 ? on : (byte)0; d[i + 7] = (v & 0x01) != 0 ? on : (byte)0;
                }
                if (i < n)
                {
                    int v = *sp;
                    for (int k = 0; i < n; i++, k++) d[i] = (v & (0x80 >> k)) != 0 ? on : (byte)0;
                }
                return;
            }
            bool signed = Format == 2;
            long pos = contig ? (long)sx * _pixelBits : (long)sx * BitsPer[plane];
            for (int x = 0; x < n; x++)
                for (int c = 0; c < ps; c++)
                {
                    int ch = contig ? c : plane;
                    int b = BitsPer[ch];
                    // gather the bytes the sample touches (at most 5 for 32 bits at an odd offset)
                    long first = pos >> 3;
                    int end = (int)(pos & 7) + b, nb = (end + 7) >> 3;
                    ulong v = 0;
                    for (int k = 0; k < nb; k++) v = v << 8 | s[first + k];
                    v = v >> (nb * 8 - end) & _inMax[ch];
                    pos += b;
                    if (signed)
                    {
                        ulong sb = 1UL << (b - 1);
                        v = (v ^ sb) - sb; // sign extend
                    }
                    else if (_scale[ch]) v = (v * _outMax + _inMax[ch] / 2) / _inMax[ch];
                    byte* o = d + ((long)x * rc + ch) * bps;
                    switch (bps)
                    {
                        case 1: *o = (byte)v; break;
                        case 2: *(ushort*)o = (ushort)v; break;
                        default: *(uint*)o = (uint)v; break;
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

        /// <summary>Converts interleaved full resolution YCbCr pixels to RGB in place.</summary>
        void YccInPlace(byte* p, long pixels)
        {
            float[] ty = _yccY!, crR = _yccCrR!, cbB = _yccCbB!, crG = _yccCrG!, cbG = _yccCbG!;
            for (long i = 0; i < pixels; i++, p += 3)
            {
                float l = ty[p[0]];
                int cb = p[1], cr = p[2];
                p[0] = Clamp(l + crR[cr]); p[1] = Clamp(l + crG[cr] + cbG[cb]); p[2] = Clamp(l + cbB[cb]);
            }
        }

        static byte Clamp(float v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5f);
    }
}
