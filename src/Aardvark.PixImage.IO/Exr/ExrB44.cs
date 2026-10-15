using System;

namespace Aardvark.ImageCodecs
{
    /// <summary>
    /// B44 / B44A: HALF channels are coded in 4x4 blocks of 14 bytes (a 16 bit base value plus fifteen 6 bit
    /// differences with a common shift); B44A stores uniform blocks in 3 bytes. Other channel types are stored as is.
    /// The data is organised channel by channel.
    /// </summary>
    internal static unsafe class ExrB44
    {
        static ushort[]? _expTable, _logTable;

        // pLinear channels are passed through exp(x/8) before packing and 8*log(x) after unpacking.
        static ushort[] ExpTable => _expTable ??= BuildTable(true);
        static ushort[] LogTable => _logTable ??= BuildTable(false);

        static ushort[] BuildTable(bool exp)
        {
            var t = new ushort[65536];
            float limit = 8f * MathF.Log(65504f);
            for (int i = 0; i < 65536; i++)
            {
                float f = BitConverter.UInt32BitsToSingle(ExrHalf.ToFloatBits[i]);
                float r;
                if (!float.IsFinite(f)) r = 0;
                else if (exp) r = f >= limit ? 65504f : MathF.Exp(f / 8f);
                else r = f < 0 ? 0 : 8f * MathF.Log(f);
                t[i] = ExrHalf.FromFloatIeee(r);
            }
            return t;
        }

        static ImageFormatException Corrupt() => new("EXR B44 data is truncated");

        public static void Decode(ReadOnlySpan<byte> src, Span<byte> dst, in ExrChunk c, ExrScratch s)
        {
            int nc = c.Chans.Length;
            var planes = s.Bytes(0, dst.Length);
            ushort* blk = stackalloc ushort[16];
            fixed (byte* ps = src)
            fixed (byte* pp = planes)
            fixed (byte* pd = dst)
            {
                byte* inp = ps, end = ps + src.Length;
                byte* plane = pp;
                for (int ci = 0; ci < nc; ci++)
                {
                    int nx = c.Nx[ci], ny = c.Ny[ci];
                    long bytes = (long)nx * ny * c.Chans[ci].Bytes;
                    if (c.Chans[ci].Type != 1)
                    {
                        if (end - inp < bytes) throw Corrupt();
                        Buffer.MemoryCopy(inp, plane, bytes, bytes);
                        inp += bytes; plane += bytes;
                        continue;
                    }
                    ushort[]? lin = c.Chans[ci].PLinear ? LogTable : null;
                    ushort* img = (ushort*)plane;
                    for (int y = 0; y < ny; y += 4)
                        for (int x = 0; x < nx; x += 4)
                        {
                            if (end - inp < 3) throw Corrupt();
                            if (inp[2] >= (13 << 2))
                            {
                                int v = (inp[0] << 8) | inp[1];
                                v = (v & 0x8000) != 0 ? v & 0x7fff : ~v & 0xffff;
                                for (int i = 0; i < 16; i++) blk[i] = (ushort)v;
                                inp += 3;
                            }
                            else
                            {
                                if (end - inp < 14) throw Corrupt();
                                Unpack14(inp, blk);
                                inp += 14;
                            }
                            if (lin != null) for (int i = 0; i < 16; i++) blk[i] = lin[blk[i]];
                            int bw = Math.Min(4, nx - x), bh = Math.Min(4, ny - y);
                            for (int j = 0; j < bh; j++)
                            {
                                ushort* row = img + (long)(y + j) * nx + x;
                                for (int i = 0; i < bw; i++) row[i] = blk[j * 4 + i];
                            }
                        }
                    plane += bytes;
                }

                // channel planes -> line layout
                var cur = s.Cursor;
                long off = 0;
                for (int ci = 0; ci < nc; ci++) { cur[ci] = off; off += (long)c.Nx[ci] * c.Ny[ci] * c.Chans[ci].Bytes; }
                byte* d = pd;
                for (int y = c.Y0; y <= c.Y1; y++)
                    for (int ci = 0; ci < nc; ci++)
                    {
                        if (!ExrChunk.IsSampled(y, c.Chans[ci].YS)) continue;
                        int bytes = c.Nx[ci] * c.Chans[ci].Bytes;
                        Buffer.MemoryCopy(pp + cur[ci], d, bytes, bytes);
                        cur[ci] += bytes;
                        d += bytes;
                    }
            }
        }

        static void Unpack14(byte* b, ushort* s)
        {
            int shift = b[2] >> 2;
            int bias = 0x20 << shift;
            int s0 = (b[0] << 8) | b[1];
            int s4 = s0 + ((((b[2] << 4) | (b[3] >> 4)) & 0x3f) << shift) - bias;
            int s8 = s4 + ((((b[3] << 2) | (b[4] >> 6)) & 0x3f) << shift) - bias;
            int s12 = s8 + ((b[4] & 0x3f) << shift) - bias;
            int s1 = s0 + ((b[5] >> 2) << shift) - bias;
            int s5 = s4 + ((((b[5] << 4) | (b[6] >> 4)) & 0x3f) << shift) - bias;
            int s9 = s8 + ((((b[6] << 2) | (b[7] >> 6)) & 0x3f) << shift) - bias;
            int s13 = s12 + ((b[7] & 0x3f) << shift) - bias;
            int s2 = s1 + ((b[8] >> 2) << shift) - bias;
            int s6 = s5 + ((((b[8] << 4) | (b[9] >> 4)) & 0x3f) << shift) - bias;
            int s10 = s9 + ((((b[9] << 2) | (b[10] >> 6)) & 0x3f) << shift) - bias;
            int s14 = s13 + ((b[10] & 0x3f) << shift) - bias;
            int s3 = s2 + ((b[11] >> 2) << shift) - bias;
            int s7 = s6 + ((((b[11] << 4) | (b[12] >> 4)) & 0x3f) << shift) - bias;
            int s11 = s10 + ((((b[12] << 2) | (b[13] >> 6)) & 0x3f) << shift) - bias;
            int s15 = s14 + ((b[13] & 0x3f) << shift) - bias;
            s[0] = (ushort)s0; s[1] = (ushort)s1; s[2] = (ushort)s2; s[3] = (ushort)s3;
            s[4] = (ushort)s4; s[5] = (ushort)s5; s[6] = (ushort)s6; s[7] = (ushort)s7;
            s[8] = (ushort)s8; s[9] = (ushort)s9; s[10] = (ushort)s10; s[11] = (ushort)s11;
            s[12] = (ushort)s12; s[13] = (ushort)s13; s[14] = (ushort)s14; s[15] = (ushort)s15;
            // back from the ordered representation to sign-magnitude half bits
            for (int i = 0; i < 16; i++)
                s[i] = (s[i] & 0x8000) != 0 ? (ushort)(s[i] & 0x7fff) : (ushort)~s[i];
        }

        static int ShiftAndRound(int x, int shift)
        {
            // x >> shift, rounded to nearest, ties to even
            x <<= 1;
            int a = (1 << shift) - 1;
            shift += 1;
            int b = (x >> shift) & 1;
            return (x + a + b) >> shift;
        }

        /// <summary>Packs a 4x4 block; returns 14, or 3 for a uniform block when flat blocks are allowed.</summary>
        static int Pack(ushort* s, byte* b, bool flatFields)
        {
            int* d = stackalloc int[16];
            int* r = stackalloc int[15];
            int* t = stackalloc int[16];
            int tMax = 0;
            for (int i = 0; i < 16; i++)
            {
                // infinities and NaNs cannot be represented; map half bits to an order preserving unsigned value
                t[i] = (s[i] & 0x7c00) == 0x7c00 ? 0x8000 : (s[i] & 0x8000) != 0 ? ~s[i] & 0xffff : s[i] | 0x8000;
                if (t[i] > tMax) tMax = t[i];
            }
            const int bias = 0x20;
            int shift = -1, rMin, rMax;
            do
            {
                shift++;
                for (int i = 0; i < 16; i++) d[i] = ShiftAndRound(tMax - t[i], shift);
                r[0] = d[0] - d[4] + bias; r[1] = d[4] - d[8] + bias; r[2] = d[8] - d[12] + bias;
                r[3] = d[0] - d[1] + bias; r[4] = d[4] - d[5] + bias; r[5] = d[8] - d[9] + bias; r[6] = d[12] - d[13] + bias;
                r[7] = d[1] - d[2] + bias; r[8] = d[5] - d[6] + bias; r[9] = d[9] - d[10] + bias; r[10] = d[13] - d[14] + bias;
                r[11] = d[2] - d[3] + bias; r[12] = d[6] - d[7] + bias; r[13] = d[10] - d[11] + bias; r[14] = d[14] - d[15] + bias;
                rMin = r[0]; rMax = r[0];
                for (int i = 1; i < 15; i++) { if (r[i] < rMin) rMin = r[i]; if (r[i] > rMax) rMax = r[i]; }
            }
            while (rMin < 0 || rMax > 0x3f);

            if (rMin == bias && rMax == bias && flatFields)
            {
                b[0] = (byte)(t[0] >> 8); b[1] = (byte)t[0]; b[2] = 0xfc;
                return 3;
            }
            // base value such that the block maximum is reproduced as exactly as possible
            int t0 = tMax - (d[0] << shift);
            b[0] = (byte)(t0 >> 8); b[1] = (byte)t0;
            b[2] = (byte)((shift << 2) | (r[0] >> 4));
            b[3] = (byte)((r[0] << 4) | (r[1] >> 2));
            b[4] = (byte)((r[1] << 6) | r[2]);
            b[5] = (byte)((r[3] << 2) | (r[4] >> 4));
            b[6] = (byte)((r[4] << 4) | (r[5] >> 2));
            b[7] = (byte)((r[5] << 6) | r[6]);
            b[8] = (byte)((r[7] << 2) | (r[8] >> 4));
            b[9] = (byte)((r[8] << 4) | (r[9] >> 2));
            b[10] = (byte)((r[9] << 6) | r[10]);
            b[11] = (byte)((r[11] << 2) | (r[12] >> 4));
            b[12] = (byte)((r[12] << 4) | (r[13] >> 2));
            b[13] = (byte)((r[13] << 6) | r[14]);
            return 14;
        }

        public static ReadOnlySpan<byte> Encode(ReadOnlySpan<byte> raw, scoped in ExrChunk c, ExrScratch s, bool flatFields)
        {
            int nc = c.Chans.Length;
            var planes = s.Bytes(0, raw.Length);
            // 14 bytes per 4x4 block of 2 byte samples can exceed the raw size for tiny images
            long worst = 0;
            for (int ci = 0; ci < nc; ci++)
                worst += c.Chans[ci].Type == 1 ? 14L * ((c.Nx[ci] + 3) / 4) * ((c.Ny[ci] + 3) / 4) : (long)c.Nx[ci] * c.Ny[ci] * 4;
            if (worst > int.MaxValue) throw new ImageNotSupportedException("EXR chunk is too large");
            var outBuf = s.Bytes(1, (int)worst);
            ushort* blk = stackalloc ushort[16];
            fixed (byte* pr = raw)
            fixed (byte* pp = planes)
            fixed (byte* po = outBuf)
            {
                // line layout -> channel planes
                var cur = s.Cursor;
                long off = 0;
                for (int ci = 0; ci < nc; ci++) { cur[ci] = off; off += (long)c.Nx[ci] * c.Ny[ci] * c.Chans[ci].Bytes; }
                byte* r = pr;
                for (int y = c.Y0; y <= c.Y1; y++)
                    for (int ci = 0; ci < nc; ci++)
                    {
                        if (!ExrChunk.IsSampled(y, c.Chans[ci].YS)) continue;
                        int bytes = c.Nx[ci] * c.Chans[ci].Bytes;
                        Buffer.MemoryCopy(r, pp + cur[ci], bytes, bytes);
                        cur[ci] += bytes;
                        r += bytes;
                    }

                byte* o = po, plane = pp;
                for (int ci = 0; ci < nc; ci++)
                {
                    int nx = c.Nx[ci], ny = c.Ny[ci];
                    long bytes = (long)nx * ny * c.Chans[ci].Bytes;
                    if (c.Chans[ci].Type != 1)
                    {
                        Buffer.MemoryCopy(plane, o, bytes, bytes);
                        o += bytes; plane += bytes;
                        continue;
                    }
                    ushort[]? lin = c.Chans[ci].PLinear ? ExpTable : null;
                    ushort* img = (ushort*)plane;
                    for (int y = 0; y < ny; y += 4)
                        for (int x = 0; x < nx; x += 4)
                        {
                            // edge blocks replicate the last column / row
                            for (int j = 0; j < 4; j++)
                            {
                                ushort* row = img + (long)Math.Min(y + j, ny - 1) * nx;
                                for (int i = 0; i < 4; i++) blk[j * 4 + i] = row[Math.Min(x + i, nx - 1)];
                            }
                            if (lin != null) for (int i = 0; i < 16; i++) blk[i] = lin[blk[i]];
                            o += Pack(blk, o, flatFields);
                        }
                    plane += bytes;
                }
                return outBuf.AsSpan(0, (int)(o - po));
            }
        }
    }
}
