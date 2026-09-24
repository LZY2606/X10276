using System.Xml;
using System.Xml.Linq;

namespace EpubReview.Domain;

public static class ContainerParser
{
    public const string ContainerPath = "META-INF/container.xml";

    public static (IReadOnlyList<Rootfile> Rootfiles, List<Diagnostic> Diagnostics, AnalysisOutcome? Failure) Parse(EpubContainer container)
    {
        var diagnostics = new List<Diagnostic>();
        var entry = container.Find(ContainerPath);
        if (entry is null)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticCodes.ContainerMissing, Severity.Error,
                "缺少 META-INF/container.xml", ContainerPath, null, null, null));
            return (Array.Empty<Rootfile>(), diagnostics, AnalysisOutcome.FormatCorrupt);
        }
        XDocument doc;
        try
        {
            doc = XDocument.Parse(System.Text.Encoding.UTF8.GetString(entry.Bytes), LoadOptions.SetLineInfo);
        }
        catch (XmlException ex)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticCodes.XmlCorrupt, Severity.Error,
                $"container.xml 不是合法 XML: {ex.Message}", ContainerPath, ex.LineNumber, ex.LinePosition, null));
            return (Array.Empty<Rootfile>(), diagnostics, AnalysisOutcome.FormatCorrupt);
        }
        var rootfiles = new List<Rootfile>();
        var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
        foreach (var rf in doc.Descendants(ns + "rootfile"))
        {
            var path = rf.Attribute("full-path")?.Value;
            if (string.IsNullOrWhiteSpace(path)) continue;
            rootfiles.Add(new Rootfile(PathResolver.NormalizeEntryName(path), rf.Attribute("media-type")?.Value));
        }
        if (rootfiles.Count == 0)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticCodes.NoRootfile, Severity.Error,
                "container.xml 未声明任何 rootfile，语义无解", ContainerPath, null, null, null));
            return (rootfiles, diagnostics, AnalysisOutcome.SemanticUnresolvable);
        }
        if (rootfiles.Count > 1)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticCodes.MultiplePackages, Severity.Warning,
                $"container.xml 声明了 {rootfiles.Count} 个 package，逐一审查", ContainerPath, null, null,
                string.Join(",", rootfiles.Select(r => r.FullPath))));
        }
        return (rootfiles, diagnostics, null);
    }
}

public static class PackageParser
{
    private static readonly HashSet<string> RecognizedMetadata = new(StringComparer.Ordinal)
    {
        "{http://purl.org/dc/elements/1.1/}title",
        "{http://purl.org/dc/elements/1.1/}creator",
        "{http://purl.org/dc/elements/1.1/}language",
        "{http://purl.org/dc/elements/1.1/}identifier",
        "{http://purl.org/dc/elements/1.1/}date",
        "{http://purl.org/dc/elements/1.1/}publisher",
    };

    public static (PackageDocument? Doc, List<Diagnostic> Diagnostics, AnalysisOutcome? Failure) Parse(
        EpubContainer container, Rootfile rootfile)
    {
        var diagnostics = new List<Diagnostic>();
        var entry = container.Find(rootfile.FullPath);
        if (entry is null)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticCodes.MissingResource, Severity.Error,
                $"rootfile 指向的 package 文档不存在: {rootfile.FullPath}", rootfile.FullPath, null, null, null));
            return (null, diagnostics, AnalysisOutcome.SemanticUnresolvable);
        }
        XDocument doc;
        try
        {
            doc = XDocument.Parse(System.Text.Encoding.UTF8.GetString(entry.Bytes), LoadOptions.SetLineInfo);
        }
        catch (XmlException ex)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticCodes.XmlCorrupt, Severity.Error,
                $"package 文档不是合法 XML: {ex.Message}", rootfile.FullPath, ex.LineNumber, ex.LinePosition, null));
            return (null, diagnostics, AnalysisOutcome.FormatCorrupt);
        }
        var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
        var manifestEl = doc.Root?.Element(ns + "manifest");
        var spineEl = doc.Root?.Element(ns + "spine");
        if (manifestEl is null || spineEl is null)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticCodes.PackageStructureMissing, Severity.Error,
                $"package 文档缺少 {(manifestEl is null ? "manifest" : "spine")} 元素，语义无解",
                rootfile.FullPath, null, null, null));
            return (null, diagnostics, AnalysisOutcome.SemanticUnresolvable);
        }

        var manifest = new List<ManifestItem>();
        foreach (var item in manifestEl.Elements(ns + "item"))
        {
            var line = (item as IXmlLineInfo).LineNumber;
            manifest.Add(new ManifestItem(
                item.Attribute("id")?.Value ?? string.Empty,
                item.Attribute("href")?.Value ?? string.Empty,
                item.Attribute("media-type")?.Value,
                item.Attribute("fallback")?.Value,
                (item.Attribute("properties")?.Value ?? "")
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries),
                item.Attribute("media-overlay")?.Value,
                line));
        }

        var spine = new List<SpineItemRef>();
        var index = 0;
        foreach (var itemref in spineEl.Elements(ns + "itemref"))
        {
            var line = (itemref as IXmlLineInfo).LineNumber;
            spine.Add(new SpineItemRef(
                itemref.Attribute("idref")?.Value ?? string.Empty,
                !string.Equals(itemref.Attribute("linear")?.Value, "no", StringComparison.OrdinalIgnoreCase),
                index++, line));
        }

        var metadata = new List<UnknownMetadata>();
        var metadataEl = doc.Root?.Element(ns + "metadata");
        if (metadataEl is not null)
        {
            foreach (var el in metadataEl.Elements())
            {
                var recognized = RecognizedMetadata.Contains(el.Name.ToString());
                metadata.Add(new UnknownMetadata(
                    el.Name.ToString(),
                    el.Value,
                    el.ToString(SaveOptions.DisableFormatting),
                    recognized));
                if (!recognized)
                {
                    var line = (el as IXmlLineInfo).LineNumber;
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnknownMetadataPreserved, Severity.Info,
                        $"未识别 metadata '{el.Name.LocalName}' 已原样保留", rootfile.FullPath, line, null,
                        el.Name.ToString()));
                }
            }
        }

        return (new PackageDocument(
            rootfile.FullPath,
            PathResolver.DirOf(rootfile.FullPath),
            manifest, spine, metadata), diagnostics, null);
    }
}
