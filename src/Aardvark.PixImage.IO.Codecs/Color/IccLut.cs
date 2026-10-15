using System;
using System.Collections.Generic;

namespace Aardvark.ImageCodecs
{
    internal enum IccLutKind { Lut8, Lut16, AToB, BToA }

    /// <summary>Reads the lookup table tag types (lut8Type, lut16Type, lutAToBType, lutBToAType) into pipeline stages.</summary>
    internal static class IccLut
    {
        public const int MaxChannels = 15;

        /// <summary>
        /// The stages of a LUT tag, mapping normalised inputs (0..1, PCS values in the tag's encoding) to normalised outputs.
        /// <paramref name="inputIsXyz"/> enables the matrix of lut8 / lut16 tables, which applies to XYZ input only.
        /// </summary>
        public static List<IccStage> Read(ReadOnlySpan<byte> tag, bool inputIsXyz, out int inCh, out int outCh, out IccLutKind kind,
            bool trilinear = false)
        {
            if (tag.Length < 32) throw IccRead.Bad("LUT tag is truncated");
            uint sig = IccRead.U32(tag, 0);
            inCh = tag[8]; outCh = tag[9];
            if (inCh < 1 || inCh > MaxChannels || outCh < 1 || outCh > MaxChannels) throw IccRead.Bad("LUT channel count out of range");
            if (sig == IccSig.TMft1 || sig == IccSig.TMft2)
            {
                kind = sig == IccSig.TMft1 ? IccLutKind.Lut8 : IccLutKind.Lut16;
                return ReadMft(tag, kind == IccLutKind.Lut16, inCh, outCh, inputIsXyz, trilinear);
            }
            if (sig == IccSig.TMab || sig == IccSig.TMba)
            {
                kind = sig == IccSig.TMab ? IccLutKind.AToB : IccLutKind.BToA;
                return ReadMab(tag, kind == IccLutKind.AToB, inCh, outCh, trilinear);
            }
            throw new IccException($"ICC: LUT type '{IccSig.ToText(sig)}' is not supported");
        }

        static List<IccStage> ReadMft(ReadOnlySpan<byte> t, bool sixteen, int inCh, int outCh, bool inputIsXyz, bool trilinear)
        {
            int g = t[10];
            if (g < 2) throw IccRead.Bad("LUT grid needs at least 2 points");
            var stages = new List<IccStage>();
            var m = new double[9];
            for (int i = 0; i < 9; i++) m[i] = IccRead.S15(t, 12 + 4 * i);
            if (inputIsXyz && inCh == 3)
            {
                var a = new AffineStage(3, 3, m);
                if (!a.IsIdentity) stages.Add(a);
            }
            int n, k, pos, bytes = sixteen ? 2 : 1;
            if (sixteen)
            {
                IccRead.Need(t, 48, 4, "lut16");
                n = IccRead.U16(t, 48); k = IccRead.U16(t, 50); pos = 52;
                if (n < 2 || n > 4096 || k < 2 || k > 4096) throw IccRead.Bad("lut16 table size out of range");
            }
            else { n = k = 256; pos = 48; }
            long clutEntries = ClutEntries(Grid(inCh, g), outCh, (t.Length - pos) / bytes);
            long total = (long)inCh * n + clutEntries + (long)outCh * k;
            if (pos + total * bytes > t.Length) throw IccRead.Bad("LUT is truncated");
            var inCurves = new IccCurve[inCh];
            for (int c = 0; c < inCh; c++) { inCurves[c] = Table(t, pos, n, sixteen); pos += n * bytes; }
            var clut = new float[clutEntries];
            float scale = sixteen ? 1 / 65535f : 1 / 255f;
            for (int i = 0; i < clut.Length; i++, pos += bytes) clut[i] = (sixteen ? IccRead.U16(t, pos) : t[pos]) * scale;
            var outCurves = new IccCurve[outCh];
            for (int c = 0; c < outCh; c++) { outCurves[c] = Table(t, pos, k, sixteen); pos += k * bytes; }
            stages.Add(new CurvesStage(inCurves));
            stages.Add(new ClutStage(Grid(inCh, g), outCh, clut, trilinear));
            stages.Add(new CurvesStage(outCurves));
            return stages;
        }

        static IccCurve Table(ReadOnlySpan<byte> t, int pos, int n, bool sixteen)
        {
            var v = new ushort[n];
            for (int i = 0; i < n; i++) v[i] = sixteen ? IccRead.U16(t, pos + 2 * i) : (ushort)(t[pos + i] * 257);
            return IccCurve.Table(v);
        }

        static int[] Grid(int inCh, int g)
        {
            var r = new int[inCh];
            Array.Fill(r, g);
            return r;
        }

        /// <summary>Number of CLUT values, rejecting tables larger than <paramref name="limit"/> before anything is allocated.</summary>
        static long ClutEntries(int[] grid, int outCh, long limit)
        {
            long e = outCh;
            foreach (var g in grid)
            {
                e *= g;
                if (e > limit || e > int.MaxValue / 4) throw IccRead.Bad("CLUT is truncated or too large");
            }
            return e;
        }

        static List<IccStage> ReadMab(ReadOnlySpan<byte> t, bool aToB, int inCh, int outCh, bool trilinear)
        {
            uint offB = IccRead.U32(t, 12), offMat = IccRead.U32(t, 16), offM = IccRead.U32(t, 20), offClut = IccRead.U32(t, 24), offA = IccRead.U32(t, 28);
            if (offB == 0) throw IccRead.Bad("lutAToB / lutBToA without B curves");
            if (offClut == 0 && inCh != outCh) throw IccRead.Bad("LUT without CLUT changes the channel count");
            // the matrix sits on the PCS side, where there are 3 channels
            int pcsCh = aToB ? outCh : inCh;
            int devCh = aToB ? inCh : outCh;
            var stages = new List<IccStage>();
            IccCurve[]? a = offA != 0 ? Curves(t, offA, devCh) : null;
            IccCurve[]? mc = offM != 0 ? Curves(t, offM, pcsCh) : null;
            IccCurve[] b = Curves(t, offB, pcsCh);
            AffineStage? mat = null;
            if (offMat != 0)
            {
                if (pcsCh != 3) throw IccRead.Bad("LUT matrix needs 3 channels");
                if (offMat > t.Length - 48) throw IccRead.Bad("LUT matrix is truncated");
                var m = new double[9];
                var o = new double[3];
                for (int i = 0; i < 9; i++) m[i] = IccRead.S15(t, (int)offMat + 4 * i);
                for (int i = 0; i < 3; i++) o[i] = IccRead.S15(t, (int)offMat + 36 + 4 * i);
                mat = new AffineStage(3, 3, m, o);
            }
            ClutStage? clut = offClut != 0 ? ReadClut(t, offClut, inCh, outCh, trilinear) : null;
            if (aToB)
            {
                if (a != null) stages.Add(new CurvesStage(a));
                if (clut != null) stages.Add(clut);
                if (mc != null) stages.Add(new CurvesStage(mc));
                if (mat != null) stages.Add(mat);
                stages.Add(new CurvesStage(b));
            }
            else
            {
                stages.Add(new CurvesStage(b));
                if (mat != null) stages.Add(mat);
                if (mc != null) stages.Add(new CurvesStage(mc));
                if (clut != null) stages.Add(clut);
                if (a != null) stages.Add(new CurvesStage(a));
            }
            return stages;
        }

        static IccCurve[] Curves(ReadOnlySpan<byte> t, uint off, int n)
        {
            if (off > t.Length) throw IccRead.Bad("LUT curve offset out of range");
            var r = new IccCurve[n];
            int pos = (int)off;
            for (int i = 0; i < n; i++)
            {
                r[i] = IccCurve.Read(t, pos, out int len);
                pos += (len + 3) & ~3;
            }
            return r;
        }

        static ClutStage ReadClut(ReadOnlySpan<byte> t, uint off, int inCh, int outCh, bool trilinear)
        {
            if (off > t.Length - 20) throw IccRead.Bad("CLUT is truncated");
            int p = (int)off;
            var grid = new int[inCh];
            for (int i = 0; i < inCh; i++)
            {
                grid[i] = t[p + i];
                if (grid[i] < 1) throw IccRead.Bad("CLUT grid dimension is 0");
            }
            int prec = t[p + 16];
            if (prec != 1 && prec != 2) throw IccRead.Bad($"CLUT precision {prec}");
            p += 20;
            long n = ClutEntries(grid, outCh, (t.Length - p) / prec);
            var v = new float[n];
            if (prec == 2) for (int i = 0; i < v.Length; i++) v[i] = IccRead.U16(t, p + 2 * i) / 65535f;
            else for (int i = 0; i < v.Length; i++) v[i] = t[p + i] / 255f;
            return new ClutStage(grid, outCh, v, trilinear);
        }
    }
}
