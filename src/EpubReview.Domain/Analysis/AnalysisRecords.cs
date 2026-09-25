using System;
using System.Collections.Generic;

namespace EpubReview.Domain.Analysis;

/// <summary>一次分析的完整派生结果。记录输入摘要、参数版本与父级依赖版本，可按历史事件重放。</summary>
public sealed record AnalysisSnapshot
{
    public required string AnalysisId { get; init; }
    public required string BundleId { get; init; }
    public required string InputDigest { get; init; }
    public required string ParametersVersion { get; init; }
    public required AnalysisParameters Parameters { get; init; }
    /// <summary>父级依赖：参数版本 + 每项材料的字节摘要版本。</summary>
    public required IReadOnlyList<DependencyVersion> ParentDependencies { get; init; }
    public required AnalysisOutcome Outcome { get; init; }
    public required IReadOnlyList<ReadingOrderEntry> ReadingOrder { get; init; }
    public required IReadOnlyList<ResourceNode> ResourceNodes { get; init; }
    public required IReadOnlyList<ResourceEdge> ResourceEdges { get; init; }
    public required IReadOnlyList<NavigationNode> NavigationTree { get; init; }
    public required IReadOnlyList<BrokenAnchor> BrokenAnchors { get; init; }
    public required IReadOnlyList<FallbackCandidate> FallbackCandidates { get; init; }
    public required IReadOnlyList<Finding> Findings { get; init; }
    public required DateTimeOffset ComputedAt { get; init; }
}

/// <summary>三类结果严格区分：语义可解 / 资源预算耗尽 / 格式损坏 / 语义无解。</summary>
public enum AnalysisOutcome { Resolved, BudgetExhausted, Corrupt, SemanticallyUnsolvable }

public sealed record AnalysisParameters
{
    /// <summary>资源预算（字节）；引用资源字节总量超出时返回 BudgetExhausted。</summary>
    public required long ResourceBudgetBytes { get; init; }
    /// <summary>是否把 non-linear spine 条目计入主阅读顺序。</summary>
    public required bool IncludeNonLinear { get; init; }
    /// <summary>fragment 锚点校验严格度。</summary>
    public required FragmentPolicy FragmentPolicy { get; init; }
    public required string Version { get; init; }
}

public enum FragmentPolicy { RequireTarget, RecordOnly }

public sealed record DependencyVersion
{
    /// <summary>依赖角色，例如 entry:OEBPS/content.opf、parameter-set、decision:D01。</summary>
    public required string Role { get; init; }
    public required string EntryName { get; init; }
    public required string Sha256 { get; init; }
    public required long Length { get; init; }
}

/// <summary>阅读顺序条目：稳定身份来自 (package, manifest-id, ordinal)。spine 只定义主顺序。</summary>
public sealed record ReadingOrderEntry
{
    public required string StableId { get; init; }
    public required int Ordinal { get; init; }
    public required string PackageEntryName { get; init; }
    public required string ManifestId { get; init; }
    public required string ResolvedEntryName { get; init; }
    public required string MediaType { get; init; }
    public required bool Linear { get; init; }
    public required string Source { get; init; }
}

public sealed record ResourceNode
{
    public required string StableId { get; init; }
    public required string EntryName { get; init; }
    public required string MediaType { get; init; }
    public required bool DeclaredInManifest { get; init; }
    public required bool ExistsInZip { get; init; }
    /// <summary>尺寸（单位：byte）。所有领域量携带单位。</summary>
    public required long SizeBytes { get; init; }
    public required IReadOnlyList<string> PackageRefs { get; init; }
}

public sealed record ResourceEdge
{
    public required string StableId { get; init; }
    public required string FromNodeId { get; init; }
    public required string ToNodeId { get; init; }
    public required string Relation { get; init; }
    /// <summary>引用来源：spine / manifest-href / media-overlay / nav / content-link 等；spine 不是唯一引用来源。</summary>
    public required string Source { get; init; }
    public required string FragmentRaw { get; init; }
}

public sealed record NavigationNode
{
    public required string StableId { get; init; }
    public required int Depth { get; init; }
    public required int SiblingOrdinal { get; init; }
    public required string LabelRaw { get; init; }
    public required string HrefRaw { get; init; }
    public required string ResolvedEntryName { get; init; }
    public required string FragmentRaw { get; init; }
    public required IReadOnlyList<NavigationNode> Children { get; init; }
}

/// <summary>断链锚点：fragment 不存在等，携带坐标基准（条目 + 行内位置 + 元素路径）。</summary>
public sealed record BrokenAnchor
{
    public required string StableId { get; init; }
    public required string PackageEntryName { get; init; }
    public required string FromEntryName { get; init; }
    public required string HrefRaw { get; init; }
    public required string TargetEntryName { get; init; }
    public required string FragmentRaw { get; init; }
    public required string ReasonCode { get; init; }
    public required string Reason { get; init; }
    public required int? Line { get; init; }
    public required int? Column { get; init; }
    public required string ElementPath { get; init; }
}

/// <summary>fallback 候选：无法唯一决定时保留全部合法候选；循环单独报告、与缺失资源分开。</summary>
public sealed record FallbackCandidate
{
    public required string StableId { get; init; }
    public required string PackageEntryName { get; init; }
    public required string ManifestId { get; init; }
    public required IReadOnlyList<FallbackChainLink> Chain { get; init; }
    public required bool CycleDetected { get; init; }
    public required IReadOnlyList<string> CycleIds { get; init; }
    public required bool TargetMissing { get; init; }
    public required IReadOnlyList<string> MissingIds { get; init; }
}

public sealed record FallbackChainLink
{
    public required int Step { get; init; }
    public required string ManifestId { get; init; }
    public required string MediaType { get; init; }
    public required bool EntryExists { get; init; }
}

/// <summary>结构化判定事件依据：机器可读，而不是只写日志。</summary>
public sealed record Finding
{
    public required string StableId { get; init; }
    public required string Code { get; init; }
    public required FindingSeverity Severity { get; init; }
    public required string SubjectStableId { get; init; }
    public required string Message { get; init; }
    public required string RuleVersion { get; init; }
    public required IReadOnlyList<string> EvidenceEntryNames { get; init; }
    public required IReadOnlyList<KeyValuePair<string, string>> Details { get; init; }
}

public enum FindingSeverity { Error, Warning, Info }
