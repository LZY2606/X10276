using System.IO.Compression;
using System.Text;

namespace EpubReview.Domain;

public static class SampleCase
{
    public const string CaseId = "offline-epub-reading-spectrum";
    public const string Title = "EPUB 阅读序谱：package spine、fallback 链与锚点完整性审阅";

    public static byte[] BuildArchive(SampleRevision revision = SampleRevision.Initial)
    {
        var chapter1 = revision == SampleRevision.LocalSourceChange ? Chapter1Revised : Chapter1Initial;
        var files = new (string Path, string Content)[]
        {
            ("mimetype", "application/epub+zip"),
            ("META-INF/container.xml", ContainerXml),
            ("OEBPS/content.opf", ContentOpf),
            ("OEBPS/alt.opf", AltOpf),
            ("OEBPS/nav.xhtml", NavXhtml),
            ("OEBPS/chapter1.xhtml", chapter1),
            ("OEBPS/chapter2.xhtml", Chapter2),
            ("OEBPS/alt.xhtml", AltXhtml),
            ("OEBPS/overlay.smil", OverlaySmil),
            ("OEBPS/data/note.txt", "EPUB candidate A\n"),
            ("OEBPS/other/../data/note.txt", "EPUB candidate B\n")
        };
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files.OrderBy(f => f.Path, StringComparer.Ordinal))
            {
                var entry = zip.CreateEntry(file.Path, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(file.Content);
            }
        }
        return output.ToArray();
    }

    public static byte[] BuildCorruptArchive() => Encoding.UTF8.GetBytes("not a zip archive but definitely not EPUB");

    private const string ContainerXml = """
<?xml version="1.0" encoding="UTF-8"?>
<container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
  <rootfiles>
    <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
    <rootfile full-path="OEBPS/alt.opf" media-type="application/oebps-package+xml"/>
  </rootfiles>
</container>
""";

    private const string ContentOpf = """
<?xml version="1.0" encoding="UTF-8"?>
<package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="bookid" xmlns:review="https://example.invalid/offline-review">
  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
    <dc:identifier id="bookid">urn:uuid:offline-reading-spectrum</dc:identifier>
    <dc:title>EPUB 阅读序谱离线案例</dc:title>
  </metadata>
  <review:evidence id="source:preserved" confidence="unit:byte+line">未识别命名空间证据必须原样往返</review:evidence>
  <manifest>
    <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
    <item id="chapter1" href="chapter1.xhtml" media-type="application/xhtml+xml" fallback="cycle-a"/>
    <item id="chapter2" href="chapter2.xhtml" media-type="application/xhtml+xml" media-overlay="overlay-missing"/>
    <item id="cycle-a" href="data/note.txt" media-type="text/plain" fallback="cycle-b"/>
    <item id="cycle-b" href="data/note-duplicate.txt" media-type="text/plain" fallback="cycle-a"/>
    <item id="missing-fallback-source" href="data/missing-fallback-source.bin" media-type="application/x-offline" fallback="fallback-not-present"/>    <item id="ambiguous-note" href="data/note.txt" media-type="text/plain"/>
    <item id="ambiguous-other" href="other/../data/note.txt" media-type="text/plain"/>
  </manifest>
  <spine>
    <itemref idref="chapter1"/>
    <itemref idref="chapter2"/>
    <itemref idref="chapter1"/>
  </spine>
</package>
""";

    private const string AltOpf = """
<?xml version="1.0" encoding="UTF-8"?>
<package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="altid" xmlns:lab="https://example.invalid/offline-lab">
  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
    <dc:identifier id="altid">urn:uuid:offline-alt-package</dc:identifier>
  </metadata>
  <lab:retention round-trip="raw">多 package 的未识别元数据</lab:retention>
  <manifest><item id="alt-nav" href="alt.xhtml" media-type="application/xhtml+xml" properties="nav"/></manifest>
  <spine><itemref idref="alt-nav"/></spine>
</package>
""";

    private const string NavXhtml = """
<?xml version="1.0" encoding="UTF-8"?>
<html xmlns="http://www.w3.org/1999/xhtml">
<head><title>Navigation</title></head>
<body>
<nav epub:type="toc" xmlns:epub="http://www.idpf.org/2007/ops">
  <ol>
    <li><a href="chapter1.xhtml#start">第一章：有效锚点</a>
      <ol><li><a href="chapter2.xhtml#missing-anchor">第二章：不存在锚点</a></li></ol>
    </li>
    <li><a href="../../outside-container.xhtml#forbidden">越界引用</a></li>
  </ol>
</nav>
</body></html>
""";

    private const string Chapter1Initial = """
<?xml version="1.0" encoding="UTF-8"?>
<html xmlns="http://www.w3.org/1999/xhtml">
<head><title>Chapter 1</title></head>
<body><section id="start"><h1>第一章</h1><p>初始基线引用一个缺失资源。</p><img src="missing-image.png" alt="missing"/></section></body>
</html>
""";

    private const string Chapter1Revised = """
<?xml version="1.0" encoding="UTF-8"?>
<html xmlns="http://www.w3.org/1999/xhtml">
<head><title>Chapter 1</title></head>
<body><section id="start"><h1>第一章（局部源数据修订）</h1><p>缺失图片仍保留，但新增一个只影响本章的合法脚注资源候选。</p><a href="data/note.txt#unit:byte">脚注候选</a><img src="missing-image.png" alt="missing"/></section></body>
</html>
""";

    private const string Chapter2 = """
<?xml version="1.0" encoding="UTF-8"?>
<html xmlns="http://www.w3.org/1999/xhtml">
<head><title>Chapter 2</title></head>
<body><section id="present"><h1>第二章</h1><a href="#present">本页锚点存在</a></body></html>
""";

    private const string AltXhtml = """
<?xml version="1.0" encoding="UTF-8"?>
<html xmlns="http://www.w3.org/1999/xhtml"><head><title>Alt</title></head><body><nav xmlns:epub="http://www.idpf.org/2007/ops"><ol><li><a href="chapter2.xhtml#present">跨 package 合法锚点</a></li></ol></nav></body></html>
""";

    private const string OverlaySmil = """
<?xml version="1.0" encoding="UTF-8"?>
<smil xmlns="http://www.w3.org/ns/SMIL" xmlns:epub="http://www.idpf.org/2007/ops">
  <body><seq id="seq1"><par id="par1"><text src="chapter2.xhtml#present"/><audio src="missing-audio.mp3" clipBegin="0s" clipEnd="2.5s"/></par></seq></body>
</smil>
""";
}

public enum SampleRevision { Initial, LocalSourceChange }
