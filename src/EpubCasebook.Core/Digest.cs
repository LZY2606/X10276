using System.Security.Cryptography;
using System.Text;

namespace EpubCasebook.Core;

public static class Digest
{
    public static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static string Sha256Hex(string text) => Sha256Hex(Encoding.UTF8.GetBytes(text));

    public static string MaterialId(byte[] bytes) => "mat:" + Sha256Hex(bytes)[..16];

    public static string InputDigest(string ruleVersion, IEnumerable<(string Name, string Sha)> entries, string? selectedPackage)
    {
        var sb = new StringBuilder();
        sb.Append("rules=").Append(ruleVersion).Append("|pkg=").Append(selectedPackage ?? "-");
        foreach (var e in entries.OrderBy(e => e.Name, StringComparer.Ordinal))
            sb.Append('|').Append(e.Name).Append(':').Append(e.Sha);
        return Sha256Hex(sb.ToString());
    }

    public static string RunId(string inputDigest) => "run:" + inputDigest[..16];

    public static string EventId(string caseId, long seq) => $"evt:{caseId}:{seq:D4}";
}
