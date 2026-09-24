using System.IO.Compression;
using EpubReview.Core.Model;

namespace EpubReview.Core.Analysis;

/// <summary>读取 ZIP 容器，保留每条目的原始字节；条目按路径排序保证等价输入稳定。</summary>
public static class EpubContainer
{
    public static bool TryRead(byte[] zipBytes, out List<EntryMaterial> entries, out Diagnostic? error)
    {
        entries = new List<EntryMaterial>();
        error = null;
        try
        {
            using var ms = new MemoryStream(zipBytes);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            foreach (var e in zip.Entries)
            {
                if (e.FullName.EndsWith('/')) continue;
                using var es = e.Open();
                using var buf = new MemoryStream();
                es.CopyTo(buf);
                var bytes = buf.ToArray();
                var path = e.FullName.Replace('\\', '/');
                entries.Add(new EntryMaterial(
                    Hashing.ShortId("m", path + ":" + Hashing.Sha256Hex(bytes)),
                    path,
                    Hashing.Sha256Hex(bytes),
                    GuessMediaType(path),
                    Classify(path),
                    bytes));
            }
        }
        catch (InvalidDataException ex)
        {
            error = new Diagnostic(FindingCodes.FormatCorrupt, "error",
                "ZIP 容器损坏: " + ex.Message, null, null, null, null);
            return false;
        }
        entries = entries
            .OrderBy(e => e.Path, StringComparer.Ordinal)
            .ToList();
        return true;
    }

    public static string Classify(string path) => path switch
    {
        "mimetype" => "mimetype",
        "META-INF/container.xml" => "container",
        var p when p.EndsWith(".opf", StringComparison.OrdinalIgnoreCase) => "package",
        var p when p.EndsWith(".smil", StringComparison.OrdinalIgnoreCase) => "overlay",
        var p when p.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase)
                || p.EndsWith(".html", StringComparison.OrdinalIgnoreCase) => "content",
        var p when p.EndsWith(".ncx", StringComparison.OrdinalIgnoreCase) => "ncx",
        _ => "resource"
    };

    public static string GuessMediaType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".opf" => "application/oebps-package+xml",
            ".xhtml" => "application/xhtml+xml",
            ".html" => "text/html",
            ".smil" => "application/smil+xml",
            ".ncx" => "application/x-dtbncx+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".css" => "text/css",
            ".mp3" => "audio/mpeg",
            ".mp4" => "audio/mp4",
            ".js" => "text/javascript",
            ".xml" => "application/xml",
            _ => "application/octet-stream"
        };
}
