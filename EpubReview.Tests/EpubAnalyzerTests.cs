using EpubReview.Domain;

namespace EpubReview.Tests;

public sealed class EpubAnalyzerTests
{
    [Fact]
    public void Sample_CoversRequiredEpubBoundaryConditions()
    {
        var result = new EpubAnalyzer().Analyze(new(SampleCase.BuildArchive(), new AnalysisParameters()));

        Assert.Equal(2, result.Packages.Count);
        Assert.Contains(result.Fallback, f => f.Status == LinkStatus.FallbackCycle);
        Assert.Contains(result.Diagnostics, d => d.Code == "MISSING_FALLBACK_TARGET");
        Assert.Contains(result.References, e => e.Status == LinkStatus.MissingFragment);
        Assert.Contains(result.References, e => e.Status == LinkStatus.PathEscapesContainer);
        Assert.Contains(result.Spine, s => s.IsDuplicate && s.DiagnosticCode == "DUPLICATE_SPINE_IDREF");
        Assert.Contains(result.Overlays, o => o.Status == LinkStatus.MissingOverlay);
        Assert.All(result.Packages, p => Assert.NotEmpty(p.UnknownMetadata));
        Assert.Equal(AnalyzeOutcome.SemanticallyUnresolvable, result.Outcome);
    }

    [Fact]
    public void EquivalentInputs_GetStableOrderAndIdentities()
    {
        var analyzer = new EpubAnalyzer();
        var first = analyzer.Analyze(new(SampleCase.BuildArchive(), new AnalysisParameters()));
        var second = analyzer.Analyze(new(SampleCase.BuildArchive(), new AnalysisParameters()));

        Assert.Equal(first.DependencyVersion, second.DependencyVersion);
        Assert.Equal(first.References.Select(e => e.Id), second.References.Select(e => e.Id));
        Assert.Equal(first.Spine.Select(s => s.Id), second.Spine.Select(s => s.Id));
        Assert.Equal(first.Resources.Select(r => r.Id), second.Resources.Select(r => r.Id));
    }

    [Fact]
    public void PathTraversal_IsReportedSeparatelyFromMissingTarget()
    {
        var result = new EpubAnalyzer().Analyze(new(SampleCase.BuildArchive(), new AnalysisParameters()));
        var escape = Assert.Single(result.References.Where(e => e.Status == LinkStatus.PathEscapesContainer && e.RawTarget.Contains("outside-container") && e.Relation == "navigation:href"));
        Assert.Empty(escape.CandidateTargetIds);
        Assert.Contains(result.Judgments, j => j.Code == "CONTAINER_PATH_BOUNDARY");
    }

    [Fact]
    public void AmbiguousNormalization_RetainsAllLegalCandidates()
    {
        var result = new EpubAnalyzer().Analyze(new(SampleCase.BuildArchive(), new AnalysisParameters()));
        var ambiguous = result.References.First(e => e.RawTarget == "other/../data/note.txt");
        Assert.Equal(LinkStatus.AmbiguousResource, ambiguous.Status);
        Assert.Equal(2, ambiguous.CandidateTargetIds.Count);
    }

    [Fact]
    public void CorruptArchive_BudgetExhaustion_AndSemanticNoSolution_AreDistinct()
    {
        var corrupt = new EpubAnalyzer().Analyze(new(SampleCase.BuildCorruptArchive(), new AnalysisParameters()));
        var budget = new EpubAnalyzer().Analyze(new(SampleCase.BuildArchive(), new AnalysisParameters() with { MaxArchiveBytes = 1 }));
        var semantic = new EpubAnalyzer().Analyze(new(SampleCase.BuildArchive(), new AnalysisParameters()));

        Assert.Equal(AnalyzeOutcome.FormatCorrupt, corrupt.Outcome);
        Assert.Equal(AnalyzeOutcome.BudgetExhausted, budget.Outcome);
        Assert.Equal(AnalyzeOutcome.SemanticallyUnresolvable, semantic.Outcome);
        Assert.Contains(corrupt.Diagnostics, d => d.Location == "input:/archive");
        Assert.Contains(budget.Diagnostics, d => d.Code == "BUDGET_ARCHIVE_BYTES");
    }

    [Fact]
    public void RawBytesAndUnknownMetadata_ArePreservedInDerivedResult()
    {
        var result = new EpubAnalyzer().Analyze(new(SampleCase.BuildArchive(), new AnalysisParameters()));
        Assert.True(result.InputByteLength > 0);
        Assert.Contains(result.Entries, e => e.RawPath.Contains("..") && e.Bytes.Length > 0);
        Assert.All(result.Packages, p => Assert.All(p.UnknownMetadata, m => Assert.Contains("<", m.OuterXml)));
        Assert.All(result.Resources, r => Assert.Equal("byte", r.ByteUnit));
    }

    [Fact]
    public void MissingFragment_IsDiagnosedWithLocationAndStableSubject()
    {
        var result = new EpubAnalyzer().Analyze(new(SampleCase.BuildArchive(), new AnalysisParameters()));
        var diagnostic = Assert.Single(result.Diagnostics.Where(d => d.Code == "MISSING_NAV_FRAGMENT" && d.Location.Contains("nav.xhtml")));
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.Location));
        Assert.NotNull(diagnostic.SubjectId);
        Assert.StartsWith("nav:", diagnostic.SubjectId);
    }
}
