namespace EpubCasebook.Core;

public static class ReviewEngine
{
    public static ReviewRun Build(
        string caseId,
        string? parentRunId,
        byte[] epubBytes,
        RuleParams p,
        string? requestedPackage,
        List<string> affectedMaterials,
        string nowUtc)
    {
        var diags = new List<Diagnostic>();
        var entries = ZipRead.ReadAll(epubBytes, p, diags, out var budget, out var malformedZip);
        var digest = Digest.InputDigest(
            p.RuleVersion,
            entries.Select(e => (e.Name, Digest.Sha256Hex(e.Bytes))),
            requestedPackage);
        var runId = Digest.RunId(digest);

        ReviewRun Finish(RunResultKind kind, ParsedContainer? pc, string? selected) => new(
            runId, caseId, digest, p.RuleVersion, parentRunId, kind,
            pc?.Rootfiles ?? new List<string>(), selected,
            new List<ReadingEntry>(), new List<GraphNode>(), new List<GraphEdge>(),
            new List<NavNode>(), new List<BrokenAnchor>(), new List<FallbackCandidate>(),
            diags, new List<string>(), affectedMaterials, nowUtc);

        if (malformedZip)
            return Finish(RunResultKind.Malformed, null, null);

        var pc = EpubParser.Parse(entries);
        diags.AddRange(pc.Diagnostics);

        if (pc.Fatal == RunResultKind.Malformed)
            return Finish(RunResultKind.Malformed, pc, null);
        if (pc.Fatal == RunResultKind.Unsolvable)
            return Finish(RunResultKind.Unsolvable, pc, null);

        string? selected = null;
        if (requestedPackage != null && pc.Rootfiles.Contains(requestedPackage, StringComparer.Ordinal))
            selected = requestedPackage;
        else if (requestedPackage != null)
            diags.Add(new Diagnostic(Diag.PackageNotCandidate, "warning",
                $"请求的 package 不在合法候选中，回退到字典序首个候选: {requestedPackage}",
                "META-INF/container.xml", requestedPackage));
        selected ??= pc.Rootfiles.First();

        if (pc.MalformedPackages.Contains(selected) || !pc.Packages.TryGetValue(selected, out var pkg))
            return Finish(RunResultKind.Malformed, pc, selected);

        var kind = budget ? RunResultKind.BudgetExhausted : RunResultKind.Ok;
        var byId = pkg.Manifest.Where(m => m.Id.Length > 0)
            .GroupBy(m => m.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var byHref = pkg.Manifest.Where(m => m.Href != null)
            .GroupBy(m => m.Href!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var spineIndexById = new Dictionary<string, int>(StringComparer.Ordinal);
        var seenIdrefs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sr in pkg.Spine)
        {
            if (!seenIdrefs.Add(sr.Idref))
                diags.Add(new Diagnostic(Diag.SpineDuplicate, "error",
                    $"spine 中 idref 重复出现: {sr.Idref}（spine 只定义主顺序，重复项不新增资源）",
                    $"{selected}#spine[{sr.Index}]", sr.Idref));
            else
                spineIndexById[sr.Idref] = sr.Index;
            if (!byId.ContainsKey(sr.Idref))
                diags.Add(new Diagnostic(Diag.SpineMissingIdref, "error",
                    $"spine 引用的 idref 不在 manifest 中: {sr.Idref}",
                    $"{selected}#spine[{sr.Index}]", sr.Idref));
        }

        var overlaysByContent = new Dictionary<string, OverlayDoc>(StringComparer.Ordinal);
        foreach (var item in pkg.Manifest)
        {
            if (item.MediaOverlay == null)
                continue;
            if (!byId.TryGetValue(item.MediaOverlay, out var ovItem) || ovItem.Href == null)
            {
                diags.Add(new Diagnostic(Diag.OverlayBrokenLink, "error",
                    $"manifest 项 {item.Id} 的 media-overlay 指向不存在的项: {item.MediaOverlay}",
                    selected, item.MediaOverlay));
                continue;
            }
            if (pc.OverlaysByPath.TryGetValue(ovItem.Href, out var ov) && item.Href != null)
                overlaysByContent[item.Href] = ov;
        }

        var reading = new List<ReadingEntry>();
        foreach (var sr in pkg.Spine)
        {
            if (!byId.TryGetValue(sr.Idref, out var item) || item.Href == null)
                continue;
            double? begin = null, end = null;
            if (overlaysByContent.TryGetValue(item.Href, out var ov))
            {
                var begins = ov.Clips.Where(c => c.BeginSec.HasValue).Select(c => c.BeginSec!.Value).ToList();
                var ends = ov.Clips.Where(c => c.EndSec.HasValue).Select(c => c.EndSec!.Value).ToList();
                if (begins.Count > 0) begin = begins.Min();
                if (ends.Count > 0) end = ends.Max();
            }
            reading.Add(new ReadingEntry(sr.Index, sr.Idref, item.Href, sr.Linear, begin, end));
        }

        var nodes = new List<GraphNode>();
        var nodeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in pkg.Manifest.OrderBy(m => m.Href ?? m.RawHref, StringComparer.Ordinal))
        {
            var key = item.Href ?? ("raw:" + item.RawHref);
            if (!nodeIds.Add(key))
                continue;
            nodes.Add(new GraphNode(key, item.Href ?? item.RawHref, item.MediaType,
                spineIndexById.ContainsKey(item.Id),
                spineIndexById.TryGetValue(item.Id, out var si) ? si : null, false));
        }

        var edges = new List<GraphEdge>();
        var broken = new List<BrokenAnchor>();

        GraphNode Phantom(string rawRef)
        {
            var key = "missing:" + rawRef;
            if (nodeIds.Add(key))
                nodes.Add(new GraphNode(key, rawRef, "missing", false, null, true));
            return nodes.First(n => n.Id == key);
        }

        void CheckTarget(string source, string rawHref, string? frag, string edgeKind, string? overlayContext)
        {
            var (pathOnly, fragment) = ContainerPath.SplitFragment(rawHref);
            fragment ??= frag;
            if (pathOnly.Length == 0)
            {
                if (fragment != null && !pc.IdsByDoc.TryGetValue(source, out var selfIds))
                    return;
                if (fragment != null && pc.IdsByDoc.TryGetValue(source, out var idsHere) && !idsHere.Contains(fragment))
                {
                    var code = overlayContext != null ? Diag.OverlayBrokenLink : Diag.FragmentMissing;
                    diags.Add(new Diagnostic(code, "error",
                        $"锚点 #{fragment} 在 {source} 中不存在", source, rawHref));
                    broken.Add(new BrokenAnchor(source, rawHref, fragment, "fragment-missing", source));
                }
                return;
            }
            if (!ContainerPath.TryNormalize(ContainerPath.DirOf(source), pathOnly, out var target, out var err))
            {
                diags.Add(new Diagnostic(Diag.PathEscape, "error",
                    $"引用路径越出容器，已阻止: {err}", source, rawHref));
                broken.Add(new BrokenAnchor(source, rawHref, fragment, "escaped", source));
                edges.Add(new GraphEdge(source, Phantom(rawHref).Id, edgeKind, rawHref, fragment, "escaped"));
                return;
            }
            if (!pc.EntryNames.Contains(target))
            {
                var code = overlayContext != null ? Diag.OverlayBrokenLink : Diag.MissingResource;
                diags.Add(new Diagnostic(code, "error",
                    $"引用目标在容器中不存在: {target}", source, rawHref));
                broken.Add(new BrokenAnchor(source, rawHref, fragment, "missing", source));
                edges.Add(new GraphEdge(source, Phantom(rawHref).Id, edgeKind, rawHref, fragment, "missing"));
                return;
            }
            if (fragment != null && pc.IdsByDoc.TryGetValue(target, out var ids) && !ids.Contains(fragment))
            {
                var code = overlayContext != null ? Diag.OverlayBrokenLink : Diag.FragmentMissing;
                diags.Add(new Diagnostic(code, "error",
                    $"锚点 #{fragment} 在 {target} 中不存在", source, rawHref));
                broken.Add(new BrokenAnchor(source, rawHref, fragment, "fragment-missing", source));
                edges.Add(new GraphEdge(source, target, edgeKind, rawHref, fragment, "fragment-missing"));
                return;
            }
            edges.Add(new GraphEdge(source, target, edgeKind, rawHref, fragment, "ok"));
        }

        foreach (var (doc, refs) in pc.RefsByDoc.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!byHref.ContainsKey(doc) && !pc.EntryNames.Contains(doc))
                continue;
            foreach (var (rawHref, element) in refs)
                CheckTarget(doc, rawHref, null, $"content-{element}", null);
        }

        foreach (var (smilPath, ov) in pc.OverlaysByPath.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            foreach (var clip in ov.Clips)
            {
                if (clip.TextRaw.Length > 0)
                    CheckTarget(smilPath, clip.TextRaw, clip.TextFrag, "overlay-text", smilPath);
                if (clip.AudioRaw.Length > 0)
                    CheckTarget(smilPath, clip.AudioRaw, null, "overlay-audio", smilPath);
            }
        }

        var navTree = new List<NavNode>();
        foreach (var (navPath, tree) in pc.NavTreeByDoc.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            NavNode Resolve(NavNode n)
            {
                var status = "ok";
                if (n.RawHref != null)
                {
                    if (n.Href == null)
                        status = "escaped";
                    else if (!pc.EntryNames.Contains(n.Href))
                        status = "missing";
                    else if (n.Fragment != null && pc.IdsByDoc.TryGetValue(n.Href, out var ids) && !ids.Contains(n.Fragment))
                        status = "fragment-missing";
                    if (status != "ok")
                    {
                        var code = status == "escaped" ? Diag.PathEscape
                            : status == "missing" ? Diag.MissingResource : Diag.FragmentMissing;
                        diags.Add(new Diagnostic(code, "error",
                            $"导航链接失效: {n.RawHref}", navPath, n.RawHref));
                        broken.Add(new BrokenAnchor(navPath, n.RawHref!, n.Fragment, status, navPath));
                    }
                }
                return n with { Status = status, Children = n.Children.Select(Resolve).ToList() };
            }
            navTree.Add(Resolve(tree));
        }

        var fallbacks = new List<FallbackCandidate>();
        foreach (var item in pkg.Manifest.OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            if (item.Fallback == null)
                continue;
            var chain = new List<string> { item.Id };
            var visited = new HashSet<string>(StringComparer.Ordinal) { item.Id };
            var current = item;
            string status = "resolved";
            string? terminal = null;
            while (current.Fallback != null)
            {
                var nextId = current.Fallback;
                if (chain.Count > p.MaxFallbackDepth)
                {
                    status = "depth-exceeded";
                    diags.Add(new Diagnostic(Diag.FallbackDepthExceeded, "error",
                        $"fallback 链超过深度预算 {p.MaxFallbackDepth}: {string.Join(" -> ", chain)}",
                        selected, string.Join(",", chain)));
                    break;
                }
                chain.Add(nextId);
                if (!visited.Add(nextId))
                {
                    status = "cycle";
                    diags.Add(new Diagnostic(Diag.FallbackCycle, "error",
                        $"fallback 链存在循环（与缺失资源分开报告）: {string.Join(" -> ", chain)}",
                        selected, string.Join(",", chain)));
                    break;
                }
                if (!byId.TryGetValue(nextId, out var next))
                {
                    status = "missing";
                    diags.Add(new Diagnostic(Diag.MissingResource, "error",
                        $"fallback 目标不在 manifest 中: {nextId}（链: {string.Join(" -> ", chain)}）",
                        selected, nextId));
                    break;
                }
                current = next;
            }
            if (status == "resolved")
                terminal = current.Href;
            fallbacks.Add(new FallbackCandidate(item.Id, chain, status, terminal));
        }

        nodes = nodes.OrderBy(n => n.Id, StringComparer.Ordinal).ToList();
        return new ReviewRun(
            runId, caseId, digest, p.RuleVersion, parentRunId, kind,
            pc.Rootfiles, selected, reading, nodes, edges, navTree,
            broken.OrderBy(b => b.Source, StringComparer.Ordinal).ThenBy(b => b.RawHref, StringComparer.Ordinal).ToList(),
            fallbacks, diags, pkg.UnknownMetadata, affectedMaterials, nowUtc);
    }
}
