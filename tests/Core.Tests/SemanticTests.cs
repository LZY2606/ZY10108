using System.Text.Json;
using SourceMapChain.Core;

namespace Core.Tests;

public sealed class SemanticTests
{
    [Fact]
    public void MissingIntermediateSourceProducesExplicitBreak()
    {
        var original = "a\n";
        var intermediate = "aa\n";
        var bundle = "aaa\n";
        var stages = new List<StageInput>
        {
            Stage("intermediate", intermediate, new SourceMapInput
            {
                Version = 3,
                Sources = ["original.js"],
                SourcesContent = [original],
                Mappings = "AAAA,CAAC"
            }),
            Stage("bundle", bundle, new SourceMapInput
            {
                Version = 3,
                Sources = ["some-other-output"],
                Mappings = "AAAA,CAAC,CAAC"
            })
        };

        var validation = new SourceMapComposer(stages).Validate();

        Assert.False(validation.Complete);
        Assert.Contains(validation.Breaks, breakPoint => breakPoint.MissingSource == "intermediate");
    }

    [Fact]
    public void EmbeddedPreviousOutputFingerprintMustMatchActualStage()
    {
        var stages = new List<StageInput>
        {
            Stage("intermediate", "actual\n", new SourceMapInput
            {
                Version = 3,
                Sources = ["original.js"],
                SourcesContent = ["original\n"],
                Mappings = "AAAA"
            }),
            Stage("bundle", "actual\n", new SourceMapInput
            {
                Version = 3,
                Sources = ["intermediate"],
                SourcesContent = ["different\n"],
                Mappings = "AAAA"
            })
        };

        var validation = new SourceMapComposer(stages).Validate();

        Assert.False(validation.Valid);
        Assert.Contains(validation.Issues, issue => issue.Code == "sources-content-fingerprint-mismatch");
    }

    [Fact]
    public void BomTrailingNewlineAndUtf16OffsetsHaveFixedSemantics()
    {
        Assert.Equal("\n😸\n", TextLines.NormalizeInput("\uFEFF\r\n😸\n"));
        Assert.Equal("😸\n", TextLines.NormalizeInput("\uFEFF😸\n"));
        var text = "abc\nde";
        Assert.Equal((1, 0), TextLines.PositionAtUtf16(text, 4));
        Assert.Equal((1, 1), TextLines.PositionAtUtf16(text, 5));
        Assert.True(TextLines.EndsWithNewline("x\n"));
        Assert.False(TextLines.EndsWithNewline("x"));
        Assert.Equal(1, TextLines.ScalarColumnFromUtf16Column("😸x", 2));
        Assert.Equal(2, TextLines.Utf16ColumnFromScalarColumn("😸x", 1));
    }

    [Fact]
    public void CrossLineSegmentStartsOnTheDeclaredGeneratedLine()
    {
        var map = Stage("stage", "ab\ncd", new SourceMapInput
        {
            Version = 3,
            Sources = ["source.js"],
            SourcesContent = ["abcdef\n"],
            Mappings = $"AAAA;{VlqCodec.EncodeSegment([1, 0, 0, 3])}"
        });

        var composer = new SourceMapComposer([map]);
        var trace = composer.TraceFinalPosition(1, 1);

        Assert.True(trace.Complete);
        Assert.Equal(1, trace.Hops[0].Line);
        Assert.Equal(3, trace.Hops[^1].OriginalColumn);
    }

    [Fact]
    public void AnyInputFingerprintChangesTheDeterministicSummary()
    {
        var stages = TwoStages();
        var first = new SourceMapComposer(stages).DeterministicSummary();
        stages[1].OutputText += " ";

        var second = new SourceMapComposer(stages).DeterministicSummary();

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ExportedDocumentRoundTripsAndProducesTheSameSummary()
    {
        var stages = TwoStages();
        var composer = new SourceMapComposer(stages);
        var document = new WorkspaceDocument { Stages = stages };
        var exported = JsonSerializer.Serialize(document);
        var imported = JsonSerializer.Deserialize<WorkspaceDocument>(exported)!;

        Assert.Equal(composer.DeterministicSummary(), new SourceMapComposer(imported.Stages, imported.Revisions).DeterministicSummary());
    }

    private static List<StageInput> TwoStages()
    {
        var original = "var x=1\n";
        return
        [
            Stage("intermediate", "Avar x=1\n", new SourceMapInput
            {
                Version = 3,
                Sources = ["original.js"],
                SourcesContent = [original],
                Mappings = "CAAA,BAAI"
            }),
            Stage("bundle", "Bvar x=1\n", new SourceMapInput
            {
                Version = 3,
                Sources = ["intermediate"],
                Mappings = "CAAA,BAAI"
            })
        ];
    }

    private static StageInput Stage(string id, string output, SourceMapInput map) =>
        new() { Id = id, Label = id, OutputText = output, MapJson = JsonSerializer.Serialize(map) };
}

internal static class TraceHopExtensions
{
    public static int GeneratedLineOrCurrentLine(this TraceHop hop) => hop.Line;
}
