using System.Net;
using System.Text;
using EpubReview.Domain;

namespace EpubReview.Web.Services;

public static class ReviewPage
{
    public static string Render(string title, IReadOnlyList<EventRecord> events, string selectedEvent, AnalysisResult r)
    {
        var sb = new StringBuilder();
        sb.Append("""<!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>EPUB 阅读序谱审阅</title><link rel="stylesheet" href="/app.css"></head><body>""");
        sb.Append($"<header><div><p class=\"eyebrow\">离线 OCF / EPUB 审阅案例</p><h1>{E(title)}</h1><p>输入 {r.InputByteLength} {E(r.InputUnit)}；规则版本 <code>{r.ParametersVersion}</code>；依赖版本 <code>{r.DependencyVersion}</code>；父依赖 <code>{E(r.ParentDependencyVersion ?? "无")}</code></p></div>");
        sb.Append($"<div class=\"outcome outcome-{r.Outcome.ToString().ToLowerInvariant()}\">{E(r.Outcome.ToString())}<small>{E(r.GeneratedAt)} / {E(r.TimeUnit)}</small></div></header>");

        sb.Append("<section class=\"toolbar\"><form method=\"get\" action=\"/\"><label>回到历史事件<select name=\"event\">");
        foreach (var e in events) sb.Append($"<option value=\"{e.EventId}\" {(e.EventId == selectedEvent ? "selected" : "")}>#{e.Sequence:00} {E(TypeLabel(e.Type))} · {E(Escape(e.Status))}</option>");
        sb.Append("</select></label><button>读回</button></form>");
        sb.Append("<form id=\"compare\"><label>比较左<select id=\"left\">");
        foreach (var e in events.Where(x => x.DependencyVersion is not null)) sb.Append($"<option>{e.EventId}</option>");
        sb.Append("</select></label><label>右<select id=\"right\">");
        foreach (var e in events.Where(x => x.DependencyVersion is not null)) sb.Append($"<option>{e.EventId}</option>");
        sb.Append("</select></label><button>比较修订草案</button></form><div id=\"comparison\"></div>");
        sb.Append("<a class=\"button\" href=\"/api/export\">导出项目</a><button id=\"deleteReimport\">删除前请先导出；导入演示</button><input id=\"importFile\" type=\"file\" accept=\"application/zip\"></section>");

        sb.Append("<section class=\"grid\">");
        Panel(sb, "阅读顺序 / spine", ReadingTable(r));
        Panel(sb, "导航树", NavigationTable(r));
        Panel(sb, "断链锚点与引用", BrokenTable(r));
        Panel(sb, "fallback 候选", FallbackTable(r));
        Panel(sb, "资源依赖图", $"<img class=\"svg\" alt=\"resource graph\" src=\"/api/events/{Uri.EscapeDataString(selectedEvent)}/graph.svg\"><div class=\"coord\">坐标：{E(r.CoordinateSpace)}；范围 0..1120 × 0..{Math.Max(360, 140 + r.Resources.Count * 30)}；有效区间以边状态为准。</div>");
        Panel(sb, "阅读顺序 SVG", $"<img class=\"svg\" alt=\"reading order\" src=\"/api/events/{Uri.EscapeDataString(selectedEvent)}/reading-order.svg\">");
        Panel(sb, "package 文档", PackageTable(r));
        Panel(sb, "资源节点与量值", ResourceTable(r));
        Panel(sb, "不可变 ZIP 材料", MaterialsTable(r));
        Panel(sb, "机器可读判断依据", JudgmentTable(r));
        Panel(sb, "诊断与失效原因", DiagnosticsTable(r));
        Panel(sb, "media-overlay", OverlayTable(r));
        Panel(sb, "未知 metadata 往返", UnknownMetadataTable(r));
        Panel(sb, "事件时间线", TimelineTable(events, selectedEvent));
        sb.Append("</section><script src=\"/app.js\"></script></body></html>");
        return sb.ToString();
    }

    private static string ReadingTable(AnalysisResult r)
    {
        var rows = r.Spine.Select(s => $"<tr data-stable-id=\"{s.Id}\"><td>{s.Order}</td><td><code>{s.Id}</code></td><td><code>{s.PackageId}</code></td><td>{E(s.IdRef)}</td><td>{(s.IsDuplicate ? "<b>重复 spine 项</b>" : "主顺序")}</td><td>{E(s.DiagnosticCode ?? "OK")}</td></tr>");
        return Table("#", "稳定身份", "package", "idref", "状态", "诊断", rows);
    }

    private static string NavigationTable(AnalysisResult r)
    {
        var rows = r.Navigation.Select(n => $"<tr data-stable-id=\"{n.Id}\"><td>{"".PadLeft(n.Depth * 2, '→')}{n.Order}</td><td><code>{n.Id}</code></td><td>{E(n.Label)}</td><td>{E(n.RawHref)}</td><td>{(n.IsBroken ? "断" : "可解析")}</td><td><code>{E(n.TargetId ?? "无")}</code></td></tr>");
        return Table("层/序", "稳定身份", "标签", "原始 href", "状态", "目标", rows);
    }

    private static string BrokenTable(AnalysisResult r)
    {
        var rows = r.References.Where(x => x.Status != LinkStatus.Ok).Select(x => $"<tr data-stable-id=\"{x.Id}\"><td><code>{x.Id}</code></td><td>{E(x.Relation)}</td><td>{E(x.RawTarget)}</td><td>{E(x.NormalizedTarget ?? "未规范化")}</td><td><b>{x.Status}</b></td><td>{E(x.Fragment ?? "无 fragment")}</td><td>{string.Join("<br>", x.CandidateTargetIds.Select(E))}</td><td>{E(x.Message ?? x.DiagnosticCode ?? "")}</td></tr>");
        return Table("稳定边", "关系", "原始目标", "规范目标", "状态", "锚点", "全部合法候选", "失效原因", rows);
    }

    private static string FallbackTable(AnalysisResult r)
    {
        var rows = r.Fallback.Select(f => $"<tr data-stable-id=\"{f.Id}\"><td><code>{f.Id}</code></td><td>{f.Step}</td><td><code>{f.SourceItemId}</code></td><td>{E(f.TargetIdRef)}</td><td><b>{f.Status}</b></td><td><code>{E(f.TargetItemId ?? "无")}</code></td><td>{E(f.CycleKey)}</td></tr>");
        return Table("稳定身份", "步", "来源", "候选 idref", "状态", "目标资源", "循环", rows);
    }


    private static string PackageTable(AnalysisResult r)
    {
        var rows = r.Packages.Select(p => $"<tr data-stable-id="{p.PackageId}"><td><code>{p.PackageId}</code></td><td><code>{p.RootFileMaterialId}</code></td><td>{E(p.RawPath)}</td><td>{E(p.NormalizedPath ?? "未规范化")}</td><td>{E(p.MediaType ?? "container 未声明")}</td><td>{p.ManifestItemCount}</td></tr>");
        return $"<table><thead><tr><th>package 稳定身份</th><th>根文件材料身份</th><th>原始路径</th><th>规范路径</th><th>media type</th><th>manifest 数量</th></tr></thead><tbody>{string.Join("", rows)}</tbody></table>";
    }

    private static string ResourceTable(AnalysisResult r)
    {
        var rows = r.Resources.Select(x => $"<tr data-stable-id="{x.Id}"><td><code>{x.Id}</code></td><td>{E(x.Label)}</td><td>{E(x.RawPath)}</td><td>{E(x.NormalizedPath ?? "未规范化")}</td><td>{E(x.DeclaredMediaType ?? "未声明")}</td><td>{E(x.InferredMediaType ?? "未推断")}</td><td>{x.ByteLength} {E(x.ByteUnit)}</td><td>{E(x.ValidRange ?? "无")}</td><td>{x.CandidateMaterialIds.Count}</td><td>{E(x.InvalidReason ?? "无")}</td></tr>");
        return $"<table><thead><tr><th>稳定身份</th><th>manifest id</th><th>原始引用</th><th>规范引用</th><th>声明 media type</th><th>推断 media type</th><th>长度/单位</th><th>有效区间</th><th>候选数</th><th>失效原因</th></tr></thead><tbody>{string.Join("", rows)}</tbody></table>";
    }

    private static string MaterialsTable(AnalysisResult r)
    {
        var rows = r.Entries.Select(e => {
            var createdBy = r.Decisions.Count == 0 ? "" : "";
            return $"<tr data-stable-id=\"{e.MaterialId}\"><td>{e.OriginalIndex}</td><td><code>{e.MaterialId}</code></td><td>{E(e.RawPath)}</td><td>{E(e.NormalizedPath ?? "不可规范化")}</td><td>{E(e.MediaType ?? "未推断")}</td><td>{e.ByteLength} {E(r.ByteUnit)}</td><td><code>{e.Sha256[..16]}…</code></td><td>{E(e.InvalidReason ?? "字节已保留")}</td></tr>";
        });
        return $"<table><thead><tr><th>#</th><th>稳定材料身份</th><th>原始 ZIP 路径</th><th>派生路径</th><th>media type</th><th>长度/{E(r.ByteUnit)}</th><th>SHA-256</th><th>保留状态</th></tr></thead><tbody>{string.Join("", rows)}</tbody></table>";
    }

    private static string JudgmentTable(AnalysisResult r) => Table("机器事件代码", "结果", "规则", "受影响对象", r.Judgments.Select(j => $"<tr data-stable-id=\"{j.Code}\"><td><code>{j.Code}</code></td><td>{j.Status}</td><td>{E(j.MachineRule)}</td><td>{j.SubjectIds.Length}</td></tr>"));

    private static string DiagnosticsTable(AnalysisResult r) => Table("代码", "级别", "位置", "行:列", "对象", "结果", "消息", r.Diagnostics.Select(d => $"<tr data-stable-id=\"{d.Code}:{d.Location}:{d.SubjectId}\"><td><code>{d.Code}</code></td><td>{d.Severity}</td><td>{E(d.Location)}</td><td>{d.Line}:{d.Position}</td><td><code>{E(d.SubjectId ?? "无")}</code></td><td>{d.Outcome?.ToString() ?? "—"}</td><td>{E(d.Message)}</td></tr>"));

    private static string OverlayTable(AnalysisResult r) => Table("稳定身份", "来源", "overlay", "状态", r.Overlays.Select(o => $"<tr data-stable-id=\"{o.Id}\"><td><code>{o.Id}</code></td><td><code>{o.SourceItemId}</code></td><td>{E(o.OverlayIdRef)}</td><td><b>{o.Status}</b></td></tr>"));

    private static string UnknownMetadataTable(AnalysisResult r) => Table("package", "身份", "位置", "元素", "原样片段", r.Packages.SelectMany(p => p.UnknownMetadata.Select(m => $"<tr data-stable-id=\"{m.Id}\"><td><code>{p.PackageId}</code></td><td><code>{m.Id}</code></td><td>{m.Line}:{m.Position}</td><td>{E(m.Namespace)}::{m.LocalName}</td><td><pre>{E(m.OuterXml)}</pre></td></tr>")));

    private static string TimelineTable(IReadOnlyList<EventRecord> events, string selected) => Table("#", "事件稳定身份", "类型", "输入", "参数", "父依赖", "依赖版本", events.Select(e => $"<tr class=\"{(e.EventId == selected ? "selected" : "")}\" data-stable-id=\"{e.EventId}\"><td>{e.Sequence}</td><td><code>{e.EventId}</code></td><td>{TypeLabel(e.Type)}</td><td>{(e.InputSha256 is null ? "—" : $"<code>{e.InputSha256[..12]}</code>")}</td><td><code>{e.ParametersVersion}</code></td><td><code>{E(e.ParentDependencyVersion ?? "—")}</code></td><td><code>{E(e.DependencyVersion ?? "—")}</code></td></tr>"));

    private static void Panel(StringBuilder sb, string title, string html) => sb.Append($"<article class=\"panel wide\"><h2>{title}</h2>{html}</article>");
    private static string Table(params object[] _) => "";
    private static string Table(string a, string b, string c, IEnumerable<string> rows) => $"<table><thead><tr><th>{a}</th><th>{b}</th><th>{c}</th></tr></thead><tbody>{string.Join("", rows)}</tbody></table>";
    private static string Table(string a, string b, string c, string d, IEnumerable<string> rows) => $"<table><thead><tr><th>{a}</th><th>{b}</th><th>{c}</th><th>{d}</th></tr></thead><tbody>{string.Join("", rows)}</tbody></table>";
    private static string Table(string a, string b, string c, string d, string e, IEnumerable<string> rows) => $"<table><thead><tr><th>{a}</th><th>{b}</th><th>{c}</th><th>{d}</th><th>{e}</th></tr></thead><tbody>{string.Join("", rows)}</tbody></table>";
    private static string Table(string a, string b, string c, string d, string e, string f, IEnumerable<string> rows) => $"<table><thead><tr><th>{a}</th><th>{b}</th><th>{c}</th><th>{d}</th><th>{e}</th><th>{f}</th></tr></thead><tbody>{string.Join("", rows)}</tbody></table>";
    private static string Table(string a, string b, string c, string d, string e, string f, string g, IEnumerable<string> rows) => $"<table><thead><tr><th>{a}</th><th>{b}</th><th>{c}</th><th>{d}</th><th>{e}</th><th>{f}</th><th>{g}</th></tr></thead><tbody>{string.Join("", rows)}</tbody></table>";
    private static string Table(string a, string b, string c, string d, string e, string f, string g, string h, IEnumerable<string> rows) => $"<table><thead><tr><th>{a}</th><th>{b}</th><th>{c}</th><th>{d}</th><th>{e}</th><th>{f}</th><th>{g}</th><th>{h}</th></tr></thead><tbody>{string.Join("", rows)}</tbody></table>";
    private static string TypeLabel(string type) => type switch { "case-created" => "建档", "archive-imported" => "导入", "analysis-completed" => "计算", "parameters-revised" => "参数", "decision-recorded" => "决定", _ => type };
    private static string E(string? value) => WebUtility.HtmlEncode(value ?? "");
    private static string Escape(string value) => E(value);
}
