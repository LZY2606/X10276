using System.Text.Json;
using EpubReview.Core;
using EpubReview.Core.Events;
using Microsoft.Data.Sqlite;

namespace EpubReview.Storage;

public sealed record StoredEventEnvelope(
    ReviewEvent Event,
    string ProjectId);

public sealed partial class ReviewRepository
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false
    };

    public IReadOnlyList<ReviewEvent> ReadEvents(string projectId, string branchId)
    {
        using var conn = Open();
        return ReadEvents(conn, projectId, branchId);
    }

    private static List<ReviewEvent> ReadEvents(SqliteConnection conn, string projectId, string branchId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT event_id, branch_id, seq, parent_event_id, prev_hash, event_hash,
                   kind, occurred_at, actor, summary, payload_json,
                   input_summaries_json, params_version, parent_deps_json, topic
            FROM events
            WHERE project_id=$p AND branch_id=$b
            ORDER BY seq
            """;
        cmd.Parameters.AddWithValue("$p", projectId);
        cmd.Parameters.AddWithValue("$b", branchId);
        var result = new List<ReviewEvent>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            result.Add(MapEvent(r));
        }
        return result;
    }

    public IReadOnlyList<ReviewEvent> ReadAllEvents(string projectId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT event_id, branch_id, seq, parent_event_id, prev_hash, event_hash,
                   kind, occurred_at, actor, summary, payload_json,
                   input_summaries_json, params_version, parent_deps_json, topic
            FROM events
            WHERE project_id=$p
            ORDER BY occurred_at, seq
            """;
        cmd.Parameters.AddWithValue("$p", projectId);
        var result = new List<ReviewEvent>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) result.Add(MapEvent(r));
        return result;
    }

    private static ReviewEvent MapEvent(SqliteDataReader r)
    {
        var payload = JsonDocument.Parse(r.GetString(10)).RootElement.Clone();
        var summaries = JsonSerializer.Deserialize<List<string>>(r.GetString(11)) ?? new();
        var deps = JsonSerializer.Deserialize<List<string>>(r.GetString(13)) ?? new();
        return new ReviewEvent(
            r.GetString(0),
            r.GetInt64(2),
            r.GetString(1),
            r.IsDBNull(3) ? null : r.GetString(3),
            r.GetString(4),
            r.GetString(5),
            Enum.Parse<EventKind>(r.GetString(6)),
            DateTimeOffset.Parse(r.GetString(7)),
            r.GetString(8),
            r.GetString(9),
            payload,
            summaries,
            r.IsDBNull(12) ? null : r.GetString(12),
            deps,
            r.IsDBNull(14) ? null : r.GetString(14));
    }

    public (ReviewEvent Event, string Hash) AppendEvent(
        string projectId,
        string branchId,
        EventKind kind,
        string actor,
        string summary,
        object payload,
        IReadOnlyList<string> inputSummaries,
        string? paramsVersion,
        IReadOnlyList<string> parentDependencies,
        string? topic,
        DateTimeOffset now,
        SqliteConnection? existingConn = null,
        SqliteTransaction? existingTx = null)
    {
        var conn = existingConn ?? Open();
        var tx = existingTx ?? conn.BeginTransaction();
        try
        {
            long seq;
            string parentEventId;
            string prevHash;
            using (var head = conn.CreateCommand())
            {
                head.Transaction = tx;
                head.CommandText = """
                    SELECT head_event_id, head_seq, head_hash
                    FROM branches WHERE project_id=$p AND branch_id=$b
                    """;
                head.Parameters.AddWithValue("$p", projectId);
                head.Parameters.AddWithValue("$b", branchId);
                using var hr = head.ExecuteReader();
                if (!hr.Read()) throw new InvalidOperationException($"branch {branchId} missing");
                parentEventId = hr.IsDBNull(0) ? "" : hr.GetString(0);
                seq = hr.GetInt64(1) + 1;
                prevHash = hr.GetString(2);
            }

            var payloadJson = JsonSerializer.Serialize(payload, JsonOpts);
            var summariesJson = JsonSerializer.Serialize(inputSummaries, JsonOpts);
            var depsJson = JsonSerializer.Serialize(parentDependencies, JsonOpts);
            var eventId = "evt-" + Hashing.StableId(
                projectId, branchId, seq, kind.ToString(), payloadJson);

            var hashBase = string.Join("\n",
                prevHash, projectId, branchId, seq.ToString(),
                parentEventId, kind.ToString(), now.ToString("O"),
                actor, summary, payloadJson, summariesJson,
                paramsVersion ?? "", depsJson, topic ?? "");
            var eventHash = Hashing.Sha256Hex(hashBase);

            using (var insert = conn.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = """
                    INSERT INTO events(event_id, project_id, branch_id, seq, parent_event_id,
                        prev_hash, event_hash, kind, occurred_at, actor, summary,
                        payload_json, input_summaries_json, params_version, parent_deps_json, topic)
                    VALUES ($e,$p,$b,$s,$pe,$ph,$h,$k,$at,$a,$su,$pj,$ij,$pv,$dj,$tp)
                    """;
                insert.Parameters.AddWithValue("$e", eventId);
                insert.Parameters.AddWithValue("$p", projectId);
                insert.Parameters.AddWithValue("$b", branchId);
                insert.Parameters.AddWithValue("$s", seq);
                insert.Parameters.AddWithValue("$pe", parentEventId.Length == 0 ? DBNull.Value : parentEventId);
                insert.Parameters.AddWithValue("$ph", prevHash);
                insert.Parameters.AddWithValue("$h", eventHash);
                insert.Parameters.AddWithValue("$k", kind.ToString());
                insert.Parameters.AddWithValue("$at", now.ToString("O"));
                insert.Parameters.AddWithValue("$a", actor);
                insert.Parameters.AddWithValue("$su", summary);
                insert.Parameters.AddWithValue("$pj", payloadJson);
                insert.Parameters.AddWithValue("$ij", summariesJson);
                insert.Parameters.AddWithValue("$pv", (object?)paramsVersion ?? DBNull.Value);
                insert.Parameters.AddWithValue("$dj", depsJson);
                insert.Parameters.AddWithValue("$tp", (object?)topic ?? DBNull.Value);
                insert.ExecuteNonQuery();
            }
            using (var upd = conn.CreateCommand())
            {
                upd.Transaction = tx;
                upd.CommandText = """
                    UPDATE branches SET head_event_id=$e, head_seq=$s, head_hash=$h
                    WHERE project_id=$p AND branch_id=$b;
                    UPDATE projects SET head_event_id=$e, head_seq=$s, head_hash=$h
                    WHERE project_id=$p AND (SELECT name FROM branches WHERE project_id=$p AND branch_id=$b)='main';
                    """;
                upd.Parameters.AddWithValue("$e", eventId);
                upd.Parameters.AddWithValue("$s", seq);
                upd.Parameters.AddWithValue("$h", eventHash);
                upd.Parameters.AddWithValue("$p", projectId);
                upd.Parameters.AddWithValue("$b", branchId);
                upd.ExecuteNonQuery();
            }

            var reviewEvent = new ReviewEvent(eventId, seq, branchId,
                parentEventId.Length == 0 ? null : parentEventId,
                prevHash, eventHash, kind, now, actor, summary,
                JsonDocument.Parse(payloadJson).RootElement.Clone(),
                inputSummaries, paramsVersion, parentDependencies, topic);

            if (existingTx is null) tx.Commit();
            return (reviewEvent, eventHash);
        }
        catch
        {
            if (existingTx is null) tx.Rollback();
            throw;
        }
        finally
        {
            if (existingConn is null) conn.Dispose();
        }
    }

    public IReadOnlyList<(string BranchId, string Name, string? Parent, string? ForkEvent, long Seq)>
        ListBranches(string projectId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT branch_id, name, parent_branch_id, fork_event_id, head_seq
            FROM branches WHERE project_id=$p ORDER BY created_at
            """;
        cmd.Parameters.AddWithValue("$p", projectId);
        var list = new List<(string, string, string?, string?, long)>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add((r.GetString(0), r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.GetInt64(4)));
        }
        return list;
    }

    public void CreateBranch(string projectId, string newBranchId, string name,
        string fromBranchId, string fromEventId, DateTimeOffset now)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        string headHash = "";
        long headSeq = -1;
        using (var q = conn.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = """
                SELECT head_seq, head_hash FROM branches
                WHERE project_id=$p AND branch_id=$b
                """;
            q.Parameters.AddWithValue("$p", projectId);
            q.Parameters.AddWithValue("$b", fromBranchId);
            using var rr = q.ExecuteReader();
            if (!rr.Read()) throw new InvalidOperationException("source branch missing");
            headSeq = rr.GetInt64(0);
            headHash = rr.GetString(1);
        }
        var sourceEvents = ReadEvents(conn, projectId, fromBranchId)
            .TakeWhile(e => e.EventId != fromEventId)
            .ToList();
        var upTo = sourceEvents;
        var fork = ReadEvents(conn, projectId, fromBranchId)
            .TakeWhileInclusive(e => e.EventId == fromEventId).ToList();

        long newSeq = 0;
        string prevHash = "";
        string? lastEventId = null;
        foreach (var e in fork)
        {
            var copy = CloneEventToBranch(conn, tx, projectId, e, newBranchId, newSeq, prevHash, lastEventId);
            prevHash = copy.Hash;
            lastEventId = copy.Event.EventId;
            newSeq++;
        }

        using var ins = conn.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = """
            INSERT INTO branches(branch_id, project_id, name, parent_branch_id, fork_event_id,
                head_event_id, head_seq, head_hash, created_at)
            VALUES ($b,$p,$n,$pb,$fe,$he,$hs,$hh,$at)
            """;
        ins.Parameters.AddWithValue("$b", newBranchId);
        ins.Parameters.AddWithValue("$p", projectId);
        ins.Parameters.AddWithValue("$n", name);
        ins.Parameters.AddWithValue("$pb", fromBranchId);
        ins.Parameters.AddWithValue("$fe", fromEventId);
        ins.Parameters.AddWithValue("$he", (object?)lastEventId ?? DBNull.Value);
        ins.Parameters.AddWithValue("$hs", newSeq - 1);
        ins.Parameters.AddWithValue("$hh", prevHash);
        ins.Parameters.AddWithValue("$at", now.ToString("O"));
        ins.ExecuteNonQuery();

        CopyAnalysesToBranch(conn, tx, projectId, fromBranchId, newBranchId, fork.Select(e => e.EventId).ToHashSet());
        tx.Commit();
    }

    private (ReviewEvent Event, string Hash, string EventId) CloneEventToBranch(
        SqliteConnection conn, SqliteTransaction tx, string projectId, ReviewEvent source,
        string branchId, long seq, string prevHash, string? parentEventId)
    {
        var payloadJson = JsonSerializer.Serialize(source.Payload, JsonOpts);
        var summariesJson = JsonSerializer.Serialize(source.InputSummaries, JsonOpts);
        var depsJson = JsonSerializer.Serialize(source.ParentDependencies, JsonOpts);
        var hashBase = string.Join("\n",
            prevHash, projectId, branchId, seq.ToString(),
            parentEventId ?? "", source.Kind.ToString(), source.OccurredAt.ToString("O"),
            source.Actor, source.Summary, payloadJson, summariesJson,
            source.ParamsVersion ?? "", depsJson, source.Topic ?? "");
        var hash = Hashing.Sha256Hex(hashBase);
        var eventId = source.EventId;

        using var ins = conn.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = """
            INSERT INTO events(event_id, project_id, branch_id, seq, parent_event_id,
                prev_hash, event_hash, kind, occurred_at, actor, summary,
                payload_json, input_summaries_json, params_version, parent_deps_json, topic)
            VALUES ($e,$p,$b,$s,$pe,$ph,$h,$k,$at,$a,$su,$pj,$ij,$pv,$dj,$tp)
            """;
        ins.Parameters.AddWithValue("$e", eventId);
        ins.Parameters.AddWithValue("$p", projectId);
        ins.Parameters.AddWithValue("$b", branchId);
        ins.Parameters.AddWithValue("$s", seq);
        ins.Parameters.AddWithValue("$pe", (object?)parentEventId ?? DBNull.Value);
        ins.Parameters.AddWithValue("$ph", prevHash);
        ins.Parameters.AddWithValue("$h", hash);
        ins.Parameters.AddWithValue("$k", source.Kind.ToString());
        ins.Parameters.AddWithValue("$at", source.OccurredAt.ToString("O"));
        ins.Parameters.AddWithValue("$a", source.Actor);
        ins.Parameters.AddWithValue("$su", source.Summary);
        ins.Parameters.AddWithValue("$pj", payloadJson);
        ins.Parameters.AddWithValue("$ij", summariesJson);
        ins.Parameters.AddWithValue("$pv", (object?)source.ParamsVersion ?? DBNull.Value);
        ins.Parameters.AddWithValue("$dj", depsJson);
        ins.Parameters.AddWithValue("$tp", (object?)source.Topic ?? DBNull.Value);
        ins.ExecuteNonQuery();

        return (source, hash, eventId);
    }
}

internal static class LinqExtensions
{
    public static IEnumerable<T> TakeWhileInclusive<T>(
        this IEnumerable<T> source, Func<T, bool> predicate)
    {
        foreach (var item in source)
        {
            yield return item;
            if (!predicate(item)) yield break;
        }
    }
}
