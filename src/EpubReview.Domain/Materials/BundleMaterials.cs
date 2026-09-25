using System;
using System.Collections.Generic;

namespace EpubReview.Domain.Materials;

/// <summary>不可变材料：一次容器导入保留下来的全部原始表示。任何规范化都不发生在此层。</summary>
public sealed record BundleMaterials
{
    public required string Label { get; init; }
    public required IReadOnlyList<ZipEntryMaterial> Entries { get; init; }
    public required IReadOnlyList<ContainerRootfileMaterial> Rootfiles { get; init; }
    public required IReadOnlyList<PackageMaterial> Packages { get; init; }
    public required IReadOnlyList<ImportDiagnostic> Diagnostics { get; init; }
    public DateTimeOffset ImportedAt { get; init; }
}

public sealed record ZipEntryMaterial
{
    /// <summary>ZIP 中央目录中逐字节保留的条目名（原始表示，不做大小写/分隔符规范化）。</summary>
    public required string RawName { get; init; }
    public required byte[] Sha256 { get; init; }
    public required long Length { get; init; }
    public required CompressionKind Compression { get; init; }
    public required bool IsDirectory { get; init; }
    public required long LocalHeaderOffset { get; init; }
    public required uint Crc32 { get; init; }
}

public enum CompressionKind { Stored, Deflated, Other }

/// <summary>container.xml 中 rootfile 元素的逐项保留。</summary>
public sealed record ContainerRootfileMaterial
{
    public required string FullPathRaw { get; init; }
    public required string MediaTypeRaw { get; init; }
    public required IReadOnlyList<KeyValuePair<string, string>> OtherAttributes { get; init; }
}

/// <summary>package 文档材料：OPF 字段按原始表示保留，未识别 metadata 原样保留。</summary>
public sealed record PackageMaterial
{
    /// <summary>该 package 文档对应的容器内条目名（规范化定位键，由 rootfile full-path 解析得到）。</summary>
    public required string PackageEntryName { get; init; }
    public required string UniqueIdentifierRaw { get; init; }
    public required string VersionRaw { get; init; }
    public required string PrefixRaw { get; init; }
    public required string LangRaw { get; init; }
    public required string DirRaw { get; init; }
    public required string IdRaw { get; init; }
    public required IReadOnlyList<ManifestItemMaterial> Manifest { get; init; }
    public required IReadOnlyList<SpineItemRefMaterial> Spine { get; init; }
    public required string TocIdRaw { get; init; }
    public required IReadOnlyList<UnknownMetadataMaterial> UnknownMetadata { get; init; }
    public required IReadOnlyList<RawElementMaterial> MetadataOtherElements { get; init; }
}

public sealed record ManifestItemMaterial
{
    public required string IdRaw { get; init; }
    public required string HrefRaw { get; init; }
    public required string MediaTypeRaw { get; init; }
    public required string FallbackIdRaw { get; init; }
    public required string FallbackStyleIdRaw { get; init; }
    public required string MediaOverlayIdRaw { get; init; }
    public required string PropertiesRaw { get; init; }
    public required IReadOnlyList<KeyValuePair<string, string>> OtherAttributes { get; init; }
}

public sealed record SpineItemRefMaterial
{
    public required int Ordinal { get; init; }
    public required string IdRefRaw { get; init; }
    public required string IdRaw { get; init; }
    public required string LinearRaw { get; init; }
    public required string PropertiesRaw { get; init; }
    public required IReadOnlyList<KeyValuePair<string, string>> OtherAttributes { get; init; }
}

/// <summary>未识别 metadata：元素名、命名空间、属性、原始 XML 片段全部保留，供往返检验。</summary>
public sealed record UnknownMetadataMaterial
{
    public required string LocalName { get; init; }
    public required string NamespaceUri { get; init; }
    public required string PrefixRaw { get; init; }
    public required string ValueRaw { get; init; }
    public required IReadOnlyList<KeyValuePair<string, string>> Attributes { get; init; }
    public required string OuterXmlRaw { get; init; }
}

public sealed record RawElementMaterial
{
    public required string LocalName { get; init; }
    public required string NamespaceUri { get; init; }
    public required string OuterXmlRaw { get; init; }
}

/// <summary>输入不完整/损坏诊断：带位置，不得用空值或默认值伪装成功。</summary>
public sealed record ImportDiagnostic
{
    public required DiagnosticSeverity Severity { get; init; }
    public required string Code { get; init; }
    public required string Message { get; init; }
    public required TextPosition Position { get; init; }
}

public enum DiagnosticSeverity { Error, Warning }

public sealed record TextPosition
{
    public required string EntryName { get; init; }
    public int? Line { get; init; }
    public int? Column { get; init; }
    public string? ElementPath { get; init; }

    public static TextPosition Entry(string entry) => new() { EntryName = entry };
    public static TextPosition In(string entry, int line, int col, string? path = null) =>
        new() { EntryName = entry, Line = line, Column = col, ElementPath = path };
}
