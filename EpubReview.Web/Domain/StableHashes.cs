using System.Security.Cryptography;
using System.Text;

namespace EpubReview.Domain;

public static class StableHashes {
    public static string Sha256(ReadOnlySpan<byte> bytes) {
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string ShortHash(string value) => ShortHash(Encoding.UTF8.GetBytes(value));

    public static string ShortHash(ReadOnlySpan<byte> bytes) {
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    public static string StableId(string kind, string source) => $"{kind}:{ShortHash(source)}";
}
