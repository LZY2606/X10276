using System.Text.Json;
using EpubReview.Core;
using EpubReview.Core.Analysis;
using EpubReview.Core.Events;
using EpubReview.Core.Import;
using EpubReview.Core.Model;

namespace EpubReview.Storage;

public sealed partial class ReviewApplication
{
    private readonly ReviewRepository _repo;
    private static readonly JsonSerializerOptions JsonOpts =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public ReviewApplication(ReviewRepository repo)
    {
        _repo = repo;
        repo.InitSchema();
    }

    public ReviewRepository Repository => _repo;

    public string CreateProject(string title, DateTimeOffset? now = null)
    {
        var stamp = now ?? DateTimeOffset.UtcNow;
        var id = "prj-" + Hashing.StableId("project", title, stamp.ToString("O"));
        _repo.CreateProject(id, title, stamp);
        _repo.AppendEvent(id, "main", EventKind.ProjectCreated,
            "reviewer", "Project created",
            new { title }, Array.Empty<string>(), null,
            Array.Empty<string>(), null, stamp);
        return id;
    }

    public ImportResult ImportEpub(string projectId, byte[] zipBytes,
        AnalysisParams? parameters = null, string branchId = "main",
        DateTimeOffset? now = null)
    {
        var stamp = now ?? DateTimeOffset.UtcNow;
        var p = parameters ?? AnalysisParams.V1;
        var importer = new EpubImporter();
        var result = importer.Import(zipBytes, p, stamp);

        switch (result)
        {
            case ImportResult.Success success:
                PersistImport(projectId, branchId, success.Material, "Success",
                    success.Material.ImportDiagnostics, p, stamp);
                break;
            case ImportResult.SemanticUnresolvable sem:
                PersistImport(projectId, branchId, sem.Material, "SemanticUnresolvable",
                    sem.Diagnostics, p, stamp, sem.UnresolvedObjects);
                break;
            case ImportResult.BudgetExhausted budget:
                _repo.AppendEvent(projectId, branchId, EventKind.EpubImported,
                    "reviewer", "Import rejected: resource budget exhausted",
                    new
                    {
                        resultKind = "BudgetExhausted",
                        limitName = budget.LimitName,
                        limitValue = budget.LimitValue,
                        limitUnit = budget.LimitUnit.Symbol,
                        observedValue = budget.ObservedValue,
                        diagnosticCodes = budget.Diagnostics.Select(d => d.Code).ToArray()
                    },
                    new[] { "zip-sha:" + Hashing.Sha256Hex(zipBytes) },
                    p.ParamsVersionId, Array.Empty<string>(), "Budget", stamp);
                break;
            case ImportResult.FormatCorrupt corrupt:
                _repo.AppendEvent(projectId, branchId, EventKind.EpubImported,
                    "reviewer", "Import rejected: format corrupt",
                    new
                    {
                        resultKind = "FormatCorrupt",
                        reason = corrupt.Reason,
                        byteOffset = corrupt.ByteOffset,
                        location = corrupt.Location?.ToString(),
                        diagnosticCodes = corrupt.Diagnostics.Select(d => d.Code).ToArray()
                    },
                    new[] { "zip-sha:" + Hashing.Sha256Hex(zipBytes) },
                    p.ParamsVersionId, Array.Empty<string>(), "ZipStructure", stamp);
                break;
        }

        return result;
    }

    private void PersistImport(string projectId, string branchId, EpubMaterialSet material,
        string resultKind, IReadOnlyList<Diagnostic> diagnostics, AnalysisParams p,
        DateTimeOffset stamp, IReadOnlyList<string>? unresolved = null)
    {
        using var conn = _repo.Open();
        using var tx = conn.BeginTransaction();
        _repo.SaveMaterial(projectId, material.MaterialSetId,
            material.ContentVersion, material.OriginalZipBytes, stamp, conn, tx);
        _repo.AppendEvent(projectId, branchId, EventKind.EpubImported,
            "reviewer",
            resultKind == "Success" ? "EPUB imported" : "EPUB imported with semantic gaps",
            new ImportBody(material.MaterialSetId, material.ContentVersion, resultKind,
                diagnostics.Select(d => d.Code).ToArray()),
            new[]
            {
                "zip-sha:" + material.OriginalZipSha256,
                "entries:" + material.Entries.Count,
                "packages:" + material.Packages.Count
            },
            p.ParamsVersionId, Array.Empty<string>(), null, stamp, conn, tx);
        tx.Commit();
    }

    public AnalysisResult ComputeAnalysis(string projectId, string branchId = "main",
        AnalysisParams? parametersOverride = null, DateTimeOffset? now = null)
    {
        var stamp = now ?? DateTimeOffset.UtcNow;
        var state = BuildState(projectId, branchId);
        if (state.Material is null)
            throw new InvalidOperationException("no material imported on branch " + branchId);

        var p = parametersOverride ?? state.Parameters;
        var parents = state.Results.Select(r => r.DependencyVersion).ToArray();
        var engine = new ReviewEngine();
        var result = engine.Analyze(state.Material, p, parents, null, stamp);

        using var conn = _repo.Open();
        using var tx = conn.BeginTransaction();
        _repo.SaveAnalysis(projectId, branchId, result.AnalysisId,
            result.DependencyVersion, result.MaterialSetId,
            result.ParamsVersionId, JsonSerializer.Serialize(result, JsonOpts),
            stamp, conn, tx);

        var body = new AnalysisBody(
            result.AnalysisId, result.DependencyVersion,
            result.MaterialContentVersion, result.ParamsVersionId,
            result.ParentDependencyIds, result.Topics, result.Topics);

        _repo.AppendEvent(projectId, branchId, EventKind.AnalysisComputed,
            "reviewer",
            $"Analysis computed: {result.Findings.Count} finding(s), {result.Graph.Edges.Count} edge(s)",
            body,
            new[]
            {
                "material:" + result.MaterialContentVersion,
                "params:" + result.ParamsVersionId
            },
            p.ParamsVersionId, parents, null, stamp, conn, tx);
        tx.Commit();
        return result;
    }

    public AnalysisResult ReviseParameters(string projectId,
        Action<ParamsBuilder> patch, string reason, string branchId = "main",
        DateTimeOffset? now = null)
    {
        var stamp = now ?? DateTimeOffset.UtcNow;
        var state = BuildState(projectId, branchId);
        var current = state.Parameters;
        var builder = new ParamsBuilder(current);
        patch(builder);
        var revised = builder.Build(current.Version + 1);

        _repo.AppendEvent(projectId, branchId, EventKind.ParamsRevised,
            "reviewer", "Parameter revision: " + reason,
            new ParamsBody(current.Version, revised.Version,
                JsonSerializer.Serialize(revised, JsonOpts), reason),
            new[] { "from:" + current.ParamsVersionId, "to:" + revised.ParamsVersionId },
            revised.ParamsVersionId,
            state.Results.Select(r => r.DependencyVersion).ToArray(),
            null, stamp);

        return ComputeAnalysis(projectId, branchId, revised, stamp);
    }

    public AnalysisResult PatchMaterialEntry(string projectId, string entryName,
        byte[] newBytes, string action, string branchId = "main",
        DateTimeOffset? now = null)
    {
        var stamp = now ?? DateTimeOffset.UtcNow;
        var state = BuildState(projectId, branchId);
        if (state.Material is null) throw new InvalidOperationException("no material");

        var old = state.Material;
        var oldEntry = old.Entries.FirstOrDefault(e => e.Name == entryName);
        var patchedZip = ZipPatcher.ReplaceEntry(old.OriginalZipBytes, entryName, newBytes, action);
        var importer = new EpubImporter();
        var imported = importer.Import(patchedZip, state.Parameters, stamp);
        if (imported is not ImportResult.Success s)
            throw new InvalidOperationException("patched material failed import: " +
                                                imported.GetType().Name);

        var material = s.Material;
        var affected = AffectedTopics(entryName);
        using (var conn = _repo.Open())
        using (var tx = conn.BeginTransaction())
        {
            _repo.SaveMaterial(projectId, material.MaterialSetId,
                material.ContentVersion, material.OriginalZipBytes, stamp, conn, tx);
            _repo.AppendEvent(projectId, branchId, EventKind.MaterialPatched,
                "reviewer",
                $"Local source change: {action} '{entryName}'",
                new MaterialPatchBody(entryName, action,
                    oldEntry?.Sha256 ?? "absent",
                    material.Entries.FirstOrDefault(e => e.Name == entryName)?.Sha256 ?? "absent",
                    affected),
                new[]
                {
                    "before:" + old.ContentVersion,
                    "after:" + material.ContentVersion
                },
                state.Parameters.ParamsVersionId,
                state.Results.Select(r => r.DependencyVersion).ToArray(),
                string.Join(",", affected), stamp, conn, tx);
            tx.Commit();
        }

        return ComputeAnalysis(projectId, branchId, state.Parameters, stamp);
    }

    private static IReadOnlyList<string> AffectedTopics(string entryName)
    {
        if (entryName.EndsWith(".opf"))
            return new[] { "Manifest", "Spine", "Fallback", "MediaOverlay", "Metadata" };
        if (entryName.EndsWith(".smil")) return new[] { "MediaOverlay", "Fragment" };
        if (entryName.EndsWith(".xhtml") || entryName.EndsWith(".html") || entryName.EndsWith(".nav"))
            return new[] { "Fragment", "Resource" };
        return new[] { "Resource" };
    }

    public DecisionView RecordDecision(string projectId, string findingId,
        DecisionVerdict verdict, string rationale, string? adjustedParamsJson = null,
        string branchId = "main", DateTimeOffset? now = null)
    {
        var stamp = now ?? DateTimeOffset.UtcNow;
        var state = BuildState(projectId, branchId);
        var prior = state.Decisions
            .Where(d => d.FindingId == findingId && d.Verdict != verdict)
            .ToList();
        bool conflict = prior.Count > 0;
        var decisionId = "dec-" + Hashing.StableId(
            "decision", projectId, branchId, findingId, verdict.ToString(),
            rationale, state.Decisions.Count);

        string? adjustedVersion = null;
        if (adjustedParamsJson is not null)
        {
            var adjusted = JsonSerializer.Deserialize<AnalysisParams>(adjustedParamsJson, JsonOpts)!;
            adjustedVersion = adjusted.ParamsVersionId;
        }

        using var conn = _repo.Open();
        using var tx = conn.BeginTransaction();
        var (evt, _) = _repo.AppendEvent(projectId, branchId,
            EventKind.DecisionRecorded, "reviewer",
            $"{verdict}: {findingId}",
            new DecisionBody(findingId, verdict, rationale, adjustedParamsJson, conflict),
            new[] { "finding:" + findingId },
            state.Parameters.ParamsVersionId,
            state.Results.Select(r => r.DependencyVersion).ToArray(),
            null, stamp, conn, tx);
        _repo.SaveDecision(projectId, branchId, decisionId, evt.EventId, findingId,
            verdict.ToString(), rationale, adjustedParamsJson, conflict, stamp, conn, tx);
        tx.Commit();

        return new DecisionView(decisionId, evt.EventId, findingId, verdict,
            rationale, conflict, stamp, adjustedVersion);
    }

    public void CreateBranch(string projectId, string newBranchId, string reason,
        string fromBranchId = "main", string? fromEventId = null,
        DateTimeOffset? now = null)
    {
        var stamp = now ?? DateTimeOffset.UtcNow;
        var events = _repo.ReadEvents(projectId, fromBranchId);
        var forkId = fromEventId ?? events.Last().EventId;
        _repo.CreateBranch(projectId, newBranchId, newBranchId,
            fromBranchId, forkId, stamp);
        _repo.AppendEvent(projectId, newBranchId, EventKind.BranchCreated,
            "reviewer", "Branch created: " + reason,
            new BranchBody(newBranchId, forkId, reason),
            new[] { "fork:" + forkId }, null,
            new[] { forkId }, null, stamp);
    }
}
