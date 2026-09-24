namespace EpubReview.Epub;

public enum Severity { Info, Warning, Error }

/// <summary>A located diagnostic. Position is path + optional 1-based line/column.</summary>
public sealed record Diagnostic(string Code, Severity Severity, string Message, string? Path, int? Line, int? Column)
{
    public string Position => Path is null ? "-" : Line is null ? Path : $"{Path}:{Line}:{Column}";
}

/// <summary>Immutable ZIP entry material. Raw bytes are always preserved.</summary>
public sealed record ZipEntryMaterial(string Name, byte[] Bytes, string MediaType, string Sha256)
{
    public string Id => "mat:" + Sha256[..16];
}

public sealed record ManifestItem(
    string Id,
    string HrefRaw,
    string? ResolvedPath,
    string? Fragment,
    string MediaTypeRaw,
    string? FallbackId,
    string? MediaOverlayId,
    string Properties);

public sealed record SpineRef(string IdRef, bool Linear, int Position);

/// <summary>Unrecognized metadata, preserved verbatim for round-trip.</summary>
public sealed record UnknownMetadata(string Id, string RawXml, string Reason);

public sealed record NavLink(string RawHref, string? ResolvedPath, string? Fragment, string Text, int Line);

public sealed record OverlayRef(
    string RawSrc, string? ResolvedPath, string? Fragment,
    string Kind, string? ClipBeginSeconds, string? ClipEndSeconds, int Line);

public sealed class PackageDoc
{
    public required string Path { get; init; }
    public required string RawXml { get; init; }
    public required string Sha256 { get; init; }
    public string Identity => "pkg:" + Sha256[..16];
    public List<ManifestItem> Manifest { get; } = new();
    public List<SpineRef> Spine { get; } = new();
    public List<UnknownMetadata> UnknownMetadata { get; } = new();
    public List<NavLink> NavLinks { get; } = new();
    public List<OverlayRef> OverlayRefs { get; } = new();
    /// <summary>resolved xhtml path -> set of fragment ids present in that document.</summary>
    public Dictionary<string, HashSet<string>> FragmentIds { get; } = new(StringComparer.Ordinal);
}

public enum ParseStatus { Ok, BudgetExhausted, Malformed, Unresolvable }

public sealed class EpubContainer
{
    public required string Fingerprint { get; init; }
    public List<ZipEntryMaterial> Entries { get; } = new();
    public string? ContainerXmlRaw { get; set; }
    public List<PackageDoc> Packages { get; } = new();
    public List<Diagnostic> Diagnostics { get; } = new();
    public HashSet<string> EntryNames { get; } = new(StringComparer.Ordinal);
    public ParseStatus Status { get; set; } = ParseStatus.Ok;
}
