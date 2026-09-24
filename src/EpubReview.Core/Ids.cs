using System.Security.Cryptography;
using System.Text;

namespace EpubReview.Core;

public static class Ids
{
    public static string Sha256Hex(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static string Sha256Hex(string text)
        => Sha256Hex(Encoding.UTF8.GetBytes(text));

    public static string Short(string text, int len = 16)
        => Sha256Hex(text)[..len];

    public static string MaterialId(string path, byte[] bytes)
        => "mat_" + Short(path + "\0" + Sha256Hex(bytes));

    public static string EventId(string projectId, long seq, string kind, string payloadJson)
        => "evt_" + Short(projectId + "|" + seq + "|" + kind + "|" + payloadJson);

    public static string AnalysisId(string inputDigest, string ruleVersion, string paramsJson, IEnumerable<string> parents)
        => "ana_" + Short(inputDigest + "|" + ruleVersion + "|" + paramsJson + "|" + string.Join(",", parents.OrderBy(p => p, StringComparer.Ordinal)));

    public static string DecisionId(string projectId, long seq, string targetId, string action, string? paramsJson, string? rationale)
        => "dec_" + Short(projectId + "|" + seq + "|" + targetId + "|" + action + "|" + (paramsJson ?? "") + "|" + (rationale ?? ""));
}
