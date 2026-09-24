using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EpubReview.Core;

public static class StableIdentity
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Sha256(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string Sha256(string value) => Sha256(Encoding.UTF8.GetBytes(value));

    public static string Short(string prefix, string material)
    {
        Span<byte> hash = stackalloc byte[16];
        SHA256.HashData(Encoding.UTF8.GetBytes(material), hash);
        return $"{prefix}:{Convert.ToHexString(hash).Replace("-", "").ToLowerInvariant()}";
    }

    public static string ForJson(string prefix, object value) =>
        Short(prefix, JsonSerializer.Serialize(value, Options));

    public static string ToJson(object value) =>
        JsonSerializer.Serialize(value, Options);

    public static T? FromJson<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options);
}

public sealed record PathResolution(
    string RawReference,
    string? NormalizedPath,
    bool EscapedContainer,
    bool IsMalformed,
    IReadOnlyList<string> LegalCandidates);

public static class ContainerPaths
{
    public static PathResolution Resolve(string baseDirectory, string reference, bool caseSensitive, IReadOnlyCollection<string> entries)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return new PathResolution(reference, null, false, true, Array.Empty<string>());
        }

        var raw = reference.Split('#', 2)[0];
        if (raw.Length == 0)
        {
            return new PathResolution(reference, baseDirectory.Trim('/'), false, false, Array.Empty<string>());
        }

        if (raw.Contains('\\') || raw.Contains('\\'))
        {
            return new PathResolution(reference, null, false, true, Array.Empty<string>());
        }

        var combined = raw.StartsWith('/')
            ? raw.TrimStart('/')
            : string.IsNullOrEmpty(baseDirectory) ? raw : $"{baseDirectory}/{raw}";
        var segments = combined.Split('/', StringSplitOptions.None);
        var stack = new List<string>();
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }
            if (segment == "..")
            {
                if (stack.Count == 0)
                {
                    return new PathResolution(reference, null, true, false, Array.Empty<string>());
                }
                stack.RemoveAt(stack.Count - 1);
            }
            else
            {
                stack.Add(segment);
            }
        }

        var normalized = string.Join('/', stack);
        var candidates = new List<string>();
        if (entries.Contains(normalized))
        {
            candidates.Add(normalized);
        }

        var decoded = Uri.UnescapeDataString(normalized);
        if (decoded != normalized && entries.Contains(decoded) && !candidates.Contains(decoded))
        {
            candidates.Add(decoded);
        }

        if (!caseSensitive)
        {
            foreach (var entry in entries)
            {
                if (string.Equals(entry, normalized, StringComparison.OrdinalIgnoreCase) && !candidates.Contains(entry))
                {
                    candidates.Add(entry);
                }
            }
        }

        return new PathResolution(reference, normalized, false, false, candidates);
    }

    public static string Directory(string entryPath)
    {
        var index = entryPath.LastIndexOf('/');
        return index < 0 ? string.Empty : entryPath[..index];
    }
}
