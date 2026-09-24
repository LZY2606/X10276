namespace EpubReview.Domain;

/// <summary>机器可读取的诊断代码。代码稳定，作为事件与导出的判断依据。</summary>
public static class Diag
{
    public const string MultiPackage = "EPUB-MULTI-PACKAGE";
    public const string FallbackCycle = "EPUB-FALLBACK-CYCLE";
    public const string FallbackMissing = "EPUB-FALLBACK-MISSING";
    public const string MissingResource = "EPUB-MISSING-RESOURCE";
    public const string FragmentNotFound = "EPUB-FRAGMENT-NOT-FOUND";
    public const string PathOutOfContainer = "EPUB-PATH-OUT-OF-CONTAINER";
    public const string SpineDuplicate = "EPUB-SPINE-DUPLICATE";
    public const string OverlayBrokenLink = "EPUB-OVERLAY-BROKEN-LINK";
    public const string UnknownMetadata = "EPUB-UNKNOWN-METADATA";
    public const string FormatCorrupt = "EPUB-FORMAT-CORRUPT";
    public const string BudgetExhausted = "EPUB-BUDGET-EXHAUSTED";
    public const string SemanticsUnsolvable = "EPUB-SEMANTICS-UNSOLVABLE";
    public const string ContainerMissing = "EPUB-CONTAINER-MISSING";
}

/// <summary>带位置（条目路径 + 行列）的诊断，不允许用空值伪装成功。</summary>
public sealed record Diagnostic(
    string Code,
    string Severity,          // "error" | "warning" | "info"
    string Message,
    string? EntryPath,        // 容器内条目路径（坐标基准：容器根 /）
    int? Line,
    int? Column,
    string? SubjectId);       // 相关 manifest item / spine idref 等稳定身份

/// <summary>不可变材料：ZIP 条目字节原样保留。</summary>
public sealed record ZipEntryMaterial(string Path, long Length, string Sha256, byte[] Bytes);

public sealed record ManifestItem(
    string Id,
    string Href,              // 原始表示，导入时保留
    string? ResolvedPath,     // 派生层规范化结果；越界时为 null 并附诊断
    string? Fragment,
    string MediaType,
    string? Fallback,
    string? MediaOverlay,
    string? Properties);

public sealed record SpineItemRef(string IdRef, bool Linear, int Ordinal);

public sealed record PackageDoc(
    string Path,
    string RawXml,                       // package 文档原文保留
    string? UniqueIdentifier,
    IReadOnlyList<ManifestItem> Manifest,
    IReadOnlyList<SpineItemRef> Spine,
    IReadOnlyList<string> UnknownMetadata);  // 未识别 metadata 的原始 XML，往返保留

public sealed record EpubMaterials(
    string MaterialsId,                  // 全部条目内容哈希的摘要 → 稳定身份
    IReadOnlyList<ZipEntryMaterial> Entries,
    string ContainerXml,                 // container.xml 原文保留
    IReadOnlyList<PackageDoc> Packages,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    public ZipEntryMaterial? Find(string path) =>
        Entries.FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.Ordinal));
}

/// <summary>分析参数：资源预算耗尽、格式损坏、语义无解必须是三种不同结果。</summary>
public sealed record AnalysisParams(int MaxEntries, int MaxBytes, int MaxFallbackSteps)
{
    public static readonly AnalysisParams Default = new(MaxEntries: 512, MaxBytes: 64 * 1024 * 1024, MaxFallbackSteps: 32);
    public string ParamsHash => Hashing.Sha256Hex($"{MaxEntries}|{MaxBytes}|{MaxFallbackSteps}");
}

public enum AnalysisOutcome { Ok, BudgetExhausted, FormatCorrupt, SemanticsUnsolvable }

public sealed record ReadingOrderItem(
    int Ordinal,               // 单位：spine 序号（从 0 起）
    string IdRef,
    string? ItemId,            // 解析到的 manifest item 稳定身份
    string? Href,
    string? ResolvedPath,
    bool Linear,
    IReadOnlyList<string> Issues);

public sealed record GraphNode(string Id, string Path, string MediaType, string Kind);
public sealed record GraphEdge(string FromId, string ToId, string Kind); // spine|fallback|overlay|nav|content

public sealed record NavNode(
    string Label,
    string? Href,
    string? Fragment,
    IReadOnlyList<NavNode> Children,
    IReadOnlyList<string> Issues);

public sealed record BrokenAnchor(
    string SourcePath, string TargetPath, string Fragment, string Reason, string DiagnosticCode);

public sealed record FallbackChain(
    string StartItemId,
    IReadOnlyList<string> Steps,        // 依次访问到的 manifest item id
    string Status,                      // "ok" | "cycle" | "missing"
    IReadOnlyList<string> CycleMembers, // 循环与缺失分开报告
    string? MissingItemId);

/// <summary>无法唯一决定时保留的全部合法候选。</summary>
public sealed record CandidateSet(string SubjectId, string Reason, IReadOnlyList<string> Candidates);

public sealed record AnalysisResult(
    string AnalysisId,          // (materials, rules, params) 摘要 → 等价输入得到稳定身份与顺序
    string MaterialsId,
    string RulesVersion,
    string ParamsHash,
    AnalysisOutcome Outcome,
    string? PrimaryPackagePath,
    IReadOnlyList<ReadingOrderItem> ReadingOrder,
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphEdge> Edges,
    IReadOnlyList<NavNode> NavTree,
    IReadOnlyList<BrokenAnchor> BrokenAnchors,
    IReadOnlyList<FallbackChain> FallbackChains,
    IReadOnlyList<CandidateSet> CandidateSets,
    IReadOnlyList<Diagnostic> Diagnostics);
