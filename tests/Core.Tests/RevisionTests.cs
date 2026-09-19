using SourceMapChains.Core;
using Xunit;

namespace Core.Tests;

public class RevisionTests
{
    private static MappingEdit SegmentEdit(int genLine, int genCol, int newSrcLine, int newSrcCol) =>
        new("segment", 2, "dist/bundle.js", genLine, genCol,
            new SegmentPatch(null, newSrcLine, newSrcCol), null, null, null, null);

    [Fact]
    public void Segment_Edit_Produces_Earliest_Change_And_Leaves_Original_Untouched()
    {
        var levels = TestData.ThreeLevels();
        var originalMappings = levels[2].Maps[0].Map.Mappings;
        var baseComp = TestData.ComposeBase(levels);

        var edits = new List<MappingEdit> { SegmentEdit(1, 0, 0, 2) };
        var revised = RevisionEngine.ApplyEdits(levels, edits);
        var revComp = Composer.Compose(revised, "rev1", 2, DateTimeOffset.UnixEpoch);

        var change = CompositionQueries.EarliestChange(baseComp, revComp);
        Assert.NotNull(change);
        Assert.Equal("dist/bundle.js", change!.Value.Path);
        Assert.Equal(1, change.Value.Line);
        Assert.Equal(0, change.Value.Col);
        // Original map data is not modified.
        Assert.Equal(originalMappings, levels[2].Maps[0].Map.Mappings);
    }

    [Fact]
    public void Path_Rule_Edit_Rewrites_Identity()
    {
        var levels = TestData.ThreeLevels();
        // Break the link by moving the original source under a sourceRoot...
        levels[0].Files[0] = TestData.File("src/hello.ts", TestData.Ts, root: "vendor");
        var broken = TestData.ComposeBase(levels);
        Assert.All(broken.Entries, e => Assert.False(e.Complete));
        // ...then fix it with a path rule on the transpile map.
        var edit = new MappingEdit("pathRule", 1, "out/hello.js", null, null, null,
            MatchSourceRoot: null, MatchPath: "src/hello.ts",
            ReplaceSourceRoot: "vendor", ReplacePath: null);
        var revised = RevisionEngine.ApplyEdits(levels, new[] { edit });
        var fixedComp = Composer.Compose(revised, "rev", 2, DateTimeOffset.UnixEpoch);
        Assert.All(fixedComp.Entries, e => Assert.True(e.Complete));
    }

    [Fact]
    public void Disjoint_Revisions_Merge()
    {
        var levels = TestData.ThreeLevels();
        var a = new Revision("a", "A", new() { SegmentEdit(1, 0, 0, 1) }, "composed", DateTimeOffset.UnixEpoch);
        var b = new Revision("b", "B", new() { SegmentEdit(2, 0, 1, 1) }, "composed", DateTimeOffset.UnixEpoch);
        var compA = Composer.Compose(RevisionEngine.ApplyEdits(levels, a.Edits), "a", 2, DateTimeOffset.UnixEpoch);
        var compB = Composer.Compose(RevisionEngine.ApplyEdits(levels, b.Edits), "b", 2, DateTimeOffset.UnixEpoch);
        var (merged, conflicts) = RevisionEngine.Merge(levels, a, compA, b, compB);
        Assert.Null(conflicts);
        Assert.Equal(2, merged!.Count);
    }

    [Fact]
    public void Overlapping_Revisions_Conflict_With_Original_And_Impact()
    {
        var levels = TestData.ThreeLevels();
        var a = new Revision("a", "A", new() { SegmentEdit(1, 0, 0, 1) }, "composed", DateTimeOffset.UnixEpoch);
        var b = new Revision("b", "B", new() { SegmentEdit(1, 0, 2, 0) }, "composed", DateTimeOffset.UnixEpoch);
        var compA = Composer.Compose(RevisionEngine.ApplyEdits(levels, a.Edits), "a", 2, DateTimeOffset.UnixEpoch);
        var compB = Composer.Compose(RevisionEngine.ApplyEdits(levels, b.Edits), "b", 2, DateTimeOffset.UnixEpoch);
        var (merged, conflicts) = RevisionEngine.Merge(levels, a, compA, b, compB);
        Assert.Null(merged);
        var conflict = Assert.Single(conflicts!);
        Assert.NotNull(conflict.Original);
        Assert.Single(conflict.EditA);
        Assert.Single(conflict.EditB);
        Assert.NotEmpty(conflict.DownstreamImpactA);
        Assert.NotEmpty(conflict.DownstreamImpactB);
    }
}
