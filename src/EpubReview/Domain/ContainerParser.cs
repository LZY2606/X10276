using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace EpubReview.Domain;

public sealed record Rootfile(string FullPath, string MediaType);

public sealed record ContainerParseResult(
    IReadOnlyList<Rootfile> Rootfiles,
    List<Diagnostic> Diagnostics,
    string RawXmlHash);

public static class XmlHelper
{
    public static XDocument LoadWithLineInfo(byte[] bytes, string file, List<Diagnostic> diagnostics, string corruptCode)
    {
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
            using var ms = new MemoryStream(bytes);
            using var reader = XmlReader.Create(ms, settings);
            return XDocument.Load(reader, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex)
        {
            diagnostics.Add(new Diagnostic(corruptCode, "error",
                $"XML is not well-formed: {ex.Message}",
                new SourceLocation(file, ex.LineNumber > 0 ? ex.LineNumber : null, ex.LinePosition > 0 ? ex.LinePosition : null, null)));
            throw new ContainerCorruptException($"XML document '{file}' is corrupt: {ex.Message}", ex);
        }
    }

    public static SourceLocation Loc(XElement el, string file, string? anchor = null)
    {
        var li = (IXmlLineInfo)el;
        return new SourceLocation(file, li.HasLineInfo() ? li.LineNumber : null,
            li.HasLineInfo() ? li.LinePosition : null, anchor);
    }

    public static string RawOf(XElement el)
    {
        var sb = new StringBuilder();
        using var sw = new StringWriter(sb);
        el.Save(sw, SaveOptions.DisableFormatting);
        return sb.ToString();
    }
}

public static class ContainerParser
{
    public const string ContainerPath = "META-INF/container.xml";

    public static ContainerParseResult Parse(EpubContainer container)
    {
        var diagnostics = new List<Diagnostic>();
        var entry = container.Find(ContainerPath);
        if (entry is null)
        {
            diagnostics.Add(new Diagnostic("container-missing", "error",
                "META-INF/container.xml is absent from the container.",
                new SourceLocation(ContainerPath, null, null, null)));
            throw new ContainerCorruptException("container.xml missing");
        }

        var doc = XmlHelper.LoadWithLineInfo(entry.Bytes, ContainerPath, diagnostics, "container-xml-corrupt");
        var rootfiles = new List<Rootfile>();
        var root = doc.Root;
        if (root is null || root.Name.LocalName != "container")
        {
            diagnostics.Add(new Diagnostic("container-xml-corrupt", "error",
                "container.xml root element is not <container>.",
                new SourceLocation(ContainerPath, null, null, null)));
            throw new ContainerCorruptException("container.xml root invalid");
        }

        foreach (var rf in root.Descendants().Where(e => e.Name.LocalName == "rootfile"))
        {
            var fullPath = rf.Attribute("full-path")?.Value;
            var mediaType = rf.Attribute("media-type")?.Value ?? "application/oebps-package+xml";
            if (string.IsNullOrWhiteSpace(fullPath))
            {
                diagnostics.Add(new Diagnostic("rootfile-path-missing", "error",
                    "rootfile element lacks a full-path attribute.", XmlHelper.Loc(rf, ContainerPath)));
                continue;
            }
            var normalized = PathResolver.Normalize(fullPath);
            if (normalized is null)
            {
                diagnostics.Add(new Diagnostic("path-escape", "error",
                    $"rootfile full-path '{fullPath}' escapes the container root after normalization.",
                    XmlHelper.Loc(rf, ContainerPath)));
                continue;
            }
            rootfiles.Add(new Rootfile(normalized, mediaType));
        }

        if (rootfiles.Count == 0)
        {
            diagnostics.Add(new Diagnostic("package-missing", "error",
                "container.xml declares no usable rootfile package.",
                new SourceLocation(ContainerPath, null, null, null)));
            throw new ContainerCorruptException("no rootfile");
        }

        if (rootfiles.Count > 1)
        {
            diagnostics.Add(new Diagnostic("multi-package", "warning",
                $"container.xml declares {rootfiles.Count} rootfile packages; no unique primary reading order can be decided, all candidates are kept.",
                new SourceLocation(ContainerPath, null, null, null)));
        }

        return new ContainerParseResult(rootfiles, diagnostics, Hashing.Sha256Hex(entry.Bytes));
    }
}
