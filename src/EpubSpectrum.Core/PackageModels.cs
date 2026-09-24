using System.Text.Json;
using System.Text.Json.Serialization;

namespace EpubSpectrum.Core;

// ---------- 不可变材料层：解析结果全部保留原始表示 ----------

public sealed record ZipEntryRec(
    string Name,
    long CompressedLength,
    long Length,
    uint Crc32,
    string Sha256,
    byte[] Bytes);

public sealed record OpfRef(string FullPath, string? MediaType, string? ExistsEntryId, bool Chosen);

public sealed record ContainerDoc(
    string EntryName, string RawXml, string? Version,
    IReadOnlyList<OpfRef> Packages, IReadOnlyList<Diag> Diags);

public sealed record ManifestItem(
    string Id, string Href, string FullPath, string MediaType,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FallbackId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MediaOverlayId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Properties,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EntryId,
    bool Exists);

public sealed record SpineItem(
    string ItemId, int Index, bool Linear,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EntryId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FullPath);

public sealed record LinkRec(
    string Key, string SourceEntryId, string SourceFullPath,
    string RawHref, string TargetFullPath, string? Fragment,
    string Kind, bool TargetExists, bool FragmentExists,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TargetEntryId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LineHint);

public sealed record NavNode(
    string Key, int Depth, int Ordinal, string? RawHref,
    string? TargetFullPath, string? Fragment, string Text,
    string ParentKey, bool TargetExists, bool FragmentExists);

public sealed record OverlaySeq(
    string Id, string TextSrc, string? TextFragment, string AudioSrc,
    DVal ClipBegin, DVal ClipEnd,
    bool TextTargetExists, bool FragmentExists, bool AudioExists,
    string? AudioEntryId);

public sealed record OverlayDoc(
    string ItemId, string EntryName, string RawSmil,
    IReadOnlyList<OverlaySeq> Sequences, IReadOnlyList<Diag> Diags);

/// <summary>未识别 metadata 整体保留：原始 XML + 命名空间 + 属性。</summary>
public sealed record UnknownMeta(
    string Key, string ElementName, string? Namespace,
    string RawOuterXml, IReadOnlyDictionary<string, string> Attributes);

public sealed record PackageDoc(
    string FullPath, string EntryName, string RawXml,
    string? UniqueIdentifier, string? Version, string? Lang,
    IReadOnlyList<ManifestItem> Manifest,
    IReadOnlyList<SpineItem> Spine,
    IReadOnlyDictionary<string, string> TocNcxOrNav,
    IReadOnlyList<UnknownMeta> UnknownMetadata,
    IReadOnlyList<Diag> Diags);

public sealed record ImportedEpub(
    string ImportId,
    ImportOutcome Outcome,
    string RawByteHash,
    long RawByteLength,
    IReadOnlyList<ZipEntryRec> Entries,
    ContainerDoc? Container,
    IReadOnlyList<PackageDoc> Packages,
    IReadOnlyList<OverlayDoc> Overlays,
    IReadOnlyList<LinkRec> Links,
    IReadOnlyList<NavNode> NavTree,
    IReadOnlyList<Diag> Diags)
{
    public JsonElement ToJson() =>
        JsonSerializer.SerializeToElement(this, ModelJson.Options);
}
