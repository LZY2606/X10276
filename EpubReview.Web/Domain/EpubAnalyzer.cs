using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace EpubReview.Domain;

public sealed class EpubAnalyzer
{
    private const string ContainerNs = "urn:oasis:names:tc:opendocument:xmlns:container";
    private const string OpfNs = "http://www.idpf.org/2007/opf";
    private const string DcNs = "http://purl.org/dc/elements/1.1/";
    private const string XhtmlNs = "http://www.w3.org/1999/xhtml";
    private const string SmilNs = "http://www.w3.org/ns/SMIL";

    public AnalysisResult Analyze(AnalysisRequest request)
    {
        var now = SystemClock.UtcNow();
        var bytes = request.ArchiveBytes;
        var inputHash = StableHashes.Sha256(bytes);
        var archiveId = StableHashes.StableId("material:archive", $"archive|{inputHash}");
        var decisions = request.Decisions ?? [];
        var diagnostics = new List<Diagnostic>();
        var judgments = new List<JudgmentBasis>();
        var p = request.Parameters;
        var entries = new List<EntryMaterial>();
        var packages = new List<PackageInfo>();
        var resources = new List<ResourceNode>();
        var references = new List<ReferenceEdge>();
        var spine = new List<SpineEntry>();
        var navigation = new List<NavigationNode>();
        var fallback = new List<FallbackStep>();
        var overlays = new List<OverlayLink>();

        if (bytes.Length > p.MaxArchiveBytes)
        {
            diagnostics.Add(new("BUDGET_ARCHIVE_BYTES", DiagnosticSeverity.Error,
                $"Archive is {bytes.Length} {p.ByteUnit}; budget is {p.MaxArchiveBytes} {p.ByteUnit}.",
                "input:/archive", null, null, archiveId, AnalyzeOutcome.BudgetExhausted));
            return Empty(archiveId, inputHash, bytes, p, request.ParentDependencyVersion, now, AnalyzeOutcome.BudgetExhausted, diagnostics, decisions);
        }

        ZipArchive zip;
        try
        {
            using var input = new MemoryStream(bytes, writable: false);
            zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false, Encoding.UTF8);
            if (zip.Entries.Count > p.MaxZipEntries)
            {
                diagnostics.Add(new("BUDGET_ENTRY_COUNT", DiagnosticSeverity.Error,
                    $"ZIP has {zip.Entries.Count} entries; budget is {p.MaxZipEntries}.", "input:/archive/entries", null, null, archiveId, AnalyzeOutcome.BudgetExhausted));
                return Empty(archiveId, inputHash, bytes, p, request.ParentDependencyVersion, now, AnalyzeOutcome.BudgetExhausted, diagnostics, decisions);
            }
            var index = 0;
            foreach (var entry in zip.Entries.OrderBy(e => e.FullName, StringComparer.Ordinal))
            {
                var raw = entry.FullName;
                var norm = PathRules.NormalizeEntryName(raw);
                byte[] entryBytes;
                string? invalid = null;
                try
                {
                    using var source = entry.Open();
                    using var target = new MemoryStream();
                    source.CopyTo(target);
                    entryBytes = target.ToArray();
                }
                catch (InvalidDataException ex) { entryBytes = []; invalid = ex.Message; }
                var entryId = StableHashes.StableId("material:entry", $"entry|{raw}|{index}");
                entries.Add(new(entryId, index, raw, string.IsNullOrEmpty(norm) ? null : norm, entryBytes.Length, StableHashes.Sha256(entryBytes), GuessMediaType(norm), invalid, entryBytes));
                if (invalid is not null)
                    diagnostics.Add(new("FORMAT_ZIP_ENTRY", DiagnosticSeverity.Error, invalid, $"zip:/{raw}", null, null, entryId, AnalyzeOutcome.FormatCorrupt));
                index++;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException)
        {
            diagnostics.Add(new("FORMAT_ZIP_CONTAINER", DiagnosticSeverity.Error, ex.Message, "input:/archive", null, null, archiveId, AnalyzeOutcome.FormatCorrupt));
            return Empty(archiveId, inputHash, bytes, p, request.ParentDependencyVersion, now, AnalyzeOutcome.FormatCorrupt, diagnostics, decisions);
        }

        var byNorm = entries
            .Where(e => e.NormalizedPath is not null)
            .GroupBy(e => e.NormalizedPath!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var mimetype = entries.FirstOrDefault(e => e.NormalizedPath == "mimetype");
        if (mimetype is null || mimetype.Bytes.Length == 0)
            diagnostics.Add(new("FORMAT_MISSING_MIMETYPE", DiagnosticSeverity.Error, "OCF mimetype entry is missing or empty.", "zip:/mimetype", null, null, archiveId, AnalyzeOutcome.FormatCorrupt));

        var container = entries.FirstOrDefault(e => e.NormalizedPath == "META-INF/container.xml");
        if (container is null)
            diagnostics.Add(new("FORMAT_MISSING_CONTAINER", DiagnosticSeverity.Error, "META-INF/container.xml is missing.", "zip:/META-INF/container.xml", null, null, archiveId, AnalyzeOutcome.FormatCorrupt));
        else
        {
            XDocument? doc = null;
            try { doc = ParseXml(container.Bytes); }
            catch (XmlException ex)
            {
                diagnostics.Add(new("FORMAT_CONTAINER_XML", DiagnosticSeverity.Error, ex.Message, "zip:/META-INF/container.xml", ex.LineNumber, ex.LinePosition, container.MaterialId, AnalyzeOutcome.FormatCorrupt));
            }
            if (doc is not null)
            {
                var roots = doc.Descendants(XName.Get("rootfile", ContainerNs)).ToList();
                if (roots.Count == 0)
                    diagnostics.Add(new("FORMAT_NO_ROOTFILE", DiagnosticSeverity.Error, "container.xml declares no rootfile.", "META-INF/container.xml", null, null, container.MaterialId, AnalyzeOutcome.FormatCorrupt));
                foreach (var root in roots)
                {
                    var full = root.Attribute("full-path")?.Value;
                    var media = root.Attribute("media-type")?.Value;
                    if (string.IsNullOrWhiteSpace(full))
                    {
                        diagnostics.Add(new("FORMAT_ROOTFILE_PATH", DiagnosticSeverity.Error, "rootfile has no full-path.", "/ocf:container/ocf:rootfiles/ocf:rootfile", null, null, container.MaterialId, AnalyzeOutcome.FormatCorrupt));
                        continue;
                    }
                    var resolved = PathRules.Resolve("", full);
                    if (resolved.Escapes || resolved.Path is null)
                        diagnostics.Add(new("PATH_ROOTFILE_ESCAPE", DiagnosticSeverity.Error, resolved.Error ?? "Rootfile escapes container.", $"zip:/META-INF/container.xml#{full}", null, null, container.MaterialId, AnalyzeOutcome.FormatCorrupt));
                    var pkgMaterial = resolved.Path is null ? null : FindCandidates(byNorm, resolved.Path).FirstOrDefault();
                    if (pkgMaterial is null)
                        diagnostics.Add(new("MISSING_PACKAGE_DOCUMENT", DiagnosticSeverity.Error, $"Declared package '{full}' is not present in the ZIP.", "META-INF/container.xml", null, null, container.MaterialId, AnalyzeOutcome.FormatCorrupt));
                    else ParsePackage(pkgMaterial, media, entries, byNorm, packages, resources, references, spine, navigation, fallback, overlays, diagnostics, p);
                }
            }
        }

        var outcome = DiagnoseOutcome(diagnostics, decisions, p);
        AddJudgments(judgments, references, fallback, resources, diagnostics, outcome);
        var dependencyVersion = StableHashes.ShortHash(string.Join('\n', new[]
        {
            inputHash, p.Version, request.ParentDependencyVersion ?? "",
            string.Join(';', decisions.OrderBy(d => d.EventId).Select(d => $"{d.EventId}:{d.SubjectId}:{d.Action}")),
            string.Join(';', resources.Select(r => r.Id)), string.Join(';', references.Select(r => $"{r.Id}:{r.Status}")),
            string.Join(';', spine.Select(s => s.Id)), string.Join(';', navigation.Select(n => n.Id)), string.Join(';', fallback.Select(f => f.Id))
        }));
        return new(archiveId, inputHash, p.ByteUnit, bytes.Length, p.Version, request.ParentDependencyVersion, dependencyVersion, now, outcome,
            diagnostics.DistinctBy(d => $"{d.Code}|{d.Location}|{d.SubjectId}").OrderBy(d => d.Code).ThenBy(d => d.Location).ToList(),
            entries, packages,
            resources.OrderBy(r => r.Id).ToList(), references.OrderBy(r => r.Id).ToList(),
            spine.OrderBy(s => s.PackageId).ThenBy(s => s.Order).ToList(), navigation.OrderBy(n => n.Id).ToList(),
            fallback.OrderBy(f => f.Id).ToList(), overlays.OrderBy(o => o.Id).ToList(), judgments.OrderBy(j => j.Code).ThenBy(j => j.MachineRule).ToList(),
            decisions.OrderBy(d => d.EventId).ToList(), p.CoordinateSpace, p.TimeUnit, p.ByteUnit);
    }

    private void ParsePackage(
        EntryMaterial packageMaterial, string? rootMediaType,
        List<EntryMaterial> entries, IReadOnlyDictionary<string, List<EntryMaterial>> byNorm,
        List<PackageInfo> packages, List<ResourceNode> resources, List<ReferenceEdge> references,
        List<SpineEntry> spine, List<NavigationNode> navigation, List<FallbackStep> fallback,
        List<OverlayLink> overlays, List<Diagnostic> diagnostics, AnalysisParameters p)
    {
        XDocument doc;
        try { doc = ParseXml(packageMaterial.Bytes); }
        catch (XmlException ex)
        {
            diagnostics.Add(new("FORMAT_PACKAGE_XML", DiagnosticSeverity.Error, ex.Message, $"zip:/{packageMaterial.RawPath}", ex.LineNumber, ex.LinePosition, packageMaterial.MaterialId, AnalyzeOutcome.FormatCorrupt));
            return;
        }

        var packageElement = doc.Root;
        var packageId = StableHashes.StableId("package", packageMaterial.RawPath);
        var packageDirectory = packageMaterial.NormalizedPath is null ? "" : Path.GetDirectoryName(packageMaterial.NormalizedPath)?.Replace('\\', '/') ?? "";
        var unknown = new List<UnknownMetadata>();
        if (packageElement is not null)
        {
            foreach (var element in packageElement.Elements()
                .Where(e => e.Name.NamespaceName is not (OpfNs or DcNs or "") || e.Name.LocalName is "unknown-evidence").Select((e, i) => (e, i)))
            {
                var info = element.e as IXmlLineInfo;
                unknown.Add(new(StableHashes.StableId("metadata", $"{packageId}|{element.i}|{element.e.Name}"), packageId, info?.LineNumber, info?.LinePosition, element.e.Name.NamespaceName, element.e.Name.LocalName, element.e.ToString()));
                diagnostics.Add(new("UNKNOWN_METADATA_PRESERVED", DiagnosticSeverity.Info, $"Unrecognized metadata <{element.e.Name.LocalName}> is preserved without interpretation.", $"zip:/{packageMaterial.RawPath}", info?.LineNumber, info?.LinePosition, packageId, null));
            }
        }

        var manifestItems = new Dictionary<string, XElement>(StringComparer.Ordinal);
        var orderedManifest = doc.Descendants(XName.Get("item", OpfNs)).ToList();
        foreach (var item in orderedManifest)
        {
            var id = item.Attribute("id")?.Value;
            var href = item.Attribute("href")?.Value;
            if (string.IsNullOrWhiteSpace(id))
            {
                var line = (item as IXmlLineInfo)?.LineNumber;
                var pos = (item as IXmlLineInfo)?.LinePosition;
                diagnostics.Add(new("FORMAT_MANIFEST_ID", DiagnosticSeverity.Error, "Manifest item has no id.", $"zip:/{packageMaterial.RawPath}", line, pos, packageId, AnalyzeOutcome.FormatCorrupt));
                continue;
            }
            if (manifestItems.ContainsKey(id))
            {
                diagnostics.Add(new("FORMAT_DUPLICATE_MANIFEST_ID", DiagnosticSeverity.Error, $"Manifest id '{id}' occurs more than once.", $"zip:/{packageMaterial.RawPath}#manifest/{id}", null, null, packageId, AnalyzeOutcome.FormatCorrupt));
                continue;
            }
            manifestItems[id] = item;
            if (string.IsNullOrWhiteSpace(href))
            {
                diagnostics.Add(new("FORMAT_MANIFEST_HREF", DiagnosticSeverity.Error, $"Manifest item '{id}' has no href.", $"zip:/{packageMaterial.RawPath}#manifest/{id}", null, null, packageId, AnalyzeOutcome.FormatCorrupt));
                continue;
            }
            var resolved = PathRules.Resolve(packageDirectory, href);
            var candidates = resolved.Path is null ? [] : FindCandidates(byNorm, resolved.Path);
            var material = candidates.FirstOrDefault();
            var status = LinkStatus.Ok;
            string? code = null;
            if (resolved.Escapes) { status = LinkStatus.PathEscapesContainer; code = "PATH_MANIFEST_ESCAPE"; }
            else if (candidates.Count == 0) { status = LinkStatus.MissingResource; code = "MISSING_MANIFEST_RESOURCE"; }
            else if (candidates.Count > 1) { status = LinkStatus.AmbiguousResource; code = "AMBIGUOUS_MANIFEST_RESOURCE"; }
            var resourceId = StableHashes.StableId("resource", $"{packageId}|{id}");
            resources.Add(new(resourceId, "manifest-item", id, href, resolved.Full, item.Attribute("media-type")?.Value, material?.MediaType, material?.ByteLength ?? 0, p.ByteUnit, material?.Sha256 ?? "missing", candidates.Select(c => c.MaterialId).ToArray(), false, $"0..{(material?.ByteLength ?? 0)} {p.ByteUnit}", code));
            var edge = new ReferenceEdge(StableHashes.StableId("edge:manifest", $"{packageId}|{id}"), packageId, "manifest:href", href, resolved.Full, resolved.Fragment, status, resources.Last().CandidateMaterialIds, material?.MaterialId, code, code is null ? null : $"Manifest item {id} target status is {status}.");
            references.Add(edge);
            if (status != LinkStatus.Ok)
                diagnostics.Add(new(code ?? status.ToString(), status == LinkStatus.MissingResource ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning, edge.Message!, $"zip:/{packageMaterial.RawPath}#manifest/{id}", null, null, resourceId, status == LinkStatus.PathEscapesContainer ? AnalyzeOutcome.SemanticallyUnresolvable : null));
            BuildResourceReferences(material, resourceId, id, packageDirectory, byNorm, resources, references, diagnostics, p, requireFragment: false, packageId);
        }

        packages.Add(new(packageId, packageMaterial.MaterialId, packageMaterial.RawPath, packageMaterial.NormalizedPath, rootMediaType, manifestItems.Count, unknown));
        BuildSpine(doc, packageId, manifestItems, resources, spine, references, diagnostics);
        BuildFallback(packageId, manifestItems, resources, fallback, references, diagnostics);
        BuildNavigation(doc, packageId, packageDirectory, packageMaterial, manifestItems, byNorm, resources, references, navigation, diagnostics, p);
        BuildOverlays(packageId, manifestItems, resources, references, overlays, diagnostics);
    }

    private void BuildSpine(XDocument doc, string packageId, Dictionary<string, XElement> manifest, List<ResourceNode> resources, List<SpineEntry> spine, List<ReferenceEdge> references, List<Diagnostic> diagnostics)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var order = 0;
        foreach (var itemref in doc.Descendants(XName.Get("itemref", OpfNs)))
        {
            var idref = itemref.Attribute("idref")?.Value ?? "";
            var exists = manifest.ContainsKey(idref);
            var duplicate = !seen.Add(idref);
            var resourceId = StableHashes.StableId("resource", $"{packageId}|{idref}");
            var entryId = StableHashes.StableId("spine", $"{packageId}|{order}|{idref}");
            string? code = null;
            if (!exists) code = "MISSING_SPINE_TARGET";
            else if (duplicate) code = "DUPLICATE_SPINE_IDREF";
            spine.Add(new(entryId, packageId, order, idref, exists ? resourceId : null, duplicate, code));
            if (!exists)
            {
                references.Add(new(StableHashes.StableId("edge:spine", entryId), packageId, "spine:idref", idref, null, null, LinkStatus.MissingResource, [], null, code, $"Spine references manifest id '{idref}', which does not exist."));
                diagnostics.Add(new(code, DiagnosticSeverity.Error, $"Spine idref '{idref}' is not declared in the manifest.", $"package:{packageId}#spine/{order}", null, null, entryId, AnalyzeOutcome.SemanticallyUnresolvable));
            }
            else if (duplicate) diagnostics.Add(new(code, DiagnosticSeverity.Warning, $"Spine idref '{idref}' appears more than once; primary reading order still preserves each occurrence.", $"package:{packageId}#spine/{order}", null, null, entryId, null));
            order++;
        }
        var referencedIds = spine.Where(s => s.ItemId is not null).Select(s => s.ItemId!).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < resources.Count; i++)
            if (referencedIds.Contains(resources[i].Id)) resources[i] = resources[i] with { IsReferenced = true };
    }

    private void BuildFallback(string packageId, Dictionary<string, XElement> manifest, List<ResourceNode> resources, List<FallbackStep> fallback, List<ReferenceEdge> references, List<Diagnostic> diagnostics)
    {
        var resourceByManifest = resources.Where(r => r.Kind == "manifest-item").ToDictionary(r => r.Label, r => r.Id, StringComparer.Ordinal);
        foreach (var (id, element) in manifest)
        {
            var fallbackRef = element.Attribute("fallback")?.Value;
            if (string.IsNullOrWhiteSpace(fallbackRef)) continue;
            var visited = new List<string> { id };
            var current = id;
            var step = 0;
            var cycleKey = "";
            while (true)
            {
                if (!manifest.TryGetValue(current, out var currentElement))
                {
                    AddFallbackStep(packageId, id, current, step, LinkStatus.MissingFallbackTarget, null, "MISSING_FALLBACK_TARGET", fallback, references, diagnostics, cycleKey);
                    break;
                }
                var next = currentElement.Attribute("fallback")?.Value;
                if (string.IsNullOrWhiteSpace(next))
                {
                    AddFallbackStep(packageId, id, current, step, LinkStatus.Ok, resourceByManifest.GetValueOrDefault(current), null, fallback, references, diagnostics, cycleKey);
                    break;
                }
                AddFallbackStep(packageId, id, next, step + 1, visited.Contains(next) ? LinkStatus.FallbackCycle : LinkStatus.Ok, resourceByManifest.GetValueOrDefault(next), visited.Contains(next) ? "FALLBACK_CYCLE" : null, fallback, references, diagnostics, cycleKey);
                if (visited.Contains(next))
                {
                    cycleKey = string.Join("->", visited.Append(next));
                    diagnostics.Add(new("FALLBACK_CYCLE", DiagnosticSeverity.Error, $"Fallback chain forms a cycle: {cycleKey}.", $"package:{packageId}#manifest/{id}/fallback", null, null, resourceByManifest.GetValueOrDefault(id), AnalyzeOutcome.SemanticallyUnresolvable));
                    break;
                }
                visited.Add(next);
                current = next;
                step++;
            }
        }
    }

    private static void AddFallbackStep(string packageId, string sourceId, string targetRef, int step, LinkStatus status, string? targetId, string? code, List<FallbackStep> fallback, List<ReferenceEdge> references, List<Diagnostic> diagnostics, string cycleKey)
    {
        var stepId = StableHashes.StableId("fallback", $"{packageId}|{sourceId}|{step}|{targetRef}");
        fallback.Add(new(stepId, StableHashes.StableId("resource", $"{packageId}|{sourceId}"), targetRef, step, status, targetId, code, cycleKey));
        references.Add(new(StableHashes.StableId("edge:fallback", stepId), packageId, "manifest:fallback", targetRef, null, null, status, targetId is null ? [] : [targetId], targetId, code, code is null ? null : $"Fallback from {sourceId} to {targetRef} is {status}."));
        if (code == "MISSING_FALLBACK_TARGET")
            diagnostics.Add(new(code, DiagnosticSeverity.Error, $"Fallback target '{targetRef}' is not in the manifest.", $"package:{packageId}#manifest/{sourceId}/fallback", null, null, stepId, AnalyzeOutcome.SemanticallyUnresolvable));
    }

    private void BuildOverlays(string packageId, Dictionary<string, XElement> manifest, List<ResourceNode> resources, List<ReferenceEdge> references, List<OverlayLink> overlays, List<Diagnostic> diagnostics)
    {
        var resourceByManifest = resources.Where(r => r.Kind == "manifest-item").ToDictionary(r => r.Label, r => r.Id, StringComparer.Ordinal);
        foreach (var (id, element) in manifest)
        {
            var overlayRef = element.Attribute("media-overlay")?.Value;
            if (string.IsNullOrWhiteSpace(overlayRef)) continue;
            var targetId = resourceByManifest.GetValueOrDefault(overlayRef);
            var status = manifest.ContainsKey(overlayRef) ? LinkStatus.Ok : LinkStatus.MissingOverlay;
            var linkId = StableHashes.StableId("overlay", $"{packageId}|{id}|{overlayRef}");
            overlays.Add(new(linkId, resourceByManifest[id], overlayRef, targetId, status, status == LinkStatus.MissingOverlay ? "MISSING_MEDIA_OVERLAY" : null));
            references.Add(new(StableHashes.StableId("edge:overlay", linkId), packageId, "media-overlay", overlayRef, null, null, status, targetId is null ? [] : [targetId], targetId, linkId == "" ? null : "MEDIA_OVERLAY_LINK", status == LinkStatus.Ok ? null : $"Media overlay '{overlayRef}' linked from '{id}' is missing."));
            if (status == LinkStatus.MissingOverlay)
                diagnostics.Add(new("MISSING_MEDIA_OVERLAY", DiagnosticSeverity.Error, $"media-overlay '{overlayRef}' linked from manifest item '{id}' is missing.", $"package:{packageId}#manifest/{id}/media-overlay", null, null, linkId, AnalyzeOutcome.SemanticallyUnresolvable));
        }
    }

    private void BuildNavigation(XDocument doc, string packageId, string packageDirectory, EntryMaterial packageMaterial, Dictionary<string, XElement> manifest, IReadOnlyDictionary<string, List<EntryMaterial>> byNorm, List<ResourceNode> resources, List<ReferenceEdge> references, List<NavigationNode> navigation, List<Diagnostic> diagnostics, AnalysisParameters p)
    {
        var resourceByManifest = resources.Where(r => r.Kind == "manifest-item").ToDictionary(r => r.Label, r => r, StringComparer.Ordinal);
        foreach (var (itemId, item) in manifest.Where(kv => (kv.Value.Attribute("properties")?.Value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("nav")))
        {
            if (!resourceByManifest.TryGetValue(itemId, out var node)) continue;
            var material = node.CandidateMaterialIds.Count == 0 ? null : FindCandidates(byNorm, node.NormalizedPath ?? "").FirstOrDefault();
            if (material is null) continue;
            XDocument navDoc;
            try { navDoc = ParseXml(material.Bytes); }
            catch (XmlException ex)
            {
                diagnostics.Add(new("FORMAT_NAV_XML", DiagnosticSeverity.Error, ex.Message, $"zip:/{material.RawPath}", ex.LineNumber, ex.LinePosition, material.MaterialId, AnalyzeOutcome.FormatCorrupt));
                continue;
            }
            var order = 0;
            foreach (var nav in navDoc.Descendants(XName.Get("nav", XhtmlNs)))
            {
                foreach (var li in nav.Descendants(XName.Get("li", XhtmlNs)))
                {
                    var anchor = li.Elements(XName.Get("a", XhtmlNs)).FirstOrDefault();
                    var href = anchor?.Attribute("href")?.Value;
                    if (href is null) continue;
                    var resolved = PathRules.Resolve(Path.GetDirectoryName(material.NormalizedPath ?? "")?.Replace('\\','/') ?? "", href);
                    var candidates = resolved.Path is null ? [] : FindCandidates(byNorm, resolved.Path);
                    var target = candidates.FirstOrDefault();
                    var broken = resolved.Escapes || target is null;
                    var status = resolved.Escapes ? LinkStatus.PathEscapesContainer : target is null ? LinkStatus.MissingResource : LinkStatus.Ok;
                    if (!broken && !string.IsNullOrEmpty(resolved.Fragment) && p.RequireFragmentTargets)
                    {
                        var ids = FragmentIds(target!);
                        if (!ids.Contains(resolved.Fragment)) { broken = true; status = LinkStatus.MissingFragment; }
                    }
                    var depth = li.Ancestors(XName.Get("li", XhtmlNs)).Count();
                    var navId = StableHashes.StableId("nav", $"{packageId}|{itemId}|{order}|{href}|{material.RawPath}");
                    var parent = li.Parent?.AncestorsAndSelf(XName.Get("li", XhtmlNs)).Skip(1).FirstOrDefault()?.Elements(XName.Get("a", XhtmlNs)).FirstOrDefault()?.Attribute("href")?.Value;
                    navigation.Add(new(navId, StableHashes.StableId("nav-parent", $"{packageId}|{itemId}|{parent ?? ""}"), packageId, depth, order, anchor!.Value.Trim(), href, resolved.Fragment, node.Id, target is null ? null : StableHashes.StableId("material:entry", $"entry|{target.RawPath}|{target.OriginalIndex}"), broken));
                    references.Add(new(StableHashes.StableId("edge:nav", navId), node.Id, "navigation:href", href, resolved.Full, resolved.Fragment, status, candidates.Select(c => c.MaterialId).ToArray(), target?.MaterialId, status == LinkStatus.Ok ? null : status.ToString(), $"Navigation anchor '{anchor.Value.Trim()}' target is {status}."));
                    if (broken) diagnostics.Add(new(status == LinkStatus.PathEscapesContainer ? "PATH_NAV_ESCAPE" : status == LinkStatus.MissingFragment ? "MISSING_NAV_FRAGMENT" : "MISSING_NAV_TARGET", status == LinkStatus.PathEscapesContainer ? DiagnosticSeverity.Warning : DiagnosticSeverity.Error, $"Navigation anchor '{anchor.Value.Trim()}' -> '{href}' is {status}.", $"zip:/{material.RawPath}", null, null, navId, status == LinkStatus.PathEscapesContainer ? AnalyzeOutcome.SemanticallyUnresolvable : null));
                    order++;
                }
            }
        }
    }

    private void BuildResourceReferences(EntryMaterial? material, string sourceId, string manifestId, string sourceDirectory, IReadOnlyDictionary<string, List<EntryMaterial>> byNorm, List<ResourceNode> resources, List<ReferenceEdge> references, List<Diagnostic> diagnostics, AnalysisParameters p, bool requireFragment, string packageId)
    {
        if (material is null || material.Bytes.Length == 0) return;
        var isXmlLike = (material.MediaType ?? "").Contains("xml", StringComparison.OrdinalIgnoreCase) || material.RawPath.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase) || material.RawPath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) || material.RawPath.EndsWith(".smil", StringComparison.OrdinalIgnoreCase);
        if (!isXmlLike) return;
        XDocument doc;
        try { doc = ParseXml(material.Bytes); }
        catch (XmlException) { return; }
        var directory = Path.GetDirectoryName(material.NormalizedPath ?? "")?.Replace('\\', '/') ?? "";
        var links = new List<(string Relation, string Raw, string? Attribute)>();
        foreach (var element in doc.Descendants())
        {
            foreach (var attributeName in new[] { "href", "src" })
            {
                var value = element.Attribute(attributeName)?.Value;
                if (!string.IsNullOrWhiteSpace(value)) links.Add((attributeName, value, attributeName));
            }
            if (element.Name.LocalName == "audio")
            {
                var src = element.Attribute("src")?.Value;
                if (src is not null) links.Add(("smil:audio", $"{src}#clipBegin={element.Attribute("clipBegin")?.Value};clipEnd={element.Attribute("clipEnd")?.Value}", "clipBegin"));
            }
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var link in links)
        {
            if (link.Raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || link.Raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) continue;
            var key = $"{link.Relation}|{link.Raw}";
            if (!seen.Add(key)) continue;
            var resolved = PathRules.Resolve(directory, link.Raw);
            var candidates = resolved.Path is null ? [] : FindCandidates(byNorm, resolved.Path);
            var target = candidates.FirstOrDefault();
            var status = LinkStatus.Ok;
            string? code = null;
            if (resolved.Escapes) { status = LinkStatus.PathEscapesContainer; code = "PATH_REFERENCE_ESCAPE"; }
            else if (candidates.Count == 0) { status = LinkStatus.MissingResource; code = "MISSING_REFERENCED_RESOURCE"; }
            else if (candidates.Count > 1) { status = LinkStatus.AmbiguousResource; code = "AMBIGUOUS_REFERENCE_TARGET"; }
            else if (!string.IsNullOrEmpty(resolved.Fragment) && p.RequireFragmentTargets)
            {
                var ids = FragmentIds(target!);
                if (!ids.Contains(resolved.Fragment)) { status = LinkStatus.MissingFragment; code = "MISSING_REFERENCE_FRAGMENT"; }
            }
            var edgeId = StableHashes.StableId($"edge:{link.Relation}", $"{sourceId}|{link.Raw}");
            references.Add(new(edgeId, sourceId, link.Relation, link.Raw, resolved.Full, resolved.Fragment, status, candidates.Select(c => c.MaterialId).ToArray(), target?.MaterialId, code, code is null ? null : $"Resource reference {link.Raw} is {status}."));
            if (status != LinkStatus.Ok) diagnostics.Add(new(code ?? status.ToString(), status == LinkStatus.MissingResource ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning, $"Resource '{manifestId}' references '{link.Raw}': {status}.", $"zip:/{material.RawPath}#{link.Attribute}", null, null, edgeId, status == LinkStatus.PathEscapesContainer ? AnalyzeOutcome.SemanticallyUnresolvable : null));
        }
    }

    private static HashSet<string> FragmentIds(EntryMaterial material)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var doc = ParseXml(material.Bytes);
            foreach (var element in doc.Descendants())
            {
                var id = element.Attribute("id")?.Value ?? element.Attribute(XNamespace.Xml + "id")?.Value;
                if (!string.IsNullOrWhiteSpace(id)) ids.Add(id);
                if (element.Name.LocalName == "a")
                {
                    var name = element.Attribute("name")?.Value;
                    if (!string.IsNullOrWhiteSpace(name)) ids.Add(name);
                }
            }
        }
        catch (XmlException) { }
        return ids;
    }

    private static List<EntryMaterial> FindCandidates(IReadOnlyDictionary<string, List<EntryMaterial>> byNorm, string path)
        => byNorm.TryGetValue(path, out var matches) ? matches.OrderBy(e => e.OriginalIndex).ToList() : [];

    private static XDocument ParseXml(byte[] bytes)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreWhitespace = false,
            Async = false
        };
        using var stream = new MemoryStream(bytes);
        using var reader = XmlReader.Create(stream, settings);
        return XDocument.Load(reader, LoadOptions.SetLineInfo);
    }

    private static string? GuessMediaType(string? path)
    {
        return path switch
        {
            "mimetype" => "application/octet-stream",
            "META-INF/container.xml" => "application/oebps-container-xml+xml",
            _ when path?.EndsWith(".opf", StringComparison.OrdinalIgnoreCase) == true => "application/oebps-package+xml",
            _ when path?.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase) == true => "application/xhtml+xml",
            _ when path?.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) == true => "image/svg+xml",
            _ when path?.EndsWith(".smil", StringComparison.OrdinalIgnoreCase) == true => "application/smil+xml",
            _ when path?.EndsWith(".css", StringComparison.OrdinalIgnoreCase) == true => "text/css",
            _ when path?.EndsWith(".png", StringComparison.OrdinalIgnoreCase) == true => "image/png",
            _ when path?.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) == true || path?.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) == true => "image/jpeg",
            _ when path?.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) == true => "audio/mpeg",
            _ when path?.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) == true => "video/mp4",
            _ => null
        };
    }

    private AnalyzeOutcome DiagnoseOutcome(List<Diagnostic> diagnostics, IReadOnlyList<ReviewDecision> decisions, AnalysisParameters p)
    {
        if (diagnostics.Any(d => d.Outcome == AnalyzeOutcome.FormatCorrupt)) return AnalyzeOutcome.FormatCorrupt;
        if (decisions.GroupBy(d => d.SubjectId).Any(g => g.Select(d => d.Action).Distinct().Count() > 1))
        {
            foreach (var group in decisions.GroupBy(d => d.SubjectId).Where(g => g.Select(d => d.Action).Distinct().Count() > 1))
                diagnostics.Add(new("CONFLICTING_DECISION", DiagnosticSeverity.Error, $"Manual decisions for '{group.Key}' contain incompatible accepted/rejected/adjusted actions.", $"decision:{group.Key}", null, null, group.Key, AnalyzeOutcome.SemanticallyUnresolvable));
            return AnalyzeOutcome.SemanticallyUnresolvable;
        }
        if (diagnostics.Any(d => d.Outcome == AnalyzeOutcome.SemanticallyUnresolvable)) return AnalyzeOutcome.SemanticallyUnresolvable;
        return AnalyzeOutcome.Completed;
    }

    private static void AddJudgments(List<JudgmentBasis> judgments, List<ReferenceEdge> references, List<FallbackStep> fallback, List<ResourceNode> resources, List<Diagnostic> diagnostics, AnalyzeOutcome outcome)
    {
        judgments.Add(new("SPINE_PRIMARY_ORDER_ONLY", outcome.ToString(), "spine defines the primary linear reading order; it is not treated as the complete set of manifest, navigation, content or media-overlay references.", resources.Select(r => r.Id).ToArray()));
        judgments.Add(new("FALLBACK_CYCLE_VS_MISSING_SEPARATE", outcome.ToString(), "fallback cycles and missing fallback targets are distinct states and are reported with separate diagnostic codes and stable edge identities.", fallback.Select(f => f.Id).ToArray()));
        judgments.Add(new("CONTAINER_PATH_BOUNDARY", outcome.ToString(), "RFC3986-style lexical normalization resolves '.' and '..', but any target whose normalized form leaves the OCF root is rejected and all in-container candidates remain visible.", references.Where(r => r.Status is LinkStatus.PathEscapesContainer or LinkStatus.AmbiguousResource).Select(r => r.Id).ToArray()));
        judgments.Add(new("RAW_BEFORE_DERIVED", outcome.ToString(), "raw ZIP bytes, raw paths, declared media types, fragments and unknown metadata remain stored; normalization and graph inference occur only in derived results.", []));
    }

    private static AnalysisResult Empty(string archiveId, string inputHash, byte[] bytes, AnalysisParameters p, string? parent, string now, AnalyzeOutcome outcome, List<Diagnostic> diagnostics, IReadOnlyList<ReviewDecision> decisions)
    {
        return new(archiveId, inputHash, p.ByteUnit, bytes.Length, p.Version, parent, StableHashes.ShortHash($"{inputHash}|{p.Version}|{parent}|{outcome}"), now, outcome, diagnostics, [], [], [], [], [], [], [], [], [], decisions, p.CoordinateSpace, p.TimeUnit, p.ByteUnit);
    }
}

public static class SystemClock
{
    public static Func<DateTimeOffset> UtcNowFunc { get; set; } = () => DateTimeOffset.UtcNow;
    public static string UtcNow() => UtcNowFunc().UtcDateTime.ToString("O");
}
