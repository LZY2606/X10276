namespace EpubSpectrum.Ingest;

public sealed record PathResolution(
    string? Normalized,
    bool EscapesContainer,
    bool Exists,
    bool CaseAmbiguous,
    IReadOnlyList<string> Candidates,
    string? Diagnostics);

public static class PathNormalizer
{
    public static string NormalizeWithinContainer(string baseEntryPath, string href)
    {
        string combined;
        int hash = href.IndexOf('#');
        string pathPart = hash >= 0 ? href[..hash] : href;
        if (string.IsNullOrEmpty(pathPart))
            return SplitHash(baseEntryPath).Path;

        if (pathPart.StartsWith('/'))
            combined = pathPart.TrimStart('/');
        else
        {
            string baseDir = baseEntryPath.Contains('/')
                ? baseEntryPath[..baseEntryPath.LastIndexOf('/')]
                : "";
            combined = string.IsNullOrEmpty(baseDir) ? pathPart : baseDir + "/" + pathPart;
        }

        var stack = new List<string>();
        foreach (string segment in combined.Split('/'))
        {
            if (segment is "." or "")
                continue;
            if (segment == "..")
            {
                if (stack.Count == 0)
                    return "";
                stack.RemoveAt(stack.Count - 1);
            }
            else
            {
                stack.Add(segment);
            }
        }
        return string.Join('/', stack);
    }

    public static bool TryEscapesContainer(string baseEntryPath, string href)
    {
        string pathPart = SplitHash(href).Path;
        string combined;
        if (pathPart.StartsWith('/'))
        {
            combined = pathPart.TrimStart('/');
            if (pathPart.Contains("..", StringComparison.Ordinal))
            {
                // absolute base "/" then .. must still not escape root
            }
        }
        else
        {
            string baseDir = baseEntryPath.Contains('/')
                ? baseEntryPath[..baseEntryPath.LastIndexOf('/')]
                : "";
            combined = string.IsNullOrEmpty(baseDir) ? pathPart : baseDir + "/" + pathPart;
        }

        int depth = 0;
        foreach (string segment in combined.Split('/'))
        {
            if (segment is "." or "") continue;
            if (segment == "..")
            {
                depth--;
                if (depth < 0) return true;
            }
            else depth++;
        }
        return false;
    }

    public static (string Path, string? Fragment) SplitHash(string href)
    {
        int hash = href.IndexOf('#');
        return hash >= 0 ? (href[..hash], href[(hash + 1)..]) : (href, null);
    }

    public static PathResolution Resolve(string baseEntryPath, string rawHref,
        ISet<string> entryNames, bool caseSensitive)
    {
        var (path, fragment) = SplitHash(rawHref);
        bool escapes = TryEscapesContainer(baseEntryPath, rawHref);
        if (escapes)
        {
            return new PathResolution(null, true, false, false,
                Array.Empty<string>(), "路径规范化结果越出容器根");
        }

        string normalized = NormalizeWithinContainer(baseEntryPath, path);
        var candidates = new List<string>();

        if (caseSensitive)
        {
            bool exists = entryNames.Contains(normalized);
            if (exists)
                candidates.Add(normalized);
            return new PathResolution(exists ? normalized : null, false, exists, false,
                candidates, exists ? null : "容器内不存在该条目");
        }

        foreach (string name in entryNames)
        {
            if (string.Equals(name, normalized, StringComparison.OrdinalIgnoreCase))
                candidates.Add(name);
        }

        if (candidates.Count == 1 && candidates[0] == normalized)
            return new PathResolution(normalized, false, true, false, candidates, null);
        if (candidates.Count == 1)
            return new PathResolution(candidates[0], false, true, true, candidates,
                "仅大小写不同的候选");
        if (candidates.Count > 1)
            return new PathResolution(null, false, true, true, candidates,
                "存在多个大小写候选，无法唯一决定");
        return new PathResolution(null, false, false, false,
            Array.Empty<string>(), "容器内不存在该条目");
    }
}
