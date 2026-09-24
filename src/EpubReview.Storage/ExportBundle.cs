namespace EpubReview.Storage;

public sealed record ExportBundle(
    int BundleVersion,
    string ExportedAtInstant,
    string ContentSha256,
    IReadOnlyList<object> Materials,
    IReadOnlyList<object> Profiles,
    IReadOnlyList<object> Analyses,
    IReadOnlyList<object> Decisions,
    IReadOnlyList<object> Events);
