using EpubReview.Core;

namespace EpubReview.Storage;

public sealed record ReviewSnapshot(
    long StateVersion,
    string CurrentEventId,
    IReadOnlyList<EpubMaterial> Materials,
    IReadOnlyList<RuleProfile> Profiles,
    IReadOnlyList<AnalysisResult> Analyses,
    IReadOnlyList<ReviewDecision> Decisions,
    IReadOnlyList<EvidenceEvent> Events);

internal sealed record ExportEnvelope(
    int bundleVersion,
    string? exportedAtInstant,
    string? contentSha256,
    JsonElement[]? materials,
    JsonElement[]? profiles,
    JsonElement[]? analyses,
    JsonElement[]? decisions,
    JsonElement[]? events);
