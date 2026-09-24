using System.Xml.Linq;
using EpubReview.Core.Model;

namespace EpubReview.Core.Import;

public static class PackageParser
{
    private static readonly HashSet<string> RecognizedMetadataElements = new()
    {
        "metadata", "title", "creator", "identifier", "language", "contributor",
        "publisher", "date", "meta", "link"
    };

    public static (PackageDocument? Document, List<Diagnostic> Diagnostics) Parse(
        ZipEntryRecord entry, string packageId)
    {
        var diagnostics = new List<Diagnostic>();
        var text = System.Text.Encoding.UTF8.GetString(entry.Bytes);
        XDocument doc;
        try
        {
            doc = XmlUtil.LoadWithLineInfo(text);
        }
        catch (System.Xml.XmlException ex)
        {
            diagnostics.Add(new Diagnostic("PACKAGE-XML-MALFORMED",
                ex.Message, DiagnosticSeverity.Error,
                new SourceLocation(entry.Name, ex.LineNumber, ex.LinePosition)));
            return (null, diagnostics);
        }

        var root = doc.Root;
        if (root?.Name.LocalName != "package" || root.Name.NamespaceName != XmlUtil.NsOpf.NamespaceName)
        {
            diagnostics.Add(new Diagnostic("PACKAGE-ROOT",
                "OPF root must be {http://www.idpf.org/2007/opf}package.",
                DiagnosticSeverity.Error, XmlUtil.Location(entry.Name, root)));
            return (null, diagnostics);
        }

        var manifestElements = root.Element(XmlUtil.NsOpf + "manifest")?
            .Elements(XmlUtil.NsOpf + "item") ?? Enumerable.Empty<XElement>();
        var manifest = new List<ManifestItem>();
        var idsSeen = new HashSet<string>();
        foreach (var item in manifestElements)
        {
            var itemDiags = new List<Diagnostic>();
            var id = item.Attribute("id")?.Value ?? "";
            var href = item.Attribute("href")?.Value ?? "";
            var mediaType = item.Attribute("media-type")?.Value ?? "";
            var fallback = item.Attribute("fallback")?.Value;
            var overlay = item.Attribute("media-overlay")?.Value;
            var props = item.Attribute("properties")?.Value;

            if (string.IsNullOrEmpty(id))
            {
                itemDiags.Add(new Diagnostic("MANIFEST-ITEM-NO-ID",
                    "manifest item is missing required id attribute.",
                    DiagnosticSeverity.Error, XmlUtil.Location(entry.Name, item)));
            }
            else if (!idsSeen.Add(id))
            {
                itemDiags.Add(new Diagnostic("MANIFEST-DUPLICATE-ID",
                    $"Duplicate manifest id '{id}'; both occurrences retained with locations.",
                    DiagnosticSeverity.Error, XmlUtil.Location(entry.Name, item)));
            }
            if (string.IsNullOrEmpty(href))
            {
                itemDiags.Add(new Diagnostic("MANIFEST-ITEM-NO-HREF",
                    $"manifest item '{id}' is missing href.",
                    DiagnosticSeverity.Error, XmlUtil.Location(entry.Name, item)));
            }
            if (string.IsNullOrEmpty(mediaType))
            {
                itemDiags.Add(new Diagnostic("MANIFEST-ITEM-NO-MEDIATYPE",
                    $"manifest item '{id}' is missing media-type; original representation kept.",
                    DiagnosticSeverity.Warning, XmlUtil.Location(entry.Name, item)));
            }

            manifest.Add(new ManifestItem(id, href, mediaType, fallback, overlay, props,
                XmlUtil.Location(entry.Name, item), itemDiags));
            diagnostics.AddRange(itemDiags);
        }

        var spineEl = root.Element(XmlUtil.NsOpf + "spine");
        var spine = new List<SpineItemRef>();
        int order = 0;
        foreach (var itemref in spineEl?.Elements(XmlUtil.NsOpf + "itemref") ?? Enumerable.Empty<XElement>())
        {
            var idref = itemref.Attribute("idref")?.Value ?? "";
            if (string.IsNullOrEmpty(idref))
            {
                diagnostics.Add(new Diagnostic("SPINE-ITEMREF-NO-IDREF",
                    "spine itemref without idref; position retained but cannot resolve.",
                    DiagnosticSeverity.Error, XmlUtil.Location(entry.Name, itemref)));
            }
            spine.Add(new SpineItemRef(order++, idref,
                itemref.Attribute("linear")?.Value,
                itemref.Attribute("properties")?.Value,
                XmlUtil.Location(entry.Name, itemref)));
        }

        var metadata = new List<MetadataItem>();
        var mdEl = root.Element(XmlUtil.NsOpf + "metadata");
        if (mdEl is not null)
        {
            foreach (var child in mdEl.Elements())
            {
                bool recognized = child.Name.NamespaceName == XmlUtil.NsDc.NamespaceName
                    ? RecognizedMetadataElements.Contains(child.Name.LocalName)
                    : child.Name.NamespaceName == XmlUtil.NsOpf.NamespaceName &&
                      RecognizedMetadataElements.Contains(child.Name.LocalName);

                var key = string.IsNullOrEmpty(child.Name.NamespaceName)
                    ? child.Name.LocalName
                    : $"{{{child.Name.NamespaceName}}}{child.Name.LocalName}";
                metadata.Add(new MetadataItem(
                    key,
                    child.Value.Trim(),
                    recognized,
                    child.ToString(SaveOptions.DisableFormatting),
                    XmlUtil.Location(entry.Name, child)));
            }
        }

        var unknownCount = metadata.Count(m => !m.Recognized);
        if (unknownCount > 0)
        {
            diagnostics.Add(new Diagnostic("METADATA-UNKNOWN-PRESERVED",
                $"{unknownCount} unrecognized metadata element(s) preserved verbatim for round-trip.",
                DiagnosticSeverity.Info,
                XmlUtil.Location(entry.Name, mdEl),
                new Dictionary<string, string> { ["count"] = unknownCount.ToString() }));
        }

        return (new PackageDocument(
            packageId, entry.Name, entry.Bytes, text, entry.Sha256,
            root.Attribute("unique-identifier")?.Value,
            root.Attribute("version")?.Value,
            root.Attribute(XNamespace.Xml + "lang")?.Value,
            root.Attribute("dir")?.Value,
            root.Attribute("id")?.Value,
            manifest, spine,
            spineEl?.Attribute("toc")?.Value,
            metadata, diagnostics), diagnostics);
    }
}
