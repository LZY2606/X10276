using System.Data;
using System.Text.Json;
using EpubReview.Core;
using EpubReview.Core.Events;
using Microsoft.Data.Sqlite;

namespace EpubReview.Storage;

public sealed class MaterialBlob
{
    public required string MaterialSetId { get; init; }
    public required string ContentVersion { get; init; }
    public required byte[] ZipBytes { get; init; }
}

public sealed class AnalysisBlob
{
    public required string AnalysisId { get; init; }
    public required string DependencyVersion { get; init; }
    public required string MaterialSetId { get; init; }
    public required string ParamsVersionId { get; init; }
    public required string Json { get; init; }
    public required string BranchId { get; init; }
}

public sealed partial class ReviewRepository
{
    private readonly string _connectionString;

    public ReviewRepository(string dbPath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true
        }.ToString();
        DbPath = dbPath;
    }

    public string DbPath { get; }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    public void InitSchema()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = Schema.Sql;
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<string> ListProjectIds()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT project_id FROM projects ORDER BY created_at, project_id";
        var ids = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) ids.Add(r.GetString(0));
        return ids;
    }

    public string CreateProject(string projectId, string title, DateTimeOffset now)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO projects(project_id, title, created_at, head_event_id, head_seq, head_hash)
                VALUES ($id, $title, $at, NULL, -1, '')
                """;
            cmd.Parameters.AddWithValue("$id", projectId);
            cmd.Parameters.AddWithValue("$title", title);
            cmd.Parameters.AddWithValue("$at", now.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO branches(branch_id, project_id, name, parent_branch_id, fork_event_id, head_event_id, head_seq, head_hash, created_at)
                VALUES ($b, $p, 'main', NULL, NULL, NULL, -1, '', $at)
                """;
            cmd.Parameters.AddWithValue("$b", "main");
            cmd.Parameters.AddWithValue("$p", projectId);
            cmd.Parameters.AddWithValue("$at", now.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return projectId;
    }

    public (string? Title, DateTimeOffset? CreatedAt) GetProjectMeta(string projectId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT title, created_at FROM projects WHERE project_id=$id";
        cmd.Parameters.AddWithValue("$id", projectId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return (null, null);
        return (r.GetString(0), DateTimeOffset.Parse(r.GetString(1)));
    }

    public void RenameProject(string projectId, string title)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE projects SET title=$t WHERE project_id=$id";
        cmd.Parameters.AddWithValue("$t", title);
        cmd.Parameters.AddWithValue("$id", projectId);
        cmd.ExecuteNonQuery();
    }
}
