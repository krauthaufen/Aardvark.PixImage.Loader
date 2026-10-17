using System;
using System.Collections.Generic;

namespace Aardvark.ImageCodecs.Av1
{
    /// <summary>
    /// Layout of all adaptive CDF arrays of AV1 (those listed in the semantics of init_coeff_cdfs and init_non_coeff_cdfs,
    /// spec 6.8.2) in one ushort array, so that loading, saving and copying a context are single array copies. Each CDF of
    /// N symbols takes N + 1 entries (the last is the adaptation counter).
    /// </summary>
    internal static class Av1Cdf
    {
        delegate ReadOnlySpan<ushort> Defaults();

        readonly struct Entry
        {
            public readonly int Offset, Length, N;
            public readonly Defaults Src;
            public readonly int SrcStride;   // per q context for coefficient CDFs, else 0
            public Entry(int offset, int length, int n, Defaults src, int srcStride) { Offset = offset; Length = length; N = n; Src = src; SrcStride = srcStride; }
        }

        static readonly List<Entry> entries = new();
        static int total;

        static int Add(Defaults src, int n, int copies = 1, int coeffQctxStride = 0)
        {
            int len = coeffQctxStride != 0 ? coeffQctxStride : src().Length;
            if (len % (n + 1) != 0) throw new InvalidOperationException("AV1 CDF table layout");
            int off = total;
            for (int i = 0; i < copies; i++)
            {
                entries.Add(new Entry(total, len, n, src, coeffQctxStride));
                total += len;
            }
            return off;
        }

        // non coefficient CDFs
        public static readonly int YMode = Add(() => Av1Tables.DefaultYModeCdf, 13);
        public static readonly int UVModeCflNotAllowed = Add(() => Av1Tables.DefaultUvModeCflNotAllowedCdf, 13);
        public static readonly int UVModeCflAllowed = Add(() => Av1Tables.DefaultUvModeCflAllowedCdf, 14);
        public static readonly int AngleDelta = Add(() => Av1Tables.DefaultAngleDeltaCdf, 7);
        public static readonly int Intrabc = Add(() => Av1Tables.DefaultIntrabcCdf, 2);
        public static readonly int PartitionW8 = Add(() => Av1Tables.DefaultPartitionW8Cdf, 4);
        public static readonly int PartitionW16 = Add(() => Av1Tables.DefaultPartitionW16Cdf, 10);
        public static readonly int PartitionW32 = Add(() => Av1Tables.DefaultPartitionW32Cdf, 10);
        public static readonly int PartitionW64 = Add(() => Av1Tables.DefaultPartitionW64Cdf, 10);
        public static readonly int PartitionW128 = Add(() => Av1Tables.DefaultPartitionW128Cdf, 8);
        public static readonly int SegmentId = Add(() => Av1Tables.DefaultSegmentIdCdf, 8);
        public static readonly int SegmentIdPredicted = Add(() => Av1Tables.DefaultSegmentIdPredictedCdf, 2);
        public static readonly int Tx8x8 = Add(() => Av1Tables.DefaultTx8x8Cdf, 2);
        public static readonly int Tx16x16 = Add(() => Av1Tables.DefaultTx16x16Cdf, 3);
        public static readonly int Tx32x32 = Add(() => Av1Tables.DefaultTx32x32Cdf, 3);
        public static readonly int Tx64x64 = Add(() => Av1Tables.DefaultTx64x64Cdf, 3);
        public static readonly int TxfmSplit = Add(() => Av1Tables.DefaultTxfmSplitCdf, 2);
        public static readonly int FilterIntraMode = Add(() => Av1Tables.DefaultFilterIntraModeCdf, 5);
        public static readonly int FilterIntra = Add(() => Av1Tables.DefaultFilterIntraCdf, 2);
        public static readonly int InterpFilter = Add(() => Av1Tables.DefaultInterpFilterCdf, 3);
        public static readonly int MotionMode = Add(() => Av1Tables.DefaultMotionModeCdf, 3);
        public static readonly int NewMv = Add(() => Av1Tables.DefaultNewMvCdf, 2);
        public static readonly int ZeroMv = Add(() => Av1Tables.DefaultZeroMvCdf, 2);
        public static readonly int RefMv = Add(() => Av1Tables.DefaultRefMvCdf, 2);
        public static readonly int CompoundMode = Add(() => Av1Tables.DefaultCompoundModeCdf, 8);
        public static readonly int DrlMode = Add(() => Av1Tables.DefaultDrlModeCdf, 2);
        public static readonly int IsInter = Add(() => Av1Tables.DefaultIsInterCdf, 2);
        public static readonly int CompMode = Add(() => Av1Tables.DefaultCompModeCdf, 2);
        public static readonly int SkipMode = Add(() => Av1Tables.DefaultSkipModeCdf, 2);
        public static readonly int Skip = Add(() => Av1Tables.DefaultSkipCdf, 2);
        public static readonly int CompRef = Add(() => Av1Tables.DefaultCompRefCdf, 2);           // [ctx][3]
        public static readonly int CompBwdRef = Add(() => Av1Tables.DefaultCompBwdRefCdf, 2);     // [ctx][2]
        public static readonly int SingleRef = Add(() => Av1Tables.DefaultSingleRefCdf, 2);       // [ctx][6]
        // motion vectors: one set per MvCtx (MV_CONTEXTS = 2), each [comp] where the spec indexes by comp
        public static readonly int MvJoint = Add(() => Av1Tables.DefaultMvJointCdf, 4, 2);                 // [ctx]
        public static readonly int MvClass = Add(() => Av1Tables.DefaultMvClassCdf, 11, 2);               // [ctx][comp]
        public static readonly int MvClass0Bit = Add(() => Av1Tables.DefaultMvClass0BitCdf, 2, 4);        // [ctx][comp]
        public static readonly int MvFr = Add(() => Av1Tables.DefaultMvFrCdf, 4, 2);                      // [ctx][comp]
        public static readonly int MvClass0Fr = Add(() => Av1Tables.DefaultMvClass0FrCdf, 4, 2);          // [ctx][comp][bit]
        public static readonly int MvClass0Hp = Add(() => Av1Tables.DefaultMvClass0HpCdf, 2, 4);          // [ctx][comp]
        public static readonly int MvSign = Add(() => Av1Tables.DefaultMvSignCdf, 2, 4);                  // [ctx][comp]
        public static readonly int MvBit = Add(() => Av1Tables.DefaultMvBitCdf, 2, 4);                    // [ctx][comp][i]
        public static readonly int MvHp = Add(() => Av1Tables.DefaultMvHpCdf, 2, 4);                      // [ctx][comp]
        public static readonly int PaletteYMode = Add(() => Av1Tables.DefaultPaletteYModeCdf, 2);       // [bsizeCtx][ctx]
        public static readonly int PaletteUVMode = Add(() => Av1Tables.DefaultPaletteUvModeCdf, 2);
        public static readonly int PaletteYSize = Add(() => Av1Tables.DefaultPaletteYSizeCdf, 7);
        public static readonly int PaletteUVSize = Add(() => Av1Tables.DefaultPaletteUvSizeCdf, 7);
        public static readonly int[] PaletteYColor =
        {
            0, 0,
            Add(() => Av1Tables.DefaultPaletteSize2YColorCdf, 2), Add(() => Av1Tables.DefaultPaletteSize3YColorCdf, 3),
            Add(() => Av1Tables.DefaultPaletteSize4YColorCdf, 4), Add(() => Av1Tables.DefaultPaletteSize5YColorCdf, 5),
            Add(() => Av1Tables.DefaultPaletteSize6YColorCdf, 6), Add(() => Av1Tables.DefaultPaletteSize7YColorCdf, 7),
            Add(() => Av1Tables.DefaultPaletteSize8YColorCdf, 8),
        };
        public static readonly int[] PaletteUVColor =
        {
            0, 0,
            Add(() => Av1Tables.DefaultPaletteSize2UvColorCdf, 2), Add(() => Av1Tables.DefaultPaletteSize3UvColorCdf, 3),
            Add(() => Av1Tables.DefaultPaletteSize4UvColorCdf, 4), Add(() => Av1Tables.DefaultPaletteSize5UvColorCdf, 5),
            Add(() => Av1Tables.DefaultPaletteSize6UvColorCdf, 6), Add(() => Av1Tables.DefaultPaletteSize7UvColorCdf, 7),
            Add(() => Av1Tables.DefaultPaletteSize8UvColorCdf, 8),
        };
        public static readonly int DeltaQ = Add(() => Av1Tables.DefaultDeltaQCdf, 4);
        public static readonly int DeltaLF = Add(() => Av1Tables.DefaultDeltaLfCdf, 4);
        public static readonly int DeltaLFMulti = Add(() => Av1Tables.DefaultDeltaLfCdf, 4, 4);
        public static readonly int IntraTxTypeSet1 = Add(() => Av1Tables.DefaultIntraTxTypeSet1Cdf, 7);   // [txSzSqr][intraDir]
        public static readonly int IntraTxTypeSet2 = Add(() => Av1Tables.DefaultIntraTxTypeSet2Cdf, 5);
        public static readonly int InterTxTypeSet1 = Add(() => Av1Tables.DefaultInterTxTypeSet1Cdf, 16);  // [txSzSqr]
        public static readonly int InterTxTypeSet2 = Add(() => Av1Tables.DefaultInterTxTypeSet2Cdf, 12);
        public static readonly int InterTxTypeSet3 = Add(() => Av1Tables.DefaultInterTxTypeSet3Cdf, 2);   // [txSzSqr]
        public static readonly int UseObmc = Add(() => Av1Tables.DefaultUseObmcCdf, 2);
        public static readonly int InterIntra = Add(() => Av1Tables.DefaultInterIntraCdf, 2);
        public static readonly int CompRefType = Add(() => Av1Tables.DefaultCompRefTypeCdf, 2);
        public static readonly int CflSign = Add(() => Av1Tables.DefaultCflSignCdf, 8);
        public static readonly int UniCompRef = Add(() => Av1Tables.DefaultUniCompRefCdf, 2);             // [ctx][3]
        public static readonly int WedgeInterIntra = Add(() => Av1Tables.DefaultWedgeInterIntraCdf, 2);
        public static readonly int CompGroupIdx = Add(() => Av1Tables.DefaultCompGroupIdxCdf, 2);
        public static readonly int CompoundIdx = Add(() => Av1Tables.DefaultCompoundIdxCdf, 2);
        public static readonly int CompoundType = Add(() => Av1Tables.DefaultCompoundTypeCdf, 2);
        public static readonly int InterIntraMode = Add(() => Av1Tables.DefaultInterIntraModeCdf, 4);
        public static readonly int WedgeIndex = Add(() => Av1Tables.DefaultWedgeIndexCdf, 16);
        public static readonly int CflAlpha = Add(() => Av1Tables.DefaultCflAlphaCdf, 16);
        public static readonly int UseWiener = Add(() => Av1Tables.DefaultUseWienerCdf, 2);
        public static readonly int UseSgrproj = Add(() => Av1Tables.DefaultUseSgrprojCdf, 2);
        public static readonly int RestorationType = Add(() => Av1Tables.DefaultRestorationTypeCdf, 3);

        public static readonly int CoeffStart = total;
        // coefficient CDFs: the defaults have a leading q context dimension (COEFF_CDF_Q_CTXS = 4)
        public static readonly int TxbSkip = Add(() => Av1Tables.DefaultTxbSkipCdf, 2, 1, 5 * 13 * 3);              // [txSzCtx][ctx]
        public static readonly int EobPt16 = Add(() => Av1Tables.DefaultEobPt16Cdf, 5, 1, 2 * 2 * 6);               // [ptype][ctx]
        public static readonly int EobPt32 = Add(() => Av1Tables.DefaultEobPt32Cdf, 6, 1, 2 * 2 * 7);
        public static readonly int EobPt64 = Add(() => Av1Tables.DefaultEobPt64Cdf, 7, 1, 2 * 2 * 8);
        public static readonly int EobPt128 = Add(() => Av1Tables.DefaultEobPt128Cdf, 8, 1, 2 * 2 * 9);
        public static readonly int EobPt256 = Add(() => Av1Tables.DefaultEobPt256Cdf, 9, 1, 2 * 2 * 10);
        public static readonly int EobPt512 = Add(() => Av1Tables.DefaultEobPt512Cdf, 10, 1, 2 * 11);              // [ptype]
        public static readonly int EobPt1024 = Add(() => Av1Tables.DefaultEobPt1024Cdf, 11, 1, 2 * 12);
        public static readonly int EobExtra = Add(() => Av1Tables.DefaultEobExtraCdf, 2, 1, 5 * 2 * 9 * 3);        // [txSzCtx][ptype][eobPt-3]
        public static readonly int DcSign = Add(() => Av1Tables.DefaultDcSignCdf, 2, 1, 2 * 3 * 3);                // [ptype][ctx]
        public static readonly int CoeffBaseEob = Add(() => Av1Tables.DefaultCoeffBaseEobCdf, 3, 1, 5 * 2 * 4 * 4); // [txSzCtx][ptype][ctx]
        public static readonly int CoeffBase = Add(() => Av1Tables.DefaultCoeffBaseCdf, 4, 1, 5 * 2 * 42 * 5);    // [txSzCtx][ptype][ctx]
        public static readonly int CoeffBr = Add(() => Av1Tables.DefaultCoeffBrCdf, 4, 1, 5 * 2 * 21 * 5);       // [txSzCtx][ptype][ctx]

        public static readonly int Total = total;

        /// <summary>Positions of the adaptation counters (zeroed by load_cdfs).</summary>
        static readonly int[] counters = Counters();

        static int[] Counters()
        {
            var r = new List<int>();
            foreach (var e in entries)
                for (int p = e.Offset + e.N; p < e.Offset + e.Length; p += e.N + 1) r.Add(p);
            return r.ToArray();
        }

        public static ushort[] Create() => new ushort[Total];

        /// <summary>init_non_coeff_cdfs.</summary>
        public static void InitNonCoeff(ushort[] cdf)
        {
            foreach (var e in entries)
                if (e.SrcStride == 0) e.Src().CopyTo(cdf.AsSpan(e.Offset, e.Length));
        }

        /// <summary>init_coeff_cdfs.</summary>
        public static void InitCoeff(ushort[] cdf, int baseQIdx)
        {
            int idx = baseQIdx <= 20 ? 0 : baseQIdx <= 60 ? 1 : baseQIdx <= 120 ? 2 : 3;
            foreach (var e in entries)
                if (e.SrcStride != 0) e.Src().Slice(idx * e.SrcStride, e.Length).CopyTo(cdf.AsSpan(e.Offset, e.Length));
        }

        /// <summary>The counter reset of load_cdfs.</summary>
        public static void ClearCounters(ushort[] cdf)
        {
            foreach (int p in counters) cdf[p] = 0;
        }

        /// <summary>The default intra frame y mode CDFs (TileIntraFrameYModeCdf is reset from them for each tile).</summary>
        public static ushort[] IntraFrameYModeDefaults() => Av1Tables.DefaultIntraFrameYModeCdf.ToArray();
    }
}
