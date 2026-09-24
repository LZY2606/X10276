using EpubReview.Core.Import;
using EpubReview.Core.Model;

namespace EpubReview.Core.Analysis;

internal sealed class ResolvedResource
{
    public string NormalizedPath { get; init; } = "";
    public bool Traversal { get; init; }
    public bool Absolute { get; init; }
    public List<string> Candidates { get; } = new();
    public string? AmbiguityReason { get; set; }
    public bool Present => Candidates.Count > 0 || Traversal;
}

public sealed partial class ReviewEngine
{
    private Dictionary<string, ZipEntryRecord> _entriesByName = null!;
    private Dictionary<string, List<ZipEntryRecord>> _entriesByIgnoreCase = null!;

    private void IndexEntries(IReadOnlyList<ZipEntryRecord> entries)
    {
        _entriesByName = new(StringComparer.Ordinal);
        _entriesByIgnoreCase = new(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries.Where(x => !x.IsDirectory))
        {
            _entriesByName[e.Name] = e;
            if (!_entriesByIgnoreCase.TryGetValue(e.Name, out var list))
            {
                list = new List<ZipEntryRecord>();
                _entriesByIgnoreCase[e.Name] = list;
            }
            list.Add(e);
        }
    }

    private ResolvedResource ResolveResource(string packageEntryName, string rawHref)
    {
        var (pathPart, _) = PathRel.SplitFragment(rawHref);
        var decoded = PathRel.PercentDecode(pathPart);
        var baseDir = PathRel.DirectoryOf(packageEntryName);
        var joined = PathRel.Join(baseDir, decoded);
        var resolved = PathRel.Resolve(joined);

        var result = new ResolvedResource
        {
            NormalizedPath = resolved.Normalized,
            Traversal = resolved.Traversal,
            Absolute = resolved.Absolute
        };

        if (resolved.EscapesContainer)
        {
            result.AmbiguityReason = "PATH-TRAVERSAL: normalized path escapes the container root";
            return result;
        }

        if (_entriesByName.TryGetValue(resolved.Normalized, out var exact))
        {
            result.Candidates.Add(exact.Name);
            return result;
        }

        if (!_parameters.CaseSensitivePaths &&
            _entriesByIgnoreCase.TryGetValue(resolved.Normalized, out var ci))
        {
            foreach (var c in ci) result.Candidates.Add(c.Name);
            if (result.Candidates.Count > 1)
            {
                result.AmbiguityReason =
                    $"case-insensitive match yields {result.Candidates.Count} legal entries; all retained";
            }
            else
            {
                result.AmbiguityReason =
                    "case-insensitive match only; exact-case entry is absent";
            }
        }

        return result;
    }

    private GraphNode GetOrCreateNode(ResourceGraph graph, ResolvedResource resolved,
        string? packageId, string? manifestId, string mediaType, string? declaredMediaType,
        ResourceOrigin origin)
    {
        var nodeId = "node-" + Hashing.StableId(
            "res", resolved.NormalizedPath,
            resolved.Candidates.Count == 1 ? resolved.Candidates[0] : "multi",
            packageId ?? "");

        var node = graph.Node(nodeId);
        if (node is null)
        {
            string sha = "";
            long size = 0;
            if (resolved.Candidates.Count >= 1 && _entriesByName.TryGetValue(resolved.Candidates[0], out var e))
            {
                sha = e.Sha256;
                size = (long)e.UncompressedSize;
            }
            node = new GraphNode(nodeId, resolved.NormalizedPath, packageId, manifestId,
                mediaType, declaredMediaType, size, sha,
                resolved.Candidates.Count > 0,
                resolved.Traversal || resolved.Absolute,
                new[] { origin },
                resolved.Candidates.ToArray(),
                resolved.AmbiguityReason);
            graph.Nodes.Add(node);
            return node;
        }

        var origins = node.Origins.ToList();
        if (!origins.Contains(origin)) origins.Add(origin);
        var updated = node with
        {
            Origins = origins,
            CandidatePaths = node.CandidatePaths.Union(resolved.Candidates).Distinct().ToArray(),
            AmbiguityReason = node.AmbiguityReason ?? resolved.AmbiguityReason
        };
        var idx = graph.Nodes.FindIndex(n => n.NodeId == nodeId);
        graph.Nodes[idx] = updated;
        return updated;
    }
}
