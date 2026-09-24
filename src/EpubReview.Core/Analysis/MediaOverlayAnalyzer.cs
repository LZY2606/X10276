using EpubReview.Core.Import;
using EpubReview.Core.Model;

namespace EpubReview.Core.Analysis;

public sealed partial class ReviewEngine
{
    private List<MediaOverlayReport> AnalyzeMediaOverlays(ResourceGraph graph)
    {
        var reports = new List<MediaOverlayReport>();

        foreach (var package in _material.Packages)
        {
            foreach (var item in package.Manifest.Where(m => m.MediaOverlayId is not null))
            {
                var overlayItem = package.FindManifestItem(item.MediaOverlayId!);
                var reportId = "mo-" + Hashing.StableId(package.PackageId, item.Id);

                if (overlayItem is null)
                {
                    reports.Add(new MediaOverlayReport(package.PackageId, item.Id, null,
                        false, "media-overlay idref undefined", Array.Empty<SmilTiming>(), 0));
                    continue;
                }

                var resolved = ResolveResource(package.EntryName, overlayItem.Href);
                GraphNode? smilNode = resolved.Candidates.Count > 0
                    ? graph.Node(graph.Nodes.FirstOrDefault(n =>
                        n.CandidatePaths.Contains(resolved.Candidates[0]))?.NodeId ?? "")
                    : null;

                if (resolved.Candidates.Count == 0 ||
                    !_entriesByName.TryGetValue(resolved.Candidates[0], out var smilEntry))
                {
                    reports.Add(new MediaOverlayReport(package.PackageId, item.Id,
                        smilNode?.NodeId, false,
                        resolved.Traversal ? "SMIL path escapes container" : "SMIL entry missing",
                        Array.Empty<SmilTiming>(), 1));
                    AddFinding("MEDIA-OVERLAY-BROKEN",
                        "Media overlay link is broken",
                        $"Item '{item.Id}' overlay '{overlayItem.Id}' -> '{overlayItem.Href}' cannot be loaded.",
                        FindingTopic.MediaOverlay,
                        new[] { reportId, item.Id }, item.Location);
                    continue;
                }

                var scan = SmilParser.Scan(smilEntry.Name, smilEntry.Bytes);
                int brokenLinks = 0;
                var timings = new List<SmilTiming>();

                foreach (var t in scan.Timings)
                {
                    bool linked = true;
                    if (t.AudioSrc is not null)
                    {
                        var r = ResolveResource(smilEntry.Name, t.AudioSrc);
                        if (r.Traversal || r.Candidates.Count == 0)
                        {
                            linked = false;
                            brokenLinks++;
                            AddFinding("MEDIA-OVERLAY-BROKEN",
                                "SMIL audio source is unresolvable",
                                $"'{t.AudioSrc}' in '{smilEntry.Name}' is missing or escapes the container.",
                                FindingTopic.MediaOverlay,
                                new[] { reportId, t.ElementId },
                                new SourceLocation(smilEntry.Name, XPointer: t.ElementId));
                        }
                    }
                    if (t.TextHref is not null)
                    {
                        var r = ResolveResource(smilEntry.Name, t.TextHref);
                        if (r.Traversal || r.Candidates.Count == 0)
                        {
                            linked = false;
                            brokenLinks++;
                            AddFinding("MEDIA-OVERLAY-BROKEN",
                                "SMIL text source is unresolvable",
                                $"'{t.TextHref}' in '{smilEntry.Name}' is missing or escapes the container.",
                                FindingTopic.MediaOverlay,
                                new[] { reportId, t.ElementId },
                                new SourceLocation(smilEntry.Name, XPointer: t.ElementId));
                        }
                        else if (!string.IsNullOrEmpty(t.TextFragment) &&
                                 _entriesByName.TryGetValue(r.Candidates[0], out var textEntry))
                        {
                            var tscan = XhtmlScanner.Scan(textEntry.Name, textEntry.Bytes);
                            if (!tscan.AnchorIds.Contains(t.TextFragment))
                            {
                                linked = false;
                                brokenLinks++;
                                AddFinding("FRAGMENT-MISSING",
                                    "SMIL text fragment anchor does not exist",
                                    $"#{t.TextFragment} missing in '{textEntry.Name}'.",
                                    FindingTopic.Fragment,
                                    new[] { reportId, t.ElementId },
                                    new SourceLocation(smilEntry.Name, XPointer: t.ElementId),
                                    new[] { t.TextFragment });
                            }
                        }
                    }
                    timings.Add(t with { Linked = linked, AudioNodeId = null, TextNodeId = null });
                }

                reports.Add(new MediaOverlayReport(package.PackageId, item.Id,
                    smilNode?.NodeId, true,
                    brokenLinks > 0 ? $"{brokenLinks} broken inner reference(s)" : null,
                    timings, brokenLinks));
            }
        }

        return reports;
    }
}
