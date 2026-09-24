using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EpubSpectrum.Core;

public static class Hashing
{
    public static string Sha256Hex(ReadOnlySpan<byte> data)
        => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string Sha256Hex(string text)
        => Sha256Hex(Encoding.UTF8.GetBytes(text));

    private static readonly JsonSerializerOptions CanonicalOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static string Canonical(JsonElement element)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
            WriteCanonical(element, writer);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteCanonical(JsonElement el, Utf8JsonWriter w)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                w.WriteStartObject();
                foreach (var prop in el.EnumerateObject()
                             .OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    w.WritePropertyName(prop.Name);
                    WriteCanonical(prop.Value, w);
                }
                w.WriteEndObject();
                break;
            case JsonValueKind.Array:
                w.WriteStartArray();
                foreach (var item in el.EnumerateArray()) WriteCanonical(item, w);
                w.WriteEndArray();
                break;
            case JsonValueKind.String: w.WriteStringValue(el.GetString()); break;
            case JsonValueKind.Number: w.WriteRawValue(el.GetRawText()); break;
            case JsonValueKind.True: w.WriteBooleanValue(true); break;
            case JsonValueKind.False: w.WriteBooleanValue(false); break;
            case JsonValueKind.Null: w.WriteNullValue(); break;
            default: w.WriteNullValue(); break;
        }
    }

    public static string Id(string prefix, string canonicalContent)
        => $"{prefix}_{Sha256Hex(canonicalContent)[..16]}";
}
