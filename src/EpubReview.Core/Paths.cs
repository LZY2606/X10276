namespace EpubReview.Core;

/// <summary>
/// 容器内路径解析（RFC 3986 relative-ref 的 remove_dot_segments 思路）。
/// 关键领域规则：规范化不允许越出容器根；越界时返回 Escapes=true，绝不静默折叠成 "..\\x"。
/// </summary>
public sealed record NormalizedPath(bool Escapes, string? Normalized, string? TraversalSegment)
{
    public static NormalizedPath Resolve(string? baseDir, string rawRef)
    {
        if (string.IsNullOrWhiteSpace(rawRef))
            return new NormalizedPath(false, null, null);

        var refPart = rawRef;
        string? fragment = null;
        var hash = rawRef.IndexOf('#');
        if (hash >= 0)
        {
            refPart = rawRef[..hash];
            fragment = rawRef[hash..];
        }

        // 反斜杠不是合法的 IRI 分隔符：保留原值但标记问题由调用方诊断；此处不把它当目录符。
        var combined = string.IsNullOrEmpty(baseDir) ? refPart : baseDir.TrimEnd('/') + "/" + refPart;
        var segments = combined.Split('/');
        var stack = new List<string>();
        string? traversal = null;
        var escapes = false;

        foreach (var seg in segments)
        {
            if (seg.Length == 0) continue;
            if (seg == ".") continue;
            if (seg == "..")
            {
                if (stack.Count > 0)
                {
                    stack.RemoveAt(stack.Count - 1);
                }
                else
                {
                    escapes = true;
                    traversal = "..";
                }
                continue;
            }
            stack.Add(seg);
        }

        if (escapes)
            return new NormalizedPath(true, null, traversal ?? "..");

        var normalized = string.Join('/', stack);
        if (fragment is not null) normalized += fragment;
        return new NormalizedPath(false, normalized, null);
    }

    public static string? StripFragment(string? href)
        => href is null ? null : (href.Contains('#') ? href[..href.IndexOf('#')] : href);
}
