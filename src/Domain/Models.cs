using System.Text.Json.Serialization;

namespace EpubSpectrum.Domain;

public enum IngestOutcome
{
    Ok = 0,
    BudgetExhausted = 1,
    FormatCorrupt = 2,
    SemanticUnresolvable = 3
}

public enum Severity { Info, Warning, Error }

public enum EdgeKind
{
    Spine,
    NavLink,
    ManifestHref,
    Fallback,
    MediaOverlay,
    ContainerRootFile,
    CssLink,
    MetadataUnknown
}

public enum FindingStatus
{
    Open,
    Accepted,
    Rejected,
    Adjusted
}

public sealed record Diagnostic(
    string Code,
    Severity Severity,
    string Message,
    string? Location = null,
    string? Unit = null,
    long? Offset = null,
    long? Length = null)
{
    public static readonly IReadOnlyList<Diagnostic> None = Array.Empty<Diagnostic>();
}

public sealed record ZipEntryRecord(
    int Index,
    string RawName,
    ushort DosVersion,
    ushort Flags,
    ushort CompressionMethod,
    uint Crc32,
    long CompressedLength,
    long UncompressedLength,
    ushort ModifiedTime,
    ushort ModifiedDate,
    byte[] RawBytes,
    bool CrcVerified);

public sealed record ContainerRootFile(string FullPath, string MediaType, int Order);

public sealed record ContainerXml(
    string RawXml,
    IReadOnlyList<ContainerRootFile> RootFiles,
    string? DefaultVersion,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record ManifestItem(
    string Id,
    string RawHref,
    string MediaType,
    string? FallbackId,
    string? MediaOverlayId,
    string? Properties);

public sealed record SpineItem(string IdRef, int Position, bool Linear, string? RawProperties);

public sealed record UnknownMetadata(
    string StableId,
    string RawElementName,
    string RawOuterXml,
    string ParentSection,
    string? NamespaceUri);

public sealed record PackageDoc(
    string PackageId,
    string EntryPath,
    string RawXml,
    string? UniqueIdentifier,
    string? Version,
    string? XmlLang,
    IReadOnlyList<ManifestItem> Manifest,
    IReadOnlyList<SpineItem> Spine,
    IReadOnlyList<UnknownMetadata> UnknownMetadata,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record RawMaterial(
    string MaterialId,
    string Kind,
    string StableKey,
    byte[] RawBytes,
    string MediaType,
    string OriginalRepresentation,
    string Sha256,
    long Length,
    string Unit,
    string? CoordinateBasis,
    IReadOnlyDictionary<string, string> Attributes);

public sealed record IngestResult(
    IngestOutcome Outcome,
    IReadOnlyList<ZipEntryRecord> Entries,
    ContainerXml? Container,
    IReadOnlyList<PackageDoc> Packages,
    IReadOnlyList<RawMaterial> Materials,
    IReadOnlyList<Diagnostic> Diagnostics,
    string InputSha256,
    long InputLength,
    ResourceBudget Budget)
{
    public bool IsSuccess => Outcome == IngestOutcome.Ok;
}

public sealed record ResourceBudget(long TotalBytes, long UsedBytes, int TotalEntries, int UsedEntries)
{
    public long RemainingBytes => TotalBytes - UsedBytes;
    public bool Exceeded => UsedBytes > TotalBytes || UsedEntries > TotalEntries;
}

public sealed record ResourceNode(
    string NodeId,
    string? PackageId,
    string RawHref,
    string NormalizedPath,
    string MediaType,
    bool InManifest,
    bool InSpine,
    bool IsNav,
    string MaterialId,
    bool ExistsInContainer,
    bool CaseAmbiguous,
    IReadOnlyList<string> CaseCandidates,
    string Unit);

public sealed record ResourceEdge(
    string EdgeId,
    string SourceNodeId,
    string TargetNodeId,
    EdgeKind Kind,
    int Order,
    bool Valid,
    string? Fragment,
    bool FragmentResolved,
    string? Location);

public sealed record ReadingOrderEntry(
    string EntryId,
    int Sequence,
    string PackageId,
    string SpineIdRef,
    string NodeId,
    bool Linear,
    string RawHref,
    string MediaType);

public sealed record NavNode(
    string NavNodeId,
    int Depth,
    int Order,
    string PackageId,
    string? Text,
    string NodeId,
    string? Fragment,
    bool Resolved,
    string? ParentNavNodeId,
    string? Location);

public sealed record FallbackCandidate(
    string ChainId,
    string PackageId,
    string StartItemId,
    IReadOnlyList<string> ChainItemIds,
    bool Cyclic,
    bool MissingTarget,
    IReadOnlyList<string> Diagnostics);

public sealed record BrokenAnchor(
    string AnchorId,
    string PackageId,
    string SourceNodeId,
    string RawHref,
    string? Fragment,
    string Reason,
    string Location,
    Severity Severity);

public sealed record Finding(
    string FindingId,
    string Code,
    Severity Severity,
    string Status,
    string Message,
    string? SubjectStableId,
    string? Location,
    IReadOnlyList<string> Candidates,
    IReadOnlyList<string> EvidenceEventIds,
    string RuleVersion,
    string? DecisionEventId);

public sealed record ResourceGraph(
    IReadOnlyList<ResourceNode> Nodes,
    IReadOnlyList<ResourceEdge> Edges);

public sealed record AnalysisSnapshot(
    string SnapshotId,
    string RuleVersion,
    string InputSha256,
    string ParentEventId,
    string? DecisionEventId,
    IngestOutcome Outcome,
    IReadOnlyList<Diagnostic> Diagnostics,
    ResourceGraph Graph,
    IReadOnlyList<ReadingOrderEntry> ReadingOrder,
    IReadOnlyList<NavNode> NavTree,
    IReadOnlyList<BrokenAnchor> BrokenAnchors,
    IReadOnlyList<FallbackCandidate> FallbackChains,
    IReadOnlyList<Finding> Findings,
    RuleParams Parameters,
    DateTimeOffset ComputedAt)
{
    public static readonly AnalysisSnapshot Empty = new(
        "", "", "", "", null, IngestOutcome.Ok,
        Array.Empty<Diagnostic>(),
        new ResourceGraph(Array.Empty<ResourceNode>(), Array.Empty<ResourceEdge>()),
        Array.Empty<ReadingOrderEntry>(), Array.Empty<NavNode>(),
        Array.Empty<BrokenAnchor>(), Array.Empty<FallbackCandidate>(),
        Array.Empty<Finding>(), RuleParams.V1, DateTimeOffset.UnixEpoch);
}

public sealed record RuleParams(
    string Version,
    bool ResolveLinksCaseSensitive,
    bool TreatDuplicateSpineAsError,
    bool RequireFragmentTarget,
    long MaxContainerBytes,
    int MaxEntries,
    bool AutoPickAmbiguousPath)
{
    public static readonly RuleParams V1 = new(
        "rules/v1",
        ResolveLinksCaseSensitive: true,
        TreatDuplicateSpineAsError: true,
        RequireFragmentTarget: true,
        MaxContainerBytes: 64L * 1024 * 1024,
        MaxEntries: 4096,
        AutoPickAmbiguousPath: false);

    public string ToCompactJson() =>
        System.Text.Json.JsonSerializer.Serialize(this, DomainJson.Options);
}

[JsonSerializable(typeof(Dictionary<string, string>))]
internal static class DomainJson
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
