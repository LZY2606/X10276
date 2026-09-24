using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EpubReview.Core;

public enum ReviewStatus { Success, CompletedWithDiagnostics, MalformedInput, ResourceBudgetExhausted, SemanticallyUnresolvable }
public enum DiagnosticSeverity { Info, Warning, Error, Fatal }
public enum DiagnosticCategory { Integrity, Format, Budget, Semantics, Metadata, Decision }
public enum DecisionKind { Accept, Reject, ParameterAdjustment, ConflictResolution }

public sealed record SourceLocation
{
    public string? ZipEntry { get; init; }
    public int? Line { get; init; }
    public int? Column { get; init; }
    public string? XPath { get; init; }
    public int? Ordinal { get; init; }
    public string CoordinateBasis { get; init; } = "XML line/column, one-based; ZIP ordinal zero-based";
    public override string ToString()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(ZipEntry)) parts.Add(ZipEntry);
        if (Line is { } line) parts.Add($"line {line}");
        if (Column is { } column) parts.Add($"column {column}");
        if (!string.IsNullOrWhiteSpace(XPath)) parts.Add(XPath);
        if (Ordinal is { } ordinal) parts.Add($"ordinal {ordinal}");
        return string.Join(", ", parts);
    }
}

public sealed record Diagnostic
{
    public required string Code { get; init; }
    public required DiagnosticSeverity Severity { get; init; }
    public required DiagnosticCategory Category { get; init; }
    public required string Message { get; init; }
    public required SourceLocation Location { get; init; }
    public string? AffectedStableId { get; init; }
    public string? RawValue { get; init; }
}

public sealed record ZipEntryMaterial
{
    public required string StableId { get; init; }
    public required string RawEntryName { get; init; }
    public required string NormalizedEntryName { get; init; }
    public required byte[] Bytes { get; init; }
    public required string Sha256 { get; init; }
    public long LengthBytes { get; init; }
    public long CompressedLengthBytes { get; init; }
    public long Crc32 { get; init; }
    public string LastWriteTimeRaw { get; init; } = "";
    public string Unit { get; init; } = "byte";
    public SourceLocation? Location { get; init; }
}

public sealed record UnknownMetadata { public required string StableId { get; init; } public required string PackageStableId { get; init; } public required string ElementName { get; init; } public required string Namespace { get; init; } public required string RawXml { get; init; } public required SourceLocation Location { get; init; } }
public sealed record ManifestItem { public required string StableId { get; init; } public required string PackageStableId { get; init; } public required string ManifestId { get; init; } public required string RawHref { get; init; } public string? NormalizedPath { get; init; } public required string MediaType { get; init; } public string? FallbackManifestId { get; init; } public string? MediaOverlayManifestId { get; init; } public string? Properties { get; init; } public required Dictionary<string, string> RawAttributes { get; init; } public required SourceLocation Location { get; init; } }
public sealed record RootFileReference { public required string RawPath { get; init; } public string? NormalizedPath { get; init; } public string? MediaType { get; init; } public required SourceLocation Location { get; init; } }
public sealed record PackageMaterial
{
    public required string StableId { get; init; }
    public required string ContainerRawPath { get; init; }
    public required string NormalizedPath { get; init; }
    public required byte[] Bytes { get; init; }
    public required string Sha256 { get; init; }
    public string? UniqueIdentifier { get; init; }
    public string? Version { get; init; }
    public string? XmlLang { get; init; }
    public string? MediaType { get; init; }
    public required IReadOnlyList<ManifestItem> ManifestItems { get; init; }
    public required IReadOnlyList<UnknownMetadata> UnknownMetadata { get; init; }
    public required SourceLocation Location { get; init; }
}
public sealed record ContainerMaterial { public required byte[] Bytes { get; init; } public required string Sha256 { get; init; } public required IReadOnlyList<RootFileReference> RootFiles { get; init; } public required SourceLocation Location { get; init; } }
public sealed record ResourceAsset { public required string StableId { get; init; } public string? PackageStableId { get; init; } public required string RawReference { get; init; } public string? NormalizedPath { get; init; } public string? Fragment { get; init; } public string? MediaType { get; init; } public string? Sha256 { get; init; } public long LengthBytes { get; init; } public bool ExistsInContainer { get; init; } public bool IsPathEscaped { get; init; } public string? ManifestItemStableId { get; init; } public string Unit { get; init; } = "byte"; }
public sealed record ReferenceEdge { public required string StableId { get; init; } public required string SourceStableId { get; init; } public required string RawHref { get; init; } public string? TargetResourceStableId { get; init; } public string? Fragment { get; init; } public required string ReferenceKind { get; init; } public bool TargetExists { get; init; } public bool FragmentExists { get; init; } public int Ordinal { get; init; } public required SourceLocation Location { get; init; } }
public sealed record SpineEntry { public required string StableId { get; init; } public required string PackageStableId { get; init; } public required string IdRef { get; init; } public string? ManifestItemStableId { get; init; } public string? ResourceStableId { get; init; } public bool Linear { get; init; } public int Position { get; init; } public string Unit { get; init; } = "ordinal"; public required SourceLocation Location { get; init; } }
public sealed record NavigationNode { public required string StableId { get; init; } public required string PackageStableId { get; init; } public required string Label { get; init; } public required string RawHref { get; init; } public string? ResourceStableId { get; init; } public string? Fragment { get; init; } public int Depth { get; init; } public int Ordinal { get; init; } public string? ParentStableId { get; init; } public required SourceLocation Location { get; init; } }
public sealed record BrokenAnchor { public required string StableId { get; init; } public required string ReferenceStableId { get; init; } public required string RawHref { get; init; } public required string Fragment { get; init; } public string? ResourceStableId { get; init; } public required SourceLocation Location { get; init; } }
public sealed record FallbackCandidate { public required string StableId { get; init; } public required string OriginManifestItemStableId { get; init; } public required string CandidateManifestItemStableId { get; init; } public required string CandidateResourceStableId { get; init; } public required string MediaType { get; init; } public int Hop { get; init; } public bool IsLegalCandidate { get; init; } public bool IsSelectedByRules { get; init; } public string? Reason { get; init; } public string Unit { get; init; } = "hop"; }
public sealed record FallbackChainStatus { public required string OriginManifestItemStableId { get; init; } public required IReadOnlyList<FallbackCandidate> Candidates { get; init; } public bool HasCycle { get; init; } public bool HasMissingResource { get; init; } public IReadOnlyList<string> Cycle { get; init; } = Array.Empty<string>(); public string? MissingManifestId { get; init; } }
public sealed record MediaOverlayLink { public required string StableId { get; init; } public required string ContentManifestItemStableId { get; init; } public required string OverlayManifestIdRaw { get; init; } public string? OverlayManifestItemStableId { get; init; } public string? OverlayResourceStableId { get; init; } public bool LinkResolves { get; init; } public required SourceLocation Location { get; init; } }

public sealed record ReviewParameters
{
    public required string StableId { get; init; }
    public required int Version { get; init; }
    public int MaxContainerBytes { get; init; } = 50 * 1024 * 1024;
    public int MaxResourceCount { get; init; } = 5000;
    public int MaxFallbackDepth { get; init; } = 16;
    public bool RequireUniqueFallbackTarget { get; init; }
    public string[] SupportedMediaTypes { get; init; } = Array.Empty<string>();
    public string ByteUnit { get; init; } = "byte";
    public string FallbackDepthUnit { get; init; } = "hop";
}

public sealed record MaterialDependency { public required string Role { get; init; } public required string StableId { get; init; } public required string Sha256 { get; init; } public long LengthBytes { get; init; } }
public sealed record EffectiveRange { public required string FromEventId { get; init; } public string? ToEventId { get; init; } public string TimeBasis { get; init; } = "UTC ISO 8601"; public string? InvalidationReason { get; init; } }

public sealed record ReviewResult
{
    public required string StableId { get; init; }
    public required string CaseId { get; init; }
    public required ReviewStatus Status { get; init; }
    public required string InputSummary { get; init; }
    public required string InputMaterialSetHash { get; init; }
    public required string ParametersStableId { get; init; }
    public int ParametersVersion { get; init; }
    public string? ParentResultStableId { get; init; }
    public required IReadOnlyList<MaterialDependency> MaterialDependencies { get; init; }
    public required IReadOnlyList<ResourceAsset> Resources { get; init; }
    public required IReadOnlyList<ReferenceEdge> References { get; init; }
    public required IReadOnlyList<SpineEntry> Spine { get; init; }
    public required IReadOnlyList<NavigationNode> Navigation { get; init; }
    public required IReadOnlyList<BrokenAnchor> BrokenAnchors { get; init; }
    public required IReadOnlyList<FallbackChainStatus> FallbackChains { get; init; }
    public required IReadOnlyList<MediaOverlayLink> MediaOverlays { get; init; }
    public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }
    public required EffectiveRange EffectiveRange { get; init; }
    public string CoordinateBasis { get; init; } = "SVG user unit px; origin is top-left";
    public string TimeBasis { get; init; } = "UTC ISO 8601";
    public DateTimeOffset ComputedAt { get; init; }
}

public sealed record DecisionRecord { public required string StableId { get; init; } public required string EventStableId { get; init; } public required DecisionKind Kind { get; init; } public required string SubjectStableId { get; init; } public required string Summary { get; init; } public string? ConflictsWithDecisionStableId { get; init; } public string? Resolution { get; init; } public required DateTimeOffset CreatedAt { get; init; } public string TimeBasis { get; init; } = "UTC ISO 8601"; }
public sealed record CaseEvent { public required string StableId { get; init; } public required int Sequence { get; init; } public required string Type { get; init; } public required string Summary { get; init; } public required string Actor { get; init; } public required DateTimeOffset OccurredAt { get; init; } public string? ParentEventStableId { get; init; } public string? ImportMaterialSetHash { get; init; } public string? ParametersStableId { get; init; } public string? ResultStableId { get; init; } public string? DecisionStableId { get; init; } public string? BranchName { get; init; } public string TimeBasis { get; init; } = "UTC ISO 8601"; }
public sealed record ImportedEpub { public required string MaterialSetHash { get; init; } public required IReadOnlyList<ZipEntryMaterial> ZipEntries { get; init; } public ContainerMaterial? Container { get; init; } public required IReadOnlyList<PackageMaterial> Packages { get; init; } public required IReadOnlyList<Diagnostic> Diagnostics { get; init; } }
public sealed record CaseBundle
{
    public required string CaseId { get; init; }
    public required string Title { get; init; }
    public required List<ImportedEpub> Imports { get; init; }
    public required List<ReviewParameters> Parameters { get; init; }
    public required List<ReviewResult> Results { get; init; }
    public required List<DecisionRecord> Decisions { get; init; }
    public required List<CaseEvent> Events { get; init; }
    public int SchemaVersion { get; init; } = 1;
}

public static class StableHash
{
    public static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static string Of(string scope, params string[] parts)
    {
        var json = JsonSerializer.Serialize(new { scope, parts }, JsonOptions.Compact);
        return $"{scope}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant()[..20]}";
    }
}

public static class JsonOptions
{
    public static readonly JsonSerializerOptions Compact = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.Never };
    public static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web) { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never };
}
