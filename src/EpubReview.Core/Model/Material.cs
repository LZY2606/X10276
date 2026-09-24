namespace EpubReview.Core.Model;

public sealed record ZipEntryRecord(
    int Ordinal,
    string Name,
    string NameRaw,
    ushort CompressionMethod,
    ulong CompressedSize,
    ulong UncompressedSize,
    uint Crc32,
    long DataOffset,
    bool CrcVerified,
    byte[] Bytes,
    string Sha256)
{
    public bool IsDirectory => Name.EndsWith('/');
    public string Id => Hashing.StableId("zip-entry", Ordinal, Name);
}

public sealed record RootFileRef(
    string FullPath,
    string? MediaType,
    SourceLocation Location);

public sealed record ContainerDocument(
    string EntryName,
    byte[] RawBytes,
    string RawText,
    string Sha256,
    IReadOnlyList<RootFileRef> RootFiles,
    string? Version);

public sealed record MetadataItem(
    string Key,
    string Value,
    bool Recognized,
    string OuterXml,
    SourceLocation Location);

public sealed record ManifestItem(
    string Id,
    string Href,
    string MediaType,
    string? FallbackId,
    string? MediaOverlayId,
    string? Properties,
    SourceLocation Location,
    IReadOnlyList<Diagnostic> LocalDiagnostics);

public sealed record SpineItemRef(
    int Order,
    string Idref,
    string? Linear,
    string? Properties,
    SourceLocation Location);

public sealed record PackageDocument(
    string PackageId,
    string EntryName,
    byte[] RawBytes,
    string RawText,
    string Sha256,
    string? UniqueIdentifier,
    string? Version,
    string? Lang,
    string? Dir,
    string? Id,
    IReadOnlyList<ManifestItem> Manifest,
    IReadOnlyList<SpineItemRef> Spine,
    string? TocId,
    IReadOnlyList<MetadataItem> Metadata,
    IReadOnlyList<Diagnostic> LocalDiagnostics)
{
    public ManifestItem? FindManifestItem(string id) =>
        Manifest.FirstOrDefault(m => m.Id == id);
}

public sealed record UnknownRawEntry(
    string EntryName,
    byte[] RawBytes,
    string Sha256,
    IReadOnlyList<string> Reasons);

public sealed record EpubMaterialSet(
    string MaterialSetId,
    DateTimeOffset ImportedAt,
    byte[] OriginalZipBytes,
    string OriginalZipSha256,
    long OriginalZipLength,
    IReadOnlyList<ZipEntryRecord> Entries,
    ContainerDocument? Container,
    IReadOnlyList<PackageDocument> Packages,
    IReadOnlyList<UnknownRawEntry> UnknownEntries,
    IReadOnlyList<Diagnostic> ImportDiagnostics)
{
    public string ContentVersion =>
        Hashing.StableId("mset", OriginalZipSha256, Entries.Count,
            string.Join("|", Packages.Select(p => p.Sha256)),
            Container?.Sha256 ?? "no-container");
}
