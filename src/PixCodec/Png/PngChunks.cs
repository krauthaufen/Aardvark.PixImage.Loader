using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using PixCodec.Compression;

namespace PixCodec
{
    internal struct PngHeader
    {
        public int Width, Height, BitDepth, ColorType;
        public bool Interlaced;

        public readonly int PngChannels => ColorType switch { 2 => 3, 4 => 2, 6 => 4, _ => 1 };
        public readonly int BitsPerPixel => PngChannels * BitDepth;
        public readonly bool IsPalette => ColorType == 3;
    }

    /// <summary>One animation frame as found in the file: its fcTL fields and where its compressed data lives.</summary>
    internal sealed class PngFrameEntry
    {
        public int X, Y, Width, Height, DelayNumerator, DelayDenominator;
        public PngDisposal Disposal;
        public PngBlend Blend;
        /// <summary>The frame is the default image (IDAT chunks) rather than fdAT chunks.</summary>
        public bool UsesIdat;
        /// <summary>fdAT payloads (offset, length) without their sequence numbers.</summary>
        public List<(int Offset, int Length)> Data = new();
        public long Total;
    }

    /// <summary>The chunk level structure of a PNG file: offsets into the data rather than copies.</summary>
    internal sealed class PngStructure
    {
        public PngHeader Header;
        public int PlteOffset = -1, PlteLength;
        public int TrnsOffset = -1, TrnsLength;
        public int FirstIdat = -1, IdatCount;
        public long IdatTotal;
        public ImageMetadata? Meta;
        public PngMetadata? PngMeta;
        public bool Animated;
        public int AnnouncedFrames, Plays;
        public List<PngFrameEntry>? Frames;

        public ReadOnlySpan<byte> Plte(ReadOnlySpan<byte> data) => PlteOffset < 0 ? default : data.Slice(PlteOffset, PlteLength);
        public ReadOnlySpan<byte> Trns(ReadOnlySpan<byte> data) => TrnsOffset < 0 ? default : data.Slice(TrnsOffset, TrnsLength);
    }

    /// <summary>PNG container: signature, chunk walking, ancillary chunk parsing.</summary>
    internal static class PngChunks
    {
        public static ReadOnlySpan<byte> Signature => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        public const uint IHDR = 0x49484452, PLTE = 0x504C5445, IDAT = 0x49444154, IEND = 0x49454E44,
                          tRNS = 0x74524E53, gAMA = 0x67414D41, pHYs = 0x70485973, iCCP = 0x69434350, eXIf = 0x65584966,
                          sRGB = 0x73524742, cHRM = 0x6348524D, sBIT = 0x73424954, bKGD = 0x624B4744, tIME = 0x74494D45,
                          tEXt = 0x74455874, zTXt = 0x7A545874, iTXt = 0x69545874,
                          acTL = 0x6163544C, fcTL = 0x6663544C, fdAT = 0x66644154,
                          hIST = 0x68495354, sPLT = 0x73504C54, oFFs = 0x6F464673, pCAL = 0x7043414C, sCAL = 0x7343414C,
                          sTER = 0x73544552, cICP = 0x63494350, mDCv = 0x6D444376, cLLi = 0x634C4C69;

        public const int FirstChunkAfterHeader = 8 + 12 + 13;

        /// <summary>Upper bound for the inflated size of a text chunk or ICC profile; larger ones are skipped.</summary>
        const int MaxInflatedAncillary = 64 << 20;

        [Flags]
        public enum ParseFlags
        {
            None = 0,
            /// <summary>Collect sRGB, cHRM, sBIT, bKGD, tIME and text chunks.</summary>
            PngMetadata = 1,
            /// <summary>Collect and validate the APNG frame structure.</summary>
            Animation = 2,
        }

        public static PngHeader ReadHeader(ReadOnlySpan<byte> data, bool verifyCrc)
        {
            if (data.Length < 8 || !data.Slice(0, 8).SequenceEqual(Signature))
                throw new ImageFormatException("not a PNG file (bad signature)");
            if (data.Length < FirstChunkAfterHeader)
                throw new ImageFormatException("PNG header is truncated");
            if (BinaryPrimitives.ReadUInt32BigEndian(data.Slice(8)) != 13 || BinaryPrimitives.ReadUInt32BigEndian(data.Slice(12)) != IHDR)
                throw new ImageFormatException("PNG does not start with an IHDR chunk");
            if (verifyCrc && !CrcOk(data, 8, 13))
                throw new ImageFormatException("PNG IHDR checksum mismatch");

            var b = data.Slice(16, 13);
            uint w = BinaryPrimitives.ReadUInt32BigEndian(b), h = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(4));
            if (w == 0 || h == 0 || w > int.MaxValue || h > int.MaxValue)
                throw new ImageFormatException("invalid PNG image dimensions");
            int depth = b[8], color = b[9];
            bool ok = color switch
            {
                0 => depth is 1 or 2 or 4 or 8 or 16,
                3 => depth is 1 or 2 or 4 or 8,
                2 or 4 or 6 => depth is 8 or 16,
                _ => false,
            };
            if (!ok) throw new ImageFormatException($"invalid PNG color type {color} / bit depth {depth}");
            if (b[10] != 0) throw new ImageFormatException("invalid PNG compression method");
            if (b[11] != 0) throw new ImageFormatException("invalid PNG filter method");
            if (b[12] > 1) throw new ImageFormatException("invalid PNG interlace method");
            return new PngHeader { Width = (int)w, Height = (int)h, BitDepth = depth, ColorType = color, Interlaced = b[12] == 1 };
        }

        /// <summary>Reads the chunk at <paramref name="pos"/>; throws when it does not fit into the data.</summary>
        public static void ReadChunk(ReadOnlySpan<byte> data, int pos, out uint type, out ReadOnlySpan<byte> body)
        {
            if (data.Length - pos < 12) throw new ImageFormatException("PNG chunk is truncated");
            uint len = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(pos));
            if (len > (uint)(data.Length - pos - 12)) throw new ImageFormatException("PNG chunk is truncated");
            type = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(pos + 4));
            for (int s = 0; s < 32; s += 8)
            {
                uint c = ((type >> s) & 0xFF) | 0x20;
                if (c < 'a' || c > 'z') throw new ImageFormatException("invalid PNG chunk type");
            }
            body = data.Slice(pos + 8, (int)len);
        }

        public static bool CrcOk(ReadOnlySpan<byte> data, int chunkPos, int bodyLength)
            => Zlib.Crc32(data.Slice(chunkPos + 4, 4 + bodyLength)) == BinaryPrimitives.ReadUInt32BigEndian(data.Slice(chunkPos + 8 + bodyLength));

        static bool IsCritical(uint type) => (type & 0x20000000) == 0;

        static string TypeName(uint type)
            => new string(new[] { (char)(type >> 24), (char)((type >> 16) & 0xFF), (char)((type >> 8) & 0xFF), (char)(type & 0xFF) });

        public static bool IsUsableTransparency(scoped in PngHeader h, int length) => h.ColorType switch
        {
            0 => length >= 2,
            2 => length >= 6,
            3 => length >= 1,
            _ => false,
        };

        /// <summary>What the chunks in front of the pixel data say (see <see cref="ScanHead"/>).</summary>
        public ref struct HeadInfo
        {
            public ReadOnlySpan<byte> Plte, Trns;
            /// <summary>An acTL chunk makes the file an APNG; Frames is the frame count it announces (else 1).</summary>
            public bool Animated;
            public int Frames;
            public double DpiX, DpiY;
            public int Orientation;
            public bool HasIccProfile;
        }

        /// <summary>
        /// Walks the chunks in front of the first IDAT and picks up what header level information needs: PLTE, tRNS,
        /// acTL, pHYs, iCCP and eXIf. Ancillary chunks are CRC checked, because a default decode drops them when
        /// damaged and the two must agree. Stops quietly at IDAT, IEND or where the data ends, so chunks behind the
        /// pixel data (where eXIf is allowed too) are not seen.
        /// </summary>
        public static HeadInfo ScanHead(ReadOnlySpan<byte> data, scoped in PngHeader h)
        {
            var info = new HeadInfo { Frames = 1 };
            int pos = FirstChunkAfterHeader;
            while (data.Length - pos >= 12)
            {
                uint len = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(pos));
                uint type = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(pos + 4));
                if (type == IDAT || type == IEND || len > (uint)(data.Length - pos - 12)) break;
                var body = data.Slice(pos + 8, (int)len);
                switch (type)
                {
                    case PLTE:
                        if (info.Plte.IsEmpty) info.Plte = body;
                        break;
                    case tRNS:
                        if (info.Trns.IsEmpty && IsUsableTransparency(h, body.Length) && CrcOk(data, pos, body.Length)) info.Trns = body;
                        break;
                    case acTL:
                        if (!info.Animated && body.Length == 8 && CrcOk(data, pos, 8)
                            && BinaryPrimitives.ReadUInt32BigEndian(body) is > 0 and <= int.MaxValue and var n
                            && BinaryPrimitives.ReadUInt32BigEndian(body.Slice(4)) <= int.MaxValue)
                        {
                            info.Animated = true;
                            info.Frames = (int)n;
                        }
                        break;
                    case pHYs:
                        if (body.Length == 9 && body[8] == 1 && CrcOk(data, pos, 9))
                        {
                            double x = PixelsPerMeterToDpi(BinaryPrimitives.ReadUInt32BigEndian(body));
                            double y = PixelsPerMeterToDpi(BinaryPrimitives.ReadUInt32BigEndian(body.Slice(4)));
                            if (x > 0 && y > 0) { info.DpiX = x; info.DpiY = y; }
                        }
                        break;
                    case eXIf:
                        if (body.Length >= 8 && CrcOk(data, pos, body.Length)) info.Orientation = ExifOrientation(body);
                        break;
                    case iCCP:
                        // structure only - the profile is not inflated here
                        int nameEnd = body.Slice(0, Math.Min(body.Length, 80)).IndexOf((byte)0);
                        if (nameEnd >= 1 && body.Length >= nameEnd + 3 && body[nameEnd + 1] == 0 && CrcOk(data, pos, body.Length)) info.HasIccProfile = true;
                        break;
                }
                pos += 12 + (int)len;
            }
            return info;
        }

        /// <summary>
        /// Validates the chunk structure of the whole file and records where everything is.
        /// Always collects PLTE, tRNS, the IDAT run and the common metadata (gAMA, pHYs, iCCP, eXIf).
        /// </summary>
        public static PngStructure Parse(ReadOnlySpan<byte> data, PngDecodeOptions options, ParseFlags flags)
        {
            bool verify = options.VerifyChecksums;
            bool wantPngMeta = (flags & ParseFlags.PngMetadata) != 0, wantAnimation = (flags & ParseFlags.Animation) != 0;
            var s = new PngStructure { Header = ReadHeader(data, verify) };
            ref readonly PngHeader h = ref s.Header;
            bool idatEnded = false;
            uint sequence = 0;

            int pos = FirstChunkAfterHeader;
            for (bool end = false; !end;)
            {
                if (pos == data.Length) throw new ImageFormatException("PNG data ends without an IEND chunk");
                ReadChunk(data, pos, out uint type, out var body);
                int chunkPos = pos;
                pos += 12 + body.Length;

                bool critical = IsCritical(type);
                bool animationChunk = wantAnimation && type is acTL or fcTL or fdAT;
                bool wanted = critical || animationChunk || type is tRNS or gAMA or pHYs or iCCP or eXIf
                              || (wantPngMeta && type is sRGB or cHRM or sBIT or bKGD or tIME or tEXt or zTXt or iTXt
                                                         or hIST or sPLT or oFFs or pCAL or sCAL or sTER or cICP or mDCv or cLLi);
                if (verify && wanted && !CrcOk(data, chunkPos, body.Length))
                {
                    if (critical) throw new ImageFormatException($"PNG {TypeName(type)} checksum mismatch");
                    if (animationChunk && (s.Animated || type == acTL)) throw new ImageFormatException($"PNG {TypeName(type)} checksum mismatch");
                    continue; // a damaged ancillary chunk is dropped
                }
                if (type != IDAT && s.IdatCount > 0) idatEnded = true;

                switch (type)
                {
                    case IHDR:
                        throw new ImageFormatException("duplicate PNG IHDR chunk");
                    case PLTE:
                        if (s.PlteOffset >= 0) throw new ImageFormatException("duplicate PNG PLTE chunk");
                        if (s.IdatCount > 0) throw new ImageFormatException("PNG PLTE chunk after the pixel data");
                        if (body.Length == 0 || body.Length % 3 != 0 || body.Length > 768)
                            throw new ImageFormatException("invalid PNG PLTE chunk length");
                        s.PlteOffset = chunkPos + 8; s.PlteLength = body.Length;
                        break;
                    case IDAT:
                        if (idatEnded) throw new ImageFormatException("PNG IDAT chunks are not consecutive");
                        if (s.FirstIdat < 0) s.FirstIdat = chunkPos;
                        s.IdatCount++;
                        s.IdatTotal += body.Length;
                        break;
                    case IEND:
                        end = true;
                        break;
                    case tRNS:
                        if (s.IdatCount == 0 && s.TrnsOffset < 0 && IsUsableTransparency(h, body.Length))
                        {
                            s.TrnsOffset = chunkPos + 8; s.TrnsLength = body.Length;
                        }
                        break;
                    case gAMA:
                        if (body.Length == 4 && BinaryPrimitives.ReadUInt32BigEndian(body) is > 0 and var g)
                            (s.Meta ??= new ImageMetadata()).Gamma = g / 100000.0;
                        break;
                    case pHYs:
                        if (body.Length == 9 && body[8] == 1)
                        {
                            double x = PixelsPerMeterToDpi(BinaryPrimitives.ReadUInt32BigEndian(body));
                            double y = PixelsPerMeterToDpi(BinaryPrimitives.ReadUInt32BigEndian(body.Slice(4)));
                            if (x > 0 && y > 0) { s.Meta ??= new ImageMetadata(); s.Meta.DpiX = x; s.Meta.DpiY = y; }
                        }
                        break;
                    case iCCP:
                        if (ReadIccProfile(body) is { } icc) (s.Meta ??= new ImageMetadata()).IccProfile = icc;
                        break;
                    case eXIf:
                        if (body.Length >= 8)
                        {
                            s.Meta ??= new ImageMetadata();
                            s.Meta.Exif = body.ToArray();
                            s.Meta.Orientation = ExifOrientation(body);
                        }
                        break;
                    case acTL:
                        if (!wantAnimation || s.Animated || s.IdatCount > 0) break;
                        if (body.Length != 8) throw new ImageFormatException("invalid APNG acTL chunk");
                        uint frames = BinaryPrimitives.ReadUInt32BigEndian(body), plays = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(4));
                        if (frames == 0 || frames > int.MaxValue || plays > int.MaxValue) throw new ImageFormatException("invalid APNG acTL chunk");
                        s.Animated = true; s.AnnouncedFrames = (int)frames; s.Plays = (int)plays;
                        s.Frames = new List<PngFrameEntry>();
                        break;
                    case fcTL:
                        if (!wantAnimation || !s.Animated) break;
                        s.Frames!.Add(ReadFrameControl(body, h, ref sequence, beforeIdat: s.IdatCount == 0, first: s.Frames.Count == 0));
                        break;
                    case fdAT:
                        if (!wantAnimation || !s.Animated) break;
                        if (body.Length < 4 || BinaryPrimitives.ReadUInt32BigEndian(body) != sequence++)
                            throw new ImageFormatException("APNG chunks are out of sequence");
                        if (s.Frames!.Count == 0 || s.Frames[^1].UsesIdat || s.IdatCount == 0)
                            throw new ImageFormatException("APNG fdAT chunk without a frame");
                        s.Frames[^1].Data.Add((chunkPos + 12, body.Length - 4));
                        s.Frames[^1].Total += body.Length - 4;
                        break;
                    default:
                        if (critical) throw new ImageNotSupportedException($"unknown critical PNG chunk '{TypeName(type)}'");
                        if (wantPngMeta) ReadPngMetadata(type, body, data, s);
                        break;
                }
            }

            if (s.IdatCount == 0) throw new ImageFormatException("PNG has no IDAT chunk");
            if (h.IsPalette && s.PlteOffset < 0) throw new ImageFormatException("palette PNG has no PLTE chunk");

            if (wantAnimation)
            {
                if (s.Animated)
                {
                    if (s.Frames!.Count != s.AnnouncedFrames)
                        throw new ImageFormatException($"APNG announces {s.AnnouncedFrames} frames but contains {s.Frames.Count}");
                    foreach (var f in s.Frames)
                        if (!f.UsesIdat && f.Data.Count == 0) throw new ImageFormatException("APNG frame without data");
                }
                else
                {
                    s.Frames = new List<PngFrameEntry> { new PngFrameEntry { Width = h.Width, Height = h.Height, UsesIdat = true } };
                }
            }
            return s;
        }

        static PngFrameEntry ReadFrameControl(ReadOnlySpan<byte> body, scoped in PngHeader h, ref uint sequence, bool beforeIdat, bool first)
        {
            if (body.Length != 26) throw new ImageFormatException("invalid APNG fcTL chunk");
            if (BinaryPrimitives.ReadUInt32BigEndian(body) != sequence++) throw new ImageFormatException("APNG chunks are out of sequence");
            uint w = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(4)), fh = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(8));
            uint x = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(12)), y = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(16));
            if (w == 0 || fh == 0 || w > (uint)h.Width || fh > (uint)h.Height || x > (uint)h.Width - w || y > (uint)h.Height - fh)
                throw new ImageFormatException("APNG frame region is outside the image");
            if (body[24] > 2 || body[25] > 1) throw new ImageFormatException("invalid APNG dispose or blend operation");
            var f = new PngFrameEntry
            {
                X = (int)x, Y = (int)y, Width = (int)w, Height = (int)fh,
                DelayNumerator = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(20)),
                DelayDenominator = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(22)),
                Disposal = (PngDisposal)body[24], Blend = (PngBlend)body[25],
                UsesIdat = beforeIdat,
            };
            if (beforeIdat)
            {
                // The default image can only be the first frame, and then it covers the whole canvas.
                if (!first) throw new ImageFormatException("more than one APNG fcTL chunk before the image data");
                if (x != 0 || y != 0 || w != (uint)h.Width || fh != (uint)h.Height)
                    throw new ImageFormatException("the first APNG frame does not cover the image");
            }
            return f;
        }

        // ------------------------------------------------------------------ ancillary chunks

        /// <summary>
        /// pHYs stores whole pixels per meter, so e.g. 72 dpi comes back as 72.009. A value within half a
        /// quantization step (0.0127 dpi) of a whole dpi is taken to mean that whole number.
        /// </summary>
        static double PixelsPerMeterToDpi(uint pixelsPerMeter)
        {
            double dpi = pixelsPerMeter * 0.0254;
            double rounded = Math.Round(dpi);
            return Math.Abs(dpi - rounded) <= 0.0127 ? rounded : dpi;
        }

        /// <summary>
        /// Inflates a zlib stream of unknown size without trusting it: the buffer grows step by step and the
        /// attempt is given up (null) beyond <see cref="MaxInflatedAncillary"/> or on corrupt data.
        /// </summary>
        static byte[]? InflateBounded(ReadOnlySpan<byte> z)
        {
            long size = Math.Max(4096L, z.Length * 4L);
            while (true)
            {
                size = Math.Min(size, MaxInflatedAncillary);
                var buffer = new byte[size + 1];
                int got;
                try { got = Zlib.Decompress(z, buffer); }
                catch (Exception e) when (e is InvalidDataException or IOException or ImageFormatException) { return null; }
                if (got <= size) return buffer.AsSpan(0, got).ToArray();
                if (size == MaxInflatedAncillary) return null;
                size *= 8;
            }
        }

        static byte[]? ReadIccProfile(ReadOnlySpan<byte> body)
        {
            // profile name (1..79 bytes), 0, compression method (0), zlib stream
            int nameEnd = body.Slice(0, Math.Min(body.Length, 80)).IndexOf((byte)0);
            if (nameEnd < 1 || body.Length < nameEnd + 3 || body[nameEnd + 1] != 0) return null;
            var icc = InflateBounded(body.Slice(nameEnd + 2));
            return icc is { Length: > 0 } ? icc : null;
        }

        /// <summary>Orientation (tag 0x0112) from the first IFD of an EXIF blob, 0 if absent or malformed.</summary>
        static int ExifOrientation(ReadOnlySpan<byte> exif)
        {
            if (exif.Length < 8) return 0;
            bool little;
            if (exif[0] == 'I' && exif[1] == 'I') little = true;
            else if (exif[0] == 'M' && exif[1] == 'M') little = false;
            else return 0;
            if (U16(exif, 2, little) != 42) return 0;
            uint ifd = U32(exif, 4, little);
            if (ifd > (uint)exif.Length - 2) return 0;
            int count = U16(exif, (int)ifd, little);
            long entry = ifd + 2;
            for (int i = 0; i < count && entry + 12 <= exif.Length; i++, entry += 12)
            {
                if (U16(exif, (int)entry, little) != 0x0112) continue;
                if (U16(exif, (int)entry + 2, little) != 3 || U32(exif, (int)entry + 4, little) != 1) return 0;
                int v = U16(exif, (int)entry + 8, little);
                return v is >= 1 and <= 8 ? v : 0;
            }
            return 0;

            static int U16(ReadOnlySpan<byte> s, int o, bool le)
                => le ? BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(o)) : BinaryPrimitives.ReadUInt16BigEndian(s.Slice(o));
            static uint U32(ReadOnlySpan<byte> s, int o, bool le)
                => le ? BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(o)) : BinaryPrimitives.ReadUInt32BigEndian(s.Slice(o));
        }

        /// <summary>Number of sBIT entries for a color type.</summary>
        public static int SignificantBitsCount(int colorType) => colorType switch { 0 => 1, 4 => 2, 6 => 4, _ => 3 };

        static void ReadPngMetadata(uint type, ReadOnlySpan<byte> body, ReadOnlySpan<byte> data, PngStructure s)
        {
            ref readonly PngHeader h = ref s.Header;
            switch (type)
            {
                case sRGB:
                    if (body.Length == 1 && body[0] <= 3) (s.PngMeta ??= new PngMetadata()).SrgbIntent = (PngRenderingIntent)body[0];
                    break;
                case cHRM:
                    if (body.Length == 32)
                    {
                        Span<double> v = stackalloc double[8];
                        for (int i = 0; i < 8; i++) v[i] = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(i * 4)) / 100000.0;
                        (s.PngMeta ??= new PngMetadata()).Chromaticities = new PngChromaticities(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7]);
                    }
                    break;
                case sBIT:
                    if (body.Length == SignificantBitsCount(h.ColorType)) (s.PngMeta ??= new PngMetadata()).SignificantBits = body.ToArray();
                    break;
                case bKGD:
                    if (h.IsPalette)
                    {
                        var plte = s.Plte(data);
                        if (body.Length == 1 && body[0] * 3 + 2 < plte.Length)
                        {
                            int i = body[0] * 3;
                            (s.PngMeta ??= new PngMetadata()).Background = new PngBackground(plte[i], plte[i + 1], plte[i + 2], body[0]);
                        }
                    }
                    else if (h.ColorType is 0 or 4 && body.Length == 2)
                    {
                        // scaled like the decoded samples: 1, 2 and 4 bit gray decodes to 0..255
                        int max = (1 << h.BitDepth) - 1;
                        int v = (BinaryPrimitives.ReadUInt16BigEndian(body) & max) * (h.BitDepth < 8 ? 255 / max : 1);
                        (s.PngMeta ??= new PngMetadata()).Background = new PngBackground((ushort)v, (ushort)v, (ushort)v);
                    }
                    else if (h.ColorType is 2 or 6 && body.Length == 6)
                    {
                        int max = (1 << h.BitDepth) - 1;
                        (s.PngMeta ??= new PngMetadata()).Background = new PngBackground(
                            (ushort)(BinaryPrimitives.ReadUInt16BigEndian(body) & max),
                            (ushort)(BinaryPrimitives.ReadUInt16BigEndian(body.Slice(2)) & max),
                            (ushort)(BinaryPrimitives.ReadUInt16BigEndian(body.Slice(4)) & max));
                    }
                    break;
                case tIME:
                    if (body.Length == 7)
                    {
                        int year = BinaryPrimitives.ReadUInt16BigEndian(body);
                        int month = body[2], day = body[3], hour = body[4], minute = body[5], second = Math.Min((int)body[6], 59); // 60 = leap second
                        if (year is >= 1 and <= 9999 && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month) && hour <= 23 && minute <= 59)
                            (s.PngMeta ??= new PngMetadata()).LastModified = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
                    }
                    break;
                case tEXt:
                case zTXt:
                case iTXt:
                    if (ReadText(type, body) is { } entry) (s.PngMeta ??= new PngMetadata()).Text.Add(entry);
                    break;
                case hIST:
                    if (h.IsPalette && s.PlteLength > 0 && body.Length == s.PlteLength / 3 * 2)
                    {
                        var hist = new ushort[body.Length / 2];
                        for (int i = 0; i < hist.Length; i++) hist[i] = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(i * 2));
                        (s.PngMeta ??= new PngMetadata()).Histogram = hist;
                    }
                    break;
                case sPLT:
                    if (ReadSuggestedPalette(body) is { } palette) (s.PngMeta ??= new PngMetadata()).SuggestedPalettes.Add(palette);
                    break;
                case oFFs:
                    if (body.Length == 9 && body[8] <= 1)
                        (s.PngMeta ??= new PngMetadata()).Offset = new PngOffset(BinaryPrimitives.ReadInt32BigEndian(body),
                            BinaryPrimitives.ReadInt32BigEndian(body.Slice(4)), (PngOffsetUnit)body[8]);
                    break;
                case pCAL:
                    if (ReadPixelCalibration(body) is { } calibration) (s.PngMeta ??= new PngMetadata()).PixelCalibration = calibration;
                    break;
                case sCAL:
                    if (body.Length >= 4 && body[0] is 1 or 2)
                    {
                        var numbers = body.Slice(1);
                        int split = numbers.IndexOf((byte)0);
                        if (split > 0 && TryParseFloat(numbers.Slice(0, split), out double pw) && TryParseFloat(numbers.Slice(split + 1), out double ph) && pw > 0 && ph > 0)
                            (s.PngMeta ??= new PngMetadata()).PhysicalScale = new PngPhysicalScale((PngScaleUnit)body[0], pw, ph);
                    }
                    break;
                case sTER:
                    if (body.Length == 1 && body[0] <= 1) (s.PngMeta ??= new PngMetadata()).StereoMode = (PngStereoMode)body[0];
                    break;
                case cICP:
                    if (body.Length == 4 && body[3] <= 1)
                        (s.PngMeta ??= new PngMetadata()).CodingIndependentCodePoints = new PngCodingIndependentCodePoints(body[0], body[1], body[2], body[3] == 1);
                    break;
                case mDCv:
                    if (body.Length == 24)
                    {
                        Span<double> c = stackalloc double[8];
                        for (int i = 0; i < 8; i++) c[i] = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(i * 2)) * 0.00002;
                        (s.PngMeta ??= new PngMetadata()).MasteringDisplay = new PngMasteringDisplay(c[0], c[1], c[2], c[3], c[4], c[5], c[6], c[7],
                            BinaryPrimitives.ReadUInt32BigEndian(body.Slice(16)) * 0.0001, BinaryPrimitives.ReadUInt32BigEndian(body.Slice(20)) * 0.0001);
                    }
                    break;
                case cLLi:
                    if (body.Length == 8)
                        (s.PngMeta ??= new PngMetadata()).ContentLightLevel = new PngContentLightLevel(
                            BinaryPrimitives.ReadUInt32BigEndian(body) * 0.0001, BinaryPrimitives.ReadUInt32BigEndian(body.Slice(4)) * 0.0001);
                    break;
            }
        }

        /// <summary>A floating point number as PNG writes it in ASCII: digits, optional fraction and exponent.</summary>
        static bool TryParseFloat(ReadOnlySpan<byte> ascii, out double value)
        {
            value = 0;
            if (ascii.Length is 0 or > 80) return false;
            foreach (byte b in ascii)
                if (!(b is >= (byte)'0' and <= (byte)'9' or (byte)'+' or (byte)'-' or (byte)'.' or (byte)'e' or (byte)'E')) return false;
            return double.TryParse(Encoding.ASCII.GetString(ascii), System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
        }

        static PngSuggestedPalette? ReadSuggestedPalette(ReadOnlySpan<byte> body)
        {
            int nameEnd = body.Slice(0, Math.Min(body.Length, 80)).IndexOf((byte)0);
            if (nameEnd < 1 || body.Length < nameEnd + 2) return null;
            int depth = body[nameEnd + 1];
            if (depth != 8 && depth != 16) return null;
            var rest = body.Slice(nameEnd + 2);
            int size = depth == 8 ? 6 : 10;
            if (rest.Length % size != 0) return null;
            var entries = new PngSuggestedPaletteEntry[rest.Length / size];
            for (int i = 0; i < entries.Length; i++)
            {
                var e = rest.Slice(i * size, size);
                entries[i] = depth == 8
                    ? new PngSuggestedPaletteEntry(e[0], e[1], e[2], e[3], BinaryPrimitives.ReadUInt16BigEndian(e.Slice(4)))
                    : new PngSuggestedPaletteEntry(BinaryPrimitives.ReadUInt16BigEndian(e), BinaryPrimitives.ReadUInt16BigEndian(e.Slice(2)),
                        BinaryPrimitives.ReadUInt16BigEndian(e.Slice(4)), BinaryPrimitives.ReadUInt16BigEndian(e.Slice(6)), BinaryPrimitives.ReadUInt16BigEndian(e.Slice(8)));
            }
            return new PngSuggestedPalette(Encoding.Latin1.GetString(body.Slice(0, nameEnd)), depth, entries);
        }

        /// <summary>Parameters each pCAL equation type takes.</summary>
        public static int CalibrationParameterCount(PngCalibrationEquation equation) => equation switch
        {
            PngCalibrationEquation.Linear => 2,
            PngCalibrationEquation.Hyperbolic => 4,
            _ => 3,
        };

        static PngPixelCalibration? ReadPixelCalibration(ReadOnlySpan<byte> body)
        {
            // name, 0, x0, x1, equation type, parameter count, unit, 0, then the parameters separated by 0
            int nameEnd = body.Slice(0, Math.Min(body.Length, 80)).IndexOf((byte)0);
            if (nameEnd < 1 || body.Length < nameEnd + 12) return null;
            var rest = body.Slice(nameEnd + 1);
            int x0 = BinaryPrimitives.ReadInt32BigEndian(rest), x1 = BinaryPrimitives.ReadInt32BigEndian(rest.Slice(4));
            int equation = rest[8], count = rest[9];
            if (equation > 3 || x0 == x1 || count != CalibrationParameterCount((PngCalibrationEquation)equation)) return null;
            rest = rest.Slice(10);
            int unitEnd = rest.IndexOf((byte)0);
            if (unitEnd < 0) return null;
            string unit = Encoding.Latin1.GetString(rest.Slice(0, unitEnd));
            rest = rest.Slice(unitEnd + 1);
            var parameters = new double[count];
            for (int i = 0; i < count; i++)
            {
                int end = i == count - 1 ? rest.Length : rest.IndexOf((byte)0);
                if (end < 0 || !TryParseFloat(rest.Slice(0, end), out parameters[i])) return null;
                rest = rest.Slice(Math.Min(end + 1, rest.Length));
            }
            return new PngPixelCalibration(Encoding.Latin1.GetString(body.Slice(0, nameEnd)), x0, x1, (PngCalibrationEquation)equation, unit, parameters);
        }

        static PngTextEntry? ReadText(uint type, ReadOnlySpan<byte> body)
        {
            int keyEnd = body.Slice(0, Math.Min(body.Length, 80)).IndexOf((byte)0);
            if (keyEnd < 1) return null;
            string keyword = Encoding.Latin1.GetString(body.Slice(0, keyEnd));
            var rest = body.Slice(keyEnd + 1);
            if (type == tEXt) return new PngTextEntry(keyword, Encoding.Latin1.GetString(rest));
            if (type == zTXt)
            {
                if (rest.Length < 1 || rest[0] != 0) return null;
                var text = InflateBounded(rest.Slice(1));
                return text == null ? null : new PngTextEntry(keyword, Encoding.Latin1.GetString(text)) { Compressed = true };
            }
            // iTXt: compression flag, compression method, language tag, 0, translated keyword, 0, text
            if (rest.Length < 2 || rest[0] > 1 || (rest[0] == 1 && rest[1] != 0)) return null;
            bool compressed = rest[0] == 1;
            rest = rest.Slice(2);
            int langEnd = rest.IndexOf((byte)0);
            if (langEnd < 0) return null;
            string language = Encoding.ASCII.GetString(rest.Slice(0, langEnd));
            rest = rest.Slice(langEnd + 1);
            int transEnd = rest.IndexOf((byte)0);
            if (transEnd < 0) return null;
            string translated = Encoding.UTF8.GetString(rest.Slice(0, transEnd));
            rest = rest.Slice(transEnd + 1);
            string value;
            if (compressed)
            {
                var text = InflateBounded(rest);
                if (text == null) return null;
                value = Encoding.UTF8.GetString(text);
            }
            else value = Encoding.UTF8.GetString(rest);
            return new PngTextEntry(keyword, value) { International = true, Compressed = compressed, LanguageTag = language, TranslatedKeyword = translated };
        }
    }
}
