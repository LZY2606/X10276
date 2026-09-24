using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EpubReview.Core;

/// <summary>
/// 稳定身份与输入摘要：全部以规范化 JSON + SHA-256 得到，导出/导入前后保持一致。
/// </summary>
public static class StableIds
{
    public static string Sha256Hex(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string Sha256Hex(string text) => Sha256Hex(Encoding.UTF8.GetBytes(text));

    public static string BlobId(byte[] bytes) => "blob-" + Sha256Hex(bytes);

    public static string Short(string prefix, string canonical)
        => prefix + StableIds.Sha256Hex(canonical)[..16];

    /// <summary>RFC 8785 风格的确定性 JSON：对象键按序名字典序排列，无空白。</summary>
    public static string Canonical(object? value, JsonSerializerOptions? options = null)
    {
        using var doc = JsonSerializer.SerializeToDocument(value, options ?? JsonOptions.Default);
        var sb = new StringBuilder();
        WriteCanonical(doc.RootElement, sb);
        return sb.ToString();
    }

    private static void WriteCanonical(JsonElement el, StringBuilder sb)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append('{');
                var first = true;
                foreach (var prop in EnumerateSorted(el))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteJsonString(prop.Name, sb);
                    sb.Append(':');
                    WriteCanonical(prop.Value, sb);
                }
                sb.Append('}');
                break;
            case JsonValueKind.Array:
                sb.Append('[');
                var firstItem = true;
                foreach (var item in el.EnumerateArray())
                {
                    if (!firstItem) sb.Append(',');
                    firstItem = false;
                    WriteCanonical(item, sb);
                }
                sb.Append(']');
                break;
            case JsonValueKind.String:
                WriteJsonString(el.GetString() ?? string.Empty, sb);
                break;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                sb.Append(el.GetRawText());
                break;
            default:
                sb.Append("null");
                break;
        }
    }

    private static IEnumerable<JsonProperty> EnumerateSorted(JsonElement el)
        => el.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal);

    private static void WriteJsonString(string value, StringBuilder sb)
    {
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append($"\\u{(int)c:x4}");
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}

public static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static readonly JsonSerializerOptions Indented = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
}
