using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace EpubReview.Core;

public sealed class EpubImporter
{
    static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";

    public ImportedEpub Import(byte[] zipBytes, ReviewParameters parameters)
    {
        var diagnostics = new List<Diagnostic>();
        if (zipBytes.Length > parameters.MaxContainerBytes)
            throw new ResourceBudgetExhaustedException(new Diagnostic
            {
                Code = "BUDGET-ZIP-BYTES",
                Severity = DiagnosticSeverity.Fatal,
                Category = DiagnosticCategory.Budget,
                Message = $"EPUB container is {zipBytes.Length} byte; limit is {parameters.MaxContainerBytes} byte.",
                Location = new SourceLocation { XPath = "/zip" },
                RawValue = zipBytes.Length.ToString()
            });

        using var input = new MemoryStream(zipBytes, writable: false);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        var entries = new List<ZipEntryMaterial>();
        var entryByNormalized = new Dictionary<string, ZipEntryMaterial>(StringComparer.Ordinal);

        foreach (var (entry, ordinal) in archive.Entries.Select((entry, index) => (entry, index)))
        {
            using var stream = entry.Open();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            var bytes = copy.ToArray();
            var normalized = ContainerPath.NormalizeEntryName(entry.FullName);
            var material = new ZipEntryMaterial
            {
                StableId = StableHash.Of("zip-entry", entry.FullName, StableHash.Sha256(bytes)),
                RawEntryName = entry.FullName,
                NormalizedEntryName = normalized,
                Bytes = bytes,
                Sha256 = StableHash.Sha256(bytes),
                LengthBytes = entry.Length,
                CompressedLengthBytes = entry.CompressedLength,
                Crc32 = entry.Crc32,
                LastWriteTimeRaw = entry.LastWriteTime.ToString("O"),
                Location = new SourceLocation { ZipEntry = entry.FullName, Ordinal = ordinal }
            };
            entries.Add(material);
            if (!string.IsNullOrEmpty(normalized)) entryByNormalized[normalized] = material;
        }

        if (entries.Count > parameters.MaxResourceCount)
            throw new ResourceBudgetExhaustedException(new Diagnostic
            {
                Code = "BUDGET-ENTRY-COUNT",
                Severity = DiagnosticSeverity.Fatal,
                Category = DiagnosticCategory.Budget,
                Message = $"EPUB contains {entries.Count} entries; limit is {parameters.MaxResourceCount}.",
                Location = new SourceLocation { XPath = "/zip" },
                RawValue = entries.Count.ToString()
            });

        var containerEntry = entries.FirstOrDefault(e => e.NormalizedEntryName == "META-INF/container.xml");
        if (containerEntry is null)
            throw new EpubFormatException(new Diagnostic
            {
                Code = "FORMAT-CONTAINER-MISSING",
                Severity = DiagnosticSeverity.Fatal,
                Category = DiagnosticCategory.Format,
                Message = "Required META-INF/container.xml ZIP entry is absent.",
                Location = new SourceLocation { XPath = "/zip/META-INF/container.xml" }
            });

        var containerDoc = LoadXml(containerEntry);
        var rootFileElements = containerDoc.Descendants().Where(e => e.Name.LocalName == "rootfile").ToArray();
        var rootReferences = rootFileElements.Select((element, ordinal) =>
        {
            var raw = element.Attribute("full-path")?.Value ?? "";
            var resolved = ContainerPath.Resolve(null, raw);
            if (resolved.EscapesContainer || !entryByNormalized.ContainsKey(resolved.NormalizedPath ?? ""))
                diagnostics.Add(new Diagnostic
                {
                    Code = "FORMAT-ROOTFILE-MISSING",
                    Severity = DiagnosticSeverity.Error,
                    Category = DiagnosticCategory.Format,
                    Message = "container.xml root-file does not resolve to an entry in this container.",
                    Location = Location(containerEntry.RawEntryName, element, "/container/rootfiles/rootfile", ordinal),
                    AffectedStableId = StableHash.Of("rootfile", raw),
                    RawValue = raw
                });
            return new RootFileReference { RawPath = raw, NormalizedPath = resolved.NormalizedPath, MediaType = element.Attribute("media-type")?.Value, Location = Location(containerEntry.RawEntryName, element, "/container/rootfiles/rootfile", ordinal) };
        }).ToArray();

        var container = new ContainerMaterial
        {
            Bytes = containerEntry.Bytes,
            Sha256 = containerEntry.Sha256,
            RootFiles = rootReferences,
            Location = new SourceLocation { ZipEntry = containerEntry.RawEntryName, XPath = "/META-INF/container.xml", Line = 1 }
        };

        var packages = new List<PackageMaterial>();
        foreach (var reference in rootReferences)
        {
            if (reference.NormalizedPath is null || !entryByNormalized.TryGetValue(reference.NormalizedPath, out var packageEntry)) continue;
            try { packages.Add(ParsePackage(packageEntry, entryByNormalized, diagnostics)); }
            catch (XmlException exception)
            {
                throw new EpubFormatException(new Diagnostic
                {
                    Code = "FORMAT-PACKAGE-XML",
                    Severity = DiagnosticSeverity.Fatal,
                    Category = DiagnosticCategory.Format,
                    Message = exception.Message,
                    Location = new SourceLocation { ZipEntry = packageEntry.RawEntryName, Line = exception.LineNumber, Column = exception.LinePosition },
                    RawValue = reference.RawPath
                });
            }
        }

        var setHash = StableHash.Of("import", string.Join("\n", entries.OrderBy(e => e.RawEntryName, StringComparer.Ordinal).Select(e => $"{e.RawEntryName}:{e.Sha256}")));
        return new ImportedEpub { MaterialSetHash = setHash, ZipEntries = entries, Container = container, Packages = packages, Diagnostics = diagnostics };
    }

    static PackageMaterial ParsePackage(ZipEntryMaterial packageEntry, Dictionary<string, ZipEntryMaterial> entries, List<Diagnostic> diagnostics)
    {
        var doc = LoadXml(packageEntry);
        var root = doc.Root ?? throw new EpubFormatException(new Diagnostic { Code = "FORMAT-PACKAGE-ROOT", Severity = DiagnosticSeverity.Fatal, Category = DiagnosticCategory.Format, Message = "Package document has no root element.", Location = new SourceLocation { ZipEntry = packageEntry.RawEntryName } });
        var packageId = root.Attribute("unique-identifier")?.Value;
        var baseDirectory = ContainerPath.DirectoryOf(packageEntry.NormalizedEntryName);
        var manifestElements = root.Elements(Opf + "manifest").Elements(Opf + "item").ToArray();
        var manifest = new List<ManifestItem>();
        var seenIds = new HashSet<string>();

        foreach (var (element, ordinal) in manifestElements.Select((x, i) => (x, i)))
        {
            var id = element.Attribute("id")?.Value ?? $"__missing-{ordinal}";
            var href = element.Attribute("href")?.Value ?? "";
            var resolved = ContainerPath.Resolve(baseDirectory, href);
            if (resolved.EscapesContainer)
                diagnostics.Add(new Diagnostic { Code = "PATH-ESCAPES-CONTAINER", Severity = DiagnosticSeverity.Error, Category = DiagnosticCategory.Semantics, Message = "Manifest href normalizes outside the container root.", Location = Location(packageEntry.RawEntryName, element, "/package/manifest/item", ordinal), AffectedStableId = StableHash.Of("manifest", packageEntry.Sha256, id), RawValue = href });
            if (!seenIds.Add(id))
                diagnostics.Add(new Diagnostic { Code = "MANIFEST-DUPLICATE-ID", Severity = DiagnosticSeverity.Error, Category = DiagnosticCategory.Format, Message = "Manifest item id is duplicated.", Location = Location(packageEntry.RawEntryName, element, "/package/manifest/item", ordinal), RawValue = id });
            if (string.IsNullOrWhiteSpace(href))
                diagnostics.Add(new Diagnostic { Code = "MANIFEST-MISSING-HREF", Severity = DiagnosticSeverity.Error, Category = DiagnosticCategory.Format, Message = "Manifest item href is empty or absent.", Location = Location(packageEntry.RawEntryName, element, "/package/manifest/item", ordinal), RawValue = id });

            manifest.Add(new ManifestItem
            {
                StableId = StableHash.Of("manifest", packageEntry.Sha256, id),
                PackageStableId = StableHash.Of("package", packageEntry.Sha256),
                ManifestId = id,
                RawHref = href,
                NormalizedPath = resolved.NormalizedPath,
                MediaType = element.Attribute("media-type")?.Value ?? "",
                FallbackManifestId = element.Attribute("fallback")?.Value,
                MediaOverlayManifestId = element.Attribute("media-overlay")?.Value,
                Properties = element.Attribute("properties")?.Value,
                RawAttributes = element.Attributes().ToDictionary(attribute => $"{attribute.Name.NamespaceName}:{attribute.Name.LocalName}", attribute => attribute.Value),
                Location = Location(packageEntry.RawEntryName, element, "/package/manifest/item", ordinal)
            });
        }

        var metadataElement = root.Element(Opf + "metadata");
        var unknown = metadataElement?.Elements().Where(element =>
            element.Name.Namespace != Opf && element.Name.Namespace != Dc &&
            !(element.Name.Namespace == Opf && element.Name.LocalName == "meta")).ToArray() ?? Array.Empty<XElement>();
        var unknownMetadata = unknown.Select((element, ordinal) => new UnknownMetadata
        {
            StableId = StableHash.Of("unknown-metadata", packageEntry.Sha256, element.ToString(SaveOptions.DisableFormatting)),
            PackageStableId = StableHash.Of("package", packageEntry.Sha256),
            ElementName = element.Name.LocalName,
            Namespace = element.Name.NamespaceName,
            RawXml = element.ToString(SaveOptions.DisableFormatting),
            Location = Location(packageEntry.RawEntryName, element, "/package/metadata/*", ordinal)
        }).ToArray();

        return new PackageMaterial
        {
            StableId = StableHash.Of("package", packageEntry.Sha256),
            ContainerRawPath = packageEntry.RawEntryName,
            NormalizedPath = packageEntry.NormalizedEntryName,
            Bytes = packageEntry.Bytes,
            Sha256 = packageEntry.Sha256,
            UniqueIdentifier = packageId,
            Version = root.Attribute("version")?.Value,
            XmlLang = root.Attribute(XNamespace.Xml + "lang")?.Value,
            MediaType = "application/oebps-package+xml",
            ManifestItems = manifest,
            UnknownMetadata = unknownMetadata,
            Location = new SourceLocation { ZipEntry = packageEntry.RawEntryName, XPath = "/package" }
        };
    }

    static XDocument LoadXml(ZipEntryMaterial entry)
    {
        try { return XDocument.Load(new MemoryStream(entry.Bytes), LoadOptions.SetLineInfo); }
        catch (XmlException exception)
        {
            throw new EpubFormatException(new Diagnostic { Code = "FORMAT-XML", Severity = DiagnosticSeverity.Fatal, Category = DiagnosticCategory.Format, Message = exception.Message, Location = new SourceLocation { ZipEntry = entry.RawEntryName, Line = exception.LineNumber, Column = exception.LinePosition } });
        }
    }

    internal static SourceLocation Location(string zipEntry, XObject? obj, string xpath, int ordinal)
    {
        var info = obj as IXmlLineInfo;
        return new SourceLocation { ZipEntry = zipEntry, Line = info?.HasLineInfo() == true ? info.LineNumber : null, Column = info?.HasLineInfo() == true ? info.LinePosition : null, XPath = xpath, Ordinal = ordinal };
    }
}

public sealed class ResourceBudgetExhaustedException : Exception
{
    public required Diagnostic Diagnostic { get; init; }
    public ResourceBudgetExhaustedException(Diagnostic diagnostic) : base(diagnostic.Message) => Diagnostic = diagnostic;
}
