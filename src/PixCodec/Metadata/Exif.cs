using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PixCodec
{
    /// <summary>The directories of an EXIF block (and of a TIFF file's first page).</summary>
    public enum ExifIfd
    {
        /// <summary>IFD0: the main image (TIFF tags, camera make / model, date, ...).</summary>
        Image,
        /// <summary>IFD1: the embedded thumbnail.</summary>
        Thumbnail,
        /// <summary>The EXIF private directory (exposure, lens, capture date, ...).</summary>
        Exif,
        /// <summary>The GPS directory.</summary>
        Gps,
        /// <summary>The interoperability directory.</summary>
        Interop,
    }

    /// <summary>A TIFF / EXIF field type.</summary>
    public enum ExifType : ushort
    {
        Byte = 1, Ascii = 2, Short = 3, Long = 4, Rational = 5, SByte = 6, Undefined = 7, SShort = 8, SLong = 9,
        SRational = 10, Float = 11, Double = 12, Ifd = 13, Long8 = 16, SLong8 = 17, Ifd8 = 18, Utf8 = 129,
    }

    /// <summary>An unsigned or signed rational number as stored (numerator / denominator, not reduced).</summary>
    public readonly record struct ExifRational(long Numerator, long Denominator)
    {
        /// <summary>The value; NaN for a zero denominator.</summary>
        public double Value => Denominator == 0 ? double.NaN : (double)Numerator / Denominator;
        public override string ToString() => $"{Numerator}/{Denominator}";
    }

    /// <summary>
    /// One field of an EXIF / TIFF directory. <see cref="Value"/> holds the decoded values: a string for ASCII and UTF-8,
    /// byte[] for BYTE and UNDEFINED, sbyte[], ushort[], short[], uint[], int[], ulong[], long[], float[], double[] for the
    /// numeric types and <see cref="ExifRational"/>[] for the rationals.
    /// </summary>
    public sealed class ExifEntry
    {
        public ExifIfd Ifd { get; }
        public ushort Tag { get; }
        public ExifType Type { get; }
        public long Count { get; }
        public object Value { get; }

        internal ExifEntry(ExifIfd ifd, ushort tag, ExifType type, long count, object value)
        {
            Ifd = ifd; Tag = tag; Type = type; Count = count; Value = value;
        }

        /// <summary>The tag's name from the EXIF / TIFF / GPS specifications, or "Tag0x1234" for unknown tags.</summary>
        public string Name => ExifTagNames.Name(Ifd, Tag);

        /// <summary>Number of values (characters for text).</summary>
        public int Length => Value switch { string s => s.Length, Array a => a.Length, _ => 0 };

        /// <summary>Value <paramref name="i"/> as a double (NaN when there is none or it is not numeric).</summary>
        public double GetDouble(int i = 0) => Value switch
        {
            ExifRational[] r when i < r.Length => r[i].Value,
            byte[] b when i < b.Length && Type == ExifType.Byte => b[i],
            sbyte[] b when i < b.Length => b[i],
            ushort[] s when i < s.Length => s[i],
            short[] s when i < s.Length => s[i],
            uint[] u when i < u.Length => u[i],
            int[] u when i < u.Length => u[i],
            ulong[] u when i < u.Length => u[i],
            long[] u when i < u.Length => u[i],
            float[] f when i < f.Length => f[i],
            double[] f when i < f.Length => f[i],
            string s when i == 0 && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => double.NaN,
        };

        /// <summary>The value as text: strings as they are (up to the first NUL), UNDEFINED / BYTE data that is text (EXIF
        /// UserComment with its character code prefix, XP* UTF-16 tags), numbers joined by spaces.</summary>
        public override string ToString() => Value switch
        {
            string s => s,
            byte[] b when Type == ExifType.Byte && Tag is not (>= 0x9C9B and <= 0x9C9F) && !(Ifd == ExifIfd.Gps && Tag == 0) => JoinNumbers(b),
            byte[] b => ExifReader.BytesAsText(Tag, b),
            ExifRational[] r => string.Join(" ", r),
            Array a => JoinNumbers(a),
            _ => "",
        };

        static string JoinNumbers(Array a)
        {
            var sb = new StringBuilder();
            int n = Math.Min(a.Length, 256);
            for (int i = 0; i < n; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(Convert.ToString(a.GetValue(i), CultureInfo.InvariantCulture));
            }
            if (a.Length > n) sb.Append(" ...");
            return sb.ToString();
        }
    }

    /// <summary>The parsed directories of an EXIF block (or the first page of a TIFF file).</summary>
    public sealed class ExifData
    {
        readonly List<ExifEntry> _entries;
        internal ExifData(List<ExifEntry> entries, bool bigEndian, int thumbnailOffset, int thumbnailLength, byte[] raw)
        {
            _entries = entries; BigEndian = bigEndian; _thumbOffset = thumbnailOffset; _thumbLength = thumbnailLength; _raw = raw;
        }

        readonly int _thumbOffset, _thumbLength;
        readonly byte[] _raw;

        public IReadOnlyList<ExifEntry> Entries => _entries;

        /// <summary>Motorola (big endian) byte order.</summary>
        public bool BigEndian { get; }

        /// <summary>The field, or null.</summary>
        public ExifEntry? Get(ExifIfd ifd, ushort tag)
        {
            foreach (var e in _entries) if (e.Ifd == ifd && e.Tag == tag) return e;
            return null;
        }

        /// <summary>The field by name (as <see cref="ExifEntry.Name"/>), in any directory; null if absent.</summary>
        public ExifEntry? Get(string name)
        {
            foreach (var e in _entries) if (string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) return e;
            return null;
        }

        /// <summary>The embedded JPEG thumbnail (IFD1), if any.</summary>
        public byte[]? Thumbnail
            => _thumbLength > 0 && _thumbOffset >= 0 && _thumbOffset + (long)_thumbLength <= _raw.Length
                ? _raw.AsSpan(_thumbOffset, _thumbLength).ToArray() : null;

        /// <summary>Parses an EXIF block starting at the TIFF header ("II*\0" / "MM\0*"); null when it is not one.</summary>
        public static ExifData? Parse(ReadOnlySpan<byte> tiff) => ExifReader.Parse(tiff, false);
    }

    internal static class ExifReader
    {
        const int MaxEntriesPerIfd = 4096;
        const long MaxValues = 1 << 20;

        /// <summary>
        /// Parses IFD0 and IFD1, following the EXIF, GPS and interoperability pointers. Damaged fields are skipped.
        /// <paramref name="tiffFile"/>: a TIFF file, where only the first page counts (its next IFD is a page, not a thumbnail).
        /// </summary>
        public static ExifData? Parse(ReadOnlySpan<byte> d, bool tiffFile)
        {
            if (d.Length < 8) return null;
            bool be;
            if (d[0] == 'I' && d[1] == 'I') be = false;
            else if (d[0] == 'M' && d[1] == 'M') be = true;
            else return null;
            int magic = U16(d, 2, be);
            bool big = magic == 43;
            if (magic != 42 && !big) return null;
            long first;
            if (big)
            {
                if (d.Length < 16) return null;
                first = (long)U64(d, 8, be);
            }
            else first = U32(d, 4, be);

            var entries = new List<ExifEntry>();
            var visited = new HashSet<long>();
            long next = ReadIfd(d, first, ExifIfd.Image, be, big, entries, visited);
            int thumbOffset = -1, thumbLength = 0;
            if (!tiffFile && next > 0)
            {
                ReadIfd(d, next, ExifIfd.Thumbnail, be, big, entries, visited);
                var off = Find(entries, ExifIfd.Thumbnail, 0x0201);
                var len = Find(entries, ExifIfd.Thumbnail, 0x0202);
                if (off != null && len != null)
                {
                    double o = off.GetDouble(), l = len.GetDouble();
                    if (o > 0 && l > 0 && o + l <= d.Length) { thumbOffset = (int)o; thumbLength = (int)l; }
                }
            }
            Follow(d, ExifIfd.Image, 0x8769, ExifIfd.Exif, be, big, entries, visited);
            Follow(d, ExifIfd.Image, 0x8825, ExifIfd.Gps, be, big, entries, visited);
            Follow(d, ExifIfd.Exif, 0xA005, ExifIfd.Interop, be, big, entries, visited);
            return new ExifData(entries, be, thumbOffset, thumbLength, thumbLength > 0 ? d.ToArray() : Array.Empty<byte>());
        }

        static ExifEntry? Find(List<ExifEntry> es, ExifIfd ifd, ushort tag)
        {
            foreach (var e in es) if (e.Ifd == ifd && e.Tag == tag) return e;
            return null;
        }

        static void Follow(ReadOnlySpan<byte> d, ExifIfd from, ushort tag, ExifIfd to, bool be, bool big, List<ExifEntry> es, HashSet<long> visited)
        {
            var p = Find(es, from, tag);
            if (p == null) return;
            double off = p.GetDouble();
            if (off > 0 && off < d.Length) ReadIfd(d, (long)off, to, be, big, es, visited);
        }

        /// <summary>Reads one directory; returns the offset of the next one (0 if none).</summary>
        static long ReadIfd(ReadOnlySpan<byte> d, long pos, ExifIfd ifd, bool be, bool big, List<ExifEntry> es, HashSet<long> visited)
        {
            if (pos <= 0 || pos >= d.Length || !visited.Add(pos)) return 0;
            int countSize = big ? 8 : 2, entrySize = big ? 20 : 12, valueSize = big ? 8 : 4;
            if (pos + countSize > d.Length) return 0;
            long n = big ? (long)U64(d, (int)pos, be) : U16(d, (int)pos, be);
            if (n > MaxEntriesPerIfd) return 0;
            long p = pos + countSize;
            for (long k = 0; k < n; k++, p += entrySize)
            {
                if (p + entrySize > d.Length) return 0;
                int ip = (int)p;
                ushort tag = (ushort)U16(d, ip, be);
                var type = (ExifType)U16(d, ip + 2, be);
                long count = big ? (long)Math.Min(U64(d, ip + 4, be), long.MaxValue) : U32(d, ip + 4, be);
                int size = TypeSize(type);
                if (size == 0 || count < 0 || count > MaxValues * 8) continue;
                long bytes = count * size;
                long at = bytes <= valueSize ? ip + 4 + (big ? 8 : 4) : big ? (long)U64(d, ip + 12, be) : U32(d, ip + 8, be);
                if (at < 0 || at + bytes > d.Length) continue;
                var v = ReadValue(d.Slice((int)at, (int)bytes), type, (int)count, be);
                if (v != null) es.Add(new ExifEntry(ifd, tag, type, count, v));
            }
            if (p + (big ? 8 : 4) > d.Length) return 0;
            return big ? (long)U64(d, (int)p, be) : U32(d, (int)p, be);
        }

        static int TypeSize(ExifType t) => t switch
        {
            ExifType.Byte or ExifType.Ascii or ExifType.SByte or ExifType.Undefined or ExifType.Utf8 => 1,
            ExifType.Short or ExifType.SShort => 2,
            ExifType.Long or ExifType.SLong or ExifType.Float or ExifType.Ifd => 4,
            ExifType.Rational or ExifType.SRational or ExifType.Double or ExifType.Long8 or ExifType.SLong8 or ExifType.Ifd8 => 8,
            _ => 0,
        };

        static object? ReadValue(ReadOnlySpan<byte> s, ExifType type, int n, bool be)
        {
            switch (type)
            {
                case ExifType.Ascii: return Text(s, Encoding.Latin1);
                case ExifType.Utf8: return Text(s, Encoding.UTF8);
                case ExifType.Byte:
                case ExifType.Undefined: return s.ToArray();
                case ExifType.SByte: { var r = new sbyte[n]; for (int i = 0; i < n; i++) r[i] = (sbyte)s[i]; return r; }
                case ExifType.Short: { var r = new ushort[n]; for (int i = 0; i < n; i++) r[i] = (ushort)U16(s, i * 2, be); return r; }
                case ExifType.SShort: { var r = new short[n]; for (int i = 0; i < n; i++) r[i] = (short)U16(s, i * 2, be); return r; }
                case ExifType.Long:
                case ExifType.Ifd: { var r = new uint[n]; for (int i = 0; i < n; i++) r[i] = U32(s, i * 4, be); return r; }
                case ExifType.SLong: { var r = new int[n]; for (int i = 0; i < n; i++) r[i] = (int)U32(s, i * 4, be); return r; }
                case ExifType.Long8:
                case ExifType.Ifd8: { var r = new ulong[n]; for (int i = 0; i < n; i++) r[i] = U64(s, i * 8, be); return r; }
                case ExifType.SLong8: { var r = new long[n]; for (int i = 0; i < n; i++) r[i] = (long)U64(s, i * 8, be); return r; }
                case ExifType.Float:
                    { var r = new float[n]; for (int i = 0; i < n; i++) r[i] = BitConverter.Int32BitsToSingle((int)U32(s, i * 4, be)); return r; }
                case ExifType.Double:
                    { var r = new double[n]; for (int i = 0; i < n; i++) r[i] = BitConverter.Int64BitsToDouble((long)U64(s, i * 8, be)); return r; }
                case ExifType.Rational:
                    { var r = new ExifRational[n]; for (int i = 0; i < n; i++) r[i] = new ExifRational(U32(s, i * 8, be), U32(s, i * 8 + 4, be)); return r; }
                case ExifType.SRational:
                    { var r = new ExifRational[n]; for (int i = 0; i < n; i++) r[i] = new ExifRational((int)U32(s, i * 8, be), (int)U32(s, i * 8 + 4, be)); return r; }
                default: return null;
            }
        }

        /// <summary>Text up to the first NUL, trailing blanks removed.</summary>
        static string Text(ReadOnlySpan<byte> s, Encoding enc)
        {
            int z = s.IndexOf((byte)0);
            if (z >= 0) s = s.Slice(0, z);
            // ASCII fields often hold UTF-8 in practice: take it as such when it decodes cleanly
            if (enc == Encoding.Latin1 && !IsAscii(s) && IsValidUtf8(s)) enc = Encoding.UTF8;
            return enc.GetString(s).TrimEnd(' ');
        }

        static bool IsAscii(ReadOnlySpan<byte> s)
        {
            foreach (var b in s) if (b >= 0x80) return false;
            return true;
        }

        static bool IsValidUtf8(ReadOnlySpan<byte> s) => System.Text.Unicode.Utf8.IsValid(s);

        /// <summary>Text held in UNDEFINED / BYTE fields: UserComment (8 byte character code prefix), XP* (UTF-16LE),
        /// version strings; other data as hex (at most 64 bytes).</summary>
        public static string BytesAsText(ushort tag, byte[] b)
        {
            if (tag is 0x9286 or 0x9C9B or 0x9C9C or 0x9C9D or 0x9C9E or 0x9C9F) return ExifText(tag, b) ?? "";
            if (tag is 0x9000 or 0xA000 or 0x0000 or 0x0002 or 0x9101 && b.Length <= 8)
            {
                if (tag == 0x0000 && b.Length == 4) return string.Join(".", b);   // GPSVersionID (BYTE in the GPS directory)
                if (tag == 0x9101) return string.Join(" ", b);
                return Encoding.ASCII.GetString(b).TrimEnd('\0');
            }
            var sb = new StringBuilder();
            for (int i = 0; i < Math.Min(64, b.Length); i++) sb.Append(b[i].ToString("x2"));
            if (b.Length > 64) sb.Append("...");
            return sb.ToString();
        }

        /// <summary>UserComment / GPSProcessingMethod / GPSAreaInformation (character code prefix) and XP* (UTF-16LE) text.</summary>
        public static string? ExifText(ushort tag, byte[] b)
        {
            if (tag is >= 0x9C9B and <= 0x9C9F) return Encoding.Unicode.GetString(b).TrimEnd('\0', ' ');
            if (b.Length < 8) return Encoding.ASCII.GetString(b).TrimEnd('\0', ' ');
            var code = Encoding.ASCII.GetString(b, 0, 8).TrimEnd('\0');
            var body = b.AsSpan(8);
            string s = code switch
            {
                "UNICODE" => DecodeUtf16(body),
                "JIS" => Encoding.Latin1.GetString(body),
                _ => IsValidUtf8(Cut(body)) ? Encoding.UTF8.GetString(Cut(body)) : Encoding.Latin1.GetString(Cut(body)),
            };
            return s.TrimEnd('\0', ' ');
        }

        /// <summary>Up to the first NUL (8 bit text).</summary>
        static ReadOnlySpan<byte> Cut(ReadOnlySpan<byte> b) { int z = b.IndexOf((byte)0); return z >= 0 ? b.Slice(0, z) : b; }

        static string DecodeUtf16(ReadOnlySpan<byte> b)
        {
            // byte order: a BOM, else guess from where the zero bytes are
            if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b.Slice(2));
            if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b.Slice(2));
            int evenZero = 0, oddZero = 0;
            for (int i = 0; i + 1 < b.Length; i += 2) { if (b[i] == 0) evenZero++; if (b[i + 1] == 0) oddZero++; }
            return evenZero > oddZero ? Encoding.BigEndianUnicode.GetString(b) : Encoding.Unicode.GetString(b);
        }

        static int U16(ReadOnlySpan<byte> d, int o, bool be) => be ? BinaryPrimitives.ReadUInt16BigEndian(d.Slice(o)) : BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(o));
        static uint U32(ReadOnlySpan<byte> d, int o, bool be) => be ? BinaryPrimitives.ReadUInt32BigEndian(d.Slice(o)) : BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(o));
        static ulong U64(ReadOnlySpan<byte> d, int o, bool be) => be ? BinaryPrimitives.ReadUInt64BigEndian(d.Slice(o)) : BinaryPrimitives.ReadUInt64LittleEndian(d.Slice(o));
    }
}
