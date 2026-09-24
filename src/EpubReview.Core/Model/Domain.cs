namespace EpubReview.Core.Model;

/// <summary>三种必须区分开的分析终止状态，外加正常完成。</summary>
public enum AnalysisStatus
{
    Ok = 0,
    BudgetExhausted = 1,   // 资源预算耗尽
    FormatCorrupt = 2,     // 格式损坏
    SemanticallyUnresolvable = 3 // 语义无解（输入不完整，无法唯一决定）
}

public static class FindingCodes
{
    public const string MultiplePackages = "multiple-packages";
    public const string FallbackCycle = "fallback-cycle";
    public const string FallbackMissing = "fallback-missing";
    public const string FragmentMissing = "fragment-missing";
    public const string PathEscape = "path-escape";
    public const string SpineDuplicateIdref = "spine-duplicate-idref";
    public const string SpineIdrefMissing = "spine-idref-missing";
    public const string OverlayBrokenLink = "overlay-broken-link";
    public const string UnknownMetadata = "unknown-metadata";
    public const string ResourceMissing = "resource-missing";
    public const string ManifestResourceMissing = "manifest-resource-missing";
    public const string ManifestItemIncomplete = "manifest-item-incomplete";
    public const string NotInSpine = "not-in-spine";
    public const string BudgetExceeded = "budget-exceeded";
    public const string FormatCorrupt = "format-corrupt";
    public const string Unresolvable = "unresolvable";
    public const string ContainerMissing = "container-missing";
}

/// <summary>带位置的诊断；输入不完整时用它说明，绝不用空值伪装成功。</summary>
public sealed record Diagnostic(
    string Code,
    string Severity, // "error" | "warning" | "info"
    string Message,
    string? Path,
    int? Line,
    int? Column,
    string? Fragment);

/// <summary>不可变材料：一条 ZIP 条目的原始字节。</summary>
public sealed record EntryMaterial(
    string Id,
    string Path,
    string Sha256,
    string MediaType,
    string Kind,
    byte[] Bytes);

public sealed record ManifestItem(
    string Id,
    string HrefRaw,
    string HrefNormalized,
    string MediaType,
    string? Fallback,
    string? Properties,
    string? MediaOverlay,
    int Line);

public sealed record SpineItemref(string Idref, string? Id, bool Linear, string? Properties, int Line);

public sealed record PackageDocument(
    string MaterialId,
    string Path,
    string Dir,
    List<ManifestItem> Manifest,
    List<SpineItemref> Spine,
    List<string> UnknownMetadataRaw,
    Dictionary<string, string> KnownMetadata);

public sealed record RefEdge(
    string Id,
    string FromPath,
    string ToPathRaw,
    string? ToPathNormalized,
    string? Fragment,
    string Kind,      // content-ref | nav-ref | overlay-text | overlay-audio
    string State,     // ok | missing | escaped | external | fragment-missing
    List<string> Candidates);

public sealed record OverlayClip(
    string Id,
    string OverlayPath,
    string TextSrcRaw,
    string? AudioSrcRaw,
    string ClipBeginRaw,
    string? ClipEndRaw,
    double? BeginSeconds,
    double? EndSeconds,
    string TimeBase); // "npt"

public sealed record NavNode(string Title, string? HrefRaw, string? Fragment, List<NavNode> Children);

public sealed record ReadingOrderEntry(int Index, string Idref, string? ItemId, string? Href, bool Linear, string PackagePath);

public sealed record GraphNode(string Id, string Path, string Kind, string MediaType, int Depth, bool InSpine);
public sealed record GraphEdge(string From, string To, string Kind, string State);

public sealed record AnalysisResult(
    AnalysisStatus Status,
    string InputDigest,
    string ParamVersion,
    List<Diagnostic> Diagnostics,
    List<PackageDocument> Packages,
    List<ReadingOrderEntry> ReadingOrder,
    List<GraphNode> GraphNodes,
    List<GraphEdge> GraphEdges,
    List<NavNode> NavTrees,
    List<RefEdge> BrokenAnchors,
    List<RefEdge> AllEdges,
    List<OverlayClip> OverlayClips,
    List<FindingDraft> Findings,
    List<string> UnknownMetadata);

public sealed record FindingDraft(
    string Id,
    string Code,
    string Severity,
    string Subject,
    string? Path,
    int? Line,
    int? Column,
    string? Fragment,
    string Message,
    string PayloadJson);
