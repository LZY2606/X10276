namespace EpubReview.Core.Import;

public readonly record struct ResolvedPath(string Normalized, bool Traversal, bool Absolute)
{
    public bool EscapesContainer => Traversal || Absolute;
}

public static class PathRel
{
    public static string DirectoryOf(string path)
    {
        var idx = path.LastIndexOf('/');
        return idx < 0 ? "" : path[..idx];
    }

    public static string Join(string baseDir, string relative)
    {
        if (relative.StartsWith('/')) return relative.TrimStart('/');
        return string.IsNullOrEmpty(baseDir) ? relative : baseDir + "/" + relative;
    }

    public static ResolvedPath Resolve(string raw)
    {
        var absolute = raw.StartsWith('/');
        var withoutQuery = raw.Split('?')[0];
        var parts = withoutQuery.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<string>();
        bool traversal = false;

        foreach (var part in parts)
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                else traversal = true;
                continue;
            }
            if (part.Length == 2 && part[0] == '.' && part[1] == '.')
            {
                traversal = true;
                continue;
            }
            stack.Add(part);
        }

        var normalized = string.Join('/', stack);
        var trailing = withoutQuery.EndsWith('/') && stack.Count > 0 ? "/" : "";
        return new ResolvedPath(normalized + trailing, traversal, absolute);
    }

    public static (string Path, string? Fragment) SplitFragment(string uri)
    {
        var hash = uri.IndexOf('#');
        return hash < 0 ? (uri, null) : (uri[..hash], uri[(hash + 1)..]);
    }

    public static string PercentDecode(string s)
    {
        try
        {
            return Uri.UnescapeDataString(s);
        }
        catch
        {
            return s;
        }
    }
}
