using System.Text.Json;
using SourceMapChains.Core;

var builder = WebApplication.CreateBuilder(args);
var dataDir = builder.Configuration["DATA_DIR"] ?? Path.Combine(builder.Environment.ContentRootPath, "data");
var store = new ProjectStore(dataDir);
var service = new ProjectService(store);
service.ResumeIncompleteJobs();
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

string? IdemKey(HttpRequest req) =>
    req.Headers.TryGetValue("Idempotency-Key", out var v) ? v.ToString() : null;

IResult Run(Func<object> work)
{
    try
    {
        return Results.Ok(work());
    }
    catch (ServiceException ex)
    {
        return Results.Json(new { error = ex.Code, message = ex.Message }, statusCode: 409);
    }
    catch (KeyNotFoundException ex)
    {
        return Results.Json(new { error = "NotFound", message = ex.Message }, statusCode: 404);
    }
    catch (FormatException ex)
    {
        return Results.Json(new { error = "BadFormat", message = ex.Message }, statusCode: 400);
    }
}

app.MapGet("/api/projects", () => Run(() => service.List().Select(p => new
{
    p.Id, p.Name, status = p.Status.ToString(), levels = p.Levels.Count,
    revisions = p.Revisions.Count, published = p.PublishedRevisionId,
})));

app.MapPost("/api/projects", async (HttpRequest req) =>
{
    var body = await req.ReadFromJsonAsync<Dictionary<string, JsonElement>>();
    var name = body is not null && body.TryGetValue("name", out var n) ? n.GetString() ?? "project" : "project";
    return Run(() => service.CreateProject(name, IdemKey(req)));
});

app.MapGet("/api/projects/{id}", (string id) => Run(() =>
{
    var p = service.Get(id) ?? throw new ServiceException("NotFound", $"Project '{id}' not found.");
    return (object)p;
}));

app.MapPost("/api/projects/{id}/imports", async (string id, HttpRequest req) =>
{
    var payload = await req.ReadFromJsonAsync<List<ImportLevelDto>>(JsonOptions.Default)
        ?? throw new ServiceException("BadFormat", "Expected a JSON array of levels.");
    return Run(() => service.ImportLevels(id, payload, IdemKey(req)));
});

app.MapPost("/api/projects/{id}/compose", async (string id, HttpRequest req) =>
{
    var body = await req.ReadFromJsonAsync<Dictionary<string, JsonElement>>();
    string? revisionId = body is not null && body.TryGetValue("revisionId", out var r) && r.ValueKind == JsonValueKind.String
        ? r.GetString() : null;
    return Run(() => service.Compose(id, revisionId, IdemKey(req)));
});

app.MapPost("/api/projects/{id}/publish", async (string id, HttpRequest req) =>
{
    var body = await req.ReadFromJsonAsync<Dictionary<string, JsonElement>>();
    string? revisionId = body is not null && body.TryGetValue("revisionId", out var r) && r.ValueKind == JsonValueKind.String
        ? r.GetString() : null;
    return Run(() => service.Publish(id, revisionId, IdemKey(req)));
});

app.MapGet("/api/projects/{id}/composition", (string id, string? revisionId) =>
    Run(() => service.GetCompositionView(id, revisionId)));

app.MapGet("/api/projects/{id}/trace", (string id, string path, int line, int col, string? revisionId) =>
    Run(() => service.Trace(id, path, line, col, revisionId)));

app.MapGet("/api/projects/{id}/reverse", (string id, int level, string path, int line, int col, int length, string? sourceRoot, string? revisionId) =>
    Run(() => service.Reverse(id, level, sourceRoot, path, line, col, length, revisionId)));

app.MapPost("/api/projects/{id}/revisions", async (string id, HttpRequest req) =>
{
    var body = await req.ReadFromJsonAsync<CreateRevisionRequest>(JsonOptions.Default)
        ?? throw new ServiceException("BadFormat", "Expected {name, edits}.");
    return Run(() => service.CreateRevision(id, body.Name, body.Edits, IdemKey(req)));
});

app.MapGet("/api/projects/{id}/compare", (string id, string revisionId) =>
    Run(() => service.CompareWithBase(id, revisionId)));

app.MapPost("/api/projects/{id}/merge", async (string id, HttpRequest req) =>
{
    var body = await req.ReadFromJsonAsync<Dictionary<string, JsonElement>>();
    var a = body!["a"].GetString()!;
    var b = body!["b"].GetString()!;
    return Run(() => service.MergeRevisions(id, a, b, IdemKey(req)));
});

app.MapGet("/api/projects/{id}/events", (string id) => Run(() =>
{
    var p = service.Get(id) ?? throw new ServiceException("NotFound", $"Project '{id}' not found.");
    return (object)p.Events.OrderBy(e => e.Seq).ToList();
}));

app.MapGet("/api/projects/{id}/export", (string id) =>
{
    try
    {
        return Results.Text(service.Export(id), "application/json");
    }
    catch (ServiceException ex)
    {
        return Results.Json(new { error = ex.Code, message = ex.Message }, statusCode: 409);
    }
});

app.MapPost("/api/import", async (HttpRequest req) =>
{
    using var reader = new StreamReader(req.Body);
    var json = await reader.ReadToEndAsync();
    return Run(() => service.Import(json, IdemKey(req)));
});

app.MapPost("/api/demo", (HttpRequest req) => Run(() => DemoProject.Create(service, IdemKey(req))));

app.Run();

public sealed record CreateRevisionRequest(string Name, List<MappingEdit> Edits);

/// <summary>Builds a 3-level demo project: TS source → transpiled JS → bundle.</summary>
public static class DemoProject
{
    public static object Create(ProjectService service, string? idemKey)
    {
        var project = service.CreateProject("demo-bundle", idemKey is null ? null : idemKey + ":create");

        var ts = "export function hi() {\n  return 'hi';\n}\n";
        var js = "export function hi() {\n  return 'hi';\n}\n";
        var bundle = "/* banner */\nexport function hi() {\n  return 'hi';\n}\n";

        string LineMap(int lines)
        {
            var parts = new List<string>();
            for (var i = 0; i < lines; i++)
                parts.Add(i == 0 ? Vlq.Encode(new[] { 0, 0, 0, 0 }) : Vlq.Encode(new[] { 0, 0, 1, 0 }));
            return string.Join(';', parts);
        }

        var map1 = JsonSerializer.Serialize(new
        {
            version = 3,
            file = "hello.js",
            sources = new[] { "src/hello.ts" },
            sourcesContent = new[] { ts },
            names = Array.Empty<string>(),
            mappings = LineMap(3),
        });
        var map2 = JsonSerializer.Serialize(new
        {
            version = 3,
            file = "bundle.js",
            sources = new[] { "out/hello.js" },
            sourcesContent = new[] { js },
            names = Array.Empty<string>(),
            mappings = ";" + LineMap(3),
        });

        var levels = new List<ImportLevelDto>
        {
            new("original", new List<ImportFileDto>
            {
                new("src/hello.ts", null, ts, "utf16"),
            }, null),
            new("transpiled", new List<ImportFileDto>
            {
                new("out/hello.js", null, js, "utf16"),
            }, new List<ImportMapDto>
            {
                new("out/hello.js", null, map1, null),
            }),
            new("bundle", new List<ImportFileDto>
            {
                new("dist/bundle.js", null, bundle, "utf16"),
            }, new List<ImportMapDto>
            {
                new("dist/bundle.js", null, map2, null),
            }),
        };
        service.ImportLevels(project.Id, levels, idemKey is null ? null : idemKey + ":import");
        service.Compose(project.Id, null, idemKey is null ? null : idemKey + ":compose");
        service.Publish(project.Id, null, idemKey is null ? null : idemKey + ":publish");
        return new { project.Id, status = "published" };
    }
}
