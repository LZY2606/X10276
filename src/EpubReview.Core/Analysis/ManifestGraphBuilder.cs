using EpubReview.Core.Import;
using EpubReview.Core.Model;

namespace EpubReview.Core.Analysis;

public sealed partial class ReviewEngine
{
    private void BuildManifestGraph(ResourceGraph graph)
    {
        foreach (var package in _material.Packages)
        {
            var pkgNode = NodeForPackage(graph, package);
            foreach (var item in package.Manifest)
            {
                var resolved = ResolveResource(package.EntryName, item.Href);
                var node = GetOrCreateNode(graph, resolved, package.PackageId, item.Id,
                    item.MediaType, item.MediaType, ResourceOrigin.Manifest);
                AddEdge(graph, pkgNode, node, ReferenceKind.ManifestItem, null,
                    package.PackageId, item.Id, item.Location);

                if (node.Traversal)
                {
                    AddFinding("PATH-TRAVERSAL",
                        "Manifest href escapes the container root",
                        $"'{item.Href}' resolves to '{resolved.NormalizedPath}'.",
                        FindingTopic.PathTraversal,
                        new[] { node.NodeId, package.PackageId }, item.Location);
                }
                else if (resolved.Candidates.Count > 1)
                {
                    AddFinding("RESOURCE-AMBIGUOUS",
                        "Resource path cannot be resolved uniquely",
                        node.AmbiguityReason ?? "multiple legal candidates",
                        FindingTopic.Resource,
                        new[] { node.NodeId }, item.Location,
                        resolved.Candidates);
                }

                if (item.FallbackId is not null && package.FindManifestItem(item.FallbackId) is { } fbItem)
                {
                    var fbResolved = ResolveResource(package.EntryName, fbItem.Href);
                    var fbNode = GetOrCreateNode(graph, fbResolved, package.PackageId,
                        fbItem.Id, fbItem.MediaType, fbItem.MediaType, ResourceOrigin.Manifest);
                    AddEdge(graph, node, fbNode, ReferenceKind.Fallback, null,
                        package.PackageId, item.Id, item.Location);
                }

                if (item.MediaOverlayId is not null)
                {
                    var overlayItem = package.FindManifestItem(item.MediaOverlayId);
                    if (overlayItem is null)
                    {
                        AddFinding("MEDIA-OVERLAY-DANGLING",
                            "media-overlay attribute points to a missing manifest id",
                            $"Item '{item.Id}' declares media-overlay '{item.MediaOverlayId}'.",
                            FindingTopic.MediaOverlay,
                            new[] { node.NodeId, package.PackageId }, item.Location,
                            new[] { item.MediaOverlayId! });
                    }
                    else
                    {
                        var oResolved = ResolveResource(package.EntryName, overlayItem.Href);
                        var oNode = GetOrCreateNode(graph, oResolved, package.PackageId,
                            overlayItem.Id, overlayItem.MediaType, overlayItem.MediaType,
                            ResourceOrigin.Manifest);
                        AddEdge(graph, node, oNode, ReferenceKind.MediaOverlay, null,
                            package.PackageId, item.Id, item.Location);
                        if (!oNode.Present)
                        {
                            AddFinding("MEDIA-OVERLAY-BROKEN",
                                "Media overlay SMIL document is missing from the container",
                                $"SMIL '{overlayItem.Href}' for '{item.Id}' is absent.",
                                FindingTopic.MediaOverlay,
                                new[] { node.NodeId, oNode.NodeId }, item.Location);
                        }
                    }
                }

                ScanContentReferences(graph, package, item, node);
            }
        }
    }

    private void ScanContentReferences(ResourceGraph graph, PackageDocument package,
        ManifestItem item, GraphNode ownerNode)
    {
        if (!_entriesByName.TryGetValue(
                ResolveResource(package.EntryName, item.Href).Candidates.FirstOrDefault() ?? "",
                out var entry))
        {
            return;
        }

        if (item.MediaType is "application/xhtml+xml" or "text/html")
        {
            var scan = XhtmlScanner.Scan(entry.Name, entry.Bytes);
            foreach (var (href, loc) in scan.Hrefs)
            {
                var (pathPart, frag) = PathRel.SplitFragment(href);
                if (pathPart.Length == 0)
                {
                    if (!string.IsNullOrEmpty(frag) && !scan.AnchorIds.Contains(frag))
                    {
                        AddFinding("FRAGMENT-MISSING",
                            "Internal fragment anchor does not exist",
                            $"Fragment '#{frag}' in '{entry.Name}' has no matching id.",
                            FindingTopic.Fragment,
                            new[] { ownerNode.NodeId }, loc,
                            new[] { frag! });
                    }
                    continue;
                }
                if (href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    href.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var resolved = ResolveResource(entry.Name, pathPart);
                var target = GetOrCreateNode(graph, resolved, null, null,
                    "", null, ResourceOrigin.ContentReference);
                AddEdge(graph, ownerNode, target, ReferenceKind.XhtmlHref, frag,
                    package.PackageId, item.Id, loc);

                if (target.Traversal)
                {
                    AddFinding("PATH-TRAVERSAL",
                        "Content link escapes the container root",
                        $"Link '{href}' in '{entry.Name}' resolves outside the container.",
                        FindingTopic.PathTraversal,
                        new[] { ownerNode.NodeId, target.NodeId }, loc);
                }
                else if (!target.Present)
                {
                    AddFinding("FRAGMENT-MISSING",
                        "Content link target resource is missing",
                        $"Link '{href}' in '{entry.Name}' has no ZIP entry.",
                        FindingTopic.Fragment,
                        new[] { ownerNode.NodeId, target.NodeId }, loc,
                        target.CandidatePaths);
                }
                else if (!string.IsNullOrEmpty(frag) &&
                         _entriesByName.TryGetValue(target.CandidatePaths[0], out var targetEntry))
                {
                    var targetScan = XhtmlScanner.Scan(targetEntry.Name, targetEntry.Bytes);
                    if (!targetScan.AnchorIds.Contains(frag))
                    {
                        AddFinding("FRAGMENT-MISSING",
                            "Cross-document fragment anchor does not exist",
                            $"Fragment '#{frag}' is not defined in '{targetEntry.Name}'.",
                            FindingTopic.Fragment,
                            new[] { ownerNode.NodeId, target.NodeId }, loc,
                            new[] { frag! });
                    }
                }
            }
        }
    }
}
