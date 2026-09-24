using System.Text;

namespace EpubReview.Domain;

public sealed record NormalizedPath(bool Escapes, string? Path, string? Fragment, string? Error)
{
    public string? Full => Path is null ? null : (Fragment is null ? Path : $"{Path}#{Fragment}");
}

public static class PathRules {
    public static NormalizedPath Resolve(string baseDirectory, string rawValue) {
        var fragment = (string?)null;
        var value = rawValue;
        var hash = value.IndexOf('#');
        if (hash >= 0) {
            fragment = value[(hash + 1)..];
            value = value[..hash];
        }
        if (string.IsNullOrWhiteSpace(value)) return new NormalizedPath(false, baseDirectory.Trim('/'), fragment, null);
        string decoded;
        try { decoded = Uri.UnescapeDataString(value); }
        catch (Exception ex) { return new NormalizedPath(false, null, fragment, ex.Message); }
        if (decoded.Contains('\\')) decoded = decoded.Replace('\\', '/');
        if (Path.IsPathRooted(decoded) || decoded.Contains(':', StringComparison.Ordinal))
            return new NormalizedPath(true, null, fragment, "Absolute or drive-qualified paths are not allowed inside an OCF container.");
        var combined = string.IsNullOrEmpty(baseDirectory) ? decoded : $"{baseDirectory.TrimEnd('/')}/{decoded.TrimStart('/')}";
        var stack = new List<string>();
        foreach (var part in combined.Split('/', StringSplitOptions.RemoveEmptyEntries)) {
            if (part == ".") continue;
            if (part == "..") {
                if (stack.Count == 0) return new NormalizedPath(true, null, fragment, "Path normalization would escape the OCF container root.");
                stack.RemoveAt(stack.Count - 1);
            }
            else stack.Add(part);
        }
        return new NormalizedPath(false, string.Join('/', stack), fragment, null);
    }

    public static string NormalizeEntryName(string raw) {
        var name = raw.Replace('\\', '/');
        var stack = new List<string>();
        foreach (var part in name.Split('/')) {
            if (part == "." || part.Length == 0) continue;
            if (part == "..") {
                if (stack.Count == 0) return "";
                stack.RemoveAt(stack.Count - 1);
            }
            else stack.Add(part);
        }
        return string.Join('/', stack);
    }
}
