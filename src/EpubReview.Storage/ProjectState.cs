using System.Text.Json;
using EpubReview.Core.Analysis;
using EpubReview.Core.Events;
using EpubReview.Core.Model;

namespace EpubReview.Storage;

internal sealed class ProjectState
{
    public EpubMaterialSet? Material { get; set; }
    public AnalysisParams Parameters { get; set; } = AnalysisParams.V1;
    public List<AnalysisResult> Results { get; } = new();
    public List<DecisionView> Decisions { get; } = new();
    public List<TimelinePointView> Timeline { get; } = new();
    public List<MaterialInfoView> MaterialInfos { get; } = new();
}

public sealed record HistoricalView(
    ProjectView View,
    AnalysisResult? Analysis,
    EpubMaterialSet? Material,
    IReadOnlyList<string> Diagnostics,
    string? FailureKind);

public sealed partial class ReviewApplication
{
    private static readonly JsonSerializerOptions StateJson =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private ProjectState BuildState(string projectId, string branchId,
        string? upToEventId = null)
    {
        var state = new ProjectState();
        var events = _repo.ReadEvents(projectId, branchId);

        foreach (var e in events)
        {
            ApplyEvent(projectId, branchId, e, state);
            state.Timeline.Add(ToTimeline(e, state));
            if (upToEventId is not null && e.EventId == upToEventId) break;
        }

        return state;
    }

    private void ApplyEvent(string projectId, string branchId,
        ReviewEvent e, ProjectState state)
    {
        switch (e.Kind)
        {
            case EventKind.EpubImported:
            {
                var body = e.PayloadAs<ImportBody>(StateJson);
                if (body?.MaterialSetId is { } mid)
                {
                    var blob = _repo.GetMaterial(projectId, mid);
                    if (blob is not null)
                    {
                        var imported = new EpubImporter().Import(blob.ZipBytes, state.Parameters, e.OccurredAt);
                        EpubMaterialSet material = imported switch
                        {
                            ImportResult.Success s => s.Material,
                            ImportResult.SemanticUnresolvable sem => sem.Material,
                            _ => state.Material!
                        };
                        state.Material = material;
                        state.MaterialInfos.Add(new MaterialInfoView(
                            material.MaterialSetId, material.ContentVersion,
                            material.OriginalZipLength, material.OriginalZipSha256,
                            material.Entries.Count, material.Packages.Count,
                            material.ImportDiagnostics, body.ResultKind));
                    }
                }
                break;
            }
            case EventKind.ParamsRevised:
            {
                var body = e.PayloadAs<ParamsBody>(StateJson);
                if (body?.PatchJson is { } json)
                {
                    state.Parameters = JsonSerializer.Deserialize<AnalysisParams>(json, StateJson)!;
                }
                break;
            }
            case EventKind.AnalysisComputed:
            {
                var body = e.PayloadAs<AnalysisBody>(StateJson);
                if (body?.AnalysisId is { } aid)
                {
                    var blob = _repo.GetAnalysis(projectId, aid);
                    if (blob is not null)
                    {
                        var result = JsonSerializer.Deserialize<AnalysisResult>(blob.Json, StateJson)!;
                        var withDecisions = ApplyDecisionsToFindings(result, state.Decisions);
                        var existing = state.Results.FindIndex(r => r.AnalysisId == aid);
                        if (existing >= 0) state.Results[existing] = withDecisions;
                        else state.Results.Add(withDecisions);
                    }
                }
                break;
            }
            case EventKind.DecisionRecorded:
            {
                var body = e.PayloadAs<DecisionBody>(StateJson)!;
                var decisionId = "dec-" + Hashing.StableId(
                    "decision", projectId, branchId, body.FindingId,
                    body.Verdict.ToString(), body.Rationale, state.Decisions.Count);
                state.Decisions.Add(new DecisionView(
                    decisionId, e.EventId, body.FindingId, body.Verdict,
                    body.Rationale, body.ConflictsWithPrior, e.OccurredAt,
                    body.AdjustedParamsJson));
                break;
            }
        }
    }

    private static AnalysisResult ApplyDecisionsToFindings(AnalysisResult result,
        IReadOnlyList<DecisionView> decisions)
    {
        if (decisions.Count == 0) return result;
        var byFinding = decisions
            .GroupBy(d => d.FindingId)
            .ToDictionary(g => g.Key, g => g.OrderBy(d => d.CreatedAt).Last());

        var findings = result.Findings.Select(f =>
        {
            if (!byFinding.TryGetValue(f.FindingId, out var d)) return f;
            var status = d.Verdict switch
            {
                DecisionVerdict.Accepted => FindingStatus.Accepted,
                DecisionVerdict.Rejected => FindingStatus.Rejected,
                DecisionVerdict.Adjusted => FindingStatus.Adjusted,
                _ => FindingStatus.Open
            };
            return f with { Status = status, DecisionId = d.EventId };
        }).ToArray();

        return result with { Findings = findings };
    }

    private TimelinePointView ToTimeline(ReviewEvent e, ProjectState state)
    {
        string? analysisId = null;
        string? materialSetId = null;
        if (e.Kind == EventKind.AnalysisComputed)
            analysisId = e.PayloadAs<AnalysisBody>(StateJson)?.AnalysisId;
        if (e.Kind is EventKind.EpubImported or EventKind.MaterialPatched)
            materialSetId = e.PayloadAs<ImportBody>(StateJson)?.MaterialSetId
                            ?? e.PayloadAs<MaterialPatchBody>(StateJson)?.AfterSha;

        return new TimelinePointView(
            e.EventId, e.Sequence, e.Kind, e.OccurredAt, e.Summary,
            e.EventHash, e.ParamsVersion, e.ParentDependencies,
            analysisId, materialSetId, e.Topic);
    }

    public ProjectView GetProjectView(string projectId, string branchId = "main")
    {
        var state = BuildState(projectId, branchId);
        var head = state.Timeline.LastOrDefault()
                   ?? throw new InvalidOperationException("project has no events");
        var branches = _repo.ListBranches(projectId)
            .Select(b => new BranchInfoView(b.BranchId, b.Name, b.Seq,
                b.Seq >= 0 ? "evt-seq-" + b.Seq : ""))
            .ToArray();

        var latest = state.Results.LastOrDefault();
        return new ProjectView(
            projectId,
            _repo.GetProjectMeta(projectId).Title ?? projectId,
            branchId, branches, head,
            state.MaterialInfos.ToArray(),
            new CurrentStateView(
                state.Material?.MaterialSetId,
                latest?.AnalysisId,
                latest?.DependencyVersion,
                state.Parameters,
                Array.Empty<string>()));
    }

    public HistoricalView GetHistoricalView(string projectId, string branchId,
        string eventId)
    {
        var state = BuildState(projectId, branchId, eventId);
        var point = state.Timeline.LastOrDefault(x => x.EventId == eventId)
                    ?? throw new InvalidOperationException("event not found on branch");

        var analysesAt = new List<AnalysisResult>();
        foreach (var e in _repo.ReadEvents(projectId, branchId)
                     .TakeWhile(x => x.Sequence <= point.Seq))
        {
            if (e.Kind != EventKind.AnalysisComputed) continue;
            var aid = e.PayloadAs<AnalysisBody>(StateJson)?.AnalysisId;
            if (aid is null) continue;
            var blob = _repo.GetAnalysis(projectId, aid);
            if (blob is not null)
            {
                analysesAt.Add(ApplyDecisionsToFindings(
                    JsonSerializer.Deserialize<AnalysisResult>(blob.Json, StateJson)!,
                    state.Decisions.Where(d => d.CreatedAt <= e.OccurredAt).ToArray()));
            }
        }

        var latest = analysesAt.LastOrDefault();
        var view = new ProjectView(
            projectId,
            _repo.GetProjectMeta(projectId).Title ?? projectId,
            branchId,
            _repo.ListBranches(projectId)
                .Select(b => new BranchInfoView(b.BranchId, b.Name, b.Seq,
                    b.Seq >= 0 ? "evt-seq-" + b.Seq : ""))
                .ToArray(),
            point,
            state.MaterialInfos.ToArray(),
            new CurrentStateView(
                state.Material?.MaterialSetId, latest?.AnalysisId,
                latest?.DependencyVersion, state.Parameters, Array.Empty<string>()));

        string? failureKind = null;
        var diagnostics = new List<string>();
        if (state.Material is not null)
        {
            diagnostics = state.Material.ImportDiagnostics
                .Select(d => d.Code + ": " + d.Message).ToList();
        }

        return new HistoricalView(view, latest, state.Material, diagnostics, failureKind);
    }

    public AnalysisResult? GetLatestAnalysis(string projectId, string branchId = "main")
    {
        var state = BuildState(projectId, branchId);
        return state.Results.LastOrDefault();
    }

    public EpubMaterialSet? GetLatestMaterial(string projectId, string branchId = "main")
    {
        return BuildState(projectId, branchId).Material;
    }

    public IReadOnlyList<DecisionView> GetDecisions(string projectId, string branchId = "main")
    {
        return BuildState(projectId, branchId).Decisions;
    }

    public IReadOnlyList<TimelinePointView> GetTimeline(string projectId, string branchId = "main")
    {
        return BuildState(projectId, branchId).Timeline;
    }
}
