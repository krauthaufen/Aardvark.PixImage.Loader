using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Aardvark.ImageCodecs.Compression;

namespace Aardvark.ImageCodecs
{
    public sealed class MetaImageEncodeOptions
    {
        /// <summary>zlib compress the samples (CompressedData = True).</summary>
        public bool Compress { get; set; } = true;
        public int CompressionLevel { get; set; } = 6;
    }

    /// <summary>
    /// MetaImage (ITK / VTK, .mha with the samples attached, .mhd with them in separate files). Reads every MET_ element type,
    /// ElementNumberOfChannels, both byte orders, zlib compressed data (CompressedData), HeaderSize (also -1), and
    /// ElementDataFile LOCAL / a file / LIST [subdim] / a printf pattern with min max step [subdim].
    /// <para>
    /// Axes: DimSize order, x fastest, without any flip (ITK's index order). Offset / Position / Origin, TransformMatrix /
    /// Rotation / Orientation (axis direction vectors, as ITK writes them) and ElementSpacing (else ElementSize) go to
    /// <see cref="PhysicalMetadata"/> in LPS, ITK's world space; the tags themselves (AnatomicalOrientation, ...) are in its
    /// Header.
    /// </para>
    /// Writes .mha (attached) and .mhd + .raw / .zraw (<see cref="EncodeDetached(RawVolume, string, MetaImageEncodeOptions?)"/>), compressed by default.
    /// </summary>
    public static class MetaImage
    {
        const string Fmt = "MetaImage";

        static readonly HashSet<string> FirstKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "ObjectType", "NDims", "Comment", "ObjectSubType", "TransformType", "Name", "ID", "ParentID", "CompressedData",
            "BinaryData", "ElementByteOrderMSB", "BinaryDataByteOrderMSB", "Color", "Position", "Offset", "Origin",
            "Orientation", "Rotation", "TransformMatrix", "DimSize", "ElementSpacing", "ElementType",
        };

        /// <summary>A text header whose first line is "Key = value" with a MetaImage tag, and an NDims and ElementType somewhere.</summary>
        public static bool IsMatch(ReadOnlySpan<byte> header)
        {
            int nl = header.IndexOf((byte)'\n');
            var first = nl < 0 ? header : header.Slice(0, nl);
            if (first.Length > 256) return false;
            int eq = first.IndexOf((byte)'=');
            if (eq <= 0) return false;
            foreach (var b in first) if (b < 9 || b > 126) return false;
            var key = Encoding.ASCII.GetString(first.Slice(0, eq)).Trim();
            if (!FirstKeys.Contains(key)) return false;
            if (key.Equals("ObjectType", StringComparison.OrdinalIgnoreCase))
                return Encoding.ASCII.GetString(first.Slice(eq + 1)).Trim().Equals("Image", StringComparison.OrdinalIgnoreCase);
            var probe = header.Slice(0, Math.Min(header.Length, 8192));
            return probe.IndexOf("NDims"u8) >= 0 && probe.IndexOf("ElementType"u8) >= 0;
        }

        internal sealed class Header
        {
            public int NDims;
            public int[] Dims = Array.Empty<int>();
            public SampleType Type;
            public int Channels = 1;
            public bool BigEndian, Compressed, Binary = true;
            public long CompressedSize = -1, HeaderSize;
            public double[]? Spacing, Offset, Matrix;
            public List<string> Files = new();
            public bool Local = true;
            public int SubDim;
            public long DataOffset;
            public List<KeyValuePair<string, string>> Entries = new();
        }

        static SampleType ParseType(string t) => t.Trim().ToUpperInvariant() switch
        {
            "MET_UCHAR" or "MET_UCHAR_ARRAY" => SampleType.UInt8,
            "MET_CHAR" or "MET_CHAR_ARRAY" => SampleType.Int8,
            "MET_USHORT" or "MET_USHORT_ARRAY" => SampleType.UInt16,
            "MET_SHORT" or "MET_SHORT_ARRAY" => SampleType.Int16,
            "MET_UINT" or "MET_UINT_ARRAY" or "MET_ULONG" or "MET_ULONG_ARRAY" => SampleType.UInt32,
            "MET_INT" or "MET_INT_ARRAY" or "MET_LONG" or "MET_LONG_ARRAY" => SampleType.Int32,
            "MET_ULONG_LONG" or "MET_ULONG_LONG_ARRAY" => SampleType.UInt64,
            "MET_LONG_LONG" or "MET_LONG_LONG_ARRAY" => SampleType.Int64,
            "MET_FLOAT" or "MET_FLOAT_ARRAY" => SampleType.Float32,
            "MET_DOUBLE" or "MET_DOUBLE_ARRAY" => SampleType.Float64,
            var x => throw new ImageNotSupportedException($"MetaImage: element type '{x}' is not supported"),
        };

        static string TypeName(SampleType t) => t switch
        {
            SampleType.UInt8 => "MET_UCHAR", SampleType.Int8 => "MET_CHAR", SampleType.UInt16 => "MET_USHORT", SampleType.Int16 => "MET_SHORT",
            SampleType.UInt32 => "MET_UINT", SampleType.Int32 => "MET_INT", SampleType.UInt64 => "MET_ULONG_LONG", SampleType.Int64 => "MET_LONG_LONG",
            SampleType.Float32 => "MET_FLOAT", SampleType.Float64 => "MET_DOUBLE",
            _ => throw new ArgumentException($"MetaImage: cannot write {t}"),
        };

        static bool Bool(string v) => v.Trim().ToLowerInvariant() is "true" or "t" or "1" or "yes";

        static double[] Numbers(string v) => Array.ConvertAll(VolumeIO.Words(v), w => VolumeIO.ParseDouble(w, Fmt, "a number list"));

        internal static Header Parse(ReadOnlySpan<byte> d, bool complete = true)
        {
            var h = new Header();
            int pos = 0;
            bool sawData = false;
            while (pos < d.Length)
            {
                int nl = d.Slice(pos).IndexOf((byte)'\n');
                if (nl < 0 && !complete) throw new ImageFormatException("MetaImage: header incomplete");
                int len = nl < 0 ? d.Length - pos : nl;
                var lb = d.Slice(pos, len);
                pos += nl < 0 ? len : len + 1;
                if (lb.Length > 0 && lb[^1] == '\r') lb = lb.Slice(0, lb.Length - 1);
                string line = Encoding.Latin1.GetString(lb);
                if (line.Trim().Length == 0) continue;
                int eq = line.IndexOf('=');
                if (eq < 0) throw new ImageFormatException($"MetaImage: bad header line '{line}'");
                string key = line.Substring(0, eq).Trim(), val = line.Substring(eq + 1).Trim();
                h.Entries.Add(new(key, val));
                switch (key.ToLowerInvariant())
                {
                    case "ndims":
                        if (!int.TryParse(val, System.Globalization.NumberStyles.Integer, VolumeIO.Inv, out h.NDims) || h.NDims < 1 || h.NDims > 16)
                            throw new ImageFormatException("MetaImage: bad NDims");
                        break;
                    case "dimsize":
                        {
                            var w = VolumeIO.Words(val);
                            h.Dims = new int[w.Length];
                            for (int i = 0; i < w.Length; i++)
                                if (!int.TryParse(w[i], System.Globalization.NumberStyles.Integer, VolumeIO.Inv, out h.Dims[i]) || h.Dims[i] <= 0)
                                    throw new ImageFormatException("MetaImage: bad DimSize");
                            break;
                        }
                    case "elementtype": h.Type = ParseType(val); break;
                    case "elementnumberofchannels":
                        if (!int.TryParse(val, System.Globalization.NumberStyles.Integer, VolumeIO.Inv, out h.Channels) || h.Channels < 1 || h.Channels > 4096)
                            throw new ImageFormatException("MetaImage: bad ElementNumberOfChannels");
                        break;
                    case "binarydatabyteordermsb": case "elementbyteordermsb": case "byteordermsb": h.BigEndian = Bool(val); break;
                    case "binarydata": h.Binary = Bool(val); break;
                    case "compresseddata": h.Compressed = Bool(val); break;
                    case "compresseddatasize": h.CompressedSize = (long)VolumeIO.ParseDouble(val, Fmt, key); break;
                    case "headersize": h.HeaderSize = (long)VolumeIO.ParseDouble(val, Fmt, key); break;
                    case "elementspacing": h.Spacing = Numbers(val); break;
                    case "elementsize": h.Spacing ??= Numbers(val); break;
                    case "offset": case "position": case "origin": h.Offset = Numbers(val); break;
                    case "transformmatrix": case "rotation": case "orientation": h.Matrix = Numbers(val); break;
                    case "elementdatafile":
                        {
                            sawData = true;
                            var w = VolumeIO.Words(val);
                            if (val.Equals("LOCAL", StringComparison.OrdinalIgnoreCase)) h.Local = true;
                            else if (w.Length >= 1 && w[0].Equals("LIST", StringComparison.OrdinalIgnoreCase))
                            {
                                h.Local = false;
                                if (w.Length > 1) h.SubDim = int.Parse(w[1], VolumeIO.Inv);
                                // the file names follow, one per line (several per line are allowed too)
                                while (pos < d.Length)
                                {
                                    int n2 = d.Slice(pos).IndexOf((byte)'\n');
                                    int l2 = n2 < 0 ? d.Length - pos : n2;
                                    var nm = Encoding.Latin1.GetString(d.Slice(pos, l2)).Trim();
                                    pos += n2 < 0 ? l2 : l2 + 1;
                                    foreach (var f in VolumeIO.Words(nm)) h.Files.Add(f);
                                }
                            }
                            else if (w.Length >= 4 && w[0].Contains('%') && int.TryParse(w[1], out int mn) && int.TryParse(w[2], out int mx) && int.TryParse(w[3], out int st))
                            {
                                h.Local = false;
                                if (st == 0 || st > 0 && mn > mx || st < 0 && mx > mn) throw new ImageFormatException("MetaImage: bad file pattern range");
                                if (w.Length >= 5) h.SubDim = int.Parse(w[4], VolumeIO.Inv);
                                for (long v = mn; st > 0 ? v <= mx : v >= mx; v += st)
                                {
                                    h.Files.Add(Nrrd.CFormat(w[0], (int)v));
                                    if (h.Files.Count > 1 << 20) throw new ImageFormatException("MetaImage: too many data files");
                                }
                            }
                            else { h.Local = false; h.Files.Add(val); }
                            break;
                        }
                }
                if (sawData) break;
            }
            if (!sawData) throw new ImageFormatException("MetaImage: no ElementDataFile");
            if (h.NDims == 0) h.NDims = h.Dims.Length;
            if (h.Dims.Length != h.NDims || h.NDims == 0) throw new ImageFormatException("MetaImage: DimSize does not match NDims");
            if (!h.Entries.Exists(e => e.Key.Equals("ElementType", StringComparison.OrdinalIgnoreCase))) throw new ImageFormatException("MetaImage: no ElementType");
            if (!h.Binary) throw new ImageNotSupportedException("MetaImage: ASCII data (BinaryData = False) is not supported");
            h.DataOffset = pos;
            return h;
        }

        static PhysicalMetadata Metadata(Header h)
        {
            int n = h.NDims;
            var m = new PhysicalMetadata { SourceFormat = ImageFormat.MetaImage, Space = "LPS", Header = h.Entries };
            m.Spacing = new double[n];
            m.Units = new string?[n];
            for (int i = 0; i < n; i++) m.Spacing[i] = h.Spacing != null && i < h.Spacing.Length ? h.Spacing[i] : double.NaN;
            if (h.Offset != null && h.Offset.Length >= n) m.Origin = h.Offset.AsSpan(0, n).ToArray();
            if (h.Matrix != null && h.Matrix.Length >= n * n)
            {
                // the matrix lists the direction of axis 0 first, then axis 1, ...
                var dir = new double[n, n];
                for (int a = 0; a < n; a++)
                    for (int r = 0; r < n; r++) dir[r, a] = h.Matrix[a * n + r];
                m.Direction = dir;
            }
            return m;
        }

        // ================================================================ info

        public static VolumeInfo GetVolumeInfo(ReadOnlySpan<byte> data, VolumeDecodeOptions? options = null) => Info(Parse(data));

        static VolumeInfo Info(Header h) => new()
        {
            Format = ImageFormat.MetaImage, Sizes = h.Dims, Channels = h.Channels, SampleType = h.Type, StoredType = h.Type,
            Color = RawImage.DefaultColorModel(h.Channels), Encoding = h.Compressed ? "zlib" : "raw", Detached = !h.Local, Metadata = Metadata(h),
        };

        public static ImageInfo GetInfo(ReadOnlySpan<byte> data) => GetInfo(data, false);

        internal static ImageInfo GetInfo(ReadOnlySpan<byte> data, bool prefix)
        {
            var h = Parse(data, !prefix);
            return Info(h).ToImageInfo(RawImage.BytesPerSample(h.Type) * 8);
        }

        // ================================================================ decode

        public static RawImage Decode(ReadOnlySpan<byte> data, VolumeDecodeOptions? options = null) => DecodeVolume(data, options).GetSlice(0);

        public static RawVolume DecodeVolume(ReadOnlySpan<byte> data, VolumeDecodeOptions? options = null)
        {
            options ??= VolumeDecodeOptions.Default;
            var h = Parse(data);
            if (!h.Local) return Read(h, null, options);
            long off = h.DataOffset;
            return VolumeIO.WithStream(data, s => { s.Position = off; return Read(h, s, options); });
        }

        /// <summary>Decodes a .mha or .mhd file; detached data files are found relative to it.</summary>
        public static RawVolume DecodeVolume(string path, VolumeDecodeOptions? options = null)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            var prefix = Nrrd.ReadPrefix(fs, p => { try { Parse(p, false); return true; } catch (ImageFormatException) { return false; } });
            var h = Parse(prefix);
            options = Nrrd.WithDirectory(options, path);
            if (!h.Local) return Read(h, null, options);
            fs.Position = h.DataOffset;
            return Read(h, fs, options);
        }

        static RawVolume Read(Header h, Stream? attached, VolumeDecodeOptions o)
        {
            long total = VolumeIO.Product(h.Dims, h.Channels);
            var arr = VolumeIO.Allocate(h.Type, total, o.MaxSamples, Fmt);
            int bps = RawImage.BytesPerSample(h.Type);
            if (h.Local) ReadPart(h, attached!, arr, 0, total, bps, true);
            else
            {
                int files = h.Files.Count;
                if (files == 0 || total % files != 0) throw new ImageFormatException("MetaImage: the samples do not divide evenly among the data files");
                long share = total / files;
                for (int f = 0; f < files; f++)
                {
                    using var s = o.Open(h.Files[f], Fmt);
                    ReadPart(h, s, arr, f * share, share, bps, files == 1);
                }
            }
            return new RawVolume(h.Dims, RawImage.DefaultColorModel(h.Channels), arr, h.Channels, Metadata(h));
        }

        static void ReadPart(Header h, Stream s, Array arr, long offset, long count, int bps, bool single)
        {
            if (h.HeaderSize == -1 && !h.Compressed)
            {
                if (!s.CanSeek) throw new ImageNotSupportedException("MetaImage: HeaderSize -1 needs a seekable data file");
                long start = s.Length - count * bps;
                if (start < s.Position) throw new ImageFormatException("MetaImage: data file too short");
                s.Position = start;
            }
            else VolumeIO.Skip(s, h.HeaderSize, Fmt);
            if (h.Compressed)
            {
                // a zlib stream (CompressedDataSize bytes for all of a single file's data)
                using var z = new ZlibReader(s);
                VolumeIO.ReadSamples(z, arr, offset, count, h.BigEndian, Fmt);
            }
            else VolumeIO.ReadSamples(s, arr, offset, count, h.BigEndian, Fmt);
        }

        /// <summary>Streaming zlib decompression.</summary>
        sealed class ZlibReader : Stream
        {
            readonly Stream input;
            readonly Inflater inf = new(zlibWrapper: true, verifyChecksum: true);
            readonly byte[] buf = new byte[1 << 16];
            int pos, end;
            bool done;
            public ZlibReader(Stream input) => this.input = input;
            public override int Read(Span<byte> output)
            {
                if (done || output.IsEmpty) return 0;
                while (true)
                {
                    if (pos >= end) { end = input.Read(buf, 0, buf.Length); pos = 0; if (end < 0) end = 0; }
                    var st = inf.Inflate(buf.AsSpan(pos, end - pos), output, out int c, out int w);
                    pos += c;
                    if (st == InflateStatus.Done) done = true;
                    if (w > 0 || done) return w;
                    if (st == InflateStatus.NeedInput && end == 0) throw new ImageFormatException("MetaImage: compressed data ends early");
                }
            }
            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
            public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        // ================================================================ encode

        public static void Encode(RawImage image, Stream output, MetaImageEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            Encode(RawVolume.FromImage(image), output, options);
        }

        public static byte[] Encode(RawImage image, MetaImageEncodeOptions? options = null) => ToBytes(s => Encode(image, s, options));
        public static byte[] Encode(RawVolume volume, MetaImageEncodeOptions? options = null) => ToBytes(s => Encode(volume, s, options));

        /// <summary>Writes a .mha (header and samples in one file).</summary>
        public static void Encode(RawVolume volume, Stream output, MetaImageEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(volume);
            ArgumentNullException.ThrowIfNull(output);
            options ??= new MetaImageEncodeOptions();
            var (data, type) = Writable(volume);
            var payload = Payload(data, options, out long csize);
            output.Write(Encoding.Latin1.GetBytes(HeaderText(volume, type, options, csize, "LOCAL")));
            WritePayload(output, data, payload);
        }

        /// <summary>Writes a .mhd header and the samples next to it (&lt;name&gt;.raw, or .zraw when compressed).</summary>
        public static void EncodeDetached(RawVolume volume, string headerPath, MetaImageEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(volume);
            options ??= new MetaImageEncodeOptions();
            var (data, type) = Writable(volume);
            string name = Path.GetFileNameWithoutExtension(headerPath) + (options.Compress ? ".zraw" : ".raw");
            var payload = Payload(data, options, out long csize);
            File.WriteAllText(headerPath, HeaderText(volume, type, options, csize, name), Encoding.Latin1);
            using var fs = new FileStream(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(headerPath))!, name), FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            WritePayload(fs, data, payload);
        }

        /// <inheritdoc cref="EncodeDetached(RawVolume, string, MetaImageEncodeOptions?)"/>
        public static void EncodeDetached(RawImage image, string headerPath, MetaImageEncodeOptions? options = null)
            => EncodeDetached(RawVolume.FromImage(image), headerPath, options);

        static (Array, SampleType) Writable(RawVolume v)
            => v.SampleType == SampleType.Float16 ? (VolumeIO.Convert(v.Data, SampleType.Float32), SampleType.Float32) : (v.Data, v.SampleType);

        /// <summary>The compressed samples (the header needs their size first), or null for raw.</summary>
        static MemoryStream? Payload(Array data, MetaImageEncodeOptions o, out long size)
        {
            size = -1;
            if (!o.Compress) return null;
            var ms = new MemoryStream();
            using (var z = Zlib.CreateCompressor(ms, o.CompressionLevel))
                VolumeIO.WriteSamples(z, data, 0, data.LongLength, false);
            size = ms.Length;
            return ms;
        }

        static void WritePayload(Stream output, Array data, MemoryStream? payload)
        {
            if (payload != null) { payload.Position = 0; payload.CopyTo(output); }
            else VolumeIO.WriteSamples(output, data, 0, data.LongLength, false);
        }

        static readonly HashSet<string> Managed = new(StringComparer.OrdinalIgnoreCase)
        {
            "ObjectType", "NDims", "BinaryData", "BinaryDataByteOrderMSB", "ElementByteOrderMSB", "ByteOrderMSB", "CompressedData",
            "CompressedDataSize", "TransformMatrix", "Rotation", "Orientation", "Offset", "Position", "Origin", "ElementSpacing",
            "ElementSize", "DimSize", "ElementNumberOfChannels", "ElementType", "ElementDataFile", "HeaderSize", "CenterOfRotation",
        };

        static string HeaderText(RawVolume v, SampleType type, MetaImageEncodeOptions o, long csize, string dataFile)
        {
            var m = v.Metadata;
            int n = v.Sizes.Length;
            var sb = new StringBuilder();
            void L(string k, string val) => sb.Append(k).Append(" = ").Append(val).Append('\n');
            L("ObjectType", "Image");
            L("NDims", n.ToString(VolumeIO.Inv));
            L("BinaryData", "True");
            L("BinaryDataByteOrderMSB", "False");
            L("CompressedData", o.Compress ? "True" : "False");
            if (o.Compress) L("CompressedDataSize", csize.ToString(VolumeIO.Inv));
            // world space: LPS
            var flips = m == null ? null : PhysicalMetadata.Flips(m.Space, "LPS");
            var dir = new StringBuilder();
            for (int a = 0; a < n; a++)
                for (int r = 0; r < n; r++)
                {
                    double d = m?.Direction != null && a < m.Direction.GetLength(1) ? (r < m.Direction.GetLength(0) ? m.Direction[r, a] : 0) : (r == a ? 1 : 0);
                    if (flips != null && r < 3) d *= flips[r];
                    dir.Append(a + r > 0 ? " " : "").Append(VolumeIO.Format(d + 0.0));
                }
            L("TransformMatrix", dir.ToString());
            var off = new StringBuilder();
            for (int r = 0; r < n; r++)
            {
                double x = m?.Origin != null && r < m.Origin.Length ? m.Origin[r] : 0;
                if (flips != null && r < 3) x *= flips[r];
                off.Append(r > 0 ? " " : "").Append(VolumeIO.Format(x + 0.0));
            }
            L("Offset", off.ToString());
            var sp = new StringBuilder();
            for (int a = 0; a < n; a++)
            {
                double s = m != null && a < m.Spacing.Length && !double.IsNaN(m.Spacing[a]) ? m.Spacing[a] : 1;
                sp.Append(a > 0 ? " " : "").Append(VolumeIO.Format(s));
            }
            L("ElementSpacing", sp.ToString());
            if (m?.SourceFormat == ImageFormat.MetaImage)
                foreach (var kv in m.Header)
                    if (!Managed.Contains(kv.Key)) L(kv.Key, kv.Value);
            L("DimSize", string.Join(" ", Array.ConvertAll(v.Sizes, x => x.ToString(VolumeIO.Inv))));
            if (v.Channels > 1) L("ElementNumberOfChannels", v.Channels.ToString(VolumeIO.Inv));
            L("ElementType", TypeName(type));
            L("ElementDataFile", dataFile);
            return sb.ToString();
        }

        static byte[] ToBytes(Action<Stream> write)
        {
            var ms = new MemoryStream();
            write(ms);
            return ms.ToArray();
        }
    }
}
