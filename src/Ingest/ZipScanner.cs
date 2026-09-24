using System.IO.Compression;
using System.Text;
using EpubSpectrum.Domain;

namespace EpubSpectrum.Ingest;

public sealed record ZipScan(
    IngestOutcome Outcome,
    IReadOnlyList<ZipEntryRecord> Entries,
    IReadOnlyList<Diagnostic> Diagnostics,
    ResourceBudget Budget,
    long TotalBytes);

public sealed class ZipScanner
{
    private const uint CentralSig = 0x02014b50;
    private const uint EocdSig = 0x06054b50;

    public ZipScan Scan(byte[] zipBytes, RuleParams parameters)
    {
        var diagnostics = new List<Diagnostic>();
        long budgetBytes = parameters.MaxContainerBytes;
        int budgetEntries = parameters.MaxEntries;

        if (zipBytes.Length < 22)
            return Fail(IngestOutcome.FormatCorrupt, "ZIP-TOO-SHORT",
                "容器字节数不足以构成 ZIP（EOCD 至少 22 字节）",
                "zip:eocd", zipBytes.Length, 0, diagnostics, parameters, zipBytes.Length, 0);

        if (zipBytes.Length > budgetBytes)
            return Fail(IngestOutcome.BudgetExhausted, "BUDGET-BYTES",
                $"容器大小 {zipBytes.Length} byte 超过资源预算 {budgetBytes} byte",
                "zip:input", 0, zipBytes.Length, diagnostics, parameters, zipBytes.Length, 0);

        int eocd = FindEocd(zipBytes);
        if (eocd < 0)
            return Fail(IngestOutcome.FormatCorrupt, "ZIP-EOCD-MISSING",
                "未找到 ZIP 中央目录结尾记录(EOCD)签名 0x06054b50",
                "zip:eocd", zipBytes.Length - 22, 22, diagnostics, parameters, zipBytes.Length, 0);

        ushort totalEntries = ReadU16(zipBytes, eocd + 10);
        uint centralSize = ReadU32(zipBytes, eocd + 12);
        uint centralOffset = ReadU32(zipBytes, eocd + 16);

        if (totalEntries == 0xFFFF || centralOffset == 0xFFFFFFFF || centralSize == 0xFFFFFFFF)
            return Fail(IngestOutcome.FormatCorrupt, "ZIP64-UNSUPPORTED",
                "该容器使用 ZIP64 特性，当前离线解析器仅支持经典中央目录",
                "zip:eocd", eocd, 22, diagnostics, parameters, zipBytes.Length, 0);

        if (totalEntries > budgetEntries)
            return Fail(IngestOutcome.BudgetExhausted, "BUDGET-ENTRIES",
                $"条目数 {totalEntries} 超过资源预算 {budgetEntries} 条",
                "zip:central-directory", centralOffset, totalEntries,
                diagnostics, parameters, zipBytes.Length, totalEntries);

        if ((long)centralOffset + centralSize > zipBytes.Length || centralOffset >= zipBytes.Length)
            return Fail(IngestOutcome.FormatCorrupt, "ZIP-CENTRAL-TRUNCATED",
                "中央目录偏移/长度越出容器字节范围，容器被截断",
                "zip:central-directory", centralOffset, centralSize,
                diagnostics, parameters, zipBytes.Length, 0);

        var entries = new List<(ZipEntryRecord Record, uint Offset)>();
        int position = (int)centralOffset;
        for (int i = 0; i < totalEntries; i++)
        {
            if (position + 46 > zipBytes.Length || ReadU32(zipBytes, position) != CentralSig)
                return Fail(IngestOutcome.FormatCorrupt, "ZIP-CENTRAL-BADRECORD",
                    $"第 {i + 1}/{totalEntries} 条中央目录记录签名损坏",
                    $"zip:central[{i}]", position, 46, diagnostics, parameters,
                    zipBytes.Length, entries.Count);

            ushort version = ReadU16(zipBytes, position + 6);
            ushort flags = ReadU16(zipBytes, position + 8);
            ushort method = ReadU16(zipBytes, position + 10);
            ushort mtime = ReadU16(zipBytes, position + 12);
            ushort mdate = ReadU16(zipBytes, position + 14);
            uint crc = ReadU32(zipBytes, position + 16);
            uint compLen = ReadU32(zipBytes, position + 20);
            uint uncompLen = ReadU32(zipBytes, position + 24);
            ushort nameLen = ReadU16(zipBytes, position + 28);
            ushort extraLen = ReadU16(zipBytes, position + 30);
            ushort commentLen = ReadU16(zipBytes, position + 32);
            uint localHeaderOffset = ReadU32(zipBytes, position + 42);

            int nameStart = position + 46;
            if (nameStart + nameLen > zipBytes.Length)
                return Fail(IngestOutcome.FormatCorrupt, "ZIP-NAME-TRUNCATED",
                    "条目名越出容器范围", $"zip:central[{i}].name",
                    nameStart, nameLen, diagnostics, parameters, zipBytes.Length, entries.Count);

            string rawName = Encoding.UTF8.GetString(zipBytes, nameStart, nameLen);
            position = nameStart + nameLen + extraLen + commentLen;

            byte[] stub = new byte[uncompLen];
            entries.Add((new ZipEntryRecord(i, rawName, version, flags, method, crc,
                compLen, uncompLen, mtime, mdate, stub, crcVerified: false), localHeaderOffset));
        }

        Dictionary<string, byte[]> decompressed;
        try
        {
            decompressed = DecompressAll(zipBytes, diagnostics);
        }
        catch (InvalidDataException ex)
        {
            return Fail(IngestOutcome.FormatCorrupt, "ZIP-ARCHIVE-UNREADABLE",
                $"ZIP 结构损坏，无法打开归档：{ex.Message}",
                "zip:archive", 0, zipBytes.Length, diagnostics, parameters,
                zipBytes.Length, entries.Count);
        }

        long usedUncompressed = 0;
        var finalEntries = new List<ZipEntryRecord>();
        foreach (var (record, offset) in entries)
        {
            if (!decompressed.TryGetValue(record.RawName, out var data))
            {
                diagnostics.Add(new Diagnostic("ZIP-DECOMPRESS-FAILED", Severity.Error,
                    $"条目 “{record.RawName}” 无法从本地文件头解出",
                    Location: $"zip:entry[{record.Index}]", Unit: "byte",
                    Offset: offset, Length: record.CompressedLength));
                return Fail(IngestOutcome.FormatCorrupt, "ZIP-DECOMPRESS-FAILED",
                    $"条目 “{record.RawName}” 无法从本地文件头解出",
                    $"zip:entry[{record.Index}]", offset, record.CompressedLength,
                    diagnostics, parameters, zipBytes.Length, finalEntries.Count);
            }

            bool crcOk = Crc32Algorithm.Compute(data) == record.Crc32;
            if (!crcOk)
                diagnostics.Add(new Diagnostic("ZIP-CRC-MISMATCH", Severity.Warning,
                    $"条目 “{record.RawName}” 解出字节 CRC32 与中央目录声明不一致",
                    Location: $"zip:entry[{record.Index}]", Unit: "crc32",
                    Offset: offset, Length: record.CompressedLength));

            usedUncompressed += data.Length;
            finalEntries.Add(record with { RawBytes = data, CrcVerified = crcOk });
        }

        return new ZipScan(IngestOutcome.Ok, finalEntries, diagnostics,
            new ResourceBudget(budgetBytes, usedUncompressed, budgetEntries, finalEntries.Count),
            zipBytes.Length);
    }

    private static Dictionary<string, byte[]> DecompressAll(byte[] zip, List<Diagnostic> diagnostics)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var ms = new MemoryStream(zip, writable: false);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: true,
            entryNameEncoding: Encoding.UTF8);
        foreach (var entry in archive.Entries)
        {
            try
            {
                using var output = new MemoryStream();
                using (var es = entry.Open())
                    es.CopyTo(output);
                result[entry.FullName] = output.ToByteArray();
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                diagnostics.Add(new Diagnostic("ZIP-DECOMPRESS-FAILED", Severity.Error,
                    $"条目 “{entry.FullName}” 解压缩失败：{ex.Message}",
                    Location: $"zip:entry:{entry.FullName}", Unit: "byte"));
                throw new InvalidDataException(entry.FullName, ex);
            }
        }
        return result;
    }

    private ZipScan Fail(IngestOutcome outcome, string code, string message, string location,
        long offset, long length, List<Diagnostic> diagnostics, RuleParams p,
        long inputLength, int usedEntries)
    {
        diagnostics.Add(new Diagnostic(code, Severity.Error, message,
            Location: location, Unit: location.StartsWith("zip:entry", StringComparison.Ordinal)
                ? "byte" : (code.StartsWith("BUDGET", StringComparison.Ordinal)
                    ? (code == "BUDGET-BYTES" ? "byte" : "entry") : "byte"),
            Offset: offset, Length: length));
        return new ZipScan(outcome, Array.Empty<ZipEntryRecord>(), diagnostics,
            new ResourceBudget(p.MaxContainerBytes, inputLength, p.MaxEntries, usedEntries),
            inputLength);
    }

    private static int FindEocd(byte[] data)
    {
        int maxScan = Math.Min(data.Length, 22 + 65535);
        int start = data.Length - maxScan;
        for (int i = data.Length - 22; i >= start; i--)
        {
            if (ReadU32(data, i) == EocdSig)
                return i;
        }
        return -1;
    }

    internal static ushort ReadU16(byte[] b, int o) =>
        (ushort)(b[o] | (b[o + 1] << 8));

    internal static uint ReadU32(byte[] b, int o) =>
        (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
}
