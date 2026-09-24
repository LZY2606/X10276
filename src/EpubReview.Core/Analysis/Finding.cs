namespace EpubReview.Core.Analysis;

public enum FindingStatus
{
    Open,
    Accepted,
    Rejected,
    Adjusted
}

public enum FindingTopic
{
    Container,
    ZipStructure,
    Manifest,
    Spine,
    Fallback,
    Fragment,
    PathTraversal,
    MediaOverlay,
    Metadata,
    Resource,
    Budget
}

public sealed record Finding(
    string FindingId,
    string Code,
    string Title,
    string Detail,
    FindingTopic Topic,
    IReadOnlyList<string> AffectedObjectIds,
    Model.SourceLocation? Location,
    IReadOnlyList<string> CandidateObjectIds,
    string ParamsVersion,
    string? ParentDependencyId,
    DateTimeOffset FirstComputedAt,
    FindingStatus Status = FindingStatus.Open,
    string? DecisionId = null)
{
    public bool HasCandidates => CandidateObjectIds.Count > 0;
}
