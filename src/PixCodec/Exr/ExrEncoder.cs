using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace PixCodec
{
    internal sealed unsafe class ExrEncoder
    {
        const int ParallelThreshold = 512 * 1024;

        struct Source
        {
            public byte* Data;     // first element of the (pinned) array
            public int Kind;       // element type: 0 uint, 1 Half, 2 float
            public long Offset, Stride;
        }

        int _width, _height, _x0, _y0, _linesPerChunk, _zipLevel;
        ExrCompression _compression;
        ExrChan[] _chans = null!;
        Source[] _sources = null!;
        byte[][] _chunks = null!;

        public static void Encode(int width, int height, IReadOnlyList<ExrChannelData> channels, Stream output, ExrEncodeOptions opt)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (channels == null || channels.Count == 0) throw new ArgumentException("no channels given");
            if (width <= 0 || height <= 0) throw new ArgumentException("image dimensions must be positive");
            if (opt.Compression < ExrCompression.None || opt.Compression > ExrCompression.B44A)
                throw new ImageNotSupportedException($"EXR compression {opt.Compression} is not supported for encoding");
            const int lim = 1 << 30;
            long maxX = (long)opt.DataWindowX + width - 1, maxY = (long)opt.DataWindowY + height - 1;
            if (opt.DataWindowX <= -lim || opt.DataWindowY <= -lim || maxX >= lim || maxY >= lim)
                throw new ArgumentException("data window is out of range");

            // channels are stored sorted by name
            var sorted = new List<ExrChannelData>(channels);
            sorted.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            bool longNames = false;
            var e = new ExrEncoder
            {
                _width = width, _height = height, _x0 = opt.DataWindowX, _y0 = opt.DataWindowY,
                _compression = opt.Compression, _zipLevel = opt.ZipLevel,
                _linesPerChunk = ExrCodecs.LinesPerChunk(opt.Compression),
                _chans = new ExrChan[sorted.Count], _sources = new Source[sorted.Count],
            };
            long lineBytes = 0;
            for (int i = 0; i < sorted.Count; i++)
            {
                var c = sorted[i];
                CheckName(c.Name, "channel", ref longNames);
                if (i > 0 && sorted[i - 1].Name == c.Name) throw new ArgumentException($"channel '{c.Name}' is given twice");
                int kind = c.Data switch
                {
                    uint[] => 0,
                    Half[] => 1,
                    float[] => 2,
                    _ => throw new ImageNotSupportedException($"OpenEXR cannot store {c.Data.GetType().GetElementType()} samples; use float, Half or uint"),
                };
                var type = c.PixelType ?? (kind == 0 ? ExrPixelType.UInt : ExrPixelType.Half);
                if ((kind == 0) != (type == ExrPixelType.UInt))
                    throw new ImageNotSupportedException($"channel '{c.Name}': cannot store {(kind == 0 ? "uint" : "floating point")} samples as {type}");
                if (c.Data.LongLength < c.Offset + ((long)width * height - 1) * c.Stride + 1)
                    throw new ArgumentException($"channel '{c.Name}' has too few samples for {width}x{height}");
                e._chans[i] = new ExrChan { Name = c.Name, Type = (int)type, XS = 1, YS = 1, PLinear = c.PLinear, Bytes = type == ExrPixelType.Half ? 2 : 4 };
                e._sources[i] = new Source { Kind = kind, Offset = c.Offset, Stride = c.Stride };
                lineBytes += (long)width * e._chans[i].Bytes;
            }
            if (lineBytes * e._linesPerChunk > int.MaxValue) throw new ImageNotSupportedException("EXR scan lines are too large");

            int chunkCount = (height + e._linesPerChunk - 1) / e._linesPerChunk;
            e._chunks = new byte[chunkCount][];
            var handles = new GCHandle[sorted.Count];
            try
            {
                for (int i = 0; i < sorted.Count; i++)
                {
                    handles[i] = GCHandle.Alloc(sorted[i].Data, GCHandleType.Pinned);
                    e._sources[i].Data = (byte*)handles[i].AddrOfPinnedObject();
                }
                if (opt.Parallel && chunkCount > 1 && lineBytes * height >= ParallelThreshold && Environment.ProcessorCount > 1)
                {
                    try
                    {
                        Parallel.For(0, chunkCount, () => new ExrScratch(), (i, _, s) => { e.EncodeChunk(i, s); return s; }, _ => { });
                    }
                    catch (AggregateException ex) when (ex.InnerException != null)
                    {
                        ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                    }
                }
                else
                {
                    var s = new ExrScratch();
                    for (int i = 0; i < chunkCount; i++) e.EncodeChunk(i, s);
                }
            }
            finally
            {
                foreach (var h in handles) if (h.IsAllocated) h.Free();
            }

            // header
            var hdr = new MemoryStream();
            var w = new BinaryWriter(hdr);
            var custom = new List<ExrAttribute>(opt.Attributes);
            foreach (var a in custom)
            {
                if (a == null) throw new ArgumentException("null attribute");
                CheckName(a.Name, "attribute", ref longNames);
                CheckName(a.TypeName, "attribute type", ref longNames);
                if (a.Name is "channels" or "compression" or "dataWindow" or "lineOrder" or "tiles" or "type" or "chunkCount" or "version")
                    throw new ArgumentException($"attribute '{a.Name}' is written by the encoder and cannot be supplied");
            }
            bool Has(string name) => custom.Exists(a => a.Name == name);
            void Attr(ExrAttribute a)
            {
                w.Write(Encoding.UTF8.GetBytes(a.Name)); w.Write((byte)0);
                w.Write(Encoding.UTF8.GetBytes(a.TypeName)); w.Write((byte)0);
                w.Write(a.Value.Length);
                w.Write(a.Value);
            }

            var chl = new MemoryStream();
            Span<byte> rec = stackalloc byte[17];
            for (int i = 0; i < sorted.Count; i++)
            {
                chl.Write(Encoding.UTF8.GetBytes(sorted[i].Name));
                rec.Clear();
                BinaryPrimitives.WriteInt32LittleEndian(rec.Slice(1), e._chans[i].Type);
                rec[5] = (byte)(e._chans[i].PLinear ? 1 : 0);
                BinaryPrimitives.WriteInt32LittleEndian(rec.Slice(9), 1);
                BinaryPrimitives.WriteInt32LittleEndian(rec.Slice(13), 1);
                chl.Write(rec);
            }
            chl.WriteByte(0);

            var lineOrder = opt.LineOrder == ExrLineOrder.DecreasingY ? ExrLineOrder.DecreasingY : ExrLineOrder.IncreasingY;
            var dataWindow = new ExrBox2i(opt.DataWindowX, opt.DataWindowY, (int)maxX, (int)maxY);
            w.Write(0x01312f76);
            w.Write(2 | (longNames ? ExrFileStructure.FlagLongNames : 0));
            Attr(new ExrAttribute("channels", "chlist", chl.ToArray()));
            Attr(new ExrAttribute("compression", "compression", new[] { (byte)opt.Compression }));
            Attr(ExrAttribute.Box2i("dataWindow", dataWindow));
            if (!Has("displayWindow")) Attr(ExrAttribute.Box2i("displayWindow", opt.DisplayWindow ?? new ExrBox2i(0, 0, width - 1, height - 1)));
            Attr(new ExrAttribute("lineOrder", "lineOrder", new[] { (byte)lineOrder }));
            if (!Has("pixelAspectRatio")) Attr(ExrAttribute.Float("pixelAspectRatio", 1f));
            if (!Has("screenWindowCenter")) Attr(ExrAttribute.V2f("screenWindowCenter", 0f, 0f));
            if (!Has("screenWindowWidth")) Attr(ExrAttribute.Float("screenWindowWidth", 1f));
            foreach (var a in custom) Attr(a);
            w.Write((byte)0);

            // offset table (indexed by increasing y), then the chunks in line order
            long pos = hdr.Length + 8L * chunkCount;
            var offsets = new long[chunkCount];
            for (int k = 0; k < chunkCount; k++)
            {
                int i = lineOrder == ExrLineOrder.DecreasingY ? chunkCount - 1 - k : k;
                offsets[i] = pos;
                pos += 8 + e._chunks[i].Length;
            }
            foreach (long o in offsets) w.Write(o);
            w.Flush();
            hdr.Position = 0;
            hdr.CopyTo(output);
            Span<byte> ch = stackalloc byte[8];
            for (int k = 0; k < chunkCount; k++)
            {
                int i = lineOrder == ExrLineOrder.DecreasingY ? chunkCount - 1 - k : k;
                BinaryPrimitives.WriteInt32LittleEndian(ch, opt.DataWindowY + i * e._linesPerChunk);
                BinaryPrimitives.WriteInt32LittleEndian(ch.Slice(4), e._chunks[i].Length);
                output.Write(ch);
                output.Write(e._chunks[i]);
            }
        }

        static void CheckName(string name, string what, ref bool longNames)
        {
            int n = Encoding.UTF8.GetByteCount(name);
            if (n == 0 || n > 255 || name.Contains('\0')) throw new ArgumentException($"invalid {what} name '{name}' (1..255 bytes)");
            if (n > 31) longNames = true;
        }

        void EncodeChunk(int index, ExrScratch s)
        {
            int row0 = index * _linesPerChunk, row1 = Math.Min(row0 + _linesPerChunk, _height) - 1;
            var chunk = ExrChunk.Create(_chans, _x0, _x0 + _width - 1, _y0 + row0, _y0 + row1, s);
            int size = (int)chunk.Size;
            var raw = s.Bytes(2, size);
            fixed (byte* pr = raw)
            fixed (uint* h2f = ExrHalf.ToFloatBits)
            {
                byte* p = pr;
                int n = _width;
                for (int row = row0; row <= row1; row++)
                    for (int ci = 0; ci < _chans.Length; ci++)
                    {
                        ref readonly Source src = ref _sources[ci];
                        long first = src.Offset + (long)row * _width * src.Stride, st = src.Stride;
                        int type = _chans[ci].Type;
                        if (src.Kind == 2 && type == 1)
                        {
                            float* f = (float*)src.Data + first;
                            ushort* o = (ushort*)p;
                            for (int j = 0; j < n; j++) o[j] = ExrHalf.FromFloat(f[j * st]);
                        }
                        else if (src.Kind == 1 && type == 2)
                        {
                            ushort* hsrc = (ushort*)src.Data + first;
                            uint* o = (uint*)p;
                            for (int j = 0; j < n; j++) o[j] = h2f[hsrc[j * st]];
                        }
                        else if (src.Kind == 1)
                        {
                            ushort* hsrc = (ushort*)src.Data + first;
                            ushort* o = (ushort*)p;
                            if (st == 1) Buffer.MemoryCopy(hsrc, o, 2L * n, 2L * n);
                            else for (int j = 0; j < n; j++) o[j] = hsrc[j * st];
                        }
                        else
                        {
                            // float -> FLOAT and uint -> UINT are plain 32 bit copies
                            uint* u = (uint*)src.Data + first;
                            uint* o = (uint*)p;
                            if (st == 1) Buffer.MemoryCopy(u, o, 4L * n, 4L * n);
                            else for (int j = 0; j < n; j++) o[j] = u[j * st];
                        }
                        p += (long)n * _chans[ci].Bytes;
                    }
            }
            _chunks[index] = ExrCodecs.Encode(_compression, raw.AsSpan(0, size), chunk, s, _zipLevel);
        }
    }
}
