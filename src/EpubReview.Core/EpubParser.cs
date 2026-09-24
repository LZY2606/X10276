using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace EpubReview.Core;

public sealed class EpubParser
{
    private static readonly XNamespace ContainerNs = "urn:oasis:names:tc:opendocument:xmlns:container";
    private static readonly XNamespace OpfNs = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace DcNs = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace XhtmlNs = "http://www.w3.org/1999/xhtml";
    private static readonly XNamespace NcxNs = "http://www.daisy.org/z3986/2005/ncx/";
    private static readonly XNamespace SmilNs = "http://www.w3.org/ns/SMIL";

    private readonly EpubZipReader zipReader = new();

    public EpubMaterial Import(byte[] bytes, string fileName, ZipBudget budget, string receivedAtInstant)
    {
        var (entries, zipDiagnostics, zipOutcome) = zipReader.Read(bytes, budget);
        var diagnostics = zipDiagnostics.ToList();
        var entryByPath = entries.ToDictionary(e => e.FullName, StringComparer.Ordinal);
        ContainerDocument? container = null;

        if (zipOutcome == AnalysisOutcome.Success)
        {
            if (!entryByPath.TryGetValue("META-INF/container.xml", out var containerEntry))
            {
                diagnostics.Add(Error("CONTAINER.MISSING", "META-INF/container.xml is required.", new SourceLocation("META-INF/container.xml", 0, 0)));
            }
            else
            {
                container = ParseContainer(containerEntry, diagnostics);
            }
        }

        var packages = new List<RawPackageDocument>();
        if (container is not null)
        {
            if (container.RootFiles.Count == 0)
            {
                diagnostics.Add(Error("CONTAINER.NO_ROOTFILE", "container.xml declares no rootfile.", container.Location));
            }

            foreach (var root in container.RootFiles)
            {
                if (!entryByPath.TryGetValue(root.FullPath, out var packageEntry))
                {
                    diagnostics.Add(Error("PACKAGE.MISSING", $"Rootfile '{root.FullPath}' does not exist in the ZIP.", root.Location, root.FullPath));
                    continue;
                }

                var package = ParsePackage(packageEntry, diagnostics);
                if (package is not null) packages.Add(package);
            }
        }

        var materialId = Hashing.StableId("material", $"{fileName}\0{Hashing.Sha256Hex(bytes)}\0{receivedAtInstant}");
        return new EpubMaterial(materialId, fileName, Hashing.Sha256Hex(bytes), bytes.Length, receivedAtInstant, entries, container, packages, diagnostics);
    }

    private ContainerDocument? ParseContainer(ZipEntryRecord entry, List<Diagnostic> diagnostics)
    {
        var raw = System.Text.Encoding.UTF8.GetString(entry.Bytes);
        if (!TryLoad(raw, entry.FullName, diagnostics, out var doc)) return null;

        var roots = doc.Descendants(ContainerNs + "rootfile").Select((element, index) =>
        {
            var location = Location(element, entry.FullName, $"//c:rootfile[{index + 1}]");
            var attrs = Attributes(element);
            var href = (string?)element.Attribute("full-path");
            var media = (string?)element.Attribute("media-type");
            if (string.IsNullOrWhiteSpace(href)) diagnostics.Add(Error("CONTAINER.ROOTFILE_HREF", "Rootfile requires full-path.", location, href));
            if (string.IsNullOrWhiteSpace(media)) diagnostics.Add(Error("CONTAINER.ROOTFILE_MEDIA_TYPE", "Rootfile requires media-type.", location, href));
            return new ContainerRootFile(media ?? string.Empty, ContainerPathResolver.NormalizeEntry(href ?? string.Empty), href ?? string.Empty, attrs, location);
        }).ToList();

        if (roots.Count > 1)
            diagnostics.Add(new Diagnostic("CONTAINER.MULTIPLE_PACKAGES", DiagnosticSeverity.Info, $"{roots.Count} package rootfiles were retained independently.", Location(doc.Root, entry.FullName, "//c:rootfiles"), null, "MULTI_PACKAGE"));

        return new ContainerDocument(raw, roots, Location(doc.Root, entry.FullName, "/container"));
    }

    private RawPackageDocument? ParsePackage(ZipEntryRecord entry, List<Diagnostic> diagnostics)
    {
        var raw = System.Text.Encoding.UTF8.GetString(entry.Bytes);
        if (!TryLoad(raw, entry.FullName, diagnostics, out var doc)) return null;
        var root = doc.Root;
        var ns = root.Name.Namespace;
        var packageId = Hashing.StableId("package", $"{entry.FullName}\0{Hashing.Sha256Hex(entry.Bytes)}");

        var manifest = root.Descendants(ns + "manifest").FirstOrDefault();
        if (manifest is null) diagnostics.Add(Error("PACKAGE.MANIFEST_MISSING", "Package manifest element is required.", Location(root, entry.FullName, "/*[local-name()='package']"), packageId));

        var items = manifest?.Elements(ns + "item")?.Select((element, index) =>
        {
            var location = Location(element, entry.FullName, $"//opf:item[{index + 1}]");
            var id = RequiredAttribute(element, "id", entry.FullName, diagnostics, packageId);
            var href = RequiredAttribute(element, "href", entry.FullName, diagnostics, id);
            var mediaType = RequiredAttribute(element, "media-type", entry.FullName, diagnostics, id);
            return new ManifestItemRecord(
                id,
                href,
                mediaType,
                (string?)element.Attribute("fallback"),
                (string?)element.Attribute("fallback-style"),
                (string?)element.Attribute("media-overlay"),
                (string?)element.Attribute("properties") ?? string.Empty,
                Attributes(element),
                location);
        }).ToList() ?? [];

        var spineElement = root.Descendants(ns + "spine").FirstOrDefault();
        if (spineElement is null) diagnostics.Add(Error("PACKAGE.SPINE_MISSING", "spine element is required.", Location(root, entry.FullName, "//opf:spine"), packageId));
        var spine = spineElement?.Elements(ns + "itemref")?.Select((element, index) =>
        {
            var idref = RequiredAttribute(element, "idref", entry.FullName, diagnostics, packageId);
            return new SpineRefRecord(idref, (string?)element.Attribute("linear") ?? "yes", index + 1, Attributes(element), Location(element, entry.FullName, $"//opf:spine/opf:itemref[{index + 1}]"));
        }).ToList() ?? [];

        var metadata = root.Descendants(ns + "metadata").Elements().Select((element, index) =>
        {
            var known = element.Name.Namespace == DcNs || element.Name.Namespace == OpfNs;
            var id = Hashing.StableId("metadata", $"{entry.FullName}\0{element.Name}\0{index}\0{element.Value}");
            if (!known || element.Name.LocalName is "meta")
            {
                diagnostics.Add(new Diagnostic("METADATA.UNKNOWN_RETAINED", DiagnosticSeverity.Info, $"Unknown metadata <{element.Name.LocalName}> was retained without interpretation.", Location(element, entry.FullName, $"//opf:metadata/*[{index + 1}]"), id, "UNKNOWN_METADATA_ROUNDTRIP"));
            }
            return new MetadataRecord(id, element.Name.NamespaceName, element.Name.LocalName, element.Value.Trim(), element.ToString(SaveOptions.DisableFormatting), Location(element, entry.FullName, $"//opf:metadata/*[{index + 1}]"));
        }).ToList();

        return new RawPackageDocument(
            packageId,
            entry.FullName,
            raw,
            (string?)root.Attribute("unique-identifier"),
            items,
            spine,
            metadata,
            Attributes(root),
            Location(root, entry.FullName, "/opf:package"));
    }

    public static IReadOnlyCollection<string> ExtractAnchors(byte[] bytes, string mediaType)
    {
        if (mediaType != "application/xhtml+xml") return [];
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        if (!TryLoad(text, "<resource>", [], out var doc)) return [];
        return doc.Descendants().Select(e => (string?)e.Attribute("id")).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!).ToHashSet(StringComparer.Ordinal);
    }

    public static IReadOnlyList<(string Relation, string Target, SourceLocation Location)> ExtractXhtmlLinks(XDocument doc, string entryPath)
    {
        var result = new List<(string, string, SourceLocation)>();
        foreach (var element in doc.Descendants())
        {
            var href = (string?)element.Attribute("href");
            var src = (string?)element.Attribute("src");
            var post = element.Name.LocalName == "a" ? href : null;
            if (!string.IsNullOrWhiteSpace(post)) result.Add(("link", post, Location(element, entryPath, $"//a[@href='{post}']")));
            if (!string.IsNullOrWhiteSpace(src)) result.Add((element.Name.LocalName == "img" ? "embedded-image" : "media-source", src, Location(element, entryPath, $"//*[@src='{src}']")));
        }
        return result;
    }

    public static bool TryLoad(string xml, string entryPath, List<Diagnostic> diagnostics, out XDocument document)
    {
        try
        {
            document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
            return true;
        }
        catch (XmlException ex)
        {
            diagnostics.Add(Error("XML.PARSE", ex.Message, new SourceLocation(entryPath, ex.LineNumber, ex.LinePosition)));
            document = new XDocument();
            return false;
        }
    }

    public static SourceLocation Location(XObject? obj, string entry, string? xpath = null)
    {
        var info = obj as IXmlLineInfo;
        return new SourceLocation(entry, info?.LineNumber ?? 0, info?.LinePosition ?? 0, xpath);
    }

    public static List<RawAttribute> Attributes(XElement element) =>
        element.Attributes().Select(a => new RawAttribute(a.Name.ToString(), a.Value)).ToList();

    private static string RequiredAttribute(XElement element, string name, string entry, List<Diagnostic> diagnostics, string? subject)
    {
        var value = (string?)element.Attribute(name);
        if (string.IsNullOrWhiteSpace(value))
            diagnostics.Add(Error($"PACKAGE.REQUIRED_{name.ToUpperInvariant()}", $"Required attribute '{name}' is missing.", Location(element, entry), subject));
        return value ?? string.Empty;
    }

    private static Diagnostic Error(string code, string message, SourceLocation location, string? subject = null, string? rule = null) =>
        new(code, DiagnosticSeverity.Error, message, location, subject, rule);
}
