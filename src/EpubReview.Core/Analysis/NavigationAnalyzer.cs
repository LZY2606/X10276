using EpubReview.Core.Import;
using EpubReview.Core.Model;

namespace EpubReview.Core.Analysis;

public sealed partial class ReviewEngine
{
    private (List<NavTree>, List<BrokenAnchor>) AnalyzeNavigation(ResourceGraph graph)
    {
        var trees = new List<NavTree>();
        var broken = new List<BrokenAnchor>();

        foreach (var package in _material.Packages)
        {
            var navItem = package.Manifest.FirstOrDefault(m =>
                (m.Properties ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Contains("nav"));

            if (navItem is null)
            {
                if (_parameters.RequireNavDocument)
                {
                    AddFinding("NAV-DOCUMENT-MISSING",
                        "Package has no EPUB navigation document",
                        $"Package '{package.PackageId}' manifest contains no item with properties='nav'.",
                        FindingTopic.Manifest,
                        new[] { package.PackageId },
                        new SourceLocation(package.EntryName));
                }
                continue;
            }

            var resolved = ResolveResource(package.EntryName, navItem.Href);
            if (resolved.Candidates.Count == 0 ||
                !_entriesByName.TryGetValue(resolved.Candidates[0], out var entry))
            {
                AddFinding("NAV-DOCUMENT-MISSING",
                    "Navigation document entry is absent",
                    $"nav item '{navItem.Id}' resolves to '{resolved.NormalizedPath}'.",
                    FindingTopic.Manifest,
                    new[] { package.PackageId }, navItem.Location);
                continue;
            }

            var scan = XhtmlScanner.Scan(entry.Name, entry.Bytes);
            var navNodeItems = new List<(NavElement El, NavNode Node)>();
            var stack = new Stack<(int Depth, NavNode Node)>();
            var roots = new List<NavNode>();

            foreach (var navEl in scan.NavElements)
            {
                while (stack.Count > 0 && stack.Peek().Depth >= navEl.Depth) stack.Pop();

                var (pathPart, frag) = PathRel.SplitFragment(navEl.Href);
                ResolvedResource? targetResolved = pathPart.Length == 0
                    ? null
                    : ResolveResource(entry.Name, pathPart);

                string? targetNodeId = null;
                bool anchorOk = true;
                if (pathPart.Length == 0)
                {
                    targetNodeId = graph.Nodes.FirstOrDefault(n =>
                        n.CandidatePaths.Contains(entry.Name))?.NodeId;
                    if (!string.IsNullOrEmpty(frag) && !scan.AnchorIds.Contains(frag))
                    {
                        anchorOk = false;
                        var bid = "ba-" + Hashing.StableId(entry.Name, navEl.Href);
                        broken.Add(new BrokenAnchor(bid, targetNodeId ?? entry.Name,
                            navEl.Href, frag!, "fragment not defined in navigation document",
                            navEl.Location));
                        AddFinding("FRAGMENT-MISSING",
                            "Navigation fragment anchor does not exist",
                            $"nav link '{navEl.Href}' target fragment is missing.",
                            FindingTopic.Fragment,
                            new[] { bid }, navEl.Location, new[] { frag! });
                    }
                }
                else if (targetResolved?.Candidates.Count > 0)
                {
                    var targetNode = graph.Nodes.FirstOrDefault(n =>
                        n.CandidatePaths.Intersect(targetResolved!.Candidates).Any());
                    targetNodeId = targetNode?.NodeId;
                    if (!string.IsNullOrEmpty(frag) &&
                        _entriesByName.TryGetValue(targetResolved!.Candidates[0], out var te))
                    {
                        var tscan = XhtmlScanner.Scan(te.Name, te.Bytes);
                        if (!tscan.AnchorIds.Contains(frag))
                        {
                            anchorOk = false;
                            var bid = "ba-" + Hashing.StableId(te.Name, frag, navEl.Href);
                            broken.Add(new BrokenAnchor(bid,
                                targetNodeId ?? te.Name, navEl.Href, frag!,
                                "fragment not defined in target document", navEl.Location));
                            AddFinding("FRAGMENT-MISSING",
                                "Navigation cross-document anchor does not exist",
                                $"nav link '{navEl.Href}' -> '#{frag}' missing in '{te.Name}'.",
                                FindingTopic.Fragment,
                                new[] { bid }, navEl.Location, new[] { frag! });
                        }
                    }
                }
                else
                {
                    anchorOk = false;
                    var trav = targetResolved?.Traversal ?? false;
                    var bid = "ba-" + Hashing.StableId("missing", navEl.Href);
                    broken.Add(new BrokenAnchor(bid, entry.Name, navEl.Href, frag ?? "",
                        trav
                            ? "target path escapes the container"
                            : "target resource missing from container",
                        navEl.Location));
                    AddFinding(trav ? "PATH-TRAVERSAL" : "NAV-TARGET-MISSING",
                        trav
                            ? "Navigation link escapes the container"
                            : "Navigation target resource is missing",
                        $"nav link '{navEl.Href}' cannot be resolved.",
                        trav ? FindingTopic.PathTraversal : FindingTopic.Fragment,
                        new[] { bid }, navEl.Location,
                        (IReadOnlyList<string>)(targetResolved?.Candidates ?? new List<string>()));
                }

                var nodeId = "navn-" + Hashing.StableId(package.PackageId, navEl.Href,
                    roots.Count, navNodeItems.Count);
                var navNode = new NavNode(nodeId, navEl.Depth, navEl.Text,
                    pathPart.Length == 0 ? null : pathPart, frag, targetNodeId,
                    anchorOk, Array.Empty<NavNode>());
                navNodeItems.Add((navEl, navNode));

                if (stack.Count == 0) roots.Add(navNode);
                else
                {
                    var parent = stack.Peek().Node;
                    var children = parent.Children.Append(navNode).ToArray();
                    var replaced = parent with { Children = children };
                    ReplaceNavNode(roots, parent, replaced);
                    stack.Pop();
                    stack.Push((parent.Depth, replaced));
                }
                stack.Push((navEl.Depth, navNode));
            }

            trees.Add(new NavTree(package.PackageId, entry.Name, roots,
                broken.Count(b => b.SourceNodeId == entry.Name ||
                                 trees.SelectMany(t => t.Roots).Any())));
        }

        return (trees, broken);
    }

    private static bool ReplaceNavNode(IList<NavNode> roots, NavNode old, NavNode replacement)
    {
        for (int i = 0; i < roots.Count; i++)
        {
            if (roots[i].NavNodeId == old.NavNodeId)
            {
                roots[i] = replacement;
                return true;
            }
            if (ReplaceNavNode(roots[i].Children as IList<NavNode> ?? roots[i].Children.ToList(),
                    old, replacement))
            {
                return true;
            }
        }
        return false;
    }
}
