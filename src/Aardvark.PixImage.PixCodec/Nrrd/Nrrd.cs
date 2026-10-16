using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Aardvark.ImageCodecs.Compression;

namespace Aardvark.ImageCodecs
{
    /// <summary>Sample encoding of a written NRRD file.</summary>
    public enum NrrdEncoding
    {
        Raw,
        /// <summary>gzip compressed raw samples (what 3D Slicer writes by default).</summary>
        Gzip,
        /// <summary>Whitespace separated decimal numbers.</summary>
        Ascii,
        /// <summary>Two hexadecimal digits per byte.</summary>
        Hex,
    }

    public sealed class NrrdEncodeOptions
    {
        public NrrdEncoding Encoding { get; set; } = NrrdEncoding.Gzip;
        /// <summary>zlib level 0..9 for <see cref="NrrdEncoding.Gzip"/>.</summary>
        public int CompressionLevel { get; set; } = 6;
        /// <summary>Write big-endian samples (default little-endian).</summary>
        public bool BigEndian { get; set; }
    }

    /// <summary>
    /// NRRD ("nearly raw raster data", teem.sourceforge.net/nrrd): n-dimensional grids with attached (.nrrd) or detached
    /// (.nhdr) headers. Reads every scalar type, all encodings (raw, ascii / text, hex, gzip, bzip2), both byte orders, line
    /// skip / byte skip (also -1), and detached data in one file, a numbered file pattern or a LIST of files (with subdim).
    /// <para>
    /// Axes: a leading non-spatial axis (a kind such as RGB-color / vector / list, or "none" as its space direction) becomes the
    /// interleaved channels; a single such axis elsewhere is moved to the front. The remaining axes are the grid in file order
    /// (axis 0 = x, fastest), without any flip: row y = 0 is the first row, as ITK and 3D Slicer index the data. Space
    /// directions and origin go to <see cref="PhysicalMetadata"/> (space as named in the header, LPS for ITK / Slicer
    /// written files); spacing is the length of each direction vector, else the spacings field.
    /// </para>
    /// Writes raw, gzip, ascii or hex, attached or detached (<see cref="EncodeDetached(RawVolume, string, NrrdEncodeOptions?)"/>), with space, directions, origin
    /// and spacing from the metadata.
    /// </summary>
    public static class Nrrd
    {
        const string Fmt = "NRRD";

        public static bool IsMatch(ReadOnlySpan<byte> header)
            => header.Length >= 8 && header.StartsWith("NRRD000"u8) && header[7] is >= (byte)'1' and <= (byte)'9';

        // ================================================================ header

        internal sealed class Header
        {
            public int Dimension;
            public int[] Sizes = Array.Empty<int>();
            public SampleType Type;
            public string Encoding = "raw";
            public bool BigEndian;
            public long LineSkip, ByteSkip;
            public List<string> DataFiles = new();
            public int SubDim;              // dimension of the data in each file (0 = dimension - 1 if several files)
            public bool Detached;
            public long DataOffset;         // attached: offset of the data
            public string? Space;
            public int SpaceDim;
            public double[]?[]? Directions; // per axis, null = none
            public double[]? Origin;
            public double[]? Spacings;
            public string[]? Kinds;
            public string[]? Units, SpaceUnits;
            public string? SampleUnits;
            public List<KeyValuePair<string, string>> Entries = new();
            // derived
            public int ChannelAxis = -1, Channels = 1;
            public ColorModel Color = ColorModel.Gray;
            public int[] Grid = Array.Empty<int>();
        }

        static SampleType ParseType(string t) => t.Trim().ToLowerInvariant() switch
        {
            "signed char" or "int8" or "int8_t" => SampleType.Int8,
            "uchar" or "unsigned char" or "uint8" or "uint8_t" => SampleType.UInt8,
            "short" or "short int" or "signed short" or "signed short int" or "int16" or "int16_t" => SampleType.Int16,
            "ushort" or "unsigned short" or "unsigned short int" or "uint16" or "uint16_t" => SampleType.UInt16,
            "int" or "signed int" or "int32" or "int32_t" => SampleType.Int32,
            "uint" or "unsigned int" or "uint32" or "uint32_t" => SampleType.UInt32,
            "longlong" or "long long" or "long long int" or "signed long long" or "signed long long int" or "int64" or "int64_t" => SampleType.Int64,
            "ulonglong" or "unsigned long long" or "unsigned long long int" or "uint64" or "uint64_t" => SampleType.UInt64,
            "float" => SampleType.Float32,
            "double" => SampleType.Float64,
            "block" => throw new ImageNotSupportedException("NRRD: the block type is not supported"),
            var x => throw new ImageFormatException($"NRRD: unknown type '{x}'"),
        };

        static string TypeName(SampleType t) => t switch
        {
            SampleType.Int8 => "int8", SampleType.UInt8 => "uint8", SampleType.Int16 => "int16", SampleType.UInt16 => "uint16",
            SampleType.Int32 => "int32", SampleType.UInt32 => "uint32", SampleType.Int64 => "int64", SampleType.UInt64 => "uint64",
            SampleType.Float32 => "float", SampleType.Float64 => "double",
            _ => throw new ArgumentException($"NRRD: cannot write {t}"),
        };

        static string? CanonicalSpace(string s) => s.Trim().ToLowerInvariant() switch
        {
            "right-anterior-superior" or "ras" or "right-anterior-superior-time" or "rast" => "RAS",
            "left-anterior-superior" or "las" or "left-anterior-superior-time" or "last" => "LAS",
            "left-posterior-superior" or "lps" or "left-posterior-superior-time" or "lpst" => "LPS",
            "scanner-xyz" or "scanner-xyz-time" => "scanner-xyz",
            "3d-right-handed" or "3d-right-handed-time" => "3D-right-handed",
            "3d-left-handed" or "3d-left-handed-time" => "3D-left-handed",
            _ => s.Trim(),
        };

        static int SpaceDimOf(string s) => s.Trim().ToLowerInvariant() switch
        {
            "right-anterior-superior-time" or "rast" or "left-anterior-superior-time" or "last" or "left-posterior-superior-time" or "lpst"
                or "scanner-xyz-time" or "3d-right-handed-time" or "3d-left-handed-time" => 4,
            "right-anterior-superior" or "ras" or "left-anterior-superior" or "las" or "left-posterior-superior" or "lps"
                or "scanner-xyz" or "3d-right-handed" or "3d-left-handed" => 3,
            var x => throw new ImageFormatException($"NRRD: unknown space '{x}'"),
        };

        static string NrrdSpaceName(string space) => space switch
        {
            "RAS" => "right-anterior-superior",
            "LAS" => "left-anterior-superior",
            "LPS" => "left-posterior-superior",
            _ => space,
        };

        static bool IsKnownSpaceName(string s)
        {
            try { SpaceDimOf(s); return true; } catch (ImageFormatException) { return false; }
        }

        static double[] ParseVector(string s, int dim)
        {
            var t = s.Trim();
            if (t.Length < 2 || t[0] != '(' || t[^1] != ')') throw new ImageFormatException($"NRRD: bad vector '{s}'");
            var parts = t.Substring(1, t.Length - 2).Split(',');
            if (dim > 0 && parts.Length != dim) throw new ImageFormatException($"NRRD: vector '{s}' needs {dim} components");
            var v = new double[parts.Length];
            for (int i = 0; i < v.Length; i++) v[i] = VolumeIO.ParseDouble(parts[i], Fmt, "a vector");
            return v;
        }

        /// <summary>Splits per axis vectors "(a,b,c) none (d,e,f)" (whitespace may occur inside the parentheses).</summary>
        static List<string> SplitVectors(string s)
        {
            var r = new List<string>();
            int i = 0;
            while (i < s.Length)
            {
                if (char.IsWhiteSpace(s[i])) { i++; continue; }
                if (s[i] == '(')
                {
                    int j = s.IndexOf(')', i);
                    if (j < 0) throw new ImageFormatException("NRRD: unterminated vector");
                    r.Add(s.Substring(i, j - i + 1).Replace(" ", "").Replace("\t", ""));
                    i = j + 1;
                }
                else
                {
                    int j = i;
                    while (j < s.Length && !char.IsWhiteSpace(s[j])) j++;
                    r.Add(s.Substring(i, j - i));
                    i = j;
                }
            }
            return r;
        }

        static string[] Quoted(string s)
        {
            var r = new List<string>();
            int i = 0;
            while (i < s.Length)
            {
                if (s[i] != '"') { i++; continue; }
                var sb = new StringBuilder();
                i++;
                while (i < s.Length && s[i] != '"')
                {
                    if (s[i] == '\\' && i + 1 < s.Length && s[i + 1] == '"') { sb.Append('"'); i += 2; continue; }
                    sb.Append(s[i++]);
                }
                i++;
                r.Add(sb.ToString());
            }
            return r.ToArray();
        }

        static bool IsDomainKind(string k) => k.ToLowerInvariant() is "domain" or "space" or "time" or "???" or "none";

        /// <summary>
        /// Parses the header. <paramref name="complete"/> false means the prefix ends inside the header (no blank line and no
        /// data file field yet) and throws ImageFormatException.
        /// </summary>
        internal static Header Parse(ReadOnlySpan<byte> d, bool complete = true)
        {
            if (!IsMatch(d)) throw new ImageFormatException("NRRD: not a NRRD file");
            var h = new Header();
            int pos = 0;
            bool first = true, sawBlank = false, listMode = false;
            string? dataFile = null;
            var fields = new Dictionary<string, string>();
            while (pos < d.Length)
            {
                int nl = d.Slice(pos).IndexOf((byte)'\n');
                if (nl < 0 && !complete) throw new ImageFormatException("NRRD: header incomplete");
                int len = nl < 0 ? d.Length - pos : nl;
                var lineBytes = d.Slice(pos, len);
                if (lineBytes.Length > 0 && lineBytes[^1] == '\r') lineBytes = lineBytes.Slice(0, lineBytes.Length - 1);
                pos += nl < 0 ? len : len + 1;
                string line = Encoding.Latin1.GetString(lineBytes);
                if (first) { first = false; continue; }
                if (listMode)
                {
                    if (line.Length == 0) { sawBlank = true; break; }
                    h.DataFiles.Add(line.Trim());
                    continue;
                }
                if (line.Length == 0) { sawBlank = true; break; }
                if (line[0] == '#') continue;
                int fi = line.IndexOf(": ", StringComparison.Ordinal), ki = line.IndexOf(":=", StringComparison.Ordinal);
                if (ki >= 0 && (fi < 0 || ki < fi))
                {
                    string key = line.Substring(0, ki), val = line.Substring(ki + 2).Replace("\\n", "\n").Replace("\\\\", "\\");
                    h.Entries.Add(new(key, val));
                    continue;
                }
                if (fi < 0)
                {
                    // tolerate "field:" with an empty descriptor
                    if (line.EndsWith(':')) fi = line.Length - 1;
                    else throw new ImageFormatException($"NRRD: bad header line '{line}'");
                }
                string id = line.Substring(0, fi).Trim().ToLowerInvariant(), desc = fi + 2 <= line.Length ? line.Substring(fi + 2).TrimEnd() : "";
                id = id switch { "datafile" => "data file", "lineskip" => "line skip", "byteskip" => "byte skip", "blocksize" => "block size",
                                 "sampleunits" => "sample units", "oldmin" => "old min", "oldmax" => "old max", "axismins" => "axis mins",
                                 "axismaxs" => "axis maxs", _ => id };
                fields[id] = desc;
                h.Entries.Add(new(id, desc));
                if (id == "data file")
                {
                    var w = VolumeIO.Words(desc);
                    if (w.Length >= 1 && w[0] == "LIST") { listMode = true; if (w.Length > 1) h.SubDim = SubDim(w[1]); }
                    else dataFile = desc;
                }
            }
            if (!sawBlank && !listMode && dataFile == null && !complete) throw new ImageFormatException("NRRD: header incomplete");
            if (!sawBlank && dataFile == null && !listMode) throw new ImageFormatException("NRRD: no blank line after the header and no data file");
            h.DataOffset = pos;

            string F(string k) => fields.TryGetValue(k, out var v) ? v : throw new ImageFormatException($"NRRD: missing '{k}' field");
            if (!int.TryParse(F("dimension").Trim(), System.Globalization.NumberStyles.Integer, VolumeIO.Inv, out h.Dimension) || h.Dimension < 1 || h.Dimension > 16)
                throw new ImageFormatException("NRRD: bad dimension");
            h.Type = ParseType(F("type"));
            var sz = VolumeIO.Words(F("sizes"));
            if (sz.Length != h.Dimension) throw new ImageFormatException("NRRD: sizes do not match the dimension");
            h.Sizes = new int[sz.Length];
            for (int i = 0; i < sz.Length; i++)
                if (!int.TryParse(sz[i], System.Globalization.NumberStyles.Integer, VolumeIO.Inv, out h.Sizes[i]) || h.Sizes[i] <= 0)
                    throw new ImageFormatException("NRRD: bad size");
            h.Encoding = F("encoding").Trim().ToLowerInvariant() switch
            {
                "raw" => "raw",
                "txt" or "text" or "ascii" => "ascii",
                "hex" => "hex",
                "gz" or "gzip" => "gzip",
                "bz2" or "bzip2" => "bzip2",
                var e => throw new ImageNotSupportedException($"NRRD: encoding '{e}' is not supported"),
            };
            if (fields.TryGetValue("endian", out var en))
                h.BigEndian = en.Trim().ToLowerInvariant() switch { "big" => true, "little" => false, var x => throw new ImageFormatException($"NRRD: bad endian '{x}'") };
            if (fields.TryGetValue("line skip", out var ls) && (!long.TryParse(ls.Trim(), System.Globalization.NumberStyles.Integer, VolumeIO.Inv, out h.LineSkip) || h.LineSkip < 0))
                throw new ImageFormatException("NRRD: bad line skip");
            if (fields.TryGetValue("byte skip", out var bs) && (!long.TryParse(bs.Trim(), System.Globalization.NumberStyles.Integer, VolumeIO.Inv, out h.ByteSkip) || h.ByteSkip < -1))
                throw new ImageFormatException("NRRD: bad byte skip");
            if (h.ByteSkip == -1 && h.Encoding != "raw") throw new ImageFormatException("NRRD: byte skip -1 needs raw encoding");

            if (dataFile != null)
            {
                h.Detached = true;
                var w = VolumeIO.Words(dataFile);
                if (w.Length >= 4 && w[0].Contains('%') && TryInt(w[1], out int mn) && TryInt(w[2], out int mx) && TryInt(w[3], out int st))
                {
                    if (st == 0 || st > 0 && mn > mx || st < 0 && mx > mn) throw new ImageFormatException("NRRD: bad data file range");
                    if (w.Length >= 5) h.SubDim = SubDim(w[4]);
                    for (long v = mn; st > 0 ? v <= mx : v >= mx; v += st)
                    {
                        h.DataFiles.Add(CFormat(w[0], (int)v));
                        if (h.DataFiles.Count > 1 << 20) throw new ImageFormatException("NRRD: too many data files");
                    }
                }
                else h.DataFiles.Add(dataFile.Trim());
            }
            else if (listMode)
            {
                h.Detached = true;
                if (h.DataFiles.Count == 0) throw new ImageFormatException("NRRD: empty data file list");
            }

            // orientation
            if (fields.TryGetValue("space", out var sp))
            {
                h.SpaceDim = SpaceDimOf(sp);
                h.Space = CanonicalSpace(sp);
            }
            else if (fields.TryGetValue("space dimension", out var sd))
            {
                if (!int.TryParse(sd.Trim(), System.Globalization.NumberStyles.Integer, VolumeIO.Inv, out h.SpaceDim) || h.SpaceDim < 1 || h.SpaceDim > 16)
                    throw new ImageFormatException("NRRD: bad space dimension");
            }
            if (fields.TryGetValue("space directions", out var sdirs))
            {
                var v = SplitVectors(sdirs);
                if (v.Count != h.Dimension) throw new ImageFormatException("NRRD: space directions do not match the dimension");
                h.Directions = new double[]?[h.Dimension];
                for (int i = 0; i < v.Count; i++)
                    h.Directions[i] = v[i].Equals("none", StringComparison.OrdinalIgnoreCase) ? null : ParseVector(v[i], h.SpaceDim);
            }
            if (fields.TryGetValue("space origin", out var so)) h.Origin = ParseVector(so, h.SpaceDim);
            if (fields.TryGetValue("spacings", out var spc))
            {
                var w = VolumeIO.Words(spc);
                if (w.Length != h.Dimension) throw new ImageFormatException("NRRD: spacings do not match the dimension");
                h.Spacings = Array.ConvertAll(w, x => VolumeIO.ParseDouble(x, Fmt, "spacings"));
            }
            if (fields.TryGetValue("kinds", out var kd))
            {
                h.Kinds = VolumeIO.Words(kd);
                if (h.Kinds.Length != h.Dimension) throw new ImageFormatException("NRRD: kinds do not match the dimension");
            }
            if (fields.TryGetValue("units", out var un)) h.Units = Quoted(un);
            if (fields.TryGetValue("space units", out var su)) h.SpaceUnits = Quoted(su);
            if (fields.TryGetValue("sample units", out var smu)) h.SampleUnits = smu.Trim().Trim('"');

            // channel axis: a non-domain kind, or (without a kind saying otherwise) "none" as the direction of axis 0
            int chAxis = -1;
            if (h.Dimension > 1)
            {
                var cands = new List<int>();
                for (int a = 0; a < h.Dimension; a++)
                {
                    string? k = h.Kinds?[a].ToLowerInvariant();
                    bool nonDomain = k != null && !IsDomainKind(k);
                    bool noDir = a == 0 && h.Directions != null && h.Directions[0] == null && (k == null || k is "none" or "???");
                    if (nonDomain || noDir) cands.Add(a);
                }
                if (cands.Count == 1 || cands.Count > 1 && cands[0] == 0) chAxis = cands[0];
                else if (cands.Count > 1) throw new ImageNotSupportedException("NRRD: more than one non-spatial axis");
            }
            h.ChannelAxis = chAxis;
            h.Channels = chAxis >= 0 ? h.Sizes[chAxis] : 1;
            var kind = chAxis >= 0 && h.Kinds != null ? h.Kinds[chAxis].ToLowerInvariant() : "";
            h.Color = h.Channels switch
            {
                1 => ColorModel.Gray,
                3 when kind is "rgb-color" or "3-color" => ColorModel.Rgb,
                4 when kind is "rgba-color" or "4-color" => ColorModel.Rgba,
                _ => ColorModel.Other,
            };
            var grid = new List<int>();
            for (int a = 0; a < h.Dimension; a++) if (a != chAxis) grid.Add(h.Sizes[a]);
            if (grid.Count == 0) grid.Add(1);
            h.Grid = grid.ToArray();
            return h;
        }

        internal static int SubDim(string s)
        {
            int n = 0, i = 0;
            while (i < s.Length && char.IsDigit(s[i]) && n < 100) n = n * 10 + (s[i++] - '0');
            if (i == 0) throw new ImageFormatException($"bad subdim '{s}'");
            return n;
        }

        static bool TryInt(string s, out int v) => int.TryParse(s, System.Globalization.NumberStyles.Integer, VolumeIO.Inv, out v);

        /// <summary>printf with a single integer conversion (%d, %i, %u, %03d, %5d, ...).</summary>
        internal static string CFormat(string fmt, int value)
        {
            int p = fmt.IndexOf('%');
            while (p >= 0 && p + 1 < fmt.Length && fmt[p + 1] == '%') p = fmt.IndexOf('%', p + 2);
            if (p < 0) throw new ImageFormatException($"bad file name pattern '{fmt}'");
            int i = p + 1;
            bool zero = false, left = false;
            while (i < fmt.Length && fmt[i] is '0' or '-' or '+' or ' ') { if (fmt[i] == '0') zero = true; if (fmt[i] == '-') left = true; i++; }
            int width = 0;
            while (i < fmt.Length && char.IsDigit(fmt[i])) width = width * 10 + (fmt[i++] - '0');
            while (i < fmt.Length && fmt[i] is 'l' or 'h') i++;
            if (i >= fmt.Length || fmt[i] is not ('d' or 'i' or 'u')) throw new ImageFormatException($"bad file name pattern '{fmt}'");
            string num = value.ToString(VolumeIO.Inv);
            if (num.Length < width)
                num = left ? num.PadRight(width) : zero ? (value < 0 ? "-" + num.Substring(1).PadLeft(width - 1, '0') : num.PadLeft(width, '0')) : num.PadLeft(width);
            return (fmt.Substring(0, p) + num + fmt.Substring(i + 1)).Replace("%%", "%");
        }

        static PhysicalMetadata Metadata(Header h)
        {
            var m = new PhysicalMetadata { SourceFormat = ImageFormat.Nrrd, Space = h.Space, Origin = h.Origin, ValueUnit = h.SampleUnits, Header = h.Entries };
            int n = h.Grid.Length;
            var gridAxes = new List<int>();
            for (int a = 0; a < h.Dimension; a++) if (a != h.ChannelAxis) gridAxes.Add(a);
            m.Spacing = new double[n];
            m.Units = new string?[n];
            int spatial = 0;
            if (h.Directions != null)
                for (int g = 0; g < gridAxes.Count; g++) if (h.Directions[gridAxes[g]] != null) spatial = g + 1;
            double[,]? dir = spatial > 0 && h.SpaceDim > 0 ? new double[h.SpaceDim, spatial] : null;
            for (int g = 0; g < gridAxes.Count; g++)
            {
                int a = gridAxes[g];
                double s = double.NaN;
                var v = h.Directions?[a];
                if (v != null)
                {
                    double l = 0;
                    foreach (var x in v) l += x * x;
                    l = Math.Sqrt(l);
                    s = l;
                    if (dir != null && g < spatial)
                        for (int r = 0; r < v.Length; r++) dir[r, g] = l > 0 ? v[r] / l : 0;
                }
                else if (h.Spacings != null) s = h.Spacings[a];
                m.Spacing[g] = s;
                if (h.Units != null && a < h.Units.Length && h.Units[a].Length > 0) m.Units[g] = h.Units[a];
                else if (v != null && h.SpaceUnits != null && h.SpaceUnits.Length > 0)
                    m.Units[g] = Array.TrueForAll(h.SpaceUnits, u => u == h.SpaceUnits[0]) ? h.SpaceUnits[0] : g < h.SpaceUnits.Length ? h.SpaceUnits[g] : null;
            }
            m.Direction = dir;
            return m;
        }

        // ================================================================ info

        /// <summary>The grid, sample type and physical metadata DecodeVolume would return, from the header only.</summary>
        public static VolumeInfo GetVolumeInfo(ReadOnlySpan<byte> data, VolumeDecodeOptions? options = null)
        {
            var h = Parse(data);
            return Info(h);
        }

        static VolumeInfo Info(Header h) => new()
        {
            Format = ImageFormat.Nrrd, Sizes = h.Grid, Channels = h.Channels, Color = h.Color, SampleType = h.Type, StoredType = h.Type,
            Encoding = h.Encoding, Detached = h.Detached, Metadata = Metadata(h),
        };

        /// <summary>Header level information: Width / Height / Depth of the grid, FrameCount = product of the further axes.</summary>
        public static ImageInfo GetInfo(ReadOnlySpan<byte> data) => GetInfo(data, false);

        internal static ImageInfo GetInfo(ReadOnlySpan<byte> data, bool prefix)
        {
            var h = Parse(data, !prefix);
            return Info(h).ToImageInfo(RawImage.BytesPerSample(h.Type) * 8);
        }

        // ================================================================ decode

        /// <summary>Decodes the first z slice (of the first frame) as an image; 2D files completely.</summary>
        public static RawImage Decode(ReadOnlySpan<byte> data, VolumeDecodeOptions? options = null)
        {
            var v = DecodeVolume(data, options);
            return v.GetSlice(0);
        }

        /// <summary>Decodes the whole grid. Detached data needs <see cref="VolumeDecodeOptions.DataDirectory"/> or the path overload.</summary>
        public static RawVolume DecodeVolume(ReadOnlySpan<byte> data, VolumeDecodeOptions? options = null)
        {
            options ??= VolumeDecodeOptions.Default;
            var h = Parse(data);
            if (h.Detached) return Read(h, null, options);
            long off = h.DataOffset;
            return VolumeIO.WithStream(data, s => { s.Position = off; return Read(h, s, options); });
        }

        /// <summary>Decodes a .nrrd or .nhdr file; detached data files are found relative to it. Data above 2 GB is fine.</summary>
        public static RawVolume DecodeVolume(string path, VolumeDecodeOptions? options = null)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            var prefix = ReadPrefix(fs, p => { try { Parse(p, false); return true; } catch (ImageFormatException) { return false; } });
            var h = Parse(prefix);
            options = WithDirectory(options, path);
            if (h.Detached) return Read(h, null, options);
            fs.Position = h.DataOffset;
            return Read(h, fs, options);
        }

        internal static VolumeDecodeOptions WithDirectory(VolumeDecodeOptions? o, string path)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (o == null) return new VolumeDecodeOptions { DataDirectory = dir };
            if (o.DataDirectory != null || o.OpenDataFile != null) return o;
            return new VolumeDecodeOptions { Scaling = o.Scaling, MaxSamples = o.MaxSamples, DataDirectory = dir, OpenDataFile = o.OpenDataFile };
        }

        /// <summary>Reads a growing prefix of a stream until <paramref name="complete"/> accepts it or the stream ends.</summary>
        internal static byte[] ReadPrefix(Stream s, Func<byte[], bool> complete, int initial = 4096)
        {
            var buf = new byte[initial];
            int n = 0;
            while (true)
            {
                int r = s.Read(buf, n, buf.Length - n);
                if (r > 0) n += r;
                bool eof = r <= 0;
                if (n == buf.Length || eof)
                {
                    var p = buf.AsSpan(0, n).ToArray();
                    if (eof || complete(p)) return p;
                    if (buf.Length >= 1 << 26) throw new ImageFormatException("header too large");
                    Array.Resize(ref buf, buf.Length * 4);
                }
            }
        }

        static RawVolume Read(Header h, Stream? attached, VolumeDecodeOptions o)
        {
            long total = VolumeIO.Product(h.Sizes, 1);
            var arr = VolumeIO.Allocate(h.Type, total, o.MaxSamples, Fmt);
            int bps = RawImage.BytesPerSample(h.Type);
            if (!h.Detached)
            {
                ReadPart(h, attached!, arr, 0, total, bps, null);
            }
            else
            {
                int files = h.DataFiles.Count;
                if (total % files != 0) throw new ImageFormatException("NRRD: the samples do not divide evenly among the data files");
                if (h.SubDim > 0)
                {
                    // subdim gives the dimension of each file's data: check it is consistent with the file count
                    long per = 1;
                    for (int a = 0; a < Math.Min(h.SubDim, h.Dimension); a++) per *= h.Sizes[a];
                    if (h.SubDim < h.Dimension && per * files != total) throw new ImageFormatException("NRRD: data file count does not match subdim");
                }
                long share = total / files;
                for (int f = 0; f < files; f++)
                {
                    using var s = o.Open(h.DataFiles[f], Fmt);
                    ReadPart(h, s, arr, f * share, share, bps, s.CanSeek ? s.Length : null);
                }
            }
            // channel axis to the front (interleaved)
            if (h.ChannelAxis > 0)
            {
                long inner = 1;
                for (int a = 0; a < h.ChannelAxis; a++) inner *= h.Sizes[a];
                long block = inner * h.Channels;
                var dst = RawImage.CreateArray(h.Type, arr.Length);
                for (long b = 0; b < total; b += block)
                {
                    var src = RawImage.CreateArray(h.Type, (int)block);
                    Array.Copy(arr, b, src, 0, block);
                    var il = VolumeIO.Interleave(src, inner, h.Channels);
                    Array.Copy(il, 0, dst, b, block);
                }
                arr = dst;
            }
            return new RawVolume(h.Grid, h.Color, arr, h.Channels, Metadata(h));
        }

        static void ReadPart(Header h, Stream s, Array arr, long offset, long count, int bps, long? fileLength)
        {
            if (h.ByteSkip == -1)
            {
                if (fileLength == null || !s.CanSeek) throw new ImageNotSupportedException("NRRD: byte skip -1 needs a seekable data file");
                long start = fileLength.Value - count * bps;
                if (start < 0 || start < s.Position && h.Detached) throw new ImageFormatException("NRRD: data file too short");
                s.Position = start;
                VolumeIO.ReadSamples(s, arr, offset, count, h.BigEndian, Fmt);
                return;
            }
            SkipLines(s, h.LineSkip);
            switch (h.Encoding)
            {
                case "raw":
                    VolumeIO.Skip(s, h.ByteSkip, Fmt);
                    VolumeIO.ReadSamples(s, arr, offset, count, h.BigEndian, Fmt);
                    break;
                case "gzip":
                    using (var g = new GzipReader(s, leaveOpen: true) { StopAtEnd = false })
                    {
                        VolumeIO.Skip(g, h.ByteSkip, Fmt);
                        VolumeIO.ReadSamples(g, arr, offset, count, h.BigEndian, Fmt);
                    }
                    break;
                case "bzip2":
                    using (var b = new Bzip2Reader(s, leaveOpen: true))
                    {
                        VolumeIO.Skip(b, h.ByteSkip, Fmt);
                        VolumeIO.ReadSamples(b, arr, offset, count, h.BigEndian, Fmt);
                    }
                    break;
                case "hex":
                    VolumeIO.Skip(s, h.ByteSkip, Fmt);
                    ReadHex(s, arr, offset, count, bps, h.BigEndian);
                    break;
                case "ascii":
                    VolumeIO.Skip(s, h.ByteSkip, Fmt);
                    ReadAscii(s, arr, offset, count, h.Type);
                    break;
            }
        }

        static void SkipLines(Stream s, long lines)
        {
            if (lines <= 0) return;
            var b = new byte[1];
            while (lines > 0)
            {
                int c = s.ReadByte();
                if (c < 0) throw new ImageFormatException("NRRD: data ends inside the skipped lines");
                if (c == '\n') lines--;
            }
        }

        static int HexVal(int c) => c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1,
        };

        static void ReadHex(Stream s, Array arr, long offset, long count, int bps, bool big)
        {
            var t = RawImage.SampleTypeOf(arr.GetType().GetElementType()!);
            var buf = new byte[1 << 16];
            var outBuf = new byte[(1 << 15) / bps * bps];
            int bp = 0, be = 0, o = 0, hi = -1;
            long need = count * bps, done = 0;
            while (done < need)
            {
                if (bp >= be)
                {
                    be = s.Read(buf, 0, buf.Length); bp = 0;
                    if (be <= 0) throw new ImageFormatException("NRRD: hex data ends early");
                }
                int c = buf[bp++];
                int v = HexVal(c);
                if (v < 0)
                {
                    if (c is ' ' or '\t' or '\r' or '\n' or '\v' or '\f') continue;
                    throw new ImageFormatException("NRRD: bad character in hex data");
                }
                if (hi < 0) { hi = v; continue; }
                outBuf[o++] = (byte)(hi << 4 | v); hi = -1;
                done++;
                if (o == outBuf.Length || done == need)
                {
                    VolumeIO.CopySamples(outBuf.AsSpan(0, o), arr, offset, big);
                    offset += o / bps; o = 0;
                }
            }
        }

        static void ReadAscii(Stream s, Array arr, long offset, long count, SampleType t)
        {
            var buf = new byte[1 << 16];
            int bp = 0, be = 0;
            Span<char> tok = stackalloc char[128];
            for (long i = 0; i < count; i++)
            {
                int tl = 0;
                // skip whitespace (and commas, which some writers use)
                while (true)
                {
                    if (bp >= be) { be = s.Read(buf, 0, buf.Length); bp = 0; if (be <= 0) throw new ImageFormatException("NRRD: ascii data ends early"); }
                    byte c = buf[bp];
                    if (c is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\v' or (byte)'\f' or (byte)',') { bp++; continue; }
                    break;
                }
                while (true)
                {
                    if (bp >= be) { be = s.Read(buf, 0, buf.Length); bp = 0; if (be <= 0) break; }
                    byte c = buf[bp];
                    if (c is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\v' or (byte)'\f' or (byte)',') break;
                    if (tl >= tok.Length) throw new ImageFormatException("NRRD: ascii value too long");
                    tok[tl++] = (char)c; bp++;
                }
                Store(arr, t, offset + i, tok.Slice(0, tl));
            }
        }

        static void Store(Array arr, SampleType t, long i, ReadOnlySpan<char> s)
        {
            var st = System.Globalization.NumberStyles.Integer;
            var inv = VolumeIO.Inv;
            bool ok = true;
            switch (t)
            {
                case SampleType.UInt8: ok = byte.TryParse(s, st, inv, out var a); ((byte[])arr)[i] = a; break;
                case SampleType.Int8: ok = sbyte.TryParse(s, st, inv, out var b); ((sbyte[])arr)[i] = b; break;
                case SampleType.UInt16: ok = ushort.TryParse(s, st, inv, out var c); ((ushort[])arr)[i] = c; break;
                case SampleType.Int16: ok = short.TryParse(s, st, inv, out var d); ((short[])arr)[i] = d; break;
                case SampleType.UInt32: ok = uint.TryParse(s, st, inv, out var e); ((uint[])arr)[i] = e; break;
                case SampleType.Int32: ok = int.TryParse(s, st, inv, out var f); ((int[])arr)[i] = f; break;
                case SampleType.UInt64: ok = ulong.TryParse(s, st, inv, out var g); ((ulong[])arr)[i] = g; break;
                case SampleType.Int64: ok = long.TryParse(s, st, inv, out var k); ((long[])arr)[i] = k; break;
                case SampleType.Float32: ok = VolumeIO.TryParseDouble(s, out var x); ((float[])arr)[i] = (float)x; break;
                case SampleType.Float64: ok = VolumeIO.TryParseDouble(s, out var y); ((double[])arr)[i] = y; break;
            }
            if (!ok)
            {
                // integers written as "12.0" or with an exponent
                if (VolumeIO.TryParseDouble(s, out var v) && Math.Floor(v) == v)
                {
                    Array.Copy(VolumeIO.Scale(new[] { v }, t, 1, 0, null), 0, arr, i, 1);
                    return;
                }
                throw new ImageFormatException($"NRRD: bad ascii value '{s.ToString()}'");
            }
        }

        // ================================================================ encode

        /// <summary>Writes an image as a 2D NRRD (channels as a leading axis).</summary>
        public static void Encode(RawImage image, Stream output, NrrdEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            Encode(RawVolume.FromImage(image), output, options);
        }

        public static byte[] Encode(RawImage image, NrrdEncodeOptions? options = null) => ToBytes(s => Encode(image, s, options));

        /// <summary>Writes a grid as an attached NRRD (.nrrd).</summary>
        public static void Encode(RawVolume volume, Stream output, NrrdEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(volume);
            ArgumentNullException.ThrowIfNull(output);
            options ??= new NrrdEncodeOptions();
            var (data, type) = Writable(volume);
            output.Write(Encoding.Latin1.GetBytes(HeaderText(volume, type, options, null)));
            WriteData(output, data, type, options);
        }

        public static byte[] Encode(RawVolume volume, NrrdEncodeOptions? options = null) => ToBytes(s => Encode(volume, s, options));

        /// <summary>
        /// Writes a detached header (<paramref name="headerPath"/>, usually .nhdr) and the samples next to it in
        /// &lt;name&gt;.raw, .raw.gz, .txt or .hex depending on the encoding.
        /// </summary>
        public static void EncodeDetached(RawVolume volume, string headerPath, NrrdEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(volume);
            options ??= new NrrdEncodeOptions();
            var (data, type) = Writable(volume);
            string ext = options.Encoding switch { NrrdEncoding.Gzip => ".raw.gz", NrrdEncoding.Ascii => ".txt", NrrdEncoding.Hex => ".hex", _ => ".raw" };
            string dataName = Path.GetFileNameWithoutExtension(headerPath) + ext;
            string dataPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(headerPath))!, dataName);
            File.WriteAllText(headerPath, HeaderText(volume, type, options, dataName), Encoding.Latin1);
            using var fs = new FileStream(dataPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            WriteData(fs, data, type, options);
        }

        /// <inheritdoc cref="EncodeDetached(RawVolume, string, NrrdEncodeOptions?)"/>
        public static void EncodeDetached(RawImage image, string headerPath, NrrdEncodeOptions? options = null)
            => EncodeDetached(RawVolume.FromImage(image), headerPath, options);

        static (Array data, SampleType type) Writable(RawVolume v)
            => v.SampleType == SampleType.Float16 ? (VolumeIO.Convert(v.Data, SampleType.Float32), SampleType.Float32) : (v.Data, v.SampleType);

        static string Vec(double[,] m, int col)
        {
            var sb = new StringBuilder("(");
            for (int r = 0; r < m.GetLength(0); r++) { if (r > 0) sb.Append(','); sb.Append(VolumeIO.Format(m[r, col])); }
            return sb.Append(')').ToString();
        }

        static readonly HashSet<string> StandardFields = new(StringComparer.OrdinalIgnoreCase)
        {
            "type", "dimension", "sizes", "encoding", "endian", "space", "space dimension", "space directions", "space origin",
            "space units", "spacings", "kinds", "units", "labels", "centers", "centerings", "thicknesses", "axis mins", "axis maxs",
            "data file", "line skip", "byte skip", "block size", "sample units", "measurement frame", "number", "content",
            "min", "max", "old min", "old max",
        };

        internal static string HeaderText(RawVolume v, SampleType type, NrrdEncodeOptions o, string? dataFile)
        {
            var m = v.Metadata;
            bool ch = v.Channels > 1;
            int grid = v.Sizes.Length, dim = grid + (ch ? 1 : 0);
            var sb = new StringBuilder();
            sb.Append("NRRD0004\n# Complete NRRD file format specification at:\n# http://teem.sourceforge.net/nrrd/format.html\n");
            sb.Append("type: ").Append(TypeName(type)).Append('\n');
            sb.Append("dimension: ").Append(dim).Append('\n');
            bool geom = VolumeIO.HasGeometry(m);
            int spatial = Math.Min(grid, 3);
            int spaceDim = 0;
            if (geom)
            {
                spaceDim = m!.Direction?.GetLength(0) ?? m.Origin?.Length ?? Math.Max(spatial, 1);
                if (m.Direction != null) spatial = Math.Min(grid, m.Direction.GetLength(1));
                if (m.Space != null && IsKnownSpaceName(NrrdSpaceName(m.Space)) && SpaceDimOf(NrrdSpaceName(m.Space)) == spaceDim)
                    sb.Append("space: ").Append(NrrdSpaceName(m.Space)).Append('\n');
                else sb.Append("space dimension: ").Append(spaceDim).Append('\n');
            }
            sb.Append("sizes:");
            if (ch) sb.Append(' ').Append(v.Channels);
            foreach (var s in v.Sizes) sb.Append(' ').Append(s);
            sb.Append('\n');
            if (geom)
            {
                var vecs = VolumeIO.AxisVectors(m!, spatial, spaceDim);
                sb.Append("space directions:");
                if (ch) sb.Append(" none");
                for (int a = 0; a < grid; a++) sb.Append(' ').Append(a < spatial ? Vec(vecs, a) : "none");
                sb.Append('\n');
            }
            sb.Append("kinds:");
            if (ch) sb.Append(' ').Append(v.Color == ColorModel.Rgb ? "RGB-color" : v.Color == ColorModel.Rgba ? "RGBA-color" : "vector");
            for (int a = 0; a < grid; a++) sb.Append(" domain");
            sb.Append('\n');
            if (geom && grid > spatial)
            {
                // spacings for the axes without a direction (time, ...)
                bool any = false;
                for (int a = spatial; a < grid; a++) any |= a < m!.Spacing.Length && !double.IsNaN(m.Spacing[a]);
                if (any)
                {
                    sb.Append("spacings:");
                    if (ch) sb.Append(" nan");
                    for (int a = 0; a < grid; a++) sb.Append(' ').Append(a >= spatial && a < m!.Spacing.Length ? VolumeIO.Format(m.Spacing[a]) : "nan");
                    sb.Append('\n');
                }
            }
            if (RawImage.BytesPerSample(type) > 1 && o.Encoding != NrrdEncoding.Ascii) sb.Append("endian: ").Append(o.BigEndian ? "big" : "little").Append('\n');
            sb.Append("encoding: ").Append(o.Encoding switch { NrrdEncoding.Gzip => "gzip", NrrdEncoding.Ascii => "ascii", NrrdEncoding.Hex => "hex", _ => "raw" }).Append('\n');
            if (geom && m!.Origin != null && m.Origin.Length == spaceDim)
            {
                sb.Append("space origin: (");
                for (int i = 0; i < m.Origin.Length; i++) { if (i > 0) sb.Append(','); sb.Append(VolumeIO.Format(m.Origin[i])); }
                sb.Append(")\n");
            }
            if (geom && spatial > 0 && m!.Units.Length > 0 && m.Units[0] != null && spaceDim > 0)
            {
                sb.Append("space units:");
                for (int i = 0; i < spaceDim; i++) sb.Append(" \"").Append(m.Units[0]).Append('"');
                sb.Append('\n');
            }
            if (m?.ValueUnit != null) sb.Append("sample units: ").Append(m.ValueUnit).Append('\n');
            if (m != null && m.SourceFormat == ImageFormat.Nrrd)
                foreach (var kv in m.Header)
                    if (!StandardFields.Contains(kv.Key) && !kv.Key.Contains(":="))
                        sb.Append(kv.Key).Append(":=").Append(kv.Value.Replace("\\", "\\\\").Replace("\n", "\\n")).Append('\n');
            if (dataFile != null) sb.Append("data file: ").Append(dataFile).Append('\n');
            sb.Append('\n');
            return sb.ToString();
        }

        static void WriteData(Stream output, Array data, SampleType type, NrrdEncodeOptions o)
        {
            switch (o.Encoding)
            {
                case NrrdEncoding.Raw:
                    VolumeIO.WriteSamples(output, data, 0, data.LongLength, o.BigEndian);
                    break;
                case NrrdEncoding.Gzip:
                    using (var g = new GzipWriter(output, o.CompressionLevel, leaveOpen: true))
                        VolumeIO.WriteSamples(g, data, 0, data.LongLength, o.BigEndian);
                    break;
                case NrrdEncoding.Hex:
                    {
                        int bps = RawImage.BytesPerSample(type);
                        var tmp = new MemoryStream();
                        var line = new StringBuilder();
                        const string digits = "0123456789abcdef";
                        long chunk = Math.Max(1, (1 << 16) / bps);
                        int col = 0;
                        var w = new StreamWriter(output, Encoding.ASCII, 1 << 16, leaveOpen: true);
                        for (long off = 0; off < data.LongLength; off += chunk)
                        {
                            int n = (int)Math.Min(chunk, data.LongLength - off);
                            tmp.SetLength(0);
                            VolumeIO.WriteSamples(tmp, data, off, n, o.BigEndian);
                            foreach (var b in tmp.GetBuffer().AsSpan(0, (int)tmp.Length))
                            {
                                w.Write(digits[b >> 4]); w.Write(digits[b & 15]);
                                col += 2;
                                if (col >= 70) { w.Write('\n'); col = 0; }
                            }
                        }
                        if (col > 0) w.Write('\n');
                        w.Flush();
                        break;
                    }
                case NrrdEncoding.Ascii:
                    {
                        var w = new StreamWriter(output, Encoding.ASCII, 1 << 16, leaveOpen: true);
                        for (long i = 0; i < data.LongLength; i++)
                        {
                            object? x = data.GetValue(i);
                            string s = type switch
                            {
                                SampleType.Float32 => float.IsFinite((float)x!) ? ((float)x!).ToString("R", VolumeIO.Inv) : VolumeIO.Format((double)(float)x!),
                                SampleType.Float64 => VolumeIO.Format((double)x!),
                                _ => Convert.ToString(x, VolumeIO.Inv)!,
                            };
                            w.Write(s);
                            w.Write((i + 1) % 16 == 0 ? '\n' : ' ');
                        }
                        w.Write('\n');
                        w.Flush();
                        break;
                    }
            }
        }

        static byte[] ToBytes(Action<Stream> write)
        {
            var ms = new MemoryStream();
            write(ms);
            return ms.ToArray();
        }
    }
}
