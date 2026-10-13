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
    /// <summary>Writes one or more flat parts (scan line or tiled, optionally with generated mip / rip levels).</summary>
    internal sealed unsafe class ExrEncoder
    {
        const int ParallelThreshold = 512 * 1024;

        struct Source
        {
            public Array Array;
            public byte* Data;     // first element of the pinned array
            public int Kind;       // element type: 0 uint, 1 Half, 2 float
            public long Offset, Stride;
            public int PlaneWidth; // samples per row of this channel's plane
        }

        sealed class Level
        {
            public int LevelX, LevelY, Width, Height, TilesX, TilesY;
            public Source[] Sources = Array.Empty<Source>();
            public int FirstChunk;
        }

        /// <summary>Everything needed to produce the chunks of one part.</summary>
        sealed class Part
        {
            public int Index, Width, Height, X0, Y0, LinesPerChunk, TileWidth, TileHeight;
            public bool Tiled;
            public ExrLevelMode LevelMode;
            public ExrLineOrder LineOrder;
            public ExrEncodeOptions Options = null!;
            public ExrChan[] Chans = Array.Empty<ExrChan>();
            public List<ExrChannelData> Channels = null!;
            public List<Level> Levels = new();
            public byte[][] Chunks = Array.Empty<byte[]>();
            public byte[] Header = Array.Empty<byte>();
            public string? Name;
        }

        /// <summary>Attributes that describe the storage layout; the encoder always writes its own.</summary>
        internal static bool IsStructural(string name)
            => name is "channels" or "compression" or "dataWindow" or "lineOrder" or "tiles" or "type" or "chunkCount" or "version"
                or "dwaCompressionLevel" or "maxSamplesPerPixel";

        public static void Encode(IReadOnlyList<ExrPart> parts, Stream output, bool multiPart)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (parts == null || parts.Count == 0) throw new ArgumentException("no parts given");
            bool longNames = false;
            var plans = new List<Part>();
            var pins = new List<GCHandle>();
            try
            {
                var names = new HashSet<string>();
                for (int i = 0; i < parts.Count; i++)
                {
                    var p = parts[i] ?? throw new ArgumentException("null part");
                    if (multiPart)
                    {
                        if (string.IsNullOrEmpty(p.Name)) throw new ArgumentException("every part of a multi-part file needs a name");
                        if (!names.Add(p.Name)) throw new ArgumentException($"part name '{p.Name}' is used twice");
                    }
                    plans.Add(Plan(p, i, multiPart, pins, ref longNames));
                }
                if (multiPart)
                    for (int i = 1; i < plans.Count; i++)
                        if (DisplayWindow(plans[i]) != DisplayWindow(plans[0]) || PixelAspect(plans[i]) != PixelAspect(plans[0]))
                            throw new ArgumentException("all parts of a multi-part file must have the same display window and pixel aspect ratio");

                // compress all chunks of all parts
                var jobs = new List<(Part part, int chunk)>();
                long bytes = 0;
                foreach (var plan in plans)
                {
                    for (int c = 0; c < plan.Chunks.Length; c++) jobs.Add((plan, c));
                    foreach (var pc in plan.Chans) bytes += (long)plan.Width * plan.Height * pc.Bytes / (pc.XS * pc.YS);
                }
                bool parallel = true;
                foreach (var p in parts) parallel &= p.Options.Parallel;
                if (parallel && jobs.Count > 1 && bytes >= ParallelThreshold && Environment.ProcessorCount > 1)
                {
                    try
                    {
                        Parallel.For(0, jobs.Count, () => new ExrScratch(), (i, _, s) => { EncodeChunk(jobs[i].part, jobs[i].chunk, s); return s; }, _ => { });
                    }
                    catch (AggregateException ex) when (ex.InnerException != null)
                    {
                        ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                    }
                }
                else
                {
                    var s = new ExrScratch();
                    foreach (var (part, chunk) in jobs) EncodeChunk(part, chunk, s);
                }
            }
            finally
            {
                foreach (var h in pins) if (h.IsAllocated) h.Free();
            }

            // magic, version, headers
            var head = new MemoryStream();
            var w = new BinaryWriter(head);
            bool tiledSingle = !multiPart && plans[0].Tiled;
            w.Write(0x01312f76);
            w.Write(2 | (longNames ? ExrFileStructure.FlagLongNames : 0) | (tiledSingle ? ExrFileStructure.FlagTiled : 0)
                      | (multiPart ? ExrFileStructure.FlagMultiPart : 0));
            foreach (var plan in plans) w.Write(plan.Header);
            if (multiPart) w.Write((byte)0);

            // offset tables (indexed by chunk number), then the chunks in file order
            long pos = head.Length;
            foreach (var plan in plans) pos += 8L * plan.Chunks.Length;
            int partNumberBytes = multiPart ? 4 : 0;
            var order = new List<(Part part, int chunk)>();
            foreach (var plan in plans)
            {
                var offsets = new long[plan.Chunks.Length];
                foreach (int c in FileOrder(plan))
                {
                    offsets[c] = pos;
                    pos += partNumberBytes + (plan.Tiled ? 20 : 8) + plan.Chunks[c].Length;
                    order.Add((plan, c));
                }
                foreach (long o in offsets) w.Write(o);
            }
            w.Flush();
            head.Position = 0;
            head.CopyTo(output);
            Span<byte> ch = stackalloc byte[24];
            foreach (var (plan, c) in order)
            {
                int n = 0;
                if (multiPart) { BinaryPrimitives.WriteInt32LittleEndian(ch, plan.Index); n = 4; }
                if (plan.Tiled)
                {
                    var level = LevelOf(plan, c);
                    int t = c - level.FirstChunk;
                    BinaryPrimitives.WriteInt32LittleEndian(ch.Slice(n), t % level.TilesX);
                    BinaryPrimitives.WriteInt32LittleEndian(ch.Slice(n + 4), t / level.TilesX);
                    BinaryPrimitives.WriteInt32LittleEndian(ch.Slice(n + 8), level.LevelX);
                    BinaryPrimitives.WriteInt32LittleEndian(ch.Slice(n + 12), level.LevelY);
                    n += 16;
                }
                else
                {
                    BinaryPrimitives.WriteInt32LittleEndian(ch.Slice(n), plan.Y0 + c * plan.LinesPerChunk);
                    n += 4;
                }
                BinaryPrimitives.WriteInt32LittleEndian(ch.Slice(n), plan.Chunks[c].Length);
                output.Write(ch.Slice(0, n + 4));
                output.Write(plan.Chunks[c]);
            }
        }

        static ExrBox2i DisplayWindow(Part p)
        {
            foreach (var a in p.Options.Attributes) if (a.Name == "displayWindow") return a.AsBox2i();
            return p.Options.DisplayWindow ?? new ExrBox2i(0, 0, p.Width - 1, p.Height - 1);
        }

        static float PixelAspect(Part p)
        {
            foreach (var a in p.Options.Attributes) if (a.Name == "pixelAspectRatio") return a.AsFloat();
            return 1f;
        }

        static Level LevelOf(Part p, int chunk)
        {
            for (int i = p.Levels.Count - 1; i > 0; i--) if (chunk >= p.Levels[i].FirstChunk) return p.Levels[i];
            return p.Levels[0];
        }

        /// <summary>Chunk numbers in the order they are written.</summary>
        static IEnumerable<int> FileOrder(Part p)
        {
            bool dec = p.LineOrder == ExrLineOrder.DecreasingY;
            if (!p.Tiled)
            {
                for (int k = 0; k < p.Chunks.Length; k++) yield return dec ? p.Chunks.Length - 1 - k : k;
                yield break;
            }
            foreach (var l in p.Levels)
                for (int row = 0; row < l.TilesY; row++)
                {
                    int ty = dec ? l.TilesY - 1 - row : row;
                    for (int tx = 0; tx < l.TilesX; tx++) yield return l.FirstChunk + ty * l.TilesX + tx;
                }
        }

        static void CheckName(string name, string what, ref bool longNames)
        {
            int n = Encoding.UTF8.GetByteCount(name);
            if (n == 0 || n > 255 || name.Contains('\0')) throw new ArgumentException($"invalid {what} name '{name}' (1..255 bytes)");
            if (n > 31) longNames = true;
        }

        static Part Plan(ExrPart src, int index, bool multiPart, List<GCHandle> pins, ref bool longNames)
        {
            var opt = src.Options;
            int width = src.Width, height = src.Height;
            if (src.Channels == null || src.Channels.Count == 0) throw new ArgumentException("no channels given");
            if (width <= 0 || height <= 0) throw new ArgumentException("image dimensions must be positive");
            if (opt.Compression < ExrCompression.None || opt.Compression > ExrCompression.Dwab)
                throw new ImageNotSupportedException($"EXR compression {opt.Compression} is not supported for encoding");
            const int lim = 1 << 30;
            long maxX = (long)opt.DataWindowX + width - 1, maxY = (long)opt.DataWindowY + height - 1;
            if (opt.DataWindowX <= -lim || opt.DataWindowY <= -lim || maxX >= lim || maxY >= lim)
                throw new ArgumentException("data window is out of range");
            bool tiled = opt.TileWidth > 0 || opt.TileHeight > 0;
            if (tiled && (opt.TileWidth <= 0 || opt.TileHeight <= 0)) throw new ArgumentException("TileWidth and TileHeight must both be set");
            if (!tiled && opt.LevelMode != ExrLevelMode.OneLevel) throw new ArgumentException("mip / rip levels need a tiled part (set TileWidth and TileHeight)");

            // channels are stored sorted by name
            var sorted = new List<ExrChannelData>(src.Channels);
            sorted.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            var p = new Part
            {
                Index = index, Width = width, Height = height, X0 = opt.DataWindowX, Y0 = opt.DataWindowY, Options = opt, Name = src.Name,
                Tiled = tiled, TileWidth = opt.TileWidth, TileHeight = opt.TileHeight, LevelMode = opt.LevelMode, Channels = sorted,
                LineOrder = tiled ? opt.LineOrder : opt.LineOrder == ExrLineOrder.DecreasingY ? ExrLineOrder.DecreasingY : ExrLineOrder.IncreasingY,
                LinesPerChunk = ExrCodecs.LinesPerChunk(opt.Compression),
                Chans = new ExrChan[sorted.Count],
            };
            var level0 = new Level { Width = width, Height = height, Sources = new Source[sorted.Count] };
            long lineBytes = 0;
            for (int i = 0; i < sorted.Count; i++)
            {
                var c = sorted[i];
                CheckName(c.Name, "channel", ref longNames);
                if (i > 0 && sorted[i - 1].Name == c.Name) throw new ArgumentException($"channel '{c.Name}' is given twice");
                var et = c.Data.GetType().GetElementType();
                int kind = et == typeof(uint) ? 0 : et == typeof(Half) ? 1 : et == typeof(float) ? 2
                    : throw new ImageNotSupportedException($"OpenEXR cannot store {et} samples; use float, Half or uint");
                var type = c.PixelType ?? (kind == 0 ? ExrPixelType.UInt : ExrPixelType.Half);
                if ((kind == 0) != (type == ExrPixelType.UInt))
                    throw new ImageNotSupportedException($"channel '{c.Name}': cannot store {(kind == 0 ? "uint" : "floating point")} samples as {type}");
                int xs = c.XSampling, ys = c.YSampling;
                if (xs < 1 || ys < 1) throw new ArgumentException($"channel '{c.Name}' has invalid sampling");
                if (xs != 1 || ys != 1)
                {
                    if (tiled) throw new ArgumentException("tiled parts cannot have subsampled channels");
                    // the format requires the data window to start and end on sample positions
                    if (ExrChunk.FloorDiv(opt.DataWindowX, xs) * xs != opt.DataWindowX || ExrChunk.FloorDiv(opt.DataWindowY, ys) * ys != opt.DataWindowY
                        || width % xs != 0 || height % ys != 0)
                        throw new ArgumentException($"channel '{c.Name}': data window position and size must be multiples of the sampling {xs}x{ys}");
                }
                int pw = width / xs, ph = height / ys;
                if (c.Data.LongLength < c.Offset + ((long)pw * ph - 1) * c.Stride + 1)
                    throw new ArgumentException($"channel '{c.Name}' has too few samples for {pw}x{ph}");
                p.Chans[i] = new ExrChan { Name = c.Name, Type = (int)type, XS = xs, YS = ys, PLinear = c.PLinear, Bytes = type == ExrPixelType.Half ? 2 : 4 };
                level0.Sources[i] = new Source { Array = c.Data, Kind = kind, Offset = c.Offset, Stride = c.Stride, PlaneWidth = pw };
                lineBytes += (long)pw * p.Chans[i].Bytes;
            }
            if (lineBytes * (tiled ? Math.Min(height, opt.TileHeight) : p.LinesPerChunk) > int.MaxValue) throw new ImageNotSupportedException("EXR chunks are too large");

            // levels
            p.Levels.Add(level0);
            if (opt.LevelMode == ExrLevelMode.MipMap)
            {
                var prev = level0;
                for (int l = 1; prev.Width > 1 || prev.Height > 1; l++)
                {
                    prev = Reduce(prev, prev.Width > 1, prev.Height > 1);
                    prev.LevelX = prev.LevelY = l;
                    p.Levels.Add(prev);
                }
            }
            else if (opt.LevelMode == ExrLevelMode.RipMap)
            {
                // order of the offset table: all x levels for y level 0, then y level 1, ...
                var rowStart = level0;
                for (int ly = 0; ; ly++)
                {
                    var prev = rowStart;
                    for (int lx = 1; prev.Width > 1; lx++)
                    {
                        prev = Reduce(prev, true, false);
                        prev.LevelX = lx; prev.LevelY = ly;
                        p.Levels.Add(prev);
                    }
                    if (rowStart.Height <= 1) break;
                    rowStart = Reduce(rowStart, false, true);
                    rowStart.LevelX = 0; rowStart.LevelY = ly + 1;
                    p.Levels.Add(rowStart);
                }
            }
            int chunkCount = 0;
            foreach (var l in p.Levels)
            {
                l.FirstChunk = chunkCount;
                if (tiled)
                {
                    l.TilesX = (l.Width + opt.TileWidth - 1) / opt.TileWidth;
                    l.TilesY = (l.Height + opt.TileHeight - 1) / opt.TileHeight;
                    chunkCount = checked(chunkCount + l.TilesX * l.TilesY);
                }
                else chunkCount = (height + p.LinesPerChunk - 1) / p.LinesPerChunk;
                for (int i = 0; i < l.Sources.Length; i++)
                {
                    var h = GCHandle.Alloc(l.Sources[i].Array, GCHandleType.Pinned);
                    pins.Add(h);
                    l.Sources[i].Data = (byte*)h.AddrOfPinnedObject();
                }
            }
            p.Chunks = new byte[chunkCount][];
            p.Header = BuildHeader(p, multiPart, chunkCount, ref longNames);
            return p;
        }

        /// <summary>
        /// Next coarser level (ROUND_DOWN sizes): floating point channels are averaged over 2 (one direction) or 2x2
        /// samples, UINT channels take the top left sample because ids cannot be averaged.
        /// </summary>
        static Level Reduce(Level src, bool inX, bool inY)
        {
            int w = inX ? Math.Max(1, src.Width / 2) : src.Width, h = inY ? Math.Max(1, src.Height / 2) : src.Height;
            int sx = inX && src.Width > 1 ? 2 : 1, sy = inY && src.Height > 1 ? 2 : 1;
            var l = new Level { Width = w, Height = h, Sources = new Source[src.Sources.Length] };
            for (int i = 0; i < src.Sources.Length; i++)
            {
                var s = src.Sources[i];
                long At(int x, int y) => s.Offset + ((long)y * src.Width + x) * s.Stride;
                Array result;
                if (s.Kind == 0)
                {
                    var a = (uint[])s.Array; var r = new uint[w * h];
                    for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) r[y * w + x] = a[At(x * sx, y * sy)];
                    result = r;
                }
                else
                {
                    var r = new float[w * h];
                    float norm = 1f / (sx * sy);
                    var fa = s.Array as float[];
                    var ha = s.Array as Half[];
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                        {
                            float sum = 0;
                            for (int dy = 0; dy < sy; dy++)
                                for (int dx = 0; dx < sx; dx++)
                                {
                                    long k = At(x * sx + dx, y * sy + dy);
                                    sum += fa != null ? fa[k] : (float)ha![k];
                                }
                            r[y * w + x] = sum * norm;
                        }
                    result = r;
                }
                l.Sources[i] = new Source { Array = result, Kind = s.Kind == 0 ? 0 : 2, Offset = 0, Stride = 1, PlaneWidth = w };
            }
            return l;
        }

        static byte[] BuildHeader(Part p, bool multiPart, int chunkCount, ref bool longNames)
        {
            var opt = p.Options;
            var hdr = new MemoryStream();
            var w = new BinaryWriter(hdr);
            var custom = new List<ExrAttribute>(opt.Attributes);
            foreach (var a in custom)
            {
                if (a == null) throw new ArgumentException("null attribute");
                CheckName(a.Name, "attribute", ref longNames);
                CheckName(a.TypeName, "attribute type", ref longNames);
                if (IsStructural(a.Name) || a.Name == "name")
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
            foreach (var c in p.Chans)
            {
                chl.Write(Encoding.UTF8.GetBytes(c.Name));
                rec.Clear();
                BinaryPrimitives.WriteInt32LittleEndian(rec.Slice(1), c.Type);
                rec[5] = (byte)(c.PLinear ? 1 : 0);
                BinaryPrimitives.WriteInt32LittleEndian(rec.Slice(9), c.XS);
                BinaryPrimitives.WriteInt32LittleEndian(rec.Slice(13), c.YS);
                chl.Write(rec);
            }
            chl.WriteByte(0);

            Attr(new ExrAttribute("channels", "chlist", chl.ToArray()));
            Attr(new ExrAttribute("compression", "compression", new[] { (byte)opt.Compression }));
            Attr(ExrAttribute.Box2i("dataWindow", new ExrBox2i(p.X0, p.Y0, p.X0 + p.Width - 1, p.Y0 + p.Height - 1)));
            if (!Has("displayWindow")) Attr(ExrAttribute.Box2i("displayWindow", opt.DisplayWindow ?? new ExrBox2i(0, 0, p.Width - 1, p.Height - 1)));
            Attr(new ExrAttribute("lineOrder", "lineOrder", new[] { (byte)p.LineOrder }));
            if (!Has("pixelAspectRatio")) Attr(ExrAttribute.Float("pixelAspectRatio", 1f));
            if (!Has("screenWindowCenter")) Attr(ExrAttribute.V2f("screenWindowCenter", 0f, 0f));
            if (!Has("screenWindowWidth")) Attr(ExrAttribute.Float("screenWindowWidth", 1f));
            if (opt.Compression is ExrCompression.Dwaa or ExrCompression.Dwab) Attr(ExrAttribute.Float("dwaCompressionLevel", opt.DwaCompressionLevel));
            if (p.Tiled)
            {
                var t = new byte[9];
                BinaryPrimitives.WriteInt32LittleEndian(t, p.TileWidth);
                BinaryPrimitives.WriteInt32LittleEndian(t.AsSpan(4), p.TileHeight);
                t[8] = (byte)p.LevelMode;      // rounding mode ROUND_DOWN in the high nibble
                Attr(new ExrAttribute("tiles", "tiledesc", t));
            }
            if (multiPart || p.Tiled)
            {
                Attr(ExrAttribute.String("type", p.Tiled ? "tiledimage" : "scanlineimage"));
                Attr(ExrAttribute.Int("chunkCount", chunkCount));
            }
            if (multiPart) Attr(ExrAttribute.String("name", p.Name!));
            foreach (var a in custom) Attr(a);
            w.Write((byte)0);
            w.Flush();
            return hdr.ToArray();
        }

        static void EncodeChunk(Part p, int index, ExrScratch s)
        {
            Level level;
            int x0, x1, y0, y1;
            if (p.Tiled)
            {
                level = LevelOf(p, index);
                int t = index - level.FirstChunk, tx = t % level.TilesX, ty = t / level.TilesX;
                x0 = tx * p.TileWidth; x1 = Math.Min(x0 + p.TileWidth, level.Width) - 1;
                y0 = ty * p.TileHeight; y1 = Math.Min(y0 + p.TileHeight, level.Height) - 1;
            }
            else
            {
                level = p.Levels[0];
                x0 = 0; x1 = p.Width - 1;
                y0 = index * p.LinesPerChunk; y1 = Math.Min(y0 + p.LinesPerChunk, p.Height) - 1;
            }
            // chunk geometry is in data window coordinates; x0.. above are relative to the level's top left pixel
            var chunk = ExrChunk.Create(p.Chans, p.X0 + x0, p.X0 + x1, p.Y0 + y0, p.Y0 + y1, s);
            int size = checked((int)chunk.Size);
            var raw = s.Bytes(2, size);
            fixed (byte* pr = raw)
            fixed (uint* h2f = ExrHalf.ToFloatBits)
            {
                byte* o = pr;
                for (int row = y0; row <= y1; row++)
                    for (int ci = 0; ci < p.Chans.Length; ci++)
                    {
                        ref readonly ExrChan ch = ref p.Chans[ci];
                        if (row % ch.YS != 0) continue;
                        ref readonly Source src = ref level.Sources[ci];
                        int n = chunk.Nx[ci];
                        long first = src.Offset + ((long)(row / ch.YS) * src.PlaneWidth + x0 / ch.XS) * src.Stride, st = src.Stride;
                        if (src.Kind == 2 && ch.Type == 1)
                        {
                            float* f = (float*)src.Data + first;
                            ushort* d = (ushort*)o;
                            for (int j = 0; j < n; j++) d[j] = ExrHalf.FromFloat(f[j * st]);
                        }
                        else if (src.Kind == 1 && ch.Type == 2)
                        {
                            ushort* hs = (ushort*)src.Data + first;
                            uint* d = (uint*)o;
                            for (int j = 0; j < n; j++) d[j] = h2f[hs[j * st]];
                        }
                        else if (src.Kind == 1)
                        {
                            ushort* hs = (ushort*)src.Data + first;
                            ushort* d = (ushort*)o;
                            if (st == 1) Buffer.MemoryCopy(hs, d, 2L * n, 2L * n);
                            else for (int j = 0; j < n; j++) d[j] = hs[j * st];
                        }
                        else
                        {
                            // float -> FLOAT and uint -> UINT are plain 32 bit copies
                            uint* u = (uint*)src.Data + first;
                            uint* d = (uint*)o;
                            if (st == 1) Buffer.MemoryCopy(u, d, 4L * n, 4L * n);
                            else for (int j = 0; j < n; j++) d[j] = u[j * st];
                        }
                        o += (long)n * ch.Bytes;
                    }
            }
            p.Chunks[index] = ExrCodecs.Encode(p.Options.Compression, raw.AsSpan(0, size), chunk, s, p.Options.ZipLevel, p.Options.DwaCompressionLevel);
        }
    }
}
