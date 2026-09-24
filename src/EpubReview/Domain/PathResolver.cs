namespace EpubReview.Domain;

public static class PathResolver
{
    public static string BaseDirOf(string packagePath)
    {
        var idx = packagePath.LastIndexOf('/');
        return idx < 0 ? "" : packagePath[..idx];
    }

    public static string? Normalize(string path)
    {
        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<string>();
        foreach (var seg in segments)
        {
            if (seg == ".") continue;
            if (seg == "..")
            {
                if (stack.Count == 0) return null;
                stack.RemoveAt(stack.Count - 1);
                continue;
            }
            stack.Add(seg);
        }
        return string.Join('/', stack);
    }

    public static string? Resolve(string baseDir, string href)
    {
        var raw = string.IsNullOrEmpty(baseDir) ? href : baseDir + "/" + href;
        return Normalize(raw);
    }

    public static (string Path, string? Fragment) SplitFragment(string href)
    {
        var idx = href.IndexOf('#');
        if (idx < 0) return (href, null);
        return (href[..idx], Uri.UnescapeDataString(href[(idx + 1)..]));
    }
}
