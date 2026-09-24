using System.Security.Cryptography;
using System.Text;

namespace EpubReview.Core;

public static class Ids
{
    public static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static string Sha256Hex(string text) =>
        Sha256Hex(Encoding.UTF8.GetBytes(text));

    /// <summary>稳定身份: 同样的内容 + 规则版本 + 参数 + 父级依赖 必得同样的 id。</summary>
    public static string StableId(string prefix, params string[] parts)
    {
        var joined = string.Join("\n", parts);
        return $"{prefix}-{Sha256Hex(joined)[..16]}";
    }
}
