using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace EpubSpectrum.Core;

/// <summary>Stable, content-derived identity. The same logical input always yields the same id.</summary>
public static class Ids
{
    public static string Sha256Hex(byte[] bytes)
    {
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    public static string Sha256Hex(ReadOnlySpan<byte> bytes)
    {
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    public static string Short(byte[] bytes)
    {
        return Sha256Hex(bytes)[..12];
    }

    public static string Short(string content)
    {
        return Sha256Hex(Encoding.UTF8.GetBytes(content))[..12];
    }

    /// <summary>Deterministic id for a scope: stable across re-import/export of the same material graph.</summary>
    public static string Scoped(string scope, string discriminator)
    {
        return scope + "_" + Short(scope + "|" + discriminator);
    }

    public static string WithOrder(string baseId, int ordinal)
    {
        return baseId + "#" + ordinal.ToString(CultureInfo.InvariantCulture);
    }
}
