using System.Text.Json;
using SourceMapChain.Core;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<WorkspaceStore>(_ =>
{
    var dataDirectory = Path.Combine(builder.Environment.ContentRootPath, "data");
    return new WorkspaceStore(Path.Combine(dataDirectory, "workspace.json"));
});
builder.Services.AddSingleton<WorkspaceService>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

using (var scope = app.Services.CreateScope())
{
    var service = scope.ServiceProvider.GetRequiredService<WorkspaceService>();
    var document = await service.GetAsync();
    if (document.Stages.Count == 0)
    {
        await service.ImportBatchAsync("seed-chain-v1", SampleData.Stages());
    }
    await service.RecoverJobsAsync(job => Task.FromResult(job.Kind + ":recovered-once"));
}

app.MapGet("/api/workspace", async (WorkspaceService service) =>
{
    var document = await service.GetAsync();
    var composer = new SourceMapComposer(document.Stages, document.Revisions);
    return Results.Ok(new
    {
        document.SchemaVersion,
        document.WorkspaceId,
        document.State,
        document.RuleVersion,
        document.CreatedAt,
        document.UpdatedAt,
        stages = document.Stages.Select(stage => new
        {
            stage.Id,
            stage.Label,
            outputText = stage.OutputText,
            mapJson = stage.MapJson,
            outputSha256 = TextLines.Sha256Fingerprint(stage.OutputText),
            mapSha256 = TextLines.Sha256Fingerprint(stage.MapJson)
        }),
        revisions = document.Revisions,
        publications = document.Publications,
        events = document.Events,
        jobs = document.Jobs,
        validation = composer.Validate(),
        deterministicSummary = composer.DeterministicSummary()
    });
});

app.MapPost("/api/import", async (ImportRequest request, WorkspaceService service) =>
{
    var result = await service.ImportBatchAsync(request.IdempotencyKey ?? Guid.NewGuid().ToString("N"), request.Stages ?? []);
    return result.Applied || result.Duplicate
        ? Results.Ok(new { result.Applied, result.Duplicate, result.AddedStages })
        : Results.BadRequest(new { result.Applied, result.Duplicate, error = result.Error });
});

app.MapPost("/api/trace", (TraceRequest request) =>
{
    var composer = BuildComposer(request.Workspace);
    return Results.Ok(composer.TraceFinalPosition(request.Line, request.Column, request.ColumnEncoding ?? "utf16"));
});

app.MapPost("/api/reverse", (ReverseRequest request) =>
{
    var composer = BuildComposer(request.Workspace);
    return Results.Ok(composer.ReverseImpact(
        request.SourcePath ?? string.Empty, request.StartLine, request.StartColumn,
        request.EndLine, request.EndColumn, request.ColumnEncoding ?? "utf16"));
});

app.MapPost("/api/revisions", async (RevisionProposal proposal, WorkspaceService service) =>
{
    proposal.IdempotencyKey = string.IsNullOrWhiteSpace(proposal.IdempotencyKey) ? Guid.NewGuid().ToString("N") : proposal.IdempotencyKey;
    var result = await service.AddRevisionAsync(proposal);
    return result.Error is null ? Results.Ok(new { revision = result.Revision, result.Duplicate }) : Results.BadRequest(new { error = result.Error });
});

app.MapPost("/api/revisions/{id}/compare", async (string id, WorkspaceService service) =>
{
    var document = await service.GetAsync();
    var proposal = document.Revisions.FirstOrDefault(item => item.Id == id);
    return proposal is null ? Results.NotFound() : Results.Ok(new SourceMapComposer(document.Stages, document.Revisions.Where(item => item.Id != id).ToList()).CompareRevision(proposal));
});

app.MapPost("/api/revisions/merge", async (MergeRequest request, WorkspaceService service) =>
{
    var document = await service.GetAsync();
    var composer = new SourceMapComposer(document.Stages, document.Revisions.Where(revision => revision.Id != request.FirstRevisionId && revision.Id != request.SecondRevisionId).ToList());
    return Results.Ok(composer.MergeRevisions(request.FirstRevisionId, request.SecondRevisionId, document.Revisions));
});

app.MapPost("/api/publish", async (PublishRequest request, WorkspaceService service) =>
    Results.Ok(await service.PublishAsync(request.IdempotencyKey ?? throw new InvalidOperationException("idempotencyKey is required"), request.RevisionIds ?? [])));

app.MapPost("/api/jobs/recover", async (JobRequest request, WorkspaceService service) =>
{
    var enqueued = await service.EnqueueOrGetJobAsync(request.IdempotencyKey ?? Guid.NewGuid().ToString("N"), request.Kind ?? "fingerprint-scan");
    if (!enqueued.Duplicate)
    {
        await service.CompleteJobAsync(enqueued.Job.IdempotencyKey, "fingerprints-verified-once");
    }
    return Results.Ok((await service.GetAsync()).Jobs);
});

app.MapGet("/api/export", async (WorkspaceService service) =>
    Results.Text(JsonSerializer.Serialize(await service.GetAsync(), jsonOptions), "application/json"));

app.MapPost("/api/export/import", async (WorkspaceDocument document, WorkspaceService service) =>
{
    var composer = new SourceMapComposer(document.Stages, document.Revisions);
    composer.Validate();
    await service.SaveAsync(document);
    return Results.Ok(new { deterministicSummary = composer.DeterministicSummary(), document.SchemaVersion });
});

app.Run();

static SourceMapComposer BuildComposer(WorkspaceDocument? workspace) =>
    workspace?.Stages is { Count: > 0 }
        ? new SourceMapComposer(workspace.Stages, workspace.Revisions ?? [])
        : new SourceMapComposer(SampleData.Stages());

public sealed class ImportRequest { public string? IdempotencyKey { get; set; } public List<StageInput>? Stages { get; set; } }
public class TraceRequest { public int Line { get; set; } public int Column { get; set; } public string? ColumnEncoding { get; set; } public WorkspaceDocument? Workspace { get; set; } }
public sealed class ReverseRequest : TraceRequest
{
    public string? SourcePath { get; set; }
    public int StartLine { get; set; }
    public int StartColumn { get; set; }
    public int EndLine { get; set; }
    public int EndColumn { get; set; }
}
public sealed class MergeRequest { public string FirstRevisionId { get; set; } = string.Empty; public string SecondRevisionId { get; set; } = string.Empty; }
public sealed class PublishRequest { public string? IdempotencyKey { get; set; } public List<string>? RevisionIds { get; set; } }
public sealed class JobRequest { public string? IdempotencyKey { get; set; } public string? Kind { get; set; } }
