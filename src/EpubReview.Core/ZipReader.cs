using System.IO.Compression;
using System.Text;

namespace EpubReview.Core;

public sealed record ZipBudget(int MaxEntries, long MaxUncompressedBytes);

public sealed class EpubZipReader
{
    public (IReadOnlyList<ZipEntryRecord> Entries, IReadOnlyList<Diagnostic> Diagnostics, AnalysisOutcome Outcome) Read(byte[] bytes, ZipBudget budget)
    {
        var diagnostics = new List<Diagnostic>();
        if (bytes.Length < 22)
        {
            return ([], [new Diagnostic("ZIP.TOO_SHORT", DiagnosticSeverity.Error, "ZIP container requires an end-of-central-directory record.", new SourceLocation("<zip>", 0, 0))], AnalysisOutcome.FormatCorrupt);
        }

        var eocd = FindEndOfCentralDirectory(bytes);
        if (eocd < 0)
        {
            return ([], [new Diagnostic("ZIP.EOCD_MISSING", DiagnosticSeverity.Error, "End-of-central-directory signature was not found.", new SourceLocation("<zip>", 0, 0))], AnalysisOutcome.FormatCorrupt);
        }

        var count = bytes.U16(eocd + 10);
        var centralOffset = checked((int)bytes.U32(eocd + 16));
        if (centralOffset >= bytes.Length)
        {
            return ([], [new Diagnostic("ZIP.CENTRAL_OFFSET", DiagnosticSeverity.Error, "Central directory offset points outside the ZIP bytes.", new SourceLocation("<zip>", 0, 0))], AnalysisOutcome.FormatCorrupt);
        }

        if (count > budget.MaxEntries)
        {
            return ([], [new Diagnostic("BUDGET.ZIP_ENTRIES", DiagnosticSeverity.Error, $"ZIP contains {count} entries; budget allows {budget.MaxEntries}.", new SourceLocation("<zip>", 0, 0))], AnalysisOutcome.ResourceBudgetExhausted);
        }

        var result = new List<ZipEntryRecord>();
        long total = 0;
        using var stream = new MemoryStream(bytes);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, false, Encoding.UTF8);
        var byName = archive.Entries.ToDictionary(e => e.FullName, StringComparer.Ordinal);
        var p = centralOffset;

        for (var i = 0; i < count; i++)
        {
            if (p + 46 > bytes.Length || bytes.U32(p) != 0x02014b50)
            {
                diagnostics.Add(new Diagnostic("ZIP.CENTRAL_SIGNATURE", DiagnosticSeverity.Error, "Malformed central directory header.", new SourceLocation("<zip>", i + 1, 1)));
                return (result, diagnostics, AnalysisOutcome.FormatCorrupt);
            }

            var method = bytes.U16(p + 10);
            var crc = bytes.U32(p + 16);
            var compressed = bytes.U32(p + 20);
            var length = bytes.U32(p + 24);
            var nameLength = bytes.U16(p + 28);
            var extraLength = bytes.U16(p + 30);
            var commentLength = bytes.U16(p + 32);
            var localOffset = bytes.U32(p + 42);
            var nameStart = p + 46;
            if (nameStart + nameLength > bytes.Length)
            {
                diagnostics.Add(new Diagnostic("ZIP.ENTRY_NAME", DiagnosticSeverity.Error, "ZIP entry name runs outside the container.", new SourceLocation("<zip>", i + 1, 1)));
                return (result, diagnostics, AnalysisOutcome.FormatCorrupt);
            }

            var rawName = Encoding.UTF8.GetString(bytes, nameStart, nameLength);
            total += length;
            if (total > budget.MaxUncompressedBytes)
            {
                diagnostics.Add(new Diagnostic("BUDGET.UNCOMPRESSED_BYTES", DiagnosticSeverity.Error, $"Uncompressed bytes exceed budget of {budget.MaxUncompressedBytes}.", new SourceLocation(rawName, 0, 0)));
                return (result, diagnostics, AnalysisOutcome.ResourceBudgetExhausted);
            }

            byte[] data;
            if (byName.TryGetValue(rawName, out var archiveEntry))
            {
                using var entryStream = archiveEntry.Open();
                using var copied = new MemoryStream();
                entryStream.CopyTo(copied);
                data = copied.ToArray();
            }
            else
            {
                data = [];
                diagnostics.Add(new Diagnostic("ZIP.ENTRY_LOOKUP", DiagnosticSeverity.Error, "Central directory entry was not available in ZIP data.", new SourceLocation(rawName, 0, 0)));
            }

            result.Add(new ZipEntryRecord(rawName, rawName, localOffset, compressed, length, crc, method, Hashing.Sha256Hex(data), data));
            p = nameStart + nameLength + extraLength + commentLength;
        }

        return (result, diagnostics, diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error) ? AnalysisOutcome.FormatCorrupt : AnalysisOutcome.Success);
    }

    private static int FindEndOfCentralDirectory(byte[] bytes)
    {
        var limit = Math.Max(0, bytes.Length - 65557);
        for (var i = bytes.Length - 22; i >= limit; i--)
        {
            if (bytes.U32(i) == 0x06054b50) return i;
        }
        return -1;
    }

    private static ushort U16(this byte[] b, int o) => System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o));
    private static uint U32(this byte[] b, int o) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
}
