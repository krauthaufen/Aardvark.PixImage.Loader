using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Aardvark.ImageCodecs
{
    /// <summary>One IPTC-IIM dataset (record:dataset, e.g. 2:25 Keywords).</summary>
    public sealed class IptcEntry
    {
        public int Record { get; }
        public int Dataset { get; }
        public byte[] Data { get; }
        readonly bool _utf8;
        internal IptcEntry(int record, int dataset, byte[] data, bool utf8) { Record = record; Dataset = dataset; Data = data; _utf8 = utf8; }

        /// <summary>The dataset's name from the IIM 4.2 specification, or "2:123".</summary>
        public string Name => IptcData.Names.TryGetValue((Record, Dataset), out var n) ? n : $"{Record}:{Dataset}";

        /// <summary>The value as text (UTF-8 when the file says so, else UTF-8 if valid, else Latin-1).</summary>
        public string Text => (_utf8 || System.Text.Unicode.Utf8.IsValid(Data) ? Encoding.UTF8 : Encoding.Latin1).GetString(Data).TrimEnd('\0', ' ');

        public override string ToString() => Text;
    }

    /// <summary>IPTC-IIM data (JPEG APP13 Photoshop resources, TIFF tag 33723).</summary>
    public sealed class IptcData
    {
        readonly List<IptcEntry> _entries = new();
        public IReadOnlyList<IptcEntry> Entries => _entries;

        /// <summary>All values of a dataset by name ("Keywords"), in file order.</summary>
        public IReadOnlyList<string> GetAll(string name)
        {
            var r = new List<string>();
            foreach (var e in _entries) if (string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) r.Add(e.Text);
            return r;
        }

        /// <summary>The first value of a dataset by name, or null.</summary>
        public string? this[string name]
        {
            get
            {
                foreach (var e in _entries) if (string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) return e.Text;
                return null;
            }
        }

        /// <summary>Parses IIM datasets (tag 0x1C, record, dataset, length); null if there are none.</summary>
        public static IptcData? Parse(ReadOnlySpan<byte> d)
        {
            var r = new IptcData();
            bool utf8 = false;
            int p = 0;
            while (p + 5 <= d.Length)
            {
                if (d[p] != 0x1C) { p++; continue; }   // tolerate padding
                int rec = d[p + 1], ds = d[p + 2];
                int len = BinaryPrimitives.ReadUInt16BigEndian(d.Slice(p + 3));
                p += 5;
                if ((len & 0x8000) != 0)
                {
                    // extended length: the low bits give the size of the length field
                    int n = len & 0x7FFF;
                    if (n > 4 || p + n > d.Length) break;
                    len = 0;
                    for (int k = 0; k < n; k++) len = len << 8 | d[p + k];
                    p += n;
                }
                if (len < 0 || p + len > d.Length) break;
                var data = d.Slice(p, len).ToArray();
                p += len;
                // 1:90 coded character set: ESC % G is UTF-8
                if (rec == 1 && ds == 90 && data.Length >= 3 && data[0] == 0x1B && data[1] == '%' && data[2] == 'G') utf8 = true;
                r._entries.Add(new IptcEntry(rec, ds, data, utf8));
            }
            return r._entries.Count > 0 ? r : null;
        }

        /// <summary>The IPTC block inside Photoshop image resources ("8BIM" resource 0x0404).</summary>
        internal static ReadOnlySpan<byte> FromPhotoshopResources(ReadOnlySpan<byte> d)
        {
            int p = 0;
            while (p + 12 <= d.Length)
            {
                if (!d.Slice(p, 4).SequenceEqual("8BIM"u8)) break;
                int id = BinaryPrimitives.ReadUInt16BigEndian(d.Slice(p + 4));
                int nameLen = d[p + 6];
                int q = p + 6 + ((nameLen + 2) & ~1);   // Pascal string padded to even length
                if (q + 4 > d.Length) break;
                long size = BinaryPrimitives.ReadUInt32BigEndian(d.Slice(q));
                q += 4;
                if (q + size > d.Length) break;
                if (id == 0x0404) return d.Slice(q, (int)size);
                p = q + (int)((size + 1) & ~1L);
            }
            return default;
        }

        internal static readonly Dictionary<(int, int), string> Names = new()
        {
            [(1, 90)] = "CodedCharacterSet", [(2, 0)] = "RecordVersion", [(2, 3)] = "ObjectTypeReference",
            [(2, 4)] = "ObjectAttributeReference", [(2, 5)] = "ObjectName", [(2, 7)] = "EditStatus", [(2, 10)] = "Urgency",
            [(2, 12)] = "SubjectReference", [(2, 15)] = "Category", [(2, 20)] = "SupplementalCategories",
            [(2, 22)] = "FixtureIdentifier", [(2, 25)] = "Keywords", [(2, 26)] = "ContentLocationCode",
            [(2, 27)] = "ContentLocationName", [(2, 30)] = "ReleaseDate", [(2, 35)] = "ReleaseTime",
            [(2, 37)] = "ExpirationDate", [(2, 38)] = "ExpirationTime", [(2, 40)] = "SpecialInstructions",
            [(2, 42)] = "ActionAdvised", [(2, 45)] = "ReferenceService", [(2, 47)] = "ReferenceDate",
            [(2, 50)] = "ReferenceNumber", [(2, 55)] = "DateCreated", [(2, 60)] = "TimeCreated",
            [(2, 62)] = "DigitalCreationDate", [(2, 63)] = "DigitalCreationTime", [(2, 65)] = "OriginatingProgram",
            [(2, 70)] = "ProgramVersion", [(2, 75)] = "ObjectCycle", [(2, 80)] = "By-line", [(2, 85)] = "By-lineTitle",
            [(2, 90)] = "City", [(2, 92)] = "Sub-location", [(2, 95)] = "Province-State",
            [(2, 100)] = "Country-PrimaryLocationCode", [(2, 101)] = "Country-PrimaryLocationName",
            [(2, 103)] = "OriginalTransmissionReference", [(2, 105)] = "Headline", [(2, 110)] = "Credit",
            [(2, 115)] = "Source", [(2, 116)] = "CopyrightNotice", [(2, 118)] = "Contact", [(2, 120)] = "Caption-Abstract",
            [(2, 122)] = "Writer-Editor", [(2, 130)] = "ImageType", [(2, 131)] = "ImageOrientation",
            [(2, 135)] = "LanguageIdentifier",
        };
    }
}
