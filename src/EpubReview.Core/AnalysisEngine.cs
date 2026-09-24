using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace EpubReview.Core;

public sealed class AnalysisEngine
{
    private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace Xhtml = "http://www.w3.org/1999/xhtml";
    private static readonly XNamespace Ncx = "http://www.daisy.org/z3986/2005/ncx/";
    private static readonly XNamespace Smil = "http://www.w3.org/ns/SMIL";

    private readonly ContainerPathResolver paths = new();

    public AnalysisResult Analyze(EpubMaterial material, RuleProfile profile, string? parentAnalysisId = null, string scenarioLabel = "current", string now = "")
    {
        var diagnostics = material.Diagnostics.ToList();
        var entries = material.ZipEntries.ToDictionary(e => e.FullName, StringComparer.Ordinal);
        var resources = new Dictionary<string, ResourceRecord>(StringComparer.Ordinal);
        var references = new List<ResourceReference>();
        var reading = new List<ReadingOrderItem>();
        var navNodes = new List<NavNode>();
        var fallbacks = new List<FallbackCandidate>();
        var parents = new List<string> { material.MaterialId, profile.RuleProfileId };
        if (parentAnalysisId is not null) parents.Add(parentAnalysisId);

        foreach (var package in material.Packages)
        {
            var manifestById = new Dictionary<string, ManifestItemRecord>(StringComparer.Ordinal);
            foreach (var item in package.ManifestItems)
            {
                manifestById[item.Id] = item;
                AddManifestResource(resources, entries, package, item, diagnostics);
            }

            DetectFallbackChains(package, manifestById, resources, fallbacks, diagnostics, profile);
            BuildSpine(package, manifestById, resources, reading, diagnostics, profile);
            BuildMediaOverlays(package, manifestById, resources, references, diagnostics, profile);
            BuildNavigation(package, manifestById, resources, references, navNodes, diagnostics, profile);
        }

        ScanReferencedResources(resources, entries, references, diagnostics, profile);
        var outcome = DetermineOutcome(material, diagnostics);
        var inputDigest = Hashing.StableId("input", $"{material.Sha256}|{profile.RuleProfileId}|{parentAnalysisId ?? "root"}|{scenarioLabel}");
        var analysisId = Hashing.StableId("analysis", $"{inputDigest}|{now}");
        var json = JsonSerializer.Serialize(new { profile.MaxZipEntries, profile.MaxUncompressedBytes, profile.MissingFragmentSeverity, profile.RequireUniqueSpineItems, profile.RejectPathEscape });
        _ = json;

        return new AnalysisResult(
            analysisId,
            material.MaterialId,
            profile.RuleProfileId,
            parentAnalysisId,
            inputDigest,
            profile.ParameterVersion,
            now,
            outcome,
            diagnostics.OrderBy(d => d.Location.EntryPath).ThenBy(d => d.Location.Line).ThenBy(d => d.Code).ToList(),
            resources.Values.OrderBy(r => r.PackageId).ThenBy(r => r.NormalizedPath).ToList(),
            references.OrderBy(r => r.ReferenceId).ToList(),
            reading.OrderBy(r => r.Position).ToList(),
            navNodes.OrderBy(n => n.NavNodeId).ToList(),
            fallbacks.OrderBy(f => f.FallbackPathId).ThenBy(f => f.Hop).ToList(),
            parents,
            false,
            scenarioLabel);
    }

    private static AnalysisOutcome DetermineOutcome(EpubMaterial material, List<Diagnostic> diagnostics)
    {
        if (diagnostics.Any(d => d.Code.StartsWith("BUDGET", StringComparison.Ordinal))) return AnalysisOutcome.ResourceBudgetExhausted;
        if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error && IsFormat(d.Code))) return AnalysisOutcome.FormatCorrupt;
        if (material.Packages.Count == 0 || diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)) return AnalysisOutcome.SemanticallyUnsolvable;
        return AnalysisOutcome.Success;
    }

    private static bool IsFormat(string code) =>
        code is "ZIP.TOO_SHORT" or "ZIP.EOCD_MISSING" or "ZIP.CENTRAL_OFFSET" or "ZIP.CENTRAL_SIGNATURE" or "ZIP.ENTRY_NAME" or "ZIP.ENTRY_LOOKUP" or "XML.PARSE" ||
        code.StartsWith("CONTAINER.MISSING", StringComparison.Ordinal) ||
        code.StartsWith("CONTAINER.ROOTFILE", StringComparison.Ordinal) ||
        code.StartsWith("CONTAINER.NO_ROOTFILE", StringComparison.Ordinal) ||
        code.StartsWith("PACKAGE.MISSING", StringComparison.Ordinal) ||
        code.StartsWith("PACKAGE.REQUIRED", StringComparison.Ordinal) ||
        code.Contains("MANIFEST_MISSING", StringComparison.Ordinal) ||
        code.Contains("SPINE_MISSING", StringComparison.Ordinal);

    private void AddManifestResource(Dictionary<string, ResourceRecord> resources, Dictionary<string, ZipEntryRecord> entries, RawPackageDocument package, ManifestItemRecord item, List<Diagnostic> diagnostics)
    {
        var resolved = paths.Resolve(package.EntryPath, item.RawHref);
        var path = resolved.NormalizedPath ?? string.Empty;
        if (resolved.EscapesContainer)
        {
            diagnostics.Add(new Diagnostic("PATH.ESCAPE", DiagnosticSeverity.Error, $"Href '{item.RawHref}' resolves outside the EPUB container.", item.Location, item.Id, "PATH_NORMALIZATION_NO_ESCAPE"));
            return;
        }

        var exists = entries.TryGetValue(path, out var entry);
        var bytes = entry?.Bytes ?? [];
        var anchors = exists ? EpubParser.ExtractAnchors(bytes, item.MediaType) : [];
        var id = Hashing.StableId("resource", $"{package.PackageId}|{item.Id}|{path}|{item.MediaType}");
        if (!exists) diagnostics.Add(new Diagnostic("RESOURCE.MISSING", DiagnosticSeverity.Error, $"Manifest item '{item.Id}' points to missing path '{path}'.", item.Location, id, "MISSING_RESOURCE_SEPARATE"));
        resources[id] = new ResourceRecord(id, package.PackageId, item.Id, item.RawHref, path, resolved.Fragment, item.MediaType, true, exists, entry?.LengthBytes ?? 0, exists ? Hashing.Sha256Hex(bytes) : null, bytes, anchors, item.Location, Quantity.Bytes(entry?.LengthBytes ?? 0, $"manifest item:{item.Id}"));
    }

    private void DetectFallbackChains(RawPackageDocument package, Dictionary<string, ManifestItemRecord> manifest, Dictionary<string, ResourceRecord> resources, List<FallbackCandidate> fallbacks, List<Diagnostic> diagnostics, RuleProfile profile)
    {
        foreach (var item in package.ManifestItems.Where(i => !string.IsNullOrWhiteSpace(i.FallbackId)))
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var current = item;
            var hop = 0;
            while (true)
            {
                if (!manifest.TryGetValue(current.FallbackId!, out var next))
                {
                    diagnostics.Add(new Diagnostic("FALLBACK.MISSING_TARGET", DiagnosticSeverity.Error, $"Fallback target '{current.FallbackId}' is absent from the manifest.", current.Location, current.Id, "MISSING_RESOURCE_SEPARATE"));
                    break;
                }
                hop++;
                var nextResource = resources.Values.FirstOrDefault(r => r.PackageId == package.PackageId && r.ManifestId == next.Id);
                var legal = nextResource is not null && nextResource.ExistsInZip;
                fallbacks.Add(new FallbackCandidate(Hashing.StableId("fallback-path", $"{package.PackageId}|{item.Id}"), item.Id, next.Id, hop, next.RawHref, next.MediaType, legal, legal ? null : "FALLBACK.MISSING_TARGET", next.Location));
                if (!seen.Add(next.Id))
                {
                    diagnostics.Add(new Diagnostic("FALLBACK.CYCLE", DiagnosticSeverity.Error, $"Fallback chain from '{item.Id}' returns to '{next.Id}' and is not an ordering chain.", current.Location, item.Id, "FALLBACK_CYCLE_SEPARATE_FROM_MISSING"));
                    break;
                }
                if (string.IsNullOrWhiteSpace(next.FallbackId)) break;
                current = next;
            }
        }
    }

    private void BuildSpine(RawPackageDocument package, Dictionary<string, ManifestItemRecord> manifest, Dictionary<string, ResourceRecord> resources, List<ReadingOrderItem> reading, List<Diagnostic> diagnostics, RuleProfile profile)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var spineRef in package.SpineRefs)
        {
            if (!manifest.TryGetValue(spineRef.IdRef, out var item))
            {
                diagnostics.Add(new Diagnostic("SPINE.IDREF_MISSING", DiagnosticSeverity.Error, $"spine idref '{spineRef.IdRef}' is not declared in manifest.", spineRef.Location, spineRef.IdRef, "SPINE_PRIMARY_ORDER_ONLY"));
                continue;
            }
            if (profile.RequireUniqueSpineItems && !seen.Add(spineRef.IdRef))
                diagnostics.Add(new Diagnostic("SPINE.DUPLICATE", DiagnosticSeverity.Error, $"spine idref '{spineRef.IdRef}' appears more than once in the primary order.", spineRef.Location, spineRef.IdRef, "SPINE_DUPLICATE_REPORTED"));
            var resource = resources.Values.FirstOrDefault(r => r.PackageId == package.PackageId && r.ManifestId == item.Id);
            if (resource is null) continue;
            var position = reading.Count(r => r.PackageId == package.PackageId) + 1;
            reading.Add(new ReadingOrderItem(Hashing.StableId("reading", $"{package.PackageId}|{item.Id}|{spineRef.Occurrence}"), position, package.PackageId, item.Id, resource.ResourceId, spineRef.LinearRaw, Quantity.Ordinal(position, "spine itemref occurrence; primary linear order only"), spineRef.Location));
        }
    }

    private void BuildMediaOverlays(RawPackageDocument package, Dictionary<string, ManifestItemRecord> manifest, Dictionary<string, ResourceRecord> resources, List<ResourceReference> references, List<Diagnostic> diagnostics, RuleProfile profile)
    {
        foreach (var item in package.ManifestItems.Where(i => !string.IsNullOrWhiteSpace(i.MediaOverlayId)))
        {
            var ownerResource = resources.Values.FirstOrDefault(r => r.PackageId == package.PackageId && r.ManifestId == item.Id);
            if (ownerResource is null) continue;
            if (!manifest.TryGetValue(item.MediaOverlayId!, out var overlayItem))
            {
                diagnostics.Add(new Diagnostic("MEDIA_OVERLAY.MISSING", DiagnosticSeverity.Error, $"media-overlay '{item.MediaOverlayId}' for '{item.Id}' is absent from manifest.", item.Location, item.Id, "MEDIA_OVERLAY_BROKEN_LINK"));
                continue;
            }
            var overlayResource = resources.Values.FirstOrDefault(r => r.PackageId == package.PackageId && r.ManifestId == overlayItem.Id);
            if (overlayResource is null || !overlayResource.ExistsInZip)
            {
                diagnostics.Add(new Diagnostic("MEDIA_OVERLAY.MISSING", DiagnosticSeverity.Error, $"media-overlay document '{overlayItem.Id}' cannot be read.", overlayItem.Location, item.Id, "MEDIA_OVERLAY_BROKEN_LINK"));
                continue;
            }
            references.Add(MakeReference(package, ownerResource, overlayResource, "media-overlay", overlayItem.RawHref, overlayItem.Location, null));
            AddSmilReferences(package, ownerResource, overlayResource, manifest, resources, references, diagnostics, profile);
        }
    }

    private void AddSmilReferences(RawPackageDocument package, ResourceRecord owner, ResourceRecord overlay, Dictionary<string, ManifestItemRecord> manifest, Dictionary<string, ResourceRecord> resources, List<ResourceReference> references, List<Diagnostic> diagnostics, RuleProfile profile)
    {
        if (!EpubParser.TryLoad(System.Text.Encoding.UTF8.GetString(overlay.RawBytes), overlay.NormalizedPath, diagnostics, out var doc)) return;
        foreach (var media in doc.Descendants().Where(e => e.Name.LocalName is "audio" or "text"))
        {
        var attr = media.Name.LocalName == "audio" ? "src" : "src";
        var raw = (string?)media.Attribute(attr);
        if (string.IsNullOrWhiteSpace(raw))
        {
            diagnostics.Add(new Diagnostic("MEDIA_OVERLAY.SRC_MISSING", DiagnosticSeverity.Error, "SMIL media/text element requires src.", EpubParser.Location(media, overlay.NormalizedPath), overlay.ResourceId, "MEDIA_OVERLAY_BROKEN_LINK"));
            continue;
        }

        var resolved = paths.Resolve(overlay.NormalizedPath, raw);
        var target = resolved.NormalizedPath is null ? null : resources.Values.FirstOrDefault(r => r.PackageId == package.PackageId && r.NormalizedPath == resolved.NormalizedPath);
        var timing = ParseTiming((string?)media.Attribute("clipBegin") ?? "0s", (string?)media.Attribute("clipEnd") ?? "");
        var reference = MakeReference(package, overlay, target, media.Name.LocalName == "audio" ? "smil-audio" : "smil-text", raw, EpubParser.Location(media, overlay.NormalizedPath), timing);
        references.Add(reference);
        if (target is null || !target.ExistsInZip)
            diagnostics.Add(new Diagnostic("MEDIA_OVERLAY.TARGET_MISSING", DiagnosticSeverity.Error, $"SMIL target '{raw}' is not a readable container resource.", EpubParser.Location(media, overlay.NormalizedPath), reference.ReferenceId, "MEDIA_OVERLAY_BROKEN_LINK"));
        }
    }

    private void BuildNavigation(RawPackageDocument package, Dictionary<string, ManifestItemRecord> manifest, Dictionary<string, ResourceRecord> resources, List<ResourceReference> references, List<NavNode> navNodes, List<Diagnostic> diagnostics, RuleProfile profile)
    {
        var navItems = package.ManifestItems.Where(i => i.PropertiesRaw.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("nav")).ToList();
        foreach (var navItem in navItems)
        {
            var navResource = resources.Values.FirstOrDefault(r => r.PackageId == package.PackageId && r.ManifestId == navItem.Id);
            if (navResource is null || !navResource.ExistsInZip) continue;
            if (!EpubParser.TryLoad(System.Text.Encoding.UTF8.GetString(navResource.RawBytes), navResource.NormalizedPath, diagnostics, out var doc)) continue;
            foreach (var anchor in doc.Descendants(Xhtml + "a"))
            {
                var rawHref = (string?)anchor.Attribute("href");
                if (string.IsNullOrWhiteSpace(rawHref)) continue;
                var resolved = paths.Resolve(navResource.NormalizedPath, rawHref);
                var target = resolved.NormalizedPath is null ? null : resources.Values.FirstOrDefault(r => r.PackageId == package.PackageId && r.NormalizedPath == resolved.NormalizedPath);
                var reference = MakeReference(package, navResource, target, "navigation", rawHref, EpubParser.Location(anchor, navResource.NormalizedPath), null);
                references.Add(reference);
                AddFragmentDiagnostic(reference, target, resolved.Fragment, diagnostics, profile, anchor, navResource.NormalizedPath);
                var li = anchor.Ancestors(Xhtml + "li").FirstOrDefault();
                var depth = li?.Ancestors(Xhtml + "li").Count() ?? 0;
                var sibling = li?.ElementsBeforeSelf(Xhtml + "li").Count() ?? 0;
                navNodes.Add(new NavNode(Hashing.StableId("nav", $"{navResource.ResourceId}|{rawHref}|{depth}|{sibling}"), navResource.ResourceId, null, depth, sibling, anchor.Value.Trim(), reference.ReferenceId, EpubParser.Location(anchor, navResource.NormalizedPath)));
            }
        }
    }

    private void ScanReferencedResources(Dictionary<string, ResourceRecord> resources, Dictionary<string, ZipEntryRecord> entries, List<ResourceReference> references, List<Diagnostic> diagnostics, RuleProfile profile)
    {
        foreach (var resource in resources.Values.Where(r => r.ExistsInZip && r.MediaType == "application/xhtml+xml").ToList())
        {
            if (!EpubParser.TryLoad(System.Text.Encoding.UTF8.GetString(resource.RawBytes), resource.NormalizedPath, diagnostics, out var doc)) continue;
            foreach (var link in EpubParser.ExtractXhtmlLinks(doc, resource.NormalizedPath))
            {
                var resolved = paths.Resolve(resource.NormalizedPath, link.Target);
                if (resolved.EscapesContainer)
                {
                    diagnostics.Add(new Diagnostic("PATH.ESCAPE", DiagnosticSeverity.Error, $"Reference '{link.Target}' resolves outside the container.", link.Location, resource.ResourceId, "PATH_NORMALIZATION_NO_ESCAPE"));
                    continue;
                }
                var target = resolved.NormalizedPath is null ? null : resources.Values.FirstOrDefault(r => r.PackageId == resource.PackageId && r.NormalizedPath == resolved.NormalizedPath);
                var reference = MakeReference(null, resource, target, link.Relation, link.Target, link.Location, null);
                references.Add(reference);
                AddFragmentDiagnostic(reference, target, resolved.Fragment, diagnostics, profile, null, resource.NormalizedPath);
                if (target is null || !target.ExistsInZip)
                    diagnostics.Add(new Diagnostic("RESOURCE.REFERENCE_MISSING", DiagnosticSeverity.Error, $"Reference '{link.Target}' does not resolve to a packaged resource.", link.Location, reference.ReferenceId, "SPINE_PRIMARY_ORDER_ONLY"));
            }
        }
    }

    private void AddFragmentDiagnostic(ResourceReference reference, ResourceRecord? target, string? fragment, List<Diagnostic> diagnostics, RuleProfile profile, XElement? element, string entry)
    {
        if (string.IsNullOrEmpty(fragment) || target is null || !target.ExistsInZip) return;
        if (!target.AnchorIds.Contains(fragment))
            diagnostics.Add(new Diagnostic("FRAGMENT.MISSING", profile.MissingFragmentSeverity, $"Anchor '#{fragment}' is not present in '{target.NormalizedPath}'.", element is null ? reference.Location : EpubParser.Location(element, entry), reference.ReferenceId, "FRAGMENT_INTEGRITY"));
    }

    private ResourceReference MakeReference(RawPackageDocument? package, ResourceRecord source, ResourceRecord? target, string relation, string rawTarget, SourceLocation location, Timing? timing)
    {
        var resolved = paths.Resolve(source.NormalizedPath, rawTarget);
        return new ResourceReference(
            Hashing.StableId("ref", $"{source.ResourceId}|{relation}|{rawTarget}|{location.Line}|{location.Column}"),
            source.ResourceId,
            target?.ResourceId,
            relation,
            rawTarget,
            resolved.NormalizedPath,
            resolved.Fragment,
            target?.ExistsInZip ?? false,
            target is not null && (string.IsNullOrEmpty(resolved.Fragment) || target.AnchorIds.Contains(resolved.Fragment)),
            location,
            timing);
    }

    private static Timing ParseTiming(string beginRaw, string endRaw)
    {
        var begin = ParseSeconds(beginRaw, out var beginError);
        var end = ParseSeconds(endRaw, out var endError);
        return new Timing(beginRaw, endRaw, begin?.ToString(System.Globalization.CultureInfo.InvariantCulture), end?.ToString(System.Globalization.CultureInfo.InvariantCulture), "seconds", "SMIL clip time; relative to referenced media timeline", begin is null || end is null ? "not effective" : $"[{begin},{end})", beginError ?? endError);
    }

    private static double? ParseSeconds(string raw, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(raw, @"^\s*(\d+(?:\.\d+)?)(ms|s|min|h)?\s*$");
        if (!match.Success) { error = $"TIME.UNPARSEABLE:{raw}"; return null; }
        var value = double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var unit = string.IsNullOrEmpty(match.Groups[2].Value) ? "s" : match.Groups[2].Value;
        return unit switch { "ms" => value / 1000d, "s" => value, "min" => value * 60d, "h" => value * 3600d, _ => null };
    }
}
