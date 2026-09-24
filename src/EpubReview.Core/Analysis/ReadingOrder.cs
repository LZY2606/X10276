namespace EpubReview.Core.Analysis;

public sealed record ReadingOrderEntry(
    string EntryRefId,
    int Order,
    string PackageId,
    string Idref,
    string? ManifestId,
    string NormalizedPath,
    string MediaType,
    bool Linear,
    bool Duplicate,
    int FirstSeenOrder,
    bool Resolved,
    IReadOnlyList<string> CandidatePaths);

public sealed record SpineReport(
    string PackageId,
    IReadOnlyList<ReadingOrderEntry> Entries,
    int PrimaryOrderCount,
    int DuplicateCount,
    int UnresolvedCount);
