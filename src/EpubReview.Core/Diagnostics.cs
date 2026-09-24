namespace EpubReview.Core;

public enum Severity
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

/// <summary>三种失败结果必须彼此可区分，外加输入不完整（不得伪装成功）。</summary>
public enum OutcomeKind
{
    Ok = 0,
    ResourceBudgetExhausted = 1,
    FormatCorrupt = 2,
    SemanticallyUnsolvable = 3,
    Incomplete = 4,
}

public sealed record TextLocation(string? EntryName, int? Line, int? Column, string? XPath)
{
    public override string ToString()
    {
        var where = EntryName ?? "<unknown-entry>";
        if (Line is int ln)
        {
            where += $":{ln}";
            if (Column is int col) where += $":{col}";
        }
        return XPath is null ? where : $"{where} [{XPath}]";
    }
}

public sealed record Diagnostic(
    string Code,
    Severity Severity,
    string Message,
    TextLocation? Location,
    IReadOnlyList<string> RelatedStableIds)
{
    public static Diagnostic Create(string code, Severity severity, string message,
        TextLocation? location = null, IReadOnlyList<string>? related = null)
        => new(code, severity, message, location, related ?? Array.Empty<string>());
}
