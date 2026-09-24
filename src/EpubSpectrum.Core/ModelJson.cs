using System.Text.Json;
using System.Text.Json.Serialization;

namespace EpubSpectrum.Core.Json;

public static class ModelJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static readonly JsonElement Empty =
        JsonSerializer.SerializeToElement(new { });

    public static JsonElement Obj(object? value)
        => value is null ? Empty : JsonSerializer.SerializeToElement(value, Options);

    public static T? Deser<T>(JsonElement el) =>
        el.ValueKind == JsonValueKind.Undefined ? default : el.Deserialize<T>(Options);
}

public enum Units
{
    Byte,
    Second,
    Millisecond,
    Character,
    Index,
    CssPixel,
    XmlLine,
    XmlColumn
}
