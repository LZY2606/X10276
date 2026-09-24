using System.Text;
using System.Xml.Linq;

namespace EpubReview.Core;

public sealed class ReviewAnalyzer
{
    public const string RuleSetVersion = "epub-review-rules:2";

    public ReviewSnapshot Analyze(
        EpubMaterials materials,
        ReviewParameters parameters,
        string scope,
        IEnumerable<string> parentDependencyEventIds,
        IEnumerable<ReviewDecision> decisions,
        DateTime nowUtc)
    {
        var diagnostics = new List<Diagnostic>();
        var entryByPath = materials.Entries.ToDictionary(e => e.Path, StringComparer.Ordinal);
        var entryPaths = materials.Entries.Select(e => e.Path).ToArray();
        var resources = new List<ResourceNode>();
        var refs = new List<ReferenceRecord>();
        var anchors = new List<AnchorRecord>();
        var nav = new List<NavNode>();
        var fallbackCandidates = new List<FallbackCandidate>();

        var itemByPackageId = new Dictionary<string, ManifestItem>();
        var manifestPathById = new Dictionary<string, (string PackageId, string Path)>();
        var spineItemPaths = new HashSet<(string PackageId, string Path)>();

        foreach (var package in materials.Packages)
        {
            foreach (var item in package.Manifest)
            {
                itemByPackageId[$"{package.StableId}:{item.Id}"] = item;
                var resolved = ContainerPaths.Resolve(ContainerPaths.Directory(package.EntryPath), item.Href, parameters.CaseSensitivePaths, entryPaths);
                var path = resolved.NormalizedPath ?? item.Href.Split('#')[0];
                manifestPathById[$"{package.StableId}:{item.Id}"] = (package.StableId, path);
            foreach (var spine in package.Spine)
            {
                if (manifestPathById.TryGetValue($"{package.StableId}:{spine.IdRef}", out var path))
                {
                    spineItemPaths.Add(path);
                }
            }
        }

        AddResources(materials, parameters, entryByPath, manifestPathById, spineItemPaths, resources, diagnostics);

        var edgeBudget = parameters.MaxGraphEdges;
        foreach (var package in materials.Packages)
        {
            AnalyzeManifestReferences(package, parameters, entryPaths, resources, refs, diagnostics, ref edgeBudget);
            AnalyzeSpineReferences(package, parameters, resources, refs, diagnostics, ref edgeBudget);
            AnalyzeContentReferences(package, parameters, entryByPath, resources, refs, anchors, nav, diagnostics, ref edgeBudget);
            AnalyzeFallbacks(package, parameters, resources, fallbackCandidates, diagnostics);
        }

        VerifyFragments(parameters, resources, refs, anchors, diagnostics);
        VerifyMediaOverlays(parameters, resources, refs, anchors, diagnostics);
        diagnostics.AddRange(materials.ParseDiagnostics);

        var readingOrder = BuildReadingOrder(materials, manifestPathById, diagnostics);
        var decisionMap = decisions.ToDictionary(d => d.SubjectStableId, StringComparer.Ordinal);
        fallbackCandidates = fallbackCandidates
            .Select(candidate => decisionMap.TryGetValue(candidate.StableId, out var decision) && decision.Adopted
                ? candidate with { Adopted = true }
                : candidate)
            .ToList();

        var allDiagnostics = ApplyDecisions(diagnostics, decisionMap).OrderBy(d => d.RuleId).ThenBy(d => d.Location).ThenBy(d => d.StableId).ToList();
        var outcome = DetermineOutcome(materials, allDiagnostics);
        var positions = LayoutPositions(resources, refs, parameters);
        var materialHashes = new Dictionary<string, string>
        {
            ["archive"] = materials.ArchiveSha256,
            ["container"] = materials.Container.Sha256,
            ["materials"] = StableIdentity.Short("material-version", $"{materials.MaterialId}:{materials.Version}:{materials.DirectSource}:{materials.ArchiveSha256}").ToString()
        };
        foreach (var package in materials.Packages)
        {
            materialHashes[$"package:{package.EntryPath}"] = package.Sha256;
        }

        var inputSummary = new InputSummary(StableIdentity.Sha256(materials.OriginalArchiveBytes), materials.OriginalArchiveBytes.Length, "byte", materialHashes);
        var basis = new Dictionary<string, string>
        {
            ["coordinateBasis"] = parameters.CoordinateBasis,
            ["timeBasis"] = parameters.TimeBasis,
            ["lengthUnit"] = parameters.LengthUnit,
            ["effectiveFromUtc"] = parameters.EffectiveFromUtc.ToString("O"),
            ["effectiveToUtc"] = parameters.EffectiveToUtc?.ToString("O") ?? "open",
            ["identityBasis"] = "Stable IDs derive from container-relative path, package path, manifest id and source location; export preserves explicit IDs."
        };
        var orderedParent = parentDependencyEventIds.OrderBy(x => x).ToArray();
        var snapshotId = StableIdentity.Short("snapshot", $"{materials.ArchiveSha256}:{RuleSetVersion}:p{parameters.Version}:{scope}:{string.Join(',', orderedParent)}");

        return new ReviewSnapshot(
            snapshotId,
            string.Empty,
            scope,
            outcome,
            inputSummary,
            parameters.Version,
            orderedParent,
            RuleSetVersion,
            nowUtc,
            resources.OrderBy(r => r.PackageStableId).ThenBy(r => r.EntryPath).ThenBy(r => r.StableId).ToList(),
            readingOrder,
            refs.OrderBy(r => r.SourceEntryPath).ThenBy(r => r.Position).ThenBy(r => r.StableId).ToList(),
            anchors.OrderBy(a => a.EntryPath).ThenBy(a => a.Position).ThenBy(a => a.StableId).ToList(),
            nav.OrderBy(n => n.Ordinal).ThenBy(n => n.StableId).ToList(),
            materials.Packages.SelectMany(p => p.UnknownMetadata.Select(m => m with { PackageId = p.StableId })).OrderBy(m => m.PackageId).ThenBy(m => m.Location).ToList(),
            fallbackCandidates.OrderBy(c => c.SourceItemStableId).ThenBy(c => c.Distance).ThenBy(c => c.StableId).ToList(),
            allDiagnostics,
            materials.ParseDiagnostics.OrderBy(d => d.Location).ThenBy(d => d.StableId).ToList(),
            positions,
            basis);
    }

    private static ReviewOutcome DetermineOutcome(EpubMaterials materials, IReadOnlyList<Diagnostic> diagnostics)
    {
        if (diagnostics.Any(d => d.FailureReason == "ResourceBudgetExhausted")) return ReviewOutcome.ResourceBudgetExhausted;
        if (materials.ParseDiagnostics.Any(d => d.FailureReason == "FormatCorrupt") || diagnostics.Any(d => d.FailureReason == "FormatCorrupt")) return ReviewOutcome.FormatCorrupt;
        if (diagnostics.Any(d => d.Severity == Severity.Error)) return ReviewOutcome.SemanticUnresolved;
        return ReviewOutcome.Accepted;
    }

    private static IReadOnlyList<Diagnostic> ApplyDecisions(List<Diagnostic> diagnostics, IReadOnlyDictionary<string, ReviewDecision> decisions)
    {
        return diagnostics.Select(diagnostic =>
        {
            if (!decisions.TryGetValue(diagnostic.StableId, out var decision)) return diagnostic;
            var evidence = new Dictionary<string, string>(diagnostic.Evidence)
            {
                ["decisionEventId"] = decision.EventId,
                ["decisionKind"] = decision.Kind,
                ["decisionAdopted"] = decision.Adopted.ToString().ToLowerInvariant()
            };
            return diagnostic with
            {
                Severity = decision.Adopted ? Severity.Info : diagnostic.Severity,
                Evidence = evidence,
                FailureReason = decision.Adopted ? "AcceptedByDecision" : diagnostic.FailureReason
            };
        }).ToList();
    }

    private static void AddResources(
        EpubMaterials materials,
        ReviewParameters parameters,
        IReadOnlyDictionary<string, ZipEntryRecord> entryByPath,
        IReadOnlyDictionary<string, (string PackageId, string Path)> manifestPathById,
        HashSet<(string PackageId, string Path)> spineItemPaths,
        List<ResourceNode> resources,
        List<Diagnostic> diagnostics)
    {
        foreach (var package in materials.Packages)
        {
            foreach (var item in package.Manifest)
            {
                var itemId = StableIdentity.Short("item", $"{package.StableId}:{item.Id}");
                var resolution = ContainerPaths.Resolve(ContainerPaths.Directory(package.EntryPath), item.Href, parameters.CaseSensitivePaths, entryByPath.Keys);
                var path = resolution.NormalizedPath ?? item.Href.Split('#')[0];
                if (resolution.IsMalformed || resolution.EscapedContainer)
                {
                    diagnostics.Add(resolution.EscapedContainer
                        ? DiagnosticFactory.Semantic("path.escape-container", "Normalized path escapes the EPUB container.", item.Location, itemId, item.Href, new Dictionary<string, string> { ["rule"] = "container-path-no-escape" })
                        : DiagnosticFactory.Format("path.malformed", "Manifest href is malformed.", item.Location, item.Href, itemId));
                }

                var exists = resolution.LegalCandidates.Count == 1 && entryByPath.TryGetValue(resolution.LegalCandidates[0], out _);
                if (!resolution.EscapedContainer && !resolution.IsMalformed && !exists)
                {
                    diagnostics.Add(DiagnosticFactory.Semantic("resource.missing", "Manifest resource is missing from the ZIP container.", item.Location, itemId, item.Href, new Dictionary<string, string> { ["normalizedPath"] = path, ["unit"] = parameters.LengthUnit }));
                }

                ZipEntryRecord? entry = exists ? entryByPath[resolution.LegalCandidates[0]] : null;
                resources.Add(new ResourceNode(
                    itemId, package.StableId, path, item.Id, item.MediaType,
                    entry?.OriginalMediaHint ?? "application/octet-stream", entry?.UncompressedLength ?? 0,
                    parameters.LengthUnit, entry?.Sha256 ?? "missing", exists,
                    spineItemPaths.Contains((package.StableId, path)),
                    resolution.LegalCandidates.Count > 1 ? resolution.LegalCandidates : (IReadOnlyList<string>)new[] { path }));
            }
        }
    }

    private static void AnalyzeManifestReferences(
        PackageDocument package,
        ReviewParameters parameters,
        IReadOnlyCollection<string> entryPaths,
        List<ResourceNode> resources,
        List<ReferenceRecord> references,
        List<Diagnostic> diagnostics,
        ref int edgeBudget)
    {
        var itemById = package.Manifest.ToDictionary(i => i.Id);
        foreach (var item in package.Manifest)
        {
            var sourceId = StableIdentity.Short("item", $"{package.StableId}:{item.Id}");
            if (!string.IsNullOrWhiteSpace(item.FallbackId))
            {
                AddReference(package, parameters, entryPaths, resources, references, diagnostics, ref edgeBudget,
                    sourceId, package.EntryPath, "opf:fallback", $"#{item.FallbackId}", null, item.Id, item.Location);
                if (!itemById.ContainsKey(item.FallbackId))
                {
                    diagnostics.Add(DiagnosticFactory.Semantic("fallback.missing-item", "fallback idref points to a missing manifest item.", item.Location, sourceId, item.FallbackId, new Dictionary<string, string> { ["rule"] = "fallback-cycle-and-missing-separated" }));
                }
            }
            if (!string.IsNullOrWhiteSpace(item.MediaOverlayId))
            {
                AddReference(package, parameters, entryPaths, resources, references, diagnostics, ref edgeBudget,
                    sourceId, package.EntryPath, "opf:media-overlay", $"#{item.MediaOverlayId}", null, item.Id, item.Location);
                if (!itemById.ContainsKey(item.MediaOverlayId))
                {
                    diagnostics.Add(DiagnosticFactory.Semantic("media-overlay.broken-idref", "media-overlay idref points to a missing manifest item.", item.Location, sourceId, item.MediaOverlayId, new Dictionary<string, string> { ["rule"] = "media-overlay-broken-chain" }));
                }
                else if (itemById[item.MediaOverlayId].MediaType != "application/smil+xml")
                {
                    diagnostics.Add(DiagnosticFactory.Semantic("media-overlay.wrong-media-type", "media-overlay target is not a SMIL document.", item.Location, sourceId, itemById[item.MediaOverlayId].MediaType, new Dictionary<string, string> { ["expectedMediaType"] = "application/smil+xml" }));
                }
            }
        }
    }

    private static void AnalyzeSpineReferences(
        PackageDocument package,
        ReviewParameters parameters,
        List<ResourceNode> resources,
        List<ReferenceRecord> references,
        List<Diagnostic> diagnostics,
        ref int edgeBudget)
    {
        var itemIds = package.Manifest.Select(i => i.Id).ToHashSet();
        foreach (var spine in package.Spine)
        {
            var sourceId = StableIdentity.Short("package", package.EntryPath);
            if (!itemIds.Contains(spine.IdRef))
            {
                diagnostics.Add(DiagnosticFactory.Semantic("spine.missing-idref", "spine item references a manifest id that does not exist.", spine.Location, sourceId, spine.IdRef));
            }
            AddReference(package, parameters, new[] { package.EntryPath }, resources, references, diagnostics, ref edgeBudget,
                sourceId, package.EntryPath, "opf:spine-idref", $"#{spine.IdRef}", null, spine.IdRef, spine.Location);
        }
    }

    private static void AddReference(
        PackageDocument package,
        ReviewParameters parameters,
        IReadOnlyCollection<string> entryPaths,
        List<ResourceNode> resources,
        List<ReferenceRecord> references,
        List<Diagnostic> diagnostics,
        ref int edgeBudget,
        string sourceStableId,
        string sourceEntryPath,
        string attribute,
        string rawReference,
        string? targetManifestItemId,
        string fallbackCandidateItemId,
        string location)
    {
        if (edgeBudget <= 0)
        {
            diagnostics.Add(DiagnosticFactory.Budget("budget.graph-edges", "Resource dependency edge budget is exhausted.", location, rawReference));
            return;
        }
        edgeBudget--;

        var fragment = rawReference.Contains('#') ? rawReference[(rawReference.IndexOf('#') + 1)..] : null;
        string? targetId = null;
        string? normalizedPath = null;
        if (rawReference.StartsWith('#'))
        {
            var targetItem = resources.FirstOrDefault(r => r.PackageStableId == package.StableId && r.ManifestItemId == rawReference[1..]);
            targetId = targetItem?.StableId;
            normalizedPath = sourceEntryPath;
        }
        else
        {
            var resolution = ContainerPaths.Resolve(ContainerPaths.Directory(sourceEntryPath), rawReference, parameters.CaseSensitivePaths, entryPaths);
            normalizedPath = resolution.NormalizedPath;
            var targetItem = resources.FirstOrDefault(r => r.PackageStableId == package.StableId && r.EntryPath == normalizedPath);
            targetId = targetItem?.StableId ?? (resolution.NormalizedPath is null ? null : StableIdentity.Short("missing-resource", $"{package.StableId}:{resolution.NormalizedPath}"));
            if (resolution.EscapedContainer)
            {
                diagnostics.Add(DiagnosticFactory.Semantic("path.escape-container", "Normalized reference escapes the EPUB container.", location, sourceStableId, rawReference, new Dictionary<string, string> { ["rule"] = "container-path-no-escape" }));
            }
        }

        var referenceKind = attribute switch
        {
            "opf:fallback" => "fallback",
            "opf:media-overlay" => "media-overlay",
            "opf:spine-idref" => "spine",
            "xlink:href" => "smil-media",
            "href" => "content",
            _ => "reference"
        };
        var id = StableIdentity.Short("ref", $"{sourceStableId}:{attribute}:{rawReference}:{location}");
        references.Add(new ReferenceRecord(id, sourceStableId, sourceEntryPath, attribute, rawReference, normalizedPath ?? string.Empty, fragment, targetId ?? "missing", referenceKind, references.Count, location));
    }

    private static void AnalyzeContentReferences(
        PackageDocument package,
        ReviewParameters parameters,
        IReadOnlyCollection<string> entryPaths,
        List<ResourceNode> resources,
        List<ReferenceRecord> references,
        List<AnchorRecord> anchors,
        List<NavNode> nav,
        List<Diagnostic> diagnostics,
        ref int edgeBudget)
    {
        var itemByPath = package.Manifest.ToDictionary(i => ContainerPaths.Resolve(ContainerPaths.Directory(package.EntryPath), i.Href, true, entryPaths).NormalizedPath ?? i.Href);
        foreach (var resource in resources.Where(r => r.PackageStableId == package.StableId && r.Exists))
        {
            itemByPath.TryGetValue(resource.EntryPath, out var manifestItem);
            entryByPath.TryGetValue(resource.EntryPath, out var zipEntry);
            var bytes = zipEntry?.Bytes;
            var mediaType = resource.InferredMediaType;
            if (bytes is null || mediaType is not ("application/xhtml+xml" or "image/svg+xml" or "application/smil+xml"))
            {
                continue;
            }

            XDocument doc;
            try
            {
                doc = ZipEpubReader.LoadXml(bytes);
            }
            catch (System.Xml.XmlException ex)
            {
                diagnostics.Add(DiagnosticFactory.Format("content.xml-corrupt", $"Referenced XML resource is not well-formed: {ex.Message}", $"{resource.EntryPath}:line{ex.LineNumber}:col{ex.LinePosition}", resource.StableId, resource.EntryPath));
                continue;
            }

            CollectAnchors(resource, anchors, doc);
            var sourceId = resource.StableId;
            foreach (var element in doc.Descendants())
            {
                foreach (var attribute in new[] { "href", "src" })
                {
                    var value = element.Attribute(attribute)?.Value;
                    if (!string.IsNullOrWhiteSpace(value) && !value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        AddReference(package, parameters, entryPaths, resources, references, diagnostics, ref edgeBudget,
                            sourceId, resource.EntryPath, attribute, value, null, manifestItem?.Id ?? string.Empty, Location(element));
                    }
                }

                var xlink = element.Attribute(ZipEpubReader.XlinkNs + "href")?.Value;
                if (!string.IsNullOrWhiteSpace(xlink))
                {
                    AddReference(package, parameters, entryPaths, resources, references, diagnostics, ref edgeBudget,
                        sourceId, resource.EntryPath, "xlink:href", xlink, null, manifestItem?.Id ?? string.Empty, Location(element));
                }
            }

            if (mediaType == "application/xhtml+xml")
            {
                CollectNavigation(package, resource, doc, nav);
            }
        }
    }

    private static void CollectAnchors(ResourceNode resource, List<AnchorRecord> anchors, XDocument doc)
    {
        var position = 0;
        foreach (var element in doc.Descendants())
        {
            var name = element.Attribute("id")?.Value ?? element.Attribute("name")?.Value;
            if (!string.IsNullOrWhiteSpace(name))
            {
                var id = StableIdentity.Short("anchor", $"{resource.StableId}:{name}");
                anchors.Add(new AnchorRecord(id, resource.StableId, resource.EntryPath, name, position++, Location(element)));
            }
        }
    }

    private static void CollectNavigation(PackageDocument package, ResourceNode resource, XDocument doc, List<NavNode> nav)
    {
        var epubNs = XNamespace.Get("http://www.idpf.org/2007/ops");
        var toc = doc.Descendants().FirstOrDefault(e => (e.Attribute(epubNs + "type")?.Value ?? string.Empty).Split(' ').Contains("toc", StringComparer.OrdinalIgnoreCase));
        if (toc is null) return;
        var ordinal = 0;
        foreach (var anchor in toc.Descendants(XNamespace.Get("http://www.w3.org/1999/xhtml") + "a"))
        {
            var depth = ancestorLiDepth(anchor);
            var raw = anchor.Attribute("href")?.Value;
            var fragment = raw?.Contains('#') == true ? raw[(raw.IndexOf('#') + 1)..] : null;
            var path = raw?.Split('#')[0] is { Length: > 0 } p
                ? ContainerPaths.Resolve(ContainerPaths.Directory(resource.EntryPath), p, true, new[] { resource.EntryPath }).NormalizedPath
                : resource.EntryPath;
            nav.Add(new NavNode(
                StableIdentity.Short("nav", $"{package.StableId}:{resource.EntryPath}:{ordinal}"),
                package.StableId,
                null,
                depth,
                ordinal++,
                string.Join(' ', anchor.Nodes().Where(n => n is XText).Select(n => ((XText)n).Value.Trim()).Where(x => x.Length > 0)),
                raw,
                StableIdentity.Short("resource", $"{package.StableId}:{path}"),
                fragment));
        }

        int ancestorLiDepth(XObject node)
        {
            var depth = 0;
            var current = node.Parent;
            while (current is not null)
            {
                if (current.Name.LocalName == "li") depth++;
                current = current.Parent;
            }
            return depth;
        }
    }

    private static string Location(XObject element)
    {
        var info = element as System.Xml.IXmlLineInfo;
        return info?.HasLineInfo() == true ? $"line:{info.LineNumber},column:{info.LinePosition}" : "xml";
    }
