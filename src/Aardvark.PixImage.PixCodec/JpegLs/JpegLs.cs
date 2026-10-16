using System;
using System.IO;

namespace Aardvark.ImageCodecs
{
    /// <summary>Interleave mode of a JPEG-LS scan.</summary>
    public enum JpegLsInterleave
    {
        /// <summary>One scan per component.</summary>
        None = 0,
        /// <summary>One scan, a line of each component in turn.</summary>
        Line = 1,
        /// <summary>One scan, the components of each pixel in turn.</summary>
        Sample = 2,
    }

    /// <summary>The reversible colour transforms of HP's JPEG-LS implementation (APP8 "mrfx"), also used by CharLS.</summary>
    public enum JpegLsColorTransform
    {
        None = 0,
        /// <summary>R - G, G, B - G.</summary>
        Hp1 = 1,
        /// <summary>R - G, G, B - (R + G) / 2.</summary>
        Hp2 = 2,
        /// <summary>G + (R' + B') / 4, B' = B - G, R' = R - G (stored in that order).</summary>
        Hp3 = 3,
    }

    /// <summary>JPEG-LS preset coding parameters (LSE segment id 1); zero entries take the defaults of T.87 C.2.4.1.1.</summary>
    public sealed record JpegLsPresetParameters(int MaxVal = 0, int T1 = 0, int T2 = 0, int T3 = 0, int Reset = 0);

    public sealed class JpegLsDecodeOptions
    {
        /// <summary>Turn the image upright according to an EXIF orientation (APP1), if the file has one.</summary>
        public bool ApplyOrientation { get; set; } = true;
        /// <summary>Undo an HP colour transform (APP8 "mrfx"). Off returns the components as coded.</summary>
        public bool ApplyColorTransform { get; set; } = true;
        /// <summary>Replace sample values by their mapping table (palette) entries. Off returns the indices.</summary>
        public bool ApplyMappingTables { get; set; } = true;
        /// <summary>Upper bound for the image in pixels; larger files are rejected with <see cref="ImageNotSupportedException"/>.</summary>
        public long MaxPixels { get; set; } = 1L << 30;

        internal static readonly JpegLsDecodeOptions Default = new();
    }

    public sealed class JpegLsEncodeOptions
    {
        /// <summary>NEAR: 0 is lossless; n &gt; 0 bounds every sample's error by n (near-lossless).</summary>
        public int Near { get; set; }
        /// <summary>
        /// Interleave mode for images with several channels (one channel always uses None). Null (the default) takes Sample
        /// for 3 and 4 channels, as CharLS does, and None otherwise (CharLS reads interleaved scans only with 3 or 4
        /// components).
        /// </summary>
        public JpegLsInterleave? Interleave { get; set; }
        /// <summary>Bits per sample 2..16; 0 takes 8 for UInt8 and 16 for UInt16 images. All samples must fit.</summary>
        public int BitsPerSample { get; set; }
        /// <summary>HP colour transform of the first three channels (lossless only makes sense with Near = 0).</summary>
        public JpegLsColorTransform ColorTransform { get; set; }
        /// <summary>Custom preset coding parameters; null uses the defaults (no LSE segment is written then).</summary>
        public JpegLsPresetParameters? Preset { get; set; }
        /// <summary>Write a SPIFF header (T.84 Annex F) with colour space and resolution.</summary>
        public bool Spiff { get; set; }
        /// <summary>Restart marker every this many lines (0 = none).</summary>
        public int RestartInterval { get; set; }
    }

    /// <summary>
    /// JPEG-LS (ITU-T T.87 / ISO 14495-1), lossless and near-lossless.
    /// <para>Decoding: 2..16 bit samples, 1..255 components, all interleave modes, NEAR &gt; 0, preset coding parameters
    /// (LSE 1), mapping tables (LSE 2 / 3: palettes), oversize dimensions (LSE 4), restart markers, DNL, SPIFF headers
    /// (resolution), HP colour transforms (APP8 "mrfx", HP1..HP3), subsampled components (upsampled by replication to the
    /// full size). Palettes with 3 / 4 byte entries on a single component give Rgb / Rgba. Samples are returned as coded:
    /// UInt8 up to 8 bits, else UInt16 (e.g. 0..4095 for 12 bits). Files that end early decode as far as the data goes.
    /// Not supported: the ISO 14495-2 extensions (SOF57, LSE ids 5 and up) and point transforms. JPEG-LS has no TIFF
    /// compression code.</para>
    /// <para>Encoding: UInt8 / UInt16 images with 1..255 channels, lossless or near-lossless, any interleave mode, any bit
    /// depth 2..16, preset parameters, HP colour transforms, restart intervals, optional SPIFF header. The output equals
    /// CharLS's byte for byte for the same parameters (including the LSE segment CharLS writes for more than 12 bits).
    /// A MAXVAL below 2^bits - 1 is coded as T.87 says (RANGE and the prediction clamp follow MAXVAL); CharLS 2.4 reads
    /// such files differently.</para>
    /// </summary>
    public static unsafe class JpegLs
    {
        /// <summary>
        /// Whether the data is a JPEG-LS file: an SOI marker followed by marker segments up to a JPEG-LS frame header (SOF55,
        /// or SOF57 of ISO 14495-2). False for other JPEG frames, and when the prefix ends before the frame header.
        /// </summary>
        public static bool IsMatch(ReadOnlySpan<byte> header)
        {
            if (header.Length < 4 || header[0] != 0xFF || header[1] != 0xD8 || header[2] != 0xFF) return false;
            int p = 2;
            while (p + 4 <= header.Length)
            {
                if (header[p] != 0xFF) return false;
                int m = header[p + 1];
                if (m == 0xFF) { p++; continue; }
                if (m == 0xF7 || m == 0xF9) return true;
                if (m >= 0xC0 && m <= 0xCF && m != 0xC4 && m != 0xC8 && m != 0xCC) return false;
                if (m == 0xDA || m == 0xD9) return false;
                if (m == 0xD8 || m == 0x01 || (m >= 0xD0 && m <= 0xD7)) { p += 2; continue; }
                p += 2 + (header[p + 2] << 8 | header[p + 3]);
            }
            return false;
        }

        static JpegLsDecoder Open(byte* p, int length, bool headerOnly, long maxPixels)
        {
            var d = new JpegLsDecoder(p, length);
            try { d.Parse(headerOnly, maxPixels); }
            catch { d.Release(); throw; }
            return d;
        }

        /// <summary>Channels, sample type, colour model and stored bits of what Decode returns.</summary>
        static (int ch, SampleType type, ColorModel model, int bits) Plan(JpegLsDecoder d, JpegLsDecodeOptions o)
        {
            int ch = d.Nf;
            bool wide = d.Wide;
            int bits = d.Bits;
            if (o.ApplyMappingTables && Array.Exists(d.CompTable, t => t != 0))
            {
                int maxWt = 0;
                foreach (var t in d.CompTable) if (t != 0) maxWt = Math.Max(maxWt, d.Tables[t].wt);
                if (maxWt >= 3)
                {
                    if (d.Nf != 1) throw new ImageNotSupportedException("JPEG-LS: mapping tables with 3 or 4 byte entries on several components");
                    ch = maxWt; wide = false; bits = 8;
                }
                else
                {
                    bool unmappedWide = d.Wide && Array.Exists(d.CompTable, t => t == 0);
                    wide = maxWt == 2 || unmappedWide;
                    bits = wide ? 16 : 8;
                }
            }
            var model = RawImage.DefaultColorModel(ch);
            if (ch == d.Nf && d.SpiffColorSpace >= 0)
            {
                // SPIFF colour spaces: 8 gray, 10 RGB; YCbCr (1, 3, 4), CMY(K), YCCK, Lab are returned as stored
                if (d.SpiffColorSpace is 1 or 3 or 4 or 9 or 11 or 12 or 13 or 14) model = ColorModel.Other;
            }
            return (ch, wide ? SampleType.UInt16 : SampleType.UInt8, model, bits);
        }

        /// <summary>Reads header level information without decoding pixel data (what <see cref="Decode"/> returns).</summary>
        public static ImageInfo GetInfo(ReadOnlySpan<byte> data) => GetInfo(data, null);

        /// <inheritdoc cref="GetInfo(ReadOnlySpan{byte})"/>
        public static ImageInfo GetInfo(ReadOnlySpan<byte> data, JpegLsDecodeOptions? options)
        {
            options ??= JpegLsDecodeOptions.Default;
            fixed (byte* p = data)
            {
                var d = Open(p, data.Length, true, long.MaxValue);
                var (ch, type, model, bits) = Plan(d, options);
                var info = new ImageInfo(ImageFormat.JpegLs, d.Width, d.Height, ch, type, model)
                {
                    BitsPerSample = bits,
                    DpiX = d.DpiX, DpiY = d.DpiY,
                    HasIccProfile = d.Icc != null,
                    Orientation = d.Orientation,
                };
                return options.ApplyOrientation ? ExifTags.Upright(info) : info;
            }
        }

        /// <summary>Decodes a JPEG-LS file held in memory (see the class summary for what is supported).</summary>
        public static RawImage Decode(ReadOnlySpan<byte> data, JpegLsDecodeOptions? options = null)
        {
            options ??= JpegLsDecodeOptions.Default;
            fixed (byte* p = data)
            {
                var d = Open(p, data.Length, false, options.MaxPixels);
                var pixels = d.TakeOutput();
                var (ch, type, model, _) = Plan(d, options);
                if (options.ApplyMappingTables && Array.Exists(d.CompTable, t => t != 0)) pixels = Map(d, pixels, ch, type);
                else if (options.ApplyColorTransform && d.Transform != 0 && d.Nf >= 3)
                {
                    if (pixels is byte[] b) JpegLsColor.Inverse(b, d.Nf, d.Bits, (JpegLsColorTransform)d.Transform);
                    else JpegLsColor.Inverse((ushort[])pixels, d.Nf, d.Bits, (JpegLsColorTransform)d.Transform);
                }
                ImageMetadata? meta = null;
                if (d.Icc != null || d.Exif != null || d.DpiX > 0)
                    meta = new ImageMetadata { IccProfile = d.Icc, Exif = d.Exif, DpiX = d.DpiX, DpiY = d.DpiY, Orientation = d.Orientation };
                var img = new RawImage(d.Width, d.Height, model, pixels, ch, meta);
                return options.ApplyOrientation && d.Orientation > 1 ? ImageOps.ApplyOrientation(img) : img;
            }
        }

        static Array Map(JpegLsDecoder d, Array src, int ch, SampleType type)
        {
            int nf = d.Nf;
            long n = (long)d.Width * d.Height;
            if (ch != nf)
            {
                // one component of indices into entries of 3 or 4 bytes: R, G, B (, A)
                var (wt, data) = d.Tables[d.CompTable[0]];
                int entries = data.Count / wt;
                var tab = data.ToArray();
                var o = new byte[n * wt];
                for (long i = 0; i < n; i++)
                {
                    int idx = src is byte[] b ? b[i] : ((ushort[])src)[i];
                    if (idx >= entries) throw new ImageFormatException($"JPEG-LS: sample {idx} outside the mapping table ({entries} entries)");
                    for (int k = 0; k < wt; k++) o[i * wt + k] = tab[idx * wt + k];
                }
                return o;
            }
            var res = RawImage.CreateArray(type, checked((int)(n * nf)));
            for (int c = 0; c < nf; c++)
            {
                int tid = d.CompTable[c];
                byte[]? tab = null; int wt = 0, entries = 0;
                if (tid != 0) { var t = d.Tables[tid]; wt = t.wt; tab = t.data.ToArray(); entries = tab.Length / wt; }
                for (long i = c; i < n * nf; i += nf)
                {
                    int v = src is byte[] b ? b[i] : ((ushort[])src)[i];
                    if (tab != null)
                    {
                        if (v >= entries) throw new ImageFormatException($"JPEG-LS: sample {v} outside the mapping table ({entries} entries)");
                        v = wt == 1 ? tab[v] : tab[2 * v] << 8 | tab[2 * v + 1];
                    }
                    if (res is ushort[] r16) r16[i] = (ushort)v; else ((byte[])res)[i] = (byte)v;
                }
            }
            return res;
        }

        /// <summary>Encodes a UInt8 or UInt16 image (lossless by default).</summary>
        public static byte[] Encode(RawImage image, JpegLsEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            return JpegLsEncoder.Encode(image, options ?? new JpegLsEncodeOptions());
        }

        public static void Encode(RawImage image, Stream output, JpegLsEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(output);
            output.Write(Encode(image, options));
        }
    }
}
