using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EpubReview;

public static class Hashing
{
    public static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static string Sha256Hex(string text) => Sha256Hex(Encoding.UTF8.GetBytes(text));
    public static string Short(string text) => Sha256Hex(text)[..12];
}

public static class Json
{
    public static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Ser<T>(T value) => JsonSerializer.Serialize(value, Opts);
    public static T? Des<T>(string text) => JsonSerializer.Deserialize<T>(text, Opts);
}

public static class MediaTypes
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["opf"] = "application/oebps-package+xml",
        ["xhtml"] = "application/xhtml+xml",
        ["html"] = "text/html",
        ["smil"] = "application/smil+xml",
        ["xml"] = "application/xml",
        ["css"] = "text/css",
        ["svg"] = "image/svg+xml",
        ["png"] = "image/png",
        ["jpg"] = "image/jpeg",
        ["jpeg"] = "image/jpeg",
        ["gif"] = "image/gif",
        ["mp3"] = "audio/mpeg",
        ["m4a"] = "audio/mp4",
        ["ogg"] = "audio/ogg",
        ["otf"] = "application/vnd.ms-opentype",
        ["ttf"] = "font/ttf",
        ["woff"] = "font/woff",
        ["woff2"] = "font/woff2",
        ["js"] = "text/javascript",
        ["ncx"] = "application/x-dtbncx+xml",
        ["txt"] = "text/plain"
    };

    public static string Guess(string entryName)
    {
        var slash = entryName.LastIndexOf('/');
        var file = slash < 0 ? entryName : entryName[(slash + 1)..];
        if (file == "mimetype") return "text/plain";
        var dot = file.LastIndexOf('.');
        if (dot < 0 || dot == file.Length - 1) return "application/octet-stream";
        var ext = file[(dot + 1)..];
        return Map.TryGetValue(ext, out var mt) ? mt : "application/octet-stream";
    }
}
