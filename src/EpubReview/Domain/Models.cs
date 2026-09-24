using System.Security.Cryptography;
using System.Text;

namespace EpubReview.Domain;

public enum ComputationStatus
{
    Ok = 0,
    BudgetExhausted = 1,
    FormatCorrupt = 2,
    SemanticallyUnsolvable = 3
}

public sealed record SourceLocation(string? File, int? Line, int? Column, string? Anchor)
{
    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append(File ?? "(unknown)");
        if (Line is not null) sb.Append(':').Append(Line);
        if (Column is not null) sb.Append(':').Append(Column);
        if (Anchor is not null) sb.Append('#').Append(Anchor);
        return sb.ToString();
    }
}

public sealed record Diagnostic(string Code, string Severity, string Message, SourceLocation? Location);

public static class Hashing
{
    public static string Sha256Hex(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string Sha256Hex(string text) => Sha256Hex(Encoding.UTF8.GetBytes(text));

    public static string StableId(string prefix, string raw) =>
        $"{prefix}:{Sha256Hex(raw)[..16]}";
}
