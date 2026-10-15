using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;

namespace Aardvark.ImageCodecs
{
    /// <summary>
    /// Edits XMP packets (XMP specification part 1: RDF/XML serialization): sets and removes properties, keeps everything
    /// else, writes the packet wrapper with padding. Also splits packets for JPEG (part 3: standard and extended XMP).
    /// </summary>
    internal static class XmpEditor
    {
        const string Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
        const string XNs = "adobe:ns:meta/";
        const string XmlnsNs = "http://www.w3.org/2000/xmlns/";
        const string XmlNs = "http://www.w3.org/XML/1998/namespace";
        const string NoteNs = "http://ns.adobe.com/xmp/note/";

        static readonly Dictionary<string, string> PrefixUri = BuildPrefixes();

        static Dictionary<string, string> BuildPrefixes()
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (uri, prefix) in XmpData.KnownNamespaces) d.TryAdd(prefix, uri);
            return d;
        }

        // ------------------------------------------------------------------ keys

        /// <summary>Checks the form of a key ("p:Name", "p:Struct/q:Field", "p:*" when <paramref name="allowWildcard"/>).</summary>
        public static void CheckKey(string key, MetadataEdit edit, bool allowWildcard = false)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("empty XMP key");
            var parts = key.Split('/');
            for (int i = 0; i < parts.Length; i++)
            {
                var p = parts[i];
                int c = p.IndexOf(':');
                if (c <= 0 || c == p.Length - 1 || p.IndexOf(':', c + 1) >= 0 || p.Contains('['))
                    throw new ArgumentException($"bad XMP key '{key}' (expected prefix:Name or prefix:Struct/prefix:Field)");
                var local = p[(c + 1)..];
                if (local == "*") { if (!allowWildcard || parts.Length != 1) throw new ArgumentException($"bad XMP key '{key}'"); continue; }
                if (!IsXmlName(local)) throw new ArgumentException($"bad XMP property name '{local}'");
            }
        }

        static bool IsXmlName(string s)
        {
            try { XmlConvert.VerifyNCName(s); return true; }
            catch (XmlException) { return false; }
        }

        static string? UriOf(string prefix, Dictionary<string, string> ns)
            => ns.TryGetValue(prefix, out var u) ? u : PrefixUri.TryGetValue(prefix, out var k) ? k : null;

        /// <summary>The prefixes of the keys must name known or registered namespaces.</summary>
        public static void CheckPrefixes(MetadataPlan plan)
        {
            foreach (var op in plan.Xmp)
                if (op.Value != null)   // (removals also match the prefixes a file uses itself)
                foreach (var part in op.Key.Split('/'))
                {
                    var prefix = part[..part.IndexOf(':')];
                    if (UriOf(prefix, plan.Namespaces) == null) throw new ArgumentException($"XMP prefix '{prefix}' is unknown: register it with SetXmpNamespace");
                }
        }

        // ------------------------------------------------------------------ loading and writing

        internal static string Text(ReadOnlySpan<byte> packet)
        {
            int end = packet.Length;
            while (end > 0 && packet[end - 1] == 0) end--;
            packet = packet.Slice(0, end);
            string text;
            if (packet.Length >= 2 && (packet[0] == 0xFE && packet[1] == 0xFF || packet[0] == 0 && packet[1] == '<')) text = Encoding.BigEndianUnicode.GetString(packet);
            else if (packet.Length >= 2 && (packet[0] == 0xFF && packet[1] == 0xFE || packet[0] == '<' && packet[1] == 0)) text = Encoding.Unicode.GetString(packet);
            else text = Encoding.UTF8.GetString(packet);
            text = text.TrimStart('﻿');
            int lt = text.IndexOf('<');
            return lt < 0 ? "" : text.Substring(lt);
        }

        /// <summary>The packet as a document whose root is x:xmpmeta; null when it is not usable XML with an rdf:RDF.</summary>
        public static XmlDocument? Load(ReadOnlySpan<byte> packet)
        {
            var text = Text(packet);
            if (text.Length == 0) return null;
            var doc = new XmlDocument { XmlResolver = null, PreserveWhitespace = false };
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64 << 20,
                IgnoreComments = true, IgnoreProcessingInstructions = true, IgnoreWhitespace = true, CheckCharacters = false,
            };
            try
            {
                using var r = XmlReader.Create(new StringReader(text), settings);
                doc.Load(r);
            }
            catch (XmlException) { return null; }
            var rdf = FindRdf(doc);
            if (rdf == null) return null;
            if (doc.DocumentElement is { LocalName: "xmpmeta" } root && root.NamespaceURI == XNs) return doc;
            // a bare rdf:RDF (or something else around it): put it into x:xmpmeta
            var n = NewDocument();
            var meta = n.DocumentElement!;
            meta.RemoveAll();
            meta.SetAttribute("xmlns:x", XNs);
            meta.AppendChild(n.ImportNode(rdf, true));
            return n;
        }

        static XmlElement? FindRdf(XmlDocument doc)
        {
            foreach (XmlElement e in doc.GetElementsByTagName("RDF", Rdf)) return e;
            return null;
        }

        public static XmlDocument NewDocument()
        {
            var doc = new XmlDocument { XmlResolver = null };
            var meta = doc.CreateElement("x", "xmpmeta", XNs);
            meta.SetAttribute("xmlns:x", XNs);
            meta.SetAttribute("xmptk", XNs, "Aardvark.PixImage.IO");
            doc.AppendChild(meta);
            var rdf = doc.CreateElement("rdf", "RDF", Rdf);
            rdf.SetAttribute("xmlns:rdf", Rdf);
            meta.AppendChild(rdf);
            var d = doc.CreateElement("rdf", "Description", Rdf);
            d.SetAttribute("about", Rdf, "");
            rdf.AppendChild(d);
            return doc;
        }

        static string Xml(XmlDocument doc, bool indent)
        {
            var sb = new StringBuilder();
            var settings = new XmlWriterSettings { OmitXmlDeclaration = true, Indent = indent, IndentChars = " ", NewLineChars = "\n", Encoding = new UTF8Encoding(false), NewLineHandling = NewLineHandling.Entitize };
            using (var w = XmlWriter.Create(sb, settings)) doc.DocumentElement!.WriteTo(w);
            return sb.ToString();
        }

        /// <summary>The packet: xpacket wrapper, the document and <paramref name="padding"/> bytes of white space.</summary>
        public static byte[] Serialize(XmlDocument doc, int padding = 2048, bool indent = true)
        {
            var sb = new StringBuilder();
            sb.Append("<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n");
            sb.Append(Xml(doc, indent));
            sb.Append('\n');
            for (int i = 0; i < padding; i += 100) sb.Append(' ', Math.Min(99, padding - i)).Append('\n');
            sb.Append("<?xpacket end=\"w\"?>");
            return new UTF8Encoding(false).GetBytes(sb.ToString());
        }

        // ------------------------------------------------------------------ editing

        /// <summary>
        /// The packet with the plan applied: null when no XMP remains (or there was none and the plan writes none). A packet
        /// that cannot be parsed is kept as it is unless the plan writes XMP, which then replaces it.
        /// </summary>
        public static byte[]? Edit(byte[]? packet, MetadataPlan plan, out bool changed)
        {
            changed = false;
            if (!plan.HasXmpChanges) return packet;
            CheckPrefixes(plan);
            XmlDocument? doc = packet is { Length: > 0 } && !plan.ClearAll ? Load(packet) : null;
            if (doc == null)
            {
                if (!plan.CreatesXmp)
                {
                    if (plan.ClearAll && packet != null) { changed = true; return null; }
                    return packet;
                }
                doc = NewDocument();
            }
            var dom = new Dom(doc, plan.Namespaces);
            bool any = false;
            if (plan.ClearGps) any |= dom.RemoveWhere(MetadataPlan.IsGpsProperty);
            foreach (var op in plan.Xmp)
            {
                if (op.Value == null)
                {
                    if (op.Key.EndsWith(":*", StringComparison.Ordinal)) { var pre = op.Key[..^2]; any |= dom.RemoveWhere(k => k.StartsWith(pre + ":", StringComparison.Ordinal)); }
                    else any |= dom.Remove(op.Key);
                }
                else if (!op.IfPresent || dom.Exists(op.Key))
                {
                    dom.Set(op.Key, op.Value);
                    any = true;
                }
            }
            if (!any && packet != null && !plan.ClearAll) return packet;
            changed = true;
            if (dom.IsEmpty) return null;
            return Serialize(doc);
        }

        /// <summary>Merges the properties of a JPEG extended XMP part into its standard packet (xmpNote:HasExtendedXMP removed).</summary>
        public static byte[] MergeExtended(byte[] standard, byte[] extended)
        {
            var doc = Load(standard);
            var ext = Load(extended);
            if (doc == null || ext == null) return standard;
            var dom = new Dom(doc, new());
            dom.Remove("xmpNote:HasExtendedXMP");
            var target = dom.FirstDescription();
            foreach (var d in Descriptions(ext))
            {
                foreach (XmlAttribute a in d.Attributes)
                    if (IsPropertyAttribute(a)) { DeclareOn(target, a.Prefix, a.NamespaceURI); target.SetAttribute(a.LocalName, a.NamespaceURI, a.Value); }
                foreach (XmlNode c in d.ChildNodes)
                    if (c is XmlElement e) target.AppendChild(doc.ImportNode(e, true));
            }
            return Serialize(doc);
        }

        static IEnumerable<XmlElement> Descriptions(XmlDocument doc)
        {
            var rdf = FindRdf(doc);
            if (rdf == null) yield break;
            foreach (XmlNode n in rdf.ChildNodes)
                if (n is XmlElement d && d.LocalName == "Description" && d.NamespaceURI == Rdf) yield return d;
        }

        static bool IsPropertyAttribute(XmlAttribute a)
            => !string.IsNullOrEmpty(a.NamespaceURI) && a.NamespaceURI != Rdf && a.NamespaceURI != XmlnsNs && a.NamespaceURI != XmlNs && a.Prefix != "xmlns";

        /// <summary>Declares prefix → uri on the element unless it is already in scope there; returns the prefix to use.</summary>
        static string DeclareOn(XmlElement e, string prefix, string uri)
        {
            if (string.IsNullOrEmpty(prefix)) prefix = "ns";
            for (int k = 0; ; k++)
            {
                var p = k == 0 ? prefix : prefix + k;
                var bound = e.GetNamespaceOfPrefix(p);
                if (bound == uri) return p;
                if (string.IsNullOrEmpty(bound))
                {
                    e.SetAttribute("xmlns:" + p, uri);
                    return p;
                }
            }
        }

        /// <summary>Property access on an XMP document by "prefix:Name" keys (prefix as XmpData reports it).</summary>
        sealed class Dom
        {
            readonly XmlDocument _doc;
            readonly Dictionary<string, string> _ns;
            readonly Dictionary<string, string> _uriPrefix = new();

            public Dom(XmlDocument doc, Dictionary<string, string> ns)
            {
                _doc = doc; _ns = ns;
                foreach (var (p, u) in ns) _uriPrefix[u] = p;
            }

            string KeyPrefix(XmlNode n)
                => XmpData.KnownNamespaces.TryGetValue(n.NamespaceURI, out var p) ? p : _uriPrefix.TryGetValue(n.NamespaceURI, out var q) ? q : string.IsNullOrEmpty(n.Prefix) ? "ns" : n.Prefix;

            bool Is(XmlNode n, string prefix, string local) => n.LocalName == local && KeyPrefix(n) == prefix;

            static (string Prefix, string Local) Split(string part) { int c = part.IndexOf(':'); return (part[..c], part[(c + 1)..]); }

            public XmlElement FirstDescription()
            {
                foreach (var d in Descriptions(_doc)) return d;
                var rdf = FindRdf(_doc)!;
                var n = _doc.CreateElement("rdf", "Description", Rdf);
                n.SetAttribute("about", Rdf, "");
                rdf.AppendChild(n);
                return n;
            }

            /// <summary>The namespace URI to write a prefix with: the one the document already uses for it, else the registered / known one.</summary>
            string Uri(string prefix)
            {
                foreach (var d in Descriptions(_doc))
                {
                    foreach (XmlAttribute a in d.Attributes) if (IsPropertyAttribute(a) && KeyPrefix(a) == prefix) return a.NamespaceURI;
                    foreach (XmlNode c in d.ChildNodes) if (c is XmlElement e && KeyPrefix(e) == prefix) return e.NamespaceURI;
                }
                return UriOf(prefix, _ns) ?? throw new ArgumentException($"XMP prefix '{prefix}' is unknown");
            }

            /// <summary>The nodes holding the fields of a structure element (the element itself or its rdf:Description).</summary>
            static XmlElement Container(XmlElement e)
            {
                foreach (XmlNode c in e.ChildNodes)
                    if (c is XmlElement d && d.LocalName == "Description" && d.NamespaceURI == Rdf) return d;
                return e;
            }

            /// <summary>Top level property nodes (attributes or elements) for a key's first part.</summary>
            List<XmlNode> Top(string prefix, string local)
            {
                var r = new List<XmlNode>();
                foreach (var d in Descriptions(_doc))
                {
                    foreach (XmlAttribute a in d.Attributes) if (IsPropertyAttribute(a) && Is(a, prefix, local)) r.Add(a);
                    foreach (XmlNode c in d.ChildNodes) if (c is XmlElement e && Is(e, prefix, local)) r.Add(e);
                }
                return r;
            }

            /// <summary>Field nodes of a structure container.</summary>
            List<XmlNode> Fields(XmlElement container, string prefix, string local)
            {
                var r = new List<XmlNode>();
                foreach (XmlAttribute a in container.Attributes) if (IsPropertyAttribute(a) && Is(a, prefix, local)) r.Add(a);
                foreach (XmlNode c in container.ChildNodes) if (c is XmlElement e && Is(e, prefix, local)) r.Add(e);
                return r;
            }

            /// <summary>The nodes a key names (all occurrences).</summary>
            List<XmlNode> Resolve(string key)
            {
                var parts = key.Split('/');
                var (p0, l0) = Split(parts[0]);
                var nodes = Top(p0, l0);
                for (int i = 1; i < parts.Length; i++)
                {
                    var (p, l) = Split(parts[i]);
                    var next = new List<XmlNode>();
                    foreach (var n in nodes) if (n is XmlElement e) next.AddRange(Fields(Container(e), p, l));
                    nodes = next;
                }
                return nodes;
            }

            public bool Exists(string key) => Resolve(key).Count > 0;

            public bool Remove(string key)
            {
                var nodes = Resolve(key);
                foreach (var n in nodes)
                {
                    if (n is XmlAttribute a) a.OwnerElement?.Attributes.Remove(a);
                    else n.ParentNode?.RemoveChild(n);
                }
                return nodes.Count > 0;
            }

            public bool RemoveWhere(Func<string, bool> pred)
            {
                var victims = new List<XmlNode>();
                foreach (var d in Descriptions(_doc))
                {
                    foreach (XmlAttribute a in d.Attributes) if (IsPropertyAttribute(a) && pred(KeyPrefix(a) + ":" + a.LocalName)) victims.Add(a);
                    foreach (XmlNode c in d.ChildNodes) if (c is XmlElement e && pred(KeyPrefix(e) + ":" + e.LocalName)) victims.Add(e);
                }
                foreach (var n in victims)
                {
                    if (n is XmlAttribute a) a.OwnerElement?.Attributes.Remove(a);
                    else n.ParentNode?.RemoveChild(n);
                }
                return victims.Count > 0;
            }

            public bool IsEmpty
            {
                get
                {
                    foreach (var d in Descriptions(_doc))
                    {
                        foreach (XmlAttribute a in d.Attributes) if (IsPropertyAttribute(a)) return false;
                        foreach (XmlNode c in d.ChildNodes) if (c is XmlElement) return false;
                    }
                    return true;
                }
            }

            XmlElement NewElement(XmlElement scope, string prefix, string local)
            {
                var top = FirstDescription();
                var uri = Uri(prefix);
                // declarations go on the description so that the serialization stays readable
                var p = DeclareOn(top, prefix, uri);
                if (scope != top && scope.GetNamespaceOfPrefix(p) != uri) p = DeclareOn(scope, prefix, uri);
                return _doc.CreateElement(p, local, uri);
            }

            public void Set(string key, XmpValue value)
            {
                Remove(key);
                var parts = key.Split('/');
                var top = FirstDescription();
                XmlElement container = top;
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    var (p, l) = Split(parts[i]);
                    XmlElement? s = null;
                    foreach (var n in i == 0 ? Top(p, l) : Fields(container, p, l)) if (n is XmlElement e) { s = e; break; }
                    if (s == null)
                    {
                        s = NewElement(container, p, l);
                        s.SetAttribute("parseType", Rdf, "Resource");
                        container.AppendChild(s);
                    }
                    else NormalizeStructure(s);
                    container = Container(s);
                }
                var (fp, fl) = Split(parts[^1]);
                var el = NewElement(container, fp, fl);
                if (value.Array is XmpArrayType t)
                {
                    var arr = _doc.CreateElement("rdf", t.ToString(), Rdf);
                    for (int i = 0; i < value.Values.Length; i++)
                    {
                        var li = _doc.CreateElement("rdf", "li", Rdf);
                        if (t == XmpArrayType.Alt && i == 0) li.SetAttribute("lang", XmlNs, "x-default");
                        li.InnerText = value.Values[i];
                        arr.AppendChild(li);
                    }
                    el.AppendChild(arr);
                }
                else el.InnerText = value.Values.Length > 0 ? value.Values[0] : "";
                container.AppendChild(el);
            }

            /// <summary>A structure written with its fields as attributes becomes rdf:parseType="Resource" with field elements.</summary>
            void NormalizeStructure(XmlElement s)
            {
                if (Container(s) != s || s.GetAttribute("parseType", Rdf) == "Resource") return;
                var attrs = s.Attributes.Cast<XmlAttribute>().Where(IsPropertyAttribute).ToList();
                foreach (var a in attrs)
                {
                    s.Attributes.Remove(a);
                    var f = _doc.CreateElement(string.IsNullOrEmpty(a.Prefix) ? "ns" : a.Prefix, a.LocalName, a.NamespaceURI);
                    f.InnerText = a.Value;
                    s.AppendChild(f);
                }
                s.SetAttribute("parseType", Rdf, "Resource");
            }
        }

        // ------------------------------------------------------------------ JPEG (XMP part 3)

        internal static ReadOnlySpan<byte> JpegStandardId => "http://ns.adobe.com/xap/1.0/\0"u8;
        internal static ReadOnlySpan<byte> JpegExtendedId => "http://ns.adobe.com/xmp/extension/\0"u8;
        const int MaxStandard = 65533 - 29;
        const int MaxExtendedChunk = 65533 - 35 - 32 - 8;

        /// <summary>
        /// The APP1 payloads for a packet: one standard XMP segment, or, for packets that do not fit, a standard packet with
        /// xmpNote:HasExtendedXMP plus the extended part in chunks (GUID: MD5 of the extended serialization).
        /// </summary>
        public static List<byte[]> JpegSegments(byte[] packet)
        {
            var r = new List<byte[]>();
            if (packet.Length <= MaxStandard) { r.Add(Concat(JpegStandardId, packet)); return r; }
            var doc = Load(packet) ?? throw new ImageNotSupportedException("XMP packet larger than a JPEG segment and not parseable");
            var dom = new Dom(doc, new());
            dom.Remove("xmpNote:HasExtendedXMP");
            var compact = Serialize(doc, 0, false);
            if (compact.Length <= MaxStandard) { r.Add(Concat(JpegStandardId, compact)); return r; }

            var ext = NewDocument();
            ext.DocumentElement!.RemoveAttribute("xmptk", XNs);
            var extDesc = new Dom(ext, new()).FirstDescription();
            // move the largest top level properties out until the standard part fits (with room for the GUID note)
            var props = new List<(XmlNode Node, int Size)>();
            foreach (var d in Descriptions(doc))
            {
                foreach (XmlAttribute a in d.Attributes) if (IsPropertyAttribute(a)) props.Add((a, a.OuterXml.Length));
                foreach (XmlNode c in d.ChildNodes) if (c is XmlElement e) props.Add((e, Encoding.UTF8.GetByteCount(e.OuterXml)));
            }
            props.Sort((a, b) => b.Size.CompareTo(a.Size));
            int size = compact.Length + 200;
            foreach (var (node, sz) in props)
            {
                if (size <= MaxStandard) break;
                if (node is XmlAttribute a)
                {
                    DeclareOn(extDesc, a.Prefix, a.NamespaceURI);
                    extDesc.SetAttribute(a.LocalName, a.NamespaceURI, a.Value);
                    a.OwnerElement!.Attributes.Remove(a);
                }
                else
                {
                    DeclareOn(extDesc, node.Prefix, node.NamespaceURI);
                    extDesc.AppendChild(ext.ImportNode(node, true));
                    node.ParentNode!.RemoveChild(node);
                }
                size -= sz;
            }
            var extBytes = new UTF8Encoding(false).GetBytes(Xml(ext, false));
            var guid = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(extBytes));
            var top = dom.FirstDescription();
            DeclareOn(top, "xmpNote", NoteNs);
            top.SetAttribute("HasExtendedXMP", NoteNs, guid);
            var std = Serialize(doc, 0, false);
            if (std.Length > MaxStandard) throw new ImageNotSupportedException("XMP: the standard part does not fit into a JPEG segment");
            r.Add(Concat(JpegStandardId, std));
            for (int off = 0; off < extBytes.Length; off += MaxExtendedChunk)
            {
                int n = Math.Min(MaxExtendedChunk, extBytes.Length - off);
                var seg = new byte[35 + 32 + 8 + n];
                JpegExtendedId.CopyTo(seg);
                Encoding.ASCII.GetBytes(guid, seg.AsSpan(35));
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(seg.AsSpan(67), (uint)extBytes.Length);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(seg.AsSpan(71), (uint)off);
                extBytes.AsSpan(off, n).CopyTo(seg.AsSpan(75));
                r.Add(seg);
            }
            return r;
        }

        static byte[] Concat(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            var r = new byte[a.Length + b.Length];
            a.CopyTo(r);
            b.CopyTo(r.AsSpan(a.Length));
            return r;
        }
    }
}
