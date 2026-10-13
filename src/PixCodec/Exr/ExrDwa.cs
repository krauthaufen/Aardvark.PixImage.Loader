using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace PixCodec
{
    /// <summary>
    /// DWAA / DWAB decoding. Channels are classified by rules stored in each chunk: R,G,B style triples are
    /// converted to Y'CbCr and stored as quantized 8x8 DCT coefficients (half floats: DC values zlib packed, AC values
    /// run-length + Huffman or deflate), alpha style channels are byte-plane RLE + zlib, everything else plain zlib.
    /// The inverse DCT is done in float32, so results can differ from other decoders in the last half float bit.
    /// </summary>
    internal static unsafe class ExrDwa
    {
        const int SchemeUnknown = 0, SchemeDct = 1, SchemeRle = 2;

        static readonly byte[] ZigZag =
        {
            0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
            35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
        };

        static readonly float[] Cos = BuildCos();
        static ushort[]? _toLinear;

        static float[] BuildCos()
        {
            // orthonormal DCT basis: Cos[u * 8 + x] = c(u) / 2 * cos((2x + 1) u pi / 16)
            var t = new float[64];
            for (int u = 0; u < 8; u++)
                for (int x = 0; x < 8; x++)
                    t[u * 8 + x] = (float)((u == 0 ? Math.Sqrt(0.5) : 1.0) * 0.5 * Math.Cos((2 * x + 1) * u * Math.PI / 16));
            return t;
        }

        /// <summary>The DCT works on perceptually encoded values; this maps them (as half bits) back to linear.</summary>
        static ushort[] ToLinear => _toLinear ??= BuildToLinear();

        static ushort[] BuildToLinear()
        {
            var t = new ushort[65536];
            float logBase = MathF.Pow(2.7182818f, 2.2f);
            for (int i = 0; i < 65536; i++)
            {
                float x = BitConverter.UInt32BitsToSingle(ExrHalf.ToFloatBits[i]);
                if (!float.IsFinite(x)) continue;
                float a = MathF.Abs(x);
                float p = a <= 1 ? MathF.Pow(a, 2.2f) : MathF.Pow(logBase, a - 1);
                t[i] = ExrHalf.FromFloatIeee(x < 0 ? -p : p);
            }
            return t;
        }

        static ImageFormatException Corrupt(string what) => new("EXR DWA data is corrupt: " + what);

        public static void Decode(ReadOnlySpan<byte> src, Span<byte> dst, in ExrChunk c, ExrScratch s)
        {
            int nc = c.Chans.Length;
            int w = c.X1 - c.X0 + 1, h = c.Y1 - c.Y0 + 1;
            foreach (var ch in c.Chans)
                if (ch.XS != 1 || ch.YS != 1) throw new ImageNotSupportedException("EXR DWA compression with subsampled channels is not supported");
            if (src.Length < 88) throw Corrupt("truncated header");
            Span<long> hdr = stackalloc long[11];
            for (int i = 0; i < 11; i++) hdr[i] = BinaryPrimitives.ReadInt64LittleEndian(src.Slice(i * 8));
            long version = hdr[0], unknownSize = hdr[1], unknownPacked = hdr[2], acPacked = hdr[3], dcPacked = hdr[4],
                 rlePacked = hdr[5], rleSize = hdr[6], rleRaw = hdr[7], acCount = hdr[8], dcCount = hdr[9], acCompression = hdr[10];
            if (version < 0 || version > 2) throw new ImageNotSupportedException($"EXR DWA version {version} is not supported");
            int p = 88;

            // classification rules: suffix, flags (csc index + 1, scheme, case insensitive), pixel type
            var scheme = new int[nc];
            var csc = new int[nc];
            Array.Fill(csc, -1);
            if (version >= 2)
            {
                if (src.Length - p < 2) throw Corrupt("truncated rules");
                int ruleSize = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(p));
                if (ruleSize < 2 || ruleSize > src.Length - p) throw Corrupt("rule size");
                var rules = src.Slice(p + 2, ruleSize - 2);
                p += ruleSize;
                var matched = new bool[nc];
                while (rules.Length > 0)
                {
                    int e = rules.IndexOf((byte)0);
                    if (e < 0 || rules.Length - e < 3) throw Corrupt("rules");
                    string suffix = System.Text.Encoding.UTF8.GetString(rules.Slice(0, e));
                    int flags = rules[e + 1], type = rules[e + 2];
                    rules = rules.Slice(e + 3);
                    for (int i = 0; i < nc; i++)
                    {
                        if (matched[i] || c.Chans[i].Type != type) continue;
                        if (!string.Equals(Suffix(c.Chans[i].Name), suffix, (flags & 1) != 0 ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) continue;
                        matched[i] = true;
                        scheme[i] = (flags >> 2) & 3;
                        csc[i] = (flags >> 4) - 1;
                    }
                }
            }
            else
            {
                // files from before the rules were stored: fixed, case insensitive names
                for (int i = 0; i < nc; i++)
                {
                    string sfx = Suffix(c.Chans[i].Name).ToLowerInvariant();
                    bool fp = c.Chans[i].Type != 0;
                    if (fp && sfx is "r" or "g" or "b" && fp) { scheme[i] = SchemeDct; csc[i] = sfx == "r" ? 0 : sfx == "g" ? 1 : 2; }
                    else if (fp && sfx is "y" or "by" or "ry" && fp) scheme[i] = SchemeDct;
                    else if (sfx == "a") scheme[i] = SchemeRle;
                }
            }

            // section sizes must agree with the channel layout before anything is allocated from them
            long blocks = (long)((w + 7) / 8) * ((h + 7) / 8), pixels = (long)w * h;
            long expDc = 0, expRle = 0, expUnknown = 0;
            for (int i = 0; i < nc; i++)
            {
                if (scheme[i] == SchemeDct && c.Chans[i].Type == 0) throw Corrupt("DCT on an integer channel");
                if (scheme[i] == SchemeDct) expDc += blocks;
                else if (scheme[i] == SchemeRle) expRle += pixels * c.Chans[i].Bytes;
                else if (scheme[i] == SchemeUnknown) expUnknown += pixels * c.Chans[i].Bytes;
                else throw Corrupt("channel scheme");
            }
            if (dcCount != expDc || acCount < 0 || acCount > expDc * 64 || (expRle != 0 && rleRaw != expRle)
                || (expUnknown != 0 && unknownSize != expUnknown)
                || unknownPacked < 0 || acPacked < 0 || dcPacked < 0 || rlePacked < 0 || rleSize < 0 || rleSize > 2 * expRle + 64
                || unknownPacked + acPacked + dcPacked + rlePacked > src.Length - p
                || (acCompression != 0 && acCompression != 1))
                throw Corrupt("section sizes");

            var unknown = src.Slice(p, (int)unknownPacked); p += (int)unknownPacked;
            var acData = src.Slice(p, (int)acPacked); p += (int)acPacked;
            var dcData = src.Slice(p, (int)dcPacked); p += (int)dcPacked;
            var rleData = src.Slice(p, (int)rlePacked);

            var ac = new ushort[(int)acCount];
            if (acCount > 0)
            {
                if (acCompression == 0) ExrPiz.HufDecode(acData, ac, s.Piz ??= new ExrPiz.Tables(), s);
                else ExrCodecs.Inflate(acData, MemoryMarshal.AsBytes<ushort>(ac));
            }
            var dc = new ushort[(int)expDc];
            if (expDc > 0)
            {
                var tmp = new byte[expDc * 2];
                ExrCodecs.Inflate(dcData, tmp);
                ExrCodecs.Reconstruct(tmp, MemoryMarshal.AsBytes<ushort>(dc));
            }
            var rle = new byte[expRle];
            if (expRle > 0)
            {
                var tmp = new byte[rleSize];
                ExrCodecs.Inflate(rleData, tmp);
                ExrCodecs.RleDecode(tmp, rle);
            }
            var unk = new byte[expUnknown];
            if (expUnknown > 0) ExrCodecs.Inflate(unknown, unk);

            // DCT channels decode to planes of half bits
            var planes = new ushort[nc][];
            // DC planes and the AC stream follow the decoding order: colour triples (as Y', Cb, Cr) first, then the rest
            var dcStart = new int[nc];
            int dcPos = 0;
            for (int i = 0; i < nc; i++)
                if (scheme[i] == SchemeDct) planes[i] = new ushort[pixels];
            int acPos = 0;
            var done = new bool[nc];
            var set = new int[3];
            for (int i = 0; i < nc; i++)
            {
                // colour triples (same layer prefix, csc index 0,1,2) are decoded together, before the single channels
                if (scheme[i] != SchemeDct || csc[i] < 0 || done[i]) continue;
                string prefix = Prefix(c.Chans[i].Name);
                set[0] = set[1] = set[2] = -1;
                for (int j = 0; j < nc; j++)
                    if (scheme[j] == SchemeDct && csc[j] >= 0 && csc[j] < 3 && !done[j] && Prefix(c.Chans[j].Name) == prefix && set[csc[j]] < 0) set[csc[j]] = j;
                if (set[0] < 0 || set[1] < 0 || set[2] < 0 || c.Chans[set[0]].Type != c.Chans[set[1]].Type || c.Chans[set[0]].Type != c.Chans[set[2]].Type) continue;
                done[set[0]] = done[set[1]] = done[set[2]] = true;
                for (int k = 0; k < 3; k++) { dcStart[set[k]] = dcPos; dcPos += (int)blocks; }
                DecodeDct(3, set, planes, dc, dcStart, ac, ref acPos, w, h);
            }
            for (int i = 0; i < nc; i++)
            {
                if (scheme[i] != SchemeDct || done[i]) continue;
                set[0] = i;
                dcStart[i] = dcPos; dcPos += (int)blocks;
                DecodeDct(1, set, planes, dc, dcStart, ac, ref acPos, w, h);
            }

            // assemble the line layout
            int lineBytes = 0;
            var lineOff = new int[nc];
            for (int i = 0; i < nc; i++) { lineOff[i] = lineBytes; lineBytes += w * c.Chans[i].Bytes; }
            int rlePos = 0, unkPos = 0;
            fixed (byte* pd = dst)
            fixed (uint* h2f = ExrHalf.ToFloatBits)
                for (int i = 0; i < nc; i++)
                {
                    int bytes = c.Chans[i].Bytes;
                    if (scheme[i] == SchemeDct)
                    {
                        var plane = planes[i];
                        for (int y = 0; y < h; y++)
                        {
                            byte* row = pd + (long)y * lineBytes + lineOff[i];
                            if (bytes == 2) for (int x = 0; x < w; x++) ((ushort*)row)[x] = plane[y * w + x];
                            else for (int x = 0; x < w; x++) ((uint*)row)[x] = h2f[plane[y * w + x]];
                        }
                    }
                    else if (scheme[i] == SchemeRle)
                    {
                        // one plane per byte of the sample type, each covering the whole chunk
                        for (int b = 0; b < bytes; b++)
                        {
                            for (int y = 0; y < h; y++)
                            {
                                byte* row = pd + (long)y * lineBytes + lineOff[i] + b;
                                for (int x = 0; x < w; x++) row[x * bytes] = rle[rlePos + y * w + x];
                            }
                            rlePos += (int)pixels;
                        }
                    }
                    else
                    {
                        for (int y = 0; y < h; y++)
                        {
                            unk.AsSpan(unkPos, w * bytes).CopyTo(new Span<byte>(pd + (long)y * lineBytes + lineOff[i], w * bytes));
                            unkPos += w * bytes;
                        }
                    }
                }
        }

        public static byte[] Encode(ReadOnlySpan<byte> raw, in ExrChunk c, ExrScratch s, float level) => throw new ImageNotSupportedException("DWA encoding");

        static string Suffix(string name) { int d = name.LastIndexOf('.'); return d < 0 ? name : name.Substring(d + 1); }
        static string Prefix(string name) { int d = name.LastIndexOf('.'); return d < 0 ? "" : name.Substring(0, d); }

        // The inverse DCT result is rounded to a half and sent through a steep lookup table, so it has to reproduce the
        // reference decoder's float32 results to the last bit: same constants (7 digit literals, not exact cosines)
        // and the same order of additions. The reference has several SIMD variants that differ in exactly these
        // details; this is the order of its AVX code path (the one x86-64 machines with AVX run), established by
        // comparing against the library on millions of samples: no other association of the sums matches.
        const float Ca = 3.535536e-01f, Cb = 4.903927e-01f, Cc = 4.619398e-01f, Cd = 4.157349e-01f,
                    Ce = 2.777855e-01f, Cf = 1.913422e-01f, Cg = 9.754573e-02f;

        /// <summary>8x8 inverse DCT in place (row major): rows first, then columns.</summary>
        static void InverseDct(float* data)
        {
            const float a = Ca, b = Cb, c = Cc, d = Cd, e = Ce, f = Cf, g = Cg;
            for (int i = 0; i < 8; i++)
            {
                float* p = data + i * 8;
                float p0 = p[0], p1 = p[1], p2 = p[2], p3 = p[3], p4 = p[4], p5 = p[5], p6 = p[6], p7 = p[7];
                // every output is (pair) + (pair) of products
                float e0 = (a * p0 + c * p2) + (a * p4 + f * p6), o0 = (b * p1 + d * p3) + (e * p5 + g * p7);
                float e1 = (a * p0 + f * p2) + (-a * p4 + -c * p6), o1 = (d * p1 + -g * p3) + (-b * p5 + -e * p7);
                float e2 = (a * p0 + -f * p2) + (-a * p4 + c * p6), o2 = (e * p1 + -b * p3) + (g * p5 + d * p7);
                float e3 = (a * p0 + -c * p2) + (a * p4 + -f * p6), o3 = (g * p1 + -e * p3) + (d * p5 + -b * p7);
                p[0] = e0 + o0; p[7] = e0 - o0;
                p[1] = e1 + o1; p[6] = e1 - o1;
                p[2] = e2 + o2; p[5] = e2 - o2;
                p[3] = e3 + o3; p[4] = e3 - o3;
            }
            for (int i = 0; i < 8; i++)
            {
                float* p = data + i;
                float p0 = p[0], p1 = p[8], p2 = p[16], p3 = p[24], p4 = p[32], p5 = p[40], p6 = p[48], p7 = p[56];
                float sum = a * p0 + a * p4, dif = a * p0 - a * p4;
                float t1 = c * p2 + f * p6, t2 = f * p2 - c * p6;
                float e0 = sum + t1, e1 = dif + t2, e2 = dif - t2, e3 = sum - t1;
                // the odd sums are associated differently for every output
                float o0 = (b * p1 + d * p3) + (e * p5 + g * p7);
                float o1 = (d * p1 + (-g * p3 + -b * p5)) + -e * p7;
                float o2 = ((e * p1 + -b * p3) + g * p5) + d * p7;
                float o3 = (g * p1 + d * p5) + (-e * p3 + -b * p7);
                p[0] = e0 + o0; p[56] = e0 - o0;
                p[8] = e1 + o1; p[48] = e1 - o1;
                p[16] = e2 + o2; p[40] = e2 - o2;
                p[24] = e3 + o3; p[32] = e3 - o3;
            }
        }

        static void DecodeDct(int ncomp, int[] chan, ushort[][] planes, ushort[] dc, int[] dcStart, ushort[] ac, ref int acPos, int w, int h)
        {
            int nbx = (w + 7) / 8, nby = (h + 7) / 8;
            float* f = stackalloc float[3 * 64];
            float* tmp = stackalloc float[64];
            ushort* zig = stackalloc ushort[64];
            var toLinear = ToLinear;
            fixed (uint* h2f = ExrHalf.ToFloatBits)
            fixed (float* cos = Cos)
            fixed (byte* zz = ZigZag)
                for (int by = 0; by < nby; by++)
                    for (int bx = 0; bx < nbx; bx++)
                    {
                        for (int comp = 0; comp < ncomp; comp++)
                        {
                            float* blk = f + comp * 64;
                            new Span<ushort>(zig, 64).Clear();
                            zig[0] = dc[dcStart[chan[comp]] + by * nbx + bx];
                            int k = 1, last = 0;
                            while (k < 64)
                            {
                                if (acPos >= ac.Length) throw Corrupt("AC data is truncated");
                                ushort v = ac[acPos++];
                                if (v == 0xff00) break;                       // end of block
                                if ((v >> 8) == 0xff) k += v & 0xff;          // run of zeros
                                else { zig[k] = v; last = k; k++; }
                            }
                            if (last == 0)
                            {
                                float v = *(float*)(h2f + zig[0]) * 3.535536e-01f * 3.535536e-01f;
                                for (int i = 0; i < 64; i++) blk[i] = v;
                                continue;
                            }
                            for (int i = 0; i < 64; i++) blk[zz[i]] = *(float*)(h2f + zig[i]);
                            InverseDct(blk);
                        }
                        if (ncomp == 3)
                            for (int i = 0; i < 64; i++)
                            {
                                // Rec. 709 Y'CbCr -> R'G'B'
                                float y = f[i], cb = f[64 + i], cr = f[128 + i];
                                f[i] = y + 1.5747f * cr;
                                f[64 + i] = y - 0.1873f * cb - 0.4682f * cr;
                                f[128 + i] = y + 1.8556f * cb;
                            }
                        int x0 = bx * 8, y0 = by * 8, bw = Math.Min(8, w - x0), bh = Math.Min(8, h - y0);
                        for (int comp = 0; comp < ncomp; comp++)
                        {
                            var plane = planes[chan[comp]];
                            float* blk = f + comp * 64;
                            for (int y = 0; y < bh; y++)
                                for (int x = 0; x < bw; x++)
                                    plane[(y0 + y) * w + x0 + x] = toLinear[ExrHalf.FromFloatIeee(blk[y * 8 + x])];
                        }
                    }
        }
    }
}
