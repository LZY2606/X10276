using System.Xml.Linq;
using EpubReview.Core.Model;

namespace EpubReview.Core.Import;

public sealed record XhtmlScan(
    IReadOnlySet<string> AnchorIds,
    IReadOnlyList<(string Href, SourceLocation Location)> Hrefs,
    IReadOnlyList<NavElement> NavElements,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record NavElement(
    int Depth,
    string? Text,
    string Href,
    SourceLocation Location);

public static class XhtmlScanner
{
    private static readonly XNamespace NsXhtml = "http://www.w3.org/1999/xhtml";
    private static readonly XNamespace NsEpub = "http://www.idpf.org/2007/ops";

    public static XhtmlScan Scan(string entryName, byte[] bytes)
    {
        var diagnostics = new List<Diagnostic>();
        var ids = new HashSet<string>();
        var hrefs = new List<(string, SourceLocation)>();
        var navs = new List<NavElement>();

        XDocument doc;
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        try
        {
            doc = XmlUtil.LoadWithLineInfo(text);
        }
        catch (System.Xml.XmlException ex)
        {
            diagnostics.Add(new Diagnostic("XHTML-MALFORMED",
                ex.Message, DiagnosticSeverity.Warning,
                new SourceLocation(entryName, ex.LineNumber, ex.LinePosition)));
            return new XhtmlScan(ids, hrefs, navs, diagnostics);
        }

        foreach (var el in doc.Descendants())
        {
            var id = el.Attribute("id")?.Value;
            if (id is not null) ids.Add(id);
            if (el.Name == NsXhtml + "a" || el.Name == NsXhtml + "area")
            {
                var href = el.Attribute("href")?.Value;
                if (href is not null)
                {
                    hrefs.Add((href, XmlUtil.Location(entryName, el)));
                }
            }
        }

        foreach (var nav in doc.Descendants(NsXhtml + "nav")
                     .Where(n => (n.Attribute(NsEpub + "type")?.Value ?? "")
                                 .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                                 .Contains("toc")))
        {
            foreach (var li in nav.Descendants(NsXhtml + "li"))
            {
                var anchor = li.Element(NsXhtml + "a");
                if (anchor is null) continue;
                var href = anchor.Attribute("href")?.Value;
                if (href is null) continue;
                int depth = 0;
                var parent = li.Parent;
                while (parent is not null)
                {
                    if (parent.Name == NsXhtml + "ol" || parent.Name == NsXhtml + "ul") depth++;
                    parent = parent.Parent;
                }
                navs.Add(new NavElement(depth, anchor.Value.Trim(), href,
                    XmlUtil.Location(entryName, anchor)));
            }
        }

        return new XhtmlScan(ids, hrefs, navs, diagnostics);
    }
}
