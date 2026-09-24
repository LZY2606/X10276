namespace EpubReview.Core.Model;

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error
}

public sealed record SourceLocation(
    string Source,
    int? Line = null,
    int? Column = null,
    long? ByteOffset = null,
    string? XPointer = null)
{
    public override string ToString()
    {
        var where = Source;
        if (Line is { } l) where += ":" + l;
        if (Column is { } c) where += ":" + c;
        if (ByteOffset is { } b) where += string.Format("@0x{0:X}", b);
        if (!string.IsNullOrEmpty(XPointer)) where += "#" + XPointer;
        return where;
    }
}

public sealed record Diagnostic(
    string Code,
    string Message,
    DiagnosticSeverity Severity,
    SourceLocation? Location = null,
    IReadOnlyDictionary<string, string>? Context = null)
{
    public Diagnostic At(SourceLocation? location) => this with { Location = location };
}
