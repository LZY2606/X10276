using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace EpubReview.Core;

public sealed class CaseStore : IDisposable
{
    readonly SqliteConnection _connection;

    public CaseStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        CleanStagingArtifacts(databasePath);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate, ForeignKeys = true }.ToString());
        _connection.Open();
        Initialize();
    }

    public string DatabasePath => ((SqliteConnectionStringBuilder)_connection.DataSource).DataSource!;

    public void SaveBundle(CaseBundle bundle)
    {
        using var transaction = _connection.BeginTransaction();
        var json = JsonSerializer.Serialize(bundle, JsonOptions.Indented);
        using (var delete = _connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "delete from cases where case_id = $id";
            delete.Parameters.AddWithValue("$id", bundle.CaseId);
            delete.ExecuteNonQuery();
        }
        using var insert = _connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "insert into cases(case_id, title, schema_version, payload_json, updated_at) values ($id, $title, $schema, $payload, $updated)";
        insert.Parameters.AddWithValue("$id", bundle.CaseId);
        insert.Parameters.AddWithValue("$title", bundle.Title);
        insert.Parameters.AddWithValue("$schema", bundle.SchemaVersion);
        insert.Parameters.AddWithValue("$payload", json);
        insert.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        insert.ExecuteNonQuery();
        transaction.Commit();
    }

    public CaseBundle? GetCase(string caseId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "select payload_json from cases where case_id = $id";
        command.Parameters.AddWithValue("$id", caseId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? JsonSerializer.Deserialize<CaseBundle>(reader.GetString(0), JsonOptions.Indented) : null;
    }

    public IReadOnlyList<(string CaseId, string Title, int EventCount)> ListCases()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "select case_id,title,payload_json from cases order by title";
        using var reader = command.ExecuteReader();
        var rows = new List<(string, string, int)>();
        while (reader.Read())
        {
            var bundle = JsonSerializer.Deserialize<CaseBundle>(reader.GetString(2), JsonOptions.Indented)!;
            rows.Add((reader.GetString(0), reader.GetString(1), bundle.Events.Count));
        }
        return rows;
    }

    public void DeleteCase(string caseId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "delete from cases where case_id = $id";
        command.Parameters.AddWithValue("$id", caseId);
        command.ExecuteNonQuery();
    }

    public void ReplaceFromExport(Stream exportStream)
    {
        var bundle = JsonSerializer.Deserialize<CaseBundle>(exportStream, JsonOptions.Indented) ?? throw new InvalidDataException("Export contains no case bundle.");
        var directory = Path.GetDirectoryName(Path.GetFullPath(DatabasePath))!;
        var staging = Path.Combine(directory, $".{Guid.NewGuid():N}.restore.db");
        try
        {
            using (var stagingConnection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = staging, Mode = SqliteOpenMode.ReadWriteCreate }.ToString()))
            {
                stagingConnection.Open();
                using (var command = stagingConnection.CreateCommand())
                {
                    command.CommandText = SchemaSql;
                    command.ExecuteNonQuery();
                }
                var json = JsonSerializer.Serialize(bundle, JsonOptions.Indented);
                using var insert = stagingConnection.CreateCommand();
                insert.CommandText = "insert into cases(case_id,title,schema_version,payload_json,updated_at) values($id,$title,$schema,$payload,$updated)";
                insert.Parameters.AddWithValue("$id", bundle.CaseId);
                insert.Parameters.AddWithValue("$title", bundle.Title);
                insert.Parameters.AddWithValue("$schema", bundle.SchemaVersion);
                insert.Parameters.AddWithValue("$payload", json);
                insert.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
                insert.ExecuteNonQuery();
            }
            SqliteConnection.ClearPool(_connection);
            _connection.Dispose();
            File.Move(staging, DatabasePath, overwrite: true);
            var reopened = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, ForeignKeys = true }.ToString());
            reopened.Open();
            typeof(CaseStore).GetField("_connection", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(this, reopened);
        }
        catch
        {
            if (File.Exists(staging)) File.Delete(staging);
            throw;
        }
    }

    public byte[] ExportCase(string caseId)
    {
        var bundle = GetCase(caseId) ?? throw new FileNotFoundException($"Case {caseId} is not present.");
        return JsonSerializer.SerializeToUtf8Bytes(bundle, JsonOptions.Indented);
    }

    void Initialize()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = SchemaSql;
        command.ExecuteNonQuery();
    }

    static void CleanStagingArtifacts(string databasePath)
    {
        var full = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(full)!;
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, ".*.restore.db"))
        {
            try { File.Delete(file); } catch { }
        }
        foreach (var file in Directory.EnumerateFiles(directory, ".*.restore.db-*"))
        {
            try { File.Delete(file); } catch { }
        }
    }

    const string SchemaSql = """
        pragma journal_mode=WAL;
        create table if not exists cases (
          case_id text primary key,
          title text not null,
          schema_version integer not null,
          payload_json text not null,
          updated_at text not null
        );
        """;

    public void Dispose() => _connection.Dispose();
}
