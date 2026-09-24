using System.Text.Json;
using EpubReview.Core;
using Microsoft.Data.Sqlite;

namespace EpubReview.Storage;

public sealed class ReviewStore : IAsyncDisposable
{
    private readonly SqliteConnection connection;

    public ReviewStore(string databasePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var recovered = RecoverIfCorrupt(databasePath);
        DatabasePath = recovered ? databasePath + ".recovered" : databasePath;
        RecoveredFromCorruptDatabase = recovered;
        connection = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadWriteCreate;Foreign Keys=True");
        connection.Open();
        Initialize();
    }

    public string DatabasePath { get; }
    public bool RecoveredFromCorruptDatabase { get; }

    private bool RecoverIfCorrupt(string path)
    {
        if (File.Exists(path) && new FileInfo(path).Length == 0)
        {
            File.Move(path, path + $".corrupt-{DateTimeOffset.UtcNow.Ticks}");
            return true;
        }
        if (!File.Exists(path)) return false;
        try
        {
            using var probe = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
            probe.Open();
            using var command = probe.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            var result = Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            return !string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch (SqliteException)
        {
            var quarantine = path + $".corrupt-{DateTimeOffset.UtcNow.Ticks}";
            File.Move(path, quarantine);
            return true;
        }
    }

    private void Initialize()
    {
        using var tx = connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
        Exec("PRAGMA journal_mode=WAL;", tx);
        Exec("""
CREATE TABLE IF NOT EXISTS materials(id TEXT PRIMARY KEY, original_name TEXT NOT NULL, sha256 TEXT NOT NULL, received_at TEXT NOT NULL, payload_json TEXT NOT NULL, bytes BLOB NOT NULL);
CREATE TABLE IF NOT EXISTS profiles(id TEXT PRIMARY KEY, version_no INTEGER NOT NULL, name TEXT NOT NULL, created_at TEXT NOT NULL, payload_json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS analyses(id TEXT PRIMARY KEY, material_id TEXT NOT NULL, profile_id TEXT NOT NULL, parent_analysis_id TEXT, adopted INTEGER NOT NULL, scenario TEXT NOT NULL, outcome TEXT NOT NULL, payload_json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS decisions(id TEXT PRIMARY KEY, event_id TEXT NOT NULL, analysis_id TEXT NOT NULL, subject_id TEXT NOT NULL, kind TEXT NOT NULL, created_at TEXT NOT NULL, payload_json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS events(id TEXT PRIMARY KEY, sequence_no INTEGER NOT NULL UNIQUE, state_version INTEGER NOT NULL UNIQUE, occurred_at TEXT NOT NULL, type TEXT NOT NULL, subject_id TEXT NOT NULL, adopted INTEGER NOT NULL, reason_code TEXT, input_summary TEXT NOT NULL, parameter_version TEXT NOT NULL, parent_dependency_json TEXT NOT NULL, payload_json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS export_log(id INTEGER PRIMARY KEY AUTOINCREMENT, exported_at TEXT NOT NULL, content_sha256 TEXT NOT NULL);
""", tx);
        tx.Commit();
    }

    public Task SaveMaterialAsync(EpubMaterial material, EvidenceEvent materialEvent) =>
        InTransactionAsync(async tx =>
        {
            await AddEvent(tx, materialEvent);
            await ExecAsync("""
INSERT OR REPLACE INTO materials(id, original_name, sha256, received_at, payload_json, bytes)
VALUES($id, $name, $sha, $at, $json, $bytes)
""", tx,
                ("$id", material.MaterialId), ("$name", material.OriginalFileName), ("$sha", material.Sha256), ("$at", material.ReceivedAtInstant),
                ("$json", Json(material)), ("$bytes", material.ZipEntries.SelectMany(e => e.Bytes).ToArray()));
        });

    public Task SaveProfileAsync(RuleProfile profile, EvidenceEvent profileEvent) =>
        InTransactionAsync(async tx =>
        {
            await AddEvent(tx, profileEvent);
            await ExecAsync("INSERT OR REPLACE INTO profiles(id, version_no, name, created_at, payload_json) VALUES($id,$v,$n,$at,$json)", tx,
                ("$id", profile.RuleProfileId), ("$v", profile.Version), ("$n", profile.Name), ("$at", profile.CreatedAtInstant), ("$json", Json(profile)));
        });

    public Task SaveAnalysisAsync(AnalysisResult analysis, EvidenceEvent analysisEvent) =>
        InTransactionAsync(async tx =>
        {
            await AddEvent(tx, analysisEvent);
            await ExecAsync("""
INSERT OR REPLACE INTO analyses(id, material_id, profile_id, parent_analysis_id, adopted, scenario, outcome, payload_json)
VALUES($id,$m,$p,$parent,$adopted,$scenario,$outcome,$json)
""", tx,
                ("$id", analysis.AnalysisId), ("$m", analysis.MaterialId), ("$p", analysis.RuleProfileId), ("$parent", (object?)analysis.ParentAnalysisId ?? DBNull.Value),
                ("$adopted", analysis.Adopted ? 1 : 0), ("$scenario", analysis.ScenarioLabel), ("$outcome", analysis.Outcome.ToString()), ("$json", Json(analysis)));
        });

    public Task SaveDecisionAsync(ReviewDecision decision, EvidenceEvent decisionEvent) =>
        InTransactionAsync(async tx =>
        {
            await AddEvent(tx, decisionEvent);
            await ExecAsync("""
INSERT OR REPLACE INTO decisions(id, event_id, analysis_id, subject_id, kind, created_at, payload_json)
VALUES($id,$event,$analysis,$subject,$kind,$at,$json)
""", tx,
                ("$id", decision.DecisionId), ("$event", decision.EventId), ("$analysis", decision.AnalysisId), ("$subject", decision.SubjectId),
                ("$kind", decision.Kind.ToString()), ("$at", decision.CreatedAtInstant), ("$json", Json(decision)));
        });

    public async Task<ReviewSnapshot> GetSnapshotAsync(string? toEventId = null)
    {
        long? limit = null;
        if (!string.IsNullOrWhiteSpace(toEventId))
        {
            await using var limitCommand = connection.CreateCommand();
            limitCommand.CommandText = "SELECT sequence_no FROM events WHERE id=$id";
            limitCommand.Parameters.AddWithValue("$id", toEventId);
            limit = (long?)await limitCommand.ExecuteScalarAsync();
            if (limit is null) throw new KeyNotFoundException($"Event '{toEventId}' was not found.");
        }

        var events = await LoadListAsync("SELECT payload_json FROM events ORDER BY sequence_no", r => Deserialize<EvidenceEvent>(r, 0));
        events = events.Where(e => !limit.HasValue || e.Sequence <= limit.Value).ToList();
        var materialIds = new List<string>();
        var profileIds = new List<string>();
        var analysisIds = new List<string>();
        var decisionIds = new List<string>();
        foreach (var e in events)
        {
            if (e.Type == "material.imported") materialIds.Add(e.SubjectId);
            if (e.Type == "profile.saved") profileIds.Add(e.SubjectId);
            if (e.Type == "analysis.calculated") analysisIds.Add(e.SubjectId);
            if (e.Type.StartsWith("decision.", StringComparison.Ordinal)) decisionIds.Add(e.SubjectId);
        }

        var materials = await LoadManyByIdsAsync("materials", materialIds, r => Deserialize<EpubMaterial>(r, 0));
        var profiles = await LoadManyByIdsAsync("profiles", profileIds, r => Deserialize<RuleProfile>(r, 0));
        var analyses = await LoadManyByIdsAsync("analyses", analysisIds, r => Deserialize<AnalysisResult>(r, 0));
        var decisions = await LoadManyByIdsAsync("decisions", decisionIds, r => Deserialize<ReviewDecision>(r, 0));
        var adoptedIds = decisions.Where(d => d.Kind != DecisionKind.Rejected).Select(d => d.AnalysisId).ToHashSet(StringComparer.Ordinal);
        var rejectedIds = decisions.Where(d => d.Kind == DecisionKind.Rejected).Select(d => d.AnalysisId).ToHashSet(StringComparer.Ordinal);
        analyses = analyses.Select(a => a with { Adopted = adoptedIds.Contains(a.AnalysisId) || (!rejectedIds.Contains(a.AnalysisId) && a.Adopted) }).ToList();
        var last = events[^1];
        return new ReviewSnapshot(last.StateVersion, last.EventId, materials, profiles, analyses, decisions, events);
    }

    public async Task<string> ExportAsync(string targetPath, string now)
    {
        var snapshot = await GetSnapshotAsync();
        var payload = new
        {
            bundleVersion = 1,
            exportedAtInstant = now,
            materials = snapshot.Materials.Select(JsonElement).ToArray(),
            profiles = snapshot.Profiles.Select(JsonElement).ToArray(),
            analyses = snapshot.Analyses.Select(JsonElement).ToArray(),
            decisions = snapshot.Decisions.Select(JsonElement).ToArray(),
            events = snapshot.Events.Select(JsonElement).ToArray()
        };
        var digest = Hashing.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { payload.bundleVersion, payload.exportedAtInstant, payload.materials, payload.profiles, payload.analyses, payload.decisions, payload.events }, Options)));
        var envelope = JsonSerializer.Serialize(new { bundleVersion = 1, exportedAtInstant = now, contentSha256 = digest, payload.materials, payload.profiles, payload.analyses, payload.decisions, payload.events }, Options);
        var temporary = targetPath + ".tmp";
        await File.WriteAllTextAsync(temporary, envelope);
        File.Move(temporary, targetPath, true);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO export_log(exported_at, content_sha256) VALUES($at,$sha)";
        command.Parameters.AddWithValue("$at", now);
        command.Parameters.AddWithValue("$sha", digest);
        await command.ExecuteNonQueryAsync();
        return digest;
    }

    public async Task ImportExportAsync(string json, string now)
    {
        ExportEnvelope? envelope;
        try { envelope = JsonSerializer.Deserialize<ExportEnvelope>(json, Options); }
        catch (JsonException ex) { throw new InvalidDataException("EXPORT.FORMAT_CORRUPT: export bundle is not valid JSON.", ex); }
        if (envelope is null || envelope.bundleVersion != 1 || envelope.events is null) throw new InvalidDataException("EXPORT.FORMAT_CORRUPT: export envelope is incomplete.");
        var recomputed = Hashing.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { envelope.bundleVersion, envelope.exportedAtInstant, envelope.materials, envelope.profiles, envelope.analyses, envelope.decisions, envelope.events }, Options)));
        if (!string.Equals(recomputed, envelope.contentSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("EXPORT.CONTENT_DIGEST_MISMATCH: half-written or modified export was rejected.");

        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        try
        {
            foreach (var item in envelope.materials ?? []) await InsertJsonAsync("materials", MaterialColumns(), item, tx);
            foreach (var item in envelope.profiles ?? []) await InsertJsonAsync("profiles", ProfileColumns(), item, tx);
            foreach (var item in envelope.analyses ?? []) await InsertJsonAsync("analyses", AnalysisColumns(), item, tx);
            foreach (var item in envelope.decisions ?? []) await InsertJsonAsync("decisions", DecisionColumns(), item, tx);
            foreach (var item in envelope.events ?? []) await InsertEventAsync(item, tx);
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    public ValueTask DisposeAsync() => connection.DisposeAsync();
}
