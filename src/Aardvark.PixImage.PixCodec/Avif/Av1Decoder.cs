using System;
using System.Collections.Generic;

namespace Aardvark.ImageCodecs.Av1
{
    /// <summary>The state saved with a reference frame slot (spec 7.20).</summary>
    internal sealed class Av1RefSlot
    {
        public int FrameId, UpscaledWidth, FrameWidth, FrameHeight, RenderWidth, RenderHeight, MiCols, MiRows, FrameType;
        public int SubX, SubY, BitDepth, OrderHint;
        public bool Showable;
        public readonly int[] SavedOrderHints = new int[8];
        public sbyte[]? MfRefFrames;
        public int[]? MfMvs;            // row in the low 16 bits, column in the high 16 bits
        public readonly int[,] GmParams = new int[8, 6];
        public byte[]? SegmentIds;
        public ushort[] Cdfs = null!;
        public Av1FilmGrainParams Grain = new();
        public readonly int[] LfRefDeltas = new int[8];
        public readonly int[] LfModeDeltas = new int[2];
        public readonly bool[] FeatureEnabled = new bool[64];
        public readonly int[] FeatureData = new int[64];
        public Av1Frame Frame = null!;
    }

    internal sealed class Av1DecoderOptions
    {
        /// <summary>Operating point to decode (AVIF a1op); 0 by default.</summary>
        public int OperatingPoint;
        /// <summary>Output every shown frame of every layer (true) or only the highest spatial layer of each temporal unit.</summary>
        public bool OutputAllLayers;
        /// <summary>Apply film grain synthesis to output frames.</summary>
        public bool ApplyFilmGrain = true;
        public bool Multithreaded = true;
        public long MaxPixels = 1L << 28;
    }

    /// <summary>
    /// AV1 decoder (AV1 Bitstream &amp; Decoding Process Specification): OBU parsing, frame headers, reference frame
    /// management and the frame decoding processes. Feed temporal units with <see cref="Decode"/>; shown frames are
    /// appended to <see cref="Output"/>.
    /// </summary>
    internal sealed partial class Av1Decoder
    {
        public readonly Av1DecoderOptions Options;
        public Av1SequenceHeader? Seq;
        public readonly Av1RefSlot?[] Ref = new Av1RefSlot?[8];
        public readonly List<Av1Picture> Output = new();
        readonly bool[] refValid = new bool[8];

        bool seenFrameHeader;
        Av1FrameHeader? fh;
        Av1FrameState? fs;
        int temporalId, spatialId;
        int tileNum;
        readonly List<(int offset, int size)> tileData = new();
        byte[] tileBuffer = Array.Empty<byte>();

        public Av1Decoder(Av1DecoderOptions? options = null) { Options = options ?? new Av1DecoderOptions(); }

        /// <summary>Decodes the OBUs of one temporal unit (low overhead bitstream format, spec 5.2).</summary>
        public void Decode(byte[] data, int offset, int length)
        {
            int pos = offset, end = offset + length;
            int outputBefore = Output.Count;
            while (pos < end) pos = Obu(data, pos, end);
            EndTemporalUnit(outputBefore);
        }

        /// <summary>Decodes a length delimited bitstream (spec Annex B): temporal units of frame units of OBUs.</summary>
        public void DecodeAnnexB(byte[] data, int offset, int length)
        {
            int pos = offset, end = offset + length;
            while (pos < end)
            {
                long tuSize = ReadLeb(data, ref pos, end);
                int tuEnd = Checked(pos, tuSize, end);
                int outputBefore = Output.Count;
                while (pos < tuEnd)
                {
                    long fuSize = ReadLeb(data, ref pos, tuEnd);
                    int fuEnd = Checked(pos, fuSize, tuEnd);
                    while (pos < fuEnd)
                    {
                        long obuLen = ReadLeb(data, ref pos, fuEnd);
                        int obuEnd = Checked(pos, obuLen, fuEnd);
                        Obu(data, pos, obuEnd);
                        pos = obuEnd;
                    }
                }
                EndTemporalUnit(outputBefore);
            }
        }

        static int Checked(int pos, long size, int end)
        {
            if (size < 0 || pos + size > end) throw new ImageFormatException("AV1: Annex B unit exceeds its container");
            return (int)(pos + size);
        }

        static long ReadLeb(byte[] d, ref int pos, int end)
        {
            long v = 0;
            for (int i = 0; i < 8; i++)
            {
                if (pos >= end) throw new ImageFormatException("AV1: truncated leb128");
                int b = d[pos++];
                v |= (long)(b & 0x7f) << (7 * i);
                if ((b & 0x80) == 0) break;
            }
            return v;
        }

        void EndTemporalUnit(int outputBefore)
        {
            if (!Options.OutputAllLayers && Output.Count - outputBefore > 1)
            {
                // the output process shows the highest spatial layer of a temporal unit
                var last = Output[^1];
                Output.RemoveRange(outputBefore, Output.Count - outputBefore);
                Output.Add(last);
            }
        }

        /// <summary>Decodes the OBU at <paramref name="pos"/> (which ends at or before <paramref name="end"/>); returns the position after it.</summary>
        int Obu(byte[] data, int pos, int end)
        {
            {
                var r = new Av1BitReader(data, pos, end - pos);
                if (r.Bit() != 0) throw new ImageFormatException("AV1: forbidden bit set");
                int obuType = r.I(4);
                bool ext = r.Flag();
                bool hasSize = r.Flag();
                r.Bit();
                int tid = 0, sid = 0;
                if (ext)
                {
                    tid = r.I(3);
                    sid = r.I(2);
                    r.I(3);
                }
                long obuSize;
                if (hasSize) obuSize = (long)r.Leb128();
                else obuSize = end - pos - 1 - (ext ? 1 : 0);
                int payload = pos + r.BytePosition;
                if (obuSize < 0 || payload + obuSize > end) throw new ImageFormatException("AV1: OBU exceeds the data");
                int size = (int)obuSize;
                int next = payload + size;
                if (obuType != Av1C.ObuSequenceHeader && obuType != Av1C.ObuTemporalDelimiter && Seq != null && Seq.OperatingPointIdc0 != 0 && ext)
                {
                    bool inTemporal = ((Seq.OperatingPointIdc0 >> tid) & 1) != 0;
                    bool inSpatial = ((Seq.OperatingPointIdc0 >> (sid + 8)) & 1) != 0;
                    if (!inTemporal || !inSpatial) return next;   // drop_obu
                }
                temporalId = tid; spatialId = sid;
                switch (obuType)
                {
                    case Av1C.ObuSequenceHeader:
                        {
                            var s = Av1SequenceHeader.Parse(new Av1BitReader(data, payload, size), Options.OperatingPoint);
                            Seq = s;
                            break;
                        }
                    case Av1C.ObuTemporalDelimiter:
                        seenFrameHeader = false;
                        break;
                    case Av1C.ObuFrameHeader:
                    case Av1C.ObuRedundantFrameHeader:
                        FrameHeaderObu(new Av1BitReader(data, payload, size), obuType);
                        break;
                    case Av1C.ObuFrame:
                        {
                            var hr = new Av1BitReader(data, payload, size);
                            FrameHeaderObu(hr, obuType);
                            hr.ByteAlign();
                            int hb = hr.BytePosition;
                            TileGroupObu(data, payload + hb, size - hb);
                            break;
                        }
                    case Av1C.ObuTileGroup:
                        TileGroupObu(data, payload, size);
                        break;
                    default:
                        break;   // metadata, tile list, padding, reserved: ignored
                }
                return next;
            }
        }

        void FrameHeaderObu(Av1BitReader r, int obuType)
        {
            if (Seq == null) throw new ImageFormatException("AV1: frame header before the sequence header");
            if (seenFrameHeader)
            {
                // frame_header_copy: identical to the first, nothing to do
                return;
            }
            if (obuType == Av1C.ObuRedundantFrameHeader) return;
            seenFrameHeader = true;
            UncompressedHeader(r);
            if (fh!.ShowExistingFrame)
            {
                ShowExistingFrame();
                seenFrameHeader = false;
            }
            else
            {
                tileNum = 0;
                tileData.Clear();
                seenFrameHeader = true;
            }
        }

        void TileGroupObu(byte[] data, int offset, int sz)
        {
            if (fh == null || fs == null || !seenFrameHeader) throw new ImageFormatException("AV1: tile group without a frame header");
            var r = new Av1BitReader(data, offset, sz);
            int numTiles = fh.TileCols * fh.TileRows;
            bool startEnd = numTiles > 1 && r.Flag();
            int tgStart, tgEnd;
            if (numTiles == 1 || !startEnd) { tgStart = 0; tgEnd = numTiles - 1; }
            else
            {
                int bits = fh.TileColsLog2 + fh.TileRowsLog2;
                tgStart = r.I(bits);
                tgEnd = r.I(bits);
            }
            r.ByteAlign();
            int pos = offset + r.BytePosition;
            int left = sz - r.BytePosition;
            if (tgStart != tileNum || tgEnd < tgStart || tgEnd >= numTiles) throw new ImageFormatException("AV1: bad tile group");
            for (tileNum = tgStart; tileNum <= tgEnd; tileNum++)
            {
                int tileSize;
                if (tileNum == tgEnd) tileSize = left;
                else
                {
                    if (left < fh.TileSizeBytes) throw new ImageFormatException("AV1: truncated tile sizes");
                    uint t = 0;
                    for (int i = 0; i < fh.TileSizeBytes; i++) t |= (uint)data[pos + i] << (8 * i);
                    pos += fh.TileSizeBytes; left -= fh.TileSizeBytes;
                    if (t + 1L > left) throw new ImageFormatException("AV1: tile exceeds the tile group");
                    tileSize = (int)t + 1;
                }
                if (tileSize <= 0) throw new ImageFormatException("AV1: empty tile");
                tileData.Add((pos, tileSize));
                pos += tileSize; left -= tileSize;
            }
            fs.TileSource = data;
            if (tgEnd == numTiles - 1)
            {
                DecodeTiles(data);
                if (!fh.DisableFrameEndUpdateCdf) Array.Copy(fs.SavedCdfs!, fs.Cdfs, fs.Cdfs.Length);   // frame_end_update_cdf
                DecodeFrameWrapup();
                seenFrameHeader = false;
            }
        }

        public int RelativeDist(int a, int b)
        {
            if (!Seq!.EnableOrderHint) return 0;
            int diff = a - b;
            int m = 1 << (Seq.OrderHintBits - 1);
            diff = (diff & (m - 1)) - (diff & m);
            return diff;
        }

        // ------------------------------------------------------------------ uncompressed header (spec 5.9.2)

        void UncompressedHeader(Av1BitReader r)
        {
            var s = Seq!;
            var h = new Av1FrameHeader();
            var prev = fh;
            fh = h;
            int idLen = 0;
            if (s.FrameIdNumbersPresent) idLen = s.AdditionalFrameIdLengthMinus1 + s.DeltaFrameIdLengthMinus2 + 3;
            const int allFrames = 255;
            if (s.ReducedStillPictureHeader)
            {
                h.ShowExistingFrame = false;
                h.FrameType = Av1C.KeyFrame;
                h.FrameIsIntra = true;
                h.ShowFrame = true;
                h.ShowableFrame = false;
            }
            else
            {
                h.ShowExistingFrame = r.Flag();
                if (h.ShowExistingFrame)
                {
                    h.FrameToShowMapIdx = r.I(3);
                    if (s.DecoderModelInfoPresent && !s.EqualPictureInterval) r.F(s.FramePresentationTimeLengthMinus1 + 1);
                    h.RefreshFrameFlags = 0;
                    if (s.FrameIdNumbersPresent) r.F(idLen);
                    var slot = Ref[h.FrameToShowMapIdx] ?? throw new ImageFormatException("AV1: show_existing_frame of an empty slot");
                    h.FrameType = slot.FrameType;
                    if (h.FrameType == Av1C.KeyFrame) h.RefreshFrameFlags = allFrames;
                    if (s.FilmGrainParamsPresent) h.FilmGrain = slot.Grain.Clone();
                    return;
                }
                h.FrameType = r.I(2);
                h.FrameIsIntra = h.FrameType == Av1C.IntraOnlyFrame || h.FrameType == Av1C.KeyFrame;
                h.ShowFrame = r.Flag();
                if (h.ShowFrame && s.DecoderModelInfoPresent && !s.EqualPictureInterval) r.F(s.FramePresentationTimeLengthMinus1 + 1);
                h.ShowableFrame = h.ShowFrame ? h.FrameType != Av1C.KeyFrame : r.Flag();
                h.ErrorResilientMode = h.FrameType == Av1C.SwitchFrame || h.FrameType == Av1C.KeyFrame && h.ShowFrame || r.Flag();
            }
            if (h.FrameType == Av1C.KeyFrame && h.ShowFrame)
            {
                for (int i = 0; i < 8; i++) refValid[i] = false;
                for (int i = 0; i < 7; i++) h.OrderHints[Av1C.LastFrame + i] = 0;
            }
            h.DisableCdfUpdate = r.Flag();
            h.AllowScreenContentTools = s.SeqForceScreenContentTools == Av1C.SelectScreenContentTools ? r.Flag() : s.SeqForceScreenContentTools != 0;
            if (h.AllowScreenContentTools)
                h.ForceIntegerMv = s.SeqForceIntegerMv == Av1C.SelectIntegerMv ? r.Flag() : s.SeqForceIntegerMv != 0;
            else h.ForceIntegerMv = false;
            if (h.FrameIsIntra) h.ForceIntegerMv = true;
            if (s.FrameIdNumbersPresent)
            {
                h.CurrentFrameId = r.I(idLen);
                // mark_ref_frames only affects conformance checking
            }
            if (h.FrameType == Av1C.SwitchFrame) h.FrameSizeOverrideFlag = true;
            else if (s.ReducedStillPictureHeader) h.FrameSizeOverrideFlag = false;
            else h.FrameSizeOverrideFlag = r.Flag();
            h.OrderHint = r.I(s.OrderHintBits);
            h.PrimaryRefFrame = h.FrameIsIntra || h.ErrorResilientMode ? Av1C.PrimaryRefNone : r.I(3);
            if (s.DecoderModelInfoPresent)
            {
                bool present = r.Flag();
                if (present)
                    for (int op = 0; op < s.OperatingPointsCount; op++)
                        if (s.DecoderModelPresentForThisOp[op])
                        {
                            int idc = s.OperatingPointIdc[op];
                            bool inT = ((idc >> temporalId) & 1) != 0, inS = ((idc >> (spatialId + 8)) & 1) != 0;
                            if (idc == 0 || inT && inS) r.F(s.BufferRemovalTimeLengthMinus1 + 1);
                        }
            }
            h.AllowHighPrecisionMv = false;
            h.UseRefFrameMvs = false;
            h.AllowIntrabc = false;
            h.RefreshFrameFlags = h.FrameType == Av1C.SwitchFrame || h.FrameType == Av1C.KeyFrame && h.ShowFrame ? allFrames : r.I(8);
            if (!h.FrameIsIntra || h.RefreshFrameFlags != allFrames)
            {
                if (h.ErrorResilientMode && s.EnableOrderHint)
                    for (int i = 0; i < 8; i++)
                    {
                        int roh = r.I(s.OrderHintBits);
                        if (Ref[i] == null || roh != Ref[i]!.OrderHint) refValid[i] = false;
                    }
            }
            if (h.FrameIsIntra)
            {
                FrameSize(r, h);
                RenderSize(r, h);
                if (h.AllowScreenContentTools && h.UpscaledWidth == h.FrameWidth) h.AllowIntrabc = r.Flag();
            }
            else
            {
                bool shortSignaling = false;
                if (s.EnableOrderHint)
                {
                    shortSignaling = r.Flag();
                    if (shortSignaling)
                    {
                        int last = r.I(3), gold = r.I(3);
                        SetFrameRefs(h, last, gold);
                    }
                }
                for (int i = 0; i < 7; i++)
                {
                    if (!shortSignaling) h.RefFrameIdx[i] = r.I(3);
                    if (s.FrameIdNumbersPresent) r.F(s.DeltaFrameIdLengthMinus2 + 2);
                }
                for (int i = 0; i < 7; i++)
                    if (Ref[h.RefFrameIdx[i]] == null) throw new ImageFormatException("AV1: inter frame references an empty slot");
                if (h.FrameSizeOverrideFlag && !h.ErrorResilientMode) FrameSizeWithRefs(r, h);
                else { FrameSize(r, h); RenderSize(r, h); }
                h.AllowHighPrecisionMv = !h.ForceIntegerMv && r.Flag();
                h.InterpolationFilter = r.Flag() ? Av1C.Switchable : r.I(2);
                h.IsMotionModeSwitchable = r.Flag();
                h.UseRefFrameMvs = !(h.ErrorResilientMode || !s.EnableRefFrameMvs) && r.Flag();
                for (int i = 0; i < 7; i++)
                {
                    int refFrame = Av1C.LastFrame + i;
                    int hint = Ref[h.RefFrameIdx[i]]!.OrderHint;
                    h.OrderHints[refFrame] = hint;
                    h.RefFrameSignBias[refFrame] = !s.EnableOrderHint ? 0 : RelativeDist(hint, h.OrderHint) > 0 ? 1 : 0;
                }
            }
            long px = (long)h.UpscaledWidth * h.FrameHeight;
            if (px > Options.MaxPixels) throw new ImageNotSupportedException($"AV1: frame of {h.UpscaledWidth}x{h.FrameHeight} exceeds the pixel limit");
            h.DisableFrameEndUpdateCdf = s.ReducedStillPictureHeader || h.DisableCdfUpdate || r.Flag();

            fs = new Av1FrameState(this, s, h);
            if (h.PrimaryRefFrame == Av1C.PrimaryRefNone)
            {
                Av1Cdf.InitNonCoeff(fs.Cdfs);
                SetupPastIndependence(h);
            }
            else
            {
                var p = Ref[h.RefFrameIdx[h.PrimaryRefFrame]]!;
                Array.Copy(p.Cdfs, fs.Cdfs, fs.Cdfs.Length);
                Av1Cdf.ClearCounters(fs.Cdfs);
                // load_previous
                Array.Copy(p.GmParams, fs.PrevGmParams, 48);
                Array.Copy(p.LfRefDeltas, h.LoopFilterRefDeltas, 8);
                Array.Copy(p.LfModeDeltas, h.LoopFilterModeDeltas, 2);
                Array.Copy(p.FeatureEnabled, h.FeatureEnabled, 64);
                Array.Copy(p.FeatureData, h.FeatureData, 64);
            }
            if (h.UseRefFrameMvs) MotionFieldEstimation();
            TileInfo(r, h, s);
            QuantizationParams(r, h, s);
            SegmentationParams(r, h);
            h.DeltaQRes = 0; h.DeltaQPresent = false;
            if (h.BaseQIdx > 0) h.DeltaQPresent = r.Flag();
            if (h.DeltaQPresent) h.DeltaQRes = r.I(2);
            h.DeltaLfPresent = false; h.DeltaLfRes = 0; h.DeltaLfMulti = false;
            if (h.DeltaQPresent)
            {
                if (!h.AllowIntrabc) h.DeltaLfPresent = r.Flag();
                if (h.DeltaLfPresent)
                {
                    h.DeltaLfRes = r.I(2);
                    h.DeltaLfMulti = r.Flag();
                }
            }
            if (h.PrimaryRefFrame == Av1C.PrimaryRefNone) Av1Cdf.InitCoeff(fs.Cdfs, h.BaseQIdx);
            else LoadPreviousSegmentIds(h);
            h.CodedLossless = true;
            for (int seg = 0; seg < 8; seg++)
            {
                int qindex = h.QIndex(true, seg, 0);
                h.LosslessArray[seg] = qindex == 0 && h.DeltaQYDc == 0 && h.DeltaQUAc == 0 && h.DeltaQUDc == 0 && h.DeltaQVAc == 0 && h.DeltaQVDc == 0;
                if (!h.LosslessArray[seg]) h.CodedLossless = false;
                if (h.UsingQmatrix)
                {
                    if (h.LosslessArray[seg]) { h.SegQMLevel[seg] = h.SegQMLevel[8 + seg] = h.SegQMLevel[16 + seg] = 15; }
                    else { h.SegQMLevel[seg] = h.QmY; h.SegQMLevel[8 + seg] = h.QmU; h.SegQMLevel[16 + seg] = h.QmV; }
                }
            }
            h.AllLossless = h.CodedLossless && h.FrameWidth == h.UpscaledWidth;
            LoopFilterParams(r, h, s);
            CdefParams(r, h, s);
            LrParams(r, h, s);
            h.TxMode = h.CodedLossless ? Av1C.Only4x4 : r.Flag() ? Av1C.TxModeSelect : Av1C.TxModeLargest;
            h.ReferenceSelect = !h.FrameIsIntra && r.Flag();
            SkipModeParams(r, h, s);
            h.AllowWarpedMotion = !(h.FrameIsIntra || h.ErrorResilientMode || !s.EnableWarpedMotion) && r.Flag();
            h.ReducedTxSet = r.Flag();
            GlobalMotionParams(r, h);
            FilmGrainParams(r, h, s);
            fs.Allocate();
            if (Av1Stats.On) FrameStats(h, s);
        }

        static void FrameStats(Av1FrameHeader h, Av1SequenceHeader s)
        {
            Av1Stats.Count($"frame.type{h.FrameType}");
            Av1Stats.Count($"seq.bd{s.BitDepth}.ss{s.SubsamplingX}{s.SubsamplingY}{(s.MonoChrome ? ".mono" : "")}");
            if (s.Use128x128Superblock) Av1Stats.Count("seq.sb128");
            if (s.ReducedStillPictureHeader) Av1Stats.Count("seq.reducedStill");
            Av1Stats.Count($"frame.tiles{h.TileCols}x{h.TileRows}");
            if (h.UseSuperres) Av1Stats.Count($"frame.superres{h.SuperresDenom}");
            if (h.CdefBits > 0 || h.CdefYPriStrength[0] + h.CdefYSecStrength[0] + h.CdefUvPriStrength[0] + h.CdefUvSecStrength[0] > 0) Av1Stats.Count("frame.cdef");
            for (int p = 0; p < 3; p++) if (h.FrameRestorationType[p] != 0) Av1Stats.Count($"frame.lr.p{p}.t{h.FrameRestorationType[p]}");
            if (h.UsingQmatrix) Av1Stats.Count("frame.qmatrix");
            if (h.SegmentationEnabled) Av1Stats.Count("frame.segmentation");
            if (h.DeltaQPresent) Av1Stats.Count("frame.deltaQ");
            if (h.DeltaLfPresent) Av1Stats.Count(h.DeltaLfMulti ? "frame.deltaLfMulti" : "frame.deltaLf");
            if (h.FilmGrain.ApplyGrain) Av1Stats.Count("frame.filmGrain");
            if (h.AllowIntrabc) Av1Stats.Count("frame.allowIntrabc");
            if (h.AllowScreenContentTools) Av1Stats.Count("frame.screenContent");
            if (h.CodedLossless) Av1Stats.Count("frame.lossless");
            if (h.ReducedTxSet) Av1Stats.Count("frame.reducedTxSet");
            Av1Stats.Count($"frame.txMode{h.TxMode}");
            if (h.LoopFilterLevel[0] != 0 || h.LoopFilterLevel[1] != 0) Av1Stats.Count("frame.deblock");
            if (!h.FrameIsIntra)
            {
                if (h.ReferenceSelect) Av1Stats.Count("frame.referenceSelect");
                if (h.SkipModePresent) Av1Stats.Count("frame.skipMode");
                if (h.AllowWarpedMotion) Av1Stats.Count("frame.allowWarped");
                if (h.UseRefFrameMvs) Av1Stats.Count("frame.refFrameMvs");
                Av1Stats.Count($"frame.interp{h.InterpolationFilter}");
                for (int rf = 1; rf < 8; rf++) if (h.GmType[rf] != 0) Av1Stats.Count($"frame.gm{h.GmType[rf]}");
            }
        }

        void SetupPastIndependence(Av1FrameHeader h)
        {
            Array.Clear(h.FeatureData);
            Array.Clear(h.FeatureEnabled);
            // PrevSegmentIds stay zero (fresh frame state)
            for (int rf = Av1C.LastFrame; rf <= Av1C.AltrefFrame; rf++)
                for (int i = 0; i < 6; i++) fs!.PrevGmParams[rf, i] = i % 3 == 2 ? 1 << Av1C.WarpedModelPrecBits : 0;
            h.LoopFilterDeltaEnabled = true;
            h.LoopFilterRefDeltas[Av1C.IntraFrame] = 1;
            h.LoopFilterRefDeltas[Av1C.LastFrame] = 0;
            h.LoopFilterRefDeltas[Av1C.Last2Frame] = 0;
            h.LoopFilterRefDeltas[Av1C.Last3Frame] = 0;
            h.LoopFilterRefDeltas[Av1C.BwdrefFrame] = 0;
            h.LoopFilterRefDeltas[Av1C.GoldenFrame] = -1;
            h.LoopFilterRefDeltas[Av1C.AltrefFrame] = -1;
            h.LoopFilterRefDeltas[Av1C.Altref2Frame] = -1;
            h.LoopFilterModeDeltas[0] = h.LoopFilterModeDeltas[1] = 0;
        }

        void LoadPreviousSegmentIds(Av1FrameHeader h)
        {
            var p = Ref[h.RefFrameIdx[h.PrimaryRefFrame]]!;
            if (h.SegmentationEnabled && p.MiCols == h.MiCols && p.MiRows == h.MiRows && p.SegmentIds != null)
                fs!.PrevSegmentIds = p.SegmentIds;
        }

        void SetFrameRefs(Av1FrameHeader h, int lastFrameIdx, int goldFrameIdx)
        {
            var s = Seq!;
            for (int i = 0; i < 7; i++) h.RefFrameIdx[i] = -1;
            h.RefFrameIdx[0] = lastFrameIdx;
            h.RefFrameIdx[Av1C.GoldenFrame - Av1C.LastFrame] = goldFrameIdx;
            var usedFrame = new bool[8];
            usedFrame[lastFrameIdx] = true;
            usedFrame[goldFrameIdx] = true;
            int curFrameHint = 1 << (s.OrderHintBits - 1);
            var shifted = new int[8];
            for (int i = 0; i < 8; i++)
                shifted[i] = curFrameHint + RelativeDist(Ref[i]?.OrderHint ?? 0, h.OrderHint);
            int latestOrderHint = 0, earliestOrderHint = 0;
            int FindLatestBackward()
            {
                int rr = -1;
                for (int i = 0; i < 8; i++)
                {
                    int hint = shifted[i];
                    if (!usedFrame[i] && hint >= curFrameHint && (rr < 0 || hint >= latestOrderHint)) { rr = i; latestOrderHint = hint; }
                }
                return rr;
            }
            int FindEarliestBackward()
            {
                int rr = -1;
                for (int i = 0; i < 8; i++)
                {
                    int hint = shifted[i];
                    if (!usedFrame[i] && hint >= curFrameHint && (rr < 0 || hint < earliestOrderHint)) { rr = i; earliestOrderHint = hint; }
                }
                return rr;
            }
            int FindLatestForward()
            {
                int rr = -1;
                for (int i = 0; i < 8; i++)
                {
                    int hint = shifted[i];
                    if (!usedFrame[i] && hint < curFrameHint && (rr < 0 || hint >= latestOrderHint)) { rr = i; latestOrderHint = hint; }
                }
                return rr;
            }
            int rf = FindLatestBackward();
            if (rf >= 0) { h.RefFrameIdx[Av1C.AltrefFrame - Av1C.LastFrame] = rf; usedFrame[rf] = true; }
            rf = FindEarliestBackward();
            if (rf >= 0) { h.RefFrameIdx[Av1C.BwdrefFrame - Av1C.LastFrame] = rf; usedFrame[rf] = true; }
            rf = FindEarliestBackward();
            if (rf >= 0) { h.RefFrameIdx[Av1C.Altref2Frame - Av1C.LastFrame] = rf; usedFrame[rf] = true; }
            var list = Av1Tables.RefFrameList;
            for (int i = 0; i < 5; i++)
            {
                int refFrame = list[i];
                if (h.RefFrameIdx[refFrame - Av1C.LastFrame] < 0)
                {
                    rf = FindLatestForward();
                    if (rf >= 0) { h.RefFrameIdx[refFrame - Av1C.LastFrame] = rf; usedFrame[rf] = true; }
                }
            }
            rf = -1;
            for (int i = 0; i < 8; i++)
            {
                int hint = shifted[i];
                if (rf < 0 || hint < earliestOrderHint) { rf = i; earliestOrderHint = hint; }
            }
            for (int i = 0; i < 7; i++) if (h.RefFrameIdx[i] < 0) h.RefFrameIdx[i] = rf;
        }

        void FrameSize(Av1BitReader r, Av1FrameHeader h)
        {
            var s = Seq!;
            if (h.FrameSizeOverrideFlag)
            {
                h.FrameWidth = r.I(s.FrameWidthBitsMinus1 + 1) + 1;
                h.FrameHeight = r.I(s.FrameHeightBitsMinus1 + 1) + 1;
            }
            else
            {
                h.FrameWidth = s.MaxFrameWidthMinus1 + 1;
                h.FrameHeight = s.MaxFrameHeightMinus1 + 1;
            }
            SuperresParams(r, h);
            ComputeImageSize(h);
        }

        void SuperresParams(Av1BitReader r, Av1FrameHeader h)
        {
            h.UseSuperres = Seq!.EnableSuperres && r.Flag();
            h.SuperresDenom = h.UseSuperres ? r.I(Av1C.SuperresDenomBits) + Av1C.SuperresDenomMin : Av1C.SuperresNum;
            h.UpscaledWidth = h.FrameWidth;
            h.FrameWidth = (h.UpscaledWidth * Av1C.SuperresNum + h.SuperresDenom / 2) / h.SuperresDenom;
        }

        static void ComputeImageSize(Av1FrameHeader h)
        {
            h.MiCols = 2 * ((h.FrameWidth + 7) >> 3);
            h.MiRows = 2 * ((h.FrameHeight + 7) >> 3);
        }

        static void RenderSize(Av1BitReader r, Av1FrameHeader h)
        {
            if (r.Flag())
            {
                h.RenderWidth = r.I(16) + 1;
                h.RenderHeight = r.I(16) + 1;
            }
            else
            {
                h.RenderWidth = h.UpscaledWidth;
                h.RenderHeight = h.FrameHeight;
            }
        }

        void FrameSizeWithRefs(Av1BitReader r, Av1FrameHeader h)
        {
            bool found = false;
            for (int i = 0; i < 7; i++)
            {
                if (r.Flag())
                {
                    var rs = Ref[h.RefFrameIdx[i]]!;
                    h.UpscaledWidth = rs.UpscaledWidth;
                    h.FrameWidth = h.UpscaledWidth;
                    h.FrameHeight = rs.FrameHeight;
                    h.RenderWidth = rs.RenderWidth;
                    h.RenderHeight = rs.RenderHeight;
                    found = true;
                    break;
                }
            }
            if (!found) { FrameSize(r, h); RenderSize(r, h); }
            else { SuperresParams(r, h); ComputeImageSize(h); }
        }

        static int TileLog2(int blkSize, int target)
        {
            int k = 0;
            for (; (blkSize << k) < target; k++) { }
            return k;
        }

        static void TileInfo(Av1BitReader r, Av1FrameHeader h, Av1SequenceHeader s)
        {
            int sbCols = s.Use128x128Superblock ? (h.MiCols + 31) >> 5 : (h.MiCols + 15) >> 4;
            int sbRows = s.Use128x128Superblock ? (h.MiRows + 31) >> 5 : (h.MiRows + 15) >> 4;
            int sbShift = s.Use128x128Superblock ? 5 : 4;
            int sbSize = sbShift + 2;
            int maxTileWidthSb = Av1C.MaxTileWidth >> sbSize;
            int maxTileAreaSb = Av1C.MaxTileArea >> (2 * sbSize);
            int minLog2TileCols = TileLog2(maxTileWidthSb, sbCols);
            int maxLog2TileCols = TileLog2(1, Math.Min(sbCols, Av1C.MaxTileCols));
            int maxLog2TileRows = TileLog2(1, Math.Min(sbRows, Av1C.MaxTileRows));
            int minLog2Tiles = Math.Max(minLog2TileCols, TileLog2(maxTileAreaSb, sbRows * sbCols));
            bool uniform = r.Flag();
            if (uniform)
            {
                h.TileColsLog2 = minLog2TileCols;
                while (h.TileColsLog2 < maxLog2TileCols)
                {
                    if (r.Flag()) h.TileColsLog2++;
                    else break;
                }
                int tileWidthSb = (sbCols + (1 << h.TileColsLog2) - 1) >> h.TileColsLog2;
                int i = 0;
                for (int startSb = 0; startSb < sbCols; startSb += tileWidthSb) h.MiColStarts[i++] = startSb << sbShift;
                h.MiColStarts[i] = h.MiCols;
                h.TileCols = i;
                int minLog2TileRows = Math.Max(minLog2Tiles - h.TileColsLog2, 0);
                h.TileRowsLog2 = minLog2TileRows;
                while (h.TileRowsLog2 < maxLog2TileRows)
                {
                    if (r.Flag()) h.TileRowsLog2++;
                    else break;
                }
                int tileHeightSb = (sbRows + (1 << h.TileRowsLog2) - 1) >> h.TileRowsLog2;
                i = 0;
                for (int startSb = 0; startSb < sbRows; startSb += tileHeightSb) h.MiRowStarts[i++] = startSb << sbShift;
                h.MiRowStarts[i] = h.MiRows;
                h.TileRows = i;
            }
            else
            {
                int widestTileSb = 0, startSb = 0, i;
                for (i = 0; startSb < sbCols; i++)
                {
                    if (i >= Av1C.MaxTileCols) throw new ImageFormatException("AV1: too many tile columns");
                    h.MiColStarts[i] = startSb << sbShift;
                    int maxWidth = Math.Min(sbCols - startSb, maxTileWidthSb);
                    int sizeSb = r.Ns(maxWidth) + 1;
                    widestTileSb = Math.Max(sizeSb, widestTileSb);
                    startSb += sizeSb;
                }
                h.MiColStarts[i] = h.MiCols;
                h.TileCols = i;
                h.TileColsLog2 = TileLog2(1, h.TileCols);
                if (minLog2Tiles > 0) maxTileAreaSb = (sbRows * sbCols) >> (minLog2Tiles + 1);
                else maxTileAreaSb = sbRows * sbCols;
                int maxTileHeightSb = Math.Max(maxTileAreaSb / widestTileSb, 1);
                startSb = 0;
                for (i = 0; startSb < sbRows; i++)
                {
                    if (i >= Av1C.MaxTileRows) throw new ImageFormatException("AV1: too many tile rows");
                    h.MiRowStarts[i] = startSb << sbShift;
                    int maxHeight = Math.Min(sbRows - startSb, maxTileHeightSb);
                    int sizeSb = r.Ns(maxHeight) + 1;
                    startSb += sizeSb;
                }
                h.MiRowStarts[i] = h.MiRows;
                h.TileRows = i;
                h.TileRowsLog2 = TileLog2(1, h.TileRows);
            }
            if (h.TileColsLog2 > 0 || h.TileRowsLog2 > 0)
            {
                h.ContextUpdateTileId = r.I(h.TileRowsLog2 + h.TileColsLog2);
                h.TileSizeBytes = r.I(2) + 1;
            }
            else h.ContextUpdateTileId = 0;
            if (h.ContextUpdateTileId >= h.TileCols * h.TileRows) throw new ImageFormatException("AV1: bad context_update_tile_id");
        }

        static int ReadDeltaQ(Av1BitReader r) => r.Flag() ? r.Su(7) : 0;

        static void QuantizationParams(Av1BitReader r, Av1FrameHeader h, Av1SequenceHeader s)
        {
            h.BaseQIdx = r.I(8);
            h.DeltaQYDc = ReadDeltaQ(r);
            if (s.NumPlanes > 1)
            {
                bool diffUvDelta = s.SeparateUvDeltaQ && r.Flag();
                h.DeltaQUDc = ReadDeltaQ(r);
                h.DeltaQUAc = ReadDeltaQ(r);
                if (diffUvDelta)
                {
                    h.DeltaQVDc = ReadDeltaQ(r);
                    h.DeltaQVAc = ReadDeltaQ(r);
                }
                else
                {
                    h.DeltaQVDc = h.DeltaQUDc;
                    h.DeltaQVAc = h.DeltaQUAc;
                }
            }
            h.UsingQmatrix = r.Flag();
            if (h.UsingQmatrix)
            {
                h.QmY = r.I(4);
                h.QmU = r.I(4);
                h.QmV = !s.SeparateUvDeltaQ ? h.QmU : r.I(4);
            }
        }

        static void SegmentationParams(Av1BitReader r, Av1FrameHeader h)
        {
            h.SegmentationEnabled = r.Flag();
            if (h.SegmentationEnabled)
            {
                if (h.PrimaryRefFrame == Av1C.PrimaryRefNone)
                {
                    h.SegmentationUpdateMap = true;
                    h.SegmentationTemporalUpdate = false;
                    h.SegmentationUpdateData = true;
                }
                else
                {
                    h.SegmentationUpdateMap = r.Flag();
                    if (h.SegmentationUpdateMap) h.SegmentationTemporalUpdate = r.Flag();
                    h.SegmentationUpdateData = r.Flag();
                }
                if (h.SegmentationUpdateData)
                {
                    var bits = Av1Tables.SegmentationFeatureBits;
                    var signed = Av1Tables.SegmentationFeatureSigned;
                    var max = Av1Tables.SegmentationFeatureMax;
                    for (int i = 0; i < 8; i++)
                        for (int j = 0; j < 8; j++)
                        {
                            bool enabled = r.Flag();
                            h.FeatureEnabled[i * 8 + j] = enabled;
                            int clipped = 0;
                            if (enabled)
                            {
                                int b = bits[j], limit = max[j];
                                if (signed[j] == 1) clipped = Math.Clamp(r.Su(1 + b), -limit, limit);
                                else clipped = Math.Clamp(r.I(b), 0, limit);
                            }
                            h.FeatureData[i * 8 + j] = clipped;
                        }
                }
            }
            else
            {
                Array.Clear(h.FeatureEnabled);
                Array.Clear(h.FeatureData);
            }
            h.SegIdPreSkip = false;
            h.LastActiveSegId = 0;
            for (int i = 0; i < 8; i++)
                for (int j = 0; j < 8; j++)
                    if (h.FeatureEnabled[i * 8 + j])
                    {
                        h.LastActiveSegId = i;
                        if (j >= Av1C.SegLvlRefFrame) h.SegIdPreSkip = true;
                    }
        }

        static void LoopFilterParams(Av1BitReader r, Av1FrameHeader h, Av1SequenceHeader s)
        {
            if (h.CodedLossless || h.AllowIntrabc)
            {
                h.LoopFilterLevel[0] = h.LoopFilterLevel[1] = 0;
                h.LoopFilterRefDeltas[Av1C.IntraFrame] = 1;
                h.LoopFilterRefDeltas[Av1C.LastFrame] = 0;
                h.LoopFilterRefDeltas[Av1C.Last2Frame] = 0;
                h.LoopFilterRefDeltas[Av1C.Last3Frame] = 0;
                h.LoopFilterRefDeltas[Av1C.BwdrefFrame] = 0;
                h.LoopFilterRefDeltas[Av1C.GoldenFrame] = -1;
                h.LoopFilterRefDeltas[Av1C.AltrefFrame] = -1;
                h.LoopFilterRefDeltas[Av1C.Altref2Frame] = -1;
                h.LoopFilterModeDeltas[0] = h.LoopFilterModeDeltas[1] = 0;
                return;
            }
            h.LoopFilterLevel[0] = r.I(6);
            h.LoopFilterLevel[1] = r.I(6);
            if (s.NumPlanes > 1 && (h.LoopFilterLevel[0] != 0 || h.LoopFilterLevel[1] != 0))
            {
                h.LoopFilterLevel[2] = r.I(6);
                h.LoopFilterLevel[3] = r.I(6);
            }
            h.LoopFilterSharpness = r.I(3);
            h.LoopFilterDeltaEnabled = r.Flag();
            if (h.LoopFilterDeltaEnabled)
            {
                if (r.Flag())
                {
                    for (int i = 0; i < 8; i++) if (r.Flag()) h.LoopFilterRefDeltas[i] = r.Su(7);
                    for (int i = 0; i < 2; i++) if (r.Flag()) h.LoopFilterModeDeltas[i] = r.Su(7);
                }
            }
        }

        static void CdefParams(Av1BitReader r, Av1FrameHeader h, Av1SequenceHeader s)
        {
            if (h.CodedLossless || h.AllowIntrabc || !s.EnableCdef)
            {
                h.CdefBits = 0;
                h.CdefYPriStrength[0] = h.CdefYSecStrength[0] = h.CdefUvPriStrength[0] = h.CdefUvSecStrength[0] = 0;
                h.CdefDamping = 3;
                return;
            }
            h.CdefDamping = r.I(2) + 3;
            h.CdefBits = r.I(2);
            for (int i = 0; i < (1 << h.CdefBits); i++)
            {
                h.CdefYPriStrength[i] = r.I(4);
                h.CdefYSecStrength[i] = r.I(2);
                if (h.CdefYSecStrength[i] == 3) h.CdefYSecStrength[i]++;
                if (s.NumPlanes > 1)
                {
                    h.CdefUvPriStrength[i] = r.I(4);
                    h.CdefUvSecStrength[i] = r.I(2);
                    if (h.CdefUvSecStrength[i] == 3) h.CdefUvSecStrength[i]++;
                }
            }
        }

        static void LrParams(Av1BitReader r, Av1FrameHeader h, Av1SequenceHeader s)
        {
            h.UsesLr = false;
            if (h.AllLossless || h.AllowIntrabc || !s.EnableRestoration)
            {
                h.FrameRestorationType[0] = h.FrameRestorationType[1] = h.FrameRestorationType[2] = Av1C.RestoreNone;
                return;
            }
            bool usesChromaLr = false;
            var remap = Av1Tables.RemapLrType;
            for (int i = 0; i < s.NumPlanes; i++)
            {
                h.FrameRestorationType[i] = remap[r.I(2)];
                if (h.FrameRestorationType[i] != Av1C.RestoreNone)
                {
                    h.UsesLr = true;
                    if (i > 0) usesChromaLr = true;
                }
            }
            if (h.UsesLr)
            {
                int shift;
                if (s.Use128x128Superblock) shift = r.I(1) + 1;
                else
                {
                    shift = r.I(1);
                    if (shift != 0) shift += r.I(1);
                }
                h.LoopRestorationSize[0] = Av1C.RestorationTileSizeMax >> (2 - shift);
                int uvShift = s.SubsamplingX != 0 && s.SubsamplingY != 0 && usesChromaLr ? r.I(1) : 0;
                h.LoopRestorationSize[1] = h.LoopRestorationSize[0] >> uvShift;
                h.LoopRestorationSize[2] = h.LoopRestorationSize[0] >> uvShift;
            }
        }

        void SkipModeParams(Av1BitReader r, Av1FrameHeader h, Av1SequenceHeader s)
        {
            bool allowed;
            if (h.FrameIsIntra || !h.ReferenceSelect || !s.EnableOrderHint) allowed = false;
            else
            {
                int forwardIdx = -1, backwardIdx = -1, forwardHint = 0, backwardHint = 0;
                for (int i = 0; i < 7; i++)
                {
                    int refHint = Ref[h.RefFrameIdx[i]]!.OrderHint;
                    if (RelativeDist(refHint, h.OrderHint) < 0)
                    {
                        if (forwardIdx < 0 || RelativeDist(refHint, forwardHint) > 0) { forwardIdx = i; forwardHint = refHint; }
                    }
                    else if (RelativeDist(refHint, h.OrderHint) > 0)
                    {
                        if (backwardIdx < 0 || RelativeDist(refHint, backwardHint) < 0) { backwardIdx = i; backwardHint = refHint; }
                    }
                }
                if (forwardIdx < 0) allowed = false;
                else if (backwardIdx >= 0)
                {
                    allowed = true;
                    h.SkipModeFrame[0] = Av1C.LastFrame + Math.Min(forwardIdx, backwardIdx);
                    h.SkipModeFrame[1] = Av1C.LastFrame + Math.Max(forwardIdx, backwardIdx);
                }
                else
                {
                    int secondForwardIdx = -1, secondForwardHint = 0;
                    for (int i = 0; i < 7; i++)
                    {
                        int refHint = Ref[h.RefFrameIdx[i]]!.OrderHint;
                        if (RelativeDist(refHint, forwardHint) < 0)
                            if (secondForwardIdx < 0 || RelativeDist(refHint, secondForwardHint) > 0) { secondForwardIdx = i; secondForwardHint = refHint; }
                    }
                    if (secondForwardIdx < 0) allowed = false;
                    else
                    {
                        allowed = true;
                        h.SkipModeFrame[0] = Av1C.LastFrame + Math.Min(forwardIdx, secondForwardIdx);
                        h.SkipModeFrame[1] = Av1C.LastFrame + Math.Max(forwardIdx, secondForwardIdx);
                    }
                }
            }
            h.SkipModePresent = allowed && r.Flag();
        }

        void GlobalMotionParams(Av1BitReader r, Av1FrameHeader h)
        {
            for (int rf = Av1C.LastFrame; rf <= Av1C.AltrefFrame; rf++)
            {
                h.GmType[rf] = Av1C.Identity;
                for (int i = 0; i < 6; i++) h.GmParams[rf, i] = i % 3 == 2 ? 1 << Av1C.WarpedModelPrecBits : 0;
            }
            if (h.FrameIsIntra) return;
            for (int rf = Av1C.LastFrame; rf <= Av1C.AltrefFrame; rf++)
            {
                int type;
                if (r.Flag())
                {
                    if (r.Flag()) type = Av1C.RotZoom;
                    else type = r.Flag() ? Av1C.Translation : Av1C.Affine;
                }
                else type = Av1C.Identity;
                h.GmType[rf] = type;
                if (type >= Av1C.RotZoom)
                {
                    ReadGlobalParam(r, h, type, rf, 2);
                    ReadGlobalParam(r, h, type, rf, 3);
                    if (type == Av1C.Affine)
                    {
                        ReadGlobalParam(r, h, type, rf, 4);
                        ReadGlobalParam(r, h, type, rf, 5);
                    }
                    else
                    {
                        h.GmParams[rf, 4] = -h.GmParams[rf, 3];
                        h.GmParams[rf, 5] = h.GmParams[rf, 2];
                    }
                }
                if (type >= Av1C.Translation)
                {
                    ReadGlobalParam(r, h, type, rf, 0);
                    ReadGlobalParam(r, h, type, rf, 1);
                }
            }
        }

        void ReadGlobalParam(Av1BitReader r, Av1FrameHeader h, int type, int rf, int idx)
        {
            int absBits = Av1C.GmAbsAlphaBits, precBits = Av1C.GmAlphaPrecBits;
            if (idx < 2)
            {
                if (type == Av1C.Translation)
                {
                    int hp = h.AllowHighPrecisionMv ? 0 : 1;
                    absBits = Av1C.GmAbsTransOnlyBits - hp;
                    precBits = Av1C.GmTransOnlyPrecBits - hp;
                }
                else
                {
                    absBits = Av1C.GmAbsTransBits;
                    precBits = Av1C.GmTransPrecBits;
                }
            }
            int precDiff = Av1C.WarpedModelPrecBits - precBits;
            int round = idx % 3 == 2 ? 1 << Av1C.WarpedModelPrecBits : 0;
            int sub = idx % 3 == 2 ? 1 << precBits : 0;
            int mx = 1 << absBits;
            int rr = (fs!.PrevGmParams[rf, idx] >> precDiff) - sub;
            h.GmParams[rf, idx] = (DecodeSignedSubexpWithRef(r, -mx, mx + 1, rr) << precDiff) + round;
        }

        static int DecodeSignedSubexpWithRef(Av1BitReader r, int low, int high, int rr)
        {
            int x = DecodeUnsignedSubexpWithRef(r, high - low, rr - low);
            return x + low;
        }

        static int DecodeUnsignedSubexpWithRef(Av1BitReader r, int mx, int rr)
        {
            int v = DecodeSubexp(r, mx);
            if ((rr << 1) <= mx) return InverseRecenter(rr, v);
            return mx - 1 - InverseRecenter(mx - 1 - rr, v);
        }

        static int DecodeSubexp(Av1BitReader r, int numSyms)
        {
            int i = 0, mk = 0, k = 3;
            while (true)
            {
                int b2 = i != 0 ? k + i - 1 : k;
                int a = 1 << b2;
                if (numSyms <= mk + 3 * a) return r.Ns(numSyms - mk) + mk;
                if (r.Flag()) { i++; mk += a; }
                else return r.I(b2) + mk;
            }
        }

        public static int InverseRecenter(int rr, int v)
        {
            if (v > 2 * rr) return v;
            if ((v & 1) != 0) return rr - ((v + 1) >> 1);
            return rr + (v >> 1);
        }

        void FilmGrainParams(Av1BitReader r, Av1FrameHeader h, Av1SequenceHeader s)
        {
            var g = new Av1FilmGrainParams();
            h.FilmGrain = g;
            if (!s.FilmGrainParamsPresent || (!h.ShowFrame && !h.ShowableFrame)) return;
            g.ApplyGrain = r.Flag();
            if (!g.ApplyGrain) return;
            g.GrainSeed = r.I(16);
            g.UpdateGrain = h.FrameType != Av1C.InterFrame || r.Flag();
            if (!g.UpdateGrain)
            {
                int idx = r.I(3);
                var src = Ref[idx] ?? throw new ImageFormatException("AV1: film grain from an empty slot");
                var c = src.Grain.Clone();   // load_grain_params, keeping grain_seed
                c.GrainSeed = g.GrainSeed;
                h.FilmGrain = c;
                return;
            }
            g.NumYPoints = r.I(4);
            if (g.NumYPoints > 14) throw new ImageFormatException("AV1: num_y_points > 14");
            for (int i = 0; i < g.NumYPoints; i++)
            {
                g.PointYValue[i] = r.I(8);
                g.PointYScaling[i] = r.I(8);
            }
            g.ChromaScalingFromLuma = !s.MonoChrome && r.Flag();
            if (s.MonoChrome || g.ChromaScalingFromLuma || s.SubsamplingX == 1 && s.SubsamplingY == 1 && g.NumYPoints == 0)
            {
                g.NumCbPoints = g.NumCrPoints = 0;
            }
            else
            {
                g.NumCbPoints = r.I(4);
                if (g.NumCbPoints > 10) throw new ImageFormatException("AV1: num_cb_points > 10");
                for (int i = 0; i < g.NumCbPoints; i++)
                {
                    g.PointCbValue[i] = r.I(8);
                    g.PointCbScaling[i] = r.I(8);
                }
                g.NumCrPoints = r.I(4);
                if (g.NumCrPoints > 10) throw new ImageFormatException("AV1: num_cr_points > 10");
                for (int i = 0; i < g.NumCrPoints; i++)
                {
                    g.PointCrValue[i] = r.I(8);
                    g.PointCrScaling[i] = r.I(8);
                }
            }
            g.GrainScalingMinus8 = r.I(2);
            g.ArCoeffLag = r.I(2);
            int numPosLuma = 2 * g.ArCoeffLag * (g.ArCoeffLag + 1);
            int numPosChroma;
            if (g.NumYPoints != 0)
            {
                numPosChroma = numPosLuma + 1;
                for (int i = 0; i < numPosLuma; i++) g.ArCoeffsYPlus128[i] = r.I(8);
            }
            else numPosChroma = numPosLuma;
            if (g.ChromaScalingFromLuma || g.NumCbPoints != 0)
                for (int i = 0; i < numPosChroma; i++) g.ArCoeffsCbPlus128[i] = r.I(8);
            if (g.ChromaScalingFromLuma || g.NumCrPoints != 0)
                for (int i = 0; i < numPosChroma; i++) g.ArCoeffsCrPlus128[i] = r.I(8);
            g.ArCoeffShiftMinus6 = r.I(2);
            g.GrainScaleShift = r.I(2);
            if (g.NumCbPoints != 0)
            {
                g.CbMult = r.I(8);
                g.CbLumaMult = r.I(8);
                g.CbOffset = r.I(9);
            }
            if (g.NumCrPoints != 0)
            {
                g.CrMult = r.I(8);
                g.CrLumaMult = r.I(8);
                g.CrOffset = r.I(9);
            }
            g.OverlapFlag = r.Flag();
            g.ClipToRestrictedRange = r.Flag();
        }
    }
}
