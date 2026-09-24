using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace EpubReview;

/// <summary>Stable, content-addressed identities. Equivalent inputs => identical ids => stable ordering.</summary>
public static class Ids
{
    public static string New(string prefix, params object?[] parts)
    {
        var sb = new StringBuilder();
        foreach (var p in parts) sb.Append(p?.ToString() ?? "∅").Append('\u001f');
        return prefix + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..24].ToLowerInvariant();
    }

    public static string ForBytes(string prefix, byte[] bytes, params object?[] parts)
    {
        var sb = new StringBuilder(Convert.ToHexString(SHA256.HashData(bytes)));
        foreach (var p in parts) sb.Append('\u001f').Append(p?.ToString() ?? "∅");
        return prefix + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..24].ToLowerInvariant();
    }
}

/// <summary>Computation outcomes. BudgetExhausted / FormatCorrupt / SemanticallyUnsolvable are three distinct failure kinds.</summary>
public enum CompStatus
{
    Ok,
    CompletedWithDiagnostics,
    BudgetExhausted,
    FormatCorrupt,
    SemanticallyUnsolvable
}

public record DiagPosition(string? Entry, int? Line, int? Column, string? Fragment, long? Offset)
{
    public JsonObject ToJson()
    {
        var o = new JsonObject();
        if (Entry is not null) o["entry"] = Entry;
        if (Line is not null) o["line"] = Line;
        if (Column is not null) o["column"] = Column;
        if (Fragment is not null) o["fragment"] = Fragment;
        if (Offset is not null) o["offset"] = Offset;
        return o;
    }
}

public record Diagnostic(string Code, string Message, string Severity, DiagPosition? Position)
{
    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["code"] = Code,
            ["message"] = Message,
            ["severity"] = Severity
        };
        if (Position is not null) o["position"] = Position.ToJson();
        return o;
    }

    public static Diagnostic FromJson(JsonObject o) => new(
        o["code"]!.GetValue<string>(),
        o["message"]!.GetValue<string>(),
        o["severity"]!.GetValue<string>(),
        o["position"] is JsonObject p ? new DiagPosition(
            p["entry"]?.GetValue<string>(),
            p["line"]?.GetValue<int>(),
            p["column"]?.GetValue<int>(),
            p["fragment"]?.GetValue<string>(),
            p["offset"]?.GetValue<long>()) : null);
}

/// <summary>Immutable material: original bytes and original representation are always preserved.</summary>
public record Material(
    string Id,
    string Kind,
    string Name,
    byte[] Bytes,
    string MediaType,
    Dictionary<string, string> Attrs,
    string Source);

public record Computation(
    string Id,
    string Rule,
    string ParamVersion,
    string ParamsJson,
    string InputHash,
    List<string> Parents,
    CompStatus Status,
    string OutputJson,
    string DiagnosticsJson);

public record EventRec(long Seq, string Id, string? Parent, string Type, string PayloadJson);

public record Decision(string Id, string Target, string Verdict, string Rationale, string EventId, string PayloadJson);
