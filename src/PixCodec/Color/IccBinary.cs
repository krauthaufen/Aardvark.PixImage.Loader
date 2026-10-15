using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace PixCodec
{
    /// <summary>Bounds checked big-endian reads of ICC data; every violation is an <see cref="ImageFormatException"/>.</summary>
    internal static class IccRead
    {
        public static ImageFormatException Bad(string what) => new("ICC: " + what);

        public static void Need(ReadOnlySpan<byte> d, int pos, int len, string what)
        {
            if (pos < 0 || len < 0 || (long)pos + len > d.Length) throw Bad(what + " is truncated");
        }

        public static uint U32(ReadOnlySpan<byte> d, int pos) { Need(d, pos, 4, "tag"); return BinaryPrimitives.ReadUInt32BigEndian(d.Slice(pos)); }
        public static ushort U16(ReadOnlySpan<byte> d, int pos) { Need(d, pos, 2, "tag"); return BinaryPrimitives.ReadUInt16BigEndian(d.Slice(pos)); }
        public static byte U8(ReadOnlySpan<byte> d, int pos) { Need(d, pos, 1, "tag"); return d[pos]; }
        public static double S15(ReadOnlySpan<byte> d, int pos) => (int)U32(d, pos) / 65536.0;
        public static double U8F8(ReadOnlySpan<byte> d, int pos) => U16(d, pos) / 256.0;

        public static CieXyz Xyz(ReadOnlySpan<byte> d, int pos) => new(S15(d, pos), S15(d, pos + 4), S15(d, pos + 8));

        /// <summary>A whole XYZType tag (the first value).</summary>
        public static CieXyz XyzTag(ReadOnlySpan<byte> tag)
        {
            if (tag.Length < 20 || U32(tag, 0) != IccSig.TXyz) throw Bad("XYZ tag expected");
            var v = Xyz(tag, 8);
            if (!double.IsFinite(v.X + v.Y + v.Z)) throw Bad("bad XYZ value");
            return v;
        }

        /// <summary>An s15Fixed16ArrayType tag of 9 values (chad).</summary>
        public static double[] Sf32Matrix(ReadOnlySpan<byte> tag)
        {
            if (tag.Length < 8 + 36 || U32(tag, 0) != IccSig.TSf32) throw Bad("s15Fixed16Array tag of 9 values expected");
            var m = new double[9];
            for (int i = 0; i < 9; i++) m[i] = S15(tag, 8 + 4 * i);
            return m;
        }

        /// <summary>Text of a desc (v2), mluc (v4) or text tag; null when the tag is none of these.</summary>
        public static string? Text(ReadOnlySpan<byte> tag)
        {
            if (tag.Length < 8) return null;
            uint t = U32(tag, 0);
            if (t == IccSig.TText) return Ascii(tag.Slice(8));
            if (t == IccSig.TDesc)
            {
                if (tag.Length < 12) return null;
                uint n = U32(tag, 8);
                if (n > tag.Length - 12) n = (uint)(tag.Length - 12);
                return Ascii(tag.Slice(12, (int)n));
            }
            if (t == IccSig.TMluc)
            {
                if (tag.Length < 16) return null;
                uint count = U32(tag, 8), recSize = U32(tag, 12);
                if (recSize < 12 || count == 0) return null;
                string? first = null;
                for (long i = 0; i < count; i++)
                {
                    long rec = 16 + i * recSize;
                    if (rec + 12 > tag.Length) break;
                    int r = (int)rec;
                    uint len = U32(tag, r + 4), off = U32(tag, r + 8);
                    if (off > tag.Length || len > tag.Length - off) continue;
                    var s = Encoding.BigEndianUnicode.GetString(tag.Slice((int)off, (int)len & ~1)).TrimEnd('\0');
                    bool en = tag[r] == 'e' && tag[r + 1] == 'n';
                    if (en && tag[r + 2] == 'U' && tag[r + 3] == 'S') return s;
                    if (first == null || en) first = s;
                }
                return first;
            }
            return null;
        }

        static string Ascii(ReadOnlySpan<byte> s)
        {
            int z = s.IndexOf((byte)0);
            if (z >= 0) s = s.Slice(0, z);
            return Encoding.Latin1.GetString(s).Trim();
        }
    }

    /// <summary>Big-endian ICC writer.</summary>
    internal sealed class IccWrite
    {
        readonly List<byte> b = new();
        public int Length => b.Count;
        public byte[] ToArray() => b.ToArray();
        public void U8(int v) => b.Add((byte)v);
        public void U16(int v) { b.Add((byte)(v >> 8)); b.Add((byte)v); }
        public void U32(uint v) { b.Add((byte)(v >> 24)); b.Add((byte)(v >> 16)); b.Add((byte)(v >> 8)); b.Add((byte)v); }
        public void S15(double v) => U32((uint)(int)Math.Round(v * 65536));
        public void Sig(uint s) => U32(s);
        public void Zeros(int n) { for (int i = 0; i < n; i++) b.Add(0); }
        public void Bytes(ReadOnlySpan<byte> s) { foreach (var x in s) b.Add(x); }
        public void Pad4() { while ((b.Count & 3) != 0) b.Add(0); }
        public void Xyz(CieXyz v) { S15(v.X); S15(v.Y); S15(v.Z); }
        public void SetU32(int pos, uint v) { b[pos] = (byte)(v >> 24); b[pos + 1] = (byte)(v >> 16); b[pos + 2] = (byte)(v >> 8); b[pos + 3] = (byte)v; }
    }
}
