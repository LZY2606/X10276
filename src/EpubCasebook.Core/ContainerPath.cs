namespace EpubCasebook.Core;

public static class ContainerPath
{
    public static string DirOf(string path)
    {
        var i = path.LastIndexOf('/');
        return i < 0 ? string.Empty : path[..i];
    }

    public static (string Path, string? Fragment) SplitFragment(string raw)
    {
        var i = raw.IndexOf('#');
        return i < 0 ? (raw, null) : (raw[..i], raw[(i + 1)..]);
    }

    public static bool TryNormalize(string baseDir, string rawPath, out string normalized, out string? error)
    {
        normalized = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            error = "empty-path";
            return false;
        }
        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(rawPath);
        }
        catch (UriFormatException)
        {
            error = "percent-decode-failed";
            return false;
        }
        if (decoded.StartsWith('/', StringComparison.Ordinal))
        {
            error = "absolute-path-escapes-container";
            return false;
        }
        if (decoded.Contains("://", StringComparison.Ordinal))
        {
            error = "external-uri-not-in-container";
            return false;
        }
        var stack = new List<string>();
        if (!string.IsNullOrEmpty(baseDir))
            foreach (var s in baseDir.Split('/'))
                if (s.Length > 0)
                    stack.Add(s);
        foreach (var seg in decoded.Split('/'))
        {
            if (seg.Length == 0 || seg == ".")
                continue;
            if (seg == "..")
            {
                if (stack.Count == 0)
                {
                    error = "path-escapes-container";
                    return false;
                }
                stack.RemoveAt(stack.Count - 1);
                continue;
            }
            stack.Add(seg);
        }
        if (stack.Count == 0)
        {
            error = "empty-path";
            return false;
        }
        normalized = string.Join('/', stack);
        return true;
    }
}
