using System.Globalization;
using System.Text;

namespace EpubSpectrum.Core.Materials;

// ============================================================================
// 不可变材料层：原始表示一律保留，派生信息另放，规范化不覆盖原文。
// ============================================================================

/// <summary>来源位置与坐标基准。行列从 1 开始；Offset 为字节偏移（单位 byte）。</summary>
public sealed record SourceLocation(string SourceId, int? Line = null, int? Column = null, long? Offset = null)
{
    public string Format()
    {
        var sb = new StringBuilder(SourceId);
        if (Line is not null) sb.Append(CultureInfo.InvariantCulture).Append(':').Append(Line);
        if (Column is not null) sb.Append(',').Append(Column);
        if (Offset is not null) sb.Append(" (offset=").Append(Offset.Value.ToString(CultureInfo.InvariantCulture)).Append(" byte)");
        return sb.ToString();
    }
}

public sealed record RawFragment(string RawText, SourceLocation Location);

/// <summary>ZIP 条目：字节、原始名、编码标志与时间基准全部保留。时间为 DOS 时间，基准 1980-01-01。</summary>
public sealed class ZipEntryData
{
    public required string RawName { get; init; }
    public required byte[] Bytes { get; init; }
    public required int OrderIndex { get; init; }
    public required bool Utf8Flag { get; init; }
    public required uint Crc32 { get; init; }
    public required long CompressedSize { get; init; }
    public required ushort DosDate { get; init; }
    public required ushort DosTime { get; init; }
    public required ushort CompressionMethod { get; init; }
    public DateTimeOffset ModifiedUtc { get; init; }
}

public sealed class ContainerXml
{
    public required byte[] RawBytes { get; init; }
    public required string RawName { get; init; } = "META-INF/container.xml";
    public required IReadOnlyList<RootFileRef> RootFiles { get; init; }
}

public sealed class RootFileRef
{
    public required string FullPathRaw { get; init; }
    public required string MediaTypeRaw { get; init; }
    public required SourceLocation Location { get; init; }
    /// <summary>规范化后的 OCF 相对路径（仅派生层使用；原文在 FullPathRaw）。</summary>
    public string? NormalizedPath { get; set; }
}

public sealed class PackageDocument
{
    public required string PackageId { get; init; }
    public required string RawEntryName { get; init; }
    public required byte[] RawBytes { get; init; }
    public required string? UniqueIdentifierRaw { get; init; }
    public required string? VersionRaw { get; init; }
    public required string? Xmllang { get; init; }
    public required IReadOnlyList<ManifestItem> Manifest { get; init; }
    public required Spine Spine { get; init; }
    public required IReadOnlyList<MetadataEntry> UnknownMetadata { get; init; }
    public required IReadOnlyList<RawFragment> MetadataRawFragments { get; init; }
}

public sealed class ManifestItem
{
    public required string Id { get; init; }
    public required string HrefRaw { get; init; }
    public required string MediaTypeRaw { get; init; }
    public required string? FallbackIdRaw { get; init; }
    public required string? MediaOverlayIdRaw { get; init; }
    public required string? PropertiesRaw { get; init; }
    public required SourceLocation Location { get; init; }
    public string? HrefNormalized { get; set; }
}

public sealed class SpineItemRef
{
    public required int Ordinal { get; init; }
    public required string IdRefRaw { get; init; }
    public required string? LinearRaw { get; init; }
    public required string? IdAttribute { get; init; }
    public required SourceLocation Location { get; init; }
}

public sealed class Spine
{
    public required string? TocIdRaw { get; init; }
    public required IReadOnlyList<SpineItemRef> ItemRefs { get; init; }
    public required SourceLocation Location { get; init; }
}

/// <summary>未识别 metadata：原 XML 片段、命名空间、位置都保留，派生层不丢弃。</summary>
public sealed class MetadataEntry
{
    public required string LocalName { get; init; }
    public required string NamespaceUri { get; init; }
    public required string RawXml { get; init; }
    public required SourceLocation Location { get; init; }
}

/// <summary>原始内容引用：href 原文、片段原文、属性名与位置全部保留。</summary>
public sealed class ContentRef
{
    public required string SourceEntryRawName { get; init; }
    public required string SourceKind { get; init; }   // xhtml-link, xhtml-a, svg-image, smil-text, smil-audio, css-url, manifest-item
    public required string HrefRaw { get; init; }
    public required string? FragmentRaw { get; init; }
    public required string? AttributeName { get; init; }
    public required SourceLocation Location { get; init; }
    public string? HrefNormalized { get; set; }
}

public sealed class ResourceMaterial
{
    public required string EntryRawName { get; init; }
    public required byte[] Bytes { get; init; }
    public required string? MediaTypeDeclaredRaw { get; init; }
    public required long SizeBytes { get; init; }
}

public sealed class ImportedEpub
{
    public required Guid CorrelationId { get; init; }
    public required byte[] OriginalZipBytes { get; init; }
    public required byte[] MimetypeBytes { get; init; }
    public required bool MimetypeFirst { get; init; }
    public required bool MimetypeStored { get; init; }
    public required IReadOnlyList<ZipEntryData> Entries { get; init; }
    public required ContainerXml Container { get; init; }
    public required IReadOnlyList<PackageDocument> Packages { get; init; }
    public required IReadOnlyList<ResourceMaterial> Resources { get; init; }
    public required IReadOnlyList<ContentRef> ContentRefs { get; init; }
    public required string Sha256 { get; init; }
}
