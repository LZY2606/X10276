using Microsoft.Data.Sqlite;

namespace EpubReview.Web.Services;

public sealed class Database
{
    private readonly string _connectionString;
    public Database(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, ForeignKeys = true }.ToString();
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
CREATE TABLE IF NOT EXISTS cases (
  case_id TEXT PRIMARY KEY,
  title TEXT NOT NULL,
  created_event_id TEXT NOT NULL,
  created_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS materials (
  material_id TEXT NOT NULL,
  case_id TEXT NOT NULL REFERENCES cases(case_id) ON DELETE CASCADE,
  role TEXT NOT NULL,
  raw_path TEXT NOT NULL,
  normalized_path TEXT,
  media_type TEXT,
  byte_length INTEGER NOT NULL,
  sha256 TEXT NOT NULL,
  parent_material_id TEXT,
  created_event_id TEXT NOT NULL,
  bytes BLOB NOT NULL,
  PRIMARY KEY (case_id, material_id, created_event_id)
);
CREATE TABLE IF NOT EXISTS events (
  event_id TEXT NOT NULL,
  case_id TEXT NOT NULL REFERENCES cases(case_id) ON DELETE CASCADE,
  sequence INTEGER NOT NULL,
  type TEXT NOT NULL,
  occurred_at TEXT NOT NULL,
  parameters_version TEXT NOT NULL,
  parent_dependency_version TEXT,
  input_sha256 TEXT,
  dependency_version TEXT,
  payload_json TEXT NOT NULL,
  status TEXT NOT NULL,
  PRIMARY KEY (case_id, event_id),
  UNIQUE (case_id, sequence)
);
CREATE TABLE IF NOT EXISTS operation_ledger (
  operation_id TEXT PRIMARY KEY,
  case_id TEXT NOT NULL,
  started_at TEXT NOT NULL,
  finished_at TEXT,
  status TEXT NOT NULL,
  detail TEXT
);
""";
        command.ExecuteNonQuery();
        RecoverInterruptedOperations(connection);
    }

    private static void RecoverInterruptedOperations(SqliteConnection connection)
    {
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT operation_id FROM operation_ledger WHERE status='started'";
        var stale = new List<string>();
        using (var reader = query.ExecuteReader()) while (reader.Read()) stale.Add(reader.GetString(0));
        foreach (var operationId in stale)
        {
            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE operation_ledger SET status='recovered', finished_at=$time, detail='Interrupted SQLite transaction was rolled back.' WHERE operation_id=$id";
            update.Parameters.AddWithValue("$time", Domain.SystemClock.UtcNow());
            update.Parameters.AddWithValue("$id", operationId);
            update.ExecuteNonQuery();
        }
    }
}
