using SourceMapChains.Core;
using Xunit;

namespace Core.Tests;

public class ServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "smc-tests-" + Guid.NewGuid().ToString("n"));
    private readonly ProjectService _svc;

    public ServiceTests()
    {
        _svc = new ProjectService(new ProjectStore(_dir));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private List<ImportLevelDto> DemoPayload(string ts = TestData.Ts) =>
        new()
        {
            new("original", new() { new ImportFileDto("src/hello.ts", null, ts, "utf16") }, null),
            new("transpiled", new() { new ImportFileDto("out/hello.js", null, TestData.Js, "utf16") },
                new() { new ImportMapDto("out/hello.js", null,
                    TestData.MapJson("hello.js", new[] { "src/hello.ts" }, TestData.LineMap(3), new[] { ts }), null) }),
            new("bundle", new() { new ImportFileDto("dist/bundle.js", null, TestData.Bundle, "utf16") },
                new() { new ImportMapDto("dist/bundle.js", null,
                    TestData.MapJson("bundle.js", new[] { "out/hello.js" }, TestData.LineMap(3, 1), new[] { TestData.Js }), null) }),
        };

    private ProjectDocument ReadyProject()
    {
        var p = _svc.CreateProject("t", null);
        _svc.ImportLevels(p.Id, DemoPayload(), null);
        _svc.Compose(p.Id, null, null);
        return _svc.Get(p.Id)!;
    }

    [Fact]
    public void StateMachine_Enforces_Order()
    {
        var p = _svc.CreateProject("t", null);
        Assert.Throws<ServiceException>(() => _svc.Compose(p.Id, null, null));
        _svc.ImportLevels(p.Id, DemoPayload(), null);
        Assert.Throws<ServiceException>(() => _svc.Publish(p.Id, null, null));
        _svc.Compose(p.Id, null, null);
        _svc.Publish(p.Id, null, null);
        Assert.Equal(ProjectStatus.Published, _svc.Get(p.Id)!.Status);
    }

    [Fact]
    public void Batch_Import_Is_Atomic()
    {
        var p = _svc.CreateProject("t", null);
        var bad = DemoPayload();
        bad[1] = bad[1] with { Maps = new() { new ImportMapDto("out/hello.js", null, "{not json", null) } };
        Assert.ThrowsAny<Exception>(() => _svc.ImportLevels(p.Id, bad, null));
        var after = _svc.Get(p.Id)!;
        Assert.Empty(after.Levels);
        Assert.Equal(ProjectStatus.Created, after.Status);
        Assert.DoesNotContain(after.Events, e => e.Type == "LevelsImported");
    }

    [Fact]
    public void Idempotent_Retry_Records_Nothing_Twice()
    {
        var p = _svc.CreateProject("t", null);
        _svc.ImportLevels(p.Id, DemoPayload(), null);
        var job1 = _svc.Compose(p.Id, null, "op-compose-1");
        var job2 = _svc.Compose(p.Id, null, "op-compose-1");
        Assert.Equal(job1.Id, job2.Id);
        var doc = _svc.Get(p.Id)!;
        Assert.Single(doc.Jobs);
        Assert.Single(doc.Events.Where(e => e.Type == "JobCompleted"));
    }

    [Fact]
    public void Digest_Changes_When_Any_Level_Changes()
    {
        var p = ReadyProject();
        var d1 = p.Compositions["base"].Digest;
        _svc.ImportLevels(p.Id, DemoPayload(ts: TestData.Ts + "// v2\n"), null);
        _svc.Compose(p.Id, null, null);
        var d2 = _svc.Get(p.Id)!.Compositions["base"].Digest;
        Assert.NotEqual(d1, d2);
    }

    [Fact]
    public void Export_Then_Import_Preserves_Query_Results()
    {
        var p = ReadyProject();
        _svc.Publish(p.Id, null, null);
        var traceBefore = _svc.Trace(p.Id, "dist/bundle.js", 1, 0, null);
        var export = _svc.Export(p.Id);

        var dir2 = Path.Combine(Path.GetTempPath(), "smc-tests-" + Guid.NewGuid().ToString("n"));
        try
        {
            var svc2 = new ProjectService(new ProjectStore(dir2));
            var imported = svc2.Import(export, null);
            Assert.Equal(p.Id, imported.Id);
            var traceAfter = svc2.Trace(imported.Id, "dist/bundle.js", 1, 0, null);
            Assert.Equal(
                System.Text.Json.JsonSerializer.Serialize(traceBefore),
                System.Text.Json.JsonSerializer.Serialize(traceAfter));
            Assert.Equal(
                p.Compositions["base"].Digest,
                svc2.Get(imported.Id)!.Compositions["base"].Digest);
        }
        finally
        {
            Directory.Delete(dir2, recursive: true);
        }
    }

    [Fact]
    public void Reimport_Of_Same_Project_Is_Idempotent()
    {
        var p = ReadyProject();
        var export = _svc.Export(p.Id);
        var again = _svc.Import(export, null);
        Assert.Equal(p.Id, again.Id);
        Assert.Single(_svc.Get(p.Id)!.Events.Where(e => e.Type == "ProjectExported"));
    }

    [Fact]
    public void Interrupted_Job_Resumes_Without_Duplicate_Results()
    {
        var p = _svc.CreateProject("t", null);
        _svc.ImportLevels(p.Id, DemoPayload(), null);
        // Simulate a crash: a compose job stuck in "running".
        var store = new ProjectStore(_dir);
        store.Mutate(p.Id, doc =>
        {
            doc.Jobs.Add(new JobRecord { Id = "stuck1", Kind = "compose", Status = "running", RevisionId = "base", CreatedAt = DateTimeOffset.UnixEpoch });
            return doc;
        });
        var svc2 = new ProjectService(new ProjectStore(_dir));
        svc2.ResumeIncompleteJobs();
        var doc = svc2.Get(p.Id)!;
        var job = Assert.Single(doc.Jobs);
        Assert.Equal("completed", job.Status);
        Assert.Single(doc.Events.Where(e => e.Type == "JobCompleted"));
        Assert.True(doc.Compositions.ContainsKey("base"));
        // Resuming again records nothing new.
        svc2.ResumeIncompleteJobs();
        Assert.Single(svc2.Get(p.Id)!.Events.Where(e => e.Type == "JobCompleted"));
    }

    [Fact]
    public void Revision_Flow_Compare_And_Merge()
    {
        var p = ReadyProject();
        var editA = new MappingEdit("segment", 2, "dist/bundle.js", 1, 0,
            new SegmentPatch(null, 0, 2), null, null, null, null);
        var editB = new MappingEdit("segment", 2, "dist/bundle.js", 2, 0,
            new SegmentPatch(null, 1, 0), null, null, null, null);
        var revA = _svc.CreateRevision(p.Id, "A", new() { editA }, null);
        var revB = _svc.CreateRevision(p.Id, "B", new() { editB }, null);
        _svc.Compose(p.Id, revA.Id, null);
        _svc.Compose(p.Id, revB.Id, null);

        var compare = _svc.CompareWithBase(p.Id, revA.Id);
        var json = System.Text.Json.JsonSerializer.Serialize(compare);
        Assert.Contains("\"changed\":true", json);
        Assert.Contains("\"line\":1", json);

        // Original map untouched.
        var doc = _svc.Get(p.Id)!;
        Assert.Equal(TestData.LineMap(3, 1), doc.Levels[2].Maps[0].Map.Mappings);

        // Disjoint edits merge.
        var merged = _svc.MergeRevisions(p.Id, revA.Id, revB.Id, null);
        Assert.Contains("\"status\":\"merged\"", System.Text.Json.JsonSerializer.Serialize(merged));

        // Overlapping edits conflict.
        var revC = _svc.CreateRevision(p.Id, "C", new()
        {
            new MappingEdit("segment", 2, "dist/bundle.js", 1, 0,
                new SegmentPatch(null, 2, 0), null, null, null, null),
        }, null);
        _svc.Compose(p.Id, revC.Id, null);
        var conflict = _svc.MergeRevisions(p.Id, revA.Id, revC.Id, null);
        var conflictJson = System.Text.Json.JsonSerializer.Serialize(conflict, SourceMapChains.Core.JsonOptions.Default);
        Assert.Contains("\"status\":\"conflict\"", conflictJson);
        Assert.Contains("downstreamImpactA", conflictJson);
    }
}
