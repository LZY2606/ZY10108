using System.Text.Json;

namespace SourceMapChains.Core;

public sealed record ImportFileDto(string Path, string? SourceRoot, string Content, string? Semantics);
public sealed record ImportMapDto(string GeneratedPath, string? GeneratedSourceRoot, string MapJson, string? Semantics);
public sealed record ImportLevelDto(string Name, List<ImportFileDto> Files, List<ImportMapDto>? Maps);

public sealed class ServiceException : Exception
{
    public string Code { get; }
    public ServiceException(string code, string message) : base(message) => Code = code;
}

/// <summary>Business state machine over projects, compositions, revisions and jobs.</summary>
public sealed class ProjectService
{
    public const string BaseRevisionId = "base";
    public const int ExportFormatVersion = 1;

    private readonly ProjectStore _store;
    private readonly Func<DateTimeOffset> _now;

    public ProjectService(ProjectStore store, Func<DateTimeOffset>? now = null)
    {
        _store = store;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    // ---- helpers ----

    private EventRecord AppendEvent(ProjectDocument doc, string type, object data)
    {
        var ev = new EventRecord(doc.NextSeq++, _now(), type, data);
        doc.Events.Add(ev);
        return ev;
    }

    /// <summary>Idempotency gate: a retry with the same key returns the recorded
    /// result and performs no mutation and appends no events.</summary>
    private T Idempotent<T>(ProjectDocument doc, string? key, Func<T> work)
    {
        if (!string.IsNullOrEmpty(key) && doc.Operations.TryGetValue(key, out var op))
            return JsonSerializer.Deserialize<T>(op.ResultJson, JsonOptions.Default)!;
        var result = work();
        if (!string.IsNullOrEmpty(key))
            doc.Operations[key] = new OperationRecord(key,
                JsonSerializer.Serialize(result, JsonOptions.Default), _now());
        return result;
    }

    private static void Require(bool condition, string code, string message)
    {
        if (!condition) throw new ServiceException(code, message);
    }

    // ---- operations ----

    public ProjectDocument CreateProject(string name, string? idempotencyKey)
    {
        var doc = new ProjectDocument
        {
            Id = Guid.NewGuid().ToString("n")[..12],
            Name = name,
        };
        AppendEvent(doc, "ProjectCreated", new { doc.Id, name });
        if (!string.IsNullOrEmpty(idempotencyKey))
            doc.Operations[idempotencyKey] = new OperationRecord(idempotencyKey,
                JsonSerializer.Serialize(doc, JsonOptions.Default), _now());
        _store.Save(doc);
        return doc;
    }

    /// <summary>Atomic batch import: all levels parse and validate, or nothing changes.</summary>
    public ProjectDocument ImportLevels(string projectId, List<ImportLevelDto> payload, string? idempotencyKey)
    {
        return _store.Mutate(projectId, doc =>
            Idempotent(doc, idempotencyKey, () =>
            {
                Require(doc.Status is ProjectStatus.Created or ProjectStatus.Imported
                        or ProjectStatus.Composed,
                    "InvalidState", $"Cannot import while project is {doc.Status}.");
                Require(payload.Count >= 1, "EmptyImport", "At least one level is required.");
                // Parse everything first: any failure aborts the whole batch.
                var levels = payload.Select(ParseLevel).ToList();
                doc.Levels = levels;
                doc.Compositions.Clear();
                doc.PublishedRevisionId = null;
                doc.Status = ProjectStatus.Imported;
                AppendEvent(doc, "LevelsImported", new
                {
                    levels = levels.Select(l => new { l.Name, files = l.Files.Count, maps = l.Maps.Count, fingerprint = l.Fingerprint() }).ToList(),
                });
                return doc;
            }));
    }

    private static LevelData ParseLevel(ImportLevelDto dto)
    {
        var files = dto.Files.Select(f => SourceFileData.Create(
            f.Path, f.SourceRoot, f.Content, ParseSemantics(f.Semantics))).ToList();
        var maps = (dto.Maps ?? new List<ImportMapDto>()).Select(m =>
        {
            var map = SourceMapDocument.Parse(m.MapJson, ParseSemantics(m.Semantics));
            return new MapData(m.GeneratedPath, FileIdentity.NormalizeRoot(m.GeneratedSourceRoot), map);
        }).ToList();
        return new LevelData(dto.Name, files, maps);
    }

    private static ColumnSemantics ParseSemantics(string? value) =>
        string.Equals(value, "unicodeScalar", StringComparison.OrdinalIgnoreCase)
            ? ColumnSemantics.UnicodeScalar
            : ColumnSemantics.Utf16;

    /// <summary>Compose a revision (or the base chain) through the job machinery.</summary>
    public JobRecord Compose(string projectId, string? revisionId, string? idempotencyKey)
    {
        var jobId = Guid.NewGuid().ToString("n")[..12];
        return _store.Mutate(projectId, doc =>
            Idempotent(doc, idempotencyKey, () =>
            {
                Require(doc.Levels.Count >= 1, "InvalidState", "Import levels before composing.");
                var revId = revisionId ?? BaseRevisionId;
                Revision? revision = null;
                if (revId != BaseRevisionId)
                {
                    revision = doc.Revisions.FirstOrDefault(r => r.Id == revId)
                        ?? throw new ServiceException("NotFound", $"Revision '{revId}' not found.");
                }
                var job = new JobRecord { Id = jobId, Kind = "compose", Status = "pending", RevisionId = revId, CreatedAt = _now() };
                doc.Jobs.Add(job);
                AppendEvent(doc, "JobScheduled", new { job.Id, job.Kind, revisionId = revId });
                RunComposeJob(doc, job, revision);
                return job;
            }));
    }

    private void RunComposeJob(ProjectDocument doc, JobRecord job, Revision? revision)
    {
        // Completion apply is guarded: a job id is recorded at most once.
        if (doc.Events.Any(e => e.Type == "JobCompleted" &&
            JsonSerializer.Serialize(e.Data, JsonOptions.Default).Contains(job.Id)))
            return;
        job.Status = "running";
        AppendEvent(doc, "JobStarted", new { job.Id });
        try
        {
            var revId = revision?.Id ?? BaseRevisionId;
            var edits = revision?.Edits ?? new List<MappingEdit>();
            var levels = RevisionEngine.ApplyEdits(doc.Levels, edits);
            var ruleVersion = doc.RuleVersion + (revision is null ? 0 : doc.Revisions.IndexOf(revision) + 1);
            var result = Composer.Compose(levels, revId, ruleVersion, _now());
            doc.Compositions[revId] = result;
            if (revision is not null)
                doc.Revisions[doc.Revisions.IndexOf(revision)] = revision with { Status = "composed" };
            if (doc.Status == ProjectStatus.Imported) doc.Status = ProjectStatus.Composed;
            job.Status = "completed";
            job.FinishedAt = _now();
            AppendEvent(doc, "JobCompleted", new { job.Id, digest = result.Digest, entries = result.Entries.Count, issues = result.Issues.Count });
        }
        catch (Exception ex)
        {
            job.Status = "failed";
            job.FinishedAt = _now();
            AppendEvent(doc, "JobFailed", new { job.Id, error = ex.Message });
            throw;
        }
    }

    /// <summary>Re-run jobs that were interrupted mid-flight. Completion is
    /// idempotent, so results are never recorded twice.</summary>
    public void ResumeIncompleteJobs()
    {
        foreach (var doc in _store.List())
        {
            var incomplete = doc.Jobs.Where(j => j.Status is "pending" or "running").ToList();
            if (incomplete.Count == 0) continue;
            _store.Mutate(doc.Id, d =>
            {
                foreach (var job in d.Jobs.Where(j => j.Status is "pending" or "running"))
                {
                    var revision = d.Revisions.FirstOrDefault(r => r.Id == job.RevisionId);
                    AppendEvent(d, "JobResumed", new { job.Id });
                    RunComposeJob(d, job, revision);
                }
                return d;
            });
        }
    }

    public CompositionResult Publish(string projectId, string? revisionId, string? idempotencyKey)
    {
        return _store.Mutate(projectId, doc =>
            Idempotent(doc, idempotencyKey, () =>
            {
                var revId = revisionId ?? BaseRevisionId;
                Require(doc.Compositions.TryGetValue(revId, out var composition),
                    "InvalidState", $"No composition for revision '{revId}'; compose first.");
                doc.PublishedRevisionId = revId;
                doc.Status = ProjectStatus.Published;
                AppendEvent(doc, "CompositionPublished", new { revisionId = revId, composition!.Digest });
                return composition!;
            }));
    }

    public Revision CreateRevision(string projectId, string name, List<MappingEdit> edits, string? idempotencyKey)
    {
        return _store.Mutate(projectId, doc =>
            Idempotent(doc, idempotencyKey, () =>
            {
                var revision = new Revision(Guid.NewGuid().ToString("n")[..8], name, edits, "draft", _now());
                doc.Revisions.Add(revision);
                AppendEvent(doc, "RevisionCreated", new { revision.Id, name, edits = edits.Count });
                return revision;
            }));
    }

    public object MergeRevisions(string projectId, string aId, string bId, string? idempotencyKey)
    {
        return _store.Mutate(projectId, doc =>
            Idempotent(doc, idempotencyKey, () =>
            {
                var a = doc.Revisions.FirstOrDefault(r => r.Id == aId)
                    ?? throw new ServiceException("NotFound", $"Revision '{aId}' not found.");
                var b = doc.Revisions.FirstOrDefault(r => r.Id == bId)
                    ?? throw new ServiceException("NotFound", $"Revision '{bId}' not found.");
                Require(doc.Compositions.TryGetValue(aId, out var compA), "InvalidState", $"Compose revision '{aId}' first.");
                Require(doc.Compositions.TryGetValue(bId, out var compB), "InvalidState", $"Compose revision '{bId}' first.");
                var (merged, conflicts) = RevisionEngine.Merge(doc.Levels, a, compA!, b, compB!);
                if (conflicts is not null)
                {
                    AppendEvent(doc, "RevisionMergeConflict", new { aId, bId, count = conflicts.Count });
                    return (object)new { status = "conflict", conflicts };
                }
                var revision = new Revision(Guid.NewGuid().ToString("n")[..8],
                    $"merge({a.Name},{b.Name})", merged!, "draft", _now());
                doc.Revisions.Add(revision);
                doc.RuleVersion++;
                AppendEvent(doc, "RevisionMerged", new { aId, bId, mergedId = revision.Id, doc.RuleVersion });
                return new { status = "merged", revision };
            }));
    }

    public object CompareWithBase(string projectId, string revisionId)
    {
        var doc = _store.Get(projectId) ?? throw new ServiceException("NotFound", "Project not found.");
        Require(doc.Compositions.TryGetValue(BaseRevisionId, out var baseComp), "InvalidState", "Compose the base chain first.");
        Require(doc.Compositions.TryGetValue(revisionId, out var revComp), "InvalidState", $"Compose revision '{revisionId}' first.");
        var change = CompositionQueries.EarliestChange(baseComp!, revComp!);
        return change is null
            ? new { changed = false }
            : new { changed = true, path = change.Value.Path, line = change.Value.Line, col = change.Value.Col, detail = change.Value.Detail };
    }

    public object Trace(string projectId, string generatedPath, int genLine, int genCol, string? revisionId)
    {
        var composition = GetComposition(projectId, revisionId);
        var entry = CompositionQueries.Trace(composition, generatedPath, genLine, genCol);
        return entry is null
            ? new { found = false }
            : new { found = true, entry };
    }

    public object Reverse(string projectId, int level, string? sourceRoot, string path, int line, int col, int length, string? revisionId)
    {
        var composition = GetComposition(projectId, revisionId);
        var hits = CompositionQueries.ReverseLookup(composition, level, sourceRoot, path, line, col, length);
        return new { count = hits.Count, ranges = hits.Select(h => new { generatedPath = h.GeneratedPath, h.GenLine, h.GenCol, h.Length }) };
    }

    public CompositionResult GetCompositionView(string projectId, string? revisionId) =>
        GetComposition(projectId, revisionId);

    private CompositionResult GetComposition(string projectId, string? revisionId)
    {
        var doc = _store.Get(projectId) ?? throw new ServiceException("NotFound", "Project not found.");
        var revId = revisionId ?? doc.PublishedRevisionId ?? BaseRevisionId;
        Require(doc.Compositions.TryGetValue(revId, out var composition),
            "InvalidState", $"No composition for revision '{revId}'; compose first.");
        return composition!;
    }

    // ---- export / import ----

    public string Export(string projectId)
    {
        var doc = _store.Get(projectId) ?? throw new ServiceException("NotFound", "Project not found.");
        AppendEvent(doc, "ProjectExported", new { doc.Id });
        _store.Save(doc);
        return JsonSerializer.Serialize(new
        {
            formatVersion = ExportFormatVersion,
            exportedAt = _now(),
            project = doc,
        }, JsonOptions.Pretty);
    }

    public ProjectDocument Import(string exportJson, string? idempotencyKey)
    {
        using var parsed = JsonDocument.Parse(exportJson);
        var root = parsed.RootElement;
        var version = root.GetProperty("formatVersion").GetInt32();
        Require(version <= ExportFormatVersion, "UnsupportedFormat",
            $"Export format version {version} is newer than supported {ExportFormatVersion}.");
        var doc = root.GetProperty("project").Deserialize<ProjectDocument>(JsonOptions.Default)
            ?? throw new ServiceException("BadFormat", "Missing project payload.");
        if (_store.Get(doc.Id) is not null)
        {
            // Re-import of the same project: idempotent no-op returning existing.
            return _store.Get(doc.Id)!;
        }
        AppendEvent(doc, "ProjectImported", new { doc.Id, formatVersion = version });
        _store.Save(doc);
        return doc;
    }

    public ProjectDocument? Get(string id) => _store.Get(id);
    public IReadOnlyList<ProjectDocument> List() => _store.List();
}
