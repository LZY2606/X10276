using EpubReview.Core.Model;

namespace EpubReview.Core.Analysis;

public sealed partial class ReviewEngine
{
    private List<FallbackChainReport> AnalyzeFallbacks(ResourceGraph graph)
    {
        var reports = new List<FallbackChainReport>();

        foreach (var package in _material.Packages)
        {
            var byId = package.Manifest.ToDictionary(m => m.Id);

            foreach (var start in package.Manifest.Where(m => m.FallbackId is not null ||
                                                            package.Manifest.Any(x => x.FallbackId == m.Id)))
            {
                if (start.FallbackId is null) continue;

                var chainId = "fb-" + Hashing.StableId(package.PackageId, start.Id);
                if (reports.Any(r => r.ChainId == chainId)) continue;

                var candidates = new List<FallbackCandidate>();
                var path = new List<string>();
                var cycle = false;
                var cyclePath = Array.Empty<string>();
                var missing = new List<string>();
                var visited = new HashSet<string>();
                var current = start;
                int pos = 0;

                while (true)
                {
                    bool present = _entriesByName.Values.Any(e =>
                    {
                        var resolved = ResolveResource(package.EntryName, current.Href);
                        return resolved.Candidates.Contains(e.Name);
                    }) || package.Manifest.Any(m => m.Id == current.Id &&
                        ResolveResource(package.EntryName, m.Href).Candidates.Count > 0);

                    var itemResolved = ResolveResource(package.EntryName, current.Href);
                    present = itemResolved.Candidates.Count > 0;

                    candidates.Add(new FallbackCandidate(chainId, start.Id, pos,
                        current.Id, current.MediaType, present, false, Array.Empty<string>()));
                    if (!present) missing.Add(current.Id);

                    path.Add(current.Id);
                    if (!visited.Add(current.Id))
                    {
                        cycle = true;
                        var cycleStart = path.IndexOf(current.Id);
                        cyclePath = path.Skip(cycleStart).Concat(new[] { current.Id }).ToArray();
                        for (int i = 0; i < candidates.Count; i++)
                        {
                            if (cyclePath.Contains(candidates[i].ManifestId))
                            {
                                candidates[i] = candidates[i] with
                                {
                                    Cycle = true,
                                    CyclePath = cyclePath
                                };
                            }
                        }
                        break;
                    }

                    if (current.FallbackId is null) break;
                    if (!byId.TryGetValue(current.FallbackId, out var next))
                    {
                        missing.Add(current.FallbackId + "?");
                        AddFinding("FALLBACK-DANGLING",
                            "Fallback points to a manifest id that does not exist",
                            $"Item '{current.Id}' declares fallback '{current.FallbackId}', which is absent from the manifest.",
                            FindingTopic.Fallback,
                            new[] { chainId, current.Id }, current.Location,
                            new[] { current.FallbackId! });
                        break;
                    }
                    current = next;
                    pos++;
                }

                if (cycle)
                {
                    AddFinding("FALLBACK-CYCLE",
                        "Fallback chain contains a cycle",
                        "Cycle path: " + string.Join(" -> ", cyclePath),
                        FindingTopic.Fallback,
                        new[] { chainId }.Concat(cyclePath).ToArray(),
                        start.Location,
                        cyclePath,
                        parentDependencyId: chainId);
                }

                var missingReal = missing.Where(m => !m.EndsWith('?')).Distinct().ToList();
                if (missingReal.Count > 0)
                {
                    AddFinding("FALLBACK-MISSING-RESOURCE",
                        "Fallback chain references missing container resources",
                        "Missing manifest items: " + string.Join(", ", missingReal) +
                        ". Missing resources are reported separately from cycle detection.",
                        FindingTopic.Fallback,
                        new[] { chainId }.Concat(missingReal).ToArray(),
                        start.Location,
                        missingReal,
                        parentDependencyId: chainId);
                }

                reports.Add(new FallbackChainReport(chainId, start.Id, candidates,
                    cycle, missingReal.Count > 0, missingReal));
            }
        }

        return reports;
    }
}
