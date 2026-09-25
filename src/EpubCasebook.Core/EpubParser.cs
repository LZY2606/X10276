using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace EpubCasebook.Core;

public sealed class ParsedContainer
{
    public List<Diagnostic> Diagnostics { get; } = new();
    public List<string> Rootfiles { get; set; } = new();
    public Dictionary<string, PackageDoc> Packages { get; } = new(StringComparer.Ordinal);
    public HashSet<string> MalformedPackages { get; } = new(StringComparer.Ordinal);
    public HashSet<string> EntryNames { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, HashSet<string>> IdsByDoc { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<(string RawHref, string Element)>> RefsByDoc { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, NavNode> NavTreeByDoc { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, OverlayDoc> OverlaysByPath { get; } = new(StringComparer.Ordinal);
    public RunResultKind? Fatal { get; set; }
}

public static class EpubParser
{
    private static readonly HashSet<string> KnownMetaNames = new(StringComparer.Ordinal)
        { "title", "identifier", "language", "creator", "date" };

    private static readonly HashSet<string> KnownMetaProperties = new(StringComparer.Ordinal)
        { "dcterms:modified", "dcterms:identifier", "dcterms:title", "dcterms:language" };

    public static ParsedContainer Parse(IReadOnlyList<ZipEntryBytes> entries)
    {
        var pc = new ParsedContainer();
        var byName = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var e in entries)
            byName[e.Name] = e.Bytes;
        pc.EntryNames = byName.Keys.ToHashSet(StringComparer.Ordinal);

        if (!byName.TryGetValue("META-INF/container.xml", out var containerBytes))
        {
            pc.Diagnostics.Add(new Diagnostic(Diag.NoContainer, "error",
                "缺少 META-INF/container.xml，无法定位 package 文档", "META-INF/container.xml", null));
            pc.Fatal = RunResultKind.Malformed;
            return pc;
        }
        if (!TryParseXml(containerBytes, "META-INF/container.xml", pc.Diagnostics, out var containerDoc))
        {
            pc.Fatal = RunResultKind.Malformed;
            return pc;
        }
        foreach (var rf in containerDoc.Descendants().Where(e => e.Name.LocalName == "rootfile"))
        {
            var fp = rf.Attribute("full-path")?.Value;
            if (!string.IsNullOrWhiteSpace(fp))
                pc.Rootfiles.Add(fp);
        }
        pc.Rootfiles = pc.Rootfiles.Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
        if (pc.Rootfiles.Count == 0)
        {
            pc.Diagnostics.Add(new Diagnostic(Diag.NoRootfile, "error",
                "container.xml 未声明任何 rootfile，没有任何合法候选，语义无解", "META-INF/container.xml", null));
            pc.Fatal = RunResultKind.Unsolvable;
            return pc;
        }
        if (pc.Rootfiles.Count > 1)
            pc.Diagnostics.Add(new Diagnostic(Diag.MultiPackage, "info",
                $"container.xml 声明 {pc.Rootfiles.Count} 个 package，全部保留为合法候选",
                "META-INF/container.xml", string.Join(",", pc.Rootfiles)));

        foreach (var rf in pc.Rootfiles)
            ParsePackage(pc, byName, rf);
        return pc;
    }

    private static void ParsePackage(ParsedContainer pc, Dictionary<string, byte[]> byName, string rootfile)
    {
        if (!byName.TryGetValue(rootfile, out var bytes))
        {
            pc.Diagnostics.Add(new Diagnostic(Diag.MissingResource, "error",
                $"rootfile 指向的 package 文档不存在: {rootfile}", "META-INF/container.xml", rootfile));
            pc.MalformedPackages.Add(rootfile);
            return;
        }
        if (!TryParseXml(bytes, rootfile, pc.Diagnostics, out var doc))
        {
            pc.MalformedPackages.Add(rootfile);
            return;
        }
        var baseDir = ContainerPath.DirOf(rootfile);
        var manifest = new List<ManifestItem>();
        var spine = new List<SpineRef>();
        var unknown = new List<string>();
        var known = new Dictionary<string, string>(StringComparer.Ordinal);

        var packageEl = doc.Root;
        if (packageEl == null || packageEl.Name.LocalName != "package")
        {
            pc.Diagnostics.Add(new Diagnostic(Diag.MalformedXml, "error",
                "package 文档根元素不是 <package>", rootfile, doc.Root?.Name.LocalName));
            pc.MalformedPackages.Add(rootfile);
            return;
        }

        var metadataEl = packageEl.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata");
        if (metadataEl != null)
        {
            foreach (var el in metadataEl.Elements())
            {
                var local = el.Name.LocalName;
                var prop = el.Attribute("property")?.Value;
                bool isKnown = prop != null
                    ? KnownMetaProperties.Contains(prop)
                    : el.Name.NamespaceName.Contains("dc/elements") && KnownMetaNames.Contains(local);
                if (isKnown)
                    known[prop ?? local] = el.Value.Trim();
                else
                {
                    var raw = el.ToString(SaveOptions.None);
                    unknown.Add(raw);
                    pc.Diagnostics.Add(new Diagnostic(Diag.UnknownMetadata, "info",
                        $"未识别 metadata 已按原文保留: <{local}>", LocationOf(el, rootfile), raw));
                }
            }
        }

        var manifestEl = packageEl.Elements().FirstOrDefault(e => e.Name.LocalName == "manifest");
        if (manifestEl != null)
        {
            foreach (var item in manifestEl.Elements().Where(e => e.Name.LocalName == "item"))
            {
                var id = item.Attribute("id")?.Value ?? "";
                var rawHref = item.Attribute("href")?.Value ?? "";
                var mediaType = item.Attribute("media-type")?.Value ?? "application/octet-stream";
                var (pathOnly, _) = ContainerPath.SplitFragment(rawHref);
                string? href = null;
                if (!ContainerPath.TryNormalize(baseDir, pathOnly, out var norm, out var err))
                    pc.Diagnostics.Add(new Diagnostic(
                        err != null && err.Contains("decode") ? Diag.PathUndecodable : Diag.PathEscape,
                        "error", $"manifest href 无法安全规范化: {err}", LocationOf(item, rootfile), rawHref));
                else
                    href = norm;
                manifest.Add(new ManifestItem(id, rawHref, href, mediaType,
                    item.Attribute("fallback")?.Value,
                    item.Attribute("media-overlay")?.Value,
                    item.Attribute("properties")?.Value));
            }
        }

        var spineEl = packageEl.Elements().FirstOrDefault(e => e.Name.LocalName == "spine");
        if (spineEl != null)
        {
            var index = 0;
            foreach (var ir in spineEl.Elements().Where(e => e.Name.LocalName == "itemref"))
            {
                var idref = ir.Attribute("idref")?.Value ?? "";
                var linear = !string.Equals(ir.Attribute("linear")?.Value, "no", StringComparison.OrdinalIgnoreCase);
                spine.Add(new SpineRef(idref, linear, index++));
            }
        }

        var pkg = new PackageDoc(rootfile, baseDir, manifest, spine, unknown, known);
        pc.Packages[rootfile] = pkg;

        foreach (var item in manifest)
        {
            if (item.Href == null || !byName.ContainsKey(item.Href))
                continue;
            var props = item.Properties ?? "";
            if (item.MediaType.Contains("xhtml", StringComparison.OrdinalIgnoreCase))
            {
                ParseContentDoc(pc, byName[item.Href], item.Href, baseDir);
                if (props.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("nav"))
                    ParseNav(pc, byName[item.Href], item.Href, baseDir);
            }
            else if (item.MediaType.Contains("smil", StringComparison.OrdinalIgnoreCase))
                ParseSmil(pc, byName[item.Href], item.Href, baseDir);
        }
    }

    private static void ParseContentDoc(ParsedContainer pc, byte[] bytes, string path, string baseDir)
    {
        if (!TryParseXml(bytes, path, pc.Diagnostics, out var doc))
            return;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var refs = new List<(string, string)>();
        foreach (var el in doc.Descendants())
        {
            var id = el.Attribute("id")?.Value;
            if (!string.IsNullOrEmpty(id))
                ids.Add(id);
            foreach (var attr in el.Attributes())
            {
                var ln = attr.Name.LocalName;
                if (ln is "href" or "src" or "poster")
                    refs.Add((attr.Value, el.Name.LocalName));
            }
        }
        pc.IdsByDoc[path] = ids;
        pc.RefsByDoc[path] = refs;
    }

    private static void ParseNav(ParsedContainer pc, byte[] bytes, string path, string baseDir)
    {
        if (!TryParseXml(bytes, path, pc.Diagnostics, out var doc))
            return;
        var nav = doc.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "nav" &&
            e.Attributes().Any(a => a.Name.LocalName == "type" &&
                a.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("toc")));
        if (nav == null)
            return;
        var root = new NavNode("toc", null, null, null, "ok", new List<NavNode>());
        var ol = nav.Elements().FirstOrDefault(e => e.Name.LocalName == "ol");
        if (ol != null)
            foreach (var li in ol.Elements().Where(e => e.Name.LocalName == "li"))
                root.Children.Add(ParseNavLi(li, baseDir));
        pc.NavTreeByDoc[path] = root;
    }

    private static NavNode ParseNavLi(XElement li, string baseDir)
    {
        var a = li.Elements().FirstOrDefault(e => e.Name.LocalName is "a" or "span");
        var title = a?.Value.Trim() ?? "(untitled)";
        var rawHref = a?.Attribute("href")?.Value;
        string? href = null;
        string? frag = null;
        if (rawHref != null)
        {
            var (p, f) = ContainerPath.SplitFragment(rawHref);
            frag = f;
            if (ContainerPath.TryNormalize(baseDir, p, out var norm, out _))
                href = norm;
        }
        var node = new NavNode(title, rawHref, href, frag, "pending", new List<NavNode>());
        var ol = li.Elements().FirstOrDefault(e => e.Name.LocalName == "ol");
        if (ol != null)
            foreach (var child in ol.Elements().Where(e => e.Name.LocalName == "li"))
                node.Children.Add(ParseNavLi(child, baseDir));
        return node;
    }

    private static void ParseSmil(ParsedContainer pc, byte[] bytes, string path, string baseDir)
    {
        if (!TryParseXml(bytes, path, pc.Diagnostics, out var doc))
            return;
        var clips = new List<OverlayClip>();
        foreach (var par in doc.Descendants().Where(e => e.Name.LocalName == "par"))
        {
            var text = par.Elements().FirstOrDefault(e => e.Name.LocalName == "text");
            var audio = par.Elements().FirstOrDefault(e => e.Name.LocalName == "audio");
            if (text == null && audio == null)
                continue;
            var textRaw = text?.Attribute("src")?.Value ?? "";
            var audioRaw = audio?.Attribute("src")?.Value ?? "";
            var (tp, tf) = ContainerPath.SplitFragment(textRaw);
            ContainerPath.TryNormalize(baseDir, tp, out var textPath, out _);
            var (ap, _) = ContainerPath.SplitFragment(audioRaw);
            ContainerPath.TryNormalize(baseDir, ap, out var audioPath, out _);
            var rawBegin = audio?.Attribute("clipBegin")?.Value ?? par.Attribute("clipBegin")?.Value;
            var rawEnd = audio?.Attribute("clipEnd")?.Value ?? par.Attribute("clipEnd")?.Value;
            double? begin = null, end = null;
            if (rawBegin != null)
            {
                if (TryParseClock(rawBegin, out var b))
                    begin = b;
                else
                    pc.Diagnostics.Add(new Diagnostic(Diag.ClockUnconvertible, "warning",
                        "clipBegin 无法转换为秒，原值保留", path, rawBegin));
            }
            if (rawEnd != null)
            {
                if (TryParseClock(rawEnd, out var e2))
                    end = e2;
                else
                    pc.Diagnostics.Add(new Diagnostic(Diag.ClockUnconvertible, "warning",
                        "clipEnd 无法转换为秒，原值保留", path, rawEnd));
            }
            clips.Add(new OverlayClip(textRaw, textPath, tf, audioRaw, audioPath, begin, end, rawBegin, rawEnd));
        }
        pc.OverlaysByPath[path] = new OverlayDoc(path, clips);
    }

    public static bool TryParseClock(string raw, out double seconds)
    {
        seconds = 0;
        var s = raw.Trim();
        if (s.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
        {
            if (double.TryParse(s[..^2], out var ms)) { seconds = ms / 1000.0; return true; }
            return false;
        }
        if (s.EndsWith('s') || s.EndsWith('S'))
        {
            if (double.TryParse(s[..^1], out var v)) { seconds = v; return true; }
            return false;
        }
        if (s.Contains(':'))
        {
            var parts = s.Split(':');
            if (parts.Length is < 2 or > 3)
                return false;
            double total = 0;
            foreach (var part in parts)
            {
                if (!double.TryParse(part, out var v))
                    return false;
                total = total * 60 + v;
            }
            seconds = total;
            return true;
        }
        return double.TryParse(s, out seconds);
    }

    private static bool TryParseXml(byte[] bytes, string location, List<Diagnostic> diags, out XDocument doc)
    {
        try
        {
            doc = XDocument.Parse(Encoding.UTF8.GetString(bytes), LoadOptions.SetLineInfo);
            return true;
        }
        catch (XmlException ex)
        {
            diags.Add(new Diagnostic(Diag.MalformedXml, "error",
                $"XML 解析失败: {ex.Message}", $"{location}:{ex.LineNumber}:{ex.LinePosition}", null));
            doc = null!;
            return false;
        }
    }

    private static string LocationOf(XElement el, string file)
    {
        var li = (IXmlLineInfo)el;
        return li.HasLineInfo() ? $"{file}:{li.LineNumber}:{li.LinePosition}" : file;
    }
}
