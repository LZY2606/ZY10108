using SourceMapChains.Core;
using Xunit;

namespace Core.Tests;

public class ComposeTests
{
    [Fact]
    public void Three_Level_Chain_Composes_To_Original()
    {
        var result = TestData.ComposeBase(TestData.ThreeLevels());
        Assert.Equal(3, result.Entries.Count);
        Assert.All(result.Entries, e => Assert.True(e.Complete));
        var first = result.Entries[0];
        Assert.Equal("dist/bundle.js", first.GeneratedPath);
        Assert.Equal(1, first.GenLine); // banner line is unmapped
        Assert.Equal(2, first.Steps.Count);
        Assert.Equal(1, first.Steps[0].Level);
        Assert.Equal("out/hello.js", first.Steps[0].Path);
        Assert.Equal(0, first.Steps[1].Level);
        Assert.Equal("src/hello.ts", first.Steps[1].Path);
    }

    [Fact]
    public void Missing_Intermediate_Source_Gives_Breakpoint()
    {
        var levels = TestData.ThreeLevels();
        levels[1].Files.Clear(); // intermediate output missing
        var result = TestData.ComposeBase(levels);
        Assert.All(result.Entries, e =>
        {
            Assert.False(e.Complete);
            Assert.Equal(1, e.BreakLevel);
            Assert.Contains("missing-source", e.BreakReason);
        });
    }

    [Fact]
    public void Missing_Map_At_Intermediate_Level_Gives_Breakpoint()
    {
        var levels = TestData.ThreeLevels();
        levels[1].Maps.Clear();
        var result = TestData.ComposeBase(levels);
        Assert.All(result.Entries, e =>
        {
            Assert.False(e.Complete);
            Assert.Equal(1, e.BreakLevel);
            Assert.Contains("no-map", e.BreakReason);
        });
    }

    [Fact]
    public void Trace_From_Final_Position_Walks_All_Levels()
    {
        var result = TestData.ComposeBase(TestData.ThreeLevels());
        var entry = CompositionQueries.Trace(result, "dist/bundle.js", 2, 0);
        Assert.NotNull(entry);
        Assert.True(entry!.Complete);
        Assert.Equal("src/hello.ts", entry.Steps[^1].Path);
        Assert.Equal(1, entry.Steps[^1].Line);
    }

    [Fact]
    public void Reverse_Lookup_Finds_Generated_Ranges()
    {
        var result = TestData.ComposeBase(TestData.ThreeLevels());
        var hits = CompositionQueries.ReverseLookup(result, 0, null, "src/hello.ts", 0, 0, 6);
        Assert.Single(hits);
        Assert.Equal("dist/bundle.js", hits[0].GeneratedPath);
        Assert.Equal(1, hits[0].GenLine);
    }

    [Fact]
    public void Bom_Is_Stripped_And_Recorded()
    {
        var file = TestData.File("a.ts", "﻿abc\n");
        Assert.True(file.HadBom);
        Assert.Equal("abc\n", file.Content);
        Assert.Equal(Fingerprint.OfText("abc\n"), file.Fingerprint);
    }

    [Fact]
    public void Trailing_Newline_And_Final_Line_Rules()
    {
        Assert.Equal(2, TextLines.LineCount("a\nb\n"));
        Assert.Equal(2, TextLines.LineCount("a\nb")); // trailing line without newline still counts
        Assert.Equal(0, TextLines.LineCount(""));
        Assert.True(TestData.File("a", "a\nb\n").EndsWithNewline);
        Assert.False(TestData.File("a", "a\nb").EndsWithNewline);
    }

    [Fact]
    public void Digest_Changes_When_Any_Level_Changes()
    {
        var d1 = TestData.ComposeBase(TestData.ThreeLevels()).Digest;
        var modified = TestData.ThreeLevels();
        modified[0].Files[0] = TestData.File("src/hello.ts", TestData.Ts + "// changed\n");
        var d2 = TestData.ComposeBase(modified).Digest;
        Assert.NotEqual(d1, d2);
        // Deterministic: same inputs -> same digest.
        Assert.Equal(d1, TestData.ComposeBase(TestData.ThreeLevels()).Digest);
    }
}
