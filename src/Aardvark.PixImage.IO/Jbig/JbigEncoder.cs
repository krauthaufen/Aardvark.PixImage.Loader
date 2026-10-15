using System;
using System.IO;
using Aardvark.ImageCodecs.JpegImpl;

namespace Aardvark.ImageCodecs.JbigImpl
{
    /// <summary>
    /// JBIG1 encoder for a single resolution layer (sequential mode): three or two line template, lowest layer typical
    /// prediction, any stripe height, one or more bit planes.
    /// </summary>
    internal static unsafe class JbigEncoder
    {
        public static void Encode(RawImage image, Stream output, JbigEncodeOptions o)
        {
            ArgumentNullException.ThrowIfNull(image);
            ArgumentNullException.ThrowIfNull(output);
            if (image.SampleType != SampleType.UInt8 || (image.Color != ColorModel.Gray && image.Color != ColorModel.GrayAlpha))
                throw new ImageNotSupportedException("JBIG encoding needs a UInt8 gray image");
            if (o.BitPlanes < 1 || o.BitPlanes > 8) throw new ArgumentException("BitPlanes must be 1..8");
            int w = image.Width, h = image.Height, ch = image.Channels, planes = o.BitPlanes;
            var src = image.GetData<byte>();
            var bits = new byte[planes][];
            for (int k = 0; k < planes; k++) bits[k] = new byte[checked(w * h)];
            for (int i = 0, n = w * h; i < n; i++)
            {
                int v = src[i * ch];
                if (planes == 1) { bits[0][i] = (byte)(v < 128 ? 1 : 0); continue; }
                v >>= 8 - planes;
                if (!o.BinaryPlanes) v ^= v >> 1; // Gray code
                for (int k = 0; k < planes; k++) bits[k][i] = (byte)(v >> (planes - 1 - k) & 1);
            }
            EncodePlanes(bits, w, h, o, output);
        }

        /// <summary>planes[k] holds one byte (0 or 1) per pixel, most significant plane first.</summary>
        public static void EncodePlanes(byte[][] planes, int w, int h, JbigEncodeOptions o, Stream output)
        {
            int l0 = o.LinesPerStripe <= 0 ? h : Math.Min(o.LinesPerStripe, h);
            bool twoLine = o.TwoLineTemplate, tp = o.TypicalPrediction;
            Span<byte> header = stackalloc byte[JbigHeader.Size];
            header.Clear();
            header[2] = (byte)planes.Length;
            for (int i = 0; i < 4; i++)
            {
                header[4 + i] = (byte)(w >> (24 - 8 * i));
                header[8 + i] = (byte)(h >> (24 - 8 * i));
                header[12 + i] = (byte)(l0 >> (24 - 8 * i));
            }
            header[18] = 0x03; // layer, stripe, plane loops (ILEAVE | SMID)
            header[19] = (byte)((twoLine ? 0x40 : 0) | (tp ? 0x08 : 0));
            output.Write(header);

            const int pad = 4;
            int stride = w + 2 * pad;
            var coder = new JpegArithEncoder();
            var stats = new byte[planes.Length][];
            var lntp = new int[planes.Length];
            for (int k = 0; k < planes.Length; k++) { stats[k] = new byte[1024]; lntp[k] = 1; }
            // three padded working lines per plane are enough: the template never reaches further up
            var lines = new byte[3 * stride];
            Span<byte> terminator = [0xFF, 0x02];

            for (int y0 = 0; y0 < h; y0 += l0)
            {
                int y1 = Math.Min(h, y0 + l0);
                for (int k = 0; k < planes.Length; k++)
                {
                    coder.Start();
                    fixed (byte* st = stats[k], buf = lines, plane = planes[k])
                    {
                        byte* row = buf + pad, up1 = buf + stride + pad, up2 = buf + 2 * stride + pad;
                        // lines above the stripe are real image lines (the state is carried over), above the image background
                        LoadLine(plane, w, y0 - 1, up1);
                        LoadLine(plane, w, y0 - 2, up2);
                        for (int y = y0; y < y1; y++)
                        {
                            LoadLine(plane, w, y, row);
                            bool typical = false;
                            if (tp)
                            {
                                typical = new ReadOnlySpan<byte>(row, w).SequenceEqual(new ReadOnlySpan<byte>(up1, w));
                                int now = typical ? 0 : 1;
                                coder.Encode(st + (twoLine ? 0x195 : 0x0E5), now == lntp[k] ? 1 : 0);
                                lntp[k] = now;
                            }
                            if (!typical)
                            {
                                if (twoLine)
                                {
                                    for (int x = 0; x < w; x++)
                                    {
                                        int cx = up1[x - 3] << 9 | up1[x - 2] << 8 | up1[x - 1] << 7 | up1[x] << 6 | up1[x + 1] << 5 | up1[x + 2] << 4
                                            | row[x - 4] << 3 | row[x - 3] << 2 | row[x - 2] << 1 | row[x - 1];
                                        coder.Encode(st + cx, row[x]);
                                    }
                                }
                                else
                                {
                                    for (int x = 0; x < w; x++)
                                    {
                                        int cx = up2[x - 1] << 9 | up2[x] << 8 | up2[x + 1] << 7 | up1[x - 2] << 6 | up1[x - 1] << 5 | up1[x] << 4 | up1[x + 1] << 3
                                            | up1[x + 2] << 2 | row[x - 2] << 1 | row[x - 1];
                                        coder.Encode(st + cx, row[x]);
                                    }
                                }
                            }
                            // rotate the three lines
                            byte* t = up2; up2 = up1; up1 = row; row = t;
                        }
                    }
                    coder.Finish();
                    output.Write(coder.Buffer, 0, coder.Length);
                    output.Write(terminator);
                }
            }
        }

        static void LoadLine(byte* plane, int w, int y, byte* dst)
        {
            if (y < 0) new Span<byte>(dst, w).Clear();
            else new ReadOnlySpan<byte>(plane + (nint)y * w, w).CopyTo(new Span<byte>(dst, w));
        }
    }
}
