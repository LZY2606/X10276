using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace EpubReview.Core;

public sealed class ZipEpubReader
{
    public static readonly XNamespace OpfNs = "http://www.idpf.org/2007/opf";
    public static readonly XNamespace DcNs = "http://purl.org/dc/elements/1.1/";
    public static readonly XNamespace XlinkNs = "http://www.w3.org/1999/xlink";

    public EpubMaterials Read(byte[] archiveBytes, ReviewParameters parameters, string materialId, int version, string? parentMaterialVersionId, string directSource)
    {
        var archiveHash = StableIdentity.Sha256(archiveBytes);
        var diagnostics = new List<Diagnostic>();
        var entries = new List<ZipEntryRecord>();

        try
        {
            using var stream = new MemoryStream(archiveBytes, writable: false);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

            if (zip.Entries.Count > parameters.MaxZipEntries)
            {
                diagnostics.Add(DiagnosticFactory.Budget("budget.zip-entry-count", $"ZIP entries {zip.Entries.Count} exceed budget {parameters.MaxZipEntries}.", "ZIP central directory", zip.Entries.Count.ToString()));
            }

            long uncompressedTotal = 0;
            var seenPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in zip.Entries)
            {
                uncompressedTotal += entry.Length;
                if (!entry.FullName.EndsWith('/') && !seenPaths.Add(entry.FullName))
                {
                    diagnostics.Add(DiagnosticFactory.Format("zip.duplicate-entry", "ZIP contains duplicate entry path.", $"zip:///{entry.FullName}", entry.FullName));
                }
            }

            if (uncompressedTotal > parameters.MaxTotalBytes)
            {
                diagnostics.Add(DiagnosticFactory.Budget("budget.uncompressed-bytes", $"Uncompressed resource budget {uncompressedTotal} byte exceeds {parameters.MaxTotalBytes} byte.", "ZIP central directory", uncompressedTotal.ToString()));
            }

            foreach (var entry in zip.Entries.Take(parameters.MaxZipEntries))
            {
                using var entryStream = entry.Open();
                using var memory = new MemoryStream();
                entryStream.CopyTo(memory);
                var bytes = memory.ToArray();
                var mediaHint = GuessMediaType(entry.FullName);
                entries.Add(new ZipEntryRecord(entry.FullName, entry.Length, entry.CompressedLength, StableIdentity.Sha256(bytes), mediaHint, bytes));
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException)
        {
            diagnostics.Add(DiagnosticFactory.Format("zip.format-corrupt", $"ZIP container cannot be read: {ex.Message}", "archive:0", "<invalid-zip>"));
            return new EpubMaterials(materialId, version, parentMaterialVersionId, directSource, archiveBytes, archiveHash, entries, EmptyContainer(archiveHash), Array.Empty<PackageDocument>(), diagnostics);
        }

        if (diagnostics.Any(d => d.RuleId.StartsWith("budget.", StringComparison.Ordinal)))
        {
            return new EpubMaterials(materialId, version, parentMaterialVersionId, directSource, archiveBytes, archiveHash, entries, EmptyContainer(archiveHash), Array.Empty<PackageDocument>(), diagnostics);
        }

        var container = ParseContainer(entries, diagnostics);
        var packages = container.RootPackages
            .Select(reference => ParsePackage(reference, entries, diagnostics))
            .ToArray();

        return new EpubMaterials(materialId, version, parentMaterialVersionId, directSource, archiveBytes, archiveHash, entries, container, packages, diagnostics);
    }

    private static ContainerDocument EmptyContainer(string hash) =>
        new(Array.Empty<byte>(), hash, Array.Empty<RootPackageReference>(), Array.Empty<Diagnostic>());

    private static ContainerDocument ParseContainer(IReadOnlyList<ZipEntryRecord> entries, List<Diagnostic> diagnostics)
    {
        var entry = entries.FirstOrDefault(e => e.Path == "META-INF/container.xml");
        if (entry is null)
        {
            diagnostics.Add(DiagnosticFactory.Format("container.missing", "META-INF/container.xml is missing.", "META-INF/container.xml", "META-INF/container.xml"));
            return new ContainerDocument(Array.Empty<byte>(), "missing", Array.Empty<RootPackageReference>(), diagnostics.ToArray());
        }

        try
        {
            var doc = LoadXml(entry.Bytes);
            var roots = doc.Descendants(XNamespace.Get("urn:oasis:names:tc:opendocument:xmlns:container") + "rootfile")
                .Select((element, index) => new RootPackageReference(
                    element.Attribute("full-path")?.Value ?? string.Empty,
                    element.Attribute("media-type")?.Value ?? string.Empty,
                    index))
                .ToArray();
            if (roots.Length == 0)
            {
                diagnostics.Add(DiagnosticFactory.Format("container.no-rootfile", "container.xml defines no rootfile.", "container.xml:rootfiles", string.Empty));
            }
            foreach (var root in roots.Where(r => string.IsNullOrWhiteSpace(r.EntryPath)))
            {
                diagnostics.Add(DiagnosticFactory.Format("container.rootfile-empty-path", "rootfile full-path is empty.", $"container.xml:rootfile[{root.Position}]", root.EntryPath));
            }
            return new ContainerDocument(entry.Bytes, StableIdentity.Sha256(entry.Bytes), roots, Array.Empty<Diagnostic>());
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException)
        {
            diagnostics.Add(DiagnosticFactory.Format("container.xml-corrupt", $"container.xml is not well-formed: {ex.Message}", $"container.xml:line{((System.Xml.XmlException?)ex as System.Xml.XmlException)?.LineNumber ?? 0}", entry.Path));
            return new ContainerDocument(entry.Bytes, entry.Sha256, Array.Empty<RootPackageReference>(), Array.Empty<Diagnostic>());
        }
    }

    private static PackageDocument ParsePackage(RootPackageReference reference, IReadOnlyList<ZipEntryRecord> entries, List<Diagnostic> diagnostics)
    {
        var entry = entries.FirstOrDefault(e => e.Path == reference.EntryPath);
        var packageId = StableIdentity.Short("pkg", reference.EntryPath);
        if (entry is null)
        {
            diagnostics.Add(DiagnosticFactory.Semantic("package.missing-entry", "container.xml points to a missing package document.", $"container.xml:rootfile[{reference.Position}]", packageId, reference.EntryPath));
            return new PackageDocument(packageId, reference.EntryPath, reference.MediaType, Array.Empty<byte>(), "missing", Array.Empty<ManifestItem>(), Array.Empty<SpineItem>(), Array.Empty<UnknownMetadata>());
        }

        try
        {
            var doc = LoadXml(entry.Bytes);
            var manifest = new List<ManifestItem>();
            var seenManifestIds = new HashSet<string>();
            foreach (var element in doc.Descendants(OpfNs + "item"))
            {
                var id = element.Attribute("id")?.Value ?? string.Empty;
                if (!seenManifestIds.Add(id))
                {
                    diagnostics.Add(DiagnosticFactory.Semantic("manifest.duplicate-id", "manifest item id is duplicated.", XmlLocation(element), StableIdentity.Short("item", $"{packageId}:{id}"), id));
                }
                manifest.Add(new ManifestItem(
                    id,
                    element.Attribute("href")?.Value ?? string.Empty,
                    element.Attribute("media-type")?.Value ?? string.Empty,
                    element.Attribute("fallback")?.Value,
                    element.Attribute("media-overlay")?.Value,
                    element.Attribute("properties")?.Value ?? string.Empty,
                    RawAttributes(element),
                    XmlLocation(element)));
            }

            var spine = new List<SpineItem>();
            var idRefCounts = new Dictionary<string, int>();
            foreach (var (element, index) in doc.Descendants(OpfNs + "itemref").Select((x, i) => (x, i)))
            {
                var idRef = element.Attribute("idref")?.Value ?? string.Empty;
                idRefCounts[idRef] = idRefCounts.GetValueOrDefault(idRef) + 1;
                spine.Add(new SpineItem(index, idRef, element.Attribute("linear")?.Value != "no", element.Attribute("properties")?.Value ?? string.Empty, XmlLocation(element)));
            }

            foreach (var (idRef, count) in idRefCounts.Where(x => x.Value > 1))
            {
                diagnostics.Add(DiagnosticFactory.Semantic("spine.duplicate-idref", "spine idref appears more than once.", "opf:spine", StableIdentity.Short("spine-item", $"{packageId}:{idRef}"), idRef, new Dictionary<string, string> { ["occurrences"] = count.ToString(), ["unit"] = "count" }));
            }

            var unknown = ParseUnknownMetadata(packageId, doc);
            return new PackageDocument(packageId, entry.Path, GuessMediaType(entry.Path), entry.Bytes, entry.Sha256, manifest, spine, unknown);
        }
        catch (System.Xml.XmlException ex)
        {
            diagnostics.Add(DiagnosticFactory.Format("package.xml-corrupt", $"Package document is not well-formed: {ex.Message}", $"{reference.EntryPath}:line{ex.LineNumber}:col{ex.LinePosition}", packageId, reference.EntryPath));
            return new PackageDocument(packageId, entry.Path, GuessMediaType(entry.Path), entry.Bytes, entry.Sha256, Array.Empty<ManifestItem>(), Array.Empty<SpineItem>(), Array.Empty<UnknownMetadata>());
        }
    }

    private static IReadOnlyList<UnknownMetadata> ParseUnknownMetadata(string packageId, XDocument doc)
    {
        var result = new List<UnknownMetadata>();
        var metadata = doc.Descendants(OpfNs + "metadata").FirstOrDefault();
        if (metadata is null) return result;
        foreach (var element in metadata.Elements())
        {
            var recognized = element.Name.Namespace == DcNs || (element.Name.Namespace == OpfNs && element.Name.LocalName == "meta");
            if (recognized) continue;
            result.Add(new UnknownMetadata(
                packageId,
                element.GetPrefixOfNamespace(element.Name.Namespace) ?? string.Empty,
                element.Name.LocalName,
                element.Name.NamespaceName,
                element.Value,
                element.Attributes().ToDictionary(a => a.Name.ToString(), a => a.Value),
                element.ToString(),
                XmlLocation(element)));
        }
        return result;
    }

    public static XDocument LoadXml(byte[] bytes)
    {
        var settings = new System.Xml.XmlReaderSettings
        {
            DtdProcessing = System.Xml.DtdProcessing.Prohibit,
            XmlResolver = null
        };
        using var reader = System.Xml.XmlReader.Create(new MemoryStream(bytes), settings);
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    private static IReadOnlyDictionary<string, string> RawAttributes(XElement element) =>
        element.Attributes().ToDictionary(a => a.Name.ToString(), a => a.Value);

    private static string XmlLocation(XObject element)
    {
        var info = element as System.Xml.IXmlLineInfo;
        return info?.HasLineInfo() == true ? $"line:{info.LineNumber},column:{info.LinePosition}" : "xml";
    }

    public static string GuessMediaType(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".xhtml" or ".html" or ".htm" => "application/xhtml+xml",
            ".xml" or ".opf" => "application/oebps-package+xml",
            ".smil" => "application/smil+xml",
            ".css" => "text/css",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".svg" => "image/svg+xml",
            ".mp3" => "audio/mpeg",
            ".mp4" => "video/mp4",
            _ => "application/octet-stream"
        };
    }
}
