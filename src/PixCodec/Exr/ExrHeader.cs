using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace PixCodec
{
    /// <summary>Storage type of an OpenEXR channel (values as in the file).</summary>
    public enum ExrPixelType { UInt = 0, Half = 1, Float = 2 }

    public enum ExrLineOrder { IncreasingY = 0, DecreasingY = 1, RandomY = 2 }

    public enum ExrLevelMode { OneLevel = 0, MipMap = 1, RipMap = 2 }

    public enum ExrPartType { ScanLine, Tiled, DeepScanLine, DeepTiled }

    /// <summary>Integer box with inclusive bounds, as used for data and display windows.</summary>
    public readonly record struct ExrBox2i(int MinX, int MinY, int MaxX, int MaxY)
    {
        public long Width => (long)MaxX - MinX + 1;
        public long Height => (long)MaxY - MinY + 1;
    }

    /// <summary>CIE xy chromaticities of the RGB primaries and the white point.</summary>
    public readonly record struct ExrChromaticities(
        float RedX, float RedY, float GreenX, float GreenY, float BlueX, float BlueY, float WhiteX, float WhiteY);

    /// <summary>One entry of a part's channel list.</summary>
    public sealed class ExrChannel
    {
        /// <summary>Full name as stored, e.g. "R" or "diffuse.R".</summary>
        public string Name { get; }
        public ExrPixelType PixelType { get; }
        /// <summary>The pLinear flag (only used as a hint by the B44 compressions).</summary>
        public bool PLinear { get; }
        public int XSampling { get; }
        public int YSampling { get; }

        /// <summary>Layer prefix: everything before the last '.', or "" for the root layer.</summary>
        public string Layer { get; }
        /// <summary>Name without the layer prefix.</summary>
        public string BaseName { get; }

        public ExrChannel(string name, ExrPixelType pixelType, bool pLinear = false, int xSampling = 1, int ySampling = 1)
        {
            Name = name; PixelType = pixelType; PLinear = pLinear; XSampling = xSampling; YSampling = ySampling;
            int dot = name.LastIndexOf('.');
            Layer = dot < 0 ? "" : name.Substring(0, dot);
            BaseName = dot < 0 ? name : name.Substring(dot + 1);
        }

        public override string ToString() => $"{Name}:{PixelType}";
    }

    /// <summary>
    /// A header attribute: name, type name and the raw little-endian value bytes exactly as stored in the file.
    /// The As* accessors interpret the bytes as one of the standard attribute types and throw
    /// <see cref="ImageFormatException"/> if the value has the wrong size.
    /// </summary>
    public sealed class ExrAttribute
    {
        public string Name { get; }
        public string TypeName { get; }
        public byte[] Value { get; }

        public ExrAttribute(string name, string typeName, byte[] value)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            TypeName = typeName ?? throw new ArgumentNullException(nameof(typeName));
            Value = value ?? throw new ArgumentNullException(nameof(value));
        }

        ReadOnlySpan<byte> Need(int size)
        {
            if (Value.Length < size) throw new ImageFormatException($"EXR attribute '{Name}' ({TypeName}) has {Value.Length} bytes, need {size}");
            return Value;
        }

        public int AsInt() => BinaryPrimitives.ReadInt32LittleEndian(Need(4));
        public float AsFloat() => BinaryPrimitives.ReadSingleLittleEndian(Need(4));
        public double AsDouble() => BinaryPrimitives.ReadDoubleLittleEndian(Need(8));
        public byte AsByte() => Need(1)[0];
        /// <summary>The value as UTF-8 text ("string" attributes are not zero terminated).</summary>
        public string AsString() => Encoding.UTF8.GetString(Value);
        public ExrBox2i AsBox2i() { var v = AsInts(4); return new ExrBox2i(v[0], v[1], v[2], v[3]); }
        public (int X, int Y) AsV2i() { var v = AsInts(2); return (v[0], v[1]); }
        public (float X, float Y) AsV2f() { var v = AsFloats(2); return (v[0], v[1]); }
        public (float X, float Y, float Z) AsV3f() { var v = AsFloats(3); return (v[0], v[1], v[2]); }
        /// <summary>Numerator / denominator of a "rational" attribute.</summary>
        public (int Numerator, uint Denominator) AsRational() { var v = AsInts(2); return (v[0], (uint)v[1]); }
        public ExrChromaticities AsChromaticities()
        {
            var v = AsFloats(8);
            return new ExrChromaticities(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7]);
        }

        /// <summary>Reads <paramref name="count"/> consecutive floats (v2f, v3f, m33f, m44f, chromaticities, ...).</summary>
        public float[] AsFloats(int count)
        {
            var s = Need(checked(count * 4));
            var r = new float[count];
            for (int i = 0; i < count; i++) r[i] = BinaryPrimitives.ReadSingleLittleEndian(s.Slice(i * 4));
            return r;
        }

        /// <summary>Reads <paramref name="count"/> consecutive int32 values (v2i, box2i, ...).</summary>
        public int[] AsInts(int count)
        {
            var s = Need(checked(count * 4));
            var r = new int[count];
            for (int i = 0; i < count; i++) r[i] = BinaryPrimitives.ReadInt32LittleEndian(s.Slice(i * 4));
            return r;
        }

        /// <summary>Decodes a "stringvector" attribute (int32 length prefixed strings).</summary>
        public string[] AsStringVector()
        {
            var list = new List<string>();
            ReadOnlySpan<byte> s = Value;
            while (s.Length > 0)
            {
                if (s.Length < 4) throw new ImageFormatException($"EXR attribute '{Name}': bad string vector");
                int n = BinaryPrimitives.ReadInt32LittleEndian(s);
                if (n < 0 || n > s.Length - 4) throw new ImageFormatException($"EXR attribute '{Name}': bad string vector");
                list.Add(Encoding.UTF8.GetString(s.Slice(4, n)));
                s = s.Slice(4 + n);
            }
            return list.ToArray();
        }

        public static ExrAttribute String(string name, string value) => new(name, "string", Encoding.UTF8.GetBytes(value));
        public static ExrAttribute Int(string name, int value) => new(name, "int", Ints(value));
        public static ExrAttribute Float(string name, float value) => new(name, "float", Floats(value));
        public static ExrAttribute Double(string name, double value)
        {
            var b = new byte[8];
            BinaryPrimitives.WriteDoubleLittleEndian(b, value);
            return new(name, "double", b);
        }
        public static ExrAttribute V2i(string name, int x, int y) => new(name, "v2i", Ints(x, y));
        public static ExrAttribute V2f(string name, float x, float y) => new(name, "v2f", Floats(x, y));
        public static ExrAttribute V3f(string name, float x, float y, float z) => new(name, "v3f", Floats(x, y, z));
        public static ExrAttribute Box2i(string name, ExrBox2i box) => new(name, "box2i", Ints(box.MinX, box.MinY, box.MaxX, box.MaxY));
        public static ExrAttribute Chromaticities(string name, ExrChromaticities c)
            => new(name, "chromaticities", Floats(c.RedX, c.RedY, c.GreenX, c.GreenY, c.BlueX, c.BlueY, c.WhiteX, c.WhiteY));
        /// <summary>A 4x4 float matrix (16 values, row major) as "m44f".</summary>
        public static ExrAttribute M44f(string name, ReadOnlySpan<float> values)
        {
            if (values.Length != 16) throw new ArgumentException("m44f needs 16 values");
            return new(name, "m44f", Floats(values.ToArray()));
        }

        static byte[] Ints(params int[] v)
        {
            var b = new byte[v.Length * 4];
            for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(i * 4), v[i]);
            return b;
        }

        static byte[] Floats(params float[] v)
        {
            var b = new byte[v.Length * 4];
            for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(i * 4), v[i]);
            return b;
        }

        public override string ToString() => $"{Name} ({TypeName}, {Value.Length} bytes)";
    }

    /// <summary>The header of one part of an OpenEXR file: the standard attributes parsed, all attributes raw.</summary>
    public sealed class ExrHeader
    {
        /// <summary>All attributes in file order, including the standard ones.</summary>
        public IReadOnlyList<ExrAttribute> Attributes { get; }
        /// <summary>Channels in file order (alphabetical by name).</summary>
        public IReadOnlyList<ExrChannel> Channels { get; }
        public ExrCompression Compression { get; }
        /// <summary>The region for which pixel data is stored. Decoded images cover exactly this window.</summary>
        public ExrBox2i DataWindow { get; }
        public ExrBox2i DisplayWindow { get; }
        public ExrLineOrder LineOrder { get; }
        public float PixelAspectRatio { get; }
        public (float X, float Y) ScreenWindowCenter { get; }
        public float ScreenWindowWidth { get; }
        /// <summary>The "chromaticities" attribute, if present.</summary>
        public ExrChromaticities? Chromaticities { get; }
        /// <summary>Part name (required in multi-part files, otherwise usually null).</summary>
        public string? Name { get; }
        public ExrPartType Type { get; }
        public bool IsTiled => Type == ExrPartType.Tiled || Type == ExrPartType.DeepTiled;
        public bool IsDeep => Type == ExrPartType.DeepScanLine || Type == ExrPartType.DeepTiled;
        /// <summary>Tile size and level description, 0 / OneLevel for scan line parts.</summary>
        public int TileWidth { get; }
        public int TileHeight { get; }
        public ExrLevelMode LevelMode { get; }
        /// <summary>True if level sizes round up (ROUND_UP), false for ROUND_DOWN.</summary>
        public bool LevelRoundUp { get; }
        /// <summary>Width / height of the data window.</summary>
        public int Width { get; }
        public int Height { get; }

        internal int ChunkCountAttribute = -1;
        internal ExrChan[] Chans;

        internal ExrHeader(List<ExrAttribute> attributes, bool tiledFlag, bool nonImageFlag, bool multiPart)
        {
            Attributes = attributes;
            ExrAttribute? Find(string name, int minSize)
            {
                foreach (var a in attributes)
                    if (a.Name == name)
                    {
                        if (a.Value.Length < minSize) throw new ImageFormatException($"EXR attribute '{name}' is too short");
                        return a;
                    }
                return null;
            }

            var ch = Find("channels", 1) ?? throw new ImageFormatException("EXR header has no channel list");
            Channels = ParseChannels(ch.Value);
            Chans = new ExrChan[Channels.Count];
            for (int i = 0; i < Chans.Length; i++)
            {
                var c = Channels[i];
                Chans[i] = new ExrChan { Type = (int)c.PixelType, XS = c.XSampling, YS = c.YSampling, PLinear = c.PLinear, Bytes = c.PixelType == ExrPixelType.Half ? 2 : 4 };
            }

            var comp = Find("compression", 1) ?? throw new ImageFormatException("EXR header has no compression attribute");
            Compression = (ExrCompression)comp.Value[0];

            var dw = Find("dataWindow", 16) ?? throw new ImageFormatException("EXR header has no data window");
            DataWindow = dw.AsBox2i();
            // The reference implementation limits coordinates to about +-2^30 as well; this keeps all later int math safe.
            const int lim = 1 << 30;
            if (DataWindow.MaxX < DataWindow.MinX || DataWindow.MaxY < DataWindow.MinY
                || DataWindow.MinX <= -lim || DataWindow.MinY <= -lim || DataWindow.MaxX >= lim || DataWindow.MaxY >= lim
                || DataWindow.Width >= lim || DataWindow.Height >= lim)
                throw new ImageFormatException("EXR data window is invalid");
            Width = (int)DataWindow.Width;
            Height = (int)DataWindow.Height;
            DisplayWindow = Find("displayWindow", 16)?.AsBox2i() ?? DataWindow;

            int lo = Find("lineOrder", 1)?.Value[0] ?? 0;
            if (lo > 2) throw new ImageFormatException("EXR line order is invalid");
            LineOrder = (ExrLineOrder)lo;
            PixelAspectRatio = Find("pixelAspectRatio", 4)?.AsFloat() ?? 1f;
            ScreenWindowCenter = Find("screenWindowCenter", 8)?.AsV2f() ?? (0f, 0f);
            ScreenWindowWidth = Find("screenWindowWidth", 4)?.AsFloat() ?? 1f;
            Chromaticities = Find("chromaticities", 32)?.AsChromaticities();
            Name = Find("name", 0)?.AsString();
            ChunkCountAttribute = Find("chunkCount", 4)?.AsInt() ?? -1;

            string? type = Find("type", 0)?.AsString();
            Type = type switch
            {
                "scanlineimage" => ExrPartType.ScanLine,
                "tiledimage" => ExrPartType.Tiled,
                "deepscanline" => ExrPartType.DeepScanLine,
                "deeptile" => ExrPartType.DeepTiled,
                null => tiledFlag ? ExrPartType.Tiled : ExrPartType.ScanLine,
                _ => throw new ImageNotSupportedException($"EXR part type '{type}' is not supported"),
            };
            if (!multiPart)
            {
                // In single part files the version flags are authoritative, the type attribute is optional.
                if (nonImageFlag) Type = tiledFlag || Type == ExrPartType.DeepTiled || Type == ExrPartType.Tiled ? ExrPartType.DeepTiled : ExrPartType.DeepScanLine;
                else if (tiledFlag) Type = ExrPartType.Tiled;
                else if (!IsDeep) Type = ExrPartType.ScanLine;
            }

            if (IsTiled)
            {
                var t = Find("tiles", 9) ?? throw new ImageFormatException("tiled EXR part has no tile description");
                uint tw = BinaryPrimitives.ReadUInt32LittleEndian(t.Value);
                uint th = BinaryPrimitives.ReadUInt32LittleEndian(t.Value.AsSpan(4));
                int mode = t.Value[8];
                if (tw == 0 || th == 0 || tw > int.MaxValue || th > int.MaxValue || (mode & 15) > 2 || (mode >> 4) > 1)
                    throw new ImageFormatException("EXR tile description is invalid");
                TileWidth = (int)tw; TileHeight = (int)th;
                LevelMode = (ExrLevelMode)(mode & 15);
                LevelRoundUp = (mode >> 4) == 1;
            }
        }

        public ExrAttribute? FindAttribute(string name)
        {
            foreach (var a in Attributes) if (a.Name == name) return a;
            return null;
        }

        /// <summary>Distinct layer prefixes of the channel list in file order ("" is the root layer).</summary>
        public IReadOnlyList<string> Layers
        {
            get
            {
                var l = new List<string>();
                var seen = new HashSet<string>();
                foreach (var c in Channels) if (seen.Add(c.Layer)) l.Add(c.Layer);
                return l;
            }
        }

        public ExrChannel? FindChannel(string name)
        {
            int i = ChannelIndex(name);
            return i < 0 ? null : Channels[i];
        }

        Dictionary<string, int>? _index;

        internal int ChannelIndex(string name)
        {
            var index = _index;
            if (index == null)
            {
                index = new Dictionary<string, int>(Channels.Count);
                for (int i = 0; i < Channels.Count; i++) index[Channels[i].Name] = i;
                _index = index;
            }
            return index.TryGetValue(name, out int r) ? r : -1;
        }

        static List<ExrChannel> ParseChannels(ReadOnlySpan<byte> v)
        {
            var list = new List<ExrChannel>();
            var seen = new HashSet<string>();
            int p = 0;
            while (true)
            {
                if (p >= v.Length) throw new ImageFormatException("EXR channel list is not terminated");
                if (v[p] == 0) break;
                int e = v.Slice(p).IndexOf((byte)0);
                if (e < 0 || e > 255 || v.Length - (p + e + 1) < 16) throw new ImageFormatException("EXR channel list is truncated");
                string name = Encoding.UTF8.GetString(v.Slice(p, e));
                p += e + 1;
                int type = BinaryPrimitives.ReadInt32LittleEndian(v.Slice(p));
                bool pLinear = v[p + 4] != 0;
                int xs = BinaryPrimitives.ReadInt32LittleEndian(v.Slice(p + 8));
                int ys = BinaryPrimitives.ReadInt32LittleEndian(v.Slice(p + 12));
                p += 16;
                if (type < 0 || type > 2) throw new ImageFormatException($"EXR channel '{name}' has an unknown pixel type");
                if (xs < 1 || ys < 1) throw new ImageFormatException($"EXR channel '{name}' has invalid sampling");
                if (!seen.Add(name)) throw new ImageFormatException($"EXR channel '{name}' is listed twice");
                list.Add(new ExrChannel(name, (ExrPixelType)type, pLinear, xs, ys));
            }
            if (list.Count == 0) throw new ImageFormatException("EXR part has no channels");
            return list;
        }
    }

    internal struct ExrChan
    {
        public int Type;      // ExrPixelType
        public int XS, YS;
        public bool PLinear;
        public int Bytes;     // per sample in the file
    }

    /// <summary>Parsed file structure: version, part headers and where the offset tables start.</summary>
    internal sealed class ExrFileStructure
    {
        public const int FlagTiled = 0x200, FlagLongNames = 0x400, FlagNonImage = 0x800, FlagMultiPart = 0x1000;

        public int Flags;
        public bool MultiPart => (Flags & FlagMultiPart) != 0;
        public ExrHeader[] Parts = Array.Empty<ExrHeader>();
        /// <summary>File position of the first offset table.</summary>
        public int TablesStart;

        public static bool IsMatch(ReadOnlySpan<byte> d)
            => d.Length >= 4 && d[0] == 0x76 && d[1] == 0x2f && d[2] == 0x31 && d[3] == 0x01;

        public static ExrFileStructure Parse(ReadOnlySpan<byte> d)
        {
            if (!IsMatch(d)) throw new ImageFormatException("not an OpenEXR file");
            if (d.Length < 9) throw new ImageFormatException("EXR file is truncated");
            int v = BinaryPrimitives.ReadInt32LittleEndian(d.Slice(4));
            int version = v & 0xff, flags = v & ~0xff;
            if (version > 2 || version < 1) throw new ImageNotSupportedException($"EXR file format version {version} is not supported");
            if ((flags & ~(FlagTiled | FlagLongNames | FlagNonImage | FlagMultiPart)) != 0)
                throw new ImageNotSupportedException("EXR file uses unknown version flags");
            var s = new ExrFileStructure { Flags = flags };
            bool multi = s.MultiPart;
            var parts = new List<ExrHeader>();
            int p = 8;
            while (true)
            {
                var attrs = new List<ExrAttribute>();
                while (true)
                {
                    string name = ReadName(d, ref p);
                    if (name.Length == 0) break;
                    string type = ReadName(d, ref p);
                    if (d.Length - p < 4) throw new ImageFormatException("EXR header is truncated");
                    int size = BinaryPrimitives.ReadInt32LittleEndian(d.Slice(p));
                    p += 4;
                    if (size < 0 || size > d.Length - p) throw new ImageFormatException($"EXR attribute '{name}' is truncated");
                    attrs.Add(new ExrAttribute(name, type, d.Slice(p, size).ToArray()));
                    p += size;
                }
                if (attrs.Count == 0)
                {
                    if (multi && parts.Count > 0) break;
                    throw new ImageFormatException("EXR header is empty");
                }
                parts.Add(new ExrHeader(attrs, (flags & FlagTiled) != 0, (flags & FlagNonImage) != 0, multi));
                if (!multi) break;
                if (p >= d.Length) throw new ImageFormatException("EXR header is truncated");
                if (d[p] == 0) { p++; break; }
            }
            s.Parts = parts.ToArray();
            s.TablesStart = p;
            return s;
        }

        static string ReadName(ReadOnlySpan<byte> d, ref int p)
        {
            if (p >= d.Length) throw new ImageFormatException("EXR header is truncated");
            var rest = d.Slice(p, Math.Min(256, d.Length - p));
            int e = rest.IndexOf((byte)0);
            if (e < 0) throw new ImageFormatException("EXR header name is too long or not terminated");
            p += e + 1;
            return Encoding.UTF8.GetString(rest.Slice(0, e));
        }
    }
}
