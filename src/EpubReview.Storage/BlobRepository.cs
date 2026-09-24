using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace EpubReview.Storage;

public sealed partial class ReviewRepository
{
    public void SaveMaterial(string projectId, string materialSetId,
        string contentVersion, byte[] zipBytes, DateTimeOffset now,
        SqliteConnection? conn = null, SqliteTransaction? tx = null)
    {
        var own = conn is null;
        conn ??= Open();
        tx ??= conn.BeginTransaction();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR REPLACE INTO materials
                    (material_set_id, project_id, content_version, zip_bytes, created_at)
                VALUES ($m,$p,$c,$z,$at)
                """;
            cmd.Parameters.AddWithValue("$m", materialSetId);
            cmd.Parameters.AddWithValue("$p", projectId);
            cmd.Parameters.AddWithValue("$c", contentVersion);
            cmd.Parameters.AddWithValue("$z", zipBytes);
            cmd.Parameters.AddWithValue("$at", now.ToString("O"));
            cmd.ExecuteNonQuery();
            if (own) tx.Commit();
        }
        finally
        {
            if (own) { tx!.Dispose(); conn!.Dispose(); }
        }
    }

    public MaterialBlob? GetMaterial(string projectId, string materialSetId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT material_set_id, content_version, zip_bytes
            FROM materials WHERE project_id=$p AND material_set_id=$m
            """;
        cmd.Parameters.AddWithValue("$p", projectId);
        cmd.Parameters.AddWithValue("$m", materialSetId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        long len = r.GetBytes(2, 0, null, 0, 0);
        var bytes = new byte[len];
        r.GetBytes(2, 0, bytes, 0, (int)len);
        return new MaterialBlob
        {
            MaterialSetId = r.GetString(0),
            ContentVersion = r.GetString(1),
            ZipBytes = bytes
        };
    }

    public IReadOnlyList<MaterialBlob> GetAllMaterials(string projectId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT material_set_id, content_version, zip_bytes FROM materials WHERE project_id=$p";
        cmd.Parameters.AddWithValue("$p", projectId);
        var list = new List<MaterialBlob>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            long len = r.GetBytes(2, 0, null, 0, 0);
            var bytes = new byte[len];
            r.GetBytes(2, 0, bytes, 0, (int)len);
            list.Add(new MaterialBlob
            {
                MaterialSetId = r.GetString(0),
                ContentVersion = r.GetString(1),
                ZipBytes = bytes
            });
        }
        return list;
    }

    public void SaveAnalysis(string projectId, string branchId,
        string analysisId, string dependencyVersion, string materialSetId,
        string paramsVersionId, string resultJson, DateTimeOffset now,
        SqliteConnection? conn = null, SqliteTransaction? tx = null)
    {
        var own = conn is null;
        conn ??= Open();
        tx ??= conn.BeginTransaction();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR REPLACE INTO analyses
                    (analysis_id, project_id, branch_id, dependency_version,
                     material_set_id, params_version_id, result_json, created_at)
                VALUES ($a,$p,$b,$d,$m,$v,$j,$at)
                """;
            cmd.Parameters.AddWithValue("$a", analysisId);
            cmd.Parameters.AddWithValue("$p", projectId);
            cmd.Parameters.AddWithValue("$b", branchId);
            cmd.Parameters.AddWithValue("$d", dependencyVersion);
            cmd.Parameters.AddWithValue("$m", materialSetId);
            cmd.Parameters.AddWithValue("$v", paramsVersionId);
            cmd.Parameters.AddWithValue("$j", resultJson);
            cmd.Parameters.AddWithValue("$at", now.ToString("O"));
            cmd.ExecuteNonQuery();
            if (own) tx.Commit();
        }
        finally
        {
            if (own) { tx!.Dispose(); conn!.Dispose(); }
        }
    }

    public AnalysisBlob? GetAnalysis(string projectId, string analysisId)
    {
        using var conn = Open();
        return GetAnalysis(conn, null, projectId, analysisId);
    }

    private static AnalysisBlob? GetAnalysis(SqliteConnection conn, SqliteTransaction? tx,
        string projectId, string analysisId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT analysis_id, dependency_version, material_set_id,
                   params_version_id, result_json, branch_id
            FROM analyses WHERE project_id=$p AND analysis_id=$a
            """;
        cmd.Parameters.AddWithValue("$p", projectId);
        cmd.Parameters.AddWithValue("$a", analysisId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new AnalysisBlob
        {
            AnalysisId = r.GetString(0),
            DependencyVersion = r.GetString(1),
            MaterialSetId = r.GetString(2),
            ParamsVersionId = r.GetString(3),
            Json = r.GetString(4),
            BranchId = r.GetString(5)
        };
    }

    public IReadOnlyList<AnalysisBlob> ListAnalyses(string projectId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT analysis_id, dependency_version, material_set_id,
                   params_version_id, result_json, branch_id, created_at
            FROM analyses WHERE project_id=$p ORDER BY created_at
            """;
        cmd.Parameters.AddWithValue("$p", projectId);
        var list = new List<AnalysisBlob>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new AnalysisBlob
            {
                AnalysisId = r.GetString(0),
                DependencyVersion = r.GetString(1),
                MaterialSetId = r.GetString(2),
                ParamsVersionId = r.GetString(3),
                Json = r.GetString(4),
                BranchId = r.GetString(5)
            });
        }
        return list;
    }

    public void SaveDecision(string projectId, string branchId, string decisionId,
        string eventId, string findingId, string verdict, string rationale,
        string? adjustedParamsJson, bool conflicts, DateTimeOffset now,
        SqliteConnection? conn = null, SqliteTransaction? tx = null)
    {
        var own = conn is null;
        conn ??= Open();
        tx ??= conn.BeginTransaction();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO decisions(decision_id, project_id, branch_id, event_id,
                    finding_id, verdict, rationale, adjusted_params_json, conflicts, created_at)
                VALUES ($d,$p,$b,$e,$f,$v,$r,$aj,$c,$at)
                """;
            cmd.Parameters.AddWithValue("$d", decisionId);
            cmd.Parameters.AddWithValue("$p", projectId);
            cmd.Parameters.AddWithValue("$b", branchId);
            cmd.Parameters.AddWithValue("$e", eventId);
            cmd.Parameters.AddWithValue("$f", findingId);
            cmd.Parameters.AddWithValue("$v", verdict);
            cmd.Parameters.AddWithValue("$r", rationale);
            cmd.Parameters.AddWithValue("$aj", (object?)adjustedParamsJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$c", conflicts ? 1 : 0);
            cmd.Parameters.AddWithValue("$at", now.ToString("O"));
            cmd.ExecuteNonQuery();
            if (own) tx.Commit();
        }
        finally
        {
            if (own) { tx!.Dispose(); conn!.Dispose(); }
        }
    }

    private static void CopyAnalysesToBranch(SqliteConnection conn, SqliteTransaction tx,
        string projectId, string fromBranch, string toBranch, HashSet<string> eventIds)
    {
        var copied = new HashSet<string>();
        using var q = conn.CreateCommand();
        q.Transaction = tx;
        q.CommandText = """
            SELECT analysis_id, dependency_version, material_set_id,
                   params_version_id, result_json, created_at
            FROM analyses WHERE project_id=$p AND branch_id=$b
            """;
        q.Parameters.AddWithValue("$p", projectId);
        q.Parameters.AddWithValue("$b", fromBranch);
        var rows = new List<(string Id, string Dep, string Mat, string Ver, string Json, string At)>();
        using (var rr = q.ExecuteReader())
        {
            while (rr.Read())
            {
                rows.Add((rr.GetString(0), rr.GetString(1), rr.GetString(2),
                    rr.GetString(3), rr.GetString(4), rr.GetString(5)));
            }
        }
        foreach (var row in rows)
        {
            if (copied.Contains(row.Id)) continue;
            copied.Add(row.Id);
            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT OR IGNORE INTO analyses(analysis_id, project_id, branch_id,
                    dependency_version, material_set_id, params_version_id, result_json, created_at)
                VALUES ($a,$p,$b,$d,$m,$v,$j,$at)
                """;
            ins.Parameters.AddWithValue("$a", row.Id);
            ins.Parameters.AddWithValue("$p", projectId);
            ins.Parameters.AddWithValue("$b", toBranch);
            ins.Parameters.AddWithValue("$d", row.Dep);
            ins.Parameters.AddWithValue("$m", row.Mat);
            ins.Parameters.AddWithValue("$v", row.Ver);
            ins.Parameters.AddWithValue("$j", row.Json);
            ins.Parameters.AddWithValue("$at", row.At);
            ins.ExecuteNonQuery();
        }
    }
}
