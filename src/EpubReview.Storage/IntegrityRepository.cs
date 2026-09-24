using System.Text.Json;
using EpubReview.Core;
using EpubReview.Core.Events;
using Microsoft.Data.Sqlite;

namespace EpubReview.Storage;

public sealed record ChainProblem(long Sequence, string EventId, string ExpectedHash, string ActualHash, string Detail);

public sealed record RecoveryReport(
    int DanglingEventsRemoved,
    IReadOnlyList<ChainProblem> Problems,
    string BranchHeadHash,
    long BranchHeadSeq,
    IReadOnlyList<string> LogEntries);

public sealed partial class ReviewRepository
{
    private static readonly JsonSerializerOptions ChainJson = new() { WriteIndented = false };

    public IReadOnlyList<ChainProblem> VerifyChain(string projectId, string branchId)
    {
        var events = ReadEvents(projectId, branchId);
        var problems = new List<ChainProblem>();
        string prev = "";
        long expectedSeq = 0;

        foreach (var e in events)
        {
            if (e.Sequence != expectedSeq)
            {
                problems.Add(new ChainProblem(e.Sequence, e.EventId, "(seq)", e.Sequence.ToString(),
                    $"sequence gap: expected {expectedSeq}"));
            }

            var payloadJson = JsonSerializer.Serialize(e.Payload, ChainJson);
            var summariesJson = JsonSerializer.Serialize(e.InputSummaries, ChainJson);
            var depsJson = JsonSerializer.Serialize(e.ParentDependencies, ChainJson);
            var recomputed = string.Join("\n",
                e.PrevHash, projectId, branchId, e.Sequence.ToString(),
                e.ParentEventId ?? "", e.Kind.ToString(), e.OccurredAt.ToString("O"),
                e.Actor, e.Summary, payloadJson, summariesJson,
                e.ParamsVersion ?? "", depsJson, e.Topic ?? "");
            var expected = Hashing.Sha256Hex(recomputed);

            if (e.PrevHash != prev)
            {
                problems.Add(new ChainProblem(e.Sequence, e.EventId, prev, e.PrevHash,
                    "prev_hash does not match previous event hash"));
            }
            if (expected != e.EventHash)
            {
                problems.Add(new ChainProblem(e.Sequence, e.EventId, expected, e.EventHash,
                    "event content hash mismatch"));
            }

            prev = e.EventHash;
            expectedSeq++;
        }

        return problems;
    }

    public RecoveryReport Recover(string projectId, string branchId, DateTimeOffset now,
        bool simulateHalfWrittenTail = false)
    {
        var log = new List<string>();
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        var events = ReadEvents(conn, projectId, branchId);
        long cutAt = events.Count;
        string headHash = "";
        string? headEventId = null;
        var problems = new List<ChainProblem>();

        for (int i = 0; i < events.Count; i++)
        {
            var e = events[i];
            var payloadJson = JsonSerializer.Serialize(e.Payload, ChainJson);
            var summariesJson = JsonSerializer.Serialize(e.InputSummaries, ChainJson);
            var depsJson = JsonSerializer.Serialize(e.ParentDependencies, ChainJson);
            var recomputed = string.Join("\n",
                e.PrevHash, projectId, branchId, e.Sequence.ToString(),
                e.ParentEventId ?? "", e.Kind.ToString(), e.OccurredAt.ToString("O"),
                e.Actor, e.Summary, payloadJson, summariesJson,
                e.ParamsVersion ?? "", depsJson, e.Topic ?? "");
            var expected = Hashing.Sha256Hex(recomputed);

            bool broken = expected != e.EventHash ||
                          (i > 0 && e.PrevHash != events[i - 1].EventHash) ||
                          (i == 0 && e.PrevHash.Length != 0);

            if (simulateHalfWrittenTail && i == events.Count - 1 && !broken)
            {
                broken = true;
                log.Add($"simulated half-write detected at seq={e.Sequence} event={e.EventId}");
            }

            if (broken)
            {
                problems.Add(new ChainProblem(e.Sequence, e.EventId, expected, e.EventHash,
                    "chain broken; tail will be truncated"));
                cutAt = i;
                break;
            }

            headHash = e.EventHash;
            headEventId = e.EventId;
        }

        int removed = 0;
        if (cutAt < events.Count)
        {
            var cutoffSeq = cutAt;
            using (var del = conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText =
                    "DELETE FROM events WHERE project_id=$p AND branch_id=$b AND seq>=$s";
                del.Parameters.AddWithValue("$p", projectId);
                del.Parameters.AddWithValue("$b", branchId);
                del.Parameters.AddWithValue("$s", cutoffSeq);
                removed = del.ExecuteNonQuery();
            }
            using (var delD = conn.CreateCommand())
            {
                delD.Transaction = tx;
                delD.CommandText = """
                    DELETE FROM decisions WHERE project_id=$p AND branch_id=$b
                    AND event_id NOT IN (
                        SELECT event_id FROM events WHERE project_id=$p AND branch_id=$b
                    )
                    """;
                delD.Parameters.AddWithValue("$p", projectId);
                delD.Parameters.AddWithValue("$b", branchId);
                delD.ExecuteNonQuery();
            }
            var validAnalyses = new HashSet<string>();
            var validMaterials = new HashSet<string>();
            foreach (var ev in events.Take((int)cutAt))
            {
                if (ev.Kind == EventKind.AnalysisComputed &&
                    ev.Payload.TryGetProperty("analysisId", out var aid))
                {
                    validAnalyses.Add(aid.GetString()!);
                }
                if ((ev.Kind == EventKind.EpubImported || ev.Kind == EventKind.MaterialPatched) &&
                    ev.Payload.TryGetProperty("materialSetId", out var mid))
                {
                    validMaterials.Add(mid.GetString()!);
                }
            }
            using (var delA = conn.CreateCommand())
            {
                delA.Transaction = tx;
                delA.CommandText =
                    "SELECT analysis_id FROM analyses WHERE project_id=$p AND branch_id=$b";
                delA.Parameters.AddWithValue("$p", projectId);
                delA.Parameters.AddWithValue("$b", branchId);
                var stale = new List<string>();
                using (var rr = delA.ExecuteReader())
                {
                    while (rr.Read())
                    {
                        var id = rr.GetString(0);
                        if (!validAnalyses.Contains(id)) stale.Add(id);
                    }
                }
                foreach (var id in stale)
                {
                    using var d2 = conn.CreateCommand();
                    d2.Transaction = tx;
                    d2.CommandText =
                        "DELETE FROM analyses WHERE project_id=$p AND branch_id=$b AND analysis_id=$a";
                    d2.Parameters.AddWithValue("$p", projectId);
                    d2.Parameters.AddWithValue("$b", branchId);
                    d2.Parameters.AddWithValue("$a", id);
                    d2.ExecuteNonQuery();
                }
            }
            using (var upd = conn.CreateCommand())
            {
                upd.Transaction = tx;
                upd.CommandText = """
                    UPDATE branches SET head_event_id=$e, head_seq=$s, head_hash=$h
                    WHERE project_id=$p AND branch_id=$b
                    """;
                upd.Parameters.AddWithValue("$e", (object?)headEventId ?? DBNull.Value);
                upd.Parameters.AddWithValue("$s", cutAt - 1);
                upd.Parameters.AddWithValue("$h", headHash);
                upd.Parameters.AddWithValue("$p", projectId);
                upd.Parameters.AddWithValue("$b", branchId);
                upd.ExecuteNonQuery();
            }
            log.Add($"truncated {removed} dangling event(s) from seq {cutoffSeq} onward");
        }

        using (var rec = conn.CreateCommand())
        {
            rec.Transaction = tx;
            rec.CommandText = """
                INSERT INTO recovery_log(project_id, operation, evidence, outcome, recovered_at)
                VALUES ($p,$op,$ev,$out,$at)
                """;
            rec.Parameters.AddWithValue("$p", projectId);
            rec.Parameters.AddWithValue("$op", "chain-recovery");
            rec.Parameters.AddWithValue("$ev",
                problems.Count == 0 ? "no-break" : string.Join(";", problems.Select(x => x.EventId)));
            rec.Parameters.AddWithValue("$out",
                removed == 0 ? "clean" : $"recovered-to-seq-{cutAt - 1}");
            rec.Parameters.AddWithValue("$at", now.ToString("O"));
            rec.ExecuteNonQuery();
        }

        tx.Commit();
        return new RecoveryReport(removed, problems, headHash, cutAt - 1, log);
    }

    public IReadOnlyList<(long Id, string? Project, string Operation, string Outcome, string At)>
        ListRecoveryLog(string? projectId = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = projectId is null
            ? "SELECT id, project_id, operation, outcome, recovered_at FROM recovery_log ORDER BY id"
            : "SELECT id, project_id, operation, outcome, recovered_at FROM recovery_log WHERE project_id=$p ORDER BY id";
        if (projectId is not null) cmd.Parameters.AddWithValue("$p", projectId);
        var list = new List<(long, string?, string, string, string)>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add((r.GetInt64(0),
                r.IsDBNull(1) ? null : r.GetString(1),
                r.GetString(2), r.GetString(3), r.GetString(4)));
        }
        return list;
    }
}
