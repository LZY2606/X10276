namespace EpubReview.Core;

/// <summary>
/// 容器内路径规范化。核心规则：路径规范化不允许越出容器。
/// </summary>
public static class PathResolver
{
    public static string DirOf(string path)
    {
        var idx = path.LastIndexOf('/');
        return idx < 0 ? "" : path[..idx];
    }

    /// <summary>
    /// 把 href 相对 baseDir 解析为容器内规范化路径。
    /// escaped=true 表示试图越出容器根（此时 normalized 不可靠）。
    /// </summary>
    public static bool TryNormalize(string baseDir, string hrefPath, out string normalized, out bool escaped)
    {
        normalized = "";
        escaped = false;
        if (string.IsNullOrWhiteSpace(hrefPath)) return false;

        var p = hrefPath.Replace('\\', '/');
        // 百分号解码（解码后仍不允许越界）
        try { p = Uri.UnescapeDataString(p); } catch { return false; }

        if (p.Contains(':')) return false; // 协议或盘符，拒绝

        var segments = new List<string>();
        if (!p.StartsWith('/'))
        {
            foreach (var seg in baseDir.Split('/', StringSplitOptions.RemoveEmptyEntries))
                segments.Add(seg);
        }
        // 以 '/' 开头视为容器根相对，仍然不允许越出容器
        foreach (var raw in p.Split('/'))
        {
            var seg = raw;
            if (seg.Length == 0 || seg == ".") continue;
            if (seg == "..")
            {
                if (segments.Count == 0) { escaped = true; return false; }
                segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(seg);
        }
        if (segments.Count == 0) return false;
        normalized = string.Join('/', segments);
        return true;
    }

    /// <summary>拆出 fragment；无 fragment 时 fragment=null。</summary>
    public static (string Path, string? Fragment) SplitFragment(string href)
    {
        var idx = href.IndexOf('#');
        if (idx < 0) return (href, null);
        return (href[..idx], href[(idx + 1)..]);
    }
}
