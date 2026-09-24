using System.Text.Json.Serialization;

namespace EpubReview;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DiagSeverity { Info, Warning, Error }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FailureClass { None, BudgetExhausted, CorruptFormat, SemanticUnresolvable }

public record SourcePos(string File, int? Line, int? Column)
{
    public override string ToString() => Line is null ? File : $"{File}:{Line}:{Column ?? 0}";
}

public record Diagnostic(string Id, string Code, DiagSeverity Severity, string Message, SourcePos Pos, string? ObjectId);

public record Material(string Id, string Kind, string Name, byte[] Bytes, string Sha256, string MediaType);

public record EventRecord(long Seq, string Id, long? ParentSeq, string Kind, string Payload, string CreatedAt);

public record Derivation(string Id, long EventSeq, string RuleVersion, string InputDigest, string ParentsJson, string OutputJson);

public record RulesParams(int Version, int MaxEntries, long MaxTotalBytes, int MaxFallbackDepth, int MaxEdges, bool OverlayAudioMustExist)
{
    public static RulesParams Default => new(1, 1000, 50_000_000, 8, 5000, false);
}

public record ImportPayload(string Label, string ZipSha256, List<string> MaterialIds, FailureClass Status, List<Diagnostic> Diagnostics, string? Note);

public record ParamPayload(RulesParams Rules, string Note);

public record DecisionPayload(string ObjectId, string Action, string Reviewer, string Note);

public record SourceChangePayload(long FromImport, long ToImport, List<string> ChangedEntries, string Note);

public sealed class AnalysisReport
{
    public FailureClass Status { get; set; } = FailureClass.None;
    public string RuleVersion { get; set; } = "";
    public string InputDigest { get; set; } = "";
    public long ImportSeq { get; set; }
    public long CursorSeq { get; set; }
    public List<PackageInfo> Packages { get; set; } = new();
    public List<EntryInfo> Entries { get; set; } = new();
    public List<ReadingOrderEntry> ReadingOrder { get; set; } = new();
    public List<string> NotInSpine { get; set; } = new();
    public List<GraphEdge> Edges { get; set; } = new();
    public List<NavNode> NavTree { get; set; } = new();
    public List<FallbackCandidate> Fallbacks { get; set; } = new();
    public List<OverlayLink> Overlays { get; set; } = new();
    public List<UnknownMeta> UnknownMetadata { get; set; } = new();
    public List<Diagnostic> Diagnostics { get; set; } = new();
    public List<DecisionConclusion> Decisions { get; set; } = new();
    public string Digest { get; set; } = "";
}

public record PackageInfo(string Id, string Path, bool Primary, string Version);

public record EntryInfo(string Name, long Bytes, string Sha256, string MediaType);

public record ReadingOrderEntry(int Index, string ItemId, string? Href, bool Duplicate, bool Linear, string PackagePath);

public record GraphEdge(string Id, string From, string? To, string RawHref, string Kind, string Status, SourcePos Pos, string? Fragment);

public record NavNode(string Title, string? Href, int Depth, string Status, SourcePos Pos, List<NavNode> Children);

public record FallbackCandidate(string Id, string ItemId, List<string> Chain, string Status, string? Terminal);

public record OverlayLink(string Id, string SmilPath, string Kind, string RawHref, string? Resolved, string Status,
    string? ClipBeginRaw, double? ClipBeginSeconds, string? ClipEndRaw, double? ClipEndSeconds, SourcePos Pos);

public record UnknownMeta(string Id, string PackagePath, string Raw, SourcePos Pos);

public record DecisionRecord(long Seq, string Action, string Reviewer, string Note);

public record DecisionConclusion(string ObjectId, string? EffectiveAction, bool Conflicted, List<DecisionRecord> History);

public record DiffRow(string Aspect, string ObjectId, string ValueA, string ValueB);

public record CompareResult(long SeqA, long SeqB, List<DiffRow> Rows);
