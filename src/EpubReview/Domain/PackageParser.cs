using System.Xml.Linq;

namespace EpubReview.Domain;

public sealed record ManifestItem(
    string Id, string Href, string? ResolvedPath, string MediaType,
    string? FallbackId, string? Properties, string RawXml, SourceLocation? Location)
{
    public string StableId => Hashing.StableId("item", Id + "|" + Href + "|" + MediaType);
}

public sealed record SpineItem(string Idref, int Index, bool Linear, string? ResolvedPath, SourceLocation? Location)
{
    public string StableId => Hashing.StableId("spine", Index + "|" + Idref);
}

public sealed record UnknownMetadata(string LocalName, string Namespace, string RawXml, SourceLocation? Location)
{
    public string StableId => Hashing.StableId("meta", Namespace + "|" + LocalName + "|" + RawXml);
}

public sealed record PackageDocument(
    string PackagePath,
    string PackageDir,
    string? UniqueIdentifier,
    IReadOnlyList<ManifestItem> Manifest,
    IReadOnlyList<SpineItem> Spine,
    IReadOnlyList<UnknownMetadata> UnknownMetadata,
    IReadOnlyDictionary<string, string> KnownMetadata,
    List<Diagnostic> Diagnostics,
    string RawXmlHash)
{
    public string StableId => Hashing.StableId("pkg", PackagePath + "|" + RawXmlHash);
}

public static class PackageParser
{
    private static readonly HashSet<string> KnownMetadataNames = new(StringComparer.Ordinal)
    {
        "identifier", "title", "language", "creator", "contributor", "publisher",
        "date", "subject", "description", "type", "format", "source", "relation",
        "coverage", "rights", "meta", "link"
    };

    public static PackageDocument Parse(EpubContainer container, string packagePath)
    {
        var diagnostics = new List<Diagnostic>();
        var entry = container.Find(packagePath);
        if (entry is null)
        {
            diagnostics.Add(new Diagnostic("package-file-missing", "error",
                $"Package document '{packagePath}' declared in container.xml is absent from the container.",
                new SourceLocation(packagePath, null, null, null)));
            throw new ContainerCorruptException($"package '{packagePath}' missing");
        }

        var doc = XmlHelper.LoadWithLineInfo(entry.Bytes, packagePath, diagnostics, "package-xml-corrupt");
        var root = doc.Root ?? throw new ContainerCorruptException("empty package document");
        var packageDir = PathResolver.BaseDirOf(packagePath);

        var manifest = new List<ManifestItem>();
        var spine = new List<SpineItem>();
        var unknown = new List<UnknownMetadata>();
        var known = new Dictionary<string, string>(StringComparer.Ordinal);
        string? uniqueIdentifier = root.Attribute("unique-identifier")?.Value;

        var metadataEl = root.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata");
        if (metadataEl is not null)
        {
            foreach (var el in metadataEl.Elements())
            {
                if (KnownMetadataNames.Contains(el.Name.LocalName))
                {
                    var key = el.Name.LocalName + (el.Attribute("id") is { } idAttr ? "#" + idAttr.Value : "");
                    known[key] = el.Value;
                }
                else
                {
                    unknown.Add(new UnknownMetadata(el.Name.LocalName, el.Name.NamespaceName,
                        XmlHelper.RawOf(el), XmlHelper.Loc(el, packagePath)));
                }
            }
        }

        var manifestEl = root.Elements().FirstOrDefault(e => e.Name.LocalName == "manifest");
        if (manifestEl is not null)
        {
            foreach (var item in manifestEl.Elements().Where(e => e.Name.LocalName == "item"))
            {
                var id = item.Attribute("id")?.Value ?? "";
                var href = item.Attribute("href")?.Value ?? "";
                var mediaType = item.Attribute("media-type")?.Value ?? "application/octet-stream";
                var fallback = item.Attribute("fallback")?.Value;
                var props = item.Attribute("properties")?.Value;
                var loc = XmlHelper.Loc(item, packagePath, id.Length > 0 ? id : null);

                var (pathPart, _) = PathResolver.SplitFragment(href);
                string? resolved = null;
                if (pathPart.Length == 0)
                {
                    diagnostics.Add(new Diagnostic("manifest-href-empty", "error",
                        $"manifest item '{id}' has an empty href.", loc));
                }
                else
                {
                    resolved = PathResolver.Resolve(packageDir, Uri.UnescapeDataString(pathPart));
                    if (resolved is null)
                    {
                        diagnostics.Add(new Diagnostic("path-escape", "error",
                            $"manifest item '{id}' href '{href}' escapes the container root after normalization; the reference is rejected.",
                            loc));
                    }
                }

                manifest.Add(new ManifestItem(id, href, resolved, mediaType, fallback, props,
                    XmlHelper.RawOf(item), loc));
            }
        }

        var manifestIds = new HashSet<string>(manifest.Select(m => m.Id), StringComparer.Ordinal);
        foreach (var item in manifest)
        {
            if (item.FallbackId is not null && !manifestIds.Contains(item.FallbackId))
            {
                diagnostics.Add(new Diagnostic("fallback-missing", "error",
                    $"manifest item '{item.Id}' declares fallback '{item.FallbackId}' which is not present in the manifest.",
                    item.Location));
            }
        }

        var spineEl = root.Elements().FirstOrDefault(e => e.Name.LocalName == "spine");
        if (spineEl is not null)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            foreach (var itemref in spineEl.Elements().Where(e => e.Name.LocalName == "itemref"))
            {
                var idref = itemref.Attribute("idref")?.Value ?? "";
                var linear = !string.Equals(itemref.Attribute("linear")?.Value, "no", StringComparison.OrdinalIgnoreCase);
                var loc = XmlHelper.Loc(itemref, packagePath, idref.Length > 0 ? idref : null);
                var target = manifest.FirstOrDefault(m => m.Id == idref);
                if (target is null)
                {
                    diagnostics.Add(new Diagnostic("spine-idref-missing", "error",
                        $"spine itemref '{idref}' at position {index} has no matching manifest item.", loc));
                }
                if (!seen.Add(idref))
                {
                    diagnostics.Add(new Diagnostic("spine-duplicate", "warning",
                        $"spine idref '{idref}' occurs more than once (position {index}); the first occurrence keeps its primary-order slot.",
                        loc));
                }
                spine.Add(new SpineItem(idref, index, linear, target?.ResolvedPath, loc));
                index++;
            }
        }

        return new PackageDocument(packagePath, packageDir, uniqueIdentifier, manifest, spine,
            unknown, known, diagnostics, Hashing.Sha256Hex(entry.Bytes));
    }
}
