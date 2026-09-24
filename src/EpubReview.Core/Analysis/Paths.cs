using System.Security.Cryptography;
using System.Text;

namespace EpubReview.Core.Analysis;

public static class Hashing
{
    public static string Sha256Hex(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string Sha256Hex(string text) => Sha256Hex(Encoding.UTF8.GetBytes(text));

    public static string ShortId(string prefix, string seed) =>
        $"{prefix}-{Sha256Hex(seed)[..16]}";
}

/// <summary>
/// 容器内路径规范化：解析 . / .. 与百分号编码，禁止越出容器根。
/// </summary>
public static class PathNormalizer
{
    public static bool TryNormalize(string baseDir, string href, out string normalized, out bool escaped)
    {
        normalized = "";
        escaped = false;
        if (string.IsNullOrWhiteSpace(href)) return false;

        string decoded;
        try { decoded = Uri.UnescapeDataString(href); }
        catch (UriFormatException) { decoded = href; }

        decoded = decoded.Replace('\\', '/');
        var isRooted = decoded.StartsWith('/');
        var combined = isRooted
            ? decoded.TrimStart('/')
            : string.IsNullOrEmpty(baseDir) ? decoded : baseDir.TrimEnd('/') + "/" + decoded;

        var parts = new List<string>();
        foreach (var seg in combined.Split('/'))
        {
            if (seg is "" or ".") continue;
            if (seg == "..")
            {
                if (parts.Count == 0) { escaped = true; return false; }
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(seg);
        }
        if (isRooted) { escaped = true; return false; } // 绝对路径一律视为越界，不允许锚到容器根以外语义
        normalized = string.Join('/', parts);
        return true;
    }

    public static string DirOf(string path)
    {
        var idx = path.LastIndexOf('/');
        return idx < 0 ? "" : path[..idx];
    }

    public static string FileNameOf(string path)
    {
        var idx = path.LastIndexOf('/');
        return idx < 0 ? path : path[(idx + 1)..];
    }
}
