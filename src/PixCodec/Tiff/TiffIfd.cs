using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace PixCodec
{
    /// <summary>One directory entry to write. Values are kept as little endian bytes and swapped on output if needed.</summary>
    internal sealed class TiffField
    {
        public ushort Tag, Type;
        public long Count;
        /// <summary>Value bytes, little endian.</summary>
        public byte[] Data = Array.Empty<byte>();
        /// <summary>For a pointer tag (ExifIFD, GPS ...): the directory it points to. Data is then filled in on output.</summary>
        public TiffIfdNode? Child;

        public static TiffField Shorts(int tag, params int[] v)
        {
            var b = new byte[v.Length * 2];
            for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(i * 2), (ushort)v[i]);
            return new TiffField { Tag = (ushort)tag, Type = 3, Count = v.Length, Data = b };
        }

        /// <summary>LONG values, or LONG8 when <paramref name="wide"/> (offsets and byte counts of BigTIFF).</summary>
        public static TiffField Longs(int tag, bool wide, params long[] v)
        {
            int s = wide ? 8 : 4;
            var b = new byte[(long)v.Length * s];
            for (int i = 0; i < v.Length; i++)
            {
                if (wide) BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(i * 8), v[i]);
                else BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 4), checked((uint)v[i]));
            }
            return new TiffField { Tag = (ushort)tag, Type = (ushort)(wide ? 16 : 4), Count = v.Length, Data = b };
        }

        public static TiffField Rational(int tag, double v)
            => FromTag(new TiffTag(tag, TiffTagType.Rational, new[] { v }), true);

        /// <summary>Numerator / denominator for a value: exact for integers and short decimals, otherwise as fine as 32 bits allow.</summary>
        static void ToFraction(double v, bool signed, out long num, out long den)
        {
            double limit = signed ? int.MaxValue : uint.MaxValue;
            if (double.IsNaN(v) || double.IsInfinity(v)) { num = 0; den = 0; return; }
            if (!signed && v < 0) v = 0;
            den = 1;
            while (den < 1_000_000_000 && Math.Abs(v) * den * 10 <= limit && Math.Abs(v * den - Math.Round(v * den)) > 1e-9 * Math.Max(1, Math.Abs(v * den))) den *= 10;
            if (Math.Abs(v) * den > limit) { num = (long)(Math.Sign(v) * limit); den = 1; return; }
            num = (long)Math.Round(v * den);
        }

        /// <summary>Converts a caller supplied tag. Types that only exist in BigTIFF are refused for classic files.</summary>
        public static TiffField FromTag(TiffTag t, bool big)
        {
            var f = new TiffField { Tag = (ushort)t.Id, Type = (ushort)t.Type };
            switch (t.Type)
            {
                case TiffTagType.Byte: case TiffTagType.Undefined: f.Data = (byte[])((byte[])t.Value).Clone(); break;
                case TiffTagType.Ascii:
                {
                    var s = Encoding.UTF8.GetBytes((string)t.Value);
                    f.Data = new byte[s.Length + 1];
                    s.CopyTo(f.Data, 0);
                    break;
                }
                case TiffTagType.SByte: f.Data = MemoryMarshal.AsBytes<sbyte>((sbyte[])t.Value).ToArray(); break;
                case TiffTagType.Short: f.Data = MemoryMarshal.AsBytes<ushort>((ushort[])t.Value).ToArray(); break;
                case TiffTagType.SShort: f.Data = MemoryMarshal.AsBytes<short>((short[])t.Value).ToArray(); break;
                case TiffTagType.Long: f.Data = MemoryMarshal.AsBytes<uint>((uint[])t.Value).ToArray(); break;
                case TiffTagType.SLong: f.Data = MemoryMarshal.AsBytes<int>((int[])t.Value).ToArray(); break;
                case TiffTagType.Float: f.Data = MemoryMarshal.AsBytes<float>((float[])t.Value).ToArray(); break;
                case TiffTagType.Double: f.Data = MemoryMarshal.AsBytes<double>((double[])t.Value).ToArray(); break;
                case TiffTagType.Long8: case TiffTagType.SLong8:
                    if (!big) throw new ArgumentException($"tag {t.Id}: 64-bit field types need BigTIFF");
                    f.Data = t.Type == TiffTagType.Long8 ? MemoryMarshal.AsBytes<ulong>((ulong[])t.Value).ToArray() : MemoryMarshal.AsBytes<long>((long[])t.Value).ToArray();
                    break;
                case TiffTagType.Rational: case TiffTagType.SRational:
                {
                    var v = (double[])t.Value;
                    bool signed = t.Type == TiffTagType.SRational;
                    f.Data = new byte[v.Length * 8];
                    for (int i = 0; i < v.Length; i++)
                    {
                        ToFraction(v[i], signed, out long n, out long d);
                        BinaryPrimitives.WriteUInt32LittleEndian(f.Data.AsSpan(i * 8), unchecked((uint)n));
                        BinaryPrimitives.WriteUInt32LittleEndian(f.Data.AsSpan(i * 8 + 4), (uint)d);
                    }
                    break;
                }
                default: throw new ArgumentException($"tag {t.Id}: field type {t.Type} cannot be written");
            }
            f.Count = f.Data.Length / Math.Max(1, ElementSize(f.Type) * (t.Type is TiffTagType.Rational or TiffTagType.SRational ? 2 : 1));
            return f;
        }

        /// <summary>Bytes that are swapped as a unit for big endian output.</summary>
        public static int ElementSize(int type) => type switch
        {
            3 or 8 => 2,
            4 or 9 or 11 or 13 or 5 or 10 => 4,
            12 or 16 or 17 or 18 => 8,
            _ => 1,
        };
    }

    /// <summary>
    /// A directory to write together with its out-of-line values and child directories (EXIF, GPS). Laid out as:
    /// entries, next-IFD pointer, values (even aligned), then the children.
    /// </summary>
    internal sealed class TiffIfdNode
    {
        public readonly List<TiffField> Fields = new List<TiffField>();

        public TiffField? Find(int tag)
        {
            foreach (var f in Fields) if (f.Tag == tag) return f;
            return null;
        }

        public void Set(TiffField f)
        {
            for (int i = 0; i < Fields.Count; i++)
                if (Fields[i].Tag == f.Tag) { Fields[i] = f; return; }
            Fields.Add(f);
        }

        static long Pad(long n) => n + (n & 1);

        /// <summary>Total size in the file (the size does not depend on the offsets).</summary>
        public long Size(bool big)
        {
            int cs = big ? 8 : 2, es = big ? 20 : 12, os = big ? 8 : 4;
            long n = cs + (long)Fields.Count * es + os;
            foreach (var f in Fields)
            {
                if (f.Child != null) n += Pad(f.Child.Size(big));
                else if (f.Data.Length > os) n += Pad(f.Data.Length);
            }
            return Pad(n);
        }

        /// <summary>Serializes the directory for the absolute file position <paramref name="at"/>.</summary>
        public byte[] Serialize(long at, long next, bool big, bool bigEndian)
        {
            long size = Size(big);
            if (size > int.MaxValue - 64) throw new ImageNotSupportedException("TIFF: directory too large");
            var b = new byte[size];
            Write(b, 0, at, next, big, bigEndian);
            return b;
        }

        void Write(byte[] b, int o, long at, long next, bool big, bool be)
        {
            Fields.Sort((x, y) => x.Tag.CompareTo(y.Tag));
            int cs = big ? 8 : 2, es = big ? 20 : 12, os = big ? 8 : 4;
            void U16(int p, int v) { if (be) BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(p), (ushort)v); else BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p), (ushort)v); }
            void U32(int p, long v) { if (be) BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(p), checked((uint)v)); else BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p), checked((uint)v)); }
            void U64(int p, long v) { if (be) BinaryPrimitives.WriteInt64BigEndian(b.AsSpan(p), v); else BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(p), v); }
            void Off(int p, long v) { if (big) U64(p, v); else U32(p, v); }

            if (big) U64(o, Fields.Count); else U16(o, Fields.Count);
            int dir = cs + Fields.Count * es + os;
            Off(o + dir - os, next);
            int vp = o + dir; // next free position for values / children
            for (int i = 0; i < Fields.Count; i++)
            {
                var f = Fields[i];
                int e = o + cs + i * es;
                U16(e, f.Tag);
                int valueAt = e + (big ? 12 : 8);
                if (f.Child != null)
                {
                    // pointer to a child directory
                    U16(e + 2, big ? 18 : 4);
                    if (big) U64(e + 4, 1); else U32(e + 4, 1);
                    Off(valueAt, at + (vp - o));
                    f.Child.Write(b, vp, at + (vp - o), 0, big, be);
                    vp += (int)Pad(f.Child.Size(big));
                    continue;
                }
                U16(e + 2, f.Type);
                if (big) U64(e + 4, f.Count); else U32(e + 4, f.Count);
                int target = valueAt;
                if (f.Data.Length > os)
                {
                    Off(valueAt, at + (vp - o));
                    target = vp;
                    vp += (int)Pad(f.Data.Length);
                }
                f.Data.CopyTo(b, target);
                int unit = TiffField.ElementSize(f.Type);
                if (be && unit > 1)
                    for (int k = 0; k + unit <= f.Data.Length; k += unit) b.AsSpan(target + k, unit).Reverse();
            }
        }

        /// <summary>
        /// Reads a directory from a TIFF structure (a file or an EXIF blob) for re-serialization. Only tags accepted by
        /// <paramref name="keep"/> are taken; the EXIF, GPS and interoperability pointers are followed.
        /// </summary>
        public static TiffIfdNode Read(TiffFile file, long offset, Func<int, bool> keep, int depth = 0)
        {
            var node = new TiffIfdNode();
            foreach (var e in file.ReadIfd(offset))
            {
                bool pointer = e.Tag is 34665 or 34853 or 40965;
                if (!keep(e.Tag) && !pointer) continue;
                try
                {
                    if (pointer)
                    {
                        if (depth >= 2) continue;
                        long child = file.Integer(new[] { e }, e.Tag, 0);
                        if (child <= 0 || child >= file.Src.Length) continue;
                        var c = Read(file, child, _ => true, depth + 1);
                        if (c.Fields.Count > 0) node.Fields.Add(new TiffField { Tag = e.Tag, Child = c });
                        continue;
                    }
                    if (e.Type is 13 or 16 or 17 or 18) continue; // nothing in EXIF uses these
                    // copied byte for byte (rationals keep their exact fractions)
                    node.Fields.Add(new TiffField { Tag = e.Tag, Type = e.Type, Count = e.Count, Data = file.RawLittleEndian(e) });
                }
                catch (ImageFormatException) { } // a broken entry is dropped, the rest survives
            }
            return node;
        }
    }

    /// <summary>EXIF handling: the EXIF data of a TIFF lives in its own directories, <see cref="ImageMetadata.Exif"/> is a standalone little TIFF.</summary>
    internal static unsafe class TiffExif
    {
        /// <summary>Baseline tags that EXIF readers expect in IFD0.</summary>
        public static bool IsDescriptive(int tag) => tag is 270 or 271 or 272 or 305 or 306 or 315 or 33432;

        /// <summary>
        /// Builds an EXIF blob (little endian TIFF header + IFD0 + Exif / GPS directories) from an image directory.
        /// Returns null when the image has neither an Exif nor a GPS directory.
        /// </summary>
        public static byte[]? Extract(TiffFile file, long ifdOffset)
        {
            var node = TiffIfdNode.Read(file, ifdOffset, t => IsDescriptive(t) || t is 274 or 282 or 283 or 296);
            if (node.Find(34665) == null && node.Find(34853) == null) return null;
            var dir = node.Serialize(8, 0, false, false);
            var blob = new byte[8 + dir.Length];
            blob[0] = blob[1] = (byte)'I'; blob[2] = 42; blob[4] = 8;
            dir.CopyTo(blob, 8);
            return blob;
        }

        /// <summary>Parses an EXIF blob into a directory tree; null if it is not usable.</summary>
        public static TiffIfdNode? Parse(byte[] exif)
        {
            try
            {
                fixed (byte* p = exif)
                {
                    var file = new TiffFile(new TiffMemorySource(p, exif.Length));
                    return TiffIfdNode.Read(file, file.FirstIfd, t => IsDescriptive(t) || t is 274 or 282 or 283 or 296);
                }
            }
            catch (ImageFormatException) { return null; }
        }
    }
}
