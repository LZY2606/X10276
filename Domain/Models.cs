using System.Text.Json.Serialization;

namespace EpubSpectra.Domain;

public enum OutcomeKind
{
    Ok,
    BudgetExhausted,
    FormatCorrupt,
    SemanticallyUnresolvable
}

public enum Severity { Info, Warning, Error }

public enum FindingCode
{
    MultiPackage,
    FallbackCycle,
    MissingResource,
    FragmentMissing,
    PathEscape,
    SpineDuplicate,
    MediaOverlayBroken,
    UnknownMetadata,
    AmbiguousCandidate,
    UnusedResource
}

public enum DecisionVerdict { Accepted, Rejected, Adjusted }

/// <summary>位置基准：全部诊断都带容器内路径与 1 基行列号；字节偏移可选。</summary>
public sealed record SourceLocation(string Path, int Line, int Column, long? ByteOffset = null)
{
    public override string ToString() => ByteOffset is long b ? $"{Path}:{Line}:{Column}@0x{b:x}" : $"{Path}:{Line}:{Column}";
}

public sealed record ZipEntry(
    string EntryName,
    long Length,
    long CompressedLength,
    uint Crc32,
    byte[] Sha256,
    string MediaType,
    bool Stored,
    SourceLocation? Location);

public sealed record ContainerRootfile(
    string RootfileId,
    string FullPath,
    string? MediaType,
    SourceLocation Location,
    bool PathEscapesContainer,
    bool DuplicateFullPath);

public sealed record ContainerDocument(
    string RawXml,
    IReadOnlyList<ContainerRootfile> Rootfiles,
    string? DefaultRootfileId,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record MetadataEntry(
    string MetadataId,
    string Property,
    string? Prefix,
    string Value,
    string? Refines,
    string? Scheme,
    bool Unknown,
    string RawXmlSnippet,
    SourceLocation Location);

public sealed record ManifestItem(
    string ManifestItemId,
    string Id,
    string Href,
    string? MediaType,
    string? FallbackIdRef,
    string? MediaOverlayIdRef,
    string? Properties,
    SourceLocation Location);

public sealed record SpineItem(
    int Index,
    string IdRef,
    bool Linear,
    string? Id,
    SourceLocation Location);

public sealed record PackageDocument(
    string PackageId,
    string FullPath,
    string? MediaType,
    string RawXml,
    string? UniqueIdentifier,
    string Version,
    string? XmlLang,
    string? PrefixMap,
    IReadOnlyList<MetadataEntry> Metadata,
    IReadOnlyList<ManifestItem> Manifest,
    IReadOnlyList<SpineItem> Spine,
    string? TocManifestId,
    IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>原始层资源：直接来自 manifest 声明或 ZIP 条目，绝不被派生层删除。</summary>
public sealed record ResourceRecord(
    string ResourceId,
    string ContainerPath,
    string? DeclaredMediaType,
    string? SniffedMediaType,
    bool PresentInZip,
    long? ByteLength,
    byte[]? Sha256,
    string? ManifestItemId,
    string? OwningPackageId,
    string? FragmentNote,
    SourceLocation? Location);

public sealed record MediaOverlayRecord(
    string OverlayId,
    string ManifestItemId,
    string? SmilPath,
    bool PresentInZip,
    IReadOnlyList<OverlaySpan> Spans,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record OverlaySpan(
    string SpanId,
    string TextRef,
    string? AudioRef,
    string? ClipBegin,
    string? ClipEnd,
    SourceLocation? Location);

public sealed record ArchiveMaterial(
    string ArchiveId,
    string OriginalFileName,
    string ImportedAtIso,
    string InputSha256,
    long SizeBytes,
    IReadOnlyList<ZipEntry> Entries,
    ContainerDocument? Container,
    IReadOnlyList<PackageDocument> Packages,
    IReadOnlyList<ResourceRecord> Resources,
    IReadOnlyList<MediaOverlayRecord> Overlays,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool IsUsable => Container is not null;
}

public sealed record Diagnostic(
    string Code,
    string Message,
    Severity Severity,
    SourceLocation? Location,
    string? SubjectId = null)
{
    public string Anchor => Location?.ToString() ?? "(no-location)";
}
