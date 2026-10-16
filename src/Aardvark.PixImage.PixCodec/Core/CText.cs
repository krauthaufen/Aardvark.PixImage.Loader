using System;

namespace Aardvark.ImageCodecs
{
    /// <summary>A minimal scanner for the C source text of XBM and XPM files: white space, comments, words, numbers, strings.</summary>
    internal ref struct CText
    {
        readonly ReadOnlySpan<byte> d;
        public int Pos;

        public CText(ReadOnlySpan<byte> data, int pos = 0) { d = data; Pos = pos; }

        public bool AtEnd { get { Skip(); return Pos >= d.Length; } }

        public static bool IsSpace(byte c) => c is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\f' or (byte)'\v';
        static bool IsWord(byte c) => c is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9' or (byte)'_';

        /// <summary>Skips white space and /* */ and // comments (an unterminated comment runs to the end).</summary>
        public void Skip()
        {
            while (Pos < d.Length)
            {
                byte c = d[Pos];
                if (IsSpace(c)) { Pos++; continue; }
                if (c == '/' && Pos + 1 < d.Length && d[Pos + 1] == '*')
                {
                    int e = d.Slice(Pos + 2).IndexOf("*/"u8);
                    Pos = e < 0 ? d.Length : Pos + 2 + e + 2;
                    continue;
                }
                if (c == '/' && Pos + 1 < d.Length && d[Pos + 1] == '/')
                {
                    int e = d.Slice(Pos).IndexOf((byte)'\n');
                    Pos = e < 0 ? d.Length : Pos + e + 1;
                    continue;
                }
                break;
            }
        }

        /// <summary>The next word ([A-Za-z0-9_]+), empty if the next character is something else.</summary>
        public ReadOnlySpan<byte> Word()
        {
            Skip();
            int s = Pos;
            while (Pos < d.Length && IsWord(d[Pos])) Pos++;
            return d.Slice(s, Pos - s);
        }

        /// <summary>Consumes the character if it comes next.</summary>
        public bool Char(byte c)
        {
            Skip();
            if (Pos < d.Length && d[Pos] == c) { Pos++; return true; }
            return false;
        }

        public bool Peek(byte c)
        {
            Skip();
            return Pos < d.Length && d[Pos] == c;
        }

        /// <summary>A C integer literal (decimal, 0x hexadecimal, 0 octal, optional sign and u / l suffixes).</summary>
        public bool Number(out long v)
        {
            v = 0;
            int save = Pos;
            bool neg = Char((byte)'-');
            var w = Word();
            if (TryParse(w, out v)) { if (neg) v = -v; return true; }
            Pos = save;
            return false;
        }

        public static bool TryParse(ReadOnlySpan<byte> w, out long v)
        {
            v = 0;
            while (w.Length > 1 && w[^1] is (byte)'u' or (byte)'U' or (byte)'l' or (byte)'L') w = w.Slice(0, w.Length - 1);
            if (w.IsEmpty || w.Length > 20) return false;
            int radix = 10, i = 0;
            if (w.Length > 2 && w[0] == '0' && w[1] is (byte)'x' or (byte)'X') { radix = 16; i = 2; }
            else if (w.Length > 1 && w[0] == '0') { radix = 8; i = 1; }
            for (; i < w.Length; i++)
            {
                int c = w[i], k = c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'f' ? c - 'a' + 10 : c is >= 'A' and <= 'F' ? c - 'A' + 10 : 99;
                if (k >= radix) return false;
                v = v * radix + k;
                if (v > uint.MaxValue) return false;
            }
            return true;
        }

        /// <summary>A double quoted string: (start, length) of its contents, which are taken as they are (no escapes).</summary>
        public bool String(out int start, out int length)
        {
            start = length = 0;
            Skip();
            if (Pos >= d.Length || d[Pos] != '"') return false;
            int e = d.Slice(Pos + 1).IndexOfAny((byte)'"', (byte)'\n');
            if (e < 0 || d[Pos + 1 + e] != '"') return false;
            start = Pos + 1; length = e;
            Pos = start + e + 1;
            return true;
        }

        /// <summary>Moves past the next occurrence of the character outside comments; false at the end.</summary>
        public bool SkipPast(byte c)
        {
            while (true)
            {
                Skip();
                if (Pos >= d.Length) return false;
                if (d[Pos] == '"')
                {
                    if (!String(out _, out _)) Pos++;
                    continue;
                }
                if (d[Pos++] == c) return true;
            }
        }
    }
}
