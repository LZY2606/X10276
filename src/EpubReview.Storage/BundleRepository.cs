using System.IO.Compression;
using System.Text;
using System.Text.Json;
using EpubReview.Core;
using EpubReview.Core.Events;
using Microsoft.Data.Sqlite;

namespace EpubReview.Storage;

public sealed record BundleManifest(
    string BundleFormat,
    int FormatVersion,
    string ProjectId,
    string Title,
    string CreatedAt,
    IReadOnlyList<string> EventFiles,
    string EventsSha256,
    IReadOnlyList<string> MaterialHashes);

public sealed record BundleImportReport(
    string ProjectId,
    int EventCount,
    int MaterialCount,
    int AnalysisCount,
    int DecisionCount,
    IReadOnlyList<string> BranchIds,
    string ManifestSha256,
    bool Verified);

public sealed partial class ReviewRepository
{
    private static readonly JsonSerializerOptions BundleJson = new()
    {
        WriteIndented = false
    };

    public byte[] ExportBundle(string projectId, DateTimeOffset now)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var metaTuple = GetProjectMeta(projectId);
            if (metaTuple.Title is null) throw new InvalidOperationException("project missing");
            var meta = (Title: metaTuple.Title!, CreatedAt: metaTuple.CreatedAt!.Value);
            var events = ReadAllEvents(projectId);
            var materials = GetAllMaterials(projectId);
            var analyses = ListAnalyses(projectId);
            var branches = ListBranches(projectId);

            var decisions = ReadDecisionsRaw(projectId);

            var eventsJson = JsonSerializer.Serialize(events.Select(e => EventWire.From(e)), BundleJson);
            var eventsBytes = Encoding.UTF8.GetBytes(eventsJson);
            WriteEntry(zip, "events.json", eventsBytes);

            var materialsJson = JsonSerializer.Serialize(materials.Select(m => new
            {
                materialSetId = m.MaterialSetId,
                contentVersion = m.ContentVersion,
                file = "materials/" + m.MaterialSetId + ".bin",
                sha256 = Hashing.Sha256Hex(m.ZipBytes)
            }), BundleJson);
            WriteEntry(zip, "materials/index.json", Encoding.UTF8.GetBytes(materialsJson));

            foreach (var m in materials)
            {
                WriteEntry(zip, "materials/" + m.MaterialSetId + ".bin", m.ZipBytes);
            }

            var analysesJson = JsonSerializer.Serialize(analyses.Select(a => new
            {
                analysisId = a.AnalysisId,
                branchId = a.BranchId,
                dependencyVersion = a.DependencyVersion,
                materialSetId = a.MaterialSetId,
                paramsVersionId = a.ParamsVersionId,
                resultJson = a.Json
            }), BundleJson);
            WriteEntry(zip, "analyses.json", Encoding.UTF8.GetBytes(analysesJson));

            WriteEntry(zip, "decisions.json", Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(decisions, BundleJson)));

            WriteEntry(zip, "branches.json", Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(branches, BundleJson)));

            var eventsSha = Hashing.Sha256Hex(eventsBytes);
            var manifest = new
            {
                bundleFormat = "epub-review-bundle",
                formatVersion = 1,
                projectId,
                title = meta.Title,
                createdAt = now.ToString("O"),
                eventsFile = "events.json",
                eventsSha256 = eventsSha,
                materialHashes = materials.Select(m => Hashing.Sha256Hex(m.ZipBytes)).ToArray()
            };
            var manifestJson = JsonSerializer.Serialize(manifest, BundleJson);
            WriteEntry(zip, "manifest.json", Encoding.UTF8.GetBytes(manifestJson));
        }
        return ms.ToArray();
    }

    private static void WriteEntry(ZipArchive zip, string name, byte[] bytes)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
        using var s = entry.Open();
        s.Write(bytes, 0, bytes.Length);
    }

    private IReadOnlyList<object> ReadDecisionsRaw(string projectId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT decision_id, branch_id, event_id, finding_id, verdict,
                   rationale, adjusted_params_json, conflicts, created_at
            FROM decisions WHERE project_id=$p ORDER BY rowid
            """;
        cmd.Parameters.AddWithValue("$p", projectId);
        var list = new List<object>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new
            {
                decisionId = r.GetString(0),
                branchId = r.GetString(1),
                eventId = r.GetString(2),
                findingId = r.GetString(3),
                verdict = r.GetString(4),
                rationale = r.GetString(5),
                adjustedParamsJson = r.IsDBNull(6) ? null : r.GetString(6),
                conflicts = r.GetInt32(7) != 0,
                createdAt = r.GetString(8)
            });
        }
        return list;
    }

    public BundleImportReport ImportBundle(byte[] bundleBytes, ReviewRepository target,
        DateTimeOffset now, bool keepIdentity = true)
    {
        using var src = new ZipArchive(new MemoryStream(bundleBytes), ZipArchiveMode.Read);
        var manifestEntry = src.GetEntry("manifest.json")
            ?? throw new InvalidDataException("manifest.json absent; bundle is not a valid export");
        using var ms = new MemoryStream();
        manifestEntry.Open().CopyTo(ms);
        using var manifestDoc = JsonDocument.Parse(ms.ToArray());
        var root = manifestDoc.RootElement;
        var projectId = root.GetProperty("projectId").GetString()!;
        var title = root.GetProperty("title").GetString()!;
        var declaredEventsSha = root.GetProperty("eventsSha256").GetString()!;

        var eventsEntry = src.GetEntry(root.GetProperty("eventsFile").GetString()!)!;
        byte[] eventsBytes;
        using (var es = new MemoryStream())
        {
            eventsEntry.Open().CopyTo(es);
            eventsBytes = es.ToArray();
        }
        var actualEventsSha = Hashing.Sha256Hex(eventsBytes);
        bool verified = actualEventsSha == declaredEventsSha;

        target.InitSchema();
        target.CreateProject(projectId, title, now);

        using var conn = target.Open();
        using var tx = conn.BeginTransaction();

        var wireEvents = JsonSerializer.Deserialize<List<EventWire>>(
            Encoding.UTF8.GetString(eventsBytes), BundleJson)!;

        var seenBranches = new HashSet<string>();
        foreach (var wire in wireEvents)
        {
            if (seenBranches.Add(wire.branchId) && wire.branchId != "main")
            {
                EnsureBranch(conn, tx, projectId, wire.branchId, wire.branchId);
            }
            InsertWireEvent(conn, tx, projectId, wire);
        }

        var matIndexEntry = src.GetEntry("materials/index.json");
        if (matIndexEntry is not null)
        {
            using var ms2 = new MemoryStream();
            matIndexEntry.Open().CopyTo(ms2);
            var matIndex = JsonSerializer.Deserialize<List<MaterialIndexEntry>>(
                Encoding.UTF8.GetString(ms2.ToArray()), BundleJson)!;
            foreach (var mi in matIndex)
            {
                var entry = src.GetEntry(mi.file)
                    ?? throw new InvalidDataException("material file missing: " + mi.file);
                using var es = new MemoryStream();
                entry.Open().CopyTo(es);
                var bytes = es.ToArray();
                var sha = Hashing.Sha256Hex(bytes);
                if (sha != mi.sha256)
                {
                    throw new InvalidDataException("material hash mismatch: " + mi.materialSetId);
                }
                target.SaveMaterial(projectId, mi.materialSetId, mi.contentVersion, bytes, now, conn, tx);
            }
        }

        int analysisCount = 0;
        var analysesEntry = src.GetEntry("analyses.json");
        if (analysesEntry is not null)
        {
            using var es = new MemoryStream();
            analysesEntry.Open().CopyTo(es);
            var analyses = JsonSerializer.Deserialize<List<AnalysisIndexEntry>>(
                Encoding.UTF8.GetString(es.ToArray()), BundleJson)!;
            foreach (var a in analyses)
            {
                target.SaveAnalysis(projectId, a.branchId, a.analysisId, a.dependencyVersion,
                    a.materialSetId, a.paramsVersionId, a.resultJson, now, conn, tx);
                analysisCount++;
            }
        }

        int decisionCount = 0;
        var decisionsEntry = src.GetEntry("decisions.json");
        if (decisionsEntry is not null)
        {
            using var es = new MemoryStream();
            decisionsEntry.Open().CopyTo(es);
            var decisions = JsonSerializer.Deserialize<List<DecisionWire>>(
                Encoding.UTF8.GetString(es.ToArray()), BundleJson)!;
            foreach (var d in decisions)
            {
                target.SaveDecision(projectId, d.branchId, d.decisionId, d.eventId,
                    d.findingId, d.verdict, d.rationale, d.adjustedParamsJson,
                    d.conflicts, DateTimeOffset.Parse(d.createdAt), conn, tx);
                decisionCount++;
            }
        }

        RecomputeHeadsFromEvents(conn, tx, projectId);
        tx.Commit();

        var bundleSha = Hashing.Sha256Hex(ms.ToArray());
        return new BundleImportReport(projectId, wireEvents.Count,
            root.GetProperty("materialHashes").GetArrayLength(),
            analysisCount, decisionCount,
            wireEvents.Select(e => e.branchId).Distinct().ToArray(),
            actualEventsSha, verified);
    }

    private static void RecomputeHeadsFromEvents(SqliteConnection conn, SqliteTransaction tx,
        string projectId)
    {
        var branchIds = new List<string>();
        using (var q = conn.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = "SELECT branch_id FROM branches WHERE project_id=$p";
            q.Parameters.AddWithValue("$p", projectId);
            using var r = q.ExecuteReader();
            while (r.Read()) branchIds.Add(r.GetString(0));
        }

        foreach (var b in branchIds)
        {
            string? lastEventId = null;
            string lastHash = "";
            long lastSeq = -1;
            using var q = conn.CreateCommand();
            q.Transaction = tx;
            q.CommandText = """
                SELECT event_id, event_hash, seq FROM events
                WHERE project_id=$p AND branch_id=$b ORDER BY seq DESC LIMIT 1
                """;
            q.Parameters.AddWithValue("$p", projectId);
            q.Parameters.AddWithValue("$b", b);
            using var r = q.ExecuteReader();
            if (r.Read())
            {
                lastEventId = r.GetString(0);
                lastHash = r.GetString(1);
                lastSeq = r.GetInt64(2);
            }
            using var u = conn.CreateCommand();
            u.Transaction = tx;
            u.CommandText = """
                UPDATE branches SET head_event_id=$e, head_seq=$s, head_hash=$h
                WHERE project_id=$p AND branch_id=$b;
                UPDATE projects SET head_event_id=$e, head_seq=$s, head_hash=$h
                WHERE project_id=$p AND (SELECT name FROM branches
                    WHERE project_id=$p AND branch_id=$b)='main';
                """;
            u.Parameters.AddWithValue("$e", (object?)lastEventId ?? DBNull.Value);
            u.Parameters.AddWithValue("$s", lastSeq);
            u.Parameters.AddWithValue("$h", lastHash);
            u.Parameters.AddWithValue("$p", projectId);
            u.Parameters.AddWithValue("$b", b);
            u.ExecuteNonQuery();
        }
    }

    private static void EnsureBranch(SqliteConnection conn, SqliteTransaction tx,
        string projectId, string branchId, string name)
    {
        using var q = conn.CreateCommand();
        q.Transaction = tx;
        q.CommandText = """
            INSERT OR IGNORE INTO branches(branch_id, project_id, name, parent_branch_id,
                fork_event_id, head_event_id, head_seq, head_hash, created_at)
            VALUES ($b,$p,$n,'main',NULL,NULL,-1,'',$at)
            """;
        q.Parameters.AddWithValue("$b", branchId);
        q.Parameters.AddWithValue("$p", projectId);
        q.Parameters.AddWithValue("$n", name);
        q.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        q.ExecuteNonQuery();
    }

    private static void InsertWireEvent(SqliteConnection conn, SqliteTransaction tx,
        string projectId, EventWire wire)
    {
        using var ins = conn.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = """
            INSERT INTO events(event_id, project_id, branch_id, seq, parent_event_id,
                prev_hash, event_hash, kind, occurred_at, actor, summary,
                payload_json, input_summaries_json, params_version, parent_deps_json, topic)
            VALUES ($e,$p,$b,$s,$pe,$ph,$h,$k,$at,$a,$su,$pj,$ij,$pv,$dj,$tp)
            """;
        ins.Parameters.AddWithValue("$e", wire.eventId);
        ins.Parameters.AddWithValue("$p", projectId);
        ins.Parameters.AddWithValue("$b", wire.branchId);
        ins.Parameters.AddWithValue("$s", wire.seq);
        ins.Parameters.AddWithValue("$pe", (object?)wire.parentEventId ?? DBNull.Value);
        ins.Parameters.AddWithValue("$ph", wire.prevHash);
        ins.Parameters.AddWithValue("$h", wire.eventHash);
        ins.Parameters.AddWithValue("$k", wire.kind);
        ins.Parameters.AddWithValue("$at", wire.occurredAt);
        ins.Parameters.AddWithValue("$a", wire.actor);
        ins.Parameters.AddWithValue("$su", wire.summary);
        ins.Parameters.AddWithValue("$pj", wire.payloadJson);
        ins.Parameters.AddWithValue("$ij", wire.inputSummariesJson);
        ins.Parameters.AddWithValue("$pv", (object?)wire.paramsVersion ?? DBNull.Value);
        ins.Parameters.AddWithValue("$dj", wire.parentDepsJson);
        ins.Parameters.AddWithValue("$tp", (object?)wire.topic ?? DBNull.Value);
        ins.ExecuteNonQuery();
    }

    private sealed record MaterialIndexEntry(
        string materialSetId, string contentVersion, string file, string sha256);

    private sealed record AnalysisIndexEntry(
        string analysisId, string branchId, string dependencyVersion,
        string materialSetId, string paramsVersionId, string resultJson);

    private sealed record DecisionWire(
        string decisionId, string branchId, string eventId, string findingId,
        string verdict, string rationale, string? adjustedParamsJson,
        bool conflicts, string createdAt);

    public sealed record EventWire(
        string eventId, string branchId, long seq, string? parentEventId,
        string prevHash, string eventHash, string kind, string occurredAt,
        string actor, string summary, string payloadJson,
        string inputSummariesJson, string? paramsVersion,
        string parentDepsJson, string? topic)
    {
        public static EventWire From(ReviewEvent e) => new(
            e.EventId, e.BranchId, e.Sequence, e.ParentEventId,
            e.PrevHash, e.EventHash, e.Kind.ToString(), e.OccurredAt.ToString("O"),
            e.Actor, e.Summary,
            JsonSerializer.Serialize(e.Payload, BundleJson),
            JsonSerializer.Serialize(e.InputSummaries, BundleJson),
            e.ParamsVersion,
            JsonSerializer.Serialize(e.ParentDependencies, BundleJson),
            e.Topic);
    }
}
