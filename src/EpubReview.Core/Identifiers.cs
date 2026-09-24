using System.Security.Cryptography;
using System.Text;

namespace EpubReview.Core;

/// <summary>
/// Stable, content-derived identities. Equivalent inputs always produce the same identifier,
/// which is what keeps identity stable across export/delete/re-import.
/// </summary>
public static class StableId
{
    public static string Sha256Hex(ReadOnlySpan<byte> bytes)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes, hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string Sha256Hex(string text) =>
        Sha256Hex(Encoding.UTF8.GetBytes(text));

    /// <summary>Deterministic prefixed identifier, e.g. pkg:sha…, item:…, ref:….</summary>
    public static string Of(string prefix, string material) => $"{prefix}:{Sha256Hex(prefix + "\0" + material)}";

    public static string Short(string id, int keep = 12)
    {
        var colon = id.IndexOf(':');
        var body = colon >= 0 ? id[(colon + 1)..] : id;
        return body.Length <= keep ? body : body[..keep];
    }
}

/// <summary>Content-addressed byte blob (ZIP entry bytes, documents, resources).</summary>
public sealed record Blob(string BlobId, string Sha256, long Length);

/// <summary>Quantity carrying its unit/coordinate/time basis plus the original textual representation.</summary>
public sealed record QualifiedValue(
    string Raw,
    string? Canonical,
    string Unit,
    string? Basis,
    bool Converted,
    string? Diagnostic)
{
    public static QualifiedValue Plain(string raw, string unit, string? basis = null) =>
        new(raw, raw, unit, basis, true, null);

    public static QualifiedValue Unconvertible(string raw, string unit, string diagnostic) =>
        new(raw, null, unit, null, false, diagnostic);
}
