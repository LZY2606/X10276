namespace EpubReview.Domain;

public static class PathResolver
{
    public static string NormalizeEntryName(string name) =>
        name.Replace('\\', '/').TrimStart('/');

    public static (string Path, string? Fragment) SplitFragment(string href)
    {
        var idx = href.IndexOf('#');
        if (idx < 0) return (href, null);
        return (href[..idx], href[(idx + 1)..]);
    }

    public static bool TryResolve(string baseDir, string hrefPath, out string normalized, out Diagnostic? diagnostic)
    {
        normalized = string.Empty;
        diagnostic = null;
        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(hrefPath).Replace('\\', '/');
        }
        catch (UriFormatException)
        {
            decoded = hrefPath.Replace('\\', '/');
        }
        if (decoded.StartsWith('/'))
            decoded = decoded[1..];
        else if (baseDir.Length > 0)
            decoded = baseDir + "/" + decoded;

        var segments = new List<string>();
        foreach (var seg in decoded.Split('/'))
        {
            if (seg is "" or ".") continue;
            if (seg == "..")
            {
                if (segments.Count == 0)
                {
                    diagnostic = new Diagnostic(
                        DiagnosticCodes.PathEscapesContainer, Severity.Error,
                        $"路径 '{hrefPath}' 规范化后越出容器根", null, null, null, hrefPath);
                    return false;
                }
                segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(seg);
        }
        normalized = string.Join('/', segments);
        return true;
    }

    public static string DirOf(string fullPath)
    {
        var idx = fullPath.LastIndexOf('/');
        return idx < 0 ? string.Empty : fullPath[..idx];
    }
}
