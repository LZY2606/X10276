namespace EpubReview.Core.Analysis;

public sealed partial class ReviewEngine
{
    private List<UnknownMetadataView> AnalyzeMetadata()
    {
        var views = new List<UnknownMetadataView>();

        foreach (var package in _material.Packages)
        {
            foreach (var md in package.Metadata.Where(m => !m.Recognized))
            {
                var metadataId = "md-" + Hashing.StableId(package.PackageId,
                    md.Key, md.OuterXml, md.Location.Line);

                var roundTripped = package.RawText.Contains(md.OuterXml,
                    StringComparison.Ordinal);

                views.Add(new UnknownMetadataView(
                    metadataId, package.PackageId, md.Key, md.OuterXml,
                    roundTripped,
                    roundTripped ? Hashing.Sha256Hex(md.OuterXml) : null));

                if (!roundTripped)
                {
                    AddFinding("METADATA-ROUNDTRIP-FAILED",
                        "Unknown metadata element did not survive document round-trip",
                        $"Element '{md.Key}' at {md.Location} was altered during parse/serialize.",
                        FindingTopic.Metadata,
                        new[] { metadataId, package.PackageId }, md.Location);
                }
            }
        }

        return views;
    }

    private void AnalyzeContainer()
    {
        if (_material.Container is null) return;
        if (_material.Container.RootFiles.Count > 1)
        {
            AddFinding("CONTAINER-MULTI-PACKAGE",
                "container.xml declares multiple packages",
                "All rootfiles are retained; each package gets its own spine and graphs.",
                FindingTopic.Container,
                _material.Container.RootFiles.Select(r =>
                    "rf-" + Hashing.StableId(r.FullPath)).ToArray(),
                new Model.SourceLocation(_material.Container.EntryName),
                _material.Container.RootFiles.Select(r => r.FullPath).ToArray());
        }
    }

    private void AnalyzeZipStructure()
    {
        foreach (var d in _material.ImportDiagnostics
                     .Where(d => d.Code is "ZIP-CRC-MISMATCH" or "ZIP-DATA-OUT-OF-RANGE"))
        {
            AddFinding(d.Code,
                d.Code == "ZIP-CRC-MISMATCH" ? "ZIP entry CRC mismatch" : "ZIP data range error",
                d.Message, FindingTopic.ZipStructure,
                new[] { "zip:" + (d.Location?.Source ?? "?") }, d.Location);
        }
    }
}
