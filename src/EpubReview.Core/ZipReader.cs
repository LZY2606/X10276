using System.IO.Compression;

namespace EpubReview.Core;

public sealed record ZipReadResult(
    IReadOnlyList<ZipEntryRecord> Entries,
    IReadOnlyList<Diagnostic> Diagnostics,
    bool Corrupt);

/// <summary>
/// ZIP 条目字节读取：保留每个条目的原始未压缩字节、压缩方法、CRC32 与长度。
/// 中心目录损坏属 FormatCorrupt；空容器属 Incomplete（由上层决定）。
/// </summary>
public static class ZipReader
{
    public static ZipReadResult Read(byte[] epubBytes)
    {
        var entries = new List<ZipEntryRecord>();
        var diagnostics = new List<Diagnostic>();

        try
        {
            using var ms = new MemoryStream(epubBytes);
            using var archive = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false);
            foreach (var entry in archive.Entries)
            {
                byte[] bytes;
                try
                {
                    using var es = entry.Open();
                    using var sink = new MemoryStream();
                    es.CopyTo(sink);
                    bytes = sink.ToArray();
                }
                catch (InvalidDataException ex)
                {
                    diagnostics.Add(Diagnostic.Create(
                        "ZIP_ENTRY_CORRUPT", Severity.Error,
                        $"条目 {entry.FullName} 字节无法解压缩：{ex.Message}",
                        new TextLocation(entry.FullName, null, null, null)));
                    continue;
                }

                var method = entry.CompressionLevel.ToString();
                entries.Add(new ZipEntryRecord(
                    EntryName: entry.FullName,
                    CompressedLength: entry.CompressedLength,
                    Length: entry.Length,
                    Crc32: entry.Crc32,
                    CompressionMethod: entry.CompressionMethod.ToString(),
                    IsDirectory: entry.FullName.EndsWith('/'),
                    Bytes: bytes));
            }
        }
        catch (InvalidDataException ex)
        {
            diagnostics.Add(Diagnostic.Create(
                "ZIP_CONTAINER_CORRUPT", Severity.Error,
                $"ZIP 容器结构损坏：{ex.Message}",
                new TextLocation(null, null, null, null)));
            return new ZipReadResult(entries, diagnostics, Corrupt: true);
        }

        return new ZipReadResult(entries, diagnostics, Corrupt: false);
    }
}
