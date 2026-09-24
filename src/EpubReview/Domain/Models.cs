using System.Text.Json.Serialization;

namespace EpubReview.Domain;

// ---- 三种互斥结果：资源预算耗尽 / 格式损坏 / 语义无解 ----
public enum RunStatus { Ok, BudgetExhausted, FormatCorrupt, SemanticUnsolvable }

public enum DiagCode
{
    MultiPackage, FallbackCycle, FallbackMissing, FragmentMissing,
    PathEscape, SpineDuplicate, OverlayBrokenLink, UnknownMetadata,
    UnconvertibleValue,
}

public enum Severity { Error, Warning, Info }

public sealed record Diagnostic(
    DiagCode Code, Severity Severity, string Message,
    string? Path = null, int? Line = null, string? ObjectId = null)
{
    public string StableId => $"diag_{Code}_{Path ?? "?"}_{Line?.ToString() ?? "?"}_{ObjectId ?? "?"}";
}

// ---- 可见对象：每个显示值都带稳定身份 ----
public sealed record ReadingOrderEntry(string Id, int Position, string PackagePath, string ItemId,
    string Href, string MediaType, bool Linear, string? OverlayId, string? Note);

public sealed record GraphNode(string Id, string Kind, string Label, string Href, string MediaType);
public sealed record GraphEdge(string Id, string FromId, string ToId, string Relation,
    string? Fragment, string SourcePath, int? Line);

public sealed record NavNode(string Id, string Text, string Href, string? Fragment, List<NavNode> Children);

public sealed record BrokenAnchor(string Id, string SourcePath, int? Line,
    string TargetHref, string Fragment, string Reason);

public sealed record FallbackCandidate(string Id, string ItemId, string ItemHref,
    string Status, List<string> Chain, List<string> LegalCandidates, string? CycleAt);

public sealed record UnknownMeta(string Id, string PackagePath, int Line, string RawXml);

public sealed record AnalysisResult
{
    public required RunStatus Status { get; init; }
    public required string InputDigest { get; init; }
    public required string ParamVersion { get; init; }
    public required List<string> ParentIds { get; init; }
    public List<Diagnostic> Diagnostics { get; init; } = new();
    public List<ReadingOrderEntry> ReadingOrder { get; init; } = new();
    public List<GraphNode> Nodes { get; init; } = new();
    public List<GraphEdge> Edges { get; init; } = new();
    public List<NavNode> NavTree { get; init; } = new();
    public List<BrokenAnchor> BrokenAnchors { get; init; } = new();
    public List<FallbackCandidate> FallbackCandidates { get; init; } = new();
    public List<UnknownMeta> UnknownMetadata { get; init; } = new();
    // 单位 / 坐标基准 / 时间基准 / 有效区间（界面需要暴露）
    public string? Budget { get; init; }
    public string? CoordinateBasis { get; init; }
    public string? GeneratedAt { get; init; }
    public string? ValidRange { get; init; }
}

// ---- 不可变材料层 ----
public sealed class Material
{
    public required string Id { get; set; }        // mat_<sha256:12>
    public required string ProjectId { get; set; }
    public required string Kind { get; set; }      // ZipEntry | ContainerXml | PackageDocument | MediaType | Fallback | Fragment | UnknownMetadata
    public string? Path { get; set; }
    public string? MediaType { get; set; }
    public required byte[] Bytes { get; set; }
    public required string Hash { get; set; }
    public long CreatedAt { get; set; }
}

// ---- 事件：输入摘要 + 参数版本 + 父级依赖 ----
public sealed class EventRecord
{
    public required string Id { get; set; }        // evt_0001 ...
    public required string ProjectId { get; set; }
    public required string Branch { get; set; }
    public required string Kind { get; set; }      // Import | ParamRevision | SourceChange | Decision
    public required string ParamVersion { get; set; }
    public required string InputDigest { get; set; }
    public required List<string> Parents { get; set; }
    public required string Payload { get; set; }   // JSON
    public required long CreatedAt { get; set; }
    public int Seq { get; set; }
    public string? ComputationId { get; set; }
}

public sealed class Decision
{
    public required string Id { get; set; }
    public required string ProjectId { get; set; }
    public required string TargetId { get; set; }
    public required string Verdict { get; set; }   // accept | reject | adjust
    public string Params { get; set; } = "{}";
    public string Rationale { get; set; } = "";
    public required string EventId { get; set; }
    public long CreatedAt { get; set; }
}

public sealed class Computation
{
    public required string Id { get; set; }
    public required string ProjectId { get; set; }
    public required string EventId { get; set; }
    public required string InputDigest { get; set; }
    public required string ParamVersion { get; set; }
    public required string ParentsJson { get; set; }
    public required string Status { get; set; }
    public required string ResultJson { get; set; }
    public long CreatedAt { get; set; }
}
