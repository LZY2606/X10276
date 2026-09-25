using System.Text.Json.Serialization;

namespace EpubCasebook.Core;

[JsonConverter(typeof(JsonStringEnumConverter<RunResultKind>))]
public enum RunResultKind
{
    Ok,
    BudgetExhausted,
    Malformed,
    Unsolvable
}

public static class Diag
{
    public const string BudgetExceeded = "budget-exceeded";
    public const string MalformedZip = "malformed-zip";
    public const string MalformedXml = "malformed-xml";
    public const string NoContainer = "no-container";
    public const string NoRootfile = "no-rootfile";
    public const string MultiPackage = "multi-package";
    public const string PackageNotCandidate = "package-not-candidate";
    public const string PathEscape = "path-escape";
    public const string PathUndecodable = "path-undecodable";
    public const string FallbackCycle = "fallback-cycle";
    public const string FallbackDepthExceeded = "fallback-depth-exceeded";
    public const string MissingResource = "missing-resource";
    public const string FragmentMissing = "fragment-missing";
    public const string SpineDuplicate = "spine-duplicate";
    public const string SpineMissingIdref = "spine-missing-idref";
    public const string OverlayBrokenLink = "overlay-broken-link";
    public const string UnknownMetadata = "unknown-metadata";
    public const string ClockUnconvertible = "clock-unconvertible";
}

public sealed record Diagnostic(string Code, string Severity, string Message, string Location, string? RawValue);

public sealed record RuleParams(int MaxEntries, long MaxBytes, int MaxFallbackDepth, string RuleVersion)
{
    public static RuleParams Default => new(512, 64L * 1024 * 1024, 8, "rules/1");
}

public sealed record Material(
    string Id, string CaseId, string Kind, string Name, string? MediaType,
    byte[] Bytes, string Sha256, string RawPropsJson);

public sealed record ManifestItem(
    string Id, string RawHref, string? Href, string MediaType,
    string? Fallback, string? MediaOverlay, string? Properties);

public sealed record SpineRef(string Idref, bool Linear, int Index);

public sealed record PackageDoc(
    string RootfilePath, string BaseDir,
    List<ManifestItem> Manifest, List<SpineRef> Spine,
    List<string> UnknownMetadata, Dictionary<string, string> KnownMetadata);

public sealed record OverlayClip(
    string TextRaw, string? TextPath, string? TextFrag,
    string AudioRaw, string? AudioPath,
    double? BeginSec, double? EndSec, string? RawBegin, string? RawEnd);

public sealed record OverlayDoc(string SmilPath, List<OverlayClip> Clips);

public sealed record GraphNode(string Id, string Href, string MediaType, bool InSpine, int? SpineIndex, bool Phantom);

public sealed record GraphEdge(string From, string To, string Kind, string RawHref, string? Fragment, string Status);

public sealed record ReadingEntry(int SpineIndex, string Idref, string Href, bool Linear, double? ClipBeginSec, double? ClipEndSec);

public sealed record NavNode(string Title, string? RawHref, string? Href, string? Fragment, string Status, List<NavNode> Children);

public sealed record FallbackCandidate(string ItemId, List<string> Chain, string Status, string? TerminalHref);

public sealed record BrokenAnchor(string Source, string RawHref, string? Fragment, string Reason, string Location);

public sealed record ReviewRun(
    string Id, string CaseId, string InputDigest, string RuleVersion, string? ParentRunId,
    RunResultKind Kind, List<string> PackageCandidates, string? SelectedPackage,
    List<ReadingEntry> ReadingOrder, List<GraphNode> Nodes, List<GraphEdge> Edges,
    List<NavNode> NavTree, List<BrokenAnchor> BrokenAnchors,
    List<FallbackCandidate> Fallbacks, List<Diagnostic> Diagnostics,
    List<string> UnknownMetadata, List<string> AffectedMaterials, string CreatedAtUtc);

public sealed record CaseEvent(
    string Id, string CaseId, long Seq, string Kind, string? ParentEventId,
    string RuleVersion, string InputDigest, string PayloadJson, string CreatedAtUtc);

public static class EventKinds
{
    public const string ImportBaseline = "import-baseline";
    public const string ParameterRevision = "parameter-revision";
    public const string SourceDataChange = "source-data-change";
    public const string DecisionAccept = "decision-accept";
    public const string DecisionReject = "decision-reject";
    public const string DecisionAdjust = "decision-adjust";
}
