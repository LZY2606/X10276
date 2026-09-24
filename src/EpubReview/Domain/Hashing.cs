using System.Security.Cryptography;
using System.Text;

namespace EpubReview.Domain;

public static class Hashing
{
    public static string Sha256Hex(string text) => Sha256Hex(Encoding.UTF8.GetBytes(text));

    public static string Sha256Hex(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

/// <summary>
/// 容器内路径规范化。坐标基准：容器根 "/"。
/// 解析 "." 与 ".."，但任何试图越出容器根的结果都被拒绝（返回 false），
/// 绝不允许规范化结果逃逸容器。
/// </summary>
public static class ContainerPath
{
    public static bool TryNormalize(string baseDir, string href, out string normalized, out string? fragment)
    {
        normalized = string.Empty;
        fragment = null;
        if (string.IsNullOrWhiteSpace(href)) return false;

        var hashIdx = href.IndexOf('#');
        var pathPart = hashIdx >= 0 ? href[..hashIdx] : href;
        if (hashIdx >= 0) fragment = href[(hashIdx + 1)..];

        string combined;
        if (pathPart.StartsWith('/'))
        {
            combined = pathPart;
        }
        else if (pathPart.Length == 0)
        {
            combined = "/" + baseDir.Trim('/');
        }
        else
        {
            combined = string.IsNullOrEmpty(baseDir) ? "/" + pathPart : "/" + baseDir.Trim('/') + "/" + pathPart;
        }

        var segments = combined.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<string>();
        foreach (var seg in segments)
        {
            if (seg == ".") continue;
            if (seg == "..")
            {
                if (stack.Count == 0) return false; // 越出容器根
                stack.RemoveAt(stack.Count - 1);
                continue;
            }
            if (seg.IndexOfAny(new[] { '\\', ':', '\0' }) >= 0) return false;
            stack.Add(seg);
        }
        normalized = string.Join('/', stack);
        return normalized.Length > 0;
    }

    public static string DirOf(string entryPath)
    {
        var idx = entryPath.LastIndexOf('/');
        return idx < 0 ? string.Empty : entryPath[..idx];
    }
}
