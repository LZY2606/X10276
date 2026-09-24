using System.Xml.Linq;

namespace EpubReview.Core;

public static class PackageParser
{
    private static readonly HashSet<string> KnownMetadataElements = new()
    {
        "title", "creator", "subject", "description", "publisher", "contributor",
        "date", "type", "format", "source", "relation", "coverage", "rights",
        "identifier", "language", "meta",
    };

    public static PackageDocumentRecord Parse(
        string packageKey,
        string? rootFilePathRaw,
        string? normalizedPath,
        bool escapes,
        string? mediaType,
        byte[] bytes,
        bool entryExists)
    {
        var diags = new List<Diagnostic>();
        var label = normalizedPath ?? rootFilePathRaw ?? "<package>";

        if (!entryExists)
        {
            diags.Add(Diagnostic.Create("PACKAGE_ENTRY_MISSING", Severity.Error,
                $"container.xml 指向的 package 文档 \"{rootFilePathRaw}\" 在 ZIP 条目中不存在（缺失资源，区别于 fallback 循环）。",
                new TextLocation(rootFilePathRaw, null, null, null),
                new[] { packageKey }));
            return Empty(packageKey, rootFilePathRaw, mediaType, bytes, diags);
        }

        if (escapes)
        {
            diags.Add(Diagnostic.Create("PACKAGE_PATH_TRAVERSAL", Severity.Error,
                $"package 路径 \"{rootFilePathRaw}\" 越出容器根，按容器安全规则拒绝读取其指向的外部字节。",
                new TextLocation(rootFilePathRaw, null, null, null), new[] { packageKey }));
            return Empty(packageKey, rootFilePathRaw, mediaType, bytes, diags);
        }

        var doc = Xml.Parse(bytes, label, diags);
        if (doc is null) return Empty(packageKey, rootFilePathRaw, mediaType, bytes, diags);

        var packageEl = doc.Root;
        var opfNs = ResolveOpfNamespace(packageEl);
        var metadataEl = packageEl?.Element(opfNs + "metadata");
        var manifestEl = packageEl?.Element(opfNs + "manifest");
        var spineEl = packageEl?.Element(opfNs + "spine");

        var metadata = ParseMetadata(metadataEl, label, diags);
        var baseDir = (normalizedPath ?? "").Contains('/')
            ? normalizedPath![..normalizedPath!.LastIndexOf('/')]
            : "";

        var items = ParseManifest(manifestEl, label, baseDir, diags, opfNs);
        var byId = items
            .GroupBy(i => i.Id)
            .ToDictionary(g => g.Key, g => g.First());
        foreach (var dup in items.GroupBy(i => i.Id).Where(g => g.Count() > 1))
        {
            diags.Add(Diagnostic.Create("MANIFEST_DUPLICATE_ID", Severity.Error,
                $"manifest 中 id \"{dup.Key}\" 重复，无法唯一决定资源身份。",
                dup.First().Location, new[] { ItemNodeId(packageKey, dup.Key) }));
        }

        items = ResolveReferences(items, byId, label, diags, packageKey);
        var spine = ParseSpine(spineEl, label, byId, diags, packageKey);
        var overlays = BuildOverlayRefs(items, label, diags, packageKey);

        return new PackageDocumentRecord(
            packageKey, rootFilePathRaw, mediaType,
            packageEl?.GetPrefixOfNamespace(EpubNamespaces.Dc) is null ? null :
                packageEl.Attribute("unique-identifier")?.Value,
            packageEl?.Attribute("version")?.Value,
            packageEl?.Attribute(XNamespace.Xml + "lang")?.Value,
            metadata, items, spine, overlays, bytes, diags,
            Xml.Loc(packageEl, label, "/opf:package"));
    }

    private static XNamespace ResolveOpfNamespace(XElement? packageEl)
    {
        var ns = packageEl?.Name.Namespace;
        if (ns is not null && !string.IsNullOrEmpty(ns.NamespaceName)) return ns!;
        return EpubNamespaces.Opf;
    }

    private static PackageDocumentRecord Empty(string key, string? rootPath, string? mt,
        byte[] bytes, List<Diagnostic> diags) =>
        new(key, rootPath, mt, null, null, null,
            new MetadataRecord(new Dictionary<string, string>(), Array.Empty<UnknownMetadataRecord>()),
            Array.Empty<ManifestItemRecord>(), Array.Empty<SpineItemRefRecord>(),
            Array.Empty<OverlayRefRecord>(), bytes, diags, null);

    public static string ItemNodeId(string packageKey, string itemId) => $"n:item:{packageKey}:{itemId}";

    private static MetadataRecord ParseMetadata(XElement? metadataEl, string label, List<Diagnostic> diags)
    {
        var known = new Dictionary<string, string>();
        var unknown = new List<UnknownMetadataRecord>();
        if (metadataEl is null) return new MetadataRecord(known, unknown);

        foreach (var el in metadataEl.Elements())
        {
            var local = el.Name.LocalName;
            var ns = el.Name.NamespaceName ?? "";
            var isDc = ns == EpubNamespaces.Dc.NamespaceName;
            if (isDc && KnownMetadataElements.Contains(local))
            {
                var key = $"{el.GetPrefixOfNamespace(EpubNamespaces.Dc) ?? "dc"}:{local}";
                known[key] = el.Value;
            }
            else
            {
                unknown.Add(new UnknownMetadataRecord(
                    local, ns, el.ToString(SaveOptions.DisableFormatting),
                    BuildPath(el), Xml.Loc(el, label, BuildPath(el))));
            }
        }
        return new MetadataRecord(known, unknown);
    }

    private static string BuildPath(XElement el)
    {
        var parts = new List<string>();
        var cur = el;
        while (cur is not null)
        {
            var prefix = cur.GetPrefixOfNamespace(cur.Name.Namespace);
            var name = string.IsNullOrEmpty(prefix) ? cur.Name.LocalName : $"{prefix}:{cur.Name.LocalName}";
            parts.Add(name);
            cur = cur.Parent;
        }
        parts.Reverse();
        return "/" + string.Join('/', parts);
    }

    private static List<ManifestItemRecord> ParseManifest(
        XElement? manifestEl, string label, string baseDir,
        List<Diagnostic> diags, XNamespace opfNs)
    {
        var items = new List<ManifestItemRecord>();
        foreach (var el in manifestEl?.Elements(opfNs + "item") ?? Enumerable.Empty<XElement>())
        {
            var id = el.Attribute("id")?.Value ?? "";
            var href = el.Attribute("href")?.Value;
            var mt = el.Attribute("media-type")?.Value ?? "";
            var fallback = el.Attribute("fallback")?.Value;
            var overlay = el.Attribute("media-overlay")?.Value;

            if (string.IsNullOrEmpty(id))
            {
                diags.Add(Diagnostic.Create("MANIFEST_ITEM_NO_ID", Severity.Error,
                    "manifest item 缺少 id。", Xml.Loc(el, label, "/opf:package/opf:manifest/opf:item")));
            }

            NormalizedPath? resolved = null;
            if (href is not null) resolved = NormalizedPath.Resolve(baseDir, href);
            var escapes = resolved?.Escapes ?? false;
            if (href is null)
            {
                diags.Add(Diagnostic.Create("MANIFEST_ITEM_NO_HREF", Severity.Error,
                    $"manifest item \"{id}\" 缺少 href。",
                    Xml.Loc(el, label, "/opf:package/opf:manifest/opf:item"), new[] { ItemNodeId("<pkg>", id) }));
            }
            else if (escapes)
            {
                diags.Add(Diagnostic.Create("PATH_TRAVERSAL", Severity.Error,
                    $"manifest item \"{id}\" 的 href \"{href}\" 规范化时越出容器根；不允许用 \"..\" 离开容器。",
                    Xml.Loc(el, label, "/opf:package/opf:manifest/opf:item")));
            }

            items.Add(new ManifestItemRecord(
                id, href, escapes ? null : resolved?.Normalized, escapes, mt,
                fallback, null, overlay, null, false,
                Array.Empty<AnchorRecord>(),
                Xml.Loc(el, label, "/opf:package/opf:manifest/opf:item")));
        }
        return items;
    }

    private static List<ManifestItemRecord> ResolveReferences(
        List<ManifestItemRecord> items, Dictionary<string, ManifestItemRecord> byId,
        string label, List<Diagnostic> diags, string packageKey)
    {
        var result = new List<ManifestItemRecord>();
        foreach (var item in items)
        {
            string? fbTarget = null, ovTarget = null;
            var overlayBroken = false;
            if (item.FallbackIdRaw is not null)
            {
                if (byId.TryGetValue(item.FallbackIdRaw, out var fb)) fbTarget = fb.Id;
                else
                    diags.Add(Diagnostic.Create("FALLBACK_TARGET_MISSING", Severity.Error,
                        $"item \"{item.Id}\" 的 fallback=\"{item.FallbackIdRaw}\" 在 manifest 中不存在（缺失资源）。",
                        item.Location, new[] { ItemNodeId(packageKey, item.Id) }));
            }
            if (item.MediaOverlayIdRaw is not null)
            {
                if (byId.TryGetValue(item.MediaOverlayIdRaw, out var ov))
                {
                    ovTarget = ov.Id;
                    if (!ov.MediaType.Contains("smil", StringComparison.OrdinalIgnoreCase))
                    {
                        overlayBroken = true;
                        diags.Add(Diagnostic.Create("MEDIA_OVERLAY_BROKEN", Severity.Error,
                            $"item \"{item.Id}\" 的 media-overlay 指向 \"{ov.Id}\"，其 media-type \"{ov.MediaType}\" 不是 SMIL。",
                            item.Location, new[] { ItemNodeId(packageKey, item.Id), ItemNodeId(packageKey, ov.Id) }));
                    }
                }
                else
                {
                    overlayBroken = true;
                    diags.Add(Diagnostic.Create("MEDIA_OVERLAY_BROKEN", Severity.Error,
                        $"item \"{item.Id}\" 的 media-overlay=\"{item.MediaOverlayIdRaw}\" 断链：manifest 中不存在该条目。",
                        item.Location, new[] { ItemNodeId(packageKey, item.Id) }));
                }
            }
            result.Add(item with
            {
                FallbackTargetItemId = fbTarget,
                MediaOverlayTargetItemId = ovTarget,
                MediaOverlayBroken = overlayBroken,
            });
        }
        return result;
    }

    private static List<SpineItemRefRecord> ParseSpine(
        XElement? spineEl, string label, Dictionary<string, ManifestItemRecord> byId,
        List<Diagnostic> diags, string packageKey)
    {
        var refs = new List<SpineItemRefRecord>();
        var seen = new HashSet<string>();
        var ordinal = 0;
        foreach (var el in spineEl?.Elements() ?? Enumerable.Empty<XElement>())
        {
            if (el.Name.LocalName != "itemref") continue;
            var idref = el.Attribute("idref")?.Value;
            var linear = el.Attribute("linear")?.Value?.ToLowerInvariant() != "no";
            var loc = Xml.Loc(el, label, "/opf:package/opf:spine/opf:itemref");
            string? target = null;
            if (idref is not null && byId.TryGetValue(idref, out var item)) target = item.Id;
            else if (idref is not null)
                diags.Add(Diagnostic.Create("SPINE_IDREF_MISSING", Severity.Error,
                    $"spine itemref idref=\"{idref}\" 在 manifest 中不存在。",
                    loc, new[] { ItemNodeId(packageKey, idref) }));

            var duplicate = idref is not null && !seen.Add(idref);
            if (duplicate)
                diags.Add(Diagnostic.Create("SPINE_DUPLICATE", Severity.Warning,
                    $"spine 重复引用 \"{idref}\"（主阅读顺序中第 {ordinal + 1} 个位置）。",
                    loc, new[] { ItemNodeId(packageKey, idref!) }));

            refs.Add(new SpineItemRefRecord(idref ?? "", ordinal, linear, target, loc) { Duplicate = duplicate });
            ordinal++;
        }
        return refs;
    }

    private static List<OverlayRefRecord> BuildOverlayRefs(
        List<ManifestItemRecord> items, string label, List<Diagnostic> diags, string packageKey)
        => items
            .Where(i => i.MediaOverlayIdRaw is not null)
            .Select(i => new OverlayRefRecord(
                i.Id, i.MediaOverlayTargetItemId ?? i.MediaOverlayIdRaw!,
                null, i.Location))
            .ToList();
}
