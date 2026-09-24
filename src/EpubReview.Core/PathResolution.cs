namespace EpubReview.Core;

public sealed record PathResolutionResult(string RawPath, string? NormalizedPath, string? Fragment, bool EscapesContainer, bool IsAbsolute)
{
    public string CoordinateBasis { get; init; } = "container-relative POSIX path; fragments are not normalized";
}

public static class ContainerPath
{
    public static string NormalizeEntryName(string raw)
    {
        var normalized = raw.Replace('\\', '/');
        if (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];
        return normalized.TrimEnd('/');
    }

    public static PathResolutionResult Resolve(string? baseDirectory, string rawReference)
    {
        var raw = rawReference ?? "";
        var hashIndex = raw.IndexOf('#');
        var pathPart = hashIndex >= 0 ? raw[..hashIndex] : raw;
        var fragment = hashIndex >= 0 ? raw[(hashIndex + 1)..] : null;
        var decoded = Uri.UnescapeDataString(pathPart).Replace('\\', '/');
        var isAbsolute = decoded.StartsWith('/');

        IEnumerable<string> StartingSegments()
        {
            if (isAbsolute) return Array.Empty<string>();
            if (string.IsNullOrWhiteSpace(baseDirectory)) return Array.Empty<string>();
            return baseDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries);
        }

        var stack = new List<string>(StartingSegments());
        var escapes = false;
        foreach (var segment in decoded.Split('/', StringSplitOptions.None))
        {
            if (segment is "" or ".") continue;
            if (segment == "..")
            {
                if (stack.Count == 0) escapes = true;
                else stack.RemoveAt(stack.Count - 1);
            }
            else stack.Add(segment);
        }

        var normalized = escapes ? null : string.Join('/', stack);
        return new PathResolutionResult(raw, normalized, string.IsNullOrEmpty(fragment) ? null : fragment, escapes, isAbsolute);
    }

    public static string DirectoryOf(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? "" : path[..index];
    }
}
