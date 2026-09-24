using System.Net;
using System.Text;
using EpubReview.Domain;

namespace EpubReview.Web.Services;

public static class SvgView
{
    public static string ResourceGraph(AnalysisResult r)
    {
        var nodes = r.Resources.ToList();
        var height = Math.Max(360, 100 + nodes.Count * 34);
        var sb = new StringBuilder($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 1120 {height}\" role=\"img\"><defs><marker id=\"arrow\" markerWidth=\"10\" markerHeight=\"10\" refX=\"8\" refY=\"3\" orient=\"auto\"><path d=\"M0,0 L0,6 L9,3 z\" fill=\"#5b6b7a\"/></marker></defs><rect width=\"1120\" height=\"{height}\" fill=\"#0f172a\"/><text x=\"24\" y=\"34\" fill=\"#e2e8f0\" font-size=\"18\" font-family=\"sans-serif\">资源依赖图 · 坐标 {E(r.CoordinateSpace)}</text><text x=\"24\" y=\"58\" fill=\"#94a3b8\" font-size=\"12\" font-family=\"sans-serif\">单位：{E(r.ByteUnit)}；边按稳定身份和关系绘制；{E(r.DependencyVersion)}</text>");
        var positions = new Dictionary<string, (int X, int Y)>();
        for (var i = 0; i < nodes.Count; i++)
        {
            var y = 92 + i * 34;
            positions[nodes[i].Id] = (42, y);
            var color = r.References.Any(e => e.SourceId == nodes[i].Id && e.Status != LinkStatus.Ok) ? "#f97316" : "#38bdf8";
            sb.Append($"<rect x=\"24\" y=\"{y - 18}\" width=\"360\" height=\"26\" rx=\"6\" fill=\"#111827\" stroke=\"{color}\"/><text x=\"36\" y=\"{y}\" fill=\"#e5e7eb\" font-size=\"11\" font-family=\"monospace\"><title>{E(nodes[i].Id)}</title>{E(Short(nodes[i].Id, 46))}</text>");
        }
        var edgeIndex = 0;
        foreach (var edge in r.References.Where(e => positions.ContainsKey(e.SourceId)).DistinctBy(e => e.Id))
        {
            var source = positions[edge.SourceId];
            var targetY = 92 + (edgeIndex % Math.Max(1, nodes.Count)) * 34;
            var targetX = 740;
            var color = edge.Status == LinkStatus.Ok ? "#94a3b8" : edge.Status == LinkStatus.PathEscapesContainer ? "#ef4444" : "#f59e0b";
            sb.Append($"<path d=\"M384,{source.Y} C520,{source.Y} 600,{targetY} {targetX - 8},{targetY}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"1.5\" marker-end=\"url(#arrow)\"><title>{E(edge.Id)} · {edge.Status} · {E(edge.RawTarget)}</title></path>");
            sb.Append($"<text x=\"{targetX}\" y=\"{targetY + 4}\" fill=\"#f8fafc\" font-size=\"11\" font-family=\"monospace\">{E(edge.Relation)}: {E(Short(edge.Status + " " + edge.RawTarget, 44))}</text>");
            edgeIndex++;
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    public static string ReadingOrder(AnalysisResult r)
    {
        var height = 260 + r.Spine.Count * 58;
        var sb = new StringBuilder($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 1120 {height}\"><rect width=\"1120\" height=\"{height}\" fill=\"#f8fafc\"/><text x=\"28\" y=\"38\" font-size=\"20\" font-family=\"sans-serif\" fill=\"#0f172a\">阅读顺序 · spine 只定义主线性顺序</text><text x=\"28\" y=\"62\" font-size=\"12\" font-family=\"sans-serif\" fill=\"#475569\">时间基准：{E(r.TimeUnit)}；顺序单位：线性 ordinal；依赖 {E(r.DependencyVersion)}</text>");
        for (var i = 0; i < r.Spine.Count; i++)
        {
            var item = r.Spine[i];
            var x = 80 + (i % 3) * 320;
            var y = 104 + (i / 3) * 96;
            var color = item.IsDuplicate ? "#f59e0b" : "#0ea5e9";
            if (i > 0) sb.Append($"<line x1=\"{x - 320 + (i % 3 == 0 ? 640 : 0)}\" y1=\"{y - (i % 3 == 0 ? 96 : 0)}\" x2=\"{x}\" y2=\"{y - 18}\" stroke=\"#64748b\" stroke-width=\"2\" marker-end=\"url(#arrow2)\"/>");
            sb.Append($"<rect x=\"{x}\" y=\"{y - 24}\" width=\"250\" height=\"58\" rx=\"10\" fill=\"white\" stroke=\"{color}\" stroke-width=\"2\"/><text x=\"{x + 12}\" y=\"{y - 2}\" font-size=\"13\" font-family=\"sans-serif\" fill=\"#0f172a\">#{item.Order} {E(item.IdRef)}</text><text x=\"{x + 12}\" y=\"{y + 18}\" font-size=\"10\" font-family=\"monospace\" fill=\"#64748b\"><title>{E(item.Id)}</title>{E(Short(item.Id, 34))}</text>");
        }
        sb.Append("<defs><marker id=\"arrow2\" markerWidth=\"10\" markerHeight=\"10\" refX=\"8\" refY=\"3\" orient=\"auto\"><path d=\"M0,0 L0,6 L9,3 z\" fill=\"#64748b\"/></marker></defs></svg>");
        return sb.ToString();
    }

    private static string Short(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
    private static string E(string? value) => WebUtility.HtmlEncode(value ?? "");
}
