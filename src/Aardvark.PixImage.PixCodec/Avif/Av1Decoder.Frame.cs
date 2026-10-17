using System;
using System.Threading.Tasks;

namespace Aardvark.ImageCodecs.Av1
{
    internal sealed partial class Av1Decoder
    {
        void DecodeTiles(byte[] data)
        {
            var h = fh!;
            var f = fs!;
            int n = tileData.Count;
            if (n != h.TileCols * h.TileRows) throw new ImageFormatException("AV1: missing tiles");
            f.Tiles = new Av1TileDecoder[n];
            for (int i = 0; i < n; i++) f.Tiles[i] = new Av1TileDecoder(f, i);
            void Run(int i)
            {
                var (off, size) = tileData[i];
                f.Tiles[i].DecodeTile(data, off, size, i);
            }
            bool parallel = Options.Multithreaded && n > 1 && Environment.ProcessorCount > 1;
            if (parallel)
            {
                try { Parallel.For(0, n, Run); }
                catch (AggregateException e) when (e.InnerExceptions.Count > 0) { throw e.InnerExceptions[0]; }
            }
            else
                for (int i = 0; i < n; i++) Run(i);
            if (!h.DisableFrameEndUpdateCdf)
                f.SavedCdfs = f.Tiles[h.ContextUpdateTileId].Cdf;
        }

        /// <summary>decode_frame_wrapup (spec 7.4) for a decoded frame.</summary>
        void DecodeFrameWrapup()
        {
            var h = fh!;
            var f = fs!;
            var s = Seq!;
            if (h.LoopFilterLevel[0] != 0 || h.LoopFilterLevel[1] != 0)
                Av1LoopFilter.Apply(f, Options.Multithreaded);
            bool anyCdef = s.EnableCdef && !h.CodedLossless && !h.AllowIntrabc;
            var cdef = anyCdef ? Av1Cdef.Apply(f, Options.Multithreaded) : f.Cur;
            var upCdef = h.UseSuperres ? Av1SuperRes.Upscale(f, cdef) : cdef;
            Av1Frame lr;
            if (h.UsesLr)
            {
                var upCur = h.UseSuperres ? Av1SuperRes.Upscale(f, f.Cur) : f.Cur;
                lr = Av1LoopRestoration.Apply(f, upCur, upCdef, Options.Multithreaded);
            }
            else lr = upCdef;
            lr.Width = h.UpscaledWidth;
            lr.Height = h.FrameHeight;
            var slot = MakeRefSlot(h, f, lr);
            for (int i = 0; i < 8; i++)
                if (((h.RefreshFrameFlags >> i) & 1) != 0) { Ref[i] = slot; refValid[i] = true; }
            if (h.ShowFrame) EmitOutput(lr, h.FilmGrain, h.FrameType, false);
        }

        Av1RefSlot MakeRefSlot(Av1FrameHeader h, Av1FrameState f, Av1Frame lr)
        {
            var s = Seq!;
            var slot = new Av1RefSlot
            {
                FrameId = h.CurrentFrameId, UpscaledWidth = h.UpscaledWidth, FrameWidth = h.FrameWidth, FrameHeight = h.FrameHeight,
                RenderWidth = h.RenderWidth, RenderHeight = h.RenderHeight, MiCols = h.MiCols, MiRows = h.MiRows, FrameType = h.FrameType,
                SubX = s.SubsamplingX, SubY = s.SubsamplingY, BitDepth = s.BitDepth, OrderHint = h.OrderHint, Showable = h.ShowableFrame,
                Frame = lr, Cdfs = f.Cdfs, Grain = h.FilmGrain.Clone(),
            };
            for (int j = 0; j < 7; j++) slot.SavedOrderHints[j + Av1C.LastFrame] = h.OrderHints[j + Av1C.LastFrame];
            Array.Copy(h.GmParams, slot.GmParams, 48);
            Array.Copy(h.LoopFilterRefDeltas, slot.LfRefDeltas, 8);
            Array.Copy(h.LoopFilterModeDeltas, slot.LfModeDeltas, 2);
            Array.Copy(h.FeatureEnabled, slot.FeatureEnabled, 64);
            Array.Copy(h.FeatureData, slot.FeatureData, 64);
            // segmentation map: kept from the previous frame when it was not updated (7.4 step 7)
            if (h.SegmentationEnabled && !h.SegmentationUpdateMap)
                slot.SegmentIds = f.PrevSegmentIds ?? new byte[f.MiRows * f.MiCols];
            else slot.SegmentIds = f.SegmentIds;
            MotionFieldMotionVectorStorage(h, f, slot);
            return slot;
        }

        /// <summary>The motion field motion vector storage process (7.19).</summary>
        void MotionFieldMotionVectorStorage(Av1FrameHeader h, Av1FrameState f, Av1RefSlot slot)
        {
            int n = f.MiRows * f.MiCols;
            if (h.FrameIsIntra)
            {
                // all blocks are intra (or intra block copy): no stored motion
                slot.MfRefFrames = null;
                slot.MfMvs = null;
                return;
            }
            var refs = new sbyte[n];
            var mvs = new int[n];
            for (int row = 0; row < f.MiRows; row++)
                for (int col = 0; col < f.MiCols; col++)
                {
                    int i = row * f.MiStride + col;
                    sbyte mfRef = Av1C.None;
                    int mv = 0;
                    ref var b = ref f.Block(f.BlockAt[i]);
                    for (int list = 0; list < 2; list++)
                    {
                        int r = b.RefFrame(list);
                        if (r > Av1C.IntraFrame)
                        {
                            int refIdx = h.RefFrameIdx[r - Av1C.LastFrame];
                            int dist = RelativeDist(Ref[refIdx]!.OrderHint, h.OrderHint);
                            if (dist < 0)
                            {
                                int mvRow = list == 0 ? b.Mv0Row : b.Mv1Row;
                                int mvCol = list == 0 ? b.Mv0Col : b.Mv1Col;
                                if (Math.Abs(mvRow) <= Av1C.RefMvsLimit && Math.Abs(mvCol) <= Av1C.RefMvsLimit)
                                {
                                    mfRef = (sbyte)r;
                                    mv = (mvRow & 0xFFFF) | (mvCol << 16);
                                }
                            }
                        }
                    }
                    refs[i] = mfRef;
                    mvs[i] = mv;
                }
            slot.MfRefFrames = refs;
            slot.MfMvs = mvs;
        }

        void ShowExistingFrame()
        {
            var h = fh!;
            var slot = Ref[h.FrameToShowMapIdx]!;
            var grain = Seq!.FilmGrainParamsPresent ? slot.Grain : new Av1FilmGrainParams();
            EmitOutput(slot.Frame, grain, slot.FrameType, true, slot);
            if (h.FrameType == Av1C.KeyFrame)
            {
                // reference frame loading process (7.21) then the update of every slot (refresh_frame_flags = allFrames)
                for (int i = 0; i < 8; i++) { Ref[i] = slot; refValid[i] = true; }
            }
        }

        void EmitOutput(Av1Frame frame, Av1FilmGrainParams grain, int frameType, bool showExisting, Av1RefSlot? slot = null)
        {
            var s = Seq!;
            var outFrame = frame;
            if (Options.ApplyFilmGrain && s.FilmGrainParamsPresent && grain.ApplyGrain)
                outFrame = Av1FilmGrain.Apply(frame, grain, s);
            Output.Add(new Av1Picture
            {
                Frame = outFrame,
                Width = frame.Width, Height = frame.Height, BitDepth = frame.BitDepth, SubX = frame.SubX, SubY = frame.SubY,
                Monochrome = s.MonoChrome, ColorPrimaries = s.ColorPrimaries, TransferCharacteristics = s.TransferCharacteristics,
                MatrixCoefficients = s.MatrixCoefficients, FullRange = s.ColorRange, ChromaSamplePosition = s.ChromaSamplePosition,
                SpatialId = spatialId, TemporalId = temporalId, ShowExisting = showExisting, FrameType = frameType,
            });
        }

        // ------------------------------------------------------------------ motion field estimation (7.9)

        void MotionFieldEstimation()
        {
            var h = fh!;
            var f = fs!;
            int w8 = h.MiCols >> 1, h8 = h.MiRows >> 1;
            var mf = new int[8][];
            for (int rf = Av1C.LastFrame; rf <= Av1C.AltrefFrame; rf++)
            {
                mf[rf] = new int[Math.Max(1, w8 * h8)];
                Array.Fill(mf[rf], unchecked((int)0x80008000));   // row and col -32768 (only the row is tested)
            }
            f.MotionFieldMvs = mf;
            int lastIdx = h.RefFrameIdx[0];
            int curGoldOrderHint = h.OrderHints[Av1C.GoldenFrame];
            int lastAltOrderHint = Ref[lastIdx]!.SavedOrderHints[Av1C.AltrefFrame];
            bool useLast = lastAltOrderHint != curGoldOrderHint;
            if (useLast) Project(Av1C.LastFrame, -1);
            int refStamp = Av1C.MfmvStackSize - 2;
            if (RelativeDist(h.OrderHints[Av1C.BwdrefFrame], h.OrderHint) > 0)
                if (Project(Av1C.BwdrefFrame, 1)) refStamp--;
            if (RelativeDist(h.OrderHints[Av1C.Altref2Frame], h.OrderHint) > 0)
                if (Project(Av1C.Altref2Frame, 1)) refStamp--;
            if (RelativeDist(h.OrderHints[Av1C.AltrefFrame], h.OrderHint) > 0 && refStamp >= 0)
                if (Project(Av1C.AltrefFrame, 1)) refStamp--;
            if (refStamp >= 0) Project(Av1C.Last2Frame, -1);
        }

        bool Project(int src, int dstSign)
        {
            var h = fh!;
            var mf = fs!.MotionFieldMvs!;
            int srcIdx = h.RefFrameIdx[src - Av1C.LastFrame];
            int w8 = h.MiCols >> 1, h8 = h.MiRows >> 1;
            var rs = Ref[srcIdx]!;
            if (rs.MiRows != h.MiRows || rs.MiCols != h.MiCols || rs.FrameType == Av1C.IntraOnlyFrame || rs.FrameType == Av1C.KeyFrame)
                return false;
            if (rs.MfRefFrames == null) return true;
            var div = Av1Tables.DivMult;
            for (int y8 = 0; y8 < h8; y8++)
                for (int x8 = 0; x8 < w8; x8++)
                {
                    int row = 2 * y8 + 1, col = 2 * x8 + 1;
                    int i = row * h.MiCols + col;
                    int srcRef = rs.MfRefFrames[i];
                    if (srcRef <= Av1C.IntraFrame) continue;
                    int refToCur = RelativeDist(h.OrderHints[src], h.OrderHint);
                    int refOffset = RelativeDist(h.OrderHints[src], rs.SavedOrderHints[srcRef]);
                    bool posValid = Math.Abs(refToCur) <= Av1C.MaxFrameDistance && Math.Abs(refOffset) <= Av1C.MaxFrameDistance && refOffset > 0;
                    if (!posValid) continue;
                    int packed = rs.MfMvs![i];
                    int mvRow = (short)packed, mvCol = packed >> 16;
                    MvProjection(mvRow, mvCol, refToCur * dstSign, refOffset, div, out int pr, out int pc);
                    int posY8 = ProjectPos(y8, pr, dstSign, h.MiRows >> 1, Av1C.MaxOffsetHeight, ref posValid);
                    int posX8 = ProjectPos(x8, pc, dstSign, h.MiCols >> 1, Av1C.MaxOffsetWidth, ref posValid);
                    if (!posValid) continue;
                    for (int dst = Av1C.LastFrame; dst <= Av1C.AltrefFrame; dst++)
                    {
                        int refToDst = RelativeDist(h.OrderHint, h.OrderHints[dst]);
                        MvProjection(mvRow, mvCol, refToDst, refOffset, div, out int qr, out int qc);
                        mf[dst][posY8 * w8 + posX8] = (qr & 0xFFFF) | (qc << 16);
                    }
                }
            return true;
        }

        static void MvProjection(int mvRow, int mvCol, int numerator, int denominator, ReadOnlySpan<ushort> div, out int pr, out int pc)
        {
            int clippedDenominator = Math.Min(Av1C.MaxFrameDistance, denominator);
            int clippedNumerator = Math.Clamp(numerator, -Av1C.MaxFrameDistance, Av1C.MaxFrameDistance);
            int d = div[clippedDenominator];
            pr = Proj(mvRow, clippedNumerator, d);
            pc = Proj(mvCol, clippedNumerator, d);
        }

        static int Proj(int v, int num, int d)
        {
            long scaled = (long)v * num * d;
            long r = scaled >= 0 ? (scaled + (1 << 13)) >> 14 : -((-scaled + (1 << 13)) >> 14);
            return (int)Math.Clamp(r, -(1 << 14) + 1, (1 << 14) - 1);
        }

        static int ProjectPos(int v8, int delta, int dstSign, int max8, int maxOff8, ref bool posValid)
        {
            int base8 = (v8 >> 3) << 3;
            int offset8 = delta >= 0 ? delta >> (3 + 1 + Av1C.MiSizeLog2) : -((-delta) >> (3 + 1 + Av1C.MiSizeLog2));
            v8 += dstSign * offset8;
            if (v8 < 0 || v8 >= max8 || v8 < base8 - maxOff8 || v8 >= base8 + 8 + maxOff8) posValid = false;
            return v8;
        }
    }
}
