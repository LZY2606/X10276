using System.Text.Json.Serialization;

namespace EpubReview.Core;

public enum ReviewOutcome
{
    Accepted = 0,
    ResourceBudgetExhausted = 1,
    FormatCorrupt = 2,
    SemanticUnresolved = 3
}

public enum Severity
{
    Info = 0,
    Warning = 1,
    Error = 2
}

public sealed record ZipEntryRecord(
    string Path,
    long UncompressedLength,
    long CompressedLength,
    string Sha256,
    string OriginalMediaHint,
    byte[] Bytes);

public sealed record UnknownMetadata(
    string PackageId,
    string Prefix,
    string LocalName,
    string Namespace,
    string Value,
    IReadOnlyDictionary<string, string> Attributes,
    string RawXml,
    string Location);

public sealed record ManifestItem(
    string Id,
    string Href,
    string MediaType,
    string? FallbackId,
    string? MediaOverlayId,
    string Properties,
    IReadOnlyDictionary<string, string> RawAttributes,
    string Location);

public sealed record SpineItem(
    int Position,
    string IdRef,
    bool Linear,
    string Properties,
    string Location);

public sealed record PackageDocument(
    string StableId,
    string EntryPath,
    string MediaType,
    byte[] OriginalBytes,
    string Sha256,
    IReadOnlyList<ManifestItem> Manifest,
    IReadOnlyList<SpineItem> Spine,
    IReadOnlyList<UnknownMetadata> UnknownMetadata);

public sealed record RootPackageReference(string EntryPath, string MediaType, int Position);

public sealed record ContainerDocument(
    byte[] OriginalBytes,
    string Sha256,
    IReadOnlyList<RootPackageReference> RootPackages,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record ReferenceRecord(
    string StableId,
    string SourceStableId,
    string SourceEntryPath,
    string Attribute,
    string RawReference,
    string NormalizedPath,
    string? Fragment,
    string TargetStableId,
    string ReferenceKind,
    int Position,
    string Location);

public sealed record AnchorRecord(
    string StableId,
    string ResourceStableId,
    string EntryPath,
    string Name,
    int Position,
    string Location);

public sealed record NavNode(
    string StableId,
    string PackageStableId,
    string? ParentStableId,
    int Depth,
    int Ordinal,
    string Label,
    string? RawReference,
    string? TargetStableId,
    string? Fragment);

public sealed record FallbackCandidate(
    string StableId,
    string SourceItemStableId,
    string CandidateItemStableId,
    int Distance,
    string MediaType,
    IReadOnlyList<string> ChainItemStableIds,
    bool Adopted);

public sealed record Diagnostic(
    string StableId,
    string RuleId,
    Severity Severity,
    string Message,
    string Location,
    string? SubjectStableId,
    string OriginalValue,
    string? FailureReason,
    IReadOnlyDictionary<string, string> Evidence);

public sealed record ResourceNode(
    string StableId,
    string PackageStableId,
    string EntryPath,
    string? ManifestItemId,
    string DeclaredMediaType,
    string InferredMediaType,
    long ByteLength,
    string Unit,
    string Sha256,
    bool Exists,
    bool InSpine,
    IReadOnlyList<string> LegalCandidateEntryPaths);

public sealed record ReadingOrderItem(
    string StableId,
    string PackageStableId,
    int Position,
    string ItemStableId,
    string EntryPath,
    bool Linear,
    bool DuplicateIdRef,
    string Branch);

public sealed record GraphPosition(string StableId, double X, double Y, string Unit, string CoordinateBasis);

public sealed record ReviewParameters(
    int Version,
    int MaxZipEntries,
    long MaxTotalBytes,
    int MaxGraphEdges,
    int MaxFallbackDepth,
    bool CaseSensitivePaths,
    string MissingFragmentSeverity,
    string CoordinateBasis,
    string TimeBasis,
    string LengthUnit,
    DateTime EffectiveFromUtc,
    DateTime? EffectiveToUtc)
{
    public static ReviewParameters Initial(DateTime now) => new(
        1, 4096, 64L * 1024L * 1024L, 512, 12, true, "Warning",
        "SVG user unit; origin at top-left; x right, y down",
        "UTC; ISO 8601 extended instant",
        "byte", now, null);
}

public sealed record InputSummary(
    string ArchiveBytesSha256,
    long ArchiveByteLength,
    string Unit,
    IReadOnlyDictionary<string, string> MaterialVersionHashes);

public sealed record ReviewSnapshot(
    string StableId,
    string ProjectId,
    string Scope,
    ReviewOutcome Outcome,
    InputSummary InputSummary,
    int ParameterVersion,
    IReadOnlyList<string> ParentDependencyEventIds,
    string RuleSetVersion,
    DateTime ProducedAtUtc,
    IReadOnlyList<ResourceNode> Resources,
    IReadOnlyList<ReadingOrderItem> ReadingOrder,
    IReadOnlyList<ReferenceRecord> References,
    IReadOnlyList<AnchorRecord> Anchors,
    IReadOnlyList<NavNode> Navigation,
    IReadOnlyList<UnknownMetadata> UnknownMetadata,
    IReadOnlyList<FallbackCandidate> FallbackCandidates,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<Diagnostic> ParseDiagnostics,
    IReadOnlyList<GraphPosition> Positions,
    IReadOnlyDictionary<string, string> Basis);

public sealed record EpubMaterials(
    string MaterialId,
    int Version,
    string? ParentMaterialVersionId,
    string DirectSource,
    byte[] OriginalArchiveBytes,
    string ArchiveSha256,
    IReadOnlyList<ZipEntryRecord> Entries,
    ContainerDocument Container,
    IReadOnlyList<PackageDocument> Packages,
    IReadOnlyList<Diagnostic> ParseDiagnostics)
{
    [JsonIgnore]
    public bool IsComplete => ParseDiagnostics.All(d => d.Severity != Severity.Error);
}

public sealed record ReviewDecision(
    string StableId,
    string EventId,
    string SubjectStableId,
    string Kind,
    bool Adopted,
    string Rationale,
    DateTime DecidedAtUtc,
    IReadOnlyDictionary<string, string> Parameters);

public sealed record ProjectState(
    string ProjectId,
    string Name,
    EpubMaterials? Materials,
    ReviewParameters Parameters,
    IReadOnlyList<ReviewDecision> Decisions,
    IReadOnlyList<ReviewSnapshot> Snapshots,
    IReadOnlyList<ProjectEvent> Events);

public sealed record ProjectEvent(
    string Id,
    string ProjectId,
    string BranchId,
    string? ParentEventId,
    int Sequence,
    string Type,
    string Actor,
    DateTime OccurredAtUtc,
    string PayloadJson,
    string? InputSummaryJson,
    int? ParameterVersion,
    IReadOnlyList<string> Dependencies);

public sealed record BranchRecord(string BranchId, string Name, string? ParentEventId, DateTime CreatedAtUtc);
