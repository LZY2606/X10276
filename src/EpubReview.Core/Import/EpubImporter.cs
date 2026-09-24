using EpubReview.Core.Model;

namespace EpubReview.Core.Import;

public sealed class EpubImporter
{
    public ImportResult Import(byte[] zipBytes, Analysis.AnalysisParams parameters, DateTimeOffset? now = null)
    {
        var stamp = now ?? DateTimeOffset.UtcNow;
        var zipSha = Hashing.Sha256Hex(zipBytes);

        var zipResult = ZipReader.Read(zipBytes, parameters.MaxUncompressedBytes, parameters.MaxEntryCount);

        var budget = zipResult.Diagnostics.FirstOrDefault(d => d.Code == "ZIP-BUDGET-BYTES");
        if (budget is not null)
        {
            return new ImportResult.BudgetExhausted(
                "maxUncompressedBytes",
                parameters.MaxUncompressedBytes,
                UnitRef.Bytes,
                long.Parse(budget.Context?["observedBytes"] ?? "0"),
                budget.Location,
                zipResult.Diagnostics);
        }

        var entryBudget = zipResult.Diagnostics.FirstOrDefault(d => d.Code == "ZIP-BUDGET-ENTRIES");
        if (entryBudget is not null)
        {
            return new ImportResult.BudgetExhausted(
                "maxEntryCount",
                parameters.MaxEntryCount,
                UnitRef.Ordinal,
                zipResult.Entries.Count,
                entryBudget.Location,
                zipResult.Diagnostics);
        }

        var fatalFormat = zipResult.Diagnostics.FirstOrDefault(d =>
            d.Severity == DiagnosticSeverity.Error &&
            (d.Code.StartsWith("ZIP-EOCD") || d.Code.StartsWith("ZIP-CD")));
        if (fatalFormat is not null)
        {
            return new ImportResult.FormatCorrupt(
                fatalFormat.Message, fatalFormat.Location, zipResult.Diagnostics, fatalFormat.Location?.ByteOffset);
        }

        var entries = zipResult.Entries.Where(e => !e.IsDirectory).ToList();
        var diagnostics = new List<Diagnostic>(zipResult.Diagnostics);

        var containerEntry = entries.FirstOrDefault(e =>
            e.Name == "META-INF/container.xml");
        if (containerEntry is null)
        {
            return new ImportResult.FormatCorrupt(
                "META-INF/container.xml is absent; EPUB container is incomplete.",
                new SourceLocation("META-INF/container.xml"),
                diagnostics.Concat(new[]
                {
                    new Diagnostic("CONTAINER-MISSING",
                        "META-INF/container.xml is absent.",
                        DiagnosticSeverity.Error,
                        new SourceLocation("META-INF/container.xml"))
                }).ToList(),
                null);
        }

        var (container, containerDiags) = ContainerXmlParser.Parse(containerEntry);
        diagnostics.AddRange(containerDiags);
        if (container is null)
        {
            return new ImportResult.FormatCorrupt(
                "container.xml could not be parsed.",
                new SourceLocation(containerEntry.Name),
                diagnostics, null);
        }

        var byName = entries.ToDictionary(e => e.Name, StringComparer.Ordinal);
        var packages = new List<PackageDocument>();
        var unresolved = new List<string>();

        int pkgIndex = 0;
        foreach (var rootFile in container.RootFiles)
        {
            var packageId = "pkg-" + Hashing.StableId(container.Sha256, rootFile.FullPath, pkgIndex++);
            if (!byName.TryGetValue(rootFile.FullPath, out var pkgEntry))
            {
                diagnostics.Add(new Diagnostic("PACKAGE-MISSING-ENTRY",
                    $"Rootfile '{rootFile.FullPath}' declared in container.xml has no ZIP entry.",
                    DiagnosticSeverity.Error, rootFile.Location,
                    new Dictionary<string, string> { ["packageId"] = packageId }));
                unresolved.Add(packageId);
                continue;
            }

            var (package, packageDiags) = PackageParser.Parse(pkgEntry, packageId);
            diagnostics.AddRange(packageDiags);
            if (package is not null)
            {
                packages.Add(package);
                foreach (var item in package.Manifest)
                {
                    var baseDir = PathRel.DirectoryOf(pkgEntry.Name);
                    var joined = PathRel.Join(baseDir, item.Href);
                    var resolved = PathRel.Resolve(joined);
                    if (resolved.EscapesContainer)
                    {
                        diagnostics.Add(new Diagnostic("PATH-TRAVERSAL",
                            $"Manifest href '{item.Href}' normalizes to '{resolved.Normalized}', which escapes the container root.",
                            DiagnosticSeverity.Error, item.Location,
                            new Dictionary<string, string>
                            {
                                ["packageId"] = packageId,
                                ["manifestId"] = item.Id,
                                ["normalized"] = resolved.Normalized
                            }));
                    }
                    else if (!byName.ContainsKey(resolved.Normalized))
                    {
                        diagnostics.Add(new Diagnostic("RESOURCE-MISSING",
                            $"Manifest item '{item.Id}' resolves to '{resolved.Normalized}', which has no ZIP entry.",
                            DiagnosticSeverity.Error, item.Location,
                            new Dictionary<string, string>
                            {
                                ["packageId"] = packageId,
                                ["manifestId"] = item.Id,
                                ["normalized"] = resolved.Normalized
                            }));
                        unresolved.Add(packageId + ":" + item.Id);
                    }
                }
            }
        }

        var unknown = entries
            .Where(e => e.Name != "mimetype" && e.Name != containerEntry.Name &&
                        !packages.Any(p => p.EntryName == e.Name) &&
                        !packages.Any(p =>
                        {
                            var baseDir = PathRel.DirectoryOf(p.EntryName);
                            return p.Manifest.Any(m =>
                                PathRel.Resolve(PathRel.Join(baseDir, m.Href)).Normalized == e.Name);
                        }))
            .Select(e => new UnknownRawEntry(e.Name, e.Bytes, e.Sha256,
                new[] { "not referenced by any manifest item" }))
            .ToList();

        var setId = "mat-" + zipSha[..12];
        var material = new EpubMaterialSet(setId, stamp, zipBytes, zipSha, zipBytes.Length,
            zipResult.Entries, container, packages, unknown, diagnostics);

        if (packages.Count == 0 || unresolved.Count > 0)
        {
            return new ImportResult.SemanticUnresolvable(material, diagnostics, unresolved);
        }

        return new ImportResult.Success(material);
    }
}
