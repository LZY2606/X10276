using EpubReview.Core.Model;

namespace EpubReview.Core.Analysis;

public sealed record UnknownMetadataView(
    string MetadataId,
    string PackageId,
    string Key,
    string OuterXml,
    bool RoundTripped,
    string? RoundTripSha);

public sealed record AnalysisResult(
    string AnalysisId,
    string MaterialSetId,
    string MaterialContentVersion,
    string ParamsVersionId,
    int ParamsVersion,
    IReadOnlyList<string> ParentDependencyIds,
    DateTimeOffset ComputedAt,
    ResourceGraph Graph,
    IReadOnlyList<SpineReport> Spines,
    IReadOnlyList<NavTree> NavTrees,
    IReadOnlyList<BrokenAnchor> BrokenAnchors,
    IReadOnlyList<FallbackChainReport> FallbackChains,
    IReadOnlyList<MediaOverlayReport> MediaOverlays,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<UnknownMetadataView> UnknownMetadata,
    IReadOnlyList<string> Topics,
    InputDigest InputDigest)
{
    public string DependencyVersion =>
        Hashing.StableId("analysis", MaterialContentVersion, ParamsVersionId,
            string.Join("|", ParentDependencyIds.OrderBy(x => x)));
}
