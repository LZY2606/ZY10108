using System.Text.Json;

namespace SourceMapChain.Core;

public sealed record BatchResult(bool Applied, bool Duplicate, WorkspaceDocument Document, List<StageInput> AddedStages, string? Error);
public sealed record JobResult(bool Duplicate, BackgroundJob Job);

public sealed class WorkspaceService
{
    private readonly WorkspaceStore store;

    public WorkspaceService(WorkspaceStore store)
    {
        this.store = store;
    }

    public Task<WorkspaceDocument> GetAsync(CancellationToken cancellationToken = default) =>
        store.LoadAsync(cancellationToken);

    public Task SaveAsync(WorkspaceDocument document, CancellationToken cancellationToken = default) =>
        store.SaveAsync(document, cancellationToken);

    public async Task<BatchResult> ImportBatchAsync(string idempotencyKey, List<StageInput> stages, CancellationToken cancellationToken = default)
    {
        return await store.MutateAsync(document =>
        {
            if (HasIdempotencyKey(document, idempotencyKey))
            {
                return new BatchResult(false, true, document, [], null);
            }

            try
            {
                var snapshot = CloneDocument(document);
                foreach (var stage in stages)
                {
                    if (string.IsNullOrWhiteSpace(stage.Id))
                    {
                        throw new SourceMapException("Stage id is required.");
                    }

                    if (snapshot.Stages.Any(existing => existing.Id == stage.Id))
                    {
                        throw new SourceMapException($"Duplicate stage id '{stage.Id}'.");
                    }

                    snapshot.Stages.Add(stage);
                    new SourceMapComposer(snapshot.Stages);
                }

                AppendEvent(snapshot, "batch-submitted", idempotencyKey, JsonSerializer.SerializeToElement(new { stageIds = stages.Select(stage => stage.Id) }));
                snapshot.State = WorkspaceStateMachine.Apply(snapshot.State, "batch-submitted");
                snapshot.State = WorkspaceStateMachine.Apply(snapshot.State, "analysis-completed");
                AppendEvent(snapshot, "analysis-completed", idempotencyKey + ":analysis", JsonSerializer.SerializeToElement(new { stages = stages.Count }));
                ReplaceWith(document, snapshot);
                return new BatchResult(true, false, document, stages, null);
            }
            catch (Exception ex)
            {
                return new BatchResult(false, false, document, [], ex.Message);
            }
        }, cancellationToken);
    }

    public async Task<(RevisionProposal? Revision, bool Duplicate, string? Error)> AddRevisionAsync(RevisionProposal proposal, CancellationToken cancellationToken = default)
    {
        return await store.MutateAsync(document =>
        {
            if (HasIdempotencyKey(document, proposal.IdempotencyKey))
            {
                return (document.Revisions.FirstOrDefault(item => item.IdempotencyKey == proposal.IdempotencyKey), true, (string?)null);
            }

            try
            {
                if (string.IsNullOrWhiteSpace(proposal.Id))
                {
                    proposal.Id = "rev-" + (document.Revisions.Count + 1);
                }

                var composer = new SourceMapComposer(document.Stages, document.Revisions.Append(proposal).ToList());
                composer.Validate();
                document.Revisions.Add(proposal);
                AppendEvent(document, "revision-opened", proposal.IdempotencyKey, JsonSerializer.SerializeToElement(new { proposal.Id, proposal.StageId, proposal.SegmentIndex }));
                document.State = WorkspaceStateMachine.Apply(document.State, "revision-opened");
                return ((RevisionProposal?)proposal, false, (string?)null);
            }
            catch (Exception ex)
            {
                return ((RevisionProposal?)null, false, ex.Message);
            }
        }, cancellationToken);
    }

    public async Task<PublishedComposition?> PublishAsync(string idempotencyKey, IReadOnlyList<string> revisionIds, CancellationToken cancellationToken = default)
    {
        return await store.MutateAsync(document =>
        {
            if (HasIdempotencyKey(document, idempotencyKey))
            {
                return document.Publications.FirstOrDefault(item => item.Id == idempotencyKey);
            }

            var revisions = document.Revisions.Where(revision => revisionIds.Contains(revision.Id)).ToList();
            var composer = new SourceMapComposer(document.Stages, revisions);
            var validation = composer.Validate();
            if (!validation.Valid)
            {
                throw new SourceMapException("Cannot publish an invalid composition.");
            }

            var composition = new PublishedComposition
            {
                Id = idempotencyKey,
                DeterministicSummary = composer.DeterministicSummary(),
                Fingerprints = composer.Fingerprints(),
                RuleVersion = SourceMapComposer.RuleVersionValue,
                RevisionIds = revisions.Select(revision => revision.Id).ToList(),
                Complete = validation.Complete,
                Breaks = validation.Breaks,
                PublishedAt = DateTimeOffset.UtcNow
            };
            document.Publications.Add(composition);
            AppendEvent(document, "publication-started", idempotencyKey + ":start", JsonSerializer.SerializeToElement(new { composition.DeterministicSummary }));
            document.State = WorkspaceStateMachine.Apply(document.State, "publication-started");
            AppendEvent(document, "composition-published", idempotencyKey, JsonSerializer.SerializeToElement(new { composition.DeterministicSummary }));
            document.State = WorkspaceStateMachine.Apply(document.State, "composition-published");
            return composition;
        }, cancellationToken);
    }

    public async Task<JobResult> EnqueueOrGetJobAsync(string idempotencyKey, string kind, CancellationToken cancellationToken = default)
    {
        return await store.MutateAsync(document =>
        {
            var existing = document.Jobs.FirstOrDefault(job => job.IdempotencyKey == idempotencyKey);
            if (existing is not null)
            {
                return new JobResult(true, existing);
            }

            var job = new BackgroundJob
            {
                Id = "job-" + (document.Jobs.Count + 1),
                Kind = kind,
                IdempotencyKey = idempotencyKey,
                Status = "queued",
                CreatedAt = DateTimeOffset.UtcNow
            };
            document.Jobs.Add(job);
            AppendEvent(document, "job-queued", idempotencyKey, JsonSerializer.SerializeToElement(new { job.Kind }));
            return new JobResult(false, job);
        }, cancellationToken);
    }

    public async Task<bool> CompleteJobAsync(string idempotencyKey, string resultSummary, CancellationToken cancellationToken = default)
    {
        return await store.MutateAsync(document =>
        {
            var job = document.Jobs.FirstOrDefault(item => item.IdempotencyKey == idempotencyKey);
            if (job is null || job.Status == "completed")
            {
                return false;
            }

            job.Status = "completed";
            job.Attempts++;
            job.ResultSummary = resultSummary;
            job.UpdatedAt = DateTimeOffset.UtcNow;
            AppendEvent(document, "job-completed", idempotencyKey, JsonSerializer.SerializeToElement(new { resultSummary }));
            return true;
        }, cancellationToken);
    }

    public async Task RecoverJobsAsync(Func<BackgroundJob, Task<string>> runner, CancellationToken cancellationToken = default)
    {
        var document = await store.LoadAsync(cancellationToken);
        foreach (var job in document.Jobs.Where(job => job.Status is "queued" or "running"))
        {
            var summary = await runner(job);
            await CompleteJobAsync(job.IdempotencyKey, summary, cancellationToken);
        }
    }

    private static bool HasIdempotencyKey(WorkspaceDocument document, string key) =>
        document.Events.Any(evt => evt.IdempotencyKey == key);

    private static void AppendEvent(WorkspaceDocument document, string type, string idempotencyKey, JsonElement payload)
    {
        document.Events.Add(new BusinessEvent
        {
            Sequence = document.Events.Count + 1,
            Id = "evt-" + document.Events.Count,
            Type = type,
            IdempotencyKey = idempotencyKey,
            OccurredAt = DateTimeOffset.UtcNow,
            Payload = payload
        });
    }

    private static WorkspaceDocument CloneDocument(WorkspaceDocument source) =>
        JsonSerializer.Deserialize<WorkspaceDocument>(JsonSerializer.Serialize(source, WorkspaceStore.Options), WorkspaceStore.Options)!;

    private static void ReplaceWith(WorkspaceDocument target, WorkspaceDocument source)
    {
        target.SchemaVersion = source.SchemaVersion;
        target.State = source.State;
        target.RuleVersion = source.RuleVersion;
        target.Stages = source.Stages;
        target.Revisions = source.Revisions;
        target.Publications = source.Publications;
        target.Events = source.Events;
        target.Jobs = source.Jobs;
        target.CreatedAt = source.CreatedAt;
    }
}
