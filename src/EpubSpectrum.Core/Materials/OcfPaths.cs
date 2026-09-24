namespace EpubSpectrum.Core.Materials;

public enum PathStatus { Ok, ExternalScheme, EscapesContainer, Invalid }

/// <summary>OCF 相对路径规范化（RFC3986 dot-remove）。规范化只发生在派生层，原文始终保留。</summary>
public sealed record PathResolution(PathStatus Status, string? Normalized, string? Reason)
{
    public bool Escapes => Status == PathStatus.EscapesContainer;
}

public static class OcfPaths
{
    // OCF 容器内引用允许的相对形式；绝对 IRI / 非 file 方案均不属于容器内部图。
    public static PathResolution Resolve(string baseEntryName, string hrefRaw)
    {
        if (string.IsNullOrWhiteSpace(hrefRaw))
            return new PathResolution(PathStatus.Invalid, null, "空引用。");

        string noFragment = hrefRaw;
        int hash = noFragment.IndexOf('#');
        if (hash >= 0) noFragment = noFragment[..hash];
        if (noFragment.Length == 0)
            return new PathResolution(PathStatus.Ok, NormalizeEntry(baseEntryName), "纯 fragment 引用，目标为所在文档自身。");

        if (noFragment.Contains("://") || HasDriveScheme(noFragment))
            return new PathResolution(PathStatus.ExternalScheme, null, $"外部/绝对 IRI 方案：{noFragment}");
        if (noFragment.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            noFragment.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            return new PathResolution(PathStatus.ExternalScheme, null, $"非容器方案：{noFragment}");

        string combined = noFragment.StartsWith('/')
            ? noFragment[1..]
            : Combine(DirOf(baseEntryName), noFragment);

        var stack = new List<string>();
        foreach (string seg in combined.Split('/'))
        {
            if (seg is "" or ".") continue;
            if (seg == "..")
            {
                if (stack.Count == 0)
                    return new PathResolution(PathStatus.EscapesContainer, null,
                        $"路径 {hrefRaw} 经 dot-remove 后越出 OCF 容器根。");
                stack.RemoveAt(stack.Count - 1);
            }
            else stack.Add(seg);
        }
        return new PathResolution(PathStatus.Ok, string.Join('/', stack), null);
    }

    public static string NormalizeEntry(string entryName) => entryName.TrimStart('/').Replace('\\', '/');

    private static bool HasDriveScheme(string s)
    {
        if (s.Length < 2) return false;
        return char.IsLetter(s[0]) && s[1] == ':';
    }

    private static string DirOf(string entry)
    {
        int idx = entry.LastIndexOf('/');
        return idx < 0 ? "" : entry[..idx];
    }

    private static string Combine(string dir, string rel)
        => dir.Length == 0 ? rel : dir + "/" + rel;
}
