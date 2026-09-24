using System.Text.Json.Serialization;

namespace EpubReview.Core;

// ============================= 不可变材料层 =============================

/// <summary>ZIP 条目字节：原样保留（包括压缩方法、CRC、路径、目录标记）。</summary>
public sealed record ZipEntryRecord(
    string EntryName,
    long CompressedLength,
    long Length,
    uint Crc32,
    string CompressionMethod,
    bool IsDirectory,
    byte[] Bytes)
{
    public string BlobId => StableIds.BlobId(Bytes);
}

public sealed record MediaTypeRecord(string Value, string? GuessedFrom, bool Declared);

public sealed record ManifestItemRecord(
    string Id,
    string HrefRaw,                 // 原始表示保留
    string? HrefNormalized,         // 派生层才规范化；越界/无解时为 null
    bool HrefEscapesContainer,
    string MediaType,
    string? FallbackIdRaw,          // fallback 属性原值
    string? FallbackTargetItemId,   // 解析到的 manifest id（缺失为 null 且出诊断）
    string? MediaOverlayIdRaw,
    string? MediaOverlayTargetItemId,
    bool MediaOverlayBroken,
    IReadOnlyList<AnchorRecord> DeclaredAnchors,
    TextLocation? Location);

public sealed record AnchorRecord(string Id, string Kind, TextLocation? Location);

public sealed record SpineItemRefRecord(
    string IdRefRaw,
    int Ordinal,
    bool Linear,
    string? TargetItemId,
    TextLocation? Location);

public sealed record UnknownMetadataRecord(
    string ElementName,
    string Namespace,
    string OuterXml,
    string RawPath,
    TextLocation? Location);

public sealed record MetadataRecord(
    IReadOnlyDictionary<string, string> Known,
    IReadOnlyList<UnknownMetadataRecord> Unknown);

public sealed record OverlayRefRecord(
    string SourceItemId,
    string TargetSmilItemId,
    string? TextRefFragment,
    TextLocation? Location);

public sealed record PackageDocumentRecord(
    string PackageKey,                // 多 package 各自稳定键
    string? RootFilePath,             // container.xml 中声明的完整路径（可能越界则保留原值）
    string? MediaType,
    string? UniqueIdentifier,
    string? Version,
    string? Lang,
    MetadataRecord Metadata,
    IReadOnlyList<ManifestItemRecord> Manifest,
    IReadOnlyList<SpineItemRefRecord> Spine,
    IReadOnlyList<OverlayRefRecord> MediaOverlays,
    byte[] RawBytes,
    IReadOnlyList<Diagnostic> Diagnostics,
    TextLocation? Location);

public sealed record ContainerRootFileRecord(
    string FullPathRaw,
    string? FullPathNormalized,
    bool EscapesContainer,
    string? MediaType,
    TextLocation? Location);

public sealed record ContainerRecord(
    byte[] RawBytes,
    string? Version,
    IReadOnlyList<ContainerRootFileRecord> RootFiles,
    IReadOnlyList<Diagnostic> Diagnostics,
    TextLocation? Location);

/// <summary>一次直接来源导入保留的全部不可变材料。</summary>
public sealed record SourceMaterial
{
    public required string SourceId { get; init; }
    public required string Label { get; init; }
    public required byte[] OriginalBytes { get; init; }          // 整个 EPUB 字节
    public string OriginalBlobId => StableIds.BlobId(OriginalBytes);
    public required IReadOnlyList<ZipEntryRecord> Entries { get; init; }
    public ContainerRecord? Container { get; init; }
    public required IReadOnlyList<PackageDocumentRecord> Packages { get; init; }
    /// <summary>条目路径 → media type（声明优先，未知保留 application/octet-stream 之外的原值）。</summary>
    public required IReadOnlyDictionary<string, MediaTypeRecord> MediaTypes { get; init; }
    public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }
    /// <summary>格式损坏/输入不完整时仍保留已读到的材料，解析结果按阶段给出结局。</summary>
    public required IReadOnlyDictionary<string, string> StageOutcomes { get; init; }
}

// ============================= 事件（机器可读判定依据） =============================

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(SourceImportedEvent), "source-imported")]
[JsonDerivedType(typeof(DecisionRecordedEvent), "decision")]
[JsonDerivedType(typeof(ParameterRevisedEvent), "parameter-revised")]
public abstract record ReviewEvent
{
    public required string EventId { get; init; }
    public required string CaseId { get; init; }
    public required long Sequence { get; init; }
    public required DateTimeOffset UtcTime { get; init; }
    public abstract string Summary { get; }
}

public sealed record SourceImportedEvent : ReviewEvent
{
    public required string SourceId { get; init; }
    public required string Label { get; init; }
    public required string InputBlobId { get; init; }
    /// <summary>该事件触发后产出的计算版本（结果自身记录输入摘要/参数版本/父级依赖）。</summary>
    public required string ComputationId { get; init; }
    public override string Summary() => $"导入来源 {Label}";
    public string Summary => Summary();
}

public sealed record DecisionRecordedEvent : ReviewEvent
{
    public required string SubjectKind { get; init; }   // package | fallback-chain | anchor | parameter
    public required string SubjectStableId { get; init; }
    /// <summary>accept | reject | choose-candidate | set-parameter</summary>
    public required string Action { get; init; }
    public required string? ChosenCandidateId { get; init; }
    public required IReadOnlyList<string> CandidateIds { get; init; }
    public required string Rationale { get; init; }
    public required string ParentDecisionEventId { get; init; } // 无父级为 ""
    public required string SupersededComputationId { get; init; }
    public required string ComputationId { get; init; }
    public override string Summary() =>
        $"{SubjectKind}:{SubjectStableId} {Action}" + (ChosenCandidateId is null ? "" : $" -> {ChosenCandidateId}");
    public new string Summary => Summary();
}

public sealed record ParameterRevisedEvent : ReviewEvent
{
    public required string ParameterName { get; init; }
    public required string OldValue { get; init; }
    public required string NewValue { get; init; }
    public required string Reason { get; init; }
    public required int ParameterVersion { get; init; }
    public required string SupersededComputationId { get; init; }
    public required string ComputationId { get; init; }
    public override string Summary() => $"参数 {ParameterName}: {OldValue} -> {NewValue}";
    public new string Summary => Summary();
}

// ============================= 计算结果（派生层） =============================

public sealed record ComputationRef(
    string ComputationId,
    string TriggerEventId,
    string InputDigest,
    int ParameterVersion,
    IReadOnlyList<string> ParentDependencyIds,
    long Sequence,
    DateTimeOffset UtcTime,
    long? ValidFromSequence,
    long? ValidToSequence,
    bool Current);

public enum NodeKind
{
    Entry = 0,
    ManifestItem = 1,
    Fragment = 2,
    MediaOverlay = 3,
}

public enum EdgeKind
{
    PackageContains = 0,
    SpineReferences = 1,
    Fallback = 2,
    MediaOverlay = 3,
    ContentRef = 4,
    NavRef = 5,
}

public sealed record GraphNodeRecord(
    string NodeId,
    NodeKind Kind,
    string Label,
    string? EntryName,
    string? ManifestItemId,
    string? Fragment,
    string? MediaType,
    string CoordinateBasis,   // “容器根相对路径 + 条目内锚点”
    bool InSpine);

public sealed record GraphEdgeRecord(
    string EdgeId,
    EdgeKind Kind,
    string FromNodeId,
    string ToNodeId,
    string RawReference,
    TextLocation? Location);

public sealed record GraphSnapshot(
    IReadOnlyList<GraphNodeRecord> Nodes,
    IReadOnlyList<GraphEdgeRecord> Edges,
    int NodeBudget,
    bool BudgetExceeded,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record ReadingOrderStepRecord(
    int Ordinal,
    string NodeId,
    string ItemId,
    string Label,
    bool Linear,
    bool Duplicate,
    string? HrefNormalized,
    TextLocation? Location);

public sealed record ReadingOrderSnapshot(
    IReadOnlyList<ReadingOrderStepRecord> Steps,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record NavNodeRecord(
    string NavNodeId,
    int Depth,
    int Ordinal,
    string Text,
    string? RefHref,
    string? TargetNodeId,
    bool Broken,
    IReadOnlyList<NavNodeRecord> Children);

public sealed record NavigationSnapshot(
    IReadOnlyList<NavNodeRecord> Tree,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record BrokenAnchorRecord(
    string AnchorId,
    string FromNodeId,
    string RawReference,
    string ReasonCode,
    TextLocation? Location);

public sealed record FallbackCandidateRecord(
    string ChainId,
    string StartItemId,
    IReadOnlyList<string> CandidateItemIds,
    bool Cyclic,
    bool Missing,
    string? CycleAtItemId,
    string? MissingAtItemId,
    string? AcceptedItemId,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record PackageReviewRecord(
    string PackageKey,
    string? RootFilePath,
    string? MediaType,
    bool Selectable,
    bool IsChosen,
    IReadOnlyList<string> SelectionCandidatePackageKeys,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record MaterialEntryView(
    string EntryName,
    long Length,
    string BlobId,
    string MediaType,
    bool InManifest,
    bool InSpine,
    bool ReferencedOutsideSpine);

public sealed record ReviewComputation
{
    public required ComputationRef Header { get; init; }
    public required OutcomeKind Outcome { get; init; }
    public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }
    public required IReadOnlyList<PackageReviewRecord> Packages { get; init; }
    public required GraphSnapshot Graph { get; init; }
    public required ReadingOrderSnapshot ReadingOrder { get; init; }
    public required NavigationSnapshot Navigation { get; init; }
    public required IReadOnlyList<BrokenAnchorRecord> BrokenAnchors { get; init; }
    public required IReadOnlyList<FallbackCandidateRecord> FallbackChains { get; init; }
    public required IReadOnlyList<MaterialEntryView> Materials { get; init; }
    public required string SourceId { get; init; }
}

// ============================= 参数 =============================

public sealed record ReviewParameters(
    int MaxGraphNodes,
    IReadOnlyDictionary<string, string> Extra)
{
    public static ReviewParameters Default =>
        new(10000, new Dictionary<string, string>());
}
