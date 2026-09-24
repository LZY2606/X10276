using System.Xml;
using System.Xml.Linq;

namespace EpubReview.Core;

public static class EpubNamespaces
{
    public static readonly XNamespace Container = "urn:oasis:names:tc:opendocument:xmlns:container";
    public static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    public static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    public static readonly XNamespace Xhtml = "http://www.w3.org/1999/xhtml";
    public static readonly XNamespace Smil = "http://www.w3.org/ns/SMIL";
    public static readonly XNamespace SmilLegacy = "http://www.w3.org/2001/SMIL20/";
    public static readonly XNamespace EpubSmil = "http://www.idpf.org/epub/301/wd/epub-contentdocs-20140916#sec-smil";
}

internal static class Xml
{
    public static XDocument? Parse(byte[] bytes, string entryName, List<Diagnostic> diagnostics)
    {
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
            };
            using var ms = new MemoryStream(bytes);
            using var reader = XmlReader.Create(ms, settings);
            var doc = XDocument.Load(reader, LoadOptions.SetLineInfo);
            return doc;
        }
        catch (XmlException ex)
        {
            diagnostics.Add(Diagnostic.Create(
                "XML_NOT_WELL_FORMED", Severity.Error,
                $"XML 无法解析：{ex.Message}",
                new TextLocation(entryName, ex.LineNumber, ex.LinePosition, null)));
            return null;
        }
    }

    public static TextLocation? Loc(XObject? obj, string entryName, string? xpath = null)
    {
        if (obj is not null && obj is IXmlLineInfo info && info.HasLineInfo())
            return new TextLocation(entryName, info.LineNumber, info.LinePosition, xpath);
        return new TextLocation(entryName, null, null, xpath);
    }
}
