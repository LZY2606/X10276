namespace EpubReview;

public record ImportResult(long EventSeq, FailureClass Status, List<Diagnostic> Diagnostics, string? ZipSha);

public static class ImportService
{
    private static Diagnostic Diag(string code, DiagSeverity severity, string message, SourcePos pos) =>
        new("dg-" + Hashing.Short($"{code}|{pos}|{message}"), code, severity, message, pos, null);

    public static ImportResult ImportZip(Store store, byte[] zipBytes, string label, RulesParams rules, long? parentSeq, string? note = null)
    {
        var diags = new List<Diagnostic>();
        var status = FailureClass.None;
        List<(string Name, byte[] Bytes)>? entries = null;
        string? zipSha = zipBytes.Length > 0 ? Hashing.Sha256Hex(zipBytes) : null;
        var zipPos = new SourcePos("zip:container", null, null);

        if (zipBytes.Length == 0)
        {
            status = FailureClass.CorruptFormat;
            diags.Add(Diag("EmptyContainer", DiagSeverity.Error, "容器为 0 字节，无法作为 EPUB 打开", zipPos));
        }
        else if (zipBytes.LongLength > rules.MaxTotalBytes)
        {
            status = FailureClass.BudgetExhausted;
            diags.Add(Diag("ContainerSizeBudget", DiagSeverity.Error,
                $"容器 {zipBytes.LongLength} 字节超过总字节预算 {rules.MaxTotalBytes} 字节", zipPos));
        }
        else
        {
            try
            {
                entries = ZipBuilder.ReadEntries(zipBytes);
            }
            catch (InvalidDataException ex)
            {
                status = FailureClass.CorruptFormat;
                diags.Add(Diag("ZipMalformed", DiagSeverity.Error, $"ZIP 结构损坏: {ex.Message}", zipPos));
            }
        }

        if (status == FailureClass.None && entries is not null)
        {
            if (entries.Count > rules.MaxEntries)
            {
                status = FailureClass.BudgetExhausted;
                diags.Add(Diag("EntryCountBudget", DiagSeverity.Error,
                    $"ZIP 条目数 {entries.Count} 超过预算 {rules.MaxEntries} 条", zipPos));
            }
            else if (entries.Sum(e => e.Bytes.LongLength) > rules.MaxTotalBytes)
            {
                status = FailureClass.BudgetExhausted;
                diags.Add(Diag("UncompressedSizeBudget", DiagSeverity.Error,
                    $"解压后总字节数超过预算 {rules.MaxTotalBytes} 字节", zipPos));
            }
        }

        var materialIds = new List<string>();
        if (status == FailureClass.None && entries is not null && zipSha is not null)
        {
            var zipMaterial = new Material(
                "mat-" + Hashing.Short("zip|" + zipSha), "zip", $"{label}.epub", zipBytes, zipSha, "application/epub+zip");
            store.AddMaterial(zipMaterial);
            materialIds.Add(zipMaterial.Id);
            foreach (var (name, data) in entries)
            {
                var sha = Hashing.Sha256Hex(data);
                var material = new Material(
                    "mat-" + Hashing.Short($"entry|{name}|{sha}"), "zip-entry", name, data, sha, MediaTypes.Guess(name));
                store.AddMaterial(material);
                materialIds.Add(material.Id);
            }
        }

        var payload = new ImportPayload(label, zipSha ?? "", materialIds, status, diags, note);
        var evt = store.AppendEvent("import", Json.Ser(payload), parentSeq);
        return new ImportResult(evt.Seq, status, diags, zipSha);
    }
}
