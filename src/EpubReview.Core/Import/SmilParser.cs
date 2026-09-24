using System.Globalization;
using System.Xml.Linq;
using EpubReview.Core.Analysis;
using EpubReview.Core.Model;

namespace EpubReview.Core.Import;

public sealed record SmilScan(
    IReadOnlyList<SmilTiming> Timings,
    IReadOnlyList<(string Src, SourceLocation Location)> AudioSrcs,
    IReadOnlyList<(string Src, SourceLocation Location)> TextSrcs,
    IReadOnlyList<Diagnostic> Diagnostics);

public static class SmilParser
{
    private static readonly XNamespace NsSmil = "http://www.w3.org/ns/SMIL";

    public static SmilScan Scan(string entryName, byte[] bytes)
    {
        var diagnostics = new List<Diagnostic>();
        var timings = new List<SmilTiming>();
        var audios = new List<(string, SourceLocation)>();
        var texts = new List<(string, SourceLocation)>();

        XDocument doc;
        try
        {
            doc = XmlUtil.LoadWithLineInfo(System.Text.Encoding.UTF8.GetString(bytes));
        }
        catch (System.Xml.XmlException ex)
        {
            diagnostics.Add(new Diagnostic("SMIL-MALFORMED",
                ex.Message, DiagnosticSeverity.Warning,
                new SourceLocation(entryName, ex.LineNumber, ex.LinePosition)));
            return new SmilScan(timings, audios, texts, diagnostics);
        }

        var media = doc.Descendants()
            .Where(e => e.Name.LocalName is "audio" or "text" or "par" or "seq");

        foreach (var el in media)
        {
            var beginRaw = el.Attribute("clipBegin")?.Value ?? el.Attribute("begin")?.Value;
            var endRaw = el.Attribute("clipEnd")?.Value ?? el.Attribute("end")?.Value;
            var begin = ParseClock(beginRaw, entryName, el, diagnostics);
            var end = ParseClock(endRaw, entryName, el, diagnostics);
            var src = el.Attribute("src")?.Value;

            if (el.Name.LocalName == "audio" && src is not null)
            {
                audios.Add((src, XmlUtil.Location(entryName, el)));
            }
            if (el.Name.LocalName == "text" && src is not null)
            {
                texts.Add((src, XmlUtil.Location(entryName, el)));
            }

            if ((beginRaw ?? endRaw ?? src) is not null)
            {
                string? textHref = el.Name.LocalName == "text" ? src : null;
                string? frag = null;
                if (textHref is not null)
                {
                    var (p, f) = PathRel.SplitFragment(textHref);
                    textHref = p;
                    frag = f;
                }
                timings.Add(new SmilTiming(
                    el.Attribute("id")?.Value ?? Hashing.StableId("smil-el", entryName, timings.Count),
                    beginRaw, endRaw, begin, end,
                    el.Name.LocalName == "audio" ? null : null,
                    el.Name.LocalName == "audio" ? src : null,
                    null,
                    textHref,
                    frag,
                    false));
            }
        }

        return new SmilScan(timings, audios, texts, diagnostics);
    }

    public static Scalar? ParseClock(string? raw, string entryName, XObject? obj, List<Diagnostic> sink)
    {
        if (raw is null) return null;

        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var secs))
        {
            return new Scalar(secs * 1000d, UnitRef.ClockMs, raw);
        }

        var mmss = raw.Split(':');
        if (mmss.Length == 3 &&
            double.TryParse(mmss[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var h) &&
            double.TryParse(mmss[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var m) &&
            double.TryParse(mmss[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
        {
            return new Scalar(((h * 60 + m) * 60 + s) * 1000d, UnitRef.ClockMs, raw);
        }

        if (mmss.Length == 2 &&
            double.TryParse(mmss[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var m2) &&
            double.TryParse(mmss[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var s2))
        {
            return new Scalar((m2 * 60 + s2) * 1000d, UnitRef.ClockMs, raw);
        }

        sink.Add(new Diagnostic("SMIL-CLOCK-UNPARSEABLE",
            $"Clock value '{raw}' could not be converted; raw representation retained at origin.",
            DiagnosticSeverity.Warning,
            XmlUtil.Location(entryName, obj)));
        return new Scalar(double.NaN, UnitRef.ClockRaw, raw, "SMIL-CLOCK-UNPARSEABLE");
    }
}
