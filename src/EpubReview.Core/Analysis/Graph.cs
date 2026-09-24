using EpubReview.Core.Model;

namespace EpubReview.Core.Analysis;

public enum ResourceOrigin
{
    Manifest,
    Spine,
    ContentReference,
    Container,
    Orphan
}

public sealed record GraphNode(
    string NodeId,
    string NormalizedPath,
    string? PackageId,
    string? ManifestId,
    string MediaType,
    string? DeclaredMediaType,
    long SizeBytes,
    string EntrySha256,
    bool Present,
    bool Traversal,
    IReadOnlyList<ResourceOrigin> Origins,
    IReadOnlyList<string> CandidatePaths,
    string? AmbiguityReason)
{
    public bool Ambiguous => CandidatePaths.Count > 1;
}

public enum ReferenceKind
{
    Spine,
    ManifestItem,
    Fallback,
    MediaOverlay,
    NavLink,
    XhtmlHref,
    SmilTextRef,
    SmilAudioRef,
    ContainerRootFile,
    MetadataLink
}

public sealed record GraphEdge(
    string EdgeId,
    string FromNodeId,
    string ToNodeId,
    ReferenceKind Kind,
    string? Fragment,
    string? SourcePackageId,
    string? SourceItemId,
    SourceLocation Location)
{
    public bool HasFragment => !string.IsNullOrEmpty(Fragment);
}

public sealed class ResourceGraph
{
    public List<GraphNode> Nodes { get; } = new();
    public List<GraphEdge> Edges { get; } = new();

    public GraphNode? Node(string id) => Nodes.FirstOrDefault(n => n.NodeId == id);
    public IEnumerable<GraphEdge> Outgoing(string nodeId) => Edges.Where(e => e.FromNodeId == nodeId);
}
