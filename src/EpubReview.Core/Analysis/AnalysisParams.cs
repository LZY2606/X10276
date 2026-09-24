using System.Text.Json;

namespace EpubReview.Core.Analysis;

public sealed record AnalysisParams(
    int Version,
    long MaxUncompressedBytes,
    long MaxEntryCount,
    bool TreatFallbackCycleAsError,
    bool RequireNavDocument,
    bool CaseSensitivePaths,
    string TraversalPolicy,
    string UnknownMetadataPolicy)
{
    public const string TraversalReject = "reject";
    public const string TraversalReportOnly = "report-only";

    public static AnalysisParams V1 { get; } = new(
        1,
        MaxUncompressedBytes: 64L * 1024 * 1024,
        MaxEntryCount: 4096,
        TreatFallbackCycleAsError: true,
        RequireNavDocument: true,
        CaseSensitivePaths: false,
        TraversalPolicy: TraversalReject,
        UnknownMetadataPolicy: "preserve");

    public string ParamsVersionId =>
        "params-v" + Version + "-" + Hashing.StableId(JsonSerializer.Serialize(this,
            new JsonSerializerOptions { WriteIndented = false }));
}

public sealed class ParamsBuilder
{
    private long _maxUncompressedBytes;
    private long _maxEntryCount;
    private bool _treatFallbackCycleAsError;
    private bool _requireNavDocument;
    private bool _caseSensitivePaths;
    private string _traversalPolicy;
    private string _unknownMetadataPolicy;

    public ParamsBuilder(AnalysisParams current)
    {
        _maxUncompressedBytes = current.MaxUncompressedBytes;
        _maxEntryCount = current.MaxEntryCount;
        _treatFallbackCycleAsError = current.TreatFallbackCycleAsError;
        _requireNavDocument = current.RequireNavDocument;
        _caseSensitivePaths = current.CaseSensitivePaths;
        _traversalPolicy = current.TraversalPolicy;
        _unknownMetadataPolicy = current.UnknownMetadataPolicy;
    }

    public ParamsBuilder MaxUncompressedBytes(long v) { _maxUncompressedBytes = v; return this; }
    public ParamsBuilder MaxEntryCount(long v) { _maxEntryCount = v; return this; }
    public ParamsBuilder FallbackCycleAsError(bool v) { _treatFallbackCycleAsError = v; return this; }
    public ParamsBuilder RequireNavDocument(bool v) { _requireNavDocument = v; return this; }
    public ParamsBuilder CaseSensitivePaths(bool v) { _caseSensitivePaths = v; return this; }
    public ParamsBuilder TraversalPolicy(string v) { _traversalPolicy = v; return this; }
    public ParamsBuilder UnknownMetadataPolicy(string v) { _unknownMetadataPolicy = v; return this; }

    public AnalysisParams Build(int version) => new(
        version,
        _maxUncompressedBytes,
        _maxEntryCount,
        _treatFallbackCycleAsError,
        _requireNavDocument,
        _caseSensitivePaths,
        _traversalPolicy,
        _unknownMetadataPolicy);
}
