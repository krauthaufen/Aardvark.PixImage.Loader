using System;
using System.Buffers.Binary;

namespace Aardvark.ImageCodecs
{
    /// <summary>One entry of the BasisLZ image descriptor array (KTX 2.0 specification, Appendix C.7).</summary>
    internal readonly struct BasisLzImageDesc
    {
        public readonly uint Flags;
        public readonly int RgbOffset, RgbLength, AlphaOffset, AlphaLength;
        public BasisLzImageDesc(uint flags, int rgbOffset, int rgbLength, int alphaOffset, int alphaLength)
        {
            Flags = flags; RgbOffset = rgbOffset; RgbLength = rgbLength; AlphaOffset = alphaOffset; AlphaLength = alphaLength;
        }
        /// <summary>imageFlags isPFrame (0x02): the image refers to the previous frame of an animation sequence.</summary>
        public bool IsPFrame => (Flags & 2) != 0;
        public bool HasAlpha => AlphaLength != 0;
    }

    /// <summary>
    /// The decoded BasisLZ supercompression global data of a KTX2 file (KTX 2.0 Appendix C): image descriptors, the
    /// ETC1S endpoint and selector codebooks and the four slice Huffman tables. Immutable after parsing; one instance
    /// serves every image of the file.
    /// </summary>
    internal sealed class BasisLzGlobal
    {
        public int EndpointCount, SelectorCount;
        public BasisLzImageDesc[] Images = Array.Empty<BasisLzImageDesc>();
        /// <summary>Endpoint i: R, G, B (5 bit each) and the intensity table index at 4 * i ...  4 * i + 3.</summary>
        internal byte[] Endpoints = Array.Empty<byte>();
        /// <summary>Selector i: 16 two bit values, texel x + 4 * y at bits 2 * (x + 4 * y); 0 = -b, 1 = -a, 2 = +a, 3 = +b.</summary>
        internal uint[] Selectors = Array.Empty<uint>();
        internal BasisEtc1s.Huffman EndpointPred = null!, EndpointDelta = null!, Selector = null!, SelectorHistoryRle = null!;
        internal int SelectorHistorySize;
    }

    /// <summary>
    /// Decoder state of one mip level of an animation sequence (KTX2 with KTXanimData): the endpoint and selector indices
    /// of the previous frame, which P-frames refer to. Use one instance per level and decode the frames (layers) of
    /// that level in order.
    /// </summary>
    internal sealed class BasisLzVideoState
    {
        internal int BlocksX = -1, BlocksY = -1;
        internal ushort[]? RgbEndpoints, RgbSelectors, AlphaEndpoints, AlphaSelectors;
    }

    /// <summary>
    /// BasisLZ / ETC1S decoding (supercompressionScheme 1 of KTX2), written from the KTX 2.0 specification (Appendix C
    /// "BasisLZ Global Data", Appendix D "BasisLZ/ETC1S Bitstream Specification") and the public domain ".basis File
    /// Format and ETC1S Texture Video Specification"; the ETC1 block semantics are those of the Khronos Data Format
    /// Specification. Decodes to tightly packed RGBA8, bit exact.
    /// </summary>
    internal static class BasisEtc1s
    {
        // ------------------------------------------------------------------ image indexing

        /// <summary>
        /// Number of BasisLZ image descriptors of a KTX2 file: for every level, max(layerCount, 1) * faceCount *
        /// max(pixelDepth &gt;&gt; level, 1) (KTX 2.0 C).
        /// </summary>
        public static int ImageCount(int levelCount, int layerCount, int faceCount, int pixelDepth)
        {
            int n = 0;
            for (int l = 0; l < Math.Max(levelCount, 1); l++) n += Math.Max(layerCount, 1) * faceCount * Math.Max(pixelDepth >> l, 1);
            return n;
        }

        /// <summary>
        /// Index of an image in the descriptor array. Order (KTX 2.0 C): level, then layer, then face, then z slice; so
        /// all images of level 0 come first (layer major), unlike the level data, which stores the smallest level first.
        /// </summary>
        public static int ImageIndex(int level, int layer, int face, int zSlice, int layerCount, int faceCount, int pixelDepth)
        {
            int layers = Math.Max(layerCount, 1), n = 0;
            for (int l = 0; l < level; l++) n += layers * faceCount * Math.Max(pixelDepth >> l, 1);
            int depth = Math.Max(pixelDepth >> level, 1);
            return n + (layer * faceCount + face) * depth + zSlice;
        }

        // ------------------------------------------------------------------ global data

        /// <summary>
        /// Parses the supercompressionGlobalData of a BasisLZ KTX2 file. <paramref name="imageCount"/> comes from the
        /// header (<see cref="ImageCount"/>). Decodes both codebooks and the slice Huffman tables up front.
        /// </summary>
        public static BasisLzGlobal ParseGlobal(ReadOnlySpan<byte> sgd, int imageCount)
        {
            if (sgd.Length < 20) throw new ImageFormatException("BasisLZ: global data too short");
            var g = new BasisLzGlobal
            {
                EndpointCount = BinaryPrimitives.ReadUInt16LittleEndian(sgd),
                SelectorCount = BinaryPrimitives.ReadUInt16LittleEndian(sgd.Slice(2)),
            };
            long endpointsLen = BinaryPrimitives.ReadUInt32LittleEndian(sgd.Slice(4));
            long selectorsLen = BinaryPrimitives.ReadUInt32LittleEndian(sgd.Slice(8));
            long tablesLen = BinaryPrimitives.ReadUInt32LittleEndian(sgd.Slice(12));
            long extendedLen = BinaryPrimitives.ReadUInt32LittleEndian(sgd.Slice(16));
            if (imageCount < 0 || 20L + 20L * imageCount + endpointsLen + selectorsLen + tablesLen + extendedLen > sgd.Length)
                throw new ImageFormatException("BasisLZ: global data truncated");
            if (g.EndpointCount == 0 || g.SelectorCount == 0) throw new ImageFormatException("BasisLZ: empty codebook");

            g.Images = new BasisLzImageDesc[imageCount];
            for (int i = 0; i < imageCount; i++)
            {
                var d = sgd.Slice(20 + 20 * i);
                uint flags = BinaryPrimitives.ReadUInt32LittleEndian(d);
                uint ro = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(4)), rl = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(8));
                uint ao = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(12)), al = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(16));
                if ((ro | rl | ao | al) > int.MaxValue) throw new ImageFormatException("BasisLZ: bad image descriptor");
                g.Images[i] = new BasisLzImageDesc(flags, (int)ro, (int)rl, (int)ao, (int)al);
            }
            int p = 20 + 20 * imageCount;
            var endpoints = sgd.Slice(p, (int)endpointsLen); p += (int)endpointsLen;
            var selectors = sgd.Slice(p, (int)selectorsLen); p += (int)selectorsLen;
            var tables = sgd.Slice(p, (int)tablesLen);

            ReadEndpoints(g, endpoints);
            ReadSelectors(g, selectors);
            var r = new BitReader(tables);
            g.EndpointPred = Huffman.Read(ref r);
            g.EndpointDelta = Huffman.Read(ref r);
            g.Selector = Huffman.Read(ref r);
            g.SelectorHistoryRle = Huffman.Read(ref r);
            g.SelectorHistorySize = (int)r.Get(13);
            return g;
        }

        static void ReadEndpoints(BasisLzGlobal g, ReadOnlySpan<byte> data)
        {
            var r = new BitReader(data);
            var m0 = Huffman.Read(ref r);
            var m1 = Huffman.Read(ref r);
            var m2 = Huffman.Read(ref r);
            var mi = Huffman.Read(ref r);
            bool gray = r.Get(1) != 0;
            int n = g.EndpointCount;
            var e = new byte[n * 4];
            Span<int> prev = stackalloc int[3] { 16, 16, 16 };
            int prevInten = 0;
            for (int i = 0; i < n; i++)
            {
                prevInten = (prevInten + mi.Decode(ref r)) & 7;
                e[i * 4 + 3] = (byte)prevInten;
                for (int c = 0; c < (gray ? 1 : 3); c++)
                {
                    var m = prev[c] <= 9 ? m0 : prev[c] <= 21 ? m1 : m2;
                    prev[c] = (prev[c] + m.Decode(ref r)) & 31;
                    e[i * 4 + c] = (byte)prev[c];
                }
                if (gray) { e[i * 4 + 1] = e[i * 4]; e[i * 4 + 2] = e[i * 4]; }
            }
            r.CheckOverrun("endpoint codebook");
            g.Endpoints = e;
        }

        static void ReadSelectors(BasisLzGlobal g, ReadOnlySpan<byte> data)
        {
            var r = new BitReader(data);
            if (r.Get(2) != 0) throw new ImageNotSupportedException("BasisLZ: global selector codebooks");
            bool raw = r.Get(1) != 0;
            int n = g.SelectorCount;
            var s = new uint[n];
            if (raw)
            {
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < 4; j++) s[i] |= r.Get(8) << (8 * j);
            }
            else
            {
                var m = Huffman.Read(ref r);
                for (int j = 0; j < 4; j++) s[0] |= r.Get(8) << (8 * j);
                for (int i = 1; i < n; i++)
                    for (int j = 0; j < 4; j++)
                        s[i] |= (uint)((m.Decode(ref r) ^ (int)(s[i - 1] >> (8 * j))) & 0xFF) << (8 * j);
            }
            r.CheckOverrun("selector codebook");
            g.Selectors = s;
        }

        // ------------------------------------------------------------------ images

        /// <summary>
        /// Decodes image <paramref name="imageIndex"/> (see <see cref="ImageIndex"/>) of a BasisLZ KTX2 file to RGBA8:
        /// <paramref name="levelData"/> is the whole data of the image's mip level as stored in the file (the slice
        /// offsets of the descriptor are relative to it), <paramref name="width"/> x <paramref name="height"/> the
        /// level's size (max(1, pixelWidth &gt;&gt; level), ...). Writes width * height * 4 bytes, row-major, top row
        /// first (in the stored order: KTXorientation is not applied), blocks cropped at the right and bottom edges.
        /// With an alpha slice its ETC1S green channel becomes A, else A = 255. P-frames of animation sequences need
        /// <paramref name="video"/>: one state per level, frames decoded in order.
        /// </summary>
        public static void DecodeImage(BasisLzGlobal g, int imageIndex, ReadOnlySpan<byte> levelData, int width, int height,
            Span<byte> rgba, BasisLzVideoState? video = null)
        {
            if ((uint)imageIndex >= (uint)g.Images.Length) throw new ArgumentOutOfRangeException(nameof(imageIndex));
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (rgba.Length < (long)width * height * 4) throw new ArgumentException("output too small", nameof(rgba));
            var d = g.Images[imageIndex];
            int bx = (width + 3) >> 2, by = (height + 3) >> 2;
            if (video != null && (video.BlocksX != bx || video.BlocksY != by))
            {
                video.BlocksX = bx; video.BlocksY = by;
                video.RgbEndpoints = video.RgbSelectors = video.AlphaEndpoints = video.AlphaSelectors = null;
            }
            if (d.IsPFrame && (video == null || video.RgbEndpoints == null))
                throw new ImageFormatException("BasisLZ: P-frame without a previous frame");

            var ep = new ushort[bx * by];
            var sel = new ushort[bx * by];
            DecodeSlice(g, Slice(levelData, d.RgbOffset, d.RgbLength), bx, by, d.IsPFrame,
                video?.RgbEndpoints, video?.RgbSelectors, ep, sel);
            WriteBlocks(g, ep, sel, bx, width, height, rgba, alpha: false);
            if (video != null) { video.RgbEndpoints = ep; video.RgbSelectors = sel; }

            if (d.HasAlpha)
            {
                if (d.IsPFrame && video!.AlphaEndpoints == null) throw new ImageFormatException("BasisLZ: P-frame without a previous frame");
                var ea = new ushort[bx * by];
                var sa = new ushort[bx * by];
                DecodeSlice(g, Slice(levelData, d.AlphaOffset, d.AlphaLength), bx, by, d.IsPFrame,
                    video?.AlphaEndpoints, video?.AlphaSelectors, ea, sa);
                WriteBlocks(g, ea, sa, bx, width, height, rgba, alpha: true);
                if (video != null) { video.AlphaEndpoints = ea; video.AlphaSelectors = sa; }
            }
            else
                for (int i = 3; i < width * height * 4; i += 4) rgba[i] = 255;
        }

        static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> level, int offset, int length)
        {
            if (length <= 0 || (long)offset + length > level.Length) throw new ImageFormatException("BasisLZ: slice outside the level data");
            return level.Slice(offset, length);
        }

        // ETC1 intensity modifier tables (Khronos Data Format Specification, ETC1): the small and large magnitude
        static readonly int[] IntenA = { 2, 5, 9, 13, 18, 24, 33, 47 };
        static readonly int[] IntenB = { 8, 17, 29, 42, 60, 80, 106, 183 };

        /// <summary>Writes the RGB (or, for an alpha slice, the A channel from G) of the decoded blocks.</summary>
        static void WriteBlocks(BasisLzGlobal g, ushort[] ep, ushort[] sel, int bx, int width, int height, Span<byte> rgba, bool alpha)
        {
            Span<byte> pal = stackalloc byte[12];
            int by = (height + 3) >> 2;
            for (int y = 0; y < by; y++)
                for (int x = 0; x < bx; x++)
                {
                    int e = ep[y * bx + x] * 4;
                    uint s = g.Selectors[sel[y * bx + x]];
                    int a = IntenA[g.Endpoints[e + 3]], b = IntenB[g.Endpoints[e + 3]];
                    for (int c = 0; c < 3; c++)
                    {
                        int c5 = g.Endpoints[e + c], v = c5 << 3 | c5 >> 2;
                        pal[c] = Clamp(v - b); pal[3 + c] = Clamp(v - a); pal[6 + c] = Clamp(v + a); pal[9 + c] = Clamp(v + b);
                    }
                    int w = Math.Min(4, width - 4 * x), h = Math.Min(4, height - 4 * y);
                    for (int py = 0; py < h; py++)
                    {
                        int o = ((4 * y + py) * width + 4 * x) * 4;
                        for (int px = 0; px < w; px++, o += 4)
                        {
                            int k = (int)(s >> (2 * (4 * py + px)) & 3) * 3;
                            if (alpha) rgba[o + 3] = pal[k + 1];
                            else { rgba[o] = pal[k]; rgba[o + 1] = pal[k + 1]; rgba[o + 2] = pal[k + 2]; }
                        }
                    }
                }
        }

        static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

        /// <summary>
        /// Decodes one ETC1S slice (KTX 2.0 D.5) to per-block endpoint and selector codebook indices, raster order.
        /// <paramref name="prevEndpoints"/> / <paramref name="prevSelectors"/>: the previous frame's indices, needed
        /// for P-frames only.
        /// </summary>
        internal static void DecodeSlice(BasisLzGlobal g, ReadOnlySpan<byte> data, int bx, int by, bool isPFrame,
            ushort[]? prevEndpoints, ushort[]? prevSelectors, Span<ushort> endpoints, Span<ushort> selectors)
        {
            const int RepeatLastSymbol = 4 * 4 * 4 * 4;
            int nEp = g.EndpointCount, nSel = g.SelectorCount, histSize = g.SelectorHistorySize;
            int rleSymbol = nSel + histSize;
            var r = new BitReader(data);

            // endpoint index of the previous row (odd / even) and the predictor bits stored for odd rows
            var rowEp = new ushort[2 * bx];
            var predBits = new byte[(bx + 1) >> 1];
            var hist = histSize > 0 ? new int[histSize] : Array.Empty<int>();
            int rover = histSize / 2;

            int curSelRle = 0, curPred = 0, prevPredSym = 0, predRepeat = 0, prevEp = 0;
            for (int y = 0; y < by; y++)
            {
                int cur = (y & 1) * bx, other = ((y & 1) ^ 1) * bx;
                for (int x = 0; x < bx; x++)
                {
                    if ((x & 1) == 0)
                    {
                        if ((y & 1) == 0)
                        {
                            if (predRepeat > 0) { predRepeat--; curPred = prevPredSym; }
                            else
                            {
                                curPred = g.EndpointPred.Decode(ref r);
                                if (curPred == RepeatLastSymbol)
                                {
                                    predRepeat = (int)Vlc(ref r, 4) + 3 - 1;
                                    curPred = prevPredSym;
                                }
                                else prevPredSym = curPred;
                            }
                            predBits[x >> 1] = (byte)(curPred >> 4);
                        }
                        else curPred = predBits[x >> 1];
                    }
                    int pred = curPred & 3;
                    curPred >>= 2;

                    int epIndex, selIndex = 0;
                    bool reused = false;
                    // (predictions from outside the slice are invalid; they read index 0 here, never fail)
                    if (pred == 0) epIndex = prevEp;
                    else if (pred == 1) epIndex = rowEp[other + x];
                    else if (pred == 2)
                    {
                        if (isPFrame)
                        {
                            epIndex = prevEndpoints![y * bx + x];
                            selIndex = prevSelectors![y * bx + x];
                            reused = true;
                        }
                        else epIndex = x > 0 ? rowEp[other + x - 1] : 0;
                    }
                    else
                    {
                        epIndex = g.EndpointDelta.Decode(ref r) + prevEp;
                        if (epIndex >= nEp) epIndex -= nEp;
                    }
                    if ((uint)epIndex >= (uint)nEp) throw new ImageFormatException("BasisLZ: endpoint index out of range");
                    rowEp[cur + x] = (ushort)epIndex;
                    prevEp = epIndex;

                    if (!reused)
                    {
                        int sym;
                        if (curSelRle > 0) { curSelRle--; sym = nSel; }
                        else
                        {
                            sym = g.Selector.Decode(ref r);
                            if (sym == rleSymbol)
                            {
                                int run = g.SelectorHistoryRle.Decode(ref r);
                                curSelRle = run == 63 ? (int)Vlc(ref r, 7) + 3 : run + 3;
                                sym = nSel;
                                curSelRle--;
                            }
                        }
                        if (sym >= nSel)
                        {
                            int h = sym - nSel;
                            if (h >= histSize) throw new ImageFormatException("BasisLZ: selector history index out of range");
                            selIndex = hist[h];
                            if (h != 0) (hist[h / 2], hist[h]) = (hist[h], hist[h / 2]);
                        }
                        else
                        {
                            selIndex = sym;
                            if (histSize > 0)
                            {
                                hist[rover++] = sym;
                                if (rover == histSize) rover = histSize / 2;
                            }
                        }
                    }
                    if ((uint)selIndex >= (uint)nSel) throw new ImageFormatException("BasisLZ: selector index out of range");
                    endpoints[y * bx + x] = (ushort)epIndex;
                    selectors[y * bx + x] = (ushort)selIndex;
                }
            }
            r.CheckOverrun("slice");
        }

        /// <summary>Variable length code of chunks of <paramref name="bits"/> bits plus a continuation bit (D.5.2).</summary>
        static uint Vlc(ref BitReader r, int bits)
        {
            uint v = 0, mask = (1u << bits) - 1;
            for (int ofs = 0; ofs < 32; ofs += bits)
            {
                uint s = r.Get(bits + 1);
                v |= (s & mask) << ofs;
                if ((s >> bits) == 0) break;
            }
            return v;
        }

        // ------------------------------------------------------------------ bits and Huffman codes

        /// <summary>Least significant bit first reader; reads zeros past the end (checked with <see cref="CheckOverrun"/>).</summary>
        internal ref struct BitReader
        {
            readonly ReadOnlySpan<byte> d;
            int pos, cnt;
            ulong buf;
            public BitReader(ReadOnlySpan<byte> data) { d = data; pos = 0; cnt = 0; buf = 0; }

            void Fill()
            {
                while (cnt <= 56)
                {
                    ulong b = pos < d.Length ? d[pos] : 0UL;
                    pos++;
                    buf |= b << cnt;
                    cnt += 8;
                }
            }

            public uint Peek(int n) { if (cnt < n) Fill(); return (uint)(buf & ((1UL << n) - 1)); }
            public void Skip(int n) { buf >>= n; cnt -= n; }
            public uint Get(int n)
            {
                if (n == 0) return 0;
                uint v = Peek(n);
                Skip(n);
                return v;
            }

            /// <summary>Bits consumed.</summary>
            public long Position => (long)pos * 8 - cnt;

            public void CheckOverrun(string what)
            {
                if (Position > (long)d.Length * 8) throw new ImageFormatException($"BasisLZ: {what} truncated");
            }
        }

        /// <summary>Canonical Huffman code as in Deflate (RFC 1951 3.2.2), codes sent first bit first.</summary>
        internal sealed class Huffman
        {
            int maxLen;
            int[] table = Array.Empty<int>();   // indexed by the next maxLen bits: symbol | length << 16, 0 = invalid

            public static Huffman FromLengths(ReadOnlySpan<byte> lengths)
            {
                var h = new Huffman();
                Span<int> count = stackalloc int[17];
                int used = 0, last = 0;
                foreach (var l in lengths) { if (l > 16) throw new ImageFormatException("BasisLZ: bad code length"); count[l]++; }
                for (int i = 0; i < lengths.Length; i++) if (lengths[i] != 0) { used++; last = i; }
                count[0] = 0;
                for (int l = 1; l <= 16; l++) if (count[l] != 0) h.maxLen = l;
                if (used == 0) return h;   // never decoded from
                h.table = new int[1 << h.maxLen];
                if (used == 1)
                {
                    // a single symbol: its code is 0 (whatever its length); any bits decode it
                    int len = lengths[last];
                    Array.Fill(h.table, last | len << 16);
                    return h;
                }
                Span<int> next = stackalloc int[17];
                int code = 0;
                for (int l = 1; l <= 16; l++)
                {
                    code = (code + count[l - 1]) << 1;
                    next[l] = code;
                }
                for (int s = 0; s < lengths.Length; s++)
                {
                    int len = lengths[s];
                    if (len == 0) continue;
                    int c = next[len]++;
                    if (c >= 1 << len) throw new ImageFormatException("BasisLZ: over-subscribed Huffman code");
                    int rev = 0;
                    for (int i = 0; i < len; i++) rev |= (c >> i & 1) << (len - 1 - i);
                    for (int k = rev; k < h.table.Length; k += 1 << len) h.table[k] = s | len << 16;
                }
                return h;
            }

            public int Decode(ref BitReader r)
            {
                if (maxLen == 0) throw new ImageFormatException("BasisLZ: symbol from an empty Huffman table");
                int e = table[r.Peek(maxLen)];
                if (e == 0) throw new ImageFormatException("BasisLZ: invalid Huffman code");
                r.Skip(e >> 16);
                return e & 0xFFFF;
            }

            static readonly byte[] CodeLengthOrder = { 17, 18, 19, 20, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15, 16 };

            /// <summary>Reads a compressed Huffman table (KTX 2.0 D.1).</summary>
            public static Huffman Read(ref BitReader r)
            {
                int total = (int)r.Get(14);
                int ncl = (int)r.Get(5);
                if (total == 0 || ncl == 0 || ncl > 21) throw new ImageFormatException("BasisLZ: bad Huffman table header");
                Span<byte> cl = stackalloc byte[21];
                cl.Clear();
                for (int i = 0; i < ncl; i++) cl[CodeLengthOrder[i]] = (byte)r.Get(3);
                var clh = FromLengths(cl);
                var lens = new byte[total];
                int n = 0;
                while (n < total)
                {
                    int sym = clh.Decode(ref r);
                    if (sym <= 16) { lens[n++] = (byte)sym; continue; }
                    int run;
                    byte v = 0;
                    switch (sym)
                    {
                        case 17: run = (int)r.Get(3) + 3; break;
                        case 18: run = (int)r.Get(7) + 11; break;
                        case 19: run = (int)r.Get(2) + 3; v = n > 0 ? lens[n - 1] : (byte)0; break;
                        default: run = (int)r.Get(7) + 7; v = n > 0 ? lens[n - 1] : (byte)0; break;
                    }
                    if (sym >= 19 && v == 0) throw new ImageFormatException("BasisLZ: code length repeat without a previous length");
                    if (n + run > total) throw new ImageFormatException("BasisLZ: code length run too long");
                    lens.AsSpan(n, run).Fill(v);
                    n += run;
                }
                return FromLengths(lens);
            }
        }
    }
}
