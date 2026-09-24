using System.Xml.Linq;

namespace EpubReview.Core;

public static class ContainerParser
{
    public sealed record ContainerOutcome(
        IReadOnlyList<string>? Rootfiles, FailureKind Failure, Diagnostic? Error);

    public static ContainerOutcome Parse(IReadOnlyList<StoredEntry> entries)
    {
        var entry = entries.FirstOrDefault(e => e.Path == "META-INF/container.xml");
        if (entry is null)
            return new ContainerOutcome(null, FailureKind.FormatCorrupt,
                new Diagnostic("container-missing", DiagSeverity.Error,
                    "缺少 META-INF/container.xml", "META-INF/container.xml", null, Array.Empty<string>()));
        XDocument doc;
        try { doc = XDocument.Parse(System.Text.Encoding.UTF8.GetString(entry.Bytes)); }
        catch (System.Xml.XmlException ex)
        {
            return new ContainerOutcome(null, FailureKind.FormatCorrupt,
                new Diagnostic("container-malformed", DiagSeverity.Error,
                    "container.xml 不是合法 XML: " + ex.Message, entry.Path, null, Array.Empty<string>()));
        }
        var rootfiles = doc.Descendants()
            .Where(e => e.Name.LocalName == "rootfile")
            .Select(e => (string?)e.Attribute("full-path"))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.Replace('\\', '/').TrimStart('/'))
            .Distinct()
            .ToList();
        if (rootfiles.Count == 0)
            return new ContainerOutcome(null, FailureKind.SemanticUnresolvable,
                new Diagnostic("rootfile-none", DiagSeverity.Error,
                    "container.xml 未声明任何 rootfile，无法确定 package 文档",
                    entry.Path, null, Array.Empty<string>()));
        return new ContainerOutcome(rootfiles, FailureKind.None, null);
    }
}

public sealed record OpfDocument(
    string PackagePath,
    IReadOnlyList<ManifestItem> Manifest,
    IReadOnlyList<SpineItemRef> Spine,
    IReadOnlyList<string> UnknownMetadataRaw,
    IReadOnlyList<string> AllMetadataRaw);

public static class OpfParser
{
    private static readonly HashSet<string> KnownMetaProperties = new(StringComparer.Ordinal)
    {
        "dcterms:modified", "dcterms:conformsTo", "schema:accessMode",
        "schema:accessibilityFeature", "schema:accessibilityHazard",
    };

    public sealed record OpfOutcome(OpfDocument? Doc, FailureKind Failure, Diagnostic? Error);

    public static OpfOutcome Parse(StoredEntry opfEntry)
    {
        XDocument doc;
        try { doc = XDocument.Parse(System.Text.Encoding.UTF8.GetString(opfEntry.Bytes)); }
        catch (System.Xml.XmlException ex)
        {
            return new OpfOutcome(null, FailureKind.FormatCorrupt,
                new Diagnostic("opf-malformed", DiagSeverity.Error,
                    "package 文档不是合法 XML: " + ex.Message, opfEntry.Path, null, Array.Empty<string>()));
        }
        var root = doc.Root;
        if (root is null || root.Name.LocalName != "package")
            return new OpfOutcome(null, FailureKind.FormatCorrupt,
                new Diagnostic("opf-not-package", DiagSeverity.Error,
                    "package 文档根元素不是 <package>", opfEntry.Path, null, Array.Empty<string>()));

        var baseDir = PathResolver.DirOf(opfEntry.Path);

        // ---- metadata：全部保留原始 XML；未识别项单独标记 ----
        var allMeta = new List<string>();
        var unknownMeta = new List<string>();
        var metaEl = root.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata");
        if (metaEl is not null)
        {
            foreach (var m in metaEl.Elements())
            {
                var raw = m.ToString(SaveOptions.DisableFormatting);
                allMeta.Add(raw);
                var ns = m.Name.NamespaceName;
                var isDc = ns == "http://purl.org/dc/elements/1.1/";
                var isOpfMeta = m.Name.LocalName == "meta" &&
                    (string.IsNullOrEmpty(ns) || ns == "http://www.idpf.org/2007/opf") &&
                    KnownMetaProperties.Contains((string?)m.Attribute("property") ?? "");
                if (!isDc && !isOpfMeta)
                    unknownMeta.Add(raw);
            }
        }

        // ---- manifest ----
        var manifest = new List<ManifestItem>();
        var manEl = root.Elements().FirstOrDefault(e => e.Name.LocalName == "manifest");
        if (manEl is not null)
        {
            foreach (var item in manEl.Elements().Where(e => e.Name.LocalName == "item"))
            {
                var id = (string?)item.Attribute("id") ?? "";
                var href = (string?)item.Attribute("href") ?? "";
                var (hrefPath, _) = PathResolver.SplitFragment(href);
                var ok = PathResolver.TryNormalize(baseDir, hrefPath, out var norm, out var escaped);
                manifest.Add(new ManifestItem(
                    id, href,
                    (string?)item.Attribute("media-type") ?? "application/octet-stream",
                    (string?)item.Attribute("fallback"),
                    (string?)item.Attribute("properties"),
                    (string?)item.Attribute("media-overlay"),
                    opfEntry.Path,
                    ok ? norm : "__invalid__:" + href));
            }
        }

        // ---- spine ----
        var spine = new List<SpineItemRef>();
        var spineEl = root.Elements().FirstOrDefault(e => e.Name.LocalName == "spine");
        if (spineEl is not null)
        {
            var pos = 0;
            foreach (var ir in spineEl.Elements().Where(e => e.Name.LocalName == "itemref"))
            {
                var idref = (string?)ir.Attribute("idref") ?? "";
                var linear = !string.Equals((string?)ir.Attribute("linear"), "no", StringComparison.OrdinalIgnoreCase);
                spine.Add(new SpineItemRef(idref, linear, pos++));
            }
        }

        return new OpfOutcome(
            new OpfDocument(opfEntry.Path, manifest, spine, unknownMeta, allMeta),
            FailureKind.None, null);
    }
}
