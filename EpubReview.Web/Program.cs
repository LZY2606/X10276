using System.Text.Json;
using EpubReview.Domain;
using EpubReview.Web.Services;

var builder = WebApplication.CreateBuilder(args);
var dataPath = Path.Combine(builder.Environment.ContentRootPath, "data", "epub-review.sqlite");
var database = new Database(dataPath);
database.Initialize();
builder.Services.AddSingleton(database);
builder.Services.AddSingleton<ReviewRepository>();
builder.Services.AddSingleton<ExportService>();
builder.Services.AddSingleton<ReviewService>();

var app = builder.Build();
app.Services.GetRequiredService<ReviewService>().EnsureSeeded();
app.UseStaticFiles();

app.MapGet("/", async (ReviewService service, HttpContext context) =>
{
    var events = service.Timeline(SampleCase.CaseId);
    var selectedEvent = context.Request.Query.TryGetValue("event", out var eventValue) ? eventValue.ToString() : events.Last(e => e.DependencyVersion is not null).EventId;
    var result = service.ResultAt(SampleCase.CaseId, selectedEvent);
    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.WriteAsync(ReviewPage.Render(SampleCase.Title, events, selectedEvent, result));
});

app.MapGet("/api/case", (ReviewService service) =>
{
    var events = service.Timeline(SampleCase.CaseId);
    var last = events.Last(e => e.DependencyVersion is not null).EventId;
    return Results.Json(new { caseId = SampleCase.CaseId, title = SampleCase.Title, events = events.Select(e => new { e.EventId, e.Sequence, e.Type, e.Status, e.ParametersVersion, e.DependencyVersion, e.ParentDependencyVersion, e.InputSha256 }), selectedEvent = last });
});

app.MapGet("/api/events/{eventId}", (string eventId, ReviewService service) => Results.Json(service.ResultAt(SampleCase.CaseId, eventId)));

app.MapGet("/api/events/{eventId}/graph.svg", (string eventId, ReviewService service) =>
{
    var result = service.ResultAt(SampleCase.CaseId, eventId);
    return Results.Content(SvgView.ResourceGraph(result), "image/svg+xml; charset=utf-8");
});

app.MapGet("/api/events/{eventId}/reading-order.svg", (string eventId, ReviewService service) =>
{
    var result = service.ResultAt(SampleCase.CaseId, eventId);
    return Results.Content(SvgView.ReadingOrder(result), "image/svg+xml; charset=utf-8");
});

app.MapGet("/api/compare/{leftEventId}/{rightEventId}", (string leftEventId, string rightEventId, ReviewService service) =>
{
    var left = service.ResultAt(SampleCase.CaseId, leftEventId);
    var right = service.ResultAt(SampleCase.CaseId, rightEventId);
    return Results.Json(new
    {
        left = new { leftEventId, left.DependencyVersion, left.Outcome, left.InputSha256, left.ParametersVersion, diagnostics = left.Diagnostics, resources = left.Resources, references = left.References, spine = left.Spine, navigation = left.Navigation, fallback = left.Fallback, overlays = left.Overlays },
        right = new { rightEventId, right.DependencyVersion, right.Outcome, right.InputSha256, right.ParametersVersion, diagnostics = right.Diagnostics, resources = right.Resources, references = right.References, spine = right.Spine, navigation = right.Navigation, fallback = right.Fallback, overlays = right.Overlays }
    });
});

app.MapGet("/api/export", (ReviewService service, ExportService exporter) =>
{
    var export = service.Export(SampleCase.CaseId);
    return Results.File(exporter.WriteToBytes(export), "application/zip", "epub-review-offline-case.epubreview.zip");
});

app.MapPost("/api/import", async (HttpContext context, ReviewService service, ExportService exporter) =>
{
    using var memory = new MemoryStream();
    await context.Request.Body.CopyToAsync(memory);
    var export = exporter.ReadProject(memory.ToArray());
    var caseId = service.ImportExternal(export);
    return Results.Json(new { caseId, events = export.Events.Count, materials = export.Materials.Count });
});

app.MapPost("/api/decisions", async (HttpContext context, ReviewService service) =>
{
    var form = await context.Request.ReadFormAsync();
    var subject = form["subject"].ToString();
    var action = Enum.Parse<DecisionAction>(form["action"].ToString(), ignoreCase: true);
    var rationale = form["rationale"].ToString();
    var recorded = service.RecordDecision(SampleCase.CaseId, subject, action, rationale);
    return Results.Redirect($"/?event={Uri.EscapeDataString(recorded.ResultEventId)}");
});

app.MapPost("/api/parameters", async (HttpContext context, ReviewService service) =>
{
    var form = await context.Request.ReadFormAsync();
    var recorded = service.RecordParameters(SampleCase.CaseId, long.Parse(form["maxArchiveBytes"]!), int.Parse(form["maxZipEntries"]!), form["requireFragmentTargets"] == "true", form["coordinateSpace"].ToString());
    return Results.Redirect($"/?event={Uri.EscapeDataString(recorded.ResultEventId)}");
});

app.Run();
