namespace EpubReview.Domain;

/// <summary>三种必须区分的失败结果：预算耗尽 / 格式损坏 / 语义无解。</summary>
public enum FailureKind { None, BudgetExhausted, FormatCorrupt, SemanticallyUnsolvable }

public record DiagLocation(string? ContainerPath, int? Line, int? Column, string? Fragment);

public record Diagnostic(
    string Id, string Kind, string Severity,
    DiagLocation? Location, string Message, string? Evidence)
{
    public static Diagnostic Make(string kind, string message, string severity = "error",
        DiagLocation? location = null, string? evidence = null)
        => new(Guid.NewGuid().ToString("N"), kind, severity, location, message, evidence);
}

/// <summary>规则参数版本。所有计算结果都记录该版本。</summary>
public record RuleSet(
    string Version,
    int MaxEntries = 10_000,
    long MaxUncompressedBytes = 200_000_000,
    int MaxFallbackDepth = 32,
    string DuplicateSpineSeverity = "error",
    bool MultiPackageIsError = false)
{
    public static RuleSet V1 => new("rules-1.0.0");
}

// ---------- 不可变材料层 ----------

public record ZipEntryInfo(string Path, long Size, string Sha256, string MediaType);

public record ManifestItem(
    string Id, string Href, string? NormalizedPath, string MediaType,
    string? Fallback, string? MediaOverlay, List<string> Properties,
    int? Line, int? Column);

public record SpineRef(string Idref, bool Linear, int? Line, int? Column);

/// <summary>未识别 metadata 原样保留（原始 XML 表示，派生层才做解释）。</summary>
public record PreservedMetadata(string RawXml, string LocalName, string? Namespace);

public record PackageModel(
    string Id, string Path, string RawXmlSha256,
    List<ManifestItem> Manifest, List<SpineRef> Spine,
    List<PreservedMetadata> UnknownMetadata);

public record ImportResult(
    FailureKind Failure,
    List<ZipEntryInfo> Entries,
    string? ContainerXmlRaw,
    string? ContainerXmlSha256,
    List<PackageModel> Packages,
    List<Diagnostic> Diagnostics,
    Dictionary<string, string> EntryBlobs); // container path -> blob sha256

// ---------- 派生计算层 ----------

public record ComputationResult(
    string Id, string Kind, string Status, string InputDigest,
    string ParamVersion, List<string> ParentIds,
    string ResultJson, List<Diagnostic> Diagnostics);
