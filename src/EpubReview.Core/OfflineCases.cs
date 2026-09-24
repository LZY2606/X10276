using System.IO.Compression;
using System.Text;

namespace EpubReview.Core;

public sealed record OfflineCase(string Key, string Title, string ChangedDirectSource, string Narrative, byte[] Bytes, IReadOnlyList<string> ExpectedCodes);

public static class OfflineCaseFactory
{
    public const string BaselineKey = "baseline";

    public static IReadOnlyList<OfflineCase> All() =>
        new[]
        {
            "baseline", "multiple-packages", "fallback-cycle", "fragment-missing", "path-escape",
            "spine-duplicate", "media-overlay-broken", "unknown-metadata-roundtrip", "local-chapter-revision"
        }.Select(Build).ToList();

    public static OfflineCase Build(string key)
    {
        var files = BaselineFiles();
        var expected = new List<string>();
        var source = "none";
        var title = key switch
        {
            "baseline" => "基线：完整 EPUB 审阅链",
            "multiple-packages" => "反例：多 package 根文档",
            "fallback-cycle" => "反例：fallback 链循环",
            "fragment-missing" => "反例：fragment 不存在",
            "path-escape" => "反例：路径越出容器",
            "spine-duplicate" => "反例：spine 重复",
            "media-overlay-broken" => "反例：media-overlay 断链",
            "unknown-metadata-roundtrip" => "往返：未知 metadata 原样保留",
            "local-chapter-revision" => "局部源修订：第一章补充证据说明",
            _ => throw new ArgumentOutOfRangeException(nameof(key))
        };
        ApplyVariant(key, files, expected, ref source);
        var narrative = "数字出版团队导入 EPUB 容器，沿 package manifest、spine、navigation、media-overlay 和资源引用图完成证据审阅；派生层不覆盖不可变 ZIP 条目。";
        return new OfflineCase(key, title, source, narrative, BuildZip(files), expected);
    }

    private static void ApplyVariant(string key, Dictionary<string, byte[]> files, List<string> expected, ref string source)
    {
        switch (key)
        {
            case "baseline": break;
            case "multiple-packages":
                source = "META-INF/container.xml";
                files[source] = Encoding.UTF8.GetBytes(ContainerXml(true));
                expected.Add("CONTAINER.MULTIPLE_PACKAGES");
                break;
            case "fallback-cycle":
            case "path-escape":
            case "spine-duplicate":
            case "media-overlay-broken":
            case "unknown-metadata-roundtrip":
                source = "OEBPS/content.opf";
                files[source] = Encoding.UTF8.GetBytes(PackageXml(
                    fallbackCycle: key == "fallback-cycle",
                    pathEscape: key == "path-escape",
                    spineDuplicate: key == "spine-duplicate",
                    brokenOverlay: key == "media-overlay-broken",
                    extraUnknownMetadata: key == "unknown-metadata-roundtrip"));
                expected.Add(key switch
                {
                    "fallback-cycle" => "FALLBACK.CYCLE",
                    "path-escape" => "PATH.ESCAPE",
                    "spine-duplicate" => "SPINE.DUPLICATE",
                    "media-overlay-broken" => "MEDIA_OVERLAY.MISSING",
                    _ => "METADATA.UNKNOWN_RETAINED"
                });
                break;
            case "fragment-missing":
                source = "OEBPS/nav.xhtml";
                files[source] = Encoding.UTF8.GetBytes(NavXml(true));
                expected.Add("FRAGMENT.MISSING");
                break;
            case "local-chapter-revision":
                source = "OEBPS/ch1.xhtml";
                files[source] = Encoding.UTF8.GetBytes(Chapter1Xml(true));
                break;
        }
    }

    private static Dictionary<string, byte[]> BaselineFiles() => new(StringComparer.Ordinal)
    {
        ["mimetype"] = Encoding.ASCII.GetBytes("application/epub+zip"),
        ["META-INF/container.xml"] = Encoding.UTF8.GetBytes(ContainerXml(false)),
        ["OEBPS/content.opf"] = Encoding.UTF8.GetBytes(PackageXml()),
        ["OEBPS/nav.xhtml"] = Encoding.UTF8.GetBytes(NavXml()),
        ["OEBPS/ch1.xhtml"] = Encoding.UTF8.GetBytes(Chapter1Xml(false)),
        ["OEBPS/ch2.xhtml"] = Encoding.UTF8.GetBytes(Chapter2Xml()),
        ["OEBPS/stylesheet.css"] = Encoding.UTF8.GetBytes("body{font-family:sans-serif}"),
        ["OEBPS/cover.png"] = Encoding.ASCII.GetBytes("PNG-PLACEHOLDER original bytes retained"),
        ["OEBPS/narration.mp3"] = Encoding.ASCII.GetBytes("MP3-PLACEHOLDER original bytes retained"),
        ["OEBPS/page1.smil"] = Encoding.UTF8.GetBytes(SmilXml())
    };

    public static byte[] BuildZip(IReadOnlyDictionary<string, byte[]> files)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
            foreach (var pair in files)
            {
                var entry = zip.CreateEntry(pair.Key, CompressionLevel.NoCompression);
                using var stream = entry.Open();
                stream.Write(pair.Value);
            }
        return memory.ToArray();
    }

    private static string ContainerXml(bool multiple) => multiple ? """
<?xml version="1.0" encoding="UTF-8"?>
<container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
  <rootfiles>
    <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
    <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
  </rootfiles>
</container>
""" : """
<?xml version="1.0" encoding="UTF-8"?>
<container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
  <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
</container>
""";

    private static string PackageXml(bool fallbackCycle = false, bool pathEscape = false, bool spineDuplicate = false, bool brokenOverlay = false, bool extraUnknownMetadata = false)
    {
        var exoticFallback = fallbackCycle ? "exotic-widget" : "img-cover";
        var coverFallback = fallbackCycle ? "exotic-widget" : null;
        var coverFallbackAttribute = coverFallback is null ? string.Empty : $" fallback=\"{coverFallback}\"";
        var overlay = brokenOverlay ? "missing-overlay" : "overlay-1";
        var extra = extraUnknownMetadata ? "\n    <review:custom-priority>preserve-unknown-roundtrip</review:custom-priority>" : string.Empty;
        var escapeItem = pathEscape ? "\n    <item id=\"escape\" href=\"../../outside-container.txt\" media-type=\"text/plain\"/>" : string.Empty;
        var duplicate = spineDuplicate ? "\n    <itemref idref=\"ch1\" linear=\"yes\"/>" : string.Empty;
        return $"""
<?xml version="1.0" encoding="UTF-8"?>
<package xmlns="http://www.idpf.org/2007/opf" unique-identifier="bookid" version="3.0">
  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:review="https://offline.local/review">
    <dc:identifier id="bookid">urn:uuid:offline-reading-spectrum</dc:identifier>
    <dc:title>EPUB 阅读序谱离线案例</dc:title>
    <review:unrecognized-team-note>保留命名空间、原文和位置，不由派生层解释</review:unrecognized-team-note>{extra}
  </metadata>
  <manifest>
    <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
    <item id="ch1" href="ch1.xhtml" media-type="application/xhtml+xml" media-overlay="{overlay}"/>
    <item id="ch2" href="ch2.xhtml" media-type="application/xhtml+xml"/>
    <item id="css" href="stylesheet.css" media-type="text/css"/>
    <item id="img-cover" href="cover.png" media-type="image/png"{coverFallbackAttribute}/>
    <item id="audio-1" href="narration.mp3" media-type="audio/mpeg"/>
    <item id="overlay-1" href="page1.smil" media-type="application/smil+xml"/>
    <item id="exotic-widget" href="cover.png" media-type="application/x-team-widget" fallback="{exoticFallback}"/>{escapeItem}
  </manifest>
  <spine>
    <itemref idref="ch1" linear="yes"/>
    <itemref idref="ch2" linear="yes"/>{duplicate}
  </spine>
</package>
""";
    }

    private static string NavXml(bool missingFragment = false)
    {
        var second = missingFragment ? "ch2.xhtml#not-in-document" : "ch2.xhtml#start";
        return $"""
<?xml version="1.0" encoding="UTF-8"?>
<html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
<head><title>Navigation</title></head>
<body><nav epub:type="toc"><ol>
  <li><a href="ch1.xhtml#start">第一章：开始</a><ol><li><a href="ch1.xhtml#mid">第一章：中段</a></li></ol></li>
  <li><a href="{second}">第二章：开始</a></li>
</ol></nav></body></html>
""";
    }

    private static string Chapter1Xml(bool revised)
    {
        var added = revised ? "\n<p id=\"local-revision\">局部依赖源修订：只替换本章 ZIP 条目字节。</p>" : string.Empty;
        return $"""
<?xml version="1.0" encoding="UTF-8"?>
<html xmlns="http://www.w3.org/1999/xhtml">
<head><title>Chapter 1</title><link rel="stylesheet" href="stylesheet.css"/></head>
<body>
<h1 id="start">第一章</h1>
<p>基线资源引用：<a href="ch2.xhtml#start">下一章</a></p>
<p id="mid">中段锚点。</p>
<img src="cover.png" alt="cover"/>{added}
</body></html>
""";
    }

    private static string Chapter2Xml() => """
<?xml version="1.0" encoding="UTF-8"?>
<html xmlns="http://www.w3.org/1999/xhtml">
<head><title>Chapter 2</title></head>
<body><h1 id="start">第二章</h1><p id="end">结束锚点。</p></body></html>
""";

    private static string SmilXml() => """
<?xml version="1.0" encoding="UTF-8"?>
<smil xmlns="http://www.w3.org/ns/SMIL" version="3.0">
  <body><seq id="seq1"><par id="par1">
    <text src="ch1.xhtml#start"/>
    <audio src="narration.mp3" clipBegin="0s" clipEnd="2.5s"/>
  </par></seq></body>
</smil>
""";
    }
}
