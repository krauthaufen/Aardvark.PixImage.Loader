using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace Aardvark.ImageCodecs.DpxImpl
{
    /// <summary>Little / big endian field access on a DPX or Cineon header.</summary>
    internal readonly ref struct Fields
    {
        readonly ReadOnlySpan<byte> _d;
        public readonly bool Be;
        public Fields(ReadOnlySpan<byte> d, bool be) { _d = d; Be = be; }
        public int Length => _d.Length;
        public byte U8(int o) => _d[o];
        public ushort U16(int o) => Be ? BinaryPrimitives.ReadUInt16BigEndian(_d.Slice(o)) : BinaryPrimitives.ReadUInt16LittleEndian(_d.Slice(o));
        public uint U32(int o) => Be ? BinaryPrimitives.ReadUInt32BigEndian(_d.Slice(o)) : BinaryPrimitives.ReadUInt32LittleEndian(_d.Slice(o));
        public uint? OptU32(int o) { uint v = U32(o); return v == 0xFFFFFFFF ? null : v; }
        public ushort? OptU16(int o) { ushort v = U16(o); return v == 0xFFFF ? null : v; }
        public byte? OptU8(int o) { byte v = _d[o]; return v == 0xFF ? null : v; }
        /// <summary>A float field; null for the undefined patterns (all ones, NaN, +/- infinity as Cineon uses).</summary>
        public float? OptF32(int o)
        {
            uint bits = U32(o);
            float f = BitConverter.UInt32BitsToSingle(bits);
            return bits == 0xFFFFFFFF || float.IsNaN(f) || float.IsInfinity(f) ? null : f;
        }
        /// <summary>A NUL terminated Latin-1 string; empty when undefined (first byte NUL or FF).</summary>
        public string Str(int o, int len)
        {
            var s = _d.Slice(o, len);
            if (s.Length == 0 || s[0] == 0xFF) return "";
            int n = s.IndexOf((byte)0);
            if (n >= 0) s = s.Slice(0, n);
            return Encoding.Latin1.GetString(s).TrimEnd();
        }
    }

    /// <summary>How the components of a DPX image element map to the decoded channels.</summary>
    internal enum DpxKind : byte { Direct, Abgr, Ycc422, Ycca422, Ycc444, Ycca444 }

    /// <summary>The geometry and sample layout of one image element.</summary>
    internal sealed class DpxElementLayout
    {
        public DpxImageElement E = null!;
        public int Width, Height;
        /// <summary>Components per packing unit, and pixels per unit (2 for the 4:2:2 descriptors).</summary>
        public int Comps, UnitPixels;
        public int Units => (Width + UnitPixels - 1) / UnitPixels;
        public long DatumsPerLine => (long)Units * Comps;
        public DpxKind Kind;
        public ColorModel Color;
        public int Channels;
        public SampleType Type;
        public string? Unsupported;
    }

    internal static class DpxDecoder
    {
        public const int GenericSize = 1664, HeaderSize = 2048;

        public static bool IsMatch(ReadOnlySpan<byte> h)
            => h.Length >= 4 && (h[0] == 'S' && h[1] == 'D' && h[2] == 'P' && h[3] == 'X' || h[0] == 'X' && h[1] == 'P' && h[2] == 'D' && h[3] == 'S');

        // ------------------------------------------------------------------ header

        public static DpxHeader ReadHeader(ReadOnlySpan<byte> d)
        {
            if (!IsMatch(d)) throw new ImageFormatException("DPX: not a DPX file");
            if (d.Length < GenericSize) throw new ImageFormatException("DPX: truncated header");
            var f = new Fields(d, d[0] == 'S');
            int n = f.U16(770);
            if (n < 1 || n > 8) throw new ImageFormatException($"DPX: bad number of image elements {n}");
            uint w = f.U32(772), h = f.U32(776);
            if (w == 0 || h == 0 || w > int.MaxValue || h > int.MaxValue) throw new ImageFormatException($"DPX: bad dimensions {w} x {h}");
            var elems = new DpxImageElement[n];
            for (int i = 0; i < n; i++)
            {
                int o = 780 + 72 * i;
                elems[i] = new DpxImageElement
                {
                    Index = i,
                    Signed = f.U32(o) == 1,
                    ReferenceLowCode = f.OptU32(o + 4),
                    ReferenceLowQuantity = f.OptF32(o + 8),
                    ReferenceHighCode = f.OptU32(o + 12),
                    ReferenceHighQuantity = f.OptF32(o + 16),
                    Descriptor = (DpxDescriptor)f.U8(o + 20),
                    Transfer = (DpxTransfer)f.U8(o + 21),
                    Colorimetric = (DpxColorimetric)f.U8(o + 22),
                    BitDepth = f.U8(o + 23),
                    Packing = (DpxPacking)(f.U16(o + 24) == 0xFFFF ? 0 : f.U16(o + 24)),
                    Encoding = f.U16(o + 26) == 0xFFFF ? 0 : f.U16(o + 26),
                    DataOffset = f.OptU32(o + 28),
                    EndOfLinePadding = f.OptU32(o + 32),
                    EndOfImagePadding = f.OptU32(o + 36),
                    Description = f.Str(o + 40, 32),
                };
            }
            bool ind = d.Length >= HeaderSize;
            ushort? b0 = f.OptU16(1620), b1 = f.OptU16(1622), b2 = f.OptU16(1624), b3 = f.OptU16(1626);
            uint? ah = f.OptU32(1628), av = f.OptU32(1632);
            uint? userSize = f.OptU32(32);
            string userId = "";
            byte[]? userData = null;
            if (userSize is uint us && us > 0 && d.Length >= HeaderSize + 32 && us <= (1u << 21))
            {
                userId = f.Str(HeaderSize, 32);
                long end = Math.Min((long)HeaderSize + us, d.Length);
                if (end > HeaderSize + 32) userData = d.Slice(HeaderSize + 32, (int)(end - HeaderSize - 32)).ToArray();
            }
            ushort orient = f.U16(768);
            return new DpxHeader
            {
                BigEndian = f.Be,
                ImageDataOffset = f.U32(4),
                Version = f.Str(8, 8),
                FileSize = f.OptU32(16),
                DittoKey = f.OptU32(20),
                GenericHeaderSize = f.OptU32(24),
                IndustryHeaderSize = f.OptU32(28),
                UserDataSize = userSize,
                ImageFileName = f.Str(36, 100),
                CreationDateTime = f.Str(136, 24),
                Creator = f.Str(160, 100),
                Project = f.Str(260, 200),
                Copyright = f.Str(460, 200),
                EncryptionKey = f.OptU32(660),
                Orientation = orient == 0xFFFF ? 0 : orient,
                PixelsPerLine = (int)w,
                Lines = (int)h,
                Elements = elems,
                XOffset = f.OptU32(1408),
                YOffset = f.OptU32(1412),
                XCenter = f.OptF32(1416),
                YCenter = f.OptF32(1420),
                XOriginalSize = f.OptU32(1424),
                YOriginalSize = f.OptU32(1428),
                SourceFileName = f.Str(1432, 100),
                SourceDateTime = f.Str(1532, 24),
                InputDevice = f.Str(1556, 32),
                InputDeviceSerial = f.Str(1588, 32),
                Border = b0 is ushort x0 && b1 is ushort x1 && b2 is ushort x2 && b3 is ushort x3 ? (x0, x1, x2, x3) : null,
                PixelAspectRatio = ah is uint hh && av is uint vv ? (hh, vv) : null,
                XScannedSize = f.OptF32(1636),
                YScannedSize = f.OptF32(1640),
                FilmManufacturerId = ind ? f.Str(1664, 2) : "",
                FilmType = ind ? f.Str(1666, 2) : "",
                PerfsOffset = ind ? f.Str(1668, 2) : "",
                FilmPrefix = ind ? f.Str(1670, 6) : "",
                FilmCount = ind ? f.Str(1676, 4) : "",
                FilmFormat = ind ? f.Str(1680, 32) : "",
                FramePosition = ind ? f.OptU32(1712) : null,
                SequenceLength = ind ? f.OptU32(1716) : null,
                HeldCount = ind ? f.OptU32(1720) : null,
                FrameRate = ind ? f.OptF32(1724) : null,
                ShutterAngle = ind ? f.OptF32(1728) : null,
                FrameId = ind ? f.Str(1732, 32) : "",
                SlateInfo = ind ? f.Str(1764, 100) : "",
                TimeCode = ind ? f.OptU32(1920) : null,
                UserBits = ind ? f.OptU32(1924) : null,
                Interlace = ind ? f.OptU8(1928) : null,
                FieldNumber = ind ? f.OptU8(1929) : null,
                VideoSignalStandard = ind ? f.OptU8(1930) : null,
                HorizontalSamplingRate = ind ? f.OptF32(1932) : null,
                VerticalSamplingRate = ind ? f.OptF32(1936) : null,
                TemporalSamplingRate = ind ? f.OptF32(1940) : null,
                TimeOffset = ind ? f.OptF32(1944) : null,
                Gamma = ind ? f.OptF32(1948) : null,
                BlackLevel = ind ? f.OptF32(1952) : null,
                BlackGain = ind ? f.OptF32(1956) : null,
                BreakPoint = ind ? f.OptF32(1960) : null,
                WhiteLevel = ind ? f.OptF32(1964) : null,
                IntegrationTime = ind ? f.OptF32(1968) : null,
                UserId = userId,
                UserData = userData,
            };
        }

        // ------------------------------------------------------------------ element layout

        public static DpxElementLayout Layout(DpxHeader h, DpxImageElement e)
        {
            var l = new DpxElementLayout { E = e, Width = h.PixelsPerLine, Height = h.Lines, Comps = 1, UnitPixels = 1, Color = ColorModel.Gray, Channels = 1 };
            switch (e.Descriptor)
            {
                case DpxDescriptor.UserDefined: case DpxDescriptor.Red: case DpxDescriptor.Green: case DpxDescriptor.Blue:
                case DpxDescriptor.Alpha: case DpxDescriptor.Luma: case DpxDescriptor.Depth:
                    break;
                case DpxDescriptor.Rgb: l.Comps = 3; l.Color = ColorModel.Rgb; l.Channels = 3; break;
                case DpxDescriptor.Rgba: l.Comps = 4; l.Color = ColorModel.Rgba; l.Channels = 4; break;
                case DpxDescriptor.Abgr: l.Comps = 4; l.Color = ColorModel.Rgba; l.Channels = 4; l.Kind = DpxKind.Abgr; break;
                case DpxDescriptor.CbYCrY: l.Comps = 4; l.UnitPixels = 2; l.Color = ColorModel.Rgb; l.Channels = 3; l.Kind = DpxKind.Ycc422; break;
                case DpxDescriptor.CbYACrYA: l.Comps = 6; l.UnitPixels = 2; l.Color = ColorModel.Rgba; l.Channels = 4; l.Kind = DpxKind.Ycca422; break;
                case DpxDescriptor.CbYCr: l.Comps = 3; l.Color = ColorModel.Rgb; l.Channels = 3; l.Kind = DpxKind.Ycc444; break;
                case DpxDescriptor.CbYCrA: l.Comps = 4; l.Color = ColorModel.Rgba; l.Channels = 4; l.Kind = DpxKind.Ycca444; break;
                case >= DpxDescriptor.UserDefined2 and <= DpxDescriptor.UserDefined8:
                    l.Comps = l.Channels = (int)e.Descriptor - 148; l.Color = ColorModel.Other; break;
                default:
                    l.Unsupported = $"DPX: image element descriptor {(int)e.Descriptor} ({e.Descriptor}) is not supported";
                    break;
            }
            bool ycc = l.Kind is not (DpxKind.Direct or DpxKind.Abgr);
            l.Type = e.BitDepth switch
            {
                1 => SampleType.UInt8,
                8 => e.Signed && !ycc ? SampleType.Int8 : SampleType.UInt8,
                10 or 12 or 16 => e.Signed && !ycc ? SampleType.Int16 : SampleType.UInt16,
                32 => SampleType.Float32,
                64 => SampleType.Float64,
                _ => SampleType.UInt8,
            };
            if (l.Unsupported != null) return l;
            if (e.BitDepth is not (1 or 8 or 10 or 12 or 16 or 32 or 64)) l.Unsupported = $"DPX: bit depth {e.BitDepth} is not supported";
            else if (ycc && (e.BitDepth is 32 or 64 or 1)) l.Unsupported = $"DPX: {e.BitDepth} bit Y'CbCr elements are not supported";
            else if (ycc && e.Signed) l.Unsupported = "DPX: signed Y'CbCr elements are not supported";
            else if (e.Encoding is not (0 or 1)) l.Unsupported = $"DPX: encoding {e.Encoding} is not supported";
            else if (e.IsRle && e.BitDepth is 32 or 64) l.Unsupported = "DPX: run length encoded float elements are not supported";
            else if (e.BitDepth is 10 or 12 && (int)e.Packing > 2) l.Unsupported = $"DPX: packing {(int)e.Packing} is not supported";
            return l;
        }

        /// <summary>Bytes of one line's datums without padding, as written by the tools that do not pad (OIIO for 8 bit, everyone for 16 bit).</summary>
        static long NaturalLineBytes(DpxImageElement e, long n) => e.BitDepth switch
        {
            1 => (n + 31) / 32 * 4,
            8 => n,
            10 when e.Packing == DpxPacking.Packed => (n * 10 + 31) / 32 * 4,
            10 => (n + 2) / 3 * 4,
            12 when e.Packing == DpxPacking.Packed => (n * 12 + 31) / 32 * 4,
            12 or 16 => n * 2,
            32 => n * 4,
            _ => n * 8,
        };

        /// <summary>Whether a line naturally ends on a 32-bit word boundary for this depth (so there is no padding question).</summary>
        static bool PadAmbiguous(DpxImageElement e) => e.BitDepth is 8 or 16 || e.BitDepth == 12 && e.Packing != DpxPacking.Packed;

        // ------------------------------------------------------------------ element placement

        /// <summary>Data start of every element (-1 when it cannot be located) and the end of the region the element may use.</summary>
        static (long start, long limit) Place(DpxHeader h, int index, int dataLength)
        {
            var els = h.Elements;
            long start = -1;
            long prevEnd = h.ImageDataOffset;
            for (int i = 0; i <= index; i++)
            {
                var e = els[i];
                long s = e.DataOffset is uint o && o > 0 && o < dataLength ? o : i == 0 ? h.ImageDataOffset : prevEnd;
                if (i == index) { start = s; break; }
                if (e.IsRle) { prevEnd = -1; continue; }   // cannot know where an RLE element ends
                if (s < 0) { prevEnd = -1; continue; }
                var l = Layout(h, e);
                long stride = NaturalLineBytes(e, l.DatumsPerLine) + (e.EndOfLinePadding is uint p and < 1u << 20 ? p : 0);
                prevEnd = s + stride * l.Height + (e.EndOfImagePadding is uint q and < 1u << 30 ? q : 0);
            }
            if (start < 0 || start > dataLength) return (-1, 0);
            long limit = dataLength;
            foreach (var e in els)
                if (e.DataOffset is uint o && o > start && o < limit) limit = o;
            return (start, limit);
        }

        // ------------------------------------------------------------------ element choice

        /// <summary>Elements that can be decoded, in header order.</summary>
        public static List<int> Decodable(DpxHeader h)
        {
            var r = new List<int>();
            for (int i = 0; i < h.Elements.Count; i++)
                if (Layout(h, h.Elements[i]).Unsupported == null) r.Add(i);
            return r;
        }

        /// <summary>
        /// What Decode returns without an element index: the first colour element (RGB, RGBA, ABGR, Y'CbCr), else separate R, G and
        /// B elements (and an A element) combined, else the first element that can be decoded. Returns the element indices to
        /// combine (one index for a plain element).
        /// </summary>
        public static int[] Choose(DpxHeader h, int? index)
        {
            if (index is int ix)
            {
                if (ix < 0 || ix >= h.Elements.Count) throw new ArgumentException($"DPX: ElementIndex {ix} does not exist, the file has {h.Elements.Count} image elements", "options");
                var l = Layout(h, h.Elements[ix]);
                if (l.Unsupported != null) throw new ImageNotSupportedException(l.Unsupported);
                return new[] { ix };
            }
            var ok = Decodable(h);
            foreach (int i in ok)
                if (h.Elements[i].Descriptor is DpxDescriptor.Rgb or DpxDescriptor.Rgba or DpxDescriptor.Abgr
                    or >= DpxDescriptor.CbYCrY and <= DpxDescriptor.CbYCrA) return new[] { i };
            int Find(DpxDescriptor d)
            {
                foreach (int i in ok) if (h.Elements[i].Descriptor == d) return i;
                return -1;
            }
            int r = Find(DpxDescriptor.Red), g = Find(DpxDescriptor.Green), b = Find(DpxDescriptor.Blue), a = Find(DpxDescriptor.Alpha);
            if (r >= 0 && g >= 0 && b >= 0)
            {
                var t = Layout(h, h.Elements[r]).Type;
                if (Layout(h, h.Elements[g]).Type == t && Layout(h, h.Elements[b]).Type == t)
                    return a >= 0 && Layout(h, h.Elements[a]).Type == t ? new[] { r, g, b, a } : new[] { r, g, b };
            }
            if (ok.Count > 0) return new[] { ok[0] };
            throw new ImageNotSupportedException(Layout(h, h.Elements[0]).Unsupported!);
        }

        // ------------------------------------------------------------------ colour tags

        /// <summary>DPX orientation code to EXIF orientation.</summary>
        public static int ExifOrientation(int dpx) => dpx switch { 1 => 2, 2 => 4, 3 => 3, 4 => 5, 5 => 6, 6 => 8, 7 => 7, _ => 1 };

        /// <summary>The transfer function of the decoded values of an element, with the power law exponent for Gamma.</summary>
        public static TransferFunction Transfer(DpxHeader h, DpxImageElement e, SampleType type, out double gamma)
        {
            gamma = 0;
            double? tv = h.Gamma is float g && g > 0.1f && g < 10f ? 1.0 / g : null;
            switch (e.Transfer)
            {
                case DpxTransfer.Linear: case DpxTransfer.DepthLinear: return TransferFunction.Linear;
                case DpxTransfer.UnspecifiedVideo:
                    if (tv is double t) { gamma = t; return TransferFunction.Gamma; }
                    return TransferFunction.Unknown;
                case DpxTransfer.Smpte274M: case DpxTransfer.ItuR709: case DpxTransfer.ItuR601BG: case DpxTransfer.ItuR601M: case DpxTransfer.CompositeNtsc:
                    gamma = tv ?? 0.45; return TransferFunction.Gamma;
                case DpxTransfer.CompositePal:
                    gamma = tv ?? 1 / 2.8; return TransferFunction.Gamma;
                case DpxTransfer.UserDefined: case DpxTransfer.Undefined:
                    // float data without a stated characteristic is scene linear in practice
                    return type is SampleType.Float32 or SampleType.Float64 ? TransferFunction.Linear : TransferFunction.Unknown;
                default: return TransferFunction.Unknown;
            }
        }

        // ------------------------------------------------------------------ info

        public static ImageInfo GetInfo(ReadOnlySpan<byte> data, DpxDecodeOptions o)
        {
            var h = ReadHeader(data);
            var pick = Choose(h, o.ElementIndex);
            var e = h.Elements[pick[0]];
            var l = Layout(h, e);
            int ch = pick.Length > 1 ? pick.Length : l.Channels;
            var color = pick.Length > 1 ? (pick.Length == 4 ? ColorModel.Rgba : ColorModel.Rgb) : l.Color;
            int exif = ExifOrientation(h.Orientation);
            bool applied = o.ApplyOrientation && exif >= 2;
            bool swap = applied && exif >= 5;
            var tf = Transfer(h, e, l.Type, out _);
            return new ImageInfo(ImageFormat.Dpx, swap ? h.Lines : h.PixelsPerLine, swap ? h.PixelsPerLine : h.Lines, ch, l.Type, color,
                o.ElementIndex != null ? 1 : Math.Max(1, Decodable(h).Count))
            {
                BitsPerSample = e.BitDepth,
                Orientation = exif,
                OrientationApplied = applied,
                Transfer = tf,
            };
        }

        // ------------------------------------------------------------------ decode

        public static RawImage Decode(ReadOnlySpan<byte> data, DpxDecodeOptions o)
        {
            var h = ReadHeader(data);
            var pick = Choose(h, o.ElementIndex);
            RawImage img;
            if (pick.Length == 1) img = DecodeElement(data, h, pick[0], o);
            else
            {
                var parts = new RawImage[pick.Length];
                for (int i = 0; i < parts.Length; i++) parts[i] = DecodeElement(data, h, pick[i], o);
                img = Interleave(parts);
            }
            return Finish(img, h, h.Elements[pick[0]], o);
        }

        public static RawImage[] DecodeAll(ReadOnlySpan<byte> data, DpxDecodeOptions o)
        {
            var h = ReadHeader(data);
            var ok = Decodable(h);
            if (ok.Count == 0) throw new ImageNotSupportedException(Layout(h, h.Elements[0]).Unsupported!);
            var r = new RawImage[ok.Count];
            for (int i = 0; i < r.Length; i++) r[i] = Finish(DecodeElement(data, h, ok[i], o), h, h.Elements[ok[i]], o);
            return r;
        }

        static RawImage Finish(RawImage img, DpxHeader h, DpxImageElement e, DpxDecodeOptions o)
        {
            var m = img.Metadata ??= new ImageMetadata();
            m.Transfer = Transfer(h, e, img.SampleType, out double g);
            m.Gamma = g;
            m.Orientation = ExifOrientation(h.Orientation);
            return o.ApplyOrientation && m.Orientation >= 2 ? ImageOps.ApplyOrientation(img) : img;
        }

        static RawImage Interleave(RawImage[] parts)
        {
            var p0 = parts[0];
            int n = parts.Length;
            var r = RawImage.Create(p0.Width, p0.Height, n == 4 ? ColorModel.Rgba : ColorModel.Rgb, p0.SampleType, n);
            int bps = RawImage.BytesPerSample(p0.SampleType);
            var dst = r.GetBytes();
            for (int c = 0; c < n; c++)
            {
                var src = parts[c].GetBytes();
                for (long i = 0, px = (long)p0.Width * p0.Height; i < px; i++)
                    src.Slice((int)(i * bps), bps).CopyTo(dst.Slice((int)((i * n + c) * bps), bps));
            }
            return r;
        }

        static RawImage DecodeElement(ReadOnlySpan<byte> data, DpxHeader h, int index, DpxDecodeOptions o)
        {
            var e = h.Elements[index];
            var l = Layout(h, e);
            if (l.Unsupported != null) throw new ImageNotSupportedException(l.Unsupported);
            if (e.Packing != DpxPacking.Packed && e.BitDepth is 10 or 12 && e.Packing is not (DpxPacking.FilledA or DpxPacking.FilledB))
                throw new ImageNotSupportedException($"DPX: packing {(int)e.Packing} is not supported");
            long px = (long)l.Width * l.Height;
            if (px > o.MaxPixels || px * l.Channels > Array.MaxLength) throw new ImageNotSupportedException($"DPX: {l.Width} x {l.Height} exceeds MaxPixels");
            var (start, limit) = Place(h, index, data.Length);
            if (start < 0) throw new ImageFormatException("DPX: cannot locate the image element's data");
            var src = data.Slice((int)start, (int)(limit - start));
            var img = RawImage.Create(l.Width, l.Height, l.Color, l.Type, l.Channels);
            if (e.IsRle) DecodeRle(src, h.BigEndian, l, img);
            else DecodePlain(src, h.BigEndian, l, img, e, o);
            return img;
        }

        // ------------------------------------------------------------------ uncompressed

        static unsafe void DecodePlain(ReadOnlySpan<byte> src, bool be, DpxElementLayout l, RawImage img, DpxImageElement e, DpxDecodeOptions o)
        {
            long n = l.DatumsPerLine;
            if (n > int.MaxValue / 8) throw new ImageNotSupportedException("DPX: lines too long");
            long natural = NaturalLineBytes(e, n);
            long eol = e.EndOfLinePadding is uint p && p != 0 && p < 1u << 20 ? p : 0;
            long stride = natural + eol;
            if (eol == 0 && PadAmbiguous(e))
            {
                // Lines of 8 / 16 bit data (and 12 bit in 16 bit words) that do not fill whole 32-bit words: SMPTE 268M packs
                // all data into 32-bit words, broken at line ends, which ImageMagick follows for 8 bit, while OpenImageIO does
                // not pad and nobody pads 16 bit lines. The writers' habit holds unless the data ends exactly where the other
                // layout ends (so that truncated files are not taken for the other layout).
                long padded = (natural + 3) & ~3L;
                if (padded != natural)
                {
                    long avail = src.Length - (e.EndOfImagePadding is uint q && q < src.Length ? q : 0);
                    bool preferPadded = e.BitDepth == 8;
                    long other = preferPadded ? natural * l.Height : padded * l.Height;
                    long otherMin = preferPadded ? other : padded * (l.Height - 1) + natural;
                    bool otherExact = avail >= otherMin && avail < other + 4;
                    long mine = preferPadded ? padded * l.Height : natural * l.Height;
                    bool mineExact = avail >= (preferPadded ? padded * (l.Height - 1) + natural : mine) && avail < mine + 4;
                    if (otherExact && !mineExact ? !preferPadded : preferPadded) stride = padded;
                }
            }
            long need = stride * (l.Height - 1) + natural;
            if (need > src.Length) throw new ImageFormatException("DPX: image data is truncated");

            int h = l.Height;
            int threads = (long)l.Width * h * l.Channels >= 1 << 20 && !OperatingSystem.IsBrowser() ? Math.Min(Environment.ProcessorCount, 8) : 1;
            if (!o.Parallel) threads = 1;
            fixed (byte* ps = src)
            {
                byte* sp = ps;
                int srcLen = src.Length;
                void Band(int y0, int y1)
                {
                    var s = new ReadOnlySpan<byte>(sp, srcLen);
                    var line = l.Kind == DpxKind.Direct && img.SampleType is SampleType.UInt16 or SampleType.UInt8 && e.BitDepth != 1
                        ? null : new ushort[n];
                    for (int y = y0; y < y1; y++)
                    {
                        var row = s.Slice((int)(stride * y), (int)natural);
                        StoreLine(row, be, l, e, img, y, line);
                    }
                }
                if (threads <= 1) Band(0, h);
                else Parallel.For(0, threads, i => Band((int)((long)h * i / threads), (int)((long)h * (i + 1) / threads)));
            }
        }

        /// <summary>Unpacks one line and stores it into row y of the image.</summary>
        static void StoreLine(ReadOnlySpan<byte> row, bool be, DpxElementLayout l, DpxImageElement e, RawImage img, int y, ushort[]? line)
        {
            int n = (int)l.DatumsPerLine;
            int rowLen = l.Width * l.Channels;
            switch (img.SampleType)
            {
                case SampleType.Float32:
                    {
                        var dst = img.GetData<float>().AsSpan(y * rowLen, rowLen);
                        for (int i = 0; i < n; i++)
                        {
                            uint b = be ? BinaryPrimitives.ReadUInt32BigEndian(row.Slice(4 * i)) : BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(4 * i));
                            dst[Pos(l.Kind, i)] = BitConverter.UInt32BitsToSingle(b);
                        }
                        return;
                    }
                case SampleType.Float64:
                    {
                        var dst = img.GetData<double>().AsSpan(y * rowLen, rowLen);
                        for (int i = 0; i < n; i++)
                        {
                            ulong b = be ? BinaryPrimitives.ReadUInt64BigEndian(row.Slice(8 * i)) : BinaryPrimitives.ReadUInt64LittleEndian(row.Slice(8 * i));
                            dst[Pos(l.Kind, i)] = BitConverter.UInt64BitsToDouble(b);
                        }
                        return;
                    }
            }
            if (line == null)
            {
                // plain unsigned components straight into the row
                if (img.SampleType == SampleType.UInt8) row.Slice(0, n).CopyTo(img.GetData<byte>().AsSpan(y * rowLen, n));
                else Unpack(row, be, e, l.Comps == 1, img.GetData<ushort>().AsSpan(y * rowLen, n));
                return;
            }
            Unpack(row, be, e, l.Comps == 1, line);
            Convert(line, l, e, img, y);
        }

        /// <summary>Destination index of datum i in a row for the float paths (Direct or ABGR).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Pos(DpxKind k, int i) => k == DpxKind.Abgr ? (i & ~3) + 3 - (i & 3) : i;

        /// <summary>
        /// Unpacks dst.Length integer datums of a line (1 to 16 bits) as stored. 10 bit filled words hold the first datum in the
        /// high bits for elements of several components (R in bits 31..22 of an RGB word, what every tool does) but in the low
        /// bits for one component elements (SMPTE 268M figure C.3, also what ImageMagick and OpenImageIO read; for the last,
        /// partly filled word of a line ImageMagick keeps to that, OpenImageIO takes the datums from the high bits).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void Unpack(ReadOnlySpan<byte> row, bool be, DpxImageElement e, bool lowFirst, Span<ushort> dst)
        {
            int n = dst.Length;
            switch (e.BitDepth)
            {
                case 8:
                    for (int i = 0; i < n; i++) dst[i] = row[i];
                    return;
                case 16:
                    if (be) for (int i = 0; i < n; i++) dst[i] = BinaryPrimitives.ReadUInt16BigEndian(row.Slice(2 * i));
                    else for (int i = 0; i < n; i++) dst[i] = BinaryPrimitives.ReadUInt16LittleEndian(row.Slice(2 * i));
                    return;
                case 12 when e.Packing != DpxPacking.Packed:
                    {
                        bool a = e.Packing == DpxPacking.FilledA;
                        for (int i = 0; i < n; i++)
                        {
                            int v = be ? BinaryPrimitives.ReadUInt16BigEndian(row.Slice(2 * i)) : BinaryPrimitives.ReadUInt16LittleEndian(row.Slice(2 * i));
                            dst[i] = (ushort)(a ? v >> 4 : v & 0xFFF);
                        }
                        return;
                    }
                case 10 when e.Packing != DpxPacking.Packed:
                    {
                        int s0 = e.Packing == DpxPacking.FilledA ? 22 : 20, s1 = s0 - 10, s2 = s0 - 20;
                        if (lowFirst) (s0, s2) = (s2, s0);
                        int full = n / 3, i = 0;
                        for (int k = 0; k < full; k++, i += 3)
                        {
                            uint w = be ? BinaryPrimitives.ReadUInt32BigEndian(row.Slice(4 * k)) : BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(4 * k));
                            dst[i] = (ushort)(w >> s0 & 1023);
                            dst[i + 1] = (ushort)(w >> s1 & 1023);
                            dst[i + 2] = (ushort)(w >> s2 & 1023);
                        }
                        if (i < n)
                        {
                            uint w = be ? BinaryPrimitives.ReadUInt32BigEndian(row.Slice(4 * full)) : BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(4 * full));
                            dst[i] = (ushort)(w >> s0 & 1023);
                            if (i + 1 < n) dst[i + 1] = (ushort)(w >> s1 & 1023);
                        }
                        return;
                    }
                default:
                    {
                        // packed (1, 10, 12 bit): an LSB first bit stream through 32-bit words in file byte order
                        int bits = e.BitDepth;
                        uint mask = (1u << bits) - 1;
                        ulong acc = 0;
                        int have = 0, word = 0;
                        for (int i = 0; i < n; i++)
                        {
                            if (have < bits)
                            {
                                uint w = be ? BinaryPrimitives.ReadUInt32BigEndian(row.Slice(4 * word)) : BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(4 * word));
                                word++;
                                acc |= (ulong)w << have;
                                have += 32;
                            }
                            dst[i] = (ushort)(acc & mask);
                            acc >>= bits;
                            have -= bits;
                        }
                        return;
                    }
            }
        }

        // ------------------------------------------------------------------ conversion of unpacked lines

        static void Convert(ushort[] line, DpxElementLayout l, DpxImageElement e, RawImage img, int y)
        {
            int rowLen = l.Width * l.Channels;
            switch (img.SampleType)
            {
                case SampleType.UInt8: ConvertTo(line, l, e, img.GetData<byte>().AsSpan(y * rowLen, rowLen)); break;
                case SampleType.UInt16: ConvertTo(line, l, e, img.GetData<ushort>().AsSpan(y * rowLen, rowLen)); break;
                case SampleType.Int8:
                    {
                        var d = img.GetData<sbyte>().AsSpan(y * rowLen, rowLen);
                        for (int i = 0; i < d.Length; i++) d[Pos(l.Kind, i)] = (sbyte)line[i];
                        break;
                    }
                case SampleType.Int16:
                    {
                        var d = img.GetData<short>().AsSpan(y * rowLen, rowLen);
                        int sh = 32 - e.BitDepth;
                        for (int i = 0; i < d.Length; i++) d[Pos(l.Kind, i)] = (short)((int)line[i] << sh >> sh);
                        break;
                    }
            }
        }

        static void ConvertTo<T>(ushort[] line, DpxElementLayout l, DpxImageElement e, Span<T> d) where T : unmanaged, IBinaryInteger<T>
        {
            switch (l.Kind)
            {
                case DpxKind.Direct:
                    if (e.BitDepth == 1) for (int i = 0; i < d.Length; i++) d[i] = T.CreateTruncating(line[i] != 0 ? 255 : 0);
                    else for (int i = 0; i < d.Length; i++) d[i] = T.CreateTruncating(line[i]);
                    return;
                case DpxKind.Abgr:
                    for (int i = 0; i < d.Length; i += 4)
                    {
                        d[i] = T.CreateTruncating(line[i + 3]);
                        d[i + 1] = T.CreateTruncating(line[i + 2]);
                        d[i + 2] = T.CreateTruncating(line[i + 1]);
                        d[i + 3] = T.CreateTruncating(line[i]);
                    }
                    return;
                default:
                    Ycc(line, l, e, d);
                    return;
            }
        }

        /// <summary>
        /// Y'CbCr to R'G'B': BT.709 coefficients for SMPTE 274M / ITU-R 709 colorimetry, BT.601 for ITU-R 601 and composite video,
        /// otherwise BT.709 from 1280 pixels per line on and BT.601 below. The code range is the reference low / high code of the
        /// element when given (Y from low to high, chroma around their middle with the same span), else the video range (Y 16..235,
        /// chroma 16..240 at 8 bit, scaled for more bits). Results use the full code range 0..2^bits-1. 4:2:2 chroma is co-sited
        /// with the even pixels; odd pixels take the mean of the two neighbours.
        /// </summary>
        static void Ycc<T>(ushort[] line, DpxElementLayout l, DpxImageElement e, Span<T> d) where T : unmanaged, IBinaryInteger<T>
        {
            var (kr, kb) = Coefficients(e, l.Width);
            int bits = e.BitDepth;
            double max = (1 << bits) - 1, scale = 1 << (bits - 8);
            double yLo = 16 * scale, yHi = 235 * scale, cLo = 16 * scale, cHi = 240 * scale;
            if (e.ReferenceLowCode is uint lo && e.ReferenceHighCode is uint hi && hi > lo && hi <= max
                && !(Math.Abs(lo - yLo) < 0.5 && Math.Abs(hi - yHi) < 0.5))
            {
                yLo = cLo = lo; yHi = cHi = hi;
            }
            double ys = 1 / (yHi - yLo), cs = 1 / (cHi - cLo), cMid = (cLo + cHi) / 2;
            double kg = 1 - kr - kb;
            double crR = 2 * (1 - kr), cbB = 2 * (1 - kb), cbG = -cbB * kb / kg, crG = -crR * kr / kg;
            int w = l.Width, ch = l.Channels;
            bool alpha = l.Kind is DpxKind.Ycca422 or DpxKind.Ycca444;

            var k = new YccConst { YLo = yLo, Ys = ys, CMid = cMid, Cs = cs, CrR = crR, CbB = cbB, CbG = cbG, CrG = crG, Max = max, Ch = ch, Alpha = alpha };

            switch (l.Kind)
            {
                case DpxKind.Ycc444: case DpxKind.Ycca444:
                    {
                        int nc = l.Comps;
                        for (int x = 0; x < w; x++) Put(d, k, x, line[x * nc + 1], line[x * nc], line[x * nc + 2], alpha ? line[x * nc + 3] : 0);
                        break;
                    }
                default:
                    {
                        // units of two pixels: Cb Y0 Cr Y1 or Cb Y0 A0 Cr Y1 A1
                        int nc = l.Comps, units = l.Units;
                        int cbO = 0, y0O = 1, crO = alpha ? 3 : 2, y1O = alpha ? 4 : 3, a0O = 2, a1O = 5;
                        for (int u = 0; u < units; u++)
                        {
                            int b = u * nc, nb = u + 1 < units ? b + nc : b;
                            int x = 2 * u;
                            Put(d, k, x, line[b + y0O], line[b + cbO], line[b + crO], alpha ? line[b + a0O] : 0);
                            if (x + 1 < w)
                                Put(d, k, x + 1, line[b + y1O], (line[b + cbO] + line[nb + cbO]) * 0.5, (line[b + crO] + line[nb + crO]) * 0.5, alpha ? line[b + a1O] : 0);
                        }
                        break;
                    }
            }
        }

        struct YccConst { public double YLo, Ys, CMid, Cs, CrR, CbB, CbG, CrG, Max; public int Ch; public bool Alpha; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Put<T>(Span<T> d, in YccConst k, int x, double yv, double cb, double cr, int a) where T : unmanaged, IBinaryInteger<T>
        {
            double Y = (yv - k.YLo) * k.Ys, Cb = (cb - k.CMid) * k.Cs, Cr = (cr - k.CMid) * k.Cs;
            int o = x * k.Ch;
            d[o] = T.CreateTruncating(Q(Y + k.CrR * Cr, k.Max));
            d[o + 1] = T.CreateTruncating(Q(Y + k.CbG * Cb + k.CrG * Cr, k.Max));
            d[o + 2] = T.CreateTruncating(Q(Y + k.CbB * Cb, k.Max));
            if (k.Alpha) d[o + 3] = T.CreateTruncating(a);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Q(double v, double max) => v <= 0 ? 0 : v >= 1 ? (int)max : (int)(v * max + 0.5);

        static (double kr, double kb) Coefficients(DpxImageElement e, int width)
        {
            static int Pick(int code) => code switch { 5 or 6 => 709, 7 or 8 or 9 or 10 => 601, _ => 0 };
            int std = Pick((int)e.Colorimetric);
            if (std == 0) std = Pick((int)e.Transfer);
            if (std == 0) std = width >= 1280 ? 709 : 601;
            return std == 709 ? (0.2126, 0.0722) : (0.299, 0.114);
        }

        // ------------------------------------------------------------------ run length encoding

        /// <summary>Sequential datum reader for run length encoded lines (integer depths).</summary>
        ref struct DatumReader
        {
            readonly ReadOnlySpan<byte> _d;
            readonly bool _be;
            readonly int _bits, _mode;   // mode: 0 bytes, 1 16-bit units (16, 12 filled), 2 10-bit filled, 3 packed bit stream
            readonly bool _a, _low;
            long _pos;                    // byte position (modes 0, 1, 2), bit position (mode 3)
            int _slot;                    // datum within a 10-bit filled word

            public DatumReader(ReadOnlySpan<byte> d, bool be, DpxImageElement e, bool lowFirst)
            {
                _d = d; _be = be; _bits = e.BitDepth; _a = e.Packing != DpxPacking.FilledB; _low = lowFirst;
                _mode = e.BitDepth switch
                {
                    8 => 0,
                    16 => 1,
                    12 when e.Packing != DpxPacking.Packed => 1,
                    10 when e.Packing != DpxPacking.Packed => 2,
                    _ => 3,
                };
                _pos = 0; _slot = 0;
            }

            uint Word(long byteOffset)
            {
                if (byteOffset + 4 > _d.Length) throw new ImageFormatException("DPX: run length data is truncated");
                return _be ? BinaryPrimitives.ReadUInt32BigEndian(_d.Slice((int)byteOffset)) : BinaryPrimitives.ReadUInt32LittleEndian(_d.Slice((int)byteOffset));
            }

            public ushort Next()
            {
                switch (_mode)
                {
                    case 0:
                        if (_pos >= _d.Length) throw new ImageFormatException("DPX: run length data is truncated");
                        return _d[(int)_pos++];
                    case 1:
                        {
                            if (_pos + 2 > _d.Length) throw new ImageFormatException("DPX: run length data is truncated");
                            int v = _be ? BinaryPrimitives.ReadUInt16BigEndian(_d.Slice((int)_pos)) : BinaryPrimitives.ReadUInt16LittleEndian(_d.Slice((int)_pos));
                            _pos += 2;
                            return (ushort)(_bits == 16 ? v : _a ? v >> 4 : v & 0xFFF);
                        }
                    case 2:
                        {
                            uint w = Word(_pos);
                            int shift = (_a ? 22 : 20) - 10 * (_low ? 2 - _slot : _slot);
                            if (++_slot == 3) { _slot = 0; _pos += 4; }
                            return (ushort)(w >> shift & 1023);
                        }
                    default:
                        {
                            long word = _pos >> 5;
                            int bit = (int)(_pos & 31);
                            ulong acc = Word(word * 4);
                            if (bit + _bits > 32) acc |= (ulong)Word(word * 4 + 4) << 32;
                            _pos += _bits;
                            return (ushort)(acc >> bit & ((1u << _bits) - 1));
                        }
                }
            }

            /// <summary>Lines start on a 32-bit word boundary.</summary>
            public void Align()
            {
                switch (_mode)
                {
                    case 2: if (_slot != 0) { _slot = 0; _pos += 4; } break;
                    case 3: _pos = (_pos + 31) & ~31L; break;
                    default: _pos = (_pos + 3) & ~3L; break;
                }
            }
        }

        static void DecodeRle(ReadOnlySpan<byte> src, bool be, DpxElementLayout l, RawImage img)
        {
            var e = l.E;
            var r = new DatumReader(src, be, e, l.Comps == 1);
            int nc = l.Comps, units = l.Units;
            var line = new ushort[(long)units * nc];
            var pixel = new ushort[nc];
            for (int y = 0; y < l.Height; y++)
            {
                int u = 0;
                while (u < units)
                {
                    int flag = r.Next();
                    int count = flag >> 1;
                    if (count == 0) throw new ImageFormatException("DPX: run of length zero");
                    if (count > units - u) throw new ImageFormatException("DPX: run crosses the end of a line");
                    if ((flag & 1) != 0)
                    {
                        for (int c = 0; c < nc; c++) pixel[c] = r.Next();
                        for (int k = 0; k < count; k++, u++) pixel.CopyTo(line, u * nc);
                    }
                    else
                        for (int k = 0; k < count * nc; k++) line[u * nc + k] = r.Next();
                    if ((flag & 1) == 0) u += count;
                }
                r.Align();
                if (l.Kind == DpxKind.Direct && e.BitDepth != 1 && img.SampleType == SampleType.UInt8)
                {
                    var d = img.GetData<byte>().AsSpan(y * l.Width * l.Channels, l.Width * l.Channels);
                    for (int i = 0; i < d.Length; i++) d[i] = (byte)line[i];
                }
                else if (l.Kind == DpxKind.Direct && img.SampleType == SampleType.UInt16)
                    line.AsSpan(0, l.Width * l.Channels).CopyTo(img.GetData<ushort>().AsSpan(y * l.Width * l.Channels));
                else Convert(line, l, e, img, y);
            }
        }
    }
}
