using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Aardvark.ImageCodecs
{
    /// <summary>TIFF field types (TIFF 6.0 plus the BigTIFF additions).</summary>
    public enum TiffTagType : ushort
    {
        Byte = 1, Ascii = 2, Short = 3, Long = 4, Rational = 5, SByte = 6, Undefined = 7, SShort = 8, SLong = 9,
        SRational = 10, Float = 11, Double = 12, Ifd = 13, Long8 = 16, SLong8 = 17, Ifd8 = 18,
    }

    /// <summary>
    /// One raw IFD entry. <see cref="Value"/> holds the decoded values in machine byte order:
    /// Byte/Undefined: byte[]; Ascii: string; Short: ushort[]; Long/Ifd: uint[]; SByte: sbyte[]; SShort: short[];
    /// SLong: int[]; Long8/Ifd8: ulong[]; SLong8: long[]; Float: float[]; Double: double[];
    /// Rational/SRational: double[] (numerator / denominator).
    /// </summary>
    public sealed class TiffTag
    {
        public int Id { get; }
        public TiffTagType Type { get; }
        /// <summary>Number of values as stored in the file (for Ascii: bytes including the terminating NUL).</summary>
        public int Count { get; }
        public object Value { get; }

        internal TiffTag(int id, TiffTagType type, int count, object value)
        {
            Id = id; Type = type; Count = count; Value = value;
        }

        /// <summary>
        /// A tag to write with <see cref="TiffEncodeOptions.ExtraTags"/>. <paramref name="value"/> must have the
        /// type listed above for <paramref name="type"/> (Rational / SRational take a double[] and are stored as
        /// fractions). The factory methods below cover the common cases.
        /// </summary>
        public TiffTag(int id, TiffTagType type, object value)
        {
            if (id < 0 || id > 65535) throw new ArgumentOutOfRangeException(nameof(id));
            if (value == null) throw new ArgumentNullException(nameof(value));
            var want = type switch
            {
                TiffTagType.Byte or TiffTagType.Undefined => typeof(byte[]),
                TiffTagType.Ascii => typeof(string),
                TiffTagType.Short => typeof(ushort[]),
                TiffTagType.Long or TiffTagType.Ifd => typeof(uint[]),
                TiffTagType.SByte => typeof(sbyte[]),
                TiffTagType.SShort => typeof(short[]),
                TiffTagType.SLong => typeof(int[]),
                TiffTagType.Long8 or TiffTagType.Ifd8 => typeof(ulong[]),
                TiffTagType.SLong8 => typeof(long[]),
                TiffTagType.Float => typeof(float[]),
                TiffTagType.Double or TiffTagType.Rational or TiffTagType.SRational => typeof(double[]),
                _ => throw new ArgumentException("unknown TIFF field type", nameof(type)),
            };
            // exact type test: `value is byte[]` would also accept an sbyte[]
            if (value.GetType() != want) throw new ArgumentException($"a {type} tag needs a {want.Name} value, got {value.GetType().Name}");
            Id = id; Type = type; Value = value;
            Count = value is string s ? Encoding.UTF8.GetByteCount(s) + 1 : ((Array)value).Length;
        }

        public static TiffTag Ascii(int id, string text) => new TiffTag(id, TiffTagType.Ascii, text);
        public static TiffTag Shorts(int id, params ushort[] values) => new TiffTag(id, TiffTagType.Short, values);
        public static TiffTag Longs(int id, params uint[] values) => new TiffTag(id, TiffTagType.Long, values);
        public static TiffTag Doubles(int id, params double[] values) => new TiffTag(id, TiffTagType.Double, values);
        public static TiffTag Rationals(int id, params double[] values) => new TiffTag(id, TiffTagType.Rational, values);
        public static TiffTag Bytes(int id, byte[] values) => new TiffTag(id, TiffTagType.Undefined, values);

        /// <summary>The string of an Ascii tag, null for other types.</summary>
        public string? AsString() => Value as string;

        /// <summary>The bytes of a Byte/Undefined tag, null for other types.</summary>
        public byte[]? AsBytes() => Value as byte[];

        // Note: no type patterns here. The CLR treats sbyte[]/byte[] (short[]/ushort[], ...) as interchangeable,
        // so "Value is byte[]" also matches an sbyte[] - the field type decides instead.

        /// <summary>All values converted to double (empty for Ascii).</summary>
        public double[] AsDoubles() => Type switch
        {
            TiffTagType.Byte or TiffTagType.Undefined => Array.ConvertAll((byte[])Value, x => (double)x),
            TiffTagType.SByte => Array.ConvertAll((sbyte[])Value, x => (double)x),
            TiffTagType.Short => Array.ConvertAll((ushort[])Value, x => (double)x),
            TiffTagType.SShort => Array.ConvertAll((short[])Value, x => (double)x),
            TiffTagType.Long or TiffTagType.Ifd => Array.ConvertAll((uint[])Value, x => (double)x),
            TiffTagType.SLong => Array.ConvertAll((int[])Value, x => (double)x),
            TiffTagType.Long8 or TiffTagType.Ifd8 => Array.ConvertAll((ulong[])Value, x => (double)x),
            TiffTagType.SLong8 => Array.ConvertAll((long[])Value, x => (double)x),
            TiffTagType.Float => Array.ConvertAll((float[])Value, x => (double)x),
            TiffTagType.Double or TiffTagType.Rational or TiffTagType.SRational => (double[])((double[])Value).Clone(),
            _ => Array.Empty<double>(),
        };

        /// <summary>All values converted to long (truncating for floating point types, empty for Ascii).</summary>
        public long[] AsIntegers() => Type switch
        {
            TiffTagType.Byte or TiffTagType.Undefined => Array.ConvertAll((byte[])Value, x => (long)x),
            TiffTagType.SByte => Array.ConvertAll((sbyte[])Value, x => (long)x),
            TiffTagType.Short => Array.ConvertAll((ushort[])Value, x => (long)x),
            TiffTagType.SShort => Array.ConvertAll((short[])Value, x => (long)x),
            TiffTagType.Long or TiffTagType.Ifd => Array.ConvertAll((uint[])Value, x => (long)x),
            TiffTagType.SLong => Array.ConvertAll((int[])Value, x => (long)x),
            TiffTagType.Long8 or TiffTagType.Ifd8 => Array.ConvertAll((ulong[])Value, x => unchecked((long)x)),
            TiffTagType.SLong8 => (long[])((long[])Value).Clone(),
            TiffTagType.Float => Array.ConvertAll((float[])Value, x => (long)x),
            TiffTagType.Double or TiffTagType.Rational or TiffTagType.SRational => Array.ConvertAll((double[])Value, x => (long)x),
            _ => Array.Empty<long>(),
        };

        public override string ToString() => $"{Id} {Type}[{Count}]";
    }

    /// <summary>Random access to the bytes of a TIFF file, either pinned memory or a seekable stream.</summary>
    internal abstract class TiffSource
    {
        public long Length;

        /// <summary>Fills <paramref name="dst"/> from the absolute file offset. Throws ImageFormatException when out of range.</summary>
        public abstract void Read(long offset, Span<byte> dst);

        /// <summary>
        /// Returns <paramref name="count"/> bytes at <paramref name="offset"/>. Memory sources return a view;
        /// stream sources read into <paramref name="scratch"/> (rented from the shared pool, the caller returns it).
        /// </summary>
        public abstract ReadOnlySpan<byte> Get(long offset, int count, ref byte[]? scratch);

        protected void Check(long offset, long count)
        {
            if (offset < 0 || count < 0 || offset > Length || count > Length - offset)
                throw new ImageFormatException("TIFF: data offset outside of the file");
        }
    }

    internal sealed unsafe class TiffMemorySource : TiffSource
    {
        readonly byte* _p;
        public TiffMemorySource(byte* p, int length) { _p = p; Length = length; }

        public override void Read(long offset, Span<byte> dst)
        {
            Check(offset, dst.Length);
            new ReadOnlySpan<byte>(_p + offset, dst.Length).CopyTo(dst);
        }

        public override ReadOnlySpan<byte> Get(long offset, int count, ref byte[]? scratch)
        {
            Check(offset, count);
            return new ReadOnlySpan<byte>(_p + offset, count);
        }
    }

    internal sealed class TiffStreamSource : TiffSource
    {
        readonly Stream _s;
        readonly long _origin;
        public long Origin => _origin;

        public TiffStreamSource(Stream s)
        {
            if (!s.CanSeek || !s.CanRead) throw new ArgumentException("TIFF stream decoding needs a readable, seekable stream");
            _s = s; _origin = s.Position; Length = s.Length - _origin;
        }

        public override void Read(long offset, Span<byte> dst)
        {
            Check(offset, dst.Length);
            lock (_s)
            {
                _s.Position = _origin + offset;
                try { _s.ReadExactly(dst); }
                catch (EndOfStreamException e) { throw new ImageFormatException("TIFF: unexpected end of stream", e); }
            }
        }

        public override ReadOnlySpan<byte> Get(long offset, int count, ref byte[]? scratch)
        {
            Check(offset, count);
            if (scratch == null || scratch.Length < count)
            {
                if (scratch != null) ArrayPool<byte>.Shared.Return(scratch);
                scratch = ArrayPool<byte>.Shared.Rent(count);
            }
            Read(offset, scratch.AsSpan(0, count));
            return scratch.AsSpan(0, count);
        }
    }

    internal struct TiffEntry
    {
        public ushort Tag, Type;
        public long Count;
        /// <summary>Absolute file position of the value bytes (inside the entry itself for inline values).</summary>
        public long ValueOffset;
    }

    /// <summary>Header + IFD level access to a classic TIFF or BigTIFF.</summary>
    internal sealed class TiffFile
    {
        public readonly TiffSource Src;
        public readonly bool BigEndian, Big;
        public readonly long FirstIfd;

        public static bool IsMatch(ReadOnlySpan<byte> h)
        {
            if (h.Length < 4) return false;
            if (h[0] == (byte)'I' && h[1] == (byte)'I') return (h[2] == 42 || h[2] == 43) && h[3] == 0;
            if (h[0] == (byte)'M' && h[1] == (byte)'M') return h[2] == 0 && (h[3] == 42 || h[3] == 43);
            return false;
        }

        public TiffFile(TiffSource src)
        {
            Src = src;
            Span<byte> h = stackalloc byte[16];
            if (src.Length < 8) throw new ImageFormatException("TIFF: file too short");
            src.Read(0, h.Slice(0, 8));
            // JPEG XR files (T.832 Annex A) are little endian TIFF directories behind the magic II 0xBC 0x01
            bool jxr = h[0] == (byte)'I' && h[1] == (byte)'I' && h[2] == 0xBC && h[3] == 1;
            if (!IsMatch(h) && !jxr) throw new ImageFormatException("not a TIFF file");
            BigEndian = h[0] == (byte)'M';
            Big = !jxr && U16(h.Slice(2)) == 43;
            if (Big)
            {
                if (src.Length < 16) throw new ImageFormatException("TIFF: file too short");
                src.Read(0, h);
                if (U16(h.Slice(4)) != 8 || U16(h.Slice(6)) != 0) throw new ImageFormatException("BigTIFF: unsupported offset size");
                FirstIfd = (long)U64(h.Slice(8));
            }
            else FirstIfd = U32(h.Slice(4));
            if (FirstIfd < 8 || FirstIfd >= src.Length) throw new ImageFormatException("TIFF: invalid first IFD offset");
        }

        public ushort U16(ReadOnlySpan<byte> b) => BigEndian ? BinaryPrimitives.ReadUInt16BigEndian(b) : BinaryPrimitives.ReadUInt16LittleEndian(b);
        public uint U32(ReadOnlySpan<byte> b) => BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(b) : BinaryPrimitives.ReadUInt32LittleEndian(b);
        public ulong U64(ReadOnlySpan<byte> b) => BigEndian ? BinaryPrimitives.ReadUInt64BigEndian(b) : BinaryPrimitives.ReadUInt64LittleEndian(b);

        public static int TypeSize(int type) => type switch
        {
            1 or 2 or 6 or 7 => 1,
            3 or 8 => 2,
            4 or 9 or 11 or 13 => 4,
            5 or 10 or 12 or 16 or 17 or 18 => 8,
            _ => 0,
        };

        /// <summary>Number of entries of the IFD at <paramref name="offset"/> and the position of its "next IFD" field.</summary>
        long EntryCount(long offset, out long nextPos)
        {
            Span<byte> b = stackalloc byte[8];
            int cs = Big ? 8 : 2, es = Big ? 20 : 12;
            if (offset < 8 || offset > Src.Length - cs) throw new ImageFormatException("TIFF: invalid IFD offset");
            Src.Read(offset, b.Slice(0, cs));
            ulong n = Big ? U64(b) : U16(b);
            if (n > (ulong)((Src.Length - offset - cs) / es)) throw new ImageFormatException("TIFF: IFD exceeds the file");
            nextPos = offset + cs + (long)n * es;
            return (long)n;
        }

        long ReadNext(long nextPos)
        {
            Span<byte> b = stackalloc byte[8];
            int os = Big ? 8 : 4;
            if (nextPos > Src.Length - os) return 0; // tolerate a missing terminator at the end of the file
            Src.Read(nextPos, b.Slice(0, os));
            return Big ? (long)U64(b) : U32(b);
        }

        /// <summary>Offsets of the IFDs in the main chain. A broken link ends the chain instead of failing.</summary>
        public List<long> IfdOffsets(int max = int.MaxValue)
        {
            var list = new List<long>();
            var seen = new HashSet<long>();
            long off = FirstIfd;
            while (off != 0 && list.Count < max && seen.Add(off))
            {
                long nextPos;
                try { EntryCount(off, out nextPos); }
                catch (ImageFormatException) when (list.Count > 0) { break; }
                list.Add(off);
                off = ReadNext(nextPos);
            }
            return list;
        }

        public long IfdOffset(int index)
        {
            if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
            var l = IfdOffsets(index + 1);
            if (index >= l.Count) throw new ArgumentOutOfRangeException(nameof(index), $"TIFF has only {l.Count} image(s)");
            return l[index];
        }

        public TiffEntry[] ReadIfd(long offset)
        {
            long n = EntryCount(offset, out _);
            if (n > 1 << 24) throw new ImageFormatException("TIFF: too many IFD entries");
            int cs = Big ? 8 : 2, es = Big ? 20 : 12, inl = Big ? 8 : 4;
            var buf = new byte[n * es];
            Src.Read(offset + cs, buf);
            var list = new List<TiffEntry>((int)n);
            for (int i = 0; i < n; i++)
            {
                var e = buf.AsSpan(i * es, es);
                var t = new TiffEntry { Tag = U16(e), Type = U16(e.Slice(2)) };
                int size = TypeSize(t.Type);
                if (size == 0) continue; // unknown field type: ignore the entry as the spec demands
                ulong count = Big ? U64(e.Slice(4)) : U32(e.Slice(4));
                if (count > (ulong)Src.Length) continue; // cannot possibly be valid
                t.Count = (long)count;
                int vp = Big ? 12 : 8;
                if (t.Count * size <= inl) t.ValueOffset = offset + cs + (long)i * es + vp;
                else t.ValueOffset = Big ? (long)U64(e.Slice(vp)) : U32(e.Slice(vp));
                list.Add(t);
            }
            return list.ToArray();
        }

        public static bool Find(TiffEntry[] entries, int tag, out TiffEntry entry)
        {
            foreach (var e in entries)
                if (e.Tag == tag) { entry = e; return true; }
            entry = default;
            return false;
        }

        byte[] ReadRaw(in TiffEntry e, out int count)
        {
            int size = TypeSize(e.Type);
            long bytes = e.Count * size;
            if (e.Count > int.MaxValue / 8 || e.ValueOffset < 0 || e.ValueOffset > Src.Length || bytes > Src.Length - e.ValueOffset)
                throw new ImageFormatException($"TIFF: value of tag {e.Tag} lies outside of the file");
            count = (int)e.Count;
            var raw = new byte[bytes];
            Src.Read(e.ValueOffset, raw);
            return raw;
        }

        /// <summary>The value bytes of an entry converted to little endian, for copying an entry into another file.</summary>
        public byte[] RawLittleEndian(in TiffEntry e)
        {
            var raw = ReadRaw(e, out _);
            int unit = TiffField.ElementSize(e.Type);
            if (BigEndian && unit > 1)
                for (int i = 0; i + unit <= raw.Length; i += unit) raw.AsSpan(i, unit).Reverse();
            return raw;
        }

        T[] Cast<T>(byte[] raw, int count) where T : unmanaged
        {
            var r = new T[count];
            raw.AsSpan().CopyTo(MemoryMarshal.AsBytes<T>(r));
            return r;
        }

        /// <summary>Reads the values of an entry in machine byte order (see <see cref="TiffTag.Value"/> for the types).</summary>
        public object ReadValue(in TiffEntry e)
        {
            var raw = ReadRaw(e, out int n);
            int size = TypeSize(e.Type);
            if (BigEndian && size > 1)
            {
                int unit = e.Type is 5 or 10 ? 4 : size;
                for (int i = 0; i + unit <= raw.Length; i += unit) raw.AsSpan(i, unit).Reverse();
            }
            switch (e.Type)
            {
                case 1: case 7: return raw;
                case 2:
                    int len = raw.Length;
                    while (len > 0 && raw[len - 1] == 0) len--;
                    return Encoding.UTF8.GetString(raw, 0, len);
                case 3: return Cast<ushort>(raw, n);
                case 4: case 13: return Cast<uint>(raw, n);
                case 6: return Cast<sbyte>(raw, n);
                case 8: return Cast<short>(raw, n);
                case 9: return Cast<int>(raw, n);
                case 11: return Cast<float>(raw, n);
                case 12: return Cast<double>(raw, n);
                case 16: case 18: return Cast<ulong>(raw, n);
                case 17: return Cast<long>(raw, n);
                case 5:
                {
                    var v = Cast<uint>(raw, n * 2);
                    var r = new double[n];
                    for (int i = 0; i < n; i++) r[i] = v[2 * i + 1] == 0 ? 0 : (double)v[2 * i] / v[2 * i + 1];
                    return r;
                }
                case 10:
                {
                    var v = Cast<int>(raw, n * 2);
                    var r = new double[n];
                    for (int i = 0; i < n; i++) r[i] = v[2 * i + 1] == 0 ? 0 : (double)v[2 * i] / v[2 * i + 1];
                    return r;
                }
                default: throw new ImageFormatException($"TIFF: unknown field type {e.Type}");
            }
        }

        /// <summary>Integer values of an entry (any integer field type), or null when the tag is absent.</summary>
        public long[]? Integers(TiffEntry[] entries, int tag)
        {
            if (!Find(entries, tag, out var e)) return null;
            var v = ReadValue(e);
            switch (e.Type)
            {
                case 1: case 7: return Array.ConvertAll((byte[])v, x => (long)x);
                case 6: return Array.ConvertAll((sbyte[])v, x => (long)x);
                case 3: return Array.ConvertAll((ushort[])v, x => (long)x);
                case 8: return Array.ConvertAll((short[])v, x => (long)x);
                case 4: case 13: return Array.ConvertAll((uint[])v, x => (long)x);
                case 9: return Array.ConvertAll((int[])v, x => (long)x);
                case 17: return (long[])v;
                case 16: case 18:
                    var a = (ulong[])v;
                    var r = new long[a.Length];
                    for (int i = 0; i < a.Length; i++)
                    {
                        if (a[i] > long.MaxValue) throw new ImageFormatException($"TIFF: value of tag {tag} out of range");
                        r[i] = (long)a[i];
                    }
                    return r;
                default: throw new ImageFormatException($"TIFF: tag {tag} has a non-integer type");
            }
        }

        public long Integer(TiffEntry[] entries, int tag, long def)
        {
            var v = Integers(entries, tag);
            return v == null || v.Length == 0 ? def : v[0];
        }

        public double[]? Doubles(TiffEntry[] entries, int tag)
        {
            if (!Find(entries, tag, out var e) || e.Type == 2) return null;
            var v = ReadValue(e);
            return new TiffTag(tag, (TiffTagType)e.Type, 0, v).AsDoubles();
        }

        public byte[]? Bytes(TiffEntry[] entries, int tag)
        {
            if (!Find(entries, tag, out var e) || (e.Type != 1 && e.Type != 7)) return null;
            return ReadRaw(e, out _);
        }

        public TiffTag[] ReadTags(TiffEntry[] entries)
        {
            var r = new List<TiffTag>(entries.Length);
            foreach (var e in entries)
            {
                object v;
                try { v = ReadValue(e); }
                catch (ImageFormatException) { continue; } // a tag pointing outside of the file is dropped
                r.Add(new TiffTag(e.Tag, (TiffTagType)e.Type, (int)e.Count, v));
            }
            return r.ToArray();
        }
    }
}
