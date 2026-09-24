namespace EpubSpectrum.Core.Materials;

/// <summary>
/// 导入与分析参数。每个参数带单位；参数版本化 (ParameterVersion)，分析事件固定该版本，
/// 重放旧事件时使用旧版本，绝不拿新参数覆盖历史结论。
/// </summary>
public sealed record ImportParameters(
    int Version,
    int MaxEntries,            // unit: entry
    long MaxTotalBytes,        // unit: byte
    long MaxEntryBytes,        // unit: byte
    int MaxPackages,           // unit: package
    int MaxContentRefs,        // unit: reference
    int FallbackMaxDepth)      // unit: hop
{
    public const int CurrentVersion = 1;

    public static ImportParameters V1 => new(1, 4096, 256L * 1024 * 1024, 64L * 1024 * 1024, 8, 100_000, 64);

    public ImportParameters WithVersion(int version) => this with { Version = version };
}
