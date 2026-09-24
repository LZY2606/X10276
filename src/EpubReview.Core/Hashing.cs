using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace EpubReview.Core;

public static class Hashing
{
    public static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static string StableId(string kind, string discriminator)
    {
        var digest = Sha256Hex(Encoding.UTF8.GetBytes($"{kind}|{NormalizeNewlines(discriminator)}"));
        return $"{kind}_{digest[..16]}";
    }

    public static string Sha256Hex(string value) => Sha256Hex(Encoding.UTF8.GetBytes(value));

    private static string NormalizeNewlines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}

file static class LittleEndianReader
{
    public static ushort U16(this byte[] b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o));
    public static uint U32(this byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
}
