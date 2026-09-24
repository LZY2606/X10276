using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace EpubReview.Core;

/// <summary>分析参数。每次参数修订提升版本号，计算结果记录该版本。</summary>
public sealed record AnalysisParameters(
    int Version,
    int MaxFallbackChainDepth = 32,
    int MaxGraphEdges = 4096,
    bool TreatDuplicateSpineAsError = false)
{
    public static readonly AnalysisParameters V1 = new(1);
}

public static class Analyzer
{
    public static AnalysisResult Analyze(ParsedContainer container, AnalysisParameters? parameters = null)
    {
        var p = parameters ?? AnalysisParameters.V1;
        var diagnostics = new List<Diagnostic>(container.Diagnostics);

        if (container.Status is AnalysisStatus.FormatCorrupt or AnalysisStatus.BudgetExhausted
            or AnalysisStatus.SemanticallyUnsolvable)
            return new AnalysisResult(container.Status, Array.Empty<ReadingOrderEntry>(),
                Array.Empty<ResourceEdge>(), container.NavTree, Array.Empty<BrokenAnchor>(),
                Array.Empty<FallbackNode>(), diagnostics, Digest(container), "params-v" + p.Version);

        var byName = container.Materials.ToDictionary(m => m.OriginalName, StringComparer.Ordinal);
        var readingOrder = new List<ReadingOrderEntry>();
        var edges = new List<ResourceEdge>();
        var fallbacks = new List<FallbackNode>();
        var brokenAnchors = new List<BrokenAnchor>();
        var status = AnalysisStatus.Ok;

        foreach (var pkg in container.Packages)
        {
            var manifestById = pkg.Manifest.ToDictionary(m => m.Id, StringComparer.Ordinal);

            // 阅读顺序：spine 只定义主顺序，不代替所有引用
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int index = 0;
            foreach (var si in pkg.Spine)
            {
                manifestById.TryGetValue(si.IdRef, out var item);
                string? resolved = item is null ? null : EpubParser.Resolve(pkg.BaseDir, item.Href, out _);
                readingOrder.Add(new ReadingOrderEntry(index++, si.IdRef, item?.Id, resolved, !seen.Add(si.IdRef)));
            }

            // 资源引用图：manifest 声明 + 内容文档内引用（href/src），独立于 spine
            foreach (var item in pkg.Manifest)
            {
                var from = EpubParser.Resolve(pkg.BaseDir, item.Href, out _);
                if (!byName.TryGetValue(from, out var mat)) continue;
                if (mat.MediaType is not ("application/xhtml+xml" or "application/smil+xml" or "image/svg+xml" or "text/css")) continue;
                foreach (var (raw, pos) in ScanReferences(mat))
                {
                    var fragIdx = raw.IndexOf('#');
                    var pathPart = fragIdx >= 0 ? raw[..fragIdx] : raw;
                    var frag = fragIdx >= 0 ? raw[(fragIdx + 1)..] : null;
                    if (pathPart.Length == 0) pathPart = from; // 纯 fragment 引用自身
                    else pathPart = EpubParser.Resolve(EpubParser.BaseDir(from), pathPart, out var esc);
                    if (edges.Count >= p.MaxGraphEdges)
                    {
                        status = AnalysisStatus.BudgetExhausted;
                        diagnostics.Add(new Diagnostic("BUDGET_EDGES", DiagnosticSeverity.Error,
                            $"资源引用边数超出预算 {p.MaxGraphEdges}（单位：edge）", pos, new[] { from }));
                        break;
                    }
                    edges.Add(new ResourceEdge(from, pathPart, frag, "content-ref", pos));
                }
                if (status == AnalysisStatus.BudgetExhausted) break;
            }

            // fallback 链：循环与缺失分开报告
            foreach (var item in pkg.Manifest)
            {
                if (item.Fallback is null) continue;
                var chain = new List<string> { item.Id };
                var visited = new HashSet<string>(StringComparer.Ordinal) { item.Id };
                var current = item;
                string state = "resolved";
                while (current.Fallback is not null)
                {
                    if (chain.Count >= p.MaxFallbackChainDepth)
                    {
                        state = "budget-exhausted";
                        diagnostics.Add(new Diagnostic("BUDGET_FALLBACK", DiagnosticSeverity.Error,
                            $"fallback 链深度超出预算 {p.MaxFallbackChainDepth}（单位：hop）",
                            current.Position, chain.ToArray()));
                        status = AnalysisStatus.BudgetExhausted;
                        break;
                    }
                    if (!visited.Add(current.Fallback))
                    {
                        state = "cycle";
                        chain.Add(current.Fallback);
                        diagnostics.Add(new Diagnostic("FALLBACK_CYCLE", DiagnosticSeverity.Error,
                            $"fallback 链循环：{string.Join(" -> ", chain)}（与缺失资源分开报告）",
                            current.Position, chain.ToArray()));
                        break;
                    }
                    if (!manifestById.TryGetValue(current.Fallback, out var next))
                    {
                        state = "missing";
                        chain.Add(current.Fallback);
                        diagnostics.Add(new Diagnostic("FALLBACK_MISSING", DiagnosticSeverity.Error,
                            $"fallback 目标 '{current.Fallback}' 不在 manifest 中（缺失资源，与循环分开报告）",
                            current.Position, new[] { item.Id, current.Fallback }));
                        break;
                    }
                    chain.Add(next.Id);
                    current = next;
                }
                fallbacks.Add(new FallbackNode(item.Id, item.Href, item.MediaType, state, chain));
            }

            // 断链锚点：fragment 不存在时保留全部合法候选
            foreach (var edge in edges)
            {
                if (edge.Fragment is null) continue;
                if (!byName.TryGetValue(edge.ToEntry, out var target))
                {
                    brokenAnchors.Add(new BrokenAnchor(edge.FromEntry, edge.ToEntry, edge.Fragment,
                        pkg.Manifest.Select(m => m.Id).ToArray()));
                    diagnostics.Add(new Diagnostic("ANCHOR_TARGET_MISSING", DiagnosticSeverity.Error,
                        $"锚点目标资源不存在：{edge.ToEntry}#{edge.Fragment}",
                        edge.Position, new[] { edge.FromEntry, edge.ToEntry }));
                    continue;
                }
                var ids = ScanIds(target);
                if (!ids.Contains(edge.Fragment))
                {
                    brokenAnchors.Add(new BrokenAnchor(edge.FromEntry, edge.ToEntry, edge.Fragment, ids));
                    diagnostics.Add(new Diagnostic("FRAGMENT_MISSING", DiagnosticSeverity.Error,
                        $"fragment '#{edge.Fragment}' 在 {edge.ToEntry} 中不存在；合法候选：{(ids.Count == 0 ? "(无)" : string.Join(", ", ids))}",
                        edge.Position, new[] { edge.FromEntry, edge.ToEntry, edge.Fragment }));
                }
            }

            // media-overlay 断链
            foreach (var ov in container.OverlayRefs)
            {
                var ovResolved = EpubParser.Resolve(EpubParser.BaseDir(ov.SmilPath), ov.TextSrc, out var ovEsc);
                if (ovEsc)
                {
                    diagnostics.Add(new Diagnostic("PATH_ESCAPE", DiagnosticSeverity.Error,
                        $"media-overlay 引用越出容器：{ov.TextSrc}", ov.Position, new[] { ov.TextSrc }));
                    continue;
                }
                if (!byName.ContainsKey(ovResolved))
                    diagnostics.Add(new Diagnostic("OVERLAY_BROKEN", DiagnosticSeverity.Error,
                        $"media-overlay 断链：{ov.SmilPath} 引用不存在的 {ovResolved}",
                        ov.Position, new[] { ov.SmilPath, ovResolved }));
                else if (ov.Fragment is not null && !ScanIds(byName[ovResolved]).Contains(ov.Fragment))
                    diagnostics.Add(new Diagnostic("OVERLAY_BROKEN", DiagnosticSeverity.Error,
                        $"media-overlay 断链：{ovResolved} 中不存在 fragment '#{ov.Fragment}'",
                        ov.Position, new[] { ov.SmilPath, ovResolved, ov.Fragment }));
            }
        }

        return new AnalysisResult(status, readingOrder, edges, container.NavTree,
            brokenAnchors, fallbacks, diagnostics, Digest(container), "params-v" + p.Version);
    }

    private static readonly Regex RefRegex = new(
        "(?:href|src)\\s*=\\s*\"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static IEnumerable<(string Raw, SourcePosition Pos)> ScanReferences(Material mat)
    {
        var text = Encoding.UTF8.GetString(mat.Bytes);
        var line = 1; var lastIndex = 0;
        foreach (Match m in RefRegex.Matches(text))
        {
            var raw = m.Groups[1].Value;
            if (raw.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
                raw.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ||
                raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            for (var i = lastIndex; i < m.Index; i++) if (text[i] == '\n') line++;
            lastIndex = m.Index;
            yield return (raw, new SourcePosition(mat.OriginalName, line, 1));
        }
    }

    private static readonly Regex IdRegex = new(
        "\\bid\\s*=\\s*\"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static IReadOnlyList<string> ScanIds(Material mat)
    {
        var text = Encoding.UTF8.GetString(mat.Bytes);
        var ids = new List<string>();
        foreach (Match m in IdRegex.Matches(text)) ids.Add(m.Groups[1].Value);
        return ids;
    }

    public static string Digest(ParsedContainer c) =>
        "sha256:" + EpubParser.Sha256Hex(Encoding.UTF8.GetBytes(
            string.Join("\n", c.Materials.OrderBy(m => m.OriginalName).Select(m => m.Id))))[..23];
}
