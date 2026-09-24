using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EpubAudit.Domain;

public enum AuditStatus { Accepted, Rejected, BudgetExhausted, CorruptFormat, SemanticNoSolution }

public record SourceLocation(string EntryPath, int Line, int Column)
{
    public override string ToString() => $"{EntryPath}:{Line}:{Column}";
}

public record Diagnostic(
    string Code,
    AuditStatus Severity,
    string Message,
    SourceLocation Location,
    IReadOnlyList<string> CandidateValues,
    string? Unit = null)
{
    public string StableId => $"{Code}|{Location}|{Message}";
}

public sealed class ZipEntryMaterial
{
    public required string Path { get; init; }
    public required byte[] Bytes { get; init; }
    public required string Sha256 { get; init; }
    public long LengthBytes => Bytes.Length;
    public string CompressionBasis { get; init; } = "stored-raw-entry-bytes";
}

public sealed class MaterialBundle
{
    public string MaterialVersionId { get; init; } = "";
    public string ParentMaterialVersionId { get; init; } = "";
    public DateTimeOffset CapturedAt { get; init; }
    public string Origin { get; init; } = "local-epub-container";
    public List<ZipEntryMaterial> Entries { get; init; } = [];
    public string ContainerXml { get; init; } = "";
    public List<PackageMaterial> Packages { get; init; } = [];
}

public sealed class PackageMaterial
{
    public required string PackageId { get; init; }
    public required string EntryPath { get; init; }
    public required string OriginalXml { get; init; }
    public required string MediaType { get; init; }
}

public sealed class RuleParameters
{
    public int ParameterVersion { get; init; } = 1;
    public int MaxPackages { get; init; } = 2;
    public int MaxResourceBytes { get; init; } = 1_048_576;
    public int MaxGraphEdges { get; init; } = 512;
    public string PathCoordinateBasis { get; init; } = "container-relative-posix-path";
    public string TimeBasis { get; init; } = "media-overlay-clock";
    public string LayoutCoordinateSystem { get; init; } = "svg-user-units";
}

public abstract record AuditEvent(
    string EventId,
    string CaseId,
    string BranchId,
    string ParentEventId,
    DateTimeOffset OccurredAt,
    string Actor,
    string MaterialVersionId,
    int ParameterVersion,
    string EventType,
    string JsonPayload);

public sealed record AnalysisEvent : AuditEvent
{
    public AnalysisEvent(
        string eventId,
        string caseId,
        string branchId,
        string parentEventId,
        DateTimeOffset occurredAt,
        string materialVersionId,
        int parameterVersion,
        string resultId,
        AuditStatus status,
        string payload) :
        base(eventId, caseId, branchId, parentEventId, occurredAt, "server-analyzer", materialVersionId, parameterVersion, "analysis.computed", payload)
    {
        ResultId = resultId;
        Status = status;
    }

    public string ResultId { get; }
    public AuditStatus Status { get; }
}

public sealed record DecisionEvent : AuditEvent
{
    public DecisionEvent(
        string eventId,
        string caseId,
        string branchId,
        string parentEventId,
        DateTimeOffset occurredAt,
        string actor,
        string materialVersionId,
        int parameterVersion,
        string decision,
        string subjectStableId,
        bool adopted,
        string rationale,
        string payload) :
        base(eventId, caseId, branchId, parentEventId, occurredAt, actor, materialVersionId, parameterVersion, adopted ? "decision.accepted" : "decision.rejected", payload)
    {
        Decision = decision;
        SubjectStableId = subjectStableId;
        Adopted = adopted;
        Rationale = rationale;
    }

    public string Decision { get; }
    public string SubjectStableId { get; }
    public bool Adopted { get; }
    public string Rationale { get; }
}

public sealed record ParameterRevisionEvent : AuditEvent
{
    public ParameterRevisionEvent(
        string eventId,
        string caseId,
        string branchId,
        string parentEventId,
        DateTimeOffset occurredAt,
        int parameterVersion,
        string materialVersionId,
        string payload) :
        base(eventId, caseId, branchId, parentEventId, occurredAt, "reviewer", materialVersionId, parameterVersion, "parameters.revised", payload)
    {
    }
}

public sealed record ResourceNode
{
    public required string StableId { get; init; }
    public required string PackageId { get; init; }
    public required string ManifestId { get; init; }
    public required string OriginalHref { get; init; }
    public required string NormalizedPath { get; init; }
    public required string MediaType { get; init; }
    public long SizeBytes { get; init; }
    public string? FallbackManifestId { get; init; }
    public string? MediaOverlayManifestId { get; init; }
    public bool InSpine { get; init; }
}

public sealed record SpineItem(string StableId, string PackageId, string IdRef, int Ordinal, bool Linear, string ResourceStableId);

public sealed record NavigationNode(string StableId, string PackageId, string ResourceStableId, string Text, string OriginalHref, string? Fragment, int Depth, int Ordinal);

public sealed record GraphEdge(
    string StableId,
    string SourceStableId,
    string TargetStableId,
    string Kind,
    string OriginalReference,
    string? Fragment,
    bool Resolved,
    IReadOnlyList<string> Candidates);

public sealed record FallbackCandidate(string StableId, string SourceStableId, string CandidateStableId, int Distance, bool Reachable, bool PartOfCycle);

public sealed record OverlayInterval(
    string StableId,
    string OverlayStableId,
    string TargetStableId,
    string RawBegin,
    string RawEnd,
    double? BeginSeconds,
    double? EndSeconds,
    string TimeBasis,
    string? FailureReason);

public sealed record UnknownMetadata(string StableId, string PackageId, string OriginalXml, string Namespace, string LocalName);

public sealed record AnalysisResult
{
    public required string ResultId { get; init; }
    public required string CaseId { get; init; }
    public required string BranchId { get; init; }
    public required string ParentEventId { get; init; }
    public required string MaterialVersionId { get; init; }
    public required RuleParameters Parameters { get; init; }
    public required AuditStatus Status { get; init; }
    public required string InputSummary { get; init; }
    public required List<ResourceNode> Resources { get; init; }
    public required List<SpineItem> Spine { get; init; }
    public required List<NavigationNode> Navigation { get; init; }
    public required List<GraphEdge> Edges { get; init; }
    public required List<FallbackCandidate> FallbackCandidates { get; init; }
    public required List<OverlayInterval> OverlayIntervals { get; init; }
    public required List<UnknownMetadata> UnknownMetadata { get; init; }
    public required List<Diagnostic> Diagnostics { get; init; }
    public required IReadOnlyDictionary<string, string> AcceptedDecisions { get; init; }
    public required IReadOnlySet<string> RejectedSubjects { get; init; }
}

public static class StableHashing
{
    public static string Sha256Hex(byte[] bytes)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string Id(string prefix, params string?[] parts)
    {
        var joined = string.Join('\u001f', parts);
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(joined));
        return prefix + "_" + Convert.ToHexString(bytes[..12]).ToLowerInvariant();
    }
}

public static class JsonOptions
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() }
    };
}
