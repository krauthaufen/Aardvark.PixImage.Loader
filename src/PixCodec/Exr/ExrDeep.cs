using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PixCodec
{
    /// <summary>
    /// A decoded deep part: every pixel has <see cref="SampleCounts"/>[y * Width + x] samples, stored consecutively per
    /// channel starting at <see cref="SampleOffsets"/>[y * Width + x], in file order (not sorted by depth).
    /// </summary>
    public sealed class ExrDeepImage
    {
        /// <summary>Header of the part the image was decoded from; null for images built in memory.</summary>
        public ExrHeader? Header { get; }
        public int Width { get; }
        public int Height { get; }
        /// <summary>Samples per pixel, row major, top row first.</summary>
        public int[] SampleCounts { get; }
        /// <summary>Index of the first sample of each pixel; has Width * Height + 1 entries, the last is <see cref="TotalSamples"/>.</summary>
        public int[] SampleOffsets { get; }
        public int TotalSamples => SampleOffsets[^1];
        public IReadOnlyList<string> ChannelNames { get; }
        /// <summary>One array of TotalSamples elements per channel: float[] for HALF and FLOAT channels, uint[] for UINT channels.</summary>
        public IReadOnlyList<Array> ChannelData { get; }
        /// <summary>The type each channel has in the file (decoded images) or will be stored with (<see cref="Exr.EncodeDeep(ExrDeepImage, System.IO.Stream, ExrEncodeOptions)"/>).</summary>
        public IReadOnlyList<ExrPixelType> ChannelTypes { get; }

        internal ExrDeepImage(ExrHeader header, int width, int height, int[] counts, int[] offsets, string[] names, Array[] data, ExrPixelType[] types)
        {
            Header = header; Width = width; Height = height; SampleCounts = counts; SampleOffsets = offsets; ChannelNames = names; ChannelData = data;
            ChannelTypes = types;
        }

        /// <summary>
        /// Builds a deep image for <see cref="Exr.EncodeDeep(ExrDeepImage, System.IO.Stream, ExrEncodeOptions)"/> or <see cref="Flatten"/>.
        /// </summary>
        /// <param name="width">Image width.</param>
        /// <param name="height">Image height.</param>
        /// <param name="sampleCounts">Samples per pixel, width * height entries, row major.</param>
        /// <param name="channelNames">Channel names; the deep data conventions are A (alpha), Z and ZBack (depth), colour premultiplied.</param>
        /// <param name="channelData">Per channel a float[] (stored as HALF or FLOAT) or uint[] (UINT) with one element per sample, pixel after pixel.</param>
        /// <param name="channelTypes">Stored types; null stores uint[] as UINT, Z and ZBack as FLOAT and other float[] channels as HALF.</param>
        public ExrDeepImage(int width, int height, int[] sampleCounts, IReadOnlyList<string> channelNames, IReadOnlyList<Array> channelData, IReadOnlyList<ExrPixelType>? channelTypes = null)
        {
            if (width <= 0 || height <= 0) throw new ArgumentException("image dimensions must be positive");
            if (sampleCounts == null || sampleCounts.LongLength != (long)width * height) throw new ArgumentException("sampleCounts must have width * height entries");
            if (channelNames == null || channelData == null || channelNames.Count == 0 || channelNames.Count != channelData.Count
                || (channelTypes != null && channelTypes.Count != channelNames.Count))
                throw new ArgumentException("channel names, data and types must have the same, non zero, count");
            var offsets = new int[sampleCounts.Length + 1];
            long total = 0;
            for (int i = 0; i < sampleCounts.Length; i++)
            {
                if (sampleCounts[i] < 0) throw new ArgumentException("negative sample count");
                total += sampleCounts[i];
                if (total > 0x7FFFFFC7) throw new ArgumentException("too many samples");
                offsets[i + 1] = (int)total;
            }
            var types = new ExrPixelType[channelNames.Count];
            for (int k = 0; k < types.Length; k++)
            {
                var et = channelData[k]?.GetType().GetElementType();
                bool isUInt = et == typeof(uint);
                if (!isUInt && et != typeof(float)) throw new ArgumentException($"channel '{channelNames[k]}': deep samples must be float[] or uint[]");
                if (channelData[k].LongLength != total) throw new ArgumentException($"channel '{channelNames[k]}' has {channelData[k].LongLength} samples, expected {total}");
                types[k] = channelTypes != null ? channelTypes[k] : isUInt ? ExrPixelType.UInt : channelNames[k] is "Z" or "ZBack" ? ExrPixelType.Float : ExrPixelType.Half;
                if (isUInt != (types[k] == ExrPixelType.UInt)) throw new ArgumentException($"channel '{channelNames[k]}': cannot store {(isUInt ? "uint" : "float")} samples as {types[k]}");
            }
            Width = width; Height = height; SampleCounts = sampleCounts; SampleOffsets = offsets;
            ChannelNames = new List<string>(channelNames).ToArray(); ChannelData = new List<Array>(channelData).ToArray(); ChannelTypes = types;
        }

        int IndexOf(string name)
        {
            for (int i = 0; i < ChannelNames.Count; i++) if (ChannelNames[i] == name) return i;
            return -1;
        }

        public bool HasChannel(string name) => IndexOf(name) >= 0;

        /// <summary>Samples of a HALF or FLOAT channel.</summary>
        public float[] GetFloatChannel(string name)
        {
            int i = IndexOf(name);
            if (i < 0) throw new ArgumentException($"deep image has no channel '{name}'");
            return ChannelData[i] as float[] ?? throw new InvalidOperationException($"channel '{name}' is UINT, use GetUIntChannel");
        }

        /// <summary>Samples of a UINT channel.</summary>
        public uint[] GetUIntChannel(string name)
        {
            int i = IndexOf(name);
            if (i < 0) throw new ArgumentException($"deep image has no channel '{name}'");
            return ChannelData[i] as uint[] ?? throw new InvalidOperationException($"channel '{name}' is not UINT, use GetFloatChannel");
        }

        /// <summary>
        /// Composites the samples of every pixel front to back into a flat Float32 image with the given channels
        /// (interleaved in that order, <see cref="ColorModel.Other"/> unless they are R,G,B(,A) or Y(,A)).
        /// <para>
        /// Samples are sorted by Z, then ZBack (stable; file order if there is no Z channel or sortByDepth is false) and
        /// combined with "over": every channel c, including A, Z and ZBack if requested, accumulates
        /// c += (1 - alpha) * sample.c, where alpha is the alpha accumulated before the sample; compositing stops once
        /// alpha reaches 1. Colour channels are expected to be premultiplied. Without an A channel every sample is
        /// opaque (the nearest sample wins). UINT channels are composited as float values. Pixels without samples are 0.
        /// The reference library's CompositeDeepScanLine composites in file order without sorting, so it gives the
        /// same result (bit for bit) for files whose samples are stored front to back and free of overlapping volumes,
        /// and for any file when sortByDepth and tidy are false.
        /// </para>
        /// <para>
        /// Pixels whose samples overlap are first made "tidy" as described in "Interpreting OpenEXR Deep Pixels" when
        /// <paramref name="tidy"/> is true (default): each volume sample (ZBack > Z) is split at the depths of the
        /// samples inside it (alpha' = 1 - (1 - alpha)^(part / length), colours scaled by alpha' / alpha, or by
        /// part / length if alpha is 0), and samples that then cover the same interval - including point samples at
        /// the same depth - are merged into one, which makes the result independent of their order in the file. With
        /// tidy false overlapping samples are composited as they are. OpenImageIO's DeepData sort / split /
        /// merge_overlaps followed by "over" gives the same result (to float rounding).
        /// </para>
        /// </summary>
        public RawImage Flatten(IReadOnlyList<string> channels, bool sortByDepth = true, bool tidy = true)
        {
            if (channels == null || channels.Count == 0) throw new ArgumentException("no channels given", nameof(channels));
            int nc = channels.Count;
            var src = new Array[nc];
            int alphaSlot = -1;
            for (int k = 0; k < nc; k++)
            {
                int i = IndexOf(channels[k]);
                if (i < 0) throw new ArgumentException($"deep image has no channel '{channels[k]}'");
                src[k] = ChannelData[i];
            }
            // compositing works on the requested channels plus alpha
            float[]? alpha = HasChannel("A") ? GetFloatChannel("A") : null;
            float[]? z = HasChannel("Z") && ChannelData[IndexOf("Z")] is float[] zf ? zf : null;
            float[]? zb = HasChannel("ZBack") && ChannelData[IndexOf("ZBack")] is float[] zbf ? zbf : null;
            for (int k = 0; k < nc; k++) if (channels[k] == "A") alphaSlot = k;

            if ((long)Width * Height * nc > 0x7FFFFFC7) throw new ImageNotSupportedException("flattened EXR image is too large for a single array");
            var result = new float[Width * Height * nc];
            var order = new List<int>();
            var work = new List<DeepSample>();
            var acc = new float[nc];
            for (int p = 0; p < SampleCounts.Length; p++)
            {
                int n = SampleCounts[p], first = SampleOffsets[p];
                if (n == 0) continue;
                order.Clear();
                for (int i = 0; i < n; i++) order.Add(first + i);
                if (sortByDepth && z != null && n > 1)
                    order.Sort((a, b) =>
                    {
                        int c = z[a].CompareTo(z[b]);
                        if (c == 0 && zb != null) c = zb[a].CompareTo(zb[b]);
                        return c != 0 ? c : a.CompareTo(b);
                    });
                Array.Clear(acc);
                float a = 0;
                if (tidy && sortByDepth && z != null && n > 1 && NeedsTidy(order, z, zb ?? z))
                {
                    Tidy(order, z, zb ?? z, alpha, src, work);
                    foreach (var s in work)
                    {
                        if (a >= 1) break;
                        float t = 1 - a;
                        for (int k = 0; k < nc; k++) acc[k] += t * s.Values[k];
                        a += t * s.Alpha;
                    }
                }
                else
                    foreach (int s in order)
                    {
                        if (a >= 1) break;
                        float t = 1 - a;
                        for (int k = 0; k < nc; k++) acc[k] += t * (src[k] is float[] f ? f[s] : ((uint[])src[k])[s]);
                        a += t * (alpha != null ? alpha[s] : 1f);
                    }
                // the accumulated alpha is the A output (identical to accumulating A like the other channels)
                if (alphaSlot >= 0) acc[alphaSlot] = a;
                acc.CopyTo(result, p * nc);
            }
            return new RawImage(Width, Height, Exr.NaturalModel(channels), result, nc);
        }

        sealed class DeepSample
        {
            public float Z, ZBack, Alpha;
            public float[] Values = Array.Empty<float>();
        }

        static bool NeedsTidy(List<int> order, float[] z, float[] zb)
        {
            // sorted by Z, ZBack: a volume sample overlaps if the next sample starts before it ends, and two samples
            // coincide if they cover the same interval (or are point samples at the same depth)
            for (int i = 0; i + 1 < order.Count; i++)
            {
                int s = order[i], t = order[i + 1];
                if (zb[s] > z[s] && z[t] < zb[s]) return true;
                if (z[t] == z[s] && Math.Max(zb[t], z[t]) == Math.Max(zb[s], z[s])) return true;
            }
            return false;
        }

        static void Tidy(List<int> order, float[] z, float[] zb, float[]? alpha, Array[] src, List<DeepSample> work)
        {
            int nc = src.Length;
            work.Clear();
            // every depth at which a sample starts or ends is a potential split position
            var cuts = new List<float>();
            foreach (int s in order) { cuts.Add(z[s]); if (zb[s] > z[s]) cuts.Add(zb[s]); }
            cuts.Sort();
            foreach (int s in order)
            {
                var v = new float[nc];
                for (int k = 0; k < nc; k++) v[k] = src[k] is float[] f ? f[s] : ((uint[])src[k])[s];
                float a = alpha != null ? alpha[s] : 1f, z0 = z[s], z1 = zb[s];
                if (!(z1 > z0)) { work.Add(new DeepSample { Z = z0, ZBack = z0, Alpha = a, Values = v }); continue; }
                float length = z1 - z0, start = z0;
                foreach (float c in cuts)
                {
                    if (c <= start) continue;
                    float end = Math.Min(c, z1);
                    work.Add(Part(v, a, (end - start) / length, start, end));
                    start = end;
                    if (end >= z1) break;
                }
                if (start < z1) work.Add(Part(v, a, (z1 - start) / length, start, z1));
            }
            work.Sort((p, q) =>
            {
                int c = p.Z.CompareTo(q.Z);
                return c != 0 ? c : p.ZBack.CompareTo(q.ZBack);
            });
            // merge samples that now cover exactly the same interval, point samples at the same depth included
            for (int i = 0; i + 1 < work.Count;)
            {
                var p = work[i]; var q = work[i + 1];
                if (p.Z == q.Z && p.ZBack == q.ZBack)
                {
                    Merge(p, q);
                    work.RemoveAt(i + 1);
                }
                else i++;
            }
        }

        static DeepSample Part(float[] v, float a, float fraction, float z0, float z1)
        {
            var r = new float[v.Length];
            float na, scale;
            if (a >= 1) { na = 1; scale = 1; }
            else if (a <= 0) { na = 0; scale = fraction; }
            else { na = (float)(1 - Math.Pow(1 - (double)a, fraction)); scale = na / a; }
            for (int k = 0; k < v.Length; k++) r[k] = v[k] * scale;
            return new DeepSample { Z = z0, ZBack = z1, Alpha = na, Values = r };
        }

        static void Merge(DeepSample p, DeepSample q)
        {
            double a1 = Math.Clamp(p.Alpha, 0f, 1f), a2 = Math.Clamp(q.Alpha, 0f, 1f);
            double am = a1 + a2 - a1 * a2;
            for (int k = 0; k < p.Values.Length; k++)
            {
                double c1 = p.Values[k], c2 = q.Values[k], c;
                if (a1 >= 1 && a2 >= 1) c = (c1 + c2) / 2;
                else if (a1 >= 1) c = c1;
                else if (a2 >= 1) c = c2;
                else
                {
                    double u1 = -Math.Log(1 - a1), u2 = -Math.Log(1 - a2);
                    double v1 = a1 > 0 ? u1 / a1 : 1, v2 = a2 > 0 ? u2 / a2 : 1;
                    double w = am > 0 ? am / (u1 + u2) : 1;
                    c = (c1 * v1 + c2 * v2) * w;
                }
                p.Values[k] = (float)c;
            }
            p.Alpha = (float)am;
        }
    }

    internal static class ExrDeepDecoder
    {
        const long MaxRatio = 2048;

        public static ExrDeepImage Decode(ExrInput input, ExrFileStructure file, int partIndex, int[] sel, ExrDecodeOptions opt)
        {
            var part = file.Parts[partIndex];
            if (!part.IsDeep) throw new ArgumentException("EXR part is not deep; use Exr.Decode");
            var comp = part.Compression;
            if (comp != ExrCompression.None && comp != ExrCompression.Rle && comp != ExrCompression.Zips && comp != ExrCompression.Zstd)
                throw new ImageFormatException($"EXR compression {comp} is not valid for deep data");
            var chans = part.Chans;
            foreach (var c in chans)
                if (c.XS != 1 || c.YS != 1) throw new ImageFormatException("EXR deep part has subsampled channels");
            int lx = opt.LevelX, ly = opt.LevelY;
            if (!part.HasLevel(lx, ly)) throw new ArgumentException($"EXR part has no level ({lx}, {ly})");
            var (w, h) = part.GetLevelSize(lx, ly);
            int minX = part.DataWindow.MinX, minY = part.DataWindow.MinY;
            var dw = new ExrBox2i(minX, minY, minX + w - 1, minY + h - 1);
            var region = ExrDecoder.OutputWindow(part, opt, dw);
            int rw = (int)region.Width, rh = (int)region.Height;
            if ((long)rw * rh >= 0x7FFFFFC7) throw ExrDecoder.TooLarge(rw, rh, 1);
            bool multi = file.MultiPart, tiled = part.IsTiled;

            // the chunks that intersect the region (in region-relative order they need not be contiguous in the table)
            int ix0 = Math.Max(region.MinX, dw.MinX) - minX, ix1 = Math.Min(region.MaxX, dw.MaxX) - minX;
            int iy0 = Math.Max(region.MinY, dw.MinY) - minY, iy1 = Math.Min(region.MaxY, dw.MaxY) - minY;
            bool empty = ix0 > ix1 || iy0 > iy1;
            int tilesX = tiled ? (w + part.TileWidth - 1) / part.TileWidth : 0;
            var needed = new List<int>();
            if (!empty)
            {
                if (tiled)
                    for (int ty = iy0 / part.TileHeight; ty <= iy1 / part.TileHeight; ty++)
                        for (int tx = ix0 / part.TileWidth; tx <= ix1 / part.TileWidth; tx++) needed.Add(checked(ty * tilesX + tx));
                else
                    for (int y = iy0; y <= iy1; y++) needed.Add(y);
            }
            // the offset table alone needs 8 bytes per chunk, which bounds everything allocated from the header
            if ((long)needed.Count * 8 > input.Length || (long)(empty ? 0 : (ix1 - ix0 + 1)) * (empty ? 0 : (iy1 - iy0 + 1)) > input.Length * MaxRatio)
                throw new ImageFormatException("EXR data window is too large for the file size");
            int firstIndex = needed.Count > 0 ? needed[0] : 0;
            var offsets = needed.Count == 0 ? Array.Empty<long>()
                : ExrChunkTable.Locate(input, file, partIndex, part.FirstChunkOfLevel(lx, ly) + firstIndex, needed[^1] - firstIndex + 1, lx, ly, firstIndex);

            int hdrSize = (multi ? 4 : 0) + (tiled ? 16 : 4) + 24;
            var counts = new int[rw * rh];
            var info = new (long pos, long packedTable, long packedData, long unpackedData, int x0, int y0, int cw, int ch, bool rawTable)[needed.Count];
            var s = new ExrScratch();
            int bytesPerSample = 0;
            foreach (var c in chans) bytesPerSample += c.Bytes;
            int offX = minX - region.MinX, offY = minY - region.MinY;      // data window pixel -> region pixel

            ReadOnlySpan<byte> Table(in (long pos, long packedTable, long packedData, long unpackedData, int x0, int y0, int cw, int ch, bool rawTable) c)
            {
                int tableSize = c.cw * c.ch * 4;
                var table = input.Read(c.pos, (int)c.packedTable, s, 4);
                if (c.rawTable) return table.Slice(0, tableSize);
                var buf = s.Bytes(2, tableSize).AsSpan(0, tableSize);
                ExrCodecs.DecodeBytes(comp, table, buf, s);
                return buf;
            }

            // pass 1: the sample count tables (cumulative counts along each row of the chunk)
            long total = 0;
            for (int i = 0; i < needed.Count; i++)
            {
                int index = needed[i];
                long pos = offsets[index - firstIndex];
                if (pos <= 0) throw new ImageFormatException("EXR file is incomplete: a chunk is missing");
                if (pos > input.Length - hdrSize) throw new ImageFormatException("EXR chunk offset is out of range");
                var hd = input.Read(pos, hdrSize, s, 5);
                if (multi)
                {
                    if (BinaryPrimitives.ReadInt32LittleEndian(hd) != partIndex) throw new ImageFormatException("EXR chunk belongs to another part");
                    hd = hd.Slice(4);
                }
                int x0 = 0, y0 = index, cw = w, ch = 1;
                if (tiled)
                {
                    int tx = index % tilesX, ty = index / tilesX;
                    if (BinaryPrimitives.ReadInt32LittleEndian(hd) != tx || BinaryPrimitives.ReadInt32LittleEndian(hd.Slice(4)) != ty
                        || BinaryPrimitives.ReadInt32LittleEndian(hd.Slice(8)) != lx || BinaryPrimitives.ReadInt32LittleEndian(hd.Slice(12)) != ly)
                        throw new ImageFormatException("EXR tile coordinates do not match the offset table");
                    x0 = tx * part.TileWidth; y0 = ty * part.TileHeight;
                    cw = Math.Min(part.TileWidth, w - x0); ch = Math.Min(part.TileHeight, h - y0);
                    hd = hd.Slice(16);
                }
                else
                {
                    if (BinaryPrimitives.ReadInt32LittleEndian(hd) != minY + index) throw new ImageFormatException("EXR scan line chunk does not match the offset table");
                    hd = hd.Slice(4);
                }
                long packedTable = BinaryPrimitives.ReadInt64LittleEndian(hd), packedData = BinaryPrimitives.ReadInt64LittleEndian(hd.Slice(8));
                long unpackedData = BinaryPrimitives.ReadInt64LittleEndian(hd.Slice(16));
                pos += hdrSize;
                // The table has one entry per pixel of the chunk. It is stored raw when its packed size is that of a full
                // (not clipped) tile, which is what the reference writer does even for the narrower tiles at the edges.
                long tableSize = (long)cw * ch * 4, nominal = tiled ? (long)part.TileWidth * part.TileHeight * 4 : tableSize;
                bool rawTable = packedTable == nominal || (comp == ExrCompression.None && packedTable == tableSize);
                if (packedTable < 0 || packedData < 0 || unpackedData < 0 || packedTable > input.Length - pos || packedData > input.Length - pos - packedTable
                    || packedTable > nominal || packedData > unpackedData || tableSize > int.MaxValue || unpackedData > int.MaxValue
                    || (comp == ExrCompression.None && (!rawTable || packedData != unpackedData))
                    || tableSize > Math.Max(packedTable, 1) * MaxRatio || unpackedData > Math.Max(packedData, 1) * MaxRatio)
                    throw new ImageFormatException("EXR deep chunk has impossible sizes");
                info[i] = (pos, packedTable, packedData, unpackedData, x0, y0, cw, ch, rawTable);

                var table = Table(info[i]);
                long chunkSamples = 0;
                for (int y = 0; y < ch; y++)
                {
                    int prev = 0, ry = y0 + y + offY;
                    for (int x = 0; x < cw; x++)
                    {
                        int cum = BinaryPrimitives.ReadInt32LittleEndian(table.Slice((y * cw + x) * 4));
                        int n = cum - prev;
                        if (n < 0) throw new ImageFormatException("EXR deep sample counts are not increasing");
                        prev = cum;
                        chunkSamples += n;
                        int rx = x0 + x + offX;
                        if (rx >= 0 && rx < rw && ry >= 0 && ry < rh) { counts[ry * rw + rx] = n; total += n; }
                    }
                }
                if (chunkSamples * bytesPerSample != unpackedData) throw new ImageFormatException("EXR deep sample data size does not match the sample counts");
            }
            if (total > 0x7FFFFFC7) throw new ImageNotSupportedException("EXR deep image has too many samples for a single array; decode it in parts with ExrDecodeOptions.Region");

            var sampleOffsets = new int[rw * rh + 1];
            for (int i = 0; i < counts.Length; i++) sampleOffsets[i + 1] = sampleOffsets[i] + counts[i];

            var names = new string[sel.Length];
            var arrays = new Array[sel.Length];
            var slot = new int[chans.Length];
            Array.Fill(slot, -1);
            for (int k = 0; k < sel.Length; k++)
            {
                names[k] = part.Channels[sel[k]].Name;
                arrays[k] = chans[sel[k]].Type == 0 ? new uint[total] : new float[total];
                slot[sel[k]] = k;
            }

            // pass 2: sample data; per row of the chunk, per channel, the samples of the row's pixels
            var h2f = ExrHalf.ToFloatBits;
            var rowStart = new int[2];
            for (int i = 0; i < needed.Count; i++)
            {
                var c = info[i];
                if (c.unpackedData == 0) continue;
                // cumulative counts of the pixels of each row that lie in the region
                var table = Table(c);
                int xa = Math.Max(c.x0, -offX), xb = Math.Min(c.x0 + c.cw - 1, rw - 1 - offX);       // data window columns wanted
                if (xa > xb) continue;
                var skip = new int[c.ch]; var take = new int[c.ch]; var rowTotal = new int[c.ch];
                for (int y = 0; y < c.ch; y++)
                {
                    var row = table.Slice(y * c.cw * 4, c.cw * 4);
                    skip[y] = xa == c.x0 ? 0 : BinaryPrimitives.ReadInt32LittleEndian(row.Slice((xa - 1 - c.x0) * 4));
                    take[y] = BinaryPrimitives.ReadInt32LittleEndian(row.Slice((xb - c.x0) * 4)) - skip[y];
                    rowTotal[y] = BinaryPrimitives.ReadInt32LittleEndian(row.Slice((c.cw - 1) * 4));
                }
                ReadOnlySpan<byte> raw = input.Read(c.pos + c.packedTable, (int)c.packedData, s, 4);
                if (c.packedData != c.unpackedData)
                {
                    var buf = s.Bytes(1, (int)c.unpackedData).AsSpan(0, (int)c.unpackedData);
                    ExrCodecs.DecodeBytes(comp, raw, buf, s);
                    raw = buf;
                }
                int p = 0;
                for (int y = 0; y < c.ch; y++)
                {
                    int ry = c.y0 + y + offY;
                    bool wanted = ry >= 0 && ry < rh && take[y] > 0;
                    int first = wanted ? sampleOffsets[ry * rw + xa + offX] : 0, n = take[y];
                    for (int ci = 0; ci < chans.Length; ci++)
                    {
                        int k = slot[ci], type = chans[ci].Type, bytes = chans[ci].Bytes;
                        if (k >= 0 && wanted)
                        {
                            int q = p + skip[y] * bytes;
                            if (type == 0) MemoryMarshal.Cast<byte, uint>(raw.Slice(q, n * 4)).CopyTo(((uint[])arrays[k]).AsSpan(first));
                            else if (type == 2) MemoryMarshal.Cast<byte, float>(raw.Slice(q, n * 4)).CopyTo(((float[])arrays[k]).AsSpan(first));
                            else
                            {
                                var src = MemoryMarshal.Cast<byte, ushort>(raw.Slice(q, n * 2));
                                var dst = MemoryMarshal.Cast<float, uint>(((float[])arrays[k]).AsSpan(first, n));
                                for (int j = 0; j < n; j++) dst[j] = h2f[src[j]];
                            }
                        }
                        p += rowTotal[y] * bytes;
                    }
                }
            }
            var types = new ExrPixelType[sel.Length];
            for (int k = 0; k < sel.Length; k++) types[k] = (ExrPixelType)chans[sel[k]].Type;
            return new ExrDeepImage(part, rw, rh, counts, sampleOffsets, names, arrays, types);
        }
    }
}
