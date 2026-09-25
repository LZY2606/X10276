using System.IO.Compression;

namespace EpubCasebook.Core;

public sealed record ZipEntryBytes(string Name, byte[] Bytes);

public static class ZipRead
{
    public static List<ZipEntryBytes> ReadAll(byte[] zipBytes, RuleParams p, List<Diagnostic> diags,
        out bool budgetExceeded, out bool malformed)
    {
        var entries = new List<ZipEntryBytes>();
        budgetExceeded = false;
        malformed = false;
        var ms = new MemoryStream(zipBytes);
        ZipArchive za;
        try
        {
            za = new ZipArchive(ms, ZipArchiveMode.Read);
        }
        catch (InvalidDataException ex)
        {
            malformed = true;
            diags.Add(new Diagnostic(Diag.MalformedZip, "error", $"ZIP 容器无法打开: {ex.Message}", "zip", null));
            ms.Dispose();
            return entries;
        }
        using (za)
        {
            long total = 0;
            foreach (var e in za.Entries)
            {
                if (entries.Count >= p.MaxEntries)
                {
                    budgetExceeded = true;
                    diags.Add(new Diagnostic(Diag.BudgetExceeded, "error",
                        $"ZIP 条目数超过预算 {p.MaxEntries}", $"zip-entry[{entries.Count}]", e.FullName));
                    break;
                }
                using var s = e.Open();
                using var os = new MemoryStream();
                s.CopyTo(os);
                var b = os.ToArray();
                total += b.Length;
                if (total > p.MaxBytes)
                {
                    budgetExceeded = true;
                    diags.Add(new Diagnostic(Diag.BudgetExceeded, "error",
                        $"累计字节 {total} 超过预算 {p.MaxBytes}", e.FullName, null));
                    break;
                }
                entries.Add(new ZipEntryBytes(e.FullName.Replace('\\', '/'), b));
            }
        }
        return entries;
    }
}
