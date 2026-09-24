using System.Xml;
using System.Xml.Linq;

namespace EpubSpectrum.Ingest;

public readonly record struct LinePos(int Line, int Column)
{
    public override string ToString() => $"{Line}:{Column}";
}

public static class XmlLoad
{
    public static (XDocument? Doc, Domain.Diagnostic? Error) TryParse(string label, byte[] raw)
    {
        try
        {
            using var stream = new MemoryStream(raw, writable: false);
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            };
            using var reader = XmlReader.Create(stream, settings);
            var doc = XDocument.Load(reader, LoadOptions.SetLineInfo);
            return (doc, null);
        }
        catch (XmlException ex)
        {
            return (null, new Domain.Diagnostic(
                "XML-PARSE", Domain.Severity.Error,
                $"{label} 不是合法 XML：{ex.Message}",
                Location: $"{label}@{ex.LineNumber}:{ex.LinePosition}",
                Unit: "line:column",
                Offset: ex.LineNumber,
                Length: ex.LinePosition));
        }
    }

    public static LinePos PositionOf(XObject? obj) =>
        obj is IXmlLineInfo info && info.HasLineInfo()
            ? new LinePos(info.LineNumber, info.LinePosition)
            : new LinePos(0, 0);
}
