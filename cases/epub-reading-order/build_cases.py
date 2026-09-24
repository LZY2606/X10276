#!/usr/bin/env python3
"""Deterministically builds the offline EPUB case package for
'EPUB reading-order spectrum: package spine, fallback chains and anchor integrity review'.
Fixed timestamps + fixed entry order => stable bytes => stable material identities."""
import io, os, zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
FIXED_DATE = (2026, 9, 19, 0, 0, 0)

CONTAINER = """<?xml version="1.0" encoding="UTF-8"?>
<container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
  <rootfiles>
    <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
  </rootfiles>
</container>
"""

CONTAINER_MULTI = """<?xml version="1.0" encoding="UTF-8"?>
<container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
  <rootfiles>
    <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
    <rootfile full-path="OEBPS/alt.opf" media-type="application/oebps-package+xml"/>
  </rootfiles>
</container>
"""

OPF = """<?xml version="1.0" encoding="UTF-8"?>
<package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="uid">
  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
    <dc:identifier id="uid">urn:uuid:9f2c1a70-0000-4a00-b000-000000000001</dc:identifier>
    <dc:title>Reading Order Spectrum Sample</dc:title>
    <dc:language>zh-CN</dc:language>
    <meta property="dcterms:modified">2026-09-19T00:00:00Z</meta>
    <foo:rating xmlns:foo="http://example.com/ns/foo" foo:scale="stars">4.5</foo:rating>
  </metadata>
  <manifest>
    <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
    <item id="ch1" href="chapter1.xhtml" media-type="application/xhtml+xml" media-overlay="ov1"/>
    <item id="ch2" href="chapter2.xhtml" media-type="application/xhtml+xml"/>
    <item id="css" href="style.css" media-type="text/css"/>
    <item id="ov1" href="overlay1.smil" media-type="application/smil+xml"/>
    <item id="au1" href="audio/c1.mp3" media-type="audio/mpeg"/>
    <item id="cover" href="cover.png" media-type="image/png"/>
  </manifest>
  <spine>
    <itemref idref="ch1"/>
    <itemref idref="ch2"/>
  </spine>
</package>
"""

ALT_OPF = """<?xml version="1.0" encoding="UTF-8"?>
<package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="uid">
  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
    <dc:identifier id="uid">urn:uuid:9f2c1a70-0000-4a00-b000-000000000002</dc:identifier>
    <dc:title>Alternate Rendition</dc:title>
    <dc:language>zh-CN</dc:language>
  </metadata>
  <manifest>
    <item id="ch1" href="chapter1.xhtml" media-type="application/xhtml+xml"/>
  </manifest>
  <spine>
    <itemref idref="ch1"/>
  </spine>
</package>
"""

NAV = """<?xml version="1.0" encoding="UTF-8"?>
<html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
<head><title>TOC</title></head>
<body>
<nav epub:type="toc">
  <ol>
    <li><a href="chapter1.xhtml#p1">Chapter 1</a></li>
    <li><a href="chapter2.xhtml#s1">Chapter 2</a></li>
    <li><a href="notes.xhtml#n1">Notes (outside spine)</a></li>
  </ol>
</nav>
</body>
</html>
"""

NAV_BAD_FRAGMENT = NAV.replace('chapter2.xhtml#s1', 'chapter2.xhtml#no-such-anchor')

CH1 = """<?xml version="1.0" encoding="UTF-8"?>
<html xmlns="http://www.w3.org/1999/xhtml">
<head><title>Chapter 1</title><link rel="stylesheet" href="style.css"/></head>
<body>
<p id="p1">First paragraph.</p>
<p id="p2">Second paragraph with <img src="cover.png" alt="cover"/>.</p>
</body>
</html>
"""

CH2 = """<?xml version="1.0" encoding="UTF-8"?>
<html xmlns="http://www.w3.org/1999/xhtml">
<head><title>Chapter 2</title></head>
<body>
<p id="s1">Second chapter, first paragraph.</p>
<p id="s2">Second chapter, second paragraph.</p>
</body>
</html>
"""

CH2_V2 = CH2.replace('second paragraph.', 'second paragraph, locally revised.')

CSS = "body { font-family: serif; }\n"

SMIL = """<?xml version="1.0" encoding="UTF-8"?>
<smil xmlns="http://www.w3.org/ns/SMIL" version="3.0">
  <body>
    <par id="par1">
      <text src="chapter1.xhtml#p1"/>
      <audio src="audio/c1.mp3" clipBegin="0:00:00.000" clipEnd="0:00:12.500"/>
    </par>
    <par id="par2">
      <text src="chapter1.xhtml#p2"/>
      <audio src="audio/c1.mp3" clipBegin="0:00:12.500" clipEnd="0:00:30.250"/>
    </par>
  </body>
</smil>
"""

SMIL_BROKEN = SMIL.replace('chapter1.xhtml#p2', 'chapter9.xhtml#gone').replace(
    '<audio src="audio/c1.mp3" clipBegin="0:00:12.500"', '<audio src="audio/missing.mp3" clipBegin="0:00:12.500"')

OPF_FALLBACK = OPF.replace(
    '    <item id="cover" href="cover.png" media-type="image/png"/>',
    '    <item id="cover" href="cover.png" media-type="image/png"/>\n'
    '    <item id="fa" href="exotic-a.bin" media-type="application/x-exotic" fallback="fb"/>\n'
    '    <item id="fb" href="exotic-b.bin" media-type="application/x-exotic" fallback="fa"/>\n'
    '    <item id="fc" href="lonely.bin" media-type="application/x-exotic" fallback="no-such-item"/>\n'
    '    <item id="fd" href="exotic-d.bin" media-type="application/x-exotic" fallback="fe"/>\n'
    '    <item id="fe" href="exotic-e.bin" media-type="application/x-exotic" fallback="ff"/>\n'
    '    <item id="ff" href="exotic-f.bin" media-type="application/x-exotic" fallback="fg"/>\n'
    '    <item id="fg" href="style.css" media-type="text/css"/>')

OPF_SPINE_DUP = OPF.replace('    <itemref idref="ch2"/>',
                            '    <itemref idref="ch2"/>\n    <itemref idref="ch1"/>')

OPF_PATH_ESCAPE = OPF.replace(
    '    <item id="css" href="style.css" media-type="text/css"/>',
    '    <item id="css" href="style.css" media-type="text/css"/>\n'
    '    <item id="evil" href="../../../outside.txt" media-type="text/plain"/>')

OPF_UNKNOWN_META = OPF.replace(
    '</metadata>',
    '    <bar:reading xmlns:bar="http://example.com/ns/bar" bar:unit="mm" bar:basis="trim-box-origin">210.0</bar:reading>\n'
    '    <foo:rating xmlns:foo="http://example.com/ns/foo2" foo:scale="mood">unknown-scale</foo:rating>\n'
    '  </metadata>')

ORDER = ["mimetype", "META-INF/container.xml", "OEBPS/content.opf", "OEBPS/alt.opf",
         "OEBPS/nav.xhtml", "OEBPS/chapter1.xhtml", "OEBPS/chapter2.xhtml",
         "OEBPS/style.css", "OEBPS/overlay1.smil", "OEBPS/audio/c1.mp3",
         "OEBPS/cover.png", "OEBPS/exotic-a.bin", "OEBPS/exotic-b.bin", "OEBPS/lonely.bin",
         "OEBPS/exotic-d.bin", "OEBPS/exotic-e.bin", "OEBPS/exotic-f.bin"]

BASE = {
    "mimetype": b"application/epub+zip",
    "META-INF/container.xml": CONTAINER.encode(),
    "OEBPS/content.opf": OPF.encode(),
    "OEBPS/nav.xhtml": NAV.encode(),
    "OEBPS/chapter1.xhtml": CH1.encode(),
    "OEBPS/chapter2.xhtml": CH2.encode(),
    "OEBPS/style.css": CSS.encode(),
    "OEBPS/overlay1.smil": SMIL.encode(),
    "OEBPS/audio/c1.mp3": bytes.fromhex("4944330400000000000f") + b"\xff\xfb" * 32,
    "OEBPS/cover.png": bytes.fromhex("89504e470d0a1a0a") + b"\x00" * 24,
}

def build(entries):
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as z:
        for name in ORDER:
            if name not in entries:
                continue
            info = zipfile.ZipInfo(name, date_time=FIXED_DATE)
            info.compress_type = zipfile.ZIP_STORED if name == "mimetype" else zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16
            z.writestr(info, entries[name])
    return buf.getvalue()

def variant(**overrides):
    e = dict(BASE)
    for k, v in overrides.items():
        if v is None:
            e.pop(k, None)
        else:
            e[k] = v.encode() if isinstance(v, str) else v
    return e

CASES = {
    "baseline.epub": variant(),
    "baseline-v2.epub": variant(**{"OEBPS/chapter2.xhtml": CH2_V2}),
    "multi-package.epub": variant(**{"META-INF/container.xml": CONTAINER_MULTI, "OEBPS/alt.opf": ALT_OPF}),
    "fallback-cycle.epub": variant(**{
        "OEBPS/content.opf": OPF_FALLBACK,
        "OEBPS/exotic-a.bin": b"EXOTIC-A",
        "OEBPS/exotic-b.bin": b"EXOTIC-B",
        "OEBPS/lonely.bin": b"LONELY",
        "OEBPS/exotic-d.bin": b"EXOTIC-D",
        "OEBPS/exotic-e.bin": b"EXOTIC-E",
        "OEBPS/exotic-f.bin": b"EXOTIC-F"}),
    "fragment-missing.epub": variant(**{"OEBPS/nav.xhtml": NAV_BAD_FRAGMENT}),
    "path-escape.epub": variant(**{"OEBPS/content.opf": OPF_PATH_ESCAPE}),
    "spine-duplicate.epub": variant(**{"OEBPS/content.opf": OPF_SPINE_DUP}),
    "overlay-broken.epub": variant(**{"OEBPS/overlay1.smil": SMIL_BROKEN}),
    "unknown-metadata.epub": variant(**{"OEBPS/content.opf": OPF_UNKNOWN_META}),
}

if __name__ == "__main__":
    for name, entries in CASES.items():
        data = build(entries)
        with open(os.path.join(HERE, name), "wb") as f:
            f.write(data)
        print(f"{name}: {len(data)} bytes")
