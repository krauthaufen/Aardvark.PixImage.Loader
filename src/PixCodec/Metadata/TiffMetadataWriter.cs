using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace PixCodec
{
    /// <summary>Random access to the bytes of a file being rewritten.</summary>
    internal interface IByteSource
    {
        long Length { get; }
        /// <summary>Reads exactly <paramref name="dst"/>.Length bytes at <paramref name="pos"/> (throws past the end).</summary>
        void Read(long pos, Span<byte> dst);
    }

    internal sealed class ArraySource : IByteSource
    {
        readonly byte[] _d;
        public ArraySource(byte[] d) => _d = d;
        public long Length => _d.Length;
        public void Read(long pos, Span<byte> dst)
        {
            if (pos < 0 || pos + dst.Length > _d.Length) throw new ImageFormatException("TIFF: offset beyond the end of the file");
            _d.AsSpan((int)pos, dst.Length).CopyTo(dst);
        }
    }

    internal sealed class StreamSource : IByteSource
    {
        readonly Stream _s;
        readonly long _start;
        public StreamSource(Stream s) { _s = s; _start = s.Position; Length = s.Length - _start; }
        public long Length { get; }
        public void Read(long pos, Span<byte> dst)
        {
            if (pos < 0 || pos + dst.Length > Length) throw new ImageFormatException("TIFF: offset beyond the end of the file");
            _s.Position = _start + pos;
            _s.ReadExactly(dst);
        }
    }

    /// <summary>
    /// Rewrites the metadata of a TIFF / BigTIFF file without moving any image data: the changed directories (IFD0 and the
    /// EXIF, GPS and interoperability directories) are written anew at the end of the file, the header points to the new
    /// IFD0, and entries that do not change are copied verbatim (so their values, maker notes included, stay where they
    /// are). Directories and values that are no longer referenced are overwritten with zeros. Only the first page is edited.
    /// </summary>
    internal static class TiffMetadataWriter
    {
        /// <summary>The changes to make: bytes to overwrite in the original and bytes to append at <see cref="TailAt"/>.</summary>
        internal sealed class Changes
        {
            public readonly List<(long Offset, byte[] Data)> Patches = new();
            public long TailAt;
            public byte[] Tail = Array.Empty<byte>();
            public bool IsEmpty => Patches.Count == 0 && Tail.Length == 0;
        }

        sealed class Raw
        {
            public ushort Tag, Type;
            public long Count;
            public byte[] Entry = Array.Empty<byte>();   // the directory entry as stored
            public long ValueAt = -1, ValueBytes;          // out of line value
            public bool OutOfLine;
        }

        sealed class Ifd
        {
            public long Offset, Next, Size;
            public readonly List<Raw> Entries = new();
            public Raw? Find(int tag) { foreach (var e in Entries) if (e.Tag == tag) return e; return null; }
        }

        /// <summary>An entry of a rewritten directory: kept as stored, or a new value.</summary>
        sealed class Out
        {
            public ushort Tag;
            public Raw? Kept;
            public ExifValue? Value;
            public ExifIfd? Child;   // pointer to a rewritten directory
        }

        sealed class Reader
        {
            public IByteSource Src = null!;
            public bool Be, Big;
            readonly byte[] _b = new byte[8];

            public ulong U(long pos, int n)
            {
                Src.Read(pos, _b.AsSpan(0, n));
                return n switch
                {
                    2 => Be ? BinaryPrimitives.ReadUInt16BigEndian(_b) : BinaryPrimitives.ReadUInt16LittleEndian(_b),
                    4 => Be ? BinaryPrimitives.ReadUInt32BigEndian(_b) : BinaryPrimitives.ReadUInt32LittleEndian(_b),
                    _ => Be ? BinaryPrimitives.ReadUInt64BigEndian(_b) : BinaryPrimitives.ReadUInt64LittleEndian(_b),
                };
            }

            public byte[] Bytes(long pos, long n)
            {
                if (n > int.MaxValue - 64) throw new ImageNotSupportedException("TIFF: metadata field too large");
                var r = new byte[n];
                Src.Read(pos, r);
                return r;
            }

            public Ifd ReadIfd(long pos)
            {
                int cs = Big ? 8 : 2, es = Big ? 20 : 12, os = Big ? 8 : 4;
                if (pos < 8 || pos + cs > Src.Length) throw new ImageFormatException("TIFF: directory offset outside the file");
                long n = (long)U(pos, cs);
                if (n > 65535 || pos + cs + n * es + os > Src.Length) throw new ImageFormatException("TIFF: damaged directory");
                var ifd = new Ifd { Offset = pos, Size = cs + n * es + os };
                var raw = Bytes(pos + cs, n * es);
                for (int k = 0; k < n; k++)
                {
                    var e = new Raw { Entry = raw.AsSpan(k * es, es).ToArray() };
                    long p = pos + cs + (long)k * es;
                    e.Tag = (ushort)U(p, 2);
                    e.Type = (ushort)U(p + 2, 2);
                    e.Count = Big ? (long)Math.Min(U(p + 4, 8), long.MaxValue) : (long)U(p + 4, 4);
                    int size = ExifValue.TypeSize((ExifType)e.Type);
                    if (size > 0 && e.Count >= 0 && e.Count <= long.MaxValue / 8)
                    {
                        long bytes = e.Count * size;
                        if (bytes > os)
                        {
                            e.OutOfLine = true;
                            ulong at = U(p + (Big ? 12 : 8), os);
                            if (at < (ulong)Src.Length) { e.ValueAt = (long)at; e.ValueBytes = bytes; }
                        }
                    }
                    else e.OutOfLine = true;   // (unknown type or absurd count: nothing to read)
                    ifd.Entries.Add(e);
                }
                ifd.Next = (long)U(pos + cs + n * es, os);
                return ifd;
            }

            /// <summary>The values of an entry as integers (pointers, offsets, byte counts).</summary>
            public long[] Integers(Raw e)
            {
                int size = ExifValue.TypeSize((ExifType)e.Type);
                if (e.Type is not (3 or 4 or 13 or 16 or 18) || e.Count <= 0 || e.Count > 1 << 24) return Array.Empty<long>();
                byte[] data;
                if (e.OutOfLine)
                {
                    if (e.ValueAt < 0 || e.ValueAt + e.ValueBytes > Src.Length) return Array.Empty<long>();
                    data = Bytes(e.ValueAt, e.ValueBytes);
                }
                else data = e.Entry.AsSpan(Big ? 12 : 8, (int)(e.Count * size)).ToArray();
                var r = new long[e.Count];
                for (int i = 0; i < r.Length; i++)
                {
                    var s = data.AsSpan(i * size, size);
                    r[i] = size switch
                    {
                        2 => Be ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s),
                        4 => Be ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s),
                        _ => (long)(Be ? BinaryPrimitives.ReadUInt64BigEndian(s) : BinaryPrimitives.ReadUInt64LittleEndian(s)),
                    };
                }
                return r;
            }

            /// <summary>The value bytes of an entry, in file byte order.</summary>
            public byte[] Value(Raw e)
            {
                int size = ExifValue.TypeSize((ExifType)e.Type);
                if (size == 0) return Array.Empty<byte>();
                if (e.OutOfLine) return e.ValueAt >= 0 && e.ValueAt + e.ValueBytes <= Src.Length ? Bytes(e.ValueAt, e.ValueBytes) : Array.Empty<byte>();
                return e.Entry.AsSpan(Big ? 12 : 8, (int)(e.Count * size)).ToArray();
            }
        }

        /// <summary>Tags that "remove all" takes out of IFD0 (descriptive metadata; structure, colour and GeoTIFF tags stay).</summary>
        static bool IsRemovable(int tag) => tag is 269 or 270 or 271 or 272 or 285 or 305 or 306 or 315 or 316 or 33432 or 700 or 33723 or 34377
            or 0x4746 or 0x4749 or (>= 0x9C9B and <= 0x9C9F) or 0x8769 or 0x8825 or 0xA005 or 37510 or 0xC4A5;

        static readonly ExifIfd[] Order = { ExifIfd.Interop, ExifIfd.Exif, ExifIfd.Gps, ExifIfd.Image };

        public static Changes Plan(IByteSource src, MetadataPlan plan)
        {
            var c = new Changes();
            if (src.Length < 8) throw new ImageFormatException("TIFF: file too short");
            var head = new byte[16];
            src.Read(0, head.AsSpan(0, (int)Math.Min(16, src.Length)));
            var rd = new Reader { Src = src };
            if (head[0] == 'I' && head[1] == 'I') rd.Be = false;
            else if (head[0] == 'M' && head[1] == 'M') rd.Be = true;
            else throw new ImageFormatException("TIFF: bad byte order");
            int magic = (int)rd.U(2, 2);
            rd.Big = magic == 43;
            if (magic != 42 && !rd.Big) throw new ImageFormatException("TIFF: bad magic number");
            if (rd.Big && (src.Length < 16 || rd.U(4, 2) != 8)) throw new ImageNotSupportedException("BigTIFF: unsupported offset size");
            bool big = rd.Big, be = rd.Be;
            int os = big ? 8 : 4;
            long first = (long)rd.U(big ? 8 : 4, os);

            // the directories of the first page
            var old = new Dictionary<ExifIfd, Ifd>();
            old[ExifIfd.Image] = rd.ReadIfd(first);
            Ifd? Child(Ifd from, int tag)
            {
                var p = from.Find(tag);
                if (p == null) return null;
                var v = rd.Integers(p);
                if (v.Length == 0 || v[0] < 8 || v[0] >= src.Length) return null;
                try { return rd.ReadIfd(v[0]); } catch (ImageFormatException) { return null; }
            }
            if (Child(old[ExifIfd.Image], ExifEdit.ExifPointer) is { } ex) old[ExifIfd.Exif] = ex;
            if (Child(old[ExifIfd.Image], ExifEdit.GpsPointer) is { } gp) old[ExifIfd.Gps] = gp;
            if (old.TryGetValue(ExifIfd.Exif, out var exo) && Child(exo, ExifEdit.InteropPointer) is { } io) old[ExifIfd.Interop] = io;

            // the new entry lists
            var outs = new Dictionary<ExifIfd, List<Out>>();
            foreach (var k in new[] { ExifIfd.Image, ExifIfd.Exif, ExifIfd.Gps, ExifIfd.Interop })
            {
                var l = new List<Out>();
                if (old.TryGetValue(k, out var ifd))
                    foreach (var e in ifd.Entries) l.Add(new Out { Tag = e.Tag, Kept = e });
                outs[k] = l;
            }
            bool PointerTag(ExifIfd k, int tag) => k == ExifIfd.Image && tag is ExifEdit.ExifPointer or ExifEdit.GpsPointer || k == ExifIfd.Exif && tag == ExifEdit.InteropPointer;
            void Set(ExifIfd k, ushort tag, ExifValue v)
            {
                var l = outs[k];
                int i = l.FindIndex(o => o.Tag == tag);
                if (i >= 0 && l[i].Kept is Raw r && r.Type == (ushort)v.Type && r.Count == v.Count && Same(rd.Value(r), v.Bytes(be))) return;
                if (i >= 0) l[i] = new Out { Tag = tag, Value = v }; else l.Add(new Out { Tag = tag, Value = v });
            }
            void Remove(ExifIfd k, ushort tag) => outs[k].RemoveAll(o => o.Tag == tag);
            bool Has(ExifIfd k, ushort tag) => outs[k].Exists(o => o.Tag == tag);

            if (plan.ClearAll)
            {
                outs[ExifIfd.Image].RemoveAll(o => IsRemovable(o.Tag));
                outs[ExifIfd.Exif].Clear(); outs[ExifIfd.Gps].Clear(); outs[ExifIfd.Interop].Clear();
            }
            if (plan.ClearGps) outs[ExifIfd.Gps].Clear();
            int version = outs[ExifIfd.Exif].Find(o => o.Tag == 0x9000)?.Kept is Raw vr ? ExifOp.VersionOf(rd.Value(vr)) : 0, bump = 0;
            int gpsVersion = outs[ExifIfd.Gps].Find(o => o.Tag == 0)?.Kept is Raw gr ? ExifOp.GpsVersionOf(rd.Value(gr)) : 0, bumpGps = 0;
            foreach (var op in plan.Exif)
            {
                if (op.Ifd == ExifIfd.Thumbnail || PointerTag(op.Ifd, op.Tag)) continue;
                int have = op.Ifd == ExifIfd.Gps ? gpsVersion : version;
                if (op.Value != null && op.MinVersion != 0 && have < Math.Abs(op.MinVersion))
                {
                    if (op.MinVersion < 0) continue;
                    if (have != 0) { if (op.Ifd == ExifIfd.Gps) bumpGps = Math.Max(bumpGps, op.MinVersion); else bump = Math.Max(bump, op.MinVersion); }
                }
                if (op.Value == null) Remove(op.Ifd, op.Tag);
                else if (!op.IfPresent || Has(op.Ifd, op.Tag)) Set(op.Ifd, op.Tag, op.Value);
            }

            if (bump > version && outs[ExifIfd.Exif].Count > 0) Set(ExifIfd.Exif, 0x9000, ExifValue.Undefined(ExifOp.VersionBytes(bump)));
            if (bumpGps > gpsVersion && outs[ExifIfd.Gps].Count > 0) Set(ExifIfd.Gps, 0, ExifOp.GpsVersionBytes(bumpGps));

            // XMP (tag 700) and IPTC (33723, Photoshop resources 34377)
            byte[]? Stored(int tag) => old[ExifIfd.Image].Find(tag) is Raw r && outs[ExifIfd.Image].Exists(o => o.Kept == r) ? rd.Value(r) : null;
            if (plan.HasXmpChanges)
            {
                var x = XmpEditor.Edit(Stored(700), plan, out bool changed);
                if (changed)
                {
                    if (x == null) Remove(ExifIfd.Image, 700);
                    else Set(ExifIfd.Image, 700, new ExifValue { Type = ExifType.Byte, Count = x.Length, Data = x });
                }
            }
            if (plan.HasIptcChanges && !plan.ClearAll)
            {
                if (old[ExifIfd.Image].Find(33723) is Raw ir && Stored(33723) is byte[] iim)
                {
                    var n = IptcEditor.EditIim(iim, plan);
                    if (n == null) Remove(ExifIfd.Image, 33723);
                    else if (ir.Type == 4)
                    {
                        // stored as LONGs: whole 4 byte units (the bytes as they are, in either byte order)
                        var padded = new byte[(n.Length + 3) & ~3];
                        n.CopyTo(padded, 0);
                        var v = new ExifValue { Type = ExifType.Long, Count = padded.Length / 4, Data = be ? ExifValue.Swap(padded, ExifType.Long) : padded };
                        Set(ExifIfd.Image, 33723, v);
                    }
                    else Set(ExifIfd.Image, 33723, new ExifValue { Type = ir.Type == 1 ? ExifType.Byte : ExifType.Undefined, Count = n.Length, Data = n });
                }
                if (Stored(34377) is byte[] ps)
                {
                    var n = IptcEditor.EditPhotoshop(ps, plan, out bool changed);
                    if (changed)
                    {
                        if (n == null) Remove(ExifIfd.Image, 34377);
                        else Set(ExifIfd.Image, 34377, new ExifValue { Type = old[ExifIfd.Image].Find(34377)!.Type == 1 ? ExifType.Byte : ExifType.Undefined, Count = n.Length, Data = n });
                    }
                }
            }

            // required tags of directories made here
            if (outs[ExifIfd.Exif].Count > 0 && !old.ContainsKey(ExifIfd.Exif))
            {
                // what EXIF requires of the EXIF directory of a TIFF file
                if (!Has(ExifIfd.Exif, 0x9000)) Set(ExifIfd.Exif, 0x9000, ExifValue.Undefined("0232"u8.ToArray()));
                if (!Has(ExifIfd.Exif, 0xA000)) Set(ExifIfd.Exif, 0xA000, ExifValue.Undefined("0100"u8.ToArray()));
                if (!Has(ExifIfd.Exif, 0xA001)) Set(ExifIfd.Exif, 0xA001, ExifValue.Shorts(Has(ExifIfd.Image, 34675) ? 0xFFFF : 1));
            }
            if (outs[ExifIfd.Exif].Count > 0 && !Has(ExifIfd.Exif, 0x9000)) Set(ExifIfd.Exif, 0x9000, ExifValue.Undefined("0232"u8.ToArray()));
            if (outs[ExifIfd.Gps].Count > 0 && !Has(ExifIfd.Gps, 0x0000)) Set(ExifIfd.Gps, 0x0000, ExifValue.Bytes(2, 3, 0, 0));

            // which directories change: their entries, or a child directory that moves / appears / disappears
            bool Changed(ExifIfd k)
            {
                var l = outs[k];
                bool had = old.TryGetValue(k, out var o);
                if (!had) return l.Exists(x => !PointerTag(k, x.Tag));
                if (l.Count != o!.Entries.Count) return true;
                foreach (var x in l) if (x.Kept == null) return true;
                return false;
            }
            var changedSet = new HashSet<ExifIfd>();
            foreach (var k in Order) if (k != ExifIfd.Image && Changed(k)) changedSet.Add(k);
            bool Empty(ExifIfd k) => !outs[k].Exists(x => !PointerTag(k, x.Tag));
            // pointers: point to rewritten children, drop pointers to children that are gone
            void Pointer(ExifIfd parent, ushort tag, ExifIfd child)
            {
                var l = outs[parent];
                bool exists = old.ContainsKey(child);
                if (Empty(child) && !(exists && !changedSet.Contains(child)))
                {
                    if (l.RemoveAll(o => o.Tag == tag) > 0) changedSet.Add(parent);
                    return;
                }
                if (changedSet.Contains(child) || !exists)
                {
                    l.RemoveAll(o => o.Tag == tag);
                    l.Add(new Out { Tag = tag, Child = child });
                    changedSet.Add(parent);
                }
            }
            if (Empty(ExifIfd.Exif)) outs[ExifIfd.Interop].Clear();
            if (Changed(ExifIfd.Interop)) changedSet.Add(ExifIfd.Interop);
            Pointer(ExifIfd.Exif, ExifEdit.InteropPointer, ExifIfd.Interop);
            if (Changed(ExifIfd.Exif)) changedSet.Add(ExifIfd.Exif);
            Pointer(ExifIfd.Image, ExifEdit.ExifPointer, ExifIfd.Exif);
            Pointer(ExifIfd.Image, ExifEdit.GpsPointer, ExifIfd.Gps);
            if (Changed(ExifIfd.Image)) changedSet.Add(ExifIfd.Image);
            if (changedSet.Count == 0) return c;
            changedSet.Add(ExifIfd.Image);
            foreach (var k in Order) if (changedSet.Contains(k) && Empty(k) && k != ExifIfd.Image) changedSet.Remove(k);

            // layout at the end of the file: children first, IFD0 last
            long align = big ? 8 : 2;
            long tailAt = (src.Length + align - 1) / align * align;
            var tail = new MemoryStream();
            tail.Write(new byte[tailAt - src.Length]);
            c.TailAt = src.Length;
            var newAt = new Dictionary<ExifIfd, long>();
            foreach (var k in Order)
            {
                if (!changedSet.Contains(k)) continue;
                long at = src.Length + tail.Length;
                at = (at + align - 1) / align * align;
                while (src.Length + tail.Length < at) tail.WriteByte(0);
                newAt[k] = at;
                long next = k == ExifIfd.Image ? old[ExifIfd.Image].Next : 0;
                tail.Write(WriteIfd(outs[k], at, next, newAt, rd));
            }
            c.Tail = tail.ToArray();
            if (big && src.Length + c.Tail.Length < 0) throw new ImageNotSupportedException("TIFF: file too large");
            if (!big && src.Length + c.Tail.Length > uint.MaxValue) throw new ImageNotSupportedException("TIFF: the file would exceed 4 GB (classic TIFF)");

            // header: the new IFD0
            var hp = new byte[os];
            WriteOffset(hp, 0, newAt[ExifIfd.Image], big, be);
            c.Patches.Add((big ? 8 : 4, hp));

            // zero what is no longer referenced
            var dead = new List<(long, long)>();
            foreach (var (k, ifd) in old)
            {
                if (!changedSet.Contains(k) && !(Empty(k) && k != ExifIfd.Image)) continue;
                dead.Add((ifd.Offset, ifd.Offset + ifd.Size));
                var kept = new HashSet<Raw>();
                foreach (var o in outs[k]) if (o.Kept != null) kept.Add(o.Kept);
                foreach (var e in ifd.Entries)
                    if (!kept.Contains(e) && e.ValueAt >= 8 && !PointerTag(k, e.Tag)) dead.Add((e.ValueAt, Math.Min(src.Length, e.ValueAt + e.ValueBytes)));
            }
            var live = LiveRanges(rd, old, outs, changedSet);
            foreach (var (s, e) in Subtract(dead, live))
                if (e > s && e - s <= int.MaxValue) c.Patches.Add((s, new byte[e - s]));
            return c;
        }

        static bool Same(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b);

        static void WriteOffset(byte[] b, int p, long v, bool big, bool be)
        {
            if (big) { if (be) BinaryPrimitives.WriteInt64BigEndian(b.AsSpan(p), v); else BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(p), v); }
            else { if (be) BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(p), checked((uint)v)); else BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p), checked((uint)v)); }
        }

        /// <summary>A directory at <paramref name="at"/> followed by its new out of line values.</summary>
        static byte[] WriteIfd(List<Out> entries, long at, long next, Dictionary<ExifIfd, long> newAt, Reader rd)
        {
            bool big = rd.Big, be = rd.Be;
            int cs = big ? 8 : 2, es = big ? 20 : 12, os = big ? 8 : 4;
            entries.Sort((a, b) => a.Tag.CompareTo(b.Tag));
            long dirSize = cs + (long)entries.Count * es + os;
            long size = dirSize;
            foreach (var o in entries)
                if (o.Value != null && o.Value.Data.Length > os) size += o.Value.Data.Length + (o.Value.Data.Length & 1);
            var b = new byte[size];
            void U16(long p, int v) { if (be) BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan((int)p), (ushort)v); else BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan((int)p), (ushort)v); }
            void Cnt(long p, long v) { if (big) WriteOffset(b, (int)p, v, true, be); else WriteOffset(b, (int)p, v, false, be); }
            if (big) WriteOffset(b, 0, entries.Count, true, be); else U16(0, entries.Count);
            long vp = dirSize;
            for (int i = 0; i < entries.Count; i++)
            {
                var o = entries[i];
                long p = cs + (long)i * es;
                if (o.Kept != null) { o.Kept.Entry.CopyTo(b, p); continue; }
                U16(p, o.Tag);
                if (o.Child is ExifIfd child)
                {
                    U16(p + 2, big ? 18 : 4);
                    Cnt(p + 4, 1);
                    WriteOffset(b, (int)(p + 4 + os), newAt[child], big, be);
                    continue;
                }
                var v = o.Value!;
                U16(p + 2, (int)v.Type);
                Cnt(p + 4, v.Count);
                var bytes = v.Bytes(be);
                if (bytes.Length <= os) bytes.CopyTo(b, p + 4 + os);
                else
                {
                    WriteOffset(b, (int)(p + 4 + os), at + vp, big, be);
                    bytes.CopyTo(b, vp);
                    vp += bytes.Length + (bytes.Length & 1);
                }
            }
            WriteOffset(b, (int)(cs + (long)entries.Count * es), next, big, be);
            return b;
        }

        // ------------------------------------------------------------------ what stays referenced

        static readonly int[] ChildTags = { 330, ExifEdit.ExifPointer, ExifEdit.GpsPointer, ExifEdit.InteropPointer };

        /// <summary>Byte ranges still in use: every directory and value reachable without the rewritten directories.</summary>
        static List<(long, long)> LiveRanges(Reader rd, Dictionary<ExifIfd, Ifd> old, Dictionary<ExifIfd, List<Out>> outs, HashSet<ExifIfd> rewritten)
        {
            var live = new List<(long, long)>();
            var visited = new HashSet<long>();
            foreach (var (k, ifd) in old) if (rewritten.Contains(k)) visited.Add(ifd.Offset);
            var todo = new Stack<long>();
            void Entries(IEnumerable<Raw> es)
            {
                var list = new List<Raw>(es);
                foreach (var e in list)
                {
                    if (e.ValueAt >= 0) live.Add((e.ValueAt, e.ValueAt + e.ValueBytes));
                    if (Array.IndexOf(ChildTags, (int)e.Tag) >= 0 || e.Type is 13 or 18)
                        foreach (var v in rd.Integers(e)) if (v >= 8) todo.Push(v);
                }
                // image data: offsets with their byte counts
                foreach (var (off, cnt) in new[] { (273, 279), (324, 325), (513, 514), (288, 289) })
                {
                    var o = list.Find(x => x.Tag == off);
                    var n = list.Find(x => x.Tag == cnt);
                    if (o == null) continue;
                    var ov = rd.Integers(o);
                    var nv = n != null ? rd.Integers(n) : Array.Empty<long>();
                    for (int i = 0; i < ov.Length; i++) live.Add((ov[i], ov[i] + (i < nv.Length ? nv[i] : 1)));
                }
            }
            foreach (var k in rewritten)
            {
                var kept = new List<Raw>();
                foreach (var o in outs[k]) if (o.Kept != null) kept.Add(o.Kept);
                Entries(kept);
            }
            foreach (var (k, ifd) in old)
                if (!rewritten.Contains(k) && outs[k].Count > 0 && visited.Add(ifd.Offset)) { live.Add((ifd.Offset, ifd.Offset + ifd.Size)); Entries(ifd.Entries); }
            if (old[ExifIfd.Image].Next >= 8) todo.Push(old[ExifIfd.Image].Next);
            int guard = 0;
            while (todo.Count > 0 && guard++ < 200_000)
            {
                long at = todo.Pop();
                if (!visited.Add(at)) continue;
                Ifd ifd;
                try { ifd = rd.ReadIfd(at); } catch (ImageFormatException) { continue; }
                live.Add((ifd.Offset, ifd.Offset + ifd.Size));
                Entries(ifd.Entries);
                if (ifd.Next >= 8) todo.Push(ifd.Next);
            }
            return live;
        }

        static List<(long, long)> Subtract(List<(long, long)> dead, List<(long, long)> live)
        {
            live.Sort();
            var r = new List<(long, long)>();
            foreach (var (s0, e0) in dead)
            {
                var pieces = new List<(long, long)> { (s0, e0) };
                foreach (var (ls, le) in live)
                {
                    if (le <= s0 || ls >= e0) continue;
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

        // ------------------------------------------------------------------ output

        /// <summary>Copies the original with the patches applied and appends the tail.</summary>
        public static void Write(IByteSource src, Changes c, Stream output)
        {
            var patches = new List<(long Offset, byte[] Data)>(c.Patches);
            patches.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            var buf = new byte[1 << 20];
            for (long pos = 0; pos < src.Length;)
            {
                int n = (int)Math.Min(buf.Length, src.Length - pos);
                src.Read(pos, buf.AsSpan(0, n));
                foreach (var (off, data) in patches)
                {
                    long s = Math.Max(off, pos), e = Math.Min(off + data.Length, pos + n);
                    if (s < e) data.AsSpan((int)(s - off), (int)(e - s)).CopyTo(buf.AsSpan((int)(s - pos)));
                }
                output.Write(buf, 0, n);
                pos += n;
            }
            output.Write(c.Tail);
        }

        /// <summary>Applies the changes to a file in place: the tail first, then the header, then the zeroing.</summary>
        public static void WriteInPlace(FileStream f, Changes c)
        {
            f.Position = c.TailAt;
            f.Write(c.Tail);
            f.Flush(true);
            var patches = new List<(long Offset, byte[] Data)>(c.Patches);
            // the header patch is the first one added
            var (ho, hd) = patches[0];
            f.Position = ho; f.Write(hd); f.Flush(true);
            for (int i = 1; i < patches.Count; i++) { f.Position = patches[i].Offset; f.Write(patches[i].Data); }
            f.Flush(true);
        }
    }
}
