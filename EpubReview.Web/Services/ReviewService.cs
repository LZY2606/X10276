using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using EpubReview.Domain;

namespace EpubReview.Web.Services;

public sealed class ReviewService
{
    private static readonly JsonSerializerOptions Options = CreateJsonOptions();
    private readonly ReviewRepository _repository;
    private readonly EpubAnalyzer _analyzer = new();

    public ReviewService(ReviewRepository repository) { _repository = repository; }

    public static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public void EnsureSeeded()
    {
        if (_repository.CaseExists(SampleCase.CaseId)) return;
        var initialArchive = SampleCase.BuildArchive(SampleRevision.Initial);
        var revisedArchive = SampleCase.BuildArchive(SampleRevision.LocalSourceChange);
        var initialParameters = new AnalysisParameters();
        var revisedParameters = initialParameters with { MaxArchiveBytes = 2 * 1024 * 1024, CoordinateSpace = "SVG user units; origin=top-left; x=right; y=down" };
        var state = new SeedingState { Sequence = 1, Parameters = initialParameters };

        var firstEventId = EventId(0, "case-created");
        var firstPayload = new EventPayload("不可变案例创建：离线 EPUB 阅读序谱审阅开始", null, initialParameters, null, null, null, SampleCase.CaseId);
        _repository.InTransaction(SampleCase.CaseId, (c, t) =>
        {
            ReviewRepository.InsertCase(c, t, SampleCase.CaseId, SampleCase.Title, firstEventId, SystemClock.UtcNow());
            ReviewRepository.InsertEvent(c, t, SampleCase.CaseId, new ProjectEvent(firstEventId, state.Sequence, "case-created", SystemClock.UtcNow(), initialParameters.Version, null, null, null, SerializeJson(firstPayload), "preserved"));
            return true;
        });

        ImportArchive("初始导入：保留 OCF 容器和每个 ZIP 条目的原始字节", initialArchive, null, SampleRevision.Initial.ToString(), state);
        Analyze("基线复核：package manifest、spine、navigation、media-overlay、fallback 与资源图", state);
        ReviseParameters("参数修订：收紧资源预算并显式记录 SVG 坐标基准", revisedParameters, state);
        var parentArchiveId = state.ArchiveMaterialId;
        ImportArchive("局部源数据改动：仅修订 chapter1 的合法候选引用", revisedArchive, parentArchiveId, SampleRevision.LocalSourceChange.ToString(), state);
        Analyze("局部修订复核：父级依赖保持不变，只重算受影响资源边", state);
        Decide(new ReviewDecision(EventId(state.Sequence, "decision-recorded"), "MISSING_REFERENCED_RESOURCE", DecisionAction.Rejected, "审阅者先否定缺失图片问题，要求修订草案补证。", SystemClock.UtcNow(), null), state);
        Decide(new ReviewDecision(EventId(state.Sequence, "decision-recorded"), "MISSING_REFERENCED_RESOURCE", DecisionAction.Accepted, "另一位审阅者接受同一问题，形成互相冲突的人工决定。", SystemClock.UtcNow(), state.Events[^1].EventId), state);
    }

    public IReadOnlyList<EventRecord> Timeline(string caseId) => _repository.GetEvents(caseId);

    public AnalysisResult ResultAt(string caseId, string eventId)
    {
        var events = _repository.GetEvents(caseId);
        var selected = events.FirstOrDefault(e => e.EventId == eventId) ?? throw new InvalidOperationException("Unknown event identity.");
        var payload = System.Text.Json.JsonSerializer.Deserialize<EventPayload>(selected.PayloadJson, Options) ?? throw new InvalidDataException("Event payload is incomplete.");
        if (payload.Analysis is not null) return payload.Analysis;
        var prior = events.TakeWhile(e => e.EventId != selected.EventId).LastOrDefault(e => e.DependencyVersion is not null);
        if (prior is not null) return System.Text.Json.JsonSerializer.Deserialize<EventPayload>(prior.PayloadJson, Options)!.Analysis!;
        throw new InvalidOperationException("This event records immutable input before the first calculation; advance to the next event to inspect derived results.");
    }

    public ProjectExport Export(string caseId) => _repository.ExportCase(caseId);

    public string ImportExternal(ProjectExport export)
    {
        if (export.Schema != "epub-review/offline/1") throw new InvalidDataException("Unsupported project schema.");
        if (export.Events.Count == 0 || export.Materials.Count == 0) throw new InvalidDataException("Project must contain events and immutable materials.");
        return _repository.ImportCase(export);
    }


    public (string DependencyVersion, string ResultEventId) RecordDecision(string caseId, string subjectId, DecisionAction action, string rationale)
    {
        var events = _repository.GetEvents(caseId);
        var materials = _repository.GetMaterials(caseId);
        var currentPayload = LatestAnalysisPayload(events);
        var current = currentPayload.Analysis!;
        var archive = CurrentArchive(caseId, current.InputSha256, materials);
        var sequence = events.Max(e => e.Sequence);
        var decisions = events.Select(e => System.Text.Json.JsonSerializer.Deserialize<EventPayload>(e.PayloadJson, Options)).Where(p => p?.Decision is not null).Select(p => p!.Decision!).ToList();
        var decision = new ReviewDecision(EventId(sequence, "decision-recorded"), subjectId, action, rationale, SystemClock.UtcNow(), events.LastOrDefault(e => e.PayloadJson.Contains(subjectId, StringComparison.Ordinal))?.EventId);
        decisions.Add(decision);
        var payload = new EventPayload(decision.Rationale, null, currentPayload.Parameters, decision, currentPayload.MaterialId, currentPayload.ParentMaterialId, null);
        var decisionEvent = new ProjectEvent(decision.EventId, sequence + 1, "decision-recorded", SystemClock.UtcNow(), current.ParametersVersion, current.DependencyVersion, null, null, SerializeJson(payload), action.ToString());
        var result = _analyzer.Analyze(new(archive, currentPayload.Parameters!, current.DependencyVersion, decisions));
        var resultPayload = new EventPayload($"决定“{action}”后的依赖版本", result, currentPayload.Parameters, null, currentPayload.MaterialId, currentPayload.ParentMaterialId, null);
        var resultEvent = new ProjectEvent(EventId(sequence + 1, "analysis-completed"), sequence + 2, "analysis-completed", SystemClock.UtcNow(), result.ParametersVersion, result.ParentDependencyVersion, result.InputSha256, result.DependencyVersion, SerializeJson(resultPayload), result.Outcome.ToString());
        _repository.InTransaction(caseId, (c, t) =>
        {
            ReviewRepository.InsertEvent(c, t, caseId, decisionEvent);
            ReviewRepository.InsertEvent(c, t, caseId, resultEvent);
            return true;
        });
        return (result.DependencyVersion, resultEvent.EventId);
    }

    public (string DependencyVersion, string ResultEventId) RecordParameters(string caseId, long maxArchiveBytes, int maxZipEntries, bool requireFragmentTargets, string coordinateSpace)
    {
        var events = _repository.GetEvents(caseId);
        var materials = _repository.GetMaterials(caseId);
        var currentPayload = LatestAnalysisPayload(events);
        var current = currentPayload.Analysis!;
        var archive = CurrentArchive(caseId, current.InputSha256, materials);
        var sequence = events.Max(e => e.Sequence);
        var decisions = events.Select(e => System.Text.Json.JsonSerializer.Deserialize<EventPayload>(e.PayloadJson, Options)).Where(p => p?.Decision is not null).Select(p => p!.Decision!).ToList();
        var parameters = new AnalysisParameters(maxArchiveBytes, maxZipEntries, true, requireFragmentTargets, "id,xml:id", coordinateSpace, "ISO-8601 UTC", "byte");
        var payload = new EventPayload("页面参数调整：从可见审阅路径提交服务端规则版本", null, parameters, null, currentPayload.MaterialId, currentPayload.ParentMaterialId, null);
        var parameterEvent = new ProjectEvent(EventId(sequence, "parameters-revised"), sequence + 1, "parameters-revised", SystemClock.UtcNow(), parameters.Version, current.DependencyVersion, null, null, SerializeJson(payload), "active");
        var result = _analyzer.Analyze(new(archive, parameters, current.DependencyVersion, decisions));
        var resultPayload = new EventPayload("参数调整后的派生结果", result, parameters, null, currentPayload.MaterialId, currentPayload.ParentMaterialId, null);
        var resultEvent = new ProjectEvent(EventId(sequence + 1, "analysis-completed"), sequence + 2, "analysis-completed", SystemClock.UtcNow(), result.ParametersVersion, result.ParentDependencyVersion, result.InputSha256, result.DependencyVersion, SerializeJson(resultPayload), result.Outcome.ToString());
        _repository.InTransaction(caseId, (c, t) =>
        {
            ReviewRepository.InsertEvent(c, t, caseId, parameterEvent);
            ReviewRepository.InsertEvent(c, t, caseId, resultEvent);
            return true;
        });
        return (result.DependencyVersion, resultEvent.EventId);
    }

    private EventPayload LatestAnalysisPayload(IReadOnlyList<EventRecord> events)
    {
        var latest = events.Last(e => e.DependencyVersion is not null);
        return System.Text.Json.JsonSerializer.Deserialize<EventPayload>(latest.PayloadJson, Options) ?? throw new InvalidOperationException("Latest analysis payload is incomplete.");
    }

    private byte[] CurrentArchive(string caseId, string inputSha, IReadOnlyList<StoredMaterial> materials)
    {
        var material = materials.Last(m => m.Role == "ocf-archive" && m.Sha256 == inputSha);
        return material.Bytes;
    }

    private void ImportArchive(string message, byte[] archive, string? parentMaterialId, string revision, SeedingState state)
    {
        var inputSha = StableHashes.Sha256(archive);
        var materialId = StableHashes.StableId("material:archive", $"archive|{inputSha}");
        state.Archive = archive;
        state.ArchiveMaterialId = materialId;
        state.ParentMaterialId = parentMaterialId;
        var entries = ParseEntryMaterials(archive);
        var payload = new EventPayload(message, null, state.Parameters, null, materialId, parentMaterialId, revision);
        var eventId = EventId(state.Sequence, "archive-imported");
        state.Sequence++;
        var archiveMaterial = new ProjectMaterial(materialId, "ocf-archive", "input.epub", null, "application/epub+zip", archive.Length, inputSha, parentMaterialId, eventId, archive);
        var entryMaterials = entries.Select(e => new ProjectMaterial(e.MaterialId, "zip-entry", e.RawPath, e.NormalizedPath, e.MediaType, e.ByteLength, e.Sha256, materialId, eventId, e.Bytes)).ToArray();
        var projectEvent = new ProjectEvent(eventId, state.Sequence, "archive-imported", SystemClock.UtcNow(), state.Parameters.Version, state.LastDependencyVersion, inputSha, null, SerializeJson(payload), "preserved");
        CommitEvent(projectEvent, entryMaterials.Prepend(archiveMaterial).ToArray(), state);
    }

    private void Analyze(string message, SeedingState state)
    {
        var result = _analyzer.Analyze(new(state.Archive!, state.Parameters, state.LastDependencyVersion, state.Decisions));
        state.LastDependencyVersion = result.DependencyVersion;
        var payload = new EventPayload(message, result, state.Parameters, null, state.ArchiveMaterialId, state.ParentMaterialId, null);
        var eventId = EventId(state.Sequence, "analysis-completed");
        state.Sequence++;
        var projectEvent = new ProjectEvent(eventId, state.Sequence, "analysis-completed", SystemClock.UtcNow(), state.Parameters.Version, result.ParentDependencyVersion, result.InputSha256, result.DependencyVersion, SerializeJson(payload), result.Outcome.ToString());
        CommitEvent(projectEvent, [], state);
    }

    private void ReviseParameters(string message, AnalysisParameters parameters, SeedingState state)
    {
        state.Parameters = parameters;
        var eventId = EventId(state.Sequence, "parameters-revised");
        state.Sequence++;
        var payload = new EventPayload(message, null, parameters, null, state.ArchiveMaterialId, state.ParentMaterialId, null);
        var projectEvent = new ProjectEvent(eventId, state.Sequence, "parameters-revised", SystemClock.UtcNow(), parameters.Version, state.LastDependencyVersion, null, null, SerializeJson(payload), "active");
        CommitEvent(projectEvent, [], state);
        Analyze($"{message}后的派生结果", state);
    }

    private void Decide(ReviewDecision decision, SeedingState state)
    {
        state.Decisions.Add(decision);
        var payload = new EventPayload(decision.Rationale, null, state.Parameters, decision, state.ArchiveMaterialId, state.ParentMaterialId, null);
        var eventId = EventId(state.Sequence, "decision-recorded");
        state.Sequence++;
        payload = payload with { Decision = decision with { EventId = eventId } };
        state.Decisions[^1] = payload.Decision!;
        var projectEvent = new ProjectEvent(eventId, state.Sequence, "decision-recorded", SystemClock.UtcNow(), state.Parameters.Version, state.LastDependencyVersion, null, null, SerializeJson(payload), decision.Action.ToString());
        CommitEvent(projectEvent, [], state);
        Analyze($"决定“{decision.Action}”后的依赖版本", state);
    }

    private void CommitEvent(ProjectEvent projectEvent, IReadOnlyList<ProjectMaterial> materials, SeedingState state)
    {
        var sequence = projectEvent.Sequence;
        _repository.InTransaction(SampleCase.CaseId, (c, t) =>
        {
            ReviewRepository.InsertEvent(c, t, SampleCase.CaseId, projectEvent with { Sequence = sequence });
            foreach (var material in materials) ReviewRepository.InsertMaterial(c, t, SampleCase.CaseId, material);
            return true;
        });
        state.Events.Add(new EventRecord(projectEvent.EventId, sequence, projectEvent.Type, projectEvent.OccurredAt, projectEvent.ParametersVersion, projectEvent.ParentDependencyVersion, projectEvent.InputSha256, projectEvent.DependencyVersion, projectEvent.PayloadJson, projectEvent.Status));
    }

    private static List<EntryMaterial> ParseEntryMaterials(byte[] archive)
    {
        using var input = new MemoryStream(archive);
        using var zip = new ZipArchive(input, ZipArchiveMode.Read);
        var entries = new List<EntryMaterial>();
        var index = 0;
        foreach (var entry in zip.Entries.OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            using var source = entry.Open();
            using var target = new MemoryStream();
            source.CopyTo(target);
            var bytes = target.ToArray();
            var normalized = PathRules.NormalizeEntryName(entry.FullName);
            entries.Add(new(StableHashes.StableId("material:entry", $"entry|{entry.FullName}|{index}"), index, entry.FullName, normalized, bytes.Length, StableHashes.Sha256(bytes), null, null, bytes));
            index++;
        }
        return entries;
    }

    private static string SerializeJson<T>(T value) => System.Text.Json.JsonSerializer.Serialize(value, Options);
    private static string EventId(int sequence, string label) => $"event:{sequence + 1:D3}:{label}";

    private sealed class SeedingState
    {
        public int Sequence { get; set; }
        public byte[]? Archive { get; set; }
        public string? ArchiveMaterialId { get; set; }
        public string? ParentMaterialId { get; set; }
        public AnalysisParameters Parameters { get; set; } = new();
        public string? LastDependencyVersion { get; set; }
        public List<ReviewDecision> Decisions { get; } = [];
        public List<EventRecord> Events { get; } = [];
    }
}

public sealed record EventPayload(string Message, AnalysisResult? Analysis, AnalysisParameters? Parameters, ReviewDecision? Decision, string? MaterialId, string? ParentMaterialId, string? Revision);
