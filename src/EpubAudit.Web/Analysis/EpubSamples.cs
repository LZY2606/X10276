using System.IO.Compression;
using System.Text;
using EpubAudit.Domain;

namespace EpubAudit.Analysis;

public static class EpubSamples
{
    public static byte[] CleanContainer() => BuildZip(CleanEntries);
    public static byte[] EdgeContainer() => BuildZip(EdgeEntries);

    private static byte[] BuildZip(Func<Dictionary<string, byte[]>> entriesFactory)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            foreach (var entry in entriesFactory().OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                var zipEntry = archive.CreateEntry(entry.Key, CompressionLevel.NoCompression);
                using var stream = zipEntry.Open();
                stream.Write(entry.Value);
            }
        }
        return output.ToArray();
    }

    private static Dictionary<string, byte[]> CleanEntries()
    {
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["mimetype"] = Encoding.UTF8.GetBytes("application/epub+zip"),
            ["META-INF/container.xml"] = Encoding.UTF8.GetBytes("""
<?xml version="1.0" encoding="UTF-8"?>
<container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
  <rootfiles>
    <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
    <rootfile full-path="ALT/guide.opf" media-type="application/oebps-package+xml"/>
  </rootfiles>
</container>
"""),
            ["OEBPS/content.opf"] = Encoding.UTF8.GetBytes(CleanContentOpf()),
            ["OEBPS/chapter1.xhtml"] = Encoding.UTF8.GetBytes("""
<?xml version="1.0" encoding="UTF-8"?>
<html xmlns="http://www.w3.org/1999/xhtml" xml:lang="en">
<head><title>First Reading Unit</title><link rel="stylesheet" href="styles.css"/></head>
<body>
  <h1 id="top">First Reading Unit</h1>
  <p id="p1">Coordinates in links remain relative to their package.</p>
  <p><a href="images\cover.svg">Ambiguous separator candidate</a></p>
  <img src="images/cover.svg" alt="cover"/>
</body>
</html>
"""),
            ["OEBPS/chapter2.xhtml"] = Encoding.UTF8.GetBytes("""
<?xml version="1.0" encoding="UTF-8"?>
<html xmlns="http://www.w3.org/1999/xhtml">
<head><title>Second Reading Unit</title><link href="styles.css" rel="stylesheet"/></head>
<body><h1 id="start">Second Reading Unit</h1><p>Reference graph includes non-spine dependencies.</p></body>
</html>
"""),
            ["OEBPS/styles.css"] = Encoding.UTF8.GetBytes("body { font-size: 12pt; }\n"),
            ["OEBPS/images/cover.svg"] = Encoding.UTF8.GetBytes("""
<svg xmlns="http://www.w3.org/2000/svg" width="320px" height="180px" viewBox="0 0 320 180"><rect x="0" y="0" width="320" height="180" fill="#e8f1ff"/><text x="160" y="90" text-anchor="middle">cover</text></svg>
"""),
            ["OEBPS/images\\cover.svg"] = Encoding.UTF8.GetBytes("""
<svg xmlns="http://www.w3.org/2000/svg" width="320px" height="180px" viewBox="0 0 320 180"><rect width="320" height="180" fill="#fff1e8"/><text x="160" y="90" text-anchor="middle">literal-backslash</text></svg>
"""),
            ["OEBPS/images/cover.png"] = Encoding.UTF8.GetBytes([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
            ["OEBPS/audio/narration.mp3"] = Encoding.UTF8.GetBytes("ID3offline-audio-byte-placeholder"),
            ["OEBPS/overlay.smil"] = Encoding.UTF8.GetBytes("""
<?xml version="1.0" encoding="UTF-8"?>
<smil xmlns="http://www.w3.org/ns/SMIL" xmlns:epub="http://www.idpf.org/2007/ops" version="3.0">
  <body>
    <seq id="seq1" epub:textref="chapter1.xhtml#top">
      <par id="par1">
        <text src="chapter1.xhtml#p1"/>
        <audio src="audio/narration.mp3" clipBegin="00:00:01.500" clipEnd="00:00:04.000"/>
      </par>
      <par id="par2">
        <text src="chapter1.xhtml#top"/>
        <audio src="audio/narration.mp3" clipBegin="4s" clipEnd="00:00:07.250"/>
      </par>
    </seq>
  </body>
</smil>
"""),
            ["ALT/guide.opf"] = Encoding.UTF8.GetBytes("""
<?xml version="1.0" encoding="UTF-8"?>
<package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="bookid" xml:lang="en">
  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
    <dc:identifier id="bookid">urn:local:alt-guide</dc:identifier>
    <dc:title>Alternate Guide Package</dc:title>
  </metadata>
  <manifest>
    <item id="guidenav" href="guide.xhtml" media-type="application/xhtml+xml" properties="nav"/>
  </manifest>
  <spine><itemref idref="guidenav"/></spine>
</package>
"""),
            ["ALT/guide.xhtml"] = Encoding.UTF8.GetBytes("""
<?xml version="1.0" encoding="UTF-8"?>
<html xmlns="http://www.w3.org/1999/xhtml"><head><title>Guide</title></head><body><nav epub:type="toc" xmlns:epub="http://www.idpf.org/2007/ops"><ol><li><a href="guide.xhtml#top">Guide top</a></li></ol></nav><h1 id="top">Guide</h1></body></html>
""")
        };
        return entries;
    }

    private static string CleanContentOpf() => """
<?xml version="1.0" encoding="UTF-8"?>
<package xmlns="http://www.idpf.org/2007/opf" xmlns:epub="http://www.idpf.org/2007/ops" xmlns:demo="https://local.example/epub-audit" version="3.0" unique-identifier="bookid" xml:lang="en">
  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
    <dc:identifier id="bookid">urn:local:epub-spectrum</dc:identifier>
    <dc:title>EPUB Reading-order Spectrum</dc:title>
    <dc:language>en</dc:language>
    <meta property="dcterms:modified">2026-09-24T09:00:00Z</meta>
    <demo:review-note demo:evidence="baseline">Preserve unknown metadata exactly</demo:review-note>
  </metadata>
  <manifest>
    <item id="nav" href="chapter1.xhtml" media-type="application/xhtml+xml" properties="nav"/>
    <item id="chapter2" href="chapter2.xhtml" media-type="application/xhtml+xml"/>
    <item id="coversvg" href="images/cover.svg" media-type="image/svg+xml" fallback="coverpng"/>
    <item id="coverpng" href="images/cover.png" media-type="image/png"/>
    <item id="styles" href="styles.css" media-type="text/css"/>
    <item id="narration" href="audio/narration.mp3" media-type="audio/mpeg"/>
    <item id="overlay" href="overlay.smil" media-type="application/smil+xml"/>
    <item id="chapter1-overlay" href="chapter1.xhtml" media-type="application/xhtml+xml" media-overlay="overlay"/>
  </manifest>
  <spine>
    <itemref idref="nav" linear="yes"/>
    <itemref idref="chapter2" linear="yes"/>
  </spine>
</package>
""";

    private static Dictionary<string, byte[]> EdgeEntries()
    {
        var entries = CleanEntries();
        entries["OEBPS/content.opf"] = Encoding.UTF8.GetBytes(EdgeContentOpf());
        return entries;
    }

    private static string EdgeContentOpf() => """
<?xml version="1.0" encoding="UTF-8"?>
<package xmlns="http://www.idpf.org/2007/opf" xmlns:epub="http://www.idpf.org/2007/ops" xmlns:demo="https://local.example/epub-audit" version="3.0" unique-identifier="bookid" xml:lang="en">
  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
    <dc:identifier id="bookid">urn:local:epub-spectrum</dc:identifier>
    <dc:title>EPUB Reading-order Spectrum Counterexample</dc:title>
    <dc:language>en</dc:language>
    <meta property="dcterms:modified">2026-09-24T10:30:00Z</meta>
    <demo:review-note demo:evidence="counterexample-roundtrip">Unknown metadata survives a local OPF revision</demo:review-note>
  </metadata>
  <manifest>
    <item id="nav" href="chapter1.xhtml" media-type="application/xhtml+xml" properties="nav"/>
    <item id="chapter2" href="chapter2.xhtml" media-type="application/xhtml+xml"/>
    <item id="cycle-a" href="images/cover.svg" media-type="image/svg+xml" fallback="cycle-b"/>
    <item id="cycle-b" href="images/cover.png" media-type="image/png" fallback="cycle-a"/>
    <item id="needs-fallback" href="chapter2.xhtml" media-type="application/x-unknown" fallback="missing-fallback"/>
    <item id="escaped" href="../../outside/container-leak.txt" media-type="text/plain"/>
    <item id="styles" href="styles.css" media-type="text/css"/>
    <item id="narration" href="audio/narration.mp3" media-type="audio/mpeg"/>
    <item id="overlay" href="overlay.smil" media-type="application/smil+xml"/>
    <item id="broken-overlay-resource" href="chapter2.xhtml" media-type="application/xhtml+xml" media-overlay="overlay-does-not-exist"/>
    <item id="chapter1-overlay" href="chapter1.xhtml" media-type="application/xhtml+xml" media-overlay="overlay"/>
  </manifest>
  <spine>
    <itemref idref="nav" linear="yes"/>
    <itemref idref="nav" linear="yes"/>
    <itemref idref="chapter2" linear="yes"/>
    <itemref idref="nav#anchor-that-does-not-exist" linear="no"/>
  </spine>
</package>
""";
}
