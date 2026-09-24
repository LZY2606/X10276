using System.Text.Json;
using EpubReview.Core.Events;
using EpubReview.Core.Import;
using EpubReview.Core.Model;

namespace EpubReview.Core.Analysis;

public sealed record ProjectView(
    string ProjectId,
    string Title,
    string BranchId,
    IReadOnlyList<BranchInfoView> Branches,
    TimelinePointView Head,
    IReadOnlyList<MaterialInfoView> Materials,
    CurrentStateView State);

public sealed record BranchInfoView(string BranchId, string Name, long Seq, string HeadEventId);

public sealed record TimelinePointView(
    string EventId,
    long Seq,
    EventKind Kind,
    DateTimeOffset OccurredAt,
    string Summary,
    string EventHash,
    string? ParamsVersion,
    IReadOnlyList<string> ParentDependencies,
    string? AnalysisId,
    string? MaterialSetId,
    string? Topic);

public sealed record MaterialInfoView(
    string MaterialSetId,
    string ContentVersion,
    long OriginalZipLength,
    string OriginalZipSha256,
    int EntryCount,
    int PackageCount,
    IReadOnlyList<Diagnostic> ImportDiagnostics,
    string? ResultKind);

public sealed record CurrentStateView(
    string? MaterialSetId,
    string? AnalysisId,
    string? DependencyVersion,
    AnalysisParams Parameters,
    IReadOnlyList<string> Warnings);

public sealed record DecisionView(
    string DecisionId,
    string EventId,
    string FindingId,
    DecisionVerdict Verdict,
    string Rationale,
    bool ConflictsWithPrior,
    DateTimeOffset CreatedAt,
    string? AdjustedParamsVersion);
