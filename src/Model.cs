using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EpubReview;

/// <summary>三种必须区分的结果，外加正常。</summary>
public enum ReviewStatus
{
    Ok,
    BudgetExhausted,   // 资源预算耗尽
    FormatCorrupt,     // 格式损坏
    SemanticUnsolvable // 语义无解
}

public enum DiagCategory { Info, Warning, SemanticUnsolvable, BudgetExhausted, FormatCorrupt }

public sealed record Diagnostic(
    string Code,
    DiagCategory Category,
    string Message,
    string? Entry = null,     // 容器内条目（坐标基准：容器相对路径）
    int? Position = null,     // 条目内字符偏移（单位：char）
    string? Fragment = null);

public sealed record ReviewParameters(
    int MaxEntries,
    long MaxBytes,
    bool RequireMediaOverlay,
    bool CheckFragments,
    string RuleVersion)
{
    public static ReviewParameters Default => new(2048, 64L * 1024 * 1024, false, true, "rules-1.0");
    public string ToJson() => JsonSerializer.Serialize(this);
    public static ReviewParameters FromJson(string json) =>
        JsonSerializer.Deserialize<ReviewParameters>(json) ?? Default;
}

public sealed record Material(string Id, string Kind, string Name, string MediaType, byte[] Content)
{
    public static string IdFor(byte[] content) => "mat-" + Convert.ToHexString(SHA256.HashData(content))[..16].ToLowerInvariant();
    public static Material Epub(string name, byte[] content) => new(IdFor(content), "epub-container", name, "application/epub+zip", content);
}

public sealed record EventRecord(
    string Id, int Seq, string Kind, string? ParentId, string Payload, string CreatedAt)
{
    public static string NewId(int seq, string kind, string? parentId, string payload)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes($"{seq}|{kind}|{parentId}|{payload}"));
        return $"evt-{seq:D4}-" + Convert.ToHexString(h)[..10].ToLowerInvariant();
    }
}

// ---------- 快照模型（派生层，全部可 JSON 序列化） ----------

public sealed record SpineEntry(
    string Id,          // 稳定身份 sp-{index}
    string Idref,
    string? Href,
    string? NormalizedPath,
    string? MediaType,
    bool Linear,
    bool Duplicate,
    bool Exists,
    string? MediaOverlayId);

public sealed record ResourceNode(
    string Id,          // 稳定身份 res-{hash(path)}
    string Path,
    string MediaType,
    long SizeBytes,
    bool InManifest,
    bool InSpine,
    string? FallbackOf);

public sealed record RefEdge(
    string Id,          // 稳定身份 edge-{hash(source|raw|pos)}
    string Source,
    string RawHref,
    string? Target,
    string? Fragment,
    string Status,      // Ok | MissingTarget | MissingFragment | EscapesContainer | External
    int Position,
    string Via);        // href | src | css-url | smil-text | smil-audio | overlay

public sealed record NavNode(string Id, string Label, string? Href, string? Fragment, List<NavNode> Children);

public sealed record BrokenAnchor(string Id, string Entry, string RawHref, string? Fragment, string Reason, int Position);

public sealed record FallbackChain(
    string Id,
    string StartItem,
    string Kind,        // resolved | cycle | missing
    List<string> Items, // 链上 manifest id
    string? Terminal,   // resolved 时的最终资源
    string? Detail);

public sealed record OverlayLink(string Id, string OverlayPath, string TextRef, string? AudioRef, string Status, string? Detail);

public sealed record PackageInfo(
    string Id, string Path, string? Title, string? Identifier, string? Language,
    int ManifestCount, int SpineCount, List<string> UnknownMetadata);

public sealed record DecisionView(string Id, string Target, string Verdict, string Reviewer, string Note, string EventId, string CreatedAt);

public sealed record BasisInfo(
    string TimeBase,          // 时间基准
    string CoordinateBase,    // 坐标基准
    string SizeUnit,          // 单位
    string SvgCoordinate,     // SVG 坐标说明
    string ValidFrom,         // 有效区间起
    string ValidTo,           // 有效区间止
    string InvalidationReason // 失效原因
);

public sealed record ReviewSnapshot(
    ReviewStatus Status,
    string RuleVersion,
    string InputDigest,
    string[] ParentIds,
    string? MaterialId,
    ReviewParameters Parameters,
    List<SpineEntry> ReadingOrder,
    List<ResourceNode> Resources,
    List<RefEdge> Edges,
    List<NavNode> NavTree,
    List<BrokenAnchor> BrokenAnchors,
    List<FallbackChain> FallbackChains,
    List<OverlayLink> Overlays,
    List<Diagnostic> Diagnostics,
    List<PackageInfo> Packages,
    List<DecisionView> Decisions,
    BasisInfo Basis)
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = false };
    public string ToJson() => JsonSerializer.Serialize(this, Opts);
    public static ReviewSnapshot FromJson(string json) => JsonSerializer.Deserialize<ReviewSnapshot>(json)!;
}

public static class StableId
{
    public static string Hash8(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..8].ToLowerInvariant();
    public static string DigestOf(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}
