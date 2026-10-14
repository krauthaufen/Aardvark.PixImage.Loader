using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;

namespace PixCodec
{
    /// <summary>
    /// One XMP property. <see cref="Key"/> is "prefix:Name" with the usual prefix of the namespace (e.g. "exif:FNumber",
    /// "drone-dji:GimbalYawDegree") whatever prefix the file used; fields of structures are "a:B/c:D", items of arrays of
    /// structures "a:B[2]/c:D" (1 based). Arrays (Seq, Bag, Alt) have one value per item, the default language first.
    /// </summary>
    public sealed class XmpProperty
    {
        public string Key { get; }
        public string Namespace { get; }
        public IReadOnlyList<string> Values { get; }
        internal XmpProperty(string key, string ns, IReadOnlyList<string> values) { Key = key; Namespace = ns; Values = values; }
        /// <summary>The first value.</summary>
        public string Value => Values.Count > 0 ? Values[0] : "";
        public override string ToString() => Values.Count == 1 ? Value : string.Join("; ", Values);
    }

    /// <summary>The properties of one or more XMP packets.</summary>
    public sealed class XmpData
    {
        readonly List<XmpProperty> _props = new();
        readonly Dictionary<string, XmpProperty> _byKey = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<XmpProperty> Properties => _props;

        /// <summary>The packets' text (several when a JPEG carries extended XMP).</summary>
        public IReadOnlyList<string> Packets => _packets;
        readonly List<string> _packets = new();

        /// <summary>The property by key ("exif:DateTimeOriginal"), null if absent. Keys compare case-insensitively.</summary>
        public XmpProperty? Get(string key) => _byKey.TryGetValue(key, out var p) ? p : null;

        /// <summary>The first value of a property, or null.</summary>
        public string? this[string key] => Get(key)?.Value;

        /// <summary>Usual prefixes by namespace.</summary>
        public static IReadOnlyDictionary<string, string> KnownNamespaces => Known;

        static readonly Dictionary<string, string> Known = new()
        {
            ["adobe:ns:meta/"] = "x",
            ["http://www.w3.org/1999/02/22-rdf-syntax-ns#"] = "rdf",
            ["http://ns.adobe.com/xap/1.0/"] = "xmp",
            ["http://ns.adobe.com/xap/1.0/mm/"] = "xmpMM",
            ["http://ns.adobe.com/xap/1.0/rights/"] = "xmpRights",
            ["http://ns.adobe.com/xmp/note/"] = "xmpNote",
            ["http://purl.org/dc/elements/1.1/"] = "dc",
            ["http://ns.adobe.com/tiff/1.0/"] = "tiff",
            ["http://ns.adobe.com/exif/1.0/"] = "exif",
            ["http://cipa.jp/exif/1.0/"] = "exifEX",
            ["http://ns.adobe.com/exif/1.0/aux/"] = "aux",
            ["http://ns.adobe.com/photoshop/1.0/"] = "photoshop",
            ["http://ns.adobe.com/camera-raw-settings/1.0/"] = "crs",
            ["http://ns.adobe.com/lightroom/1.0/"] = "lr",
            ["http://iptc.org/std/Iptc4xmpCore/1.0/xmlns/"] = "Iptc4xmpCore",
            ["http://iptc.org/std/Iptc4xmpExt/2008-02-29/"] = "Iptc4xmpExt",
            ["http://www.dji.com/drone-dji/1.0/"] = "drone-dji",
            ["http://www.parrot.com/drone-parrot/1.0/"] = "drone-parrot",
            ["http://www.parrot.com/drone-parrot/1.1/"] = "drone-parrot",
            ["http://pix4d.com/camera/1.0/"] = "Camera",
            ["http://micasense.com/MicaSense/1.0/"] = "MicaSense",
            ["http://ns.google.com/photos/1.0/panorama/"] = "GPano",
            ["http://ns.google.com/photos/1.0/camera/"] = "GCamera",
            ["http://ns.google.com/photos/1.0/image/"] = "GImage",
            ["http://ns.google.com/photos/1.0/container/"] = "Container",
            ["http://ns.adobe.com/hdr-gain-map/1.0/"] = "hdrgm",
            ["http://ns.adobe.com/xap/1.0/g/img/"] = "xmpGImg",
            ["http://ns.adobe.com/pdf/1.3/"] = "pdf",
            ["http://www.metadataworkinggroup.com/schemas/regions/"] = "mwg-rs",
            ["http://ns.microsoft.com/photo/1.0/"] = "MicrosoftPhoto",
        };

        const string Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";

        internal XmpData() { }

        /// <summary>Parses an XMP packet (UTF-8 / UTF-16, with or without the xpacket wrapper); null when it is not XML.</summary>
        public static XmpData? Parse(ReadOnlySpan<byte> packet)
        {
            var x = new XmpData();
            return x.Add(packet) ? x : null;
        }

        /// <summary>Adds the properties of another packet (earlier values win).</summary>
        internal bool Add(ReadOnlySpan<byte> packet)
        {
            // trailing NULs / padding, and data before the first '<'
            int end = packet.Length;
            while (end > 0 && packet[end - 1] == 0) end--;
            packet = packet.Slice(0, end);
            string text;
            if (packet.Length >= 2 && (packet[0] == 0xFE && packet[1] == 0xFF || packet[0] == 0 && packet[1] == '<')) text = Encoding.BigEndianUnicode.GetString(packet);
            else if (packet.Length >= 2 && (packet[0] == 0xFF && packet[1] == 0xFE || packet[0] == '<' && packet[1] == 0)) text = Encoding.Unicode.GetString(packet);
            else text = Encoding.UTF8.GetString(packet);
            text = text.TrimStart('﻿');
            int lt = text.IndexOf('<');
            if (lt < 0) return false;
            text = text.Substring(lt);
            var doc = new XmlDocument { XmlResolver = null };
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64 << 20,
                IgnoreComments = true, IgnoreProcessingInstructions = true, CheckCharacters = false,
            };
            try
            {
                using var r = XmlReader.Create(new StringReader(text), settings);
                doc.Load(r);
            }
            catch (XmlException) { return false; }
            _packets.Add(text);
            foreach (XmlElement rdf in doc.GetElementsByTagName("RDF", Rdf))
                foreach (XmlNode n in rdf.ChildNodes)
                    if (n is XmlElement d && d.LocalName == "Description" && d.NamespaceURI == Rdf) Description(d, "");
            return true;
        }

        string Qualified(XmlNode n)
        {
            string prefix = Known.TryGetValue(n.NamespaceURI, out var p) ? p : string.IsNullOrEmpty(n.Prefix) ? "ns" : n.Prefix;
            return prefix + ":" + n.LocalName;
        }

        void Put(string key, string ns, List<string> values)
        {
            if (_byKey.ContainsKey(key)) return;
            var p = new XmpProperty(key, ns, values);
            _props.Add(p);
            _byKey[key] = p;
        }

        /// <summary>The attributes and child elements of a description / structure as properties under <paramref name="path"/>.</summary>
        void Description(XmlElement d, string path)
        {
            foreach (XmlAttribute a in d.Attributes)
            {
                if (a.NamespaceURI == Rdf || a.NamespaceURI == "http://www.w3.org/2000/xmlns/" || a.Prefix == "xmlns" || a.Name == "xmlns" || a.Prefix == "xml") continue;
                if (string.IsNullOrEmpty(a.NamespaceURI)) continue;
                Put(path + Qualified(a), a.NamespaceURI, new List<string> { a.Value });
            }
            foreach (XmlNode c in d.ChildNodes)
                if (c is XmlElement e) Property(e, path + Qualified(e));
        }

        void Property(XmlElement e, string key)
        {
            var res = e.GetAttribute("resource", Rdf);
            if (!string.IsNullOrEmpty(res)) { Put(key, e.NamespaceURI, new List<string> { res }); return; }
            if (e.GetAttribute("parseType", Rdf) == "Resource") { Description(e, key + "/"); return; }
            XmlElement? child = null;
            foreach (XmlNode c in e.ChildNodes) if (c is XmlElement ce) { child = ce; break; }
            if (child == null)
            {
                // shorthand structure (fields as attributes) or a simple value
                bool fields = false;
                foreach (XmlAttribute a in e.Attributes)
                    if (!string.IsNullOrEmpty(a.NamespaceURI) && a.NamespaceURI != Rdf && a.Prefix != "xmlns" && a.Prefix != "xml") fields = true;
                if (fields) Description(e, key + "/");
                else Put(key, e.NamespaceURI, new List<string> { e.InnerText.Trim() });
                return;
            }
            if (child.NamespaceURI == Rdf && child.LocalName is "Seq" or "Bag" or "Alt")
            {
                var values = new List<string>();
                int i = 0;
                foreach (XmlNode li in child.ChildNodes)
                {
                    if (li is not XmlElement le || le.LocalName != "li") continue;
                    i++;
                    XmlElement? inner = null;
                    foreach (XmlNode c in le.ChildNodes) if (c is XmlElement ce) { inner = ce; break; }
                    bool structure = inner != null || le.GetAttribute("parseType", Rdf) == "Resource" || HasFieldAttributes(le);
                    if (structure)
                    {
                        if (inner != null && inner.LocalName == "Description" && inner.NamespaceURI == Rdf) Description(inner, $"{key}[{i}]/");
                        else Description(le, $"{key}[{i}]/");
                        continue;
                    }
                    var v = le.InnerText.Trim();
                    // Alt: the default language first
                    if (child.LocalName == "Alt" && le.GetAttribute("xml:lang") == "x-default") values.Insert(0, v);
                    else values.Add(v);
                }
                if (values.Count > 0 || i == 0) Put(key, e.NamespaceURI, values);
                return;
            }
            if (child.NamespaceURI == Rdf && child.LocalName == "Description") { Description(child, key + "/"); return; }
            // a structure written without rdf:Description
            Description(e, key + "/");
        }

        static bool HasFieldAttributes(XmlElement e)
        {
            foreach (XmlAttribute a in e.Attributes)
                if (!string.IsNullOrEmpty(a.NamespaceURI) && a.NamespaceURI != Rdf && a.Prefix != "xmlns" && a.Prefix != "xml") return true;
            return false;
        }
    }
}
