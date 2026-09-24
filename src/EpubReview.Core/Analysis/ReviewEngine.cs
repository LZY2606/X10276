using EpubReview.Core.Import;
using EpubReview.Core.Model;

namespace EpubReview.Core.Analysis;

public sealed partial class ReviewEngine
{
    private AnalysisParams _parameters = AnalysisParams.V1;
    private readonly List<Finding> _findings = new();
    private DateTimeOffset _now;
    private EpubMaterialSet _material = null!;

    public AnalysisResult Analyze(EpubMaterialSet material,
        AnalysisParams? parameters = null,
        IReadOnlyList<string>? parentDependencyIds = null,
        IReadOnlySet<FindingTopic>? topics = null,
        DateTimeOffset? now = null)
    {
        _material = material;
        _parameters = parameters ?? AnalysisParams.V1;
        _now = now ?? DateTimeOffset.UtcNow;
        _findings.Clear();
        IndexEntries(material.Entries);

        var graph = new ResourceGraph();
        BuildManifestGraph(graph);
        var spines = AnalyzeSpines(graph);
        var (navTrees, brokenAnchors) = AnalyzeNavigation(graph);
        var fallbacks = AnalyzeFallbacks(graph);
        var overlays = AnalyzeMediaOverlays(graph);
        var unknownMetadata = AnalyzeMetadata();
        AnalyzeContainer();
        AnalyzeZipStructure();

        var allTopics = Enum.GetValues<FindingTopic>()
            .Where(t => _findings.Any(f => f.Topic == t))
            .Select(t => t.ToString())
            .OrderBy(x => x)
            .ToArray();

        var digest = InputDigest.Of(material.OriginalZipSha256,
            material.OriginalZipLength,
            material.Entries.Select(e => e.Sha256).ToArray());

        var partialResult = new AnalysisResult(
            "", material.MaterialSetId, material.ContentVersion,
            _parameters.ParamsVersionId, _parameters.Version,
            (parentDependencyIds ?? Array.Empty<string>()).OrderBy(x => x).ToArray(),
            _now, graph, spines, navTrees, brokenAnchors,
            fallbacks, overlays,
            _findings.OrderBy(f => f.FindingId).ToArray(),
            unknownMetadata, allTopics, digest);

        return partialResult with { AnalysisId = "an-" + partialResult.DependencyVersion[..16] };
    }

    private void AddFinding(string code, string title, string detail, FindingTopic topic,
        IEnumerable<string> affected, SourceLocation? location,
        IEnumerable<string>? candidates = null,
        string? parentDependencyId = null)
    {
        var affectedList = affected.ToArray();
        var candidateList = candidates?.ToArray() ?? Array.Empty<string>();
        var id = "find-" + Hashing.StableId(
            code, string.Join("|", affectedList.OrderBy(x => x)),
            string.Join("|", candidateList.OrderBy(x => x)),
            _parameters.ParamsVersionId);

        if (_findings.Any(f => f.FindingId == id)) return;

        _findings.Add(new Finding(
            id, code, title, detail, topic, affectedList, location,
            candidateList, _parameters.ParamsVersionId, parentDependencyId,
            _now));
    }

    private void AddEdge(ResourceGraph graph, GraphNode from, GraphNode? to, ReferenceKind kind,
        string? fragment, string packageId, string? itemId, SourceLocation location)
    {
        var edgeId = "edge-" + Hashing.StableId(
            "e", from.NodeId, to?.NodeId ?? "unresolved", kind,
            fragment ?? "", packageId, itemId ?? "", location.ToString());
        if (graph.Edges.Any(e => e.EdgeId == edgeId)) return;
        graph.Edges.Add(new GraphEdge(edgeId, from.NodeId,
            to?.NodeId ?? "node-unresolved:" + Hashing.StableId("u", from.NodeId, kind, fragment ?? "", itemId ?? ""),
            kind, fragment, packageId, itemId, location));
    }
}
