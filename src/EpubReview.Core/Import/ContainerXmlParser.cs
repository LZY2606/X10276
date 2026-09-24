using System.Xml.Linq;
using EpubReview.Core.Model;

namespace EpubReview.Core.Import;

public static class ContainerXmlParser
{
    public static (ContainerDocument? Document, List<Diagnostic> Diagnostics) Parse(ZipEntryRecord entry)
    {
        var diagnostics = new List<Diagnostic>();
        var text = System.Text.Encoding.UTF8.GetString(entry.Bytes);
        XDocument doc;
        try
        {
            doc = XmlUtil.LoadWithLineInfo(text);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException)
        {
            var xe = (System.Xml.XmlException)ex;
            diagnostics.Add(new Diagnostic("CONTAINER-XML-MALFORMED",
                ex.Message, DiagnosticSeverity.Error,
                new SourceLocation(entry.Name, xe.LineNumber, xe.LinePosition)));
            return (null, diagnostics);
        }

        var root = doc.Root;
        if (root?.Name.LocalName != "container")
        {
            diagnostics.Add(new Diagnostic("CONTAINER-ROOT",
                "container.xml root element must be {urn:oasis...}container.",
                DiagnosticSeverity.Error, XmlUtil.Location(entry.Name, root)));
            return (null, diagnostics);
        }

        var rootFiles = new List<RootFileRef>();
        foreach (var rf in root.Descendants(XmlUtil.NsContainer + "rootfile"))
        {
            var path = rf.Attribute("full-path")?.Value;
            var mediaType = rf.Attribute("media-type")?.Value;
            if (string.IsNullOrWhiteSpace(path))
            {
                diagnostics.Add(new Diagnostic("CONTAINER-ROOTFILE-NOPATH",
                    "rootfile element missing full-path attribute.",
                    DiagnosticSeverity.Error, XmlUtil.Location(entry.Name, rf)));
                continue;
            }
            rootFiles.Add(new RootFileRef(path!, mediaType, XmlUtil.Location(entry.Name, rf)));
        }

        if (rootFiles.Count == 0)
        {
            diagnostics.Add(new Diagnostic("CONTAINER-NO-ROOTFILE",
                "container.xml declares no rootfile elements.",
                DiagnosticSeverity.Error, XmlUtil.Location(entry.Name, root)));
        }

        if (rootFiles.Count > 1)
        {
            diagnostics.Add(new Diagnostic("CONTAINER-MULTI-PACKAGE",
                $"container.xml declares {rootFiles.Count} rootfiles; all packages are retained as legal candidates.",
                DiagnosticSeverity.Info, XmlUtil.Location(entry.Name, root),
                new Dictionary<string, string> { ["count"] = rootFiles.Count.ToString() }));
        }

        var version = root.Attribute("version")?.Value;
        return (new ContainerDocument(entry.Name, entry.Bytes, text, entry.Sha256, rootFiles, version), diagnostics);
    }
}
