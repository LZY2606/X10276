using EpubReview.Domain;
using EpubReview.Web.Services;

namespace EpubReview.Tests;

public sealed class PersistenceTests
{
    [Fact]
    public void SeededTimeline_ReplaysHistoricalDependencyVersions()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlite");
        try
        {
            var database = new Database(path);
            database.Initialize();
            var service = new ReviewService(new ReviewRepository(database));
            service.EnsureSeeded();
            var events = service.Timeline(SampleCase.CaseId);
            var firstAnalysis = events.First(e => e.Type == "analysis-completed");
            var finalAnalysis = events.Last(e => e.Type == "analysis-completed");
            var first = service.ResultAt(SampleCase.CaseId, firstAnalysis.EventId);
            var final = service.ResultAt(SampleCase.CaseId, finalAnalysis.EventId);

            Assert.NotEqual(first.DependencyVersion, final.DependencyVersion);
            Assert.Equal(finalAnalysis.DependencyVersion, final.DependencyVersion);
            Assert.Equal(firstAnalysis.DependencyVersion, first.DependencyVersion);
            Assert.True(final.Decisions.Count >= 2);
            Assert.Equal(AnalyzeOutcome.SemanticallyUnresolvable, final.Outcome);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ExportDeleteImport_PreservesIdentitiesOrderAndMaterials()
    {
        var originalPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlite");
        var restoredPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlite");
        try
        {
            var originalDb = new Database(originalPath);
            originalDb.Initialize();
            var original = new ReviewService(new ReviewRepository(originalDb));
            original.EnsureSeeded();
            var export = original.Export(SampleCase.CaseId);

            var restoredDb = new Database(restoredPath);
            restoredDb.Initialize();
            var restored = new ReviewService(new ReviewRepository(restoredDb));
            restored.ImportExternal(export);
            var originalEvents = original.Timeline(SampleCase.CaseId);
            var restoredEvents = restored.Timeline(SampleCase.CaseId);

            Assert.Equal(originalEvents.Select(e => e.EventId), restoredEvents.Select(e => e.EventId));
            Assert.Equal(originalEvents.Select(e => e.Sequence), restoredEvents.Select(e => e.Sequence));
            Assert.Equal(originalEvents.Select(e => e.DependencyVersion), restoredEvents.Select(e => e.DependencyVersion));
            Assert.Equal(original.Export(SampleCase.CaseId).Materials.Select(m => m.MaterialId), restored.Export(SampleCase.CaseId).Materials.Select(m => m.MaterialId));
        }
        finally { File.Delete(originalPath); File.Delete(restoredPath); }
    }

    [Fact]
    public void InvalidImport_IsRejectedAndDatabaseRemainsUsable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlite");
        try
        {
            var database = new Database(path);
            database.Initialize();
            var service = new ReviewService(new ReviewRepository(database));
            service.EnsureSeeded();
            var before = service.Timeline(SampleCase.CaseId).Count;
            var invalid = new ProjectExport("epub-review/offline/1", new ProjectCase("x", "x", "e", "t"), [], []);
            Assert.Throws<InvalidDataException>(() => service.ImportExternal(invalid));
            Assert.Equal(before, service.Timeline(SampleCase.CaseId).Count);
        }
        finally { File.Delete(path); }
    }
}

public sealed class HalfWriteRecoveryTests
{
    [Fact]
    public void StaleOperationLedger_IsMarkedRecoveredOnNextOpen()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlite");
        try
        {
            var database = new Database(path);
            database.Initialize();
            using (var connection = database.Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "INSERT INTO operation_ledger(operation_id,case_id,started_at,status) VALUES($id,$case,$at,'started')";
                command.Parameters.AddWithValue("$id", "simulated-half-write");
                command.Parameters.AddWithValue("$case", SampleCase.CaseId);
                command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
                command.ExecuteNonQuery();
            }

            new Database(path).Initialize();

            using var verifyConnection = database.Open();
            using var verify = verifyConnection.CreateCommand();
            verify.CommandText = "SELECT status, detail FROM operation_ledger WHERE operation_id='simulated-half-write'";
            using var reader = verify.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("recovered", reader.GetString(0));
            Assert.Contains("rolled back", reader.GetString(1));
        }
        finally { File.Delete(path); }
    }
}

public sealed class DynamicActionTests
{
    [Fact]
    public void PageDecision_CreatesNewConflictEventWithoutOverwritingHistory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sqlite");
        try
        {
            var database = new Database(path);
            database.Initialize();
            var service = new ReviewService(new ReviewRepository(database));
            service.EnsureSeeded();
            var before = service.Timeline(SampleCase.CaseId).Count;
            var recorded = service.RecordDecision(SampleCase.CaseId, "PATH_NAV_ESCAPE", DecisionAction.Accepted, "页面接受一个越界引用问题。");
            service.RecordDecision(SampleCase.CaseId, "PATH_NAV_ESCAPE", DecisionAction.Rejected, "页面随后否定同一问题。");
            var after = service.Timeline(SampleCase.CaseId);
            var finalEvent = after.Last(e => e.DependencyVersion is not null).EventId;
            var result = service.ResultAt(SampleCase.CaseId, finalEvent);

            Assert.Equal(before + 4, after.Count);
            Assert.Contains(result.Diagnostics, d => d.Code == "CONFLICTING_DECISION" && d.SubjectId == "PATH_NAV_ESCAPE");
            Assert.Equal(AnalyzeOutcome.SemanticallyUnresolvable, result.Outcome);
        }
        finally { File.Delete(path); }
    }
}
