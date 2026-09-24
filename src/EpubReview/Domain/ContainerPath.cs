namespace EpubReview.Domain;

/// <summary>
/// 容器内路径规范化：允许 ./ 与 ../ 在容器内部解析，
/// 但任何越出容器根的路径都会被拒绝并产生诊断，绝不静默截断。
/// </summary>
public static class ContainerPath
{
    public static string? Normalize(string baseDir, string href, out string? error)
    {
        error = null;
        var noFrag = href.Split('#')[0].Split('?')[0];
        string decoded;
        try { decoded = Uri.UnescapeDataString(noFrag); }
        catch (UriFormatException) { error = $"无法解码的路径: {href}"; return null; }

        var combined = baseDir.Length > 0 ? baseDir + "/" + decoded : decoded;
        var parts = new List<string>();
        foreach (var seg in combined.Split('/'))
        {
            if (seg is "" or ".") continue;
            if (seg == "..")
            {
                if (parts.Count == 0)
                {
                    error = $"路径越出容器根: {href}";
                    return null;
                }
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(seg);
        }
        return string.Join('/', parts);
    }

    public static string DirOf(string path)
    {
        var i = path.LastIndexOf('/');
        return i < 0 ? "" : path[..i];
    }

    public static (string Path, string? Fragment) SplitFragment(string href)
    {
        var i = href.IndexOf('#');
        return i < 0 ? (href, null) : (href[..i], href[(i + 1)..]);
    }
}
