using System.IO.Compression;

namespace EpubReview.Core;

/// <summary>读取 EPUB(ZIP) 容器，原样保留条目字节；预算耗尽与格式损坏分开报告。</summary>
public static class EpubReader
{
    public sealed record ReadOutcome(
        IReadOnlyList<StoredEntry>? Entries,
        FailureKind Failure,
        Diagnostic? Error);

    public static ReadOutcome Read(byte[] zipBytes, AnalysisParams p)
    {
        List<StoredEntry> entries;
        try
        {
            using var ms = new MemoryStream(zipBytes, writable: false);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            entries = new List<StoredEntry>(zip.Entries.Count);
            long total = 0;
            foreach (var e in zip.Entries)
            {
                if (entries.Count >= p.MaxEntries)
                    return new ReadOutcome(null, FailureKind.BudgetExhausted,
                        new Diagnostic("budget-entries", DiagSeverity.Error,
                            $"ZIP 条目数超过预算 {p.MaxEntries}", e.FullName, null, Array.Empty<string>()));
                if (e.IsDirectory()) continue;
                using var s = e.Open();
                using var buf = new MemoryStream();
                s.CopyTo(buf);
                var bytes = buf.ToArray();
                total += bytes.Length;
                if (total > p.MaxTotalBytes)
                    return new ReadOutcome(null, FailureKind.BudgetExhausted,
                        new Diagnostic("budget-bytes", DiagSeverity.Error,
                            $"ZIP 总字节数超过预算 {p.MaxTotalBytes}", e.FullName, null, Array.Empty<string>()));
                entries.Add(new StoredEntry(Norm(e.FullName), bytes, Ids.Sha256Hex(bytes), GuessMediaType(e.FullName)));
            }
        }
        catch (InvalidDataException ex)
        {
            return new ReadOutcome(null, FailureKind.FormatCorrupt,
                new Diagnostic("zip-unreadable", DiagSeverity.Error,
                    "ZIP 容器无法打开: " + ex.Message, null, null, Array.Empty<string>()));
        }
        entries.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return new ReadOutcome(entries, FailureKind.None, null);
    }

    private static string Norm(string p) => p.Replace('\\', '/').TrimStart('/');

    public static string GuessMediaType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".xhtml" or ".html" or ".htm" => "application/xhtml+xml",
            ".opf" => "application/oebps-package+xml",
            ".xml" => "application/xml",
            ".smil" => "application/smil+xml",
            ".ncx" => "application/x-dtbncx+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".mp3" => "audio/mpeg",
            ".mp4" => "audio/mp4",
            ".css" => "text/css",
            ".ttf" => "font/ttf",
            ".otf" => "font/otf",
            _ => "application/octet-stream",
        };
    }
}

file static class ZipEntryExt
{
    public static bool IsDirectory(this ZipArchiveEntry e) => e.FullName.EndsWith('/');
}
