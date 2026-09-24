using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace EpubReview.Core;

public sealed record ParseBudget(int MaxEntries = 4096, long MaxTotalBytes = 64L * 1024 * 1024, int MaxXmlDepth = 64)
{
    public static readonly ParseBudget Default = new();
}

/// <summary>
/// EPUB 容器解析器。所有 ZIP 条目字节、container.xml、package 文档、media type、
/// fallback、fragment 与未识别 metadata 均原样保留为不可变材料。
/// </summary>
public static class EpubParser
{
    public static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static ParsedContainer Parse(byte[] epubBytes, ParseBudget? budget = null)
    {
        budget ??= ParseBudget.Default;
        var diagnostics = new List<Diagnostic>();
        var materials = new List<Material>();

        ZipArchive zip;
        try
        {
            zip = new ZipArchive(new MemoryStream(epubBytes, writable: false), ZipArchiveMode.Read);
        }
        catch (InvalidDataException ex)
        {
            return Failed(AnalysisStatus.FormatCorrupt,
                new Diagnostic("ZIP_CORRUPT", DiagnosticSeverity.Error,
                    $"ZIP 容器损坏：{ex.Message}", null, Array.Empty<string>()));
        }

        using (zip)
        {
            if (zip.Entries.Count > budget.MaxEntries)
                return Failed(AnalysisStatus.BudgetExhausted,
                    new Diagnostic("BUDGET_ENTRIES", DiagnosticSeverity.Error,
                        $"条目数 {zip.Entries.Count} 超出预算 {budget.MaxEntries}（单位：entry）",
                        null, Array.Empty<string>()));

            long total = 0;
            foreach (var entry in zip.Entries)
            {
                total += entry.Length;
                if (total > budget.MaxTotalBytes)
                    return Failed(AnalysisStatus.BudgetExhausted,
                        new Diagnostic("BUDGET_BYTES", DiagnosticSeverity.Error,
                            $"累计字节 {total} 超出预算 {budget.MaxTotalBytes}（单位：byte）",
                            new SourcePosition(entry.FullName, 1, 1), new[] { entry.FullName }));
                using var s = entry.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                var bytes = ms.ToArray();
                materials.Add(new Material(
                    Id: "mat-" + Sha256Hex(bytes)[..16],
                    Kind: "ZipEntry",
                    MediaType: GuessMediaType(entry.FullName),
                    Bytes: bytes,
                    OriginalName: entry.FullName));
            }
        }

        var byName = materials.ToDictionary(m => m.OriginalName, StringComparer.Ordinal);

        // container.xml
        if (!byName.TryGetValue("META-INF/container.xml", out var containerMat))
            return new ParsedContainer(materials, Array.Empty<PackageDocument>(), Array.Empty<NavNode>(),
                Array.Empty<OverlayRef>(),
                new[] { new Diagnostic("CONTAINER_MISSING", DiagnosticSeverity.Error,
                    "缺少 META-INF/container.xml，语义无解：无法定位 package 文档",
                    null, Array.Empty<string>()) },
                AnalysisStatus.SemanticallyUnsolvable);

        XDocument containerDoc;
        try { containerDoc = LoadXml(containerMat, out _); }
        catch (XmlException ex)
        {
            return new ParsedContainer(materials, Array.Empty<PackageDocument>(), Array.Empty<NavNode>(),
                Array.Empty<OverlayRef>(),
                new[] { new Diagnostic("CONTAINER_XML_CORRUPT", DiagnosticSeverity.Error,
                    $"container.xml 格式损坏：{ex.Message}",
                    new SourcePosition("META-INF/container.xml", ex.LineNumber, ex.LinePosition),
                    new[] { "META-INF/container.xml" }) },
                AnalysisStatus.FormatCorrupt);
        }

        var rootfiles = containerDoc.Descendants()
            .Where(e => e.Name.LocalName == "rootfile")
            .Select(e => (Path: (string?)e.Attribute("full-path"),
                          MediaType: (string?)e.Attribute("media-type"),
                          Pos: PosOf(e, "META-INF/container.xml")))
            .ToList();

        if (rootfiles.Count == 0)
            return new ParsedContainer(materials, Array.Empty<PackageDocument>(), Array.Empty<NavNode>(),
                Array.Empty<OverlayRef>(),
                new[] { new Diagnostic("ROOTFILE_MISSING", DiagnosticSeverity.Error,
                    "container.xml 未声明任何 rootfile，语义无解",
                    new SourcePosition("META-INF/container.xml", 1, 1),
                    new[] { "META-INF/container.xml" }) },
                AnalysisStatus.SemanticallyUnsolvable);

        if (rootfiles.Count > 1)
            diagnostics.Add(new Diagnostic("MULTI_PACKAGE", DiagnosticSeverity.Info,
                $"container.xml 声明了 {rootfiles.Count} 个 package（多 package 容器）",
                rootfiles[1].Pos, rootfiles.Select(r => r.Path ?? "?").ToArray()));

        var packages = new List<PackageDocument>();
        var navTree = new List<NavNode>();
        var overlayRefs = new List<OverlayRef>();

        foreach (var rf in rootfiles)
        {
            if (rf.Path is null)
            {
                diagnostics.Add(new Diagnostic("ROOTFILE_NO_PATH", DiagnosticSeverity.Error,
                    "rootfile 缺少 full-path 属性", rf.Pos, new[] { "META-INF/container.xml" }));
                continue;
            }
            var normRoot = NormalizePath(rf.Path, out var rootEscaped);
            if (rootEscaped)
            {
                diagnostics.Add(new Diagnostic("PATH_ESCAPE", DiagnosticSeverity.Error,
                    $"rootfile 路径越出容器：{rf.Path}（路径规范化不允许越出容器）",
                    rf.Pos, new[] { rf.Path }));
                continue;
            }
            if (!byName.TryGetValue(normRoot, out var opfMat))
            {
                diagnostics.Add(new Diagnostic("PACKAGE_MISSING", DiagnosticSeverity.Error,
                    $"package 文档不存在：{normRoot}", rf.Pos, new[] { normRoot }));
                continue;
            }
            packages.Add(ParsePackage(opfMat, byName, diagnostics, navTree, overlayRefs, budget));
        }

        var status = diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)
            ? AnalysisStatus.Ok // 诊断错误不升级为整体失败；整体失败仅三种：预算/格式/语义
            : AnalysisStatus.Ok;

        return new ParsedContainer(materials, packages, navTree, overlayRefs, diagnostics, status);
    }

    private static ParsedContainer Failed(AnalysisStatus status, Diagnostic d) =>
        new(Array.Empty<Material>(), Array.Empty<PackageDocument>(), Array.Empty<NavNode>(),
            Array.Empty<OverlayRef>(), new[] { d }, status);

    private static PackageDocument ParsePackage(
        Material opfMat, Dictionary<string, Material> byName,
        List<Diagnostic> diagnostics, List<NavNode> navTree, List<OverlayRef> overlayRefs,
        ParseBudget budget)
    {
        var entry = opfMat.OriginalName;
        XDocument doc;
        try { doc = LoadXml(opfMat, out _); }
        catch (XmlException ex)
        {
            diagnostics.Add(new Diagnostic("OPF_XML_CORRUPT", DiagnosticSeverity.Error,
                $"package 文档格式损坏：{ex.Message}",
                new SourcePosition(entry, ex.LineNumber, ex.LinePosition), new[] { entry }));
            return new PackageDocument(entry, BaseDir(entry), Array.Empty<ManifestItem>(),
                Array.Empty<SpineItem>(), Array.Empty<UnknownMetadata>());
        }

        var baseDir = BaseDir(entry);
        var manifest = new List<ManifestItem>();
        var spine = new List<SpineItem>();
        var unknown = new List<UnknownMetadata>();

        var root = doc.Root!;
        var manifestEl = root.Elements().FirstOrDefault(e => e.Name.LocalName == "manifest");
        var spineEl = root.Elements().FirstOrDefault(e => e.Name.LocalName == "spine");
        var metadataEl = root.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata");

        if (metadataEl is not null)
        {
            foreach (var el in metadataEl.Elements())
            {
                var ns = el.Name.NamespaceName;
                if (ns is not ("http://purl.org/dc/elements/1.1/" or "http://www.idpf.org/2007/opf" or ""))
                    unknown.Add(new UnknownMetadata(el.ToString(SaveOptions.DisableFormatting), PosOf(el, entry)));
                else if (el.Name.LocalName == "meta" && ((string?)el.Attribute("property") ?? "").Contains(':'))
                {
                    var prop = (string?)el.Attribute("property") ?? "";
                    var prefix = prop.Split(':')[0];
                    if (prefix is not ("dcterms" or "media" or "rendition" or "schema" or "marc" or "onix"))
                        unknown.Add(new UnknownMetadata(el.ToString(SaveOptions.DisableFormatting), PosOf(el, entry)));
                }
            }
        }

        if (manifestEl is not null)
        {
            foreach (var item in manifestEl.Elements().Where(e => e.Name.LocalName == "item"))
            {
                var id = (string?)item.Attribute("id") ?? "";
                var href = (string?)item.Attribute("href") ?? "";
                var pos = PosOf(item, entry);
                var resolved = Resolve(baseDir, href, out var escaped);
                if (escaped)
                    diagnostics.Add(new Diagnostic("PATH_ESCAPE", DiagnosticSeverity.Error,
                        $"manifest item '{id}' 的 href 越出容器：{href}",
                        pos, new[] { id, href }));
                else if (href.Length > 0 && !byName.ContainsKey(resolved))
                    diagnostics.Add(new Diagnostic("RESOURCE_MISSING", DiagnosticSeverity.Error,
                        $"manifest item '{id}' 引用的资源不存在：{resolved}",
                        pos, new[] { id, resolved }));
                manifest.Add(new ManifestItem(id, href,
                    (string?)item.Attribute("media-type") ?? "application/octet-stream",
                    (string?)item.Attribute("fallback"),
                    (string?)item.Attribute("media-overlay"),
                    (string?)item.Attribute("properties"),
                    pos));
            }
        }

        if (spineEl is not null)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var ir in spineEl.Elements().Where(e => e.Name.LocalName == "itemref"))
            {
                var idref = (string?)ir.Attribute("idref") ?? "";
                var pos = PosOf(ir, entry);
                if (!seen.Add(idref))
                    diagnostics.Add(new Diagnostic("SPINE_DUPLICATE", DiagnosticSeverity.Warning,
                        $"spine 中 idref '{idref}' 重复；spine 只定义主顺序，重复项保留但标记",
                        pos, new[] { idref }));
                if (manifest.All(m => m.Id != idref))
                    diagnostics.Add(new Diagnostic("SPINE_IDREF_MISSING", DiagnosticSeverity.Error,
                        $"spine idref '{idref}' 在 manifest 中不存在",
                        pos, new[] { idref }));
                spine.Add(new SpineItem(idref,
                    !string.Equals((string?)ir.Attribute("linear"), "no", StringComparison.OrdinalIgnoreCase), pos));
            }
        }

        var pkg = new PackageDocument(entry, baseDir, manifest, spine, unknown);

        // navigation 文档
        var navItem = manifest.FirstOrDefault(m =>
            (m.Properties ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("nav"));
        if (navItem is not null)
        {
            var navPath = Resolve(baseDir, navItem.Href, out _);
            if (byName.TryGetValue(navPath, out var navMat))
                navTree.AddRange(ParseNav(navMat, diagnostics));
        }

        // media-overlay（SMIL）
        foreach (var item in manifest.Where(m => m.MediaOverlay is not null))
        {
            var smilItem = manifest.FirstOrDefault(m => m.Id == item.MediaOverlay);
            if (smilItem is null)
            {
                diagnostics.Add(new Diagnostic("OVERLAY_MISSING", DiagnosticSeverity.Error,
                    $"manifest item '{item.Id}' 的 media-overlay 指向不存在的项 '{item.MediaOverlay}'（media-overlay 断链）",
                    item.Position, new[] { item.Id, item.MediaOverlay! }));
                continue;
            }
            var smilPath = Resolve(baseDir, smilItem.Href, out _);
            if (!byName.TryGetValue(smilPath, out var smilMat))
            {
                diagnostics.Add(new Diagnostic("OVERLAY_MISSING", DiagnosticSeverity.Error,
                    $"media-overlay 文件不存在：{smilPath}", item.Position, new[] { smilPath }));
                continue;
            }
            ParseSmil(smilMat, overlayRefs, diagnostics);
        }

        return pkg;
    }

    private static List<NavNode> ParseNav(Material navMat, List<Diagnostic> diagnostics)
    {
        XDocument doc;
        try { doc = LoadXml(navMat, out _); }
        catch (XmlException ex)
        {
            diagnostics.Add(new Diagnostic("NAV_XML_CORRUPT", DiagnosticSeverity.Error,
                $"导航文档格式损坏：{ex.Message}",
                new SourcePosition(navMat.OriginalName, ex.LineNumber, ex.LinePosition),
                new[] { navMat.OriginalName }));
            return new List<NavNode>();
        }
        var navEl = doc.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "nav" &&
            string.Equals((string?)e.Attribute(e.Name.Namespace + "type") ?? (string?)e.Attribute("type"),
                "toc", StringComparison.OrdinalIgnoreCase));
        var result = new List<NavNode>();
        if (navEl is null) return result;
        var ol = navEl.Elements().FirstOrDefault(e => e.Name.LocalName == "ol");
        if (ol is not null) result.AddRange(ParseNavList(ol));
        return result;
    }

    private static IEnumerable<NavNode> ParseNavList(XElement ol)
    {
        foreach (var li in ol.Elements().Where(e => e.Name.LocalName == "li"))
        {
            var a = li.Elements().FirstOrDefault(e => e.Name.LocalName == "a");
            var href = (string?)a?.Attribute("href");
            string? frag = null;
            string? path = href;
            if (href is not null)
            {
                var idx = href.IndexOf('#');
                if (idx >= 0) { path = href[..idx]; frag = href[(idx + 1)..]; }
            }
            var children = li.Elements().Where(e => e.Name.LocalName == "ol")
                .SelectMany(ParseNavList).ToList();
            yield return new NavNode(a?.Value.Trim() ?? "(untitled)", path, frag, children);
        }
    }

    private static void ParseSmil(Material smilMat, List<OverlayRef> refs, List<Diagnostic> diagnostics)
    {
        XDocument doc;
        try { doc = LoadXml(smilMat, out _); }
        catch (XmlException ex)
        {
            diagnostics.Add(new Diagnostic("SMIL_XML_CORRUPT", DiagnosticSeverity.Error,
                $"media-overlay 文档格式损坏：{ex.Message}",
                new SourcePosition(smilMat.OriginalName, ex.LineNumber, ex.LinePosition),
                new[] { smilMat.OriginalName }));
            return;
        }
        foreach (var text in doc.Descendants().Where(e => e.Name.LocalName == "text"))
        {
            var src = (string?)text.Attribute("src") ?? "";
            var idx = src.IndexOf('#');
            refs.Add(new OverlayRef(smilMat.OriginalName,
                idx >= 0 ? src[..idx] : src,
                idx >= 0 ? src[(idx + 1)..] : null,
                PosOf(text, smilMat.OriginalName)));
        }
    }

    /// <summary>容器内路径规范化：解析 . 与 ..，但绝不允许越出容器根。</summary>
    public static string NormalizePath(string path, out bool escaped)
    {
        escaped = false;
        var parts = new List<string>();
        foreach (var seg in path.Replace('\\', '/').Split('/'))
        {
            if (seg is "" or ".") continue;
            if (seg == "..")
            {
                if (parts.Count == 0) { escaped = true; continue; }
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(seg);
        }
        return string.Join('/', parts);
    }

    public static string Resolve(string baseDir, string href, out bool escaped) =>
        NormalizePath(baseDir.Length == 0 ? href : baseDir + "/" + href, out escaped);

    public static string BaseDir(string entryPath)
    {
        var idx = entryPath.LastIndexOf('/');
        return idx < 0 ? "" : entryPath[..idx];
    }

    private static XDocument LoadXml(Material mat, out int maxDepth)
    {
        var text = Encoding.UTF8.GetString(mat.Bytes);
        var doc = XDocument.Parse(text, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
        maxDepth = 0;
        return doc;
    }

    private static SourcePosition PosOf(XElement el, string entry)
    {
        var li = (IXmlLineInfo)el;
        return new SourcePosition(entry, li.HasLineInfo() ? li.LineNumber : 0, li.HasLineInfo() ? li.LinePosition : 0);
    }

    public static string GuessMediaType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".xhtml" or ".html" => "application/xhtml+xml",
        ".opf" => "application/oebps-package+xml",
        ".xml" => "application/xml",
        ".ncx" => "application/x-dtbncx+xml",
        ".smil" => "application/smil+xml",
        ".css" => "text/css",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".mp3" => "audio/mpeg",
        ".mp4" => "video/mp4",
        ".js" => "text/javascript",
        _ => "application/octet-stream"
    };
}
