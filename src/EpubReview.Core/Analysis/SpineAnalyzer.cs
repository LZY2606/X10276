using EpubReview.Core.Model;

namespace EpubReview.Core.Analysis;

public sealed partial class ReviewEngine
{
    private List<SpineReport> AnalyzeSpines(ResourceGraph graph)
    {
        var reports = new List<SpineReport>();

        foreach (var package in _material.Packages)
        {
            var entriesOut = new List<ReadingOrderEntry>();
            var seen = new Dictionary<string, int>();
            int unresolved = 0;
            int duplicates = 0;

            foreach (var spineRef in package.Spine)
            {
                var manifestItem = package.FindManifestItem(spineRef.Idref);
                var entryRefId = "ro-" + Hashing.StableId(
                    package.PackageId, spineRef.Order, spineRef.Idref);

                if (manifestItem is null)
                {
                    unresolved++;
                    entriesOut.Add(new ReadingOrderEntry(entryRefId, spineRef.Order,
                        package.PackageId, spineRef.Idref, null, "", "",
                        spineRef.Linear != "no", false, -1, false, Array.Empty<string>()));
                    AddFinding("SPINE-IDREF-UNKNOWN",
                        "Spine itemref points to an undefined manifest id",
                        $"idref '{spineRef.Idref}' at order {spineRef.Order} has no manifest item.",
                        FindingTopic.Spine,
                        new[] { entryRefId, package.PackageId }, spineRef.Location);
                    continue;
                }

                var resolved = ResolveResource(package.EntryName, manifestItem.Href);
                var node = GetOrCreateNode(graph, resolved, package.PackageId,
                    manifestItem.Id, manifestItem.MediaType, manifestItem.MediaType,
                    ResourceOrigin.Spine);
                AddEdge(graph, NodeForPackage(graph, package), node, ReferenceKind.Spine,
                    null, package.PackageId, manifestItem.Id, spineRef.Location);

                bool duplicate = seen.TryGetValue(manifestItem.Id, out var firstOrder);
                if (!duplicate) seen[manifestItem.Id] = spineRef.Order;
                else duplicates++;

                if (duplicate)
                {
                    AddFinding("SPINE-DUPLICATE-IDREF",
                        "Duplicate manifest id in spine reading order",
                        $"idref '{manifestItem.Id}' appears again at order {spineRef.Order} (first at order {firstOrder}).",
                        FindingTopic.Spine,
                        new[] { entryRefId, node.NodeId }, spineRef.Location);
                }

                if (!node.Present && !node.Traversal)
                {
                    unresolved++;
                    AddFinding("RESOURCE-MISSING",
                        "Spine target resource is absent",
                        $"Spine target '{resolved.NormalizedPath}' for manifest item '{manifestItem.Id}' is not in the container.",
                        FindingTopic.Resource,
                        new[] { entryRefId, node.NodeId }, manifestItem.Location,
                        node.CandidatePaths);
                }

                if (node.Traversal)
                {
                    AddFinding("PATH-TRAVERSAL",
                        "Spine target path escapes the container",
                        $"'{manifestItem.Href}' normalizes outside the container root.",
                        FindingTopic.PathTraversal,
                        new[] { entryRefId, node.NodeId }, manifestItem.Location);
                }

                entriesOut.Add(new ReadingOrderEntry(entryRefId, spineRef.Order,
                    package.PackageId, spineRef.Idref, manifestItem.Id,
                    resolved.NormalizedPath, manifestItem.MediaType,
                    spineRef.Linear != "no", duplicate,
                    duplicate ? firstOrder : spineRef.Order,
                    node.Present, node.CandidatePaths));
            }

            var referencedIds = new HashSet<string>(package.Spine.Select(s => s.Idref));
            foreach (var item in package.Manifest)
            {
                if (!referencedIds.Contains(item.Id) && item.MediaType is not "application/x-dtbncx+xml")
                {
                    var resolved = ResolveResource(package.EntryName, item.Href);
                    var node = GetOrCreateNode(graph, resolved, package.PackageId, item.Id,
                        item.MediaType, item.MediaType, ResourceOrigin.ContentReference);
                    if (!node.Present && !node.Traversal)
                    {
                        AddFinding("RESOURCE-MISSING",
                            "Manifest-only resource is absent (outside spine)",
                            $"Manifest item '{item.Id}' -> '{resolved.NormalizedPath}' is not in the container; the spine does not replace the reference graph.",
                            FindingTopic.Resource,
                            new[] { node.NodeId }, item.Location, node.CandidatePaths);
                    }
                }
            }

            reports.Add(new SpineReport(package.PackageId, entriesOut,
                entriesOut.Count, duplicates, unresolved));
        }

        return reports;
    }

    private GraphNode NodeForPackage(ResourceGraph graph, PackageDocument package)
    {
        var nodeId = "node-package-" + package.PackageId;
        var existing = graph.Node(nodeId);
        if (existing is not null) return existing;
        var node = new GraphNode(nodeId, package.EntryName, package.PackageId, null,
            "application/oebps-package+xml", "application/oebps-package+xml",
            package.RawBytes.Length, package.Sha256, true, false,
            new[] { ResourceOrigin.Container }, Array.Empty<string>(), null);
        graph.Nodes.Add(node);
        return node;
    }
}
