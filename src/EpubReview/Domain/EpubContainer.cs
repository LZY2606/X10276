using System.IO.Compression;

namespace EpubReview.Domain;

public sealed class EpubContainer
{
    private readonly Dictionary<string, ZipEntryData> _byName;

    public IReadOnlyList<ZipEntryData> Entries { get; }

    private EpubContainer(List<ZipEntryData> entries)
    {
        Entries = entries;
        _byName = new Dictionary<string, ZipEntryData>(StringComparer.Ordinal);
        foreach (var e in entries)
            _byName.TryAdd(e.Name, e);
    }

    public ZipEntryData? Find(string normalizedPath) =>
        _byName.TryGetValue(normalizedPath, out var e) ? e : null;

    public bool Contains(string normalizedPath) => _byName.ContainsKey(normalizedPath);

    public static ContainerOpenResult Open(byte[] zipBytes, ResourceBudget budget)
    {
        var diagnostics = new List<Diagnostic>();
        List<ZipEntryData> entries;
        try
        {
            entries = new List<ZipEntryData>();
            using var ms = new MemoryStream(zipBytes, writable: false);
            using var archive = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count > budget.MaxEntries)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticCodes.BudgetEntries, Severity.Error,
                    $"ZIP 条目数 {archive.Entries.Count} 超过预算 {budget.MaxEntries}",
                    null, null, null, null));
                return new ContainerOpenResult(null, AnalysisOutcome.ResourceBudgetExhausted, diagnostics);
            }
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue;
                if (entry.Length > budget.MaxEntryBytes)
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.BudgetEntryBytes, Severity.Error,
                        $"条目 {entry.FullName} 大小 {entry.Length} 超过单条目预算 {budget.MaxEntryBytes}",
                        entry.FullName, null, null, null));
                    return new ContainerOpenResult(null, AnalysisOutcome.ResourceBudgetExhausted, diagnostics);
                }
                total += entry.Length;
                if (total > budget.MaxTotalBytes)
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.BudgetBytes, Severity.Error,
                        $"累计解压字节 {total} 超过总预算 {budget.MaxTotalBytes}",
                        entry.FullName, null, null, null));
                    return new ContainerOpenResult(null, AnalysisOutcome.ResourceBudgetExhausted, diagnostics);
                }
                using var es = entry.Open();
                using var buffer = new MemoryStream();
                es.CopyTo(buffer);
                var name = PathResolver.NormalizeEntryName(entry.FullName);
                entries.Add(new ZipEntryData(name, buffer.ToArray()));
            }
        }
        catch (InvalidDataException ex)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticCodes.ZipCorrupt, Severity.Error,
                $"ZIP 容器损坏: {ex.Message}", null, null, null, null));
            return new ContainerOpenResult(null, AnalysisOutcome.FormatCorrupt, diagnostics);
        }
        return new ContainerOpenResult(new EpubContainer(entries), AnalysisOutcome.Success, diagnostics);
    }
}

public sealed record ContainerOpenResult(
    EpubContainer? Container,
    AnalysisOutcome Outcome,
    IReadOnlyList<Diagnostic> Diagnostics);
