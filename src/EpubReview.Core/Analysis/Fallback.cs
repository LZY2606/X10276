namespace EpubReview.Core.Analysis;

public sealed record FallbackCandidate(
    string ChainId,
    string StartManifestId,
    int Position,
    string ManifestId,
    string MediaType,
    bool Present,
    bool Cycle,
    IReadOnlyList<string> CyclePath);

public sealed record FallbackChainReport(
    string ChainId,
    string StartManifestId,
    IReadOnlyList<FallbackCandidate> Candidates,
    bool HasCycle,
    bool MissingResource,
    IReadOnlyList<string> MissingManifestIds);
