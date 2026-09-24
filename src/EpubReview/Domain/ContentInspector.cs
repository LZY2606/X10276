using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace EpubReview.Domain;

public sealed record ContentRef(string Attr, string Value, int? Line);
public sealed record XhtmlInspection(IReadOnlyList<ContentRef> Refs, IReadOnlySet<string> Ids, Diagnostic? ParseError);
public sealed record SmilClip(string TextSrc, string? AudioSrc, double? BeginSeconds, double? EndSeconds, int? Line);
public sealed record SmilInspection(IReadOnlyList<SmilClip> Clips, Diagnostic? ParseError);

public static class ContentInspector
{
    private static readonly HashSet<string> RefAttrs = new(StringComparer.OrdinalIgnoreCase)
        { "href", "src", "poster", "data" };

    public static XhtmlInspection InspectXhtml(byte[] bytes, string entryName)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(Encoding.UTF8.GetString(bytes), LoadOptions.SetLineInfo);
        }
        catch (XmlException ex)
        {
            return new XhtmlInspection(Array.Empty<ContentRef>(), new HashSet<string>(),
                new Diagnostic(DiagnosticCodes.XmlCorrupt, Severity.Error,
                    $"内容文档不是合法 XML: {ex.Message}", entryName, ex.LineNumber, ex.LinePosition, null));
        }
        var refs = new List<ContentRef>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var el in doc.Descendants())
        {
            foreach (var attr in el.Attributes())
            {
                if (attr.IsNamespaceDeclaration) continue;
                var local = attr.Name.LocalName;
                if (local == "id")
                    ids.Add(attr.Value);
                else if (RefAttrs.Contains(local) && !string.IsNullOrWhiteSpace(attr.Value))
                    refs.Add(new ContentRef(local, attr.Value, (el as IXmlLineInfo).LineNumber));
            }
        }
        return new XhtmlInspection(refs, ids, null);
    }

    private static readonly Regex CssUrl = new(@"url\(\s*[""']?([^""')\s]+)[""']?\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CssImport = new(@"@import\s+[""']([^""']+)[""']",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static IReadOnlyList<ContentRef> InspectCss(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        var refs = new List<ContentRef>();
        foreach (Match m in CssUrl.Matches(text))
            refs.Add(new ContentRef("url", m.Groups[1].Value, LineOf(text, m.Index)));
        foreach (Match m in CssImport.Matches(text))
            refs.Add(new ContentRef("import", m.Groups[1].Value, LineOf(text, m.Index)));
        return refs;
    }

    private static int LineOf(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < text.Length; i++)
            if (text[i] == '\n') line++;
        return line;
    }

    public static SmilInspection InspectSmil(byte[] bytes, string entryName)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(Encoding.UTF8.GetString(bytes), LoadOptions.SetLineInfo);
        }
        catch (XmlException ex)
        {
            return new SmilInspection(Array.Empty<SmilClip>(),
                new Diagnostic(DiagnosticCodes.XmlCorrupt, Severity.Error,
                    $"SMIL 文档不是合法 XML: {ex.Message}", entryName, ex.LineNumber, ex.LinePosition, null));
        }
        var clips = new List<SmilClip>();
        foreach (var par in doc.Descendants().Where(e => e.Name.LocalName == "par"))
        {
            var text = par.Elements().FirstOrDefault(e => e.Name.LocalName == "text");
            var audio = par.Elements().FirstOrDefault(e => e.Name.LocalName == "audio");
            var textSrc = text?.Attribute("src")?.Value;
            if (textSrc is null) continue;
            clips.Add(new SmilClip(
                textSrc,
                audio?.Attribute("src")?.Value,
                ParseClock(audio?.Attribute("clipBegin")?.Value),
                ParseClock(audio?.Attribute("clipEnd")?.Value),
                (par as IXmlLineInfo).LineNumber));
        }
        return new SmilInspection(clips, null);
    }

    public static double? ParseClock(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (value.EndsWith("ms", StringComparison.OrdinalIgnoreCase) &&
            double.TryParse(value[..^2], out var ms))
            return ms / 1000.0;
        if (value.EndsWith('s') && double.TryParse(value[..^1], out var s))
            return s;
        var parts = value.Split(':');
        if (parts.Length is 2 or 3 &&
            parts.All(p => double.TryParse(p, out _)))
        {
            var nums = parts.Select(double.Parse).ToArray();
            return nums.Length == 2
                ? nums[0] * 60 + nums[1]
                : nums[0] * 3600 + nums[1] * 60 + nums[2];
        }
        return null;
    }
}
