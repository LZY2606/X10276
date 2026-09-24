using System.Text.Json;
using EpubReview.Domain;
using Microsoft.Data.Sqlite;

namespace EpubReview.Web.Services;

public sealed class ReviewRepository
{
    private readonly Database _database;
    public ReviewRepository(Database database) { _database = database; }

    public bool CaseExists(string caseId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM cases WHERE case_id=$id";
        command.Parameters.AddWithValue("$id", caseId);
        return command.ExecuteScalar() is not null;
    }

    public ProjectExport ExportCase(string caseId)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        var caseRecord = GetCase(connection, caseId) ?? throw new InvalidOperationException("Case not found.");
        var events = QueryEvents(connection, caseId).Select(ToProjectEvent).ToList();
        var materials = QueryMaterials(connection, caseId).Select(ToProjectMaterial).ToList();
        transaction.Commit();
        return new("epub-review/offline/1", caseRecord, events, materials);
    }

    public string ImportCase(ProjectExport export)
    {
        var operationId = Guid.NewGuid().ToString("N");
        using var connection = _database.Open();
        BeginLedger(connection, operationId, export.Case.CaseId);
        using var transaction = connection.BeginTransaction();
        try
        {
            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM cases WHERE case_id=$id";
                delete.Parameters.AddWithValue("$id", export.Case.CaseId);
                delete.ExecuteNonQuery();
            }
            using (var insertCase = connection.CreateCommand())
            {
                insertCase.Transaction = transaction;
                insertCase.CommandText = "INSERT INTO cases(case_id,title,created_event_id,created_at) VALUES($id,$title,$event,$at)";
                insertCase.Parameters.AddWithValue("$id", export.Case.CaseId);
                insertCase.Parameters.AddWithValue("$title", export.Case.Title);
                insertCase.Parameters.AddWithValue("$event", export.Case.CreatedEventId);
                insertCase.Parameters.AddWithValue("$at", export.Case.CreatedAt);
                insertCase.ExecuteNonQuery();
            }
            foreach (var e in export.Events.OrderBy(e => e.Sequence)) InsertEvent(connection, transaction, export.Case.CaseId, e);
            foreach (var m in export.Materials) InsertMaterial(connection, transaction, export.Case.CaseId, m);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            FinishLedger(connection, operationId, "rolled-back", "Import validation or write failed; all import writes were rolled back.");
            throw;
        }
        FinishLedger(connection, operationId, "committed", null);
        return export.Case.CaseId;
    }

    public T InTransaction<T>(string caseId, Func<SqliteConnection, SqliteTransaction, T> action)
    {
        var operationId = Guid.NewGuid().ToString("N");
        using var connection = _database.Open();
        BeginLedger(connection, operationId, caseId);
        using var transaction = connection.BeginTransaction();
        try
        {
            var result = action(connection, transaction);
            transaction.Commit();
            FinishLedger(connection, operationId, "committed", null);
            return result;
        }
        catch
        {
            transaction.Rollback();
            FinishLedger(connection, operationId, "rolled-back", "Operation failed before commit; no event or material was partially retained.");
            throw;
        }
    }

    public List<EventRecord> GetEvents(string caseId) { using var c = _database.Open(); return QueryEvents(c, caseId); }
    public List<StoredMaterial> GetMaterials(string caseId) { using var c = _database.Open(); return QueryMaterials(c, caseId); }

    public static void InsertCase(SqliteConnection c, SqliteTransaction t, string caseId, string title, string createdEventId, string createdAt)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = t;
        cmd.CommandText = "INSERT INTO cases(case_id,title,created_event_id,created_at) VALUES($id,$title,$event,$at)";
        cmd.Parameters.AddWithValue("$id", caseId);
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$event", createdEventId);
        cmd.Parameters.AddWithValue("$at", createdAt);
        cmd.ExecuteNonQuery();
    }

    public static void InsertEvent(SqliteConnection c, SqliteTransaction t, string caseId, ProjectEvent e)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = t;
        cmd.CommandText = """
INSERT INTO events(event_id,case_id,sequence,type,occurred_at,parameters_version,parent_dependency_version,input_sha256,dependency_version,payload_json,status)
VALUES($eventId,$caseId,$seq,$type,$at,$params,$parent,$input,$dep,$payload,$status)
""";
        cmd.Parameters.AddWithValue("$eventId", e.EventId);
        cmd.Parameters.AddWithValue("$caseId", caseId);
        cmd.Parameters.AddWithValue("$seq", e.Sequence);
        cmd.Parameters.AddWithValue("$type", e.Type);
        cmd.Parameters.AddWithValue("$at", e.OccurredAt);
        cmd.Parameters.AddWithValue("$params", e.ParametersVersion);
        cmd.Parameters.AddWithValue("$parent", (object?)e.ParentDependencyVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$input", (object?)e.InputSha256 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dep", (object?)e.DependencyVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$payload", e.PayloadJson);
        cmd.Parameters.AddWithValue("$status", e.Status);
        cmd.ExecuteNonQuery();
    }

    public static void InsertMaterial(SqliteConnection c, SqliteTransaction t, string caseId, ProjectMaterial m)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = t;
        cmd.CommandText = """
INSERT INTO materials(material_id,case_id,role,raw_path,normalized_path,media_type,byte_length,sha256,parent_material_id,created_event_id,bytes)
VALUES($materialId,$caseId,$role,$raw,$norm,$media,$bytes,$sha,$parent,$event,$blob)
""";
        cmd.Parameters.AddWithValue("$materialId", m.MaterialId);
        cmd.Parameters.AddWithValue("$caseId", caseId);
        cmd.Parameters.AddWithValue("$role", m.Role);
        cmd.Parameters.AddWithValue("$raw", m.RawPath);
        cmd.Parameters.AddWithValue("$norm", (object?)m.NormalizedPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$media", (object?)m.MediaType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$bytes", m.ByteLength);
        cmd.Parameters.AddWithValue("$sha", m.Sha256);
        cmd.Parameters.AddWithValue("$parent", (object?)m.ParentMaterialId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$event", m.CreatedEventId);
        cmd.Parameters.AddWithValue("$blob", m.Bytes);
        cmd.ExecuteNonQuery();
    }

    private static ProjectCase? GetCase(SqliteConnection c, string caseId)
    {
        using var q = c.CreateCommand();
        q.CommandText = "SELECT case_id,title,created_event_id,created_at FROM cases WHERE case_id=$id";
        q.Parameters.AddWithValue("$id", caseId);
        using var r = q.ExecuteReader();
        return r.Read() ? new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)) : null;
    }

    private static List<EventRecord> QueryEvents(SqliteConnection c, string caseId)
    {
        using var q = c.CreateCommand();
        q.CommandText = "SELECT event_id,sequence,type,occurred_at,parameters_version,parent_dependency_version,input_sha256,dependency_version,payload_json,status FROM events WHERE case_id=$id ORDER BY sequence";
        q.Parameters.AddWithValue("$id", caseId);
        using var r = q.ExecuteReader();
        var result = new List<EventRecord>();
        while (r.Read()) result.Add(new(r.GetString(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.GetString(8), r.GetString(9)));
        return result;
    }

    private static List<StoredMaterial> QueryMaterials(SqliteConnection c, string caseId)
    {
        using var q = c.CreateCommand();
        q.CommandText = "SELECT material_id,case_id,role,raw_path,normalized_path,media_type,byte_length,sha256,parent_material_id,created_event_id,bytes FROM materials WHERE case_id=$id ORDER BY material_id,created_event_id";
        q.Parameters.AddWithValue("$id", caseId);
        using var r = q.ExecuteReader();
        var result = new List<StoredMaterial>();
        while (r.Read()) result.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetInt64(6), r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8), r.GetString(9), (byte[])r[10]));
        return result;
    }

    private static ProjectEvent ToProjectEvent(EventRecord e) => new(e.EventId, e.Sequence, e.Type, e.OccurredAt, e.ParametersVersion, e.ParentDependencyVersion, e.InputSha256, e.DependencyVersion, e.PayloadJson, e.Status);
    private static ProjectMaterial ToProjectMaterial(StoredMaterial m) => new(m.MaterialId, m.Role, m.RawPath, m.NormalizedPath, m.MediaType, m.ByteLength, m.Sha256, m.ParentMaterialId, m.CreatedEventId, m.Bytes);

    private void BeginLedger(SqliteConnection c, string operationId, string caseId)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO operation_ledger(operation_id,case_id,started_at,status) VALUES($id,$case,$at,'started')";
        cmd.Parameters.AddWithValue("$id", operationId);
        cmd.Parameters.AddWithValue("$case", caseId);
        cmd.Parameters.AddWithValue("$at", SystemClock.UtcNow());
        cmd.ExecuteNonQuery();
    }

    private void FinishLedger(SqliteConnection c, string operationId, string status, string? detail)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE operation_ledger SET finished_at=$at,status=$status,detail=$detail WHERE operation_id=$id";
        cmd.Parameters.AddWithValue("$at", SystemClock.UtcNow());
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", operationId);
        cmd.ExecuteNonQuery();
    }
}
