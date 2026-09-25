using System;
using System.Collections.Generic;

namespace EpubReview.Domain.Analysis;

/// <summary>追加式历史事件：不可变材料修订、参数修订、人工决定与分析派生全部以事件保存。</summary>
public sealed record HistoryEvent
{
    public required long Sequence { get; init; }
    public required string EventId { get; init; }
    public required string BranchId { get; init; }
    public required string BundleId { get; init; }
    public required string Kind { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required string Author { get; init; }
    public required string Summary { get; init; }
    /// <summary>修订的目标条目（材料修订事件）。</summary>
    public string? EntryName { get; init; }
    public string? EntrySha256Before { get; init; }
    public string? EntrySha256After { get; init; }
    /// <summary>参数版本（参数修订事件）。</summary>
    public string? ParametersVersion { get; init; }
    /// <summary>人工决定。</summary>
    public Decision? Decision { get; init; }
    /// <summary>该事件完成后可见的分析快照 id（可回放到此版本的依赖与结论）。</summary>
    public string? AnalysisId { get; init; }
    public required string InputDigest { get; init; }
}

/// <summary>可修改决定层：接受 / 否定 / 参数调整。互相冲突的决定都保留，并标记与既有决定的冲突。</summary>
public sealed record Decision
{
    public required string DecisionId { get; init; }
    public required DecisionAction Action { get; init; }
    public required string SubjectStableId { get; init; }
    public required string SubjectKind { get; init; }
    /// <summary>被采纳或否定的候选稳定 id（例如某个 fallback 候选）。</summary>
    public string? CandidateStableId { get; init; }
    public required string Rationale { get; init; }
    /// <summary>参数调整载荷（Action == ParameterAdjustment 时使用）。</summary>
    public IReadOnlyList<KeyValuePair<string, string>>? ParameterChanges { get; init; }
    public required bool Adopted { get; init; }
    public required IReadOnlyList<string> ConflictsWithDecisionIds { get; init; }
    public required string SupersedesDecisionId { get; init; }
    /// <summary>有效区间 [validFromEvent, validToEvent)；被后续决定否定时闭合。</summary>
    public long? ValidFromEvent { get; init; }
    public long? ValidToEvent { get; init; }
}

public enum DecisionAction { Accept, Reject, ParameterAdjustment }

public sealed record BranchInfo
{
    public required string BranchId { get; init; }
    public required string Name { get; init; }
    public required string ParentBranchId { get; init; }
    public required long ForkFromSequence { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public bool IsTrunk { get; init; }
}
