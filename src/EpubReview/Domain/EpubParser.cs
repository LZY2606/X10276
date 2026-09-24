using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace EpubReview.Domain;

/// <summary>
/// 导入层：把 EPUB 容器解析为不可变材料。
/// ZIP 条目字节、container.xml、package 文档、media type、fallback、fragment
/// 与未识别 metadata 全部原样保留；解析失败只产生带位置的诊断，不抛异常、
/// 不用空值或默认值伪装成功。
/// </summary>
public static class EpubParser
{
    private static readonly XNamespace ContainerNs = "urn:oasis:names:tc:opendocument:xmlns:container";
    private static readonly XNamespace OpfNs = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace DcNs = "http://purl.org/dc/elements/1.1/";

    private static readonly HashSet<string> KnownMetaProperties = new(StringComparer.Ordinal)
    {
        "dcterms:modified", "rendition:layout", "rendition:orientation",
        "rendition:spread", "rendition:flow", "media:duration", "schema:accessibilityFeature"
    };

    public static EpubMaterials Parse(byte[] zipBytes, AnalysisParams budget)
    {
        var diagnostics = new List<Diagnostic>();
        var entries = new List<ZipEntryMaterial>();

        if (zipBytes.Length > budget.MaxBytes)
        {
            diagnostics.Add(new Diagnostic(Diag.BudgetExhausted, "error",
                $"容器字节数 {zipBytes.Length} 超出预算 {budget.MaxBytes}", null, null, null, null));
            return Empty(zipBytes, diagnostics);
        }

        ZipArchive archive;
        try
        {
            archive = new ZipArchive(new MemoryStream(zipBytes, writable: false), ZipArchiveMode.Read);
        }
        catch (InvalidDataException ex)
        {
            diagnostics.Add(new Diagnostic(Diag.FormatCorrupt, "error",
                $"ZIP 容器无法打开: {ex.Message}", null, null, null, null));
            return Empty(zipBytes, diagnostics);
        }

        using (archive)
        {
            if (archive.Entries.Count > budget.MaxEntries)
            {
                diagnostics.Add(new Diagnostic(Diag.BudgetExhausted, "error",
                    $"ZIP 条目数 {archive.Entries.Count} 超出预算 {budget.MaxEntries}", null, null, null, null));
                return Empty(zipBytes, diagnostics);
            }

            foreach (var e in archive.Entries.OrderBy(e => e.FullName, StringComparer.Ordinal))
            {
                var path = e.FullName.Replace('\\', '/').TrimStart('/');
                if (path.Length == 0 || path.EndsWith('/')) continue;
                using var stream = e.Open();
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                var bytes = ms.ToArray();
                entries.Add(new ZipEntryMaterial(path, bytes.Length, Hashing.Sha256Hex(bytes), bytes));
            }
        }

        var materialsId = Hashing.Sha256Hex(string.Join('\n',
            entries.Select(e => $"{e.Path}:{e.Sha256}")));

        // container.xml 原文保留
        var containerEntry = entries.FirstOrDefault(e => e.Path == "META-INF/container.xml");
        if (containerEntry is null)
        {
            diagnostics.Add(new Diagnostic(Diag.ContainerMissing, "error",
                "缺少 META-INF/container.xml，无法定位 package 文档", "META-INF/container.xml", null, null, null));
            return new EpubMaterials(materialsId, entries, "", Array.Empty<PackageDoc>(), diagnostics);
        }
        var containerXml = Encoding.UTF8.GetString(containerEntry.Bytes);

        var rootfilePaths = ParseContainer(containerXml, diagnostics);
        var packages = new List<PackageDoc>();
        foreach (var (path, line) in rootfilePaths)
        {
            var entry = entries.FirstOrDefault(e => e.Path == path);
            if (entry is null)
            {
                diagnostics.Add(new Diagnostic(Diag.MissingResource, "error",
                    $"container.xml 声明的 package 文档 {path} 在容器中不存在", "META-INF/container.xml", line, null, path));
                continue;
            }
            var raw = Encoding.UTF8.GetString(entry.Bytes);
            packages.Add(ParsePackage(path, raw, diagnostics));
        }

        return new EpubMaterials(materialsId, entries, containerXml, packages, diagnostics);
    }

    private static EpubMaterials Empty(byte[] zipBytes, List<Diagnostic> diagnostics) =>
        new(Hashing.Sha256Hex("empty:" + Hashing.Sha256Hex(zipBytes)),
            Array.Empty<ZipEntryMaterial>(), "", Array.Empty<PackageDoc>(), diagnostics);

    private static List<(string Path, int? Line)> ParseContainer(string xml, List<Diagnostic> diagnostics)
    {
        var result = new List<(string, int?)>();
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml, LoadOptions.SetLineInfo);
        }
        catch (XmlException ex)
        {
            diagnostics.Add(new Diagnostic(Diag.FormatCorrupt, "error",
                $"container.xml 不是合法 XML: {ex.Message}", "META-INF/container.xml", ex.LineNumber, ex.LinePosition, null));
            return result;
        }

        var rootfiles = doc.Descendants(ContainerNs + "rootfile").ToList();
        foreach (var rf in rootfiles)
        {
            var fullPath = rf.Attribute("full-path")?.Value;
            var mediaType = rf.Attribute("media-type")?.Value;
            var line = (rf as IXmlLineInfo)?.LineNumber;
            if (string.IsNullOrWhiteSpace(fullPath))
            {
                diagnostics.Add(new Diagnostic(Diag.FormatCorrupt, "error",
                    "rootfile 缺少 full-path 属性", "META-INF/container.xml", line, null, null));
                continue;
            }
            if (mediaType != "application/oebps-package+xml")
            {
                diagnostics.Add(new Diagnostic(Diag.FormatCorrupt, "warning",
                    $"rootfile {fullPath} 的 media-type 为 {mediaType ?? "(缺失)"}，不是 application/oebps-package+xml",
                    "META-INF/container.xml", line, null, fullPath));
            }
            result.Add((fullPath.TrimStart('/'), line));
        }

        if (result.Count > 1)
        {
            diagnostics.Add(new Diagnostic(Diag.MultiPackage, "warning",
                $"container.xml 声明了 {result.Count} 个 package 文档；全部保留为候选，主分析使用第一个",
                "META-INF/container.xml", null, null, string.Join(",", result.Select(r => r.Item1))));
        }
        if (result.Count == 0)
        {
            diagnostics.Add(new Diagnostic(Diag.ContainerMissing, "error",
                "container.xml 中没有可用的 rootfile", "META-INF/container.xml", null, null, null));
        }
        return result;
    }

    private static PackageDoc ParsePackage(string path, string raw, List<Diagnostic> diagnostics)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(raw, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex)
        {
            diagnostics.Add(new Diagnostic(Diag.FormatCorrupt, "error",
                $"package 文档 {path} 不是合法 XML: {ex.Message}", path, ex.LineNumber, ex.LinePosition, null));
            return new PackageDoc(path, raw, null, Array.Empty<ManifestItem>(),
                Array.Empty<SpineItemRef>(), Array.Empty<string>());
        }

        var baseDir = ContainerPath.DirOf(path);
        var manifest = new List<ManifestItem>();
        var spine = new List<SpineItemRef>();
        var unknown = new List<string>();
        string? uniqueId = doc.Root?.Attribute("unique-identifier")?.Value;

        var metadataEl = doc.Root?.Element(OpfNs + "metadata");
        if (metadataEl is not null)
        {
            foreach (var el in metadataEl.Elements())
            {
                var isKnown = el.Name.Namespace == DcNs
                    || (el.Name == OpfNs + "meta"
                        && KnownMetaProperties.Contains(el.Attribute("property")?.Value ?? ""))
                    || el.Name == OpfNs + "link";
                if (!isKnown)
                {
                    var rawEl = el.ToString(SaveOptions.DisableFormatting);
                    unknown.Add(rawEl);
                    var line = (el as IXmlLineInfo)?.LineNumber;
                    diagnostics.Add(new Diagnostic(Diag.UnknownMetadata, "info",
                        $"未识别 metadata 元素 <{el.Name.LocalName}> 已原样保留", path, line, null,
                        el.Attribute("property")?.Value ?? el.Name.LocalName));
                }
            }
        }

        var manifestEl = doc.Root?.Element(OpfNs + "manifest");
        if (manifestEl is not null)
        {
            foreach (var item in manifestEl.Elements(OpfNs + "item"))
            {
                var line = (item as IXmlLineInfo)?.LineNumber;
                var id = item.Attribute("id")?.Value;
                var href = item.Attribute("href")?.Value;
                var mediaType = item.Attribute("media-type")?.Value;
                if (id is null || href is null || mediaType is null)
                {
                    diagnostics.Add(new Diagnostic(Diag.FormatCorrupt, "error",
                        $"manifest item 缺少必需属性 (id={id ?? "?"}, href={href ?? "?"}, media-type={mediaType ?? "?"})",
                        path, line, null, id));
                    continue;
                }
                string? resolved = null;
                string? fragment = null;
                if (ContainerPath.TryNormalize(baseDir, href, out var norm, out var frag))
                {
                    resolved = norm;
                    fragment = frag;
                }
                else
                {
                    diagnostics.Add(new Diagnostic(Diag.PathOutOfContainer, "error",
                        $"manifest item {id} 的 href \"{href}\" 规范化后越出容器根，已拒绝",
                        path, line, null, id));
                }
                manifest.Add(new ManifestItem(id, href, resolved, fragment, mediaType,
                    item.Attribute("fallback")?.Value,
                    item.Attribute("media-overlay")?.Value,
                    item.Attribute("properties")?.Value));
            }
        }

        var spineEl = doc.Root?.Element(OpfNs + "spine");
        if (spineEl is not null)
        {
            var ordinal = 0;
            foreach (var itemref in spineEl.Elements(OpfNs + "itemref"))
            {
                var idref = itemref.Attribute("idref")?.Value;
                var line = (itemref as IXmlLineInfo)?.LineNumber;
                if (idref is null)
                {
                    diagnostics.Add(new Diagnostic(Diag.FormatCorrupt, "error",
                        "spine itemref 缺少 idref", path, line, null, null));
                    continue;
                }
                var linear = !string.Equals(itemref.Attribute("linear")?.Value, "no", StringComparison.OrdinalIgnoreCase);
                spine.Add(new SpineItemRef(idref, linear, ordinal++));
            }
        }

        return new PackageDoc(path, raw, uniqueId, manifest, spine, unknown);
    }
}
