using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace EpubReview.Domain;

/// <summary>
/// EPUB 容器解析。ZIP 条目字节、container.xml、package 文档原文、
/// 资源 media type、fallback、fragment 与未识别 metadata 全部保留；
/// 输入不完整时返回带位置的诊断，不用空值伪装成功。
/// </summary>
public static class EpubParser
{
    public static string Sha256Hex(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static string GuessMediaType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".xhtml" or ".html" or ".htm" => "application/xhtml+xml",
        ".opf" => "application/oebps-package+xml",
        ".xml" => "application/xml",
        ".ncx" => "application/x-dtbncx+xml",
        ".smil" => "application/smil+xml",
        ".css" => "text/css",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        ".mp3" => "audio/mpeg",
        ".mp4" => "audio/mp4",
        ".tif" or ".tiff" => "image/tiff",
        ".js" => "text/javascript",
        _ => "application/octet-stream",
    };

    public static ImportResult Parse(byte[] zipBytes, RuleSet rules)
    {
        var diags = new List<Diagnostic>();
        var entries = new List<ZipEntryInfo>();
        var blobs = new Dictionary<string, byte[]>();
        var entryBlobs = new Dictionary<string, string>();

        ZipArchive zip;
        try { zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read); }
        catch (InvalidDataException ex)
        {
            diags.Add(Diagnostic.Make("format-corrupt",
                $"ZIP 容器无法打开: {ex.Message}", "fatal",
                new DiagLocation(null, null, null, null)));
            return new ImportResult(FailureKind.FormatCorrupt, entries, null, null, [], diags, entryBlobs);
        }

        using (zip)
        {
            if (zip.Entries.Count > rules.MaxEntries)
            {
                diags.Add(Diagnostic.Make("budget-exhausted",
                    $"条目数 {zip.Entries.Count} 超过预算 {rules.MaxEntries}", "fatal",
                    new DiagLocation(null, null, null, null)));
                return new ImportResult(FailureKind.BudgetExhausted, entries, null, null, [], diags, entryBlobs);
            }
            long total = 0;
            foreach (var e in zip.Entries)
            {
                total += e.Length;
                if (total > rules.MaxUncompressedBytes)
                {
                    diags.Add(Diagnostic.Make("budget-exhausted",
                        $"解压总字节超过预算 {rules.MaxUncompressedBytes}", "fatal",
                        new DiagLocation(e.FullName, null, null, null)));
                    return new ImportResult(FailureKind.BudgetExhausted, entries, null, null, [], diags, entryBlobs);
                }
                if (e.FullName.EndsWith('/')) continue;
                byte[] bytes;
                try
                {
                    using var s = e.Open();
                    using var ms = new MemoryStream();
                    s.CopyTo(ms);
                    bytes = ms.ToArray();
                }
                catch (InvalidDataException ex)
                {
                    diags.Add(Diagnostic.Make("format-corrupt",
                        $"条目无法解压: {ex.Message}", "fatal",
                        new DiagLocation(e.FullName, null, null, null)));
                    return new ImportResult(FailureKind.FormatCorrupt, entries, null, null, [], diags, entryBlobs);
                }
                var hash = Sha256Hex(bytes);
                var path = e.FullName.Replace('\\', '/');
                blobs[path] = bytes;
                entryBlobs[path] = hash;
                entries.Add(new ZipEntryInfo(path, e.Length, hash, GuessMediaType(path)));
            }
        }

        // container.xml —— 原文保留
        if (!blobs.TryGetValue("META-INF/container.xml", out var containerBytes))
        {
            diags.Add(Diagnostic.Make("container-missing",
                "缺少 META-INF/container.xml，无法定位 package 文档", "fatal",
                new DiagLocation("META-INF/container.xml", null, null, null)));
            return new ImportResult(FailureKind.FormatCorrupt, entries, null, null, [], diags, entryBlobs);
        }
        var containerRaw = Encoding.UTF8.GetString(containerBytes);
        var containerHash = Sha256Hex(containerBytes);

        List<string> rootfiles;
        try
        {
            var doc = XDocument.Parse(containerRaw, LoadOptions.SetLineInfo);
            rootfiles = doc.Descendants()
                .Where(x => x.Name.LocalName == "rootfile")
                .Select(x => (string?)x.Attribute("full-path"))
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p!).ToList();
        }
        catch (XmlException ex)
        {
            diags.Add(Diagnostic.Make("format-corrupt",
                $"container.xml 不是合法 XML: {ex.Message}", "fatal",
                new DiagLocation("META-INF/container.xml", ex.LineNumber, ex.LinePosition, null)));
            return new ImportResult(FailureKind.FormatCorrupt, entries, containerRaw, containerHash, [], diags, entryBlobs);
        }

        if (rootfiles.Count == 0)
        {
            diags.Add(Diagnostic.Make("semantic-unsolvable",
                "container.xml 未声明任何 rootfile，无法确定 package 文档", "fatal",
                new DiagLocation("META-INF/container.xml", null, null, null)));
            return new ImportResult(FailureKind.SemanticallyUnsolvable, entries, containerRaw, containerHash, [], diags, entryBlobs);
        }
        if (rootfiles.Count > 1)
        {
            // 多 package：无法唯一决定时保留全部合法候选
            diags.Add(Diagnostic.Make(
                MultiPackageKind(rules),
                $"container.xml 声明了 {rootfiles.Count} 个 package 文档，全部候选均保留",
                rules.MultiPackageIsError ? "error" : "warning",
                new DiagLocation("META-INF/container.xml", null, null, null),
                string.Join(";", rootfiles)));
        }

        var packages = new List<PackageModel>();
        foreach (var rf in rootfiles)
        {
            if (!blobs.TryGetValue(rf, out var opfBytes))
            {
                diags.Add(Diagnostic.Make("package-missing",
                    $"rootfile 指向的 package 文档不存在: {rf}", "error",
                    new DiagLocation(rf, null, null, null)));
                continue;
            }
            packages.Add(ParsePackage(rf, Encoding.UTF8.GetString(opfBytes), Sha256Hex(opfBytes), diags));
        }

        var failure = diags.Any(d => d.Severity == "fatal")
            ? FailureKind.FormatCorrupt : FailureKind.None;

        return new ImportResult(failure, entries, containerRaw, containerHash, packages, diags, entryBlobs);
    }

    internal static string MultiPackageKind(RuleSet rules) => "multi-package";

    private static PackageModel ParsePackage(string path, string raw, string hash, List<Diagnostic> diags)
    {
        var baseDir = ContainerPath.DirOf(path);
        var manifest = new List<ManifestItem>();
        var spine = new List<SpineRef>();
        var unknownMeta = new List<PreservedMetadata>();

        XDocument doc;
        try { doc = XDocument.Parse(raw, LoadOptions.SetLineInfo); }
        catch (XmlException ex)
        {
            diags.Add(Diagnostic.Make("format-corrupt",
                $"package 文档不是合法 XML: {ex.Message}", "fatal",
                new DiagLocation(path, ex.LineNumber, ex.LinePosition, null)));
            return new PackageModel(Guid.NewGuid().ToString("N"), path, hash, manifest, spine, unknownMeta);
        }

        var root = doc.Root;
        if (root is null || root.Name.LocalName != "package")
        {
            diags.Add(Diagnostic.Make("format-corrupt",
                "package 文档根元素不是 <package>", "fatal",
                new DiagLocation(path, null, null, null)));
            return new PackageModel(Guid.NewGuid().ToString("N"), path, hash, manifest, spine, unknownMeta);
        }

        var knownMetaNames = new HashSet<string> { "identifier", "title", "language", "meta", "link", "creator", "date", "publisher", "subject", "description", "rights", "contributor", "type", "format", "source", "relation", "coverage" };
        var dcNs = "http://purl.org/dc/elements/1.1/";
        var opfNs = root.Name.NamespaceName;

        var metaEl = root.Elements().FirstOrDefault(x => x.Name.LocalName == "metadata");
        if (metaEl is not null)
        {
            foreach (var m in metaEl.Elements())
            {
                var isKnown = m.Name.NamespaceName == dcNs
                    || (m.Name.NamespaceName == opfNs && knownMetaNames.Contains(m.Name.LocalName));
                if (!isKnown)
                    unknownMeta.Add(new PreservedMetadata(m.ToString(SaveOptions.DisableFormatting),
                        m.Name.LocalName, m.Name.NamespaceName));
            }
        }

        var manifestEl = root.Elements().FirstOrDefault(x => x.Name.LocalName == "manifest");
        if (manifestEl is not null)
        {
            foreach (var item in manifestEl.Elements().Where(x => x.Name.LocalName == "item"))
            {
                var li = (IXmlLineInfo)item;
                var id = (string?)item.Attribute("id") ?? "";
                var href = (string?)item.Attribute("href") ?? "";
                var mediaType = (string?)item.Attribute("media-type") ?? "application/octet-stream";
                var fallback = (string?)item.Attribute("fallback");
                var overlay = (string?)item.Attribute("media-overlay");
                var props = ((string?)item.Attribute("properties") ?? "")
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

                string? normalized = null;
                if (href.Length > 0)
                {
                    normalized = ContainerPath.Normalize(baseDir, href, out var err);
                    if (err is not null)
                        diags.Add(Diagnostic.Make("path-escape", err, "error",
                            new DiagLocation(path, li.HasLineInfo() ? li.LineNumber : null,
                                li.HasLineInfo() ? li.LinePosition : null, null),
                            $"item id={id} href={href}"));
                }
                manifest.Add(new ManifestItem(id, href, normalized, mediaType, fallback, overlay,
                    props, li.HasLineInfo() ? li.LineNumber : null, li.HasLineInfo() ? li.LinePosition : null));
            }
        }

        var spineEl = root.Elements().FirstOrDefault(x => x.Name.LocalName == "spine");
        if (spineEl is not null)
        {
            foreach (var ir in spineEl.Elements().Where(x => x.Name.LocalName == "itemref"))
            {
                var li = (IXmlLineInfo)ir;
                var idref = (string?)ir.Attribute("idref") ?? "";
                var linear = ((string?)ir.Attribute("linear") ?? "yes") != "no";
                spine.Add(new SpineRef(idref, linear,
                    li.HasLineInfo() ? li.LineNumber : null, li.HasLineInfo() ? li.LinePosition : null));
            }
        }

        return new PackageModel(Guid.NewGuid().ToString("N"), path, hash, manifest, spine, unknownMeta);
    }
}
