using EpubReview.Core.Model;

namespace EpubReview.Core.Analysis;

public sealed record SmilTiming(
    string ElementId,
    string? RawBegin,
    string? RawEnd,
    Scalar? Begin,
    Scalar? End,
    string? AudioNodeId,
    string? AudioSrc,
    string? TextNodeId,
    string? TextHref,
    string? TextFragment,
    bool Linked);

public sealed record MediaOverlayReport(
    string PackageId,
    string ManifestId,
    string? SmilNodeId,
    bool Linked,
    string? BrokenReason,
    IReadOnlyList<SmilTiming> Timings,
    int BrokenLinkCount);
