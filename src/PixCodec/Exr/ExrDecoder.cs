using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace PixCodec
{
    internal sealed unsafe class ExrDecoder
    {
        const int MaxArrayLength = 0x7FFFFFC7;
        // No EXR compression scheme can exceed this ratio (zlib tops out near 1032:1, PXR24 adds 4/3).
        const long MaxRatio = 2048;
        const int ParallelThreshold = 512 * 1024;

        byte* _data;
        int _length;
        bool _multiPart;
        int _partIndex;
        ExrHeader _part = null!;
        ExrChan[] _chans = null!;
        int[] _slot = null!;          // file channel -> output slot or -1
        int _nSel;
        int _outType;                 // ExrPixelType of the output samples
        byte* _out;
        long[] _offsets = null!;
        int _linesPerChunk, _tilesX, _levelX, _levelY;
        int _minX, _minY, _maxX, _maxY, _width;

        public static RawImage Decode(ReadOnlySpan<byte> data, ExrFileStructure file, int partIndex, int[] sel, ColorModel color, ExrDecodeOptions opt)
        {
            var part = file.Parts[partIndex];
            if (part.IsDeep) throw new ImageNotSupportedException("EXR deep parts have no flat channels; use Exr.Decode (flattened) or Exr.DecodeDeep");
            if (part.Compression > ExrCompression.Dwab)
                throw new ImageNotSupportedException($"EXR compression {(int)part.Compression} is not supported");
            var chans = part.Chans;
            if (part.IsTiled)
                foreach (var c in chans)
                    if (c.XS != 1 || c.YS != 1) throw new ImageFormatException("EXR tiled part has subsampled channels");

            int lx = opt.LevelX, ly = opt.LevelY;
            if (!part.HasLevel(lx, ly)) throw new ArgumentException($"EXR part has no level ({lx}, {ly})");
            var (w, h) = part.GetLevelSize(lx, ly);
            // a level keeps the origin of the data window
            var dw = new ExrBox2i(part.DataWindow.MinX, part.DataWindow.MinY, part.DataWindow.MinX + w - 1, part.DataWindow.MinY + h - 1);
            if ((long)w * h * sel.Length > MaxArrayLength)
                throw new ImageNotSupportedException($"EXR image {w}x{h}x{sel.Length} is too large for a single array");

            // A header can claim any size; pixel data that cannot possibly be in the file is rejected before allocating.
            long stored = 0;
            foreach (var c in chans)
                stored += (long)ExrChunk.NumSamples(c.XS, dw.MinX, dw.MaxX) * ExrChunk.NumSamples(c.YS, dw.MinY, dw.MaxY) * c.Bytes;
            if (stored > data.Length * (part.Compression == ExrCompression.None ? 1 : MaxRatio))
                throw new ImageFormatException("EXR data window is too large for the file size");

            var outType = Exr.OutputType(part, sel, opt.HalfAsFloat);
            var d = new ExrDecoder
            {
                _length = data.Length, _multiPart = file.MultiPart, _partIndex = partIndex, _part = part, _chans = chans,
                _nSel = sel.Length, _minX = dw.MinX, _minY = dw.MinY, _maxX = dw.MaxX, _maxY = dw.MaxY, _width = w,
                _outType = outType == SampleType.UInt32 ? 0 : outType == SampleType.Float16 ? 1 : 2,
                _slot = new int[chans.Length], _levelX = lx, _levelY = ly,
            };
            Array.Fill(d._slot, -1);
            for (int i = 0; i < sel.Length; i++) d._slot[sel[i]] = i;

            int chunks;
            if (part.IsTiled)
            {
                d._tilesX = (w + part.TileWidth - 1) / part.TileWidth;
                chunks = checked(d._tilesX * ((h + part.TileHeight - 1) / part.TileHeight));
            }
            else
            {
                d._linesPerChunk = ExrCodecs.LinesPerChunk(part.Compression);
                chunks = (h + d._linesPerChunk - 1) / d._linesPerChunk;
            }
            d._offsets = ExrChunkTable.Locate(data, file, partIndex, part.FirstChunkOfLevel(lx, ly), chunks, lx, ly);

            // every sample is overwritten unless a subsampled channel leaves gaps, so the (large) array need not be zeroed
            bool subsampled = false;
            foreach (int i in sel) subsampled |= chans[i].XS != 1 || chans[i].YS != 1;
            int count = w * h * sel.Length;
            Array samples = subsampled ? RawImage.CreateArray(outType, count)
                : outType == SampleType.UInt32 ? GC.AllocateUninitializedArray<uint>(count)
                : outType == SampleType.Float16 ? GC.AllocateUninitializedArray<Half>(count)
                : GC.AllocateUninitializedArray<float>(count);
            var result = new RawImage(w, h, color, samples, sel.Length);
            fixed (byte* pd = data)
            fixed (byte* po = &MemoryMarshal.GetArrayDataReference(result.Data))
            {
                d._data = pd; d._out = po;
                if (opt.Parallel && chunks > 1 && stored >= ParallelThreshold && Environment.ProcessorCount > 1)
                {
                    try
                    {
                        Parallel.For(0, chunks, () => new ExrScratch(), (i, _, s) => { d.DecodeChunk(i, s); return s; }, _ => { });
                    }
                    catch (AggregateException e) when (e.InnerException != null)
                    {
                        ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                    }
                }
                else
                {
                    var s = new ExrScratch();
                    for (int i = 0; i < chunks; i++) d.DecodeChunk(i, s);
                }
                d.FillSubsampled(h);
            }
            return result;
        }

        // ------------------------------------------------------------------ chunks

        void DecodeChunk(int index, ExrScratch s)
        {
            long pos = _offsets[index];
            if (pos <= 0) throw new ImageFormatException("EXR file is incomplete: a chunk is missing");
            int hdr = (_multiPart ? 4 : 0) + (_part.IsTiled ? 20 : 8);
            if (pos > _length - hdr) throw new ImageFormatException("EXR chunk offset is out of range");
            var h = new ReadOnlySpan<byte>(_data + pos, hdr);
            if (_multiPart)
            {
                if (BinaryPrimitives.ReadInt32LittleEndian(h) != _partIndex) throw new ImageFormatException("EXR chunk belongs to another part");
                h = h.Slice(4);
            }
            int x0 = _minX, x1 = _maxX, y0, y1;
            if (_part.IsTiled)
            {
                int tx = index % _tilesX, ty = index / _tilesX;
                if (BinaryPrimitives.ReadInt32LittleEndian(h) != tx || BinaryPrimitives.ReadInt32LittleEndian(h.Slice(4)) != ty
                    || BinaryPrimitives.ReadInt32LittleEndian(h.Slice(8)) != _levelX || BinaryPrimitives.ReadInt32LittleEndian(h.Slice(12)) != _levelY)
                    throw new ImageFormatException("EXR tile coordinates do not match the offset table");
                x0 = (int)Math.Min(_minX + (long)tx * _part.TileWidth, int.MaxValue);
                x1 = (int)Math.Min(x0 + (long)_part.TileWidth - 1, _maxX);
                y0 = (int)Math.Min(_minY + (long)ty * _part.TileHeight, int.MaxValue);
                y1 = (int)Math.Min(y0 + (long)_part.TileHeight - 1, _maxY);
            }
            else
            {
                y0 = _minY + index * _linesPerChunk;
                y1 = Math.Min(y0 + _linesPerChunk - 1, _maxY);
                if (BinaryPrimitives.ReadInt32LittleEndian(h) != y0) throw new ImageFormatException("EXR scan line chunk does not match the offset table");
            }
            int size = BinaryPrimitives.ReadInt32LittleEndian(h.Slice(h.Length - 4));
            pos += hdr;
            if (size < 0 || size > _length - pos) throw new ImageFormatException("EXR chunk is truncated");

            var chunk = ExrChunk.Create(_chans, x0, x1, y0, y1, s);
            if (chunk.Size > int.MaxValue) throw new ImageNotSupportedException("EXR chunk is too large");
            int rawSize = (int)chunk.Size;
            if (s.Row == null || s.Row.Length < _width) s.Row = new float[_width];
            if (size == rawSize)
            {
                // stored uncompressed (the writer does this whenever compression does not shrink a chunk)
                Unpack(_data + pos, chunk, s.Row);
                return;
            }
            if (size > rawSize || _part.Compression == ExrCompression.None || rawSize > size * MaxRatio)
                throw new ImageFormatException("EXR chunk has an impossible size");
            var raw = s.Bytes(2, rawSize);
            ExrCodecs.Decode(_part.Compression, new ReadOnlySpan<byte>(_data + pos, size), raw.AsSpan(0, rawSize), chunk, s);
            fixed (byte* pr = raw) Unpack(pr, chunk, s.Row);
        }

        /// <summary>Converts the selected channels of an uncompressed chunk into the interleaved output.</summary>
        void Unpack(byte* p, in ExrChunk c, float[] rowBuffer)
        {
            int nSel = _nSel;
            fixed (float* tmp = rowBuffer)
                for (int y = c.Y0; y <= c.Y1; y++)
                {
                    long rowBase = (long)(y - _minY) * _width * nSel;
                    for (int ci = 0; ci < _chans.Length; ci++)
                    {
                        ref readonly ExrChan ch = ref _chans[ci];
                        if (!ExrChunk.IsSampled(y, ch.YS)) continue;
                        int n = c.Nx[ci], k = _slot[ci];
                        if (k >= 0 && n > 0)
                        {
                            int fx = ch.XS == 1 ? c.X0 : ExrChunk.FloorDiv(c.X0 + ch.XS - 1, ch.XS) * ch.XS;
                            long idx = rowBase + (long)(fx - _minX) * nSel + k;
                            int step = nSel * ch.XS;
                            if (_outType == 2)
                            {
                                float* o = (float*)_out + idx;
                                if (ch.Type == 1)
                                {
                                    if (step == 1) ExrHalf.ToFloat((ushort*)p, o, n);
                                    else
                                    {
                                        ExrHalf.ToFloat((ushort*)p, tmp, n);
                                        for (int j = 0; j < n; j++) o[(long)j * step] = tmp[j];
                                    }
                                }
                                else if (ch.Type == 2)
                                {
                                    if (step == 1) Buffer.MemoryCopy(p, o, 4L * n, 4L * n);
                                    else for (int j = 0; j < n; j++) *(uint*)(o + (long)j * step) = Unsafe.ReadUnaligned<uint>(p + 4 * j);
                                }
                                else
                                    for (int j = 0; j < n; j++) o[(long)j * step] = Unsafe.ReadUnaligned<uint>(p + 4 * j);
                            }
                            else if (_outType == 1)
                            {
                                ushort* o = (ushort*)_out + idx;
                                if (step == 1) Buffer.MemoryCopy(p, o, 2L * n, 2L * n);
                                else for (int j = 0; j < n; j++) o[(long)j * step] = Unsafe.ReadUnaligned<ushort>(p + 2 * j);
                            }
                            else
                            {
                                uint* o = (uint*)_out + idx;
                                if (step == 1) Buffer.MemoryCopy(p, o, 4L * n, 4L * n);
                                else for (int j = 0; j < n; j++) o[(long)j * step] = Unsafe.ReadUnaligned<uint>(p + 4 * j);
                            }
                        }
                        p += (long)n * ch.Bytes;
                    }
                }
        }

        /// <summary>Expands subsampled channels to full resolution by replicating each sample over its xSampling x ySampling cell.</summary>
        void FillSubsampled(int height)
        {
            int es = _outType == 1 ? 2 : 4;
            for (int ci = 0; ci < _chans.Length; ci++)
            {
                int k = _slot[ci], xs = _chans[ci].XS, ys = _chans[ci].YS;
                if (k < 0 || (xs == 1 && ys == 1)) continue;
                long rowBytes = (long)_width * _nSel * es;
                for (int pass = 0; pass < 2; pass++)
                    for (int y = _minY; y <= _maxY; y++)
                    {
                        byte* row = _out + (y - _minY) * rowBytes + (long)k * es;
                        bool sampled = ExrChunk.IsSampled(y, ys);
                        if (pass == 0)
                        {
                            if (!sampled || xs == 1) continue;
                            for (int x = _minX; x <= _maxX; x++)
                            {
                                long sx = (long)ExrChunk.FloorDiv(x, xs) * xs;
                                if (sx < _minX) sx += xs;
                                if (sx == x || sx > _maxX) continue;
                                Buffer.MemoryCopy(row + (sx - _minX) * _nSel * es, row + (long)(x - _minX) * _nSel * es, es, es);
                            }
                        }
                        else
                        {
                            if (sampled) continue;
                            long sy = (long)ExrChunk.FloorDiv(y, ys) * ys;
                            if (sy < _minY) sy += ys;
                            if (sy > _maxY) continue;
                            byte* src = _out + (sy - _minY) * rowBytes + (long)k * es;
                            for (long x = 0; x < _width; x++)
                                Buffer.MemoryCopy(src + x * _nSel * es, row + x * _nSel * es, es, es);
                        }
                    }
            }
        }
    }

    /// <summary>Finds the chunks of one level of one part: from the offset table, or by scanning the file if the table is damaged.</summary>
    internal static class ExrChunkTable
    {
        /// <summary>Bytes of a chunk header before the pixel data (without the part number of multi-part files).</summary>
        static int HeaderSize(ExrHeader p) => (p.IsTiled ? 16 : 4) + (p.IsDeep ? 24 : 4);

        public static long[] Locate(ReadOnlySpan<byte> data, ExrFileStructure file, int partIndex, long first, int count, int lx, int ly)
        {
            // table sizes of all parts (ours starts after those of the preceding parts)
            long tablePos = file.TablesStart, tablesEnd = file.TablesStart;
            for (int i = 0; i < file.Parts.Length; i++)
            {
                var p = file.Parts[i];
                long n = p.ChunkCountAttribute;
                if (n < 0 || (i == partIndex && !file.MultiPart)) n = p.ComputeChunkCount();
                if (i == partIndex)
                {
                    tablePos = tablesEnd;
                    if (n < first + count) throw new ImageFormatException("EXR chunkCount does not match the image size");
                }
                tablesEnd += 8 * n;
                if (tablesEnd > data.Length) throw new ImageFormatException("EXR offset table is truncated");
            }

            var offsets = new long[count];
            bool valid = true;
            int minChunk = (file.MultiPart ? 4 : 0) + HeaderSize(file.Parts[partIndex]);
            for (int i = 0; i < count; i++)
            {
                ulong o = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice((int)(tablePos + 8L * (first + i))));
                if (o < (ulong)tablesEnd || o > (ulong)(data.Length - minChunk)) { valid = false; break; }
                offsets[i] = (long)o;
            }
            if (!valid) Scan(data, file, partIndex, (int)tablesEnd, offsets, lx, ly);
            return offsets;
        }

        /// <summary>
        /// Rebuilds the offsets by walking the chunks in file order (for files with a missing or incomplete offset
        /// table, as left behind by an interrupted writer).
        /// </summary>
        static void Scan(ReadOnlySpan<byte> data, ExrFileStructure file, int partIndex, int start, long[] offsets, int levelX, int levelY)
        {
            Array.Clear(offsets);
            var our = file.Parts[partIndex];
            int lpc = our.IsTiled ? 0 : ExrCodecs.LinesPerChunk(our.Compression);
            int tilesX = our.IsTiled ? (our.GetLevelSize(levelX, levelY).Width + our.TileWidth - 1) / our.TileWidth : 0;
            long pos = start;
            while (true)
            {
                long chunkStart = pos;
                var part = our;
                if (file.MultiPart)
                {
                    if (data.Length - pos < 4) break;
                    int pn = BinaryPrimitives.ReadInt32LittleEndian(data.Slice((int)pos));
                    if (pn < 0 || pn >= file.Parts.Length) break;
                    part = file.Parts[pn];
                    pos += 4;
                }
                int hdr = HeaderSize(part);
                if (data.Length - pos < hdr) break;
                var h = data.Slice((int)pos, hdr);
                long size;
                if (part.IsDeep)
                {
                    long table = BinaryPrimitives.ReadInt64LittleEndian(h.Slice(hdr - 24)), packed = BinaryPrimitives.ReadInt64LittleEndian(h.Slice(hdr - 16));
                    if (table < 0 || packed < 0 || table > data.Length || packed > data.Length) break;
                    size = table + packed;
                }
                else size = BinaryPrimitives.ReadInt32LittleEndian(h.Slice(hdr - 4));
                pos += hdr;
                if (size < 0 || size > data.Length - pos) break;
                pos += size;
                if (part != our) continue;
                long index;
                if (part.IsTiled)
                {
                    int tx = BinaryPrimitives.ReadInt32LittleEndian(h), ty = BinaryPrimitives.ReadInt32LittleEndian(h.Slice(4));
                    int lx = BinaryPrimitives.ReadInt32LittleEndian(h.Slice(8)), ly = BinaryPrimitives.ReadInt32LittleEndian(h.Slice(12));
                    if (lx != levelX || ly != levelY || tx < 0 || tx >= tilesX || ty < 0) continue;
                    index = (long)ty * tilesX + tx;
                }
                else
                {
                    long dy = (long)BinaryPrimitives.ReadInt32LittleEndian(h) - our.DataWindow.MinY;
                    if (dy < 0 || dy % lpc != 0) continue;
                    index = dy / lpc;
                }
                if (index < offsets.Length) offsets[index] = chunkStart;
            }
        }
    }
}
