using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace EpubReview.Core;

public sealed class SemanticUnresolvedException : Exception
{
    public required Diagnostic Diagnostic { get; init; }
    public SemanticUnresolvedException(Diagnostic diagnostic) : base(diagnostic.Message) => Diagnostic = diagnostic;
}

public sealed class ReviewEngine
{
    static readonly XNamespace Xhtml = "http://www.w3.org/1999/xhtml";
    static readonly XNamespace Ncx = "http://www.daisy.org/z3986/2005/ncx/";

    public ReviewResult Evaluate(string caseId, ImportedEpub import, ReviewParameters parameters, string? parentResultId, string fromEventId)
    {
        var diagnostics = new List<Diagnostic>(import.Diagnostics);
        var entryByPath = import.ZipEntries.Where(e => !string.IsNullOrEmpty(e.NormalizedEntryName)).ToDictionary(e => e.NormalizedEntryName!, e => e, StringComparer.Ordinal);
        var resources = new Dictionary<string, ResourceAsset>(StringComparer.Ordinal);
        var references = new List<ReferenceEdge>();
        var spine = new List<SpineEntry>();
        var navigation = new List<NavigationNode>();
        var brokenAnchors = new List<BrokenAnchor>();
        var fallbackChains = new List<FallbackChainStatus>();
        var mediaOverlays = new List<MediaOverlayLink>();

        foreach (var package in import.Packages)
        {
            var byId = package.ManifestItems.ToDictionary(item => item.ManifestId, item => item, StringComparer.Ordinal);
            AddPackageResources(package, entryByPath, resources);
            EvaluateSpine(package, byId, resources, spine, references, diagnostics);
            EvaluateManifestLinks(package, byId, references, mediaOverlays, diagnostics);
            EvaluateContent(package, byId, entryByPath, resources, references, navigation, brokenAnchors, diagnostics);
            EvaluateFallbackChains(package, byId, resources, fallbackChains, parameters, diagnostics);
        }

        if (import.Packages.Count > 1)
            diagnostics.Add(new Diagnostic { Code = "MULTIPLE-PACKAGES", Severity = DiagnosticSeverity.Info, Category = DiagnosticCategory.Semantics, Message = "Container exposes multiple OPF package documents; package-qualified stable identities are retained.", Location = new SourceLocation { XPath = "/container/rootfiles" } });

        var status = ResolveStatus(diagnostics);
        if (parameters.RequireUniqueFallbackTarget && fallbackChains.Any(chain => chain.Candidates.Count(c => c.IsLegalCandidate) > 1))
            status = ReviewStatus.SemanticallyUnresolvable;

        var dependencyInput = import.ZipEntries.OrderBy(e => e.RawEntryName, StringComparer.Ordinal).Select(e => $"{e.RawEntryName}|{e.LengthBytes}|{e.Sha256}");
        var summaryPayload = string.Join('\n', dependencyInput);
        var parametersPayload = string.Join('|', parameters.Version, parameters.MaxContainerBytes, parameters.MaxResourceCount, parameters.MaxFallbackDepth, parameters.RequireUniqueFallbackTarget, string.Join(',', parameters.SupportedMediaTypes.OrderBy(x => x)));
        var inputHash = StableHash.Of("input-summary", summaryPayload);
        var parameterHash = StableHash.Of("parameters", parametersPayload);
        var stable = StableHash.Of("result", caseId, inputHash, parameterHash, parentResultId ?? "");
        var dependencies = import.ZipEntries.OrderBy(e => e.RawEntryName, StringComparer.Ordinal).Select(entry => new MaterialDependency { Role = "zip-entry-byte", StableId = entry.StableId, Sha256 = entry.Sha256, LengthBytes = entry.LengthBytes }).ToArray();

        return new ReviewResult
        {
            StableId = stable,
            CaseId = caseId,
            Status = status,
            InputSummary = $"{import.ZipEntries.Count} entries; {import.Packages.Count} packages; SHA-256 {inputHash}; bytes unit byte",
            InputMaterialSetHash = inputHash,
            ParametersStableId = parameterHash,
            ParametersVersion = parameters.Version,
            ParentResultStableId = parentResultId,
            MaterialDependencies = dependencies,
            Resources = resources.Values.OrderBy(r => r.StableId, StringComparer.Ordinal).ToArray(),
            References = references.OrderBy(r => r.Ordinal).ThenBy(r => r.StableId, StringComparer.Ordinal).ToArray(),
            Spine = spine.OrderBy(s => s.PackageStableId, StringComparer.Ordinal).ThenBy(s => s.Position).ThenBy(s => s.StableId, StringComparer.Ordinal).ToArray(),
            Navigation = navigation.OrderBy(n => n.Ordinal).ThenBy(n => n.StableId, StringComparer.Ordinal).ToArray(),
            BrokenAnchors = brokenAnchors.OrderBy(a => a.StableId, StringComparer.Ordinal).ToArray(),
            FallbackChains = fallbackChains.OrderBy(c => c.OriginManifestItemStableId, StringComparer.Ordinal).ToArray(),
            MediaOverlays = mediaOverlays.OrderBy(m => m.StableId, StringComparer.Ordinal).ToArray(),
            Diagnostics = diagnostics.OrderBy(d => d.Location.ZipEntry ?? "").ThenBy(d => d.Location.Line ?? int.MaxValue).ThenBy(d => d.Code).ToArray(),
            EffectiveRange = new EffectiveRange { FromEventId = fromEventId, InvalidationReason = null },
            ComputedAt = DateTimeOffset.UtcNow
        };
    }

    static ReviewStatus ResolveStatus(List<Diagnostic> diagnostics)
    {
        if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Fatal && d.Category == DiagnosticCategory.Budget)) return ReviewStatus.ResourceBudgetExhausted;
        if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Fatal && d.Category == DiagnosticCategory.Format)) return ReviewStatus.MalformedInput;
        if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Fatal && d.Category == DiagnosticCategory.Semantics)) return ReviewStatus.SemanticallyUnresolvable;
        return diagnostics.Count == 0 ? ReviewStatus.Success : ReviewStatus.CompletedWithDiagnostics;
    }

    static void AddPackageResources(PackageMaterial package, Dictionary<string, ZipEntryMaterial> entries, Dictionary<string, ResourceAsset> resources)
    {
        AddResource(resources, package.StableId, package.ContainerRawPath, package.NormalizedPath, null, package.MediaType, package.Bytes, package.StableId);
        foreach (var item in package.ManifestItems)
        {
            ZipEntryMaterial? entry = item.NormalizedPath is not null && entries.TryGetValue(item.NormalizedPath, out var found) ? found : null;
            var escaped = item.NormalizedPath is null;
            AddResource(resources, package.StableId, item.RawHref, item.NormalizedPath, null, item.MediaType, entry?.Bytes ?? Array.Empty<byte>(), item.StableId, escaped, entry is not null);
        }
    }

    static void AddResource(Dictionary<string, ResourceAsset> map, string packageId, string raw, string? path, string? fragment, string? mediaType, byte[] bytes, string manifestId, bool escaped = false, bool? existsOverride = null)
    {
        var id = StableHash.Of("resource", packageId, path ?? raw, fragment ?? "");
        map[id] = new ResourceAsset { StableId = id, PackageStableId = packageId, RawReference = raw, NormalizedPath = path, Fragment = fragment, MediaType = mediaType, Sha256 = bytes.Length == 0 ? null : StableHash.Sha256(bytes), LengthBytes = bytes.Length, ExistsInContainer = existsOverride ?? bytes.Length > 0, IsPathEscaped = escaped, ManifestItemStableId = manifestId };
    }

    static void EvaluateSpine(PackageMaterial package, Dictionary<string, ManifestItem> byId, Dictionary<string, ResourceAsset> resources, List<SpineEntry> spine, List<ReferenceEdge> references, List<Diagnostic> diagnostics)
    {
        var doc = TryXml(package);
        var spineElement = doc?.Root?.Element(XName.Get("spine", "http://www.idpf.org/2007/opf"));
        if (spineElement is null) return;
        var seen = new HashSet<string>();
        foreach (var (element, ordinal) in spineElement.Elements(XName.Get("itemref", "http://www.idpf.org/2007/opf")).Select((x, i) => (x, i)))
        {
            var idRef = element.Attribute("idref")?.Value ?? "";
            byId.TryGetValue(idRef, out var item);
            var resourceId = item is null ? null : StableHash.Of("resource", package.StableId, item.NormalizedPath ?? item.RawHref, "");
            var entryId = StableHash.Of("spine-entry", package.Sha256, ordinal.ToString(), idRef);
            spine.Add(new SpineEntry { StableId = entryId, PackageStableId = package.StableId, IdRef = idRef, ManifestItemStableId = item?.StableId, ResourceStableId = resources.ContainsKey(resourceId ?? "") ? resourceId : null, Linear = element.Attribute("linear")?.Value != "no", Position = ordinal, Location = EpubImporter.Location(package.ContainerRawPath, element, "/package/spine/itemref", ordinal) });
            references.Add(new ReferenceEdge { StableId = StableHash.Of("edge", "spine", entryId), SourceStableId = package.StableId, RawHref = idRef, TargetResourceStableId = resourceId, Fragment = null, ReferenceKind = "spine-primary-order", TargetExists = resourceId is not null && resources.ContainsKey(resourceId), FragmentExists = true, Ordinal = references.Count, Location = EpubImporter.Location(package.ContainerRawPath, element, "/package/spine/itemref", ordinal) });
            if (!seen.Add(idRef))
                diagnostics.Add(new Diagnostic { Code = "SPINE-DUPLICATE-IDREF", Severity = DiagnosticSeverity.Warning, Category = DiagnosticCategory.Semantics, Message = "Spine repeats an idref; both ordinal positions are retained and primary order does not erase other references.", Location = EpubImporter.Location(package.ContainerRawPath, element, "/package/spine/itemref", ordinal), AffectedStableId = entryId, RawValue = idRef });
            if (item is null)
                diagnostics.Add(new Diagnostic { Code = "SPINE-IDREF-MISSING", Severity = DiagnosticSeverity.Error, Category = DiagnosticCategory.Semantics, Message = "Spine idref has no manifest item.", Location = EpubImporter.Location(package.ContainerRawPath, element, "/package/spine/itemref", ordinal), AffectedStableId = entryId, RawValue = idRef });
        }
    }

    static void EvaluateManifestLinks(PackageMaterial package, Dictionary<string, ManifestItem> byId, List<ReferenceEdge> references, List<MediaOverlayLink> mediaOverlays, List<Diagnostic> diagnostics)
    {
        foreach (var item in package.ManifestItems)
        {
            if (item.FallbackManifestId is not null)
            {
                byId.TryGetValue(item.FallbackManifestId, out var fallback);
                references.Add(new ReferenceEdge { StableId = StableHash.Of("edge", "fallback", item.StableId, item.FallbackManifestId), SourceStableId = item.StableId, RawHref = item.FallbackManifestId, TargetResourceStableId = fallback is null ? null : StableHash.Of("resource", package.StableId, fallback.NormalizedPath ?? fallback.RawHref, ""), ReferenceKind = "manifest-fallback", TargetExists = fallback is not null, FragmentExists = true, Ordinal = references.Count, Location = item.Location });
                if (fallback is null)
                    diagnostics.Add(new Diagnostic { Code = "FALLBACK-ID-MISSING", Severity = DiagnosticSeverity.Error, Category = DiagnosticCategory.Semantics, Message = "fallback attribute names a missing manifest id.", Location = item.Location, AffectedStableId = item.StableId, RawValue = item.FallbackManifestId });
            }
            if (item.MediaOverlayManifestId is not null)
            {
                byId.TryGetValue(item.MediaOverlayManifestId, out var overlay);
                var resource = overlay is null ? null : StableHash.Of("resource", package.StableId, overlay.NormalizedPath ?? overlay.RawHref, "");
                var link = new MediaOverlayLink { StableId = StableHash.Of("media-overlay", item.StableId, item.MediaOverlayManifestId), ContentManifestItemStableId = item.StableId, OverlayManifestIdRaw = item.MediaOverlayManifestId, OverlayManifestItemStableId = overlay?.StableId, OverlayResourceStableId = resource, LinkResolves = overlay is not null, Location = item.Location };
                mediaOverlays.Add(link);
                if (!link.LinkResolves)
                    diagnostics.Add(new Diagnostic { Code = "MEDIA-OVERLAY-BROKEN", Severity = DiagnosticSeverity.Error, Category = DiagnosticCategory.Semantics, Message = "media-overlay attribute does not resolve to a manifest item.", Location = item.Location, AffectedStableId = link.StableId, RawValue = item.MediaOverlayManifestId });
            }
        }
    }

    static XDocument? TryXml(PackageMaterial package)
    {
        try { return XDocument.Load(new MemoryStream(package.Bytes), LoadOptions.SetLineInfo); }
        catch { return null; }
    }

    void EvaluateContent(
        PackageMaterial package,
        Dictionary<string, ManifestItem> byId,
        Dictionary<string, ZipEntryMaterial> entryByPath,
        Dictionary<string, ResourceAsset> resources,
        List<ReferenceEdge> references,
        List<NavigationNode> navigation,
        List<BrokenAnchor> brokenAnchors,
        List<Diagnostic> diagnostics)
    {
        var baseDirectory = ContainerPath.DirectoryOf(package.NormalizedPath);
        foreach (var item in package.ManifestItems)
        {
            if (item.NormalizedPath is null || !entryByPath.TryGetValue(item.NormalizedPath, out var entry)) continue;
            XDocument? content;
            try { content = XDocument.Load(new MemoryStream(entry.Bytes), LoadOptions.SetLineInfo); }
            catch (XmlException exception)
            {
                diagnostics.Add(new Diagnostic { Code = "CONTENT-XML", Severity = DiagnosticSeverity.Warning, Category = DiagnosticCategory.Format, Message = exception.Message, Location = new SourceLocation { ZipEntry = entry.RawEntryName, Line = exception.LineNumber, Column = exception.LinePosition }, AffectedStableId = item.StableId });
                continue;
            }
            if (content.Root is null) continue;

            var isNav = item.Properties?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("nav") == true;
            if (isNav) AddNavigation(package, entry, content.Root, navigation, resources, references, brokenAnchors, entryByPath, diagnostics);

            var attributes = content.Descendants().SelectMany(element => element.Attributes().Where(attribute =>
                attribute.Name.LocalName is "href" or "src" or "data" ||
                (element.Name.LocalName == "text" && attribute.Name.LocalName == "src"))).ToArray();
            foreach (var (attribute, ordinal) in attributes.Select((a, i) => (a, i)))
            {
                if (string.IsNullOrWhiteSpace(attribute.Value) || attribute.Value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || attribute.Value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || attribute.Value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) continue;
                var resolved = ContainerPath.Resolve(baseDirectory, attribute.Value);
                var targetPath = resolved.NormalizedPath ?? item.NormalizedPath;
                var targetEntry = targetPath is not null && entryByPath.TryGetValue(targetPath, out var foundEntry) ? foundEntry : null;
                var targetId = EnsureReferencedResource(package, resources, byId, resolved.NormalizedPath is null ? item.NormalizedPath : resolved.NormalizedPath, resolved.Fragment, targetEntry?.MediaType ?? GuessMediaType(targetPath), targetEntry?.Bytes ?? Array.Empty<byte>(), targetEntry is not null, resolved.EscapesContainer);
                var kind = item.MediaType.Contains("smil", StringComparison.OrdinalIgnoreCase) ? "media-overlay-smil-reference" : "content-reference";
                var edgeId = StableHash.Of("edge", kind, item.StableId, ordinal.ToString(), attribute.Value);
                var elementLocation = EpubImporter.Location(entry.RawEntryName, attribute.Parent, "/*", ordinal);
                var fragmentExists = resolved.Fragment is null || FragmentExists(targetEntry, resolved.Fragment);
                references.Add(new ReferenceEdge { StableId = edgeId, SourceStableId = item.StableId, RawHref = attribute.Value, TargetResourceStableId = targetId, Fragment = resolved.Fragment, ReferenceKind = kind, TargetExists = targetEntry is not null && !resolved.EscapesContainer, FragmentExists = fragmentExists, Ordinal = references.Count, Location = elementLocation });
                if (resolved.EscapesContainer)
                    diagnostics.Add(new Diagnostic { Code = "PATH-ESCAPES-CONTAINER", Severity = DiagnosticSeverity.Error, Category = DiagnosticCategory.Semantics, Message = "Reference normalizes outside the EPUB container; it is retained but not treated as a container resource.", Location = elementLocation, AffectedStableId = edgeId, RawValue = attribute.Value });
                else if (targetEntry is null)
                    diagnostics.Add(new Diagnostic { Code = "REFERENCE-TARGET-MISSING", Severity = DiagnosticSeverity.Error, Category = DiagnosticCategory.Integrity, Message = "Reference target is absent from ZIP entries.", Location = elementLocation, AffectedStableId = edgeId, RawValue = attribute.Value });
                else if (resolved.Fragment is not null && !fragmentExists)
                    {
                    var anchorId = StableHash.Of("broken-anchor", edgeId, resolved.Fragment);
                    brokenAnchors.Add(new BrokenAnchor { StableId = anchorId, ReferenceStableId = edgeId, RawHref = attribute.Value, Fragment = resolved.Fragment, ResourceStableId = targetId, Location = elementLocation });
                    diagnostics.Add(new Diagnostic { Code = "FRAGMENT-TARGET-MISSING", Severity = DiagnosticSeverity.Warning, Category = DiagnosticCategory.Integrity, Message = "Fragment anchor is absent from the target document.", Location = elementLocation, AffectedStableId = anchorId, RawValue = resolved.Fragment });
                }
            }
        }
    }

    static string EnsureReferencedResource(PackageMaterial package, Dictionary<string, ResourceAsset> resources, Dictionary<string, ManifestItem> byId, string? path, string? fragment, string? mediaType, byte[] bytes, bool exists, bool escaped)
    {
        var id = StableHash.Of("resource", package.StableId, path ?? "", fragment ?? "");
        if (!resources.ContainsKey(id))
        {
            var manifest = byId.Values.FirstOrDefault(item => item.NormalizedPath == path)?.StableId;
            resources.Add(id, new ResourceAsset { StableId = id, PackageStableId = package.StableId, RawReference = path + (fragment is null ? "" : $"#{fragment}"), NormalizedPath = path, Fragment = fragment, MediaType = mediaType, Sha256 = bytes.Length == 0 ? null : StableHash.Sha256(bytes), LengthBytes = bytes.Length, ExistsInContainer = exists, IsPathEscaped = escaped, ManifestItemStableId = manifest });
        }
        return id;
    }

    static void AddNavigation(PackageMaterial package, ZipEntryMaterial entry, XElement root, List<NavigationNode> navigation, Dictionary<string, ResourceAsset> resources, List<ReferenceEdge> references, List<BrokenAnchor> brokenAnchors, Dictionary<string, ZipEntryMaterial> entries, List<Diagnostic> diagnostics)
    {
        var nav = root.Descendants().FirstOrDefault(element => element.Name.LocalName == "nav" && element.Attributes().Any(attribute => attribute.Name.LocalName == "type" && attribute.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("toc")));
        if (nav is null) return;
        var firstList = nav.Elements().FirstOrDefault(element => element.Name.LocalName == "ol" || element.Name.LocalName == "ul");
        var ordinal = 0;
        WalkNav(package, entry, firstList, 0, null, navigation, ref ordinal);
    }

    static void WalkNav(PackageMaterial package, ZipEntryMaterial entry, XElement? list, int depth, string? parentId, List<NavigationNode> nodes, ref int ordinal)
    {
        if (list is null) return;
        foreach (var listItem in list.Elements().Where(element => element.Name.LocalName == "li"))
        {
            var anchor = listItem.Elements().FirstOrDefault(element => element.Name.LocalName == "a");
            if (anchor is null) continue;
            var href = anchor.Attribute("href")?.Value ?? "";
            var nodeId = StableHash.Of("nav", package.Sha256, parentId ?? "root", ordinal.ToString(), href, anchor.Value.Trim());
            var resolved = ContainerPath.Resolve(ContainerPath.DirectoryOf(package.NormalizedPath), href);
            nodes.Add(new NavigationNode { StableId = nodeId, PackageStableId = package.StableId, Label = anchor.Value.Trim(), RawHref = href, ResourceStableId = StableHash.Of("resource", package.StableId, resolved.NormalizedPath ?? "", resolved.Fragment ?? ""), Fragment = resolved.Fragment, Depth = depth, Ordinal = ordinal, ParentStableId = parentId, Location = EpubImporter.Location(entry.RawEntryName, anchor, "//nav[@epub:type='toc']//a", ordinal) });
            ordinal++;
            WalkNav(package, entry, listItem.Elements().FirstOrDefault(element => element.Name.LocalName == "ol" || element.Name.LocalName == "ul"), depth + 1, nodeId, nodes, ref ordinal);
        }
    }

    static bool FragmentExists(ZipEntryMaterial? entry, string fragment)
    {
        if (entry is null || string.IsNullOrWhiteSpace(fragment)) return false;
        try
        {
            var doc = XDocument.Load(new MemoryStream(entry.Bytes));
            return doc.Descendants().Any(element => (string?)element.Attribute("id") == fragment || (string?)element.Attribute(XNamespace.Xml + "id") == fragment || (element.Name.LocalName == "a" && (string?)element.Attribute("name") == fragment));
        }
        catch { return false; }
    }

    static string? GuessMediaType(string? path)
    {
        var extension = path is null ? "" : Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".xhtml" or ".html" or ".htm" => "application/xhtml+xml",
            ".ncx" => "application/x-dtbncx+xml",
            ".smil" => "application/smil+xml",
            ".css" => "text/css",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".svg" => "image/svg+xml",
            ".mp3" => "audio/mpeg",
            _ => null
        };
    }

    static void EvaluateFallbackChains(PackageMaterial package, Dictionary<string, ManifestItem> byId, Dictionary<string, ResourceAsset> resources, List<FallbackChainStatus> chains, ReviewParameters parameters, List<Diagnostic> diagnostics)
    {
        foreach (var origin in package.ManifestItems.Where(item => item.FallbackManifestId is not null))
        {
            var candidates = new List<FallbackCandidate>();
            var pathIds = new List<string> { origin.ManifestId };
            var currentId = origin.FallbackManifestId;
            var hasCycle = false;
            var missingId = (string?)null;

            for (var hop = 1; hop <= parameters.MaxFallbackDepth + 1; hop++)
            {
                if (currentId is null) break;
                if (pathIds.Contains(currentId))
                {
                    hasCycle = true;
                    var start = pathIds.IndexOf(currentId);
                    pathIds.Add(currentId);
                    diagnostics.Add(new Diagnostic { Code = "FALLBACK-CYCLE", Severity = DiagnosticSeverity.Error, Category = DiagnosticCategory.Semantics, Message = "Fallback chain returns to an earlier manifest id; cycle is reported separately from missing resources.", Location = origin.Location, AffectedStableId = origin.StableId, RawValue = string.Join(" -> ", pathIds.Skip(start)) });
                    break;
                }
                if (!byId.TryGetValue(currentId, out var current))
                {
                    missingId = currentId;
                    diagnostics.Add(new Diagnostic { Code = "FALLBACK-MISSING-RESOURCE", Severity = DiagnosticSeverity.Error, Category = DiagnosticCategory.Integrity, Message = "Fallback chain reaches a manifest id that does not exist.", Location = origin.Location, AffectedStableId = origin.StableId, RawValue = currentId });
                    break;
                }

                pathIds.Add(current.ManifestId);
                var resourceId = StableHash.Of("resource", package.StableId, current.NormalizedPath ?? current.RawHref, "");
                var supported = parameters.SupportedMediaTypes.Length == 0 || parameters.SupportedMediaTypes.Contains(current.MediaType, StringComparer.Ordinal);
                candidates.Add(new FallbackCandidate
                {
                    StableId = StableHash.Of("fallback-candidate", origin.StableId, hop.ToString(), current.StableId),
                    OriginManifestItemStableId = origin.StableId,
                    CandidateManifestItemStableId = current.StableId,
                    CandidateResourceStableId = resources.ContainsKey(resourceId) ? resourceId : "",
                    MediaType = current.MediaType,
                    Hop = hop,
                    IsLegalCandidate = current.NormalizedPath is not null && resources.ContainsKey(resourceId) && supported,
                    IsSelectedByRules = false,
                    Reason = supported ? "reachable fallback candidate" : "candidate media type is outside supported parameter set"
                });
                currentId = current.FallbackManifestId;
            }

            var legal = candidates.Where(candidate => candidate.IsLegalCandidate).ToArray();
            if (legal.Length == 1) candidates[candidates.IndexOf(legal[0])] = legal[0] with { IsSelectedByRules = true };
            if (legal.Length > 1)
                diagnostics.Add(new Diagnostic { Code = "FALLBACK-AMBIGUOUS-CANDIDATES", Severity = DiagnosticSeverity.Warning, Category = DiagnosticCategory.Semantics, Message = "More than one legal fallback candidate remains; every legal candidate is retained instead of choosing an implicit default.", Location = origin.Location, AffectedStableId = origin.StableId, RawValue = string.Join(",", legal.Select(c => c.Hop.ToString())) });
            chains.Add(new FallbackChainStatus { OriginManifestItemStableId = origin.StableId, Candidates = candidates, HasCycle = hasCycle, HasMissingResource = missingId is not null, Cycle = hasCycle ? pathIds.Skip(pathIds.IndexOf(currentId!)).ToArray() : Array.Empty<string>(), MissingManifestId = missingId });
        }
    }
}
