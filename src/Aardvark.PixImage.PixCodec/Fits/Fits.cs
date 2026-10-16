using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Aardvark.ImageCodecs.Compression;

namespace Aardvark.ImageCodecs
{
    public sealed class FitsDecodeOptions : VolumeDecodeOptions
    {
        /// <summary>
        /// Index of the HDU to decode (0 = primary), or -1 (the default) for the first HDU holding an image (the primary array
        /// if it has data, else the first IMAGE extension or tile compressed image).
        /// </summary>
        public int Hdu { get; set; } = -1;

        /// <summary>
        /// FITS stores the first row (y = 1) at the bottom, as ds9 shows it. By default the rows are reversed so that the
        /// decoded image has the top row first like every other image here; false keeps the file's row order (row 0 = FITS y = 1).
        /// </summary>
        public bool ApplyOrientation { get; set; } = true;

        internal static readonly FitsDecodeOptions DefaultFits = new();

        internal static FitsDecodeOptions From(VolumeDecodeOptions? o) => o as FitsDecodeOptions ?? (o == null ? DefaultFits
            : new FitsDecodeOptions { Scaling = o.Scaling, MaxSamples = o.MaxSamples, DataDirectory = o.DataDirectory, OpenDataFile = o.OpenDataFile });
    }

    /// <summary>Tile compression of written FITS images.</summary>
    public enum FitsCompression
    {
        None,
        /// <summary>RICE_1 (lossless; 8, 16 and 32 bit integer images, including the unsigned conventions).</summary>
        Rice,
        /// <summary>GZIP_1 (lossless, every BITPIX).</summary>
        Gzip,
        /// <summary>GZIP_2: gzip of byte-shuffled samples (lossless, every BITPIX; better for float images).</summary>
        Gzip2,
    }

    public sealed class FitsEncodeOptions
    {
        public FitsCompression Compression { get; set; } = FitsCompression.None;
        /// <summary>
        /// Tile size (x, y) for compression; 0 means the default: whole rows (ZTILE1 = width, ZTILE2 = 1), as the tiled image
        /// convention suggests.
        /// </summary>
        public int TileWidth { get; set; }
        public int TileHeight { get; set; }
        /// <summary>The image's rows are written bottom-up (see <see cref="FitsDecodeOptions.ApplyOrientation"/>); false writes row 0 as FITS y = 1.</summary>
        public bool ApplyOrientation { get; set; } = true;
    }

    /// <summary>One header data unit of a FITS file.</summary>
    public sealed class FitsHdu
    {
        public int Index { get; internal set; }
        /// <summary>"PRIMARY", or the XTENSION value ("IMAGE", "BINTABLE", "TABLE", ...).</summary>
        public string Type { get; internal set; } = "";
        /// <summary>Whether this HDU holds an image: a primary array or IMAGE extension with data, or a tile compressed image (ZIMAGE = T).</summary>
        public bool IsImage { get; internal set; }
        public bool IsCompressed { get; internal set; }
        /// <summary>The image's axis sizes (NAXIS1, NAXIS2, ...; ZNAXISn for compressed images), empty for non-images.</summary>
        public int[] Axes { get; internal set; } = Array.Empty<int>();
        /// <summary>BITPIX of the image (ZBITPIX for compressed images).</summary>
        public int BitPix { get; internal set; }
        /// <summary>The header cards: keyword, value (strings without quotes, logicals T / F) and comment.</summary>
        public List<(string Key, string Value, string Comment)> Cards { get; } = new();
        internal long DataOffset, DataSize;

        /// <summary>The value of a keyword (last occurrence), null if missing.</summary>
        public string? Get(string key)
        {
            for (int i = Cards.Count - 1; i >= 0; i--) if (Cards[i].Key == key) return Cards[i].Value;
            return null;
        }

        internal double GetDouble(string key, double def)
            => Get(key) is { } v && VolumeIO.TryParseDouble(v, out var d) ? d : def;

        internal long GetLong(string key, long def)
        {
            var v = Get(key);
            if (v == null) return def;
            if (long.TryParse(v.Trim(), System.Globalization.NumberStyles.Integer, VolumeIO.Inv, out var l)) return l;
            if (VolumeIO.TryParseDouble(v, out var d) && Math.Floor(d) == d && Math.Abs(d) < 9e18) return (long)d;
            throw new ImageFormatException($"FITS: keyword {key} is not an integer");
        }

        internal bool GetBool(string key) => Get(key)?.Trim() == "T";
    }

    /// <summary>
    /// FITS (Flexible Image Transport System): the primary array, IMAGE extensions and tile compressed images (ZIMAGE binary
    /// tables: RICE_1, GZIP_1, GZIP_2, PLIO_1, NOCOMPRESS, with float quantization: NO_DITHER, SUBTRACTIVE_DITHER_1 / 2, and
    /// the GZIP_COMPRESSED_DATA / UNCOMPRESSED_DATA fallbacks; HCOMPRESS_1 is not supported), BITPIX 8, 16, 32, 64, -32,
    /// -64, BSCALE / BZERO (applied per <see cref="VolumeDecodeOptions.Scaling"/>: the unsigned conventions give UInt16 /
    /// UInt32 / UInt64 / Int8 exactly) and BLANK. Also gzipped files (.fits.gz).
    /// <para>
    /// Axes: NAXIS1 = x, NAXIS2 = y, NAXIS3 = z, further axes are frames. FITS puts y = 1 at the bottom (ds9 shows it so):
    /// decoded rows are reversed so that row 0 is the top row (<see cref="FitsDecodeOptions.ApplyOrientation"/>), slices
    /// stay in file order. The header cards are in <see cref="PhysicalMetadata.Header"/>; the linear part of the WCS (CRPIX,
    /// CRVAL, CDELT, PC / CD, CROTA2, CUNIT) becomes Origin / Spacing / Direction for the decoded (flipped) grid, Space
    /// "FITS-WCS". Celestial projections are not evaluated: use the raw keywords for that.
    /// </para>
    /// Writes a primary image of any BITPIX (unsigned types with the BZERO conventions, multi-channel images as an extra
    /// axis), optionally RICE_1 / GZIP_1 / GZIP_2 tile compressed (then as an extension after an empty primary HDU).
    /// </summary>
    public static class Fits
    {
        const string Fmt = "FITS";
        const int Block = 2880;

        public static bool IsMatch(ReadOnlySpan<byte> header)
        {
            if (Gzip.IsGzip(header))
            {
                Span<byte> h = stackalloc byte[80];
                int n;
                try { n = Gzip.Decompress(header, h); }
                catch (ImageFormatException) { return false; }
                return IsPlain(h.Slice(0, n));
            }
            return IsPlain(header);
        }

        static bool IsPlain(ReadOnlySpan<byte> h) => h.Length >= 30 && h.StartsWith("SIMPLE  ="u8) && h[29] == 'T';

        // ================================================================ headers

        static string Card(ReadOnlySpan<byte> c) => Encoding.Latin1.GetString(c);

        /// <summary>Parses the value and comment of a card (after "= ").</summary>
        static (string value, string comment, bool isString) ParseValue(string s)
        {
            int i = 0;
            while (i < s.Length && s[i] == ' ') i++;
            if (i < s.Length && s[i] == '\'')
            {
                var sb = new StringBuilder();
                i++;
                while (i < s.Length)
                {
                    if (s[i] == '\'')
                    {
                        if (i + 1 < s.Length && s[i + 1] == '\'') { sb.Append('\''); i += 2; continue; }
                        i++;
                        break;
                    }
                    sb.Append(s[i++]);
                }
                int sl = s.IndexOf('/', i);
                return (sb.ToString().TrimEnd(), sl >= 0 ? s.Substring(sl + 1).Trim() : "", true);
            }
            int slash = s.IndexOf('/', i);
            string v = (slash >= 0 ? s.Substring(i, slash - i) : s.Substring(i)).Trim();
            return (v, slash >= 0 ? s.Substring(slash + 1).Trim() : "", false);
        }

        /// <summary>Reads one header (up to END) from the stream; null at the end of the file.</summary>
        static List<(string, string, string)>? ReadHeader(Stream s, ref long pos, bool first)
        {
            var cards = new List<(string, string, string)>();
            var blk = new byte[Block];
            bool end = false, continued = false;
            int blocks = 0;
            while (!end)
            {
                int got = 0;
                while (got < Block)
                {
                    int r = s.Read(blk, got, Block - got);
                    if (r <= 0) break;
                    got += r;
                }
                if (got == 0 && blocks == 0 && !first) return null;
                if (got < Block)
                {
                    // trailing junk / padding shorter than a block after the last HDU
                    if (blocks == 0 && !first) return null;
                    throw new ImageFormatException("FITS: header ends early");
                }
                if (blocks == 0 && !first && blk.AsSpan().IndexOfAnyExcept((byte)0, (byte)' ') < 0) return null;
                pos += Block;
                blocks++;
                if (blocks > 1 << 16) throw new ImageFormatException("FITS: header too long");
                for (int c = 0; c < 36; c++)
                {
                    var raw = blk.AsSpan(c * 80, 80);
                    string card = Card(raw);
                    string key = card.Substring(0, 8).TrimEnd();
                    if (key == "END") { end = true; break; }
                    if (blocks == 1 && c == 0 && key != (first ? "SIMPLE" : "XTENSION"))
                        throw new ImageFormatException(first ? "FITS: no SIMPLE card" : "FITS: no XTENSION card");
                    if (key == "CONTINUE" && continued && cards.Count > 0)
                    {
                        var (cv, cc, _) = ParseValue(card.Substring(8));
                        var last = cards[^1];
                        var lv = last.Item2.EndsWith('&') ? last.Item2.Substring(0, last.Item2.Length - 1) : last.Item2;
                        cards[^1] = (last.Item1, lv + cv, last.Item3.Length > 0 ? last.Item3 + " " + cc : cc);
                        continued = cv.EndsWith('&');
                        continue;
                    }
                    continued = false;
                    if (card.Length >= 10 && card[8] == '=' && card[9] == ' ')
                    {
                        var (v, cm, str) = ParseValue(card.Substring(10));
                        cards.Add((key, v, cm));
                        continued = str && v.EndsWith('&');
                    }
                    else if (key == "HIERARCH")
                    {
                        int eq = card.IndexOf('=');
                        if (eq > 0) { var (v, cm, _) = ParseValue(card.Substring(eq + 1)); cards.Add((card.Substring(9, eq - 9).Trim(), v, cm)); }
                        else cards.Add((key, card.Substring(8).TrimEnd(), ""));
                    }
                    else if (key.Length > 0 || card.Substring(8).Trim().Length > 0)
                        cards.Add((key, card.Substring(8).TrimEnd(), ""));
                }
            }
            return cards;
        }

        /// <summary>Reads all HDU headers, seeking past the data.</summary>
        static List<FitsHdu> ReadHdus(Stream s, int stopAfter = int.MaxValue)
        {
            var list = new List<FitsHdu>();
            long pos = 0;
            while (list.Count <= stopAfter)
            {
                var cards = ReadHeader(s, ref pos, list.Count == 0);
                if (cards == null) break;
                var h = new FitsHdu { Index = list.Count };
                h.Cards.AddRange(cards);
                Describe(h);
                h.DataOffset = pos;
                list.Add(h);
                long skip = (h.DataSize + Block - 1) / Block * Block;
                if (s.CanSeek)
                {
                    if (pos + h.DataSize > s.Length)
                    {
                        // data truncated: keep the header (decoding will fail), stop here
                        break;
                    }
                    pos += skip;
                    if (pos >= s.Length) break;
                    s.Position = pos;
                }
                else
                {
                    try { VolumeIO.Skip(s, skip, Fmt); } catch (ImageFormatException) { break; }
                    pos += skip;
                }
            }
            if (list.Count == 0) throw new ImageFormatException("FITS: no header");
            return list;
        }

        static void Describe(FitsHdu h)
        {
            h.Type = h.Index == 0 ? "PRIMARY" : (h.Get("XTENSION") ?? "").Trim();
            long bitpix = h.GetLong("BITPIX", 0);
            if (bitpix is not (8 or 16 or 32 or 64 or -32 or -64)) throw new ImageFormatException($"FITS: bad BITPIX {bitpix}");
            long naxis = h.GetLong("NAXIS", -1);
            if (naxis < 0 || naxis > 999) throw new ImageFormatException("FITS: bad NAXIS");
            long n = naxis == 0 ? 0 : 1;
            var axes = new long[naxis];
            for (int i = 0; i < naxis; i++)
            {
                axes[i] = h.GetLong("NAXIS" + (i + 1), -1);
                if (axes[i] < 0) throw new ImageFormatException($"FITS: bad NAXIS{i + 1}");
                n = VolumeIO.Mul(n, axes[i], Fmt);
            }
            long pcount = h.GetLong("PCOUNT", 0), gcount = h.GetLong("GCOUNT", 1);
            bool groups = h.Index == 0 && naxis > 0 && axes[0] == 0 && h.GetBool("GROUPS");
            if (groups)
            {
                n = 1;
                for (int i = 1; i < naxis; i++) n = VolumeIO.Mul(n, axes[i], Fmt);
            }
            if (pcount < 0 || gcount < 0) throw new ImageFormatException("FITS: bad PCOUNT / GCOUNT");
            h.DataSize = VolumeIO.Mul(Math.Abs(bitpix) / 8, h.Index == 0 && !groups ? n : VolumeIO.Mul(gcount, pcount + n, Fmt), Fmt);
            h.BitPix = (int)bitpix;
            if ((h.Type == "PRIMARY" || h.Type == "IMAGE") && !groups && n > 0)
            {
                h.IsImage = true;
                h.Axes = Array.ConvertAll(axes, a => a > int.MaxValue ? throw new ImageNotSupportedException("FITS: axis too long") : (int)a);
            }
            else if (h.Type == "BINTABLE" && h.GetBool("ZIMAGE"))
            {
                h.IsImage = h.IsCompressed = true;
                h.BitPix = (int)h.GetLong("ZBITPIX", 0);
                if (h.BitPix is not (8 or 16 or 32 or 64 or -32 or -64)) throw new ImageFormatException("FITS: bad ZBITPIX");
                long zn = h.GetLong("ZNAXIS", 0);
                if (zn < 1 || zn > 999) throw new ImageFormatException("FITS: bad ZNAXIS");
                h.Axes = new int[zn];
                for (int i = 0; i < zn; i++)
                {
                    long a = h.GetLong("ZNAXIS" + (i + 1), -1);
                    if (a <= 0 || a > int.MaxValue) throw new ImageFormatException($"FITS: bad ZNAXIS{i + 1}");
                    h.Axes[i] = (int)a;
                }
            }
        }

        static T WithFits<T>(ReadOnlySpan<byte> data, Func<Stream, T> f)
        {
            if (Gzip.IsGzip(data))
            {
                var plain = Gzip.Decompress(data);
                return VolumeIO.WithStream(plain, f);
            }
            return VolumeIO.WithStream(data, f);
        }

        /// <summary>Every HDU of the file with its header cards (data is not read).</summary>
        public static IReadOnlyList<FitsHdu> ReadHdus(ReadOnlySpan<byte> data) => WithFits(data, s => ReadHdus(s));

        static FitsHdu Select(List<FitsHdu> hdus, int index)
        {
            if (index >= 0)
            {
                if (index >= hdus.Count) throw new ArgumentOutOfRangeException(nameof(index), "FITS: no such HDU");
                if (!hdus[index].IsImage) throw new ImageNotSupportedException($"FITS: HDU {index} ({hdus[index].Type}) is not an image");
                return hdus[index];
            }
            foreach (var h in hdus) if (h.IsImage) return h;
            throw new ImageNotSupportedException("FITS: the file holds no image");
        }

        // ================================================================ info

        static SampleType StoredType(int bitpix) => bitpix switch
        {
            8 => SampleType.UInt8, 16 => SampleType.Int16, 32 => SampleType.Int32, 64 => SampleType.Int64,
            -32 => SampleType.Float32, -64 => SampleType.Float64, _ => throw new ImageFormatException("FITS: bad BITPIX"),
        };

        static (double slope, double zero) Scaling(FitsHdu h) => (h.GetDouble("BSCALE", 1), h.GetDouble("BZERO", 0));

        static int[] Grid(FitsHdu h)
        {
            var g = new List<int>(h.Axes);
            while (g.Count > 2 && g[^1] == 1) g.RemoveAt(g.Count - 1);
            if (g.Count == 1) g.Add(1);
            return g.ToArray();
        }

        static VolumeInfo Info(FitsHdu h, FitsDecodeOptions o)
        {
            var stored = StoredType(h.BitPix);
            var (slope, zero) = Scaling(h);
            var t = VolumeIO.ScaledType(stored, slope, zero, o.Scaling);
            bool quantized = h.IsCompressed && h.BitPix < 0 && HasQuantization(h);
            return new VolumeInfo
            {
                Format = ImageFormat.Fits, Sizes = Grid(h), Channels = 1, Color = ColorModel.Gray, StoredType = stored, SampleType = t,
                Encoding = h.IsCompressed ? (h.Get("ZCMPTYPE") ?? "").Trim() + (quantized ? "+quantized" : "") : "raw",
                Metadata = Metadata(h, o.ApplyOrientation),
            };
        }

        static bool HasQuantization(FitsHdu h)
        {
            for (int i = 1; i <= h.GetLong("TFIELDS", 0); i++)
                if ((h.Get("TTYPE" + i) ?? "").Trim().Equals("ZSCALE", StringComparison.OrdinalIgnoreCase)) return true;
            return h.Get("ZSCALE") != null;
        }

        /// <summary>Header level description of the image HDU <see cref="FitsDecodeOptions.Hdu"/> selects.</summary>
        public static VolumeInfo GetVolumeInfo(ReadOnlySpan<byte> data, VolumeDecodeOptions? options = null)
        {
            var o = FitsDecodeOptions.From(options);
            return WithFits(data, s => Info(Select(ReadHdus(s), o.Hdu), o));
        }

        public static ImageInfo GetInfo(ReadOnlySpan<byte> data) => GetInfo(data, null);

        public static ImageInfo GetInfo(ReadOnlySpan<byte> data, VolumeDecodeOptions? options)
        {
            var o = FitsDecodeOptions.From(options);
            return WithFits(data, s =>
            {
                var hdus = ReadHdus(s);
                var i = Info(Select(hdus, o.Hdu), o);
                return i.ToImageInfo(Math.Abs(Select(hdus, o.Hdu).BitPix));
            });
        }

        static PhysicalMetadata Metadata(FitsHdu h, bool flip)
        {
            var m = new PhysicalMetadata { SourceFormat = ImageFormat.Fits };
            foreach (var c in h.Cards) m.Header.Add(new(c.Key, c.Value));
            var (slope, zero) = Scaling(h);
            m.Slope = slope; m.Intercept = zero;
            m.ValueUnit = h.Get("BUNIT")?.Trim();
            var grid = Grid(h);
            int n = grid.Length;
            m.Spacing = new double[n];
            m.Units = new string?[n];
            for (int a = 0; a < n; a++) { m.Spacing[a] = double.NaN; m.Units[a] = h.Get("CUNIT" + (a + 1))?.Trim(); }
            // linear WCS
            int w = (int)Math.Min(h.GetLong("WCSAXES", n), 9);
            bool any = false;
            foreach (var c in h.Cards)
                if (c.Key.StartsWith("CRPIX") || c.Key.StartsWith("CRVAL") || c.Key.StartsWith("CDELT") || c.Key.StartsWith("CD") && c.Key.Contains('_')
                    || c.Key.StartsWith("PC") && c.Key.Contains('_') || c.Key == "CROTA2") { any = true; break; }
            if (!any || w < 1) return m;
            int sp = Math.Min(n, w);
            var M = new double[w, w];
            bool cd = false;
            for (int i = 0; i < w; i++)
                for (int j = 0; j < w; j++)
                    if (h.Get($"CD{i + 1}_{j + 1}") != null) cd = true;
            if (cd)
            {
                for (int i = 0; i < w; i++)
                    for (int j = 0; j < w; j++) M[i, j] = h.GetDouble($"CD{i + 1}_{j + 1}", 0);
            }
            else
            {
                double crota = h.GetDouble("CROTA2", 0) * Math.PI / 180;
                bool pc = false;
                for (int i = 0; i < w; i++) for (int j = 0; j < w; j++) if (h.Get($"PC{i + 1}_{j + 1}") != null) pc = true;
                for (int i = 0; i < w; i++)
                {
                    double cdelt = h.GetDouble("CDELT" + (i + 1), 1);
                    for (int j = 0; j < w; j++)
                    {
                        double p = h.GetDouble($"PC{i + 1}_{j + 1}", i == j ? 1 : 0);
                        if (!pc && crota != 0 && i < 2 && j < 2 && w >= 2)
                            p = (i, j) switch { (0, 0) => Math.Cos(crota), (0, 1) => -Math.Sin(crota) * h.GetDouble("CDELT2", 1) / cdelt,
                                                (1, 0) => Math.Sin(crota) * h.GetDouble("CDELT1", 1) / cdelt, _ => Math.Cos(crota) };
                        M[i, j] = cdelt * p;
                    }
                }
            }
            var p0 = new double[w];
            for (int j = 0; j < w; j++)
            {
                double first = j == 1 && flip && n > 1 ? grid[1] : 1;
                p0[j] = first - h.GetDouble("CRPIX" + (j + 1), 0);
            }
            var origin = new double[w];
            for (int i = 0; i < w; i++)
            {
                double v = h.GetDouble("CRVAL" + (i + 1), 0);
                for (int j = 0; j < w; j++) v += M[i, j] * p0[j];
                origin[i] = v;
            }
            var dir = new double[w, sp];
            for (int a = 0; a < sp; a++)
                for (int i = 0; i < w; i++) dir[i, a] = M[i, a] * (a == 1 && flip ? -1 : 1);
            var len = VolumeIO.Normalize(dir);
            for (int a = 0; a < sp; a++) m.Spacing[a] = len[a];
            m.Space = "FITS-WCS";
            m.Origin = origin;
            m.Direction = dir;
            return m;
        }

        // ================================================================ decode

        public static RawImage Decode(ReadOnlySpan<byte> data, VolumeDecodeOptions? options = null) => DecodeVolume(data, options).GetSlice(0);

        /// <summary>Decodes the image HDU selected by <see cref="FitsDecodeOptions.Hdu"/> (default: the first image).</summary>
        public static RawVolume DecodeVolume(ReadOnlySpan<byte> data, VolumeDecodeOptions? options = null)
        {
            var o = FitsDecodeOptions.From(options);
            return WithFits(data, s => Read(s, Select(ReadHdus(s), o.Hdu), o));
        }

        /// <summary>Decodes every image HDU (primary array, IMAGE extensions, compressed images), in file order.</summary>
        public static RawVolume[] DecodeAllVolumes(ReadOnlySpan<byte> data, VolumeDecodeOptions? options = null)
        {
            var o = FitsDecodeOptions.From(options);
            return WithFits(data, s =>
            {
                var r = new List<RawVolume>();
                foreach (var h in ReadHdus(s)) if (h.IsImage) r.Add(Read(s, h, o));
                return r.ToArray();
            });
        }

        /// <summary>Decodes a FITS file from disk (images above 2 GB are fine for uncompressed files).</summary>
        public static RawVolume DecodeVolume(string path, VolumeDecodeOptions? options = null)
        {
            var o = FitsDecodeOptions.From(options);
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            Span<byte> sig = stackalloc byte[3];
            int n = fs.Read(sig);
            fs.Position = 0;
            if (n == 3 && Gzip.IsGzip(sig))
            {
                using var ms = new MemoryStream();
                using (var g = new GzipReader(fs, true)) g.CopyTo(ms);
                ms.Position = 0;
                return Read(ms, Select(ReadHdus(ms), o.Hdu), o);
            }
            return Read(fs, Select(ReadHdus(fs), o.Hdu), o);
        }

        static RawVolume Read(Stream s, FitsHdu h, FitsDecodeOptions o)
        {
            var grid = Grid(h);
            var stored = StoredType(h.BitPix);
            long count = VolumeIO.Product(grid, 1);
            Array arr;
            bool floatQuantized = false;
            if (!h.IsCompressed)
            {
                if (s.CanSeek && h.DataOffset + h.DataSize > s.Length) throw new ImageFormatException("FITS: data ends early");
                s.Position = h.DataOffset;
                arr = VolumeIO.Allocate(stored, count, o.MaxSamples, Fmt);
                VolumeIO.ReadSamples(s, arr, 0, count, true, Fmt);
            }
            else
            {
                arr = VolumeIO.Allocate(stored, count, o.MaxSamples, Fmt);
                FitsTiles.Decompress(s, h, arr, out floatQuantized);
            }
            var m = Metadata(h, o.ApplyOrientation);
            long? blank = VolumeIO.IsInteger(stored) && h.Get("BLANK") != null ? h.GetLong("BLANK", 0) : null;
            var t = VolumeIO.ScaledType(stored, m.Slope, m.Intercept, o.Scaling);
            bool scale = VolumeIO.HasScaling(m.Slope, m.Intercept) && o.Scaling != DataScaling.Raw;
            if (scale || t != stored)
            {
                bool toFloat = !VolumeIO.IsInteger(t);
                arr = VolumeIO.Scale(arr, t, scale ? m.Slope : 1, scale ? m.Intercept : 0, toFloat ? blank : null);
                m.ScalingApplied = scale;
                if (blank != null && !toFloat) m.Blank = scale ? blank.Value * m.Slope + m.Intercept : blank.Value;
            }
            else if (blank != null) m.Blank = blank.Value;
            _ = floatQuantized;
            if (o.ApplyOrientation) VolumeIO.FlipRows(arr, grid[0], grid[1]);
            return new RawVolume(grid, ColorModel.Gray, arr, 1, m);
        }

        // ================================================================ encode

        public static void Encode(RawImage image, Stream output, FitsEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            Encode(RawVolume.FromImage(image), output, options);
        }

        public static byte[] Encode(RawImage image, FitsEncodeOptions? options = null) => ToBytes(s => Encode(image, s, options));
        public static byte[] Encode(RawVolume volume, FitsEncodeOptions? options = null) => ToBytes(s => Encode(volume, s, options));

        /// <summary>
        /// Writes the grid as the primary image (or, compressed, as a tile compressed extension). Channels become the slowest
        /// axis (NAXIS3 for an RGB image); Float16 is written as BITPIX -32.
        /// </summary>
        public static void Encode(RawVolume v, Stream output, FitsEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(v);
            ArgumentNullException.ThrowIfNull(output);
            options ??= new FitsEncodeOptions();
            var data = v.Data;
            var type = v.SampleType;
            if (type == SampleType.Float16) { data = VolumeIO.Convert(data, SampleType.Float32); type = SampleType.Float32; }
            var axes = new List<int>(v.Sizes);
            while (axes.Count > 2 && axes[^1] == 1) axes.RemoveAt(axes.Count - 1);
            if (v.Channels > 1)
            {
                data = VolumeIO.Deinterleave(data, data.LongLength / v.Channels, v.Channels);
                axes.Add(v.Channels);
            }
            if (options.ApplyOrientation)
            {
                if (data == v.Data) data = (Array)data.Clone();
                VolumeIO.FlipRows(data, axes[0], axes.Count > 1 ? axes[1] : 1);
            }
            // stored type and BZERO
            (int bitpix, long bzero, SampleType st) = type switch
            {
                SampleType.UInt8 => (8, 0L, SampleType.UInt8),
                SampleType.Int8 => (8, -128L, SampleType.UInt8),
                SampleType.Int16 => (16, 0L, SampleType.Int16),
                SampleType.UInt16 => (16, 32768L, SampleType.Int16),
                SampleType.Int32 => (32, 0L, SampleType.Int32),
                SampleType.UInt32 => (32, 2147483648L, SampleType.Int32),
                SampleType.Int64 => (64, 0L, SampleType.Int64),
                SampleType.UInt64 => (64, long.MinValue, SampleType.Int64), // BZERO = 2^63, written as text below
                SampleType.Float32 => (-32, 0L, SampleType.Float32),
                SampleType.Float64 => (-64, 0L, SampleType.Float64),
                _ => throw new ArgumentException($"FITS: cannot write {type}"),
            };
            if (bzero != 0) data = VolumeIO.Offset(data, st, bzero);
            var cards = new List<string>();
            var extra = ExtraCards(v.Metadata, axes, options.ApplyOrientation);
            if (options.Compression == FitsCompression.None)
            {
                cards.Add(Logical("SIMPLE", true, "conforms to FITS standard"));
                cards.Add(Int("BITPIX", bitpix, "array data type"));
                cards.Add(Int("NAXIS", axes.Count, "number of array dimensions"));
                for (int i = 0; i < axes.Count; i++) cards.Add(Int("NAXIS" + (i + 1), axes[i], null));
                cards.Add(Logical("EXTEND", true, null));
                AddScaling(cards, bzero, type);
                cards.AddRange(extra);
                WriteHeader(output, cards);
                VolumeIO.WriteSamples(output, data, 0, data.LongLength, true);
                Pad(output, data.LongLength * RawImage.BytesPerSample(st), 0);
                return;
            }
            // empty primary HDU, then the compressed image
            cards.Add(Logical("SIMPLE", true, "conforms to FITS standard"));
            cards.Add(Int("BITPIX", 8, null));
            cards.Add(Int("NAXIS", 0, null));
            cards.Add(Logical("EXTEND", true, null));
            WriteHeader(output, cards);
            FitsTiles.WriteCompressed(output, data, st, bitpix, axes.ToArray(), options, c =>
            {
                AddScaling(c, bzero, type);
                c.AddRange(extra);
            });
        }

        static void AddScaling(List<string> cards, long bzero, SampleType type)
        {
            if (bzero == 0) return;
            cards.Add(Raw("BZERO", type == SampleType.UInt64 ? "9223372036854775808" : bzero.ToString(VolumeIO.Inv), "offset data range to that of unsigned"));
            cards.Add(Int("BSCALE", 1, null));
        }

        static readonly HashSet<string> Structural = new()
        {
            "SIMPLE", "BITPIX", "NAXIS", "EXTEND", "BZERO", "BSCALE", "BLANK", "END", "XTENSION", "PCOUNT", "GCOUNT", "TFIELDS",
            "ZIMAGE", "ZBITPIX", "ZNAXIS", "ZCMPTYPE", "ZQUANTIZ", "ZDITHER0", "ZSIMPLE", "ZEXTEND", "ZTENSION", "ZPCOUNT", "ZGCOUNT",
            "ZHECKSUM", "ZDATASUM", "CHECKSUM", "DATASUM", "THEAP", "ZBLANK", "ZMASKCMP", "EXTNAME", "ZBLOCKED", "GROUPS",
        };

        static bool IsStructural(string k)
        {
            if (Structural.Contains(k)) return true;
            foreach (var p in new[] { "NAXIS", "ZNAXIS", "ZTILE", "ZNAME", "ZVAL", "TTYPE", "TFORM", "TUNIT", "TSCAL", "TZERO", "TNULL", "TDIM", "TDISP" })
                if (k.StartsWith(p, StringComparison.Ordinal) && k.Length > p.Length && char.IsDigit(k[p.Length])) return true;
            return false;
        }

        /// <summary>Header cards carried over from the metadata: a FITS source's own keywords, or a linear WCS built from the geometry.</summary>
        static List<string> ExtraCards(PhysicalMetadata? m, List<int> axes, bool flip)
        {
            var r = new List<string>();
            if (m == null) return r;
            if (m.SourceFormat == ImageFormat.Fits)
            {
                foreach (var kv in m.Header)
                {
                    if (IsStructural(kv.Key)) continue;
                    if (kv.Key is "COMMENT" or "HISTORY" or "") { r.Add(Commentary(kv.Key, kv.Value)); continue; }
                    if (kv.Key.Length > 8) continue;
                    r.Add(Guess(kv.Key, kv.Value));
                }
                return r;
            }
            if (m.ValueUnit != null) r.Add(Str("BUNIT", m.ValueUnit, null));
            if (!VolumeIO.HasGeometry(m)) return r;
            int n = Math.Min(axes.Count, 3);
            int space = m.SpaceDimension;
            if (space < n) return r;
            var vecs = VolumeIO.AxisVectors(m, n, space);
            // FITS pixel (1,1,...) is our sample (0, H-1, 0) when the rows are flipped
            var crval = new double[space];
            for (int i = 0; i < space; i++)
            {
                crval[i] = m.Origin != null && i < m.Origin.Length ? m.Origin[i] : 0;
                if (flip && n > 1) crval[i] += vecs[i, 1] * (axes[1] - 1);
            }
            r.Add(Int("WCSAXES", space, null));
            for (int i = 0; i < space; i++)
            {
                r.Add(Raw("CRPIX" + (i + 1), "1.0", null));
                r.Add(Raw("CRVAL" + (i + 1), VolumeIO.Format(crval[i]), null));
                if (i < m.Units.Length && m.Units[i] != null) r.Add(Str("CUNIT" + (i + 1), m.Units[i]!, null));
            }
            for (int i = 0; i < space; i++)
                for (int j = 0; j < n; j++)
                {
                    double d = vecs[i, j] * (j == 1 && flip ? -1 : 1);
                    r.Add(Raw($"CD{i + 1}_{j + 1}", VolumeIO.Format(d + 0.0), null));
                }
            return r;
        }

        static string Guess(string key, string value)
        {
            var t = value.Trim();
            if (t is "T" or "F") return Raw(key, t, null);
            if (long.TryParse(t, System.Globalization.NumberStyles.Integer, VolumeIO.Inv, out _)) return Raw(key, t, null);
            if (double.TryParse(t, System.Globalization.NumberStyles.Float, VolumeIO.Inv, out _) && t.Length <= 20 && !t.Contains(' ')) return Raw(key, t, null);
            return Str(key, value, null);
        }

        internal static string Pad80(string s) => s.Length >= 80 ? s.Substring(0, 80) : s.PadRight(80);
        internal static string Raw(string key, string value, string? comment)
            => Pad80(key.PadRight(8) + "= " + value.PadLeft(20) + (comment != null ? " / " + comment : ""));
        internal static string Int(string key, long v, string? comment) => Raw(key, v.ToString(VolumeIO.Inv), comment);
        internal static string Logical(string key, bool v, string? comment) => Raw(key, v ? "T" : "F", comment);
        internal static string Str(string key, string v, string? comment)
        {
            var esc = v.Replace("'", "''");
            if (esc.Length <= 68)
                return Pad80(key.PadRight(8) + "= " + "'" + esc.PadRight(8) + "'" + (comment != null ? " / " + comment : ""));
            // long string convention: chunks ending in '&', continued on CONTINUE cards
            var sb = new StringBuilder();
            int pos = 0;
            bool first = true;
            while (pos < esc.Length)
            {
                int n = Math.Min(67, esc.Length - pos);
                bool last = pos + n == esc.Length;
                if (!last && esc[pos + n - 1] == '\'' && QuoteRunOdd(esc, pos, n)) n--; // never split an escaped quote
                string chunk = esc.Substring(pos, n);
                pos += n;
                last = pos == esc.Length;
                string val = "'" + chunk + (last ? "" : "&") + "'";
                sb.Append(Pad80((first ? key.PadRight(8) + "= " : "CONTINUE  ") + val));
                first = false;
            }
            return sb.ToString();
        }

        static bool QuoteRunOdd(string s, int start, int n)
        {
            int k = 0;
            for (int i = start + n - 1; i >= start && s[i] == '\''; i--) k++;
            return (k & 1) == 1;
        }

        static string Commentary(string key, string text) => Pad80(key.PadRight(8) + text);

        internal static void WriteHeader(Stream s, List<string> cards)
        {
            var sb = new StringBuilder();
            foreach (var c in cards) sb.Append(c);
            sb.Append(Pad80("END"));
            int len = sb.Length;
            int padded = (len + Block - 1) / Block * Block;
            sb.Append(' ', padded - len);
            s.Write(Encoding.Latin1.GetBytes(sb.ToString()));
        }

        internal static void Pad(Stream s, long written, byte fill)
        {
            long rem = written % Block;
            if (rem == 0) return;
            var p = new byte[Block - rem];
            if (fill != 0) p.AsSpan().Fill(fill);
            s.Write(p);
        }

        static byte[] ToBytes(Action<Stream> write)
        {
            var ms = new MemoryStream();
            write(ms);
            return ms.ToArray();
        }
    }
}
