using EpubReview.Core.Model;

namespace EpubReview.Core.Analysis;

public sealed record NavNode(
    string NavNodeId,
    int Depth,
    string? Text,
    string? Href,
    string? Fragment,
    string? TargetNodeId,
    bool AnchorResolved,
    IReadOnlyList<NavNode> Children);

public sealed record NavTree(
    string PackageId,
    string? NavDocumentPath,
    IReadOnlyList<NavNode> Roots,
    int BrokenAnchorCount);

public sealed record BrokenAnchor(
    string AnchorId,
    string SourceNodeId,
    string Href,
    string Fragment,
    string Reason,
    SourceLocation Location);
