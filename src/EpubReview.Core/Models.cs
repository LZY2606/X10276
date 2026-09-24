using System.Text.Json.Serialization;

namespace EpubReview.Core;

public enum AnalysisOutcome { Success, ResourceBudgetExhausted, FormatCorrupt, SemanticallyUnsolvable }
public enum DiagnosticSeverity { Info, Warning, Error }
public enum DecisionKind { Accepted, Rejected, Adjusted }

public sealed record SourceLocation(string EntryPath, int Line, int Column, string? XPath = null)
{
    public override string ToString() => $"{EntryPath}:{Line}:{Column}{(XPath is null ? string.Empty : $" ({XPath})")}";
}

public sealed record Quantity(string RawRepresentation, string Value, string Unit, string CoordinateSystem, string EffectiveRange, string? FailureReason = null)
{
    public static Quantity Bytes(long value, string range) => new(value.ToString(System.Globalization.CultureInfo.InvariantCulture), value.ToString(System.Globalization.CultureInfo.InvariantCulture), "bytes", "zero-based ZIP byte offset", range);
    public static Quantity Ordinal(int value, string basis) => new(value.ToString(System.Globalization.CultureInfo.InvariantCulture), value.ToString(System.Globalization.CultureInfo.InvariantCulture), "ordinal", basis, "event version in which source remains present");
}

public sealed record Diagnostic(string Code, DiagnosticSeverity Severity, string Message, SourceLocation Location, string? SubjectId = null, string? RuleId = null, string? BasisEventCode = null);

public sealed record ZipEntryRecord(string FullName, string RawName, long OffsetBytes, long CompressedLengthBytes, long LengthBytes, uint Crc32, ushort CompressionMethod, string Sha256, byte[] Bytes)
{
    public Quantity OffsetQuantity => Quantity.Bytes(OffsetBytes, $"[0,{LengthBytes}) after local header");
    public Quantity LengthQuantity => Quantity.Bytes(LengthBytes, $"entry:{FullName}");
}

public sealed record ContainerRootFile(string MediaType, string FullPath, string RawHref, IReadOnlyDictionary<string,string> RawAttributes, SourceLocation Location);
public sealed record ContainerDocument(string RawXml, IReadOnlyList<ContainerRootFile> RootFiles, SourceLocation Location);
public sealed record RawAttribute(string Name, string Value);

public sealed record ManifestItemRecord(string Id, string RawHref, string MediaType, string? FallbackId, string? FallbackStyleId, string? MediaOverlayId, string PropertiesRaw, IReadOnlyList<RawAttribute> RawAttributes, SourceLocation Location);
public sealed record SpineRefRecord(string IdRef, string LinearRaw, int Occurrence, IReadOnlyList<RawAttribute> RawAttributes, SourceLocation Location);
public sealed record MetadataRecord(string MetadataId, string NamespaceUri, string LocalName, string Value, string RawXml, SourceLocation Location);

public sealed record RawPackageDocument(string PackageId, string EntryPath, string RawXml, string? UniqueIdentifier, IReadOnlyList<ManifestItemRecord> ManifestItems, IReadOnlyList<SpineRefRecord> SpineRefs, IReadOnlyList<MetadataRecord> Metadata, IReadOnlyList<RawAttribute> RawDocumentAttributes, SourceLocation Location);

public sealed record ResourceRecord(string ResourceId, string PackageId, string ManifestId, string RawHref, string NormalizedPath, string? Fragment, string MediaType, bool DeclaredInManifest, bool ExistsInZip, long LengthBytes, string? Sha256, byte[] RawBytes, IReadOnlyCollection<string> AnchorIds, SourceLocation Location, Quantity Size)
{
    public string StableDisplayName => string.IsNullOrWhiteSpace(ManifestId) ? NormalizedPath : $"{ManifestId} → {NormalizedPath}";
}

public sealed record Timing(string RawBegin, string RawEnd, string? BeginSeconds, string? EndSeconds, string Unit, string TimeBasis, string EffectiveRange, string? FailureReason);

public sealed record ResourceReference(string ReferenceId, string SourceResourceId, string? TargetResourceId, string Relation, string RawTarget, string? NormalizedPath, string? Fragment, bool TargetExists, bool FragmentExists, SourceLocation Location, Timing? Timing = null)
{
    public string StableLabel => $"{Relation}:{RawTarget}";
}

public sealed record ReadingOrderItem(string ReadingOrderItemId, int Position, string PackageId, string ManifestId, string ResourceId, string LinearRaw, Quantity PositionQuantity, SourceLocation Location);

public sealed record NavNode(string NavNodeId, string NavigationDocumentResourceId, string? ParentNavNodeId, int Depth, int SiblingOrder, string Text, string? ReferenceId, SourceLocation Location);

public sealed record FallbackCandidate(string FallbackPathId, string SourceManifestId, string CandidateManifestId, int Hop, string RawLink, string MediaType, bool Legal, string? FailureCode, SourceLocation Location);

public sealed record EpubMaterial(string MaterialId, string OriginalFileName, string Sha256, long LengthBytes, string ReceivedAtInstant, IReadOnlyList<ZipEntryRecord> ZipEntries, ContainerDocument? Container, IReadOnlyList<RawPackageDocument> Packages, IReadOnlyList<Diagnostic> Diagnostics)
{
    public Quantity FileSize => Quantity.Bytes(LengthBytes, $"material:{MaterialId}");
}

public sealed record RuleProfile(string RuleProfileId, int Version, string Name, int MaxZipEntries, long MaxUncompressedBytes, DiagnosticSeverity MissingFragmentSeverity, bool RequireUniqueSpineItems, bool RejectPathEscape, string ParametersJson, string CreatedAtInstant, string TimeBasis)
{
    public string ParameterVersion => $"{Name}@{Version}:{RuleProfileId[..12]}";
}

public sealed record AnalysisResult(string AnalysisId, string MaterialId, string RuleProfileId, string? ParentAnalysisId, string InputDigest, string ParameterVersion, string CalculatedAtInstant, AnalysisOutcome Outcome, IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<ResourceRecord> Resources, IReadOnlyList<ResourceReference> References, IReadOnlyList<ReadingOrderItem> ReadingOrder, IReadOnlyList<NavNode> NavigationTree, IReadOnlyList<FallbackCandidate> FallbackCandidates, IReadOnlyList<string> ParentDependencyIds, bool Adopted, string ScenarioLabel)
{
    [JsonIgnore] public bool HasErrors => Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
}

public sealed record EvidenceEvent(string EventId, long Sequence, long StateVersion, string OccurredAtInstant, string Type, string SubjectId, string PayloadJson, string InputSummary, string ParameterVersion, IReadOnlyList<string> ParentDependencyIds, bool Adopted, string? ReasonCode);

public sealed record ReviewDecision(string DecisionId, string EventId, string AnalysisId, string SubjectId, DecisionKind Kind, string Rationale, string ParametersJson, string? ConflictsWithDecisionId, string CreatedAtInstant);

public sealed record ReplayState(long Version, string ReplayedToEventId, IReadOnlyList<string> MaterialIds, IReadOnlyList<string> ProfileIds, IReadOnlyList<string> AnalysisIds, IReadOnlyList<string> DecisionIds, IReadOnlyList<EvidenceEvent> Events)
{
    public string TimeBasis => "UTC ISO-8601 case clock; state version is an immutable event sequence number";
}
