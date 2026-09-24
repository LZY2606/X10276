using System.Text.Json.Serialization;

namespace EpubSpectrum.Core;

public enum DiagLevel
{
    Error,      // 阻断该节点：输入不完整 / 语义无解 / 损坏
    Warning,    // 不阻断，但需要审阅
    Info
}

public enum DiagCode
{
    ZipCorrupt,
    ContainerMissing,
    ContainerCorrupt,
    PackageMissing,
    PackageCorrupt,
    MultiPackageAmbiguous,
    EntryAmbiguousDuplicate,
    PathEscape,
    FragmentMissing,
    SpineDuplicate,
    SpineItemMissing,
    FallbackCycle,
    FallbackMissing,
    MediaOverlayMissing,
    MediaOverlayCorrupt,
    MediaOverlayTargetMissing,
    MediaOverlayResourceMissing,
    UnknownMetadataPreserved,
    BudgetExhausted,
    SemanticNoSolution
}

/// <summary>带位置的诊断：输入不完整时绝不以空值/默认值伪装成功。</summary>
public sealed record Diag(
    DiagCode Code,
    DiagLevel Level,
    string Message,
    string Location,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AffectedId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Json.Units? Unit = null)
{
    public bool Fatal => Level == DiagLevel.Error;
}

/// <summary>
/// 领域量：派生层所有“带量纲/坐标基准”的值统一外壳。
/// 无法转换的值留在原位（Raw != null, Normalized == null）并附诊断。
/// </summary>
public sealed record DVal(
    string Raw,
    string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Unit,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Basis,
    double? Normalized,
    bool Valid,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null)
{
    public static DVal Scalar(string raw) => new(raw, "scalar", null, null, null, true);
}

public enum ImportOutcome
{
    Success,
    SuccessWithWarnings,
    Corrupt,            // 格式损坏
    SemanticNoSolution, // 语义无解（多义且无法唯一决定等）
    BudgetExhausted     // 资源预算耗尽
}
