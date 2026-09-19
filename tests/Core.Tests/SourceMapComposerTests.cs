using System.Text.Json;
using SourceMapChain.Core;

namespace Core.Tests;

public sealed class SourceMapComposerTests
{
    [Fact]
    public void TraceFinalPositionAcrossTwoStages()
    {
        var composer = new SourceMapComposer(TwoStages());

        var trace = composer.TraceFinalPosition(0, 5);

        Assert.True(trace.Complete, System.Text.Json.JsonSerializer.Serialize(trace.Breaks));
        Assert.Equal(3, trace.Hops.Count);
        Assert.Equal("original.js", trace.Hops[^1].SourcePath);
        Assert.Equal(0, trace.Hops[^1].OriginalLine);
        Assert.Equal(4, trace.Hops[^1].OriginalColumn);
    }

    [Fact]
    public void ReverseImpactFindsBothGeneratedStages()
    {
        var composer = new SourceMapComposer(TwoStages());

        var ranges = composer.ReverseImpact("original.js", 0, 4, 0, 5);

        Assert.Equal(["intermediate", "bundle"], ranges
            .GroupBy(range => range.StageId)
            .Select(group => group.Key)
            .ToArray());
        Assert.Contains(ranges, range => range.StageId == "bundle");
    }

    [Fact]
    public void SameRelativePathUnderDifferentSourceRootsStaysDistinct()
    {
        var source = "v\n";
        var output = "xv\n";
        var first = Stage("a", output, Map("a/src", ["app.js"], [source], "CAAA"));
        var second = Stage("b", output + "y", Map("b/src", ["app.js"], [source], "CAAA"));

        var composer = new SourceMapComposer([first, second]);

        Assert.Equal("a/src/app.js", composer.Stages[0].ResolvedSources[0]);
        Assert.Equal("b/src/app.js", composer.Stages[1].ResolvedSources[0]);
        Assert.False(composer.Validate().Complete);
    }

    [Fact]
    public void UnicodeScalarColumnsAreConvertedSeparatelyFromUtf16()
    {
        var source = "😸x\n";
        var output = "😸x\n";
        var map = new SourceMapInput
        {
            Version = 3,
            Sources = ["app.js"],
            SourcesContent = [source],
            XColumnEncoding = "unicode-scalars",
            Mappings = "CAAC"
        };
        var stage = Stage("s", output, map);
        var composer = new SourceMapComposer([stage]);

        var trace = composer.TraceFinalPosition(0, 2, "utf16");

        Assert.True(trace.Complete, System.Text.Json.JsonSerializer.Serialize(trace));
        Assert.Equal(0, trace.Hops[0].SegmentIndex);
        Assert.Equal(1, trace.Hops[0].OriginalColumn);
        Assert.Equal(1, trace.Hops[1].Column);
        Assert.Equal(1, trace.Hops[^1].OriginalColumn);
    }

    [Fact]
    public void EmptyCommaSegmentAndNonMonotonicColumnsAreRejected()
    {
        var stage = Stage("s", "ab", new SourceMapInput { Version = 3, Sources = ["x"], Mappings = "A," });
        Assert.Throws<SourceMapException>(() => new SourceMapComposer([stage]));

        var bad = Stage("s2", "ba", new SourceMapInput { Version = 3, Sources = ["x"], Mappings = "CAAI,D" });
        Assert.Throws<SourceMapException>(() => new SourceMapComposer([bad]));
    }

    [Fact]
    public void RevisionDoesNotMutateOriginalMapAndReportsChangedRange()
    {
        var stages = TwoStages();
        var originalMap = stages[0].MapJson;
        var revision = new RevisionProposal
        {
            Id = "rev-x",
            StageId = "intermediate",
            Kind = "segment",
            SegmentIndex = 1,
            OriginalLine = 0,
            OriginalColumn = 3,
            IdempotencyKey = "rev-x"
        };
        var composer = new SourceMapComposer(stages);

        var comparison = composer.CompareRevision(revision);

        Assert.True(comparison.Valid);
        Assert.Equal("intermediate", comparison.EarliestChangedStageId);
        Assert.NotEmpty(comparison.ChangedRanges);
        Assert.Equal(originalMap, stages[0].MapJson);
    }

    [Fact]
    public void TwoDisjointRevisionsCanMerge()
    {
        var stages = TwoStages();
        var first = new RevisionProposal { Id = "a", StageId = "intermediate", Kind = "segment", SegmentIndex = 1, OriginalColumn = 3, IdempotencyKey = "a" };
        var second = new RevisionProposal { Id = "b", StageId = "intermediate", Kind = "segment", SegmentIndex = 0, IdempotencyKey = "b" };
        var composer = new SourceMapComposer(stages);

        var review = composer.MergeRevisions("a", "b", [first, second]);

        Assert.True(review.CanMerge);
        Assert.NotNull(review.CombinedComparison);
    }

    [Fact]
    public void PathRuleRevisionKeepsSameNameUnderDifferentRootsDistinct()
    {
        var output = "v\n";
        var stages = new List<StageInput>
        {
            Stage("intermediate", output, new SourceMapInput
            {
                Version = 3,
                Sources = ["app.js"],
                SourcesContent = [output],
                Mappings = "AAAA"
            }),
            Stage("bundle", output, new SourceMapInput
            {
                Version = 3,
                SourceRoot = "wrong",
                Sources = ["app.js"],
                Mappings = "AAAA"
            })
        };
        var revision = new RevisionProposal
        {
            Id = "path",
            StageId = "bundle",
            Kind = "path-rule",
            SegmentIndex = -1,
            SourcePathRule = "source[0]:/intermediate",
            IdempotencyKey = "path"
        };

        var candidate = new SourceMapComposer(stages, [revision]);

        Assert.Equal("source[0]:/intermediate", candidate.Stages[1].PathRevision);
        Assert.Equal("/intermediate", candidate.Stages[1].Map.Raw.Sources[0]);
        Assert.Equal("intermediate", candidate.Stages[1].ResolvedSources[0]);
        Assert.True(candidate.Validate().Complete);
    }

    [Fact]
    public async Task BatchImportIsAtomicAndIdempotent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"workspace-{Guid.NewGuid():N}.json");
        var service = new WorkspaceService(new WorkspaceStore(path));
        var good = TwoStages();

        var first = await service.ImportBatchAsync("key", good);
        var duplicate = await service.ImportBatchAsync("key", good);
        var bad = await service.ImportBatchAsync("bad", [new StageInput { Id = "intermediate", OutputText = "x", MapJson = "{}" }]);
        var document = await service.GetAsync();

        Assert.True(first.Applied);
        Assert.True(duplicate.Duplicate);
        Assert.False(bad.Applied);
        Assert.Equal(2, document.Stages.Count);
        Assert.DoesNotContain(document.Events, evt => evt.IdempotencyKey == "bad");
    }

    [Fact]
    public async Task RecoveredJobCompletesOnlyOnce()
    {
        var path = Path.Combine(Path.GetTempPath(), $"workspace-{Guid.NewGuid():N}.json");
        var service = new WorkspaceService(new WorkspaceStore(path));
        var runs = 0;

        await service.EnqueueOrGetJobAsync("job", "scan");
        await service.RecoverJobsAsync(_ => { runs++; return Task.FromResult("done"); });
        await service.RecoverJobsAsync(_ => { runs++; return Task.FromResult("done"); });

        Assert.Equal(1, runs);
        var document = await service.GetAsync();
        Assert.Single(document.Jobs);
        Assert.Equal("completed", document.Jobs[0].Status);
    }

    private static List<StageInput> TwoStages()
    {
        var original = "var x=1\n";
        var intermediate = "Avar x=1\n";
        var bundle = "Bvar x=1\n";
        return
        [
            new StageInput
            {
                Id = "intermediate",
                Label = "intermediate",
                OutputText = intermediate,
                MapJson = JsonSerializer.Serialize(new
                {
                    version = 3,
                    sources = new[] { "original.js" },
                    sourcesContent = new[] { original },
                    names = Array.Empty<string>(),
                    mappings = "CAAA,BAAI"
                })
            },
            new StageInput
            {
                Id = "bundle",
                Label = "bundle",
                OutputText = bundle,
                MapJson = JsonSerializer.Serialize(new
                {
                    version = 3,
                    sources = new[] { "intermediate" },
                    names = Array.Empty<string>(),
                    mappings = "CAAA,BAAI"
                })
            }
        ];
    }

    private static StageInput Stage(string id, string output, SourceMapInput map) =>
        new() { Id = id, Label = id, OutputText = output, MapJson = JsonSerializer.Serialize(map) };

    private static SourceMapInput Map(string root, string[] sources, string?[] contents, string mappings) =>
        new() { Version = 3, SourceRoot = root, Sources = [.. sources], SourcesContent = [.. contents], Mappings = mappings };

}
