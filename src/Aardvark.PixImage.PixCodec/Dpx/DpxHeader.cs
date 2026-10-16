using System;
using System.Collections.Generic;
using System.Globalization;

namespace Aardvark.ImageCodecs
{
    /// <summary>DPX image element descriptor (SMPTE 268M table 1): the components of an element and their order.</summary>
    public enum DpxDescriptor : byte
    {
        /// <summary>User defined, or an unspecified single component.</summary>
        UserDefined = 0,
        Red = 1,
        Green = 2,
        Blue = 3,
        Alpha = 4,
        Luma = 6,
        /// <summary>Cb, Cr, each subsampled by two (the chroma of a 4:2:2 luma element).</summary>
        ColorDifference = 7,
        Depth = 8,
        CompositeVideo = 9,
        Rgb = 50,
        Rgba = 51,
        Abgr = 52,
        /// <summary>Cb, Y, Cr, Y (4:2:2, SMPTE 125M).</summary>
        CbYCrY = 100,
        /// <summary>Cb, Y, A, Cr, Y, A (4:2:2:4).</summary>
        CbYACrYA = 101,
        /// <summary>Cb, Y, Cr (4:4:4).</summary>
        CbYCr = 102,
        /// <summary>Cb, Y, Cr, A (4:4:4:4).</summary>
        CbYCrA = 103,
        UserDefined2 = 150,
        UserDefined3 = 151,
        UserDefined4 = 152,
        UserDefined5 = 153,
        UserDefined6 = 154,
        UserDefined7 = 155,
        UserDefined8 = 156,
        /// <summary>The field holds the undefined value FF.</summary>
        Undefined = 255,
    }

    /// <summary>DPX transfer characteristic (SMPTE 268M table 5A). Values outside the list are reserved codes as stored.</summary>
    public enum DpxTransfer : byte
    {
        UserDefined = 0,
        /// <summary>Printing density: logarithmic film scans (the classic 10 bit Cineon / DPX log encoding).</summary>
        PrintingDensity = 1,
        Linear = 2,
        Logarithmic = 3,
        UnspecifiedVideo = 4,
        Smpte274M = 5,
        ItuR709 = 6,
        ItuR601BG = 7,
        ItuR601M = 8,
        CompositeNtsc = 9,
        CompositePal = 10,
        DepthLinear = 11,
        DepthHomogeneous = 12,
        /// <summary>The field holds the undefined value FF.</summary>
        Undefined = 255,
    }

    /// <summary>DPX colorimetric specification (SMPTE 268M table 5B). Values outside the list are reserved codes as stored.</summary>
    public enum DpxColorimetric : byte
    {
        UserDefined = 0,
        PrintingDensity = 1,
        /// <summary>Code 2 ("not applicable", paired with the linear transfer characteristic).</summary>
        NotApplicable = 2,
        /// <summary>Code 3 ("not applicable", paired with the logarithmic transfer characteristic).</summary>
        NotApplicable3 = 3,
        UnspecifiedVideo = 4,
        Smpte274M = 5,
        ItuR709 = 6,
        ItuR601BG = 7,
        ItuR601M = 8,
        CompositeNtsc = 9,
        CompositePal = 10,
        /// <summary>The field holds the undefined value FF.</summary>
        Undefined = 255,
    }

    /// <summary>DPX component packing (SMPTE 268M table 3B).</summary>
    public enum DpxPacking : ushort
    {
        /// <summary>Components packed into 32-bit words without gaps, lines start on a word boundary.</summary>
        Packed = 0,
        /// <summary>Filled to 32-bit words (10 bit: three per word) or 16-bit words (12 bit), padding in the low bits. The common layout.</summary>
        FilledA = 1,
        /// <summary>Filled like <see cref="FilledA"/>, padding in the high bits (the layout of the 1994 standard text).</summary>
        FilledB = 2,
    }

    /// <summary>One image element of a DPX file (SMPTE 268M field 21): an independent picture of one or more components.</summary>
    public sealed record DpxImageElement
    {
        /// <summary>Position in the header (0..7).</summary>
        public int Index { get; init; }
        /// <summary>Data sign: true for signed (two's complement) integer components.</summary>
        public bool Signed { get; init; }
        /// <summary>Reference low data code value, null when undefined.</summary>
        public uint? ReferenceLowCode { get; init; }
        /// <summary>Reference low quantity represented, null when undefined.</summary>
        public float? ReferenceLowQuantity { get; init; }
        /// <summary>Reference high data code value, null when undefined.</summary>
        public uint? ReferenceHighCode { get; init; }
        /// <summary>Reference high quantity represented, null when undefined.</summary>
        public float? ReferenceHighQuantity { get; init; }
        public DpxDescriptor Descriptor { get; init; }
        /// <summary>The transfer characteristic code; <see cref="ImageInfo.Transfer"/> says what it means for the decoded values.</summary>
        public DpxTransfer Transfer { get; init; }
        public DpxColorimetric Colorimetric { get; init; }
        /// <summary>Bits per component: 1, 8, 10, 12, 16 (integer), 32 or 64 (IEEE float).</summary>
        public int BitDepth { get; init; }
        public DpxPacking Packing { get; init; }
        /// <summary>Encoding field: 1 is run length encoded.</summary>
        public int Encoding { get; init; }
        /// <summary>Offset of the element's data from the start of the file, null when undefined.</summary>
        public uint? DataOffset { get; init; }
        /// <summary>Padding bytes at the end of each line, null when undefined.</summary>
        public uint? EndOfLinePadding { get; init; }
        /// <summary>Padding bytes at the end of the element, null when undefined.</summary>
        public uint? EndOfImagePadding { get; init; }
        public string Description { get; init; } = "";

        /// <summary>Whether the element is run length encoded.</summary>
        public bool IsRle => Encoding == 1;
    }

    /// <summary>
    /// The header of a DPX file (SMPTE 268M): file, image, image source, motion-picture film and television information. Numeric
    /// fields holding the standard's "undefined" value (all bits set) are null, undefined strings are empty. Read with
    /// <see cref="Dpx.GetHeader"/>; passed in <see cref="DpxEncodeOptions.Header"/>, the descriptive fields are written to a new
    /// file (the structural ones - size, elements, offsets, orientation, version - are always those of the encoded image).
    /// </summary>
    public sealed record DpxHeader
    {
        // ---------------------------------------------------------------- file information
        /// <summary>Byte order of the file: "SDPX" (most significant byte first) or "XPDS".</summary>
        public bool BigEndian { get; init; } = true;
        /// <summary>Offset to the image data (field 2).</summary>
        public uint ImageDataOffset { get; init; }
        /// <summary>Version string, e.g. "V1.0" or "V2.0".</summary>
        public string Version { get; init; } = "";
        /// <summary>Total file size as recorded (field 4).</summary>
        public uint? FileSize { get; init; }
        /// <summary>Ditto key: 0 = same as the previous frame, 1 = new.</summary>
        public uint? DittoKey { get; init; }
        public uint? GenericHeaderSize { get; init; }
        public uint? IndustryHeaderSize { get; init; }
        public uint? UserDataSize { get; init; }
        public string ImageFileName { get; init; } = "";
        /// <summary>Creation date / time as stored, "yyyy:mm:dd:hh:mm:ssLTZ" (see <see cref="CreationTime"/>).</summary>
        public string CreationDateTime { get; init; } = "";
        public string Creator { get; init; } = "";
        public string Project { get; init; } = "";
        public string Copyright { get; init; } = "";
        /// <summary>Encryption key; null (FFFFFFFF) for unencrypted data.</summary>
        public uint? EncryptionKey { get; init; }

        // ---------------------------------------------------------------- image information
        /// <summary>DPX orientation code 0..7 (table 2) as stored; <see cref="ImageInfo.Orientation"/> gives the EXIF equivalent.</summary>
        public int Orientation { get; init; }
        /// <summary>Pixels per line as stored (before the orientation is applied).</summary>
        public int PixelsPerLine { get; init; }
        /// <summary>Lines per image element as stored.</summary>
        public int Lines { get; init; }
        public IReadOnlyList<DpxImageElement> Elements { get; init; } = Array.Empty<DpxImageElement>();

        // ---------------------------------------------------------------- image source information
        public uint? XOffset { get; init; }
        public uint? YOffset { get; init; }
        public float? XCenter { get; init; }
        public float? YCenter { get; init; }
        public uint? XOriginalSize { get; init; }
        public uint? YOriginalSize { get; init; }
        public string SourceFileName { get; init; } = "";
        public string SourceDateTime { get; init; } = "";
        public string InputDevice { get; init; } = "";
        public string InputDeviceSerial { get; init; } = "";
        /// <summary>Border validity XL, XR, YT, YB, null when undefined.</summary>
        public (ushort Left, ushort Right, ushort Top, ushort Bottom)? Border { get; init; }
        /// <summary>Pixel aspect ratio horizontal : vertical, null when undefined.</summary>
        public (uint Horizontal, uint Vertical)? PixelAspectRatio { get; init; }
        /// <summary>Size of the scanned optical image in millimetres (DPX 2.0).</summary>
        public float? XScannedSize { get; init; }
        /// <inheritdoc cref="XScannedSize"/>
        public float? YScannedSize { get; init; }

        // ---------------------------------------------------------------- motion-picture film information
        public string FilmManufacturerId { get; init; } = "";
        public string FilmType { get; init; } = "";
        public string PerfsOffset { get; init; } = "";
        public string FilmPrefix { get; init; } = "";
        public string FilmCount { get; init; } = "";
        public string FilmFormat { get; init; } = "";
        public uint? FramePosition { get; init; }
        public uint? SequenceLength { get; init; }
        public uint? HeldCount { get; init; }
        /// <summary>Frame rate of the original (frames per second).</summary>
        public float? FrameRate { get; init; }
        public float? ShutterAngle { get; init; }
        public string FrameId { get; init; } = "";
        public string SlateInfo { get; init; } = "";

        // ---------------------------------------------------------------- television information
        /// <summary>SMPTE 12M time code, BCD hh mm ss ff from the high nibble down (see <see cref="TimeCodeText"/>).</summary>
        public uint? TimeCode { get; init; }
        public uint? UserBits { get; init; }
        public byte? Interlace { get; init; }
        public byte? FieldNumber { get; init; }
        /// <summary>Video signal standard (table 4).</summary>
        public byte? VideoSignalStandard { get; init; }
        public float? HorizontalSamplingRate { get; init; }
        public float? VerticalSamplingRate { get; init; }
        /// <summary>Temporal sampling rate or frame rate (Hz).</summary>
        public float? TemporalSamplingRate { get; init; }
        public float? TimeOffset { get; init; }
        /// <summary>Video gamma: the exponent g of Y = X^(1/g).</summary>
        public float? Gamma { get; init; }
        public float? BlackLevel { get; init; }
        public float? BlackGain { get; init; }
        public float? BreakPoint { get; init; }
        public float? WhiteLevel { get; init; }
        public float? IntegrationTime { get; init; }

        // ---------------------------------------------------------------- user data
        /// <summary>User identification of the user defined data section.</summary>
        public string UserId { get; init; } = "";
        /// <summary>The user defined data after the 32 byte user identification, null when there is none.</summary>
        public byte[]? UserData { get; init; }

        /// <summary>The creation date / time, null when missing or not parseable.</summary>
        public DateTimeOffset? CreationTime => DpxHeaderIo.ParseDate(CreationDateTime, out var t) ? t : null;

        /// <summary>The time code as "hh:mm:ss:ff", null when undefined.</summary>
        public string? TimeCodeText => TimeCode is uint t ? FormatTimeCode(t) : null;

        /// <summary>Formats a BCD time code word as "hh:mm:ss:ff".</summary>
        public static string FormatTimeCode(uint t)
            => string.Create(CultureInfo.InvariantCulture, $"{t >> 28 & 15}{t >> 24 & 15}:{t >> 20 & 15}{t >> 16 & 15}:{t >> 12 & 15}{t >> 8 & 15}:{t >> 4 & 15}{t & 15}");

        /// <summary>Builds a BCD time code word (hours 0..99, minutes and seconds 0..59, frames 0..99).</summary>
        public static uint MakeTimeCode(int hours, int minutes, int seconds, int frames)
        {
            static uint Bcd(int v, int max, string name)
                => v < 0 || v > max ? throw new ArgumentOutOfRangeException(name) : (uint)(v / 10 << 4 | v % 10);
            return Bcd(hours, 99, nameof(hours)) << 24 | Bcd(minutes, 59, nameof(minutes)) << 16 | Bcd(seconds, 59, nameof(seconds)) << 8 | Bcd(frames, 99, nameof(frames));
        }

        /// <summary>Formats a date in the DPX form "yyyy:mm:dd:hh:mm:ssLTZ" (LTZ "Z" or "+hhmm").</summary>
        public static string FormatDate(DateTimeOffset t)
        {
            var o = t.Offset;
            string tz = o == TimeSpan.Zero ? "Z" : (o < TimeSpan.Zero ? "-" : "+") + o.Duration().ToString("hhmm", CultureInfo.InvariantCulture);
            return t.ToString("yyyy:MM:dd:HH:mm:ss", CultureInfo.InvariantCulture) + tz;
        }
    }

    internal static class DpxHeaderIo
    {
        /// <summary>
        /// Parses "yyyy:mm:dd:hh:mm:ss" with an optional zone ("Z", "+hh", "+hhmm", "+hh:mm", also with a ':' before it, and
        /// letters such as "UTC" / "EST" which are taken as unknown offsets except UTC/GMT/Z). Separators may also be '-', 'T' or ' '
        /// (what some writers use). Without a zone the time is taken as UTC.
        /// </summary>
        public static bool ParseDate(string s, out DateTimeOffset t)
        {
            t = default;
            s = s.Trim();
            if (s.Length < 10) return false;
            int p = 0;
            bool Num(int digits, out int v)
            {
                v = 0;
                if (p + digits > s.Length) return false;
                for (int i = 0; i < digits; i++)
                {
                    char c = s[p + i];
                    if (c < '0' || c > '9') return false;
                    v = v * 10 + (c - '0');
                }
                p += digits;
                return true;
            }
            bool Sep() { if (p < s.Length && s[p] is ':' or '-' or 'T' or ' ' or '/') { p++; return true; } return false; }
            if (!Num(4, out int y) || !Sep() || !Num(2, out int mo) || !Sep() || !Num(2, out int d)) return false;
            int h = 0, mi = 0, se = 0;
            if (Sep() && Num(2, out h) && Sep() && Num(2, out mi) && p < s.Length && s[p] == ':') { p++; Num(2, out se); }
            if (y < 1 || mo is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, mo) || h > 23 || mi > 59 || se > 60) return false;
            var offset = TimeSpan.Zero;
            var rest = s.Substring(p).TrimStart(':', ' ');
            if (rest.Length > 0 && rest[0] is '+' or '-')
            {
                int sign = rest[0] == '-' ? -1 : 1;
                var digits = rest.Substring(1).Replace(":", "");
                if (digits.Length >= 2 && int.TryParse(digits.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int oh))
                {
                    int om = digits.Length >= 4 && int.TryParse(digits.AsSpan(2, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int m2) ? m2 : 0;
                    if (oh <= 14 && om < 60) offset = TimeSpan.FromMinutes(sign * (oh * 60 + om));
                }
            }
            t = new DateTimeOffset(y, mo, d, h, mi, Math.Min(se, 59), offset);
            return true;
        }
    }
}
