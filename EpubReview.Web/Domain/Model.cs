namespace EpubReview.Domain;

public enum AnalyzeOutcome { Completed, BudgetExhausted, FormatCorrupt, SemanticallyUnresolvable }
public enum DiagnosticSeverity { Info, Warning, Error }
public enum LinkStatus { Ok, MissingResource, AmbiguousResource, PathEscapesContainer, MissingFragment, MalformedTarget, MissingFallbackTarget, FallbackCycle, MissingOverlay, DuplicateSpineEntry, UnrecognizedMetadata }
public enum DecisionAction { Accepted, Rejected, Adjusted }

public sealed record Diagnostic(string Code, DiagnosticSeverity Severity, string Message, string Location, int? Line, int? Position, string? SubjectId, AnalyzeOutcome? Outcome);

public sealed record AnalysisParameters(
    long MaxArchiveBytes = 8 * 1024 * 1024,
    int MaxZipEntries = 1024,
    bool IncludeUnreferencedResources = true,
    bool RequireFragmentTargets = true,
    string FragmentAttributes = "id,xml:id",
    string CoordinateSpace = "SVG user units",
    string TimeUnit = "ISO-8601 UTC",
    string ByteUnit = "byte")
{
    public string Version => StableHashes.ShortHash($"{MaxArchiveBytes}|{MaxZipEntries}|{IncludeUnreferencedResources}|{RequireFragmentTargets}|{FragmentAttributes}|{CoordinateSpace}|{TimeUnit}|{ByteUnit}");
}

public sealed record ReviewDecision(string EventId, string SubjectId, DecisionAction Action, string Rationale, string OccurredAt, string? ConflictsWithEventId);

public sealed record AnalysisRequest(byte[] ArchiveBytes, AnalysisParameters Parameters, string? ParentDependencyVersion = null, IReadOnlyList<ReviewDecision>? Decisions = null);

public sealed record EntryMaterial(string MaterialId, int OriginalIndex, string RawPath, string? NormalizedPath, long ByteLength, string Sha256, string? MediaType, string? InvalidReason, byte[] Bytes);

public sealed record PackageInfo(
    string PackageId,
    string RootFileMaterialId,
    string RawPath,
    string? NormalizedPath,
    string? MediaType,
    int ManifestItemCount,
    IReadOnlyList<UnknownMetadata> UnknownMetadata);

public sealed record UnknownMetadata(string Id, string PackageId, int? Line, int? Position, string Namespace, string LocalName, string OuterXml);

public sealed record ResourceNode(
    string Id,
    string Kind,
    string Label,
    string RawPath,
    string? NormalizedPath,
    string? DeclaredMediaType,
    string? InferredMediaType,
    long ByteLength,
    string ByteUnit,
    string Sha256,
    IReadOnlyList<string> CandidateMaterialIds,
    bool IsReferenced,
    string? ValidRange,
    string? InvalidReason);

public sealed record ReferenceEdge(
    string Id,
    string SourceId,
    string Relation,
    string RawTarget,
    string? NormalizedTarget,
    string? Fragment,
    LinkStatus Status,
    IReadOnlyList<string> CandidateTargetIds,
    string? TargetId,
    string? DiagnosticCode,
    string? Message);

public sealed record SpineEntry(string Id, string PackageId, int Order, string IdRef, string? ItemId, bool IsDuplicate, string? DiagnosticCode);

public sealed record NavigationNode(string Id, string ParentId, string PackageId, int Depth, int Order, string Label, string RawHref, string? Fragment, string SourceItemId, string? TargetId, bool IsBroken);

public sealed record FallbackStep(string Id, string SourceItemId, string TargetIdRef, int Step, LinkStatus Status, string? TargetItemId, string? DiagnosticCode, string CycleKey);

public sealed record OverlayLink(string Id, string SourceItemId, string OverlayIdRef, string? OverlayItemId, LinkStatus Status, string? DiagnosticCode);

public sealed record JudgmentBasis(string Code, string Status, string MachineRule, string[] SubjectIds);

public sealed record AnalysisResult(
    string ArchiveMaterialId,
    string InputSha256,
    string InputUnit,
    long InputByteLength,
    string ParametersVersion,
    string? ParentDependencyVersion,
    string DependencyVersion,
    string GeneratedAt,
    AnalyzeOutcome Outcome,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<EntryMaterial> Entries,
    IReadOnlyList<PackageInfo> Packages,
    IReadOnlyList<ResourceNode> Resources,
    IReadOnlyList<ReferenceEdge> References,
    IReadOnlyList<SpineEntry> Spine,
    IReadOnlyList<NavigationNode> Navigation,
    IReadOnlyList<FallbackStep> Fallback,
    IReadOnlyList<OverlayLink> Overlays,
    IReadOnlyList<JudgmentBasis> Judgments,
    IReadOnlyList<ReviewDecision> Decisions,
    string CoordinateSpace,
    string TimeUnit,
    string ByteUnit);

public sealed record EventRecord(string EventId, int Sequence, string Type, string OccurredAt, string ParametersVersion, string? ParentDependencyVersion, string? InputSha256, string? DependencyVersion, string PayloadJson, string Status);

public sealed record StoredMaterial(string MaterialId, string CaseId, string Role, string RawPath, string? NormalizedPath, string? MediaType, long ByteLength, string Sha256, string? ParentMaterialId, string CreatedEventId, byte[] Bytes);
