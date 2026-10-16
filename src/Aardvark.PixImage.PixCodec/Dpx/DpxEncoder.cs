using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Aardvark.ImageCodecs.DpxImpl
{
    /// <summary>Big / little endian field writer on a header buffer.</summary>
    internal readonly struct FieldWriter
    {
        readonly byte[] _b;
        readonly bool _be;
        public FieldWriter(byte[] b, bool be) { _b = b; _be = be; }
        public void U8(int o, byte v) => _b[o] = v;
        public void U16(int o, ushort v) { if (_be) BinaryPrimitives.WriteUInt16BigEndian(_b.AsSpan(o), v); else BinaryPrimitives.WriteUInt16LittleEndian(_b.AsSpan(o), v); }
        public void U32(int o, uint v) { if (_be) BinaryPrimitives.WriteUInt32BigEndian(_b.AsSpan(o), v); else BinaryPrimitives.WriteUInt32LittleEndian(_b.AsSpan(o), v); }
        public void F32(int o, float v) => U32(o, BitConverter.SingleToUInt32Bits(v));
        public void OptU32(int o, uint? v) { if (v is uint x) U32(o, x); }
        public void OptF32(int o, float? v) { if (v is float x) F32(o, x); }
        public void OptU8(int o, byte? v) { if (v is byte x) U8(o, x); }
        /// <summary>A NUL padded Latin-1 string field (an empty string is the undefined value, all NUL).</summary>
        public void Str(int o, int len, string? s)
        {
            var f = _b.AsSpan(o, len);
            f.Clear();
            if (string.IsNullOrEmpty(s)) return;
            var bytes = Encoding.Latin1.GetBytes(s);
            bytes.AsSpan(0, Math.Min(len, bytes.Length)).CopyTo(f);
        }
    }

    internal static class DpxEncoder
    {
        const int DataOffset = 8192;

        public static void Encode(RawImage image, Stream output, DpxEncodeOptions o)
        {
            var img = image;
            if (img.SampleType == SampleType.Float16) img = ToFloat(img);
            if (img.Color == ColorModel.GrayAlpha) img = GrayAlphaToRgba(img);
            var type = img.SampleType;
            if (type is not (SampleType.UInt8 or SampleType.UInt16 or SampleType.Float32 or SampleType.Float64))
                throw new ArgumentException($"DPX: cannot write {type} samples (UInt8, UInt16, Float16, Float32 and Float64 are supported)");
            int ch = img.Channels;
            DpxDescriptor desc = img.Color switch
            {
                ColorModel.Gray => o.Descriptor ?? DpxDescriptor.Luma,
                ColorModel.Rgb => DpxDescriptor.Rgb,
                ColorModel.Rgba => DpxDescriptor.Rgba,
                _ => ch == 1 ? o.Descriptor ?? DpxDescriptor.UserDefined : ch is >= 2 and <= 8 ? (DpxDescriptor)(148 + ch)
                    : throw new ArgumentException($"DPX: cannot write {ch} channels (at most 8)"),
            };
            if (ch == 1 && desc is not (DpxDescriptor.Luma or DpxDescriptor.Red or DpxDescriptor.Green or DpxDescriptor.Blue
                or DpxDescriptor.Alpha or DpxDescriptor.Depth or DpxDescriptor.UserDefined))
                throw new ArgumentException($"DPX: {desc} is not a descriptor for one channel");
            int bits = o.BitDepth ?? type switch { SampleType.UInt8 => 8, SampleType.UInt16 => 16, SampleType.Float32 => 32, _ => 64 };
            bool isFloat = type is SampleType.Float32 or SampleType.Float64;
            if (bits is not (8 or 10 or 12 or 16 or 32 or 64)) throw new ArgumentException($"DPX: cannot write {bits} bit components (8, 10, 12, 16, 32, 64)");
            if (isFloat != bits >= 32)
                throw new ArgumentException(isFloat ? $"DPX: float samples are written as 32 or 64 bit, not {bits}" : $"DPX: {bits} bit components need float samples");
            if (bits is 10 or 12 && o.Packing is not (DpxPacking.Packed or DpxPacking.FilledA or DpxPacking.FilledB))
                throw new ArgumentException($"DPX: unknown packing {o.Packing}");
            var packing = bits is 10 or 12 ? o.Packing : DpxPacking.Packed;
            bool be = o.BigEndian;
            int w = img.Width, h = img.Height;
            long n = (long)w * ch;
            if (n > int.MaxValue / 8) throw new ArgumentException("DPX: lines too long");

            // line layout
            long natural = bits switch
            {
                8 => n,
                10 when packing == DpxPacking.Packed => (n * 10 + 31) / 32 * 4,
                10 => (n + 2) / 3 * 4,
                12 when packing == DpxPacking.Packed => (n * 12 + 31) / 32 * 4,
                12 or 16 => 2 * n,
                32 => 4 * n,
                _ => 8 * n,
            };
            long eol = bits == 8 ? ((natural + 3) & ~3L) - natural : 0;
            long stride = natural + eol;
            long dataSize = stride * h;
            if (DataOffset + dataSize > uint.MaxValue) throw new ArgumentException("DPX: the image is too large for a DPX file (4 GB)");

            // transfer / colorimetric
            var md = img.Metadata;
            var tf = md?.Transfer ?? TransferFunction.Unknown;
            if (tf == TransferFunction.Unknown && isFloat) tf = TransferFunction.Linear;
            float? tvGamma = null;
            DpxTransfer transfer = tf switch
            {
                TransferFunction.Linear => DpxTransfer.Linear,
                TransferFunction.Gamma when md!.Gamma > 0 => DpxTransfer.UnspecifiedVideo,
                _ => DpxTransfer.UserDefined,
            };
            if (transfer == DpxTransfer.UnspecifiedVideo) tvGamma = (float)(1 / md!.Gamma);
            if (o.Transfer is DpxTransfer t) { transfer = t; if (t != DpxTransfer.UnspecifiedVideo) tvGamma = null; }
            var colorimetric = o.Colorimetric ?? transfer switch
            {
                DpxTransfer.Linear => DpxColorimetric.NotApplicable,
                DpxTransfer.PrintingDensity => DpxColorimetric.PrintingDensity,
                DpxTransfer.UnspecifiedVideo or DpxTransfer.Smpte274M or DpxTransfer.ItuR709 or DpxTransfer.ItuR601BG or DpxTransfer.ItuR601M
                    or DpxTransfer.CompositeNtsc or DpxTransfer.CompositePal => (DpxColorimetric)(byte)transfer,
                _ => DpxColorimetric.UserDefined,
            };

            var hd = o.Header;
            byte[]? userData = null;
            int userSize = 0;
            if (hd != null && (hd.UserData is { Length: > 0 } || hd.UserId.Length > 0))
            {
                if ((hd.UserData?.Length ?? 0) > (1 << 20) - 32) throw new ArgumentException("DPX: user data is limited to 1 MB");
                userSize = 32 + (hd.UserData?.Length ?? 0);
                userData = new byte[userSize];
                new FieldWriter(userData, be).Str(0, 32, hd.UserId);
                hd.UserData?.CopyTo(userData, 32);
            }
            int dataOffset = Math.Max(DataOffset, (2048 + userSize + 8191) & ~8191);
            var head = new byte[dataOffset];
            head.AsSpan(0, 2048).Fill(0xFF);
            var f = new FieldWriter(head, be);
            // file information
            head[0] = (byte)(be ? 'S' : 'X'); head[1] = (byte)(be ? 'D' : 'P'); head[2] = (byte)(be ? 'P' : 'D'); head[3] = (byte)(be ? 'X' : 'S');
            f.U32(4, (uint)dataOffset);
            f.Str(8, 8, "V2.0");
            f.U32(16, (uint)(dataOffset + dataSize));
            f.U32(20, 1);
            f.U32(24, 1664);
            f.U32(28, 384);
            f.U32(32, (uint)userSize);
            f.Str(36, 100, hd?.ImageFileName);
            f.Str(136, 24, hd?.CreationDateTime);
            f.Str(160, 100, hd != null ? hd.Creator : "Aardvark.PixImage.PixCodec");
            f.Str(260, 200, hd?.Project);
            f.Str(460, 200, hd?.Copyright);
            f.U32(660, hd?.EncryptionKey ?? 0xFFFFFFFF);
            // image information
            f.U16(768, 0);
            f.U16(770, 1);
            f.U32(772, (uint)w);
            f.U32(776, (uint)h);
            f.U32(780, 0);
            f.U32(784, 0);
            if (!isFloat) f.U32(792, (uint)((1 << bits) - 1));
            f.U8(800, (byte)desc);
            f.U8(801, (byte)transfer);
            f.U8(802, (byte)colorimetric);
            f.U8(803, (byte)bits);
            f.U16(804, (ushort)packing);
            f.U16(806, 0);
            f.U32(808, (uint)dataOffset);
            f.U32(812, (uint)eol);
            f.U32(816, 0);
            f.Str(820, 32, "");
            // image source information
            if (hd != null)
            {
                f.OptU32(1408, hd.XOffset); f.OptU32(1412, hd.YOffset);
                f.OptF32(1416, hd.XCenter); f.OptF32(1420, hd.YCenter);
                f.OptU32(1424, hd.XOriginalSize); f.OptU32(1428, hd.YOriginalSize);
                f.Str(1432, 100, hd.SourceFileName);
                f.Str(1532, 24, hd.SourceDateTime);
                f.Str(1556, 32, hd.InputDevice);
                f.Str(1588, 32, hd.InputDeviceSerial);
                if (hd.Border is var (bl, br, bt, bb)) { f.U16(1620, bl); f.U16(1622, br); f.U16(1624, bt); f.U16(1626, bb); }
                if (hd.PixelAspectRatio is var (ph, pv)) { f.U32(1628, ph); f.U32(1632, pv); }
                f.OptF32(1636, hd.XScannedSize); f.OptF32(1640, hd.YScannedSize);
                // film
                f.Str(1664, 2, hd.FilmManufacturerId); f.Str(1666, 2, hd.FilmType); f.Str(1668, 2, hd.PerfsOffset);
                f.Str(1670, 6, hd.FilmPrefix); f.Str(1676, 4, hd.FilmCount); f.Str(1680, 32, hd.FilmFormat);
                f.OptU32(1712, hd.FramePosition); f.OptU32(1716, hd.SequenceLength); f.OptU32(1720, hd.HeldCount);
                f.OptF32(1724, hd.FrameRate); f.OptF32(1728, hd.ShutterAngle);
                f.Str(1732, 32, hd.FrameId); f.Str(1764, 100, hd.SlateInfo);
                // television
                f.OptU32(1920, hd.TimeCode); f.OptU32(1924, hd.UserBits);
                f.OptU8(1928, hd.Interlace); f.OptU8(1929, hd.FieldNumber); f.OptU8(1930, hd.VideoSignalStandard);
                f.U8(1931, 0);
                f.OptF32(1932, hd.HorizontalSamplingRate); f.OptF32(1936, hd.VerticalSamplingRate);
                f.OptF32(1940, hd.TemporalSamplingRate); f.OptF32(1944, hd.TimeOffset);
                f.OptF32(1948, hd.Gamma); f.OptF32(1952, hd.BlackLevel); f.OptF32(1956, hd.BlackGain);
                f.OptF32(1960, hd.BreakPoint); f.OptF32(1964, hd.WhiteLevel); f.OptF32(1968, hd.IntegrationTime);
            }
            else
            {
                foreach (int s in new[] { 1432, 1532, 1556, 1588 }) f.Str(s, s == 1532 ? 24 : s == 1432 ? 100 : 32, "");
                foreach (var (s, l) in new[] { (1664, 2), (1666, 2), (1668, 2), (1670, 6), (1676, 4), (1680, 32), (1732, 32), (1764, 100) }) f.Str(s, l, "");
                f.U8(1931, 0);
            }
            if (tvGamma is float g) f.F32(1948, g);
            head.AsSpan(1356, 52).Clear();   // reserved areas: zero
            head.AsSpan(664, 104).Clear();
            if (userData != null) userData.CopyTo(head, 2048);
            output.Write(head);

            // pixel data: bands of lines, on several threads for large images
            int max = (1 << Math.Min(bits, 16)) - 1;
            void Rows(int y0, int y1, Span<byte> dst)
            {
                var vals = isFloat ? null : new ushort[n];
                for (int y = y0; y < y1; y++)
                {
                    var row = dst.Slice((int)((y - y0) * stride), (int)stride);
                    if (isFloat) FloatRow(img, y, (int)n, bits, be, row);
                    else
                    {
                        Values(img, y, (int)n, bits, max, vals!);
                        Pack(vals!, bits, packing, be, ch == 1, row);
                    }
                }
            }
            int bandRows = (int)Math.Clamp((1 << 22) / Math.Max(stride, 1), 1, h);   // about 4 MB per band
            int bands = (h + bandRows - 1) / bandRows;
            var bufs = new byte[Math.Min(bands, Math.Max(1, Environment.ProcessorCount))][];
            for (int b0 = 0; b0 < bands; b0 += bufs.Length)
            {
                int count = Math.Min(bufs.Length, bands - b0);
                void Band(int k)
                {
                    int y0 = (b0 + k) * bandRows, y1 = Math.Min(h, y0 + bandRows);
                    var buf = bufs[k] ??= new byte[bandRows * stride];
                    buf.AsSpan(0, (int)((y1 - y0) * stride)).Clear();
                    Rows(y0, y1, buf);
                }
                if (count > 1 && !OperatingSystem.IsBrowser()) System.Threading.Tasks.Parallel.For(0, count, Band);
                else for (int k = 0; k < count; k++) Band(k);
                for (int k = 0; k < count; k++)
                {
                    int y0 = (b0 + k) * bandRows, y1 = Math.Min(h, y0 + bandRows);
                    output.Write(bufs[k], 0, (int)((y1 - y0) * stride));
                }
            }
        }

        static RawImage ToFloat(RawImage img)
        {
            var s = img.GetData<Half>();
            var d = new float[s.Length];
            for (int i = 0; i < s.Length; i++) d[i] = (float)s[i];
            return new RawImage(img.Width, img.Height, img.Color, d, img.Channels, img.Metadata);
        }

        static RawImage GrayAlphaToRgba(RawImage img)
        {
            var r = RawImage.Create(img.Width, img.Height, ColorModel.Rgba, img.SampleType);
            int bps = RawImage.BytesPerSample(img.SampleType);
            var s = img.GetBytes();
            var d = r.GetBytes();
            for (long i = 0, px = (long)img.Width * img.Height; i < px; i++)
            {
                var g = s.Slice((int)(i * 2 * bps), bps);
                for (int c = 0; c < 3; c++) g.CopyTo(d.Slice((int)((i * 4 + c) * bps)));
                s.Slice((int)((i * 2 + 1) * bps), bps).CopyTo(d.Slice((int)((i * 4 + 3) * bps)));
            }
            r.Metadata = img.Metadata;
            return r;
        }

        /// <summary>The integer values of row y at the target depth: UInt8 scaled up, UInt16 as they are (checked to fit).</summary>
        static void Values(RawImage img, int y, int n, int bits, int max, ushort[] v)
        {
            if (img.SampleType == SampleType.UInt8)
            {
                var s = img.GetData<byte>().AsSpan(y * n, n);
                if (bits == 8) for (int i = 0; i < n; i++) v[i] = s[i];
                else if (bits == 16) for (int i = 0; i < n; i++) v[i] = (ushort)(s[i] * 257);
                else for (int i = 0; i < n; i++) v[i] = (ushort)((s[i] * max + 127) / 255);
            }
            else
            {
                var s = img.GetData<ushort>().AsSpan(y * n, n);
                s.CopyTo(v);
                if (bits < 16)
                    for (int i = 0; i < n; i++)
                        if (s[i] > max) throw new ArgumentException($"DPX: sample value {s[i]} does not fit {bits} bits (UInt16 samples are written as they are)");
            }
        }

        static void Pack(ushort[] v, int bits, DpxPacking packing, bool be, bool lowFirst, Span<byte> row)
        {
            int n = v.Length;
            switch (bits)
            {
                case 8:
                    for (int i = 0; i < n; i++) row[i] = (byte)v[i];
                    return;
                case 16:
                case 12 when packing != DpxPacking.Packed:
                    {
                        int sh = bits == 12 && packing == DpxPacking.FilledA ? 4 : 0;
                        for (int i = 0; i < n; i++)
                        {
                            ushort u = (ushort)(v[i] << sh);
                            if (be) BinaryPrimitives.WriteUInt16BigEndian(row.Slice(2 * i), u); else BinaryPrimitives.WriteUInt16LittleEndian(row.Slice(2 * i), u);
                        }
                        return;
                    }
                case 10 when packing != DpxPacking.Packed:
                    {
                        // one component elements: first datum in the low bits (see DpxDecoder.Unpack)
                        int t0 = packing == DpxPacking.FilledA ? 22 : 20;
                        int s0 = lowFirst ? t0 - 20 : t0, s2 = lowFirst ? t0 : t0 - 20;
                        for (int i = 0, k = 0; i < n; i += 3, k += 4)
                        {
                            uint w = (uint)v[i] << s0;
                            if (i + 1 < n) w |= (uint)v[i + 1] << (t0 - 10);
                            if (i + 2 < n) w |= (uint)v[i + 2] << s2;
                            if (be) BinaryPrimitives.WriteUInt32BigEndian(row.Slice(k), w); else BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(k), w);
                        }
                        return;
                    }
                default:
                    {
                        // packed: LSB first bit stream through 32-bit words
                        ulong acc = 0;
                        int have = 0, k = 0;
                        for (int i = 0; i < n; i++)
                        {
                            acc |= (ulong)v[i] << have;
                            have += bits;
                            if (have >= 32)
                            {
                                Put((uint)acc, be, row, k); k += 4;
                                acc >>= 32; have -= 32;
                            }
                        }
                        if (have > 0) Put((uint)acc, be, row, k);
                        return;
                    }
            }
        }

        static void Put(uint w, bool be, Span<byte> row, int k)
        {
            if (be) BinaryPrimitives.WriteUInt32BigEndian(row.Slice(k), w); else BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(k), w);
        }

        static void FloatRow(RawImage img, int y, int n, int bits, bool be, Span<byte> row)
        {
            if (img.SampleType == SampleType.Float32)
            {
                var s = img.GetData<float>().AsSpan(y * n, n);
                if (bits == 32)
                    for (int i = 0; i < n; i++) Put(BitConverter.SingleToUInt32Bits(s[i]), be, row, 4 * i);
                else
                    for (int i = 0; i < n; i++) Put64(BitConverter.DoubleToUInt64Bits(s[i]), be, row, 8 * i);
            }
            else
            {
                var s = img.GetData<double>().AsSpan(y * n, n);
                if (bits == 32)
                    for (int i = 0; i < n; i++) Put(BitConverter.SingleToUInt32Bits((float)s[i]), be, row, 4 * i);
                else
                    for (int i = 0; i < n; i++) Put64(BitConverter.DoubleToUInt64Bits(s[i]), be, row, 8 * i);
            }
        }

        static void Put64(ulong w, bool be, Span<byte> row, int k)
        {
            if (be) BinaryPrimitives.WriteUInt64BigEndian(row.Slice(k), w); else BinaryPrimitives.WriteUInt64LittleEndian(row.Slice(k), w);
        }
    }
}
