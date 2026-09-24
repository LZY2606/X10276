using System.Data.Common;
using System.Text.Json;
using EpubReview.Core;
using Microsoft.Data.Sqlite;

namespace EpubReview.Storage;

public sealed partial class ReviewStore
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, Options);
    private static JsonElement JsonElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);
    private static T Deserialize<T>(DbDataReader reader, int ordinal) =>
        JsonSerializer.Deserialize<T>(reader.GetString(ordinal) ?? "{}", Options) ?? throw new InvalidDataException($"Stored {typeof(T).Name} payload was empty.");

    private void Exec(string sql, SqliteTransaction tx)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private async Task InTransactionAsync(Func<SqliteTransaction, Task> work)
    {
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        try
        {
            await work(tx);
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    private static async Task ExecAsync(string sql, SqliteTransaction tx, params (string Name, object Value)[] parameters)
    {
        await using var command = tx.Connection!.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AddEvent(SqliteTransaction tx, EvidenceEvent evidence)
    {
        await ExecAsync("""
INSERT INTO events(id, sequence_no, state_version, occurred_at, type, subject_id, adopted, reason_code, input_summary, parameter_version, parent_dependency_json, payload_json)
VALUES($id,$seq,$ver,$at,$type,$subject,$adopted,$reason,$input,$params,$parents,$json)
""", tx,
            ("$id", evidence.EventId), ("$seq", evidence.Sequence), ("$ver", evidence.StateVersion), ("$at", evidence.OccurredAtInstant),
            ("$type", evidence.Type), ("$subject", evidence.SubjectId), ("$adopted", evidence.Adopted ? 1 : 0),
            ("$reason", (object?)evidence.ReasonCode ?? DBNull.Value), ("$input", evidence.InputSummary), ("$params", evidence.ParameterVersion),
            ("$parents", Json(evidence.ParentDependencyIds)), ("$json", Json(evidence)));
    }

    private async Task<List<T>> LoadListAsync<T>(string sql, Func<DbDataReader, T> read)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<T>();
        while (await reader.ReadAsync()) result.Add(read(reader));
        return result;
    }

    private async Task<List<T>> LoadManyByIdsAsync<T>(string table, IReadOnlyList<string> ids, Func<DbDataReader, T> read)
    {
        if (ids.Count == 0) return [];
        await using var command = connection.CreateCommand();
        var names = string.Join(",", ids.Select((_, i) => $"$id{i}"));
        command.CommandText = $"SELECT payload_json FROM {table} WHERE id IN ({names})";
        for (var i = 0; i < ids.Count; i++) command.Parameters.AddWithValue($"$id{i}", ids[i]);
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<T>();
        while (await reader.ReadAsync()) result.Add(read(reader));
        return result.OrderBy(x => ids.IndexOf(ExtractId(x))).ToList();
    }

    private static string ExtractId<T>(T value) => value switch
    {
        EpubMaterial m => m.MaterialId,
        RuleProfile p => p.RuleProfileId,
        AnalysisResult a => a.AnalysisId,
        ReviewDecision d => d.DecisionId,
        EvidenceEvent e => e.EventId,
        _ => throw new InvalidOperationException($"No stable id for {typeof(T).Name}")
    };

    private static async Task InsertJsonAsync(string table, string columns, JsonElement element, SqliteTransaction tx)
    {
        switch (table)
        {
            case "materials":
                var material = element.Deserialize<EpubMaterial>(Options) ?? throw new InvalidDataException("Material payload was empty.");
                await ExecAsync("INSERT OR REPLACE INTO materials(id, original_name, sha256, received_at, payload_json, bytes) VALUES($id,$name,$sha,$at,$json,$bytes)", tx,
                    ("$id", material.MaterialId), ("$name", material.OriginalFileName), ("$sha", material.Sha256), ("$at", material.ReceivedAtInstant),
                    ("$json", element.GetRawText()), ("$bytes", material.ZipEntries.SelectMany(e => e.Bytes).ToArray()));
                break;
            case "profiles":
                var profile = element.Deserialize<RuleProfile>(Options) ?? throw new InvalidDataException("Profile payload was empty.");
                await ExecAsync("INSERT OR REPLACE INTO profiles(id, version_no, name, created_at, payload_json) VALUES($id,$v,$n,$at,$json)", tx,
                    ("$id", profile.RuleProfileId), ("$v", profile.Version), ("$n", profile.Name), ("$at", profile.CreatedAtInstant), ("$json", element.GetRawText()));
                break;
            case "analyses":
                var analysis = element.Deserialize<AnalysisResult>(Options) ?? throw new InvalidDataException("Analysis payload was empty.");
                await ExecAsync("INSERT OR REPLACE INTO analyses(id, material_id, profile_id, parent_analysis_id, adopted, scenario, outcome, payload_json) VALUES($id,$m,$p,$parent,$a,$s,$o,$json)", tx,
                    ("$id", analysis.AnalysisId), ("$m", analysis.MaterialId), ("$p", analysis.RuleProfileId),
                    ("$parent", (object?)analysis.ParentAnalysisId ?? DBNull.Value), ("$a", analysis.Adopted ? 1 : 0),
                    ("$s", analysis.ScenarioLabel), ("$o", analysis.Outcome.ToString()), ("$json", element.GetRawText()));
                break;
            case "decisions":
                var decision = element.Deserialize<ReviewDecision>(Options) ?? throw new InvalidDataException("Decision payload was empty.");
                await ExecAsync("INSERT OR REPLACE INTO decisions(id, event_id, analysis_id, subject_id, kind, created_at, payload_json) VALUES($id,$e,$a,$s,$k,$at,$json)", tx,
                    ("$id", decision.DecisionId), ("$e", decision.EventId), ("$a", decision.AnalysisId), ("$s", decision.SubjectId),
                    ("$k", decision.Kind.ToString()), ("$at", decision.CreatedAtInstant), ("$json", element.GetRawText()));
                break;
            default: throw new InvalidOperationException($"Unknown table {table}");
        }
    }

    private static async Task InsertEventAsync(JsonElement element, SqliteTransaction tx)
    {
        var evidence = element.Deserialize<EvidenceEvent>(Options) ?? throw new InvalidDataException("Event payload was empty.");
        await AddEvent(tx, evidence);
    }

    private static string MaterialColumns() => string.Empty;
    private static string ProfileColumns() => string.Empty;
    private static string AnalysisColumns() => string.Empty;
    private static string DecisionColumns() => string.Empty;
}
