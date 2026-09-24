using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace EpubReview.Core;

public sealed record StoredEvent(
    string Id, long Seq, string? ParentId, string Kind, string PayloadJson,
    string InputDigest, string ParameterVersion, string CreatedUtc,
    long? ValidToSeq, string? InvalidationReason);

public sealed record ExportArchive(
    int FormatVersion,
    IReadOnlyList<Material> Materials,
    IReadOnlyList<StoredEvent> Events,
    IReadOnlyDictionary<string, string> Results);

public sealed record ImportReport(int Materials, int Events, int Results, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// SQLite 事件存储。材料不可变；决定以事件追加；每个计算结果记录输入摘要、
/// 参数版本与父级依赖。所有多行写入都在事务内，半写会被回滚或在打开时隔离。
/// </summary>
public sealed class EventStore : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly SqliteConnection _db;

    public EventStore(string path)
    {
        _db = new SqliteConnection($"Data Source={path}");
        _db.Open();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS materials(
                id TEXT PRIMARY KEY, kind TEXT NOT NULL, media_type TEXT NOT NULL,
                bytes BLOB NOT NULL, original_name TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS events(
                id TEXT PRIMARY KEY, seq INTEGER NOT NULL UNIQUE, parent_id TEXT,
                kind TEXT NOT NULL, payload TEXT NOT NULL, input_digest TEXT NOT NULL,
                param_version TEXT NOT NULL, created_utc TEXT NOT NULL,
                valid_to_seq INTEGER, invalidation_reason TEXT);
            CREATE TABLE IF NOT EXISTS results(
                event_id TEXT PRIMARY KEY, status TEXT NOT NULL, payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS quarantine(
                id TEXT PRIMARY KEY, payload TEXT NOT NULL, reason TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
        Recover();
    }

    public static EventStore InMemory()
    {
        var path = Path.Combine(Path.GetTempPath(), "epubreview-" + Guid.NewGuid().ToString("N") + ".db");
        return new EventStore(path);
    }

    /// <summary>半写恢复：把引用完整性受损的行隔离到 quarantine，而不是崩溃或静默吞掉。</summary>
    public IReadOnlyList<Diagnostic> Recover()
    {
        var diags = new List<Diagnostic>();
        using var tx = _db.BeginTransaction();
        Quarantine(tx, """
            SELECT id, payload FROM events e
            WHERE e.parent_id IS NOT NULL AND NOT EXISTS(SELECT 1 FROM events p WHERE p.id = e.parent_id)
            """, "半写恢复：事件的父级依赖缺失", diags);
        Quarantine(tx, """
            SELECT event_id, payload FROM results r
            WHERE NOT EXISTS(SELECT 1 FROM events e WHERE e.id = r.event_id)
            """, "半写恢复：结果所属事件缺失", diags, deleteFrom: "results", deleteKey: "event_id");
        tx.Commit();
        return diags;
    }

    private void Quarantine(SqliteTransaction tx, string selectSql, string reason,
        List<Diagnostic> diags, string deleteFrom = "events", string deleteKey = "id")
    {
        var orphans = new List<(string Id, string Payload)>();
        using (var cmd = _db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = selectSql;
            using var r = cmd.ExecuteReader();
            while (r.Read()) orphans.Add((r.GetString(0), r.GetString(1)));
        }
        foreach (var (id, payload) in orphans)
        {
            using var ins = _db.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT OR REPLACE INTO quarantine(id,payload,reason) VALUES($id,$p,$r)";
            ins.Parameters.AddWithValue("$id", "q-" + id);
            ins.Parameters.AddWithValue("$p", payload);
            ins.Parameters.AddWithValue("$r", reason);
            ins.ExecuteNonQuery();
            using var del = _db.CreateCommand();
            del.Transaction = tx;
            del.CommandText = $"DELETE FROM {deleteFrom} WHERE {deleteKey}=$id";
            del.Parameters.AddWithValue("$id", id);
            del.ExecuteNonQuery();
            diags.Add(new Diagnostic("HALF_WRITE_RECOVERED", DiagnosticSeverity.Warning,
                $"{reason}：{id}（已隔离至 quarantine）", null, new[] { id }));
        }
    }

    public void SaveMaterials(IEnumerable<Material> materials)
    {
        using var tx = _db.BeginTransaction();
        foreach (var m in materials)
        {
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR IGNORE INTO materials(id,kind,media_type,bytes,original_name) VALUES($id,$k,$t,$b,$n)";
            cmd.Parameters.AddWithValue("$id", m.Id);
            cmd.Parameters.AddWithValue("$k", m.Kind);
            cmd.Parameters.AddWithValue("$t", m.MediaType);
            cmd.Parameters.AddWithValue("$b", m.Bytes);
            cmd.Parameters.AddWithValue("$n", m.OriginalName);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public IReadOnlyList<Material> GetMaterials()
    {
        var list = new List<Material>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT id,kind,media_type,bytes,original_name FROM materials ORDER BY original_name";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new Material(r.GetString(0), r.GetString(1), r.GetString(2),
                (byte[])r.GetValue(3), r.GetString(4)));
        return list;
    }

    /// <summary>追加事件并原子地保存其计算结果；同一事务，半写即整体回滚。</summary>
    public StoredEvent AppendEvent(string kind, object payload, string? parentId,
        string inputDigest, string paramVersion, AnalysisResult? result)
    {
        var payloadJson = JsonSerializer.Serialize(payload, Json);
        using var tx = _db.BeginTransaction();
        long seq;
        using (var cmd = _db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT COALESCE(MAX(seq),0)+1 FROM events";
            seq = (long)cmd.ExecuteScalar()!;
        }
        var id = "evt-" + seq.ToString("D4") + "-" +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson + inputDigest + paramVersion)))[..6].ToLowerInvariant();
        var created = DateTime.UtcNow.ToString("O");
        using (var cmd = _db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO events(id,seq,parent_id,kind,payload,input_digest,param_version,created_utc)
                VALUES($id,$seq,$parent,$kind,$payload,$digest,$params,$created)
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$seq", seq);
            cmd.Parameters.AddWithValue("$parent", (object?)parentId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$kind", kind);
            cmd.Parameters.AddWithValue("$payload", payloadJson);
            cmd.Parameters.AddWithValue("$digest", inputDigest);
            cmd.Parameters.AddWithValue("$params", paramVersion);
            cmd.Parameters.AddWithValue("$created", created);
            cmd.ExecuteNonQuery();
        }
        if (result is not null)
        {
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO results(event_id,status,payload) VALUES($id,$s,$p)";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$s", result.Status.ToString());
            cmd.Parameters.AddWithValue("$p", JsonSerializer.Serialize(result, Json));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return new StoredEvent(id, seq, parentId, kind, payloadJson, inputDigest, paramVersion, created, null, null);
    }

    /// <summary>使旧事件失效：记录有效区间终点与失效原因，历史结论不被覆盖。</summary>
    public void Invalidate(string eventId, long atSeq, string reason)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "UPDATE events SET valid_to_seq=$s, invalidation_reason=$r WHERE id=$id";
        cmd.Parameters.AddWithValue("$s", atSeq);
        cmd.Parameters.AddWithValue("$r", reason);
        cmd.Parameters.AddWithValue("$id", eventId);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<StoredEvent> GetEvents()
    {
        var list = new List<StoredEvent>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT id,seq,parent_id,kind,payload,input_digest,param_version,created_utc,valid_to_seq,invalidation_reason FROM events ORDER BY seq";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new StoredEvent(r.GetString(0), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetString(2),
                r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7),
                r.IsDBNull(8) ? null : r.GetInt64(8), r.IsDBNull(9) ? null : r.GetString(9)));
        return list;
    }

    public AnalysisResult? GetResult(string eventId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT payload FROM results WHERE event_id=$id";
        cmd.Parameters.AddWithValue("$id", eventId);
        var json = cmd.ExecuteScalar() as string;
        return json is null ? null : JsonSerializer.Deserialize<AnalysisResult>(json, Json);
    }

    public string? GetResultStatus(string eventId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT status FROM results WHERE event_id=$id";
        cmd.Parameters.AddWithValue("$id", eventId);
        return cmd.ExecuteScalar() as string;
    }

    // ---- 导出 / 导入 ----

    public void Export(string path)
    {
        var results = new Dictionary<string, string>();
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "SELECT event_id, payload FROM results";
            using var r = cmd.ExecuteReader();
            while (r.Read()) results[r.GetString(0)] = r.GetString(1);
        }
        var archive = new ExportArchive(1, GetMaterials(), GetEvents(), results);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(archive, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true); // 原子替换，避免半写导出文件
    }

    /// <summary>导入档案：身份、分支与决定历史原样恢复；引用受损的行进入 quarantine。</summary>
    public ImportReport Import(string path)
    {
        ExportArchive archive;
        try
        {
            archive = JsonSerializer.Deserialize<ExportArchive>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException("空档案");
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
        {
            return new ImportReport(0, 0, 0, new[]
            {
                new Diagnostic("IMPORT_CORRUPT", DiagnosticSeverity.Error,
                    $"导出档案损坏，导入被拒绝（数据库未改动）：{ex.Message}", null, new[] { path })
            });
        }

        using var tx = _db.BeginTransaction();
        var materialIds = new HashSet<string>(archive.Materials.Select(m => m.Id), StringComparer.Ordinal);
        foreach (var m in archive.Materials)
        {
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR IGNORE INTO materials(id,kind,media_type,bytes,original_name) VALUES($id,$k,$t,$b,$n)";
            cmd.Parameters.AddWithValue("$id", m.Id);
            cmd.Parameters.AddWithValue("$k", m.Kind);
            cmd.Parameters.AddWithValue("$t", m.MediaType);
            cmd.Parameters.AddWithValue("$b", m.Bytes);
            cmd.Parameters.AddWithValue("$n", m.OriginalName);
            cmd.ExecuteNonQuery();
        }
        var eventIds = new HashSet<string>(archive.Events.Select(e => e.Id), StringComparer.Ordinal);
        var diags = new List<Diagnostic>();
        var importedEvents = 0;
        foreach (var e in archive.Events.OrderBy(e => e.Seq))
        {
            if (e.ParentId is not null && !eventIds.Contains(e.ParentId))
            {
                diags.Add(new Diagnostic("IMPORT_ORPHAN_EVENT", DiagnosticSeverity.Warning,
                    $"事件 {e.Id} 的父级 {e.ParentId} 不在档案中，已隔离", null, new[] { e.Id }));
                QuarantineRow(tx, e.Id, JsonSerializer.Serialize(e, Json), "导入时父级依赖缺失");
                continue;
            }
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR IGNORE INTO events(id,seq,parent_id,kind,payload,input_digest,param_version,created_utc,valid_to_seq,invalidation_reason)
                VALUES($id,$seq,$parent,$kind,$payload,$digest,$params,$created,$vts,$reason)
                """;
            cmd.Parameters.AddWithValue("$id", e.Id);
            cmd.Parameters.AddWithValue("$seq", e.Seq);
            cmd.Parameters.AddWithValue("$parent", (object?)e.ParentId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$kind", e.Kind);
            cmd.Parameters.AddWithValue("$payload", e.PayloadJson);
            cmd.Parameters.AddWithValue("$digest", e.InputDigest);
            cmd.Parameters.AddWithValue("$params", e.ParameterVersion);
            cmd.Parameters.AddWithValue("$created", e.CreatedUtc);
            cmd.Parameters.AddWithValue("$vts", (object?)e.ValidToSeq ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$reason", (object?)e.InvalidationReason ?? DBNull.Value);
            cmd.ExecuteNonQuery();
            importedEvents++;
        }
        var importedResults = 0;
        foreach (var (eventId, payload) in archive.Results)
        {
            if (!eventIds.Contains(eventId))
            {
                diags.Add(new Diagnostic("IMPORT_ORPHAN_RESULT", DiagnosticSeverity.Warning,
                    $"结果所属事件 {eventId} 不在档案中，已隔离", null, new[] { eventId }));
                QuarantineRow(tx, "r-" + eventId, payload, "导入时结果所属事件缺失");
                continue;
            }
            var status = JsonSerializer.Deserialize<AnalysisResult>(payload, Json)?.Status.ToString() ?? "Ok";
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR IGNORE INTO results(event_id,status,payload) VALUES($id,$s,$p)";
            cmd.Parameters.AddWithValue("$id", eventId);
            cmd.Parameters.AddWithValue("$s", status);
            cmd.Parameters.AddWithValue("$p", payload);
            cmd.ExecuteNonQuery();
            importedResults++;
        }
        tx.Commit();
        return new ImportReport(archive.Materials.Count, importedEvents, importedResults, diags);
    }

    private void QuarantineRow(SqliteTransaction tx, string id, string payload, string reason)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR REPLACE INTO quarantine(id,payload,reason) VALUES($id,$p,$r)";
        cmd.Parameters.AddWithValue("$id", "q-" + id);
        cmd.Parameters.AddWithValue("$p", payload);
        cmd.Parameters.AddWithValue("$r", reason);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<(string Id, string Reason)> GetQuarantine()
    {
        var list = new List<(string, string)>();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT id, reason FROM quarantine ORDER BY id";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), r.GetString(1)));
        return list;
    }

    public void Dispose() => _db.Dispose();
}
