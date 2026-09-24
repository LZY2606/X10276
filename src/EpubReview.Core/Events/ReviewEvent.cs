using System.Text.Json;

namespace EpubReview.Core.Events;

public enum EventKind
{
    ProjectCreated,
    EpubImported,
    AnalysisComputed,
    ParamsRevised,
    MaterialPatched,
    DecisionRecorded,
    BranchCreated,
    BundleImported,
    RecoveryRecorded
}

public enum DecisionVerdict
{
    Accepted,
    Rejected,
    Adjusted
}

public sealed record EventPayload(
    string Type,
    JsonElement Data,
    IReadOnlyList<string> InputSummaries,
    string? ParamsVersion,
    IReadOnlyList<string> ParentDependencies);

public sealed record ReviewEvent(
    string EventId,
    long Sequence,
    string BranchId,
    string? ParentEventId,
    string PrevHash,
    string EventHash,
    EventKind Kind,
    DateTimeOffset OccurredAt,
    string Actor,
    string Summary,
    JsonElement Payload,
    IReadOnlyList<string> InputSummaries,
    string? ParamsVersion,
    IReadOnlyList<string> ParentDependencies,
    string? Topic)
{
    public T? PayloadAs<T>() => Payload.Deserialize<T>(JsonOptions);

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
}

public sealed record DecisionBody(
    string FindingId,
    DecisionVerdict Verdict,
    string Rationale,
    string? AdjustedParamsJson,
    bool ConflictsWithPrior);

public sealed record ImportBody(
    string MaterialSetId,
    string ContentVersion,
    string ResultKind,
    IReadOnlyList<string> DiagnosticCodes);

public sealed record AnalysisBody(
    string AnalysisId,
    string DependencyVersion,
    string MaterialContentVersion,
    string ParamsVersionId,
    IReadOnlyList<string> ParentDependencyIds,
    IReadOnlyList<string> Topics,
    IReadOnlyList<string> RecomputedTopics);

public sealed record ParamsBody(
    int FromVersion,
    int ToVersion,
    string PatchJson,
    string Reason);

public sealed record MaterialPatchBody(
    string EntryName,
    string Action,
    string BeforeSha,
    string AfterSha,
    IReadOnlyList<string> AffectedTopics);

public sealed record BranchBody(
    string NewBranchId,
    string FromEventId,
    string Reason);

public sealed record RecoveryBody(
    string Operation,
    string DetectedAtSequence,
    string Evidence,
    string Outcome);
