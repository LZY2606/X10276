using System.Xml;
using System.Xml.Linq;

namespace EpubReview.Core.Import;

internal static class XmlUtil
{
    public static readonly XNamespace NsContainer = "urn:oasis:names:tc:opendocument:xmlns:container";
    public static readonly XNamespace NsOpf = "http://www.idpf.org/2007/opf";
    public static readonly XNamespace NsDc = "http://purl.org/dc/elements/1.1/";

    public static XDocument LoadWithLineInfo(string text)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreWhitespace = false,
            IgnoreComments = false
        };
        using var sr = new StringReader(text);
        using var reader = XmlReader.Create(sr, settings);
        return XDocument.Load(reader, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
    }

    public static Model.SourceLocation Location(string source, XObject? obj, string? xpointer = null)
    {
        int? line = null;
        int? col = null;
        if (obj is IXmlLineInfo info && info.HasLineInfo())
        {
            line = info.LineNumber;
            col = info.LinePosition;
        }
        return new Model.SourceLocation(source, line, col, null, xpointer);
    }
}
