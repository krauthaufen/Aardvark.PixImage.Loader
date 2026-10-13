using System;
using System.Collections.Generic;

namespace PixCodec
{
    /// <summary>
    /// How the components of a codestream become the channels of a <see cref="RawImage"/>: palette, channel
    /// definitions, colour space conversion, common sample type. Computed from the headers alone so that GetInfo and
    /// Decode agree.
    /// </summary>
    internal sealed class Jp2Layout
    {
        public struct Channel
        {
            public int Comp;
            public int PalCol;               // -1 = the component's samples themselves
            public int Depth;
            public bool Signed;
        }

        public Channel[] Channels = Array.Empty<Channel>();   // in output order (before colour conversion)
        public ColorModel Color;
        public SampleType Type;
        public int OutChannels, OutDepth, BitsPerSample;
        public bool OutSigned;
        public int Conversion;               // 0 none, 1 sYCC -> RGB, 2 CMYK -> RGB
        public int Alpha;                    // 0 none, 1 straight, 2 premultiplied; the alpha channel is the last one
        public bool Scale;                   // bring all channels to OutDepth

        public static Jp2Layout Create(Jp2Header h, J2kSiz siz, bool raw, int[]? components)
        {
            var l = new Jp2Layout();
            int nc = siz.NumComps;
            var chans = new List<Channel>();
            if (components != null)
            {
                foreach (int c in components)
                {
                    if (c < 0 || c >= nc) throw new ArgumentException("component index out of range");
                    chans.Add(new Channel { Comp = c, PalCol = -1, Depth = siz.Prec[c], Signed = siz.Signed[c] });
                }
            }
            else if (!raw && h.Palette != null && h.Cmap != null && h.Cmap.Length > 0)
            {
                foreach (var m in h.Cmap)
                {
                    if (m.Comp >= nc) throw new ImageFormatException("invalid JP2 file: component mapping out of range");
                    if (m.Type == 1)
                    {
                        if (m.Col >= h.PaletteCols) throw new ImageFormatException("invalid JP2 file: palette column out of range");
                        chans.Add(new Channel { Comp = m.Comp, PalCol = m.Col, Depth = (h.PaletteDepth![m.Col] & 0x7F) + 1, Signed = (h.PaletteDepth[m.Col] & 0x80) != 0 });
                    }
                    else chans.Add(new Channel { Comp = m.Comp, PalCol = -1, Depth = siz.Prec[m.Comp], Signed = siz.Signed[m.Comp] });
                }
            }
            else
            {
                for (int c = 0; c < nc; c++) chans.Add(new Channel { Comp = c, PalCol = -1, Depth = siz.Prec[c], Signed = siz.Signed[c] });
            }
            int n = chans.Count;

            int maxDepth = 0;
            bool anySigned = false, allSigned = true, sameDepth = true;
            foreach (var c in chans)
            {
                maxDepth = Math.Max(maxDepth, c.Depth);
                anySigned |= c.Signed; allSigned &= c.Signed;
                sameDepth &= c.Depth == chans[0].Depth;
            }
            l.BitsPerSample = maxDepth;
            if (anySigned && !allSigned)
            {
                // unsigned values next to signed ones need a sign bit of their own
                foreach (var c in chans) if (!c.Signed) maxDepth = Math.Max(maxDepth, c.Depth + 1);
            }

            // roles: colour number 1.., -1 straight alpha, -2 premultiplied alpha, 0 anything else
            var role = new int[n];
            int colours;
            if (raw) colours = 0;
            else
            {
                int cs = h.ColorMethod == 1 ? h.EnumCs : -1;
                if (cs == Jp2Header.CsGray || cs == Jp2Header.CsBilevel || cs == 15) colours = 1;
                else if (cs == Jp2Header.CsCmyk) colours = 4;
                else if (cs >= 0) colours = 3;
                else if (h.ColorMethod == 2 && h.Icc!.Length >= 20)
                {
                    var sig = h.Icc.AsSpan(16, 4);
                    colours = sig.SequenceEqual("GRAY"u8) ? 1 : sig.SequenceEqual("CMYK"u8) ? 4 : 3;
                }
                else colours = n <= 2 ? 1 : 3;
                if (colours > n) colours = 0;
                if (colours > 0)
                {
                    if (h.Cdef != null && h.IsJp2)
                    {
                        foreach (var d in h.Cdef)
                        {
                            if (d.Channel >= n) continue;
                            role[d.Channel] = d.Type == 0 ? (d.Assoc >= 1 && d.Assoc <= colours ? d.Assoc : 0)
                                : d.Type == 1 && d.Assoc == 0 ? -1 : d.Type == 2 && d.Assoc == 0 ? -2 : 0;
                        }
                    }
                    else
                    {
                        for (int i = 0; i < colours; i++) role[i] = i + 1;
                        if (n == colours + 1) role[n - 1] = -1;
                    }
                    // every colour exactly once, and nothing else but at most one alpha channel
                    var seen = new bool[colours + 1];
                    int alphas = 0;
                    bool ok = true;
                    for (int i = 0; i < n; i++)
                    {
                        if (role[i] > 0) { if (seen[role[i]]) ok = false; seen[role[i]] = true; }
                        else if (role[i] < 0) alphas++;
                        else ok = false;
                    }
                    for (int i = 1; i <= colours; i++) ok &= seen[i];
                    if (!ok || alphas > 1) colours = 0;
                }
            }

            if (colours == 0)
            {
                l.Channels = chans.ToArray();
                l.Color = n == 1 ? ColorModel.Gray : ColorModel.Other;
                l.OutChannels = n;
            }
            else
            {
                var ordered = new Channel[n];
                int alphaAt = -1;
                for (int i = 0; i < n; i++)
                {
                    if (role[i] > 0) ordered[role[i] - 1] = chans[i];
                    else { alphaAt = i; l.Alpha = -role[i]; }
                }
                if (alphaAt >= 0) ordered[n - 1] = chans[alphaAt];
                l.Channels = ordered;
                int cs = h.ColorMethod == 1 ? h.EnumCs : -1;
                bool uniform = true;
                for (int i = 0; i < colours; i++) uniform &= !ordered[i].Signed && ordered[i].Depth == ordered[0].Depth;
                if (colours == 3 && (cs == Jp2Header.CsSycc || cs == Jp2Header.CsEsycc || cs == 1 || cs == 3 || cs == 4) && uniform) l.Conversion = 1;
                // Second and third components subsampled 2 x 1 or 2 x 2 under a full resolution first one are luma and
                // chroma even if nothing (a raw codestream) or the wrong thing (sRGB) is declared; OpenJPEG decides the
                // same way, and like there other subsampling layouts are left alone.
                if (colours == 3 && (cs < 0 || cs == Jp2Header.CsSrgb) && h.ColorMethod != 2 && uniform
                    && siz.Dx[ordered[0].Comp] == 1 && siz.Dy[ordered[0].Comp] == 1 && siz.Dx[ordered[1].Comp] == 2 && siz.Dy[ordered[1].Comp] <= 2
                    && siz.Dx[ordered[1].Comp] == siz.Dx[ordered[2].Comp] && siz.Dy[ordered[1].Comp] == siz.Dy[ordered[2].Comp]
                    && ordered[0].PalCol < 0) l.Conversion = 1;
                if (colours == 4)
                {
                    if (!uniform)
                    {
                        // CMYK that cannot be converted stays as it is
                        l.Channels = chans.ToArray(); l.Color = ColorModel.Other; l.OutChannels = n; l.Alpha = 0;
                        colours = 0;
                    }
                    else l.Conversion = 2;
                }
                if (colours != 0)
                {
                    int outColours = colours == 4 ? 3 : colours;
                    l.OutChannels = outColours + (l.Alpha != 0 ? 1 : 0);
                    l.Color = outColours == 1 ? (l.Alpha != 0 ? ColorModel.GrayAlpha : ColorModel.Gray) : (l.Alpha != 0 ? ColorModel.Rgba : ColorModel.Rgb);
                }
            }

            // Unsigned samples of less than 8 bit are scaled to 8 bit (a bilevel image is 0 / 255), deeper ones stay as stored.
            if (!raw && !anySigned && maxDepth < 8) { maxDepth = 8; sameDepth = false; }
            l.OutDepth = maxDepth;
            l.OutSigned = anySigned;
            l.Scale = !raw && !sameDepth && !anySigned;
            l.Type = maxDepth <= 8 ? (anySigned ? SampleType.Int8 : SampleType.UInt8)
                : maxDepth <= 16 ? (anySigned ? SampleType.Int16 : SampleType.UInt16)
                : maxDepth <= 32 ? (anySigned ? SampleType.Int32 : SampleType.UInt32)
                : (anySigned ? SampleType.Int64 : SampleType.UInt64);
            return l;
        }

        /// <summary>Interleaves the decoded planes into the output image.</summary>
        public unsafe RawImage Assemble(J2kPlanes planes, Jp2Header h)
        {
            int w = planes.Width, hgt = planes.Height;
            int n = Channels.Length;
            if ((long)w * hgt * OutChannels > Array.MaxLength) throw new ImageNotSupportedException("JPEG 2000 image too large for one array");
            var data = RawImage.CreateArray(Type, w * hgt * OutChannels);
            var img = new RawImage(w, hgt, Color, data, OutChannels);

            // fast path: full resolution 8 / 16 bit components copied straight through
            bool simple = Conversion == 0 && Alpha != 2 && !Scale && (Type == SampleType.UInt8 || Type == SampleType.UInt16);
            foreach (var c in Channels)
            {
                var p = planes.Planes[c.Comp];
                simple &= c.PalCol < 0 && p.Data != null && p.Width == w && p.Height == hgt && p.Dx == 1 && p.Dy == 1;
            }
            if (simple)
            {
                long count = (long)w * hgt;
                if (Type == SampleType.UInt8)
                    fixed (byte* dst = (byte[])data)
                        for (int c = 0; c < n; c++)
                            fixed (int* src = planes.Planes[Channels[c].Comp].Data)
                            {
                                byte* d = dst + c;
                                for (long i = 0; i < count; i++, d += n) *d = (byte)src[i];
                            }
                else
                    fixed (ushort* dst = (ushort[])data)
                        for (int c = 0; c < n; c++)
                            fixed (int* src = planes.Planes[Channels[c].Comp].Data)
                            {
                                ushort* d = dst + c;
                                for (long i = 0; i < count; i++, d += n) *d = (ushort)src[i];
                            }
                return img;
            }

            // general path: one row of every channel as long values, then convert and store
            var rows = new long[n][];
            for (int c = 0; c < n; c++) rows[c] = new long[w];
            var xmap = new int[n][];
            for (int c = 0; c < n; c++)
            {
                var p = planes.Planes[Channels[c].Comp];
                if (p.Data == null && p.Data64 == null) throw new InvalidOperationException("component was not decoded");
                xmap[c] = new int[w];
                for (int x = 0; x < w; x++) xmap[c][x] = (int)Math.Clamp((planes.X0 + x) / p.Dx - p.X0, 0, p.Width - 1);
            }
            long outMax = OutSigned ? 0 : OutDepth >= 63 ? long.MaxValue : (1L << OutDepth) - 1;
            int colourCount = n - (Alpha != 0 ? 1 : 0);
            var bytes = Type == SampleType.UInt8 ? (byte[])data : null;
            var shorts = Type == SampleType.UInt16 ? (ushort[])data : null;
            for (int y = 0; y < hgt; y++)
            {
                for (int c = 0; c < n; c++)
                {
                    var ch = Channels[c];
                    var p = planes.Planes[ch.Comp];
                    int py = (int)Math.Clamp((planes.Y0 + y) / p.Dy - p.Y0, 0, p.Height - 1);
                    long basei = (long)py * p.Width;
                    var row = rows[c]; var map = xmap[c];
                    bool unsigned32 = !p.Signed && p.Precision == 32;
                    if (p.Data64 != null) for (int x = 0; x < w; x++) row[x] = p.Data64[basei + map[x]];
                    else if (unsigned32) for (int x = 0; x < w; x++) row[x] = (uint)p.Data![basei + map[x]];
                    else for (int x = 0; x < w; x++) row[x] = p.Data![basei + map[x]];
                    if (ch.PalCol >= 0)
                    {
                        var pal = h.Palette!;
                        int cols = h.PaletteCols, last = h.PaletteEntries - 1;
                        for (int x = 0; x < w; x++) row[x] = pal[(int)Math.Clamp(row[x], 0, last) * cols + ch.PalCol];
                    }
                    if (Scale && ch.Depth != OutDepth)
                    {
                        long inMax = (1L << ch.Depth) - 1;
                        for (int x = 0; x < w; x++) row[x] = (row[x] * outMax + inMax / 2) / inMax;
                    }
                }
                if (Conversion == 1)
                {
                    // as OpenJPEG's sycc to rgb: products truncated toward zero
                    long[] ry = rows[0], rcb = rows[1], rcr = rows[2];
                    int off = 1 << (OutDepth - 1);
                    for (int x = 0; x < w; x++)
                    {
                        int yy = (int)ry[x], cb = (int)rcb[x] - off, cr = (int)rcr[x] - off;
                        ry[x] = Math.Clamp(yy + (int)(1.402 * cr), 0, outMax);
                        rcb[x] = Math.Clamp(yy - (int)(0.344 * cb + 0.714 * cr), 0, outMax);
                        rcr[x] = Math.Clamp(yy + (int)(1.772 * cb), 0, outMax);
                    }
                }
                else if (Conversion == 2)
                {
                    // as OpenJPEG's cmyk to rgb: 0 is no ink
                    long[] rc = rows[0], rm = rows[1], ryl = rows[2], rk = rows[3];
                    float s = 1f / outMax;
                    for (int x = 0; x < w; x++)
                    {
                        float k = 1f - rk[x] * s;
                        rc[x] = (long)(outMax * (1f - rc[x] * s) * k);
                        rm[x] = (long)(outMax * (1f - rm[x] * s) * k);
                        ryl[x] = (long)(outMax * (1f - ryl[x] * s) * k);
                    }
                    if (Alpha != 0) rows[4].AsSpan().CopyTo(rows[3]);
                }
                int oc = OutChannels;
                if (Alpha == 2)
                {
                    var ra = rows[oc - 1 + (Conversion == 2 ? 0 : 0)];
                    for (int c = 0; c < oc - 1; c++)
                    {
                        var row = rows[c];
                        for (int x = 0; x < w; x++)
                        {
                            long a = ra[x];
                            if (a > 0 && a < outMax) row[x] = Math.Min((row[x] * outMax + a / 2) / a, outMax);
                        }
                    }
                }
                _ = colourCount;
                long o = (long)y * w * oc;
                for (int c = 0; c < oc; c++)
                {
                    var row = rows[c];
                    long i = o + c;
                    if (bytes != null) for (int x = 0; x < w; x++, i += oc) bytes[i] = (byte)row[x];
                    else if (shorts != null) for (int x = 0; x < w; x++, i += oc) shorts[i] = (ushort)row[x];
                    else switch (Type)
                    {
                        case SampleType.Int8: { var a = (sbyte[])data; for (int x = 0; x < w; x++, i += oc) a[i] = (sbyte)row[x]; break; }
                        case SampleType.Int16: { var a = (short[])data; for (int x = 0; x < w; x++, i += oc) a[i] = (short)row[x]; break; }
                        case SampleType.UInt32: { var a = (uint[])data; for (int x = 0; x < w; x++, i += oc) a[i] = (uint)row[x]; break; }
                        case SampleType.Int32: { var a = (int[])data; for (int x = 0; x < w; x++, i += oc) a[i] = (int)row[x]; break; }
                        case SampleType.UInt64: { var a = (ulong[])data; for (int x = 0; x < w; x++, i += oc) a[i] = (ulong)row[x]; break; }
                        default: { var a = (long[])data; for (int x = 0; x < w; x++, i += oc) a[i] = row[x]; break; }
                    }
                }
            }
            return img;
        }
    }
}
