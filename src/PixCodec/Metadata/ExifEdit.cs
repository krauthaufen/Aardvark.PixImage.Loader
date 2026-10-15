using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace PixCodec
{
    /// <summary>A field value to write: type, count and the value bytes in little endian order.</summary>
    internal sealed class ExifValue
    {
        public ExifType Type;
        public long Count;
        public byte[] Data = Array.Empty<byte>();
        /// <summary>UNDEFINED text with an 8 byte "UNICODE\0" prefix: the UTF-16 units follow the block's byte order.</summary>
        public bool Utf16Body;

        public static int TypeSize(ExifType t) => t switch
        {
            ExifType.Byte or ExifType.Ascii or ExifType.SByte or ExifType.Undefined or ExifType.Utf8 => 1,
            ExifType.Short or ExifType.SShort => 2,
            ExifType.Long or ExifType.SLong or ExifType.Float or ExifType.Ifd => 4,
            ExifType.Rational or ExifType.SRational or ExifType.Double or ExifType.Long8 or ExifType.SLong8 or ExifType.Ifd8 => 8,
            _ => 0,
        };

        /// <summary>Bytes swapped as a unit for big endian output (the two halves of a rational separately).</summary>
        public static int UnitSize(ExifType t) => t switch
        {
            ExifType.Short or ExifType.SShort => 2,
            ExifType.Long or ExifType.SLong or ExifType.Float or ExifType.Ifd or ExifType.Rational or ExifType.SRational => 4,
            ExifType.Double or ExifType.Long8 or ExifType.SLong8 or ExifType.Ifd8 => 8,
            _ => 1,
        };

        /// <summary>The value bytes in the given byte order.</summary>
        public byte[] Bytes(bool bigEndian)
        {
            if (!bigEndian) return Data;
            if (!Utf16Body) return Swap(Data, Type);
            var r = (byte[])Data.Clone();
            for (int k = 8; k + 2 <= r.Length; k += 2) (r[k], r[k + 1]) = (r[k + 1], r[k]);
            return r;
        }

        public static byte[] Swap(byte[] d, ExifType t)
        {
            int u = UnitSize(t);
            if (u == 1) return d;
            var r = (byte[])d.Clone();
            for (int k = 0; k + u <= r.Length; k += u) r.AsSpan(k, u).Reverse();
            return r;
        }

        public static ExifValue Ascii(string s, ExifType type = ExifType.Ascii)
        {
            var b = Encoding.UTF8.GetBytes(s);
            var d = new byte[b.Length + 1];
            b.CopyTo(d, 0);
            return new ExifValue { Type = type, Count = d.Length, Data = d };
        }

        public static ExifValue Undefined(byte[] b) => new() { Type = ExifType.Undefined, Count = b.Length, Data = (byte[])b.Clone() };
        public static ExifValue Bytes(params byte[] b) => new() { Type = ExifType.Byte, Count = b.Length, Data = (byte[])b.Clone() };

        public static ExifValue Shorts(params int[] v)
        {
            var d = new byte[v.Length * 2];
            for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(i * 2), checked((ushort)v[i]));
            return new ExifValue { Type = ExifType.Short, Count = v.Length, Data = d };
        }

        public static ExifValue Longs(params uint[] v)
        {
            var d = new byte[v.Length * 4];
            for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(i * 4), v[i]);
            return new ExifValue { Type = ExifType.Long, Count = v.Length, Data = d };
        }

        public static ExifValue Rationals(bool signed, params ExifRational[] v)
        {
            var d = new byte[v.Length * 8];
            for (int i = 0; i < v.Length; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(i * 8), unchecked((uint)v[i].Numerator));
                BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(i * 8 + 4), unchecked((uint)v[i].Denominator));
            }
            return new ExifValue { Type = signed ? ExifType.SRational : ExifType.Rational, Count = v.Length, Data = d };
        }

        public static ExifValue Rationals(bool signed, params double[] v)
        {
            var r = new ExifRational[v.Length];
            for (int i = 0; i < v.Length; i++) r[i] = Rational(v[i], signed);
            return Rationals(signed, r);
        }

        /// <summary>
        /// A fraction for a value: exact for decimals of up to 9 places that fit, else the closest one with a power of ten
        /// denominator; values below 1 that are reciprocals of whole numbers (exposure times) as 1/n.
        /// </summary>
        public static ExifRational Rational(double v, bool signed)
        {
            double limit = signed ? int.MaxValue : uint.MaxValue;
            if (double.IsNaN(v) || double.IsInfinity(v)) throw new ArgumentException("not a finite number");
            if (!signed && v < 0) throw new ArgumentException("negative value for an unsigned rational");
            if (Math.Abs(v) >= limit) return new ExifRational((long)(Math.Sign(v) * limit), 1);
            if (v != 0 && Math.Abs(v) < 1)
            {
                double inv = 1 / Math.Abs(v);
                if (Math.Abs(inv - Math.Round(inv)) < 1e-9 * inv && Math.Round(inv) <= limit) return new ExifRational(Math.Sign(v), (long)Math.Round(inv));
            }
            long den = 1;
            while (den < 1_000_000_000 && Math.Abs(v) * den * 10 <= limit && Math.Abs(v * den - Math.Round(v * den)) > 1e-9 * Math.Max(1, Math.Abs(v * den))) den *= 10;
            long num = (long)Math.Round(v * den);
            long a = Math.Abs(num), b = den;
            while (b != 0) (a, b) = (b, a % b);
            if (a > 1) { num /= a; den /= a; }
            return new ExifRational(num, den);
        }

        /// <summary>A caller supplied value (see <see cref="MetadataEdit.SetExif"/>).</summary>
        public static ExifValue From(ExifType type, object value)
        {
            T[] Arr<T>(object v) => v switch
            {
                T[] a => a,
                T x => new[] { x },
                _ => throw new ArgumentException($"EXIF {type}: a {typeof(T).Name} or {typeof(T).Name}[] value is needed, not {v.GetType().Name}"),
            };
            byte[] Raw<T>(T[] a, int size, SpanWriter<T> w)
            {
                var d = new byte[a.Length * size];
                for (int i = 0; i < a.Length; i++) w(d.AsSpan(i * size), a[i]);
                return d;
            }
            ExifValue V(byte[] d) => new() { Type = type, Count = d.Length / Math.Max(1, TypeSize(type)), Data = d };
            switch (type)
            {
                case ExifType.Ascii:
                case ExifType.Utf8:
                    return Ascii(value as string ?? throw new ArgumentException($"EXIF {type}: a string value is needed"), type);
                case ExifType.Byte:
                case ExifType.Undefined:
                    if (value is string s) return V(Encoding.ASCII.GetBytes(s));
                    return V((byte[])Arr<byte>(value).Clone());
                case ExifType.SByte: return V(Array.ConvertAll(Arr<sbyte>(value), x => (byte)x));
                case ExifType.Short:
                    if (value is int or int[]) return V(Raw(Arr<int>(value), 2, (d, x) => BinaryPrimitives.WriteUInt16LittleEndian(d, checked((ushort)x))));
                    return V(Raw(Arr<ushort>(value), 2, (d, x) => BinaryPrimitives.WriteUInt16LittleEndian(d, x)));
                case ExifType.SShort: return V(Raw(Arr<short>(value), 2, (d, x) => BinaryPrimitives.WriteInt16LittleEndian(d, x)));
                case ExifType.Long:
                    if (value is int or int[]) return V(Raw(Arr<int>(value), 4, (d, x) => BinaryPrimitives.WriteUInt32LittleEndian(d, checked((uint)x))));
                    return V(Raw(Arr<uint>(value), 4, (d, x) => BinaryPrimitives.WriteUInt32LittleEndian(d, x)));
                case ExifType.SLong: return V(Raw(Arr<int>(value), 4, (d, x) => BinaryPrimitives.WriteInt32LittleEndian(d, x)));
                case ExifType.Float: return V(Raw(Arr<float>(value), 4, (d, x) => BinaryPrimitives.WriteSingleLittleEndian(d, x)));
                case ExifType.Double: return V(Raw(Arr<double>(value), 8, (d, x) => BinaryPrimitives.WriteDoubleLittleEndian(d, x)));
                case ExifType.Rational:
                case ExifType.SRational:
                    bool signed = type == ExifType.SRational;
                    if (value is double or double[]) return Rationals(signed, Arr<double>(value));
                    return Rationals(signed, Arr<ExifRational>(value));
                default: throw new ArgumentException($"EXIF field type {type} cannot be written");
            }
        }

        public bool SameAs(ExifValue o) => Type == o.Type && Count == o.Count && Data.AsSpan().SequenceEqual(o.Data);
    }

    internal delegate void SpanWriter<T>(Span<byte> d, T v);

    /// <summary>One EXIF change: a value to set, or null to remove; <see cref="IfPresent"/>: only where the tag exists.</summary>
    /// <remarks>
    /// <see cref="MinVersion"/>: the EXIF version (230 for 2.3) that defines the tag. When the EXIF directory states an older
    /// one, a positive value raises its ExifVersion, a negative one leaves the tag out.
    /// </remarks>
    internal readonly record struct ExifOp(ExifIfd Ifd, ushort Tag, ExifValue? Value, bool IfPresent = false, int MinVersion = 0)
    {
        /// <summary>An ExifVersion value ("0232", "0300", ...) as a number, 0 if it is not one.</summary>
        public static int VersionOf(ReadOnlySpan<byte> version)
            => version.Length >= 4 && int.TryParse(System.Text.Encoding.ASCII.GetString(version.Slice(0, 4)), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int v) ? v : 0;

        public static byte[] VersionBytes(int v) => System.Text.Encoding.ASCII.GetBytes(v.ToString("D4", System.Globalization.CultureInfo.InvariantCulture));

        /// <summary>A GPSVersionID (BYTE[4], 2.2.0.0) as a number (220).</summary>
        public static int GpsVersionOf(ReadOnlySpan<byte> v) => v.Length >= 3 ? v[0] * 100 + v[1] * 10 + v[2] : 0;

        public static ExifValue GpsVersionBytes(int v) => ExifValue.Bytes((byte)(v / 100), (byte)(v / 10 % 10), (byte)(v % 10), 0);
    }

    /// <summary>Optional facts about the image an EXIF block is written for (to fill in the tags readers require).</summary>
    internal sealed class ExifContext
    {
        public bool Jpeg;
        public int Width, Height;
        public double DpiX, DpiY;
        public bool HasIcc;
    }

    /// <summary>Edits EXIF blocks (standalone TIFF structures as JPEG, PNG, WebP and JPEG 2000 carry them).</summary>
    internal static class ExifEdit
    {
        public const ushort ExifPointer = 0x8769, GpsPointer = 0x8825, InteropPointer = 0xA005, MakerNote = 0x927C;

        /// <summary>Tags that describe the directory structure or image layout and are written by the writers themselves.</summary>
        public static bool IsStructural(ExifIfd ifd, ushort tag)
        {
            if (tag is ExifPointer or GpsPointer or InteropPointer or 0x014A) return true;
            if (ifd is ExifIfd.Image or ExifIfd.Thumbnail)
                return tag is 254 or 255 or 256 or 257 or 258 or 259 or 262 or 273 or 277 or 278 or 279 or 284 or 317 or 320 or 322 or 323 or 324 or 325
                    or 338 or 339 or 347 or 513 or 514 or 530 or 532 or 34675 or 700 or 33723 or 34377;
            return false;
        }

        public static void CheckSettable(ExifIfd ifd, ushort tag)
        {
            if (IsStructural(ifd, tag))
                throw new ArgumentException($"EXIF tag 0x{tag:X4} in {ifd} describes the file structure (or is ICC / XMP / IPTC data) and cannot be set this way");
        }

        // ------------------------------------------------------------------ model

        internal sealed class Entry
        {
            public ushort Tag;
            public ExifValue Value = null!;
            /// <summary>Where the value was in the original block (-1: inline or new).</summary>
            public long Origin = -1;
            /// <summary>A pointer to data this writer does not know (sub directories, image data): kept only with the layout.</summary>
            public bool Opaque;
        }

        internal sealed class Dirs
        {
            public bool BigEndian;
            public readonly Dictionary<ExifIfd, List<Entry>> Ifds = new()
            {
                [ExifIfd.Image] = new(), [ExifIfd.Exif] = new(), [ExifIfd.Gps] = new(), [ExifIfd.Interop] = new(), [ExifIfd.Thumbnail] = new(),
            };
            /// <summary>The thumbnail data and whether it is stored as a strip (else JPEGInterchangeFormat).</summary>
            public byte[]? Thumb;
            public bool ThumbStrip;
            /// <summary>Where the thumbnail was in the original block (-1: none) and its length there.</summary>
            public long ThumbOrigin = -1, ThumbOriginLength;
            public readonly HashSet<ExifIfd> Existed = new();

            /// <summary>The block parsed, the places of its directories and out of line values.</summary>
            public byte[]? Original;
            public readonly List<(long Start, long End)> OldDirs = new();
            public readonly Dictionary<ExifIfd, (long At, long Size)> OldDirAt = new();
            public readonly List<(long Start, long End)> OldValues = new();
            /// <summary>
            /// The block has data this writer does not lay out itself (a maker note, whose offsets often count from the block
            /// start, sub directories, or bytes no known field refers to): everything then stays where it is and the
            /// directories and changed values are appended.
            /// </summary>
            public bool KeepLayout;

            public List<Entry> this[ExifIfd i] => Ifds[i];

            public Entry? Find(ExifIfd ifd, ushort tag)
            {
                foreach (var e in Ifds[ifd]) if (e.Tag == tag) return e;
                return null;
            }

            public void Set(ExifIfd ifd, ushort tag, ExifValue v)
            {
                var l = Ifds[ifd];
                for (int i = 0; i < l.Count; i++)
                    if (l[i].Tag == tag)
                    {
                        if (!l[i].Value.SameAs(v)) l[i] = new Entry { Tag = tag, Value = v };
                        return;
                    }
                l.Add(new Entry { Tag = tag, Value = v });
            }

            public void Remove(ExifIfd ifd, ushort tag) => Ifds[ifd].RemoveAll(e => e.Tag == tag);

            public bool IsEmpty
            {
                get
                {
                    foreach (var l in Ifds.Values) if (l.Count > 0) return false;
                    return true;
                }
            }
        }

        // ------------------------------------------------------------------ parsing

        const int MaxEntries = 4096;

        static int U16(ReadOnlySpan<byte> d, long o, bool be) => be ? BinaryPrimitives.ReadUInt16BigEndian(d.Slice((int)o)) : BinaryPrimitives.ReadUInt16LittleEndian(d.Slice((int)o));
        static uint U32(ReadOnlySpan<byte> d, long o, bool be) => be ? BinaryPrimitives.ReadUInt32BigEndian(d.Slice((int)o)) : BinaryPrimitives.ReadUInt32LittleEndian(d.Slice((int)o));

        /// <summary>Parses an EXIF block into its directories; null when it is not a (classic) TIFF structure.</summary>
        public static Dirs? Parse(byte[] d)
        {
            if (d.Length < 8) return null;
            bool be;
            if (d[0] == 'I' && d[1] == 'I') be = false;
            else if (d[0] == 'M' && d[1] == 'M') be = true;
            else return null;
            if (U16(d, 2, be) != 42) return null;
            var r = new Dirs { BigEndian = be, Original = d };
            var visited = new HashSet<long>();
            long ifd0 = U32(d, 4, be);
            long next = ReadIfd(d, ifd0, ExifIfd.Image, r, visited, out var p0);
            if (p0.TryGetValue(ExifPointer, out long ep)) { ReadIfd(d, ep, ExifIfd.Exif, r, visited, out var pe); if (pe.TryGetValue(InteropPointer, out long ip)) ReadIfd(d, ip, ExifIfd.Interop, r, visited, out _); }
            if (p0.TryGetValue(GpsPointer, out long gp)) ReadIfd(d, gp, ExifIfd.Gps, r, visited, out _);
            if (next > 0)
            {
                ReadIfd(d, next, ExifIfd.Thumbnail, r, visited, out var pt);
                long to = 0, tl = -1;
                if (pt.TryGetValue(0x0201, out to) && pt.TryGetValue(0x0202, out tl)) { }
                else if (pt.TryGetValue(0x0111, out to) && pt.TryGetValue(0x0117, out tl)) r.ThumbStrip = true;
                else tl = -1;
                if (tl == 0) r.Thumb = Array.Empty<byte>();
                else if (tl > 0 && to > 0 && to + tl <= d.Length) { r.Thumb = d.AsSpan((int)to, (int)tl).ToArray(); r.ThumbOrigin = to; r.ThumbOriginLength = tl; }
                else if (pt.Count > 0) r[ExifIfd.Thumbnail].Clear();   // thumbnail data out of reach: drop the directory
            }
            foreach (var (k, l) in r.Ifds) if (l.Count > 0) r.Existed.Add(k);

            // is every byte accounted for?
            bool keep = r.Find(ExifIfd.Exif, MakerNote) != null;
            foreach (var l in r.Ifds.Values) foreach (var e in l) keep |= e.Opaque;
            var known = new List<(long, long)>(r.OldDirs) { (0, 8) };
            known.AddRange(r.OldValues);
            if (r.ThumbOrigin >= 0) known.Add((r.ThumbOrigin, r.ThumbOrigin + r.ThumbOriginLength));
            r.KeepLayout = keep || Unaccounted(d, known);
            return r;
        }

        /// <summary>Whether bytes outside the known ranges hold anything but zero / 0xFF padding.</summary>
        static bool Unaccounted(byte[] d, List<(long Start, long End)> known)
        {
            known.Sort();
            long p = 0;
            foreach (var (s, e) in known)
            {
                for (long i = p; i < Math.Min(s, d.Length); i++) if (d[i] != 0 && d[i] != 0xFF) return true;
                p = Math.Max(p, e);
            }
            for (long i = p; i < d.Length; i++) if (d[i] != 0 && d[i] != 0xFF) return true;
            return false;
        }

        /// <summary>Reads a directory; pointer and thumbnail location tags go to <paramref name="pointers"/>. Returns the next IFD offset.</summary>
        static long ReadIfd(ReadOnlySpan<byte> d, long pos, ExifIfd ifd, Dirs r, HashSet<long> visited, out Dictionary<ushort, long> pointers)
        {
            pointers = new();
            if (pos < 8 || pos + 2 > d.Length || !visited.Add(pos)) return 0;
            bool be = r.BigEndian;
            int n = U16(d, pos, be);
            if (n > MaxEntries || pos + 2 + 12L * n > d.Length) return 0;
            r.OldDirs.Add((pos, Math.Min(d.Length, pos + 2 + 12L * n + 4)));
            r.OldDirAt[ifd] = (pos, Math.Min(d.Length, pos + 2 + 12L * n + 4) - pos);
            long p = pos + 2;
            var list = r[ifd];
            for (int k = 0; k < n; k++, p += 12)
            {
                ushort tag = (ushort)U16(d, p, be);
                var type = (ExifType)U16(d, p + 2, be);
                long count = U32(d, p + 4, be);
                int size = ExifValue.TypeSize(type);
                if (size == 0 || type is ExifType.Long8 or ExifType.SLong8 or ExifType.Ifd8) continue;
                long bytes = count * size;
                long at = bytes <= 4 ? p + 8 : U32(d, p + 8, be);
                if (at < 0 || bytes > d.Length || at + bytes > d.Length) continue;
                bool isPointer = ifd == ExifIfd.Image && tag is ExifPointer or GpsPointer || ifd == ExifIfd.Exif && tag == InteropPointer
                    || ifd == ExifIfd.Thumbnail && tag is 0x0201 or 0x0202 or 0x0111 or 0x0117;
                if (isPointer)
                {
                    if (count >= 1 && type is ExifType.Long or ExifType.Ifd or ExifType.Short)
                        pointers[tag] = type == ExifType.Short ? U16(d, at, be) : U32(d, at, be);
                    continue;
                }
                var data = d.Slice((int)at, (int)bytes).ToArray();
                if (be) data = ExifValue.Swap(data, type);
                if (bytes > 4) r.OldValues.Add((at, at + bytes));
                // sub directories and image data cannot be followed generically: kept only with the layout
                bool opaque = type == ExifType.Ifd || tag is 0x014A or 273 or 279 or 324 or 325 or 513 or 514;
                list.Add(new Entry { Tag = tag, Value = new ExifValue { Type = type, Count = count, Data = data }, Origin = bytes > 4 ? at : -1, Opaque = opaque });
            }
            return p + 4 <= d.Length ? U32(d, p, be) : 0;
        }

        // ------------------------------------------------------------------ editing

        /// <summary>
        /// The EXIF block with the plan applied; <paramref name="exif"/> may be null (a block is then made when the plan sets
        /// EXIF values). Null when nothing remains. A block that cannot be parsed is kept as it is, or replaced when the
        /// plan writes EXIF values (dropped by RemoveAll).
        /// </summary>
        public static byte[]? EditBlob(byte[]? exif, MetadataPlan plan, ExifContext? ctx)
        {
            var dirs = exif is { Length: > 0 } ? Parse(exif) : null;
            if (dirs == null && !plan.CreatesExif) return plan.ClearAll ? null : exif;
            if (dirs != null && !plan.HasExifChanges) return exif;
            dirs ??= new Dirs();
            Apply(dirs, plan, ctx);
            if (dirs.IsEmpty) return null;
            return Serialize(dirs);
        }

        /// <summary>The ExifVersion (232 for "0232") or GPSVersionID (220 for 2.2.0.0) as a number, 0 if absent.</summary>
        static int Version(Dirs dirs, ExifIfd ifd)
            => ifd == ExifIfd.Gps ? dirs.Find(ExifIfd.Gps, 0) is Entry g ? ExifOp.GpsVersionOf(g.Value.Data) : 0
                : dirs.Find(ExifIfd.Exif, 0x9000) is Entry v ? ExifOp.VersionOf(v.Value.Data) : 0;

        /// <summary>Applies the plan's EXIF changes to the directories.</summary>
        public static void Apply(Dirs dirs, MetadataPlan plan, ExifContext? ctx)
        {
            if (plan.ClearAll)
            {
                foreach (var (k, l) in dirs.Ifds)
                    if (k == ExifIfd.Image) l.RemoveAll(e => e.Tag is not (0x0112 or 0x011A or 0x011B or 0x0128 or 0x0213));
                    else l.Clear();
                dirs.Thumb = null;
            }
            if (plan.ClearGps) dirs[ExifIfd.Gps].Clear();
            if (plan.ClearThumbnail) { dirs[ExifIfd.Thumbnail].Clear(); dirs.Thumb = null; }
            int bump = 0, bumpGps = 0;
            foreach (var op in plan.Exif)
            {
                if (op.Value != null && op.MinVersion != 0)
                {
                    int have = Version(dirs, op.Ifd), need = Math.Abs(op.MinVersion);
                    if (have < need)
                    {
                        // not worth claiming a newer EXIF version for (new directories are 2.32)
                        if (op.MinVersion < 0) continue;
                        if (have != 0) { if (op.Ifd == ExifIfd.Gps) bumpGps = Math.Max(bumpGps, need); else bump = Math.Max(bump, need); }
                    }
                }
                if (op.Value == null) dirs.Remove(op.Ifd, op.Tag);
                else if (!op.IfPresent || dirs.Find(op.Ifd, op.Tag) != null) dirs.Set(op.Ifd, op.Tag, op.Value);
            }
            // tags of a later version than the directory states: the directory now follows that version
            if (bump > 0 && dirs[ExifIfd.Exif].Count > 0 && Version(dirs, ExifIfd.Exif) < bump)
                dirs.Set(ExifIfd.Exif, 0x9000, ExifValue.Undefined(ExifOp.VersionBytes(bump)));
            if (bumpGps > 0 && dirs[ExifIfd.Gps].Count > 0 && Version(dirs, ExifIfd.Gps) < bumpGps)
                dirs.Set(ExifIfd.Gps, 0, ExifOp.GpsVersionBytes(bumpGps));
            if (dirs.Thumb == null) dirs[ExifIfd.Thumbnail].Clear();
            if (dirs[ExifIfd.Exif].Count == 0) dirs[ExifIfd.Interop].Clear();
            Complete(dirs, ctx);
        }

        /// <summary>The tags the EXIF specification requires in directories this writer creates.</summary>
        static void Complete(Dirs dirs, ExifContext? ctx)
        {
            var ex = dirs[ExifIfd.Exif];
            bool newExif = ex.Count > 0 && !dirs.Existed.Contains(ExifIfd.Exif);
            if (ex.Count > 0 && dirs.Find(ExifIfd.Exif, 0x9000) == null) dirs.Set(ExifIfd.Exif, 0x9000, ExifValue.Undefined("0232"u8.ToArray()));
            if (dirs[ExifIfd.Gps].Count > 0 && dirs.Find(ExifIfd.Gps, 0x0000) == null) dirs.Set(ExifIfd.Gps, 0x0000, ExifValue.Bytes(2, 3, 0, 0));
            if (ctx is not { Jpeg: true }) return;
            // what EXIF requires of a JPEG file's blocks, for the directories made here
            if (!dirs.Existed.Contains(ExifIfd.Image))
            {
                double dx = ctx.DpiX > 0 ? ctx.DpiX : 72, dy = ctx.DpiY > 0 ? ctx.DpiY : 72;
                if (dirs.Find(ExifIfd.Image, 0x011A) == null) dirs.Set(ExifIfd.Image, 0x011A, ExifValue.Rationals(false, dx));
                if (dirs.Find(ExifIfd.Image, 0x011B) == null) dirs.Set(ExifIfd.Image, 0x011B, ExifValue.Rationals(false, dy));
                if (dirs.Find(ExifIfd.Image, 0x0128) == null) dirs.Set(ExifIfd.Image, 0x0128, ExifValue.Shorts(2));
                if (dirs.Find(ExifIfd.Image, 0x0213) == null) dirs.Set(ExifIfd.Image, 0x0213, ExifValue.Shorts(1));
            }
            if (newExif)
            {
                if (dirs.Find(ExifIfd.Exif, 0x9101) == null) dirs.Set(ExifIfd.Exif, 0x9101, ExifValue.Undefined(new byte[] { 1, 2, 3, 0 }));
                if (dirs.Find(ExifIfd.Exif, 0xA000) == null) dirs.Set(ExifIfd.Exif, 0xA000, ExifValue.Undefined("0100"u8.ToArray()));
                if (dirs.Find(ExifIfd.Exif, 0xA001) == null) dirs.Set(ExifIfd.Exif, 0xA001, ExifValue.Shorts(ctx.HasIcc ? 0xFFFF : 1));
                if (ctx.Width > 0 && dirs.Find(ExifIfd.Exif, 0xA002) == null) dirs.Set(ExifIfd.Exif, 0xA002, ctx.Width <= 65535 ? ExifValue.Shorts(ctx.Width) : ExifValue.Longs((uint)ctx.Width));
                if (ctx.Height > 0 && dirs.Find(ExifIfd.Exif, 0xA003) == null) dirs.Set(ExifIfd.Exif, 0xA003, ctx.Height <= 65535 ? ExifValue.Shorts(ctx.Height) : ExifValue.Longs((uint)ctx.Height));
            }
        }

        // ------------------------------------------------------------------ writing

        static readonly ExifIfd[] Order = { ExifIfd.Image, ExifIfd.Exif, ExifIfd.Interop, ExifIfd.Gps, ExifIfd.Thumbnail };

        /// <summary>The entries of each directory including its pointers (pointer values are filled in when written).</summary>
        static Dictionary<ExifIfd, List<Entry>> WithPointers(Dirs dirs, bool keepOpaque)
        {
            bool has(ExifIfd i) => dirs[i].Count > 0;
            var full = new Dictionary<ExifIfd, List<Entry>>();
            foreach (var i in Order)
            {
                var l = new List<Entry>();
                foreach (var e in dirs[i]) if (keepOpaque || !e.Opaque) l.Add(e);
                if (i == ExifIfd.Image)
                {
                    if (has(ExifIfd.Exif)) l.Add(new Entry { Tag = ExifPointer, Value = ExifValue.Longs(0) });
                    if (has(ExifIfd.Gps)) l.Add(new Entry { Tag = GpsPointer, Value = ExifValue.Longs(0) });
                }
                if (i == ExifIfd.Exif && has(ExifIfd.Interop)) l.Add(new Entry { Tag = InteropPointer, Value = ExifValue.Longs(0) });
                if (i == ExifIfd.Thumbnail && dirs.Thumb != null)
                {
                    l.Add(new Entry { Tag = dirs.ThumbStrip ? (ushort)0x0111 : (ushort)0x0201, Value = ExifValue.Longs(0) });
                    l.Add(new Entry { Tag = dirs.ThumbStrip ? (ushort)0x0117 : (ushort)0x0202, Value = ExifValue.Longs((uint)dirs.Thumb.Length) });
                }
                l.Sort((a, b) => a.Tag.CompareTo(b.Tag));
                full[i] = l;
            }
            return full;
        }

        /// <summary>Writes the directories as a TIFF structure in their byte order (see <see cref="Dirs.KeepLayout"/>).</summary>
        public static byte[] Serialize(Dirs dirs)
        {
            bool be = dirs.BigEndian, keep = dirs.KeepLayout && dirs.Original != null;
            var orig = keep ? dirs.Original! : Array.Empty<byte>();
            var full = WithPointers(dirs, keep);
            long pos = keep ? orig.Length + (orig.Length & 1) : 8;
            var dirAt = new Dictionary<ExifIfd, long>();
            var valueAt = new Dictionary<Entry, long>();
            var live = new List<(long, long)>();
            long thumbAt = 0;
            bool thumbKept = false;
            foreach (var i in Order)
            {
                var l = full[i];
                // IFD0 must exist for the thumbnail directory to be reachable
                if (l.Count == 0 && !(i == ExifIfd.Image && full[ExifIfd.Thumbnail].Count > 0)) continue;
                long size = 2 + 12L * l.Count + 4;
                if (keep && dirs.OldDirAt.TryGetValue(i, out var od) && size <= od.Size)
                {
                    // the directory fits where it was
                    dirAt[i] = od.At;
                    live.Add((od.At, od.At + size));
                }
                else { dirAt[i] = pos; pos += size; }
                foreach (var e in l)
                {
                    if (e.Value.Data.Length <= 4) continue;
                    if (keep && e.Origin >= 0) { valueAt[e] = e.Origin; live.Add((e.Origin, e.Origin + e.Value.Data.Length)); }
                    else { valueAt[e] = pos; pos += e.Value.Data.Length + (e.Value.Data.Length & 1); }
                }
                if (i == ExifIfd.Thumbnail && dirs.Thumb != null)
                {
                    if (keep && dirs.ThumbOrigin >= 0) { thumbAt = dirs.ThumbOrigin; thumbKept = true; live.Add((thumbAt, thumbAt + dirs.Thumb.Length)); }
                    else { thumbAt = pos; pos += dirs.Thumb.Length + (dirs.Thumb.Length & 1); }
                }
            }
            if (pos > int.MaxValue) throw new ImageNotSupportedException("EXIF block too large");
            var o = new byte[pos];
            if (keep)
            {
                orig.CopyTo(o, 0);
                // zero what is no longer referenced: old directories, values of removed / replaced fields, a removed thumbnail
                var dead = new List<(long, long)>(dirs.OldDirs);
                dead.AddRange(dirs.OldValues);
                if (dirs.ThumbOrigin >= 0 && !thumbKept) dead.Add((dirs.ThumbOrigin, dirs.ThumbOrigin + dirs.ThumbOriginLength));
                foreach (var (s, e) in Subtract(dead, live)) o.AsSpan((int)s, (int)(e - s)).Clear();
            }
            o[0] = o[1] = be ? (byte)'M' : (byte)'I';
            W16(o, 2, 42, be);
            foreach (var i in Order)
                if (dirAt.TryGetValue(i, out long at)) WriteDir(o, at, full[i], i, dirAt, valueAt, thumbAt, be, keep);
            if (dirs.Thumb != null && !thumbKept && dirs.Thumb.Length > 0) dirs.Thumb.CopyTo(o, (int)thumbAt);
            W32(o, 4, (uint)dirAt[ExifIfd.Image], be);
            return o;
        }

        static List<(long, long)> Subtract(List<(long, long)> dead, List<(long, long)> live)
        {
            var r = new List<(long, long)>();
            foreach (var (s0, e0) in dead)
            {
                var pieces = new List<(long, long)> { (s0, e0) };
                foreach (var (ls, le) in live)
                {
                    var n = new List<(long, long)>();
                    foreach (var (s, e) in pieces)
                    {
                        if (le <= s || ls >= e) { n.Add((s, e)); continue; }
                        if (ls > s) n.Add((s, ls));
                        if (le < e) n.Add((le, e));
                    }
                    pieces = n;
                }
                r.AddRange(pieces);
            }
            return r;
        }

        static void WriteDir(byte[] o, long at, List<Entry> l, ExifIfd i, Dictionary<ExifIfd, long> dirAt, Dictionary<Entry, long> valueAt, long thumbAt, bool be, bool keep)
        {
            W16(o, at, l.Count, be);
            long p = at + 2;
            foreach (var e in l)
            {
                long pointer = -1;
                if (i == ExifIfd.Image && e.Tag == ExifPointer) pointer = dirAt[ExifIfd.Exif];
                else if (i == ExifIfd.Image && e.Tag == GpsPointer) pointer = dirAt[ExifIfd.Gps];
                else if (i == ExifIfd.Exif && e.Tag == InteropPointer) pointer = dirAt[ExifIfd.Interop];
                else if (i == ExifIfd.Thumbnail && e.Tag is 0x0201 or 0x0111) pointer = thumbAt;
                var v = pointer >= 0 ? ExifValue.Longs((uint)pointer) : e.Value;
                W16(o, p, e.Tag, be);
                W16(o, p + 2, (int)v.Type, be);
                W32(o, p + 4, (uint)v.Count, be);
                var bytes = v.Bytes(be);
                if (bytes.Length <= 4) bytes.CopyTo(o, (int)p + 8);
                else
                {
                    long va = valueAt[e];
                    W32(o, p + 8, (uint)va, be);
                    if (!(keep && va == e.Origin)) bytes.CopyTo(o, (int)va);
                }
                p += 12;
            }
            long next = i == ExifIfd.Image && dirAt.TryGetValue(ExifIfd.Thumbnail, out long t1) ? t1 : 0;
            W32(o, p, (uint)next, be);
        }

        static void W16(byte[] o, long p, int v, bool be)
        {
            if (be) BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan((int)p), (ushort)v); else BinaryPrimitives.WriteUInt16LittleEndian(o.AsSpan((int)p), (ushort)v);
        }

        static void W32(byte[] o, long p, uint v, bool be)
        {
            if (be) BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan((int)p), v); else BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan((int)p), v);
        }
    }
}
