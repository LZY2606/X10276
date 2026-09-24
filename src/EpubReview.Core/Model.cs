namespace EpubReview.Core;

/// <summary>Three distinct failure results plus ok/incomplete.</summary>
public enum ParseStatus
{
    Ok,
    Incomplete,
    BudgetExhausted,
    FormatCorrupt,
    SemanticallyUnresolvable
}

public sealed record ParseBudget(
    int MaxEntries = 4096,
    long MaxTotalBytes = 64L * 1024 * 1024,
    int MaxFallbackDepth = 64,
    int MaxXmlBytes = 8 * 1024 * 1024)
{
    public static readonly ParseBudget Default = new();
    public const string Version = "rules-1.0.0";

    public string ParameterVersion =>
        $"{Version}/maxEntries={MaxEntries}/maxBytes={MaxTotalBytes}/maxFallbackDepth={MaxFallbackDepth}/maxXml={MaxXmlBytes}";
}

public enum DiagCode
{
    MultiplePackages,
    ContainerMissing,
    ContainerParseError,
    NoRootfiles,
    PackageFileMissing,
    PackageParseError,
    ManifestItemMissingId,
    ManifestItemMissingHref,
    SpineMissing,
    SpineDuplicateIdref,
    SpineRefUnknownItem,
    PathEscapesContainer,
    ResourceFileMissing,
    FragmentNotFound,
    FallbackCycle,
    FallbackTargetMissing,
    FallbackDepthExceeded,
    MediaOverlayBrokenLink,
    MediaOverlayMissing,
    NavTargetMissing,
    UnknownMetadata,
    UndeclaredResource
}

public enum DiagSeverity { Info, Warning, Error }

/// <summary>A diagnostic always carries a position (entry path + optional line/column).</summary>
public sealed record Diagnostic(
    DiagCode Code,
    DiagSeverity Severity,
    string EntryPath,
    int? Line,
    int? Column,
    string Message,
    IReadOnlyList<string> AffectedIds)
{
    public string StableId => $"{Code}@{EntryPath}:{Line?.ToString() ?? "-"}:{Column?.ToString() ?? "-"}";
}

/// <summary>Immutable material: one ZIP entry, bytes preserved verbatim.</summary>
public sealed record ZipEntryMaterial(string Path, byte[] Bytes, string Sha256)
{
    public string StableId => "mat-" + Sha256[..12];
    public long SizeBytes => Bytes.LongLength;
}

public sealed record ManifestItem(
    string? Id,
    string? Href,
    string? NormalizedPath,
    string MediaType,
    string? FallbackId,
    string? MediaOverlayId,
    IReadOnlyList<string> Properties,
    string RawXml)
{
    public string StableId(string pkgPath) => $"{pkgPath}#item/{Id ?? "(no-id)"}";
}

public sealed record SpineItemRef(string? Idref, int Index, bool Linear)
{
    /// <summary>Stable identity independent of document edits: package + ordinal.</summary>
    public string StableId(string pkgPath) => $"{pkgPath}/spine#{Index}";
}

public sealed record UnknownMetadata(string RawXml, string Name);

public sealed record PackageDocument(
    string RootfilePath,
    string BaseDir,
    IReadOnlyList<ManifestItem> Manifest,
    IReadOnlyList<SpineItemRef> Spine,
    string RawMetadataXml,
    IReadOnlyList<UnknownMetadata> UnknownMetadata,
    string RawXml);

public sealed record NavLink(
    string Text,
    string? Href,
    string? NormalizedPath,
    string? Fragment,
    IReadOnlyList<NavLink> Children,
    string StableId);

public sealed record OverlayRef(
    string OverlayPath,
    string TextSrc,
    string? TextFragment,
    string? AudioSrc,
    string StableId);

public sealed record ResourceNode(string Path, string MediaType, long SizeBytes, string Sha256)
{
    public string StableId => "res-" + Sha256[..12];
}

public sealed record ResourceEdge(string FromPath, string ToPath, string Kind)
{
    public string StableId => $"edge:{Kind}:{FromPath}->{ToPath}";
}

public sealed record ResourceGraph(IReadOnlyList<ResourceNode> Nodes, IReadOnlyList<ResourceEdge> Edges)
{
    public string Digest()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var n in Nodes.OrderBy(n => n.Path, StringComparer.Ordinal))
            sb.Append(n.Path).Append('|').Append(n.MediaType).Append('|').Append(n.SizeBytes).Append(';');
        foreach (var e in Edges.OrderBy(e => e.StableId, StringComparer.Ordinal))
            sb.Append(e.StableId).Append(';');
        return Hashing.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
    }
}

public enum FallbackChainStatus { Ok, Cycle, MissingTarget, DepthExceeded }

public sealed record FallbackChain(
    string PackagePath,
    string StartItemId,
    IReadOnlyList<string> ItemIds,
    FallbackChainStatus Status,
    IReadOnlyList<string> CandidateItemIds)
{
    public string StableId => $"fb:{PackagePath}#{StartItemId}";
}

public sealed record ReadingOrderEntry(string PackagePath, int SpineIndex, string? Idref, string? NormalizedPath, bool Linear)
{
    public string StableId => $"{PackagePath}/spine#{SpineIndex}";
}

public sealed class ParseResult
{
    public required ParseStatus Status { get; init; }
    public required string ParameterVersion { get; init; }
    public required string InputDigest { get; init; }
    public List<Diagnostic> Diagnostics { get; } = new();
    public IReadOnlyList<ZipEntryMaterial> Entries { get; init; } = Array.Empty<ZipEntryMaterial>();
    public List<PackageDocument> Packages { get; } = new();
    public List<ReadingOrderEntry> ReadingOrder { get; } = new();
    public ResourceGraph Graph { get; init; } = new(Array.Empty<ResourceNode>(), Array.Empty<ResourceEdge>());
    public List<FallbackChain> FallbackChains { get; } = new();
    public List<NavLink> NavRoots { get; } = new();
    public List<OverlayRef> Overlays { get; } = new();
    public IReadOnlyList<UnknownMetadata> AllUnknownMetadata =>
        Packages.SelectMany(p => p.UnknownMetadata).ToList();
}

public static class Hashing
{
    public static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
}
