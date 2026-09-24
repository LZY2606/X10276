namespace EpubReview.Core;

/// <summary>诊断位置：ZIP 条目 + 行列坐标基准（1 基，单位：字符）。</summary>
public sealed record SourcePosition(string Entry, int Line, int Column)
{
    public const string Basis = "zip-entry/line/column(1-based,char)";
    public override string ToString() => $"{Entry}:{Line}:{Column}";
}

public enum DiagnosticSeverity { Info, Warning, Error }

/// <summary>结构化诊断。输入不完整时以此返回，绝不用空值伪装成功。</summary>
public sealed record Diagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Message,
    SourcePosition? Position,
    IReadOnlyList<string> AffectedObjects);

/// <summary>分析结论的三种失败与成功必须可区分。</summary>
public enum AnalysisStatus { Ok, BudgetExhausted, FormatCorrupt, SemanticallyUnsolvable }

/// <summary>不可变材料：字节与原始表示永久保留。</summary>
public sealed record Material(
    string Id,          // 稳定身份：sha256 摘要
    string Kind,        // ZipEntry | ContainerXml | PackageDocument | NavDocument | MediaOverlay | UnknownMetadata | ContentDocument
    string MediaType,   // 资源 media type，原样保留
    byte[] Bytes,       // ZIP 条目字节，原样保留
    string OriginalName // 容器内路径，原样保留
)
{
    public long ByteCount => Bytes.LongLength; // 单位：byte
}

public sealed record ManifestItem(
    string Id, string Href, string MediaType,
    string? Fallback, string? MediaOverlay, string? Properties,
    SourcePosition Position);

public sealed record SpineItem(string IdRef, bool Linear, SourcePosition Position);

public sealed record UnknownMetadata(string RawXml, SourcePosition Position);

public sealed record PackageDocument(
    string RootfilePath,          // container.xml 中声明的 full-path，原样保留
    string BaseDir,               // 规范化后的容器内目录（坐标基准：容器根）
    IReadOnlyList<ManifestItem> Manifest,
    IReadOnlyList<SpineItem> Spine,
    IReadOnlyList<UnknownMetadata> UnknownMetadata);

public sealed record NavLink(string Text, string Href, string? Fragment, SourcePosition Position);

public sealed record NavNode(string Text, string? Href, string? Fragment, IReadOnlyList<NavNode> Children);

public sealed record OverlayRef(string SmilPath, string TextSrc, string? Fragment, SourcePosition Position);

/// <summary>解析后的 EPUB 容器快照（不可变材料层的视图）。</summary>
public sealed record ParsedContainer(
    IReadOnlyList<Material> Materials,
    IReadOnlyList<PackageDocument> Packages,
    IReadOnlyList<NavNode> NavTree,
    IReadOnlyList<OverlayRef> OverlayRefs,
    IReadOnlyList<Diagnostic> Diagnostics,
    AnalysisStatus Status);

/// <summary>资源引用边：from-entry 引用 to-entry（fragment 原样保留）。</summary>
public sealed record ResourceEdge(string FromEntry, string ToEntry, string? Fragment, string Kind, SourcePosition Position);

public sealed record ReadingOrderEntry(int Index, string SpineIdRef, string? ManifestItemId, string? ResolvedEntry, bool IsDuplicate);

public sealed record FallbackNode(string ManifestItemId, string Href, string MediaType, string State, IReadOnlyList<string> Chain);

public sealed record BrokenAnchor(string SourceEntry, string TargetEntry, string Fragment, IReadOnlyList<string> Candidates);

public sealed record AnalysisResult(
    AnalysisStatus Status,
    IReadOnlyList<ReadingOrderEntry> ReadingOrder,
    IReadOnlyList<ResourceEdge> ResourceGraph,
    IReadOnlyList<NavNode> NavTree,
    IReadOnlyList<BrokenAnchor> BrokenAnchors,
    IReadOnlyList<FallbackNode> Fallbacks,
    IReadOnlyList<Diagnostic> Diagnostics,
    string InputDigest,
    string ParameterVersion);
