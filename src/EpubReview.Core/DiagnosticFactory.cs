namespace EpubReview.Core;

public static class DiagnosticFactory
{
    public static Diagnostic Format(string rule, string message, string location, string originalValue, string? subject = null) =>
        new(StableIdentity.Short("diag", $"{rule}:{location}:{originalValue}"), rule, Severity.Error, message, location, subject, originalValue, "FormatCorrupt", new Dictionary<string, string> { ["outcome"] = "FormatCorrupt" });

    public static Diagnostic Semantic(string rule, string message, string location, string? subject, string originalValue, IReadOnlyDictionary<string, string>? evidence = null)
    {
        var map = new Dictionary<string, string>(evidence ?? new Dictionary<string, string>()) { ["outcome"] = "SemanticUnresolved" };
        return new Diagnostic(StableIdentity.Short("diag", $"{rule}:{location}:{originalValue}"), rule, Severity.Error, message, location, subject, originalValue, "SemanticUnresolved", map);
    }

    public static Diagnostic Budget(string rule, string message, string location, string originalValue) =>
        new(StableIdentity.Short("diag", $"{rule}:{location}:{originalValue}"), rule, Severity.Error, message, location, null, originalValue, "ResourceBudgetExhausted", new Dictionary<string, string> { ["outcome"] = "ResourceBudgetExhausted" });

    public static Diagnostic BrokenAnchor(string rule, string message, string location, string subject, string originalValue, IReadOnlyDictionary<string, string>? evidence = null)
    {
        var map = new Dictionary<string, string>(evidence ?? new Dictionary<string, string>()) { ["outcome"] = "SemanticUnresolved" };
        return new Diagnostic(StableIdentity.Short("diag", $"{rule}:{location}:{originalValue}"), rule, Severity.Warning, message, location, subject, originalValue, "AnchorOrLinkMissing", map);
    }
}
