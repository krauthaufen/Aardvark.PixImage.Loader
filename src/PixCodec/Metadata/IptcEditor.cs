using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PixCodec
{
    /// <summary>Edits IPTC-IIM data (IIM 4.2 datasets) and the Photoshop image resources that carry it.</summary>
    internal static class IptcEditor
    {
        /// <summary>Maximum lengths in bytes of the datasets this writer sets (IIM 4.2 record 2).</summary>
        static readonly Dictionary<(int, int), int> MaxLength = new()
        {
            [(2, 5)] = 64, [(2, 25)] = 64, [(2, 55)] = 8, [(2, 60)] = 11, [(2, 62)] = 8, [(2, 63)] = 11, [(2, 80)] = 32,
            [(2, 90)] = 32, [(2, 92)] = 32, [(2, 95)] = 32, [(2, 101)] = 64, [(2, 116)] = 128, [(2, 120)] = 2000,
        };

        sealed class DataSet { public int Record, Dataset; public byte[] Data = Array.Empty<byte>(); }

        static List<DataSet> Parse(ReadOnlySpan<byte> d)
        {
            var r = new List<DataSet>();
            int p = 0;
            while (p + 5 <= d.Length)
            {
                if (d[p] != 0x1C) { p++; continue; }
                int rec = d[p + 1], ds = d[p + 2];
                int len = BinaryPrimitives.ReadUInt16BigEndian(d.Slice(p + 3));
                p += 5;
                if ((len & 0x8000) != 0)
                {
                    int n = len & 0x7FFF;
                    if (n > 4 || p + n > d.Length) break;
                    long l = 0;
                    for (int k = 0; k < n; k++) l = l << 8 | d[p + k];
                    p += n;
                    if (l > int.MaxValue) break;
                    len = (int)l;
                }
                if (p + len > d.Length) break;
                r.Add(new DataSet { Record = rec, Dataset = ds, Data = d.Slice(p, len).ToArray() });
                p += len;
            }
            return r;
        }

        static byte[] Serialize(List<DataSet> list)
        {
            var o = new MemoryStream();
            Span<byte> l = stackalloc byte[4];
            foreach (var s in list)
            {
                o.WriteByte(0x1C); o.WriteByte((byte)s.Record); o.WriteByte((byte)s.Dataset);
                if (s.Data.Length <= 32767) { o.WriteByte((byte)(s.Data.Length >> 8)); o.WriteByte((byte)s.Data.Length); }
                else
                {
                    o.WriteByte(0x80); o.WriteByte(4);
                    BinaryPrimitives.WriteUInt32BigEndian(l, (uint)s.Data.Length);
                    o.Write(l);
                }
                o.Write(s.Data);
            }
            return o.ToArray();
        }

        static bool IsUtf8Marker(DataSet s) => s.Record == 1 && s.Dataset == 90 && s.Data.Length >= 3 && s.Data[0] == 0x1B && s.Data[1] == '%' && s.Data[2] == 'G';

        static bool IsAscii(string s) { foreach (var c in s) if (c >= 0x80) return false; return true; }

        /// <summary>Text cut to at most <paramref name="max"/> UTF-8 / Latin-1 bytes without splitting a character.</summary>
        static byte[] Encode(string s, bool utf8, int max)
        {
            var enc = utf8 ? Encoding.UTF8 : Encoding.Latin1;
            var b = enc.GetBytes(s);
            if (b.Length <= max) return b;
            int n = max;
            if (utf8) while (n > 0 && (b[n] & 0xC0) == 0x80) n--;
            return b.AsSpan(0, n).ToArray();
        }

        /// <summary>The IIM data with the plan applied; null when no dataset of record 2 remains.</summary>
        public static byte[]? EditIim(ReadOnlySpan<byte> iim, MetadataPlan plan)
        {
            if (plan.ClearAll) return null;
            var list = Parse(iim);
            bool utf8 = list.Exists(IsUtf8Marker);
            foreach (var op in plan.Iptc)
            {
                int at = list.FindIndex(s => s.Record == op.Record && s.Dataset == op.Dataset);
                list.RemoveAll(s => s.Record == op.Record && s.Dataset == op.Dataset);
                if (op.Values == null) continue;
                if (!utf8 && Array.Exists(op.Values, v => !IsAscii(v)))
                {
                    // switch the block to UTF-8 (1:90 ESC % G); Latin-1 text already there is converted
                    utf8 = true;
                    foreach (var s in list)
                        if (s.Record == 2 && s.Dataset != 0 && !System.Text.Unicode.Utf8.IsValid(s.Data)) s.Data = Encoding.UTF8.GetBytes(Encoding.Latin1.GetString(s.Data));
                    int r1 = list.FindLastIndex(s => s.Record <= 1);
                    list.Insert(r1 + 1, new DataSet { Record = 1, Dataset = 90, Data = new byte[] { 0x1B, (byte)'%', (byte)'G' } });
                    if (at > r1) at++;
                }
                if (at < 0 || at > list.Count)
                {
                    // ascending record / dataset order
                    at = list.FindIndex(s => s.Record > op.Record || s.Record == op.Record && s.Dataset > op.Dataset);
                    if (at < 0) at = list.Count;
                }
                int max = MaxLength.TryGetValue((op.Record, op.Dataset), out int m) ? m : 32000;
                foreach (var v in op.Values)
                    list.Insert(at++, new DataSet { Record = op.Record, Dataset = op.Dataset, Data = Encode(v, utf8, max) });
            }
            if (!list.Exists(s => s.Record == 2 && s.Dataset != 0)) return null;
            if (!list.Exists(s => s.Record == 2 && s.Dataset == 0))
            {
                // 2:00 record version first in record 2
                int r2 = list.FindIndex(s => s.Record >= 2);
                list.Insert(r2 < 0 ? list.Count : r2, new DataSet { Record = 2, Dataset = 0, Data = new byte[] { 0, 4 } });
            }
            return Serialize(list);
        }

        // ------------------------------------------------------------------ Photoshop image resources

        sealed class Resource { public int Id; public byte[] Name = Array.Empty<byte>(); public byte[] Data = Array.Empty<byte>(); }

        static List<Resource>? ParseResources(ReadOnlySpan<byte> d)
        {
            var r = new List<Resource>();
            int p = 0;
            while (p + 12 <= d.Length)
            {
                if (!d.Slice(p, 4).SequenceEqual("8BIM"u8)) break;
                int id = BinaryPrimitives.ReadUInt16BigEndian(d.Slice(p + 4));
                int nameLen = d[p + 6];
                int nameTotal = (nameLen + 2) & ~1;
                int q = p + 6 + nameTotal;
                if (q + 4 > d.Length) return null;
                long size = BinaryPrimitives.ReadUInt32BigEndian(d.Slice(q));
                q += 4;
                if (q + size > d.Length) return null;
                r.Add(new Resource { Id = id, Name = d.Slice(p + 6, nameTotal).ToArray(), Data = d.Slice(q, (int)size).ToArray() });
                p = q + (int)((size + 1) & ~1L);
            }
            return r;
        }

        static byte[] SerializeResources(List<Resource> list)
        {
            var o = new MemoryStream();
            Span<byte> b = stackalloc byte[4];
            foreach (var res in list)
            {
                o.Write("8BIM"u8);
                BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)res.Id); o.Write(b.Slice(0, 2));
                o.Write(res.Name);
                BinaryPrimitives.WriteUInt32BigEndian(b, (uint)res.Data.Length); o.Write(b);
                o.Write(res.Data);
                if ((res.Data.Length & 1) != 0) o.WriteByte(0);
            }
            return o.ToArray();
        }

        /// <summary>
        /// Photoshop resources with their IPTC block (0x0404) edited and its MD5 digest (0x0425) kept in step; null when no
        /// resource remains. Resources without IPTC are returned unchanged.
        /// </summary>
        public static byte[]? EditPhotoshop(byte[] resources, MetadataPlan plan, out bool changed)
        {
            changed = false;
            var list = ParseResources(resources);
            if (list == null) return resources;
            int i = list.FindIndex(r => r.Id == 0x0404);
            if (i < 0) return resources;
            var iim = EditIim(list[i].Data, plan);
            changed = true;
            if (iim == null) { list.RemoveAt(i); list.RemoveAll(r => r.Id == 0x0425); }
            else
            {
                list[i].Data = iim;
                foreach (var r in list) if (r.Id == 0x0425) r.Data = System.Security.Cryptography.MD5.HashData(iim);
            }
            return list.Count == 0 ? null : SerializeResources(list);
        }
    }
}
