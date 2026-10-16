using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Aardvark.ImageCodecs
{
    /// <summary>Writes JPEG-LS files (T.87): headers, then one scan per component or one interleaved scan.</summary>
    [SkipLocalsInit]
    internal static unsafe class JpegLsEncoder
    {
        sealed class Output
        {
            public byte[] Buf;
            public GCHandle Pin;
            public int Len;
            public Output(int cap) { Buf = new byte[cap]; Pin = GCHandle.Alloc(Buf, GCHandleType.Pinned); }
            public byte* Base => (byte*)Pin.AddrOfPinnedObject();
            public void Byte(int b) { Ensure(1); Buf[Len++] = (byte)b; }
            public void U16(int v) { Byte(v >> 8); Byte(v); }
            public void Bytes(ReadOnlySpan<byte> b) { Ensure(b.Length); b.CopyTo(Buf.AsSpan(Len)); Len += b.Length; }
            public void Marker(int m) { Byte(0xFF); Byte(m); }
            public void Ensure(long more)
            {
                if (Len + more <= Buf.Length) return;
                long cap = Math.Max(Len + more, (long)Buf.Length * 2);
                if (cap > Array.MaxLength) throw new ImageNotSupportedException("JPEG-LS: encoded image exceeds 2 GB");
                var nb = new byte[cap];
                Buf.AsSpan(0, Len).CopyTo(nb);
                Pin.Free();
                Buf = nb;
                Pin = GCHandle.Alloc(Buf, GCHandleType.Pinned);
            }
        }

        public static byte[] Encode(RawImage image, JpegLsEncodeOptions o)
        {
            if (image.SampleType is not (SampleType.UInt8 or SampleType.UInt16))
                throw new ArgumentException($"JPEG-LS: cannot write {image.SampleType} samples (UInt8 or UInt16 needed)");
            int w = image.Width, h = image.Height, nc = image.Channels;
            if (nc > 255) throw new ArgumentException("JPEG-LS: at most 255 channels");
            int bits = o.BitsPerSample != 0 ? o.BitsPerSample : image.SampleType == SampleType.UInt8 ? 8 : 16;
            if (bits < 2 || bits > 16 || (image.SampleType == SampleType.UInt8 && bits > 8))
                throw new ArgumentException($"JPEG-LS: BitsPerSample {bits} does not fit {image.SampleType} samples");
            var pre = o.Preset;
            int maxVal = pre?.MaxVal ?? 0;
            if (maxVal == 0) maxVal = (1 << bits) - 1;
            if (maxVal > (1 << bits) - 1) throw new ArgumentException($"JPEG-LS: MaxVal {maxVal} exceeds {bits} bits");
            if (o.Near < 0 || o.Near > Math.Min(255, maxVal / 2)) throw new ArgumentException($"JPEG-LS: NEAR {o.Near} out of range 0..{Math.Min(255, maxVal / 2)}");
            JlsParams prm;
            try { prm = JlsParams.Resolve(bits, o.Near, maxVal, pre?.T1 ?? 0, pre?.T2 ?? 0, pre?.T3 ?? 0, pre?.Reset ?? 0); }
            catch (ImageFormatException e) { throw new ArgumentException(e.Message); }
            var ilv = nc == 1 ? JpegLsInterleave.None : o.Interleave ?? (nc is 3 or 4 ? JpegLsInterleave.Sample : JpegLsInterleave.None);
            var tf = o.ColorTransform;
            if (tf != JpegLsColorTransform.None)
            {
                if (nc < 3) throw new ArgumentException("JPEG-LS: the HP colour transforms need at least 3 channels");
                if (maxVal != (1 << bits) - 1) throw new ArgumentException("JPEG-LS: the HP colour transforms need MaxVal = 2^bits - 1");
            }
            if (o.RestartInterval < 0 || o.RestartInterval > 65535) throw new ArgumentException("JPEG-LS: RestartInterval must be 0..65535");
            // check the samples fit MAXVAL
            if (maxVal < (image.SampleType == SampleType.UInt8 ? 255 : 65535))
            {
                if (image.SampleType == SampleType.UInt8) { foreach (var v in (byte[])image.Data) if (v > maxVal) throw new ArgumentException($"JPEG-LS: sample {v} exceeds MaxVal {maxVal}"); }
                else foreach (var v in (ushort[])image.Data) if (v > maxVal) throw new ArgumentException($"JPEG-LS: sample {v} exceeds MaxVal {maxVal}");
            }

            var coder = new JlsCoder(prm);
            long est = (long)w * h * nc * bits / 16 + 1024;
            var outp = new Output((int)Math.Min(Math.Max(est, 4096), Array.MaxLength));
            try
            {
                outp.Marker(0xD8);
                if (o.Spiff) WriteSpiff(outp, image, bits);
                if (tf != JpegLsColorTransform.None)
                {
                    outp.Marker(0xE8); outp.U16(7); outp.Bytes("mrfx"u8); outp.Byte((int)tf);
                }
                bool oversize = w > 65535 || h > 65535;
                outp.Marker(0xF7);
                outp.U16(8 + 3 * nc);
                outp.Byte(bits);
                outp.U16(oversize ? 0 : h);
                outp.U16(oversize ? 0 : w);
                outp.Byte(nc);
                for (int c = 0; c < nc; c++) { outp.Byte(c + 1); outp.Byte(0x11); outp.Byte(0); }
                if (oversize)
                {
                    outp.Marker(0xF8); outp.U16(12); outp.Byte(4); outp.Byte(4);
                    outp.U16(h >> 16); outp.U16(h & 0xFFFF); outp.U16(w >> 16); outp.U16(w & 0xFFFF);
                }
                // explicit for MAXVAL > 4095 too: some decoders (JAI, old HP) get those defaults wrong; CharLS does the same
                if (!prm.IsDefault(bits) || prm.MaxVal > 4095)
                {
                    outp.Marker(0xF8); outp.U16(13); outp.Byte(1);
                    outp.U16(prm.MaxVal); outp.U16(prm.T1); outp.U16(prm.T2); outp.U16(prm.T3); outp.U16(prm.Reset);
                }
                if (o.RestartInterval > 0) { outp.Marker(0xDD); outp.U16(4); outp.U16(o.RestartInterval); }

                var pin = GCHandle.Alloc(image.Data, GCHandleType.Pinned);
                try
                {
                    var src = new Source((byte*)pin.AddrOfPinnedObject(), image.SampleType == SampleType.UInt16, w, nc, tf, bits);
                    if (ilv == JpegLsInterleave.None)
                    {
                        for (int c = 0; c < nc; c++)
                        {
                            WriteSos(outp, new[] { c }, o.Near, 0);
                            if (c > 0) coder.ResetContexts();
                            EncodeScan(outp, coder, src, new[] { c }, h, w, 0, o.RestartInterval);
                        }
                    }
                    else
                    {
                        var all = new int[nc];
                        for (int c = 0; c < nc; c++) all[c] = c;
                        WriteSos(outp, all, o.Near, (int)ilv);
                        EncodeScan(outp, coder, src, all, h, w, (int)ilv, o.RestartInterval);
                    }
                }
                finally { pin.Free(); }
                outp.Marker(0xD9);
                return outp.Buf.AsSpan(0, outp.Len).ToArray();
            }
            finally { outp.Pin.Free(); }
        }

        static void WriteSpiff(Output o, RawImage img, int bits)
        {
            int nc = img.Channels;
            int cs = nc == 1 ? 8 : nc == 3 ? 10 : 2;   // grayscale, RGB, "not specified"
            o.Marker(0xE8); o.U16(32);
            o.Bytes("SPIFF\0"u8);
            o.Byte(2); o.Byte(0);   // version 2.0
            o.Byte(0);              // profile: none
            o.Byte(nc);
            o.U16(img.Height >> 16); o.U16(img.Height & 0xFFFF);
            o.U16(img.Width >> 16); o.U16(img.Width & 0xFFFF);
            o.Byte(cs);
            o.Byte(bits);
            o.Byte(6);              // compression: JPEG-LS
            double dx = img.Metadata?.DpiX ?? 0, dy = img.Metadata?.DpiY ?? 0;
            if (dx > 0 && dy > 0)
            {
                o.Byte(1);
                uint vy = (uint)Math.Round(dy), vx = (uint)Math.Round(dx);
                o.U16((int)(vy >> 16)); o.U16((int)(vy & 0xFFFF)); o.U16((int)(vx >> 16)); o.U16((int)(vx & 0xFFFF));
            }
            else
            {
                o.Byte(0);
                o.U16(0); o.U16(1); o.U16(0); o.U16(1);   // aspect ratio 1:1
            }
            // end of directory entry, followed by the SOI that SPIFF wants after it
            o.Marker(0xE8); o.U16(8); o.U16(0); o.U16(1); o.Marker(0xD8);
        }

        static void WriteSos(Output o, int[] comps, int near, int ilv)
        {
            o.Marker(0xDA);
            o.U16(6 + 2 * comps.Length);
            o.Byte(comps.Length);
            foreach (var c in comps) { o.Byte(c + 1); o.Byte(0); }
            o.Byte(near); o.Byte(ilv); o.Byte(0);
        }

        /// <summary>Reads lines of the image as ints, applying the HP colour transform.</summary>
        readonly struct Source
        {
            readonly byte* _p;
            readonly bool _wide;
            readonly int _w, _nc, _bits;
            readonly JpegLsColorTransform _tf;

            public Source(byte* p, bool wide, int w, int nc, JpegLsColorTransform tf, int bits)
            { _p = p; _wide = wide; _w = w; _nc = nc; _tf = tf; _bits = bits; }

            public void Line(int y, int c, int* dst)
            {
                int w = _w, nc = _nc;
                long i0 = (long)y * w * nc;
                if (_tf != JpegLsColorTransform.None && c < 3)
                {
                    int range = 1 << _bits, mask = range - 1, half = range >> 1;
                    for (int x = 0; x < w; x++)
                    {
                        long i = i0 + (long)x * nc;
                        int r = Get(i), g = Get(i + 1), b = Get(i + 2);
                        dst[x] = JpegLsColor.Forward(_tf, r, g, b, c, half, mask);
                    }
                    return;
                }
                if (_wide)
                {
                    ushort* s = (ushort*)_p + i0 + c;
                    if (nc == 1) for (int x = 0; x < w; x++) dst[x] = s[x];
                    else for (int x = 0; x < w; x++) dst[x] = s[(long)x * nc];
                }
                else
                {
                    byte* s = _p + i0 + c;
                    if (nc == 1) for (int x = 0; x < w; x++) dst[x] = s[x];
                    else for (int x = 0; x < w; x++) dst[x] = s[(long)x * nc];
                }
            }

            int Get(long i) => _wide ? ((ushort*)_p)[i] : _p[i];
        }

        static void EncodeScan(Output o, JlsCoder coder, Source src, int[] comps, int h, int w, int ilv, int restart)
        {
            int ns = comps.Length;
            int stride = w + 2;
            var buf = new int[ns * (2 * stride + w)];
            fixed (int* b = buf)
            {
                int** prevs = stackalloc int*[ns];
                int** curs = stackalloc int*[ns];
                int** srcs = stackalloc int*[ns];
                int* runIndex = stackalloc int[ns];
                for (int i = 0; i < ns; i++) { runIndex[i] = 0; srcs[i] = b + i * (2 * stride + w) + 2 * stride; }
                int cur = 0;
                long perLine = (long)ns * w * (coder.Limit + 1) / 7 + 64;
                o.Ensure(perLine);
                var wr = new JlsBitWriter(o.Base + o.Len);
                int interval = 0, marker = 0;
                for (int y = 0; y < h; y++)
                {
                    if (restart > 0 && interval == restart)
                    {
                        byte* e = wr.Finish();
                        o.Len = (int)(e - o.Base);
                        o.Marker(0xD0 + marker);
                        marker = (marker + 1) & 7;
                        coder.ResetContexts();
                        for (int i = 0; i < ns; i++) runIndex[i] = 0;
                        new Span<int>(b, ns * (2 * stride + w)).Clear();
                        interval = 0;
                        o.Ensure(perLine);
                        wr = new JlsBitWriter(o.Base + o.Len);
                    }
                    interval++;
                    // keep room for the worst case of one line
                    long used = wr.Position - o.Base;
                    if (used + perLine > o.Buf.Length)
                    {
                        o.Len = (int)used;   // pending bits stay in the writer
                        byte* oldBase = o.Base;
                        o.Ensure(perLine);
                        wr.Rebase(o.Base - oldBase);
                    }
                    for (int i = 0; i < ns; i++)
                    {
                        int* lb = b + i * (2 * stride + w);
                        prevs[i] = lb + (cur ^ 1) * stride + 1;
                        curs[i] = lb + cur * stride + 1;
                        prevs[i][w] = prevs[i][w - 1];
                        curs[i][-1] = prevs[i][0];
                        src.Line(y, comps[i], srcs[i]);
                    }
                    if (ilv == 2) coder.EncodeLineSample(ref wr, srcs, prevs, curs, ns, w, ref runIndex[0]);
                    else
                        for (int i = 0; i < ns; i++) coder.EncodeLine(ref wr, srcs[i], prevs[i], curs[i], w, ref runIndex[i]);
                    cur ^= 1;
                }
                byte* end = wr.Finish();
                o.Len = (int)(end - o.Base);
            }
        }
    }

    /// <summary>
    /// The colour transforms of HP's JPEG-LS implementation, signalled by an APP8 "mrfx" segment (also written by CharLS):
    /// reversible integer transforms of R, G, B modulo 2^bits.
    /// </summary>
    internal static class JpegLsColor
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Forward(JpegLsColorTransform tf, int r, int g, int b, int c, int half, int mask)
        {
            switch (tf)
            {
                case JpegLsColorTransform.Hp1:
                    return c switch { 0 => (r - g + half) & mask, 1 => g, _ => (b - g + half) & mask };
                case JpegLsColorTransform.Hp2:
                    return c switch { 0 => (r - g + half) & mask, 1 => g, _ => (b - ((r + g) >> 1) + half) & mask };
                default:
                    {
                        int r1 = (r - g + half) & mask, b1 = (b - g + half) & mask;
                        // stored as G', B', R'
                        return c switch { 0 => (g + ((r1 + b1) >> 2) - (half >> 1)) & mask, 1 => b1, _ => r1 };
                    }
            }
        }

        /// <summary>Inverts the transform in place on the first three channels of interleaved samples.</summary>
        public static void Inverse<T>(T[] data, int nc, int bits, JpegLsColorTransform tf) where T : unmanaged
        {
            int range = 1 << bits, mask = range - 1, half = range >> 1;
            var span = data.AsSpan();
            if (typeof(T) == typeof(byte))
            {
                var d = MemoryMarshal.Cast<T, byte>(span);
                for (int i = 0; i + 2 < d.Length; i += nc)
                {
                    Inv(tf, d[i], d[i + 1], d[i + 2], half, mask, out int r, out int g, out int b);
                    d[i] = (byte)r; d[i + 1] = (byte)g; d[i + 2] = (byte)b;
                }
            }
            else
            {
                var d = MemoryMarshal.Cast<T, ushort>(span);
                for (int i = 0; i + 2 < d.Length; i += nc)
                {
                    Inv(tf, d[i], d[i + 1], d[i + 2], half, mask, out int r, out int g, out int b);
                    d[i] = (ushort)r; d[i + 1] = (ushort)g; d[i + 2] = (ushort)b;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Inv(JpegLsColorTransform tf, int c0, int c1, int c2, int half, int mask, out int r, out int g, out int b)
        {
            switch (tf)
            {
                case JpegLsColorTransform.Hp1:
                    g = c1; r = (c0 + g - half) & mask; b = (c2 + g - half) & mask; return;
                case JpegLsColorTransform.Hp2:
                    g = c1; r = (c0 + g - half) & mask; b = (c2 + ((r + g) >> 1) - half) & mask; return;
                default:
                    g = (c0 - ((c1 + c2) >> 2) + (half >> 1)) & mask;
                    r = (c2 + g - half) & mask; b = (c1 + g - half) & mask; return;
            }
        }
    }
}
