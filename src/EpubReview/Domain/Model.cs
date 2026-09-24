using System.Text.Json.Serialization;

namespace EpubReview.Domain;

public enum AnalysisOutcome
{
    Success = 0,
    ResourceBudgetExhausted = 1,
    FormatCorrupt = 2,
    SemanticUnresolvable = 3
}

public enum Severity { Info = 0, Warning = 1, Error = 2 }

public sealed record Diagnostic(
    string Code,
    Severity Severity,
    string Message,
    string? Entry,
    int? Line,
    int? Column,
    string? Context);

public sealed record ResourceBudget(
    int MaxEntries = 4096,
    long MaxTotalBytes = 64L * 1024 * 1024,
    long MaxEntryBytes = 8L * 1024 * 1024,
    int MaxGraphNodes = 8192)
{
    public static readonly ResourceBudget Default = new();
}

public sealed record AnalysisParameters(
    ResourceBudget Budget,
    int MaxFallbackDepth = 8)
{
    public static readonly AnalysisParameters Default = new(ResourceBudget.Default);
    public const string RuleVersion = "epub-review-rules/1.0.0";
}

public sealed record ZipEntryData(string Name, byte[] Bytes);

public sealed record UnknownMetadata(
    string Name,
    string? Value,
    string RawXml,
    bool Recognized);

public sealed record ManifestItem(
    string Id,
    string Href,
    string? MediaType,
    string? Fallback,
    IReadOnlyList<string> Properties,
    string? MediaOverlay,
    int Line);

public sealed record SpineItemRef(string IdRef, bool Linear, int Index, int Line);

public sealed record PackageDocument(
    string FullPath,
    string Dir,
    IReadOnlyList<ManifestItem> Manifest,
    IReadOnlyList<SpineItemRef> Spine,
    IReadOnlyList<UnknownMetadata> Metadata);

public sealed record Rootfile(string FullPath, string? MediaType);

public sealed record ReadingOrderItem(
    string PackagePath,
    int Index,
    string IdRef,
    string? ResolvedPath,
    string? MediaType,
    bool Linear,
    bool IsDuplicate,
    bool Unresolved);

public sealed record GraphNode(
    string Id,
    string Path,
    string? MediaType,
    string? ManifestItemId,
    bool InManifest,
    bool InSpine,
    string PackagePath);

public sealed record GraphEdge(
    string FromPath,
    string ToPath,
    string Kind,
    string? Fragment,
    bool? FragmentExists,
    string? SourceEntry,
    int? Line);

public sealed record NavNode(
    string Label,
    string? Href,
    string? ResolvedPath,
    string? Fragment,
    bool? FragmentExists,
    IReadOnlyList<NavNode> Children);

public sealed record BrokenAnchor(
    string SourceEntry,
    string Href,
    string Fragment,
    string TargetPath,
    string Origin,
    int? Line);

public sealed record FallbackCandidate(
    string PackagePath,
    string ItemId,
    string ItemHref,
    string CandidateId,
    string CandidateHref,
    string? CandidateMediaType,
    int Depth);

public sealed record OverlayClip(
    string PackagePath,
    string SmilItemId,
    string TextSrc,
    string? ResolvedTextPath,
    string? TextFragment,
    bool? TextFragmentExists,
    string? AudioSrc,
    bool AudioExists,
    double? ClipBeginSeconds,
    double? ClipEndSeconds,
    string ClockBasis);

public sealed record AnalysisResult(
    string AnalysisId,
    string InputDigest,
    string RuleVersion,
    IReadOnlyList<string> ParentIds,
    AnalysisParameters Parameters,
    AnalysisOutcome Outcome,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<Rootfile> Rootfiles,
    IReadOnlyList<ReadingOrderItem> ReadingOrder,
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphEdge> Edges,
    IReadOnlyList<NavNode> NavTrees,
    IReadOnlyList<BrokenAnchor> BrokenAnchors,
    IReadOnlyList<FallbackCandidate> FallbackCandidates,
    IReadOnlyList<OverlayClip> OverlayClips,
    IReadOnlyList<UnknownMetadata> UnknownMetadata,
    IReadOnlyList<string> PreservedEntries);

public static class DiagnosticCodes
{
    public const string BudgetEntries = "ResourceBudgetExhausted.Entries";
    public const string BudgetBytes = "ResourceBudgetExhausted.Bytes";
    public const string BudgetEntryBytes = "ResourceBudgetExhausted.EntryBytes";
    public const string ZipCorrupt = "FormatCorrupt.Zip";
    public const string XmlCorrupt = "FormatCorrupt.Xml";
    public const string ContainerMissing = "FormatCorrupt.ContainerMissing";
    public const string NoRootfile = "SemanticUnresolvable.NoRootfile";
    public const string PackageStructureMissing = "SemanticUnresolvable.PackageStructure";
    public const string SpineUnknownIdref = "SemanticUnresolvable.SpineUnknownIdref";
    public const string MultiplePackages = "Package.Multiple";
    public const string MissingResource = "Resource.Missing";
    public const string FallbackCycle = "Fallback.Cycle";
    public const string FallbackTargetMissing = "Fallback.TargetMissing";
    public const string FallbackDepthExceeded = "Fallback.DepthExceeded";
    public const string FragmentNotFound = "Anchor.FragmentNotFound";
    public const string PathEscapesContainer = "Path.EscapesContainer";
    public const string SpineDuplicateIdref = "Spine.DuplicateIdref";
    public const string OverlayBrokenAudio = "MediaOverlay.BrokenAudioLink";
    public const string OverlayBrokenText = "MediaOverlay.BrokenTextLink";
    public const string OverlayMissingLink = "MediaOverlay.MissingLink";
    public const string UnmanifestedReference = "Resource.UnmanifestedReference";
    public const string UnknownMetadataPreserved = "Metadata.UnknownPreserved";
}
