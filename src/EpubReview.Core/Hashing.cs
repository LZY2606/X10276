using System.Security.Cryptography;
using System.Text;

namespace EpubReview.Core;

public static class Hashing
{
    public static string Sha256Hex(ReadOnlySpan<byte> bytes)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes, hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string Sha256Hex(string text) =>
        Sha256Hex(Encoding.UTF8.GetBytes(text));

    public static string StableId(params object?[] parts)
    {
        var joined = string.Join(" ", parts.Select(p => p?.ToString() ?? ""));
        return Sha256Hex(joined)[..16];
    }
}
