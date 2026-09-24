namespace EpubReview.Core;

/// <summary>三种必须区分开的失败结果，加上成功。</summary>
public enum FailureKind
{
    None = 0,
    /// <summary>资源预算耗尽（条目数 / 字节数 / fallback 深度 / 事件数）。</summary>
    BudgetExhausted = 1,
    /// <summary>格式损坏（ZIP 打不开、container.xml / OPF 不是合法 XML）。</summary>
    FormatCorrupt = 2,
    /// <summary>语义无解（格式合法但没有任何合法解释，例如所有 rootfile 都缺失）。</summary>
    SemanticUnresolvable = 3,
}

public enum DiagSeverity { Info, Warning, Error }

/// <summary>带位置的诊断。输入不完整时返回它，而不是用空值伪装成功。</summary>
public sealed record Diagnostic(
    string Code,
    DiagSeverity Severity,
    string Message,
    string? EntryPath,
    string? Fragment,
    IReadOnlyList<string> Subjects);

/// <summary>分析参数。每次修订形成新的参数版本。</summary>
public sealed record AnalysisParams(
    int MaxEntries = 512,
    long MaxTotalBytes = 32L * 1024 * 1024,
    int MaxFallbackDepth = 16,
    int MaxEvents = 4096,
    string? PreferredPackage = null,
    string ParamVersion = "1.0")
{
    public static AnalysisParams Default { get; } = new();
}

/// <summary>不可变材料层：ZIP 条目字节原样保留。</summary>
public sealed record StoredEntry(string Path, byte[] Bytes, string Sha256, string MediaType);

public sealed record ManifestItem(
    string Id, string Href, string MediaType,
    string? Fallback, string? Properties, string? MediaOverlay,
    string PackagePath, string NormalizedPath);

public sealed record SpineItemRef(string IdRef, bool Linear, int Position);

public sealed record ReadingOrderEntry(
    int Position, string ItemId, string ResourcePath, string MediaType, bool Linear, string StableId);

public sealed record GraphNode(string Id, string Label, string Kind, string? MediaType);
public sealed record GraphEdge(string From, string To, string Kind, string? Label);

public sealed record NavNode(string Id, string Label, string? Href, IReadOnlyList<NavNode> Children);

public sealed record BrokenAnchor(
    string SourcePath, string Href, string? Fragment, string Reason, string StableId);

public sealed record FallbackCandidate(
    string ForItemId, string CandidateItemId, string MediaType,
    string Status, int Depth, string StableId);

public sealed record AnalysisResult(
    string AnalysisId,
    string ProjectId,
    string InputDigest,
    string RuleVersion,
    string ParamVersion,
    IReadOnlyList<string> ParentAnalysisIds,
    FailureKind Failure,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<ReadingOrderEntry> ReadingOrder,
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphEdge> Edges,
    IReadOnlyList<NavNode> NavTree,
    IReadOnlyList<BrokenAnchor> BrokenAnchors,
    IReadOnlyList<FallbackCandidate> FallbackCandidates,
    IReadOnlyList<string> PreservedUnknownMetadata,
    IReadOnlyList<string> PackageCandidates);

/// <summary>机器可读取的判断事件。</summary>
public sealed record PendingEvent(string Kind, object Payload, IReadOnlyList<string> ParentEventIds);

public sealed record ReviewEvent(
    string Id, string ProjectId, long Seq, string Kind, string RuleVersion,
    string PayloadJson, IReadOnlyList<string> ParentEventIds, string CreatedUtc);

public sealed record Decision(
    string Id, string ProjectId, long Seq, string TargetId, string Action,
    string? ParamsJson, string? Rationale, string AnalysisId, string CreatedUtc);

public sealed record ProjectState(
    string ProjectId,
    AnalysisResult? CurrentAnalysis,
    IReadOnlyList<ReviewEvent> Events,
    IReadOnlyList<Decision> Decisions);
